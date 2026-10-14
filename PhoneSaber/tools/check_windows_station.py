#!/usr/bin/env python3
"""GUIなしで実探索socketのWindows定義と過大datagram後の復帰を検査する。"""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
INPUT = ROOT / 'Assets/Scripts/Managers/Inputsystem/Input'


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before-ref')
    args = parser.parse_args()
    editor = Path(os.environ.get('PHONESABER_UNITY_CONTENTS',
                                '/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents'))
    mono = editor / 'Resources/Scripting/MonoBleedingEdge'
    nunit = editor / 'Resources/PackageManager/BuiltInPackages/com.unity.ext.nunit/net40/unity-custom/nunit.framework.dll'
    with tempfile.TemporaryDirectory(prefix='phonesaber-windows-') as temporary:
        folder = Path(temporary)
        source = INPUT / 'PhoneSaberDiscoveryResponder.cs'
        if args.before_ref:
            source = folder / source.name
            source.write_bytes(subprocess.check_output([
                'git', 'show', args.before_ref + ':Assets/Scripts/Managers/Inputsystem/Input/' + source.name], cwd=ROOT))
        binary = folder / 'tests.exe'
        subprocess.run([str(mono / 'bin/mono'), str(mono / 'lib/mono/4.5/csc.exe'), '/nologo',
                        '/langversion:9.0', '/define:UNITY_STANDALONE_WIN', '/r:' + str(nunit),
                        '/out:' + str(binary), str(Path(__file__).with_name('windows_station_tests.cs.txt')),
                        str(source), str(INPUT / 'PhoneSaberInputStats.cs'),
                        str(INPUT / 'PhoneSaberLatencyEstimate.cs')], check=True)
        return subprocess.run([str(mono / 'bin/mono'), str(binary)], cwd=ROOT,
                              env=dict(os.environ, MONO_PATH=str(nunit.parent))).returncode


if __name__ == '__main__':
    raise SystemExit(main())
