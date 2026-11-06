# GPU Monitor for Windows

A tray app that watches your NVIDIA cards through NVML: load, VRAM, temperature, power, clocks and throttle reasons. It alerts on a hot card, low VRAM or a card that stops reporting, and it collects detailed diagnostics. It only reads; it changes no GPU setting.

## Install

1. Open the latest successful [windows workflow run](https://github.com/SpaceJamp/Dual-GPU/actions/workflows/windows.yml) and download the `GpuMonitor-win-x64` artifact.
2. Unzip it into a folder of your choice. Keep `GpuMonitor.exe` and `icon.png` together. The exe is about 42 MB and needs no .NET install.
3. Run `GpuMonitor.exe`. The exe is not signed, so SmartScreen warns on first run: choose **More info**, then **Run anyway**.

You need the NVIDIA driver, which installs `nvml.dll`. Without it the window says so and the app tries again every 30 seconds.

## Use

- The tray icon shows the hotter card's temperature, or `!` when NVML failed or a card stopped reporting. Left-click opens the window. Right-click for Open, Create diagnostic report, Settings and Exit.
- Closing the window hides it to the tray. Exit in the tray menu quits.
- A second launch brings the running window forward.
- **Start with Windows** in Settings starts the app hidden in the tray at sign-in. It needs no admin rights.

## Where files live

| What | Where |
|---|---|
| Settings | `%APPDATA%\GpuMonitor\settings.json` |
| Log, 1 MB per file, 3 backups | `%LOCALAPPDATA%\GpuMonitor\logs\gpumonitor.log` |
| Crash text files and minidumps, last 3 | `%LOCALAPPDATA%\GpuMonitor\logs\crash` |
| CSV recordings from the Record button | `%LOCALAPPDATA%\GpuMonitor\recordings` |

## Command line

```
GpuMonitor.exe --diagnose                  print the report in the terminal
GpuMonitor.exe --diagnose -o report.txt    save it to a file
GpuMonitor.exe --diagnose --json -o r.json save it as JSON
GpuMonitor.exe --diagnose --no-redact      keep your user name, computer name and home path
GpuMonitor.exe --tray                      start hidden in the tray
GpuMonitor.exe --crash-test                throw on purpose to test the crash files
```

In PowerShell, read the exit code with `$LASTEXITCODE`. PowerShell doesn't wait for a windowed exe, so use `Start-Process -Wait` or pipe through `| Out-Default` before reading it.

| Exit code | Meaning |
|---|---|
| 0 | OK |
| 1 | `--diagnose` found a failed check |
| 2 | Bad arguments, or the report file could not be written |
| 70 | Unexpected error; see the log and crash files |

## Remove

1. Choose Exit in the tray menu.
2. If you turned on Start with Windows, turn it off first, or delete the `GpuMonitor` value under `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`.
3. Delete the app folder, `%APPDATA%\GpuMonitor` and `%LOCALAPPDATA%\GpuMonitor`.
4. Delete the notification registration: `HKCU\Software\Classes\AppUserModelId\SpaceJamp.GpuMonitor`.

## Build

Needs the .NET 10 SDK.

```
dotnet test windows/GpuMonitor.slnx
dotnet publish windows/src/GpuMonitor.App -c Release -f net10.0-windows10.0.19041.0 -o windows/publish
```

`TESTING.md` is the manual checklist for a real PC.
