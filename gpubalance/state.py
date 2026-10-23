"""Remember which GPU each launched program was given.

Utilization counters lag by a second or more. Without this file, five
programs started back to back would all see the same idle GPU and pile
onto it. Entries for processes that have exited are dropped on every read.
"""

from __future__ import annotations

import json
import logging
import time
from contextlib import contextmanager
from dataclasses import asdict, dataclass
from pathlib import Path

from gpubalance import system

log = logging.getLogger(__name__)


def default_path() -> Path:
    return system.runtime_dir() / "assignments.json"


@dataclass
class Assignment:
    pid: int
    gpu: str
    kind: str
    command: str
    started: float


class State:
    def __init__(self, path: Path | None = None):
        self.path = path or default_path()

    @contextmanager
    def _locked(self):
        with system.file_lock(self.path.with_suffix(".lock")):
            yield

    def _load(self) -> list[Assignment]:
        try:
            raw = json.loads(self.path.read_text())
        except FileNotFoundError:
            return []
        except (OSError, ValueError) as exc:
            log.warning("job file %s unreadable, starting empty: %s", self.path, exc)
            return []
        items = []
        for item in raw:
            try:
                items.append(Assignment(**item))
            except TypeError:
                log.warning("dropping malformed job entry: %r", item)
        alive = []
        for a in items:
            if system.pid_alive(a.pid):
                alive.append(a)
            else:
                log.debug("job pid %d on %s has exited (%s)", a.pid, a.gpu, a.command)
        return alive

    def _save(self, items: list[Assignment]) -> None:
        tmp = self.path.with_suffix(".tmp")
        tmp.write_text(json.dumps([asdict(a) for a in items], indent=2))
        tmp.replace(self.path)

    def active(self) -> list[Assignment]:
        with self._locked():
            items = self._load()
            self._save(items)
            return items

    def add(self, pid: int, gpu: str, kind: str, command: str) -> None:
        with self._locked():
            items = [a for a in self._load() if a.pid != pid]
            items.append(Assignment(pid, gpu, kind, command, time.time()))
            self._save(items)
        log.info("recorded pid %d on %s as %s: %s", pid, gpu, kind, command)

    def counts(self) -> dict[str, int]:
        counts: dict[str, int] = {}
        for a in self.active():
            counts[a.gpu] = counts.get(a.gpu, 0) + 1
        return counts
