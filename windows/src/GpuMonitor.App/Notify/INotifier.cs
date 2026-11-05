namespace GpuMonitor.App.Notify;

/// <summary>Shows a notification to the user.</summary>
public interface INotifier
{
    /// <summary>Returns false when the notification could not be shown.</summary>
    bool Show(string title, string message);
}
