#!/usr/bin/env python3
"""低ISO/emitter ON の保存PNGを本番 analyzeSabers で測る（画像・結果はGit外）。

Pillow / NumPy が必要。context は本番step=2の点サンプルだけを保持するため、最近傍で
原寸に戻す。マスクのサンプルは保持するが、blueNoDeepSupport等の原寸近傍画素は
復元できないためcontextの再解析は近似。eventの原寸解析とは区別する。
5秒窓の未保存フレームは再解析できない。windowRecordedは録画時の値であり、
保存PNGの再解析・変更後の出力と混ぜない。--baseline は同じPNG/設定の実測比較。
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from concurrent.futures import ThreadPoolExecutor
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile

TOOLS = Path(__file__).resolve().parents[1] / 'ios/PhoneSaberSender/Tools'
sys.path.insert(0, str(TOOLS))
import field_regression as field
import run_lossless_regression as lossless
from PIL import Image
import numpy as np


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def measure_clip(item, harness):
    bundle, summary = item
    metadata = summary['autoClip']
    camera = metadata['camera']
    mode = {'shutter': camera['shutter'], 'iso': camera['shutterISO'], 'enabled': camera['shutterEmitter']}
    lossless.validate_shutter_emitter(mode)
    field.validate_frame_integrity(bundle, summary)
    size = (metadata['frameSize']['width'], metadata['frameSize']['height'])
    images = [i for i in summary['images'] if i['role'] in ('event', 'context')]
    payload, backgrounds, hashes = [], [], []
    for item in images:
        path = bundle / item['path']
        hashes.append(sha(path))
        with Image.open(path) as original:
            image = original.convert('RGBA')
            if image.size != size:
                context = metadata['contextImages']
                if (item['role'] != 'context' or context['scale'] != 0.5
                        or 'point sample' not in context['sampling']
                        or image.size != ((size[0] + 1) // 2, (size[1] + 1) // 2)):
                    raise ValueError(f'本番の点サンプルを再現できません: {path}')
                # 奇数寸法でも各元画素を正確に2回ずつ繰り返す。
                image = image.resize((image.width * 2, image.height * 2), Image.Resampling.NEAREST)
                image = image.crop((0, 0, *size))
            samples = np.asarray(image)[::2, ::2, :3].max(axis=2)
            histogram = np.bincount(samples.ravel(), minlength=256)
            median = int(np.searchsorted(histogram.cumsum(), samples.size // 2, side='right'))
            backgrounds.append(median)
            payload.append(image.tobytes('raw', 'BGRA'))
    command = [str(harness), *map(str, size), '2', str(mode['shutter']), str(mode['iso']), '1']
    result = subprocess.run(command, input=b''.join(payload), capture_output=True, check=True,
                            env={**os.environ, 'PHONESABER_EMITTER_METRICS': '1'})
    analyses = [json.loads(line) for line in result.stdout.splitlines()]
    if len(analyses) != len(images):
        raise ValueError(f'解析行数不一致: {bundle}')
    rows = []
    for item, analysis, background, digest in zip(images, analyses, backgrounds, hashes):
        candidates = analysis['colors']['blue']['candidates']
        rows.append({'clip': bundle.name, 'image': item['path'], 'frameID': item['frameID'],
                     'role': item['role'], 'contextApproximation': item['role'] == 'context',
                     'sha256': digest, 'date': bundle.name.split('_')[4],
                     'shutterEmitter': mode, 'backgroundMedian': background,
                     'selected': {c: analysis['colors'][c]['selected'] for c in ('red', 'blue')},
                     'blueEmitterCandidates': [{**c, 'componentArea': c['point_count'] * 4}
                                               for c in candidates if c['source'] == 'shutter-color-emitter']})
    frames = metadata['frames']
    window = {'clip': bundle.name, 'frames': len(frames),
              'blueOutputRecorded': sum(f['blue']['sel'] is not None for f in frames)}
    return rows, window


def compare(rows, baseline):
    def key(row):
        return row['clip'], row['image']
    old = {key(r): r for r in baseline}
    if len(old) != len(baseline) or set(old) != {key(r) for r in rows}:
        raise ValueError('比較画像集合が異なります')
    totals, per_clip, changes = defaultdict(Counter), defaultdict(Counter), []
    for row in rows:
        before = old[key(row)]
        if any(before[k] != row[k] for k in ('sha256', 'shutterEmitter')):
            raise ValueError(f'比較画像/設定が異なります: {key(row)}')
        b, a = before['selected']['blue'] is not None, row['selected']['blue'] is not None
        group = (row['date'], row['shutterEmitter']['iso'])
        for counts in (totals[group], per_clip[row['clip']]):
            counts.update(images=1, before=int(b), after=int(a),
                          disappeared=int(b and not a), appeared=int(a and not b))
        if before['selected'] != row['selected']:
            changes.append({'clip': row['clip'], 'image': row['image'], 'frameID': row['frameID'],
                            'before': before['selected'], 'after': row['selected']})
    return {'byDateISO': [{'date': d, 'iso': iso, **counts} for (d, iso), counts in sorted(totals.items())],
            'byClip': [{'clip': clip, **counts} for clip, counts in sorted(per_clip.items())],
            'changes': changes}


def project_recorded_window(summary, peak):
    # 未保存画像は再解析しない。丸め済み候補の適格性だけを落とす参考推計。
    metadata = summary['autoClip']
    counts = Counter()
    for frame in metadata['frames']:
        blue = frame['blue']
        candidates = [dict(zip(metadata['candidateColumns'], c)) for c in blue['c']]
        eligible = [c for c in candidates if c['flags'] & 1]
        remaining = [c for c in eligible
                     if metadata['sources'][c['source']] != 'shutter-color-emitter'
                     or c['peakValue'] >= peak]
        counts.update(frames=1, before=int(blue['sel'] is not None),
                      projectedAfter=int(bool(remaining)),
                      projectedDisappeared=int(blue['sel'] is not None and not remaining),
                      projectedAppeared=int(blue['sel'] is None and bool(remaining)),
                      incompleteEligibility=int(len(eligible) != blue['e']))
    return {'minimumBluePeak': peak, 'kind': 'recorded-candidate-projection-not-pixel-reanalysis', **counts}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--inbox', type=Path, default=field.DEFAULT_INBOX)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--baseline', type=Path)
    parser.add_argument('--workers', type=int, default=4)
    parser.add_argument('--window-peak', type=int, choices=range(0, 256), metavar='0..255',
                        help='未保存フレームは録画済み候補だけでpeak下限の参考推計（本番出力の再現ではない）')
    args = parser.parse_args()
    os.umask(0o077)
    output = field.outside_git(args.output)
    if output.exists() or args.workers < 1:
        parser.error('output は新規のGit外ファイル、workers は1以上が必要です')
    clips = []
    for path in sorted(field.outside_git(args.inbox).glob('*/summary.json')):
        summary = json.loads(path.read_text())
        camera = summary.get('autoClip', {}).get('camera', {})
        iso = camera.get('shutterISO')
        if camera.get('shutterEmitter') is True and type(iso) is int and 0 < iso < 2000:
            clips.append((path.parent, summary))
    if not clips:
        parser.error('対象クリップがありません')
    projections = ([{'clip': bundle.name, **project_recorded_window(summary, args.window_peak)}
                    for bundle, summary in clips] if args.window_peak is not None else [])
    sources = {str(p.relative_to(lossless.REPO)): sha(p) for p in (
        lossless.REPO / 'ios/PhoneSaberSender/PhoneSaberSender/DetectionCore.swift',
        lossless.REPO / 'ios/PhoneSaberSender/PhoneSaberSender/BGRADetection.swift',
        lossless.REPO / 'ios/PhoneSaberSenderTests/VideoDetectionDiagnostic.swift')}
    rows, windows = [], []
    with tempfile.TemporaryDirectory(prefix='phonesaber-blue-emitter-') as temporary:
        for name in ('CLANG_MODULE_CACHE_PATH', 'SWIFT_MODULECACHE_PATH'):
            os.environ[name] = str(Path(temporary) / 'module-cache')
        harness = Path(temporary) / 'detector'
        lossless.compile_harness(harness)
        with ThreadPoolExecutor(max_workers=args.workers) as pool:
            for index, (measured, window) in enumerate(pool.map(lambda item: measure_clip(item, harness), clips), 1):
                rows.extend(measured)
                windows.append(window)
                if index % 20 == 0:
                    print(f'{index}/{len(clips)} clips', file=sys.stderr, flush=True)
    document = {'schemaVersion': 1, 'detectorSources': sources,
                'swiftFlags': lossless.guard_build.swift_flags(), 'sampleStep': 2,
                'contextReconstruction': 'nearest repeat 2x; mask samples preserved, full-resolution neighborhood gates approximate',
                'rows': rows, 'windowRecorded': windows, 'windowProjection': projections}
    if args.baseline:
        document['comparison'] = compare(rows, json.loads(args.baseline.read_text())['rows'])
    else:
        document['comparison'] = compare(rows, rows)
    field.write_json(output, document)
    print(json.dumps({'clips': len(clips), 'images': len(rows), **document['comparison']}, ensure_ascii=False))


if __name__ == '__main__':
    main()
