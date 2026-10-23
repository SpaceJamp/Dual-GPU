"""Find GPUs through the DRM subsystem in sysfs.

Every GPU with a kernel driver shows up as /sys/class/drm/cardN, and its
device/ directory is the PCI device. That gives us vendor, device ID, PCI
slot and driver without any vendor tooling installed.
"""

from __future__ import annotations

import logging
from dataclasses import dataclass
from pathlib import Path

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
    sysfs: Path
    render_node: str | None = None
    # Monitors currently plugged into this card.
    displays: int = 0

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


def discover(sysfs_root: Path = Path("/sys")) -> list[Gpu]:
    """Return one Gpu per PCI display device, sorted by PCI slot."""
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
