using Xunit;

namespace GpuMonitor.App.Tests;

public class CommandLineTests
{
    [Fact]
    public void Parse_NoArgs_StartsWindow()
    {
        Assert.Equal(new Options(false, null, false, false, false, false, null), CommandLine.Parse([]));
    }

    [Fact]
    public void Parse_Diagnose_Output_Json()
    {
        var o = CommandLine.Parse(["--diagnose", "-o", "r.json", "--json", "--no-redact"]);

        Assert.True(o.Diagnose);
        Assert.Equal("r.json", o.Output);
        Assert.True(o.Json);
        Assert.True(o.NoRedact);
        Assert.Null(o.Error);
    }

    [Theory]
    [InlineData("--tray")]
    [InlineData("--crash-test")]
    public void Parse_Flags(string flag)
    {
        var o = CommandLine.Parse([flag]);

        Assert.Equal(flag == "--tray", o.Tray);
        Assert.Equal(flag == "--crash-test", o.CrashTest);
        Assert.Null(o.Error);
    }

    [Fact]
    public void Parse_Unknown_Error()
    {
        Assert.Equal("Unknown option: --nope", CommandLine.Parse(["--tray", "--nope"]).Error);
    }

    [Fact]
    public void Parse_OutputWithoutValue_Error()
    {
        Assert.Equal("-o needs a file name.", CommandLine.Parse(["--diagnose", "-o"]).Error);
    }

    [Theory]
    [InlineData("--json")]
    [InlineData("--no-redact")]
    public void Parse_ReportOptionWithoutDiagnose_Error(string flag)
    {
        Assert.Equal(flag + " works only with --diagnose.", CommandLine.Parse([flag]).Error);
    }
}
