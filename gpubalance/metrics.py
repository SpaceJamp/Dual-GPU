"""Read current load for each GPU.

Sources by driver:
  amdgpu  sysfs gpu_busy_percent and mem_info_vram_*, hwmon for
          temperature and power
  nvidia  nvidia-smi query (the proprietary driver has no sysfs counters)
  others  nothing reliable without root, so values stay None
"""

from __future__ import annotations

import logging
import subprocess
import time
from dataclasses import dataclass
from typing import Callable, Iterable

from gpubalance.detect import Gpu
from gpubalance.log import timed

log = logging.getLogger(__name__)

Runner = Callable[[list[str]], str]

_NVIDIA_QUERY = [
    "nvidia-smi",
    # name goes last so a comma inside it cannot shift the other fields.
    "--query-gpu=pci.bus_id,uuid,utilization.gpu,memory.used,memory.total,"
    "temperature.gpu,power.draw,power.limit,name",
    "--format=csv,noheader,nounits",
]
_MIB = 1024 * 1024

# Most cards start to throttle in the mid 80s C.
TEMP_WARN_C = 75
TEMP_HOT_C = 85


@dataclass
class Load:
    util: float | None = None
    vram_used: int | None = None
    vram_total: int | None = None
    uuid: str | None = None
    name: str | None = None
    temp_c: float | None = None
    power_w: float | None = None
    power_limit_w: float | None = None
    source: str = "none"

    @property
    def vram_frac(self) -> float | None:
        if self.vram_used is None or not self.vram_total:
            return None
        return self.vram_used / self.vram_total

    @property
    def power_frac(self) -> float | None:
        if self.power_w is None or not self.power_limit_w:
            return None
        return self.power_w / self.power_limit_w


def _run(cmd: list[str]) -> str:
    return subprocess.run(
        cmd, capture_output=True, text=True, check=True, timeout=5
    ).stdout


def _int_file(path) -> int | None:
    try:
        return int(path.read_text().strip())
    except (OSError, ValueError) as exc:
        log.debug("cannot read %s: %s", path, exc)
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
        with timed(log, "nvidia-smi query"):
            out = runner(_NVIDIA_QUERY)
    except subprocess.CalledProcessError as exc:
        log.warning("nvidia-smi exited with %s: %s", exc.returncode,
                    (exc.stderr or exc.stdout or "").strip()[:500])
        return {}
    except (OSError, subprocess.SubprocessError) as exc:
        log.warning("nvidia-smi unavailable, NVIDIA load unknown: %s", exc)
        return {}
    log.debug("nvidia-smi output: %r", out)
    loads = {}
    for line in out.strip().splitlines():
        parts = [p.strip() for p in line.split(",", 8)]
        if len(parts) != 9:
            log.warning("nvidia-smi line has %d fields, expected 9: %r", len(parts), line)
            continue
        bus, uuid, util, used, total, temp, power, limit, name = parts
        used_n, total_n = _number(used), _number(total)
        try:
            slot = _normalize_bus_id(bus)
        except ValueError:
            log.warning("nvidia-smi bus ID not understood: %r", bus)
            continue
        loads[slot] = Load(
            util=_number(util),
            vram_used=int(used_n * _MIB) if used_n is not None else None,
            vram_total=int(total_n * _MIB) if total_n is not None else None,
            uuid=uuid,
            name=name,
            temp_c=_number(temp),
            power_w=_number(power),
            power_limit_w=_number(limit),
            source="nvidia-smi",
        )
    return loads


def _scaled(path, scale: float) -> float | None:
    value = _int_file(path)
    return None if value is None else value / scale


def _read_hwmon(gpu: Gpu) -> tuple[float | None, float | None, float | None]:
    """Temperature in C, power and power cap in W from the card's hwmon.

    temp1 is the edge sensor, in millidegrees. Power is in microwatts:
    newer cards report power1_input, older ones power1_average.
    """
    for hwmon in sorted((gpu.sysfs / "hwmon").glob("hwmon*")):
        temp = _scaled(hwmon / "temp1_input", 1000)
        power = _scaled(hwmon / "power1_input", 1e6)
        if power is None:
            power = _scaled(hwmon / "power1_average", 1e6)
        cap = _scaled(hwmon / "power1_cap", 1e6)
        if temp is not None or power is not None:
            return temp, power, cap
    return None, None, None


def _read_amd_once(gpu: Gpu) -> Load:
    temp, power, cap = _read_hwmon(gpu)
    return Load(
        util=_int_file(gpu.sysfs / "gpu_busy_percent"),
        vram_used=_int_file(gpu.sysfs / "mem_info_vram_used"),
        vram_total=_int_file(gpu.sysfs / "mem_info_vram_total"),
        temp_c=temp,
        power_w=power,
        power_limit_w=cap,
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
    log.debug("%s busy samples %s -> %s%%", gpu.label, utils, last.util)
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
            if gpu.driver == "nvidia" and not nvidia:
                reason = "nvidia-smi gave no data"
            elif gpu.driver == "nvidia":
                reason = f"nvidia-smi listed {sorted(nvidia)} but not this slot"
            elif gpu.vendor == "nvidia":
                reason = f"driver is {gpu.driver}; load needs the proprietary nvidia driver"
            else:
                reason = f"no load source for driver {gpu.driver}"
            log.info("%s load unknown: %s", gpu.label, reason)
            loads[gpu.pci_slot] = Load()
    for slot, load in loads.items():
        log.debug("load %s: util=%s vram=%s/%s temp=%s power=%s/%s source=%s", slot,
                  load.util, load.vram_used, load.vram_total, load.temp_c, load.power_w,
                  load.power_limit_w, load.source)
    return loads
