using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Reflection;
using Il2Cpp; using ModKit; using UnityEngine;

// Проверка разведки героя на ПК: фальшивый герой со Щитом, Спутниками и Духами.
int passes = 0, fails = 0;
void Check(bool ok, string what) { if (ok) { passes++; Console.WriteLine("  PASS " + what); } else { fails++; Console.WriteLine("  FAIL " + what); } }

var hero = new C_Battle_AllyUnit();
var ctx = new Ctx();
var probeType = typeof(MagicSurvivalCoop.Probe.ProbeMod).Assembly.GetType("MagicSurvivalCoop.Probe.Probe");
var outDir = Path.Combine(Path.GetTempPath(), "coop_probe_test"); Directory.CreateDirectory(outDir);
probeType.GetField("DirOverride", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, outDir);
probeType.GetField("HeroOverride", BindingFlags.NonPublic | BindingFlags.Static).SetValue(null, (Func<object>)(() => hero));
var report = Path.Combine(outDir, "coop_probe.txt"); if (File.Exists(report)) File.Delete(report);
var def = new MagicSurvivalCoop.Probe.ProbeMod().Define();
def.OnEnable(ctx);
var probeObj = def.OnEnable.Target.GetType().GetField("_p", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(def.OnEnable.Target);
var boardField = probeObj.GetType().GetField("_boardText", BindingFlags.NonPublic | BindingFlags.Instance);
Check(ctx.Hooks.ContainsKey("Before C_Battle_AllyUnit.Bullet_Create/13"), "хук на снаряды героя поставлен");

ctx.Emit("battleStart");
var spiritTr = hero.List_ObjSpirit[1].transform;
for (int i = 0; i < 600; i++)   // 60 с игрового времени, шаг 0,1 с
{
    ctx.GameTime += 0.1; Ctx.Sp.Now = ctx.GameTime;
    if (i % 5 == 0) ctx.Fire("Before C_Battle_AllyUnit.Bullet_Create/13", hero, new object[] { hero.Transform_ObjUnit, 1, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 1f });
    if (i % 10 == 0) ctx.Fire("Before C_Battle_AllyUnit.Bullet_Create/13", hero, new object[] { spiritTr, 16, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 1f });
    ctx.Fire("Before C_Battle_Manager.Update/0", null, new object[0]);
    if (i % 2 == 0) { foreach (var f in ctx.Ticks.ToArray()) f(); var bt = (string)boardField.GetValue(probeObj); if (ctx.BoardTexts.Count == 0 || ctx.BoardTexts[^1] != bt) ctx.BoardTexts.Add(bt); }
}
ctx.Emit("battleEnd");
def.OnDisable(ctx); foreach (var c in ctx.Cleanups) c();
var txt = File.Exists(report) ? File.ReadAllText(report) : "";
Console.WriteLine(txt);
foreach (var l in ctx.Logs.Where(l => l.Contains("ОШИБКА"))) Console.WriteLine("    " + l);
Check(!ctx.Logs.Any(l => l.Contains("ОШИБКА")), "без исключений");
Check(txt.Contains("##### ГЕРОЙ") && txt.Contains("GameObject Obj_UnitShield") && txt.Contains("Void Play_Shield()"), "структура героя: поля и методы Щита");
Check(txt.Contains("Obj_UnitShield = объект «Obj_UnitShield» активен True, от героя (0.00, 0.00), детей 3"), "снимок: Щит — объект, активен, где, сколько детей");
Check(txt.Contains("Array_Obj_UnitSatellitesA = ") && txt.Contains("[3]: объект «SatA01» активен True, от героя (2.00, 0.00)"), "снимок: массив Спутников с положениями");
Check(txt.Contains("List_ObjSpirit = ") && txt.Contains("«Spirit01»"), "снимок: список Духов");
Check(txt.Contains("UnitApply_ShieldMaxNum = 3") && txt.Contains("IsShield_P = True"), "снимок: числа и флаги");
Check(txt.Contains("ГЕРОЙ (1000): ") && txt.Contains("Spirit01: ") && txt.Contains("16×"), "источники снарядов: герой и Дух по именам");
Check(System.Text.RegularExpressions.Regex.Matches(txt, "===== снимок").Count >= 3, "снимки раз в 15 с и в конце боя");
Check(ctx.Logs.Any(l => l.Contains("РАЗВЕДКА ГЕРОЯ ЗАВЕРШЕНА")), "разведка завершилась с концом боя");
Check(txt.Contains("Щит (Obj_Shield): копия «1100(Clone)» создана, активна True"), "опыт копий: копия Щита создана и включена");
Check(txt.Contains("Спутник (Array_Obj_UnitSatellitesA[0]): копия «SatA01(Clone)» создана") && txt.Contains("Дух (List_ObjSpirit[1]): копия «Spirit01(Clone)» создана"), "опыт копий: копии Спутника и Духа созданы");
Check(txt.Contains("через 8 с ведения") && txt.Contains("Щит (Obj_Shield): 0.000"), "опыт копий: ведомая копия стоит точно в поставленной точке");
var shieldClone = C_Battle_AllyUnit.Clones.FirstOrDefault(c => c.name == "1100(Clone)");
Check(shieldClone != null && Math.Abs(shieldClone.transform.position.x - 3) < 0.01, "опыт копий: копия Щита стоит справа от героя (+3)");
Check(txt.Contains("копии удалены: 3 из 3") && C_Battle_AllyUnit.Clones.All(c => c.DestroyedForTest), "опыт копий: копии удалены");
Check(ctx.BoardTexts.Any(b => b.Contains("СПРАВА — копия Щита")), "табло объясняет опыт копий");
Check(ctx.BoardTexts.Any(b => b.Contains("возьмите (можно читом)")), "до появления заклинаний табло подсказывает взять их");
Check(System.Text.RegularExpressions.Regex.IsMatch(txt, @"опыт копий начат на 3[0-1] с \(Дух, Спутник и Щит есть\)"), "опыт копий начался, как только появились Дух, Спутник и Щит");
Console.WriteLine($"\nИТОГ: {passes} PASS, {fails} FAIL");
return fails == 0 ? 0 : 1;

class Ctx : ModContext
{
    public double GameTime; public readonly List<string> BoardTexts = new();
    public readonly Dictionary<string, List<Action<object, object[]>>> Hooks = new(); public readonly List<Action> Ticks = new(), Cleanups = new();
    public readonly Dictionary<string, List<Action<object>>> Handlers = new(); public readonly List<string> Logs = new();
    public void Fire(string key, object self, object[] a) { if (Hooks.TryGetValue(key, out var l)) foreach (var h in l) h(self, a); }
    public void Emit(string ev) { if (Handlers.TryGetValue(ev, out var l)) foreach (var h in l) h(null); }
    public string Id => "coop_probe"; public IReadOnlyDictionary<string, double> Settings => new Dictionary<string, double>(); public double Setting(string k) => 0; public void SetSetting(string k, double v) { }
    public void Log(string m) => Logs.Add(m); public void Warn(string m) => Logs.Add("WARN " + m);
    public ModEvents Events => new Ev(this); public void Expose(object api) { }
    public T Mod<T>(string id) where T : class =>
        typeof(T) == typeof(ILibrary<IEnemies>) ? new Lib<IEnemies>(new En()) as T :
        typeof(T) == typeof(ILibrary<ISpells>) ? new Lib<ISpells>(new Sp()) as T : null;
    public void Replace(string c, string m, int n, Func<object, object[], object> f) { }
    public void After(string c, string m, int n, Action<object, object[]> f) => Add($"After {c}.{m}/{n}", f);
    public void Before(string c, string m, int n, Action<object, object[]> f) => Add($"Before {c}.{m}/{n}", f);
    void Add(string k, Action<object, object[]> f) { if (!Hooks.TryGetValue(k, out var l)) Hooks[k] = l = new(); l.Add(f); }
    public void Every(double ms, Action fn) => Ticks.Add(fn); public void OnCleanup(Action fn) => Cleanups.Add(fn);
    public IRegistry<T> Registry<T>(string name) => null; public void Toast(string text, double sec = 3) => Logs.Add("экран: " + text);
    public byte[] ReadAsset(string path) => null; public IGameTimer Timer => new Tm(this); public IStore Store => null; public ITags Tags => null;
    public void Guard(string what, Action fn) { try { fn(); } catch (Exception e) { Logs.Add($"ОШИБКА {what}: {e}"); } }
    public void Error(string msg) => Logs.Add("ERR " + msg); public void Mark(string what) { }
    class Tm : IGameTimer { readonly Ctx _c; public Tm(Ctx c) { _c = c; } public double Now => _c.GameTime; public double Delta => 0.1; public void After(double s, Action f) { } public void Every(double s, Action f) { } }
    class Ev : ModEvents { readonly Ctx _c; public Ev(Ctx c) { _c = c; } public void On(string n, Action<object> f) { if (!_c.Handlers.TryGetValue(n, out var l)) _c.Handlers[n] = l = new(); l.Add(f); } public void Emit(string n, object p = null) { } public void On<T>(Action<T> f) { } public void Emit<T>(T p) { } }
    class Lib<T> : ILibrary<T> where T : class { readonly T _a; public Lib(T a) { _a = a; } public T For(ModContext c) => _a; }
    class En : IEnemies { public IReadOnlyList<IEnemy> Alive => new List<IEnemy>(); public IReadOnlyList<IEnemy> Near(double x, double y, double r) => new List<IEnemy>(); public IEnemy Nearest(double x, double y) => null; public Vector3? HeroPos => new Vector3(0, 0, 0); }
    public class Sp : ISpells
    {
        public IActiveSpell AddActive(ActiveSpellDef d) => null; public bool InBattle => true; public bool Paused => false;
        public static double Now;
        public int Level(int id) => id == 1 ? 3 : (id == 3 || id == 4 || id == 6) && Now >= 30 ? 2 : 0;   // Дух, Спутник, Щит «взяты» на 30-й секунде
        public bool CanCast(int id) => false; public void SetAuto(int id, bool a) { } public bool Cast(int id, int s = 2) => false; public double Cooldown(int id) => 1;
    }
}
