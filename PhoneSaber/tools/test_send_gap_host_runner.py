"""旧版比較がコンパイルを通り、解放漏れのアサーションまで到達することを確認。"""
from pathlib import Path
import shutil
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
RUNNER = ROOT / 'PhoneSaber/ios/PhoneSaberSenderTests/run_send_gap_host_tests.py'


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'macOS XCTest が必要')
class SendGapHostRunnerTests(unittest.TestCase):
    def test_revision_without_probe_cleanup_runs_the_regression(self):
        # この修正直前の版を固定し、main の更新後も修正前の失敗を検証する。
        result = subprocess.run([sys.executable, '-B', str(RUNNER), '--revision', 'b487f21'],
                                cwd=ROOT, text=True, capture_output=True, timeout=180)
        output = result.stdout + result.stderr
        self.assertEqual(result.returncode, 1, output)
        self.assertIn("Test Suite 'Send gaps (host; camera/UI/discovery stubs)' started", output)
        self.assertRegex(output, r'Test Case .*testReleasingLANProbeWithoutStopCancelsConnectionAndTimer.* failed')
        self.assertIn('所有側の破棄後に探索タイマーを残さない', output)
        self.assertIn('所有側の破棄後に探索・メトリクス用の LAN 接続を残さない', output)


if __name__ == '__main__':
    unittest.main()
