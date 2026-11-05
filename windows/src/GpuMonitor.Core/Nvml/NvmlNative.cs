using System.Runtime.InteropServices;

namespace GpuMonitor.Core.Nvml;

/// <summary>
/// NVML C entry points, resolved lazily from a loaded library handle with NativeLibrary.TryGetExport
/// and called through cdecl function pointers. A missing export returns FunctionNotFound instead of throwing.
/// Signatures and layouts follow nvml.h (NVML 13.4.92, NVIDIA/go-nvml pkg/nvml/nvml.h).
/// </summary>
internal sealed unsafe class NvmlNative
{
    // nvml.h buffer sizes.
    public const int NameBufferSize = 96;     // NVML_DEVICE_NAME_V2_BUFFER_SIZE
    public const int UuidBufferSize = 96;     // NVML_DEVICE_UUID_V2_BUFFER_SIZE
    public const int VbiosBufferSize = 32;    // NVML_DEVICE_VBIOS_VERSION_BUFFER_SIZE
    public const int DriverBufferSize = 80;   // NVML_SYSTEM_DRIVER_VERSION_BUFFER_SIZE
    public const int NvmlVersionBufferSize = 80; // NVML_SYSTEM_NVML_VERSION_BUFFER_SIZE

    // nvml.h enum values.
    public const int TemperatureGpu = 0;      // NVML_TEMPERATURE_GPU
    public const int FeatureEnabled = 1;      // NVML_FEATURE_ENABLED

    /// <summary>nvmlPciInfo_t (filled by nvmlDeviceGetPciInfo_v3). 68 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PciInfo
    {
        public fixed byte BusIdLegacy[16];    // NVML_DEVICE_PCI_BUS_ID_BUFFER_V2_SIZE
        public uint Domain;
        public uint Bus;
        public uint Device;
        public uint PciDeviceId;
        public uint PciSubSystemId;
        public fixed byte BusId[32];          // NVML_DEVICE_PCI_BUS_ID_BUFFER_SIZE
    }

    /// <summary>nvmlMemory_t (v1). 24 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Memory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    /// <summary>nvmlUtilization_t. 8 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization
    {
        public uint Gpu;
        public uint Memory;
    }

    /// <summary>nvmlProcessInfo_t (= nvmlProcessInfo_v2_t, used by the _v3 process calls). 24 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInfo
    {
        public uint Pid;
        public ulong UsedGpuMemory;
        public uint GpuInstanceId;
        public uint ComputeInstanceId;
    }

    /// <summary>Library names or paths to try, in order.</summary>
    public static IReadOnlyList<string> Candidates()
    {
        if (!OperatingSystem.IsWindows())
            return ["libnvidia-ml.so.1"];
        var list = new List<string> { "nvml.dll" };
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrEmpty(programFiles))
            list.Add(Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI", "nvml.dll"));
        return list;
    }

    /// <summary>Loads the first candidate that loads. On failure, error names every candidate tried.</summary>
    public static bool TryLoad(out NvmlNative? native, out string? loadedPath, out string error)
    {
        var candidates = Candidates();
        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                native = new NvmlNative(handle);
                loadedPath = candidate;
                error = "";
                return true;
            }
        }
        native = null;
        loadedPath = null;
        error = "NVML library not found. Tried: " + string.Join(", ", candidates);
        return false;
    }

    private const nint Missing = -1;
    private readonly nint _library;
    private readonly nint[] _slots = new nint[(int)Fn.Count];

    public NvmlNative(nint library) => _library = library;

    private enum Fn
    {
        Init_v2, Shutdown, SystemGetDriverVersion, SystemGetNVMLVersion, SystemGetCudaDriverVersion_v2,
        DeviceGetCount_v2, DeviceGetHandleByIndex_v2, DeviceGetName, DeviceGetUUID, DeviceGetPciInfo_v3,
        DeviceGetVbiosVersion, DeviceGetUtilizationRates, DeviceGetMemoryInfo, DeviceGetTemperature,
        DeviceGetTemperatureThreshold, DeviceGetPowerUsage, DeviceGetEnforcedPowerLimit,
        DeviceGetPowerManagementDefaultLimit, DeviceGetClockInfo, DeviceGetMaxClockInfo, DeviceGetFanSpeed,
        DeviceGetPerformanceState, DeviceGetCurrentClocksEventReasons, DeviceGetCurrentClocksThrottleReasons,
        DeviceGetCurrPcieLinkGeneration, DeviceGetMaxPcieLinkGeneration, DeviceGetCurrPcieLinkWidth,
        DeviceGetMaxPcieLinkWidth, DeviceGetDisplayActive, DeviceGetComputeRunningProcesses_v3,
        DeviceGetGraphicsRunningProcesses_v3,
        Count,
    }

    /// <summary>Export address, or 0 when the library does not export it. Cached per entry point.</summary>
    private nint Get(Fn fn)
    {
        var slot = _slots[(int)fn];
        if (slot == 0)
        {
            slot = NativeLibrary.TryGetExport(_library, "nvml" + fn, out var address) ? address : Missing;
            _slots[(int)fn] = slot;
        }
        return slot == Missing ? 0 : slot;
    }

    private static NvmlReturn R(int rc) => (NvmlReturn)rc;
    private const NvmlReturn NotFound = NvmlReturn.FunctionNotFound;

    // nvmlReturn_t nvmlInit_v2(void)
    public NvmlReturn Init_v2()
    {
        var f = (delegate* unmanaged[Cdecl]<int>)Get(Fn.Init_v2);
        return f == null ? NotFound : R(f());
    }

    // nvmlReturn_t nvmlShutdown(void)
    public NvmlReturn Shutdown()
    {
        var f = (delegate* unmanaged[Cdecl]<int>)Get(Fn.Shutdown);
        return f == null ? NotFound : R(f());
    }

    // nvmlReturn_t nvmlSystemGetDriverVersion(char *version, unsigned int length)
    public NvmlReturn SystemGetDriverVersion(byte* version, uint length)
    {
        var f = (delegate* unmanaged[Cdecl]<byte*, uint, int>)Get(Fn.SystemGetDriverVersion);
        return f == null ? NotFound : R(f(version, length));
    }

    // nvmlReturn_t nvmlSystemGetNVMLVersion(char *version, unsigned int length)
    public NvmlReturn SystemGetNVMLVersion(byte* version, uint length)
    {
        var f = (delegate* unmanaged[Cdecl]<byte*, uint, int>)Get(Fn.SystemGetNVMLVersion);
        return f == null ? NotFound : R(f(version, length));
    }

    // nvmlReturn_t nvmlSystemGetCudaDriverVersion_v2(int *cudaDriverVersion)
    public NvmlReturn SystemGetCudaDriverVersion_v2(int* version)
    {
        var f = (delegate* unmanaged[Cdecl]<int*, int>)Get(Fn.SystemGetCudaDriverVersion_v2);
        return f == null ? NotFound : R(f(version));
    }

    // nvmlReturn_t nvmlDeviceGetCount_v2(unsigned int *deviceCount)
    public NvmlReturn DeviceGetCount_v2(uint* count)
    {
        var f = (delegate* unmanaged[Cdecl]<uint*, int>)Get(Fn.DeviceGetCount_v2);
        return f == null ? NotFound : R(f(count));
    }

    // nvmlReturn_t nvmlDeviceGetHandleByIndex_v2(unsigned int index, nvmlDevice_t *device)
    public NvmlReturn DeviceGetHandleByIndex_v2(uint index, nint* device)
    {
        var f = (delegate* unmanaged[Cdecl]<uint, nint*, int>)Get(Fn.DeviceGetHandleByIndex_v2);
        return f == null ? NotFound : R(f(index, device));
    }

    // nvmlReturn_t nvmlDeviceGetName(nvmlDevice_t device, char *name, unsigned int length)
    public NvmlReturn DeviceGetName(nint device, byte* name, uint length)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, byte*, uint, int>)Get(Fn.DeviceGetName);
        return f == null ? NotFound : R(f(device, name, length));
    }

    // nvmlReturn_t nvmlDeviceGetUUID(nvmlDevice_t device, char *uuid, unsigned int length)
    public NvmlReturn DeviceGetUUID(nint device, byte* uuid, uint length)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, byte*, uint, int>)Get(Fn.DeviceGetUUID);
        return f == null ? NotFound : R(f(device, uuid, length));
    }

    // nvmlReturn_t nvmlDeviceGetPciInfo_v3(nvmlDevice_t device, nvmlPciInfo_t *pci)
    public NvmlReturn DeviceGetPciInfo_v3(nint device, PciInfo* pci)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, PciInfo*, int>)Get(Fn.DeviceGetPciInfo_v3);
        return f == null ? NotFound : R(f(device, pci));
    }

    // nvmlReturn_t nvmlDeviceGetVbiosVersion(nvmlDevice_t device, char *version, unsigned int length)
    public NvmlReturn DeviceGetVbiosVersion(nint device, byte* version, uint length)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, byte*, uint, int>)Get(Fn.DeviceGetVbiosVersion);
        return f == null ? NotFound : R(f(device, version, length));
    }

    // nvmlReturn_t nvmlDeviceGetUtilizationRates(nvmlDevice_t device, nvmlUtilization_t *utilization)
    public NvmlReturn DeviceGetUtilizationRates(nint device, Utilization* utilization)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, Utilization*, int>)Get(Fn.DeviceGetUtilizationRates);
        return f == null ? NotFound : R(f(device, utilization));
    }

    // nvmlReturn_t nvmlDeviceGetMemoryInfo(nvmlDevice_t device, nvmlMemory_t *memory)
    public NvmlReturn DeviceGetMemoryInfo(nint device, Memory* memory)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, Memory*, int>)Get(Fn.DeviceGetMemoryInfo);
        return f == null ? NotFound : R(f(device, memory));
    }

    // nvmlReturn_t nvmlDeviceGetTemperature(nvmlDevice_t device, nvmlTemperatureSensors_t sensorType, unsigned int *temp)
    public NvmlReturn DeviceGetTemperature(nint device, int sensorType, uint* temp)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, int, uint*, int>)Get(Fn.DeviceGetTemperature);
        return f == null ? NotFound : R(f(device, sensorType, temp));
    }

    // nvmlReturn_t nvmlDeviceGetTemperatureThreshold(nvmlDevice_t device, nvmlTemperatureThresholds_t thresholdType, unsigned int *temp)
    public NvmlReturn DeviceGetTemperatureThreshold(nint device, int thresholdType, uint* temp)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, int, uint*, int>)Get(Fn.DeviceGetTemperatureThreshold);
        return f == null ? NotFound : R(f(device, thresholdType, temp));
    }

    // nvmlReturn_t nvmlDeviceGetPowerUsage(nvmlDevice_t device, unsigned int *power)
    public NvmlReturn DeviceGetPowerUsage(nint device, uint* power) => DeviceUInt(Fn.DeviceGetPowerUsage, device, power);

    // nvmlReturn_t nvmlDeviceGetEnforcedPowerLimit(nvmlDevice_t device, unsigned int *limit)
    public NvmlReturn DeviceGetEnforcedPowerLimit(nint device, uint* limit) => DeviceUInt(Fn.DeviceGetEnforcedPowerLimit, device, limit);

    // nvmlReturn_t nvmlDeviceGetPowerManagementDefaultLimit(nvmlDevice_t device, unsigned int *defaultLimit)
    public NvmlReturn DeviceGetPowerManagementDefaultLimit(nint device, uint* limit) => DeviceUInt(Fn.DeviceGetPowerManagementDefaultLimit, device, limit);

    // nvmlReturn_t nvmlDeviceGetClockInfo(nvmlDevice_t device, nvmlClockType_t type, unsigned int *clock)
    public NvmlReturn DeviceGetClockInfo(nint device, int type, uint* clock)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, int, uint*, int>)Get(Fn.DeviceGetClockInfo);
        return f == null ? NotFound : R(f(device, type, clock));
    }

    // nvmlReturn_t nvmlDeviceGetMaxClockInfo(nvmlDevice_t device, nvmlClockType_t type, unsigned int *clock)
    public NvmlReturn DeviceGetMaxClockInfo(nint device, int type, uint* clock)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, int, uint*, int>)Get(Fn.DeviceGetMaxClockInfo);
        return f == null ? NotFound : R(f(device, type, clock));
    }

    // nvmlReturn_t nvmlDeviceGetFanSpeed(nvmlDevice_t device, unsigned int *speed)
    public NvmlReturn DeviceGetFanSpeed(nint device, uint* speed) => DeviceUInt(Fn.DeviceGetFanSpeed, device, speed);

    // nvmlReturn_t nvmlDeviceGetPerformanceState(nvmlDevice_t device, nvmlPstates_t *pState)
    public NvmlReturn DeviceGetPerformanceState(nint device, int* pState)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, int*, int>)Get(Fn.DeviceGetPerformanceState);
        return f == null ? NotFound : R(f(device, pState));
    }

    // nvmlReturn_t nvmlDeviceGetCurrentClocksEventReasons(nvmlDevice_t device, unsigned long long *clocksEventReasons)
    public NvmlReturn DeviceGetCurrentClocksEventReasons(nint device, ulong* reasons) => DeviceULong(Fn.DeviceGetCurrentClocksEventReasons, device, reasons);

    // nvmlReturn_t nvmlDeviceGetCurrentClocksThrottleReasons(nvmlDevice_t device, unsigned long long *clocksThrottleReasons)
    public NvmlReturn DeviceGetCurrentClocksThrottleReasons(nint device, ulong* reasons) => DeviceULong(Fn.DeviceGetCurrentClocksThrottleReasons, device, reasons);

    // nvmlReturn_t nvmlDeviceGetCurrPcieLinkGeneration(nvmlDevice_t device, unsigned int *currLinkGen)
    public NvmlReturn DeviceGetCurrPcieLinkGeneration(nint device, uint* gen) => DeviceUInt(Fn.DeviceGetCurrPcieLinkGeneration, device, gen);

    // nvmlReturn_t nvmlDeviceGetMaxPcieLinkGeneration(nvmlDevice_t device, unsigned int *maxLinkGen)
    public NvmlReturn DeviceGetMaxPcieLinkGeneration(nint device, uint* gen) => DeviceUInt(Fn.DeviceGetMaxPcieLinkGeneration, device, gen);

    // nvmlReturn_t nvmlDeviceGetCurrPcieLinkWidth(nvmlDevice_t device, unsigned int *currLinkWidth)
    public NvmlReturn DeviceGetCurrPcieLinkWidth(nint device, uint* width) => DeviceUInt(Fn.DeviceGetCurrPcieLinkWidth, device, width);

    // nvmlReturn_t nvmlDeviceGetMaxPcieLinkWidth(nvmlDevice_t device, unsigned int *maxLinkWidth)
    public NvmlReturn DeviceGetMaxPcieLinkWidth(nint device, uint* width) => DeviceUInt(Fn.DeviceGetMaxPcieLinkWidth, device, width);

    // nvmlReturn_t nvmlDeviceGetDisplayActive(nvmlDevice_t device, nvmlEnableState_t *isActive)
    public NvmlReturn DeviceGetDisplayActive(nint device, int* isActive)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, int*, int>)Get(Fn.DeviceGetDisplayActive);
        return f == null ? NotFound : R(f(device, isActive));
    }

    // nvmlReturn_t nvmlDeviceGetComputeRunningProcesses_v3(nvmlDevice_t device, unsigned int *infoCount, nvmlProcessInfo_t *infos)
    public NvmlReturn DeviceGetComputeRunningProcesses_v3(nint device, uint* infoCount, ProcessInfo* infos) =>
        DeviceProcesses(Fn.DeviceGetComputeRunningProcesses_v3, device, infoCount, infos);

    // nvmlReturn_t nvmlDeviceGetGraphicsRunningProcesses_v3(nvmlDevice_t device, unsigned int *infoCount, nvmlProcessInfo_t *infos)
    public NvmlReturn DeviceGetGraphicsRunningProcesses_v3(nint device, uint* infoCount, ProcessInfo* infos) =>
        DeviceProcesses(Fn.DeviceGetGraphicsRunningProcesses_v3, device, infoCount, infos);

    private NvmlReturn DeviceUInt(Fn fn, nint device, uint* value)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, uint*, int>)Get(fn);
        return f == null ? NotFound : R(f(device, value));
    }

    private NvmlReturn DeviceULong(Fn fn, nint device, ulong* value)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, ulong*, int>)Get(fn);
        return f == null ? NotFound : R(f(device, value));
    }

    private NvmlReturn DeviceProcesses(Fn fn, nint device, uint* infoCount, ProcessInfo* infos)
    {
        var f = (delegate* unmanaged[Cdecl]<nint, uint*, ProcessInfo*, int>)Get(fn);
        return f == null ? NotFound : R(f(device, infoCount, infos));
    }
}
