"""第5稿: サビの金同時アクセントと、前半の片手フリックを発展させた両手配置。"""
import collections
import copy

import remix as base
import revision2
import revision3
import revision4


# すべて既存の打点。金の両手アクセントを音のある拍へ重ねる。
GOLD_ACCENTS = ((80.5,1.1,.45),(84.5,1.1,.45),(87.5,1.35,-.35),(93,1.25,-.1),
                (104,1.1,.45),(106.5,1.15,.45),(111.5,1.2,.33),(116.5,1.25,.38),(121.5,1.45,.03),
                (225,1.1,.45),(230.5,1.1,-.15),(236,1.3,.15),(241,1.5,-.15),
                (260.5,1.15,.30),(263,1.45,.03),(267.5,1.2,.33))
# 小さなモチーフのリズム・座標・矢印を引用し、反対側の弧状配置を足す。
DEVELOPMENTS = (
    dict(source=[68,68.5,69],hand='red',target=220,arc=[(-1.65,-.5),(-1.40,-.1),(-1.1,.35)]),
    dict(source=[109.5,110],hand='blue',target=225.5,arc=[(1.0,.40),(1.35,.05),(1.7,-.35)]),
    dict(source=[114,114.5,115],hand='red',target=231,arc=[(-1.0,.50),(-1.35,.2),(-1.7,-.15)]),
    dict(source=[120,120.5],hand='blue',target=236.5,arc=[(1.65,-.4),(1.35,-.05),(1.0,.35)]),
)


def revise(fourth, provenance, old_report, audio):
    chart,origins = copy.deepcopy(fourth),copy.deepcopy(provenance)
    ns = chart['notes']
    for i,p in enumerate(origins['notes']): p['revision4Index'] = i
    protected = set()
    developments = []

    def at(beat):
        return [i for i,n in enumerate(ns) if abs(n['beat']-beat) < .00001]

    def add(note, motif, **extra):
        ns.append(note)
        origins['notes'].append(dict(source=[],frozen=False,motif=motif,revision5Added=True,
                                     timingEvidence=audio.at(note['time']),**extra))
        return len(ns)-1

    for spec in DEVELOPMENTS:
        source_ids = [next(i for i,n in enumerate(ns) if n['beat']==beat and n['color']==spec['hand']) for beat in spec['source']]
        protected.update(source_ids)
        targets,companions = [],[]
        for k,point in enumerate(spec['arc']):
            beat = spec['target']+k*.5
            ids = at(beat)
            assert len(ids)==1 and ns[ids[0]]['count']==1
            i = ids[0]
            template = ns[source_ids[k%len(source_ids)]]
            for key in ('color','x','y','direction','type'): ns[i][key] = template[key]
            protected.add(i)
            origins['notes'][i].update(motif='developed_flick',sourceMotifBeat=spec['source'][k%len(source_ids)])
            targets.append(i)
            other = 'blue' if spec['hand']=='red' else 'red'
            companion = dict(ns[i],color=other,x=point[0],y=point[1],direction='none',type='tap')
            companions.append(add(companion,'opposite_hand_arc',parentBeat=beat))
        developments.append(dict(sourceBeats=spec['source'],targetBeats=[spec['target']+k*.5 for k in range(3)],
                                 hand=spec['hand'],sourceIndices=source_ids,targetIndices=targets,arcIndices=companions,
                                 arcMeaning='Three free-cut notes on a continuous spatial path; not a new slide judgment'))

    for beat,width,y in GOLD_ACCENTS:
        ids = at(beat)
        assert 1 <= len(ids) <= 2 and all(ns[i]['count']==1 and i not in protected for i in ids),(beat,ids)
        used = set()
        for i in ids:
            h = base.hand(ns[i]); used.add(h)
            ns[i].update(color='gold',type='tap',direction='none',x=width if h=='red' else -width,y=y)
            origins['notes'][i].update(motif='gold_pair_accent')
        if len(ids)==1:
            h = 'red' if 'red' not in used else 'blue'
            add(dict(ns[ids[0]],x=width if h=='red' else -width),'gold_pair_accent')

    for beat in (114,119):
        ids = at(beat)
        assert len(ids)==1
        n = ns[ids[0]]
        h = 'red' if base.hand(n)=='blue' else 'blue'
        add(dict(n,color=h,x=-n['x'],direction='none',type='tap'),'colored_pair_accent')

    # 並べ替え後も、前半の引用元と後半の発展形の対応を保存する。
    order = sorted(range(len(ns)),key=lambda i:(ns[i]['time'],ns[i]['x']))
    remap = {old:new for new,old in enumerate(order)}
    chart['notes'] = ns = [ns[i] for i in order]
    origins['notes'] = [origins['notes'][i] for i in order]
    protected = {remap[i] for i in protected}
    for spec in developments:
        for key in ('sourceIndices','targetIndices','arcIndices'): spec[key]=[remap[i] for i in spec[key]]
    plan = revision3.musical_cut_plan(ns)
    flow_module = base.audit_module()
    relaxed = []
    for _ in range(24):
        cuts = revision2.expanded(ns,plan)
        failures = flow_module.audit_swing_flow(cuts)['failures']
        if not failures: break
        changed=False
        for f in failures:
            for key in ('note','previous'):
                if f[key]<0: continue
                i=cuts[f[key]]['parent']
                if i not in protected and ns[i]['count']==1 and ns[i]['direction']!='none':
                    ns[i].update(type='tap',direction='none');relaxed.append(i);changed=True
        if not changed: break
    cuts = revision2.expanded(ns,plan)
    flow = flow_module.audit_swing_flow(cuts)
    assert not flow['failures'],[(f, cuts[f['previous']],cuts[f['note']]) for f in flow['failures'][:4]]
    revision2.assign_swing_directions(ns,plan)
    repeats=revision4.repetition_audit(chart)
    assert not repeats['adjacentIdenticalLayouts'] and not any(repeats['reusedDenseHandRhythmWindows'].values()),repeats
    for n,p in zip(ns,origins['notes']):
        if 'revision4Index' in p:
            old=fourth['notes'][p['revision4Index']]
            for key in ('time','beat','count','lengthMs'): assert n[key]==old[key]
            if old['count']>1 or provenance['notes'][p['revision4Index']]['motif']=='simple_outer_long_accent': assert n==old
        assert abs(n['x'])<=2.5 and abs(n['y'])<=1.5
    groups=collections.defaultdict(list)
    for n in ns: groups[n['beat']].append(n)
    assert all(len(g)<=2 and len({base.hand(n) for n in g})==len(g) for g in groups.values())
    pairs=[(beat,g) for beat,g in groups.items() if len(g)==2]
    normal_gold=[beat for beat,g in pairs if all(n['color']=='gold' and n['count']==1 for n in g)]
    for spec in developments:
        for k,i in enumerate(spec['targetIndices']):
            a=ns[spec['sourceIndices'][k%len(spec['sourceIndices'])]];b=ns[i]
            for key in ('color','x','y','direction','type'): assert a[key]==b[key],(spec,key)
    flow.pop('allGoldAssignmentsFeasible')
    flow['goldRouting']='position-side assignment; gold pairs use one hand each'
    origins.update(revision=5,motifDevelopments=developments,revision5ArrowsRelaxed=sorted(set(relaxed)))
    for key in ('barPhrases','continuousFlickRuns'): origins['revision4'+key[0].upper()+key[1:]]=origins.pop(key)
    report=copy.deepcopy(old_report)
    report.update(revision=5,notes=len(ns),cuts=sum(n['count'] for n in ns),directionNotes=sum(n['direction']!='none' for n in ns),
                  simultaneousPairs=len(pairs),normalGoldPairs=len(normal_gold),normalGoldPairBeats=normal_gold,
                  addedNotes=len(ns)-len(fourth['notes']),motifDevelopments=len(developments),oppositeHandArcNotes=12,
                  retainedOriginalNoteTimings=True,colors=dict(collections.Counter(n['color'] for n in ns)),flow=flow,
                  peakPairs=dict(first=sum(80<=beat<124 for beat,g in pairs),final=sum(220<=beat<268 for beat,g in pairs)),
                  repetitionAfter={k:v for k,v in repeats.items() if k!='bars'})
    report.pop('continuousFlickRuns',None)
    return chart,origins,plan,report
