using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using ModKit;
using UnityEngine;
using UnityEngine.UI;

namespace MagicSurvivalMenuArt
{
    public class MenuArtMod : IKitMod
    {
        public ModDef Define() => new ModDef
        {
            Id = "menu_art",
            Name = "Меню 1.00",
            Version = typeof(MenuArtMod).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion,
            Description = "Фон главного меню из анонса Magic Survival 1.00: колдунья и плывущие облака. С исходной картинки убраны значок настроек, нижние кнопки и корейская надпись.",
            OnEnable = ctx => _mod = new MenuArt(ctx),
            OnDisable = ctx => { _mod?.Stop(); _mod = null; },
        };

        private MenuArt _mod;
    }

    internal sealed class MenuArt
    {
        private const double FrameSec = 0.2;
        private const double ArtAspect = 1280.0 / 720.0;
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private static readonly string[] TitleNames = { "Img_TitleBack", "Img_TitleFront", "Img_TitleName" };

        private readonly ModContext _ctx;
        private readonly byte[][] _frames;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly List<Image> _titles = new List<Image>();
        private readonly List<bool> _titleWas = new List<bool>();

        private GameObject _overlay;
        private Image _image;
        private RawImage _raw;
        private int _frame;
        private double _nextFrame;
        private double _nextScan;

        public MenuArt(ModContext ctx)
        {
            _ctx = ctx;
            _frames = new[] { ctx.ReadAsset("assets/0.png"), ctx.ReadAsset("assets/1.png"), ctx.ReadAsset("assets/2.png") };
            if (_frames[0] == null || _frames[1] == null || _frames[2] == null)
            {
                ctx.Warn("меню 1.00: в архиве нет assets/0.png…2.png");
                return;
            }
            ctx.OnCleanup(Stop);
            try { ctx.After("A_MainMenuInformation", "Update", 0, (s, a) => ctx.Guard("меню 1.00: кадр", Step)); }
            catch (Exception e) { ctx.Warn("меню 1.00: хук главного меню недоступен: " + e.Message); return; }
            ctx.Log($"меню 1.00 включено: {_frames.Length} кадра по {FrameSec * 1000:0} мс");
        }

        public void Stop()
        {
            try { if (_overlay != null) UiKit.SafeDestroy(_overlay); } catch { }
            _overlay = null;
            _image = null;
            _raw = null;
            for (int i = 0; i < _titles.Count; i++)
                try { if (_titles[i] != null) _titles[i].enabled = _titleWas[i]; } catch { }
            _titles.Clear();
            _titleWas.Clear();
        }

        private void Step()
        {
            if (!Alive(_overlay) || _image == null)
            {
                try { if (_overlay != null) UiKit.SafeDestroy(_overlay); } catch { }
                _overlay = null;
                _image = null;
                _raw = null;
                Create();
            }
            if (_overlay == null) return;

            for (int i = 0; i < _titles.Count; i++)
                try { if (_titles[i] != null && _titles[i].enabled) _titles[i].enabled = false; } catch { }

            double now = _clock.Elapsed.TotalSeconds;
            if (now < _nextFrame) return;
            _nextFrame = now + FrameSec;
            _frame = (_frame + 1) % _frames.Length;
            UiKit.SetImageOverlay(_image, _frames[_frame]);
            if (_raw == null) AttachRaw();
        }

        private void Create()
        {
            double now = _clock.Elapsed.TotalSeconds;
            if (now < _nextScan) return;
            _nextScan = now + 0.5;

            var back = Find("Img_BackGround");
            if (back == null) return;
            var parent = back.transform;

            var node = UiKit.Node("MenuArt100", parent);
            _overlay = node.go;
            UiKit.Stretch(node.rt);
            _image = UiKit.AddImage(_overlay, 1, 1, 1, 1);
            _image.raycastTarget = false;

            HideTitles();
            _frame = 0;
            UiKit.SetImageOverlay(_image, _frames[0]);
            AttachRaw();
            _nextFrame = now + FrameSec;
            _ctx.Log($"меню 1.00: фон над «{back.name}», заголовков {_titles.Count}");
        }

        private void HideTitles()
        {
            _titles.Clear();
            _titleWas.Clear();
            foreach (var name in TitleNames)
            {
                var go = Find(name);
                var img = go == null ? null : go.GetComponent<Image>();
                if (img == null) continue;
                _titles.Add(img);
                _titleWas.Add(img.enabled);
                img.enabled = false;
            }
        }

        private void AttachRaw()
        {
            if (_image == null || _raw != null) return;
            var t = _image.transform;
            for (int i = 0; i < t.childCount; i++)
            {
                var c = t.GetChild(i);
                if (c.name != "ModKitIcon") continue;
                _raw = c.GetComponent<RawImage>();
                break;
            }
            if (_raw == null) return;

            double screen = Screen.height / (double)Screen.width;
            float uw = 1f, vh = 1f;
            if (screen > ArtAspect) uw = (float)(ArtAspect / screen);
            else vh = (float)(screen / ArtAspect);
            try { _raw.uvRect = new Rect((1f - uw) / 2f, (1f - vh) / 2f, uw, vh); }
            catch (Exception e) { _ctx.Warn("меню 1.00: обрезка кадра не задана: " + e.Message); }
        }

        private static GameObject Find(string name)
        {
            foreach (var go in AllObjects())
            {
                if (go == null) continue;
                string n;
                try { n = go.name; } catch { continue; }
                if (n == name) return go;
            }
            return null;
        }

        private static MethodInfo _findAll, _il2cppOf;
        private static object _goType;
        private static bool _findTried, _findBroken;

        private static List<GameObject> AllObjects()
        {
            var res = new List<GameObject>();
            if (!_findTried)
            {
                _findTried = true;
                var asms = AppDomain.CurrentDomain.GetAssemblies();
                Type il2 = null, unityObject = null;
                foreach (var a in asms)
                {
                    try { il2 ??= a.GetType("Il2CppInterop.Runtime.Il2CppType"); } catch { }
                    try { unityObject ??= a.GetType("UnityEngine.Object"); } catch { }
                }
                _il2cppOf = il2?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "Of" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                _findAll = unityObject?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "FindObjectsOfType" && !m.IsGenericMethod && m.GetParameters().Length == 1);
                if (_il2cppOf == null || _findAll == null) { _findBroken = true; return res; }
                _goType = _il2cppOf.MakeGenericMethod(typeof(GameObject)).Invoke(null, null);
            }
            if (_findBroken) return res;
            try
            {
                var arr = _findAll.Invoke(null, new[] { _goType });
                var at = arr.GetType();
                int n = Convert.ToInt32(at.GetProperty("Length")?.GetValue(arr) ?? 0);
                var item = at.GetMethod("get_Item", new[] { typeof(int) });
                for (int i = 0; i < n; i++)
                {
                    var o = item.Invoke(arr, new object[] { i });
                    if (o == null) continue;
                    var go = o.GetType().GetMethod("TryCast", BindingFlags.Public | BindingFlags.Instance)?.MakeGenericMethod(typeof(GameObject))?.Invoke(o, null) as GameObject;
                    if (go == null)
                    {
                        var ptr = o.GetType().GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance)?.GetValue(o);
                        var ctor = ptr is IntPtr p && p != IntPtr.Zero ? typeof(GameObject).GetConstructor(new[] { typeof(IntPtr) }) : null;
                        if (ctor != null) go = ctor.Invoke(new object[] { ptr }) as GameObject;
                    }
                    if (go != null) res.Add(go);
                }
            }
            catch { _findBroken = true; }
            return res;
        }

        private static bool Alive(GameObject go)
        {
            try { return go != null; } catch { return false; }
        }
    }
}
