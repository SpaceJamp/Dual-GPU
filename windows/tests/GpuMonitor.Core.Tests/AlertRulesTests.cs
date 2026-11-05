using System;
using System.Collections.Generic;
using System.Linq;
using GpuMonitor.Core.Alerts;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Settings;
using Xunit;

namespace GpuMonitor.Core.Tests;

public class AlertRulesTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly Card A = new("00000000:01:00.0", "NVIDIA GeForce RTX 5080", "u1", 0);
    private static readonly Card B = new("00000000:02:00.0", "NVIDIA GeForce RTX 4070 Ti SUPER", "u2", 1);
    private static readonly AppSettings S = new();

    private static Reading R(Card c, Field<int> temp, long used = 1000, long total = 10000) => new(
        c.BusId, T0, Field<int>.Of(0), Field<long>.Of(used), Field<long>.Of(total), temp,
        Field<double>.Of(0), Field<double>.Of(0), Field<int>.Of(0), Field<int>.Of(0), Field<int>.Of(0),
        Field<int>.Of(0), Field<int>.Of(0), Field<ThrottleReasons>.Of(default), Field<bool>.Of(false));

    private static Reading R(Card c, int temp, long used = 1000, long total = 10000) => R(c, Field<int>.Of(temp), used, total);

    private static IReadOnlyList<Alert> Poll(AlertRules rules, DateTimeOffset now, params Reading[] rs) =>
        rules.Evaluate(new[] { A, B }, rs, now, S);

    [Fact]
    public void Hot_FiresOnceAtLimit()
    {
        var rules = new AlertRules();
        var all = new List<Alert>();
        int i = 0;
        foreach (var t in new[] { 79, 80, 81, 82 })
            all.AddRange(Poll(rules, T0.AddSeconds(i++), R(A, t), R(B, 40)));
        var hot = Assert.Single(all);
        Assert.Equal(AlertKind.Hot, hot.Kind);
        Assert.Equal(80, hot.Value);
        Assert.Equal("NVIDIA GeForce RTX 5080 is hot", hot.Title);
        Assert.Equal("80°C, limit 80°C", hot.Message);
        Assert.True(hot.Notify);
    }

    [Fact]
    public void Hot_ReArmsBelowMargin()
    {
        var rules = new AlertRules();
        var kinds = new List<AlertKind>();
        int i = 0;
        foreach (var t in new[] { 80, 76, 75, 80 })
            kinds.AddRange(Poll(rules, T0.AddSeconds(i++), R(A, t), R(B, 40)).Select(a => a.Kind));
        Assert.Equal(new[] { AlertKind.Hot, AlertKind.HotCleared, AlertKind.Hot }, kinds);
        var cleared = rules.History[1];
        Assert.False(cleared.Notify);
        Assert.Equal("75°C, below 75°C", cleared.Message);
    }

    [Fact]
    public void Hot_PerCard()
    {
        var rules = new AlertRules();
        var a = Assert.Single(Poll(rules, T0, R(A, 85), R(B, 60)));
        Assert.Equal(A.BusId, a.BusId);
    }

    [Fact]
    public void LowVram_FiresAt90Pct_ReArmsAt85()
    {
        var rules = new AlertRules();
        var kinds = new List<AlertKind>();
        int i = 0;
        foreach (var used in new[] { 8900L, 9000, 9500, 8600, 8500, 9000 })
            kinds.AddRange(Poll(rules, T0.AddSeconds(i++), R(A, 50, used), R(B, 40)).Select(a => a.Kind));
        Assert.Equal(new[] { AlertKind.LowVram, AlertKind.LowVramCleared, AlertKind.LowVram }, kinds);
        Assert.Equal("90% used, limit 90%", rules.History[0].Message);
        Assert.Equal("85% used", rules.History[1].Message);
    }

    [Fact]
    public void MissingTemperature_NoHeatAlert()
    {
        var rules = new AlertRules();
        var r = R(A, Field<int>.Fail(NvmlReturn.NotSupported));
        Assert.Empty(rules.Evaluate(new[] { A, B }, new[] { r, R(B, Field<int>.Fail(NvmlReturn.NotSupported)) }, T0, new AppSettings { HeatLimitC = 0 }));
    }

    [Fact]
    public void FailedField_LeavesStateUnchanged()
    {
        var rules = new AlertRules();
        Poll(rules, T0, R(A, 85), R(B, 40));
        Poll(rules, T0.AddSeconds(1), R(A, Field<int>.Fail(NvmlReturn.NotSupported)), R(B, 40));
        Assert.Empty(Poll(rules, T0.AddSeconds(2), R(A, 85), R(B, 40)));
    }

    [Fact]
    public void CardMissing_After5Seconds()
    {
        var rules = new AlertRules();
        Assert.Empty(Poll(rules, T0, R(A, 40), R(B, 40)));
        for (int i = 1; i <= 3; i++) Assert.Empty(Poll(rules, T0.AddSeconds(i), R(A, 40)));
        Assert.Empty(Poll(rules, T0.AddSeconds(4), R(A, 40)));
        var m = Assert.Single(Poll(rules, T0.AddSeconds(5), R(A, 40)));
        Assert.Equal(AlertKind.CardMissing, m.Kind);
        Assert.Equal(B.BusId, m.BusId);
        Assert.Equal("NVIDIA GeForce RTX 4070 Ti SUPER stopped reporting", m.Title);
        Assert.Equal("No reading for 5 seconds", m.Message);
        Assert.Empty(Poll(rules, T0.AddSeconds(6), R(A, 40)));
        var back = Assert.Single(Poll(rules, T0.AddSeconds(7), R(A, 40), R(B, 40)));
        Assert.Equal(AlertKind.CardBack, back.Kind);
        Assert.Equal("Readings resumed", back.Message);
    }

    [Fact]
    public void CardNeverSeen_MissingCountsFromFirstPoll()
    {
        var rules = new AlertRules();
        Assert.Empty(Poll(rules, T0, R(A, 40)));
        for (int i = 1; i <= 4; i++) Assert.Empty(Poll(rules, T0.AddSeconds(i), R(A, 40)));
        Assert.Equal(AlertKind.CardMissing, Assert.Single(Poll(rules, T0.AddSeconds(5), R(A, 40))).Kind);
    }

    [Fact]
    public void LongGapAfterSleep_ResetsMissingTimers()
    {
        var rules = new AlertRules();
        Poll(rules, T0, R(A, 40), R(B, 40));
        var wake = T0.AddHours(3);
        Assert.Empty(Poll(rules, wake, R(A, 40)));
        Assert.Empty(Poll(rules, wake.AddSeconds(1), R(A, 40)));
        Assert.Empty(Poll(rules, wake.AddSeconds(2), R(A, 40)));
        Assert.Empty(Poll(rules, wake.AddSeconds(3), R(A, 40)));
        Assert.Empty(Poll(rules, wake.AddSeconds(4), R(A, 40)));
        var m = Assert.Single(Poll(rules, wake.AddSeconds(5), R(A, 40)));
        Assert.Equal(AlertKind.CardMissing, m.Kind);
        Assert.Equal(B.BusId, m.BusId);
    }

    [Fact]
    public void History_Capped200()
    {
        var rules = new AlertRules();
        for (int i = 0; i < 150; i++)
        {
            Poll(rules, T0.AddSeconds(2 * i), R(A, 85), R(B, 40));
            Poll(rules, T0.AddSeconds(2 * i + 1), R(A, 60), R(B, 40));
        }
        Assert.Equal(200, rules.History.Count);
    }
}
