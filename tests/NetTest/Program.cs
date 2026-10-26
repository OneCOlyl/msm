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
//                NetTest external <host> <port> [<порт с пределами>] — проверить внешний сервер (например, coop_server.py);
//                  третий порт — второй coop_server.py с --max-per-ip 3 --max-connections 10 --room-ops-per-minute 4 --join-timeout 2
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

Console.WriteLine("7б. кириллица и эмодзи на границе буфера чтения");
{
    // буква «Ж» (2 байта) и эмодзи (4 байта) попадают на границу 4096-байтового буфера LineReader
    var text = new string('a', 4095) + "Ж" + new string('b', 4090) + "🔥" + "конец";
    var ms = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(text + "\n"));
    Check(new LineReader(ms).ReadLine() == text, "строка собирается из байтов целиком и только потом декодируется — символы не рвутся");
}
Console.WriteLine("7в. LineReader: целое, пустое и оборванное сообщение, дробление TCP");
{
    LineReader Of(string s, int chunk = 0) { var m = new System.IO.MemoryStream(Encoding.UTF8.GetBytes(s)); return new LineReader(chunk > 0 ? new Chunked(m, chunk) : m); }
    var r = Of("{\"type\":\"PING\"}\n");
    Check(r.ReadLine() == "{\"type\":\"PING\"}" && r.ReadLine() == null, "целое сообщение, затем конец потока → null");
    r = Of("\n\r\n{\"a\":1}\n");
    Check(r.ReadLine() == "" && r.ReadLine() == "" && r.ReadLine() == "{\"a\":1}" && r.ReadLine() == null, "пустые строки отдаются пустыми, сообщение после них цело");
    r = Of("{\"type\":\"PING\"}\n{\"type\":\"PLAYER_ST");
    Check(r.ReadLine() == "{\"type\":\"PING\"}" && r.ReadLine() == null && r.ReadLine() == null, "обрыв посреди JSON: хвост без \\n не отдаётся, дальше null");
    Check(Of("{\"x\":1}").ReadLine() == null, "единственное сообщение без \\n → null");
    var many = string.Concat(Enumerable.Range(0, 50).Select(i => "{\"i\":" + i + ",\"s\":\"Ж🔥" + new string('z', i * 37) + "\"}\n"));
    foreach (var chunk in new[] { 1, 2, 3, 7, 4095, 4097 })
    {
        var cr = Of(many, chunk); var got = new List<string>(); string l;
        while ((l = cr.ReadLine()) != null) got.Add(l);
        Check(got.SequenceEqual(many.Split('\n').Take(50)), $"50 сообщений кусками по {chunk} Б читаются без ошибок ({got.Count})");
    }
}
Console.WriteLine("7г. Inbox: всё по порядку, старые состояния выбрасываются только сверх предела, события не теряются");
{
    JsonElement J(string s) { using var doc = JsonDocument.Parse(s); return doc.RootElement.Clone(); }
    JsonElement St(string id, int x) => J("{\"type\":\"REMOTE_PLAYER_STATE\",\"playerId\":\"" + id + "\",\"slot\":2,\"state\":{\"x\":" + x + "}}");
    JsonElement Ev(string id, int n) => J("{\"type\":\"REMOTE_EVENT\",\"playerId\":\"" + id + "\",\"event\":{\"kind\":\"dmg\",\"n\":" + n + "}}");
    string Desc(JsonElement m) => Protocol.TypeOf(m) == Protocol.RemotePlayerState ? "S" + Protocol.Str(m, "playerId") + Protocol.Num(Protocol.Obj(m, "state"), "x")
        : Protocol.TypeOf(m) == Protocol.RemoteEvent ? "E" + Protocol.Num(Protocol.Obj(m, "event"), "n") : Protocol.TypeOf(m);
    List<string> All(Inbox q) { var l = new List<string>(); while (q.TryDequeue(out var m)) l.Add(Desc(m)); return l; }

    var ib = new Inbox();
    // состояние — не полный снимок: враги приходят не в каждом; то, что с врагами, не должно пропасть из-за следующего без них
    ib.TryEnqueue(J("{\"type\":\"REMOTE_PLAYER_STATE\",\"playerId\":\"p2\",\"state\":{\"x\":1,\"en\":[[1,2,3,4]]}}"), 60);
    ib.TryEnqueue(St("p2", 2), 50); ib.TryEnqueue(Ev("p2", 3), 50); ib.TryEnqueue(St("p2", 4), 50);
    var seq = new List<JsonElement>(); while (ib.TryDequeue(out var sm)) seq.Add(sm);
    Check(seq.Count == 4 && Protocol.Obj(seq[0], "state").TryGetProperty("en", out _) && string.Join(",", seq.Select(Desc)) == "Sp21,Sp22,E3,Sp24",
          $"пачка состояний и событие доходят все и по порядку, враги из первого состояния целы ({string.Join(",", seq.Select(Desc))})");

    int K = Inbox.MaxStatesPerPlayer;
    for (int i = 0; i < 1000; i++) { ib.TryEnqueue(St("p2", i), 50); ib.TryEnqueue(St("p3", i), 50); if (i % 100 == 0) ib.TryEnqueue(Ev("p2", i), 50); }
    Check(ib.Count == 2 * K + 10, $"1000 состояний от двух игроков и 10 событий → в очереди {ib.Count} (ждём {2 * K + 10})");
    var order = All(ib);
    Check(order.Where(s => s[0] == 'E').SequenceEqual(Enumerable.Range(0, 10).Select(i => "E" + i * 100)), "все события на месте и по порядку");
    var kept2 = order.Where(s => s.StartsWith("Sp2")).ToList(); var kept3 = order.Where(s => s.StartsWith("Sp3")).ToList();
    Check(kept2.SequenceEqual(Enumerable.Range(1000 - K, K).Select(i => "Sp2" + i)) && kept3.SequenceEqual(Enumerable.Range(1000 - K, K).Select(i => "Sp3" + i)),
          $"от каждого игрока — последние {K} состояний по порядку ({kept2.FirstOrDefault()}…{kept2.LastOrDefault()})");

    ib.TryEnqueue(St("p2", 1), 50); ib.TryEnqueue(St("p3", 1), 50); ib.TryEnqueue(St("p2", 2), 50);
    ib.TryEnqueue(J("{\"type\":\"PLAYER_LEFT\",\"playerId\":\"p2\"}"), 30);
    Check(string.Join(",", All(ib)) == "Sp31,PLAYER_LEFT", "PLAYER_LEFT убирает неразобранные состояния ушедшего, чужие остаются");
    ib.TryEnqueue(St("p2", 1), 50); ib.TryEnqueue(Ev("p2", 7), 50);
    ib.Enqueue(J("{\"type\":\"DISCONNECTED\"}"));
    Check(string.Join(",", All(ib)) == "E7,DISCONNECTED", "обрыв связи убирает все старые состояния, события остаются");

    int accepted = 0;
    for (int i = 0; i < Inbox.MaxMessages + 10; i++) if (ib.TryEnqueue(Ev("p2", i), 10)) accepted++;
    Check(accepted == Inbox.MaxMessages && !ib.TryEnqueue(Ev("p2", -1), 10), $"событий принято ровно {accepted} (предел {Inbox.MaxMessages}), дальше отказ");
    Check(ib.TryEnqueue(St("p2", 5), 50) && ib.TryEnqueue(St("p2", 6), 50), "состояния принимаются и при полной очереди событий (их число на игрока и так ограничено)");
    ib.Clear();
    Check(!ib.TryEnqueue(Ev("p2", 0), Inbox.MaxBytes + 1) && ib.TryEnqueue(Ev("p2", 0), 100), "предел по байтам тоже работает");
    ib.Clear();
    int took = 0; bool stopped = false;
    for (int i = 0; i < Inbox.MaxEntries + 10 && !stopped; i++) { if (ib.TryEnqueue(St("p2", i), 10)) took++; else stopped = true; }
    Check(stopped && took == Inbox.MaxEntries && ib.Count == Inbox.MaxStatesPerPlayer,
          $"игра стоит: записей не больше {Inbox.MaxEntries} (принято {took}), ждут только {ib.Count} последних состояний");
    ib.Clear();
    Check(ib.Count == 0 && ib.TryEnqueue(St("p2", 1), 10), "после разбора очередь снова принимает");
    ib.Clear();

    // сетевой поток пишет, главный читает одновременно: порядок, ни одно событие не потеряно, счётчики сходятся
    var got = new List<string>(); var writer = new Thread(() =>
    {
        for (int i = 0; i < 20000; i++) { ib.TryEnqueue(St("p2", i), 10); if (i % 7 == 0) ib.TryEnqueue(Ev("p2", i), 10); }
    });
    writer.Start();
    var swc = Stopwatch.StartNew();
    while ((writer.IsAlive || ib.Count > 0) && swc.ElapsedMilliseconds < 10000) { while (ib.TryDequeue(out var cm)) got.Add(Desc(cm)); if (got.Count % 500 == 0) Thread.Yield(); }
    var gotEv = got.Where(s => s[0] == 'E').Select(s => int.Parse(s.Substring(1))).ToList();
    var gotSt = got.Where(s => s[0] == 'S').Select(s => int.Parse(s.Substring(3))).ToList();
    Check(gotEv.SequenceEqual(Enumerable.Range(0, 20000).Where(i => i % 7 == 0)), $"параллельно: все {gotEv.Count} событий по порядку");
    Check(gotSt.Count > 0 && gotSt.Last() == 19999 && gotSt.Zip(gotSt.Skip(1), (p, q) => p < q).All(x => x), $"параллельно: состояния по порядку, последнее дошло ({gotSt.Count} из 20000)");
    Check(ib.Count == 0 && ib.Pending == 0, $"счётчики сошлись: ждёт {ib.Count}, событий {ib.Pending}");
}
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
    var old = Encoding.UTF8.GetBytes("{\"type\":\"JOIN_ROOM\",\"roomCode\":\"\",\"name\":\"Старый\",\"protocol\":1}\n"); st.Write(old, 0, old.Length);
    resp = rd.ReadLine();
    Check(resp != null && resp.Contains("protocol_mismatch"), "клиент со старым протоколом → понятная ошибка protocol_mismatch");
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
    // два порта перед altPort должны отвечать отказом: случайный порт может оказаться через один от сервера прошлых шагов
    // (на CI порты выдаются подряд), и клиент вошёл бы к нему. Сокет с Bind без Listen держит порт закрытым на время теста
    CoopServer alt = null; int altPort = 0; var holds = new List<Socket>();
    for (int attempt = 0; attempt < 20 && alt == null; attempt++)
    {
        var cand = new CoopServer(); cand.Start(0);
        try
        {
            foreach (var p in new[] { cand.Port - 2, cand.Port - 1 })
            {
                var h = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                holds.Add(h);
                h.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Any, p));
            }
            alt = cand; altPort = cand.Port;
        }
        catch (SocketException)
        {
            foreach (var h in holds) h.Dispose();
            holds.Clear();
            cand.Stop();
        }
    }
    Check(alt != null, "нашёлся порт, перед которым два свободных");
    var f = new CoopClient(); var boxF = new List<JsonElement>();
    f.Start(host, altPort - 2, "Ищущий", portSpan: 4);
    var jf = WaitFor(f, Protocol.RoomJoined, boxF, 8000);
    Check(jf.HasValue && f.Port == altPort, $"клиент подключился к порту {altPort}, начав с {altPort - 2} (сейчас {f.Port})");
    Check(!boxF.Any(m => Protocol.TypeOf(m) == "DISCONNECTED"), "неудачные попытки не выдают «связь потеряна»");
    f.Stop(); alt?.Stop();
    foreach (var h in holds) h.Dispose();

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
Thread.Sleep(500);

// ---- дальше — общие проверки совместимости: одни и те же ответы и коды ошибок у встроенного сервера и coop_server.py
// для встроенного — свой свежий сервер (у основного остались комнаты прошлых проверок), для внешнего — тот же
CoopServer fresh = null; string fh = host; int fp = port;
if (server != null) { fresh = new CoopServer { Log = s => Console.WriteLine("    [srv2] " + s) }; fresh.Start(0); fh = "127.0.0.1"; fp = fresh.Port; }

Console.WriteLine("13. хост явно создаёт комнату (create): слот 1, гость входит к нему; обрыв хоста — возврат в ту же комнату");
{
    using var proxy = new Proxy(fh, fp);
    var hc = new CoopClient { RetrySec = 0.3 }; var bh = new List<JsonElement>();
    hc.Start("127.0.0.1", proxy.Port, "Хост", create: true, clientId: "host-13");
    var created = WaitFor(hc, Protocol.RoomCreated, bh);
    Check(created.HasValue && Protocol.Num(created.Value, "slot") == 1, "хост получил ROOM_CREATED и слот 1");
    string code13 = created.HasValue ? Protocol.Str(created.Value, "roomCode") : null;
    var gc = new CoopClient(); var bg = new List<JsonElement>();
    gc.Start(fh, fp, "Гость", clientId: "guest-13");
    var gj = WaitFor(gc, Protocol.RoomJoined, bg);
    Check(gj.HasValue && Protocol.Str(gj.Value, "roomCode") == code13 && Protocol.Num(gj.Value, "slot") == 2, "гость без кода вошёл в комнату хоста, слот 2");
    proxy.Cut();                                                   // обрыв связи хоста (мобильная сеть)
    var back = WaitFor(hc, Protocol.RoomJoined, bh, 8000);
    Check(back.HasValue && Protocol.Str(back.Value, "roomCode") == code13, $"хост вернулся в ту же комнату {code13}, а не создал новую ({(back.HasValue ? Protocol.Str(back.Value, "roomCode") : "нет ответа")})");
    Check(back.HasValue && Protocol.Num(back.Value, "slot") == 1 && back.Value.GetProperty("players").GetArrayLength() == 2, "у хоста снова слот 1, в комнате два игрока — без двойника");
    Check(!bh.Any(m => Protocol.TypeOf(m) == Protocol.RoomCreated), "второй комнаты не создано");
    hc.Stop(); gc.Stop();
    Thread.Sleep(300);
}

Console.WriteLine("14. clientId сохраняется и при CREATE_ROOM: переподключение хоста раньше, чем сервер заметил обрыв");
{
    var (t1, r1, w1) = Raw(fh, fp);
    SendLine(w1, "{\"type\":\"CREATE_ROOM\",\"name\":\"Хост\",\"clientId\":\"host-14\",\"protocol\":2}");
    var cr = ReadType(r1, Protocol.RoomCreated);
    string code14 = cr.HasValue ? Protocol.Str(cr.Value, "roomCode") : "";
    var (t2, r2, w2) = Raw(fh, fp);
    SendLine(w2, "{\"type\":\"JOIN_ROOM\",\"roomCode\":\"" + code14 + "\",\"name\":\"Гость\",\"clientId\":\"guest-14\",\"protocol\":2}");
    ReadType(r2, Protocol.RoomJoined);
    // тот же хост с нового соединения (старое ещё открыто — сервер обрыва не видел)
    var (t3, r3, w3) = Raw(fh, fp);
    SendLine(w3, "{\"type\":\"JOIN_ROOM\",\"roomCode\":\"" + code14 + "\",\"name\":\"Хост\",\"clientId\":\"host-14\",\"protocol\":2}");
    var again = ReadType(r3, Protocol.RoomJoined);
    Check(again.HasValue && Protocol.Num(again.Value, "slot") == 1, $"хост получил прежний слот 1 (вышло {(again.HasValue ? Protocol.Num(again.Value, "slot") : -1)})");
    Check(again.HasValue && again.Value.GetProperty("players").GetArrayLength() == 2, "в комнате два игрока: старое соединение хоста убрано");
    Check(ClosedBy(r1, 3000), "старое соединение хоста закрыто сервером");
    // снова CREATE_ROOM с тем же clientId — двойник в прежней комнате тоже убирается
    var (t4, r4, w4) = Raw(fh, fp);
    SendLine(w4, "{\"type\":\"CREATE_ROOM\",\"name\":\"Хост\",\"clientId\":\"host-14\",\"protocol\":2}");
    ReadType(r4, Protocol.RoomCreated);
    string id3 = again.HasValue ? Protocol.Str(again.Value, "playerId") : "?";
    var leftMsg = ReadWhere(r2, m => Protocol.TypeOf(m) == Protocol.PlayerLeft && Protocol.Str(m, "playerId") == id3);
    Check(leftMsg.HasValue, "CREATE_ROOM с тем же clientId: гость прежней комнаты получил PLAYER_LEFT за двойника");
    foreach (var t in new[] { t1, t2, t3, t4 }) t.Close();
    Thread.Sleep(300);
}

Console.WriteLine("15. поле protocol: одинаковая проверка у обоих серверов, без падений");
{
    var (t, r, s) = Raw(fh, fp);
    foreach (var (pv, ok) in new[] { ("", true), ("null", true), ("2", true), ("1", false), ("3", false), ("2.0", false), ("2.5", false), ("\"2\"", false),
                                      ("true", false), ("99999999999999999999999", false), ("1e400", false), ("-2", false), ("[2]", false), ("{\"v\":2}", false) })
    {
        var field = pv.Length == 0 ? "" : ",\"protocol\":" + pv;
        SendLine(s, "{\"type\":\"JOIN_ROOM\",\"roomCode\":\"\",\"name\":\"Проверка\"" + field + "}");
        var resp = ReadType(r, Protocol.RoomJoined, Protocol.Error);
        bool joined = resp.HasValue && Protocol.TypeOf(resp.Value) == Protocol.RoomJoined;
        bool mismatch = resp.HasValue && Protocol.Str(resp.Value, "code") == "protocol_mismatch";
        Check(ok ? joined : mismatch, $"protocol {(pv.Length == 0 ? "нет" : pv)} → {(ok ? "вход" : "protocol_mismatch")} (ответ: {(resp.HasValue ? resp.Value.GetRawText() : "нет")})");
        if (joined) SendLine(s, "{\"type\":\"LEAVE_ROOM\"}");
    }
    SendLine(s, "{\"type\":\"PING\",\"t\":5}");
    Check(ReadType(r, Protocol.Pong).HasValue, "после всех вариантов соединение живо (PONG)");
    t.Close();
}
fresh?.Stop();

// ограничения: отдельный сервер с маленькими пределами (внешний — второй coop_server.py, его порт — 4-й аргумент)
int limPort = -1; CoopServer lim = null;
if (server != null)
{
    lim = new CoopServer { MaxConnectionsPerIp = 3, MaxConnections = 10, RoomOpsPerMinute = 4, JoinTimeoutSec = 2, Log = s => Console.WriteLine("    [lim] " + s) };
    lim.Start(0); limPort = lim.Port;
}
else if (args.Length > 3) limPort = int.Parse(args[3]);
if (limPort > 0)
{
    Console.WriteLine("16. защита сервера: соединений с адреса, входов в минуту, ожидание входа (пределы 3 / 4 / 2 с)");
    var held = new List<TcpClient>();
    for (int i = 0; i < 3; i++) { var (t, r, _) = Raw(host, limPort); held.Add(t); ReadType(r, Protocol.Welcome); }
    var (tx, rx, _) = Raw(host, limPort);
    var busy = ReadType(rx, Protocol.Error, Protocol.Welcome);
    Check(busy.HasValue && Protocol.Str(busy.Value, "code") == "server_busy", $"четвёртое соединение с адреса → server_busy ({(busy.HasValue ? busy.Value.GetRawText() : "нет ответа")})");
    Check(ClosedBy(rx, 3000), "лишнее соединение закрыто");
    tx.Close();
    // клиент мода на переполненном сервере: понятная причина, повтор позже
    var cb = new CoopClient { RetrySec = 0.5 };
    cb.Start(host, limPort, "Лишний");
    var swb = Stopwatch.StartNew();
    while (swb.ElapsedMilliseconds < 3000 && cb.LastError == null) Thread.Sleep(50);
    Check((cb.LastError ?? "").Contains("адреса"), $"клиент показывает причину отказа: «{cb.LastError}»");
    held[0].Close();
    // соединения без комнаты закрываются через JoinTimeout (2 с; чистка раз в 2 с)
    Check(held.Skip(1).All(h => ClosedBy(new LineReader(h.GetStream()), 7000)), "соединения, не вошедшие в комнату, закрыты по таймауту");
    swb.Restart();
    while (swb.ElapsedMilliseconds < 6000 && !cb.Connected) Thread.Sleep(100);
    Check(cb.Connected, "место освободилось — клиент вошёл сам");
    cb.Stop();
    foreach (var h in held) h.Close();
    Thread.Sleep(300);
    // входов в минуту с адреса — 4 (один уже у клиента выше): дальше rate_limited и закрытие
    var (t1, r1, w1) = Raw(host, limPort);
    int okJoins = 0; JsonElement? rl = null;
    for (int i = 0; i < 6 && rl == null; i++)
    {
        SendLine(w1, "{\"type\":\"JOIN_ROOM\",\"roomCode\":\"\",\"name\":\"Частый\",\"protocol\":2}");
        var resp = ReadType(r1, Protocol.Error, Protocol.RoomJoined);
        if (resp.HasValue && Protocol.TypeOf(resp.Value) == Protocol.RoomJoined) okJoins++; else rl = resp ?? default(JsonElement);
    }
    Check(okJoins == 3 && rl.HasValue && rl.Value.ValueKind == JsonValueKind.Object && Protocol.Str(rl.Value, "code") == "rate_limited",
          $"пятый вход за минуту → rate_limited (до него прошло {okJoins}, ответ {(rl.HasValue && rl.Value.ValueKind == JsonValueKind.Object ? rl.Value.GetRawText() : "нет")})");
    Check(ClosedBy(r1, 3000), "после rate_limited соединение закрыто");
    t1.Close();
    lim?.Stop();
}
else Console.WriteLine("16. защита сервера: пропущено (нет порта сервера с пределами)");

if (server != null)
{
    Console.WriteLine("17. сервер в игре хоста: гость раньше хоста не создаёт комнату и не занимает слот 1");
    var hs = new CoopServer { AutoCreateRoom = false }; hs.Start(0);
    var early = new CoopClient { RetrySec = 0.3 }; var be = new List<JsonElement>();
    early.Start("127.0.0.1", hs.Port, "Ранний гость", clientId: "early");
    var nf = WaitFor(early, Protocol.Error, be, 3000);
    Check(nf.HasValue && Protocol.Str(nf.Value, "code") == "room_not_found", "гость получил «хост ещё не создал игру»");
    Check(!early.Connected, "гость не в комнате");
    var hc = new CoopClient(); var bh = new List<JsonElement>();
    hc.Start("127.0.0.1", hs.Port, "Хост", create: true, clientId: "host-17");
    var hcr = WaitFor(hc, Protocol.RoomCreated, bh);
    var ej = WaitFor(early, Protocol.RoomJoined, be, 5000);
    Check(hcr.HasValue && Protocol.Num(hcr.Value, "slot") == 1, "хост — слот 1");
    Check(ej.HasValue && Protocol.Num(ej.Value, "slot") == 2 && Protocol.Str(ej.Value, "roomCode") == Protocol.Str(hcr.Value, "roomCode"), "гость сам переподключился и вошёл в комнату хоста, слот 2");
    early.Stop(); hc.Stop(); hs.Stop();

    Console.WriteLine("18. быстрые Start/Stop: без лишних соединений и потоков, после — всё работает");
    var ss = new CoopServer(); ss.Start(0);
    var peer = new CoopClient(); var bp = new List<JsonElement>();
    peer.Start("127.0.0.1", ss.Port, "Друг", clientId: "peer-18");
    WaitFor(peer, Protocol.RoomJoined, bp);
    var cl = new CoopClient { RetrySec = 0.2 }; var bc = new List<JsonElement>();
    var rnd = new Random(18);
    for (int i = 0; i < 40; i++)
    {
        cl.Start("127.0.0.1", ss.Port, "Дёрганый", clientId: "jumpy-18");
        if (i % 5 == 0) WaitFor(cl, Protocol.RoomJoined, bc, 2000); else Thread.Sleep(rnd.Next(0, 25));
        cl.Stop();
    }
    var sws = Stopwatch.StartNew();
    while (sws.ElapsedMilliseconds < 3000 && (ss.ConnectionCount > 1 || CoopClient.LiveThreads > 2)) Thread.Sleep(50);
    Check(CoopClient.LiveThreads == 2, $"после 40 Start/Stop живы только потоки друга: {CoopClient.LiveThreads} (ждём 2)");
    Check(ss.ConnectionCount == 1, $"на сервере только соединение друга: {ss.ConnectionCount}");
    while (cl.Incoming.TryDequeue(out _)) { }
    bc.Clear();
    cl.Start("127.0.0.1", ss.Port, "Дёрганый", clientId: "jumpy-18");
    var fin = WaitFor(cl, Protocol.RoomJoined, bc);
    Check(fin.HasValue && fin.Value.GetProperty("players").GetArrayLength() == 2, "последний Start вошёл в комнату, в ней два игрока — без двойников");
    Check(ss.ConnectionCount == 2, $"соединений на сервере ровно два: {ss.ConnectionCount}");
    bp.Clear(); Drain(peer);
    cl.SendState("{\"x\":18}");
    var st18 = WaitFor(peer, Protocol.RemotePlayerState, bp);
    Check(st18.HasValue && Protocol.Num(Protocol.Obj(st18.Value, "state"), "x") == 18, "состояние доходит");
    cl.SendEvent("{\"kind\":\"sos\"}");
    Check(WaitFor(peer, Protocol.RemoteEvent, bp).HasValue, "события доходят");
    Thread.Sleep(2300);
    Check(cl.PingMs >= 0, $"пинг измерен: {cl.PingMs} мс");
    cl.Stop(); peer.Stop(); ss.Stop();
    sws.Restart();
    while (sws.ElapsedMilliseconds < 3000 && CoopClient.LiveThreads > 0) Thread.Sleep(50);
    Check(CoopClient.LiveThreads == 0, $"после Stop потоков клиентов не осталось: {CoopClient.LiveThreads}");
}

if (server != null)
{
    Console.WriteLine("19. счётчик входов: адреса без запросов забываются, число адресов ограничено, предел работает");
    var rl19 = new CoopServer { RoomOpsPerMinute = 2, RoomOpsWindowSec = 1, MaxTrackedIps = 100 };
    rl19.Start(0);
    Check(rl19.RoomOpAllowedForTest("10.0.0.1") && rl19.RoomOpAllowedForTest("10.0.0.1") && !rl19.RoomOpAllowedForTest("10.0.0.1"), "третий вход за окно с адреса → отказ");
    for (int i = 0; i < 5000; i++) rl19.RoomOpAllowedForTest("10.1." + (i / 250) + "." + (i % 250));
    Check(rl19.RoomOpsTracked <= 100, $"5000 разных адресов → в счётчике {rl19.RoomOpsTracked} (предел 100)");
    rl19.RoomOpAllowedForTest("10.9.9.9"); rl19.RoomOpAllowedForTest("10.9.9.9");
    for (int i = 0; i < 150; i++) { rl19.RoomOpAllowedForTest("10.2.0." + i); rl19.RoomOpAllowedForTest("10.9.9.9"); }
    Check(!rl19.RoomOpAllowedForTest("10.9.9.9"), "адрес, упёршийся в предел и стучащийся дальше, при переполнении счётчика не сбрасывается");
    var sw19 = Stopwatch.StartNew();
    while (sw19.ElapsedMilliseconds < 6000 && rl19.RoomOpsTracked > 0) Thread.Sleep(100);
    Check(rl19.RoomOpsTracked == 0, $"без новых запросов адреса забыты по таймеру через {sw19.ElapsedMilliseconds} мс (окно 1 с)");
    Check(rl19.RoomOpAllowedForTest("10.0.0.1"), "после окна адрес снова может входить");
    // по сети: один адрес, предел 2 за окно
    var (t19, r19, w19) = Raw("127.0.0.1", rl19.Port);
    int ok19 = 0; JsonElement? lim19 = null;
    for (int i = 0; i < 4 && lim19 == null; i++)
    {
        SendLine(w19, "{\"type\":\"JOIN_ROOM\",\"roomCode\":\"\",\"name\":\"Ч\",\"protocol\":2}");
        var resp = ReadType(r19, Protocol.Error, Protocol.RoomJoined);
        if (resp.HasValue && Protocol.TypeOf(resp.Value) == Protocol.RoomJoined) ok19++; else lim19 = resp ?? default(JsonElement);
    }
    Check(ok19 == 2 && lim19.HasValue && lim19.Value.ValueKind == JsonValueKind.Object && Protocol.Str(lim19.Value, "code") == "rate_limited", $"по сети предел соблюдается (прошло {ok19} из 2, затем rate_limited)");
    t19.Close(); rl19.Stop();

    Console.WriteLine("20. гонка: состояние пересылается во время повторного входа того же игрока");
    var rc = new CoopServer { MaxConnectionsPerIp = 100, MaxConnections = 200, RoomOpsPerMinute = 1000 }; rc.Start(0);
    var (tr, rr, wr) = Raw("127.0.0.1", rc.Port);
    SendLine(wr, "{\"type\":\"CREATE_ROOM\",\"name\":\"Хост\",\"clientId\":\"host-20\",\"protocol\":2}");
    var cr20 = ReadType(rr, Protocol.RoomCreated);
    string code20 = cr20.HasValue ? Protocol.Str(cr20.Value, "roomCode") : "";
    var (to, ro, wo) = Raw("127.0.0.1", rc.Port);                                   // игрок другой комнаты
    SendLine(wo, "{\"type\":\"CREATE_ROOM\",\"name\":\"Чужой\",\"clientId\":\"other-20\",\"protocol\":2}");
    ReadType(ro, Protocol.RoomCreated);
    int got20 = 0, zero20 = 0, other20 = 0;
    tr.GetStream().ReadTimeout = 1500; to.GetStream().ReadTimeout = 1500;
    var readers = new[]
    {
        new Thread(() => { string l; try { while ((l = rr.ReadLine()) != null) if (l.Contains(Protocol.RemotePlayerState)) { Interlocked.Increment(ref got20); if (l.Contains("\"slot\":0")) Interlocked.Increment(ref zero20); } } catch { } }) { IsBackground = true },
        new Thread(() => { string l; try { while ((l = ro.ReadLine()) != null) if (l.Contains(Protocol.RemotePlayerState)) Interlocked.Increment(ref other20); } catch { } }) { IsBackground = true },
    };
    foreach (var th in readers) th.Start();
    var spam = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("{\"type\":\"PLAYER_STATE\",\"state\":{\"x\":1}}\n", 300)));
    var olds = new List<TcpClient>();
    for (int i = 0; i < 40; i++)
    {
        var (ts, rsx, wsx) = Raw("127.0.0.1", rc.Port);
        olds.Add(ts);
        SendLine(wsx, "{\"type\":\"JOIN_ROOM\",\"roomCode\":\"" + code20 + "\",\"name\":\"Гость\",\"clientId\":\"guest-20\",\"protocol\":2}");
        ReadType(rsx, Protocol.RoomJoined);
        // поток состояний с этого соединения, а следующий виток тем временем входит заново с тем же clientId
        new Thread(() => { try { wsx.Write(spam, 0, spam.Length); } catch { } }) { IsBackground = true }.Start();
        Thread.Sleep(i % 3);
    }
    Thread.Sleep(800);
    foreach (var th in readers) th.Join(3000);
    Check(got20 > 0 && zero20 == 0, $"ни одно из {got20} пересланных состояний не ушло со слотом 0");
    Check(other20 == 0, $"игроку другой комнаты состояний не пришло ({other20})");
    foreach (var t in olds) t.Close();
    tr.Close(); to.Close(); rc.Stop();

    // то же окно гонки, но наверняка: повторный вход происходит ровно между снимком под замком и сборкой сообщения
    var rd = new CoopServer(); rd.Start(0);
    var (th1, rh1, wh1) = Raw("127.0.0.1", rd.Port);
    SendLine(wh1, "{\"type\":\"CREATE_ROOM\",\"name\":\"Хост\",\"clientId\":\"host-20b\",\"protocol\":2}");
    var crd = ReadType(rh1, Protocol.RoomCreated);
    string coded = crd.HasValue ? Protocol.Str(crd.Value, "roomCode") : "";
    var (tg1, rg1, wg1) = Raw("127.0.0.1", rd.Port);
    SendLine(wg1, "{\"type\":\"JOIN_ROOM\",\"roomCode\":\"" + coded + "\",\"name\":\"Гость\",\"clientId\":\"guest-20b\",\"protocol\":2}");
    var gj1 = ReadType(rg1, Protocol.RoomJoined);
    int gslot = gj1.HasValue ? (int)Protocol.Num(gj1.Value, "slot") : -1;
    TcpClient tg2 = null; JsonElement? gj2 = null; int fired = 0;
    rd.RelayHook = () =>
    {
        if (Interlocked.Exchange(ref fired, 1) != 0) return;
        LineReader rg2; NetworkStream wg2;
        (tg2, rg2, wg2) = Raw("127.0.0.1", rd.Port);
        SendLine(wg2, "{\"type\":\"JOIN_ROOM\",\"roomCode\":\"" + coded + "\",\"name\":\"Гость\",\"clientId\":\"guest-20b\",\"protocol\":2}");
        gj2 = ReadType(rg2, Protocol.RoomJoined);
    };
    SendLine(wg1, "{\"type\":\"PLAYER_STATE\",\"state\":{\"x\":7}}");
    var relayed = ReadType(rh1, Protocol.RemotePlayerState);
    Check(gj2.HasValue, "повторный вход случился посреди пересылки");
    Check(relayed.HasValue && Protocol.Num(relayed.Value, "slot") == gslot, $"состояние ушло со слотом из снимка {gslot}, а не 0 (вышло {(relayed.HasValue ? Protocol.Num(relayed.Value, "slot") : -1)})");
    th1.Close(); tg1.Close(); tg2?.Close(); rd.Stop();

    Console.WriteLine("21. игра долго не разбирает входящие: состояний не больше предела, события целы");
    var ps = new CoopServer(); ps.Start(0);
    var snd = new CoopClient(); var rcv = new CoopClient(); var b21 = new List<JsonElement>();
    snd.Start("127.0.0.1", ps.Port, "Отправитель", clientId: "s21");
    WaitFor(snd, Protocol.RoomJoined, b21);
    rcv.Start("127.0.0.1", ps.Port, "Тормоз", clientId: "r21");
    WaitFor(rcv, Protocol.RoomJoined, b21);
    Thread.Sleep(300); Drain(rcv);
    for (int i = 0; i < 300; i++)
    {
        snd.SendState("{\"x\":" + i + "}");
        if (i % 30 == 0) snd.SendEvent("{\"kind\":\"dmg\",\"n\":" + i + "}");
        Thread.Sleep(3);
    }
    snd.SendState("{\"x\":1000}");
    Thread.Sleep(1000);
    int queued = rcv.Incoming.Count;
    var all21 = Drain(rcv);
    var st21 = all21.Where(m => Protocol.TypeOf(m) == Protocol.RemotePlayerState).Select(m => Protocol.Num(Protocol.Obj(m, "state"), "x")).ToList();
    var ev21 = all21.Where(m => Protocol.TypeOf(m) == Protocol.RemoteEvent).Select(m => (int)Protocol.Num(Protocol.Obj(m, "event"), "n")).ToList();
    Check(queued <= Inbox.MaxStatesPerPlayer + 10 && st21.Count <= Inbox.MaxStatesPerPlayer && st21.Count > 0 && st21.Last() == 1000,
          $"в очереди {queued} сообщений; состояний {st21.Count} (предел {Inbox.MaxStatesPerPlayer}), последнее — самое свежее ({st21.LastOrDefault()})");
    Check(st21.Zip(st21.Skip(1), (p, q) => p < q).All(ok => ok), "оставшиеся состояния идут по порядку");
    Check(ev21.SequenceEqual(Enumerable.Range(0, 10).Select(i => i * 30)), $"все 10 событий дошли по порядку ({ev21.Count})");
    snd.Stop(); rcv.Stop(); ps.Stop();
}

Console.WriteLine("22. сервер не читает: очередь событий клиента ограничена, клиент переподключается");
{
    var stuck = new TcpListener(System.Net.IPAddress.Loopback, 0); stuck.Start();
    int sp = ((System.Net.IPEndPoint)stuck.LocalEndpoint).Port, accepts = 0;
    var keep = new List<TcpClient>();
    new Thread(() =>
    {
        try
        {
            while (true)
            {
                var cl = stuck.AcceptTcpClient(); Interlocked.Increment(ref accepts);
                cl.ReceiveBufferSize = 4096;
                lock (keep) keep.Add(cl);
                var hello = Encoding.UTF8.GetBytes("{\"type\":\"WELCOME\",\"protocol\":2,\"playerId\":\"p1\"}\n{\"type\":\"ROOM_JOINED\",\"roomCode\":\"AAAAAA\",\"playerId\":\"p1\",\"slot\":1,\"players\":[]}\n");
                cl.GetStream().Write(hello, 0, hello.Length);   // и больше ни байта не читаем
            }
        }
        catch { }
    }) { IsBackground = true }.Start();
    var sc = new CoopClient { RetrySec = 0.3 }; var b22 = new List<JsonElement>();
    sc.Start("127.0.0.1", sp, "Застрявший");
    WaitFor(sc, Protocol.RoomJoined, b22);
    var big = "{\"kind\":\"casts\",\"pad\":\"" + new string('x', 8000) + "\"}";
    int maxPending = 0, sent22 = 0; bool refused = false;
    var sw22 = Stopwatch.StartNew();
    while (sw22.ElapsedMilliseconds < 15000 && !refused)
    {
        if (sc.Connected) { if (sc.SendEvent(big)) sent22++; else refused = true; }
        maxPending = Math.Max(maxPending, sc.PendingEvents);
        if (sent22 % 50 == 0) Thread.Sleep(1);
    }
    Check(refused, $"очередь переполнилась и событие отклонено (принято {sent22}, ~{sent22 * 8 / 1024} МБ)");
    Check(maxPending <= CoopClient.MaxPendingEvents, $"в очереди не больше {CoopClient.MaxPendingEvents} (было до {maxPending})");
    sw22.Restart();
    while (sw22.ElapsedMilliseconds < 5000 && Volatile.Read(ref accepts) < 2) Thread.Sleep(50);
    Check(Volatile.Read(ref accepts) >= 2, $"клиент переподключился ({accepts} подключений): «{sc.LastError}»");
    Check(sc.PendingEvents < CoopClient.MaxPendingEvents, $"старая очередь сброшена ({sc.PendingEvents})");
    sc.Stop(); stuck.Stop(); lock (keep) foreach (var k in keep) k.Close();
}

server?.Stop();
Console.WriteLine($"\nИТОГ: {passes} PASS, {fails} FAIL");
return fails == 0 ? 0 : 1;

// ---- сырые соединения для проверок протокола
static (TcpClient, LineReader, NetworkStream) Raw(string h, int p)
{
    var t = new TcpClient(h, p) { NoDelay = true };
    var s = t.GetStream(); s.ReadTimeout = 4000;
    return (t, new LineReader(s), s);
}
static void SendLine(NetworkStream s, string line) { var b = Encoding.UTF8.GetBytes(line + "\n"); try { s.Write(b, 0, b.Length); } catch { } }
/// <summary>Следующее сообщение одного из типов (остальные пропускаются); null — соединение закрыто или тишина 4 с.</summary>
static JsonElement? ReadType(LineReader r, params string[] types) => ReadWhere(r, el => types.Contains(Protocol.TypeOf(el)));
static JsonElement? ReadWhere(LineReader r, Func<JsonElement, bool> want)
{
    try
    {
        string line;
        while ((line = r.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            JsonElement el;
            try { using var doc = JsonDocument.Parse(line); el = doc.RootElement.Clone(); } catch (JsonException) { continue; }
            if (want(el)) return el;
        }
    }
    catch { }
    return null;
}
/// <summary>Сервер закрыл соединение за ms (всё, что было до закрытия, пропускается).</summary>
static bool ClosedBy(LineReader r, int ms)
{
    var sw = Stopwatch.StartNew();
    try { while (sw.ElapsedMilliseconds < ms) { if (r.ReadLine() == null) return true; } }
    catch (System.IO.IOException e) when (e.InnerException is SocketException se && se.SocketErrorCode == SocketError.TimedOut) { return false; }
    catch { return true; }
    return false;
}

/// <summary>Поток, который отдаёт данные кусками не больше chunk байт — как TCP, дробящий сообщения.</summary>
sealed class Chunked : System.IO.Stream
{
    private readonly System.IO.Stream _s; private readonly int _chunk;
    public Chunked(System.IO.Stream s, int chunk) { _s = s; _chunk = chunk; }
    public override int Read(byte[] buffer, int offset, int count) => _s.Read(buffer, offset, Math.Min(count, _chunk));
    public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { } public override long Seek(long o, System.IO.SeekOrigin so) => throw new NotSupportedException();
    public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
}

/// <summary>TCP-посредник между клиентом и сервером: Cut() рвёт все текущие соединения, как обрыв мобильной сети.</summary>
sealed class Proxy : IDisposable
{
    private readonly TcpListener _l = new TcpListener(System.Net.IPAddress.Loopback, 0);
    private readonly List<TcpClient> _open = new List<TcpClient>();
    private volatile bool _stop;
    public int Port { get; }
    public Proxy(string host, int port)
    {
        _l.Start(); Port = ((System.Net.IPEndPoint)_l.LocalEndpoint).Port;
        new Thread(() =>
        {
            while (!_stop)
            {
                TcpClient a;
                try { a = _l.AcceptTcpClient(); } catch { return; }
                try
                {
                    var b = new TcpClient(host, port) { NoDelay = true };
                    lock (_open) { _open.Add(a); _open.Add(b); }
                    Pipe(a, b); Pipe(b, a);
                }
                catch { a.Close(); }
            }
        }) { IsBackground = true }.Start();
    }
    private static void Pipe(TcpClient from, TcpClient to) => new Thread(() =>
    {
        var buf = new byte[8192];
        try { int n; var fs = from.GetStream(); var ts = to.GetStream(); while ((n = fs.Read(buf, 0, buf.Length)) > 0) ts.Write(buf, 0, n); } catch { }
        try { from.Close(); } catch { } try { to.Close(); } catch { }
    }) { IsBackground = true }.Start();
    public void Cut() { lock (_open) { foreach (var c in _open) try { c.Close(); } catch { } _open.Clear(); } }
    public void Dispose() { _stop = true; try { _l.Stop(); } catch { } Cut(); }
}
