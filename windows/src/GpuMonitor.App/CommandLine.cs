namespace GpuMonitor.App;

public sealed record Options(bool Diagnose, string? Output, bool Json, bool NoRedact, bool CrashTest, bool Tray, string? Error);

public static class CommandLine
{
    public const string Usage = "Usage: GpuMonitor [--tray] [--crash-test] [--diagnose [-o <file>] [--json] [--no-redact]]";

    public static Options Parse(string[] args)
    {
        var o = new Options(false, null, false, false, false, false, null);
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--diagnose": o = o with { Diagnose = true }; break;
                case "--json": o = o with { Json = true }; break;
                case "--no-redact": o = o with { NoRedact = true }; break;
                case "--crash-test": o = o with { CrashTest = true }; break;
                case "--tray": o = o with { Tray = true }; break;
                case "-o":
                    if (i + 1 >= args.Length) return o with { Error = "-o needs a file name." };
                    o = o with { Output = args[++i] };
                    break;
                default:
                    return o with { Error = "Unknown option: " + args[i] };
            }
        }

        if (o.Diagnose) return o;
        // Without this, "GpuMonitor --json" would silently open the window instead of a report.
        if (o.Output is not null) return o with { Error = "-o works only with --diagnose." };
        if (o.Json) return o with { Error = "--json works only with --diagnose." };
        if (o.NoRedact) return o with { Error = "--no-redact works only with --diagnose." };
        return o;
    }
}
