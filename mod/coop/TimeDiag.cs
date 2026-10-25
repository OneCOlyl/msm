using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using ModKit;
using System.Text.RegularExpressions;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Журнал времени боя (1.5.6): какие поля менеджера хранят часы боя, волну, боссов и погоду, и как они расходятся у игроков
    /// (у каждого своя пауза на выбор умения). Нужен, чтобы синхронизировать часы гостя с хостом — тогда боссы и волны совпадут.
    /// </summary>
    internal sealed partial class Session
    {
        private static readonly Regex TimeWords = new Regex("Time|Timer|Sec|Minute|Wave|Boss|Weather|Level|Stage|Clock|Play", RegexOptions.IgnoreCase);
        private double _timeDiagAt;
        private int _timeDiags;
        private bool _timeShapeLogged;
        private double _battleStartSec;

        private void InstallTimeDiag()
        {
            // только журнал — хватает события ModKit (тот же Create_UnitEnemyBoss, до вызова, синхронно: копии отсекает _spawningPuppet)
            _ctx.Events.On("bossSpawn", e => OnBossCreate(e as EventData));
        }

        private void ResetTimeDiag() { _timeDiagAt = 0; _timeDiags = 0; _battleStartSec = Clock.Elapsed.TotalSeconds; }

        private void OnBossCreate(EventData d)
        {
            if (_shutdown || _spawningPuppet || !_inRun || d == null) return;   // наши копии — не в счёт
            _ctx.Log($"время боя: игра создаёт босса — тип {d.Get<int>("unitId")}, число {d.Get<int>("count")}, через {Clock.Elapsed.TotalSeconds - _battleStartSec:0.0} с от начала боя (реальных)");
        }

        private void TimeDiagStep(double now)
        {
            var mgr = _battleMgr;
            if (mgr == null || now < _timeDiagAt || _timeDiags >= 30) return;
            _timeDiagAt = now + 30; _timeDiags++;
            if (!_timeShapeLogged)
            {
                _timeShapeLogged = true;
                const BindingFlags decl = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
                var ms = mgr.GetType().GetMethods(decl).Where(m => !m.IsSpecialName && !m.Name.StartsWith("Native") && TimeWords.IsMatch(m.Name))
                            .Select(m => $"{m.ReturnType.Name} {m.Name}/{m.GetParameters().Length}");
                _ctx.Log("время боя: методы менеджера: " + string.Join(", ", ms));
            }
            _ctx.Log($"время боя ({(_hosting ? "хост" : "гость")}, {now - _battleStartSec:0} с реальных от начала, таймер ModKit {_ctx.Timer.Now:0.0}): {TimeFields(mgr)}");
        }

        /// <summary>Простые поля менеджера (числа, флаги) с Time/Wave/Boss/Weather/Level… в имени — обычные и статические.</summary>
        private static string TimeFields(object mgr)
        {
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var parts = new List<string>();
            foreach (var p in mgr.GetType().GetProperties(any))
            {
                if (p.GetIndexParameters().Length > 0 || p.Name.StartsWith("Native") || !TimeWords.IsMatch(p.Name)) continue;
                var t = p.PropertyType;
                if (!(t.IsPrimitive || t.IsEnum)) continue;
                try
                {
                    var v = p.GetValue(p.GetGetMethod(true)?.IsStatic == true ? null : mgr);
                    parts.Add($"{p.Name}={(v is float f ? f.ToString("0.##", CultureInfo.InvariantCulture) : Convert.ToString(v, CultureInfo.InvariantCulture))}");
                }
                catch { }
            }
            return string.Join(", ", parts);
        }
    }
}
