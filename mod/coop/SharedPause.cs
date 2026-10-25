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
        private const double MaxPartnerPause = 60;          // дольше друг «выбирает» (меню паузы, завис) — не держим паузу у всех

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
            if (!_pauseForced) _ownNow = ts == 0;
            else if (ts > 0) _ownNow = false;
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
        /// Каждый кадр и шаг таймера: у друга своя пауза — держим паузу и у себя; кончилась — вернуть скорость.
        /// 1.6.1: «кто впереди — ждёт» — общая пауза не даёт часам расходиться, но разница от старта боя оставалась (консоль: друг
        /// вошёл в бой на 5 с позже — 0:14 против 0:09). Если наши часы обгоняют часы друга на 2 с и больше, ждём, пока догонит.
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
                // Своя пауза друга дольше MaxPartnerPause (меню паузы, завис) — не ждём.
                double nowR = Clock.Elapsed.TotalSeconds;
                // друг с нулевым здоровьем — погиб: его анимация смерти останавливает его игру, это не «выбирает умение»
                by = _partners.Values.FirstOrDefault(p => p.OwnPause && p.Battle && !p.Dead && p.Hp > 0 && p.HasState && nowR - p.StateReal < 3
                                                          && (p.OwnPauseSince < 0 || nowR - p.OwnPauseSince < MaxPartnerPause));
                if (by != null) why = "выбирает умение";
                else if (!OwnPause()) by = StartWait(nowR, ref why) ?? BossApproachWait(nowR, ref why) ?? GapWait(nowR, ref why);   // 1.6.16: ожидание «после босса» убрано — ставило хоста на паузу, пока его обелиск лежал не поднятым (часы и так стоят до подбора)
            }
            float ts = TimeScale;
            if (by == null) CatchUpStep(ts);
            else if (_catchUp) { _catchUp = false; }                    // пауза из-за друга — ускорение не нужно (скорость 0 ставим ниже)
            if (by != null)
            {
                if (!_pauseForced || _pauseWhy != why)
                {
                    if (!_pauseForced) _pauseSavedScale = _catchUp ? _gameScale : ts > 0 ? ts : 1;
                    _pauseForced = true; _pauseBy = by.Name; _pauseWhy = why; _pauseToastAt = 0;   // новая причина — подпись сразу
                    _ctx.Log(why == "старт" ? $"старт: жду «{by.Name}» ({MyClock()} с против {by.BattleClock} с)"
                           : why == "босс" ? $"босс: жду «{by.Name}» — у него ещё бой с боссом"
                           : why == "часы" ? $"часы: мои впереди «{by.Name}» на {MyClock() - by.BattleClock} с — жду"
                           : why == "к боссу" ? $"к боссу: жду «{by.Name}» ({MyClock()} с против {by.BattleClock} с), чтобы босс-волна началась у всех вместе"
                           : $"общая пауза: «{by.Name}» выбирает умение");
                }
                // игра у себя сняла свою паузу (закрыли окно выбора) — запомнить её скорость и снова остановить
                if (ts > 0) { if (!OwnPause()) _pauseSavedScale = ts; TimeScale = 0; }
                double now = Clock.Elapsed.TotalSeconds;
                if (!OwnPause() && now >= _pauseToastAt)
                {
                    _pauseToastAt = now + 3;
                    _ctx.Toast(why == "старт" ? $"Ждём «{by.Name}»…"
                             : why == "босс" ? $"«{by.Name}» добивает босса…"
                             : why == "часы" ? $"Ждём «{by.Name}» (часы)…"
                             : why == "к боссу" ? $"Ждём «{by.Name}» перед боссом…"
                             : $"Пауза: «{by.Name}» выбирает умение", 3);
                }
                return;
            }
            if (!_pauseForced) return;
            _pauseForced = false;
            if (!OwnPause() && ts == 0) TimeScale = _pauseSavedScale;   // своё окно выбора открыто — скоростью управляет игра
            _ctx.Log(_pauseWhy == "старт" || _pauseWhy == "часы" || _pauseWhy == "к боссу" ? $"{_pauseWhy}: «{_pauseBy}» догнал" : _pauseWhy == "босс" ? $"босс: «{_pauseBy}» закончил бой с боссом" : $"общая пауза: «{_pauseBy}» продолжил");
            _pauseWhy = null;
        }

        /// <summary>
        /// Отставшие часы — догнать ускорением, а не паузой у того, кто впереди (1.6.5: паузы «Синхронизация часов» раз в
        /// 10–15 с раздражали; часы расходятся на задержку общей паузы — 0,2–0,5 с за окно выбора — и на замедления игры у
        /// одного игрока). Наши часы отстают от самых быстрых часов друга на 2 с и больше, игра идёт с обычной скоростью 1 —
        /// ставим 1,25, пока не догоним. Свою скорость игра поменяла (замедление, пауза) — не трогаем.
        /// </summary>
        private void CatchUpStep(float ts)
        {
            const float Fast = 1.25f;
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
                    want = _catchUp ? behindBy >= 1 : behindBy >= 2;
                    if (want && !_catchUp) { _catchUpBy = ahead.Name; _catchUps++; if (_catchUps <= 10) _ctx.Log($"часы: отстаю от «{ahead.Name}» на {behindBy} с — ускоряю до ×{Fast}"); }
                }
            }
            // 1.6.7: ускоряем от текущей скорости игры (у друга она часто 0,5–0,8 — 1.6.5 ускорял только при 1 и почти не догонял).
            // Скорость, которую поставила игра, — _gameScale; ставим _gameScale × 1,25. Игра поменяла скорость сама (не наше
            // значение) — запоминаем её новую и ускоряем уже от неё. Паузу (0) не трогаем.
            if (want && ts > 0)
            {
                if (!_catchUp || Math.Abs(ts - _catchSet) > 0.001f) _gameScale = ts;
                _catchSet = _gameScale * (_catchBehind >= 4 ? 1.5f : Fast);
                if (Math.Abs(ts - _catchSet) > 0.001f) TimeScale = _catchSet;
                _catchUp = true;
                return;
            }
            if (_catchUp && !want)
            {
                _catchUp = false;
                if (Math.Abs(TimeScale - _catchSet) < 0.001f) TimeScale = _gameScale;
                if (_catchUps <= 10 && !_pauseForced && !OwnPause()) _ctx.Log($"часы: догнал «{_catchUpBy}»");
            }
        }
        private float _gameScale = 1, _catchSet;
        private int _catchBehind;
        /// <summary>
        /// Старт боя (1.6.8): кто вошёл в бой раньше, ждёт, пока часы друга не догонят (наблюдение 24.10: друг вошёл на 5–6 с
        /// позже, и ускорение ×1,25 с паузами на выбор умения этого не догоняло). Только в первые 40 с боя и не дольше 20 с.
        /// </summary>
        /// <summary>
        /// 1.6.31: последние 10 с перед босс-волной (3:00, 6:00, …) тот, кто впереди, ждёт отставших — босс-волна (и её вступление
        /// ~40 с) начинается у всех вместе. Иначе гость, отставший на полминуты (телефоны 24.10), после обелиска хоста ещё столько же
        /// ждал конца своего вступления. Не дольше 60 с подряд.
        /// </summary>
        private Partner BossApproachWait(double nowR, ref string why)
        {
            int mine = MyClock();
            if (mine < 0 || MyBossWave() != 0) { _bossApproachSince = 0; return null; }
            int toBoss = BossEvery - mine % BossEvery;
            if (toBoss > 10) { _bossApproachSince = 0; return null; }
            if (_bossApproachSince > 0 && nowR - _bossApproachSince > 60) return null;
            var late = _partners.Values.Where(p => p.Battle && !p.Dead && p.Hp > 0 && !p.BossWave && p.BattleClock >= 0 && nowR - p.StateReal < 1.5 && mine - p.BattleClock >= 1)
                                       .OrderBy(p => p.BattleClock).FirstOrDefault();
            if (late == null) { if (_pauseWhy != "к боссу") _bossApproachSince = 0; return null; }
            if (_bossApproachSince <= 0) _bossApproachSince = nowR;
            why = "к боссу";
            return late;
        }
        private const int BossEvery = 180;
        private double _bossApproachSince;

        private Partner StartWait(double nowR, ref string why)
        {
            int mine = MyClock();
            if (mine < 0 || mine > 40 || nowR - _battleStartSec > 60) return null;
            if (_startWaitSince > 0 && nowR - _startWaitSince > 20) return null;
            var late = _partners.Values.Where(p => p.Battle && !p.Dead && p.BattleClock >= 0 && nowR - p.StateReal < 1.5 && mine - p.BattleClock >= (_pauseWhy == "старт" ? 1 : 2))
                                       .OrderBy(p => p.BattleClock).FirstOrDefault();
            if (late == null) return null;
            if (_startWaitSince <= 0) _startWaitSince = nowR;
            why = "старт";
            return late;
        }
        private double _startWaitSince;

        /// <summary>
        /// 1.6.14: босс-волна у меня кончилась (поднял обелиск — часы пошли), а у друга ещё идёт (его часы стоят на 3:00, пока он
        /// не поднимет свой обелиск) — жду, иначе убегаю вперёд (наблюдение 24.10: на 15 с, дальше больше). Не дольше 60 с.
        /// </summary>
        private Partner BossWait(double nowR, ref string why)
        {
            if (MyBossWave() != 0) { _bossWaitSince = 0; return null; }
            // 1.6.15: ждём, только если свой бой с боссом мы уже закончили (смена области прошла), а друг — нет. 1.6.14 ждал и в
            // начале босс-волны: у хоста она началась раньше, друг вставал «ждать», его часы не доходили до 3:00 — взаимная блокировка
            int myAc = MyAreaChange();
            var p = _partners.Values.FirstOrDefault(x => x.BossWave && x.Battle && !x.Dead && x.Hp > 0 && nowR - x.StateReal < 1.5
                                                         && myAc >= 0 && x.AreaChange >= 0 && myAc > x.AreaChange);
            if (p == null) { _bossWaitSince = 0; return null; }
            if (_bossWaitSince <= 0) _bossWaitSince = nowR;
            if (nowR - _bossWaitSince > 60) return null;
            why = "босс";
            return p;
        }
        private double _bossWaitSince;

        /// <summary>
        /// 1.6.14: большое расхождение часов (от 8 с — у друга часто замедлена игра: артефакты, умения, и ускорения ×1,25 от его
        /// скорости не хватает) — тот, кто впереди, ждёт, пока разница не станет 2 с. Редко; мелкое — догоняющее ускорение.
        /// </summary>
        private Partner GapWait(double nowR, ref string why)
        {
            int mine = MyClock();
            if (mine < 0 || MyBossWave() != 0) return null;
            if (_gapWaitSince > 0 && nowR - _gapWaitSince > 30) { if (nowR - _gapWaitSince > 90) _gapWaitSince = 0; return null; }   // не дольше 30 с подряд
            var late = _partners.Values.Where(p => p.Battle && !p.Dead && p.Hp > 0 && !p.BossWave && p.BattleClock >= 0 && nowR - p.StateReal < 1.5
                                                   && mine - p.BattleClock >= (_pauseWhy == "часы" ? 3 : 8))
                                       .OrderBy(p => p.BattleClock).FirstOrDefault();
            if (late == null) { if (_pauseWhy != "часы") _gapWaitSince = 0; return null; }
            if (_gapWaitSince <= 0) _gapWaitSince = nowR;
            why = "часы";
            return late;
        }
        private double _gapWaitSince;

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
            _startWaitSince = 0; _bossWaitSince = 0; _gapWaitSince = 0;
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
