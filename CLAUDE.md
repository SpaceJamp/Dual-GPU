# Project notes

## Platform priority

Linux is the primary platform. Windows is secondary.

- Design, build and test for Linux first. A feature is done when it works on Linux.
- Windows support is best effort. Never weaken, slow down or complicate Linux behavior to make Windows work.
- Keep platform differences in `gpubalance/system.py` (and the Windows sections of `detect.py`), so Linux code paths stay readable.
- If something cannot work on Windows, say so in the command's output and in the README's Windows table instead of shipping a guess.
- Windows code is tested on Linux by forcing `GPUBALANCE_PLATFORM=windows` with fake tool output (`tests/fakewin.py`).

## The user's hardware

- Main card: NVIDIA RTX 5080 (drives the monitor).
- Second card: NVIDIA RTX 4070 Ti SUPER.
- Two cards from one vendor, so vendor names are ambiguous in config; use PCI slots.

## Checks before pushing

```
python3 -m pyflakes gpubalance tests
python3 -m unittest discover -s tests
```
