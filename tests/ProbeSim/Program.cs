using System; using System.Collections.Generic; using System.IO; using System.Linq; using System.Reflection;
using Il2Cpp; using ModKit; using UnityEngine;

// Проверка разведки героя на ПК: фальшивый герой со Щитом, Спутниками и Духами.
// Культура как на телефоне (дробные через точку), иначе на русской Windows «50,10 с».
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
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

var mgr = new C_Battle_Manager();
ctx.Emit("battleStart");
for (int i = 0; i < 1300; i++)   // 130 с игрового времени, шаг 0,1 с; Армагеддон берётся на 30-й секунде
{
    ctx.GameTime += 0.1; Ctx.Sp.Now = ctx.GameTime;
    if (i % 5 == 0) ctx.Fire("Before C_Battle_AllyUnit.Bullet_Create/13", hero, new object[] { hero.Transform_ObjUnit, 1, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 1f });
    if (ctx.GameTime >= 30) { hero.IsArmageddon_P = true; hero.Obj_CooltimeBar_Armageddon.activeSelf = true;
                              C_Battle_Manager.Apply_ArmageddonDelayNum = (float)(40 - (ctx.GameTime - 10) % 40); }   // взрывы на 50-й и 90-й секунде
    if (i == 400) C_Battle_Manager.Apply_ArmageddonDelayNum += 6;   // рост не у нуля — не взрыв
    // взрыв на 50-й секунде: вспышка менеджера на 1 с, новый снаряд 77, вызов метода, счётчик
    bool boom = ctx.GameTime >= 50 && ctx.GameTime < 51;
    mgr.Obj_ArmageddonFlash.activeSelf = boom;
    if (i == 500) { ctx.Fire("Before C_Battle_AllyUnit.Armageddon_Explosion/0", hero, new object[0]); hero.ArmageddonCastNum++;
                    ctx.Fire("Before C_Battle_Manager.Play_Armageddon/0", mgr, new object[0]);
                    ctx.Fire("Before C_Battle_AllyUnit.Bullet_Create/13", hero, new object[] { hero.Transform_ObjUnit, 77, 0f, 0f, 0f, 0f, 1f, 0f, 0f, 0f, 0f, 0f, 1f }); }
    ctx.Fire("Before C_Battle_Manager.Update/0", mgr, new object[0]);
    if (i % 2 == 0) { foreach (var f in ctx.Ticks.ToArray()) f(); var bt = (string)boardField.GetValue(probeObj); if (ctx.BoardTexts.Count == 0 || ctx.BoardTexts[^1] != bt) ctx.BoardTexts.Add(bt); }
}
ctx.Emit("battleEnd");
def.OnDisable(ctx); foreach (var c in ctx.Cleanups) c();
var txt = File.Exists(report) ? File.ReadAllText(report) : "";
Console.WriteLine(txt);
foreach (var l in ctx.Logs.Where(l => l.Contains("ОШИБКА"))) Console.WriteLine("    " + l);
Check(!ctx.Logs.Any(l => l.Contains("ОШИБКА")), "без исключений");
Check(txt.Contains("Boolean IsArmageddon_P") && txt.Contains("Void Armageddon_Explosion()"), "структура героя: поля и методы Армагеддона");
Check(txt.Contains("Apply_ArmageddonEnemyHpRatio") && txt.Contains("Obj_ArmageddonFlash"), "структура менеджера боя: поля Армагеддона");
Check(txt.Contains("===== Армагеддон взят"), "отмечено, когда взят Армагеддон");
Check(txt.Contains("IsArmageddon_P = True"), "значения полей Армагеддона");
Check(System.Text.RegularExpressions.Regex.IsMatch(txt, @"\[\s*50\.\d\d с\] ВКЛ  менеджер\.Obj_ArmageddonFlash"), "включение объекта при взрыве — с временем");
Check(txt.Contains("выкл менеджер.Obj_ArmageddonFlash"), "и выключение");
Check(txt.Contains("НОВЫЙ снаряд 77"), "новый снаряд после взятия");
Check(!txt.Contains("НОВЫЙ снаряд 1 "), "старые снаряды не считаются новыми");
Check(txt.Contains("ВЫЗОВ герой.Armageddon_Explosion(0)"), "вызов метода Армагеддона — с временем");
Check(txt.Contains("ArmageddonCastNum: 0 → 1"), "изменение счётчика Армагеддона");
Check(txt.Contains("ВЫЗОВ менеджер.Play_Armageddon(0)"), "запуск корутины Армагеддона — с временем");
Check(System.Text.RegularExpressions.Regex.IsMatch(txt, @"\[\s*50\.\d\d с\] ===== ВЗРЫВ №1"), "первый взрыв — по сбросу отсчёта, с временем");
Check(System.Text.RegularExpressions.Regex.IsMatch(txt, @"\[\s*90\.\d\d с\] ===== ВЗРЫВ №2"), "второй взрыв");
Check(txt.Contains("вырос не у нуля") && !txt.Contains("ВЗРЫВ №3"), "рост отсчёта не у нуля — не взрыв");
Check(txt.Contains("] до Армагеддона "), "отсчёт до взрыва в отчёте");
Check(txt.Contains("===== разведка завершена: пойманы взрывы: 2"), "разведка закончилась после двух взрывов");
Check(ctx.Logs.Any(l => l.Contains("РАЗВЕДКА АРМАГЕДДОНА ЗАВЕРШЕНА")), "в журнале: что прислать");
Check(ctx.BoardTexts.Any(b => b.Contains("возьмите Армагеддон")) && ctx.BoardTexts.Any(b => b.Contains("ждите взрывов: 1/2")), "табло подсказывает, что делать");
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
        public int Level(int id) => id == 1 ? 3 : id == 14 && Now >= 30 ? 1 : 0;   // Армагеддон «взят» на 30-й секунде
        public bool CanCast(int id) => false; public void SetAuto(int id, bool a) { } public bool Cast(int id, int s = 2) => false; public double Cooldown(int id) => 1;
    }
}
