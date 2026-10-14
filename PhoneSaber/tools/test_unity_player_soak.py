"""アプリは起動せず、soakの集計・隠し起動・タイムアウト対象を検証。"""
import os
import signal
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest import mock

from unity_soak_summary import summarise
from unity_soak_stop import matches
import unity_soak_stop

SOURCE = Path(__file__).with_name('unity_copy.sh')
ROW = ('2026-10-14T00:00:00.000Z station=soak colour=BOTH event=soak loop=1 '
       'fps=60.0 worst-frame-ms=20 red-packets=120 blue-packets=120 '
       'title-ms=1000 select-ms=1200 load-game-ms=1800 game-ms=40000 return-ms=1000 '
       'managed-bytes=100000 allocated-bytes=200000 reserved-bytes=300000 '
       'AudioSource=4 Material=20 Texture2D=8 Mesh=10 GameObject=100 MonoBehaviour=100 SaberInputBridge=2 InputPoint=1 '
       'red-active=True blue-active=True red-moved=True blue-moved=True input=ok flow=ok '
       'exceptions=0 errors=0 messages=""\n')
DONE = '[PhoneSaberSoak] done loops=1 exceptions=0 leak=insufficient errors=0 input=ok flow=ok\n'


class SoakSummaryTests(unittest.TestCase):
    def test_short_pass_explicitly_says_leaks_unverified(self):
        code, text = summarise(ROW + DONE)
        self.assertEqual(0, code)
        self.assertIn('未判定', text)

    def test_fail_closed_on_exception_error_input_flow_growth_or_missing_completion(self):
        for log in (ROW, ROW + DONE + DONE, DONE,
                    ROW + DONE.replace('exceptions=0', 'exceptions=1'),
                    ROW + DONE.replace('errors=0', 'errors=1'),
                    ROW + DONE.replace('input=ok', 'input=FAILED'),
                    ROW + DONE.replace('flow=ok', 'flow=FAILED'),
                    ROW + DONE.replace('insufficient', 'growth:Material'),
                    ROW + DONE.replace('insufficient', 'invalid'),
                    ROW.replace('red-moved=True', 'red-moved=False') + DONE,
                    ROW.replace('errors=0', 'errors=1') + DONE,
                    ROW.replace('red-packets=120', 'red-packets=0') + DONE,
                    ROW.replace('managed-bytes=100000 ', '') + DONE,
                    ROW.replace('fps=60.0', 'fps=NaN') + DONE):
            with self.subTest(log=log):
                self.assertEqual(1, summarise(log)[0])

    def test_missing_repeated_or_out_of_order_loop_is_failure(self):
        done = DONE.replace('loops=1', 'loops=2')
        row2 = ROW.replace('loop=1', 'loop=2')
        self.assertEqual(0, summarise(ROW + row2 + done)[0])
        for rows in (ROW, ROW + ROW, row2 + ROW):
            self.assertEqual(1, summarise(rows + done)[0])

    def test_truncated_log_cannot_be_certified_by_old_success(self):
        self.assertEqual(1, summarise('[PhoneSaberSmoke] done exceptions=0 input=ok\n')[0])

    def test_completion_must_follow_samples_and_match_the_sample_count(self):
        rows = ''.join(ROW.replace('loop=1 ', f'loop={i} ') for i in range(1, 12))
        done = DONE.replace('loops=1 ', 'loops=11 ')
        for log in (DONE + ROW, ROW + DONE.replace('insufficient', 'stable'), rows + done):
            with self.subTest(log=log):
                self.assertEqual(1, summarise(log)[0])
        self.assertEqual(0, summarise(rows + done.replace('insufficient', 'stable'))[0])

    def test_stop_never_matches_other_run_or_normal_game(self):
        self.assertTrue(matches('player -phonesaberSoak 2 -logFile /tmp/soak.log', '/tmp/soak.log'))
        self.assertFalse(matches('player -phonesaberSoak 2 -logFile /tmp/other.log', '/tmp/soak.log'))
        self.assertFalse(matches('player -logFile /tmp/soak.log', '/tmp/soak.log'))
        self.assertFalse(matches('player -phonesaberSoak 2 -logFile /tmp/soak.log.bak', '/tmp/soak.log'))
        self.assertTrue(matches('player -phonesaberSoak 2 -logFile "/tmp/space dir/soak.log"', '/tmp/space dir/soak.log'))
        self.assertTrue(matches('player -phonesaberSoak 2 -logFile /tmp/space dir/soak.log -phonesaberSoakGameSeconds 40', '/tmp/space dir/soak.log'))


class SoakShellTests(unittest.TestCase):
    def run_shell(self, log=ROW + DONE, minutes='2', cleanup_failure=False):
        source = SOURCE.read_text()
        function = 'soak_player() {' + source.split('soak_player() {', 1)[1].split('\n}\n', 1)[0] + '\n}\n'
        with tempfile.TemporaryDirectory(prefix='soak-shell-') as tmp:
            root = Path(tmp)
            app = root / 'test.app'
            (app / 'Contents/MacOS').mkdir(parents=True)
            payload = root / 'payload'
            payload.write_text(log)
            command = function + '''
function /usr/bin/open() { printf '%s\\n' "$*" > "$ARGS"; cp "$PAYLOAD" "$log"; }
if [[ "$CLEANUP_FAILURE" == 1 ]]; then rm() { return 1; }; fi
soak_player "$MINUTES" "$APP"
'''
            result = subprocess.run(['/bin/bash', '-c', command], text=True, capture_output=True, timeout=10,
                                    env=dict(os.environ, OUT=str(root), APP=str(app), ROOT=str(SOURCE.resolve().parents[2]),
                                             MINUTES=minutes, PAYLOAD=str(payload), ARGS=str(root / 'args'),
                                             CLEANUP_FAILURE=str(int(cleanup_failure))))
            return result, (root / 'args').read_text() if (root / 'args').exists() else ''

    def test_hidden_batch_launch_and_summary(self):
        result, args = self.run_shell()
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn('-g -j -n -W', args)
        self.assertIn('-batchmode -phonesaberSoak 2', args)
        self.assertIn('判定=PASS', result.stdout)

    def test_cleanup_failure_and_bad_duration_never_launch(self):
        for kwargs in ({'cleanup_failure': True}, {'minutes': 'NaN'}, {'minutes': '0'}, {'minutes': '1441'}):
            result, args = self.run_shell(**kwargs)
            self.assertNotEqual(0, result.returncode)
            self.assertEqual('', args)

    def test_crash_and_legacy_player_fail(self):
        for log in ('', 'old player\n', ROW + DONE.replace('input=ok', 'input=FAILED')):
            result, _ = self.run_shell(log)
            self.assertNotEqual(0, result.returncode)


class SoakStopTests(unittest.TestCase):
    def test_reused_pid_is_not_force_killed(self):
        player = subprocess.CompletedProcess([], 0, stdout=
            '10 /tmp/test.app/Contents/MacOS/player -phonesaberSoak 2 -logFile /tmp/soak.log\n')
        reused = subprocess.CompletedProcess([], 0, stdout='10 /usr/bin/unrelated\n')
        with mock.patch.object(unity_soak_stop.sys, 'argv', ['stop', '/tmp/soak.log']), \
             mock.patch.object(unity_soak_stop.subprocess, 'run', side_effect=[player, reused]), \
             mock.patch.object(unity_soak_stop.os, 'kill') as kill, \
             mock.patch('time.sleep'), mock.patch('time.monotonic', side_effect=range(100)):
            self.assertEqual(0, unity_soak_stop.main())
        self.assertNotIn(mock.call(10, signal.SIGKILL), kill.call_args_list)

    def test_unresponsive_player_is_killed_and_other_runs_are_untouched(self):
        listing = subprocess.CompletedProcess([], 0, stdout=(
            '10 /tmp/test.app/Contents/MacOS/player -phonesaberSoak 2 -logFile /tmp/soak.log\n'
            '11 /tmp/test.app/Contents/MacOS/player -phonesaberSoak 2 -logFile /tmp/other.log\n'
            '12 /tmp/test.app/Contents/MacOS/player -logFile /tmp/soak.log\n'))
        signals = []
        def kill(pid, sig):
            if sig == 0 and (10, signal.SIGKILL) in signals:
                raise ProcessLookupError()
            signals.append((pid, sig))
        with mock.patch.object(unity_soak_stop.sys, 'argv', ['stop', '/tmp/soak.log']), \
             mock.patch.object(unity_soak_stop.subprocess, 'run', return_value=listing), \
             mock.patch.object(unity_soak_stop.os, 'kill', side_effect=kill), \
             mock.patch('time.sleep'), \
             mock.patch('time.monotonic', side_effect=range(100)):
            self.assertEqual(0, unity_soak_stop.main())
        self.assertIn((10, signal.SIGTERM), signals)
        self.assertIn((10, signal.SIGKILL), signals)
        self.assertTrue(all(pid == 10 for pid, _ in signals))

    def test_inaccessible_process_inventory_is_failure(self):
        with mock.patch.object(unity_soak_stop.sys, 'argv', ['stop', '/tmp/soak.log']), \
             mock.patch.object(unity_soak_stop.subprocess, 'run', return_value=subprocess.CompletedProcess([], 1, stdout='')), \
             mock.patch.object(unity_soak_stop.os, 'kill') as kill:
            self.assertEqual(1, unity_soak_stop.main())
            kill.assert_not_called()


if __name__ == '__main__':
    unittest.main()
