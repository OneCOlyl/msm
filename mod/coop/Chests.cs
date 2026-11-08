using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using MagicSurvivalCoop.Net;
using UnityEngine;

namespace MagicSurvivalCoop
{
    // Все сундуки общие. Свои сундуки гостя — копии дропа хоста, их гость убирает. Сундук за свой выбор (артефакт, умение)
    // игра гостя кладёт за его паузу: гость отдаёт его хосту, хост кладёт у себя, и сундук приходит всем как общий.
    // Хук на V_Artifact_Manager.Button_Artifact_Select роняет игру при нажатии, поэтому такие сундуки узнаются по паузе.
    internal sealed partial class Session
    {
        // 7 — торговец: смерть моба кладёт предмет 5, а сундук это или торговец, игра решает сама, у гостя жребий свой
        private static readonly int[] ChestKinds = { 5, 6, 7 };
        private const int MerchantKind = 7;
        // Скан сундуков — FindObjectsOfType по всей сцене, в замере кадра это было главное в части «сундуки»
        private const double ChestScanSec = 1, ChestClaimRadius = 0.5, ChestClaimRetrySec = 2;
        private static readonly Vector3 ChestLockedCenter = new Vector3(0, 1000, 0);
        private bool ChestsOn => _ctx.Setting("chests") > 0;

        private double _chestScanAt;
        private HashSet<object> _chestsBeforePause;
        // Хост: id лежащих сундуков
        private int _chestSeq;
        private readonly Dictionary<object, int> _chestIds = new Dictionary<object, int>();
        private readonly Dictionary<int, object> _chestBoxes = new Dictionary<int, object>();
        private readonly List<(int id, int kind, double x, double y)> _chestList = new List<(int, int, double, double)>();
        // Гость: сундуки хоста и наши копии
        private Partner _chestHost;
        private double _hostChestsAt = -100;
        private readonly Dictionary<int, (int kind, double x, double y)> _hostChests = new Dictionary<int, (int, double, double)>();
        private readonly Dictionary<int, ChestCopy> _chestCopies = new Dictionary<int, ChestCopy>();
        private readonly HashSet<int> _chestDone = new HashSet<int>();   // Взяты у нас или копия не вышла: заново не кладём
        private int _ownChestsRemoved;

        private sealed class ChestCopy
        {
            public object Box, Key;
            public int Kind;
            public double X, Y;
            public object Center;
            public bool Granted;
            public double ClaimAt = -100;
        }

        private bool ChestsShared => _hosting ? ChestsOn && _partners.Count > 0 : _chestHost != null;

        // Гость, перед каждым сканом паузы и на её конце: скан убрал бы новый сундук как свой.
        private void ChoiceChestStep(double now)
        {
            bool paused = ChestsShared && OwnPause();
            if (paused && _chestsBeforePause == null) { _chestsBeforePause = LyingChests().Select(KeyOf).ToHashSet(); return; }
            if (_chestsBeforePause == null || paused && now < _chestScanAt) return;
            var copies = new HashSet<object>(_chestCopies.Values.Select(c => c.Key));
            int n = 0;
            foreach (var box in LyingChests())
            {
                var key = KeyOf(box);
                if (_chestsBeforePause.Contains(key) || copies.Contains(key)) continue;
                var pos = ChestPos(box);
                int kind = ItemType(box);
                double x = pos.x - _startX.Value, y = pos.y - (_startY ?? 0);
                _client.SendEvent(Protocol.Build(w => { w.WriteString("kind", "chestNew"); w.WriteNumber("k", kind); w.WriteNumber("x", Math.Round(x, 2)); w.WriteNumber("y", Math.Round(y, 2)); }));
                DestroyChest(box, "отдан хосту");
                _chestsBeforePause.Add(key);
                n++;
            }
            if (n > 0) _ctx.Log($"сундук: окно выбора положило мне {n} — отдал хосту, он положит их всем");
            if (!paused) _chestsBeforePause = null;
        }

        // Хост: сундук за выбор гостя кладётся у хоста и дальше идёт как общий.
        private void OnRemoteNewChest(string who, JsonElement e)
        {
            if (!_hosting || !_inRun || !ChestsOn || _battleMgr == null || !_startX.HasValue) return;
            int kind = (int)Protocol.Num(e, "k", -1);
            if (Array.IndexOf(ChestKinds, kind) < 0) return;
            double x = _startX.Value + Protocol.Num(e, "x", 0), y = (_startY ?? 0) + Protocol.Num(e, "y", 0);
            var box = CreateChestAt(kind, x, y, out string how);
            _ctx.Log(box != null ? $"сундук от «{who}» (вид {kind}): положил у себя в ({x - _startX.Value:0.0}; {y - (_startY ?? 0):0.0}) от старта"
                                 : $"сундук от «{who}» (вид {kind}): не лёг после {how}");
        }

        // Вызовы ничего не возвращают: сундук это тот, что лёг при вызове. Торговца CreateItem_Position не кладёт,
        // его кладёт CreateItem_Character у героя, а мод переносит на место.
        private object CreateChestAt(int kind, double x, double y, out string how)
        {
            var before = LyingChests().Select(KeyOf).ToHashSet();
            how = kind == MerchantKind ? "CreateItem_Character" : "CreateItem_Position";
            if (kind == MerchantKind) CallMethod(_battleMgr, how, kind, 1);
            else CallMethod(_battleMgr, how, kind, 1, (float)x, (float)y);
            var box = LyingChests().FirstOrDefault(b => ItemType(b) == kind && !before.Contains(KeyOf(b)));
            if (box != null && kind == MerchantKind)
            {
                var tr = ((GameObject)GetMember(box, "gameObject")).transform;
                tr.localPosition = new Vector3((float)x, (float)y, tr.localPosition.z);
            }
            return box;
        }

        private void ResetChests()
        {
            _chestScanAt = 0;
            _chestSig = 0; _chestLeft = 0; _chestSentAt = -100;
            _chestsBeforePause = null;
            _chestIds.Clear(); _chestBoxes.Clear(); _chestList.Clear();
            _hostChests.Clear(); _chestCopies.Clear(); _chestDone.Clear();
            _chestHost = null; _hostChestsAt = -100; _ownChestsRemoved = 0;
        }

        private static bool IsChest(object box) => Array.IndexOf(ChestKinds, ItemType(box)) >= 0;

        private static bool ChestLying(object box)
        {
            try { return ActiveObj(box) && GetMember(GetMember(box, "Obj_ItemPos"), "activeSelf") is bool on && on; }
            catch { return false; }
        }

        private List<object> LyingChests() => FindAllOfType("C_Battle_Item_TreasureBox").Where(b => IsChest(b) && ChestLying(b)).ToList();

        private static Vector3 ChestPos(object box) => ((GameObject)GetMember(box, "gameObject")).transform.localPosition;

        private void DestroyChest(object box, string what)
        {
            try { CallMethod(box, "DestroyItem"); } catch (Exception e) { _ctx.Log($"сундук: {what}: DestroyItem: {Inner(e).Message}"); }
        }

        // Список сундуков: при изменении три отправки подряд и не реже раза в ChestSendSec. Выключенные общие сундуки — "ch": null.
        private const double ChestSendSec = 0.5;
        private int _chestSig, _chestLeft;
        private double _chestSentAt = -100;

        private void WriteChests(Utf8JsonWriter w)
        {
            double now = Clock.Elapsed.TotalSeconds;
            if (!ChestsOn || _partners.Count == 0)
            {
                if (now - _chestSentAt < ChestSendSec) return;
                _chestSentAt = now; _chestSig = 0;
                w.WriteNull("ch");
                return;
            }
            int sig = _chestList.Count + 1;
            foreach (var c in _chestList) sig = sig * 31 + c.id * 7 + c.kind;
            if (sig != _chestSig) { _chestSig = sig; _chestLeft = 3; }
            if (_chestLeft == 0 && now - _chestSentAt < ChestSendSec) return;
            if (_chestLeft > 0) _chestLeft--;
            _chestSentAt = now;
            w.WriteStartArray("ch");
            foreach (var c in _chestList)
            {
                w.WriteStartArray();
                w.WriteNumberValue(c.id); w.WriteNumberValue(c.kind);
                w.WriteNumberValue(Math.Round(c.x, 2)); w.WriteNumberValue(Math.Round(c.y, 2));
                w.WriteEndArray();
            }
            w.WriteEndArray();
        }

        // По списку хоста гость и узнаёт, чьи сундуки повторять.
        private void ReadChests(Partner p, JsonElement s)
        {
            if (_hosting) return;
            if (!s.TryGetProperty("ch", out var arr)) return;
            if (arr.ValueKind != JsonValueKind.Array)
            {
                if (p == _chestHost) _chestHost = null;
                return;
            }
            _chestHost = p;
            _hostChestsAt = Clock.Elapsed.TotalSeconds;
            _hostChests.Clear();
            foreach (var c in arr.EnumerateArray())
            {
                if (c.ValueKind != JsonValueKind.Array || c.GetArrayLength() < 4) continue;
                if (!c[0].TryGetInt32(out int id) || !c[1].TryGetInt32(out int kind) || !c[2].TryGetDouble(out double x) || !c[3].TryGetDouble(out double y)) continue;
                if (Array.IndexOf(ChestKinds, kind) >= 0) _hostChests[id] = (kind, x, y);
            }
        }

        // Каждый кадр и на паузе: заявка гостя должна уйти сразу, как он дошёл до сундука.
        private void ChestStep(double now)
        {
            if (!_startX.HasValue) return;
            if (!_hosting) ChoiceChestStep(now);
            if (!_hosting && _chestCopies.Count > 0) ClaimStep(now);
            if (now < _chestScanAt) return;
            _chestScanAt = now + ChestScanSec;
            if (_hosting) HostChestStep();
            else GuestChestStep(now);
        }

        private void HostChestStep()
        {
            _chestList.Clear();
            if (!ChestsOn || _partners.Count == 0) { _chestIds.Clear(); _chestBoxes.Clear(); return; }
            var seen = new HashSet<int>();
            foreach (var box in LyingChests())
            {
                var key = KeyOf(box);
                if (!_chestIds.TryGetValue(key, out int id)) { id = ++_chestSeq; _chestIds[key] = id; }
                _chestBoxes[id] = box;
                seen.Add(id);
                var pos = ChestPos(box);
                _chestList.Add((id, ItemType(box), pos.x - _startX.Value, pos.y - (_startY ?? 0)));
            }
            foreach (var gone in _chestIds.Where(kv => !seen.Contains(kv.Value)).ToList())
            {
                _chestIds.Remove(gone.Key);
                _chestBoxes.Remove(gone.Value);
            }
        }

        // Взят раньше или уже отдан: ответа нет, копия у гостя уйдёт со списком.
        private void OnRemoteChest(string from, string who, JsonElement e)
        {
            if (!_hosting || !_inRun || from == null) return;
            int id = (int)Protocol.Num(e, "id", -1);
            if (!_chestBoxes.TryGetValue(id, out var box)) return;
            _chestBoxes.Remove(id);
            _chestIds.Remove(KeyOf(box));
            _chestList.RemoveAll(c => c.id == id);
            if (!ChestLying(box)) return;
            DestroyChest(box, "отдан другу");
            _client.SendEvent(Protocol.Build(w => { w.WriteString("kind", "chestOk"); w.WriteString("to", from); w.WriteNumber("id", id); }));
            _ctx.Toast($"{who} забрал сундук", 2);
            _ctx.Log($"сундук #{id}: отдан «{who}», у меня убран");
        }

        private void OnChestGranted(JsonElement e)
        {
            int id = (int)Protocol.Num(e, "id", -1);
            if (!_chestCopies.TryGetValue(id, out var c) || c.Granted) return;
            c.Granted = true;
            UnlockChest(c);
            _ctx.Log($"сундук #{id}: хост отдал его мне");
        }

        private void ClaimStep(double now)
        {
            var pos = _enemies.HeroPos;
            if (!pos.HasValue) return;
            foreach (var kv in _chestCopies)
            {
                var c = kv.Value;
                if (c.Granted || now - c.ClaimAt < ChestClaimRetrySec) continue;
                double dx = pos.Value.x - c.X, dy = pos.Value.y - c.Y;
                if (dx * dx + dy * dy > ChestClaimRadius * ChestClaimRadius) continue;
                c.ClaimAt = now;
                int id = kv.Key;
                _client.SendEvent(Protocol.Build(w => { w.WriteString("kind", "chest"); w.WriteNumber("id", id); }));
            }
        }

        private void LockChest(ChestCopy c)
        {
            var col = GetMember(c.Box, "BoxCollider_ObjItem");
            c.Center = GetMember(col, "center");
            SetMember(col, "center", ChestLockedCenter);
        }

        private void UnlockChest(ChestCopy c)
        {
            try { SetMember(GetMember(c.Box, "BoxCollider_ObjItem"), "center", c.Center ?? new Vector3(0, 0, 0)); }
            catch (Exception e) { _ctx.Log("сундук: вернуть коллайдер: " + Inner(e).Message); }
        }

        private void GuestChestStep(double now)
        {
            var host = _chestHost;
            if (host == null || !host.Battle || _clockMs - host.StateAt > 3000 || now - _hostChestsAt > 1.5 || _battleMgr == null) return;

            foreach (var kv in _chestCopies.ToList())
            {
                int id = kv.Key; var c = kv.Value;
                bool lying = ChestLying(c.Box);
                if (c.Granted)
                {
                    // Хост свой уже убрал, копия наша, пока не подберём
                    if (lying) continue;
                    _chestCopies.Remove(id);
                    _chestDone.Add(id);
                    _ctx.Log($"сундук #{id}: подобрал я");
                    continue;
                }
                if (!_hostChests.ContainsKey(id))
                {
                    if (lying) DestroyChest(c.Box, "забрал хост или другой друг");
                    _chestCopies.Remove(id);
                    continue;
                }
                if (lying) continue;
                _chestCopies.Remove(id);                    // Объект пула забрали под другой предмет, копию положим заново
            }
            _chestDone.RemoveWhere(id => !_hostChests.ContainsKey(id));

            var copies = new HashSet<object>(_chestCopies.Values.Select(c => c.Key));
            foreach (var box in LyingChests())
            {
                var key = KeyOf(box);
                if (copies.Contains(key)) continue;
                DestroyChest(box, "свой у гостя");
                if (++_ownChestsRemoved <= 3) _ctx.Log("сундук: свой у гостя убран — сундуки кладёт хост");
            }

            foreach (var kv in _hostChests)
                if (!_chestCopies.ContainsKey(kv.Key) && !_chestDone.Contains(kv.Key))
                    PlaceChestCopy(kv.Key, kv.Value.kind, _startX.Value + kv.Value.x, (_startY ?? 0) + kv.Value.y);
        }

        private void PlaceChestCopy(int id, int kind, double x, double y)
        {
            object box;
            string how;
            try { _ctx.Mark("сундук: копия у гостя"); box = CreateChestAt(kind, x, y, out how); }
            catch (Exception e) { _chestDone.Add(id); _ctx.Log($"сундук #{id}: {Inner(e).Message}"); return; }
            if (box == null)
            {
                // Без копии в руках повторный вызов клал бы новый сундук каждые полсекунды
                _chestDone.Add(id);
                _ctx.Log($"сундук #{id}: копия не нашлась после {how}");
                return;
            }
            var c = new ChestCopy { Box = box, Key = KeyOf(box), Kind = kind, X = x, Y = y };
            try { LockChest(c); }
            catch (Exception e)
            {
                // Незапертую копию игра отдала бы гостю мимо хоста
                DestroyChest(box, "не запирается");
                _chestDone.Add(id);
                _ctx.Log($"сундук #{id}: коллайдер копии не сдвинуть ({Inner(e).Message}), копия убрана");
                return;
            }
            _chestCopies[id] = c;
            _ctx.Log($"сундук #{id} хоста (вид {kind}): копия у меня в ({x - _startX.Value:0.0}; {y - (_startY ?? 0):0.0}) от старта");
        }
    }
}
