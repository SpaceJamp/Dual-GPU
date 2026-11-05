using GpuMonitor.Core.Logging;

namespace GpuMonitor.App.Notify;

/// <summary>Tries <paramref name="primary"/> first and uses <paramref name="fallback"/> whenever it returns false.
/// The first fallback is logged as a warning; later ones are not.</summary>
public sealed class FallbackNotifier(INotifier primary, INotifier fallback, AppLog log) : INotifier
{
    private int _logged;

    public bool Show(string title, string message)
    {
        if (primary.Show(title, message)) return true;

        if (Interlocked.Exchange(ref _logged, 1) == 0)
            log.Warn("notify", "System notifications are not available; showing them inside the window instead.");
        return fallback.Show(title, message);
    }
}
