using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ModKit;
using ModKitData;
using UnityEngine;
using UnityEngine.UI;

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
        private static readonly double[] Blue = { 0.20, 0.40, 0.80, 0.95 };
        private static readonly double[] GoldTab = { 0.62, 0.48, 0.16, 0.95 };
        private static readonly double[] Page = { 0.12, 0.12, 0.13, 0.96 };
        private static readonly double[] Clear = { 0.0, 0.0, 0.0, 0.01 };
        private static readonly double[] Red = { 0.60, 0.20, 0.20, 0.95 };

        private readonly ModContext _ctx;
        private readonly ISpells _spells;
        private readonly ISpellsData _data;
        private readonly IData _tables;

        // Вкладки (0.4.1): «Магии» — 21 заклинание; дальше — записи таблицы Ability по типам (колонка 유형: заклинания-магии,
        // особенности и пр.), «Комбо» (MagicCom) и «Синергии» (Synergy). Имя — колонка 이름 (в игре английские названия).
        // Комбо и синергии игра даёт сама, когда выучены их составляющие (MagicCom 특성A/B/C레벨, Synergy ID_01…05 — номера
        // способностей Ability): чит учит недостающие составляющие. 0.4.0 клал номер комбо в список выученного — это номер
        // другой способности (Комбо 1 = Пуля), так нельзя.
        private sealed class Row { public int Id; public string Name; public int[] Req; public int Grade = -1; public int Icon; public int Slot; public TabDef Tab; }

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
        private readonly Dictionary<int, List<int>> _traits = new Dictionary<int, List<int>>();   // базовое заклинание → его особенности (특성)
        private static readonly Dictionary<string, string> TypeNames = new Dictionary<string, string>
        {
            // типы таблицы Ability (журнал 0.4.1): 특성 — особенности заклинаний (Magic Arrow, Fireworks…), 특수패시브 — специальные
            // пассивы (Lord of Fire…), 패시브 — пассивы, 아티팩트 — артефакты, 연구 — исследования, 인챈트 — зачарование, ` — прочее
            ["특성"] = "Особенн.", ["특수패시브"] = "Спец.пасс.", ["패시브"] = "Пассив", ["아티팩트"] = "Артефакты", ["연구"] = "Исслед.",
            ["인챈트"] = "Зачаров.", ["`"] = "Прочее", ["마법"] = "Магия", ["조합"] = "Комбо",
        };
        private static readonly string[] TypeOrder = { "특성", "특수패시브", "패시브", "아티팩트", "`", "연구", "인챈트" };
        private static readonly Dictionary<string, string> Headers = new Dictionary<string, string>
        {
            ["Особенн."] = "Особенности", ["Спец.пасс."] = "Спец. пассивы", ["Пассив"] = "Пассивы",
            ["Исслед."] = "Исследования", ["Зачаров."] = "Зачарование",
        };
        // Пустые записи таблиц игры: вместо имени «`» (170 штук в Ability).
        private static bool Blank(string name) => string.IsNullOrWhiteSpace(name) || name.Trim() == "`" || name == "Name";
        private const double CardW = 118, CardH = 150, CardGap = 8;
        private int _tab;
        private bool _tablesLoaded;
        private readonly IEffects _effects;
        private readonly Dictionary<int, int> _max = new Dictionary<int, int>();

        private UiKit.HudButton _toggle;
        private GameObject _panel;
        private RectTransform _content;
        private readonly Dictionary<int, Text> _labels = new Dictionary<int, Text>();
        private readonly Dictionary<int, Image> _icons = new Dictionary<int, Image>();
        private readonly Dictionary<int, bool> _lit = new Dictionary<int, bool>();
        private readonly Dictionary<int, string> _shown = new Dictionary<int, string>();   // текст уровня: меняется только при изменении
        private readonly Dictionary<string, Dictionary<int, int>> _counts = new Dictionary<string, Dictionary<int, int>>();   // список игры → сколько раз в нём номер; чистится при каждом обновлении
        private readonly Dictionary<int, int> _abilityMax = new Dictionary<int, int>();   // Ability.최대레벨: больше игра не ждёт — лишние уровни ломали её (Barrier ×10 → Change_ShieldMax вне массива)
        private const string LearnList = "List_AllyAbilityLearn", ArtifactList = "List_ArtifactObtain";
        private bool _open, _minus;
        private Type _db;
        private object _abilityMgr;                                  // V_Ability_Manager: ловится хуком на его Update
        private readonly Dictionary<int, int> _forced = new Dictionary<int, int>();   // выданные читом уровни: держим их перед каждым StartInformation_B
        private readonly Dictionary<int, List<int>> _traitLevels = new Dictionary<int, List<int>>();   // магия → уровни, на которых игра предлагает особенность
        private Dictionary<int, nint> _sheetIcons;   // ID особенности → спрайт из нижнего ряда игрового окна магии
        private static readonly byte[][] _slotIcons = new byte[6][];

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
            if (_tab < 0 || _tab >= _tabs.Count) _tab = 0;
            _toggle?.SetVisible(false);

            var win = UiKit.Node("cheat_book", UiKit.CanvasTf);
            _panel = win.go;
            UiKit.Stretch(win.rt);
            UiKit.AddImage(win.go, 0.02, 0.02, 0.03, 0.95);

            UiKit.Tint(UiKit.Label(win.rt, Header(), 0, 26, 720, 62, 34, 4), UiKit.Gold);
            UiKit.AddButton(win.rt, "X", 636, 24, 64, 64, 38, Clear, HidePanel);

            const int cols = 5;
            int tabRows = (_tabs.Count + cols - 1) / cols;
            for (int t = 0; t < _tabs.Count; t++)
            {
                int tab = t;
                UiKit.AddButton(win.rt, TabText(_tabs[t]), 10 + (t % cols) * 142, 96 + (t / cols) * 46, 134, 40, 12,
                    t == _tab ? GoldTab : Gray, () => _ctx.Guard("вкладка", () => { _tab = tab; ShowPage(); }));
            }

            double pageY = 96 + tabRows * 46 + 8;
            double actionsY = UiKit.ScreenH - 58;
            double pageH = actionsY - 10 - pageY;
            UiKit.AddButton(win.rt, "", 22, pageY, 676, pageH, 0, Page, () => { });
            _content = UiKit.AddScroll(win.rt, 32, pageY + 10, 656, pageH - 20);

            var items = Items();
            double height = 8 + ((items.Count + cols - 1) / cols) * (CardH + CardGap);
            UiKit.SetContentHeight(_content, 660, Math.Max(CardH + 16, height));
            for (int i = 0; i < items.Count; i++)
                AddCard(items[i], 8 + (i % cols) * (CardW + CardGap), 8 + (i / cols) * (CardH + CardGap));

            UiKit.AddButton(win.rt, "Уровень героя +1", 22, actionsY, 320, 48, 13, Blue, () => _ctx.Guard("уровень героя", StartLevelUp));
            UiKit.AddButton(win.rt, "Все магии — максимум", 372, actionsY, 326, 48, 13, Green, () => _ctx.Guard("все магии", MaxAll));
        }

        private void AddCard(Row r, double x, double y)
        {
            bool lit = Learned(r);
            var border = Kind == "artifact" && r.Grade >= 0 ? GradeColors[r.Grade] : Gray;
            r.Tab = _tabs[_tab];
            var card = UiKit.AddButton(_content, "", x, y, CardW, CardH, 0, border, () => _ctx.Guard("описание", () => OpenDetail(r)));
            var tf = card.transform;

            var inner = UiKit.Node("in", tf);
            UiKit.Place(inner.rt, 3, 3, CardW - 6, CardH - 6);
            UiKit.AddImage(inner.go, 0.06, 0.07, 0.10, 0.98).raycastTarget = false;

            nint sprite = IconSprite(r);
            if (sprite != IntPtr.Zero)
            {
                var icon = UiKit.Node("icon", tf);
                UiKit.Place(icon.rt, 7, 7, CardW - 14, CardH - 48);
                var img = UiKit.AddImage(icon.go, 1, 1, 1, 1);
                img.raycastTarget = false;
                img.preserveAspect = true;
                img.color = UiKit.Col(1, 1, 1, lit ? 0.96 : 0.35);
                UiKit.SetSprite(img, sprite);
                _icons[r.Id] = img;
            }
            else
            {
                UiKit.Tint(UiKit.Label(tf, Initial(r.Name), 4, 8, CardW - 8, CardH - 52, 30, 4), UiKit.Gold);
            }

            UiKit.Tint(UiKit.Label(tf, CardName(r.Name), 3, CardH - 40, CardW - 6, 16, 10, 4), UiKit.Col(0.82, 0.85, 0.90, 0.95));
            _labels[r.Id] = UiKit.Tint(UiKit.Label(tf, LevelText(r), 3, CardH - 24, CardW - 6, 18, 13, 4), UiKit.Gold);
            _shown[r.Id] = LevelText(r);
            _lit[r.Id] = lit;
        }

        private List<Row> Items() => _tab < _tabs.Count ? _tabs[_tab].Rows : new List<Row>();
        private string Kind => _tab < _tabs.Count ? _tabs[_tab].Kind : "spell";

        private string Header()
        {
            switch (Kind)
            {
                case "spell": return "Заклинания";
                case "artifact": return "Артефакты";
                case "combo": return "Комбинации";
                case "syn": return "Синергии";
                default: return Headers.TryGetValue(_tabs[_tab].Label, out var title) ? title : _tabs[_tab].Label;
            }
        }

        private static string TabText(TabDef td) => $"{td.Label} {td.Rows.Count}";
        private static string CardName(string name) => name.Length > 15 ? name.Substring(0, 14) + "…" : name;
        private static string Initial(string name) => string.IsNullOrEmpty(name) ? "?" : name.Substring(0, 1).ToUpperInvariant();

        private static byte[] SlotIcon(int slot)
        {
            if (slot < 0 || slot >= _slotIcons.Length) return null;
            if (_slotIcons[slot] != null) return _slotIcons[slot];
            var asm = typeof(Cheat).Assembly;
            string name = asm.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith($"slot{slot}.png"));
            if (name == null) return null;
            using var s = asm.GetManifestResourceStream(name);
            if (s == null) return null;
            using var ms = new System.IO.MemoryStream();
            s.CopyTo(ms);
            return _slotIcons[slot] = ms.ToArray();
        }

        private Dictionary<int, nint> ShowIcons()
        {
            var res = new Dictionary<int, nint>();
            try
            {
                var asm = _db?.GetType().Assembly;
                var showType = asm?.GetType("V_AbilityShow") ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("V_AbilityShow")).FirstOrDefault(x => x != null);
                var sheetType = asm?.GetType("X_Select_AbilityShow") ?? AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("X_Select_AbilityShow")).FirstOrDefault(x => x != null);
                if (sheetType == null) return res;
                var idField = sheetType.GetField("Apply_AbilityID", Any);
                var imgField = sheetType.GetField("Img_AbilityPortrait", Any);
                void Add(object sheet)
                {
                    if (sheet == null || res.Count >= 32) return;
                    int id = Convert.ToInt32(idField?.GetValue(sheet) ?? 0);
                    var img = imgField?.GetValue(sheet) as Image;
                    var sprite = img != null ? img.GetType().GetProperty("sprite")?.GetValue(img) : null;
                    if (id == 0 || sprite == null) return;
                    var ptr = sprite.GetType().GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance)?.GetValue(sprite);
                    if (ptr is nint p && p != IntPtr.Zero) res[id] = p;
                }

                // Ресурсный поиск в IL2CPP может быть вырезан; обходим активную сцену и её Canvas.
                try
                {
                    var sceneType = Type.GetType("UnityEngine.SceneManagement.SceneManager, UnityEngine.CoreModule");
                    var scene = sceneType?.GetMethod("GetActiveScene", Type.EmptyTypes)?.Invoke(null, null);
                    var roots = scene?.GetType().GetMethod("GetRootGameObjects", Type.EmptyTypes)?.Invoke(scene, null) as Array;
                    if (roots != null) foreach (var root in roots)
                    {
                        var get = root?.GetType().GetMethod("GetComponentsInChildren", new[] { typeof(Type), typeof(bool) });
                        if (get == null) continue;
                        var comps = get.Invoke(root, new object[] { sheetType, true }) as Array;
                        if (comps != null) foreach (var c in comps) Add(c);
                    }
                }
                catch { }

                var find = typeof(Resources).GetMethod("FindObjectsOfTypeAll", new[] { typeof(Type) })
                    ?? typeof(UnityEngine.Object).GetMethod("FindObjectsOfType", new[] { typeof(Type) });
                var sheets = find != null ? find.Invoke(null, new object[] { sheetType }) as Array : null;
                if (sheets != null) foreach (var sheet in sheets) Add(sheet);
                var shows = find != null && showType != null ? find.Invoke(null, new object[] { showType }) as Array : null;
                var listField = showType?.GetField("List_ObjAbilityShowSheet", Any);
                if (shows != null) foreach (var show in shows)
                {
                    if (!(listField?.GetValue(show) is System.Collections.IEnumerable list)) continue;
                    foreach (var item in list)
                    {
                        if (!(item is GameObject go)) continue;
                        Add(go.GetType().GetMethod("GetComponent", new[] { typeof(Type) })?.Invoke(go, new object[] { sheetType }));
                    }
                }
            }
            catch { }
            return res;
        }

        private nint IconSprite(Row r)
        {
            if (r.Tab?.Kind == "ability" && _sheetIcons != null && _sheetIcons.TryGetValue(r.Id, out var sheet)) return sheet;
            string field = Kind == "combo" ? "SpriteList_MagicComPortrait" : Kind == "syn" ? "SpriteList_SynergyPortrait" : "SpriteList_AbilityPortrait";
            nint sprite = UiKit.GameSprite(field, r.Icon > 0 ? r.Icon : r.Id);
            if (sprite == IntPtr.Zero && Kind == "ability") { sprite = UiKit.GameSprite("SpriteList_AbilityChPortrait", r.Slot); if (sprite == IntPtr.Zero) sprite = UiKit.GameSprite("SpriteList_AbilityChPortrait", r.Slot + 1); }
            if (sprite == IntPtr.Zero && Kind == "syn") sprite = UiKit.GameSprite("SpriteList_SynergyNum", r.Id);
            return sprite;
        }

        private bool Learned(Row r)
        {
            switch (Kind)
            {
                case "spell": try { return _spells.Level(r.Id) > 0; } catch { return false; }
                case "combo":
                case "syn": { var req = r.Req ?? Array.Empty<int>(); return req.Length > 0 && req.All(x => Count(ListFor(Kind), x) > 0); }
                default: return Count(ListFor(Kind), r.Id) > 0;
            }
        }

        private void ClearPage()
        {
            if (_panel != null) UiKit.SafeDestroy(_panel);
            _panel = null;
            _content = null;
            _labels.Clear();
            _icons.Clear();
            _lit.Clear();
            _shown.Clear();
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
                string derivedCol = cols.Contains("파생기술ID") ? "파생기술ID" : null;
                var byType = new Dictionary<string, TabDef>();
                for (int r = 0; r < tb.Length; r++)
                {
                    int id = CellInt(tb, r, tb.IdColumn);
                    string name = Cell(tb, r, nameCol);
                    if (id <= 0 || Blank(name)) continue;                   // строка 0 — заголовок таблицы; пустые записи
                    _abilityMax[id] = CellInt(tb, r, maxCol);
                    if (id <= 21)
                    {
                        var levels = new[] { "특성A레벨", "특성B레벨" }.Select(c => CellInt(tb, r, c)).Where(x => x > 0).Distinct().OrderBy(x => x).ToList();
                        if (levels.Count > 0) _traitLevels[id] = levels;
                        continue;                                           // 21 заклинание — на вкладке «Магии»
                    }
                    string type = Cell(tb, r, typeCol) ?? "?";
                    if (type == "연구") continue;                         // исследования — мета-прогресс главного меню
                    if (!byType.TryGetValue(type, out var td))
                        byType[type] = td = new TabDef { Label = TypeNames.TryGetValue(type, out var ru) ? ru : "Тип " + (byType.Count + 1), Kind = type == "아티팩트" ? "artifact" : "ability" };
                    int derived = type == "특성" ? CellInt(tb, r, derivedCol) : 0;
                    int slot = type == "특성" ? CellInt(tb, r, "특성ID") : 0;
                    int icon = id;
                    if (type == "`" && id >= 361 && id <= 541) icon = (id - 361) / 9 + 1;
                    var newRow = new Row { Id = id, Name = Short(name), Icon = icon, Slot = slot };
                    td.Rows.Add(newRow);
                    if (derived > 0)
                    {
                        if (!_traits.TryGetValue(derived, out var list)) _traits[derived] = list = new List<int>();
                        newRow.Slot = list.Count;
                        list.Add(id);
                    }
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

        private void HidePanel()
        {
            _open = false;
            ClearPage();
            _toggle?.SetVisible(true);
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
            if (!_open || _panel == null) return;
            _counts.Clear();
            foreach (var r in Items())
            {
                string text = LevelText(r);
                if (!_shown.TryGetValue(r.Id, out var old) || old != text)
                {
                    _shown[r.Id] = text;
                    if (_labels.TryGetValue(r.Id, out var label)) { try { label.text = text; } catch { } }
                }
                bool lit = Learned(r);
                if (_lit.TryGetValue(r.Id, out var was) && was == lit) continue;
                _lit[r.Id] = lit;
                if (_icons.TryGetValue(r.Id, out var icon)) { try { icon.color = UiKit.Col(1, 1, 1, lit ? 0.96 : 0.35); } catch { } }
            }
        }

        private int GameMax(int id)
        {
            if (_max.TryGetValue(id, out var m)) return m;
            try { m = _data.Get(id)?.MaxLevel ?? 0; } catch { m = 0; }
            if (m <= 0) m = 8;
            _max[id] = m;
            return m;
        }

        private int Max(int id)
        {
            int game = GameMax(id);
            if (!_traitLevels.TryGetValue(id, out var levels) || levels.Count == 0) return game;
            try
            {
                int cur = _spells.Level(id);
                foreach (var gate in levels)
                    if (gate > cur) return Math.Min(gate - 1, game);
            }
            catch { }
            return game;
        }

        private string LevelText(Row r)
        {
            switch (Kind)
            {
                case "ability":
                case "artifact": return $"{Count(ListFor(Kind), r.Id)}/{AbilityMax(r.Id)}";
                case "combo":
                case "syn": { var req = r.Req ?? Array.Empty<int>(); return $"{req.Count(x => Count(ListFor(Kind), x) > 0)}/{req.Length}"; }
                default: { int lv = 0; try { lv = _spells.Level(r.Id); } catch { } return $"{lv}/{GameMax(r.Id)}"; }
            }
        }

        private string Label(Row r) => Kind == "artifact"
            ? $"{(r.Grade >= 0 ? Grades[r.Grade] : "?")} {r.Name} {LevelText(r)}"
            : $"{r.Name} {LevelText(r)}";

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
            if (!Alive(mgr)) return false;
            foreach (var name in SelectCanvases)
            {
                object go;
                try
                {
                    var p = mgr.GetType().GetProperty(name, Any);
                    go = p != null ? p.GetValue(mgr) : mgr.GetType().GetField(name, Any)?.GetValue(mgr);
                }
                catch { continue; }
                if (go == null) continue;
                try { if (Invoke(go, "get_activeSelf") is bool b && b) return true; } catch { }
            }
            return false;
        }

        private void StartLevelUp()
        {
            if (!_spells.InBattle) { _ctx.Toast("Только в бою", 2); return; }
            if (!Alive(_abilityMgr))
            {
                _abilityMgr = null;
                _ctx.Toast("Менеджер способностей ещё не готов — подождите", 3);
                _ctx.Log("уровень героя: менеджер способностей ещё не пойман или уничтожен");
                return;
            }
            HidePanel();                                              // окно выбора магии не должно быть закрыто кнопками чита
            SyncPool();                                               // убрать максимум из пула перед окном выбора
            ClampLevels();                                            // и не дать уровням уйти выше максимума
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
            if (!Alive(_abilityMgr))
            {
                _abilityMgr = null;
                _portion = -1;
                _levelQueue = 0;
                _ctx.Log("уровень героя: менеджер способностей уничтожен");
                return;
            }
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
            if (mgr != null && !Alive(mgr))
            {
                _ctx.Log("пересчёт: менеджер способностей уничтожен, ищу живой");
                _abilityMgr = null;
                mgr = null;
            }
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
                // Игра могла поднять уровень сама в окне выбора (перед особенностью) — не откатывать его назад.
                if (cur < kv.Value) Invoke(levels, "set_Item", kv.Key, kv.Value);
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

        // Менеджер способностей пересоздаётся при входе в бой: старая обёртка остаётся не null, но объект уже уничтожен.
        private static bool Alive(object o)
        {
            if (o == null) return false;
            try
            {
                var p = o.GetType().GetProperty("Pointer", BindingFlags.Public | BindingFlags.Instance);
                if (p != null && p.GetValue(o) is nint ptr) return !Raw.IsDestroyed(ptr);
            }
            catch { return false; }
            return true;
        }

        // Пул выбора уровня игра строит из List_AllyAbilityPossible: пока заклинание не максимум, там его номера.
        // Чит останавливается за уровень до особенности: такое заклинание остаётся в пуле, и следующее
        // «Уровень героя +1» даёт его выбрать — окно особенности игра откроет сама.
        private void SyncPool()
        {
            if (_traits.Count == 0) return;
            var pool = Static("List_AllyAbilityPossible");
            if (pool == null) return;
            try
            {
                var counts = new Dictionary<int, int>();
                foreach (var x in Items("List_AllyAbilityPossible")) counts[x] = counts.TryGetValue(x, out var n) ? n + 1 : 1;
                int removed = 0, added = 0;

                void Remove(int id)
                {
                    if (!counts.TryGetValue(id, out var n)) return;
                    for (int k = 0; k < n; k++) { try { Invoke(pool, "Remove", id); } catch { break; } }
                    counts.Remove(id);
                    removed += n;
                }
                void Add(int id)
                {
                    if (counts.ContainsKey(id)) return;
                    try { Invoke(pool, "Add", id); counts[id] = 1; added++; } catch { }
                }

                for (int spell = 1; spell <= 21; spell++)
                {
                    bool maxed;
                    try { maxed = _spells.Level(spell) >= GameMax(spell); } catch { continue; }
                    _traits.TryGetValue(spell, out var traits);
                    if (maxed)
                    {
                        Remove(spell);
                        if (traits != null) foreach (var trait in traits) Remove(trait);
                    }
                    else
                    {
                        if (traits != null) foreach (var trait in traits) Remove(trait);
                        Add(spell);
                    }
                }
                if (removed + added > 0) _ctx.Log($"пул улучшений: убрано {removed}, добавлено {added}");
            }
            catch (Exception e) { _ctx.Log("пул улучшений: " + Err(e)); }
        }

        // Уровни могли уйти выше максимума от прошлых выдач. Лишние записи в списке выученного
        // заставляют игру снова предлагать ту же магию и её особенность — обрезаем до максимума.
        private void ClampLevels()
        {
            var learn = Static(LearnList);
            foreach (var (id, _) in Spells)
            {
                int max;
                try { max = GameMax(id); } catch { continue; }
                int guard = 0;
                while (true)
                {
                    int cur;
                    try { cur = _spells.Level(id); } catch { break; }
                    if (cur <= max || learn == null || guard++ > 32) break;
                    try { if (!(bool)Invoke(learn, "Remove", id)) break; } catch { break; }
                }
                try { if (_spells.Level(id) > max) TryDirect(id, max); } catch { }
            }
        }

        private void MaxAll()
        {
            if (!_spells.InBattle) { _ctx.Toast("Только в бою", 2); return; }
            ClampLevels();
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
            if (want.Count == 0) { SyncPool(); _ctx.Toast("Все магии уже готовы к выбору особенностей", 2); return; }
            bool ok = added > 0 && Recalc();
            int direct = 0, failed = 0;
            foreach (var (id, name, max) in want)
            {
                if (_spells.Level(id) >= max) continue;
                if (TryDirect(id, max)) direct++; else failed++;
            }
            _ctx.Log($"чит: все магии — к особенностям: заклинаний {want.Count}, записей в список выученного {added} (пересчёт {(ok ? "прошёл" : "не прошёл")}), прямой записью {direct}, не вышло {failed}");
            _ctx.Toast(failed == 0 ? $"Все магии готовы к выбору особенностей ({want.Count})" : $"Магии готовы, кроме {failed} (см. журнал)", 3);
            SyncPool();
            Refresh();
        }

        private void Press(int id, string name)
        {
            if (!_spells.InBattle) { _ctx.Toast("Только в бою", 2); return; }
            int cur = _spells.Level(id), max = Max(id);
            if (!_minus && cur >= max)
            {
                string next = _traitLevels.TryGetValue(id, out var levels) && levels.Count > 0 && levels.Any(x => x > cur)
                    ? "дальше — выбор особенности («Уровень героя +1»)" : "уже максимум";
                _ctx.Toast($"{name}: {next}", 2);
                return;
            }
            if (_minus && cur <= 0) { _ctx.Toast($"{name}: нет уровней", 2); return; }
            int want = _minus ? cur - 1 : cur + 1;
            string how = TryLearnList(id, want) ? "список выученного" : TryDirect(id, want) ? "прямая запись" : null;
            int now = _spells.Level(id);
            _ctx.Log($"{name} (ID {id}): {cur} → {now} (нужно {want}), способ: {how ?? "не сработал"}");
            _ctx.Toast(how != null ? $"{name}: уровень {now}" : $"{name}: не удалось (см. журнал)", 2);
            SyncPool();
            Refresh();
        }

        private void OpenDetail(Row r, int iconSlot = -1)
        {
            _sheetIcons = r.Tab?.Kind == "spell" || r.Tab?.Kind == "ability" ? ShowIcons() : null;
            var win = UiKit.OpenPage("cheat_detail", r.Tab?.Label ?? "", null, () => Refresh());
            double cardH = UiKit.ScreenH - 460;
            var card = UiKit.Node("detail_card", win.rt);
            UiKit.Place(card.rt, 30, 190, 660, cardH);
            UiKit.AddImage(card.go, 0.02, 0.02, 0.03, 0.98);

            var pic = UiKit.Node("pic", card.rt);
            UiKit.Place(pic.rt, 230, 24, 200, 200);
            nint sprite = IconSprite(r);
            if (sprite != IntPtr.Zero)
            {
                var img = UiKit.AddImage(pic.go, 1, 1, 1, 1);
                img.preserveAspect = true;
                UiKit.SetSprite(img, sprite);
            }
            else if (SlotIcon(iconSlot) is byte[] icon) UiKit.Picture(pic.rt, icon, 0, 0, 200, 200);
            else UiKit.Tint(UiKit.Label(pic.rt, Initial(r.Name), 0, 0, 200, 200, 90, 4), UiKit.Gold);

            UiKit.Tint(UiKit.Label(card.rt, r.Name, 10, 234, 640, 58, 30, 4), UiKit.Gold);
            string SubLabel() => r.Tab?.Kind == "artifact" && r.Grade >= 0 ? Grades[r.Grade] : LevelText(r);
            Text level = UiKit.Tint(UiKit.Label(card.rt, SubLabel(), 10, 292, 640, 34, 18, 4), UiKit.Col(0.55, 0.75, 0.95, 0.95));
            string desc = Describe(r);
            if (!string.IsNullOrEmpty(desc)) UiKit.Label(card.rt, desc, 20, 330, 620, 120, 17, 4);
            double linksH = Math.Max(100, cardH - 480 - (r.Tab?.Kind == "spell" ? 120 : 0));
            Text links = UiKit.Label(card.rt, Links(r), 20, 460, 620, linksH, 16, 0);

            if (r.Tab?.Kind == "spell" && _traits.TryGetValue(r.Id, out var traits))
            {
                double iy = UiKit.ScreenH - 260;
                double rowW = traits.Count * 86 - 10;
                double ix = Math.Max(10, (720 - rowW) / 2);
                int ti = 0;
                foreach (int traitId in traits)
                {
                    var row = RowOf("ability", traitId);
                    if (row == null) continue;
                    row.Tab = _tabs.FirstOrDefault(t => t.Rows.Contains(row));
                    int id = traitId;
                    int slot = ti;
                    var b = UiKit.AddButton(win.rt, "", ix, iy, 76, 76, 0, Gray, () => _ctx.Guard("особенность", () => OpenDetail(row, slot)));
                    nint sp = _sheetIcons != null && _sheetIcons.TryGetValue(id, out var sheet) ? sheet : IntPtr.Zero;
                    if (sp == IntPtr.Zero) sp = UiKit.GameSprite("SpriteList_AbilityChPortrait", ti);
                    if (sp == IntPtr.Zero) sp = UiKit.GameSprite("SpriteList_AbilityChPortrait", ti + 1);
                    if (sp == IntPtr.Zero) sp = UiKit.GameSprite("SpriteList_AbilityChPortrait", row.Id);
                    if (sp == IntPtr.Zero) sp = UiKit.GameSprite("SpriteList_AbilityChPortrait", row.Slot);
                    if (sp == IntPtr.Zero) sp = UiKit.GameSprite("SpriteList_AbilityChPortrait", row.Slot + 1);
                    if (sp == IntPtr.Zero) sp = UiKit.GameSprite("SpriteList_AbilityPortrait", id);
                    if (SlotIcon(ti) is byte[] iconBytes)
                    {
                        var ic = UiKit.Node("ic", b.transform);
                        UiKit.Place(ic.rt, 2, 2, 72, 72);
                        UiKit.Picture(ic.rt, iconBytes, 0, 0, 72, 72);
                    }
                    else if (sp != IntPtr.Zero)
                    {
                        var ic = UiKit.Node("ic", b.transform);
                        UiKit.Place(ic.rt, 2, 2, 72, 72);
                        var im = UiKit.AddImage(ic.go, 1, 1, 1, 1);
                        im.preserveAspect = true;
                        im.raycastTarget = false;
                        UiKit.SetSprite(im, sp);
                    }
                    else UiKit.Tint(UiKit.Label(b.transform, Initial(row.Name), 0, 0, 76, 76, 26, 4), UiKit.Gold);
                    ix += 86;
                    ti++;
                }
            }

            double y = UiKit.ScreenH - 140;
            void Close() { win.go.SetActive(false); }
            void Update() { level.text = SubLabel(); links.text = Links(r); }
            switch (r.Tab?.Kind)
            {
                case "spell":
                case "ability":
                    UiKit.AddButton(win.rt, "+1 уровень", 28, y, 320, 60, 18, Green, () => _ctx.Guard("+1", () => { PressOn(r, false); Update(); }));
                    UiKit.AddButton(win.rt, "−1 уровень", 372, y, 320, 60, 18, Gray, () => _ctx.Guard("−1", () => { PressOn(r, true); Update(); }));
                    break;
                case "artifact":
                    UiKit.AddButton(win.rt, "Убрать артефакт", 28, y, 664, 60, 18, Red, () => { Close(); _ctx.Guard("убрать", () => PressOn(r, true)); });
                    break;
                case "syn":
                    UiKit.AddButton(win.rt, "Убрать синергию", 28, y, 664, 60, 18, Red, () => { Close(); _ctx.Guard("убрать", () => PressOn(r, true)); });
                    break;
                case "combo":
                    UiKit.AddButton(win.rt, "Откатить способности", 28, y, 664, 60, 18, Red, () => { Close(); _ctx.Guard("откат", () => PressOn(r, true)); });
                    break;
            }
        }

        private void PressOn(Row r, bool down)
        {
            var tab = r.Tab;
            if (tab == null) return;
            if (tab.Kind == "spell")
            {
                bool old = _minus; _minus = down;
                try { Press(r.Id, r.Name); } finally { _minus = old; }
                return;
            }
            int oldTab = _tab; bool oldMinus = _minus;
            _tab = Math.Max(0, _tabs.IndexOf(tab)); _minus = down;
            try { PressRow(r); } finally { _tab = oldTab; _minus = oldMinus; }
        }

        private string Describe(Row r)
        {
            string table = r.Tab?.Kind switch { "combo" => "MagicCom", "syn" => "Synergy", _ => "Ability" };
            try
            {
                var tb = _tables.Table(table);
                int i = tb.IndexOf(r.Id, tb.IdColumn);
                if (i < 0) return "";
                foreach (var col in new[] { "효과 설명줄01", "설명줄01" })
                {
                    string v = Cell(tb, i, col);
                    if (!Blank(v) && v != "`") return v;
                }
            }
            catch { }
            return "";
        }

        private string Links(Row r)
        {
            var parts = new List<string>();
            string kind = r.Tab?.Kind ?? Kind;
            if (kind == "spell" && _traits.TryGetValue(r.Id, out var traits))
            {
                var combos = CombosUsing(traits);
                if (combos.Count > 0) parts.Add("Комбинации: " + string.Join(", ", combos));
            }
            else if (kind == "ability")
            {
                var parent = _traits.FirstOrDefault(kv => kv.Value.Contains(r.Id));
                if (parent.Value != null) parts.Add("Магия: " + NameOf("spell", parent.Key));
                var combos = CombosUsing(new[] { r.Id });
                if (combos.Count > 0) parts.Add("Комбинации: " + string.Join(", ", combos));
            }
            else if (kind == "artifact")
            {
                var syns = _tabs.Where(t => t.Kind == "syn").SelectMany(t => t.Rows)
                    .Where(x => x.Req != null && x.Req.Contains(r.Id)).Select(x => x.Name).Distinct().ToList();
                if (syns.Count > 0) parts.Add("Синергии: " + string.Join(", ", syns));
            }
            else if (kind == "syn" && r.Req != null)
                parts.Add("Артефакты: " + string.Join(", ", r.Req.Select(x => NameOf("artifact", x))));
            else if (kind == "combo" && r.Req != null)
                parts.Add("Составляющие: " + string.Join(", ", r.Req.Select(x => NameOf("ability", x))));
            return parts.Count == 0 ? "Связей в таблицах нет" : string.Join("\n", parts);
        }

        private List<string> CombosUsing(IEnumerable<int> ids)
        {
            var set = new HashSet<int>(ids);
            return _tabs.Where(t => t.Kind == "combo").SelectMany(t => t.Rows)
                .Where(x => x.Req != null && x.Req.Any(set.Contains)).Select(x => x.Name).Distinct().ToList();
        }

        private Row RowOf(string kind, int id) =>
            _tabs.Where(t => t.Kind == kind).SelectMany(t => t.Rows).FirstOrDefault(x => x.Id == id);

        private string NameOf(string kind, int id) => RowOf(kind, id)?.Name ?? id.ToString();

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
            if (Kind == "ability") SyncPool();
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
