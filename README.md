# Dual-GPU

`gpubalance` spreads work across two (or more) GPUs from different vendors on Linux, for example an NVIDIA card next to an AMD card.

## What it does

Each time you start a program through `gpubalance run`, it:

1. Finds every GPU through `/sys/class/drm`, no vendor tools needed.
2. Reads current load: AMD from sysfs (`gpu_busy_percent`, `mem_info_vram_*`), NVIDIA from `nvidia-smi`.
3. Scores each card. Lower wins.
   `score = (1.0 * util + 0.5 * vram_used + 0.3 * running_jobs) / capacity`
4. Sets the environment variables that point OpenGL, Vulkan, CUDA, ROCm or oneAPI at that card.
5. Replaces itself with your program and records the PID, so the next launch knows that card already has a job.

## What it does not do

It balances per program, at launch. It cannot split one game frame or one CUDA kernel across an NVIDIA and an AMD card. Linux has no driver path for that across vendors. If a program needs two GPUs at once, it has to support that itself (Blender Cycles, some ML frameworks, `llama.cpp` with Vulkan).

## Install

Needs Python 3.11 or newer. No other dependencies.

```
pip install --user .
```

## Use

```
gpubalance list                        # detected cards
gpubalance status                      # live load, scores, running jobs
gpubalance run -- steam                # start on the best card
gpubalance run -k compute -- python train.py
gpubalance run -g nvidia -- blender    # force a vendor
gpubalance run -n -- glxgears          # dry run: show choice and env
eval "$(gpubalance pick --env)"        # export vars into your shell
```

`--kind` picks which variables get set:

| kind | variables |
|------|-----------|
| graphics | `DRI_PRIME`, `MESA_VK_DEVICE_SELECT`, `__NV_PRIME_RENDER_OFFLOAD`, `__GLX_VENDOR_LIBRARY_NAME`, `__VK_LAYER_NV_optimus` |
| compute | `CUDA_VISIBLE_DEVICES` (by UUID), `ROCR_VISIBLE_DEVICES`, `ONEAPI_DEVICE_SELECTOR` |
| auto (default) | both |

Steam games: set the launch option to `gpubalance run -- %command%`.

## Configure

Copy `examples/config.toml` to `~/.config/gpubalance/config.toml`. You can set:

- `capacity` per card, so a stronger card takes more work.
- `allow` per card, for example keep compute off the card driving your monitors.
- `[pin]` rules that always send a program to one card.
- Score weights.

## Known limits

- The NVIDIA proprietary driver is required for NVIDIA load data. With `nouveau`, the card reads as 50% busy.
- Intel load is not read yet. Intel cards also read as 50% busy.
- Programs that start a helper and exit (some launchers) drop their job record early.
- With two NVIDIA cards, OpenGL offload may need `__NV_PRIME_RENDER_OFFLOAD_PROVIDER`. Not handled yet.
- The display is still driven by the card your monitor is plugged into. Rendering on the other card adds a copy across PCIe.

## Tests

```
python3 -m unittest discover -s tests -v
```

Tests build a fake sysfs tree, so they run on machines with no GPU.
