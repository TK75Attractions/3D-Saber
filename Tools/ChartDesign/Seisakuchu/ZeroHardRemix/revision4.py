"""第4稿: 小節ごとに手順と軌道を変え、2小節・4小節の配置の使い回しを崩す。"""
import collections
import copy

import remix as base
import revision2
import revision3


# 確認済みの変拍子マップの小節番号。リズムを変えず、連打区間の色順を設計する。
HAND_PHRASES = {
    13:'RR',14:'B',15:'BR',16:'RB',17:'BRR',18:'BB',19:'RRB',20:'BR',
    31:'RRRB',32:'BRRB',33:'BBRR',34:'RBB',37:'RRB',38:'BRRB',
    41:'RRB',42:'BRRBB',43:'RBBRR',44:'BRBRB',45:'RRRBB',46:'RBRRB',47:'BRBBR',48:'RBBRR',49:'B',
    68:'R',69:'RRBB',70:'BRR',71:'BB',72:'RR',73:'RBBB',74:'RRBB',75:'R',
    81:'RRBBRBRRRBB',82:'RBBBRRBRBBR',83:'BRRBRBBRRRB',84:'RBRBBRRBRB',
    92:'RRBBR',93:'BRRRB',94:'BBRBR',
}
LAYOUTS = (
    dict(name='upper_horizontal',width=1.15,y=.30,directions=('left','right')),
    dict(name='outer_vertical',width=1.45,y=-.15,directions=('up','down')),
    dict(name='rising_diagonal',width=1.20,y=.15,directions=('upright','downleft')),
    dict(name='lower_horizontal',width=1.40,y=-.30,directions=('left','right')),
    dict(name='falling_diagonal',width=1.25,y=.20,directions=('upleft','downright')),
)


def bars():
    return base.load(base.ROOT.parent/'metronome-timing.json')['bars']


def measure(note, measures):
    return next(b for b in measures if b['beat'] <= note['beat'] < b['beat']+b['beats'])


def repetition_audit(chart):
    ns = chart['notes']
    rows = []
    for b in bars():
        ids = [i for i,n in enumerate(ns) if b['beat'] <= n['beat'] < b['beat']+b['beats']]
        # 既存の長い連打と、その間の固定レーン単発はユーザー指定で維持する。
        fixed = any(n['count'] > 1 and n['beat'] < b['beat']+b['beats'] and
                    (n['time']+n['lengthMs']+90)/base.BEAT_MS > b['beat'] for n in ns)
        hands = tuple((ns[i]['beat']-b['beat'],ns[i]['color'],ns[i]['count']) for i in ids)
        layout = tuple((ns[i]['beat']-b['beat'],ns[i]['color'],round(ns[i]['x'],1),round(ns[i]['y'],1),
                        ns[i]['direction'],ns[i]['count']) for i in ids)
        rows.append(dict(measure=b['measure'],count=len(ids),fixed=fixed,
                         handSignature=(b['beats'],hands),layoutSignature=(b['beats'],layout)))
    adjacent = [dict(first=a['measure'],second=b['measure']) for a,b in zip(rows,rows[1:])
                if not a['fixed'] and not b['fixed'] and a['count'] >= 2 and a['layoutSignature'] == b['layoutSignature']]
    repeated = {}
    for size in (2,4):
        seen = collections.defaultdict(list)
        duplicates = []
        for start in range(len(rows)-size+1):
            group = rows[start:start+size]
            # 休符や長い連打自体を「反復配置」と数えず、通常配置の連続区間を比較する。
            if any(r['fixed'] or r['count'] == 0 for r in group) or sum(r['count'] for r in group) < size*3: continue
            key = tuple(r['handSignature'] for r in group)
            if seen[key]: duplicates.append(dict(firstMeasures=seen[key][:],repeatedAt=start+1))
            seen[key].append(start+1)
        repeated[str(size)] = duplicates
    return dict(adjacentIdenticalLayouts=adjacent,reusedDenseHandRhythmWindows=repeated,
                comparison='actual meter, relative beats and colors; 2/4-bar windows with >=3 heads/bar on average, no empty bars or active longs',
                bars=[dict(measure=r['measure'],heads=r['count'],activeLong=r['fixed']) for r in rows])


def revise(third, provenance, report):
    chart,origins = copy.deepcopy(third),copy.deepcopy(provenance)
    ns = chart['notes']
    measures = bars()
    selected = collections.defaultdict(list)
    for i,p in enumerate(origins['notes']):
        if p['motif'].endswith('_note_run'):
            selected[measure(ns[i],measures)['measure']].append(i)
    assert set(selected) == set(HAND_PHRASES), sorted(selected)
    layouts,run_indices,phrases = {},[],[]
    for order,(bar,ids) in enumerate(sorted(selected.items())):
        pattern = HAND_PHRASES[bar]
        assert len(pattern) == len(ids),(bar,pattern,ids)
        layout = LAYOUTS[order%len(LAYOUTS)]
        for k,(i,color) in enumerate(zip(ids,pattern)):
            color = 'red' if color == 'R' else 'blue'
            n = ns[i]
            n.update(color=color,x=layout['width']*(1 if color == 'red' else -1),
                     y=layout['y']+(.18 if k%2 == 0 else -.18))
            layouts[i] = layout
            origins['notes'][i].update(motif='bar_phrase',measure=bar,phrasePosition=k)
        # ひとつの小節内の同色連打だけを往復フリックにし、単発の受け渡しも混ぜる。
        runs = []
        for i in ids:
            if runs and ns[runs[-1][-1]]['color'] == ns[i]['color']:
                runs[-1].append(i)
            else: runs.append([i])
        run_indices.extend(group for group in runs if len(group) >= 2)
        phrases.append(dict(measure=bar,handPattern=pattern,layout=layout['name'],indices=ids))
    plan = revision3.musical_cut_plan(ns)
    connected = revision3.connect_runs(ns,origins,plan,run_indices=run_indices,layouts=layouts)
    for p in phrases:
        p['actualDirections'] = [ns[i]['direction'] for i in p['indices']]
    before,after = repetition_audit(third),repetition_audit(chart)
    assert not after['adjacentIdenticalLayouts'],after['adjacentIdenticalLayouts']
    assert not any(after['reusedDenseHandRhythmWindows'].values()),after['reusedDenseHandRhythmWindows']
    assert {k:v for k,v in chart.items() if k!='notes'} == {k:v for k,v in third.items() if k!='notes'}
    for i,(old,n) in enumerate(zip(third['notes'],ns)):
        for key in ('time','beat','count','lengthMs'): assert n[key] == old[key],(i,key)
        if old['count'] > 1 or provenance['notes'][i]['motif'] == 'simple_outer_long_accent': assert n == old
        assert abs(n['x']) <= 2.5 and abs(n['y']) <= 1.5
    cuts = revision2.expanded(ns,plan)
    flow = base.audit_module().audit_swing_flow(cuts)
    assert not flow['failures'],flow['failures'][:12]
    flow.pop('allGoldAssignmentsFeasible')
    flow['goldRouting'] = 'position-side assignment; paired gold longs use one hand each'
    origins.update(revision=4,barPhrases=phrases,revision3FlickRuns=origins.pop('continuousFlickRuns'),continuousFlickRuns=connected)
    stats = copy.deepcopy(report)
    stats.update(revision=4,directionNotes=sum(n['direction']!='none' for n in ns),flow=flow,
                 continuousFlickRuns=dict(collections.Counter(str(r['count']) for r in connected)),
                 variedMeasures=len(phrases),colors=dict(collections.Counter(n['color'] for n in ns)),
                 repetitionBefore={k:v for k,v in before.items() if k!='bars'},
                 repetitionAfter={k:v for k,v in after.items() if k!='bars'},
                 revision4TimingChanges=0)
    return chart,origins,plan,stats
