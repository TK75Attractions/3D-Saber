#!/usr/bin/env python3
"""Unity実DLLで4構成をcompileし、実PerfLogと純粋部分をstandalone NUnitで実行する。"""
import argparse
import os
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
INPUT = ROOT / 'Assets/Scripts/Managers/Inputsystem/Input'
HOST = Path(__file__).with_name('pc_latency_tests')


def run(command, **kwargs):
    result = subprocess.run(command, text=True, capture_output=True, **kwargs)
    if result.stdout:
        print(result.stdout, end='')
    if result.stderr:
        print(result.stderr, end='')
    return result.returncode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before-ref', help='旧PerfLogで窓境界の回帰3件がFAILすることも確認')
    parser.add_argument('--compensation-before-ref', help='旧自動補正/画面推定で2件の回帰FAILを確認')
    parser.add_argument('--platform-before-ref', help='旧macOS専用サービスのWindows Editor判定FAILを確認')
    parser.add_argument('--frame-before-ref', help='旧InputPointの新着なしlock回帰FAILとhost計測を確認')
    parser.add_argument('--frames-only', action='store_true', help='新着なし/定常入力のhost回帰と計測だけ実行')
    parser.add_argument('--sender-before-ref', help='旧統計の競合なしpoll回帰FAILとhost計測を確認')
    parser.add_argument('--prediction-before-ref', help='旧InputPointの時計混在による予測期限切れ回帰FAILを確認')
    parser.add_argument('--learning-only', action='store_true', help='台準備ACKのNUnit・実Swiftとの偽配送だけ実行')
    parser.add_argument('--learning-before-ref', help='旧指示再送・表示の実ソースで同じ回帰FAILも確認')
    args = parser.parse_args()
    editor = Path(os.environ.get('PHONESABER_UNITY_CONTENTS', '/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents'))
    mono = editor / 'Resources/Scripting/MonoBleedingEdge'
    nunit = editor / 'Resources/PackageManager/BuiltInPackages/com.unity.ext.nunit/net40/unity-custom/nunit.framework.dll'
    compiler = [str(mono / 'bin/mono'), str(mono / 'lib/mono/4.5/csc.exe'), '/nologo', '/langversion:9.0']
    template = editor / 'Resources/PackageManager/ProjectTemplates/libcache/com.unity.template.2d-cross-platform-2d-6.1.2/ScriptAssemblies'
    with tempfile.TemporaryDirectory(prefix='phonesaber-pc-latency-') as tmp:
        build = Path(tmp)
        stubs = build / 'GameDependencies.cs'
        stubs.write_text('''using UnityEngine;
public static class UdpImuBridge { public static bool TryGetLatest(out Vector3 a,out Vector3 g,out bool c){a=g=default;c=false;return false;} }
public static class GameSession {public static string SelectedSongId,SelectedSongTitle,SelectedDifficulty;public static bool IsCalibrationMode,TutorialPending; public static int FinalScore,FinalHit,FinalMiss,FinalMaxCombo,FinalComboBonus,FinalPerfect,FinalGreat,FinalGood,FinalBad,JudgmentOffsetMs;}
public static class HighScoreStore { public static string Key(string s,string d) => s+d; }
public static class SongAchievementStore { public static string Key(string s,string d) => s+d; }
public static class DailyRankingStore { public static string Key(string s,string d) => s+d; }
public static class ScreenTransition { public static bool IsBusy; }
public class ScoreManager : MonoBehaviour { public int Score,HitCount,MissCount,MaxCombo,ComboBonus,PerfectCount,GreatCount,GoodCount,BadCount; }
public class ResultController : MonoBehaviour { public void OnBackButton(){} }
public class CalibrationController : MonoBehaviour { public bool IsLive; public void DiscardAndExit(){} }
public class SongSelectController : MonoBehaviour {
 public string[] difficultyNames; public int SongCount; public bool IsLocked(int i)=>false;
 public string SongIdAt(int i)=>""; public static bool HasPlayableChart(string s,string[] d)=>false;
 public void Select(int i){} public void SetDifficulty(int i){} public void StartGame(){} public void ReturnToTitle(){}
}
public class SaberBladeVisual : MonoBehaviour { public int VertexCount; public void Clear(){} public void NotifyCut(){} public void Show(Vector3 a,Vector3 b,float w,Color c,float t){} }
public static class DisplaySettings { public static bool ProjectorMode; }
public static class SaberCutJudge { public static float DistPointToSegment(Vector2 p,Vector2 a,Vector2 b,out Vector2 q){q=a;return 0;} }
public enum CameraSaberColor { Red, Blue }
public static class ProjectorMode { public const float BladeWidthScale=1,BladeOutlineScale=1,BladeOutlineZOffset=0; public static Color BladeOutlineColor; }
// 判定の記録・補正（PhoneSaberJudgeTimingLog / PhoneSaberJudgeLatency）が読むゲーム本体の型の代役。
public enum JudgmentTier { Perfect, Great, Good, Bad }
public enum SaberHand { Any, Left, Right }
public class SongPlayer : MonoBehaviour { public double SongTime; }
public class CuttableNote : MonoBehaviour { public double HitTime; public double? LastCutSongTime; public int RequiredCutCount = 1, CutsAchieved; public string RequiredDirection = "None"; public SaberHand RequiredHand, LastCutterHand; public bool LatestCutCorrectDirection, LastCutCorrectDirection, IsMissed; public event System.Action<CuttableNote, JudgmentTier, Vector3, Vector3> OnJudged; }
public class NoteSpawner : MonoBehaviour { public double JudgeLatencySeconds; public double JudgeClock(double t) => t - JudgeLatencySeconds; public event System.Action<CuttableNote> OnNoteSpawned, OnNoteMissed; }
public class GamePlayManager : MonoBehaviour { public NoteSpawner noteSpawner; public SongPlayer songPlayer; public float cameraJudgeLatencySeconds = .12f; }
''')
        refs = ['/r:' + str(p) for p in (editor / 'Resources/Scripting/Managed/UnityEngine').glob('*.dll')]
        refs += ['/r:' + str(template / 'Unity.InputSystem.dll'), '/r:' + str(mono / 'lib/mono/4.5/Facades/netstandard.dll')]
        sources = sorted(INPUT.glob('PhoneSaber*.cs')) + [INPUT / 'InputPoint.cs', INPUT / 'SwingEventStream.cs', ROOT / 'Assets/Scripts/Saber/SaberInputBridge.cs', ROOT / 'Assets/Scripts/Saber/MouseSaberInput.cs', ROOT / 'Assets/Scripts/Saber/CameraSaberHistory.cs', stubs]
        frame_tests = ROOT / 'Assets/Tests/Editor/PhoneSaberReceiverStateReadTests.cs'
        stats_tests = ROOT / 'Assets/Tests/Editor/PhoneSaberInputStatsTests.cs'
        sender_tests = ROOT / 'Assets/Tests/Editor/PhoneSaberSenderGateTests.cs'
        frame_sources = sources + [frame_tests, stats_tests, sender_tests, HOST / 'Host.cs']
        runtime = dict(os.environ, MONO_PATH=os.pathsep.join(map(str, (
            nunit.parent, build, editor / 'Resources/Scripting/Managed/UnityEngine', template))))
        for before in ([] if args.learning_only else [True, False] if args.frame_before_ref or args.sender_before_ref or args.prediction_before_ref else [False]):
            current = list(frame_sources)
            # native API は起動しない。Unity時計を固定し、画面・保存設定のqueryもhostへ差し替える。
            host_input = build / 'InputPointHost.cs'
            input_before = args.frame_before_ref or args.prediction_before_ref
            source = (subprocess.check_output(['git', 'show', input_before +
                ':Assets/Scripts/Managers/Inputsystem/Input/InputPoint.cs'], cwd=ROOT).decode()
                if before and input_before else (INPUT / 'InputPoint.cs').read_text())
            source = source.replace('PhoneSaberPerfLog.Estimate(', 'FramePredictionPerfLog.Estimate(')
            host_input.write_text('using Time = FrameClock;\nusing Screen = FrameScreen;\n' + source)
            current[current.index(INPUT / 'InputPoint.cs')] = host_input
            if before and args.sender_before_ref:
                old = build / 'InputStatsBefore.cs'
                old.write_bytes(subprocess.check_output(['git', 'show', args.sender_before_ref +
                    ':Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberInputStats.cs'], cwd=ROOT))
                current[current.index(INPUT / 'PhoneSaberInputStats.cs')] = old
            exe = build / ('frames-before.exe' if before else 'frames.exe')
            if run(compiler + ['/optimize+', '/define:UNITY_EDITOR,UNITY_EDITOR_WIN,PHONESABER_FRAME_HOST',
                              '/r:' + str(nunit), '/out:' + str(exe)] + refs + list(map(str, current))):
                return 1
            expected = (3 if before and args.frame_before_ref else 0) | (4 if before and args.sender_before_ref else 0)
            expected |= 24 if before and args.prediction_before_ref else 0
            skip_prediction = ['skip-prediction'] if before and args.frame_before_ref and not args.prediction_before_ref else []
            result = run([str(mono / 'bin/mono'), str(exe), str(expected)] + skip_prediction, env=runtime)
            if result:
                return 1
            if before:
                print('Before frame regressions FAIL reproduced (expected mask): ' + str(expected))
        # 位置補正の既存NUnit全件も実行する。native生成・保存APIだけhostへ置換する。
        calibration_tests = ROOT / 'Assets/Tests/Editor/PhoneSaberPositionCalibrationTests.cs'
        acknowledgement_tests = ROOT / 'Assets/Tests/Editor/PhoneSaberAcknowledgementTests.cs'
        host_calibration = build / 'CalibrationHost.cs'
        aliases = 'using PlayerPrefs = CalibrationHostPrefs;\nusing JsonUtility = CalibrationHostJson;\n'
        host_calibration.write_text(aliases + (INPUT / 'PhoneSaberPositionCalibration.cs').read_text())
        host_calibration_tests = build / 'CalibrationTestsHost.cs'
        test_source = calibration_tests.read_text().replace('input.enabled = false;', '')
        test_source = test_source.replace('UnityEngine.Object.DestroyImmediate(go);', 'go.Dispose();')
        host_calibration_tests.write_text(aliases + 'using GameObject = CalibrationHostObject;\n' + test_source)
        calibration_sources = list(frame_sources)
        if args.learning_only:
            host_input = build / 'InputPointHost.cs'
            host_input.write_text('using Time = FrameClock;\nusing Screen = FrameScreen;\n' +
                                  (INPUT / 'InputPoint.cs').read_text().replace('PhoneSaberPerfLog.Estimate(', 'FramePredictionPerfLog.Estimate('))
        calibration_sources[calibration_sources.index(INPUT / 'InputPoint.cs')] = host_input
        calibration_sources[calibration_sources.index(INPUT / 'PhoneSaberPositionCalibration.cs')] = host_calibration
        discovery_tests = ROOT / 'Assets/Tests/Editor/PhoneSaberDiscoveryResponderTests.cs'
        test_runner = template / 'UnityEngine.TestRunner.dll'
        runtime['MONO_PATH'] += os.pathsep + str(template)
        for before in ([True, False] if args.learning_before_ref else [False]):
            current = list(calibration_sources)
            for name in ('PhoneSaberDiscoveryResponder.cs', 'PhoneSaberOperatorKeys.cs'):
                source = (subprocess.check_output(['git', 'show', args.learning_before_ref +
                          ':Assets/Scripts/Managers/Inputsystem/Input/' + name], cwd=ROOT).decode()
                          if before else (INPUT / name).read_text())
                host_source = build / ('Host' + name)
                host_source.write_text(source.replace('SwingMonotonicClock.', 'CommandHostClock.'))
                current[current.index(INPUT / name)] = host_source
            exe = build / ('learning-before.exe' if before else 'calibration.exe')
            if run(compiler + ['/define:UNITY_EDITOR,UNITY_EDITOR_WIN,PHONESABER_FRAME_HOST,PHONESABER_CALIBRATION_HOST',
                               '/r:' + str(nunit), '/r:' + str(test_runner), '/r:System.Runtime.Serialization.dll', '/out:' + str(exe)] + refs +
                   list(map(str, current + [host_calibration_tests, acknowledgement_tests, discovery_tests]))):
                return 1
            schedule, trace = build / 'learning-schedule.tsv', build / 'learning-acks.tsv'
            if run([str(mono / 'bin/mono'), str(exe), 'learning-schedule', str(schedule)], env=runtime):
                return 1
            if run([sys.executable, str(ROOT / 'PhoneSaber/ios/PhoneSaberSenderTests/run_pc_command_host_tests.py'),
                    '--retry-schedule', str(schedule), '--ack-trace', str(trace)]):
                return 1
            replay_result = run([str(mono / 'bin/mono'), str(exe), 'learning-replay', str(trace)], env=runtime)
            test_result = run([str(mono / 'bin/mono'), str(exe)], env=runtime)
            if before:
                if replay_result == 0 or test_result == 0:
                    print('ERROR: 旧実装で台準備ACK回帰FAILを再現できませんでした')
                    return 1
                print('Before learning recovery + status regressions FAIL reproduced (expected)')
            elif replay_result or test_result:
                return 1
        if args.frames_only or args.learning_only:
            return 0
        if args.platform_before_ref:
            for name in ('PhoneSaberBonjourPublisher.cs', 'PhoneSaberP2PBridgeProcess.cs', 'PhoneSaberTriageReceiverLauncher.cs'):
                before = build / name
                before.write_bytes(subprocess.check_output(['git', 'show', args.platform_before_ref +
                                   ':Assets/Scripts/Managers/Inputsystem/Input/' + name], cwd=ROOT))
                sources[sources.index(INPUT / name)] = before
        check = build / 'PlatformCheck.cs'
        check.write_text('''using System;
class Check { static int Main(string[] args) {
 bool expected = bool.Parse(args[0]);
 if (PhoneSaberBonjourPublisher.IsSupported != expected || PhoneSaberP2PBridgeProcess.IsSupported != expected) {
  Console.WriteLine("FAIL: macOS services support does not match Editor host / Player OS"); return 1;
 }
 if (!expected) {
  bool accessedEnvironment = false;
  using (var bridge = new PhoneSaberP2PBridgeProcess()) {
   var start = typeof(PhoneSaberP2PBridgeProcess).GetMethod("Start", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
   Func<string,string> environment = key => { accessedEnvironment = true; return "0"; };
   if ((bool)start.Invoke(bridge, new object[] { 5005, 5006, null, environment, "A" }) || accessedEnvironment) return 1;
  }
  if (PhoneSaberTriageReceiverLauncher.EnsureStarted(null)) return 1;
 }
 Console.WriteLine("macOS service platform policy PASS: supported=" + expected); return 0;
}}
''')
        for platform in ('UNITY_EDITOR,UNITY_EDITOR_OSX', 'UNITY_EDITOR,UNITY_EDITOR_WIN', 'UNITY_STANDALONE_OSX', 'UNITY_STANDALONE_WIN',
                         'UNITY_EDITOR,UNITY_EDITOR_WIN,UNITY_STANDALONE_OSX',
                         'UNITY_EDITOR,UNITY_EDITOR_OSX,UNITY_STANDALONE_WIN'):
            assembly = build / (platform.replace(',', '-') + '.dll')
            code = run(compiler + ['/target:library', '/define:' + platform, '/out:' + str(assembly)] + refs + list(map(str, sources)))
            if code:
                return code
            print('Unity 6000.3.9f1 API compile PASS: ' + platform)
            binary = build / 'platform.exe'
            if run(compiler + ['/out:' + str(binary), '/r:' + str(assembly), str(check)] + refs):
                return 1
            supported = 'UNITY_EDITOR_OSX' in platform or ('UNITY_EDITOR' not in platform and 'UNITY_STANDALONE_OSX' in platform)
            runtime = dict(os.environ, MONO_PATH=os.pathsep.join(map(str, (build, editor / 'Resources/Scripting/Managed/UnityEngine', template))))
            if run([str(mono / 'bin/mono'), str(binary), str(supported)], env=runtime):
                return 1
            if platform.startswith('UNITY_EDITOR'):
                tests = [ROOT / 'Assets/Tests/Editor/PhoneSaberLatencyCompensationTests.cs',
                         ROOT / 'Assets/Tests/Editor/PhoneSaberLatencyProbeTests.cs',
                         ROOT / 'Assets/Tests/Editor/PhoneSaberJudgeTimingLogTests.cs',
                         ROOT / 'Assets/Tests/Editor/PhoneSaberEndpointPredictorTests.cs', frame_tests, stats_tests, sender_tests,
                         calibration_tests, acknowledgement_tests]
                if run(compiler + ['/target:library', '/r:' + str(nunit),
                                   '/out:' + str(build / (platform.replace(',', '-') + '-tests.dll'))] + refs + list(map(str, sources + tests))):
                    return 1
                print('Compensation EditMode test assembly API compile PASS: ' + platform)
        if run(compiler + ['/target:library', '/define:UNITY_EDITOR,UNITY_EDITOR_WIN',
                           '/out:' + str(build / 'PlayerBuild.dll')] + refs +
               [str(ROOT / 'Assets/Editor/PhoneSaberPlayerBuild.cs')]):
            return 1
        print('Windows Player build method Unity API compile PASS (BuildPipeline not executed)')
        # 実Windows launcherのP/Invoke構造体もcompile（OS APIの呼び出しはしない）。
        ps = (ROOT / 'PhoneSaber/windows/saber-watchdog.ps1').read_text(encoding='utf-8-sig')
        native = build / 'DisplayRead.cs'
        native.write_text(ps.split("-TypeDefinition @'\n", 1)[1].split("\n'@", 1)[0])
        if run(compiler + ['/target:library', '/out:' + str(build / 'DisplayRead.dll'), str(native)]):
            return 1
        print('Windows display/power PInvoke declaration compile PASS (native calls not run)')
        host_env = dict(os.environ, MONO_PATH=str(nunit.parent))
        host_sources = [HOST / 'Host.cs', HOST / 'Tests.cs', INPUT / 'PhoneSaberInputStats.cs']
        if args.before_ref:
            before = build / 'Before.cs'
            before.write_bytes(subprocess.check_output(['git', 'show', args.before_ref + ':Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberPerfLog.cs'], cwd=ROOT))
            exe = build / 'before.exe'
            if run(compiler + ['/r:' + str(nunit), '/out:' + str(exe)] + list(map(str, host_sources + [before]))):
                return 1
            if run([str(mono / 'bin/mono'), str(exe)], env=host_env) != 1:
                print('ERROR: 旧実装で回帰FAILを再現できませんでした')
                return 1
            print('Before regression FAIL reproduced (expected)')
        compensation = ROOT / 'Assets/Tests/Editor/PhoneSaberLatencyCompensationTests.cs'
        if args.compensation_before_ref:
            old = []
            for name in ('PhoneSaberPredictionSettings.cs', 'PhoneSaberLatencyEstimate.cs', 'PhoneSaberPerfLog.cs'):
                path = build / name
                path.write_bytes(subprocess.check_output(['git', 'show', args.compensation_before_ref + ':Assets/Scripts/Managers/Inputsystem/Input/' + name], cwd=ROOT))
                old.append(path)
            exe = build / 'compensation-before.exe'
            if run(compiler + ['/define:AFTER,COMPENSATION_BEFORE', '/r:' + str(nunit), '/out:' + str(exe)] + list(map(str, host_sources + old + [compensation]))):
                return 1
            if run([str(mono / 'bin/mono'), str(exe)], env=host_env) != 1:
                print('ERROR: 自動補正/画面推定の旧実装でFAILを再現できませんでした')
                return 1
            print('Before compensation regressions FAIL reproduced (expected)')
        exe = build / 'after.exe'
        # 設定間の併用制約も実ソースでcompileする。フィルタ本体は上のUnity API compileで確認済み。
        filter_modes = build / 'FilterModes.cs'
        filter_modes.write_text('public static class PhoneSaberEndpointFilter { public const int Off=0, Weak=1, Medium=2, Steady=3, Responsive=4; }')
        if run(compiler + ['/define:AFTER', '/r:' + str(nunit), '/out:' + str(exe)] + list(map(str, host_sources + [INPUT / 'PhoneSaberPerfLog.cs', INPUT / 'PhoneSaberLatencyEstimate.cs', INPUT / 'PhoneSaberPredictionSettings.cs', INPUT / 'PhoneSaberFilterSettings.cs', filter_modes, compensation]))):
            return 1
        return run([str(mono / 'bin/mono'), str(exe)], env=host_env)


if __name__ == '__main__':
    raise SystemExit(main())
