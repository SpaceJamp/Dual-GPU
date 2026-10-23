import json
import os
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from fakesys import make_mixed, nvidia_smi, plug_monitor  # noqa: E402
from gpubalance import diagnose  # noqa: E402

REPO = Path(__file__).resolve().parent.parent


def set_link(sysfs: Path, slot: str, width: int, max_width: int) -> None:
    dev = sysfs / "devices" / slot
    (dev / "current_link_width").write_text(f"{width}\n")
    (dev / "max_link_width").write_text(f"{max_width}\n")
    (dev / "current_link_speed").write_text("16.0 GT/s PCIe\n")
    (dev / "max_link_speed").write_text("16.0 GT/s PCIe\n")


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.sys = make_mixed(self.root / "sys")
        self.log = self.root / "gpubalance.log"

    def tearDown(self):
        self.tmp.cleanup()

    def run_cli(self, *args, config=None):
        cmd = [sys.executable, "-m", "gpubalance", "--sysfs", str(self.sys),
               "--root", str(self.root), "--state", str(self.root / "s.json"),
               "--config", str(config or self.root / "none.toml"),
               "--log-file", str(self.log), *args]
        env = {k: v for k, v in os.environ.items() if k not in diagnose.STEERING_VARS}
        return subprocess.run(cmd, capture_output=True, text=True, cwd=REPO, env=env)

    def checks(self, report):
        return {c["id"]: c for c in report["checks"]}


class LogFileTest(Base):
    def test_file_gets_debug_lines_with_run_id(self):
        out = self.run_cli("run", "-n", "--", "true")
        self.assertEqual(out.returncode, 0, out.stderr)
        text = self.log.read_text()
        self.assertIn("DEBUG", text)
        self.assertIn("score amd@0000:03:00.0", text)
        self.assertIn("chose amd@0000:03:00.0", text)
        run_ids = {line.split()[2] for line in text.splitlines()}
        self.assertEqual(len(run_ids), 1)
        # Debug detail stays out of the terminal by default.
        self.assertNotIn("score amd", out.stderr)

    def test_json_lines(self):
        out = self.run_cli("--log-json", "list")
        self.assertEqual(out.returncode, 0, out.stderr)
        entries = [json.loads(line) for line in self.log.read_text().splitlines()]
        self.assertTrue(all({"time", "run", "level", "logger", "msg"} <= e.keys() for e in entries))
        self.assertTrue(any(e["msg"].startswith("found card1") for e in entries))

    def test_debug_on_screen(self):
        out = self.run_cli("--log-level", "debug", "list")
        self.assertIn("DEBUG gpubalance.detect: found card0", out.stderr)

    def test_no_log_file(self):
        out = self.run_cli("--no-log-file", "list")
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertFalse(self.log.exists())


class ErrorHandlingTest(Base):
    def test_bad_toml_is_a_clear_error(self):
        cfg = self.root / "bad.toml"
        cfg.write_text("[roles\nrender = 1\n")
        out = self.run_cli("roles", config=cfg)
        self.assertEqual(out.returncode, 1)
        self.assertIn("bad.toml", out.stderr)
        self.assertNotIn("Traceback", out.stderr)

    def test_crash_goes_to_log(self):
        # A directory where the config file should be makes read_text fail.
        out = self.run_cli("roles", config=self.root)
        self.assertEqual(out.returncode, 70)
        self.assertIn("unexpected error: IsADirectoryError", out.stderr)
        self.assertIn(str(self.log), out.stderr)
        self.assertNotIn("Traceback", out.stderr)
        self.assertIn("Traceback", self.log.read_text())


class DiagnoseTest(Base):
    def collect(self, **kw):
        return diagnose.collect(sysfs_root=self.sys, root=self.root,
                                config_path=self.root / "none.toml",
                                state_path=self.root / "s.json",
                                runner=nvidia_smi(), **kw)

    def test_narrow_pcie_link_is_flagged(self):
        set_link(self.sys, "0000:03:00.0", 4, 16)
        set_link(self.sys, "0000:01:00.0", 16, 16)
        checks = [c for c in self.collect()["checks"] if c["id"] == "pcie-width"]
        levels = sorted(c["level"] for c in checks)
        self.assertEqual(levels, ["pass", "warn"])
        self.assertIn("x4", next(c for c in checks if c["level"] == "warn")["message"])

    def test_missing_monitor_and_layer(self):
        checks = self.checks(self.collect())
        self.assertEqual(checks["no-monitor"]["level"], "warn")
        self.assertEqual(checks["vk-device-select"]["level"], "warn")
        layer = self.root / "usr/share/vulkan/implicit_layer.d"
        layer.mkdir(parents=True)
        (layer / "VkLayer_MESA_device_select.json").write_text("{}")
        plug_monitor(self.sys, "card1-DP-1")
        checks = self.checks(self.collect())
        self.assertNotIn("no-monitor", checks)
        self.assertNotIn("vk-device-select", checks)

    def test_nouveau_and_modeset(self):
        report = self.collect()
        checks = self.checks(report)
        self.assertEqual(checks["modeset"]["level"], "warn")
        report["gpus"][0]["driver"] = "nouveau"
        ids = {c.id for c in diagnose.run_checks(report)}
        self.assertIn("nouveau", ids)
        self.assertNotIn("modeset", ids)

    def test_steering_env_and_redaction(self):
        os.environ["DRI_PRIME"] = "1"
        try:
            checks = self.checks(self.collect())
        finally:
            del os.environ["DRI_PRIME"]
        self.assertIn("DRI_PRIME", checks["steering-env"]["message"])
        home = os.path.expanduser("~")
        self.assertEqual(diagnose.redact(f"{home}/x"), "~/x")
        user = __import__("getpass").getuser()
        self.assertEqual(diagnose.redact(f"--{user} /run/user/{user}/a"),
                         f"--{user} /run/user/<user>/a")

    def test_cli_text_and_json(self):
        set_link(self.sys, "0000:03:00.0", 8, 16)
        out = self.run_cli("diagnose")
        self.assertIn("[WARN] amd@0000:03:00.0 runs at PCIe x8", out.stdout)
        self.assertIn("Last log lines", out.stdout)
        report_file = self.root / "report.json"
        out = self.run_cli("diagnose", "--json", "-o", str(report_file))
        report = json.loads(report_file.read_text())
        self.assertEqual(len(report["gpus"]), 2)
        self.assertIn("render", report["roles"])

    def test_cli_fails_on_failed_check(self):
        cfg = self.root / "bad.toml"
        cfg.write_text("[roles]\nrender = 'nvidia'\nrender = 'amd'\n")
        out = self.run_cli("diagnose", config=cfg)
        self.assertEqual(out.returncode, 1)
        self.assertIn("[FAIL] Config error", out.stdout)

    def test_llama_view(self):
        fake = self.root / "llama-server"
        fake.write_text("#!/bin/sh\ncat <<EOT\nAvailable devices:\n"
                        "  CUDA0: NVIDIA GeForce RTX 4090 (24080 MiB, 22528 MiB free)\nEOT\n")
        fake.chmod(fake.stat().st_mode | stat.S_IEXEC)
        checks = self.checks(self.collect(llama_binary=str(fake)))
        self.assertEqual(checks["llama-missing"]["level"], "warn")
        self.assertIn("amd@0000:03:00.0", checks["llama-missing"]["message"])


if __name__ == "__main__":
    unittest.main()
