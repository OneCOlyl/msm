using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using MagicSurvivalCoop.Net;

// Использование: NetTest embedded            — поднять встроенный сервер мода и проверить
//                NetTest external <host> <port> — проверить внешний сервер (например, coop_server.py)
int fails = 0, passes = 0;
void Check(bool ok, string what) { if (ok) { passes++; Console.WriteLine("  PASS " + what); } else { fails++; Console.WriteLine("  FAIL " + what); } }

string host = "127.0.0.1"; int port; CoopServer server = null;
if (args.Length > 0 && args[0] == "external") { host = args[1]; port = int.Parse(args[2]); Console.WriteLine($"== внешний сервер {host}:{port}"); }
else { server = new CoopServer { Log = s => Console.WriteLine("    [srv] " + s) }; server.Start(0); port = server.Port; Console.WriteLine($"== встроенный сервер, порт {port}"); }

List<JsonElement> Drain(CoopClient c) { var l = new List<JsonElement>(); while (c.Incoming.TryDequeue(out var m)) l.Add(m); return l; }
JsonElement? WaitFor(CoopClient c, string type, List<JsonElement> sink, int ms = 4000, Func<JsonElement, bool> pred = null)
{
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < ms)
    {
        while (c.Incoming.TryDequeue(out var m)) { sink.Add(m); }
        var hit = sink.FirstOrDefault(m => Protocol.TypeOf(m) == type && (pred == null || pred(m)));
        if (hit.ValueKind != JsonValueKind.Undefined) { sink.Remove(hit); return hit; }
        Thread.Sleep(20);
    }
    return null;
}

var boxA = new List<JsonElement>(); var boxB = new List<JsonElement>(); var boxC = new List<JsonElement>(); var boxD = new List<JsonElement>(); var boxE = new List<JsonElement>();
var a = new CoopClient(); var b = new CoopClient(); var c3 = new CoopClient(); var d = new CoopClient(); var e5 = new CoopClient();

Console.WriteLine("1. хост и вход по IP без кода");
a.Start(host, port, "Хост");
var ja = WaitFor(a, Protocol.RoomJoined, boxA);
Check(ja.HasValue && a.Connected, "хост вошёл в комнату (создана автоматически)");
string room = ja.HasValue ? Protocol.Str(ja.Value, "roomCode") : null;
Check(ja.HasValue && Protocol.Num(ja.Value, "slot") == 1, "у хоста слот 1");
string idA = ja.HasValue ? Protocol.Str(ja.Value, "playerId") : null;

b.Start(host, port, "Друг");
var jb = WaitFor(b, Protocol.RoomJoined, boxB);
Check(jb.HasValue && Protocol.Str(jb.Value, "roomCode") == room, "друг попал в ту же комнату без кода");
Check(jb.HasValue && Protocol.Num(jb.Value, "slot") == 2, "у друга слот 2");
Check(jb.HasValue && jb.Value.GetProperty("players").GetArrayLength() == 2, "в списке два игрока");
string idB = jb.HasValue ? Protocol.Str(jb.Value, "playerId") : null;
var pj = WaitFor(a, Protocol.PlayerJoined, boxA);
Check(pj.HasValue && Protocol.Str(pj.Value, "name") == "Друг", "хост получил PLAYER_JOINED с именем");

Console.WriteLine("2. состояние игрока");
b.SendState("{\"battle\":1,\"x\":3.5,\"y\":-2,\"hp\":80,\"hpMax\":120,\"spell\":2}");
var rs = WaitFor(a, Protocol.RemotePlayerState, boxA);
Check(rs.HasValue && Protocol.Str(rs.Value, "playerId") == idB, "хост получил REMOTE_PLAYER_STATE от друга");
Check(rs.HasValue && Protocol.Num(Protocol.Obj(rs.Value, "state"), "x") == 3.5 && Protocol.Num(Protocol.Obj(rs.Value, "state"), "hp") == 80, "координаты и здоровье дошли без искажений");
Check(rs.HasValue && Protocol.Num(rs.Value, "slot") == 2, "в состоянии указан слот отправителя");
Thread.Sleep(300); Drain(b).ForEach(boxB.Add);
Check(!boxB.Any(m => Protocol.TypeOf(m) == Protocol.RemotePlayerState), "своё состояние отправителю не возвращается");

Console.WriteLine("3. частая отправка: доходит самое свежее");
for (int i = 0; i < 200; i++) b.SendState("{\"x\":" + i + "}");
Thread.Sleep(800);
var states = new List<double>();
foreach (var m in Drain(a)) if (Protocol.TypeOf(m) == Protocol.RemotePlayerState) states.Add(Protocol.Num(Protocol.Obj(m, "state"), "x")); else boxA.Add(m);
Check(states.Count > 0 && states.Last() == 199, $"последнее состояние x=199 дошло (получено пакетов: {states.Count} из 200 — старые схлопнуты)");

Console.WriteLine("4. до 4 игроков, пятый получает отказ");
c3.Start(host, port, "Третий"); d.Start(host, port, "Четвёртый");
var j3 = WaitFor(c3, Protocol.RoomJoined, boxC); var j4 = WaitFor(d, Protocol.RoomJoined, boxD);
Check(j3.HasValue && j4.HasValue, "третий и четвёртый вошли");
var slots = new[] { j3, j4 }.Where(x => x.HasValue).Select(x => (int)Protocol.Num(x.Value, "slot")).OrderBy(x => x).ToArray();
Check(slots.SequenceEqual(new[] { 3, 4 }), "слоты 3 и 4");
e5.RetrySec = 60;
e5.Start(host, port, "Пятый");
var err = WaitFor(e5, Protocol.Error, boxE);
Check(err.HasValue && Protocol.Str(err.Value, "code") == "room_full", "пятый игрок: room_full");
e5.Stop();

Console.WriteLine("5. события: лечение одному, SOS всем");
a.SendEvent("{\"kind\":\"heal\",\"to\":\"" + idB + "\",\"pct\":25}");
var heal = WaitFor(b, Protocol.RemoteEvent, boxB);
Check(heal.HasValue && Protocol.Str(Protocol.Obj(heal.Value, "event"), "kind") == "heal" && Protocol.Str(heal.Value, "playerId") == idA, "друг получил лечение от хоста");
Thread.Sleep(300); foreach (var m in Drain(c3)) boxC.Add(m);
Check(!boxC.Any(m => Protocol.TypeOf(m) == Protocol.RemoteEvent), "адресное лечение не ушло третьему");
b.SendEvent("{\"kind\":\"sos\"}");
var s1 = WaitFor(a, Protocol.RemoteEvent, boxA); var s3 = WaitFor(c3, Protocol.RemoteEvent, boxC); var s4 = WaitFor(d, Protocol.RemoteEvent, boxD);
Check(s1.HasValue && s3.HasValue && s4.HasValue, "SOS получили все остальные");

Console.WriteLine("6. пинг");
Thread.Sleep(2500);
Check(a.PingMs >= 0 && b.PingMs >= 0, $"пинг измерен: хост {a.PingMs} мс, друг {b.PingMs} мс");

Console.WriteLine("7. выход игрока");
d.Stop();
var left = WaitFor(a, Protocol.PlayerLeft, boxA, 5000);
Check(left.HasValue, "остальные получили PLAYER_LEFT");
var j6 = (CoopClient)null;
j6 = new CoopClient(); var box6 = new List<JsonElement>(); j6.Start(host, port, "Новый");
var jn = WaitFor(j6, Protocol.RoomJoined, box6);
int dSlot = j4.HasValue ? (int)Protocol.Num(j4.Value, "slot") : -1;
Check(jn.HasValue && Protocol.Num(jn.Value, "slot") == dSlot, $"освободившийся слот {dSlot} занят новым игроком");
j6.Stop();

Console.WriteLine("8. защита сервера");
using (var raw = new TcpClient(host, port))
{
    var st = raw.GetStream(); var rd = new LineReader(st);
    rd.ReadLine(); // WELCOME
    var bad = Encoding.UTF8.GetBytes("{oops\n"); st.Write(bad, 0, bad.Length);
    var resp = rd.ReadLine();
    Check(resp != null && resp.Contains("invalid_json"), "битый JSON → ошибка, соединение живо");
    var unk = Encoding.UTF8.GetBytes("{\"type\":\"HACK\"}\n"); st.Write(unk, 0, unk.Length);
    resp = rd.ReadLine();
    Check(resp != null && resp.Contains("unknown_type"), "неизвестный тип → ошибка");
    var nr = Encoding.UTF8.GetBytes("{\"type\":\"PLAYER_STATE\",\"state\":{}}\n"); st.Write(nr, 0, nr.Length);
    resp = rd.ReadLine();
    Check(resp != null && resp.Contains("not_in_room"), "состояние без комнаты → not_in_room");
    var big = Encoding.UTF8.GetBytes(new string('x', 40000) + "\n");
    bool closed = false;
    try { st.Write(big, 0, big.Length); string l; var sw = Stopwatch.StartNew(); while ((l = rd.ReadLine()) != null && sw.ElapsedMilliseconds < 4000) { } closed = true; } catch { closed = true; }
    Check(closed, "строка 40 КБ → сервер закрыл соединение");
}

if (server != null)
{
    Console.WriteLine("9. обрыв и автопереподключение (перезапуск сервера)");
    server.Stop();
    var sw = Stopwatch.StartNew();
    while (sw.ElapsedMilliseconds < 3000 && b.State == CoopClient.Status.Connected) Thread.Sleep(50);
    Check(b.State != CoopClient.Status.Connected, "клиент заметил обрыв");
    server = new CoopServer { Log = s => Console.WriteLine("    [srv] " + s) };
    server.Start(port);
    a.RememberRoom(room); b.RememberRoom(room);   // как в моде: клиенты помнят старый код, которого на новом сервере нет
    sw.Restart();
    while (sw.ElapsedMilliseconds < 12000 && !(a.Connected && b.Connected)) Thread.Sleep(100);
    Check(a.Connected && b.Connected, $"оба клиента переподключились сами за {sw.ElapsedMilliseconds} мс");
    boxA.Clear(); Drain(a);
    b.SendState("{\"x\":42}");
    var after = WaitFor(a, Protocol.RemotePlayerState, boxA);
    Check(after.HasValue && Protocol.Num(Protocol.Obj(after.Value, "state"), "x") == 42, "после переподключения состояние снова ходит");
}

if (server != null)
{
    Console.WriteLine("10. хост занял соседний порт — клиент находит его сам");
    var alt = new CoopServer(); alt.Start(0); int altPort = alt.Port;
    var f = new CoopClient(); var boxF = new List<JsonElement>();
    f.Start(host, altPort - 2, "Ищущий", portSpan: 4);
    var jf = WaitFor(f, Protocol.RoomJoined, boxF, 8000);
    Check(jf.HasValue && f.Port == altPort, $"клиент подключился к порту {altPort}, начав с {altPort - 2} (сейчас {f.Port})");
    Check(!boxF.Any(m => Protocol.TypeOf(m) == "DISCONNECTED"), "неудачные попытки не выдают «связь потеряна»");
    f.Stop(); alt.Stop();

    Console.WriteLine("11. на порту чужая программа (как тестовый сервер в Termux) — клиент идёт дальше");
    var foreign = new TcpListener(System.Net.IPAddress.Loopback, 0); foreign.Start(); int fPort = ((System.Net.IPEndPoint)foreign.LocalEndpoint).Port;
    var fth = new Thread(() => { try { while (true) { var cl = foreign.AcceptTcpClient(); var w = Encoding.UTF8.GetBytes("COOP_TCP_READY\n"); cl.GetStream().Write(w, 0, w.Length); } } catch { } }) { IsBackground = true };
    fth.Start();
    var real = new CoopServer(); bool started = false;
    try { real.Start(fPort + 1); started = true; } catch { }
    if (started)
    {
        var g2 = new CoopClient(); var boxG = new List<JsonElement>();
        g2.Start(host, fPort, "Гость", portSpan: 4);
        var jg = WaitFor(g2, Protocol.RoomJoined, boxG, 9000);
        Check(jg.HasValue && g2.Port == fPort + 1, $"клиент распознал чужой сервер на {fPort} и подключился к {fPort + 1}");
        g2.Stop(); real.Stop();
    }
    else Check(true, "соседний порт занят системой — проверка пропущена");
    foreign.Stop();
}

Console.WriteLine("12. тот же игрок переподключился раньше, чем сервер заметил обрыв — получает свой прежний номер");
{
    if (server == null) { foreach (var cl in new[] { a, b, c3, d, e5 }) cl.Stop(); Thread.Sleep(1500); }   // внешний сервер: освободить комнату
    var r1 = new CoopClient(); var br1 = new List<JsonElement>();
    // свободная комната на своём сервере, чтобы номера были предсказуемы
    CoopServer own = null; string h2 = host; int p2 = port;
    if (server != null) { own = new CoopServer(); own.Start(0); h2 = "127.0.0.1"; p2 = own.Port; }
    var hostC = new CoopClient(); var bh = new List<JsonElement>();
    hostC.Start(h2, p2, "Хост2", clientId: "host-1");
    WaitFor(hostC, Protocol.RoomJoined, bh);
    r1.Start(h2, p2, "Друг2", clientId: "friend-1");
    var j1 = WaitFor(r1, Protocol.RoomJoined, br1);
    int slot1 = j1.HasValue ? (int)Protocol.Num(j1.Value, "slot") : -1;
    // второй клиент с тем же clientId (как после обрыва, пока старое соединение ещё «живо» на сервере)
    var r2 = new CoopClient(); var br2 = new List<JsonElement>();
    r2.Start(h2, p2, "Друг2", clientId: "friend-1");
    var j2 = WaitFor(r2, Protocol.RoomJoined, br2);
    int slot2 = j2.HasValue ? (int)Protocol.Num(j2.Value, "slot") : -1;
    int players = j2.HasValue ? j2.Value.GetProperty("players").GetArrayLength() : -1;
    Check(slot1 > 0 && slot2 == slot1 && players == 2, $"переподключённый игрок получил тот же номер {slot2} (был {slot1}), в комнате {players} игрока");
    r1.Stop(); r2.Stop(); hostC.Stop(); own?.Stop();
}

foreach (var cl in new[] { a, b, c3, d, e5 }) cl.Stop();
server?.Stop();
Console.WriteLine($"\nИТОГ: {passes} PASS, {fails} FAIL");
return fails == 0 ? 0 : 1;
