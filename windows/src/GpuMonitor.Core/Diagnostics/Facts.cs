using GpuMonitor.Core.Alerts;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Readers;
using GpuMonitor.Core.Settings;

namespace GpuMonitor.Core.Diagnostics;

public sealed record Fact<T>(bool Ok, T? Value, string? Error)
{
    public static Fact<T> Of(T value) => new(true, value, null);
    public static Fact<T> Fail(string error) => new(false, default, error.StartsWith("error: ") ? error : "error: " + error);
}
// NVML failures use Fail(code.NvmlReturnName()) -> "error: NVML_ERROR_NOT_SUPPORTED".

public sealed record AppFacts(string Version, string Commit, string Runtime);
public sealed record WindowsFacts(Fact<string> Version, Fact<TimeSpan> Uptime, Fact<int?> HwSchMode, Fact<bool?> GameMode);
public sealed record DriverFacts(NvmlReturn InitCode, string? NvmlPath, string? LoadError,
    Fact<string> DriverVersion, Fact<string> NvmlVersion, Fact<string> CudaVersion);   // CudaVersion via Units.CudaVersion, e.g. "12.8"
public sealed record ProcessFact(uint Pid, string Name, long? UsedMiB);               // Name "error: exited" if gone; UsedMiB null when NVML gives ulong.MaxValue
public sealed record CardFacts(string BusId,
    Fact<string> Name, Fact<string> Uuid, Fact<string> Vbios,
    Fact<uint> PcieGenCur, Fact<uint> PcieGenMax, Fact<uint> PcieWidthCur, Fact<uint> PcieWidthMax,
    Fact<bool> DisplayActive,
    Fact<uint> TempC, Fact<uint> TempSlowdownC, Fact<uint> TempShutdownC,
    Fact<double> PowerW, Fact<double> PowerLimitW, Fact<double> PowerDefaultW,
    Fact<uint> GraphicsMHz, Fact<uint> GraphicsMaxMHz, Fact<uint> MemoryMHz, Fact<uint> MemoryMaxMHz, Fact<uint> SmMHz, Fact<uint> SmMaxMHz,
    Fact<uint> FanPct, Fact<int> PState, Fact<ThrottleReasons> Throttle,
    Fact<long> VramUsedMiB, Fact<long> VramTotalMiB,
    Fact<IReadOnlyList<ProcessFact>> Processes);
public sealed record StateFacts(AppSettings Settings, SettingsStatus SettingsStatus, string? StartupValue,
    IReadOnlyList<Alert> Alerts, IReadOnlyDictionary<string, TimingStats> Timings,
    IReadOnlyList<string> LogTail, bool LogWritable);
public sealed record Facts(DateTimeOffset CollectedAt, AppFacts App, WindowsFacts Windows, DriverFacts Driver,
    IReadOnlyList<CardFacts> Cards,
    Fact<IReadOnlyList<WmiAdapter>> Wmi, Fact<string?> NvidiaSmi,
    Fact<IReadOnlyDictionary<string, string>> GpuPreferences, Fact<IReadOnlyList<GpuEvent>> EventLog,
    StateFacts State);
