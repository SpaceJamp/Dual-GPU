namespace GpuMonitor.App.ViewModels;

public sealed class CardViewModel : ObservableBase
{
    private string _busId = "";
    private string _name = "";
    private bool _isDisplay;
    private bool _isStale;
    private DateTimeOffset? _lastSeen;
    private int? _loadPct;
    private long? _vramUsedMiB;
    private long? _vramTotalMiB;
    private int? _tempC;
    private double? _powerW;
    private double? _powerLimitW;
    private IReadOnlyList<double?> _tempHistory = Array.Empty<double?>();
    private IReadOnlyList<double?> _loadHistory = Array.Empty<double?>();

    public string BusId { get => _busId; set => Set(ref _busId, value); }

    public string Name { get => _name; set => Set(ref _name, value); }

    public bool IsDisplay { get => _isDisplay; set => Set(ref _isDisplay, value); }

    public bool IsStale { get => _isStale; set => Set(ref _isStale, value); }

    public DateTimeOffset? LastSeen { get => _lastSeen; set => Set(ref _lastSeen, value); }

    public int? LoadPct { get => _loadPct; set => Set(ref _loadPct, value); }

    public long? VramUsedMiB { get => _vramUsedMiB; set => Set(ref _vramUsedMiB, value); }

    public long? VramTotalMiB { get => _vramTotalMiB; set => Set(ref _vramTotalMiB, value); }

    public int? TempC { get => _tempC; set => Set(ref _tempC, value); }

    public double? PowerW { get => _powerW; set => Set(ref _powerW, value); }

    public double? PowerLimitW { get => _powerLimitW; set => Set(ref _powerLimitW, value); }

    public IReadOnlyList<double?> TempHistory { get => _tempHistory; set => Set(ref _tempHistory, value); }

    public IReadOnlyList<double?> LoadHistory { get => _loadHistory; set => Set(ref _loadHistory, value); }
}
