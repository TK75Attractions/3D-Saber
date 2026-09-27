import contextlib
import io
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import analyze_freeze_timeline as freeze_timeline
import analyze_session_metadata as session_metadata
import compare_phone_saber_sessions as session_comparison
from phone_saber_metadata_schema import (
    CURRENT_FORMAT_VERSION,
    LEGACY_FORMAT_VERSION,
    load_metadata_file,
    validate_document,
)
from validate_phone_saber_metadata import validate_paths


TOOLS = Path(__file__).resolve().parent
FIXTURES = TOOLS / "fixtures"
LEGACY_SESSION = FIXTURES / "legacy-pre-diagnostics-session.json"
RED_SESSION = FIXTURES / "unversioned-red-dropout-excerpt.json"
BLUE_SESSION = FIXTURES / "unversioned-blue-dropout-excerpt.json"


class PhoneSaberMetadataSchemaTests(unittest.TestCase):
    def test_real_legacy_session_keeps_analyzable_fields_and_marks_absent_diagnostics_unknown(self):
        validated = load_metadata_file(LEGACY_SESSION)
        self.assertEqual(validated.report.format_version, LEGACY_FORMAT_VERSION)
        self.assertEqual(len(validated.frames), 294)
        self.assertTrue(any("blueDetectionSucceeded" in item for item in validated.report.warnings))

        result = session_metadata.analyze_file(LEGACY_SESSION)
        self.assertEqual(result["session_id"], "phonesaber_20260921_184852_675")
        for color in ("red", "blue"):
            summary = result["colors"][color]
            self.assertEqual(summary["frames"], 294)
            self.assertEqual(summary["frames_with_detection_status"], 294)
            self.assertEqual(summary["candidate_diagnostic_frames"], 0)
            self.assertEqual(summary["total_candidate_zero_frames"], 0)

        timeline = freeze_timeline.analyze_file(LEGACY_SESSION)
        self.assertEqual(timeline["sessionID"], "phonesaber_20260921_184852_675")

    def test_unversioned_recorded_excerpts_validate_both_dropout_colors_and_camera_samples(self):
        for path, color in ((RED_SESSION, "red"), (BLUE_SESSION, "blue")):
            with self.subTest(color=color):
                validated = load_metadata_file(path)
                self.assertEqual(validated.report.format_version, LEGACY_FORMAT_VERSION)
                self.assertEqual(validated.report.warning_count, 1)  # only the missing version marker
                self.assertEqual(len(validated.document["cameraSamples"]), 2)
                roles = {frame.get(f"{color}DropoutRole") for frame in validated.frames}
                self.assertIn("dropout", roles)
                self.assertIn("recovered", roles)

                summary = session_metadata.analyze_file(path)
                self.assertEqual(summary["colors"][color]["candidate_diagnostic_frames"], len(validated.frames))
                timeline = freeze_timeline.analyze_file(path)
                self.assertEqual(timeline["sessionID"], "phonesaber_20260923_143446_247")

        compared = session_comparison.compare_sessions(LEGACY_SESSION, RED_SESSION)
        self.assertEqual(compared["sessions"]["baseline"]["session_id"], "phonesaber_20260921_184852_675")
        self.assertEqual(compared["sessions"]["experimental"]["session_id"], "phonesaber_20260923_143446_247")

    def test_missing_and_mistyped_fields_become_local_unknowns(self):
        validated = validate_document({
            "frames": [
                {
                    "frameID": 1,
                    "presentationTimeSeconds": 0.0,
                    "red": {"detected": True, "x1": 1, "y1": 2},
                    "blue": {"detected": "not-a-boolean"},
                },
                {"frameID": 2, "red": {}, "blue": {}},
            ]
        })
        self.assertEqual(validated.report.format_version, LEGACY_FORMAT_VERSION)
        self.assertFalse(any(name in validated.frames[0]["red"] for name in ("x1", "y1", "x2", "y2")))
        result = {
            color: session_metadata.analyze_color(validated.frames, color)
            for color in ("red", "blue")
        }
        self.assertEqual(result["red"]["frames_with_detection_status"], 1)
        self.assertEqual(result["red"]["unknown_detection_status_frames"], 1)
        self.assertEqual(result["blue"]["frames_with_detection_status"], 0)
        self.assertEqual(result["blue"]["unknown_detection_status_frames"], 2)

        # Missing or invalid timestamps only split the timeline; they do not
        # prevent analysis of the other frame objects in the file.
        timeline_events = freeze_timeline.analyze_frames(validated.frames)
        self.assertEqual(timeline_events, [])

    def test_top_level_frame_array_remains_supported_as_legacy_version_zero(self):
        validated = validate_document([{"frameID": 1, "presentationTimeSeconds": 0.0}])
        self.assertEqual(validated.report.format_version, LEGACY_FORMAT_VERSION)
        self.assertEqual(len(validated.frames), 1)

    def test_version_one_accepts_complete_recorded_diagnostic_shapes(self):
        for path in (RED_SESSION, BLUE_SESSION):
            with self.subTest(path=path.name):
                document = json.loads(path.read_text(encoding="utf-8"))
                document["formatVersion"] = CURRENT_FORMAT_VERSION
                validated = validate_document(document)
                self.assertEqual(validated.report.format_version, CURRENT_FORMAT_VERSION)
                self.assertEqual(validated.report.warning_count, 0)

    def test_v1_version_marker_and_validator_cli(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "versioned.json"
            path.write_text(json.dumps({
                "formatVersion": CURRENT_FORMAT_VERSION,
                "sessionID": "v1",
                "width": 640,
                "height": 480,
                "frames": [],
            }), encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(validate_paths([path]), 0)

        command = [sys.executable, str(TOOLS / "validate_phone_saber_metadata.py"), str(LEGACY_SESSION)]
        process = subprocess.run(command, text=True, capture_output=True, check=False)
        self.assertEqual(process.returncode, 0, process.stderr)
        self.assertIn("formatVersion=0", process.stdout)
        self.assertIn("valid with unknown fields", process.stdout)

    def test_missing_frames_array_is_a_structural_error(self):
        with self.assertRaisesRegex(ValueError, "frames array"):
            validate_document({"sessionID": "no-frames"})


if __name__ == "__main__":
    unittest.main()
