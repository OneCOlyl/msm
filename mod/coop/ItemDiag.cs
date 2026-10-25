using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using ModKit;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Журнал босс-этапа (1.6.16–1.6.17): кто и когда запускает смену области и конец босс-волны — первый шаг корутин менеджера
    /// боя и событие смены области. Хуки на каждый предмет (подбор, DestroyItem, шаг притягивания) убраны в 1.6.46: обелиск
    /// изучен, а при подборе обелиска притягиваются ~500 предметов — сотни вызовов хуков в кадр, и у гостя (Pixel 3a) после
    /// 7-секундного подвисания упала среда Mono (assertion в mono-threads-posix.c при остановке потоков).
    /// </summary>
    internal sealed partial class Session
    {
        private void InstallItemDiag()
        {
            _ctx.Events.On("areaChange", _ => { if (!_shutdown) LogBossState("игра запустила смену области (Start_AreaChange)"); });

            // корутины менеджера боя: первый шаг каждой — в журнал
            Type t = null;
            try { t = Hooks.FindType("C_Battle_Manager"); } catch { }
            if (t == null) return;
            foreach (var nt in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!System.Text.RegularExpressions.Regex.IsMatch(nt.Name, "Play_BossWaveEffect|Play_AreaChange|Play_BossWave_Enforce")) continue;
                var mi = nt.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (mi == null) continue;
                var key = nt.Name;
                try
                {
                    // хук не через контекст — ModKit сам его не снимет: снятие отдаём в OnCleanup, иначе он жил бы после выключения мода
                    _ctx.OnCleanup(Hooks.HookBefore(_ctx.Id, mi, Hook("обелиск: корутина", (self, args) =>
                    {
                        if (self == null || !_inRun || _coSeen.Count > 200) return;
                        if (!_coSeen.Add(key + PtrOf(self))) return;
                        LogBossState("корутина " + key);
                    })));
                }
                catch (Exception e) { _ctx.Log($"обелиск: хук {key} недоступен: " + e.Message); }
            }
        }
        private readonly HashSet<string> _coSeen = new HashSet<string>();

        private void LogBossState(string what)
        {
            if (!_inRun || _battleMgr == null) return;
            string F(string f) { try { return Convert.ToString(GetAny(_battleMgr, f), CultureInfo.InvariantCulture); } catch { return "?"; } }
            _ctx.Log($"обелиск ({(_hosting ? "хост" : "гость")}): {what}; босс-волна {F("Battle_ClearWaveON")}, боссов {F("ApplyBossNum")}, арена {F("ApplyBossGround_Size")}, " +
                     $"обелиск {F("ApplyObeliskON")}, смен области {F("AreaChangeNum")}, часы {MyClock()}");
        }
    }
}
