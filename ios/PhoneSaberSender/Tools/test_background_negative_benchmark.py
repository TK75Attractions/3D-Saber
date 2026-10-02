import contextlib
import copy
import io
import json
import os
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import run_background_negative_benchmark as bench


def _fake_analysis(red_selected=None, red_candidates=None):
    return {
        "colors": {
            "red": {"selected": red_selected, "candidates": red_candidates or []},
            "blue": {"selected": None, "candidates": []},
        }
    }


class ManifestSchemaTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.document, cls.images = bench.load_manifest(bench.DEFAULT_MANIFEST)

    def test_manifest_entries_are_pinned_unique_and_inbox_relative(self):
        self.assertEqual(len(self.images), 8)
        self.assertEqual(len({image["id"] for image in self.images}), len(self.images))
        for image in self.images:
            self.assertRegex(image["sha256"], r"^[0-9a-f]{64}$")
            path = Path(image["path"])
            self.assertFalse(path.is_absolute())
            self.assertFalse(image["path"].startswith("~"))
            self.assertNotIn("..", path.parts)
            self.assertTrue(image["path"].startswith("phone_saber_triage_phonesaber_"))
            self.assertIn(image["sourceSession"], image["path"])
            self.assertEqual(path.parts[1], "images")

    def test_manifest_does_not_reference_repository_fixtures_or_the_real_saber_frame(self):
        for image in self.images:
            self.assertNotIn("72881", image["path"])
            self.assertNotIn("Fixtures", image["path"])
        lossless_names = {
            fixture["path"] for fixture in json.loads(
                (bench.TOOLS_DIR / "lossless_regression_manifest.json").read_text(encoding="utf-8")
            )["fixtures"]
        }
        self.assertTrue(lossless_names.isdisjoint(image["path"] for image in self.images))

    def test_baseline_records_known_failures_and_the_rejected_control(self):
        detected = [image["id"] for image in self.images if "known_detected" in image["baselineStatus"].values()]
        self.assertEqual(len(detected), 7)
        controls = [image for image in self.images if image["id"].startswith("control_")]
        self.assertEqual(len(controls), 1)
        self.assertEqual(controls[0]["baselineStatus"], {"RED": "rejected"})
        self.assertEqual(self.document["baseline"]["falsePositives"], 7)

    def _write_manifest(self, directory, mutate):
        document = copy.deepcopy(self.document)
        mutate(document)
        path = Path(directory) / "manifest.json"
        path.write_text(json.dumps(document), encoding="utf-8")
        return path

    def test_invalid_entries_are_rejected(self):
        mutations = {
            "short sha": lambda d: d["images"][0].update(sha256="a3835b47"),
            "duplicate id": lambda d: d["images"][1].update(id=d["images"][0]["id"]),
            "absolute path": lambda d: d["images"][0].update(path="/tmp/x.png"),
            "home path": lambda d: d["images"][0].update(path="~/x.png"),
            "parent path": lambda d: d["images"][0].update(path="../x.png"),
            "bad color": lambda d: d["images"][0].update(expectedNotDetected=["GREEN"]),
            "status keys": lambda d: d["images"][0].update(baselineStatus={"RED": "known_detected"}),
            "status value": lambda d: d["images"][1].update(baselineStatus={"RED": "maybe"}),
            "missing field": lambda d: d["images"][0].pop("content"),
            "schema": lambda d: d.update(schemaVersion=2),
        }
        with tempfile.TemporaryDirectory() as temporary:
            for label, mutate in mutations.items():
                with self.subTest(label):
                    with self.assertRaises(ValueError):
                        bench.load_manifest(self._write_manifest(temporary, mutate))


class RunnerTests(unittest.TestCase):
    def setUp(self):
        _, self.images = bench.load_manifest(bench.DEFAULT_MANIFEST)

    def _main(self, *arguments):
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(io.StringIO()):
            code = bench.main(list(arguments))
        return code, stdout.getvalue()

    def test_empty_inbox_skips_everything_and_never_compiles(self):
        with tempfile.TemporaryDirectory() as temporary, \
                mock.patch.object(bench.lossless, "compile_harness", side_effect=AssertionError("compiled")):
            code, output = self._main("--inbox", temporary)
            strict_code, _ = self._main("--inbox", temporary, "--strict")
            json_code, json_output = self._main("--inbox", temporary, "--json", "-")
        self.assertEqual((code, strict_code, json_code), (0, 0, 0))
        self.assertIn("false positives 0 / available 0 (missing 8, errors 0)", output)
        summary = json.loads(json_output)["summary"]
        self.assertEqual((summary["available"], summary["missing"]), (0, 8))

    def test_sha_mismatch_is_an_error_entry_and_not_evaluated(self):
        with tempfile.TemporaryDirectory() as temporary:
            target = Path(temporary) / self.images[0]["path"]
            target.parent.mkdir(parents=True)
            target.write_bytes(b"not the pinned image")
            with mock.patch.object(bench.lossless, "compile_harness", side_effect=AssertionError("compiled")), \
                    mock.patch.object(bench.lossless, "analyze_png", side_effect=AssertionError("analyzed")):
                rows = bench.run(self.images, Path(temporary))
                code, output = self._main("--inbox", temporary)
                strict_code, _ = self._main("--inbox", temporary, "--strict")
        first = rows[0]
        self.assertEqual(first["state"], "error")
        self.assertIn("SHA-256 mismatch", first["error"])
        self.assertIsNone(first["result"])
        self.assertTrue(all(row["state"] == "missing" for row in rows[1:]))
        self.assertEqual(code, 0)
        self.assertEqual(strict_code, 1)
        self.assertIn(f"ERROR {self.images[0]['id']}", output)
        self.assertIn("(missing 7, errors 1)", output)

    def test_evaluate_counts_expected_color_detection_as_false_positive(self):
        image = next(image for image in self.images if image["expectedNotDetected"] == ["RED", "BLUE"])
        winner = {
            "source": "color-close", "score": 60.0, "eligible": True,
            "bounding_box": {"min_x": 10, "min_y": 20, "max_x": 30, "max_y": 24},
            "endpoints": [{"x": 10, "y": 22}, {"x": 30, "y": 22}],
            "peak_value": 255, "mean_value": 220.0, "high_value_ratio": 0.5, "color_purity": 0.6,
        }
        loser = {**winner, "score": 40.0, "eligible": False, "source": "core-line"}
        result = bench.evaluate_image(image, _fake_analysis(winner["endpoints"], [winner, loser]))
        self.assertTrue(result["falsePositive"])
        self.assertEqual(result["falsePositiveColors"], ["RED"])
        red = result["colors"]["RED"]
        self.assertEqual((red["eligibleCount"], red["candidateCount"]), (1, 2))
        self.assertEqual(red["winner"]["centroid"], {"x": 20.0, "y": 22.0})
        self.assertEqual(red["winner"]["features"]["peak_value"], 255)
        self.assertEqual(result["measuredStatus"], {"RED": "known_detected", "BLUE": "rejected"})
        self.assertFalse(result["baselineChanged"])

        rejected = bench.evaluate_image(image, _fake_analysis(None, [loser]))
        self.assertFalse(rejected["falsePositive"])
        self.assertIsNone(rejected["colors"]["RED"]["winner"])
        self.assertTrue(rejected["baselineChanged"])


@unittest.skipUnless(
    os.environ.get("PHONESABER_BACKGROUND_BENCHMARK_INTEGRATION") == "1",
    "opt-in: set PHONESABER_BACKGROUND_BENCHMARK_INTEGRATION=1 to run against the private inbox",
)
class RealInboxIntegrationTests(unittest.TestCase):
    def test_present_private_images_match_pinned_hashes_and_run(self):
        _, images = bench.load_manifest(bench.DEFAULT_MANIFEST)
        inbox = bench.resolve_inbox(None)
        states = [bench.locate(image, inbox)[0] for image in images]
        if "available" not in states:
            self.skipTest("no benchmark images present in the diagnostics inbox")
        self.assertNotIn("error", states)
        rows = bench.run(images, inbox)
        summary = bench.summarize(rows)
        self.assertEqual(summary["errors"], 0)
        self.assertEqual(summary["available"], states.count("available"))


if __name__ == "__main__":
    unittest.main()
