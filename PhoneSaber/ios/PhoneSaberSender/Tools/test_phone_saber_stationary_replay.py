"""候補保存順・打切り・production選択履歴の回帰。"""
import json
from pathlib import Path
import tempfile
import unittest

import phone_saber_stationary_replay as replay


class StationaryReplayTests(unittest.TestCase):
    def test_rank_order_eligibility_and_observed_candidates(self):
        columns = ['rank', 'x1', 'y1', 'x2', 'y2', 'minX', 'minY', 'maxX', 'maxY',
                   'score', 'flags', 'source']
        def candidate(rank, flags):
            return [rank, 100, 100, 100, 180, 96, 100, 104, 180, 80, flags, 0] + [0] * 8
        a, b, rejected = candidate(1, 7), candidate(0, 3), candidate(2, 0)
        frame = {'f': 1, 't': 1, 'red': {'c': [a, b, rejected], 'e': 2, 'n': 3, 'sel': a[1:5]},
                 'blue': {'c': [], 'e': 0, 'n': 0, 'sel': None}}
        with tempfile.TemporaryDirectory() as directory:
            directory = Path(directory)
            summary = {'autoClip': {'candidateColumns': columns, 'frames': [frame],
                                   'frameSize': {'width': 480, 'height': 640}, 'playArea': [0, 0, 1, 1]}}
            (directory / 'summary.json').write_text(json.dumps(summary))
            result = replay.load_clip(directory)['frames'][0]
            self.assertEqual([c[0] for c in result['red']], [0, 1])
            self.assertEqual(len(result['observed']['red']), 3)
            self.assertEqual(result['seed']['red'], a[1:5])
            frame['red']['e'] = 3  # 保存リスト欠落は確定したreplayにしない。
            (directory / 'summary.json').write_text(json.dumps(summary))
            result = replay.load_clip(directory)['frames'][0]
            self.assertTrue(result['reset'])
            self.assertEqual(result['red'], [])

    def test_production_continuous_selection(self):
        replay.run([], self_test=True)

    def test_comparison_counts_missing_outputs_and_changed_winners(self):
        frames = [{'f': i, 'red': [[0, 100, 100, 100, 180]], 'blue': [],
                   'recorded': {'red': [10, 10, 10, 30], 'blue': None}} for i in range(3)]
        before = [{'frames': [{'red': {'sel': [10, 10, 10, 30]}, 'blue': {'sel': None}} for _ in frames]}]
        after = [{'frames': [{'red': {'sel': e}, 'blue': {'sel': None}}
                             for e in ([100, 100, 100, 180], None, [10, 10, 10, 30])]}]
        result = replay.comparison([{'name': 'synthetic', 'frames': frames}], before, after, [10, 20, 5])[0]['red']
        self.assertEqual((result['changed'], result['lost'], result['gained']), (2, 1, 0))
        self.assertEqual((result['staticBefore'], result['staticAfter']), (3, 1))
        self.assertEqual(result['baselineRecordedMismatch'], 0)


if __name__ == '__main__':
    unittest.main()
