"""User configuration: ~/.config/gpubalance/config.toml on Linux,
%APPDATA%\\gpubalance\\config.toml on Windows."""

from __future__ import annotations

import logging
import tomllib
from dataclasses import dataclass, field
from pathlib import Path

from gpubalance import system

log = logging.getLogger(__name__)


class ConfigError(ValueError):
    pass


def default_path() -> Path:
    return system.config_dir() / "config.toml"


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
        unknown_util=float(data.get("unknown_utilization", 0.5)),
        samples=int(data.get("samples", 5)),
        pins={str(k): str(v) for k, v in data.get("pin", {}).items()},
        render=data.get("roles", {}).get("render"),
        display=data.get("roles", {}).get("display"),
    )
    for slot, prefs in data.get("gpu", {}).items():
        cfg.gpus[slot] = GpuPrefs(
            capacity=float(prefs.get("capacity", 1.0)),
            allow=tuple(prefs.get("allow", ("graphics", "compute"))),
        )
    return cfg
