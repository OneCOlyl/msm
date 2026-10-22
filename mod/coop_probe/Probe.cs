using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using ModKit;
using UnityEngine;

namespace MagicSurvivalCoop.Probe
{
    /// <summary>
    /// Разведка 4 — герой: как устроены заклинания, которые не стреляют снарядами (Щит, Спутники, Духи, двойники…), —
    /// чтобы показать их у союзника-друга. Ничего не меняет в игре, только записывает:
    ///   • структуру героя (поля и методы с Shield/Spirit/Satellite/Doppel/… в имени);
    ///   • снимки этих полей раз в 15 с: объекты (активен ли, где относительно героя, сколько детей), массивы и списки, числа;
    ///   • из каких объектов вылетают снаряды героя (Bullet_Create) — по именам источников.
    /// Табло вверху экрана подсказывает, что делать. Отчёт — UserData/ModKit/coop_probe.txt (результаты разведок врагов — в docs/ENEMY_SYNC.md).
    /// </summary>
    public class ProbeMod : IKitMod
    {
        public ModDef Define() => new ModDef
        {
            Id = "coop_probe",
            Name = "Кооператив: разведка",
            Version = "0.5.0",
            Description = "Разведка героя: как устроены Щит, Спутники, Духи — чтобы показать их у союзника-друга. Ничего не меняет, только записывает. Потом выключите.",
            Depends = { ["modkit_combat"] = ">=1.0", ["modkit_spells"] = ">=1.0" },
            OnEnable = ctx => _p = new Probe(ctx),
            OnDisable = ctx => { _p?.Finish("мод выключен"); _p = null; },
        };
        private Probe _p;
    }

    internal sealed class Probe
    {
        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Any = Inst | BindingFlags.Static;
        private static readonly Regex Interesting = new Regex("Shield|Spirit|Satellite|Doppel|Barrier|Orbit|Aura|Guard|Sword|Blade|Circle|Field|Zone", RegexOptions.IgnoreCase);

        /// <summary>Только для тестов на ПК (через рефлексию).</summary>
        private static string DirOverride;
        private static Func<object> HeroOverride;

        private readonly ModContext _ctx;
        private readonly IEnemies _enemies;
        private readonly ISpells _spells;
        private readonly StringBuilder _out = new StringBuilder();
        private readonly string _path;

        private bool _inBattle, _done, _dumped;
        private double _t0 = -1, _nextSnap;
        private int _snaps;
        private UiKit.HudBar _board;
        private string _boardText = "";
        private int _uiFails;
        private readonly Dictionary<object, (string name, int n, Dictionary<int, int> ids)> _sources = new Dictionary<object, (string, int, Dictionary<int, int>)>();
        private object _heroTrKey;

        public Probe(ModContext ctx)
        {
            _ctx = ctx;
            _enemies = ctx.Use<IEnemies>("modkit_combat");
            _spells = ctx.Use<ISpells>("modkit_spells");
            string dir = DirOverride;
            if (dir == null) { try { dir = Paths.Dir; } catch { dir = "/sdcard/MelonLoader/com.vkslrzm.Zombix/UserData/ModKit"; } }
            _path = Path.Combine(dir, "coop_probe.txt");
            W($"=== coop_probe 0.5.0 (разведка героя + копии Щита/Спутника/Духа), {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            ctx.Log("разведка героя включена: начните бой, берите Щит, Спутник, Духа, если выпадут");

            ctx.Before("C_Battle_AllyUnit", "Bullet_Create", 13, (s, a) => OnBullet(a));
            ctx.Before("C_Battle_Manager", "Update", 0, (s, a) => CloneFrame());
            ctx.Events.On("battleStart", _ => ctx.Guard("начало боя", OnBattleStart));
            ctx.Events.On("battleEnd", _ => { if (_inBattle && !_done) Finish("бой закончился"); _inBattle = false; DestroyBoard(); });
            ctx.Every(250, () => ctx.Guard("шаг", Step));
            ctx.OnCleanup(() => { DestroyBoard(); Flush(); });
        }

        // ------------------------------------------------------------ вывод

        private void W(string line) => _out.AppendLine(line);
        private void Flush()
        {
            try { File.WriteAllText(_path, _out.ToString(), new UTF8Encoding(false)); }
            catch (Exception e) { _ctx.Log("не удалось записать отчёт: " + e.Message); }
        }
        private double GameT => _t0 < 0 ? 0 : _ctx.Timer.Now - _t0;

        private void ShowBoard()
        {
            try
            {
                if (_board == null || !_board.Alive) _board = UiKit.AddHudBar("coop_probe_board", 10, 70, 700, 56, new[] { 0.08, 0.08, 0.22, 0.85 });
                _board.Set(1, _boardText);
            }
            catch (Exception e) { if (_uiFails++ < 3) W("табло: " + e.Message); _board = null; }
        }
        private void DestroyBoard() { try { _board?.Destroy(); } catch { } _board = null; }

        // ------------------------------------------------------------ рефлексия

        private object Hero() => HeroOverride != null ? HeroOverride() : typeof(BattleState).GetProperty("Hero", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);

        private static object Get(object o, string name)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(name, Any);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(p.GetGetMethod(true)?.IsStatic == true ? null : o);
            var f = o.GetType().GetField(name, Any);
            return f?.GetValue(f.IsStatic ? null : o);
        }
        private static object Ptr(object o) { try { var p = o?.GetType().GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance)?.GetValue(o); return p is IntPtr ip && ip != IntPtr.Zero ? (object)ip : o; } catch { return o; } }
        private static string TypeName(Type t) => t == null ? "?" : !t.IsGenericType ? t.Name : t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + ">";
        private static bool Junk(string n) => n.StartsWith("NativeMethodInfoPtr") || n.StartsWith("NativeFieldInfoPtr") || n.StartsWith("Il2Cpp") || n.StartsWith("<") || n == "NativeClassPtr";

        // ------------------------------------------------------------ события

        private void OnBattleStart()
        {
            if (_done) return;
            _inBattle = true; _t0 = _ctx.Timer.Now; _nextSnap = 5;
            W(""); W("===== бой начался =====");
            Flush();
        }

        private void OnBullet(object[] a)
        {
            if (!_inBattle || _done || a == null || a.Length < 2) return;
            try
            {
                var src = a[0]; var key = Ptr(src);
                if (key == null) return;
                int id = Convert.ToInt32(a[1]);
                if (!_sources.TryGetValue(key, out var v))
                {
                    if (_sources.Count > 80) return;
                    string name = "?";
                    try { name = Get(src, "name") as string ?? "?"; } catch { }
                    try
                    {
                        if (_heroTrKey == null) _heroTrKey = Ptr(Get(Hero(), "Transform_ObjUnit"));
                        if (Equals(_heroTrKey, key)) name = "ГЕРОЙ (" + name + ")";
                    }
                    catch { }
                    v = (name, 0, new Dictionary<int, int>());
                }
                v.ids[id] = v.ids.TryGetValue(id, out var n) ? n + 1 : 1;
                _sources[key] = (v.name, v.n + 1, v.ids);
            }
            catch { }
        }

        private void Step()
        {
            if (!_inBattle || _done) return;
            double t = GameT;
            if (!_dumped && t >= 2) { _dumped = true; _ctx.Guard("структура героя", DumpStructure); }
            if (t >= _nextSnap) { _nextSnap = t + 15; _ctx.Guard("снимок героя", Snapshot); }
            if (_cloneStage == 0 && t >= 10) { _cloneStage = 1; _ctx.Guard("копии", ClonesStart); }
            if (_cloneStage == 1 && t >= 11) { _cloneStage = 2; _ctx.Guard("копии", () => ClonesCheck("через 1 с ведения")); }
            if (_cloneStage == 2 && t >= 16) { _cloneStage = 3; _ctx.Guard("копии", () => ClonesCheck("через 6 с ведения")); _cloneFollow = false; _cloneFreeAt = t; foreach (var c in _clones) c.FreeStart = PosOf(c.Go); }
            if (_cloneStage == 3 && t >= 19) { _cloneStage = 4; _ctx.Guard("копии", ClonesFreeCheck); }
            if (_cloneStage == 4 && t >= 22) { _cloneStage = 5; _ctx.Guard("копии", ClonesEnd); }
            if (_cloneStage >= 1 && _cloneStage <= 4)
            {
                _boardText = $"Опыт копий: СПРАВА — копия Щита, СЛЕВА — Спутника, СВЕРХУ — Духа. {(_cloneFollow ? "Идут за героем" : "Оставлены на месте")} ({Math.Max(0, 22 - t):0} с)";
                ShowBoard();
                return;
            }
            if (t - _spellsAt >= 2 || _spellsText == null) { _spellsAt = t; _spellsText = SpellsText(); }
            _boardText = $"Разведка героя: берите Щит, Спутник, Духа. Снимков {_snaps}, следующий через {Math.Max(0, _nextSnap - t):0} с. Заклинания: {_spellsText}";
            ShowBoard();
        }

        private int SafeLevel(int id) { try { return _spells.Level(id); } catch { return 0; } }
        private double _spellsAt;
        private string _spellsText;
        private string SpellsText() => string.Join(" ", Enumerable.Range(1, 21).Select(id => (id, l: SafeLevel(id))).Where(x => x.l > 0).Select(x => $"{x.id}:{x.l}"));

        // ------------------------------------------------------------ опыт копий: Щит, Спутник, Дух

        private sealed class CloneInfo { public string What; public object Go; public double Dx, Dy, MaxErr; public (double x, double y)? Last, FreeStart; }
        private readonly List<CloneInfo> _clones = new List<CloneInfo>();
        private int _cloneStage;
        private bool _cloneFollow;
        private double _cloneFreeAt;

        private static (double x, double y)? PosOf(object go)
        {
            try { if (Get(go, "transform") is Transform tr) { var p = tr.position; return (p.x, p.y); } } catch { }
            return null;
        }
        private static object Item(object coll, int i)
        {
            if (coll == null) return null;
            var m = coll.GetType().GetMethod("get_Item", new[] { typeof(int) });
            if (m != null) return m.Invoke(coll, new object[] { i });
            return coll is IList l && l.Count > i ? l[i] : null;
        }

        private void ClonesStart()
        {
            W(""); W("===== опыт копий: копии Щита, Спутника и Духа героя рядом с героем =====");
            var hero = Hero(); if (hero == null) { W("  героя нет"); return; }
            var objType = typeof(Transform).BaseType?.BaseType;                         // UnityEngine.Object
            var inst = objType?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "Instantiate" && !m.IsGenericMethod && m.GetParameters().Length == 1);
            if (inst == null) { W("  нет Object.Instantiate"); return; }
            var plan = new List<(string what, Func<object> tpl, double dx, double dy)>
            {
                ("Щит (Obj_Shield)", () => Get(hero, "Obj_Shield"), 3, 0),
                ("Спутник (Array_Obj_UnitSatellitesA[0])", () => Item(Get(hero, "Array_Obj_UnitSatellitesA"), 0), -3, 0),
                ("Дух (List_ObjSpirit[1])", () => Item(Get(hero, "List_ObjSpirit"), 1), 0, 3),
            };
            foreach (var (what, tpl, dx, dy) in plan)
            {
                try
                {
                    var t = tpl();
                    if (t == null) { W($"  {what}: шаблона нет"); continue; }
                    Flush(); _ctx.Mark("копия: " + what);
                    var go = inst.Invoke(null, new[] { t });
                    if (go == null) { W($"  {what}: копия не создалась"); continue; }
                    Call(go, "SetActive", true);
                    var c = new CloneInfo { What = what, Go = go, Dx = dx, Dy = dy };
                    _clones.Add(c);
                    var tr = Get(go, "transform");
                    W($"  {what}: копия «{Get(go, "name")}» создана, активна {Get(go, "activeSelf")}, детей {Get(tr, "childCount")}");
                }
                catch (Exception e) { W($"  {what}: {((e as TargetInvocationException)?.InnerException ?? e).GetType().Name}: {((e as TargetInvocationException)?.InnerException ?? e).Message}"); }
            }
            _cloneFollow = true;
            Flush();
        }

        private static void Call(object o, string name, params object[] args)
        {
            var m = o.GetType().GetMethods(Inst).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length) ?? throw new MissingMethodException(name);
            m.Invoke(o, args);
        }

        /// <summary>Каждый кадр, пока идёт ведение: проверить, где копия (не сдвинула ли её игра), и поставить рядом с героем.</summary>
        private void CloneFrame()
        {
            if (!_cloneFollow || _clones.Count == 0) return;
            try
            {
                var h = _enemies.HeroPos; if (!h.HasValue) return;
                foreach (var c in _clones)
                {
                    if (!(Get(c.Go, "transform") is Transform tr)) continue;
                    var p = tr.position;
                    if (c.Last.HasValue) c.MaxErr = Math.Max(c.MaxErr, Math.Sqrt(Math.Pow(p.x - c.Last.Value.x, 2) + Math.Pow(p.y - c.Last.Value.y, 2)));
                    double x = h.Value.x + c.Dx, y = h.Value.y + c.Dy;
                    tr.position = new Vector3((float)x, (float)y, p.z);
                    c.Last = (x, y);
                }
            }
            catch (Exception e) { if (_uiFails++ < 5) W("копии, кадр: " + e.Message); }
        }

        private void ClonesCheck(string when)
        {
            W($"  --- {when}: наибольший уход от поставленной точки за кадр (игра сама двигает копию, если > 0)");
            foreach (var c in _clones) W($"    {c.What}: {c.MaxErr:0.000}, активна {Get(c.Go, "activeSelf")}");
            Flush();
        }

        private void ClonesFreeCheck()
        {
            W("  --- оставлены на месте 3 с: насколько сдвинулись сами");
            foreach (var c in _clones)
            {
                var p = PosOf(c.Go);
                W($"    {c.What}: {(p.HasValue && c.FreeStart.HasValue ? Math.Sqrt(Math.Pow(p.Value.x - c.FreeStart.Value.x, 2) + Math.Pow(p.Value.y - c.FreeStart.Value.y, 2)).ToString("0.00") : "?")}, активна {Get(c.Go, "activeSelf")}");
            }
            Flush();
        }

        private void ClonesEnd()
        {
            var objType = typeof(Transform).BaseType?.BaseType;
            var destroy = objType?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "Destroy" && m.GetParameters().Length == 1);
            int n = 0;
            foreach (var c in _clones)
            {
                try { destroy?.Invoke(null, new[] { c.Go }); n++; } catch (Exception e) { W("  удаление: " + e.Message); }
            }
            W($"  копии удалены: {n} из {_clones.Count}");
            _clones.Clear();
            Flush();
        }

        // ------------------------------------------------------------ структура и снимки

        private void DumpStructure()
        {
            var hero = Hero();
            if (hero == null) { W("героя нет"); return; }
            var t = hero.GetType();
            W(""); W($"##### ГЕРОЙ: {t.FullName} : {t.BaseType?.FullName}");
            var props = t.GetProperties(Any).Where(p => !Junk(p.Name) && p.GetIndexParameters().Length == 0).OrderBy(p => p.Name).ToList();
            W($"--- всего полей {props.Count}; ниже — с Shield/Spirit/Satellite/Doppel/… в имени и все объекты (GameObject/Transform/массивы объектов)");
            foreach (var p in props)
            {
                var tn = TypeName(p.PropertyType);
                bool obj = tn.Contains("GameObject") || tn.Contains("Transform") || tn.Contains("SpriteRenderer") || tn.Contains("Collider");
                if (Interesting.IsMatch(p.Name) || obj) W($"  {tn} {p.Name}{(p.GetGetMethod(true)?.IsStatic == true ? " [static]" : "")}");
            }
            var methods = t.GetMethods(Any | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName && !Junk(m.Name) && Interesting.IsMatch(m.Name)).OrderBy(m => m.Name).ToList();
            W($"--- методы с Shield/Spirit/Satellite/Doppel/… ({methods.Count})");
            foreach (var m in methods) W($"  {TypeName(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(a => TypeName(a.ParameterType) + " " + a.Name))})");
            Flush();
        }

        private void Snapshot()
        {
            var hero = Hero();
            if (hero == null) return;
            _snaps++;
            var hp = _enemies.HeroPos;
            W(""); W($"===== снимок {_snaps} на {GameT:0} с; заклинания (ID:уровень): {SpellsText()} =====");
            foreach (var p in hero.GetType().GetProperties(Any).Where(p => !Junk(p.Name) && p.GetIndexParameters().Length == 0 && Interesting.IsMatch(p.Name)).OrderBy(p => p.Name))
            {
                object v;
                try { v = p.GetValue(p.GetGetMethod(true)?.IsStatic == true ? null : hero); } catch (Exception e) { W($"  {p.Name}: {e.GetType().Name}"); continue; }
                string d;
                try { d = Describe(v, hp, 0); } catch (Exception e) { d = "ошибка чтения: " + ((e as TargetInvocationException)?.InnerException ?? e).Message; }
                if (d != null) W($"  {p.Name} = {d}");
            }
            W("--- источники снарядов героя (имя объекта: всего, по типам снаряда):");
            foreach (var s in _sources.Values.OrderByDescending(x => x.n)) W($"  {s.name}: {s.n} ({string.Join(", ", s.ids.OrderBy(k => k.Key).Select(k => $"{k.Key}×{k.Value}"))})");
            Flush();
        }

        /// <summary>Короткое описание значения: числа, флаги, объекты (активен, где относительно героя), коллекции (размер и элементы).</summary>
        private string Describe(object v, Vector3? hero, int depth)
        {
            if (v == null) return "null";
            var t = v.GetType();
            if (t.IsPrimitive || v is string || t.IsEnum) return v is float f ? f.ToString("0.###") : v.ToString();
            if (t.Name == "Vector3" || t.Name == "Vector2" || t.Name == "Color")
                return "(" + string.Join(", ", t.GetFields().Where(x => !x.IsStatic).Select(x => Convert.ToDouble(x.GetValue(v)).ToString("0.##"))) + ")";
            if (v is GameObject)
            {
                var tr = Get(v, "transform");
                return $"объект «{Get(v, "name")}» активен {Get(v, "activeSelf")}{Where(tr, hero)}{Children(tr)}";
            }
            if (v is Transform)
            {
                var go = Get(v, "gameObject");
                return $"transform «{Get(v, "name")}»{(go != null ? $" активен {Get(go, "activeSelf")}" : "")}{Where(v, hero)}{Children(v)}";
            }
            if (v is SpriteRenderer) { try { return $"картинка «{Get(Get(v, "sprite"), "name")}»"; } catch { return "картинка"; } }   // enabled не читать: icall Renderer::get_enabled не разрешается
            if (depth > 0) return t.Name;
            // коллекции: массивы Il2Cpp (Length + индексатор) и списки (Count + get_Item)
            var count = Get(v, "Length") ?? Get(v, "Count");
            if (count != null)
            {
                int n = Convert.ToInt32(count);
                var item = t.GetMethod("get_Item", new[] { typeof(int) });
                if (item == null && v is IList il)
                    return $"{TypeName(t)} [{n}]: " + string.Join("; ", Enumerable.Range(0, Math.Min(n, 12)).Select(i => { try { return Describe(il[i], hero, 1); } catch (Exception e) { return e.GetType().Name; } }));
                if (item == null) return $"{TypeName(t)} [{n}]";
                var parts = new List<string>();
                for (int i = 0; i < Math.Min(n, 12); i++)
                {
                    try { parts.Add(Describe(item.Invoke(v, new object[] { i }), hero, 1)); } catch (Exception e) { parts.Add(((e as TargetInvocationException)?.InnerException ?? e).GetType().Name); }
                }
                return $"{TypeName(t)} [{n}]: " + string.Join("; ", parts);
            }
            return null;   // прочие ссылки (компоненты, корутины) — пропускаем
        }

        private static string Where(object tr, Vector3? hero)
        {
            try
            {
                if (!(tr is Transform t) || !hero.HasValue) return "";
                var p = t.position;
                return $", от героя ({p.x - hero.Value.x:0.00}, {p.y - hero.Value.y:0.00})";
            }
            catch { return ""; }
        }
        private static string Children(object tr)
        {
            try { var c = Get(tr, "childCount"); return c != null && Convert.ToInt32(c) > 0 ? $", детей {c}" : ""; } catch { return ""; }
        }

        public void Finish(string why)
        {
            if (_done) return;
            _done = true;
            try { if (_inBattle) Snapshot(); } catch { }
            W(""); W($"===== разведка героя завершена: {why}; снимков {_snaps} =====");
            Flush();
            _boardText = "Разведка завершена — пришлите coop_probe.txt и modkit.log";
            ShowBoard();
            _ctx.Log($"РАЗВЕДКА ГЕРОЯ ЗАВЕРШЕНА ({why}). Пришлите coop_probe.txt и modkit.log из UserData/ModKit");
            try { _ctx.Timer.After(8, DestroyBoard); } catch { }
        }
    }
}
