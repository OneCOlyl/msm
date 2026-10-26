using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MagicSurvivalCoop.Net
{
    /// <summary>
    /// Клиент протокола. Вся сеть — в фоновых потоках; игра забирает сообщения из <see cref="Incoming"/> на главном потоке.
    /// Сам переподключается при обрыве (мобильный интернет, смена Wi-Fi) и снова входит в комнату.
    /// Состояние игрока не копится в очереди: отправляется только самое свежее. События копятся, но не больше
    /// <see cref="MaxPendingEvents"/>: дальше связь считается зависшей и клиент переподключается (см. <see cref="SendEvent"/>).
    /// Входящие — в <see cref="Inbox"/>: по порядку, состояний от друга не больше предела, остальное ограничено.
    /// Каждый <see cref="Start"/> — отдельное подключение (<see cref="Worker"/>) со своим сокетом, очередями и потоками;
    /// <see cref="Stop"/> закрывает его и ждёт, пока оба его потока закончатся, поэтому старое подключение не живёт рядом с новым.
    /// </summary>
    public sealed class CoopClient : IDisposable
    {
        public enum Status { Off, Connecting, Connected, Waiting }

        private static readonly Stopwatch Clock = Stopwatch.StartNew();
        private static int _liveThreads;
        /// <summary>Сколько потоков клиентов (рабочих и читающих) живо во всём процессе — для тестов.</summary>
        public static int LiveThreads => Volatile.Read(ref _liveThreads);
        private const int JoinTimeoutMs = 3000;
        /// <summary>
        /// Сколько событий (и прочих сообщений, кроме состояния) может ждать отправки. Очередь растёт, только когда запись
        /// в сокет стоит (сеть зависла или сервер не читает): события и так пачки за шаг отправки, ~20–30 в секунду, так что
        /// это ~10 с без связи. Каждое ≤ <see cref="Protocol.MaxLineBytes"/>, всего ≤ 4 МБ.
        /// </summary>
        public const int MaxPendingEvents = 256;

        private readonly object _ctl = new object();     // Start/Stop — по очереди
        private readonly object _lock = new object();    // смена текущего подключения и всё, что оно публикует
        private Worker _w;
        private string _host;
        private int _port, _portIdx;

        /// <summary>Сообщения сервера (разобранные JSON), читать на главном потоке.</summary>
        public readonly Inbox Incoming = new Inbox();
        public Action<string> Log = _ => { };

        public volatile Status State = Status.Off;
        public volatile string LastError;
        public volatile int PingMs = -1;
        public string Host => _host;
        /// <summary>Порт, к которому клиент подключается сейчас (с учётом перебора соседних портов).</summary>
        public int Port => _port + Volatile.Read(ref _portIdx);
        /// <summary>Сколько секунд ждать перед повторным подключением.</summary>
        public double RetrySec = 3;
        /// <summary>Нет данных столько секунд — соединение считается оборванным.</summary>
        public double DeadSec = 20;   // загрузка боя у хоста или ретранслятор VPN могут молчать дольше 10 с

        /// <summary>
        /// Подключиться и войти в комнату. roomCode пустой — «любая комната на сервере» (режим «по IP»). create — создать новую
        /// комнату (хост): после обрыва клиент возвращается в неё по коду, а если её уже нет — создаёт заново, а не входит в чужую.
        /// portSpan > 0 — если порт закрыт, пробовать следующие (port+1 … port+portSpan): хост мог занять соседний, когда основной был занят.
        /// </summary>
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

        public void Stop()
        {
            lock (_ctl)
            {
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

        /// <summary>Отправить состояние игрока (JSON-объект). Старое неотправленное состояние заменяется новым.</summary>
        public void SendState(string stateJson)
        {
            var w = _w;
            if (w == null || !Connected) return;
            w.LatestState = Protocol.Build(b => { b.WriteString("type", Protocol.PlayerState); b.WritePropertyName("state"); b.WriteRawValue(stateJson, true); });
            w.Wake.Set();
        }

        /// <summary>
        /// Событие для всех (to = null) или одного игрока. События не схлопываются и не выбрасываются по одному: если
        /// в очереди уже <see cref="MaxPendingEvents"/>, связь рвётся и клиент переподключается (друзья получат PLAYER_LEFT,
        /// игра — DISCONNECTED), а очередь сбрасывается. false — событие не принято.
        /// </summary>
        public bool SendEvent(string eventJson)
        {
            var w = _w;
            if (w == null || !Connected) return false;
            return w.TryQueue(Protocol.Build(b => { b.WriteString("type", Protocol.Event); b.WritePropertyName("event"); b.WriteRawValue(eventJson, true); }));
        }

        public bool SendRaw(string line) { var w = _w; return w != null && Connected && w.TryQueue(line); }

        /// <summary>Сколько сообщений ждёт отправки — для тестов.</summary>
        public int PendingEvents => _w?.OutCount ?? 0;

        /// <summary>Запомнить код комнаты, в которую мы вошли, чтобы вернуться в неё после обрыва.</summary>
        public void RememberRoom(string code) { var w = _w; if (w != null && !string.IsNullOrEmpty(code)) w.RoomCode = code; }

        /// <summary>Действие от имени подключения w — только если оно ещё текущее (старое после Stop ничего не меняет).</summary>
        private bool Publish(Worker w, Action a)
        {
            lock (_lock)
            {
                if (_w != w) return false;
                a();
                return true;
            }
        }

        // ------------------------------------------------------------ одно подключение (от Start до Stop)

        private sealed class Worker
        {
            private readonly CoopClient _c;
            private readonly ConcurrentQueue<string> _out = new ConcurrentQueue<string>();
            private int _outCount;
            private volatile bool _outOverflow;         // очередь переполнилась: эта сессия рвётся, очередь сбрасывается
            public readonly AutoResetEvent Wake = new AutoResetEvent(false);
            public volatile string LatestState;
            public volatile string RoomCode;
            public volatile bool Create;
            public bool Creator;
            public string Name, ClientId;
            public int PortSpan;
            private volatile bool _running = true;
            private volatile string _refuse;           // причина отказа сервера в этой попытке
            private int _portIdx, _refusedInRow;
            private readonly object _tcpLock = new object();
            private TcpClient _tcp;                     // сокет только этого подключения; закрывает его только оно само или Abort
            private Thread _thread;

            public Worker(CoopClient c) { _c = c; }

            private int Port => _c._port + _portIdx;

            public void Begin()
            {
                _thread = new Thread(() => Counted(Run)) { IsBackground = true, Name = "coop-client" };
                _thread.Start();
            }

            /// <summary>Остановить: дальше ни одной попытки, сокет закрыт (это будит и чтение, и подключение).</summary>
            public void Abort()
            {
                _running = false;
                lock (_tcpLock) { try { _tcp?.Close(); } catch { } }
                Wake.Set();
            }

            public bool Join(int ms) => _thread == null || _thread == Thread.CurrentThread || _thread.Join(ms);

            public int OutCount => Volatile.Read(ref _outCount);

            /// <summary>В очередь отправки; переполнение — связь зависла: закрыть сокет (запись проснётся с ошибкой) и переподключиться.</summary>
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
                                // порт закрыт — пробуем соседний; полный круг без ответа — обычная пауза
                                _refusedInRow++;
                                _portIdx = (_portIdx + 1) % (PortSpan + 1);
                                _c.Publish(this, () => Volatile.Write(ref _c._portIdx, _portIdx));
                                if (_refusedInRow <= PortSpan) retry = 0.2;
                                else _refusedInRow = 0;
                            }
                            // сервер отказал с причиной (занят, частые входы…) — показать её, а не ошибку закрытого сокета,
                            // на которую в это время могла наткнуться запись
                            var msg = _refuse ?? Friendly(e);
                            _c.Publish(this, () => _c.LastError = msg);
                            _c.Log($"соединение ({_c._host}:{Port}): {msg}");
                        }
                    }
                    finally { CloseTcp(); }
                    if (_outOverflow)
                    {
                        // то, что не ушло за ~10 с, после переподключения устарело: друзья нас потеряли и встретят заново
                        while (TryTake(out _)) { }
                        _outOverflow = false;
                    }
                    if (!_running) break;
                    // сообщаем только об обрыве живой связи, не о каждой неудачной попытке
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
                bool ours = false;            // ответил именно сервер кооператива (WELCOME / вход в комнату / его ошибка)
                bool joined = false;
                string refuse = null;         // сервер отказал так, что надо переподключиться позже (комнаты ещё нет, занят)

                // чтение — отдельный поток, запись и пинги — этот
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
                            if (line.Length == 0) continue;
                            JsonElement el;
                            try { using var doc = JsonDocument.Parse(line); el = doc.RootElement.Clone(); }
                            catch (JsonException) { continue; }
                            int size = line.Length;
                            bool full = false;
                            // в очередь игры; переполнена — игра давно её не разбирает: рвём связь, а не копим память
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
                                if (sent >= 0) _c.Publish(this, () => _c.PingMs = (int)Math.Max(0, Clock.Elapsed.TotalMilliseconds - sent));
                                continue;
                            }
                            if (type == Protocol.RoomJoined || type == Protocol.RoomCreated)
                            {
                                var code = Protocol.Str(el, "roomCode");
                                if (!string.IsNullOrEmpty(code)) RoomCode = code;   // вернуться в неё после обрыва — сразу, не дожидаясь игры
                                Create = false;                                    // комната есть — дальше только входим в неё
                                Volatile.Write(ref joined, true);
                                if (!Deliver(() => _c.State = Status.Connected)) break;
                                continue;
                            }
                            if (type == Protocol.Error && !Volatile.Read(ref joined))
                            {
                                var code = Protocol.Str(el, "code");
                                if (code == "room_not_found" && RoomCode.Length > 0)
                                {
                                    // старой комнаты нет (хост перезапустил игру или комната опустела): хост создаёт её заново, гость входит в ту, что есть
                                    RoomCode = "";
                                    Create = Creator;
                                    TryQueue(EnterMessage());
                                    continue;
                                }
                                if (code == "room_not_found" || code == "server_busy" || code == "rate_limited")
                                {
                                    // хост ещё не создал игру / сервер перегружен — переподключимся через паузу
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
                    reader.Join(JoinTimeoutMs);   // чтение этого сокета кончается вместе с ним — следующая попытка начнётся без него
                }
            }

            /// <summary>Вход: создать комнату (хост, пока её нет) или войти в комнату по коду (пустой — в любую).</summary>
            private string EnterMessage() => Create
                ? Protocol.Build(w => { w.WriteString("type", Protocol.CreateRoom); w.WriteString("name", Name); w.WriteString("clientId", ClientId); w.WriteNumber("protocol", Protocol.Version); })
                : Protocol.Build(w => { w.WriteString("type", Protocol.JoinRoom); w.WriteString("roomCode", RoomCode); w.WriteString("name", Name); w.WriteString("clientId", ClientId); w.WriteNumber("protocol", Protocol.Version); });
        }

        private sealed class WrongServerException : Exception { public WrongServerException(string m) : base(m) { } }

        private static void Write(NetworkStream s, string line)
        {
            var b = Encoding.UTF8.GetBytes(line + "\n");
            s.Write(b, 0, b.Length);
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
