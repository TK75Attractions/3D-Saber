#!/usr/bin/env python3
"""Unity実DLLで4構成をcompileし、実PerfLogと純粋部分をstandalone NUnitで実行する。"""
import argparse
import os
from pathlib import Path
import subprocess
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
public static class GameSession {public static string SelectedSongId,SelectedDifficulty;public static bool IsCalibrationMode,TutorialPending;}
public class SaberBladeVisual : MonoBehaviour { public void Clear(){} public void NotifyCut(){} public void Show(Vector3 a,Vector3 b,float w,Color c,float t){} }
public static class DisplaySettings { public static bool ProjectorMode; }
public static class SaberCutJudge { public static float DistPointToSegment(Vector2 p,Vector2 a,Vector2 b,out Vector2 q){q=a;return 0;} }
public enum CameraSaberColor { Red, Blue }
public static class ProjectorMode { public const float BladeWidthScale=1,BladeOutlineScale=1,BladeOutlineZOffset=0; public static Color BladeOutlineColor; }
''')
        refs = ['/r:' + str(p) for p in (editor / 'Resources/Scripting/Managed/UnityEngine').glob('*.dll')]
        refs += ['/r:' + str(template / 'Unity.InputSystem.dll'), '/r:' + str(mono / 'lib/mono/4.5/Facades/netstandard.dll')]
        sources = sorted(INPUT.glob('PhoneSaber*.cs')) + [INPUT / 'InputPoint.cs', INPUT / 'SwingEventStream.cs', ROOT / 'Assets/Scripts/Saber/SaberInputBridge.cs', ROOT / 'Assets/Scripts/Saber/MouseSaberInput.cs', ROOT / 'Assets/Scripts/Saber/CameraSaberHistory.cs', stubs]
        for platform in ('UNITY_EDITOR,UNITY_EDITOR_OSX', 'UNITY_EDITOR,UNITY_EDITOR_WIN', 'UNITY_STANDALONE_OSX', 'UNITY_STANDALONE_WIN'):
            code = run(compiler + ['/target:library', '/define:' + platform, '/out:' + str(build / (platform.replace(',', '-') + '.dll'))] + refs + list(map(str, sources)))
            if code:
                return code
            print('Unity 6000.3.9f1 API compile PASS: ' + platform)
            if platform.startswith('UNITY_EDITOR'):
                tests = [ROOT / 'Assets/Tests/Editor/PhoneSaberLatencyCompensationTests.cs',
                         ROOT / 'Assets/Tests/Editor/PhoneSaberLatencyProbeTests.cs']
                if run(compiler + ['/target:library', '/r:' + str(nunit),
                                   '/out:' + str(build / (platform.replace(',', '-') + '-tests.dll'))] + refs + list(map(str, sources + tests))):
                    return 1
                print('Compensation EditMode test assembly API compile PASS: ' + platform)
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
        if run(compiler + ['/define:AFTER', '/r:' + str(nunit), '/out:' + str(exe)] + list(map(str, host_sources + [INPUT / 'PhoneSaberPerfLog.cs', INPUT / 'PhoneSaberLatencyEstimate.cs', INPUT / 'PhoneSaberPredictionSettings.cs', compensation]))):
            return 1
        return run([str(mono / 'bin/mono'), str(exe)], env=host_env)


if __name__ == '__main__':
    raise SystemExit(main())
