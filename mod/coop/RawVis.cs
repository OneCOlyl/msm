using System;
using System.Runtime.InteropServices;
using ModKit;
using UnityEngine;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Видимость рендеров напрямую через IL2CPP (Raw): Renderer.enabled и цвет обёртка на телефоне не читает
    /// («icall Renderer::get_enabled не разрешается»), а игра мигает Электрозоной и рисует Армагеддон именно ими.
    /// Без Unity (тесты) — обычные свойства заглушек.
    /// </summary>
    internal sealed partial class Session
    {
        private static IntPtr _rGetEnabled, _rSetEnabled, _srGetColor, _srSetColor, _bGetEnabled, _gGetColor;
        private static bool _visInit, _visFailed;

        private static bool InitVis()
        {
            if (_visInit) return !_visFailed;
            _visInit = true;
            try
            {
                const string core = "UnityEngine.CoreModule.dll";
                var r = Raw.Class(core, "UnityEngine", "Renderer");
                _rGetEnabled = Raw.Method(r, "get_enabled", 0);
                _rSetEnabled = Raw.Method(r, "set_enabled", 1);
                var sr = Raw.Class(core, "UnityEngine", "SpriteRenderer");
                _srGetColor = Raw.Method(sr, "get_color", 0);
                _srSetColor = Raw.Method(sr, "set_color", 1);
                _bGetEnabled = Raw.Method(Raw.Class(core, "UnityEngine", "Behaviour"), "get_enabled", 0);
                try { _gGetColor = Raw.Method(Raw.Class("UnityEngine.UI.dll", "UnityEngine.UI", "Graphic"), "get_color", 0); } catch { }
            }
            catch { _visFailed = true; }
            return !_visFailed;
        }

        /// <summary>enabled рендера (SpriteRenderer) или компонента (Image); null — не прочитать.</summary>
        private static bool? EnabledOf(object comp, bool behaviour = false)
        {
            if (comp == null) return null;
            if (!RawSprites) return GetMember(comp, "enabled") as bool?;
            try
            {
                if (!InitVis()) return null;
                var m = behaviour ? _bGetEnabled : _rGetEnabled;
                var p = LivePtr(comp);
                if (m == IntPtr.Zero || p == IntPtr.Zero) return null;
                var box = Raw.Invoke(m, p);
                return box == IntPtr.Zero ? (bool?)null : Raw.UnboxBool(box);
            }
            catch { return null; }
        }

        private static void SetEnabled(object sr, bool on)
        {
            if (sr == null) return;
            if (!RawSprites) { sr.GetType().GetProperty("enabled")?.SetValue(sr, on); return; }
            if (!InitVis() || _rSetEnabled == IntPtr.Zero) return;
            var p = LivePtr(sr);
            if (p != IntPtr.Zero) Raw.InvokeInt(_rSetEnabled, p, on ? 1 : 0);   // bool в первом байте int
        }

        /// <summary>Цвет (r, g, b, a) SpriteRenderer или Graphic (Image); null — не прочитать.</summary>
        private static float[] ColorOf(object comp, bool graphic = false)
        {
            if (comp == null) return null;
            if (!RawSprites) return GetMember(comp, "color") is Color c ? new[] { c.r, c.g, c.b, c.a } : null;
            try
            {
                if (!InitVis()) return null;
                var m = graphic ? _gGetColor : _srGetColor;
                var p = LivePtr(comp);
                if (m == IntPtr.Zero || p == IntPtr.Zero) return null;
                var box = Raw.Invoke(m, p);
                if (box == IntPtr.Zero) return null;
                var f = new float[4];
                Marshal.Copy(box + 2 * IntPtr.Size, f, 0, 4);                     // данные структуры — после заголовка объекта
                return f;
            }
            catch { return null; }
        }

        private static void SetColor(object sr, float[] c)
        {
            if (sr == null || c == null) return;
            if (!RawSprites) { sr.GetType().GetProperty("color")?.SetValue(sr, new Color(c[0], c[1], c[2], c[3])); return; }
            if (!InitVis() || _srSetColor == IntPtr.Zero) return;
            var p = LivePtr(sr);
            if (p == IntPtr.Zero) return;
            var mem = Marshal.AllocHGlobal(16);
            try { Marshal.Copy(c, 0, mem, 4); Raw.Invoke(_srSetColor, p, mem); }
            finally { Marshal.FreeHGlobal(mem); }
        }

        /// <summary>Для журнала: «рендер вкл, α 0.50».</summary>
        private static string VisOf(object comp, bool ui = false)
        {
            if (comp == null) return "рендера нет";
            var en = EnabledOf(comp, ui);
            var c = ColorOf(comp, ui);
            return $"рендер {(en == null ? "?" : en.Value ? "вкл" : "выкл")}, α {(c == null ? "?" : c[3].ToString("0.00"))}";
        }
    }
}
