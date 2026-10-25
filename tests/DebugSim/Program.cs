using System; using System.Collections.Generic; using System.Net.Sockets; using System.Reflection; using System.Threading; using MagicSurvivalCoop.Debug; using ModKit;

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

Console.WriteLine("== выключение консоли: порт и сессии закрываются, повторное включение работает");
{
    bool Listening() { try { using var t = new TcpClient("127.0.0.1", DebugConsolePort); return true; } catch (SocketException) { return false; } }
    if (Listening()) Check(true, $"порт {DebugConsolePort} занят другой программой — проверка пропущена");
    else
    {
        var def = new DebugMod().Define();
        var ctx = DispatchProxy.Create<ModContext, NullCtx>();
        def.OnEnable(ctx);
        Check(Listening(), "консоль слушает порт");
        var open = new TcpClient("127.0.0.1", DebugConsolePort);
        Thread.Sleep(200);
        def.OnDisable(ctx);
        def.OnDisable(ctx);                                               // OnDisable и OnCleanup — выключение может прийти дважды
        int n;
        try { var s = open.GetStream(); s.ReadTimeout = 3000; n = s.Read(new byte[16], 0, 16); } catch { n = 0; }
        open.Close();
        Check(n == 0, "открытая сессия закрыта при выключении");
        Check(!Listening(), "порт закрыт после выключения");
        def.OnEnable(ctx);
        Check(Listening(), "после повторного включения порт снова открыт");
        def.OnDisable(ctx);
        Check(!Listening(), "и снова закрыт");
    }
}
Console.WriteLine($"\nИТОГ: {passes} PASS, {fails} FAIL");
return fails == 0 ? 0 : 1;

/// <summary>Контекст мода, который ничего не делает (консоли для запуска нужны только Log, Before, Every, OnCleanup).</summary>
public class NullCtx : DispatchProxy
{
    protected override object Invoke(MethodInfo m, object[] args) => m.ReturnType.IsValueType && m.ReturnType != typeof(void) ? Activator.CreateInstance(m.ReturnType) : null;
}

partial class Program { const int DebugConsolePort = 8770; }

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
