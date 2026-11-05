using System.Collections.ObjectModel;
using GpuMonitor.Core.Logging;
using GpuMonitor.Core.Nvml;
using GpuMonitor.Core.Poll;

namespace GpuMonitor.App.ViewModels;

/// <summary>Root view model of the main window.</summary>
public sealed class MainViewModel : ObservableBase
{
    private readonly NvmlReturn _initCode;
    private readonly string? _loadError;
    private readonly AppLog _log;
    private readonly History _history;
    private readonly CurrentSettings _settings;
    private string _banner = "";
    private int _heatLimitC;

    /// <param name="initCode">Result of the reader's NVML init; anything but Success shows the banner.</param>
    /// <param name="loadError">Why nvml.dll did not load, if known; shown in the banner instead of the code.</param>
    /// <param name="history">The History the Poller fills; the cards' graphs read it.</param>
    public MainViewModel(NvmlReturn initCode, string? loadError, AppLog log, History history, CurrentSettings settings,
        DiagnosticsViewModel diagnostics, SettingsViewModel settingsViewModel)
    {
        _initCode = initCode;
        _loadError = loadError;
        _log = log;
        _history = history;
        _settings = settings;
        Diagnostics = diagnostics;
        Diagnostics.Cards = Cards;
        Settings = settingsViewModel;
        Settings.Saved += s => HeatLimitC = s.HeatLimitC;
        _heatLimitC = settings.Value.HeatLimitC;
        foreach (var c in Diagnostics.Commands)
            c.OnError = ex => CommandFailed(ex, Diagnostics.ShowStatus);
        Settings.SaveCommand.OnError = ex => CommandFailed(ex, Settings.ShowStatus);
        UpdateBanner();
    }

    private void CommandFailed(Exception ex, Action<string> showStatus)
    {
        _log.Error("ui", "Command failed: " + ex.Message, ex);
        showStatus("Something went wrong: " + ex.Message);
    }

    /// <summary>One entry per card, ordered by PCI bus ID. Items are inserted in place and never moved.</summary>
    public ObservableCollection<CardViewModel> Cards { get; } = new();

    public DiagnosticsViewModel Diagnostics { get; }

    public SettingsViewModel Settings { get; }

    /// <summary>Problems the user must see at once; empty when there are none.</summary>
    public string Banner { get => _banner; private set => Set(ref _banner, value); }

    /// <summary>Heat limit drawn on the cards' temperature graphs.</summary>
    public int HeatLimitC { get => _heatLimitC; private set => Set(ref _heatLimitC, value); }

    /// <summary>Applies a poll. The caller invokes it on the UI thread.</summary>
    public void OnPolled(PollResult r)
    {
        var readings = r.Readings.ToDictionary(x => x.BusId, StringComparer.Ordinal);
        foreach (var c in r.Cards) Find(c.BusId, c.Name);
        foreach (var x in r.Readings) Find(x.BusId, null);

        foreach (var vm in Cards)
            vm.Update(readings.GetValueOrDefault(vm.BusId), _history.For(vm.BusId), r.At);

        HeatLimitC = _settings.Value.HeatLimitC;
        Diagnostics.OnPolled(r);
        UpdateBanner();
    }

    private CardViewModel Find(string busId, string? name)
    {
        var i = 0;
        for (; i < Cards.Count; i++)
        {
            var cmp = string.CompareOrdinal(Cards[i].BusId, busId);
            if (cmp == 0)
            {
                if (name is not null) Cards[i].Name = name;
                return Cards[i];
            }
            if (cmp > 0) break;
        }

        var vm = new CardViewModel { BusId = busId, Name = name ?? busId };
        Cards.Insert(i, vm);
        return vm;
    }

    private void UpdateBanner()
    {
        var lines = new List<string>(2);
        if (_initCode != NvmlReturn.Success)
            lines.Add("NVML did not load: " + (string.IsNullOrEmpty(_loadError) ? _initCode.NvmlReturnName() : _loadError)
                + ". See the Diagnostics tab.");
        if (_log.LastWriteError is { Length: > 0 } err)
            lines.Add("Log file can't be written: " + err);
        Banner = string.Join("\n", lines);
    }
}
