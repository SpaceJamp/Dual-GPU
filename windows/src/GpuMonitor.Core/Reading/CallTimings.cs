namespace GpuMonitor.Core.Readers;

public sealed record TimingStats(int Count, double MinMs, double AvgMs, double MaxMs, double P99Ms);

public sealed class CallTimings
{
    private const int MaxSamples = 600;

    private readonly object _gate = new();
    private readonly Dictionary<string, Queue<double>> _samples = new();

    public void Record(string call, TimeSpan t)
    {
        lock (_gate)
        {
            if (!_samples.TryGetValue(call, out var q))
                _samples[call] = q = new Queue<double>();
            q.Enqueue(t.TotalMilliseconds);
            while (q.Count > MaxSamples) q.Dequeue();
        }
    }

    public IReadOnlyDictionary<string, TimingStats> Snapshot()
    {
        var result = new Dictionary<string, TimingStats>();
        lock (_gate)
        {
            foreach (var (call, q) in _samples)
            {
                var sorted = q.ToArray();
                Array.Sort(sorted);
                var p99 = sorted[(int)Math.Ceiling(0.99 * sorted.Length) - 1];
                result[call] = new TimingStats(sorted.Length, sorted[0], sorted.Average(), sorted[^1], p99);
            }
        }
        return result;
    }
}
