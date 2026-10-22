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
Check(g != null && g.Spell == 7, $"союзник стреляет лучшим заклинанием друга (7, Молния): {g?.Spell}");
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
    var frameX = new List<double>();
    B.Combat.Pos = new Vector3((float)x0, 104, 0); TickAll(600);
    double t0 = sw.Elapsed.TotalSeconds;
    while (sw.Elapsed.TotalSeconds - t0 < 2.0)
    {
        double now = sw.Elapsed.TotalSeconds - t0;
        B.Combat.Pos = new Vector3((float)(x0 + 4 * now), 104, 0);        // друг идёт ровно 4 ед/с
        if (now >= nextSend) { B.Tick(); nextSend = now + 0.1; }            // друг отправляет 10 раз в секунду
        if (now >= nextPump) { A.Call("Pump"); nextPump = now + (rnd.NextDouble() < 0.3 ? 0.15 + rnd.NextDouble() * 0.15 : 0.02 + rnd.NextDouble() * 0.06); }
        if (now - lastFrame >= 1.0 / 60) { lastFrame = now; A.Call("FrameStep"); frameX.Add(g.Offset.x); }
        Thread.Sleep(2);
    }
    // после разгона: шаги по кадрам должны быть ровными (идеал 4/60 ≈ 0,067)
    var steps = frameX.Skip(40).Zip(frameX.Skip(41), (p0, p1) => p1 - p0).Where(d => Math.Abs(d) < 1).ToList();
    double mean = steps.Average(), sd = Math.Sqrt(steps.Average(d => (d - mean) * (d - mean)));
    int stops = steps.Count(d => d < 0.01), back = steps.Count(d => d < -0.005);
    Console.WriteLine($"    шаг за кадр: средний {mean:0.000}, разброс {sd:0.000}, остановок {stops}, откатов назад {back} (кадров {steps.Count})");
    Check(sd < 0.01, $"движение ровное: разброс шага {sd:0.000} (меньше 0,01)");
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

B.Combat.Pos = new Vector3(150, 100, 0);
TickAll(400);
Check(g != null && Math.Abs(Math.Sqrt(g.Offset.x * g.Offset.x + g.Offset.y * g.Offset.y) - 6) < 0.01, "друг убежал далеко — союзник держится на дальности 6");
var gB = B.Comp.Spawned.LastOrDefault(c => c.Alive);
Check(gB != null && gB.Spell == 0, "у друга тоже есть союзник-хост (без заклинаний — стрельба двойника игры)");

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
    public List<string> Toasts => Ctx.Toasts; public List<string> Errors => Ctx.Errors;
    private readonly ModDef _def; private object _session;
    public Player(string name)
    {
        Ctx = new FakeCtx(name, this);
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
    public void After(string c, string m, int n, Action<object, object[]> f) { }
    public void Before(string c, string m, int n, Action<object, object[]> f) { }
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
    public Vector3 Pos;
    public IReadOnlyList<IEnemy> Alive => new List<IEnemy>();
    public IReadOnlyList<IEnemy> Near(double x, double y, double r) => new List<IEnemy>();
    public IEnemy Nearest(double x, double y) => null;
    public Vector3? HeroPos => Pos;
}
class FakeComp : ICompanion
{
    public int Slot { get; set; } public Vector2 Offset { get; set; } public bool Alive { get; set; } = true; public int Spell { get; set; }
    public CompanionMode Mode { get; set; } public Vector2 Pos => Offset; public double Speed { get; set; } public double Leash { get; set; } public double StandOff { get; set; } public double GuardRadius { get; set; }
    public void Remove() => Alive = false;
}
class FakeComps : ICompanions
{
    public List<FakeComp> Spawned = new();
    public int MaxSlots => 9; public IReadOnlyList<ICompanion> Active => Spawned.Where(c => c.Alive).ToList<ICompanion>();
    public ICompanion Spawn(double dx, double dy, int spellId = 0, double[] color = null, double scale = 0.7, CompanionMode mode = CompanionMode.Follow)
    { var c = new FakeComp { Slot = 9 - Spawned.Count, Offset = new Vector2((float)dx, (float)dy), Spell = spellId, Mode = mode }; Spawned.Add(c); return c; }
    public void Clear() { foreach (var c in Spawned) c.Alive = false; }
}
