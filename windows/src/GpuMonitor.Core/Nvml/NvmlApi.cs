using System.Text;

namespace GpuMonitor.Core.Nvml;

/// <summary>INvml over the NVIDIA driver library. Translates calls only: no logging, no policy.</summary>
public sealed unsafe class NvmlApi : INvml
{
    private readonly NvmlNative _native;

    internal NvmlApi(NvmlNative native, string loadedPath)
    {
        _native = native;
        LoadedPath = loadedPath;
    }

    /// <summary>The library name or path that loaded, for diagnostics.</summary>
    public string? LoadedPath { get; }

    /// <summary>Loads nvml.dll (Windows) or libnvidia-ml.so.1 (elsewhere). Does not call Init.</summary>
    public static bool TryLoad(out NvmlApi? api, out string error)
    {
        if (NvmlNative.TryLoad(out var native, out var path, out error))
        {
            api = new NvmlApi(native!, path!);
            return true;
        }
        api = null;
        return false;
    }

    public NvmlReturn Init() => _native.Init_v2();

    public NvmlReturn Shutdown() => _native.Shutdown();

    public NvmlReturn GetDeviceCount(out uint count)
    {
        uint v = 0;
        var rc = _native.DeviceGetCount_v2(&v);
        count = v;
        return rc;
    }

    public NvmlReturn GetHandle(uint index, out nint handle)
    {
        nint v = 0;
        var rc = _native.DeviceGetHandleByIndex_v2(index, &v);
        handle = v;
        return rc;
    }

    public NvmlReturn GetName(nint handle, out string name)
    {
        var buf = stackalloc byte[NvmlNative.NameBufferSize];
        var rc = _native.DeviceGetName(handle, buf, NvmlNative.NameBufferSize);
        name = Text(rc, buf, NvmlNative.NameBufferSize);
        return rc;
    }

    public NvmlReturn GetUuid(nint handle, out string uuid)
    {
        var buf = stackalloc byte[NvmlNative.UuidBufferSize];
        var rc = _native.DeviceGetUUID(handle, buf, NvmlNative.UuidBufferSize);
        uuid = Text(rc, buf, NvmlNative.UuidBufferSize);
        return rc;
    }

    public NvmlReturn GetBusId(nint handle, out string busId)
    {
        NvmlNative.PciInfo pci = default;
        var rc = _native.DeviceGetPciInfo_v3(handle, &pci);
        busId = rc == NvmlReturn.Success ? BusIdOf(pci) : "";
        return rc;
    }

    /// <summary>The card key: the 32-byte busId field ("%08X:%02X:%02X.0", e.g. 00000000:01:00.0), not busIdLegacy.</summary>
    internal static string BusIdOf(in NvmlNative.PciInfo pci)
    {
        fixed (byte* busId = pci.BusId)
            return Decode(new ReadOnlySpan<byte>(busId, NvmlNative.PciBusIdBufferSize));
    }

    public NvmlReturn GetVbios(nint handle, out string version)
    {
        var buf = stackalloc byte[NvmlNative.VbiosBufferSize];
        var rc = _native.DeviceGetVbiosVersion(handle, buf, NvmlNative.VbiosBufferSize);
        version = Text(rc, buf, NvmlNative.VbiosBufferSize);
        return rc;
    }

    public NvmlReturn GetUtilization(nint handle, out uint gpuPct)
    {
        NvmlNative.Utilization u = default;
        var rc = _native.DeviceGetUtilizationRates(handle, &u);
        gpuPct = u.Gpu;
        return rc;
    }

    public NvmlReturn GetMemory(nint handle, out ulong usedBytes, out ulong totalBytes)
    {
        NvmlNative.Memory m = default;
        var rc = _native.DeviceGetMemoryInfo(handle, &m);
        usedBytes = m.Used;
        totalBytes = m.Total;
        return rc;
    }

    public NvmlReturn GetTemperature(nint handle, out uint c)
    {
        uint v = 0;
        var rc = _native.DeviceGetTemperature(handle, NvmlNative.TemperatureGpu, &v);
        c = v;
        return rc;
    }

    /// <param name="kind">0 NVML_TEMPERATURE_THRESHOLD_SHUTDOWN, 1 NVML_TEMPERATURE_THRESHOLD_SLOWDOWN.</param>
    public NvmlReturn GetTempThreshold(nint handle, int kind, out uint c)
    {
        uint v = 0;
        var rc = _native.DeviceGetTemperatureThreshold(handle, kind, &v);
        c = v;
        return rc;
    }

    public NvmlReturn GetPowerUsage(nint handle, out uint mW)
    {
        uint v = 0;
        var rc = _native.DeviceGetPowerUsage(handle, &v);
        mW = v;
        return rc;
    }

    public NvmlReturn GetEnforcedPowerLimit(nint handle, out uint mW)
    {
        uint v = 0;
        var rc = _native.DeviceGetEnforcedPowerLimit(handle, &v);
        mW = v;
        return rc;
    }

    public NvmlReturn GetDefaultPowerLimit(nint handle, out uint mW)
    {
        uint v = 0;
        var rc = _native.DeviceGetPowerManagementDefaultLimit(handle, &v);
        mW = v;
        return rc;
    }

    /// <param name="type">NVML_CLOCK_GRAPHICS 0, NVML_CLOCK_SM 1, NVML_CLOCK_MEM 2.</param>
    public NvmlReturn GetClock(nint handle, int type, out uint mhz)
    {
        uint v = 0;
        var rc = _native.DeviceGetClockInfo(handle, type, &v);
        mhz = v;
        return rc;
    }

    /// <param name="type">NVML_CLOCK_GRAPHICS 0, NVML_CLOCK_SM 1, NVML_CLOCK_MEM 2.</param>
    public NvmlReturn GetMaxClock(nint handle, int type, out uint mhz)
    {
        uint v = 0;
        var rc = _native.DeviceGetMaxClockInfo(handle, type, &v);
        mhz = v;
        return rc;
    }

    public NvmlReturn GetFanSpeed(nint handle, out uint pct)
    {
        uint v = 0;
        var rc = _native.DeviceGetFanSpeed(handle, &v);
        pct = v;
        return rc;
    }

    public NvmlReturn GetPState(nint handle, out int pstate)
    {
        int v = 0;
        var rc = _native.DeviceGetPerformanceState(handle, &v);
        pstate = v;
        return rc;
    }

    /// <summary>nvmlDeviceGetCurrentClocksEventReasons, or the older ThrottleReasons export when it is missing.</summary>
    public NvmlReturn GetThrottleReasons(nint handle, out ulong reasons)
    {
        ulong v = 0;
        var rc = _native.DeviceGetCurrentClocksEventReasons(handle, &v);
        if (rc == NvmlReturn.FunctionNotFound)
            rc = _native.DeviceGetCurrentClocksThrottleReasons(handle, &v);
        reasons = v;
        return rc;
    }

    public NvmlReturn GetPcieGen(nint handle, out uint current, out uint max)
    {
        uint cur = 0, mx = 0;
        var rcCur = _native.DeviceGetCurrPcieLinkGeneration(handle, &cur);
        var rcMax = _native.DeviceGetMaxPcieLinkGeneration(handle, &mx);
        current = cur;
        max = mx;
        return First(rcCur, rcMax);
    }

    public NvmlReturn GetPcieWidth(nint handle, out uint current, out uint max)
    {
        uint cur = 0, mx = 0;
        var rcCur = _native.DeviceGetCurrPcieLinkWidth(handle, &cur);
        var rcMax = _native.DeviceGetMaxPcieLinkWidth(handle, &mx);
        current = cur;
        max = mx;
        return First(rcCur, rcMax);
    }

    public NvmlReturn GetDisplayActive(nint handle, out bool active)
    {
        int v = 0;
        var rc = _native.DeviceGetDisplayActive(handle, &v);
        active = rc == NvmlReturn.Success && v == NvmlNative.FeatureEnabled;
        return rc;
    }

    /// <summary>
    /// Compute processes, then graphics processes, each PID once. UsedBytes is NVML's raw usedGpuMemory
    /// (ulong.MaxValue is NVML_VALUE_NOT_AVAILABLE, which WDDM always reports). Returns the first failing code.
    /// </summary>
    public NvmlReturn GetProcesses(nint handle, out IReadOnlyList<(uint Pid, ulong UsedBytes)> processes)
    {
        var list = new List<(uint Pid, ulong UsedBytes)>();
        var rcCompute = ReadProcesses(handle, graphics: false, list);
        var rcGraphics = ReadProcesses(handle, graphics: true, list);
        processes = list;
        return First(rcCompute, rcGraphics);
    }

    public NvmlReturn GetDriverVersion(out string version)
    {
        var buf = stackalloc byte[NvmlNative.DriverBufferSize];
        var rc = _native.SystemGetDriverVersion(buf, NvmlNative.DriverBufferSize);
        version = Text(rc, buf, NvmlNative.DriverBufferSize);
        return rc;
    }

    public NvmlReturn GetNvmlVersion(out string version)
    {
        var buf = stackalloc byte[NvmlNative.NvmlVersionBufferSize];
        var rc = _native.SystemGetNVMLVersion(buf, NvmlNative.NvmlVersionBufferSize);
        version = Text(rc, buf, NvmlNative.NvmlVersionBufferSize);
        return rc;
    }

    public NvmlReturn GetCudaDriverVersion(out int version)
    {
        int v = 0;
        var rc = _native.SystemGetCudaDriverVersion_v2(&v);
        version = v;
        return rc;
    }

    /// <summary>UTF-8 up to the first NUL (or the whole buffer when there is none).</summary>
    internal static string Decode(ReadOnlySpan<byte> buffer)
    {
        var end = buffer.IndexOf((byte)0);
        return Encoding.UTF8.GetString(end < 0 ? buffer : buffer[..end]);
    }

    private static string Text(NvmlReturn rc, byte* buffer, int length) =>
        rc == NvmlReturn.Success ? Decode(new ReadOnlySpan<byte>(buffer, length)) : "";

    private static NvmlReturn First(NvmlReturn a, NvmlReturn b) => a != NvmlReturn.Success ? a : b;

    private const int ProcessAttempts = 3;
    private const uint ProcessSlack = 8;

    /// <summary>Asks with count 0 first; on InsufficientSize allocates the reported count plus slack and retries.</summary>
    private NvmlReturn ReadProcesses(nint handle, bool graphics, List<(uint Pid, ulong UsedBytes)> into)
    {
        uint count = 0;
        var rc = Call(handle, graphics, &count, null);
        for (var attempt = 0; attempt < ProcessAttempts && rc == NvmlReturn.InsufficientSize; attempt++)
        {
            var size = count + ProcessSlack;
            var infos = new NvmlNative.ProcessInfo[size];
            fixed (NvmlNative.ProcessInfo* p = infos)
            {
                count = size;
                rc = Call(handle, graphics, &count, p);
            }
            if (rc == NvmlReturn.Success)
            {
                for (var i = 0; i < count && i < infos.Length; i++)
                {
                    var pid = infos[i].Pid;
                    if (!into.Exists(e => e.Pid == pid))
                        into.Add((pid, infos[i].UsedGpuMemory));
                }
            }
        }
        return rc;
    }

    private NvmlReturn Call(nint handle, bool graphics, uint* count, NvmlNative.ProcessInfo* infos) =>
        graphics
            ? _native.DeviceGetGraphicsRunningProcesses_v3(handle, count, infos)
            : _native.DeviceGetComputeRunningProcesses_v3(handle, count, infos);
}
