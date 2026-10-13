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
            cuts=[];maxspeed=0;mingap=999
            for i,(n,p) in enumerate(zip(ns,plan)):
                assert len(p['timesMs'])==len(p['directions'])==n['count']
                assert p['index']==i and abs(p['timesMs'][0]-n['time'])<.001
                assert p['timesMs'][-1]<=n['time']+n['lengthMs']+.001
                if n['direction']!='none':assert p['directions'][0]==n['direction']
                for k,(t,d) in enumerate(zip(p['timesMs'],p['directions'])):
                    cuts.append(dict(n,index=i,time=t/1000,arrow=n['direction']!='none' and k==0,swing=d,hand=n['x']>0))
            for hand in (False,True):
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
            assert not repeats,(spec['id'],diff,repeats)
            developments=read(design/'audit.json')[spec['id']][diff]['developments']
            for item in developments:
                source=(item['sourceBar']-1)*4;target=(item['developedBar']-1)*4;hand=item['leadHand']
                for q in item['offsets']:
                    a=next(n for n in ns if n['beat']==source+q and n['color']==hand)
                    b=next(n for n in ns if n['beat']==target+q and n['color']==hand)
                    assert a['x']==b['x'] and a['y']==b['y']
                    assert len([n for n in ns if n['beat']==target+q])==2
            if diff=='hard':assert len(developments)>=1
            report[spec['id']][diff]=dict(notes=len(ns),cuts=len(cuts),maxSwingSpeed=round(maxspeed,4),
                 minimumSameHandGapMs=round(mingap*1000,3),repeatedOneTwoFourBarBlocks=repeats,developedMotifs=len(developments))
    return report

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--repo',type=Path,required=True);p.add_argument('--output',type=Path,required=True);args=p.parse_args()
    report=verify(args.repo)
    args.output.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
    print('PASS: all 15 charts, timing plans, swing paths, simultaneous separation, 1/2/4-bar repetition and motif development.')
