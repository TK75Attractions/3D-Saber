"""Mac は偽 open と実プロセスで検証。Windows の実行統合テストは Windows 上でのみ行う。"""
import os
from pathlib import Path
import signal
import shutil
import time
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]


@unittest.skipUnless(sys.platform == 'darwin', 'macOS bash / BSD stat の検証')
class MacWatchdogTests(unittest.TestCase):
    def run_watchdog(self, mode, threshold="1", displays="0", clamshell="No", station="A"):
        with tempfile.TemporaryDirectory(prefix="縁日 テスト (1) ! ") as tmp:
            folder = Path(tmp)
            app = folder / "3D-Saber.app"
            game = app / "Contents/MacOS/ServTechSlash"
            game.parent.mkdir(parents=True)
            game.write_text('''#!/usr/bin/env python3
import os, sys, time
from pathlib import Path
args = sys.argv
quit_path = Path(args[args.index('-phonesaberQuitMarker') + 1])
count_path = Path(os.environ['COUNT_PATH'])
count = int(count_path.read_text()) + 1 if count_path.exists() else 1
count_path.write_text(str(count))
mode = os.environ['MODE']
if mode == 'hang' and count == 1:
    if '-phonesaberHeartbeat' in args:
        path = Path(args[args.index('-phonesaberHeartbeat') + 1])
        path.write_text('1')
        os.utime(path, (time.time() - 90, time.time() - 90))
    quit_path.write_text('stale marker must not make hang a clean quit')
    time.sleep(30)
elif mode.startswith('display-') and count == 1:
    path = Path(args[args.index('-phonesaberHeartbeat') + 1])
    path.write_text('1')
    os.utime(path, (time.time() - 90, time.time() - 90))
    time.sleep(1.5)
    if mode in ('display-wake-fresh', 'display-wake-hang'):
        Path(os.environ['DISPLAY_PATH']).write_text('0')
        if mode == 'display-wake-hang':
            time.sleep(30)
        else:
            # 復帰直後はまだ古い heartbeat。猶予中に Unity が再開する。
            time.sleep(0.5)
            for _ in range(4):
                path.write_text('1')
                time.sleep(0.3)
elif mode == 'fresh':
    path = Path(args[args.index('-phonesaberHeartbeat') + 1])
    for _ in range(4):
        path.write_text('1')
        time.sleep(0.4)
elif mode in ('old', 'disabled', 'stop', 'empty'):
    if (mode in ('disabled', 'stop') or (mode == 'empty' and count == 1)) and '-phonesaberHeartbeat' in args:
        path = Path(args[args.index('-phonesaberHeartbeat') + 1])
        path.write_text('' if mode == 'empty' else '1')
        os.utime(path, (time.time() - 90, time.time() - 90))
    if mode == 'stop':
        Path(os.environ['STOP_PATH']).touch()
    time.sleep(1.5)
quit_path.write_text('ok')
''')
            game.chmod(0o755)
            fake_open = folder / "open"
            fake_open.write_text('''#!/usr/bin/env python3
import os, subprocess, sys
from pathlib import Path
args = sys.argv[1:]
app = args[args.index('--args') - 1]
p = subprocess.Popen([str(Path(app) / 'Contents/MacOS/ServTechSlash')] + args[args.index('--args') + 1:])
Path(os.environ['PID_PATH']).write_text(str(p.pid))
Path(os.environ['COMMAND_PATH']).write_text(str(Path(app) / 'Contents/MacOS/ServTechSlash') + ' ' + ' '.join(args[args.index('--args') + 1:]))
p.wait()
# 本物の open はアプリの crash でも 0 を返し得る。
sys.exit(0)
''')
            fake_open.chmod(0o755)
            # sandbox では ps が禁止されるため、open が記録した実 PID / 引数を返す。
            # kill 自体は偽物にしない。
            fake_ps = folder / "ps"
            fake_ps.write_text('''#!/usr/bin/env python3
import os, sys
from pathlib import Path
pid_file = Path(os.environ['PID_PATH'])
if not pid_file.exists():
    sys.exit(1)
pid = int(pid_file.read_text())
try:
    os.kill(pid, 0)
except ProcessLookupError:
    sys.exit(1)
command = Path(os.environ['COMMAND_PATH']).read_text()
if '-p' in sys.argv:
    if sys.argv[sys.argv.index('-p') + 1] != str(pid):
        sys.exit(1)
    print(command)
elif 'pid=' in sys.argv:
    print(str(pid) + ' ' + command)
else:
    print(command)
''')
            fake_ps.chmod(0o755)
            # 実際の画面や電源設定に依存させない。inline Python はそのまま実行し、
            # ctypes の CoreGraphics 呼出しだけを差し替える（複数画面・取得失敗も再現）。
            fake_python = folder / "python3"
            fake_python.write_text(f'#!{sys.executable}\n' + '''
import ctypes as c
import os, sys
from pathlib import Path
if sys.argv[1] != '-c':
    os.execv(sys.executable, [sys.executable, *sys.argv[1:]])
def display_list(capacity, displays, count):
    state = Path(os.environ['DISPLAY_PATH']).read_text()
    if state == 'error':
        return 1
    states = [] if state == 'none' else state.split(',')
    count._obj.value = min(capacity, len(states)) if displays is not None else len(states)
    if displays is not None:
        for index in range(count._obj.value):
            displays[index] = index + 1
    return 0
def is_asleep(display):
    return int(Path(os.environ['DISPLAY_PATH']).read_text().split(',')[display - 1])
def is_builtin(display):
    return int(os.environ.get('DISPLAY_BUILTIN', '0'))
class FakeCG:
    CGGetOnlineDisplayList = staticmethod(display_list)
    CGDisplayIsAsleep = staticmethod(is_asleep)
    CGDisplayIsBuiltin = staticmethod(is_builtin)
c.CDLL = lambda path: FakeCG()
exec(sys.argv[2])
''')
            fake_python.chmod(0o755)
            (folder / 'display').write_text(displays)
            for name, output in {
                'ioreg': f'  |   "AppleClamshellState" = {clamshell}\n',
                'pmset': "Now drawing from 'AC Power'\n lowpowermode 0\n",
            }.items():
                tool = folder / name
                tool.write_text("#!/bin/bash\ncat <<'WATCHDOG_TEST_OUTPUT'\n" +
                                output + 'WATCHDOG_TEST_OUTPUT\n')
                tool.chmod(0o755)
            watcher = folder / "saber-watchdog.sh"
            watcher.write_text((ROOT / "PhoneSaber/mac/saber-watchdog.sh").read_text().replace(
                '/usr/bin/open -W', f'"{fake_open}" -W').replace('sleep 1', 'sleep 0.1'))
            env = dict(os.environ, MODE=mode, COUNT_PATH=str(folder / 'count'), PID_PATH=str(folder / 'pid'),
                       PHONESABER_HANG_SECONDS=threshold, PHONESABER_P2P_BRIDGE_SCRIPT=str(folder / 'missing'),
                       PHONESABER_TRIAGE_RECEIVER='0', DISPLAY_PATH=str(folder / 'display'),
                       COMMAND_PATH=str(folder / 'command'), STOP_PATH=str(folder / 'STOP'), PATH=str(folder) + os.pathsep + os.environ['PATH'])
            process = subprocess.Popen(['/bin/bash', str(watcher), station, str(app)], env=env,
                                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
            try:
                out, _ = process.communicate(timeout=10)
                log = (folder / f'Start-Saber-{station}.log').read_text()
                return process.returncode, log, int((folder / 'count').read_text()), out.decode()
            except subprocess.TimeoutExpired:
                self.fail('監視がハングから復帰しなかった')
            finally:
                if process.poll() is None:
                    process.send_signal(signal.SIGTERM)
                pid_file = folder / 'pid'
                if pid_file.exists():
                    try:
                        os.kill(int(pid_file.read_text()), signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                process.communicate(timeout=3)

    def test_hang_kills_real_game_and_restarts_even_with_quit_marker(self):
        code, log, count, _ = self.run_watchdog('hang')
        self.assertEqual(code, 0)
        self.assertEqual(count, 2)
        self.assertIn('hang detected', log)
        self.assertIn('exit-code=1', log)
        self.assertIn('restart=1', log)

    def test_no_heartbeat_old_build_is_never_killed(self):
        code, log, count, _ = self.run_watchdog('old')
        self.assertEqual((code, count), (0, 1))
        self.assertNotIn('hang detected', log)

    def test_created_but_unwritten_heartbeat_does_not_arm_hang_detection(self):
        # OpenOrCreate 成功後、最初の Write が容量不足で失敗すると0 byteのファイルだけが残る。
        code, log, count, _ = self.run_watchdog('empty')
        self.assertEqual((code, count), (0, 1))
        self.assertNotIn('hang detected', log)

    def test_disabled_threshold_never_kills(self):
        code, log, count, _ = self.run_watchdog('disabled', '0', displays='1')
        self.assertEqual((code, count), (0, 1))
        self.assertNotIn('hang detected', log)
        self.assertNotIn('hang-check paused', log)

    def test_stop_does_not_force_kill_current_game(self):
        code, log, count, _ = self.run_watchdog('stop')
        self.assertEqual((code, count), (0, 1))
        self.assertNotIn('hang detected', log)

    def test_fresh_heartbeat_and_normal_quit(self):
        code, log, count, _ = self.run_watchdog('fresh', '2')
        self.assertEqual((code, count), (0, 1))
        self.assertNotIn('hang detected', log)

    def test_sleeping_displays_pause_once_without_restart_warning_spam(self):
        for station in ('A', 'B'):
            with self.subTest(station=station):
                code, log, count, out = self.run_watchdog('display-sleep', displays='1,1', station=station)
                self.assertEqual((code, count), (0, 1), out)
                self.assertEqual(log.count('hang-check paused display-asleep'), 1)
                self.assertNotIn('hang detected', log)
                self.assertNotIn('異常終了', out)
                self.assertEqual(out.count('再起動回数:'), 1)

    def test_closed_lid_without_online_display_pauses(self):
        code, log, count, out = self.run_watchdog('display-sleep', displays='none', clamshell='Yes')
        self.assertEqual((code, count), (0, 1), out)
        self.assertEqual(log.count('hang-check paused display-asleep'), 1)
        self.assertNotIn('hang detected', log)

    def test_awake_external_display_with_closed_lid_still_detects_hang(self):
        code, log, count, out = self.run_watchdog('hang', displays='1,0', clamshell='Yes')
        self.assertEqual((code, count), (0, 2), out)
        self.assertIn('hang detected', log)
        self.assertNotIn('hang-check paused', log)

    def test_display_probe_failure_does_not_disable_real_hang_detection(self):
        for displays, clamshell in (('error', 'Yes'), ('none', 'No'), ('none', 'unknown')):
            with self.subTest(displays=displays, clamshell=clamshell):
                code, log, count, out = self.run_watchdog('hang', displays=displays, clamshell=clamshell)
                self.assertEqual((code, count), (0, 2), out)
                self.assertIn('hang detected', log)
                self.assertNotIn('hang-check paused', log)

    def test_display_wake_allows_heartbeat_to_resume_during_grace(self):
        code, log, count, out = self.run_watchdog('display-wake-fresh', '2', displays='1')
        self.assertEqual((code, count), (0, 1), out)
        self.assertEqual(log.count('hang-check paused display-asleep'), 1)
        self.assertIn('hang-check resumed display-awake grace=2s', log)
        self.assertNotIn('hang detected', log)

    def test_display_wake_still_detects_hang_after_full_grace(self):
        code, log, count, out = self.run_watchdog('display-wake-hang', '2', displays='1')
        self.assertEqual((code, count), (0, 2), out)
        resumed = next(line for line in log.splitlines() if 'hang-check resumed' in line)
        detected = next(line for line in log.splitlines() if 'hang detected' in line)
        resumed_at = time.strptime(resumed[1:21], '%Y-%m-%dT%H:%M:%SZ')
        detected_at = time.strptime(detected[1:21], '%Y-%m-%dT%H:%M:%SZ')
        self.assertGreaterEqual(time.mktime(detected_at) - time.mktime(resumed_at), 2)


class WindowsSourceContractTests(unittest.TestCase):
    def test_both_bats_use_shared_lock_and_monitored_exit_code(self):
        for station in ('A', 'B'):
            raw = (ROOT / f'PhoneSaber/windows/Start-Saber-{station}.bat').read_bytes()
            self.assertFalse(raw.startswith(b'\xef\xbb\xbf'))
            self.assertNotIn(b'\n', raw.replace(b'\r\n', b''))
            text = raw.decode('utf-8')
            self.assertIn('-Mode Lock', text)
            self.assertIn('-Mode Run', text)
            self.assertIn('-NoProfile', text)
            self.assertIn('set "GAME_EXIT=%ERRORLEVEL%"', text)
            self.assertIn('if "%GAME_EXIT%"=="0" exit /b 0', text)
            self.assertIn('goto stopped', text)

    def test_windows_lock_uses_os_liveness_and_game_process_identity(self):
        helper = ROOT / 'PhoneSaber/windows/saber-watchdog.ps1'
        self.assertTrue(helper.is_file(), '排他ロックとハートビートを処理する helper が無い')
        raw = helper.read_bytes()
        self.assertTrue(raw.startswith(b'\xef\xbb\xbf'))
        self.assertNotIn(b'\n', raw.replace(b'\r\n', b''))
        text = raw.decode('utf-8-sig')
        for contract in ('FileShare]::None', 'Get-Process -Name', '3D-Saber',
                         '-phonesaberHeartbeat', 'PHONESABER_HANG_SECONDS', 'hang detected',
                         '$game.Kill()', '$game.ExitCode'):
            self.assertIn(contract, text)


@unittest.skipUnless(os.name == 'nt' or shutil.which('pwsh'), 'PowerShell が必要')
class WindowsWatchdogPrelaunchTests(unittest.TestCase):
    def run_watchdog(self, folder, probe, stop_name=None):
        wrapper = folder / 'test.ps1'
        wrapper.write_text("""[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
function powercfg {
    if ($env:STOP_TO_CREATE) { [IO.File]::WriteAllText($env:STOP_TO_CREATE, '') }
    'test power plan'
}
function Get-ItemProperty { [PSCustomObject]@{ AutoGameModeEnabled = 1 } }
function Add-Type { throw 'test display unavailable' }
& $env:WATCHDOG_SCRIPT -Mode Run -Probe $env:WATCHDOG_TEST_PROBE -ProbeCount ([int]$env:WATCHDOG_TEST_COUNT)
exit $LASTEXITCODE
""", encoding='utf-8-sig')
        shell = 'powershell.exe' if os.name == 'nt' else 'pwsh'
        env = dict(os.environ, WATCHDOG_SCRIPT=str(ROOT / 'PhoneSaber/windows/saber-watchdog.ps1'),
                   WATCHDOG_TEST_PROBE=probe, WATCHDOG_TEST_COUNT='10' if probe == 'Tracking' else '8',
                   PHONESABER_STATION='B', RESTART_COUNT='0', TEMP=str(folder),
                   STOP_FILE=str(folder / 'Start-Saber-B.STOP'), LOG_FILE=str(folder / 'watchdog.log'),
                   STOP_TO_CREATE=str(folder / stop_name) if stop_name else '',
                   GAME_EXE=sys.executable, PHONESABER_HANG_SECONDS='0')
        # Python は Player 引数を拒否して code=2 で終了する。GUI を開かず実際の起動経路を検出する。
        return subprocess.run([shell, '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', str(wrapper)],
                              env=env, cwd=folder, capture_output=True, text=True, encoding='utf-8', timeout=15)

    def test_stop_during_presentation_check_prevents_normal_and_probe_launches(self):
        for probe in ('None', 'Latency', 'Tracking'):
            for stop_name in ('Start-Saber-B.STOP', 'STOP'):
                with self.subTest(probe=probe, stop=stop_name), tempfile.TemporaryDirectory() as temporary:
                    folder = Path(temporary)
                    result = self.run_watchdog(folder, probe, stop_name)
                    self.assertTrue((folder / stop_name).is_file(), '電源確認中に STOP を作成した')
                    log = (folder / 'watchdog.log').read_text(encoding='utf-8-sig')
                    self.assertIn('test display unavailable', log)
                    self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                    self.assertIn('not-started STOP-file', log)
                    self.assertEqual(list(folder.glob('phonesaber-heartbeat-*')), [])

    def test_without_stop_the_player_launch_path_is_reached(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            result = self.run_watchdog(folder, 'None')
            self.assertEqual(result.returncode, 2, result.stdout + result.stderr)
            self.assertIn('Unknown option: -p', result.stderr)
            self.assertNotIn('not-started STOP-file', (folder / 'watchdog.log').read_text(encoding='utf-8-sig'))


@unittest.skipUnless(os.name == 'nt', 'cmd.exe / PowerShell 5.1 は Windows 実機でのみ実行')
class WindowsWatchdogIntegrationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="縁日 テスト (1) ! %PATH% & [1] ' ")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.kit = self.root / 'PhoneSaber/windows'
        self.kit.mkdir(parents=True)
        build = self.root / 'Builds/Windows'
        build.mkdir(parents=True)
        for name in ('Start-Saber-A.bat', 'Start-Saber-B.bat', 'saber-watchdog.ps1'):
            source = ROOT / 'PhoneSaber/windows' / name
            if source.exists():
                shutil.copyfile(source, self.kit / name)
        csc = Path(os.environ['WINDIR']) / 'Microsoft.NET/Framework/v4.0.30319/csc.exe'
        if not csc.exists():
            self.skipTest('.NET Framework C# compiler が必要')
        # 実ゲームが動いている PC ではテストしない。テストの kill はこの exe の PID に限定する。
        existing = subprocess.check_output(['tasklist', '/fi', 'IMAGENAME eq 3D-Saber.exe'], text=True)
        if '3D-Saber.exe' in existing:
            self.skipTest('運用中の Player を終了してからテスト')
        fake = self.root / 'Fake.cs'
        fake.write_text('''using System;
using System.IO;
using System.Threading;
class Fake {
    static int Main(string[] args) {
        string countPath = Environment.GetEnvironmentVariable("COUNT_PATH");
        int n = File.Exists(countPath) ? int.Parse(File.ReadAllText(countPath))+1 : 1;
        File.WriteAllText(countPath,n.ToString());
        File.WriteAllText(Environment.GetEnvironmentVariable("PID_PATH"),
            System.Diagnostics.Process.GetCurrentProcess().Id.ToString());
        string mode = Environment.GetEnvironmentVariable("MODE");
        File.WriteAllLines(Environment.GetEnvironmentVariable("COUNT_PATH")+".args", args);
        File.WriteAllText(Environment.GetEnvironmentVariable("COUNT_PATH")+".cwd", Environment.CurrentDirectory);
        int index = Array.IndexOf(args,"-phonesaberHeartbeat");
        if ((mode=="hang" && n==1) || mode=="disabled" || (mode=="empty" && n==1)) {
            if (index>=0) {
                File.WriteAllText(args[index+1],mode=="empty" ? "" : "1");
                File.SetLastWriteTimeUtc(args[index+1],DateTime.UtcNow.AddMinutes(-2));
            }
        }
        if (mode=="hang" && n==1 || mode=="hold") Thread.Sleep(30000);
        else Thread.Sleep(2000);
        return 0;
    }
}''', encoding='utf-8')
        subprocess.run([str(csc), '/nologo', '/target:winexe', '/out:' + str(build / '3D-Saber.exe'), str(fake)], check=True)
        self.env = dict(os.environ, COUNT_PATH=str(self.root / 'count'), PID_PATH=str(self.root / 'pid'),
                        PHONESABER_HANG_SECONDS='1')
        self.processes = []
        self.addCleanup(self.stop_processes)

    def stop_processes(self):
        # STOP を先に作ることで、テスト中の Player を止めても再起動しない。
        (self.kit / 'STOP').touch()
        pid_path = self.root / 'pid'
        if pid_path.exists():
            subprocess.run(['taskkill', '/pid', pid_path.read_text(), '/f'], capture_output=True)
        for process in self.processes:
            try:
                process.communicate(timeout=5)
            except subprocess.TimeoutExpired:
                subprocess.run(['taskkill', '/pid', str(process.pid), '/t', '/f'], capture_output=True)
                process.communicate(timeout=5)

    def launch(self, station, mode):
        # env 展開は一度だけ。パス中の ! / % / 括弧を cmd コードに再挿入しない。
        env = dict(self.env, MODE=mode, TEST_BATCH=str(self.kit / f'Start-Saber-{station}.bat'))
        process = subprocess.Popen('"' + os.environ['COMSPEC'] + '" /d /s /c ""%TEST_BATCH%""',
                                   env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT)
        process.stdin.write(b'\n' * 10)
        process.stdin.flush()
        self.processes.append(process)
        return process

    def wait_for_count(self, wanted, seconds=15):
        deadline = time.monotonic() + seconds
        path = self.root / 'count'
        while time.monotonic() < deadline:
            if path.exists() and path.read_text() == str(wanted):
                return
            time.sleep(0.1)
        self.fail(f'Player 起動回数が {wanted} にならなかった')

    def test_hang_restart_preserves_crash_exit_code(self):
        process = self.launch('A', 'hang')
        out, _ = process.communicate(timeout=20)
        self.assertEqual(process.returncode, 0, out)
        self.assertEqual((self.root / 'count').read_text(), '2')
        log = (self.kit / 'Start-Saber-A.log').read_text(encoding='utf-8-sig')
        self.assertIn('hang detected', log)
        self.assertIn('exit-code=1', log)
        self.assertIn('exit-code=0', log)

    def test_probes_use_the_shared_lock_and_forward_quoted_paths(self):
        for probe, count, flag in (('Latency', '8', '-phonesaberLatencyProbe'),
                                   ('Tracking', '66', '-phonesaberTrackingProbe')):
            with self.subTest(probe=probe):
                log = self.root / '計測 log [1].txt'
                env = dict(self.env, MODE='old', PHONESABER_STATION='B', RESTART_COUNT='0',
                           GAME_EXE=str(self.root / 'Builds/Windows/3D-Saber.exe'),
                           LOG_FILE=str(self.kit / 'Start-Saber-B.log'),
                           STOP_FILE=str(self.kit / 'Start-Saber-B.STOP'), PHONESABER_PROBE_LOG=str(log))
                result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass',
                                         '-File', str(self.kit / 'saber-watchdog.ps1'), '-Mode', 'Lock',
                                         '-Probe', probe, '-ProbeCount', count], env=env,
                                        input='\n', capture_output=True, text=True, timeout=15)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                args = (self.root / 'count.args').read_text(encoding='utf-8-sig').splitlines()
                self.assertEqual(args[args.index(flag) + 1], count)
                self.assertEqual(args[args.index('-logFile') + 1], str(log))
                self.assertEqual(args[args.index('-phonesaberStation') + 1], 'B')
                self.assertEqual((self.root / 'count.cwd').read_text(encoding='utf-8-sig'), str(self.root / 'Builds/Windows'))

    def test_probe_cannot_bypass_another_station_watchdog(self):
        self.launch('A', 'hold')
        self.wait_for_count(1)
        env = dict(self.env, PHONESABER_STATION='B', RESTART_COUNT='0',
                   LOG_FILE=str(self.kit / 'Start-Saber-B.log'))
        result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                                 str(self.kit / 'saber-watchdog.ps1'), '-Mode', 'Lock', '-Probe', 'Latency'],
                                env=env, input='\n', capture_output=True, text=True, timeout=10)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual((self.root / 'count').read_text(), '1')

    def test_probe_honours_stop_before_starting(self):
        for stop in ('Start-Saber-B.STOP', 'STOP'):
            path = self.kit / stop
            path.touch()
            env = dict(self.env, PHONESABER_STATION='B', RESTART_COUNT='0',
                       LOG_FILE=str(self.kit / 'Start-Saber-B.log'),
                       STOP_FILE=str(self.kit / 'Start-Saber-B.STOP'))
            result = subprocess.run(['powershell.exe', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
                                     str(self.kit / 'saber-watchdog.ps1'), '-Mode', 'Lock', '-Probe', 'Latency'],
                                    env=env, input='\n', capture_output=True, text=True, timeout=10)
            path.unlink()
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertFalse((self.root / 'count').exists())

    def test_second_station_cannot_start_player(self):
        self.launch('A', 'hold')
        self.wait_for_count(1)
        second = self.launch('B', 'hold')
        out, _ = second.communicate(timeout=5)
        self.assertNotEqual(second.returncode, 0, out)
        self.assertEqual((self.root / 'count').read_text(), '1')
        self.assertIn('not-started', (self.kit / 'Start-Saber-B.log').read_text(encoding='utf-8-sig'))

    def test_already_running_player_without_watchdog_is_refused(self):
        env = dict(self.env, MODE='hold')
        game = subprocess.Popen([str(self.root / 'Builds/Windows/3D-Saber.exe')], env=env)
        def stop_game():
            if game.poll() is None:
                game.kill()
            game.wait(timeout=5)
        self.addCleanup(stop_game)
        self.wait_for_count(1)
        second = self.launch('B', 'hold')
        out, _ = second.communicate(timeout=5)
        self.assertNotEqual(second.returncode, 0, out)
        self.assertEqual((self.root / 'count').read_text(), '1')
        self.assertIn('game-already-running', (self.kit / 'Start-Saber-B.log').read_text(encoding='utf-8-sig'))
        game.kill()

    def test_stale_lock_file_and_old_build_do_not_block_start(self):
        (Path(os.environ['TEMP']) / 'phonesaber-watchdog.lock').touch(exist_ok=True)
        process = self.launch('A', 'old')
        out, _ = process.communicate(timeout=8)
        self.assertEqual(process.returncode, 0, out)
        self.assertEqual((self.root / 'count').read_text(), '1')
        self.assertNotIn('hang detected', (self.kit / 'Start-Saber-A.log').read_text(encoding='utf-8-sig'))

    def test_created_but_unwritten_heartbeat_does_not_arm_hang_detection(self):
        process = self.launch('A', 'empty')
        out, _ = process.communicate(timeout=10)
        self.assertEqual(process.returncode, 0, out)
        self.assertEqual((self.root / 'count').read_text(), '1')
        self.assertNotIn('hang detected', (self.kit / 'Start-Saber-A.log').read_text(encoding='utf-8-sig'))

    def test_zero_threshold_disables_hang_detection(self):
        self.env['PHONESABER_HANG_SECONDS'] = '0'
        process = self.launch('B', 'disabled')
        out, _ = process.communicate(timeout=8)
        self.assertEqual(process.returncode, 0, out)
        self.assertEqual((self.root / 'count').read_text(), '1')
        self.assertNotIn('hang detected', (self.kit / 'Start-Saber-B.log').read_text(encoding='utf-8-sig'))


if __name__ == '__main__':
    unittest.main()
