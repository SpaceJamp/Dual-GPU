"""Spread a local language model across GPUs with llama.cpp.

llama.cpp can split one model's layers across devices from different
backends, for example an NVIDIA card through CUDA and an AMD card through
Vulkan. We ask the binary which devices it sees (--list-devices), keep one
backend per physical card, and add --device and --tensor-split so each
card gets a share that matches its free VRAM.
"""

from __future__ import annotations

import re
import subprocess
from dataclasses import dataclass
from typing import Callable

# "  CUDA0: NVIDIA GeForce RTX 4090 (24080 MiB, 23400 MiB free)"
_LINE = re.compile(r"^\s*(\S+): (.+) \((\d+) MiB, (\d+) MiB free\)\s*$")

# Faster native backend first, Vulkan as the fallback every vendor has.
_BACKEND_PREFERENCE = {
    "nvidia": ("CUDA", "Vulkan"),
    "amd": ("ROCm", "Vulkan"),
    "intel": ("SYCL", "Vulkan"),
}

# Flags we would otherwise add. If you pass one yourself, yours wins.
_DEVICE_FLAGS = ("-dev", "--device")
_SPLIT_FLAGS = ("-ts", "--tensor-split")
_MODES = ("split", "single")


@dataclass
class LlamaDevice:
    name: str
    description: str
    total_mib: int
    free_mib: int

    @property
    def backend(self) -> str:
        return self.name.rstrip("0123456789")

    @property
    def vendor(self) -> str:
        desc = self.description.lower()
        if any(word in desc for word in ("nvidia", "geforce", "quadro")):
            return "nvidia"
        if any(word in desc for word in ("amd", "radeon")):
            return "amd"
        if "intel" in desc:
            return "intel"
        return "unknown"


class LlmError(RuntimeError):
    pass


def parse_devices(text: str) -> list[LlamaDevice]:
    devices = []
    for line in text.splitlines():
        m = _LINE.match(line)
        if m:
            devices.append(LlamaDevice(m[1], m[2], int(m[3]), int(m[4])))
    return devices


def list_devices(binary: str, runner: Callable[[list[str]], str] | None = None) -> list[LlamaDevice]:
    cmd = [binary, "--list-devices"]
    try:
        if runner:
            out = runner(cmd)
        else:
            out = subprocess.run(cmd, capture_output=True, text=True, timeout=60).stdout
    except (OSError, subprocess.SubprocessError) as exc:
        raise LlmError(f"could not run {binary} --list-devices: {exc}") from exc
    return parse_devices(out)


def _rank(device: LlamaDevice) -> int:
    order = _BACKEND_PREFERENCE.get(device.vendor, ())
    return order.index(device.backend) if device.backend in order else len(order)


def one_per_card(devices: list[LlamaDevice]) -> list[LlamaDevice]:
    """Drop duplicate views of the same card (CUDA0 and Vulkan0, say).

    Two devices count as the same card when the vendor matches and the
    reported memory is within 512 MiB. Backends round memory differently,
    so an exact match would miss real duplicates.
    """
    kept: list[LlamaDevice] = []
    for dev in sorted(devices, key=_rank):
        same = any(k.vendor == dev.vendor and abs(k.total_mib - dev.total_mib) <= 512
                   for k in kept)
        if not same:
            kept.append(dev)
    # Back to llama.cpp's own order so the split lines up with --device.
    return [d for d in devices if d in kept]


def has_flag(command: list[str], flags: tuple[str, ...]) -> bool:
    return any(arg in flags or arg.split("=", 1)[0] in flags for arg in command)


def plan(
    devices: list[LlamaDevice],
    mode: str = "split",
    reserve_mib: int = 1024,
    skip_vendors: tuple[str, ...] = (),
) -> tuple[list[LlamaDevice], list[int]]:
    """Return the devices to use and the MiB each one can give the model."""
    if mode not in _MODES:
        raise ValueError(f"mode must be one of {_MODES}")
    usable = [d for d in one_per_card(devices) if d.vendor not in skip_vendors]
    room = [(d, d.free_mib - reserve_mib) for d in usable]
    room = [(d, mib) for d, mib in room if mib > 0]
    if not room:
        raise LlmError("no GPU has free VRAM left after the reserve")
    if mode == "single":
        room = [max(room, key=lambda pair: pair[1])]
    return [d for d, _ in room], [mib for _, mib in room]


def build_args(command: list[str], chosen: list[LlamaDevice], shares: list[int]) -> list[str]:
    extra: list[str] = []
    if not has_flag(command, _DEVICE_FLAGS):
        extra += ["--device", ",".join(d.name for d in chosen)]
    if len(chosen) > 1 and not has_flag(command, _SPLIT_FLAGS):
        extra += ["--tensor-split", ",".join(str(s) for s in shares)]
    return command + extra
