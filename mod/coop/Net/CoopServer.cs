using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MagicSurvivalCoop.Net
{
    /// <summary>
    /// Сервер комнат, запускается в игре у хоста. Комнаты по коду, до 4 игроков. JOIN_ROOM без кода заходит в единственную
    /// комнату: так работает подключение по IP. Шифрования и пароля нет, сервер только для виртуальной сети.
    /// </summary>
    public sealed class CoopServer : IDisposable
    {
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        private const double IdleTimeoutSec = 40;

        private readonly object _lock = new object();
        private readonly Dictionary<string, Room> _rooms = new Dictionary<string, Room>(StringComparer.OrdinalIgnoreCase);
        private readonly List<Conn> _conns = new List<Conn>();
        private TcpListener _listener;
        private Thread _acceptThread, _reaperThread;
        private volatile bool _running;
        private int _nextId;

        private readonly Dictionary<string, RoomOps> _roomOps = new Dictionary<string, RoomOps>();

        public Action<string> Log = _ => { };
        /// <summary>Лишние получают server_busy.</summary>
        public int MaxConnections = 32;
        public int MaxConnectionsPerIp = 8;
        /// <summary>CREATE_ROOM и JOIN_ROOM с одного адреса за окно RoomOpsWindowSec. Сверх предела rate_limited.</summary>
        public int RoomOpsPerMinute = 60;
        /// <summary>Адрес без входов дольше окна забывается.</summary>
        public double RoomOpsWindowSec = 60;
        /// <summary>При переполнении забывается адрес, дольше всех не обращавшийся.</summary>
        public int MaxTrackedIps = 4096;
        public int MaxRooms = 16;
        /// <summary>Соединение без комнаты дольше этого закрывается.</summary>
        public double JoinTimeoutSec = 30;
        /// <summary>
        /// JOIN_ROOM без кода при пустом сервере создаёт комнату. В игре хоста false: комнату создаёт только хост, и гость,
        /// успевший раньше, ждёт её, а не занимает слот 1.
        /// </summary>
        public bool AutoCreateRoom = true;
        /// <summary>Для теста гонки: зовётся между снимком под замком и сборкой сообщения.</summary>
        internal Action RelayHook;
        public int Port { get; private set; }
        public bool Running => _running;
        public int ConnectionCount { get { lock (_lock) return _conns.Count; } }

        public void Start(int port, IPAddress bind = null)
        {
            if (_running) return;
            _listener = new TcpListener(bind ?? IPAddress.Any, port);
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "coop-accept" };
            _acceptThread.Start();
            _reaperThread = new Thread(ReapLoop) { IsBackground = true, Name = "coop-reaper" };
            _reaperThread.Start();
            Log($"сервер слушает порт {Port}");
        }

        public void Stop()
        {
            if (!_running) return;
            _running = false;
            try { _listener.Stop(); } catch { }
            Conn[] all;
            lock (_lock) { all = _conns.ToArray(); }
            foreach (var c in all) c.Close();
            Log("сервер остановлен");
        }

        public void Dispose() => Stop();

        private sealed class Room
        {
            public string Code;
            public readonly List<Conn> Players = new List<Conn>();
        }

        private sealed class Conn
        {
            public string Id;
            public string Name = "Игрок";
            public string ClientId = "";
            public int Slot;
            public Room Room;
            public TcpClient Tcp;
            public NetworkStream Stream;
            public string Ip = "";
            public readonly BlockingCollection<byte[]> Out = new BlockingCollection<byte[]>(512);
            public long LastSeenTicks = DateTime.UtcNow.Ticks;
            public long LastInRoomTicks = DateTime.UtcNow.Ticks;
            public volatile bool Closed;
            private volatile bool _ending;

            public void Send(string line) => Send(Bytes(line));

            /// <summary>Массив может уйти нескольким получателям, его не меняют.</summary>
            public void Send(byte[] bytes)
            {
                if (Closed || _ending) return;
                bool ok;
                try { ok = Out.TryAdd(bytes); } catch (InvalidOperationException) { return; }
                if (!ok) Close();      // Клиент не успевает читать: отключаем, а не копим память
            }

            /// <summary>Закрывает соединение, когда это последнее сообщение уйдёт.</summary>
            public void SendAndClose(string line)
            {
                Send(line);
                _ending = true;
                try { Out.CompleteAdding(); } catch { }
            }

            public void Close()
            {
                if (Closed) return;
                Closed = true;
                try { Out.CompleteAdding(); } catch { }
                try { Tcp.Close(); } catch { }
            }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient tcp;
                try { tcp = _listener.AcceptTcpClient(); }
                catch { if (_running) Thread.Sleep(200); continue; }
                try
                {
                    tcp.NoDelay = true;
                    var ip = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
                    var c = new Conn { Id = "p" + Interlocked.Increment(ref _nextId), Tcp = tcp, Stream = tcp.GetStream(), Ip = ip };
                    string busy = null;
                    lock (_lock)
                    {
                        if (_conns.Count >= MaxConnections) busy = "Сервер переполнен, попробуйте позже.";
                        else if (_conns.Count(x => x.Ip == ip) >= MaxConnectionsPerIp) busy = "Слишком много подключений с вашего адреса.";
                        else _conns.Add(c);
                    }
                    if (busy != null) { Log($"отказ {ip}: {busy}"); Reject(tcp, "server_busy", busy); continue; }
                    new Thread(() => WriteLoop(c)) { IsBackground = true, Name = "coop-w-" + c.Id }.Start();
                    new Thread(() => ReadLoop(c)) { IsBackground = true, Name = "coop-r-" + c.Id }.Start();
                    Log($"подключение {c.Id} с {tcp.Client.RemoteEndPoint}");
                    c.Send(Protocol.Build(w => { w.WriteString("type", Protocol.Welcome); w.WriteNumber("protocol", Protocol.Version); w.WriteString("playerId", c.Id); }));
                }
                catch (Exception e) { Log("ошибка приёма: " + e.Message); try { tcp.Close(); } catch { } }
            }
        }

        /// <summary>Дочитывает присланное клиентом: иначе сброс соединения съест ошибку.</summary>
        private static void Reject(TcpClient tcp, string code, string message)
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var s = tcp.GetStream();
                    var b = Bytes(ErrorLine(code, message));
                    using var cts = new CancellationTokenSource(1500);
                    await s.WriteAsync(b, 0, b.Length, cts.Token);
                    tcp.Client.Shutdown(SocketShutdown.Send);
                    var buf = new byte[1024];
                    while (await s.ReadAsync(buf, 0, buf.Length, cts.Token) > 0) { }
                }
                catch { }
                finally { try { tcp.Close(); } catch { } }
            });
        }

        private void WriteLoop(Conn c)
        {
            try
            {
                foreach (var bytes in c.Out.GetConsumingEnumerable()) c.Stream.Write(bytes, 0, bytes.Length);
            }
            catch { }
            finally { c.Close(); }
        }

        private void ReadLoop(Conn c)
        {
            try
            {
                var reader = new LineReader(c.Stream);
                while (_running && !c.Closed)
                {
                    var line = reader.ReadLine();
                    if (line == null) break;
                    Interlocked.Exchange(ref c.LastSeenTicks, DateTime.UtcNow.Ticks);
                    if (line.Length == 0) continue;
                    JsonDocument doc;
                    try { doc = JsonDocument.Parse(line); }
                    catch (JsonException) { SendError(c, "invalid_json", "Некорректный JSON."); continue; }
                    using (doc) Handle(c, doc.RootElement);
                }
            }
            catch (IOException e) when (e.Message.Contains("строка")) { SendError(c, "message_too_large", "Сообщение слишком большое."); }
            catch { }
            finally
            {
                lock (_lock) { LeaveRoomLocked(c); _conns.Remove(c); }
                c.Close();
                Log($"отключение {c.Id}");
            }
        }

        private void ReapLoop()
        {
            while (_running)
            {
                Thread.Sleep(2000);
                Conn[] all;
                var now = DateTime.UtcNow.Ticks;
                lock (_lock) { all = _conns.ToArray(); SweepRoomOpsLocked(now); }
                foreach (var c in all)
                {
                    if (TimeSpan.FromTicks(now - Interlocked.Read(ref c.LastSeenTicks)).TotalSeconds > IdleTimeoutSec)
                    {
                        Log($"{c.Id}: нет данных {IdleTimeoutSec} с, отключаю");
                        c.Close();
                        continue;
                    }
                    bool inRoom;
                    lock (_lock) inRoom = c.Room != null;
                    if (inRoom) Interlocked.Exchange(ref c.LastInRoomTicks, now);
                    else if (TimeSpan.FromTicks(now - Interlocked.Read(ref c.LastInRoomTicks)).TotalSeconds > JoinTimeoutSec)
                    {
                        Log($"{c.Id}: {JoinTimeoutSec:0} с без комнаты, отключаю");
                        c.Close();
                    }
                }
            }
        }

        private static byte[] Bytes(string line) => Encoding.UTF8.GetBytes(line + "\n");

        private static string ErrorLine(string code, string message) =>
            Protocol.Build(w => { w.WriteString("type", Protocol.Error); w.WriteString("code", code); w.WriteString("message", message); });

        private static void SendError(Conn c, string code, string message) => c.Send(ErrorLine(code, message));

        /// <summary>
        /// Без поля protocol клиент старый и проверку не проходит, пускаем. Иначе нужно целое, равное нашей версии, как в
        /// coop_server.py.
        /// </summary>
        internal static bool ProtocolOk(JsonElement m, out string shown)
        {
            shown = null;
            if (!m.TryGetProperty("protocol", out var pv) || pv.ValueKind == JsonValueKind.Null) return true;
            if (pv.ValueKind == JsonValueKind.Number && pv.TryGetInt64(out var n) && n == Protocol.Version) return true;
            var raw = pv.GetRawText();
            shown = raw.Length > 20 ? raw.Substring(0, 20) + "…" : raw;
            return false;
        }

        private void Handle(Conn c, JsonElement m)
        {
            var type = Protocol.TypeOf(m);
            if (type == Protocol.CreateRoom || type == Protocol.JoinRoom)
            {
                // Понятная ошибка вместо странных обрывов между разными версиями
                if (!ProtocolOk(m, out var shown))
                {
                    SendError(c, "protocol_mismatch", $"Версия «Кооператива» не совпадает с хостом (протокол {shown}, у хоста {Protocol.Version}). Обновите мод у всех игроков.");
                    return;
                }
                bool allowed;
                lock (_lock) allowed = RoomOpAllowedLocked(c.Ip);
                if (!allowed)
                {
                    Log($"{c.Id}: слишком часто входит в комнаты, отключаю");
                    c.SendAndClose(ErrorLine("rate_limited", "Слишком частые подключения, попробуйте через минуту."));
                    return;
                }
            }
            switch (type)
            {
                case Protocol.CreateRoom:
                    lock (_lock)
                    {
                        DropStaleLocked(c, Protocol.Str(m, "clientId") ?? "");
                        if (_rooms.Count >= MaxRooms) { SendError(c, "server_busy", "На сервере слишком много комнат, попробуйте позже."); break; }
                        var room = new Room { Code = NewCodeLocked() };
                        _rooms[room.Code] = room;
                        EnterRoomLocked(c, room, m, Protocol.RoomCreated, 0);
                        Log($"{c.Id} «{c.Name}» создал комнату {room.Code}");
                    }
                    break;

                case Protocol.JoinRoom:
                    lock (_lock)
                    {
                        // Тот же игрок переподключился раньше, чем сервер заметил обрыв: слот отдаём ему
                        var stale = DropStaleLocked(c, Protocol.Str(m, "clientId") ?? "");
                        var code = (Protocol.Str(m, "roomCode") ?? "").Trim().ToUpperInvariant();
                        Room room = null;
                        if (code.Length == 0 || code == "*")
                        {
                            room = _rooms.Values.Where(r => r.Players.Count < Protocol.MaxPlayers || r == c.Room).OrderByDescending(r => r.Players.Count).FirstOrDefault();
                            if (room == null)
                            {
                                if (_rooms.Count > 0) { SendError(c, "room_full", "Комната заполнена (максимум 4 игрока)."); break; }
                                if (!AutoCreateRoom) { SendError(c, "room_not_found", "Хост ещё не создал игру."); break; }
                                if (_rooms.Count >= MaxRooms) { SendError(c, "server_busy", "На сервере слишком много комнат, попробуйте позже."); break; }
                                room = new Room { Code = NewCodeLocked() };
                                _rooms[room.Code] = room;
                            }
                        }
                        else if (!_rooms.TryGetValue(code, out room)) { SendError(c, "room_not_found", "Комната не найдена."); break; }

                        if (room.Players.Contains(c)) { c.Send(RoomMessage(Protocol.RoomJoined, room, c)); break; }
                        if (room.Players.Count >= Protocol.MaxPlayers) { SendError(c, "room_full", "Комната заполнена (максимум 4 игрока)."); break; }
                        EnterRoomLocked(c, room, m, Protocol.RoomJoined, stale.room == room ? stale.slot : 0);
                        Log($"{c.Id} «{c.Name}» вошёл в {room.Code} (слот {c.Slot})");
                    }
                    break;

                case Protocol.PlayerState:
                case Protocol.Event:
                    {
                        bool isState = type == Protocol.PlayerState;
                        var payload = Protocol.Obj(m, isState ? "state" : "event");
                        if (payload.ValueKind != JsonValueKind.Object) { SendError(c, "invalid_message", isState ? "Нет поля state." : "Нет поля event."); break; }
                        string to = isState ? null : Protocol.Str(payload, "to");
                        string raw = payload.GetRawText();
                        var targets = new List<Conn>(Protocol.MaxPlayers);
                        int slot;
                        // Одним снимком под замком: повторный вход того же игрока в другом потоке обнуляет c.Slot и c.Room
                        lock (_lock)
                        {
                            if (c.Room == null) { SendError(c, "not_in_room", "Сначала войдите в комнату."); break; }
                            slot = c.Slot;
                            foreach (var p in c.Room.Players) if (p != c && (to == null || p.Id == to)) targets.Add(p);
                        }
                        RelayHook?.Invoke();
                        if (targets.Count == 0) break;
                        var line = Protocol.Build(w =>
                        {
                            w.WriteString("type", isState ? Protocol.RemotePlayerState : Protocol.RemoteEvent);
                            w.WriteString("playerId", c.Id);
                            w.WriteNumber("slot", slot);
                            w.WritePropertyName(isState ? "state" : "event");
                            w.WriteRawValue(raw, skipInputValidation: true);
                        });
                        var bytes = Bytes(line);
                        foreach (var p in targets) p.Send(bytes);
                    }
                    break;

                case Protocol.Ping:
                    {
                        double t = Protocol.Num(m, "t", 0);
                        c.Send(Protocol.Build(w => { w.WriteString("type", Protocol.Pong); w.WriteNumber("t", t); }));
                    }
                    break;

                case Protocol.LeaveRoom:
                    lock (_lock) LeaveRoomLocked(c);
                    break;

                default:
                    SendError(c, "unknown_type", "Неизвестный тип сообщения: " + (type ?? "нет"));
                    break;
            }
        }

        private string NewCodeLocked()
        {
            var rnd = new Random();
            for (int i = 0; i < 100; i++)
            {
                var code = new string(Enumerable.Range(0, 6).Select(_ => CodeAlphabet[rnd.Next(CodeAlphabet.Length)]).ToArray());
                if (!_rooms.ContainsKey(code)) return code;
            }
            throw new InvalidOperationException("не удалось выдать код комнаты");
        }

        private void EnterRoomLocked(Conn c, Room room, JsonElement m, string replyType, int preferSlot)
        {
            LeaveRoomLocked(c);
            c.Name = Protocol.CleanName(Protocol.Str(m, "name"));
            c.ClientId = Protocol.Str(m, "clientId") ?? "";
            AddToRoomLocked(c, room, preferSlot);
            c.Send(RoomMessage(replyType, room, c));
            var joined = Bytes(Protocol.Build(w =>
            {
                w.WriteString("type", Protocol.PlayerJoined);
                w.WriteString("playerId", c.Id);
                w.WriteString("name", c.Name);
                w.WriteNumber("slot", c.Slot);
            }));
            foreach (var p in room.Players) if (p != c) p.Send(joined);
        }

        /// <summary>
        /// Другое соединение с тем же clientId это тот же игрок, чей обрыв сервер ещё не заметил. Закрываем его, чтобы не было
        /// двойника, и возвращаем его комнату и слот: слот вернётся игроку, если он входит в ту же комнату.
        /// </summary>
        private (Room room, int slot) DropStaleLocked(Conn c, string cid)
        {
            (Room room, int slot) found = (null, 0);
            if (cid.Length == 0) return found;
            foreach (var s in _conns.Where(p => p != c && p.ClientId == cid).ToList())
            {
                if (s.Room != null && found.room == null) found = (s.Room, s.Slot);
                LeaveRoomLocked(s);
                s.ClientId = "";
                s.Close();
                Log($"{s.Id}: тот же игрок вошёл заново — старое соединение закрыто");
            }
            return found;
        }

        private bool RoomOpAllowedLocked(string ip)
        {
            long now = DateTime.UtcNow.Ticks;
            long window = RoomOpsWindowTicks;
            if (!_roomOps.TryGetValue(ip, out var r))
            {
                if (_roomOps.Count >= MaxTrackedIps) SweepRoomOpsLocked(now);
                if (_roomOps.Count >= MaxTrackedIps) EvictIdlestRoomOpsLocked();
                _roomOps[ip] = r = new RoomOps();
            }
            r.LastTry = now;
            while (r.Ops.Count > 0 && now - r.Ops.Peek() > window) r.Ops.Dequeue();
            if (r.Ops.Count >= RoomOpsPerMinute) return false;
            r.Ops.Enqueue(now);
            r.LastOp = now;
            return true;
        }

        private sealed class RoomOps
        {
            public readonly Queue<long> Ops = new Queue<long>();
            public long LastOp, LastTry;     // Последний разрешённый вход и последняя попытка, в том числе отказанная
        }

        private long RoomOpsWindowTicks => (long)(RoomOpsWindowSec * TimeSpan.TicksPerSecond);

        /// <summary>Счётчик адреса, у которого все входы старше окна, уже ни на что не влияет.</summary>
        private void SweepRoomOpsLocked(long now)
        {
            long window = RoomOpsWindowTicks;
            List<string> expired = null;
            foreach (var kv in _roomOps)
                if (now - kv.Value.LastOp > window) (expired ??= new List<string>()).Add(kv.Key);
            if (expired != null) foreach (var k in expired) _roomOps.Remove(k);
        }

        /// <summary>
        /// Забывает адрес, дольше всех не обращавшийся. По последней попытке, а не входу: адрес, упёршийся в предел и продолжающий
        /// стучаться, не получает сброс счётчика.
        /// </summary>
        private void EvictIdlestRoomOpsLocked()
        {
            string idlest = null; long idlestAt = long.MaxValue;
            foreach (var kv in _roomOps)
                if (kv.Value.LastTry < idlestAt) { idlestAt = kv.Value.LastTry; idlest = kv.Key; }
            if (idlest != null) _roomOps.Remove(idlest);
        }

        internal int RoomOpsTracked { get { lock (_lock) return _roomOps.Count; } }

        internal bool RoomOpAllowedForTest(string ip) { lock (_lock) return RoomOpAllowedLocked(ip); }

        private static void AddToRoomLocked(Conn c, Room room, int preferSlot = 0)
        {
            int slot = preferSlot > 0 && !room.Players.Any(p => p.Slot == preferSlot) ? preferSlot : 1;
            while (room.Players.Any(p => p.Slot == slot)) slot++;
            c.Slot = slot;
            c.Room = room;
            room.Players.Add(c);
        }

        private void LeaveRoomLocked(Conn c)
        {
            var room = c.Room;
            if (room == null) return;
            room.Players.Remove(c);
            c.Room = null;
            c.Slot = 0;
            if (room.Players.Count == 0) { _rooms.Remove(room.Code); return; }
            var left = Protocol.Build(w => { w.WriteString("type", Protocol.PlayerLeft); w.WriteString("playerId", c.Id); });
            foreach (var p in room.Players) p.Send(left);
        }

        private static string RoomMessage(string type, Room room, Conn me) => Protocol.Build(w =>
        {
            w.WriteString("type", type);
            w.WriteString("roomCode", room.Code);
            w.WriteString("playerId", me.Id);
            w.WriteNumber("slot", me.Slot);
            w.WriteStartArray("players");
            foreach (var p in room.Players)
            {
                w.WriteStartObject();
                w.WriteString("playerId", p.Id);
                w.WriteString("name", p.Name);
                w.WriteNumber("slot", p.Slot);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        });
    }
}
