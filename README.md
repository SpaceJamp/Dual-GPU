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

## Game mode: one card renders, one card shows

Plug your monitors into one card. That card is the display card. The other card is the render card and only draws games.

```
gpubalance roles                      # check which card does what
gpubalance game -- %command%          # Steam launch option
gpubalance side -- obs                # keep OBS, Discord, browsers off the game card
```

The game draws each frame on the render card. Linux (PRIME render offload) copies the finished frame over PCIe to the display card, which sends it to the monitor. Your desktop, browser and other apps stay on the display card, so the render card is free for the game.

`roles` picks automatically: the card with monitors plugged in shows, and the strongest other card renders. To set it yourself, add this to the config:

```toml
[roles]
render = "nvidia"
display = "amd"
```

Things to know:

- The best supported setup is AMD or Intel as the display card and NVIDIA as the render card. The reverse also works on Wayland with recent NVIDIA drivers, but it gets less testing.
- Copying each frame costs a little frame rate and adds a little latency. The cost grows with resolution and frame rate, and it is worse if the render card sits in a PCIe x4 slot.
- If a game ignores the variables and still runs on the display card, check `gpubalance game -v -- <game>` to see what was set, and look for a GPU choice in the game's own settings.

## Local language models

Unlike games, one llama.cpp model can run on both cards at once. llama.cpp puts some of the model's layers on each card, even across vendors. `gpubalance llm` sets that up from the free VRAM on each card.

```
gpubalance llm -- llama-server -m model.gguf             # split over both cards
gpubalance llm -m single -- llama-server -m model.gguf   # all on the emptiest card
gpubalance llm --leave-game-card -- llama-server -m model.gguf
gpubalance llm -n -- llama-server -m model.gguf          # dry run: show the plan
```

What happens:

1. It runs `<program> --list-devices` to see the devices llama.cpp found.
2. If a card shows up twice (for example `CUDA0` and `Vulkan0` for the same NVIDIA card), it keeps the faster backend: CUDA for NVIDIA, ROCm for AMD, SYCL for Intel, Vulkan otherwise.
3. It leaves `--reserve` MiB free on each card (1024 by default) and gives the rest to the model.
4. It adds `--device` and `--tensor-split` to your command. If you already pass either flag, yours is used.

`--leave-game-card` keeps the model off the render card, so you can game and run a model at the same time.

Which llama.cpp build to use:

- Vulkan only: the simplest way to cover NVIDIA and AMD in one program. Speed is good on both.
- CUDA plus Vulkan (`-DGGML_CUDA=ON -DGGML_VULKAN=ON`): NVIDIA runs through CUDA, AMD through Vulkan. Usually faster on the NVIDIA side.

Things to know:

- Splitting by layers lets you load a model bigger than either card. It does not make generation faster than the faster card alone, because the cards take turns.
- The slower card sets the pace for its share. If one card is much slower, `-m single` on the fast card can win for models that fit.
- Ollama and LM Studio manage GPUs themselves. Use `gpubalance run -k compute -- ollama serve` to pick a card for Ollama.

## Example: RTX 5080 plus RTX 4070 Ti SUPER

With two NVIDIA cards, keep the monitor on the stronger card and let it render games too. There is no frame copy, so games run at full speed. The second card takes models and background work.

Find the 5080's PCI slot with `gpubalance list`, then set:

```toml
[roles]
render = "0000:01:00.0"   # the RTX 5080 slot from `gpubalance list`
```

Then:

```
gpubalance roles                                     # render and display: 5080, side: 4070 Ti SUPER
gpubalance llm -- llama-server -m model.gguf         # one model over both cards, 32 GB total
gpubalance llm --leave-game-card -- llama-server -m model.gguf   # model on the 4070 Ti SUPER only
```

Use a llama.cpp build with CUDA. With two NVIDIA cards, only CUDA device numbers can be matched to PCI slots, so `--leave-game-card` refuses to guess on a Vulkan-only build. `gpubalance llm` sets `CUDA_DEVICE_ORDER=PCI_BUS_ID` so CUDA0 and CUDA1 follow slot order.

Vendor names such as `nvidia` are refused in `[roles]` when two cards share a vendor. Use the PCI slot.

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
- With two NVIDIA cards, Vulkan programs can be steered to either card, but OpenGL programs cannot. Rendering games on the card that drives the monitor avoids the problem.
- The display is still driven by the card your monitor is plugged into. Rendering on the other card adds a copy across PCIe.

## Tests

```
python3 -m unittest discover -s tests -v
```

Tests build a fake sysfs tree, so they run on machines with no GPU.
