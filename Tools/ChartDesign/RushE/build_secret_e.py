"""難易度E: 弱い連続音も拾い、和音の減衰をランダムな連打にしない。"""
from pathlib import Path
import argparse
import collections
import json
import math

import numpy as np


def save(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + '\n', encoding='utf8')


# 原版の連続旋律区間。周期は音声から再推定し、音のないグリッド点は追加しない。
RUNS = [(41.61, 64.65), (66.38, 78.17), (89.2, 104.16),
        (105.05, 111.70), (113.12, 121.85), (121.85, 125.70)]
# 大量の重なった音の余韻。ここでは細かなスペクトル変動を発音と扱わない。
CLOUDS = [(12.60, 13.50), (15.85, 16.70), (32.60, 33.45),
          (78.30, 80.64), (125.71, 135.39)]
DENSE_RHYTHMS = [(125.71, 129.13, .154, .168),
                 (129.13, 133.52, .146, .159), (133.52, 135.39, .142, .155)]
FINAL = 136.28027


def select_attacks(features):
    times, onset, flux = (features[k] for k in ('times', 'onset', 'flux'))
    # 低音のうなりだけで弱い旋律を判定しない。中高域の発音も独立に調べる。
    upper = .45 * flux[2] + .55 * flux[3]
    local = np.median(np.lib.stride_tricks.sliding_window_view(
        np.pad(onset, (15, 15), mode='edge'), 31), axis=1)
    upper_local = np.median(np.lib.stride_tricks.sliding_window_view(
        np.pad(upper, (15, 15), mode='edge'), 31), axis=1)
    novelty = np.maximum(onset, upper - .55 * upper_local)
    maxima = np.zeros(len(times)-2, dtype=bool)
    for band in (novelty, onset, upper):
        maxima |= (band[1:-1] > band[:-2]) & (band[1:-1] >= band[2:])
    indices = np.where(maxima & (novelty[1:-1] >= .12))[0] + 1
    candidates = []
    for i in indices:
        time = float(times[i])
        if time > FINAL + .012:
            continue
        prominence = float(novelty[i] - max(np.min(novelty[max(0, i-5):i+1]),
                                            np.min(novelty[i:i+6])))
        candidates.append(dict(frame=int(i), time=time, strength=float(onset[i]),
                               upper=float(upper[i]), prominence=prominence,
                               score=float(novelty[i]), background=float(local[i])))

    # 同じピアノ打音の帯域別ピークを統合。従来の75ms間引きは行わない。
    distinct = []
    for e in sorted(candidates, key=lambda e: -e['score']):
        if all(abs(e['time'] - p['time']) >= .035 for p in distinct):
            distinct.append(e)
    distinct.sort(key=lambda e: e['time'])
    chosen = {}
    fits = []
    for e in distinct:
        i, time = e['frame'], e['time']
        cloud = any(a <= time < b for a, b in CLOUDS)
        if cloud:
            # 「バーン」の明確な立ち上がりだけ。余韻へ架空の刻みを置かない。
            accept = e['strength'] >= .72 and e['strength'] >= 2.5 * e['background'] and e['prominence'] >= .23
            role = 'chord-attack'
        else:
            accept = ((e['strength'] >= .48 and e['strength'] >= 1.8 * e['background']) or
                      (e['upper'] >= .55 and e['upper'] >= 2.0 * upper_local[i])) and e['prominence'] >= .12
            role = 'attack'
        if accept:
            chosen[i] = dict(e, role=role)

    # 連打の弱拍を強拍と同じ音量しきい値で捨てない。
    # 周期は採否の裏付けだけに使い、ノーツ時刻は必ず実音のピークにする。
    for start, end in RUNS:
        best = (-1., 0., 0.)
        for period in np.arange(.083, .101, .00002):
            phases = np.arange(0, period, .001)
            grid = start + phases[:, None] + np.arange(int((end-start)/period))[None, :] * period
            scores = np.interp(grid, times, np.minimum(onset, 1.8)).mean(axis=1)
            k = int(np.argmax(scores))
            if scores[k] > best[0]:
                best = float(scores[k]), float(period), float(start + phases[k])
        score, period, phase = best
        fits.append(dict(start=start, end=end, periodMs=round(period*1000, 4),
                         phaseMs=round(phase*1000, 4), score=round(score, 4)))
        for pulse in np.arange(phase-period, end, period):
            options = [e for e in candidates if start <= e['time'] <= end and
                       abs(e['time']-pulse) <= .020 and e['strength'] >= .12 and e['prominence'] >= .035 and
                       (flux[1, e['frame']] >= .35 or flux[2, e['frame']] >= .35 or flux[3, e['frame']] >= .20)]
            if options:
                e = max(options, key=lambda e: e['score'])
                if e['frame'] not in chosen:
                    chosen[e['frame']] = dict(e, role='quiet-run-attack')

    # 終盤の大量和音は、鳴っている主要拍を拾う。音塊内の不規則なピークは除く。
    for start, end, low, high in DENSE_RHYTHMS:
        chosen = {i: e for i, e in chosen.items() if not start <= e['time'] < end}
        best = (-1., 0., 0.)
        for period in np.arange(low, high, .00002):
            phases = np.arange(0, period, .001)
            grid = start + phases[:, None] + np.arange(int((end-start)/period))[None, :] * period
            scores = np.interp(grid, times, np.minimum(onset, 1.8)).mean(axis=1)
            k = int(np.argmax(scores))
            if scores[k] > best[0]:
                best = float(scores[k]), float(period), float(start + phases[k])
        score, period, phase = best
        fits.append(dict(start=start, end=end, periodMs=round(period*1000, 4),
                         phaseMs=round(phase*1000, 4), score=round(score, 4), role='dense-chord-pulse'))
        for pulse in np.arange(phase-period, end, period):
            options = [e for e in distinct if start <= e['time'] < end and
                       abs(e['time']-pulse) <= .024 and e['strength'] >= .22 and e['prominence'] >= .045]
            if options:
                e = max(options, key=lambda e: e['score'])
                chosen[e['frame']] = dict(e, role='dense-chord-pulse')

    attacks = sorted(chosen.values(), key=lambda e: e['time'])
    # 和音の帯域ごとの立ち上がりは同一打音へまとめる。時刻をずらして連打にしない。
    merged = []
    for e in attacks:
        if merged and e['time'] - merged[-1]['time'] < .035:
            if e['score'] > merged[-1]['score']:
                merged[-1] = e
        else:
            merged.append(e)
    assert merged and abs(merged[-1]['time'] - FINAL) < .02
    return merged, fits


def build(source, output):
    analysis = json.loads((source / 'Analysis/analysis.json').read_text(encoding='utf8'))
    base = json.loads((source / 'Staging/Assets/StreamingAssets/Songs/RushE/chart_hard.json').read_text(encoding='utf8'))
    features = np.load(source / 'Analysis/features.npz')
    attacks, fits = select_attacks(features)
    notes = []
    hand, last_pair = 0, -10.
    last = [-10., -10.]
    for i, e in enumerate(attacks):
        t = e['time']
        before = t - attacks[i-1]['time'] if i else 10
        after = attacks[i+1]['time'] - t if i+1 < len(attacks) else 10
        # 両手同時は実際の強い打音だけ。連打が続くときは左右交互を優先する。
        pair = (e['strength'] >= 1.03 and t > 13 and t-last_pair >= .75 and
                min(before, after) >= .135 and min(t-v for v in last) >= .135)
        if i == len(attacks)-1:
            pair = True
        for h in ((0, 1) if pair else (hand,)):
            x = (1.04 + .07 * math.sin(t*.32)) * (1 if h == 0 else -1)
            y = .11 * math.sin(t*.52 + h*2.5)
            notes.append(dict(time=round(t*1000, 4), x=round(x, 4), y=round(y, 4),
                              type='tap', color='red' if h == 0 else 'blue', direction='none',
                              count=1, lengthMs=0, _hand=h, _event=i, _role=e['role']))
            last[h] = t
        if pair:
            last_pair = t
        hand = 1-hand

    grid = base['editorTempoMap']
    def beat_at(ms):
        k = next((i for i in range(len(grid)-2, -1, -1) if grid[i]['timeMs'] <= ms), 0)
        a, b = grid[k], grid[k+1]
        return a['beat']+(ms-a['timeMs'])*(b['beat']-a['beat'])/(b['timeMs']-a['timeMs'])

    plan = [dict(index=i, timesMs=[n['time']], directions=[]) for i, n in enumerate(notes)]
    maxspeed, mingap = 0., 100.
    for h in (0, 1):
        previous = None
        for j, (i, n) in enumerate((i, n) for i, n in enumerate(notes) if n['_hand'] == h):
            # 密集時に腕を毎回戻さず、同じ場所で上下を交互に切る。
            plan[i]['directions'].append('down' if j % 2 else 'up')
            if previous:
                gap = (n['time']-previous['time']) / 1000
                speed = (.5 + math.hypot(n['x']-previous['x'], n['y']-previous['y'])) / gap
                maxspeed, mingap = max(maxspeed, speed), min(mingap, gap)
            previous = n
    assert mingap >= .070, f'左右の割り当てを要確認: {mingap}'
    assert maxspeed < 7.5, f'動作速度を要確認: {maxspeed}'
    clean = []
    evidence = []
    for i, n in enumerate(notes):
        row = {k: v for k, v in n.items() if not k.startswith('_')}
        row['beat'] = round(beat_at(n['time']), 6)
        clean.append(row)
        e = attacks[n['_event']]
        evidence.append(dict(index=i, attackIndex=n['_event'], timeMs=n['time'],
                             strength=round(e['strength'], 4), upperStrength=round(e['upper'], 4),
                             role=n['_role'], cutTimesMs=[n['time']]))
    base.update(_comment='RUSH E限定E。連続旋律の弱い発音も1打ずつ通常ノーツへ。密集和音は明確な立ち上がりにまとめ、余韻の架空連打・連打を隠すロング・休憩用の間引きなし。',
                displayLevel=10, notes=clean)
    dest = output / 'Tools/ChartDesign/RushE'
    save(output / 'Assets/StreamingAssets/Songs/RushE/chart_e.json', base)
    save(dest / 'e-cut-plan.json', dict(notes=plan))
    save(dest / 'e-evidence.json', evidence)
    source_attacks = [dict(timeMs=round(e['time']*1000, 4), strength=round(e['strength'], 4),
                          upperStrength=round(e['upper'], 4), role=e['role']) for e in attacks]
    save(dest / 'e-attacks.json', dict(audioSha256=analysis['sha256'],
                                      method='audio peaks + quieter periodic attacks; no synthetic cuts',
                                      rhythmFits=fits, chordCloudsSeconds=CLOUDS, attacks=source_attacks))
    groups = collections.Counter(n['time'] for n in notes)
    audit = dict(revision='audio-faithful-2026-10-14', audioSha256=analysis['sha256'],
                 notes=len(notes), cuts=len(notes), types={'tap': len(notes)},
                 distinctAttacks=len(attacks), coveredAttacks=len(groups),
                 quietRunAttacks=sum(e['role'] == 'quiet-run-attack' for e in attacks),
                 simultaneousGroups=sum(v == 2 for v in groups.values()),
                 maximumSwingSpeed=round(maxspeed, 4), minimumSameHandGapMs=round(mingap*1000, 3),
                 minimumDistinctAttackGapMs=round(min(b['time']-a['time'] for a, b in zip(attacks, attacks[1:]))*1000, 3),
                 firstNoteMs=notes[0]['time'], lastNoteEndMs=notes[-1]['time'],
                 syntheticCuts=0, droppedForHandRecovery=0, flicks=0)
    save(dest / 'e-audit.json', audit)
    print(json.dumps(audit, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('source', type=Path, help='Analysisと標準譜面のStagingがある解析作業フォルダー')
    parser.add_argument('--output', type=Path, help='別のStagingフォルダー（本編には自動適用しない）')
    args = parser.parse_args()
    build(args.source.resolve(), (args.output or args.source / 'Staging').resolve())
