using GpuMonitor.Core.Logging;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Readers;
using Xunit;

namespace GpuMonitor.Core.Tests;

public class NvmlReaderTests
{
    private const string BusA = "00000000:01:00.0";
    private const string BusB = "00000000:02:00.0";
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static (FakeNvml fake, NvmlReader reader, List<LogLine> lines) Make()
    {
        var fake = new FakeNvml();
        var log = new AppLog(null, LogLevel.Debug, false);
        var lines = new List<LogLine>();
        log.Line += lines.Add;
        return (fake, new NvmlReader(fake, log), lines);
    }

    [Fact]
    public void Poll_TwoCards_ConvertsUnits()
    {
        var (fake, reader, _) = Make();
        var a = fake.Add(BusA, "RTX 5080");
        a.PowerMilliwatts = 285000;
        a.MemUsedBytes = 4UL * 1024 * 1024 * 1024;
        a.MemTotalBytes = 16UL * 1024 * 1024 * 1024;
        a.TempC = 66;
        a.UtilizationPct = 97;
        a.GraphicsMHz = 2800; a.SmMHz = 2801; a.MemoryMHz = 11000;
        a.ThrottleReasons = 0x4;
        a.DisplayActive = true;
        fake.Add(BusB, "RTX 4070 Ti SUPER").PowerMilliwatts = 100500;

        var r = reader.Poll(T0);

        Assert.Equal(2, r.Count);
        Assert.Equal(2, reader.Cards.Count);
        var ra = r.Single(x => x.BusId == BusA);
        Assert.Equal(T0, ra.At);
        Assert.Equal(285.0, ra.PowerW.Value);
        Assert.Equal(4096, ra.VramUsedMiB.Value);
        Assert.Equal(16384, ra.VramTotalMiB.Value);
        Assert.Equal(66, ra.TempC.Value);
        Assert.Equal(97, ra.LoadPct.Value);
        Assert.Equal(2800, ra.GraphicsMHz.Value);
        Assert.Equal(2801, ra.SmMHz.Value);
        Assert.Equal(11000, ra.MemoryMHz.Value);
        Assert.Equal(320.0, ra.PowerLimitW.Value);
        Assert.Equal(ThrottleReasons.SwPowerCap, ra.Throttle.Value);
        Assert.True(ra.DisplayActive.Value);
        Assert.Equal(100.5, r.Single(x => x.BusId == BusB).PowerW.Value);
        Assert.Equal(NvmlReturn.Success, reader.InitCode);
        Assert.Equal(1, fake.InitCalls);
    }

    [Fact]
    public void InitFails_PollIsEmpty()
    {
        var fake = new FakeNvml { InitResult = NvmlReturn.DriverNotLoaded };
        fake.Add(BusA);
        var reader = new NvmlReader(fake, AppLog.Null);
        Assert.Equal(NvmlReturn.DriverNotLoaded, reader.InitCode);
        Assert.Empty(reader.Poll(T0));
        Assert.Empty(reader.Cards);
    }

    [Fact]
    public void CardReturnsAtNewIndex_KeepsIdentity()
    {
        var (fake, reader, _) = Make();
        var a = fake.Add(BusA, "A");
        var b = fake.Add(BusB, "B");
        a.TempC = 50; b.TempC = 70;
        reader.Poll(T0);

        fake.Gpus.Remove(b);
        var r1 = reader.Poll(T0.AddSeconds(1));
        Assert.Single(r1);
        Assert.Equal(2, reader.Cards.Count);

        fake.Gpus.Insert(0, b);
        var r2 = reader.Poll(T0.AddSeconds(2));
        Assert.Equal(2, r2.Count);
        Assert.Equal(70, r2.Single(x => x.BusId == BusB).TempC.Value);
        Assert.Equal(50, r2.Single(x => x.BusId == BusA).TempC.Value);
        Assert.Equal(0, reader.Cards.Single(c => c.BusId == BusB).Index);
        Assert.Equal(1, reader.Cards.Single(c => c.BusId == BusA).Index);
        Assert.Equal(2, reader.Cards.Count);
    }

    [Fact]
    public void NotSupported_IsBlankAndLoggedOnce()
    {
        var (fake, reader, lines) = Make();
        var a = fake.Add(BusA, "RTX 5080");
        a.Fail["GetFanSpeed"] = NvmlReturn.NotSupported;

        for (var i = 0; i < 3; i++)
        {
            var r = reader.Poll(T0.AddSeconds(i)).Single();
            Assert.False(r.FanPct.Ok);
            Assert.Null(r.FanPct.Value);
            Assert.Equal(NvmlReturn.NotSupported, r.FanPct.Code);
            Assert.True(r.TempC.Ok);
        }
        var warns = lines.Where(l => l.Level == LogLevel.Warning).ToList();
        Assert.Single(warns);
        Assert.Equal("nvml", warns[0].Source);
        Assert.Equal($"RTX 5080 ({BusA}): FanPct failed: NVML_ERROR_NOT_SUPPORTED", warns[0].Message);

        a.Fail.Clear();
        Assert.True(reader.Poll(T0.AddSeconds(3)).Single().FanPct.Ok);
        a.Fail["GetFanSpeed"] = NvmlReturn.NotSupported;
        reader.Poll(T0.AddSeconds(4));
        reader.Poll(T0.AddSeconds(5));
        Assert.Equal(2, lines.Count(l => l.Level == LogLevel.Warning));
    }

    [Fact]
    public void GpuLost_CardHasNoReading()
    {
        var (fake, reader, _) = Make();
        fake.Add(BusA, "A");
        var b = fake.Add(BusB, "B");
        Assert.Equal(2, reader.Poll(T0).Count);

        b.Fail["GetName"] = NvmlReturn.GpuIsLost;
        var r = reader.Poll(T0.AddSeconds(1));
        Assert.Single(r);
        Assert.Equal(BusA, r[0].BusId);
        Assert.Equal(2, reader.Cards.Count);

        b.Fail.Clear();
        b.Fail["GetHandle"] = NvmlReturn.GpuNotFound;
        Assert.Single(reader.Poll(T0.AddSeconds(2)));
        Assert.Equal(2, reader.Cards.Count);

        b.Fail.Clear();
        b.Fail["GetBusId"] = NvmlReturn.GpuIsLost;
        Assert.Single(reader.Poll(T0.AddSeconds(3)));
        Assert.Equal(2, reader.Cards.Count);
    }

    [Fact]
    public void NewCard_AddedToCards()
    {
        var (fake, reader, _) = Make();
        fake.Add(BusA, "A");
        reader.Poll(T0);
        Assert.Single(reader.Cards);

        fake.Add(BusB, "B");
        reader.Poll(T0.AddSeconds(1));
        Assert.Equal(2, reader.Cards.Count);
        var b = reader.Cards.Single(c => c.BusId == BusB);
        Assert.Equal("B", b.Name);
        Assert.Equal("GPU-" + BusB, b.Uuid);
        Assert.Equal(1, b.Index);
    }

    [Fact]
    public void Timings_RecordedPerCall()
    {
        var (fake, reader, _) = Make();
        fake.Add(BusA);
        reader.Poll(T0);
        reader.Poll(T0.AddSeconds(1));
        var snap = reader.Timings.Snapshot();
        Assert.Equal(2, snap["GetTemperature"].Count);
        Assert.Equal(2, snap["GetDeviceCount"].Count);
        Assert.Equal(6, snap["GetClock"].Count);
        Assert.True(snap.ContainsKey("GetBusId"));
        Assert.True(snap.ContainsKey("GetHandle"));
    }
}
