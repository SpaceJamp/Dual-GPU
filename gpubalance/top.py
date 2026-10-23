"""Live terminal view of the cards, jobs and log: `gpubalance top`.

Three layers, so the drawing can be tested without a terminal:
  sample()  reads loads, scores and jobs into a View
  render()  turns a View into lines of (text, style) segments
  run()     the curses loop: keys, colors, timing
"""

from __future__ import annotations

import curses
import json
import locale
import logging
import os
import re
import time
from collections import deque
from dataclasses import dataclass, field
from pathlib import Path

from gpubalance import config, detect, metrics, policy
from gpubalance.state import Assignment, State

logger = logging.getLogger(__name__)

Segment = tuple[str, str]
Line = list[Segment]

_SPARK = "▁▂▃▄▅▆▇█"
_SPARK_ASCII = "_.-=+*#@"
_BLOCKS = " ▏▎▍▌▋▊▉"
_HISTORY = 240
_BAR = 20
_LEVELS = {"DEBUG": 10, "INFO": 20, "WARNING": 30, "ERROR": 40, "CRITICAL": 50}
_TEXT_LINE = re.compile(r"^\S+ (\S+?)(?:,\d+)? (\w{8}) (\d+) (\w+)\s+(\S+): (.*)$")

HELP = [
    "q        quit",
    "p        pause or resume sampling",
    "+ / -    sample faster or slower",
    "v        show debug lines in the log panel",
    "c        clear alerts",
    "?        show or hide this help",
]


# --- data ---------------------------------------------------------------

@dataclass
class LogEntry:
    time: str
    level: str
    logger: str
    message: str
    run: str = ""


class LogTail:
    """Follow the log file like `tail -f`, surviving rotation."""

    def __init__(self, path: Path | None, keep: int = 200, start_bytes: int = 64_000):
        self.path = path
        self.entries: deque[LogEntry] = deque(maxlen=keep)
        self._start_bytes = start_bytes
        self._pos: int | None = None
        self._inode: int | None = None
        self._partial = b""

    def poll(self) -> None:
        if not self.path:
            return
        try:
            st = self.path.stat()
        except OSError:
            return
        skip_first = False
        if self._pos is None:
            self._pos = max(0, st.st_size - self._start_bytes)
            self._inode = st.st_ino
            skip_first = self._pos > 0  # started mid-file: first line is cut off
        elif st.st_ino != self._inode or st.st_size < self._pos:
            # Rotated or truncated: the new file starts from scratch.
            self._pos, self._inode, self._partial = 0, st.st_ino, b""
        if st.st_size == self._pos:
            return
        try:
            # Binary, because text-mode seek only accepts offsets from tell().
            with open(self.path, "rb") as f:
                f.seek(self._pos)
                chunk = f.read()
        except OSError:
            return
        self._pos += len(chunk)
        lines = (self._partial + chunk).split(b"\n")
        self._partial = lines.pop()
        if skip_first:
            lines = lines[1:]
        for raw in lines:
            line = raw.decode("utf-8", errors="replace")
            if line.strip():
                self.entries.append(parse_log_line(line))


def parse_log_line(line: str) -> LogEntry:
    if line.startswith("{"):
        try:
            d = json.loads(line)
            return LogEntry(d.get("time", "")[-8:], d.get("level", "info").upper(),
                            d.get("logger", ""), d.get("msg", ""), d.get("run", ""))
        except ValueError:
            pass
    m = _TEXT_LINE.match(line)
    if m:
        return LogEntry(m[1], m[4], m[5], m[6], m[2])
    # Traceback lines and anything else unrecognised.
    return LogEntry("", "INFO", "", line)


class FirstOnly(logging.Filter):
    """Let each warning through once.

    top samples every second, so a missing nvidia-smi would otherwise write
    the same warning to the log file 3600 times an hour. This sits on the
    handlers: a filter on the logger would never see records from the
    gpubalance.* child loggers.
    """

    def __init__(self):
        super().__init__()
        self.passed: set[str] = set()

    def filter(self, record: logging.LogRecord) -> bool:
        if record.levelno < logging.WARNING:
            return True
        msg = record.getMessage()
        if msg in self.passed:
            return False
        self.passed.add(msg)
        return True


class AlertCounter(logging.Handler):
    """Count warnings by message for the Alerts panel."""

    def __init__(self):
        super().__init__(logging.WARNING)
        self.seen: dict[str, list] = {}  # message -> [count, last time]

    def emit(self, record: logging.LogRecord) -> None:
        entry = self.seen.setdefault(record.getMessage(), [0, 0.0])
        entry[0] += 1
        entry[1] = record.created

    def alerts(self, limit: int = 5) -> list[tuple[str, int, float]]:
        items = sorted(self.seen.items(), key=lambda kv: kv[1][1], reverse=True)
        return [(msg, count, last) for msg, (count, last) in items[:limit]]


class History:
    def __init__(self, keep: int = _HISTORY):
        self.util: dict[str, deque] = {}
        self.vram: dict[str, deque] = {}
        self.keep = keep

    def add(self, loads: dict[str, metrics.Load]) -> None:
        for slot, load in loads.items():
            self.util.setdefault(slot, deque(maxlen=self.keep)).append(
                None if load.util is None else load.util / 100)
            self.vram.setdefault(slot, deque(maxlen=self.keep)).append(load.vram_frac)


@dataclass
class View:
    gpus: list[detect.Gpu]
    loads: dict[str, metrics.Load] = field(default_factory=dict)
    roles: dict[str, str] = field(default_factory=dict)  # slot -> "render, display"
    scores: dict[str, float] = field(default_factory=dict)
    links: dict[str, tuple[str | None, str | None]] = field(default_factory=dict)
    jobs: list[Assignment] = field(default_factory=list)
    alerts: list[tuple[str, int, float]] = field(default_factory=list)
    log: list[LogEntry] = field(default_factory=list)
    log_path: Path | None = None
    history: History = field(default_factory=History)
    interval: float = 1.0
    paused: bool = False
    verbose: bool = False
    show_help: bool = False
    ascii: bool = False
    now: float = field(default_factory=time.time)


def _read(path: Path) -> str | None:
    try:
        return path.read_text().strip()
    except OSError:
        return None


def role_map(gpus: list[detect.Gpu], cfg: config.Config) -> dict[str, str]:
    try:
        roles = policy.assign_roles(gpus, cfg)
    except policy.NoGpuError as exc:
        logger.warning("roles unavailable: %s", exc)
        return {}
    out: dict[str, list[str]] = {}
    for role in policy.ROLES:
        out.setdefault(roles[role].pci_slot, []).append(role)
    return {slot: ", ".join(names) for slot, names in out.items()}


def sample(view: View, cfg: config.Config, state: State, runner=None) -> None:
    kwargs = {"runner": runner} if runner else {}
    view.loads = metrics.sample(view.gpus, samples=1, **kwargs)
    view.history.add(view.loads)
    view.jobs = state.active()
    counts: dict[str, int] = {}
    for job in view.jobs:
        counts[job.gpu] = counts.get(job.gpu, 0) + 1
    view.scores = {s.gpu.pci_slot: s.score
                   for s in policy.score_all(view.gpus, view.loads, counts, cfg)}
    view.links = {g.pci_slot: (_read(g.sysfs / "current_link_width"),
                               _read(g.sysfs / "max_link_width")) for g in view.gpus}
    view.now = time.time()


# --- drawing ------------------------------------------------------------

def _style_for(frac: float | None) -> str:
    if frac is None:
        return "dim"
    return "ok" if frac < 0.5 else "warn" if frac < 0.85 else "bad"


def bar(frac: float | None, width: int = _BAR, ascii: bool = False) -> str:
    if frac is None:
        return "?" * width if ascii else "·" * width
    cells = max(0.0, min(1.0, frac)) * width
    full = int(cells)
    if ascii:
        return ("#" * full).ljust(width, ".")
    part = _BLOCKS[int((cells - full) * 8)] if full < width else ""
    return ("█" * full + part.strip()).ljust(width)


def sparkline(values, width: int, ascii: bool = False) -> str:
    chars = _SPARK_ASCII if ascii else _SPARK
    recent = list(values)[-width:] if width > 0 else []
    out = []
    for v in recent:
        if v is None:
            out.append(" ")
        else:
            out.append(chars[min(len(chars) - 1, int(max(0.0, min(1.0, v)) * len(chars)))])
    return "".join(out).rjust(width)


def _elapsed(seconds: float) -> str:
    s = max(0, int(seconds))
    h, rem = divmod(s, 3600)
    m, s = divmod(rem, 60)
    return f"{h}h{m:02d}m" if h else f"{m}m{s:02d}s"


def _gib(value: int | None) -> str:
    return "?" if value is None else f"{value / 2**30:.1f}"


def fit(line: Line, width: int) -> Line:
    out: Line = []
    left = width
    for text, style in line:
        if left <= 0:
            break
        out.append((text[:left], style))
        left -= len(text[:left])
    return out


def _gpu_lines(view: View, gpu: detect.Gpu, width: int) -> list[Line]:
    slot = gpu.pci_slot
    load = view.loads.get(slot, metrics.Load())
    name = load.name or gpu.pci_id
    cur, top = view.links.get(slot, (None, None))
    link: Segment = ("", "")
    if cur and top:
        narrow = cur.isdigit() and top.isdigit() and int(cur) < int(top)
        link = (f"  PCIe x{cur}/x{top}", "warn" if narrow else "dim")
    score = view.scores.get(slot)
    head: Line = [(gpu.label, "head"), (f"  {name}", ""), (f"  {gpu.driver}", "dim"), link]
    if slot in view.roles:
        head.append((f"  [{view.roles[slot]}]", "key"))
    if score is not None:
        head.append((f"  score {score:.2f}", "dim"))

    spark_w = max(0, width - 2 - 5 - _BAR - 2 - 16 - 2)
    util = None if load.util is None else load.util / 100
    util_txt = "unknown" if util is None else f"{load.util:.0f}%"
    vram = load.vram_frac
    vram_txt = f"{_gib(load.vram_used)}/{_gib(load.vram_total)} GiB"
    return [
        head,
        [("  util ", "dim"), (bar(util, ascii=view.ascii), _style_for(util)),
         (f"  {util_txt:<16}", ""),
         (sparkline(view.history.util.get(slot, ()), spark_w, view.ascii), _style_for(util))],
        [("  vram ", "dim"), (bar(vram, ascii=view.ascii), _style_for(vram)),
         (f"  {vram_txt:<16}", ""),
         (sparkline(view.history.vram.get(slot, ()), spark_w, view.ascii), _style_for(vram))],
    ]


def render(view: View, width: int, height: int) -> list[Line]:
    clock = time.strftime("%H:%M:%S", time.localtime(view.now))
    header: Line = [("gpubalance top", "title")]
    if view.paused:
        # Before the clock, so a narrow terminal still shows it.
        header.append(("  PAUSED", "warn"))
    header.append((f"  {clock}  every {view.interval:.1f}s", "dim"))
    footer: Line = [("q", "key"), (" quit  ", "dim"), ("p", "key"), (" pause  ", "dim"),
                    ("+/-", "key"), (" speed  ", "dim"), ("v", "key"), (" verbose  ", "dim"),
                    ("c", "key"), (" clear alerts  ", "dim"), ("?", "key"), (" help", "dim")]
    body: list[Line] = []

    if view.show_help:
        body.append([("Keys", "head")])
        body += [[(f"  {h}", "")] for h in HELP]
    else:
        if not view.gpus:
            body.append([("No GPUs found.", "bad")])
        for gpu in view.gpus:
            body += _gpu_lines(view, gpu, width)
            body.append([])

        body.append([(f"Jobs ({len(view.jobs)})", "head")])
        if not view.jobs:
            body.append([("  none running under gpubalance", "dim")])
        labels = {g.pci_slot: g.label for g in view.gpus}
        for job in view.jobs:
            body.append([(f"  pid {job.pid:<7}", ""), (f"{labels.get(job.gpu, job.gpu):<22}", "key"),
                         (f"{job.kind:<8}", "dim"), (f"{_elapsed(view.now - job.started):>8}  ", ""),
                         (job.command, "")])

        if view.alerts:
            body.append([])
            body.append([("Alerts", "head")])
            for msg, count, last in view.alerts:
                when = time.strftime("%H:%M:%S", time.localtime(last))
                times = f" x{count}" if count > 1 else ""
                body.append([(f"  {when}{times}  ", "dim"), (msg, "warn")])

        body.append([])
        body.append([("Log", "head"), (f"  {view.log_path or 'file logging is off'}", "dim")])
        floor = 10 if view.verbose else 20
        entries = [e for e in view.log if _LEVELS.get(e.level, 20) >= floor]
        room = max(0, height - 2 - len(body))
        if not entries:
            body.append([("  nothing yet", "dim")])
        for e in entries[-room:] if room else []:
            style = {"WARNING": "warn", "ERROR": "bad", "CRITICAL": "bad", "DEBUG": "dim"}.get(e.level, "")
            short = e.logger.removeprefix("gpubalance.")
            body.append([(f"  {e.time:<8} ", "dim"), (f"{e.level[:4]:<5}", style),
                         (f"{short:<8} ", "dim"), (e.message, style)])

    lines = [header] + body[: max(0, height - 2)]
    lines += [[]] * max(0, height - 1 - len(lines))
    lines.append(footer)
    return [fit(line, width) for line in lines[:height]]


def to_text(lines: list[Line]) -> str:
    return "\n".join("".join(text for text, _ in line).rstrip() for line in lines) + "\n"


# --- terminal -----------------------------------------------------------

class _QuietLogging:
    """Keep the screen clean and the log file small while top runs."""

    def __init__(self):
        self.root = logging.getLogger("gpubalance")
        self.counter = AlertCounter()
        self.first_only = FirstOnly()
        self._levels: list = []

    def __enter__(self) -> AlertCounter:
        self._levels = [(self.root, self.root.level)]
        self.root.setLevel(logging.WARNING)  # skip per-tick debug lines
        for handler in self.root.handlers:
            if type(handler) is logging.StreamHandler:
                self._levels.append((handler, handler.level))
                handler.setLevel(logging.CRITICAL + 1)  # stderr would scramble curses
            else:
                handler.addFilter(self.first_only)
        self.root.addHandler(self.counter)
        return self.counter

    def __exit__(self, *exc) -> None:
        self.root.removeHandler(self.counter)
        for handler in self.root.handlers:
            handler.removeFilter(self.first_only)
        for obj, level in self._levels:
            obj.setLevel(level)


def _init_colors() -> dict[str, int]:
    styles = {"": curses.A_NORMAL, "dim": curses.A_DIM, "head": curses.A_BOLD,
              "title": curses.A_BOLD, "key": curses.A_BOLD, "ok": curses.A_NORMAL,
              "warn": curses.A_BOLD, "bad": curses.A_BOLD | curses.A_REVERSE}
    if not curses.has_colors():
        return styles
    curses.start_color()
    try:
        curses.use_default_colors()
        bg = -1
    except curses.error:
        bg = curses.COLOR_BLACK
    for i, (name, fg) in enumerate((("ok", curses.COLOR_GREEN), ("warn", curses.COLOR_YELLOW),
                                     ("bad", curses.COLOR_RED), ("title", curses.COLOR_CYAN),
                                     ("key", curses.COLOR_CYAN)), start=1):
        curses.init_pair(i, fg, bg)
        styles[name] = curses.color_pair(i) | (curses.A_BOLD if name in ("title", "bad") else 0)
    return styles


def _draw(screen, lines: list[Line], styles: dict[str, int]) -> None:
    screen.erase()
    for y, line in enumerate(lines):
        x = 0
        for text, style in line:
            try:
                screen.addstr(y, x, text, styles.get(style, curses.A_NORMAL))
            except curses.error:
                pass  # writing the bottom-right cell raises; the text still shows
            x += len(text)
    screen.refresh()


def run(gpus: list[detect.Gpu], cfg: config.Config, state: State,
        log_path: Path | None, interval: float = 1.0) -> None:
    locale.setlocale(locale.LC_ALL, "")
    view = View(gpus=gpus, roles=role_map(gpus, cfg), log_path=log_path, interval=interval,
                ascii="utf" not in locale.getpreferredencoding(False).lower())
    tail = LogTail(log_path)
    logger.info("top started, interval %.1fs", interval)
    quiet = _QuietLogging()

    def loop(screen) -> None:
        curses.curs_set(0)
        screen.timeout(100)
        styles = _init_colors()
        next_sample = 0.0
        while True:
            if not view.paused and time.monotonic() >= next_sample:
                sample(view, cfg, state)
                next_sample = time.monotonic() + view.interval
            tail.poll()
            view.log = list(tail.entries)
            view.alerts = counter.alerts()
            height, width = screen.getmaxyx()
            _draw(screen, render(view, width, height), styles)
            key = screen.getch()
            if key in (ord("q"), ord("Q"), 27):
                return
            if key == ord("p"):
                view.paused = not view.paused
            elif key in (ord("+"), ord("=")):
                view.interval = max(0.2, round(view.interval / 2, 1))
                next_sample = 0.0
            elif key in (ord("-"), ord("_")):
                view.interval = min(30.0, view.interval * 2)
            elif key == ord("v"):
                view.verbose = not view.verbose
            elif key == ord("c"):
                counter.seen.clear()
            elif key == ord("?"):
                view.show_help = not view.show_help

    with quiet as counter:
        try:
            curses.wrapper(loop)
        except KeyboardInterrupt:
            pass
    logger.info("top stopped")


def once(gpus: list[detect.Gpu], cfg: config.Config, state: State, log_path: Path | None,
         width: int, height: int, runner=None) -> str:
    """One frame as plain text, for scripts, tests and terminals without curses."""
    view = View(gpus=gpus, roles=role_map(gpus, cfg), log_path=log_path,
                ascii=os.environ.get("GPUBALANCE_ASCII") == "1")
    sample(view, cfg, state, runner)
    tail = LogTail(log_path)
    tail.poll()
    view.log = list(tail.entries)
    return to_text(render(view, width, height))
