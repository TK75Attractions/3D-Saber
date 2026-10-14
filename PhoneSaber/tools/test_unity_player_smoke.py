"""実アプリを起動せず、Playerログによる配布gateを検証する。"""
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

SOURCE = Path(__file__).with_name('unity_copy.sh')
SUCCESS = '[PhoneSaberSmoke] done exceptions=0 errors=0 input=ok cuts=3 flow=ok\n'
PERF = ''.join('[PhoneSaberSmoke] perf scene=' + scene + ' fps=60.0 frame-ms-max=17\n'
               for scene in ('Title', 'SongSelect', 'Game', 'Result'))


class PlayerSmokeGateTests(unittest.TestCase):
    def smoke(self, log, mode="batch"):
        source = Path(os.environ.get('PHONESABER_SMOKE_TEST_SOURCE', SOURCE)).read_text()
        function = 'smoke_player() {' + source.split('smoke_player() {', 1)[1].split('\n}\n', 1)[0] + '\n}\n'
        with tempfile.TemporaryDirectory(prefix='player-smoke-gate-j-') as tmp:
            root = Path(tmp)
            exe = root / 'test.app/Contents/MacOS/player'
            exe.parent.mkdir(parents=True)
            exe.write_text('never launched')
            exe.chmod(0o700)
            payload = root / 'payload'
            payload.write_text(log)
            command = function + '''
run_hidden() { printf '%s\\n' "$*" > "$ARGS"; cp "$PAYLOAD" "$OUT/smoke.log"; }
function /usr/bin/open() { printf '%s\\n' "$*" > "$ARGS"; cp "$PAYLOAD" "$OUT/smoke.log"; }
smoke_player "$APP" "$MODE"
'''
            result = subprocess.run(['/bin/bash', '-c', command], text=True, capture_output=True, timeout=10,
                                    env=dict(os.environ, OUT=str(root), APP=str(root / 'test.app'),
                                             PAYLOAD=str(payload), ARGS=str(root / 'args'), MODE=mode))
            return result, (root / 'args').read_text()

    def test_complete_result_prints_each_scenes_perf_and_uses_batchmode(self):
        result, args = self.smoke(PERF + SUCCESS)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn('-batchmode', args)
        for scene in ('Title', 'SongSelect', 'Game', 'Result'):
            self.assertIn('perf scene=' + scene, result.stdout)

    def test_window_mode_opens_background_window_without_batchmode(self):
        result, args = self.smoke(PERF + SUCCESS, mode="window")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn('smoke-window:', result.stdout)
        self.assertIn('-g -n -W', args)
        self.assertIn('-screen-fullscreen 0', args)
        self.assertNotIn('-batchmode', args)
        self.assertNotIn('-j', args.split())

    def test_legacy_success_cannot_certify_error_cut_result_or_calibration_checks(self):
        result, _ = self.smoke('shader initialization failed\n[PhoneSaberSmoke] done exceptions=0 input=ok\n')
        self.assertNotEqual(0, result.returncode, result.stdout)

    def test_error_exception_input_cut_and_flow_fail_closed(self):
        for summary in (SUCCESS.replace('errors=0', 'errors=1'),
                        SUCCESS.replace('exceptions=0', 'exceptions=1'),
                        SUCCESS.replace('input=ok', 'input=FAILED'),
                        SUCCESS.replace('cuts=3', 'cuts=0'),
                        SUCCESS.replace('flow=ok', 'flow=FAILED')):
            with self.subTest(summary=summary):
                result, _ = self.smoke(PERF + summary)
                self.assertNotEqual(0, result.returncode, result.stdout)

    def test_missing_scene_perf_is_failure(self):
        result, _ = self.smoke(PERF.replace('[PhoneSaberSmoke] perf scene=Result fps=60.0 frame-ms-max=17\n', '') + SUCCESS)
        self.assertNotEqual(0, result.returncode, result.stdout)

    def test_stopped_coroutine_cannot_pass_without_completion(self):
        result, _ = self.smoke(PERF + '[PhoneSaberSmoke] input: mode=game received=True\n')
        self.assertNotEqual(0, result.returncode, result.stdout)

    def test_multiple_completion_lines_cannot_hide_a_failure(self):
        for first in (SUCCESS, SUCCESS.replace('errors=0', 'errors=1')):
            with self.subTest(first=first):
                result, _ = self.smoke(PERF + first + SUCCESS)
                self.assertNotEqual(0, result.returncode, result.stdout)


if __name__ == '__main__':
    unittest.main()
