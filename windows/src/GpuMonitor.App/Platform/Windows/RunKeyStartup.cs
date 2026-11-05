using System.Runtime.Versioning;
using Microsoft.Win32;

namespace GpuMonitor.App.Platform.Windows;

/// <summary>Start on sign-in through HKCU\Software\Microsoft\Windows\CurrentVersion\Run.</summary>
[SupportedOSPlatform("windows")]
public sealed class RunKeyStartup : IStartup
{
    private readonly string _exePath;
    private readonly string _valueName;

    public RunKeyStartup(string? exePath = null, string valueName = RegistrySource.RunValueName)
    {
        _exePath = exePath ?? Environment.ProcessPath
            ?? throw new InvalidOperationException("The executable path is unknown.");
        _valueName = valueName;
    }

    private string Command => "\"" + _exePath + "\" --tray";

    public bool IsEnabled() => string.Equals(Value(), Command, StringComparison.OrdinalIgnoreCase);

    public void Set(bool on)
    {
        if (on)
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistrySource.RunKeyPath, writable: true);
            key.SetValue(_valueName, Command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistrySource.RunKeyPath, writable: true);
            key?.DeleteValue(_valueName, throwOnMissingValue: false);
        }
    }

    internal string? Value()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistrySource.RunKeyPath);
        return key?.GetValue(_valueName)?.ToString();
    }
}
