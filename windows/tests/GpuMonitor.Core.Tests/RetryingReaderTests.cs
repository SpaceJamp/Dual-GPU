using GpuMonitor.Core.Logging;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Readers;
using Xunit;

namespace GpuMonitor.Core.Tests;

public class RetryingReaderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static (AppLog log, List<LogLine> lines) Log()
    {
        var log = new AppLog(null, LogLevel.Debug, false);
        var lines = new List<LogLine>();
        log.Line += lines.Add;
        return (log, lines);
    }

    [Fact]
    public void LibraryMissing_ReportsErrorAndReadsNothing()
    {
        var (log, _) = Log();
        var reader = new RetryingReader(() => new NvmlLoad(null, null, "nvml.dll not found"), log, T0);

        Assert.Equal(NvmlReturn.LibraryNotFound, reader.InitCode);
        Assert.Equal("nvml.dll not found", reader.LoadError);
        Assert.Null(reader.Nvml);
        Assert.Empty(reader.Poll(T0));
        Assert.Empty(reader.Cards);
    }

    [Fact]
    public void Retries_OnlyEvery30Seconds()
    {
        var (log, _) = Log();
        var loads = 0;
        var reader = new RetryingReader(() => { loads++; return new NvmlLoad(null, null, "gone"); }, log, T0);
        Assert.Equal(1, loads);

        reader.Poll(T0.AddSeconds(29));
        Assert.Equal(1, loads);
        reader.Poll(T0.AddSeconds(30));
        Assert.Equal(2, loads);
        reader.Poll(T0.AddSeconds(59));
        Assert.Equal(2, loads);
    }

    [Fact]
    public void Failure_LoggedOnceUntilItChanges()
    {
        var (log, lines) = Log();
        var error = "gone";
        var reader = new RetryingReader(() => new NvmlLoad(null, null, error), log, T0);
        reader.Poll(T0.AddSeconds(30));
        reader.Poll(T0.AddSeconds(60));
        Assert.Single(lines, l => l.Level == LogLevel.Warning && l.Message.Contains("gone"));

        error = "still broken";
        reader.Poll(T0.AddSeconds(90));
        Assert.Single(lines, l => l.Level == LogLevel.Warning && l.Message.Contains("still broken"));
    }

    [Fact]
    public void InitFailure_KeepsCodeAndRetries()
    {
        var (log, lines) = Log();
        var fake = new FakeNvml { InitResult = NvmlReturn.DriverNotLoaded };
        fake.Add("00000000:01:00.0", "RTX 5080");
        var reader = new RetryingReader(() => new NvmlLoad(fake, "nvml.dll", null), log, T0);

        Assert.Equal(NvmlReturn.DriverNotLoaded, reader.InitCode);
        Assert.Null(reader.Nvml);
        Assert.Empty(reader.Poll(T0));
        Assert.Contains(lines, l => l.Level == LogLevel.Warning && l.Message.Contains("NVML_ERROR_DRIVER_NOT_LOADED"));

        fake.InitResult = NvmlReturn.Success;
        Assert.Single(reader.Poll(T0.AddSeconds(30)));
        Assert.Equal(NvmlReturn.Success, reader.InitCode);
        Assert.Null(reader.LoadError);
        Assert.Same(fake, reader.Nvml);
        Assert.Equal("nvml.dll", reader.LoadedPath);
        Assert.Single(reader.Cards);
        Assert.Contains(lines, l => l.Level == LogLevel.Info && l.Message.Contains("NVML loaded"));
    }

    [Fact]
    public void LoaderThrowing_CountsAsFailure()
    {
        var (log, _) = Log();
        var reader = new RetryingReader(() => throw new DllNotFoundException("boom"), log, T0);

        Assert.Equal(NvmlReturn.LibraryNotFound, reader.InitCode);
        Assert.Contains("boom", reader.LoadError);
    }

    [Fact]
    public void Dispose_ShutsDownOnce()
    {
        var (log, _) = Log();
        var fake = new FakeNvml();
        var reader = new RetryingReader(() => new NvmlLoad(fake, "nvml.dll", null), log, T0);

        reader.Dispose();
        reader.Dispose();

        Assert.Equal(1, fake.ShutdownCalls);
    }
}
