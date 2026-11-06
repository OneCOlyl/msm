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
    // Картинка шаблона пуста, пока у нас нет этого заклинания, поэтому картинку ставим по имени от друга.
    internal sealed partial class Session
    {
        private const BindingFlags AnyInst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const double SatRadius = 2.25, SpiritRadius = 1.1;

        internal sealed class OrbState
        {
            public int SatA, SatB, Spirits;
            public bool Shield;
            public string ImgShield, ImgSatA, ImgSatB, ImgSpirit;
            public double AngA, AngB, AngAt = -1, SpeedA, SpeedB;     // Угол первого спутника (рад), когда получен, скорость (рад/с)
            public double SenderT = -1;                               // Время снятия угла по часам друга
            public bool HasSpeed;
            public double DispA, DispB, LastFrame = -1;               // Рисуемый угол плавно догоняет предсказанный
            public bool HasDisp;
            public int ShownSatA, ShownSatB;
            public double[] SpiritOff;                                // Смещения духов от героя друга парами x, y
            public double[] SpiritDisp;
            public bool Zone, ZoneOn;
            public string ImgZone; public bool ZoneEn = true; public double ZoneA = double.NaN;
            public double ShieldDx, ShieldDy = 0.03, ShieldSx = 1, ShieldSy = 1;
            public double ZoneSx = 1, ZoneSy = 1;
            public bool Mc, McOn; public double McS = 1;
            public readonly string[] McImg = new string[3]; public readonly double[] McRot = new double[3];
        }

        internal sealed class OrbClone
        {
            public object Go; public Transform Tr; public object Sr;
            public string Applied, Missed;
            public double MissedAt;
            public bool? Active;
        }

        internal sealed class OrbVisuals
        {
            public readonly List<OrbClone> SatA = new List<OrbClone>(), SatB = new List<OrbClone>(), Spirits = new List<OrbClone>();
            public OrbClone Shield, Zone, Mc;
            public readonly List<OrbClone> McKids = new List<OrbClone>();
            public bool McFailed, McLogged; public double McS = double.NaN;
            public bool ZoneScaleFailed;
            public double ZoneSx = double.NaN, ZoneSy = double.NaN;
            public bool? ZoneEnSet; public double ZoneASet = double.NaN;
            public (int, int, int, bool, bool) LastKey;
            public bool HasKey, Hidden, ShieldFailed;
            public double ShieldSx = double.NaN, ShieldSy = double.NaN;

            public void ForEach(Action<OrbClone> fn)
            {
                foreach (var c in SatA) fn(c);
                foreach (var c in SatB) fn(c);
                foreach (var c in Spirits) fn(c);
                if (Shield != null) fn(Shield);
                if (Zone != null) fn(Zone);
                if (Mc != null) fn(Mc);
            }
            public int Count => SatA.Count + SatB.Count + Spirits.Count + (Shield != null ? 1 : 0) + (Zone != null ? 1 : 0) + (Mc != null ? 1 : 0);
        }

        private double _orbAt = -1;
        private int _myOrbSatA, _myOrbSatB, _myOrbSpirits;
        private bool _myOrbShield;
        private double _myOrbAngA, _myOrbAngB;
        private string _myImgShield, _myImgSatA, _myImgSatB, _myImgSpirit;
        private double _myShieldDx, _myShieldDy = 0.03, _myShieldSx = 1, _myShieldSy = 1;
        private int _orbFails, _orbStateLogs;
        private string _myOrbState;
        private int _myOrbLogs;
        private readonly List<Transform> _mySpiritTr = new List<Transform>();
        private readonly List<double> _mySpiritOff = new List<double>();
        private bool _myZone, _myZoneOn;      // Зона вспыхивает, включение читаем при каждой отправке
        private object _myZoneObj;
        private string _myZoneImg; private bool? _myZoneEn; private float _myZoneA = float.NaN;
        private double _myZoneSx = 1, _myZoneSy = 1;
        private double _zoneLookAt;

        // Угол спутников идёт с временем «t» того же сообщения. Остальное реже: чтение массивов героя через рефлексию.
        private void RefreshMyOrbits()
        {
            double now = Clock.Elapsed.TotalSeconds;
            if (now - _orbAt >= 0.25) RefreshOrbitCounts(now);
            if (_myOrbSatA + _myOrbSatB > 0 && HeroPosition(out var hx, out var hy, out _))
            {
                var hero0 = HeroObject();
                try
                {
                    if (_myOrbSatA > 0) _myOrbAngA = FirstAngle(GetMember(hero0, "Array_Transform_ObjSatellitesA"), hx, hy);
                    if (_myOrbSatB > 0) _myOrbAngB = FirstAngle(GetMember(hero0, "Array_Transform_ObjSatellitesB"), hx, hy);
                }
                catch { }
            }
            try { _myZoneOn = _myZoneObj != null && Convert.ToBoolean(GetMember(_myZoneObj, "activeSelf") ?? false); } catch { _myZoneOn = false; }
            if (_myZoneOn && now >= _zoneLookAt)
            {
                // Игра мигает зоной рендером и меняет её кадры. Пересылаем не чаще 10 раз в секунду: это четыре обращения к игре
                _zoneLookAt = now + 0.1;
                try
                {
                    var zsr = RendererOf(_myZoneObj);
                    _myZoneImg = NameOf(SpriteOf(zsr)); _myZoneEn = EnabledOf(zsr);
                    var zc = ColorOf(zsr); _myZoneA = zc != null ? zc[3] : float.NaN;
                }
                catch { _myZoneImg = null; _myZoneEn = null; _myZoneA = float.NaN; }
            }
            try { RefreshMyCircle(); } catch { _myMc = false; }
            _mySpiritOff.Clear();
            if (_mySpiritTr.Count > 0 && HeroPosition(out var sx, out var sy, out _))
            {
                try
                {
                    foreach (var tr in _mySpiritTr) { var q = tr.position; _mySpiritOff.Add(Math.Round(q.x - sx, 2)); _mySpiritOff.Add(Math.Round(q.y - sy, 2)); }
                }
                catch { _mySpiritOff.Clear(); }
            }
        }

        private void RefreshOrbitCounts(double now)
        {
            _orbAt = now;
            var hero = HeroObject();
            if (hero == null) { _myOrbSatA = _myOrbSatB = _myOrbSpirits = 0; _myOrbShield = false; _myZone = false; _mySpiritTr.Clear(); return; }
            try
            {
                _myOrbSatA = CountTrue(GetMember(hero, "Array_IsSatelliteA_P"), 0);
                _myOrbSatB = CountTrue(GetMember(hero, "Array_IsSatelliteB_P"), 0);
                // Дух считается, если включён флагом и его объект активен
                _mySpiritTr.Clear();
                var flags = GetMember(hero, "List_IsSpirit_P");
                var gos = GetMember(hero, "List_ObjSpirit");
                var trs = GetMember(hero, "List_Transform_ObjSpirit");
                int nsp = Count(flags);
                for (int i = 1; i < nsp; i++)
                {
                    if (!(ItemAt(flags, i) is bool f && f)) continue;
                    var go = ItemAt(gos, i);
                    if (go != null && !Convert.ToBoolean(GetMember(go, "activeSelf") ?? false)) continue;
                    if (ItemAt(trs, i) is Transform tr) _mySpiritTr.Add(tr);
                }
                _myOrbSpirits = _mySpiritTr.Count;
                // Объект Электрозоны включён, пока заклинание есть. Размер зоны равен масштабу объекта
                var ez = GetMember(hero, "Obj_ElectricZone");
                _myZone = GetMember(hero, "IsElectricZone_P") is bool ezp ? ezp : ez != null && Convert.ToBoolean(GetMember(ez, "activeSelf") ?? false);
                _myZoneObj = _myZone ? ez : null;
                if (_myZone)
                {
                    try { if (GetMember(GetMember(ez, "transform"), "localScale") is Vector3 ls && Math.Abs(ls.x) > 0.01 && Math.Abs(ls.y) > 0.01) { _myZoneSx = ls.x; _myZoneSy = ls.y; } }
                    catch { }
                }
                // Щит рисует SpriteRenderer_Shield, а не Obj_Shield без картинки. Картинка меняется с зарядами
                var shSr = GetMember(hero, "SpriteRenderer_Shield");
                bool shOn = GetMember(hero, "IsShield_P") is bool isSh && isSh;
                _myImgShield = shOn ? NameOf(SpriteOf(shSr)) : null;
                _myOrbShield = _myImgShield != null;
                if (_myOrbShield)
                {
                    try
                    {
                        var shTr = GetMember(shSr, "transform") as Transform;
                        if (shTr != null && HeroPosition(out var shx, out var shy, out _)) { var q = shTr.position; _myShieldDx = q.x - shx; _myShieldDy = q.y - shy; }
                        if (GetMember(shTr, "localScale") is Vector3 ls && Math.Abs(ls.x) > 0.01 && Math.Abs(ls.y) > 0.01) { _myShieldSx = ls.x; _myShieldSy = ls.y; }
                    }
                    catch (Exception e) { if (++_orbFails <= 6) _ctx.Log("орбиты: щит: " + Inner(e).Message); }
                }
                _myImgSatA = _myOrbSatA > 0 ? ImageOf(ItemAt(GetMember(hero, "Array_Obj_UnitSatellitesA"), 0)) : null;
                _myImgSatB = _myOrbSatB > 0 ? ImageOf(ItemAt(GetMember(hero, "Array_Obj_UnitSatellitesB"), 0)) : null;
                _myImgSpirit = _myOrbSpirits > 0 ? ImageOf(ItemAt(GetMember(hero, "List_ObjSpirit"), 1)) : null;
                // В журнал только смена чисел: картинки Спутников и Духов это кадры анимации
                string state = $"щит {(_myOrbShield ? "есть" : "нет")}, спутники {_myOrbSatA}+{_myOrbSatB}, духи {_myOrbSpirits}, электрозона {(_myZone ? $"есть ×{_myZoneSx:0.##}" : "нет")}";
                if (state != _myOrbState && (_myOrbShield || _myZone || _myOrbSatA + _myOrbSatB + _myOrbSpirits > 0))
                {
                    _myOrbState = state;
                    if (_myOrbLogs++ < 12) _ctx.Log($"орбиты (свои): {state}; картинки: щит «{_myImgShield}», спутник «{_myImgSatA}», дух «{_myImgSpirit}»");
                }
            }
            catch (Exception e) { if (++_orbFails <= 3) _ctx.Log("орбиты: " + Inner(e).Message); }
        }

        private void WriteMyOrbits(Utf8JsonWriter w)
        {
            if (_myOrbSatA + _myOrbSatB + _myOrbSpirits == 0 && !_myOrbShield && !_myZone && !_myMc) return;
            w.WriteStartObject("orb");
            w.WriteNumber("sa", _myOrbSatA); w.WriteNumber("sb", _myOrbSatB); w.WriteNumber("sp", _myOrbSpirits); w.WriteNumber("sh", _myOrbShield ? 1 : 0);
            w.WriteNumber("aa", Math.Round(_myOrbAngA, 3)); w.WriteNumber("ab", Math.Round(_myOrbAngB, 3));
            if (_myImgShield != null)
            {
                w.WriteString("ish", _myImgShield);
                w.WriteNumber("shx", Math.Round(_myShieldDx, 3)); w.WriteNumber("shy", Math.Round(_myShieldDy, 3));
                w.WriteNumber("shs", Math.Round(_myShieldSx, 3)); w.WriteNumber("sht", Math.Round(_myShieldSy, 3));
            }
            if (_myImgSatA != null) w.WriteString("isa", _myImgSatA);
            if (_myImgSatB != null) w.WriteString("isb", _myImgSatB);
            if (_myImgSpirit != null) w.WriteString("isp", _myImgSpirit);
            if (_mySpiritOff.Count > 0)
            {
                w.WriteStartArray("so");
                foreach (var v in _mySpiritOff) w.WriteNumberValue(v);
                w.WriteEndArray();
            }
            if (_myZone)
            {
                w.WriteNumber("ez", 1); w.WriteNumber("eza", _myZoneOn ? 1 : 0); w.WriteNumber("ezx", Math.Round(_myZoneSx, 3)); w.WriteNumber("ezy", Math.Round(_myZoneSy, 3));
                if (_myZoneImg != null) w.WriteString("ezi", _myZoneImg);
                if (_myZoneEn.HasValue) w.WriteNumber("eze", _myZoneEn.Value ? 1 : 0);
                if (!float.IsNaN(_myZoneA)) w.WriteNumber("ezc", Math.Round(_myZoneA, 2));
            }
            WriteMyCircle(w);
            w.WriteEndObject();
        }

        private static Exception Inner(Exception e) => (e as TargetInvocationException)?.InnerException ?? e;

        private static readonly Dictionary<(Type, string), MemberInfo> MemberCache = new Dictionary<(Type, string), MemberInfo>();
        private static object GetMember(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            MemberInfo mi;
            lock (MemberCache)
            {
                if (!MemberCache.TryGetValue((t, name), out mi))
                {
                    var p = t.GetProperty(name, AnyInst);
                    mi = p != null && p.GetIndexParameters().Length == 0 ? p : (MemberInfo)t.GetField(name, AnyInst);
                    MemberCache[(t, name)] = mi;
                }
            }
            return mi is PropertyInfo pi ? pi.GetValue(o) : (mi as FieldInfo)?.GetValue(o);
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
        private static double FirstAngle(object coll, double hx, double hy)
        {
            if (!(ItemAt(coll, 0) is Transform tr)) return 0;
            var p = tr.position;
            return Math.Atan2(p.y - hy, p.x - hx);
        }

        private readonly Dictionary<Type, MethodInfo> _rendererGetters = new Dictionary<Type, MethodInfo>();
        private readonly Dictionary<string, object> _sprites = new Dictionary<string, object>();
        private bool _spriteScanDone;
        private double _spriteHarvestAt = -1;

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
            // GetComponentInChildren(true) на телефоне иногда пуст, поэтому сначала свой компонент
            var own = GetComponentOf(go);
            if (own != null) return own;
            return m?.Invoke(go, new object[] { true });
        }

        private readonly Dictionary<Type, MethodInfo> _componentGetters = new Dictionary<Type, MethodInfo>();
        private object GetComponentOf(object go)
        {
            var t = go.GetType();
            if (!_componentGetters.TryGetValue(t, out var m))
            {
                m = t.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(x => x.Name == "GetComponent" && x.IsGenericMethodDefinition && x.GetParameters().Length == 0)
                    ?.MakeGenericMethod(typeof(SpriteRenderer));
                _componentGetters[t] = m;
            }
            try { return m?.Invoke(go, null); } catch { return null; }
        }
        // Картинки только по указателям: обёртка UnityEngine.Sprite из Il2CppInterop роняет игру под Mono (MS_KIT, MELONLOADER.md).
        // «Картинка» ниже это IntPtr на телефоне или заглушка Sprite в тестах
        private static readonly bool RawSprites = typeof(SpriteRenderer).GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance) != null;
        private static IntPtr _getSprite, _setSprite, _getName, _spriteClass;
        private static Type _il2;

        private static readonly Dictionary<Type, PropertyInfo> PointerCache = new Dictionary<Type, PropertyInfo>();
        private static IntPtr PtrOf(object wrapper)
        {
            if (wrapper == null) return IntPtr.Zero;
            var t = wrapper.GetType();
            PropertyInfo p;
            lock (PointerCache)
                if (!PointerCache.TryGetValue(t, out p)) PointerCache[t] = p = t.GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
            return p?.GetValue(wrapper) is IntPtr ptr ? ptr : IntPtr.Zero;
        }

        private static void InitRaw()
        {
            if (_getSprite != IntPtr.Zero) return;
            const string core = "UnityEngine.CoreModule.dll";
            var sr = Raw.Class(core, "UnityEngine", "SpriteRenderer");
            _setSprite = Raw.Method(sr, "set_sprite", 1);
            _getName = Raw.Method(Raw.Class(core, "UnityEngine", "Object"), "get_name", 0);
            _spriteClass = Raw.Class(core, "UnityEngine", "Sprite");
            _getSprite = Raw.Method(sr, "get_sprite", 0);
        }

        private static object SpriteOf(object sr)
        {
            if (sr == null) return null;
            if (!RawSprites) return GetMember(sr, "sprite");
            InitRaw();
            var p = LivePtr(sr);
            if (p == IntPtr.Zero) return null;
            var sp = Raw.Invoke(_getSprite, p);
            return sp == IntPtr.Zero ? null : (object)sp;
        }
        private static void SetSprite(object sr, object sprite)
        {
            if (!RawSprites) { sr.GetType().GetProperty("sprite")?.SetValue(sr, sprite); return; }
            InitRaw();
            var p = LivePtr(sr);
            if (p != IntPtr.Zero) Raw.Invoke(_setSprite, p, (IntPtr)sprite);
        }
        // Прямой вызов Raw на уничтоженном объекте роняет игру целиком.
        private static IntPtr LivePtr(object wrapper)
        {
            var p = PtrOf(wrapper);
            if (p == IntPtr.Zero) return p;
            try { return Raw.IsDestroyed(p) ? IntPtr.Zero : p; } catch { return IntPtr.Zero; }
        }
        private static string NameOf(object o)
        {
            if (o == null) return null;
            string n;
            if (o is IntPtr p)
            {
                InitRaw();
                n = ReadString(Raw.Invoke(_getName, p));
            }
            else n = GetMember(o, "name") as string;
            return string.IsNullOrEmpty(n) ? null : n;
        }
        // Строка IL2CPP: заголовок объекта в 2 указателя, длина int32, символы UTF-16.
        private static string ReadString(IntPtr s)
        {
            if (s == IntPtr.Zero) return null;
            int head = 2 * IntPtr.Size;
            int len = System.Runtime.InteropServices.Marshal.ReadInt32(s + head);
            if (len <= 0 || len > 512) return null;
            return System.Runtime.InteropServices.Marshal.PtrToStringUni(s + head + 4, len);
        }
        private string ImageOf(object go) => NameOf(SpriteOf(RendererOf(go)));

        private void Remember(object sprite)
        {
            var n = NameOf(sprite);
            if (n == null || _sprites.ContainsKey(n)) return;
            if (sprite is IntPtr p) PinOnce(p);
            _sprites[n] = sprite;
        }

        // Картинки живут всё время игры. Закрепляем каждую один раз за запуск, иначе дескрипторы копились бы с каждым боем
        private static readonly HashSet<IntPtr> Pinned = new HashSet<IntPtr>();
        private static void PinOnce(IntPtr p) { if (Pinned.Add(p)) Raw.Pin(p); }

        private List<object> AllSprites()
        {
            var res = new List<object>();
            if (!RawSprites)
            {
                var spType = typeof(SpriteRenderer).GetProperty("sprite")?.PropertyType;
                var m = typeof(Transform).Assembly.GetType("UnityEngine.Resources")?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(x => x.Name == "FindObjectsOfTypeAll" && x.IsGenericMethodDefinition && x.GetParameters().Length == 0);
                if (spType == null || m == null) return res;
                foreach (var o in (IEnumerable)m.MakeGenericMethod(spType).Invoke(null, null)) res.Add(o);
                return res;
            }
            InitRaw();
            // Поля героя и Z_Database с Sprite, Sprite[] и List<Sprite> читаем по указателям, без обёрток
            var spriteType = typeof(SpriteRenderer).GetProperty("sprite")?.PropertyType;
            int fromHero = 0, fromDb = 0, fromAll = 0;
            var hero = HeroObject();
            if (hero != null) fromHero = SpriteFields(hero.GetType(), PtrOf(hero), spriteType, res);
            var db = Hooks.FindType("Z_Database");
            if (db != null) fromDb = SpriteFields(db, IntPtr.Zero, spriteType, res);
            // Метод могли вырезать из сборки
            try { fromAll = FindAllSprites(res); } catch (Exception e) { _ctx.Log("орбиты: FindObjectsOfTypeAll: " + Inner(e).Message); }
            _ctx.Log($"орбиты: картинки — из полей героя {fromHero}, из Z_Database {fromDb}, всех картинок игры {fromAll}");
            return res;
        }

        private static MethodInfo Il2(string name)
        {
            _il2 ??= AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Il2CppInterop.Runtime.IL2CPP")).FirstOrDefault(t => t != null)
                     ?? throw new Exception("нет Il2CppInterop.Runtime.IL2CPP");
            return _il2.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == name) ?? throw new Exception("нет IL2CPP." + name);
        }
        private static IntPtr Field(IntPtr klass, string name) => (IntPtr)Il2("GetIl2CppField").Invoke(null, new object[] { klass, name });
        private static IntPtr ClassOf(IntPtr obj) => (IntPtr)Il2("il2cpp_object_get_class").Invoke(null, new object[] { obj });
        private static int OffsetOf(IntPtr field) => Convert.ToInt32(Il2("il2cpp_field_get_offset").Invoke(null, new object[] { field }));
        private static unsafe IntPtr StaticValue(IntPtr field)
        {
            IntPtr v = IntPtr.Zero;
            Il2("il2cpp_field_static_get_value").Invoke(null, new object[] { field, Pointer.Box(&v, typeof(void*)) });
            return v;
        }

        // obj равен IntPtr.Zero для статических полей.
        private int SpriteFields(Type t, IntPtr obj, Type spriteType, List<object> res)
        {
            if (spriteType == null) return 0;
            int found = 0;
            IntPtr klass = IntPtr.Zero;
            var flags = BindingFlags.Public | BindingFlags.NonPublic | (obj == IntPtr.Zero ? BindingFlags.Static : BindingFlags.Instance);
            foreach (var pr in t.GetProperties(flags))
            {
                var pt = pr.PropertyType;
                int kind = pt == spriteType ? 0
                    : pt.IsGenericType && pt.GetGenericArguments().Length == 1 && pt.GetGenericArguments()[0] == spriteType
                        ? (pt.Name.StartsWith("List") ? 2 : pt.Name.Contains("Array") ? 1 : -1)
                        : -1;
                if (kind < 0) continue;
                try
                {
                    if (klass == IntPtr.Zero) klass = obj != IntPtr.Zero ? ClassOf(obj) : Raw.Class("Assembly-CSharp.dll", "", t.Name);
                    var f = Field(klass, pr.Name);
                    if (f == IntPtr.Zero) continue;
                    var v = obj != IntPtr.Zero ? System.Runtime.InteropServices.Marshal.ReadIntPtr(obj + OffsetOf(f)) : StaticValue(f);
                    if (v == IntPtr.Zero) continue;
                    if (kind == 0) { res.Add(v); found++; continue; }
                    IntPtr arr = v; long n;
                    if (kind == 2)
                    {
                        var lk = ClassOf(v);
                        n = System.Runtime.InteropServices.Marshal.ReadInt32(v + OffsetOf(Field(lk, "_size")));
                        arr = System.Runtime.InteropServices.Marshal.ReadIntPtr(v + OffsetOf(Field(lk, "_items")));
                        if (arr == IntPtr.Zero) continue;
                    }
                    else n = System.Runtime.InteropServices.Marshal.ReadInt64(arr + 3 * IntPtr.Size);
                    for (long i = 0; i < n && i < 5000; i++)
                    {
                        var e = System.Runtime.InteropServices.Marshal.ReadIntPtr(arr + 4 * IntPtr.Size + (int)(i * IntPtr.Size));
                        if (e != IntPtr.Zero && !Raw.IsDestroyed(e)) { res.Add(e); found++; }
                    }
                }
                catch (Exception e) { if (++_orbFails <= 6) _ctx.Log($"орбиты: поле {t.Name}.{pr.Name}: {Inner(e).Message}"); }
            }
            return found;
        }

        [System.Runtime.InteropServices.UnmanagedFunctionPointer(System.Runtime.InteropServices.CallingConvention.Cdecl)]
        private delegate IntPtr FindAllFn(IntPtr type);

        // Метод FindObjectsOfTypeAll могли вырезать из сборки, тогда icall Unity.
        private int FindAllSprites(List<object> res)
        {
            var t2 = (IntPtr)Il2("il2cpp_class_get_type").Invoke(null, new object[] { _spriteClass });
            var tObj = (IntPtr)Il2("il2cpp_type_get_object").Invoke(null, new object[] { t2 });
            IntPtr arr = IntPtr.Zero;
            const string core = "UnityEngine.CoreModule.dll";
            foreach (var cls in new[] { "Resources", "ResourcesAPIInternal" })
            {
                IntPtr k;
                try { k = Raw.Class(core, "UnityEngine", cls); } catch { continue; }
                var m = (IntPtr)Il2("il2cpp_class_get_method_from_name").Invoke(null, new object[] { k, "FindObjectsOfTypeAll", 1 });
                if (m == IntPtr.Zero) continue;
                arr = Raw.Invoke(m, IntPtr.Zero, tObj);
                break;
            }
            if (arr == IntPtr.Zero)
                foreach (var ic in new[] { "UnityEngine.ResourcesAPIInternal::FindObjectsOfTypeAll(System.Type)", "UnityEngine.ResourcesAPIInternal::FindObjectsOfTypeAll",
                                           "UnityEngine.Resources::FindObjectsOfTypeAll(System.Type)", "UnityEngine.Resources::FindObjectsOfTypeAll" })
                {
                    var fn = (IntPtr)Il2("il2cpp_resolve_icall").Invoke(null, new object[] { ic });
                    if (fn == IntPtr.Zero) continue;
                    arr = System.Runtime.InteropServices.Marshal.GetDelegateForFunctionPointer<FindAllFn>(fn)(tObj);
                    break;
                }
            if (arr == IntPtr.Zero) return 0;
            // Массив IL2CPP: заголовок в 2 указателя, bounds, max_length, затем элементы
            long n = System.Runtime.InteropServices.Marshal.ReadInt64(arr + 3 * IntPtr.Size);
            int k2 = 0;
            for (long i = 0; i < n && i < 100000; i++)
            {
                var e = System.Runtime.InteropServices.Marshal.ReadIntPtr(arr + 4 * IntPtr.Size + (int)(i * IntPtr.Size));
                if (e != IntPtr.Zero) { res.Add(e); k2++; }
            }
            return k2;
        }

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

                    Remember(SpriteOf(RendererOf(ItemAt(GetMember(hero, "Array_Obj_UnitSatellitesA"), 0))));
                    Remember(SpriteOf(RendererOf(ItemAt(GetMember(hero, "Array_Obj_UnitSatellitesB"), 0))));
                    Remember(SpriteOf(RendererOf(ItemAt(GetMember(hero, "List_ObjSpirit"), 1))));
                    Remember(SpriteOf(RendererOf(GetMember(hero, "Obj_ElectricZone"))));
                    foreach (var k in CircleKeys) Remember(SpriteOf(GetMember(hero, "SpriteRenderer_MagicCircle" + k)));
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
                _ctx.Mark("орбиты: поиск картинок");
                var all = AllSprites();
                int n = all.Count, kept = 0;
                foreach (var sp in all)
                {
                    var nm = NameOf(sp);
                    if (nm == null || _sprites.ContainsKey(nm)) continue;
                    if (nm.IndexOf("Shield", StringComparison.OrdinalIgnoreCase) < 0 && nm.IndexOf("Satellite", StringComparison.OrdinalIgnoreCase) < 0
                        && nm.IndexOf("Spirit", StringComparison.OrdinalIgnoreCase) < 0 && nm.IndexOf("ElectricZone", StringComparison.OrdinalIgnoreCase) < 0
                        && nm.IndexOf("MagicCircle", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (sp is IntPtr pp) PinOnce(pp);
                    _sprites[nm] = sp; kept++;
                }
                _ctx.Log($"орбиты: поиск картинок по игре — всего {n}, подходящих {kept} ({string.Join(", ", _sprites.Keys.Where(k => k.IndexOf("Shield", StringComparison.OrdinalIgnoreCase) >= 0).Take(8))}) за {sw.ElapsedMilliseconds} мс");
            }
            catch (Exception e) { _ctx.Log("орбиты: поиск картинок не удался: " + Inner(e).Message); }
        }

        private void ApplyImage(OrbClone c, string want, double now, string what, bool onlyIfEmpty = false)
        {
            if (want == null || want == c.Applied || (want == c.Missed && now - c.MissedAt < 3)) return;
            c.Sr ??= RendererOf(c.Go);
            if (c.Sr == null) { c.Missed = want; c.MissedAt = now + 1e9; _ctx.Log($"орбиты: у копии ({what}) нет картинки-рендера"); return; }
            var have = NameOf(SpriteOf(c.Sr));
            if (have == want || (onlyIfEmpty && have != null)) { c.Applied = want; return; }
            var sp = FindSprite(want, now);
            if (sp == null) { if (c.Missed != want) _ctx.Log($"орбиты: картинка «{want}» ({what}) не найдена"); c.Missed = want; c.MissedAt = now; return; }
            SetSprite(c.Sr, sp);
            c.Applied = want;
        }

        private static void ReadOrbits(Partner p, JsonElement s, double now)
        {
            double senderT = Protocol.Num(s, "t", -1);
            var o = Protocol.Obj(s, "orb");
            var st = p.Orb ??= new OrbState();
            if (o.ValueKind != JsonValueKind.Object) { st.SatA = st.SatB = st.Spirits = 0; st.Shield = false; st.Zone = false; st.Mc = st.McOn = false; st.SpiritOff = null; return; }
            st.SatA = Clamp((int)Protocol.Num(o, "sa"), 0, 14);
            st.SatB = Clamp((int)Protocol.Num(o, "sb"), 0, 14);
            st.Spirits = Clamp((int)Protocol.Num(o, "sp"), 0, 14);
            st.Shield = Protocol.Bool(o, "sh");
            st.Zone = Protocol.Bool(o, "ez");
            st.ZoneOn = st.Zone && Protocol.Num(o, "eza", 1) != 0;
            ReadCircle(st, o);
            if (st.Zone)
            {
                st.ZoneSx = Protocol.Num(o, "ezx", 1); st.ZoneSy = Protocol.Num(o, "ezy", 1);
                st.ImgZone = Protocol.Str(o, "ezi"); st.ZoneEn = Protocol.Num(o, "eze", 1) != 0; st.ZoneA = Protocol.Num(o, "ezc", double.NaN);
            }
            if (o.TryGetProperty("so", out var so) && so.ValueKind == JsonValueKind.Array)
            {
                int n = Math.Min(28, so.GetArrayLength());
                var arr = new double[n & ~1];
                int i = 0;
                foreach (var v in so.EnumerateArray()) { if (i >= arr.Length) break; arr[i++] = v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0; }
                st.SpiritOff = arr;
            }
            else st.SpiritOff = null;
            st.ImgShield = Protocol.Str(o, "ish") ?? st.ImgShield;
            if (o.TryGetProperty("shx", out _))
            {
                st.ShieldDx = Protocol.Num(o, "shx"); st.ShieldDy = Protocol.Num(o, "shy", 0.03);
                st.ShieldSx = Protocol.Num(o, "shs", 1); st.ShieldSy = Protocol.Num(o, "sht", 1);
            }
            st.ImgSatA = Protocol.Str(o, "isa") ?? st.ImgSatA;
            st.ImgSatB = Protocol.Str(o, "isb") ?? st.ImgSatB;
            st.ImgSpirit = Protocol.Str(o, "isp") ?? st.ImgSpirit;
            double aa = Protocol.Num(o, "aa"), ab = Protocol.Num(o, "ab");
            if (st.AngAt > 0)
            {
                // Скорость по часам друга: сеть доставляет неровно
                double dt = senderT > 0 && st.SenderT > 0 ? senderT - st.SenderT : now - st.AngAt;
                if (dt > 0.02 && dt < 2)
                {
                    double da = Wrap(aa - st.AngA), db = Wrap(ab - st.AngB);
                    // Скачок угла значит, что круг перестроился, это не вращение
                    if (Math.Abs(da) < 1.0 && Math.Abs(db) < 1.0)
                    {
                        double va = da / dt, vb = db / dt;
                        if (!st.HasSpeed) { st.SpeedA = va; st.SpeedB = vb; st.HasSpeed = true; }
                        else { st.SpeedA += (va - st.SpeedA) * 0.2; st.SpeedB += (vb - st.SpeedB) * 0.2; }
                    }
                }
            }
            st.AngA = aa; st.AngB = ab; st.AngAt = now; st.SenderT = senderT;
        }
        private static int Clamp(int v, int a, int b) => v < a ? a : v > b ? b : v;
        private static double Wrap(double a) { while (a > Math.PI) a -= 2 * Math.PI; while (a < -Math.PI) a += 2 * Math.PI; return a; }

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
            // Instantiate отдаёт обёртку UnityEngine.Object без методов GameObject
            var ptr = PtrOf(raw);
            if (ptr != IntPtr.Zero) return Activator.CreateInstance(typeof(GameObject), ptr);
            return null;
        }

        private static readonly Dictionary<Type, MethodInfo> SetActiveCache = new Dictionary<Type, MethodInfo>();
        private static void SetActive(object go, bool on)
        {
            var t = go.GetType();
            if (!SetActiveCache.TryGetValue(t, out var m))
                SetActiveCache[t] = m = t.GetMethod("SetActive", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
            m?.Invoke(go, new object[] { on });
        }

        private string TreeOf(object go)
        {
            if (go == null) return "нет";
            try
            {
                var tr = GetMember(go, "transform");
                int n = Convert.ToInt32(GetMember(tr, "childCount") ?? 0);
                var s = $"«{NameOf(go)}» активен {GetMember(go, "activeSelf")}, масштаб {GetMember(tr, "localScale")}, картинка «{NameOf(SpriteOf(GetComponentOf(go)))}», детей {n}";
                var getChild = tr?.GetType().GetMethod("GetChild", new[] { typeof(int) });
                for (int i = 0; i < Math.Min(n, 8) && getChild != null; i++)
                {
                    var cgo = GetMember(getChild.Invoke(tr, new object[] { i }), "gameObject");
                    s += $"; [{i}] «{NameOf(cgo)}» {GetMember(cgo, "activeSelf")} «{NameOf(SpriteOf(GetComponentOf(cgo)))}»";
                }
                return s;
            }
            catch (Exception e) { return "ошибка: " + Inner(e).Message; }
        }

        // Копия только для вида, поэтому коллайдер зоны выключаем.
        private void DisableChild(OrbClone c, string name)
        {
            try
            {
                var tr = c?.Tr; if (tr == null) return;
                var getChild = tr.GetType().GetMethod("GetChild", new[] { typeof(int) });
                int n = Convert.ToInt32(GetMember(tr, "childCount") ?? 0);
                for (int i = 0; i < n && getChild != null; i++)
                {
                    var cgo = GetMember(getChild.Invoke(tr, new object[] { i }), "gameObject");
                    if (NameOf(cgo) == name) { SetActive(cgo, false); return; }
                }
            }
            catch (Exception e) { if (++_orbFails <= 6) _ctx.Log($"орбиты: не выключить «{name}»: {Inner(e).Message}"); }
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
            v.ForEach(c => { if (c.Go != null) { try { _destroy?.Invoke(null, new[] { c.Go }); } catch { } } });
        }

        private void EnsureCount(List<OrbClone> list, int want, object hero, string field, int index, string img, double now, string what)
        {
            while (list.Count < want)
            {
                var c = MakeClone(ItemAt(GetMember(hero, field), index));
                if (c == null) return;
                list.Add(c);
            }
            for (int i = 0; i < list.Count; i++)
            {
                if (i < want) ApplyImage(list[i], img, now, what, true);
                Show(list[i], i < want);
            }
        }
        private static void Show(OrbClone c, bool on)
        {
            if (c?.Go == null || c.Active == on) return;
            SetActive(c.Go, on);
            c.Active = on;
        }

        private void PlaceOrbits(Partner p, double now)
        {
            var st = p.Orb;
            if (st == null || p.GhostTr == null || _ctx.Setting("orbitals") <= 0) { if (p.OrbVis != null) HideAll(p.OrbVis); return; }
            var hero = HeroObject();
            if (hero == null) return;
            var v = p.OrbVis ??= new OrbVisuals();
            v.Hidden = false;
            try
            {
                EnsureCount(v.SatA, st.SatA, hero, "Array_Obj_UnitSatellitesA", 0, st.ImgSatA, now, "спутник");
                EnsureCount(v.SatB, st.SatB, hero, "Array_Obj_UnitSatellitesB", 0, st.ImgSatB, now, "спутник B");
                EnsureCount(v.Spirits, st.Spirits, hero, "List_ObjSpirit", 1, st.ImgSpirit, now, "дух");
                if (st.Shield && v.Shield == null && !v.ShieldFailed)
                {
                    // Шаблон с множеством детей это не щит, а что-то крупное
                    var tpl = GetMember(GetMember(hero, "SpriteRenderer_Shield"), "gameObject");
                    int kids = Convert.ToInt32(GetMember(GetMember(tpl, "transform"), "childCount") ?? 0);
                    if (tpl == null || kids > 4) { v.ShieldFailed = true; _ctx.Log($"орбиты: шаблон Щита не подходит («{NameOf(tpl)}», детей {kids})"); }
                    else v.Shield = MakeClone(tpl);
                }
                if (v.Shield != null)
                {
                    if (st.Shield) ApplyImage(v.Shield, st.ImgShield, now, "щит");
                    Show(v.Shield, st.Shield);
                }
                if (st.Zone && v.Zone == null) { v.Zone = MakeClone(GetMember(hero, "Obj_ElectricZone")); DisableChild(v.Zone, "Apply_BoxCollider"); }
                if (v.Zone != null) Show(v.Zone, st.ZoneOn);
                if (v.Zone != null && st.ZoneOn)
                {
                    ApplyImage(v.Zone, st.ImgZone, now, "электрозона");
                    v.Zone.Sr ??= RendererOf(v.Zone.Go);
                    if (v.ZoneEnSet != st.ZoneEn) { v.ZoneEnSet = st.ZoneEn; SetEnabled(v.Zone.Sr, st.ZoneEn); }
                    if (!double.IsNaN(st.ZoneA) && (double.IsNaN(v.ZoneASet) || Math.Abs(v.ZoneASet - st.ZoneA) > 0.02))
                    {
                        v.ZoneASet = st.ZoneA;
                        var c = ColorOf(v.Zone.Sr);
                        if (c != null) { c[3] = (float)st.ZoneA; SetColor(v.Zone.Sr, c); }
                    }
                }
                UpdateCircleCopy(p, v, st, hero, now);
                var key = (st.SatA, st.SatB, st.Spirits, st.Shield, st.Zone);
                if (!v.HasKey || !key.Equals(v.LastKey))
                {
                    v.HasKey = true; v.LastKey = key;
                    if (_orbStateLogs++ < 30)
                        _ctx.Log($"орбиты «{p.Name}»: спутники {st.SatA}+{st.SatB} «{st.ImgSatA}», духи {st.Spirits} «{st.ImgSpirit}», щит {(st.Shield ? "есть" : "нет")} «{st.ImgShield}», электрозона {(st.Zone ? "есть" : "нет")}; копий {v.Count}");
                }
            }
            catch (Exception e) { if (++_orbFails <= 3) _ctx.Log("орбиты, копии: " + Inner(e).Message); return; }

            var g = p.GhostTr.position;
            // Каждый кадр поворачиваем с оценённой скоростью и мягко подтягиваем к углу, предсказанному от последнего угла друга
            double age = st.AngAt > 0 ? Math.Min(1.0, now - st.AngAt) : 0;
            double ta = st.AngA + st.SpeedA * age, tb = st.AngB + st.SpeedB * age;
            double fdt = st.LastFrame < 0 ? 0 : Math.Max(0, Math.Min(0.1, now - st.LastFrame));
            st.LastFrame = now;
            // Число спутников изменилось, круг перестроен: ставим сразу
            if (!st.HasDisp || st.ShownSatA != st.SatA || st.ShownSatB != st.SatB) { st.DispA = ta; st.DispB = tb; st.HasDisp = true; st.ShownSatA = st.SatA; st.ShownSatB = st.SatB; }
            else { st.DispA = Follow(st.DispA, ta, st.SpeedA, fdt); st.DispB = Follow(st.DispB, tb, st.SpeedB, fdt); }
            Ring(v.SatA, st.SatA, g, SatRadius, st.DispA);
            Ring(v.SatB, st.SatB, g, SatRadius, st.DispB);
            if (st.SpiritOff != null && st.SpiritOff.Length > 0) PlaceSpirits(v.Spirits, st, g, fdt);
            else Ring(v.Spirits, st.Spirits, g, SpiritRadius, now * 0.8);
            if (v.Shield != null && st.Shield)
            {
                v.Shield.Tr.position = new Vector3((float)(g.x + st.ShieldDx), (float)(g.y + st.ShieldDy), g.z);
                if (st.ShieldSx != v.ShieldSx || st.ShieldSy != v.ShieldSy)
                {
                    v.ShieldSx = st.ShieldSx; v.ShieldSy = st.ShieldSy;
                    try { SetMember(v.Shield.Tr, "localScale", new Vector3((float)st.ShieldSx, (float)st.ShieldSy, 1f)); } catch { }
                }
            }
            PlaceCircleCopy(v, st, g);
            if (v.Zone != null && st.ZoneOn)
            {
                v.Zone.Tr.position = new Vector3(g.x, g.y, g.z);
                if (!v.ZoneScaleFailed && (st.ZoneSx != v.ZoneSx || st.ZoneSy != v.ZoneSy))
                {
                    v.ZoneSx = st.ZoneSx; v.ZoneSy = st.ZoneSy;
                    try { SetMember(v.Zone.Tr, "localScale", new Vector3((float)st.ZoneSx, (float)st.ZoneSy, 1f)); }
                    catch (Exception e) { v.ZoneScaleFailed = true; _ctx.Log("орбиты: размер электрозоны: " + Inner(e).Message); }
                }
            }
        }

        private static void PlaceSpirits(List<OrbClone> list, OrbState st, Vector3 g, double dt)
        {
            var off = st.SpiritOff;
            if (st.SpiritDisp == null || st.SpiritDisp.Length != off.Length) st.SpiritDisp = (double[])off.Clone();
            else
            {
                double k = Math.Min(1, dt * 10);
                for (int i = 0; i < off.Length; i++) st.SpiritDisp[i] += (off[i] - st.SpiritDisp[i]) * k;
            }
            int n = Math.Min(off.Length / 2, list.Count);
            for (int i = 0; i < n; i++)
                list[i].Tr.position = new Vector3((float)(g.x + st.SpiritDisp[2 * i]), (float)(g.y + st.SpiritDisp[2 * i + 1]), g.z);
        }

        private static double Follow(double disp, double target, double speed, double dt)
        {
            disp += speed * dt;
            double err = Wrap(target - disp);
            if (Math.Abs(err) > 0.8) return target;
            return disp + err * Math.Min(1, dt * 4);
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
            if (v.Hidden) return;                                     // Зовётся каждый кадр, пока союзника нет
            v.Hidden = true;
            v.ForEach(c => Show(c, false));
        }

        // Картинки и объекты прошлого боя больше не годятся.
        private void ResetOrbits()
        {
            _sprites.Clear(); _spriteScanDone = false; _spriteHarvestAt = -1; _myOrbState = null; _myOrbLogs = 0; _orbStateLogs = 0; _mySpiritTr.Clear(); _mySpiritOff.Clear();
        }
    }
}
