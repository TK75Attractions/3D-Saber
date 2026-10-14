import unittest

import numpy as np

from hit_feel_replay import CSharpReplay, ROOT, world_packets
import tracking_report


class HitFeelReplayTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.replay = CSharpReplay()

    @classmethod
    def tearDownClass(cls):
        cls.replay.close()

    def test_unity_adaptive_tests_use_an_existing_asset_with_metadata(self):
        path = ROOT / 'Assets/Tests/Editor/PhoneSaberEndpointPredictorTests.cs'
        self.assertIn('public class PhoneSaberAdaptivePredictionTests', path.read_text())
        self.assertTrue(path.with_suffix('.cs.meta').is_file())
        self.assertFalse(path.with_name('PhoneSaberAdaptivePredictionTests.cs').exists())

    def test_adaptive_parity_cli_rejects_a_single_changed_float_bit(self):
        import check_endpoint_predictor_parity as parity
        from unittest import mock
        import contextlib
        import io
        import subprocess
        import tempfile
        from pathlib import Path
        rows = parity.ADAPTIVE_VECTORS.read_text().splitlines()
        for index, line in enumerate(rows):
            row = line.split(',')
            if row[0] == 'predict' and int(row[7], 16) & 0x7fffffff:
                row[7] = f'{int(row[7], 16) ^ 1:08x}'
                rows[index] = ','.join(row)
                break
        else:
            self.fail('非ゼロの自動予測fixtureがありません')
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'changed-bit.csv'
            path.write_text('\n'.join(rows) + '\n')
            with mock.patch.object(parity, 'ADAPTIVE_VECTORS', path), mock.patch('sys.argv', ['check_endpoint_predictor_parity.py']):
                with self.subTest(gate='CLI'), contextlib.redirect_stdout(io.StringIO()), self.assertRaises(subprocess.CalledProcessError):
                    parity.main()
            import os
            editor = Path(os.environ.get('PHONESABER_UNITY_CONTENTS', '/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents'))
            nunit = editor / 'Resources/PackageManager/BuiltInPackages/com.unity.ext.nunit/net40/unity-custom/nunit.framework.dll'
            source = (ROOT / 'Assets/Tests/Editor/PhoneSaberEndpointPredictorTests.cs').read_text()
            source = source[:source.index('public class PhoneSaberEndpointPredictorTests')] + source[source.index('public class PhoneSaberAdaptivePredictionTests'):]
            source = source.replace('Application.dataPath', 'HostPaths.dataPath')
            fixture = Path(directory) / 'PhoneSaber/tools/fixtures/endpoint_catchup_vectors.csv'
            fixture.parent.mkdir(parents=True)
            fixture.write_text(path.read_text())
            assets = Path(directory) / 'Assets'
            assets.mkdir()
            program = '''using System;
public static class HostPaths { public static string dataPath; }
class Program {
    static void Main(string[] args) {
        HostPaths.dataPath = args[0];
        var test = new PhoneSaberAdaptivePredictionTests();
        foreach (var method in test.GetType().GetMethods())
            if (method.Name.StartsWith("SharedVectorsMatchStandalone")) { method.Invoke(test, null); return; }
        throw new Exception("共有fixtureテストがありません");
    }
}
'''
            replay = CSharpReplay(program=program, extra_sources={'Tests.cs': source}, references=(nunit,))
            try:
                with self.subTest(gate='Unity test'):
                    with self.assertRaises(subprocess.CalledProcessError) as error:
                        subprocess.check_output(['dotnet', str(replay.dll), str(assets)], env=replay.env, stderr=subprocess.STDOUT)
                    self.assertIn('予測0 成分0', error.exception.output.decode())
            finally:
                replay.close()

    def test_operator_prediction_guidance_matches_the_runtime_limits(self):
        import subprocess
        source = (ROOT / 'Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberOperatorOverlay.cs').read_text()
        start = source.index('        GUILayout.Label("初期値はOFF。')
        end = source.index('\n    }', start)
        # 実際に表示するLabelを実行する。GUI自体は開かない。
        program = '''using System; using System.Globalization;
class Program {
    static object textStyle;
    static void Main() {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
''' + source[start:end] + '''
    }
}
static class GUILayout { public static void Label(string text, object style) { Console.WriteLine(text); } }
'''
        replay = CSharpReplay(program=program)
        try:
            output = subprocess.check_output(['dotnet', str(replay.dll)], env=replay.env, text=True)
        finally:
            replay.close()
        self.assertIn('手動: 最大60 ms・移動上限0.35 world', output)
        self.assertIn('自動: 最大120 ms・移動上限2 world', output)
        self.assertIn('スマホ＋通信＋PC＋画面＋露光', output)
        self.assertIn('受信が100 ms止まると予測量はゼロ', output)

    def test_direct_mapping_moves_both_endpoints_equally_and_clamps_midpoint(self):
        rows = np.array([[0, .8, -.2, .6, .2], [1, -.2, .1, .2, .3]])
        mapped = world_packets(rows)
        middle = (mapped[:, 1:3] + mapped[:, 3:5]) / 2
        np.testing.assert_allclose(middle, [[5.5, 0], [0, 1.2]], atol=1e-6)
        np.testing.assert_allclose(mapped[:, 3:5] - mapped[:, 1:3], (rows[:, 3:5] - rows[:, 1:3]) * [5.5, 3], atol=1e-6)

    def test_mailbox_coalesces_samples_that_arrive_before_one_update(self):
        packets = np.array([[0, 0, -.5, 0, .5], [.01, .1, -.5, .1, .5], [.02, .2, -.5, .2, .5], [.03, .3, -.5, .3, .5]])
        # 一回のUpdateに4packetが来ても、予測履歴は1点。起動直後の跳ねを延長しない。
        output = self.replay.run([.04], packets)
        np.testing.assert_allclose(output[0, 1:], packets[-1, 1:], atol=1e-6)

    def test_actual_bridge_clamp_preserves_length_at_the_play_area_edge(self):
        output = self.replay.run([0], np.array([[0, 6, -.5, 8, .5]]), mode='off')
        np.testing.assert_allclose(output[0, 1:], [4.5, -.5, 6.5, .5])

    def test_clean_fast_sweep_error_is_lower_than_production_predictor(self):
        times = np.arange(0, 4, 1 / 60)
        receive = np.arange(0, 4, 1 / 30)
        x = 2.2 * np.sin(2 * np.pi * 2 * (receive - .112))
        packets = np.column_stack((receive, x, x * 0 - .5, x, x * 0 + .5))
        current = self.replay.run(times, packets, mode='current')[:, 1]
        adaptive = self.replay.run(times, packets)[:, 1]
        truth = 2.2 * np.sin(2 * np.pi * 2 * times)
        use = times > .5
        self.assertLess(np.mean((adaptive[use] - truth[use]) ** 2), np.mean((current[use] - truth[use]) ** 2))

    def test_prediction_never_extends_after_a_hundred_ms_dropout(self):
        receive = np.arange(4) / 30
        x = receive * 4
        packets = np.column_stack((receive, x, x * 0 - .5, x, x * 0 + .5))
        output = self.replay.run([.1, .15, .199, .201, .4], packets)
        np.testing.assert_allclose(output[-2:, 1], x[-1], atol=1e-6)
        self.assertTrue(np.all(abs(output[:, 1] - x[-1]) <= 2.000001))

    def test_note_model_runs_in_update_independently_of_physics_rate(self):
        receive = np.arange(0, 1, 1 / 30)
        x = np.clip((receive - .112 - .4) * 10, -3, 3)
        packets = np.column_stack((receive, x, x * 0 - .5, x, x * 0 + .5))
        times = np.arange(0, 1, 1 / 900)
        first = self.replay.run(times, packets, mode='off', physics_hz=25)
        second = self.replay.run(times, packets, mode='off', physics_hz=100)
        np.testing.assert_equal(first[:, :8], second[:, :8])
        self.assertEqual(int(first[:, 5].sum()), 1)
        self.assertGreater(first[first[:, 5] == 1, 0][0], .4 + .112)

    def test_actual_csharp_report_has_all_speeds_and_world_units(self):
        from test_tracking_report import synthetic
        import tempfile
        from pathlib import Path
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'probe.csv'
            synthetic(path, .112, rate=30, seconds=66)
            frames, packets = tracking_report.load(path)
            report = tracking_report.evaluate_csharp(frames, packets, self.replay, leads=(60,))
        self.assertGreater(report['travel_world'], 0)
        self.assertEqual(len(report['results']), 3)
        still = report['results'][0]['by_frequency']['0.0']
        self.assertLess(still['jitter_percent'], .001, "無雑音の静止に直前の運動をjitterとして混ぜない")
        self.assertGreater(still['rms_percent'], .01, "切替直後の誤差は全frame RMSには残す")
        for item in report['results']:
            self.assertEqual(len(item['by_frequency']), 7)
            self.assertGreater(item['jerk_rms_world_s3'], 0)
            for metrics in item['by_frequency'].values():
                self.assertAlmostEqual(metrics['rms_world'] / report['travel_world'] * 100, metrics['rms_percent'])

    def test_before_report_does_not_label_legacy_output_as_adaptive(self):
        from test_tracking_report import synthetic
        import tempfile
        from pathlib import Path
        replay = CSharpReplay('main')
        try:
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / 'probe.csv'
                synthetic(path, .112, rate=30, seconds=9)
                frames, packets = tracking_report.load(path)
                report = tracking_report.evaluate_csharp(frames, packets, replay, leads=(60,))
            self.assertEqual([item['kind'] for item in report['results']], ['off', 'current'])
            with self.assertRaises(ValueError):
                replay.run([0], np.array([[0, 0, -.5, 0, .5]]), mode='adaptive')
        finally:
            replay.close()

    def test_csharp_options_require_actual_csharp(self):
        from unittest import mock
        import contextlib
        import io
        for option in (['--before-ref', 'main'], ['--render-hz', '120'], ['--candidate-maximum-ms', '90'],
                       ['--ablation-ref', 'ea7848d'], ['--compare-ref', 'ea7848d'], ['--lead-ms', '106'], ['--check-targets']):
            with self.subTest(option=option), mock.patch('sys.argv', ['tracking_report.py'] + option):
                with contextlib.redirect_stderr(io.StringIO()), contextlib.redirect_stdout(io.StringIO()), self.assertRaises(SystemExit) as error:
                    tracking_report.main()
                self.assertEqual(error.exception.code, 2)

    def test_comparison_cli_keeps_old_adaptive_and_rejects_partial_target_capture(self):
        from test_tracking_report import synthetic
        from unittest import mock
        import contextlib
        import io
        import json
        import tempfile
        from pathlib import Path
        with tempfile.TemporaryDirectory() as directory:
            path, output = Path(directory) / 'probe.csv', Path(directory) / 'report.json'
            synthetic(path, .106, seconds=8.1)
            args = ['tracking_report.py', str(path), '--actual-csharp', '--lead-ms', '106',
                    '--compare-ref', 'ea7848d', '--mapping', 'affine', '--check-targets', '--json', str(output)]
            with mock.patch('sys.argv', args), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(tracking_report.main(), 1)
            report = json.loads(output.read_text())
        self.assertIn('affine fit', report['calibration'])
        self.assertEqual([item['lead_ms'] for item in report['results']], [0, 20, 40, 60, 106])
        self.assertEqual([item['kind'] for item in report['before']['results']], ['off', 'current', 'adaptive'])
        self.assertEqual(report['before']['results'][-1]['lead_ms'], 60)
        self.assertIn('2.5Hz 未測定', report['target_failures'])

    def test_ablation_cli_builds_each_old_constraint_separately(self):
        from test_tracking_report import synthetic
        from unittest import mock
        import contextlib
        import io
        import json
        import tempfile
        from pathlib import Path
        with tempfile.TemporaryDirectory() as directory:
            path, output = Path(directory) / 'probe.csv', Path(directory) / 'report.json'
            synthetic(path, .106, seconds=8.1)
            args = ['tracking_report.py', str(path), '--actual-csharp', '--ablation-ref', 'ea7848d', '--json', str(output)]
            with mock.patch('sys.argv', args), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(tracking_report.main(), 0)
            report = json.loads(output.read_text())
        self.assertEqual(set(report), {'baseline', 'no-displacement-cap', 'no-horizon-cap', 'no-decay',
                                       'no-conservative', 'no-confidence', 'unlimited'})
        off = report['baseline']['results'][0]
        for item in report.values():
            self.assertEqual(item['results'][0], off)
            self.assertEqual(item['results'][-1]['kind'], 'adaptive')
        self.assertNotEqual(report['no-displacement-cap']['results'][1], report['baseline']['results'][1])

    def test_candidate_limit_cannot_silently_apply_to_before_build(self):
        with self.assertRaises(ValueError):
            CSharpReplay('main', candidate_maximum_ms=90)

    def test_candidate_limits_change_only_adaptive_replay(self):
        packets = np.array([[i / 30, i / 3, -.5, i / 3, .5] for i in range(13)])
        times = np.arange(13) / 30
        current = self.replay.run(times, packets, 120, 'current')
        adaptive = self.replay.run(times, packets, 120, 'adaptive')
        for limit in (60, 90, 120):
            replay = CSharpReplay(candidate_maximum_ms=limit)
            try:
                np.testing.assert_equal(replay.run(times, packets, 120, 'current'), current)
                candidate = replay.run(times, packets, 120, 'adaptive')
                if limit == 120:
                    np.testing.assert_equal(candidate, adaptive)
                else:
                    self.assertLess(candidate[-1, 1], adaptive[-1, 1])
            finally:
                replay.close()

    def test_csharp_cli_writes_candidate_and_before_reports_at_requested_rate(self):
        from test_tracking_report import synthetic
        from unittest import mock
        import contextlib
        import io
        import json
        import tempfile
        from pathlib import Path
        with tempfile.TemporaryDirectory() as directory:
            path, output = Path(directory) / 'probe.csv', Path(directory) / 'report.json'
            synthetic(path, .112, rate=30, seconds=8.1)
            for options, kinds in ((['--candidate-maximum-ms', '90'], {'off', 'current', 'adaptive'}),
                                   (['--before-ref', 'main'], {'off', 'current'})):
                args = ['tracking_report.py', str(path), '--actual-csharp', '--render-hz', '120', '--json', str(output)] + options
                with mock.patch('sys.argv', args), contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(tracking_report.main(), 0)
                report = json.loads(output.read_text())
                self.assertEqual(report['render_hz'], 120)
                self.assertEqual({item['kind'] for item in report['results']}, kinds)
                self.assertEqual(report['candidate_maximum_ms'], 90 if 'adaptive' in kinds else None)
                json.dumps(report, allow_nan=False)


if __name__ == '__main__':
    unittest.main()
