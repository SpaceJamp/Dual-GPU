using System.Diagnostics;
using System.Runtime.Versioning;
using GpuMonitor.Core.Diagnostics;

namespace GpuMonitor.App.Platform.Windows;

/// <summary>Runs <c>nvidia-smi -q</c> for the diagnostic report.</summary>
[SupportedOSPlatform("windows")]
public sealed class NvidiaSmiSource : INvidiaSmi
{
    private const string ExeName = "nvidia-smi.exe";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(2);

    public string? QueryAll()
    {
        var exe = Find();
        if (exe is null) return null;

        var psi = new ProcessStartInfo(exe, "-q")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi);
        if (process is null) return null;
        var output = process.StandardOutput.ReadToEndAsync();
        if (!process.WaitForExit(Timeout))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception) { /* already gone */ }
            _ = output.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return "error: timeout";
        }
        if (!output.Wait(ReadTimeout))
        {
            _ = output.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            return "error: timeout";
        }
        return output.GetAwaiter().GetResult();
    }

    internal static string? Find()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = TryCombine(dir.Trim('"'));
            if (candidate is not null) return candidate;
        }
        var systemRoot = Environment.GetEnvironmentVariable("SystemRoot");
        if (!string.IsNullOrEmpty(systemRoot) && TryCombine(Path.Combine(systemRoot, "System32")) is { } sys)
            return sys;
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (!string.IsNullOrEmpty(programFiles) && TryCombine(Path.Combine(programFiles, "NVIDIA Corporation", "NVSMI")) is { } nvsmi)
            return nvsmi;
        return null;
    }

    private static string? TryCombine(string dir)
    {
        try
        {
            var candidate = Path.Combine(dir, ExeName);
            return File.Exists(candidate) ? candidate : null;
        }
        catch (ArgumentException) { return null; }
    }
}
