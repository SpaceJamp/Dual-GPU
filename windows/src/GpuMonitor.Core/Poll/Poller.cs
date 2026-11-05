using System.Diagnostics;
using System.Globalization;
using GpuMonitor.Core.Alerts;
using GpuMonitor.Core.Logging;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Readers;
using GpuMonitor.Core.Settings;

namespace GpuMonitor.Core.Poll;

public sealed class Poller : IDisposable
{
    private const string Source = "poll";
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan SlowPoll = TimeSpan.FromMilliseconds(100);

    private readonly ICardReader _reader;
    private readonly AlertRules _rules;
    private readonly History _history;
    private readonly Func<AppSettings> _settings;
    private readonly AppLog _log;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private Task? _loop;
    private Task? _inFlight;
    private volatile bool _stopping;
    private int _disposed;
    private int _running;
    private int _skipped;

    public Poller(ICardReader reader, AlertRules rules, History history, Func<AppSettings> settings, AppLog log, TimeProvider clock)
    {
        _reader = reader;
        _rules = rules;
        _history = history;
        _settings = settings;
        _log = log;
        _clock = clock;
    }

    public event Action<PollResult>? Polled;

    public int Skipped => Volatile.Read(ref _skipped);

    public void Start()
    {
        lock (_gate)
        {
            if (_loop is not null || _stopping) return;
            var ct = _cts.Token;
            _loop = Task.Run(() => RunLoop(ct));
        }
    }

    private async Task RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            PeriodicTimer? timer = null;
            try
            {
                double seconds = _settings().RefreshSeconds;
                timer = new PeriodicTimer(TimeSpan.FromSeconds(seconds), _clock);
                while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    Tick();
                    if (_settings().RefreshSeconds != seconds) break;
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.Error(Source, "poll loop failed", ex);
                try { await Task.Delay(RetryDelay, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
            finally { timer?.Dispose(); }
        }
    }

    internal bool Tick()
    {
        if (_stopping) return false;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            Interlocked.Increment(ref _skipped);
            _log.Debug(Source, "poll skipped: previous poll still running");
            return false;
        }
        lock (_gate)
        {
            if (_stopping)
            {
                Volatile.Write(ref _running, 0);
                return false;
            }
            _inFlight = Task.Run(() =>
            {
                try { if (!_stopping) PollOnce(); }
                finally { Volatile.Write(ref _running, 0); }
            });
        }
        return true;
    }

    public PollResult PollOnce()
    {
        PollResult result;
        try
        {
            var now = _clock.GetUtcNow();
            var sw = Stopwatch.StartNew();
            var readings = _reader.Poll(now);
            var settings = _settings();
            var alerts = _rules.Evaluate(_reader.Cards, readings, now, settings);
            foreach (var r in readings) _history.Add(r, settings.RefreshSeconds);
            sw.Stop();
            result = new PollResult(now, _reader.Cards, readings, alerts, sw.Elapsed);
            if (sw.Elapsed > SlowPoll) LogSlow(sw.Elapsed);
        }
        catch (Exception ex)
        {
            _log.Error(Source, "poll failed", ex);
            return new PollResult(_clock.GetUtcNow(), Array.Empty<Card>(), Array.Empty<Reading>(), Array.Empty<Alert>(), TimeSpan.Zero);
        }

        if (_stopping) return result;
        try { Polled?.Invoke(result); }
        catch (Exception ex) { _log.Error(Source, "Polled subscriber failed", ex); }
        return result;
    }

    private void LogSlow(TimeSpan took)
    {
        var inv = CultureInfo.InvariantCulture;
        var msg = string.Create(inv, $"slow poll: {took.TotalMilliseconds:0} ms");
        foreach (var (call, st) in _reader.Timings.Snapshot().OrderBy(k => k.Key, StringComparer.Ordinal))
            msg += string.Create(inv, $" {call} {st.AvgMs:0.0} ms");
        _log.Warn(Source, msg);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        Task?[] pending;
        lock (_gate)
        {
            _stopping = true;
            pending = new[] { _loop, _inFlight };
        }
        _cts.Cancel();
        bool finished = true;
        try { finished = Task.WaitAll(pending.OfType<Task>().ToArray(), TimeSpan.FromSeconds(2)); }
        catch (AggregateException) { }
        if (finished) _cts.Dispose();
    }
}
