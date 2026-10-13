"""Phobia PARTⅠ: 音源の解析結果を照合し、展開別の3譜面を作る。

python build_chart.py --features <features.npz> --output <folder>
音源は変更しない。96→132 BPMの編集用格子と、音源先頭からの実時刻を保存する。
"""
import argparse
import collections
import importlib.util
import json
import math
from pathlib import Path

import numpy as np

BPM = 96
OFFSET = 16
DURATION = 110.96816326530612
MAP = [{'beat': 0, 'timeMs': 0}, {'beat': 64, 'timeMs': 40000},
       {'beat': 196, 'timeMs': 100000}]
MARKERS = [(0, '導入 / 96 BPM'), (16, '旋律の展開'), (48, '前半の収束'),
           (60, '余韻'), (64, '132 BPM の格子へ'), (68, 'リズム導入'),
           (84, '主部'), (140, '低域中心の間奏'), (152, '再展開'),
           (172, '最終部'), (204, '終止・余韻')]
LEVELS = {'easy': 4, 'normal': 6, 'hard': 8}


def seconds(beat):
    return beat * 60 / 96 if beat <= 64 else 40 + (beat - 64) * 60 / 132


def region(beat):
    if beat < 60: return 'intro'
    if beat < 68: return 'tail'
    if beat < 84: return 'entry'
    if beat < 140: return 'main'
    if beat < 152: return 'break'
    if beat < 172: return 'rise'
    if beat < 204: return 'final'
    return 'outro'


class Evidence:
    def __init__(self, path):
        f = np.load(path)
        self.times, self.onset, self.flux = f['times'], f['onset'], f['flux']

    def at(self, beat):
        target = seconds(beat) + OFFSET / 1000
        ids = np.where(abs(self.times - target) <= .045)[0]
        i = ids[np.argmax(self.onset[ids])]
        return {'peakSeconds': round(float(self.times[i]), 6),
                'distanceMs': round(float(self.times[i] - target) * 1000, 3),
                'strength': round(float(self.onset[i]), 4),
                'bands': [round(float(v), 4) for v in self.flux[:, i]]}


def score_specs(diff, evidence):
    easy, hard = diff == 'easy', diff == 'hard'
    specs = {}

    def add(beat, kind='single', length=0, count=1):
        ev = evidence.at(beat)
        threshold = .24 if beat < 60 else .28
        if kind == 'single' and ev['strength'] < threshold:
            return
        specs[beat] = dict(beat=beat, kind=kind, length=length, count=count, evidence=ev)

    # 前半は旋律の起伏。後半の打楽器密度を持ち込まない。
    patterns = {
        'easy': [[0, 2], [0, 3], [1, 3], [0, 2]],
        'normal': [[0, 1, 2.5], [0, 1.5, 3], [0, 1, 2, 3], [0, 1.5, 2.5, 3.5]],
        'hard': [[0, .5, 1.5, 2, 3], [0, 1, 1.5, 2.5, 3, 3.5],
                 [0, 1, 2, 2.5, 3.5], [0, .5, 1.5, 2.5, 3, 3.5]],
    }[diff]
    for bar in range(1, 15):
        pattern = patterns[bar % len(patterns)]
        if bar < 3 and hard: pattern = [0, 1, 2.5, 3.5]
        for p in pattern: add(bar * 4 + p)
    if hard: add(2)

    # リズム導入4小節。主部の直前までは両手アクセントを控える。
    for bar in range(4):
        pattern = [0, 2] if easy else ([0, 1, 2, 3] if not hard else [0, 1, 1.5, 2, 2.5, 3, 3.5])
        for p in pattern: add(68 + bar * 4 + p)

    # 主部は 3+3+2 のアクセント(0, 1.5, 3拍)を軸にする。
    main_patterns = {
        'easy': [[0, 3], [0, 1.5, 3], [0, 3], [0, 1.5, 3]],
        'normal': [[0, 1.5, 2, 3], [0, .5, 1.5, 3], [0, 1.5, 3, 3.5], [0, 1, 1.5, 2.5, 3]],
        'hard': [[0, .5, 1.5, 2, 3, 3.5], [0, .5, 1, 1.5, 2.5, 3, 3.5],
                 [0, .5, 1.5, 2, 2.5, 3, 3.5], [0, 1, 1.5, 2, 3, 3.5]],
    }[diff]
    for bar in range(14):
        for p in main_patterns[bar % 4]: add(84 + 4 * bar + p)

    for bar in range(3):
        for p in ([0, 2] if easy else ([0, 1, 2, 3] if not hard else [0, 1, 2, 2.5, 3.5])):
            add(140 + 4 * bar + p)
    for bar in range(5):
        pattern = ([0, 2] if bar % 2 == 0 else [1, 3]) if easy else (
            [0, 1, 2, 3] if not hard else [[0, .5, 1, 2, 2.5, 3], [0, 1, 1.5, 2, 3, 3.5]][bar % 2])
        for p in pattern: add(152 + 4 * bar + p)
    for bar in range(8):
        pattern = main_patterns[(bar + 1) % 4]
        if hard and bar in (3, 7): pattern = [0, .5, 1.5, 2, 2.5, 3, 3.5]
        for p in pattern: add(172 + 4 * bar + p)

    # ロングは指定回数を切る形式。最低でも約0.45秒間隔を確保し、内部へ別ノーツを置かない。
    holds = [(8, 2, 3), (24, 2, 3), (56, 2, 3), (60, 4, 3),
             (140, 3 if not hard else 2, 3), (148, 2, 3), (204, 4, 3 if easy else 4)]
    for beat, length, count in holds:
        start, end = seconds(beat), seconds(beat + length)
        padding = .65 if easy else .43
        for old in list(specs):
            if start - padding < seconds(old) < end + padding: del specs[old]
        add(beat, 'long', length, count)

    # 両手は区間の着地だけ。隣接音との間隔が両手の回復に足りる箇所へ限定する。
    pair_beats = {
        'easy': [84, 100, 116, 132, 172, 188, 200],
        'normal': [68, 84, 92, 100, 108, 116, 124, 132, 152, 160, 172, 180, 188, 196, 200],
        'hard': [68, 76, 84, 92, 100, 108, 116, 124, 132, 136, 152, 160, 168, 172, 180, 188, 196, 200],
    }[diff]
    for b in pair_beats:
        if b not in specs: continue
        recovery = .55 if easy else .43
        if hard and specs[b]['evidence']['strength'] >= .9:
            nearby = [other for other in specs if other != b and abs(seconds(b)-seconds(other)) < recovery]
            # 強い両手着地の直前直後は、弱い細分音を休符にして構え直す。
            if all(specs[o]['kind'] == 'single' and specs[o]['evidence']['strength'] < specs[b]['evidence']['strength'] for o in nearby):
                for other in nearby: del specs[other]
        if all(abs(seconds(b) - seconds(other)) >= recovery for other in specs if other != b):
            specs[b]['kind'] = 'pair'

    # 金は休符を伴う応答。どちらの手で取っても次へ戻れる余裕を残す。
    if hard:
        for b in (16, 32, 48, 95, 111, 127, 183, 199):
            if b not in specs or specs[b]['kind'] != 'single': continue
            nearby = [o for o in specs if o != b and abs(seconds(b)-seconds(o)) < .43]
            if all(specs[o]['kind'] == 'single' and specs[o]['evidence']['strength'] < specs[b]['evidence']['strength'] for o in nearby):
                for other in nearby: del specs[other]
    rows = sorted(specs.values(), key=lambda r: r['beat'])
    gold_gap = .65 if easy else .43
    gold_choices = {16, 32, 48, 72, 80, 87, 95, 103, 111, 119, 127, 135, 154, 162, 170, 175, 183, 191, 199}
    for i, r in enumerate(rows):
        if r['kind'] != 'single' or r['beat'] not in gold_choices: continue
        before = seconds(r['beat']) - seconds(rows[i-1]['beat'] + rows[i-1]['length']) if i else 10
        after = seconds(rows[i+1]['beat']) - seconds(r['beat']) if i+1 < len(rows) else 10
        if min(before, after) >= gold_gap: r['kind'] = 'gold'
    return rows


def build(diff, evidence):
    rows = score_specs(diff, evidence)
    notes, provenance = [], []
    hand_index = {'blue': 0, 'red': 0}
    previous_time = {'blue': -10, 'red': -10}
    next_hand = 'blue'
    paths = [(.95, -.40), (1.20, .45), (.75, -.10), (1.05, .60), (1.30, -.50), (.80, .15)]
    if diff == 'easy': paths = [(1.0, -.15), (1.0, .35), (.80, -.10), (1.10, .45)]
    for r in rows:
        b, kind = r['beat'], r['kind']
        if kind in ('gold', 'long'):
            colors = ['gold']
        elif kind == 'pair':
            colors = ['blue', 'red']
        else:
            hand = min(previous_time, key=previous_time.get)
            if abs(previous_time['blue'] - previous_time['red']) < .001: hand = next_hand
            colors = [hand]
            next_hand = 'red' if hand == 'blue' else 'blue'
        for color in colors:
            if color == 'gold': x, y = 0, .10
            else:
                phase = (hand_index[color] + int(b // 16)) % len(paths)
                x, y = paths[phase]
                if kind == 'pair': x, y = 1.10, .35 if int(b // 4) % 2 else -.20
                if color == 'blue': x = -x
                hand_index[color] += 1
            note = dict(beat=b, time=round(seconds(b)*1000, 4), x=x, y=y,
                        type='long' if kind == 'long' else 'tap', color=color,
                        direction='none', count=r['count'],
                        lengthMs=round((seconds(b+r['length'])-seconds(b))*1000, 4))
            notes.append(note)
            provenance.append(dict(beat=b, region=region(b), role=kind, **r['evidence']))
            for hand in (('blue', 'red') if color == 'gold' else (color,)):
                previous_time[hand] = seconds(b+r['length'])

    # フリックは同手の前後に余裕がある動きへ。金の取り方に依存する矢印は後段の全分岐監査で外す。
    if diff != 'easy':
        minimum = .60 if diff == 'normal' else .43
        for hand in ('blue', 'red'):
            ns = [n for n in notes if n['color'] in (hand, 'gold')]
            for i in range(1, len(ns)-1):
                a, n, after = ns[i-1:i+2]
                if n['color'] == 'gold' or n['type'] == 'long' or i % 3: continue
                if (n['time']-a['time']-a['lengthMs'])/1000 < minimum or (after['time']-n['time'])/1000 < minimum: continue
                dx, dy = n['x']-a['x'], n['y']-a['y']
                if abs(dy) >= abs(dx) and abs(dy) > .25 and dy*n['y'] > 0:
                    n['direction'] = 'up' if dy > 0 else 'down'
                    n['type'] = 'direction'
    chart = dict(_comment='Phobia PARTⅠ: 96→132 BPMの解析に基づく初版。変化の格子境界は余韻中の40秒。ノーツは音源先頭からの実時刻。',
                 bpm=BPM, coordScale=1.0, offsetMs=OFFSET, beatZeroMs=0,
                 displayLevel=LEVELS[diff], timeSignatures=[dict(beat=0, numerator=4, denominator=4)],
                 editorTempoMap=MAP,
                 editorMarkers=[dict(timeMs=round(seconds(b)*1000,4),name=name) for b,name in MARKERS], notes=notes)
    return chart, provenance


def flow_module():
    # 既存譜面と同じ、金の両手分岐・振り抜き・進入角を含む監査を使う。
    path = Path(__file__).parents[1] / 'PrismCircuit/chart_v2.py'
    spec = importlib.util.spec_from_file_location('prism_flow', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.BEAT = 60 / BPM  # 遅い区間を基準に、方向転換の制約を保守的に扱う。
    return module


def validate(chart, diff, flow):
    ns = chart['notes']
    assert ns and all(n['time'] <= m['time'] for n, m in zip(ns, ns[1:]))
    assert ns[0]['time'] >= 1000
    for n in ns:
        assert abs(n['time']/1000-seconds(n['beat'])) < 1e-6
        assert abs(n['x']) <= 2.5 and abs(n['y']) <= 1.5
        assert (n['time']+OFFSET+n['lengthMs'])/1000 < DURATION
        if n['type'] == 'long':
            assert n['lengthMs']/(n['count']-1) >= 450
            assert all(m['time'] <= n['time'] or m['time'] >= n['time']+n['lengthMs']+.01 for m in ns)
    minimum, speed = float('inf'), 0
    for hand in ('blue', 'red'):
        seq = [n for n in ns if n['color'] in (hand, 'gold')]
        for a, b in zip(seq, seq[1:]):
            gap = (b['time']-a['time']-a['lengthMs'])/1000
            minimum = min(minimum, gap)
            speed = max(speed, math.hypot(b['x']-a['x'], b['y']-a['y'])/gap)
            assert gap >= (.6 if diff == 'easy' else .40), (diff, 'recovery', a['beat'], b['beat'], gap)
            assert math.hypot(b['x']-a['x'], b['y']-a['y'])/gap <= 4.5
    flow_result = flow.audit_swing_flow(ns)
    assert not flow_result['failures'], flow_result['failures'][:4]
    times = [n['time']/1000 for n in ns]
    groups = collections.Counter(times)
    assert max(groups.values()) <= 2
    for t, count in groups.items():
        if count == 2:
            pair = [n for n in ns if n['time']/1000 == t]
            assert {n['color'] for n in pair} == {'blue', 'red'}
            assert abs(pair[0]['x']-pair[1]['x']) >= 1.5
            assert pair[0]['y'] == pair[1]['y']
    peak = max(sum(t <= u <= t+2 for u in times) for t in times)/2
    return dict(notes=len(ns), cuts=sum(n['count'] for n in ns),
                types=dict(collections.Counter(n['type'] for n in ns)),
                colors=dict(collections.Counter(n['color'] for n in ns)),
                simultaneousPairs=sum(c == 2 for c in groups.values()), peakNotesPerSecond=peak,
                minimumSameHandRecoveryMs=round(minimum*1000,3), maximumCenterTravelUnitsPerSecond=round(speed,3),
                sections=dict(collections.Counter(region(n['beat']) for n in ns)), flow=flow_result)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--features', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=True)
    evidence, flow = Evidence(args.features), flow_module()
    stats, origins = {}, {}
    for diff in LEVELS:
        chart, origins[diff] = build(diff, evidence)
        # 成立しない矢印だけ自由切りへ戻す。打点と配置を隠れて削除しない。
        for _ in range(30):
            failures = flow.audit_swing_flow(chart['notes'])['failures']
            if not failures: break
            changed = False
            for f in failures:
                for i in (f['note'], f['previous']):
                    if i >= 0 and chart['notes'][i]['direction'] != 'none':
                        chart['notes'][i]['direction'] = 'none'
                        chart['notes'][i]['type'] = 'tap'
                        changed = True
            if not changed: break
        stats[diff] = validate(chart, diff, flow)
        (args.output/f'chart_{diff}.json').write_text(json.dumps(chart,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    (args.output/'chart.json').write_bytes((args.output/'chart_normal.json').read_bytes())
    (args.output/'validation.json').write_text(json.dumps(stats,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    (args.output/'note-evidence.json').write_text(json.dumps(origins,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps({d:{k:v for k,v in s.items() if k!='flow'} for d,s in stats.items()},ensure_ascii=False,indent=2))


if __name__ == '__main__': main()
