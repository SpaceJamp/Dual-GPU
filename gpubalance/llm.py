"""Spread a local language model across GPUs with llama.cpp.

llama.cpp can split one model's layers across devices from different
backends, for example an NVIDIA card through CUDA and an AMD card through
Vulkan. We ask the binary which devices it sees (--list-devices), keep one
backend per physical card, and add --device and --tensor-split so each
card gets a share that matches its free VRAM.
"""

from __future__ import annotations

import os
import re
import subprocess
from dataclasses import dataclass
from typing import Callable

from gpubalance.detect import Gpu

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


# CUDA numbers cards fastest first by default. PCI order lets us match
# CUDA0, CUDA1... to physical slots when two NVIDIA cards are installed.
# Must be set both when listing and when running, so the numbers agree.
LAUNCH_ENV = {"CUDA_DEVICE_ORDER": "PCI_BUS_ID"}


def list_devices(binary: str, runner: Callable[[list[str]], str] | None = None) -> list[LlamaDevice]:
    cmd = [binary, "--list-devices"]
    try:
        if runner:
            out = runner(cmd)
        else:
            out = subprocess.run(cmd, capture_output=True, text=True, timeout=60,
                                 env={**os.environ, **LAUNCH_ENV}).stdout
    except (OSError, subprocess.SubprocessError) as exc:
        raise LlmError(f"could not run {binary} --list-devices: {exc}") from exc
    return parse_devices(out)


def _rank(device: LlamaDevice) -> int:
    order = _BACKEND_PREFERENCE.get(device.vendor, ())
    return order.index(device.backend) if device.backend in order else len(order)


def _same_card(a: LlamaDevice, b: LlamaDevice) -> bool:
    # Backends round memory differently, so allow 512 MiB of slack.
    return a.vendor == b.vendor and abs(a.total_mib - b.total_mib) <= 512


def _same_model(a: LlamaDevice, b: LlamaDevice) -> bool:
    # Vulkan appends driver info, e.g. "AMD Radeon RX 9060 XT (RADV GFX1200)".
    # A bare prefix test would pair "RTX 4070" with "RTX 4070 Ti SUPER".
    x, y = sorted((a.description, b.description), key=len)
    return y == x or y.startswith(x + " (")


def one_per_card(devices: list[LlamaDevice]) -> list[LlamaDevice]:
    """Drop duplicate views of the same card (CUDA0 and Vulkan0, say).

    Devices from one backend are always distinct cards. A device from a
    slower backend is a duplicate when it matches a kept card that this
    backend has not matched yet: by model name first, then by vendor and
    memory size. Each kept card absorbs at most one device per backend, so
    two 16 GB NVIDIA cards stay two cards.
    """
    kept: list[LlamaDevice] = []
    claimed: set[tuple[str, str]] = set()  # (kept card, backend that matched it)
    for dev in sorted(devices, key=_rank):
        free = [k for k in kept
                if k.backend != dev.backend and (k.name, dev.backend) not in claimed
                and _same_card(k, dev)]
        twin = next((k for k in free if _same_model(k, dev)), free[0] if free else None)
        if twin:
            claimed.add((twin.name, dev.backend))
        else:
            kept.append(dev)
    # Back to llama.cpp's own order so the split lines up with --device.
    return [d for d in devices if d in kept]


def map_slots(devices: list[LlamaDevice], gpus: list[Gpu]) -> dict[str, str | None]:
    """Match llama.cpp device names to PCI slots where we can be sure.

    CUDA devices follow PCI order because of LAUNCH_ENV. Any other device
    maps only when its vendor has a single card in the machine.
    """
    by_vendor: dict[str, list[Gpu]] = {}
    for gpu in gpus:
        by_vendor.setdefault(gpu.vendor, []).append(gpu)
    slots: dict[str, str | None] = {}
    for dev in devices:
        same = by_vendor.get(dev.vendor, [])
        index = dev.name[len(dev.backend):]
        if dev.backend == "CUDA" and index.isdigit() and int(index) < len(same):
            slots[dev.name] = same[int(index)].pci_slot
        elif len(same) == 1:
            slots[dev.name] = same[0].pci_slot
        else:
            slots[dev.name] = None
    return slots


def has_flag(command: list[str], flags: tuple[str, ...]) -> bool:
    return any(arg in flags or arg.split("=", 1)[0] in flags for arg in command)


def plan(
    devices: list[LlamaDevice],
    mode: str = "split",
    reserve_mib: int = 1024,
    skip: frozenset[str] = frozenset(),
) -> tuple[list[LlamaDevice], list[int]]:
    """Return the devices to use and the MiB each one can give the model.

    skip holds llama.cpp device names to leave out.
    """
    if mode not in _MODES:
        raise ValueError(f"mode must be one of {_MODES}")
    usable = [d for d in one_per_card(devices) if d.name not in skip]
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
