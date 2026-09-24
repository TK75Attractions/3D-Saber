import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

import analyze_freeze_timeline as timeline


def frame(frame_id, time, blue=None, red=None, blue_diagnostics=None, red_diagnostics=None):
    return {
        "frameID": frame_id, "presentationTimeSeconds": time,
        "blue": blue or {"detected": False}, "red": red or {"detected": False},
        "candidateDiagnostics": {
            "blue": blue_diagnostics or {}, "red": red_diagnostics or {},
        },
    }


def found(x):
    return {"detected": True, "x1": x, "y1": 0, "x2": x + 20, "y2": 0}


class FreezeTimelineTests(unittest.TestCase):
    def test_events_and_overlap(self):
        core = {"totalCandidateCount": 8, "eligibleCandidateCount": 3,
                "maskPixelCount": 20, "morphologyPixelCount": 15,
                "selectedCandidate": {"sourceType": "core-line", "rawPCASpan": 300,
                                      "robustMainIntervalLength": 50}}
        empty = {"totalCandidateCount": 0, "eligibleCandidateCount": 0,
                 "maskPixelCount": 12, "morphologyPixelCount": 0}
        ineligible = {"totalCandidateCount": 2, "eligibleCandidateCount": 0}
        frames = [
            frame(10, 0.000, found(0), found(10), core),
            frame(11, 0.033, found(0), found(10), core),
            frame(12, 0.066, found(0), found(10), core),
            frame(13, 0.099, found(0), found(10), core),
            frame(14, 0.132, found(212), found(310), core),
            frame(15, 0.165, blue_diagnostics=empty, red_diagnostics=ineligible),
            frame(16, 0.198, blue_diagnostics=empty, red_diagnostics=ineligible),
        ]
        events = timeline.analyze_frames(frames)
        blue_types = {event["eventType"] for event in events if event["color"] == "BLUE"}
        self.assertEqual(blue_types, {
            "identical_endpoint", "selected_core_line", "raw_robust_divergence",
            "endpoint_jump_180", "detected_false", "candidate_zero",
            "mask_present_morphology_zero",
        })
        red_types = {event["eventType"] for event in events if event["color"] == "RED"}
        self.assertIn("endpoint_jump_260", red_types)
        self.assertIn("candidate_present_eligible_zero", red_types)
        identical = next(event for event in events if event["eventType"] == "identical_endpoint" and event["color"] == "BLUE")
        self.assertEqual((identical["frameID"], identical["endFrameID"], identical["frameCount"]), (10, 13, 4))
        self.assertAlmostEqual(identical["durationMs"], 132)
        self.assertEqual(identical["endpoint"], [0.0, 0.0, 20.0, 0.0])
        jump = next(event for event in events if event["eventType"] == "endpoint_jump_180" and event["color"] == "BLUE")
        self.assertEqual(jump["jumpPx"], 212)
        self.assertEqual(jump["candidateCount"], 8)
        self.assertEqual(jump["eligibleCount"], 3)
        incidents = timeline.incidents(events)
        self.assertTrue(any(len(incident["events"]) > 1 for incident in incidents))
        self.assertTrue(all(incident["durationMs"] >= 0 for incident in incidents))

    def test_dropout_breaks_jump_and_short_identical_ignored(self):
        frames = [frame(1, 0, found(0)), frame(2, .03, found(0)),
                  frame(3, .06), frame(4, .09, found(400))]
        kinds = {event["eventType"] for event in timeline.analyze_frames(frames)}
        self.assertNotIn("identical_endpoint", kinds)
        self.assertNotIn("endpoint_jump_180", kinds)
        self.assertIn("detected_false", kinds)

    def test_distinct_short_identical_runs_do_not_merge_into_long_suspect(self):
        frames = [
            frame(1, .00, found(0)), frame(2, .03, found(0)),
            frame(3, .06, found(100)), frame(4, .09, found(100)),
        ]
        kinds = {event["eventType"] for event in timeline.analyze_frames(frames)}
        self.assertNotIn("identical_endpoint", kinds)

    def test_cli_json_and_unity_correlation_unavailable(self):
        with tempfile.TemporaryDirectory() as directory:
            metadata = Path(directory) / "metadata.json"
            unity = Path(directory) / "unity.log"
            metadata.write_text(json.dumps({"sessionID": "sample", "frames": [
                frame(1, 0.0), frame(2, 0.033),
            ]}), encoding="utf-8")
            unity.write_text(
                "[FREEZE][Unity RX][BLUE] gap=212.0ms receive=1234.567890 sinceLastReceive=212.0ms\n"
                "[FREEZE][Unity APPLY][RED] gap=260.0ms apply=1235.000000 receive=1234.990000 receiveAge=10.0ms\n",
                encoding="utf-8",
            )
            command = [sys.executable, str(Path(timeline.__file__)), str(metadata),
                       "--unity-log", str(unity), "--json"]
            process = subprocess.run(command, text=True, capture_output=True, check=True)
            result = json.loads(process.stdout)
            self.assertEqual(result["unityCorrelation"], "correlation unavailable")
            self.assertEqual({gap["eventType"] for gap in result["unityGaps"]},
                             {"unity_rx_gap", "unity_apply_gap"})
            self.assertEqual(len(result["events"]), 2)
            self.assertNotIn("unityStartSeconds", result["incidents"][0])

    def test_invalid_timestamps_rejected(self):
        with self.assertRaisesRegex(ValueError, "increase strictly"):
            timeline.analyze_frames([frame(1, 0), frame(2, 0)])


if __name__ == "__main__":
    unittest.main()
