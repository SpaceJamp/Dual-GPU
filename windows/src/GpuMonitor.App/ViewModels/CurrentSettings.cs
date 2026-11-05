using GpuMonitor.Core.Settings;

namespace GpuMonitor.App.ViewModels;

/// <summary>The settings in force. The Poller reads them through <see cref="Get"/>; Settings Save replaces them.</summary>
public sealed class CurrentSettings(AppSettings initial)
{
    private volatile AppSettings _value = initial;

    public AppSettings Value
    {
        get => _value;
        set => _value = value;
    }

    public AppSettings Get() => _value;
}
