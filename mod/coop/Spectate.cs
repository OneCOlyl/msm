using System;
using System.Linq;
using System.Reflection;
using ModKit;
using UnityEngine;

namespace MagicSurvivalCoop
{
    // Свой мир после смерти остаётся боевым: игра не строит итог, камера идёт за живым другом.
    internal sealed partial class Session
    {
        private const double SpectateLostAfter = 10;
        private const double SpectateStaleMs = 15000;
        private const int SpectateButtonFailLimit = 5;

        private static Session _spectateOwner;
        private static object _spectateHarmony;
        private static Type _spectateHarmonyType;
        private static bool _spectatePatchLogged;
        [ThreadStatic] private static int _companionCalls;

        private bool _specActive, _specPending, _specEnded, _specButtonsOff;
        private Partner _specTarget;
        private double _specLostAt = -1, _specUiAt;
        private int _specFails, _specButtonFails, _specCamFails;
        private Transform _specAnchor, _specCamera;
        private double _specCamX, _specCamY, _specCamAt;
        private bool _specCamReady, _specAfterHook;
        private SpriteRenderer _heroSr;
        private UnityEngine.UI.Image _hurtImg;
        private Color _hurtColor;
        private bool _hurtSaved;
        private Sprite _heroSprite;
        private Color _heroColor;
        private bool _heroLookSaved;
        private double _heroLookAt;
        private string _heroSpriteName;
        private UiKit.HudButton _specBtn, _specExitBtn;
        private string _specBtnText;
        private Action _spectateAction;

        private static readonly MethodInfo SpectatePrefix =
            typeof(Session).GetMethod(nameof(SpectateEndPrefix), BindingFlags.Public | BindingFlags.Static);

        // Harmony не даёт заменить конец боя на «продолжить»: префикс возвращает false, когда бой остаётся.
        public static bool SpectateEndPrefix()
        {
            var s = _spectateOwner;
            if (s == null) return true;
            try { return !s.KeepBattleOnEnd(); }
            catch { return true; }
        }

        // Библиотека союзников не создаёт и не двигает копию, когда герой вне боя.
        // Подменяем «бой» только на время её вызовов: иначе мёртвый герой снова кастует через modkit_spells.
        public static bool SpectateInBattlePrefix(ref bool __result)
        {
            var s = _spectateOwner;
            if (s == null || s._shutdown || !s._specActive || _companionCalls <= 0) return true;
            __result = true;
            return false;
        }

        private static void CompanionEnter() => _companionCalls++;

        private static void CompanionExit() { if (_companionCalls > 0) _companionCalls--; }

        private bool KeepBattleOnEnd()
        {
            if (_shutdown) return false;
            if (_specActive || _specPending || _specEnded) return true;
            if (!_inRun || !HasSpectateTarget()) return false;
            bool dead;
            try { dead = _hero.Hp <= 0; } catch { dead = false; }
            if (!dead) return false;
            _specPending = true;
            return true;
        }

        private void InstallSpectate()
        {
            _spectateOwner = this;
            _spectateAction = SpectateStep;
            EnsureSpectatePatch();
        }

        private void EnsureSpectatePatch()
        {
            if (_spectateHarmony != null) return;
            int step = 0;
            try
            {
                var harmonyType = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => { try { return a.GetType("HarmonyLib.Harmony"); } catch { return null; } })
                    .FirstOrDefault(t => t != null);
                if (harmonyType == null) throw new MissingMemberException("HarmonyLib.Harmony");
                step = 1;
                var methodType = harmonyType.Assembly.GetType("HarmonyLib.HarmonyMethod");
                var hooks = typeof(BattleState).Assembly.GetType("ModKit.Hooks");
                var gameType = hooks?.GetMethod("FindType", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    ?.Invoke(null, new object[] { "C_Battle_AllyUnit" }) as Type;
                if (methodType == null || gameType == null) throw new MissingMemberException("C_Battle_AllyUnit");
                step = 2;
                var hx = Activator.CreateInstance(harmonyType, "coop-spectate");
                var hm = Activator.CreateInstance(methodType, SpectatePrefix);
                var hmBattle = Activator.CreateInstance(methodType, typeof(Session).GetMethod(nameof(SpectateInBattlePrefix), BindingFlags.Public | BindingFlags.Static));
                step = 3;
                var patch = harmonyType.GetMethod("CreateProcessor", new[] { typeof(MethodBase) });
                var procType = harmonyType.Assembly.GetType("HarmonyLib.PatchProcessor");
                var add = procType?.GetMethod("AddPrefix", new[] { methodType });
                if (patch == null || add == null || hm == null || hmBattle == null) throw new MissingMethodException("Harmony CreateProcessor/AddPrefix");
                step = 4;
                int n = 0;
                void Patch(MethodInfo m, object prefix, string name)
                {
                    var proc = patch.Invoke(hx, new object[] { m });
                    step = 5;
                    add.Invoke(proc, new[] { prefix });
                    step = 6;
                    proc.GetType().GetMethod("Patch", Array.Empty<Type>()).Invoke(proc, null);
                    n++;
                    _ctx.Log($"слежение: хук {name} поставлен");
                }
                foreach (var name in new[] { "Game_EndON", "Button_RevivalCancel" })
                {
                    try
                    {
                        var m = gameType.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (m != null) Patch(m, hm, name);
                    }
                    catch (Exception e) { _ctx.Log($"слежение: хук {name} не поставлен — шаг {step}: {Inner(e)}"); }
                }
                try
                {
                    var m = typeof(BattleState).GetMethod("get_InBattle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (m != null) Patch(m, hmBattle, "BattleState.InBattle");
                }
                catch (Exception e) { _ctx.Log("слежение: хук InBattle не поставлен — шаг " + step + ": " + Inner(e)); }
                try
                {
                    var companionType = typeof(ICompanions).Assembly.GetType("ModKit.CompanionsImpl");
                    var enter = Activator.CreateInstance(methodType, typeof(Session).GetMethod(nameof(CompanionEnter), BindingFlags.NonPublic | BindingFlags.Static));
                    var exit = Activator.CreateInstance(methodType, typeof(Session).GetMethod(nameof(CompanionExit), BindingFlags.NonPublic | BindingFlags.Static));
                    var addPost = procType?.GetMethod("AddPostfix", new[] { methodType });
                    foreach (var name in new[] { "Spawn", "Tick" })
                    {
                        var m = companionType?.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (m == null) continue;
                        var proc = patch.Invoke(hx, new object[] { m });
                        add.Invoke(proc, new[] { enter });
                        addPost.Invoke(proc, new[] { exit });
                        proc.GetType().GetMethod("Patch", Array.Empty<Type>()).Invoke(proc, null);
                        n++;
                    }
                }
                catch (Exception e) { _ctx.Log("слежение: хуки союзников не поставлены — шаг " + step + ": " + Inner(e)); }
                if (n == 0) throw new MissingMemberException("Game_EndON, Button_RevivalCancel, InBattle");
                _spectateHarmony = hx;
                _spectateHarmonyType = harmonyType;
                _ctx.Log($"слежение: хуков поставлено {n}");
            }
            catch (Exception e)
            {
                if (!_spectatePatchLogged) { _spectatePatchLogged = true; _ctx.Log($"слежение: хук конца боя не поставлен — шаг {step}: {Inner(e)}"); }
            }
        }

        private void RemoveSpectatePatch()
        {
            if (!ReferenceEquals(_spectateOwner, this)) return;
            _spectateOwner = null;
            if (_spectateHarmony == null) return;
            try { _spectateHarmonyType?.GetMethod("UnpatchSelf", Array.Empty<Type>())?.Invoke(_spectateHarmony, null); } catch { }
            _spectateHarmony = null;
            _spectateHarmonyType = null;
        }

        private void SpectateStep()
        {
            if (_shutdown) return;
            try
            {
                double now = Clock.Elapsed.TotalSeconds;
                if (!_inRun)
                {
                    if (_specActive || _specPending) StopSpectate();
                    return;
                }
                CaptureHeroLook();
                if (_specEnded) return;
                if (_specActive)
                {
                    bool alive;
                    try { alive = _hero.Hp > 0; } catch { alive = false; }
                    if (alive) { StopSpectate(); _ctx.Toast("Вы снова в бою", 2); return; }
                }
                if (_specPending && !_specActive && !BeginSpectate())
                {
                    _specPending = false;
                    EndBattleNow();
                    return;
                }
                if (!_specActive) return;

                HideRevival();
                RestoreSpectateTime();
                foreach (var p in _partners.Values) if (p.Ghost != null && p.Ghost.Alive && p.Ghost.Speed < 1000) p.Ghost.Speed = 1000;
                if (!HasWatchedTarget())
                {
                    if (_specLostAt < 0) _specLostAt = now;
                    else if (now - _specLostAt >= SpectateLostAfter) { EndSpectateByUser(); return; }
                }
                else
                {
                    _specLostAt = -1;
                    if (!IsWatched(_specTarget)) SelectSpectateTarget(cycle: false, fresh: false);
                }
                if (now >= _specUiAt) { _specUiAt = now + 0.25; UpdateSpectateButtons(); }
            }
            catch (Exception e)
            {
                if (++_specFails <= 3) _ctx.Log("слежение: " + Inner(e).Message);
            }
        }

        private bool BeginSpectate()
        {
            if (!SelectSpectateTarget(cycle: false, fresh: true)) return false;
            _specActive = true;
            _specPending = false;
            _specLostAt = -1;
            _specAnchor = _specCamera = null;
            _specCamReady = false;
            CaptureHeroLook(force: true);
            ApplyHeroLook();
            foreach (var p in _partners.Values)
            {
                if (p.Ghost != null && p.Ghost.Alive) { try { p.Ghost.Remove(); } catch { } }
                p.Ghost = null; p.NextSpawn = 0; p.HasWorldPos = false;
            }
            HideRevival();
            RestoreSpectateTime();
            try { CallMethod(RawHero(), "StopAllCoroutines"); } catch { }   // мёртвый герой не должен продолжать кастовать
            _ctx.Log($"слежение: смотрю бой «{_specTarget.Name}»");
            _ctx.Toast($"Следим за «{_specTarget.Name}»", 3);
            UpdateSpectateButtons();
            return true;
        }

        private void StopSpectate()
        {
            RestoreHeroLook();
            _specActive = _specPending = false;
            _specTarget = null;
            _specLostAt = -1;
            _specUiAt = 0;
            _specAnchor = _specCamera = null;
            _specCamReady = false;
            _companionCalls = 0;
            DestroySpectateButtons();
        }

        private void EndSpectateByUser()
        {
            if (!_specActive && !_specPending) return;
            _specEnded = true;
            StopSpectate();
            _ctx.Log("слежение: вышел из боя");
            EndBattleNow();
        }

        private void EndBattleNow()
        {
            try
            {
                if (_battleMgr == null) { _specEnded = false; _ctx.Warn("слежение: менеджер боя не найден"); return; }
                var end = _battleMgr.GetType().GetMethod("End_Battle", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (end == null) { _specEnded = false; _ctx.Warn("слежение: в менеджере боя нет End_Battle"); return; }
                end.Invoke(_battleMgr, null);
            }
            catch (Exception e) { _specEnded = false; _ctx.Log("слежение: завершить бой не удалось — " + Inner(e).Message); }
        }

        private bool HasSpectateTarget() => _partners.Values.Any(IsFreshTarget);

        private bool HasWatchedTarget() => _partners.Values.Any(IsWatched);

        private bool IsFreshTarget(Partner p) =>
            p != null && p.HasState && _clockMs - p.StateAt < 3000 && p.Run && p.Battle && !p.Dead && p.Hp > 0;

        // Друг может на секунду пропасть из сети или из боя: камеру не бросаем сразу.
        private bool IsWatched(Partner p) =>
            p != null && p.HasState && _clockMs - p.StateAt < SpectateStaleMs && !p.Dead && p.Hp > 0;

        private bool SelectSpectateTarget(bool cycle, bool fresh)
        {
            bool Valid(Partner p) => fresh ? IsFreshTarget(p) : IsWatched(p);
            if (Valid(_specTarget)) { if (!cycle) return true; }
            Partner first = null, next = null;
            int slot = _specTarget?.Slot ?? int.MinValue;
            foreach (var p in _partners.Values.OrderBy(x => x.Slot))
            {
                if (!Valid(p)) continue;
                first ??= p;
                if (next == null && p.Slot > slot) next = p;
            }
            var pick = cycle ? (next ?? first) : first;
            if (pick == null) { _specTarget = null; return false; }
            if (!ReferenceEquals(pick, _specTarget))
            {
                _specTarget = pick;
                _specLostAt = -1;
                _specCamReady = false;
                if (_specActive) { _ctx.Toast($"Следим за «{pick.Name}»", 2); UpdateSpectateButtons(); }
            }
            return true;
        }

        private void CycleSpectate()
        {
            if (_specActive) SelectSpectateTarget(cycle: true, fresh: false);
        }

        private static double HudHeight()
        {
            try { return Math.Max(600, Screen.height * 720.0 / Math.Max(1, Screen.width)); } catch { return 1280; }
        }

        private void UpdateSpectateButtons()
        {
            if (!_specActive || _specButtonsOff) { if (!_specActive) DestroySpectateButtons(); return; }
            try
            {
                string text = _specTarget != null ? "Следить: " + _specTarget.Name : "Следить";
                double y = HudHeight() - 96;
                if (_specBtn == null || !_specBtn.Alive)
                {
                    DestroyBtn(ref _specBtn);
                    _specBtn = UiKit.AddHudButton("coop_spectate", text, 170, y, 350, 64, 22, BtnBlue, () => _ctx.Guard("слежение", CycleSpectate));
                    _specBtnText = text;
                }
                else if (_specBtnText != text) { _specBtn.SetText(text); _specBtnText = text; }
                _specBtn.SetVisible(true);
                if (_specExitBtn == null || !_specExitBtn.Alive)
                {
                    DestroyBtn(ref _specExitBtn);
                    _specExitBtn = UiKit.AddHudButton("coop_spectate_exit", "Выйти", 530, y, 170, 64, 22, BtnRed, () => _ctx.Guard("слежение: выход", EndSpectateByUser));
                }
                _specExitBtn.SetVisible(true);
            }
            catch (Exception e)
            {
                if (++_specButtonFails <= 3) _ctx.Log("слежение: кнопки — " + Inner(e).Message);
                if (_specButtonFails >= SpectateButtonFailLimit) _specButtonsOff = true;
                _specBtn = _specExitBtn = null;
                _specBtnText = null;
            }
        }

        private void DestroySpectateButtons()
        {
            DestroyBtn(ref _specBtn);
            DestroyBtn(ref _specExitBtn);
            _specBtnText = null;
        }

        private void CaptureHeroLook(bool force = false)
        {
            double now = Clock.Elapsed.TotalSeconds;
            if (!force && now < _heroLookAt) return;
            _heroLookAt = now + 0.5;
            try
            {
                var hero = RawHero();
                if (hero == null) return;
                if (_heroSr == null) _heroSr = GetMember(hero, "Motion_Unit") as SpriteRenderer;
                if (_heroSr == null) return;
                var sp = GetMember(_heroSr, "sprite") as Sprite;
                if (sp == null) return;
                var name = GetMember(sp, "name") as string;
                if (string.IsNullOrEmpty(name) || name == "Sprite_Trans" || NameIsEffect(name)) return;
                if (_heroSpriteName != name && _heroSprite == null) _ctx.Log($"слежение: модель героя — «{name}»");
                _heroSpriteName = name;
                _heroSprite = sp;
            }
            catch { }
        }

        private static bool NameIsEffect(string name) =>
            name.Contains("Explosion") || name.Contains("Death") || name.Contains("Hit") ||
            name.Contains("Shadow") || name.Contains("Effect") || name.Contains("Blood") || name.Contains("Hurt");

        // У погибшего героя остаётся красная вспышка урона, а анимация становится прозрачной.
        private void ApplyHeroLook()
        {
            if (!_specActive || _shutdown) return;
            HideHurt();
            if (_heroSprite == null || _heroSr == null) return;
            try
            {
                if (!_heroLookSaved) { _heroColor = _heroSr.color; _heroLookSaved = true; }
                _heroSr.sprite = _heroSprite;
                var c = _heroSr.color;
                if (c.a != 0) { c.a = 0; _heroSr.color = c; }
                foreach (var p in _partners.Values)
                {
                    if (p.Ghost == null || !p.Ghost.Alive) continue;
                    p.GhostSr ??= GetMember(p.Ghost, "Sr") as SpriteRenderer;
                    if (p.GhostSr != null) p.GhostSr.sprite = _heroSprite;
                }
            }
            catch { }
        }

        private void HideHurt()
        {
            try
            {
                if (_hurtImg == null) _hurtImg = GetMember(RawHero(), "Img_HurtGround") as UnityEngine.UI.Image;
                if (_hurtImg == null) return;
                if (!_hurtSaved) { if (GetMember(_hurtImg, "color") is Color saved) { _hurtColor = saved; _hurtSaved = true; } }
                if (GetMember(_hurtImg, "color") is Color c && c.a != 0) { c.a = 0; SetMember(_hurtImg, "color", c); }
            }
            catch { }
        }

        private void RestoreHeroLook()
        {
            if (_hurtSaved)
            {
                _hurtSaved = false;
                try { if (_hurtImg != null) SetMember(_hurtImg, "color", _hurtColor); } catch { }
            }
            if (!_heroLookSaved) return;
            _heroLookSaved = false;
            try { if (_heroSr != null) _heroSr.color = _heroColor; } catch { }
        }

        private void SpectateVisualStep()
        {
            if (!_specActive) return;
            ApplyHeroLook();
            foreach (var p in _partners.Values)
            {
                if (p.Ghost == null || !p.Ghost.Alive) continue;
                if (p.GhostTr == null) EnsureGhostTr(p);
                if (p.GhostTr == null) continue;
                try { var v = p.GhostTr.position; p.WorldX = v.x; p.WorldY = v.y; p.WorldZ = v.z; p.HasWorldPos = true; } catch { }
            }
            SpectateCameraStep();
        }

        private void HideRevival()
        {
            try
            {
                var go = GetMember(RawHero(), "Obj_FieldRevival") as GameObject;
                if (go != null && go.activeSelf) go.SetActive(false);
            }
            catch { }
        }

        private void RestoreSpectateTime()
        {
            try { if (Time.timeScale < 0.001f && !MenuPaused()) Time.timeScale = 1f; } catch { }
        }

        private void SpectateCameraStep()
        {
            if (!_specActive || _shutdown) return;
            var p = _specTarget;
            if (!IsWatched(p) || !p.HasWorldPos || _battleMgr == null) return;
            try
            {
                _specAnchor ??= GetMember(_battleMgr, "Transform_UnitAllyCamera") as Transform;
                _specCamera ??= GetMember(_battleMgr, "Transform_MainCamera") as Transform;
                double now = Clock.Elapsed.TotalSeconds;
                double dt = _specCamAt > 0 ? Math.Min(0.1, Math.Max(0.001, now - _specCamAt)) : 0.016;
                _specCamAt = now;
                if (!_specCamReady) { _specCamX = p.WorldX; _specCamY = p.WorldY; _specCamReady = true; }
                else
                {
                    double k = 1 - Math.Exp(-dt * 20);
                    _specCamX += (p.WorldX - _specCamX) * k;
                    _specCamY += (p.WorldY - _specCamY) * k;
                }
                float x = (float)_specCamX, y = (float)_specCamY;
                if (_specAnchor != null) { var a = _specAnchor.position; _specAnchor.position = new Vector3(x, y, a.z); }
                if (_specCamera != null) { var c = _specCamera.position; _specCamera.position = new Vector3(x, y, c.z); }
            }
            catch (Exception e)
            {
                if (++_specCamFails <= 3) _ctx.Log("слежение: камера — " + Inner(e).Message);
            }
        }

        private void ResetSpectate()
        {
            StopSpectate();
            _specEnded = false;
            _specFails = _specButtonFails = _specCamFails = 0;
            _specButtonsOff = false;
            _heroSr = null; _hurtImg = null; _hurtSaved = false; _heroSprite = null; _heroSpriteName = null; _heroLookSaved = false; _heroLookAt = 0;
            _spectateAction = SpectateStep;
            EnsureSpectatePatch();
        }
    }
}
