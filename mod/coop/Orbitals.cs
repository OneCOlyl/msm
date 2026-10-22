using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using MagicSurvivalCoop.Net;
using ModKit;
using UnityEngine;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Щит, Спутники и Духи друга — заклинания без снарядов (docs/ORBITALS.md). Друг 4 раза в секунду сообщает, сколько у него
    /// спутников (круг A и B) и под каким углом стоит первый, сколько духов, включён ли щит — и имена их картинок. У нас — копии
    /// наших собственных объектов-шаблонов героя (Instantiate), расставленные вокруг союзника-друга.
    /// Картинка шаблона пуста, пока у нас самих нет этого заклинания (Щит: спрайт ставит корутина) — поэтому картинку копии
    /// ставим сами по имени от друга: берём её из своих объектов героя, а если там нет — один раз ищем среди картинок игры.
    /// </summary>
    internal sealed partial class Session
    {
        private const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const double SatRadius = 2.25, SpiritRadius = 1.1;

        /// <summary>Что у друга включено (из его состояния).</summary>
        internal sealed class OrbState
        {
            public int SatA, SatB, Spirits;
            public bool Shield;
            public string ImgShield, ImgSatA, ImgSatB, ImgSpirit;   // имена картинок у друга
            public double AngA, AngB, AngAt = -1, SpeedA, SpeedB;     // угол первого спутника (рад), когда получен, скорость (рад/с)
        }

        internal sealed class OrbClone
        {
            public object Go; public Transform Tr; public object Sr;
            public string Applied, Missed;
            public bool? Active;
        }

        /// <summary>Наши копии вокруг союзника одного друга.</summary>
        internal sealed class OrbVisuals
        {
            public readonly List<OrbClone> SatA = new List<OrbClone>(), SatB = new List<OrbClone>(), Spirits = new List<OrbClone>();
            public OrbClone Shield;
            public bool Logged;
            public IEnumerable<OrbClone> All => SatA.Concat(SatB).Concat(Spirits).Concat(Shield != null ? new[] { Shield } : Array.Empty<OrbClone>());
        }

        // ------------------------------------------------------------ отправка: своё состояние

        private double _orbAt = -1;
        private int _myOrbSatA, _myOrbSatB, _myOrbSpirits;
        private bool _myOrbShield;
        private double _myOrbAngA, _myOrbAngB;
        private string _myImgShield, _myImgSatA, _myImgSatB, _myImgSpirit;
        private int _orbFails;
        private bool _myOrbLogged;

        /// <summary>Своё состояние «орбит» — не чаще 4 раз в секунду (чтение массивов героя через рефлексию).</summary>
        private void RefreshMyOrbits(double hx, double hy)
        {
            double now = Clock.Elapsed.TotalSeconds;
            if (now - _orbAt < 0.25) return;
            _orbAt = now;
            var hero = HeroObject();
            if (hero == null) { _myOrbSatA = _myOrbSatB = _myOrbSpirits = 0; _myOrbShield = false; return; }
            try
            {
                _myOrbSatA = CountTrue(GetMember(hero, "Array_IsSatelliteA_P"), 0);
                _myOrbSatB = CountTrue(GetMember(hero, "Array_IsSatelliteB_P"), 0);
                _myOrbSpirits = CountTrue(GetMember(hero, "List_IsSpirit_P"), 1);
                var sh = GetMember(hero, "Obj_Shield");
                _myOrbShield = sh != null && Convert.ToBoolean(GetMember(sh, "activeSelf") ?? false);
                _myOrbAngA = _myOrbSatA > 0 ? FirstAngle(GetMember(hero, "Array_Transform_ObjSatellitesA"), hx, hy) : 0;
                _myOrbAngB = _myOrbSatB > 0 ? FirstAngle(GetMember(hero, "Array_Transform_ObjSatellitesB"), hx, hy) : 0;
                _myImgShield = _myOrbShield ? ImageOf(sh) : null;
                _myImgSatA = _myOrbSatA > 0 ? ImageOf(ItemAt(GetMember(hero, "Array_Obj_UnitSatellitesA"), 0)) : null;
                _myImgSatB = _myOrbSatB > 0 ? ImageOf(ItemAt(GetMember(hero, "Array_Obj_UnitSatellitesB"), 0)) : null;
                _myImgSpirit = _myOrbSpirits > 0 ? ImageOf(ItemAt(GetMember(hero, "List_ObjSpirit"), 1)) : null;
                if (!_myOrbLogged && (_myOrbShield || _myOrbSatA + _myOrbSatB + _myOrbSpirits > 0))
                {
                    _myOrbLogged = true;
                    _ctx.Log($"орбиты (свои): щит {(_myOrbShield ? "есть" : "нет")} «{_myImgShield}», спутники {_myOrbSatA}+{_myOrbSatB} «{_myImgSatA}», духи {_myOrbSpirits} «{_myImgSpirit}»");
                }
            }
            catch (Exception e) { if (++_orbFails <= 3) _ctx.Log("орбиты: " + Inner(e).Message); }
        }

        private void WriteMyOrbits(Utf8JsonWriter w)
        {
            if (_myOrbSatA + _myOrbSatB + _myOrbSpirits == 0 && !_myOrbShield) return;
            w.WriteStartObject("orb");
            w.WriteNumber("sa", _myOrbSatA); w.WriteNumber("sb", _myOrbSatB); w.WriteNumber("sp", _myOrbSpirits); w.WriteNumber("sh", _myOrbShield ? 1 : 0);
            w.WriteNumber("aa", Math.Round(_myOrbAngA, 3)); w.WriteNumber("ab", Math.Round(_myOrbAngB, 3));
            if (_myImgShield != null) w.WriteString("ish", _myImgShield);
            if (_myImgSatA != null) w.WriteString("isa", _myImgSatA);
            if (_myImgSatB != null) w.WriteString("isb", _myImgSatB);
            if (_myImgSpirit != null) w.WriteString("isp", _myImgSpirit);
            w.WriteEndObject();
        }

        private static Exception Inner(Exception e) => (e as TargetInvocationException)?.InnerException ?? e;

        private static object GetMember(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var p = t.GetProperty(name, AnyInst);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(o);
            return t.GetField(name, AnyInst)?.GetValue(o);
        }
        private static int Count(object coll) => coll == null ? 0 : Convert.ToInt32(GetMember(coll, "Length") ?? GetMember(coll, "Count") ?? (coll is ICollection c ? c.Count : 0));
        private static object ItemAt(object coll, int i)
        {
            if (coll == null || i >= Count(coll)) return null;
            var m = coll.GetType().GetMethod("get_Item", new[] { typeof(int) });
            if (m != null) return m.Invoke(coll, new object[] { i });
            return coll is IList l ? l[i] : null;
        }
        private static int CountTrue(object coll, int from)
        {
            int n = Count(coll), k = 0;
            for (int i = from; i < n; i++) if (ItemAt(coll, i) is bool b && b) k++;
            return k;
        }
        /// <summary>Угол первого объекта массива относительно героя (рад); 0 — нет.</summary>
        private static double FirstAngle(object coll, double hx, double hy)
        {
            if (!(ItemAt(coll, 0) is Transform tr)) return 0;
            var p = tr.position;
            return Math.Atan2(p.y - hy, p.x - hx);
        }

        // ------------------------------------------------------------ картинки

        private readonly Dictionary<Type, MethodInfo> _rendererGetters = new Dictionary<Type, MethodInfo>();
        private readonly Dictionary<string, object> _sprites = new Dictionary<string, object>();
        private bool _spriteScanDone;
        private double _spriteHarvestAt = -1;

        /// <summary>Первый SpriteRenderer объекта или его детей (и у выключенных).</summary>
        private object RendererOf(object go)
        {
            if (go == null) return null;
            var t = go.GetType();
            if (!_rendererGetters.TryGetValue(t, out var m))
            {
                m = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(x => x.Name == "GetComponentInChildren" && x.IsGenericMethodDefinition && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(bool))
                    ?.MakeGenericMethod(typeof(SpriteRenderer));
                _rendererGetters[t] = m;
            }
            return m?.Invoke(go, new object[] { true });
        }
        private static object SpriteOf(object sr) => GetMember(sr, "sprite");
        private static string NameOf(object o) { var n = GetMember(o, "name") as string; return string.IsNullOrEmpty(n) ? null : n; }
        private string ImageOf(object go) => NameOf(SpriteOf(RendererOf(go)));

        private void Remember(object sprite)
        {
            var n = NameOf(sprite);
            if (n != null && !_sprites.ContainsKey(n)) _sprites[n] = sprite;
        }

        /// <summary>Картинка по имени: из своих объектов героя, иначе — один раз за бой поиск среди всех картинок игры.</summary>
        private object FindSprite(string name, double now)
        {
            if (_sprites.TryGetValue(name, out var s)) return s;
            if (now - _spriteHarvestAt > 2)
            {
                _spriteHarvestAt = now;
                var hero = HeroObject();
                if (hero != null)
                {
                    Remember(SpriteOf(GetMember(hero, "SpriteRenderer_Shield")));
                    Remember(SpriteOf(RendererOf(GetMember(hero, "Obj_Shield"))));
                    Remember(SpriteOf(RendererOf(ItemAt(GetMember(hero, "Array_Obj_UnitSatellitesA"), 0))));
                    Remember(SpriteOf(RendererOf(ItemAt(GetMember(hero, "Array_Obj_UnitSatellitesB"), 0))));
                    Remember(SpriteOf(RendererOf(ItemAt(GetMember(hero, "List_ObjSpirit"), 1))));
                }
                if (_sprites.TryGetValue(name, out s)) return s;
            }
            if (!_spriteScanDone)
            {
                _spriteScanDone = true;
                ScanSprites();
                if (_sprites.TryGetValue(name, out s)) return s;
            }
            return null;
        }

        private void ScanSprites()
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var spriteType = typeof(SpriteRenderer).GetProperty("sprite")?.PropertyType;
                var res = typeof(Transform).Assembly.GetType("UnityEngine.Resources");
                var m = res?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(x => x.Name == "FindObjectsOfTypeAll" && x.IsGenericMethodDefinition && x.GetParameters().Length == 0);
                if (spriteType == null || m == null) { _ctx.Log("орбиты: поиск картинок недоступен"); return; }
                _ctx.Mark("орбиты: поиск картинок");
                var all = m.MakeGenericMethod(spriteType).Invoke(null, null);
                int n = Count(all), kept = 0;
                var get = all.GetType().GetMethod("get_Item", new[] { typeof(int) });
                var list = all as IList;
                for (int i = 0; i < n; i++)
                {
                    var sp = get != null ? get.Invoke(all, new object[] { i }) : list?[i];
                    var nm = NameOf(sp);
                    if (nm == null || _sprites.ContainsKey(nm)) continue;
                    if (nm.IndexOf("Shield", StringComparison.OrdinalIgnoreCase) < 0 && nm.IndexOf("Satellite", StringComparison.OrdinalIgnoreCase) < 0
                        && nm.IndexOf("Spirit", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    _sprites[nm] = sp; kept++;
                }
                _ctx.Log($"орбиты: поиск картинок по игре — всего {n}, подходящих {kept} ({string.Join(", ", _sprites.Keys.Where(k => k.IndexOf("Shield", StringComparison.OrdinalIgnoreCase) >= 0).Take(8))}) за {sw.ElapsedMilliseconds} мс");
            }
            catch (Exception e) { _ctx.Log("орбиты: поиск картинок не удался: " + Inner(e).Message); }
        }

        /// <summary>Поставить копии картинку, как у друга (если надо).</summary>
        private void ApplyImage(OrbClone c, string want, double now, string what)
        {
            if (want == null || want == c.Applied || want == c.Missed) return;
            c.Sr ??= RendererOf(c.Go);
            if (c.Sr == null) { c.Missed = want; _ctx.Log($"орбиты: у копии ({what}) нет картинки-рендера"); return; }
            if (NameOf(SpriteOf(c.Sr)) == want) { c.Applied = want; return; }
            var sp = FindSprite(want, now);
            if (sp == null) { c.Missed = want; _ctx.Log($"орбиты: картинка «{want}» ({what}) не найдена"); return; }
            c.Sr.GetType().GetProperty("sprite")?.SetValue(c.Sr, sp);
            c.Applied = want;
        }

        // ------------------------------------------------------------ приём: состояние друга

        private static void ReadOrbits(Partner p, JsonElement s, double now)
        {
            var o = Protocol.Obj(s, "orb");
            var st = p.Orb ??= new OrbState();
            if (o.ValueKind != JsonValueKind.Object) { st.SatA = st.SatB = st.Spirits = 0; st.Shield = false; return; }
            st.SatA = Clamp((int)Protocol.Num(o, "sa"), 0, 14);
            st.SatB = Clamp((int)Protocol.Num(o, "sb"), 0, 14);
            st.Spirits = Clamp((int)Protocol.Num(o, "sp"), 0, 14);
            st.Shield = Protocol.Bool(o, "sh");
            st.ImgShield = Protocol.Str(o, "ish") ?? st.ImgShield;
            st.ImgSatA = Protocol.Str(o, "isa") ?? st.ImgSatA;
            st.ImgSatB = Protocol.Str(o, "isb") ?? st.ImgSatB;
            st.ImgSpirit = Protocol.Str(o, "isp") ?? st.ImgSpirit;
            double aa = Protocol.Num(o, "aa"), ab = Protocol.Num(o, "ab");
            if (st.AngAt > 0)
            {
                double dt = now - st.AngAt;
                if (dt > 0.05 && dt < 2)
                {
                    st.SpeedA = st.SpeedA * 0.5 + Wrap(aa - st.AngA) / dt * 0.5;
                    st.SpeedB = st.SpeedB * 0.5 + Wrap(ab - st.AngB) / dt * 0.5;
                }
            }
            st.AngA = aa; st.AngB = ab; st.AngAt = now;
        }
        private static int Clamp(int v, int a, int b) => v < a ? a : v > b ? b : v;
        private static double Wrap(double a) { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; }

        // ------------------------------------------------------------ копии вокруг союзника

        private MethodInfo _instantiate, _destroy;

        private object CloneOf(object template)
        {
            if (template == null) return null;
            if (_instantiate == null)
            {
                var objType = typeof(Transform).BaseType?.BaseType;                         // UnityEngine.Object
                _instantiate = objType?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "Instantiate" && !m.IsGenericMethod && m.GetParameters().Length == 1);
                _destroy = objType?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "Destroy" && m.GetParameters().Length == 1);
            }
            if (_instantiate == null) return null;
            _ctx.Mark("орбиты: копия шаблона");
            var raw = _instantiate.Invoke(null, new[] { template });
            if (raw == null) return null;
            if (raw is GameObject) return raw;
            // Instantiate отдаёт обёртку UnityEngine.Object — у неё нет методов GameObject; обёртка GameObject по адресу
            if (raw.GetType().GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance)?.GetValue(raw) is IntPtr ptr && ptr != IntPtr.Zero)
                return Activator.CreateInstance(typeof(GameObject), ptr);
            return null;
        }

        private static void SetActive(object go, bool on)
        {
            var m = go.GetType().GetMethod("SetActive", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
            m?.Invoke(go, new object[] { on });
        }

        private OrbClone MakeClone(object template)
        {
            var go = CloneOf(template);
            if (go == null) return null;
            SetActive(go, false);
            var tr = GetMember(go, "transform") as Transform;
            return tr == null ? null : new OrbClone { Go = go, Tr = tr, Active = false };
        }

        private void DestroyOrbVisuals(Partner p)
        {
            var v = p.OrbVis;
            if (v == null) return;
            p.OrbVis = null;
            foreach (var c in v.All)
                if (c.Go != null) { try { _destroy?.Invoke(null, new[] { c.Go }); } catch { } }
        }

        /// <summary>Держать столько копий, сколько у друга, и включать/выключать их без лишних вызовов.</summary>
        private void EnsureCount(List<OrbClone> list, int want, Func<object> template, string img, double now, string what)
        {
            while (list.Count < want)
            {
                var c = MakeClone(template());
                if (c == null) return;
                list.Add(c);
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (i < want) ApplyImage(list[i], img, now, what);
                Show(list[i], i < want);
            }
        }
        private static void Show(OrbClone c, bool on)
        {
            if (c?.Go == null || c.Active == on) return;
            SetActive(c.Go, on);
            c.Active = on;
        }

        /// <summary>Каждый кадр: копии вокруг союзника-друга (по сглаженной позиции союзника).</summary>
        private void PlaceOrbits(Partner p, double now)
        {
            var st = p.Orb;
            if (st == null || p.GhostTr == null || _ctx.Setting("orbitals") <= 0) { if (p.OrbVis != null) HideAll(p.OrbVis); return; }
            var hero = HeroObject();
            if (hero == null) return;
            var v = p.OrbVis ??= new OrbVisuals();
            try
            {
                EnsureCount(v.SatA, st.SatA, () => ItemAt(GetMember(hero, "Array_Obj_UnitSatellitesA"), 0), st.ImgSatA, now, "спутник");
                EnsureCount(v.SatB, st.SatB, () => ItemAt(GetMember(hero, "Array_Obj_UnitSatellitesB"), 0), st.ImgSatB, now, "спутник B");
                EnsureCount(v.Spirits, st.Spirits, () => ItemAt(GetMember(hero, "List_ObjSpirit"), 1), st.ImgSpirit, now, "дух");
                if (st.Shield && v.Shield == null) v.Shield = MakeClone(GetMember(hero, "Obj_Shield"));
                if (v.Shield != null)
                {
                    if (st.Shield) ApplyImage(v.Shield, st.ImgShield, now, "щит");
                    Show(v.Shield, st.Shield);
                }
                if (!v.Logged && v.All.Any(c => c.Active == true))
                {
                    v.Logged = true;
                    _ctx.Log($"орбиты «{p.Name}»: спутники {st.SatA}+{st.SatB} «{st.ImgSatA}», духи {st.Spirits} «{st.ImgSpirit}», щит {(st.Shield ? "есть" : "нет")} «{st.ImgShield}»; копий {v.All.Count()}");
                }
            }
            catch (Exception e) { if (++_orbFails <= 3) _ctx.Log("орбиты, копии: " + Inner(e).Message); return; }

            var g = p.GhostTr.position;
            double age = st.AngAt > 0 ? Math.Min(1.0, now - st.AngAt) : 0;
            Ring(v.SatA, st.SatA, g, SatRadius, st.AngA + st.SpeedA * age);
            Ring(v.SatB, st.SatB, g, SatRadius, st.AngB + st.SpeedB * age);
            Ring(v.Spirits, st.Spirits, g, SpiritRadius, now * 0.8);
            if (v.Shield != null && st.Shield) v.Shield.Tr.position = new Vector3(g.x, g.y + 0.03f, g.z);
        }

        private static void Ring(List<OrbClone> list, int n, Vector3 c, double r, double a0)
        {
            for (int i = 0; i < n && i < list.Count; i++)
            {
                double a = a0 + 2 * Math.PI * i / n;
                list[i].Tr.position = new Vector3((float)(c.x + Math.Cos(a) * r), (float)(c.y + Math.Sin(a) * r), c.z);
            }
        }

        private static void HideAll(OrbVisuals v)
        {
            foreach (var c in v.All) Show(c, false);
        }

        /// <summary>Конец боя: картинки и объекты прошлого боя больше не годятся.</summary>
        private void ResetOrbits()
        {
            _sprites.Clear(); _spriteScanDone = false; _spriteHarvestAt = -1; _myOrbLogged = false;
        }
    }
}
