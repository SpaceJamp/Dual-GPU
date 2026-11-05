using System.Windows.Input;

namespace GpuMonitor.App.ViewModels;

/// <summary>Minimal ICommand over an action or an async action. An async command is disabled while it runs.
/// Any exception from the body goes to <see cref="OnError"/>; none escapes to the dispatcher.</summary>
public sealed class RelayCommand : ICommand
{
    private readonly Func<Task> _run;
    private bool _busy;

    public RelayCommand(Action run) : this(() => { run(); return Task.CompletedTask; }) { }

    public RelayCommand(Func<Task> run) => _run = run;

    public event EventHandler? CanExecuteChanged;

    /// <summary>Receives exceptions the body did not handle. MainViewModel wires it to the log and Status.</summary>
    public Action<Exception>? OnError { get; set; }

    public bool CanExecute(object? parameter) => !_busy;

    public async void Execute(object? parameter)
    {
        if (_busy) return;
        SetBusy(true);
        try { await _run(); }
        catch (Exception ex)
        {
            try { OnError?.Invoke(ex); }
            catch (Exception) { /* an error handler must not crash the dispatcher */ }
        }
        finally { SetBusy(false); }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
