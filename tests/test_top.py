import json
import logging
import os
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from fakesys import make_mixed, nvidia_smi, plug_monitor  # noqa: E402
from gpubalance import config, detect, top  # noqa: E402
from gpubalance.state import Assignment, State  # noqa: E402

REPO = Path(__file__).resolve().parent.parent


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.sys = make_mixed(self.root / "sys")
        self.gpus = detect.discover(self.sys)

    def tearDown(self):
        self.tmp.cleanup()


class DrawingTest(unittest.TestCase):
    def test_bar(self):
        self.assertEqual(top.bar(0.5, 10), "█████     ")
        self.assertEqual(top.bar(1.2, 4), "████")
        self.assertEqual(top.bar(0.06, 10), "▌         ")
        self.assertEqual(top.bar(None, 3), "···")
        self.assertEqual(top.bar(0.5, 4, ascii=True), "##..")

    def test_sparkline(self):
        self.assertEqual(top.sparkline([0, 0.5, 1.0, None], 4), "▁▅█ ")
        self.assertEqual(top.sparkline([1.0], 3), "  █")
        self.assertEqual(top.sparkline([0.0, 1.0], 2, ascii=True), "_@")

    def test_fit_cuts_at_width(self):
        line = [("abcd", "x"), ("efgh", "y")]
        self.assertEqual(top.fit(line, 6), [("abcd", "x"), ("ef", "y")])

    def test_parse_text_and_json(self):
        e = top.parse_log_line("2026-10-23 12:35:22,967 32f3a5f0 1942 WARNING "
                               "gpubalance.metrics: nvidia-smi unavailable")
        self.assertEqual((e.time, e.level, e.logger, e.run), ("12:35:22", "WARNING",
                                                             "gpubalance.metrics", "32f3a5f0"))
        self.assertEqual(e.message, "nvidia-smi unavailable")
        j = top.parse_log_line(json.dumps({"time": "2026-10-23T12:00:01", "level": "info",
                                           "logger": "gpubalance.cli", "msg": "hi", "run": "ab"}))
        self.assertEqual((j.time, j.level, j.message), ("12:00:01", "INFO", "hi"))
        self.assertEqual(top.parse_log_line("Traceback (most recent call last):").level, "INFO")


class LogTailTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.path = Path(self.tmp.name) / "g.log"

    def tearDown(self):
        self.tmp.cleanup()

    def line(self, n):
        return f"2026-10-23 12:00:{n:02d},000 aaaaaaaa 1 INFO    gpubalance.cli: line {n}\n"

    def test_follows_new_lines_and_partial_writes(self):
        self.path.write_text(self.line(1))
        tail = top.LogTail(self.path)
        tail.poll()
        with open(self.path, "a") as f:
            f.write(self.line(2) + self.line(3)[:20])
        tail.poll()
        self.assertEqual([e.message for e in tail.entries], ["line 1", "line 2"])
        with open(self.path, "a") as f:
            f.write(self.line(3)[20:])
        tail.poll()
        self.assertEqual(tail.entries[-1].message, "line 3")

    def test_starts_near_end_and_skips_cut_line(self):
        self.path.write_text("".join(self.line(n) for n in range(50)))
        tail = top.LogTail(self.path, start_bytes=200)
        tail.poll()
        messages = [e.message for e in tail.entries]
        self.assertEqual(messages[-1], "line 49")
        self.assertTrue(all(m.startswith("line ") for m in messages))

    def test_survives_rotation(self):
        self.path.write_text(self.line(1) + self.line(2))
        tail = top.LogTail(self.path)
        tail.poll()
        self.path.rename(self.path.with_suffix(".log.1"))
        self.path.write_text(self.line(9))
        tail.poll()
        self.assertEqual(tail.entries[-1].message, "line 9")


class QuietLoggingTest(unittest.TestCase):
    def test_child_logger_warnings_reach_file_once_and_are_counted(self):
        # Regression: a logger-level filter misses records from child loggers.
        root = logging.getLogger("gpubalance")
        saved = (root.level, list(root.handlers))
        written = []

        class Capture(logging.Handler):
            def emit(self, record):
                written.append(record.getMessage())

        for h in list(root.handlers):
            root.removeHandler(h)
        root.addHandler(Capture())
        try:
            with top._QuietLogging() as counter:
                child = logging.getLogger("gpubalance.metrics")
                for _ in range(3):
                    child.warning("nvidia-smi unavailable")
                child.debug("tick detail")
        finally:
            for h in list(root.handlers):
                root.removeHandler(h)
            root.setLevel(saved[0])
            for h in saved[1]:
                root.addHandler(h)
        self.assertEqual(written, ["nvidia-smi unavailable"])
        (msg, count, _), = counter.alerts()
        self.assertEqual((msg, count), ("nvidia-smi unavailable", 3))


class RenderTest(Base):
    def view(self, **kw):
        cfg = config.Config()
        v = top.View(gpus=self.gpus, roles=top.role_map(self.gpus, cfg), **kw)
        top.sample(v, cfg, State(self.root / "s.json"), nvidia_smi(util=40, name="NVIDIA GeForce RTX 5080"))
        return v

    def text(self, v, width=100, height=40):
        lines = top.render(v, width, height)
        self.assertEqual(len(lines), height)
        self.assertTrue(all(sum(len(t) for t, _ in line) <= width for line in lines))
        return top.to_text(lines)

    def test_cards_roles_and_link(self):
        plug_monitor(self.sys, "card1-DP-1")
        self.gpus = detect.discover(self.sys)
        dev = self.sys / "devices" / "0000:01:00.0"
        (dev / "current_link_width").write_text("4\n")
        (dev / "max_link_width").write_text("16\n")
        out = self.text(self.view())
        self.assertIn("nvidia@0000:01:00.0  NVIDIA GeForce RTX 5080", out)
        self.assertIn("PCIe x4/x16", out)
        self.assertIn("[render]", out)
        self.assertIn("[display, side]", out)
        self.assertIn("40%", out)
        self.assertIn("11.7/24.0 GiB", out)

    def test_jobs_alerts_and_log(self):
        v = self.view()
        v.jobs = [Assignment(4211, "0000:01:00.0", "game", "steam", time.time() - 125)]
        v.now = time.time()
        v.alerts = [("nvidia-smi unavailable", 4, time.time())]
        v.log = [top.LogEntry("12:00:00", "INFO", "gpubalance.cli", "hello"),
                 top.LogEntry("12:00:01", "DEBUG", "gpubalance.detect", "hidden detail")]
        out = self.text(v)
        self.assertIn("pid 4211", out)
        self.assertIn("2m05s", out)
        self.assertIn("x4  nvidia-smi unavailable", out)
        self.assertIn("hello", out)
        self.assertNotIn("hidden detail", out)
        v.verbose = True
        self.assertIn("hidden detail", self.text(v))

    def test_small_terminal_and_help(self):
        v = self.view(paused=True)
        out = self.text(v, width=40, height=6)
        self.assertIn("PAUSED", out)
        self.assertTrue(out.rstrip().splitlines()[-1].startswith("q quit  p pause"))
        v.show_help = True
        self.assertIn("pause or resume", self.text(v))


class CliTest(Base):
    def run_cli(self, *args):
        cmd = [sys.executable, "-m", "gpubalance", "--sysfs", str(self.sys),
               "--state", str(self.root / "s.json"), "--config", str(self.root / "none.toml"),
               "--log-file", str(self.root / "g.log"), *args]
        env = dict(os.environ, COLUMNS="90", LINES="30")
        return subprocess.run(cmd, capture_output=True, text=True, cwd=REPO, env=env,
                              stdin=subprocess.DEVNULL)

    def test_once(self):
        out = self.run_cli("top", "--once")
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertIn("gpubalance top", out.stdout)
        self.assertIn("amd@0000:03:00.0", out.stdout)
        self.assertEqual(len(out.stdout.splitlines()), 30)

    def test_needs_terminal(self):
        out = self.run_cli("top")
        self.assertEqual(out.returncode, 2)
        self.assertIn("--once", out.stderr)


if __name__ == "__main__":
    unittest.main()
