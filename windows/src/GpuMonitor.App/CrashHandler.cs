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

    public static void Install(AppLog log, string crashDir)
    {
        lock (Gate)
        {
            _log = log;
            _dir = crashDir;
            if (_hooked) return;
            _hooked = true;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Handle("AppDomain", e.ExceptionObject as Exception
                ?? new InvalidOperationException("Non-exception object thrown: " + e.ExceptionObject));
        TaskScheduler.UnobservedTaskException += (_, e) => Handle("TaskScheduler", e.Exception);
        Dispatcher.UIThread.UnhandledException += (_, e) => Handle("Dispatcher", e.Exception);
    }

    private static void Handle(string source, Exception ex)
    {
        try { _log.Error("crash", "Unhandled exception (" + source + ")", ex); }
        catch (Exception) { /* never throw from a crash handler */ }
        try { WriteCrash(ex); }
        catch (Exception) { /* never throw from a crash handler */ }
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
            now = DateTime.Now;
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
        try
        {
            if (!MiniDump.Write(dmp)) File.Delete(dmp);
        }
        catch (Exception) { /* the text file is the part that matters */ }
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
