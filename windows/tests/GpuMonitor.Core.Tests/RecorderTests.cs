using System.Globalization;
using GpuMonitor.Core.Alerts;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Poll;
using Xunit;

namespace GpuMonitor.Core.Tests;

public sealed class RecorderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpumonitor-rec-" + Guid.NewGuid().ToString("N"), "sub");
    private static readonly Card A = new("00000000:01:00.0", "RTX 5080", "u1", 0);
    private static readonly Card B = new("00000000:02:00.0", "RTX 4070 Ti SUPER", "u2", 1);

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_dir)!, true); } catch (IOException) { }
    }

    private static PollResult Result(DateTimeOffset at, params Reading[] rs) =>
        new(at, new[] { A, B }, rs, Array.Empty<Alert>(), TimeSpan.Zero);

    [Fact]
    public void Recorder_HeaderAndRows()
    {
        var rec = new Recorder(_dir);
        rec.Start(Make.T0);
        Assert.True(rec.IsRecording);
        Assert.EndsWith("2026-11-05-120000.csv", rec.CurrentFile);
        for (int i = 0; i < 2; i++)
        {
            var at = Make.T0.AddSeconds(i);
            rec.Write(Result(at, Make.Reading(A.BusId, at), Make.Reading(B.BusId, at)));
        }
        var file = rec.CurrentFile!;
        rec.Stop();
        Assert.False(rec.IsRecording);
        var lines = File.ReadAllLines(file);
        Assert.Equal(5, lines.Length);
        Assert.Equal("time,bus_id,name,load_pct,vram_used_mib,vram_total_mib,temp_c,power_w,power_limit_w,graphics_mhz,memory_mhz,sm_mhz,fan_pct,pstate,throttle,display_active", lines[0]);
        Assert.Contains("00000000:01:00.0,RTX 5080,10,1000,16000,50,100,320,1500,9000,1500,30,2,4,true", lines[1]);
        Assert.Contains("RTX 4070 Ti SUPER", lines[2]);
    }

    [Fact]
    public void Recorder_FailedFieldEmpty()
    {
        var rec = new Recorder(_dir);
        rec.Start(Make.T0);
        var r = Make.Reading(A.BusId, Make.T0) with
        {
            TempC = Field<int>.Fail(NvmlReturn.Unknown),
            Throttle = Field<ThrottleReasons>.Fail(NvmlReturn.Unknown),
            DisplayActive = Field<bool>.Of(false),
        };
        rec.Write(Result(Make.T0, r));
        var file = rec.CurrentFile!;
        rec.Stop();
        var cells = File.ReadAllLines(file)[1].Split(',');
        Assert.Equal("", cells[6]);
        Assert.Equal("", cells[14]);
        Assert.Equal("false", cells[15]);
        Assert.Equal("100", cells[7]);
    }

    [Fact]
    public void Recorder_InvariantCulture()
    {
        var old = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var rec = new Recorder(_dir);
            rec.Start(Make.T0);
            rec.Write(Result(Make.T0, Make.Reading(A.BusId, Make.T0, power: 285.5)));
            var file = rec.CurrentFile!;
            rec.Stop();
            Assert.Contains(",285.5,", File.ReadAllText(file));
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void WriteAndStop_WhenNotRecording_AreNoOps()
    {
        var rec = new Recorder(_dir);
        rec.Write(Result(Make.T0, Make.Reading(A.BusId, Make.T0)));
        rec.Stop();
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public void StartWhileRecording_StopsOldFile()
    {
        var rec = new Recorder(_dir);
        rec.Start(Make.T0);
        var first = rec.CurrentFile!;
        rec.Start(Make.T0.AddSeconds(5));
        Assert.NotEqual(first, rec.CurrentFile);
        File.ReadAllText(first); // not locked for reading
        rec.Stop();
    }

    [Fact]
    public void Quoting_ForCommasAndQuotes()
    {
        var rec = new Recorder(_dir);
        rec.Start(Make.T0);
        var odd = new Card(A.BusId, "Foo, \"Bar\"", "u", 0);
        rec.Write(new PollResult(Make.T0, new[] { odd }, new[] { Make.Reading(A.BusId, Make.T0) }, Array.Empty<Alert>(), TimeSpan.Zero));
        var file = rec.CurrentFile!;
        rec.Stop();
        Assert.Contains("\"Foo, \"\"Bar\"\"\"", File.ReadAllText(file));
    }
}
