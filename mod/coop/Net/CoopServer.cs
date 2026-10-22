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

        public Action<string> Log = _ => { };
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
            public readonly BlockingCollection<string> Out = new BlockingCollection<string>(512);
            public long LastSeenTicks = DateTime.UtcNow.Ticks;
            public volatile bool Closed;

            public void Send(string line)
            {
                if (Closed) return;
                if (!Out.TryAdd(line)) Close();      // клиент не успевает читать — отключаем, а не копим память
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
                    var c = new Conn { Id = "p" + Interlocked.Increment(ref _nextId), Tcp = tcp, Stream = tcp.GetStream() };
                    lock (_lock) _conns.Add(c);
                    new Thread(() => WriteLoop(c)) { IsBackground = true, Name = "coop-w-" + c.Id }.Start();
                    new Thread(() => ReadLoop(c)) { IsBackground = true, Name = "coop-r-" + c.Id }.Start();
                    Log($"подключение {c.Id} с {tcp.Client.RemoteEndPoint}");
                    c.Send(Protocol.Build(w => { w.WriteString("type", Protocol.Welcome); w.WriteNumber("protocol", Protocol.Version); w.WriteString("playerId", c.Id); }));
                }
                catch (Exception e) { Log("ошибка приёма: " + e.Message); try { tcp.Close(); } catch { } }
            }
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
                    if (TimeSpan.FromTicks(now - Interlocked.Read(ref c.LastSeenTicks)).TotalSeconds > IdleTimeoutSec)
                    {
                        Log($"{c.Id}: нет данных {IdleTimeoutSec} с, отключаю");
                        c.Close();
                    }
            }
        }

        // ------------------------------------------------------------ сообщения

        private static void SendError(Conn c, string code, string message) =>
            c.Send(Protocol.Build(w => { w.WriteString("type", Protocol.Error); w.WriteString("code", code); w.WriteString("message", message); }));

        private void Handle(Conn c, JsonElement m)
        {
            var type = Protocol.TypeOf(m);
            switch (type)
            {
                case Protocol.CreateRoom:
                    lock (_lock)
                    {
                        LeaveRoomLocked(c);
                        c.Name = Protocol.CleanName(Protocol.Str(m, "name"));
                        var room = new Room { Code = NewCodeLocked() };
                        _rooms[room.Code] = room;
                        AddToRoomLocked(c, room);
                        c.Send(RoomMessage(Protocol.RoomCreated, room, c));
                        Log($"{c.Id} «{c.Name}» создал комнату {room.Code}");
                    }
                    break;

                case Protocol.JoinRoom:
                    lock (_lock)
                    {
                        var code = (Protocol.Str(m, "roomCode") ?? "").Trim().ToUpperInvariant();
                        Room room = null;
                        if (code.Length == 0 || code == "*")
                        {
                            room = _rooms.Values.Where(r => r.Players.Count < Protocol.MaxPlayers).OrderByDescending(r => r.Players.Count).FirstOrDefault();
                            if (room == null)
                            {
                                if (_rooms.Count > 0) { SendError(c, "room_full", "Комната заполнена (максимум 4 игрока)."); break; }
                                room = new Room { Code = NewCodeLocked() };   // никого нет — первый вошедший создаёт комнату сам
                                _rooms[room.Code] = room;
                            }
                        }
                        else if (!_rooms.TryGetValue(code, out room)) { SendError(c, "room_not_found", "Комната не найдена."); break; }

                        if (room.Players.Contains(c)) { c.Send(RoomMessage(Protocol.RoomJoined, room, c)); break; }
                        // тот же игрок переподключился раньше, чем сервер заметил обрыв: старое соединение закрываем, слот отдаём ему
                        var cid = Protocol.Str(m, "clientId") ?? "";
                        int preferSlot = 0;
                        var stale = cid.Length > 0 ? room.Players.FirstOrDefault(p => p != c && p.ClientId == cid) : null;
                        if (stale != null) { preferSlot = stale.Slot; LeaveRoomLocked(stale); stale.Close(); Log($"{stale.Id}: тот же игрок вошёл заново — старое соединение закрыто"); }
                        if (room.Players.Count >= Protocol.MaxPlayers) { SendError(c, "room_full", "Комната заполнена (максимум 4 игрока)."); break; }
                        LeaveRoomLocked(c);
                        c.Name = Protocol.CleanName(Protocol.Str(m, "name"));
                        c.ClientId = cid;
                        AddToRoomLocked(c, room, preferSlot);
                        c.Send(RoomMessage(Protocol.RoomJoined, room, c));
                        var joined = Protocol.Build(w =>
                        {
                            w.WriteString("type", Protocol.PlayerJoined);
                            w.WriteString("playerId", c.Id);
                            w.WriteString("name", c.Name);
                            w.WriteNumber("slot", c.Slot);
                        });
                        foreach (var p in room.Players) if (p != c) p.Send(joined);
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
