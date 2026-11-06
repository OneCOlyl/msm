using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

namespace MagicSurvivalCoop.Net
{
    // Главный поток не берёт блокировок: очередь под lock подозревалась в падении среды Mono при сборке мусора.
    public sealed class Inbox
    {
        // Около минуты событий от трёх друзей.
        public const int MaxMessages = 4096;
        public const int MaxBytes = 8 * 1024 * 1024;
        // 1–2 с при 15–30 отправках в секунду, больше любого рывка сети.
        public const int MaxStatesPerPlayer = 32;
        // Вместе с пустыми записями выброшенных состояний, около 3 мин стоящей игры.
        public const int MaxEntries = 16384;

        private const int Waiting = 0, Taken = 1, Dropped = 2;

        private sealed class Item
        {
            public JsonElement Msg;
            public IDisposable Owner;           // Документ и буфер Msg: освобождаются, когда сообщение больше не нужно
            public int Bytes;
            public bool IsState;
            public int Status;                  // Waiting → Taken (главный поток) или Dropped (сетевой), только через CAS
        }

        private readonly ConcurrentQueue<Item> _q = new ConcurrentQueue<Item>();
        private int _entries, _live, _other, _bytes;

        // Только для пишущей стороны. Взятые и выброшенные состояния снимаются с головы
        private readonly object _writeLock = new object();
        private readonly Dictionary<string, Queue<Item>> _states = new Dictionary<string, Queue<Item>>();

        public int Count => Volatile.Read(ref _live);

        public int Pending => Volatile.Read(ref _other);

        // Без проверки предела, для служебных сообщений клиента вроде DISCONNECTED.
        public void Enqueue(JsonElement m) { lock (_writeLock) Add(m, 0, force: true, null); }

        // owner освобождается вместе с сообщением, false — сообщение не принято и owner уже освобождён.
        public bool TryEnqueue(JsonElement m, int bytes, IDisposable owner = null)
        {
            bool ok;
            lock (_writeLock) ok = Add(m, bytes, force: false, owner);
            if (!ok) owner?.Dispose();
            return ok;
        }

        private IDisposable _held;

        // Без блокировок и без копии: m годен до следующего TryTake или Clear. Только для главного потока игры.
        public bool TryTake(out JsonElement m)
        {
            _held?.Dispose();
            _held = null;
            while (_q.TryDequeue(out var it))
            {
                Interlocked.Decrement(ref _entries);
                if (Interlocked.CompareExchange(ref it.Status, Taken, Waiting) != Waiting) continue;   // Выброшено пишущей стороной
                Release(it);
                m = it.Msg;
                _held = it.Owner;
                it.Owner = null;
                return true;
            }
            m = default;
            return false;
        }

        // Копия, годная сколько угодно: для тестов и редких мест, где сообщение хранят.
        public bool TryDequeue(out JsonElement m)
        {
            if (!TryTake(out m)) return false;
            m = m.Clone();
            _held?.Dispose();
            _held = null;
            return true;
        }

        public void Clear() { while (TryTake(out _)) { } _held?.Dispose(); _held = null; }

        private bool Add(JsonElement m, int bytes, bool force, IDisposable owner)
        {
            var type = Protocol.TypeOf(m);
            bool isState = type == Protocol.RemotePlayerState;
            if (!force && Volatile.Read(ref _entries) >= MaxEntries) return false;
            if (isState)
            {
                var id = Protocol.Str(m, "playerId") ?? "";
                if (!_states.TryGetValue(id, out var mine)) _states[id] = mine = new Queue<Item>();
                Trim(mine);
                while (mine.Count >= MaxStatesPerPlayer) Drop(mine.Dequeue());
                var it = new Item { Msg = m, Bytes = bytes, IsState = true, Owner = owner };
                mine.Enqueue(it);
                Push(it);
                return true;
            }
            if (!force && (Volatile.Read(ref _other) >= MaxMessages || Volatile.Read(ref _bytes) + bytes > MaxBytes)) return false;
            // Неразобранные состояния ушедшего игрока, старой комнаты или до обрыва уже не нужны
            if (type == Protocol.PlayerLeft) DropStates(Protocol.Str(m, "playerId"));
            else if (type == Protocol.RoomJoined || type == Protocol.RoomCreated || type == "DISCONNECTED")
                foreach (var k in new List<string>(_states.Keys)) DropStates(k);
            Interlocked.Increment(ref _other);
            Push(new Item { Msg = m, Bytes = bytes, Owner = owner });
            return true;
        }

        private void Push(Item it)
        {
            Interlocked.Increment(ref _entries);
            Interlocked.Increment(ref _live);
            Interlocked.Add(ref _bytes, it.Bytes);
            _q.Enqueue(it);
        }

        private void Release(Item it)
        {
            Interlocked.Decrement(ref _live);
            Interlocked.Add(ref _bytes, -it.Bytes);
            if (!it.IsState) Interlocked.Decrement(ref _other);
        }

        private void Drop(Item it)
        {
            if (Interlocked.CompareExchange(ref it.Status, Dropped, Waiting) != Waiting) return;   // Главный поток успел взять
            Release(it);
            it.Msg = default;                   // Пустая запись остаётся до прохода главного потока
            it.Owner?.Dispose();
            it.Owner = null;
        }

        // Главный поток берёт по порядку, так что взятые состояния игрока всегда в голове его очереди.
        private static void Trim(Queue<Item> mine)
        {
            while (mine.Count > 0 && Volatile.Read(ref mine.Peek().Status) != Waiting) mine.Dequeue();
        }

        private void DropStates(string id)
        {
            if (id == null || !_states.TryGetValue(id, out var mine)) return;
            while (mine.Count > 0) Drop(mine.Dequeue());
            _states.Remove(id);
        }
    }
}
