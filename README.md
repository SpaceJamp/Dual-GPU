# Dual-GPU

`gpubalance` spreads work across two (or more) GPUs on Linux and Windows, for example an NVIDIA card next to an AMD card, or two NVIDIA cards. Windows support covers less; see [Windows](#windows).

## What it does

Each time you start a program through `gpubalance run`, it:

1. Finds every GPU through `/sys/class/drm` on Linux, no vendor tools needed (Windows: `nvidia-smi` and PowerShell).
2. Reads current load: AMD from sysfs (`gpu_busy_percent`, `mem_info_vram_*`), NVIDIA from `nvidia-smi`.
3. Scores each card. Lower wins.
   `score = (1.0 * util + 0.5 * vram_used + 0.3 * running_jobs) / capacity`
4. Sets the environment variables that point OpenGL, Vulkan, CUDA, ROCm or oneAPI at that card.
5. Replaces itself with your program and records the PID, so the next launch knows that card already has a job. On Windows it starts the program and waits instead.

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

Needs Python 3.11 or newer. On Linux there are no other dependencies. On Windows, pip also installs `windows-curses` for `gpubalance top`.

```
pip install --user .
```

## Windows

Install Python 3.11 or newer from python.org, then in PowerShell:

```
git clone https://github.com/SpaceJamp/Dual-GPU
cd Dual-GPU
py -m pip install --user .
gpubalance list
```

If `gpubalance` is not found, use `py -m gpubalance list`, or add the Scripts folder pip names to your PATH.

What works on Windows:

| Feature | Windows |
|---------|---------|
| `list`, `status`, `roles` | Yes. NVIDIA cards come from `nvidia-smi`; other cards from Windows' adapter list. |
| Load and VRAM | NVIDIA cards only. Other cards show as unknown and score as half busy. |
| `run -k compute` | Yes. Sets `CUDA_VISIBLE_DEVICES` (by UUID) or `HIP_VISIBLE_DEVICES`. |
| `llm` | Yes, including `--leave-game-card` with two NVIDIA cards and a CUDA build. |
| `top`, `diagnose`, logging | Yes. `top` reads the PCIe link width from `nvidia-smi`. |
| `game`, `side`, `run -k graphics` | No. They start the program and tell you to set its GPU in Settings > System > Display > Graphics. |

Why graphics steering is missing: on Linux, environment variables decide which card renders. Windows has no such variables. Its only per-app control is the Graphics settings page, and the registry format for picking one specific card (when both cards are high performance) is not documented. gpubalance does not write guesses into your registry.

Other differences:

- Windows has no `exec`, so gpubalance starts the program, waits for it, records the program's own PID, and returns its exit code.
- Files live in `%APPDATA%\gpubalance\config.toml` (config) and `%LOCALAPPDATA%\gpubalance\` (log and job list).
- The monitor count is a heuristic: a card counts as driving a monitor when Windows reports a current resolution for it.

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

## Live view

```
gpubalance top            # live screen, updates every second
gpubalance top -i 0.5     # update twice a second
gpubalance top --once     # print one frame as plain text and exit
```

```
gpubalance top  14:02:11  every 1.0s
nvidia@0000:01:00.0  NVIDIA GeForce RTX 5080  nvidia  PCIe x16/x16  [render, display]  score 0.42
  util ██████████████▍       72%               ▁▂▃▅▇▇▆▅▅▆▇▇▇▆▅▆▇
  vram █████████████▉        11.2/16.0 GiB     ▅▅▅▆▆▆▆▆▆▆▆▆▆▆▆▆▆
nvidia@0000:05:00.0  NVIDIA GeForce RTX 4070 Ti SUPER  nvidia  PCIe x4/x16  [side]  score 0.61
  util █████████████████▌    88%               ▁▁▂▅▇▇▇▇▇▇▇▇▇▇▇▇▇
  vram █████████████████▌    14.0/16.0 GiB     ▂▂▃▇▇▇▇▇▇▇▇▇▇▇▇▇▇

Jobs (2)
  pid 4211   nvidia@0000:01:00.0   render    12m03s  steam
  pid 4302   nvidia@0000:05:00.0   llm        3m40s  llama-server -m model.gguf ...

Log  ~/.local/state/gpubalance/gpubalance.log
  14:01:58 INFO policy   chose nvidia@0000:05:00.0 for compute work (score 0.180; ...)
q quit  p pause  +/- speed  v verbose  c clear alerts  ? help
```

What you see:

- One block per card: model name (NVIDIA cards), driver, PCIe link, role and score.
- Utilization and VRAM bars with a history graph that fills in from the right. Green is below 50%, yellow is up to 85%, red is above that.
- A PCIe link in yellow means the card runs at fewer lanes than it supports.
- Every program started through gpubalance, with its card and how long it has run.
- Alerts: warnings collected into one line each with a count, so a missing `nvidia-smi` shows once as `x60` instead of filling the screen.
- The live end of the log file, including launches from other terminals. Press `v` to include debug lines.

Keys: `q` quit, `p` pause, `+` and `-` change the speed (0.2 s to 30 s), `v` verbose log, `c` clear alerts, `?` help.

While `top` runs, it writes each warning to the log file once, and skips its own per-second debug lines, so the log stays useful. It falls back to plain ASCII bars on a terminal that isn't UTF-8.

## Logging and diagnostics

Every run writes a detailed log to `~/.local/state/gpubalance/gpubalance.log`. Each file holds up to 1 MB, and the 3 previous files are kept. Every line carries a run ID, so you can pull out one launch even when several ran at once:

```
2026-10-23 12:35:22,967 32f3a5f0 1942 DEBUG   gpubalance.detect: found card1: amd@0000:03:00.0 1002:744c driver=amdgpu ...
grep 32f3a5f0 ~/.local/state/gpubalance/gpubalance.log
```

The log records each decision and the reason for it:

- Detection: every card found or skipped, and why (not PCI, not a display controller, duplicate slot), plus every connector's status.
- Load: raw `nvidia-smi` output and how long it took, each AMD busy sample, and the reason a card's load is unknown.
- Scoring: each card's score broken into utilization, VRAM, jobs and capacity, and the final ranking.
- Roles: which card got each role, and whether that came from your config or from where the monitors are plugged in.
- LLM planning: the devices llama.cpp listed, which duplicate views were merged and why, the device to PCI slot map, and the memory left on each card.
- Launch: the exact environment variables set, and the job recorded.

Options:

```
gpubalance --log-level debug status     # show everything on screen too
gpubalance --log-json run -- steam      # log file as JSON lines, for jq or scripts
gpubalance --log-file /tmp/gb.log list  # log somewhere else
gpubalance --no-log-file list           # no log file (or GPUBALANCE_LOG_FILE=off)
GPUBALANCE_LOG=info gpubalance run -- steam
```

If gpubalance hits an unexpected error, the screen shows a one-line summary and the run ID, and the full traceback goes to the log file. The exit codes are 1 for a known problem (no GPU, bad config), 70 for an unexpected error and 130 for Ctrl+C.

### Bug reports

```
gpubalance diagnose                      # read it on screen
gpubalance diagnose -o report.txt        # save it to share
gpubalance diagnose --json -o report.json
gpubalance diagnose --llama llama-server # include what llama.cpp sees
```

The report collects:

- System: kernel, distro, session type, loaded modules and the NVIDIA modeset setting.
- Each card: PCIe link width and speed, connectors, AMD sysfs values, and access to the render node.
- Tools: `nvidia-smi` output, installed tools, and whether the Vulkan device-select layer is installed.
- Setup: environment variables already set in your shell, the config file, running jobs, roles and scores.
- The last 40 log lines.

It ends with checks marked PASS, INFO, WARN or FAIL, each with a fix. Among them:

- A card running at fewer PCIe lanes than it supports, such as x4 in an x16 slot.
- The nouveau driver on an NVIDIA card, a broken `nvidia-smi`, or nvidia-drm modeset turned off.
- No permission to open a render node (fix: join the render and video groups).
- A two-NVIDIA setup where OpenGL can't be steered.
- Leftover variables like `DRI_PRIME` in your shell.
- A llama.cpp build that can't see one of your cards.

Your user name, host name and home path are hidden in the report. Pass `--no-redact` to keep them. `diagnose` exits with 1 if any check fails.

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
