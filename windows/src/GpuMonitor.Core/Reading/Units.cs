namespace GpuMonitor.Core.Readers;

public static class Units
{
    public static double MilliwattsToWatts(uint mW) => mW / 1000.0;

    public static long BytesToMiB(ulong bytes) => (long)(bytes / (1024UL * 1024UL));

    /// <summary>12080 becomes "12.8".</summary>
    public static string CudaVersion(int v) => $"{v / 1000}.{v % 1000 / 10}";
}
