using System.Globalization;
using System.Text;
using System.Text.Json;

namespace GpuMonitor.Core.Logging;

public enum LogLevel { Debug, Info, Warning, Error }

public sealed record LogLine(
    DateTimeOffset At,
    string RunId,
    LogLevel Level,
    string Source,
    string Message,
    string? Exception);

public sealed class AppLog : IDisposable
{
    private const int MemoryLines = 500;

    private readonly object _gate = new();
    private readonly RollingFile? _file;
    private readonly TimeProvider _clock;
    private readonly bool _noop;
    private readonly Queue<string> _memory = new();
    private volatile int _level;
    private volatile bool _json;

    public AppLog(string? filePath, LogLevel level, bool json, TimeProvider? clock = null)
    {
        _file = filePath is null ? null : new RollingFile(filePath);
        _level = (int)level;
        _json = json;
        _clock = clock ?? TimeProvider.System;
        RunId = Guid.NewGuid().ToString("N")[..8];
    }

    private AppLog()
    {
        _noop = true;
        _clock = TimeProvider.System;
        RunId = "00000000";
        _level = (int)LogLevel.Error + 1;
    }

    /// <summary>Writes nothing and keeps nothing; the Line event never fires.</summary>
    public static AppLog Null { get; } = new();

    public string RunId { get; }

    public LogLevel Level
    {
        get => (LogLevel)_level;
        set { if (!_noop) _level = (int)value; }
    }

    public bool Json
    {
        get => _json;
        set { if (!_noop) _json = value; }
    }

    public string? LastWriteError => _file?.LastWriteError;

    public event Action<LogLine>? Line
    {
        add { if (!_noop) _line += value; }
        remove { if (!_noop) _line -= value; }
    }
    private Action<LogLine>? _line;

    public void Debug(string source, string message, Exception? ex = null) => Write(LogLevel.Debug, source, message, ex);
    public void Info(string source, string message, Exception? ex = null) => Write(LogLevel.Info, source, message, ex);
    public void Warn(string source, string message, Exception? ex = null) => Write(LogLevel.Warning, source, message, ex);
    public void Error(string source, string message, Exception? ex = null) => Write(LogLevel.Error, source, message, ex);

    public IReadOnlyList<string> Tail(int lines)
    {
        if (_noop || lines <= 0) return Array.Empty<string>();
        lock (_gate)
        {
            if (_file is null)
                return _memory.Skip(Math.Max(0, _memory.Count - lines)).ToArray();
            try
            {
                if (!File.Exists(_file.Path)) return Array.Empty<string>();
                using var fs = new FileStream(_file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                var all = new List<string>();
                string? l;
                while ((l = reader.ReadLine()) is not null) all.Add(l);
                return all.Skip(Math.Max(0, all.Count - lines)).ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }
    }

    public void Dispose()
    {
    }

    private void Write(LogLevel level, string source, string message, Exception? ex)
    {
        if (_noop || (int)level < _level) return;
        var line = new LogLine(_clock.GetLocalNow(), RunId, level, source, message, ex?.ToString());
        var text = _json ? FormatJson(line) : FormatText(line);
        lock (_gate)
        {
            if (_file is null)
            {
                foreach (var l in text.TrimEnd('\n').Split('\n')) _memory.Enqueue(l.TrimEnd('\r'));
                while (_memory.Count > MemoryLines) _memory.Dequeue();
            }
            else
            {
                _file.Write(text);
            }
        }
        try { _line?.Invoke(line); }
        catch (Exception) { /* a faulty subscriber must not break logging */ }
    }

    private static string LevelName(LogLevel level) => level switch
    {
        LogLevel.Debug => "DEBUG",
        LogLevel.Info => "INFO",
        LogLevel.Warning => "WARNING",
        _ => "ERROR",
    };

    private static string FormatText(LogLine l)
    {
        var sb = new StringBuilder();
        sb.Append(l.At.ToString("o", CultureInfo.InvariantCulture)).Append(' ')
          .Append(l.RunId).Append(' ')
          .Append(LevelName(l.Level).PadRight(7)).Append(' ')
          .Append(l.Source).Append(": ").Append(l.Message).Append('\n');
        if (l.Exception is not null) sb.Append(l.Exception.TrimEnd()).Append('\n');
        return sb.ToString();
    }

    private static string FormatJson(LogLine l)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("time", l.At.ToString("o", CultureInfo.InvariantCulture));
            w.WriteString("run", l.RunId);
            w.WriteString("level", LevelName(l.Level));
            w.WriteString("source", l.Source);
            w.WriteString("msg", l.Message);
            if (l.Exception is null) w.WriteNull("exception"); else w.WriteString("exception", l.Exception);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }
}
