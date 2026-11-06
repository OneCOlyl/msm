using System;
using System.Diagnostics;
using System.Linq;

namespace MagicSurvivalCoop
{
    // Lap зовётся каждый кадр, поэтому без выделений памяти.
    internal sealed class Laps
    {
        public readonly string[] Names;
        public readonly double[] Now, Sum, Max;
        public int Count;                       // Число полных замеров, делитель для средних
        private long _at;

        public Laps(params string[] names)
        {
            Names = names;
            Now = new double[names.Length]; Sum = new double[names.Length]; Max = new double[names.Length];
        }

        public void NewFrame() => Array.Clear(Now, 0, Now.Length);
        public void Start() => _at = Stopwatch.GetTimestamp();

        public void Lap(int i)
        {
            long t = Stopwatch.GetTimestamp();
            double ms = (t - _at) * 1000.0 / Stopwatch.Frequency;
            Now[i] += ms; Sum[i] += ms; if (ms > Max[i]) Max[i] = ms;
            _at = t;
        }

        public void Reset() { Array.Clear(Now, 0, Now.Length); Array.Clear(Sum, 0, Sum.Length); Array.Clear(Max, 0, Max.Length); Count = 0; }

        public string Line(double[] ms) => Line(ms, 0.05, "0.0");
        private string Line(double[] ms, double min, string fmt) =>
            string.Join(", ", Names.Select((n, i) => (n, v: ms[i])).Where(x => x.v >= min).Select(x => $"{x.n} {x.v.ToString(fmt)}"));

        public string Summary() => Count == 0 ? "" : $"среднее мс: {Line(Sum.Select(v => v / Count).ToArray(), 0.005, "0.00")}; максимум мс: {Line(Max)}";
    }
}
