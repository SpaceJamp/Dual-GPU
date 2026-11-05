using System;
using System.Collections.Generic;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Settings;

namespace GpuMonitor.Core.Alerts;

/// <summary>Holds per-card alert state. Not thread-safe: call from one thread.</summary>
public sealed class AlertRules
{
    private const int Margin = 5;
    private const int HistoryMax = 200;
    private static readonly TimeSpan MissingAfter = TimeSpan.FromSeconds(5);

    private sealed class State
    {
        public DateTimeOffset? LastSeen;
        public bool Missing;
        public bool HotActive;
        public bool VramActive;
    }

    private readonly Dictionary<string, State> _states = new();
    private readonly List<Alert> _history = new();
    private DateTimeOffset? _lastEvaluate;

    public IReadOnlyList<Alert> History => _history;

    public IReadOnlyList<Alert> Evaluate(
        IReadOnlyList<Card> known, IReadOnlyList<Reading> readings, DateTimeOffset now, AppSettings s)
    {
        var raised = new List<Alert>();
        var names = new Dictionary<string, string>();
        foreach (var c in known) names[c.BusId] = c.Name;

        if (_lastEvaluate is { } prev && now - prev > TimeSpan.FromSeconds(3 * s.RefreshSeconds))
            foreach (var st in _states.Values) st.LastSeen = now;
        _lastEvaluate = now;

        var seen = new HashSet<string>();
        foreach (var r in readings)
        {
            seen.Add(r.BusId);
            var st = StateFor(r.BusId);
            var name = names.TryGetValue(r.BusId, out var n) ? n : r.BusId;
            st.LastSeen = now;
            if (st.Missing)
            {
                st.Missing = false;
                raised.Add(new Alert(AlertKind.CardBack, r.BusId, name, null, now));
            }

            if (r.TempC.Ok)
            {
                int t = r.TempC.Value!.Value;
                if (!st.HotActive && t >= s.HeatLimitC)
                {
                    st.HotActive = true;
                    raised.Add(new Alert(AlertKind.Hot, r.BusId, name, t, now) { Limit = s.HeatLimitC });
                }
                else if (st.HotActive && t <= s.HeatLimitC - Margin)
                {
                    st.HotActive = false;
                    raised.Add(new Alert(AlertKind.HotCleared, r.BusId, name, t, now) { Limit = s.HeatLimitC });
                }
            }

            if (r.VramPct is { } pct)
            {
                if (!st.VramActive && pct >= s.VramLimitPct)
                {
                    st.VramActive = true;
                    raised.Add(new Alert(AlertKind.LowVram, r.BusId, name, pct, now) { Limit = s.VramLimitPct });
                }
                else if (st.VramActive && pct <= s.VramLimitPct - Margin)
                {
                    st.VramActive = false;
                    raised.Add(new Alert(AlertKind.LowVramCleared, r.BusId, name, pct, now) { Limit = s.VramLimitPct });
                }
            }
        }

        foreach (var c in known)
        {
            if (seen.Contains(c.BusId)) continue;
            var st = StateFor(c.BusId);
            if (st.LastSeen is null) st.LastSeen = now;
            else if (!st.Missing && now - st.LastSeen.Value >= MissingAfter)
            {
                st.Missing = true;
                raised.Add(new Alert(AlertKind.CardMissing, c.BusId, c.Name, null, now));
            }
        }

        _history.AddRange(raised);
        if (_history.Count > HistoryMax) _history.RemoveRange(0, _history.Count - HistoryMax);
        return raised;
    }

    private State StateFor(string busId)
    {
        if (!_states.TryGetValue(busId, out var st)) _states[busId] = st = new State();
        return st;
    }
}
