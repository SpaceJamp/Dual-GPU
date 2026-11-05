using GpuMonitor.Core.Alerts;
using GpuMonitor.Core.Model;

namespace GpuMonitor.Core.Poll;

public sealed record PollResult(
    DateTimeOffset At,
    IReadOnlyList<Card> Cards,
    IReadOnlyList<Reading> Readings,
    IReadOnlyList<Alert> Alerts,
    TimeSpan Took);
