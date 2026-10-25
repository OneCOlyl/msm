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
    /// Сервер комнат. Запускается прямо в игре у хоста (VPS и Termux не нужны) или в консоли для тестов.
    /// Комнаты по коду, до 4 игроков, слоты 1..4. JOIN_ROOM без кода заходит в единственную комнату — так работает «подключиться по IP».
    /// Защита от перегрузки: ограничено число соединений (всего и с одного адреса), комнат и входов в комнату в минуту с адреса;
    /// соединение, которое долго не входит в комнату, закрывается. Шифрования и пароля нет — сервер только для виртуальной сети.
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

        private readonly Dictionary<string, Queue<long>> _roomOps = new Dictionary<string, Queue<long>>();

        public Action<string> Log = _ => { };
        /// <summary>Сколько соединений всего (лишние получают server_busy и закрываются).</summary>
        public int MaxConnections = 32;
        /// <summary>Сколько соединений с одного адреса.</summary>
        public int MaxConnectionsPerIp = 8;
        /// <summary>Сколько CREATE_ROOM / JOIN_ROOM в минуту с одного адреса (больше — rate_limited, соединение закрывается).</summary>
        public int RoomOpsPerMinute = 60;
        /// <summary>Сколько комнат одновременно.</summary>
        public int MaxRooms = 16;
        /// <summary>Соединение без комнаты дольше этого закрывается.</summary>
        public double JoinTimeoutSec = 30;
        /// <summary>
        /// JOIN_ROOM без кода, когда комнат нет, создаёт комнату (отдельный сервер). У сервера в игре хоста — false: комнату создаёт
        /// только хост (CREATE_ROOM), и гость, успевший раньше, не займёт слот 1 — он подождёт и войдёт в комнату хоста.
        /// </summary>
        public bool AutoCreateRoom = true;
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

        // ------------------------------------------------------------ соединения

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
            public readonly BlockingCollection<string> Out = new BlockingCollection<string>(512);
            public long LastSeenTicks = DateTime.UtcNow.Ticks;
            public long LastInRoomTicks = DateTime.UtcNow.Ticks;
            public volatile bool Closed;
            private volatile bool _ending;

            public void Send(string line)
            {
                if (Closed || _ending) return;
                bool ok;
                try { ok = Out.TryAdd(line); } catch (InvalidOperationException) { return; }
                if (!ok) Close();      // клиент не успевает читать — отключаем, а не копим память
            }

            /// <summary>Отправить последнее сообщение (ошибку) и закрыть соединение, когда оно уйдёт.</summary>
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

        /// <summary>Ошибка и закрытие без потоков соединения: дописать, закрыть отправку, дочитать то, что клиент успел прислать (иначе сброс съест ошибку).</summary>
        private static void Reject(TcpClient tcp, string code, string message)
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    var s = tcp.GetStream();
                    var b = Encoding.UTF8.GetBytes(ErrorLine(code, message) + "\n");
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
                foreach (var line in c.Out.GetConsumingEnumerable())
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    c.Stream.Write(bytes, 0, bytes.Length);
                }
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
                lock (_lock) all = _conns.ToArray();
                var now = DateTime.UtcNow.Ticks;
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

        // ------------------------------------------------------------ сообщения

        private static string ErrorLine(string code, string message) =>
            Protocol.Build(w => { w.WriteString("type", Protocol.Error); w.WriteString("code", code); w.WriteString("message", message); });

        private static void SendError(Conn c, string code, string message) => c.Send(ErrorLine(code, message));

        /// <summary>
        /// Версия протокола из CREATE_ROOM / JOIN_ROOM. Нет поля (или null) — старый клиент без проверки, пускаем. Иначе это должно
        /// быть целое число, равное нашей версии: дробное, строка, огромное или другое число — protocol_mismatch (как в coop_server.py).
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
                // разные версии протокола не поймут друг друга — сразу понятная ошибка вместо странных обрывов
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
                        // тот же игрок переподключился раньше, чем сервер заметил обрыв: старое соединение закрываем, слот отдаём ему
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
                                room = new Room { Code = NewCodeLocked() };   // никого нет — первый вошедший создаёт комнату сам
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
                        Conn[] targets;
                        lock (_lock)
                        {
                            if (c.Room == null) { SendError(c, "not_in_room", "Сначала войдите в комнату."); break; }
                            targets = c.Room.Players.Where(p => p != c && (to == null || p.Id == to)).ToArray();
                        }
                        var line = Protocol.Build(w =>
                        {
                            w.WriteString("type", isState ? Protocol.RemotePlayerState : Protocol.RemoteEvent);
                            w.WriteString("playerId", c.Id);
                            w.WriteNumber("slot", c.Slot);
                            w.WritePropertyName(isState ? "state" : "event");
                            w.WriteRawValue(raw, skipInputValidation: true);
                        });
                        foreach (var p in targets) p.Send(line);
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

        /// <summary>Общий вход в комнату для CREATE_ROOM и JOIN_ROOM: имя, clientId, слот, ответ входящему, PLAYER_JOINED остальным.</summary>
        private void EnterRoomLocked(Conn c, Room room, JsonElement m, string replyType, int preferSlot)
        {
            LeaveRoomLocked(c);
            c.Name = Protocol.CleanName(Protocol.Str(m, "name"));
            c.ClientId = Protocol.Str(m, "clientId") ?? "";
            AddToRoomLocked(c, room, preferSlot);
            c.Send(RoomMessage(replyType, room, c));
            var joined = Protocol.Build(w =>
            {
                w.WriteString("type", Protocol.PlayerJoined);
                w.WriteString("playerId", c.Id);
                w.WriteString("name", c.Name);
                w.WriteNumber("slot", c.Slot);
            });
            foreach (var p in room.Players) if (p != c) p.Send(joined);
        }

        /// <summary>
        /// Другие соединения с тем же clientId — это тот же игрок, чей обрыв сервер ещё не заметил: убрать их из комнат и закрыть,
        /// чтобы не было двойника. Возвращает комнату и слот такого соединения (слот вернём игроку, если он входит в ту же комнату).
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

        /// <summary>Не больше RoomOpsPerMinute входов в комнату за минуту с одного адреса.</summary>
        private bool RoomOpAllowedLocked(string ip)
        {
            long now = DateTime.UtcNow.Ticks;
            if (!_roomOps.TryGetValue(ip, out var q)) _roomOps[ip] = q = new Queue<long>();
            while (q.Count > 0 && now - q.Peek() > TimeSpan.TicksPerMinute) q.Dequeue();
            if (_roomOps.Count > 1024)
                foreach (var k in _roomOps.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) _roomOps.Remove(k);
            if (q.Count >= RoomOpsPerMinute) return false;
            q.Enqueue(now);
            return true;
        }

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
