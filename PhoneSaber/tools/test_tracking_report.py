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
    @staticmethod
    def target_report():
        metrics = dict(p95_percent=30, rms_percent=20, lag_component_percent=20, jitter_percent=1)
        off = {str(hz): dict(metrics) for hz in set(tracking_report.FREQUENCIES)}
        after = {hz: dict(value, p95_percent=20, rms_percent=15, lag_component_percent=9) for hz, value in off.items()}
        return {"results": [{"kind": "off", "by_frequency": off},
                            {"kind": "adaptive", "by_frequency": after}]}

    def test_targets_require_every_speed_and_still_measurement(self):
        report = self.target_report()
        self.assertEqual(tracking_report.check_targets(report), [])
        for missing in ("0.0", "2.5"):
            with self.subTest(missing=missing):
                partial = self.target_report()
                for item in partial["results"]:
                    del item["by_frequency"][missing]
                self.assertIn(missing + "Hz 未測定", tracking_report.check_targets(partial))

    def test_targets_reject_nonfinite_metrics_in_both_modes(self):
        for mode in range(2):
            for hz, key in (("0.0", "jitter_percent"), ("0.5", "lag_component_percent"),
                            ("1.5", "rms_percent"), ("2.5", "p95_percent")):
                for value in (None, float("nan"), float("inf")):
                    with self.subTest(mode=mode, hz=hz, key=key, value=value):
                        report = self.target_report()
                        report["results"][mode]["by_frequency"][hz][key] = value
                        self.assertIn(hz + "Hz " + key + "未測定", tracking_report.check_targets(report))

    def test_ablation_cannot_silently_ignore_replay_or_target_options(self):
        for option in (("--check-targets", "--lead-ms", "106"), ("--lead-ms", "106"),
                       ("--render-hz", "120"), ("--candidate-maximum-ms", "90"),
                       ("--before-ref", "main"), ("--compare-ref", "main")):
            with self.subTest(option=option), mock.patch("sys.argv", ["tracking_report.py", "missing-predict3-probe.csv", "--actual-csharp", "--ablation-ref", "ea7848d", *option]):
                with contextlib.redirect_stderr(io.StringIO()), contextlib.redirect_stdout(io.StringIO()), self.assertRaises(SystemExit) as error:
                    tracking_report.main()
                self.assertEqual(error.exception.code, 2)

    def test_partial_still_segment_prints_unmeasured_instead_of_nan_or_crashing(self):
        report = dict(lag_ms=106, calibration="test", residual_percent=0, travel_world=1, excluded_frames=0,
                      results=[dict(kind="off", lead_ms=0, jerk_rms_world_s3=0,
                                    by_frequency={"0.0": dict(jitter_percent=None, jitter_with_transition_percent=1)})])
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            tracking_report.print_csharp(report)
        self.assertIn("静止jitter=-", output.getvalue())

    def test_target_does_not_hide_correlated_residual_by_subtracting_rms_squares(self):
        # 総RMSと残差が相関すると二乗差は小さくなり、遅延半減を誤ってPASSにしていた。
        off = {"p95_percent": 30, "lag_excess_percent": 20, "lag_component_percent": 20}
        after = {"p95_percent": 20, "lag_excess_percent": 5, "lag_component_percent": 11}
        report = self.target_report()
        report["results"][0]["by_frequency"]["0.5"] = off
        report["results"][1]["by_frequency"]["0.5"] = after
        self.assertEqual(tracking_report.check_targets(report), ["0.5Hz lag-component > 0.5x OFF"])

    def test_rounded_turning_point_is_not_lost_between_zero_steps(self):
        times = np.arange(8) * .01
        command = np.array([.6, .7, .8, .8, .8, .7, .6, .5])
        np.testing.assert_allclose(tracking_report.turning_times(times, command), [.03])

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

    def test_perspective_fit_removes_distortion_without_fitting_away_lag(self):
        frames = np.array([(t, target(t)) for t in np.arange(0, 64, 1 / 120)])
        times = np.arange(.5, 63, 1 / 60)
        reference = np.interp(times - .106, frames[:, 0], frames[:, 1])
        expected = np.array([1.4, -.12, 1.2])
        values = tracking_report.mapping(reference, expected)
        lag, fit, correlation = tracking_report.fit_mapping(times, values, frames[:, 0], frames[:, 1])
        self.assertAlmostEqual(lag, 106, delta=.5)
        self.assertGreater(correlation, .9999)
        self.assertLess(np.std(values - tracking_report.mapping(reference, fit)), .001)
        affine_lag, affine, _ = tracking_report.fit_mapping(times, values, frames[:, 0], frames[:, 1], "affine")
        self.assertGreater(np.std(values - tracking_report.mapping(reference, affine)), .04)
        self.assertEqual(affine_lag, tracking_report.best_lag(times, values, frames[:, 0], frames[:, 1])[0])
        grid = tracking_report.mapping(np.linspace(.2, .8, 101), fit)
        self.assertTrue(np.all(np.diff(grid) > 0))

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
