using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using GpuMonitor.Core.Diagnostics;

namespace GpuMonitor.App.Platform.Windows;

/// <summary>NVIDIA driver (nvlddmkm) events and Display 4101 (driver stopped responding) from the System log.</summary>
[SupportedOSPlatform("windows")]
public sealed class EventLogSource : IEventLogSource
{
    private const int MaxEvents = 100;

    internal static string XPath(TimeSpan window) =>
        "*[System[(Provider[@Name='nvlddmkm'] or (Provider[@Name='Display'] and EventID=4101)) and TimeCreated[timediff(@SystemTime) <= "
        + ((long)window.TotalMilliseconds).ToString(CultureInfo.InvariantCulture) + "]]]";

    public IReadOnlyList<GpuEvent> Recent(TimeSpan window)
    {
        var query = new EventLogQuery("System", PathType.LogName, XPath(window)) { ReverseDirection = true };
        var list = new List<GpuEvent>();
        using var reader = new EventLogReader(query);
        while (list.Count < MaxEvents)
        {
            using var record = reader.ReadEvent();
            if (record is null) break;
            list.Add(new GpuEvent(
                record.TimeCreated is { } t ? new DateTimeOffset(t) : DateTimeOffset.MinValue,
                record.ProviderName ?? "",
                record.Id,
                Level(record),
                Message(record)));
        }
        return list;
    }

    private static string Message(EventRecord record)
    {
        try { return record.FormatDescription() ?? "(no description)"; }
        catch (Exception) { return "(no description)"; }
    }

    private static string Level(EventRecord record)
    {
        try
        {
            var name = record.LevelDisplayName;
            if (!string.IsNullOrEmpty(name)) return name;
        }
        catch (Exception) { /* fall back to the number */ }
        return record.Level?.ToString(CultureInfo.InvariantCulture) ?? "";
    }
}
