"""第2稿: 50回の同時ロング、2連・3連、句末のフリック、ロング中の単発。

ロングの切断計画は検証・録画用。ゲーム上は制限時間内の切断回数だけを要求する。
"""
import collections
import copy
import math

import remix as base

START = 88750
LENGTH = 21677
EXTRA_SOURCES = (166, 169, 172, 173, 174, 175, 178, 181)
RUN_SECTIONS = ((18000, 29000, (2, 2)), (41000, 46000, (3, 3, 2, 2)),
                (54000, 60000, (2, 2)), (64200, 76000, (2, 2, 3, 3)),
                (111000, 119000, (3, 3, 2, 2)), (134000, 148000, (2, 2, 3, 3)),
                (159000, 164000, (2, 2, 3, 3)))


def long_schedule(note, notes):
    start, end = note['time'], note['time'] + note['lengthMs']
    count = note['count']
    if start != START or count != 50:
        return [start + (note['lengthMs'] * k / (count-1) if count > 1 else 0) for k in range(count)]
    extras = [n for n in notes if start < n['time'] < end and n['count'] == 1 and base.hand(n) == base.hand(note)]
    # 8分刻みを基本に、外側の単発へ出入りする時間を左右別々に確保する。
    lo = math.ceil((start + 90 + 280) / base.BEAT_MS * 2)
    hi = math.floor((end + 90 - 280) / base.BEAT_MS * 2)
    candidates = [start] + [-90 + k * base.BEAT_MS / 2 for k in range(lo, hi+1)] + [end]
    candidates = [t for t in candidates if all(abs(t-n['time']) >= max(440,
                  (math.hypot(n['x']-note['x'], n['y']-note['y'])+.5)/5.5*1000+80) for n in extras)]
    # 切断回数50と共通の終了時刻を守りつつ、均等ペースからのずれを小さくする。
    states = {0: (0, [0])}
    for k in range(1, count):
        following = {}
        for j in range(1, len(candidates)):
            if (k == count-1) != (j == len(candidates)-1):
                continue
            choices = [(cost, route) for i, (cost, route) in states.items()
                       if j > i and candidates[j]-candidates[i] >= 280]
            if choices:
                cost, route = min(choices, key=lambda x: x[0])
                error = (candidates[j] - (start + k * note['lengthMs']/(count-1))) / 1000
                following[j] = (cost + error*error, route + [j])
        states = following
    assert len(candidates)-1 in states, '外側単発へ出入りする50回の連打経路が必要'
    return [candidates[j] for j in states[len(candidates)-1][1]]


def cut_plan(notes):
    return dict(purpose='Verification and autoplay only; long cuts are flexible inside the deadline',
                notes=[dict(index=i, timesMs=[round(t, 6) for t in long_schedule(n, notes)]) for i, n in enumerate(notes)])


def expanded(notes, plan):
    cuts = []
    for i, n in enumerate(notes):
        for k, t in enumerate(plan['notes'][i]['timesMs']):
            c = dict(n, time=t, beat=(t+90)/base.BEAT_MS, color=base.hand(n), count=1,
                     lengthMs=0, parent=i, cutIndex=k)
            if k: c.update(direction='none', type='tap')
            cuts.append(c)
    return sorted(cuts, key=lambda n: (n['time'], n['color']))


def assign_swing_directions(notes, plan):
    # 監査で成立する方向列を録画にも渡し、出入りを実際の刃の動きで見せる。
    flow = base.audit_module()
    for p in plan['notes']: p['directions'] = ['none'] * len(p['timesMs'])
    cuts = expanded(notes, plan)
    for hand in base.PALETTE:
        seq = [c for c in cuts if c['color'] == hand]
        states = {}
        for j, n in enumerate(seq):
            allowed = [n['direction']] if n['direction'] != 'none' else flow.DIR_NAMES
            following = {}
            for d in allowed:
                penalty = 0 if d in ('up', 'down') else .02
                if not j:
                    following[d] = (penalty, [d])
                    continue
                choices = []
                for prev, (cost, path) in states.items():
                    if not flow.swing_edge(seq[j-1], n, prev, d): continue
                    av, bv = flow.UNIT_DIRECTIONS[prev], flow.UNIT_DIRECTIONS[d]
                    delta = (n['x']-seq[j-1]['x'], n['y']-seq[j-1]['y'])
                    travel = math.hypot(*(delta[i]-flow.HALF_SWING*(av[i]+bv[i]) for i in (0,1)))
                    choices.append((cost+travel+penalty, path+[d]))
                if choices: following[d] = min(choices, key=lambda p:p[0])
            assert following, ('recorded swing path', hand, n['time'])
            states = following
        _, directions = min(states.values(), key=lambda p:p[0])
        for n, d in zip(seq, directions):
            plan['notes'][n['parent']]['directions'][n['cutIndex']] = d


def revise(source, first, provenance):
    chart, origins = copy.deepcopy(first), copy.deepcopy(provenance)
    notes = chart['notes']
    runs = []
    for i, n in enumerate(notes):
        if n['time'] == START and n['count'] > 1:
            n.update(count=50, lengthMs=LENGTH, y=-.3)
            origins['notes'][i].update(frozen=False, motif='signature_long_50')
        if n['count'] == 1:
            n.update(direction='none', type='tap')

    grouped_indices = set()
    for lo, hi, pattern in RUN_SECTIONS:
        ids = [i for i, n in enumerate(notes) if lo <= n['time'] < hi and n['count'] == 1
               and sum(abs(m['time']-n['time']) < .001 for m in notes) == 1]
        offset, group = 0, 0
        while offset < len(ids):
            size = min(pattern[group % len(pattern)], len(ids)-offset)
            selected = ids[offset:offset+size]
            color = 'red' if group % 2 == 0 else 'blue'
            width = (1.05, 1.30, 1.15)[(group//2) % 3]
            for k, i in enumerate(selected):
                n = notes[i]
                n.update(color=color, x=width if color == 'red' else -width, y=.25 if k % 2 == 0 else -.25)
                # 2連は下へ、3連は上へ抜ける終点だけをフリックにする。
                if k == size-1 and size >= 2:
                    n.update(type='direction', direction='up' if k % 2 == 0 else 'down')
                origins['notes'][i].update(motif=f'{size}_note_run', runPosition=k)
                grouped_indices.add(i)
            runs.append(dict(startMs=notes[selected[0]]['time'], color=color, count=size,
                             indices=selected, endMs=notes[selected[-1]]['time']))
            offset += size
            group += 1

    # 同時アクセントは上下対称、その他は間が空く句末だけ。移動方向に合う矢印を選ぶ。
    for h in base.PALETTE:
        seq = [(i, n) for i, n in enumerate(notes) if base.hand(n) == h]
        for j in range(1, len(seq)):
            i, n = seq[j]
            _, prev = seq[j-1]
            if i in grouped_indices or n['count'] > 1 or n['color'] == 'gold': continue
            # 片手だけの間隔ではなく、譜面全体の休符を見る。左右交互の最中を句末扱いしない。
            next_time = min((m['time'] for m in notes if m['time'] > n['time']+.001), default=n['time']+2000)
            paired = sum(abs(m['time']-n['time']) < .001 for m in notes) == 2
            if not ((paired and n['y'] < 0) or next_time-n['time'] > 900): continue
            if n['time']-prev['time']-prev['lengthMs'] < 400: continue
            dx, dy = n['x']-prev['x'], n['y']-prev['y']
            if math.hypot(dx, dy) < .35: continue
            if abs(dy) >= abs(dx)*1.5: direction='up' if dy > 0 else 'down'
            elif abs(dx) >= abs(dy)*1.5: direction='right' if dx > 0 else 'left'
            else: direction=('up' if dy > 0 else 'down')+('right' if dx > 0 else 'left')
            n.update(type='direction', direction=direction)

    # 作者版の音取りを再利用し、内側のロングとは別の固定レーンに単発だけ置く。
    for old_index in EXTRA_SOURCES:
        old = source['notes'][old_index]
        n = dict(old, count=1, lengthMs=0, direction='none', type='tap',
                 x=2.5 if old['color'] == 'red' else -2.5, y=1.5)
        notes.append(n)
        origins['notes'].append(dict(source=[old_index], motif='simple_outer_long_accent', frozen=False))
    ordered = sorted(zip(notes, origins['notes']), key=lambda p: (p[0]['time'], p[0]['x']))
    chart['notes'], origins['notes'] = map(list, zip(*ordered))
    notes = chart['notes']
    plan = cut_plan(notes)
    flow_module = base.audit_module()
    relaxed = []
    for _ in range(20):
        cuts = expanded(notes, plan)
        failures = flow_module.audit_swing_flow(cuts)['failures']
        if not failures: break
        changed = False
        for f in failures:
            for key in ('note', 'previous'):
                if f[key] < 0: continue
                i = cuts[f[key]]['parent']
                if notes[i]['direction'] != 'none' and notes[i]['count'] == 1:
                    notes[i].update(direction='none', type='tap')
                    relaxed.append(i)
                    changed = True
        if not changed: break
    origins['revision'] = 2
    origins['revision2ArrowsRelaxed'] = sorted(set(relaxed))
    origins['runs'] = [{k:v for k,v in run.items() if k != 'indices'} for run in runs]
    # 第1稿の removed は履歴。今回戻した元ノーツを削除扱いのままにしない。
    origins['removed'] = [r for r in origins['removed'] if r['source'] not in EXTRA_SOURCES]
    report = validate(source, chart, plan, runs)
    assign_swing_directions(notes, plan)
    return chart, origins, plan, report


def validate(source, chart, plan, runs):
    ns = chart['notes']
    assert {k:v for k,v in source.items() if k != 'notes'} == {k:v for k,v in chart.items() if k != 'notes'}
    for t in base.PROTECTED_STARTS:
        original = sorted([n for n in source['notes'] if n['time'] == t and n['count'] > 1], key=lambda n:n['x'])
        actual = sorted([n for n in ns if n['time'] == t and n['count'] > 1], key=lambda n:n['x'])
        if t == START: original = [dict(n, count=50, lengthMs=LENGTH, y=-.3) for n in original]
        assert original == actual
    extras = [n for n in ns if START < n['time'] < START+LENGTH]
    assert len(extras) == len(EXTRA_SOURCES)
    assert all(n['type'] == 'tap' and n['direction'] == 'none' and n['count'] == 1 for n in extras)
    assert len({(n['x'], n['y']) for n in extras}) == 2
    for n in ns:
        assert abs(n['x']) <= 2.5 and abs(n['y']) <= 1.5
        assert abs(n['beat']*base.BEAT_MS-90-n['time']) < .001
        assert n['time']+110+n['lengthMs'] < 178224
    for n, p in zip(ns, plan['notes']):
        assert len(p['timesMs']) == n['count']
        assert abs(p['timesMs'][0]-n['time']) < .001
        if n['count'] > 1: assert abs(p['timesMs'][-1]-n['time']-n['lengthMs']) < .001
    cuts = expanded(ns, plan)
    flow = base.audit_module().audit_swing_flow(cuts)
    assert not flow['failures'], flow['failures'][:12]
    flow.pop('allGoldAssignmentsFeasible')
    flow['goldRouting'] = 'position-side assignment; paired gold longs use one hand each'
    recoveries=[]
    for h in base.PALETTE:
        seq=[n for n in cuts if n['color']==h]
        recoveries += [b['time']-a['time'] for a,b in zip(seq,seq[1:])]
    pairs=collections.Counter(round(n['time'],3) for n in ns)
    return dict(revision=2, notes=len(ns), cuts=len(cuts), longNotes=sum(n['count']>1 for n in ns),
                directionNotes=sum(n['direction']!='none' for n in ns), simultaneousPairs=sum(v==2 for v in pairs.values()),
                sameColorRuns=dict(collections.Counter(str(r['count']) for r in runs)),
                signatureLongCount=50, signatureLongLengthMs=LENGTH, signatureExtraTaps=len(extras),
                minimumSameHandRecoveryMs=round(min(recoveries),3), flow=flow)
