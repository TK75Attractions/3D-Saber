"""位置評価の定義を既知の幾何・欠測で確認する。非公開データには依存しない。"""
import unittest

import numpy as np

from position_accuracy_eval import angle_delta, errors, stats, windows


class PositionMetricsTests(unittest.TestCase):
    def test_truth_endpoint_order_does_not_change_errors(self):
        actual = [[1, 2, 11, 2]]
        truth = [[0, 0, 10, 0]]
        direct = errors(actual, truth)
        self.assertEqual(direct, errors(actual, [[10, 0, 0, 0]]))
        self.assertAlmostEqual(5 ** .5, direct['endpoint_px']['median'])
        self.assertAlmostEqual(5 ** .5, direct['midpoint_px']['median'])
        self.assertEqual(0, direct['angle_deg']['max'])
        self.assertEqual(0, direct['length_px']['max'])

    def test_length_and_angle_are_separate_from_midpoint(self):
        result = errors([[-10, 0, 10, 0]], [[0, -5, 0, 5]])
        self.assertEqual(0, result['midpoint_px']['max'])
        self.assertEqual(10, result['length_px']['max'])
        self.assertEqual(90, result['angle_deg']['max'])
        np.testing.assert_allclose(angle_delta(np.radians([179, 1]), np.radians([1, 179])), [2, 2])

    def test_nearest_rank_quantiles_and_empty_samples(self):
        self.assertEqual(dict(n=4, median=2.5, p90=10., max=10.), stats([1, 2, 3, 10]))
        self.assertIsNone(stats([])['median'])

    def test_motion_windows_do_not_bridge_frame_holes(self):
        group = dict(t=np.arange(32)/60, frame=np.arange(32),
                     raw=np.tile([0, 0, 10, 0], (32, 1)))
        self.assertEqual(2, len(windows([group])))
        group['frame'][8:] += 1
        self.assertEqual([(0, 16, 32, 0)], windows([group]))


if __name__ == '__main__':
    unittest.main()
