# Windows GPU Monitor (Part 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A Windows tray app that monitors the RTX 5080 and RTX 4070 Ti SUPER through NVML, alerts on heat, VRAM and missing cards, and collects deep diagnostics.

**Architecture:** `GpuMonitor.Core` holds all logic with no UI and no Windows-only calls, behind interfaces (`INvml`, fact sources, `INotifier`). `GpuMonitor.App` is an Avalonia 12 app with its own drawn controls; Windows-only code lives in `Platform/Windows` and compiles only for the Windows target. Both projects build and test on Linux; CI also builds and publishes on Windows.

**Tech Stack:** C# 14, .NET 10 (SDK 10.0.403 verified), Avalonia 12.1.5, xUnit v3 3.2.2, Avalonia.Headless.XUnit 12.1.5, System.Management 10.0.14, System.Diagnostics.EventLog 10.0.14.

**Spec:** `docs/superpowers/specs/2026-11-05-windows-gpu-monitor-design.md`

## Global Constraints

- All code under `windows/`. Do not touch `gpubalance/` or `tests/`.
- Target frameworks: Core `net10.0`; App `net10.0;net10.0-windows10.0.19041.0`; tests `net10.0`.
- Windows-only code is wrapped in `#if WINDOWS` or lives in `Platform/Windows/` (excluded from the `net10.0` build).
- `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` (except IL2104 trim warnings from `Microsoft.Windows.SDK.NET` and `WinRT.Runtime`, which are known).
- No third-party control or chart libraries. No Microsoft.Toolkit.Uwp.Notifications.
- Avalonia bindings compiled: `<AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>`, every view sets `x:DataType`.
- Cards are keyed by PCI bus ID string (NVML `busIdLegacy` form, for example `00000000:01:00.0`), never by index or name.
- Paths: settings `%APPDATA%\GpuMonitor\settings.json`; logs `%LOCALAPPDATA%\GpuMonitor\logs\gpumonitor.log`; crash `...\logs\crash`; recordings `%LOCALAPPDATA%\GpuMonitor\recordings`. Off Windows, use `Environment.SpecialFolder.ApplicationData` / `LocalApplicationData` (same code).
- Defaults: refresh 1.0 s (range 0.5 to 5.0); heat limit 80°C (range 50 to 95), re-arm margin 5°C; VRAM limit 90% (range 50 to 99), re-arm margin 5 points; card missing after 5 s; slow poll 100 ms; log 1 MB × 3 backups; crash files keep 3; history 60 readings; report log tail 40 lines; event log window 7 days.
- App ID: `SpaceJamp.GpuMonitor`. Display name: `GPU Monitor`.
- No em dashes in user-facing text.
- Shell setup for every command: `export PATH=$HOME/.dotnet:$PATH DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1`. Install the SDK if missing: `bash dotnet-install.sh --channel 10.0 --install-dir $HOME/.dotnet` (script from https://dot.net/v1/dotnet-install.sh).
- Test command: `dotnet test windows/GpuMonitor.sln`. Commit after each task with the repo's attribution lines.

## Review Focus

1. A card that disappears and returns gets a different NVML index. Its history, alert state and panel must follow the PCI bus ID, not the index. Test: `NvmlReaderTests.CardReturnsAtNewIndex_KeepsIdentity` (Task 3).
2. NVML returns `NOT_SUPPORTED` for a field (fan, power limit, processes). The value shows blank, never 0, triggers no alert, and logs once, not every poll. Test: `NvmlReaderTests.NotSupported_IsBlankAndLoggedOnce` (Task 3) and `AlertRulesTests.MissingTemperature_NoHeatAlert` (Task 5).
3. The PC sleeps and wakes. The first poll after hours must not report every card as missing or draw a line across the gap. Test: `AlertRulesTests.LongGapAfterSleep_ResetsMissingTimers` (Task 5) and `HistoryTests.GapLongerThanThreeIntervals_InsertsBreak` (Task 8).
4. Settings edited by hand with out-of-range or wrong-type values (refresh 0, heat 500, string for a number). Values clamp to range or fall back to defaults per field; the rest of the file still loads. Test: `SettingsTests.OutOfRange_ClampsPerField` and `SettingsTests.WrongType_FallsBackPerField` (Task 6).
5. A short or non-ASCII user name (for example `al` or `Jöns`). Redaction replaces only path segments and the exact host and user tokens at word boundaries, so "total" stays intact. Test: `RedactTests.ShortUserName_DoesNotEatWords` and `RedactTests.NonAsciiUserName` (Task 11).

---

## File map

```
windows/
  global.json                         SDK 10.0.100, rollForward latestFeature
  Directory.Build.props               shared props, version 0.1.0
  GpuMonitor.sln
  README.md  TESTING.md
  src/GpuMonitor.Core/
    Model/Card.cs  Model/Reading.cs  Model/Field.cs  Model/ThrottleReasons.cs
    Nvml/INvml.cs  Nvml/NvmlReturn.cs  Nvml/NvmlNative.cs  Nvml/NvmlApi.cs
    Reading/ICardReader.cs  Reading/NvmlReader.cs  Reading/Units.cs  Reading/CallTimings.cs
    Alerts/Alert.cs  Alerts/AlertRules.cs
    Settings/AppSettings.cs  Settings/SettingsStore.cs
    Logging/AppLog.cs  Logging/RollingFile.cs
    Poll/History.cs  Poll/Poller.cs  Poll/Recorder.cs
    Diagnostics/Facts.cs  Diagnostics/IFactSources.cs  Diagnostics/FactCollector.cs
    Diagnostics/Check.cs  Diagnostics/Checks.cs  Diagnostics/Redact.cs  Diagnostics/ReportRender.cs
    Paths.cs
  src/GpuMonitor.App/
    Program.cs  App.axaml(.cs)  CommandLine.cs  SingleInstance.cs  CrashHandler.cs
    Theme/Theme.axaml
    Controls/Meter.cs  Controls/LineGraph.cs  Controls/CardPanel.cs
    ViewModels/ObservableBase.cs  ViewModels/MainViewModel.cs  ViewModels/CardViewModel.cs
    ViewModels/DiagnosticsViewModel.cs  ViewModels/SettingsViewModel.cs
    Views/MainWindow.axaml(.cs)  Views/CardsView.axaml  Views/DiagnosticsView.axaml  Views/SettingsView.axaml
    Tray/TrayController.cs  Tray/TrayIconRenderer.cs
    Notify/INotifier.cs  Notify/InAppNotifier.cs
    Platform/IStartup.cs  Platform/NullPlatform.cs
    Platform/Windows/WindowsToastNotifier.cs  Platform/Windows/RunKeyStartup.cs
    Platform/Windows/WmiSource.cs  Platform/Windows/EventLogSource.cs
    Platform/Windows/RegistrySource.cs  Platform/Windows/NvidiaSmiSource.cs  Platform/Windows/MiniDump.cs
    Assets/icon.ico  Assets/icon.png
  tests/GpuMonitor.Core.Tests/        FakeNvml.cs + one test file per Core class
  tests/GpuMonitor.App.Tests/         TestApp.cs + headless tests
.github/workflows/windows.yml
CLAUDE.md                             modified
```

---

### Task 1: Scaffold, CI and repo rules

**Files:**
- Create: `windows/global.json`, `windows/Directory.Build.props`, `windows/GpuMonitor.sln`, the four `.csproj` files, `windows/.gitignore` (`bin/`, `obj/`), `.github/workflows/windows.yml`, `windows/tests/GpuMonitor.Core.Tests/SmokeTests.cs`, `windows/tests/GpuMonitor.App.Tests/TestApp.cs`, `windows/tests/GpuMonitor.App.Tests/SmokeTests.cs`, `windows/src/GpuMonitor.App/App.axaml(.cs)`, `windows/src/GpuMonitor.App/Program.cs` (empty window for now)
- Modify: `CLAUDE.md`

**Interfaces:**
- Produces: projects `GpuMonitor.Core`, `GpuMonitor.App` (`OutputType WinExe`, `AssemblyName GpuMonitor`), `GpuMonitor.Core.Tests`, `GpuMonitor.App.Tests`; `TestApp` with `BuildAvaloniaApp()` using `UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })` and `[assembly: AvaloniaTestApplication(typeof(TestApp))]`.

Packages: App: `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent` 12.1.5. App `net10.0-windows10.0.19041.0` only: `System.Management`, `System.Diagnostics.EventLog` 10.0.14. App sets `<EnableWindowsTargeting>true</EnableWindowsTargeting>` and removes `Platform/Windows/**` from compile when `'$(TargetFramework)'=='net10.0'`. Tests: `xunit.v3` 3.2.2, `xunit.runner.visualstudio` 3.1.5, `Microsoft.NET.Test.Sdk` 18.11.0; App.Tests adds `Avalonia.Headless.XUnit` and `Avalonia.Skia` 12.1.5 and references App's `net10.0` target. `InternalsVisibleTo` from Core and App to their test projects.

- [ ] **Step 1: Write smoke tests.** Core: `Smoke_CoreAssemblyLoads` asserts `typeof(GpuMonitor.Core.Paths).Assembly` is not null (add an empty `public static class Paths`). App: `[AvaloniaFact] Smoke_WindowShows` creates `new Window()`, calls `Show()`, asserts `IsVisible`.
- [ ] **Step 2: Run** `dotnet test windows/GpuMonitor.sln`. Expected: 2 passed.
- [ ] **Step 3: Check the Windows target builds** `dotnet build windows/src/GpuMonitor.App -f net10.0-windows10.0.19041.0`. Expected: Build succeeded.
- [ ] **Step 4: Write `.github/workflows/windows.yml`.** Trigger on push and pull_request with `paths: ['windows/**', '.github/workflows/windows.yml']`. Job on `windows-latest`: `actions/checkout@v4`, `actions/setup-dotnet@v4` with `global-json-file: windows/global.json`, `dotnet test windows/GpuMonitor.sln -c Release`. (Publish is added in Task 17.)
- [ ] **Step 5: Update `CLAUDE.md`.** Keep the Linux-only rule for `gpubalance`. Add a "Windows app" section: `windows/` is a Windows app (C#, .NET 10, Avalonia) the user asked for on 2026-11-05 and it must stay; spec and plan paths; its checks `dotnet test windows/GpuMonitor.sln` and `dotnet build windows/src/GpuMonitor.App -f net10.0-windows10.0.19041.0`, run only when `windows/` changes.
- [ ] **Step 6: Run the Python checks from CLAUDE.md** to confirm nothing outside `windows/` broke. Expected: pyflakes silent, unittest OK.
- [ ] **Step 7: Commit** "Scaffold the Windows GPU monitor and its CI".

### Task 2: Model types

**Files:**
- Create: `Model/Field.cs`, `Model/Card.cs`, `Model/Reading.cs`, `Model/ThrottleReasons.cs`, `Nvml/NvmlReturn.cs`
- Test: `tests/GpuMonitor.Core.Tests/ModelTests.cs`

**Interfaces:**
- Produces:
  - `enum NvmlReturn` with the exact numeric values from `nvml.h` (Success 0 … GpuIsLost 15 … Unknown 999). `static string NvmlReturnName(this NvmlReturn r)` returns `NVML_ERROR_<UPPER_SNAKE>` or `NVML_SUCCESS`; unknown numbers give `NVML_ERROR_<number>`.
  - `readonly record struct Field<T>(T? Value, NvmlReturn Code) where T : struct` with `bool Ok => Code == NvmlReturn.Success && Value.HasValue`, `static Field<T> Of(T v)`, `static Field<T> Fail(NvmlReturn c)`.
  - `sealed record Card(string BusId, string Name, string Uuid, int Index)`.
  - `[Flags] enum ThrottleReasons : ulong` with nvml.h bits: GpuIdle 0x1, ApplicationsClocksSetting 0x2, SwPowerCap 0x4, HwSlowdown 0x8, SyncBoost 0x10, SwThermalSlowdown 0x20, HwThermalSlowdown 0x40, HwPowerBrakeSlowdown 0x80, DisplayClockSetting 0x100. `static bool IsThermal(this ThrottleReasons r)` true for SwThermal, HwThermal, HwSlowdown.
  - `sealed record Reading(string BusId, DateTimeOffset At, Field<int> LoadPct, Field<long> VramUsedMiB, Field<long> VramTotalMiB, Field<int> TempC, Field<double> PowerW, Field<double> PowerLimitW, Field<int> GraphicsMHz, Field<int> MemoryMHz, Field<int> SmMHz, Field<int> FanPct, Field<int> PState, Field<ThrottleReasons> Throttle, Field<bool> DisplayActive)` with `double? VramPct` (null unless both VRAM fields ok and total > 0).

- [ ] **Step 1: Failing tests:** `NvmlReturnName_KnownCodes` (`GpuIsLost` → `NVML_ERROR_GPU_IS_LOST`, `NotSupported` → `NVML_ERROR_NOT_SUPPORTED`, `Success` → `NVML_SUCCESS`, `(NvmlReturn)12345` → `NVML_ERROR_12345`); `Field_FailIsNotOk`; `VramPct_NullWhenTotalZero`; `VramPct_Computes` (used 14745, total 16384 → 90.0 within 0.01); `IsThermal_Flags` (HwThermalSlowdown true, SwPowerCap false).
- [ ] **Step 2: Run, expect compile failure.**
- [ ] **Step 3: Implement the types.** Copy every return code from the NVML API reference `nvmlReturn_t` list; do not guess numbers.
- [ ] **Step 4: Run, expect all pass.**
- [ ] **Step 5: Commit** "Add card and reading model".

### Task 3: NVML interface, fake and reader

**Files:**
- Create: `Nvml/INvml.cs`, `Reading/ICardReader.cs`, `Reading/NvmlReader.cs`, `Reading/Units.cs`, `Reading/CallTimings.cs`, `tests/.../FakeNvml.cs`
- Test: `tests/GpuMonitor.Core.Tests/NvmlReaderTests.cs`, `UnitsTests.cs`, `CallTimingsTests.cs`

**Interfaces:**
- Consumes: Task 2 types.
- Produces:
  - `interface INvml` with raw NVML units, every method returning `NvmlReturn` and an `out` value: `Init()`, `Shutdown()`, `GetDeviceCount(out uint)`, `GetHandle(uint index, out nint)`, `GetName(nint, out string)`, `GetUuid(nint, out string)`, `GetBusId(nint, out string)`, `GetVbios(nint, out string)`, `GetUtilization(nint, out uint gpuPct)`, `GetMemory(nint, out ulong usedBytes, out ulong totalBytes)`, `GetTemperature(nint, out uint c)`, `GetTempThreshold(nint, int kind, out uint c)` (kind 0 shutdown, 1 slowdown), `GetPowerUsage(nint, out uint mW)`, `GetEnforcedPowerLimit(nint, out uint mW)`, `GetDefaultPowerLimit(nint, out uint mW)`, `GetClock(nint, int type, out uint mhz)` and `GetMaxClock(...)` (type 0 graphics, 1 SM, 2 memory), `GetFanSpeed(nint, out uint pct)`, `GetPState(nint, out int)`, `GetThrottleReasons(nint, out ulong)`, `GetPcieGen(nint, out uint cur, out uint max)`, `GetPcieWidth(nint, out uint cur, out uint max)`, `GetDisplayActive(nint, out bool)`, `GetProcesses(nint, out IReadOnlyList<(uint Pid, ulong UsedBytes)>)`, `GetDriverVersion(out string)`, `GetNvmlVersion(out string)`, `GetCudaDriverVersion(out int)`.
  - `static class Units`: `double MilliwattsToWatts(uint)`, `long BytesToMiB(ulong)` (floor), `string CudaVersion(int)` (12080 → `12.8`).
  - `interface ICardReader { NvmlReturn InitCode { get; } IReadOnlyList<Card> Cards { get; } IReadOnlyList<Reading> Poll(DateTimeOffset now); CallTimings Timings { get; } }`.
  - `sealed class NvmlReader(INvml nvml, AppLog log) : ICardReader`. On every `Poll` it re-enumerates handles, matches by bus ID, and returns one `Reading` per known card. A card whose handle or name calls fail with `GpuIsLost`, `GpuNotFound` or whose bus ID is no longer listed returns no reading. A new bus ID is added to `Cards`.
  - `sealed class CallTimings`: `void Record(string call, TimeSpan t)`; `IReadOnlyDictionary<string, TimingStats> Snapshot()` with `record TimingStats(int Count, double MinMs, double AvgMs, double MaxMs, double P99Ms)`; keeps the last 600 samples per call.
- `AppLog` is defined in Task 7. Until then tests pass `AppLog.Null` (add `public sealed class AppLog { public static AppLog Null { get; } }` stub in Task 3; Task 7 fills it in and keeps `Null`).

- [ ] **Step 1: Write `FakeNvml : INvml`.** Holds a list of `FakeGpu` (bus ID, name and settable raw values, plus `Dictionary<string, NvmlReturn> Fail` by method name). Supports reorder and removal to simulate index changes.
- [ ] **Step 2: Failing tests:** `Units_*` (5000 mW → 5.0 W; 1048576 B → 1 MiB; 1048575 B → 0 MiB; 12080 → "12.8"); `Poll_TwoCards_ConvertsUnits` (power 285000 mW → 285.0, memory bytes → MiB); `CardReturnsAtNewIndex_KeepsIdentity` (remove card B, poll, re-add B at index 0, poll: reading for B carries B's bus ID and A's values stay A's); `NotSupported_IsBlankAndLoggedOnce` (fan fails `NotSupported` for 3 polls → `FanPct.Ok` false, `Value` null, log captured one warning for that card and field; after it succeeds and fails again, one more warning); `GpuLost_CardHasNoReading`; `NewCard_AddedToCards`; `Timings_RecordedPerCall`; `CallTimings_P99` (100 samples 1..100 ms → P99 99 within 1).
- [ ] **Step 3: Run, expect failures.**
- [ ] **Step 4: Implement** `Units`, `CallTimings` (time each INvml call with `Stopwatch`), `NvmlReader`. Per-field logging uses a `HashSet<(string busId, string field)>` of fields currently failing.
- [ ] **Step 5: Run, expect pass.**
- [ ] **Step 6: Commit** "Read both cards through an NVML interface".

### Task 4: Real NVML bindings

**Files:**
- Create: `Nvml/NvmlNative.cs` (`[LibraryImport]` declarations), `Nvml/NvmlApi.cs` (`sealed class NvmlApi : INvml`)
- Test: `tests/GpuMonitor.Core.Tests/NvmlApiTests.cs`

**Interfaces:**
- Produces: `NvmlApi.TryLoad(out NvmlApi? api, out string error)`. Library name `nvml` on Windows (`nvml.dll`), `libnvidia-ml.so.1` elsewhere, resolved with `NativeLibrary.SetDllImportResolver`. Records the resolved path for diagnostics: `string? LoadedPath`.
- Native entry points (verify each name and signature against the NVML API reference before writing): `nvmlInit_v2`, `nvmlShutdown`, `nvmlSystemGetDriverVersion`, `nvmlSystemGetNVMLVersion`, `nvmlSystemGetCudaDriverVersion_v2`, `nvmlDeviceGetCount_v2`, `nvmlDeviceGetHandleByIndex_v2`, `nvmlDeviceGetName`, `nvmlDeviceGetUUID`, `nvmlDeviceGetPciInfo_v3` (use `busIdLegacy`), `nvmlDeviceGetVbiosVersion`, `nvmlDeviceGetUtilizationRates`, `nvmlDeviceGetMemoryInfo`, `nvmlDeviceGetTemperature`, `nvmlDeviceGetTemperatureThreshold`, `nvmlDeviceGetPowerUsage`, `nvmlDeviceGetEnforcedPowerLimit`, `nvmlDeviceGetPowerManagementDefaultLimit`, `nvmlDeviceGetClockInfo`, `nvmlDeviceGetMaxClockInfo`, `nvmlDeviceGetFanSpeed`, `nvmlDeviceGetPerformanceState`, `nvmlDeviceGetCurrentClocksEventReasons` (fall back to `nvmlDeviceGetCurrentClocksThrottleReasons` on `FunctionNotFound`), `nvmlDeviceGetCurrPcieLinkGeneration`, `nvmlDeviceGetMaxPcieLinkGeneration`, `nvmlDeviceGetCurrPcieLinkWidth`, `nvmlDeviceGetMaxPcieLinkWidth`, `nvmlDeviceGetDisplayActive`, `nvmlDeviceGetComputeRunningProcesses_v3`, `nvmlDeviceGetGraphicsRunningProcesses_v3` (call with count 0 first, retry on `InsufficientSize`).
- A missing export maps to `NvmlReturn.FunctionNotFound`, not an exception.

- [ ] **Step 1: Failing tests:** `TryLoad_NoDriver_ReturnsFalseWithError` (this container has no NVIDIA driver: expect false and a non-empty error naming the library); `Struct_Layouts` (`Marshal.SizeOf` of the PCI info struct and memory struct equal the sizes from nvml.h; write the expected numbers from the header into the test).
- [ ] **Step 2: Run, expect failure.**
- [ ] **Step 3: Implement.** Strings use fixed buffers (96 bytes for name and UUID, 32 for driver and VBIOS) decoded as UTF-8 up to the first NUL.
- [ ] **Step 4: Run, expect pass.** Real calls are checked on the user's PC in Task 17's checklist.
- [ ] **Step 5: Commit** "Bind to nvml.dll".

### Task 5: Alert rules

**Files:**
- Create: `Alerts/Alert.cs`, `Alerts/AlertRules.cs`
- Test: `tests/GpuMonitor.Core.Tests/AlertRulesTests.cs`

**Interfaces:**
- Consumes: `Reading`, `Card`, `AppSettings` (Task 6 defines it; this task uses only `HeatLimitC`, `VramLimitPct`, `RefreshSeconds`, so define those three in a minimal `AppSettings` record here and Task 6 extends it).
- Produces:
  - `enum AlertKind { Hot, HotCleared, LowVram, LowVramCleared, CardMissing, CardBack }`
  - `sealed record Alert(AlertKind Kind, string BusId, string CardName, double? Value, DateTimeOffset At)` with `string Title` and `string Message`, for example `Hot`: title `RTX 4070 Ti SUPER is hot`, message `82°C, limit 80°C`; `CardMissing`: `RTX 4070 Ti SUPER stopped reporting`, message `No reading for 5 seconds`.
  - `sealed class AlertRules { IReadOnlyList<Alert> Evaluate(IReadOnlyList<Card> known, IReadOnlyList<Reading> readings, DateTimeOffset now, AppSettings s); IReadOnlyList<Alert> History { get; } }`. History keeps the last 200.
  - Cleared alerts are logged and kept in history but not shown as notifications (`bool Alert.Notify`, true for Hot, LowVram, CardMissing, CardBack).

- [ ] **Step 1: Failing tests:**
  - `Hot_FiresOnceAtLimit`: temps 79, 80, 81, 82 → one `Hot` at 80.
  - `Hot_ReArmsBelowMargin`: 80 (fire), 76 (no), 75 (`HotCleared`), 80 (fire again).
  - `Hot_PerCard`: card A at 85, card B at 60 → only A.
  - `LowVram_FiresAt90Pct_ReArmsAt85`.
  - `MissingTemperature_NoHeatAlert`: `TempC` failed `NotSupported` → no alert.
  - `CardMissing_After5Seconds`: last reading at t, polls at t+4 (none), t+5 (`CardMissing`), t+6 (none); reading at t+7 → `CardBack`.
  - `LongGapAfterSleep_ResetsMissingTimers`: polls at t, then t+3h with readings for both → no `CardMissing`. Gap rule: if `now - lastEvaluate > 3 × RefreshSeconds`, every card's last-seen time resets to `now`.
  - `History_Capped200`.
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement.** Hysteresis margins 5°C and 5 points from Global Constraints.
- [ ] **Step 4: Run, expect pass.**
- [ ] **Step 5: Commit** "Add alert rules".

### Task 6: Settings

**Files:**
- Create: `Settings/AppSettings.cs` (extend Task 5's record), `Settings/SettingsStore.cs`, `Paths.cs` (fill in)
- Test: `tests/GpuMonitor.Core.Tests/SettingsTests.cs`

**Interfaces:**
- Produces:
  - `sealed record AppSettings { double RefreshSeconds = 1.0; int HeatLimitC = 80; int VramLimitPct = 90; LogLevel LogLevel = Info; bool LogJson = false; bool StartWithWindows = false; string ExpectedDisplayCard = "RTX 5080"; AppSettings Clamp(); }` (`enum LogLevel { Debug, Info, Warning, Error }`).
  - `sealed class SettingsStore(string path, AppLog log) { LoadResult Load(); void Save(AppSettings s); }`, `record LoadResult(AppSettings Settings, SettingsStatus Status, string? Error)`, `enum SettingsStatus { Loaded, Missing, Invalid }`.
  - `static class Paths { string SettingsFile; string LogDir; string CrashDir; string RecordingDir; }` from `Environment.GetFolderPath`, with an internal `Paths.Root` override for tests.
- Loading parses with `JsonDocument` field by field. A field with the wrong type keeps its default and logs a warning; the file still counts as `Loaded`. Unparsable JSON → `Invalid`, file renamed to `settings.json.bad` (replacing an older `.bad`), defaults returned. Save writes to `settings.json.tmp` then `File.Move(..., overwrite: true)`.

- [ ] **Step 1: Failing tests:** `Missing_ReturnsDefaults` (Status Missing, refresh 1.0, heat 80, VRAM 90, start false, expected `RTX 5080`); `RoundTrip`; `OutOfRange_ClampsPerField` (refresh 0 → 0.5, heat 500 → 95, VRAM 10 → 50); `WrongType_FallsBackPerField` (`"HeatLimitC": "hot"`, `"VramLimitPct": 85` → heat 80, VRAM 85); `Corrupt_RenamedToBad` (`{not json` → Invalid, `.bad` exists, original gone); `Save_IsAtomic` (no `.tmp` left after save).
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect pass.**
- [ ] **Step 5: Commit** "Add settings with per-field validation".

### Task 7: Log

**Files:**
- Create: `Logging/AppLog.cs` (replace stub), `Logging/RollingFile.cs`
- Test: `tests/GpuMonitor.Core.Tests/AppLogTests.cs`

**Interfaces:**
- Produces:
  - `sealed class AppLog : IDisposable`: `AppLog(string? filePath, LogLevel level, bool json, TimeProvider? clock = null)`; `static AppLog Null`; `string RunId` (8 lowercase hex chars, new per process); `LogLevel Level { get; set; }` (runtime change); `bool Json { get; set; }`; `void Debug/Info/Warn/Error(string source, string message, Exception? ex = null)`; `event Action<LogLine>? Line` (for the live view); `IReadOnlyList<string> Tail(int lines)`; `string? LastWriteError`.
  - `record LogLine(DateTimeOffset At, string RunId, LogLevel Level, string Source, string Message, string? Exception)`.
  - Text format: `2026-11-05T14:02:11.123+00:00 1a2b3c4d INFO    nvml: message`. JSON lines: keys `time, run, level, source, msg, exception`.
  - `RollingFile(string path, long maxBytes = 1_000_000, int backups = 3)`: before a write that would exceed `maxBytes`, rename `.log.2`→`.log.3`, `.log.1`→`.log.2`, `.log`→`.log.1`. Open, append, close per write (file never held open). A write failure sets `LastWriteError` and never throws.

- [ ] **Step 1: Failing tests:** `Line_HasRunIdAndLevel`; `BelowLevel_NotWritten` (level Warning drops Info); `Level_ChangesAtRuntime`; `Json_ParsesPerLine`; `Rotate_At1MB_Keeps3` (write ~3.5 MB → `.log`, `.1`, `.2`, `.3` exist, no `.4`, each ≤ 1 MB); `Exception_IncludesStack`; `WriteFailure_DoesNotThrow` (path in a non-existent read-only location → `LastWriteError` set); `Tail_ReturnsLastN`.
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement.** Writes are serialized with a lock.
- [ ] **Step 4: Run, expect pass, and rerun Task 3 tests (they now use the real `AppLog`).**
- [ ] **Step 5: Commit** "Add rolling log with run IDs".

### Task 8: History, poller and recorder

**Files:**
- Create: `Poll/History.cs`, `Poll/Poller.cs`, `Poll/Recorder.cs`
- Test: `HistoryTests.cs`, `PollerTests.cs`, `RecorderTests.cs`

**Interfaces:**
- Consumes: `ICardReader`, `AlertRules`, `AppSettings`, `AppLog`.
- Produces:
  - `sealed class History(int capacity = 60) { void Add(Reading r, double refreshSeconds); IReadOnlyList<Reading?> For(string busId); }`. `null` entries are breaks: when the gap since that card's previous reading exceeds 3 × refresh, a `null` is added first.
  - `sealed class Poller : IDisposable`: `Poller(ICardReader reader, AlertRules rules, History history, Func<AppSettings> settings, AppLog log, TimeProvider clock)`; `void Start()`; `PollResult PollOnce()` (public for tests); `event Action<PollResult>? Polled`; `int Skipped`. Uses `PeriodicTimer` on a background task. If a poll is still running when the next tick fires, the tick is skipped, `Skipped` increments, and a debug line is logged. A poll slower than 100 ms logs a warning listing each call's time from `CallTimings`. Exceptions inside a poll are logged as errors and polling continues.
  - `record PollResult(DateTimeOffset At, IReadOnlyList<Card> Cards, IReadOnlyList<Reading> Readings, IReadOnlyList<Alert> Alerts, TimeSpan Took)`.
  - `sealed class Recorder(string dir) { bool IsRecording; string? CurrentFile; void Start(DateTimeOffset now); void Write(PollResult r); void Stop(); }`. File name `yyyy-MM-dd-HHmmss.csv`. Header: `time,bus_id,name,load_pct,vram_used_mib,vram_total_mib,temp_c,power_w,power_limit_w,graphics_mhz,memory_mhz,sm_mhz,fan_pct,pstate,throttle,display_active`. Failed fields are empty cells. Invariant culture (`.` decimal), ISO 8601 times. One row per card per poll.

- [ ] **Step 1: Failing tests:** `History_Keeps60`; `GapLongerThanThreeIntervals_InsertsBreak`; `PollOnce_PassesReadingsToRulesAndHistory`; `Overlap_IsSkipped` (reader blocks on a semaphore, tick twice → `Skipped == 1`); `SlowPoll_LogsWarning` (fake reader sleeps 150 ms); `PollException_Logged_ContinuesNext`; `Recorder_HeaderAndRows` (2 cards, 2 polls → 1 header + 4 rows); `Recorder_FailedFieldEmpty`; `Recorder_InvariantCulture` (run under `de-DE`, power `285.5` written with a dot).
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect pass.**
- [ ] **Step 5: Commit** "Poll cards on a timer, keep history, record CSV".

### Task 9: Diagnostic facts

**Files:**
- Create: `Diagnostics/Facts.cs`, `Diagnostics/IFactSources.cs`, `Diagnostics/FactCollector.cs`
- Test: `FactCollectorTests.cs`

**Interfaces:**
- Produces:
  - `sealed class Facts` holding `Dictionary<string, object?>` sections built by the collector: `app`, `windows`, `driver`, `cards` (list per card), `wmi`, `nvidia_smi`, `gpu_preferences`, `event_log`, `state`. Any value that failed is a string starting `error: `.
  - Interfaces implemented by App (Task 12) and by `NullSources` in Core for non-Windows and tests: `IWindowsInfo { string Version(); TimeSpan Uptime(); int? HwSchMode(); bool? GameMode(); }`, `IWmiSource { IReadOnlyList<WmiAdapter> Adapters(); }` (`record WmiAdapter(string Name, string DriverVersion, string PnpDeviceId, string Status)`), `IEventLogSource { IReadOnlyList<GpuEvent> Recent(TimeSpan window); }` (`record GpuEvent(DateTimeOffset At, string Provider, int Id, string Level, string Message)`), `IRegistrySource { IReadOnlyDictionary<string,string> GpuPreferences(); string? RunValue(); }`, `INvidiaSmi { string? QueryAll(); }`.
  - `sealed class FactCollector(INvml? nvml, NvmlReturn initCode, string? nvmlPath, string? nvmlLoadError, CardSources sources, AppState state)` with `Facts Collect()`. `record CardSources(IWindowsInfo Win, IWmiSource Wmi, IEventLogSource Events, IRegistrySource Registry, INvidiaSmi Smi)`. `record AppState(string Version, string Commit, string Runtime, AppSettings Settings, SettingsStatus SettingsStatus, string? StartupValue, IReadOnlyList<Alert> Alerts, IReadOnlyDictionary<string, TimingStats> Timings, IReadOnlyList<string> LogTail, bool LogWritable)`.
  - Per card facts: every field listed under "Per card" in the spec, including threshold temps, default power limit, max clocks, PCIe current and max, and processes with names from `Process.GetProcessById` (name `error: exited` if gone).

- [ ] **Step 1: Failing tests:** `Collect_AllSectionsPresent`; `SourceThrows_RecordedAsError` (WMI source throws → `wmi` is `error: ...`, other sections still filled); `NvmlNotLoaded_DriverSectionError_CardsEmpty`; `ProcessGone_NamedExited`; `LogTail_Is40Lines`.
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement.** Each section is built inside its own try/catch.
- [ ] **Step 4: Run, expect pass.**
- [ ] **Step 5: Commit** "Collect diagnostic facts".

### Task 10: Diagnostic checks

**Files:**
- Create: `Diagnostics/Check.cs`, `Diagnostics/Checks.cs`
- Test: `ChecksTests.cs`

**Interfaces:**
- Consumes: `Facts`.
- Produces: `enum CheckLevel { Pass, Info, Warn, Fail }`, `record Check(CheckLevel Level, string Id, string Message, string Hint)`, `static IReadOnlyList<Check> Checks.Run(Facts f)` returning one entry for each of the 11 IDs in the spec's table, in table order, `Pass` when nothing is wrong.
- Thresholds: `temp-headroom` warns when `slowdown - temp <= 5`; `poll-time` warns when the average over all calls summed per poll is > 100 ms; `throttle` is `Warn` for any thermal reason, `Info` for SwPowerCap or HwPowerBrakeSlowdown, `Pass` for none or only GpuIdle; `display-card` compares against `Settings.ExpectedDisplayCard` by case-insensitive substring of the name; `pcie-width` warns when current width < max width.
- Hints (exact copy):
  - nvml-load: `Install or reinstall the NVIDIA driver. nvml.dll ships with it in C:\Windows\System32.`
  - card-count: `Check that both cards show in Device Manager under Display adapters. Reseat the card or its power cables if one is missing.`
  - wmi-match: `Windows and the NVIDIA driver disagree about the cards. Restart, then reinstall the driver with a clean install.`
  - display-card: `Plug the monitor into the RTX 5080, or change "Expected display card" in Settings.`
  - pcie-width: `Run a game or load first: some boards drop lanes at idle. If it stays low, check the slot and BIOS lane settings.`
  - temp-headroom: `Improve airflow or lower the power limit. The card will slow itself down at its slowdown limit.`
  - throttle: `Thermal throttling means the card is too hot. Power cap means it hit its power limit, which is normal under full load.`
  - driver-errors: `Driver crashes are often caused by unstable overclocks, power supply limits or old drivers. Remove overclocks and update the driver.`
  - settings: `Settings were reset to defaults. The bad file was kept as settings.json.bad.`
  - log-writable: `Check that %LOCALAPPDATA%\GpuMonitor\logs exists and is not read-only.`
  - poll-time: `NVML is slow to answer. Check the driver and the per-call timings in this report.`

- [ ] **Step 1: Failing tests:** one test per ID covering pass and the non-pass levels, for example `CardCount_OneCard_Fail`, `DisplayCard_Is4070_Warn`, `DisplayCard_Is5080_Pass`, `PcieWidth_X8OfX16_Warn`, `TempHeadroom_4Below_Warn`, `Throttle_HwThermal_Warn`, `Throttle_PowerCap_Info`, `Throttle_IdleOnly_Pass`, `DriverErrors_OneEvent_Warn`, `Settings_Missing_Info`, `Settings_Invalid_Warn`, `PollTime_150ms_Warn`, `Run_ReturnsElevenInTableOrder`.
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect pass.**
- [ ] **Step 5: Commit** "Add diagnostic checks".

### Task 11: Redaction and report output

**Files:**
- Create: `Diagnostics/Redact.cs`, `Diagnostics/ReportRender.cs`
- Test: `RedactTests.cs`, `ReportRenderTests.cs`

**Interfaces:**
- Produces:
  - `sealed class Redactor(string userName, string machineName, string homePath) { string Apply(string text); }`. Replaces `homePath` (and its JSON-escaped form with doubled backslashes, matched case-insensitively) with `<home>`; then whole-token matches (regex `(?<![\p{L}\p{N}_])TOKEN(?![\p{L}\p{N}_])`, case-insensitive) of `userName` with `<user>` and `machineName` with `<host>`. Tokens shorter than 3 characters are replaced only inside paths, never as free words.
  - `static class ReportRender { string Text(Facts f, IReadOnlyList<Check> checks); string Json(Facts f, IReadOnlyList<Check> checks); int ExitCode(IReadOnlyList<Check> checks); }`. Text puts checks first (one line each `[FAIL] card-count: ...` then an indented hint), then facts by section. JSON is `{ "checks": [...], "facts": {...} }`, indented. `ExitCode` is 1 if any `Fail`, else 0.

- [ ] **Step 1: Failing tests:** `HomePath_Replaced` (`C:\Users\Jeff\AppData` → `<home>\AppData`); `HomePath_JsonEscaped_Replaced` (`C:\\Users\\Jeff\\AppData`); `UserAndHost_Replaced`; `ShortUserName_DoesNotEatWords` (user `al`, home `C:\Users\al`: `total` and `al.` in prose unchanged, path replaced); `NonAsciiUserName` (`Jöns`); `Text_ChecksFirst`; `Json_RoundTrips` (parse with `JsonDocument`, `checks` length 11); `ExitCode_OneOnFail`.
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement.** Render calls `Redactor.Apply` on the final string unless told not to (`bool redact` parameter on `Text` and `Json`, default true).
- [ ] **Step 4: Run, expect pass.**
- [ ] **Step 5: Commit** "Render redacted diagnostic reports".

### Task 12: Windows fact sources, startup and crash dump

**Files:**
- Create: everything under `src/GpuMonitor.App/Platform/` plus `CrashHandler.cs`
- Test: `tests/GpuMonitor.App.Tests/PlatformTests.cs`

**Interfaces:**
- Consumes: Task 9 interfaces.
- Produces:
  - `WmiSource : IWmiSource` (`SELECT Name, DriverVersion, PNPDeviceID, Status FROM Win32_VideoController`).
  - `EventLogSource : IEventLogSource` reading the `System` log with `EventLogReader` and XPath `*[System[(Provider[@Name='nvlddmkm'] or (Provider[@Name='Display'] and EventID=4101)) and TimeCreated[timediff(@SystemTime) <= 604800000]]]`, newest first, max 100.
  - `RegistrySource : IRegistrySource, IWindowsInfo`: `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` values; Run value; `HwSchMode` from `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers`; Game Mode from `HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled` (missing → true); version from `RtlGetVersion`; uptime from `Environment.TickCount64`.
  - `NvidiaSmiSource : INvidiaSmi`: runs `nvidia-smi -q` with a 10 s timeout, `null` if not found.
  - `interface IStartup { bool IsEnabled(); void Set(bool on); }`; `RunKeyStartup` writes `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `GpuMonitor` = `"<exe path>" --tray`.
  - `MiniDump.Write(string path)` via `dbghelp.dll` `MiniDumpWriteDump`, type `MiniDumpWithThreadInfo | MiniDumpWithIndirectlyReferencedMemory`.
  - `NullPlatform` implements all of the above for `net10.0`: empty lists, `null`, `IStartup` no-op returning false.
  - `static class CrashHandler { void Install(AppLog log, string crashDir); }` hooks `AppDomain.CurrentDomain.UnhandledException`, `TaskScheduler.UnobservedTaskException` and Avalonia's `Dispatcher.UIThread.UnhandledException`. Each writes `crash-<yyyyMMdd-HHmmss>.txt` (exception, run ID, last 40 log lines) and, on Windows, a `.dmp` with the same stem; keeps the newest 3 pairs.
  - `static class Platform { CardSources Sources(); IStartup Startup(); INotifier Notifier(...) }` chosen by `#if WINDOWS`.

- [ ] **Step 1: Failing tests (run everywhere):** `CrashHandler_WritesTextAndKeeps3` (call the internal `WriteCrash(Exception)` 5 times → 3 `.txt` files); `NullPlatform_ReturnsEmpty`. Windows-only tests marked `[Fact(Skip = ...)]` off Windows via `OperatingSystem.IsWindows()` (`Assert.Skip` in xUnit v3): `Wmi_ListsAdapters`, `EventLog_QueryRuns`, `RunKey_RoundTrip` (uses a test value name `GpuMonitorTest`, removed after).
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run on Linux, expect pass with Windows tests skipped; build the Windows target.** CI runs the Windows tests.
- [ ] **Step 5: Commit** "Add Windows fact sources, startup and crash files".

### Task 13: Drawn controls and theme

**Files:**
- Create: `Theme/Theme.axaml`, `Controls/Meter.cs`, `Controls/LineGraph.cs`, `Controls/CardPanel.cs`
- Test: `tests/GpuMonitor.App.Tests/ControlsTests.cs`

**Interfaces:**
- Produces (all `Control` subclasses that override `Render(DrawingContext)` and declare `StyledProperty`s with `AffectsRender`):
  - `Meter`: `double Value`, `double Maximum`, `string Label`, `string Text`, `bool HasValue`. Bar fill is `Accent` until 80% of max, `Warn` to 95%, `Danger` above. `HasValue == false` draws an empty track and `Text` `n/a`.
  - `LineGraph`: `IReadOnlyList<double?> Points`, `double Minimum`, `double Maximum`, `double? Limit`. `null` points break the line. Draws a dashed `Danger` line at `Limit`. Exposes `internal int LastSegmentCount` for tests.
  - `CardPanel`: `CardViewModel Card` (Task 14) and renders title, `Display` badge when `IsDisplay`, meters for load, VRAM and power, a temperature graph and a load graph. `IsStale == true` draws the panel at 40% opacity with `No reading since HH:mm:ss`.
  - Theme brushes as resources, light and dark variants via `ThemeVariant`: `Background`, `Surface`, `Text`, `TextDim`, `Accent`, `Warn`, `Danger`, `Grid`. App follows the system theme (`RequestedThemeVariant = Default`).

- [ ] **Step 1: Failing headless tests:** `Meter_ColorBands` (value 50/100 uses Accent, 85 Warn, 97 Danger: expose `internal IBrush CurrentFill`); `Meter_NoValue_ShowsNa`; `LineGraph_NullBreaksLine` (points `[1,2,null,3,4]` → `LastSegmentCount == 2`); `LineGraph_Keeps60` (render with 60 points without exceptions, capture frame not null); `CardPanel_StaleText` (stale card shows `No reading since 14:02:11`, assert through `internal string StatusText`).
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect pass.**
- [ ] **Step 5: Commit** "Add drawn meter, graph and card panel".

### Task 14: View models and main window

**Files:**
- Create: `ViewModels/*`, `Views/*`
- Test: `tests/GpuMonitor.App.Tests/MainWindowTests.cs`, `ViewModelTests.cs`

**Interfaces:**
- Consumes: `Poller`, `PollResult`, `History`, `SettingsStore`, `AppLog`, `FactCollector`, `Checks`, `ReportRender`, `Recorder`, `IStartup`, controls.
- Produces:
  - `ObservableBase : INotifyPropertyChanged` with `protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)`.
  - `CardViewModel`: `BusId`, `Name`, `IsDisplay`, `IsStale`, `LastSeen`, `LoadPct`, `VramUsedMiB`, `VramTotalMiB`, `TempC`, `PowerW`, `PowerLimitW`, `TempHistory`, `LoadHistory`, `Fields` (list of `FieldRow(string Name, string Value, string Code)` for the Diagnostics tab); `void Update(Reading? r, IReadOnlyList<Reading?> history, DateTimeOffset now)`. Stale when no reading for 5 s.
  - `MainViewModel`: `ObservableCollection<CardViewModel> Cards` ordered by bus ID; `string Banner` (non-empty when NVML failed or the log can't be written); `void OnPolled(PollResult r)` (marshalled to the UI thread by the caller).
  - `DiagnosticsViewModel`: live `Fields` per card, `Timings`, `Events`, `LogLines` (last 500, filter by `MinLevel`), commands `CreateReport()` returning the text, `CopyReport`, `SaveReport(path)`, `OpenLogFolder`, `ToggleRecording`, `IsRecording`.
  - `SettingsViewModel`: two-way properties for every `AppSettings` field, `Save()` calls `SettingsStore.Save`, `IStartup.Set`, and sets `AppLog.Level` and `AppLog.Json` at once; values clamp on save.
  - `MainWindow` with `TabControl`: Cards (`ItemsControl` of `CardPanel`, two columns), Diagnostics, Settings. Closing hides the window (`e.Cancel = true; Hide();`) unless `App.IsExiting`.

- [ ] **Step 1: Failing tests:** `CardViewModel_StaleAfter5s`; `MainViewModel_OrdersByBusId`; `MainViewModel_NewCardAdded`; `Settings_SaveClampsAndAppliesLogLevel`; `Diagnostics_LogFilter`; `[AvaloniaFact] MainWindow_HasThreeTabs`; `[AvaloniaFact] Close_HidesNotExits`; `[AvaloniaFact] TwoCards_TwoPanels` (fake poll result with two readings).
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect pass.**
- [ ] **Step 5: Commit** "Add main window with cards, diagnostics and settings tabs".

### Task 15: Tray, notifications and single instance

**Files:**
- Create: `Tray/TrayController.cs`, `Tray/TrayIconRenderer.cs`, `Notify/INotifier.cs`, `Notify/InAppNotifier.cs`, `Platform/Windows/WindowsToastNotifier.cs`, `SingleInstance.cs`
- Test: `tests/GpuMonitor.App.Tests/TrayTests.cs`, `SingleInstanceTests.cs`

**Interfaces:**
- Consumes: `Alert`, `MainViewModel`.
- Produces:
  - `interface INotifier { bool Show(string title, string message); }` (false on failure).
  - `InAppNotifier(Window host)` using Avalonia `WindowNotificationManager` with max 3 items.
  - `WindowsToastNotifier(AppLog log)`: on first use creates `HKCU\Software\Classes\AppUserModelId\SpaceJamp.GpuMonitor` with `DisplayName = GPU Monitor` and `IconUri` = path of `Assets/icon.png` copied next to the exe; shows `ToastText02` through `ToastNotificationManager.CreateToastNotifier("SpaceJamp.GpuMonitor")`. Any exception → log warning once, return false.
  - `sealed class FallbackNotifier(INotifier primary, INotifier fallback, AppLog log)`: uses fallback when primary returns false and logs it once.
  - `TrayIconRenderer.Render(int? tempC, bool warning) : WindowIcon` (tempC is the hotter card; warning when NVML failed or a card is missing) draws a 32×32 bitmap with the temperature number (or `!` when warning, `--` when null) using theme colors.
  - `TrayController`: Avalonia `TrayIcon` with tooltip `GPU Monitor: 5080 64°C, 4070 Ti SUPER 58°C`, left click opens the window, menu items Open, Create diagnostic report, Settings, Exit. `void Update(PollResult r)` re-renders the icon only when the shown value changes.
  - `sealed class SingleInstance : IDisposable`: `static SingleInstance? TryAcquire(string id, Action onShowRequested)` using a named `Mutex` `Local\SpaceJamp.GpuMonitor` and a named pipe `SpaceJamp.GpuMonitor.show`; `static bool SignalExisting(string id)`.

- [ ] **Step 1: Failing tests:** `Fallback_UsedWhenPrimaryFails` (primary returns false twice → fallback called twice, log has one warning); `TrayIcon_RendersNumberAndWarning` (non-null icons for 64, null, warning); `Tray_UpdatesOnlyOnChange` (internal render counter); `SingleInstance_SecondSignalsFirst` (acquire, then `SignalExisting` → callback fires within 2 s; second `TryAcquire` returns null).
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect pass; build the Windows target.**
- [ ] **Step 5: Commit** "Add tray icon, notifications and single instance".

### Task 16: Startup wiring and command line

**Files:**
- Create: `CommandLine.cs`; Modify: `Program.cs`, `App.axaml.cs`
- Test: `tests/GpuMonitor.App.Tests/CommandLineTests.cs`, `StartupTests.cs`

**Interfaces:**
- Produces:
  - `record Options(bool Diagnose, string? Output, bool Json, bool NoRedact, bool CrashTest, bool Tray, string? Error)`; `static Options CommandLine.Parse(string[] args)`. Unknown argument → `Error = "Unknown option: <arg>"`.
  - `static int Program.Main(string[] args)`: installs `CrashHandler` first; `--crash-test` throws `InvalidOperationException("Crash test")` after the handler is installed; `--diagnose` collects facts without starting the UI, writes text or JSON to `Output` or stdout, returns `ReportRender.ExitCode`; on Windows the exe is `WinExe`, so `--diagnose` calls `AttachConsole(-1)` before writing to stdout. Otherwise starts Avalonia. A second instance signals the first and exits 0. `--tray` starts hidden in the tray (used by the Run key).
  - Exit codes: 0 ok, 1 diagnose found a failure, 2 bad arguments, 70 unexpected error.
  - App startup order: settings → log (level and format from settings, logs startup facts: app version, runtime, OS, run ID) → NVML load (retry every 30 s while failed, logging each failure once until it changes) → reader → poller → window and tray. Poll results are posted with `Dispatcher.UIThread.Post`.

- [ ] **Step 1: Failing tests:** `Parse_Diagnose_Output_Json`; `Parse_Unknown_Error`; `Diagnose_WritesReportAndExitCode` (run the internal `RunDiagnose(Options, FactCollector)` with fakes: one card → output contains `[FAIL] card-count`, returns 1); `Startup_LogsFacts` (start the internal `Bootstrap` with fakes: log contains run ID and `cards found: 2`).
- [ ] **Step 2: Run, expect failures.**
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Run, expect pass; run `dotnet run --project windows/src/GpuMonitor.App -f net10.0 -- --diagnose` here. Expected: report prints with `[FAIL] nvml-load` (no driver in this container) and exit code 1.**
- [ ] **Step 5: Commit** "Wire up startup, diagnose and crash-test options".

### Task 17: Publish, docs and checklist

**Files:**
- Create: `windows/README.md`, `windows/TESTING.md`, `src/GpuMonitor.App/Assets/icon.ico`, `Assets/icon.png`
- Modify: `.github/workflows/windows.yml`, `App csproj` (publish props), root `README.md` (one line pointing to `windows/README.md`)

**Interfaces:**
- Publish props (Windows target only): `RuntimeIdentifier win-x64`, `SelfContained true`, `PublishSingleFile true`, `PublishTrimmed true`, `TrimMode partial`, `IncludeNativeLibrariesForSelfExtract true`, `ApplicationIcon Assets\icon.ico`.

- [ ] **Step 1: Publish locally:** `dotnet publish windows/src/GpuMonitor.App -c Release -f net10.0-windows10.0.19041.0`. Expected: `GpuMonitor.exe` in the publish folder; record its size for the README.
- [ ] **Step 2: Extend CI:** after tests, run the same publish, zip it as `GpuMonitor-<version>-win-x64.zip`, upload with `actions/upload-artifact@v4`, name `GpuMonitor-win-x64`.
- [ ] **Step 3: Write `windows/README.md`:** download from the Actions run, unzip, SmartScreen steps (More info, Run anyway), where settings, logs, crash files and recordings live, `--diagnose` usage and exit codes, how to remove the app.
- [ ] **Step 4: Write `windows/TESTING.md`:** the manual checklist from the spec, one checkbox each, plus: real NVML values match `nvidia-smi` within 1°C, 1% and 5 W; a toast appears (if it doesn't, the in-app notice appears and the log says why); tray tooltip names both cards.
- [ ] **Step 5: Run all checks:** `dotnet test windows/GpuMonitor.sln`, the Windows target build, and the Python checks from CLAUDE.md. Expected: all pass.
- [ ] **Step 6: Commit and push** "Publish the Windows app as a single exe".
