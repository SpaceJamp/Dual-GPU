using GpuMonitor.Core.Logging;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;

namespace GpuMonitor.Core.Readers;

/// <summary>What one attempt to load NVML found: the library, or why there is none.</summary>
public sealed record NvmlLoad(INvml? Nvml, string? Path, string? Error);

/// <summary>Reads through <see cref="NvmlReader"/> once NVML is available. While it is not, polls read nothing
/// and the load is tried again every 30 seconds, so a driver installed later is picked up without a restart.</summary>
public sealed class RetryingReader : ICardReader, IDisposable
{
    private const string Source = "nvml";
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(30);

    private readonly Func<NvmlLoad> _load;
    private readonly AppLog _log;
    private readonly CallTimings _noTimings = new();
    private volatile NvmlReader? _reader;
    private volatile INvml? _nvml;
    private volatile string? _path;
    private volatile string? _error;
    private volatile NvmlReturn _code;
    private string? _lastLogged;
    private DateTimeOffset _lastTry;
    private int _disposed;

    public RetryingReader(Func<NvmlLoad> load, AppLog log, DateTimeOffset now)
    {
        _load = load;
        _log = log;
        Try(now);
    }

    public NvmlReturn InitCode => _code;

    /// <summary>Why the library did not load; null once it did or when only Init failed.</summary>
    public string? LoadError => _error;

    /// <summary>The initialized library; null until NVML works.</summary>
    public INvml? Nvml => _nvml;

    public string? LoadedPath => _path;

    public IReadOnlyList<Card> Cards => _reader?.Cards ?? Array.Empty<Card>();

    public CallTimings Timings => _reader?.Timings ?? _noTimings;

    public IReadOnlyList<Reading> Poll(DateTimeOffset now)
    {
        if (_reader is null && now - _lastTry >= RetryEvery) Try(now);
        return _reader?.Poll(now) ?? Array.Empty<Reading>();
    }

    private void Try(DateTimeOffset now)
    {
        _lastTry = now;
        NvmlLoad load;
        try { load = _load(); }
        catch (Exception ex) { load = new NvmlLoad(null, null, ex.GetType().Name + ": " + ex.Message); }

        if (load.Nvml is null)
        {
            _code = NvmlReturn.LibraryNotFound;
            _error = load.Error;
            LogFailure("NVML did not load: " + load.Error);
            return;
        }

        var reader = new NvmlReader(load.Nvml, _log);
        _code = reader.InitCode;
        _path = load.Path;
        _error = null;
        if (reader.InitCode != NvmlReturn.Success)
        {
            LogFailure("NVML init failed: " + reader.InitCode.NvmlReturnName());
            return;
        }

        _nvml = load.Nvml;
        _reader = reader;
        _log.Info(Source, "NVML loaded from " + load.Path);
    }

    private void LogFailure(string message)
    {
        if (message == _lastLogged) return;
        _lastLogged = message;
        _log.Warn(Source, message);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _nvml?.Shutdown();
    }
}
