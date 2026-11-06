using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MagicSurvivalCoop.Net
{
    // Stop ждёт конца потоков Worker, чтобы старое подключение не жило рядом с новым.
    public sealed class CoopClient : IDisposable
    {
        public enum Status { Off, Connecting, Connected, Waiting }

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static int _liveThreads;
        public static int LiveThreads => Volatile.Read(ref _liveThreads);
        private const int JoinTimeoutMs = 3000;
        // События идут 20–30 в секунду, так что это около 10 с без связи; дальше связь считается зависшей.
        public const int MaxPendingEvents = 256;

        private readonly object _ctl = new object();     // Start и Stop по очереди
        private readonly object _lock = new object();    // Смена текущего подключения и всё, что оно публикует
        private Worker _w;
        private volatile CoopServer.LocalPeer _local;
        private string _host;
        private int _port, _portIdx;

        public readonly Inbox Incoming = new Inbox();
        public Action<string> Log = _ => { };

        public volatile Status State = Status.Off;
        public volatile string LastError;
        public volatile int PingMs = -1;
        public readonly NetStats Net = new NetStats();
        public string Host => _host;
        public int Port => _port + Volatile.Read(ref _portIdx);
        public double RetrySec = 3;
        // Загрузка боя у хоста или ретранслятор VPN могут молчать дольше 10 с.
        public double DeadSec = 20;

        // create: после обрыва клиент вернётся в комнату по коду, а если её нет, создаст заново. portSpan: хост мог занять соседний порт.
        public void Start(string host, int port, string name, string roomCode = "", bool create = false, int portSpan = 0, string clientId = "")
        {
            lock (_ctl)
            {
                Stop();
                var w = new Worker(this)
                {
                    PortSpan = Math.Max(0, portSpan), Name = Protocol.CleanName(name), ClientId = clientId ?? "",
                    RoomCode = roomCode ?? "", Create = create, Creator = create,
                };
                lock (_lock)
                {
                    _w = w;
                    _host = host; _port = port; _portIdx = 0;
                    State = Status.Connecting;
                    LastError = null;
                    PingMs = -1;
                }
                w.Begin();
            }
        }

        // Хост в той же игре: к своему серверу без сокета. Переподключаться некуда: сервер живёт, пока живёт клиент.
        public void StartLocal(CoopServer server, string name, string clientId = "")
        {
            lock (_ctl)
            {
                Stop();
                lock (_lock)
                {
                    _host = "127.0.0.1"; _port = server.Port; _portIdx = 0;
                    State = Status.Connecting;
                    LastError = null;
                    PingMs = 0;
                }
                CoopServer.LocalPeer peer = null;
                peer = server.ConnectLocal(b => OnLocal(peer, b), () => { if (_local == peer) State = Status.Off; });
                _local = peer;
                SendLocal(Encoding.UTF8.GetBytes(Protocol.Build(w =>
                {
                    w.WriteString("type", Protocol.CreateRoom); w.WriteString("name", Protocol.CleanName(name));
                    w.WriteString("clientId", clientId ?? ""); w.WriteNumber("protocol", Protocol.Version);
                })));
            }
        }

        // Зовётся из потоков сервера, иногда под его замком: только запись полей и очередь Incoming, без замков клиента.
        private void OnLocal(CoopServer.LocalPeer peer, byte[] bytes)
        {
            if (peer != null && _local != peer) return;
            Net.Received(bytes.Length);
            // Сервер массив больше не меняет, поэтому документ читает его без копии
            JsonDocument doc;
            try { doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, 0, bytes.Length - 1)); }
            catch (JsonException) { return; }
            var el = doc.RootElement;
            var type = Protocol.TypeOf(el);
            if (type == Protocol.RoomCreated || type == Protocol.RoomJoined) State = Status.Connected;
            if (!Incoming.TryEnqueue(el, bytes.Length, doc) && Interlocked.Exchange(ref _localDropLogged, 1) == 0)
                Log("клиент: игра не успевает разбирать входящие сообщения, лишние выброшены");
        }
        private int _localDropLogged;

        private byte[] _localBuf = new byte[16 * 1024];

        // Только из главного потока игры: _localBuf общий.
        private void SendLocal(ReadOnlySpan<byte> line)
        {
            var peer = _local;
            if (peer == null) return;
            if (_localBuf.Length < line.Length) _localBuf = new byte[line.Length];
            line.CopyTo(_localBuf);
            Net.Sent(line.Length + 1);
            peer.Receive(_localBuf, line.Length);
        }

        private static readonly byte[] StatePrefix = Encoding.UTF8.GetBytes("{\"type\":\"" + Protocol.PlayerState + "\",\"state\":");
        private static readonly byte[] EventPrefix = Encoding.UTF8.GetBytes("{\"type\":\"" + Protocol.Event + "\",\"event\":");

        // Строка с переводом строки в буфере из ArrayPool: так же, как WriteRawValue в конверт, но без промежуточной string.
        private static (byte[] buf, int len) Frame(byte[] prefix, string json, bool close)
        {
            int max = prefix.Length + Encoding.UTF8.GetMaxByteCount(json.Length) + 2;
            var buf = ArrayPool<byte>.Shared.Rent(max);
            prefix.CopyTo(buf, 0);
            int n = prefix.Length + Encoding.UTF8.GetBytes(json, 0, json.Length, buf, prefix.Length);
            if (close) buf[n++] = (byte)'}';
            buf[n++] = (byte)'\n';
            return (buf, n);
        }

        public void Stop()
        {
            lock (_ctl)
            {
                var local = _local;
                if (local != null)
                {
                    _local = null;
                    State = Status.Off;
                    local.Close();
                    return;
                }
                Worker w;
                lock (_lock)
                {
                    w = _w;
                    if (w == null) return;
                    _w = null;
                    State = Status.Off;
                }
                w.Abort();
                if (!w.Join(JoinTimeoutMs)) Log("клиент: старое подключение не закрылось за 3 с");
            }
        }

        public void Dispose() => Stop();

        public bool Connected => State == Status.Connected;

        public void SendState(string stateJson)
        {
            var (buf, len) = Frame(StatePrefix, stateJson, close: true);
            SendStateMessage(new ReadOnlySpan<byte>(buf, 0, len - 1));
            ArrayPool<byte>.Shared.Return(buf);
        }

        // Готовое сообщение PLAYER_STATE без перевода строки. Байты копируются, буфер вызывающего можно сразу переписать.
        public void SendStateMessage(ReadOnlySpan<byte> message)
        {
            if (_local != null) { if (Connected) SendLocal(message); return; }
            var w = _w;
            if (w == null || !Connected) return;
            w.SetState(message);
            w.Wake.Set();
        }

        // События не выбрасываются по одному: при переполнении связь рвётся, и клиент переподключается.
        public bool SendEvent(string eventJson) => SendFrame(Frame(EventPrefix, eventJson, close: true));

        public bool SendRaw(string line) => SendFrame(Frame(Array.Empty<byte>(), line, close: false));

        private bool SendFrame((byte[] buf, int len) f)
        {
            bool ok = false;
            if (_local != null)
            {
                if (Connected) { SendLocal(new ReadOnlySpan<byte>(f.buf, 0, f.len - 1)); ok = true; }
            }
            else if (_w is Worker w && Connected && w.TryQueue(f)) return true;
            ArrayPool<byte>.Shared.Return(f.buf);
            return ok;
        }

        public int PendingEvents => _w?.OutCount ?? 0;

        public void RememberRoom(string code) { var w = _w; if (w != null && !string.IsNullOrEmpty(code)) w.RoomCode = code; }

        // Подключение после Stop уже ничего не меняет.
        private bool Publish(Worker w, Action a)
        {
            lock (_lock)
            {
                if (_w != w) return false;
                a();
                return true;
            }
        }

        private sealed class Worker
        {
            private readonly CoopClient _c;
            private readonly ConcurrentQueue<(byte[] buf, int len)> _out = new ConcurrentQueue<(byte[], int)>();
            private int _outCount;
            private volatile bool _outOverflow;         // Сессия рвётся, очередь сбрасывается
            public readonly AutoResetEvent Wake = new AutoResetEvent(false);

            // Последнее состояние и запасной буфер: их меняют местами главный поток и поток записи, без выделений
            private sealed class StateBuf { public byte[] Buf = new byte[16 * 1024]; public int Len; }
            private StateBuf _latest, _spare = new StateBuf();
            public volatile string RoomCode;
            public volatile bool Create;
            public bool Creator;
            public string Name, ClientId;
            public int PortSpan;
            private volatile bool _running = true;
            private volatile string _refuse;
            private int _portIdx, _refusedInRow;
            private readonly object _tcpLock = new object();
            private TcpClient _tcp;                     // Закрывает только само подключение или Abort
            private Thread _thread;

            public Worker(CoopClient c) { _c = c; }

            private int Port => _c._port + _portIdx;

            public void Begin()
            {
                _thread = new Thread(() => Counted(Run)) { IsBackground = true, Name = "coop-client" };
                _thread.Start();
            }

            // Закрытие сокета будит и чтение, и подключение.
            public void Abort()
            {
                _running = false;
                lock (_tcpLock) { try { _tcp?.Close(); } catch { } }
                Wake.Set();
            }

            public bool Join(int ms) => _thread == null || _thread == Thread.CurrentThread || _thread.Join(ms);

            public int OutCount => Volatile.Read(ref _outCount);

            public void SetState(ReadOnlySpan<byte> message)
            {
                var s = Interlocked.Exchange(ref _spare, null) ?? new StateBuf();
                if (s.Buf.Length < message.Length + 1) s.Buf = new byte[message.Length + 1];
                message.CopyTo(s.Buf);
                s.Buf[message.Length] = (byte)'\n';
                s.Len = message.Length + 1;
                var old = Interlocked.Exchange(ref _latest, s);
                if (old != null) Interlocked.CompareExchange(ref _spare, old, null);
            }

            // При переполнении закрывает сокет: запись проснётся с ошибкой, и клиент переподключится.
            public bool TryQueue((byte[] buf, int len) line)
            {
                if (_outOverflow) return false;
                if (Interlocked.Increment(ref _outCount) > MaxPendingEvents)
                {
                    Interlocked.Decrement(ref _outCount);
                    _outOverflow = true;
                    _refuse = "связь зависла: не отправлено " + MaxPendingEvents + " событий, переподключаюсь";
                    _c.Log("клиент: " + _refuse);
                    lock (_tcpLock) { try { _tcp?.Close(); } catch { } }
                    Wake.Set();
                    return false;
                }
                _out.Enqueue(line);
                Wake.Set();
                return true;
            }

            private bool TryTake(out (byte[] buf, int len) line)
            {
                if (!_out.TryDequeue(out line)) return false;
                Interlocked.Decrement(ref _outCount);
                return true;
            }

            private void Write(NetworkStream s, byte[] buf, int len)
            {
                s.Write(buf, 0, len);
                _c.Net.Sent(len);
            }


            private static void Counted(Action body)
            {
                Interlocked.Increment(ref _liveThreads);
                try { body(); }
                finally { Interlocked.Decrement(ref _liveThreads); }
            }

            private void Run()
            {
                while (_running)
                {
                    _c.Publish(this, () => _c.State = Status.Connecting);
                    double retry = _c.RetrySec;
                    bool hadRoom = false;
                    _refuse = null;
                    try { Session(ref hadRoom); }
                    catch (Exception e)
                    {
                        if (_running)
                        {
                            var be = e is AggregateException ae ? ae.GetBaseException() : e;
                            bool refused = be is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused;
                            if ((refused || be is WrongServerException) && PortSpan > 0)
                            {
                                // Порт закрыт: пробуем соседний, а после полного круга обычная пауза
                                _refusedInRow++;
                                _portIdx = (_portIdx + 1) % (PortSpan + 1);
                                _c.Publish(this, () => Volatile.Write(ref _c._portIdx, _portIdx));
                                if (_refusedInRow <= PortSpan) retry = 0.2;
                                else _refusedInRow = 0;
                            }
                            // Причину отказа сервера, а не ошибку закрытого сокета, на которую могла наткнуться запись
                            var msg = _refuse ?? Friendly(e);
                            _c.Publish(this, () => _c.LastError = msg);
                            _c.Log($"соединение ({_c._host}:{Port}): {msg}");
                        }
                    }
                    finally { CloseTcp(); }
                    if (_outOverflow)
                    {
                        // Не ушедшее за ~10 с после переподключения устарело
                        while (TryTake(out var stale)) ArrayPool<byte>.Shared.Return(stale.buf);
                        _outOverflow = false;
                    }
                    if (!_running) break;
                    // Только об обрыве живой связи, не о каждой неудачной попытке
                    _c.Publish(this, () => { _c.State = Status.Waiting; if (hadRoom) _c.Incoming.Enqueue(Local("DISCONNECTED")); });
                    var until = Clock.Elapsed.TotalSeconds + retry;
                    while (_running && Clock.Elapsed.TotalSeconds < until) Wake.WaitOne(200);
                }
            }

            private void CloseTcp()
            {
                lock (_tcpLock) { try { _tcp?.Close(); } catch { } _tcp = null; }
            }

            private void Session(ref bool hadRoom)
            {
                var tcp = new TcpClient { NoDelay = true };
                lock (_tcpLock)
                {
                    if (!_running) { tcp.Close(); return; }
                    _tcp = tcp;
                }
                var connect = tcp.ConnectAsync(_c._host, Port);
                if (!connect.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("сервер не ответил за 8 с");
                if (connect.IsFaulted) throw connect.Exception.GetBaseException();
                if (!_running) return;
                var stream = tcp.GetStream();
                _refusedInRow = 0;
                _c.Publish(this, () => _c.LastError = null);
                double lastRx = Clock.Elapsed.TotalSeconds;
                double connectedAt = lastRx;
                bool ours = false;            // Ответил именно сервер кооператива
                bool joined = false;
                string refuse = null;         // Отказ, после которого переподключаться позже

                // Чтение в отдельном потоке, запись и пинги в этом
                var reader = new Thread(() => Counted(() =>
                {
                    try
                    {
                        var lr = new LineReader(stream);
                        while (_running)
                        {
                            if (!lr.ReadLineBytes(out var raw, out int rawLen)) break;
                            Volatile.Write(ref lastRx, Clock.Elapsed.TotalSeconds);
                            _c.Net.Received(lr.LastBytes);
                            while (rawLen > 0 && raw[rawLen - 1] == (byte)'\r') rawLen--;
                            if (rawLen == 0) continue;
                            var msg = Message.Parse(raw, rawLen);
                            if (msg == null) continue;
                            var el = msg.Root;
                            int size = rawLen;
                            bool full = false;
                            // Переполненная очередь значит, что игра давно её не разбирает: рвём связь, а не копим память
                            bool Deliver(Action before = null)
                            {
                                var owner = msg; msg = null;
                                if (!_c.Publish(this, () => { before?.Invoke(); full = !_c.Incoming.TryEnqueue(el, size, owner); })) { owner.Dispose(); return false; }
                                if (!full) return true;
                                _refuse = "игра не успевает разбирать входящие сообщения, переподключаюсь";
                                Volatile.Write(ref refuse, _refuse);
                                return false;
                            }
                            var type = Protocol.TypeOf(el);
                            if (type == Protocol.Welcome || type == Protocol.RoomJoined || type == Protocol.RoomCreated || type == Protocol.Error) Volatile.Write(ref ours, true);
                            if (type == Protocol.Pong)
                            {
                                var sent = Protocol.Num(el, "t", -1);
                                msg.Dispose();
                                if (sent >= 0)
                                {
                                    int ms = (int)Math.Max(0, Clock.Elapsed.TotalMilliseconds - sent);
                                    if (_c.Publish(this, () => _c.PingMs = ms)) _c.Net.Ping(ms);
                                }
                                continue;
                            }
                            if (type == Protocol.RoomJoined || type == Protocol.RoomCreated)
                            {
                                var code = Protocol.Str(el, "roomCode");
                                if (!string.IsNullOrEmpty(code)) RoomCode = code;   // Для возврата после обрыва, не дожидаясь игры
                                Create = false;
                                Volatile.Write(ref joined, true);
                                if (!Deliver(() => _c.State = Status.Connected)) break;
                                continue;
                            }
                            if (type == Protocol.Error && !Volatile.Read(ref joined))
                            {
                                var code = Protocol.Str(el, "code");
                                if (code == "room_not_found" && RoomCode.Length > 0)
                                {
                                    // Старой комнаты нет: хост создаёт её заново, гость входит в ту, что есть
                                    RoomCode = "";
                                    Create = Creator;
                                    msg.Dispose();
                                    TryQueue(Frame(Array.Empty<byte>(), EnterMessage(), close: false));
                                    continue;
                                }
                                if (code == "room_not_found" || code == "server_busy" || code == "rate_limited")
                                {
                                    // Хост ещё не создал игру или сервер перегружен: переподключимся через паузу
                                    Volatile.Write(ref refuse, Protocol.Str(el, "message", code));
                                    _refuse = refuse;
                                    Deliver();
                                    break;
                                }
                            }
                            if (!Deliver()) break;
                        }
                    }
                    catch { }
                    try { tcp.Close(); } catch { }
                })) { IsBackground = true, Name = "coop-client-read" };
                reader.Start();

                try
                {
                    Write(stream, EnterMessage());

                    double nextPing = 0;
                    while (_running && reader.IsAlive)
                    {
                        double now = Clock.Elapsed.TotalSeconds;
                        if (now - Volatile.Read(ref lastRx) > _c.DeadSec) throw new TimeoutException($"нет ответа {_c.DeadSec:0} с");
                        if (!Volatile.Read(ref ours) && now - connectedAt > 4)
                            throw new WrongServerException($"на порту {Port} отвечает не сервер кооператива (другая программа)");
                        if (now >= nextPing)
                        {
                            nextPing = now + 2;
                            double t = Clock.Elapsed.TotalMilliseconds;
                            Write(stream, Protocol.Build(w => { w.WriteString("type", Protocol.Ping); w.WriteNumber("t", Math.Round(t)); }));
                        }
                        while (_running && !_outOverflow && TryTake(out var line))
                        {
                            try { Write(stream, line.buf, line.len); }
                            finally { ArrayPool<byte>.Shared.Return(line.buf); }
                        }
                        var st = Interlocked.Exchange(ref _latest, null);
                        if (st != null)
                        {
                            try { if (_running) Write(stream, st.Buf, st.Len); }
                            finally { Interlocked.CompareExchange(ref _spare, st, null); }
                        }
                        Wake.WaitOne(100);
                    }
                    var why = Volatile.Read(ref refuse);
                    if (_running && why != null) throw new System.IO.IOException(why);
                    if (_running && !reader.IsAlive) throw new System.IO.IOException("сервер закрыл соединение");
                }
                finally
                {
                    hadRoom = Volatile.Read(ref joined);
                    try { tcp.Close(); } catch { }
                    reader.Join(JoinTimeoutMs);   // Следующая попытка начнётся без старого чтения
                }
            }

            private void Write(NetworkStream s, string line)
            {
                var b = Encoding.UTF8.GetBytes(line + "\n");
                Write(s, b, b.Length);
            }

            private string EnterMessage() => Create
                ? Protocol.Build(w => { w.WriteString("type", Protocol.CreateRoom); w.WriteString("name", Name); w.WriteString("clientId", ClientId); w.WriteNumber("protocol", Protocol.Version); })
                : Protocol.Build(w => { w.WriteString("type", Protocol.JoinRoom); w.WriteString("roomCode", RoomCode); w.WriteString("name", Name); w.WriteString("clientId", ClientId); w.WriteNumber("protocol", Protocol.Version); });
        }

        private sealed class WrongServerException : Exception { public WrongServerException(string m) : base(m) { } }

        // Документ входящего сообщения на арендованном буфере: Inbox освобождает его, когда игра взяла следующее.
        private sealed class Message : IDisposable
        {
            private JsonDocument _doc;
            private byte[] _buf;
            public JsonElement Root => _doc.RootElement;

            public static Message Parse(byte[] raw, int len)
            {
                var buf = ArrayPool<byte>.Shared.Rent(len);
                Buffer.BlockCopy(raw, 0, buf, 0, len);
                try { return new Message { _doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(buf, 0, len)), _buf = buf }; }
                catch (JsonException) { ArrayPool<byte>.Shared.Return(buf); return null; }
            }

            public void Dispose()
            {
                _doc?.Dispose(); _doc = null;
                if (_buf != null) { ArrayPool<byte>.Shared.Return(_buf); _buf = null; }
            }
        }

        private static JsonElement Local(string type)
        {
            using var doc = JsonDocument.Parse(Protocol.Simple(type));
            return doc.RootElement.Clone();
        }

        private static string Friendly(Exception e)
        {
            if (e is AggregateException ae) e = ae.GetBaseException();
            if (e is SocketException se)
                switch (se.SocketErrorCode)
                {
                    case SocketError.ConnectionRefused: return "хост не принимает подключения (игра у хоста не запущена или он не нажал «Создать игру»)";
                    case SocketError.TimedOut: return "время ожидания истекло (проверьте Anywherelan и IP)";
                    case SocketError.HostUnreachable:
                    case SocketError.NetworkUnreachable: return "адрес недоступен (VPN выключен или IP неверный)";
                }
            return e.Message;
        }
    }
}
