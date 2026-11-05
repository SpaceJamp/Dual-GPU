using System;

namespace GpuMonitor.Core.Model;

public sealed record Reading(
    string BusId,
    DateTimeOffset At,
    Field<int> LoadPct,
    Field<long> VramUsedMiB,
    Field<long> VramTotalMiB,
    Field<int> TempC,
    Field<double> PowerW,
    Field<double> PowerLimitW,
    Field<int> GraphicsMHz,
    Field<int> MemoryMHz,
    Field<int> SmMHz,
    Field<int> FanPct,
    Field<int> PState,
    Field<ThrottleReasons> Throttle,
    Field<bool> DisplayActive)
{
    public double? VramPct =>
        VramUsedMiB.Ok && VramTotalMiB.Ok && VramTotalMiB.Value!.Value > 0
            ? 100.0 * VramUsedMiB.Value!.Value / VramTotalMiB.Value!.Value
            : null;
}
