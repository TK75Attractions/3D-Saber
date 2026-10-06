import json
import tempfile
import unittest
from pathlib import Path

import compare_phone_saber_sessions as comparison
import analyze_freeze_timeline as freeze_timeline
import analyze_session_metadata as session_metadata


def frame(frame_id, time, *, red_success=None, blue_success=None,
          red_detected=None, blue_detected=None, red_predicted=False,
          blue_predicted=False, red_type=None, blue_type=None,
          red_candidates=None, blue_candidates=None):
    value = {
        "frameID": frame_id,
        "presentationTimeSeconds": time,
        "candidateDiagnostics": {
            "red": red_candidates or {},
            "blue": blue_candidates or {},
        },
    }
    for color, success, detected, predicted, source in (
        ("red", red_success, red_detected, red_predicted, red_type),
        ("blue", blue_success, blue_detected, blue_predicted, blue_type),
    ):
        if success is not None:
            value[f"{color}DetectionSucceeded"] = success
        detection = {}
        if detected is not None:
            detection["detected"] = detected
        if predicted:
            detection["predicted"] = True
        if detected:
            detection.update({"x1": 10, "y1": 20, "x2": 30, "y2": 40})
        value[color] = detection
        if source:
            value["candidateDiagnostics"][color] = {
                **value["candidateDiagnostics"][color],
                "selectedCandidate": {"sourceType": source},
            }
    return value


def write_metadata(path, frames):
    path.write_text(json.dumps({"sessionID": path.stem, "frames": frames}), encoding="utf-8")


class ComparePhoneSaberSessionsTests(unittest.TestCase):
    def test_actual_detection_excludes_prediction_and_unknown_breaks_runs(self):
        frames = [
            frame(1, 0.0, red_success=True, red_detected=True, blue_success=True, blue_detected=True),
            frame(2, 0.03, red_success=False, red_detected=True, red_predicted=True,
                  blue_success=False, blue_detected=False,
                  blue_candidates={"totalCandidateCount": 2, "eligibleCandidateCount": 0}),
            frame(3, 0.06, red_detected=None, blue_success=True, blue_detected=True),
        ]
        result = session_metadata.analyze_color(frames, "red")
        self.assertEqual(result["frames_with_detection_status"], 2)
        self.assertEqual(result["unknown_detection_status_frames"], 1)
        self.assertEqual(result["detected_frames"], 1)
        self.assertEqual(result["detection_rate_percent"], 50.0)
        self.assertEqual(result["dropout_runs"], 1)
        self.assertEqual(result["longest_dropout_frames"], 1)
        blue = session_metadata.analyze_color(frames, "blue")
        self.assertEqual(blue["candidate_present_eligible_zero_frames"], 1)

    def test_freeze_log_summary_and_udp_comparison(self):
        with tempfile.TemporaryDirectory() as directory:
            baseline_log = Path(directory) / "baseline.log"
            experimental_log = Path(directory) / "experimental.log"
            baseline_log.write_text(
                "[FREEZE_DIAG][SUMMARY] cameraGap p50/p95/max=10.0/12.0/15.0ms "
                "queueWait p50/p95/max=1.0/2.0/3.0ms detection p50/p95/max=5.0/6.0/8.0ms "
                "mainDelay p50/p95/max=2.0/3.0/4.0ms frames=150\n"
                "[FREEZE_DIAG][UDP] sendGap=120.0ms color=RED queueWait=0.1ms replaced=0\n"
                "[FREEZE_DIAG][UDP] sendGap=200.0ms color=BLUE queueWait=0.2ms replaced=0\n",
                encoding="utf-8",
            )
            experimental_log.write_text(
                "[FREEZE_DIAG][SUMMARY] cameraGap p50/p95/max=20.0/22.0/25.0ms "
                "queueWait p50/p95/max=2.0/3.0/4.0ms detection p50/p95/max=6.0/7.0/9.0ms "
                "mainDelay p50/p95/max=3.0/4.0/5.0ms frames=150\n"
                "[FREEZE_DIAG][UDP] sendGap=240.0ms color=BLUE queueWait=0.2ms replaced=0\n",
                encoding="utf-8",
            )
            baseline = freeze_timeline.read_freeze_diagnostics(baseline_log)
            experimental = freeze_timeline.read_freeze_diagnostics(experimental_log)
        self.assertEqual(baseline["window_aggregate"]["cameraGap"]["p95_ms"], 12.0)
        self.assertEqual(baseline["udp_send_gap_event_count"], 2)
        self.assertEqual(baseline["udp_send_gap_by_color"]["BLUE"]["max_send_gap_ms"], 200.0)
        diff = comparison.compare_freeze(baseline, experimental)
        self.assertEqual(diff["metrics"]["cameraGap.p50_ms"]["delta"], 10.0)
        self.assertEqual(diff["metrics"]["udp_blue_max_send_gap_ms"]["percent_change"], 20.0)

    def test_session_report_json_metrics_and_ground_truth_note(self):
        with tempfile.TemporaryDirectory() as directory:
            baseline_path = Path(directory) / "baseline.json"
            experimental_path = Path(directory) / "experimental.json"
            write_metadata(baseline_path, [
                frame(1, 0.0, red_success=True, red_detected=True, red_type="core-line",
                      red_candidates={"totalCandidateCount": 1, "eligibleCandidateCount": 1}),
                frame(2, 0.03, red_success=False, red_detected=False,
                      red_candidates={"totalCandidateCount": 0, "eligibleCandidateCount": 0}),
            ])
            write_metadata(experimental_path, [
                frame(1, 0.0, red_success=True, red_detected=True, red_type="connected-core",
                      red_candidates={"totalCandidateCount": 2, "eligibleCandidateCount": 1}),
                frame(2, 0.03, red_success=True, red_detected=True, red_type="connected-core",
                      red_candidates={"totalCandidateCount": 1, "eligibleCandidateCount": 1}),
            ])
            result = comparison.compare_sessions(baseline_path, experimental_path)
        red = result["comparison"]["colors"]["red"]
        self.assertEqual(red["detection_rate_percent"]["delta"], 50.0)
        self.assertEqual(red["detection_rate_percent"]["percent_change"], 100.0)
        self.assertEqual(red["candidate_type:core-line"]["delta"], -1)
        self.assertEqual(red["candidate_type:connected-core"]["delta"], 2)
        self.assertIn("outside the camera view", result["ground_truth"]["object_presence"])
        self.assertFalse(result["comparison"]["freeze"]["available"])


if __name__ == "__main__":
    unittest.main()
