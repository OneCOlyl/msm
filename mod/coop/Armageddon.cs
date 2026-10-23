using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MagicSurvivalCoop.Net;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Армагеддон друга. Снарядов у него нет: взрыв рисует менеджер боя (C_Battle_Manager) корутинами, пока
    /// IsArmageddonContinue = True (~5 с) — docs/ARMAGEDDON.md. Начало взрыва ловим по флагу False → True и шлём событие «arma».
    /// Получатель запускает у себя корутины картинки взрыва (Play_ArmageddonEffect, Play_ArmageddonLand); не вышло — включает
    /// объекты по таблице разведки (1.4.7: без анимации их не видно). Обе стороны пишут в журнал раскладку взрыва по кадрам.
    /// </summary>
    internal sealed partial class Session
    {
        // (время от начала взрыва, поле менеджера, включить) — запасной способ, по разведке
        private static readonly (double t, string field, bool on)[] ArmaSteps =
        {
            (0.00, "Obj_ArmageddonGround_Front", true),
            (0.00, "Obj_ArmageddonGround_Back", true),
            (1.86, "Obj_Armageddon", true),
            (2.09, "Obj_Armageddon", false),
            (2.45, "Obj_ArmageddonLand", true),
            (2.45, "Obj_ArmageddonGround_Back", false),
            (2.45, "Obj_ArmageddonGround_Destroy", true),
            (2.69, "Obj_ArmageddonGround_Destroy", false),
            (3.14, "Obj_ArmageddonGround_Front", false),
            (4.78, "Obj_ArmageddonLand", false),
        };
        private static readonly string[] ArmaFields = { "Obj_ArmageddonGround_Front", "Obj_ArmageddonGround_Back", "Obj_Armageddon", "Obj_ArmageddonLand", "Obj_ArmageddonGround_Destroy" };
        private static readonly string[] ArmaCoroutines = { "Play_ArmageddonEffect", "Play_ArmageddonLand" };
        private const double ArmaTraceSec = 6;

        private object _battleMgr;                 // C_Battle_Manager — из хука его Update
        private bool _armaWas;                     // свой взрыв идёт (прошлое значение флага)
        private double _armaCheckAt;
        private double _armaBusyUntil;            // до этого времени идёт повтор взрыва друга
        private double _armaPlayAt = -1;           // начало повтора взрыва друга по таблице (-1 — не идёт)
        private int _armaStep;
        private int _armaSent, _armaPlayed, _armaSkipped, _armaFails, _armaTraces;
        private bool _armaCoroutinesFailed;
        private MethodInfo _startCoroutine;

        // раскладка взрыва по кадрам: для каждого объекта — моменты, когда его вид менялся
        private double _traceAt = -1;
        private string _traceWhat;
        private readonly Dictionary<string, List<string>> _trace = new Dictionary<string, List<string>>();
        private readonly Dictionary<string, string> _traceLast = new Dictionary<string, string>();
        private readonly List<string> _traceEnemies = new List<string>();
        private int _traceNextEnemy;

        private void ResetArmageddon()
        {
            if (_armaPlayAt >= 0) StopArmaReplay();
            _armaWas = false; _armaCheckAt = 0; _armaPlayAt = -1; _traceAt = -1; _armaBusyUntil = 0;
            _armaSent = _armaPlayed = _armaSkipped = _armaTraces = 0;
        }

        private bool OwnArmaRunning() => GetMember(_battleMgr, "IsArmageddonContinue") is bool b && b;

        /// <summary>Каждый кадр: свой взрыв начался — сообщить друзьям; идёт повтор по таблице — следующий шаг; раскладка в журнал.</summary>
        private void ArmageddonStep(double now)
        {
            if (_battleMgr == null) return;
            try
            {
                if (now >= _armaCheckAt)
                {
                    _armaCheckAt = now + 0.1;
                    bool on = OwnArmaRunning();
                    if (on && !_armaWas)
                    {
                        if (_client.Connected && _partners.Count > 0)
                        {
                            _client.SendEvent(Protocol.Build(w => w.WriteString("kind", "arma")));
                            if (_armaSent++ < 3) _ctx.Log("Армагеддон: свой взрыв — отправлен друзьям");
                        }
                        StartTrace(now, "свой взрыв");
                    }
                    _armaWas = on;
                }
                if (_armaPlayAt >= 0)
                {
                    double t = now - _armaPlayAt;
                    while (_armaStep < ArmaSteps.Length && ArmaSteps[_armaStep].t <= t)
                    {
                        var s = ArmaSteps[_armaStep++];
                        var go = GetMember(_battleMgr, s.field);
                        if (go != null) SetActive(go, s.on);
                    }
                    if (_armaStep >= ArmaSteps.Length) _armaPlayAt = -1;
                }
                if (_traceAt >= 0) TraceStep(now);
            }
            catch (Exception e) { if (++_armaFails <= 3) _ctx.Log("Армагеддон: " + Inner(e).Message); _armaPlayAt = -1; }
        }

        private void OnRemoteArmageddon(string who)
        {
            if (!_inRun || _battleMgr == null) return;
            // свой взрыв идёт — его объекты заняты игрой; повтор уже идёт — не начинаем заново
            double now = Clock.Elapsed.TotalSeconds;
            if (OwnArmaRunning() || now < _armaBusyUntil) { _armaSkipped++; return; }
            _armaBusyUntil = now + 5.5;
            _armaPlayed++;
            string how = StartArmaCoroutines();
            if (how == null) { _armaPlayAt = now; _armaStep = 0; how = "объекты по таблице"; }
            StartTrace(now, $"взрыв «{who}» ({how})");
        }

        /// <summary>Корутины картинки взрыва у себя (StartCoroutine по имени); null — не вышло.</summary>
        private string StartArmaCoroutines()
        {
            if (_armaCoroutinesFailed) return null;
            try
            {
                _startCoroutine ??= _battleMgr.GetType().GetMethod("StartCoroutine", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (_startCoroutine == null) throw new MissingMethodException("StartCoroutine(string)");
                foreach (var name in ArmaCoroutines) _startCoroutine.Invoke(_battleMgr, new object[] { name });
                return "корутины " + string.Join(", ", ArmaCoroutines);
            }
            catch (Exception e)
            {
                _armaCoroutinesFailed = true;
                _ctx.Log("Армагеддон: корутины не запустить (" + Inner(e).Message + ") — дальше объекты по таблице");
                return null;
            }
        }

        // ------------------------------------------------------------ раскладка взрыва в журнал (первые 2 взрыва боя)

        private void StartTrace(double now, string what)
        {
            if (_armaTraces >= 2 || _traceAt >= 0) return;
            _armaTraces++;
            _traceAt = now; _traceWhat = what; _traceNextEnemy = 0;
            _trace.Clear(); _traceLast.Clear(); _traceEnemies.Clear();
        }

        private void TraceStep(double now)
        {
            double t = now - _traceAt;
            if (t >= _traceNextEnemy && _traceNextEnemy <= 6)
            {
                int n; try { n = _enemies.Alive.Count; } catch { n = -1; }
                _traceEnemies.Add($"{_traceNextEnemy} с: {n}");
                _traceNextEnemy += _traceNextEnemy == 0 ? 1 : 2;
            }
            foreach (var f in ArmaFields)
            {
                string st = ObjVis(f);
                if (_traceLast.TryGetValue(f, out var last) && last == st) continue;
                _traceLast[f] = st;
                if (!_trace.TryGetValue(f, out var list)) _trace[f] = list = new List<string>();
                if (list.Count < 24) list.Add($"{t:0.00} {st}");
            }
            if (t < ArmaTraceSec) return;
            _traceAt = -1;
            _ctx.Log($"Армагеддон, {_traceWhat}: врагов {string.Join(", ", _traceEnemies)}; " +
                     string.Join(" | ", ArmaFields.Select(f => ArmaLabel(f) + ": " + TraceOf(f))));
        }
        private static string ArmaLabel(string f) => f == "Obj_Armageddon" ? "Вспышка" : f.Substring("Obj_Armageddon".Length).TrimStart('_');
        private string TraceOf(string f) => _trace.TryGetValue(f, out var l) ? string.Join("; ", l) : "—";

        /// <summary>Вид объекта взрыва: включён, рендер, прозрачность, кадр, масштаб.</summary>
        private string ObjVis(string field)
        {
            var go = GetMember(_battleMgr, field);
            if (go == null) return "нет";
            bool act = GetMember(go, "activeSelf") is bool a && a;
            if (!act) return "выкл";
            bool ui = field == "Obj_ArmageddonGround_Front";
            var comp = ui ? GetMember(_battleMgr, "Img_ArmageddonGround_Front") : RendererOf(go);
            var en = EnabledOf(comp, ui);
            var c = ColorOf(comp, ui);
            var sc = GetMember(GetMember(go, "transform"), "localScale") is UnityEngine.Vector3 v ? v.x : float.NaN;
            return $"вкл{(en == false ? " рендер выкл" : "")} α{(c == null ? "?" : c[3].ToString("0.00"))}{(ui ? "" : $" «{NameOf(SpriteOf(comp))}»")} ×{sc:0.##}";
        }

        /// <summary>Конец боя посреди повтора по таблице — выключить всё, что включили (если свой взрыв не идёт).</summary>
        private void StopArmaReplay()
        {
            _armaPlayAt = -1;
            try
            {
                if (_battleMgr == null || OwnArmaRunning()) return;
                foreach (var f in ArmaFields) { var go = GetMember(_battleMgr, f); if (go != null) SetActive(go, false); }
            }
            catch { }
        }

        private string ArmageddonSummary() => _armaSent + _armaPlayed + _armaSkipped == 0 ? "" :
            $"; Армагеддон: своих взрывов отправлено {_armaSent}, повторено взрывов друзей {_armaPlayed}, пропущено {_armaSkipped}";
    }
}
