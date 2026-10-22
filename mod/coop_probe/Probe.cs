using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ModKit;
using UnityEngine;

namespace MagicSurvivalCoop.Probe
{
    /// <summary>
    /// Разведка 2 для синхронизации врагов. Проверяет механизмы, которые понадобятся «миру хоста»:
    ///   A (15–25 с) — создать врага самим: Create_UnitEnemyBoss и копия шаблона AObj_UnitEnemy + Start_Unit;
    ///   B (30–36 с) — марионетки: остановка корутин врага и кинематическое тело; держится ли враг там, куда его ставят каждый кадр;
    ///   C (40 с)    — сколько стоит двигать всех врагов каждый кадр;
    ///   D (45–55 с) — убирать врагов игры сразу после появления (Unit_Destroy_Simple) — без засчитанных убийств?
    ///   E (58–68 с) — погасить спавн обнулением числа врагов в пачке.
    /// Время — игровое (на паузе стоит). Отчёт — UserData/ModKit/coop_probe.txt, перед каждым рискованным шагом он сохраняется.
    /// </summary>
    public class ProbeMod : IKitMod
    {
        public ModDef Define() => new ModDef
        {
            Id = "coop_probe",
            Name = "Кооператив: разведка врагов",
            Version = "0.2.0",
            Description = "Диагностика для синхронизации врагов, этап 2: создание врагов, марионетки, нагрузка (один бой ~70 с игрового времени). Потом выключите.",
            Depends = { ["modkit_combat"] = ">=1.0" },
            OnEnable = ctx => _p = new Probe(ctx),
            OnDisable = ctx => { _p?.Finish("мод выключен"); _p = null; },
        };
        private Probe _p;
    }

    internal sealed class Probe
    {
        private const BindingFlags Inst = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        private const BindingFlags Any = Inst | BindingFlags.Static;

        /// <summary>Только для тестов на ПК (через рефлексию).</summary>
        private static string DirOverride;

        private readonly ModContext _ctx;
        private readonly IEnemies _enemies;
        private readonly StringBuilder _out = new StringBuilder();
        private readonly string _path;

        private object _mgr;
        private Type _enemyType;
        private bool _inBattle, _done;
        private double _t0 = -1;
        private int _stage, _spawns, _deaths, _frames;

        // создание своих врагов
        private bool _ourCall;
        private readonly List<object> _ourEnemies = new List<object>();
        private readonly List<(double t, int unitId, bool ours, string where)> _recentSpawns = new List<(double, int, bool, string)>();
        private Dictionary<string, string> _snap;

        // марионетки
        private readonly List<Puppet> _puppets = new List<Puppet>();
        private sealed class Puppet { public object E; public string Group; public double Phase, Tx, Ty, MaxErr; public bool Driven, HasTarget; }
        private double _driveUntil;

        // подавление
        private bool _suppress;
        private readonly Queue<object> _toDestroy = new Queue<object>();
        private int _destroyed, _deathsAtSuppress, _spawnsAtStage;
        private readonly Dictionary<string, object> _savedFields = new Dictionary<string, object>();

        public Probe(ModContext ctx)
        {
            _ctx = ctx;
            _enemies = ctx.Use<IEnemies>("modkit_combat");
            string dir = DirOverride;
            if (dir == null) { try { dir = Paths.Dir; } catch { dir = "/sdcard/MelonLoader/com.vkslrzm.Zombix/UserData/ModKit"; } }
            _path = Path.Combine(dir, "coop_probe.txt");
            W($"=== coop_probe 0.2.0, {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            ctx.Log("разведка 2 включена: начните бой; опыты идут ~70 с игрового времени");
            try { _enemyType = Hooks.FindType("C_Battle_EnemyUnit"); }
            catch (Exception e) { W("типы: " + e.Message); }

            ctx.Before("C_Battle_Manager", "Update", 0, (s, a) => { _mgr = s; _frames++; Frame(); });
            ctx.After("C_Battle_EnemyUnit", "Start_Unit", 3, (s, a) => OnSpawn(s, a));
            ctx.Events.On("battleStart", _ => ctx.Guard("начало боя", OnBattleStart));
            ctx.Events.On("battleEnd", _ => { if (_inBattle && !_done) Finish("бой закончился раньше"); _inBattle = false; });
            ctx.Events.On("enemyDeath", _ => _deaths++);
            ctx.Every(100, () => ctx.Guard("шаг", Step));
            ctx.OnCleanup(() => { Restore(); Flush(); });
        }

        // ------------------------------------------------------------ вывод и время

        private void W(string line) => _out.AppendLine(line);
        private void Flush()
        {
            try { File.WriteAllText(_path, _out.ToString(), new UTF8Encoding(false)); }
            catch (Exception e) { _ctx.Log("не удалось записать отчёт: " + e.Message); }
        }
        private double GameT => _t0 < 0 ? 0 : _ctx.Timer.Now - _t0;
        private string T => $"[{GameT,6:0.00}]";

        // ------------------------------------------------------------ рефлексия

        private static object Get(object o, string name)
        {
            if (o == null) return null;
            var p = o.GetType().GetProperty(name, Any);
            if (p != null) return p.GetValue(p.GetGetMethod(true)?.IsStatic == true ? null : o);
            var f = o.GetType().GetField(name, Any);
            return f?.GetValue(f.IsStatic ? null : o);
        }
        private static bool Set(object o, string name, object v)
        {
            var p = o?.GetType().GetProperty(name, Any);
            if (p == null || !p.CanWrite) return false;
            var val = v == null || p.PropertyType.IsInstanceOfType(v) ? v : Convert.ChangeType(v, p.PropertyType);
            p.SetValue(p.GetSetMethod(true)?.IsStatic == true ? null : o, val);
            return true;
        }
        private static object Call(object o, string name, params object[] args)
        {
            var m = o.GetType().GetMethods(Inst).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length)
                    ?? throw new MissingMethodException(o.GetType().Name + "." + name + "/" + args.Length);
            var ps = m.GetParameters();
            var conv = args.Select((a, i) => a == null || ps[i].ParameterType.IsInstanceOfType(a) ? a : Convert.ChangeType(a, ps[i].ParameterType)).ToArray();
            return m.Invoke(o, conv);
        }
        private static string Err(Exception e)
        {
            var x = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
            return x.GetType().Name + ": " + x.Message;
        }

        private static Transform Tr(object enemy) => Get(enemy, "Transform_ObjUnit") as Transform;

        private static (double x, double y)? Pos(object enemy)
        {
            try { var tr = Tr(enemy); if (tr == null) return null; var p = tr.position; return (p.x, p.y); }
            catch { return null; }
        }
        private static void SetPos(object enemy, double x, double y)
        {
            var tr = Tr(enemy) ?? throw new InvalidOperationException("нет Transform_ObjUnit");
            var z = tr.position.z;
            tr.position = new Vector3((float)x, (float)y, z);
        }
        private (double x, double y)? Hero() { var h = _enemies.HeroPos; return h.HasValue ? (h.Value.x, h.Value.y) : ((double, double)?)null; }

        /// <summary>Снимок простых полей менеджера — чтобы увидеть побочные эффекты (счётчики убийств, волна боссов…).</summary>
        private Dictionary<string, string> Snapshot()
        {
            var d = new Dictionary<string, string>();
            if (_mgr == null) return d;
            foreach (var p in _mgr.GetType().GetProperties(Any))
            {
                var t = p.PropertyType;
                if (!(t.IsPrimitive || t == typeof(string)) || p.GetIndexParameters().Length > 0 || p.Name.StartsWith("Native")) continue;
                try { d[p.Name] = Convert.ToString(p.GetValue(p.GetGetMethod(true).IsStatic ? null : _mgr), System.Globalization.CultureInfo.InvariantCulture); } catch { }
            }
            return d;
        }
        private void DiffSnapshot(string title)
        {
            var now = Snapshot();
            var changed = now.Where(kv => _snap != null && _snap.TryGetValue(kv.Key, out var old) && old != kv.Value)
                             .Select(kv => $"{kv.Key}: {_snap[kv.Key]} → {kv.Value}").ToList();
            W($"  изменения в менеджере ({title}): {(changed.Count == 0 ? "нет" : string.Join("; ", changed.Take(50)))}");
        }

        // ------------------------------------------------------------ события

        private void OnBattleStart()
        {
            if (_done) return;
            _inBattle = true;
            _t0 = _ctx.Timer.Now;
            _stage = 0;
            W(""); W("===== бой начался =====");
            _ctx.Toast("Разведка 2: играйте как обычно ~70 с", 3);
            Flush();
        }

        private void OnSpawn(object enemy, object[] a)
        {
            try
            {
                if (!_inBattle) return;
                _spawns++;
                int unitId = a != null && a.Length > 0 ? Convert.ToInt32(a[0]) : -1;
                var pos = Pos(enemy); var h = Hero();
                string where = pos.HasValue && h.HasValue ? $"от героя ({pos.Value.x - h.Value.x:0.0}, {pos.Value.y - h.Value.y:0.0})" : "?";
                if (_ourCall) _ourEnemies.Add(enemy);
                if (_recentSpawns.Count >= 400) _recentSpawns.RemoveAt(0);
                _recentSpawns.Add((GameT, unitId, _ourCall, where));
                if (_suppress && !_ourCall) _toDestroy.Enqueue(enemy);   // убрать на следующем кадре, не внутри Start_Unit
            }
            catch (Exception e) { W("OnSpawn: " + Err(e)); }
        }

        /// <summary>Каждый кадр боя: подавление, ведение марионеток.</summary>
        private void Frame()
        {
            if (!_inBattle || _done) return;
            try
            {
                while (_toDestroy.Count > 0)
                {
                    var e = _toDestroy.Dequeue();
                    try { Call(e, "Unit_Destroy_Simple"); _destroyed++; }
                    catch (Exception ex) { if (_destroyed == 0) W("Unit_Destroy_Simple: " + Err(ex)); }
                }
                if (GameT < _driveUntil && _ctx.Timer.Delta > 0)
                {
                    var h = Hero(); if (h == null) return;
                    double ang = GameT * 1.5;
                    foreach (var p in _puppets)
                    {
                        if (!p.Driven) continue;
                        var before = Pos(p.E);                                // отклонение от точки, поставленной в прошлом кадре
                        if (before.HasValue && p.HasTarget) p.MaxErr = Math.Max(p.MaxErr, Math.Sqrt(Math.Pow(before.Value.x - p.Tx, 2) + Math.Pow(before.Value.y - p.Ty, 2)));
                        p.Tx = h.Value.x + Math.Cos(ang + p.Phase) * 4; p.Ty = h.Value.y + Math.Sin(ang + p.Phase) * 4; p.HasTarget = true;
                        SetPos(p.E, p.Tx, p.Ty);
                    }
                }
            }
            catch (Exception e) { W($"{T} кадр: {Err(e)}"); }
        }

        // ------------------------------------------------------------ сценарий

        private void Step()
        {
            if (!_inBattle || _done) return;
            double t = GameT;
            switch (_stage)
            {
                case 0 when t >= 15: _stage = 1; _ctx.Guard("A1", ExpBoss); break;
                case 1 when t >= 17: _stage = 2; _ctx.Guard("A1 итог", ExpBossResult); break;
                case 2 when t >= 20: _stage = 3; _ctx.Guard("A2", ExpInstantiate); break;
                case 3 when t >= 22: _stage = 4; _ctx.Guard("A2 итог", ExpInstantiateResult); break;
                case 4 when t >= 30: _stage = 5; _ctx.Guard("B", PuppetsStart); break;
                case 5 when t >= 31: _stage = 6; _ctx.Guard("B", () => PuppetsCheck("через 1 с")); break;
                case 6 when t >= 33: _stage = 7; _ctx.Guard("B", () => PuppetsCheck("через 3 с")); break;
                case 7 when t >= 36: _stage = 8; _ctx.Guard("B", () => PuppetsCheck("через 6 с (ведение закончилось)")); break;
                case 8 when t >= 40: _stage = 9; _ctx.Guard("C", CostTest); break;
                case 9 when t >= 45: _stage = 10; _ctx.Guard("D", SuppressStart); break;
                case 10 when t >= 55: _stage = 11; _ctx.Guard("D итог", SuppressEnd); break;
                case 11 when t >= 58: _stage = 12; _ctx.Guard("E", ZeroSpawnStart); break;
                case 12 when t >= 68: _stage = 13; _ctx.Guard("E итог", ZeroSpawnEnd); break;
                case 13 when t >= 70: Finish("все опыты проведены"); break;
            }
        }

        // ---- A1: Create_UnitEnemyBoss
        private void ExpBoss()
        {
            W(""); W("===== A1: Create_UnitEnemyBoss(1, 1) — создаётся ли обычный враг по команде =====");
            if (_mgr == null) { W("менеджер не пойман"); return; }
            _snap = Snapshot(); _recentSpawns.Clear();
            Flush(); _ctx.Mark("A1 Create_UnitEnemyBoss");
            _ourCall = true;
            try { Call(_mgr, "Create_UnitEnemyBoss", 1, 1); W($"{T} вызов прошёл"); }
            catch (Exception e) { W($"{T} ошибка: {Err(e)}"); }
            finally { _ourCall = false; }
            W($"  создано прямо во время вызова: {_ourEnemies.Count}" + (_ourEnemies.Count > 0 ? $" ({string.Join("; ", _recentSpawns.Where(s => s.ours).Select(s => $"тип {s.unitId}, {s.where}"))})" : ""));
            _ctx.Toast("Разведка 2: опыт A — создание врага", 2);
            Flush();
        }
        private void ExpBossResult()
        {
            W($"  за 2 с после вызова появилось: {string.Join("; ", _recentSpawns.Select(s => $"{s.t:0.0}с тип {s.unitId}{(s.ours ? " (наш)" : "")} {s.where}"))}");
            DiffSnapshot("за 2 с после A1");
            foreach (var e in _ourEnemies.ToList())
                W($"  наш враг: UnitID {Get(e, "UnitID")}, жив {Get(e, "Unit_IsLive")}, HP {Get(e, "Unit_Hp")}/{Get(e, "UnitApply_HpMax")}, размер {Get(e, "UnitApply_UnitSize")}, имя «{Get(e, "name")}»");
            Flush();
        }

        // ---- A2: копия шаблона
        private object _clone;
        private (double x, double y) _clonePos;
        private void ExpInstantiate()
        {
            W(""); W("===== A2: копия шаблона AObj_UnitEnemy + Start_Unit(6, 0, 0) =====");
            var prefab = Get(_mgr, "AObj_UnitEnemy");
            if (prefab == null) { W("  AObj_UnitEnemy = null"); return; }
            W($"  шаблон: «{Get(prefab, "name")}», активен {Get(prefab, "activeSelf")}");
            Flush(); _ctx.Mark("A2 Instantiate");
            object go;
            try
            {
                var objType = typeof(Transform).BaseType?.BaseType;          // UnityEngine.Object
                var inst = objType?.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Instantiate" && !m.IsGenericMethod && m.GetParameters().Length == 1);
                if (inst == null) { W($"  нет Object.Instantiate(Object) у {objType?.FullName}"); return; }
                go = inst.Invoke(null, new[] { prefab });
                W($"  копия создана: «{Get(go, "name")}»");
            }
            catch (Exception e) { W("  Instantiate: " + Err(e)); Flush(); return; }
            Flush(); _ctx.Mark("A2 GetComponent");
            try
            {
                var gc = go.GetType().GetMethods(Inst).FirstOrDefault(m => m.Name == "GetComponent" && m.IsGenericMethod && m.GetParameters().Length == 0);
                _clone = gc?.MakeGenericMethod(_enemyType).Invoke(go, null);
                W($"  компонент врага: {(_clone != null ? "есть" : "НЕТ")}");
                if (_clone == null) return;
                Call(go, "SetActive", true);
                var h = Hero();
                Flush(); _ctx.Mark("A2 Start_Unit");
                _ourCall = true;
                try { Call(_clone, "Start_Unit", 6, 0f, 0f); } finally { _ourCall = false; }
                W($"  Start_Unit прошёл; жив {Get(_clone, "Unit_IsLive")}, HP {Get(_clone, "Unit_Hp")}");
                if (h.HasValue) { SetPos(_clone, h.Value.x + 4, h.Value.y); _clonePos = (h.Value.x + 4, h.Value.y); W("  поставлен в 4 ед справа от героя"); }
            }
            catch (Exception e) { W("  " + Err(e)); }
            Flush();
        }
        private void ExpInstantiateResult()
        {
            if (_clone == null) return;
            var p = Pos(_clone); var h = Hero();
            W($"  через 2 с: жив {Get(_clone, "Unit_IsLive")}, HP {Get(_clone, "Unit_Hp")}, " +
              (p.HasValue ? $"сдвинулся на {Math.Sqrt(Math.Pow(p.Value.x - _clonePos.x, 2) + Math.Pow(p.Value.y - _clonePos.y, 2)):0.00}" : "позиция ?") +
              (p.HasValue && h.HasValue ? $", до героя {Math.Sqrt(Math.Pow(p.Value.x - h.Value.x, 2) + Math.Pow(p.Value.y - h.Value.y, 2)):0.0}" : "") +
              " (если подходит к герою — ИИ работает, враг полноценный)");
            Flush();
        }

        // ---- B: марионетки
        private void PuppetsStart()
        {
            W(""); W("===== B: марионетки =====");
            W("  «стоп+кин» — StopAllCoroutines + isKinematic, ведём каждый кадр по кругу; «стоп» — только StopAllCoroutines; «кин» — только isKinematic (оба ставим один раз)");
            var h = Hero(); if (h == null) { W("  нет героя"); return; }
            var near = _enemies.Near(h.Value.x, h.Value.y, 14).Where(e => !_ourEnemies.Contains(e.Raw)).Take(6).ToList();
            string[] groups = { "стоп+кин", "стоп+кин", "стоп", "стоп", "кин", "кин" };
            for (int i = 0; i < near.Count; i++)
            {
                var e = near[i].Raw; var g = groups[i];
                var p = new Puppet { E = e, Group = g, Phase = i * 1.05, Driven = g == "стоп+кин" };
                try
                {
                    if (g.Contains("стоп")) Call(e, "StopAllCoroutines");
                    if (g.Contains("кин")) { var rb = Get(e, "Rigidbody_ObjUnit"); W($"    тело: {(rb == null ? "нет" : Set(rb, "isKinematic", true) ? "кинематическое" : "нельзя изменить")}"); }
                    if (!p.Driven) { p.Tx = h.Value.x + Math.Cos(i) * 5; p.Ty = h.Value.y + Math.Sin(i) * 5; p.HasTarget = true; SetPos(e, p.Tx, p.Ty); }
                    W($"  {g}: подготовлен, HP {Get(e, "Unit_Hp")}");
                }
                catch (Exception ex) { W($"  {g}: {Err(ex)}"); }
                _puppets.Add(p);
            }
            _driveUntil = GameT + 6;
            _ctx.Toast("Разведка 2: опыт B — марионетки", 2);
            Flush();
        }
        private void PuppetsCheck(string when)
        {
            W($"  --- {when}:");
            foreach (var p in _puppets)
            {
                string alive = Convert.ToString(Get(p.E, "Unit_IsLive"));
                if (p.Driven) W($"    {p.Group}: наибольший уход от поставленной точки за кадр {p.MaxErr:0.000}, жив {alive}, HP {Get(p.E, "Unit_Hp")}");
                else
                {
                    var pos = Pos(p.E);
                    W($"    {p.Group}: ушёл от точки на {(pos.HasValue ? Math.Sqrt(Math.Pow(pos.Value.x - p.Tx, 2) + Math.Pow(pos.Value.y - p.Ty, 2)).ToString("0.00") : "?")}, жив {alive}, HP {Get(p.E, "Unit_Hp")}");
                }
            }
            Flush();
        }

        // ---- C: нагрузка
        private void CostTest()
        {
            W(""); W("===== C: сколько стоит переставить всех врагов за один кадр =====");
            var list = _enemies.Alive.Select(e => e.Raw).ToList();
            var sw = Stopwatch.StartNew();
            int ok = 0;
            foreach (var e in list) { var p = Pos(e); if (p.HasValue) { SetPos(e, p.Value.x, p.Value.y); ok++; } }
            double naive = sw.Elapsed.TotalMilliseconds;
            var trs = list.Select(Tr).Where(x => x != null).ToList();
            sw.Restart();
            foreach (var tr in trs) { var v = tr.position; tr.position = v; }
            double cached = sw.Elapsed.TotalMilliseconds;
            W($"  врагов {list.Count}: с поиском объекта {naive:0.00} мс ({ok} шт.), с готовыми ссылками {cached:0.00} мс");
            W($"  оценка на 200 врагов с готовыми ссылками: {(trs.Count > 0 ? cached / trs.Count * 200 : 0):0.00} мс за кадр");
            Flush();
        }

        // ---- D: подавление
        private void SuppressStart()
        {
            W(""); W("===== D: убираю врагов игры сразу после появления (Unit_Destroy_Simple), 10 с =====");
            _snap = Snapshot(); _deathsAtSuppress = _deaths; _destroyed = 0; _spawnsAtStage = _spawns;
            _suppress = true;
            _ctx.Toast("Разведка 2: опыт D — новые враги исчезают", 2);
            Flush();
        }
        private void SuppressEnd()
        {
            _suppress = false;
            W($"  появилось {_spawns - _spawnsAtStage}, убрано {_destroyed}, событий смерти за это время {_deaths - _deathsAtSuppress} (сюда входят и убийства героем)");
            DiffSnapshot("за 10 с подавления");
            Flush();
        }

        // ---- E: обнуление спавна
        private void ZeroSpawnStart()
        {
            W(""); W("===== E: EnemyCreateNum/_Min/_Max = 0 на 10 с =====");
            _spawnsAtStage = _spawns;
            foreach (var f in new[] { "EnemyCreateNum", "EnemyCreateNum_Min", "EnemyCreateNum_Max" })
            {
                try { _savedFields[f] = Get(_mgr, f); W($"  {f} было {_savedFields[f]}, ставлю 0: {(Set(_mgr, f, 0) ? "ок" : "нельзя")}"); }
                catch (Exception e) { W($"  {f}: {Err(e)}"); }
            }
            Flush();
        }
        private void ZeroSpawnEnd()
        {
            double t = GameT;
            W($"  за 10 с создано врагов: {_spawns - _spawnsAtStage} (типы: {string.Join(",", _recentSpawns.Where(s => s.t >= t - 10).Select(s => s.unitId).Distinct())})");
            Restore();
            Flush();
        }
        private void Restore()
        {
            foreach (var kv in _savedFields.ToList())
            {
                try { Set(_mgr, kv.Key, kv.Value); W($"  {kv.Key} возвращено {kv.Value}"); } catch { }
            }
            _savedFields.Clear();
            _suppress = false;
        }

        public void Finish(string why)
        {
            if (_done) return;
            _done = true;
            Restore();
            W(""); W($"===== разведка 2 завершена: {why}; всего врагов создано {_spawns}, смертей {_deaths}, кадров {_frames} =====");
            Flush();
            _ctx.Log($"РАЗВЕДКА 2 ЗАВЕРШЕНА ({why}). Пришлите coop_probe.txt и modkit.log из UserData/ModKit");
            try { _ctx.Toast("Разведка 2 завершена — пришлите coop_probe.txt и modkit.log", 5); } catch { }
        }
    }
}
