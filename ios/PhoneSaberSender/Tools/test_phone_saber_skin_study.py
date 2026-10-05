"""Synthetic feature/shadow tests; no private PNG or Swift build required."""
import copy
import math
import unittest
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import phone_saber_skin_study as skin
import phone_saber_deep_red_study as deep


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


class DeepRedTests(unittest.TestCase):
    @staticmethod
    def raw(rgb):
        return bytes(channel for r, g, b in rgb for channel in (b, g, r, 255))

    def test_dilation_is_unique_full_resolution_and_clipped(self):
        self.assertEqual(deep.pixel_indices([[0, 0], [1, 0], [1, 0]], 4, 2), [0, 2])
        self.assertEqual(deep.pixel_indices([[0, 0], [1, 0]], 4, 2, 1), list(range(8)))
        with self.assertRaises(ValueError):
            deep.pixel_indices([[2, 0]], 4, 2)

    def test_strict_ratios_inclusive_floor_and_all_pixel_denominator(self):
        grid = [{'gr': 0.45, 'br': 0.55, 'brightness': 'absolute', 'r': 150}]
        raw = self.raw([(200, 90, 0), (200, 0, 110), (150, 30, 30),
                        (149, 0, 0), (255, 255, 255), (0, 0, 0)])
        f = deep.support_features(raw, list(range(6)), grid)
        self.assertEqual(f['counts'], [1])
        self.assertEqual(f['fractions'], [1 / 6])
        self.assertEqual(f['clipped_channels'], 3)
        with self.assertRaises(ValueError):
            deep.support_features(raw, [0, 0], grid)

    def test_relative_floor_uses_candidate_percentile_including_white(self):
        grid = [{'gr': 0.45, 'br': 0.55, 'brightness': 'percentile', 'r': 75}]
        f = deep.support_features(self.raw([(100, 10, 10), (200, 20, 20),
                                          (250, 25, 25), (255, 255, 255)]), list(range(4)), grid)
        self.assertEqual(f['r_percentiles']['75'], 251.25)
        self.assertEqual(f['counts'], [0])
        self.assertEqual(deep.support_features(b'', [], grid)['fractions'], [0])

    def test_uniform_gain_ratios_and_clipping_are_distinguished(self):
        grid = [{'gr': 0.53, 'br': 0.55, 'brightness': 'absolute', 'r': 120}]
        raw = self.raw([(200, 80, 40), (250, 130, 20)])
        self.assertEqual(deep.support_features(raw, [0, 1], grid)['counts'], [2])
        self.assertEqual(deep.support_features(skin.study.apply_gain(raw, 0.92), [0, 1], grid)['counts'], [2])
        clipped = deep.support_features(skin.study.apply_gain(raw, 1.08), [0, 1], grid)
        self.assertEqual(clipped['counts'], [1])
        self.assertEqual(clipped['clipped_channels'], 1)

    def test_shadow_boundary(self):
        f = {'dilate1': {'counts': [5], 'fractions': [0.1]}}
        self.assertTrue(deep.deep_accept(f, 'dilate1', 0, 'counts', 5))
        self.assertFalse(deep.deep_accept(f, 'dilate1', 0, 'fractions', 0.10001))

    def test_warm_fraction_inclusive_boundaries_and_full_domain_denominator(self):
        rgb = [(200, 100, 85), (200, 100, 90), (200, 100, 95),
               (200, 90, 0), (200, 184, 0), (119, 60, 0),
               (200, 89, 0), (200, 185, 0), (255, 255, 255),
               (200, 0, 0), (0, 0, 0)]
        values = deep.support_features(self.raw(rgb), list(range(len(rgb))))
        self.assertEqual(values['warm_counts'], [3, 4, 5])
        self.assertEqual(values['warm_fractions'], [3 / 11, 4 / 11, 5 / 11])

    def test_bright_medians_exclude_dark_and_report_undefined_bg(self):
        rgb = [(200, 100, 100), (255, 255, 255), (100, 50, 0),
               (180, 90, 45), (200, 0, 0)]
        values = deep.support_features(self.raw(rgb), list(range(5)))
        self.assertEqual(values['r120_count'], 4)
        self.assertEqual(values['r120_zero_g'], 1)
        self.assertEqual(values['median_bg'], 1)
        self.assertEqual(values['median_gr'], 0.5)
        empty = deep.support_features(b'', [])
        self.assertIsNone(empty['median_bg'])
        self.assertIsNone(empty['median_gr'])
        self.assertEqual(empty['warm_fractions'], [0, 0, 0])

    def test_warm_gate_requires_both_zero_deep_and_inclusive_warm_fraction(self):
        def features(rgb):
            return {'dilate1': deep.support_features(self.raw(rgb), list(range(len(rgb))))}
        self.assertFalse(deep.warm_accept(features([(200, 100, 90)]), 0.90, 0.7))
        self.assertTrue(deep.warm_accept(features([(200, 100, 91)]), 0.90, 0.7))
        pale = features([(255, 210, 230), (255, 255, 255)])
        self.assertEqual(pale['dilate1']['counts'][deep.SHADOW_GRID_INDEX], 0)
        self.assertTrue(deep.warm_accept(pale, 0.95, 0.3))
        boundary = features([(200, 100, 90), (255, 255, 255)])
        self.assertFalse(deep.warm_accept(boundary, 0.90, 0.5))
        self.assertTrue(deep.warm_accept(boundary, 0.90, 0.500001))
        self.assertTrue(deep.warm_accept(features([(200, 100, 90), (180, 30, 30)]), 0.90, 0.3))

    def test_warm_grid_selects_margin_and_retains_pale_winner(self):
        def row(label, warm, rank):
            values = {'counts': [0] * len(deep.GRID), 'fractions': [0] * len(deep.GRID),
                      'warm_fractions': [warm] * 3, 'median_bg': 1, 'median_gr': 0.8,
                      'r120_zero_g': 0}
            return {'session': 'synthetic', 'frame': 1, 'source': 'labels', 'gain': 1.0,
                    'rank': rank, 'label': label,
                    'candidate': {'eligible': True, 'endpoints': [{'x': rank, 'y': 0}] * 2},
                    'features': {domain: copy.deepcopy(values) for domain in deep.DOMAINS}}
        rows = [row('saber', 0.2, 0), row('skin', 0.8, 1)]
        analysis = {'colors': {'red': {'candidates': [r['candidate'] for r in rows],
                                       'selected': rows[0]['candidate']['endpoints']},
                               'blue': {'candidates': [], 'selected': None}}}
        frame = {'session': 'synthetic', 'frame': 1, 'source': 'labels', 'gain': 1.0,
                 'truth': [0, 0, 10, 10], 'rows': [0, 1], 'formal_base': [], 'analysis': analysis}
        report = {'rows': rows, 'frames': [frame], 'grid': deep.GRID,
                  'domains': deep.DOMAINS, 'gains': [1.0]}
        result = deep.summarize_warm(report, [])
        self.assertEqual(result['chosen'], {'bg': 0.85, 'fraction': 0.5})
        self.assertEqual(result['rules'][1]['gains']['1.0']['correct_output_retention'], [1, 1])
        self.assertEqual(result['rules'][1]['gains']['1.0']['rejected'], {'skin': 1})
        rows[0]['features']['dilate1']['warm_fractions'] = [0.7] * 3
        self.assertIsNone(deep.summarize_warm(report, [])['chosen'])

    def test_warm_retention_requires_same_saber_candidate_at_every_gain(self):
        values = {'counts': [0] * len(deep.GRID), 'fractions': [0] * len(deep.GRID),
                  'warm_fractions': [0.4] * 3, 'median_bg': 1, 'median_gr': 0.8,
                  'r120_zero_g': 0}
        rows, frames = [], []
        for gain in skin.GAINS:
            offset = len(rows)
            for rank, warm in enumerate((0.4 if gain != 1.08 else 0.71, 0.1)):
                features = dict(values, warm_fractions=[warm] * 3)
                rows.append({'session': 'synthetic', 'frame': 1, 'source': 'labels', 'gain': gain,
                             'rank': rank, 'label': 'saber',
                             'candidate': {'eligible': True, 'endpoints': [{'x': rank, 'y': 0}] * 2},
                             'features': {d: copy.deepcopy(features) for d in deep.DOMAINS}})
            red = {'candidates': [r['candidate'] for r in rows[offset:]],
                   'selected': rows[offset]['candidate']['endpoints']}
            frames.append({'session': 'synthetic', 'frame': 1, 'source': 'labels', 'gain': gain,
                           'truth': [0, 0, 10, 10], 'rows': [offset, offset + 1], 'formal_base': [],
                           'analysis': {'colors': {'red': red, 'blue': {'candidates': [], 'selected': None}}}})
        report = {'rows': rows, 'frames': frames, 'grid': deep.GRID,
                  'domains': deep.DOMAINS, 'gains': skin.GAINS}
        result = deep.summarize_warm(report, [])
        gain = result['rules'][0]['gains']['1.0']
        self.assertEqual(gain['correct_retention'], [1, 1])
        self.assertEqual(gain['correct_output_retention'], [0, 1])
        self.assertIsNone(result['chosen'])

    def test_summary_counts_zero_support_sabers_and_background_reselection(self):
        def row(label, count, rank):
            return {'session': 'synthetic', 'frame': 1, 'source': 'labels', 'gain': 1.0,
                    'rank': rank, 'label': label,
                    'candidate': {'eligible': True, 'endpoints': [{'x': rank, 'y': 0}] * 2},
                    'features': {d: {'counts': [count], 'fractions': [count / 10]}
                                 for d in deep.DOMAINS}}
        rows = [row('skin', 0, 0), row('saber', 3, 1), row('saber', 0, 2)]
        red = {'candidates': [r['candidate'] for r in rows], 'selected': rows[0]['candidate']['endpoints']}
        frame = {'session': 'synthetic', 'frame': 1, 'source': 'labels', 'gain': 1.0,
                 'truth': [0, 0, 10, 10], 'rows': [0, 1, 2], 'formal_base': [],
                 'analysis': {'colors': {'red': red, 'blue': {'candidates': [], 'selected': None}}}}
        report = {'rows': rows, 'frames': [frame], 'grid': deep.GRID[:1],
                  'domains': deep.DOMAINS, 'gains': [1.0]}
        summary = deep.summarize(report, [], grid_index=0)
        self.assertEqual(summary['gains']['1.0']['labelled_recall'], [1, 1])
        self.assertEqual(summary['gains']['1.0']['rejected'], {'skin': 1, 'saber': 1})
        self.assertEqual(summary['grid_margins'][0]['positive_min'], 0)
        self.assertEqual(summary['outcomes'][0]['after_rank'], 1)


if __name__ == '__main__':
    unittest.main()
