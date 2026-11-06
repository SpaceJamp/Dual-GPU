using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using GpuMonitor.App.Notify;
using GpuMonitor.Core.Alerts;
using GpuMonitor.Core.Diagnostics;
using GpuMonitor.Core.Logging;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Readers;
using GpuMonitor.Core.Settings;
using GpuMonitor.Core.Tests;
using Xunit;
using static GpuMonitor.App.Tests.VmKit;

namespace GpuMonitor.App.Tests;

public class StartupTests
{
    private const string Bus1 = "00000000:01:00.0";
    private const string Bus2 = "00000000:02:00.0";

    private sealed class Boot : IDisposable
    {
        public string Dir { get; } = TempDir();
        public AppLog? Log { get; private set; }
        public Func<FakeNvml> Make { get; init; } = TwoCards;

        public Bootstrap Start()
        {
            Log = new AppLog(Path.Combine(Dir, "app.log"), LogLevel.Debug, false);
            return new Bootstrap(Log, Path.Combine(Dir, "settings.json"), Path.Combine(Dir, "rec"),
                () => new NvmlLoad(Make(), "nvml.dll", null), NullSources.All(), new FakeStartup(), TimeProvider.System);
        }

        public void Dispose() => Log?.Dispose();
    }

    private static FakeNvml TwoCards()
    {
        var fake = new FakeNvml();
        fake.Add(Bus1, "NVIDIA GeForce RTX 5080");
        fake.Add(Bus2, "NVIDIA GeForce RTX 4070 Ti SUPER");
        return fake;
    }

    private static FactCollector NoCards()
    {
        var fake = new FakeNvml();
        return new FactCollector(fake, NvmlReturn.Success, "nvml.dll", null, NullSources.All(),
            new AppState("1.0", "abc", ".NET", new AppSettings(), SettingsStatus.Missing, null, [],
                new Dictionary<string, TimingStats>(), [], true));
    }

    [Fact]
    public void Diagnose_WritesReportAndExitCode()
    {
        var o = new StringWriter();
        var code = Program.RunDiagnose(CommandLine.Parse(["--diagnose"]), NoCards(), o, new StringWriter());

        Assert.Contains("[FAIL] card-count", o.ToString());
        Assert.Equal(1, code);
    }

    [Fact]
    public void Diagnose_Json_ToFile()
    {
        var path = Path.Combine(TempDir(), "r.json");
        var o = new StringWriter();

        Program.RunDiagnose(CommandLine.Parse(["--diagnose", "--json", "-o", path]), NoCards(), o, new StringWriter());

        Assert.Equal("", o.ToString());
        Assert.StartsWith("{", File.ReadAllText(path));
    }

    [Fact]
    public void Diagnose_UnwritableOutput_Exit2()
    {
        var blocker = Path.Combine(TempDir(), "afile");
        File.WriteAllText(blocker, "x");
        var err = new StringWriter();

        var code = Program.RunDiagnose(CommandLine.Parse(["--diagnose", "-o", Path.Combine(blocker, "r.txt")]),
            NoCards(), new StringWriter(), err);

        Assert.Equal(2, code);
        Assert.Contains("Could not write", err.ToString());
    }

    [Fact]
    public void AppInfo_VersionHasNoCommitSuffix()
    {
        Assert.DoesNotContain("+", AppInfo.Version);
        Assert.NotEqual("", AppInfo.Version);
    }

    [Fact]
    public void Startup_LogsFacts()
    {
        using var boot = new Boot();
        using var b = boot.Start();

        var text = File.ReadAllText(Path.Combine(boot.Dir, "app.log"));
        Assert.Contains(boot.Log!.RunId, text);
        Assert.Contains("cards found: 2", text);
        Assert.Contains("driver 572.83", text);
        Assert.Contains("settings: Missing", text);
    }

    [Fact]
    public void Startup_AppliesLogSettings()
    {
        using var boot = new Boot();
        File.WriteAllText(Path.Combine(boot.Dir, "settings.json"), """{"LogLevel":"Error","LogJson":true}""");
        using var b = boot.Start();

        Assert.Equal(LogLevel.Error, boot.Log!.Level);
        Assert.True(boot.Log.Json);
    }

    [Fact]
    public void Collector_SeesCardsAndAlerts()
    {
        using var boot = new Boot();
        using var b = boot.Start();
        b.NoteAlerts([new Alert(AlertKind.Hot, Bus1, "RTX 5080", 85, T0)]);

        var facts = b.Collector().Collect();

        Assert.Equal(2, facts.Cards.Count);
        Assert.Single(facts.State.Alerts);
        Assert.Equal(AppInfo.Version, facts.App.Version);
    }

    private sealed class FakeNotifier : INotifier
    {
        public List<string> Shown { get; } = new();
        public bool Show(string title, string message) { Shown.Add(title); return true; }
    }

    private static (Shell shell, FakeNotifier notifier, List<int> exits) MakeShell(Bootstrap b)
    {
        var notifier = new FakeNotifier();
        var exits = new List<int>();
        return (new Shell(Application.Current!, b, _ => notifier, () => exits.Add(0)), notifier, exits);
    }

    [AvaloniaFact]
    public void Shell_AppliesPoll()
    {
        using var boot = new Boot();
        using var b = boot.Start();
        var (shell, notifier, _) = MakeShell(b);
        var poll = Poll(T0, (Bus1, "NVIDIA GeForce RTX 5080", true), (Bus2, "NVIDIA GeForce RTX 4070 Ti SUPER", false))
            with { Alerts = [new Alert(AlertKind.Hot, Bus1, "RTX 5080", 85, T0) { Limit = 80 }] };

        shell.Apply(poll);

        Assert.Equal(2, shell.ViewModel.Cards.Count);
        Assert.Equal("GPU Monitor: 5080 50°C, 4070 Ti SUPER 50°C", shell.Tray.ToolTipText);
        Assert.Equal(["RTX 5080 is hot"], notifier.Shown);
        Assert.Single(b.Collector().Collect().State.Alerts);
    }

    [AvaloniaFact]
    public void Shell_StartHidden_OpensOnRequest()
    {
        using var boot = new Boot();
        using var b = boot.Start();
        var (shell, _, _) = MakeShell(b);

        shell.Start(hidden: true);
        Assert.False(shell.Window.IsVisible);

        shell.Open();
        Assert.True(shell.Window.IsVisible);
        shell.Exit();
    }

    [AvaloniaFact]
    public void Shell_Start_ShowsWindow()
    {
        using var boot = new Boot();
        using var b = boot.Start();
        var (shell, _, _) = MakeShell(b);

        shell.Start(hidden: false);

        Assert.True(shell.Window.IsVisible);
        shell.Exit();
    }

    [AvaloniaFact]
    public void Shell_OpenSettings_SelectsTab()
    {
        using var boot = new Boot();
        using var b = boot.Start();
        var (shell, _, _) = MakeShell(b);
        shell.Start(hidden: true);

        shell.OpenSettings();

        var tabs = shell.Window.GetLogicalDescendants().OfType<TabControl>().Single();
        Assert.Equal("Settings", ((TabItem)tabs.SelectedItem!).Header);
        Assert.True(shell.Window.IsVisible);
        shell.Exit();
    }

    [AvaloniaFact]
    public void Shell_CreateReport_FillsDiagnosticsTab()
    {
        using var boot = new Boot();
        using var b = boot.Start();
        var (shell, _, _) = MakeShell(b);
        shell.Start(hidden: true);

        shell.CreateReport();

        var tabs = shell.Window.GetLogicalDescendants().OfType<TabControl>().Single();
        Assert.Equal("Diagnostics", ((TabItem)tabs.SelectedItem!).Header);
        var until = DateTime.UtcNow.AddSeconds(5);
        while (shell.ViewModel.Diagnostics.ReportText.Length == 0 && DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Assert.Contains("GPU Monitor diagnostic report", shell.ViewModel.Diagnostics.ReportText);
        shell.Exit();
    }

    [AvaloniaFact]
    public void Shell_Exit_QuitsEvenWhenCleanupFails()
    {
        using var boot = new Boot
        {
            Make = () => { var f = TwoCards(); f.ShutdownThrows = true; return f; },
        };
        using var b = boot.Start();
        var (shell, _, exits) = MakeShell(b);
        shell.Start(hidden: false);
        var closed = false;
        shell.Window.Closed += (_, _) => closed = true;

        shell.Exit();

        Assert.True(closed);
        Assert.Equal([0], exits);
        Assert.Contains("shutdown failed", File.ReadAllText(Path.Combine(boot.Dir, "app.log")));
    }

    [AvaloniaFact]
    public void Shell_Exit_ClosesAndStopsRecording()
    {
        using var boot = new Boot();
        using var b = boot.Start();
        var (shell, _, exits) = MakeShell(b);
        shell.Start(hidden: false);
        b.Recorder.Start(T0);
        var closed = false;
        shell.Window.Closed += (_, _) => closed = true;

        shell.Exit();

        Assert.True(closed);
        Assert.False(b.Recorder.IsRecording);
        Assert.Equal([0], exits);
    }
}
