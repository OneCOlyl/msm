using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModKit;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Обелиск у гостя в режиме общих врагов (1.6.42). Своего обелиска у гостя нет: хост поднял свой — гость сразу получает то же,
    /// что дал бы подбор (выбор легендарного артефакта, притянутую ману, смену области), его босс-волна снимается, а часы ставятся
    /// как у хоста. Раньше (1.6.29–1.6.41) гость сперва дожидался конца своего вступления босс-волны (до ~1 мин после хоста).
    ///
    /// Как устроено в игре (консоль на телефоне 25.10):
    /// - часы боя — Z_Database.Game_SurvivalTime (ObscuredFloat), Time_Min/Time_Sec игра выводит из него; подставить можно;
    /// - босс-этап начинается, когда часы ровно на пороге (3:00, 6:00…), и часы стоят до смены области (Start_AreaChange);
    ///   перескочить порог — этапа нет вовсе; Start_AreaChange на самом пороге запускает вступление заново (1.6.40);
    /// - конец вступления (BossWave_EnforceVar дорос до 0,999) при ApplyBossNum 0 заканчивает волну и кладёт обелиск
    ///   (CreateItem_Character(18, 1)), часы снова встают; при ApplyBossNum 1 — ничего не делает. Вернуть 0 и после конца
    ///   вступления нельзя: через 5 с игра всё равно заканчивает этап (телефоны 25.10, 1.6.42) — держим 1 весь бой, с самого
    ///   начала (1.6.45: с первого обелиска хоста было поздно — на 3:00 гостю ложился свой обелиск, и убрать его не выходило).
    /// </summary>
    internal sealed partial class Session
    {
        private const int ObeliskItemId = 10;            // Z_Database.ArtifactType после обелиска у хоста — легендарные артефакты
        private const int ObeliskObjType = 4;            // Type_ObjItem обелиска (предмет «18»)
        private const string ObeliskName = "18";
        private static MethodInfo _findObjectsOfType, _il2cppTypeOf;
        private static bool _findFailed;
        private int _obelisksRemoved, _obeliskRewards;

        /// <summary>Только для тестов на ПК: объекты сцены по имени типа (в тесте Unity нет).</summary>
        internal static Func<ModContext, string, List<object>> FindAllOverride;

        /// <summary>Все объекты сцены типа игры typeName (UnityEngine.Object.FindObjectsOfType), приведённые к нему.</summary>
        private List<object> FindAllOfType(string typeName)
        {
            if (FindAllOverride != null) return FindAllOverride(_ctx, typeName);
            Type t;
            try { t = Hooks.FindType(typeName); } catch { t = null; }
            return t == null ? new List<object>() : FindAllOfType(t, typeName);
        }

        /// <summary>То же для типа t (игры или Unity); what — подпись для журнала.</summary>
        private List<object> FindAllOfType(Type t, string what)
        {
            var res = new List<object>();
            if (_findFailed) return res;
            try
            {
                if (_findObjectsOfType == null)
                {
                    var asms = AppDomain.CurrentDomain.GetAssemblies();
                    var il2cppType = asms.Select(a => { try { return a.GetType("Il2CppInterop.Runtime.Il2CppType"); } catch { return null; } }).FirstOrDefault(x => x != null);
                    _il2cppTypeOf = il2cppType?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "Of" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                    var unityObject = asms.Select(a => { try { return a.GetType("UnityEngine.Object"); } catch { return null; } }).FirstOrDefault(x => x != null && x.GetMethods().Any(m => m.Name == "FindObjectsOfType"));
                    _findObjectsOfType = unityObject?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "FindObjectsOfType" && !m.IsGenericMethod && m.GetParameters().Length == 1);
                    if (_il2cppTypeOf == null || _findObjectsOfType == null) { _findFailed = true; _ctx.Log("обелиск: поиск объектов по типу недоступен"); return res; }
                }
                var arr = _findObjectsOfType.Invoke(null, new[] { _il2cppTypeOf.MakeGenericMethod(t).Invoke(null, null) });
                int n = Convert.ToInt32(arr.GetType().GetProperty("Length")?.GetValue(arr) ?? 0);
                var item = arr.GetType().GetMethod("get_Item", new[] { typeof(int) });
                var idx = new object[1];
                Type castFor = null; MethodInfo cast = null;      // TryCast<t> — один раз, а не на каждый из сотен предметов (1.6.46)
                for (int i = 0; i < n; i++)
                {
                    idx[0] = i;
                    var o = item.Invoke(arr, idx);
                    if (o == null) continue;
                    if (o.GetType() != castFor) { castFor = o.GetType(); cast = castFor.GetMethod("TryCast")?.MakeGenericMethod(t); }
                    res.Add(cast?.Invoke(o, null) ?? o);
                }
            }
            catch (Exception e) { _ctx.Log($"обелиск: поиск {what}: {Inner(e).Message}"); }
            return res;
        }

        /// <summary>Вид предмета: ObtainItemType, а не прочитать — имя объекта (у предметов оно и есть номер вида: «1», «10»…).</summary>
        private static int ItemType(object item)
        {
            try { var v = GetMember(item, "ObtainItemType"); if (v != null) return Convert.ToInt32(v); } catch { }
            try { return int.TryParse(NameOf(GetMember(item, "gameObject")), out var n) ? n : -1; } catch { return -1; }
        }

        /// <summary>
        /// Обелиски на карте гостя — убрать. Обелиск — предмет «18» с Type_ObjItem 4 (журнал 1.6.16); тот же вид бывает и у других
        /// предметов (1.6.42 в 2:00 убрал что-то ещё по CreateItem_Character(18)) — поэтому только при флаге обелиска и по имени.
        /// </summary>
        private void RemoveObelisks(string why)
        {
            if (MyObelisk() != 1) return;
            int n = 0;
            foreach (var it in FindAllOfType("C_Battle_Item"))
            {
                try
                {
                    if (Convert.ToInt32(GetMember(it, "Type_ObjItem")) != ObeliskObjType) continue;
                    if (NameOf(GetMember(it, "gameObject")) != ObeliskName) continue;
                    var go = GetMember(it, "gameObject");
                    if (go != null && GetMember(go, "activeSelf") is bool act && !act) continue;
                    _ctx.Mark("обелиск: убираю обелиск гостя");
                    try { CallMethod(it, "DestroyItem"); } catch (Exception e) { _ctx.Log("обелиск: DestroyItem: " + Inner(e).Message); }
                    try { go?.GetType().GetMethod("SetActive")?.Invoke(go, new object[] { false }); } catch { }
                    n++;
                }
                catch { }
            }
            _obelisksRemoved += n;
            if (n > 0) _ctx.Log($"обелиск: {why} — убрал с карты ({n})");
        }

        /// <summary>Только для тестов на ПК: тип игры по имени (Hooks.FindType ищет в сборке игры).</summary>
        internal static Func<string, Type> TypeOverride;

        /// <summary>Статическое поле типа игры (Z_Database.ArtifactType и т.п.).</summary>
        private static void SetStatic(string typeName, string field, object value)
        {
            var t = TypeOverride?.Invoke(typeName) ?? Hooks.FindType(typeName);
            var m = t == null ? null : FindAny(t, field);
            if (m == null) throw new MissingMemberException(typeName + "." + field);
            if (m.Prop != null && m.Prop.CanWrite) m.Prop.SetValue(null, Convert.ChangeType(value, m.ValueType));
            else ((m.Field ?? m.SetField) ?? throw new MissingMemberException(typeName + "." + field)).SetValue(null, Convert.ChangeType(value, m.ValueType));
        }

        private static PropertyInfo _survivalTime;
        private static MethodInfo _survivalFromFloat;

        /// <summary>
        /// Часы боя гостя = sec: Z_Database.Game_SurvivalTime (ObscuredFloat, из числа — неявным приведением). Без Z_Database
        /// (имитация на ПК) — поля менеджера.
        /// </summary>
        private void SetClock(int sec)
        {
            try
            {
                if (_survivalTime == null)
                {
                    _survivalTime = Hooks.FindType("Z_Database")?.GetProperty("Game_SurvivalTime", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    _survivalFromFloat = _survivalTime?.PropertyType.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "op_Implicit" && m.ReturnType == _survivalTime.PropertyType && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(float));
                }
                if (_survivalTime != null && _survivalFromFloat != null)
                {
                    _survivalTime.SetValue(null, _survivalFromFloat.Invoke(null, new object[] { (float)sec }));
                    return;
                }
            }
            catch (Exception e) { _ctx.Log("часы боя: Game_SurvivalTime: " + Inner(e).Message); }
            SetMember(_battleMgr, "Time_Min", sec / 60);
            SetMember(_battleMgr, "Time_Sec", sec % 60);
        }

        /// <summary>
        /// Хост поднял обелиск — у гостя сразу: босс-волна снята, часы как у хоста, смена области, выбор легендарного артефакта и
        /// притянутая мана (reward false — без награды: игра у гостя сама закончила этап, награду он уже получил). Счётчик боссов
        /// держим 1 (BossHoldStep), иначе конец вступления закончит этап ещё раз и положит обелиск.
        /// </summary>
        private void CatchUpBoss(bool reward)
        {
            if (reward) _obeliskRewards++;
            int before = MyClock(), acBefore = MyAreaChange();
            bool wave = MyBossWave() != 0;
            KillHeldQuietly();
            _bossHold = true;
            BossHoldStep();
            // часы за порог босса: Start_AreaChange на самом пороге начал бы вступление заново
            int target = Math.Max(_hostClock, before + 1);
            try { _ctx.Mark("обелиск: часы гостя как у хоста"); SetClock(target); }
            catch (Exception e) { _ctx.Log("обелиск: часы: " + Inner(e).Message); }
            // смена области, как у хоста после подбора; счётчик смен — как у хоста. С наградой область меняет само окно артефакта:
            // Create_Artifact при ArtifactType 9 и 10 зовёт Start_AreaChange (libil2cpp, 30.10). До 1.6.106 мод менял её и сам —
            // у гостя область менялась дважды, и уровни за смену на Terra (+12 у хоста) он получал дважды
            try
            {
                int id = Convert.ToInt32(GetAny(_battleMgr, "AreaChangeID") ?? -1);
                int num = _hostAreaChange >= 0 ? _hostAreaChange : id;
                if (num >= 0) SetMember(_battleMgr, "AreaChangeNum", num);
            }
            catch (Exception e) { _ctx.Log("обелиск: счётчик смен области: " + Inner(e).Message); }
            // особый обелиск Terra (TreasureStep): лежит с начала боя и тоже ставит ApplyObeliskON; подбор — артефакты вида 9 и смена
            // области, ману не притягивает (C_Battle_Item_TreasureBox.OnCollisionEnter). Обелиск после босса идёт за босс-волной хоста —
            // особый только первый, пока босс-волны у хоста не было (по часам нельзя: на Terra босс в 2:30, 1.6.106 спутал)
            bool special = reward && !_hostHadBoss && _treasureRewards == 0;
            bool artifact = false, legend = false;
            if (reward)
            {
                if (special) _treasureRewards++;
                artifact = GiveArtifactChoice(special ? TreasureType : ObeliskItemId, out legend);
            }
            bool area = artifact && legend;
            if (!area)
            {
                try { _ctx.Mark("обелиск: смена области у гостя"); CallMethod(_battleMgr, "Start_AreaChange"); area = true; }
                catch (Exception e) { _ctx.Log("обелиск: смена области: " + Inner(e).Message); }
            }
            RemoveObelisks("остался у гостя");                  // до сброса ApplyObeliskON: без флага RemoveObelisks ничего не ищет
            try
            {
                SetMember(_battleMgr, "Battle_ClearWaveON", 0);
                SetMember(_battleMgr, "BossWave_VisionRatio", 0f);
                SetMember(_battleMgr, "ApplyBossGround_Size", 0f);
                SetMember(_battleMgr, "ApplyObeliskON", 0);
            }
            catch (Exception e) { _ctx.Log("обелиск: снять волну: " + Inner(e).Message); }
            HideBossWaveUi();
            if (!reward)
            {
                _ctx.Log($"обелиск: игра у меня сама закончила босс-этап — обелиск убран, часы {before / 60}:{before % 60:00} → {target / 60}:{target % 60:00}, смена области {(area ? $"{acBefore} → {MyAreaChange()}" : "не вышла")}");
                return;
            }
            int pulled = special ? 0 : PullItems();
            _ctx.Log($"{(special ? "особый " : "")}обелиск: хост поднял свой — у меня {(wave ? "босс-волна снята" : "босс-волны не было")}, часы {before / 60}:{before % 60:00} → {target / 60}:{target % 60:00} (у хоста {_hostClock / 60}:{_hostClock % 60:00}), " +
                     $"смена области {(area ? $"{acBefore} → {MyAreaChange()}" : "не вышла")}, {(artifact ? "выбор артефакта" + (legend ? (special ? " (вид 9)" : " (легендарные)") : "") : "окна артефакта нет")}, " +
                     $"притянуто предметов {pulled}, вступление {MyEnforce():0.00}");
        }

        private object _clockColor;                      // цвет часов вне босс-этапа (в этапе игра красит их в оранжевый)

        /// <summary>Шаг часов гостя: вне босс-этапа запомнить цвет часов — после перехода вернуть его.</summary>
        private void NoteClockColor()
        {
            if (MyBossWave() != 0 || MyVision() > 0.01 || MyObelisk() == 1) return;
            try { _clockColor = GetMember(GetMember(_battleMgr, "Text_Time"), "color") ?? _clockColor; } catch { }
        }

        /// <summary>
        /// Череп босс-волны над часами (Obj_MonsterClearBar) и оранжевые часы обычно убирает Play_BossWaveEffect_End — её у гостя
        /// нет (она бы положила обелиск), поэтому убираем сами (1.6.43: череп оставался до конца боя). Следующий этап игра
        /// включает их снова.
        /// </summary>
        private void HideBossWaveUi()
        {
            try
            {
                var bar = GetMember(_battleMgr, "Obj_MonsterClearBar");
                bar?.GetType().GetMethod("SetActive")?.Invoke(bar, new object[] { false });
            }
            catch (Exception e) { _ctx.Log("обелиск: череп босс-волны: " + Inner(e).Message); }
            try { SetMember(GetMember(_battleMgr, "Text_Time"), "color", _clockColor ?? new UnityEngine.Color(1, 1, 1, 1)); }
            catch (Exception e) { _ctx.Log("обелиск: цвет часов: " + Inner(e).Message); }
        }

        private bool _bossHold;

        /// <summary>Шаг часов гостя: после первого обелиска хоста счётчик боссов гостя не меньше 1 до конца боя.</summary>
        private void BossHoldStep()
        {
            if (!_bossHold || MyBossCount() >= 1) return;
            try { SetMember(_battleMgr, "ApplyBossNum", 1); } catch (Exception e) { _ctx.Log("обелиск: ApplyBossNum: " + Inner(e).Message); _bossHold = false; }
        }

        /// <summary>Снять удержание счётчика боссов (конец боя, выключение мода). Повторно — ничего не делает.</summary>
        private void ReleaseBossHold(string why)
        {
            if (!_bossHold) return;
            _bossHold = false;
            try { if (MyBossCount() >= 1) SetMember(_battleMgr, "ApplyBossNum", 0); } catch { }
            _ctx.Log($"обелиск: {why} — счётчик боссов гостя снова 0");
        }

        /// <summary>Свои спрятанные боссы гостя — убрать без смерти (смерть босса закончила бы волну и положила обелиск).</summary>
        private void KillHeldQuietly()
        {
            _heldNew.Clear();
            foreach (var e in _held) DestroySimple(e, false);
            if (_held.Count > 0) _ctx.Log($"обелиск: свой спрятанный босс гостя убран без смерти ({_held.Count})");
            _held.Clear(); _heldKeys.Clear();
        }

        /// <summary>Окно выбора артефакта вида type, как после подбора у хоста (Z_Database.ArtifactType: 10 — легендарные после обелиска).</summary>
        private bool GiveArtifactChoice(int type, out bool typed)
        {
            typed = false;
            // без ArtifactType окно предлагало обычные (у гостя было 0 — телефоны 24.10)
            try { SetStatic("Z_Database", "ArtifactType", type); typed = true; } catch (Exception e) { _ctx.Log("обелиск: тип артефактов: " + Inner(e).Message); }
            var am = FindAllOfType("V_Artifact_Manager").FirstOrDefault();
            if (am == null) return false;
            try { _ctx.Mark("обелиск: выбор артефакта у гостя"); CallMethod(am, "Create_Artifact"); return true; }
            catch (Exception e) { _ctx.Log("обелиск: окно артефакта: " + Inner(e).Message); return false; }
        }

        /// <summary>
        /// Мана и предметы на карте — к герою, как при подборе обелиска (Play_ItemAllObtain_Move). Обелиск не трогаем: притянутый,
        /// он подбирается — второй выбор артефакта и смена области (1.6.44: на 6:00 гость так подобрал обелиск, оставшийся с 3:00).
        /// </summary>
        private double _pulledAt = -100;
        private int PullItems()
        {
            _pulledAt = Clock.Elapsed.TotalSeconds;
            int pulled = 0;
            MethodInfo sc = null;
            var args = new object[] { "Play_ItemAllObtain_Move" };
            foreach (var it in FindAllOfType("C_Battle_Item"))
            {
                try
                {
                    if (Convert.ToInt32(GetMember(it, "Type_ObjItem")) == ObeliskObjType && NameOf(GetMember(it, "gameObject")) == ObeliskName) continue;
                    sc ??= it.GetType().GetMethod("StartCoroutine", new[] { typeof(string) });
                    if (sc == null) break;
                    if (pulled == 0) _ctx.Mark("обелиск: притягиваю ману гостя");
                    sc.Invoke(it, args);
                    pulled++;
                }
                catch { }
            }
            return pulled;
        }

        // Особый обелиск (Terra): в начале боя игра кладёт каждому C_Battle_Item_TreasureBox с ObtainItemType 9 и ставит ApplyObeliskON;
        // подбор — выбор артефакта вида 9 и смена области (libil2cpp и телефоны, 30.10), часы боя от него не зависят. В общих
        // врагах он только у хоста: у гостя убираем, а подбор хоста гостю повторяет CatchUpBoss (флаг обелиска хоста 1 → 0).
        private const int TreasureType = 9, TreasureScanUntil = 60;
        private double _treasureScanAt;
        private int _treasureRemoved, _treasureRewards;

        private static bool ActiveObj(object o)
        {
            try { return GetMember(GetMember(o, "gameObject"), "activeSelf") is bool b && b; } catch { return false; }
        }

        /// <summary>Гость, первая минута боя, раз в секунду: свой особый обелиск — убрать (DestroyItem: картинки и коллайдер выключены).</summary>
        private void TreasureStep(double now)
        {
            if (_hosting || !WorldOn || _partners.Count == 0 || now < _treasureScanAt || MyClock() > TreasureScanUntil) return;
            _treasureScanAt = now + 1;
            foreach (var x in FindAllOfType("C_Battle_Item_TreasureBox"))
            {
                var key = KeyOf(x);
                if (ItemType(x) != TreasureType || !ActiveObj(x) || !_treasureGone.Add(key)) continue;
                _ctx.Mark("особый обелиск: убираю у гостя");
                try { CallMethod(x, "DestroyItem"); } catch (Exception e) { _ctx.Log("особый обелиск: DestroyItem: " + Inner(e).Message); }
                _treasureRemoved++;
                _ctx.Log("особый обелиск: у гостя убран — он только у хоста");
            }
        }
        private readonly HashSet<object> _treasureGone = new HashSet<object>();
    }
}
