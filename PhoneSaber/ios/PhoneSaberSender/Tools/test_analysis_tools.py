import importlib.util
import json
import tempfile
import unittest
from pathlib import Path


MODULE_PATH = Path(__file__).with_name("analyze_session_metadata.py")
SPEC = importlib.util.spec_from_file_location("session_analyzer", MODULE_PATH)
assert SPEC and SPEC.loader
analyzer = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(analyzer)


def candidate(source="core-line", raw=300, robust=50):
    return {
        "sourceType": source,
        "rawPCASpan": raw,
        "robustMainIntervalLength": robust,
    }


def frame(frame_id, timestamp, detected, endpoint=None, diagnostics=None):
    blue = {"detected": detected}
    if endpoint:
        blue.update(dict(zip(("x1", "y1", "x2", "y2"), endpoint)))
    return {
        "frameID": frame_id,
        "presentationTimeSeconds": timestamp,
        "blue": blue,
        "red": {"detected": False},
        "candidateDiagnostics": {"blue": diagnostics or {}, "red": {}},
    }


class SessionAnalyzerTests(unittest.TestCase):
    def test_dropout_stuck_jump_and_candidate_counts(self):
        selected = {
            "eligibleCandidateCount": 1,
            "maskPixelCount": 20,
            "morphologyPixelCount": 10,
            "selectedCandidate": candidate(),
        }
        empty = {
            "eligibleCandidateCount": 0,
            "totalCandidateCount": 0,
            "maskPixelCount": 12,
            "morphologyPixelCount": 0,
        }
        frames = [
            frame(1, 0.000, True, (0, 0, 20, 0), selected),
            frame(2, 0.033, True, (0, 0, 20, 0), selected),
            frame(3, 0.066, True, (0, 0, 20, 0), selected),
            frame(4, 0.099, True, (0, 0, 20, 0), selected),
            frame(5, 0.132, False, diagnostics=empty),
            frame(6, 0.165, False, diagnostics=empty),
            frame(7, 0.198, True, (300, 0, 320, 0), selected),
        ]
        result = analyzer.analyze_color(frames, "blue")
        self.assertEqual(result["dropout_runs"], 1)
        self.assertEqual(result["dropout_length_counts"]["2"], 1)
        self.assertAlmostEqual(result["longest_dropout_milliseconds"], 66.0, places=3)
        self.assertEqual(result["stuck_suspect_runs_4_or_more"], 1)
        self.assertEqual(result["jumps_260_or_more"], 0,
                         "reacquisition after a dropout is not a frame-to-frame jump")
        self.assertEqual(result["core_line_selected"], 5)
        self.assertEqual(result["raw_robust_large_divergence"], 5)
        self.assertEqual(result["eligible_candidate_zero_frames"], 2)
        self.assertEqual(result["total_candidate_zero_frames"], 2)
        self.assertEqual(result["mask_present_morphology_zero_frames"], 2)

    def test_consecutive_detected_endpoint_jump(self):
        frames = [
            frame(1, 0.000, True, (0, 0, 20, 0)),
            frame(2, 0.033, True, (300, 0, 320, 0)),
        ]
        result = analyzer.analyze_color(frames, "blue")
        self.assertEqual(result["jumps_180_or_more"], 1)
        self.assertEqual(result["jumps_260_or_more"], 1)

    def test_missing_timestamp_keeps_detection_but_makes_dropout_duration_unknown(self):
        frames = [frame(1, 0.0, False), frame(2, None, False)]
        result = analyzer.analyze_color(frames, "blue")
        self.assertEqual(result["not_detected_frames"], 2)
        self.assertEqual(result["longest_dropout_frames"], 2)
        self.assertIsNone(result["longest_dropout_milliseconds"])

    def test_file_and_comparison_json(self):
        frames = [frame(1, 0.0, False), frame(2, 0.033, True, (0, 0, 10, 0))]
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "metadata.json"
            path.write_text(json.dumps({"sessionID": "test", "frames": frames}))
            result = analyzer.analyze_file(path)
        self.assertEqual(result["session_id"], "test")
        self.assertFalse(result["prediction_bridge"]["available"])
        compared = analyzer.comparison(result, result)
        self.assertEqual(compared["colors"]["blue"]["detection_rate_percent"]["delta"], 0)


if __name__ == "__main__":
    unittest.main()
