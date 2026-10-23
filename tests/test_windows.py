"""Windows code paths, run on Linux by forcing system.WINDOWS."""

import json
import logging
import os
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import fakewin  # noqa: E402
from gpubalance import config, detect, diagnose, launch, metrics, system, top  # noqa: E402
from gpubalance.state import State  # noqa: E402

REPO = Path(__file__).resolve().parent.parent


class WindowsBase(unittest.TestCase):
    def setUp(self):
        patcher = mock.patch.object(system, "WINDOWS", True)
        patcher.start()
        self.addCleanup(patcher.stop)
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)


class DetectTest(WindowsBase):
    def test_two_nvidia_cards_and_an_igpu(self):
        gpus = detect.discover(runner=fakewin.runner())
        self.assertEqual([g.pci_slot for g in gpus], ["0000:01:00.0", "0000:21:00.0", "pnp-1002-164e"])
        g5080, g4070, igpu = gpus
        self.assertEqual((g5080.name, g5080.driver, g5080.displays), ("NVIDIA GeForce RTX 5080", "nvidia", 1))
        self.assertEqual(g5080.pci_id, "10de:2c02")
        self.assertEqual((g4070.displays, g4070.sysfs), (0, None))
        self.assertEqual((igpu.vendor, igpu.driver), ("amd", "amd-windows"))

    def test_single_adapter_json_object(self):
        one = fakewin.ADAPTERS[0]
        run = fakewin.runner()

        def runner(cmd):
            return json.dumps(one) if cmd[0] == "powershell" else run(cmd)
        gpus = detect.discover(runner=runner)
        self.assertEqual(len(gpus), 2)  # both from nvidia-smi
        self.assertEqual(gpus[0].displays, 1)

    def test_powershell_missing_keeps_nvidia(self):
        gpus = detect.discover(runner=fakewin.runner(powershell_ok=False))
        self.assertEqual([g.name for g in gpus],
                         ["NVIDIA GeForce RTX 5080", "NVIDIA GeForce RTX 4070 Ti SUPER"])

    def test_nvidia_smi_missing(self):
        gpus = detect.discover(runner=fakewin.runner(nvidia_ok=False))
        nvidia = [g for g in gpus if g.vendor == "nvidia"]
        self.assertEqual({g.driver for g in nvidia}, {"unknown"})
        self.assertEqual(nvidia[1].pci_slot, "0000:21:00.0")


class LaunchTest(WindowsBase):
    def setUp(self):
        super().setUp()
        self.gpus = detect.discover(runner=fakewin.runner())

    def test_no_graphics_variables(self):
        env = launch.build_env(self.gpus[0], self.gpus, "auto",
                               metrics.Load(uuid="GPU-5080-uuid"))
        self.assertEqual(env, {"CUDA_DEVICE_ORDER": "PCI_BUS_ID",
                               "CUDA_VISIBLE_DEVICES": "GPU-5080-uuid",
                               "GPUBALANCE_GPU": "0000:01:00.0"})

    def test_graphics_request_warns(self):
        with self.assertLogs("gpubalance.launch", "WARNING") as logs:
            launch.build_env(self.gpus[1], self.gpus, "graphics")
        self.assertIn("Settings > System > Display > Graphics", logs.output[0])

    def test_amd_uses_hip(self):
        env = launch.compute_env(self.gpus[2], self.gpus)
        self.assertEqual(env, {"HIP_VISIBLE_DEVICES": "0"})


class ShortHoldHandlerTest(unittest.TestCase):
    def test_file_closed_between_lines_and_rotation_works(self):
        from gpubalance.log import ShortHoldHandler
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "g.log"
            handler = ShortHoldHandler(path, maxBytes=200, backupCount=2, encoding="utf-8", delay=True)
            logger = logging.getLogger("gpubalance.test.shorthold")
            logger.propagate = False
            logger.addHandler(handler)
            try:
                for i in range(20):
                    logger.warning("line %d %s", i, "x" * 30)
                    self.assertIsNone(handler.stream)
            finally:
                logger.removeHandler(handler)
                handler.close()
            self.assertIn("line 19", path.read_text())
            self.assertTrue(path.with_name("g.log.1").exists())


class PathsTest(WindowsBase):
    def test_appdata_paths(self):
        with mock.patch.dict(os.environ, {"APPDATA": r"C:\Users\x\AppData\Roaming",
                                          "LOCALAPPDATA": r"C:\Users\x\AppData\Local"}):
            self.assertEqual(str(config.default_path()),
                             str(Path(r"C:\Users\x\AppData\Roaming") / "gpubalance" / "config.toml"))
            self.assertEqual(system.runtime_dir(), system.state_dir())


class LoadAndTopTest(WindowsBase):
    def test_link_width_from_nvidia_smi(self):
        gpus = detect.discover(runner=fakewin.runner())
        loads = metrics.sample(gpus, samples=1, runner=fakewin.runner())
        self.assertEqual((loads["0000:21:00.0"].link_width, loads["0000:21:00.0"].link_width_max), (4, 16))
        self.assertIsNone(loads["pnp-1002-164e"].util)
        view = top.View(gpus=gpus)
        top.sample(view, config.Config(), State(self.root / "s.json"), fakewin.runner())
        out = top.to_text(top.render(view, 120, 30))
        self.assertIn("NVIDIA GeForce RTX 4070 Ti SUPER", out)
        self.assertIn("PCIe x4/x16", out)
        self.assertIn("AMD Radeon(TM) Graphics", out)


class DiagnoseTest(WindowsBase):
    def test_windows_report_and_checks(self):
        report = diagnose.collect(config_path=self.root / "none.toml",
                                  state_path=self.root / "s.json", runner=fakewin.runner())
        self.assertEqual(report["meta"]["os"], "windows")
        self.assertEqual(len(report["windows_adapters"]), 3)
        checks = {c["id"]: c for c in report["checks"]}
        self.assertEqual(checks["windows-graphics"]["level"], "info")
        self.assertEqual(checks["nvidia-smi"]["level"], "pass")
        self.assertNotIn("modeset", checks)
        self.assertNotIn("vk-device-select", checks)
        widths = [c["message"] for c in report["checks"] if c["id"] == "pcie-width"]
        self.assertTrue(any("x4" in m for m in widths))
        self.assertIn("windows-load", checks)

    def test_redact_windows_paths(self):
        with mock.patch("os.path.expanduser", return_value=r"C:\Users\jeffr"), \
             mock.patch("getpass.getuser", return_value="jeffr"), \
             mock.patch("platform.node", return_value="DESKTOP-ABC"):
            out = diagnose.redact(r'C:\Users\jeffr\AppData "C:\\Users\\jeffr\\x" D:\Users\jeffr desktop-abc')
        self.assertEqual(out, r'~\AppData "~\\x" D:\Users\<user> <host>')


@unittest.skipIf(sys.platform == "win32", "uses POSIX shell scripts as fake tools")
class CliTest(unittest.TestCase):
    """Run the real CLI with the Windows code paths and fake tools on PATH."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        bin_dir = self.root / "bin"
        bin_dir.mkdir()
        (self.root / "adapters.json").write_text(json.dumps(fakewin.ADAPTERS))
        (self.root / "list.txt").write_text(fakewin.NVIDIA_LIST)
        (self.root / "load.txt").write_text(fakewin.NVIDIA_LOAD)
        scripts = {
            "nvidia-smi": f'case "$1" in *pci.device_id*) cat {self.root}/list.txt;; '
                          f'*) cat {self.root}/load.txt;; esac',
            "powershell": f"cat {self.root}/adapters.json",
        }
        for name, body in scripts.items():
            path = bin_dir / name
            path.write_text(f"#!/bin/sh\n{body}\n")
            path.chmod(path.stat().st_mode | stat.S_IEXEC)
        self.env = dict(os.environ, GPUBALANCE_PLATFORM="windows",
                        PATH=f"{bin_dir}{os.pathsep}{os.environ['PATH']}",
                        LOCALAPPDATA=str(self.root / "local"), APPDATA=str(self.root / "roaming"))

    def run_cli(self, *args):
        return subprocess.run([sys.executable, "-m", "gpubalance", *args], capture_output=True,
                              text=True, cwd=REPO, env=self.env, stdin=subprocess.DEVNULL)

    def test_list(self):
        out = self.run_cli("list")
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertIn("NVIDIA GeForce RTX 4070 Ti SUPER", out.stdout)
        self.assertTrue((self.root / "local" / "gpubalance" / "gpubalance.log").exists())

    def test_run_waits_and_passes_exit_code(self):
        out = self.run_cli("run", "-k", "compute", "-g", "0000:21:00.0", "--",
                           "sh", "-c", 'echo "$CUDA_VISIBLE_DEVICES"; echo "pid $$"; exit 3')
        self.assertEqual(out.returncode, 3, out.stderr)
        uuid, pid_line = out.stdout.split("\n")[:2]
        self.assertEqual(uuid, "GPU-4070-uuid")
        log = (self.root / "local" / "gpubalance" / "gpubalance.log").read_text()
        # The child's own PID is recorded, not gpubalance's.
        self.assertIn(f"recorded {pid_line} on 0000:21:00.0", log)
        self.assertIn("exited with 3", log)

    def test_game_explains_windows(self):
        out = self.run_cli("game", "--", "sh", "-c", "exit 0")
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertIn("Settings > System > Display > Graphics", out.stderr)


@unittest.skipUnless(sys.platform == "win32", "real Windows only")
class RealWindowsTest(unittest.TestCase):
    def test_pid_alive_does_not_signal(self):
        proc = subprocess.Popen([sys.executable, "-c", "import time; time.sleep(5)"])
        try:
            self.assertTrue(system.pid_alive(proc.pid))
            self.assertIsNone(proc.poll())  # still running: the probe did not kill it
        finally:
            proc.kill()
            proc.wait()
        self.assertFalse(system.pid_alive(proc.pid))


if __name__ == "__main__":
    unittest.main()
