namespace GpuMonitor.App.Platform;

/// <summary>Startup stand-in for non-Windows hosts: never enabled, Set does nothing.
/// Fact sources off Windows come from Core's NullSources.</summary>
public sealed class NullPlatform : IStartup
{
    public bool IsEnabled() => false;
    public void Set(bool on) { }
}
