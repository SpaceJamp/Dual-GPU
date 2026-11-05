using GpuMonitor.Core.Readers;
using Xunit;

namespace GpuMonitor.Core.Tests;

public class UnitsTests
{
    [Fact] public void Units_MilliwattsToWatts() => Assert.Equal(5.0, Units.MilliwattsToWatts(5000));
    [Fact] public void Units_BytesToMiB_Exact() => Assert.Equal(1, Units.BytesToMiB(1048576));
    [Fact] public void Units_BytesToMiB_Floors() => Assert.Equal(0, Units.BytesToMiB(1048575));
    [Fact] public void Units_CudaVersion() => Assert.Equal("12.8", Units.CudaVersion(12080));
}
