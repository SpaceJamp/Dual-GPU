# Project notes

## Platform

Linux only. Windows support was built once and removed on purpose.

- Do not add Windows support, Windows code paths or Windows dependencies unless the user asks for it again.
- Linux APIs (sysfs, fcntl, exec, curses) are fine to use directly.

## The user's hardware

- Main card: NVIDIA RTX 5080 (drives the monitor).
- Second card: NVIDIA RTX 4070 Ti SUPER.
- Two cards from one vendor, so vendor names are ambiguous in config; use PCI slots.

## Checks before pushing

```
python3 -m pyflakes gpubalance tests
python3 -m unittest discover -s tests
```
