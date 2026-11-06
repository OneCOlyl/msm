using System;
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
                SendLocal(Protocol.Build(w =>
                {
                    w.WriteString("type", Protocol.CreateRoom); w.WriteString("name", Protocol.CleanName(name));
                    w.WriteString("clientId", clientId ?? ""); w.WriteNumber("protocol", Protocol.Version);
                }));
            }
        }

        // Зовётся из потоков сервера, иногда под его замком: только запись полей и очередь Incoming, без замков клиента.
        private void OnLocal(CoopServer.LocalPeer peer, byte[] bytes)
        {
            if (peer != null && _local != peer) return;
            Net.Received(bytes.Length);
            JsonElement el;
            try { using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(bytes, 0, bytes.Length - 1)); el = doc.RootElement.Clone(); }
            catch (JsonException) { return; }
            var type = Protocol.TypeOf(el);
            if (type == Protocol.RoomCreated || type == Protocol.RoomJoined) State = Status.Connected;
            if (!Incoming.TryEnqueue(el, bytes.Length) && Interlocked.Exchange(ref _localDropLogged, 1) == 0)
                Log("клиент: игра не успевает разбирать входящие сообщения, лишние выброшены");
        }
        private int _localDropLogged;

        private void SendLocal(string line)
        {
            var peer = _local;
            if (peer == null) return;
            var b = Encoding.UTF8.GetBytes(line);
            Net.Sent(b.Length + 1);
            peer.Receive(b);
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
            if (_local != null)
            {
                if (Connected) SendLocal(Protocol.Build(b => { b.WriteString("type", Protocol.PlayerState); b.WritePropertyName("state"); b.WriteRawValue(stateJson, true); }));
                return;
            }
            var w = _w;
            if (w == null || !Connected) return;
            w.LatestState = Protocol.Build(b => { b.WriteString("type", Protocol.PlayerState); b.WritePropertyName("state"); b.WriteRawValue(stateJson, true); });
            w.Wake.Set();
        }

        // События не выбрасываются по одному: при переполнении связь рвётся, и клиент переподключается.
        public bool SendEvent(string eventJson)
        {
            if (_local != null)
            {
                if (!Connected) return false;
                SendLocal(Protocol.Build(b => { b.WriteString("type", Protocol.Event); b.WritePropertyName("event"); b.WriteRawValue(eventJson, true); }));
                return true;
            }
            var w = _w;
            if (w == null || !Connected) return false;
            return w.TryQueue(Protocol.Build(b => { b.WriteString("type", Protocol.Event); b.WritePropertyName("event"); b.WriteRawValue(eventJson, true); }));
        }

        public bool SendRaw(string line)
        {
            if (_local != null) { if (!Connected) return false; SendLocal(line); return true; }
            var w = _w; return w != null && Connected && w.TryQueue(line);
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
            private readonly ConcurrentQueue<string> _out = new ConcurrentQueue<string>();
            private int _outCount;
            private volatile bool _outOverflow;         // Сессия рвётся, очередь сбрасывается
            public readonly AutoResetEvent Wake = new AutoResetEvent(false);
            public volatile string LatestState;
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

            // При переполнении закрывает сокет: запись проснётся с ошибкой, и клиент переподключится.
            public bool TryQueue(string line)
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

            private bool TryTake(out string line)
            {
                if (!_out.TryDequeue(out line)) return false;
                Interlocked.Decrement(ref _outCount);
                return true;
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
                        while (TryTake(out _)) { }
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
                            var line = lr.ReadLine();
                            if (line == null) break;
                            Volatile.Write(ref lastRx, Clock.Elapsed.TotalSeconds);
                            _c.Net.Received(lr.LastBytes);
                            if (line.Length == 0) continue;
                            JsonElement el;
                            try { using var doc = JsonDocument.Parse(line); el = doc.RootElement.Clone(); }
                            catch (JsonException) { continue; }
                            int size = line.Length;
                            bool full = false;
                            // Переполненная очередь значит, что игра давно её не разбирает: рвём связь, а не копим память
                            bool Deliver(Action before = null)
                            {
                                if (!_c.Publish(this, () => { before?.Invoke(); full = !_c.Incoming.TryEnqueue(el, size); })) return false;
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
                                    TryQueue(EnterMessage());
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
                        while (_running && !_outOverflow && TryTake(out var line)) Write(stream, line);
                        var st = Interlocked.Exchange(ref LatestState, null);
                        if (st != null && _running) Write(stream, st);
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
                s.Write(b, 0, b.Length);
                _c.Net.Sent(b.Length);
            }

            private string EnterMessage() => Create
                ? Protocol.Build(w => { w.WriteString("type", Protocol.CreateRoom); w.WriteString("name", Name); w.WriteString("clientId", ClientId); w.WriteNumber("protocol", Protocol.Version); })
                : Protocol.Build(w => { w.WriteString("type", Protocol.JoinRoom); w.WriteString("roomCode", RoomCode); w.WriteString("name", Name); w.WriteString("clientId", ClientId); w.WriteNumber("protocol", Protocol.Version); });
        }

        private sealed class WrongServerException : Exception { public WrongServerException(string m) : base(m) { } }

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
