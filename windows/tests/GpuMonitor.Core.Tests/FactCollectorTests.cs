using Xunit;
using GpuMonitor.Core.Alerts;
using GpuMonitor.Core.Diagnostics;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Readers;
using GpuMonitor.Core.Settings;

namespace GpuMonitor.Core.Tests;

public class FactCollectorTests
{
    private sealed class ThrowingWmi : IWmiSource
    {
        public IReadOnlyList<WmiAdapter> Adapters() => throw new InvalidOperationException("wmi down");
    }

    private static AppState State(IReadOnlyList<string>? tail = null) => new(
        "1.0", "abc", ".NET 10", new AppSettings(), SettingsStatus.Loaded, null,
        Array.Empty<Alert>(), new Dictionary<string, TimingStats>(), tail ?? Array.Empty<string>(), true);

    private static FakeNvml TwoCards()
    {
        var f = new FakeNvml();
        f.Add("00000000:01:00.0", "RTX 5080");
        f.Add("00000000:02:00.0", "RTX 4070 Ti SUPER");
        return f;
    }

    private static FactCollector Make(INvml? n, NvmlReturn rc, CardSources? s = null, AppState? st = null) =>
        new(n, rc, "nvml.dll", null, s ?? NullSources.All(), st ?? State());

    [Fact]
    public void Collect_AllSectionsPresent()
    {
        var f = TwoCards();
        f.Gpus[0].PowerMilliwatts = 123500;
        f.Gpus[0].MemUsedBytes = 2UL * 1024 * 1024 * 1024;
        var t = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        var facts = new FactCollector(f, NvmlReturn.Success, "p", null, NullSources.All(), State(), new ManualClock(t)).Collect();

        Assert.Equal(t, facts.CollectedAt);
        Assert.Equal("1.0", facts.App.Version);
        Assert.True(facts.Windows.Version.Ok);
        Assert.Equal("12.8", facts.Driver.CudaVersion.Value);
        Assert.Equal("572.83", facts.Driver.DriverVersion.Value);
        Assert.Equal(2, facts.Cards.Count);
        var c = facts.Cards[0];
        Assert.Equal("00000000:01:00.0", c.BusId);
        Assert.Equal(123.5, c.PowerW.Value);
        Assert.Equal(2048, c.VramUsedMiB.Value);
        Assert.Equal(16384, c.VramTotalMiB.Value);
        Assert.Equal(90u, c.TempSlowdownC.Value);
        Assert.Equal(100u, c.TempShutdownC.Value);
        Assert.Equal(320.0, c.PowerDefaultW.Value);
        Assert.Equal(3000u, c.GraphicsMaxMHz.Value);
        Assert.Equal(5u, c.PcieGenMax.Value);
        Assert.Equal(4u, c.PcieGenCur.Value);
        Assert.Equal("95.02.00.00.01", c.Vbios.Value);
        Assert.True(facts.Wmi.Ok);
        Assert.True(facts.NvidiaSmi.Ok);
        Assert.True(facts.GpuPreferences.Ok);
        Assert.True(facts.EventLog.Ok);
    }

    [Fact]
    public void SourceThrows_RecordedAsError()
    {
        var s = NullSources.All() with { Wmi = new ThrowingWmi() };
        var facts = Make(TwoCards(), NvmlReturn.Success, s).Collect();
        Assert.False(facts.Wmi.Ok);
        Assert.Equal("error: InvalidOperationException: wmi down", facts.Wmi.Error);
        Assert.True(facts.EventLog.Ok);
        Assert.Equal(2, facts.Cards.Count);
    }

    [Fact]
    public void NvmlNotLoaded_DriverSectionError_CardsEmpty()
    {
        var facts = new FactCollector(null, NvmlReturn.LibraryNotFound, null, "no dll", NullSources.All(), State()).Collect();
        Assert.Empty(facts.Cards);
        Assert.Equal("no dll", facts.Driver.LoadError);
        Assert.Equal(NvmlReturn.LibraryNotFound, facts.Driver.InitCode);
        Assert.Equal("error: NVML not loaded", facts.Driver.DriverVersion.Error);
        Assert.Equal("error: NVML not loaded", facts.Driver.CudaVersion.Error);
    }

    [Fact]
    public void ProcessGone_NamedExited()
    {
        var f = new FakeNvml();
        var g = f.Add("00000000:01:00.0");
        g.Processes.Add((999999999u, 1024UL * 1024 * 100));
        g.Processes.Add((1u, ulong.MaxValue));
        var procs = Make(f, NvmlReturn.Success).Collect().Cards[0].Processes.Value!;
        Assert.Equal("error: exited", procs[0].Name);
        Assert.Equal(100, procs[0].UsedMiB);
        Assert.Null(procs[1].UsedMiB);
    }

    [Fact]
    public void LogTail_Is40Lines()
    {
        var tail = Enumerable.Range(1, 100).Select(i => "line " + i).ToList();
        var facts = Make(TwoCards(), NvmlReturn.Success, null, State(tail)).Collect();
        Assert.Equal(40, facts.State.LogTail.Count);
        Assert.Equal("line 61", facts.State.LogTail[0]);
        Assert.Equal("line 100", facts.State.LogTail[39]);
    }

    [Fact]
    public void FieldFailure_DoesNotAffectOthers_AndBadCardSkipped()
    {
        var f = TwoCards();
        f.Gpus[0].Fail["GetFanSpeed"] = NvmlReturn.NotSupported;
        f.Gpus[1].Fail["GetBusId"] = NvmlReturn.Unknown;
        var facts = Make(f, NvmlReturn.Success).Collect();
        var c = Assert.Single(facts.Cards);
        Assert.Equal("error: NVML_ERROR_NOT_SUPPORTED", c.FanPct.Error);
        Assert.True(c.TempC.Ok);
    }
}
