using GpuMonitor.App.Platform;
using GpuMonitor.App.ViewModels;
using GpuMonitor.Core.Alerts;
using GpuMonitor.Core.Diagnostics;
using GpuMonitor.Core.Logging;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Poll;
using GpuMonitor.Core.Readers;
using GpuMonitor.Core.Settings;

namespace GpuMonitor.App.Tests;

/// <summary>Builders for view model tests: readings, poll results and a fully faked MainViewModel.</summary>
internal static class VmKit
{
    public static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    public static Reading Reading(string busId, DateTimeOffset at, int temp = 50, int load = 10, bool display = false) =>
        new(busId, at,
            Field<int>.Of(load), Field<long>.Of(1000), Field<long>.Of(16000), Field<int>.Of(temp),
            Field<double>.Of(120.5), Field<double>.Of(300), Field<int>.Of(1500), Field<int>.Of(10000),
            Field<int>.Of(1500), Field<int>.Of(40), Field<int>.Of(2),
            Field<ThrottleReasons>.Of(ThrottleReasons.None), Field<bool>.Of(display));

    public static Card Card(string busId, string name) => new(busId, name, "GPU-" + busId, 0);

    public static PollResult Poll(DateTimeOffset at, params (string BusId, string Name, bool Display)[] cards) =>
        new(at,
            cards.Select(c => Card(c.BusId, c.Name)).ToArray(),
            cards.Select(c => Reading(c.BusId, at, display: c.Display)).ToArray(),
            Array.Empty<Alert>(),
            TimeSpan.FromMilliseconds(3));

    public static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gpumon-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static FactCollector Collector(AppLog log) =>
        new(null, NvmlReturn.LibraryNotFound, null, "nvml.dll not found", NullSources.All(),
            new AppState("1.0", "abc", ".NET", new AppSettings(), SettingsStatus.Missing, null,
                Array.Empty<Alert>(), new Dictionary<string, TimingStats> { ["GetTemperature"] = new(3, 0.1, 0.2, 0.3, 0.3) },
                log.Tail(40), true));

    public sealed class FakeStartup : IStartup
    {
        public List<bool> Calls { get; } = new();
        public bool IsEnabled() => Calls.Count > 0 && Calls[^1];
        public void Set(bool on) => Calls.Add(on);
    }

    public static MainViewModel MainVm(string dir, NvmlReturn init = NvmlReturn.Success, string? loadError = null,
        AppLog? log = null, History? history = null, Recorder? recorder = null)
    {
        log ??= new AppLog(null, LogLevel.Debug, false);
        var current = new CurrentSettings(new AppSettings());
        var diagnostics = new DiagnosticsViewModel(log, () => Collector(log), recorder ?? new Recorder(Path.Combine(dir, "rec")));
        var settings = new SettingsViewModel(new SettingsStore(Path.Combine(dir, "settings.json"), log),
            new FakeStartup(), log, current);
        return new MainViewModel(init, loadError, log, history ?? new History(), current, diagnostics, settings);
    }
}
