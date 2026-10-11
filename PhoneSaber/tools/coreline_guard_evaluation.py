#!/usr/bin/env python3
"""実装 ON/OFF と offline shadow の照合。画像は読取のみ、派生物は TMPDIR 限定。"""
from __future__ import annotations

import argparse
from collections import Counter
import json
import os
from pathlib import Path
import struct
import subprocess
import tempfile

import coreline_shadow as shadow
import recognition_benchmark as benchmark


def build(destination, enabled=False, baseline=False):
    core = shadow.CORE
    destination.mkdir(parents=True, exist_ok=True)
    if baseline:
        # branch の出発点の認識コード。共有 .git は読取だけ。
        for name in ('include/phonesaber/core.hpp', 'src/internal.hpp', 'src/detection.cpp',
                     'src/pipeline.cpp', 'src/frame_processor.cpp', 'tools/png_cli.cpp',
                     'tools/png.cpp', 'tools/png.hpp'):
            path = destination / name
            path.parent.mkdir(parents=True, exist_ok=True)
            relative = (core / name).relative_to(benchmark.PHONE.parent)
            path.write_bytes(subprocess.check_output(['git', 'show', f'HEAD:{relative}']))
        core = destination
    cli = destination / 'phonesaber-png'
    subprocess.run(['clang++', '-std=c++17', '-O2', '-Wall', '-Wextra', '-Werror',
                    '-ffp-contract=off', *([] if enabled else ['-DPHONESABER_DISABLE_CORELINE_GUARD']),
                    '-I'+str(core/'include'), *(str(core/'src'/n) for n in
                    ('detection.cpp','pipeline.cpp','frame_processor.cpp')),
                    str(core/'tools/png_cli.cpp'),str(core/'tools/png.cpp'),'-lz','-o',str(cli)],
                   check=True)
    return cli


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    root = Path(os.environ.get('TMPDIR', tempfile.gettempdir())).resolve()
    if not args.output.resolve().is_relative_to(root):
        parser.error('derived files must be under TMPDIR')
    args.output.mkdir(parents=True, exist_ok=True)
    downloads = Path.home()/'Downloads'
    inbox = Path.home()/'Library/Application Support/PhoneSaber/diagnostics-inbox'
    images, _ = benchmark.discover(downloads,inbox)
    old = [i for i in images if not (i.origin == 'inbox' and 'phonesaber_20261010_' in str(i.path))]
    sessions = [i for i in images if any('phonesaber_20261010_'+s in str(i.path) for s in shadow.SESSIONS)]
    inventory = benchmark.signature(sorted((i.digest,i.occurrence) for i in old))
    if inventory != shadow.BASELINE_INVENTORY or len(sessions) != 22:
        raise ValueError('study inventory changed')
    truths, _, missing = benchmark.truth_index(benchmark.LABELS,benchmark.MANIFEST,downloads,inbox)
    if missing:
        raise ValueError('label evidence missing')
    off = build(args.output/'off')
    on = build(args.output/'on',enabled=True)
    baseline = build(args.output/'baseline',baseline=True)
    probe = shadow.build_probe(args.output/'probe')
    all_rows, actual_rows = [], []
    off_errors, shadow_errors, formal_rows = [], [], []
    for group, entries in (('benchmark',old),('sessions',sessions)):
        rows = benchmark.measure(off,entries,truths)
        for image,row in zip(entries,rows):
            main_colors = json.loads(subprocess.check_output([str(baseline),str(image.path)]))['colors']
            if benchmark.signature(main_colors) != row['recognition_sha256']:
                off_errors.append(str(image.path))
            observed = shadow.observe(probe,image,row)
            width,height = struct.unpack('>II',image.path.read_bytes()[16:24])
            observed.update(group=group,area_pixels=width*height)
            if group == 'sessions':
                observed.update(session=next(s for s in shadow.SESSIONS if s in str(image.path)),
                                frame=int(image.path.stem.split('_')[-1]))
                truth = 'saber' if observed['session'] == shadow.SESSIONS[0] and observed['frame'] in shadow.DAYLIGHT_SABER_FRAMES else 'no_saber'
                observed['truth'] = {c:truth for c in benchmark.COLORS}
            actual = json.loads(subprocess.check_output([str(on),str(image.path)]))
            for color in benchmark.COLORS:
                predicted = shadow.select(observed['colors'][color],color,{'guarded_lit':.6},width*height)
                winner = benchmark.winner(actual['colors'][color])
                if shadow.output_key(predicted) != shadow.output_key(winner):
                    shadow_errors.append([str(image.path),color])
            all_rows.append(observed)
            actual_rows.append({**observed,'colors':actual['colors']})
        print(group,len(entries),'evaluated; OFF errors',len(off_errors),'shadow errors',len(shadow_errors),flush=True)
    # 正式 checker の source / endpoint 許容差 / 必須棄却をそのまま適用。
    import sys
    sys.path.insert(0,str(benchmark.PHONE/'ios/PhoneSaberSender/Tools'))
    import run_lossless_regression as formal
    _,fixtures = formal.load_fixtures(benchmark.MANIFEST)
    formal_selection_changes = []
    for fixture in fixtures:
        analysis = json.loads(subprocess.check_output([str(on),str(fixture['resolved_path'])]))
        before = json.loads(subprocess.check_output([str(off),str(fixture['resolved_path'])]))
        color = formal.COLORS[fixture['color']]
        a,b = benchmark.winner(before['colors'][color]),benchmark.winner(analysis['colors'][color])
        if shadow.output_key(a) != shadow.output_key(b) or (a and b and a['production']['double_bits'] != b['production']['double_bits']):
            formal_selection_changes.append(fixture['name'])
        formal_rows.append(formal.evaluate_fixture(fixture,{**analysis,'profile':{'total_ms':0}}))
    unique = benchmark.unique_rows(all_rows)
    actual_unique = benchmark.unique_rows(actual_rows)
    summary = {}
    for group in ('benchmark','sessions'):
        summary[group] = {}
        for color in benchmark.COLORS:
            counts = Counter()
            for before,after in zip(unique,actual_unique):
                if before['group'] != group: continue
                truth = before['truth'][color]
                a,b = benchmark.winner(before['colors'][color]),benchmark.winner(after['colors'][color])
                counts[truth+'_n'] += 1
                counts[truth+'_before'] += a is not None
                counts[truth+'_after'] += b is not None
                counts[truth+'_changed'] += shadow.output_key(a) != shadow.output_key(b)
                counts[truth+'_lost'] += a is not None and b is None
            summary[group][color] = dict(counts)
    result = {'inventory_sha256':inventory,'off_main_bit_mismatches':off_errors,
              'on_shadow_mismatches':shadow_errors,'formal_selection_changes':formal_selection_changes,'formal':formal.summarize(formal_rows),
              'summary':summary,'session_winners':[r for r in actual_rows if r['group']=='sessions']}
    (args.output/'evaluation.json').write_text(json.dumps(result,ensure_ascii=False,indent=2))
    (args.output/'observed.json').write_text(json.dumps(all_rows))
    print(json.dumps({k:v for k,v in result.items() if k!='session_winners'},ensure_ascii=False),flush=True)
    if off_errors or shadow_errors or formal_selection_changes or result['formal']['failed']:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
