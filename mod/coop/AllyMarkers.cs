using System;
using ModKit;
using UnityEngine;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Метки союзников в бою (1.6.50): полоска здоровья под моделью союзника, а когда союзник за краем экрана — стрелка
    /// у края в его сторону с именем. Полоска (1.6.51) — копия родной полоски героя (Obj_UnitHpBar), не получилось — своя. Узлы — на канвасе ModKit (UiKit.CanvasTf, его ставит первая полоска HUD), точка
    /// на экране — через камеру игры. Каждый кадр — только перестановка узлов, без выделений памяти.
    /// </summary>
    internal sealed partial class Session
    {
        private const double BarW = 60, BarH = 9;          // полоска под моделью, логические единицы UiKit (ширина экрана 720)
        private const double BarDrop = 34;                 // насколько ниже точки опоры союзника
        private const double ArrowEdge = 48;               // отступ стрелки от края экрана
        private const double TagW = 170, TagH = 30;          // плашка с именем у стрелки (она же полоска здоровья)
        private static readonly double[] TagHpColor = { 0.25, 0.85, 0.30, 0.55 };   // полупрозрачный зелёный
        private const int MarkerFailLimit = 20;

        private sealed class Marker
        {
            public GameObject Bar, Arrow, Tag;
            public RectTransform BarRt, FillRt, ArrowRt, TagRt, TagFillRt;
            public UnityEngine.UI.Text TagText;
            public UnityEngine.UI.Image Front;
            public UnityEngine.UI.Image ArrowImg;       // стрелка из меню игры (спрайт Button_AreaMoveR); null — своя «>»          // заливка копии родной полоски (fillAmount); null — своя полоска
            public double Drop;
            public double Frac = -1, TagFrac = -1;
            public bool BarOn, ArrowOn, TagOn;
            public string Name;
        }

        private GameObject _markerRoot;
        private bool _markerRootOn;
        private Camera _cam;
        private double _camRetryAt;
        private int _markerFails;
        private bool _markersOff, _camMissLogged;

        /// <summary>Каждый кадр боя, после размещения союзников.</summary>
        private void UpdateMarkers(double now)
        {
            if (_markersOff) return;
            try
            {
                bool want = _inRun && !_spells.Paused && _partners.Count > 0;
                if (!want || !EnsureMarkerRoot()) { ShowMarkerRoot(false); return; }
                var cam = GameCamera(now);
                if (cam == null) { ShowMarkerRoot(false); return; }
                ShowMarkerRoot(true);

                double k = UiKit.K;
                double u = 720.0 * k / Math.Max(1, Screen.width);      // единиц канваса на пиксель экрана
                double cw = Screen.width * u, ch = Screen.height * u;
                foreach (var p in _partners.Values)
                {
                    if (p.Ghost == null || !p.Ghost.Alive || !p.HasWorldPos) { if (p.Mk != null) { SetOn(p.Mk.Bar, ref p.Mk.BarOn, false); SetOn(p.Mk.Arrow, ref p.Mk.ArrowOn, false); SetOn(p.Mk.Tag, ref p.Mk.TagOn, false); } continue; }
                    var m = p.Mk ??= NewMarker(p);
                    var sp = cam.WorldToScreenPoint(new Vector3((float)p.WorldX, (float)p.WorldY, (float)p.WorldZ));
                    double x = sp.x * u, y = sp.y * u;

                    if (x >= 0 && x <= cw && y >= 0 && y <= ch)
                    {
                        SetOn(m.Arrow, ref m.ArrowOn, false); SetOn(m.Tag, ref m.TagOn, false);
                        m.BarRt.anchoredPosition = new Vector2((float)x, (float)(y - m.Drop));
                        double frac = HpFrac(p);
                        if (Math.Abs(frac - m.Frac) > 0.002)
                        {
                            m.Frac = frac;
                            if (m.Front != null) m.Front.fillAmount = (float)frac;
                            else UiKit.Place(m.FillRt, 1.5, 1.5, (BarW - 3) * frac, BarH - 3);
                        }
                        SetOn(m.Bar, ref m.BarOn, true);
                        continue;
                    }

                    // союзник за экраном: стрелка на краю по лучу из центра экрана к нему
                    SetOn(m.Bar, ref m.BarOn, false);
                    double dx = x - cw / 2, dy = y - ch / 2;
                    double edge = ArrowEdge * k;
                    double s = Math.Min(Math.Abs(dx) > 1e-3 ? (cw / 2 - edge) / Math.Abs(dx) : double.MaxValue,
                                        Math.Abs(dy) > 1e-3 ? (ch / 2 - edge) / Math.Abs(dy) : double.MaxValue);
                    double ax = cw / 2 + dx * s, ay = ch / 2 + dy * s;
                    double ang = Math.Atan2(dy, dx);
                    m.ArrowRt.anchoredPosition = new Vector2((float)ax, (float)ay);
                    m.ArrowRt.localEulerAngles = new Vector3(0, 0, (float)(ang * 180 / Math.PI));
                    SetOn(m.Arrow, ref m.ArrowOn, true);
                    // имя — от стрелки к центру экрана, чтобы не уходило за край
                    double cos = Math.Cos(ang), sin = Math.Sin(ang);
                    double back = (Math.Abs(cos) * (TagW / 2 + 28) + Math.Abs(sin) * 40) * k;   // сбоку подпись шире — отступ больше
                    double tx = Math.Max(TagW / 2 * k + 4, Math.Min(cw - TagW / 2 * k - 4, ax - cos * back));
                    double ty = Math.Max(TagH / 2 * k + 4, Math.Min(ch - TagH / 2 * k - 4, ay - sin * back));
                    m.TagRt.anchoredPosition = new Vector2((float)tx, (float)ty);
                    if (m.Name != p.Name) { m.Name = p.Name; m.TagText.text = p.Name; }
                    double hp = HpFrac(p);
                    if (Math.Abs(hp - m.TagFrac) > 0.002) { m.TagFrac = hp; UiKit.Place(m.TagFillRt, 0, 0, TagW * hp, TagH); }
                    SetOn(m.Tag, ref m.TagOn, true);
                }
            }
            catch (Exception e)
            {
                if (++_markerFails == 1 || _markerFails == MarkerFailLimit) _ctx.Log($"метки союзников: {e.Message}" + (_markerFails >= MarkerFailLimit ? " — выключены до конца боя" : ""));
                if (_markerFails >= MarkerFailLimit) { _markersOff = true; DestroyMarkers(); }
                else _cam = null;   // камера или канвас сменились вместе со сценой — найдём заново
            }
        }

        private static void SetOn(GameObject go, ref bool state, bool on)
        {
            if (state == on || go == null) return;
            state = on;
            go.SetActive(on);
        }

        private void ShowMarkerRoot(bool on)
        {
            if (_markerRoot == null || _markerRootOn == on) return;
            _markerRootOn = on;
            _markerRoot.SetActive(on);
        }

        /// <summary>Общий узел меток на канвасе ModKit; канвас сменился (новая сцена) — узел пересоздаётся.</summary>
        private bool EnsureMarkerRoot()
        {
            if (_markerRoot != null) return true;          // уничтоженный вместе со сценой узел Unity сравнивает с null
            double now = Clock.Elapsed.TotalSeconds;
            if (now < _canvasTryAt) return false;
            _canvasTryAt = now + 1;
            DestroyMarkers();
            // канвас боя ModKit находит сам при создании своих элементов HUD; их в бою у мода больше нет (1.6.54) — просим явно
            try { EnsureHudCanvas?.Invoke(null, null); } catch { return false; }   // без Unity (тесты) канваса нет
            var canvas = UiKit.CanvasTf;
            if (canvas == null) return false;
            var (go, rt) = UiKit.Node("ModKitHud_coop_markers", canvas);
            UiKit.Stretch(rt);
            _markerRoot = go; _markerRootOn = true;
            return true;
        }

        private static readonly System.Reflection.MethodInfo EnsureHudCanvas =
            typeof(UiKit).GetMethod("EnsureHudCanvas", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        private double _canvasTryAt;

        private static double HpFrac(Partner p) => p.HasState ? Math.Max(0, Math.Min(1, p.Hp / p.HpMax)) : 0;

        private Marker NewMarker(Partner p)
        {
            var c = SlotColors[Math.Max(0, Math.Min(3, p.Slot - 1))];
            var parent = _markerRoot.transform;
            var m = new Marker();

            (m.Bar, m.BarRt) = UiKit.Node("bar_" + p.Id, parent);
            if (!CloneHeroHpBar(m))
            {
                // своя полоска: верх по центру — под точкой опоры союзника
                Pin(m.BarRt, 0.5, 1, BarW, BarH);
                m.Drop = BarDrop * UiKit.K;
                var back = UiKit.Node("back", m.BarRt);
                UiKit.Stretch(back.rt);
                UiKit.AddImage(back.go, 0, 0, 0, 0.65).raycastTarget = false;
                var fill = UiKit.Node("fill", m.BarRt);
                m.FillRt = fill.rt;
                UiKit.AddImage(fill.go, c[0], c[1], c[2], 1).raycastTarget = false;
            }

            (m.Arrow, m.ArrowRt) = UiKit.Node("arrow_" + p.Id, parent);
            Pin(m.ArrowRt, 0.5, 0.5, 0, 0);
            if (MenuArrowAlive())
            {
                // стрелка «вправо» с экрана выбора локации, уменьшенная и в цвет игрока; поворот узла — к союзнику
                var shadow = UiKit.Node("shadow", m.ArrowRt);           // тёмная тень под стрелкой — видна и на светлых картах
                shadow.rt.anchorMin = shadow.rt.anchorMax = UiKit.V2(0.5, 0.5);
                shadow.rt.pivot = UiKit.V2(0.5, 0.5);
                shadow.rt.anchoredPosition = UiKit.V2(0, 0);
                shadow.rt.sizeDelta = UiKit.V2((ArrowH + 8) * _menuArrowAspect * UiKit.K, (ArrowH + 8) * UiKit.K);
                var sh = UiKit.AddImage(shadow.go, 0, 0, 0, 0.75);
                sh.raycastTarget = false;
                UiKit.SetSprite(sh, _menuArrow);
                var img = UiKit.Node("img", m.ArrowRt);
                img.rt.anchorMin = img.rt.anchorMax = UiKit.V2(0.5, 0.5);
                img.rt.pivot = UiKit.V2(0.5, 0.5);
                img.rt.anchoredPosition = UiKit.V2(0, 0);
                img.rt.sizeDelta = UiKit.V2(ArrowH * _menuArrowAspect * UiKit.K, ArrowH * UiKit.K);
                m.ArrowImg = UiKit.AddImage(img.go, c[0], c[1], c[2], 1);
                m.ArrowImg.raycastTarget = false;
                UiKit.SetSprite(m.ArrowImg, _menuArrow);
            }
            else
            // своя стрелка «>»: тёмная обводка, поверх — цвет игрока
            foreach (var (len, thick, col) in new[] { (34.0, 14.0, new[] { 0.0, 0.0, 0.0, 0.7 }), (30.0, 8.0, new[] { c[0], c[1], c[2], 1.0 }) })
                foreach (var tilt in new[] { 40.0, -40.0 })
                {
                    var arm = UiKit.Node("arm", m.ArrowRt);
                    arm.rt.anchorMin = arm.rt.anchorMax = UiKit.V2(0.5, 0.5);
                    arm.rt.pivot = UiKit.V2(1, 0.5);                     // поворот вокруг острия
                    arm.rt.anchoredPosition = UiKit.V2(16 * UiKit.K, 0);
                    arm.rt.sizeDelta = UiKit.V2(len * UiKit.K, thick * UiKit.K);
                    arm.rt.localEulerAngles = new Vector3(0, 0, (float)tilt);
                    UiKit.AddImage(arm.go, col[0], col[1], col[2], col[3]).raycastTarget = false;
                }

            (m.Tag, m.TagRt) = UiKit.Node("tag_" + p.Id, parent);
            Pin(m.TagRt, 0.5, 0.5, TagW, TagH);
            // плашка — полоска здоровья: тёмная подложка, поверх зелёная заливка по доле здоровья, поверх имя
            var plate = UiKit.Node("plate", m.TagRt);
            UiKit.Stretch(plate.rt);
            UiKit.AddImage(plate.go, 0, 0, 0, 0.5).raycastTarget = false;
            var hpFill = UiKit.Node("hp", m.TagRt);
            m.TagFillRt = hpFill.rt;
            UiKit.Place(m.TagFillRt, 0, 0, 0, TagH);
            UiKit.AddImage(hpFill.go, TagHpColor[0], TagHpColor[1], TagHpColor[2], TagHpColor[3]).raycastTarget = false;
            m.TagText = UiKit.Label(m.TagRt, p.Name, 0, 0, TagW, TagH, 20, 4);
            m.TagText.raycastTarget = false;
            UiKit.Tint(m.TagText, UiKit.Col(1, 1, 1, 1));
            m.Name = p.Name;

            m.Bar.SetActive(false); m.Arrow.SetActive(false); m.Tag.SetActive(false);
            return m;
        }

        // ------------------------------------------------------------ стрелка из меню

        private const double ArrowH = 34;                 // высота стрелки у края экрана (в меню она ~70)
        private IntPtr _menuArrow;                        // спрайт стрелки «следующая локация»; удерживается Raw.Pin, иначе смена сцены его выгрузит
        private double _menuArrowAspect = 0.6, _menuArrowLookAt;
        private bool _menuArrowLogged;

        private bool MenuArrowAlive()
        {
            if (_menuArrow == IntPtr.Zero) return false;
            try { if (!Raw.IsDestroyed(_menuArrow)) return true; } catch { }
            _ctx.Log("метки союзников: спрайт стрелки из меню выгружен — стрелка своя");
            _menuArrow = IntPtr.Zero;
            return false;
        }

        /// <summary>
        /// Вне боя, раз в секунду, пока не нашли: на экране выбора локации взять картинку кнопки Button_AreaMoveR (стрелка вправо).
        /// Экран выбора локации игрок проходит перед каждым боем.
        /// </summary>
        private void CaptureMenuArrow()
        {
            if (_inRun || _menuArrow != IntPtr.Zero || !RawSprites) return;
            double now = Clock.Elapsed.TotalSeconds;
            if (now < _menuArrowLookAt) return;
            _menuArrowLookAt = now + 1;
            var go = FindActiveObject("Button_AreaMoveR");   // ModKit Screen.Buttons() на этом экране пуст — ищем объект сами
            if (go == null) return;
            var img = go.GetComponent<UnityEngine.UI.Image>();
            if (img == null) img = go.GetComponentInChildren<UnityEngine.UI.Image>(true);
            var sp = img == null ? IntPtr.Zero : UiKit.GetSprite(img);
            if (sp == IntPtr.Zero || Raw.IsDestroyed(sp)) { if (!_menuArrowLogged) { _menuArrowLogged = true; _ctx.Log("метки союзников: у Button_AreaMoveR нет картинки"); } return; }
            Raw.Pin(sp);
            _menuArrow = sp;
            var size = img.rectTransform.sizeDelta;
            if (size.x > 1 && size.y > 1) _menuArrowAspect = Math.Max(0.2, Math.Min(3, size.x / size.y));
            _ctx.Log($"метки союзников: стрелка из меню — «{NameOf(sp)}», {size.x:0}×{size.y:0}");
        }

        /// <summary>Активный объект сцены по имени (поиск по всем GameObject — только в меню, раз в секунду).</summary>
        private GameObject FindActiveObject(string name)
        {
            foreach (var o in FindAllOfType(typeof(GameObject), "GameObject"))
                if (o is GameObject go && go.name == name) return go;
            return null;
        }

        private int _hpBarFrontIdx = -2, _hpBarPlusIdx = -1;   // -2 — ещё не искали; -1 — не нашли

        /// <summary>
        /// Копия родной полоски здоровья героя внутрь m.Bar: тот же вид и размер, что у своего героя, и та же высота под моделью.
        /// Заливка — Image типа Filled (fillAmount), её ребёнок ищется по указателю Img_UnitHpBar_Front героя; полоска щита
        /// (Img_UnitHpPlusBar_Front) у копии пуста — щит друга не пересылается.
        /// </summary>
        private bool CloneHeroHpBar(Marker m)
        {
            try
            {
                var hero = RawHero();
                var tpl = GetMember(hero, "Obj_UnitHpBar") as GameObject;
                if (tpl == null) return false;
                var ttr = tpl.transform;
                if (_hpBarFrontIdx == -2)
                {
                    _hpBarFrontIdx = ChildIndexOf(ttr, (GetMember(hero, "Img_UnitHpBar_Front") as Component)?.gameObject);
                    _hpBarPlusIdx = ChildIndexOf(ttr, (GetMember(hero, "Img_UnitHpPlusBar_Front") as Component)?.gameObject);
                    _ctx.Log($"метки союзников: полоска героя «{tpl.name}», детей {ttr.childCount}, заливка [{_hpBarFrontIdx}], щит [{_hpBarPlusIdx}]");
                }
                if (_hpBarFrontIdx < 0) return false;
                var clone = CloneOf(tpl) as GameObject;
                if (clone == null) return false;
                var ctr = clone.transform;
                ctr.SetParent(m.BarRt, false);
                ctr.localPosition = new Vector3(0, 0, 0);
                clone.SetActive(true);
                m.Front = ctr.GetChild(_hpBarFrontIdx).gameObject.GetComponent<UnityEngine.UI.Image>();
                if (m.Front == null) { UiKit.SafeDestroy(clone); return false; }
                if (_hpBarPlusIdx >= 0) { var plus = ctr.GetChild(_hpBarPlusIdx).gameObject.GetComponent<UnityEngine.UI.Image>(); if (plus != null) plus.fillAmount = 0; }
                Pin(m.BarRt, 0.5, 0.5, 0, 0);
                m.Drop = -ttr.localPosition.y;   // героя полоска висит на столько ниже его точки (в единицах канваса)
                return true;
            }
            catch (Exception e)
            {
                _ctx.Log("метки союзников: родную полоску не скопировать, рисую свою — " + Inner(e).Message);
                _hpBarFrontIdx = -1;
                return false;
            }
        }

        /// <summary>Номер ребёнка tr, чей объект — go (сравнение по адресу объекта игры); -1 — не ребёнок.</summary>
        private static int ChildIndexOf(Transform tr, GameObject go)
        {
            if (tr == null || go == null) return -1;
            var want = PtrOf(go);
            for (int i = 0; i < tr.childCount; i++)
            {
                var c = tr.GetChild(i).gameObject;
                if (want != IntPtr.Zero ? PtrOf(c) == want : ReferenceEquals(c, go)) return i;
            }
            return -1;
        }

        /// <summary>Узел, который ставится по точке экрана: якорь — левый нижний угол канваса, pivot — точка привязки.</summary>
        private static void Pin(RectTransform rt, double pivotX, double pivotY, double w, double h)
        {
            rt.anchorMin = rt.anchorMax = UiKit.V2(0, 0);
            rt.pivot = UiKit.V2(pivotX, pivotY);
            rt.sizeDelta = UiKit.V2(w * UiKit.K, h * UiKit.K);
        }

        private Camera GameCamera(double now)
        {
            if (_cam != null) return _cam;
            if (now < _camRetryAt) return null;
            _camRetryAt = now + 1;
            _cam = Camera.main;
            if (_cam == null && !_camMissLogged) { _camMissLogged = true; _ctx.Log("метки союзников: камера игры не найдена"); }
            return _cam;
        }

        private static void DropMarker(Partner p)
        {
            if (p.Mk == null) return;
            UiKit.SafeDestroy(p.Mk.Bar);
            UiKit.SafeDestroy(p.Mk.Arrow);
            UiKit.SafeDestroy(p.Mk.Tag);
            p.Mk = null;
        }

        /// <summary>Убрать все метки (конец боя, выключение мода, смена канваса).</summary>
        private void DestroyMarkers()
        {
            foreach (var p in _partners.Values) p.Mk = null;   // узлы меток — дети общего узла и уходят вместе с ним
            UiKit.SafeDestroy(_markerRoot);
            _markerRoot = null; _markerRootOn = false;
        }

        private void ResetMarkers()
        {
            DestroyMarkers();
            _cam = null; _camRetryAt = 0; _canvasTryAt = 0;
            _markerFails = 0; _markersOff = false; _camMissLogged = false;
            _hpBarFrontIdx = -2;
            foreach (var p in _partners.Values) p.HasWorldPos = false;
        }
    }
}
