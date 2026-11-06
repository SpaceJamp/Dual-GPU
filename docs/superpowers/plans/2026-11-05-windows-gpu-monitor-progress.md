# Windows GPU Monitor: build progress

Handoff note for continuing the plan in `2026-11-05-windows-gpu-monitor.md` outside the cloud session. Last updated 2026-11-06, after continuing on the PC itself.

## Status

| Task | State |
|---|---|
| 1 Scaffold, CI, CLAUDE.md | done, reviewed |
| 2 Model types | done, reviewed |
| 3 NVML reader | done, reviewed |
| 4 NVML bindings | done, reviewed |
| 5 Alert rules | done, reviewed |
| 6 Settings | done, reviewed |
| 7 Log | done, reviewed |
| 8 Poller, history, recorder | done, reviewed |
| 9 Diagnostic facts | done, reviewed |
| 10 Diagnostic checks | done, reviewed |
| 11 Redaction and report | done, reviewed |
| 12 Windows sources, startup, crash files | done, reviewed |
| 13 Drawn controls and theme | done, reviewed |
| 14 View models and main window | done, reviewed |
| 15 Tray, notifications, single instance | done, reviewed |
| 16 Startup wiring and command line | done |
| 17 Publish, docs, checklist | done, not pushed; CI publish step not run yet |

Tasks ran in the order 1, 2, 7, 3, 4, 5, 6, 8 to 15 (ruling 1).

Checks at e04657f: Core 138/138, App 67/67, Windows target build 0 warnings, Python checks OK. One App test failed once in 10 runs and passed on every rerun; the failing test name was not captured. Watch for a flaky test in App.Tests.

## Findings from running on the real PC

These only showed up on real hardware or in the published exe, not in the cloud session.

- The CI hangs and the flaky App test had the same source. MiniDumpWriteDump on our own process suspends every other thread, and deadlocks if one holds the loader lock. Parallel test runs hung most of the time on Windows, and a real crash could have frozen the app. Dumps now come from a helper process (`GpuMonitor.exe --write-dump <pid> <path>`). Before the fix: 4 hangs in the first 5 runs. After: 0 in 45.
- MiniDumpWriteDump also fails about 1 time in 60 on a busy process, which was the flaky test. The helper tries 3 times.
- Publishing failed on IL2026 from reflection JSON, and WMI failed at run time once trimmed. Fixed with allowed warnings, `TrimmerRootAssembly System.Management` and `BuiltInComInteropSupport`. The published exe was run and checked.
- NVML bit 0x400 is "Reliability" on driver 621.14. It printed as 1024.
- NVML values match nvidia-smi on the 5080 (temperature, power, driver, bus ID).
- The PC runs only the RTX 5080: the 4070 Ti SUPER is not installed because it does not fit in the case. One card now passes card-count, and Parts 2 and 3 are shelved until the second card is in. Two-card steps in TESTING.md could not run.

## Run the checks

```
dotnet test windows/GpuMonitor.slnx
dotnet build windows/src/GpuMonitor.App -f net10.0-windows10.0.19041.0
```

## Task 15 points for its review

- InAppNotifier posts off the UI thread and returns true; if the window is hidden by then, the notice is dropped with no fallback log.
- SingleInstance calls `onShowRequested` on the pipe thread; the caller must switch to the UI thread.
- WindowsToastNotifier is marked `[SupportedOSPlatform("windows10.0.10240.0")]` (plain "windows" fails CA1416 on the WinRT calls). Toasts have not run on real Windows yet.
- Extra files: Notify/FallbackNotifier.cs, Notify/AlertDispatcher.cs, NotifyTests.cs, `Drawing.Brush(string)`.

## Carry into Task 16

- Call `CrashHandler.Install(log, Paths.CrashDir)` on the UI thread at startup, before anything else that can throw.
- Stop the Recorder on exit.
- `MainViewModel(NvmlReturn initCode, string? loadError, AppLog log, History history, CurrentSettings settings, DiagnosticsViewModel, SettingsViewModel)`; the Poller reads settings through `CurrentSettings.Get`.
- Marshal `SingleInstance` show requests and `Poller.Polled` to the UI thread with `Dispatcher.UIThread.Post`.
- Wire `AlertDispatcher` to `INotifier` from `Platform.Notifier(log, host)`.
- `App.axaml.cs` still creates a plain `Window`; replace it with `MainWindow` and the tray.

## Rulings made during the build

Each line: what was decided, why, and the cost if wrong.

1. Order 1, 2, 7, 3 to 6, 8 to 17; LogLevel lives in Logging/AppLog.cs. AppLog had no dependencies. Cost: none.
2. Task 13 created ObservableBase and a property-only CardViewModel; Task 14 added Update and Fields. CardPanel needed the type. Cost: none.
3. Platform got Sources and Startup in Task 12, Notifier in Task 15. INotifier came later. Cost: none.
4. NVML struct sizes 68, 24, 8, 24, confirmed against nvml.h (go-nvml copy, 13.4.92). Cost: wrong reads on the PC, caught by the nvidia-smi comparison in TESTING.md.
5. The log write-failure test uses a path whose parent is a file, since the container runs as root. Cost: none.
6. The solution is `windows/GpuMonitor.slnx` (the .NET 10 default). Cost: rename one file.
7. Cards are keyed by the 32-byte `busId` (00000000:01:00.0), not `busIdLegacy`. Matches nvidia-smi. Cost: one-line change.
8. Version string buffers are 80 bytes (header size), not 32. Cost: none.
9. Process list merges compute then graphics processes, each PID once. Cost: a process shows once instead of twice.
10. Card-missing tests poll every second, because the brief's sparse sequence tripped the sleep rule. Cost: none.
11. The sleep test was rewritten so it fails without the gap reset. Cost: none.
12. The missing-temperature test was strengthened so a read-as-0 bug fails it. Cost: none.
13. The clamp test checks each field's warning and a valid sibling field. Cost: none.
14. The Poller fix also covered a silently dying loop and double Dispose. Cost: small diff.
15. Diagnostic facts are typed records (Diagnostics/Facts.cs), not a string-keyed dictionary. Cost: a larger Task 9.
16. A missing settings file gets the hint "No settings file yet. Defaults are in use until you save settings." Cost: none.
17. "Not available" results get service hints (WMI, Event Log) instead of the mismatch and crash hints. Cost: none.
18. JSON reports redact every string and dictionary key before serializing, with no pass over the JSON text. Cost: converter code.
19. App.Tests adds the Windows target only when building on Windows; Windows tests run in CI only. Cost: none on Linux.
20. The crash path logs dump errors, deletes empty dumps, observes the nvidia-smi read task, uses UTC stamps, and has an exact XPath test. Cost: small diff.
21. A stale card panel dims its body to 40% but keeps the "No reading since" line readable. Cost: visual only.
22. A cleared number box shows the saved value after Save; the log view is seeded from the log file. Cost: small.

## Deferred minor findings

For the final whole-branch review to triage.

- Task 1: `windows/global.json` only applies when dotnet runs inside `windows/`; Directory.Build.props adds ImplicitUsings and LangVersion latest.
- Task 2: NvmlReturn names and values are spot-tested only; names are rebuilt per call.
- Task 3: enumeration failures aren't logged; a failed UUID read stays empty; the INvml throwing contract is unstated; the timings test skips GetName and GetUuid.
- Task 4: process-list retry and dedup are untested; `LoadedPath` is the candidate name, not the resolved path.
- Task 5: `Alert.Limit` defaults to 0; "No reading for 5 seconds" duplicates `AlertRules.MissingAfter`; History returns its live list.
- Task 6: `Paths.Root` is a plain static; an Int32 overflow falls back to the default instead of clamping.
- Task 7: the `AppLog.Line` add and remove aren't atomic; `AppLog.Null.Level` is undefined; `LastWriteError` isn't volatile; the rotation test doesn't prove the oldest file is dropped.
- Task 8: a new interval applies after the next old tick; the slow-poll message uses a 600-sample average; two recordings started in the same second truncate one another; Recorder isn't IDisposable; Dispose from inside a Polled handler stalls 2 s; some timer tests use Sleep(50).
- Task 9: a thrown handle or bus ID exception skips the card silently; PCIe and VRAM pairs call NVML twice; NullSources.Version "" counts as Ok.
- Task 10: the NVIDIA name match is case-insensitive.
- Task 11: no placeholder self-match guard (a user named "user"); the settings block in the text report uses snake_case.
- Task 12: the crash test can recreate a deleted temp folder; `output.Wait` in NvidiaSmiSource wraps AggregateException.
- Task 13: the "n/a" logic is duplicated; the unsubscribe-on-detach path is untested; the class name `Drawing` shadows `Avalonia.Media.Drawing`.
- Task 14: the AppLog.Line subscription is never removed; `ToDictionary` throws on a duplicate bus ID; recorder IO runs on the UI thread; a RelayCommand without OnError swallows errors.
