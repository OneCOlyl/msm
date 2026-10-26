using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;

namespace MagicSurvivalCoop.Net
{
    /// <summary>
    /// Входящие сообщения клиента: пишет сетевой поток, читает главный поток игры. Всё доставляется по порядку.
    /// Главный поток не берёт блокировок: в основе <see cref="ConcurrentQueue{T}"/>, как было до пределов (1.6.56 с очередью
    /// под lock у гостя упала в среде Mono — поток не остановился для сборки мусора; связь не доказана, но риск убран).
    /// REMOTE_PLAYER_STATE не схлопываются до последнего: состояние — не полный снимок (враги, часы, спутники идут
    /// не в каждом), и выброшенное промежуточное теряло бы их (1.6.55: копии врагов у гостя замирали). Но от каждого
    /// игрока ждёт не больше <see cref="MaxStatesPerPlayer"/> состояний: больше копится, только когда игра стоит
    /// (пауза, свёрнута), — тогда самые старые помечаются выброшенными, и после паузы игра не перебирает старые координаты.
    /// Остальное (события, вход/выход игроков, ошибки) не выбрасывается, но ограничено: <see cref="MaxMessages"/>
    /// сообщений и <see cref="MaxBytes"/> байт, а записей в очереди вместе с выброшенными — <see cref="MaxEntries"/>.
    /// Переполнение — игра надолго перестала разбирать очередь; <see cref="TryEnqueue"/> возвращает false, и клиент
    /// переподключается (как сервер отключает медленного клиента).
    /// </summary>
    public sealed class Inbox
    {
        /// <summary>Предел сообщений, кроме состояний (~минута событий от трёх друзей).</summary>
        public const int MaxMessages = 4096;
        /// <summary>Предел их суммарного размера вместе с ждущими состояниями (по длине исходных строк).</summary>
        public const int MaxBytes = 8 * 1024 * 1024;
        /// <summary>Сколько состояний одного игрока может ждать: 1–2 с при 15–30 отправках в секунду — больше любого рывка сети.</summary>
        public const int MaxStatesPerPlayer = 32;
        /// <summary>Записей в очереди вместе с выброшенными состояниями (от них остаётся пустая запись) — ~3 мин стоящей игры.</summary>
        public const int MaxEntries = 16384;

        private const int Waiting = 0, Taken = 1, Dropped = 2;

        private sealed class Item
        {
            public JsonElement Msg;
            public int Bytes;
            public bool IsState;
            public int Status;                  // Waiting → Taken (главный поток) или Dropped (сетевой); меняется только через CAS
        }

        private readonly ConcurrentQueue<Item> _q = new ConcurrentQueue<Item>();
        private int _entries, _live, _other, _bytes;

        // только для пишущей стороны: состояния каждого игрока по порядку (взятые и выброшенные снимаются с головы)
        private readonly object _writeLock = new object();
        private readonly Dictionary<string, Queue<Item>> _states = new Dictionary<string, Queue<Item>>();

        /// <summary>Сколько сообщений ждёт (вместе с состояниями).</summary>
        public int Count => Volatile.Read(ref _live);

        /// <summary>Сколько ждёт сообщений, кроме состояний.</summary>
        public int Pending => Volatile.Read(ref _other);

        /// <summary>Добавить без проверки предела — для служебных сообщений клиента (DISCONNECTED).</summary>
        public void Enqueue(JsonElement m) { lock (_writeLock) Add(m, 0, force: true); }

        /// <summary>Добавить; false — очередь переполнена, сообщение не добавлено.</summary>
        public bool TryEnqueue(JsonElement m, int bytes) { lock (_writeLock) return Add(m, bytes, force: false); }

        /// <summary>Следующее сообщение. Без блокировок: зовётся из главного потока игры каждый кадр.</summary>
        public bool TryDequeue(out JsonElement m)
        {
            while (_q.TryDequeue(out var it))
            {
                Interlocked.Decrement(ref _entries);
                if (Interlocked.CompareExchange(ref it.Status, Taken, Waiting) != Waiting) continue;   // выброшено пишущей стороной
                Release(it);
                m = it.Msg;
                return true;
            }
            m = default;
            return false;
        }

        public void Clear() { while (TryDequeue(out _)) { } }

        private bool Add(JsonElement m, int bytes, bool force)
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
                var it = new Item { Msg = m, Bytes = bytes, IsState = true };
                mine.Enqueue(it);
                Push(it);
                return true;
            }
            if (!force && (Volatile.Read(ref _other) >= MaxMessages || Volatile.Read(ref _bytes) + bytes > MaxBytes)) return false;
            // ушедший игрок или новая комната / обрыв: его (их) неразобранные состояния уже не нужны
            if (type == Protocol.PlayerLeft) DropStates(Protocol.Str(m, "playerId"));
            else if (type == Protocol.RoomJoined || type == Protocol.RoomCreated || type == "DISCONNECTED")
                foreach (var k in new List<string>(_states.Keys)) DropStates(k);
            Interlocked.Increment(ref _other);
            Push(new Item { Msg = m, Bytes = bytes });
            return true;
        }

        private void Push(Item it)
        {
            Interlocked.Increment(ref _entries);
            Interlocked.Increment(ref _live);
            Interlocked.Add(ref _bytes, it.Bytes);
            _q.Enqueue(it);
        }

        /// <summary>Сообщение ушло из ожидающих (взято или выброшено): снять со счётчиков.</summary>
        private void Release(Item it)
        {
            Interlocked.Decrement(ref _live);
            Interlocked.Add(ref _bytes, -it.Bytes);
            if (!it.IsState) Interlocked.Decrement(ref _other);
        }

        private void Drop(Item it)
        {
            if (Interlocked.CompareExchange(ref it.Status, Dropped, Waiting) != Waiting) return;   // главный поток успел взять
            Release(it);
            it.Msg = default;                   // от выброшенного остаётся пустая запись до прохода главного потока
        }

        /// <summary>Главный поток берёт по порядку, так что взятые состояния игрока всегда в голове его очереди.</summary>
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
