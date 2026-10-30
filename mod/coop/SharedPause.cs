using System;
using System.Linq;
using System.Reflection;
using ModKit;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Общая пауза (1.6.0): пока кто-то из друзей выбирает умение (или в меню паузы), игра на паузе у всех.
    /// Время боя игра берёт из часов сцены Unity (консоль 23.10: часы боя = Time.timeSinceLevelLoad − ~3 с, своего счётчика нет),
    /// а на паузе они стоят только у того, кто выбирает, — без общей паузы часы игроков расходятся на длину чужих пауз.
    /// Своя пауза — флаги игры (V_Ability_Manager.Ability_SelectON, C_Battle_Manager.IsGamePaused/IsStopGame), а не timeScale:
    /// иначе наша же пауза из-за друга выглядела бы для него как наша — и паузы держали бы друг друга.
    /// </summary>
    internal sealed partial class Session
    {
        private bool _pauseForced;
        private float _pauseSavedScale = 1;
        private double _pauseToastAt;
        private string _pauseBy;
        private PropertyInfo _timeScale;
        private double _lastPumpSec;
        private string _pauseWhy;

        private bool SharedPauseOn => _ctx.Setting("sharedPause") > 0;
        /// <summary>Только для тестов на ПК: своя пауза игрока по контексту (в тесте два «телефона» в одном процессе).</summary>
        internal static Func<ModContext, bool> OwnPauseOverride;

        /// <summary>Своя пауза игрока: открыто окно выбора умения или меню паузы.</summary>
        /// <summary>
        /// Своя пауза игрока — игра сама остановилась: окно выбора умения, сундук, меню паузы… (1.6.3). Флаги игры не годятся:
        /// Ability_SelectON остаётся True и после закрытия окна (консоль 23.10: окно закрыто, бой идёт, флаг True — друг висел
        /// на паузе), а сундук флагов не трогает вовсе. Поэтому: пока мод сам паузу не держит — своя пауза = скорость игры 0;
        /// держит — своя пауза запомнена с начала нашей паузы и кончается, когда игра попробует продолжить (скорость больше 0).
        /// </summary>
        private bool OwnPause() => OwnPauseOverride != null ? OwnPauseOverride(_ctx) : _ownNow;
        private bool _ownNow;

        private void UpdateOwnPause(float ts)
        {
            if (!_inRun) { _ownNow = false; return; }
            // 1.6.84: меню паузы (C_Battle_Manager.IsStopGame) — своя пауза, даже если мод сам держит игру на паузе: хост открыл
            // меню, пока ждал друга перед боссом, — по скорости 0 это была «пауза из-за друга», друг её своей не видел и играл
            bool menu = MenuPaused();
            if (menu != _menuWas)
            {
                _menuWas = menu;
                if (_menuLogs++ < 30) _ctx.Log($"своя пауза: меню паузы {(menu ? "открыто" : "закрыто")} (скорость {ts}, пауза из-за друга {_pauseForced})");
            }
            if (menu) { _ownNow = true; return; }
            if (!_pauseForced) _ownNow = ts == 0;
            else if (ts > 0) _ownNow = false;
        }

        private bool _menuWas;
        private int _menuLogs;

        private bool MenuPaused()
        {
            if (_battleMgr == null) return false;
            try { return GetAny(_battleMgr, "IsStopGame") is bool b && b; } catch { return false; }
        }

        private float TimeScale
        {
            get
            {
                _timeScale ??= AppDomain.CurrentDomain.GetAssemblies().Select(a => { try { return a.GetType("UnityEngine.Time"); } catch { return null; } })
                                   .FirstOrDefault(t => t != null)?.GetProperty("timeScale", BindingFlags.Public | BindingFlags.Static);
                return _timeScale?.GetValue(null) is float f ? f : 1f;
            }
            set { _timeScale?.SetValue(null, value); }
        }

        /// <summary>
        /// Каждый кадр и шаг таймера: у друга своя пауза — держим паузу и у себя; кончилась — вернуть скорость. Ещё пауза — пока
        /// друг не вошёл в бой. Разошедшиеся часы паузой не выравниваем: отстающий догоняет ускорением (CatchUpStep).
        /// </summary>
        private void SharedPauseStep()
        {
            UpdateOwnPause(TimeScale);
            // свой герой погиб — экрану смерти игры нужно, чтобы время шло: паузу из-за друга снять и больше не ставить
            // (1.6.2: хост погиб, пока стоял на паузе из-за друга, — анимация смерти ждала вечно на скорости 0)
            if (_inRun && MyHeroDead())
            {
                if (_pauseForced)
                {
                    _pauseForced = false; _pauseWhy = null;
                    TimeScale = _pauseSavedScale > 0 ? _pauseSavedScale : 1;
                    _ctx.Log("общая пауза: свой герой погиб — пауза снята");
                }
                return;
            }
            Partner by = null; string why = null;
            if (_inRun && SharedPauseOn)
            {
                // свежесть — по реальным часам: таймер мода на паузе стоит, и друг, вышедший из боя, держал бы паузу вечно (1.6.0).
                // Сколько друг выбирает, столько и ждём: до 1.6.58 пауза снималась через 60 с, и гость, пока хост думал над
                // окном выбора, играл один и убегал вперёд по часам. Зависший или отключившийся друг состояний не шлёт — его
                // отпускает проверка свежести (3 с).
                double nowR = Clock.Elapsed.TotalSeconds;
                // друг с нулевым здоровьем — погиб: его анимация смерти останавливает его игру, это не «выбирает умение»
                by = _partners.Values.FirstOrDefault(p => p.OwnPause && p.Battle && !p.Dead && p.Hp > 0 && p.HasState && nowR - p.StateReal < 3);
                if (by != null) why = "выбирает умение";
                else if (!OwnPause()) by = EntryWait(nowR, ref why);
            }
            float ts = TimeScale;
            if (by == null) CatchUpStep(ts);
            else if (_catchUp) { _catchUp = false; RestoreSpeedBonus(); }   // пауза из-за друга — ускорение не нужно (скорость 0 ставим ниже)
            if (by != null)
            {
                if (!_pauseForced || _pauseWhy != why)
                {
                    if (!_pauseForced) _pauseSavedScale = _catchUp ? _gameScale : ts > 0 ? ts : 1;
                    _pauseForced = true; _pauseBy = by.Name; _pauseWhy = why; _pauseToastAt = 0;   // новая причина — подпись сразу
                    _ctx.Log(why == "вход" ? $"вход: жду «{by.Name}» — ещё не вошёл в бой" : $"общая пауза: «{by.Name}» выбирает умение");
                }
                // игра у себя сняла свою паузу (закрыли окно выбора) — запомнить её скорость и снова остановить
                if (ts > 0) { if (!OwnPause()) _pauseSavedScale = ts; TimeScale = 0; }
                double now = Clock.Elapsed.TotalSeconds;
                if (!OwnPause() && now >= _pauseToastAt)
                {
                    _pauseToastAt = now + 3;
                    _ctx.Toast(why == "вход" ? $"Ждём «{by.Name}» — ещё не в бою…" : $"Пауза: «{by.Name}» выбирает умение", 3);
                }
                return;
            }
            if (!_pauseForced) return;
            _pauseForced = false;
            if (!OwnPause() && ts == 0) TimeScale = _pauseSavedScale;   // своё окно выбора открыто — скоростью управляет игра
            _ctx.Log(_pauseWhy == "вход" ? $"вход: все вошли в бой" : $"общая пауза: «{_pauseBy}» продолжил");
            _pauseWhy = null;
        }

        /// <summary>
        /// Отставшие часы — догнать ускорением, а не паузой у того, кто впереди (1.6.5; с 1.6.99 пауз из-за часов нет совсем:
        /// раньше ждал при разнице от 8 с, в первые 40 с боя и перед боссом). Часы расходятся на задержку общей паузы — 0,2–0,5 с
        /// за окно выбора — и на замедления игры у одного игрока. Наши часы отстают от самых быстрых часов друга на 2 с и больше —
        /// ускоряем ×1,25, от 4 с — ×1,5, от 8 с — ×2, пока не догоним. За 30 с до босс-волны — уже с 1 с и ступенью выше,
        /// чтобы волна началась у всех почти вместе. Паузу (скорость 0) не трогаем. Ускоряем через бонус скорости игры
        /// (SetSpeedBonus); нет его — прямо скоростью.
        /// </summary>
        private void CatchUpStep(float ts)
        {
            bool want = false;
            if (_inRun && SharedPauseOn && !OwnPause() && !_pauseForced && MyBossWave() == 0)
            {
                int mine = MyClock();
                double nowR = Clock.Elapsed.TotalSeconds;
                var ahead = _partners.Values.Where(p => p.Battle && !p.Dead && p.Hp > 0 && p.BattleClock >= 0 && nowR - p.StateReal < 1.5)
                                            .OrderByDescending(p => p.BattleClock).FirstOrDefault();
                if (ahead != null && mine >= 0)
                {
                    int behindBy = ahead.BattleClock - mine;
                    _catchBehind = behindBy;
                    _catchNearBoss = BossEvery - mine % BossEvery <= NearBoss;
                    want = behindBy >= (_catchUp || _catchNearBoss ? 1 : 2);
                    if (want && !_catchUp) { _catchUpBy = ahead.Name; _catchUps++; if (_catchUps <= 10) _ctx.Log($"часы: отстаю от «{ahead.Name}» на {behindBy} с — ускоряю до ×{CatchFactor()}{(_catchNearBoss ? " (скоро босс)" : "")}"); }
                }
            }
            // 1.6.7: ускоряем от текущей скорости игры (у друга она часто 0,5–0,8 — 1.6.5 ускорял только при 1 и почти не догонял).
            // Скорость, которую поставила игра, — _gameScale; ставим _gameScale × множитель. Игра поменяла скорость сама (не наше
            // значение) — запоминаем её новую и ускоряем уже от неё. Паузу (0) не трогаем.
            if (want && ts > 0)
            {
                if (SetSpeedBonus(CatchFactor())) { _catchUp = true; _catchSet = -1; return; }
                if (!_catchUp || Math.Abs(ts - _catchSet) > 0.001f) _gameScale = ts;
                _catchSet = _gameScale * CatchFactor();
                if (Math.Abs(ts - _catchSet) > 0.001f) TimeScale = _catchSet;
                _catchUp = true;
                return;
            }
            if (_catchUp && !want)
            {
                _catchUp = false;
                RestoreSpeedBonus();
                if (Math.Abs(TimeScale - _catchSet) < 0.001f) TimeScale = _gameScale;
                if (_catchUps <= 10 && !_pauseForced && !OwnPause()) _ctx.Log($"часы: догнал «{_catchUpBy}»");
            }
        }
        private static readonly float[] CatchFactors = { 1.25f, 1.5f, 2f };
        private float CatchFactor()
        {
            int level = _catchBehind >= 8 ? 2 : _catchBehind >= 4 ? 1 : 0;
            return CatchFactors[Math.Min(2, level + (_catchNearBoss ? 1 : 0))];
        }
        private const int NearBoss = 30, BossEvery = 180;
        private bool _catchNearBoss;

        // Скорость боя игра пересчитывает в каждом C_Battle_Manager.Update: ApplyTimeScale = Time.timeScale = k × TimeScaleBonus
        // (статическое поле менеджера), k = 0,1 × округлённое число от частоты кадров, от 0,01 до 1 (libil2cpp, 30.10): при низком
        // FPS игра замедляет время — потому часы гостя на слабом телефоне и отстают (Pixel 3a: k 0,5–0,8, у хоста 1). Запись мода
        // в timeScale стиралась в том же кадре (до 1.6.100 ускорение часы почти не догоняло). Ускоряем бонусом и на время догона
        // снимаем замедление: бонус = исходный × factor / k, не больше ×MaxBonus. k меняется почти каждый кадр — берём среднее
        // (по последнему кадру скорость гостя прыгала 0,2–4,7: телефоны 30.10).
        private const float MaxBonus = 4;
        private object _bonusMgr;
        private float _bonusBase, _bonusSet, _kAvg;
        /// <summary>Бонус скорости под итоговую скорость × factor. Ложь — бонуса нет (тесты): ускорять прямо скоростью.</summary>
        private bool SetSpeedBonus(float factor)
        {
            var mgr = _battleMgr;
            if (mgr == null || !(GetAny(mgr, "TimeScaleBonus") is float cur)) return false;
            if (_bonusMgr == null || Math.Abs(cur - _bonusSet) > 0.001f) _bonusBase = cur;   // игра поменяла бонус сама — ускоряем от нового
            _bonusMgr = mgr;
            float k = cur > 0 && GetAny(mgr, "ApplyTimeScale") is float applied ? applied / cur : 1;   // с бонусом прошлого кадра
            if (k < 0.1f || k > 1) k = 1;
            _kAvg = _kAvg <= 0 ? k : _kAvg + 0.2f * (k - _kAvg);
            _bonusSet = _bonusBase * Math.Min(MaxBonus, factor / _kAvg);
            if (Math.Abs(cur - _bonusSet) > 0.001f) SetMember(mgr, "TimeScaleBonus", _bonusSet);
            return true;
        }
        /// <summary>Вернуть бонус скорости, если его с нашего значения не меняла игра. Повторно — ничего не делает.</summary>
        private void RestoreSpeedBonus()
        {
            if (_bonusMgr == null) return;
            try { if (GetAny(_bonusMgr, "TimeScaleBonus") is float cur && Math.Abs(cur - _bonusSet) < 0.001f) SetMember(_bonusMgr, "TimeScaleBonus", _bonusBase); } catch { }
            _bonusMgr = null; _kAvg = 0;
        }
        private float _gameScale = 1, _catchSet;
        private int _catchBehind;
        /// <summary>
        /// 1.6.45: вход в бой — пока кто-то из комнаты ещё не в бою (меню, выбор героя, загрузка), вошедший стоит на паузе: бой
        /// у всех начинается вместе. Друг без свежего состояния (3 с — связь пропала) не держит. Все вошли или прошло
        /// EntryWaitMax реальных секунд от начала боя — до конца этого боя больше не ждём (друг, позже вышедший в меню, паузу не держит).
        /// </summary>
        private Partner EntryWait(double nowR, ref string why)
        {
            if (_entryWaitDone) return null;
            var late = _partners.Values.FirstOrDefault(p => !p.Battle && p.HasState && nowR - p.StateReal < 3);
            if (late == null || nowR - _battleStartSec > EntryWaitMax)
            {
                _entryWaitDone = true;
                if (late != null) _ctx.Log($"вход: «{late.Name}» не вошёл в бой за {EntryWaitMax} с — дальше без него");
                return null;
            }
            why = "вход";
            return late;
        }
        private const double EntryWaitMax = 120;
        private bool _entryWaitDone;

        private bool _catchUp;
        private string _catchUpBy;
        private int _catchUps;

        private bool MyHeroDead()
        {
            try { return _hero.Hp <= 0; } catch { return false; }
        }

        /// <summary>Снять всё, что мод сделал со скоростью игры: паузу из-за друга и догоняющее ускорение. Повторно — ничего не делает.</summary>
        private void ReleaseSharedPause()
        {
            RestoreSpeedBonus();
            if (_catchUp)
            {
                _catchUp = false;
                try { if (Math.Abs(TimeScale - _catchSet) < 0.001f) TimeScale = _gameScale > 0 ? _gameScale : 1; } catch { }   // скорость поменяла игра — её не трогаем
            }
            if (!_pauseForced) return;
            _pauseForced = false; _pauseWhy = null;
            try { if (TimeScale == 0 && !OwnPause()) TimeScale = _pauseSavedScale > 0 ? _pauseSavedScale : 1; } catch { }
        }
    }
}
