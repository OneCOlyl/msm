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

        // Часы боя игра выводит из часов сцены Unity (Time.timeSinceLevelLoad − ~3 с; консоль 23.10) — своего счётчика нет,
        // подставленные Time_Min/Time_Sec она сразу пересчитывает. Поэтому часы держит общая пауза (SharedPause.cs), а здесь —
        // только то, что игра хранит сама: счётчик усиления врагов EnemyLevelNum, номер волны и конец босс-волны.
        private int _hostClock = -1, _hostBossWave, _hostLevel = -1, _hostWave = -1, _hostBwPrev;
        private double _forceEndAt = -1;
        // 1.6.9: смена области после победы над боссом. У гостя своя босс-волна сама не кончается (его босс — копия, гибнет вслед
        // за хостом), часы стоят. Консоль 24.10: StartCoroutine("Play_AreaChange") у гостя заканчивает босс-волну и пускает часы,
        // как у хоста после его обелиска (AreaChangeNum 0 → 1). Обелиск — только награда (выбор артефакта), с часами не связан.
        private int _hostAreaChange = -1, _areaChangesStarted, _areaChangeDoneFor;
        private bool _hostBossEnded;
        private double _areaChangeWaitFrom = -1;
        private double _hostClockAt = -100;
        private int _clockFixes, _clockFixLogs;
        private double _clockCheckAt;
        private readonly Dictionary<string, string> _clockEvents = new Dictionary<string, string>();
        private static readonly string[] ClockEventFields = { "EnemyWaveID", "Battle_ClearWaveON", "BossWave_VisionRatio", "Transition_EnemyMax", "Transition_EnemyNum",
                                                                    "ApplyBossNum", "ApplyBossGround_Size", "ApplyObeliskON", "AreaChangeNum", "AreaChangeID", "Battle_ClearON" };

        private void ResetClock()
        {
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
            int ac = MyAreaChange(); if (ac >= 0) w.WriteNumber("ac", ac);
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
            // 1.6.8: босс-волну у гостя вслед за хостом больше не завершаем — у друга выпадал второй обелиск, а часы всё равно
            // стояли до его подбора (часы идут после смены области: AreaChangeNum, наблюдатель 24.10).
            if (_hostBwPrev == 1 && _hostBossWave == 0) _hostBossEnded = true;   // хост победил своего босса — пора убить своего
            _hostBwPrev = _hostBossWave;
            _hostAreaChange = (int)Protocol.Num(s, "ac", -1);
        }

        /// <summary>4 раза в секунду: события часов в журнал (обе стороны); гость — подстройка часов под хоста.</summary>
        private void ClockStep(double now)
        {
            if (now < _clockCheckAt || _battleMgr == null) return;
            _clockCheckAt = now + 0.25;
            int mine = MyClock();
            if (mine < 0) return;
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
                _forceEndAt = -1; _hostAreaChange = -1; _areaChangeWaitFrom = -1; _areaChangesStarted = 0; _areaChangeDoneFor = 0;
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
            // 1.6.7: номер волны и уровень врагов больше не подставляем. EnemyWaveID 5 → 6 у игры начинает босс-волну: друг
            // поднимал свой обелиск, мод снова ставил ему волну хоста 6 — босс-файт начинался повторно. Часы держат общая
            // пауза и догоняющее ускорение (SharedPause.cs).
        }

        private int MyAreaChange()
        {
            try { return Convert.ToInt32(GetAny(_battleMgr, "AreaChangeNum") ?? -1); } catch { return -1; }
        }

        /// <summary>Гость: у хоста прошла смена области, а у нас нет — через 1 с запустить её у себя (один раз на каждую).</summary>
        /// <remarks>
        /// 1.6.12: вызывается каждый кадр и на паузе — в 1.6.11 проверка шла только без паузы, а хост в это время выбирал артефакт
        /// из обелиска (общая пауза держала гостя ~30 с), смена области запускалась на 34 с позже и повторно (сбивая первую,
        /// пока корутина шла на паузе) — часы гостя так и стояли. Теперь — сразу и ровно один раз на каждую смену области хоста.
        /// </remarks>
        private void AreaChangeStep(double now)
        {
            if (_hosting || !_inRun || _battleMgr == null || _hostAreaChange < 0) return;
            if (_hostAreaChange <= _areaChangeDoneFor) return;                    // эту смену области уже запускали
            int mine = MyAreaChange();
            if (mine < 0) return;
            if (mine >= _hostAreaChange) { _areaChangeDoneFor = _hostAreaChange; return; }   // своя уже прошла (подняли обелиск сами)
            if (MyBossWave() == 0 && _hostBossWave == 0 && mine + 1 < _hostAreaChange) { _areaChangeDoneFor = _hostAreaChange; return; }   // давно разошлись — не догоняем
            _areaChangeDoneFor = _hostAreaChange;
            try
            {
                _battleMgr.GetType().GetMethod("StartCoroutine", new[] { typeof(string) })?.Invoke(_battleMgr, new object[] { "Play_AreaChange" });
                _areaChangesStarted++;
                _ctx.Log($"часы боя: у хоста смена области ({_hostAreaChange}), у меня {mine} — запустил Play_AreaChange");
            }
            catch (Exception e) { _ctx.Log("часы боя: Play_AreaChange не запустить: " + Inner(e).Message); }
        }

        private string ClockSummary() => _hosting || _clockFixes == 0 ? "" : $"; уровень врагов и волна подтянуты к хосту {_clockFixes} раз";
    }
}
