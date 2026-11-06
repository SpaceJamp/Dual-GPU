using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace GpuMonitor.App;

public partial class App : Application
{
    private static volatile Shell? _shell;

    /// <summary>True once the user chose Exit. Until then, closing the main window only hides it.</summary>
    public static bool IsExiting { get; set; }

    /// <summary>Set by Program before Avalonia starts. Null in the XAML previewer.</summary>
    internal static Bootstrap? Boot { get; set; }

    internal static bool StartHidden { get; set; }

    /// <summary>The running UI, once it exists. The single-instance pipe thread reads this.</summary>
    internal static Shell? Shell => _shell;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Boot is { } boot)
        {
            CrashHandler.HookDispatcher();
            // The window hides on close and MainWindow stays unset, so only Exit ends the app.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var shell = new Shell(this, boot, host => Platform.Platform.Notifier(boot.Log, host), () => desktop.Shutdown(0));
            _shell = shell;
            shell.Start(StartHidden);
        }

        base.OnFrameworkInitializationCompleted();
    }
}
