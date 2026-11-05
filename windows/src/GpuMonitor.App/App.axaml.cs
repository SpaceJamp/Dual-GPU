using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace GpuMonitor.App;

public partial class App : Application
{
    /// <summary>True once the user chose Exit. Until then, closing the main window only hides it.</summary>
    public static bool IsExiting { get; set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window { Title = "GPU Monitor", Width = 480, Height = 320 };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
