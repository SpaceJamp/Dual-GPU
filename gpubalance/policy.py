"""Pick a GPU for a new program.

Each eligible GPU gets a score, lower is better:

    score = (w_util * util + w_vram * vram_used_fraction + w_jobs * jobs) / capacity

util and vram are 0..1. jobs is how many programs this tool already sent
to that GPU and that are still running. capacity lets you tell the tool
that one card is stronger than the other.
"""

from __future__ import annotations

from dataclasses import dataclass

from gpubalance.config import Config
from gpubalance.detect import Gpu
from gpubalance.metrics import Load

KINDS = ("auto", "graphics", "compute")

# Drivers that ship a usable compute stack (CUDA, ROCm/HIP, oneAPI).
_COMPUTE_DRIVERS = {"nvidia", "amdgpu", "i915", "xe"}


@dataclass
class Scored:
    gpu: Gpu
    score: float
    util: float
    vram: float
    jobs: int


class NoGpuError(RuntimeError):
    pass


def _eligible(gpu: Gpu, kind: str, cfg: Config) -> bool:
    allow = cfg.prefs(gpu.pci_slot).allow
    if kind == "compute" and gpu.driver not in _COMPUTE_DRIVERS:
        return False
    if kind == "auto":
        return bool(allow)
    return kind in allow


def matches(gpu: Gpu, target: str) -> bool:
    """True if target names this GPU by vendor, PCI slot or card name."""
    target = target.lower()
    return target in (gpu.vendor, gpu.pci_slot, gpu.card)


def score_all(
    gpus: list[Gpu],
    loads: dict[str, Load],
    jobs: dict[str, int],
    cfg: Config,
    kind: str = "auto",
) -> list[Scored]:
    scored = []
    for gpu in gpus:
        if not _eligible(gpu, kind, cfg):
            continue
        load = loads.get(gpu.pci_slot, Load())
        util = load.util / 100 if load.util is not None else cfg.unknown_util
        vram = load.vram_frac if load.vram_frac is not None else 0.0
        count = jobs.get(gpu.pci_slot, 0)
        capacity = max(cfg.prefs(gpu.pci_slot).capacity, 0.01)
        raw = cfg.w_util * util + cfg.w_vram * vram + cfg.w_jobs * count
        scored.append(Scored(gpu, raw / capacity, util, vram, count))
    # Ties go to the lower PCI slot so results are stable.
    return sorted(scored, key=lambda s: (s.score, s.gpu.pci_slot))


def choose(
    gpus: list[Gpu],
    loads: dict[str, Load],
    jobs: dict[str, int],
    cfg: Config,
    kind: str = "auto",
    target: str | None = None,
) -> Scored:
    if kind not in KINDS:
        raise ValueError(f"kind must be one of {KINDS}")
    pool = [g for g in gpus if matches(g, target)] if target else gpus
    if target and not pool:
        raise NoGpuError(f"no GPU matches '{target}'")
    ranked = score_all(pool, loads, jobs, cfg, kind)
    if not ranked:
        raise NoGpuError(f"no GPU is eligible for {kind} work")
    return ranked[0]
