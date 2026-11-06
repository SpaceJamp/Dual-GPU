using System.Runtime.Versioning;
using GpuMonitor.App.Platform.Windows;
using Xunit;

namespace GpuMonitor.App.Tests.Windows;

[SupportedOSPlatform("windows")]
public class WindowsPlatformTests
{
    [Fact]
    public void MiniDump_ViaHelper_WritesDump()
    {
        var path = Path.Combine(VmKit.TempDir(), "x.dmp");

        Assert.Null(MiniDump.WriteViaHelper(CrashHandler.DumpHelper!, path));

        Assert.True(new FileInfo(path).Length > 0);
    }

    [Fact]
    public void MiniDump_ViaHelper_MissingHelper_ReportsError()
    {
        var error = MiniDump.WriteViaHelper(Path.Combine(VmKit.TempDir(), "nope.exe"), Path.Combine(VmKit.TempDir(), "x.dmp"));

        Assert.NotNull(error);
    }

    [Fact]
    public void MiniDump_ViaHelper_DeadProcess_ReportsExitCode()
    {
        var error = MiniDump.WriteViaHelper(CrashHandler.DumpHelper!, Path.Combine(VmKit.TempDir(), "x.dmp"), processId: int.MaxValue);

        Assert.Contains("exit code", error);
    }

    [Fact]
    public void MiniDump_Retry_SucceedsOnThirdAttempt()
    {
        var calls = 0;

        Assert.True(MiniDump.Retry(() => ++calls == 3));

        Assert.Equal(3, calls);
    }

    [Fact]
    public void MiniDump_Retry_GivesUpAfterThree()
    {
        var calls = 0;

        Assert.False(MiniDump.Retry(() => { calls++; return false; }));

        Assert.Equal(3, calls);
    }

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
    public void EventLog_XPathMatchesBrief()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");
        Assert.Equal(
            "*[System[(Provider[@Name='nvlddmkm'] or (Provider[@Name='Display'] and EventID=4101)) and TimeCreated[timediff(@SystemTime) <= 604800000]]]",
            EventLogSource.XPath(TimeSpan.FromDays(7)));
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
