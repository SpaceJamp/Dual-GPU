# Project notes

## Platform

The Python tool (`gpubalance`) is Linux only. Windows support was built once and removed on purpose.

- Do not add Windows support, Windows code paths or Windows dependencies to `gpubalance` unless the user asks for it again.
- The separate Windows app under `windows/` is the exception; see "Windows app" below.
- Linux APIs (sysfs, fcntl, exec, curses) are fine to use directly.

## The user's hardware

- Main card: NVIDIA RTX 5080 (drives the monitor).
- Second card: NVIDIA RTX 4070 Ti SUPER, not installed. It does not fit in the case, so the PC runs one card for now.
- With both cards, two cards share one vendor, so vendor names are ambiguous in config; use PCI slots.

## Checks before pushing

```
python3 -m pyflakes gpubalance tests
python3 -m unittest discover -s tests
```

## Windows app

`windows/` is a separate Windows GPU monitor app (C#, .NET 10, Avalonia). The user asked for it on 2026-11-05 and it must stay. It is independent of `gpubalance`.

- Spec: `docs/superpowers/specs/2026-11-05-windows-gpu-monitor-design.md`
- Plan: `docs/superpowers/plans/2026-11-05-windows-gpu-monitor.md`
- Checks, run only when `windows/` changes:

```
dotnet test windows/GpuMonitor.slnx
dotnet build windows/src/GpuMonitor.App -f net10.0-windows10.0.19041.0
```
