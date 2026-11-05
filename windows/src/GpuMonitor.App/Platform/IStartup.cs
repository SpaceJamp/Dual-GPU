namespace GpuMonitor.App.Platform;

/// <summary>Start with Windows on sign-in.</summary>
public interface IStartup
{
    bool IsEnabled();
    void Set(bool on);
}
