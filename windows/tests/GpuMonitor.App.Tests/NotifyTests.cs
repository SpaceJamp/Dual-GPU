using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using GpuMonitor.App.Notify;
using GpuMonitor.Core.Alerts;
using GpuMonitor.Core.Logging;
using Xunit;

namespace GpuMonitor.App.Tests;

public class NotifyTests
{
    internal sealed class FakeNotifier(bool result) : INotifier
    {
        public bool Result { get; set; } = result;
        public List<(string Title, string Message)> Shown { get; } = new();

        public bool Show(string title, string message)
        {
            Shown.Add((title, message));
            return Result;
        }
    }

    private static (AppLog Log, List<LogLine> Lines) Log()
    {
        var log = new AppLog(null, LogLevel.Debug, json: false);
        var lines = new List<LogLine>();
        log.Line += lines.Add;
        return (log, lines);
    }

    [Fact]
    public void Fallback_UsedWhenPrimaryFails()
    {
        var (log, lines) = Log();
        var primary = new FakeNotifier(false);
        var fallback = new FakeNotifier(true);
        var n = new FallbackNotifier(primary, fallback, log);

        Assert.True(n.Show("t1", "m1"));
        Assert.True(n.Show("t2", "m2"));

        Assert.Equal(2, primary.Shown.Count);
        Assert.Equal([("t1", "m1"), ("t2", "m2")], fallback.Shown);
        var warn = Assert.Single(lines, l => l.Level == LogLevel.Warning);
        Assert.Equal("notify", warn.Source);
    }

    [Fact]
    public void Fallback_NotUsedWhenPrimaryWorks()
    {
        var (log, lines) = Log();
        var primary = new FakeNotifier(true);
        var fallback = new FakeNotifier(true);

        Assert.True(new FallbackNotifier(primary, fallback, log).Show("t", "m"));

        Assert.Single(primary.Shown);
        Assert.Empty(fallback.Shown);
        Assert.DoesNotContain(lines, l => l.Level == LogLevel.Warning);
    }

    [Fact]
    public void Fallback_ReturnsFallbackResult()
    {
        var (log, _) = Log();
        Assert.False(new FallbackNotifier(new FakeNotifier(false), new FakeNotifier(false), log).Show("t", "m"));
    }

    [Fact]
    public void AlertDispatcher_SendsOnlyNotifyAlerts()
    {
        var n = new FakeNotifier(true);
        var at = VmKit.T0;
        var hot = new Alert(AlertKind.Hot, "b1", "RTX 5080", 85, at) { Limit = 80 };
        var cooled = new Alert(AlertKind.HotCleared, "b1", "RTX 5080", 70, at) { Limit = 80 };
        var missing = new Alert(AlertKind.CardMissing, "b2", "RTX 4070 Ti SUPER", null, at);

        new AlertDispatcher(n).Dispatch([hot, cooled, missing]);

        Assert.Equal([(hot.Title, hot.Message), (missing.Title, missing.Message)], n.Shown);
    }

    [Fact]
    public void AlertDispatcher_EmptyList_ShowsNothing()
    {
        var n = new FakeNotifier(true);
        new AlertDispatcher(n).Dispatch(Array.Empty<Alert>());
        Assert.Empty(n.Shown);
    }

    [AvaloniaFact]
    public void InApp_NoHostOrHidden_ReturnsFalse()
    {
        Assert.False(new InAppNotifier(() => null).Show("t", "m"));

        var hidden = new Window();
        Assert.False(new InAppNotifier(() => hidden).Show("t", "m"));
    }

    [AvaloniaFact]
    public void InApp_VisibleHost_Shows()
    {
        var window = new Window { Width = 400, Height = 300 };
        window.Show();
        Window? host = window;
        var n = new InAppNotifier(() => host);

        Assert.True(n.Show("t", "m"));
        Assert.True(n.Show("t2", "m2"));

        // The window is recreated: the notifier follows the new one.
        window.Close();
        var second = new Window { Width = 400, Height = 300 };
        second.Show();
        host = second;
        Assert.True(n.Show("t3", "m3"));
        second.Close();
    }

    [Fact]
    public void Platform_NotifierOffWindows_IsInApp()
    {
        if (OperatingSystem.IsWindows()) return;
        var n = GpuMonitor.App.Platform.Platform.Notifier(AppLog.Null, () => null);
        Assert.IsType<InAppNotifier>(n);
    }
}
