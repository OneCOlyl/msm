using System.Collections.Generic;
using System.Linq;

namespace MagicSurvivalCoop
{
    /// <summary>
    /// Журнал сверки спавна (1.6.112–1.6.116): раз в 30 с часов боя хост и гость пишут нарастающие счётчики по типам.
    /// Хост — создано игрой и ушло гостю (номер выдан), гость — пришло номеров, создано копий, пропало у хоста до создания копии.
    /// Часы общие, строки обоих телефонов с одними часами сравниваются напрямую.
    /// </summary>
    internal sealed partial class Session
    {
        private readonly Dictionary<int, int> _auSpawn = new Dictionary<int, int>(), _auSent = new Dictionary<int, int>(),
            _auRecv = new Dictionary<int, int>(), _auMade = new Dictionary<int, int>(), _auNeverMade = new Dictionary<int, int>();
        private readonly int[] _auSpawnOrigin = new int[4], _auRecvOrigin = new int[4];
        // по номерам, а не по копиям: выключение мира у гостя забывает копии, и хост присылает тех же врагов снова
        private readonly HashSet<int> _auRecvIds = new HashSet<int>(), _auMadeIds = new HashSet<int>();
        private readonly Dictionary<int, int> _auSpecialIds = new Dictionary<int, int>();   // номер → вид особого
        private int _auMinId = int.MaxValue, _auMaxId, _auLastSlot = -1;

        private void ResetSpawnAudit()
        {
            _auSpawn.Clear(); _auSent.Clear(); _auRecv.Clear(); _auMade.Clear(); _auNeverMade.Clear(); _auRecvIds.Clear(); _auMadeIds.Clear(); _auSpecialIds.Clear();
            System.Array.Clear(_auSpawnOrigin, 0, 4); System.Array.Clear(_auRecvOrigin, 0, 4);
            _auMinId = int.MaxValue; _auMaxId = 0; _auLastSlot = -1;
        }

        private static void AuInc(Dictionary<int, int> d, int type) => d[type] = d.TryGetValue(type, out var n) ? n + 1 : 1;
        private static string AuTypes(Dictionary<int, int> d) => d.Count == 0 ? "—" : string.Join(" ", d.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}×{kv.Value}"));
        private static string AuOrigins(int[] o) => string.Join(" ", Enumerable.Range(0, 4).Select(i => $"{OriginNames[i]} {o[i]}"));

        private void AuditHostSpawn(object[] a, int origin)
        {
            int type = -1;
            try { type = System.Convert.ToInt32(a[0]); } catch { }
            AuInc(_auSpawn, type);
            _auSpawnOrigin[origin]++;
        }

        private void AuditRecv(int id, int type)
        {
            if (!_auRecvIds.Add(id)) return;
            AuInc(_auRecv, type);
            _auRecvOrigin[OriginNormal]++;
            if (id < _auMinId) _auMinId = id;
            if (id > _auMaxId) _auMaxId = id;
        }

        private void SpawnAuditStep()
        {
            if (!WorldOn || (!_hosting && !_worldActive)) return;
            int c = MyClock();
            if (c < 0 || c / 30 == _auLastSlot) return;
            _auLastSlot = c / 30;
            _ctx.Log("сверка спавна: " + SpawnAuditLine(c));
        }

        private int[] MadeOrigins()
        {
            var o = new int[4];
            foreach (var kv in _auSpecialIds) if (_auMadeIds.Contains(kv.Key)) o[kv.Value]++;
            o[OriginNormal] = _auMadeIds.Count - o.Sum();
            return o;
        }

        private void AuditMade(int id, int type) { if (_auMadeIds.Add(id)) AuInc(_auMade, type); }
        private void AuditGone(int id, int type) { if (!_auMadeIds.Contains(id)) AuInc(_auNeverMade, type); }

        private string SpawnAuditLine(int clock)
        {
            if (_hosting)
                return $"хост, часы {clock} с, живых {AliveNow().Count}; создано {_auSpawn.Values.Sum()} ({AuOrigins(_auSpawnOrigin)}): {AuTypes(_auSpawn)}; " +
                       $"ушло гостю {_auSent.Values.Sum()}: {AuTypes(_auSent)}";
            int recv = _auRecv.Values.Sum();
            int gaps = recv == 0 ? 0 : _auMaxId - _auMinId + 1 - recv;
            int live = _puppets.Values.Count(p => p.Obj != null && !p.LocalDead);
            return $"гость, часы {clock} с, у хоста {_puppets.Count}, копий живых {live}; пришло {recv} (номера {(recv == 0 ? "—" : $"{_auMinId}–{_auMaxId}")}, пропущено {gaps}; {AuOrigins(_auRecvOrigin)}): {AuTypes(_auRecv)}; " +
                   $"копий создано {_auMade.Values.Sum()} ({AuOrigins(MadeOrigins())}): {AuTypes(_auMade)}; пропали у хоста до копии {_auNeverMade.Values.Sum()}: {AuTypes(_auNeverMade)}";
        }
    }
}
