import contextlib
import io
import json
import math
from pathlib import Path
import tempfile
import unittest

import endpoint_prediction_eval as replay


class PredictorTests(unittest.TestCase):
    def test_deceleration_axis_damping_preserves_acceleration_and_off(self):
        predictor = replay.Predictor()
        baseline = replay.BaselinePredictor()
        for timestamp, endpoints in ((0,(0,0,0,0)),(.02,(.04,.02,-.04,-.02)),(.04,(.06,.06,-.06,-.06))):
            predictor.add(timestamp,endpoints)
            baseline.add(timestamp,endpoints)
        for expected,actual in zip((.08,.10,-.08,-.10),predictor.predict(.04,40)):
            self.assertAlmostEqual(expected,actual,delta=1e-6)
        self.assertEqual(baseline.predict(.04,0),predictor.predict(.04,0))
        self.assertGreater(baseline.predict(.04,40)[0],predictor.predict(.04,40)[0])

    def test_off_identity_including_signed_zero(self):
        predictor = replay.Predictor()
        predictor.add(0, (1, 1, 1, 1))
        endpoints = tuple(replay.f32(v) for v in (-0.0, 1.234567, 0.125, -2.75))
        predictor.add(0.02, endpoints)
        result = predictor.predict(0.025, 0)
        self.assertEqual(endpoints, result)
        self.assertEqual(-1, math.copysign(1, result[0]))

    def test_constant_velocity_two_and_three_samples(self):
        for count in (2, 3):
            predictor = replay.Predictor()
            for i in range(count):
                t = i * 0.025
                predictor.add(t, (2 * t, -t, 1 - t, 1 + 3 * t))
            target = (2 * (t + 0.04), -(t + 0.04), 1 - (t + 0.04), 1 + 3 * (t + 0.04))
            for expected, actual in zip(target, predictor.predict(t, 40)):
                self.assertAlmostEqual(expected, actual, delta=1e-6)

    def test_clamp_gap_decay_and_restart(self):
        predictor = replay.Predictor(0.25)
        predictor.add(0, (0, 0, 0, 0))
        predictor.add(0.02, (3, 4, -4, 3))
        self.assertEqual(tuple(replay.f32(v) for v in (3.15, 4.2, -4.2, 3.15)), predictor.predict(0.02, 1000))
        self.assertEqual((3, 4, -4, 3), predictor.predict(0.121, 60))
        predictor.add(0.121, (1, 1, 1, 1))
        self.assertEqual((1, 1, 1, 1), predictor.predict(0.121, 60))
        predictor.add(0.141, (1.02, 1, 1, 1))
        self.assertAlmostEqual(1.08, predictor.predict(0.141, 60)[0], delta=1e-6)
        halfway = 0.141 + (replay.DECAY_START + replay.MAX_GAP) / 2
        self.assertAlmostEqual(1.05, predictor.predict(halfway, 60)[0])

    def test_reversal_spike_duplicate_invalid_and_reset(self):
        predictor = replay.Predictor()
        predictor.add(0, (0, 0, 0, 0))
        predictor.add(0.02, (0.02, 0, 0.02, 0))
        predictor.add(0.04, (0.01, 0, 0.2, 0))
        for expected, actual in zip((0.01, 0, 0.24, 0), predictor.predict(0.04, 40)):
            self.assertAlmostEqual(expected, actual, delta=1e-6)
        predictor.add(0.04, (9, 9, 9, 9))
        predictor.add(0.03, (9, 9, 9, 9))
        predictor.add(float("nan"), (9, 9, 9, 9))
        predictor.add(0.06, (float("inf"), 9, 9, 9))
        for expected, actual in zip((0.01, 0, 0.24, 0), predictor.predict(0.04, 40)):
            self.assertAlmostEqual(expected, actual, delta=1e-6)
        predictor.reset()
        predictor.add(1, (1, 1, 1, 1))
        self.assertEqual((1, 1, 1, 1), predictor.predict(1, 60))


class EvaluationTests(unittest.TestCase):
    def test_repeatable_jittered_replay_improves_same_future_target(self):
        samples = replay.synthetic_samples("sinusoid", 60, 3, 6, 140, 42)
        self.assertEqual(samples, replay.synthetic_samples("sinusoid", 60, 3, 6, 140, 42))
        truth = lambda t: replay.synthetic_position("sinusoid", t)
        results = replay.evaluate(samples, truth)
        self.assertEqual(results, replay.evaluate(samples, truth))
        self.assertLess(results[0]["future_rmse"], 1e-6)
        self.assertLess(results[1]["future_rmse"], results[1]["off_future_rmse"])
        self.assertLess(results[3]["live_rmse"], results[0]["live_rmse"])
        self.assertGreater(results[3]["reversal_overshoot_max"], 0)
        self.assertGreater(results[3]["reversal_observations"], 0)
        self.assertLess(results[0]["applied_samples"], len(samples))

    def test_swing_reversal_exposes_overshoot_even_with_lower_latency_error(self):
        samples = replay.synthetic_samples("swing", 30, 3, 0, 140, 42)
        results = replay.evaluate(samples, lambda t: replay.synthetic_position("swing", t))
        self.assertLess(results[0]["reversal_overshoot_max"], 1e-6)
        self.assertGreater(results[-1]["reversal_overshoot_max"], 0)
        self.assertLessEqual(results[-1]["reversal_overshoot_max"], 0.35 + 1e-6)

    def test_csv_truth_interpolation_rejects_missing_future_instead_of_clamping(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "truth.csv"
            path.write_text("capture_s,receive_s,ax,ay,bx,by\n"
                            "1000,1000.14,0,0,0,1\n1000.05,1000.19,0.05,0,0.05,1\n"
                            "1000.1,1000.24,0.1,0,0.1,1\n")
            samples = replay.read_csv(path)
            truth = replay.interpolated_truth(samples)
            self.assertAlmostEqual(0.025, truth(1000.025)[0])
            self.assertIsNone(truth(1000.11))
            results = replay.evaluate(samples, truth)
            self.assertLess(results[-1]["endpoint_observations"], results[0]["endpoint_observations"])
            self.assertIsNone(results[-1]["live_rmse"])
            path.write_text(path.read_text().replace("1000.24", "1000.19"))
            with self.assertRaises(ValueError):
                replay.read_csv(path)

    def test_cli_writes_finite_json_and_reports_all_horizons(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "result.json"
            with contextlib.redirect_stdout(io.StringIO()):
                report = replay.main(["--duration", "2", "--json", str(path)])
            self.assertEqual(report, json.loads(path.read_text()))
            self.assertEqual(4, len(report["cases"]))
            for case in report["cases"]:
                self.assertEqual([0, 20, 40, 60], [r["horizon_ms"] for r in case["results"]])


if __name__ == "__main__":
    unittest.main()
