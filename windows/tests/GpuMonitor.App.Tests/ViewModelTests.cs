using Avalonia.Headless.XUnit;
using GpuMonitor.App.ViewModels;
using GpuMonitor.Core.Logging;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Poll;
using GpuMonitor.Core.Settings;
using Xunit;
using static GpuMonitor.App.Tests.VmKit;

namespace GpuMonitor.App.Tests;

public class ViewModelTests
{
    private const string Bus1 = "00000000:01:00.0";
    private const string Bus2 = "00000000:02:00.0";

    [Fact]
    public void CardViewModel_StaleAfter5s()
    {
        var vm = new CardViewModel { BusId = Bus1 };
        var r = Reading(Bus1, T0, temp: 61, load: 33);
        vm.Update(r, new Reading?[] { r }, T0);

        Assert.False(vm.IsStale);
        Assert.Equal(T0, vm.LastSeen);
        Assert.Equal(61, vm.TempC);

        vm.Update(null, new Reading?[] { r }, T0.AddSeconds(4.9));
        Assert.False(vm.IsStale);

        vm.Update(null, new Reading?[] { r }, T0.AddSeconds(5));
        Assert.True(vm.IsStale);
        Assert.Equal(T0, vm.LastSeen);
        Assert.Equal(61, vm.TempC);
        Assert.Equal(33, vm.LoadPct);

        vm.Update(Reading(Bus1, T0.AddSeconds(6)), new Reading?[] { r }, T0.AddSeconds(6));
        Assert.False(vm.IsStale);
    }

    [Fact]
    public void CardViewModel_NewListsAndHistoryMapping()
    {
        var vm = new CardViewModel { BusId = Bus1 };
        var hot = Reading(Bus1, T0, temp: 70, load: 20);
        var failed = hot with { TempC = Field<int>.Fail(NvmlReturn.NotSupported) };
        var history = new Reading?[] { hot, null, failed };

        vm.Update(hot, history, T0);
        var temp = vm.TempHistory;
        var load = vm.LoadHistory;
        var fields = vm.Fields;
        Assert.Equal(new double?[] { 70, null, null }, temp);
        Assert.Equal(new double?[] { 20, null, 20 }, load);

        vm.Update(null, history, T0.AddSeconds(1));
        Assert.NotSame(temp, vm.TempHistory);
        Assert.NotSame(load, vm.LoadHistory);
        Assert.NotSame(fields, vm.Fields);
        Assert.Equal(temp, vm.TempHistory);
    }

    [Fact]
    public void CardViewModel_FieldsListEveryReadingField()
    {
        var vm = new CardViewModel { BusId = Bus1 };
        var r = Reading(Bus1, T0) with { FanPct = Field<int>.Fail(NvmlReturn.NotSupported) };
        vm.Update(r, new Reading?[] { r }, T0);

        Assert.Equal(
            new[] { "LoadPct", "VramUsedMiB", "VramTotalMiB", "TempC", "PowerW", "PowerLimitW", "GraphicsMHz",
                "MemoryMHz", "SmMHz", "FanPct", "PState", "Throttle", "DisplayActive" },
            vm.Fields.Select(f => f.Name));
        Assert.Equal(new FieldRow("PowerW", "120.5", "NVML_SUCCESS"), vm.Fields.Single(f => f.Name == "PowerW"));
        Assert.Equal(new FieldRow("FanPct", "", "NVML_ERROR_NOT_SUPPORTED"), vm.Fields.Single(f => f.Name == "FanPct"));
        Assert.Equal("false", vm.Fields.Single(f => f.Name == "DisplayActive").Value);
    }

    [AvaloniaFact]
    public void MainViewModel_OrdersByBusId()
    {
        var vm = MainVm(TempDir());
        vm.OnPolled(Poll(T0, (Bus2, "RTX 4070 Ti SUPER", false), (Bus1, "RTX 5080", true)));

        Assert.Equal(new[] { Bus1, Bus2 }, vm.Cards.Select(c => c.BusId));
        Assert.True(vm.Cards[0].IsDisplay);
        Assert.False(vm.Cards[1].IsDisplay);
        Assert.Equal("RTX 5080", vm.Cards[0].Name);
    }

    [AvaloniaFact]
    public void MainViewModel_NewCardAdded()
    {
        var vm = MainVm(TempDir());
        vm.OnPolled(Poll(T0, (Bus2, "B", false)));
        var first = vm.Cards[0];
        var moves = 0;
        vm.Cards.CollectionChanged += (_, e) =>
        {
            if (e.Action != System.Collections.Specialized.NotifyCollectionChangedAction.Add) moves++;
        };

        vm.OnPolled(Poll(T0.AddSeconds(1), (Bus1, "A", false), (Bus2, "B", false)));

        Assert.Equal(new[] { Bus1, Bus2 }, vm.Cards.Select(c => c.BusId));
        Assert.Same(first, vm.Cards[1]);
        Assert.Equal(0, moves);

        // A card that stops reporting stays and goes stale.
        vm.OnPolled(Poll(T0.AddSeconds(7), (Bus1, "A", false)));
        Assert.Equal(2, vm.Cards.Count);
        Assert.True(vm.Cards[1].IsStale);
        Assert.False(vm.Cards[0].IsStale);
    }

    [AvaloniaFact]
    public void MainViewModel_UsesHistory()
    {
        var history = new History();
        var vm = MainVm(TempDir(), history: history);
        var r = Reading(Bus1, T0, temp: 55);
        history.Add(r, 1.0);
        vm.OnPolled(Poll(T0, (Bus1, "A", false)) with { Readings = new[] { r } });

        Assert.Equal(new double?[] { 55 }, vm.Cards[0].TempHistory);
    }

    [AvaloniaFact]
    public void MainViewModel_Banner()
    {
        var ok = MainVm(TempDir());
        Assert.Equal("", ok.Banner);

        var failed = MainVm(TempDir(), NvmlReturn.LibraryNotFound, "nvml.dll not found");
        Assert.Equal("NVML did not load: nvml.dll not found. See the Diagnostics tab.", failed.Banner);

        var noReason = MainVm(TempDir(), NvmlReturn.DriverNotLoaded);
        Assert.Equal("NVML did not load: NVML_ERROR_DRIVER_NOT_LOADED. See the Diagnostics tab.", noReason.Banner);

        var dir = TempDir();
        var blocker = Path.Combine(dir, "afile");
        File.WriteAllText(blocker, "x");
        using var log = new AppLog(Path.Combine(blocker, "log.log"), LogLevel.Info, false);
        log.Info("test", "hello");
        Assert.NotNull(log.LastWriteError);
        var both = MainVm(dir, NvmlReturn.LibraryNotFound, "gone", log);
        both.OnPolled(Poll(T0));
        Assert.Equal(
            "NVML did not load: gone. See the Diagnostics tab.\nLog file can't be written: " + log.LastWriteError,
            both.Banner);
    }

    [AvaloniaFact]
    public void MainViewModel_HeatLimitFollowsSettings()
    {
        var vm = MainVm(TempDir());
        Assert.Equal(80, vm.HeatLimitC);
        vm.Settings.HeatLimitC = 72;
        vm.Settings.Save();
        Assert.Equal(72, vm.HeatLimitC);
    }

    [Fact]
    public void Settings_SaveClampsAndAppliesLogLevel()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "settings.json");
        var log = new AppLog(null, LogLevel.Info, false);
        var startup = new FakeStartup();
        var current = new CurrentSettings(new AppSettings());
        Func<AppSettings> pollerReads = current.Get;
        var vm = new SettingsViewModel(new SettingsStore(path, log), startup, log, current);
        AppSettings? saved = null;
        vm.Saved += s => saved = s;

        vm.RefreshSeconds = 9;
        vm.HeatLimitC = 20;
        vm.VramLimitPct = 100;
        vm.LogLevel = LogLevel.Debug;
        vm.LogJson = true;
        vm.StartWithWindows = true;
        vm.ExpectedDisplayCard = "  ";
        vm.Save();

        var expected = new AppSettings
        {
            RefreshSeconds = 5.0, HeatLimitC = 50, VramLimitPct = 99, LogLevel = LogLevel.Debug, LogJson = true,
            StartWithWindows = true, ExpectedDisplayCard = AppSettings.DefaultExpectedDisplayCard,
        };
        Assert.Equal(expected, saved);
        Assert.Equal(expected, pollerReads());
        Assert.Equal(expected, new SettingsStore(path, log).Load().Settings);
        Assert.Equal(LogLevel.Debug, log.Level);
        Assert.True(log.Json);
        Assert.Equal(new[] { true }, startup.Calls);
        Assert.Equal(5.0, vm.RefreshSeconds);
        Assert.Equal(50, vm.HeatLimitC);
        Assert.Equal(AppSettings.DefaultExpectedDisplayCard, vm.ExpectedDisplayCard);
    }

    [AvaloniaFact]
    public void Diagnostics_LogFilter()
    {
        var log = new AppLog(null, LogLevel.Debug, false);
        var vm = new DiagnosticsViewModel(log, () => Collector(log), new Recorder(TempDir()));
        log.Debug("t", "d1");
        log.Info("t", "i1");
        log.Warn("t", "w1");
        log.Error("t", "e1");
        Assert.Equal(4, vm.LogLines.Count);

        vm.MinLevel = LogLevel.Warning;
        Assert.Equal(2, vm.LogLines.Count);
        Assert.Contains("w1", vm.LogLines[0]);
        Assert.Contains("e1", vm.LogLines[1]);

        log.Info("t", "i2");
        log.Error("t", "e2");
        Assert.Equal(3, vm.LogLines.Count);

        vm.MinLevel = LogLevel.Debug;
        Assert.Equal(6, vm.LogLines.Count);
        Assert.Contains("i2", vm.LogLines[4]);
    }

    [AvaloniaFact]
    public void Diagnostics_LogCappedAt500()
    {
        var log = new AppLog(null, LogLevel.Debug, false);
        var vm = new DiagnosticsViewModel(log, () => Collector(log), new Recorder(TempDir()));
        for (var i = 0; i < 600; i++) log.Info("t", "line " + i);

        Assert.Equal(500, vm.LogLines.Count);
        Assert.EndsWith("line 100", vm.LogLines[0]);
        Assert.EndsWith("line 599", vm.LogLines[^1]);

        vm.MinLevel = LogLevel.Warning;
        vm.MinLevel = LogLevel.Debug;
        Assert.Equal(500, vm.LogLines.Count);
        Assert.EndsWith("line 100", vm.LogLines[0]);
    }

    [AvaloniaFact]
    public async Task Diagnostics_LineFromOtherThreadIsPosted()
    {
        var log = new AppLog(null, LogLevel.Debug, false);
        var vm = new DiagnosticsViewModel(log, () => Collector(log), new Recorder(TempDir()));
        await Task.Run(() => log.Info("t", "from worker"));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.Contains(vm.LogLines, l => l.EndsWith("from worker"));
    }

    [AvaloniaFact]
    public async Task Diagnostics_ReportCopySaveAndFolder()
    {
        var dir = TempDir();
        var log = new AppLog(null, LogLevel.Debug, false);
        string? copied = null;
        string? opened = null;
        var vm = new DiagnosticsViewModel(log, () => Collector(log), new Recorder(Path.Combine(dir, "rec")),
            copyText: t => { copied = t; return Task.CompletedTask; },
            openFolder: d => opened = d, logDir: Path.Combine(dir, "logs"));

        var text = vm.CreateReport();
        Assert.StartsWith("GPU Monitor diagnostic report", text);
        Assert.Contains("nvml.dll not found", text);
        Assert.Equal(text, vm.ReportText);
        Assert.Contains(vm.Timings, t => t.StartsWith("GetTemperature:"));
        Assert.Equal(new[] { "none" }, vm.Events);

        await vm.CopyReport();
        Assert.Equal(text, copied);

        var file = Path.Combine(dir, "report.txt");
        vm.SaveReport(file);
        Assert.Equal(text, File.ReadAllText(file));

        vm.OpenLogFolder();
        Assert.Equal(Path.Combine(dir, "logs"), opened);
    }

    [AvaloniaFact]
    public void Diagnostics_ToggleRecording()
    {
        var dir = TempDir();
        var rec = Path.Combine(dir, "rec");
        var vm = MainVm(dir);
        Assert.False(vm.Diagnostics.IsRecording);

        vm.Diagnostics.ToggleRecording();
        Assert.True(vm.Diagnostics.IsRecording);
        vm.OnPolled(Poll(T0, (Bus1, "A", true)));
        vm.Diagnostics.ToggleRecording();
        Assert.False(vm.Diagnostics.IsRecording);

        var lines = File.ReadAllLines(Directory.GetFiles(rec).Single());
        Assert.Equal(2, lines.Length);
        Assert.Contains(Bus1, lines[1]);
    }
}
