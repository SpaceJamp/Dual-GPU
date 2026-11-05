using GpuMonitor.Core.Diagnostics;
#if WINDOWS
using GpuMonitor.App.Platform.Windows;
#endif

namespace GpuMonitor.App.Platform;

/// <summary>Picks the Windows implementations on the Windows build and the null ones elsewhere.</summary>
public static class Platform
{
    public static CardSources Sources()
    {
#if WINDOWS
        var registry = new RegistrySource();
        return new CardSources(registry, new WmiSource(), new EventLogSource(), registry, new NvidiaSmiSource());
#else
        return NullSources.All();
#endif
    }

    public static IStartup Startup()
    {
#if WINDOWS
        return new RunKeyStartup();
#else
        return new NullPlatform();
#endif
    }
}
