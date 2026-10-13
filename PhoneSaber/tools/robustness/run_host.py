#!/usr/bin/env python3
"""GUI なしで Unity Mono / Swift host harness を build・実行する。"""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
INPUT = ROOT / 'Assets/Scripts/Managers/Inputsystem/Input'
IOS = ROOT / 'PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender'
MONO = Path('/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents/Resources/Scripting/MonoBleedingEdge')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('target', choices=['csharp', 'swift'])
    parser.add_argument('--seed', type=int, default=20261013)
    parser.add_argument('--seconds', type=float, default=120)
    args = parser.parse_args()
    assert 0 < args.seconds <= 180
    with tempfile.TemporaryDirectory(prefix='phonesaber-host-fuzz-') as temporary:
        build = Path(temporary)
        env = dict(os.environ, CLANG_MODULE_CACHE_PATH=str(build / 'clang-cache'))
        if args.target == 'csharp':
            binary = build / 'check.exe'
            command = [str(MONO / 'bin/mono'), str(MONO / 'lib/mono/4.5/csc.exe'),
                       '/nologo', '/langversion:9.0', '/define:PHONESABER_ALLOC_BENCH', f'/out:{binary}',
                       str(HERE / 'ReceiverCheck.cs'), str(ROOT / 'Assets/Tests/Editor/PhoneSaberPacketParserTests.cs'),
                       *[str(INPUT / name) for name in ['PhoneSaberPacketParser.cs', 'PhoneSaberInputStats.cs', 'PhoneSaberEventLog.cs']]]
            run = [str(MONO / 'bin/mono'), str(binary)]
        else:
            binary = build / 'check'
            command = ['xcrun', 'swiftc', '-O', '-module-cache-path', str(build / 'cache'),
                       str(IOS / 'P2PProtocol.swift'), str(IOS / 'DetectionCore.swift'),
                       str(HERE / 'CodecCheck.swift'), '-o', str(binary)]
            run = [str(binary)]
        subprocess.run(command, check=True, timeout=120, env=env)
        subprocess.run(run + [str(args.seed), str(args.seconds)], check=True, timeout=args.seconds+30, env=env)


if __name__ == '__main__':
    main()
