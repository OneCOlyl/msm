using System; using System.Collections.Generic; using System.Linq; using System.Reflection;
using Il2Cpp; using ModKit; using ModKitData;

int passes = 0, fails = 0;
void Check(bool ok, string what) { if (ok) { passes++; Console.WriteLine("  PASS " + what); } else { fails++; Console.WriteLine("  FAIL " + what); } }
Z_Database.Recalc();
var ctx = new Ctx(); var def = new CoopCheat.CheatMod().Define(); def.OnEnable(ctx);
var cheat = def.OnEnable.Target.GetType().GetField("_c", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(def.OnEnable.Target);
void Press(int id, string name) => cheat.GetType().GetMethod("Press", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(cheat, new object[] { id, name });
void Minus(bool on) => cheat.GetType().GetField("_minus", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(cheat, on);

Console.WriteLine("1. игра собирает уровни из списка выученного");
Press(6, "Щит"); Press(6, "Щит");
Check(Z_Database.List_AllyMagicLevel[6] == 2 && ctx.Logs.Any(l => l.Contains("способ: список выученного")), $"Щит 0 → 2 через список выученного: {Z_Database.List_AllyMagicLevel[6]}");
Z_Database.Recalc();
Check(Z_Database.List_AllyMagicLevel[6] == 2, "уровень переживает следующий пересчёт игры");
Minus(true); Press(6, "Щит"); Minus(false);
Check(Z_Database.List_AllyMagicLevel[6] == 1, "режим «−»: Щит 2 → 1");
for (int i = 0; i < 10; i++) Press(4, "Спутник");
Check(Z_Database.List_AllyMagicLevel[4] == 7 && ctx.Toasts.Any(t => t.Contains("уже максимум")), $"не выше максимума (7): {Z_Database.List_AllyMagicLevel[4]}");
Minus(true); Press(17, "Метель"); Minus(false);
Check(ctx.Toasts.Any(t => t.Contains("нет уровней")), "нельзя снять уровень у неизученного");

Console.WriteLine("2. пересчёт не трогает уровни — запасной путь");
Z_Database.RecalcFromLearn = false;
int learnBefore = Z_Database.List_AllyAbilityLearn.Count;
Press(12, "Вихрь");
Check(Z_Database.List_AllyMagicLevel[12] == 1 && ctx.Logs.Any(l => l.Contains("Вихрь") && l.Contains("прямая запись")), "Вихрь 0 → 1 прямой записью");
Check(Z_Database.List_AllyAbilityLearn.Count == learnBefore, "неудачная запись в список выученного откатана");

Console.WriteLine("2б. как на телефоне: пересчёт библиотеки не работает, менеджер способностей ловится хуком");
Ctx.Fx.Works = false;
var mgr = new V_Ability_Manager();
ctx.Fire("Before V_Ability_Manager.AbilityStatApply/1", mgr);
V_Ability_Manager.BeforeStartInformationB = () => ctx.Fire("Before C_Battle_AllyUnit.StartInformation_B/0", null);
Z_Database.RecalcFromLearn = true;
Press(9, "Цунами");
Check(Z_Database.List_AllyMagicLevel[9] == 1 && V_Ability_Manager.Started.Contains(9), "Цунами 0 → 1, и игра запустила заклинание");
Z_Database.RecalcFromLearn = false;                                    // игра не берёт уровни из списка выученного
Press(19, "Маг. круг");
Check(Z_Database.List_AllyMagicLevel[19] == 1 && V_Ability_Manager.Started.Contains(19), "Маг. круг прямой записью — тоже запущен (уровень выставлен перед StartInformation_B)");
Z_Database.RecalcFromLearn = true; Z_Database.Recalc();                 // следующий пересчёт игры собрал уровни заново
mgr.AbilityStatApply(2);
Check(Z_Database.List_AllyMagicLevel[19] == 1, "выданный прямой записью уровень держится и после пересчётов игры");

Console.WriteLine("2г. вкладки: способность — номер в список выученного; комбо — его составляющие");
{
    var fl = BindingFlags.NonPublic | BindingFlags.Instance; var ct = cheat.GetType();
    var rowT = ct.GetNestedType("Row", fl); var tabT = ct.GetNestedType("TabDef", fl);
    object MakeRow(int id, string name, int[] req) { var r = Activator.CreateInstance(rowT); rowT.GetField("Id").SetValue(r, id); rowT.GetField("Name").SetValue(r, name); rowT.GetField("Req").SetValue(r, req); return r; }
    var tabs = (System.Collections.IList)ct.GetField("_tabs", fl).GetValue(cheat);
    ct.GetMethod("LoadTables", fl).Invoke(cheat, null);                     // вкладка «Магии» (таблиц игры в тесте нет)
    var ab = Activator.CreateInstance(tabT); tabT.GetField("Label").SetValue(ab, "Особенн."); tabT.GetField("Kind").SetValue(ab, "ability"); tabs.Add(ab);
    var cb = Activator.CreateInstance(tabT); tabT.GetField("Label").SetValue(cb, "Комбо"); tabT.GetField("Kind").SetValue(cb, "combo"); tabs.Add(cb);
    void PressRow(int tab, object row) { ct.GetField("_tab", fl).SetValue(cheat, tab); ct.GetMethod("PressRow", fl).Invoke(cheat, new[] { row }); }
    Z_Database.RecalcFromLearn = true;
    PressRow(tabs.Count - 2, MakeRow(417, "Trait 417", null));
    Check(Z_Database.List_AllyAbilityLearn.Count(x => x == 417) == 1, "способность 417 выучена");
    PressRow(tabs.Count - 1, MakeRow(1, "Empyrean Wrath", new[] { 417, 444 }));
    Check(Z_Database.List_AllyAbilityLearn.Count(x => x == 417) == 1 && Z_Database.List_AllyAbilityLearn.Count(x => x == 444) == 1, "комбо: выучена недостающая составляющая 444, 417 не задвоена");
    Check(Z_Database.List_AllyAbilityLearn.Count(x => x == 1) == 0 || Z_Database.List_AllyMagicLevel[1] == 1, "номер комбо (1) в список выученного не попал — Пуля не выросла");
    Minus(true); PressRow(tabs.Count - 1, MakeRow(1, "Empyrean Wrath", new[] { 417, 444 })); Minus(false);
    Check(!Z_Database.List_AllyAbilityLearn.Contains(417) && !Z_Database.List_AllyAbilityLearn.Contains(444), "режим «−» снимает составляющие");
    var art = Activator.CreateInstance(tabT); tabT.GetField("Label").SetValue(art, "Артефакты"); tabT.GetField("Kind").SetValue(art, "artifact"); tabs.Add(art);
    var syn = Activator.CreateInstance(tabT); tabT.GetField("Label").SetValue(syn, "Синергии"); tabT.GetField("Kind").SetValue(syn, "syn"); tabs.Add(syn);
    var maxes = (Dictionary<int, int>)ct.GetField("_abilityMax", fl).GetValue(cheat); maxes[417] = 2;
    for (int i = 0; i < 4; i++) PressRow(tabs.Count - 4, MakeRow(417, "Trait 417", null));
    Check(Z_Database.List_AllyAbilityLearn.Count(x => x == 417) == 2, "способность не выше 최대레벨 (2)");
    PressRow(tabs.Count - 2, MakeRow(41, "ASI", null)); PressRow(tabs.Count - 2, MakeRow(41, "ASI", null));
    Check(Z_Database.List_ArtifactObtain.Count(x => x == 41) == 1 && !Z_Database.List_AllyAbilityLearn.Contains(41), "артефакт — один раз и в список артефактов");
    PressRow(tabs.Count - 1, MakeRow(5, "Avalon", new[] { 41, 73, 109 }));
    Check(new[] { 41, 73, 109 }.All(x => Z_Database.List_ArtifactObtain.Count(y => y == x) == 1), "синергия: недостающие артефакты в список артефактов");
    Z_Database.List_AllyAbilityLearn.RemoveAll(x => x == 417);
    {
        var sortTab = Activator.CreateInstance(tabT); tabT.GetField("Kind").SetValue(sortTab, "artifact");
        var rowsF = tabT.GetField("Rows"); var rows = (System.Collections.IList)rowsF.GetValue(sortTab);
        foreach (var id in new[] { 99, 41, 43, 42 }) rows.Add(MakeRow(id, "A" + id, null));
        ct.GetMethod("SortByGrade", fl).Invoke(cheat, new[] { sortTab });
        var order = ((System.Collections.IEnumerable)rowsF.GetValue(sortTab)).Cast<object>().Select(r => (int)rowT.GetField("Id").GetValue(r)).ToList();
        Check(string.Join(",", order) == "43,42,41,99", $"артефакты от редких к частым, неизвестные в конце: {string.Join(",", order)}");
    }
    ct.GetField("_tab", fl).SetValue(cheat, 0);
}

Console.WriteLine("2в. уровень героя +1: недостающая мана одним вызовом, окно выбора открывается сразу");
{
    var t = cheat.GetType(); var fl = BindingFlags.NonPublic | BindingFlags.Instance;
    t.GetField("_abilityMgr", fl).SetValue(cheat, new V_Ability_Manager());
    Z_Database.AllyUnitGlobal_ManaCost = 713; Z_Database.AllyUnitGlobal_ManaStock = 74;
    t.GetMethod("StartLevelUp", fl).Invoke(cheat, null);
    Check(V_Ability_Manager.Canvas.activeSelf && V_Ability_Manager.ManaCalls == 1, $"окно выбора открылось сразу, вызовов ManaGain {V_Ability_Manager.ManaCalls}");
    Check(Z_Database.AllyUnitGlobal_ManaStock < 1, $"лишней маны нет: осталось {Z_Database.AllyUnitGlobal_ManaStock}");
    t.GetMethod("LevelStep", fl).Invoke(cheat, null);
    int calls = V_Ability_Manager.ManaCalls;
    t.GetMethod("StartLevelUp", fl).Invoke(cheat, null);                 // окно ещё открыто — второй уровень в очередь
    t.GetMethod("LevelStep", fl).Invoke(cheat, null);
    Check(V_Ability_Manager.ManaCalls == calls, "пока окно открыто, мана не выдаётся");
    V_Ability_Manager.Canvas.SetActive(false);                           // игрок выбрал заклинание, а Ability_SelectON остался True
    for (int i = 0; i < 40 && !V_Ability_Manager.Canvas.activeSelf; i++) t.GetMethod("LevelStep", fl).Invoke(cheat, null);
    Check(V_Ability_Manager.Canvas.activeSelf, "второй уровень из очереди — окно снова открылось");
    Check(ctx.Logs.Any(l => l.Contains("окно выбора открылось")), "в журнале: сколько маны понадобилось");
    V_Ability_Manager.Canvas.SetActive(false); V_Ability_Manager.Ability_SelectON = false;
}

Console.WriteLine("3. вне боя");
Console.WriteLine("6. все магии — максимум одним нажатием");
{
    var fl = BindingFlags.NonPublic | BindingFlags.Instance; var ct = cheat.GetType();
    Z_Database.RecalcFromLearn = true;
    ct.GetMethod("MaxAll", fl).Invoke(cheat, null);
    int Lv(int id) => Z_Database.List_AllyMagicLevel[id];
    int Mx(int id) => (int)ct.GetMethod("Max", fl).Invoke(cheat, new object[] { id });
    var notMax = Enumerable.Range(1, 21).Where(id => Mx(id) > 0 && Lv(id) < Mx(id)).ToList();
    Check(notMax.Count == 0, $"все заклинания на максимуме (не дотянули: {string.Join(",", notMax)})");
    Check(ctx.Toasts.Any(t => t.StartsWith("Все магии на максимуме")), "показано «Все магии на максимуме»");
    ct.GetMethod("MaxAll", fl).Invoke(cheat, null);
    Check(ctx.Toasts.Last().Contains("уже на максимуме"), "повторное нажатие — «уже на максимуме»");
}
ctx.Sp.InBattle = false; Press(1, "Пуля");
Check(ctx.Toasts.Any(t => t.Contains("Только в бою")), "вне боя не выдаёт");
foreach (var l in ctx.Logs) Console.WriteLine("    журнал: " + l);
Console.WriteLine($"\nИТОГ: {passes} PASS, {fails} FAIL");
return fails == 0 ? 0 : 1;

class Ctx : ModContext
{
    public readonly List<string> Logs = new(), Toasts = new(); public readonly Sp Sp = new();
    public string Id => "coop_cheat"; public IReadOnlyDictionary<string, double> Settings => new Dictionary<string, double>(); public double Setting(string k) => 0; public void SetSetting(string k, double v) { }
    public void Log(string m) => Logs.Add(m); public void Warn(string m) => Logs.Add("WARN " + m);
    public ModEvents Events => new Ev(); public void Expose(object api) { }
    public T Mod<T>(string id) where T : class =>
        typeof(T) == typeof(ILibrary<ISpells>) ? new Lib<ISpells>(Sp) as T :
        typeof(T) == typeof(ILibrary<ISpellsData>) ? new Lib<ISpellsData>(new SD()) as T :
        typeof(T) == typeof(ILibrary<IEffects>) ? new Lib<IEffects>(new Fx()) as T : null;
    public void Replace(string c, string m, int n, Func<object, object[], object> f) { }
    public readonly Dictionary<string, List<Action<object, object[]>>> Hooks = new();
    public void After(string c, string m, int n, Action<object, object[]> f) { }
    public void Before(string c, string m, int n, Action<object, object[]> f) { var k = $"Before {c}.{m}/{n}"; if (!Hooks.TryGetValue(k, out var l)) Hooks[k] = l = new(); l.Add(f); }
    public void Fire(string k, object self) { if (Hooks.TryGetValue(k, out var l)) foreach (var h in l) h(self, new object[0]); }
    public void Every(double ms, Action fn) { } public void OnCleanup(Action fn) { }
    public IRegistry<T> Registry<T>(string name) => null; public void Toast(string text, double sec = 3) => Toasts.Add(text);
    public byte[] ReadAsset(string path) => null; public IGameTimer Timer => null; public IStore Store => null; public ITags Tags => null;
    public void Guard(string what, Action fn) { try { fn(); } catch (Exception e) { Logs.Add($"ОШИБКА {what}: {e.Message}"); } }
    public void Error(string msg) => Logs.Add("ERR " + msg); public void Mark(string what) { }
    class Ev : ModEvents { public void On(string n, Action<object> f) { } public void Emit(string n, object p = null) { } public void On<T>(Action<T> f) { } public void Emit<T>(T p) { } }
    class Lib<T> : ILibrary<T> where T : class { readonly T _a; public Lib(T a) { _a = a; } public T For(ModContext c) => _a; }
    class SD : ISpellsData { public SpellInfo Get(int id) => new SpellInfo { Id = id, MaxLevel = 7 }; public void Edit(int id, SpellChanges c) { } public void Scale(int id, int e, double f) { } }
    public class Fx : IEffects
    {
        public static bool Works = true;
        public void Define(EffectDef def) { } public void SetGlobal((int Id, double Value)[] pairs) { } public void OnHeroPass(Action<IHeroView> fn) { } public void UseBuiltin() { }
        public bool Recalc() { if (!Works) return false; Z_Database.Recalc(); return true; }
    }
}
class Sp : ISpells
{
    public bool InBattle { get; set; } = true;
    public IActiveSpell AddActive(ActiveSpellDef d) => null; public bool Paused => false;
    public int Level(int id) => Z_Database.List_AllyMagicLevel[id]; public bool CanCast(int id) => false; public void SetAuto(int id, bool a) { } public bool Cast(int id, int s = 2) => false; public double Cooldown(int id) => 1;
}
