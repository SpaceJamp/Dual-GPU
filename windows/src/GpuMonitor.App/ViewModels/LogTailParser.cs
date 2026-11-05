using System.Globalization;
using System.Text.Json;
using GpuMonitor.Core.Logging;

namespace GpuMonitor.App.ViewModels;

/// <summary>Turns a line from <see cref="AppLog.Tail"/> (text or JSON format) back into a LogLine.
/// Exception continuation lines do not parse; the live view leaves exceptions out too.</summary>
internal static class LogTailParser
{
    public static bool TryParse(string text, out LogLine line)
    {
        line = null!;
        try
        {
            return text.StartsWith('{') ? TryJson(text, out line) : TryText(text, out line);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return false;
        }
    }

    // "<time o> <run> <LEVEL padded> <source>: <message>"
    private static bool TryText(string text, out LogLine line)
    {
        line = null!;
        var parts = text.Split(' ', 4, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 4 || !TryTime(parts[0], out var at) || !TryLevel(parts[2], out var level)) return false;
        var rest = parts[3].TrimStart();
        var colon = rest.IndexOf(": ", StringComparison.Ordinal);
        if (colon <= 0) return false;
        line = new LogLine(at, parts[1], level, rest[..colon], rest[(colon + 2)..], null);
        return true;
    }

    private static bool TryJson(string text, out LogLine line)
    {
        line = null!;
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!TryTime(root.GetProperty("time").GetString(), out var at)
            || !TryLevel(root.GetProperty("level").GetString(), out var level)) return false;
        line = new LogLine(at, root.GetProperty("run").GetString() ?? "", level,
            root.GetProperty("source").GetString() ?? "", root.GetProperty("msg").GetString() ?? "", null);
        return true;
    }

    private static bool TryTime(string? s, out DateTimeOffset at) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at);

    private static bool TryLevel(string? s, out LogLevel level)
    {
        (var ok, level) = s switch
        {
            "DEBUG" => (true, LogLevel.Debug),
            "INFO" => (true, LogLevel.Info),
            "WARNING" => (true, LogLevel.Warning),
            "ERROR" => (true, LogLevel.Error),
            _ => (false, LogLevel.Info),
        };
        return ok;
    }
}
