using GpuMonitor.Core.Alerts;
using GpuMonitor.Core.Logging;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Poll;
using GpuMonitor.Core.Settings;
using Xunit;

namespace GpuMonitor.Core.Tests;

public sealed class PollerTests
{
    private static readonly Card CardA = new("00000000:01:00.0", "RTX 5080", "u1", 0);

    private static (Poller p, StubReader r, History h, AppLog log, List<LogLine> lines, ManualClock clock) Build(AppSettings? s = null)
    {
        var r = new StubReader { Cards = new[] { CardA } };
        var h = new History();
        var log = new AppLog(null, LogLevel.Debug, false);
        var lines = new List<LogLine>();
        log.Line += l => { lock (lines) lines.Add(l); };
        var clock = new ManualClock(Make.T0);
        var settings = s ?? new AppSettings();
        var p = new Poller(r, new AlertRules(), h, () => settings, log, clock);
        return (p, r, h, log, lines, clock);
    }

    [Fact]
    public void PollOnce_PassesReadingsToRulesAndHistory()
    {
        var (p, r, h, _, _, _) = Build();
        r.OnPoll = now => new[] { Make.Reading(CardA.BusId, now, temp: 85) };
        PollResult? seen = null;
        p.Polled += x => seen = x;
        var res = p.PollOnce();
        Assert.Equal(Make.T0, res.At);
        Assert.Single(res.Readings);
        Assert.Contains(res.Alerts, a => a.Kind == AlertKind.Hot);
        Assert.Single(h.For(CardA.BusId));
        Assert.Same(res, seen);
        Assert.Equal(CardA, Assert.Single(res.Cards));
    }

    [Fact]
    public async Task Overlap_IsSkipped()
    {
        var (p, r, _, _, lines, _) = Build();
        var entered = new SemaphoreSlim(0);
        var gate = new SemaphoreSlim(0);
        var done = new SemaphoreSlim(0);
        r.OnPoll = now => { entered.Release(); gate.Wait(); return Array.Empty<Reading>(); };
        p.Polled += _ => done.Release();
        Assert.True(p.Tick());
        Assert.True(await entered.WaitAsync(5000, TestContext.Current.CancellationToken));
        Assert.False(p.Tick());
        Assert.Equal(1, p.Skipped);
        lock (lines) Assert.Contains(lines, l => l.Level == LogLevel.Debug && l.Source == "poll" && l.Message == "poll skipped: previous poll still running");
        gate.Release();
        Assert.True(await done.WaitAsync(5000, TestContext.Current.CancellationToken));
        // flag clears just after Polled; wait for next tick to be accepted
        var ok = SpinWait.SpinUntil(() => { if (!p.Tick()) return false; return true; }, 5000);
        Assert.True(ok);
        gate.Release();
    }

    [Fact]
    public void SlowPoll_LogsWarning()
    {
        var (p, r, _, _, lines, _) = Build();
        r.OnPoll = now =>
        {
            Thread.Sleep(150);
            r.Timings.Record("GetTemperature", TimeSpan.FromMilliseconds(120));
            return Array.Empty<Reading>();
        };
        p.PollOnce();
        lock (lines)
        {
            var w = Assert.Single(lines, l => l.Level == LogLevel.Warning);
            Assert.Equal("poll", w.Source);
            Assert.StartsWith("slow poll: ", w.Message);
            Assert.Contains("GetTemperature 120.0 ms", w.Message);
        }
    }

    [Fact]
    public void PollException_Logged_ContinuesNext()
    {
        var (p, r, _, _, lines, _) = Build();
        int n = 0;
        r.OnPoll = now => n++ == 0 ? throw new InvalidOperationException("boom") : new[] { Make.Reading(CardA.BusId, now) };
        int raised = 0;
        p.Polled += _ => raised++;
        var bad = p.PollOnce();
        Assert.Empty(bad.Readings);
        Assert.Empty(bad.Alerts);
        Assert.Equal(0, raised);
        lock (lines)
        {
            var e = Assert.Single(lines, l => l.Level == LogLevel.Error);
            Assert.Equal("poll", e.Source);
            Assert.Contains("boom", e.Exception);
        }
        Assert.Single(p.PollOnce().Readings);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void ThrowingSubscriber_IsCaught()
    {
        var (p, r, _, _, lines, _) = Build();
        p.Polled += _ => throw new InvalidOperationException("sub");
        var res = p.PollOnce();
        Assert.NotNull(res);
        lock (lines) Assert.Contains(lines, l => l.Level == LogLevel.Error && l.Source == "poll");
    }

    private static void WaitFor(Func<bool> cond) =>
        Assert.True(SpinWait.SpinUntil(cond, 5000), "condition not reached");

    [Fact]
    public async Task Dispose_WaitsForRunningPoll_NoPolledAfter_TickReturnsFalse()
    {
        var (p, r, _, _, _, _) = Build();
        var entered = new SemaphoreSlim(0);
        var gate = new SemaphoreSlim(0);
        r.OnPoll = now => { entered.Release(); gate.Wait(); return Array.Empty<Reading>(); };
        int raised = 0;
        p.Polled += _ => Interlocked.Increment(ref raised);
        Assert.True(p.Tick());
        Assert.True(await entered.WaitAsync(5000, TestContext.Current.CancellationToken));
        var disposing = Task.Run(p.Dispose, TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.False(disposing.IsCompleted);
        gate.Release();
        await disposing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(0, raised);
        Assert.False(p.Tick());
        Assert.Equal(0, p.Skipped);
    }

    [Fact]
    public void Dispose_Twice_IsFine_StartAfterDispose_IsNoOp()
    {
        var (p, _, _, _, _, clock) = Build();
        p.Dispose();
        p.Dispose();
        p.Start();
        Assert.Equal(0, clock.TimersCreated);
    }

    [Fact]
    public void Start_PollsOnEachInterval()
    {
        var (p, r, _, _, _, clock) = Build();
        var polled = new SemaphoreSlim(0);
        r.OnPoll = _ => Array.Empty<Reading>();
        p.Polled += _ => polled.Release();
        using (p)
        {
            p.Start();
            WaitFor(() => clock.TimersCreated == 1);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(polled.Wait(5000, TestContext.Current.CancellationToken));
            Thread.Sleep(50);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(polled.Wait(5000, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void ChangedRefresh_UsesNewIntervalForNextTimer()
    {
        double refresh = 1.0;
        var r = new StubReader { Cards = new[] { CardA } };
        var clock = new ManualClock(Make.T0);
        var p = new Poller(r, new AlertRules(), new History(), () => new AppSettings { RefreshSeconds = refresh }, AppLog.Null, clock);
        var polled = new SemaphoreSlim(0);
        p.Polled += _ => polled.Release();
        using (p)
        {
            p.Start();
            WaitFor(() => clock.TimersCreated == 1);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(polled.Wait(5000, TestContext.Current.CancellationToken));
            Thread.Sleep(50);
            refresh = 2.0;
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(polled.Wait(5000, TestContext.Current.CancellationToken));
            WaitFor(() => clock.TimersCreated == 2 && clock.LiveTimers == 1);
            Thread.Sleep(50);
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.False(polled.Wait(200, TestContext.Current.CancellationToken));
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(polled.Wait(5000, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void SettingsThrowsOnce_LoopLogsAndContinues()
    {
        int calls = 0;
        var r = new StubReader { Cards = new[] { CardA } };
        var clock = new ManualClock(Make.T0);
        var log = new AppLog(null, LogLevel.Debug, false);
        var lines = new List<LogLine>();
        log.Line += l => { lock (lines) lines.Add(l); };
        var p = new Poller(r, new AlertRules(), new History(),
            () => Interlocked.Increment(ref calls) == 1 ? throw new InvalidOperationException("cfg") : new AppSettings(), log, clock);
        var polled = new SemaphoreSlim(0);
        p.Polled += _ => polled.Release();
        using (p)
        {
            p.Start();
            WaitFor(() => clock.TimersCreated == 1);
            lock (lines) Assert.Contains(lines, l => l.Level == LogLevel.Error && l.Source == "poll");
            clock.Advance(TimeSpan.FromSeconds(1));
            Assert.True(polled.Wait(5000, TestContext.Current.CancellationToken));
        }
    }
}
