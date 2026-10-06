import json
import unittest
from pathlib import Path

import run_lossless_regression as lossless


class LosslessRegressionTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document, cls.fixtures = lossless.load_fixtures(lossless.DEFAULT_MANIFEST)

    def test_manifest_is_self_contained_and_hash_pinned(self):
        self.assertEqual(len(self.fixtures), 40)
        self.assertEqual(len({fixture["path"] for fixture in self.fixtures}), 35)
        self.assertTrue(all(fixture["resolved_path"].is_relative_to(lossless.FIXTURES_DIR) for fixture in self.fixtures))
        self.assertTrue(all(len(fixture["sha256"]) == 64 for fixture in self.fixtures))
        self.assertFalse(any("optional" in fixture for fixture in self.fixtures))

    def test_summary_counts_each_class_and_both_truth_labels(self):
        rows = [
            {
                "failureClass": fixture["failureClass"],
                "truth": fixture["truth"],
                "path": fixture["path"],
                "passed": True,
                "detected": fixture["expectedDetected"],
            }
            for fixture in self.fixtures
        ]
        summary = lossless.summarize(rows)
        self.assertEqual(set(summary["by_failure_class"]), set("ABCDEFG"))
        self.assertTrue(all(item["fixture_count"] > 0 for item in summary["by_failure_class"].values()))
        self.assertGreater(summary["positive_count"], 0)
        self.assertGreater(summary["negative_count"], 0)

    def test_axis_endpoint_distance_ignores_endpoint_order(self):
        expected = [{"x": 12, "y": 34}, {"x": 56, "y": 78}]
        reversed_actual = [{"x": 56, "y": 78}, {"x": 12, "y": 34}]
        shifted_actual = [{"x": 13, "y": 34}, {"x": 56, "y": 80}]
        self.assertEqual(lossless.endpoint_distance(reversed_actual, expected), 0)
        self.assertAlmostEqual(lossless.endpoint_distance(shifted_actual, expected), 1.5)

    def test_fixture_evaluation_checks_candidate_type_endpoints_and_rejected_proposals(self):
        fixture = {
            "name": "representative-blue-core-line",
            "path": "fixture.png",
            "color": "BLUE",
            "truth": "positive",
            "failureClass": "D",
            "scenario": "raw-tail-control",
            "expectedDetected": True,
            "expectedCandidateType": "core-line",
            "expectedEndpoint": [{"x": 10, "y": 20}, {"x": 40, "y": 22}],
            "endpointTolerancePx": 3,
            "expectedRejectedCandidateTypes": ["core-line-weak-raw-tail"],
        }
        analysis = {
            "colors": {
                "blue": {
                    "selected": [{"x": 11, "y": 20}, {"x": 40, "y": 23}],
                    "candidates": [
                        {"source": "core-line", "eligible": True},
                        {"source": "core-line-weak-raw-tail", "eligible": False},
                    ],
                }
            },
            "profile": {"total_ms": 1.25},
        }
        row = lossless.evaluate_fixture(fixture, analysis)
        self.assertTrue(row["passed"], json.dumps(row, indent=2))
        analysis["colors"]["blue"]["selected"] = [{"x": 30, "y": 40}, {"x": 60, "y": 42}]
        self.assertFalse(lossless.evaluate_fixture(fixture, analysis)["passed"])


if __name__ == "__main__":
    unittest.main()
