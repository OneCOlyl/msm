using System;
using System.Linq;
using System.Reflection;
using ModKit;

namespace MagicSurvivalCoop
{
    // Часы боя игра берёт из часов сцены Unity, и без общей паузы часы игроков расходятся на длину чужих пауз.
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
        internal static Func<ModContext, bool> OwnPauseOverride;

        // Флаги игры не годятся: Ability_SelectON остаётся True и после закрытия окна, а сундук флагов не ставит.
        private bool OwnPause() => OwnPauseOverride != null ? OwnPauseOverride(_ctx) : _ownNow;
        private bool _ownNow;

        private void UpdateOwnPause(float ts)
        {
            if (!_inRun) { _ownNow = false; return; }
            // Меню паузы своё, даже если мод сам держит паузу: по скорости 0 его не отличить от паузы из-за друга
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

        // Разошедшиеся часы выравнивает не пауза, а ускорение отстающего.
        private void SharedPauseStep()
        {
            UpdateOwnPause(TimeScale);
            // Анимации смерти нужно, чтобы время шло
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
                // Свежесть по реальным часам: таймер мода на паузе стоит. Ждём, сколько друг выбирает, а зависшего отпускает свежесть
                double nowR = Clock.Elapsed.TotalSeconds;
                // Анимация смерти друга тоже останавливает его игру
                foreach (var p in _partners.Values)
                    if (p.OwnPause && p.Battle && !p.Dead && p.Hp > 0 && p.HasState && nowR - p.StateReal < 3) { by = p; break; }
                if (by != null) why = "выбирает умение";
                else if (!OwnPause()) by = EntryWait(nowR, ref why);
            }
            float ts = TimeScale;
            if (by == null) CatchUpStep(ts);
            else if (_catchUp) { _catchUp = false; RestoreSpeedBonus(); }
            if (by != null)
            {
                if (!_pauseForced || _pauseWhy != why)
                {
                    if (!_pauseForced) _pauseSavedScale = _catchUp ? _gameScale : ts > 0 ? ts : 1;
                    _pauseForced = true; _pauseBy = by.Name; _pauseWhy = why; _pauseToastAt = 0;
                    _ctx.Log(why == "вход" ? $"вход: жду «{by.Name}» — ещё не вошёл в бой" : $"общая пауза: «{by.Name}» выбирает умение");
                }
                // Игра сняла свою паузу: запомнить её скорость и снова остановить
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
            if (!OwnPause() && ts == 0) TimeScale = _pauseSavedScale;   // При своей паузе скоростью управляет игра
            _ctx.Log(_pauseWhy == "вход" ? $"вход: все вошли в бой" : $"общая пауза: «{_pauseBy}» продолжил");
            _pauseWhy = null;
        }

        // Перед босс-волной ускоряем раньше и сильнее, чтобы волна началась у всех почти вместе.
        private void CatchUpStep(float ts)
        {
            bool want = false;
            if (_inRun && SharedPauseOn && !OwnPause() && !_pauseForced && MyBossWave() == 0)
            {
                int mine = MyClock();
                double nowR = Clock.Elapsed.TotalSeconds;
                Partner ahead = null;
                foreach (var p in _partners.Values)
                    if (p.Battle && !p.Dead && p.Hp > 0 && p.BattleClock >= 0 && nowR - p.StateReal < 1.5 && (ahead == null || p.BattleClock > ahead.BattleClock))
                        ahead = p;
                if (ahead != null && mine >= 0)
                {
                    int behindBy = ahead.BattleClock - mine;
                    _catchBehind = behindBy;
                    _catchNearBoss = BossEvery - mine % BossEvery <= NearBoss;
                    want = behindBy >= (_catchUp || _catchNearBoss ? 1 : 2);
                    if (want && !_catchUp) { _catchUpBy = ahead.Name; _catchUps++; if (_catchUps <= 10) _ctx.Log($"часы: отстаю от «{ahead.Name}» на {behindBy} с — ускоряю до ×{CatchFactor()}{(_catchNearBoss ? " (скоро босс)" : "")}"); }
                }
            }
            // Без бонуса скорости ускоряем от скорости игры _gameScale. Игра поменяла её сама: ускоряем уже от новой
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

        // Каждый C_Battle_Manager.Update ставит Time.timeScale = k × TimeScaleBonus, где k от 0,01 до 1 растёт с частотой кадров.
        // Поэтому часы слабого телефона отстают, а запись в timeScale стирается в том же кадре. Ускоряем бонусом и на время догона
        // снимаем замедление: бонус = исходный × factor / k. k прыгает почти каждый кадр, берём среднее
        private const float MaxBonus = 4;
        private object _bonusMgr;
        private float _bonusBase, _bonusSet, _kAvg;
        private bool SetSpeedBonus(float factor)
        {
            var mgr = _battleMgr;
            if (mgr == null || !(GetAny(mgr, "TimeScaleBonus") is float cur)) return false;
            if (_bonusMgr == null || Math.Abs(cur - _bonusSet) > 0.001f) _bonusBase = cur;   // Игра поменяла бонус сама
            _bonusMgr = mgr;
            float k = cur > 0 && GetAny(mgr, "ApplyTimeScale") is float applied ? applied / cur : 1;   // С бонусом прошлого кадра
            if (k < 0.1f || k > 1) k = 1;
            _kAvg = _kAvg <= 0 ? k : _kAvg + 0.2f * (k - _kAvg);
            _bonusSet = _bonusBase * Math.Min(MaxBonus, factor / _kAvg);
            if (Math.Abs(cur - _bonusSet) > 0.001f) SetMember(mgr, "TimeScaleBonus", _bonusSet);
            return true;
        }
        // Возвращает бонус, только если игра его не меняла.
        private void RestoreSpeedBonus()
        {
            if (_bonusMgr == null) return;
            try { if (GetAny(_bonusMgr, "TimeScaleBonus") is float cur && Math.Abs(cur - _bonusSet) < 0.001f) SetMember(_bonusMgr, "TimeScaleBonus", _bonusBase); } catch { }
            _bonusMgr = null; _kAvg = 0;
        }
        private float _gameScale = 1, _catchSet;
        private int _catchBehind;
        // Вошедший ждёт остальных, чтобы бой у всех начался вместе.
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
        private double _battleStartSec;

        private bool _catchUp;
        private string _catchUpBy;
        private int _catchUps;

        private bool MyHeroDead()
        {
            try { return _hero.Hp <= 0; } catch { return false; }
        }

        // Повторный вызов ничего не делает.
        private void ReleaseSharedPause()
        {
            RestoreSpeedBonus();
            if (_catchUp)
            {
                _catchUp = false;
                try { if (Math.Abs(TimeScale - _catchSet) < 0.001f) TimeScale = _gameScale > 0 ? _gameScale : 1; } catch { }   // Скорость, поменянную игрой, не трогаем
            }
            if (!_pauseForced) return;
            _pauseForced = false; _pauseWhy = null;
            try { if (TimeScale == 0 && !OwnPause()) TimeScale = _pauseSavedScale > 0 ? _pauseSavedScale : 1; } catch { }
        }
    }
}
