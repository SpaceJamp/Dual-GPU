using Xunit;

namespace GpuMonitor.App.Tests;

public class SingleInstanceTests
{
    private static string NewId() => "test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void SingleInstance_SecondSignalsFirst()
    {
        var id = NewId();
        using var shown = new SemaphoreSlim(0);
        using var first = SingleInstance.TryAcquire(id, () => shown.Release());
        Assert.NotNull(first);

        Assert.Null(SingleInstance.TryAcquire(id, () => { }));
        Assert.True(SingleInstance.SignalExisting(id));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        // The server keeps listening after the first request.
        Assert.True(SingleInstance.SignalExisting(id));
        Assert.True(shown.Wait(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void SignalExisting_NoServer_ReturnsFalse()
    {
        Assert.False(SingleInstance.SignalExisting(NewId()));
    }

    [Fact]
    public void Dispose_ReleasesNameAndStopsServer()
    {
        var id = NewId();
        var first = SingleInstance.TryAcquire(id, () => { });
        Assert.NotNull(first);
        first.Dispose();
        first.Dispose();

        Assert.False(SingleInstance.SignalExisting(id));
        using var again = SingleInstance.TryAcquire(id, () => { });
        Assert.NotNull(again);
    }
}
