using GpuMonitor.App.Platform;
using GpuMonitor.Core.Logging;
using GpuMonitor.Core.Settings;

namespace GpuMonitor.App.ViewModels;

/// <summary>Editable copy of <see cref="AppSettings"/>. Nothing applies until <see cref="Save"/>.</summary>
public sealed class SettingsViewModel : ObservableBase
{
    private const string Source = "settings";

    private readonly SettingsStore _store;
    private readonly IStartup _startup;
    private readonly AppLog _log;
    private readonly CurrentSettings _current;

    private double? _refreshSeconds;
    private int? _heatLimitC;
    private int? _vramLimitPct;
    private LogLevel _logLevel;
    private bool _logJson;
    private bool _startWithWindows;
    private string _expectedDisplayCard = "";
    private string _status = "";

    public SettingsViewModel(SettingsStore store, IStartup startup, AppLog log, CurrentSettings current)
    {
        _store = store;
        _startup = startup;
        _log = log;
        _current = current;
        Load(current.Value);
        SaveCommand = new RelayCommand(Save);
    }

    public event Action<AppSettings>? Saved;

    public IReadOnlyList<LogLevel> LogLevels { get; } = Enum.GetValues<LogLevel>();

    // Nullable because a cleared NumericUpDown writes null. Save treats null as "keep the current value",
    // and Load then puts the real value back, which the box shows because the value changed.
    public double? RefreshSeconds { get => _refreshSeconds; set => Set(ref _refreshSeconds, value); }
    public int? HeatLimitC { get => _heatLimitC; set => Set(ref _heatLimitC, value); }
    public int? VramLimitPct { get => _vramLimitPct; set => Set(ref _vramLimitPct, value); }
    public LogLevel LogLevel { get => _logLevel; set => Set(ref _logLevel, value); }
    public bool LogJson { get => _logJson; set => Set(ref _logJson, value); }
    public bool StartWithWindows { get => _startWithWindows; set => Set(ref _startWithWindows, value); }
    public string ExpectedDisplayCard { get => _expectedDisplayCard; set => Set(ref _expectedDisplayCard, value ?? ""); }

    /// <summary>"Saved." or the reason saving failed; empty before the first save.</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }

    public RelayCommand SaveCommand { get; }

    /// <summary>Clamps, writes the file, applies startup and log settings, and publishes the new settings.</summary>
    public void Save()
    {
        var before = _current.Value;
        var s = new AppSettings
        {
            RefreshSeconds = RefreshSeconds ?? before.RefreshSeconds,
            HeatLimitC = HeatLimitC ?? before.HeatLimitC,
            VramLimitPct = VramLimitPct ?? before.VramLimitPct,
            LogLevel = LogLevel,
            LogJson = LogJson,
            StartWithWindows = StartWithWindows,
            ExpectedDisplayCard = ExpectedDisplayCard,
        }.Clamp();
        Load(s);

        try
        {
            _store.Save(s);
            Status = "Saved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(Source, "Could not save settings: " + ex.Message, ex);
            Status = "Could not save settings: " + ex.Message;
        }

        try { _startup.Set(s.StartWithWindows); }
        catch (Exception ex)
        {
            _log.Error(Source, "Could not change start with Windows: " + ex.Message, ex);
            Status = "Could not change start with Windows: " + ex.Message;
        }

        LogChange(nameof(s.RefreshSeconds), before.RefreshSeconds, s.RefreshSeconds);
        LogChange(nameof(s.HeatLimitC), before.HeatLimitC, s.HeatLimitC);
        LogChange(nameof(s.VramLimitPct), before.VramLimitPct, s.VramLimitPct);
        LogChange(nameof(s.LogLevel), before.LogLevel, s.LogLevel);
        LogChange(nameof(s.LogJson), before.LogJson, s.LogJson);
        LogChange(nameof(s.StartWithWindows), before.StartWithWindows, s.StartWithWindows);
        LogChange(nameof(s.ExpectedDisplayCard), before.ExpectedDisplayCard, s.ExpectedDisplayCard);

        _log.Level = s.LogLevel;
        _log.Json = s.LogJson;
        _current.Value = s;
        Saved?.Invoke(s);
    }

    private void LogChange<T>(string name, T before, T after)
    {
        if (!EqualityComparer<T>.Default.Equals(before, after))
            _log.Info(Source, $"{name} = {after} (was {before})");
    }

    internal void ShowStatus(string message) => Status = message;

    private void Load(AppSettings s)
    {
        RefreshSeconds = s.RefreshSeconds;
        HeatLimitC = s.HeatLimitC;
        VramLimitPct = s.VramLimitPct;
        LogLevel = s.LogLevel;
        LogJson = s.LogJson;
        StartWithWindows = s.StartWithWindows;
        ExpectedDisplayCard = s.ExpectedDisplayCard;
    }
}
