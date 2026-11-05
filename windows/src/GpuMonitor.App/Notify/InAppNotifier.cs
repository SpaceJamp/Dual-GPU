using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;

namespace GpuMonitor.App.Notify;

/// <summary>Shows notifications inside the main window with Avalonia's <see cref="WindowNotificationManager"/>.
/// The host is looked up on every call because the window can be hidden or recreated.</summary>
public sealed class InAppNotifier(Func<Window?> host) : INotifier
{
    private WindowNotificationManager? _manager;
    private Window? _managerHost;

    /// <summary>False when there is no visible window. Called off the UI thread, the notification is posted
    /// to the UI thread and the result is true; if the window is hidden by then, it is dropped.</summary>
    public bool Show(string title, string message)
    {
        if (Dispatcher.UIThread.CheckAccess()) return ShowNow(title, message);

        if (host() is null) return false;
        Dispatcher.UIThread.Post(() => ShowNow(title, message));
        return true;
    }

    private bool ShowNow(string title, string message)
    {
        var window = host();
        if (window is null || !window.IsVisible) return false;

        if (_manager is null || !ReferenceEquals(_managerHost, window))
        {
            _manager = new WindowNotificationManager(window)
            {
                MaxItems = 3,
                Position = NotificationPosition.BottomRight,
            };
            _managerHost = window;
        }

        _manager.Show(new Notification(title, message, NotificationType.Information));
        return true;
    }
}
