import contextlib
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import benchmark_detection as bench


class BenchmarkDetectionTests(unittest.TestCase):
    def test_single_class_manifest_runs_without_baseline(self):
        document = json.loads(bench.DEFAULT_MANIFEST.read_text())
        for blade_present in (True, False):
            with self.subTest(blade_present=blade_present), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                manifest = root / "manifest.json"
                fixture = next(row for row in document["fixtures"]
                               if row["expectedDetected"] == blade_present)
                manifest.write_text(json.dumps({**document, "fixtures": [fixture]}))
                output = root / "benchmark.json"
                analysis = {
                    "colors": {color: {"selected": None, "candidates": []}
                               for color in ("red", "blue")},
                    "profile": {metric: 1.0 for metric in bench.METRICS if metric != "components"},
                    "pipeline": {color: {"components": 0} for color in ("red", "blue")},
                }
                arguments = ["benchmark_detection.py", "--manifest", str(manifest),
                             "--source-root", str(root), "--json", str(output)]
                with mock.patch("sys.argv", arguments), \
                        mock.patch.object(bench, "compile_detector") as compile_detector, \
                        mock.patch.object(bench, "analyze_png", return_value=analysis), \
                        contextlib.redirect_stdout(io.StringIO()):
                    bench.main()
                result = json.loads(output.read_text())
                compile_detector.assert_called_once_with(mock.ANY, root)
                self.assertEqual(result["fixture_count"], 1)
                self.assertEqual({row["phase"] for row in result["samples"]}, {"after"})
                present, absent = ("blade", "no_blade") if blade_present else ("no_blade", "blade")
                self.assertEqual(result["summary"][present]["samples"], 5)
                self.assertEqual(result["summary"][absent], {"samples": 0})

    def test_p95_uses_nearest_rank_in_each_class(self):
        samples = [{"blade_present": blade_present, "components": value,
                    "profile": dict.fromkeys(bench.METRICS, value)}
                   for blade_present in (True, False) for value in range(20, 0, -1)]
        for group in bench.summarize(samples).values():
            for metric in bench.METRICS:
                self.assertEqual(group[metric], {"median": 10.5, "p95": 19, "max": 20})

    def test_formal_manifest_runs_interleaved_comparison(self):
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / "benchmark.json"
            analysis = {
                "colors": {color: {"selected": None, "candidates": []}
                           for color in ("red", "blue")},
                "profile": {metric: 1.0 for metric in bench.METRICS if metric != "components"},
                "pipeline": {color: {"components": 0} for color in ("red", "blue")},
            }
            arguments = ["benchmark_detection.py", "--iterations", "5", "--json", str(output),
                         "--baseline-source-root", temporary]
            with mock.patch("sys.argv", arguments), \
                    mock.patch.object(bench, "compile_detector") as compile_detector, \
                    mock.patch.object(bench, "analyze_png", return_value=analysis) as analyze, \
                    contextlib.redirect_stdout(io.StringIO()):
                bench.main()
            result = json.loads(output.read_text())
            self.assertEqual(result["fixture_count"], 40)
            self.assertEqual(compile_detector.call_count, 2)
            self.assertEqual(analyze.call_count, 400)
            self.assertEqual([row["phase"] for row in result["samples"][:2]], ["before", "after"])
            self.assertEqual([row["phase"] for row in result["samples"][80:82]], ["after", "before"])
            for phase in ("before", "after"):
                groups = result["summary"][phase]
                self.assertEqual(sum(group["samples"] for group in groups.values()), 200)
                self.assertTrue(all(group["samples"] > 0 for group in groups.values()))
                self.assertTrue(all(group["total_ms"]["p95"] == 1 for group in groups.values()))


if __name__ == "__main__":
    unittest.main()
