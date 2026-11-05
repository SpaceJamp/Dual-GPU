using System.Diagnostics;
using GpuMonitor.Core.Logging;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;

namespace GpuMonitor.Core.Readers;

public sealed class NvmlReader : ICardReader
{
    private readonly INvml _nvml;
    private readonly AppLog _log;
    private readonly List<Card> _cards = new();
    private readonly HashSet<(string BusId, string Field)> _failing = new();

    public NvmlReader(INvml nvml, AppLog log)
    {
        _nvml = nvml;
        _log = log;
        InitCode = nvml.Init();
    }

    public NvmlReturn InitCode { get; }

    public IReadOnlyList<Card> Cards => _cards;

    public CallTimings Timings { get; } = new();

    public IReadOnlyList<Reading> Poll(DateTimeOffset now)
    {
        var readings = new List<Reading>();
        if (InitCode != NvmlReturn.Success) return readings;

        uint count = 0;
        if (Call(nameof(INvml.GetDeviceCount), () => _nvml.GetDeviceCount(out count)) != NvmlReturn.Success)
            return readings;

        for (uint i = 0; i < count; i++)
        {
            nint handle = 0;
            if (Call(nameof(INvml.GetHandle), () => _nvml.GetHandle(i, out handle)) != NvmlReturn.Success)
                continue;

            string busId = "";
            if (Call(nameof(INvml.GetBusId), () => _nvml.GetBusId(handle, out busId)) != NvmlReturn.Success)
                continue;

            string name = "";
            var nameRc = Call(nameof(INvml.GetName), () => _nvml.GetName(handle, out name));

            var pos = _cards.FindIndex(c => c.BusId == busId);
            Card card;
            if (pos < 0)
            {
                if (nameRc != NvmlReturn.Success) continue;
                string uuid = "";
                if (Call(nameof(INvml.GetUuid), () => _nvml.GetUuid(handle, out uuid)) != NvmlReturn.Success)
                    uuid = "";
                card = new Card(busId, name, uuid, (int)i);
                _cards.Add(card);
            }
            else
            {
                card = _cards[pos];
                if (IsGone(nameRc)) continue;
                if (card.Index != (int)i)
                {
                    card = card with { Index = (int)i };
                    _cards[pos] = card;
                }
            }

            readings.Add(ReadCard(card, handle, now));
        }
        return readings;
    }

    private static bool IsGone(NvmlReturn rc) => rc is NvmlReturn.GpuIsLost or NvmlReturn.GpuNotFound;

    private Reading ReadCard(Card card, nint h, DateTimeOffset now)
    {
        uint load = 0;
        var loadRc = Call(nameof(INvml.GetUtilization), () => _nvml.GetUtilization(h, out load));

        ulong used = 0, total = 0;
        var memRc = Call(nameof(INvml.GetMemory), () => _nvml.GetMemory(h, out used, out total));

        uint temp = 0;
        var tempRc = Call(nameof(INvml.GetTemperature), () => _nvml.GetTemperature(h, out temp));

        uint power = 0;
        var powerRc = Call(nameof(INvml.GetPowerUsage), () => _nvml.GetPowerUsage(h, out power));

        uint limit = 0;
        var limitRc = Call(nameof(INvml.GetEnforcedPowerLimit), () => _nvml.GetEnforcedPowerLimit(h, out limit));

        uint gfx = 0, sm = 0, mem = 0;
        var gfxRc = Call(nameof(INvml.GetClock), () => _nvml.GetClock(h, 0, out gfx));
        var smRc = Call(nameof(INvml.GetClock), () => _nvml.GetClock(h, 1, out sm));
        var memClkRc = Call(nameof(INvml.GetClock), () => _nvml.GetClock(h, 2, out mem));

        uint fan = 0;
        var fanRc = Call(nameof(INvml.GetFanSpeed), () => _nvml.GetFanSpeed(h, out fan));

        int pstate = 0;
        var pstateRc = Call(nameof(INvml.GetPState), () => _nvml.GetPState(h, out pstate));

        ulong throttle = 0;
        var throttleRc = Call(nameof(INvml.GetThrottleReasons), () => _nvml.GetThrottleReasons(h, out throttle));

        bool display = false;
        var displayRc = Call(nameof(INvml.GetDisplayActive), () => _nvml.GetDisplayActive(h, out display));

        return new Reading(
            card.BusId,
            now,
            Result(card, nameof(Reading.LoadPct), loadRc, () => (int)load),
            Result(card, nameof(Reading.VramUsedMiB), memRc, () => Units.BytesToMiB(used)),
            Result(card, nameof(Reading.VramTotalMiB), memRc, () => Units.BytesToMiB(total)),
            Result(card, nameof(Reading.TempC), tempRc, () => (int)temp),
            Result(card, nameof(Reading.PowerW), powerRc, () => Units.MilliwattsToWatts(power)),
            Result(card, nameof(Reading.PowerLimitW), limitRc, () => Units.MilliwattsToWatts(limit)),
            Result(card, nameof(Reading.GraphicsMHz), gfxRc, () => (int)gfx),
            Result(card, nameof(Reading.MemoryMHz), memClkRc, () => (int)mem),
            Result(card, nameof(Reading.SmMHz), smRc, () => (int)sm),
            Result(card, nameof(Reading.FanPct), fanRc, () => (int)fan),
            Result(card, nameof(Reading.PState), pstateRc, () => pstate),
            Result(card, nameof(Reading.Throttle), throttleRc, () => (ThrottleReasons)throttle),
            Result(card, nameof(Reading.DisplayActive), displayRc, () => display));
    }

    private Field<T> Result<T>(Card card, string field, NvmlReturn rc, Func<T> value) where T : struct
    {
        var key = (card.BusId, field);
        if (rc == NvmlReturn.Success)
        {
            _failing.Remove(key);
            return Field<T>.Of(value());
        }
        if (_failing.Add(key))
            _log.Warn("nvml", $"{card.Name} ({card.BusId}): {field} failed: {rc.NvmlReturnName()}");
        return Field<T>.Fail(rc);
    }

    private NvmlReturn Call(string name, Func<NvmlReturn> call)
    {
        var start = Stopwatch.GetTimestamp();
        var rc = call();
        Timings.Record(name, Stopwatch.GetElapsedTime(start));
        return rc;
    }
}
