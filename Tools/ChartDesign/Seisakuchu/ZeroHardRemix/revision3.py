"""第3稿: 確認済みの8分拍へ音取りを修正し、同色連打を往復フリックへつなぐ。"""
import collections
import copy
import math

import remix as base
import revision2 as previous


def grid_time(half):
    return round(-90 + half * base.BEAT_MS / 2, 6)


def align_heads(notes, audio):
    # 120ms以内で割れた別の手の入力は、同じ拍を取る応答としてまとめる。
    groups = []
    for i, n in enumerate(notes):
        if (groups and n['time']-notes[groups[-1][0]]['time'] <= 120.001
                and all(base.hand(notes[j]) != base.hand(n) for j in groups[-1])):
            groups[-1].append(i)
        else:
            groups.append([i])
    # 単純な四捨五入では密な連打の2打が同じ拍に潰れるため、順序を保って選ぶ。
    states = {-1: (0., [])}
    for ids in groups:
        old = sum(notes[i]['time'] for i in ids)/len(ids)
        nearest = round((old+90)/base.BEAT_MS*2)
        candidates = [k for k in range(nearest-1, nearest+2)
                      if abs(grid_time(k)-old) <= 220 and 0 <= k <= 557]
        following = {}
        for half in candidates:
            t = grid_time(half)
            evidence = audio.at(t)
            # 音の立ち上がりを補助に、元のフレーズから遠くへ飛ばさない。
            cost = ((t-old)/100)**2 - .8*min(evidence['strength'], 2)
            choices = [(score+cost, path+[half]) for last,(score,path) in states.items() if last < half]
            if choices:
                following[half] = min(choices, key=lambda p:p[0])
        assert following, ('音取りの衝突', old)
        states = following
    _, route = min(states.values(), key=lambda p:p[0])
    changes = []
    for ids, half in zip(groups, route):
        for i in ids:
            n = notes[i]
            old = n['time']
            n.update(time=grid_time(half), beat=half/2)
            changes.append(dict(index=i, beforeMs=old, afterMs=n['time'],
                                deltaMs=round(n['time']-old,6),
                                before=audio.at(old), after=audio.at(n['time'])))
    return changes


def musical_cut_plan(notes):
    plan = dict(purpose='Verification and autoplay only; long cuts are flexible inside the deadline', notes=[])
    for i, n in enumerate(notes):
        start, end, count = n['time'], n['time']+n['lengthMs'], n['count']
        if count == 1:
            times = [start]
        else:
            # 期限は保存した長さを保ち、録画の切断音は期限内の8分拍に載せる。
            first = round(n['beat']*2)
            last = math.floor((end+90)/base.BEAT_MS*2+1e-6)
            candidates = [grid_time(k) for k in range(first,last+1)]
            extras = [m for j,m in enumerate(notes) if j != i and start < m['time'] <= end
                      and base.hand(m) == base.hand(n)]
            candidates = [t for t in candidates if all(abs(t-m['time']) >= max(280,
                          (math.hypot(m['x']-n['x'],m['y']-n['y'])+.5)/5.5*1000+80)
                          for m in extras)]
            assert candidates and candidates[0] == start
            states = {0:(0.,[0])}
            for k in range(1,count):
                following = {}
                for j in range(1,len(candidates)):
                    if (k == count-1) != (j == len(candidates)-1): continue
                    choices = [(cost,path) for prev,(cost,path) in states.items()
                               if j > prev and candidates[j]-candidates[prev] >= 280]
                    if choices:
                        cost,path = min(choices,key=lambda p:p[0])
                        error = (candidates[j]-(start+k*(candidates[-1]-start)/(count-1)))/1000
                        following[j] = (cost+error*error,path+[j])
                states = following
            assert len(candidates)-1 in states, ('拍上のロング経路',i,count)
            times = [candidates[j] for j in states[len(candidates)-1][1]]
        plan['notes'].append(dict(index=i,timesMs=times))
    return plan


def connect_runs(notes, origins, plan, run_indices=None, layouts=None):
    flow = base.audit_module()
    cuts = previous.expanded(notes,plan)
    runs = [] if run_indices is None else run_indices
    if run_indices is None:
        for run in origins['runs']:
            ids = [i for i,p in enumerate(origins['notes']) if p.get('revision2TimeMs',-1) >= run['startMs']
                   and p.get('revision2TimeMs',-1) <= run['endMs'] and p.get('motif') == f'{run["count"]}_note_run'
                   and notes[i]['color'] == run['color']]
            assert len(ids) == run['count'], (run,ids)
            if len(ids) >= 2:
                runs.append(ids)
    membership = {i:r for r,ids in enumerate(runs) for i in ids}
    for n in notes:
        if n['count'] == 1:
            n.update(direction='none',type='tap')
    cuts = previous.expanded(notes,plan)
    for hand in base.PALETTE:
        seq = [n for n in cuts if n['color'] == hand]
        blocks = []
        j = 0
        while j < len(seq):
            n = seq[j]
            if n['parent'] in membership:
                ids = runs[membership[n['parent']]]
                block = seq[j:j+len(ids)]
                assert [m['parent'] for m in block] == ids
                blocks.append((block,True))
                j += len(ids)
            else:
                blocks.append(([n],False))
                j += 1
        # 各連打の入口・出口を含め、全曲を通して成立する向きと座標の組を選ぶ。
        # 同じ手の全打点を追うため、自由ノーツを暗黙の構え直し扱いにしない。
        states = {None:(0.,None,[],[])}
        for block, is_run in blocks:
            options = []
            for direction in flow.DIR_NAMES:
                route = []
                if is_run:
                    unit = flow.UNIT_DIRECTIONS[direction]
                    layout = layouts.get(block[0]['parent']) if layouts else None
                    center = (layout['width'] if layout else 1.25) * (1 if hand == 'red' else -1)
                    center_y = layout['y'] if layout else 0
                    for k,n in enumerate(block):
                        v = tuple(value*((-1)**k) for value in unit)
                        d = min(flow.DIR_NAMES,key=lambda name:math.dist(v,flow.UNIT_DIRECTIONS[name]))
                        route.append((dict(n,x=round(center+.28*v[0],6),y=round(center_y+.28*v[1] if layout else .28*v[1],6),
                                           direction=d,type='direction'),d))
                    # 主に左右往復。中盤は上下を優先し、接続に合う斜めも候補に残す。
                    section = base.section(block[0]['time']+110)
                    preferred = ('up','down') if section == 'first_peak' else ('left','right')
                    if layout: preferred = layout['directions']
                    penalty = 0 if direction in preferred else (3 if layout else .32)
                else:
                    route = [(block[0],direction)]
                    penalty = 0 if direction in ('up','down') else .02
                options.append((route,penalty))
            following = {}
            for route,penalty in options:
                for _,(cost,last,path,edits) in states.items():
                    score = cost+penalty
                    prior = last
                    valid = True
                    for n,d in route:
                        if prior:
                            a,ad = prior
                            if not flow.swing_edge(a,n,ad,d): valid=False; break
                            av,bv = flow.UNIT_DIRECTIONS[ad],flow.UNIT_DIRECTIONS[d]
                            delta = (n['x']-a['x'],n['y']-a['y'])
                            score += math.hypot(*(delta[z]-flow.HALF_SWING*(av[z]+bv[z]) for z in (0,1)))
                        prior = (n,d)
                    if not valid: continue
                    n,d = prior
                    key = (d,n['x'],n['y'])
                    if key not in following or score < following[key][0]:
                        following[key] = (score,prior,path+route,edits+([route] if is_run else []))
            assert following, ('往復フリックの接続',hand,block[0]['time'])
            states = following
        _,_,path,edits = min(states.values(),key=lambda p:p[0])
        for route in edits:
            for n,d in route:
                notes[n['parent']].update(x=n['x'],y=n['y'],direction=d,type='direction')
    # 単独の句末や同時アクセントは、既に成立する経路の向きで少量だけ追加する。
    previous.assign_swing_directions(notes,plan)
    for i,n in enumerate(notes):
        if i in membership or n['count'] > 1 or n['color'] == 'gold': continue
        if origins['notes'][i]['motif'] == 'simple_outer_long_accent': continue
        next_time = min((m['time'] for m in notes if m['time'] > n['time']+.001),default=n['time']+2000)
        paired = sum(abs(m['time']-n['time']) < .001 for m in notes) == 2
        if not ((paired and n['y'] < 0) or next_time-n['time'] > 900): continue
        d = plan['notes'][i]['directions'][0]
        n.update(direction=d,type='direction')
        if flow.audit_swing_flow(previous.expanded(notes,plan))['failures']:
            n.update(direction='none',type='tap')
    previous.assign_swing_directions(notes,plan)
    return [dict(indices=ids,startMs=notes[ids[0]]['time'],endMs=notes[ids[-1]]['time'],
                 color=notes[ids[0]]['color'],count=len(ids),directions=[notes[i]['direction'] for i in ids]) for ids in runs]


def revise(source, second, provenance, audio):
    chart,origins = copy.deepcopy(second),copy.deepcopy(provenance)
    ns = chart['notes']
    for n,p in zip(ns,origins['notes']): p['revision2TimeMs'] = n['time']
    changes = align_heads(ns,audio)
    plan = musical_cut_plan(ns)
    runs = connect_runs(ns,origins,plan)
    origins.update(revision=3,continuousFlickRuns=runs,timingChanges=changes)
    for p,change in zip(origins['notes'],changes):
        p.update(frozen=False,timingEvidence=change['after'])
    assert {k:v for k,v in source.items() if k != 'notes'} == {k:v for k,v in chart.items() if k != 'notes'}
    assert len(ns) == len(second['notes']) == 270 and sum(n['count'] for n in ns) == 424
    for n,old,p in zip(ns,second['notes'],plan['notes']):
        assert n['count'] == old['count'] and n['lengthMs'] == old['lengthMs']
        assert abs(n['x']) <= 2.5 and abs(n['y']) <= 1.5
        assert abs(n['beat']*2-round(n['beat']*2)) < 1e-8
        assert abs(n['beat']*base.BEAT_MS-90-n['time']) < .001
        assert n['time']+110+n['lengthMs'] < 178224
        assert len(p['timesMs']) == n['count'] and p['timesMs'][0] == n['time']
        assert all(n['time'] <= t <= n['time']+n['lengthMs']+.001 for t in p['timesMs'])
        assert all(abs((t+90)/base.BEAT_MS*2-round((t+90)/base.BEAT_MS*2)) < 1e-6 for t in p['timesMs'])
    assert all(a['time'] <= b['time'] for a,b in zip(ns,ns[1:]))
    signature = [n for n in ns if n['count'] == 50]
    assert len(signature) == 2 and signature[0]['time'] == signature[1]['time']
    assert all(n['lengthMs'] == 21677 and n['y'] == -.3 for n in signature)
    extras = [n for n in ns if signature[0]['time'] < n['time'] < signature[0]['time']+21677]
    assert len(extras) == 8 and all(n['type'] == 'tap' and n['count'] == 1 and n['y'] == 1.5 for n in extras)
    cuts = previous.expanded(ns,plan)
    flow = base.audit_module().audit_swing_flow(cuts)
    assert not flow['failures'],flow['failures'][:12]
    flow.pop('allGoldAssignmentsFeasible')
    flow['goldRouting'] = 'position-side assignment; paired gold longs use one hand each'
    pairs = collections.Counter(round(n['time'],3) for n in ns)
    report = dict(revision=3,notes=len(ns),cuts=len(cuts),longNotes=sum(n['count']>1 for n in ns),
                  directionNotes=sum(n['direction']!='none' for n in ns),simultaneousPairs=sum(v==2 for v in pairs.values()),
                  continuousFlickRuns=dict(collections.Counter(str(len(r['indices'])) for r in runs)),
                  signatureLongCount=50,signatureLongLengthMs=21677,signatureLongStartAudioSeconds=(signature[0]['time']+110)/1000,
                  signatureExtraTaps=len(extras),
                  timing=dict(movedMoreThan1Ms=sum(abs(c['deltaMs'])>1 for c in changes),
                              movedMoreThan40Ms=sum(abs(c['deltaMs'])>40 for c in changes),
                              maxMoveMs=max(abs(c['deltaMs']) for c in changes),grid='eighth notes at BPM98, audio origin20ms',
                              meanOnsetBefore=sum(c['before']['strength'] for c in changes)/len(changes),
                              meanOnsetAfter=sum(c['after']['strength'] for c in changes)/len(changes),
                              measure='positive spectral flux within40-45ms; supporting evidence, not isolated-instrument transcription'),flow=flow)
    return chart,origins,plan,report
