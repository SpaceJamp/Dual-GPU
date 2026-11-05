using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Xunit;

namespace GpuMonitor.App.Tests;

public class SmokeTests
{
    [AvaloniaFact]
    public void Smoke_WindowShows()
    {
        var window = new Window();
        window.Show();
        Assert.True(window.IsVisible);
    }
}
