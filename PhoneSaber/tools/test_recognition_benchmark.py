import json
from pathlib import Path
import tempfile
import unittest

import recognition_benchmark as bench


def color(detected=True, source="color-mask", score=42):
    endpoints = [{"x": 0, "y": 0}, {"x": 3, "y": 4}]
    candidate = {"eligible": detected, "endpoints": endpoints, "source": source, "score": score}
    return {"selected": endpoints if detected else None, "candidates": [candidate]}


def row(digest="a", truth="unknown", detected=True):
    colors = {c: color(detected) for c in bench.COLORS}
    return {"id": digest, "truth": {c: truth for c in bench.COLORS},
            "colors": colors, "recognition_sha256": bench.signature(colors)}


class RecognitionBenchmarkTests(unittest.TestCase):
    def test_discovery_excludes_drawings_in_all_roots_and_keeps_copies(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            directories = [root / "dl/phonesaber_sample_forensic", root / "inbox/session/images",
                           root / "phone/ios/Fixtures", root / "phone/tools/fixtures"]
            for directory in directories:
                directory.mkdir(parents=True)
                (directory / "frame.png").write_bytes(b"original")
                (directory / "frame_annotated.png").write_bytes(b"drawing")
                (directory / "frame_overlay.PNG").write_bytes(b"drawing")
            unrelated = root / "dl/phonesaber_sample_analysis"
            unrelated.mkdir()
            (unrelated / "frame.png").write_bytes(b"drawing")
            images, excluded = bench.discover(root / "dl", root / "inbox", root / "phone")
            self.assertEqual(len(images), 4)
            self.assertEqual(sum(excluded.values()), 8)
            self.assertEqual(len({i.digest for i in images}), 1)
            self.assertEqual(len({i.occurrence for i in images}), 4)

    def test_hash_truth_join_does_not_label_other_color_or_basename(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            image = root / "phonesaber_s_forensic/frame.png"
            image.parent.mkdir()
            image.write_bytes(b"original")
            digest = bench.sha256(b"original")
            labels = root / "labels.json"
            labels.write_text(json.dumps({"labels": [{"color": "red", "motion": "no_saber",
                              "images": [{"path": str(image.relative_to(root)), "sha256": digest}]}]}))
            truths, _, missing = bench.truth_index(labels, None, root, root)
            self.assertEqual(truths[(digest, "red")], "no_saber")
            self.assertNotIn((digest, "blue"), truths)
            self.assertNotIn((bench.sha256(b"different"), "red"), truths)
            self.assertFalse(missing)
            image.write_bytes(b"changed")
            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                bench.truth_index(labels, None, root, root)

    def test_truth_conflict_fails_instead_of_overwriting(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            image = root / "phonesaber_s_forensic/frame.png"
            image.parent.mkdir()
            image.write_bytes(b"original")
            evidence = {"path": str(image.relative_to(root)), "sha256": bench.sha256(b"original")}
            labels = root / "labels.json"
            labels.write_text(json.dumps({"labels": [{"color": "red", "motion": motion,
                              "images": [evidence]} for motion in ("static", "no_saber")]}))
            with self.assertRaisesRegex(ValueError, "conflicting truth"):
                bench.truth_index(labels, None, root, root)

    def test_manifest_truth_is_distinct_from_expected_detection(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            (root / "a.png").write_bytes(b"a")
            manifest = root / "manifest.json"
            manifest.write_text(json.dumps({"fixtures": [{"path": "a.png", "sha256": bench.sha256(b"a"),
                "color": "BLUE", "truth": "positive", "expectedDetected": False}]}))
            truths, _, _ = bench.truth_index(None, manifest, root, root, root)
            self.assertEqual(truths[(bench.sha256(b"a"), "blue")], "saber")

    def test_path_escape_and_annotations_cannot_be_truth_evidence(self):
        with tempfile.TemporaryDirectory() as temp:
            for path in ("../escape.png", "image_annotated.png", "/outside.png"):
                with self.assertRaises(ValueError):
                    bench.contained(Path(temp), path)

    def test_rates_exclude_unknown_and_handle_empty_denominator(self):
        result = bench.summarize([row("a", "no_saber"), row("b", "no_saber", False),
                                  row("c", "saber"), row("d", "unknown")])["red"]
        self.assertEqual(result["no_saber"]["rate"], .5)
        self.assertEqual(result["saber"]["rate"], 1)
        self.assertEqual(result["unknown"]["frames"], 1)
        self.assertEqual(result["saber"]["length_px"]["p50"], 5)
        self.assertIsNone(bench.summarize([])["red"]["no_saber"]["rate"])

    def test_duplicates_are_deduplicated_and_nondeterminism_is_an_error(self):
        a = row()
        self.assertEqual(len(bench.unique_rows([a, a])), 1)
        b = row()
        b["recognition_sha256"] = "different"
        with self.assertRaisesRegex(ValueError, "different recognition"):
            bench.unique_rows([a, b])

    def test_winner_uses_first_eligible_and_checks_selected_contract(self):
        result = color()
        rejected = {**result["candidates"][0], "eligible": False, "score": 99}
        result["candidates"].insert(0, rejected)
        self.assertEqual(bench.winner(result)["score"], 42)
        result["selected"] = None
        with self.assertRaisesRegex(ValueError, "contract"):
            bench.winner(result)

    def test_comparison_separates_added_images_truth_and_candidate_changes(self):
        before = {"rows": [row("a"), row("b")], "inventory_sha256": "old", "runtime_ms": {"p50": 2}}
        after = {"rows": [row("a", "saber"), row("c")], "inventory_sha256": "new", "runtime_ms": {"p50": 1}}
        # A diagnostic/score change with identical winner endpoints must count.
        after["rows"][0]["colors"]["red"]["candidates"][0]["score"] = 43
        after["rows"][0]["recognition_sha256"] = bench.signature(after["rows"][0]["colors"])
        result = bench.compare(before, after)
        self.assertEqual(result["shared_images"], 1)
        self.assertEqual(result["removed_images"], 1)
        self.assertEqual(result["added_images"], 1)
        self.assertEqual(result["truth_changed_images"], 1)
        self.assertEqual(result["recognition_changed_images"], 1)
        self.assertFalse(result["same_corpus"])

    def test_nearest_rank_quantiles(self):
        self.assertEqual(bench.stats([1, 2, 3, 4])["p50"], 2)
        self.assertEqual(bench.stats(range(1, 21))["p95"], 19)
        self.assertIsNone(bench.stats([])["mean"])

    def test_jni_expectations_are_checked_without_changing_unknown_truth(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "jni.json"
            r = row("a")
            path.write_text(json.dumps({"images": [{"sha256": "a", "colors": {
                c: {"selected": r["colors"][c]["selected"], "candidateType": "color-mask"}
                for c in bench.COLORS}}]}))
            result = bench.check_jni_expectations([r], path)
            self.assertEqual(result["color_frames_checked"], 2)
            self.assertEqual(result["mismatches"], 0)
            self.assertEqual(r["truth"]["red"], "unknown")
            r["colors"]["blue"]["candidates"][0]["source"] = "core-line"
            self.assertEqual(bench.check_jni_expectations([r], path)["mismatches"], 1)


if __name__ == "__main__":
    unittest.main()
