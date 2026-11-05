using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace GpuMonitor.App.Platform.Windows;

/// <summary>Writes a minidump of this process through dbghelp.dll.</summary>
[SupportedOSPlatform("windows")]
public static partial class MiniDump
{
    private const uint MiniDumpWithIndirectlyReferencedMemory = 0x00000040;
    private const uint MiniDumpWithThreadInfo = 0x00001000;
    private static readonly object Gate = new();

    /// <summary>Returns false when dbghelp reports a failure.</summary>
    public static bool Write(string path) => Write(path, out _);

    /// <summary>Returns false when dbghelp reports a failure; lastError is then GetLastError (an HRESULT).</summary>
    public static bool Write(string path, out int lastError)
    {
        lastError = 0;
        // dbghelp functions are single-threaded; serialize calls.
        lock (Gate)
        {
            using var process = Process.GetCurrentProcess();
            using var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            var ok = MiniDumpWriteDump(
                process.Handle,
                (uint)process.Id,
                file.SafeFileHandle,
                MiniDumpWithThreadInfo | MiniDumpWithIndirectlyReferencedMemory,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);
            if (!ok) lastError = Marshal.GetLastPInvokeError();
            return ok;
        }
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
