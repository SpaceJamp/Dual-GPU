"""User configuration from ~/.config/gpubalance/config.toml."""

from __future__ import annotations

import logging
import os
import tomllib
from dataclasses import dataclass, field
from pathlib import Path

log = logging.getLogger(__name__)


class ConfigError(ValueError):
    pass


def default_path() -> Path:
    base = os.environ.get("XDG_CONFIG_HOME") or os.path.expanduser("~/.config")
    return Path(base) / "gpubalance" / "config.toml"


@dataclass
class GpuPrefs:
    # How much work this card can take relative to a baseline of 1.0.
    # A card with capacity 2.0 looks half as loaded at the same numbers.
    capacity: float = 1.0
    allow: tuple[str, ...] = ("graphics", "compute")


@dataclass
class Config:
    w_util: float = 1.0
    w_vram: float = 0.5
    w_jobs: float = 0.3
    # Heat penalty: 0 at heat_start_c, rising in a straight line to the
    # full weight at heat_full_c and above. 1.0 makes a card at 85 C look
    # as bad as one at 100% utilization.
    w_heat: float = 1.0
    heat_start_c: float = 75.0
    heat_full_c: float = 85.0
    # Load assumed for a GPU we cannot read (0..1).
    unknown_util: float = 0.5
    samples: int = 5
    gpus: dict[str, GpuPrefs] = field(default_factory=dict)
    # Executable name -> vendor ("amd") or PCI slot ("0000:03:00.0").
    pins: dict[str, str] = field(default_factory=dict)
    # Fixed roles for `game` and `side`. None means work it out from
    # which card has monitors plugged in.
    render: str | None = None
    display: str | None = None

    def prefs(self, pci_slot: str) -> GpuPrefs:
        return self.gpus.get(pci_slot, GpuPrefs())


def load(path: Path | None = None) -> Config:
    path = path or default_path()
    try:
        data = tomllib.loads(path.read_text())
    except FileNotFoundError:
        log.debug("no config at %s, using defaults", path)
        return Config()
    except tomllib.TOMLDecodeError as exc:
        raise ConfigError(f"{path}: {exc}") from exc
    log.debug("config %s: %s", path, data)
    weights = data.get("weights", {})
    cfg = Config(
        w_util=float(weights.get("utilization", 1.0)),
        w_vram=float(weights.get("vram", 0.5)),
        w_jobs=float(weights.get("jobs", 0.3)),
        w_heat=float(weights.get("heat", 1.0)),
        heat_start_c=float(data.get("heat", {}).get("start_c", 75.0)),
        heat_full_c=float(data.get("heat", {}).get("full_c", 85.0)),
        unknown_util=float(data.get("unknown_utilization", 0.5)),
        samples=int(data.get("samples", 5)),
        pins={str(k): str(v) for k, v in data.get("pin", {}).items()},
        render=data.get("roles", {}).get("render"),
        display=data.get("roles", {}).get("display"),
    )
    if cfg.heat_full_c <= cfg.heat_start_c:
        raise ConfigError(f"{path}: [heat] full_c ({cfg.heat_full_c}) must be above "
                          f"start_c ({cfg.heat_start_c})")
    for slot, prefs in data.get("gpu", {}).items():
        cfg.gpus[slot] = GpuPrefs(
            capacity=float(prefs.get("capacity", 1.0)),
            allow=tuple(prefs.get("allow", ("graphics", "compute"))),
        )
    return cfg
