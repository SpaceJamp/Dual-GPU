"""Everything that differs between Linux and Windows.

Other modules ask this one for paths, file locks, process checks and how
to start a program, so they never branch on the platform themselves.
GPUBALANCE_PLATFORM=windows forces the Windows code paths, which lets the
tests exercise them on Linux.
"""

from __future__ import annotations

import logging
import os
import subprocess
import sys
from contextlib import contextmanager
from pathlib import Path
from typing import Callable

log = logging.getLogger(__name__)

WINDOWS = os.environ.get("GPUBALANCE_PLATFORM", sys.platform).lower().startswith("win")


def _env_dir(var: str, fallback: str) -> Path:
    return Path(os.environ.get(var) or os.path.expanduser(fallback))


def config_dir() -> Path:
    if WINDOWS:
        return _env_dir("APPDATA", "~/AppData/Roaming") / "gpubalance"
    return _env_dir("XDG_CONFIG_HOME", "~/.config") / "gpubalance"


def state_dir() -> Path:
    if WINDOWS:
        return _env_dir("LOCALAPPDATA", "~/AppData/Local") / "gpubalance"
    return _env_dir("XDG_STATE_HOME", "~/.local/state") / "gpubalance"


def runtime_dir() -> Path:
    if WINDOWS:
        return state_dir()
    base = os.environ.get("XDG_RUNTIME_DIR") or f"/tmp/gpubalance-{os.getuid()}"
    return Path(base) / "gpubalance"


@contextmanager
def file_lock(path: Path):
    """Hold an exclusive lock on path for the duration of the block."""
    path.parent.mkdir(parents=True, exist_ok=True)
    with open(path, "a+b") as f:
        if sys.platform == "win32":
            import msvcrt
            f.seek(0)
            # LK_LOCK retries for about 10 seconds before raising.
            msvcrt.locking(f.fileno(), msvcrt.LK_LOCK, 1)
            try:
                yield
            finally:
                f.seek(0)
                msvcrt.locking(f.fileno(), msvcrt.LK_UNLCK, 1)
        else:
            import fcntl
            fcntl.flock(f, fcntl.LOCK_EX)
            try:
                yield
            finally:
                fcntl.flock(f, fcntl.LOCK_UN)


def _windows_alive(pid: int) -> bool:
    # os.kill(pid, 0) is not a probe on Windows: 0 is CTRL_C_EVENT, so it
    # would send Ctrl+C to the process. Ask for its exit code instead.
    import ctypes
    from ctypes import wintypes

    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    STILL_ACTIVE = 259
    ERROR_ACCESS_DENIED = 5
    kernel32 = ctypes.WinDLL("kernel32", use_last_error=True)
    kernel32.OpenProcess.restype = wintypes.HANDLE
    handle = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        # Access denied means it exists but belongs to someone else.
        return ctypes.get_last_error() == ERROR_ACCESS_DENIED
    try:
        code = wintypes.DWORD()
        if not kernel32.GetExitCodeProcess(handle, ctypes.byref(code)):
            return True
        return code.value == STILL_ACTIVE
    finally:
        kernel32.CloseHandle(handle)


def pid_alive(pid: int) -> bool:
    if pid <= 0:
        return False
    if sys.platform == "win32":
        return _windows_alive(pid)
    try:
        os.kill(pid, 0)
    except ProcessLookupError:
        return False
    except PermissionError:
        return True
    return True


def launch(command: list[str], env: dict[str, str],
           on_start: Callable[[int], None]) -> int:
    """Start command with env added, report its PID, return its exit code.

    Linux replaces this process (exec), so the PID gpubalance recorded is
    the program's own and the record disappears when it exits. Windows has
    no real exec, so gpubalance starts the program, waits, and passes its
    exit code through.
    """
    full_env = {**os.environ, **env}
    if not WINDOWS:
        on_start(os.getpid())
        os.execvpe(command[0], command, full_env)
    proc = subprocess.Popen(command, env=full_env)
    on_start(proc.pid)
    log.info("started pid %d, waiting for it to exit", proc.pid)
    while True:
        try:
            code = proc.wait()
            break
        except KeyboardInterrupt:
            # Ctrl+C reaches the child too; wait for it to finish its exit.
            continue
    log.info("pid %d exited with %d", proc.pid, code)
    return code
