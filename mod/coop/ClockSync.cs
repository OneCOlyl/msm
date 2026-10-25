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
        private int _hostAreaChange = -1;                // смены области у хоста (AreaChangeNum; растёт при смерти его босса)
        private int _hostObelisk = -1, _hostObPrev = -1; // обелиск хоста (ApplyObeliskON): 1 → 0 — хост его поднял
        private bool _hostPicked;                        // хост поднял обелиск этого босса — гостю сразу то же (ObeliskSync.cs)
        private double _hostClockAt = -100;
        private int _clockFixes, _clockFixLogs;
        private double _clockCheckAt;
        private readonly Dictionary<string, string> _clockEvents = new Dictionary<string, string>();
        private static readonly string[] ClockEventFields = { "EnemyWaveID", "Battle_ClearWaveON", "BossWave_VisionRatio", "Transition_EnemyMax", "Transition_EnemyNum",
                                                                    "ApplyBossNum", "ApplyBossGround_Size", "ApplyObeliskON", "AreaChangeNum", "AreaChangeID", "Battle_ClearON" };

        private void ResetClock()
        {
            _hostClock = -1; _hostBossWave = 0; _hostClockAt = -100; _hostLevel = -1; _hostWave = -1; _hostBwPrev = 0;
            _clockFixes = _clockFixLogs = 0; _clockCheckAt = 0; _clockEvents.Clear();
            _hostObelisk = _hostObPrev = -1; _hostPicked = false; _bossHold = false; _clockColor = null;
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
            int ob = MyObelisk(); if (ob >= 0) w.WriteNumber("ob", ob);
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
            if (_hostBwPrev == 0 && _hostBossWave == 1) _hostPicked = false;   // новый босс у хоста — прошлый подбор не в счёт
            _hostBwPrev = _hostBossWave;
            _hostAreaChange = (int)Protocol.Num(s, "ac", -1);
            _hostObelisk = (int)Protocol.Num(s, "ob", -1);
            if (_hostObPrev == 1 && _hostObelisk == 0) _hostPicked = true;             // хост поднял обелиск
            _hostObPrev = _hostObelisk;
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
            GuestBossStep(now);
            if (_hostBossWave != 0 || MyBossWave() != 0) return;                 // босс-волну не трогаем
            // 1.6.7: номер волны и уровень врагов больше не подставляем. EnemyWaveID 5 → 6 у игры начинает босс-волну: друг
            // поднимал свой обелиск, мод снова ставил ему волну хоста 6 — босс-файт начинался повторно. Часы держат общая
            // пауза и догоняющее ускорение (SharedPause.cs).
        }

        private int MyAreaChange()
        {
            try { return Convert.ToInt32(GetAny(_battleMgr, "AreaChangeNum") ?? -1); } catch { return -1; }
        }

        private const double EnforceDone = 0.99;
        /// <summary>Череп босс-волны (BossWave_VisionRatio: 15 — виден, 0 — погас); не прочитать — 0.</summary>
        private double MyVision()
        {
            try { return Convert.ToDouble(GetAny(_battleMgr, "BossWave_VisionRatio") ?? 0.0); } catch { return 0; }
        }
        /// <summary>Вступление босс-волны (BossWave_EnforceVar, 0 → 1); не прочитать — считаем законченным.</summary>
        private double MyEnforce()
        {
            try { return Convert.ToDouble(GetAny(_battleMgr, "BossWave_EnforceVar") ?? 1.0); } catch { return 1; }
        }

        private int MyObelisk()
        {
            try { return Convert.ToInt32(GetAny(_battleMgr, "ApplyObeliskON") ?? -1); } catch { return -1; }
        }

        /// <summary>
        /// Конец босс-волны у гостя (1.6.42, ObeliskSync.cs): хост поднял обелиск этого босса, а гость позади — у него идёт своя
        /// босс-волна (или вступление), лежит обелиск, или смен области меньше, чем у хоста (до босса не дошёл). Тогда сразу:
        /// волна снята, часы как у хоста, смена области, выбор легендарного артефакта. Своё окно выбора открыто — после него.
        /// </summary>
        private void GuestBossStep(double now)
        {
            // 1.6.45: счётчик боссов держим 1 с начала боя, а не с первого обелиска хоста: иначе на 3:00 конец вступления клал
            // гостю свой обелиск, и убрать его с карты не выходило (DestroyItem и SetActive(false) — обелиск лежал до 6:00)
            if (!_bossHold) { _bossHold = true; _ctx.Log("обелиск: счётчик боссов гостя держу 1 — своего обелиска игра гостю не положит"); }
            BossHoldStep();
            NoteClockColor();
            int ac = MyAreaChange();
            if (MyObelisk() == 1 && !_hostPicked && !OwnPause())
            {
                // игра у гостя сама закончила этап (обелиск лёг): хост свой ещё не поднял — убрать и ждать его; уже поднял
                // (у гостя этот этап кончился позже) — убрать и пустить часы без второй награды
                if (_hostObelisk == 1 || _hostBossWave != 0) RemoveObelisks("игра положила его гостю, хост свой ещё не поднял");
                else try { CatchUpBoss(false); } catch (Exception e) { _ctx.Log("обелиск: " + Inner(e).Message); }
            }
            if (!_hostPicked || OwnPause()) return;
            _hostPicked = false;
            bool behind = MyBossWave() != 0 || MyObelisk() == 1 || MyVision() > 0.01 || (ac >= 0 && _hostAreaChange > ac);
            if (!behind) { _ctx.Log($"обелиск: хост поднял свой — у меня босс уже пройден (смен области {ac}, у хоста {_hostAreaChange})"); return; }
            try { CatchUpBoss(true); } catch (Exception e) { _ctx.Log("обелиск: " + Inner(e).Message); }
        }

        /// <summary>Живых боссов у себя (ApplyBossNum, статический счётчик игры); -1 — не прочитать.</summary>
        private int MyBossCount()
        {
            try { return Convert.ToInt32(GetAny(_battleMgr, "ApplyBossNum") ?? 0); } catch { return -1; }
        }

        private string ClockSummary() => _hosting || _clockFixes == 0 ? "" : $"; уровень врагов и волна подтянуты к хосту {_clockFixes} раз";
    }
}
