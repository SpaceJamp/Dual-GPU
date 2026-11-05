namespace GpuMonitor.Core.Settings;

public sealed record AppSettings
{
    public double RefreshSeconds { get; init; } = 1.0;
    public int HeatLimitC { get; init; } = 80;
    public int VramLimitPct { get; init; } = 90;
}
