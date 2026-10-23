import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from fakesys import make_mixed, nvidia_smi  # noqa: E402
from gpubalance import config, detect, diagnose, metrics, top  # noqa: E402
from gpubalance.state import State  # noqa: E402

REPO = Path(__file__).resolve().parent.parent
AMD = "0000:03:00.0"
NV = "0000:01:00.0"


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.sys = make_mixed(self.root / "sys")
        self.gpus = detect.discover(self.sys)
        self.hwmon = self.sys / "devices" / AMD / "hwmon" / "hwmon3"


class ReadTest(Base):
    def test_nvidia_fields(self):
        load = metrics.read_nvidia(nvidia_smi(temp="71", power="301.25", limit="360.00"))[NV]
        self.assertEqual((load.temp_c, load.power_w, load.power_limit_w), (71, 301.25, 360))
        self.assertAlmostEqual(load.power_frac, 301.25 / 360)

    def test_nvidia_na_power(self):
        load = metrics.read_nvidia(nvidia_smi(power="[N/A]", limit="[N/A]"))[NV]
        self.assertEqual((load.temp_c, load.power_w, load.power_frac), (64, None, None))

    def test_amd_hwmon(self):
        load = metrics.sample(self.gpus, samples=1, runner=nvidia_smi())[AMD]
        self.assertEqual((load.temp_c, load.power_w, load.power_limit_w), (58, 120, 303))

    def test_amd_older_power_file(self):
        (self.hwmon / "power1_input").unlink()
        (self.hwmon / "power1_average").write_text("95500000\n")
        load = metrics.sample(self.gpus, samples=1, runner=nvidia_smi())[AMD]
        self.assertEqual(load.power_w, 95.5)

    def test_amd_without_hwmon(self):
        for f in self.hwmon.iterdir():
            f.unlink()
        load = metrics.sample(self.gpus, samples=1, runner=nvidia_smi())[AMD]
        self.assertEqual((load.temp_c, load.power_w), (None, None))


class TopTest(Base):
    def frame(self, runner, ascii=False):
        view = top.View(gpus=self.gpus, ascii=ascii)
        top.sample(view, config.Config(), State(self.root / "s.json"), runner)
        return top.to_text(top.render(view, 110, 40))

    def test_power_line(self):
        out = self.frame(nvidia_smi(temp="64", power="245.5", limit="450"))
        self.assertIn("powr", out)
        self.assertIn("246/450 W 64°C", out.replace("  ", " "))
        self.assertIn("120/303 W 58°C", out.replace("  ", " "))
        self.assertIn("64C", self.frame(nvidia_smi(temp="64"), ascii=True))

    def test_no_line_without_sensors(self):
        for f in self.hwmon.iterdir():
            f.unlink()
        out = self.frame(nvidia_smi())
        self.assertEqual(out.count("powr"), 1)  # NVIDIA only

    def test_temp_colors(self):
        self.assertEqual([top.temp_style(t) for t in (None, 60, 75, 84, 85, 95)],
                         ["dim", "ok", "warn", "warn", "bad", "bad"])

    def test_power_history(self):
        view = top.View(gpus=self.gpus)
        for power in ("90", "450"):
            top.sample(view, config.Config(), State(self.root / "s.json"),
                       nvidia_smi(power=power, limit="450"))
        self.assertEqual(list(view.history.power[NV]), [0.2, 1.0])


class DiagnoseTest(Base):
    def report(self, **kw):
        return diagnose.collect(sysfs_root=self.sys, root=self.root,
                                config_path=self.root / "none.toml",
                                state_path=self.root / "s.json", runner=nvidia_smi(**kw))

    def test_hot_and_power_limited(self):
        checks = {c["id"]: c for c in self.report(temp="88", power="355", limit="360")["checks"]}
        self.assertIn("88 C", checks["hot"]["message"])
        self.assertEqual(checks["power-limit"]["level"], "info")

    def test_cool_card_has_no_warning(self):
        ids = {c["id"] for c in self.report(temp="60", power="100", limit="360")["checks"]}
        self.assertNotIn("hot", ids)
        self.assertNotIn("power-limit", ids)

    def test_text_report(self):
        text = diagnose.render_text(self.report())
        self.assertIn("temp=58 C power=120/303 W", text)


class StatusTest(Base):
    def test_columns(self):
        cmd = [sys.executable, "-m", "gpubalance", "--sysfs", str(self.sys),
               "--state", str(self.root / "s.json"), "--config", str(self.root / "none.toml"),
               "--log-file", str(self.root / "g.log"), "status"]
        out = subprocess.run(cmd, capture_output=True, text=True, cwd=REPO, env=dict(os.environ))
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertIn("power W", out.stdout)
        amd_line = next(line for line in out.stdout.splitlines() if line.startswith("amd@"))
        self.assertIn(" 58 C", amd_line)
        self.assertIn("120/303", amd_line)


if __name__ == "__main__":
    unittest.main()
