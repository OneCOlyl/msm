using System;
using System.Reflection;
using MagicSurvivalCoop.Net;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Армагеддон друга (docs/ARMAGEDDON.md). Снарядов у него нет: взрыв рисует менеджер боя корутинами, пока
    /// IsArmageddonContinue = True. Начало взрыва ловим по этому флагу и шлём событие «arma». Получатель запускает у себя
    /// корутины картинки взрыва, а если не вышло, включает объекты по таблице ArmaSteps.
    /// </summary>
    internal sealed partial class Session
    {
        // Время от начала взрыва, поле менеджера, включить
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

        private object _battleMgr;                 // C_Battle_Manager из хука его Update
        private bool _armaWas;
        private double _armaCheckAt;
        private double _armaBusyUntil;
        private double _armaPlayAt = -1;           // Начало повтора по таблице, -1 если не идёт
        private int _armaStep;
        private int _armaSent, _armaPlayed, _armaSkipped, _armaFails;
        private bool _armaCoroutinesFailed;
        private MethodInfo _startCoroutine;

        private void ResetArmageddon()
        {
            if (_armaPlayAt >= 0) StopArmaReplay();
            _armaWas = false; _armaCheckAt = 0; _armaPlayAt = -1; _armaBusyUntil = 0;
            _armaSent = _armaPlayed = _armaSkipped = 0;
        }

        private bool OwnArmaRunning() => GetMember(_battleMgr, "IsArmageddonContinue") is bool b && b;

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
                    }
                    _armaWas = on;
                }
                if (_armaPlayAt >= 0)
                {
                    double t = now - _armaPlayAt;
                    while (_armaStep < ArmaSteps.Length && ArmaSteps[_armaStep].t <= t)
                    {
                        _ctx.Mark("Армагеддон друга: объекты взрыва по таблице");
                        var s = ArmaSteps[_armaStep++];
                        var go = GetMember(_battleMgr, s.field);
                        if (go != null) SetActive(go, s.on);
                    }
                    if (_armaStep >= ArmaSteps.Length) _armaPlayAt = -1;
                }
            }
            catch (Exception e) { if (++_armaFails <= 3) _ctx.Log("Армагеддон: " + Inner(e).Message); _armaPlayAt = -1; }
        }

        private void OnRemoteArmageddon()
        {
            if (!_inRun || _battleMgr == null) return;
            // Свой взрыв идёт, и его объекты заняты игрой
            double now = Clock.Elapsed.TotalSeconds;
            if (OwnArmaRunning() || now < _armaBusyUntil) { _armaSkipped++; return; }
            _armaBusyUntil = now + 5.5;
            _armaPlayed++;
            if (!StartArmaCoroutines()) { _armaPlayAt = now; _armaStep = 0; }
        }

        private bool StartArmaCoroutines()
        {
            if (_armaCoroutinesFailed) return false;
            try
            {
                _startCoroutine ??= _battleMgr.GetType().GetMethod("StartCoroutine", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
                if (_startCoroutine == null) throw new MissingMethodException("StartCoroutine(string)");
                _ctx.Mark("Армагеддон друга: запуск корутин взрыва");
                foreach (var name in ArmaCoroutines) _startCoroutine.Invoke(_battleMgr, new object[] { name });
                return true;
            }
            catch (Exception e)
            {
                _armaCoroutinesFailed = true;
                _ctx.Log("Армагеддон: корутины не запустить (" + Inner(e).Message + ") — дальше объекты по таблице");
                return false;
            }
        }

        /// <summary>Выключает включённое повтором по таблице, если свой взрыв не идёт.</summary>
        private void StopArmaReplay()
        {
            _armaPlayAt = -1;
            try
            {
                if (_battleMgr == null || OwnArmaRunning()) return;
                _ctx.Mark("Армагеддон: выключаю объекты повтора");
                foreach (var f in ArmaFields) { var go = GetMember(_battleMgr, f); if (go != null) SetActive(go, false); }
            }
            catch { }
        }

        private string ArmageddonSummary() => _armaSent + _armaPlayed + _armaSkipped == 0 ? "" :
            $"; Армагеддон: своих взрывов отправлено {_armaSent}, повторено взрывов друзей {_armaPlayed}, пропущено {_armaSkipped}";
    }
}
