using GpuMonitor.Core.Alerts;

namespace GpuMonitor.App.Notify;

/// <summary>Sends the alerts that ask for a notification (<see cref="Alert.Notify"/>) to the notifier.</summary>
public sealed class AlertDispatcher(INotifier notifier)
{
    public void Dispatch(IReadOnlyList<Alert> alerts)
    {
        foreach (var alert in alerts)
        {
            if (alert.Notify) notifier.Show(alert.Title, alert.Message);
        }
    }
}
