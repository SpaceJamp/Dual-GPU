using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Readers;

namespace GpuMonitor.Core.Tests;

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    private sealed class FakeTimer : ITimer
    {
        public ManualClock Owner = null!;
        public TimerCallback Callback = null!;
        public object? State;
        public DateTimeOffset? Next;
        public TimeSpan Period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (Owner._gate)
            {
                Next = dueTime == Timeout.InfiniteTimeSpan ? null : Owner._now + dueTime;
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
            }
            return true;
        }

        public void Dispose() { lock (Owner._gate) { Next = null; Owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private readonly object _gate = new();
    private readonly List<FakeTimer> _timers = new();
    private DateTimeOffset _now = start;
    private int _created;

    public DateTimeOffset Now { get { lock (_gate) return _now; } }
    public int TimersCreated => Volatile.Read(ref _created);
    public int LiveTimers { get { lock (_gate) return _timers.Count; } }
    public override DateTimeOffset GetUtcNow() => Now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new FakeTimer { Owner = this, Callback = callback, State = state };
        lock (_gate) { _timers.Add(t); }
        t.Change(dueTime, period);
        Interlocked.Increment(ref _created);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        var target = Now + by;
        while (true)
        {
            FakeTimer? due = null;
            lock (_gate)
            {
                foreach (var t in _timers)
                    if (t.Next is { } n && n <= target && (due is null || n < due.Next!.Value)) due = t;
                if (due is null) { _now = target; return; }
                _now = due.Next!.Value;
                due.Next = due.Period > TimeSpan.Zero ? _now + due.Period : null;
            }
            due.Callback(due.State);
        }
    }
}

internal sealed class StubReader : ICardReader
{
    public NvmlReturn InitCode => NvmlReturn.Success;
    public IReadOnlyList<Card> Cards { get; set; } = Array.Empty<Card>();
    public Func<DateTimeOffset, IReadOnlyList<Reading>> OnPoll { get; set; } = _ => Array.Empty<Reading>();
    public CallTimings Timings { get; } = new();
    public IReadOnlyList<Reading> Poll(DateTimeOffset now) => OnPoll(now);
}

internal static class Make
{
    public static Reading Reading(string bus, DateTimeOffset at, int temp = 50, double power = 100) => new(
        bus, at,
        Field<int>.Of(10), Field<long>.Of(1000), Field<long>.Of(16000), Field<int>.Of(temp),
        Field<double>.Of(power), Field<double>.Of(320), Field<int>.Of(1500), Field<int>.Of(9000),
        Field<int>.Of(1500), Field<int>.Of(30), Field<int>.Of(2), Field<ThrottleReasons>.Of(ThrottleReasons.SwPowerCap),
        Field<bool>.Of(true));

    public static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
}
