namespace GpuMonitor.Core;

public static class Paths
{
    /// <summary>Test override: when set, both AppData and LocalAppData resolve to this folder.</summary>
    internal static string? Root { get; set; }

    private static string AppData => Root ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    private static string LocalAppData => Root ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    public static string SettingsFile => Path.Combine(AppData, "GpuMonitor", "settings.json");
    public static string LogDir => Path.Combine(LocalAppData, "GpuMonitor", "logs");
    public static string LogFile => Path.Combine(LogDir, "gpumonitor.log");
    public static string CrashDir => Path.Combine(LogDir, "crash");
    public static string RecordingDir => Path.Combine(LocalAppData, "GpuMonitor", "recordings");
}
