using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using GpuMonitor.App.ViewModels;
using GpuMonitor.Core.Poll;

namespace GpuMonitor.App.Tray;

/// <summary>The notification-area icon: the hotter card's temperature, a tooltip with every card,
/// a click that opens the window and a menu. The Avalonia TrayIcon is created only by <see cref="Show"/>,
/// so the rest works without a tray (tests, headless).</summary>
public sealed class TrayController : IDisposable
{
    public const string AppName = "GPU Monitor";

    private readonly Application _app;
    private readonly Func<Window> _showWindow;
    private readonly Action _openSettings;
    private readonly Action _createReport;
    private readonly Action _exit;
    private TrayIcon? _tray;
    private WindowIcon? _icon;
    private (int? TempC, bool Warning, bool OverLimit)? _shown;

    /// <param name="showWindow">Returns the main window, creating it if needed.</param>
    public TrayController(Application app, Func<Window> showWindow, Action openSettings, Action createReport, Action exit)
    {
        _app = app;
        _showWindow = showWindow;
        _openSettings = openSettings;
        _createReport = createReport;
        _exit = exit;
    }

    public string ToolTipText { get; private set; } = AppName;

    /// <summary>Temperature the icon shows: the hottest card's, or null.</summary>
    public int? ShownTempC => _shown?.TempC;

    /// <summary>How many times the icon was drawn.</summary>
    internal int RenderCount { get; private set; }

    /// <summary>Adds the icon to the notification area.</summary>
    public void Show()
    {
        if (_tray is not null) return;
        _tray = new TrayIcon
        {
            Icon = _icon ?? TrayIconRenderer.Render(null, false, int.MaxValue),
            ToolTipText = ToolTipText,
            Menu = BuildMenu(),
            IsVisible = true,
        };
        _tray.Clicked += (_, _) => Open();
        TrayIcon.SetIcons(_app, new TrayIcons { _tray });
    }

    /// <summary>Applies a poll. Redraws the icon only when the shown temperature, the warning or the
    /// over-limit state changed.</summary>
    /// <param name="warning">See <see cref="IsWarning"/>.</param>
    public void Update(PollResult r, int heatLimit, bool warning)
    {
        var temps = r.Readings
            .Where(x => x.TempC.Ok)
            .ToDictionary(x => x.BusId, x => x.TempC.Value!.Value, StringComparer.Ordinal);
        int? hottest = temps.Count > 0 ? temps.Values.Max() : null;

        var names = r.Cards.ToDictionary(c => c.BusId, c => c.Name, StringComparer.Ordinal);
        var parts = names.Keys.Union(temps.Keys)
            .Order(StringComparer.Ordinal)
            .Select(bus => ShortName(names.GetValueOrDefault(bus, bus)) + " "
                + (temps.TryGetValue(bus, out var t) ? t.ToString(CultureInfo.InvariantCulture) + "°C" : "--"));
        ToolTipText = AppName + ": " + string.Join(", ", parts);
        if (_tray is not null) _tray.ToolTipText = ToolTipText;

        var state = (hottest, warning, hottest >= heatLimit);
        if (_shown == state) return;
        _shown = state;
        _icon = TrayIconRenderer.Render(hottest, warning, heatLimit);
        RenderCount++;
        if (_tray is not null) _tray.Icon = _icon;
    }

    /// <summary>"NVIDIA GeForce RTX 4070 Ti SUPER" becomes "4070 Ti SUPER".</summary>
    public static string ShortName(string name) =>
        name.Replace("NVIDIA GeForce ", "", StringComparison.Ordinal)
            .Replace("RTX ", "", StringComparison.Ordinal)
            .Trim();

    /// <summary>The icon warns when the window shows a banner or any card stopped reporting.</summary>
    public static bool IsWarning(MainViewModel vm) =>
        vm.Banner.Length > 0 || vm.Cards.Any(c => c.IsStale);

    internal NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();
        menu.Items.Add(Item("Open", Open));
        menu.Items.Add(Item("Create diagnostic report", _createReport));
        menu.Items.Add(Item("Settings", _openSettings));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(Item("Exit", _exit));
        return menu;
    }

    private static NativeMenuItem Item(string header, Action action) =>
        new(header) { Command = new RelayCommand(action) };

    private void Open()
    {
        var window = _showWindow();
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void Dispose()
    {
        if (_tray is null) return;
        TrayIcon.SetIcons(_app, new TrayIcons());
        _tray.Dispose();
        _tray = null;
    }
}
