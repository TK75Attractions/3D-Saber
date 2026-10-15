import contextlib
import csv
import io
import sys
import unittest
from unittest.mock import Mock, patch
import benchmark_compare


class BenchmarkComparisonTests(unittest.TestCase):
    def compare(self, baseline_csv):
        current_csv = "png,width,height,median_ms,p95_ms,min_ms,max_ms\nfixture.png,8,8,2,3,1,4\n"
        calls = []

        def run(command, **kwargs):
            calls.append(command)
            result = Mock()
            if command[0] == "before":
                result.stdout = baseline_csv.pop(0) if isinstance(baseline_csv, list) else baseline_csv
            else:
                result.stdout = current_csv
            return result

        output = io.StringIO()
        with patch.object(sys, "argv", ["benchmark_compare", "before", "after", "fixture.png",
                                      "--iterations", "20", "--rounds", "2"]), \
             patch.object(benchmark_compare.subprocess, "run", side_effect=run), \
             contextlib.redirect_stdout(output):
            benchmark_compare.main()
        self.assertEqual([call[0] for call in calls], ["before", "after", "after", "before"])
        self.assertTrue(all(call[1:] == ["20", "fixture.png"] for call in calls))
        rows = list(csv.DictReader(io.StringIO(output.getvalue())))
        self.assertEqual(len(rows), 1)
        self.assertEqual(rows[0]["reduction_percent"], "50.0")
        self.assertEqual(rows[0]["after_p95_ms"], "3.000000")
        return rows[0]

    def test_pre_p95_baseline_keeps_comparison_and_marks_tail_unmeasured(self):
        row = self.compare("png,width,height,median_ms,min_ms,max_ms\nfixture.png,8,8,4,2,6\n")
        self.assertEqual(row["before_p95_ms"], "")
        self.assertEqual(row["before_ms"], "4.000000")

    def test_measured_p95_is_aggregated_across_interleaved_trials(self):
        row = self.compare(["png,width,height,median_ms,p95_ms,min_ms,max_ms\nfixture.png,8,8,4,5,2,6\n",
                            "png,width,height,median_ms,p95_ms,min_ms,max_ms\nfixture.png,8,8,4,9,2,10\n"])
        self.assertEqual(row["before_p95_ms"], "7.000000")
        self.assertEqual(row["before_trials_ms"], "4.000000;4.000000")
        self.assertEqual(row["after_trials_ms"], "2.000000;2.000000")


if __name__ == "__main__":
    unittest.main()
