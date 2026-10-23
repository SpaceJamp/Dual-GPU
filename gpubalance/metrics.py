"""Read current load for each GPU.

Sources by driver:
  amdgpu  sysfs gpu_busy_percent and mem_info_vram_*
  nvidia  nvidia-smi query (the proprietary driver has no sysfs counters)
  others  nothing reliable without root, so values stay None
"""

from __future__ import annotations

import subprocess
import time
from dataclasses import dataclass
from typing import Callable, Iterable

from gpubalance.detect import Gpu

Runner = Callable[[list[str]], str]

_NVIDIA_QUERY = [
    "nvidia-smi",
    "--query-gpu=pci.bus_id,uuid,utilization.gpu,memory.used,memory.total",
    "--format=csv,noheader,nounits",
]
_MIB = 1024 * 1024


@dataclass
class Load:
    util: float | None = None
    vram_used: int | None = None
    vram_total: int | None = None
    uuid: str | None = None
    source: str = "none"

    @property
    def vram_frac(self) -> float | None:
        if self.vram_used is None or not self.vram_total:
            return None
        return self.vram_used / self.vram_total


def _run(cmd: list[str]) -> str:
    return subprocess.run(
        cmd, capture_output=True, text=True, check=True, timeout=5
    ).stdout


def _int_file(path) -> int | None:
    try:
        return int(path.read_text().strip())
    except (OSError, ValueError):
        return None


def _normalize_bus_id(bus_id: str) -> str:
    # nvidia-smi prints an 8-digit domain ("00000000:01:00.0"); sysfs uses 4.
    domain, _, rest = bus_id.strip().lower().partition(":")
    return f"{int(domain, 16):04x}:{rest}"


def _number(value: str) -> float | None:
    try:
        return float(value)
    except ValueError:
        return None  # "[N/A]" on some boards


def read_nvidia(runner: Runner = _run) -> dict[str, Load]:
    """Query all NVIDIA GPUs at once, keyed by PCI slot."""
    try:
        out = runner(_NVIDIA_QUERY)
    except (OSError, subprocess.SubprocessError):
        return {}
    loads = {}
    for line in out.strip().splitlines():
        parts = [p.strip() for p in line.split(",")]
        if len(parts) != 5:
            continue
        bus, uuid, util, used, total = parts
        used_n, total_n = _number(used), _number(total)
        loads[_normalize_bus_id(bus)] = Load(
            util=_number(util),
            vram_used=int(used_n * _MIB) if used_n is not None else None,
            vram_total=int(total_n * _MIB) if total_n is not None else None,
            uuid=uuid,
            source="nvidia-smi",
        )
    return loads


def _read_amd_once(gpu: Gpu) -> Load:
    return Load(
        util=_int_file(gpu.sysfs / "gpu_busy_percent"),
        vram_used=_int_file(gpu.sysfs / "mem_info_vram_used"),
        vram_total=_int_file(gpu.sysfs / "mem_info_vram_total"),
        source="sysfs",
    )


def read_amd(gpu: Gpu, samples: int = 5, interval: float = 0.1) -> Load:
    """gpu_busy_percent is close to instantaneous, so average a few reads."""
    reads = []
    for i in range(max(1, samples)):
        if i:
            time.sleep(interval)
        reads.append(_read_amd_once(gpu))
    last = reads[-1]
    utils = [r.util for r in reads if r.util is not None]
    last.util = sum(utils) / len(utils) if utils else None
    return last


def sample(
    gpus: Iterable[Gpu], samples: int = 5, runner: Runner = _run
) -> dict[str, Load]:
    gpus = list(gpus)
    nvidia = {}
    if any(g.driver == "nvidia" for g in gpus):
        nvidia = read_nvidia(runner)
    loads = {}
    for gpu in gpus:
        if gpu.driver == "amdgpu":
            loads[gpu.pci_slot] = read_amd(gpu, samples=samples)
        elif gpu.pci_slot in nvidia:
            loads[gpu.pci_slot] = nvidia[gpu.pci_slot]
        else:
            loads[gpu.pci_slot] = Load()
    return loads
