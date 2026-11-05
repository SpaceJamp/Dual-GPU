using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using GpuMonitor.Core.Diagnostics;
using Microsoft.Win32;

namespace GpuMonitor.App.Platform.Windows;

[SupportedOSPlatform("windows")]
public sealed partial class RegistrySource : IRegistrySource, IWindowsInfo
{
    internal const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string RunValueName = "GpuMonitor";
    private const string GpuPreferencesPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    private const string GraphicsDriversPath = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string GameBarPath = @"Software\Microsoft\GameBar";
    private const string CurrentVersionPath = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";

    public IReadOnlyDictionary<string, string> GpuPreferences()
    {
        var result = new Dictionary<string, string>();
        using var key = Registry.CurrentUser.OpenSubKey(GpuPreferencesPath);
        if (key is null) return result;
        foreach (var name in key.GetValueNames())
            result[name] = key.GetValue(name)?.ToString() ?? "";
        return result;
    }

    public string? RunValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName)?.ToString();
    }

    public int? HwSchMode()
    {
        using var key = Registry.LocalMachine.OpenSubKey(GraphicsDriversPath);
        return key?.GetValue("HwSchMode") is int v ? v : null;
    }

    public bool? GameMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(GameBarPath);
        return key?.GetValue("AutoGameModeEnabled") is int v ? v != 0 : true;
    }

    public TimeSpan Uptime() => TimeSpan.FromMilliseconds(Environment.TickCount64);

    public string Version()
    {
        var info = new OsVersionInfoEx { OSVersionInfoSize = (uint)Unsafe.SizeOf<OsVersionInfoEx>() /* 284 bytes, blittable */ };
        var status = RtlGetVersion(ref info);
        if (status != 0)
            throw new InvalidOperationException("RtlGetVersion failed: 0x" + status.ToString("X8", CultureInfo.InvariantCulture));
        var text = string.Create(CultureInfo.InvariantCulture, $"{info.MajorVersion}.{info.MinorVersion}.{info.BuildNumber}");
        using var key = Registry.LocalMachine.OpenSubKey(CurrentVersionPath);
        if (key?.GetValue("UBR") is int ubr)
            text += "." + ubr.ToString(CultureInfo.InvariantCulture);
        return text;
    }

    // NTSTATUS RtlGetVersion(PRTL_OSVERSIONINFOW lpVersionInformation); accepts RTL_OSVERSIONINFOEXW.
    [LibraryImport("ntdll.dll")]
    private static partial int RtlGetVersion(ref OsVersionInfoEx info);

    // OSVERSIONINFOEXW (winnt.h). WCHAR szCSDVersion[128] is held as 128 UInt16 so the struct stays blittable.
    [StructLayout(LayoutKind.Sequential)]
    private struct OsVersionInfoEx
    {
        public uint OSVersionInfoSize;
        public uint MajorVersion;
        public uint MinorVersion;
        public uint BuildNumber;
        public uint PlatformId;
        public CsdVersion CSDVersion;
        public ushort ServicePackMajor;
        public ushort ServicePackMinor;
        public ushort SuiteMask;
        public byte ProductType;
        public byte Reserved;
    }

    [InlineArray(128)]
    private struct CsdVersion
    {
        private ushort _first;
    }
}
