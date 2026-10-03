#!/usr/bin/env python3
"""summary.json selectionNotes (start/stop handling periods): validator, codex input, report.

The field is optional: bundles recorded before handling periods existed stay valid.
"""
from __future__ import annotations

import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from phone_saber_metadata_schema import selection_notes_errors  # noqa: E402
from phone_saber_session_report import build_report, render_markdown  # noqa: E402
from phone_saber_triage_codex import input_plan  # noqa: E402
from phone_saber_triage_protocol import BundleError  # noqa: E402
from test_phone_saber_triage_codex import write_codex_bundle  # noqa: E402

NOTES = {
    "handlingPeriodSeconds": {"startSeconds": 3, "stopSeconds": 5},
    "events": [
        {"kind": "tracking", "eventID": 1000000000, "centerFrameID": 306, "handlingPeriod": False,
         "selected": True,
         "notes": ["center frame 306 at 10.20 s is outside the handling periods (first 3 s, last 5 s)",
                   "highest-ranked frame 6 (score 9.10) is in the start handling period and was de-prioritised",
                   "selected: 11 frame window in the bundle"]},
        {"kind": "bridge", "eventID": 2, "centerFrameID": 3303, "handlingPeriod": True, "selected": False,
         "notes": ["center frame 3303 at 105.83 s is in the stop handling period (last 5 s)",
                   "not selected: image or byte limit reached by preferred events"]},
    ],
}


class SelectionNotesValidatorTests(unittest.TestCase):
    def test_recorder_shape_is_valid(self) -> None:
        self.assertEqual(selection_notes_errors(NOTES), [])

    def test_malformed_notes_are_rejected(self) -> None:
        cases = []
        bad = copy.deepcopy(NOTES); bad["extra"] = 1; cases.append(bad)
        bad = copy.deepcopy(NOTES); del bad["handlingPeriodSeconds"]; cases.append(bad)
        bad = copy.deepcopy(NOTES); bad["handlingPeriodSeconds"]["stopSeconds"] = -1; cases.append(bad)
        bad = copy.deepcopy(NOTES); bad["events"] = []; cases.append(bad)
        bad = copy.deepcopy(NOTES); bad["events"][0]["kind"] = "motion"; cases.append(bad)
        bad = copy.deepcopy(NOTES); bad["events"][0]["handlingPeriod"] = "yes"; cases.append(bad)
        bad = copy.deepcopy(NOTES); bad["events"][1]["eventID"] = -2; cases.append(bad)
        bad = copy.deepcopy(NOTES); bad["events"][1]["notes"] = []; cases.append(bad)
        bad = copy.deepcopy(NOTES); bad["events"][1]["notes"] = ["x" * 301]; cases.append(bad)
        bad = copy.deepcopy(NOTES); bad["events"][1]["fullSessionFrames"] = []; cases.append(bad)
        bad = copy.deepcopy(NOTES); bad["events"] = bad["events"] * 5; cases.append(bad)
        self.assertTrue(selection_notes_errors([]))
        for index, case in enumerate(cases):
            self.assertTrue(selection_notes_errors(case), index)


class SelectionNotesBundleTests(unittest.TestCase):
    def bundle(self, directory: str, notes=None) -> Path:
        bundle = Path(directory) / "bundle"
        write_codex_bundle(bundle)
        if notes is not None:
            path = bundle / "summary.json"
            summary = json.loads(path.read_text(encoding="utf-8"))
            summary["selectionNotes"] = notes
            path.write_text(json.dumps(summary), encoding="utf-8")
        return bundle

    def test_old_bundle_without_notes_stays_valid(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = self.bundle(directory)
            self.assertNotIn("selectionNotes", json.loads((bundle / "summary.json").read_text()))
            self.assertEqual(len(input_plan(bundle).image_paths), 1)
            text = render_markdown(build_report(bundle))
            self.assertIn("selection notes: n/a", text)

    def test_notes_are_accepted_and_reported(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = self.bundle(directory, NOTES)
            self.assertEqual(len(input_plan(bundle).image_paths), 1)
            text = render_markdown(build_report(bundle))
            self.assertIn("handling periods: first 3 s, last 5 s", text)
            self.assertIn("bridge 2 (center 3303, handlingPeriod true, selected false)", text)

    def test_malformed_notes_are_rejected_by_codex_input(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bad = copy.deepcopy(NOTES)
            bad["events"][0]["selected"] = 1
            bundle = self.bundle(directory, bad)
            with self.assertRaisesRegex(BundleError, "selection notes are malformed"):
                input_plan(bundle)


if __name__ == "__main__":
    unittest.main()
