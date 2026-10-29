#!/usr/bin/env python3
"""AutoClip v2 保存候補を本番Swift選択器で再生する。画像の正解判定は別途必要。

--source-root は比較checkoutの PhoneSaber/。--stationary-seconds/radius/motion
はsweep用の一時ビルドだけに適用し、productionソースを書き換えない。
クリップ開始前の状態は不明。候補欠落・timestampの断絶では履歴をresetする。
最初の記録winnerで選択を初期化するが、直前の速度/復帰/選好履歴は復元できない。
得点は記録精度（0.1点）、ineligibleは保存上限（通常2件）のまま使う。
短縮救済の原画素safety gate・無視範囲は保存候補だけでは再現できず、救済数は上限評価。
ROI外の候補や非静止の候補を、画像で確認せず実刃の正解とは扱わない。
"""
import argparse
import json
import math
import os
from pathlib import Path
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
SOURCE = HERE.parent / 'PhoneSaberSender'


def _clip_background(path):
    import numpy as np
    from PIL import Image
    image = sorted((path / 'images').glob('*event*.png'))[0]
    return int(np.median(np.asarray(Image.open(image).convert('RGB')).max(2)))


def load_clip(path):
    summary = json.loads((path / 'summary.json').read_text())
    clip = summary['autoClip']
    columns = clip['candidateColumns']
    assert columns[:12] == ['rank', 'x1', 'y1', 'x2', 'y2', 'minX', 'minY', 'maxX', 'maxY', 'score', 'flags', 'source']
    frames, previous, seeded = [], None, set()
    # 低ISO発光体候補の最大値の下限（背景×5、上限220）を再生だけで試す。環境変数で背景値を与える。
    peak_floor = None
    if os.environ.get('PHONESABER_REPLAY_EMITTER_PEAK_FLOOR'):
        background = _clip_background(path)
        ratio = 3 + 2 * min(max((background - 24) / 16, 0), 1)
        peak_floor = min(background * ratio, 220)
    sources = clip.get('sources', [])
    def dim_emitter(c):
        return (peak_floor is not None and int(c[11]) < len(sources)
                and sources[int(c[11])] == 'shutter-color-emitter' and (c[12] or 0) < peak_floor)
    for frame in clip['frames']:
        complete = all(sum(int(c[10]) & 1 != 0 for c in frame[color]['c']) == frame[color]['e'] for color in ('red', 'blue'))
        reset = not complete or (previous is not None and (frame['f'] != previous['f'] + 1 or not 0 <= frame['t'] - previous['t'] <= .25))
        row = {'f': frame['f'], 't': frame['t'], 'reset': reset,
               'observedTruncated': any(frame[c]['n'] > len(frame[c]['c']) for c in ('red', 'blue')),
               'recorded': {c: frame[c]['sel'] for c in ('red', 'blue')}}
        row['observed'] = {}
        if reset:
            seeded.clear()
        seed = {}
        for color in ('red', 'blue'):
            area = clip.get('playArea') or [0, 0, 1, 1]
            def allowed(c):
                if not int(c[10]) & 1 or dim_emitter(c):
                    return False
                if int(c[10]) & 6:
                    return True
                x = (c[1] + c[3]) / (2 * clip['frameSize']['width'])
                y = (c[2] + c[4]) / (2 * clip['frameSize']['height'])
                # 範囲外は追跡中に選べる。範囲内の非selectableだけが無視物体。
                return not (area[0] <= x <= area[2] and area[1] <= y <= area[3])
            row[color] = [[float(v) if isinstance(v, (int, float)) else 0 for v in c]
                          for c in sorted(frame[color]['c'], key=lambda c: c[0]) if allowed(c)] if complete else []
            row['observed'][color] = [[float(v) if isinstance(v, (int, float)) else 0 for v in c]
                                      for c in sorted(frame[color]['c'], key=lambda c: c[0])] if complete else []
            if color not in seeded:
                selected = frame[color]['sel']
                seed[color] = selected
                if selected:
                    seeded.add(color)
        row['seed'] = seed
        frames.append(row)
        previous = frame if complete else None
    return {'name': path.name, **clip['frameSize'], 'area': clip.get('playArea'), 'sources': sources, 'frames': frames}


def comparison(clips, before, after, static_roi=None):
    """ROIは手動の測定ラベル。ROI外を実刃の正解とは断定しない。"""
    def midpoint(e):
        return ((e[0] + e[2]) / 2, (e[1] + e[3]) / 2)

    def stationary(e):
        return bool(e and static_roi and math.dist(midpoint(e), static_roi[:2]) <= static_roi[2])

    results = []
    for clip, b, a in zip(clips, before, after):
        colors = {}
        for color in ('red', 'blue'):
            counts = dict(frames=0, changed=0, lost=0, gained=0, baselineRecordedMismatch=0,
                          observedTruncatedFrames=0,
                          staticBefore=0, staticAfter=0, separated=0, alternativeBefore=0, alternativeAfter=0)
            changed = []
            for f, fb, fa in zip(clip['frames'], b['frames'], a['frames']):
                x, y = fb[color]['sel'], fa[color]['sel']
                counts['frames'] += 1
                counts['observedTruncatedFrames'] += f.get('observedTruncated', False)
                counts['baselineRecordedMismatch'] += x != f['recorded'][color]
                counts['changed'] += x != y
                counts['lost'] += x is not None and y is None
                counts['gained'] += x is None and y is not None
                counts['staticBefore'] += stationary(x)
                counts['staticAfter'] += stationary(y)
                if static_roi:
                    endpoints = [c[1:5] for c in f[color]]
                    separated = any(stationary(e) for e in endpoints) and any(
                        not stationary(e) and math.dist(midpoint(e), static_roi[:2]) >= 60 for e in endpoints)
                    counts['separated'] += separated
                    counts['alternativeBefore'] += separated and x is not None and not stationary(x)
                    counts['alternativeAfter'] += separated and y is not None and not stationary(y)
                if x != y:
                    changed.append({'f': f['f'], 'before': x, 'after': y})
            colors[color] = {**counts, 'changedFrames': changed}
        results.append({'name': clip['name'], **colors})
    return results



def instrument(source):
    # 原因計測専用。一時コピーにラベルだけ挿入し、選択・履歴は本番コードをそのまま実行。
    pairs = [
        ('return matching\n    }', 'selectionReplayPath = ranked.endpoints.0 == matching.endpoints.0 && ranked.endpoints.1 == matching.endpoints.1 ? "ranked-in-gate" : "continuing"\n        return matching\n    }'),
        ('return challenger\n    }', 'selectionReplayPath = "recovering"\n        return challenger\n    }'),
        ('var winner = Self.continuingWinner', 'selectionReplayPath = "ranked-in-gate"\n        var winner = Self.continuingWinner'),
    ]
    for old, new in pairs:
        assert source.count(old) == 1, old
        source = source.replace(old, new)
    source = source.replace('let e = winner.endpoints\n        let wx', 'let priorReplayPath = selectionReplayPath\n        let e = winner.endpoints\n        let wx')
    source = source.replace('guard let challenger else', 'selectionReplayPath = priorReplayPath\n        guard let challenger else')
    source = source.replace('let rejected = eligible.filter(excluded)',
                            'let rejected = eligible.filter(excluded)\n        selectionReplayExcluded = rejected')
    return source


def run(clips, source_root=None, seconds=None, radius=None, motion=None, policies=None, self_test=False):
    source = source_root / 'ios/PhoneSaberSender/PhoneSaberSender' if source_root else SOURCE
    bgra = instrument((source / 'BGRADetection.swift').read_text())
    for key, value in [('stationarySeconds', seconds), ('stationaryRadius', radius), ('movingDistance', motion)]:
        if value is not None:
            import re
            bgra, count = re.subn(rf'(static let {key} = )[\d.]+', rf'\g<1>{value}', bgra)
            assert count == 1, key
    if policies:
        import re
        for key in ('stationarySeconds', 'stationaryRadius', 'movingDistance'):
            bgra, count = re.subn(rf'(static let {key} = )([\d.]+)',
                                 rf'\g<1>Double(ProcessInfo.processInfo.environment["{key}"] ?? "\2")!', bgra)
            assert count == 1, key
    with tempfile.TemporaryDirectory(prefix='phonesaber-selection-') as directory:
        directory = Path(directory)
        (directory / 'BGRADetection.swift').write_text(bgra)
        binary = directory / 'selection'
        host = (HERE / 'SelectionReplayHost.swift').read_text()
        if 'continuityCandidates:' not in bgra:
            host = host.replace(', continuityCandidates: continuity', '').replace('            testEndOnSelection()\n', '')
            host = host.replace('                    let continuity = observedRows.filter { Int($0[10]) & 1 == 0 }.map { replayCandidate($0, sources: sources) }\n', '')
            host = host[:host.index('// 短縮履歴')]
        if 'observedCandidates:' not in bgra:
            host = host.replace(', observedCandidates: observed', '')
            host = host[:host.index('// 画像生成/eligibility')].replace('            testStationarySelection()', '')
        (directory / 'Host.swift').write_text(host)
        subprocess.run(['xcrun', 'swiftc', '-O', '-module-cache-path', str(directory / 'cache'),
                        str(source / 'DetectionCore.swift'), str(directory / 'BGRADetection.swift'),
                        str(directory / 'Host.swift'), '-o', str(binary)], check=True)
        if self_test:
            subprocess.run([str(binary), '--self-test'], check=True)
            return None
        def execute(policy):
            result = subprocess.run([str(binary)], input=json.dumps(clips), text=True, capture_output=True,
                                    check=True, env={**os.environ, **{k: str(v) for k, v in policy.items()}})
            return json.loads(result.stdout)
        return [{'policy': p, 'clips': execute(p)} for p in policies] if policies else execute({})


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('clips', nargs='*', type=Path)
    parser.add_argument('--source-root', type=Path)
    parser.add_argument('--baseline-source-root', type=Path)
    parser.add_argument('--static-roi', nargs=3, type=float, metavar=('X', 'Y', 'RADIUS'),
                        help='測定専用の静止物ROI。productionには渡さない')
    parser.add_argument('--stationary-seconds', type=float)
    parser.add_argument('--stationary-radius', type=float)
    parser.add_argument('--motion', type=float)
    parser.add_argument('--sweep', action='store_true', help='1.5/2s × 8/12/16px × 16/24pxを比較')
    parser.add_argument('--json', type=Path)
    parser.add_argument('--self-test', action='store_true')
    args = parser.parse_args()
    if args.self_test:
        run([], self_test=True)
        return
    if not args.clips or not args.json:
        parser.error('clips と --json が必要です')
    policies = [{'stationarySeconds': s, 'stationaryRadius': r, 'movingDistance': m}
                for s in (1.5, 2.0) for r in (8, 12, 16) for m in (16, 24)] if args.sweep else None
    clips = [load_clip(p) for p in args.clips]
    result = run(clips, args.source_root,
                 args.stationary_seconds, args.stationary_radius, args.motion, policies)
    if args.baseline_source_root:
        baseline = run(clips, args.baseline_source_root)
        result = [{'policy': r['policy'], 'comparison': comparison(clips, baseline, r['clips'], args.static_roi),
                   'clips': r['clips']} for r in result] if policies else {
                       'comparison': comparison(clips, baseline, result, args.static_roi), 'clips': result}
    args.json.write_text(json.dumps(result))
    print(f'保存: {args.json}')


if __name__ == '__main__':
    main()
