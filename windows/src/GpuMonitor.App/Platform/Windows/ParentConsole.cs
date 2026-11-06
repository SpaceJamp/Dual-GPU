using System.Runtime.InteropServices;

namespace GpuMonitor.App.Platform.Windows;

/// <summary>The exe is WinExe and starts with no console. --diagnose attaches to the terminal that launched it.</summary>
internal static partial class ParentConsole
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    /// <summary>Started outside a terminal, this fails and output goes nowhere, which is fine with -o.</summary>
    public static void Attach() => AttachConsole(AttachParentProcess);
}
