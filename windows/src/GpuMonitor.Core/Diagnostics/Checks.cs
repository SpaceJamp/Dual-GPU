using System.Globalization;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Settings;

namespace GpuMonitor.Core.Diagnostics;

/// <summary>The eleven diagnostic checks, evaluated over a Facts snapshot.</summary>
public static class Checks
{
    private const int ExpectedCards = 2;
    private const int HeadroomWarnC = 5;
    private const double PollWarnMs = 100;

    private const string NvmlLoadHint = @"Install or reinstall the NVIDIA driver. nvml.dll ships with it in C:\Windows\System32.";
    private const string CardCountHint = "Check that both cards show in Device Manager under Display adapters. Reseat the card or its power cables if one is missing.";
    private const string WmiMatchHint = "Windows and the NVIDIA driver disagree about the cards. Restart, then reinstall the driver with a clean install.";
    private const string DisplayCardHint = "Plug the monitor into the RTX 5080, or change \"Expected display card\" in Settings.";
    private const string PcieWidthHint = "Run a game or load first: some boards drop lanes at idle. If it stays low, check the slot and BIOS lane settings.";
    private const string TempHeadroomHint = "Improve airflow or lower the power limit. The card will slow itself down at its slowdown limit.";
    private const string ThrottleHint = "Thermal throttling means the card is too hot. Power cap means it hit its power limit, which is normal under full load.";
    private const string DriverErrorsHint = "Driver crashes are often caused by unstable overclocks, power supply limits or old drivers. Remove overclocks and update the driver.";
    private const string SettingsHint = "Settings were reset to defaults. The bad file was kept as settings.json.bad.";
    private const string SettingsMissingHint = "No settings file yet. Defaults are in use until you save settings.";
    private const string WmiUnavailableHint = "Check that the Windows Management Instrumentation service is running.";
    private const string EventLogUnavailableHint = "Check that the Windows Event Log service is running.";
    private const string LogWritableHint = @"Check that %LOCALAPPDATA%\GpuMonitor\logs exists and is not read-only.";
    private const string PollTimeHint = "NVML is slow to answer. Check the driver and the per-call timings in this report.";

    public static IReadOnlyList<Check> Run(Facts f) => new[]
    {
        NvmlLoad(f), CardCount(f), WmiMatch(f), DisplayCard(f), PcieWidth(f), TempHeadroom(f),
        Throttle(f), DriverErrors(f), Settings(f), LogWritable(f), PollTime(f),
    };

    private static Check Pass(string id, string message) => new(CheckLevel.Pass, id, message, "");

    private static string CardName(CardFacts c) => c.Name.Ok && c.Name.Value is { } n ? n : c.BusId;

    private static Check NvmlLoad(Facts f)
    {
        var d = f.Driver;
        if (d.InitCode == NvmlReturn.Success) return Pass("nvml-load", "NVML loaded");
        var why = string.IsNullOrWhiteSpace(d.LoadError) ? d.InitCode.ToString() : d.LoadError;
        return new(CheckLevel.Fail, "nvml-load", $"NVML failed to load: {why}", NvmlLoadHint);
    }

    private static Check CardCount(Facts f)
    {
        var n = f.Cards.Count;
        return n >= ExpectedCards
            ? Pass("card-count", $"{n} cards found")
            : new(CheckLevel.Fail, "card-count", $"{n} of {ExpectedCards} expected cards found", CardCountHint);
    }

    private static Check WmiMatch(Facts f)
    {
        if (!f.Wmi.Ok || f.Wmi.Value is not { Count: > 0 } list)
            return new(CheckLevel.Info, "wmi-match", "Windows adapter list not available", WmiUnavailableHint);
        var nvidia = list.Count(a => a.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        return nvidia == f.Cards.Count
            ? Pass("wmi-match", $"Windows and NVML both see {nvidia} NVIDIA cards")
            : new(CheckLevel.Warn, "wmi-match", $"Windows sees {nvidia} NVIDIA adapters, NVML sees {f.Cards.Count}", WmiMatchHint);
    }

    private static Check DisplayCard(Facts f)
    {
        var active = f.Cards.Where(c => c.DisplayActive is { Ok: true, Value: true }).ToList();
        if (active.Count == 0)
            return new(CheckLevel.Warn, "display-card", "No card is driving a display", DisplayCardHint);
        if (active.Count > 1)
            return new(CheckLevel.Warn, "display-card",
                "More than one card is driving a display: " + string.Join("; ", active.Select(CardName)), DisplayCardHint);
        var name = CardName(active[0]);
        var expected = f.State.Settings.ExpectedDisplayCard;
        return name.Contains(expected, StringComparison.OrdinalIgnoreCase)
            ? Pass("display-card", $"{name} drives the display")
            : new(CheckLevel.Warn, "display-card", $"{name} drives the display, expected {expected}", DisplayCardHint);
    }

    private static Check PcieWidth(Facts f)
    {
        var bad = new List<string>();
        foreach (var c in f.Cards)
        {
            if (!c.PcieWidthCur.Ok || !c.PcieWidthMax.Ok) continue;
            if (c.PcieWidthCur.Value < c.PcieWidthMax.Value)
                bad.Add($"{CardName(c)}: x{c.PcieWidthCur.Value} of x{c.PcieWidthMax.Value}");
        }
        return bad.Count == 0
            ? Pass("pcie-width", "PCIe links at full width")
            : new(CheckLevel.Warn, "pcie-width", string.Join("; ", bad), PcieWidthHint);
    }

    private static Check TempHeadroom(Facts f)
    {
        var bad = new List<string>();
        foreach (var c in f.Cards)
        {
            if (!c.TempC.Ok || !c.TempSlowdownC.Ok) continue;
            var headroom = (long)c.TempSlowdownC.Value - c.TempC.Value;
            if (headroom <= HeadroomWarnC)
                bad.Add($"{CardName(c)}: {c.TempC.Value} C, {headroom} C below slowdown at {c.TempSlowdownC.Value} C");
        }
        return bad.Count == 0
            ? Pass("temp-headroom", "Temperatures have headroom")
            : new(CheckLevel.Warn, "temp-headroom", string.Join("; ", bad), TempHeadroomHint);
    }

    private static Check Throttle(Facts f)
    {
        var level = CheckLevel.Pass;
        var parts = new List<string>();
        foreach (var c in f.Cards)
        {
            if (!c.Throttle.Ok) continue;
            var r = c.Throttle.Value;
            if ((r & ~ThrottleReasons.GpuIdle) == 0) continue;
            var l = r.IsThermal() ? CheckLevel.Warn : CheckLevel.Info;
            if (l > level) level = l;
            parts.Add($"{CardName(c)}: {r}");
        }
        return level == CheckLevel.Pass
            ? Pass("throttle", "No throttling")
            : new(level, "throttle", string.Join("; ", parts), ThrottleHint);
    }

    private static Check DriverErrors(Facts f)
    {
        if (!f.EventLog.Ok || f.EventLog.Value is not { } events)
            return new(CheckLevel.Info, "driver-errors", "Event log not available", EventLogUnavailableHint);
        if (events.Count == 0) return Pass("driver-errors", "No driver errors in the last 7 days");
        var newest = events.Max(e => e.At);
        var time = newest.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return new(CheckLevel.Warn, "driver-errors",
            $"{events.Count} driver error(s) in the last 7 days, newest {time}", DriverErrorsHint);
    }

    private static Check Settings(Facts f) => f.State.SettingsStatus switch
    {
        SettingsStatus.Loaded => Pass("settings", "Settings loaded"),
        SettingsStatus.Missing => new(CheckLevel.Info, "settings", "No settings file, using defaults", SettingsMissingHint),
        _ => new(CheckLevel.Warn, "settings", "Settings file was invalid and was reset", SettingsHint),
    };

    private static Check LogWritable(Facts f) => f.State.LogWritable
        ? Pass("log-writable", "Log folder is writable")
        : new(CheckLevel.Warn, "log-writable", "Log folder is not writable", LogWritableHint);

    private static Check PollTime(Facts f)
    {
        var total = f.State.Timings.Values.Sum(t => t.AvgMs);
        var text = total.ToString("0.#", CultureInfo.InvariantCulture);
        return total > PollWarnMs
            ? new(CheckLevel.Warn, "poll-time", $"A poll takes about {text} ms", PollTimeHint)
            : Pass("poll-time", $"A poll takes about {text} ms");
    }
}
