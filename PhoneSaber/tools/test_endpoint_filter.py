import math
from pathlib import Path
import struct
import shutil

import check_endpoint_filter_parity as parity
import unittest

import numpy as np

from endpoint_filter_eval import EndpointFilter, aligned, correspondence, measure, prepare, match


class EndpointFilterEvaluationTests(unittest.TestCase):
    def test_shared_python_and_actual_csharp_vectors(self):
        self.assertEqual(240, parity.check_python(parity.VECTORS))
        if shutil.which('dotnet') is None:
            self.skipTest('installed .NET SDK required for actual C# parity')
        self.assertIn('PASS: 240 samples, 960 float components', parity.check_csharp(parity.VECTORS, 'dotnet'))

    def test_off_preserves_order_and_bits_then_restarts(self):
        filt = EndpointFilter(1, 20, 5)
        filt.apply(0, (0, 0, 1, 1))
        points = (-0.0, 1.234567, 4.5, -2.75)
        self.assertIs(points, filt.apply(.03, points, enabled=False))
        self.assertEqual(struct.pack('d', -0.0), struct.pack('d', points[0]))
        self.assertEqual(points, filt.apply(.04, points))

    def test_swap_matching_is_distance_sum_not_squared_distance(self):
        previous = (0, 0, 2, 0)
        # 距離の和ではdirect、二乗和ではreverse。指示された対応規則を固定する。
        current = (-5, -5, -6, -6)
        self.assertEqual(current, match(previous, current))
        self.assertEqual((2, 0, 0, 0), match((2, 0, 0, 0), (0, 0, 2, 0)))
        self.assertEqual((0, 1, 0, -1), match((-1, 0, 1, 0), (0, 1, 0, -1)))

    def test_constant_step_gap_duplicate_invalid_and_swap(self):
        filt = EndpointFilter(1, 20, 5)
        points = (-1., .25, 1., .5)
        for i in range(20):
            self.assertEqual(points, filt.apply(i / 30, points[2:] + points[:2] if i % 2 else points))
        step = (0., 1., 2., 1.5)
        output = filt.apply(20 / 30, step)
        for before, after, value in zip(points, step, output):
            self.assertLessEqual(before, value)
            self.assertLessEqual(value, after)
        self.assertEqual(output, filt.apply(20 / 30, points))
        self.assertEqual(points, filt.apply(1, points))
        invalid = (math.nan, 0, 1, 0)
        self.assertIs(invalid, filt.apply(1.03, invalid))
        self.assertEqual(step, filt.apply(1.06, step))

    def test_geometry_metrics_ignore_endpoint_order(self):
        raw = [(0, 0, 1, 0), (1, 0, 0, 0), (0, 0, 1, 0)]
        reference = aligned(raw)
        np.testing.assert_array_equal(reference, [(0, 0, 1, 0)] * 3)
        np.testing.assert_array_equal(correspondence(np.asarray(raw), reference), reference)

    def test_metrics_have_fixed_reference_masks_and_positive_filter_lag(self):
        # 遅れは既知の定速合成軌跡、jitterは小標本で評価定義を確認する。
        data, _ = prepare(Path(__file__).with_name('fixtures') / 'real_motion_sample.csv')
        before = measure(data)
        after = measure(data, (1, 20, 5))
        self.assertEqual(before['static_count'], after['static_count'])
        self.assertEqual(before['fast_count'], after['fast_count'])
        filt = EndpointFilter(1, 20, 5)
        output = None
        for i in range(120):
            output = filt.apply(i / 30, (i / 10, 0, i / 10 + 1, 0))
        self.assertGreater(11.9 - output[0], 0)
        self.assertLess((11.9 - output[0]) / 3 * 1000, 5)


if __name__ == '__main__':
    unittest.main()
