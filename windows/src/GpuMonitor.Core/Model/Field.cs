using GpuMonitor.Core.Nvml;

namespace GpuMonitor.Core.Model;

public readonly record struct Field<T>(T? Value, NvmlReturn Code) where T : struct
{
    public bool Ok => Code == NvmlReturn.Success && Value.HasValue;

    public static Field<T> Of(T v) => new(v, NvmlReturn.Success);

    public static Field<T> Fail(NvmlReturn c) => new(null, c);
}
