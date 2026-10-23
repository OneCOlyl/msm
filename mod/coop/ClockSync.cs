using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MagicSurvivalCoop.Net;
using ModKit;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Часы боя (1.5.7). Время боя — C_Battle_Manager.Time_Min / Time_Sec (корутина Play_Time); от него волны (EnemyWaveID),
    /// усиление врагов (EnemyLevelNum), погода и босс-волна (в 3:00: Battle_ClearWaveON = 1, часы стоят). У каждого игрока
    /// своя пауза на выбор умения, поэтому часы расходятся (журнал 1.5.6: на 7–9 с за 3 минуты) — и босс-волна у гостя позже.
    /// Хост шлёт свои часы и флаг босс-волны; гость в режиме общих врагов ставит себе часы хоста, если разошлись на 2 с и больше
    /// (не во время босс-волны). Обе стороны пишут в журнал смену волны и босс-волны — проверить, что события не теряются.
    /// </summary>
    internal sealed partial class Session
    {
        private const int ClockTolerance = 2;

        // Time_Min/Time_Sec и EnemyLevelNum игра каждую секунду пишет из своего счётчика (журнал 1.5.8: «подстроил 0:00 → 0:05»,
        // через секунду снова 0:01). 1.5.10 искал его в корутине Play_Time — там только служебное __1__state, и мод по ошибке писал
        // в него секунды: корутина ломалась, часы на экране вставали. 1.5.11: служебные переменные не трогаем никогда; хуки на все
        // корутины менеджера со словом Time — их переменные в журнал, счётчик правим там, где он найдётся.
        private readonly Dictionary<string, object> _timeIters = new Dictionary<string, object>();
        private readonly HashSet<string> _timeItersLogged = new HashSet<string>();
        private bool _ptHooked;
        private int _ptFixLogs;

        private void InstallPlayTimeHook()
        {
            try
            {
                var mgr = Hooks.FindType("C_Battle_Manager");
                var nested = mgr?.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(t => t.Name.Contains("Time")).ToList() ?? new List<Type>();
                foreach (var t in nested)
                {
                    var mi = t.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (mi == null) continue;
                    string name = t.Name;
                    try
                    {
                        var unhook = Hooks.HookBefore(_ctx.Id, mi, (self, a) => { if (self != null) _timeIters[name] = self; });
                        _ctx.OnCleanup(() => { try { unhook?.Invoke(); } catch { } });
                        _ptHooked = true;
                    }
                    catch (Exception e) { _ctx.Log($"часы боя: хук на {name} не поставлен: {Inner(e).Message}"); }
                }
                _ctx.Log($"часы боя: хуки на корутины: {string.Join(", ", nested.Select(t => t.Name))}");
            }
            catch (Exception e) { _ctx.Log("часы боя: хуки на корутины не поставлены: " + Inner(e).Message); }
        }

        /// <summary>Служебные переменные корутины (состояние, ссылка на менеджер) — не трогать.</summary>
        private static bool ServiceVar(string n) => n.IndexOf("state", StringComparison.OrdinalIgnoreCase) >= 0 || n.StartsWith("__") || n.StartsWith("<>")
                                                   || n.IndexOf("this", StringComparison.OrdinalIgnoreCase) >= 0 || n.StartsWith("Native");

        private static List<PropertyInfo> IterVars(object it, bool withService = false)
        {
            if (it == null) return new List<PropertyInfo>();
            return it.GetType().GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0 && p.CanWrite && (withService || !ServiceVar(p.Name))
                            && (p.PropertyType == typeof(int) || p.PropertyType == typeof(float) || p.PropertyType == typeof(double)))
                .ToList();
        }

        private static string IterDump(object it) => string.Join(", ", IterVars(it, true).Select(p => { try { return p.Name + "=" + Convert.ToString(p.GetValue(it), CultureInfo.InvariantCulture); } catch { return p.Name + "=?"; } }));

        /// <summary>Переписать в корутинах «Time» несервисные переменные, равные нашим секундам, на секунды хоста. Сколько переписано.</summary>
        private int FixPlayTime(int mine, int host)
        {
            int n = 0;
            foreach (var it in _timeIters.Values)
                foreach (var p in IterVars(it))
                {
                    double v;
                    try { v = Convert.ToDouble(p.GetValue(it)); } catch { continue; }
                    if (Math.Abs(v - mine) >= 0.999) continue;
                    try { p.SetValue(it, Convert.ChangeType(host + (v - Math.Floor(v)), p.PropertyType, CultureInfo.InvariantCulture)); n++; } catch { }
                }
            return n;
        }

        private int _hostClock = -1, _hostBossWave, _hostLevel = -1, _hostWave = -1, _hostBwPrev;
        private double _forceEndAt = -1;
        private double _hostClockAt = -100;
        private int _clockFixes, _clockFixLogs;
        private double _clockCheckAt;
        private readonly Dictionary<string, string> _clockEvents = new Dictionary<string, string>();
        private static readonly string[] ClockEventFields = { "EnemyWaveID", "Battle_ClearWaveON", "BossWave_VisionRatio" };

        private void ResetClock()
        {
            _timeIters.Clear(); _timeItersLogged.Clear(); _ptFixLogs = 0;     // корутины — новые в каждом бою
            _hostClock = -1; _hostBossWave = 0; _hostClockAt = -100; _hostLevel = -1; _hostWave = -1; _hostBwPrev = 0; _forceEndAt = -1;
            _clockFixes = _clockFixLogs = 0; _clockCheckAt = 0; _clockEvents.Clear();
        }

        /// <summary>Часы боя в секундах (-1 — не прочитать).</summary>
        private int MyClock()
        {
            var mgr = _battleMgr;
            if (mgr == null) return -1;
            try { return Convert.ToInt32(GetAny(mgr, "Time_Min")) * 60 + Convert.ToInt32(GetAny(mgr, "Time_Sec")); } catch { return -1; }
        }
        private int MyBossWave()
        {
            try { return Convert.ToInt32(GetAny(_battleMgr, "Battle_ClearWaveON") ?? 0) != 0 ? 1 : 0; } catch { return 0; }
        }

        private void WriteClock(Utf8JsonWriter w)
        {
            int c = MyClock();
            if (c < 0) return;
            w.WriteNumber("tm", c);
            w.WriteNumber("bw", MyBossWave());
            try { w.WriteNumber("lv", Convert.ToInt32(GetAny(_battleMgr, "EnemyLevelNum"))); w.WriteNumber("ew", Convert.ToInt32(GetAny(_battleMgr, "EnemyWaveID"))); } catch { }
        }

        private void ReadClock(Partner p, JsonElement s)
        {
            if (_hosting || !s.TryGetProperty("tm", out _)) return;
            if (_worldHost != null && p != _worldHost) return;
            _hostClock = (int)Protocol.Num(s, "tm", -1);
            _hostBossWave = (int)Protocol.Num(s, "bw", 0);
            _hostLevel = (int)Protocol.Num(s, "lv", -1);
            _hostWave = (int)Protocol.Num(s, "ew", -1);
            _hostClockAt = Clock.Elapsed.TotalSeconds;
            if (_hostBwPrev == 1 && _hostBossWave == 0) _forceEndAt = _hostClockAt + 1.5;   // у хоста босс-волна кончилась
            _hostBwPrev = _hostBossWave;
        }

        /// <summary>4 раза в секунду: события часов в журнал (обе стороны); гость — подстройка часов под хоста.</summary>
        private void ClockStep(double now)
        {
            if (now < _clockCheckAt || _battleMgr == null) return;
            _clockCheckAt = now + 0.25;
            int mine = MyClock();
            if (mine < 0) return;
            if (mine >= 7)
                foreach (var kv in _timeIters)
                    if (_timeItersLogged.Add(kv.Key))
                        _ctx.Log($"часы боя: переменные {kv.Key} в {mine / 60}:{mine % 60:00} (EnemyLevelNum {GetAny(_battleMgr, "EnemyLevelNum")}): {IterDump(kv.Value)}");
            foreach (var f in ClockEventFields)
            {
                string v;
                try { v = Convert.ToString(GetAny(_battleMgr, f), System.Globalization.CultureInfo.InvariantCulture); } catch { continue; }
                if (_clockEvents.TryGetValue(f, out var old) && old != v)
                    _ctx.Log($"часы боя ({(_hosting ? "хост" : "гость")}): {f} {old} → {v} в {mine / 60}:{mine % 60:00}{(_hostClock >= 0 && !_hosting ? $" (у хоста {_hostClock / 60}:{_hostClock % 60:00})" : "")}");
                _clockEvents[f] = v;
            }
            if (_hosting || !_worldActive || _ctx.Setting("clockSync") <= 0 || _hostClock < 0 || now - _hostClockAt > 1.5) return;
            // у хоста босс-волна кончилась, а у гостя идёт (своего босса у гостя нет — его убрала чистка): закончить и у гостя
            if (_forceEndAt > 0 && now >= _forceEndAt)
            {
                _forceEndAt = -1;
                if (MyBossWave() != 0 && _hostBossWave == 0)
                {
                    try
                    {
                        SetMember(_battleMgr, "Battle_ClearWaveON", 0);
                        _battleMgr.GetType().GetMethod("StartCoroutine", new[] { typeof(string) })?.Invoke(_battleMgr, new object[] { "Play_BossWaveEffect_End" });
                        _ctx.Log("часы боя: у хоста босс-волна кончилась — закончил и у себя (Play_BossWaveEffect_End)");
                    }
                    catch (Exception e) { _ctx.Log("часы боя: не закончить босс-волну: " + Inner(e).Message); }
                }
            }
            if (_hostBossWave != 0 || MyBossWave() != 0) return;                 // босс-волну не трогаем
            int diff = _hostClock - mine;
            if (Math.Abs(diff) < ClockTolerance) return;
            try
            {
                // журнал 1.5.7: Time_Min/Time_Sec игра выводит из своего счётчика секунд EnemyLevelNum (до 3:00 совпадает с ним),
                // поэтому одни Time_* она сразу возвращала назад — ставим и счётчик, и номер волны
                int fixedVars = FixPlayTime(mine, _hostClock);
                if (fixedVars == 0 && _ptFixLogs++ < 3) _ctx.Log($"часы боя: в корутинах нет переменной, равной {mine} с: {string.Join(" | ", _timeIters.Select(kv => kv.Key + ": " + IterDump(kv.Value)))}");
                if (_hostLevel >= 0) SetMember(_battleMgr, "EnemyLevelNum", _hostLevel);
                if (_hostWave >= 0) SetMember(_battleMgr, "EnemyWaveID", _hostWave);
                SetMember(_battleMgr, "Time_Min", _hostClock / 60);
                SetMember(_battleMgr, "Time_Sec", _hostClock % 60);
                _clockFixes++;
                if (_clockFixLogs++ < 15)
                    _ctx.Log($"часы боя: подстроил под хоста {mine / 60}:{mine % 60:00} → {_hostClock / 60}:{_hostClock % 60:00} ({(diff > 0 ? "+" : "")}{diff} с), переменных в корутинах {fixedVars}; волна {GetAny(_battleMgr, "EnemyWaveID")}, уровень врагов {GetAny(_battleMgr, "EnemyLevelNum")}, погода {GetAny(_battleMgr, "WeatherNum")}");
            }
            catch (Exception e) { if (_clockFixLogs++ < 15) _ctx.Log("часы боя: не подстроить: " + Inner(e).Message); }
        }

        private string ClockSummary() => _hosting || _clockFixes == 0 ? "" : $"; часы подстроены под хоста {_clockFixes} раз (хук Play_Time {(_ptHooked ? "есть" : "нет")})";
    }
}
