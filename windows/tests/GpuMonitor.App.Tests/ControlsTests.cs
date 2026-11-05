using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using GpuMonitor.App.Controls;
using GpuMonitor.App.ViewModels;
using Xunit;

namespace GpuMonitor.App.Tests;

public class ControlsTests
{
    private static readonly string[] BrushKeys =
        ["Background", "Surface", "Text", "TextDim", "Accent", "Warn", "Danger", "Grid"];

    private static (Window Window, WriteableBitmap? Frame) Render(Control control, double width, double height)
    {
        var window = new Window { Width = width, Height = height, Content = control };
        window.Show();
        var frame = window.CaptureRenderedFrame();
        return (window, frame);
    }

    private static IBrush Resource(Control control, string key)
    {
        Assert.True(control.TryFindResource(key, control.ActualThemeVariant, out var value), key);
        return Assert.IsAssignableFrom<IBrush>(value);
    }

    [AvaloniaTheory]
    [InlineData(50, "Accent")]
    [InlineData(85, "Warn")]
    [InlineData(97, "Danger")]
    public void Meter_ColorBands(double value, string expectedKey)
    {
        var meter = new Meter { Value = value, Maximum = 100, HasValue = true, Label = "Load", Text = $"{value} %" };
        var (window, frame) = Render(meter, 300, 60);

        Assert.NotNull(frame);
        Assert.Same(Resource(meter, expectedKey), meter.CurrentFill);
        window.Close();
    }

    [AvaloniaFact]
    public void Meter_NoValue_ShowsNa()
    {
        var meter = new Meter { Value = 50, Maximum = 100, HasValue = false, Label = "Load", Text = "50 %" };
        var (window, frame) = Render(meter, 300, 60);

        Assert.NotNull(frame);
        Assert.Equal("n/a", meter.ShownText);
        Assert.Same(Brushes.Transparent, meter.CurrentFill);
        window.Close();
    }

    [AvaloniaFact]
    public void LineGraph_NullBreaksLine()
    {
        var graph = new LineGraph { Points = [1, 2, null, 3, 4], Minimum = 0, Maximum = 10 };
        var (window, frame) = Render(graph, 300, 100);

        Assert.NotNull(frame);
        Assert.Equal(2, graph.LastSegmentCount);
        window.Close();
    }

    [AvaloniaFact]
    public void LineGraph_IsolatedPointIsSegment()
    {
        var graph = new LineGraph { Points = [1, null, 3, 4, null, null, 5], Minimum = 0, Maximum = 10, Limit = 8 };
        var (window, _) = Render(graph, 300, 100);

        Assert.Equal(3, graph.LastSegmentCount);
        window.Close();
    }

    [AvaloniaFact]
    public void LineGraph_Keeps60()
    {
        var points = Enumerable.Range(0, 60).Select(i => (double?)(i * 3 % 120 - 10)).ToArray();
        var graph = new LineGraph { Points = points, Minimum = 0, Maximum = 100, Limit = 80 };
        var (window, frame) = Render(graph, 300, 100);

        Assert.NotNull(frame);
        Assert.Equal(1, graph.LastSegmentCount);
        window.Close();
    }

    [AvaloniaFact]
    public void CardPanel_StaleText()
    {
        var seen = new DateTimeOffset(new DateTime(2026, 11, 5, 14, 2, 11, DateTimeKind.Local));
        var card = new CardViewModel
        {
            BusId = "00000000:01:00.0",
            Name = "NVIDIA GeForce RTX 5080",
            IsDisplay = true,
            LastSeen = seen,
            LoadPct = 40,
            VramUsedMiB = 2048,
            VramTotalMiB = 16303,
            TempC = 55,
            PowerW = 120,
            PowerLimitW = 360,
            TempHistory = [50, 52, null, 55],
            LoadHistory = [10, 20, 30, 40],
        };
        var panel = new CardPanel { Card = card, HeatLimitC = 80 };
        var (window, frame) = Render(panel, 400, 380);

        Assert.NotNull(frame);
        Assert.Equal("", panel.StatusText);

        card.IsStale = true;
        Assert.NotNull(window.CaptureRenderedFrame());
        Assert.Equal("No reading since 14:02:11", panel.StatusText);
        window.Close();
    }

    [AvaloniaFact]
    public void CardPanel_RedrawsOnCardChange()
    {
        var card = new CardViewModel { Name = "Card", LoadPct = 10 };
        var panel = new CardPanel { Card = card };
        var (window, before) = Render(panel, 400, 380);

        card.LoadPct = 90;
        var after = window.CaptureRenderedFrame();

        Assert.NotEqual(Pixels(before!), Pixels(after!));
        window.Close();
    }

    private static byte[] Pixels(WriteableBitmap bitmap)
    {
        using var buffer = bitmap.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        return bytes;
    }

    [AvaloniaFact]
    public void CardPanel_EmptyCardRenders()
    {
        var panel = new CardPanel { Card = new CardViewModel() };
        var (window, frame) = Render(panel, 400, 380);

        Assert.NotNull(frame);
        window.Close();
    }

    [AvaloniaFact]
    public void Theme_BrushesResolveInLightAndDark()
    {
        var light = new Meter();
        var dark = new Meter();
        var lightWindow = new Window { RequestedThemeVariant = ThemeVariant.Light, Content = light };
        var darkWindow = new Window { RequestedThemeVariant = ThemeVariant.Dark, Content = dark };
        lightWindow.Show();
        darkWindow.Show();

        foreach (var key in BrushKeys)
        {
            var l = Assert.IsAssignableFrom<ISolidColorBrush>(Resource(light, key));
            var d = Assert.IsAssignableFrom<ISolidColorBrush>(Resource(dark, key));
            Assert.NotEqual(l.Color, d.Color);
        }

        lightWindow.Close();
        darkWindow.Close();
    }

    [AvaloniaFact]
    public void App_MergesTheme()
    {
        var app = new App();
        app.Initialize();

        foreach (var key in BrushKeys)
        {
            Assert.True(app.Resources.TryGetResource(key, ThemeVariant.Light, out _), key);
            Assert.True(app.Resources.TryGetResource(key, ThemeVariant.Dark, out _), key);
        }
    }
}
