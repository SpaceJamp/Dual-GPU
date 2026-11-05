using GpuMonitor.Core.Logging;

namespace GpuMonitor.Core.Settings;

public sealed record AppSettings
{
    public const string DefaultExpectedDisplayCard = "RTX 5080";

    public double RefreshSeconds { get; init; } = 1.0;
    public int HeatLimitC { get; init; } = 80;
    public int VramLimitPct { get; init; } = 90;
    public LogLevel LogLevel { get; init; } = LogLevel.Info;
    public bool LogJson { get; init; } = false;
    public bool StartWithWindows { get; init; } = false;
    public string ExpectedDisplayCard { get; init; } = DefaultExpectedDisplayCard;

    /// <summary>Returns a copy with every value forced into its allowed range.</summary>
    public AppSettings Clamp() => this with
    {
        RefreshSeconds = double.IsNaN(RefreshSeconds) ? 1.0 : Math.Clamp(RefreshSeconds, 0.5, 5.0),
        HeatLimitC = Math.Clamp(HeatLimitC, 50, 95),
        VramLimitPct = Math.Clamp(VramLimitPct, 50, 99),
        ExpectedDisplayCard = string.IsNullOrWhiteSpace(ExpectedDisplayCard) ? DefaultExpectedDisplayCard : ExpectedDisplayCard,
    };
}
