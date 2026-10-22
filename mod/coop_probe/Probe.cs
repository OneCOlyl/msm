using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ModKit;

namespace MagicSurvivalCoop.Probe
{
    /// <summary>
    /// Разведчик для синхронизации врагов. Сам по себе ничего не даёт игроку: записывает, как игра создаёт и двигает врагов,
    /// и проводит три коротких опыта в первом бою после включения:
    ///   1. 0–60 с   — наблюдение: каждый новый враг (тип, параметры, место), вызовы спавна менеджера боя, счётчики;
    ///   2. 60–90 с  — выключение спавна игры (Create_UnitEnemy_Stop) на 15 с и попытка включить его обратно;
    ///   3. 90–110 с — «марионетки»: заморозка/оглушение врагов и ручной сдвиг — остаются ли они там, куда их поставили.
    /// Итог — UserData/ModKit/coop_probe.txt (подробно) и строки [coop_probe] в modkit.log (кратко).
    /// </summary>
    public class ProbeMod : IKitMod
    {
        public ModDef Define() => new ModDef
        {
            Id = "coop_probe",
            Name = "Кооператив: разведка врагов",
            Version = "0.1.0",
            Description = "Диагностика для синхронизации врагов: записывает, как игра создаёт и двигает врагов (один бой ~2 минуты). Потом выключите.",
            Depends = { ["modkit_combat"] = ">=1.0" },
            OnEnable = ctx => _p = new Probe(ctx),
            OnDisable = ctx => { _p?.Finish("мод выключен"); _p = null; },
        };
        private Probe _p;
    }

    internal sealed class Probe
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private readonly ModContext _ctx;
        private readonly IEnemies _enemies;
        private readonly ICombat _combat;
        private readonly StringBuilder _out = new StringBuilder();
        private readonly string _path;
        private readonly Stopwatch _sw = Stopwatch.StartNew();

        private object _mgr;
        private Type _mgrType, _enemyType;
        private bool _inBattle, _done, _dumped;
        private double _battleT0 = -1;
        private int _frames, _spawnsTotal, _spawnsDetailed, _deaths, _stage;
        private readonly Dictionary<int, int> _spawnsByType = new Dictionary<int, int>();
        private readonly Dictionary<string, int> _mgrCalls = new Dictionary<string, int>();
        private int _spawnsInWindow;
        private double _nextSummary;
        private object _sampleEnemy;
        private double _sampleAt = -1;

        // опыт 3
        private readonly List<(IEnemy e, string how, double x0, double y0, double tx, double ty)> _puppets = new List<(IEnemy, string, double, double, double, double)>();

        public Probe(ModContext ctx)
        {
            _ctx = ctx;
            _enemies = ctx.Use<IEnemies>("modkit_combat");
            _combat = ctx.Use<ICombat>("modkit_combat");
            string dir = DirOverride;
            if (dir == null) { try { dir = Paths.Dir; } catch { dir = "/sdcard/MelonLoader/com.vkslrzm.Zombix/UserData/ModKit"; } }
            _path = Path.Combine(dir, "coop_probe.txt");
            W($"=== coop_probe 0.1.0, {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            ctx.Log("разведка включена: начните бой и играйте ~2 минуты; отчёт — " + _path);

            ctx.Guard("типы", FindTypes);
            ctx.Guard("хуки", InstallHooks);
            ctx.Events.On("battleStart", _ => ctx.Guard("начало боя", OnBattleStart));
            ctx.Events.On("battleEnd", _ => { if (_inBattle) Finish("бой закончился раньше"); _inBattle = false; });
            ctx.Events.On("enemyDeath", _ => _deaths++);
            ctx.Every(100, () => ctx.Guard("шаг", Step));
            ctx.OnCleanup(Flush);
        }

        // ------------------------------------------------------------ вывод

        private void W(string line) { _out.AppendLine(line); }

        private void Flush()
        {
            try { File.WriteAllText(_path, _out.ToString(), new UTF8Encoding(false)); }
            catch (Exception e) { _ctx.Log("не удалось записать отчёт: " + e.Message); }
        }

        /// <summary>Ускорение времени — только для тестов на ПК (через рефлексию).</summary>
        private static double TimeScale = 1;
        /// <summary>Папка отчёта — только для тестов на ПК.</summary>
        private static string DirOverride;
        private double Now => _sw.Elapsed.TotalSeconds * TimeScale;
        private double BattleT => _battleT0 < 0 ? 0 : Now - _battleT0;

        // ------------------------------------------------------------ типы и дамп

        private void FindTypes()
        {
            _mgrType = Hooks.FindType("C_Battle_Manager");
            _enemyType = Hooks.FindType("C_Battle_EnemyUnit");
            W($"C_Battle_Manager: {_mgrType?.FullName ?? "НЕ НАЙДЕН"}; C_Battle_EnemyUnit: {_enemyType?.FullName ?? "НЕ НАЙДЕН"}");
            _ctx.Log($"типы: менеджер {(_mgrType != null ? "есть" : "НЕТ")}, враг {(_enemyType != null ? "есть" : "НЕТ")}");
        }

        private static bool Junk(string n) =>
            n.StartsWith("NativeMethodInfoPtr") || n.StartsWith("NativeFieldInfoPtr") || n.StartsWith("Il2Cpp") || n.StartsWith("<") || n == "NativeClassPtr";

        private static string TypeName(Type t)
        {
            if (t == null) return "?";
            if (!t.IsGenericType) return t.Name;
            return t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(TypeName)) + ">";
        }

        private void DumpType(Type t, string title)
        {
            if (t == null) return;
            W("");
            W($"##### {title}: {t.FullName} : {t.BaseType?.FullName}");
            var props = t.GetProperties(All).Where(p => !Junk(p.Name)).OrderBy(p => p.Name).ToList();
            W($"--- поля/свойства ({props.Count})");
            foreach (var p in props) W($"  {TypeName(p.PropertyType)} {p.Name}{(p.GetGetMethod(true)?.IsStatic == true ? " [static]" : "")}");
            var methods = t.GetMethods(All).Where(m => !m.IsSpecialName && !Junk(m.Name)).OrderBy(m => m.Name).ToList();
            W($"--- методы ({methods.Count})");
            foreach (var m in methods)
                W($"  {TypeName(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(a => TypeName(a.ParameterType) + " " + a.Name))}){(m.IsStatic ? " [static]" : "")}");
        }

        private void DumpOthers()
        {
            // остальные типы игры, связанные с врагами и пулами: только имена — чтобы знать, что искать дальше
            try
            {
                var asm = _enemyType?.Assembly;
                if (asm == null) return;
                var names = asm.GetTypes().Select(t => t.Name)
                    .Where(n => n.IndexOf("Enemy", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Pool", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Spawn", StringComparison.OrdinalIgnoreCase) >= 0)
                    .Where(n => !n.StartsWith("<")).Distinct().OrderBy(n => n).ToList();
                W("");
                W($"##### типы с Enemy/Pool/Spawn в имени ({names.Count}): {string.Join(", ", names)}");
            }
            catch (Exception e) { W("типы сборки: " + e.Message); }
        }

        // ------------------------------------------------------------ хуки

        private void InstallHooks()
        {
            _ctx.Before("C_Battle_Manager", "Update", 0, (s, a) => { _mgr = s; _frames++; });

            _ctx.After("C_Battle_EnemyUnit", "Start_Unit", 3, (s, a) => _ctx.Guard("Start_Unit", () => OnSpawn(s, a)));

            // все методы менеджера, создающие или останавливающие врагов (менеджер один — хуки дешёвые)
            if (_mgrType != null)
            {
                var targets = _mgrType.GetMethods(Inst | BindingFlags.DeclaredOnly)
                    .Where(m => !m.IsSpecialName && !Junk(m.Name))
                    .Where(m => m.Name.StartsWith("Create_Unit") || m.Name.Contains("EnemyLevelUp") || m.Name.Contains("EnenmyClear") || m.Name.Contains("EnemyClear"))
                    .GroupBy(m => (m.Name, m.GetParameters().Length)).Select(g => g.First()).ToList();
                foreach (var m in targets)
                {
                    string key = $"{m.Name}/{m.GetParameters().Length}";
                    try
                    {
                        _ctx.Before("C_Battle_Manager", m.Name, m.GetParameters().Length, (s, a) =>
                        {
                            _mgrCalls[key] = _mgrCalls.TryGetValue(key, out var n) ? n + 1 : 1;
                            if (_mgrCalls[key] <= 5) W($"[{BattleT,6:0.00}] менеджер.{m.Name}({Args(a)})");
                        });
                        W($"хук: C_Battle_Manager.{key}");
                    }
                    catch (Exception e) { W($"хук не поставлен: {key}: {e.Message}"); }
                }
            }
        }

        private static string Args(object[] a)
        {
            if (a == null) return "";
            return string.Join(", ", a.Select(x => x == null ? "null" : x is float f ? f.ToString("0.###") : x is double d ? d.ToString("0.###") : x.ToString()));
        }

        private void OnSpawn(object enemy, object[] a)
        {
            if (!_inBattle) return;
            _spawnsTotal++; _spawnsInWindow++;
            int unitId = a != null && a.Length > 0 ? Convert.ToInt32(a[0]) : -1;
            _spawnsByType[unitId] = _spawnsByType.TryGetValue(unitId, out var n) ? n + 1 : 1;
            if (_spawnsDetailed < 40)
            {
                _spawnsDetailed++;
                var pos = Pos(enemy);
                var hero = _enemies.HeroPos;
                string rel = pos.HasValue && hero.HasValue ? $"от героя ({pos.Value.x - hero.Value.x:0.0}, {pos.Value.y - hero.Value.y:0.0})" : "место ?";
                W($"[{BattleT,6:0.00}] враг: тип {unitId}, varA {Args(new[] { a.ElementAtOrDefault(1) })}, varB {Args(new[] { a.ElementAtOrDefault(2) })}, {rel}, объект «{Name(enemy)}» #{Hash(enemy)}");
            }
            if (_sampleEnemy == null) { _sampleEnemy = enemy; _sampleAt = Now; W(""); W("--- значения полей первого врага сразу после Start_Unit:"); DumpValues(enemy); }
        }

        /// <summary>Адрес объекта игры (у Il2Cpp-обёрток — Pointer): по нему видно, переиспользуется ли объект врага из пула.</summary>
        private static string Hash(object o)
        {
            try { var p = o.GetType().GetProperty("Pointer", Inst)?.GetValue(o); return p is IntPtr ip ? ip.ToString("x") : o.GetHashCode().ToString("x"); }
            catch { return "?"; }
        }

        private string Name(object unityObj)
        {
            try { return unityObj.GetType().GetProperty("name", Inst)?.GetValue(unityObj) as string ?? "?"; } catch { return "?"; }
        }

        private (double x, double y)? Pos(object enemy)
        {
            try
            {
                var tr = enemy.GetType().GetProperty("Transform_ObjUnit", Inst)?.GetValue(enemy);
                if (tr == null) return null;
                var p = tr.GetType().GetProperty("position", Inst)?.GetValue(tr);
                if (p == null) return null;
                double x = Convert.ToDouble(p.GetType().GetField("x").GetValue(p)), y = Convert.ToDouble(p.GetType().GetField("y").GetValue(p));
                return (x, y);
            }
            catch { return null; }
        }

        /// <summary>Значения простых полей объекта (числа, флаги, строки) — чтобы понять состояние врага.</summary>
        private void DumpValues(object o)
        {
            if (o == null) return;
            foreach (var p in o.GetType().GetProperties(Inst).Where(p => !Junk(p.Name)).OrderBy(p => p.Name))
            {
                var t = p.PropertyType;
                if (!(t.IsPrimitive || t == typeof(string) || t.IsEnum || t.Name == "Vector3" || t.Name == "Vector2")) continue;
                if (p.GetIndexParameters().Length > 0) continue;
                try { W($"    {p.Name} = {Fmt(p.GetValue(o))}"); } catch { }
            }
        }

        private static string Fmt(object v)
        {
            if (v == null) return "null";
            var t = v.GetType();
            if (t.Name == "Vector3" || t.Name == "Vector2")
                return "(" + string.Join(", ", t.GetFields().Where(f => !f.IsStatic).Select(f => Convert.ToDouble(f.GetValue(v)).ToString("0.##"))) + ")";
            if (v is float f1) return f1.ToString("0.###");
            return v.ToString();
        }

        /// <summary>Размеры коллекций менеджера (ищем пул врагов).</summary>
        private void DumpCollections()
        {
            if (_mgr == null) { W("менеджер ещё не пойман"); return; }
            W("");
            W($"--- коллекции менеджера боя на {BattleT:0} с (имя: тип = размер):");
            foreach (var p in _mgr.GetType().GetProperties(Inst).Where(p => !Junk(p.Name)).OrderBy(p => p.Name))
            {
                var tn = TypeName(p.PropertyType);
                if (!(tn.Contains("List") || tn.Contains("Array") || tn.Contains("[]") || tn.Contains("Dictionary") || tn.Contains("Queue") || tn.Contains("Stack"))) continue;
                try
                {
                    var v = p.GetValue(_mgr);
                    if (v == null) { W($"    {p.Name}: {tn} = null"); continue; }
                    var cnt = v.GetType().GetProperty("Count")?.GetValue(v) ?? v.GetType().GetProperty("Length")?.GetValue(v);
                    W($"    {p.Name}: {tn} = {cnt ?? "?"}");
                }
                catch (Exception e) { W($"    {p.Name}: {tn} — {e.GetType().Name}"); }
            }
        }

        // ------------------------------------------------------------ бой и опыты

        private void OnBattleStart()
        {
            if (_done) return;
            _inBattle = true;
            _battleT0 = Now;
            _stage = 1;
            _nextSummary = 10;
            W("");
            W($"===== бой начался =====");
            _ctx.Toast("Разведка: играйте как обычно ~2 минуты", 3);
            _ctx.Log("бой начался — этап 1: наблюдение 60 с");
        }

        private void Step()
        {
            if (!_inBattle || _done) return;
            double t = BattleT;

            if (!_dumped && _mgr != null)
            {
                _dumped = true;
                DumpType(_mgrType, "МЕНЕДЖЕР БОЯ");
                DumpType(_enemyType, "ВРАГ");
                DumpOthers();
                Flush();
                _ctx.Log("структура типов записана");
            }

            if (_sampleEnemy != null && _sampleAt > 0 && Now - _sampleAt > 2)
            {
                _sampleAt = -1;
                W(""); W("--- тот же враг через 2 с:"); DumpValues(_sampleEnemy);
            }

            if (t >= _nextSummary)
            {
                _nextSummary += 10;
                int alive = 0; try { alive = _enemies.Alive.Count; } catch { }
                W($"[{t,6:0.00}] сводка: живых {alive}, создано за 10 с {_spawnsInWindow} (всего {_spawnsTotal}), смертей {_deaths}, кадров/с {_frames / 10.0:0}");
                _ctx.Log($"{t:0} с: живых {alive}, новых за 10 с {_spawnsInWindow}, кадров/с {_frames / 10.0:0}");
                _spawnsInWindow = 0; _frames = 0;
                if (Math.Abs(t - 30) < 6) DumpCollections();
                Flush();
            }

            if (_stage == 1 && t >= 60) { _stage = 2; _ctx.Guard("опыт 2", StopSpawn); }
            if (_stage == 2 && t >= 75) { _stage = 3; _ctx.Guard("опыт 2б", RestartSpawn); }
            if (_stage == 3 && t >= 90) { _stage = 4; _ctx.Guard("опыт 3", PuppetsStart); }
            if (_stage == 4 && t >= 91) { _stage = 5; _ctx.Guard("опыт 3", PuppetsMove); }
            if (_stage == 5 && t >= 92) { _stage = 6; _ctx.Guard("опыт 3", () => PuppetsCheck("через 1 с")); }
            if (_stage == 6 && t >= 95) { _stage = 7; _ctx.Guard("опыт 3", () => PuppetsCheck("через 4 с")); }
            if (_stage == 7 && t >= 104) { _stage = 8; _ctx.Guard("опыт 3", () => PuppetsCheck("через 13 с (заморозка 10 с кончилась)")); }
            if (_stage == 8 && t >= 110) Finish("все опыты проведены");
        }

        private int _spawnsAtStop;

        private void StopSpawn()
        {
            W(""); W($"===== опыт 2: выключаю спавн игры на 15 с =====");
            _ctx.Toast("Разведка: спавн врагов выключен на 15 с", 3);
            _spawnsAtStop = _spawnsTotal;
            CallMgr("Create_UnitEnemy_Stop");
        }

        private void RestartSpawn()
        {
            int during = _spawnsTotal - _spawnsAtStop;
            W($"[{BattleT,6:0.00}] за 15 с без спавна создано врагов: {during} {(during == 0 ? "— спавн выключается ✔" : "— спавн НЕ выключился")}");
            _ctx.Log($"опыт 2: за 15 с выключенного спавна создано {during}");
            W("включаю спавн обратно: StartCoroutine(\"Create_UnitEnemy\")");
            bool ok = false;
            try
            {
                var m = _mgr?.GetType().GetMethods(Inst).FirstOrDefault(x => x.Name == "StartCoroutine" && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType == typeof(string));
                if (m != null) { m.Invoke(_mgr, new object[] { "Create_UnitEnemy" }); ok = true; }
                else W("нет StartCoroutine(string)");
            }
            catch (Exception e) { W("StartCoroutine: " + (e.InnerException ?? e).Message); }
            _spawnsAtStop = _spawnsTotal;
            W(ok ? "вызвано; результат — в следующих сводках (создано за 10 с)" : "не удалось");
            _ctx.Toast("Разведка: спавн включён обратно", 2);
        }

        private void CallMgr(string name)
        {
            if (_mgr == null) { W($"{name}: менеджер не пойман"); return; }
            var m = _mgr.GetType().GetMethods(Inst).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == 0);
            if (m == null) { W($"{name}: метода без параметров нет"); return; }
            _ctx.Mark("вызов " + name);
            try { m.Invoke(_mgr, null); W($"{name}() вызван"); }
            catch (Exception e) { W($"{name}: {(e.InnerException ?? e).Message}"); }
        }

        private void PuppetsStart()
        {
            W(""); W("===== опыт 3: марионетки — заморозка/оглушение и ручной сдвиг =====");
            _ctx.Toast("Разведка: опыт с заморозкой врагов", 3);
            var hero = _enemies.HeroPos;
            if (!hero.HasValue) { W("нет героя"); return; }
            var near = _enemies.Near(hero.Value.x, hero.Value.y, 12).Take(9).ToList();
            for (int i = 0; i < near.Count; i++)
            {
                var e = near[i];
                string how = i % 3 == 0 ? "заморозка" : i % 3 == 1 ? "оглушение" : "без статуса";
                if (how == "заморозка") _combat.Freeze(e, 10);
                else if (how == "оглушение") _combat.Stun(e, 10);
                _puppets.Add((e, how, e.Pos.x, e.Pos.y, 0, 0));
            }
            W($"выбрано врагов: {_puppets.Count}");
        }

        private void PuppetsMove()
        {
            // сдвигаем каждого на 3 ед от героя (как будто ставим в точку, присланную хостом)
            var hero = _enemies.HeroPos;
            for (int i = 0; i < _puppets.Count; i++)
            {
                var p = _puppets[i];
                if (!p.e.Alive || !hero.HasValue) continue;
                double bx = p.e.Pos.x, by = p.e.Pos.y;
                double drift = Math.Sqrt((bx - p.x0) * (bx - p.x0) + (by - p.y0) * (by - p.y0));
                _combat.Pull(p.e, hero.Value.x, hero.Value.y, -3);
                double tx = p.e.Pos.x, ty = p.e.Pos.y;
                _puppets[i] = (p.e, p.how, bx, by, tx, ty);
                W($"  {p.how}: за 1 с до сдвига сам прошёл {drift:0.00}; сдвинут на {Math.Sqrt((tx - bx) * (tx - bx) + (ty - by) * (ty - by)):0.00}");
            }
        }

        private void PuppetsCheck(string when)
        {
            W($"--- {when}: насколько враг ушёл от точки, куда его поставили");
            foreach (var g in _puppets.GroupBy(p => p.how))
            {
                var d = g.Where(p => p.e.Alive).Select(p => Math.Sqrt((p.e.Pos.x - p.tx) * (p.e.Pos.x - p.tx) + (p.e.Pos.y - p.ty) * (p.e.Pos.y - p.ty))).ToList();
                W($"  {g.Key}: {(d.Count == 0 ? "все погибли" : string.Join(", ", d.Select(x => x.ToString("0.00"))))}");
            }
        }

        public void Finish(string why)
        {
            if (_done) return;
            _done = true;
            W(""); W($"===== разведка завершена: {why} =====");
            W("Создано по типам: " + string.Join(", ", _spawnsByType.OrderBy(k => k.Key).Select(k => $"{k.Key}×{k.Value}")));
            W("Вызовы менеджера: " + string.Join(", ", _mgrCalls.Select(k => $"{k.Key}×{k.Value}")));
            if (!_dumped) { DumpType(_mgrType, "МЕНЕДЖЕР БОЯ"); DumpType(_enemyType, "ВРАГ"); DumpOthers(); }
            Flush();
            _ctx.Log($"РАЗВЕДКА ЗАВЕРШЕНА ({why}). Пришлите файлы coop_probe.txt и modkit.log из папки UserData/ModKit");
            try { _ctx.Toast("Разведка завершена — пришлите coop_probe.txt и modkit.log", 5); } catch { }
        }
    }
}
