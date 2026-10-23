using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using ModKit;

namespace MagicSurvivalCoop.Debug
{
    /// <summary>
    /// Отладочная консоль для разработки (не для игроков): на телефоне — порт 127.0.0.1:8770, с ПК через USB:
    /// adb forward tcp:8771 tcp:8770, затем python tools/coop_dbg.py 8771 "get mgr.Time_Sec".
    /// Запросы выполняются в главном потоке игры (по нескольку за шаг таймера), ответ — текст, в конце строка «&lt;&lt;END&gt;&gt;».
    /// </summary>
    public class DebugMod : IKitMod
    {
        public ModDef Define() => new ModDef
        {
            Id = "coop_debug",
            Name = "Кооператив: отладка",
            Version = "0.1.0",
            Description = "Для разработки: консоль на порту 8770 (только через USB/adb) — читать и менять поля игры во время боя. Игрокам не нужна.",
            OnEnable = ctx => _c = new DebugConsole(ctx),
            OnDisable = ctx => { _c?.Stop(); _c = null; },
        };
        private DebugConsole _c;
    }

    internal sealed class DebugConsole
    {
        public const int Port = 8770;
        private readonly ModContext _ctx;
        private readonly Evaluator _ev = new Evaluator();
        private readonly ConcurrentQueue<(string line, ManualResetEventSlim done, string[] result)> _q = new ConcurrentQueue<(string, ManualResetEventSlim, string[])>();
        private TcpListener _listener;
        private volatile bool _stop;
        private object _mgr, _abil;
        private readonly Dictionary<string, object> _iters = new Dictionary<string, object>();
        private readonly List<Action> _unhooks = new List<Action>();

        public DebugConsole(ModContext ctx)
        {
            _ctx = ctx;
            _ev.Root = Root;
            _ev.SpriteName = SpriteName;
            _ev.IsDestroyed = Destroyed;
            _ev.Extra = Extra;
            try { ctx.Before("C_Battle_Manager", "Update", 0, (s, a) => { if (s != null) _mgr = s; }); } catch (Exception e) { ctx.Log("менеджер боя не ловится: " + e.Message); }
            try { ctx.Before("V_Ability_Manager", "AbilityStatApply", 1, (s, a) => { if (s != null) _abil = s; }); } catch { }
            ctx.Every(50, () => ctx.Guard("консоль", Pump));
            ctx.OnCleanup(Stop);
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, Port);
                _listener.Start();
                new Thread(AcceptLoop) { IsBackground = true, Name = "coop_debug" }.Start();
                ctx.Log($"отладка: консоль на 127.0.0.1:{Port} (с ПК: adb forward tcp:8771 tcp:{Port})");
            }
            catch (Exception e) { ctx.Log("отладка: порт не открыт: " + e.Message); }
        }

        public void Stop()
        {
            _stop = true;
            try { _listener?.Stop(); } catch { }
            foreach (var u in _unhooks) { try { u(); } catch { } }
            _unhooks.Clear();
        }

        // ------------------------------------------------------------ сеть (фоновый поток)

        private void AcceptLoop()
        {
            while (!_stop)
            {
                TcpClient c;
                try { c = _listener.AcceptTcpClient(); } catch { if (_stop) return; Thread.Sleep(200); continue; }
                new Thread(() => Serve(c)) { IsBackground = true }.Start();
            }
        }

        private void Serve(TcpClient c)
        {
            using (c)
            using (var s = c.GetStream())
            using (var r = new StreamReader(s, new UTF8Encoding(false)))
            using (var w = new StreamWriter(s, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
            {
                try
                {
                    string line;
                    while (!_stop && (line = r.ReadLine()) != null)
                    {
                        var done = new ManualResetEventSlim(false);
                        var res = new string[1];
                        _q.Enqueue((line, done, res));
                        string text = done.Wait(15000) ? res[0] : "ошибка: игра не ответила за 15 с (пауза? главное меню без таймера?)";
                        w.WriteLine(text);
                        w.WriteLine("<<END>>");
                    }
                }
                catch { }
            }
        }

        // ------------------------------------------------------------ главный поток

        private void Pump()
        {
            int n = 0;
            while (n++ < 8 && _q.TryDequeue(out var job))
            {
                string res;
                try { _ctx.Mark("отладка: " + job.line); res = _ev.Run(job.line); }
                catch (Exception e) { res = "ошибка: " + e.Message; }
                job.result[0] = res;
                job.done.Set();
            }
        }

        private object Root(string name)
        {
            switch (name)
            {
                case "hero": return typeof(BattleState).GetProperty("Hero", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                case "mgr": return _mgr;
                case "abil": return _abil;
                case "db": return Hooks.FindType("Z_Database");
            }
            if (name.StartsWith("T:")) return Hooks.FindType(name.Substring(2));
            if (name.StartsWith("iter:")) return _iters.TryGetValue(name.Substring(5), out var it) ? it : null;
            return null;
        }

        /// <summary>hook &lt;Тип&gt; &lt;regex&gt; — поймать объекты корутин (вложенных типов) по MoveNext; iters — что поймано.</summary>
        private string Extra(string cmd, string[] a)
        {
            if (cmd == "iters")
                return _iters.Count == 0 ? "корутины не пойманы (hook <Тип> <regex>)" : string.Join("\n", _iters.Keys.Select(k => "iter:" + k));
            if (cmd != "hook") return null;
            if (a.Length < 2) return "hook <Тип> <regex вложенного типа>, например: hook C_Battle_Manager Play_Time";
            var t = Hooks.FindType(a[0]);
            if (t == null) return "тип не найден: " + a[0];
            var rx = new Regex(a[1], RegexOptions.IgnoreCase);
            var done = new List<string>();
            foreach (var nt in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(x => rx.IsMatch(x.Name)))
            {
                var mi = nt.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (mi == null) continue;
                string key = nt.Name;
                try { _unhooks.Add(Hooks.HookBefore(_ctx.Id, mi, (self, args) => { if (self != null) _iters[key] = self; })); done.Add(key); }
                catch (Exception e) { done.Add(key + " — не вышло: " + e.Message); }
            }
            return done.Count == 0 ? "вложенных типов с MoveNext не найдено" : "хуки: " + string.Join(", ", done) + "\n(объект появится как iter:<имя> после следующего шага корутины)";
        }

        // ------------------------------------------------------------ Unity без опасных обёрток

        private static IntPtr PtrOf(object o) => o?.GetType().GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance)?.GetValue(o) is IntPtr p ? p : IntPtr.Zero;

        private static bool Destroyed(object o)
        {
            var p = PtrOf(o);
            if (p == IntPtr.Zero) return false;
            try { return Raw.IsDestroyed(p); } catch { return false; }
        }

        private static IntPtr _getSprite, _getName;
        private static string SpriteName(object sr)
        {
            try
            {
                var p = PtrOf(sr);
                if (p == IntPtr.Zero || Raw.IsDestroyed(p)) return null;
                if (_getSprite == IntPtr.Zero)
                {
                    const string core = "UnityEngine.CoreModule.dll";
                    _getSprite = Raw.Method(Raw.Class(core, "UnityEngine", "SpriteRenderer"), "get_sprite", 0);
                    _getName = Raw.Method(Raw.Class(core, "UnityEngine", "Object"), "get_name", 0);
                }
                var sp = Raw.Invoke(_getSprite, p);
                if (sp == IntPtr.Zero) return "";
                var s = Raw.Invoke(_getName, sp);
                if (s == IntPtr.Zero) return null;
                int head = 2 * IntPtr.Size, len = System.Runtime.InteropServices.Marshal.ReadInt32(s + head);
                return len <= 0 || len > 512 ? null : System.Runtime.InteropServices.Marshal.PtrToStringUni(s + head + 4, len);
            }
            catch { return null; }
        }
    }
}
