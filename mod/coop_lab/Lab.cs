using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using ModKit;

namespace MagicSurvivalLab
{
    public class LabMod : IKitMod
    {
        public ModDef Define() => new ModDef
        {
            Id = "coop_lab",
            Name = "Кооператив: полигон",
            Version = typeof(LabMod).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion,
            Description = "Для разработки: в бою кнопка «ПОЛИГОН» — спокойная арена, любые мобы, босс-волна, обелиск и другие механики. Игрокам не нужен.",
            Depends = { ["modkit_combat"] = ">=1.0 <2", ["modkit_hero"] = ">=1.0 <2" },
            OnEnable = ctx => Lab.Current = new Lab(ctx),
            OnDisable = ctx => { Lab.Current?.Stop(); Lab.Current = null; },
        };
    }

    public sealed class Lab
    {
        public static Lab Current;

        private static readonly System.Globalization.CultureInfo Inv = System.Globalization.CultureInfo.InvariantCulture;
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private const int MaxType = 50;                     // Z_Database.Dictionary_UnitStat: 51 запись
        private const int ObeliskItem = 18;
        private static readonly int[] Counts = { 1, 5, 20 };
        private static readonly string[] SpawnFields = { "EnemyCreateNum", "EnemyCreateNum_Min", "EnemyCreateNum_Max" };
        private static readonly double[] Gray = { 0.25, 0.25, 0.30, 0.92 };
        private static readonly double[] Green = { 0.18, 0.50, 0.25, 0.95 };
        private static readonly double[] Red = { 0.60, 0.20, 0.20, 0.95 };
        private static readonly double[] Blue = { 0.20, 0.40, 0.80, 0.95 };

        private readonly ModContext _ctx;
        private readonly IEnemies _enemies;
        private readonly IHero _hero;
        private object _mgr;
        private bool _inBattle;

        private bool _noSpawn, _immortal, _clockHeld, _noMana;
        private object[] _savedSpawn;
        private int _heldClock;
        private int _type = 1, _countIdx, _item = 1;
        private object _justCreated;
        private bool _creating;

        private UiKit.HudButton _toggle;
        private readonly List<UiKit.HudButton> _panel = new List<UiKit.HudButton>();
        private int _tab = -1;

        public Lab(ModContext ctx)
        {
            _ctx = ctx;
            _enemies = ctx.Use<IEnemies>("modkit_combat");
            _hero = ctx.Use<IHero>("modkit_hero");
            ctx.OnCleanup(Stop);
            try { ctx.Before("C_Battle_Manager", "Update", 0, (s, a) => { if (s != null) _mgr = s; }); }
            catch (Exception e) { ctx.Log("полигон: менеджер боя не ловится: " + e.Message); }
            try { ctx.After("C_Battle_EnemyUnit", "Start_Unit", 3, (s, a) => { if (_creating) _justCreated = s; }); }
            catch (Exception e) { ctx.Log("полигон: хук Start_Unit недоступен: " + e.Message); }
            // «Сдаться» обнуляет здоровье героя и ждёт обычной смерти, а бессмертие её пропускает
            foreach (var button in new[] { "Button_GameGiveUp_ON", "Button_GameExit_ON" })
                try { ctx.Before("C_Battle_Manager", button, 0, (s, a) => ctx.Guard("полигон: выход из боя", LeaveBattle)); }
                catch (Exception e) { ctx.Log($"полигон: хук {button} недоступен: " + e.Message); }
            ctx.Events.On("battleStart", _ => ctx.Guard("полигон: начало боя", OnBattleStart));
            ctx.Events.On("battleEnd", _ => ctx.Guard("полигон: конец боя", OnBattleEnd));
            ctx.Every(100, () => ctx.Guard("полигон: шаг", Step));
            ctx.Log("полигон 0.1.11 включён: в бою кнопка «ПОЛИГОН»");
        }

        public void Stop()
        {
            SetImmortal(false);
            SetMana(true);
            SetSpawn(true);
            ClosePanel();
            Kill(ref _toggle);
        }

        private void OnBattleStart()
        {
            _inBattle = true;
            _noSpawn = _clockHeld = false;
            _savedSpawn = null;
            ShowToggle();
        }

        private void LeaveBattle()
        {
            if (_immortal) _ctx.Log("полигон: выход из боя, " + SetImmortal(false));
        }

        private void OnBattleEnd()
        {
            _inBattle = false;
            SetImmortal(false);
            SetMana(true);
            _noSpawn = _clockHeld = false;
            _savedSpawn = null;
            ClosePanel();
            Kill(ref _toggle);
        }

        private int _stepN;
        private void Step()
        {
            if (!_inBattle || _mgr == null) return;
            if (_clockHeld) SetClock(_heldClock);
            if (_immortal) KeepAlive();
            if (_noMana && _levelMgr != null) PassLevelMana();
            if (_noSpawn && _stepN++ % 5 == 0) SuppressSpawn();   // Новая волна возвращает число врагов в пачке
        }

        // Арена

        // Выключенный спавн держится до конца боя: каждая волна ставит число врагов заново.
        public string SetSpawn(bool on)
        {
            if (on)
            {
                _noSpawn = false;
                if (_savedSpawn != null && _mgr != null)
                    for (int i = 0; i < SpawnFields.Length; i++)
                        if (_savedSpawn[i] != null) try { SetMember(_mgr, SpawnFields[i], _savedSpawn[i]); } catch { }
                _savedSpawn = null;
                return "спавн игры включён";
            }
            _noSpawn = true;
            SuppressSpawn();
            return "спавн игры выключен";
        }

        private void SuppressSpawn()
        {
            if (_mgr == null) return;
            bool first = _savedSpawn == null;
            if (first) _savedSpawn = new object[SpawnFields.Length];
            for (int i = 0; i < SpawnFields.Length; i++)
                try
                {
                    var v = GetMember(_mgr, SpawnFields[i]);
                    if (first || (v != null && Convert.ToDouble(v) != 0)) _savedSpawn[i] = v;
                    SetMember(_mgr, SpawnFields[i], 0);
                }
                catch { }
        }

        private readonly List<Action> _immortalHooks = new List<Action>();
        public string SetImmortal(bool on)
        {
            if (on == _immortal) return on ? "бессмертие уже включено" : "бессмертие уже выключено";
            if (on)
            {
                try { Hooks.AddReplace(_ctx.Id, "C_Battle_AllyUnit", "Unit_Death", 0, (self, args) => null, _immortalHooks); }
                catch (Exception e) { return "бессмертие: " + Inner(e).Message; }
            }
            else
            {
                foreach (var u in _immortalHooks) { try { u(); } catch { } }
                _immortalHooks.Clear();
            }
            _immortal = on;
            return on ? "бессмертие включено" : "бессмертие выключено";
        }

        private readonly List<Action> _manaHooks = new List<Action>();
        private object _levelMgr;
        private double _levelMana;

        // Без опыта не открываются окна выбора магии, которые ставят игру на паузу у всех игроков.
        public string SetMana(bool on)
        {
            if (on == !_noMana) return on ? "опыт уже включён" : "опыт уже выключен";
            if (!on)
            {
                try { HookMana(); }
                catch (Exception e) { return "опыт: " + Inner(e).Message; }
            }
            else UnhookMana();
            _noMana = !on;
            return on ? "опыт включён" : "опыт выключен";
        }

        private void HookMana() => Hooks.AddReplace(_ctx.Id, "V_Ability_Manager", "ManaGain", 1, (self, args) =>
        {
            double amount = Convert.ToDouble(args[0]);
            if (amount >= DbNum("AllyUnitGlobal_ManaCost") - DbNum("AllyUnitGlobal_ManaStock")) { _levelMgr = self; _levelMana = amount; }
            return null;
        }, _manaHooks);

        private void UnhookMana()
        {
            foreach (var u in _manaHooks) { try { u(); } catch { } }
            _manaHooks.Clear();
        }

        private void PassLevelMana()
        {
            var mgr = _levelMgr; double amount = _levelMana;
            _levelMgr = null;
            var m = mgr.GetType().GetMethod("ManaGain", Any);
            if (m == null) return;
            UnhookMana();
            try { m.Invoke(mgr, new[] { Convert.ChangeType(amount, m.GetParameters()[0].ParameterType) }); }
            catch (Exception e) { _ctx.Log("полигон: мана на уровень: " + Inner(e).Message); }
            finally { HookMana(); }
        }

        // Пропуск Unit_Death не даёт герою умереть, но здоровье остаётся 0, и моды по Hp ≤ 0 считают его погибшим.
        private void KeepAlive()
        {
            try { if (_hero.Hp < _hero.HpMax * 0.5) _hero.Heal(_hero.HpMax); } catch { }
        }

        public string HoldClock(bool on)
        {
            _clockHeld = on;
            if (on) _heldClock = Math.Max(0, MyClock());
            return on ? $"часы стоят на {Clock(_heldClock)}" : "часы идут";
        }

        // Порог босса (3:00, 6:00…) игра ловит только ровно на нём.
        public string ShiftClock(int sec)
        {
            int c = MyClock();
            if (c < 0) return "часы не прочитать";
            int to = Math.Max(0, c + sec);
            SetClock(to);
            if (_clockHeld) _heldClock = to;
            return $"часы {Clock(c)} → {Clock(to)}";
        }

        // 2:30 на Terra, иначе каждые 3 минуты.
        public string ToBoss()
        {
            int c = MyClock();
            if (c < 0) return "часы не прочитать";
            int area = DbInt("Game_AreaID");
            int next = area == 14 && c < 150 ? 150 : (c / 180 + 1) * 180;
            return ShiftClock(next - 5 - c);
        }

        public string ClearEnemies()
        {
            int n = 0;
            foreach (var e in _enemies.Alive.ToList())
                try { CallMethod(e.Raw, "Unit_Destroy_Simple"); n++; } catch { }
            return $"убрано врагов: {n}";
        }

        // При Unit_Hp 0 Unit_Death засчитывает убийство, как удар магией.
        public string KillEnemies()
        {
            int n = 0;
            foreach (var e in _enemies.Alive.ToList())
                try { SetMember(e.Raw, "Unit_Hp", 0f); CallMethod(e.Raw, "Unit_Death"); n++; } catch { }
            return $"убито врагов: {n}";
        }

        // Мобы

        // Заготовки боссов нужны боссам, а у босса на обычной заготовке Start_Unit падает.
        public string Spawn(int type, int count)
        {
            if (_mgr == null) return "не в бою";
            count = Math.Max(1, Math.Min(50, count));
            int made = 0, boss = 0;
            for (int i = 0; i < count; i++)
            {
                if (CreateOne(type, true)) made++;
                else if (CreateOne(type, false)) { made++; boss++; }
            }
            var r = $"тип {type}: создано {made} из {count}" + (boss > 0 ? $" (на заготовке босса {boss})" : "");
            _ctx.Log("полигон: " + r);
            return r;
        }

        private bool CreateOne(int type, bool commonPrefab)
        {
            var bossProp = FindProp(_mgr.GetType(), "ApplyBossNum");
            object bossNum = null;
            try { bossNum = bossProp?.GetValue(null); } catch { }
            _creating = true; _justCreated = null;
            try
            {
                RefillBossPool();
                if (commonPrefab) PutCommonFirst();
                CallMethod(_mgr, "Create_UnitEnemyBoss", type, 1);
            }
            catch (Exception e) { _ctx.Log($"полигон: тип {type}: {Inner(e).Message}"); }
            finally
            {
                _creating = false;
                if (bossNum != null) try { bossProp.SetValue(null, bossNum); } catch { }   // Иначе игра ждёт смерти «босса»
            }
            bool ok = _justCreated != null;
            _justCreated = null;
            return ok;
        }

        private void RefillBossPool()
        {
            var boss = GetMember(_mgr, "List_BattleUnitEnemyBoss_Empty");
            var common = GetMember(_mgr, "List_BattleUnitEnemy_Empty");
            if (boss == null || common == null || boss.GetType() != common.GetType()) return;
            int nb = Count(boss), n = Count(common);
            for (; nb < 8 && n >= 20; nb++, n--)
            {
                var item = ItemAt(common, n - 1);
                CallMethod(common, "RemoveAt", n - 1);
                CallMethod(boss, "Add", item);
            }
        }

        private void PutCommonFirst()
        {
            var boss = GetMember(_mgr, "List_BattleUnitEnemyBoss_Empty");
            var common = GetMember(_mgr, "List_BattleUnitEnemy_Empty");
            if (boss == null || common == null || boss.GetType() != common.GetType()) return;
            int n = Count(common);
            if (n < 20) return;
            var item = ItemAt(common, n - 1);
            CallMethod(common, "RemoveAt", n - 1);
            CallMethod(boss, "Insert", 0, item);
        }

        public string SpawnEpic(int type, int count)
        {
            if (_mgr == null) return "не в бою";
            foreach (var p in new[] { "", "Apply" })
            {
                TrySet(_mgr, p + "CreateEpicEnemyID", type);
                TrySet(_mgr, p + "CreateEpicEnemyNum", count);
                TrySet(_mgr, p + "CreateEpicEnemyDelay", 0.3f);
            }
            return StartCoroutine("Create_UnitEnemyEpic") ? $"эпические: тип {type} ×{count}" : "эпические: корутина не запустилась";
        }

        // Особых игра создаёт сама, сколько разрешит волна.
        public string SpawnSpecial(int count)
        {
            if (_mgr == null) return "не в бою";
            TrySet(_mgr, "SuperEnemyCreateNum", count);
            TrySet(_mgr, "SuperEnemyCreateDelay_Num", 99);
            return $"особые: попросил {count}";
        }

        public string Transition()
        {
            if (_mgr == null) return "не в бою";
            try { CallMethod(_mgr, "Create_UnitEnemy_Transition"); return "переход запущен"; }
            catch (Exception e) { return "переход: " + Inner(e).Message; }
        }

        // Механики

        public string BossWave() => _mgr == null ? "не в бою" : StartCoroutine("Play_BossWaveEffect_Start") ? "босс-волна запущена" : "босс-волна: корутина не запустилась";

        public string Item(int id, int count)
        {
            if (_mgr == null) return "не в бою";
            try { CallMethod(_mgr, "CreateItem_Character", id, Math.Max(1, count)); return $"предмет {id} ×{count}"; }
            catch (Exception e) { return $"предмет {id}: " + Inner(e).Message; }
        }

        public string Obelisk() => Item(ObeliskItem, 1);

        public string AreaChange()
        {
            if (_mgr == null) return "не в бою";
            try { CallMethod(_mgr, "Start_AreaChange"); return "смена области"; }
            catch (Exception e) { return "смена области: " + Inner(e).Message; }
        }

        public string PullItems()
        {
            int n = 0;
            foreach (var it in FindAll("C_Battle_Item"))
                try
                {
                    if (NameOf(GetMember(it, "gameObject")) == ObeliskItem.ToString()) continue;   // Притянутый обелиск подберётся
                    CallByString(it, "StartCoroutine", "Play_ItemAllObtain_Move");
                    n++;
                }
                catch { }
            return $"притянуто предметов: {n}";
        }

        public string Status()
        {
            var sb = new StringBuilder();
            sb.Append($"бой {_inBattle}, часы {Clock(MyClock())}{(_clockHeld ? " (стоят)" : "")}, спавн игры {(!_noSpawn ? "вкл" : "выкл")}, бессмертие {(_immortal ? "вкл" : "выкл")}, опыт {(_noMana ? "выкл" : "вкл")}");
            try
            {
                var byType = new SortedDictionary<int, int>();
                foreach (var e in _enemies.Alive)
                {
                    int t = -1; try { t = Convert.ToInt32(GetMember(e.Raw, "UnitID") ?? -1); } catch { }
                    byType[t] = byType.TryGetValue(t, out var k) ? k + 1 : 1;
                }
                sb.Append($"; врагов {byType.Values.Sum()}: " + string.Join(" ", byType.Select(kv => $"{kv.Key}×{kv.Value}")));
            }
            catch (Exception e) { sb.Append("; враги: " + e.Message); }
            return sb.ToString();
        }

        // Поля читаются по именам из Session: после их переименования в coop сценарии покажут «нет».
        public string CoopStatus()
        {
            var s = CoopSession();
            if (s == null) return "coop=нет";
            var parts = new List<string> { "coop=1" };
            void Add(string key, Func<object> get) { try { parts.Add($"{key}={get() ?? "нет"}"); } catch (Exception e) { parts.Add($"{key}=ошибка {Inner(e).Message}"); } }
            Add("connected", () => GetMember(GetMember(s, "_client"), "Connected"));
            Add("hosting", () => GetMember(s, "_hosting"));
            Add("inRun", () => GetMember(s, "_inRun"));
            Add("world", () => GetMember(s, "_worldActive"));
            Add("clock", () => CallMethod(s, "MyClock"));
            Add("castsDone", () => GetMember(s, "_castsDone"));
            Add("castFails", () => GetMember(s, "_castFails"));
            Add("sent", () => Pairs(GetMember(s, "_sentById"), v => v));
            Add("replayed", () => Pairs(GetMember(s, "_castCost"), v => GetMember(v, "Item3")));
            Add("armaPlayed", () => GetMember(s, "_armaPlayed"));
            Add("armaSent", () => GetMember(s, "_armaSent"));
            Add("puppets", () =>
            {
                var byType = new SortedDictionary<string, int>();
                foreach (DictionaryEntry kv in (IDictionary)GetMember(s, "_puppets"))
                {
                    var p = kv.Value;
                    if (GetMember(p, "Obj") == null || (bool)GetMember(p, "LocalDead")) continue;
                    var key = GetMember(p, "Type") + ((bool)GetMember(p, "Special") ? "s" + GetMember(p, "Origin") : "");
                    byType[key] = byType.TryGetValue(key, out var k) ? k + 1 : 1;
                }
                return byType.Count == 0 ? "0" : string.Join(",", byType.Select(kv => $"{kv.Key}:{kv.Value}"));
            });
            Add("partnerOrb", () =>
            {
                foreach (DictionaryEntry kv in (IDictionary)GetMember(s, "_partners"))
                {
                    var o = GetMember(kv.Value, "Orb");
                    if (o == null) return "0";
                    return $"sh:{B(o, "Shield")},sa:{GetMember(o, "SatA")},sb:{GetMember(o, "SatB")},sp:{GetMember(o, "Spirits")},ez:{B(o, "ZoneOn")},mc:{B(o, "McOn")}";
                }
                return "нет друга";
            });
            try { parts.Add((string)CallMethod(s, "ProfileStatus")); } catch (Exception e) { parts.Add("profile=ошибка " + Inner(e).Message); }
            return string.Join("; ", parts);
        }

        public string CoopProfileReset()
        {
            var s = CoopSession();
            if (s == null) return "coop=нет";
            CallMethod(s, "ResetFrameProfile");
            return "замер кадра и сети обнулён";
        }

        // Мир друга совмещён с нашим по точкам старта, так что хост и гость на своих стартах стоят рядом.
        public string ToStart(double dx)
        {
            var s = CoopSession();
            if (s == null) return "нет coop";
            if (!(GetMember(s, "_startX") is double sx) || !(GetMember(s, "_startY") is double sy)) return "точка старта ещё не известна";
            var hero = typeof(BattleState).GetProperty("Hero", Any)?.GetValue(null);
            if (!(GetMember(hero, "Transform_ObjUnit") is UnityEngine.Transform tr)) return "нет героя";
            var p = new UnityEngine.Vector3((float)(sx + dx), (float)sy, tr.position.z);
            tr.position = p;
            try { SetMember(GetMember(hero, "Rigidbody_ObjUnit"), "position", p); } catch { }   // Иначе физика вернёт героя
            return $"герой на старте +{dx:0.#}";
        }

        // Так герой подбирает предмет, на который встал.
        public string GoTo(double x, double y)
        {
            var hero = typeof(BattleState).GetProperty("Hero", Any)?.GetValue(null);
            if (!(GetMember(hero, "Transform_ObjUnit") is UnityEngine.Transform tr)) return "нет героя";
            var p = new UnityEngine.Vector3((float)x, (float)y, tr.position.z);
            tr.position = p;
            try { SetMember(GetMember(hero, "Rigidbody_ObjUnit"), "position", p); } catch { }
            return $"герой в {x:0.##}, {y:0.##}";
        }

        // Подобранный предмет остаётся активным, игра уносит его в z 999.
        public string Pickups()
        {
            var sb = new StringBuilder();
            void Add(string kind, object item)
            {
                if (!(GetMember(item, "transform") is UnityEngine.Transform tr)) return;
                var p = tr.position;
                if (p.z > 100) return;
                sb.Append(kind).Append('|').Append(p.x.ToString("0.##", Inv)).Append('|').Append(p.y.ToString("0.##", Inv)).Append('\n');
            }
            foreach (var box in FindAll("C_Battle_Item_TreasureBox"))
                if (ActiveSelf(GetMember(box, "gameObject")))
                    Add("box" + GetMember(box, "ObtainItemType"), box);
            foreach (var it in FindAll("C_Battle_Item"))
                if (Convert.ToInt32(GetMember(it, "Type_ObjItem")) == 4 && NameOf(GetMember(it, "gameObject")) == "18" && ActiveSelf(GetMember(it, "gameObject")))
                    Add("obelisk", it);
            return sb.ToString().TrimEnd();
        }

        public string Buttons()
        {
            var buttons = ActiveButtons();
            var texts = new Dictionary<object, string>();
            foreach (var t in FindAll(UnityType("UI.Text")))
            {
                var owner = buttons.FirstOrDefault(b => IsInside(GetMember(t, "transform"), GetMember(b, "transform")));
                var s = (GetMember(t, "text") as string ?? "").Replace('\n', ' ').Replace('|', '/').Trim();
                if (owner != null && s.Length > 0) texts[owner] = texts.TryGetValue(owner, out var was) ? was + " / " + s : s;
            }
            var sb = new StringBuilder();
            for (int i = 0; i < buttons.Count; i++)
            {
                var tr = (UnityEngine.Transform)GetMember(buttons[i], "transform");
                sb.Append(i).Append('|').Append(PathOf(tr)).Append('|').Append(tr.position.x.ToString("0", Inv)).Append('|')
                  .Append(tr.position.y.ToString("0", Inv)).Append('|').Append(GetMember(buttons[i], "interactable")).Append('|')
                  .Append(texts.TryGetValue(buttons[i], out var text) ? text : "").Append('\n');
            }
            return sb.ToString().TrimEnd();
        }

        // Путь проверяется заново: между Buttons и Click окно могло смениться.
        public string Click(int i, string path)
        {
            var buttons = ActiveButtons();
            if (i < 0 || i >= buttons.Count) return $"нет кнопки {i}";
            var actual = PathOf((UnityEngine.Transform)GetMember(buttons[i], "transform"));
            if (actual != path) return $"кнопка {i} уже другая: {actual}";
            // Обработчики карт берут нажатую кнопку из EventSystem, при касании её выбирает сам тап
            var events = UnityType("EventSystems.EventSystem");
            var current = events?.GetProperty("current", Any)?.GetValue(null);
            if (current != null) CallMethod(current, "SetSelectedGameObject", GetMember(buttons[i], "gameObject"));
            try { CallMethod(GetMember(buttons[i], "onClick"), "Invoke"); }
            catch (Exception e) { return "ошибка: " + Inner(e).Message.Split('\n')[0]; }
            return "нажато " + actual;
        }

        private List<object> ActiveButtons() => FindAll(UnityType("UI.Button")).Where(b => GetMember(b, "isActiveAndEnabled") is bool on && on).ToList();

        private static Type UnityType(string name) => AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => { try { return a.GetType("UnityEngine." + name); } catch { return null; } })
            .FirstOrDefault(t => t != null);

        // Обёртки Il2Cpp одного объекта бывают разными экземплярами C#, сравнивать надо указатели
        private static bool IsInside(object tr, object root)
        {
            var target = GetMember(root, "Pointer");
            for (var p = tr as UnityEngine.Transform; p != null; p = GetMember(p, "parent") as UnityEngine.Transform)
                if (Equals(GetMember(p, "Pointer"), target)) return true;
            return false;
        }

        private static string PathOf(UnityEngine.Transform tr)
        {
            var parts = new List<string>();
            for (var p = tr; p != null; p = GetMember(p, "parent") as UnityEngine.Transform) parts.Add(NameOf(p) ?? "?");
            parts.Reverse();
            return string.Join("/", parts).Replace(' ', '_');
        }

        private static bool ActiveSelf(object go) => GetMember(go, "activeSelf") is bool b && b;

        // Unit_Destroy_Simple не берёт боссов и мобов перехода, их добивает Unit_Death.
        public string NoEnemies()
        {
            string a = ClearEnemies(), b = KillEnemies();
            return $"{a}, {b}";
        }

        public string CoopStopClient()
        {
            var s = CoopSession();
            if (s == null) return "нет coop";
            CallMethod(GetMember(s, "_client"), "Stop");
            return "клиент coop остановлен";
        }

        public string CoopJoin()
        {
            var s = CoopSession();
            if (s == null) return "нет coop";
            CallMethod(s, "Join");
            return "подключаюсь заново";
        }

        public string Spell(int id, int want)
        {
            var cheat = ModTarget("coop_cheat", "_c");
            if (cheat == null) return "нет coop_cheat";
            var spells = GetMember(cheat, "_spells");
            int Level() => Convert.ToInt32(CallMethod(spells, "Level", id));
            int before = Level();
            try
            {
                for (int i = 0; i < 30 && Level() != want; i++)
                {
                    SetMember(cheat, "_minus", Level() > want);
                    CallMethod(cheat, "Press", id, "#" + id);
                }
            }
            finally { TrySet(cheat, "_minus", false); }
            return $"заклинание {id}: {before} → {Level()}";
        }

        public string MaxMagic()
        {
            var cheat = ModTarget("coop_cheat", "_c");
            if (cheat == null) return "нет coop_cheat";
            CallMethod(cheat, "MaxAll");
            var spells = GetMember(cheat, "_spells");
            int max = Enumerable.Range(1, 21).Count(id => Convert.ToInt32(CallMethod(spells, "Level", id)) > 0);
            return $"заклинаний с уровнем: {max} из 21";
        }

        private const string LearnList = "List_AllyAbilityLearn";

        public string Catalog()
        {
            var cheat = ModTarget("coop_cheat", "_c");
            if (cheat == null) return "нет coop_cheat";
            CallMethod(cheat, "LoadTables");
            var lines = new List<string>();
            foreach (var tab in (IEnumerable)GetMember(cheat, "_tabs"))
            {
                string kind = GetMember(tab, "Kind") as string, label = GetMember(tab, "Label") as string;
                if (kind != "combo" && label != "Особенн.") continue;
                foreach (var row in (IEnumerable)GetMember(tab, "Rows"))
                {
                    var req = GetMember(row, "Req") as int[];
                    lines.Add($"{(kind == "combo" ? "combo" : "trait")}|{GetMember(row, "Id")}|{GetMember(row, "Name")}|{(req == null ? "" : string.Join("+", req))}");
                }
            }
            return string.Join("\n", lines);
        }

        public string Learn(int id, int count)
        {
            var cheat = ModTarget("coop_cheat", "_c");
            if (cheat == null) return "нет coop_cheat";
            int before = SetLearned(cheat, id, count);
            CallMethod(cheat, "Recalc");
            return $"способность {id}: {before} → {LearnedCount(cheat, id)}";
        }

        // Саму комбинацию игра даёт сама.
        public string Combo(int n, bool on)
        {
            var cheat = ModTarget("coop_cheat", "_c");
            if (cheat == null) return "нет coop_cheat";
            CallMethod(cheat, "LoadTables");
            foreach (var tab in (IEnumerable)GetMember(cheat, "_tabs"))
            {
                if (GetMember(tab, "Kind") as string != "combo") continue;
                foreach (var row in (IEnumerable)GetMember(tab, "Rows"))
                {
                    if (Convert.ToInt32(GetMember(row, "Id")) != n) continue;
                    var req = (int[])GetMember(row, "Req");
                    foreach (var id in req) SetLearned(cheat, id, on ? Math.Max(1, LearnedCount(cheat, id)) : 0);
                    CallMethod(cheat, "Recalc");
                    return $"комбинация {n} «{GetMember(row, "Name")}»: составляющие {string.Join(", ", req.Select(x => $"{x}={LearnedCount(cheat, x)}"))}";
                }
            }
            return $"комбинации {n} нет";
        }

        public string ResetMagic()
        {
            var cheat = ModTarget("coop_cheat", "_c");
            if (cheat == null) return "нет coop_cheat";
            var list = CallMethod(cheat, "Static", LearnList);
            int removed = 0;
            for (int i = Count(list) - 1; i >= 0; i--)
                if (Convert.ToInt32(ItemAt(list, i)) >= 361) { CallMethod(list, "RemoveAt", i); removed++; }
            ClearCounts(cheat);
            CallMethod(cheat, "Recalc");
            for (int id = 1; id <= 21; id++) Spell(id, 0);
            return $"магии сняты, особенностей убрано {removed}";
        }

        private static int SetLearned(object cheat, int id, int count)
        {
            var list = CallMethod(cheat, "Static", LearnList);
            int before = LearnedCount(cheat, id);
            for (int k = before; k < count; k++) CallMethod(list, "Add", id);
            for (int k = before; k > count; k--) CallMethod(list, "Remove", id);
            ClearCounts(cheat);
            return before;
        }

        private static int LearnedCount(object cheat, int id)
        {
            var list = CallMethod(cheat, "Static", LearnList);
            int n = 0;
            for (int i = Count(list) - 1; i >= 0; i--) if (Convert.ToInt32(ItemAt(list, i)) == id) n++;
            return n;
        }

        private static void ClearCounts(object cheat) => (GetMember(cheat, "_counts") as IDictionary)?.Clear();

        private static string Pairs(object dict, Func<object, object> value)
        {
            if (!(dict is IDictionary d) || d.Count == 0) return "0";
            var parts = new List<string>();
            foreach (DictionaryEntry kv in d) parts.Add($"{kv.Key}:{value(kv.Value)}");
            return string.Join(",", parts);
        }

        private static int B(object o, string name) => GetMember(o, name) is bool b && b ? 1 : 0;

        private static object CoopSession() => ModTarget("coop", "_session");

        private static object ModTarget(string id, string field)
        {
            var core = Hooks.FindType("ModKit:ModKit.Core");
            var mods = (core?.GetProperty("Mods", Any)?.GetValue(null) ?? core?.GetField("Mods", Any)?.GetValue(null)) as IEnumerable;
            if (mods == null) return null;
            foreach (var m in mods)
            {
                var def = GetMember(m, "Def");
                if (GetMember(def, "Id") as string == id) return GetMember((GetMember(def, "OnEnable") as Delegate)?.Target, field);
            }
            return null;
        }

        // Панель

        private void ShowToggle()
        {
            Kill(ref _toggle);
            _toggle = UiKit.AddHudButton("lab_toggle", "ПОЛИГОН", 590, 312, 125, 56, 18, Red, () => _ctx.Guard("полигон: панель", TogglePanel));
        }

        private void TogglePanel()
        {
            if (_tab >= 0) { ClosePanel(); return; }
            _tab = 0;
            ShowPage();
        }

        private void ClosePanel()
        {
            foreach (var b in _panel) { var x = b; Kill(ref x); }
            _panel.Clear();
            _tab = -1;
        }

        private void Act(Func<string> fn)
        {
            string r;
            try { r = fn(); } catch (Exception e) { r = "ошибка: " + Inner(e).Message; }
            _ctx.Toast(r, 2);
            ShowPage();
        }

        private void ShowPage()
        {
            foreach (var b in _panel) { var x = b; Kill(ref x); }
            _panel.Clear();
            string[] tabs = { "Арена", "Мобы", "Механики" };
            for (int t = 0; t < tabs.Length; t++)
            {
                int tt = t;
                _panel.Add(UiKit.AddHudButton("lab_tab" + t, tabs[t], 15 + t * 235, 380, 225, 54, 20, t == _tab ? Blue : Gray, () => _ctx.Guard("полигон", () => { _tab = tt; ShowPage(); })));
            }
            var rows = _tab == 0 ? ArenaRows() : _tab == 1 ? MobRows() : MechanicRows();
            for (int i = 0; i < rows.Count; i++)
            {
                var (text, color, fn) = rows[i];
                _panel.Add(UiKit.AddHudButton("lab_b" + i, text, 15 + (i % 2) * 355, 446 + (i / 2) * 66, 345, 60, 19, color, () => _ctx.Guard("полигон", () => Act(fn))));
            }
            int y = 446 + (rows.Count + 1) / 2 * 66;
            _panel.Add(UiKit.AddHudButton("lab_close", "Закрыть", 15, y, 700, 56, 20, Gray, () => _ctx.Guard("полигон", ClosePanel)));
        }

        private List<(string, double[], Func<string>)> ArenaRows() => new List<(string, double[], Func<string>)>
        {
            (_noSpawn ? "Спавн игры: выкл" : "Спавн игры: вкл", _noSpawn ? Red : Green, () => SetSpawn(_noSpawn)),
            (_immortal ? "Бессмертие: вкл" : "Бессмертие: выкл", _immortal ? Green : Gray, () => SetImmortal(!_immortal)),
            (_clockHeld ? $"Часы стоят ({Clock(_heldClock)})" : "Часы идут", _clockHeld ? Red : Green, () => HoldClock(!_clockHeld)),
            ("Убрать врагов", Gray, ClearEnemies),
            (_noMana ? "Опыт: выкл" : "Опыт: вкл", _noMana ? Red : Green, () => SetMana(_noMana)),
            ("Убить всех (награда)", Gray, KillEnemies),
            ("Часы −10 с", Gray, () => ShiftClock(-10)),
            ("Часы +10 с", Gray, () => ShiftClock(10)),
            ("Часы +60 с", Gray, () => ShiftClock(60)),
        };

        private List<(string, double[], Func<string>)> MobRows() => new List<(string, double[], Func<string>)>
        {
            ("◀ тип", Gray, () => { _type = _type <= 0 ? MaxType : _type - 1; return $"тип {_type}"; }),
            ("тип ▶", Gray, () => { _type = _type >= MaxType ? 0 : _type + 1; return $"тип {_type}"; }),
            ($"Тип {_type}, ×{Counts[_countIdx]} (сменить число)", Blue, () => { _countIdx = (_countIdx + 1) % Counts.Length; return $"по {Counts[_countIdx]}"; }),
            ($"Вызвать тип {_type}", Green, () => Spawn(_type, Counts[_countIdx])),
            ($"Эпические тип {_type}", Green, () => SpawnEpic(_type, Counts[_countIdx])),
            ($"Особые ×{Counts[_countIdx]}", Green, () => SpawnSpecial(Counts[_countIdx])),
            ("Переход (усиление мобов)", Green, Transition),
            ("Убрать врагов", Gray, ClearEnemies),
        };

        private List<(string, double[], Func<string>)> MechanicRows() => new List<(string, double[], Func<string>)>
        {
            ("Часы к боссу (−5 с)", Blue, ToBoss),
            ("Босс-волна сейчас", Blue, BossWave),
            ("Обелиск", Green, Obelisk),
            ("Смена области", Green, AreaChange),
            ("Притянуть ману", Green, PullItems),
            ($"Предмет {_item}", Green, () => Item(_item, 1)),
            ("◀ предмет", Gray, () => { _item = Math.Max(0, _item - 1); return $"предмет {_item}"; }),
            ("предмет ▶", Gray, () => { _item++; return $"предмет {_item}"; }),
        };

        private static void Kill(ref UiKit.HudButton b)
        {
            try { b?.Destroy(); } catch { }
            b = null;
        }

        // Игра

        private int MyClock()
        {
            if (_mgr == null) return -1;
            try { return Convert.ToInt32(GetMember(_mgr, "Time_Min")) * 60 + Convert.ToInt32(GetMember(_mgr, "Time_Sec")); } catch { return -1; }
        }

        private static string Clock(int s) => s < 0 ? "?" : $"{s / 60}:{s % 60:00}";

        private static PropertyInfo _survivalTime;
        private static MethodInfo _survivalFromFloat;

        // Z_Database.Game_SurvivalTime это ObscuredFloat, из числа он получается неявным приведением.
        private void SetClock(int sec)
        {
            try
            {
                if (_survivalTime == null)
                {
                    _survivalTime = Hooks.FindType("Z_Database")?.GetProperty("Game_SurvivalTime", Any);
                    _survivalFromFloat = _survivalTime?.PropertyType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == _survivalTime.PropertyType && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(float));
                }
                _survivalTime?.SetValue(null, _survivalFromFloat.Invoke(null, new object[] { (float)sec }));
            }
            catch (Exception e) { _ctx.Log("полигон: часы: " + Inner(e).Message); }
        }

        private static double DbNum(string name)
        {
            try { return Convert.ToDouble(Hooks.FindType("Z_Database")?.GetProperty(name, Any)?.GetValue(null) ?? double.NaN); } catch { return double.NaN; }
        }

        private static int DbInt(string name)
        {
            try { return Convert.ToInt32(Hooks.FindType("Z_Database")?.GetProperty(name, Any)?.GetValue(null) ?? -1); } catch { return -1; }
        }

        private bool StartCoroutine(string name)
        {
            try { CallByString(_mgr, "StartCoroutine", name); return true; }
            catch (Exception e) { _ctx.Log($"полигон: {name}: " + Inner(e).Message); return false; }
        }

        private static MethodInfo _findObjectsOfType, _il2cppTypeOf;
        private List<object> FindAll(string typeName) => FindAll(Hooks.FindType(typeName));

        private List<object> FindAll(Type t)
        {
            var res = new List<object>();
            if (t == null) return res;
            var asms = AppDomain.CurrentDomain.GetAssemblies();
            _il2cppTypeOf ??= asms.Select(a => { try { return a.GetType("Il2CppInterop.Runtime.Il2CppType"); } catch { return null; } }).FirstOrDefault(x => x != null)
                ?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "Of" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            _findObjectsOfType ??= asms.Select(a => { try { return a.GetType("UnityEngine.Object"); } catch { return null; } }).FirstOrDefault(x => x != null && x.GetMethods().Any(m => m.Name == "FindObjectsOfType"))
                ?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "FindObjectsOfType" && !m.IsGenericMethod && m.GetParameters().Length == 1);
            if (_il2cppTypeOf == null || _findObjectsOfType == null) return res;
            var arr = _findObjectsOfType.Invoke(null, new[] { _il2cppTypeOf.MakeGenericMethod(t).Invoke(null, null) });
            int n = Count(arr);
            var item = arr.GetType().GetMethod("get_Item", new[] { typeof(int) });
            MethodInfo cast = null; Type castFor = null;
            for (int i = 0; i < n; i++)
            {
                var o = item.Invoke(arr, new object[] { i });
                if (o == null) continue;
                if (o.GetType() != castFor) { castFor = o.GetType(); cast = castFor.GetMethod("TryCast")?.MakeGenericMethod(t); }
                res.Add(cast?.Invoke(o, null) ?? o);
            }
            return res;
        }

        // Рефлексия

        private static Exception Inner(Exception e) => (e as TargetInvocationException)?.InnerException ?? e;

        private static PropertyInfo FindProp(Type t, string name) => t.GetProperty(name, Any);

        private static object GetMember(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            var p = t.GetProperty(name, Any);
            if (p != null && p.GetIndexParameters().Length == 0) return p.GetValue(p.GetGetMethod(true)?.IsStatic == true ? null : o);
            var f = t.GetField(name, Any);
            return f?.GetValue(f.IsStatic ? null : o);
        }

        private static void SetMember(object o, string name, object value)
        {
            if (o == null) return;
            var t = o.GetType();
            var p = t.GetProperty(name, Any);
            if (p != null && p.CanWrite)
            {
                p.SetValue(p.GetSetMethod(true)?.IsStatic == true ? null : o, Convert.ChangeType(value, p.PropertyType));
                return;
            }
            var f = t.GetField(name, Any);
            f?.SetValue(f.IsStatic ? null : o, Convert.ChangeType(value, f.FieldType));
        }

        private static void TrySet(object o, string name, object value) { try { SetMember(o, name, value); } catch { } }

        private static object CallMethod(object o, string name, params object[] args)
        {
            var m = o.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length)
                    ?? throw new MissingMethodException(o.GetType().Name + "." + name + "/" + args.Length);
            var ps = m.GetParameters();
            for (int i = 0; i < args.Length; i++)
                if (args[i] != null && !ps[i].ParameterType.IsInstanceOfType(args[i])) args[i] = Convert.ChangeType(args[i], ps[i].ParameterType);
            return m.Invoke(m.IsStatic ? null : o, args);
        }

        private static object CallByString(object o, string name, string arg)
        {
            var m = o.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(string) }, null)
                    ?? throw new MissingMethodException(o.GetType().Name + "." + name + "(string)");
            return m.Invoke(o, new object[] { arg });
        }

        private static int Count(object coll) => coll == null ? 0 : Convert.ToInt32(GetMember(coll, "Count") ?? GetMember(coll, "Length") ?? (coll is ICollection c ? c.Count : 0));

        private static object ItemAt(object coll, int i)
        {
            var m = coll.GetType().GetMethod("get_Item", new[] { typeof(int) });
            return m != null ? m.Invoke(coll, new object[] { i }) : (coll as IList)?[i];
        }

        private static string NameOf(object o) { try { return GetMember(o, "name") as string; } catch { return null; } }
    }
}
