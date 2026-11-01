using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModKit;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Журнал корутин часов (1.6.114): у гостя на Terra часы шли по 2 с и не вставали на босс-волне (телефоны, 01.11) — похоже,
    /// работали две Play_Time. Пишем каждое создание Play_Time (со стеком: зовёт ли его мод) и момент, когда шагают две сразу.
    /// </summary>
    internal sealed partial class Session
    {
        private readonly Dictionary<object, double> _clockIters = new Dictionary<object, double>();
        private int _clockCreated, _clockDoubleLogs;

        private void InstallClockDiag()
        {
            try { _ctx.After("C_Battle_Manager", "Play_Time", 0, Hook("часы: Play_Time", (self, a) => OnPlayTimeCreated())); }
            catch (Exception e) { _ctx.Log("часы: хук Play_Time недоступен: " + e.Message); }
            Type t = null;
            try { t = Hooks.FindType("C_Battle_Manager"); } catch { }
            var nt = t?.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).FirstOrDefault(x => x.Name.Contains("Play_Time"));
            var mi = nt?.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (mi == null) return;
            try { _ctx.OnCleanup(Hooks.HookBefore(_ctx.Id, mi, Hook("часы: шаг Play_Time", (self, a) => OnPlayTimeStep(self)))); }
            catch (Exception e) { _ctx.Log("часы: хук шага Play_Time недоступен: " + e.Message); }
        }

        private void ResetClockDiag() { _clockIters.Clear(); _clockCreated = 0; _clockDoubleLogs = 0; }

        private void OnPlayTimeCreated()
        {
            if (_shutdown || !_inRun) return;
            _clockCreated++;
            var mod = new System.Diagnostics.StackTrace().GetFrames()?.Select(f => f.GetMethod()).Where(m => m?.DeclaringType?.Namespace == "MagicSurvivalCoop")
                .Select(m => m.Name).Where(n => n != nameof(OnPlayTimeCreated) && !n.StartsWith("<")).Distinct().Take(6).ToArray();
            _ctx.Log($"часы: игра создала Play_Time (за бой {_clockCreated}) в {MyClock()} с, босс-волна {MyBossWave()}, обелиск {MyObelisk()}, смен области {MyAreaChange()}; " +
                     (mod != null && mod.Length > 0 ? "из мода: " + string.Join(" ← ", mod) : "не из мода"));
        }

        private void OnPlayTimeStep(object it)
        {
            if (_shutdown || !_inRun || it == null) return;
            double now = Clock.Elapsed.TotalSeconds;
            var key = KeyOf(it);
            _clockIters[key] = now;
            int live = 0;
            foreach (var at in _clockIters.Values) if (now - at < 3) live++;
            if (live > 1 && _clockDoubleLogs++ < 5)
                _ctx.Log($"часы: шагают {live} корутины Play_Time сразу — часы {MyClock()} с, босс-волна {MyBossWave()}");
        }
    }
}
