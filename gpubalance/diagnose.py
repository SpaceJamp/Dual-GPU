"""Collect everything needed to pinpoint a bug into one report.

The report has raw facts (sysfs values, tool output, config, jobs, recent
log lines) and checks. Each check is pass, info, warn or fail, with a hint
saying what to do about it. Facts never raise: anything that cannot be
read is recorded as an error string, so one broken source does not hide
the rest.
"""

from __future__ import annotations

import getpass
import json
import logging
import os
import platform
import re
import shutil
import subprocess
import sys
import time
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any

from gpubalance import __version__, config, detect, llm, log, metrics, policy
from gpubalance.state import State

logger = logging.getLogger(__name__)

# Variables that change which GPU a program uses. If your shell already
# exports one, it can fight with what gpubalance sets.
STEERING_VARS = (
    "DRI_PRIME", "MESA_VK_DEVICE_SELECT", "__NV_PRIME_RENDER_OFFLOAD",
    "__NV_PRIME_RENDER_OFFLOAD_PROVIDER", "__GLX_VENDOR_LIBRARY_NAME",
    "__VK_LAYER_NV_optimus", "__EGL_VENDOR_LIBRARY_FILENAMES", "VK_ICD_FILENAMES",
    "VK_DRIVER_FILES", "CUDA_VISIBLE_DEVICES", "CUDA_DEVICE_ORDER",
    "ROCR_VISIBLE_DEVICES", "HIP_VISIBLE_DEVICES", "ONEAPI_DEVICE_SELECTOR",
    "GPUBALANCE_GPU",
)
SESSION_VARS = ("XDG_SESSION_TYPE", "XDG_CURRENT_DESKTOP", "WAYLAND_DISPLAY", "DISPLAY")
TOOLS = ("nvidia-smi", "vulkaninfo", "glxinfo", "rocminfo", "llama-server", "llama-cli")
_DEVICE_SELECT_LAYERS = (
    "usr/share/vulkan/implicit_layer.d",
    "etc/vulkan/implicit_layer.d",
)
_PCI_LINK_FILES = ("current_link_speed", "current_link_width",
                   "max_link_speed", "max_link_width")
_AMD_FILES = ("gpu_busy_percent", "mem_info_vram_used", "mem_info_vram_total", "unique_id")


@dataclass
class Check:
    level: str  # pass, info, warn, fail
    id: str
    message: str
    hint: str = ""


def _read(path: Path) -> str | None:
    try:
        return path.read_text(errors="replace").strip()
    except OSError:
        return None


def _os_release(root: Path) -> dict[str, str]:
    fields = {}
    for line in (_read(root / "etc/os-release") or "").splitlines():
        key, _, value = line.partition("=")
        if key:
            fields[key] = value.strip('"')
    return fields


def _gpu_facts(gpu: detect.Gpu, drm: Path, root: Path) -> dict[str, Any]:
    facts: dict[str, Any] = {
        "card": gpu.card, "pci_slot": gpu.pci_slot, "vendor": gpu.vendor,
        "pci_id": gpu.pci_id, "driver": gpu.driver, "render_node": gpu.render_node,
        "monitors": gpu.displays,
        "boot_vga": _read(gpu.sysfs / "boot_vga"),
        "power_state": _read(gpu.sysfs / "power_state"),
        "link": {name: _read(gpu.sysfs / name) for name in _PCI_LINK_FILES},
        "connectors": {c.name: _read(c / "status") for c in sorted(drm.glob(f"{gpu.card}-*"))},
    }
    if gpu.driver == "amdgpu":
        facts["amdgpu"] = {name: _read(gpu.sysfs / name) for name in _AMD_FILES}
    if gpu.render_node:
        node = root / gpu.render_node.lstrip("/")
        facts["render_node_access"] = (
            "missing" if not node.exists()
            else "ok" if os.access(node, os.R_OK | os.W_OK) else "denied")
    return facts


def _nvidia_smi() -> dict[str, Any]:
    if not shutil.which("nvidia-smi"):
        return {"error": "not installed"}
    try:
        proc = subprocess.run(
            ["nvidia-smi", "--query-gpu=pci.bus_id,name,driver_version,pcie.link.gen.current,"
             "pcie.link.width.current,utilization.gpu,memory.used,memory.total",
             "--format=csv"], capture_output=True, text=True, timeout=10)
    except (OSError, subprocess.SubprocessError) as exc:
        return {"error": str(exc)}
    return {"exit": proc.returncode, "stdout": proc.stdout, "stderr": proc.stderr}


def _llama(binary: str, gpus: list[detect.Gpu]) -> dict[str, Any]:
    try:
        devices = llm.list_devices(binary)
    except llm.LlmError as exc:
        return {"error": str(exc)}
    kept = llm.one_per_card(devices)
    return {
        "binary": binary,
        "devices": [asdict(d) | {"backend": d.backend, "vendor": d.vendor} for d in devices],
        "kept": [d.name for d in kept],
        "slots": llm.map_slots(kept, gpus),
    }


def collect(
    sysfs_root: Path = Path("/sys"),
    root: Path = Path("/"),
    config_path: Path | None = None,
    state_path: Path | None = None,
    log_path: Path | None = None,
    llama_binary: str | None = None,
    runner: metrics.Runner | None = None,
) -> dict[str, Any]:
    """Gather facts. root is the filesystem root, swapped out in tests."""
    logger.info("collecting diagnostics")
    report: dict[str, Any] = {
        "meta": {
            "gpubalance": __version__, "run_id": log.RUN_ID,
            "time": time.strftime("%Y-%m-%dT%H:%M:%S%z"),
            "python": sys.version.split()[0], "kernel": platform.release(),
            "machine": platform.machine(),
            "distro": _os_release(root).get("PRETTY_NAME", "unknown"),
        },
        "session": {k: os.environ.get(k) for k in SESSION_VARS},
        "steering_env": {k: os.environ[k] for k in STEERING_VARS if k in os.environ},
        "modules": {
            "nvidia": _read(sysfs_root / "module/nvidia/version"),
            "nvidia_drm_modeset": _read(sysfs_root / "module/nvidia_drm/parameters/modeset"),
            "nouveau_loaded": (sysfs_root / "module/nouveau").exists(),
            "amdgpu_loaded": (sysfs_root / "module/amdgpu").exists(),
        },
        "tools": {t: shutil.which(t) for t in TOOLS},
        "vulkan_device_select_layer": [
            str(p) for d in _DEVICE_SELECT_LAYERS
            for p in sorted((root / d).glob("*device_select*.json"))
        ],
    }

    gpus = detect.discover(sysfs_root)
    drm = sysfs_root / "class" / "drm"
    report["gpus"] = [_gpu_facts(g, drm, root) for g in gpus]
    loads = metrics.sample(gpus, samples=1, **({"runner": runner} if runner else {}))
    report["loads"] = {slot: asdict(load) for slot, load in loads.items()}
    report["nvidia_smi"] = (_nvidia_smi() if any(g.driver == "nvidia" for g in gpus)
                            else {"skipped": "no card uses the nvidia driver"})

    cfg_path = config_path or config.default_path()
    report["config"] = {"path": str(cfg_path), "text": _read(cfg_path)}
    cfg = config.Config()
    try:
        cfg = config.load(cfg_path)
    except (config.ConfigError, ValueError, TypeError) as exc:
        report["config"]["error"] = str(exc)

    try:
        roles = policy.assign_roles(gpus, cfg)
        report["roles"] = {r: g.label for r, g in roles.items()}
    except policy.NoGpuError as exc:
        report["roles"] = {"error": str(exc)}
    report["scores"] = [
        {"gpu": s.gpu.label, "score": round(s.score, 3), "util": s.util,
         "vram": round(s.vram, 3), "jobs": s.jobs}
        for s in policy.score_all(gpus, loads, {}, cfg)
    ]

    state = State(state_path)
    try:
        report["jobs"] = {"path": str(state.path),
                          "active": [asdict(a) for a in state.active()]}
    except OSError as exc:
        report["jobs"] = {"path": str(state.path), "error": str(exc)}

    if llama_binary:
        report["llama"] = _llama(llama_binary, gpus)

    report["log"] = {"path": str(log_path) if log_path else None,
                     "tail": log.tail(log_path, 40) if log_path else []}
    report["checks"] = [asdict(c) for c in run_checks(report)]
    return report


def run_checks(r: dict[str, Any]) -> list[Check]:
    """Turn facts into findings. Works only from the report, so a saved
    JSON report can be re-checked on another machine."""
    out: list[Check] = []
    gpus = r["gpus"]
    if not gpus:
        return [Check("fail", "no-gpus", "No GPUs found under /sys/class/drm.",
                      "Check that a GPU driver is loaded: lsmod | grep -E 'nvidia|amdgpu|i915|xe'.")]
    if len(gpus) == 1:
        out.append(Check("warn", "one-gpu", "Only one GPU found. There is nothing to balance.",
                         "If a second card is installed, check its power cables and BIOS slot settings."))
    else:
        out.append(Check("pass", "gpus", f"{len(gpus)} GPUs found: "
                         + ", ".join(f"{g['vendor']}@{g['pci_slot']} ({g['driver']})" for g in gpus)))

    nvidia_driver = any(g["driver"] == "nvidia" for g in gpus)
    for g in gpus:
        label = f"{g['vendor']}@{g['pci_slot']}"
        if g["vendor"] == "nvidia" and g["driver"] != "nvidia":
            out.append(Check("warn", "nouveau", f"{label} uses the {g['driver']} driver.",
                             "Install the proprietary NVIDIA driver for load data and CUDA."))
        width, max_width = g["link"]["current_link_width"], g["link"]["max_link_width"]
        if width and max_width and width.isdigit() and max_width.isdigit():
            if int(width) < int(max_width):
                out.append(Check("warn", "pcie-width",
                                 f"{label} runs at PCIe x{width}, the card supports x{max_width}.",
                                 "The slot or a shared lane setting limits it. Frame copies and model "
                                 "splits get slower. Check the motherboard manual for slot lanes."))
            else:
                out.append(Check("pass", "pcie-width", f"{label} runs at PCIe x{width}."))
        if g.get("render_node_access") == "denied":
            out.append(Check("fail", "render-access", f"No permission to open {g['render_node']}.",
                             "Add your user to the render and video groups, then log in again."))
        elif g["render_node"] is None:
            out.append(Check("warn", "no-render-node", f"{label} has no render node.",
                             "The driver may not have finished loading. Check dmesg for errors."))
        load = r["loads"].get(g["pci_slot"], {})
        temp, power, limit = load.get("temp_c"), load.get("power_w"), load.get("power_limit_w")
        if temp is not None and temp >= metrics.TEMP_HOT_C:
            out.append(Check("warn", "hot", f"{label} is at {temp:.0f} C.",
                             "Cards throttle around here. Check case airflow, fan curves and "
                             "the gap between the two cards."))
        if power is not None and limit and power >= 0.98 * limit:
            out.append(Check("info", "power-limit",
                             f"{label} draws {power:.0f} W of its {limit:.0f} W limit.",
                             "It is power limited right now, which caps its clocks."))
        amd = g.get("amdgpu")
        if amd and amd.get("gpu_busy_percent") is None:
            out.append(Check("warn", "amd-busy", f"{label} has no gpu_busy_percent file.",
                             "Load is unknown for this card. A newer kernel usually adds it."))

    if nvidia_driver:
        smi = r["nvidia_smi"]
        if smi.get("error") or smi.get("exit"):
            out.append(Check("fail", "nvidia-smi", "nvidia-smi failed: "
                             + (smi.get("error") or smi.get("stderr", "").strip()[:200]),
                             "Reinstall the NVIDIA driver utilities, or reboot after a driver update."))
        else:
            out.append(Check("pass", "nvidia-smi", "nvidia-smi works."))
        if r["modules"]["nvidia_drm_modeset"] not in ("Y", "1"):
            out.append(Check("warn", "modeset", "nvidia-drm modeset is off.",
                             "Wayland and PRIME offload need it. Add nvidia-drm.modeset=1 to the "
                             "kernel command line."))

    monitors = [g for g in gpus if g["monitors"]]
    if not monitors:
        out.append(Check("warn", "no-monitor", "No connected monitor found on any card.",
                         "Connector status may be unreadable on this driver. Set [roles] display "
                         "in the config so roles do not depend on it."))
    elif len(monitors) > 1:
        out.append(Check("info", "multi-monitor-cards",
                         "Monitors are plugged into more than one card.",
                         "Roles use the card with the most monitors. Set [roles] to choose."))

    roles = r["roles"]
    if "error" in roles:
        out.append(Check("fail", "roles", f"Roles failed: {roles['error']}",
                         "Fix [roles] in the config. Use a PCI slot from `gpubalance list`."))
    elif len(gpus) > 1:
        out.append(Check("info", "roles", f"render={roles['render']} display={roles['display']} "
                         f"side={roles['side']}"))
        vendors = [g["vendor"] for g in gpus]
        render = next(g for g in gpus if f"{g['vendor']}@{g['pci_slot']}" == roles["render"])
        if vendors.count("nvidia") > 1 and render["vendor"] == "nvidia" and not render["monitors"]:
            out.append(Check("warn", "two-nvidia-gl",
                             "Games render on an NVIDIA card with no monitor, next to another "
                             "NVIDIA card. OpenGL programs cannot be steered to it.",
                             "Plug the monitor into the render card, or set render to the "
                             "monitor card."))

    if "error" in r["config"]:
        out.append(Check("fail", "config", f"Config error: {r['config']['error']}",
                         "Fix the file, or move it aside to use defaults."))
    if r["steering_env"]:
        out.append(Check("warn", "steering-env",
                         "Your shell already sets: " + ", ".join(sorted(r["steering_env"])),
                         "gpubalance overrides these for programs it launches, but other "
                         "programs keep them. Remove them from your shell profile if unintended."))
    if not r["vulkan_device_select_layer"] and len(gpus) > 1:
        out.append(Check("warn", "vk-device-select",
                         "Mesa's Vulkan device-select layer is not installed.",
                         "Without it MESA_VK_DEVICE_SELECT is ignored. Install your distro's "
                         "mesa-vulkan-drivers package."))
    if "error" in r["jobs"]:
        out.append(Check("warn", "jobs", f"Job file problem: {r['jobs']['error']}"))

    lm = r.get("llama")
    if lm:
        if "error" in lm:
            out.append(Check("fail", "llama", lm["error"]))
        elif not lm["devices"]:
            out.append(Check("fail", "llama", "llama.cpp sees no GPUs.",
                             "Use a build with CUDA, ROCm or Vulkan enabled."))
        else:
            seen = {s for s in lm["slots"].values() if s}
            missing = [f"{g['vendor']}@{g['pci_slot']}" for g in gpus if g["pci_slot"] not in seen]
            unmapped = [n for n, s in lm["slots"].items() if s is None]
            if unmapped:
                out.append(Check("warn", "llama-map",
                                 f"Cannot match {', '.join(unmapped)} to a PCI slot.",
                                 "--leave-game-card will refuse to run. A CUDA build fixes "
                                 "this for NVIDIA cards."))
            if missing and not unmapped:
                out.append(Check("warn", "llama-missing",
                                 f"llama.cpp does not see {', '.join(missing)}.",
                                 "A CUDA-only build cannot use AMD cards. Add Vulkan "
                                 "(-DGGML_VULKAN=ON)."))
            if not missing and not unmapped:
                out.append(Check("pass", "llama", "llama.cpp sees every card: "
                                 + ", ".join(lm["kept"])))
    return out


def redact(text: str) -> str:
    """Hide the home path, user name and host name before sharing."""
    home = os.path.expanduser("~")
    if home not in ("", "/"):
        # Only where a path starts, so "/root" inside "/run/user/root" stays.
        text = re.sub(rf"(?<![\w/.-]){re.escape(home)}(?![\w.-])", "~", text)
    user, host = getpass.getuser(), platform.node()
    # The user name only as a path component, so "--root" survives a "root" user.
    if user:
        text = re.sub(rf"(?<=/){re.escape(user)}(?=/|\b)", "<user>", text)
    if host:
        text = re.sub(rf"\b{re.escape(host)}\b", "<host>", text)
    return text


def render_text(r: dict[str, Any]) -> str:
    lines = [f"gpubalance {r['meta']['gpubalance']} diagnostics, run {r['meta']['run_id']}",
             f"{r['meta']['distro']}, kernel {r['meta']['kernel']}, Python {r['meta']['python']}",
             "session: " + ", ".join(f"{k}={v}" for k, v in r["session"].items() if v), ""]
    lines.append("Checks")
    for c in r["checks"]:
        lines.append(f"  [{c['level'].upper():<4}] {c['message']}")
        if c["hint"]:
            lines.append(f"         {c['hint']}")
    lines += ["", "GPUs"]
    for g in r["gpus"]:
        link = g["link"]
        pcie = "unknown"
        if link["current_link_width"]:
            pcie = (f"x{link['current_link_width']}/x{link['max_link_width']} "
                    f"{link['current_link_speed'] or ''}").strip()
        lines.append(f"  {g['card']} {g['vendor']}@{g['pci_slot']} {g['pci_id']} "
                     f"driver={g['driver']} monitors={g['monitors']} pcie={pcie} "
                     f"render_node={g.get('render_node_access', '-')}")
        load = r["loads"].get(g["pci_slot"], {})
        util = "unknown" if load.get("util") is None else f"{load['util']:.0f}%"
        vram = "unknown"
        if load.get("vram_used") is not None and load.get("vram_total"):
            vram = f"{load['vram_used'] / 2**30:.1f}/{load['vram_total'] / 2**30:.1f} GiB"
        temp = "unknown" if load.get("temp_c") is None else f"{load['temp_c']:.0f} C"
        power = "unknown"
        if load.get("power_w") is not None:
            power = f"{load['power_w']:.0f}"
            power += f"/{load['power_limit_w']:.0f} W" if load.get("power_limit_w") else " W"
        lines.append(f"    load: util={util} vram={vram} temp={temp} power={power} "
                     f"source={load.get('source')}")
    lines += ["", "Modules: " + ", ".join(f"{k}={v}" for k, v in r["modules"].items()),
              "Tools: " + ", ".join(f"{k}={'yes' if v else 'no'}" for k, v in r["tools"].items())]
    if r["scores"]:
        lines.append("Scores: " + ", ".join(f"{s['gpu']}={s['score']}" for s in r["scores"]))
    lines.append(f"Jobs: {len(r['jobs'].get('active', []))} active ({r['jobs']['path']})")
    lines.append(f"Config: {r['config']['path']} ({'present' if r['config']['text'] else 'absent'})")
    if "llama" in r:
        lm = r["llama"]
        lines.append("llama.cpp: " + (lm.get("error") or
                     ", ".join(f"{d['name']}={d['description']}" for d in lm["devices"])))
    if r["log"]["tail"]:
        lines += ["", f"Last log lines ({r['log']['path']})"]
        lines += [f"  {line}" for line in r["log"]["tail"]]
    return "\n".join(lines) + "\n"


def render_json(r: dict[str, Any]) -> str:
    return json.dumps(r, indent=2, default=str) + "\n"
