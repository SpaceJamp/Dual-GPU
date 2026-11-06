using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using GpuMonitor.App.Tray;
using GpuMonitor.Core.Alerts;
using GpuMonitor.Core.Model;
using GpuMonitor.Core.Poll;
using Xunit;

namespace GpuMonitor.App.Tests;

public class TrayTests
{
    private const string Bus5080 = "00000000:01:00.0";
    private const string Bus4070 = "00000000:02:00.0";
    private const string Name5080 = "NVIDIA GeForce RTX 5080";
    private const string Name4070 = "NVIDIA GeForce RTX 4070 Ti SUPER";

    private static PollResult Poll(int? t5080, int? t4070)
    {
        var at = VmKit.T0;
        var readings = new List<Reading>();
        if (t5080 is { } a) readings.Add(VmKit.Reading(Bus5080, at, temp: a));
        if (t4070 is { } b) readings.Add(VmKit.Reading(Bus4070, at, temp: b));
        // Cards deliberately out of bus order.
        return new PollResult(at, [VmKit.Card(Bus4070, Name4070), VmKit.Card(Bus5080, Name5080)],
            readings, Array.Empty<Alert>(), TimeSpan.Zero);
    }

    private static TrayController Controller() =>
        new(Application.Current!, () => new Window(), () => { }, () => { }, () => { });

    [AvaloniaFact]
    public void Tray_ReusesIconWhenStateReturns()
    {
        // Every rendered WindowIcon holds native icon handles, so temperatures going up and down must not add more.
        var tray = Controller();
        foreach (var t in new[] { 64, 65, 64, 65, 64 }) tray.Update(Poll(t, null), 80, false);

        Assert.Equal(2, tray.RenderCount);
        Assert.Equal(64, tray.ShownTempC);
    }

    [AvaloniaFact]
    public void TrayIcon_RendersNumberAndWarning()
    {
        Assert.NotNull(TrayIconRenderer.Render(64, false, 80));
        Assert.NotNull(TrayIconRenderer.Render(null, false, 80));
        Assert.NotNull(TrayIconRenderer.Render(64, true, 80));
        Assert.NotNull(TrayIconRenderer.Render(100, false, 80));
    }

    [Theory]
    [InlineData(64, false, "64")]
    [InlineData(null, false, "--")]
    [InlineData(64, true, "!")]
    [InlineData(null, true, "!")]
    public void TrayIcon_Text(int? temp, bool warning, string expected)
    {
        Assert.Equal(expected, TrayIconRenderer.Text(temp, warning));
    }

    [AvaloniaFact]
    public void TrayIcon_OverLimit_DrawsDanger()
    {
        var danger = ((ISolidColorBrush)TrayIconRenderer.Brush("Danger")).Color;
        Assert.True(HasColor(TrayIconRenderer.Draw(85, false, 80), danger));
        Assert.True(HasColor(TrayIconRenderer.Draw(80, false, 80), danger));
        Assert.False(HasColor(TrayIconRenderer.Draw(79, false, 80), danger));
        Assert.True(HasColor(TrayIconRenderer.Draw(50, true, 80), danger));
    }

    private static bool HasColor(RenderTargetBitmap bitmap, Color color)
    {
        using (bitmap)
        {
            var size = bitmap.PixelSize;
            var stride = size.Width * 4;
            var buffer = new byte[stride * size.Height];
            var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);
            try { bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), buffer.Length, stride); }
            finally { handle.Free(); }

            // Premultiplied BGRA; the colors we look for are opaque.
            for (var i = 0; i < buffer.Length; i += 4)
            {
                if (buffer[i + 3] == 255 && buffer[i] == color.B && buffer[i + 1] == color.G && buffer[i + 2] == color.R)
                    return true;
            }
            return false;
        }
    }

    [Theory]
    [InlineData(Name5080, "5080")]
    [InlineData(Name4070, "4070 Ti SUPER")]
    [InlineData("NVIDIA GeForce GTX 1080", "GTX 1080")]
    [InlineData("NVIDIA RTX A4000", "NVIDIA A4000")]
    public void ShortName_DropsVendorAndRtx(string name, string expected)
    {
        Assert.Equal(expected, TrayController.ShortName(name));
    }

    [AvaloniaFact]
    public void Tooltip_OrderedByBusId()
    {
        var tray = Controller();
        tray.Update(Poll(64, 58), 80, false);
        Assert.Equal("GPU Monitor: 5080 64°C, 4070 Ti SUPER 58°C", tray.ToolTipText);

        tray.Update(Poll(64, null), 80, false);
        Assert.Equal("GPU Monitor: 5080 64°C, 4070 Ti SUPER --", tray.ToolTipText);
    }

    [AvaloniaFact]
    public void Tray_UpdatesOnlyOnChange()
    {
        var tray = Controller();
        tray.Update(Poll(64, 58), 80, false);
        Assert.Equal(1, tray.RenderCount);
        Assert.Equal(64, tray.ShownTempC);

        // Cooler card changes, hotter one does not: same icon.
        tray.Update(Poll(64, 60), 80, false);
        Assert.Equal(1, tray.RenderCount);

        tray.Update(Poll(65, 60), 80, false);
        Assert.Equal(2, tray.RenderCount);

        tray.Update(Poll(65, 60), 80, true);
        Assert.Equal(3, tray.RenderCount);
        tray.Update(Poll(65, 60), 80, true);
        Assert.Equal(3, tray.RenderCount);

        // Limit drops below the shown temperature: number turns red.
        tray.Update(Poll(65, 60), 60, true);
        Assert.Equal(4, tray.RenderCount);
        tray.Update(Poll(65, 60), 61, true);
        Assert.Equal(4, tray.RenderCount);

        tray.Update(Poll(null, null), 80, false);
        Assert.Equal(5, tray.RenderCount);
        Assert.Null(tray.ShownTempC);
    }

    [AvaloniaFact]
    public void Warning_FromBannerOrStaleCard()
    {
        var dir = VmKit.TempDir();
        try
        {
            var vm = VmKit.MainVm(dir);
            vm.OnPolled(VmKit.Poll(VmKit.T0, (Bus5080, Name5080, true)));
            Assert.False(TrayController.IsWarning(vm));

            vm.OnPolled(new PollResult(VmKit.T0.AddSeconds(6), [VmKit.Card(Bus5080, Name5080)],
                Array.Empty<Reading>(), Array.Empty<Alert>(), TimeSpan.Zero));
            Assert.True(TrayController.IsWarning(vm));

            var failed = VmKit.MainVm(dir, Core.Nvml.NvmlReturn.LibraryNotFound);
            Assert.True(TrayController.IsWarning(failed));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public void Menu_ItemsRunActions()
    {
        var calls = new List<string>();
        var window = new Window();
        var tray = new TrayController(Application.Current!, () => { calls.Add("open"); return window; },
            () => calls.Add("settings"), () => calls.Add("report"), () => calls.Add("exit"));

        var items = tray.BuildMenu().Items.OfType<NativeMenuItem>().Where(i => i is not NativeMenuItemSeparator).ToArray();
        Assert.Equal(["Open", "Create diagnostic report", "Settings", "Exit"], items.Select(i => i.Header));
        foreach (var item in items) item.Command!.Execute(null);

        Assert.Equal(["open", "report", "settings", "exit"], calls);
        Assert.True(window.IsVisible);
        window.Close();
    }
}
