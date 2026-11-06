using System;
using System.Text.Json;
using MagicSurvivalCoop.Net;

namespace MagicSurvivalCoop
{
    // Игра выводит Time_Min и Time_Sec из часов сцены Unity, своего счётчика нет, и подставленные значения тут же пересчитывает.
    internal sealed partial class Session
    {
        private int _hostClock = -1, _hostBossWave, _hostBwPrev;
        private int _hostAreaChange = -1;                // AreaChangeNum хоста, растёт после каждого его босса
        private int _hostObelisk = -1, _hostObPrev = -1; // ApplyObeliskON хоста: 1 → 0 значит, что хост поднял обелиск
        private bool _hostHadBoss;
        private bool _hostPicked;                        // Гость должен получить то же, что хост за подбор (ObeliskSync.cs)
        private double _hostClockAt = -100;
        private double _clockCheckAt;

        private void ResetClock()
        {
            _hostClock = -1; _hostBossWave = 0; _hostClockAt = -100; _hostBwPrev = 0;
            _clockCheckAt = 0;
            _clockSent = (-1, -1, -1, -1); _clockSentAt = -100;
            _hostObelisk = _hostObPrev = -1; _hostPicked = false; _bossHold = false; _clockColor = null;
            _treasureScanAt = 0; _treasureGone.Clear(); _hostHadBoss = false; _treasureRewards = 0;
        }

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

        // Часы и босс-волна: при изменении и не реже раза в ClockSendSec, а не в каждом состоянии.
        private const double ClockSendSec = 0.5;
        private (int, int, int, int) _clockSent = (-1, -1, -1, -1);
        private double _clockSentAt = -100;

        private void WriteClock(Utf8JsonWriter w)
        {
            int c = MyClock();
            if (c < 0) return;
            int bw = MyBossWave(), ob = MyObelisk(), ac = MyAreaChange();
            double now = Clock.Elapsed.TotalSeconds;
            if ((c, bw, ob, ac).Equals(_clockSent) && now - _clockSentAt < ClockSendSec) return;
            _clockSent = (c, bw, ob, ac); _clockSentAt = now;
            w.WriteNumber("tm", c);
            w.WriteNumber("bw", bw);
            if (ob >= 0) w.WriteNumber("ob", ob);
            if (ac >= 0) w.WriteNumber("ac", ac);
            if (_hosting) WriteBossGround(w);
        }

        private void ReadClock(Partner p, JsonElement s)
        {
            if (_hosting || !s.TryGetProperty("tm", out _)) return;
            if (_worldHost != null && p != _worldHost) return;
            _hostClock = (int)Protocol.Num(s, "tm", -1);
            _hostBossWave = (int)Protocol.Num(s, "bw", 0);
            _hostClockAt = Clock.Elapsed.TotalSeconds;
            if (_hostBwPrev == 0 && _hostBossWave == 1) _hostPicked = false;   // Новый босс у хоста: прошлый подбор не в счёт
            if (_hostBossWave == 1) _hostHadBoss = true;
            _hostBwPrev = _hostBossWave;
            _hostAreaChange = (int)Protocol.Num(s, "ac", -1);
            _hostObelisk = (int)Protocol.Num(s, "ob", -1);
            if (_hostObPrev == 1 && _hostObelisk == 0) _hostPicked = true;
            _hostObPrev = _hostObelisk;
            ReadBossGround(s);
        }

        private void ClockStep(double now)
        {
            if (now < _clockCheckAt || _battleMgr == null) return;
            _clockCheckAt = now + 0.25;
            if (MyClock() < 0) return;
            TreasureStep(now);
            if (_hosting || !_worldActive || _ctx.Setting("clockSync") <= 0 || _hostClock < 0 || now - _hostClockAt > 1.5) return;
            GuestBossStep();
        }

        private int MyAreaChange()
        {
            try { return Convert.ToInt32(GetAny(_battleMgr, "AreaChangeNum") ?? -1); } catch { return -1; }
        }

        // 15 пока виден череп босс-волны, 0 когда погас.
        private double MyVision()
        {
            try { return Convert.ToDouble(GetAny(_battleMgr, "BossWave_VisionRatio") ?? 0.0); } catch { return 0; }
        }
        // Не прочитать: считаем вступление законченным.
        private double MyEnforce()
        {
            try { return Convert.ToDouble(GetAny(_battleMgr, "BossWave_EnforceVar") ?? 1.0); } catch { return 1; }
        }

        private int MyObelisk()
        {
            try { return Convert.ToInt32(GetAny(_battleMgr, "ApplyObeliskON") ?? -1); } catch { return -1; }
        }

        // Своё окно выбора открыто: догоняем после него.
        private void GuestBossStep()
        {
            // Счётчик боссов держим 1 с начала боя: иначе конец вступления на 3:00 кладёт гостю обелиск, который не убрать с карты
            if (!_bossHold) { _bossHold = true; _ctx.Log("обелиск: счётчик боссов гостя держу 1 — своего обелиска игра гостю не положит"); }
            BossHoldStep();
            NoteClockColor();
            int ac = MyAreaChange();
            if (MyObelisk() == 1 && !_hostPicked && !OwnPause())
            {
                // Игра гостя сама положила обелиск. Хост свой ещё не поднял: убрать и ждать. Уже поднял: убрать и пустить часы без
                // второй награды. «Уже поднял» требует прошедшей босс-волны хоста, потому что особый обелиск Terra ставит флаг
                // обоим в начале боя
                if (_hostObelisk == 1 || _hostBossWave != 0) RemoveObelisks("игра положила его гостю, хост свой ещё не поднял");
                else if (_hostHadBoss && _hostObelisk == 0) try { CatchUpBoss(false); } catch (Exception e) { _ctx.Log("обелиск: " + Inner(e).Message); }
            }
            if (!_hostPicked || OwnPause()) return;
            _hostPicked = false;
            bool behind = MyBossWave() != 0 || MyObelisk() == 1 || MyVision() > 0.01 || (ac >= 0 && _hostAreaChange > ac);
            if (!behind) { _ctx.Log($"обелиск: хост поднял свой — у меня босс уже пройден (смен области {ac}, у хоста {_hostAreaChange})"); return; }
            try { CatchUpBoss(true); } catch (Exception e) { _ctx.Log("обелиск: " + Inner(e).Message); }
        }

        private int MyBossCount()
        {
            try { return Convert.ToInt32(GetAny(_battleMgr, "ApplyBossNum") ?? 0); } catch { return -1; }
        }
    }
}
