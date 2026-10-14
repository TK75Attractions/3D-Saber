"""RUSH Eの専用最難関。通常・同時・ロングで実音のアタックを拾う。"""
from pathlib import Path
import json,math,collections,sys
ROOT=Path(__file__).resolve().parent
SOURCE=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else ROOT
OUT=SOURCE/'Staging'
def save(path,value):
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text(json.dumps(value,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
analysis=json.loads((SOURCE/'Analysis/analysis.json').read_text(encoding='utf8'))
base=json.loads((SOURCE/'Staging/Assets/StreamingAssets/Songs/RushE/chart_hard.json').read_text(encoding='utf8'))
events=[e for e in analysis['events'] if e['time']<=136.31 and e['strength']>=.35]
# 連続する同じ発音を二重に取らず、前奏の間と終止後の減衰は休符のまま残す。
peaks=[]
for e in sorted(events,key=lambda e:-e['strength']):
    if all(abs(e['time']-p['time'])>=.075 for p in peaks):peaks.append(e)
peaks.sort(key=lambda e:e['time'])
notes=[];last=[-10.,-10.];preferred=0;last_pair=-10
for i,e in enumerate(peaks):
    t=e['time']; available=[h for h in (preferred,1-preferred) if t-last[h]>=.140]
    if not available:continue
    h=available[0]
    # 和音のアクセントは両手へ。直後の音を拾う片手を残せる間隔に限る。
    following=peaks[i+1]['time']-t if i+1<len(peaks) else 2
    pair=len(available)==2 and t>13 and e['strength']>=1.03 and t-last_pair>=1.45 and following>=.140
    for hand in ((0,1) if pair else (h,)):
        x=(1.04+.07*math.sin(t*.32))*(1 if hand==0 else -1)
        y=.11*math.sin(t*.52+hand*2.5)
        notes.append(dict(time=round(t*1000,4),x=round(x,4),y=round(y,4),type='tap',color='red' if hand==0 else 'blue',direction='none',count=1,lengthMs=0,
            _hand=hand,_cuts=[t],_strength=e['strength'],_role='chord' if pair else 'attack'))
        last[hand]=t
    if pair:last_pair=t
    preferred=1-h
# 同じ手の実際の発音列をロングにまとめる。反対の手は伴奏・旋律をそのまま拾える。
long_targets=[(13.529,.80),(24.519,.80),(32.521,.90),(40.059,.74),(47.8,.87),(53.61,.9),(65.418,.86),
              (71.6,.82),(80.666,.86),(87.676,.90),(96.28,.87),(104.65,.86),(111.696,.80),(117.8,.82),(123.9,.85),(129.136,.78),(133.08,.74)]
for k,(target,length) in enumerate(long_targets):
    candidates=[n for n in notes if n['type']=='tap' and abs(n['time']/1000-target)<.22 and n['_hand']==k%2]
    if not candidates:continue
    first=min(candidates,key=lambda n:abs(n['time']/1000-target));t=first['time']/1000
    group=[n for n in notes if n['_hand']==first['_hand'] and t<=n['time']/1000<=t+length and n['type']=='tap']
    if len(group)<3:continue
    times=[n['time']/1000 for n in group]
    first.update(type='long',count=len(times),lengthMs=round((times[-1]-t+.055)*1000,4),_cuts=times,_role='attack-roll')
    remove={id(n) for n in group[1:]};notes=[n for n in notes if id(n) not in remove]
# 最後の和音の余韻だけは、左右のロングを最後まで交互に切るフィニッシュ。
final=min(analysis['events'],key=lambda e:abs(e['time']-136.28027))
notes=[n for n in notes if n['time']<final['time']*1000-.12*1000]
for h in (0,1):
    t=final['time'];cuts=[t+k*.32 for k in range(5)]
    notes.append(dict(time=round(t*1000,4),x=1.04 if h==0 else -1.04,y=.06 if h==0 else -.06,type='long',color='red' if h==0 else 'blue',direction='none',count=5,lengthMs=1380,
        _hand=h,_cuts=cuts,_strength=final['strength'],_role='sustained-final-chord'))
notes.sort(key=lambda n:(n['time'],n['_hand']))
DIRS=('up','down','left','right','upleft','upright','downleft','downright')
VECS=dict(zip(DIRS,[(0,1),(0,-1),(-1,0),(1,0),(-2**-.5,2**-.5),(2**-.5,2**-.5),(-2**-.5,-2**-.5),(2**-.5,-2**-.5)]))
plan=[dict(index=i,timesMs=[round(t*1000,4) for t in n['_cuts']],directions=[]) for i,n in enumerate(notes)]
maxspeed=0;mingap=99
for hand in (0,1):
    seq=sorted([(t,i,k) for i,n in enumerate(notes) if n['_hand']==hand for k,t in enumerate(n['_cuts'])])
    states={}
    for j,(t,i,k) in enumerate(seq):
        n=notes[i];following={}
        for d,v in VECS.items():
            if not j:following[d]=(0,[d]);continue
            pt,pi,pk=seq[j-1];prev=notes[pi];gap=t-pt
            delta=(n['x']-prev['x'],n['y']-prev['y']);choices=[]
            for pd,(cost,path) in states.items():
                pv=VECS[pd]
                if sum(pv[a]*v[a] for a in (0,1))>.01 and gap<.5:continue
                travel=.5+math.hypot(*(delta[a]-.25*(v[a]+pv[a]) for a in (0,1)))
                if travel/gap>5.5:continue
                choices.append((cost+travel,path+[d]))
            if choices:following[d]=min(choices,key=lambda p:p[0])
        if not following:raise AssertionError(f'Hand cannot recover: {hand} / {t}')
        states=following
    dirs=min(states.values(),key=lambda p:p[0])[1]
    for (t,i,k),d in zip(seq,dirs):plan[i]['directions'].append(d)
    for (ta,ia,ka),(tb,ib,kb),da,db in zip(seq,seq[1:],dirs,dirs[1:]):
        a,b=notes[ia],notes[ib];va,vb=VECS[da],VECS[db];gap=tb-ta
        speed=(.5+math.hypot(b['x']-a['x']-.25*(va[0]+vb[0]),b['y']-a['y']-.25*(va[1]+vb[1])))/gap
        maxspeed=max(maxspeed,speed);mingap=min(mingap,gap)
grid=base['editorTempoMap']
def beat_at(ms):
    k=next((i for i in range(len(grid)-2,-1,-1) if grid[i]['timeMs']<=ms),0)
    a,b=grid[k],grid[k+1]
    return a['beat']+(ms-a['timeMs'])*(b['beat']-a['beat'])/(b['timeMs']-a['timeMs'])
clean=[]
for n in notes:
    row={k:v for k,v in n.items() if not k.startswith('_')};row['beat']=round(beat_at(n['time']),6);clean.append(row)
    assert n['time']+n['lengthMs']<analysis['duration']*1000
base.update(_comment='RUSH E限定の隠し最難関E。通常・同時・実音の発音列をまとめたロング。フリックなし。終止和音の持続以外は検出アタックに配置。',displayLevel=10,notes=clean)
dest=OUT/'Tools/ChartDesign/RushE'
save(OUT/'Assets/StreamingAssets/Songs/RushE/chart_e.json',base)
save(dest/'e-cut-plan.json',dict(notes=plan))
save(dest/'e-evidence.json',[dict(index=i,timeMs=n['time'],strength=n['_strength'],role=n['_role'],cutTimesMs=plan[i]['timesMs']) for i,n in enumerate(notes)])
groups=collections.Counter(n['time'] for n in notes)
audit=dict(notes=len(notes),cuts=sum(n['count'] for n in notes),types=dict(collections.Counter(n['type'] for n in notes)),
    simultaneousGroups=sum(v==2 for v in groups.values()),maximumSwingSpeed=round(maxspeed,4),minimumSameHandGapMs=round(mingap*1000,3),
    onsetThreshold=.35,minimumDistinctAttackGapMs=75,firstNoteMs=notes[0]['time'],lastNoteEndMs=max(n['time']+n['lengthMs'] for n in notes),
    longDesign='All roll cuts follow detected attacks; the sustained final chord uses five evenly spaced cuts per hand.',flicks=0)
save(dest/'e-audit.json',audit)
print(json.dumps(audit,ensure_ascii=False,indent=2))
