using GpuMonitor.App.Platform;
using GpuMonitor.Core.Logging;
using Xunit;

namespace GpuMonitor.App.Tests;

public class PlatformTests
{
    [Fact]
    public void CrashHandler_WritesTextAndKeeps3()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gpumon-crash-" + Guid.NewGuid().ToString("N"));
        try
        {
            var log = new AppLog(null, LogLevel.Debug, json: false);
            for (var i = 0; i < 50; i++) log.Info("test", "line " + i);
            CrashHandler.Install(log, dir);

            var written = new List<string>();
            for (var i = 0; i < 5; i++)
                written.Add(CrashHandler.WriteCrash(new InvalidOperationException("boom " + i)));

            Assert.Equal(5, written.Distinct().Count());
            var txt = Directory.GetFiles(dir, "crash-*.txt").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(3, txt.Length);
            Assert.Equal(written.Skip(2).Select(Path.GetFileName), txt);
            foreach (var f in written.Take(2)) Assert.False(File.Exists(f));

            var text = File.ReadAllText(written[^1]);
            Assert.Contains(log.RunId, text);
            Assert.Contains("System.InvalidOperationException: boom 4", text);
            Assert.Contains("line 49", text);
            Assert.Contains("line 10", text);
            Assert.DoesNotContain("line 9\n", text);
            Assert.Matches(@"crash-\d{8}-\d{6}-\d{3}\.txt$", written[^1]);

            var dmp = Directory.GetFiles(dir, "crash-*.dmp");
            if (OperatingSystem.IsWindows()) Assert.Equal(3, dmp.Length);
            else Assert.Empty(dmp);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NullPlatform_ReturnsEmpty()
    {
        IStartup startup = new NullPlatform();
        Assert.False(startup.IsEnabled());
        startup.Set(true);
        Assert.False(startup.IsEnabled());

        if (OperatingSystem.IsWindows()) return;
        var s = GpuMonitor.App.Platform.Platform.Sources();
        Assert.Empty(s.Wmi.Adapters());
        Assert.Empty(s.Events.Recent(TimeSpan.FromDays(7)));
        Assert.Empty(s.Registry.GpuPreferences());
        Assert.Null(s.Registry.RunValue());
        Assert.Null(s.Smi.QueryAll());
        Assert.Null(s.Win.HwSchMode());
        Assert.False(GpuMonitor.App.Platform.Platform.Startup().IsEnabled());
    }
}
