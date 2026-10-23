using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using ModKit;
using UnityEngine;

// Имитация игры: два настоящих экземпляра мода «Кооператив» (хост и друг) в одном процессе.
// Unity здесь нет, поэтому вызовы интерфейса падают и ловятся модом — проверяется сеть и игровая логика.
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
    Resources.TestAll.AddRange(new UnityEngine.Object[] { new Sprite { name = "Shield2_01" }, new Sprite { name = "Shield2_03" }, new Sprite { name = "Fireball" } });
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
    Check(CloneGo.Made.Count == 7, $"новых копий не создаётся: {CloneGo.Made.Count}");
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
Console.WriteLine($"\nОшибки, пойманные Guard (кроме интерфейса Unity): {string.Join("; ", A.Errors.Concat(B.Errors).Distinct().Take(8))}");
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
    public void Guard(string what, Action fn) { try { fn(); } catch (Exception e) { var m = $"{what}: {e.GetType().Name}: {e.Message}"; if (!Errors.Contains(m)) Errors.Add(m); } }
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
class FakeCombat : IEnemies
{
    public readonly Transform HeroTr = new Transform();                 // объект героя (как Transform_ObjUnit)
    public Vector3 Pos { get => HeroTr.position; set => HeroTr.position = value; }
    public readonly List<IEnemy> Enemies = new();
    public IReadOnlyList<IEnemy> Alive => Enemies;
    public IReadOnlyList<IEnemy> Near(double x, double y, double r) => new List<IEnemy>();
    public IEnemy Nearest(double x, double y) => null;
    public Vector3? HeroPos => Pos;
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
    public bool IsShield_P, IsElectricZone_P;
    public SpriteRenderer SpriteRenderer_Shield => Obj_Shield.Sr;
    public void Bullet_Create(Transform src, int id, float x, float y, float z, float aim, float p1, float p2, float s1, float s2, float s3, float s4, float dr)
    {
        _p.Ctx.Fire("Before C_Battle_AllyUnit.Bullet_Create/13", this, new object[] { src, id, x, y, z, aim, p1, p2, s1, s2, s3, s4, dr });
        Calls.Add((src, id, x, y, dr));
    }
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
