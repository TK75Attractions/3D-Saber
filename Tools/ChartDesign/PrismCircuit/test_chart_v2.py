"""生成結果だけでなく、実際に壊した譜面を監査が拒否することを確認。"""
import copy
import collections
import unittest
from chart_v2 import BEAT, DIFFICULTIES, build, validate


class ChartFlowTests(unittest.TestCase):
    def setUp(self):
        self.chart, self.sources = build('hard')

    def test_three_difficulties_are_deterministic_and_trace_every_cut(self):
        for diff in DIFFICULTIES:
            with self.subTest(diff=diff):
                chart, sources = build(diff)
                self.assertEqual((chart, sources), build(diff))
                validate(chart, diff, sources)

    def test_rejects_audio_grid_drift(self):
        self.chart['notes'][10]['time'] += 20
        with self.assertRaisesRegex(ValueError, 'timing'):
            validate(self.chart, 'hard')

    def test_rejects_two_notes_for_one_hand_at_once(self):
        ns = self.chart['notes']
        i = next(i for i in range(1, len(ns)) if ns[i]['beat'] == ns[i-1]['beat'])
        ns[i]['color'] = ns[i-1]['color']
        with self.assertRaisesRegex(ValueError, 'pair hands'):
            validate(self.chart, 'hard')

    def test_rejects_gold_without_recovery_for_either_hand(self):
        ns = self.chart['notes']
        n = next(n for n in ns if n['beat'] == 97.5)
        n.update(color='gold', x=0, direction='none', type='tap')
        with self.assertRaisesRegex(ValueError, 'hand interval'):
            validate(self.chart, 'hard')

    def test_rejects_forced_direction_after_free_gold(self):
        ns = self.chart['notes']
        i = next(i for i, n in enumerate(ns) if n['color'] == 'gold' and n['count'] == 1)
        ns[i+1].update(type='direction', direction='down')
        with self.assertRaisesRegex(ValueError, 'gold forced exit'):
            validate(self.chart, 'hard')

    def test_rejects_note_inside_gold_roll(self):
        roll = next(n for n in self.chart['notes'] if n['type'] == 'long')
        n = copy.deepcopy(roll)
        n.update(beat=roll['beat']+1, time=round((roll['beat']+1)*BEAT*1000, 3), type='tap', count=1, lengthMs=0)
        self.chart['notes'].append(n)
        self.chart['notes'].sort(key=lambda n: (n['beat'], n['color']))
        with self.assertRaisesRegex(ValueError, 'long conflict'):
            validate(self.chart, 'hard')

    def test_rejects_same_direction_reset(self):
        seq = [n for n in self.chart['notes'] if n['color'] in ('blue', 'gold')]
        a, b = next((a, b) for a, b in zip(seq, seq[1:]) if a['direction'] != 'none' and b['direction'] != 'none')
        b['direction'] = a['direction']
        with self.assertRaisesRegex(ValueError, 'double direction'):
            validate(self.chart, 'hard')

    def test_rejects_different_heights_in_a_double(self):
        ns = self.chart['notes']
        i = next(i for i in range(1, len(ns)) if ns[i]['beat'] == ns[i-1]['beat'])
        ns[i]['y'] += .2
        with self.assertRaisesRegex(ValueError, 'pair height'):
            validate(self.chart, 'hard')

    def test_rejects_low_up_and_high_down_arrows(self):
        for prefix, y in (('up', -.5), ('down', .5)):
            with self.subTest(direction=prefix):
                chart = copy.deepcopy(self.chart)
                n = next(n for n in chart['notes'] if n['direction'].startswith(prefix))
                n['y'] = y
                with self.assertRaisesRegex(ValueError, 'arrow height'):
                    validate(chart, 'hard')

    def test_rejects_flat_repeated_layouts(self):
        for n in self.chart['notes']:
            n.update(x={'blue': -1.1, 'red': 1.1, 'gold': 0}[n['color']], y=0, direction='none')
            if n['type'] == 'direction':
                n['type'] = 'tap'
        with self.assertRaisesRegex(ValueError, 'repeated layout|layout variety'):
            validate(self.chart, 'hard')

    def test_rejects_inward_handclap(self):
        counts = collections.Counter(n['beat'] for n in self.chart['notes'])
        note = next(n for n in self.chart['notes'] if n['color'] == 'blue' and counts[n['beat']] == 2)
        note['type'] = 'direction'
        note['direction'] = 'downright'
        with self.assertRaisesRegex(ValueError, 'inward pair'):
            validate(self.chart, 'hard')

    def test_rejects_unrelated_sound_provenance(self):
        self.sources[0]['sourceEventIds'] = [0]
        with self.assertRaisesRegex(ValueError, 'source timing'):
            validate(self.chart, 'hard', self.sources)

    def test_rejects_gold_being_removed_from_the_whole_song(self):
        self.chart['notes'] = [n for n in self.chart['notes'] if n['color'] != 'gold' or n['count'] > 1]
        with self.assertRaisesRegex(ValueError, 'gold budget'):
            validate(self.chart, 'hard')


if __name__ == '__main__':
    unittest.main()
