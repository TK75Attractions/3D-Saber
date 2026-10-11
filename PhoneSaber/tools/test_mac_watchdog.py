"""ゲームを起動せず、bash 3.2 と偽 open で Mac ランチャーを検証する。"""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


SOURCE = Path(__file__).resolve().parents[1] / "mac"


class MacWatchdogTests(unittest.TestCase):
    def setUp(self):
        self.storage = tempfile.TemporaryDirectory(prefix="縁日 watchdog (1) ")
        self.addCleanup(self.storage.cleanup)
        self.root = Path(self.storage.name)
        self.mac = self.root / "PhoneSaber/mac"
        self.mac.mkdir(parents=True)
        self.app = self.root / "Builds/Mac/3D-Saber.app"
        self.app.mkdir(parents=True)
        self.calls = self.root / "open.calls"
        self.arguments = self.root / "open.arguments"
        fake_open = self.root / "fake-open"
        fake_open.write_text('''#!/bin/bash
printf 'launch\\n' >> "$WATCHDOG_TEST_CALLS"
printf '%s\\n' "$@" > "$WATCHDOG_TEST_ARGUMENTS"
while [[ $# -gt 0 ]]; do
    if [[ "$1" == -phonesaberQuitMarker ]]; then
        shift
        touch "$1"
        exit 0
    fi
    shift
done
exit 2
''')
        fake_open.chmod(0o755)
        for source in SOURCE.glob("*.command"):
            (self.mac / source.name).write_text(source.read_text())
        script = (SOURCE / "saber-watchdog.sh").read_text()
        (self.mac / "saber-watchdog.sh").write_text(script.replace("/usr/bin/open", '"' + str(fake_open) + '"'))
        self.env = {**os.environ, "CDPATH": ".", "WATCHDOG_TEST_CALLS": str(self.calls),
                    "WATCHDOG_TEST_ARGUMENTS": str(self.arguments),
                    "PHONESABER_P2P_BRIDGE_SCRIPT": str(self.root / "missing-bridge.py")}

    def run_script(self, name, *args):
        return subprocess.run(["/bin/bash", "PhoneSaber/mac/" + name, *map(str, args)],
                              cwd=self.root, env=self.env, capture_output=True, text=True, timeout=10)

    def test_relative_entrypoints_with_exported_cdpath_launch_once(self):
        for station in ("A", "B"):
            with self.subTest(station=station):
                result = self.run_script(f"Start-Saber-{station}.command")
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                self.assertIn("正常終了", result.stdout)
                self.assertFalse((self.mac / ".saber-watchdog.lock").exists())
        self.assertEqual(self.calls.read_text().splitlines(), ["launch", "launch"])

    def test_relative_watchdog_with_exported_cdpath_keeps_paths_intact(self):
        result = self.run_script("saber-watchdog.sh", "A", self.app)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(self.calls.read_text().splitlines(), ["launch"])
        self.assertIn("clean-quit=yes", (self.mac / "Start-Saber-A.log").read_text())

    def test_relative_bridge_override_with_cdpath_is_passed_as_one_absolute_path(self):
        bridge = self.root / "bridge/script.py"
        bridge.parent.mkdir()
        bridge.touch()
        self.env["PHONESABER_P2P_BRIDGE_SCRIPT"] = "bridge/script.py"
        result = self.run_script("Start-Saber-A.command")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("PHONESABER_P2P_BRIDGE_SCRIPT=" + str(bridge.resolve()), self.arguments.read_text().splitlines())

    def test_rotation_does_not_move_a_log_into_a_generation_directory(self):
        log = self.mac / "Start-Saber-A.log"
        log.write_bytes(b"x" * (1048576 + 1))
        previous = self.mac / "Start-Saber-A.log.1"
        previous.mkdir()
        evidence = previous / log.name
        evidence.write_text("older log")
        result = self.run_script("Start-Saber-A.command")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(evidence.read_text(), "older log")
        self.assertGreater(log.stat().st_size, 1048576)

    def test_stop_file_prevents_launch_and_explains_why(self):
        (self.mac / "STOP").touch()
        result = self.run_script("Start-Saber-A.command")
        self.assertEqual(result.returncode, 1, result.stdout + result.stderr)
        self.assertIn("STOP", result.stderr)
        self.assertFalse(self.calls.exists())

    def test_dead_lock_is_reclaimed_and_log_rotates(self):
        lock = self.mac / ".saber-watchdog.lock"
        lock.mkdir()
        (lock / "pid").write_text("99999999\n")
        log = self.mac / "Start-Saber-A.log"
        log.write_bytes(b"x" * (1048576 + 1))
        result = self.run_script("Start-Saber-A.command")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(log.with_suffix(".log.1").stat().st_size, 1048576 + 1)
        self.assertEqual(self.calls.read_text().splitlines(), ["launch"])


if __name__ == "__main__":
    unittest.main()
