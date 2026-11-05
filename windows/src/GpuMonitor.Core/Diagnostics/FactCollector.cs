using System.Diagnostics;
using GpuMonitor.Core.Alerts;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Readers;
using GpuMonitor.Core.Settings;

namespace GpuMonitor.Core.Diagnostics;

public sealed record AppState(string Version, string Commit, string Runtime, AppSettings Settings,
    SettingsStatus SettingsStatus, string? StartupValue, IReadOnlyList<Alert> Alerts,
    IReadOnlyDictionary<string, TimingStats> Timings, IReadOnlyList<string> LogTail, bool LogWritable);

/// <summary>Gathers every diagnostic fact. Each fact is read on its own, so one failure never affects the rest.</summary>
public sealed class FactCollector(INvml? nvml, NvmlReturn initCode, string? nvmlPath, string? nvmlLoadError,
    CardSources sources, AppState state, TimeProvider? time = null)
{
    public const int LogTailLines = 40;
    private static readonly TimeSpan EventWindow = TimeSpan.FromDays(7);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public Facts Collect()
    {
        var windows = new WindowsFacts(
            Guard(sources.Win.Version),
            Guard(sources.Win.Uptime),
            Guard(sources.Win.HwSchMode),
            Guard(sources.Win.GameMode));

        var driver = BuildDriver();
        var cards = driver.InitCode == NvmlReturn.Success && nvml is not null ? BuildCards(nvml) : new List<CardFacts>();

        var tail = state.LogTail.Count > LogTailLines
            ? state.LogTail.Skip(state.LogTail.Count - LogTailLines).ToList()
            : state.LogTail;

        return new Facts(
            _time.GetUtcNow(),
            new AppFacts(state.Version, state.Commit, state.Runtime),
            windows,
            driver,
            cards,
            Guard(() => sources.Wmi.Adapters()),
            Guard(sources.Smi.QueryAll),
            Guard(sources.Registry.GpuPreferences),
            Guard(() => sources.Events.Recent(EventWindow)),
            new StateFacts(state.Settings, state.SettingsStatus, state.StartupValue, state.Alerts,
                state.Timings, tail, state.LogWritable));
    }

    private static Fact<T> Guard<T>(Func<T> read)
    {
        try { return Fact<T>.Of(read()); }
        catch (Exception ex) { return Fact<T>.Fail("error: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private delegate NvmlReturn Call<T>(out T value);

    private static Fact<T> Nv<T>(Call<T> call) => Nv(call, v => v);

    private static Fact<TOut> Nv<T, TOut>(Call<T> call, Func<T, TOut> map)
    {
        try
        {
            var rc = call(out var v);
            return rc == NvmlReturn.Success ? Fact<TOut>.Of(map(v)) : Fact<TOut>.Fail(rc.NvmlReturnName());
        }
        catch (Exception ex) { return Fact<TOut>.Fail("error: " + ex.GetType().Name + ": " + ex.Message); }
    }

    private DriverFacts BuildDriver()
    {
        if (nvml is null || initCode != NvmlReturn.Success)
        {
            const string none = "error: NVML not loaded";
            return new DriverFacts(initCode, nvmlPath, nvmlLoadError,
                Fact<string>.Fail(none), Fact<string>.Fail(none), Fact<string>.Fail(none));
        }
        return new DriverFacts(initCode, nvmlPath, nvmlLoadError,
            Nv((out string v) => nvml.GetDriverVersion(out v)),
            Nv((out string v) => nvml.GetNvmlVersion(out v)),
            Nv((out int v) => nvml.GetCudaDriverVersion(out v), Units.CudaVersion));
    }

    private List<CardFacts> BuildCards(INvml n)
    {
        var list = new List<CardFacts>();
        try
        {
            if (n.GetDeviceCount(out var count) != NvmlReturn.Success) return list;
            for (uint i = 0; i < count; i++)
            {
                try
                {
                    if (n.GetHandle(i, out var h) != NvmlReturn.Success) continue;
                    if (n.GetBusId(h, out var bus) != NvmlReturn.Success) continue;
                    list.Add(BuildCard(n, h, bus));
                }
                catch (Exception) { /* skip this card */ }
            }
        }
        catch (Exception) { /* keep what was found */ }
        return list;
    }

    private static CardFacts BuildCard(INvml n, nint h, string bus)
    {
        Fact<uint> Pair(Call<(uint, uint)> c, bool max) => Nv(c, t => max ? t.Item2 : t.Item1);
        Call<(uint, uint)> gen = (out (uint, uint) v) => { var rc = n.GetPcieGen(h, out var a, out var b); v = (a, b); return rc; };
        Call<(uint, uint)> width = (out (uint, uint) v) => { var rc = n.GetPcieWidth(h, out var a, out var b); v = (a, b); return rc; };
        Call<(ulong, ulong)> mem = (out (ulong, ulong) v) => { var rc = n.GetMemory(h, out var a, out var b); v = (a, b); return rc; };

        return new CardFacts(bus,
            Nv((out string v) => n.GetName(h, out v)),
            Nv((out string v) => n.GetUuid(h, out v)),
            Nv((out string v) => n.GetVbios(h, out v)),
            Pair(gen, false), Pair(gen, true), Pair(width, false), Pair(width, true),
            Nv((out bool v) => n.GetDisplayActive(h, out v)),
            Nv((out uint v) => n.GetTemperature(h, out v)),
            Nv((out uint v) => n.GetTempThreshold(h, 1, out v)),
            Nv((out uint v) => n.GetTempThreshold(h, 0, out v)),
            Nv((out uint v) => n.GetPowerUsage(h, out v), Units.MilliwattsToWatts),
            Nv((out uint v) => n.GetEnforcedPowerLimit(h, out v), Units.MilliwattsToWatts),
            Nv((out uint v) => n.GetDefaultPowerLimit(h, out v), Units.MilliwattsToWatts),
            Nv((out uint v) => n.GetClock(h, 0, out v)),
            Nv((out uint v) => n.GetMaxClock(h, 0, out v)),
            Nv((out uint v) => n.GetClock(h, 2, out v)),
            Nv((out uint v) => n.GetMaxClock(h, 2, out v)),
            Nv((out uint v) => n.GetClock(h, 1, out v)),
            Nv((out uint v) => n.GetMaxClock(h, 1, out v)),
            Nv((out uint v) => n.GetFanSpeed(h, out v)),
            Nv((out int v) => n.GetPState(h, out v)),
            Nv((out ulong v) => n.GetThrottleReasons(h, out v), r => (ThrottleReasons)r),
            Nv(mem, t => Units.BytesToMiB(t.Item1)),
            Nv(mem, t => Units.BytesToMiB(t.Item2)),
            Nv((out IReadOnlyList<(uint Pid, ulong UsedBytes)> v) => n.GetProcesses(h, out v),
                ps => (IReadOnlyList<ProcessFact>)ps.Select(p => new ProcessFact(p.Pid, ProcessName(p.Pid),
                    p.UsedBytes == ulong.MaxValue ? null : Units.BytesToMiB(p.UsedBytes))).ToList()));
    }

    private static string ProcessName(uint pid)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return "error: exited";
        }
        catch (Exception ex)
        {
            return "error: " + ex.GetType().Name + ": " + ex.Message;
        }
    }
}
