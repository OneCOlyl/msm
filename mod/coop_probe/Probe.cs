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
    /// Разведка 5 — Армагеддон: что происходит в игре, когда он срабатывает, — чтобы показать его у друга.
    /// Снарядов (Bullet_Create) у него, похоже, нет. Ничего не меняет в игре, только записывает:
    ///   • поля и методы героя и менеджера боя с Armageddon/… в имени, их значения;
    ///   • какие объекты героя и менеджера включаются и выключаются (с временем) после того, как взят Армагеддон;
    ///   • какие новые картинки появляются на сцене (имена объектов) и какие новые снаряды — с временем;
    ///   • вызовы методов с Armageddon в имени (если они есть и не корутины).
    /// Отчёт — UserData/ModKit/coop_probe.txt. Прежние разведки — docs/ENEMY_SYNC.md, docs/ORBITALS.md.
    /// </summary>
    public class ProbeMod : IKitMod
    {
        public ModDef Define() => new ModDef
        {
            Id = "coop_probe",
            Name = "Кооператив: разведка",
            Version = "0.6.1",
            Description = "Разведка Армагеддона: что включается в игре при его взрыве — чтобы показать его у друга. Ничего не меняет, только записывает. Потом выключите.",
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
        private static readonly Regex Interesting = new Regex("Armageddon|Apocal|Doom|Judgment|Nuke|Bomb|Explosion|Electric", RegexOptions.IgnoreCase);
        private const int ArmaId = 14;
        private const int BlastsWanted = 2;          // взрывов поймать, потом ещё AfterBlastSec — и конец
        private const double AfterBlastSec = 10;
        private const double MaxWatchSec = 330;      // перезарядка на 1-м уровне ~105 с, два взрыва ≈ 3,5 мин

        /// <summary>Только для тестов на ПК (через рефлексию).</summary>
        private static string DirOverride;
        private static Func<object> HeroOverride;

        private readonly ModContext _ctx;
        private readonly IEnemies _enemies;
        private readonly ISpells _spells;
        private readonly StringBuilder _out = new StringBuilder();
        private readonly string _path;

        private bool _inBattle, _done, _dumped;
        private double _t0 = -1, _armaAt = -1, _nextScene, _snapAt = -1;
        private UiKit.HudBar _board;
        private string _boardText = "";
        private int _uiFails, _toggles, _newImages, _newBullets, _calls;
        private object _manager;
        private readonly Dictionary<object, (string name, int n, Dictionary<int, int> ids)> _sources = new Dictionary<object, (string, int, Dictionary<int, int>)>();
        private object _heroTrKey;
        private readonly HashSet<int> _bulletIds = new HashSet<int>();
        private readonly HashSet<string> _images = new HashSet<string>();
        private sealed class Watched { public string Label; public object Go; public bool? Last; public int Flips; }
        private readonly List<Watched> _watch = new List<Watched>();
        private readonly Dictionary<string, string> _armaValues = new Dictionary<string, string>();
        private double _delayLast = double.NaN, _lastBlastAt = -1;
        private int _blasts;

        public Probe(ModContext ctx)
        {
            _ctx = ctx;
            _enemies = ctx.Use<IEnemies>("modkit_combat");
            _spells = ctx.Use<ISpells>("modkit_spells");
            string dir = DirOverride;
            if (dir == null) { try { dir = Paths.Dir; } catch { dir = "/sdcard/MelonLoader/com.vkslrzm.Zombix/UserData/ModKit"; } }
            _path = Path.Combine(dir, "coop_probe.txt");
            W($"=== coop_probe 0.6.1 (разведка Армагеддона), {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            ctx.Log("разведка Армагеддона включена: начните бой и возьмите Армагеддон (можно читом)");

            ctx.Before("C_Battle_AllyUnit", "Bullet_Create", 13, (s, a) => OnBullet(a));
            ctx.Before("C_Battle_Manager", "Update", 0, (s, a) => { if (s != null) _manager = s; });
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
        private string Ts => $"[{GameT,6:0.00} с]";

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

        private static object Item(object coll, int i)
        {
            if (coll == null) return null;
            var m = coll.GetType().GetMethod("get_Item", new[] { typeof(int) });
            if (m != null) return m.Invoke(coll, new object[] { i });
            return coll is IList l && l.Count > i ? l[i] : null;
        }

        // ------------------------------------------------------------ события

        private void OnBattleStart()
        {
            if (_done) return;
            _inBattle = true; _t0 = _ctx.Timer.Now;
            W(""); W("===== бой начался =====");
            Flush();
        }

        private void OnBullet(object[] a)
        {
            if (!_inBattle || _done || a == null || a.Length < 2) return;
            try
            {
                int id = Convert.ToInt32(a[1]);
                if (_bulletIds.Add(id) && _armaAt >= 0)
                {
                    _newBullets++;
                    string name = "?";
                    try { name = Get(a[0], "name") as string ?? "?"; } catch { }
                    W($"{Ts} НОВЫЙ снаряд {id} (впервые после Армагеддона), источник «{name}»");
                }
                var src = a[0]; var key = Ptr(src);
                if (key == null) return;
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
            if (!_dumped && t >= 2) { _dumped = true; _ctx.Guard("структура", DumpStructure); _ctx.Guard("список объектов", BuildWatch); }
            if (!_dumped) return;
            bool arma = SafeLevel(ArmaId) > 0;
            if (arma && _armaAt < 0)
            {
                _armaAt = t;
                W(""); W($"{Ts} ===== Армагеддон взят (уровень {SafeLevel(ArmaId)}); дальше — всё, что включается и появляется =====");
                _ctx.Guard("значения", () => ArmaValues(true));
                Flush();
            }
            _ctx.Guard("объекты", WatchStep);
            if (t >= _nextScene) { _nextScene = t + 0.5; _ctx.Guard("сцена", SceneScan); }
            if (_armaAt >= 0) _ctx.Guard("значения", () => ArmaValues(false));
            if (_armaAt >= 0 && t - _armaAt >= 5 && _snapAt < 0) { _snapAt = t; _ctx.Guard("снимок", () => Snapshot("через 5 с после взятия")); }
            if (_armaAt >= 0) _ctx.Guard("перезарядка", () => DelayStep(t));
            if (_blasts >= BlastsWanted && t - _lastBlastAt >= AfterBlastSec) { Finish($"пойманы взрывы: {_blasts}"); return; }
            if (_armaAt >= 0 && t - _armaAt >= MaxWatchSec) { Finish("наблюдение закончено"); return; }
            if (_armaAt < 0)
                _boardText = "Разведка Армагеддона: возьмите Армагеддон (можно читом «ЧИТ» → Армагеддон) и не умирайте ~4 мин";
            else
                _boardText = $"Армагеддон есть — ждите взрывов: {_blasts}/{BlastsWanted}, до следующего ~{(double.IsNaN(_delayLast) ? "?" : _delayLast.ToString("0"))} с (предел {Math.Max(0, MaxWatchSec - (t - _armaAt)):0} с). Замечено: переключений {_toggles}, картинок {_newImages}, снарядов {_newBullets}, вызовов {_calls}";
            ShowBoard();
            if ((int)(t * 4) % 20 == 0) Flush();
        }

        /// <summary>Отсчёт до взрыва (менеджер.Apply_ArmageddonDelayNum, ~1 в секунду): каждые 10 — в отчёт, сброс у нуля — взрыв.</summary>
        private void DelayStep(double t)
        {
            var v = Get(_manager, "Apply_ArmageddonDelayNum");
            if (v == null) return;
            double d = Convert.ToDouble(v);
            if (!double.IsNaN(_delayLast))
            {
                if (d > _delayLast + 3)
                {
                    if (_delayLast <= 5) { _blasts++; _lastBlastAt = t; W($"{Ts} ===== ВЗРЫВ №{_blasts}: отсчёт {_delayLast:0.##} → {d:0.##} ====="); }
                    else W($"{Ts} отсчёт до Армагеддона вырос не у нуля: {_delayLast:0.##} → {d:0.##}");
                    Flush();
                }
                else if (Math.Floor(d / 10) != Math.Floor(_delayLast / 10)) W($"{Ts} до Армагеддона {d:0.#}");
            }
            _delayLast = d;
        }

        private int SafeLevel(int id) { try { return _spells.Level(id); } catch { return 0; } }

        // ------------------------------------------------------------ объекты героя и менеджера: включения

        private IEnumerable<(string owner, object obj)> Owners()
        {
            var h = Hero(); if (h != null) yield return ("герой", h);
            if (_manager != null) yield return ("менеджер", _manager);
        }

        private void BuildWatch()
        {
            _watch.Clear();
            foreach (var (owner, obj) in Owners())
                foreach (var p in obj.GetType().GetProperties(Any).Where(p => !Junk(p.Name) && p.GetIndexParameters().Length == 0))
                {
                    var tn = TypeName(p.PropertyType);
                    if (!tn.Contains("GameObject")) continue;
                    object v;
                    try { v = p.GetValue(p.GetGetMethod(true)?.IsStatic == true ? null : obj); } catch { continue; }
                    if (v == null) continue;
                    if (v is GameObject) { _watch.Add(new Watched { Label = $"{owner}.{p.Name}", Go = v }); continue; }
                    int n = 0;
                    try { n = Convert.ToInt32(Get(v, "Length") ?? Get(v, "Count") ?? 0); } catch { }
                    for (int i = 0; i < Math.Min(n, 16); i++)
                    {
                        object e = null;
                        try { e = Item(v, i); } catch { }
                        if (e is GameObject) _watch.Add(new Watched { Label = $"{owner}.{p.Name}[{i}]", Go = e });
                    }
                }
            W($"{Ts} наблюдаю объектов: {_watch.Count} (герой{(_manager != null ? " и менеджер боя" : "; менеджер боя ещё не пойман")})");
            Flush();
        }

        private void WatchStep()
        {
            if (_manager != null && !_watch.Any(w => w.Label.StartsWith("менеджер"))) BuildWatch();
            foreach (var w in _watch)
            {
                bool on;
                try { on = Convert.ToBoolean(Get(w.Go, "activeSelf") ?? false); } catch { continue; }
                if (w.Last.HasValue && w.Last.Value != on && _armaAt >= 0)
                {
                    w.Flips++; _toggles++;
                    if (w.Flips <= 10) W($"{Ts} {(on ? "ВКЛ " : "выкл")} {w.Label} «{Get(w.Go, "name")}»{(on ? Where(Get(w.Go, "transform"), _enemies.HeroPos) + Children(Get(w.Go, "transform")) : "")}");
                    else if (w.Flips == 11) W($"{Ts} {w.Label}: мигает часто, дальше не пишу");
                }
                w.Last = on;
            }
        }

        // ------------------------------------------------------------ новые картинки на сцене

        private MethodInfo _findOfType; private object _srType; private bool _sceneFailed;

        private void SceneScan()
        {
            if (_sceneFailed) return;
            try
            {
                if (_findOfType == null)
                {
                    var ofType = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("Il2CppInterop.Runtime.Il2CppType")).FirstOrDefault(t => t != null);
                    var of = ofType?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "Of" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                    if (of == null) { _sceneFailed = true; W("сцена: нет Il2CppType.Of — поиск картинок по сцене пропущен"); return; }
                    _srType = of.MakeGenericMethod(typeof(SpriteRenderer)).Invoke(null, null);
                    var objType = typeof(Transform).BaseType?.BaseType;
                    _findOfType = objType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "FindObjectsOfType" && !m.IsGenericMethod && m.GetParameters().Length == 1);
                    if (_findOfType == null) { _sceneFailed = true; W("сцена: нет Object.FindObjectsOfType(Type)"); return; }
                }
                var arr = _findOfType.Invoke(null, new[] { _srType });
                int n = Convert.ToInt32(Get(arr, "Length") ?? 0);
                var fresh = new List<string>();
                for (int i = 0; i < n && i < 3000; i++)
                {
                    var o = Item(arr, i);
                    var nm = Get(o, "name") as string;
                    if (string.IsNullOrEmpty(nm)) continue;
                    nm = Regex.Replace(nm, @"\(Clone\)", "").Trim();
                    if (_images.Add(nm) && _armaAt >= 0) fresh.Add(nm);
                }
                if (fresh.Count > 0) { _newImages += fresh.Count; W($"{Ts} НОВЫЕ объекты с картинкой на сцене ({n} всего): {string.Join(", ", fresh.Take(20))}"); }
            }
            catch (Exception e) { _sceneFailed = true; W("сцена: " + ((e as TargetInvocationException)?.InnerException ?? e).Message); }
        }

        // ------------------------------------------------------------ поля и методы с Armageddon

        private void DumpStructure()
        {
            foreach (var (owner, obj) in Owners().Concat(StaticOwner()))
            {
                var t = obj as Type ?? obj.GetType();
                var props = t.GetProperties(Any).Where(p => !Junk(p.Name) && p.GetIndexParameters().Length == 0 && Interesting.IsMatch(p.Name)).OrderBy(p => p.Name).ToList();
                var methods = t.GetMethods(Any | BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName && !Junk(m.Name) && Interesting.IsMatch(m.Name)).OrderBy(m => m.Name).ToList();
                W(""); W($"##### {owner}: {t.FullName} — полей с Armageddon/… {props.Count}, методов {methods.Count}");
                foreach (var p in props) W($"  {TypeName(p.PropertyType)} {p.Name}{(p.GetGetMethod(true)?.IsStatic == true ? " [static]" : "")}");
                foreach (var m in methods) W($"  {TypeName(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(a => TypeName(a.ParameterType) + " " + a.Name))})");
                if (obj is Type) continue;
                // вызовы методов и запуски корутин (IEnumerator создаётся при StartCoroutine) — с временем; Армагеддон первым
                foreach (var m in methods.Where(m => m.ReturnType == typeof(void) || m.ReturnType.Name == "IEnumerator")
                                         .OrderBy(m => m.Name.Contains("Armageddon") ? 0 : 1).Take(16))
                {
                    var name = m.Name; int argc = m.GetParameters().Length; var cls = t.Name; var who = owner;
                    try { _ctx.Before(cls, name, argc, (s, a) => { if (_calls++ < 60) W($"{Ts} ВЫЗОВ {who}.{name}({argc})"); }); W($"  (слежу за вызовами {name})"); }
                    catch (Exception e) { W($"  хук {name}: {e.Message}"); }
                }
            }
            Flush();
        }

        private IEnumerable<(string owner, object obj)> StaticOwner()
        {
            Type mgr = null;
            try { mgr = _manager == null ? Hooks.FindType("C_Battle_Manager") : null; } catch { }
            if (mgr != null) yield return ("менеджер боя (статические)", mgr);
        }

        /// <summary>Значения полей с Armageddon: при взятии — все, дальше — только изменившиеся флаги и целые числа.</summary>
        private void ArmaValues(bool all)
        {
            foreach (var (owner, obj) in Owners())
                foreach (var p in obj.GetType().GetProperties(Any).Where(p => !Junk(p.Name) && p.GetIndexParameters().Length == 0 && Interesting.IsMatch(p.Name)))
                {
                    var pt = p.PropertyType;
                    if (!all && pt != typeof(bool) && pt != typeof(int)) continue;
                    object v;
                    try { v = p.GetValue(p.GetGetMethod(true)?.IsStatic == true ? null : obj); } catch { continue; }
                    string d;
                    try { d = Describe(v, _enemies.HeroPos, 0); } catch { d = "?"; }
                    if (d == null) continue;
                    var key = owner + "." + p.Name;
                    if (all) { _armaValues[key] = d; W($"  {key} = {d}"); continue; }
                    if (_armaValues.TryGetValue(key, out var old) && old != d) W($"{Ts} {key}: {old} → {d}");
                    _armaValues[key] = d;
                }
        }

        private void Snapshot(string when)
        {
            W(""); W($"{Ts} ===== снимок ({when}) =====");
            ArmaValues(true);
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
            try { if (_inBattle && _armaAt >= 0) Snapshot("в конце"); } catch { }
            W("--- источники снарядов героя (имя объекта: всего, по типам снаряда):");
            foreach (var s in _sources.Values.OrderByDescending(x => x.n)) W($"  {s.name}: {s.n} ({string.Join(", ", s.ids.OrderBy(k => k.Key).Select(k => $"{k.Key}×{k.Value}"))})");
            W(""); W($"===== разведка завершена: {why}; Армагеддон {(_armaAt >= 0 ? $"взят на {_armaAt:0} с" : "НЕ взят")} =====");
            Flush();
            _boardText = "Разведка завершена — пришлите coop_probe.txt и modkit.log";
            ShowBoard();
            _ctx.Log($"РАЗВЕДКА АРМАГЕДДОНА ЗАВЕРШЕНА ({why}). Пришлите coop_probe.txt и modkit.log из UserData/ModKit");
            try { _ctx.Timer.After(8, DestroyBoard); } catch { }
        }
    }
}
