"""実ソースの発熱回帰と、ネットワークを除外する計測経路をGUIなしで実行する。"""
from pathlib import Path
import shutil
import subprocess
import sys
import unittest


HARNESS = Path(__file__).with_name("ios-hot-path")


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "macOS Swift host required")
class IOSHeatTests(unittest.TestCase):
    def run_harness(self, script, *arguments):
        result = subprocess.run([sys.executable, str(HARNESS / script), *arguments],
                                capture_output=True, text=True, timeout=180)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        return result.stdout

    def test_health_collection_and_preview_use_production_sources(self):
        output = self.run_harness("heat.py", "--frames", "600")
        self.assertIn("malloc=0 frames=600", output)
        self.assertIn("health collection with 1Hz snapshots malloc=0", output)
        self.assertIn("preview identical-session assignments=0", output)
        self.assertIn("heat regressions: PASS", output)

    def test_no_network_runs_all_six_local_stages(self):
        output = self.run_harness("run.py", "--frames", "1", "--no-network")
        for stage in ["frame-results+expiry", "frame-mailbox+results", "ui-pending",
                      "payload+ASCII", "P2P-encode", "LAN-probe-read"]:
            self.assertIn(stage, output)
        self.assertIn("LAN/P2P NW-send: NOT RUN (--no-network)", output)
        self.assertNotIn("LAN-route+NW-send", output)
        self.assertNotIn("P2P-route+NW-send", output)


if __name__ == "__main__":
    unittest.main()
