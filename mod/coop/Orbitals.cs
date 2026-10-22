using System;
using System.Collections;
using System.Collections.Generic;
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
    /// спутников (круг A и B) и под каким углом стоит первый, сколько духов и включён ли щит. У нас — копии наших собственных
    /// объектов-шаблонов героя (Instantiate), расставленные вокруг союзника-друга: спутники по кругу 2,25, духи рядом, щит на нём.
    /// Разведка 0.5.1 на телефоне: копии создаются, включаются, игра их не двигает.
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
            public double AngA, AngB, AngAt = -1, SpeedA, SpeedB;     // угол первого спутника (рад), когда получен, скорость (рад/с)
        }

        /// <summary>Наши копии вокруг союзника одного друга.</summary>
        internal sealed class OrbVisuals
        {
            public readonly List<(object go, Transform tr)> SatA = new List<(object, Transform)>(), SatB = new List<(object, Transform)>(), Spirits = new List<(object, Transform)>();
            public (object go, Transform tr) Shield;
            public readonly Dictionary<object, bool> Active = new Dictionary<object, bool>();
        }

        // ------------------------------------------------------------ отправка: своё состояние

        private double _orbAt = -1;
        private int _myOrbSatA, _myOrbSatB, _myOrbSpirits;
        private bool _myOrbShield;
        private double _myOrbAngA, _myOrbAngB;

        /// <summary>Своё состояние «орбит» — не чаще 4 раз в секунду (чтение массивов героя через рефлексию).</summary>
        private void RefreshMyOrbits(double hx, double hy)
        {
            double now = Clock.Elapsed.TotalSeconds;
            if (now - _orbAt < 0.25) return;
            _orbAt = now;
            var hero = RawHero();
            if (hero == null) { _myOrbSatA = _myOrbSatB = _myOrbSpirits = 0; _myOrbShield = false; return; }
            try
            {
                _myOrbSatA = CountTrue(GetMember(hero, "Array_IsSatelliteA_P"), 0);
                _myOrbSatB = CountTrue(GetMember(hero, "Array_IsSatelliteB_P"), 0);
                _myOrbSpirits = CountTrue(GetMember(hero, "List_IsSpirit_P"), 1);
                _myOrbShield = GetMember(hero, "Obj_Shield") is object sh && Convert.ToBoolean(GetMember(sh, "activeSelf") ?? false);
                _myOrbAngA = FirstAngle(GetMember(hero, "Array_Transform_ObjSatellitesA"), hx, hy);
                _myOrbAngB = FirstAngle(GetMember(hero, "Array_Transform_ObjSatellitesB"), hx, hy);
            }
            catch (Exception e) { if (++_orbFails <= 3) _ctx.Log("орбиты: " + e.Message); }
        }
        private int _orbFails;

        private void WriteMyOrbits(System.Text.Json.Utf8JsonWriter w)
        {
            if (_myOrbSatA + _myOrbSatB + _myOrbSpirits == 0 && !_myOrbShield) return;
            w.WriteStartObject("orb");
            w.WriteNumber("sa", _myOrbSatA); w.WriteNumber("sb", _myOrbSatB); w.WriteNumber("sp", _myOrbSpirits); w.WriteNumber("sh", _myOrbShield ? 1 : 0);
            w.WriteNumber("aa", Math.Round(_myOrbAngA, 3)); w.WriteNumber("ab", Math.Round(_myOrbAngB, 3));
            w.WriteEndObject();
        }

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
            if (coll == null || Count(coll) == 0) return 0;
            if (!(ItemAt(coll, 0) is Transform tr)) return 0;
            var p = tr.position;
            return Math.Atan2(p.y - hy, p.x - hx);
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

        private (object go, Transform tr) MakeClone(object template)
        {
            var go = CloneOf(template);
            if (go == null) return (null, null);
            SetActive(go, false);
            return (go, GetMember(go, "transform") as Transform);
        }

        private void DestroyOrbVisuals(Partner p)
        {
            var v = p.OrbVis;
            if (v == null) return;
            p.OrbVis = null;
            foreach (var c in v.SatA.Concat(v.SatB).Concat(v.Spirits).Append(v.Shield))
                if (c.go != null) { try { _destroy?.Invoke(null, new[] { c.go }); } catch { } }
        }

        /// <summary>Держать столько копий, сколько у друга, и включать/выключать их без лишних вызовов.</summary>
        private void EnsureCount(OrbVisuals v, List<(object go, Transform tr)> list, int want, Func<object> template)
        {
            while (list.Count < want)
            {
                var c = MakeClone(template());
                if (c.go == null || c.tr == null) return;
                list.Add(c);
            }
            for (int i = 0; i < list.Count; i++) Show(v, list[i].go, i < want);
        }
        private static void Show(OrbVisuals v, object go, bool on)
        {
            if (go == null) return;
            if (v.Active.TryGetValue(go, out var cur) && cur == on) return;
            SetActive(go, on);
            v.Active[go] = on;
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
                EnsureCount(v, v.SatA, st.SatA, () => ItemAt(GetMember(hero, "Array_Obj_UnitSatellitesA"), 0));
                EnsureCount(v, v.SatB, st.SatB, () => ItemAt(GetMember(hero, "Array_Obj_UnitSatellitesB"), 0));
                EnsureCount(v, v.Spirits, st.Spirits, () => ItemAt(GetMember(hero, "List_ObjSpirit"), 1));
                if (st.Shield && v.Shield.go == null) v.Shield = MakeClone(GetMember(hero, "Obj_Shield"));
                if (v.Shield.go != null) Show(v, v.Shield.go, st.Shield);
            }
            catch (Exception e) { if (++_orbFails <= 3) _ctx.Log("орбиты, копии: " + ((e as TargetInvocationException)?.InnerException ?? e).Message); return; }

            var g = p.GhostTr.position;
            double age = st.AngAt > 0 ? Math.Min(1.0, now - st.AngAt) : 0;
            Ring(v.SatA, st.SatA, g, SatRadius, st.AngA + st.SpeedA * age);
            Ring(v.SatB, st.SatB, g, SatRadius, st.AngB + st.SpeedB * age);
            Ring(v.Spirits, st.Spirits, g, SpiritRadius, now * 0.8);
            if (v.Shield.tr != null && st.Shield) v.Shield.tr.position = new Vector3(g.x, g.y + 0.03f, g.z);
        }

        private static void Ring(List<(object go, Transform tr)> list, int n, Vector3 c, double r, double a0)
        {
            for (int i = 0; i < n && i < list.Count; i++)
            {
                double a = a0 + 2 * Math.PI * i / n;
                list[i].tr.position = new Vector3((float)(c.x + Math.Cos(a) * r), (float)(c.y + Math.Sin(a) * r), c.z);
            }
        }

        private static void HideAll(OrbVisuals v)
        {
            foreach (var c in v.SatA.Concat(v.SatB).Concat(v.Spirits).Append(v.Shield)) Show(v, c.go, false);
        }
    }
}
