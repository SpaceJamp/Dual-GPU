using Xunit;

namespace GpuMonitor.Core.Tests;

public class SmokeTests
{
    [Fact]
    public void Smoke_CoreAssemblyLoads()
    {
        Assert.NotNull(typeof(GpuMonitor.Core.Paths).Assembly);
    }
}
