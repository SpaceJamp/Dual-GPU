using Avalonia;
using GpuMonitor.Core;
using GpuMonitor.Core.Diagnostics;
using GpuMonitor.Core.Logging;

namespace GpuMonitor.App;

public static class Program
{
    private const int ExitBadArguments = 2;
    private const int ExitUnexpected = 70;

    [STAThread]
    public static int Main(string[] args)
    {
#if WINDOWS
        // Started by CrashHandler. It must stay outside the crashing process; see MiniDump.
        if (args is [Platform.Windows.MiniDump.HelperArgument, var processId, var path])
            return Platform.Windows.MiniDump.RunHelper(processId, path);
#endif

        var options = CommandLine.Parse(args);
        if (options.Diagnose || options.Error is not null) Platform.Platform.AttachParentConsole();
        if (options.Error is not null)
        {
            Console.Error.WriteLine(options.Error);
            Console.Error.WriteLine(CommandLine.Usage);
            return ExitBadArguments;
        }

        using var log = new AppLog(Paths.LogFile, LogLevel.Info, false);
        try
        {
            CrashHandler.Install(log, Paths.CrashDir);
            if (options.CrashTest) throw new InvalidOperationException("Crash test");
            if (options.Diagnose)
            {
                using var boot = Bootstrap.ForThisPc(log);
                return RunDiagnose(options, boot.Collector(), Console.Out, Console.Error);
            }
            return RunApp(options, log, args);
        }
        catch (Exception ex)
        {
            CrashHandler.Handle("Main", ex);
            Console.Error.WriteLine("Unexpected error: " + ex.Message + " (run " + log.RunId + ", see " + Paths.LogFile + ")");
            return ExitUnexpected;
        }
    }

    /// <summary>Writes the report and returns the process exit code.</summary>
    internal static int RunDiagnose(Options options, FactCollector collector, TextWriter stdout, TextWriter stderr)
    {
        var facts = collector.Collect();
        var checks = Checks.Run(facts);
        var redact = !options.NoRedact;
        var report = options.Json ? ReportRender.Json(facts, checks, redact) : ReportRender.Text(facts, checks, redact);

        if (options.Output is null)
        {
            stdout.Write(report);
        }
        else
        {
            try { File.WriteAllText(options.Output, report); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                stderr.WriteLine("Could not write " + options.Output + ": " + ex.Message);
                return ExitBadArguments;
            }
        }
        return ReportRender.ExitCode(checks);
    }

    private static int RunApp(Options options, AppLog log, string[] args)
    {
        using var single = SingleInstance.TryAcquire(SingleInstance.AppId, () => App.Shell?.RequestOpen());
        if (single is null)
        {
            if (!SingleInstance.SignalExisting(SingleInstance.AppId))
                log.Warn("startup", "Another instance is running but did not answer.");
            return 0;
        }

        using var boot = Bootstrap.ForThisPc(log);
        App.Boot = boot;
        App.StartHidden = options.Tray;
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect();
}
