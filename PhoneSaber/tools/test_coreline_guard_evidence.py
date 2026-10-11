"""証拠抽出が診断JSONの表記差を認識変更と数えないことを検証する。"""
from pathlib import Path
import unittest

from coreline_guard_evidence import identity, key


class GuardEvidenceTests(unittest.TestCase):
    def test_absent_and_null_snapshot_are_the_same_selected_candidate(self):
        candidate = {'source':'core-line','score':80,'endpoints':[{'x':10,'y':20},{'x':30,'y':40}]}
        self.assertEqual(key(candidate),key({**candidate,'coreLineGuard':None}))
        self.assertNotEqual(key(None),key(candidate))

    def test_same_endpoints_with_different_candidate_bits_are_a_change(self):
        candidate = {'source':'core-line','endpoints':[{'x':10,'y':20},{'x':30,'y':40}],
                     'production':{'double_bits':['3ff0000000000000']}}
        self.assertNotEqual(key(candidate),key({**candidate,
            'production':{'double_bits':['3ff0000000000001']}}))

    def test_inbox_event_suffix_is_the_real_frame_id(self):
        path = Path('/inbox/phonesaber_20261010_163949_658/images/image_08_motion_event_1000000000_peak_21952.png')
        self.assertEqual(identity(path,{},'digest'),('phonesaber_20261010_163949_658',21952))


if __name__ == '__main__':
    unittest.main()
