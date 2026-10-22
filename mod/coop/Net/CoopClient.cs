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
    /// Состояние игрока не копится в очереди: отправляется только самое свежее.
    /// </summary>
    public sealed class CoopClient : IDisposable
    {
        public enum Status { Off, Connecting, Connected, Waiting }

        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly ConcurrentQueue<string> _out = new ConcurrentQueue<string>();
        private readonly AutoResetEvent _wake = new AutoResetEvent(false);
        private volatile string _latestState;
        private volatile bool _running;
        private Thread _thread;
        private TcpClient _tcp;
        private string _host, _roomCode, _name, _clientId = "";
        private int _port, _portSpan, _portIdx, _refusedInRow;
        private bool _create;

        /// <summary>Сообщения сервера (разобранные JSON), читать на главном потоке.</summary>
        public readonly ConcurrentQueue<JsonElement> Incoming = new ConcurrentQueue<JsonElement>();
        public Action<string> Log = _ => { };

        public volatile Status State = Status.Off;
        public volatile string LastError;
        public volatile int PingMs = -1;
        public string Host => _host;
        /// <summary>Порт, к которому клиент подключается сейчас (с учётом перебора соседних портов).</summary>
        public int Port => _port + _portIdx;
        /// <summary>Сколько секунд ждать перед повторным подключением.</summary>
        public double RetrySec = 3;
        /// <summary>Нет данных столько секунд — соединение считается оборванным.</summary>
        public double DeadSec = 20;   // загрузка боя у хоста или ретранслятор VPN могут молчать дольше 10 с

        /// <summary>
        /// Подключиться и войти в комнату. roomCode пустой — «любая комната на сервере» (режим «по IP»). create — создать новую комнату.
        /// portSpan > 0 — если порт закрыт, пробовать следующие (port+1 … port+portSpan): хост мог занять соседний, когда основной был занят.
        /// </summary>
        public void Start(string host, int port, string name, string roomCode = "", bool create = false, int portSpan = 0, string clientId = "")
        {
            _clientId = clientId ?? "";
            Stop();
            _host = host; _port = port; _portSpan = Math.Max(0, portSpan); _portIdx = 0; _refusedInRow = 0;
            _name = Protocol.CleanName(name); _roomCode = roomCode ?? ""; _create = create;
            _running = true;
            State = Status.Connecting;
            LastError = null;
            _thread = new Thread(Run) { IsBackground = true, Name = "coop-client" };
            _thread.Start();
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _tcp?.Close(); } catch { }
            _wake.Set();
            State = Status.Off;
            while (_out.TryDequeue(out _)) { }
            _latestState = null;
        }

        public void Dispose() => Stop();

        public bool Connected => State == Status.Connected;

        /// <summary>Отправить состояние игрока (JSON-объект). Старое неотправленное состояние заменяется новым.</summary>
        public void SendState(string stateJson)
        {
            if (!Connected) return;
            _latestState = Protocol.Build(w => { w.WriteString("type", Protocol.PlayerState); w.WritePropertyName("state"); w.WriteRawValue(stateJson, true); });
            _wake.Set();
        }

        /// <summary>Событие для всех (to = null) или одного игрока.</summary>
        public void SendEvent(string eventJson)
        {
            if (!Connected) return;
            _out.Enqueue(Protocol.Build(w => { w.WriteString("type", Protocol.Event); w.WritePropertyName("event"); w.WriteRawValue(eventJson, true); }));
            _wake.Set();
        }

        public void SendRaw(string line) { if (Connected) { _out.Enqueue(line); _wake.Set(); } }

        // ------------------------------------------------------------ фон

        private void Run()
        {
            while (_running)
            {
                State = Status.Connecting;
                double retry = RetrySec;
                try { Session(); }
                catch (Exception e)
                {
                    if (_running)
                    {
                        var be = e is AggregateException ae ? ae.GetBaseException() : e;
                        bool refused = be is SocketException se && se.SocketErrorCode == SocketError.ConnectionRefused;
                        if ((refused || be is WrongServerException) && _portSpan > 0)
                        {
                            // порт закрыт — пробуем соседний; полный круг без ответа — обычная пауза
                            _refusedInRow++;
                            _portIdx = (_portIdx + 1) % (_portSpan + 1);
                            if (_refusedInRow <= _portSpan) retry = 0.2;
                            else _refusedInRow = 0;
                        }
                        LastError = Friendly(e);
                        Log($"соединение ({_host}:{Port}): {LastError}");
                    }
                }
                finally { try { _tcp?.Close(); } catch { } _tcp = null; }
                if (!_running) break;
                bool hadRoom = State == Status.Connected;
                State = Status.Waiting;
                if (hadRoom) Incoming.Enqueue(Local("DISCONNECTED"));   // сообщаем только об обрыве живой связи, не о каждой неудачной попытке
                var until = Clock.Elapsed.TotalSeconds + retry;
                while (_running && Clock.Elapsed.TotalSeconds < until) _wake.WaitOne(200);
            }
            State = Status.Off;
        }

        private void Session()
        {
            var tcp = new TcpClient { NoDelay = true };
            _tcp = tcp;
            var connect = tcp.ConnectAsync(_host, Port);
            if (!connect.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("сервер не ответил за 8 с");
            if (connect.IsFaulted) throw connect.Exception.GetBaseException();
            var stream = tcp.GetStream();
            _refusedInRow = 0;
            LastError = null;
            double lastRx = Clock.Elapsed.TotalSeconds;
            double connectedAt = lastRx;
            bool ours = false;            // ответил именно сервер кооператива (WELCOME / вход в комнату)

            // чтение — отдельный поток, запись и пинги — этот
            var reader = new Thread(() =>
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
                        var type = Protocol.TypeOf(el);
                        if (type == Protocol.Welcome || type == Protocol.RoomJoined || type == Protocol.RoomCreated) ours = true;
                        if (type == Protocol.Pong)
                        {
                            var sent = Protocol.Num(el, "t", -1);
                            if (sent >= 0) PingMs = (int)Math.Max(0, Clock.Elapsed.TotalMilliseconds - sent);
                            continue;
                        }
                        if (type == Protocol.RoomJoined || type == Protocol.RoomCreated) State = Status.Connected;
                        // хост перезапустил игру — старой комнаты нет: входим в ту, что есть на сервере сейчас
                        if (type == Protocol.Error && Protocol.Str(el, "code") == "room_not_found" && _roomCode.Length > 0 && State != Status.Connected)
                        {
                            _roomCode = "";
                            _out.Enqueue(Protocol.Build(w => { w.WriteString("type", Protocol.JoinRoom); w.WriteString("roomCode", ""); w.WriteString("name", _name); w.WriteString("clientId", _clientId); w.WriteNumber("protocol", Protocol.Version); }));
                            _wake.Set();
                            continue;
                        }
                        Incoming.Enqueue(el);
                    }
                }
                catch { }
                try { tcp.Close(); } catch { }
            }) { IsBackground = true, Name = "coop-client-read" };
            reader.Start();

            // вход в комнату
            Write(stream, _create
                ? Protocol.Build(w => { w.WriteString("type", Protocol.CreateRoom); w.WriteString("name", _name); w.WriteString("clientId", _clientId); w.WriteNumber("protocol", Protocol.Version); })
                : Protocol.Build(w => { w.WriteString("type", Protocol.JoinRoom); w.WriteString("roomCode", _roomCode); w.WriteString("name", _name); w.WriteString("clientId", _clientId); w.WriteNumber("protocol", Protocol.Version); }));
            _create = false;   // после переподключения входим в ту же комнату, а не создаём новую

            double nextPing = 0;
            while (_running && reader.IsAlive)
            {
                double now = Clock.Elapsed.TotalSeconds;
                if (now - Volatile.Read(ref lastRx) > DeadSec) throw new TimeoutException($"нет ответа {DeadSec:0} с");
                if (!Volatile.Read(ref ours) && now - connectedAt > 4)
                    throw new WrongServerException($"на порту {Port} отвечает не сервер кооператива (другая программа)");
                if (now >= nextPing)
                {
                    nextPing = now + 2;
                    double t = Clock.Elapsed.TotalMilliseconds;
                    Write(stream, Protocol.Build(w => { w.WriteString("type", Protocol.Ping); w.WriteNumber("t", Math.Round(t)); }));
                }
                while (_out.TryDequeue(out var line)) Write(stream, line);
                var st = Interlocked.Exchange(ref _latestState, null);
                if (st != null) Write(stream, st);
                _wake.WaitOne(100);
            }
            if (_running && !reader.IsAlive) throw new System.IO.IOException("сервер закрыл соединение");
        }

        /// <summary>Запомнить код комнаты, в которую мы вошли, чтобы вернуться в неё после обрыва.</summary>
        public void RememberRoom(string code) { if (!string.IsNullOrEmpty(code)) _roomCode = code; }

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
