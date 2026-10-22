using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using ModKit;
using UnityEngine;

namespace MagicSurvivalCoop.Probe
{
    /// <summary>
    /// Разведка 3 для общих врагов — то же, что разведка 2, но наглядно: табло вверху экрана говорит, какой опыт идёт и какого
    /// цвета его враги; опытные враги крупнее и окрашены. Опыты (игровое время, на паузе стоит):
    ///   A (15–25 с) — враг по команде (Create_UnitEnemyBoss): ЗОЛОТОЙ, появляется справа от героя и стоит;
    ///   B (30–40 с) — марионетки: СИНИЕ кружат вокруг героя (ведёт мод), ЗЕЛЁНЫЕ стоят (корутины остановлены), КРАСНЫЕ — только без физики;
    ///   C (42 с)    — нагрузка: сколько стоит переставить всех врагов;
    ///   D (45–55 с) — новые враги исчезают сразу (Unit_Destroy_Simple);
    ///   E (58–68 с) — спавн погашен (EnemyCreateNum = 0).
    /// Отчёт — UserData/ModKit/coop_probe.txt.
    /// </summary>
    public class ProbeMod : IKitMod
    {
        public ModDef Define() => new ModDef
        {
            Id = "coop_probe",
            Name = "Кооператив: разведка врагов",
            Version = "0.3.0",
            Description = "Наглядная проверка механизмов общих врагов: табло вверху экрана и цветные враги (один бой ~70 с игрового времени). Потом выключите.",
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

        private static readonly Color Gold = new Color(1f, 0.82f, 0.1f, 1f);
        private static readonly Color Blue = new Color(0.25f, 0.55f, 1f, 1f);
        private static readonly Color Green = new Color(0.2f, 1f, 0.3f, 1f);
        private static readonly Color Red = new Color(1f, 0.25f, 0.25f, 1f);

        private readonly ModContext _ctx;
        private readonly IEnemies _enemies;
        private readonly StringBuilder _out = new StringBuilder();
        private readonly string _path;

        private object _mgr;
        private bool _inBattle, _done;
        private double _t0 = -1;
        private int _stage, _spawns, _deaths, _frames;

        // табло и окраска
        private UiKit.HudBar _board;
        private string _boardText = "";
        private readonly Dictionary<object, (Color c, float scale)> _marked = new Dictionary<object, (Color, float)>();
        private int _markFails;

        // A
        private bool _ourCall;
        private readonly List<object> _ourEnemies = new List<object>();
        private readonly List<(double t, int unitId, bool ours)> _recentSpawns = new List<(double, int, bool)>();
        private Dictionary<string, string> _snap;

        // B
        private readonly List<Puppet> _puppets = new List<Puppet>();
        private sealed class Puppet { public object E; public string Group; public double Phase, Tx, Ty, MaxErr; public bool Driven, HasTarget; }
        private double _driveUntil;

        // D, E
        private bool _suppress;
        private readonly Queue<object> _toDestroy = new Queue<object>();
        private int _destroyed, _deathsAtStage, _spawnsAtStage;
        private readonly Dictionary<string, object> _savedFields = new Dictionary<string, object>();

        public Probe(ModContext ctx)
        {
            _ctx = ctx;
            _enemies = ctx.Use<IEnemies>("modkit_combat");
            string dir = DirOverride;
            if (dir == null) { try { dir = Paths.Dir; } catch { dir = "/sdcard/MelonLoader/com.vkslrzm.Zombix/UserData/ModKit"; } }
            _path = Path.Combine(dir, "coop_probe.txt");
            W($"=== coop_probe 0.3.0, {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            ctx.Log("разведка 3 включена: начните бой; опыты идут ~70 с игрового времени, смотрите на табло вверху");

            ctx.Before("C_Battle_Manager", "Update", 0, (s, a) => { _mgr = s; _frames++; Frame(); });
            ctx.After("C_Battle_EnemyUnit", "Start_Unit", 3, (s, a) => OnSpawn(s, a));
            ctx.Events.On("battleStart", _ => ctx.Guard("начало боя", OnBattleStart));
            ctx.Events.On("battleEnd", _ => { if (_inBattle && !_done) Finish("бой закончился раньше"); _inBattle = false; DestroyBoard(); });
            ctx.Events.On("enemyDeath", _ => _deaths++);
            ctx.Every(100, () => ctx.Guard("шаг", Step));
            ctx.OnCleanup(() => { Restore(); DestroyBoard(); Flush(); });
        }

        // ------------------------------------------------------------ вывод, время, табло

        private void W(string line) => _out.AppendLine(line);
        private void Flush()
        {
            try { File.WriteAllText(_path, _out.ToString(), new UTF8Encoding(false)); }
            catch (Exception e) { _ctx.Log("не удалось записать отчёт: " + e.Message); }
        }
        private double GameT => _t0 < 0 ? 0 : _ctx.Timer.Now - _t0;
        private string T => $"[{GameT,6:0.00}]";

        private void Board(string text) { _boardText = text; }

        private void ShowBoard()
        {
            try
            {
                if (_board == null || !_board.Alive) _board = UiKit.AddHudBar("coop_probe_board", 10, 70, 700, 56, new[] { 0.08, 0.08, 0.22, 0.85 });
                _board.Set(1, _boardText);
            }
            catch (Exception e) { if (_markFails++ < 3) W("табло: " + e.Message); _board = null; }
        }
        private void DestroyBoard() { try { _board?.Destroy(); } catch { } _board = null; }

        /// <summary>Окрасить и увеличить врага; повторяется каждый кадр (игра перекрашивает при попадании).</summary>
        private void Mark(object enemy, Color c, float scale) { if (enemy != null) _marked[enemy] = (c, scale); ApplyMark(enemy, c, scale); }
        private void ApplyMark(object enemy, Color c, float scale)
        {
            try
            {
                if (Get(enemy, "Motion_Unit") is SpriteRenderer sr) sr.color = c;
                if (Get(enemy, "Transform_ObjUnitSprite") is Transform st) st.localScale = new Vector3(scale, scale, 1);
            }
            catch (Exception e) { if (_markFails++ < 3) W("окраска: " + Err(e)); }
        }

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
        private static bool Alive(object e) { try { return Convert.ToBoolean(Get(e, "Unit_IsLive")); } catch { return false; } }
        private (double x, double y)? Hero() { var h = _enemies.HeroPos; return h.HasValue ? (h.Value.x, h.Value.y) : ((double, double)?)null; }

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
                             .Where(kv => !kv.Key.StartsWith("SoundEffectLimit") && kv.Key != "Time_Sec" && kv.Key != "EnemyLevelNum")
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
            Flush();
        }

        private void OnSpawn(object enemy, object[] a)
        {
            try
            {
                if (!_inBattle) return;
                _spawns++;
                int unitId = a != null && a.Length > 0 ? Convert.ToInt32(a[0]) : -1;
                if (_ourCall) _ourEnemies.Add(enemy);
                if (_recentSpawns.Count >= 400) _recentSpawns.RemoveAt(0);
                _recentSpawns.Add((GameT, unitId, _ourCall));
                if (_suppress && !_ourCall) _toDestroy.Enqueue(enemy);   // убрать на следующем кадре, не внутри Start_Unit
            }
            catch (Exception e) { W("OnSpawn: " + Err(e)); }
        }

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
                    var h = Hero();
                    if (h != null)
                    {
                        double ang = GameT * 1.5;
                        foreach (var p in _puppets)
                        {
                            if (!p.Driven || !Alive(p.E)) continue;
                            var before = Pos(p.E);
                            if (before.HasValue && p.HasTarget) p.MaxErr = Math.Max(p.MaxErr, Math.Sqrt(Math.Pow(before.Value.x - p.Tx, 2) + Math.Pow(before.Value.y - p.Ty, 2)));
                            p.Tx = h.Value.x + Math.Cos(ang + p.Phase) * 4; p.Ty = h.Value.y + Math.Sin(ang + p.Phase) * 4; p.HasTarget = true;
                            SetPos(p.E, p.Tx, p.Ty);
                        }
                    }
                }
                foreach (var kv in _marked.ToList())
                {
                    if (!Alive(kv.Key)) { _marked.Remove(kv.Key); continue; }
                    ApplyMark(kv.Key, kv.Value.c, kv.Value.scale);
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
                case 0: Board($"Разведка: опыт A через {Math.Max(0, 15 - t):0} с — играйте как обычно"); if (t >= 15) { _stage = 1; _ctx.Guard("A", ExpSpawn); } break;
                case 1: Board($"Опыт A: ЗОЛОТОЙ большой враг справа от героя — создан модом по команде ({Math.Max(0, 25 - t):0} с)"); if (t >= 17) { _stage = 2; _ctx.Guard("A итог", ExpSpawnResult); } break;
                case 2: Board($"Опыт A: ЗОЛОТОЙ враг стоит, его можно убить ({Math.Max(0, 25 - t):0} с)"); if (t >= 25) { _stage = 3; _ctx.Guard("A конец", ExpSpawnEnd); } break;
                case 3: Board($"Опыт B через {Math.Max(0, 30 - t):0} с"); if (t >= 30) { _stage = 4; _ctx.Guard("B", PuppetsStart); } break;
                case 4: Board($"Опыт B: СИНИЕ кружат (ведёт мод), ЗЕЛЁНЫЕ стоят, КРАСНЫЕ уходят ({Math.Max(0, 40 - t):0} с)"); if (t >= 31) { _stage = 5; _ctx.Guard("B", () => PuppetsCheck("через 1 с")); } break;
                case 5: Board($"Опыт B: СИНИЕ кружат (ведёт мод), ЗЕЛЁНЫЕ стоят, КРАСНЫЕ уходят ({Math.Max(0, 40 - t):0} с)"); if (t >= 34) { _stage = 6; _ctx.Guard("B", () => PuppetsCheck("через 4 с")); } break;
                case 6: Board($"Опыт B: СИНИЕ кружат (ведёт мод), ЗЕЛЁНЫЕ стоят, КРАСНЫЕ уходят ({Math.Max(0, 40 - t):0} с)"); if (t >= 40) { _stage = 7; _ctx.Guard("B", () => PuppetsCheck("через 10 с (конец)")); } break;
                case 7: Board("Опыт C: замер нагрузки"); if (t >= 42) { _stage = 8; _ctx.Guard("C", CostTest); } break;
                case 8: Board($"Опыт D через {Math.Max(0, 45 - t):0} с"); if (t >= 45) { _stage = 9; _ctx.Guard("D", SuppressStart); } break;
                case 9: Board($"Опыт D: новые враги исчезают сразу — убрано {_destroyed} ({Math.Max(0, 55 - t):0} с)"); if (t >= 55) { _stage = 10; _ctx.Guard("D итог", SuppressEnd); } break;
                case 10: Board($"Опыт E через {Math.Max(0, 58 - t):0} с"); if (t >= 58) { _stage = 11; _ctx.Guard("E", ZeroSpawnStart); } break;
                case 11: Board($"Опыт E: спавн погашен — новых врагов {_spawns - _spawnsAtStage} ({Math.Max(0, 68 - t):0} с)"); if (t >= 68) { _stage = 12; _ctx.Guard("E итог", ZeroSpawnEnd); } break;
                case 12: if (t >= 70) Finish("все опыты проведены"); break;
            }
            ShowBoard();
        }

        // ---- A: враг по команде
        private int _bossNumBefore;
        private void ExpSpawn()
        {
            W(""); W("===== A: Create_UnitEnemyBoss(1, 1) — враг по команде (золотой) =====");
            if (_mgr == null) { W("менеджер не пойман"); return; }
            _snap = Snapshot(); _recentSpawns.Clear();
            try { _bossNumBefore = Convert.ToInt32(Get(_mgr, "ApplyBossNum")); } catch { }
            Flush(); _ctx.Mark("A Create_UnitEnemyBoss");
            _ourCall = true;
            try { Call(_mgr, "Create_UnitEnemyBoss", 1, 1); W($"{T} вызов прошёл"); }
            catch (Exception e) { W($"{T} ошибка: {Err(e)}"); }
            finally { _ourCall = false; }
            // счётчик боссов вернуть как было — враг обычный
            try { Set(_mgr, "ApplyBossNum", _bossNumBefore); W($"  ApplyBossNum возвращён в {_bossNumBefore}"); } catch (Exception e) { W("  ApplyBossNum: " + Err(e)); }
            var h = Hero();
            foreach (var e in _ourEnemies)
            {
                try
                {
                    if (h.HasValue) SetPos(e, h.Value.x + 3.5, h.Value.y);
                    Call(e, "StopAllCoroutines");                     // стоит на месте, чтобы его было видно
                    Mark(e, Gold, 1.8f);
                }
                catch (Exception ex) { W("  подготовка: " + Err(ex)); }
            }
            W($"  создано во время вызова: {_ourEnemies.Count}; поставлен в 3,5 ед справа от героя, окрашен золотым, корутины остановлены");
            Flush();
        }
        private void ExpSpawnResult()
        {
            W($"  за 2 с появилось: {string.Join("; ", _recentSpawns.Select(s => $"{s.t:0.0}с тип {s.unitId}{(s.ours ? " (наш)" : "")}"))}");
            DiffSnapshot("за 2 с после A");
            foreach (var e in _ourEnemies)
            {
                var p = Pos(e); var h = Hero();
                W($"  наш враг: тип {Get(e, "UnitID")}, жив {Get(e, "Unit_IsLive")}, HP {Get(e, "Unit_Hp")}/{Get(e, "UnitApply_HpMax")}" +
                  (p.HasValue && h.HasValue ? $", до героя {Math.Sqrt(Math.Pow(p.Value.x - h.Value.x, 2) + Math.Pow(p.Value.y - h.Value.y, 2)):0.0}" : ""));
            }
            Flush();
        }
        private void ExpSpawnEnd()
        {
            foreach (var e in _ourEnemies) W($"  к концу опыта A: жив {Get(e, "Unit_IsLive")}, HP {Get(e, "Unit_Hp")}");
            Flush();
        }

        // ---- B: марионетки
        private void PuppetsStart()
        {
            W(""); W("===== B: марионетки — СИНИЕ «стоп+кин» ведёт мод по кругу, ЗЕЛЁНЫЕ «стоп» стоят, КРАСНЫЕ «кин» без физики =====");
            var h = Hero(); if (h == null) { W("  нет героя"); return; }
            var near = _enemies.Near(h.Value.x, h.Value.y, 14).Where(e => !_ourEnemies.Contains(e.Raw)).Take(9).ToList();
            for (int i = 0; i < near.Count; i++)
            {
                var e = near[i].Raw;
                string g = i % 3 == 0 ? "стоп+кин" : i % 3 == 1 ? "стоп" : "кин";
                var p = new Puppet { E = e, Group = g, Phase = i * (Math.PI * 2 / 9), Driven = g == "стоп+кин" };
                try
                {
                    if (g.Contains("стоп")) Call(e, "StopAllCoroutines");
                    if (g.Contains("кин")) Set(Get(e, "Rigidbody_ObjUnit"), "isKinematic", true);
                    if (!p.Driven) { p.Tx = h.Value.x + Math.Cos(p.Phase) * 5.5; p.Ty = h.Value.y + Math.Sin(p.Phase) * 5.5; p.HasTarget = true; SetPos(e, p.Tx, p.Ty); }
                    Mark(e, g == "стоп+кин" ? Blue : g == "стоп" ? Green : Red, 1.5f);
                }
                catch (Exception ex) { W($"  {g}: {Err(ex)}"); }
                _puppets.Add(p);
            }
            W($"  выбрано {near.Count}");
            _driveUntil = GameT + 10;
            Flush();
        }
        private void PuppetsCheck(string when)
        {
            W($"  --- {when}:");
            foreach (var g in _puppets.GroupBy(p => p.Group))
            {
                var parts = g.Select(p =>
                {
                    string st = Alive(p.E) ? "" : " (погиб)";
                    if (p.Driven) return $"{p.MaxErr:0.000}{st}";
                    var pos = Pos(p.E);
                    return (pos.HasValue ? Math.Sqrt(Math.Pow(pos.Value.x - p.Tx, 2) + Math.Pow(pos.Value.y - p.Ty, 2)).ToString("0.00") : "?") + st;
                });
                W($"    {g.Key}: {(g.Key == "стоп+кин" ? "наибольший уход за кадр" : "ушёл от точки")} {string.Join(", ", parts)}");
            }
            Flush();
        }

        // ---- C: нагрузка
        private void CostTest()
        {
            W(""); W("===== C: сколько стоит переставить всех врагов за один кадр =====");
            var list = _enemies.Alive.Select(e => e.Raw).ToList();
            var trs = list.Select(Tr).Where(x => x != null).ToList();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            foreach (var tr in trs) { var v = tr.position; tr.position = v; }
            double cached = sw.Elapsed.TotalMilliseconds;
            W($"  врагов {trs.Count}: {cached:0.00} мс; на 200 врагов ≈ {(trs.Count > 0 ? cached / trs.Count * 200 : 0):0.00} мс за кадр");
            Flush();
        }

        // ---- D: подавление
        private void SuppressStart()
        {
            W(""); W("===== D: новые враги исчезают сразу (Unit_Destroy_Simple), 10 с =====");
            _snap = Snapshot(); _deathsAtStage = _deaths; _destroyed = 0; _spawnsAtStage = _spawns;
            _suppress = true;
            Flush();
        }
        private void SuppressEnd()
        {
            _suppress = false;
            W($"  появилось {_spawns - _spawnsAtStage}, убрано {_destroyed}, событий смерти {_deaths - _deathsAtStage} (это убийства героем)");
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
            W($"  за 10 с создано врагов: {_spawns - _spawnsAtStage}");
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
            W(""); W($"===== разведка 3 завершена: {why}; всего врагов создано {_spawns}, смертей {_deaths}, кадров {_frames} =====");
            Flush();
            _boardText = "Разведка завершена — пришлите coop_probe.txt и modkit.log";
            ShowBoard();
            _ctx.Log($"РАЗВЕДКА 3 ЗАВЕРШЕНА ({why}). Пришлите coop_probe.txt и modkit.log из UserData/ModKit");
            try { _ctx.Toast("Разведка завершена — пришлите coop_probe.txt и modkit.log", 5); } catch { }
            try { _ctx.Timer.After(8, DestroyBoard); } catch { }
        }
    }
}
