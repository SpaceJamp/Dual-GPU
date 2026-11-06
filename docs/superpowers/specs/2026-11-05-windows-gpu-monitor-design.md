# Windows GPU Monitor: Part 1 design

Date: 2026-11-05
Status: draft, waiting for review

## Goal

A Windows desktop app that monitors both NVIDIA cards in the user's PC:

- RTX 5080, which drives the monitor.
- RTX 4070 Ti SUPER, the second card.

Update 2026-11-05: the PC runs only the RTX 5080 for now. The 4070 Ti SUPER is
not installed because it does not fit in the case, so one card is a supported
setup and Parts 2 and 3 below are shelved until it is in.

The app sits in the system tray, opens a window with live readings, alerts
through Windows notifications, and gathers detailed diagnostics to track
down driver, hardware and app problems.

## The three parts

The full Windows app has three jobs. Each part gets its own spec, plan and
build. This spec covers Part 1 only.

1. Monitor (this spec): readings, tray, window, alerts, diagnostics.
2. Games: choose which card each game runs on, through the per-app GPU
   choice Windows stores in
   `HKCU\Software\Microsoft\DirectX\UserGpuPreferences`. Part 2 starts with
   a test on the user's PC, because it is unknown whether Windows can tell
   two NVIDIA cards apart there.
3. AI: start llama.cpp on one card or split across both, using the free
   VRAM that Part 1 reads.

Parts 2 and 3 reuse the card readings from Part 1.

## Decisions

| Topic | Decision |
|---|---|
| Language and UI | C#, Avalonia 12. Card panels and graphs are the app's own controls, drawn directly. No third-party control or chart libraries. |
| Runtime | .NET 10 (LTS, supported to November 2028). .NET 8 support ends 2026-11-10. |
| Location | `windows/` in this repo. The Linux `gpubalance` stays unchanged. |
| How the user sees it | Tray icon and window. No in-game overlay. |
| Refresh | Every 1 second |
| Alerts | Hot card (80°C default), low VRAM (90% used default), card missing |
| Start with Windows | Optional checkbox, off by default |
| Data source | NVML (`nvml.dll`, installed with the NVIDIA driver) |
| Card identity | PCI bus ID. Both cards are NVIDIA, so vendor or name alone is ambiguous. |
| Packaging | Self-contained, trimmed, single-file `GpuMonitor.exe` for win-x64, shipped as a zip |
| CI | GitHub Actions on a Windows runner, triggered by changes under `windows/` |

## Out of scope for Part 1

- Changing any GPU setting, game assignment or clocks.
- In-game overlay.
- Saved reading history, other than the manual Record button.
- AMD and Intel cards. The NVML reader handles NVIDIA only. The
  `ICardReader` interface leaves room for other readers later.
- Installer, code signing, auto-update.

## Layout

```
windows/
  GpuMonitor.sln
  Directory.Build.props        shared version and settings
  src/
    GpuMonitor.Core/           net10.0, no UI code, builds on Linux
    GpuMonitor.App/            net10.0, Avalonia 12
      Controls/                the app's own drawn controls
      Platform/Windows/        Windows-only code behind interfaces
  tests/
    GpuMonitor.Core.Tests/     xUnit, net10.0
  TESTING.md                   manual checklist for the user's PC
  README.md                    how to build, run and read the logs
.github/workflows/windows.yml
```

## Components

### GpuMonitor.Core

No UI code and no Windows-only APIs at compile time, so it builds and its tests
run on Linux and Windows. Windows-only calls (WMI, Event Log, registry,
notifications) sit behind interfaces and are implemented in
`GpuMonitor.App/Platform/Windows`.

- `Nvml`: P/Invoke bindings to `nvml.dll`. Wraps each call, maps return
  codes to names (for example `NVML_ERROR_GPU_IS_LOST`) and never throws for
  a single failed field.
- `INvml`: the interface over the bindings. Tests use `FakeNvml`.
- `ICardReader` / `NvmlReader`: reads one `Reading` per card per poll.
- `Card`: PCI bus ID (key), name, UUID, whether it drives a monitor.
- `Reading`: timestamp, per-field value or error code. Fields: load %,
  VRAM used and total (MiB), temperature (°C), power draw and limit (W),
  clocks, fan %, performance state, throttle reasons.
- `Units`: conversions from raw NVML values (milliwatts to watts, bytes to
  MiB). Every conversion has a test.
- `AlertRules`: decides when to notify. Pure logic, no clock or UI.
  - Hot: fires once when temperature reaches the limit. Re-arms when it
    drops 5°C below the limit.
  - Low VRAM: fires once when use reaches the limit %. Re-arms 5 points
    below.
  - Card missing: fires when a known card gives no reading for 5 seconds.
    Fires "card back" when it returns.
  - Each alert is per card.
- `Settings`: limits, refresh rate, log level, log format, start with
  Windows. JSON at `%APPDATA%\GpuMonitor\settings.json`. A missing or corrupt
  file falls back to defaults, logs a warning and keeps the bad file as
  `settings.json.bad`.
- `History`: the last 60 readings per card, in memory only.
- `Log`: rolling log, see Diagnostics.
- `Diagnostics`: fact collection, checks, redaction, text and JSON output.
- `Recorder`: writes readings to CSV while recording is on.

### GpuMonitor.App

- Tray icon: shows the hotter card's temperature. Left-click opens the
  window. Right-click menu: Open, Create diagnostic report, Settings, Exit.
  Turns to a warning icon when NVML fails or a card is missing.
- Main window, one tab each:
  - Cards: one panel per card, side by side. Name, a "Display" marker on the
    card driving a monitor, current numbers, and 60-second graphs for load
    and temperature.
  - Diagnostics: see below.
  - Settings: limits, refresh rate, log level and format, start with
    Windows.
- Own controls in `Controls/`, drawn with Avalonia's `DrawingContext`:
  - `CardPanel`: name, Display marker, numbers, gray "No reading since"
    state.
  - `LineGraph`: 60-second line graph with limit line. Redraws only when a
    new reading arrives.
  - `Meter`: horizontal bar for load, VRAM and power against its max.
  - Colors come from one theme file with light and dark variants that
    follow the Windows setting.
- Tray: Avalonia's built-in `TrayIcon` with its menu. The temperature
  number on the icon is drawn into a bitmap each poll.
- Notifications: Avalonia has no built-in Windows toast API (observation:
  its TrayIcon docs cover icon, tooltip and menu only). `INotifier` has two
  implementations:
  - `WindowsToastNotifier`: native Windows toast through the built-in
    `Windows.UI.Notifications` API, with no extra package. The app registers
    its ID under `HKCU\Software\Classes\AppUserModelId\SpaceJamp.GpuMonitor`.
    The Microsoft.Toolkit.Uwp.Notifications package is not used: it pulls in
    System.Drawing.Common 4.7.0, flagged critical (GHSA-rxg9-xrhp-64gj).
  - `InAppNotifier`: an Avalonia in-window notification. Used if toasts
    fail, and logged as a warning.
- Running on Linux is only for tests here. The shipped app targets
  Windows. Startup, WMI, Event Log and toasts report "not available" off
  Windows.
- Start with Windows: adds or removes a value under
  `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`. User only, no admin.
- Closing the window hides it to the tray. Exit in the tray menu quits.
- Single instance: a second launch brings the running window forward.
- Implements the Windows-only fact sources for Diagnostics: WMI display
  adapters, Event Log, registry values.

## Data flow

1. A timer fires every second on a background thread.
2. `NvmlReader` returns one `Reading` per card.
3. `AlertRules` compares the readings with the limits and returns alerts to
   send.
4. `History` stores the readings. `Recorder` writes them if recording.
5. The UI thread updates panels, graphs and the tray icon, and sends
   notifications.

A poll that is still running when the next one is due is skipped and
logged, so polls never pile up.

## Error handling

- `nvml.dll` missing or fails to load: the window explains it with the
  hint from the diagnostic check. The tray shows a warning. The app retries
  every 30 seconds.
- A card stops answering: its panel turns gray with "No reading since
  <time>". The card-missing alert fires. The app keeps checking every 5
  seconds until the card answers.
- A single field fails: that value shows blank. The failure is logged once
  per card and field until it succeeds again. Other fields keep updating.
- Log write fails: the app keeps running and shows a warning in the window.
- Unhandled exceptions: caught on the UI thread, background threads and
  unobserved tasks. Each is logged with a stack trace and gets a crash file.

## Diagnostics

Rebuilt from the behavior of the Linux `gpubalance diagnose` and log:
facts never throw, each check is pass, info, warn or fail with a hint, logs
rotate at 1 MB with 3 backups, every line carries a run ID, and reports hide
personal names and paths.

### Log file

- Path: `%LOCALAPPDATA%\GpuMonitor\logs\gpumonitor.log`. 1 MB per file,
  3 backups.
- Line fields: time, run ID, level, source, message. JSON lines optional.
- Levels: debug, info, warning, error. Changeable at runtime, no restart.
- Records:
  - Startup facts: app, .NET and Windows versions, driver version, cards
    found, settings loaded.
  - Every failed NVML call with its error code name.
  - Alerts firing and re-arming, with the triggering value.
  - Cards appearing and disappearing.
  - Settings changes.
  - Polls slower than 100 ms, with the time per call.
  - Unhandled exceptions with stack traces.

### Crash files

- Folder: `%LOCALAPPDATA%\GpuMonitor\logs\crash`. The last 3 are kept.
- Each crash writes a text file (exception, stack trace, run ID, last 40 log
  lines) and a minidump through `MiniDumpWriteDump`. No admin rights and no
  Windows Error Reporting changes.
- `GpuMonitor.exe --crash-test` throws on purpose to test this.

### Diagnostic report

- Opened from a button in the window, the tray menu, or
  `GpuMonitor.exe --diagnose [-o <file>] [--json] [--no-redact]`.
- Exit code 1 if any check fails, 0 otherwise.
- Window actions: copy to clipboard, save, open log folder.

Facts:

- App: version, git commit, .NET runtime version.
- Windows: version and build, uptime, Hardware-accelerated GPU scheduling
  (`HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode`), Game
  Mode.
- Driver: NVIDIA driver version, NVML version, CUDA driver version, path and
  file version of the loaded `nvml.dll`.
- Per card:
  - Identity: name, PCI bus ID, UUID, VBIOS version.
  - PCIe: current and max generation and lane width.
  - Display attached.
  - Temperature: current, slowdown limit, shutdown limit.
  - Power: draw, current limit, default limit.
  - Clocks: graphics, memory, SM; current and max.
  - Fan %, performance state, active throttle reasons.
  - VRAM used and total.
  - Processes on the card: name, PID, VRAM used.
- WMI `Win32_VideoController`: name, driver version, PNP device ID, status
  for every display adapter.
- `nvidia-smi -q` output, if `nvidia-smi` is found.
- Contents of `HKCU\Software\Microsoft\DirectX\UserGpuPreferences`.
- Windows Event Log, last 7 days: entries from `nvlddmkm` and Display event
  4101.
- App state: settings file, start-with-Windows value, alert history, poll
  timing per NVML call (min, average, max, 99th percentile), last 40 log
  lines.

Any fact that cannot be read is recorded as an error string.

Checks:

| ID | Fails or warns when | Level |
|---|---|---|
| nvml-load | `nvml.dll` does not load or init fails | fail |
| card-count | NVML finds no cards | fail |
| wmi-match | WMI and NVML list different NVIDIA cards | warn |
| display-card | No card or more than one drives a monitor, or it is not the 5080 | warn |
| pcie-width | Current lane width below the card's max | warn |
| temp-headroom | Temperature within 5°C of the slowdown limit | warn |
| throttle | Thermal, power cap or other slowdown reason active | info, warn for thermal |
| driver-errors | `nvlddmkm` or Display 4101 events in the last 7 days | warn |
| settings | Settings file missing (info) or invalid (warn) | info / warn |
| log-writable | Log folder cannot be written | warn |
| poll-time | Average poll above 100 ms | warn |

PCIe generation is not checked, because cards lower it at idle to save
power.

Redaction: user name, computer name and home path are replaced, in plain
form (`C:\Users\name`) and JSON-escaped form (`C:\\Users\\name`).
`--no-redact` keeps them.

### Diagnostics tab

- Every field for each card, live, with the result code of its last NVML
  call.
- Poll timing, live.
- Recent GPU entries from the Event Log.
- Live log view with a level filter.

### Recording

- A Record button writes all fields for both cards to CSV, once per poll,
  until stopped.
- File: `%LOCALAPPDATA%\GpuMonitor\recordings\<date>-<time>.csv`.

## Install

- `dotnet publish` self-contained, trimmed, single file, win-x64. Bindings
  are compiled (`x:CompileBindings`) so trimming does not break them. Shipped as
  `GpuMonitor-<version>-win-x64.zip`.
- No installer. Removing the app means deleting the folder plus
  `%APPDATA%\GpuMonitor` and `%LOCALAPPDATA%\GpuMonitor`.
- The exe is unsigned, so SmartScreen warns on first run. `README.md` says
  how to get past it.

## CI

`.github/workflows/windows.yml`:

- Runs on `windows-latest` for pushes and pull requests that change
  `windows/` or the workflow.
- Steps: set up .NET 10, restore, build, run tests, publish, upload the zip
  as a run artifact.

## Testing

Automated (`GpuMonitor.Core.Tests`, run in CI, also on Linux):

- `AlertRules`: fires once, re-arms after the margin, per-card state,
  card-missing timing and "card back".
- `Units` and `NvmlReader` against `FakeNvml`: conversions, per-field errors,
  card lost and found.
- `Settings`: defaults, round trip, corrupt file handling.
- Redaction: plain and JSON-escaped paths, user and computer names.
- Each diagnostic check: given fake facts, returns the right level and hint.
- `Log`: rotation at 1 MB with 3 backups, run ID on every line, JSON format.
- `Recorder`: CSV header and rows.

UI, with Avalonia headless tests (run in CI, also on Linux):

- `CardPanel` shows values, blanks for failed fields, and the gray state.
- `LineGraph` keeps 60 points and draws the limit line.
- Window opens, tabs switch, settings changes save.
- Closing the window hides it; Exit quits.

Manual (`windows/TESTING.md`, on the user's PC):

- Both cards show with the right names. "Display" is on the 5080.
- Heat limit lowered to 40°C gives one toast, not one per second.
- Disabling the 4070 Ti SUPER in Device Manager gives "Card missing";
  enabling it gives "card back".
- `--diagnose` runs and every check reads correctly.
- Start with Windows takes effect after sign-out and sign-in.
- `--crash-test` leaves a crash text file and a minidump.
- Record writes a CSV that opens in Excel.

## Repo changes outside windows/

- `CLAUDE.md`: note that `windows/` is a Windows app the user asked for and
  must stay, and list its build and test commands. The Linux-only rule stays
  for `gpubalance`.
- `.github/workflows/windows.yml`: new.
