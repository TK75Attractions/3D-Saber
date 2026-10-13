"""作者版の打点と同時ロングを核に、ZeroのHardを再構成する。

python remix.py --features <features.npz> --output <folder>
入力は保存した作者版。ゲームの譜面を直接上書きせず、検証済み候補を別フォルダへ出す。
"""
import argparse
import collections
import copy
import hashlib
import importlib.util
import json
import math
from pathlib import Path

import numpy as np

ROOT = Path(__file__).parent
BPM = 98
BEAT_MS = 60000 / BPM
PROTECTED_STARTS = (34250, 88750, 148125)
PALETTE = ('blue', 'red')


def load(path):
    return json.loads(path.read_text(encoding='utf-8'))


def section(time):
    for end, name in [(18000, 'intro'), (34000, 'staircase'), (40000, 'first_roll'),
                      (60000, 'fan'), (77000, 'first_peak'), (88000, 'hold_relay'),
                      (111000, 'signature_roll'), (121000, 'return'), (134000, 'quiet'),
                      (148000, 'final_stream'), (152000, 'wide_roll'), (164000, 'finale')]:
        if time < end: return name
    return 'coda'


def hand(note):
    return note['color'] if note['color'] in PALETTE else ('blue' if note['x'] < 0 else 'red')


def expanded(notes):
    # 金の同時ロングは左右の位置に対応する手で取る、実行可能な一経路を評価する。
    # どちらの手でどの金を取っても成立する、という全分岐の保証ではない。
    cuts = []
    for i, n in enumerate(notes):
        for k in range(n['count']):
            t = n['time'] + (n['lengthMs'] * k / (n['count'] - 1) if n['count'] > 1 else 0)
            c = dict(n, time=t, beat=(t + 90) / BEAT_MS, color=hand(n), count=1, lengthMs=0, parent=i)
            if k: c.update(direction='none', type='tap')
            cuts.append(c)
    return sorted(cuts, key=lambda n: (n['time'], n['color']))


def audit_module():
    path = ROOT.parents[1] / 'PrismCircuit/chart_v2.py'
    spec = importlib.util.spec_from_file_location('flow', path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    mod.BEAT = 60 / BPM
    return mod


class AudioEvidence:
    def __init__(self, path):
        f = np.load(path)
        self.times, self.onset = f['times'], f['onset']

    def at(self, time):
        # 譜面に保存する時刻ではなく、OFFSET込みの音源時計で照合する。
        actual = (time + 110) / 1000
        ids = np.where(abs(self.times - actual) <= .045)[0]
        k = ids[np.argmax(self.onset[ids])]
        return dict(strength=round(float(self.onset[k]), 4),
                    peakSeconds=round(float(self.times[k]), 6),
                    distanceMs=round(float(self.times[k] - actual) * 1000, 3))


def make(source, audio):
    original = source['notes']
    protected = [i for i, n in enumerate(original) if n['time'] in PROTECTED_STARTS and n['type'] == 'long']
    windows = [(t, max(n['time'] + n['lengthMs'] for n in original if n['time'] == t and n['type'] == 'long'))
               for t in PROTECTED_STARTS]
    holds = []
    removed = []
    for i, n in enumerate(original):
        if n['type'] != 'long': continue
        if i not in protected and any(a < n['time'] < b for a, b in windows):
            removed.append(dict(source=i, reason='paired_long_occupies_both_hands'))
            continue
        holds.append(dict(note=copy.deepcopy(n), source=[i], motif='authored_long', frozen=True))

    # 既存の空き区間に片手ロングを置き、もう片方で旋律を拾う。
    for beat, count, length_beats, color in [(49.5, 3, 2, 'blue'), (126.5, 3, 2, 'red')]:
        t = round(-90 + beat * BEAT_MS, 4)
        n = dict(time=t, beat=beat, x=-1.1 if color == 'blue' else 1.1, y=.1,
                 color=color, direction='none', type='long', count=count, lengthMs=round(length_beats * BEAT_MS, 4))
        holds.append(dict(note=n, source=[], motif='hold_and_response', frozen=False))
    # 元の121秒の単発をロングへ伸ばす。開始時刻を動かさず、静かな区間への橋渡しにする。
    n = copy.deepcopy(original[202])
    n.update(type='long', direction='none', count=4, lengthMs=round(3 * BEAT_MS, 4), x=1.05, y=.1)
    holds.append(dict(note=n, source=[202], motif='quiet_hold_bridge', frozen=False))
    reserved = {h: [(n['note']['time'], n['note']['time'] + n['note']['lengthMs']) for n in holds if hand(n['note']) == h]
                for h in PALETTE}

    def available(h, t):
        return all(t <= a - 430 or t >= b + 430 for a, b in reserved[h])

    # 45ms以内に分かれた左右の同時入力は中心時刻へまとめる。保存した作者版は無変更で残す。
    raw = [(i, n) for i, n in enumerate(original) if n['type'] != 'long' and i != 202]
    groups = []
    for i, n in raw:
        if groups and len(groups[-1]) == 1 and n['time'] - groups[-1][0][1]['time'] <= 45 and hand(n) != hand(groups[-1][0][1]):
            groups[-1].append((i, n))
        else: groups.append([(i, n)])
    rows = []
    for group in groups:
        t = round(sum(n['time'] for _, n in group) / len(group), 4)
        ids = [i for i, _ in group]
        if any(a - 430 < t < b + 430 for a, b in windows):
            removed.extend(dict(source=i, reason='paired_long_occupies_both_hands') for i in ids)
            continue
        rows.append(dict(time=t, source=ids, wants_pair=len(group) == 2,
                         preferred=hand(group[0][1]), motif='authored_rhythm', evidence=audio.at(t)))

    # 既存の打点を優先し、選んだ空き区間だけ実音を拾ってつなぐ。無音や休符を一律に埋めない。
    ranges = [(18.5, 22.8, .52), (29.2, 33.5, .55), (40.0, 41.3, .45),
              (76.8, 79.2, .45), (111.1, 113.55, .55), (122.1, 127.7, .44),
              (134.7, 136.3, .55)]
    for begin, end, threshold in ranges:
        for half in range(math.ceil((begin - .02) * BPM / 60 * 2), math.floor((end - .02) * BPM / 60 * 2) + 1):
            beat = half / 2
            t = round(-90 + beat * BEAT_MS, 4)
            if any(abs(t - r['time']) < (500 if begin == 122.1 else 275) for r in rows): continue
            if not any(available(h, t) for h in PALETTE): continue
            evidence = audio.at(t)
            if evidence['strength'] < threshold: continue
            rows.append(dict(time=t, source=[], wants_pair=False, preferred=None,
                             motif='metered_answer', evidence=evidence))

    entries = [(h['note']['time'], 'hold', h) for h in holds] + [(r['time'], 'row', r) for r in rows]
    entries.sort(key=lambda e: (e[0], e[1]))
    result, origins = [], []
    previous = {h: -10000 for h in PALETTE}
    phases = {h: 0 for h in PALETTE}
    pair_count, last_pair = 0, -10000
    shapes = {
        'intro': [(.85, -.30), (1.25, .35), (1.60, -.20), (1.10, .50)],
        'staircase': [(1.60, -.40), (1.25, .15), (.85, .60), (1.15, .05)],
        'fan': [(.85, -.40), (1.50, .45), (1.60, -.35), (.90, .50)],
        'quiet': [(1.05, -.25), (.80, .35), (1.20, -.15), (.90, .45)],
    }
    default_shape = [(.85, -.45), (1.35, .55), (1.55, -.35), (1.05, .50)]
    for t, kind, r in entries:
        if kind == 'hold':
            n = r['note']
            result.append(n)
            origins.append(dict(source=r['source'], motif=r['motif'], frozen=r['frozen']))
            previous[hand(n)] = n['time'] + n['lengthMs']
            continue
        free = [h for h in PALETTE if available(h, t) and t - previous[h] >= 420]
        if not free:
            removed.extend(dict(source=i, reason='hand_recovery') for i in r['source'])
            continue
        pair = r['wants_pair'] and len(free) == 2 and t - last_pair >= 900
        if pair:
            colors = list(PALETTE)
            pair_count += 1
            last_pair = t
        else:
            # 両手連打が続く場所は交互打ちへ展開。片手に集中した箇所も自然に渡す。
            preferred = r['preferred']
            h = preferred if preferred in free and t - previous[preferred] >= 520 else min(free, key=previous.get)
            colors = [h]
        for h in colors:
            name = section(t + 110)
            phase = phases[h]
            width, y = shapes.get(name, default_shape)[phase % 4]
            if pair: width, y = (1.1 if pair_count % 2 else 1.5), (.45 if pair_count % 2 else -.35)
            n = dict(time=t, beat=round((t + 90) / BEAT_MS, 8),
                     x=-width if h == 'blue' else width, y=y, color=h,
                     direction='none', type='tap', count=1, lengthMs=0)
            result.append(n)
            origins.append(dict(source=r['source'], motif='paired_accent' if pair else name,
                                evidence=r['evidence'], frozen=False))
            previous[h] = t
            phases[h] += 1

    # 矢印は、その手の前の切断から自然に進入できる向き。自由切りを混ぜて構え直せるようにする。
    for h in PALETTE:
        seq = [(i, n) for i, n in enumerate(result) if hand(n) == h]
        for j in range(1, len(seq)):
            ai, a = seq[j - 1]; i, n = seq[j]
            if n['type'] == 'long' or origins[i]['frozen'] or j % 4 == 0: continue
            if n['time'] - a['time'] - a['lengthMs'] < 420: continue
            dx, dy = n['x'] - a['x'], n['y'] - a['y']
            if math.hypot(dx, dy) < .30: continue
            if abs(dx) < abs(dy) * .55: d = 'up' if dy > 0 else 'down'
            elif abs(dy) < abs(dx) * .55: d = 'right' if dx > 0 else 'left'
            else: d = ('up' if dy > 0 else 'down') + ('right' if dx > 0 else 'left')
            n.update(direction=d, type='direction')

    ordered = sorted(zip(result, origins), key=lambda p: (p[0]['time'], p[0]['x']))
    result, origins = map(list, zip(*ordered))
    # 作者版の金の応答も要所に残す。矢印密集の中へ無差別に金を増やさない。
    gold_replies = {8, 67, 85, 186, 273, 309}
    for n, p in zip(result, origins):
        if n['count'] == 1 and len(p['source']) == 1 and p['source'][0] in gold_replies:
            n.update(color='gold', direction='none', type='tap')
            p['motif'] = 'authored_gold_reply'
    flow = audit_module()
    relaxed = []
    for _ in range(30):
        cuts = expanded(result)
        failures = flow.audit_swing_flow(cuts)['failures']
        if not failures: break
        changed = False
        for f in failures:
            for k in (f['note'], f['previous']):
                if k < 0: continue
                i = cuts[k]['parent']; n = result[i]
                if n['direction'] != 'none' and not origins[i]['frozen']:
                    relaxed.append(i); n.update(direction='none', type='tap'); changed = True
        if not changed: break
    chart = copy.deepcopy(source)
    chart['notes'] = result
    return chart, dict(notes=origins, removed=removed, arrowsRelaxed=sorted(set(relaxed)), protectedSourceIndices=protected)


def metrics(chart):
    ns = chart['notes']; cuts = expanded(ns)
    grouped = collections.Counter(round(n['time'], 3) for n in ns)
    recoveries, speeds = [], []
    for h in PALETTE:
        seq = [n for n in cuts if n['color'] == h]
        for a, b in zip(seq, seq[1:]):
            dt = (b['time'] - a['time']) / 1000
            recoveries.append(dt)
            speeds.append(math.hypot(b['x'] - a['x'], b['y'] - a['y']) / max(dt, 1e-8))
    return dict(notes=len(ns), cuts=len(cuts), longNotes=sum(n['type'] == 'long' for n in ns),
                simultaneousPairs=sum(v == 2 for v in grouped.values()),
                directionNotes=sum(n['direction'] != 'none' for n in ns),
                colors=dict(collections.Counter(n['color'] for n in ns)),
                minimumSameHandRecoveryMs=round(min(recoveries)*1000, 3),
                maximumCenterSpeed=round(max(speeds), 3),
                sections=dict(collections.Counter(section(n['time']+110) for n in ns)))


def validate(source, chart):
    assert {k:v for k,v in source.items() if k != 'notes'} == {k:v for k,v in chart.items() if k != 'notes'}
    ns = chart['notes']
    assert all(a['time'] <= b['time'] for a,b in zip(ns,ns[1:]))
    for t in PROTECTED_STARTS:
        original = [n for n in source['notes'] if n['type'] == 'long' and n['time'] == t]
        actual = [n for n in ns if n['type'] == 'long' and n['time'] == t]
        assert sorted(original,key=lambda n:n['x']) == sorted(actual,key=lambda n:n['x'])
        end=max(n['time']+n['lengthMs'] for n in actual)
        assert not [n for n in ns if t < n['time'] < end], ('paired long conflict',t)
    for n in ns:
        assert abs(n['x']) <= 2.5 and abs(n['y']) <= 1.5
        assert abs(n['beat']*BEAT_MS-90-n['time']) < .001
        assert n['time']+110+n['lengthMs'] < 178224
        assert n['count'] >= 1 and (n['type']=='long') == (n['count'] > 1)
        if n['count'] > 1: assert n['lengthMs']/(n['count']-1) >= 400
    cuts = expanded(ns)
    for h in PALETTE:
        seq=[n for n in cuts if n['color']==h]
        for a,b in zip(seq,seq[1:]):
            assert b['time']-a['time'] >= 400-.001, ('hand recovery',h,a['time'],b['time'])
    flow=audit_module().audit_swing_flow(cuts)
    assert not flow['failures'], flow['failures'][:8]
    flow.pop('allGoldAssignmentsFeasible')
    flow['goldRouting']='position-side assignment; paired gold longs use one hand each'
    return dict(before=metrics(source),after=metrics(chart),flow=flow,
                protectedPairedLongs=[dict(startMs=t,lengthsMs=[n['lengthMs'] for n in ns if n['time']==t and n['type']=='long']) for t in PROTECTED_STARTS])


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--features',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--revision',type=int,choices=(1,2,3,4,5),default=5)
    args=parser.parse_args(); args.output.mkdir(parents=True,exist_ok=True)
    source=load(ROOT/'authored-hard.json')
    chart,provenance=make(source,AudioEvidence(args.features))
    if args.revision >= 2:
        from revision2 import revise
        chart,provenance,plan,report=revise(source,chart,provenance)
        if args.revision >= 3:
            from revision3 import revise
            chart,provenance,plan,report=revise(source,chart,provenance,AudioEvidence(args.features))
        if args.revision >= 4:
            from revision4 import revise
            chart,provenance,plan,report=revise(chart,provenance,report)
        if args.revision == 5:
            from revision5 import revise
            chart,provenance,plan,report=revise(chart,provenance,report,AudioEvidence(args.features))
        (args.output/'cut-plan.json').write_text(json.dumps(plan,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    else:
        report=validate(source,chart)
    report['sourceSha256']=hashlib.sha256((ROOT/'authored-hard.json').read_bytes()).hexdigest()
    for filename,value in [('chart_hard.json',chart),('provenance.json',provenance),('validation.json',report)]:
        (args.output/filename).write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(report,ensure_ascii=False,indent=2))


if __name__=='__main__': main()
