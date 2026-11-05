using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GpuMonitor.App.Controls;
using GpuMonitor.App.Views;
using GpuMonitor.Core.Nvml;
using Xunit;
using static GpuMonitor.App.Tests.VmKit;

namespace GpuMonitor.App.Tests;

public class MainWindowTests
{
    private static MainWindow Shown(GpuMonitor.App.ViewModels.MainViewModel vm)
    {
        var window = new MainWindow(vm);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    [AvaloniaFact]
    public void MainWindow_HasThreeTabs()
    {
        var window = Shown(MainVm(TempDir()));

        var tabs = window.GetLogicalDescendants().OfType<TabControl>().Single();
        Assert.Equal(new[] { "Cards", "Diagnostics", "Settings" },
            tabs.Items.OfType<TabItem>().Select(t => t.Header as string));
        Assert.Equal("GPU Monitor", window.Title);
        Assert.Equal(820, window.Width);
        Assert.Equal(620, window.Height);
    }

    [AvaloniaFact]
    public void Close_HidesNotExits()
    {
        var window = Shown(MainVm(TempDir()));
        var closed = false;
        window.Closed += (_, _) => closed = true;

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(closed);
        Assert.False(window.IsVisible);

        window.Show();
        Assert.True(window.IsVisible);
    }

    [AvaloniaFact]
    public void TwoCards_TwoPanels()
    {
        var vm = MainVm(TempDir());
        var window = Shown(vm);
        vm.OnPolled(Poll(T0, ("00000000:02:00.0", "RTX 4070 Ti SUPER", false), ("00000000:01:00.0", "RTX 5080", true)));
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        var panels = window.GetVisualDescendants().OfType<CardPanel>().ToList();
        Assert.Equal(2, panels.Count);
        Assert.Equal("RTX 5080", panels[0].Card!.Name);
        Assert.Equal("RTX 4070 Ti SUPER", panels[1].Card!.Name);
        Assert.All(panels, p => Assert.Equal(80, p.HeatLimitC));
        var left = panels[0].TranslatePoint(default, window)!.Value;
        var right = panels[1].TranslatePoint(default, window)!.Value;
        Assert.Equal(left.Y, right.Y);
        Assert.True(right.X > left.X, "two columns side by side");
    }

    [AvaloniaFact]
    public void Banner_VisibleOnlyWhenSet()
    {
        var ok = Shown(MainVm(TempDir()));
        Assert.False(ok.FindControl<TextBlock>("BannerText")!.IsVisible);

        var failed = Shown(MainVm(TempDir(), NvmlReturn.LibraryNotFound, "nvml.dll not found"));
        var banner = failed.FindControl<TextBlock>("BannerText")!;
        Assert.True(banner.IsVisible);
        Assert.StartsWith("NVML did not load", banner.Text);
    }

    [AvaloniaFact]
    public void SettingsEditors_WriteViewModel()
    {
        var vm = MainVm(TempDir());
        var window = Shown(vm);
        var tabs = window.GetLogicalDescendants().OfType<TabControl>().Single();
        tabs.SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1.0m, window.FindControl<NumericUpDown>("RefreshEditor")!.Value);
        window.FindControl<NumericUpDown>("RefreshEditor")!.Value = 2.5m;
        window.FindControl<NumericUpDown>("HeatEditor")!.Value = 70m;

        Assert.Equal(2.5, vm.Settings.RefreshSeconds);
        Assert.Equal(70, vm.Settings.HeatLimitC);
    }

    [AvaloniaFact]
    public void SettingsEditors_ClearedBoxShowsSavedValue()
    {
        var vm = MainVm(TempDir());
        var window = Shown(vm);
        window.GetLogicalDescendants().OfType<TabControl>().Single().SelectedIndex = 2;
        Dispatcher.UIThread.RunJobs();
        var refresh = window.FindControl<NumericUpDown>("RefreshEditor")!;
        var heat = window.FindControl<NumericUpDown>("HeatEditor")!;

        refresh.Value = null;
        heat.Value = null;
        vm.Settings.Save();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1.0m, refresh.Value);
        Assert.Equal(80m, heat.Value);
        Assert.Equal(1.0, vm.Settings.RefreshSeconds);
    }
}
