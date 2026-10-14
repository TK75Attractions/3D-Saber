"""保存済み譜面を独立に検証。生成時の内部フラグは使わない。"""
import argparse
import collections
import hashlib
import json
import math
from pathlib import Path

def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
D={'up':(0,1),'down':(0,-1),'left':(-1,0),'right':(1,0),
   'upleft':(-1,1),'upright':(1,1),'downleft':(-1,-1),'downright':(1,-1)}
D={k:tuple(v/math.hypot(*p) for v in p) for k,p in D.items()}

def verify(repo):
    design=repo/'Tools/ChartDesign/FiveSongs20261014';report={}
    for spec in read(design/'songs.json'):
        folder=repo/'Assets/StreamingAssets/Songs'/spec['id'];report[spec['id']]={}
        audio=list(p for p in folder.glob('audio.*') if p.suffix!='.meta')
        assert len(audio)==1
        assert hashlib.sha256(audio[0].read_bytes()).hexdigest()==read(folder/'stage.json')['audioSha256']
        for diff in ('easy','normal','hard'):
            chart=read(folder/f'chart_{diff}.json');ns=chart['notes'];plan=read(design/spec['id']/f'{diff}-cut-plan.json')['notes']
            assert len(ns)==len(plan)
            evidence=read(design/spec['id']/f'{diff}-evidence.json')
            hands=[n['color'] if n['color'] in ('red','blue') else e['hand'] for n,e in zip(ns,evidence)]
            cuts=[];maxspeed=0;mingap=999
            for i,(n,p) in enumerate(zip(ns,plan)):
                assert len(p['timesMs'])==len(p['directions'])==n['count']
                assert p['index']==i and abs(p['timesMs'][0]-n['time'])<.001
                assert p.get('hand',hands[i])==hands[i] and evidence[i]['hand']==hands[i]
                assert p['timesMs'][-1]<=n['time']+n['lengthMs']+.001
                if n['direction']!='none':assert p['directions'][0]==n['direction']
                for k,(t,d) in enumerate(zip(p['timesMs'],p['directions'])):
                    cuts.append(dict(n,index=i,time=t/1000,arrow=n['direction']!='none' and k==0,swing=d,hand=hands[i]))
            for hand in ('red','blue'):
                seq=sorted((n for n in cuts if n['hand']==hand),key=lambda n:n['time'])
                for a,b in zip(seq,seq[1:]):
                    gap=b['time']-a['time'];assert gap>0
                    va,vb=D[a['swing']],D[b['swing']]
                    delta=(b['x']-a['x'],b['y']-a['y'])
                    speed=(.5+math.hypot(*(delta[j]-.25*(va[j]+vb[j]) for j in (0,1))))/gap
                    assert speed<=5.501,(spec['id'],diff,a['index'],b['index'],speed)
                    if b['arrow']:
                        assert math.hypot(*delta)>=.08 and sum(delta[j]*vb[j] for j in (0,1))/math.hypot(*delta)>=.706
                    bpm=next(t[1] for t in reversed(spec['tempos']) if t[0]<=b['beat'])
                    if gap<120/bpm-1e-6:
                        limit=-.49 if a['arrow'] and b['arrow'] else .01
                        assert sum(va[j]*vb[j] for j in (0,1))<=limit
                    maxspeed=max(maxspeed,speed);mingap=min(mingap,gap)
            groups=collections.defaultdict(list)
            for n in ns:groups[n['time']].append(n)
            for t,group in groups.items():
                assert len(group)<=2
                if any(n['color']=='gold' for n in group):assert len(group)==2 and all(n['color']=='gold' for n in group)
                if len(group)==2:assert math.dist((group[0]['x'],group[0]['y']),(group[1]['x'],group[1]['y']))>1.5
            bars=[]
            for b in range(math.ceil(max(n['beat'] for n in ns)/4)+1):
                bars.append([(n['beat']%4,n['color'],n['type'],n['direction'],n['count']) for n in ns if b*4<=n['beat']<(b+1)*4])
            repeats=[]
            for length in (1,2,4):
                for b in range(len(bars)-2*length+1):
                    if any(bars[b:b+length]) and bars[b:b+length]==bars[b+length:b+2*length]:repeats.append([b+1,length])
            # 音楽の同型反復は許可する。反復を避けるためだけに音を欠落させない。
            def streak(seq):
                run=maximum=0
                for n in seq:
                    run=run+1 if n['direction']!='none' else 0
                    maximum=max(maximum,run)
                return maximum
            ratio=sum(n['direction']!='none' for n in ns)/len(ns)
            limit={'easy':0,'normal':.12,'hard':.22}[diff]
            assert ratio<=limit,(spec['id'],diff,ratio)
            assert streak(ns)<=4,(spec['id'],diff,'全体フリック連続')
            for hand in ('red','blue'):
                hand_notes=[n for n,h in zip(ns,hands) if h==hand]
                assert streak(hand_notes)<=3,(spec['id'],diff,'片手フリック連続')
                for i,n in enumerate(hand_notes[:-1]):
                    if n['type']=='long':assert hand_notes[i+1]['time']>n['time']+n['lengthMs']+.1
                assert .35<=len(hand_notes)/len(ns)<=.65
            for i in range(len(ns)-15):
                assert sum(n['direction']!='none' for n in ns[i:i+16])<=int(16*limit)
            # 同時ノーツの配列順で連続数をごまかさない。混在した拍も厳しく数える。
            grouped_run=grouped_max=0
            for group in groups.values():
                if any(n['direction']!='none' for n in group):grouped_run+=sum(n['direction']!='none' for n in group)
                else:grouped_run=0
                grouped_max=max(grouped_max,grouped_run)
            assert grouped_max<10
            duration=read(design/spec['id']/'analysis.json')['duration']
            for n in ns:
                assert n['time']>=0 and n['time']+n['lengthMs']<duration*1000
                assert (n['type']=='direction')==(n['direction']!='none')
                assert n['count']==(2 if n['type']=='long' else 1)
            evidence=read(design/spec['id']/f'{diff}-evidence.json')
            assert len(evidence)==len(ns)
            assert all(e['strength']>=.20 and abs(e['distanceMs'])<=45.01 for e in evidence)
            report[spec['id']][diff]=dict(notes=len(ns),cuts=len(cuts),maxSwingSpeed=round(maxspeed,4),
                 minimumSameHandGapMs=round(mingap*1000,3),repeatedOneTwoFourBarBlocks=len(repeats),
                 flickPercent=round(ratio*100,2),maxConsecutiveFlicks=streak(ns),
                 maxConsecutiveFlicksPerHand=max(streak([n for n,hand in zip(ns,hands) if hand==h]) for h in ('red','blue')),
                 maxConsecutiveFlicksByGroup=grouped_max,notesPerSecond=round(len(ns)/duration,3),
                 blueOnRight=sum(n['color']=='blue' and n['x']>.1 for n in ns),
                 redOnLeft=sum(n['color']=='red' and n['x']<-.1 for n in ns),
                 verticalPairs=sum(len(g)==2 and abs(g[0]['x']-g[1]['x'])<.1 and abs(g[0]['y']-g[1]['y'])>1.5 for g in groups.values()))
        counts=[report[spec['id']][d]['notes'] for d in ('easy','normal','hard')]
        assert counts[0]<counts[1]*.8 and counts[1]<counts[2]*.8
        assert (folder/'chart.json').read_bytes()==(folder/'chart_normal.json').read_bytes()
    return report

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--repo',type=Path,required=True);p.add_argument('--output',type=Path,required=True);args=p.parse_args()
    report=verify(args.repo)
    args.output.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
    print('PASS: all 15 charts, timing plans, swing paths, long occupancy, simultaneous separation, audio evidence and flick limits (global / per-hand / simultaneous groups).')
