namespace GpuMonitor.Core.Nvml;

/// <summary>Raw NVML surface in NVML units. Every call returns an NvmlReturn and an out value.</summary>
public interface INvml
{
    NvmlReturn Init();
    NvmlReturn Shutdown();
    NvmlReturn GetDeviceCount(out uint count);
    NvmlReturn GetHandle(uint index, out nint handle);
    NvmlReturn GetName(nint handle, out string name);
    NvmlReturn GetUuid(nint handle, out string uuid);
    NvmlReturn GetBusId(nint handle, out string busId);
    NvmlReturn GetVbios(nint handle, out string version);
    NvmlReturn GetUtilization(nint handle, out uint gpuPct);
    NvmlReturn GetMemory(nint handle, out ulong usedBytes, out ulong totalBytes);
    NvmlReturn GetTemperature(nint handle, out uint c);
    /// <param name="kind">0 shutdown, 1 slowdown.</param>
    NvmlReturn GetTempThreshold(nint handle, int kind, out uint c);
    NvmlReturn GetPowerUsage(nint handle, out uint mW);
    NvmlReturn GetEnforcedPowerLimit(nint handle, out uint mW);
    NvmlReturn GetDefaultPowerLimit(nint handle, out uint mW);
    /// <param name="type">0 graphics, 1 SM, 2 memory.</param>
    NvmlReturn GetClock(nint handle, int type, out uint mhz);
    /// <param name="type">0 graphics, 1 SM, 2 memory.</param>
    NvmlReturn GetMaxClock(nint handle, int type, out uint mhz);
    NvmlReturn GetFanSpeed(nint handle, out uint pct);
    NvmlReturn GetPState(nint handle, out int pstate);
    NvmlReturn GetThrottleReasons(nint handle, out ulong reasons);
    NvmlReturn GetPcieGen(nint handle, out uint current, out uint max);
    NvmlReturn GetPcieWidth(nint handle, out uint current, out uint max);
    NvmlReturn GetDisplayActive(nint handle, out bool active);
    NvmlReturn GetProcesses(nint handle, out IReadOnlyList<(uint Pid, ulong UsedBytes)> processes);
    NvmlReturn GetDriverVersion(out string version);
    NvmlReturn GetNvmlVersion(out string version);
    NvmlReturn GetCudaDriverVersion(out int version);
}
