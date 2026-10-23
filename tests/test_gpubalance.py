import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from fakesys import make_mixed, nvidia_smi, plug_monitor  # noqa: E402
from gpubalance import config, detect, launch, metrics, policy  # noqa: E402
from gpubalance.state import State  # noqa: E402


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        self.sys = make_mixed(self.root / "sys")
        self.gpus = detect.discover(self.sys)
        self.nv, self.amd = self.gpus

    def tearDown(self):
        self.tmp.cleanup()


class DetectTest(Base):
    def test_finds_both_cards_and_skips_connectors(self):
        self.assertEqual([g.vendor for g in self.gpus], ["nvidia", "amd"])
        self.assertEqual(self.amd.pci_slot, "0000:03:00.0")
        self.assertEqual(self.amd.pci_id, "1002:744c")
        self.assertEqual(self.amd.render_node, "/dev/dri/renderD129")


class MetricsTest(Base):
    def test_amd_from_sysfs_and_nvidia_from_smi(self):
        loads = metrics.sample(self.gpus, samples=1, runner=nvidia_smi(util=80))
        self.assertEqual(loads["0000:03:00.0"].util, 10)
        self.assertAlmostEqual(loads["0000:03:00.0"].vram_frac, 0.125)
        nv = loads["0000:01:00.0"]
        self.assertEqual(nv.util, 80)
        self.assertEqual(nv.uuid, "GPU-1111-2222")

    def test_missing_nvidia_smi_gives_unknown(self):
        def broken(cmd):
            raise FileNotFoundError(cmd[0])
        loads = metrics.sample(self.gpus, samples=1, runner=broken)
        self.assertIsNone(loads["0000:01:00.0"].util)

    def test_na_fields_do_not_crash(self):
        def runner(cmd):
            return "00000000:01:00.0, GPU-x, [N/A], 100, 1000, 50, [N/A], [N/A], Odd, Name\n"
        loads = metrics.read_nvidia(runner)
        self.assertIsNone(loads["0000:01:00.0"].util)
        self.assertEqual(loads["0000:01:00.0"].name, "Odd, Name")


class PolicyTest(Base):
    def loads(self, nv_util):
        return metrics.sample(self.gpus, samples=1, runner=nvidia_smi(util=nv_util))

    def test_picks_less_busy_gpu(self):
        cfg = config.Config()
        self.assertEqual(policy.choose(self.gpus, self.loads(80), {}, cfg).gpu.vendor, "amd")
        # NVIDIA idle, but with far more VRAM in use than AMD.
        loads = self.loads(0)
        self.assertEqual(policy.choose(self.gpus, loads, {}, cfg).gpu.vendor, "amd")

    def test_running_jobs_push_work_to_other_gpu(self):
        cfg = config.Config()
        jobs = {"0000:03:00.0": 5}
        self.assertEqual(policy.choose(self.gpus, self.loads(30), jobs, cfg).gpu.vendor, "nvidia")

    def test_capacity_favours_stronger_card(self):
        cfg = config.Config(gpus={"0000:01:00.0": config.GpuPrefs(capacity=10)})
        self.assertEqual(policy.choose(self.gpus, self.loads(40), {}, cfg).gpu.vendor, "nvidia")

    def test_target_and_allow_lists(self):
        cfg = config.Config(gpus={"0000:03:00.0": config.GpuPrefs(allow=("graphics",))})
        loads = self.loads(90)
        self.assertEqual(policy.choose(self.gpus, loads, {}, cfg, "compute").gpu.vendor, "nvidia")
        self.assertEqual(policy.choose(self.gpus, loads, {}, cfg, target="nvidia").gpu.vendor, "nvidia")
        with self.assertRaises(policy.NoGpuError):
            policy.choose(self.gpus, loads, {}, cfg, target="intel")


class LaunchTest(Base):
    def test_env_for_amd(self):
        env = launch.build_env(self.amd, self.gpus, "auto")
        self.assertEqual(env["DRI_PRIME"], "pci-0000_03_00_0")
        self.assertEqual(env["MESA_VK_DEVICE_SELECT"], "1002:744c")
        self.assertEqual(env["ROCR_VISIBLE_DEVICES"], "GPU-abcdef0123456789")
        self.assertNotIn("CUDA_VISIBLE_DEVICES", env)

    def test_env_for_nvidia(self):
        load = metrics.Load(uuid="GPU-1111-2222")
        env = launch.build_env(self.nv, self.gpus, "auto", load)
        self.assertEqual(env["__NV_PRIME_RENDER_OFFLOAD"], "1")
        self.assertEqual(env["CUDA_VISIBLE_DEVICES"], "GPU-1111-2222")
        self.assertNotIn("DRI_PRIME", env)

    def test_kind_limits_variables(self):
        env = launch.build_env(self.nv, self.gpus, "compute")
        self.assertNotIn("__NV_PRIME_RENDER_OFFLOAD", env)
        self.assertEqual(env["CUDA_VISIBLE_DEVICES"], "0")


class RolesTest(Base):
    def test_monitor_card_displays_and_other_card_renders(self):
        plug_monitor(self.sys, "card1-DP-1")
        gpus = detect.discover(self.sys)
        self.assertEqual([g.displays for g in gpus], [0, 1])
        roles = policy.assign_roles(gpus, config.Config())
        self.assertEqual(roles["display"].vendor, "amd")
        self.assertEqual(roles["render"].vendor, "nvidia")

    def test_disconnected_connectors_do_not_count(self):
        conn = self.sys / "class" / "drm" / "card0-HDMI-A-1"
        conn.mkdir()
        (conn / "status").write_text("disconnected\n")
        self.assertEqual(detect.discover(self.sys)[0].displays, 0)

    def test_config_overrides_roles(self):
        plug_monitor(self.sys, "card1-DP-1")
        cfg = config.Config(render="amd", display="nvidia")
        roles = policy.assign_roles(detect.discover(self.sys), cfg)
        self.assertEqual(roles["render"].vendor, "amd")
        self.assertEqual(roles["display"].vendor, "nvidia")

    def test_monitor_on_strongest_card(self):
        # RTX 5080 drives the monitor and renders; the spare card gets the rest.
        plug_monitor(self.sys, "card0-DP-1")
        cfg = config.Config(render="0000:01:00.0")
        roles = policy.assign_roles(detect.discover(self.sys), cfg)
        self.assertIs(roles["render"], roles["display"])
        self.assertEqual(roles["side"].vendor, "amd")

    def test_ambiguous_vendor_is_refused(self):
        with self.assertRaises(policy.NoGpuError):
            policy.assign_roles([self.nv, self.nv], config.Config(render="nvidia"))

    def test_single_gpu_does_both(self):
        roles = policy.assign_roles([self.amd], config.Config())
        self.assertIs(roles["render"], roles["display"])
        self.assertIs(roles["side"], self.amd)


class StateTest(Base):
    def test_dead_pids_are_pruned(self):
        state = State(self.root / "state.json")
        dead = subprocess.Popen(["true"])
        dead.wait()
        state.add(os.getpid(), "0000:03:00.0", "auto", "me")
        state.add(dead.pid, "0000:03:00.0", "auto", "gone")
        self.assertEqual(state.counts(), {"0000:03:00.0": 1})


class ConfigTest(unittest.TestCase):
    def test_parses_toml(self):
        with tempfile.NamedTemporaryFile("w", suffix=".toml", delete=False) as f:
            f.write('[weights]\njobs = 1.0\n[gpu."0000:01:00.0"]\ncapacity = 1.8\n'
                    '[pin]\nblender = "nvidia"\n[roles]\nrender = "nvidia"\n')
        try:
            cfg = config.load(Path(f.name))
        finally:
            os.unlink(f.name)
        self.assertEqual(cfg.w_jobs, 1.0)
        self.assertEqual(cfg.prefs("0000:01:00.0").capacity, 1.8)
        self.assertEqual(cfg.pins["blender"], "nvidia")
        self.assertEqual(cfg.render, "nvidia")
        self.assertIsNone(cfg.display)


class CliTest(Base):
    def run_cli(self, *args):
        cmd = [sys.executable, "-m", "gpubalance", "--sysfs", str(self.sys),
               "--state", str(self.root / "s.json"), "--config", str(self.root / "none.toml"),
               "--log-file", str(self.root / "gpubalance.log"), *args]
        return subprocess.run(cmd, capture_output=True, text=True,
                              cwd=Path(__file__).resolve().parent.parent)

    def test_list_and_dry_run(self):
        out = self.run_cli("list")
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertIn("amdgpu", out.stdout)
        # No nvidia-smi here, so NVIDIA reads as 50% and AMD (10%) wins.
        out = self.run_cli("run", "-n", "--", "glxgears")
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertIn("amd@0000:03:00.0", out.stderr)

    def test_run_passes_env_to_child(self):
        out = self.run_cli("run", "-g", "amd", "--", "sh", "-c", "echo $DRI_PRIME")
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertEqual(out.stdout.strip(), "pci-0000_03_00_0")

    def test_game_and_side_split_cards(self):
        plug_monitor(self.sys, "card1-DP-1")
        out = self.run_cli("roles")
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertIn("render   nvidia@0000:01:00.0", out.stdout)
        out = self.run_cli("game", "--", "sh", "-c", "echo $__NV_PRIME_RENDER_OFFLOAD")
        self.assertEqual(out.stdout.strip(), "1", out.stderr)
        out = self.run_cli("side", "--", "sh", "-c", "echo $DRI_PRIME")
        self.assertEqual(out.stdout.strip(), "pci-0000_03_00_0", out.stderr)


if __name__ == "__main__":
    unittest.main()
