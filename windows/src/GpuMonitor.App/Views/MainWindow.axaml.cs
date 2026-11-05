using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GpuMonitor.App.ViewModels;

namespace GpuMonitor.App.Views;

public partial class MainWindow : Window
{
    /// <summary>For the XAML previewer only.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(MainViewModel vm) : this()
    {
        DataContext = vm;
        vm.Diagnostics.CopyText ??= CopyToClipboard;
    }

    private Task CopyToClipboard(string text) =>
        Clipboard is { } clipboard ? clipboard.SetTextAsync(text) : Task.CompletedTask;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!App.IsExiting)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private async void OnSaveReport(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save report",
                SuggestedFileName = "gpumonitor-report.txt",
                DefaultExtension = "txt",
            });
            if (file?.TryGetLocalPath() is { } path) vm.Diagnostics.SaveReport(path);
        }
        catch (Exception ex)
        {
            vm.Diagnostics.ReportError("Could not save the report: " + ex.Message, ex);
        }
    }
}
