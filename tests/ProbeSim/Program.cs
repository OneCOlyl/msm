using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Reflection; using System.Threading;
using Il2Cpp; using ModKit; using UnityEngine;

// Проверка разведчика на ПК: фальшивый менеджер боя и враги (сборка Assembly-CSharp), время ускорено в 20 раз.
int passes = 0, fails = 0;
void Check(bool ok, string what) { if (ok) { passes++; Console.WriteLine("  PASS " + what); } else { fails++; Console.WriteLine("  FAIL " + what); } }

var mgr = new C_Battle_Manager();          // загружает сборку Assembly-CSharp
var ctx = new Ctx(); var world = new World(mgr, ctx);
var def = new MagicSurvivalCoop.Probe.ProbeMod().Define();
typeof(MagicSurvivalCoop.Probe.ProbeMod).Assembly.GetType("MagicSurvivalCoop.Probe.Probe").GetField("TimeScale", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, 20.0);
var probeType = typeof(MagicSurvivalCoop.Probe.ProbeMod).Assembly.GetType("MagicSurvivalCoop.Probe.Probe");
var outDir = Path.Combine(Path.GetTempPath(), "coop_probe_test"); Directory.CreateDirectory(outDir);
probeType.GetField("DirOverride", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, outDir);
ctx.ReportPath = Path.Combine(outDir, "coop_probe.txt"); if (File.Exists(ctx.ReportPath)) File.Delete(ctx.ReportPath);
def.OnEnable(ctx);
Check(ctx.Hooks.ContainsKey("After C_Battle_EnemyUnit.Start_Unit/3"), "хук на создание врага (Start_Unit) поставлен");
Check(ctx.Hooks.Keys.Any(k => k.Contains("Create_UnitEnemy_Stop/0")), "хуки на методы спавна менеджера поставлены");

ctx.Emit("battleStart");
var sw = System.Diagnostics.Stopwatch.StartNew();
while (sw.Elapsed.TotalSeconds < 7 && !ctx.Logs.Any(l => l.Contains("РАЗВЕДКА ЗАВЕРШЕНА")))
{
    double gameT = sw.Elapsed.TotalSeconds * 20;
    world.Step(gameT);
    foreach (var f in ctx.Ticks.ToArray()) f();
    Thread.Sleep(5);
}
def.OnDisable(ctx); foreach (var c in ctx.Cleanups) c();

var report = File.Exists(ctx.ReportPath) ? File.ReadAllText(ctx.ReportPath) : "";
Console.WriteLine("---- журнал разведчика:"); foreach (var l in ctx.Logs) Console.WriteLine("    " + l);
Check(ctx.Logs.Any(l => l.Contains("РАЗВЕДКА ЗАВЕРШЕНА (все опыты проведены)")), "разведка прошла все этапы до конца");
Check(report.Contains("##### МЕНЕДЖЕР БОЯ") && report.Contains("Create_UnitEnemy_Stop()") && report.Contains("Array_EnemyPool"), "в отчёте структура менеджера боя");
Check(report.Contains("##### ВРАГ") && report.Contains("Start_Unit(Int32 unitId, Single varA, Single varB)"), "в отчёте структура врага с сигнатурой Start_Unit");
Check(report.Contains("враг: тип ") && report.Contains("от героя"), "каждый новый враг записан с типом и местом");
Check(report.Contains("значения полей первого врага") && report.Contains("UnitApply_MoveSpeed = 1.5"), "значения полей врага записаны");
Check(report.Contains("Array_EnemyPool: C_Battle_EnemyUnit[] = 300"), "размеры коллекций менеджера (пул) записаны");
Check(C_Battle_Manager.StopCalls == 1 && report.Contains("спавн выключается ✔"), "опыт 2: спавн выключен и это подтверждено");
Check(C_Battle_Manager.RestartCalls == 1, "опыт 2: спавн включён обратно");
Check(report.Contains("C_Battle_EnemyUnitPool"), "типы с Pool/Enemy в имени перечислены");
var freezeLine = report.Split('\n').LastOrDefault(l => l.Contains("заморозка:") && !l.Contains("сдвинут"));
var freeLine = report.Split('\n').LastOrDefault(l => l.Contains("без статуса:") && !l.Contains("сдвинут"));
Console.WriteLine($"    {freezeLine?.Trim()} | {freeLine?.Trim()}");
Check(report.Contains("опыт 3") && freezeLine != null && freeLine != null, "опыт 3: марионетки проверены по группам");
Console.WriteLine("\n---- начало отчёта:"); Console.WriteLine(string.Join("\n", report.Split('\n').Take(25)));
Console.WriteLine($"\nИТОГ: {passes} PASS, {fails} FAIL");
return fails == 0 ? 0 : 1;

class World
{
    readonly C_Battle_Manager _m; readonly Ctx _c; double _nextSpawn, _last; readonly Random _r = new Random(1);
    public readonly List<FakeEnemy> Enemies = new();
    public World(C_Battle_Manager m, Ctx c) { _m = m; _c = c; c.World = this; }
    public void Step(double t)
    {
        double dt = t - _last; _last = t;
        _c.Fire("Before C_Battle_Manager.Update/0", _m, new object[0]);
        if (_m.Spawning && t >= _nextSpawn)
        {
            _nextSpawn = t + 0.5;
            var raw = new C_Battle_EnemyUnit(); double a = _r.NextDouble() * Math.PI * 2;
            raw.Transform_ObjUnit.position = new Vector3((float)(Math.Cos(a) * 9), (float)(Math.Sin(a) * 9), 0);
            var e = new FakeEnemy(raw, 1 + _r.Next(3)); Enemies.Add(e);
            _c.Fire("After C_Battle_EnemyUnit.Start_Unit/3", raw, new object[] { e.Id, 1.25f, 0f });
        }
        foreach (var e in Enemies.Where(x => x.Alive))
        {
            if (t < e.FrozenUntil) continue;
            var p = e.Raw2.Transform_ObjUnit.position; double len = Math.Sqrt(p.x * p.x + p.y * p.y);
            if (len > 0.5) e.Raw2.Transform_ObjUnit.position = new Vector3((float)(p.x - p.x / len * 1.5 * dt), (float)(p.y - p.y / len * 1.5 * dt), 0);
        }
        Now = t;
    }
    public double Now;
}
class FakeEnemy : IEnemy
{
    public readonly C_Battle_EnemyUnit Raw2; public double FrozenUntil;
    public FakeEnemy(C_Battle_EnemyUnit r, int id) { Raw2 = r; Id = id; }
    public int Id { get; } public double Hp => 10; public Vector3 Pos => Raw2.Transform_ObjUnit.position; public bool Alive => true; public object Raw => Raw2;
}
class Ctx : ModContext
{
    public World World; public readonly Dictionary<string, List<Action<object, object[]>>> Hooks = new(); public readonly List<Action> Ticks = new(), Cleanups = new();
    public readonly Dictionary<string, List<Action<object>>> Handlers = new(); public readonly List<string> Logs = new();
    public string ReportPath;
    public void Fire(string key, object self, object[] a) { if (Hooks.TryGetValue(key, out var l)) foreach (var h in l) h(self, a); }
    public void Emit(string ev) { if (Handlers.TryGetValue(ev, out var l)) foreach (var h in l) h(null); }
    public string Id => "coop_probe"; public IReadOnlyDictionary<string, double> Settings => new Dictionary<string, double>(); public double Setting(string k) => 0; public void SetSetting(string k, double v) { }
    public void Log(string m) => Logs.Add(m); public void Warn(string m) => Logs.Add("WARN " + m);
    public ModEvents Events => new Ev(this); public void Expose(object api) { }
    public T Mod<T>(string id) where T : class => id == "modkit_combat" ? (typeof(T) == typeof(ILibrary<IEnemies>) ? new Lib<IEnemies>(new En(this)) as T : new Lib<ICombat>(new Cb(this)) as T) : null;
    public void Replace(string c, string m, int n, Func<object, object[], object> f) { }
    public void After(string c, string m, int n, Action<object, object[]> f) => Add($"After {c}.{m}/{n}", f);
    public void Before(string c, string m, int n, Action<object, object[]> f) => Add($"Before {c}.{m}/{n}", f);
    void Add(string k, Action<object, object[]> f) { if (!Hooks.TryGetValue(k, out var l)) Hooks[k] = l = new(); l.Add(f); }
    public void Every(double ms, Action fn) => Ticks.Add(fn); public void OnCleanup(Action fn) => Cleanups.Add(fn);
    public IRegistry<T> Registry<T>(string name) => null; public void Toast(string text, double sec = 3) => Logs.Add("экран: " + text);
    public byte[] ReadAsset(string path) => null; public IGameTimer Timer => null; public IStore Store => null; public ITags Tags => null;
    public void Guard(string what, Action fn) { try { fn(); } catch (Exception e) { Logs.Add($"ОШИБКА {what}: {e}"); } }
    public void Error(string msg) => Logs.Add("ERR " + msg); public void Mark(string what) { }
    class Ev : ModEvents { readonly Ctx _c; public Ev(Ctx c) { _c = c; } public void On(string n, Action<object> f) { if (!_c.Handlers.TryGetValue(n, out var l)) _c.Handlers[n] = l = new(); l.Add(f); } public void Emit(string n, object p = null) { } public void On<T>(Action<T> f) { } public void Emit<T>(T p) { } }
    class Lib<T> : ILibrary<T> where T : class { readonly T _a; public Lib(T a) { _a = a; } public T For(ModContext c) => _a; }
    class En : IEnemies
    {
        readonly Ctx _c; public En(Ctx c) { _c = c; }
        public IReadOnlyList<IEnemy> Alive => _c.World.Enemies.ToList<IEnemy>();
        public IReadOnlyList<IEnemy> Near(double x, double y, double r) => _c.World.Enemies.OrderBy(e => Math.Pow(e.Pos.x - x, 2) + Math.Pow(e.Pos.y - y, 2)).Where(e => Math.Sqrt(Math.Pow(e.Pos.x - x, 2) + Math.Pow(e.Pos.y - y, 2)) <= r).ToList<IEnemy>();
        public IEnemy Nearest(double x, double y) => Near(x, y, 1e9).FirstOrDefault();
        public Vector3? HeroPos => new Vector3(0, 0, 0);
    }
    class Cb : ICombat
    {
        readonly Ctx _c; public Cb(Ctx c) { _c = c; }
        public bool Damage(IEnemy e, double a, int m = 0, int c = 0, double k = 1) => true;
        public bool Freeze(IEnemy e, double s) { ((FakeEnemy)e).FrozenUntil = _c.World.Now + s; return true; }
        public bool Stun(IEnemy e, double s) { ((FakeEnemy)e).FrozenUntil = _c.World.Now + s; return true; }
        public bool Pull(IEnemy e, double x, double y, double d) { var fe = (FakeEnemy)e; var p = fe.Pos; double dx = x - p.x, dy = y - p.y, l = Math.Sqrt(dx * dx + dy * dy); if (l < 1e-4) return true; double st = Math.Min(d, l); fe.Raw2.Transform_ObjUnit.position = new Vector3((float)(p.x + dx / l * st), (float)(p.y + dy / l * st), 0); return true; }
        public bool Shoot(int b, double x, double y, double z = 0, double r = 1) => true;
    }
}
