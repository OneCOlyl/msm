using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace MagicSurvivalCoop
{
    // Правила боя: у гостя действуют значения хоста, у хоста и без подключения свои. Личные настройки (вид, сеть) здесь нет.
    internal sealed partial class Session
    {
        private static readonly string[] RuleKeys =
            { "world", "worldZone", "bossGather", "healPct", "healCd", "mirrorDmg", "sharedPause", "mobHp", "mobDmg" };

        private Partner _ruleHost;

        private double Rule(string key)
        {
            var h = _ruleHost;
            return h?.Rules != null && h.Rules.TryGetValue(key, out var v) ? v : _ctx.Setting(key);
        }

        // Раз в тик таймера: правила читаются и в покадровых местах.
        private void UpdateRuleHost() =>
            _ruleHost = !_hosting && _client.Connected ? _partners.Values.FirstOrDefault(p => p.Rules != null) : null;

        private string RulesSignature()
        {
            var sb = new StringBuilder();
            foreach (var k in RuleKeys) sb.Append(_ctx.Setting(k).ToString(CultureInfo.InvariantCulture)).Append(';');
            return sb.ToString();
        }

        private void WriteRules(Utf8JsonWriter w)
        {
            w.WriteStartObject("rules");
            foreach (var k in RuleKeys) w.WriteNumber(k, _ctx.Setting(k));
            w.WriteEndObject();
        }

        private static void ReadRules(Partner p, JsonElement s)
        {
            if (!s.TryGetProperty("rules", out var r) || r.ValueKind != JsonValueKind.Object) return;
            p.Rules ??= new Dictionary<string, double>();
            foreach (var k in RuleKeys)
                if (r.TryGetProperty(k, out var v) && v.TryGetDouble(out var d)) p.Rules[k] = d;
        }

        private string RulesLine()
        {
            if (_ruleHost == null) return null;
            string On(string k) => Rule(k) > 0 ? "вкл" : "выкл";
            string N(string k) => Rule(k).ToString("0.##", CultureInfo.InvariantCulture);
            return $"Правила боя от хоста: общие враги {On("world")}, зона {N("worldZone")}, сбор на босса {On("bossGather")}, " +
                   $"общая пауза {On("sharedPause")}, лечение {N("healPct")}% / {N("healCd")} с, урон магии друга {N("mirrorDmg")}%, " +
                   $"ХП врагов ×{N("mobHp")}, урон врагов ×{N("mobDmg")}";
        }
    }
}
