using System.Text.Json;
using GpuMonitor.Core.Diagnostics;
using Xunit;

namespace GpuMonitor.Core.Tests;

public class ReportRenderTests
{
    private static readonly Redactor R = new("Jeff", "PC-1", @"C:\Users\Jeff");

    [Fact]
    public void Text_ChecksFirst()
    {
        var f = new FactsBuilder().Build();
        var checks = new List<Check> { new(CheckLevel.Fail, "card-count", "Only one card", "Reseat it"), new(CheckLevel.Pass, "nvml-load", "NVML loaded", "") };
        var text = ReportRender.Text(f, checks, redactor: R);
        Assert.StartsWith("GPU Monitor diagnostic report\nCollected 1970-01-01T00:00:00.0000000+00:00\n\nChecks\n[FAIL] card-count: Only one card\n       Reseat it\n[PASS] nvml-load: NVML loaded\n\nApp\n", text);
        Assert.True(text.IndexOf("Card 0 (00000000:01:00.0)") > text.IndexOf("[PASS]"));
        Assert.Contains("\nnvidia-smi\n  ok\n", text);
        Assert.Contains("\nState\n", text);
    }

    [Fact]
    public void Text_RedactsAndShowsErrors()
    {
        var b = new FactsBuilder();
        b.Driver = b.Driver with { NvmlPath = @"C:\Users\Jeff\nvml.dll", DriverVersion = Core.Diagnostics.Fact<string>.Fail("NVML_ERROR_X") };
        var text = ReportRender.Text(b.Build(), Array.Empty<Check>(), redactor: R);
        Assert.Contains(@"nvml_path: <home>\nvml.dll", text);
        Assert.Contains("driver_version: error: NVML_ERROR_X", text);
        Assert.Contains(@"C:\Users\Jeff\nvml.dll", ReportRender.Text(b.Build(), Array.Empty<Check>(), redact: false));
    }

    [Fact]
    public void Json_RoundTrips()
    {
        var f = new FactsBuilder().Build();
        var checks = Checks.Run(f);
        Assert.Equal(11, checks.Count);
        using var doc = JsonDocument.Parse(ReportRender.Json(f, checks, redactor: R));
        Assert.Equal(11, doc.RootElement.GetProperty("checks").GetArrayLength());
        var facts = doc.RootElement.GetProperty("facts");
        Assert.Equal("572.83", facts.GetProperty("driver").GetProperty("driver_version").GetString());
        Assert.Equal("01:00:00", facts.GetProperty("windows").GetProperty("uptime").GetString());
        Assert.Equal(2, facts.GetProperty("cards").GetArrayLength());
    }

    [Fact]
    public void Json_FailedFactIsErrorString()
    {
        var b = new FactsBuilder();
        b.Cards[0] = b.Cards[0] with { TempC = Fact<uint>.Fail("NVML_ERROR_NOT_SUPPORTED") };
        using var doc = JsonDocument.Parse(ReportRender.Json(b.Build(), Array.Empty<Check>(), redactor: R));
        Assert.Equal("error: NVML_ERROR_NOT_SUPPORTED", doc.RootElement.GetProperty("facts").GetProperty("cards")[0].GetProperty("temp_c").GetString());
    }

    [Fact]
    public void ExitCode_OneOnFail()
    {
        Assert.Equal(1, ReportRender.ExitCode(new[] { new Check(CheckLevel.Warn, "a", "", ""), new Check(CheckLevel.Fail, "b", "", "") }));
        Assert.Equal(0, ReportRender.ExitCode(new[] { new Check(CheckLevel.Warn, "a", "", "") }));
    }

    private static Facts Dirty(string smi, string eventMsg, string prefKey)
    {
        var b = new FactsBuilder();
        b.EventLog = Fact<IReadOnlyList<GpuEvent>>.Of(new[] { new GpuEvent(DateTimeOffset.UnixEpoch, "p", 1, "Error", eventMsg) });
        var f = b.Build();
        return f with
        {
            NvidiaSmi = Fact<string?>.Of(smi),
            GpuPreferences = Fact<IReadOnlyDictionary<string, string>>.Of(new Dictionary<string, string> { [prefKey] = "GpuPreference=2;" }),
        };
    }

    [Fact]
    public void Json_RedactsHomeInNvidiaSmiAndEventLog()
    {
        var f = Dirty(@"C:\Users\Jeff\AppData\x.exe", "Account Name:\t\tJeff", @"C:\Users\Jeff\game.exe");
        var json = ReportRender.Json(f, Array.Empty<Check>(), redactor: R);
        Assert.DoesNotContain("Jeff", json);
        Assert.DoesNotContain(@"Users\\Jeff", json);
        Assert.Contains("<home>", json);
        Assert.Contains("<user>", json);
        using var doc = JsonDocument.Parse(json);
        var facts = doc.RootElement.GetProperty("facts");
        Assert.Equal("Account Name:\t\t<user>", facts.GetProperty("event_log")[0].GetProperty("message").GetString());
        Assert.Equal(@"<home>\game.exe", facts.GetProperty("gpu_preferences").EnumerateObject().Single().Name);
    }

    [Fact]
    public void Json_RedactsNameAfterNewline()
    {
        var json = ReportRender.Json(Dirty("x\nJeff y", "m", "k"), Array.Empty<Check>(), redactor: R);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("x\n<user> y", doc.RootElement.GetProperty("facts").GetProperty("nvidia_smi").GetString());
    }
}
