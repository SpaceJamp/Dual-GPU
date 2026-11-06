using System.Globalization;
using System.Text;
using Avalonia.Threading;
using GpuMonitor.Core;
using GpuMonitor.Core.Logging;
#if WINDOWS
using GpuMonitor.App.Platform.Windows;
#endif

namespace GpuMonitor.App;

/// <summary>Logs unhandled exceptions and writes crash files. It never marks an exception handled.</summary>
public static class CrashHandler
{
    private const int Keep = 3;
    private const int LogTailLines = 40;
    private const string Prefix = "crash-";

    private static readonly object Gate = new();
    private static AppLog _log = AppLog.Null;
    private static string _dir = Paths.CrashDir;
    private static bool _hooked;
    private static DateTime _lastStamp = DateTime.MinValue;
    private static Exception? _lastHandled;

    /// <summary>The exe started to write minidumps. Tests point it at the app, since their own exe is the test host.</summary>
    internal static string? DumpHelper { get; set; } = Environment.ProcessPath;

    /// <summary>Call first in Main. The UI thread is hooked later by <see cref="HookDispatcher"/>.</summary>
    public static void Install(AppLog log, string crashDir)
    {
        Configure(log, crashDir);
        lock (Gate)
        {
            if (_hooked) return;
            _hooked = true;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Handle("AppDomain", e.ExceptionObject as Exception
                ?? new InvalidOperationException("Non-exception object thrown: " + e.ExceptionObject));
        TaskScheduler.UnobservedTaskException += (_, e) => Handle("TaskScheduler", e.Exception);
    }

    /// <summary>Call after Avalonia's platform is set up: reading Dispatcher.UIThread any earlier could
    /// create the dispatcher without the platform's message loop.</summary>
    public static void HookDispatcher() =>
        Dispatcher.UIThread.UnhandledException += (_, e) => Handle("Dispatcher", e.Exception);

    /// <summary>Sets the log and folder without hooking any event (tests use this so the
    /// test host's dispatcher and process-wide handlers are left alone).</summary>
    internal static void Configure(AppLog log, string crashDir)
    {
        lock (Gate)
        {
            _log = log;
            _dir = crashDir;
        }
    }

    /// <summary>Logs and writes one crash. The same exception object seen again (for example
    /// Dispatcher then AppDomain for one crash) is skipped. Returns false when skipped.</summary>
    internal static bool Handle(string source, Exception ex)
    {
        lock (Gate)
        {
            if (ReferenceEquals(ex, _lastHandled)) return false;
            _lastHandled = ex;
        }
        try { _log.Error("crash", "Unhandled exception (" + source + ")", ex); }
        catch (Exception) { /* never throw from a crash handler */ }
        try { WriteCrash(ex); }
        catch (Exception) { /* never throw from a crash handler */ }
        return true;
    }

    /// <summary>Writes crash-&lt;stamp&gt;.txt (and .dmp on Windows), prunes old ones, returns the .txt path.</summary>
    internal static string WriteCrash(Exception ex)
    {
        AppLog log;
        string dir;
        string stem;
        DateTime now;
        lock (Gate)
        {
            log = _log;
            dir = _dir;
            now = DateTime.UtcNow;
            // Keep stems unique and in time order even when two crashes share a millisecond.
            var stamp = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond, now.Kind);
            if (stamp <= _lastStamp) stamp = _lastStamp.AddMilliseconds(1);
            _lastStamp = stamp;
            stem = Prefix + stamp.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
        }

        Directory.CreateDirectory(dir);
        var txt = Path.Combine(dir, stem + ".txt");
        var sb = new StringBuilder();
        sb.Append("Time: ").Append(now.ToString("o", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("Run: ").Append(log.RunId).Append('\n');
        sb.Append('\n').Append(ex.ToString()).Append('\n');
        sb.Append('\n').Append("Last ").Append(LogTailLines).Append(" log lines:").Append('\n');
        foreach (var line in log.Tail(LogTailLines)) sb.Append(line).Append('\n');
        File.WriteAllText(txt, sb.ToString(), new UTF8Encoding(false));

#if WINDOWS
        var dmp = Path.Combine(dir, stem + ".dmp");
        string? dumpError = null;
        try
        {
            dumpError = DumpHelper is { } helper ? MiniDump.WriteViaHelper(helper, dmp) : "The executable path is unknown";
        }
        catch (Exception dumpEx)
        {
            dumpError = "Minidump failed: " + dumpEx.GetType().Name + ": " + dumpEx.Message;
        }
        if (dumpError is not null)
        {
            try { File.Delete(dmp); }
            catch (Exception) { /* nothing more to do */ }
            try { log.Error("crash", dumpError); }
            catch (Exception) { /* never throw from a crash handler */ }
            try { File.AppendAllText(txt, "\n" + dumpError + "\n", new UTF8Encoding(false)); }
            catch (Exception) { /* the text file already holds the exception */ }
        }
#endif

        try { Prune(dir); }
        catch (Exception) { /* old files staying around is harmless */ }
        return txt;
    }

    private static void Prune(string dir)
    {
        var files = Directory.GetFiles(dir, Prefix + "*")
            .Where(f => f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var keep = files.Select(Path.GetFileNameWithoutExtension)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.Ordinal)
            .TakeLast(Keep)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (keep.Contains(Path.GetFileNameWithoutExtension(f))) continue;
            try { File.Delete(f); }
            catch (Exception) { /* try the rest */ }
        }
    }
}
