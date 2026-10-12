import copy
import unittest

import recognition_census as census
from test_coreline_shadow import candidate


def geometry(x, score=100, length=40):
    return {"finalOutputEndpoints": [x, 0, x+length, 0], "finalScore": score,
            "rawPCASpan": length, "centroid": [x+length/2, 0], "bbox": [x, 0, x+length, 4]}


class RecognitionCensusTests(unittest.TestCase):
    def test_failure_example_keeps_null_winner_after_rejection_audit(self):
        rejected = candidate("color-mask", eligible=False, peak_value=255, mean_value=230,
                             production={"compact_red": True, "double_bits": ["0"]*7})
        row = dict(id="test", truth={"red": "saber", "blue": "unknown"},
                   boxes={"red": [0, 0, 100, 4]}, colors={
                       "red": {"selected": None, "candidates": [rejected]},
                       "blue": {"selected": None, "candidates": []}})
        result = census.census([row])["red"]
        self.assertEqual(result["box_census"]["all_matching_ineligible"], 1)
        self.assertIsNone(result["failures"][0]["winner"])

    def test_endpoint_order_does_not_change_metrics(self):
        expected = [{"x": 0, "y": 0}, {"x": 100, "y": 0}]
        metrics = census.endpoint_metrics(list(reversed(expected)), expected)
        self.assertEqual(metrics, dict(length_ratio=1, angle_deg=0, endpoint_error_px=0))

    def test_angle_has_no_directed_axis(self):
        expected = [{"x": 0, "y": 0}, {"x": 100, "y": 0}]
        actual = [{"x": 0, "y": 0}, {"x": -100, "y": 1}]
        self.assertLess(census.endpoint_metrics(actual, expected)["angle_deg"], 1)

    def test_source_specific_floor_preserves_other_sources(self):
        mask = candidate("color-mask", high_value_ratio=.44, mean_value=220, peak_value=255,
                         shadow_candidate_index=0)
        halo = candidate("core-halo", high_value_ratio=.30, mean_value=210, peak_value=255,
                         shadow_candidate_index=1)
        row = dict(colors={"red": {"candidates": [mask, halo]}}, area_pixels=307200)
        before = copy.deepcopy(row)
        self.assertEqual(census.eligible(row, "red", {"red_hv": .45, "red_sources": ["color-mask"]}), [halo])
        self.assertEqual(row, before)

    def test_ties_preserve_original_candidate_order(self):
        a = candidate("color-mask", mean_value=240, peak_value=255, shadow_candidate_index=1)
        b = candidate("core-halo", mean_value=240, peak_value=255, shadow_candidate_index=0)
        row = dict(colors={"red": {"candidates": [b, a]}}, area_pixels=307200)
        self.assertEqual(census.eligible(row, "red", {})[0], b)

    def test_strict_geometry_does_not_match_large_span_to_tiny_foreground(self):
        self.assertFalse(census.strict_match(geometry(180, length=40), geometry(0, length=400)))
        self.assertFalse(census.strict_match(geometry(100), geometry(0)))
        self.assertTrue(census.strict_match(geometry(25), geometry(0)))

    def test_endpoint_jump_catches_stretch_with_same_midpoint(self):
        self.assertEqual(census.endpoint_jump(geometry(0, length=40), geometry(-100, length=240)), 100)

    def test_override_expires_and_never_outputs_previous_coordinates(self):
        initial = geometry(0)
        winner = geometry(200, score=101)
        moving_alternative = geometry(2, score=100)
        seq = {("test", "red"): [(0, [initial]), (1, [winner, moving_alternative]),
                                  (2, [winner, moving_alternative])]}
        _, chosen = census.tracking_replay(seq, 3, 1, True)
        self.assertIs(chosen["test", "red", 1], moving_alternative)
        self.assertIs(chosen["test", "red", 2], winner)

    def test_disappearance_and_sole_candidate_release_immediately(self):
        previous, sole = geometry(0), geometry(200)
        seq = {("test", "red"): [(0, [previous]), (1, []), (2, [sole])]}
        _, chosen = census.tracking_replay(seq, 10, 3, True)
        self.assertIsNone(chosen["test", "red", 1])
        self.assertIs(chosen["test", "red", 2], sole)

    def test_nonconsecutive_windows_do_not_inherit_state(self):
        first, winner, alternative = geometry(0), geometry(200, score=101), geometry(0, score=100)
        seq = {("test", "red"): [(0, [first]), (2, [winner, alternative])]}
        result, chosen = census.tracking_replay(seq, 3, 3, True)
        self.assertEqual(result["counts"]["red"].get("changes", 0), 0)
        self.assertIs(chosen["test", "red", 2], winner)


if __name__ == "__main__":
    unittest.main()
