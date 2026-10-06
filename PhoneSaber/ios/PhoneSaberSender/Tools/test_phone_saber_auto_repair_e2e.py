import subprocess
import tempfile
import unittest
from pathlib import Path

import phone_saber_auto_repair_e2e as e2e


class PhoneSaberAutoRepairE2ETests(unittest.TestCase):
    def test_measured_gap_selects_boundary_above_targets(self):
        rows = [
            {"name": name, "color": "BLUE", "truth": "positive",
             "cleanedComponentSizes": [area], "additionalComponentSizes": []}
            for name, area in zip(e2e.TARGETS, (387, 247, 234))
        ]
        rows.append({"name": "other", "color": "BLUE", "truth": "positive",
                     "cleanedComponentSizes": [675], "additionalComponentSizes": []})
        rows.extend({"name": "unused-" + str(index), "color": "RED", "truth": "negative",
                     "cleanedComponentSizes": [1], "additionalComponentSizes": []}
                    for index in range(36))
        cutoff, areas, other_minimum = e2e.select_cutoff(
            {"summary": {"passed": 40}, "fixtures": rows})
        self.assertEqual((cutoff, other_minimum), (388, 675))
        self.assertEqual(areas[e2e.TARGETS[0]], 387)

    def test_no_gap_stops_before_mutation(self):
        rows = [
            {"name": name, "color": "BLUE", "truth": "positive",
             "cleanedComponentSizes": [area], "additionalComponentSizes": []}
            for name, area in zip(e2e.TARGETS, (387, 247, 234))
        ]
        rows.append({"name": "other", "color": "BLUE", "truth": "positive",
                     "cleanedComponentSizes": [300], "additionalComponentSizes": []})
        rows.extend({"name": "unused-" + str(index), "color": "RED", "truth": "negative",
                     "cleanedComponentSizes": [1], "additionalComponentSizes": []}
                    for index in range(36))
        with self.assertRaises(e2e.E2EError):
            e2e.select_cutoff({"summary": {"passed": 40}, "fixtures": rows})

    def test_github_remote_is_rejected_in_temporary_clone(self):
        with tempfile.TemporaryDirectory(prefix="phonesaber-auto-repair-e2e-test-", dir="/tmp") as root:
            clone, bare = Path(root) / "clone", Path(root) / "origin.git"
            subprocess.run(["git", "init", "-q", str(clone)], check=True)
            subprocess.run(["git", "-C", str(clone), "remote", "add", "origin",
                            "https://github.com/example/forbidden.git"], check=True)
            with self.assertRaisesRegex(e2e.E2EError, "GitHub remote"):
                e2e.assert_local_remote(clone, bare)

    def test_explicit_codex_flag_required_before_creating_temp_clone(self):
        script = Path(e2e.__file__)
        result = subprocess.run(["python3", str(script)], text=True,
                                capture_output=True, check=False)
        self.assertEqual(result.returncode, 2)
        self.assertIn("--allow-real-codex is required", result.stderr)


if __name__ == "__main__":
    unittest.main()
