"""5曲譜面の共通カット経路検証。生成入口は rechart.py。"""

import json
import math

ORIGINS = {'ManicSonic': 0, 'escapade': .020, 'logic': .028, 'BenkyouRemaster': .024, 'Aobasou': 0}

DIRS = ('up', 'down', 'left', 'right', 'upleft', 'upright', 'downleft', 'downright')

VECS = dict(zip(DIRS, [(0,1),(0,-1),(-1,0),(1,0),(-.707,.707),(.707,.707),(-.707,-.707),(.707,-.707)]))

def save(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf8')

def path_for(notes,m):
    # 実際の振り抜きと次の入口を8方向で探索。自由タップも振った向きを保持する。
    plan=[dict(index=i,hand='red' if n['_hand']==0 else 'blue',timesMs=[],directions=[]) for i,n in enumerate(notes)]
    for i,n in enumerate(notes):
        ts=[m.t(b)*1000 for b in n['_cutBeats']]
        plan[i]['timesMs']=[round(float(t),4) for t in ts]
    for hand in (0,1):
        seq=[(i,k,t) for i,n in enumerate(notes) if n['_hand']==hand for k,t in enumerate(plan[i]['timesMs'])]
        seq.sort(key=lambda x:x[2]); states={}
        for j,(i,k,t) in enumerate(seq):
            n=notes[i];following={}
            for d in DIRS:
                if n['_arrow'] and n['_forced'] and d!=n['_forced']:continue
                v=VECS[d]
                if not j: following[d]=(0,[d]);continue
                pi,pk,pt=seq[j-1];prev=notes[pi];gap=(t-pt)/1000
                delta=(n['x']-prev['x'],n['y']-prev['y']);dist=math.hypot(*delta)
                if n['_arrow'] and (dist<.08 or sum(delta[a]*v[a] for a in (0,1))/dist<.706):continue
                choices=[]
                for pd,(cost,path) in states.items():
                    pv=VECS[pd];turn=sum(pv[a]*v[a] for a in (0,1))
                    limit=-.49 if n['_arrow'] and prev['_arrow'] else .01
                    if gap<2*60/m.bpm(n['beat']) and turn>limit:continue
                    travel=.5+math.hypot(*(delta[a]-.25*(v[a]+pv[a]) for a in (0,1)))
                    if gap<=0 or travel/gap>5.5:continue
                    penalty=.05 if d in DIRS[4:] else 0
                    choices.append((cost+travel+penalty,path+[d]))
                if choices:following[d]=min(choices,key=lambda x:x[0])
            if not following:
                return None,(i,j,hand,t)
            states=following
        if not seq:continue
        _,dirs=min(states.values(),key=lambda x:x[0])
        for (i,k,t),d in zip(seq,dirs):plan[i]['directions'].append(d)
    return plan,None

if __name__=='__main__':
    from rechart import main
    main()
