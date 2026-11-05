using System.Runtime.Versioning;
using GpuMonitor.App.Platform.Windows;
using Xunit;

namespace GpuMonitor.App.Tests.Windows;

[SupportedOSPlatform("windows")]
public class WindowsPlatformTests
{
    [Fact]
    public void Wmi_ListsAdapters()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");
        var adapters = new WmiSource().Adapters();
        Assert.NotEmpty(adapters);
        Assert.All(adapters, a => Assert.False(string.IsNullOrEmpty(a.Name)));
    }

    [Fact]
    public void EventLog_QueryRuns()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");
        var events = new EventLogSource().Recent(TimeSpan.FromDays(7));
        Assert.True(events.Count <= 100);
        for (var i = 1; i < events.Count; i++) Assert.True(events[i - 1].At >= events[i].At);
    }

    [Fact]
    public void RunKey_RoundTrip()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");
        var startup = new RunKeyStartup(@"C:\Test\GpuMonitor.exe", "GpuMonitorTest");
        try
        {
            startup.Set(true);
            Assert.True(startup.IsEnabled());
            Assert.Equal("\"C:\\Test\\GpuMonitor.exe\" --tray", startup.Value());
            startup.Set(false);
            Assert.False(startup.IsEnabled());
            Assert.Null(startup.Value());
        }
        finally
        {
            startup.Set(false);
        }
    }

    [Fact]
    public void Registry_VersionHasBuild()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");
        Assert.Matches(@"^\d+\.\d+\.\d+(\.\d+)?$", new RegistrySource().Version());
    }
}
