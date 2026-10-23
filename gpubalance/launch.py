"""Environment variables that steer a program onto one GPU.

There is no single switch on Linux. OpenGL, Vulkan, CUDA, ROCm and oneAPI
each read their own variables, so we set the ones that apply to the target.
"""

from __future__ import annotations

from gpubalance.detect import Gpu
from gpubalance.metrics import Load


def _vendor_index(gpu: Gpu, gpus: list[Gpu]) -> int:
    same = sorted((g for g in gpus if g.vendor == gpu.vendor), key=lambda g: g.pci_slot)
    return same.index(gpu)


def graphics_env(gpu: Gpu) -> dict[str, str]:
    env = {
        # Vulkan: Mesa's device-select layer moves this device to the front.
        # The layer sees every Vulkan device, NVIDIA included.
        "MESA_VK_DEVICE_SELECT": gpu.pci_id,
    }
    if gpu.driver == "nvidia":
        env.update({
            "__NV_PRIME_RENDER_OFFLOAD": "1",
            "__GLX_VENDOR_LIBRARY_NAME": "nvidia",
            "__VK_LAYER_NV_optimus": "NVIDIA_only",
        })
    else:
        env.update({
            # Mesa OpenGL render offload, addressed by PCI slot.
            "DRI_PRIME": "pci-" + gpu.pci_slot.replace(":", "_").replace(".", "_"),
            "__GLX_VENDOR_LIBRARY_NAME": "mesa",
            "__VK_LAYER_NV_optimus": "non_NVIDIA_only",
        })
    return env


def compute_env(gpu: Gpu, gpus: list[Gpu], load: Load | None = None) -> dict[str, str]:
    index = str(_vendor_index(gpu, gpus))
    if gpu.vendor == "nvidia":
        uuid = load.uuid if load and load.uuid else None
        return {
            "CUDA_DEVICE_ORDER": "PCI_BUS_ID",
            "CUDA_VISIBLE_DEVICES": uuid or index,
        }
    if gpu.vendor == "amd":
        try:
            unique = (gpu.sysfs / "unique_id").read_text().strip()
        except OSError:
            unique = ""
        # ROCm accepts "GPU-<unique_id>", which survives enumeration order changes.
        return {"ROCR_VISIBLE_DEVICES": f"GPU-{unique}" if unique else index}
    if gpu.vendor == "intel":
        return {"ONEAPI_DEVICE_SELECTOR": f"level_zero:{index}"}
    return {}


def build_env(
    gpu: Gpu, gpus: list[Gpu], kind: str, load: Load | None = None
) -> dict[str, str]:
    env: dict[str, str] = {}
    if kind in ("auto", "graphics"):
        env.update(graphics_env(gpu))
    if kind in ("auto", "compute"):
        env.update(compute_env(gpu, gpus, load))
    env["GPUBALANCE_GPU"] = gpu.pci_slot
    return env
