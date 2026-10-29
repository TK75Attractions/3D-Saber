import unittest

from phone_saber_ambient_study import AmbientStudyState, peak_floor


class AmbientStudyTests(unittest.TestCase):
    def test_constant_night_and_day_keep_existing_floor(self):
        for policy in ("before", "q90-2s", "q90-2s-iso-reset", "attack-decay-2s"):
            for background in (6, 39, 40, 46):
                state = AmbientStudyState(policy)
                for frame in range(180):
                    result = state.update(background, frame / 60, 160)
                    self.assertEqual(result["floorBackground"], background)
                    self.assertEqual(result["peakFloor"], peak_floor(background))

    def test_foreground_drop_preserves_recent_light_without_changing_legacy_gate(self):
        state = AmbientStudyState("q90-2s")
        for frame in range(120):
            state.update(38, frame / 60)
        for frame in range(120, 180):
            result = state.update(18, frame / 60)
        self.assertEqual(result["floorBackground"], 38)
        self.assertLess(result["legacy"], 20)
        self.assertAlmostEqual(result["peakFloor"], 180.5)

    def test_real_dark_transition_eventually_releases_history(self):
        state = AmbientStudyState("q90-2s")
        for frame in range(120):
            state.update(46, frame / 60)
        for frame in range(120, 360):
            result = state.update(6, frame / 60)
        self.assertLess(result["floorBackground"], 8)
        self.assertLess(result["peakFloor"], 24)

    def test_high_quantile_rejects_single_bright_outlier(self):
        state = AmbientStudyState("q90-2s")
        for frame in range(120):
            state.update(6, frame / 60)
        state.update(46, 2)
        for frame in range(121, 180):
            result = state.update(6, frame / 60)
        self.assertLess(result["floorBackground"], 8)

    def test_iso_change_clears_quantile_but_does_not_claim_legacy_ema_is_corrected(self):
        state = AmbientStudyState("q90-2s-iso-reset")
        for frame in range(120):
            state.update(40, frame / 60, 160)
        result = state.update(20, 2, 80)
        self.assertEqual(state.history, [(2, 20)])
        self.assertEqual(result["floorBackground"], 35)
        self.assertEqual(result["legacy"], 35)

    def test_gap_clock_reversal_and_explicit_reset_forget_bright_history(self):
        for timestamp in (10, -1):
            state = AmbientStudyState("q90-2s")
            state.update(46, 0)
            self.assertEqual(state.update(6, timestamp)["floorBackground"], 6)
        state.update(46, 0)
        state.reset()
        self.assertEqual(state.update(6, 0)["floorBackground"], 6)

    def test_missing_background_is_not_imputed(self):
        state = AmbientStudyState("q90-2s")
        self.assertIsNone(state.update(None, 0))
        self.assertEqual(state.update(6, .1)["floorBackground"], 6)

    def test_invalid_observations_fail(self):
        for background, timestamp in ((-1, 0), (256, 0), (6.5, 0), (6, float("nan"))):
            with self.assertRaises(ValueError):
                AmbientStudyState("q90-2s").update(background, timestamp)


if __name__ == "__main__":
    unittest.main()
