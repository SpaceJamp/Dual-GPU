import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from fakesys import make_mixed, nvidia_smi  # noqa: E402
from gpubalance import config, detect, metrics, policy  # noqa: E402

AMD = "0000:03:00.0"


class Base(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        # AMD is 60% busy, so a cool NVIDIA card normally wins.
        self.sys = make_mixed(self.root / "sys", amd_busy=60)
        self.gpus = detect.discover(self.sys)

    def amd_temp(self, celsius: int) -> None:
        hwmon = self.sys / "devices" / AMD / "hwmon" / "hwmon3"
        (hwmon / "temp1_input").write_text(f"{celsius * 1000}\n")

    def pick(self, nv_temp: str, cfg=None):
        loads = metrics.sample(self.gpus, samples=1, runner=nvidia_smi(util=0, temp=nv_temp))
        return policy.choose(self.gpus, loads, {}, cfg or config.Config())


class PenaltyTest(unittest.TestCase):
    def test_curve(self):
        cfg = config.Config()
        values = [policy.heat_penalty(t, cfg) for t in (None, 40, 75, 80, 85, 99)]
        self.assertEqual(values, [0.0, 0.0, 0.0, 0.5, 1.0, 1.0])

    def test_custom_range(self):
        cfg = config.Config(heat_start_c=60, heat_full_c=70)
        self.assertEqual(policy.heat_penalty(65, cfg), 0.5)


class ChoiceTest(Base):
    def test_cool_nvidia_wins(self):
        self.assertEqual(self.pick("64").gpu.vendor, "nvidia")

    def test_hot_nvidia_loses(self):
        chosen = self.pick("88")
        self.assertEqual(chosen.gpu.vendor, "amd")

    def test_hot_card_is_never_refused(self):
        self.amd_temp(95)
        # Both carry the full penalty, so load decides again.
        chosen = self.pick("88")
        self.assertEqual(chosen.gpu.vendor, "nvidia")
        self.assertEqual(chosen.heat, 1.0)

    def test_cooler_of_two_warm_cards_is_favored(self):
        self.amd_temp(76)
        scores = {s.gpu.vendor: s for s in policy.score_all(
            self.gpus, metrics.sample(self.gpus, samples=1, runner=nvidia_smi(util=0, temp="84")),
            {}, config.Config())}
        self.assertAlmostEqual(scores["nvidia"].heat, 0.9)
        self.assertAlmostEqual(scores["amd"].heat, 0.1)
        self.assertEqual(policy.choose(self.gpus, metrics.sample(
            self.gpus, samples=1, runner=nvidia_smi(util=0, temp="84")), {},
            config.Config()).gpu.vendor, "amd")

    def test_weight_zero_turns_it_off(self):
        self.assertEqual(self.pick("88", config.Config(w_heat=0)).gpu.vendor, "nvidia")

    def test_unknown_temperature_costs_nothing(self):
        chosen = self.pick("[N/A]")
        self.assertEqual((chosen.gpu.vendor, chosen.heat), ("nvidia", 0.0))

    def test_logs_why(self):
        with self.assertLogs("gpubalance.policy", "INFO") as logs:
            self.pick("88")
        self.assertTrue(any("is at 88 C" in line for line in logs.output))


class ConfigTest(unittest.TestCase):
    def load(self, text: str) -> config.Config:
        with tempfile.NamedTemporaryFile("w", suffix=".toml", delete=False) as f:
            f.write(text)
        self.addCleanup(Path(f.name).unlink)
        return config.load(Path(f.name))

    def test_parse(self):
        cfg = self.load("[weights]\nheat = 2.5\n[heat]\nstart_c = 70\nfull_c = 80\n")
        self.assertEqual((cfg.w_heat, cfg.heat_start_c, cfg.heat_full_c), (2.5, 70, 80))

    def test_defaults(self):
        cfg = self.load("")
        self.assertEqual((cfg.w_heat, cfg.heat_start_c, cfg.heat_full_c), (1.0, 75, 85))

    def test_example_config_values_are_read(self):
        # Regression: plain keys placed after a [table] silently land inside it.
        example = Path(__file__).resolve().parent.parent / "examples" / "config.toml"
        cfg = config.load(example)
        self.assertEqual((cfg.unknown_util, cfg.samples), (0.5, 5))
        self.assertEqual((cfg.w_heat, cfg.heat_start_c, cfg.heat_full_c), (1.0, 75, 85))
        self.assertEqual(cfg.prefs("0000:01:00.0").capacity, 1.5)
        self.assertEqual((cfg.render, cfg.display), ("nvidia", "amd"))
        data = __import__("tomllib").loads(example.read_text())
        self.assertNotIn("samples", data["heat"])

    def test_bad_range(self):
        with self.assertRaises(config.ConfigError):
            self.load("[heat]\nstart_c = 85\nfull_c = 80\n")


if __name__ == "__main__":
    unittest.main()
