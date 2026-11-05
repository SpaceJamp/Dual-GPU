using System.Runtime.InteropServices;
using GpuMonitor.Core.Nvml;
using Xunit;

namespace GpuMonitor.Core.Tests;

public class NvmlApiTests
{
    [Fact]
    public void TryLoad_NoDriver_ReturnsFalseWithError()
    {
        // This container has no NVIDIA driver.
        var ok = NvmlApi.TryLoad(out var api, out var error);

        Assert.False(ok);
        Assert.Null(api);
        Assert.False(string.IsNullOrEmpty(error));
        foreach (var candidate in NvmlNative.Candidates())
            Assert.Contains(candidate, error);
        Assert.Contains(OperatingSystem.IsWindows() ? "nvml.dll" : "libnvidia-ml.so.1", error);
    }

    [Fact]
    public void Struct_Layouts()
    {
        // Sizes and offsets from nvml.h (NVML 13.4.92, NVIDIA/go-nvml pkg/nvml/nvml.h).
        // nvmlPciInfo_t: char busIdLegacy[16]; uint domain, bus, device, pciDeviceId, pciSubSystemId; char busId[32].
        Assert.Equal(68, Marshal.SizeOf<NvmlNative.PciInfo>());
        Assert.Equal(16, (int)Marshal.OffsetOf<NvmlNative.PciInfo>(nameof(NvmlNative.PciInfo.Domain)));
        Assert.Equal(36, (int)Marshal.OffsetOf<NvmlNative.PciInfo>(nameof(NvmlNative.PciInfo.BusId)));
        // nvmlMemory_t: unsigned long long total, free, used.
        Assert.Equal(24, Marshal.SizeOf<NvmlNative.Memory>());
        // nvmlUtilization_t: unsigned int gpu, memory.
        Assert.Equal(8, Marshal.SizeOf<NvmlNative.Utilization>());
        // nvmlProcessInfo_t (= nvmlProcessInfo_v2_t): uint pid; ulong usedGpuMemory; uint gpuInstanceId, computeInstanceId.
        Assert.Equal(24, Marshal.SizeOf<NvmlNative.ProcessInfo>());
        Assert.Equal(8, (int)Marshal.OffsetOf<NvmlNative.ProcessInfo>(nameof(NvmlNative.ProcessInfo.UsedGpuMemory)));
        Assert.Equal(16, (int)Marshal.OffsetOf<NvmlNative.ProcessInfo>(nameof(NvmlNative.ProcessInfo.GpuInstanceId)));
    }

    [Fact]
    public void MissingExports_ReturnFunctionNotFound()
    {
        // libc loads but exports none of the NVML entry points.
        var lib = NativeLibrary.Load(OperatingSystem.IsWindows() ? "kernel32.dll" : "libc.so.6");
        INvml api = new NvmlApi(new NvmlNative(lib), "test-library");

        Assert.Equal(NvmlReturn.FunctionNotFound, api.Init());
        Assert.Equal(NvmlReturn.FunctionNotFound, api.Shutdown());
        Assert.Equal(NvmlReturn.FunctionNotFound, api.GetDeviceCount(out _));
        Assert.Equal(NvmlReturn.FunctionNotFound, api.GetName(1, out var name));
        Assert.Equal("", name);
        Assert.Equal(NvmlReturn.FunctionNotFound, api.GetBusId(1, out var busId));
        Assert.Equal("", busId);
        Assert.Equal(NvmlReturn.FunctionNotFound, api.GetThrottleReasons(1, out _));
        Assert.Equal(NvmlReturn.FunctionNotFound, api.GetPcieGen(1, out _, out _));
        Assert.Equal(NvmlReturn.FunctionNotFound, api.GetProcesses(1, out var procs));
        Assert.Empty(procs);
        Assert.Equal(NvmlReturn.FunctionNotFound, api.GetDriverVersion(out var driver));
        Assert.Equal("", driver);
        Assert.Equal("test-library", ((NvmlApi)api).LoadedPath);
    }

    [Fact]
    public void BusIdOf_ReadsBusIdNotLegacy()
    {
        var pci = new NvmlNative.PciInfo();
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref pci, 1));
        "0000:01:00.0"u8.CopyTo(bytes);   // busIdLegacy at offset 0
        "00000000:01:00.0"u8.CopyTo(bytes[(int)Marshal.OffsetOf<NvmlNative.PciInfo>(nameof(NvmlNative.PciInfo.BusId))..]);

        Assert.Equal("00000000:01:00.0", NvmlApi.BusIdOf(pci));
    }

    [Theory]
    [InlineData(new byte[] { 0x41, 0x42, 0, 0x43 }, "AB")]
    [InlineData(new byte[] { 0, 0x41 }, "")]
    [InlineData(new byte[] { 0x41, 0x42 }, "AB")]
    [InlineData(new byte[] { 0xC2, 0xB0, 0x43, 0 }, "°C")]
    public void Decode_StopsAtFirstNul(byte[] buffer, string expected)
    {
        Assert.Equal(expected, NvmlApi.Decode(buffer));
    }
}
