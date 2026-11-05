using System;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;
using Xunit;

namespace GpuMonitor.Core.Tests;

public class ModelTests
{
    [Fact]
    public void NvmlReturnName_KnownCodes()
    {
        Assert.Equal("NVML_ERROR_GPU_IS_LOST", NvmlReturn.GpuIsLost.NvmlReturnName());
        Assert.Equal("NVML_ERROR_NOT_SUPPORTED", NvmlReturn.NotSupported.NvmlReturnName());
        Assert.Equal("NVML_SUCCESS", NvmlReturn.Success.NvmlReturnName());
        Assert.Equal("NVML_ERROR_12345", ((NvmlReturn)12345).NvmlReturnName());
    }

    [Theory]
    [InlineData(NvmlReturn.IrqIssue, "NVML_ERROR_IRQ_ISSUE")]
    [InlineData(NvmlReturn.VgpuEccNotSupported, "NVML_ERROR_VGPU_ECC_NOT_SUPPORTED")]
    [InlineData(NvmlReturn.LibRmVersionMismatch, "NVML_ERROR_LIB_RM_VERSION_MISMATCH")]
    [InlineData(NvmlReturn.Unknown, "NVML_ERROR_UNKNOWN")]
    public void NvmlReturnName_MultiWord(NvmlReturn r, string expected) =>
        Assert.Equal(expected, r.NvmlReturnName());

    [Fact]
    public void Field_FailIsNotOk()
    {
        Assert.False(Field<int>.Fail(NvmlReturn.NotSupported).Ok);
        Assert.True(Field<int>.Of(5).Ok);
        Assert.False(default(Field<int>).Ok);
    }

    private static Reading Make(Field<long> used, Field<long> total) => new(
        "00000000:01:00.0", DateTimeOffset.UnixEpoch,
        Field<int>.Of(0), used, total, Field<int>.Of(0),
        Field<double>.Of(0), Field<double>.Of(0),
        Field<int>.Of(0), Field<int>.Of(0), Field<int>.Of(0),
        Field<int>.Of(0), Field<int>.Of(0),
        Field<ThrottleReasons>.Of(0), Field<bool>.Of(false));

    [Fact]
    public void VramPct_NullWhenTotalZero()
    {
        Assert.Null(Make(Field<long>.Of(10), Field<long>.Of(0)).VramPct);
        Assert.Null(Make(Field<long>.Fail(NvmlReturn.Unknown), Field<long>.Of(100)).VramPct);
    }

    [Fact]
    public void VramPct_Computes()
    {
        var pct = Make(Field<long>.Of(14745), Field<long>.Of(16384)).VramPct;
        Assert.NotNull(pct);
        Assert.Equal(90.0, pct!.Value, 0.01);
    }

    [Fact]
    public void IsThermal_Flags()
    {
        Assert.True(ThrottleReasons.HwThermalSlowdown.IsThermal());
        Assert.False(ThrottleReasons.SwPowerCap.IsThermal());
    }
}
