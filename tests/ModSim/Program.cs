using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using ModKit;
using UnityEngine;

// Имитация игры: два настоящих экземпляра мода «Кооператив» (хост и друг) в одном процессе.
// Unity здесь нет, поэтому вызовы интерфейса падают и ловятся модом — проверяется сеть и игровая логика.
// Windows: шаг системного таймера ~15,6 мс — Sleep(16) длился бы ~31 мс, а на Linux (CI) — 16. Точный таймер — чтобы тесты
// шли одинаково везде (1.5.11 упал на GitHub, а на ПК проходил).
if (OperatingSystem.IsWindows()) WinTimer.timeBeginPeriod(1);
int passes = 0, fails = 0;
void Check(bool ok, string what) { if (ok) { passes++; Console.WriteLine("  PASS " + what); } else { fails++; Console.WriteLine("  FAIL " + what); } }

var A = new Player("Хост"); var B = new Player("Друг");
void TickAll(int ms) { for (int t = 0; t < ms / 50; t++) { A.Tick(); B.Tick(); Thread.Sleep(50); } }

Console.WriteLine("1. порт 28766 занят другой программой: хост берёт соседний, друг находит его сам");
var blocker = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 28766); blocker.Start();
A.Call("Host"); TickAll(500);
Check(A.Get("_hostPort") is int hp && hp == 28767, $"хост создал игру на порту 28767 (вышло {A.Get("_hostPort")})");
Check(((string)A.Get("_notice") ?? "").Contains("28767"), "в окне хоста написано, что порт сменён");
B.Call("Join");
TickAll(6500);   // на 28766 ответила чужая программа: клиент ждёт до 4 с, понимает это и идёт на 28767
Check(A.Partners().Count == 1 && B.Partners().Count == 1, "каждый видит одного друга");
Check(A.Toasts.Any(t => t.Contains("присоединился")), "хосту показано «Друг присоединился»");
Check(B.Toasts.Any(t => t.StartsWith("Подключено")), "другу показано «Подключено»");
blocker.Stop();
var C = new Player("Третий"); C.Ctx.Set["port"] = 28900;
var blk = new List<System.Net.Sockets.TcpListener>();
for (int pp = 28900; pp <= 28904; pp++) { var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, pp); l.Start(); blk.Add(l); }
C.Call("SafeActionHostNoWindow");
Check(((string)C.Get("_lastError") ?? "").Contains("заняты"), $"все порты заняты — причина показана в окне: «{C.Get("_lastError")}»");
blk.ForEach(l => l.Stop()); C.Disable();

Console.WriteLine("2. бой: друг появляется союзником и повторяет движение");
A.Spells.InBattle = B.Spells.InBattle = true;
A.Emit("battleStart"); B.Emit("battleStart");
A.Combat.Pos = new Vector3(5, 5, 0); B.Combat.Pos = new Vector3(100, 100, 0);   // у миров разные начала координат
B.Spells.Levels[2] = 3; B.Spells.Levels[7] = 5;
TickAll(800);
B.Combat.Pos = new Vector3(103, 104, 0);
TickAll(800);
var g = A.Comp.Spawned.LastOrDefault(c => c.Alive);
Check(g != null, "у хоста создан союзник-друг");
Check(g != null && Math.Abs(g.Offset.x - 3) < 0.05 && Math.Abs(g.Offset.y - 4) < 0.05, $"смещение союзника = движению друга от старта (3, 4): ({g?.Offset.x}, {g?.Offset.y})");
Check(g != null && g.Spell == 99, $"друг пересылает магию — союзник сам не стреляет, только повторяет его касты (заклинание 99): {g?.Spell}");
Check(g != null && g.Speed == 200, "библиотека ставит союзника сразу в сглаженную точку (скорость 200)");
A.Combat.Pos = new Vector3(6, 5, 0);
TickAll(400);
Check(g != null && Math.Abs(g.Offset.x - 2) < 0.05 && Math.Abs(g.Offset.y - 4) < 0.05, $"хост сдвинулся — смещение пересчитано (2, 4): ({g?.Offset.x}, {g?.Offset.y})");
Console.WriteLine("2б. плавность: друг идёт ровно 4 ед/с, точки приходят 10 раз в секунду");
var trace = new List<double>();
for (int step = 0; step < 40; step++)          // 2 с: друг идёт вправо, хост стоит
{
    B.Combat.Pos = new Vector3(103 + step * 0.2f, 104, 0);   // 0,2 ед за 50 мс = 4 ед/с
    A.Tick(); B.Tick(); Thread.Sleep(50);
    trace.Add(g.Offset.x);
}
var deltas = trace.Skip(10).Zip(trace.Skip(11), (p0, p1) => p1 - p0).ToList();
double maxJump = deltas.Max(), stalls = deltas.Count(x => x < 0.02);
Console.WriteLine($"    шаги смещения за тик (после разгона): мин {deltas.Min():0.00}, макс {maxJump:0.00}, остановок {stalls}");
Check(maxJump < 0.5, $"нет рывков: самый большой шаг за 50 мс {maxJump:0.00} (без сглаживания был бы скачок до цели)");
Check(stalls <= 2, $"нет остановок между пакетами: {stalls} тиков без движения");
Check(Math.Abs(trace.Last() - Math.Min(6, Math.Sqrt(Math.Pow(2 + 7.8, 2) + 16)) * (2 + 7.8) / Math.Sqrt(Math.Pow(2 + 7.8, 2) + 16)) < 0.8 || trace.Last() > 4, $"союзник догнал друга: x = {trace.Last():0.00}");

Console.WriteLine("2г. мобильная сеть: пакеты приходят неравномерно (паузы до 0,3 с и пачками), кадры 60 в секунду");
{
    var rnd = new Random(7);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    double nextSend = 0, nextPump = 0, lastFrame = 0, x0 = 97;   // от -3 до +5 от старта друга: союзник всё время в пределах дальности 6
    var frameX = new List<double>(); var frameT = new List<double>();
    B.Combat.Pos = new Vector3((float)x0, 104, 0); TickAll(600);
    double t0 = sw.Elapsed.TotalSeconds;
    while (sw.Elapsed.TotalSeconds - t0 < 2.0)
    {
        double now = sw.Elapsed.TotalSeconds - t0;
        B.Combat.Pos = new Vector3((float)(x0 + 4 * now), 104, 0);        // друг идёт ровно 4 ед/с
        if (now >= nextSend) { B.Tick(); nextSend = now + 0.1; }            // друг отправляет 10 раз в секунду
        if (now >= nextPump) { A.Call("Pump"); nextPump = now + (rnd.NextDouble() < 0.3 ? 0.15 + rnd.NextDouble() * 0.15 : 0.02 + rnd.NextDouble() * 0.06); }
        if (now - lastFrame >= 1.0 / 60) { lastFrame = now; A.Call("FrameStep"); frameX.Add(g.Offset.x); frameT.Add(sw.Elapsed.TotalSeconds); }
        Thread.Sleep(2);
    }
    // после разгона: скорость союзника по кадрам (шаг / длина кадра) должна быть ровной, идеал — 4 ед/с.
    // Делим на реальную длину кадра: под нагрузкой кадры неравномерны, и сам шаг за кадр «гуляет» без вины мода.
    var speeds = new List<double>(); var steps = new List<double>();
    for (int i = 40; i + 1 < frameX.Count; i++)
    {
        double d = frameX[i + 1] - frameX[i], dtf = frameT[i + 1] - frameT[i];
        if (Math.Abs(d) > 1 || dtf < 0.005) continue;
        steps.Add(d); speeds.Add(d / dtf);
    }
    double mean = speeds.Average(), sd = Math.Sqrt(speeds.Average(v => (v - mean) * (v - mean)));
    int stops = steps.Count(d => d < 0.01), back = steps.Count(d => d < -0.005);
    Console.WriteLine($"    скорость союзника: средняя {mean:0.00} ед/с (друг идёт 4), разброс {sd:0.00} ({sd / mean * 100:0}%), остановок {stops}, откатов {back} (кадров {steps.Count})");
    Check(sd / mean < 0.15, $"движение ровное: скорость колеблется на {sd / mean * 100:0}% (меньше 15%)");
    Check(stops <= 2 && back == 0, $"без остановок и откатов: {stops} остановок, {back} откатов");
}

Console.WriteLine("2в. покадровое движение (хук C_Battle_Manager.Update)");
var before = g.Offset.x;
B.Combat.Pos = new Vector3(107, 104, 0);             // обычный шаг назад на ~4 ед (не телепорт)
B.Tick(); Thread.Sleep(150); A.Call("Pump");          // точка дошла до хоста, дальше двигают только кадры
var frames = new List<double>();
for (int fr = 0; fr < 30; fr++) { A.Call("FrameStep"); frames.Add(g.Offset.x); Thread.Sleep(16); }
var fd = frames.Zip(frames.Skip(1), (p0, p1) => Math.Abs(p1 - p0)).ToList();
Check(fd.Max() < 0.6 && fd.Count(x => x > 1e-4) > 5, $"по кадрам союзник двигается мелкими шагами (макс {fd.Max():0.00} за кадр)");

Console.WriteLine("2д. магия друга: каждый снаряд героя друга повторяется у хоста из союзника");
{
    B.Combat.Pos = new Vector3(102, 103, 0); TickAll(600);
    for (int fr = 0; fr < 10; fr++) { A.Call("FrameStep"); Thread.Sleep(16); }      // союзник встал на место
    var gp = g.Tr.position;
    int callsBefore = A.Game.Calls.Count;
    // друг кастует: снаряд 6 из своего героя в точку на (2, 1) от себя
    Vector3 bh = B.Combat.HeroTr.position;
    B.Game.Bullet_Create(B.Combat.HeroTr, 6, bh.x + 2, bh.y + 1, 0, 0.25f, 1f, 0f, 0f, 0f, 0f, 0f, 1f);
    // союзник хоста (у друга) стреляет сам — это НЕ должно уйти хосту
    B.Game.Bullet_Create(B.Comp.Spawned.Last().Tr, 7, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 1);
    for (int fr = 0; fr < 40; fr++) { B.Call("FrameStep"); B.Tick(); A.Tick(); A.Call("FrameStep"); Thread.Sleep(16); }
    var mirrored = A.Game.Calls.Skip(callsBefore).ToList();
    Check(mirrored.Count == 1 && mirrored[0].id == 6, $"у хоста повторён ровно один снаряд (тип 6): {string.Join(",", mirrored.Select(m => m.id))}");
    Check(mirrored.Count == 1 && ReferenceEquals(mirrored[0].src, g.Tr), "снаряд вылетел из союзника-друга");
    var gp2 = g.Tr.position;
    Check(mirrored.Count == 1 && Math.Abs(mirrored[0].x - (gp2.x + 2)) < 0.3 && Math.Abs(mirrored[0].y - (gp2.y + 1)) < 0.3, $"цель на том же месте относительно союзника (+2, +1): ({mirrored.FirstOrDefault().x - gp2.x:0.00}, {mirrored.FirstOrDefault().y - gp2.y:0.00})");
    Check(mirrored.Count == 1 && Math.Abs(mirrored[0].dr - 1f) < 1e-3, "урон по настройке 100%");
    int echo = B.Game.Calls.Count(c => c.id == 6 && !ReferenceEquals(c.src, B.Combat.HeroTr));
    Check(echo == 0, "повтор у хоста не вернулся другу эхом");

    // Дух друга (свой объект, не герой) стреляет — тоже должно дойти до хоста
    int cs = A.Game.Calls.Count;
    var spirit = new Transform { position = new Vector3(B.Combat.HeroTr.position.x + 1, B.Combat.HeroTr.position.y, 0) };
    bh = B.Combat.HeroTr.position;
    B.Game.Bullet_Create(spirit, 16, bh.x - 3, bh.y, 0, 0, 1f, 0f, 0f, 0f, 0f, 0f, 1f);
    for (int fr = 0; fr < 40; fr++) { B.Call("FrameStep"); B.Tick(); A.Tick(); A.Call("FrameStep"); Thread.Sleep(16); }
    var sp = A.Game.Calls.Skip(cs).ToList();
    Check(sp.Count == 1 && sp[0].id == 16, $"выстрел Духа друга (не из героя) повторён у хоста: {string.Join(",", sp.Select(x => x.id))}");

    // рядом с точкой цели у хоста стоит враг — снаряд должен полететь в него
    var gp3 = g.Tr.position;
    A.Combat.Enemies.Add(new StillEnemy { P = new Vector3(gp3.x + 2.8f, gp3.y + 1.6f, 0) });
    Thread.Sleep(300);                                                  // кэш позиций врагов обновляется 4 раза в секунду
    int cb = A.Game.Calls.Count;
    bh = B.Combat.HeroTr.position;
    B.Game.Bullet_Create(B.Combat.HeroTr, 6, bh.x + 2, bh.y + 1, 0, 0, 1f, 0f, 0f, 0f, 0f, 0f, 1f);
    for (int fr = 0; fr < 40; fr++) { B.Call("FrameStep"); B.Tick(); A.Tick(); A.Call("FrameStep"); Thread.Sleep(16); }
    var rt = A.Game.Calls.Skip(cb).ToList();
    var gp4 = g.Tr.position;
    Check(rt.Count == 1 && Math.Abs(rt[0].x - (gp3.x + 2.8f)) < 0.05 && Math.Abs(rt[0].y - (gp3.y + 1.6f)) < 0.05, $"снаряд друга наведён на врага рядом с целью: ({(rt.Count > 0 ? rt[0].x - gp3.x : 0):0.00}, {(rt.Count > 0 ? rt[0].y - gp3.y : 0):0.00})");
    A.Combat.Enemies.Clear();
}

Console.WriteLine("2е. Щит, Спутники и Духи друга видны у его союзника");
{
    var bg = B.Game; var bp = B.Combat.HeroTr.position;
    for (int i = 0; i < 3; i++) { bg.Array_IsSatelliteA_P[i] = true; }
    double ang0 = 0.7;
    bg.Array_Transform_ObjSatellitesA[0].position = new Vector3((float)(bp.x + Math.Cos(ang0) * 2.25), (float)(bp.y + Math.Sin(ang0) * 2.25), 0);
    for (int i = 1; i <= 2; i++) { bg.List_IsSpirit_P[i] = true; bg.List_ObjSpirit[i].activeSelf = true; bg.List_ObjSpirit[i].transform.position = new Vector3(bp.x + (i == 1 ? -0.4f : 0.4f), bp.y + 0.55f, 0); }
    bg.Obj_ElectricZone.activeSelf = true; bg.IsElectricZone_P = true;
    bg.IsShield_P = true;
    bg.Obj_Shield.transform.position = new Vector3(bp.x, bp.y + 0.03f, 0); bg.Obj_Shield.transform.localScale = new Vector3(1, 1, 1);
    bg.Obj_Shield.Sr.sprite = new Sprite { name = "Shield2_01" };                 // у друга щит есть, у хоста — нет: картинку найдём среди картинок игры
    Resources.TestAll.AddRange(new UnityEngine.Object[] { new Sprite { name = "Shield2_01" }, new Sprite { name = "Shield2_03" }, new Sprite { name = "Fireball" }, new Sprite { name = "ElectricZone1B" } });
    for (int fr = 0; fr < 30; fr++) { B.Call("FrameStep"); B.Tick(); A.Tick(); A.Call("FrameStep"); Thread.Sleep(16); }
    var on = CloneGo.Made.Where(c => c.activeSelf).ToList();
    Check(on.Count(c => c.Kind == "satA") == 3, $"у хоста 3 копии Спутников: {on.Count(c => c.Kind == "satA")}");
    Check(on.Count(c => c.Kind == "spirit") == 2, $"2 копии Духов: {on.Count(c => c.Kind == "spirit")}");
    Check(on.Count(c => c.Kind == "shield") == 1, "копия Щита включена");
    var gp = g.Tr.position;
    var sats = on.Where(c => c.Kind == "satA").Select(c => c.transform.position).ToList();
    Check(sats.All(q => Math.Abs(Math.Sqrt((q.x - gp.x) * (q.x - gp.x) + (q.y - gp.y) * (q.y - gp.y)) - 2.25) < 0.05), "Спутники по кругу 2,25 вокруг союзника");
    var a0 = sats.Select(q => Math.Atan2(q.y - gp.y, q.x - gp.x)).OrderBy(a => Math.Abs(a - ang0)).First();
    Check(Math.Abs(a0 - ang0) < 0.1, $"поворот круга как у друга: {a0:0.00} ≈ {ang0:0.00}");
    var sh = on.First(c => c.Kind == "shield").transform.position;
    Check(Math.Abs(sh.x - gp.x) < 0.01 && Math.Abs(sh.y - gp.y) < 0.05, "Щит на союзнике");
    var shc = on.First(c => c.Kind == "shield");
    var sps = on.Where(c => c.Kind == "spirit").Select(c => (c.transform.position.x - gp.x, c.transform.position.y - gp.y)).OrderBy(q => q.Item1).ToList();
    Check(sps.Count == 2 && Math.Abs(sps[0].Item1 + 0.4) < 0.05 && Math.Abs(sps[1].Item1 - 0.4) < 0.05 && sps.All(q => Math.Abs(q.Item2 - 0.55) < 0.05),
          $"Духи стоят там же, где у друга (над героем), а не крутятся по кругу: {string.Join(" ", sps.Select(q => $"({q.Item1:0.00};{q.Item2:0.00})"))}");
    var zc = on.FirstOrDefault(c => c.Kind == "zone");
    Check(zc != null && Math.Abs(zc.transform.position.x - gp.x) < 0.01 && Math.Abs(zc.transform.position.y - gp.y) < 0.01, "Электрозона друга — на союзнике");
    bg.Obj_ElectricZone.Sr.enabled = false; bg.Obj_ElectricZone.Sr.color = new Color(1, 1, 1, 0.4f); bg.Obj_ElectricZone.Sr.sprite = new Sprite { name = "ElectricZone1B" };
    for (int fr = 0; fr < 30; fr++) { B.Call("FrameStep"); B.Tick(); A.Tick(); A.Call("FrameStep"); Thread.Sleep(16); }
    Check(zc != null && !zc.Sr.enabled && Math.Abs(zc.Sr.color.a - 0.4f) < 0.01, $"копия зоны мигает вместе с зоной друга (рендер {zc?.Sr.enabled}, α {zc?.Sr.color.a:0.00})");
    Check(zc?.Sr.sprite?.name == "ElectricZone1B", $"и меняет кадр вместе с ней: «{zc?.Sr.sprite?.name}»");
    bg.Obj_ElectricZone.Sr.enabled = true;
    bg.IsMagicCircle_P = true; bg.Obj_MagicCircleMid.activeSelf = true; bg.Obj_MagicCircleMid.transform.localScale = new Vector3(1.4f, 1.4f, 1);
    for (int fr = 0; fr < 30; fr++) { B.Call("FrameStep"); B.Tick(); A.Tick(); A.Call("FrameStep"); Thread.Sleep(16); }
    var mc = CloneGo.Made.FirstOrDefault(c => c.Kind == "circle");
    Check(mc != null && mc.activeSelf && Math.Abs(mc.transform.position.x - g.Tr.position.x) < 0.01 && Math.Abs(mc.transform.localScale.x - 1.4f) < 0.01, "Маг. круг друга — копия на союзнике, того же размера");
    bg.IsMagicCircle_P = false; bg.Obj_MagicCircleMid.activeSelf = false;
    Check(shc.Sr.sprite?.name == "Shield2_01", $"у копии Щита картинка друга, хотя у хоста щита нет: «{shc.Sr.sprite?.name}»");
    Check(on.Where(c => c.Kind == "spirit").All(c => c.Sr.sprite?.name == "UnitSpirit1MotionA"), "у копий Духов своя картинка");
    bg.Obj_Shield.Sr.sprite = new Sprite { name = "Shield2_03" };                 // у друга сменился заряд щита
    for (int fr = 0; fr < 30; fr++) { B.Call("FrameStep"); B.Tick(); A.Tick(); A.Call("FrameStep"); Thread.Sleep(16); }
    Check(shc.Sr.sprite?.name == "Shield2_03", $"картинка копии Щита меняется вместе с другом: «{shc.Sr.sprite?.name}»");
    Check(Resources.TestScans == 1, $"поиск по картинкам игры — один раз: {Resources.TestScans}");
    Check(!CloneGo.Made.Any(c => c.Kind == "satB"), "круга B нет — копий B не создано");

    {   // спутники друга вращаются 2 рад/с — у хоста копии крутятся ровно, без рывков на каждом сообщении
        var sat0 = CloneGo.Made.First(c => c.Kind == "satA");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double Ang(Vector3 q, Vector3 c) => Math.Atan2(q.y - c.y, q.x - c.x);
        var steps = new List<(double da, double dt)>();
        double prevA = double.NaN, prevT = 0;
        for (int fr = 0; fr < 90; fr++)
        {
            double t = sw.Elapsed.TotalSeconds, a = ang0 + 2 * t;
            var hp0 = B.Combat.HeroTr.position;
            bg.Array_Transform_ObjSatellitesA[0].position = new Vector3((float)(hp0.x + Math.Cos(a) * 2.25), (float)(hp0.y + Math.Sin(a) * 2.25), 0);
            B.Call("FrameStep"); B.Tick(); A.Tick(); A.Call("FrameStep");
            double now = sw.Elapsed.TotalSeconds, cur = Ang(sat0.transform.position, g.Tr.position);
            if (fr > 30 && !double.IsNaN(prevA)) { double d = cur - prevA; while (d > Math.PI) d -= 2 * Math.PI; while (d < -Math.PI) d += 2 * Math.PI; steps.Add((d, now - prevT)); }
            prevA = cur; prevT = now;
            Thread.Sleep(16);
        }
        double worst = steps.Max(x => Math.Abs(x.da / Math.Max(1e-3, x.dt) - 2));
        double mean = steps.Sum(x => x.da) / steps.Sum(x => x.dt);
        Check(Math.Abs(mean - 2) < 0.3, $"копии Спутников крутятся со скоростью друга: {mean:0.00} рад/с");
        Check(worst < 1.2, $"вращение ровное, без рывков (наибольшее отклонение скорости за кадр {worst:0.00} рад/с)");
    }
    for (int i = 0; i < 3; i++) bg.Array_IsSatelliteA_P[i] = false;
    bg.IsShield_P = false;
    bg.Obj_ElectricZone.activeSelf = false;
    for (int fr = 0; fr < 30; fr++) { B.Call("FrameStep"); B.Tick(); A.Tick(); A.Call("FrameStep"); Thread.Sleep(16); }
    on = CloneGo.Made.Where(c => c.activeSelf).ToList();
    Check(on.Count(c => c.Kind == "satA") == 0 && on.Count(c => c.Kind == "shield") == 0 && on.Count(c => c.Kind == "spirit") == 2, "Спутники и Щит пропали у друга — копии выключены, Духи остались");
    Check(on.Count(c => c.Kind == "zone") == 0, "Электрозона пропала у друга — копия выключена");
    Check(CloneGo.Made.Count == 8, $"новых копий не создаётся (с копией Маг. круга): {CloneGo.Made.Count}");
}

Console.WriteLine("2ж. Армагеддон друга: взрыв виден у хоста (объекты менеджера боя)");
{
    var am = new FakeManager(); var bm = new FakeManager();
    A.Set("_battleMgr", am); B.Set("_battleMgr", bm);
    void Frames(int n) { for (int fr = 0; fr < n; fr++) { B.Call("FrameStep"); B.Tick(); A.Tick(); A.Call("FrameStep"); Thread.Sleep(16); } }
    Frames(5);
    bm.IsArmageddonContinue = true;                       // у друга начался взрыв
    Frames(20);
    Check(am.Obj_ArmageddonGround_Front.activeSelf && am.Obj_ArmageddonGround_Back.activeSelf, "у хоста включились фон и затемнение Армагеддона");
    Check(!bm.Obj_ArmageddonGround_Front.activeSelf, "у друга мод свои объекты не трогает (их включает игра)");
    var sw = System.Diagnostics.Stopwatch.StartNew(); bool flash = false, land = false;
    while (sw.Elapsed.TotalSeconds < 5.5) { Frames(1); flash |= am.Obj_Armageddon.activeSelf; land |= am.Obj_ArmageddonLand.activeSelf; }
    Check(flash && land, "вспышка и падение — по порядку разведки");
    Check(new[] { am.Obj_ArmageddonGround_Front, am.Obj_ArmageddonGround_Back, am.Obj_Armageddon, am.Obj_ArmageddonLand, am.Obj_ArmageddonGround_Destroy }.All(o => !o.activeSelf), "через 5 с всё выключено");
    Check((int)A.Get("_armaPlayed") == 1 && (int)B.Get("_armaSent") == 1, "один взрыв — одно сообщение и один повтор");
    bm.IsArmageddonContinue = false;
    void FramesFor(double sec) { var w = System.Diagnostics.Stopwatch.StartNew(); while (w.Elapsed.TotalSeconds < sec) Frames(1); }
    FramesFor(0.4);                                                       // флаг взрыва мод смотрит раз в 0,1 с: дать ему увидеть конец взрыва
                                                                          // (5 кадров на Linux — 80 мс, CI падал)
    am.IsArmageddonContinue = true; bm.IsArmageddonContinue = true; Frames(20);   // у хоста свой взрыв идёт — повтор не нужен
    Check((int)A.Get("_armaPlayed") == 1 && (int)A.Get("_armaSkipped") == 1, "свой взрыв идёт — взрыв друга пропущен");
    am.IsArmageddonContinue = bm.IsArmageddonContinue = false; Frames(5);
}

Console.WriteLine("2з. общие враги: у гостя — копии врагов хоста, свой спавн погашен");
{
    A.Ctx.Set["world"] = 1; B.Ctx.Set["world"] = 1;
    var am = new FakeManager { Owner = A }; var bm = new FakeManager { Owner = B };
    A.Set("_battleMgr", am); B.Set("_battleMgr", bm);
    void Frames(int n) { for (int fr = 0; fr < n; fr++) { A.Call("FrameStep"); A.Tick(); B.Tick(); B.Call("FrameStep"); Thread.Sleep(16); } }
    double asx = (double)A.Get("_startX"), asy = (double)A.Get("_startY"), bsx = (double)B.Get("_startX"), bsy = (double)B.Get("_startY");
    var own = new FakeEnemy(B, 1); own.Transform_ObjUnit.position = new Vector3((float)bsx + 3, (float)bsy, 0); B.Combat.Enemies.Add(own);
    var e6 = new FakeEnemy(A, 6); e6.Transform_ObjUnit.position = new Vector3((float)asx + 2, (float)asy + 1, 0);
    var e11 = new FakeEnemy(A, 11); e11.Transform_ObjUnit.position = new Vector3((float)asx - 4, (float)asy + 0.5f, 0);
    A.Combat.Enemies.AddRange(new IEnemy[] { e6, e11 });
    Frames(60);
    var pups = B.Combat.Enemies.OfType<FakeEnemy>().Where(e => e.Puppet).ToList();
    Check(own.DestroyedSimple && !B.Combat.Enemies.Contains(own), "свой враг гостя убран без награды (Unit_Destroy_Simple)");
    Check(bm.EnemyCreateNum == 0 && bm.EnemyCreateNum_Max == 0, "свой спавн гостя погашен (EnemyCreateNum = 0)");
    Check(pups.Count == 2 && pups.Select(e => e.UnitID).OrderBy(x => x).SequenceEqual(new[] { 6, 11 }), $"у гостя копии врагов хоста тех же типов: {string.Join(",", pups.Select(e => e.UnitID))}");
    Check(pups.All(e => !e.CoroutinesStopped && e.UnitApply_MoveSpeed == 0 && e.UnitBase_MoveSpeed == 0 && e.Rigidbody_ObjUnit.isKinematic),
          "у копий ИИ работает (анимация), но скорость нулевая, тело кинематическое");
    Check(pups.All(e => Math.Abs(e.Transform_ObjUnitSprite.localScale.x - 1.25f) < 0.01), "картинка копии — по размеру врага (1,25), как у обычного врага");
    Check(FakeManager.ApplyBossNum == 1, $"счётчик боссов после создания копий возвращён (гость держит 1 с начала боя): {FakeManager.ApplyBossNum}");
    FakeEnemy P6() => pups.First(e => e.UnitID == 6);
    var q = P6().Transform_ObjUnit.position;
    Check(Math.Abs(q.x - bsx - 2) < 0.05 && Math.Abs(q.y - bsy - 1) < 0.05, $"копия стоит относительно старта гостя там же, где враг хоста от старта хоста: ({q.x - bsx:0.00}; {q.y - bsy:0.00})");
    e6.Transform_ObjUnit.position = new Vector3((float)asx + 3, (float)asy - 1, 0);
    Frames(40);
    q = P6().Transform_ObjUnit.position;
    Check(Math.Abs(q.x - bsx - 3) < 0.05 && Math.Abs(q.y - bsy + 1) < 0.05, $"копия идёт за врагом хоста: ({q.x - bsx:0.00}; {q.y - bsy:0.00})");
    P6().Unit_Hp -= 7;                                                   // гость попал по копии (любым заклинанием)
    Frames(30);
    Check(Math.Abs(e6.DamageTaken - 7) < 0.01 && e6.Unit_IsLive, $"урон гостя по копии дошёл до врага хоста: {e6.DamageTaken:0.##}");
    Check(P6().Unit_Hp >= 1e5, "здоровье копии снова полное");
    A.Combat.Enemies.Remove(e6);
    Frames(40);
    Check(B.Combat.Kills == 1 && !P6().DestroyedSimple && !P6().Unit_IsLive, "враг пропал у хоста — копия убита уроном (гостю — убийство)");
    var p11 = pups.First(e => e.UnitID == 11);
    Check(p11.Unit_Hp >= 1e5, $"копии у гостя неуязвимы — умирают только вслед за хостом (здоровье {p11.Unit_Hp:0})");
    p11.Unit_IsLive = false; B.Combat.Enemies.Remove(p11);                // копия погибла у гостя в обход здоровья (Армагеддон)
    Frames(30);
    Check(!B.Combat.Enemies.OfType<FakeEnemy>().Any(e => e.Unit_IsLive && e.UnitID == 11), "погибшую у гостя копию сразу не пересоздаём — ждём хоста");
    var sw11 = System.Diagnostics.Stopwatch.StartNew();
    while (sw11.Elapsed.TotalSeconds < 2.5 && !B.Combat.Enemies.OfType<FakeEnemy>().Any(e => e.Unit_IsLive && e.UnitID == 11)) Frames(5);
    Check(B.Combat.Enemies.OfType<FakeEnemy>().Any(e => e.Unit_IsLive && e.UnitID == 11), $"у хоста враг жив и через 2 с — копия создана заново ({sw11.Elapsed.TotalSeconds:0.0} с)");
    var late = new FakeEnemy(B, 1); B.Combat.Enemies.Add(late); late.Fire();  // игра гостя всё же создала врага (новая волна)
    Frames(5);
    Check(late.DestroyedSimple, "новый свой враг гостя убран сразу (хук Start_Unit)");
    Frames(20);
    Check(A.Combat.Enemies.OfType<FakeEnemy>().Count(e => e.Puppet && e.UnitID == 1) == 1, "свой враг гостя (фиолетовая сфера и т.п.) создан у хоста — стал общим");
    Check(!A.Combat.Enemies.OfType<FakeEnemy>().Any(e => e.DestroyedSimple), "у хоста враги не трогаются");
    var e1 = new FakeEnemy(A, 1); A.Combat.Enemies.Add(e1);
    Frames(30);
    Check(B.Combat.Enemies.OfType<FakeEnemy>().Count(e => e.Puppet && e.Unit_IsLive && e.UnitID == 1) == 2, "новый враг хоста появился у гостя (рядом с копией врага из сферы гостя)");
    var buffed = new FakeEnemy(A, 16) { UnitCreateID = 3, ApplyCreateEffectID = 1, LineColorR = 0.95f, LineColorG = 0.05f, LineColorB = 0.12f };
    A.Combat.Enemies.Add(buffed); buffed.Fire(1, 0);                       // усиленный моб хоста: тип 16, varA 1, «переход», красная обводка
    Frames(30);
    var bc = B.Combat.Enemies.OfType<FakeEnemy>().FirstOrDefault(e => e.Puppet && e.UnitID == 16);
    Check(bc != null && bc.VarA == 1, $"копия усиленного моба создана с varA 1 (Start_Unit как у игры): {bc?.VarA}");
    Check(bc != null && bc.UnitCreateID == 3 && bc.ApplyCreateEffectID == 1 && Math.Abs(bc.LineColorR - 0.95f) < 0.01f,
          $"и как «переход» с красной обводкой: создание {bc?.UnitCreateID}, обводка {bc?.LineColorR}");
    Check(FakeManager.List_BattleUnitEnemy_Empty.Count < 30, $"пул боссов кончился — пополнен из обычного пула (статического; там осталось {FakeManager.List_BattleUnitEnemy_Empty.Count})");
    am.Time_Min = 2; am.Time_Sec = 30; am.EnemyLevelNum = 150; am.EnemyWaveID = 5;
    bm.Time_Min = 2; bm.Time_Sec = 21; bm.EnemyLevelNum = 141; bm.EnemyWaveID = 4;   // у гостя была своя пауза — отстал
    Frames(30);
    Check(bm.EnemyWaveID == 4, $"номер волны гостю не подставляется (EnemyWaveID 5 → 6 начинает босс-волну — она повторялась): {bm.EnemyWaveID}");
    am.Time_Min = 3; am.Time_Sec = 0; am.Battle_ClearWaveON = 1; am.EnemyLevelNum = 180;
    Frames(15);                                                          // состояние хоста с босс-волной дошло до гостя
    bm.Time_Min = 2; bm.Time_Sec = 50; bm.EnemyLevelNum = 170;
    Frames(30);
    Check(bm.EnemyLevelNum == 170, "во время босс-волны часы не трогаем");
    am.Battle_ClearWaveON = 0;
    {   // своя босс-волна гостя: его босс прячется (без ИИ, далеко); хост победил своего — босс гостя не убит
        am.Battle_ClearWaveON = 1; bm.Battle_ClearWaveON = 1;
        Frames(15);
        var boss = new FakeEnemy(B, 50); boss.Transform_ObjUnit.position = B.Combat.Pos; B.Combat.Enemies.Add(boss); boss.Fire();
        Frames(20);
        Check(!boss.DestroyedSimple && boss.Unit_IsLive && boss.CoroutinesStopped, "свой босс гостя не убран, а спрятан (без ИИ)");
        Check(Math.Abs(boss.Transform_ObjUnit.position.x - B.Combat.Pos.x) > 100, "и стоит далеко от героя");
        am.Battle_ClearWaveON = 0;                                          // хост победил своего босса
        Frames(30);
        Check(boss.Unit_IsLive && !boss.DestroyedSimple, "хост победил — свой босс гостя не убит (смерть закончила бы этап и положила обелиск): ждёт обелиска хоста");
        bm.Battle_ClearWaveON = 0;
    }
    B.Ctx.Set["world"] = 0;
    Frames(10);
    Check(!B.Combat.Enemies.OfType<FakeEnemy>().Any(e => e.Puppet && e.Unit_IsLive), "общие враги выключены — копии убраны");
    Check(bm.EnemyCreateNum == 2 && bm.EnemyCreateNum_Max == 300, $"и свой спавн гостя вернулся: {bm.EnemyCreateNum}/{bm.EnemyCreateNum_Min}/{bm.EnemyCreateNum_Max}");
    A.Ctx.Set["world"] = 0; A.Combat.Enemies.Clear(); B.Combat.Enemies.Clear();
}

Console.WriteLine("2и. общая пауза: друг выбирает умение — пауза и у хоста");
{
    A.Set("_battleStartSec", -1000.0); B.Set("_battleStartSec", -1000.0);   // старт боя давно — ожидание на старте не мешает
    var own = new HashSet<ModContext>();
    var sess = typeof(MagicSurvivalCoop.CoopMod).Assembly.GetType("MagicSurvivalCoop.Session");
    sess.GetField("OwnPauseOverride", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, (Func<ModContext, bool>)(c => own.Contains(c)));
    void Frames(int n) { for (int fr = 0; fr < n; fr++) { A.Call("FrameStep"); A.Tick(); B.Tick(); B.Call("FrameStep"); Thread.Sleep(16); } }
    Time.timeScale = 1;
    A.Set("_battleMgr", new FakeManager { Owner = A }); B.Set("_battleMgr", new FakeManager { Owner = B });   // часы равны — ждать некому
    Frames(10);
    own.Add(B.Ctx); Time.timeScale = 0;                                   // друг открыл окно выбора умения — игра друга встала
    Frames(20);
    Check(A.Toasts.Any(t => t.Contains("выбирает умение")) && (bool)A.Get("_pauseForced"), "у хоста — пауза и подпись «Друг выбирает умение»");
    Check(!(bool)B.Get("_pauseForced"), "у самого друга мод паузу не держит (иначе паузы держали бы друг друга)");
    Time.timeScale = 0.8f;                                                // где-то игра поменяла скорость — мод снова ставит 0
    Frames(5);
    Check(Time.timeScale == 0, "пока друг выбирает, скорость держится на 0");
    own.Remove(B.Ctx);                                                    // друг выбрал умение
    Frames(20);
    Check(!(bool)A.Get("_pauseForced") && Time.timeScale > 0.5f, $"друг выбрал — пауза снята, скорость вернулась: {Time.timeScale}");
    // друг вышел из игры прямо в окне выбора: состояние больше не приходит — пауза не должна висеть вечно (1.6.0 висела)
    own.Add(B.Ctx); Frames(20);
    Check((bool)A.Get("_pauseForced"), "друг снова выбирает — пауза");
    var swGone = System.Diagnostics.Stopwatch.StartNew();
    while (swGone.Elapsed.TotalSeconds < 4) { A.Call("FrameStep"); Thread.Sleep(16); }   // таймер хоста стоит (пауза), от друга ничего
    Check(!(bool)A.Get("_pauseForced") && Time.timeScale > 0.5f, "друг пропал (3 с без состояния) — пауза снята, хотя таймер мода стоял");
    own.Remove(B.Ctx); Frames(20);
    // хост погиб, пока стоял на паузе из-за друга: пауза снимается, скорость возвращается (иначе экран смерти не появится)
    own.Add(B.Ctx); Frames(20);
    Check((bool)A.Get("_pauseForced"), "пауза из-за друга");
    A.Hero.Hp = 0; Frames(5);
    Check(!(bool)A.Get("_pauseForced") && Time.timeScale > 0.5f, "свой герой погиб — пауза снята, время идёт (экран смерти)");
    A.Hero.Hp = 100; own.Remove(B.Ctx); Frames(20);
    // часы хоста впереди на 4 с (друг вошёл в бой позже) — хост ждёт
    var am2 = new FakeManager { Owner = A, Time_Min = 0, Time_Sec = 14 }; var bm2 = new FakeManager { Owner = B, Time_Min = 0, Time_Sec = 14 };
    A.Set("_battleMgr", am2); B.Set("_battleMgr", bm2);
    Time.timeScale = 1;
    Frames(15); bm2.Time_Sec = 11;                                       // часы были равны — друг отстал на 3 с
    Frames(20);
    Check(!(bool)A.Get("_pauseForced") && !(bool)B.Get("_pauseForced"), "часы хоста впереди на 3 с — никто не стоит на паузе");
    Check((bool)B.Get("_catchUp") && Math.Abs(Time.timeScale - 1.25f) < 0.01f, $"отставший друг ускоряется ×1,25: {Time.timeScale}");
    bm2.Time_Sec = 14; Frames(20);                                        // друг догнал
    Check(!(bool)B.Get("_catchUp") && Time.timeScale == 1, $"друг догнал — обычная скорость: {Time.timeScale}");
    bm2.Time_Sec = 11; Time.timeScale = 0.5f; Frames(20);                // игра друга сама замедлилась — ускоряем от её скорости
    Check(Math.Abs(Time.timeScale - 0.625f) < 0.01f, $"игра замедлена до 0,5 — ускорение ×1,25 от неё: {Time.timeScale}");
    bm2.Time_Sec = 14; Frames(20);
    Check(Math.Abs(Time.timeScale - 0.5f) < 0.01f, $"догнал — вернулась скорость игры 0,5: {Time.timeScale}");
    bm2.Time_Sec = 14; Time.timeScale = 1; Frames(10);
    {   // старт боя: хост вошёл в бой раньше на 5 с — ждёт друга («Ждём …»), потом продолжает
        var clk = (System.Diagnostics.Stopwatch)sess.GetField("Clock", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        A.Set("_battleStartSec", clk.Elapsed.TotalSeconds); A.Set("_startWaitSince", 0.0);
        var am3 = new FakeManager { Owner = A, Time_Sec = 8 }; var bm3 = new FakeManager { Owner = B, Time_Sec = 3 };
        A.Set("_battleMgr", am3); B.Set("_battleMgr", bm3); Time.timeScale = 1;
        Frames(20);
        Check((bool)A.Get("_pauseForced") && A.Toasts.Any(x => x.Contains("Ждём")), "старт: хост впереди на 5 с — ждёт друга («Ждём …»)");
        bm3.Time_Sec = 8; Frames(20);
        Check(!(bool)A.Get("_pauseForced") && Time.timeScale > 0.5f, "друг догнал — хост продолжил");
        A.Set("_battleStartSec", -1000.0);
    }
    {   // вход в бой: хост уже в бою, друг ещё в меню — хост на паузе, пока друг не войдёт
        var clk = (System.Diagnostics.Stopwatch)sess.GetField("Clock", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        B.Spells.InBattle = false; Frames(10);                               // состояние «не в бою» дошло до хоста
        A.Set("_battleStartSec", clk.Elapsed.TotalSeconds); A.Set("_entryWaitDone", false); A.Set("_entryDoneSec", 0.0);
        Time.timeScale = 1;
        Frames(20);
        Check((bool)A.Get("_pauseForced") && Time.timeScale == 0 && A.Toasts.Any(x => x.Contains("не в бою")), "вход: друг ещё не в бою — хост на паузе («Ждём … — ещё не в бою»)");
        B.Spells.InBattle = true; Frames(20);
        Check(!(bool)A.Get("_pauseForced") && Time.timeScale > 0.5f, "друг вошёл в бой — хост продолжил");
        B.Spells.InBattle = false; Frames(20);
        Check(!(bool)A.Get("_pauseForced"), "друг позже вышел в меню — за этот бой больше не ждём");
        B.Spells.InBattle = true; Frames(30);
        g = A.Comp.Spawned.LastOrDefault(c => c.Alive) ?? g;               // друг выходил из боя — союзник у хоста создан заново
        A.Set("_battleStartSec", -1000.0);
    }
    {   // после босса: у хоста бой кончился (поднял обелиск), у друга ещё идёт — хост ждёт
        // начало: у хоста босс-волна началась раньше — друг (часы ещё не 3:00) НЕ ждёт (1.6.14 вставал — взаимная блокировка)
        var am5 = new FakeManager { Owner = A, Time_Min = 3, Time_Sec = 0, Battle_ClearWaveON = 1 }; var bm5 = new FakeManager { Owner = B, Time_Min = 2, Time_Sec = 58 };
        A.Set("_battleMgr", am5); B.Set("_battleMgr", bm5); Time.timeScale = 1;
        Frames(20);
        Check(!(bool)B.Get("_pauseForced"), "у хоста босс-волна началась раньше — друг не ждёт (его часам надо дойти до 3:00)");
        var am4 = new FakeManager { Owner = A, Time_Min = 3, Time_Sec = 5, AreaChangeNum = 1 }; var bm4 = new FakeManager { Owner = B, Time_Min = 3, Time_Sec = 0, Battle_ClearWaveON = 1 };
        A.Set("_battleMgr", am4); B.Set("_battleMgr", bm4); Time.timeScale = 1;
        Frames(20);
        Check(!(bool)A.Get("_pauseForced"), "у друга ещё бой с боссом — хост не стоит на паузе (1.6.15 вставал, пока его обелиск лежал не поднятым)");
        bm4.Battle_ClearWaveON = 0; bm4.Time_Sec = 5; bm4.AreaChangeNum = 1; Frames(20);
        // большое расхождение (10 с, у друга была замедлена игра) — хост ждёт; разница 2 с — продолжает
        am4.Time_Sec = 30; bm4.Time_Sec = 20; Frames(20);
        Check((bool)A.Get("_pauseForced") && A.Toasts.Any(x => x.Contains("(часы)")), "часы хоста впереди на 10 с — хост ждёт");
        bm4.Time_Sec = 28; Frames(20);
        Check(!(bool)A.Get("_pauseForced"), "разница 2 с — хост продолжил (дальше догоняет ускорение)");
        Time.timeScale = 1;
    }
    sess.GetField("OwnPauseOverride", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, null);
    {   // своя пауза — по скорости игры (флаг окна выбора залипает в True, сундук флагов не трогает)
        var upd = sess.GetMethod("UpdateOwnPause", BindingFlags.NonPublic | BindingFlags.Instance);
        var own0 = sess.GetMethod("OwnPause", BindingFlags.NonPublic | BindingFlags.Instance);
        bool Own() => (bool)own0.Invoke(A.Session, null);
        A.Set("_pauseForced", false);
        upd.Invoke(A.Session, new object[] { 0f });
        Check(Own(), "игра встала сама (сундук, окно выбора) — своя пауза");
        upd.Invoke(A.Session, new object[] { 1f });
        Check(!Own(), "игра пошла — своей паузы нет (флаг окна может залипнуть — не смотрим на него)");
        upd.Invoke(A.Session, new object[] { 0f }); A.Set("_pauseForced", true);
        upd.Invoke(A.Session, new object[] { 0f });
        Check(Own(), "мод держит паузу из-за друга, а своё окно ещё открыто — своя пауза помнится");
        upd.Invoke(A.Session, new object[] { 1f });
        Check(!Own(), "игра попробовала продолжить (окно закрыли) — своей паузы больше нет");
        upd.Invoke(A.Session, new object[] { 0f });
        Check(!Own(), "пауза мода — не своя (иначе паузы держали бы друг друга)");
        A.Set("_pauseForced", false);
    }
    Time.timeScale = 1;
}

B.Combat.Pos = new Vector3(150, 100, 0);
TickAll(400);
Check(g != null && Math.Abs(Math.Sqrt(g.Offset.x * g.Offset.x + g.Offset.y * g.Offset.y) - 6) < 0.01, "друг убежал далеко — союзник держится на дальности 6");
var gB = B.Comp.Spawned.LastOrDefault(c => c.Alive);
Check(gB != null && gB.Spell == 99, "у друга тоже есть союзник-хост, тоже повторяет магию");

Console.WriteLine("3. мало здоровья: сигнал SOS и лечение");
B.Hero.Hp = 20;
TickAll(600);
Check(A.Toasts.Any(t => t.Contains("нужна помощь")), "хост получил «Друг: нужна помощь!»");
A.Call("HealPressed");
TickAll(600);
Check(B.Hero.Healed.Count == 1 && Math.Abs(B.Hero.Healed[0] - 25) < 0.01, $"друг вылечен на 25% от 100: {string.Join(",", B.Hero.Healed)}");
Check(B.Toasts.Any(t => t.Contains("лечит вас")), "другу показано «Хост лечит вас»");
A.Call("HealPressed");
TickAll(300);
Check(B.Hero.Healed.Count == 1, "повторное лечение во время перезарядки не проходит");

Console.WriteLine("4. смерть друга");
B.Hero.Hp = 0;
TickAll(600);
Check(A.Toasts.Any(t => t.Contains("погиб")), "хосту показано «Друг погиб!»");
Check(!g.Alive, "союзник погибшего друга убран");

Console.WriteLine("5. конец боя и выход");
A.Spells.InBattle = B.Spells.InBattle = false; A.Emit("battleEnd"); B.Emit("battleEnd");
TickAll(300);
Check(A.Comp.Spawned.All(c => !c.Alive), "после боя союзников нет");
Check(CloneGo.Made.All(c => c.DestroyedForTest), "после боя все копии Щита/Спутников/Духов удалены");
B.Call("Disconnect");
TickAll(1500);
Check(A.Partners().Count == 0, "друг вышел — хост это увидел");
Check(A.Toasts.Any(t => t.Contains("вышел")), "хосту показано «Друг вышел»");
A.Call("Disconnect");

Console.WriteLine("6. выключение мода");
A.Disable(); B.Disable();
Check(true, "моды выключены без исключений");

Console.WriteLine("7. выключение мода посреди боя (общая пауза, общие враги, ускорение) и повторное включение");
{
    var sess = typeof(MagicSurvivalCoop.CoopMod).Assembly.GetType("MagicSurvivalCoop.Session");
    var ownField = sess.GetField("OwnPauseOverride", BindingFlags.NonPublic | BindingFlags.Static);
    var own = new HashSet<ModContext>();
    ownField.SetValue(null, (Func<ModContext, bool>)(c => own.Contains(c)));
    var H = new Player("Хост7"); var G = new Player("Друг7");
    foreach (var p in new[] { H, G }) { p.Ctx.Set["port"] = 28820; p.Ctx.Set["world"] = 1; }
    void T(int ms) { for (int t = 0; t < ms / 50; t++) { H.Tick(); G.Tick(); Thread.Sleep(50); } }
    void F(int n) { for (int fr = 0; fr < n; fr++) { H.Call("FrameStep"); H.Tick(); G.Tick(); G.Call("FrameStep"); Thread.Sleep(16); } }
    G.Call("Join"); T(800);                                              // гость раньше хоста: ждёт, комнату не создаёт
    H.Call("Host");
    Check(((string)H.Get("_notice") ?? "").Contains("Создаю"), $"до ответа сервера «Игра создана» не показано: «{H.Get("_notice")}»");
    T(4000);
    Check(H.Get("_mySlot") is int hs && hs == 1 && G.Get("_mySlot") is int gs && gs == 2, $"хост — слот 1, гость, пришедший раньше, — слот 2 ({H.Get("_mySlot")}, {G.Get("_mySlot")})");
    Check(((string)H.Get("_notice") ?? "").Contains("Игра создана"), "после ROOM_CREATED — «Игра создана»");
    H.Spells.InBattle = G.Spells.InBattle = true; H.Emit("battleStart"); G.Emit("battleStart");
    H.Combat.Pos = new Vector3(0, 0, 0); G.Combat.Pos = new Vector3(50, 50, 0);
    var hm = new FakeManager { Owner = H }; var gm = new FakeManager { Owner = G };
    H.Set("_battleMgr", hm); G.Set("_battleMgr", gm);
    H.Set("_battleStartSec", -1000.0); G.Set("_battleStartSec", -1000.0);
    FakeManager.List_BattleUnitEnemyBoss_Empty = Enumerable.Range(0, 5).Select(_ => new object()).ToList();
    var he = new FakeEnemy(H, 6); he.Transform_ObjUnit.position = new Vector3(2, 1, 0); H.Combat.Enemies.Add(he);
    Time.timeScale = 1;
    T(400); F(60);
    var pups = G.Combat.Enemies.OfType<FakeEnemy>().Where(e => e.Puppet).ToList();
    Check((bool)G.Get("_worldActive") && gm.EnemyCreateNum == 0 && pups.Count == 1, $"у гостя общие враги: свой спавн погашен, копий {pups.Count}");
    {   // 1.6.31: перед боссом тот, кто впереди, ждёт — босс-волна начинается у всех вместе
        hm.Time_Min = 2; hm.Time_Sec = 55; gm.Time_Min = 2; gm.Time_Sec = 50; hm.Battle_ClearWaveON = gm.Battle_ClearWaveON = 0;
        F(30);
        Check((bool)H.Get("_pauseForced") && (string)H.Get("_pauseWhy") == "к боссу", $"хост на 2:55, гость на 2:50 — хост ждёт гостя перед боссом ({H.Get("_pauseWhy")})");
        gm.Time_Sec = 55; F(30);
        Check(!(bool)H.Get("_pauseForced"), "гость догнал — хост продолжает");
        Time.timeScale = 1;
    }
    {   // 1.6.45: первый босс — вступление гостя кончилось раньше, чем хост поднял обелиск: своего обелиска гостю игра не кладёт
        // (1.6.44 держал счётчик боссов только с первого обелиска хоста — на 3:00 обелиск ложился, и убрать его не выходило)
        FakeManager.ApplyBossNum = 0;
        F(10);
        Check(FakeManager.ApplyBossNum == 1, "общие враги: счётчик боссов гостя держится 1 ещё до первого босса");
        gm.BeginBoss(3); gm.EnforceStep = 0.2f;
        for (int k = 0; k < 20; k++) { gm.Step(); F(1); }
        Check(gm.BossWave_EnforceVar >= 0.999f && gm.Ends == 0 && gm.ApplyObeliskON == 0, $"вступление гостя на 3:00 кончилось до подбора хоста — обелиска у гостя нет (концов {gm.Ends}, обелиск {gm.ApplyObeliskON})");
        gm.Time_Min = 2; gm.Time_Sec = 55; gm.Battle_ClearWaveON = 0; gm.BossWave_VisionRatio = 0; gm.ApplyBossGround_Size = 0; gm.BossWave_EnforceVar = 1;
        gm.Obj_MonsterClearBar.SetActive(false); gm.Text_Time.color = new Color(1, 1, 1, 1);
        F(10);
    }
    {   // босс-волна (1.6.42): хост убил босса и поднял обелиск — у гостя сразу: волна снята, часы как у хоста (за порогом босса),
        // смена области, награда; конец его вступления не кладёт второй обелиск (ApplyBossNum держится 1)
        void FS(int n) { for (int k = 0; k < n; k++) { hm.Step(); gm.Step(); F(1); } }
        void Wait(double sec, Func<bool> until) { var sw = System.Diagnostics.Stopwatch.StartNew(); while (sw.Elapsed.TotalSeconds < sec && !until()) FS(5); }
        FakeManager.ApplyBossNum = 0;
        hm.BeginBoss(3); gm.BeginBoss(3);
        hm.EnforceStep = 0; gm.EnforceStep = 0.001f;                              // вступление гостя ещё долго (телефон: ~1 мин)
        FS(20);
        hm.Battle_ClearWaveON = 0; hm.AreaChangeNum = 1; hm.ApplyObeliskON = 1; hm.BossWave_VisionRatio = 0;   // хост убил босса
        Wait(3, () => false);
        Check(gm.Battle_ClearWaveON == 1 && gm.AreaChanges == 0, "хост убил босса, обелиск ещё не поднят — у гостя волна идёт, как и часы хоста стоят");
        float enforce = gm.BossWave_EnforceVar;
        hm.ApplyObeliskON = 0; hm.Time_Sec = 2;                                   // хост поднял обелиск, его часы пошли (3:02)
        Wait(5, () => gm.AreaChanges > 0);
        FS(10);
        Check(gm.Battle_ClearWaveON == 0 && gm.BossWave_VisionRatio == 0 && gm.ApplyBossGround_Size == 0, "хост поднял обелиск — у гостя сразу снята босс-волна (затемнение, арена)");
        Check(!gm.Obj_MonsterClearBar.activeSelf && gm.Text_Time.color.g == 1, $"череп над часами убран, часы снова белые ({gm.Text_Time.color.r}, {gm.Text_Time.color.g}, {gm.Text_Time.color.b})");
        Check(gm.Clock >= 182 && gm.Clock <= 184, $"часы гостя — как у хоста, за порогом босса: {gm.Time_Min}:{gm.Time_Sec:00}");
        Check(gm.AreaChanges == 1 && gm.AreaChangeNum == 1 && gm.ApplyObeliskON == 0, $"одна смена области, счётчик как у хоста ({gm.AreaChangeNum}), обелиска нет");
        Check(gm.BossWave_EnforceVar >= enforce && FakeManager.ApplyBossNum == 1, $"вступление не начато заново ({enforce:0.00} → {gm.BossWave_EnforceVar:0.00}), счётчик боссов держится 1");
        gm.EnforceStep = 0.05f;
        Wait(3, () => gm.BossWave_EnforceVar >= 0.999f);
        FakeManager.ApplyBossNum = 0;                                             // игра сбросила счётчик — мод вернёт 1
        FS(20);
        Check(FakeManager.ApplyBossNum == 1 && gm.Ends == 0 && gm.AreaChangeNum == 1 && gm.ApplyObeliskON == 0,
              $"вступление гостя досчиталось — второго конца волны и обелиска нет, счётчик боссов держится 1 (концов {gm.Ends}, смен {gm.AreaChangeNum})");

        // гость отстал и до босса не дошёл (2:58), хост уже поднял обелиск: перескочить порог — своей босс-волны не будет
        hm.BeginBoss(6); hm.Battle_ClearWaveON = 0; hm.AreaChangeNum = 2; hm.ApplyObeliskON = 1;
        gm.Time_Min = 5; gm.Time_Sec = 58; gm.BossWave_EnforceVar = 1;
        Wait(3, () => false);
        hm.ApplyObeliskON = 0; hm.Time_Sec = 1;
        Wait(5, () => gm.AreaChanges > 1);
        FS(10);
        Check(gm.Clock >= 361 && gm.AreaChangeNum == 2 && gm.Battle_ClearWaveON == 0 && gm.Ends == 0,
              $"гость до босса не дошёл — часы за порог ({gm.Time_Min}:{gm.Time_Sec:00}), смена области как у хоста ({gm.AreaChangeNum})");
    }
    own.Add(H.Ctx); Time.timeScale = 0;                                  // хост выбирает умение — у гостя общая пауза
    F(20);
    Check((bool)G.Get("_pauseForced"), "у гостя пауза из-за хоста");
    G.Disable();
    Check(Time.timeScale == 1, $"выключение у гостя: скорость игры вернулась ({Time.timeScale})");
    Check(!(bool)G.Get("_pauseForced"), "общая пауза снята");
    Check(gm.EnemyCreateNum == 2 && gm.EnemyCreateNum_Max == 300, $"свой спавн гостя возвращён ({gm.EnemyCreateNum}, {gm.EnemyCreateNum_Max})");
    Check(pups.All(e => e.DestroyedSimple), "копии врагов хоста убраны");
    Check(!(bool)G.Get("_worldActive"), "общие враги выключены");
    bool again = true;
    try { G.Disable(); G.Tick(); G.Call("FrameStep"); } catch (Exception ex) { again = false; Console.WriteLine("    " + ex); }
    Check(again && gm.EnemyCreateNum == 2, "повторное выключение и запоздалые вызовы таймера и кадра — без исключений и без изменений игры");
    own.Clear();
    H.Set("_catchUp", true); H.Set("_gameScale", 1f); H.Set("_catchSet", 1.25f); Time.timeScale = 1.25f;   // хост догонял часы ускорением
    H.Disable();
    Check(Time.timeScale == 1, $"выключение у хоста: ускорение снято ({Time.timeScale})");
    var H2 = new Player("Хост7б"); var G2 = new Player("Друг7б");
    foreach (var p in new[] { H2, G2 }) p.Ctx.Set["port"] = 28820;
    H2.Call("Host"); for (int t = 0; t < 10; t++) { H2.Tick(); G2.Tick(); Thread.Sleep(50); }
    G2.Call("Join"); for (int t = 0; t < 40; t++) { H2.Tick(); G2.Tick(); Thread.Sleep(50); }
    Check(H2.Partners().Count == 1 && G2.Partners().Count == 1, "после повторного включения новая игра на том же порту работает");
    H2.Disable(); G2.Disable();
    ownField.SetValue(null, null);
}
Console.WriteLine($"\nОшибки, пойманные Guard (кроме интерфейса Unity): {string.Join("; ", A.Errors.Concat(B.Errors).Distinct().Take(8))}");
Console.WriteLine("8. версии библиотек ModKit: mod.json и Define() совпадают, несовместимая 2.x не запускается");
{
    var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "mod", "coop", "mod.json"))) dir = dir.Parent;
    using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(dir.FullName, "mod", "coop", "mod.json")));
    var json = doc.RootElement.GetProperty("depends").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString());
    var def = new MagicSurvivalCoop.CoopMod().Define();
    Check(json.Count == def.Depends.Count && json.All(kv => def.Depends.TryGetValue(kv.Key, out var v) && v == kv.Value),
          $"depends в mod.json (их берёт ModKit) = Define(): {string.Join(", ", json.Select(kv => kv.Key + " " + kv.Value))}");
    var range = MagicSurvivalCoop.CoopMod.LibRange;
    Check(ModKit.Core.Satisfies("1.0", range) && ModKit.Core.Satisfies("1.9.3", range), $"библиотеки 1.0 (MS_KIT 2.0) и 1.x подходят под «{range}»");
    Check(!ModKit.Core.Satisfies("2.0", range) && !ModKit.Core.Satisfies("0.9", range) && !ModKit.Core.Satisfies(null, range), "2.0, 0.9 и «без версии» — не подходят (ModKit не запустит мод и напишет причину)");
}

Console.WriteLine("9. ошибки обработчиков хуков: в журнал ModKit — первые 3, дальше только счёт (иначе ModKit выключил бы мод)");
{
    var P = new Player("Ошибки");
    var sess = P.Session;
    var hookM = sess.GetType().GetMethod("Hook", BindingFlags.NonPublic | BindingFlags.Instance);
    var h = (Action<object, object[]>)hookM.Invoke(sess, new object[] { "проверка", (Action<object, object[]>)((s, a) => throw new InvalidOperationException("сбой")) });
    int gf0 = P.Ctx.GuardFails;
    bool leaked = false;
    for (int i = 0; i < 10; i++) try { h(null, null); } catch { leaked = true; }
    var errs = (System.Collections.Generic.Dictionary<string, int>)P.Get("_hookErrors");
    Check(!leaked, "исключение не выходит из обработчика");
    Check(P.Ctx.GuardFails - gf0 == 3, $"в ModKit (ctx.Guard) ушло {P.Ctx.GuardFails - gf0} ошибки из 10");
    Check(errs.TryGetValue("проверка", out var n) && n == 10, $"все 10 посчитаны для итога боя ({n})");
    var start = P.Ctx.Hooks.Keys.FirstOrDefault(k => k.Contains("Start_Unit"));
    Check(start != null, "хук Start_Unit (синхронизация спавна) поставлен через обёртку");
    P.Disable();
    gf0 = P.Ctx.GuardFails;
    h(null, null);
    Check(P.Ctx.GuardFails == gf0, "после выключения мода обработчик ничего не делает");
    Check(P.Ctx.Handlers.ContainsKey("bossSpawn") && P.Ctx.Handlers.ContainsKey("areaChange") && !P.Ctx.Hooks.Keys.Any(k => k.Contains("Create_UnitEnemyBoss") || k.Contains("Start_AreaChange")),
          "журнал боссов и смены области — через события ModKit, без своих хуков");
}

Console.WriteLine($"ИТОГ: {passes} PASS, {fails} FAIL");
return fails == 0 ? 0 : 1;

// ============================================================ фейки

class Player
{
    public readonly FakeCtx Ctx; public readonly FakeSpells Spells = new(); public readonly FakeHero Hero = new(); public readonly FakeCombat Combat = new(); public readonly FakeComps Comp = new();
    public readonly GameHero Game;
    static readonly Dictionary<ModContext, Player> ByCtx = new();
    public List<string> Toasts => Ctx.Toasts; public List<string> Errors => Ctx.Errors;
    private readonly ModDef _def; private object _session;
    public Player(string name)
    {
        Ctx = new FakeCtx(name, this);
        Game = new GameHero(this); ByCtx[Ctx] = this;
        var sess = typeof(MagicSurvivalCoop.CoopMod).Assembly.GetType("MagicSurvivalCoop.Session");
        sess.GetField("HeroOverride", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, (Func<ModContext, object>)(c => ByCtx.TryGetValue(c, out var pl) ? pl.Game : null));
        _def = new MagicSurvivalCoop.CoopMod().Define();
        foreach (var o in _def.Options) Ctx.Set[o.Key] = o.Default;
        Ctx.Set["port"] = 28766; Ctx.Set["ip1"] = 127; Ctx.Set["ip2"] = 0; Ctx.Set["ip3"] = 0; Ctx.Set["ip4"] = 1;
        Ctx.StoreData["name"] = name;
        _def.OnEnable(Ctx);
        var mod = _def.OnEnable.Target;
        _session = mod.GetType().GetField("_session", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(mod);
    }
    public void Tick() { Ctx.GameTime += 0.05; foreach (var f in Ctx.Ticks.ToArray()) Ctx.Guard("tick", f); }
    public void Emit(string ev) { if (Ctx.Handlers.TryGetValue(ev, out var l)) foreach (var h in l) h(null); }
    public void Call(string method)
    {
        if (method == "SafeActionHostNoWindow") { Call("Host"); return; }
        _session.GetType().GetMethod(method, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Invoke(_session, null);
    }
    public void Set(string field, object v) => _session.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(_session, v);
    public object Session => _session;
    public object Get(string field) => _session.GetType().GetField(field, BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_session);
    public System.Collections.IDictionary Partners() => (System.Collections.IDictionary)_session.GetType().GetField("_partners", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(_session);
    public void Disable() { _def.OnDisable(Ctx); foreach (var c in Ctx.Cleanups) c(); }
}

class FakeCtx : ModContext
{
    public readonly string Name; private readonly Player _p;
    public readonly Dictionary<string, double> Set = new(); public readonly Dictionary<string, object> StoreData = new();
    public readonly List<Action> Ticks = new(), Cleanups = new(); public readonly Dictionary<string, List<Action<object>>> Handlers = new();
    public readonly List<string> Toasts = new(), Errors = new(); public double GameTime;
    public FakeCtx(string name, Player p) { Name = name; _p = p; }
    public string Id => "coop_" + Name;   // кэш ctx.Use в ModKit общий на процесс — в стенде два «телефона», поэтому id разные
    public IReadOnlyDictionary<string, double> Settings => Set;
    public double Setting(string key) => Set.TryGetValue(key, out var v) ? v : 0;
    public void SetSetting(string key, double value) => Set[key] = value;
    public void Log(string msg) { if (Environment.GetEnvironmentVariable("SIMLOG") != null) Console.WriteLine($"    [{Name}] {msg}"); }
    public void Warn(string msg) => Console.WriteLine($"    [{Name}] WARN {msg}");
    public ModEvents Events => new Ev(this);
    public void Expose(object api) { }
    public T Mod<T>(string id) where T : class
    {
        if (id == "modkit_combat" && typeof(T) == typeof(ILibrary<ICombat>)) return new Lib<ICombat>(_p.Combat) as T;
        object lib = id switch { "modkit_combat" => new Lib<IEnemies>(_p.Combat), "modkit_hero" => new Lib<IHero>(_p.Hero), "modkit_spells" => new Lib<ISpells>(_p.Spells), "modkit_companions" => new Lib<ICompanions>(_p.Comp), _ => null };
        return lib as T;
    }
    public void Replace(string c, string m, int n, Func<object, object[], object> f) { }
    public readonly Dictionary<string, List<Action<object, object[]>>> Hooks = new();
    public void After(string c, string m, int n, Action<object, object[]> f) => AddHook($"After {c}.{m}/{n}", f);
    public void Before(string c, string m, int n, Action<object, object[]> f) => AddHook($"Before {c}.{m}/{n}", f);
    void AddHook(string k, Action<object, object[]> f) { if (!Hooks.TryGetValue(k, out var l)) Hooks[k] = l = new(); l.Add(f); }
    public void Fire(string k, object self, object[] a) { if (Hooks.TryGetValue(k, out var l)) foreach (var h in l) h(self, a); }
    public void Every(double ms, Action fn) => Ticks.Add(fn);
    public void OnCleanup(Action fn) => Cleanups.Add(fn);
    public IRegistry<T> Registry<T>(string name) => null;
    public void Toast(string text, double sec = 3) { Toasts.Add(text); Console.WriteLine($"    [{Name}] экран: {text}"); }
    public byte[] ReadAsset(string path) => null;
    public IGameTimer Timer => new Tm(this);
    public IStore Store => new St(this);
    public ITags Tags => null;
    public int GuardFails;
    public void Guard(string what, Action fn) { try { fn(); } catch (Exception e) { GuardFails++; var m = $"{what}: {e.GetType().Name}: {e.Message}"; if (!Errors.Contains(m)) Errors.Add(m); } }
    public void Error(string msg) => Errors.Add(msg);
    public void Mark(string what) { }
    class Ev : ModEvents
    {
        private readonly FakeCtx _c; public Ev(FakeCtx c) { _c = c; }
        public void On(string name, Action<object> fn) { if (!_c.Handlers.TryGetValue(name, out var l)) _c.Handlers[name] = l = new(); l.Add(fn); }
        public void Emit(string name, object payload = null) { }
        public void On<T>(Action<T> fn) { }
        public void Emit<T>(T payload) { }
    }
    class Tm : IGameTimer { private readonly FakeCtx _c; public Tm(FakeCtx c) { _c = c; } public double Now => _c.GameTime; public void After(double s, Action f) { } public void Every(double s, Action f) { } public double Delta => 0.05; }
    class St : IStore
    {
        private readonly FakeCtx _c; public St(FakeCtx c) { _c = c; }
        public T Get<T>(string key, T def = default) => _c.StoreData.TryGetValue(key, out var v) ? (T)v : def;
        public void Set<T>(string key, T value) => _c.StoreData[key] = value;
        public bool Has(string key) => _c.StoreData.ContainsKey(key);
        public void Remove(string key) => _c.StoreData.Remove(key);
    }
}

class Lib<T> : ILibrary<T> where T : class { private readonly T _api; public Lib(T api) { _api = api; } public T For(ModContext ctx) => _api; }

class FakeSpells : ISpells
{
    public bool InBattle { get; set; } public bool Paused { get; set; } public Dictionary<int, int> Levels = new();
    public IActiveSpell AddActive(ActiveSpellDef def) => null;
    public int Level(int id) => Levels.TryGetValue(id, out var l) ? l : 0;
    public bool CanCast(int id) => false; public void SetAuto(int id, bool auto) { } public bool Cast(int id, int steps = 2) => false; public double Cooldown(int id) => 1;
}
class FakeHero : IHero
{
    public double Hp { get; set; } = 100; public double HpMax { get; set; } = 100; public List<double> Healed = new();
    public double Heal(double amount) { Healed.Add(amount); Hp = Math.Min(HpMax, Hp + amount); return amount; }
    public double Shield => 0; public void AddShield(double amount) { }
}
class FakeCombat : IEnemies, ICombat
{
    public readonly Transform HeroTr = new Transform();                 // объект героя (как Transform_ObjUnit)
    public Vector3 Pos { get => HeroTr.position; set => HeroTr.position = value; }
    public readonly List<IEnemy> Enemies = new();
    public IReadOnlyList<IEnemy> Alive => Enemies;
    public IReadOnlyList<IEnemy> Near(double x, double y, double r) => new List<IEnemy>();
    public IEnemy Nearest(double x, double y) => null;
    public Vector3? HeroPos => Pos;
    public int Kills;
    public bool Damage(IEnemy e, double amount, int magicId = 0, int criRan = 0, double criMultiplier = 1)
    {
        if (!(e is FakeEnemy fe) || !fe.Unit_IsLive) return false;
        fe.DamageTaken += amount; fe.Unit_Hp -= (float)amount;
        if (fe.Unit_Hp <= 0) { fe.Unit_IsLive = false; Enemies.Remove(e); Kills++; }
        return true;
    }
    public bool Freeze(IEnemy e, double seconds) => false; public bool Stun(IEnemy e, double seconds) => false;
    public bool Pull(IEnemy e, double x, double y, double dist) => false; public bool Shoot(int bulletId, double x, double y, double z = 0, double ratio = 1) => false;
}
/// <summary>Враг игры (вместо C_Battle_EnemyUnit): поля и методы, которыми пользуются общие враги.</summary>
class FakeEnemy : MonoBehaviour, IEnemy
{
    private readonly Player _p;
    public FakeEnemy(Player p, int unitId) { _p = p; UnitID = unitId; }
    public int UnitID; public bool Unit_IsLive = true, Puppet, DestroyedSimple;
    public Transform Transform_ObjUnit = new Transform();
    public Rigidbody Rigidbody_ObjUnit = new Rigidbody();
    public float UnitBase_MoveSpeed = 0.064f, UnitApply_MoveSpeed = 0.064f, UnitApply_UnitSize = 1.25f, Unit_Hp = 20;
    public double DamageTaken;
    public Transform Transform_ObjUnitSprite = new Transform { localScale = new Vector3(1, 1, 1) };
    public void Unit_Destroy_Simple() { DestroyedSimple = true; Unit_IsLive = false; _p.Combat.Enemies.Remove(this); }
    public void Fire(float a = 0, float b = 0) => _p.Ctx.Fire("After C_Battle_EnemyUnit.Start_Unit/3", this, new object[] { UnitID, a, b });   // как хук Harmony
    public float VarA, VarB;
    public int UnitCreateID = 1, ApplyCreateEffectID; public float LineColorR, LineColorG, LineColorB;
    public void Start_Unit(int id, float a, float b) { UnitID = id; VarA = a; VarB = b; Fire(a, b); }
    int IEnemy.Id => UnitID; double IEnemy.Hp => 20; Vector3 IEnemy.Pos => Transform_ObjUnit.position; bool IEnemy.Alive => Unit_IsLive; object IEnemy.Raw => this;
}
class StillEnemy : IEnemy { public Vector3 P; public int Id => 1; public double Hp => 20; public Vector3 Pos => P; public bool Alive => true; public object Raw => this; }
class FakeComp : ICompanion
{
    public int Slot { get; set; } public Vector2 Offset { get; set; } public bool Alive { get; set; } = true; public int Spell { get; set; }
    public CompanionMode Mode { get; set; } public Vector2 Pos => Offset; public double Speed { get; set; } public double Leash { get; set; } public double StandOff { get; set; } public double GuardRadius { get; set; }
    public void Remove() => Alive = false;
    public Transform Tr = new Transform();                               // как в библиотеке: объект союзника в поле Tr
}
/// <summary>Герой игры (вместо C_Battle_AllyUnit): Bullet_Create записывает вызов и, как хук Harmony, вызывает хуки мода.</summary>
class GameHero
{
    public readonly List<(object src, int id, float x, float y, float dr)> Calls = new();
    readonly Player _p;
    public GameHero(Player p) { _p = p; }
    public Transform Transform_ObjUnit => _p.Combat.HeroTr;
    // Щит/Спутники/Духи, как у C_Battle_AllyUnit (docs/ORBITALS.md)
    public bool[] Array_IsSatelliteA_P = new bool[14], Array_IsSatelliteB_P = new bool[14];
    public Transform[] Array_Transform_ObjSatellitesA = Enumerable.Range(0, 14).Select(_ => new Transform()).ToArray();
    public Transform[] Array_Transform_ObjSatellitesB = Enumerable.Range(0, 14).Select(_ => new Transform()).ToArray();
    public CloneGo[] Array_Obj_UnitSatellitesA = { new CloneGo("satA", "UnitSatellite01") }, Array_Obj_UnitSatellitesB = { new CloneGo("satB", "UnitSatellite01") };
    public List<bool> List_IsSpirit_P = Enumerable.Repeat(false, 15).ToList();
    public List<CloneGo> List_ObjSpirit = new[] { (CloneGo)null }.Concat(Enumerable.Range(1, 14).Select(_ => new CloneGo("spirit", "UnitSpirit1MotionA"))).ToList();
    public List<Transform> List_Transform_ObjSpirit => List_ObjSpirit.Select(g => g?.transform).ToList();
    public CloneGo Obj_ElectricZone = new CloneGo("zone", "ElectricZone01");
    public CloneGo Obj_Shield = new CloneGo("shield");          // картинки нет, пока нет заклинания (как в игре)
    public bool IsShield_P, IsElectricZone_P, IsMagicCircle_P;
    public CloneGo Obj_MagicCircleMid = new CloneGo("circle");
    public SpriteRenderer SpriteRenderer_MagicCircleA = new SpriteRenderer { sprite = new Sprite { name = "MagicCircle3" } }, SpriteRenderer_MagicCircleB = new SpriteRenderer(), SpriteRenderer_MagicCircleC = new SpriteRenderer();
    public SpriteRenderer SpriteRenderer_Shield => Obj_Shield.Sr;
    public void Bullet_Create(Transform src, int id, float x, float y, float z, float aim, float p1, float p2, float s1, float s2, float s3, float s4, float dr)
    {
        _p.Ctx.Fire("Before C_Battle_AllyUnit.Bullet_Create/13", this, new object[] { src, id, x, y, z, aim, p1, p2, s1, s2, s3, s4, dr });
        Calls.Add((src, id, x, y, dr));
    }
}
static class WinTimer { [System.Runtime.InteropServices.DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms); }
class FakePlayTime { public int Counter { get; set; } public int Other { get; set; } public int __1__state { get; set; } }
/// <summary>Менеджер боя (вместо C_Battle_Manager): флаг взрыва Армагеддона и его объекты.</summary>
class FakeText { public Color color { get; set; } }
class FakeManager
{
    public Player Owner;
    public int EnemyCreateNum = 2, EnemyCreateNum_Min = 0, EnemyCreateNum_Max = 300;
    public int Time_Min, Time_Sec, Battle_ClearWaveON, EnemyWaveID = 1, EnemyLevelNum, AreaChangeNum;
    public readonly List<string> Coroutines = new();
    public void StartCoroutine(string name)
    {
        if (name.StartsWith("Play_Armageddon")) throw new MissingMethodException(name);   // проверки Армагеддона — по таблице объектов
        Coroutines.Add(name);
    }
    // как в игре (консоль на телефоне 25.10): босс-этап — часы стоят на пороге, вступление (BossWave_EnforceVar) растёт; его
    // конец при ApplyBossNum 0 заканчивает волну и кладёт обелиск. Start_AreaChange на самом пороге начинает вступление заново,
    // за порогом — часы идут
    public int ApplyObeliskON, AreaChanges, Ends, AreaChangeID = 1; public float BossWave_EnforceVar = 1, BossWave_VisionRatio, ApplyBossGround_Size, EnforceStep = 0.01f;
    bool _intro;
    public int Clock => Time_Min * 60 + Time_Sec;
    public GameObject Obj_MonsterClearBar = new GameObject();                  // череп босс-волны над часами
    public FakeText Text_Time = new FakeText { color = new Color(1, 1, 1, 1) };
    public void BeginBoss(int min)
    {
        Time_Min = min; Time_Sec = 0; Battle_ClearWaveON = 1; BossWave_VisionRatio = 15; ApplyBossGround_Size = 145; BossWave_EnforceVar = 0; _intro = true;
        Obj_MonsterClearBar.SetActive(true); Text_Time.color = new Color(1, 0.314f, 0.039f, 1);
    }
    public void Start_AreaChange()
    {
        AreaChanges++; ApplyObeliskON = 0; ApplyBossGround_Size = 0;
        if (Time_Sec == 0 && Time_Min % 3 == 0) { BossWave_EnforceVar = 0; _intro = true; }
    }
    public void Step()
    {
        if (!_intro) return;
        BossWave_EnforceVar = Math.Min(0.999f, BossWave_EnforceVar + EnforceStep);
        if (BossWave_EnforceVar < 0.999f) return;
        _intro = false; BossWave_VisionRatio = 0;
        if (ApplyBossNum <= 0) { Ends++; Battle_ClearWaveON = 0; AreaChangeNum++; ApplyObeliskON = 1; ApplyBossGround_Size = 145; }
    }
    public static int ApplyBossNum { get; set; }
    // как в игре: статические пулы; Create_UnitEnemyBoss берёт из пула боссов (маленький; при 1 заготовке уже не создаёт),
    // погибшие уходят в обычный пул
    public static List<object> List_BattleUnitEnemyBoss_Empty { get; set; } = new() { new object(), new object(), new object() };
    public static List<object> List_BattleUnitEnemy_Empty { get; set; } = Enumerable.Range(0, 30).Select(_ => new object()).ToList();
    public void Create_UnitEnemyBoss(int id, int num)
    {
        ApplyBossNum++;
        for (int i = 0; i < num; i++) {
            if (List_BattleUnitEnemyBoss_Empty.Count <= 1) return;
            List_BattleUnitEnemyBoss_Empty.RemoveAt(0); var e = new FakeEnemy(Owner, id) { Puppet = true }; e.Transform_ObjUnit.position = Owner.Combat.Pos; Owner.Combat.Enemies.Add(e); e.Fire(); }
    }
    public bool IsArmageddonContinue;
    public CloneGo Obj_ArmageddonGround_Front = new("arma"), Obj_ArmageddonGround_Back = new("arma"), Obj_Armageddon = new("arma", "Armageddon1"),
                   Obj_ArmageddonLand = new("arma", "ArmageddonLandA"), Obj_ArmageddonGround_Destroy = new("arma");
}
class CloneGo : GameObject, ICloneTarget
{
    public static readonly List<CloneGo> Made = new();
    public readonly string Kind;
    public CloneGo(string kind, string img = null) { Kind = kind; TestComponent = new SpriteRenderer { sprite = img == null ? null : new Sprite { name = img }, gameObject = this }; }
    public SpriteRenderer Sr => (SpriteRenderer)TestComponent;
    public UnityEngine.Object CloneForTest() { var c = new CloneGo(Kind) { activeSelf = true }; c.Sr.sprite = Sr.sprite; Made.Add(c); return c; }
}
class FakeComps : ICompanions
{
    public List<FakeComp> Spawned = new();
    public int MaxSlots => 9; public IReadOnlyList<ICompanion> Active => Spawned.Where(c => c.Alive).ToList<ICompanion>();
    public ICompanion Spawn(double dx, double dy, int spellId = 0, double[] color = null, double scale = 0.7, CompanionMode mode = CompanionMode.Follow)
    { var c = new FakeComp { Slot = 9 - Spawned.Count, Offset = new Vector2((float)dx, (float)dy), Spell = spellId, Mode = mode }; Spawned.Add(c); return c; }
    public void Clear() { foreach (var c in Spawned) c.Alive = false; }
}
