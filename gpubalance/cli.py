"""Command line entry point."""

from __future__ import annotations

import argparse
import logging
import os
import shlex
import sys
from pathlib import Path

from gpubalance import __version__, config, detect, diagnose, launch, llm, log, metrics, policy
from gpubalance.state import State

logger = logging.getLogger("gpubalance.cli")


def _fmt_pct(value: float | None) -> str:
    return "  n/a" if value is None else f"{value:4.0f}%"


def _fmt_gib(value: int | None) -> str:
    return "  n/a" if value is None else f"{value / 2**30:5.1f}"


def _context(args):
    gpus = detect.discover(Path(args.sysfs))
    if not gpus:
        raise policy.NoGpuError("no GPUs found under /sys/class/drm")
    cfg = config.load(Path(args.config) if args.config else None)
    state = State(Path(args.state) if args.state else None)
    return gpus, cfg, state


def cmd_list(args) -> int:
    gpus = detect.discover(Path(args.sysfs))
    if not gpus:
        print("No GPUs found.")
        return 1
    for g in gpus:
        print(f"{g.card:<7} {g.pci_slot}  {g.vendor:<7} {g.pci_id}  "
              f"driver={g.driver:<8} render={g.render_node or '-'}  monitors={g.displays}")
    return 0


def cmd_status(args) -> int:
    gpus, cfg, state = _context(args)
    loads = metrics.sample(gpus, samples=cfg.samples)
    jobs = state.counts()
    ranked = {s.gpu.pci_slot: s for s in policy.score_all(gpus, loads, jobs, cfg)}
    print(f"{'GPU':<22} {'util':>5} {'VRAM GiB':>13} {'jobs':>4} {'score':>6}  source")
    for g in gpus:
        load = loads[g.pci_slot]
        s = ranked.get(g.pci_slot)
        vram = f"{_fmt_gib(load.vram_used)}/{_fmt_gib(load.vram_total)}"
        score = f"{s.score:6.2f}" if s else "     -"
        print(f"{g.label:<22} {_fmt_pct(load.util)} {vram:>13} "
              f"{jobs.get(g.pci_slot, 0):>4} {score}  {load.source}")
    active = state.active()
    if active:
        print("\nRunning under gpubalance:")
        for a in active:
            print(f"  pid {a.pid:<7} {a.gpu}  {a.kind:<8} {a.command}")
    return 0


def _pick(args, gpus, cfg, state, command: list[str] | None = None):
    target = args.gpu
    if not target and command:
        target = cfg.pins.get(os.path.basename(command[0]))
    loads = metrics.sample(gpus, samples=cfg.samples)
    chosen = policy.choose(gpus, loads, state.counts(), cfg, args.kind, target)
    env = launch.build_env(chosen.gpu, gpus, args.kind, loads.get(chosen.gpu.pci_slot))
    return chosen, env


def cmd_pick(args) -> int:
    gpus, cfg, state = _context(args)
    chosen, env = _pick(args, gpus, cfg, state)
    if args.env:
        for key, value in env.items():
            print(f"export {key}={shlex.quote(value)}")
    else:
        print(chosen.gpu.pci_slot)
    return 0


def _command(args) -> list[str] | None:
    command = args.command
    if command and command[0] == "--":
        command = command[1:]
    if not command:
        print(f"gpubalance {args.cmd}: no command given", file=sys.stderr)
    return command or None


def _launch(args, state, gpu, kind, command, env, note) -> int:
    if args.verbose or args.dry_run:
        print(f"gpubalance: {gpu.label} ({note})", file=sys.stderr)
        for key, value in env.items():
            print(f"  {key}={value}", file=sys.stderr)
    if args.dry_run:
        return 0
    # exec keeps our PID, so the record disappears when the program exits.
    state.add(os.getpid(), gpu.pci_slot, kind, shlex.join(command))
    try:
        os.execvpe(command[0], command, {**os.environ, **env})
    except OSError as exc:
        print(f"gpubalance: cannot run {command[0]}: {exc}", file=sys.stderr)
        return 127


def cmd_run(args) -> int:
    command = _command(args)
    if not command:
        return 2
    gpus, cfg, state = _context(args)
    chosen, env = _pick(args, gpus, cfg, state, command)
    return _launch(args, state, chosen.gpu, args.kind, command, env,
                   f"score {chosen.score:.2f}")


def cmd_roles(args) -> int:
    gpus, cfg, _ = _context(args)
    roles = policy.assign_roles(gpus, cfg)
    for role in policy.ROLES:
        gpu = roles[role]
        print(f"{role:<8} {gpu.label}  monitors={gpu.displays}")
    if len(gpus) == 1:
        print("\nOnly one card is available, so it does every job.")
    elif roles["render"] is roles["display"]:
        print("\nGames run on the monitor card with no frame copy. "
              "Background apps and models go to the other card.")
    elif roles["render"].displays:
        print("\nThe render card also has a monitor plugged in. Games on that "
              "monitor skip the copy, but the card is not dedicated to rendering.")
    return 0


def _cmd_role(args, role: str) -> int:
    command = _command(args)
    if not command:
        return 2
    gpus, cfg, state = _context(args)
    gpu = policy.assign_roles(gpus, cfg)[role]
    # The finished frames always land on the display card for scan-out;
    # these variables only decide where the drawing happens.
    env = launch.build_env(gpu, gpus, "graphics")
    if (gpu.driver == "nvidia" and not gpu.displays
            and sum(g.driver == "nvidia" for g in gpus) > 1):
        # NVIDIA's OpenGL offload switch cannot pick between two NVIDIA
        # cards. Vulkan still can, through MESA_VK_DEVICE_SELECT.
        print("gpubalance: two NVIDIA cards; Vulkan programs will follow this "
              "choice, OpenGL programs may not", file=sys.stderr)
    return _launch(args, state, gpu, role, command, env, f"{role} card")


def cmd_llm(args) -> int:
    command = _command(args)
    if not command:
        return 2
    gpus, cfg, state = _context(args)
    try:
        devices = llm.list_devices(command[0])
        if not devices:
            raise llm.LlmError(f"{command[0]} --list-devices found no GPUs; "
                               "is this a llama.cpp build with GPU support?")
        slots = llm.map_slots(devices, gpus)
        skip: frozenset[str] = frozenset()
        if args.leave_game_card:
            render = policy.assign_roles(gpus, cfg)["render"]
            unknown = [d.name for d in llm.one_per_card(devices)
                       if slots[d.name] is None and d.vendor == render.vendor]
            if unknown:
                raise llm.LlmError(
                    f"cannot tell which of {', '.join(unknown)} is the game card; "
                    "a llama.cpp build with CUDA can tell NVIDIA cards apart")
            skip = frozenset(n for n, slot in slots.items() if slot == render.pci_slot)
        chosen, shares = llm.plan(devices, args.mode, args.reserve, skip)
    except llm.LlmError as exc:
        print(f"gpubalance: {exc}", file=sys.stderr)
        return 1
    full = llm.build_args(command, chosen, shares)
    if args.verbose or args.dry_run:
        for dev, mib in zip(chosen, shares):
            print(f"gpubalance: {dev.name} {dev.description}: {mib} MiB for the model",
                  file=sys.stderr)
        print(f"  {shlex.join(full)}", file=sys.stderr)
    if args.dry_run:
        return 0
    main = chosen[shares.index(max(shares))]
    state.add(os.getpid(), slots.get(main.name) or main.name, "llm", shlex.join(full))
    try:
        os.execvpe(full[0], full, {**os.environ, **llm.LAUNCH_ENV})
    except OSError as exc:
        print(f"gpubalance: cannot run {full[0]}: {exc}", file=sys.stderr)
        return 127


def cmd_game(args) -> int:
    return _cmd_role(args, "render")


def cmd_side(args) -> int:
    return _cmd_role(args, "side")


def cmd_diagnose(args) -> int:
    report = diagnose.collect(
        sysfs_root=Path(args.sysfs),
        root=Path(args.root),
        config_path=Path(args.config) if args.config else None,
        state_path=Path(args.state) if args.state else None,
        log_path=args.log_path,
        llama_binary=args.llama,
    )
    text = diagnose.render_json(report) if args.json else diagnose.render_text(report)
    if not args.no_redact:
        text = diagnose.redact(text)
    if args.output:
        Path(args.output).write_text(text)
        print(f"Report written to {args.output}")
    else:
        sys.stdout.write(text)
    failed = [c for c in report["checks"] if c["level"] == "fail"]
    logger.info("diagnose finished: %d check(s) failed", len(failed))
    return 1 if failed else 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="gpubalance",
        description="Send each program to the least busy GPU, across vendors.",
    )
    parser.add_argument("--sysfs", default="/sys", help=argparse.SUPPRESS)
    parser.add_argument("--config", help="config file (default ~/.config/gpubalance/config.toml)")
    parser.add_argument("--state", help=argparse.SUPPRESS)
    parser.add_argument("--root", default="/", help=argparse.SUPPRESS)
    parser.add_argument("--version", action="version", version=f"gpubalance {__version__}")
    parser.add_argument("--log-level", choices=log.LEVELS,
                        default=os.environ.get("GPUBALANCE_LOG", "warning").lower(),
                        help="what to print on screen (default warning, or $GPUBALANCE_LOG)")
    parser.add_argument("--log-file", help=f"log file (default {log.default_path()})")
    parser.add_argument("--no-log-file", action="store_true",
                        help="do not write a log file (or set GPUBALANCE_LOG_FILE=off)")
    parser.add_argument("--log-json", action="store_true",
                        help="write the log file as one JSON object per line")
    sub = parser.add_subparsers(dest="cmd", required=True)

    sub.add_parser("list", help="show detected GPUs").set_defaults(func=cmd_list)
    sub.add_parser("status", help="show load, scores and running jobs").set_defaults(func=cmd_status)

    def add_choice_opts(p):
        p.add_argument("-k", "--kind", choices=policy.KINDS, default="auto",
                       help="which APIs to steer (default: both)")
        p.add_argument("-g", "--gpu", help="force a vendor (amd, nvidia, intel), PCI slot or card name")

    pick = sub.add_parser("pick", help="print the GPU a new job would get")
    add_choice_opts(pick)
    pick.add_argument("--env", action="store_true", help="print shell export lines")
    pick.set_defaults(func=cmd_pick)

    run = sub.add_parser("run", help="run a command on the best GPU")
    add_choice_opts(run)
    run.add_argument("-n", "--dry-run", action="store_true", help="show the choice, do not run")
    run.add_argument("-v", "--verbose", action="store_true")
    run.add_argument("command", nargs=argparse.REMAINDER)
    run.set_defaults(func=cmd_run)

    sub.add_parser("roles", help="show which card renders and which drives the monitors"
                   ).set_defaults(func=cmd_roles)
    for name, func, text in (
        ("game", cmd_game, "run a game on the render card"),
        ("side", cmd_side, "run a background app (OBS, Discord, browser) off the game card"),
    ):
        p = sub.add_parser(name, help=text)
        p.add_argument("-n", "--dry-run", action="store_true", help="show the choice, do not run")
        p.add_argument("-v", "--verbose", action="store_true")
        p.add_argument("command", nargs=argparse.REMAINDER)
        p.set_defaults(func=func)

    lm = sub.add_parser("llm", help="run a llama.cpp program with the model spread over your cards")
    lm.add_argument("-m", "--mode", choices=("split", "single"), default="split",
                    help="split: share the model across cards by free VRAM; "
                         "single: put it all on the card with the most free VRAM")
    lm.add_argument("-r", "--reserve", type=int, default=1024, metavar="MIB",
                    help="VRAM to leave free on each card (default 1024)")
    lm.add_argument("--leave-game-card", action="store_true",
                    help="keep the model off the render card so you can game at the same time")
    lm.add_argument("-n", "--dry-run", action="store_true", help="show the plan, do not run")
    lm.add_argument("-v", "--verbose", action="store_true")
    lm.add_argument("command", nargs=argparse.REMAINDER)
    lm.set_defaults(func=cmd_llm)

    dg = sub.add_parser("diagnose", help="collect a bug report: hardware, drivers, config, "
                        "decisions and recent log lines")
    dg.add_argument("--json", action="store_true", help="machine readable output")
    dg.add_argument("-o", "--output", help="write the report to a file")
    dg.add_argument("--llama", metavar="BINARY", help="also check what this llama.cpp binary sees")
    dg.add_argument("--no-redact", action="store_true",
                    help="keep user name, host name and home path in the report")
    dg.set_defaults(func=cmd_diagnose)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    use_file = not args.no_log_file and os.environ.get("GPUBALANCE_LOG_FILE") != "off"
    args.log_path = log.setup(args.log_level, Path(args.log_file) if args.log_file else None,
                              use_file, args.log_json)
    logger.info("gpubalance %s start: %s", __version__, shlex.join(sys.argv[1:] if argv is None else argv))
    try:
        code = args.func(args)
    except (policy.NoGpuError, config.ConfigError) as exc:
        logger.error("%s", exc)
        return 1
    except KeyboardInterrupt:
        logger.info("interrupted")
        return 130
    except Exception:
        # Full traceback goes to the log file; the screen gets a short pointer.
        logger.debug("unexpected error", exc_info=True)
        exc = sys.exc_info()[1]
        where = f"Details are in {args.log_path} (run {log.RUN_ID})." if args.log_path else \
            "Run again with --log-level debug for details."
        print(f"gpubalance: unexpected error: {type(exc).__name__}: {exc}\n{where}\n"
              "Run `gpubalance diagnose -o report.txt` and include the report in a bug report.",
              file=sys.stderr)
        return 70
    logger.info("exit %s", code)
    return code
