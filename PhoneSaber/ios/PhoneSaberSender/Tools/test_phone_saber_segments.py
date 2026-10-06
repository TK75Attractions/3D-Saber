#!/usr/bin/env python3
"""Segment markers: analyzer, metadata schema and triage validators (old bundles stay valid)."""
from __future__ import annotations

import contextlib
import copy
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from phone_saber_metadata_schema import (  # noqa: E402
    segment_marker_errors, segment_summary_errors, validate_document)
from phone_saber_segments import analyze, main  # noqa: E402
from phone_saber_triage_codex import input_plan  # noqa: E402
from phone_saber_triage_protocol import BundleError  # noqa: E402
from test_phone_saber_triage_codex import write_codex_bundle  # noqa: E402

LABELS = ("unlabeled", "sabersVisible", "noSaber", "noSaberCovered")


def detection(detected: bool) -> dict:
    return {"detected": detected, "predicted": False,
            **({"x1": 1, "y1": 2, "x2": 30, "y2": 2} if detected else {})}


def frame(frame_id: int, red: bool = False, blue: bool = False, predicted_red: bool = False) -> dict:
    red_detection = detection(red or predicted_red)
    red_detection["predicted"] = predicted_red
    return {"frameID": frame_id, "presentationTimeSeconds": frame_id / 30,
            "red": red_detection, "blue": detection(blue),
            "redDetectionSucceeded": red, "blueDetectionSucceeded": blue,
            "candidateDiagnostics": None, "forensicCaptured": False, "manualCaptured": False}


def recorder_summary(by_label: dict, markers: list) -> dict:
    """The shape DebugSegmentLedger.summary writes (every label and color present)."""
    full = {label: {"frames": 0, "red": {"detectedFrames": 0, "measuredFrames": 0},
                    "blue": {"detectedFrames": 0, "measuredFrames": 0}} for label in LABELS}
    for label, entry in by_label.items():
        full[label] = copy.deepcopy(entry)
    return {"formatVersion": 1, "totalFrames": sum(e["frames"] for e in full.values()),
            "byLabel": full,
            "falsePositiveFrames": {label: {color: full[label][color]["detectedFrames"]
                                            for color in ("red", "blue")}
                                    for label in ("noSaber", "noSaberCovered")},
            "markerCount": len(markers), "droppedMarkerCount": 0, "definition": "Operator labels."}


MARKERS = [{"frameID": 3, "timestamp": 0.1, "label": "sabersVisible"},
           {"frameID": 5, "timestamp": 5 / 30, "label": "noSaber"},
           {"frameID": 9, "timestamp": 0.3, "label": "noSaberCovered"}]
FRAMES = [frame(1, red=True), frame(2),
          frame(3, red=True, blue=True), frame(4, blue=True),
          frame(5, red=True), frame(6, predicted_red=True), frame(7), frame(8, red=True),
          frame(9), frame(10, blue=True)]
BY_LABEL = {
    "unlabeled": {"frames": 2, "red": {"detectedFrames": 1, "measuredFrames": 1},
                  "blue": {"detectedFrames": 0, "measuredFrames": 0}},
    "sabersVisible": {"frames": 2, "red": {"detectedFrames": 1, "measuredFrames": 1},
                      "blue": {"detectedFrames": 2, "measuredFrames": 2}},
    "noSaber": {"frames": 4, "red": {"detectedFrames": 3, "measuredFrames": 2},
                "blue": {"detectedFrames": 0, "measuredFrames": 0}},
    "noSaberCovered": {"frames": 2, "red": {"detectedFrames": 0, "measuredFrames": 0},
                       "blue": {"detectedFrames": 1, "measuredFrames": 1}},
}


def metadata(markers: list | None = MARKERS, summary: bool = True) -> dict:
    document = {"formatVersion": 1, "sessionID": "phonesaber_20261003_120000_000",
                "width": 64, "height": 48, "frames": copy.deepcopy(FRAMES), "cameraSamples": []}
    if markers is not None:
        document["segmentMarkers"] = copy.deepcopy(markers)
        if summary:
            document["segmentSummary"] = recorder_summary(BY_LABEL, markers)
    return document


class SegmentAnalyzerTests(unittest.TestCase):
    def write(self, directory: str, name: str, value: dict) -> Path:
        path = Path(directory) / name
        path.write_text(json.dumps(value), encoding="utf-8")
        return path

    def test_metadata_recomputation_matches_the_recorded_summary(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            report = analyze(self.write(directory, "s_metadata.json", metadata()))
        self.assertEqual(report["source"], "metadata")
        self.assertEqual(report["warnings"], [])
        self.assertEqual(report["totalFrames"], 10)
        for label in LABELS:
            self.assertEqual(report["labels"][label]["frames"], BY_LABEL[label]["frames"])
            for color in ("red", "blue"):
                self.assertEqual(report["labels"][label][color]["detectedFrames"],
                                 BY_LABEL[label][color]["detectedFrames"], (label, color))
        no_saber = report["falsePositive"]["noSaber"]
        self.assertEqual(no_saber["red"]["falsePositiveFrames"], 3)
        self.assertAlmostEqual(no_saber["red"]["falsePositiveRate"], 0.75)
        self.assertEqual(report["labels"]["noSaber"]["red"]["measuredRate"], 0.5)
        self.assertEqual(report["falsePositive"]["noSaberCovered"]["red"]["falsePositiveRate"], 0.0)
        self.assertEqual([m["frameID"] for m in report["markers"]], [3, 5, 9])

    def test_recorded_summary_disagreement_is_reported(self) -> None:
        document = metadata()
        document["segmentSummary"]["byLabel"]["noSaber"]["frames"] = 99
        with tempfile.TemporaryDirectory() as directory:
            report = analyze(self.write(directory, "s_metadata.json", document))
        self.assertIn("recomputed counts differ from the recorded segmentSummary", report["warnings"])
        self.assertEqual(report["labels"]["noSaber"]["frames"], 4)

    def test_old_metadata_counts_every_frame_as_unlabeled(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            report = analyze(self.write(directory, "old_metadata.json", metadata(markers=None)))
        self.assertEqual(report["labels"]["unlabeled"]["frames"], 10)
        self.assertEqual(report["labels"]["unlabeled"]["red"]["detectedFrames"], 5)
        self.assertIsNone(report["falsePositive"]["noSaber"]["red"]["falsePositiveRate"])
        self.assertTrue(any("no segmentMarkers" in w for w in report["warnings"]))

    def test_bundle_summary_and_text_and_json_output(self) -> None:
        summary = {"formatVersion": 1, "sessionID": "phonesaber_20261003_120000_000",
                   "segmentSummary": {**recorder_summary(BY_LABEL, MARKERS), "markers": MARKERS}}
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "phone_saber_triage_x"
            bundle.mkdir()
            self.write(str(bundle), "summary.json", summary)
            report = analyze(bundle)
            self.assertEqual(report["source"], "summary")
            self.assertEqual(report["falsePositive"]["noSaber"]["red"]["falsePositiveFrames"], 3)
            self.assertEqual(report["labels"]["noSaberCovered"]["blue"]["detectionRate"], 0.5)
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(main([str(bundle)]), 0)
            text = out.getvalue()
            self.assertIn("noSaber: RED 3/4 (75.0%), BLUE 0/4 (0.0%)", text)
            self.assertIn("from frame 5 @ 0.167s -> noSaber", text)
            out = io.StringIO()
            with contextlib.redirect_stdout(out):
                self.assertEqual(main([str(bundle / "summary.json"), "--json"]), 0)
            self.assertEqual(json.loads(out.getvalue())["totalFrames"], 10)

    def test_old_bundle_without_segment_summary_is_reported_not_crashed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            path = self.write(directory, "summary.json", {"formatVersion": 1, "sessionID": "x"})
            err = io.StringIO()
            with contextlib.redirect_stderr(err):
                self.assertEqual(main([str(path)]), 2)
            self.assertIn("no segmentSummary", err.getvalue())
            with contextlib.redirect_stderr(io.StringIO()):
                self.assertEqual(main([str(Path(directory) / "missing.json")]), 2)


class SegmentSchemaTests(unittest.TestCase):
    def test_old_and_new_metadata_validate_without_segment_warnings(self) -> None:
        for document in (metadata(markers=None), metadata()):
            report = validate_document(document).report
            self.assertFalse([w for w in report.warnings if "segment" in w], report.warnings)
        validated = validate_document(metadata())
        self.assertEqual(validated.document["segmentMarkers"], MARKERS)

    def test_unknown_marker_label_and_wrong_types_warn(self) -> None:
        document = metadata()
        document["segmentMarkers"][0]["label"] = "maybe"
        document["segmentSummary"]["totalFrames"] = "ten"
        warnings = validate_document(document).report.warnings
        self.assertIn("segmentMarkers[*].label: unknown segment label; treated as unknown", warnings)
        self.assertIn("segmentSummary.totalFrames: expected integer; treated as unknown", warnings)

    def test_strict_validators(self) -> None:
        good = {**recorder_summary(BY_LABEL, MARKERS), "markers": MARKERS}
        self.assertEqual(segment_summary_errors(good), [])
        self.assertEqual(segment_marker_errors(MARKERS), [])
        self.assertTrue(segment_marker_errors([MARKERS[1], MARKERS[0]]))
        self.assertTrue(segment_marker_errors([{**MARKERS[0], "label": "x"}]))
        cases = []
        bad = copy.deepcopy(good); bad["totalFrames"] = 11; cases.append(bad)
        bad = copy.deepcopy(good); bad["falsePositiveFrames"]["noSaber"]["red"] = 0; cases.append(bad)
        bad = copy.deepcopy(good); bad["byLabel"]["noSaber"]["red"]["detectedFrames"] = 5; cases.append(bad)
        bad = copy.deepcopy(good); bad["falsePositiveFrames"]["sabersVisible"] = {"red": 1}; cases.append(bad)
        bad = copy.deepcopy(good); bad["markerCount"] = 7; cases.append(bad)
        bad = copy.deepcopy(good); bad["fullSessionFrames"] = []; cases.append(bad)
        bad = copy.deepcopy(good); bad["byLabel"]["other"] = {"frames": 0}; cases.append(bad)
        for index, case in enumerate(cases):
            self.assertTrue(segment_summary_errors(case), index)


class SegmentTriageValidationTests(unittest.TestCase):
    def bundle(self, directory: str) -> Path:
        bundle = Path(directory) / "bundle"
        write_codex_bundle(bundle)
        return bundle

    def edit(self, path: Path, change) -> None:
        value = json.loads(path.read_text(encoding="utf-8"))
        change(value)
        path.write_text(json.dumps(value), encoding="utf-8")

    def context_path(self, bundle: Path) -> Path:
        summary = json.loads((bundle / "summary.json").read_text(encoding="utf-8"))
        return bundle / summary["images"][0]["frameContextPath"]

    def test_old_bundle_without_segment_fields_stays_valid(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = self.bundle(directory)
            self.assertNotIn("segmentSummary", json.loads((bundle / "summary.json").read_text()))
            self.assertEqual(len(input_plan(bundle).image_paths), 1)

    def test_segment_summary_and_context_label_are_accepted(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = self.bundle(directory)
            self.edit(bundle / "summary.json", lambda s: s.update(
                segmentSummary={**recorder_summary(BY_LABEL, MARKERS), "markers": MARKERS}))
            self.edit(self.context_path(bundle), lambda c: c.update(segmentLabel="noSaber"))
            self.assertEqual(len(input_plan(bundle).image_paths), 1)

    def test_malformed_segment_summary_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = self.bundle(directory)
            broken = recorder_summary(BY_LABEL, MARKERS)
            broken["falsePositiveFrames"]["noSaber"]["red"] = 0
            self.edit(bundle / "summary.json", lambda s: s.update(segmentSummary=broken))
            with self.assertRaisesRegex(BundleError, "segment summary is malformed"):
                input_plan(bundle)

    def test_unknown_context_label_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = self.bundle(directory)
            self.edit(self.context_path(bundle), lambda c: c.update(segmentLabel="probablyNoSaber"))
            with self.assertRaisesRegex(BundleError, "invalid segment label"):
                input_plan(bundle)


if __name__ == "__main__":
    unittest.main()
