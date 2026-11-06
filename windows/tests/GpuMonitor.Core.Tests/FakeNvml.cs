using System.Runtime.CompilerServices;
using GpuMonitor.Core.Nvml;

namespace GpuMonitor.Core.Tests;

/// <summary>One simulated GPU with settable raw NVML values.</summary>
public sealed class FakeGpu
{
    private static int _nextHandle = 1000;

    public FakeGpu(string busId, string name = "Fake GPU")
    {
        BusId = busId;
        Name = name;
        Uuid = "GPU-" + busId;
        Handle = Interlocked.Increment(ref _nextHandle);
    }

    public nint Handle { get; }
    public string BusId { get; set; }
    public string Name { get; set; }
    public string Uuid { get; set; }
    public string Vbios { get; set; } = "95.02.00.00.01";
    public uint UtilizationPct { get; set; }
    public ulong MemUsedBytes { get; set; }
    public ulong MemTotalBytes { get; set; } = 16UL * 1024 * 1024 * 1024;
    public uint TempC { get; set; } = 40;
    public uint ShutdownTempC { get; set; } = 100;
    public uint SlowdownTempC { get; set; } = 90;
    public uint PowerMilliwatts { get; set; }
    public uint PowerLimitMilliwatts { get; set; } = 320000;
    public uint DefaultPowerLimitMilliwatts { get; set; } = 320000;
    public uint GraphicsMHz { get; set; }
    public uint SmMHz { get; set; }
    public uint MemoryMHz { get; set; }
    public uint MaxGraphicsMHz { get; set; } = 3000;
    public uint MaxSmMHz { get; set; } = 3000;
    public uint MaxMemoryMHz { get; set; } = 11000;
    public uint FanPct { get; set; }
    public int PState { get; set; } = 8;
    public ulong ThrottleReasons { get; set; }
    public uint PcieGen { get; set; } = 4;
    public uint PcieMaxGen { get; set; } = 5;
    public uint PcieWidth { get; set; } = 16;
    public uint PcieMaxWidth { get; set; } = 16;
    public bool DisplayActive { get; set; }
    public List<(uint Pid, ulong UsedBytes)> Processes { get; } = new();

    /// <summary>Forced return code by INvml method name, for example "GetFanSpeed".</summary>
    public Dictionary<string, NvmlReturn> Fail { get; } = new();
}

public sealed class FakeNvml : INvml
{
    public List<FakeGpu> Gpus { get; } = new();
    public NvmlReturn InitResult { get; set; } = NvmlReturn.Success;
    public Dictionary<string, NvmlReturn> Fail { get; } = new();
    public string DriverVersion { get; set; } = "572.83";
    public string NvmlVersion { get; set; } = "12.572.83";
    public int CudaDriverVersion { get; set; } = 12080;
    public int InitCalls { get; private set; }

    public FakeGpu Add(string busId, string name = "Fake GPU")
    {
        var g = new FakeGpu(busId, name);
        Gpus.Add(g);
        return g;
    }

    public NvmlReturn Init() { InitCalls++; return InitResult; }
    public int ShutdownCalls { get; private set; }
    public bool ShutdownThrows { get; set; }
    public NvmlReturn Shutdown()
    {
        ShutdownCalls++;
        if (ShutdownThrows) throw new InvalidOperationException("shutdown failed");
        return NvmlReturn.Success;
    }

    public NvmlReturn GetDeviceCount(out uint count)
    {
        count = (uint)Gpus.Count;
        return Global();
    }

    public NvmlReturn GetHandle(uint index, out nint handle)
    {
        handle = 0;
        var rc = Global();
        if (rc != NvmlReturn.Success) return rc;
        if (index >= Gpus.Count) return NvmlReturn.InvalidArgument;
        var g = Gpus[(int)index];
        if (g.Fail.TryGetValue(nameof(GetHandle), out var f)) return f;
        handle = g.Handle;
        return NvmlReturn.Success;
    }

    public NvmlReturn GetName(nint h, out string v) => Get(h, g => g.Name, out v);
    public NvmlReturn GetUuid(nint h, out string v) => Get(h, g => g.Uuid, out v);
    public NvmlReturn GetBusId(nint h, out string v) => Get(h, g => g.BusId, out v);
    public NvmlReturn GetVbios(nint h, out string v) => Get(h, g => g.Vbios, out v);
    public NvmlReturn GetUtilization(nint h, out uint v) => Get(h, g => g.UtilizationPct, out v);

    public NvmlReturn GetMemory(nint h, out ulong used, out ulong total)
    {
        ulong u = 0, t = 0;
        var rc = With(h, g => { u = g.MemUsedBytes; t = g.MemTotalBytes; });
        used = u;
        total = t;
        return rc;
    }

    public NvmlReturn GetTemperature(nint h, out uint v) => Get(h, g => g.TempC, out v);

    public NvmlReturn GetTempThreshold(nint h, int kind, out uint v)
    => Get(h, g => kind == 0 ? g.ShutdownTempC : g.SlowdownTempC, out v);

    public NvmlReturn GetPowerUsage(nint h, out uint v) => Get(h, g => g.PowerMilliwatts, out v);
    public NvmlReturn GetEnforcedPowerLimit(nint h, out uint v) => Get(h, g => g.PowerLimitMilliwatts, out v);
    public NvmlReturn GetDefaultPowerLimit(nint h, out uint v) => Get(h, g => g.DefaultPowerLimitMilliwatts, out v);

    public NvmlReturn GetClock(nint h, int type, out uint v)
    => Get(h, g => type == 0 ? g.GraphicsMHz : type == 1 ? g.SmMHz : g.MemoryMHz, out v);

    public NvmlReturn GetMaxClock(nint h, int type, out uint v)
    => Get(h, g => type == 0 ? g.MaxGraphicsMHz : type == 1 ? g.MaxSmMHz : g.MaxMemoryMHz, out v);

    public NvmlReturn GetFanSpeed(nint h, out uint v) => Get(h, g => g.FanPct, out v);
    public NvmlReturn GetPState(nint h, out int v) => Get(h, g => g.PState, out v);
    public NvmlReturn GetThrottleReasons(nint h, out ulong v) => Get(h, g => g.ThrottleReasons, out v);

    public NvmlReturn GetPcieGen(nint h, out uint cur, out uint max)
    {
        uint c = 0, m = 0;
        var rc = With(h, g => { c = g.PcieGen; m = g.PcieMaxGen; });
        cur = c;
        max = m;
        return rc;
    }

    public NvmlReturn GetPcieWidth(nint h, out uint cur, out uint max)
    {
        uint c = 0, m = 0;
        var rc = With(h, g => { c = g.PcieWidth; m = g.PcieMaxWidth; });
        cur = c;
        max = m;
        return rc;
    }

    public NvmlReturn GetDisplayActive(nint h, out bool v) => Get(h, g => g.DisplayActive, out v);

    public NvmlReturn GetProcesses(nint h, out IReadOnlyList<(uint Pid, ulong UsedBytes)> processes)
    {
        IReadOnlyList<(uint Pid, ulong UsedBytes)> p = Array.Empty<(uint, ulong)>();
        var rc = With(h, g => p = g.Processes.ToArray());
        processes = p;
        return rc;
    }

    public NvmlReturn GetDriverVersion(out string v) { v = DriverVersion; return Global(); }
    public NvmlReturn GetNvmlVersion(out string v) { v = NvmlVersion; return Global(); }
    public NvmlReturn GetCudaDriverVersion(out int v) { v = CudaDriverVersion; return Global(); }

    private NvmlReturn Global([CallerMemberName] string call = "") =>
        Fail.TryGetValue(call, out var f) ? f : NvmlReturn.Success;

    private NvmlReturn Get<T>(nint handle, Func<FakeGpu, T> read, out T value, [CallerMemberName] string call = "")
    {
        T v = default!;
        var rc = With(handle, g => v = read(g), call);
        value = v;
        return rc;
    }

    private NvmlReturn With(nint handle, Action<FakeGpu> read, [CallerMemberName] string call = "")
    {
        var rc = Global(call);
        if (rc != NvmlReturn.Success) return rc;
        var g = Gpus.Find(x => x.Handle == handle);
        if (g is null) return NvmlReturn.GpuNotFound;
        if (g.Fail.TryGetValue(call, out var f)) return f;
        read(g);
        return NvmlReturn.Success;
    }
}
