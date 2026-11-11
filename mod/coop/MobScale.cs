using System;
using System.Collections.Generic;
using System.Globalization;
using ModKit;

namespace MagicSurvivalCoop
{
    // Множители — правила хоста (HostRules).
    // Здоровье: проход по живым врагам умножает UnitApply_HpMax и Unit_Hp. Хук на C_Battle_EnemyUnit.Unit_Damage был бы
    // на каждое попадание, а такие массовые хуки роняли Mono на телефоне. Переходам и особым мобам игра ставит силу после
    // Start_Unit, поэтому запоминаем выставленный максимум и пересчитываем, когда игра его сменила.
    // Урон: хук на C_Battle_AllyUnit.Unit_Damage, его же ставит сам ModKit для события heroDamaged.
    internal sealed partial class Session
    {
        internal const double MobMulMin = 0.5, MobMulMax = 10;
        private const double MobHpSweepSec = 0.2;
        private double _mobHpMul = 1, _mobDmgMul = 1;
        private double _mobHpSweepAt;
        private Dictionary<object, (float max, double mul)> _hpSet = new Dictionary<object, (float, double)>(), _hpSetNext = new Dictionary<object, (float, double)>();
        private int _mobHpScaled, _mobDmgScaled;

        private double MobHpRule => ClampMul(Rule("mobHp"));
        private double MobDmgRule => ClampMul(Rule("mobDmg"));

        internal static double ClampMul(double v) => double.IsNaN(v) || v <= 0 ? 1 : Math.Max(MobMulMin, Math.Min(MobMulMax, v));

        private void InstallMobScale()
        {
            try { _ctx.Before("C_Battle_AllyUnit", "Unit_Damage", 3, Hook("урон врагов", (self, a) => ScaleHeroDamage(a))); }
            catch (Exception e) { _ctx.Log("множитель урона врагов недоступен: " + e.Message); }
        }

        private void UpdateMobScale()
        {
            double hp = MobHpRule, dmg = MobDmgRule;
            if (hp != _mobHpMul || dmg != _mobDmgMul)
            {
                _mobHpMul = hp; _mobDmgMul = dmg;
                _ctx.Log($"враги: здоровье ×{FormatMul(hp)}, урон ×{FormatMul(dmg)}");
            }
            double now = Clock.Elapsed.TotalSeconds;
            if (!_inRun || now < _mobHpSweepAt) return;
            _mobHpSweepAt = now + MobHpSweepSec;
            ScaleEnemyHp();
        }

        // Враг из пула стал новым: его значения снова от игры.
        private void ForgetEnemyHp(object key) { if (key != null) _hpSet.Remove(key); }

        private void ScaleEnemyHp()
        {
            _hpSetNext.Clear();
            foreach (var e in AliveNow())
            {
                var obj = e.Raw;
                var key = obj == null ? null : KeyOf(obj);
                if (key == null || _puppetKeys.Contains(key) || _forwardKeys.Contains(key)) continue;
                float hm = UnitFloat(obj, key, "UnitApply_HpMax") ?? 0f;
                if (hm <= 0) continue;
                bool known = _hpSet.TryGetValue(key, out var set);
                if (known && hm == set.max && set.mul == _mobHpMul) { _hpSetNext[key] = set; continue; }
                if (!known && _mobHpMul == 1) continue;
                float hp = UnitFloat(obj, key, "Unit_Hp") ?? hm;
                double baseMax, frac;
                if (known && hm == set.max) { baseMax = set.max / set.mul; frac = hp / set.max; }      // Хост сменил множитель
                // Игра, сменив максимум, либо ставит полное здоровье (переход), либо оставляет наше
                else if (known && hp != hm && hp <= set.max) { baseMax = hm; frac = hp / set.max; }
                else { baseMax = hm; frac = hp / hm; }
                float max = (float)(baseMax * _mobHpMul);
                SetUnitFloat(obj, key, "UnitApply_HpMax", max);
                SetUnitFloat(obj, key, "Unit_Hp", (float)(Math.Max(0, Math.Min(1, frac)) * max));
                _hpSetNext[key] = (max, _mobHpMul);
                _mobHpScaled++;
            }
            (_hpSet, _hpSetNext) = (_hpSetNext, _hpSet);
        }

        private void ScaleHeroDamage(object[] a)
        {
            if (_mobDmgMul == 1 || a == null || a.Length < 1) return;
            switch (a[0])
            {
                case float f when f > 0: a[0] = (float)(f * _mobDmgMul); break;
                case double d when d > 0: a[0] = d * _mobDmgMul; break;
                default: return;
            }
            _mobDmgScaled++;
        }

        private void ResetMobScale()
        {
            _hpSet.Clear(); _hpSetNext.Clear();
            _mobHpSweepAt = 0;
        }

        private void LogMobScale()
        {
            if (_mobHpScaled == 0 && _mobDmgScaled == 0) return;
            _ctx.Log($"враги за бой: здоровье ×{FormatMul(_mobHpMul)} выставлено {_mobHpScaled} раз, урон ×{FormatMul(_mobDmgMul)} по герою — {_mobDmgScaled} попаданий");
            _mobHpScaled = _mobDmgScaled = 0;
        }

        internal static string FormatMul(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
