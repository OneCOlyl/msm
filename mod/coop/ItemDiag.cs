using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using ModKit;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Журнал подбора предметов (1.6.16). Обелиск после босса — предмет игры (C_Battle_Item), его подбор (Obtain_Item) даёт
    /// окно артефакта, притягивает ману (Play_ItemAllObtain_Move) и, видимо, запускает смену области — после неё идут часы.
    /// Чтобы у гостя «поднимать» его обелиск вместе с хостом, нужно отличать обелиск от прочих предметов: пишем поля особых
    /// предметов при подборе (обычные — мана, монеты — пропускаем по повтору).
    /// </summary>
    internal sealed partial class Session
    {
        private readonly HashSet<string> _itemKinds = new HashSet<string>();
        private int _itemLogs;

        private void InstallItemDiag()
        {
            try { _ctx.Before("C_Battle_Item", "Obtain_Item", 0, (self, a) => _ctx.Guard("предмет", () => { _obeliskBefore = ObeliskFlag(); OnItemObtain(self); })); }
            catch (Exception e) { _ctx.Log("предметы: хук Obtain_Item недоступен: " + e.Message); }
            try { _ctx.After("C_Battle_Item", "Obtain_Item", 0, (self, a) => _ctx.Guard("предмет", () => OnItemObtained(self))); }
            catch (Exception e) { _ctx.Log("предметы: хук после Obtain_Item недоступен: " + e.Message); }
            // 1.6.17: кто и когда запускает смену области и конец босс-волны
            foreach (var m in new[] { "Start_AreaChange" })
                try { var name = m; _ctx.Before("C_Battle_Manager", name, 0, (self, a) => _ctx.Guard("обелиск", () => LogBossState("игра вызвала " + name))); }
                catch (Exception e) { _ctx.Log($"обелиск: хук {m} недоступен: " + e.Message); }
            foreach (var m in new[] { "Start_ItemType", "CreateItem_Field", "Start_Transition", "DestroyItem" })
                try { var name = m; _ctx.Before("C_Battle_Item", name, 0, (self, a) => _ctx.Guard("обелиск", () => { if (_inRun && ObeliskFlag() == 1 && _itemCalls++ < 60) LogBossState($"предмет «{NameOf(GetMember(self, "gameObject"))}» {name}"); })); }
                catch (Exception e) { _ctx.Log($"обелиск: хук {m} недоступен: " + e.Message); }

            // корутины: первый шаг каждой — в журнал
            foreach (var (type, rx) in new[] { ("C_Battle_Manager", "Play_BossWaveEffect|Play_AreaChange|Play_BossWave_Enforce"), ("C_Battle_Item", "Play_ItemAllObtain_Move") })
            {
                Type t = null;
                try { t = Hooks.FindType(type); } catch { }
                if (t == null) continue;
                foreach (var nt in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (!System.Text.RegularExpressions.Regex.IsMatch(nt.Name, rx)) continue;
                    var mi = nt.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                    if (mi == null) continue;
                    var key = nt.Name;
                    try
                    {
                        Hooks.HookBefore(_ctx.Id, mi, (self, args) => _ctx.Guard("обелиск", () =>
                        {
                            if (self == null || !_inRun || _coSeen.Count > 200) return;
                            var id = (self.GetType().GetProperty("Pointer")?.GetValue(self) as IntPtr?) ?? IntPtr.Zero;
                            if (!_coSeen.Add(key + id)) return;
                            if (key.Contains("ItemAllObtain") && _coPull++ > 3) return;
                            LogBossState("корутина " + key);
                        }));
                    }
                    catch (Exception e) { _ctx.Log($"обелиск: хук {key} недоступен: " + e.Message); }
                }
            }
        }
        private readonly HashSet<string> _coSeen = new HashSet<string>();
        private int _coPull;

        private int _obeliskBefore = -1, _itemCalls;

        private int ObeliskFlag()
        {
            try { return Convert.ToInt32(GetAny(_battleMgr, "ApplyObeliskON") ?? -1); } catch { return -1; }
        }

        private void LogBossState(string what)
        {
            if (!_inRun || _battleMgr == null) return;
            string F(string f) { try { return Convert.ToString(GetAny(_battleMgr, f), CultureInfo.InvariantCulture); } catch { return "?"; } }
            _ctx.Log($"обелиск ({(_hosting ? "хост" : "гость")}): {what}; босс-волна {F("Battle_ClearWaveON")}, боссов {F("ApplyBossNum")}, арена {F("ApplyBossGround_Size")}, " +
                     $"обелиск {F("ApplyObeliskON")}, смен области {F("AreaChangeNum")}, часы {MyClock()}");
        }

        /// <summary>После подбора: флаг обелиска 1 → 0 — это был обелиск (запоминаем его вид).</summary>
        private void OnItemObtained(object item)
        {
            if (!_inRun) return;
            int after = ObeliskFlag();
            if (_obeliskBefore == 1 && after != 1)
            {
                ItemFields(item, out var kind);
                LogBossState($"поднят обелиск «{NameOf(GetMember(item, "gameObject"))}» (вид {kind})");
            }
        }

        private void OnItemObtain(object item)
        {
            if (!_inRun || item == null || _itemLogs >= 40) return;
            var fields = ItemFields(item, out string kind);
            int area = MyAreaChange(), obelisk = -1;
            try { obelisk = Convert.ToInt32(GetAny(_battleMgr, "ApplyObeliskON") ?? -1); } catch { }
            // один раз на каждый вид предмета; обелиск (флаг ApplyObeliskON = 1) — всегда
            if (!_itemKinds.Add(kind) && obelisk != 1) return;
            _itemLogs++;
            _ctx.Log($"предмет подобран: «{NameOf(GetMember(item, "gameObject"))}», вид {kind}, обелиск {obelisk}, смен области {area}, часы {MyClock()}: {fields}");
        }

        /// <summary>Простые поля предмета; вид — поля с ID/Type/Kind в имени.</summary>
        private static string ItemFields(object item, out string kind)
        {
            const BindingFlags inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var parts = new List<string>(); var ids = new List<string>();
            foreach (var p in item.GetType().GetProperties(inst))
            {
                if (p.GetIndexParameters().Length > 0 || p.Name.StartsWith("Native") || p.Name == "Pointer" || p.Name == "m_CachedPtr") continue;
                var t = p.PropertyType;
                if (!(t.IsPrimitive || t.IsEnum || t == typeof(string))) continue;
                object v; try { v = p.GetValue(item); } catch { continue; }
                var s = Convert.ToString(v, CultureInfo.InvariantCulture);
                parts.Add(p.Name + "=" + s);
                if (p.Name.IndexOf("ID", StringComparison.Ordinal) >= 0 || p.Name.IndexOf("Type", StringComparison.OrdinalIgnoreCase) >= 0) ids.Add(p.Name + "=" + s);
            }
            kind = string.Join(",", ids);
            return string.Join(", ", parts);
        }
    }
}
