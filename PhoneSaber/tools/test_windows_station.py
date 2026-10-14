import contextlib
import io
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import unittest
from unittest import mock

import latency_matrix
import tracking_report
import player_probe


class WindowsToolTests(unittest.TestCase):
    def test_nonfinite_latency_records_cannot_complete_a_probe(self):
        for field, value in (('n', 'inf'), ('n', '-inf'), ('median-ms', 'inf'), ('median-ms', 'nan')):
            with self.subTest(field=field, value=value):
                block = dict(n='20', **{'median-ms': '100'})
                block[field] = value
                lines = ['event=latency-block ' + ' '.join(f'{k}={v}' for k, v in block.items()),
                         'event=latency-loop-end n=20 p95-ms=120 misses=0']
                self.assertFalse(latency_matrix.summarise(lines, 1)['complete'])

    def test_tracking_requires_received_packets_and_the_written_file(self):
        cases = (('frames=100 packets=0 file=tracking-test.csv', True, 1),
                 ('frames=0 packets=10 file=tracking-test.csv', True, 1),
                 ('frames=100 packets=10 file=tracking-test.csv', False, 1),
                 ('frames=100 packets=10', True, 1),
                 ('frames=100 packets=10 file=tracking-test.csv', True, 0))
        for detail, exists, expected in cases:
            with self.subTest(detail=detail, exists=exists), tempfile.TemporaryDirectory() as temporary:
                folder = Path(temporary)
                if exists:
                    (folder / 'tracking-test.csv').write_text('F,0,.5\nR,0,0,0,1,1\n')
                with mock.patch.object(player_probe, 'data_folder', return_value=folder), \
                        mock.patch.object(player_probe, 'run_windows_probe'), \
                        mock.patch.object(player_probe, 'new_events', return_value=['event=tracking-probe ' + detail]), \
                        mock.patch('sys.argv', ['player_probe.py', 'Tracking']), \
                        contextlib.redirect_stdout(io.StringIO()):
                    self.assertEqual(player_probe.main(), expected)

    @unittest.skipUnless((Path('/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents') /
                          'Resources/Scripting/MonoBleedingEdge/bin/mono').is_file(), 'Unity Mono host が必要')
    def test_real_csharp_windows_socket_policy_and_oversized_recovery(self):
        import sys
        subprocess.run([sys.executable, '-B', str(Path(__file__).with_name('check_windows_station.py'))],
                       check=True, timeout=30)

    def test_rotated_events_keep_only_new_records_in_time_order(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            log = folder / 'events.log'
            log.write_text('old\n')
            cursor = player_probe.event_cursor(folder)
            with log.open('a') as handle:
                handle.write('new-before-rotation\n')
            log.rename(folder / 'events.log.1')
            log.write_text('new-after-rotation\n')
            self.assertEqual(player_probe.new_events(folder, cursor), ['new-before-rotation', 'new-after-rotation'])

    def test_windows_probe_passes_paths_as_environment_and_uses_the_lock(self):
        with tempfile.TemporaryDirectory(prefix="縁日 ! %PATH% & [1] '") as temporary:
            app = Path(temporary) / '3D-Saber.exe'
            app.touch()
            with mock.patch('sys.platform', 'win32'), mock.patch('subprocess.run') as run:
                player_probe.run_windows_probe(app, 'Tracking', 66, Path(temporary) / 'probe log.txt', 'B')
            command = run.call_args.args[0]
            self.assertEqual(command[-6:], ['-Mode', 'Lock', '-Probe', 'Tracking', '-ProbeCount', '66'])
            self.assertEqual(run.call_args.kwargs['env']['GAME_EXE'], str(app.resolve()))
            self.assertEqual(run.call_args.kwargs['env']['PHONESABER_STATION'], 'B')
            self.assertTrue(run.call_args.kwargs['check'])

    def test_mac_cannot_accidentally_launch_the_windows_probe(self):
        with mock.patch('sys.platform', 'darwin'), mock.patch('subprocess.run') as run:
            with self.assertRaises(RuntimeError):
                player_probe.run_windows_probe('missing.exe', 'Latency', 8, 'test.log')
            run.assert_not_called()

    def test_tracking_finds_the_windows_player_csv(self):
        with tempfile.TemporaryDirectory(prefix='縁日 probe ') as temporary:
            home = Path(temporary)
            folder = home / 'AppData/LocalLow/DefaultCompany/ServTechSlash/PhoneSaber'
            folder.mkdir(parents=True)
            (folder / 'tracking-test.csv').write_text('F,0,.5\nR,0,0,0,1,1\n', encoding='utf-8')
            output = io.StringIO()
            with mock.patch('sys.platform', 'win32'), mock.patch.object(Path, 'home', return_value=home), \
                    mock.patch.object(tracking_report, 'FOLDER', home / 'old-mac', create=True), \
                    mock.patch('sys.argv', ['tracking_report.py']), contextlib.redirect_stdout(output):
                self.assertEqual(tracking_report.main(), 1)
            self.assertIn('記録が足りません', output.getvalue())

    def test_windows_matrix_does_not_call_mac_phone_or_process_commands(self):
        def run(command, **kwargs):
            self.assertEqual(Path(command[0]).name, 'powershell.exe', command)
            return mock.Mock(returncode=0)

        with tempfile.TemporaryDirectory() as temporary:
            app = Path(temporary) / '日本語 ゲーム.exe'
            app.touch()
            with mock.patch('sys.platform', 'win32'), \
                    mock.patch('sys.argv', ['latency_matrix.py', '--app', str(app), '--warmup', '0']), \
                    mock.patch('subprocess.run', side_effect=run), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(latency_matrix.main(), 1)  # 空の計測を成功にしない。

    def test_windows_does_not_label_unchanged_android_settings_as_ios_experiments(self):
        with mock.patch('sys.platform', 'win32'), \
                mock.patch('sys.argv', ['latency_matrix.py', '--only', 'lowlat']), \
                mock.patch('subprocess.run') as run, contextlib.redirect_stderr(io.StringIO()):
            with self.assertRaises(SystemExit) as error:
                latency_matrix.main()
            self.assertEqual(error.exception.code, 2)
            run.assert_not_called()

    def test_windows_matrix_fails_when_only_one_of_eight_blocks_was_collected(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            app = folder / '3D-Saber.exe'
            app.touch()
            def run(*args, **kwargs):
                (folder / 'events.log').write_text(
                    'event=latency-block n=20 median-ms=100\n'
                    'event=latency-loop-end n=20 p95-ms=120 misses=0\n')
                return mock.Mock(returncode=0)
            with mock.patch('sys.platform', 'win32'), \
                    mock.patch('sys.argv', ['latency_matrix.py', '--app', str(app), '--blocks', '8', '--warmup', '0']), \
                    mock.patch.object(latency_matrix, 'data_folder', return_value=folder), \
                    mock.patch('subprocess.run', side_effect=run), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(latency_matrix.main(), 1)

    def test_windows_matrix_accepts_all_eight_blocks(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            app = folder / '3D-Saber.exe'
            app.touch()
            def run(*args, **kwargs):
                (folder / 'events.log').write_text(
                    'event=latency-block n=20 median-ms=100\n' * 8 +
                    'event=latency-loop-end n=160 p95-ms=120 misses=0\n')
                return mock.Mock(returncode=0)
            with mock.patch('sys.platform', 'win32'), \
                    mock.patch('sys.argv', ['latency_matrix.py', '--app', str(app), '--blocks', '8', '--warmup', '0']), \
                    mock.patch.object(latency_matrix, 'data_folder', return_value=folder), \
                    mock.patch('subprocess.run', side_effect=run), contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(latency_matrix.main(), 0)

    @unittest.skipUnless(os.name == 'nt' or shutil.which('pwsh'), 'PowerShell が必要')
    def test_first_run_stops_at_each_failed_stage_and_retries_latency_once(self):
        shell = 'powershell.exe' if os.name == 'nt' else 'pwsh'
        expected = ['firewall', 'pip', 'windows-tests', 'ops-tests', 'build', 'latency',
                    'firewall', 'tracking', 'report']
        for failure in ('', 'pip', 'windows-tests', 'ops-tests', 'build', 'latency',
                        'latency-once', 'tracking', 'report', 'missing-build'):
            with self.subTest(failure=failure), tempfile.TemporaryDirectory(prefix="縁日 setup [1] '") as temporary:
                root = Path(temporary)
                kit = root / 'PhoneSaber/windows'
                kit.mkdir(parents=True)
                shutil.copyfile(player_probe.ROOT / 'PhoneSaber/windows/First-Run.ps1', kit / 'First-Run.ps1')
                (kit / 'Allow-PhoneSaber-Firewall.ps1').write_text("Add-Content -LiteralPath $env:TRACE -Value 'firewall'\n")
                unity = root / 'Unity.ps1'
                unity.write_text('''Add-Content -LiteralPath $env:TRACE -Value 'build'
$global:LASTEXITCODE = 0
if ($env:FAIL_STAGE -eq 'build') { $global:LASTEXITCODE = 7; return }
if ($env:FAIL_STAGE -ne 'missing-build') {
    New-Item -ItemType Directory -Path 'Builds/Windows' -Force | Out-Null
    New-Item -ItemType File -Path 'Builds/Windows/3D-Saber.exe' | Out-Null
}
''')
                wrapper = root / 'test.ps1'
                wrapper.write_text('''function Get-Process { }
function Read-Host { }
$script:latencyCalls = 0
function py {
    $stage = switch -Wildcard ($args -join ' ') {
        '*pip install*' { 'pip' }
        '*test_windows_station.py*' { 'windows-tests' }
        '*test_ops_watchdog.py*' { 'ops-tests' }
        '*latency_matrix.py*' { 'latency' }
        '*player_probe.py*' { 'tracking' }
        '*tracking_report.py*' { 'report' }
        default { throw 'unexpected command' }
    }
    if ($stage -eq 'latency' -and ($args -join ' ') -notlike '*--station B --only baseline --blocks 8') {
        throw 'unexpected latency arguments'
    }
    if ($stage -eq 'tracking' -and ($args -join ' ') -notlike '*Tracking --station B --count 66') {
        throw 'unexpected tracking arguments'
    }
    Add-Content -LiteralPath $env:TRACE -Value $stage
    if ($stage -eq 'latency') { $script:latencyCalls++ }
    $global:LASTEXITCODE = 0
    if ($env:FAIL_STAGE -eq $stage -or ($env:FAIL_STAGE -eq 'latency-once' -and
        $stage -eq 'latency' -and $script:latencyCalls -eq 1)) { $global:LASTEXITCODE = 7 }
}
try { & $env:FIRST_RUN -Unity $env:FAKE_UNITY -Station B; exit 0 } catch { Write-Output $_; exit 1 }
''')
                trace = root / 'trace.txt'
                result = subprocess.run([shell, '-NoProfile', '-File', str(wrapper)], capture_output=True,
                                        text=True, timeout=15, cwd=root, env=dict(os.environ, OS='Windows_NT',
                                        TRACE=str(trace), FAIL_STAGE=failure, FIRST_RUN=str(kit / 'First-Run.ps1'),
                                        FAKE_UNITY=str(unity)))
                calls = trace.read_text(encoding='utf-8-sig').splitlines()
                if failure in ('', 'latency-once'):
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                    self.assertEqual(calls, expected if not failure else expected[:7] + ['latency'] + expected[7:])
                else:
                    self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
                    stop = 'build' if failure == 'missing-build' else failure
                    self.assertEqual(calls, expected[:expected.index(stop) + 1] +
                                     (['firewall', 'latency'] if failure == 'latency' else []))

    @unittest.skipUnless(os.name == 'nt' or shutil.which('pwsh'), 'PowerShell が必要')
    def test_powershell_parser_accepts_every_station_script(self):
        shell = 'powershell.exe' if os.name == 'nt' else 'pwsh'
        script = '''$failed = $false
Get-ChildItem -LiteralPath $env:KIT -Filter *.ps1 | ForEach-Object {
    $errors = $null; $tokens = $null
    $null = [Management.Automation.Language.Parser]::ParseFile($_.FullName, [ref]$tokens, [ref]$errors)
    if ($errors.Count) { $errors | Out-String | Write-Output; $failed = $true }
}
if ($failed) { exit 1 }
'''
        with tempfile.TemporaryDirectory() as temporary:
            subprocess.run([shell, '-NoProfile', '-Command', script], check=True, cwd=temporary,
                           env=dict(os.environ, KIT=str(player_probe.ROOT / 'PhoneSaber/windows')))


if __name__ == '__main__':
    unittest.main()
