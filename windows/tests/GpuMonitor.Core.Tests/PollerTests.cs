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

    [Fact]
    public void StartThenDispose_Stops()
    {
        var (p, _, _, _, _, _) = Build();
        p.Start();
        p.Dispose();
    }
}
