using System.Runtime.Versioning;
using GpuMonitor.App.Notify;
using GpuMonitor.Core.Logging;
using Microsoft.Win32;
using Windows.UI.Notifications;

namespace GpuMonitor.App.Platform.Windows;

/// <summary>Windows toast notifications for the unpackaged exe. On first use it registers the app ID under
/// HKCU\Software\Classes\AppUserModelId so toasts show the app's name and icon. Any failure is logged
/// once and reported as false, so the caller can fall back to in-app notifications.</summary>
[SupportedOSPlatform("windows10.0.10240.0")]
public sealed class WindowsToastNotifier(AppLog log) : INotifier
{
    public const string AppId = "SpaceJamp.GpuMonitor";
    public const string DisplayName = "GPU Monitor";
    internal const string AppIdKeyPath = @"Software\Classes\AppUserModelId\" + AppId;

    private readonly object _gate = new();
    private bool _registered;
    private bool _logged;

    public bool Show(string title, string message)
    {
        try
        {
            lock (_gate)
            {
                if (!_registered)
                {
                    Register();
                    _registered = true;
                }
            }

            var xml = ToastNotificationManager.GetTemplateContent(ToastTemplateType.ToastText02);
            var texts = xml.GetElementsByTagName("text");
            texts[0].AppendChild(xml.CreateTextNode(title));
            texts[1].AppendChild(xml.CreateTextNode(message));
            ToastNotificationManager.CreateToastNotifier(AppId).Show(new ToastNotification(xml));
            return true;
        }
        catch (Exception ex)
        {
            lock (_gate)
            {
                if (!_logged)
                {
                    _logged = true;
                    log.Warn("notify", "Windows notification failed: " + ex.Message, ex);
                }
            }
            return false;
        }
    }

    private static void Register()
    {
        using var key = Registry.CurrentUser.CreateSubKey(AppIdKeyPath, writable: true);
        key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
        key.SetValue("IconUri", Path.Combine(AppContext.BaseDirectory, "icon.png"), RegistryValueKind.String);
    }
}
