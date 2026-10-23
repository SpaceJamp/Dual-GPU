"""Find GPUs through the DRM subsystem in sysfs.

Every GPU with a kernel driver shows up as /sys/class/drm/cardN, and its
device/ directory is the PCI device. That gives us vendor, device ID, PCI
slot and driver without any vendor tooling installed.
"""

from __future__ import annotations

from dataclasses import dataclass
from pathlib import Path

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


def discover(sysfs_root: Path = Path("/sys")) -> list[Gpu]:
    """Return one Gpu per PCI display device, sorted by PCI slot."""
    drm = sysfs_root / "class" / "drm"
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
            continue
        if int(class_raw, 16) >> 16 != _DISPLAY_CLASS:
            continue
        uevent = _uevent(device)
        slot = uevent.get("PCI_SLOT_NAME")
        if not slot or slot in found:
            continue
        vendor_id = int(vendor_raw, 16)
        render = sorted((device / "drm").glob("renderD*"))
        found[slot] = Gpu(
            card=entry.name,
            pci_slot=slot,
            vendor=VENDORS.get(vendor_id, f"0x{vendor_id:04x}"),
            vendor_id=vendor_id,
            device_id=int(device_raw, 16),
            driver=uevent.get("DRIVER", "unknown"),
            sysfs=device,
            render_node=f"/dev/dri/{render[0].name}" if render else None,
        )
    return sorted(found.values(), key=lambda g: g.pci_slot)
