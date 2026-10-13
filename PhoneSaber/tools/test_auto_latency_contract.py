"""自動計測の共有fixtureを所有する契約テスト。実端末の遅延を証明しない。"""
import csv
import os
from pathlib import Path
import shutil
import subprocess
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / 'PhoneSaber/tools/fixtures/protocol_metrics.tsv'
KEYS = {'station', 'platform', 'app', 'fps-mode', 'capture-fps', 'send-red-fps', 'send-blue-fps',
        'c2s-median-ms', 'c2s-p95-ms', 'c2s-n', 'rtt-ms', 'rtt-n', 'rtt-age-s',
        'route', 'thermal', 'battery', 'low-latency-test', 'c2s-end'}


class AutoLatencyContractTests(unittest.TestCase):
    def test_fixture_has_both_platforms_missing_data_and_adversarial_cases(self):
        with FIXTURE.open(encoding='utf-8') as handle:
            rows = list(csv.DictReader(handle, delimiter='\t'))
        self.assertEqual(len(rows), len({row['id'] for row in rows}))
        valid = [row for row in rows if row['format'] == '1']
        self.assertEqual({row['id'] for row in valid}, {'ios', 'android', 'missing'})
        for row in valid:
            self.assertLess(len(row['line'].encode('ascii')), 400)
            self.assertEqual(row['line'].split()[:2], ['PHONESABER_METRICS', '1'])
            self.assertEqual({f.split('=', 1)[0] for f in row['line'].split()[2:]}, KEYS)
        self.assertGreaterEqual(sum(row['valid'] == '0' for row in rows), 12)

    @unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'macOS Swift/Unity Mono が必要')
    def test_real_swift_csharp_shared_fixture_and_loopback(self):
        env = dict(os.environ, CLANG_MODULE_CACHE_PATH='/private/tmp/phonesaber-auto-latency-clang')
        result = subprocess.run([sys.executable, str(ROOT / 'PhoneSaber/tools/check_auto_latency.py')],
                                cwd=ROOT, env=env, text=True, capture_output=True, timeout=120)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn('5 PASS / 0 FAIL', result.stdout)
        self.assertIn('Swift -> actual C# discovery Loop metrics=PASS', result.stdout)


if __name__ == '__main__':
    unittest.main()
