"""フレーム上限の旧実装検証が、別の失敗を回帰再現と誤認しない。"""
import contextlib
import io
from pathlib import Path
import subprocess
import sys
import unittest
from unittest import mock

import check_frame_cap as check


BEFORE = ('FAIL PersistedFourTimesAppliesAfterEverySceneRewriteWithoutChangingVSync expected 240\n'
          'FAIL TwoTimesAndExplicitVSyncRemainIndependent expected 120\n'
          'NUnit host 1 PASS / 2 FAIL (Unity native/UI/prefs/rendering substituted; build-settings test omitted)\n')


class FrameCapRegressionGateTests(unittest.TestCase):
    def run_check(self, before, code=1, stderr=''):
        def execute(command, **kwargs):
            output, status = '', 0
            if Path(command[1]).name == 'before.exe':
                output, status = before, code
            elif Path(command[1]).name == 'after.exe':
                output = 'NUnit host 15 PASS / 0 FAIL\n'
            return subprocess.CompletedProcess(command, status, output, stderr)

        with mock.patch.object(sys, 'argv', ['check_frame_cap.py', '--before-ref', 'baseline']), \
                mock.patch.object(check.subprocess, 'check_output', return_value=b'// baseline'), \
                mock.patch.object(check.subprocess, 'run', side_effect=execute), \
                contextlib.redirect_stdout(io.StringIO()):
            return check.main()

    def test_expected_two_frame_cap_failures_are_accepted(self):
        self.assertEqual(0, self.run_check(BEFORE))
        lines = BEFORE.splitlines(keepends=True)
        self.assertEqual(0, self.run_check(lines[1] + lines[0] + lines[2]))

    def test_unrelated_assertion_or_host_crash_is_not_regression_evidence(self):
        for output in ('Unhandled Exception: host crashed\n',
                       'FAIL DefaultDoesNotWriteEitherGameSetting\nNUnit host 2 PASS / 1 FAIL\n',
                       BEFORE.replace('TwoTimesAndExplicitVSyncRemainIndependent', 'DefaultDoesNotWriteEitherGameSetting'),
                       BEFORE + 'FAIL DefaultDoesNotWriteEitherGameSetting\n',
                       BEFORE.replace('1 PASS / 2 FAIL', '0 PASS / 3 FAIL')):
            with self.subTest(output=output):
                self.assertNotEqual(0, self.run_check(output))

    def test_regression_output_with_wrong_exit_status_is_rejected(self):
        for code in (0, 2, -9):
            with self.subTest(code=code):
                self.assertNotEqual(0, self.run_check(BEFORE, code))

    def test_crash_after_expected_assertions_is_rejected(self):
        self.assertNotEqual(0, self.run_check(BEFORE, stderr='Unhandled Exception: benchmark crashed\n'))


if __name__ == '__main__':
    unittest.main()
