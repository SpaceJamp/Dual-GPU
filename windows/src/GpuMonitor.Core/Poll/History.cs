using GpuMonitor.Core.Model;

namespace GpuMonitor.Core.Poll;

/// <summary>Rolling per-card readings. A null entry marks a gap in polling.</summary>
public sealed class History(int capacity = 60)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<Reading?>> _cards = new();
    private readonly Dictionary<string, DateTimeOffset> _lastAt = new();

    public void Add(Reading r, double refreshSeconds)
    {
        lock (_gate)
        {
            if (!_cards.TryGetValue(r.BusId, out var list))
                _cards[r.BusId] = list = new List<Reading?>();
            if (_lastAt.TryGetValue(r.BusId, out var prev) && r.At - prev > TimeSpan.FromSeconds(3 * refreshSeconds))
                list.Add(null);
            list.Add(r);
            _lastAt[r.BusId] = r.At;
            if (list.Count > capacity) list.RemoveRange(0, list.Count - capacity);
        }
    }

    public IReadOnlyList<Reading?> For(string busId)
    {
        lock (_gate)
            return _cards.TryGetValue(busId, out var list) ? list.ToArray() : Array.Empty<Reading?>();
    }
}
