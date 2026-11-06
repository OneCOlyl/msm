using System;
using System.Diagnostics;
using System.Globalization;

namespace MagicSurvivalCoop.Net
{
    // Пишут поток записи и поток чтения клиента, читает главный поток, поэтому всё под одним замком.
    public sealed class NetStats
    {
        private readonly object _lock = new object();
        private long _since = Stopwatch.GetTimestamp();
        private long _txBytes, _rxBytes;
        private int _txMax, _rxMax;
        private int _pings, _pingMax;
        private double _pingSum, _pingSq;

        public void Sent(int bytes) { lock (_lock) { _txBytes += bytes; if (bytes > _txMax) _txMax = bytes; } }
        public void Received(int bytes) { lock (_lock) { _rxBytes += bytes; if (bytes > _rxMax) _rxMax = bytes; } }

        public void Ping(int ms)
        {
            lock (_lock) { _pings++; _pingSum += ms; _pingSq += (double)ms * ms; if (ms > _pingMax) _pingMax = ms; }
        }

        public void Reset()
        {
            lock (_lock)
            {
                _since = Stopwatch.GetTimestamp();
                _txBytes = _rxBytes = 0; _txMax = _rxMax = 0;
                _pings = _pingMax = 0; _pingSum = _pingSq = 0;
            }
        }

        public Snapshot Take()
        {
            lock (_lock)
            {
                double sec = Math.Max(1e-3, (Stopwatch.GetTimestamp() - _since) / (double)Stopwatch.Frequency);
                double avg = _pings > 0 ? _pingSum / _pings : -1;
                double sd = _pings > 0 ? Math.Sqrt(Math.Max(0, _pingSq / _pings - avg * avg)) : -1;
                return new Snapshot
                {
                    Seconds = sec, TxPerSec = _txBytes / sec, RxPerSec = _rxBytes / sec, TxMax = _txMax, RxMax = _rxMax,
                    Pings = _pings, PingAvg = avg, PingMax = _pings > 0 ? _pingMax : -1, PingSd = sd,
                };
            }
        }

        public struct Snapshot
        {
            public double Seconds, TxPerSec, RxPerSec, PingAvg, PingSd;
            public int TxMax, RxMax, Pings, PingMax;

            public string Line() =>
                $"сеть за {Seconds:0} с: отправка {TxPerSec:0} Б/с, приём {RxPerSec:0} Б/с; самая длинная строка: отправлена {TxMax} Б, принята {RxMax} Б; " +
                (Pings > 0 ? $"пинг: среднее {PingAvg:0} мс, максимум {PingMax} мс, разброс {PingSd:0} мс ({Pings} замеров)" : "пинга нет");

            public string Pairs() => string.Join("; ",
                Pair("netSec", Seconds), Pair("netTx", TxPerSec), Pair("netRx", RxPerSec), Pair("netTxMax", TxMax), Pair("netRxMax", RxMax),
                Pair("pingAvg", PingAvg), Pair("pingMax", PingMax), Pair("pingSd", PingSd), Pair("pings", Pings));

            private static string Pair(string k, double v) => k + "=" + Math.Round(v, 1).ToString(CultureInfo.InvariantCulture);
        }
    }
}
