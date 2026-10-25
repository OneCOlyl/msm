using System;
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
    /// Общие враги — «мир хоста» (docs/ENEMY_SYNC.md). Включается настройкой «world» у всех игроков.
    /// Хост: в каждом состоянии (до 10 раз в секунду) — список живых врагов: [номер, тип (UnitID), x, y] от точки старта хоста, ×100.
    /// Гость: свой спавн погашен (EnemyCreateNum = 0 и Unit_Destroy_Simple для всех чужих врагов — без засчитанных убийств),
    /// вместо него — копии врагов хоста: Create_UnitEnemyBoss(тип, 1), корутины остановлены, тело кинематическое,
    /// каждый кадр копия стоит там же относительно старта гостя, где враг хоста относительно старта хоста (с той же задержкой,
    /// что и союзник). Враг пропал у хоста — копия убивается уроном (гость получает убийство и выпадения). Копию, убитую
    /// у гостя его магией, заново не создаём. Урон гостя (1.5.5): копии неуязвимы (здоровье 1e6) — насколько упало здоровье,
    /// столько урона нанёс гость любым заклинанием; раз в 0,1 с — событие «dmg» хосту, тот бьёт настоящего врага (ICombat.Damage).
    /// Повторы магии друг у друга в этом режиме — только картинка (иначе урон дважды).
    /// </summary>
    internal sealed partial class Session
    {
        private const int MaxSentEnemies = 250;          // ~20 байт на врага: строка состояния остаётся меньше 16 КБ
        private const double EnemySendInterval = 0.09;
        // 1.6.19: было 4 и 4 мс — бой 24.10 (1.6.18, Pixel 3a): 14 345 копий по 1,13 мс в среднем, кадры с 3–4 созданиями — медленные.
        // Нужно в среднем ~20 копий/с, 2 за кадр при 37 кадрах/с — до 74/с: волна догоняется за пару секунд, без рывков
        private const int MaxPuppetSpawnsPerFrame = 2;
        private const double PuppetSpawnBudgetMs = 2.5;
        private const int MaxPuppetKillsPerFrame = 4;
        private const double PuppetKillBudgetMs = 2;
        private readonly Queue<Puppet> _dying = new Queue<Puppet>();

        private ICombat _combat;
        private bool WorldOn => _ctx.Setting("world") > 0;

        // ---- хост
        private readonly Dictionary<int, IEnemy> _enById = new Dictionary<int, IEnemy>();   // для урона гостей
        private double _dmgIn; private int _dmgInMiss;
        private Dictionary<object, (int id, int type)> _enIds = new Dictionary<object, (int, int)>(), _enIdsNext = new Dictionary<object, (int, int)>();
        private int _enNextId = 1, _enSentMax;
        private double _enSentAt = -1;
        private bool _enHostLogged;

        // ---- гость
        private sealed class Puppet
        {
            public int Id, Type, Seen;
            public float VarA, VarB;                       // как игра хоста создала врага (усиленные мобы — varA 1)
            public bool Special;
            public int CreateId, CreateEffect;             // особый враг: способ создания (3 — «переход»), эффект появления
            public float LineR, LineG, LineB;              // и цвет обводки (усиленные — красная)
            public object Obj, Key;
            public Transform Tr;
            public bool LocalDead;
            public double DeadAt;                          // погибла у гостя (Армагеддон и т.п.) — когда; > 2 с и жива у хоста — создать заново
            public readonly List<(double t, double x, double y)> Track = new List<(double, double, double)>();
        }
        private readonly Dictionary<int, Puppet> _puppets = new Dictionary<int, Puppet>();
        private readonly HashSet<object> _puppetKeys = new HashSet<object>();
        private readonly Queue<object> _toDestroy = new Queue<object>();
        private readonly List<int> _gone = new List<int>();
        private readonly Queue<(object obj, int type, float a, float b)> _toForward = new Queue<(object, int, float, float)>();
        private readonly List<(int type, float a, float b, double x, double y)> _spawnOut = new List<(int, float, float, double, double)>();
        private readonly Dictionary<int, int> _sweptTypes = new Dictionary<int, int>();
        private int _spawnSent, _spawnIn, _spawnInLogs;
        private readonly System.Diagnostics.Stopwatch _worldWatch = new System.Diagnostics.Stopwatch();
        private double _createMsMax, _createMsSum, _stepMsMax; private int _createN;
        private readonly Dictionary<int, double> _dmgOut = new Dictionary<int, double>();
        private double _dmgSent, _dmgFlushAt;
        private Partner _worldHost;
        private int _enSnap, _worldFrame;
        private double _enSnapAt = -100, _sweepAt;
        private bool _worldActive, _spawningPuppet;
        private object _justCreated;
        private int _wCreated, _wKilled, _wLocalDead, _wDestroyed, _wFails, _wMax, _wRespawned, _wSpecial;
        // копии у гостя неуязвимы: умирают, только когда умер враг у хоста (1.5.4). Unit_Hp — float: у 1e9 шаг 64 единицы и мелкий
        // урон терялся бы; у 1e6 — 0,06, а больше миллиона за 0,1 с не бьёт никто
        private const double PuppetHp = 1e6;
        private Dictionary<string, string> _mgrAtFirst;   // журнал 1.5.1: почему Create_UnitEnemyBoss перестаёт создавать врагов
        private bool _mgrFailLogged, _poolLogged;
        private int _poolMoved;

        private void InstallWorldHooks()
        {
            try { _combat = _ctx.Use<ICombat>("modkit_combat"); } catch (Exception e) { _ctx.Log("общие враги: нет ICombat — копии будут убираться без награды: " + e.Message); }
            try { _ctx.After("C_Battle_EnemyUnit", "Start_Unit", 3, Hook("синхронизация спавна врага", (self, a) => OnEnemyStart(self, a))); }
            catch (Exception e) { _ctx.Log("общие враги: хук Start_Unit недоступен (свой спавн гасится только чисткой): " + e.Message); }
        }

        private void ResetWorld()
        {
            _coSeen.Clear(); _coPull = 0; _itemKinds.Clear(); _itemLogs = 0; _itemCalls = 0; _obeliskItemDumps = 0;
            _enIds.Clear(); _enIdsNext.Clear(); _enById.Clear(); _dmgIn = 0; _dmgInMiss = 0; _dmgOut.Clear(); _dmgSent = 0; _dmgFlushAt = 0; _enSentAt = -1; _enSentMax = 0; _enHostLogged = false;
            _puppets.Clear(); _puppetKeys.Clear(); _toDestroy.Clear(); _dying.Clear(); _savedSpawn = null;
            _worldHost = null; _enSnap = 0; _enSnapAt = -100; _worldActive = false; _spawningPuppet = false; _justCreated = null;
            _wCreated = _wKilled = _wLocalDead = _wDestroyed = _wFails = _wMax = _wRespawned = _wSpecial = 0;
            _enVars.Clear(); _enVarsNext.Clear();
            _toForward.Clear(); _spawnOut.Clear(); _sweptTypes.Clear(); _specialSpawns.Clear(); _specialLogs = 0;
            _dumpSpecial = _dumpNormal = _specialCopy = null; _dumped = false; _specialCopyAt = 0;
            _held.Clear(); _heldKeys.Clear(); _heldNew.Clear(); _heldKilled = 0; _spawnSent = _spawnIn = _spawnInLogs = 0;
            _createMsMax = _createMsSum = _stepMsMax = 0; _createN = 0;
            _mgrAtFirst = null; _mgrFailLogged = false; _poolLogged = false; _poolMoved = 0;
        }

        private static object KeyOf(object o) { var p = PtrOf(o); return p != IntPtr.Zero ? (object)p : o; }

        /// <summary>
        /// Живые враги — один снимок на кадр. Библиотека на каждое обращение к Alive строит новый список и для каждого врага
        /// зовёт игру (activeInHierarchy, Unit_Hp); а им пользуются отправка (хост), чистка, повтор магии, убийство копий.
        /// </summary>
        private IReadOnlyList<IEnemy> AliveNow()
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            if (_aliveSnap == null || now - _aliveSnapAt > System.Diagnostics.Stopwatch.Frequency / 200)   // 5 мс — в пределах кадра
            {
                _aliveSnap = _enemies.Alive;
                _aliveSnapAt = now;
            }
            return _aliveSnap;
        }
        private IReadOnlyList<IEnemy> _aliveSnap;
        private long _aliveSnapAt;

        // ============================================================ хост: отправка

        private void WriteEnemies(Utf8JsonWriter w)
        {
            DumpEnemiesStep();
            double now = Clock.Elapsed.TotalSeconds;
            if (now - _enSentAt < EnemySendInterval) return;
            _enSentAt = now;
            var alive = AliveNow();
            if (!_enHostLogged && alive.Count > 0)
            {
                _enHostLogged = true;
                var r0 = alive[0].Raw;
                _ctx.Log($"общие враги: отправляю своих врагов друзьям; мой враг — размер {GetMember(r0, "UnitApply_UnitSize")}, масштаб {GetMember(GetMember(r0, "transform"), "localScale")}, спрайт-масштаб {GetMember(GetMember(r0, "Transform_ObjUnitSprite"), "localScale")}");
            }
            double sx = _startX ?? 0, sy = _startY ?? 0;
            int n = 0;
            _enById.Clear();
            w.WriteStartArray("en");
            foreach (var e in alive)                 // список уже только из живых — e.Alive повторил бы те же вызовы игры
            {
                if (n >= MaxSentEnemies) break;
                var raw = e.Raw; var key = KeyOf(raw);
                if (key == null) continue;
                if (!_enIds.TryGetValue(key, out var v))
                {
                    int type = -1;
                    try { type = Convert.ToInt32(GetMember(raw, "UnitID") ?? -1); } catch { }
                    v = (_enNextId++, type);
                }
                _enIdsNext[key] = v;
                _enById[v.id] = e;
                if (_specialSpawns.Count > 0) MarkSpecialSent(key);
                var p = e.Pos;
                w.WriteStartArray();
                w.WriteNumberValue(v.id); w.WriteNumberValue(v.type);
                w.WriteNumberValue((int)Math.Round((p.x - sx) * 100)); w.WriteNumberValue((int)Math.Round((p.y - sy) * 100));
                if (_enVars.TryGetValue(key, out var vars)) { w.WriteNumberValue(vars.a); w.WriteNumberValue(vars.b); WriteLook(w, raw); _enVarsNext[key] = vars; }
                w.WriteEndArray();
                n++;
            }
            w.WriteEndArray();
            (_enIds, _enIdsNext) = (_enIdsNext, _enIds);
            _enIdsNext.Clear();
            if (n < MaxSentEnemies) { (_enVars, _enVarsNext) = (_enVarsNext, _enVars); _enVarsNext.Clear(); }   // мёртвых — забыть
            if (n > _enSentMax) _enSentMax = n;
        }

        // ============================================================ гость: приём

        private void ReadEnemies(Partner p, JsonElement s)
        {
            if (_hosting || !WorldOn || !s.TryGetProperty("en", out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            _worldHost = p;
            double st = Protocol.Num(s, "t", -1);
            _enSnap++; _enSnapAt = Clock.Elapsed.TotalSeconds;
            foreach (var it in arr.EnumerateArray())
            {
                if (it.ValueKind != JsonValueKind.Array || it.GetArrayLength() < 4) continue;
                if (!it[0].TryGetInt32(out int id) || !it[1].TryGetInt32(out int type) || !it[2].TryGetInt32(out int x) || !it[3].TryGetInt32(out int y)) continue;
                if (!_puppets.TryGetValue(id, out var pp))
                {
                    _puppets[id] = pp = new Puppet { Id = id, Type = type };
                    if (it.GetArrayLength() >= 6 && it[4].TryGetSingle(out float va) && it[5].TryGetSingle(out float vb)) { pp.VarA = va; pp.VarB = vb; pp.Special = true; }
                    if (it.GetArrayLength() >= 11)
                    {
                        it[6].TryGetInt32(out pp.CreateId); it[7].TryGetInt32(out pp.CreateEffect);
                        it[8].TryGetSingle(out pp.LineR); it[9].TryGetSingle(out pp.LineG); it[10].TryGetSingle(out pp.LineB);
                    }
                }
                pp.Seen = _enSnap;
                if (pp.Track.Count == 0 || st > pp.Track[pp.Track.Count - 1].t)
                {
                    pp.Track.Add((st, x / 100.0, y / 100.0));
                    if (pp.Track.Count > 4) pp.Track.RemoveAt(0);
                }
            }
        }

        private void OnEnemyStart(object self, object[] a)
        {
            if (_spawningPuppet) { _justCreated = self; return; }
            if (_hosting && WorldOn && _inRun && self != null) { NoteHostSpawn(self, a); NoteHostVars(self, a); }
            if (!_worldActive || self == null) return;
            if (MyBossWave() != 0)
            {
                // своя босс-волна гостя: её босса прячем, пока хост не поднимет обелиск, — тогда мод сам снимает волну
                // и убирает босса без смерти (ObeliskSync.cs)
                var hk = KeyOf(self);
                if (hk != null && !_heldKeys.Contains(hk)) { _heldKeys.Add(hk); _held.Add(self); _heldNew.Enqueue(self); }
                return;
            }
            _toDestroy.Enqueue(self);                                       // свой враг гостя — убрать на следующем кадре, не внутри Start_Unit
            int type = -1; float va = 0, vb = 0;
            try { type = Convert.ToInt32(a[0]); va = Convert.ToSingle(a[1]); vb = Convert.ToSingle(a[2]); } catch { }
            _sweptTypes[type] = _sweptTypes.TryGetValue(type, out var k) ? k + 1 : 1;
            // обычный спавн гостя погашен — значит, это враги от его действий (фиолетовая сфера и т.п.): создать у хоста.
            // Босс-волну не пересылаем — у хоста она своя.
            if (_hostBossWave == 0 && MyBossWave() == 0 && _toForward.Count < 40) _toForward.Enqueue((self, type, va, vb));
        }

        // ------------------------------------------------------------ журнал 1.6.6: особые враги хоста (усиление мобов → «переход»)

        // Подобранное хостом усиление мобов запускает корутину Play_Create_UnitEnemy_Transition (Transition_EnemyMax = 15,
        // Transition_EnemyNum растёт), а у гостя эти враги не появились. Пишем, кто создаётся во время перехода и с какими
        // varA/varB, и попадает ли он потом в список врагов для гостя (_enemies.Alive).
        private readonly Dictionary<object, (int type, float a, float b, double at, bool sent)> _specialSpawns = new Dictionary<object, (int, float, float, double, bool)>();
        private int _specialLogs;

        // varA/varB, с которыми игра создала врага (Start_Unit): у усиленных мобов varA = 1 (журнал 1.6.6) — их копии у гостя
        // создаются с теми же varA/varB, иначе это обычные мобы того же типа.
        private Dictionary<object, (float a, float b)> _enVars = new Dictionary<object, (float, float)>(), _enVarsNext = new Dictionary<object, (float, float)>();
        private void NoteHostVars(object self, object[] a)
        {
            float va = 0, vb = 0;
            try { va = Convert.ToSingle(a[1]); vb = Convert.ToSingle(a[2]); } catch { }
            var key = KeyOf(self);
            if (key == null) return;
            int cid = 1;
            try { cid = Convert.ToInt32(GetMember(self, "UnitCreateID") ?? 1); } catch { }
            // особый: varA/varB или создан «переходом» (UnitCreateID 3 — красная обводка, журнал 1.6.10); пул объекты переиспользует
            if (va != 0 || vb != 0 || cid == 3) _enVars[key] = (va, vb); else _enVars.Remove(key);
        }

        /// <summary>Хост: как выглядит особый враг — способ создания, эффект появления, цвет обводки (читаем при отправке).</summary>
        private static void WriteLook(Utf8JsonWriter w, object raw)
        {
            foreach (var f in new[] { "UnitCreateID", "ApplyCreateEffectID" })
            {
                int v = 0; try { v = Convert.ToInt32(GetMember(raw, f) ?? 0); } catch { }
                w.WriteNumberValue(v);
            }
            foreach (var f in new[] { "LineColorR", "LineColorG", "LineColorB" })
            {
                float v = 0; try { v = Convert.ToSingle(GetMember(raw, f) ?? 0f); } catch { }
                w.WriteNumberValue(Math.Round(v, 3));
            }
        }

        // 1.6.10: у копий varA = 1 не делает мобов «усиленными» (у друга их не было видно) — что меняется в самом враге?
        // Через 1,5 с после появления — все простые поля первого особого и одного обычного врага того же боя.
        private object _dumpSpecial, _dumpNormal, _specialCopy;
        private double _specialCopyAt;
        private double _dumpAt;
        private bool _dumped;

        private void DumpEnemiesStep()
        {
            if (_dumped || _dumpSpecial == null || Clock.Elapsed.TotalSeconds < _dumpAt) return;
            _dumped = true;
            if (_dumpNormal == null)
                foreach (var e in _enemies.Alive) { var k = KeyOf(e.Raw); if (k != null && !_specialSpawns.ContainsKey(k)) { _dumpNormal = e.Raw; break; } }
            _ctx.Log("усиленный моб (особый): " + EnemyFields(_dumpSpecial));
            if (_dumpNormal != null) _ctx.Log("обычный моб для сравнения: " + EnemyFields(_dumpNormal));
        }

        private static string EnemyFields(object e)
        {
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var parts = new List<string>();
            try
            {
                foreach (var p in e.GetType().GetProperties(any))
                {
                    if (p.GetIndexParameters().Length > 0 || p.Name.StartsWith("Native")) continue;
                    var t = p.PropertyType;
                    if (!(t.IsPrimitive || t.IsEnum)) continue;
                    object v; try { v = p.GetValue(e); } catch { continue; }
                    parts.Add(p.Name + "=" + Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex) { parts.Add("ошибка: " + Inner(ex).Message); }
            return string.Join(", ", parts);
        }

        private void NoteHostSpawn(object self, object[] a)
        {
            int type = -1; float va = 0, vb = 0;
            try { type = Convert.ToInt32(a[0]); va = Convert.ToSingle(a[1]); vb = Convert.ToSingle(a[2]); } catch { }
            int trans = 0;
            try { trans = Convert.ToInt32(GetAny(_battleMgr, "Transition_EnemyNum") ?? 0); } catch { }
            if (va == 0 && vb == 0 && trans == 0) return;
            var key = KeyOf(self);
            if (key == null || _specialSpawns.Count > 300) return;
            _specialSpawns[key] = (type, va, vb, Clock.Elapsed.TotalSeconds, false);
            if (_specialLogs++ < 20) _ctx.Log($"особый враг хоста: тип {type}, varA {va}, varB {vb}, переход {trans}, «{NameOf(self)}», размер {GetMember(self, "UnitApply_UnitSize")}");
            if (_dumpSpecial == null) { _dumpSpecial = self; _dumpAt = Clock.Elapsed.TotalSeconds + 1.5; }
        }

        /// <summary>Попал ли особый враг в список для гостя — отмечаем при отправке.</summary>
        private void MarkSpecialSent(object key)
        {
            if (_specialSpawns.TryGetValue(key, out var v) && !v.sent) _specialSpawns[key] = (v.type, v.a, v.b, v.at, true);
        }

        private string SpecialSummary() => _specialSpawns.Count == 0 ? "" :
            $"; особых врагов хоста {_specialSpawns.Count}, ушли гостю {_specialSpawns.Values.Count(v => v.sent)} " +
            $"(по типам: {string.Join(" ", _specialSpawns.Values.GroupBy(v => (v.type, v.a, v.b)).Select(g => $"{g.Key.type}/{g.Key.a}/{g.Key.b}×{g.Count()}"))})";

        // ============================================================ гость: каждый кадр

        private void WorldStep(double now)
        {
            var host = _worldHost;
            bool want = WorldOn && !_hosting && host != null && _partners.ContainsKey(host.Id) && host.Battle && now - _enSnapAt < 2 && _startX.HasValue;
            if (!want) { if (_worldActive) StopWorld("хост не присылает врагов"); return; }
            var mgr = _battleMgr;
            if (mgr == null) return;
            if (!_worldActive)
            {
                _worldActive = true; _sweepAt = 0;
                _ctx.Log($"общие враги: включено — враги «{host.Name}», свой спавн погашен");
                _ctx.Toast($"Общие враги: играем в мире «{host.Name}»", 3);
            }
            _worldFrame++;
            _world.Start();
            if (now >= _sweepAt)
            {
                _sweepAt = now + 0.5;
                _ctx.Mark("общие враги: гашу свой спавн и скорость копий");
                SuppressSpawn(mgr); Sweep();
                if (_ctx.Setting("puppetAi") > 0)                                // игра пересчитывает скорость (усиления волн) — гасим снова
                    foreach (var pp in _puppets.Values) if (pp.Obj != null && !pp.LocalDead) try { ZeroSpeed(pp.Obj); } catch { }
            }
            _world.Lap(0);
            while (_toForward.Count > 0)
            {
                var f = _toForward.Dequeue();
                try { var q = (GetMember(f.obj, "Transform_ObjUnit") as Transform).position; _spawnOut.Add((f.type, f.a, f.b, q.x - _startX.Value, q.y - (_startY ?? 0))); }
                catch { }
            }
            if (_toDestroy.Count > 0)
            {
                _ctx.Mark("общие враги: убираю своих врагов гостя (Unit_Destroy_Simple)");
                while (_toDestroy.Count > 0) DestroySimple(_toDestroy.Dequeue());
            }

            _world.Lap(1);
            double rt = now - host.ClockOff - RenderDelay(host);
            double ox = _startX.Value, oy = _startY ?? 0;
            if (HeroPosition(out var hhx, out var hhy, out _)) HeldStep(hhx, hhy);
            _world.Lap(2);
            int made = 0, live = 0;
            double createMs = 0;
            _worldWatch.Restart();
            _gone.Clear();
            _ctx.Mark("общие враги: перемещение копий");
            foreach (var pp in _puppets.Values)
            {
                if (pp.Seen != _enSnap) { _gone.Add(pp.Id); continue; }
                if (pp.LocalDead)
                {
                    if (pp.DeadAt <= 0 || now - pp.DeadAt < 2) continue;
                    pp.Obj = null; pp.Tr = null; pp.LocalDead = false; pp.DeadAt = 0; _wRespawned++;   // у хоста жив — создать снова
                }
                try
                {
                    if (pp.Obj == null)
                    {
                        // бюджет кадра: создание копии — работа самой игры (журнал 1.5.7: у гостя лагало в начале волны)
                        if (made >= MaxPuppetSpawnsPerFrame || createMs > PuppetSpawnBudgetMs) continue;
                        made++;
                        _ctx.Mark($"общие враги: создание копии врага (тип {pp.Type})");
                        double t0 = _worldWatch.Elapsed.TotalMilliseconds;
                        bool ok = CreatePuppet(mgr, pp);
                        double ms = _worldWatch.Elapsed.TotalMilliseconds - t0;
                        createMs += ms; _createMsSum += ms; _createN++; if (ms > _createMsMax) _createMsMax = ms;
                        _world.Lap(3);
                        _ctx.Mark("общие враги: перемещение копий");
                        if (!ok) continue;
                    }
                    else if ((_worldFrame + pp.Id) % 6 == 0)
                    {
                        if (!(GetMember(pp.Obj, "Unit_IsLive") is bool alive && alive))
                        {
                            pp.LocalDead = true; pp.DeadAt = now; _wLocalDead++;   // погибла в обход здоровья (Армагеддон…) — подождём хоста
                            continue;
                        }
                        // урон гостя: здоровье копии держим на PuppetHp — разница и есть урон (любым заклинанием гостя)
                        double hp = Convert.ToDouble(GetMember(pp.Obj, "Unit_Hp") ?? PuppetHp);
                        if (hp < PuppetHp - 0.01)
                        {
                            _ctx.Mark("общие враги: здоровье копии");
                            _dmgOut[pp.Id] = (_dmgOut.TryGetValue(pp.Id, out var d0) ? d0 : 0) + (PuppetHp - hp);
                            Invulnerable(pp.Obj);
                            _ctx.Mark("общие враги: перемещение копий");
                        }
                        _world.Lap(4);
                    }
                    if (pp.Tr == null || !PuppetPos(pp, rt, out var x, out var y)) continue;
                    var cur = pp.Tr.position;
                    pp.Tr.position = new Vector3((float)(ox + x), (float)(oy + y), cur.z);
                    live++;
                    _world.Lap(5);
                }
                catch (Exception e) { pp.LocalDead = true; if (++_wFails <= 5) _ctx.Log($"общие враги: копия {pp.Id}: {Inner(e).Message}"); }
            }
            _world.Lap(5);
            if (live > _wMax) _wMax = live;
            if ((_dmgOut.Count > 0 || _spawnOut.Count > 0) && now >= _dmgFlushAt) FlushDamage(now);
            _world.Lap(7);
            if (_specialCopy != null && now >= _specialCopyAt) { _ctx.Log("особая копия у гостя: " + EnemyFields(_specialCopy)); _specialCopy = null; _specialCopyAt = double.MaxValue; }
            double stepMs = _worldWatch.Elapsed.TotalMilliseconds;
            if (stepMs > _stepMsMax) _stepMsMax = stepMs;
            // враг погиб у хоста — копию из списка сразу (её больше не двигаем и не создаём заново), а убиваем по очереди: смерть
            // врага — работа игры (награда, эффекты), и пачка смертей в одном кадре давала у гостя кадр до 72 мс (1.6.19, телефоны)
            foreach (var id in _gone) { _dying.Enqueue(_puppets[id]); _puppets.Remove(id); }
            if (_dying.Count > 0)
            {
                _ctx.Mark("общие враги: убиваю копии (враг погиб у хоста)");
                FillAliveByKey();
                double t0 = _worldWatch.Elapsed.TotalMilliseconds;
                for (int k = 0; k < MaxPuppetKillsPerFrame && _dying.Count > 0 && _worldWatch.Elapsed.TotalMilliseconds - t0 < PuppetKillBudgetMs; k++)
                    KillPuppet(_dying.Dequeue());
                _aliveByKey.Clear();
            }
            _world.Lap(6);
            _world.Count++;
        }

        private void FlushDamage(double now)
        {
            _dmgFlushAt = now + 0.1;
            if (!_client.Connected || _worldHost == null) { _dmgOut.Clear(); return; }
            var to = _worldHost.Id;
            _client.SendEvent(Protocol.Build(w =>
            {
                w.WriteString("kind", "dmg"); w.WriteString("to", to);
                w.WriteStartArray("d");
                foreach (var kv in _dmgOut) { w.WriteNumberValue(kv.Key); w.WriteNumberValue(Math.Round(kv.Value, 1)); _dmgSent += kv.Value; }
                w.WriteEndArray();
                if (_spawnOut.Count > 0)
                {
                    w.WriteStartArray("s");                                 // [тип, varA, varB, x, y] от старта гостя
                    foreach (var s in _spawnOut) { w.WriteNumberValue(s.type); w.WriteNumberValue(s.a); w.WriteNumberValue(s.b); w.WriteNumberValue(Math.Round(s.x, 2)); w.WriteNumberValue(Math.Round(s.y, 2)); }
                    w.WriteEndArray();
                    _spawnSent += _spawnOut.Count;
                }
            }));
            _dmgOut.Clear(); _spawnOut.Clear();
        }

        /// <summary>Хост: особые враги гостя (от его действий) — создать у себя там же относительно старта (тип; varA/varB пока не передаются игре).</summary>
        private void OnRemoteSpawns(JsonElement e)
        {
            if (!_hosting || !WorldOn || !_inRun || _battleMgr == null || !e.TryGetProperty("s", out var s) || s.ValueKind != JsonValueKind.Array) return;
            int n = s.GetArrayLength();
            for (int i = 0; i + 4 < n && i < 5 * 10; i += 5)
            {
                if (!s[i].TryGetInt32(out int type) || type < 0) continue;
                double x = s[i + 3].GetDouble(), y = s[i + 4].GetDouble();
                var bossProp = FindAny(_battleMgr.GetType(), "ApplyBossNum")?.Prop;
                object bossNum = null;
                _ctx.Mark($"общие враги: создаю врага от гостя (тип {type})");
                _spawningPuppet = true; _justCreated = null;
                try
                {
                    try { bossNum = bossProp?.GetValue(null); } catch { }
                    RefillBossPool(_battleMgr);
                    CallMethod(_battleMgr, "Create_UnitEnemyBoss", type, 1);
                }
                catch (Exception ex) { if (++_wFails <= 5) _ctx.Log($"общие враги: враг гостя тип {type}: {Inner(ex).Message}"); }
                finally { _spawningPuppet = false; if (bossNum != null) { try { bossProp.SetValue(null, bossNum); } catch { } } }
                var obj = _justCreated; _justCreated = null;
                if (obj == null) continue;
                float sa = 0, sb = 0;
                try { sa = s[i + 1].GetSingle(); sb = s[i + 2].GetSingle(); } catch { }
                if (sa != 0 || sb != 0)
                {
                    _spawningPuppet = true;                                     // особый враг гостя (усиление мобов у гостя): как у игры
                    try { CallMethod(obj, "Start_Unit", type, sa, sb); } catch { }
                    finally { _spawningPuppet = false; _justCreated = null; }
                    NoteHostVars(obj, new object[] { type, sa, sb });
                }
                try { var tr = GetMember(obj, "Transform_ObjUnit") as Transform; var z = tr.position.z; tr.position = new Vector3((float)((_startX ?? 0) + x), (float)((_startY ?? 0) + y), z); } catch { }
                FixSpriteScale(obj);
                _spawnIn++;
                if (_spawnInLogs++ < 5) _ctx.Log($"общие враги: враг от действий гостя — тип {type}, varA {s[i + 1]}, varB {s[i + 2]}, создан у меня");
            }
        }

        /// <summary>Хост: урон гостя по общим врагам — по настоящему врагу.</summary>
        private void OnRemoteDamage(JsonElement e)
        {
            OnRemoteSpawns(e);
            if (!_hosting || !WorldOn || !_inRun || _combat == null || !e.TryGetProperty("d", out var d) || d.ValueKind != JsonValueKind.Array) return;
            int n = d.GetArrayLength();
            for (int i = 0; i + 1 < n; i += 2)
            {
                if (!d[i].TryGetInt32(out int id) || !d[i + 1].TryGetDouble(out double amt) || amt <= 0) continue;
                if (!_enById.TryGetValue(id, out var en) || !en.Alive) { _dmgInMiss++; continue; }
                try { _combat.Damage(en, amt); _dmgIn += amt; }
                catch (Exception ex) { if (++_wFails <= 5) _ctx.Log("общие враги: урон гостя: " + Inner(ex).Message); }
            }
        }

        /// <summary>Повтор магии этого друга — только картинка: урон идёт через здоровье копий (общие враги, 1.5.5).</summary>
        private bool ReplayWithoutDamage(Partner p) => (_hosting && WorldOn && p.WorldGuest) || (_worldActive && p == _worldHost);

        /// <summary>Позиция врага хоста (от старта хоста) на момент rt по часам хоста: между точками — линейно, после последней — не дальше 0,2 с.</summary>
        private static bool PuppetPos(Puppet pp, double rt, out double x, out double y)
        {
            x = y = 0;
            var tr = pp.Track;
            if (tr.Count == 0) return false;
            var last = tr[tr.Count - 1];
            if (tr.Count == 1 || rt <= tr[0].t) { var f = rt <= tr[0].t ? tr[0] : last; x = f.x; y = f.y; return true; }
            if (rt >= last.t)
            {
                var prev = tr[tr.Count - 2];
                double dtl = last.t - prev.t, ex = Math.Min(rt - last.t, 0.2);
                if (dtl <= 1e-4) { x = last.x; y = last.y; return true; }
                x = last.x + (last.x - prev.x) / dtl * ex; y = last.y + (last.y - prev.y) / dtl * ex;
                return true;
            }
            for (int i = tr.Count - 1; i > 0; i--)
            {
                var a = tr[i - 1]; var b = tr[i];
                if (rt < a.t) continue;
                double k = (rt - a.t) / Math.Max(1e-4, b.t - a.t);
                x = a.x + (b.x - a.x) * k; y = a.y + (b.y - a.y) * k;
                return true;
            }
            x = last.x; y = last.y;
            return true;
        }

        private bool CreatePuppet(object mgr, Puppet pp)
        {
            if (pp.Type < 0) { pp.LocalDead = true; return false; }
            var bossProp = FindAny(mgr.GetType(), "ApplyBossNum")?.Prop;
            object bossNum = null;
            _spawningPuppet = true; _justCreated = null;
            try
            {
                try { bossNum = bossProp?.GetValue(null); } catch { }
                RefillBossPool(mgr);
                CallMethod(mgr, "Create_UnitEnemyBoss", pp.Type, 1);     // синхронно создаёт обычного врага этого типа (разведка A1)
            }
            catch (Exception e) { pp.LocalDead = true; if (++_wFails <= 5) _ctx.Log($"общие враги: создать тип {pp.Type}: {Inner(e).Message}"); return false; }
            finally
            {
                _spawningPuppet = false;
                if (bossNum != null) { try { bossProp.SetValue(null, bossNum); } catch { } }   // побочный счётчик боссов — вернуть
            }
            var obj = _justCreated; _justCreated = null;
            if (obj == null)
            {
                pp.LocalDead = true;
                if (++_wFails <= 5) _ctx.Log($"общие враги: тип {pp.Type} не создался");
                if (!_mgrFailLogged) { _mgrFailLogged = true; _ctx.Log($"общие враги: менеджер при первой неудаче (было при первой копии → сейчас): {MgrDiff(mgr)}"); }
                return false;
            }
            if (_mgrAtFirst == null)
            {
                _mgrAtFirst = MgrSnapshot(mgr);
                _ctx.Log($"общие враги: копия — размер {GetMember(obj, "UnitApply_UnitSize")}, масштаб {GetMember(GetMember(obj, "transform"), "localScale")}, спрайт-масштаб до подгонки {GetMember(GetMember(obj, "Transform_ObjUnitSprite"), "localScale")}");
            }
            pp.Obj = obj; pp.Key = KeyOf(obj); _puppetKeys.Add(pp.Key);
            if (pp.Special)
            {
                // особый враг (усиленный): игра создаёт его «переходом» (UnitCreateID 3) и Start_Unit(тип, varA, varB) — повторяем
                // на копии; способ создания — до Start_Unit (от него игра выставляет вид), эффект и обводку — после
                _spawningPuppet = true;
                _ctx.Mark($"общие враги: особая копия — Start_Unit (тип {pp.Type})");
                try
                {
                    if (pp.CreateId > 0) SetMember(obj, "UnitCreateID", pp.CreateId);
                    CallMethod(obj, "Start_Unit", pp.Type, pp.VarA, pp.VarB);
                    if (pp.CreateId > 0) SetMember(obj, "UnitCreateID", pp.CreateId);
                    SetMember(obj, "ApplyCreateEffectID", pp.CreateEffect);
                    SetMember(obj, "LineColorR", pp.LineR); SetMember(obj, "LineColorG", pp.LineG); SetMember(obj, "LineColorB", pp.LineB);
                    _wSpecial++;
                    if (_specialCopy == null) { _specialCopy = obj; _specialCopyAt = Clock.Elapsed.TotalSeconds + 1.5; }
                }
                catch (Exception e) { if (++_wFails <= 5) _ctx.Log($"общие враги: особая копия {pp.Type}/{pp.VarA}/{pp.VarB}: {Inner(e).Message}"); }
                finally { _spawningPuppet = false; _justCreated = null; }
            }
            pp.Tr = GetMember(obj, "Transform_ObjUnit") as Transform;
            // 1.5.0 останавливал все корутины — вместе с ИИ вставали кадры ходьбы (их меняет Unit_Move). Теперь ИИ работает
            // (анимация, поворот, атаки), но скорость нулевая — двигает только мод. «puppetAi» = 0 — по-старому.
            _ctx.Mark("общие враги: настройка копии (здоровье, скорость, тело)");
            FixSpriteScale(obj);
            Invulnerable(obj);
            if (_ctx.Setting("puppetAi") > 0) ZeroSpeed(obj);
            else try { CallMethod(obj, "StopAllCoroutines"); } catch { }
            try { SetMember(GetMember(obj, "Rigidbody_ObjUnit"), "isKinematic", true); } catch { }   // соседи не расталкивают
            if (_wCreated++ == 0) _ctx.Log($"общие враги: первая копия — тип {pp.Type}, «{NameOf(obj)}», спрайт-масштаб после подгонки {GetMember(GetMember(obj, "Transform_ObjUnitSprite"), "localScale")}, здоровье {GetMember(obj, "Unit_Hp")}");
            return true;
        }

        private static readonly string[] SpeedFields = { "UnitBase_MoveSpeed", "UnitApply_MoveSpeed", "BUnitApply_MoveSpeed", "UnitApply_MoveSpeed_Bonus", "UnitApply_MoveSpeed_Buff", "ApplyMoveSpeedBonus" };
        /// <summary>Свойство или поле (обычное или статическое) по имени — поиск один раз на тип и имя, как у CallMethod.</summary>
        private sealed class AnyMember
        {
            public PropertyInfo Prop; public FieldInfo Field; public bool Static; public Type ValueType;
            public FieldInfo SetField;                     // свойство только для чтения — запись в одноимённое поле (как раньше)
        }
        private static readonly Dictionary<(Type, string), AnyMember> AnyCache = new Dictionary<(Type, string), AnyMember>();
        private static AnyMember FindAny(Type t, string name)
        {
            lock (AnyCache)
            {
                if (AnyCache.TryGetValue((t, name), out var m)) return m;
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                var p = t.GetProperty(name, any);
                if (p != null && p.GetIndexParameters().Length == 0)
                    m = new AnyMember { Prop = p, Static = (p.GetGetMethod(true) ?? p.GetSetMethod(true))?.IsStatic == true, ValueType = p.PropertyType,
                                        SetField = p.CanWrite ? null : t.GetField(name, any) };
                else if (t.GetField(name, any) is FieldInfo f)
                    m = new AnyMember { Field = f, Static = f.IsStatic, ValueType = f.FieldType };
                AnyCache[(t, name)] = m;   // null — нет такого члена (тоже запоминаем)
                return m;
            }
        }

        private static object GetAny(object o, string name)
        {
            var m = FindAny(o.GetType(), name);
            if (m == null) return null;
            var target = m.Static ? null : o;
            return m.Prop != null ? m.Prop.GetValue(target) : m.Field.GetValue(target);
        }

        /// <summary>У копии из пула боссов картинка 1,00, у обычного врага — как размер (1,25) (журнал 1.5.2).</summary>
        private static void FixSpriteScale(object enemy)
        {
            try
            {
                var tr = GetMember(enemy, "Transform_ObjUnitSprite") as Transform;
                float size = Convert.ToSingle(GetMember(enemy, "UnitApply_UnitSize") ?? 0f);
                if (tr == null || size <= 0.01f) return;
                var ls = tr.localScale;
                tr.localScale = new Vector3(ls.x < 0 ? -size : size, size, ls.z == 0 ? 1 : ls.z);
            }
            catch { }
        }

        private void Invulnerable(object enemy)
        {
            try { SetMember(enemy, "Unit_Hp", PuppetHp); }
            catch (Exception e) { if (++_wFails <= 5) _ctx.Log("общие враги: здоровье копии: " + Inner(e).Message); }
        }

        private static void ZeroSpeed(object enemy)
        {
            foreach (var f in SpeedFields) try { SetMember(enemy, f, 0f); } catch { }
        }

        /// <summary>
        /// Create_UnitEnemyBoss берёт врага из пула боссов (14 штук), а погибшие возвращаются в обычный пул (журнал 1.5.1:
        /// List_BattleUnitEnemyBoss_Empty 14 → 1, List_BattleUnitEnemy_Empty 451 → 455) — перекладываем из обычного.
        /// </summary>
        private void RefillBossPool(object mgr)
        {
            var boss = GetAny(mgr, "List_BattleUnitEnemyBoss_Empty");          // статические поля (1.5.2 искал только обычные — не находил)
            var common = GetAny(mgr, "List_BattleUnitEnemy_Empty");
            if (boss == null || common == null) { if (!_poolLogged) { _poolLogged = true; _ctx.Log("общие враги: пулов врагов нет — не перекладываю"); } return; }
            if (Count(boss) >= 4) return;                                       // при 1 заготовке Create_UnitEnemyBoss уже не создаёт
            if (boss.GetType() != common.GetType()) { if (!_poolLogged) { _poolLogged = true; _ctx.Log($"общие враги: пулы разного типа ({boss.GetType().Name} / {common.GetType().Name}) — не перекладываю"); } return; }
            int n = Count(common);
            if (n < 20) return;
            var item = ItemAt(common, n - 1);
            CallMethod(common, "RemoveAt", n - 1);
            CallMethod(boss, "Add", item);
            if (!_poolLogged) { _poolLogged = true; _ctx.Log($"общие враги: пул боссов пуст — переложил врага из обычного пула (там {n})"); }
            _poolMoved++;
        }

        /// <summary>Враг пропал у хоста — копию убить уроном (гостю — убийство и выпадения); не вышло — убрать без награды.</summary>
        /// <summary>Живые враги по адресу — один раз на кадр, когда есть кого убить (раньше — перебор всех врагов на каждую копию).</summary>
        private readonly Dictionary<object, IEnemy> _aliveByKey = new Dictionary<object, IEnemy>();
        private void FillAliveByKey()
        {
            _aliveByKey.Clear();
            foreach (var e in AliveNow()) { var k = KeyOf(e.Raw); if (k != null) _aliveByKey[k] = e; }
        }

        private void KillPuppet(Puppet pp)
        {
            if (pp.Obj == null || pp.LocalDead) return;
            try
            {
                try { SetMember(pp.Obj, "Unit_Hp", 1); } catch { }                 // снять неуязвимость, иначе удар не убьёт
                if (_combat != null && pp.Key != null && _aliveByKey.TryGetValue(pp.Key, out var e) && e.Alive && _combat.Damage(e, 1e9)) { _wKilled++; return; }
                DestroySimple(pp.Obj, own: false);
            }
            catch (Exception e) { if (++_wFails <= 5) _ctx.Log($"общие враги: убить копию {pp.Id}: {Inner(e).Message}"); }
        }

        /// <summary>Свой спавн гостя: обнулить число врагов в пачке (разведка E) — игра может вернуть его на новой волне, поэтому раз в 0,5 с.</summary>
        private static readonly string[] SpawnFields = { "EnemyCreateNum", "EnemyCreateNum_Min", "EnemyCreateNum_Max" };
        private object[] _savedSpawn;
        private void SuppressSpawn(object mgr)
        {
            if (_savedSpawn == null) { _savedSpawn = new object[SpawnFields.Length]; for (int i = 0; i < SpawnFields.Length; i++) try { _savedSpawn[i] = GetMember(mgr, SpawnFields[i]); } catch { } }
            foreach (var f in SpawnFields)
                try { SetMember(mgr, f, 0); } catch { }
        }
        /// <summary>Общие враги выключились посреди боя — вернуть свой спавн (иначе до новой волны врагов не будет).</summary>
        private void RestoreSpawn()
        {
            var saved = _savedSpawn; _savedSpawn = null;
            if (saved == null || _battleMgr == null) return;
            for (int i = 0; i < SpawnFields.Length; i++)
                if (saved[i] != null) try { SetMember(_battleMgr, SpawnFields[i], saved[i]); } catch { }
        }

        /// <summary>Чистка: все живые враги гостя, которые не копии, — убрать без награды (разведка D).</summary>
        private void Sweep()
        {
            foreach (var e in AliveNow())
            {
                var key = KeyOf(e.Raw);
                if (key != null && !_puppetKeys.Contains(key) && !_heldKeys.Contains(key)) _toDestroy.Enqueue(e.Raw);
            }
        }

        // ------------------------------------------------------------ свой босс гостя (1.6.13)

        private readonly List<object> _held = new List<object>();
        private readonly HashSet<object> _heldKeys = new HashSet<object>();
        private readonly Queue<object> _heldNew = new Queue<object>();
        private int _heldKilled;

        /// <summary>Каждый кадр: новых спрятать (без ИИ, неуязвимы, далеко от героя); хост победил своего босса — убить уроном.</summary>
        private void HeldStep(double hx, double hy)
        {
            while (_heldNew.Count > 0)
            {
                _ctx.Mark("общие враги: прячу своего босса гостя");
                var e = _heldNew.Dequeue();
                try { CallMethod(e, "StopAllCoroutines"); } catch { }
                try { SetMember(GetMember(e, "Rigidbody_ObjUnit"), "isKinematic", true); } catch { }
                Invulnerable(e);
                _ctx.Log($"общие враги: своя босс-волна — прячу своего врага «{NameOf(e)}» тип {GetMember(e, "UnitID")} до победы хоста");
            }
            // 1.6.42: хост победил — не убиваем: смерть босса закончила бы этап у гостя и положила обелиск. Убираем без смерти,
            // когда хост поднимет обелиск (CatchUpBoss)
            if (_held.Count == 0) return;
            foreach (var e in _held)
                try { if (GetMember(e, "Transform_ObjUnit") is Transform tr) { var z = tr.position.z; tr.position = new Vector3((float)hx + 400f, (float)hy + 400f, z); } } catch { }
        }

        /// <summary>
        /// Общие враги выключились посреди босс-волны (мод выключен, хост пропал): спрятанный свой босс неуязвим и без ИИ — бой
        /// с боссом не кончился бы никогда. Убиваем его, как после победы хоста.
        /// </summary>
        private void ReleaseHeld()
        {
            _heldNew.Clear();
            if (_held.Count > 0) KillHeld("общие враги выключены");
        }

        private void KillHeld(string why)
        {
            _ctx.Mark("общие враги: убиваю своего босса гостя");
            foreach (var e in _held)
            {
                try
                {
                    if (!(GetMember(e, "Unit_IsLive") is bool live && live)) continue;
                    SetMember(e, "Unit_Hp", 1);
                    var key = KeyOf(e);
                    bool done = false;
                    if (_combat != null)
                        foreach (var en in _enemies.Alive)
                            if (Equals(KeyOf(en.Raw), key)) { done = _combat.Damage(en, 1e9); break; }
                    if (!done) CallMethod(e, "Unit_Destroy");
                    _heldKilled++;
                    _ctx.Log($"общие враги: {why} — свой враг «{NameOf(e)}» убит (бой с боссом у меня закончит игра)");
                }
                catch (Exception ex) { if (++_wFails <= 5) _ctx.Log("общие враги: убить своего босса: " + Inner(ex).Message); }
            }
            _held.Clear(); _heldKeys.Clear();
        }

        private void DestroySimple(object enemy, bool own = true)
        {
            try { CallMethod(enemy, "Unit_Destroy_Simple"); if (own) _wDestroyed++; }
            catch (Exception e) { if (++_wFails <= 5) _ctx.Log("общие враги: Unit_Destroy_Simple: " + Inner(e).Message); }
        }

        private void StopWorld(string why, bool touchObjects = true)
        {
            _ctx.Mark("общие враги: выключение — уборка копий, возврат спавна");
            if (touchObjects) ReleaseBossHold("общие враги выключены");   // дальше боссы гостя — его собственные
            if (touchObjects)
            {
                foreach (var pp in _puppets.Values.Concat(_dying)) if (pp.Obj != null && !pp.LocalDead) DestroySimple(pp.Obj, own: false);
                ReleaseHeld();
            }
            RestoreSpawn();
            _ctx.Log($"общие враги: выключено ({why}); {WorldSummary()}");
            _puppets.Clear(); _puppetKeys.Clear(); _toDestroy.Clear(); _dying.Clear();
            _worldActive = false; _worldHost = null;
        }

        private string WorldSummary() => _hosting
            ? $"отправлено врагов за раз до {_enSentMax}; урон гостей нанесён {_dmgIn:0} (врагов уже не было: {_dmgInMiss}); врагов от действий гостей создано {_spawnIn}" + SpecialSummary()
            : $"копий создано {_wCreated} (особых {_wSpecial}, одновременно до {_wMax}), убито по хосту {_wKilled}, погибло у себя {_wLocalDead} (создано заново {_wRespawned}), убрано своих врагов {_wDestroyed}, переложено в пул боссов {_poolMoved}, урона отправлено хосту {_dmgSent:0}, своих врагов отправлено хосту {_spawnSent}; " +
              $"убранные свои по типам: {string.Join(" ", System.Linq.Enumerable.Select(_sweptTypes, kv => $"{kv.Key}×{kv.Value}"))}; " +
              $"создание копии мс: макс {_createMsMax:0.0}, среднее {_createMsSum / Math.Max(1, _createN):0.00}; шаг мира мс: макс {_stepMsMax:0.0}; ошибок {_wFails}";

        private void LogWorldSummary()
        {
            if (!WorldOn || (_enSentMax == 0 && _wCreated == 0 && _wDestroyed == 0 && _dmgIn == 0)) return;
            _ctx.Log("общие враги за бой: " + WorldSummary() + ClockSummary());
        }

        // ------------------------------------------------------------ журнал: менеджер и устройство врага

        private static readonly System.Text.RegularExpressions.Regex EnemyWords = new System.Text.RegularExpressions.Regex("Enemy|Boss|Unit|Create|Spawn|Wave|Num", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>Числа, флаги и размеры коллекций менеджера, в имени которых Enemy/Boss/Unit/Create/Wave/Num.</summary>
        private static Dictionary<string, string> MgrSnapshot(object mgr)
        {
            var d = new Dictionary<string, string>();
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            foreach (var p in mgr.GetType().GetProperties(any))
            {
                if (p.GetIndexParameters().Length > 0 || p.Name.StartsWith("Native") || !EnemyWords.IsMatch(p.Name)) continue;
                try
                {
                    var t = p.PropertyType;
                    var v = p.GetValue(p.GetGetMethod(true)?.IsStatic == true ? null : mgr);
                    if (t.IsPrimitive || t.IsEnum) d[p.Name] = Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture);
                    else if (v != null && (GetMember(v, "Count") ?? GetMember(v, "Length")) is object c) d[p.Name] = "[" + c + "]";
                }
                catch { }
            }
            return d;
        }
        private string MgrDiff(object mgr)
        {
            var now = MgrSnapshot(mgr);
            var first = _mgrAtFirst ?? new Dictionary<string, string>();
            var parts = new List<string>();
            foreach (var kv in now)
                if (!first.TryGetValue(kv.Key, out var old) || old != kv.Value) parts.Add($"{kv.Key} {old ?? "?"} → {kv.Value}");
            return parts.Count == 0 ? "без изменений; всё: " + string.Join(", ", System.Linq.Enumerable.Take(now, 60)) : string.Join(", ", parts);
        }
        /// <summary>Методы врага про движение/поворот/анимацию и поля с картинками/аниматором — один раз.</summary>
        private static string EnemyShape(object enemy)
        {
            var rx = new System.Text.RegularExpressions.Regex("Move|Rotat|Motion|Anim|Sprite|Flip|Attack|Dead|Die|Hit|Destroy|Disappear", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            var t = enemy.GetType();
            var ms = new List<string>();
            foreach (var m in t.GetMethods(any))
                if (!m.IsSpecialName && !m.Name.StartsWith("Native") && rx.IsMatch(m.Name)) ms.Add($"{m.ReturnType.Name} {m.Name}/{m.GetParameters().Length}");
            var fs = new List<string>();
            foreach (var p in t.GetProperties(any))
                if (!p.Name.StartsWith("Native") && rx.IsMatch(p.Name)) fs.Add($"{p.PropertyType.Name} {p.Name}");
            return $"методы: {string.Join(", ", ms)}; поля: {string.Join(", ", fs)}";
        }

        // ------------------------------------------------------------ рефлексия

        private static readonly Dictionary<(Type, string, int), MethodInfo> MethodCache = new Dictionary<(Type, string, int), MethodInfo>();
        private static object CallMethod(object o, string name, params object[] args)
        {
            var t = o.GetType();
            MethodInfo m;
            lock (MethodCache)
                if (!MethodCache.TryGetValue((t, name, args.Length), out m))
                {
                    foreach (var x in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        if (x.Name == name && x.GetParameters().Length == args.Length) { m = x; break; }
                    MethodCache[(t, name, args.Length)] = m;
                }
            if (m == null) throw new MissingMethodException(t.Name + "." + name + "/" + args.Length);
            var ps = m.GetParameters();
            for (int i = 0; i < args.Length; i++)
                if (args[i] != null && !ps[i].ParameterType.IsInstanceOfType(args[i])) args[i] = Convert.ChangeType(args[i], ps[i].ParameterType);
            return m.Invoke(o, args);
        }

        private static void SetMember(object o, string name, object value)
        {
            if (o == null) return;
            var m = FindAny(o.GetType(), name);
            if (m == null) return;
            if (m.Prop != null && m.Prop.CanWrite)
                m.Prop.SetValue(m.Static ? null : o, value != null && value.GetType() == m.ValueType ? value : Convert.ChangeType(value, m.ValueType));
            else if ((m.Field ?? m.SetField) is FieldInfo f)
                f.SetValue(f.IsStatic ? null : o, value != null && value.GetType() == f.FieldType ? value : Convert.ChangeType(value, f.FieldType));
        }
    }
}
