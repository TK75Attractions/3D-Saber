#!/usr/bin/env python3
"""HEAD旧版/新版の実journalでI/O停止の同期証明と同一通知列のMono比較を行う。"""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SOURCE = 'Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberEventLog.cs'


def instrument(source, before):
    # 保存処理は実コードのまま。旧版にも新版と同じ保存直前ゲートだけを追加する。
    start = source.index('public sealed class PhoneSaberRunJournal')
    end = source.index('// datagram の到着', start)
    source = 'using System;\nusing System.Collections.Generic;\nusing System.IO;\nusing System.Threading;\nusing System.Diagnostics;\n' + source[start:end]
    if before:
        signature = 'public PhoneSaberRunJournal(string path) { this.path = path; }'
        assert source.count(signature) == 1, 'before-refには同期Note版を指定してください'
        source = source.replace(signature, '''readonly Action beforeSave;
    public PhoneSaberRunJournal(string path, Action beforeSave = null)
    { this.path = path; this.beforeSave = beforeSave; }''')
        point = '            Directory.CreateDirectory(Path.GetDirectoryName(path));'
        assert source.count(point) == 1
        source = source.replace(point, '            beforeSave?.Invoke();\n' + point)
    if before:
        point = '            else File.Move(path + ".tmp", path);'
        assert source.count(point) == 1
        return source.replace(point, point + '\n            RunJournalWriterHost.AfterIo();')
    # 補助印の確認・削除も含む、成功した保存全体の割当を計測する。
    point = '            lastError = "";\n            return true;'
    assert source.count(point) == 1
    return source.replace(point, '            RunJournalWriterHost.AfterIo();\n' + point)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before-ref', default='d93b29826188d75c2063845e74c26c30ae99a5bb',
                        help='着手時の同期Note版Git ref')
    args = parser.parse_args()
    before_sha = subprocess.check_output(['git', 'rev-parse', args.before_ref], cwd=ROOT, text=True).strip()
    print('Comparison base: ' + before_sha, flush=True)
    editor = Path(os.environ.get('PHONESABER_UNITY_CONTENTS',
                  '/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents'))
    mono = editor / 'Resources/Scripting/MonoBleedingEdge'
    compiler = [str(mono / 'bin/mono'), str(mono / 'lib/mono/4.5/csc.exe'),
                '/nologo', '/langversion:9.0', '/optimize+']
    old = subprocess.check_output(['git', 'show', before_sha + ':' + SOURCE], cwd=ROOT, text=True)
    with tempfile.TemporaryDirectory(prefix='phonesaber-run-journal-') as tmp:
        build = Path(tmp)
        for before, source in ((True, old), (False, (ROOT / SOURCE).read_text())):
            label = 'before' if before else 'after'
            output = build / label
            output.mkdir()
            path = build / (label + '.cs')
            path.write_text(instrument(source, before))
            exe = build / (label + '.exe')
            command = compiler + (['/define:BEFORE'] if before else []) + ['/out:' + str(exe)]
            command += list(map(str, [path, Path(__file__).with_name('pc_latency_tests') / 'RunJournalWriterHost.cs']))
            subprocess.run(command, check=True)
            print('=== ' + label + ' ===', flush=True)
            subprocess.run([str(mono / 'bin/mono'), str(exe), str(output)], check=True, timeout=180)
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
