using System.Reflection;
using System.Runtime.InteropServices;

namespace GpuMonitor.App;

/// <summary>Version facts for logs and reports. The SDK stamps "version+commit" into the assembly.</summary>
internal static class AppInfo
{
    private static readonly string[] Stamp =
        (typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
        .Split('+');

    public static string Version => Stamp[0];
    public static string Commit => Stamp.Length > 1 ? Stamp[1] : "unknown";
    public static string Runtime => RuntimeInformation.FrameworkDescription;
}
