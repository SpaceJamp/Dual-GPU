using System.IO.Pipes;
using System.Text;

namespace GpuMonitor.App;

/// <summary>Keeps one instance per user session. The first instance holds a named mutex and listens on a
/// named pipe; a later instance sends "show" through the pipe and exits.</summary>
public sealed class SingleInstance : IDisposable
{
    public const string AppId = "SpaceJamp.GpuMonitor";
    private const string ShowLine = "show";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly Action _onShowRequested;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _server;
    private int _disposed;

    private SingleInstance(Mutex mutex, string id, Action onShowRequested)
    {
        _mutex = mutex;
        _pipeName = PipeName(id);
        _onShowRequested = onShowRequested;
        _server = Task.Run(ServeAsync);
    }

    private static string MutexName(string id) => @"Local\" + id;

    private static string PipeName(string id) => id + ".show";

    /// <summary>Returns the instance when this process is the first, or null when another one runs.
    /// <paramref name="onShowRequested"/> runs on a background thread for every "show" request.</summary>
    public static SingleInstance? TryAcquire(string id, Action onShowRequested)
    {
        var mutex = new Mutex(initiallyOwned: true, MutexName(id), out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return null;
        }

        return new SingleInstance(mutex, id, onShowRequested);
    }

    /// <summary>Asks the running instance to show its window. False when none answers within 2 s.</summary>
    public static bool SignalExisting(string id)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName(id), PipeDirection.Out, PipeOptions.CurrentUserOnly);
            pipe.Connect(ConnectTimeout);
            var bytes = Encoding.UTF8.GetBytes(ShowLine + "\n");
            pipe.Write(bytes);
            pipe.Flush();
            return true;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task ServeAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(token).ConfigureAwait(false);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(ConnectTimeout);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line == ShowLine) _onShowRequested();
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // A bad client, a timed-out read or a failing callback must not stop the server.
                // Wait a moment so a persistent error does not spin.
                try { await Task.Delay(100, token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Stops the pipe server and releases the mutex.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        try { _server.Wait(ConnectTimeout); }
        catch (AggregateException) { /* ServeAsync catches its own errors */ }
        _stop.Dispose();

        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { /* disposed on another thread than the one that acquired it */ }
        _mutex.Dispose();
    }
}
