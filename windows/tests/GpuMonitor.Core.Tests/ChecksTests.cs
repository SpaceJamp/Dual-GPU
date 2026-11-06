using GpuMonitor.Core.Diagnostics;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Readers;
using GpuMonitor.Core.Settings;
using Xunit;

namespace GpuMonitor.Core.Tests;

public class ChecksTests
{
    private static Check Get(FactsBuilder b, string id) => Assert.Single(Checks.Run(b.Build()), c => c.Id == id);

    private static void AssertPass(Check c) { Assert.Equal(CheckLevel.Pass, c.Level); Assert.Equal("", c.Hint); Assert.NotEmpty(c.Message); }

    [Fact]
    public void Run_ReturnsElevenInTableOrder()
    {
        var r = Checks.Run(new FactsBuilder().Build());
        Assert.Equal(new[] { "nvml-load", "card-count", "wmi-match", "display-card", "pcie-width", "temp-headroom",
            "throttle", "driver-errors", "settings", "log-writable", "poll-time" }, r.Select(c => c.Id));
        Assert.All(r, AssertPass);
    }

    [Fact]
    public void NvmlLoad_Failed_FailWithLoadError()
    {
        var b = new FactsBuilder { Driver = new(NvmlReturn.LibraryNotFound, null, "nvml.dll not found",
            Fact<string>.Fail("x"), Fact<string>.Fail("x"), Fact<string>.Fail("x")) };
        var c = Get(b, "nvml-load");
        Assert.Equal(CheckLevel.Fail, c.Level);
        Assert.Contains("nvml.dll not found", c.Message);
        Assert.Equal(@"Install or reinstall the NVIDIA driver. nvml.dll ships with it in C:\Windows\System32.", c.Hint);
    }

    [Fact]
    public void NvmlLoad_FailedNoLoadError_UsesCodeName()
    {
        var b = new FactsBuilder { Driver = new(NvmlReturn.DriverNotLoaded, null, null,
            Fact<string>.Fail("x"), Fact<string>.Fail("x"), Fact<string>.Fail("x")) };
        Assert.Contains("DriverNotLoaded", Get(b, "nvml-load").Message);
    }

    [Fact]
    public void CardCount_OneCard_Pass()
    {
        var b = new FactsBuilder(); b.Cards.RemoveAt(1);
        var c = Get(b, "card-count");
        Assert.Equal(CheckLevel.Pass, c.Level);
        Assert.Equal("1 card found", c.Message);
    }

    [Fact]
    public void CardCount_NoCards_Fail()
    {
        var b = new FactsBuilder(); b.Cards.Clear();
        var c = Get(b, "card-count");
        Assert.Equal(CheckLevel.Fail, c.Level);
        Assert.Equal("No NVIDIA cards found", c.Message);
        Assert.Contains("Device Manager", c.Hint);
    }

    [Fact]
    public void WmiMatch_OneCard_ReadsAsSingular()
    {
        var b = new FactsBuilder(); b.Cards.RemoveAt(1);
        b.Wmi = Fact<IReadOnlyList<WmiAdapter>>.Of([new WmiAdapter("NVIDIA GeForce RTX 5080", "1", "PCI", "OK")]);

        Assert.Equal("Windows and NVML both see 1 NVIDIA card", Get(b, "wmi-match").Message);
    }

    [Fact]
    public void WmiMatch_CountDiffers_Warn()
    {
        var b = new FactsBuilder { Wmi = Fact<IReadOnlyList<WmiAdapter>>.Of(new[] { new WmiAdapter("nvidia geforce", "1", "p", "OK") }) };
        var c = Get(b, "wmi-match");
        Assert.Equal(CheckLevel.Warn, c.Level);
        Assert.Equal("Windows and the NVIDIA driver disagree about the cards. Restart, then reinstall the driver with a clean install.", c.Hint);
    }

    [Fact]
    public void WmiMatch_IgnoresNonNvidiaAdapters()
    {
        var b = new FactsBuilder();
        b.Wmi = Fact<IReadOnlyList<WmiAdapter>>.Of(b.Wmi.Value!.Append(new WmiAdapter("Basic Display", "1", "p", "OK")).ToList());
        AssertPass(Get(b, "wmi-match"));
    }

    [Fact]
    public void WmiMatch_Unavailable_Info()
    {
        var b = new FactsBuilder { Wmi = Fact<IReadOnlyList<WmiAdapter>>.Fail("boom") };
        var c = Get(b, "wmi-match");
        Assert.Equal(CheckLevel.Info, c.Level);
        Assert.Equal("Windows adapter list not available", c.Message);
        Assert.Equal("Check that the Windows Management Instrumentation service is running.", c.Hint);
        b.Wmi = Fact<IReadOnlyList<WmiAdapter>>.Of(Array.Empty<WmiAdapter>());
        Assert.Equal(CheckLevel.Info, Get(b, "wmi-match").Level);
    }

    [Fact]
    public void DisplayCard_Is5080_Pass() => AssertPass(Get(new FactsBuilder(), "display-card"));

    [Fact]
    public void DisplayCard_Is4070_Warn()
    {
        var b = new FactsBuilder()
            .WithCard(0, c => c with { DisplayActive = Fact<bool>.Of(false) })
            .WithCard(1, c => c with { DisplayActive = Fact<bool>.Of(true) });
        var c = Get(b, "display-card");
        Assert.Equal(CheckLevel.Warn, c.Level);
        Assert.Contains(FactsBuilder.Card4070, c.Message);
        Assert.Equal("Plug the monitor into the RTX 5080, or change \"Expected display card\" in Settings.", c.Hint);
    }

    [Fact]
    public void DisplayCard_None_Warn()
    {
        var b = new FactsBuilder().WithCard(0, c => c with { DisplayActive = Fact<bool>.Of(false) });
        Assert.Equal(CheckLevel.Warn, Get(b, "display-card").Level);
    }

    [Fact]
    public void DisplayCard_Both_Warn()
    {
        var b = new FactsBuilder().WithCard(1, c => c with { DisplayActive = Fact<bool>.Of(true) });
        Assert.Equal(CheckLevel.Warn, Get(b, "display-card").Level);
    }

    [Fact]
    public void DisplayCard_ExpectedMatchIsCaseInsensitive()
    {
        var b = new FactsBuilder { Settings = new AppSettings { ExpectedDisplayCard = "rtx 5080" } };
        AssertPass(Get(b, "display-card"));
    }

    [Fact]
    public void PcieWidth_X8OfX16_Warn()
    {
        var b = new FactsBuilder().WithCard(1, c => c with { PcieWidthCur = Fact<uint>.Of(8) });
        var c = Get(b, "pcie-width");
        Assert.Equal(CheckLevel.Warn, c.Level);
        Assert.Equal("NVIDIA GeForce RTX 4070 Ti SUPER: x8 of x16", c.Message);
        Assert.Equal("Run a game or load first: some boards drop lanes at idle. If it stays low, check the slot and BIOS lane settings.", c.Hint);
    }

    [Fact]
    public void PcieWidth_BothLow_JoinsWithSemicolon()
    {
        var b = new FactsBuilder()
            .WithCard(0, c => c with { PcieWidthCur = Fact<uint>.Of(8) })
            .WithCard(1, c => c with { PcieWidthCur = Fact<uint>.Of(4) });
        Assert.Equal($"{FactsBuilder.Card5080}: x8 of x16; {FactsBuilder.Card4070}: x4 of x16", Get(b, "pcie-width").Message);
    }

    [Fact]
    public void PcieWidth_FactFailed_Skipped()
    {
        var b = new FactsBuilder().WithCard(1, c => c with { PcieWidthCur = Fact<uint>.Fail("NVML_ERROR_NOT_SUPPORTED") });
        AssertPass(Get(b, "pcie-width"));
    }

    [Fact]
    public void TempHeadroom_4Below_Warn()
    {
        var b = new FactsBuilder().WithCard(0, c => c with { TempC = Fact<uint>.Of(86) });
        var c = Get(b, "temp-headroom");
        Assert.Equal(CheckLevel.Warn, c.Level);
        Assert.Contains(FactsBuilder.Card5080, c.Message);
        Assert.Equal("Improve airflow or lower the power limit. The card will slow itself down at its slowdown limit.", c.Hint);
    }

    [Fact]
    public void TempHeadroom_Exactly5Below_Warn()
    {
        var b = new FactsBuilder().WithCard(0, c => c with { TempC = Fact<uint>.Of(85) });
        Assert.Equal(CheckLevel.Warn, Get(b, "temp-headroom").Level);
    }

    [Fact]
    public void TempHeadroom_6Below_Pass()
    {
        var b = new FactsBuilder().WithCard(0, c => c with { TempC = Fact<uint>.Of(84) });
        AssertPass(Get(b, "temp-headroom"));
    }

    [Fact]
    public void TempHeadroom_AboveSlowdown_NoUnderflow()
    {
        var b = new FactsBuilder().WithCard(0, c => c with { TempC = Fact<uint>.Of(95) });
        Assert.Equal(CheckLevel.Warn, Get(b, "temp-headroom").Level);
    }

    [Fact]
    public void Throttle_HwThermal_Warn()
    {
        var b = new FactsBuilder().WithCard(0, c => c with { Throttle = Fact<ThrottleReasons>.Of(ThrottleReasons.HwThermalSlowdown) });
        var c = Get(b, "throttle");
        Assert.Equal(CheckLevel.Warn, c.Level);
        Assert.Contains(FactsBuilder.Card5080, c.Message);
        Assert.Equal("Thermal throttling means the card is too hot. Power cap means it hit its power limit, which is normal under full load.", c.Hint);
    }

    [Fact]
    public void Throttle_PowerCap_Info()
    {
        var b = new FactsBuilder().WithCard(0, c => c with { Throttle = Fact<ThrottleReasons>.Of(ThrottleReasons.SwPowerCap) });
        Assert.Equal(CheckLevel.Info, Get(b, "throttle").Level);
    }

    [Fact]
    public void Throttle_PowerBrake_Info()
    {
        var b = new FactsBuilder().WithCard(0, c => c with { Throttle = Fact<ThrottleReasons>.Of(ThrottleReasons.HwPowerBrakeSlowdown) });
        Assert.Equal(CheckLevel.Info, Get(b, "throttle").Level);
    }

    [Fact]
    public void Throttle_ReliabilityBit_IsNamed()
    {
        // Seen on an RTX 5080 with driver 621.14: nvidia-smi says Reliability: Active, NVML returns 0x400.
        var b = new FactsBuilder().WithCard(0, c => c with { Throttle = Fact<ThrottleReasons>.Of((ThrottleReasons)0x400) });
        var check = Assert.Single(Checks.Run(b.Build()), c => c.Id == "throttle");
        Assert.Equal(CheckLevel.Info, check.Level);
        Assert.Contains("Reliability", check.Message);
    }

    [Fact]
    public void Throttle_OtherBit_Info()
    {
        var b = new FactsBuilder().WithCard(0, c => c with { Throttle = Fact<ThrottleReasons>.Of(ThrottleReasons.SyncBoost) });
        Assert.Equal(CheckLevel.Info, Get(b, "throttle").Level);
    }

    [Fact]
    public void Throttle_IdleOnly_Pass()
    {
        var b = new FactsBuilder().WithCard(0, c => c with { Throttle = Fact<ThrottleReasons>.Of(ThrottleReasons.GpuIdle) });
        AssertPass(Get(b, "throttle"));
    }

    [Fact]
    public void Throttle_WorstCardWins()
    {
        var b = new FactsBuilder()
            .WithCard(0, c => c with { Throttle = Fact<ThrottleReasons>.Of(ThrottleReasons.SwPowerCap) })
            .WithCard(1, c => c with { Throttle = Fact<ThrottleReasons>.Of(ThrottleReasons.SwThermalSlowdown) });
        var c = Get(b, "throttle");
        Assert.Equal(CheckLevel.Warn, c.Level);
        Assert.Contains(FactsBuilder.Card5080, c.Message);
        Assert.Contains(FactsBuilder.Card4070, c.Message);
        Assert.Contains("; ", c.Message);
    }

    [Fact]
    public void DriverErrors_OneEvent_Warn()
    {
        var at = new DateTimeOffset(2026, 11, 3, 14, 5, 0, TimeSpan.Zero);
        var b = new FactsBuilder { EventLog = Fact<IReadOnlyList<GpuEvent>>.Of(new[] { new GpuEvent(at, "nvlddmkm", 14, "Error", "reset") }) };
        var c = Get(b, "driver-errors");
        Assert.Equal(CheckLevel.Warn, c.Level);
        Assert.Equal("1 driver error(s) in the last 7 days, newest 2026-11-03 14:05", c.Message);
        Assert.Equal("Driver crashes are often caused by unstable overclocks, power supply limits or old drivers. Remove overclocks and update the driver.", c.Hint);
    }

    [Fact]
    public void DriverErrors_Unavailable_Info()
    {
        var b = new FactsBuilder { EventLog = Fact<IReadOnlyList<GpuEvent>>.Fail("denied") };
        var c = Get(b, "driver-errors");
        Assert.Equal(CheckLevel.Info, c.Level);
        Assert.Equal("Event log not available", c.Message);
        Assert.Equal("Check that the Windows Event Log service is running.", c.Hint);
    }

    [Fact]
    public void Settings_Missing_Info()
    {
        var c = Get(new FactsBuilder { SettingsStatus = SettingsStatus.Missing }, "settings");
        Assert.Equal(CheckLevel.Info, c.Level);
        Assert.Equal("No settings file yet. Defaults are in use until you save settings.", c.Hint);
    }

    [Fact]
    public void Settings_Invalid_Warn()
    {
        var c = Get(new FactsBuilder { SettingsStatus = SettingsStatus.Invalid }, "settings");
        Assert.Equal(CheckLevel.Warn, c.Level);
        Assert.Equal("Settings were reset to defaults. The bad file was kept as settings.json.bad.", c.Hint);
    }

    [Fact]
    public void LogWritable_False_Warn()
    {
        var c = Get(new FactsBuilder { LogWritable = false }, "log-writable");
        Assert.Equal(CheckLevel.Warn, c.Level);
        Assert.Equal(@"Check that %LOCALAPPDATA%\GpuMonitor\logs exists and is not read-only.", c.Hint);
    }

    [Fact]
    public void PollTime_150ms_Warn()
    {
        var b = new FactsBuilder { Timings = new() { ["a"] = new(1, 1, 100, 1, 1), ["b"] = new(1, 1, 50, 1, 1) } };
        var c = Get(b, "poll-time");
        Assert.Equal(CheckLevel.Warn, c.Level);
        Assert.Equal("NVML is slow to answer. Check the driver and the per-call timings in this report.", c.Hint);
    }

    [Fact]
    public void PollTime_Exactly100ms_Pass()
    {
        var b = new FactsBuilder { Timings = new() { ["a"] = new(1, 1, 60, 1, 1), ["b"] = new(1, 1, 40, 1, 1) } };
        AssertPass(Get(b, "poll-time"));
    }
}
