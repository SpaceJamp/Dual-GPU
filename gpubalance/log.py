"""Logging setup shared by every command.

Two outputs:
  console  warnings and errors by default, more with --log-level
  file     always DEBUG, rotated, so a bug can be traced after the fact

Each process gets a short run ID. Every line carries it, so one launch can
be pulled out of the log with grep even when several ran at once.
"""

from __future__ import annotations

import json
import logging
import logging.handlers
import os
import time
import uuid
from contextlib import contextmanager
from pathlib import Path

ROOT = "gpubalance"
LEVELS = ("debug", "info", "warning", "error")
RUN_ID = uuid.uuid4().hex[:8]

_FILE_BYTES = 1_000_000
_FILE_BACKUPS = 3
_TEXT_FORMAT = "%(asctime)s %(run_id)s %(process)d %(levelname)-7s %(name)s: %(message)s"


def default_path() -> Path:
    base = os.environ.get("XDG_STATE_HOME") or os.path.expanduser("~/.local/state")
    return Path(base) / "gpubalance" / "gpubalance.log"


class _RunIdFilter(logging.Filter):
    def filter(self, record: logging.LogRecord) -> bool:
        record.run_id = RUN_ID
        return True


class JsonFormatter(logging.Formatter):
    """One JSON object per line. Extra fields passed with extra={"data": {...}}
    land under "data" so they stay machine readable."""

    def format(self, record: logging.LogRecord) -> str:
        entry = {
            "time": self.formatTime(record, "%Y-%m-%dT%H:%M:%S"),
            "ms": int(record.msecs),
            "run": RUN_ID,
            "pid": record.process,
            "level": record.levelname.lower(),
            "logger": record.name,
            "msg": record.getMessage(),
        }
        data = getattr(record, "data", None)
        if data is not None:
            entry["data"] = data
        if record.exc_info:
            entry["traceback"] = self.formatException(record.exc_info)
        return json.dumps(entry, default=str)


def setup(
    console_level: str = "warning",
    file_path: Path | None = None,
    use_file: bool = True,
    json_file: bool = False,
) -> Path | None:
    """Configure the gpubalance logger. Returns the log file path, or None
    when file logging is off or the file cannot be opened."""
    logger = logging.getLogger(ROOT)
    logger.setLevel(logging.DEBUG)
    logger.propagate = False
    for handler in list(logger.handlers):
        logger.removeHandler(handler)
        handler.close()

    console = logging.StreamHandler()
    console.setLevel(console_level.upper())
    # Plain messages for normal use; level and module once you ask for detail.
    detailed = console_level.lower() in ("debug", "info")
    console.setFormatter(logging.Formatter(
        "gpubalance %(levelname)s %(name)s: %(message)s" if detailed else "gpubalance: %(message)s"))
    logger.addHandler(console)

    path = None
    if use_file:
        path = file_path or default_path()
        try:
            path.parent.mkdir(parents=True, exist_ok=True)
            handler = logging.handlers.RotatingFileHandler(
                path, maxBytes=_FILE_BYTES, backupCount=_FILE_BACKUPS, encoding="utf-8")
        except OSError as exc:
            logger.warning("cannot write log file %s: %s", path, exc)
            path = None
        else:
            handler.setLevel(logging.DEBUG)
            handler.setFormatter(JsonFormatter() if json_file else logging.Formatter(_TEXT_FORMAT))
            logger.addHandler(handler)

    for handler in logger.handlers:
        handler.addFilter(_RunIdFilter())
    return path


@contextmanager
def timed(logger: logging.Logger, what: str):
    """Log how long a step took, and log it as failed if it raised."""
    start = time.monotonic()
    try:
        yield
    except Exception:
        logger.debug("%s failed after %.1f ms", what, (time.monotonic() - start) * 1000)
        raise
    logger.debug("%s took %.1f ms", what, (time.monotonic() - start) * 1000)


def tail(path: Path, lines: int = 50) -> list[str]:
    try:
        return path.read_text(encoding="utf-8", errors="replace").splitlines()[-lines:]
    except OSError:
        return []
