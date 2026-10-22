using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Reflection; using System.Threading;
using Il2Cpp; using ModKit; using UnityEngine;

// Проверка разведчика 2 на ПК: фальшивый менеджер боя и враги (сборка Assembly-CSharp), игровое время ускорено.
int passes = 0, fails = 0;
void Check(bool ok, string what) { if (ok) { passes++; Console.WriteLine("  PASS " + what); } else { fails++; Console.WriteLine("  FAIL " + what); } }

var mgr = new C_Battle_Manager();          // загружает сборку Assembly-CSharp
var ctx = new Ctx(); var world = new World(mgr, ctx);
var def = new MagicSurvivalCoop.Probe.ProbeMod().Define();
var probeType = typeof(MagicSurvivalCoop.Probe.ProbeMod).Assembly.GetType("MagicSurvivalCoop.Probe.Probe");
var outDir = Path.Combine(Path.GetTempPath(), "coop_probe_test"); Directory.CreateDirectory(outDir);
probeType.GetField("DirOverride", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, outDir);
var report = Path.Combine(outDir, "coop_probe.txt"); if (File.Exists(report)) File.Delete(report);
def.OnEnable(ctx);
FakeWorld.OnStartUnit = (e, id, a, b) => ctx.Fire("After C_Battle_EnemyUnit.Start_Unit/3", e, new object[] { id, a, b });

ctx.Emit("battleStart");
double t = 0;
for (int frame = 0; frame < 75 * 30 && !ctx.Logs.Any(l => l.Contains("РАЗВЕДКА 2 ЗАВЕРШЕНА")); frame++)
{
    t += 1.0 / 30; ctx.GameTime = t; ctx.Delta = 1.0 / 30;
    if (frame >= 900 && frame < 960) { ctx.Delta = 0; t -= 1.0 / 30; ctx.GameTime = t; }   // пауза 2 с посреди боя: игровое время стоит
    world.Step(t, ctx.Delta);
    ctx.Fire("Before C_Battle_Manager.Update/0", mgr, new object[0]);
    if (frame % 3 == 0) foreach (var f in ctx.Ticks.ToArray()) f();
}
def.OnDisable(ctx); foreach (var c in ctx.Cleanups) c();
var txt = File.Exists(report) ? File.ReadAllText(report) : "";
Console.WriteLine(txt);
foreach (var l in ctx.Logs.Where(l => l.Contains("ОШИБКА"))) Console.WriteLine("    " + l);
Check(ctx.Logs.Any(l => l.Contains("РАЗВЕДКА 2 ЗАВЕРШЕНА (все опыты проведены)")), "разведка 2 прошла все опыты");
Check(!ctx.Logs.Any(l => l.Contains("ОШИБКА")), "без исключений");
Check(txt.Contains("создано прямо во время вызова: 1 (тип 1"), "A1: враг, созданный по команде, распознан как наш");
Check(txt.Contains("ApplyBossNum: 0 → 1"), "A1: побочные изменения менеджера видны");
Check(txt.Contains("копия создана") && txt.Contains("компонент врага: есть") && txt.Contains("Start_Unit прошёл; жив True"), "A2: копия шаблона создана и запущена");
Check(txt.Contains("стоп+кин: наибольший уход от поставленной точки за кадр 0.000"), "B: ведомая марионетка стоит точно в точке");
Check(System.Text.RegularExpressions.Regex.IsMatch(txt, @"  кин: ушёл от точки на [1-9]"), "B: без остановки корутин враг уходит (ИИ жив)");
Check(System.Text.RegularExpressions.Regex.IsMatch(txt, @"  стоп: ушёл от точки на 0\.[0-9][0-9]"), "B: без кинематики стоящего врага немного сдвигают");
Check(txt.Contains("===== C:") && txt.Contains("оценка на 200 врагов"), "C: нагрузка измерена");
Check(System.Text.RegularExpressions.Regex.IsMatch(txt, @"появилось (\d+), убрано \1,"), "D: все новые враги убраны");
Check(txt.Contains("EnemyCreateNum было 2, ставлю 0: ок") && txt.Contains("за 10 с создано врагов: 0") && mgr.EnemyCreateNum == 2, "E: спавн погашен и возвращён");
Console.WriteLine($"\nИТОГ: {passes} PASS, {fails} FAIL");
return fails == 0 ? 0 : 1;

class World
{
    readonly C_Battle_Manager _m; readonly Ctx _c; double _nextSpawn; readonly Random _r = new Random(1);
    public World(C_Battle_Manager m, Ctx c) { _m = m; _c = c; c.World = this; }
    public void Step(double t, double dt)
    {
        FakeWorld.Now = t;
        if (dt <= 0) return;
        if (t >= _nextSpawn)
        {
            _nextSpawn = t + 0.76;
            for (int i = 0; i < _m.EnemyCreateNum; i++) { double a = _r.NextDouble() * Math.PI * 2; C_Battle_EnemyUnit.Spawn(1, Math.Cos(a) * 9, Math.Sin(a) * 9); }
        }
        foreach (var e in FakeWorld.All.ToList())
        {
            var p = e.Transform_ObjUnit.position; double x = p.x, y = p.y;
            if (!e.CoroutinesStopped) { double len = Math.Sqrt(x * x + y * y); if (len > 0.6) { x -= x / len * 1.5 * dt; y -= y / len * 1.5 * dt; } }   // ИИ: идёт к герою
            if (!e.Rigidbody_ObjUnit.isKinematic) { x += (_r.NextDouble() - 0.5) * 0.6 * dt * 2; y += (_r.NextDouble() - 0.5) * 0.6 * dt * 2; }   // толкотня тел
            e.Transform_ObjUnit.position = new Vector3((float)x, (float)y, 0);
        }
    }
}
class En : IEnemies
{
    class E : IEnemy { public C_Battle_EnemyUnit R; public int Id => R.UnitID; public double Hp => R.Unit_Hp; public Vector3 Pos => R.Transform_ObjUnit.position; public bool Alive => R.Unit_IsLive; public object Raw => R; }
    public IReadOnlyList<IEnemy> Alive => FakeWorld.All.Select(r => (IEnemy)new E { R = r }).ToList();
    public IReadOnlyList<IEnemy> Near(double x, double y, double r) => Alive.Where(e => Math.Sqrt(Math.Pow(e.Pos.x - x, 2) + Math.Pow(e.Pos.y - y, 2)) <= r).OrderBy(e => Math.Pow(e.Pos.x - x, 2) + Math.Pow(e.Pos.y - y, 2)).ToList();
    public IEnemy Nearest(double x, double y) => Near(x, y, 1e9).FirstOrDefault();
    public Vector3? HeroPos => FakeWorld.Hero;
}
class Ctx : ModContext
{
    public World World; public double GameTime, Delta;
    public readonly Dictionary<string, List<Action<object, object[]>>> Hooks = new(); public readonly List<Action> Ticks = new(), Cleanups = new();
    public readonly Dictionary<string, List<Action<object>>> Handlers = new(); public readonly List<string> Logs = new();
    public void Fire(string key, object self, object[] a) { if (Hooks.TryGetValue(key, out var l)) foreach (var h in l) h(self, a); }
    public void Emit(string ev) { if (Handlers.TryGetValue(ev, out var l)) foreach (var h in l) h(null); }
    public string Id => "coop_probe"; public IReadOnlyDictionary<string, double> Settings => new Dictionary<string, double>(); public double Setting(string k) => 0; public void SetSetting(string k, double v) { }
    public void Log(string m) => Logs.Add(m); public void Warn(string m) => Logs.Add("WARN " + m);
    public ModEvents Events => new Ev(this); public void Expose(object api) { }
    public T Mod<T>(string id) where T : class => id == "modkit_combat" && typeof(T) == typeof(ILibrary<IEnemies>) ? new Lib<IEnemies>(new En()) as T : null;
    public void Replace(string c, string m, int n, Func<object, object[], object> f) { }
    public void After(string c, string m, int n, Action<object, object[]> f) => Add($"After {c}.{m}/{n}", f);
    public void Before(string c, string m, int n, Action<object, object[]> f) => Add($"Before {c}.{m}/{n}", f);
    void Add(string k, Action<object, object[]> f) { if (!Hooks.TryGetValue(k, out var l)) Hooks[k] = l = new(); l.Add(f); }
    public void Every(double ms, Action fn) => Ticks.Add(fn); public void OnCleanup(Action fn) => Cleanups.Add(fn);
    public IRegistry<T> Registry<T>(string name) => null; public void Toast(string text, double sec = 3) => Logs.Add("экран: " + text);
    public byte[] ReadAsset(string path) => null; public IGameTimer Timer => new Tm(this); public IStore Store => null; public ITags Tags => null;
    public void Guard(string what, Action fn) { try { fn(); } catch (Exception e) { Logs.Add($"ОШИБКА {what}: {e}"); } }
    public void Error(string msg) => Logs.Add("ERR " + msg); public void Mark(string what) { }
    class Tm : IGameTimer { readonly Ctx _c; public Tm(Ctx c) { _c = c; } public double Now => _c.GameTime; public double Delta => _c.Delta; public void After(double s, Action f) { } public void Every(double s, Action f) { } }
    class Ev : ModEvents { readonly Ctx _c; public Ev(Ctx c) { _c = c; } public void On(string n, Action<object> f) { if (!_c.Handlers.TryGetValue(n, out var l)) _c.Handlers[n] = l = new(); l.Add(f); } public void Emit(string n, object p = null) { } public void On<T>(Action<T> f) { } public void Emit<T>(T p) { } }
    class Lib<T> : ILibrary<T> where T : class { readonly T _a; public Lib(T a) { _a = a; } public T For(ModContext c) => _a; }
}
