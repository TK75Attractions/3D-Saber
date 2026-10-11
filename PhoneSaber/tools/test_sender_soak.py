import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import sender_soak  # noqa: E402


class SenderSoakTests(unittest.TestCase):
    def test_slope_is_least_squares_per_minute(self):
        self.assertAlmostEqual(sender_soak.slope([0, 1, 2, 3], [10, 12, 14, 16]), 2.0)
        self.assertEqual(sender_soak.slope([1], [5]), 0.0)
        self.assertEqual(sender_soak.slope([2, 2], [1, 3]), 0.0)

    def test_summary_separates_warmup_from_late_slope(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "run.csv"
            # 最初の5分だけ増え、その後は一定(リークではない立ち上がり)。
            rows = ["t_min,resident_bytes"]
            for index in range(61):
                minute = index / 2
                rows.append(f"{minute},{1000 + min(minute, 5) * 100}")
            path.write_text("\n".join(rows) + "\n", encoding="utf-8")
            summary = sender_soak.summarize(path)
        resident = summary["resident_bytes"]
        self.assertGreater(resident["slope_per_min"], 0)
        self.assertAlmostEqual(resident["slope_per_min_after_warmup"], 0.0)
        self.assertAlmostEqual(resident["start"], 1050.0)
        self.assertAlmostEqual(resident["end"], 1500.0)
        self.assertEqual(summary["_meta"]["samples"], 61)

    def test_summary_rejects_too_few_samples(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "run.csv"
            path.write_text("t_min,resident_bytes\n0,1\n0.1,2\n", encoding="utf-8")
            with self.assertRaises(ValueError):
                sender_soak.summarize(path)

    def test_support_source_still_finds_the_interval_types(self):
        # CameraViewModel.swift の並びが変わったら、harness の build が壊れる前にここで気付く。
        with tempfile.TemporaryDirectory() as directory:
            text = sender_soak.support_source(Path(directory)).read_text(encoding="utf-8")
        self.assertIn("struct CameraFrameIntervalStatistics", text)
        self.assertIn("struct CameraFrameIntervalWindow", text)
        self.assertNotIn("import UIKit", text)
        self.assertTrue(sender_soak.FIXTURE.exists())
        self.assertTrue(sender_soak.HARNESS.exists())


if __name__ == "__main__":
    unittest.main()
