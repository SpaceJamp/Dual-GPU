using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using GpuMonitor.App.Notify;
using GpuMonitor.App.Tray;
using GpuMonitor.App.ViewModels;
using GpuMonitor.App.Views;
using GpuMonitor.Core.Poll;

namespace GpuMonitor.App;

/// <summary>The UI side of the app: one window, the tray icon and the notifier, fed by the poller.
/// Everything here runs on the UI thread except <see cref="RequestOpen"/>.</summary>
internal sealed class Shell
{
    private readonly Bootstrap _boot;
    private readonly AlertDispatcher _alerts;
    private readonly Action _shutdown;
    private bool _exited;

    /// <param name="notifier">Builds the notifier from a function returning the window that hosts in-app notices.</param>
    public Shell(Application app, Bootstrap boot, Func<Func<Window?>, INotifier> notifier, Action shutdown)
    {
        _boot = boot;
        _shutdown = shutdown;
        ViewModel = boot.CreateViewModel();
        Window = new MainWindow(ViewModel);
        Tray = new TrayController(app, () => Window, OpenSettings, CreateReport, Exit);
        _alerts = new AlertDispatcher(notifier(() => Window));
    }

    public MainViewModel ViewModel { get; }
    public MainWindow Window { get; }
    public TrayController Tray { get; }

    /// <param name="hidden">Stay in the tray until the user opens the window (the Run key passes --tray).</param>
    public void Start(bool hidden)
    {
        Tray.Show();
        _boot.Poller.Polled += r => Dispatcher.UIThread.Post(() => Apply(r));
        _boot.Poller.Start();
        if (!hidden) Open();
    }

    public void Apply(PollResult r)
    {
        if (_exited) return;
        ViewModel.SetNvmlStatus(_boot.Reader.InitCode, _boot.Reader.LoadError);
        ViewModel.OnPolled(r);
        Tray.Update(r, _boot.Settings.Value.HeatLimitC, TrayController.IsWarning(ViewModel));
        _boot.NoteAlerts(r.Alerts);
        _alerts.Dispatch(r.Alerts);
    }

    public void Open() => Tray.Open();

    /// <summary>Open from any thread; a second launch uses this through the single-instance pipe.</summary>
    public void RequestOpen() => Dispatcher.UIThread.Post(Open);

    public void OpenSettings()
    {
        Open();
        Window.ShowTab("Settings");
    }

    public void CreateReport()
    {
        Open();
        Window.ShowTab("Diagnostics");
        ViewModel.Diagnostics.CreateReportCommand.Execute(null);
    }

    public void Exit()
    {
        if (_exited) return;
        _exited = true;
        _boot.Dispose();
        Tray.Dispose();
        App.IsExiting = true;
        Window.Close();
        App.IsExiting = false;
        _shutdown();
    }
}
