using Xunit;
using GpuMonitor.Core.Poll;

namespace GpuMonitor.Core.Tests;

public sealed class HistoryTests
{
    [Fact]
    public void History_Keeps60()
    {
        var h = new History();
        for (int i = 0; i < 75; i++) h.Add(Make.Reading("a", Make.T0.AddSeconds(i)), 1.0);
        var list = h.For("a");
        Assert.Equal(60, list.Count);
        Assert.Equal(Make.T0.AddSeconds(15), list[0]!.At);
        Assert.Equal(Make.T0.AddSeconds(74), list[^1]!.At);
    }

    [Fact]
    public void GapLongerThanThreeIntervals_InsertsBreak()
    {
        var h = new History();
        h.Add(Make.Reading("a", Make.T0), 1.0);
        h.Add(Make.Reading("a", Make.T0.AddSeconds(3)), 1.0);
        h.Add(Make.Reading("a", Make.T0.AddSeconds(3).AddSeconds(3.5)), 1.0);
        var list = h.For("a");
        Assert.Equal(4, list.Count);
        Assert.NotNull(list[1]);
        Assert.Null(list[2]);
    }

    [Fact]
    public void UnknownBus_Empty() => Assert.Empty(new History().For("nope"));

    [Fact]
    public void BreaksCountTowardCapacity()
    {
        var h = new History(3);
        h.Add(Make.Reading("a", Make.T0), 1.0);
        h.Add(Make.Reading("a", Make.T0.AddSeconds(10)), 1.0);
        h.Add(Make.Reading("a", Make.T0.AddSeconds(11)), 1.0);
        var list = h.For("a");
        Assert.Equal(3, list.Count);
        Assert.Null(list[0]);
    }
}
