using GpuMonitor.Core.Logging;
using GpuMonitor.Core.Settings;
using Xunit;

namespace GpuMonitor.Core.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpumonitor-test-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new(null, LogLevel.Debug, false);

    public SettingsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string FilePath => Path.Combine(_dir, "settings.json");
    private SettingsStore Store() => new(FilePath, _log);

    [Fact]
    public void Missing_ReturnsDefaults()
    {
        var r = Store().Load();
        Assert.Equal(SettingsStatus.Missing, r.Status);
        Assert.Equal(1.0, r.Settings.RefreshSeconds);
        Assert.Equal(80, r.Settings.HeatLimitC);
        Assert.Equal(90, r.Settings.VramLimitPct);
        Assert.False(r.Settings.StartWithWindows);
        Assert.Equal("RTX 5080", r.Settings.ExpectedDisplayCard);
    }

    [Fact]
    public void RoundTrip()
    {
        var s = new AppSettings
        {
            RefreshSeconds = 2.5, HeatLimitC = 70, VramLimitPct = 75, LogLevel = LogLevel.Debug,
            LogJson = true, StartWithWindows = true, ExpectedDisplayCard = "RTX 4070 Ti SUPER",
        };
        Store().Save(s);
        var r = Store().Load();
        Assert.Equal(SettingsStatus.Loaded, r.Status);
        Assert.Equal(s, r.Settings);
    }

    [Fact]
    public void OutOfRange_ClampsPerField()
    {
        File.WriteAllText(FilePath, """{"RefreshSeconds": 0, "HeatLimitC": 500, "VramLimitPct": 10}""");
        var lines = new List<LogLine>();
        _log.Line += lines.Add;
        var r = Store().Load();
        Assert.Equal(SettingsStatus.Loaded, r.Status);
        Assert.Equal(0.5, r.Settings.RefreshSeconds);
        Assert.Equal(95, r.Settings.HeatLimitC);
        Assert.Equal(50, r.Settings.VramLimitPct);
        Assert.Equal(3, lines.Count(l => l.Level == LogLevel.Warning));
    }

    [Fact]
    public void WrongType_FallsBackPerField()
    {
        File.WriteAllText(FilePath, """{"HeatLimitC": "hot", "VramLimitPct": 85}""");
        var lines = new List<LogLine>();
        _log.Line += lines.Add;
        var r = Store().Load();
        Assert.Equal(SettingsStatus.Loaded, r.Status);
        Assert.Equal(80, r.Settings.HeatLimitC);
        Assert.Equal(85, r.Settings.VramLimitPct);
        Assert.Contains(lines, l => l.Level == LogLevel.Warning && l.Source == "settings" && l.Message.Contains("HeatLimitC"));
    }

    [Fact]
    public void UnknownLogLevel_FallsBackWithWarning()
    {
        File.WriteAllText(FilePath, """{"LogLevel": "Loud", "Extra": 1}""");
        var lines = new List<LogLine>();
        _log.Line += lines.Add;
        var r = Store().Load();
        Assert.Equal(LogLevel.Info, r.Settings.LogLevel);
        Assert.Contains(lines, l => l.Level == LogLevel.Warning && l.Message.Contains("LogLevel"));
    }

    [Fact]
    public void LogLevel_StoredAsString()
    {
        Store().Save(new AppSettings { LogLevel = LogLevel.Warning });
        Assert.Contains("\"LogLevel\": \"Warning\"", File.ReadAllText(FilePath));
    }

    [Fact]
    public void BlankExpectedCard_ReplacedByDefault()
    {
        File.WriteAllText(FilePath, """{"ExpectedDisplayCard": "  "}""");
        Assert.Equal("RTX 5080", Store().Load().Settings.ExpectedDisplayCard);
    }

    [Fact]
    public void Corrupt_RenamedToBad()
    {
        File.WriteAllText(FilePath, "{not json");
        File.WriteAllText(FilePath + ".bad", "older");
        var r = Store().Load();
        Assert.Equal(SettingsStatus.Invalid, r.Status);
        Assert.NotNull(r.Error);
        Assert.Equal(new AppSettings(), r.Settings);
        Assert.True(File.Exists(FilePath + ".bad"));
        Assert.Equal("{not json", File.ReadAllText(FilePath + ".bad"));
        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Save_IsAtomic()
    {
        Store().Save(new AppSettings());
        Store().Save(new AppSettings { HeatLimitC = 60 });
        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Save_CreatesFolder()
    {
        var nested = Path.Combine(_dir, "a", "b", "settings.json");
        new SettingsStore(nested, _log).Save(new AppSettings());
        Assert.True(File.Exists(nested));
    }
}

public sealed class PathsTests : IDisposable
{
    public void Dispose() => Paths.Root = null;

    [Fact]
    public void Root_Overrides_AllLocations()
    {
        Paths.Root = Path.Combine(Path.GetTempPath(), "gm-root");
        var root = Paths.Root;
        Assert.Equal(Path.Combine(root, "GpuMonitor", "settings.json"), Paths.SettingsFile);
        Assert.Equal(Path.Combine(root, "GpuMonitor", "logs"), Paths.LogDir);
        Assert.Equal(Path.Combine(root, "GpuMonitor", "logs", "crash"), Paths.CrashDir);
        Assert.Equal(Path.Combine(root, "GpuMonitor", "recordings"), Paths.RecordingDir);
        Assert.Equal(Path.Combine(root, "GpuMonitor", "logs", "gpumonitor.log"), Paths.LogFile);
    }
}
