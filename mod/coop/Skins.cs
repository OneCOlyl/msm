using System;
using System.Reflection;
using System.Text.Json;
using ModKit;
using UnityEngine;

namespace MagicSurvivalCoop
{
    // Союзник — двойник нашего героя: библиотека каждый тик ставит в его спрайт кадр нашего героя.
    // Друг присылает номер своего вида (AUnit<N>), и здесь мы после библиотеки возвращаем кадр его вида.
    internal sealed partial class Session
    {
        private const string SkinPrefix = "AUnit";
        private int _mySkin = -1, _skinSent = -1, _skinLeft, _skinRescans = 3;
        private double _skinScanAt = -100;
        private readonly System.Collections.Generic.Dictionary<string, double> _skinMissAt = new System.Collections.Generic.Dictionary<string, double>();

        private static int SkinOfName(string name)
        {
            if (string.IsNullOrEmpty(name) || !name.StartsWith(SkinPrefix, StringComparison.Ordinal)) return -1;
            int i = SkinPrefix.Length, v = 0;
            while (i < name.Length && name[i] >= '0' && name[i] <= '9')
            {
                v = v * 10 + (name[i] - '0');
                if (v > 100000) return -1;
                i++;
            }
            return i > SkinPrefix.Length && i < name.Length && name[i] == 'M' ? v : -1;
        }

        private static int FrameOfName(string name)
        {
            int i = name?.IndexOf("Motion", StringComparison.Ordinal) ?? -1;
            if (i < 0) return 0;
            i += 6;
            int v = 0;
            while (i < name.Length && name[i] >= '0' && name[i] <= '9')
            {
                v = v * 10 + (name[i] - '0');
                if (v > 1000) return 0;
                i++;
            }
            return v;
        }

        private string MotionName()
        {
            try { return NameOf(SpriteOf(GetMember(HeroObject(), "Motion_Unit"))); }
            catch { return null; }
        }

        // Вид берём из спрайта героя: у школ и подопытных он свой. При смерти спрайт другой — тогда последний известный.
        private int MySkin()
        {
            int skin = SkinOfName(MotionName());
            if (skin > 0) { _mySkin = skin; return skin; }
            if (_mySkin > 0) return _mySkin;   // Спрайт сменился на смерть/эффект — вид уже известен
            try
            {
                var p = GameDbType()?.GetProperty("Game_BattleClassID", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (p != null) skin = Convert.ToInt32(p.GetValue(null));
            }
            catch { skin = -1; }
            if (skin > 0) _mySkin = skin;
            return skin > 0 ? skin : 0;
        }

        private static void WriteSkin(Utf8JsonWriter w, int skin)
        {
            if (skin > 0) w.WriteNumber("sk", skin);
        }

        private void ApplyAllySkins()
        {
            if (_shutdown || _partners.Count == 0) return;
            bool need = false;
            foreach (var p in _partners.Values) if (p.Skin > 0 && p.Ghost != null && p.Ghost.Alive) { need = true; break; }
            if (!need) return;
            int frame = FrameOfName(MotionName());
            if (frame <= 0) frame = 1;
            double now = Clock.Elapsed.TotalSeconds;
            foreach (var p in _partners.Values) ApplyAllySkin(p, frame, now);
        }

        private void ApplyAllySkin(Partner p, int frame, double now)
        {
            if (p.Skin <= 0 || p.Ghost == null || !p.Ghost.Alive) return;
            if (!EnsureGhostSr(p)) return;
            string name = SkinPrefix + p.Skin + "Motion" + frame;
            var sp = FindSkinSprite(name, p.Skin, frame, now);
            if (sp == null) return;
            try { if (NameOf(SpriteOf(p.GhostSr)) != name) SetSprite(p.GhostSr, sp); }
            catch { }
        }

        private bool EnsureGhostSr(Partner p)
        {
            try
            {
                var t = p.Ghost.GetType();
                if (t != _compType) { _compType = t; _compTr = t.GetField("Tr", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); _compSr = t.GetField("Sr", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance); }
                if (_compSr == null) _compSr = t.GetField("Sr", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                p.GhostSr = _compSr?.GetValue(p.Ghost) as SpriteRenderer;
                return p.GhostSr != null;
            }
            catch { p.GhostSr = null; return false; }
        }

        private object FindSkinSprite(string name, int skin, int frame, double now)
        {
            if (_skinMissAt.TryGetValue(name, out var missAt) && now - missAt < 2) return null;
            var sp = FindSprite(name, now);
            if (sp != null) { _skinMissAt.Remove(name); return sp; }
            sp = SkinSpriteFromDb(skin, frame);
            if (sp != null) { _skinMissAt.Remove(name); return sp; }
            if (_skinRescans > 0 && now - _skinScanAt >= 2)
            {
                _skinScanAt = now;
                _skinRescans--;        // Спрайты вида могли подгрузиться позже первого поиска
                ScanSprites();
                sp = FindSprite(name, now);
                if (sp != null) { _skinMissAt.Remove(name); return sp; }
            }
            _skinMissAt[name] = now;
            return null;
        }

        private static object SkinItem(object coll, int index)
        {
            if (coll == null) return null;
            try { var m = coll.GetType().GetMethod("get_Item", new[] { typeof(int) }); if (m != null) return m.Invoke(coll, new object[] { index }); } catch { }
            if (coll is System.Collections.IList l && index >= 0 && index < l.Count) return l[index];
            if (coll is System.Collections.IEnumerable e)
            {
                int i = 0;
                foreach (var x in e) if (i++ == index) return x;
            }
            return null;
        }

        private static object SkinValue(object dict, int key)
        {
            if (dict == null) return null;
            try
            {
                var m = dict.GetType().GetMethod("get_Item", new[] { typeof(int) });
                if (m != null) return m.Invoke(dict, new object[] { key });
            }
            catch { }
            if (dict is System.Collections.IDictionary d && d.Contains(key)) return d[key];
            if (dict is System.Collections.IEnumerable e)
                foreach (var kv in e)
                    if (Convert.ToInt32(GetMember(kv, "Key")) == key) return GetMember(kv, "Value");
            return null;
        }

        private static bool _skinDbTried;
        private static FieldInfo _skinDbField;
        private static PropertyInfo _skinDbProp;
        private Type GameDbType()
        {
            try
            {
                var asm = HeroObject()?.GetType().Assembly;
                var t = asm?.GetType("Il2Cpp.Z_Database") ?? asm?.GetType("Z_Database");
                if (t != null) return t;
            }
            catch { }
            try { return Hooks.FindType("Z_Database"); } catch { return null; }
        }

        private object SkinSpriteFromDb(int skin, int frame)
        {
            if (!_skinDbTried)
            {
                _skinDbTried = true;
                try
                {
                    var t = GameDbType();
                    for (var tt = t; tt != null && _skinDbProp == null && _skinDbField == null; tt = tt.BaseType)
                    {
                        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;
                        _skinDbProp = tt.GetProperty("Dictionary_UnitAllyMotion", flags);
                        _skinDbField = tt.GetField("Dictionary_UnitAllyMotion", flags);
                    }
                }
                catch { _skinDbProp = null; _skinDbField = null; }
            }
            if (_skinDbProp == null && _skinDbField == null) return null;
            try
            {
                var list = _skinDbProp != null ? _skinDbProp.GetValue(null) : _skinDbField?.GetValue(null);
                var sp = SkinValue(SkinItem(list, skin), frame);
                if (sp == null || NameOf(sp) != SkinPrefix + skin + "Motion" + frame) return null;
                if (RawSprites) { var p = PtrOf(sp); if (p != IntPtr.Zero) { PinOnce(p); return p; } }
                return sp;
            }
            catch { return null; }
        }
    }
}
