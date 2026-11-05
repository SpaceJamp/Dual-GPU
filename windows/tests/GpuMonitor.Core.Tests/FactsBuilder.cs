using GpuMonitor.Core.Diagnostics;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Readers;
using GpuMonitor.Core.Settings;

namespace GpuMonitor.Core.Tests;

/// <summary>Starts from an all-good two-card baseline; each test changes one thing.</summary>
internal sealed class FactsBuilder
{
    public const string Card5080 = "NVIDIA GeForce RTX 5080";
    public const string Card4070 = "NVIDIA GeForce RTX 4070 Ti SUPER";

    public DriverFacts Driver = new(NvmlReturn.Success, "nvml.dll", null,
        Fact<string>.Of("572.83"), Fact<string>.Of("12.572.83"), Fact<string>.Of("12.8"));
    public List<CardFacts> Cards = new() { Card("00000000:01:00.0", Card5080, true), Card("00000000:02:00.0", Card4070, false) };
    public Fact<IReadOnlyList<WmiAdapter>> Wmi = Fact<IReadOnlyList<WmiAdapter>>.Of(new[]
    {
        new WmiAdapter(Card5080, "32.0", "PCI\\A", "OK"), new WmiAdapter(Card4070, "32.0", "PCI\\B", "OK"),
    });
    public Fact<IReadOnlyList<GpuEvent>> EventLog = Fact<IReadOnlyList<GpuEvent>>.Of(Array.Empty<GpuEvent>());
    public AppSettings Settings = new();
    public SettingsStatus SettingsStatus = SettingsStatus.Loaded;
    public bool LogWritable = true;
    public Dictionary<string, TimingStats> Timings = new() { ["temp"] = new(10, 1, 5, 9, 9) };

    public static CardFacts Card(string bus, string name, bool display) => new(bus,
        Fact<string>.Of(name), Fact<string>.Of("GPU-" + bus), Fact<string>.Of("95.02"),
        Fact<uint>.Of(4), Fact<uint>.Of(5), Fact<uint>.Of(16), Fact<uint>.Of(16),
        Fact<bool>.Of(display),
        Fact<uint>.Of(50), Fact<uint>.Of(90), Fact<uint>.Of(100),
        Fact<double>.Of(100), Fact<double>.Of(320), Fact<double>.Of(320),
        Fact<uint>.Of(2000), Fact<uint>.Of(3000), Fact<uint>.Of(1000), Fact<uint>.Of(1500), Fact<uint>.Of(2000), Fact<uint>.Of(3000),
        Fact<uint>.Of(40), Fact<int>.Of(2), Fact<ThrottleReasons>.Of(ThrottleReasons.None),
        Fact<long>.Of(1000), Fact<long>.Of(16384),
        Fact<IReadOnlyList<ProcessFact>>.Of(Array.Empty<ProcessFact>()));

    public FactsBuilder WithCard(int i, Func<CardFacts, CardFacts> change) { Cards[i] = change(Cards[i]); return this; }

    public Facts Build() => new(DateTimeOffset.UnixEpoch,
        new AppFacts("1.0", "abc", ".NET 10"),
        new WindowsFacts(Fact<string>.Of("11"), Fact<TimeSpan>.Of(TimeSpan.FromHours(1)), Fact<int?>.Of(2), Fact<bool?>.Of(true)),
        Driver, Cards, Wmi, Fact<string?>.Of("ok"),
        Fact<IReadOnlyDictionary<string, string>>.Of(new Dictionary<string, string>()), EventLog,
        new StateFacts(Settings, SettingsStatus, null, Array.Empty<GpuMonitor.Core.Alerts.Alert>(), Timings, Array.Empty<string>(), LogWritable));
}
