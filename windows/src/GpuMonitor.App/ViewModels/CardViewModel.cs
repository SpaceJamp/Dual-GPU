using System.Globalization;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Nvml;

namespace GpuMonitor.App.ViewModels;

/// <summary>One row of the Diagnostics field table: Reading property name, invariant value ("" when failed), NVML code.</summary>
public sealed record FieldRow(string Name, string Value, string Code);

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
    private IReadOnlyList<FieldRow> _fields = Array.Empty<FieldRow>();

    /// <summary>A card with no reading for this long is stale.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(5);

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

    public IReadOnlyList<FieldRow> Fields { get => _fields; private set => Set(ref _fields, value); }

    /// <summary>Applies a poll. A null reading keeps the last values and LastSeen. TempHistory,
    /// LoadHistory and Fields always get new list instances so bound controls redraw.</summary>
    public void Update(Reading? r, IReadOnlyList<Reading?> history, DateTimeOffset now)
    {
        if (r is not null)
        {
            LastSeen = r.At;
            LoadPct = Value(r.LoadPct);
            VramUsedMiB = Value(r.VramUsedMiB);
            VramTotalMiB = Value(r.VramTotalMiB);
            TempC = Value(r.TempC);
            PowerW = Value(r.PowerW);
            PowerLimitW = Value(r.PowerLimitW);
            if (r.DisplayActive.Ok) IsDisplay = r.DisplayActive.Value!.Value;
            Fields = BuildFields(r);
        }
        else
        {
            Fields = Fields.ToArray();
        }

        IsStale = LastSeen is not { } seen || now - seen >= StaleAfter;
        TempHistory = history.Select(h => h is { TempC.Ok: true } ? (double?)h.TempC.Value!.Value : null).ToArray();
        LoadHistory = history.Select(h => h is { LoadPct.Ok: true } ? (double?)h.LoadPct.Value!.Value : null).ToArray();
    }

    private static T? Value<T>(Field<T> f) where T : struct => f.Ok ? f.Value : null;

    private static FieldRow[] BuildFields(Reading r) =>
    [
        Row(nameof(Reading.LoadPct), r.LoadPct),
        Row(nameof(Reading.VramUsedMiB), r.VramUsedMiB),
        Row(nameof(Reading.VramTotalMiB), r.VramTotalMiB),
        Row(nameof(Reading.TempC), r.TempC),
        Row(nameof(Reading.PowerW), r.PowerW),
        Row(nameof(Reading.PowerLimitW), r.PowerLimitW),
        Row(nameof(Reading.GraphicsMHz), r.GraphicsMHz),
        Row(nameof(Reading.MemoryMHz), r.MemoryMHz),
        Row(nameof(Reading.SmMHz), r.SmMHz),
        Row(nameof(Reading.FanPct), r.FanPct),
        Row(nameof(Reading.PState), r.PState),
        Row(nameof(Reading.Throttle), r.Throttle),
        Row(nameof(Reading.DisplayActive), r.DisplayActive, b => b ? "true" : "false"),
    ];

    private static FieldRow Row<T>(string name, Field<T> f) where T : struct, IFormattable =>
        Row(name, f, v => v.ToString(null, CultureInfo.InvariantCulture));

    private static FieldRow Row<T>(string name, Field<T> f, Func<T, string> format) where T : struct =>
        new(name, f.Ok ? format(f.Value!.Value) : "", f.Code.NvmlReturnName());
}
