using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GpuMonitor.App.Platform.Windows;

/// <summary>Writes a minidump through dbghelp.dll, from a separate process. MiniDumpWriteDump suspends every other
/// thread of the target, so calling it on our own process deadlocks forever if one of them holds the loader
/// or heap lock. Microsoft's documentation says to call it from another process for this reason.</summary>
[SupportedOSPlatform("windows")]
public static partial class MiniDump
{
    /// <summary>The argument that makes the exe act as the dump helper.</summary>
    public const string HelperArgument = "--write-dump";

    private const uint MiniDumpWithIndirectlyReferencedMemory = 0x00000040;
    private const uint MiniDumpWithThreadInfo = 0x00001000;
    private const int HelperOk = 0;
    private const int HelperDumpFailed = 1;
    private const int HelperBadTarget = 3;
    private static readonly TimeSpan HelperTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Starts <paramref name="helperExe"/> to dump a process and waits for it. Returns null on success,
    /// or the reason it failed.</summary>
    /// <param name="processId">The process to dump; this one by default.</param>
    public static string? WriteViaHelper(string helperExe, string path, int? processId = null)
    {
        var start = new ProcessStartInfo(helperExe) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(HelperArgument);
        start.ArgumentList.Add((processId ?? Environment.ProcessId).ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(path);

        try
        {
            using var helper = Process.Start(start);
            if (helper is null) return "Dump helper did not start";
            if (!helper.WaitForExit(HelperTimeout))
            {
                helper.Kill();
                return "Dump helper timed out after " + HelperTimeout.TotalSeconds + " s";
            }
            return helper.ExitCode == HelperOk ? null : "Dump helper failed with exit code " + helper.ExitCode;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "Dump helper did not start: " + ex.Message;
        }
    }

    /// <summary>The helper process's whole job: dump the given process and return the exit code.</summary>
    public static int RunHelper(string processId, string path)
    {
        if (!int.TryParse(processId, CultureInfo.InvariantCulture, out var id)) return HelperBadTarget;
        try
        {
            using var target = Process.GetProcessById(id);
            using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            return Retry(() =>
            {
                file.SetLength(0);
                return MiniDumpWriteDump(
                    target.Handle,
                    (uint)id,
                    file.SafeFileHandle,
                    MiniDumpWithThreadInfo | MiniDumpWithIndirectlyReferencedMemory,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);
            }) ? HelperOk : HelperDumpFailed;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException
            or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return HelperBadTarget;
        }
    }

    /// <summary>Dumps fail now and then while the target's threads are busy (measured about 1 in 60).</summary>
    internal static bool Retry(Func<bool> dump)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (dump()) return true;
        }
        return false;
    }

    // BOOL MiniDumpWriteDump(HANDLE hProcess, DWORD ProcessId, HANDLE hFile, MINIDUMP_TYPE DumpType,
    //   PMINIDUMP_EXCEPTION_INFORMATION, PMINIDUMP_USER_STREAM_INFORMATION, PMINIDUMP_CALLBACK_INFORMATION);
    [LibraryImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        SafeFileHandle hFile,
        uint dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);
}
