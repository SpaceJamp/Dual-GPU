using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Threading;
using GpuMonitor.Core;
using GpuMonitor.Core.Diagnostics;
using GpuMonitor.Core.Logging;
using GpuMonitor.Core.Poll;

namespace GpuMonitor.App.ViewModels;

/// <summary>Diagnostics tab: live log, report, timings and events from the last report, CSV recording.</summary>
public sealed class DiagnosticsViewModel : ObservableBase
{
    public const int MaxLogLines = 500;
    private const string Source = "diagnostics";

    private readonly AppLog _log;
    private readonly Func<FactCollector> _collector;
    private readonly Recorder _recorder;
    private readonly Action<string> _openFolder;
    private readonly string _logDir;
    private readonly TimeProvider _clock;
    private readonly Queue<LogLine> _buffer = new();

    private LogLevel _minLevel = LogLevel.Debug;
    private string _reportText = "";
    private string _status = "";
    private bool _isRecording;
    private IReadOnlyList<string> _timings = Array.Empty<string>();
    private IReadOnlyList<string> _events = Array.Empty<string>();

    public DiagnosticsViewModel(AppLog log, Func<FactCollector> collector, Recorder recorder,
        Func<string, Task>? copyText = null, Action<string>? openFolder = null, string? logDir = null,
        TimeProvider? clock = null)
    {
        _log = log;
        _collector = collector;
        _recorder = recorder;
        CopyText = copyText;
        _openFolder = openFolder ?? OpenInShell;
        _logDir = logDir ?? Paths.LogDir;
        _clock = clock ?? TimeProvider.System;
        _isRecording = recorder.IsRecording;
        // Lines logged before the window existed (startup). A line written between Tail and the
        // subscription can be missed; subscribing first could show it twice instead.
        foreach (var text in log.Tail(MaxLogLines))
            if (LogTailParser.TryParse(text, out var line)) Add(line);
        log.Line += OnLine;

        CreateReportCommand = new RelayCommand(CreateReportInBackground);
        CopyReportCommand = new RelayCommand(CopyReport);
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder);
        ToggleRecordingCommand = new RelayCommand(ToggleRecording);
    }

    /// <summary>Card view models shared with the Cards tab, for the per-card field tables.</summary>
    public ObservableCollection<CardViewModel> Cards { get; internal set; } = new();

    /// <summary>Puts text on the clipboard. The window sets it to its TopLevel clipboard; tests inject a fake.</summary>
    public Func<string, Task>? CopyText { get; set; }

    public ObservableCollection<string> LogLines { get; } = new();

    public IReadOnlyList<LogLevel> LogLevels { get; } = Enum.GetValues<LogLevel>();

    public LogLevel MinLevel
    {
        get => _minLevel;
        set { if (Set(ref _minLevel, value)) Rebuild(); }
    }

    public string ReportText { get => _reportText; private set => Set(ref _reportText, value); }

    /// <summary>Result of the last command, for example "Report copied." or an error.</summary>
    public string Status { get => _status; private set => Set(ref _status, value); }

    public bool IsRecording
    {
        get => _isRecording;
        private set { if (Set(ref _isRecording, value)) OnPropertyChanged(nameof(RecordButtonText)); }
    }

    public string RecordButtonText => IsRecording ? "Stop recording" : "Record";

    /// <summary>Per-call NVML timings from the last report.</summary>
    public IReadOnlyList<string> Timings { get => _timings; private set => Set(ref _timings, value); }

    /// <summary>GPU events from the Windows event log, from the last report.</summary>
    public IReadOnlyList<string> Events { get => _events; private set => Set(ref _events, value); }

    public RelayCommand CreateReportCommand { get; }
    public RelayCommand CopyReportCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand ToggleRecordingCommand { get; }

    /// <summary>Collects facts on a worker thread, runs the checks, and returns the redacted text report.
    /// Timings, Events and ReportText are set when the await resumes (on the UI thread when called from it).</summary>
    public async Task<string> CreateReport() => Apply(await Task.Run(Collect));

    public async Task CopyReport()
    {
        try
        {
            if (ReportText.Length == 0) await CreateReportInBackground();
            if (ReportText.Length == 0) return; // creating failed; keep its error in Status
            if (CopyText is null) { Status = "Clipboard is not available."; return; }
            await CopyText(ReportText);
            Status = "Report copied.";
        }
        catch (Exception ex)
        {
            _log.Error(Source, "Could not copy the report: " + ex.Message, ex);
            Status = "Could not copy the report: " + ex.Message;
        }
    }

    public async Task SaveReport(string path)
    {
        try
        {
            if (ReportText.Length == 0) await CreateReportInBackground();
            if (ReportText.Length == 0) return; // creating failed; keep its error in Status
            await File.WriteAllTextAsync(path, ReportText);
            Status = "Report saved to " + path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _log.Error(Source, "Could not save the report: " + ex.Message, ex);
            Status = "Could not save the report: " + ex.Message;
        }
    }

    public void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(_logDir);
            _openFolder(_logDir);
        }
        catch (Exception ex)
        {
            _log.Error(Source, "Could not open the log folder: " + ex.Message, ex);
            Status = "Could not open the log folder: " + ex.Message;
        }
    }

    public void ToggleRecording()
    {
        try
        {
            if (_recorder.IsRecording)
            {
                _recorder.Stop();
                Status = "Recording stopped.";
            }
            else
            {
                _recorder.Start(_clock.GetLocalNow());
                Status = "Recording to " + _recorder.CurrentFile;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(Source, "Could not start recording: " + ex.Message, ex);
            Status = "Could not start recording: " + ex.Message;
        }
        finally
        {
            IsRecording = _recorder.IsRecording;
        }
    }

    /// <summary>Writes the poll to the recording, if one is running.</summary>
    public void OnPolled(PollResult r)
    {
        try { _recorder.Write(r); }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _log.Error(Source, "Recording failed, stopping: " + ex.Message, ex);
            try { _recorder.Stop(); } catch (IOException) { }
            IsRecording = false;
            Status = "Recording failed: " + ex.Message;
        }
    }

    /// <summary>Logs a failure from the view (for example the save dialog) and shows it in Status.</summary>
    public void ReportError(string message, Exception ex)
    {
        _log.Error(Source, message, ex);
        Status = message;
    }

    /// <summary>Shows a message in Status without logging (the command error handler logs it).</summary>
    internal void ShowStatus(string message) => Status = message;

    internal IEnumerable<RelayCommand> Commands =>
        [CreateReportCommand, CopyReportCommand, OpenLogFolderCommand, ToggleRecordingCommand];

    private async Task CreateReportInBackground()
    {
        try
        {
            Status = "Creating report...";
            await CreateReport();
            Status = "Report created.";
        }
        catch (Exception ex)
        {
            _log.Error(Source, "Could not create the report: " + ex.Message, ex);
            Status = "Could not create the report: " + ex.Message;
        }
    }

    private (Facts Facts, IReadOnlyList<Check> Checks, string Text) Collect()
    {
        var facts = _collector().Collect();
        var checks = Checks.Run(facts);
        return (facts, checks, ReportRender.Text(facts, checks));
    }

    private string Apply((Facts Facts, IReadOnlyList<Check> Checks, string Text) report)
    {
        var inv = CultureInfo.InvariantCulture;
        var timings = report.Facts.State.Timings
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => string.Create(inv,
                $"{kv.Key}: count {kv.Value.Count}, avg {kv.Value.AvgMs:0.##} ms, p99 {kv.Value.P99Ms:0.##} ms, max {kv.Value.MaxMs:0.##} ms"))
            .ToArray();
        Timings = timings.Length == 0 ? new[] { "none" } : timings;

        var ev = report.Facts.EventLog;
        Events = !ev.Ok ? new[] { ev.Error ?? "error" }
            : ev.Value!.Count == 0 ? new[] { "none" }
            : ev.Value!.Select(e => string.Create(inv,
                $"{e.At.ToLocalTime():yyyy-MM-dd HH:mm:ss} {e.Provider} id={e.Id} {e.Level}: {e.Message}")).ToArray();

        ReportText = report.Text;
        return report.Text;
    }

    private void OnLine(LogLine line)
    {
        if (Dispatcher.UIThread.CheckAccess()) Add(line);
        else Dispatcher.UIThread.Post(() => Add(line));
    }

    private void Add(LogLine line)
    {
        _buffer.Enqueue(line);
        while (_buffer.Count > MaxLogLines) _buffer.Dequeue();
        if (line.Level < MinLevel) return;
        LogLines.Add(Format(line));
        while (LogLines.Count > MaxLogLines) LogLines.RemoveAt(0);
    }

    private void Rebuild()
    {
        LogLines.Clear();
        foreach (var l in _buffer)
            if (l.Level >= MinLevel) LogLines.Add(Format(l));
    }

    private static string Format(LogLine l) =>
        l.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + l.Level.ToString().ToUpperInvariant().PadRight(7)
        + " " + l.Source + ": " + l.Message;

    private static void OpenInShell(string dir) =>
        Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
}
