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
            public double SenderT = -1;                               // время снятия угла у друга (его часы)
            public bool HasSpeed;
            public double DispA, DispB, LastFrame = -1;               // что рисуем: угол плавно догоняет предсказанный
            public bool HasDisp;
            public int ShownSatA, ShownSatB;
            public double[] SpiritOff;                                // смещения духов от героя друга (x, y парами), null — старая версия
            public double[] SpiritDisp;                               // что рисуем: смещения плавно догоняют присланные
            public bool Zone, ZoneOn;                                 // Электрозона: есть / сейчас видна
            public int ZoneRecv, ZoneRecvOn;                          // для журнала: сообщений с зоной / из них «видна»
            public string ImgZone; public bool ZoneEn = true; public double ZoneA = double.NaN;   // кадр, enabled и прозрачность рендера зоны (1.4.8)
            public double ShieldDx, ShieldDy = 0.03, ShieldSx = 1, ShieldSy = 1;
            public double ZoneSx = 1, ZoneSy = 1;
            public bool Mc, McOn; public double McS = 1;                // Маг. круг (1.5.9): есть / включён, масштаб, кадры и повороты кругов
            public readonly string[] McImg = new string[3]; public readonly double[] McRot = new double[3];
        }

        internal sealed class OrbClone
        {
            public object Go; public Transform Tr; public object Sr;
            public string Applied, Missed;
            public double MissedAt;
            public bool? Active;
        }

        /// <summary>Наши копии вокруг союзника одного друга.</summary>
        internal sealed class OrbVisuals
        {
            public readonly List<OrbClone> SatA = new List<OrbClone>(), SatB = new List<OrbClone>(), Spirits = new List<OrbClone>();
            public OrbClone Shield, Zone, Mc;
            public readonly List<OrbClone> McKids = new List<OrbClone>();
            public bool McFailed, McLogged; public double McS = double.NaN;
            public bool Logged, ShieldLogged, ZoneScaleFailed;
            public double ZoneSx = double.NaN, ZoneSy = double.NaN;
            public bool? ZoneEnSet; public double ZoneASet = double.NaN;
            public (int, int, int, bool, bool, string, string, string) LastKey;
            public bool HasKey, Hidden, ShieldFailed;
            public double ShieldSx = double.NaN, ShieldSy = double.NaN;
            public IEnumerable<OrbClone> All => SatA.Concat(SatB).Concat(Spirits).Concat(new[] { Shield, Zone, Mc }.Where(c => c != null));
        }

        // ------------------------------------------------------------ отправка: своё состояние

        private double _orbAt = -1;
        private int _myOrbSatA, _myOrbSatB, _myOrbSpirits;
        private bool _myOrbShield;
        private double _myOrbAngA, _myOrbAngB;
        private string _myImgShield, _myImgSatA, _myImgSatB, _myImgSpirit;
        private double _myShieldDx, _myShieldDy = 0.03, _myShieldSx = 1, _myShieldSy = 1;
        private bool _myShieldLogged;
        private int _orbFails, _orbStateLogs;
        private string _myOrbState;
        private int _myOrbLogs;
        private readonly List<Transform> _mySpiritTr = new List<Transform>();   // включённые духи (их объекты) — смещения шлём при каждой отправке
        private readonly List<double> _mySpiritOff = new List<double>();
        private bool _myZone, _myZoneOn;      // заклинание есть / объект зоны сейчас включён (зона вспыхивает — читаем при каждой отправке)
        private object _myZoneObj;
        private string _myZoneImg; private bool? _myZoneEn; private float _myZoneA = float.NaN;
        private int _zoneSent, _zoneSentOn, _zoneDiags, _zoneCopyDiags;   // журнал Электрозоны (1.4.7): почему копию не видно
        private double _zoneDiagAt, _zoneCopyDiagAt;
        private double _myZoneSx = 1, _myZoneSy = 1;

        /// <summary>Своё состояние «орбит»: угол спутников — при каждой отправке (он вместе со временем «t» в том же сообщении),
        /// остальное — не чаще 4 раз в секунду (чтение массивов героя через рефлексию).</summary>
        private double _zoneLookAt;

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
                // игра мигает зоной рендером (enabled/прозрачность) и меняет кадры ElectricZone1A/1B/1C — пересылаем как есть,
                // но не чаще 10 раз в секунду (рендер, картинка, цвет — четыре обращения к игре через рефлексию на отправку)
                _zoneLookAt = now + 0.1;
                try
                {
                    var zsr = RendererOf(_myZoneObj);
                    _myZoneImg = NameOf(SpriteOf(zsr)); _myZoneEn = EnabledOf(zsr);
                    var zc = ColorOf(zsr); _myZoneA = zc != null ? zc[3] : float.NaN;
                }
                catch { _myZoneImg = null; _myZoneEn = null; _myZoneA = float.NaN; }
            }
            if (_myZone)
            {
                _zoneSent++; if (_myZoneOn) _zoneSentOn++;
                if (now >= _zoneDiagAt && _zoneDiags < 4)
                {
                    _zoneDiagAt = now + 15; _zoneDiags++;
                    var hero1 = HeroObject();
                    _ctx.Log($"электрозона (своя): видна в {_zoneSentOn} из {_zoneSent} отправок; объект {TreeOf(_myZoneObj)}, {VisOf(RendererOf(_myZoneObj))}; земля {TreeOf(GetMember(hero1, "Obj_EffectGround_ElectricZone"))}");
                }
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
                // духи: включённые флагом и объект активен; их Transform — для смещений
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
                // Электрозона: объект на герое, включён, пока заклинание есть; размер — масштаб объекта
                var ez = GetMember(hero, "Obj_ElectricZone");
                _myZone = GetMember(hero, "IsElectricZone_P") is bool ezp ? ezp : ez != null && Convert.ToBoolean(GetMember(ez, "activeSelf") ?? false);
                _myZoneObj = _myZone ? ez : null;
                if (_myZone)
                {
                    try { if (GetMember(GetMember(ez, "transform"), "localScale") is Vector3 ls && Math.Abs(ls.x) > 0.01 && Math.Abs(ls.y) > 0.01) { _myZoneSx = ls.x; _myZoneSy = ls.y; } }
                    catch { }
                }
                // Щит: картинку рисует SpriteRenderer_Shield (не внутри Obj_Shield — тот «1100» без картинки, разведка и журнал 1.4.5);
                // включён, пока заклинание есть (IsShield_P) и у рендера есть картинка (она меняется с зарядами: Shield2_01…04)
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
                        if (!_myShieldLogged)
                        {
                            _myShieldLogged = true;
                            var sgo = GetMember(shSr, "gameObject");
                            _ctx.Log($"орбиты (свои): Щит рисует «{NameOf(sgo)}» (родитель «{NameOf(GetMember(GetMember(shTr, "parent"), "gameObject"))}», детей {GetMember(shTr, "childCount")}), смещение ({_myShieldDx:0.00}; {_myShieldDy:0.00}), масштаб {_myShieldSx:0.##}");
                        }
                    }
                    catch (Exception e) { if (++_orbFails <= 6) _ctx.Log("орбиты: щит: " + Inner(e).Message); }
                }
                _myImgSatA = _myOrbSatA > 0 ? ImageOf(ItemAt(GetMember(hero, "Array_Obj_UnitSatellitesA"), 0)) : null;
                _myImgSatB = _myOrbSatB > 0 ? ImageOf(ItemAt(GetMember(hero, "Array_Obj_UnitSatellitesB"), 0)) : null;
                _myImgSpirit = _myOrbSpirits > 0 ? ImageOf(ItemAt(GetMember(hero, "List_ObjSpirit"), 1)) : null;
                // в журнал — только при смене числа или включения (картинки Спутников и Духов — кадры анимации, меняются постоянно)
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

        // поиск члена по имени — один раз на (тип, имя): чтение орбит идёт при каждой отправке
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
            object r = null;
            // сначала свой компонент (обычный GetComponent<T>), потом дети — GetComponentInChildren(true) на телефоне иногда пуст
            var own = GetComponentOf(go);
            if (own != null) return own;
            r = m?.Invoke(go, new object[] { true });
            return r;
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
        // Картинки — только по указателям: обёртка UnityEngine.Sprite из Il2CppInterop роняет игру под Mono (MS_KIT, MELONLOADER.md).
        // «Картинка» ниже — либо IntPtr (на телефоне), либо объект-заглушка Sprite (в тестах без Unity).
        private static readonly bool RawSprites = typeof(SpriteRenderer).GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance) != null;
        private static IntPtr _getSprite, _setSprite, _getName, _spriteClass;
        private static Type _il2;

        private static readonly Dictionary<Type, PropertyInfo> PointerCache = new Dictionary<Type, PropertyInfo>();
        /// <summary>Адрес объекта игры (обёртка IL2CPP, свойство Pointer); поиск свойства — один раз на тип (зовётся на каждого врага).</summary>
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

        /// <summary>Картинка рендера (null — нет).</summary>
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
        /// <summary>
        /// Указатель на живой объект Unity: уничтоженный (смена области в 3:04, конец боя) — IntPtr.Zero. Прямой вызов (Raw) на
        /// уничтоженный объект роняет игру целиком (1.5.10: вылет у друга внутри отправки состояния сразу после смены области).
        /// </summary>
        private static IntPtr LivePtr(object wrapper)
        {
            var p = PtrOf(wrapper);
            if (p == IntPtr.Zero) return p;
            try { return Raw.IsDestroyed(p) ? IntPtr.Zero : p; } catch { return IntPtr.Zero; }
        }
        /// <summary>Имя картинки (или любого объекта Unity в тестах).</summary>
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
        /// <summary>Строка IL2CPP: заголовок объекта (2 указателя), длина int32, символы UTF-16.</summary>
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

        // картинки — ресурсы игры, живут всё время; закрепляем каждую один раз за запуск (иначе дескрипторы копились бы с каждым боем)
        private static readonly HashSet<IntPtr> Pinned = new HashSet<IntPtr>();
        private static void PinOnce(IntPtr p) { if (Pinned.Add(p)) Raw.Pin(p); }

        /// <summary>Все картинки игры: на телефоне — Resources.FindObjectsOfTypeAll(typeof(Sprite)) по указателям, в тестах — заглушка.</summary>
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
            // 1) поля героя и Z_Database с картинками (Sprite, Sprite[], List<Sprite>) — читаем по указателям, без обёрток
            var spriteType = typeof(SpriteRenderer).GetProperty("sprite")?.PropertyType;
            int fromHero = 0, fromDb = 0, fromAll = 0;
            var hero = HeroObject();
            if (hero != null) fromHero = SpriteFields(hero.GetType(), PtrOf(hero), spriteType, res);
            var db = Hooks.FindType("Z_Database");
            if (db != null) fromDb = SpriteFields(db, IntPtr.Zero, spriteType, res);
            // 2) все картинки игры (Resources.FindObjectsOfTypeAll) — если метод не вырезан из сборки
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

        /// <summary>Картинки из полей типа: obj — объект (IntPtr.Zero — статические поля). Возвращает число найденных.</summary>
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

        /// <summary>Resources.FindObjectsOfTypeAll(typeof(Sprite)): метод (если не вырезан) или сам icall Unity.</summary>
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
            // массив IL2CPP: заголовок (2 указателя), bounds, max_length, затем элементы
            long n = System.Runtime.InteropServices.Marshal.ReadInt64(arr + 3 * IntPtr.Size);
            int k2 = 0;
            for (long i = 0; i < n && i < 100000; i++)
            {
                var e = System.Runtime.InteropServices.Marshal.ReadIntPtr(arr + 4 * IntPtr.Size + (int)(i * IntPtr.Size));
                if (e != IntPtr.Zero) { res.Add(e); k2++; }
            }
            return k2;
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

        /// <summary>Поставить копии картинку, как у друга (если надо).</summary>
        private void ApplyImage(OrbClone c, string want, double now, string what, bool onlyIfEmpty = false)
        {
            if (want == null || want == c.Applied || (want == c.Missed && now - c.MissedAt < 3)) return;
            c.Sr ??= RendererOf(c.Go);
            if (c.Sr == null) { c.Missed = want; c.MissedAt = now + 1e9; _ctx.Log($"орбиты: у копии ({what}) нет картинки-рендера"); return; }
            var have = NameOf(SpriteOf(c.Sr));
            if (have == want || (onlyIfEmpty && have != null)) { c.Applied = want; return; }      // у своей копии картинка уже есть — не трогаем
            var sp = FindSprite(want, now);
            if (sp == null) { if (c.Missed != want) _ctx.Log($"орбиты: картинка «{want}» ({what}) не найдена"); c.Missed = want; c.MissedAt = now; return; }
            SetSprite(c.Sr, sp);
            c.Applied = want;
        }

        // ------------------------------------------------------------ приём: состояние друга

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
            st.ZoneOn = st.Zone && Protocol.Num(o, "eza", 1) != 0;          // без «eza» (1.4.4–1.4.5) — показывать всё время
            if (st.Zone) { st.ZoneRecv++; if (st.ZoneOn) st.ZoneRecvOn++; }
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
                // скорость вращения — по времени друга (сеть доставляет неровно); без времени — по времени приёма
                double dt = senderT > 0 && st.SenderT > 0 ? senderT - st.SenderT : now - st.AngAt;
                if (dt > 0.02 && dt < 2)
                {
                    double da = Wrap(aa - st.AngA), db = Wrap(ab - st.AngB);
                    // скачок угла (спутников стало больше, круг перестроился) — не вращение, скорость по нему не считаем
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

        private static readonly Dictionary<Type, MethodInfo> SetActiveCache = new Dictionary<Type, MethodInfo>();
        private static void SetActive(object go, bool on)
        {
            var t = go.GetType();
            if (!SetActiveCache.TryGetValue(t, out var m))
                SetActiveCache[t] = m = t.GetMethod("SetActive", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool) }, null);
            m?.Invoke(go, new object[] { on });
        }

        /// <summary>Для журнала: объект, его включение, масштаб, картинка и дети (до 8) с их картинками.</summary>
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

        /// <summary>Выключить ребёнка копии по имени (коллайдер зоны: копия только для вида).</summary>
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
            foreach (var c in v.All)
                if (c.Go != null) { try { _destroy?.Invoke(null, new[] { c.Go }); } catch { } }
        }

        /// <summary>Держать столько копий, сколько у друга, и включать/выключать их без лишних вызовов.</summary>
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

        /// <summary>Каждый кадр: копии вокруг союзника-друга (по сглаженной позиции союзника).</summary>
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
                    // шаблон — объект с рендером Щита (SpriteRenderer_Shield.gameObject); если это что-то крупное — не копируем
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
                if (st.Zone && now >= _zoneCopyDiagAt && _zoneCopyDiags < 4)
                {
                    _zoneCopyDiagAt = now + 15; _zoneCopyDiags++;
                    var gz = p.GhostTr != null ? p.GhostTr.position.z : float.NaN;
                    _ctx.Log($"электрозона «{p.Name}»: у друга видна в {st.ZoneRecvOn} из {st.ZoneRecv} сообщений; копия {(v.Zone == null ? "не создана" : TreeOf(v.Zone.Go) + $", {VisOf(v.Zone.Sr ?? RendererOf(v.Zone.Go))}, z {v.Zone.Tr.position.z:0.##} (союзник {gz:0.##})")}; " +
                             $"от друга: кадр «{st.ImgZone}», рендер {(st.ZoneEn ? "вкл" : "выкл")}, α {st.ZoneA:0.00}; " +
                             $"шаблон {TreeOf(GetMember(hero, "Obj_ElectricZone"))}");
                }
                // строку для журнала собираем, только когда что-то изменилось (не каждый кадр)
                var key = (st.SatA, st.SatB, st.Spirits, st.Shield, st.Zone, "", "", "");
                if (!v.HasKey || !key.Equals(v.LastKey))
                {
                    v.HasKey = true; v.LastKey = key;
                    if (_orbStateLogs++ < 30)
                        _ctx.Log($"орбиты «{p.Name}»: спутники {st.SatA}+{st.SatB} «{st.ImgSatA}», духи {st.Spirits} «{st.ImgSpirit}», щит {(st.Shield ? "есть" : "нет")} «{st.ImgShield}», электрозона {(st.Zone ? "есть" : "нет")}; копий {v.All.Count()}");
                }
                if (st.Shield && v.Shield?.Active == true && !v.ShieldLogged)
                {
                    v.ShieldLogged = true;
                    _ctx.Log($"орбиты «{p.Name}»: копия Щита «{NameOf(v.Shield.Go)}» включена, рендер «{NameOf(GetMember(v.Shield.Sr, "gameObject"))}», картинка «{NameOf(SpriteOf(v.Shield.Sr))}», детей {GetMember(v.Shield.Tr, "childCount")}");
                }
            }
            catch (Exception e) { if (++_orbFails <= 3) _ctx.Log("орбиты, копии: " + Inner(e).Message); return; }

            var g = p.GhostTr.position;
            // угол: каждый кадр поворачиваем с оценённой скоростью и мягко подтягиваем к предсказанному по последнему углу друга
            double age = st.AngAt > 0 ? Math.Min(1.0, now - st.AngAt) : 0;
            double ta = st.AngA + st.SpeedA * age, tb = st.AngB + st.SpeedB * age;
            double fdt = st.LastFrame < 0 ? 0 : Math.Max(0, Math.Min(0.1, now - st.LastFrame));
            st.LastFrame = now;
            // число спутников изменилось — круг у друга перестроен, ставим сразу
            if (!st.HasDisp || st.ShownSatA != st.SatA || st.ShownSatB != st.SatB) { st.DispA = ta; st.DispB = tb; st.HasDisp = true; st.ShownSatA = st.SatA; st.ShownSatB = st.SatB; }
            else { st.DispA = Follow(st.DispA, ta, st.SpeedA, fdt); st.DispB = Follow(st.DispB, tb, st.SpeedB, fdt); }
            Ring(v.SatA, st.SatA, g, SatRadius, st.DispA);
            Ring(v.SatB, st.SatB, g, SatRadius, st.DispB);
            if (st.SpiritOff != null && st.SpiritOff.Length > 0) PlaceSpirits(v.Spirits, st, g, fdt);
            else Ring(v.Spirits, st.Spirits, g, SpiritRadius, now * 0.8);          // друг на старой версии — смещений нет
            if (v.Shield != null && st.Shield)
            {
                v.Shield.Tr.position = new Vector3((float)(g.x + st.ShieldDx), (float)(g.y + st.ShieldDy), g.z);
                if (st.ShieldSx != v.ShieldSx || st.ShieldSy != v.ShieldSy)
                {
                    v.ShieldSx = st.ShieldSx; v.ShieldSy = st.ShieldSy;
                    try { v.Shield.Tr.GetType().GetProperty("localScale")?.SetValue(v.Shield.Tr, new Vector3((float)st.ShieldSx, (float)st.ShieldSy, 1f)); } catch { }
                }
            }
            PlaceCircleCopy(v, st, g);
            if (v.Zone != null && st.ZoneOn)
            {
                v.Zone.Tr.position = new Vector3(g.x, g.y, g.z);
                if (!v.ZoneScaleFailed && (st.ZoneSx != v.ZoneSx || st.ZoneSy != v.ZoneSy))
                {
                    v.ZoneSx = st.ZoneSx; v.ZoneSy = st.ZoneSy;
                    try { v.Zone.Tr.GetType().GetProperty("localScale")?.SetValue(v.Zone.Tr, new Vector3((float)st.ZoneSx, (float)st.ZoneSy, 1f)); }
                    catch (Exception e) { v.ZoneScaleFailed = true; _ctx.Log("орбиты: размер электрозоны: " + Inner(e).Message); }
                }
            }
        }

        /// <summary>Духи — там же относительно союзника, где они у друга относительно героя (плавно, без рывков между сообщениями).</summary>
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
            if (Math.Abs(err) > 0.8) return target;                   // большой разрыв (спутников стало больше и т.п.) — сразу
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
            if (v.Hidden) return;                                     // вызывается каждый кадр, пока союзника нет — без лишней работы
            v.Hidden = true;
            foreach (var c in v.All) Show(c, false);
        }

        /// <summary>Конец боя: картинки и объекты прошлого боя больше не годятся.</summary>
        private void ResetOrbits()
        {
            _myShieldLogged = false;
            _sprites.Clear(); _spriteScanDone = false; _spriteHarvestAt = -1; _myOrbState = null; _myOrbLogs = 0; _orbStateLogs = 0; _mySpiritTr.Clear(); _mySpiritOff.Clear();
            _zoneSent = _zoneSentOn = _zoneDiags = _zoneCopyDiags = 0; _zoneDiagAt = _zoneCopyDiagAt = 0;
        }
    }
}
