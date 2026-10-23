"""Pick a GPU for a new program.

Each eligible GPU gets a score, lower is better:

    score = (w_util * util + w_vram * vram_used_fraction + w_jobs * jobs
             + w_heat * heat) / capacity

util and vram are 0..1. jobs is how many programs this tool already sent
to that GPU and that are still running. heat is 0 up to 75 C and rises to
1 at 85 C, so a hot card loses new work without ever being refused.
Between 75 and 85 C the cooler card is favored; once both are at 85 C or
more, both carry the full penalty and load decides as usual. capacity
lets you tell the tool that one card is stronger than the other.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass

from gpubalance.config import Config
from gpubalance.detect import Gpu
from gpubalance.metrics import Load

log = logging.getLogger(__name__)

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
    heat: float = 0.0


class NoGpuError(RuntimeError):
    pass


def _eligible(gpu: Gpu, kind: str, cfg: Config) -> bool:
    allow = cfg.prefs(gpu.pci_slot).allow
    if kind == "compute" and gpu.driver not in _COMPUTE_DRIVERS:
        log.debug("%s not eligible for compute: driver %s has no compute stack",
                  gpu.label, gpu.driver)
        return False
    ok = bool(allow) if kind == "auto" else kind in allow
    if not ok:
        log.debug("%s not eligible for %s: config allows %s", gpu.label, kind, list(allow))
    return ok


def matches(gpu: Gpu, target: str) -> bool:
    """True if target names this GPU by vendor, PCI slot or card name."""
    target = target.lower()
    return target in (gpu.vendor, gpu.pci_slot, gpu.card)


def heat_penalty(temp_c: float | None, cfg: Config) -> float:
    """0..1. Unknown temperature costs nothing: no data is not a reason
    to avoid a card."""
    if temp_c is None:
        return 0.0
    span = cfg.heat_full_c - cfg.heat_start_c
    return max(0.0, min(1.0, (temp_c - cfg.heat_start_c) / span))


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
        heat = heat_penalty(load.temp_c, cfg)
        raw = (cfg.w_util * util + cfg.w_vram * vram + cfg.w_jobs * count
               + cfg.w_heat * heat)
        scored.append(Scored(gpu, raw / capacity, util, vram, count, heat))
        log.debug(
            "score %s = (%.2f*util %.2f%s + %.2f*vram %.2f + %.2f*jobs %d + %.2f*heat %.2f%s)"
            " / capacity %.2f = %.3f",
            gpu.label, cfg.w_util, util, " (assumed)" if load.util is None else "",
            cfg.w_vram, vram, cfg.w_jobs, count, cfg.w_heat, heat,
            f" ({load.temp_c:.0f} C)" if load.temp_c is not None else "",
            capacity, raw / capacity,
            extra={"data": {"gpu": gpu.pci_slot, "util": util, "vram": vram, "jobs": count,
                            "heat": heat, "temp_c": load.temp_c, "capacity": capacity,
                            "score": raw / capacity}})
        if heat >= 1.0:
            log.info("%s is at %.0f C; new work goes elsewhere if another card is cooler",
                     gpu.label, load.temp_c)
    # Ties go to the lower PCI slot so results are stable.
    return sorted(scored, key=lambda s: (s.score, s.gpu.pci_slot))


ROLES = ("render", "display", "side")


def _by_target(gpus: list[Gpu], target: str) -> Gpu:
    found = [g for g in gpus if matches(g, target)]
    if not found:
        raise NoGpuError(f"no GPU matches '{target}'")
    if len(found) > 1:
        raise NoGpuError(f"'{target}' matches {len(found)} cards; use a PCI slot "
                         "from `gpubalance list` instead")
    return found[0]


def assign_roles(gpus: list[Gpu], cfg: Config) -> dict[str, Gpu]:
    """Decide which card shows the picture, which renders games, and which
    takes background work.

    Without config, the card with the most monitors plugged in is the
    display card, and the strongest remaining card renders. Capacity from
    the config decides "strongest"; ties go to the lower PCI slot. Setting
    render and display to the same card is allowed: the game then runs
    where the monitor is, with no frame copy.
    """
    if not gpus:
        raise NoGpuError("no GPUs found")
    if cfg.display:
        display = _by_target(gpus, cfg.display)
    else:
        # max() keeps the first of equal items, and gpus is sorted by slot.
        display = max(gpus, key=lambda g: g.displays)
    if cfg.render:
        render = _by_target(gpus, cfg.render)
    else:
        others = [g for g in gpus if g is not display] or gpus
        render = max(others, key=lambda g: cfg.prefs(g.pci_slot).capacity)
    # Background apps go to the display card, unless the game renders
    # there too (monitor on the strongest card); then to the spare card.
    spare = [g for g in gpus if g is not render]
    side = display if display is not render else (spare[0] if spare else render)
    log.info("roles: render=%s (%s) display=%s (%s) side=%s",
             render.label, "config" if cfg.render else "auto",
             display.label, "config" if cfg.display else f"{display.displays} monitor(s)",
             side.label)
    return {"render": render, "display": display, "side": side}


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
    if target:
        log.debug("target %r limits the choice to %s", target, [g.label for g in pool])
    ranked = score_all(pool, loads, jobs, cfg, kind)
    if not ranked:
        raise NoGpuError(f"no GPU is eligible for {kind} work")
    log.info("chose %s for %s work (score %.3f; ranking %s)", ranked[0].gpu.label, kind,
             ranked[0].score, ", ".join(f"{r.gpu.label}={r.score:.3f}" for r in ranked))
    return ranked[0]
