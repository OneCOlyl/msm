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
        private const int MaxPuppetSpawnsPerFrameFast = 4;
        private const double PuppetSpawnBudgetMsFast = 5;
        private const int MaxPuppetKillsPerFrame = 4;
        private const double PuppetKillBudgetMs = 2;
        private const int PuppetKillManyQueue = 20;
        private const int MaxPuppetKillsPerFrameMany = 10;
        private const double PuppetKillBudgetMsMany = 5;
        private readonly Queue<Puppet> _dying = new Queue<Puppet>();

        private ICombat _combat;
        private bool WorldOn => _ctx.Setting("world") > 0;

        // ---- хост
        private readonly Dictionary<int, IEnemy> _enById = new Dictionary<int, IEnemy>();   // для урона гостей
        private double _dmgIn; private int _dmgInMiss;
        private Dictionary<object, (int id, int type, int sends)> _enIds = new Dictionary<object, (int, int, int)>(), _enIdsNext = new Dictionary<object, (int, int, int)>();
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
            public int Origin;                             // 1.6.65: чья корутина создала (OriginSpecial…)
            public float Size;                             // и размер (UnitApply_UnitSize)
            public float IconSize; public bool Back;       // 1.6.77: метка особого моба и обводка (Obj_UnitSprite_Back)
            public float[] BackColor;                      // 1.6.79: цвет обводки
            public object MainSr, BackSr, LastSprite;      // обводке каждый кадр — текущий кадр моба
            public float[] BackColorUse;
            public object Obj, Key;
            public Transform Tr;
            public bool LocalDead;
            public bool Placed;                            // мод уже ставил копию на место (после создания)
            public double DeadAt;                          // погибла у гостя (Армагеддон и т.п.) — когда; > 2 с и жива у хоста — создать заново
            public readonly List<(double t, double x, double y)> Track = new List<(double, double, double)>();
        }
        private readonly Dictionary<int, Puppet> _puppets = new Dictionary<int, Puppet>();
        private readonly List<Puppet> _toUpgrade = new List<Puppet>();          // копии, которые хост позже назвал особыми
        private readonly HashSet<object> _puppetKeys = new HashSet<object>();
        private readonly Queue<object> _toDestroy = new Queue<object>();
        private readonly List<int> _gone = new List<int>();
        private readonly Queue<(object obj, int type, float a, float b, int origin, double at)> _toForward = new Queue<(object, int, float, float, int, double)>();
        private readonly List<(int type, float a, float b, double x, double y, int origin, float[] stats)> _spawnOut = new List<(int, float, float, double, double, int, float[])>();
        private readonly HashSet<object> _forwardKeys = new HashSet<object>();          // свои переходы гостя ждут, пока игра выставит им силу
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
        private int _wCreated, _wKilled, _wLocalDead, _wDestroyed, _wFails, _wMax, _wRespawned, _wSpecial, _wReused;
        // копии у гостя неуязвимы: умирают, только когда умер враг у хоста (1.5.4). Unit_Hp — float: у 1e9 шаг 64 единицы и мелкий
        // урон терялся бы; у 1e6 — 0,06, а больше миллиона за 0,1 с не бьёт никто
        private const double PuppetHp = 1e6;
        private Dictionary<string, string> _mgrAtFirst;   // журнал 1.5.1: почему Create_UnitEnemyBoss перестаёт создавать врагов
        private bool _mgrFailLogged, _poolLogged;
        private int _poolMoved;
        // 1.6.59: гость отошёл от хоста дальше «worldZone» — у него свой спавн (враги игры гостя идут на него); вернулся ближе
        // (с запасом AwayHysteresis, чтобы не дёргалось на границе) — спавн снова погашен. Его враги, появившиеся вдали, — свои:
        // не убираются и не пересылаются хосту, добиваются магией гостя
        private const double AwayHysteresis = 2;
        private bool _away;
        // свой спавн включён: вдали и босс-волны нет ни у кого (1.6.62: в босс-волну спавн гостя — её мобы, а их мод прячет как
        // «своего босса» — 350 за волну: мобы исчезали на глазах, и волна лагала)
        private bool _ownSpawn;
        private readonly HashSet<object> _localKeys = new HashSet<object>();
        private int _awayTimes, _localSpawned, _awayLogSpawned;
        private double _awayLogAt;

        private void InstallWorldHooks()
        {
            try { _combat = _ctx.Use<ICombat>("modkit_combat"); } catch (Exception e) { _ctx.Log("общие враги: нет ICombat — урон гостей не дойдёт до врагов: " + e.Message); }
            try { _ctx.After("C_Battle_EnemyUnit", "Start_Unit", 3, Hook("синхронизация спавна врага", (self, a) => OnEnemyStart(self, a))); }
            catch (Exception e) { _ctx.Log("общие враги: хук Start_Unit недоступен (свой спавн гасится только чисткой): " + e.Message); }
            foreach (var (m, stopped) in new[] { ("Create_UnitEnemy_Stop", true), ("Create_UnitEnemy_Start", false), ("Create_UnitEnemy_StartLoop", false) })
                try { _ctx.After("C_Battle_Manager", m, 0, Hook("спавн игры: " + m, (self, a) => { _spawnStopped = stopped; })); }
                catch (Exception e) { _ctx.Log($"общие враги: хук {m} недоступен (свой спавн вдали от хоста после босса не перезапустится): " + e.Message); }
            InstallOriginHooks();
        }

        // ------------------------------------------------------------ 1.6.65: откуда враг (особые мобы)

        // Особых мобов игра создаёт тремя корутинами менеджера (разведка 27.10.2026): Create_UnitEnemySpecial (раз в 5 с, свой
        // пул List_BattleUnitEnemySpecial_Empty, SpecialVarB), Create_UnitEnemyEpic (пул List_BattleUnitEnemyEpic_Empty) и
        // Play_Create_UnitEnemy_Transition (подобранное усиление мобов). Start_Unit идёт внутри шага корутины — пока он идёт,
        // помним, чья это пачка. Особые и эпические — волны игры: у гостя они всегда только копии врагов хоста (и вдали от него);
        // переход — от действия игрока: у гостя уходит хосту и возвращается общим врагом.
        private const int OriginNormal = 0, OriginSpecial = 1, OriginEpic = 2, OriginTransition = 3;
        private static readonly string[] OriginNames = { "обычный", "особый", "эпический", "переход" };
        private int _spawnOrigin;
        private int _ownSpecialSwept, _ownTransitionSent, _wUpgraded;

        private void InstallOriginHooks()
        {
            Type t = null;
            try { t = Hooks.FindType("C_Battle_Manager"); } catch { }
            if (t == null) { _ctx.Log("общие враги: нет C_Battle_Manager — особые мобы не различаю"); return; }
            foreach (var nt in t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
            {
                int origin = nt.Name.Contains("Create_UnitEnemySpecial") ? OriginSpecial
                           : nt.Name.Contains("Create_UnitEnemyEpic") ? OriginEpic
                           : nt.Name.Contains("Play_Create_UnitEnemy_Transition") ? OriginTransition : OriginNormal;
                if (origin == OriginNormal) continue;
                var mi = nt.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (mi == null) continue;
                try
                {
                    // хуки не через контекст — снятие отдаём в OnCleanup (как в ItemDiag)
                    _ctx.OnCleanup(Hooks.HookBefore(_ctx.Id, mi, Hook("особые мобы: шаг корутины", (self, a) => _spawnOrigin = origin)));
                    _ctx.OnCleanup(Hooks.HookAfter(_ctx.Id, mi, Hook("особые мобы: шаг корутины", (self, a) => _spawnOrigin = OriginNormal)));
                }
                catch (Exception e) { _ctx.Log($"общие враги: хук {nt.Name} недоступен (особые мобы будут как обычные): " + e.Message); }
            }
        }

        private void ResetWorld()
        {
            _coSeen.Clear();
            _enIds.Clear(); _enIdsNext.Clear(); _enById.Clear(); _dmgIn = 0; _dmgInMiss = 0; _dmgOut.Clear(); _dmgSent = 0; _dmgFlushAt = 0; _enSentAt = -1; _enSentMax = 0; _enHostLogged = false;
            _puppets.Clear(); _puppetKeys.Clear(); _toDestroy.Clear(); _dying.Clear(); _ownerByKey.Clear(); _viewHalfX = 0; _viewLogAt = 0; _savedSpawn = null;
            _worldHost = null; _enSnap = 0; _enSnapAt = -100; _worldActive = false; _spawningPuppet = false; _justCreated = null;
            _wCreated = _wKilled = _wLocalDead = _wDestroyed = _wFails = _wMax = _wRespawned = _wSpecial = _wReused = 0;
            _enVars.Clear(); _enVarsNext.Clear();
            _toForward.Clear(); _forwardKeys.Clear(); _spawnOut.Clear(); _sweptTypes.Clear(); _specialSpawns.Clear(); _specialLogs = 0;
            _dumpSpecial = _dumpNormal = _specialCopy = null; _dumped = false; _specialCopyAt = 0;
            _held.Clear(); _heldKeys.Clear(); _heldNew.Clear(); _heldKilled = 0; _spawnSent = _spawnIn = _spawnInLogs = 0;
            _createMsMax = _createMsSum = _stepMsMax = 0; _createN = 0;
            _mgrAtFirst = null; _mgrFailLogged = false; _poolLogged = false; _poolMoved = 0; _bossPoolEst = -1;
            ResetGather(); _gatherTimes = 0;
            _away = _ownSpawn = false; _localKeys.Clear(); _awayTimes = _localSpawned = _onlyBossCleared = _awayLogSpawned = _spawnRestarts = 0; _awayLogAt = 0; _spawnStopped = false; _spawnRestartAt = 0;
            _spawnOrigin = OriginNormal; _ownSpecialSwept = _ownTransitionSent = _wUpgraded = _trLogs = _trAliveLogs = _peaceRemoved = 0; _destroyTried.Clear(); _commonChecks = 0; _hostMarked.Clear(); _hostMarkedLast.Clear(); _forwardKill.Clear(); Array.Clear(_forwardKillHow, 0, _forwardKillHow.Length); _toUpgrade.Clear(); _originDumped.Clear(); _originDumps.Clear(); _guestVisDumped = false;
        }

        private static object KeyOf(object o) { var p = PtrOf(o); return p != IntPtr.Zero ? (object)p : o; }

        /// <summary>
        /// Живые враги — один снимок на кадр. Библиотека на каждое обращение к Alive строит новый список и для каждого врага
        /// зовёт игру (activeInHierarchy, Unit_Hp); а им пользуются отправка (хост), чистка, повтор магии.
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
            bool nearest = alive.Count > MaxSentEnemies && OrderByGuests(alive, sx, sy);
            int total = nearest ? _sendOrder.Count : alive.Count;
            int n = 0;
            _enById.Clear();
            w.WriteStartArray("en");
            for (int i = 0; i < total; i++)          // список уже только из живых — e.Alive повторил бы те же вызовы игры
            {
                var e = nearest ? alive[_sendOrder[i].i] : alive[i];
                if (n >= MaxSentEnemies)
                {
                    // не попавшим в отправку — тот же номер: иначе в следующий раз у гостя убьётся старая копия и создастся новая
                    var k2 = KeyOf(e.Raw);
                    if (k2 != null && _enIds.TryGetValue(k2, out var v2)) _enIdsNext[k2] = v2;
                    continue;
                }
                var raw = e.Raw; var key = KeyOf(raw);
                if (key == null) continue;
                if (!_enIds.TryGetValue(key, out var v))
                {
                    int type = -1;
                    try { type = Convert.ToInt32(GetMember(raw, "UnitID") ?? -1); } catch { }
                    v = (_enNextId++, type, 0);
                }
                if (v.sends < 3) { v.sends++; NoteVarsFromFields(raw, key); }  // вид особого игра ставит после Start_Unit
                _enIdsNext[key] = v;
                _enById[v.id] = e;
                if (_specialSpawns.Count > 0) MarkSpecialSent(key);
                var p = nearest ? _sendOrder[i].pos : e.Pos;
                w.WriteStartArray();
                w.WriteNumberValue(v.id); w.WriteNumberValue(v.type);
                w.WriteNumberValue((int)Math.Round((p.x - sx) * 100)); w.WriteNumberValue((int)Math.Round((p.y - sy) * 100));
                if (_enVars.TryGetValue(key, out var vars)) { w.WriteNumberValue(vars.a); w.WriteNumberValue(vars.b); WriteLook(w, raw, vars.origin); _enVarsNext[key] = vars; }
                w.WriteEndArray();
                n++;
            }
            w.WriteEndArray();
            (_enIds, _enIdsNext) = (_enIdsNext, _enIds);
            _enIdsNext.Clear();
            if (n < MaxSentEnemies) { (_enVars, _enVarsNext) = (_enVarsNext, _enVars); _enVarsNext.Clear(); }   // мёртвых — забыть
            if (n > _enSentMax) _enSentMax = n;
        }

        /// <summary>
        /// Врагов больше MaxSentEnemies — отправить ближайших к друзьям (раньше — первые по списку игры: у гостя рядом с ним
        /// могло не быть врагов, которые у хоста видны). Ложь — друзей с позицией нет, порядок прежний.
        /// </summary>
        private readonly List<(int i, double d, Vector3 pos)> _sendOrder = new List<(int, double, Vector3)>();
        private readonly List<(double x, double y)> _guestPts = new List<(double, double)>();
        private bool OrderByGuests(IReadOnlyList<IEnemy> alive, double sx, double sy)
        {
            _guestPts.Clear();
            foreach (var p in _partners.Values) if (p.HasState && p.Battle) _guestPts.Add((sx + p.RelX, sy + p.RelY));
            if (_guestPts.Count == 0) return false;
            _sendOrder.Clear();
            for (int i = 0; i < alive.Count; i++)
            {
                Vector3 pos;
                try { pos = alive[i].Pos; } catch { continue; }
                double d = double.MaxValue;
                foreach (var g in _guestPts) { double dx = pos.x - g.x, dy = pos.y - g.y, dd = dx * dx + dy * dy; if (dd < d) d = dd; }
                _sendOrder.Add((i, d, pos));
            }
            _sendOrder.Sort((a, b) => a.d.CompareTo(b.d));
            return true;
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
                bool fresh = !_puppets.TryGetValue(id, out var pp);
                if (fresh) _puppets[id] = pp = new Puppet { Id = id, Type = type };
                if (it.GetArrayLength() >= 6 && it[4].TryGetSingle(out float va) && it[5].TryGetSingle(out float vb))
                {
                    // 1.6.65: хост узнаёт вид особого не сразу (игра ставит его после Start_Unit) — изменился, копию дообновляем
                    int cid = 0;
                    if (it.GetArrayLength() >= 11) it[6].TryGetInt32(out cid);
                    bool changed = !pp.Special || pp.VarA != va || pp.VarB != vb || pp.CreateId != cid;
                    if (changed)
                    {
                        pp.VarA = va; pp.VarB = vb; pp.Special = true; pp.CreateId = cid;
                        if (it.GetArrayLength() >= 11)
                        {
                            it[7].TryGetInt32(out pp.CreateEffect);
                            it[8].TryGetSingle(out pp.LineR); it[9].TryGetSingle(out pp.LineG); it[10].TryGetSingle(out pp.LineB);
                        }
                        if (it.GetArrayLength() >= 13) { it[11].TryGetInt32(out pp.Origin); it[12].TryGetSingle(out pp.Size); }
                        if (it.GetArrayLength() >= 15) { it[13].TryGetSingle(out pp.IconSize); pp.Back = it[14].TryGetInt32(out int bk) && bk != 0; }
                        if (it.GetArrayLength() >= 18) { pp.BackColor = new float[3]; it[15].TryGetSingle(out pp.BackColor[0]); it[16].TryGetSingle(out pp.BackColor[1]); it[17].TryGetSingle(out pp.BackColor[2]); }
                        if (pp.Origin < 0 || pp.Origin >= OriginNames.Length) pp.Origin = OriginNormal;
                        if (!fresh && pp.Obj != null && !pp.LocalDead && _toUpgrade.Count < 40) _toUpgrade.Add(pp);
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
            int origin = _spawnOrigin;
            if (_hosting && WorldOn && _inRun && self != null) { NoteHostSpawn(self, a, origin); NoteHostVars(self, a, origin); }
            if (!_worldActive || self == null) return;
            var lk = KeyOf(self);
            if (lk != null) { _forwardKeys.Remove(lk); _destroyTried.Remove(lk); _forwardKill.Remove(lk); }   // объект из пула стал новым врагом
            int type = -1; float va = 0, vb = 0;
            try { type = Convert.ToInt32(a[0]); va = Convert.ToSingle(a[1]); vb = Convert.ToSingle(a[2]); } catch { }
            if ((origin == OriginSpecial || origin == OriginEpic) && MyBossWave() == 0)
            {
                // особые и эпические мобы — волна игры: у хоста своя, гостю приходят её копии (и вдали от хоста тоже).
                // В свою босс-волну гостя — как раньше (её врагов прячет ObeliskSync)
                if (lk != null) _localKeys.Remove(lk);
                _toDestroy.Enqueue(self);
                if (_ownSpecialSwept++ < 5) _ctx.Log($"общие враги: свой {OriginNames[origin]} моб гостя (тип {type}, varA {va}, varB {vb}) убран — такие только от хоста");
                return;
            }
            if (MyBossWave() != 0)
            {
                // своя босс-волна гостя: её босса прячем, пока хост не поднимет обелиск, — тогда мод сам снимает волну
                // и убирает босса без смерти (ObeliskSync.cs)
                var hk = KeyOf(self);
                if (hk != null && !_heldKeys.Contains(hk)) { _heldKeys.Add(hk); _held.Add(self); _heldNew.Enqueue(self); }
                return;
            }
            // переход (усиление мобов подобрал гость) — его действие: и вдали от хоста уходит хосту, назад приходит общим врагом
            bool transition = origin == OriginTransition || va != 0 || vb != 0;
            if (_ownSpawn && !transition)
            {
                if (lk != null) _localKeys.Add(lk);                         // гость вдали от хоста — враг его собственный
                _localSpawned++;
                return;
            }
            if (lk != null) _localKeys.Remove(lk);                          // пул переиспользует объекты
            _sweptTypes[type] = _sweptTypes.TryGetValue(type, out var k) ? k + 1 : 1;
            // обычный спавн гостя погашен — значит, это враги от его действий (фиолетовая сфера и т.п.): создать у хоста.
            // Босс-волну не пересылаем — у хоста она своя.
            bool forward = _hostBossWave == 0 && MyBossWave() == 0 && _toForward.Count < 40;
            if (forward && transition && lk != null)
            {
                // силу мобу перехода (здоровье +10000, сопротивления, скорость, размер, обводку) корутина игры ставит после
                // Start_Unit (бой 27.10.2026: созданный у хоста одним Start_Unit(тип, 1, 0) — обычный, магия хоста убивала
                // его сразу). Моб живёт у гостя ещё TransitionReadDelay, потом его поля уходят хосту, а сам он убирается
                _forwardKeys.Add(lk);
                _toForward.Enqueue((self, type, va, vb, OriginTransition, Clock.Elapsed.TotalSeconds + TransitionReadDelay));
                _ownTransitionSent++;
                return;
            }
            _toDestroy.Enqueue(self);                                       // свой враг гостя — убрать на следующем кадре, не внутри Start_Unit
            if (forward) _toForward.Enqueue((self, type, va, vb, OriginNormal, 0));
        }

        private const double TransitionReadDelay = 0.3;

        // 1.6.84: свой моб перехода гостя, отправленный хосту, — убрать после его появления: Unit_Destroy_Simple, не вышло —
        // Unit_Disappear_Peace, не вышло — выключить объект (не виден и не в списке живых). Попытки — раз в 0,5 с (чистка)
        private const double ForwardKillDelay = 1.2;
        private readonly Dictionary<object, (double at, int tries)> _forwardKill = new Dictionary<object, (double, int)>();
        private readonly int[] _forwardKillHow = new int[4];

        private void ForwardKillStep(object enemy, object key, double now)
        {
            if (!_forwardKill.TryGetValue(key, out var k)) k = (now, 0);
            if (now < k.at) { HideAway(enemy); return; }
            int t = k.tries;
            _forwardKill[key] = (now, t + 1);
            try
            {
                if (t == 0) CallMethod(enemy, "Unit_Destroy_Simple");
                else if (t == 1) CallMethod(enemy, "Unit_Disappear_Peace");
                else SetActive(GetMember(enemy, "gameObject"), false);
            }
            catch (Exception ex) { if (++_wFails <= 5) _ctx.Log("общие враги: убрать свой переход: " + Inner(ex).Message); }
            if (t >= 1 && t <= 3) _forwardKillHow[t]++;
            if (t == 2 && _forwardKillHow[2] <= 3) _ctx.Log($"общие враги: свой переход «{NameOf(enemy)}» не убрали ни Unit_Destroy_Simple, ни Unit_Disappear_Peace — выключил объект");
        }

        /// <summary>Свой моб гостя — далеко за экран (там же, куда прячется свой босс, HeldStep), чтобы не мелькал до уборки.</summary>
        private void HideAway(object enemy)
        {
            try
            {
                if (!(GetMember(enemy, "Transform_ObjUnit") is Transform tr) || !HeroPosition(out var hx, out var hy, out _)) return;
                var z = tr.position.z; tr.position = new Vector3((float)hx + 400f, (float)hy + 400f, z);
            }
            catch { }
        }
        private int _trLogs, _trAliveLogs;

        private static bool IsTransitionMob(object enemy)
        {
            try { return Convert.ToInt32(GetMember(enemy, "UnitCreateID") ?? 1) == 3 || Convert.ToSingle(GetMember(enemy, "Unit_SpecialVarA") ?? 0f) != 0; }
            catch { return false; }
        }

        /// <summary>
        /// Поля, которыми моб перехода отличается от обычного того же типа (журнал 1.6.66: усиленный тип 16 — HpMax 11010,
        /// бонус 10000, сопротивления 0,5, отталкивание 0,85, скорость ×0,65, размер 1 против 0,8, красная обводка; копия,
        /// созданная одним Start_Unit(16, 1, 0), — HpMax 200, бонус 0). Порядок — часть протокола (массив «sf» события «dmg»).
        /// </summary>
        private static readonly string[] TransitionStats =
        {
            "UnitCreateID", "ApplyCreateEffectID", "Unit_SpecialVarA", "Unit_SpecialVarB", "LineColorR", "LineColorG", "LineColorB",
            "UnitBase_HpMax", "UnitApply_HpMax_Bonus", "Unit_HpMaxRatio_Bonus", "UnitApply_HpMax", "Unit_Hp",
            "UnitBase_DebuffResistance", "UnitBase_DeathAttackResistance", "UnitApply_DebuffResistance", "UnitApply_FreezeResistance",
            "UnitApply_StunResistance", "UnitApply_KnockbackResistance", "UnitApply_DeathAttackResistance",
            "ApplyMoveSpeedRatioUVar", "UnitBase_MoveSpeed", "BUnitApply_MoveSpeed", "UnitApply_MoveSpeed",
            "UnitBase_UnitSize", "BUnitApply_UnitSize", "UnitApply_UnitSize", "HitEffectTransVar", "MonsterIcon_Size",
        };

        private static float[] ReadStats(object enemy)
        {
            var v = new float[TransitionStats.Length];
            for (int i = 0; i < v.Length; i++)
                try { v[i] = Convert.ToSingle(GetMember(enemy, TransitionStats[i]) ?? float.NaN); } catch { v[i] = float.NaN; }
            return v;
        }

        /// <summary>
        /// Метка особого моба и обводка. 1.6.77: переход игры их включает сам (у своего перехода хоста MonsterIcon_Size 0,71 и
        /// Obj_UnitSprite_Back включён, у копии гостя — 0 и выключен). 1.6.79 (дерево объектов): обводка — тот же кадр моба
        /// в Sprite_UnitEnemySpecial_Back розовым (1; 0,05; 0,5), игра подставляет туда текущий кадр анимации, у копии кадра
        /// не было и цвет белый; метка — Sprite_UnitEnemySpecial_MonsterIcon с масштабом MonsterIcon_Size, у копии 0.
        /// Возвращает отрисовщики моба и обводки — обводке каждый кадр текущий кадр моба (SpecialMarksFrame).
        /// </summary>
        private static readonly float[] SpecialBackColor = { 1f, 0.05f, 0.5f, 1f };
        private (object main, object back, float[] color) ShowSpecialMarks(object enemy, float iconSize, bool back, float[] backColor)
        {
            if (iconSize > 0.01f)
                try
                {
                    SetMember(enemy, "MonsterIcon_Size", iconSize);
                    var icon = GetMember(enemy, "Obj_MonsterIcon") ?? ChildByName(GetMember(enemy, "gameObject"), "MonsterIcon");
                    if (icon != null)
                    {
                        SetActive(icon, true);
                        SetEnabled(GetComponentOf(icon), true);
                        if (GetMember(icon, "transform") is Transform it)
                        {
                            it.localScale = new Vector3(iconSize, iconSize, 1);
                            // 1.6.86: у копии метка в (0, 0, 0) — внутри тела моба, тело её закрывает; у хоста (0, PosY, -5)
                            float posY = 0.85f; try { posY = Convert.ToSingle(GetMember(enemy, "MonsterIcon_PosY") ?? posY); } catch { }
                            it.localPosition = new Vector3(0, posY, -5);
                        }
                    }
                }
                catch { }
            if (!back) return (null, null, null);
            try
            {
                var go = GetMember(enemy, "Obj_UnitSprite_Back");
                if (go == null) go = ChildByName(GetMember(enemy, "Obj_UnitSprite"), "_Back");
                if (go == null) return (null, null, null);
                SetActive(go, true);
                var backSr = GetComponentOf(go);
                SetEnabled(backSr, true);
                var mainSr = GetComponentOf(GetMember(enemy, "Obj_UnitSprite"));
                // хост шлёт вид при первой отправке, когда игра обводку ещё не перекрасила (белая) — тогда розовый, как у игры
                bool own = backColor != null && backColor.Length >= 3 && backColor[0] + backColor[1] + backColor[2] > 0.01f
                           && !(backColor[0] > 0.95f && backColor[1] > 0.95f && backColor[2] > 0.95f);
                var color = own ? new[] { backColor[0], backColor[1], backColor[2], 1f } : SpecialBackColor;
                SetBackColor(backSr, color);
                SetFillShader(backSr);
                if (mainSr != null && backSr != null) SetSprite(backSr, SpriteOf(mainSr));
                return (mainSr, backSr, color);
            }
            catch { return (null, null, null); }
        }

        /// <summary>
        /// 1.6.86: обводка у игры — шейдер «GUI/Text Shader» (силуэт заливается цветом). У копии — Sprites/Default: розовый
        /// умножается на чёрную картинку моба, и обводка не видна.
        /// </summary>
        private static object _fillShader;
        private static void SetFillShader(object sr)
        {
            if (sr == null) return;
            try
            {
                var mat = GetMember(sr, "material");
                var prop = mat?.GetType().GetProperty("shader");
                if (prop == null) return;
                _fillShader ??= prop.PropertyType.GetMethod("Find", new[] { typeof(string) })?.Invoke(null, new object[] { "GUI/Text Shader" });
                if (_fillShader != null) prop.SetValue(mat, _fillShader);
            }
            catch (Exception e) { if (!_backColorLogged) { _backColorLogged = true; _backColorLog = "обводка: шейдер не ставится: " + Inner(e).Message; } }
        }

        /// <summary>Ребёнок (до двух уровней) с этим кусочком в имени.</summary>
        private static object ChildByName(object go, string part)
        {
            if (go == null || !(GetMember(go, "transform") is Transform tr)) return null;
            for (int i = 0; i < tr.childCount; i++)
            {
                var ch = tr.GetChild(i);
                if (ch == null) continue;
                if ((NameOf(ch.gameObject) ?? "").Contains(part)) return ch.gameObject;
                for (int j = 0; j < ch.childCount; j++) { var g = ch.GetChild(j); if (g != null && (NameOf(g.gameObject) ?? "").Contains(part)) return g.gameObject; }
            }
            return null;
        }

        /// <summary>
        /// Обводке особого моба — текущий кадр анимации моба (копия игры этого не делает) и её цвет: игра перекрашивает
        /// обводку копии в белый (1.6.80: розовый, поставленный при создании, через секунду белый) — ставим со сменой кадра.
        /// </summary>
        private static void SpecialMarksFrame(object main, object back, ref object last, float[] color)
        {
            if (main == null || back == null) return;
            var sp = SpriteOf(main);
            if (sp == null || Equals(sp, last)) return;
            SetSprite(back, sp);
            if (color != null) SetBackColor(back, color);
            last = sp;
        }

        /// <summary>
        /// Цвет обводки — через свойство обёртки. Прямой вызов (SetColor, как у орбит) на этом отрисовщике цвет не ставил
        /// (журнал 1.6.81: «через свойство»), а после него у гостя появлялся синий моб — не зовём.
        /// </summary>
        private static bool _backColorLogged;
        private static string _backColorLog;
        private static void SetBackColor(object sr, float[] c)
        {
            if (sr == null || c == null) return;
            try { sr.GetType().GetProperty("color")?.SetValue(sr, new Color(c[0], c[1], c[2], c[3])); }
            catch (Exception e) { if (!_backColorLogged) { _backColorLogged = true; _backColorLog = "обводка: цвет не ставится: " + Inner(e).Message; } }
        }

        // хост: свои мобы перехода гостя — им обводку ведём сами
        private readonly List<(object obj, object main, object back, float[] color)> _hostMarked = new List<(object, object, object, float[])>();
        private readonly Dictionary<object, object> _hostMarkedLast = new Dictionary<object, object>();
        private void HostMarksStep()
        {
            for (int i = _hostMarked.Count - 1; i >= 0; i--)
            {
                var m = _hostMarked[i];
                bool live = false;
                try { live = LivePtr(m.back) != IntPtr.Zero && GetMember(m.obj, "Unit_IsLive") is bool b && b; } catch { }
                if (!live) { _hostMarkedLast.Remove(m.back); _hostMarked.RemoveAt(i); continue; }
                _hostMarkedLast.TryGetValue(m.back, out var last);
                SpecialMarksFrame(m.main, m.back, ref last, m.color);
                _hostMarkedLast[m.back] = last;
            }
        }

        private static void ApplyStats(object enemy, JsonElement stats)
        {
            int n = Math.Min(stats.GetArrayLength(), TransitionStats.Length);
            for (int i = 0; i < n; i++)
                if (stats[i].ValueKind == JsonValueKind.Number && stats[i].TryGetDouble(out double d))
                    try { SetMember(enemy, TransitionStats[i], d); } catch { }
        }

        // ------------------------------------------------------------ журнал 1.6.6: особые враги хоста (усиление мобов → «переход»)

        // Подобранное хостом усиление мобов запускает корутину Play_Create_UnitEnemy_Transition (Transition_EnemyMax = 15,
        // Transition_EnemyNum растёт), а у гостя эти враги не появились. Пишем, кто создаётся во время перехода и с какими
        // varA/varB, и попадает ли он потом в список врагов для гостя (_enemies.Alive).
        private readonly Dictionary<object, (int type, float a, float b, double at, bool sent, int origin)> _specialSpawns = new Dictionary<object, (int, float, float, double, bool, int)>();
        private int _specialLogs;

        // varA/varB, с которыми игра создала врага (Start_Unit): у усиленных мобов varA = 1 (журнал 1.6.6) — их копии у гостя
        // создаются с теми же varA/varB, иначе это обычные мобы того же типа.
        private Dictionary<object, (float a, float b, int origin)> _enVars = new Dictionary<object, (float, float, int)>(), _enVarsNext = new Dictionary<object, (float, float, int)>();
        private void NoteHostVars(object self, object[] a, int origin)
        {
            float va = 0, vb = 0;
            try { va = Convert.ToSingle(a[1]); vb = Convert.ToSingle(a[2]); } catch { }
            var key = KeyOf(self);
            if (key == null) return;
            int cid = 1;
            try { cid = Convert.ToInt32(GetMember(self, "UnitCreateID") ?? 1); } catch { }
            // особый: varA/varB или создан «переходом» (UnitCreateID 3 — красная обводка, журнал 1.6.10); пул объекты переиспользует
            // 1.6.65: и всё, что создали корутины особых/эпических мобов и перехода
            if (va != 0 || vb != 0 || cid == 3 || origin != OriginNormal) _enVars[key] = (va, vb, origin); else _enVars.Remove(key);
        }

        /// <summary>
        /// Хост (1.6.65): особый ли враг — по его полям. Переход игра создаёт Start_Unit(тип, 0, 0) и только потом ставит
        /// Unit_SpecialVarA = 1 и UnitCreateID = 3 (бой 27.10.2026: в хуке «создан 1», через секунду — 3); раньше такие
        /// уходили гостю обычными. Первые три отправки после появления врага.
        /// </summary>
        private void NoteVarsFromFields(object raw, object key)
        {
            float va = 0, vb = 0; int cid = 1;
            try { va = Convert.ToSingle(GetMember(raw, "Unit_SpecialVarA") ?? 0f); vb = Convert.ToSingle(GetMember(raw, "Unit_SpecialVarB") ?? 0f); cid = Convert.ToInt32(GetMember(raw, "UnitCreateID") ?? 1); }
            catch { return; }
            if (va == 0 && vb == 0 && cid != 3) return;
            bool had = _enVars.TryGetValue(key, out var old);
            if (had) { if (va == 0) va = old.a; if (vb == 0) vb = old.b; }       // поля только дополняют то, что знал хук
            int origin = had ? old.origin : OriginNormal;
            if (origin == OriginNormal && cid == 3) origin = OriginTransition;
            if (!had || old.a != va || old.b != vb || old.origin != origin) _enVars[key] = (va, vb, origin);
        }

        /// <summary>Хост: как выглядит особый враг — способ создания, эффект появления, цвет обводки, откуда и размер (читаем при отправке).</summary>
        private void WriteLook(Utf8JsonWriter w, object raw, int origin)
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
            w.WriteNumberValue(origin);
            float size = 0; try { size = Convert.ToSingle(GetMember(raw, "UnitApply_UnitSize") ?? 0f); } catch { }
            w.WriteNumberValue(Math.Round(size, 3));
            float icon = 0; try { icon = Convert.ToSingle(GetMember(raw, "MonsterIcon_Size") ?? 0f); } catch { }
            w.WriteNumberValue(Math.Round(icon, 3));
            bool back = false; try { back = GetMember(GetMember(raw, "Obj_UnitSprite_Back"), "activeSelf") is bool b && b; } catch { }
            w.WriteNumberValue(back ? 1 : 0);
            float[] bc = null; try { if (back) bc = ColorOf(GetComponentOf(GetMember(raw, "Obj_UnitSprite_Back"))); } catch { }
            for (int i = 0; i < 3; i++) w.WriteNumberValue(Math.Round(bc != null && bc.Length > i ? bc[i] : 0, 3));
        }

        // 1.6.10: у копий varA = 1 не делает мобов «усиленными» (у друга их не было видно) — что меняется в самом враге?
        // Через 1,5 с после появления — все простые поля первого особого и одного обычного врага того же боя.
        private object _dumpSpecial, _dumpNormal, _specialCopy;
        private double _specialCopyAt;
        private int _specialCopyOrigin;
        private double _dumpAt;
        private bool _dumped;

        private void DumpEnemiesStep()
        {
            for (int i = _originDumps.Count - 1; i >= 0; i--)
                if (Clock.Elapsed.TotalSeconds >= _originDumps[i].at && _originDumps[i].origin < 0)
                {
                    _ctx.Log("переход гостя у хоста, вид: " + VisualState(_originDumps[i].obj));
                    _originDumps.RemoveAt(i);
                }
                else if (Clock.Elapsed.TotalSeconds >= _originDumps[i].at)
                {
                    _ctx.Log($"{OriginNames[_originDumps[i].origin]} моб хоста: " + EnemyFields(_originDumps[i].obj));
                    _ctx.Log($"{OriginNames[_originDumps[i].origin]} моб хоста, вид: " + VisualState(_originDumps[i].obj));
                    _originDumps.RemoveAt(i);
                }
            if (_dumped || _dumpSpecial == null || Clock.Elapsed.TotalSeconds < _dumpAt) return;
            _dumped = true;
            if (_dumpNormal == null)
                foreach (var e in _enemies.Alive) { var k = KeyOf(e.Raw); if (k != null && !_specialSpawns.ContainsKey(k)) { _dumpNormal = e.Raw; break; } }
            _ctx.Log("усиленный моб (особый): " + EnemyFields(_dumpSpecial));
            if (_dumpNormal != null) _ctx.Log("обычный моб для сравнения: " + EnemyFields(_dumpNormal));
        }

        private bool _guestVisDumped;

        /// <summary>Метка особого моба и обводка: что включено (1.6.73 — у перехода гостя у хоста их нет).</summary>
        /// <summary>Всё дерево объекта врага: имя, включён, масштаб, картинка и цвет её отрисовщика (до глубины 4).</summary>
        private string DeepTree(object go, int depth = 0)
        {
            if (go == null || depth > 4) return "";
            var sb = new System.Text.StringBuilder();
            try
            {
                var tr = GetMember(go, "transform");
                var sr = GetComponentOf(go);
                string col = ""; try { col = sr == null ? "" : $" цвет {GetMember(sr, "color")}, слой {GetMember(sr, "sortingOrder")}, enabled {EnabledOf(sr)}"; } catch { }
                sb.Append($" | {new string('.', depth)}«{NameOf(go)}» {GetMember(go, "activeSelf")} {GetMember(tr, "localScale")} «{NameOf(SpriteOf(sr))}»{col}");
                int n = Convert.ToInt32(GetMember(tr, "childCount") ?? 0);
                var getChild = tr?.GetType().GetMethod("GetChild", new[] { typeof(int) });
                for (int i = 0; i < Math.Min(n, 10) && getChild != null; i++)
                    sb.Append(DeepTree(GetMember(getChild.Invoke(tr, new object[] { i }), "gameObject"), depth + 1));
            }
            catch (Exception ex) { sb.Append(" ошибка " + Inner(ex).Message); }
            return sb.ToString();
        }

        private string VisualState(object e)
        {
            string Go(object go) { if (go == null) return "нет"; try { return $"active {GetMember(go, "activeSelf")}/{GetMember(go, "activeInHierarchy")}"; } catch { return "?"; } }
            string Sr(object sr)
            {
                if (sr == null) return "нет";
                try { return $"enabled {GetMember(sr, "enabled")}, go {Go(GetMember(sr, "gameObject"))}, sprite «{NameOf(GetMember(sr, "sprite"))}», color {GetMember(sr, "color")}, mat «{NameOf(GetMember(sr, "sharedMaterial"))}»"; }
                catch (Exception ex) { return "ошибка " + Inner(ex).Message; }
            }
            var parts = new List<string>();
            try
            {
                parts.Add($"icon obj {Go(GetMember(e, "Obj_MonsterIcon"))}, icon sr {Sr(GetMember(e, "SpriteRenderer_MonsterIcon"))}, icon01 «{NameOf(GetMember(e, "Sprite_MonsterIcon01"))}», icon02 «{NameOf(GetMember(e, "Sprite_MonsterIcon02"))}»");
                parts.Add($"MonsterIcon_Size {GetMember(e, "MonsterIcon_Size")}, PosY {GetMember(e, "MonsterIcon_PosY")}, UnitCreateID {GetMember(e, "UnitCreateID")}, ApplyCreateEffectID {GetMember(e, "ApplyCreateEffectID")}, UColorID {GetMember(e, "UColorID")}, Line {GetMember(e, "LineColorR")}/{GetMember(e, "LineColorG")}/{GetMember(e, "LineColorB")}");
            }
            catch (Exception ex) { parts.Add("ошибка " + Inner(ex).Message); }
            try
            {
                if (GetMember(e, "Array_SpriteRendererUnitSpecialDummy") is object arr)
                {
                    int n = Convert.ToInt32(GetMember(arr, "Length") ?? 0);
                    var ix = arr.GetType().GetMethod("get_Item", new[] { typeof(int) });
                    for (int i = 0; i < n && i < 8; i++) parts.Add($"dummy[{i}] {Sr(ix?.Invoke(arr, new object[] { i }))}");
                }
            }
            catch (Exception ex) { parts.Add("dummy ошибка " + Inner(ex).Message); }
            try { parts.Add($"originalMaterial «{NameOf(GetMember(e, "originalMaterial"))}»"); } catch { }
            try { parts.Add("дерево:" + DeepTree(GetMember(e, "gameObject"))); } catch { }
            try
            {
                parts.Add($"unit {Go(GetMember(e, "gameObject"))}, sprite obj {Go(GetMember(e, "Obj_UnitSprite"))}, sprite back {Go(GetMember(e, "Obj_UnitSprite_Back"))}");
                parts.Add($"масштаб unit {GetMember(GetMember(e, "Transform_ObjUnit"), "localScale")}, sprite {GetMember(GetMember(e, "Transform_ObjUnitSprite"), "localScale")}, позиция {GetMember(GetMember(e, "Transform_ObjUnit"), "position")}");
                var sprTr = GetMember(e, "Transform_ObjUnitSprite");
                int cc = Convert.ToInt32(GetMember(sprTr, "childCount") ?? 0);
                parts.Add($"детей у спрайта {cc}");
                var gc = sprTr?.GetType().GetMethod("GetChild", new[] { typeof(int) });
                for (int i = 0; i < cc && i < 6 && gc != null; i++) { var ch = gc.Invoke(sprTr, new object[] { i }); parts.Add($"  [{i}] «{NameOf(ch)}» {Go(GetMember(ch, "gameObject"))} scale {GetMember(ch, "localScale")}"); }
            }
            catch (Exception ex) { parts.Add("unit ошибка " + Inner(ex).Message); }
            return string.Join("; ", parts);
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

        private void NoteHostSpawn(object self, object[] a, int origin)
        {
            int type = -1; float va = 0, vb = 0;
            try { type = Convert.ToInt32(a[0]); va = Convert.ToSingle(a[1]); vb = Convert.ToSingle(a[2]); } catch { }
            int trans = 0;
            try { trans = Convert.ToInt32(GetAny(_battleMgr, "Transition_EnemyNum") ?? 0); } catch { }
            if (va == 0 && vb == 0 && trans == 0 && origin == OriginNormal) return;
            var key = KeyOf(self);
            if (key == null || _specialSpawns.Count > 300) return;
            _specialSpawns[key] = (type, va, vb, Clock.Elapsed.TotalSeconds, false, origin);
            if (_specialLogs++ < 20) _ctx.Log($"особый враг хоста ({OriginNames[origin]}): тип {type}, varA {va}, varB {vb}, переход {trans}, «{NameOf(self)}», размер {GetMember(self, "UnitApply_UnitSize")}, создан {GetMember(self, "UnitCreateID")}");
            if (_dumpSpecial == null) { _dumpSpecial = self; _dumpAt = Clock.Elapsed.TotalSeconds + 1.5; }
            if (origin != OriginNormal && _originDumped.Add(origin)) _originDumps.Add((self, origin, Clock.Elapsed.TotalSeconds + 0.5));
        }
        // 1.6.65: по одному снимку полей на вид особых мобов (особый, эпический, переход) — сверить с копией у гостя
        private readonly HashSet<int> _originDumped = new HashSet<int>();
        private readonly List<(object obj, int origin, double at)> _originDumps = new List<(object, int, double)>();

        /// <summary>Попал ли особый враг в список для гостя — отмечаем при отправке.</summary>
        private void MarkSpecialSent(object key)
        {
            if (_specialSpawns.TryGetValue(key, out var v) && !v.sent) _specialSpawns[key] = (v.type, v.a, v.b, v.at, true, v.origin);
        }

        private string SpecialSummary() => _specialSpawns.Count == 0 ? "" :
            $"; особых врагов хоста {_specialSpawns.Count}, ушли гостю {_specialSpawns.Values.Count(v => v.sent)} " +
            $"(по типам: {string.Join(" ", _specialSpawns.Values.GroupBy(v => (v.type, v.a, v.b, v.origin)).Select(g => $"{g.Key.type}/{g.Key.a}/{g.Key.b}/{OriginNames[g.Key.origin]}×{g.Count()}"))})";

        // ============================================================ гость: каждый кадр

        private void WorldStep(double now)
        {
            if (_backColorLog != null) { _ctx.Log(_backColorLog); _backColorLog = null; }
            if (_hosting) { if (_hostMarked.Count > 0) HostMarksStep(); return; }
            var host = _worldHost;
            // 1.6.67: пока своя игра на паузе, WorldStep не идёт — это время не в счёт молчания хоста; и хост на паузе
            // (выбирает умение) врагов может не слать. Раньше на каждом выборе умения мир у гостя выключался и копии
            // создавались заново, а переход гостя в эти секунды оставался только у него
            if (_worldActive && now - _worldStepAt > 0.5) _enSnapAt = Math.Min(now, _enSnapAt + (now - _worldStepAt));
            _worldStepAt = now;
            bool hostQuiet = host != null && (host.Paused || host.OwnPause) && now - _enSnapAt < 60;
            bool want = WorldOn && !_hosting && host != null && _partners.ContainsKey(host.Id) && host.Battle && (now - _enSnapAt < 2 || hostQuiet) && _startX.HasValue;
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
            bool hasHero = HeroPosition(out var hhx, out var hhy, out _);
            UpdateAway(host, hasHero, hhx, hhy);
            GatherStep(host, now, hasHero, hhx, hhy);
            if (now >= _sweepAt)
            {
                _sweepAt = now + 0.5;
                _ctx.Mark("общие враги: гашу свой спавн и скорость копий");
                if (!_ownSpawn) SuppressSpawn(mgr);
                bool noBoss = MyBossWave() == 0 && _hostBossWave == 0;
                if (noBoss) ClearBossLeftovers(mgr);
                if (_away)
                {
                    if (_ownSpawn) RestartOwnSpawn(mgr, now);
                    if (now >= _awayLogAt)
                    {
                        if (_awayLogAt > 0)
                            _ctx.Log($"общие враги вдали: своих врагов за 10 с {_localSpawned - _awayLogSpawned}, живых своих {_localKeys.Count}; " +
                                     $"EnemyCreateNum {GetAny(mgr, "EnemyCreateNum")}/{GetAny(mgr, "EnemyCreateNum_Min")}/{GetAny(mgr, "EnemyCreateNum_Max")}, OnlyBossON {GetAny(mgr, "OnlyBossON")}, часы {MyClock()} с");
                        _awayLogAt = now + 10; _awayLogSpawned = _localSpawned;
                    }
                }
                Sweep();
                if (hasHero) UpdateViewHalf(now, hhx, hhy);
            }
            _world.Lap(0);
            while (_toForward.Count > 0 && _toForward.Peek().at <= now)
            {
                var f = _toForward.Dequeue();
                float[] stats = null;
                // 1.6.87: место — до HideAway, иначе хост создавал мобов перехода гостя за 400 от него
                Vector3? pos = null;
                try { pos = (GetMember(f.obj, "Transform_ObjUnit") as Transform).position; } catch { }
                if (f.origin == OriginTransition)
                {
                    stats = ReadStats(f.obj);
                    if (!float.IsNaN(stats[2])) f.a = stats[2];
                    if (!float.IsNaN(stats[3])) f.b = stats[3];
                    // Unit_Destroy_Simple моба перехода сразу не убирает (журнал 1.6.70: «жив True, IsDestroySimple False»), а
                    // остановка его корутин (1.6.71) замораживала его навсегда: у гостя стоял рогатый моб, которого у хоста нет
                    // (1.6.83). Теперь он уходит далеко за экран, а убирается после появления (_forwardKill). Ключ остаётся в
                    // _forwardKeys: второй раз не слать
                    HideAway(f.obj);
                    var fk = KeyOf(f.obj);
                    if (fk != null) _forwardKill[fk] = (now + ForwardKillDelay, 0);
                }
                if (pos.HasValue) _spawnOut.Add((f.type, f.a, f.b, pos.Value.x - _startX.Value, pos.Value.y - (_startY ?? 0), f.origin, stats));
                if (f.origin == OriginTransition && _trLogs < 40)
                {
                    _trLogs++;
                    _ctx.Log($"общие враги: свой переход хосту — тип {f.type}, varA {f.a}, создан {(stats != null ? stats[0] : -1)}, бонус здоровья {(stats != null ? stats[8] : -1)}, «{NameOf(f.obj)}»");
                }
            }
            if (_toDestroy.Count > 0)
            {
                _ctx.Mark("общие враги: убираю своих врагов гостя (Unit_Destroy_Simple)");
                while (_toDestroy.Count > 0) DestroySimple(_toDestroy.Dequeue());
            }

            _world.Lap(1);
            double rt = now - host.ClockOff - RenderDelay(host);
            double ox = _startX.Value, oy = _startY ?? 0;
            if (hasHero) HeldStep(hhx, hhy);
            _world.Lap(2);
            int made = 0, live = 0;
            double createMs = 0;
            bool puppetAi = _ctx.Setting("puppetAi") > 0;
            int waitOnScreen = 0;
            if (!hasHero) _viewHalfX = 0;
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
                        // создаём после прохода — сначала ближайших к герою (раньше — по порядку словаря: при очереди на создание
                        // у гостя рядом с ним было меньше врагов, чем у хоста)
                        double cd = 0;
                        if (hasHero && PuppetPos(pp, rt, out var cx, out var cy))
                        {
                            double dx = ox + cx - hhx, dy = oy + cy - hhy; cd = dx * dx + dy * dy;
                            if (_viewHalfX > 0 && Math.Abs(dx) * 1.3 < _viewHalfX && Math.Abs(dy) * 1.3 < _viewHalfY) { _viewWait++; waitOnScreen++; }
                        }
                        _toCreate.Add((cd, pp));
                        continue;
                    }
                    // объект погиб у гостя и пул уже отдал его другой копии (смерть видна раз в 6 кадров) — им двигали бы обе
                    if (pp.Key != null && _ownerByKey.TryGetValue(pp.Key, out var owner) && owner != pp)
                    {
                        pp.LocalDead = true; pp.DeadAt = now; _wLocalDead++; _wReused++;
                        continue;
                    }
                    if ((_worldFrame + pp.Id) % 6 == 0)
                    {
                        ReadUnitState(pp, out bool alive, out double hp);
                        if (!alive)
                        {
                            pp.LocalDead = true; pp.DeadAt = now; _wLocalDead++;   // погибла в обход здоровья (Армагеддон…) — подождём хоста
                            if (_wLocalDead <= 10 && pp.Tr != null)
                            {
                                var dp = pp.Tr.position;
                                _ctx.Log($"общие враги: копия {pp.Id} (тип {pp.Type}) погибла у себя — от героя ({dp.x - hhx:0.0}; {dp.y - hhy:0.0}), вдали от хоста {_away}, ставилась {pp.Placed}, здоровье {GetMember(pp.Obj, "Unit_Hp")}");
                            }
                            continue;
                        }
                        // урон гостя: здоровье копии держим на PuppetHp — разница и есть урон (любым заклинанием гостя)
                        if (hp < PuppetHp - 0.01)
                        {
                            _ctx.Mark("общие враги: здоровье копии");
                            _dmgOut[pp.Id] = (_dmgOut.TryGetValue(pp.Id, out var d0) ? d0 : 0) + (PuppetHp - hp);
                            Invulnerable(pp.Obj, pp.Key);
                            _ctx.Mark("общие враги: перемещение копий");
                        }
                        // игра пересчитывает скорость (усиления волн) — гасим снова; раньше всем копиям в одном кадре раз в 0,5 с:
                        // 6 записей на копию, у гостя (Pixel 3a) кадр «чистки» до 240 мс
                        if (puppetAi && (_worldFrame / 6 + pp.Id) % 5 == 0) ZeroSpeed(pp.Obj, pp.Key);
                        _world.Lap(4);
                    }
                    if (pp.Tr == null || !PuppetPos(pp, rt, out var x, out var y)) continue;
                    // за экраном — раз в 3 кадра: перестановка — два обращения к игре на копию (Terra, 29.10: ~1 мс на 175 копий)
                    if (_viewHalfX > 0 && Math.Abs(ox + x - hhx) * 1.3 < _viewHalfX && Math.Abs(oy + y - hhy) * 1.3 < _viewHalfY) _viewLive++;
                    if (pp.Placed && _viewHalfX > 0 && (Math.Abs(ox + x - hhx) > _viewHalfX || Math.Abs(oy + y - hhy) > _viewHalfY) && (_worldFrame + pp.Id) % 3 != 0) { live++; continue; }
                    PlacePuppet(pp, ox + x, oy + y);
                    live++;
                    _world.Lap(5);
                }
                catch (Exception e) { pp.LocalDead = true; if (++_wFails <= 5) _ctx.Log($"общие враги: копия {pp.Id}: {Inner(e).Message}"); }
            }
            _world.Lap(5);
            // бюджет кадра: создание копии — работа самой игры (журнал 1.5.7: у гостя лагало в начале волны). На экране ждут —
            // бюджет вдвое (Terra, 29.10: у гостя на экране 12–24 копии, ещё 25–36 ждали создания)
            bool fast = waitOnScreen > 0;
            if (fast) _fastFrames++;
            int maxMade = fast ? MaxPuppetSpawnsPerFrameFast : MaxPuppetSpawnsPerFrame;
            double budget = fast ? PuppetSpawnBudgetMsFast : PuppetSpawnBudgetMs;
            while (_toCreate.Count > 0 && made < maxMade && createMs <= budget)
            {
                int best = 0;
                for (int i = 1; i < _toCreate.Count; i++) if (_toCreate[i].d < _toCreate[best].d) best = i;
                var pp = _toCreate[best].pp;
                _toCreate[best] = _toCreate[_toCreate.Count - 1]; _toCreate.RemoveAt(_toCreate.Count - 1);
                made++;
                _ctx.Mark($"общие враги: создание копии врага (тип {pp.Type})");
                double t0 = _worldWatch.Elapsed.TotalMilliseconds;
                try
                {
                    // новая копия стоит там, где её поставила игра (обычно у героя), — на место сразу
                    if (CreatePuppet(mgr, pp) && pp.Tr != null && PuppetPos(pp, rt, out var x, out var y)) { PlacePuppet(pp, ox + x, oy + y); live++; }
                }
                catch (Exception e) { pp.LocalDead = true; if (++_wFails <= 5) _ctx.Log($"общие враги: копия {pp.Id}: {Inner(e).Message}"); }
                double ms = _worldWatch.Elapsed.TotalMilliseconds - t0;
                createMs += ms; _createMsSum += ms; _createN++; if (ms > _createMsMax) _createMsMax = ms; _createAllMs += ms;
            }
            _viewWaitAll += _toCreate.Count; _viewFrames++;
            if (now >= _viewLogAt)
            {
                if (_viewLogAt > 0 && _viewFrames > 0)
                    _ctx.Log($"общие враги за 10 с (на кадр): на экране копий {(double)_viewLive / _viewFrames:0.0}, ждут создания на экране {(double)_viewWait / _viewFrames:0.0}, ждут всего {(double)_viewWaitAll / _viewFrames:0.0}, создано {_wCreated - _viewCreated0}" +
                             $" по {_createAllMs / Math.Max(1, _wCreated - _viewCreated0):0.00} мс (пул {_createPreMs / Math.Max(1, _wCreated - _viewCreated0):0.00}, Create_UnitEnemyBoss {_createGameMs / Math.Max(1, _wCreated - _viewCreated0):0.00}, настройка {_createPostMs / Math.Max(1, _wCreated - _viewCreated0):0.00}), кадров с ускоренным созданием {_fastFrames}");
                _viewLogAt = now + 10; _viewLive = _viewWait = _viewWaitAll = _viewFrames = _fastFrames = 0; _viewCreated0 = _wCreated; _createGameMs = _createAllMs = _createPreMs = _createPostMs = 0;
            }
            _toCreate.Clear();
            _ctx.Mark("общие враги: перемещение копий");
            _world.Lap(3);
            for (int u = 0; u < 2 && _toUpgrade.Count > 0; u++)
            {
                var up = _toUpgrade[_toUpgrade.Count - 1]; _toUpgrade.RemoveAt(_toUpgrade.Count - 1);
                if (up.Obj == null || up.LocalDead || !_puppets.ContainsKey(up.Id)) continue;
                ApplySpecialLook(up, up.Obj);
                SetupPuppet(up.Obj, up.Key);
                _wUpgraded++;
            }
            if (live > _wMax) _wMax = live;
            if ((_dmgOut.Count > 0 || _spawnOut.Count > 0) && now >= _dmgFlushAt) FlushDamage(now);
            _world.Lap(7);
            if (_specialCopy != null && now >= _specialCopyAt) { _ctx.Log($"особая копия у гостя ({OriginNames[_specialCopyOrigin]}): " + EnemyFields(_specialCopy)); _ctx.Log($"особая копия у гостя ({OriginNames[_specialCopyOrigin]}), вид: " + VisualState(_specialCopy)); _specialCopy = null; _specialCopyAt = double.MaxValue; }
            double stepMs = _worldWatch.Elapsed.TotalMilliseconds;
            if (stepMs > _stepMsMax) _stepMsMax = stepMs;
            // враг погиб у хоста — копию из списка сразу (её больше не двигаем и не создаём заново), а убиваем по очереди: смерть
            // врага — работа игры (награда, эффекты), и пачка смертей в одном кадре давала у гостя кадр до 72 мс (1.6.19, телефоны)
            // не созданные (бюджет создания) и уже погибшие у гостя — в очередь не ставим: они занимали места в лимите убийств,
            // живые копии ждали за ними секундами (Terra, 29.10: в очереди 1309, живых из них ~20%)
            foreach (var id in _gone) { var pp = _puppets[id]; _puppets.Remove(id); if (pp.Obj != null && !pp.LocalDead) _dying.Enqueue(pp); }
            if (_dying.Count > 0)
            {
                _ctx.Mark("общие враги: убиваю копии (враг погиб у хоста)");
                double t0 = _worldWatch.Elapsed.TotalMilliseconds;
                // очередь после массовой смерти у хоста (Армагеддон) — бюджет больше: иначе копии стояли у гостя секундами
                bool many = _dying.Count > PuppetKillManyQueue;
                int maxKills = many ? MaxPuppetKillsPerFrameMany : MaxPuppetKillsPerFrame;
                double killBudget = many ? PuppetKillBudgetMsMany : PuppetKillBudgetMs;
                for (int k = 0; k < maxKills && _dying.Count > 0 && _worldWatch.Elapsed.TotalMilliseconds - t0 < killBudget; k++)
                    KillPuppet(_dying.Dequeue());
            }
            _world.Lap(6);
            _world.Count++;
        }

        private readonly List<(double d, Puppet pp)> _toCreate = new List<(double, Puppet)>();
        private int _viewLive, _viewWait, _viewWaitAll, _viewFrames, _viewCreated0, _fastFrames;
        private double _viewLogAt, _createGameMs, _createAllMs, _createPreMs, _createPostMs;
        private static double Ms(long a, long b) => (b - a) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

        // Поля врага (float, bool) — по смещению: через GetMember/SetMember это рефлексия и переход в IL2CPP на каждое обращение.
        // key — адрес объекта (KeyOf); не адрес (тесты) — рефлексия. Первое слово объекта IL2CPP — его класс. Объект держит
        // обёртка, так что память жива и у уничтоженного Unity
        private static readonly Dictionary<(IntPtr, string), int> FieldOffsets = new Dictionary<(IntPtr, string), int>();
        private static bool FieldAddr(object key, string name, out IntPtr addr)
        {
            addr = IntPtr.Zero;
            if (!(key is IntPtr p)) return false;
            var klass = System.Runtime.InteropServices.Marshal.ReadIntPtr(p);
            if (!FieldOffsets.TryGetValue((klass, name), out int off))
            {
                var f = Field(klass, name);
                FieldOffsets[(klass, name)] = off = f != IntPtr.Zero ? OffsetOf(f) : -1;
            }
            if (off < 0) return false;
            addr = p + off;
            return true;
        }
        private static float? UnitFloat(object obj, object key, string name) =>
            FieldAddr(key, name, out var a) ? BitConverter.Int32BitsToSingle(System.Runtime.InteropServices.Marshal.ReadInt32(a))
                                            : GetMember(obj, name) is object v ? Convert.ToSingle(v) : (float?)null;
        private static void SetUnitFloat(object obj, object key, string name, float v)
        {
            if (FieldAddr(key, name, out var a)) System.Runtime.InteropServices.Marshal.WriteInt32(a, BitConverter.SingleToInt32Bits(v));
            else SetMember(obj, name, v);
        }
        private static bool UnitAlive(object obj, object key) =>
            FieldAddr(key, "Unit_IsLive", out var a) ? System.Runtime.InteropServices.Marshal.ReadByte(a) != 0 : GetMember(obj, "Unit_IsLive") is bool b && b;
        private static void ReadUnitState(Puppet pp, out bool alive, out double hp)
        {
            alive = UnitAlive(pp.Obj, pp.Key);
            hp = UnitFloat(pp.Obj, pp.Key, "Unit_Hp") ?? PuppetHp;
        }

        private static void PlacePuppet(Puppet pp, double wx, double wy)
        {
            if (pp.BackSr != null) SpecialMarksFrame(pp.MainSr, pp.BackSr, ref pp.LastSprite, pp.BackColorUse);
            var cur = pp.Tr.position;
            pp.Tr.position = new Vector3((float)wx, (float)wy, cur.z);
            pp.Placed = true;
        }

        /// <summary>Полуразмеры экрана в единицах мира с запасом 30% — граница «копия за экраном».</summary>
        private double _viewHalfX, _viewHalfY;
        private bool _viewLogged;
        private void UpdateViewHalf(double now, double hx, double hy)
        {
            try
            {
                var cam = GameCamera(now);
                if (cam == null) { _viewHalfX = 0; return; }
                var a = cam.WorldToScreenPoint(new Vector3((float)hx, (float)hy, 0));
                var b = cam.WorldToScreenPoint(new Vector3((float)hx + 1, (float)hy, 0));
                double ppu = Math.Abs(b.x - a.x);
                if (ppu < 1e-3) { _viewHalfX = 0; return; }
                _viewHalfX = Screen.width / 2.0 / ppu * 1.3;
                _viewHalfY = Screen.height / 2.0 / ppu * 1.3;
                if (!_viewLogged) { _viewLogged = true; _ctx.Log($"общие враги: граница экрана для копий ±{_viewHalfX:0.0} × ±{_viewHalfY:0.0}"); }
            }
            catch { _viewHalfX = 0; }
        }

        /// <summary>Гость дальше «worldZone» от хоста (оба — от своих точек старта) — свой спавн включён, ближе — погашен.</summary>
        private void UpdateAway(Partner host, bool hasHero, double hx, double hy)
        {
            double r = _ctx.Setting("worldZone");
            bool was = _away;
            if (r <= 0 || !hasHero || !host.HasState || !_startX.HasValue) _away = false;
            else
            {
                double dx = hx - _startX.Value - host.RelX, dy = hy - (_startY ?? 0) - host.RelY;
                double d = Math.Sqrt(dx * dx + dy * dy);
                _away = _away ? d > r - AwayHysteresis : d > r;
            }
            bool boss = MyBossWave() != 0 || _hostBossWave != 0;
            bool own = _away && !boss;
            if (_away != was)
            {
                if (_away)
                {
                    _awayTimes++; _awayLogAt = 0;
                    _ctx.Log($"общие враги: отошёл от «{host.Name}» дальше {r:0} — {(boss ? "идёт босс-волна, свой спавн включится после неё" : "свой спавн включён")}");
                    _ctx.Toast("Далеко от хоста: враги идут на вас", 2);
                }
                else
                {
                    _ctx.Log($"общие враги: вернулся к «{host.Name}» — свой спавн погашен, своих врагов осталось {_localKeys.Count}");
                    _ctx.Toast("Рядом с хостом: общие враги", 2);
                }
            }
            else if (own != _ownSpawn && _away)
                _ctx.Log(own ? "общие враги: босс-волна кончилась — вдали от хоста свой спавн включён" : "общие враги: босс-волна — свой спавн погашен до её конца");
            if (own == _ownSpawn) return;
            _ownSpawn = own;
            if (own) RestoreSpawn();
            else _sweepAt = 0;                                              // погасить спавн в этом же кадре
        }

        /// <summary>
        /// Хвосты босс-волны гостя (1.6.61). Босс гостя в общих врагах не умирает — мод убирает его по обелиску хоста, и после
        /// волны у гостя оставались счётчик убийств волны (ApplyMonsterClearKillNum 2240, …Plus 1) и ярость босса
        /// (BossWave_SpeedRatio/SizeRatio ≈ 15, HitDamageRatio 0,9); у хоста после смерти босса — 0, 0, 0, 0, 1. С ними игра
        /// гостя ставит «только босс» (OnlyBossON 1) и каждый кадр зовёт Create_UnitEnemy_Stop: вдали от хоста своих врагов
        /// не было (телефоны 26.10.2026, 1.6.59–1.6.60; снятый один OnlyBossON игра возвращала тут же). Вне босс-волны —
        /// как у хоста; ApplyBossNum не трогаем (держит 1 ObeliskSync).
        /// </summary>
        private static readonly (string name, double value)[] BossLeftovers =
        {
            ("ApplyMonsterClearKillNum", 0), ("ApplyMonsterClearKillNumPlus", 0), ("BossWave_SpeedRatio", 0), ("BossWave_SizeRatio", 0),
            ("BossWave_HitDamageRatio", 1), ("OnlyBossON", 0),
        };
        private void ClearBossLeftovers(object mgr)
        {
            List<string> changed = null;
            foreach (var (name, value) in BossLeftovers)
                try
                {
                    var v = GetAny(mgr, name);
                    if (v == null || Math.Abs(Convert.ToDouble(v) - value) < 1e-4) continue;
                    SetMember(mgr, name, value);
                    (changed ??= new List<string>()).Add($"{name} {Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)} → {value}");
                }
                catch { }
            if (changed != null && _onlyBossCleared++ < 10)
                _ctx.Log($"общие враги: хвосты босс-волны сняты (раз {_onlyBossCleared}, часы {MyClock()} с): {string.Join(", ", changed)}");
        }
        private int _onlyBossCleared;

        // спавн игры гостя остановлен (Create_UnitEnemy_Stop — босс-волна, «только босс»): сама игра запускает его снова только
        // на следующем шаге волн (до 30 с) — вдали от хоста запускаем сами; второй Start при идущей корутине удвоил бы спавн
        private bool _spawnStopped;
        private double _spawnRestartAt;
        private int _spawnRestarts;
        private void RestartOwnSpawn(object mgr, double now)
        {
            if (!_spawnStopped || now < _spawnRestartAt) return;
            _spawnRestartAt = now + 2;
            try
            {
                CallMethod(mgr, "Create_UnitEnemy_Start");
                _spawnStopped = false; _spawnRestarts++;
                _ctx.Log("общие враги: свой спавн игры был остановлен — запустил снова (Create_UnitEnemy_Start)");
            }
            catch (Exception e) { if (++_wFails <= 5) _ctx.Log("общие враги: запустить свой спавн: " + Inner(e).Message); }
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
                    w.WriteStartArray("so");                                // 1.6.65: откуда каждый (3 — переход)
                    foreach (var s in _spawnOut) w.WriteNumberValue(s.origin);
                    w.WriteEndArray();
                    w.WriteStartArray("sf");                                // 1.6.66: поля моба перехода (TransitionStats), без них — []
                    foreach (var s in _spawnOut)
                    {
                        w.WriteStartArray();
                        if (s.stats != null)
                            foreach (var v in s.stats) { if (float.IsNaN(v) || float.IsInfinity(v)) w.WriteNullValue(); else w.WriteNumberValue(Math.Round(v, 4)); }
                        w.WriteEndArray();
                    }
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
            e.TryGetProperty("so", out var so);
            e.TryGetProperty("sf", out var sf);
            for (int i = 0; i + 4 < n && i < 5 * 10; i += 5)
            {
                int origin = 0;
                if (so.ValueKind == JsonValueKind.Array && i / 5 < so.GetArrayLength()) so[i / 5].TryGetInt32(out origin);
                if (!s[i].TryGetInt32(out int type) || type < 0) continue;
                double x = s[i + 3].GetDouble(), y = s[i + 4].GetDouble();
                var bossProp = FindAny(_battleMgr.GetType(), "ApplyBossNum")?.Prop;
                object bossNum = null;
                _ctx.Mark($"общие враги: создаю врага от гостя (тип {type})");
                _spawningPuppet = true; _justCreated = null;
                object commonItem = null;
                try
                {
                    try { bossNum = bossProp?.GetValue(null); } catch { }
                    RefillBossPool(_battleMgr);
                    commonItem = PutPrefabFirst(_battleMgr, origin);          // заготовка из пула особых — с меткой и обводкой
                    CallMethod(_battleMgr, "Create_UnitEnemyBoss", type, 1);
                }
                catch (Exception ex) { if (++_wFails <= 5) _ctx.Log($"общие враги: враг гостя тип {type}: {Inner(ex).Message}"); }
                finally { _spawningPuppet = false; if (bossNum != null) { try { bossProp.SetValue(null, bossNum); } catch { } } }
                var obj = _justCreated; _justCreated = null;
                CheckCommonUsed(commonItem, obj);
                if (obj == null) continue;
                float sa = 0, sb = 0;
                try { sa = s[i + 1].GetSingle(); sb = s[i + 2].GetSingle(); } catch { }
                if (sa != 0 || sb != 0 || origin == OriginTransition)
                {
                    _spawningPuppet = true;                                     // особый враг гостя (усиление мобов у гостя): как у игры
                    try
                    {
                        if (origin == OriginTransition) SetMember(obj, "UnitCreateID", 3);   // вид «перехода» игра ставит от способа создания
                        CallMethod(obj, "Start_Unit", type, sa, sb);
                        if (origin == OriginTransition) SetMember(obj, "UnitCreateID", 3);
                    }
                    catch { }
                    finally { _spawningPuppet = false; _justCreated = null; }
                    if (sf.ValueKind == JsonValueKind.Array && i / 5 < sf.GetArrayLength() && sf[i / 5].ValueKind == JsonValueKind.Array && sf[i / 5].GetArrayLength() > 0)
                    {
                        ApplyStats(obj, sf[i / 5]);                             // сила моба перехода — как у гостя
                        var marks = ShowSpecialMarks(obj, Convert.ToSingle(GetMember(obj, "MonsterIcon_Size") ?? 0f), true, null);
                        if (marks.back != null && _hostMarked.Count < 100) _hostMarked.Add((obj, marks.main, marks.back, marks.color));
                        if (_spawnInLogs < 40) _ctx.Log($"общие враги: моб перехода гостя — здоровье {GetMember(obj, "Unit_Hp")}/{GetMember(obj, "UnitApply_HpMax")}, бонус {GetMember(obj, "UnitApply_HpMax_Bonus")}, размер {GetMember(obj, "UnitApply_UnitSize")}");
                    }
                    NoteHostVars(obj, new object[] { type, sa, sb }, origin);
                }
                try { var tr = GetMember(obj, "Transform_ObjUnit") as Transform; var z = tr.position.z; tr.position = new Vector3((float)((_startX ?? 0) + x), (float)((_startY ?? 0) + y), z); } catch { }
                FixSpriteScale(obj, KeyOf(obj));
                _spawnIn++;
                if (_spawnInLogs++ < 40) _ctx.Log($"общие враги: враг от действий гостя — тип {type}, varA {s[i + 1]}, varB {s[i + 2]}, создан у меня");
                if (origin == OriginTransition && !_guestVisDumped) { _guestVisDumped = true; _originDumps.Add((obj, -1, Clock.Elapsed.TotalSeconds + 0.5)); }
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
            long c0 = System.Diagnostics.Stopwatch.GetTimestamp();
            var bossProp = FindAny(mgr.GetType(), "ApplyBossNum")?.Prop;
            object bossNum = null;
            object commonItem = null;
            _spawningPuppet = true; _justCreated = null;
            try
            {
                try { bossNum = bossProp?.GetValue(null); } catch { }
                RefillBossPool(mgr);
                if (pp.Special) commonItem = PutPrefabFirst(mgr, pp.Origin);  // особой копии — заготовка из пула особых (метка, обводка)
                long g0 = System.Diagnostics.Stopwatch.GetTimestamp();
                _createPreMs += Ms(c0, g0);
                CallMethod(mgr, "Create_UnitEnemyBoss", pp.Type, 1);     // синхронно создаёт обычного врага этого типа (разведка A1)
                c0 = System.Diagnostics.Stopwatch.GetTimestamp();
                _createGameMs += Ms(g0, c0);
            }
            catch (Exception e) { pp.LocalDead = true; if (++_wFails <= 5) _ctx.Log($"общие враги: создать тип {pp.Type}: {Inner(e).Message}"); return false; }
            finally
            {
                _spawningPuppet = false;
                if (bossNum != null) { try { bossProp.SetValue(null, bossNum); } catch { } }   // побочный счётчик боссов — вернуть
            }
            var obj = _justCreated; _justCreated = null;
            CheckCommonUsed(commonItem, obj);
            if (obj == null)
            {
                pp.LocalDead = true;
                _bossPoolEst = -1;
                if (++_wFails <= 5) _ctx.Log($"общие враги: тип {pp.Type} не создался");
                if (!_mgrFailLogged) { _mgrFailLogged = true; _ctx.Log($"общие враги: менеджер при первой неудаче (было при первой копии → сейчас): {MgrDiff(mgr)}"); }
                return false;
            }
            if (_mgrAtFirst == null)
            {
                _mgrAtFirst = MgrSnapshot(mgr);
                _ctx.Log($"общие враги: копия — размер {GetMember(obj, "UnitApply_UnitSize")}, масштаб {GetMember(GetMember(obj, "transform"), "localScale")}, спрайт-масштаб до подгонки {GetMember(GetMember(obj, "Transform_ObjUnitSprite"), "localScale")}");
            }
            pp.Obj = obj; pp.Placed = false; pp.Key = KeyOf(obj); _puppetKeys.Add(pp.Key);
            if (pp.Key != null) _ownerByKey[pp.Key] = pp;
            if (pp.Special) ApplySpecialLook(pp, obj);
            pp.Tr = GetMember(obj, "Transform_ObjUnit") as Transform;
            SetupPuppet(obj, pp.Key);
            _createPostMs += Ms(c0, System.Diagnostics.Stopwatch.GetTimestamp());
            if (_wCreated++ == 0) _ctx.Log($"общие враги: первая копия — тип {pp.Type}, «{NameOf(obj)}», спрайт-масштаб после подгонки {GetMember(GetMember(obj, "Transform_ObjUnitSprite"), "localScale")}, здоровье {GetMember(obj, "Unit_Hp")}");
            return true;
        }

        /// <summary>
        /// Особый враг (усиленный): игра создаёт его «переходом» (UnitCreateID 3) и Start_Unit(тип, varA, varB) — повторяем
        /// на копии; способ создания — до Start_Unit (от него игра выставляет вид), эффект, обводку и размер — после.
        /// </summary>
        private void ApplySpecialLook(Puppet pp, object obj)
        {
            _spawningPuppet = true;
            _ctx.Mark($"общие враги: особая копия — Start_Unit (тип {pp.Type})");
            try
            {
                if (pp.CreateId > 0) SetMember(obj, "UnitCreateID", pp.CreateId);
                CallMethod(obj, "Start_Unit", pp.Type, pp.VarA, pp.VarB);
                if (pp.CreateId > 0) SetMember(obj, "UnitCreateID", pp.CreateId);
                SetMember(obj, "ApplyCreateEffectID", pp.CreateEffect);
                SetMember(obj, "LineColorR", pp.LineR); SetMember(obj, "LineColorG", pp.LineG); SetMember(obj, "LineColorB", pp.LineB);
                if (pp.Size > 0.01f) SetMember(obj, "UnitApply_UnitSize", pp.Size);   // картинку под размер подгонит FixSpriteScale
                (pp.MainSr, pp.BackSr, pp.BackColorUse) = ShowSpecialMarks(obj, pp.IconSize, pp.Back, pp.BackColor);
                pp.LastSprite = null;
                _wSpecial++;
                if (_specialCopy == null && (_wSpecial == 1 || _wSpecial == 20 || pp.Origin != OriginNormal && _originDumped.Add(pp.Origin))) { _specialCopy = obj; _specialCopyOrigin = pp.Origin; _specialCopyAt = Clock.Elapsed.TotalSeconds + 0.5; }
            }
            catch (Exception e) { if (++_wFails <= 5) _ctx.Log($"общие враги: особая копия {pp.Type}/{pp.VarA}/{pp.VarB}: {Inner(e).Message}"); }
            finally { _spawningPuppet = false; _justCreated = null; }
        }

        /// <summary>Копия: картинка под размер, неуязвима, без своей скорости, тело кинематическое (и после повторного Start_Unit).</summary>
        private void SetupPuppet(object obj, object key)
        {
            // 1.5.0 останавливал все корутины — вместе с ИИ вставали кадры ходьбы (их меняет Unit_Move). Теперь ИИ работает
            // (анимация, поворот, атаки), но скорость нулевая — двигает только мод. «puppetAi» = 0 — по-старому.
            _ctx.Mark("общие враги: настройка копии (здоровье, скорость, тело)");
            FixSpriteScale(obj, key);
            Invulnerable(obj, key);
            if (_ctx.Setting("puppetAi") > 0) ZeroSpeed(obj, key);
            else try { CallMethod(obj, "StopAllCoroutines"); } catch { }
            try { SetMember(GetMember(obj, "Rigidbody_ObjUnit"), "isKinematic", true); } catch { }   // соседи не расталкивают
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
        private static void FixSpriteScale(object enemy, object key)
        {
            try
            {
                float size = UnitFloat(enemy, key, "UnitApply_UnitSize") ?? 0f;
                if (size <= 0.01f) return;
                var tr = GetMember(enemy, "Transform_ObjUnitSprite") as Transform;
                if (tr == null) return;
                var ls = tr.localScale;
                tr.localScale = new Vector3(ls.x < 0 ? -size : size, size, ls.z == 0 ? 1 : ls.z);
            }
            catch { }
        }

        private void Invulnerable(object enemy, object key)
        {
            try { SetUnitFloat(enemy, key, "Unit_Hp", (float)PuppetHp); }
            catch (Exception e) { if (++_wFails <= 5) _ctx.Log("общие враги: здоровье копии: " + Inner(e).Message); }
        }

        private static void ZeroSpeed(object enemy, object key)
        {
            foreach (var f in SpeedFields) try { SetUnitFloat(enemy, key, f, 0f); } catch { }
        }

        /// <summary>
        /// Create_UnitEnemyBoss берёт врага из пула боссов (14 штук), а погибшие возвращаются в обычный пул (журнал 1.5.1:
        /// List_BattleUnitEnemyBoss_Empty 14 → 1, List_BattleUnitEnemy_Empty 451 → 455) — перекладываем из обычного.
        /// </summary>
        /// <summary>
        /// 1.6.74: у заготовок пула боссов и обычного пула нет метки особого моба и обводки (у перехода гостя у хоста
        /// «icon obj нет», у своего перехода хоста — есть, и при переходе хоста пул List_BattleUnitEnemySpecial_Empty 80 → 66):
        /// особые мобы игры — из своего пула. Особого врага создаём из заготовки того же пула: кладём её туда, откуда
        /// Create_UnitEnemyBoss берёт (сначала — в начало списка; не взял — дальше в конец).
        /// </summary>
        private object PutPrefabFirst(object mgr, int origin)
        {
            var boss = GetAny(mgr, "List_BattleUnitEnemyBoss_Empty");
            var common = GetAny(mgr, origin == OriginEpic ? "List_BattleUnitEnemyEpic_Empty" : "List_BattleUnitEnemySpecial_Empty");
            if (boss == null || common == null || boss.GetType() != common.GetType()) return null;
            int n = Count(common);
            if (n < 3) return null;
            var item = ItemAt(common, n - 1);
            CallMethod(common, "RemoveAt", n - 1);
            if (_commonAtEnd) CallMethod(boss, "Add", item); else CallMethod(boss, "Insert", 0, item);
            return item;
        }
        private bool _commonAtEnd;
        private int _commonChecks;

        private void CheckCommonUsed(object item, object created)
        {
            if (item == null) return;
            object go = null; try { go = created == null ? null : GetMember(created, "gameObject"); } catch { }
            bool used = go != null && Equals(KeyOf(go), KeyOf(item));
            if (_commonChecks++ < 3 || !used)
                if (_commonChecks < 10) _ctx.Log($"общие враги: особый враг из заготовки пула особых — {(used ? "взята" : "не взята")} ({(_commonAtEnd ? "в конце" : "в начале")} пула боссов)");
            if (!used && !_commonAtEnd) _commonAtEnd = true;
        }

        private void RefillBossPool(object mgr)
        {
            if (_bossPoolEst >= 4) { _bossPoolEst--; return; }
            var boss = GetAny(mgr, "List_BattleUnitEnemyBoss_Empty");          // статические поля (1.5.2 искал только обычные — не находил)
            var common = GetAny(mgr, "List_BattleUnitEnemy_Empty");
            if (boss == null || common == null) { if (!_poolLogged) { _poolLogged = true; _ctx.Log("общие враги: пулов врагов нет — не перекладываю"); } return; }
            int nb = Count(boss);
            if (nb >= 4) { _bossPoolEst = nb - 1; return; }                    // при 1 заготовке Create_UnitEnemyBoss уже не создаёт
            if (boss.GetType() != common.GetType()) { if (!_poolLogged) { _poolLogged = true; _ctx.Log($"общие враги: пулы разного типа ({boss.GetType().Name} / {common.GetType().Name}) — не перекладываю"); } return; }
            int n = Count(common);
            if (n < 20) { _bossPoolEst = -1; return; }
            if (!_poolLogged) { _poolLogged = true; _ctx.Log($"общие враги: пул боссов пуст — переложил врага из обычного пула (там {n})"); }
            for (; nb < 8 && n >= 20; nb++, n--)
            {
                var item = ItemAt(common, n - 1);
                CallMethod(common, "RemoveAt", n - 1);
                CallMethod(boss, "Add", item);
                _poolMoved++;
            }
            _bossPoolEst = nb - 1;
        }
        // сколько заготовок в пуле боссов по нашему счёту (каждая копия берёт одну): чтение пулов — пять обращений к игре
        // через рефлексию, раньше перед каждой копией. -1 — не знаем, прочитать
        private int _bossPoolEst = -1;

        /// <summary>Враг пропал у хоста — копию убить уроном (гостю — убийство и выпадения); не вышло — убрать без награды.</summary>
        // 1.6.95: копия в очереди убийств, а её объект уже погиб у гостя (Армагеддон…) и пул игры отдал его новой копии —
        // убивали новую (у хоста враг жив): она пропадала и через 2 с создавалась снова
        private readonly Dictionary<object, Puppet> _ownerByKey = new Dictionary<object, Puppet>();

        private void KillPuppet(Puppet pp)
        {
            if (pp.Obj == null || pp.LocalDead) return;
            if (pp.Key == null || !_ownerByKey.TryGetValue(pp.Key, out var owner) || owner != pp) { _wReused++; return; }
            _ownerByKey.Remove(pp.Key);
            try
            {
                // сразу Unit_Death, без Unit_Damage (эффекты попадания, статусы, расчёт урона): при Unit_Hp <= 0 и Unit_IsLive игра
                // засчитывает убийство и награду так же (телефон, 30.10: на каждый вызов +1 Game_KillMonsterNum и событие enemyDeath)
                if (UnitAlive(pp.Obj, pp.Key))
                {
                    SetUnitFloat(pp.Obj, pp.Key, "Unit_Hp", 0);
                    CallMethod(pp.Obj, "Unit_Death");
                    _wKilled++;
                    return;
                }
                DestroySimple(pp.Obj, own: false);
            }
            catch (Exception e) { if (++_wFails <= 5) _ctx.Log($"общие враги: убить копию {pp.Id}: {Inner(e).Message}"); }
        }

        /// <summary>Свой спавн гостя: обнулить число врагов в пачке (разведка E) — игра может вернуть его на новой волне, поэтому раз в 0,5 с.</summary>
        private static readonly string[] SpawnFields = { "EnemyCreateNum", "EnemyCreateNum_Min", "EnemyCreateNum_Max" };
        private object[] _savedSpawn;
        private void SuppressSpawn(object mgr)
        {
            bool first = _savedSpawn == null;
            if (first) _savedSpawn = new object[SpawnFields.Length];
            for (int i = 0; i < SpawnFields.Length; i++)
                try
                {
                    // новое значение игры (новая волна) — запомнить его: вернём спавн гостю вдали от хоста таким, как сейчас у игры
                    var v = GetMember(mgr, SpawnFields[i]);
                    if (first || (v != null && Convert.ToDouble(v) != 0)) _savedSpawn[i] = v;
                    SetMember(mgr, SpawnFields[i], 0);
                }
                catch { }
        }
        /// <summary>Общие враги выключились посреди боя — вернуть свой спавн (иначе до новой волны врагов не будет).</summary>
        private void RestoreSpawn()
        {
            var saved = _savedSpawn; _savedSpawn = null;
            if (saved == null || _battleMgr == null) return;
            for (int i = 0; i < SpawnFields.Length; i++)
                if (saved[i] != null) try { SetMember(_battleMgr, SpawnFields[i], saved[i]); } catch { }
        }

        /// <summary>Чистка: все живые враги гостя, которые не копии и не свои (появились вдали от хоста), — убрать без награды (разведка D).</summary>
        private readonly HashSet<object> _localAlive = new HashSet<object>();
        private double _worldStepAt;

        private void Sweep()
        {
            _localAlive.Clear();
            double now = Clock.Elapsed.TotalSeconds;
            foreach (var e in AliveNow())
            {
                var key = KeyOf(e.Raw);
                if (key != null && _forwardKeys.Contains(key) && !_toForward.Any(f => Equals(KeyOf(f.obj), key)))
                {
                    ForwardKillStep(e.Raw, key, now);                       // отправлен хосту, у гостя ещё жив — убрать (без награды)
                    continue;
                }
                if (key == null || _puppetKeys.Contains(key) || _heldKeys.Contains(key) || _forwardKeys.Contains(key)) continue;
                // свой моб перехода гостя, которого не поймал Start_Unit (появился, пока общий мир был выключен), — тоже хосту
                if (_hostBossWave == 0 && MyBossWave() == 0 && _toForward.Count < 40 && IsTransitionMob(e.Raw))
                {
                    int type = -1; try { type = Convert.ToInt32(GetMember(e.Raw, "UnitID") ?? -1); } catch { }
                    _localKeys.Remove(key); _forwardKeys.Add(key);
                    _toForward.Enqueue((e.Raw, type, 0f, 0f, OriginTransition, now));
                    _ownTransitionSent++;
                    continue;
                }
                if (_ownSpawn || _localKeys.Contains(key)) { _localAlive.Add(key); continue; }   // вдали — все свои враги гостя его
                if (_destroyTried.Contains(key))
                {
                    try { CallMethod(e.Raw, "Unit_Disappear_Peace"); _peaceRemoved++; }
                    catch (Exception ex) { if (++_wFails <= 5) _ctx.Log("общие враги: Unit_Disappear_Peace: " + Inner(ex).Message); }
                    continue;
                }
                _toDestroy.Enqueue(e.Raw);
            }
            _localKeys.Clear(); _localKeys.UnionWith(_localAlive);           // погибшие свои — забыть (объект уйдёт в пул)
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
                Invulnerable(e, KeyOf(e));
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
            if (own) { var k = KeyOf(enemy); if (k != null && _destroyTried.Count < 2000) _destroyTried.Add(k); }
        }

        // 1.6.71: свои враги гостя, которым уже звали Unit_Destroy_Simple, — пережил (моб перехода во время появления, журнал
        // 1.6.70) — чистка убирает его Unit_Disappear_Peace (без награды)
        private readonly HashSet<object> _destroyTried = new HashSet<object>();
        private int _peaceRemoved;

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
            _puppets.Clear(); _puppetKeys.Clear(); _toDestroy.Clear(); _dying.Clear(); _ownerByKey.Clear(); _viewHalfX = 0; _toUpgrade.Clear(); _toForward.Clear(); _forwardKeys.Clear();
            _worldActive = false; _worldHost = null; _away = _ownSpawn = false; _localKeys.Clear(); ResetGather();
        }

        private string WorldSummary() => _hosting
            ? $"отправлено врагов за раз до {_enSentMax}; урон гостей нанесён {_dmgIn:0} (врагов уже не было: {_dmgInMiss}); врагов от действий гостей создано {_spawnIn}" + SpecialSummary()
            : $"копий создано {_wCreated} (особых {_wSpecial}, одновременно до {_wMax}), убито по хосту {_wKilled}, погибло у себя {_wLocalDead} (создано заново {_wRespawned}), убрано своих врагов {_wDestroyed}, объект у другой копии {_wReused}, переложено в пул боссов {_poolMoved}, урона отправлено хосту {_dmgSent:0}, своих врагов отправлено хосту {_spawnSent}; " +
              $"уходил от хоста {_awayTimes} раз, переносов к хосту на босса {_gatherTimes}, своих врагов вдали {_localSpawned}, сняты хвосты босс-волны {_onlyBossCleared}, перезапусков спавна {_spawnRestarts}; " +
              $"копий стали особыми позже {_wUpgraded}, свои переходы убраны со 2-й/3-й попытки {_forwardKillHow[1]}/{_forwardKillHow[2]}, своих убрано Unit_Disappear_Peace {_peaceRemoved}, своих особых/эпических убрано {_ownSpecialSwept}, своих переходов отправлено хосту {_ownTransitionSent}; " +
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

        private static readonly Dictionary<(Type, string, int), (MethodInfo m, ParameterInfo[] ps)> MethodCache = new Dictionary<(Type, string, int), (MethodInfo, ParameterInfo[])>();
        private static object CallMethod(object o, string name, params object[] args)
        {
            var t = o.GetType();
            (MethodInfo m, ParameterInfo[] ps) c;
            lock (MethodCache)
                if (!MethodCache.TryGetValue((t, name, args.Length), out c))
                {
                    foreach (var x in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                        if (x.Name == name && x.GetParameters().Length == args.Length) { c = (x, x.GetParameters()); break; }
                    MethodCache[(t, name, args.Length)] = c;
                }
            var (m, ps) = c;
            if (m == null) throw new MissingMethodException(t.Name + "." + name + "/" + args.Length);
            for (int i = 0; i < args.Length; i++)
                if (args[i] != null && !ps[i].ParameterType.IsInstanceOfType(args[i])) args[i] = Convert.ChangeType(args[i], ps[i].ParameterType);
            return m.Invoke(o, args);
        }

        /// <summary>Перегрузка с одним string (у StartCoroutine/StopCoroutine есть и IEnumerator — CallMethod взял бы любую).</summary>
        private static object CallByString(object o, string name, string arg)
        {
            var m = o.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null)
                    ?? throw new MissingMethodException(o.GetType().Name + "." + name + "(string)");
            return m.Invoke(o, new object[] { arg });
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
