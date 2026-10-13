import contextlib
import io
import math
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import numpy as np

import latency_matrix
import tracking_report


def target(t):
    phase, whole = 0.0, int(t // tracking_report.SEGMENT)
    for i in range(whole):
        phase += 2 * math.pi * tracking_report.FREQUENCIES[i % len(tracking_report.FREQUENCIES)] * tracking_report.SEGMENT
    phase += 2 * math.pi * tracking_report.FREQUENCIES[whole % len(tracking_report.FREQUENCIES)] * (t - whole * tracking_report.SEGMENT)
    return 0.5 + 0.3 * math.sin(phase)


def synthetic(path, lag, rate=60.0, seconds=66.0, noise=0.0, angle=1.1):
    random = np.random.default_rng(3)
    lines = ["# display=test", "# header"]
    events = [(i / 60.0, "F") for i in range(int(seconds * 60))] + [(lag + 0.2 + i / rate, "R") for i in range(int((seconds - lag - 0.3) * rate))]
    for t, kind in sorted(events):
        if kind == "F":
            lines.append(f"F,{t:.6f},{target(t):.5f}")
        else:
            # スマホが傾いていても主軸へ射影して読めること（棒は主軸に直交する向きに長い）。
            s = (target(t - lag) - 0.5) * 1.4 + random.normal(0, noise)
            x, y = s * math.cos(angle), s * math.sin(angle)
            dx, dy = -0.25 * math.sin(angle), 0.25 * math.cos(angle)
            lines.append(f"R,{t:.6f},{x - dx:.5f},{y - dy:.5f},{x + dx:.5f},{y + dy:.5f}")
    Path(path).write_text("\n".join(lines) + "\n", encoding="utf-8")


class TrackingReportTests(unittest.TestCase):
    def test_lag_is_recovered_for_a_tilted_phone(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "tracking-test.csv"
            synthetic(path, lag=0.104, noise=0.004)
            frames, packets = tracking_report.load(path)
            lag, correlation = tracking_report.best_lag(packets[:, 0], tracking_report.project(packets), frames[:, 0], frames[:, 1])
            self.assertAlmostEqual(lag, 104, delta=4)
            self.assertGreater(correlation, 0.99)

    def test_report_runs_and_prediction_beats_hold_on_clean_motion(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "tracking-test.csv"
            synthetic(path, lag=0.100, noise=0.0)
            output = io.StringIO()
            with contextlib.redirect_stdout(output), mock.patch("sys.argv", ["tracking_report.py", str(path)]):
                self.assertEqual(tracking_report.main(), 0)
            self.assertIn("見た目の遅れ: 100 ms", output.getvalue())
            frames, packets = tracking_report.load(path)
            times, values = packets[:, 0], tracking_report.project(packets)
            reference = np.interp(times - 0.1, frames[:, 0], frames[:, 1])
            scale, offset = np.polyfit(reference, values, 1)
            results = tracking_report.evaluate(frames, times, values, scale, offset, ("hold", "linear3+age"), (60,))
            hold = next(item for item in results if item["kind"] == "hold")
            predicted = next(item for item in results if item["kind"] == "linear3+age")
            # 2 Hz 以上の往復も含む全体の値なので差は小さいが、雑音なしなら先読みのほうが必ず小さい。
            self.assertLess(predicted["rms_moving"], hold["rms_moving"] * 0.9)

    def test_too_little_data_is_reported_not_raised(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "tracking-short.csv"
            path.write_text("# x\nF,0.0,0.5\nR,0.1,0,0,0,0\nbroken,line\n", encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()), mock.patch("sys.argv", ["tracking_report.py", str(path)]):
                self.assertEqual(tracking_report.main(), 1)


class LatencyMatrixTests(unittest.TestCase):
    def test_summary_reads_blocks_estimates_and_display(self):
        lines = [
            "t station=A colour=RED event=latency-block block=0 median-ms=120 p95-ms=150 n=15 red-pkt-per-s=30.0",
            "t station=A colour=RED event=latency-block block=1 median-ms=130 p95-ms=150 n=15 red-pkt-per-s=30.0",
            "t station=A colour=RED event=latency-block block=2 median-ms=999 p95-ms=999 n=3 red-pkt-per-s=30.0",
            "t station=A colour=RED event=display-measured status=valid display-ms=60.0",
            "t station=A colour=RED event=display-measured status=rejected display-ms=NaN",
            "t station=A colour=RED event=latency-estimate status=estimate platform=ios c2s-median-ms=44.0 network-ms=2.0 thermal=serious fps-mode=30.0",
            "t station=A colour=BLUE event=latency-estimate status=estimate platform=ios c2s-median-ms=1.0 network-ms=1.0 thermal=fair fps-mode=60.0",
            "t station=A colour=RED event=latency-loop-end median-ms=128 p95-ms=204 n=45 misses=1 seen-x=-0.7..-0.3 seen-y=-0.4..0.1",
        ]
        summary = latency_matrix.summarise(lines)
        self.assertEqual(summary["loop"], 125)
        self.assertEqual((summary["samples"], summary["misses"], summary["loop_p95"]), (45, 1, 204))
        self.assertEqual((summary["c2s"], summary["network"], summary["display"]), (44.0, 2.0, 60.0))
        self.assertEqual((summary["thermal"], summary["fps_mode"]), (["serious"], ["30.0"]))

    def test_empty_run_has_no_numbers(self):
        summary = latency_matrix.summarise([])
        self.assertIsNone(summary["loop"])
        self.assertEqual(summary["samples"], 0)


if __name__ == "__main__":
    unittest.main()
