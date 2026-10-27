#!/usr/bin/env python3
"""旧/新の実EventLog保存処理で同期ゲートと同一列benchmarkを再実行する（実機・Unity起動不要）。"""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
INPUT = ROOT / 'Assets/Scripts/Managers/Inputsystem/Input'
SOURCE = 'Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberEventLog.cs'


def instrument(source, before):
    if before:
        # 旧保存ロジックは変更せず、新版と同じ直前ゲートだけを挿入する。
        signature = 'public PhoneSaberEventLog(string logPath) { LogPath = logPath; }'
        assert source.count(signature) == 1, 'before-ref は同期Flush版を指定してください'
        source = source.replace(signature, '''readonly Action beforeSave;
    public PhoneSaberEventLog(string logPath, Action beforeSave = null)
    { LogPath = logPath; this.beforeSave = beforeSave; }''')
        point = '            Directory.CreateDirectory(Path.GetDirectoryName(LogPath));'
        assert source.count(point) == 1
        source = source.replace(point, '            beforeSave?.Invoke();\n' + point)
    end = ('LastError' if before else 'lastError') + ' = rotationError ?? "";'
    assert source.count(end) == 1
    return source.replace(end, 'EventLogWriterHost.AfterIo();\n            ' + end)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before-ref', default='35a60bf909cd44ac6683ddddb4ba6bdb0031e1bb',
                        help='比較する同期Flush版のGit ref')
    args = parser.parse_args()
    editor = Path(os.environ.get('PHONESABER_UNITY_CONTENTS',
                  '/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents'))
    mono = editor / 'Resources/Scripting/MonoBleedingEdge'
    compiler = [str(mono / 'bin/mono'), str(mono / 'lib/mono/4.5/csc.exe'),
                '/nologo', '/langversion:9.0', '/optimize+']
    old = subprocess.check_output(['git', 'show', args.before_ref + ':' + SOURCE], cwd=ROOT).decode()
    clock = (INPUT / 'SwingEventStream.cs').read_text()
    clock = 'using System.Diagnostics;\n' + clock[clock.index('public static class SwingMonotonicClock'):clock.index('public static class SwingPacketParser')]
    with tempfile.TemporaryDirectory(prefix='phonesaber-event-log-') as tmp:
        build = Path(tmp)
        clock_path = build / 'Clock.cs'
        clock_path.write_text(clock)
        for before, source in ((True, old), (False, (ROOT / SOURCE).read_text())):
            label = 'before' if before else 'after'
            output = build / label
            output.mkdir()
            path = build / (label + '.cs')
            path.write_text(instrument(source, before))
            exe = build / (label + '.exe')
            command = compiler + (['/define:BEFORE'] if before else []) + ['/out:' + str(exe)]
            command += list(map(str, [path, clock_path, INPUT / 'PhoneSaberInputStats.cs',
                           Path(__file__).with_name('pc_latency_tests') / 'EventLogWriterHost.cs']))
            subprocess.run(command, check=True)
            print('=== ' + label + ' ===', flush=True)
            subprocess.run([str(mono / 'bin/mono'), str(exe), str(output)], check=True)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
