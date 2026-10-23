"""Find the GPUs in this machine.

Linux: every GPU with a kernel driver shows up as /sys/class/drm/cardN,
and its device/ directory is the PCI device. That gives vendor, device ID,
PCI slot and driver without any vendor tooling installed.

Windows: NVIDIA cards come from nvidia-smi, which also gives the PCI slot.
Other cards come from Win32_VideoController through PowerShell, with the
slot read from the device's location string.
"""

from __future__ import annotations

import json
import logging
import re
import subprocess
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

from gpubalance import system

log = logging.getLogger(__name__)

VENDORS = {
    0x10DE: "nvidia",
    0x1002: "amd",
    0x8086: "intel",
}

# PCI base class 0x03 is "display controller" (VGA, 3D, other).
_DISPLAY_CLASS = 0x03


@dataclass(frozen=True)
class Gpu:
    card: str
    pci_slot: str
    vendor: str
    vendor_id: int
    device_id: int
    driver: str
    sysfs: Path | None  # None on Windows
    render_node: str | None = None
    # Monitors currently plugged into this card.
    displays: int = 0
    name: str | None = None

    @property
    def label(self) -> str:
        return f"{self.vendor}@{self.pci_slot}"

    @property
    def pci_id(self) -> str:
        return f"{self.vendor_id:04x}:{self.device_id:04x}"


def _read(path: Path) -> str | None:
    try:
        return path.read_text().strip()
    except OSError:
        return None


def _uevent(device: Path) -> dict[str, str]:
    text = _read(device / "uevent") or ""
    fields = {}
    for line in text.splitlines():
        key, _, value = line.partition("=")
        fields[key] = value
    return fields


def _connected_displays(drm: Path, card: str) -> int:
    # Connectors live beside the card as cardN-<type>-<n>, e.g. card1-DP-2.
    count = 0
    for conn in sorted(drm.glob(f"{card}-*")):
        status = _read(conn / "status")
        log.debug("connector %s: %s", conn.name, status)
        count += status == "connected"
    return count


def discover(sysfs_root: Path = Path("/sys"), runner: Runner | None = None) -> list[Gpu]:
    """Return one Gpu per PCI display device, sorted by PCI slot."""
    if system.WINDOWS:
        return discover_windows(runner or _run)
    return discover_linux(sysfs_root)


def discover_linux(sysfs_root: Path = Path("/sys")) -> list[Gpu]:
    drm = sysfs_root / "class" / "drm"
    if not drm.is_dir():
        log.warning("%s does not exist; no DRM devices to scan", drm)
        return []
    found: dict[str, Gpu] = {}
    for entry in sorted(drm.glob("card[0-9]*")):
        # card0-DP-1 and friends are connectors, not devices.
        if "-" in entry.name:
            continue
        device = entry / "device"
        vendor_raw = _read(device / "vendor")
        device_raw = _read(device / "device")
        class_raw = _read(device / "class")
        if vendor_raw is None or device_raw is None or class_raw is None:
            # simpledrm and other non-PCI framebuffers land here.
            log.debug("skip %s: not a PCI device (vendor=%r device=%r class=%r)",
                      entry.name, vendor_raw, device_raw, class_raw)
            continue
        try:
            pci_class = int(class_raw, 16)
            vendor_id = int(vendor_raw, 16)
            device_id = int(device_raw, 16)
        except ValueError:
            log.warning("skip %s: unreadable PCI IDs (vendor=%r device=%r class=%r)",
                        entry.name, vendor_raw, device_raw, class_raw)
            continue
        if pci_class >> 16 != _DISPLAY_CLASS:
            log.debug("skip %s: PCI class 0x%06x is not a display controller",
                      entry.name, pci_class)
            continue
        uevent = _uevent(device)
        slot = uevent.get("PCI_SLOT_NAME")
        if not slot:
            log.warning("skip %s: uevent has no PCI_SLOT_NAME (%s)", entry.name, uevent)
            continue
        if slot in found:
            log.debug("skip %s: same PCI slot %s as %s", entry.name, slot, found[slot].card)
            continue
        render = sorted((device / "drm").glob("renderD*"))
        gpu = Gpu(
            card=entry.name,
            pci_slot=slot,
            vendor=VENDORS.get(vendor_id, f"0x{vendor_id:04x}"),
            vendor_id=vendor_id,
            device_id=device_id,
            driver=uevent.get("DRIVER", "unknown"),
            sysfs=device,
            render_node=f"/dev/dri/{render[0].name}" if render else None,
            displays=_connected_displays(drm, entry.name),
        )
        log.debug("found %s: %s %s driver=%s render=%s monitors=%d", gpu.card, gpu.label,
                  gpu.pci_id, gpu.driver, gpu.render_node, gpu.displays)
        found[slot] = gpu
    gpus = sorted(found.values(), key=lambda g: g.pci_slot)
    log.info("detected %d GPU(s): %s", len(gpus), ", ".join(g.label for g in gpus) or "none")
    return gpus


# --- Windows -------------------------------------------------------------

Runner = Callable[[list[str]], str]

_NVIDIA_LIST = ["nvidia-smi", "--query-gpu=pci.bus_id,pci.device_id,uuid,name",
                "--format=csv,noheader"]

# One JSON row per display adapter. LocationInfo carries the PCI bus,
# device and function, which Win32_VideoController does not.
_PS_ADAPTERS = r"""
$ErrorActionPreference = 'SilentlyContinue'
$rows = @(Get-CimInstance Win32_VideoController | ForEach-Object {
  $loc = (Get-PnpDeviceProperty -InstanceId $_.PNPDeviceID -KeyName 'DEVPKEY_Device_LocationInfo').Data
  [pscustomobject]@{ Name = $_.Name; PnpId = $_.PNPDeviceID; Driver = $_.DriverVersion;
                     HRes = $_.CurrentHorizontalResolution; Location = $loc }
})
ConvertTo-Json -InputObject $rows -Compress
"""
_PS_COMMAND = ["powershell", "-NoProfile", "-NonInteractive", "-Command", _PS_ADAPTERS]
_PNP_IDS = re.compile(r"^PCI\\VEN_([0-9A-F]{4})&DEV_([0-9A-F]{4})", re.IGNORECASE)
# "PCI bus 1, device 0, function 0", possibly translated: keep the numbers.
_LOCATION = re.compile(r"(\d+)\D+(\d+)\D+(\d+)")


def _run(cmd: list[str]) -> str:
    return subprocess.run(cmd, capture_output=True, text=True, check=True, timeout=30).stdout


def _call(runner: Runner, cmd: list[str]) -> str | None:
    try:
        return runner(cmd)
    except (OSError, subprocess.SubprocessError) as exc:
        log.warning("%s failed: %s", cmd[0], exc)
        return None


def _nvidia_cards(runner: Runner) -> list[dict]:
    out = _call(runner, _NVIDIA_LIST)
    cards = []
    for line in (out or "").strip().splitlines():
        parts = [p.strip() for p in line.split(",", 3)]
        if len(parts) != 4:
            log.warning("nvidia-smi line not understood: %r", line)
            continue
        bus, pci_id, uuid, name = parts
        try:
            domain, _, rest = bus.lower().partition(":")
            slot = f"{int(domain, 16):04x}:{rest}"
            # pci.device_id is device in the high 16 bits, vendor in the low.
            combined = int(pci_id, 16)
        except ValueError:
            log.warning("nvidia-smi IDs not understood: %r", line)
            continue
        cards.append({"slot": slot, "vendor_id": combined & 0xFFFF,
                      "device_id": combined >> 16, "name": name, "uuid": uuid})
    log.debug("nvidia-smi cards: %s", cards)
    return cards


def _windows_adapters(runner: Runner) -> list[dict]:
    out = _call(runner, _PS_COMMAND)
    if not out or not out.strip():
        return []
    try:
        rows = json.loads(out)
    except ValueError:
        log.warning("PowerShell adapter list is not JSON: %r", out[:300])
        return []
    if isinstance(rows, dict):
        rows = [rows]
    adapters = []
    for row in rows:
        pnp = row.get("PnpId") or ""
        ids = _PNP_IDS.match(pnp)
        if not ids:
            # Basic Display Adapter, remote desktop and other virtual devices.
            log.debug("skip adapter %r: not a PCI device (%s)", row.get("Name"), pnp)
            continue
        loc = _LOCATION.search(row.get("Location") or "")
        slot = f"0000:{int(loc[1]):02x}:{int(loc[2]):02x}.{int(loc[3])}" if loc else None
        adapters.append({"name": row.get("Name"), "vendor_id": int(ids[1], 16),
                         "device_id": int(ids[2], 16), "slot": slot,
                         "displays": 1 if row.get("HRes") else 0,
                         "driver_version": row.get("Driver")})
    log.debug("Windows adapters: %s", adapters)
    return adapters


def discover_windows(runner: Runner = _run) -> list[Gpu]:
    nvidia = _nvidia_cards(runner)
    adapters = _windows_adapters(runner)
    found: dict[str, Gpu] = {}

    for card in nvidia:
        # Match by slot, or by name when Windows gave no location.
        match = next((a for a in adapters if a["slot"] == card["slot"]), None)
        if match is None:
            same = [a for a in adapters if a["slot"] is None and a["name"] == card["name"]]
            match = same[0] if len(same) == 1 else None
        if match:
            adapters.remove(match)
        found[card["slot"]] = Gpu(
            card=f"nvidia{len(found)}", pci_slot=card["slot"], vendor="nvidia",
            vendor_id=card["vendor_id"], device_id=card["device_id"], driver="nvidia",
            sysfs=None, displays=match["displays"] if match else 0, name=card["name"])

    for i, a in enumerate(adapters):
        vendor = VENDORS.get(a["vendor_id"], f"0x{a['vendor_id']:04x}")
        slot = a["slot"]
        if slot is None:
            # No location from Windows. Name it by its IDs so the key stays
            # the same between runs for config and job records.
            base = slot = f"pnp-{a['vendor_id']:04x}-{a['device_id']:04x}"
            n = 1
            while slot in found:
                n += 1
                slot = f"{base}-{n}"
        if slot in found:
            continue
        # An NVIDIA adapter that nvidia-smi did not list has no usable driver.
        driver = "unknown" if vendor == "nvidia" else f"{vendor}-windows"
        found[slot] = Gpu(
            card=f"{vendor}{i}", pci_slot=slot, vendor=vendor, vendor_id=a["vendor_id"],
            device_id=a["device_id"], driver=driver, sysfs=None,
            displays=a["displays"], name=a["name"])

    gpus = sorted(found.values(), key=lambda g: g.pci_slot)
    for g in gpus:
        log.debug("found %s: %s %s driver=%s monitors=%d name=%s", g.card, g.label,
                  g.pci_id, g.driver, g.displays, g.name)
    log.info("detected %d GPU(s): %s", len(gpus), ", ".join(g.label for g in gpus) or "none")
    return gpus
