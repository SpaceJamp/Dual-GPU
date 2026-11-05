using System.Text.Json;
using GpuMonitor.Core.Logging;
using Xunit;

namespace GpuMonitor.Core.Tests;

public sealed class AppLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gpumonitor-test-" + Guid.NewGuid().ToString("N"));

    public AppLogTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private string LogPath => Path.Combine(_dir, "gpumonitor.log");

    [Fact]
    public void Line_HasRunIdAndLevel()
    {
        using var log = new AppLog(LogPath, LogLevel.Debug, false);
        LogLine? seen = null;
        log.Line += l => seen = l;
        log.Info("nvml", "hello");
        Assert.NotNull(seen);
        Assert.Equal(log.RunId, seen!.RunId);
        Assert.Equal(LogLevel.Info, seen.Level);
        Assert.Matches("^[0-9a-f]{8}$", log.RunId);
        var text = File.ReadAllText(LogPath);
        Assert.Contains($" {log.RunId} INFO    nvml: hello", text);
    }

    [Fact]
    public void BelowLevel_NotWritten()
    {
        using var log = new AppLog(LogPath, LogLevel.Warning, false);
        log.Info("a", "dropped");
        log.Warn("a", "kept");
        var text = File.ReadAllText(LogPath);
        Assert.DoesNotContain("dropped", text);
        Assert.Contains("kept", text);
    }

    [Fact]
    public void Level_ChangesAtRuntime()
    {
        using var log = new AppLog(LogPath, LogLevel.Error, false);
        log.Info("a", "first");
        log.Level = LogLevel.Debug;
        log.Debug("a", "second");
        var text = File.ReadAllText(LogPath);
        Assert.DoesNotContain("first", text);
        Assert.Contains("second", text);
    }

    [Fact]
    public void Json_ParsesPerLine()
    {
        using var log = new AppLog(LogPath, LogLevel.Debug, true);
        log.Error("poll", "bad", new InvalidOperationException("boom"));
        log.Info("poll", "ok");
        var lines = File.ReadAllLines(LogPath);
        Assert.Equal(2, lines.Length);
        using var doc = JsonDocument.Parse(lines[0]);
        var root = doc.RootElement;
        Assert.Equal("poll", root.GetProperty("source").GetString());
        Assert.Equal("bad", root.GetProperty("msg").GetString());
        Assert.Equal(log.RunId, root.GetProperty("run").GetString());
        Assert.Equal("ERROR", root.GetProperty("level").GetString());
        Assert.True(root.TryGetProperty("time", out _));
        Assert.Contains("boom", root.GetProperty("exception").GetString());
        using var doc2 = JsonDocument.Parse(lines[1]);
        Assert.True(doc2.RootElement.TryGetProperty("exception", out _));
    }

    [Fact]
    public void Rotate_At1MB_Keeps3()
    {
        using var log = new AppLog(LogPath, LogLevel.Debug, false);
        var msg = new string('x', 1000);
        for (var i = 0; i < 3500; i++) log.Info("rot", msg);
        foreach (var suffix in new[] { "", ".1", ".2", ".3" })
        {
            var p = LogPath + suffix;
            Assert.True(File.Exists(p), p);
            Assert.True(new FileInfo(p).Length <= 1_000_000, p);
        }
        Assert.False(File.Exists(LogPath + ".4"));
    }

    [Fact]
    public void Exception_IncludesStack()
    {
        using var log = new AppLog(LogPath, LogLevel.Debug, false);
        try { throw new InvalidOperationException("kaboom"); }
        catch (Exception ex) { log.Error("x", "failed", ex); }
        var text = File.ReadAllText(LogPath);
        Assert.Contains("failed", text);
        Assert.Contains("InvalidOperationException: kaboom", text);
        Assert.Contains("Exception_IncludesStack", text);
    }

    [Fact]
    public void WriteFailure_DoesNotThrow()
    {
        var afile = Path.Combine(_dir, "afile");
        File.WriteAllText(afile, "x");
        using var log = new AppLog(Path.Combine(afile, "gpumonitor.log"), LogLevel.Debug, false);
        var ex = Record.Exception(() => log.Error("x", "msg"));
        Assert.Null(ex);
        Assert.NotNull(log.LastWriteError);
    }

    [Fact]
    public void Tail_ReturnsLastN()
    {
        using var log = new AppLog(LogPath, LogLevel.Debug, false);
        for (var i = 0; i < 10; i++) log.Info("t", "m" + i);
        var tail = log.Tail(3);
        Assert.Equal(3, tail.Count);
        Assert.EndsWith("m7", tail[0]);
        Assert.EndsWith("m9", tail[2]);
    }

    [Fact]
    public void NullPath_FiresLineAndTailsFromMemory()
    {
        using var log = new AppLog(null, LogLevel.Info, false);
        var count = 0;
        log.Line += _ => count++;
        for (var i = 0; i < 600; i++) log.Info("t", "m" + i);
        log.Debug("t", "skipped");
        Assert.Equal(600, count);
        var tail = log.Tail(1000);
        Assert.Equal(500, tail.Count);
        Assert.EndsWith("m599", tail[^1]);
    }

    [Fact]
    public void Null_IsNoOp()
    {
        var fired = false;
        AppLog.Null.Line += _ => fired = true;
        AppLog.Null.Error("a", "b", new Exception("c"));
        Assert.False(fired);
        Assert.Empty(AppLog.Null.Tail(10));
    }
}
