"""Build a fake /sys tree with one AMD and one NVIDIA card."""

from pathlib import Path


def make_card(root: Path, card: str, slot: str, vendor: str, device: str,
              driver: str, render: str, extra: dict[str, str] | None = None) -> Path:
    dev = root / "devices" / slot
    dev.mkdir(parents=True)
    (dev / "vendor").write_text(vendor + "\n")
    (dev / "device").write_text(device + "\n")
    (dev / "class").write_text("0x030000\n")
    (dev / "uevent").write_text(f"DRIVER={driver}\nPCI_SLOT_NAME={slot}\n")
    (dev / "drm" / render).mkdir(parents=True)
    for name, value in (extra or {}).items():
        (dev / name).write_text(value + "\n")
    drm = root / "class" / "drm"
    (drm / card).mkdir(parents=True, exist_ok=True)
    (drm / card / "device").symlink_to(dev)
    return dev


def plug_monitor(root: Path, connector: str) -> None:
    conn = root / "class" / "drm" / connector
    conn.mkdir(parents=True, exist_ok=True)
    (conn / "status").write_text("connected\n")


def make_mixed(root: Path, amd_busy: int = 10) -> Path:
    make_card(root, "card0", "0000:01:00.0", "0x10de", "0x2684", "nvidia", "renderD128")
    make_card(root, "card1", "0000:03:00.0", "0x1002", "0x744c", "amdgpu", "renderD129", {
        "gpu_busy_percent": str(amd_busy),
        "mem_info_vram_used": str(2 * 2**30),
        "mem_info_vram_total": str(16 * 2**30),
        "unique_id": "abcdef0123456789",
    })
    # Connector entries and non-PCI framebuffers must be ignored.
    (root / "class" / "drm" / "card1-DP-1").mkdir()
    (root / "class" / "drm" / "card2").mkdir()
    return root


def nvidia_smi(util: int = 80, used_mib: int = 12000, total_mib: int = 24564,
               name: str = "NVIDIA GeForce RTX 4090"):
    def runner(cmd):
        return f"00000000:01:00.0, GPU-1111-2222, {util}, {used_mib}, {total_mib}, 16, 16, {name}\n"
    return runner
