import os
import stat
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
sys.path.insert(0, str(Path(__file__).resolve().parent))

from fakesys import make_card, make_mixed, plug_monitor  # noqa: E402
from gpubalance import detect  # noqa: E402
from gpubalance import llm  # noqa: E402

# A build with CUDA and Vulkan sees the NVIDIA card twice.
LIST = """\
ggml_cuda_init: found 1 CUDA devices:
Available devices:
  CUDA0: NVIDIA GeForce RTX 4090 (24080 MiB, 22528 MiB free)
  Vulkan0: NVIDIA GeForce RTX 4090 (24564 MiB, 22000 MiB free)
  Vulkan1: AMD Radeon RX 7900 XTX (RADV NAVI31) (24560 MiB, 11264 MiB free)
"""


class LlmTest(unittest.TestCase):
    def setUp(self):
        self.devices = llm.parse_devices(LIST)

    def test_parse(self):
        self.assertEqual([d.name for d in self.devices], ["CUDA0", "Vulkan0", "Vulkan1"])
        amd = self.devices[2]
        self.assertEqual(amd.description, "AMD Radeon RX 7900 XTX (RADV NAVI31)")
        self.assertEqual((amd.total_mib, amd.free_mib, amd.vendor), (24560, 11264, "amd"))

    def test_keeps_native_backend_per_card(self):
        self.assertEqual([d.name for d in llm.one_per_card(self.devices)], ["CUDA0", "Vulkan1"])

    def test_split_follows_free_vram(self):
        chosen, shares = llm.plan(self.devices, reserve_mib=1024)
        self.assertEqual([d.name for d in chosen], ["CUDA0", "Vulkan1"])
        self.assertEqual(shares, [21504, 10240])
        args = llm.build_args(["llama-server", "-m", "x.gguf"], chosen, shares)
        self.assertEqual(args[3:], ["--device", "CUDA0,Vulkan1", "--tensor-split", "21504,10240"])

    def test_single_and_skip(self):
        chosen, _ = llm.plan(self.devices, mode="single")
        self.assertEqual([d.name for d in chosen], ["CUDA0"])
        chosen, _ = llm.plan(self.devices, skip=frozenset({"CUDA0"}))
        self.assertEqual([d.name for d in chosen], ["Vulkan1"])
        args = llm.build_args(["llama-cli"], chosen, [1])
        self.assertNotIn("--tensor-split", args)

    def test_user_flags_win(self):
        chosen, shares = llm.plan(self.devices)
        args = llm.build_args(["llama-server", "-ts=1,1", "--device", "Vulkan0"], chosen, shares)
        self.assertEqual(args, ["llama-server", "-ts=1,1", "--device", "Vulkan0"])

    def test_reserve_bigger_than_free(self):
        with self.assertRaises(llm.LlmError):
            llm.plan(self.devices, reserve_mib=30000)


# RTX 5080 plus RTX 4070 Ti SUPER: same vendor, same 16 GB.
TWO_NVIDIA = """\
Available devices:
  CUDA0: NVIDIA GeForce RTX 5080 (16303 MiB, 15000 MiB free)
  CUDA1: NVIDIA GeForce RTX 4070 Ti SUPER (16376 MiB, 16000 MiB free)
  Vulkan0: NVIDIA GeForce RTX 4070 Ti SUPER (16376 MiB, 16000 MiB free)
  Vulkan1: NVIDIA GeForce RTX 5080 (16303 MiB, 15000 MiB free)
"""


class TwoNvidiaTest(unittest.TestCase):
    def setUp(self):
        self.devices = llm.parse_devices(TWO_NVIDIA)

    def test_two_cards_with_same_memory_are_kept(self):
        self.assertEqual([d.name for d in llm.one_per_card(self.devices)], ["CUDA0", "CUDA1"])

    def test_model_names_need_exact_match(self):
        a = llm.LlamaDevice("CUDA0", "NVIDIA GeForce RTX 4070", 12000, 0)
        b = llm.LlamaDevice("Vulkan0", "NVIDIA GeForce RTX 4070 Ti SUPER", 12000, 0)
        c = llm.LlamaDevice("Vulkan1", "AMD Radeon RX 9060 XT (RADV GFX1200)", 0, 0)
        d = llm.LlamaDevice("ROCm0", "AMD Radeon RX 9060 XT", 0, 0)
        self.assertFalse(llm._same_model(a, b))
        self.assertTrue(llm._same_model(c, d))

    def test_cuda_maps_by_pci_order(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            make_card(root, "card0", "0000:01:00.0", "0x10de", "0x2c02", "nvidia", "renderD128")
            make_card(root, "card1", "0000:05:00.0", "0x10de", "0x2705", "nvidia", "renderD129")
            slots = llm.map_slots(self.devices, detect.discover(root))
        self.assertEqual(slots["CUDA0"], "0000:01:00.0")
        self.assertEqual(slots["CUDA1"], "0000:05:00.0")
        self.assertIsNone(slots["Vulkan0"])


class LlmCliTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = Path(self.tmp.name)
        self.root = root
        self.sys = make_mixed(root / "sys")
        plug_monitor(self.sys, "card1-DP-1")
        fake = root / "llama-server"
        fake.write_text("#!/bin/sh\n"
                        'if [ "$1" = "--list-devices" ]; then cat <<EOT\n' + LIST + "EOT\n"
                        'else echo "$@"; fi\n')
        fake.chmod(fake.stat().st_mode | stat.S_IEXEC)
        self.fake = str(fake)

    def tearDown(self):
        self.tmp.cleanup()

    def run_cli(self, *args):
        cmd = [sys.executable, "-m", "gpubalance", "--sysfs", str(self.sys),
               "--state", str(self.root / "s.json"), "--config", str(self.root / "none.toml"), *args]
        return subprocess.run(cmd, capture_output=True, text=True,
                              cwd=Path(__file__).resolve().parent.parent)

    def test_split_passes_flags(self):
        out = self.run_cli("llm", "--", self.fake, "-m", "model.gguf")
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertEqual(out.stdout.strip(),
                         "-m model.gguf --device CUDA0,Vulkan1 --tensor-split 21504,10240")

    def test_leave_game_card(self):
        # Monitor is on the AMD card, so NVIDIA is the game card.
        out = self.run_cli("llm", "--leave-game-card", "--", self.fake, "-m", "m.gguf")
        self.assertEqual(out.returncode, 0, out.stderr)
        self.assertEqual(out.stdout.strip(), "-m m.gguf --device Vulkan1")

    def test_child_gets_pci_order(self):
        fake = Path(self.fake)
        fake.write_text(fake.read_text().replace('echo "$@"', 'echo "$CUDA_DEVICE_ORDER"'))
        out = self.run_cli("llm", "--", self.fake)
        self.assertEqual(out.stdout.strip(), "PCI_BUS_ID", out.stderr)


if __name__ == "__main__":
    unittest.main()
