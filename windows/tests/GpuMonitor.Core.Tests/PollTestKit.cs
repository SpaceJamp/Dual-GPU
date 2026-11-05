using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Readers;

namespace GpuMonitor.Core.Tests;

internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;
    public override DateTimeOffset GetUtcNow() => Now;
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
