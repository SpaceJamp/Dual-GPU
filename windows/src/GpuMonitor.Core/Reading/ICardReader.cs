using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;

namespace GpuMonitor.Core.Readers;

public interface ICardReader
{
    NvmlReturn InitCode { get; }
    IReadOnlyList<Card> Cards { get; }
    IReadOnlyList<Reading> Poll(DateTimeOffset now);
    CallTimings Timings { get; }
}
