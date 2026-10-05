"""Synthetic feature/shadow tests; no private PNG or Swift build required."""
import copy
import math
import unittest
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import phone_saber_skin_study as skin


class FeatureTests(unittest.TestCase):
    def test_signed_hue_and_channel_ratios(self):
        features = skin.pixel_features([(200, 40, 60), (100, 20, 30)])
        self.assertAlmostEqual(features['gr_mean'], 0.2)
        self.assertAlmostEqual(features['br_mean'], 0.3)
        self.assertAlmostEqual(features['hue_mean'], -7.5)
        self.assertAlmostEqual(features['saturation_mean'], 0.8)
        self.assertEqual(features['value_p50'], 150)

    def test_black_achromatic_and_empty_are_safe(self):
        self.assertEqual(skin.pixel_features([]), {})
        for value in skin.pixel_features([(0, 0, 0), (255, 255, 255)]).values():
            self.assertTrue(math.isfinite(value))
        self.assertEqual(skin.percentile([1, 3], 0.1), 1.2)
        with self.assertRaises(ValueError):
            skin.percentile([], 0.5)

    def test_auc_orientation_and_ties(self):
        self.assertEqual(skin.auc([3, 4], [1, 2]), 1)
        self.assertEqual(skin.auc([1], [1]), 0.5)
        self.assertEqual(skin.auc([1], [2]), 0)
        self.assertIsNone(skin.auc([], [1]))

    def test_candidate_samples_use_actual_points_and_color_membership(self):
        raw = bytearray([0, 0, 0, 255] * 16)
        raw[0:4] = bytes([20, 40, 200, 255])
        raw[8:12] = bytes([255, 255, 255, 255])
        c = {'study_points': [[0, 0], [1, 0]], 'study_color_flags': [True, False],
             'bounding_box': {'min_x': 0, 'min_y': 0, 'max_x': 2, 'max_y': 0}}
        for key in ('mean_value', 'color_purity', 'clipped_white', 'local_contrast',
                    'core_support', 'longitudinal_core_coverage', 'high_value_ratio',
                    'brightness_variation', 'width_variation', 'axial_density', 'point_count'):
            c[key] = 2
        f = skin.candidate_features(c, bytes(raw), 4, 4)
        self.assertAlmostEqual(f['color_gr_mean'], 0.2)
        self.assertAlmostEqual(f['all_gr_mean'], 0.6)
        self.assertEqual(f['d240'], 480)
        c['axial_density'] = 0
        c['raw_pca_span'] = 4
        self.assertEqual(skin.candidate_features(c, bytes(raw), 4, 4)['d240'], 120)
        c['study_color_flags'] = []
        with self.assertRaises(ValueError):
            skin.candidate_features(c, bytes(raw), 4, 4)

    def test_instrumentation_refuses_ambiguous_source(self):
        with self.assertRaises(ValueError):
            skin.replace_once('x x', 'x', 'y')
        with self.assertRaises(ValueError):
            skin.replace_once('z', 'x', 'y')


class ShadowTests(unittest.TestCase):
    def test_final_candidate_shadow_reselects_without_mutating_blue_or_input(self):
        first = {'eligible': True, 'endpoints': [{'x': 1, 'y': 2}, {'x': 3, 'y': 4}]}
        second = {'eligible': True, 'endpoints': [{'x': 9, 'y': 8}, {'x': 7, 'y': 6}]}
        third = {'eligible': False, 'endpoints': []}
        analysis = {'colors': {'red': {'candidates': [first, second, third], 'selected': first['endpoints']},
                              'blue': {'candidates': [first], 'selected': first['endpoints']}}}
        before = copy.deepcopy(analysis)
        output = skin.filtered(analysis, [{'pass': False}, {'pass': True}, {'pass': True}],
                               predicate=lambda f: f['pass'])
        self.assertEqual(analysis, before)
        self.assertEqual(output['colors']['blue'], before['colors']['blue'])
        self.assertEqual(output['colors']['red']['selected'], second['endpoints'])
        self.assertFalse(output['colors']['red']['candidates'][2]['eligible'])
        output = skin.filtered(analysis, [{}, {}, {}], predicate=lambda f: False)
        self.assertIsNone(output['colors']['red']['selected'])

    def test_shadow_boundary_and_clipped_white_no_exemption(self):
        boundary = {'color_hue_mean': 11, 'core_support': 0}
        self.assertTrue(skin.shadow_accept(boundary))
        self.assertEqual(skin.shadow_score(boundary), 0)
        self.assertTrue(skin.shadow_accept({'color_hue_mean': 20, 'core_support': 0.28}))
        self.assertFalse(skin.shadow_accept({'color_hue_mean': 11.001, 'core_support': 0.27999,
                                            'clipped_white': 1}))

    def test_formal_visual_truth_is_independent_of_expected_window_output(self):
        self.assertEqual(skin.FORMAL_SABER_BOXES['red_dropout_last_true_89'], [425, 600, 480, 640])
        self.assertIn(('formal', 'red_dropout_last_true_89'), skin.SKIN_LIGHT_REGIONS)

    def test_target_labels_include_all_required_originals(self):
        self.assertEqual(set(skin.EXTRA_TRUTHS), {*range(1248, 1256), 3409, 3888, 5449, 6288})
        for frame in range(1248, 1256):
            self.assertIsNone(skin.EXTRA_TRUTHS[frame])


if __name__ == '__main__':
    unittest.main()
