using System;
using System.Text.Json;
using UnityEngine;

namespace MagicSurvivalCoop
{
    // За краем арены босса игра бьёт героя, поэтому гостя вдали переносим к хосту.
    internal sealed partial class Session
    {
        private const double GatherBackSec = 2;
        private bool GatherOn => _ctx.Setting("bossGather") > 0;
        private enum Gather { Idle, AtHost, Leaving }
        private Gather _gather;
        private double _gatherAt, _gatherRelX, _gatherRelY, _gatherX, _gatherY;
        private int _gatherTimes;
        // Арена босса хоста от его точки старта, пока арена есть
        private double _hostBgX, _hostBgY;
        private bool _hostBg, _gatherBgHost;

        private void ResetGather() { _gather = Gather.Idle; _gatherAt = 0; _hostBg = false; }

        private void WriteBossGround(Utf8JsonWriter w)
        {
            try
            {
                if (Convert.ToDouble(GetAny(_battleMgr, "ApplyBossGround_Size") ?? 0.0) <= 0) return;
                w.WriteNumber("bgx", Math.Round(Convert.ToDouble(GetAny(_battleMgr, "ApplyBossGround_X")) - (_startX ?? 0), 2));
                w.WriteNumber("bgy", Math.Round(Convert.ToDouble(GetAny(_battleMgr, "ApplyBossGround_Y")) - (_startY ?? 0), 2));
            }
            catch { }
        }

        private void ReadBossGround(JsonElement s)
        {
            _hostBg = s.TryGetProperty("bgx", out var bx) && s.TryGetProperty("bgy", out var by) && bx.TryGetDouble(out _hostBgX) && by.TryGetDouble(out _hostBgY);
        }

        private void GatherStep(Partner host, double now, bool hasHero, double hx, double hy)
        {
            if (!_startX.HasValue || !hasHero) return;
            double sx = _startX.Value, sy = _startY ?? 0;
            bool boss = MyBossWave() != 0 || _hostBossWave != 0;
            switch (_gather)
            {
                case Gather.Idle:
                    if (!boss || !_away || !GatherOn) return;
                    _gatherRelX = hx - sx; _gatherRelY = hy - sy;
                    _ctx.Toast("Босс-волна: переношу к хосту", 2);
                    double tx = sx + host.RelX + 1.5, ty = sy + host.RelY;    // Рядом с хостом, не в нём
                    TeleportHero(tx, ty);
                    _gather = Gather.AtHost; _gatherTimes++;
                    _ctx.Log($"сбор на босса: перенесён к «{host.Name}» ({_gatherRelX:0.0}; {_gatherRelY:0.0}) → ({tx - sx:0.0}; {ty - sy:0.0}) от старта; арена — {(_hostBg ? "как у хоста" : "вокруг меня")}");
                    _gatherX = tx; _gatherY = ty;
                    MoveBossGround(tx, ty);
                    return;
                case Gather.AtHost:
                    // Игра могла поставить арену заново, а арена хоста могла прийти после переноса
                    if (boss) { if (now >= _gatherAt || (_hostBg && !_gatherBgHost)) { _gatherAt = now + 0.5; MoveBossGround(_gatherX, _gatherY); } return; }
                    _gather = Gather.Leaving; _gatherAt = now + GatherBackSec;
                    // Ману до возвращения. Если обелиск хоста её уже притянул (CatchUpBoss), повторно не тянем
                    if (now - _pulledAt > 3) _ctx.Log($"сбор на босса: волна кончилась — притягиваю ману ({PullItems()} предметов)");
                    return;
                case Gather.Leaving:
                    if (boss) { _gather = Gather.AtHost; return; }
                    if (now < _gatherAt) return;
                    TeleportHero(sx + _gatherRelX, sy + _gatherRelY);
                    _gather = Gather.Idle;
                    _ctx.Log($"сбор на босса: волна кончилась — вернул на место ({_gatherRelX:0.0}; {_gatherRelY:0.0})");
                    _ctx.Toast("Возвращаю туда, где вы стояли", 2);
                    return;
            }
        }

        private void MoveBossGround(double heroX, double heroY)
        {
            var mgr = _battleMgr;
            if (mgr == null) return;
            try
            {
                if (Convert.ToDouble(GetAny(mgr, "ApplyBossGround_Size") ?? 0.0) <= 0) return;
                double x = _hostBg ? (_startX ?? 0) + _hostBgX : heroX, y = _hostBg ? (_startY ?? 0) + _hostBgY : heroY;
                SetMember(mgr, "ApplyBossGround_X", (float)x);
                SetMember(mgr, "ApplyBossGround_Y", (float)y);
                _gatherBgHost = _hostBg;
                if (GetAny(mgr, "Obj_BossLimitGround") is GameObject g && g.transform is Transform t)
                {
                    var z = t.position.z;
                    t.position = new Vector3((float)x, (float)y, z);
                }
            }
            catch (Exception e) { if (++_wFails <= 5) _ctx.Log("сбор на босса: арена: " + Inner(e).Message); }
        }

        private void TeleportHero(double x, double y)
        {
            try
            {
                var hero = RawHero();
                var tr = _heroTr ?? GetMember(hero, "Transform_ObjUnit") as Transform;
                if (tr == null) { _ctx.Log("сбор на босса: нет объекта героя — не переношу"); return; }
                var p = new Vector3((float)x, (float)y, tr.position.z);
                tr.position = p;
                try { SetMember(GetMember(hero, "Rigidbody_ObjUnit"), "position", p); } catch { }   // Иначе физика вернёт героя
            }
            catch (Exception e) { if (++_wFails <= 5) _ctx.Log("сбор на босса: перенос героя: " + Inner(e).Message); }
        }
    }
}
