#!/usr/bin/env python3
"""GUIを起動せず全runtimeのUnity API compileとsmokeのstandalone NUnitを実行。"""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SMOKE = ROOT / 'Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberPlayerSmoke.cs'


def run(command, **kwargs):
    result = subprocess.run(list(map(str, command)), text=True, capture_output=True, **kwargs)
    print(result.stdout, end='')
    print(result.stderr, end='')
    return result.returncode


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before-ref', help='旧smokeで回帰FAILも確認するgit ref')
    args = parser.parse_args()
    editor = Path(os.environ.get('PHONESABER_UNITY_CONTENTS', '/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents'))
    mono = editor / 'Resources/Scripting/MonoBleedingEdge'
    managed = editor / 'Resources/Scripting/Managed/UnityEngine'
    template = editor / 'Resources/PackageManager/ProjectTemplates/libcache/com.unity.template.2d-cross-platform-2d-6.1.2/ScriptAssemblies'
    nunit = editor / 'Resources/PackageManager/BuiltInPackages/com.unity.ext.nunit/net40/unity-custom/nunit.framework.dll'
    compiler = [mono / 'bin/mono', mono / 'lib/mono/4.5/csc.exe', '/nologo', '/langversion:9.0']
    refs = list(managed.glob('UnityEngine*.dll'))
    refs += [p for p in template.glob('*.dll') if not p.name.startswith('UnityEditor.') and p.name != 'Assembly-CSharp.dll']
    refs += [mono / 'lib/mono/4.5/Facades/netstandard.dll']
    sources = sorted((ROOT / 'Assets/Scripts').rglob('*.cs'))
    with tempfile.TemporaryDirectory(prefix='phonesaber-player-smoke-') as tmp:
        build = Path(tmp)
        for platform in ('UNITY_STANDALONE_OSX', 'UNITY_STANDALONE_WIN', 'UNITY_EDITOR,UNITY_EDITOR_OSX', 'UNITY_EDITOR,UNITY_EDITOR_WIN'):
            assembly = build / 'SaberGame.dll'
            platform_refs = refs + (list(managed.glob('UnityEditor*.dll')) if platform.startswith('UNITY_EDITOR') else [])
            reference_args = ['/r:' + str(p) for p in platform_refs]
            if run(compiler + ['/target:library', '/define:' + platform, '/out:' + str(assembly)] + reference_args + sources):
                return 1
            print('Unity API compile PASS: ' + platform + ' runtime files=' + str(len(sources)))
            if platform.startswith('UNITY_EDITOR'):
                tests = [ROOT / 'Assets/Tests/Editor' / name for name in
                         ('PhoneSaberBuildShaderTests.cs', 'PhoneSaberSoakTests.cs')]
                if run(compiler + ['/target:library', '/out:' + str(build / 'SaberTests.Editor.dll'), '/r:' + str(nunit), '/r:' + str(assembly)] + reference_args + tests):
                    return 1
                # Editor用PhoneSaber build/検証コードも実APIを参照する。
                if run(compiler + ['/target:library', '/define:' + platform, '/out:' + str(build / 'PhoneSaber.Editor.dll'), '/r:' + str(assembly)] + reference_args + sorted((ROOT / 'Assets/Editor').glob('PhoneSaber*.cs'))):
                    return 1
                print('PhoneSaber Editor scripts / shader-smoke test assembly compile PASS')
        env = dict(os.environ, MONO_PATH=str(nunit.parent))
        host = Path(__file__).with_name('player_smoke_tests') / 'Host.cs'
        smoke_sources = [('after', SMOKE)]
        if args.before_ref:
            before = build / 'Before.cs'
            before.write_bytes(subprocess.check_output(['git', 'show', args.before_ref + ':' + str(SMOKE.relative_to(ROOT))], cwd=ROOT))
            smoke_sources.insert(0, ('before', before))
        for label, source in smoke_sources:
            exe = build / (label + '.exe')
            if run(compiler + ['/r:' + str(nunit), '/out:' + str(exe), host, source]):
                return 1
            code = run([mono / 'bin/mono', exe], env=env)
            if code != (1 if label == 'before' else 0):
                print('ERROR: unexpected NUnit result ' + label)
                return 1
            print(label + ': expected ' + ('regression FAIL' if label == 'before' else 'PASS'))
        exe = build / 'soak-flow.exe'
        if run(compiler + ['/define:SOAK', '/r:' + str(nunit), '/out:' + str(exe), host, SMOKE,
                           SMOKE.with_name('PhoneSaberPlayerSoak.cs'), SMOKE.with_name('PhoneSaberSoakModel.cs')]):
            return 1
        if run([mono / 'bin/mono', exe], env=env):
            return 1
        print('Soak coroutine host PASS (Unity native / transport substituted)')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
