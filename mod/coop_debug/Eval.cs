using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace MagicSurvivalCoop.Debug
{
    /// <summary>
    /// Разбор и выполнение команд консоли: путь «корень.член.член[индекс]» по рефлексии (обычные и статические члены).
    /// От игры не зависит (корни, имя картинки и проверка «уничтожен» приходят снаружи) — проверяется на ПК.
    /// </summary>
    public sealed class Evaluator
    {
        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Stat = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        /// <summary>Корень по имени: объект или Type (тогда члены — статические). null — нет такого.</summary>
        public Func<string, object> Root = _ => null;
        /// <summary>Имя картинки рендера — без обёртки Sprite (она роняет игру под Mono).</summary>
        public Func<object, string> SpriteName = _ => null;
        /// <summary>Объект Unity уничтожен (прямое обращение к нему уронит игру).</summary>
        public Func<object, bool> IsDestroyed = _ => false;
        /// <summary>Команды мода (hook, iters…): null — не его команда.</summary>
        public Func<string, string[], string> Extra = (c, a) => null;

        private object _last;

        public string Run(string line)
        {
            line = (line ?? "").Trim();
            if (line.Length == 0) return "";
            var parts = Split(line);
            string cmd = parts[0].ToLowerInvariant();
            var args = parts.Skip(1).ToArray();
            try
            {
                var extra = Extra(cmd, args);
                if (extra != null) return extra;
                switch (cmd)
                {
                    case "help": return Help;
                    case "get": Need(args, 1); return Describe(_last = Resolve(args[0]), 0);
                    case "ls": Need(args, 1); return List(Resolve(args[0]), args.Length > 1 ? args[1] : null, false);
                    case "lsall": Need(args, 1); return List(Resolve(args[0]), args.Length > 1 ? args[1] : null, true);
                    case "methods": Need(args, 1); return Methods(Resolve(args[0]), args.Length > 1 ? args[1] : null);
                    case "nested": Need(args, 1); return Nested(Resolve(args[0]), args.Length > 1 ? args[1] : null);
                    case "set": Need(args, 2); return Set(args[0], string.Join(" ", args.Skip(1)));
                    case "call": Need(args, 1); return Call(args[0], args.Skip(1).ToArray());
                    default: return "неизвестная команда «" + cmd + "». " + Help;
                }
            }
            catch (Exception e) { var x = (e as TargetInvocationException)?.InnerException ?? e; return "ошибка: " + x.GetType().Name + ": " + x.Message; }
        }

        public const string Help =
            "команды: get <путь> | ls <путь> [regex] (простые значения) | lsall <путь> [regex] (все члены) | methods <путь> [regex] | " +
            "nested <путь> [regex] (вложенные типы, корутины) | set <путь> <значение> | call <путь.Метод> [арг…] | hook <Тип> <regex вложенного> | iters | find <regex> (объекты сцены → found[i]). " +
            "Корни: hero, mgr, abil, db (Z_Database), T:<Тип игры>, U:<Тип Unity> (статические члены), iter:<имя>, found, last. Путь: корень.Член.Член[0]";

        private static void Need(string[] a, int n) { if (a.Length < n) throw new ArgumentException("мало аргументов"); }

        private static string[] Split(string line)
        {
            var res = new List<string>(); var sb = new StringBuilder(); bool q = false;
            foreach (var ch in line)
            {
                if (ch == '"') { q = !q; continue; }
                if (ch == ' ' && !q) { if (sb.Length > 0) { res.Add(sb.ToString()); sb.Clear(); } continue; }
                sb.Append(ch);
            }
            if (sb.Length > 0) res.Add(sb.ToString());
            return res.ToArray();
        }

        // ------------------------------------------------------------ путь

        private static readonly Regex Seg = new Regex(@"^([^\[\]]+)((\[\d+\])*)$");

        public object Resolve(string path)
        {
            var segs = SplitPath(path);
            object cur = segs[0] == "last" ? _last : Root(segs[0]);
            if (cur == null) throw new ArgumentException("нет корня «" + segs[0] + "» (или он ещё не пойман)");
            for (int i = 1; i < segs.Count; i++) cur = Step(cur, segs[i]);
            return cur;
        }

        /// <summary>Разбить путь по точкам. Корень T:Тип с точками в имени (Il2Cpp.X) отделяется «/»: T:Il2Cpp.X/Член.Член.</summary>
        private static List<string> SplitPath(string path)
        {
            if (path.StartsWith("T:") && path.Contains("/"))
            {
                int k = path.IndexOf('/');
                var rest = path.Substring(k + 1);
                var l = new List<string> { path.Substring(0, k) };
                if (rest.Length > 0) l.AddRange(rest.Split('.'));
                return l;
            }
            var all = path.Split('.').ToList();
            return all;
        }

        private object Step(object cur, string seg)
        {
            var m = Seg.Match(seg);
            if (!m.Success) throw new ArgumentException("плохой кусок пути «" + seg + "»");
            var v = Member(cur, m.Groups[1].Value);
            foreach (Match ix in Regex.Matches(m.Groups[2].Value, @"\[(\d+)\]"))
                v = Index(v, int.Parse(ix.Groups[1].Value));
            return v;
        }

        public object Member(object cur, string name)
        {
            if (cur == null) throw new NullReferenceException("перед «" + name + "» — null");
            if (cur is Type) { }
            else if (IsDestroyed(cur)) throw new InvalidOperationException("объект уничтожен");
            bool isType = cur is Type;
            var t = isType ? (Type)cur : cur.GetType();
            var flags = isType ? Stat : Inst | Stat;
            for (var tt = t; tt != null; tt = tt.BaseType)
            {
                var p = tt.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (p != null && p.GetIndexParameters().Length == 0) { var g = p.GetGetMethod(true); return p.GetValue(g != null && g.IsStatic ? null : cur); }
                var f = tt.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (f != null) return f.GetValue(f.IsStatic ? null : cur);
            }
            throw new MissingMemberException(t.Name + " не имеет «" + name + "»");
        }

        private static object Index(object coll, int i)
        {
            if (coll == null) throw new NullReferenceException("индекс у null");
            var gi = coll.GetType().GetMethod("get_Item", new[] { typeof(int) });
            if (gi != null) return gi.Invoke(coll, new object[] { i });
            if (coll is IList l) return l[i];
            throw new InvalidOperationException(coll.GetType().Name + " не коллекция");
        }

        private static int? CountOf(object v)
        {
            if (v == null || v is string) return null;
            var t = v.GetType();
            foreach (var n in new[] { "Count", "Length" })
            {
                var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.PropertyType == typeof(int) && p.GetIndexParameters().Length == 0) { try { return (int)p.GetValue(v); } catch { return null; } }
            }
            return v is ICollection c ? c.Count : (int?)null;
        }

        // ------------------------------------------------------------ вывод

        public string Describe(object v, int depth)
        {
            if (v == null) return "null";
            if (v is Type ty) return "тип " + ty.FullName;
            var t = v.GetType();
            if (t.IsPrimitive || v is string || t.IsEnum || v is decimal) return Convert.ToString(v, CultureInfo.InvariantCulture);
            if (v is IntPtr ip) return "0x" + ip.ToString("x");
            if (t.Name == "Vector3" || t.Name == "Vector2" || t.Name == "Color" || t.Name == "Quaternion")
                return "(" + string.Join(", ", t.GetFields(BindingFlags.Public | BindingFlags.Instance).Select(f => Convert.ToDouble(f.GetValue(v)).ToString("0.###", CultureInfo.InvariantCulture))) + ")";
            if (t.Name == "Sprite") return "Sprite (обёртку не читаю — роняет игру)";
            if (IsDestroyed(v)) return t.Name + " (уничтожен)";
            if (t.Name == "SpriteRenderer") return $"SpriteRenderer, картинка «{SpriteName(v)}»";
            if (t.Name == "GameObject") return $"GameObject «{Safe(() => Member(v, "name"))}» activeSelf {Safe(() => Member(v, "activeSelf"))}, детей {Safe(() => Member(Member(v, "transform"), "childCount"))}";
            if (t.Name == "Transform" || t.Name == "RectTransform")
                return $"{t.Name} «{Safe(() => Member(v, "name"))}» pos {Describe(SafeObj(() => Member(v, "position")), 1)} scale {Describe(SafeObj(() => Member(v, "localScale")), 1)} rot {Describe(SafeObj(() => Member(v, "localEulerAngles")), 1)}, детей {Safe(() => Member(v, "childCount"))}";
            var n = CountOf(v);
            if (n != null)
            {
                if (depth > 0) return $"{Short(t)} [{n}]";
                var items = new List<string>();
                for (int i = 0; i < Math.Min(n.Value, 40); i++) items.Add($"  [{i}] " + Describe(SafeObj(() => Index(v, i)), 1));
                return $"{Short(t)} [{n}]" + (items.Count > 0 ? "\n" + string.Join("\n", items) : "");
            }
            string nm = null;
            try { nm = t.GetProperty("name", BindingFlags.Public | BindingFlags.Instance)?.GetValue(v) as string; } catch { }
            return Short(t) + (nm != null ? $" «{nm}»" : "");
        }

        private static string Short(Type t) => !t.IsGenericType ? t.Name : t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(Short)) + ">";
        private static string Safe(Func<object> f) { try { return Convert.ToString(f(), CultureInfo.InvariantCulture); } catch { return "?"; } }
        private static object SafeObj(Func<object> f) { try { return f(); } catch (Exception e) { return "ошибка: " + ((e as TargetInvocationException)?.InnerException ?? e).Message; } }

        private static bool Junk(string n) => n.StartsWith("NativeFieldInfoPtr") || n.StartsWith("NativeMethodInfoPtr") || n == "NativeClassPtr" || n.StartsWith("Il2CppStaticField") || n == "ObjectClass" || n == "Pointer" || n == "WasCollected";

        private string List(object o, string filter, bool all)
        {
            var rx = filter == null ? null : new Regex(filter, RegexOptions.IgnoreCase);
            bool isType = o is Type;
            var t = isType ? (Type)o : o.GetType();
            var flags = (isType ? Stat : Inst | Stat);
            var lines = new List<string>();
            var seen = new HashSet<string>();
            foreach (var p in t.GetProperties(flags))
            {
                if (p.GetIndexParameters().Length > 0 || Junk(p.Name) || !seen.Add(p.Name) || (rx != null && !rx.IsMatch(p.Name))) continue;
                var pt = p.PropertyType;
                bool simple = pt.IsPrimitive || pt == typeof(string) || pt.IsEnum || pt.Name == "Vector3" || pt.Name == "Vector2" || pt.Name == "Color";
                bool st = p.GetGetMethod(true)?.IsStatic == true;
                if (!simple && !all) continue;
                string val = simple || all ? Describe(SafeObj(() => p.GetValue(st ? null : o)), 1) : "";
                lines.Add($"{Short(pt)} {p.Name}{(st ? " [static]" : "")} = {val}");
            }
            foreach (var f in t.GetFields(flags))
            {
                if (Junk(f.Name) || !seen.Add(f.Name) || (rx != null && !rx.IsMatch(f.Name))) continue;
                var ft = f.FieldType;
                bool simple = ft.IsPrimitive || ft == typeof(string) || ft.IsEnum;
                if (!simple && !all) continue;
                lines.Add($"{Short(ft)} {f.Name}{(f.IsStatic ? " [static]" : "")} = {Describe(SafeObj(() => f.GetValue(f.IsStatic ? null : o)), 1)}");
            }
            lines.Sort(StringComparer.Ordinal);
            return $"{t.FullName}: {lines.Count}\n" + string.Join("\n", lines.Take(400));
        }

        private static string Methods(object o, string filter)
        {
            var rx = filter == null ? null : new Regex(filter, RegexOptions.IgnoreCase);
            var t = o as Type ?? o.GetType();
            var ms = t.GetMethods(Inst | Stat).Where(m => !m.IsSpecialName && !m.Name.StartsWith("Native") && (rx == null || rx.IsMatch(m.Name)))
                      .Select(m => $"{Short(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(a => Short(a.ParameterType) + " " + a.Name))}){(m.IsStatic ? " [static]" : "")}")
                      .Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            return $"{t.FullName}: {ms.Count}\n" + string.Join("\n", ms.Take(400));
        }

        private static string Nested(object o, string filter)
        {
            var rx = filter == null ? null : new Regex(filter, RegexOptions.IgnoreCase);
            var t = o as Type ?? o.GetType();
            var ns = t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic).Where(n => rx == null || rx.IsMatch(n.Name)).Select(n => n.Name).OrderBy(x => x).ToList();
            return $"{t.FullName}: вложенных {ns.Count}\n" + string.Join("\n", ns);
        }

        // ------------------------------------------------------------ изменения

        private string Set(string path, string value)
        {
            int dot = path.LastIndexOf('.');
            if (dot < 0) throw new ArgumentException("путь до члена: корень.Член");
            var owner = Resolve(path.Substring(0, dot));
            string name = path.Substring(dot + 1);
            bool isType = owner is Type;
            var t = isType ? (Type)owner : owner.GetType();
            for (var tt = t; tt != null; tt = tt.BaseType)
            {
                var p = tt.GetProperty(name, (isType ? Stat : Inst | Stat) | BindingFlags.DeclaredOnly);
                if (p != null && p.CanWrite)
                {
                    var old = SafeObj(() => p.GetValue(p.GetGetMethod(true)?.IsStatic == true ? null : owner));
                    p.SetValue(p.GetSetMethod(true)?.IsStatic == true ? null : owner, Parse(value, p.PropertyType));
                    return $"{name}: {Describe(old, 1)} → {Describe(SafeObj(() => p.GetValue(p.GetGetMethod(true)?.IsStatic == true ? null : owner)), 1)}";
                }
                var f = tt.GetField(name, (isType ? Stat : Inst | Stat) | BindingFlags.DeclaredOnly);
                if (f != null)
                {
                    var old = f.GetValue(f.IsStatic ? null : owner);
                    f.SetValue(f.IsStatic ? null : owner, Parse(value, f.FieldType));
                    return $"{name}: {Describe(old, 1)} → {Describe(f.GetValue(f.IsStatic ? null : owner), 1)}";
                }
            }
            throw new MissingMemberException(t.Name + " не имеет изменяемого «" + name + "»");
        }

        private static object Parse(string s, Type t)
        {
            if (t == typeof(string)) return s;
            if (t == typeof(bool)) return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase);
            if (t.IsEnum) return Enum.Parse(t, s, true);
            return Convert.ChangeType(double.Parse(s, CultureInfo.InvariantCulture), t, CultureInfo.InvariantCulture);
        }

        private string Call(string path, string[] args)
        {
            int dot = path.LastIndexOf('.');
            if (dot < 0) throw new ArgumentException("путь до метода: корень.Метод");
            var owner = Resolve(path.Substring(0, dot));
            string name = path.Substring(dot + 1);
            bool isType = owner is Type;
            var t = isType ? (Type)owner : owner.GetType();
            var m = t.GetMethods(isType ? Stat : Inst | Stat).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length && x.GetParameters().All(a => a.ParameterType.IsPrimitive || a.ParameterType == typeof(string) || a.ParameterType.IsEnum))
                    ?? throw new MissingMethodException($"{t.Name}.{name} с {args.Length} простыми аргументами не найден");
            var ps = m.GetParameters();
            var conv = args.Select((a, i) => Parse(a, ps[i].ParameterType)).ToArray();
            var r = m.Invoke(m.IsStatic ? null : owner, conv);
            _last = r;
            return m.ReturnType == typeof(void) ? "вызвано" : "результат: " + Describe(r, 0);
        }
    }
}
