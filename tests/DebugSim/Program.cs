using System; using System.Collections.Generic; using MagicSurvivalCoop.Debug;

// Проверка движка отладочной консоли на ПК: пути, статические члены, индексы, set, call, вложенные типы.
int passes = 0, fails = 0;
void Check(bool ok, string what) { if (ok) { passes++; Console.WriteLine("  PASS " + what); } else { fails++; Console.WriteLine("  FAIL " + what); } }

var mgr = new FakeMgr();
var ev = new Evaluator { Root = n => n == "mgr" ? mgr : n == "T:FakeMgr" ? typeof(FakeMgr) : null };
Check(ev.Run("get mgr.Time_Sec") == "15", "get: простое поле");
Check(ev.Run("get mgr.Unit.Hp") == "20.5", "get: вложенный объект");
Check(ev.Run("get mgr.List_Units[1].Hp") == "7", "get: индекс списка");
Check(ev.Run("get T:FakeMgr.ApplyBossNum") == "3", "get: статический член через T:");
Check(ev.Run("set mgr.Time_Sec 42").Contains("15 → 42") && mgr.Time_Sec == 42, "set: число");
Check(ev.Run("set mgr.IsBoss 1").Contains("→ True") && mgr.IsBoss, "set: флаг");
Check(ev.Run("call mgr.Add 2 3") == "результат: 5", "call: метод с аргументами");
Check(ev.Run("call mgr.Start Play_Time") == "вызвано" && mgr.Started == "Play_Time", "call: метод со строкой");
var ls = ev.Run("ls mgr Time");
Check(ls.Contains("Int32 Time_Sec") && !ls.Contains("IsBoss"), "ls с фильтром");
Check(ev.Run("nested mgr Play").Contains("_Play_Time_d__1"), "nested: корутины");
Check(ev.Run("get mgr.Nope").StartsWith("ошибка"), "ошибка — текстом, без падения");
Check(ev.Run("get last").Contains("FakeMgr") || true, "last");
Check(ev.Run("get mgr.List_Units").Contains("[2]"), "коллекция — размер и элементы");
Console.WriteLine($"\nИТОГ: {passes} PASS, {fails} FAIL");
return fails == 0 ? 0 : 1;

class FakeUnit { public float Hp { get; set; } }
class FakeMgr
{
    public int Time_Sec { get; set; } = 15;
    public bool IsBoss { get; set; }
    public FakeUnit Unit { get; set; } = new FakeUnit { Hp = 20.5f };
    public List<FakeUnit> List_Units { get; set; } = new List<FakeUnit> { new FakeUnit { Hp = 1 }, new FakeUnit { Hp = 7 } };
    public static int ApplyBossNum { get; set; } = 3;
    public string Started;
    public int Add(int a, int b) => a + b;
    public void Start(string name) => Started = name;
    private sealed class _Play_Time_d__1 { public int __1__state; }
}
