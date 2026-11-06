using System.Runtime.InteropServices;
using GpuMonitor.App.Platform;
using GpuMonitor.App.ViewModels;
using GpuMonitor.Core;
using GpuMonitor.Core.Alerts;
using GpuMonitor.Core.Diagnostics;
using GpuMonitor.Core.Logging;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Poll;
using GpuMonitor.Core.Readers;
using GpuMonitor.Core.Settings;

namespace GpuMonitor.App;

/// <summary>Everything that runs without a UI, in startup order: settings, log setup, startup facts, NVML, poller.
/// --diagnose stops here; the window and tray come from <see cref="Shell"/>.</summary>
internal sealed class Bootstrap : IDisposable
{
    private const string Source = "startup";
    private const int MaxAlerts = 200;

    private readonly SettingsStore _store;
    private readonly CardSources _sources;
    private readonly IStartup _startup;
    private readonly List<Alert> _alerts = new();
    private volatile SettingsStatus _settingsStatus;

    public Bootstrap(AppLog log, string settingsFile, string recordingDir, Func<NvmlLoad> loadNvml,
        CardSources sources, IStartup startup, TimeProvider clock)
    {
        Log = log;
        _sources = sources;
        _startup = startup;
        _store = new SettingsStore(settingsFile, log);
        var loaded = _store.Load();
        _settingsStatus = loaded.Status;
        Settings = new CurrentSettings(loaded.Settings);
        log.Json = loaded.Settings.LogJson;

        log.Info(Source, $"GPU Monitor {AppInfo.Version} ({AppInfo.Commit}), {AppInfo.Runtime}, {RuntimeInformation.OSDescription}, run {log.RunId}");
        log.Info(Source, "settings: " + loaded.Status);

        var now = clock.GetUtcNow();
        Reader = new RetryingReader(loadNvml, log, now);
        Reader.Poll(now);
        var driver = "unknown";
        if (Reader.Nvml?.GetDriverVersion(out var version) == NvmlReturn.Success) driver = version;
        log.Info(Source, $"driver {driver}, cards found: {Reader.Cards.Count}");
        // Applied last, so the startup facts reach the log at any configured level.
        log.Level = loaded.Settings.LogLevel;

        Recorder = new Recorder(recordingDir);
        Poller = new Poller(Reader, new AlertRules(), History, Settings.Get, log, clock);
    }

    /// <summary>Real paths, the real NVML and this platform's sources.</summary>
    public static Bootstrap ForThisPc(AppLog log) =>
        new(log, Paths.SettingsFile, Paths.RecordingDir, LoadNvml, Platform.Platform.Sources(), Platform.Platform.Startup(), TimeProvider.System);

    private static NvmlLoad LoadNvml() =>
        NvmlApi.TryLoad(out var api, out var error)
            ? new NvmlLoad(api, api!.LoadedPath, null)
            : new NvmlLoad(null, null, error);

    public AppLog Log { get; }
    public CurrentSettings Settings { get; }
    public RetryingReader Reader { get; }
    public History History { get; } = new();
    public Recorder Recorder { get; }
    public Poller Poller { get; }

    /// <summary>Keeps recent alerts for the diagnostic report. Reports are collected off the UI thread.</summary>
    public void NoteAlerts(IReadOnlyList<Alert> alerts)
    {
        lock (_alerts)
        {
            _alerts.AddRange(alerts);
            if (_alerts.Count > MaxAlerts) _alerts.RemoveRange(0, _alerts.Count - MaxAlerts);
        }
    }

    /// <summary>A collector over the state right now. Called once per report.</summary>
    public FactCollector Collector()
    {
        string? runValue;
        try { runValue = _sources.Registry.RunValue(); }
        catch (Exception) { runValue = null; }

        Alert[] alerts;
        lock (_alerts) alerts = _alerts.ToArray();

        return new FactCollector(Reader.Nvml, Reader.InitCode, Reader.LoadedPath, Reader.LoadError, _sources,
            new AppState(AppInfo.Version, AppInfo.Commit, AppInfo.Runtime, Settings.Value, _settingsStatus, runValue, alerts,
                Reader.Timings.Snapshot(), Log.Tail(FactCollector.LogTailLines), Log.LastWriteError is null));
    }

    public MainViewModel CreateViewModel()
    {
        var diagnostics = new DiagnosticsViewModel(Log, Collector, Recorder);
        var settings = new SettingsViewModel(_store, _startup, Log, Settings);
        settings.Saved += _ => _settingsStatus = SettingsStatus.Loaded;
        return new MainViewModel(Reader.InitCode, Reader.LoadError, Log, History, Settings, diagnostics, settings);
    }

    public void Dispose()
    {
        Poller.Dispose();
        Recorder.Stop();
        Reader.Dispose();
    }
}
