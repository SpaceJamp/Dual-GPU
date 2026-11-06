using Avalonia.Controls;
using GpuMonitor.App.Notify;
using GpuMonitor.Core.Diagnostics;
using GpuMonitor.Core.Logging;
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

    public static void AttachParentConsole()
    {
#if WINDOWS
        ParentConsole.Attach();
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

    /// <summary>Windows toasts with the in-app notifier as fallback on Windows; the in-app notifier elsewhere.
    /// <paramref name="host"/> returns the main window, or null when there is none.</summary>
    public static INotifier Notifier(AppLog log, Func<Window?> host)
    {
#if WINDOWS
        return new FallbackNotifier(new WindowsToastNotifier(log), new InAppNotifier(host), log);
#else
        _ = log;
        return new InAppNotifier(host);
#endif
    }
}
