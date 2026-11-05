namespace GpuMonitor.Core.Diagnostics;

public sealed record WmiAdapter(string Name, string DriverVersion, string PnpDeviceId, string Status);
public sealed record GpuEvent(DateTimeOffset At, string Provider, int Id, string Level, string Message);

public interface IWindowsInfo
{
    string Version();
    TimeSpan Uptime();
    int? HwSchMode();
    bool? GameMode();
}

public interface IWmiSource { IReadOnlyList<WmiAdapter> Adapters(); }
public interface IEventLogSource { IReadOnlyList<GpuEvent> Recent(TimeSpan window); }

public interface IRegistrySource
{
    IReadOnlyDictionary<string, string> GpuPreferences();
    string? RunValue();
}

public interface INvidiaSmi { string? QueryAll(); }

public sealed record CardSources(IWindowsInfo Win, IWmiSource Wmi, IEventLogSource Events, IRegistrySource Registry, INvidiaSmi Smi);

/// <summary>Empty sources for non-Windows hosts and tests.</summary>
public sealed class NullSources : IWindowsInfo, IWmiSource, IEventLogSource, IRegistrySource, INvidiaSmi
{
    public static readonly NullSources Instance = new();

    public string Version() => "";
    public TimeSpan Uptime() => TimeSpan.Zero;
    public int? HwSchMode() => null;
    public bool? GameMode() => null;
    public IReadOnlyList<WmiAdapter> Adapters() => Array.Empty<WmiAdapter>();
    public IReadOnlyList<GpuEvent> Recent(TimeSpan window) => Array.Empty<GpuEvent>();
    public IReadOnlyDictionary<string, string> GpuPreferences() => new Dictionary<string, string>();
    public string? RunValue() => null;
    public string? QueryAll() => null;

    public static CardSources All() => new(Instance, Instance, Instance, Instance, Instance);
}
