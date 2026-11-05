using GpuMonitor.Core.Readers;
using Xunit;

namespace GpuMonitor.Core.Tests;

public class CallTimingsTests
{
    [Fact]
    public void CallTimings_P99()
    {
        var t = new CallTimings();
        for (var i = 1; i <= 100; i++) t.Record("X", TimeSpan.FromMilliseconds(i));
        var s = t.Snapshot()["X"];
        Assert.Equal(100, s.Count);
        Assert.Equal(99, s.P99Ms, 1.0);
        Assert.Equal(1, s.MinMs, 0.01);
        Assert.Equal(100, s.MaxMs, 0.01);
        Assert.Equal(50.5, s.AvgMs, 0.01);
    }

    [Fact]
    public void CallTimings_KeepsLast600()
    {
        var t = new CallTimings();
        for (var i = 1; i <= 700; i++) t.Record("X", TimeSpan.FromMilliseconds(i));
        var s = t.Snapshot()["X"];
        Assert.Equal(600, s.Count);
        Assert.Equal(101, s.MinMs, 0.01);
    }
}
