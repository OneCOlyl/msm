using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModKit;
using ModKitData;

namespace CoopCheat
{
    public class CheatMod : IKitMod
    {
        public ModDef Define() => new ModDef
        {
            Id = "coop_cheat",
            Name = "Чит: заклинания",
            Version = typeof(CheatMod).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion,
            Description = "Для проверки: в бою кнопка «ЧИТ» — выдать или снять уровень любого заклинания, способности, комбинации и синергии игры, или поднять уровень героя (окно выбора магии).",
            Depends = { ["modkit_spells"] = ">=1.0", ["modkit_spells_data"] = ">=1.0", ["modkit_effects"] = ">=1.0", ["modkit_data"] = ">=1.0" },
            OnEnable = ctx => _c = new Cheat(ctx),
            OnDisable = ctx => { _c?.Close(); _c = null; },
        };
        private Cheat _c;
    }

    internal sealed class Cheat
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        private static readonly (int id, string name)[] Spells =
        {
            (1, "Пуля"), (2, "Огн. шар"), (3, "Дух"), (4, "Спутник"), (5, "Лёд. иглы"), (6, "Щит"), (7, "Молния"),
            (8, "Электрозона"), (9, "Цунами"), (10, "Метеорит"), (11, "Призрак"), (12, "Вихрь"), (13, "Электричество"), (14, "Армагеддон"),
            (15, "Испепеление"), (16, "Энергопуля"), (17, "Метель"), (18, "Луч"), (19, "Маг. круг"), (20, "Лав. поле"), (21, "Вспышка"),
        };
        private static readonly double[] Gray = { 0.25, 0.25, 0.30, 0.92 };
        private static readonly double[] Green = { 0.18, 0.50, 0.25, 0.95 };
        private static readonly double[] Red = { 0.60, 0.20, 0.20, 0.95 };
        private static readonly double[] Blue = { 0.20, 0.40, 0.80, 0.95 };

        private readonly ModContext _ctx;
        private readonly ISpells _spells;
        private readonly ISpellsData _data;
        private readonly IData _tables;

        // Вкладки (0.4.1): «Магии» — 21 заклинание; дальше — записи таблицы Ability по типам (колонка 유형: заклинания-магии,
        // особенности и пр.), «Комбо» (MagicCom) и «Синергии» (Synergy). Имя — колонка 이름 (в игре английские названия).
        // Комбо и синергии игра даёт сама, когда выучены их составляющие (MagicCom 특성A/B/C레벨, Synergy ID_01…05 — номера
        // способностей Ability): чит учит недостающие составляющие. 0.4.0 клал номер комбо в список выученного — это номер
        // другой способности (Комбо 1 = Пуля), так нельзя.
        private sealed class Row { public int Id; public string Name; public int[] Req; public int Grade = -1; }

        // Редкость артефакта — в каком пуле игры его номер: Z_Database.List_ArtifactPossibleGrade_<буква>, от частых к редким (как в
        // modkit_data: C, B, A, P, S). S игра подписывает «Special Artifact». Пулы читаются при первом открытии меню в бою.
        private static readonly string[] Grades = { "C", "B", "A", "P", "S" };
        private static readonly double[][] GradeColors =
        {
            new[] { 0.35, 0.35, 0.38, 0.95 }, new[] { 0.20, 0.45, 0.80, 0.95 }, new[] { 0.50, 0.25, 0.70, 0.95 },
            new[] { 0.80, 0.55, 0.10, 0.95 }, new[] { 0.75, 0.15, 0.20, 0.95 },
        };
        private sealed class TabDef { public string Label, Kind; public List<Row> Rows = new List<Row>(); }
        private readonly List<TabDef> _tabs = new List<TabDef>();
        private static readonly Dictionary<string, string> TypeNames = new Dictionary<string, string>
        {
            // типы таблицы Ability (журнал 0.4.1): 특성 — особенности заклинаний (Magic Arrow, Fireworks…), 특수패시브 — специальные
            // пассивы (Lord of Fire…), 패시브 — пассивы, 아티팩트 — артефакты, 연구 — исследования, 인챈트 — зачарование, ` — прочее
            ["특성"] = "Особенн.", ["특수패시브"] = "Спец.пасс.", ["패시브"] = "Пассив", ["아티팩트"] = "Артефакты", ["연구"] = "Исслед.",
            ["인챈트"] = "Зачаров.", ["`"] = "Прочее", ["마법"] = "Магия", ["조합"] = "Комбо",
        };
        private static readonly string[] TypeOrder = { "특성", "특수패시브", "패시브", "아티팩트", "`", "연구", "인챈트" };
        // Пустые записи таблиц игры: вместо имени «`» (170 штук в Ability).
        private static bool Blank(string name) => string.IsNullOrWhiteSpace(name) || name.Trim() == "`" || name == "Name";
        private const int PerPage = 21;
        private int _tab, _pageStart;
        private bool _tablesLoaded;
        private readonly List<UiKit.HudButton> _chrome = new List<UiKit.HudButton>();
        private readonly IEffects _effects;
        private readonly Dictionary<int, int> _max = new Dictionary<int, int>();

        private UiKit.HudButton _toggle, _mode, _close, _levelUp;
        private readonly Dictionary<int, UiKit.HudButton> _buttons = new Dictionary<int, UiKit.HudButton>();
        private readonly Dictionary<int, string> _shown = new Dictionary<int, string>();   // текст на кнопке: SetText только при изменении
        private readonly Dictionary<string, Dictionary<int, int>> _counts = new Dictionary<string, Dictionary<int, int>>();   // список игры → сколько раз в нём номер; чистится при каждом обновлении
        private readonly Dictionary<int, int> _abilityMax = new Dictionary<int, int>();   // Ability.최대레벨: больше игра не ждёт — лишние уровни ломали её (Barrier ×10 → Change_ShieldMax вне массива)
        private const string LearnList = "List_AllyAbilityLearn", ArtifactList = "List_ArtifactObtain";
        private bool _open, _minus;
        private Type _db;
        private object _abilityMgr;                                  // V_Ability_Manager: ловится хуком на его Update
        private readonly Dictionary<int, int> _forced = new Dictionary<int, int>();   // выданные читом уровни: держим их перед каждым StartInformation_B

        public Cheat(ModContext ctx)
        {
            _ctx = ctx;
            _spells = ctx.Use<ISpells>("modkit_spells");
            _data = ctx.Use<ISpellsData>("modkit_spells_data");
            try { _tables = ctx.Use<IData>("modkit_data"); } catch (Exception e) { ctx.Log("таблицы игры недоступны (вкладки способностей пустые): " + e.Message); }
            _effects = ctx.Use<IEffects>("modkit_effects");
            try { _db = Hooks.FindType("Z_Database"); } catch (Exception e) { ctx.Log("Z_Database не найден: " + e.Message); }
            // каждый хук отдельно: если какого-то метода нет в этой версии игры, мод всё равно включится
            // менеджер способностей ловим в начале его пересчёта — игра вызывает его в начале каждого боя и при выборе заклинания
            TryHook("V_Ability_Manager.AbilityStatApply", () => ctx.Before("V_Ability_Manager", "AbilityStatApply", 1, (s, a) => _abilityMgr = s));
            TryHook("V_Ability_Manager.ManaGain", () => ctx.Before("V_Ability_Manager", "ManaGain", 1, (s, a) => _abilityMgr = s));
            // StartInformation_B запускает корутины заклинаний по уровням; перед ней возвращаем выданные уровни, если пересчёт их собрал заново
            TryHook("C_Battle_AllyUnit.StartInformation_B", () => ctx.Before("C_Battle_AllyUnit", "StartInformation_B", 0, (s, a) => ctx.Guard("уровни перед запуском", EnforceLevels)));
            ctx.Events.On("battleStart", _ => { _forced.Clear(); ctx.Guard("кнопка", ShowToggle); });
            ctx.Events.On("battleEnd", _ => Close());
            ctx.Every(400, () => ctx.Guard("обновление", Refresh));
            ctx.Every(250, () => ctx.Guard("уровень героя", LevelStep));
            ctx.OnCleanup(Close);
            ctx.Log("чит включён: в бою кнопка «ЧИТ» справа");
        }

        private void TryHook(string what, Action install)
        {
            try { install(); _ctx.Log("хук поставлен: " + what); }
            catch (Exception e) { _ctx.Log($"хук НЕ поставлен: {what}: {e.Message}"); }
        }

        // ------------------------------------------------------------ интерфейс

        private void ShowToggle()
        {
            Kill(ref _toggle);
            _toggle = UiKit.AddHudButton("cheat_toggle", "ЧИТ", 600, 250, 110, 56, 24, Blue, () => _ctx.Guard("меню", Toggle));
        }

        private void Toggle()
        {
            if (_open) { HidePanel(); return; }
            _open = true;
            _ctx.Guard("таблицы", LoadTables);
            ShowPage();
        }

        private void ShowPage()
        {
            ClearPage();
            _counts.Clear();
            if (_tabs.Count == 0) _tabs.Add(new TabDef { Label = "Магии", Kind = "spell", Rows = Spells.Select(x => new Row { Id = x.id, Name = x.name }).ToList() });
            if (_tab >= _tabs.Count) _tab = 0;
            int tabRows = (_tabs.Count + 4) / 5;
            for (int t = 0; t < _tabs.Count; t++)
            {
                int tab = t;
                var td = _tabs[t];
                string text = t == 0 ? td.Label : $"{td.Label} {td.Rows.Count}";
                _chrome.Add(UiKit.AddHudButton("cheat_tab" + t, text, 15 + (t % 5) * 117, 262 - (tabRows - 1 - t / 5) * 60, 112, 54, 15, t == _tab ? Blue : Gray,
                    () => _ctx.Guard("вкладка", () => { _tab = tab; _pageStart = 0; ShowPage(); })));
            }
            var items = Items();
            for (int i = 0; i < PerPage && _pageStart + i < items.Count; i++)
            {
                var r = items[_pageStart + i];
                int col = i % 3, row = i / 3;
                string text = Label(r);
                _shown[r.Id] = text;
                var color = Kind == "artifact" && r.Grade >= 0 ? GradeColors[r.Grade] : Gray;
                _buttons[r.Id] = UiKit.AddHudButton("cheat_" + r.Id, text, 15 + col * 235, 330 + row * 66, 225, 58, 16, color, () => _ctx.Guard("запись", () => PressRow(r)));
            }
            int y = 330 + 7 * 66 + 8;
            _mode = UiKit.AddHudButton("cheat_mode", ModeText(), 15, y, 340, 60, 22, Green, () => _ctx.Guard("режим", () => { _minus = !_minus; _mode?.SetText(ModeText()); }));
            _close = UiKit.AddHudButton("cheat_close", "Закрыть", 370, y, 340, 60, 22, Gray, () => _ctx.Guard("закрыть", HidePanel));
            _levelUp = UiKit.AddHudButton("cheat_levelup", "Уровень героя +1", 15, y + 68, 340, 60, 22, Blue, () => _ctx.Guard("уровень героя", StartLevelUp));
            _chrome.Add(UiKit.AddHudButton("cheat_maxall", "Все магии — максимум", 370, y + 68, 340, 60, 22, Green, () => _ctx.Guard("все магии", MaxAll)));
            if (items.Count > PerPage)
            {
                int pages = (items.Count + PerPage - 1) / PerPage, page = _pageStart / PerPage + 1;
                _chrome.Add(UiKit.AddHudButton("cheat_prev", "◀", 15, y + 136, 160, 60, 24, Gray, () => _ctx.Guard("листать", () => { _pageStart = Math.Max(0, _pageStart - PerPage); ShowPage(); })));
                _chrome.Add(UiKit.AddHudButton("cheat_pagen", $"стр. {page}/{pages}", 185, y + 136, 360, 60, 22, Gray, () => { }));
                _chrome.Add(UiKit.AddHudButton("cheat_next", "▶", 555, y + 136, 160, 60, 24, Gray, () => _ctx.Guard("листать", () => { if (_pageStart + PerPage < items.Count) _pageStart += PerPage; ShowPage(); })));
            }
        }

        private List<Row> Items() => _tab < _tabs.Count ? _tabs[_tab].Rows : new List<Row>();
        private string Kind => _tab < _tabs.Count ? _tabs[_tab].Kind : "spell";

        private void ClearPage()
        {
            foreach (var b in _buttons.Values) { try { b.Destroy(); } catch { } }
            _buttons.Clear();
            _shown.Clear();
            foreach (var b in _chrome) { try { b?.Destroy(); } catch { } }
            _chrome.Clear();
            Kill(ref _mode); Kill(ref _close); Kill(ref _levelUp);
        }

        // ------------------------------------------------------------ таблицы игры (Ability, MagicCom, Synergy)

        private static string NameColumn(List<string> cols) =>
            cols.FirstOrDefault(c => c == "이름") ?? cols.FirstOrDefault(c => c.IndexOf("Name", StringComparison.OrdinalIgnoreCase) >= 0);

        private static string Cell(Table tb, int r, string col) { try { return col == null ? null : Convert.ToString(tb.Get(r, col)); } catch { return null; } }
        private static int CellInt(Table tb, int r, string col) { try { return col == null ? 0 : Convert.ToInt32(tb.Num(r, col)); } catch { return 0; } }

        private static string Short(string name) => name.Length > 18 ? name.Substring(0, 18) : name;

        private void LoadTables()
        {
            if (_tablesLoaded) return;
            if (_tabs.Count == 0) _tabs.Add(new TabDef { Label = "Магии", Kind = "spell", Rows = Spells.Select(x => new Row { Id = x.id, Name = x.name }).ToList() });
            if (_tables == null) return;
            _tablesLoaded = true;
            // Ability: по типам
            try
            {
                var tb = _tables.Table("Ability");
                var cols = tb.Columns();
                string nameCol = NameColumn(cols), typeCol = cols.Contains("유형") ? "유형" : null, maxCol = cols.Contains("최대레벨") ? "최대레벨" : null;
                var byType = new Dictionary<string, TabDef>();
                for (int r = 0; r < tb.Length; r++)
                {
                    int id = CellInt(tb, r, tb.IdColumn);
                    string name = Cell(tb, r, nameCol);
                    if (id <= 0 || Blank(name)) continue;                   // строка 0 — заголовок таблицы; пустые записи
                    _abilityMax[id] = CellInt(tb, r, maxCol);
                    if (id <= 21) continue;                                 // 21 заклинание — на вкладке «Магии»
                    string type = Cell(tb, r, typeCol) ?? "?";
                    if (!byType.TryGetValue(type, out var td))
                        byType[type] = td = new TabDef { Label = TypeNames.TryGetValue(type, out var ru) ? ru : "Тип " + (byType.Count + 1), Kind = type == "아티팩트" ? "artifact" : "ability" };
                    td.Rows.Add(new Row { Id = id, Name = Short(name) });
                }
                foreach (var td in byType.Values.Where(t => t.Kind == "artifact")) SortByGrade(td);
                // особенности и спец. пассивы — первыми (это «специальные магии»), исследования и зачарование — в конце
                foreach (var kv in byType.OrderBy(kv => Array.IndexOf(TypeOrder, kv.Key) < 0 ? 99 : Array.IndexOf(TypeOrder, kv.Key))) _tabs.Add(kv.Value);
                _ctx.Log($"таблица Ability: типы {string.Join(", ", byType.Select(kv => $"{kv.Key} ({kv.Value.Label}) {kv.Value.Rows.Count}"))}");
                foreach (var kv in byType)
                    for (int i = 0; i < kv.Value.Rows.Count; i += 60)
                        _ctx.Log($"  {kv.Key}: " + string.Join(", ", kv.Value.Rows.Skip(i).Take(60).Select(x => $"{x.Id} {x.Name}")));
            }
            catch (Exception e) { _ctx.Log("таблица Ability: " + Err(e)); }
            // комбинации и синергии: составляющие — номера способностей
            AddReqTab("MagicCom", "Комбо", "combo", new[] { "특성A레벨", "특성B레벨", "특성C레벨" });
            AddReqTab("Synergy", "Синергии", "syn", new[] { "ID_01", "ID_02", "ID_03", "ID_04", "ID_05" });
        }

        private void SortByGrade(TabDef td)
        {
            var grade = new Dictionary<int, int>();
            for (int g = 0; g < Grades.Length; g++)
            {
                try { foreach (var id in Items("List_ArtifactPossibleGrade_" + Grades[g])) grade[id] = g; }
                catch (Exception e) { _ctx.Log($"пул редкости {Grades[g]}: {Err(e)}"); }
            }
            foreach (var r in td.Rows) r.Grade = grade.TryGetValue(r.Id, out var g) ? g : -1;
            td.Rows = td.Rows.OrderByDescending(r => r.Grade).ThenBy(r => r.Id).ToList();
            _ctx.Log("артефакты по редкости: " + string.Join(", ", Enumerable.Range(-1, Grades.Length + 1).Reverse()
                .Select(g => $"{(g < 0 ? "?" : Grades[g])} {td.Rows.Count(r => r.Grade == g)}")));
        }

        private void AddReqTab(string table, string label, string kind, string[] reqCols)
        {
            try
            {
                var tb = _tables.Table(table);
                var cols = tb.Columns();
                string nameCol = NameColumn(cols);
                var td = new TabDef { Label = label, Kind = kind };
                for (int r = 0; r < tb.Length; r++)
                {
                    int id = CellInt(tb, r, tb.IdColumn);
                    string name = Cell(tb, r, nameCol);
                    if (id <= 0 || Blank(name)) continue;
                    var req = reqCols.Where(cols.Contains).Select(c => CellInt(tb, r, c)).Where(x => x > 0).ToArray();
                    td.Rows.Add(new Row { Id = id, Name = Short(name), Req = req });
                }
                _tabs.Add(td);
                for (int i = 0; i < td.Rows.Count; i += 40)
                    _ctx.Log($"таблица {table}: " + string.Join(", ", td.Rows.Skip(i).Take(40).Select(x => $"{x.Id} {x.Name} [{string.Join("+", x.Req)}]")));
            }
            catch (Exception e) { _ctx.Log($"таблица {table}: {Err(e)}"); }
        }

        private static string Err(Exception e) => ((e as TargetInvocationException)?.InnerException ?? e).Message;

        private string ModeText() => _minus ? "Режим: −1 уровень" : "Режим: +1 уровень";

        private void HidePanel()
        {
            _open = false;
            ClearPage();
        }

        public void Close()
        {
            HidePanel();
            Kill(ref _toggle);
            _portion = -1; _levelQueue = 0;
        }

        private static void Kill(ref UiKit.HudButton b) { try { b?.Destroy(); } catch { } b = null; }

        private void Refresh()
        {
            if (!_open) return;
            _counts.Clear();
            foreach (var r in Items())
            {
                if (!_buttons.TryGetValue(r.Id, out var b)) continue;
                string text = Label(r);
                if (_shown.TryGetValue(r.Id, out var old) && old == text) continue;
                _shown[r.Id] = text;
                try { b.SetText(text); } catch { }
            }
        }

        private int Max(int id)
        {
            if (_max.TryGetValue(id, out var m)) return m;
            try { m = _data.Get(id)?.MaxLevel ?? 0; } catch { m = 0; }
            if (m <= 0) m = 8;
            _max[id] = m;
            return m;
        }

        private string Label(Row r)
        {
            switch (Kind)
            {
                case "ability": return $"{r.Name} {Count(ListFor(Kind), r.Id)}/{AbilityMax(r.Id)}";
                case "artifact": return $"{(r.Grade >= 0 ? Grades[r.Grade] : "?")} {r.Name} {Count(ListFor(Kind), r.Id)}/{AbilityMax(r.Id)}";
                case "combo":
                case "syn": { int have = r.Req.Count(x => Count(ListFor(Kind), x) > 0); return $"{r.Name} {have}/{r.Req.Length}"; }
                default: { int lv = 0; try { lv = _spells.Level(r.Id); } catch { } return $"{r.Name} {lv}/{Max(r.Id)}"; }
            }
        }

        // Артефакты игра держит отдельно от способностей (Z_Database.List_ArtifactObtain), составляющие синергий — артефакты.
        private static string ListFor(string kind) => kind == "artifact" || kind == "syn" ? ArtifactList : LearnList;

        private int AbilityMax(int id) => _abilityMax.TryGetValue(id, out var m) && m > 0 ? m : 1;

        // Список читается один раз до сброса _counts: перебор на каждую кнопку с ростом списка тормозил меню.
        private int Count(string list, int id)
        {
            if (!_counts.TryGetValue(list, out var counts))
            {
                _counts[list] = counts = new Dictionary<int, int>();
                foreach (var x in Items(list)) counts[x] = counts.TryGetValue(x, out var k) ? k + 1 : 1;
            }
            return counts.TryGetValue(id, out var c) ? c : 0;
        }

        private List<int> Items(string list)
        {
            var res = new List<int>();
            var l = Static(list);
            if (l == null) return res;
            int n = Convert.ToInt32(Invoke(l, "get_Count"));
            for (int i = 0; i < n; i++) res.Add(Convert.ToInt32(Invoke(l, "get_Item", i)));
            return res;
        }

        // ------------------------------------------------------------ уровень героя: окно выбора магии

        // ManaGain прибавляет ману к Z_Database.AllyUnitGlobal_ManaStock и, когда набрано AllyUnitGlobal_ManaCost, сразу
        // открывает окно выбора, если оно ещё не открыто. Если окно не открылось, следующие порции в 1,5 раза больше
        private const double FirstPortion = 10, MaxPortion = 200000;
        private double _portion = -1, _given;
        private int _levelQueue;

        // Не Ability_SelectON: флаг бывает True и после закрытия окна, тогда очередь уровней не разбиралась.
        private static readonly string[] SelectCanvases = { "Obj_CanvasAbility", "Obj_CanvasAbilityCh", "Obj_CanvasMagicCom", "Obj_CanvasUltimate" };

        private bool SelectOpen()
        {
            var mgr = _abilityMgr;
            if (mgr == null) return false;
            foreach (var name in SelectCanvases)
            {
                var p = mgr.GetType().GetProperty(name, Any);
                object go = p != null ? p.GetValue(mgr) : mgr.GetType().GetField(name, Any)?.GetValue(mgr);
                if (go != null && Invoke(go, "get_activeSelf") is bool b && b) return true;
            }
            return false;
        }

        private void StartLevelUp()
        {
            if (!_spells.InBattle) { _ctx.Toast("Только в бою", 2); return; }
            if (_abilityMgr == null) { _ctx.Toast("Подберите немного маны и нажмите снова", 3); _ctx.Log("уровень героя: менеджер способностей ещё не пойман"); return; }
            HidePanel();                                              // окно выбора магии не должно быть закрыто кнопками чита
            if (_portion > 0 || SelectOpen()) { _levelQueue++; _ctx.Toast($"Ещё уровень в очереди ({_levelQueue})", 2); return; }
            _portion = ManaToLevel(); _given = 0;
            GiveMana();
        }

        private double ManaToLevel()
        {
            if (Static("AllyUnitGlobal_ManaCost") is object cost && Static("AllyUnitGlobal_ManaStock") is object stock)
                return Math.Max(1, Convert.ToDouble(cost) - Convert.ToDouble(stock) + 0.01);
            return FirstPortion;
        }

        private void GiveMana()
        {
            var m = _abilityMgr.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == "ManaGain" && x.GetParameters().Length == 1)
                    ?? throw new MissingMethodException("V_Ability_Manager.ManaGain");
            var arg = Convert.ChangeType(_portion, m.GetParameters()[0].ParameterType);
            _ctx.Mark("чит: ManaGain");
            m.Invoke(_abilityMgr, new[] { arg });
            _given += _portion;
        }

        private void LevelStep()
        {
            if (_portion < 0)
            {
                if (_levelQueue > 0 && _spells.InBattle && !SelectOpen() && !_spells.Paused) { _levelQueue--; _portion = ManaToLevel(); _given = 0; GiveMana(); }
                return;
            }
            if (!_spells.InBattle) { _portion = -1; _levelQueue = 0; return; }
            if (SelectOpen()) { _ctx.Log($"уровень героя: окно выбора открылось, выдано маны {_given:0}"); _portion = -1; return; }
            if (_spells.Paused) return;                               // пауза — ждём, не добавляем
            if (_portion >= MaxPortion) { _ctx.Log($"уровень героя: окно не открылось, выдано маны {_given:0}"); _ctx.Toast("Уровень не поднялся (см. журнал)", 3); _portion = -1; _levelQueue = 0; return; }
            _portion *= 1.5;
            GiveMana();
        }

        // ------------------------------------------------------------ выдача уровня

        // В конце AbilityStatApply игра сама вызывает StartInformation_B.
        private bool Recalc()
        {
            var mgr = _abilityMgr;
            if (mgr == null)
            {
                bool viaLib = false;
                try { viaLib = _effects.Recalc(); } catch { }
                if (!viaLib) _ctx.Log("пересчёт: менеджер способностей ещё не пойман (он ловится при первом пересчёте игры — например, после подбора заклинания)");
                return viaLib;
            }
            _ctx.Mark("чит: AbilityStatApply(2)");
            try { Invoke(mgr, "AbilityStatApply", 2); return true; }
            catch (Exception e) { _ctx.Log("пересчёт: " + ((e as TargetInvocationException)?.InnerException ?? e).Message); return false; }
        }

        private void EnforceLevels()
        {
            if (_forced.Count == 0) return;
            var levels = Static("List_AllyMagicLevel");
            if (levels == null) return;
            foreach (var kv in _forced)
            {
                int cur = Convert.ToInt32(Invoke(levels, "get_Item", kv.Key));
                if (cur != kv.Value) Invoke(levels, "set_Item", kv.Key, kv.Value);
            }
        }

        private readonly Dictionary<string, MemberInfo> _statics = new Dictionary<string, MemberInfo>();
        private object Static(string name)
        {
            if (_db == null) return null;
            if (!_statics.TryGetValue(name, out var mi))
                _statics[name] = mi = (MemberInfo)_db.GetProperty(name, Any) ?? _db.GetField(name, Any);
            return mi is PropertyInfo p ? p.GetValue(null) : (mi as FieldInfo)?.GetValue(null);
        }

        private static readonly Dictionary<(Type, string, int), MethodInfo> Methods = new Dictionary<(Type, string, int), MethodInfo>();
        private static object Invoke(object o, string name, params object[] args)
        {
            var key = (o.GetType(), name, args.Length);
            if (!Methods.TryGetValue(key, out var m))
                Methods[key] = m = o.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Length)
                    ?? throw new MissingMethodException(o.GetType().Name + "." + name);
            return m.Invoke(o, args);
        }

        private void MaxAll()
        {
            if (!_spells.InBattle) { _ctx.Toast("Только в бою", 2); return; }
            var learn = Static("List_AllyAbilityLearn");
            var want = new List<(int id, string name, int max)>();
            int added = 0;
            _ctx.Mark("чит: все магии — максимум");
            foreach (var (id, name) in Spells)
            {
                int cur, max;
                try { cur = _spells.Level(id); max = Max(id); } catch { continue; }
                if (max <= 0 || cur >= max) continue;
                want.Add((id, name, max));
                if (learn != null)
                    for (int k = cur; k < max; k++) { try { Invoke(learn, "Add", id); added++; } catch { break; } }
            }
            if (want.Count == 0) { _ctx.Toast("Все магии уже на максимуме", 2); return; }
            bool ok = added > 0 && Recalc();
            int direct = 0, failed = 0;
            foreach (var (id, name, max) in want)
            {
                if (_spells.Level(id) >= max) continue;
                if (TryDirect(id, max)) direct++; else failed++;
            }
            _ctx.Log($"чит: все магии — максимум: заклинаний {want.Count}, записей в список выученного {added} (пересчёт {(ok ? "прошёл" : "не прошёл")}), прямой записью {direct}, не вышло {failed}");
            _ctx.Toast(failed == 0 ? $"Все магии на максимуме ({want.Count})" : $"Магии на максимуме, кроме {failed} (см. журнал)", 3);
            Refresh();
        }

        private void Press(int id, string name)
        {
            if (!_spells.InBattle) { _ctx.Toast("Только в бою", 2); return; }
            int cur = _spells.Level(id), max = Max(id);
            if (!_minus && cur >= max) { _ctx.Toast($"{name}: уже максимум", 2); return; }
            if (_minus && cur <= 0) { _ctx.Toast($"{name}: нет уровней", 2); return; }
            int want = _minus ? cur - 1 : cur + 1;
            string how = TryLearnList(id, want) ? "список выученного" : TryDirect(id, want) ? "прямая запись" : null;
            int now = _spells.Level(id);
            _ctx.Log($"{name} (ID {id}): {cur} → {now} (нужно {want}), способ: {how ?? "не сработал"}");
            _ctx.Toast(how != null ? $"{name}: уровень {now}" : $"{name}: не удалось (см. журнал)", 2);
            Refresh();
        }

        private void PressRow(Row r)
        {
            if (Kind == "spell") { Press(r.Id, r.Name); return; }
            if (!_spells.InBattle) { _ctx.Toast("Только в бою", 2); return; }
            string listName = ListFor(Kind);
            var list = Static(listName);
            if (list == null) { _ctx.Toast($"{listName} не найден", 2); return; }
            bool single = Kind == "ability" || Kind == "artifact";
            var ids = single ? new[] { r.Id } : r.Req;
            if (ids.Length == 0) { _ctx.Toast($"{r.Name}: составляющих нет", 2); return; }
            var synBefore = listName == ArtifactList ? Items("List_ApplySynergyON") : null;
            var changed = new List<string>();
            foreach (var id in ids)
            {
                int before = Count(listName, id);
                if (_minus) { if (before > 0) { Invoke(list, "Remove", id); changed.Add($"{id}: {before}→{before - 1}"); } }
                else if (single ? before < AbilityMax(id) : before == 0) { Invoke(list, "Add", id); changed.Add($"{id}: {before}→{before + 1}"); }
                _counts.Clear();
            }
            if (changed.Count == 0) { _ctx.Toast(_minus ? $"{r.Name}: нечего снимать" : single ? $"{r.Name}: уже максимум" : $"{r.Name}: уже всё есть", 2); return; }
            bool ok = Recalc();
            string syn = synBefore == null ? "" : $"; синергии игры: [{string.Join(",", synBefore)}] → [{string.Join(",", Items("List_ApplySynergyON"))}]";
            _ctx.Log($"{_tabs[_tab].Label} «{r.Name}» ({r.Id}): {listName} {string.Join(", ", changed)}; пересчёт {(ok ? "прошёл" : "не прошёл")}{syn}");
            _ctx.Toast($"{r.Name}: {Label(r)}", 2);
            Refresh();
        }

        // Каждая запись в List_AllyAbilityLearn — один уровень.
        private bool TryLearnList(int id, int want)
        {
            var learn = Static("List_AllyAbilityLearn");
            if (learn == null) { _ctx.Log("List_AllyAbilityLearn нет"); return false; }
            int before = _spells.Level(id);
            _ctx.Mark("чит: список выученного");
            try
            {
                if (want > before) Invoke(learn, "Add", id);
                else if (!(bool)Invoke(learn, "Remove", id)) return false;
                bool ok = Recalc();
                int after = _spells.Level(id);
                if (after == want) return true;
                // не сработало — вернуть список как был
                if (want > before) Invoke(learn, "Remove", id); else Invoke(learn, "Add", id);
                Recalc();
                _ctx.Log($"список выученного не дал уровня (пересчёт {(ok ? "прошёл" : "не прошёл")}, уровень {after})");
                return false;
            }
            catch (Exception e) { _ctx.Log("список выученного: " + ((e as TargetInvocationException)?.InnerException ?? e).Message); return false; }
        }

        private bool TryDirect(int id, int want)
        {
            var levels = Static("List_AllyMagicLevel");
            if (levels == null) { _ctx.Log("List_AllyMagicLevel нет"); return false; }
            _ctx.Mark("чит: прямая запись");
            try
            {
                if (want > 0) _forced[id] = want; else _forced.Remove(id);
                Invoke(levels, "set_Item", id, want);
                bool ok = Recalc();                                   // пересчёт → StartInformation_B → EnforceLevels ставит уровень → корутина запускается
                int after = _spells.Level(id);
                if (after != want) { Invoke(levels, "set_Item", id, want); after = want; }
                _ctx.Log($"прямая запись: пересчёт {(ok ? "прошёл" : "НЕ прошёл — заклинание запустится при следующем пересчёте игры")}");
                return after == want;
            }
            catch (Exception e) { _ctx.Log("прямая запись: " + ((e as TargetInvocationException)?.InnerException ?? e).Message); return false; }
        }
    }
}
