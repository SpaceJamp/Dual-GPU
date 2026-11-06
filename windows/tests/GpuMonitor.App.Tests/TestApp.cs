using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Themes.Fluent;
using GpuMonitor.App.Tests;

[assembly: AvaloniaTestApplication(typeof(TestApp))]

namespace GpuMonitor.App.Tests;

internal static class TestSetup
{
    // The crash path starts its own exe to write dumps; here that exe is the test host, so point it at the app.
    [ModuleInitializer]
    public static void Init() => CrashHandler.DumpHelper = Path.Combine(AppContext.BaseDirectory, "GpuMonitor.exe");
}

public class TestApp : Application
{
    private static readonly Uri ThemeUri = new("avares://GpuMonitor/Theme/Theme.axaml");

    public override void Initialize()
    {
        // Load the app assembly first so avares:// finds its compiled XAML.
        _ = typeof(App).Assembly;
        Resources.MergedDictionaries.Add(new ResourceInclude(ThemeUri) { Source = ThemeUri });
        Styles.Add(new FluentTheme());
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
