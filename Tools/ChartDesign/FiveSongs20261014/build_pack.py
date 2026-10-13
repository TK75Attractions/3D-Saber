"""音源の発音・音量と提供された拍子表から、展開別の3難易度を組み立てる。

長音の内部カットは検証用。実機では制限時間内の回数で判定される。
再生成: python build_pack.py --sources <audio directory> --output <candidate directory>
"""
import argparse
import collections
import hashlib
import json
import math
import shutil
from pathlib import Path

import numpy as np

from analyze import anchors, time_at

ROOT = Path(__file__).resolve().parent
ORIGINS = {'ManicSonic': 0, 'escapade': .020, 'logic': .028, 'BenkyouRemaster': .024, 'Aobasou': 0}
LEVELS = {'ManicSonic': (3, 6, 8), 'escapade': (3, 5, 7), 'logic': (4, 6, 8),
          'BenkyouRemaster': (3, 6, 8), 'Aobasou': (3, 5, 7)}
DIRS = ('up', 'down', 'left', 'right', 'upleft', 'upright', 'downleft', 'downright')
VECS = dict(zip(DIRS, [(0,1),(0,-1),(-1,0),(1,0),(-.707,.707),(.707,.707),(-.707,-.707),(.707,-.707)]))

def save(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf8')

class Music:
    def __init__(self, spec):
        self.spec = spec
        self.analysis = json.loads((ROOT/'Analysis'/spec['id']/'analysis.json').read_text(encoding='utf8'))
        self.points = anchors(spec['tempos'], ORIGINS[spec['id']])
        self.duration = self.analysis['duration']
        f = np.load(ROOT/'Analysis'/spec['id']/'features.npz')
        self.times, self.onset, self.energy, self.flux = (f[k] for k in ('times','onset','energy','flux'))
        self.last_beat = self.beat(self.analysis['lastActiveSeconds'] - .4)
        self.first_beat = max(0, self.beat(self.analysis['firstActiveSeconds'] - .025))
        self.bar_count = math.ceil(self.last_beat / 4)
        self.bar_energy = np.array([self.mean_energy(b*4, min(b*4+4,self.last_beat)) for b in range(self.bar_count)])
        # 高揚する連続区間を4小節単位で選ぶ。曲間で同じ秒数を流用しない。
        chunks = [(b, float(np.mean(self.bar_energy[b:b+4]))) for b in range(0,self.bar_count-3,4)
                  if self.t(b*4)>3 and self.t((b+4)*4)<self.duration-2]
        cutoff = np.quantile([x[1] for x in chunks], .60)
        self.bright = {bar for b,e in chunks if e>=cutoff for bar in range(b,b+4)}
        self.sections = []
        for b in sorted(self.bright):
            if self.sections and self.sections[-1][1] == b:
                self.sections[-1][1] = b+1
            else: self.sections.append([b,b+1])
        self.cache = {}

    def t(self, beat): return time_at(beat, self.points)
    def bpm(self, beat): return next(p[2] for p in reversed(self.points) if p[0]<=beat)
    def beat(self, t):
        b,s,bpm = next((p for p in reversed(self.points) if p[1]<=t), self.points[0])
        return b+(t-s)*bpm/60
    def mean_energy(self,a,b):
        ids=(self.times>=self.t(a))&(self.times<self.t(b))
        return float(self.energy[:,ids].sum(axis=0).mean()) if ids.any() else 0
    def ev(self,b):
        if b not in self.cache:
            t=self.t(b); ids=np.where(abs(self.times-t)<=.045)[0]
            i=ids[np.argmax(self.onset[ids])]
            self.cache[b]=dict(peakSeconds=round(float(self.times[i]),6),
                              distanceMs=round(float(self.times[i]-t)*1000,3),
                              strength=round(float(self.onset[i]),4))
        return self.cache[b]

def rhythm(m, diff, seed):
    easy, hard = diff=='easy', diff=='hard'
    events=[]
    # 拍の候補は実音で選別。3連符・スイングは勝手に導入しない。
    recipes = ([(0,2),(0,3),(1,3),(0,1,3),(0,2,3)] if easy else
               [(0,1,2,3),(0,1,2.5,3.5),(0,.5,2,3),(0,1.5,2,3),
                (0,1,2,2.5,3.5),(0,.5,1.5,2,3),(0,1,1.5,2.5,3.5),(0,1,2,3,3.5)])
    for bar in range(m.bar_count):
        base=bar*4; bpm=m.bpm(base); bright=bar in m.bright
        # BPM360では半分の刻みにして、テンポ表示に密度を引きずられない。
        step=2 if bpm>240 else .5
        candidates=[base+q for q in np.arange(0,4,step)
                    if m.first_beat-.01<=base+q<=m.last_beat and m.ev(base+q)['strength']>=.27]
        if not candidates: continue
        recipe=recipes[(bar*3+bar//4+seed)%len(recipes)]
        scored=[]
        for b in candidates:
            q=b-base; strength=m.ev(b)['strength']
            score=strength + (.35 if q in recipe else 0) + (.2 if q%1==0 else 0)
            scored.append((score,b))
        count=(2+(bar%4==3 and bright)) if easy else (4+int(bright)) if not hard else (5+int(bright)+int(bar%4==3))
        if bpm<=80: count+=1 if not easy else 0
        if bpm>240: count=2 if easy else 3 if not hard else 4
        chosen=sorted(b for _,b in sorted(scored,reverse=True)[:count])
        group=(2,2,3,3) if hard else (2,2,1,1) if not easy else (1,1,2,2)
        hand_index=0; used=0; side=(bar//2+seed)%2
        for i,b in enumerate(chosen):
            # ノーツが同じ手で詰まりすぎる場合は手を交代。22/33を優先しつつ回復時間を確保。
            if events and side==events[-1]['hand'] and m.t(b)-m.t(events[-1]['beat'])<(.31 if hard else .38):
                side=1-side; used=0
            events.append(dict(beat=float(b),hand=side,bar=bar,kind='single',bright=bright,role='音源の発音',end=float(b)))
            used+=1
            if used>=group[hand_index%len(group)]:
                hand_index+=1;used=0;side=1-side

    # 前半の片手反転を、後半はもう片方の弧状タップと組み合わせる。
    # 元と発展先の両方に同じリズムの発音がある小節だけを採用する。
    developments=[];reserved=set()
    if hard:
        previous=-99
        for bar in sorted(m.bright):
            if bar<m.bar_count*.52 or bar-previous<8 or m.bpm(bar*4)>220:continue
            found=None
            for source in range(4,math.floor(m.bar_count*.48)):
                if source in reserved or source+1 in reserved or source-1 in reserved:continue
                for offsets in ((0,1,2),(1,2,3)):
                    if all(m.ev(k*4+q)['strength']>=.32 and k*4+q<=m.last_beat for k in (source,bar) for q in offsets):
                        found=(source,offsets);break
                if found:break
            if not found:continue
            source,offsets=found;lead=(len(developments)+seed)%2
            for target,kind,role in ((source,'motif','前半・片手の反転フリック'),(bar,'duet','後半・反転フリック＋逆手の弧')):
                events=[e for e in events if e['bar']!=target]
                for k,q in enumerate(offsets):
                    events.append(dict(beat=float(target*4+q),end=float(target*4+q),hand=lead,bar=target,
                                       kind=kind,duetIndex=k,bright=target in m.bright,role=role))
                reserved.add(target)
            developments.append(dict(sourceBar=source+1,developedBar=bar+1,offsets=list(offsets),leadHand='red' if lead==0 else 'blue'))
            previous=bar
            if len(developments)>=4:break
    events.sort(key=lambda e:e['beat'])
    # 高揚部の強い表拍は両手の着地へ。前後の手の回復時間を満たすものだけ使う。
    spacing=8 if easy else 4 if not hard else 2
    last_pair=-999
    for e in list(events):
        if e not in events:continue
        b=e['beat']; bar=e['bar']
        if bar in reserved:continue
        if any(x['bar'] in reserved and abs(m.t(x['beat'])-m.t(b))<.30 for x in events):continue
        if b%1 or not e['bright'] or b-last_pair<spacing or m.ev(b)['strength']<.48: continue
        if (bar+seed)%3==1 and not hard: continue
        if easy or not hard:
            if not all(x is e or abs(m.t(x['beat'])-m.t(b))>=.30 for x in events):continue
        e.update(kind='pair',gold=(bar+int(b)+seed)%3!=0,role='強拍の両手アクセント');last_pair=b
        events=[x for x in events if x is e or abs(m.t(x['beat'])-m.t(b))>=.30]

    # 句末の余韻にロング。片手を空ける形と同時ロングを混ぜる。
    for bar in range(3+seed%3,m.bar_count-1,6 if hard else 8):
        if bar in reserved or bar+1 in reserved:continue
        available=[e for e in events if e['bar']==bar and e['beat']%1==0]
        if not available:continue
        e=max(available,key=lambda e:m.ev(e['beat'])['strength']);b=e['beat']
        length=2 if m.bpm(b)<220 else 4
        if b+length>m.last_beat or any(b<p[0]<b+length for p in m.points):continue
        duration=m.t(b+length)-m.t(b)
        if duration<.6 or m.mean_energy(b,b+length)<np.median(m.bar_energy)*.28:continue
        pair=bar%3==0
        e.update(kind='longpair' if pair else 'long',end=b+length,gold=False,role='句末ロング')
        hand=e['hand']
        events=[x for x in events if x is e or not (b-.8<=x['beat']<=b+length+.8 and
                    (pair or x['hand']==hand or x['kind']=='pair'))]

    for item in developments:
        for key in ('sourceBar','developedBar'):
            lo=(item[key]-1)*4+item['offsets'][0];hi=(item[key]-1)*4+item['offsets'][-1]
            events=[e for e in events if e['kind'] in ('motif','duet') or
                    not (m.t(lo)-.40<m.t(e['beat'])<m.t(lo) or m.t(hi)<m.t(e['beat'])<m.t(hi)+.40)]
    return events,developments

def make_notes(m,events,diff,seed):
    notes=[]
    hard=diff=='hard'
    phase=[0,0]
    for e in sorted(events,key=lambda e:e['beat']):
        hands=[0,1] if e['kind'] in ('pair','longpair','duet') else [e['hand']]
        for hand in hands:
            phase[hand]+=1
            sign=1 if hand==0 else -1
            # 左右の読める領域を保ち、対角と水平の折り返しを句ごとに変える。
            horizontal=(e['bar']+seed)%4 in (1,2)
            k=phase[hand]; amplitude=.18 if diff=='easy' else .25
            x=sign*(1.28+(amplitude if k%2 else -amplitude)) if horizontal else sign*1.28
            y=(.30 if e['bar']%2 else -.12) if horizontal else (.38 if k%2 else -.38)
            if e['kind'] in ('long','longpair'): x,y=sign*1.6,-.55
            if e['kind'] in ('motif','duet'):
                j=e['duetIndex']
                if hand==e['hand']:x,y=sign*(1.1+.5*(j%2)),.45
                else:x,y=sign*(1.0+.25*j),[-.45,-.15,-.45][j]
            long=e['end']>e['beat']
            # 内部カットも拍へ置き、最後の拍のあとに期限の余白を残す。
            cut_beats=[e['beat']]
            if long:
                minimum=.45 if diff=='easy' else .29
                for b in np.arange(e['beat']+.5,e['end']+.001,.5):
                    if m.t(b)-m.t(cut_beats[-1])>=minimum:cut_beats.append(float(b))
                if len(cut_beats)==1:cut_beats.append(e['end'])
            count=len(cut_beats)
            n=dict(beat=e['beat'],time=round(m.t(e['beat'])*1000,4),x=round(x,3),y=round(y,3),
                   type='long' if long else 'tap',color='gold' if e.get('gold') else ('red' if hand==0 else 'blue'),
                   direction='none',count=count,lengthMs=round((m.t(e['end'])-m.t(e['beat']))*1000+90,4) if long else 0)
            n['_cutBeats']=cut_beats
            n['_hand']=hand;n['_role']=e['role'];n['_bar']=e['bar']
            n['_arrow']=not long and not e.get('gold') and (e['kind'] in ('motif','duet') and hand==e['hand'] or
                      diff!='easy' and e['kind']=='single' and (e['bar']+seed)%4!=(0 if hard else 1) and (hard or k%3==0))
            n['_forced']=None
            if e['kind'] in ('motif','duet') and hand==e['hand']:
                positive=(e['duetIndex']%2==1)==(hand==0)
                n['_forced']='right' if positive else 'left'
            notes.append(n)
    return sorted(notes,key=lambda n:(n['time'],n['_hand']))

def path_for(notes,m):
    # 実際の振り抜きと次の入口を8方向で探索。自由タップも振った向きを保持する。
    plan=[dict(index=i,timesMs=[],directions=[]) for i in range(len(notes))]
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

def build(m,diff,seed):
    events,developments=rhythm(m,diff,seed)
    notes=make_notes(m,events,diff,seed)
    fixes=[]
    for attempt in range(200):
        plan,failure=path_for(notes,m)
        if failure is None:break
        i,_,hand,t=failure;n=notes[i]
        # 成立しない矢印は自由に戻す。間隔そのものが足りない場合は該当打点を休符にする。
        if n['_arrow']:
            n['_arrow']=False;fixes.append(dict(beat=n['beat'],action='入口を自由タップへ'))
        else:
            bad_time=n['time'];fixes.append(dict(beat=n['beat'],action='手の回復休符'))
            notes=[x for x in notes if x['time']!=bad_time]
    else: raise AssertionError('振り経路が成立しない')
    for i,n in enumerate(notes):
        if n['_arrow']:n['direction']=plan[i]['directions'][0];n['type']='direction'
    # 同じ配置を2小節続けない。手順も同じなら弱い打点を休符へ変える。
    def sig(bar):return [(n['beat']%4,n['color'],n['type'],n['direction'],n['count']) for n in notes if n['_bar']==bar]
    for b in range(1,m.bar_count):
        if sig(b) and sig(b)==sig(b-1):
            # 片方の表拍を休符へ。音の無い場所へノーツを足して変化させない。
            ids=[i for i,n in enumerate(notes) if n['_bar']==b and n['count']==1]
            if ids:
                weakest=min(ids,key=lambda i:m.ev(notes[i]['beat'])['strength']);at=notes[weakest]['time']
                notes=[n for n in notes if n['time']!=at]
                fixes.append(dict(bar=b+1,action='前小節と同一になる打点を休符へ'))
    # 休符にした後も入口方向を再評価する。
    for _ in range(100):
        plan,failure=path_for(notes,m)
        if failure is None:break
        n=notes[failure[0]];n['_arrow']=False;n['direction']='none';n['type']='long' if n['count']>1 else 'tap'
    assert failure is None
    for i,n in enumerate(notes):
        if n['_arrow']:n['direction']=plan[i]['directions'][0];n['type']='direction'
    evidence=[dict(index=i,beat=n['beat'],hand='red' if n['_hand']==0 else 'blue',role=n['_role'],**m.ev(n['beat'])) for i,n in enumerate(notes)]
    clean=[{k:v for k,v in n.items() if not k.startswith('_')} for n in notes]
    # 終点のアンカーで、最後のBPMを外挿するときの傾きを確定する。
    end_beat=max(m.last_beat+8,m.points[-1][0]+8)
    tempo=[dict(beat=b,timeMs=round(t*1000,4)) for b,t,bpm in m.points]
    tempo.append(dict(beat=round(end_beat,4),timeMs=round(m.t(end_beat)*1000,4)))
    markers=[dict(timeMs=round(t*1000,4),name=f'{b/4+1:g}小節 / BPM {bpm}') for b,t,bpm in m.points]
    markers.extend(dict(timeMs=round(m.t(a*4)*1000,4),name=f'高揚部 {j+1}') for j,(a,b) in enumerate(m.sections))
    chart=dict(_comment='提供音源の発音と拍子表を照合。左右の連打、反転フリック、両手アクセント、句末ロング。',
               bpm=m.spec['tempos'][0][1],coordScale=1,offsetMs=0,beatZeroMs=ORIGINS[m.spec['id']]*1000,
               displayLevel=LEVELS[m.spec['id']][('easy','normal','hard').index(diff)],
               timeSignatures=[dict(beat=0,numerator=m.spec['meter'][0],denominator=m.spec['meter'][1])],
               editorTempoMap=tempo,editorMarkers=sorted(markers,key=lambda p:p['timeMs']),notes=clean)
    counts=collections.Counter(n['type'] for n in clean)
    paired=sum(sum(n['time']==t for n in clean)==2 for t in {n['time'] for n in clean})
    summary=dict(notes=len(clean),cuts=sum(n['count'] for n in clean),types=dict(counts),simultaneousBeats=paired,
                 goldNotes=sum(n['color']=='gold' for n in clean),developments=developments,
                 recoveryAdjustments=fixes,flowPassed=True,
                 identicalAdjacentBars=[b+1 for b in range(1,m.bar_count) if sig(b) and sig(b)==sig(b-1)])
    assert not summary['identicalAdjacentBars']
    for n in clean:assert n['time']+n['lengthMs']<m.duration*1000 and n['time']>=0
    return chart,dict(notes=plan),evidence,summary

def main():
    parser=argparse.ArgumentParser();parser.add_argument('--sources',type=Path,required=True)
    parser.add_argument('--output',type=Path,default=ROOT/'Candidate')
    parser.add_argument('--skip-audio',action='store_true');args=parser.parse_args()
    summaries={}
    for seed,spec in enumerate(json.loads((ROOT/'songs.json').read_text(encoding='utf8'))):
        m=Music(spec);folder=args.output/'Assets/StreamingAssets/Songs'/spec['id'];folder.mkdir(parents=True,exist_ok=True)
        source=args.sources/spec['source'];extension=source.suffix;audio=folder/('audio'+extension)
        if not args.skip_audio:
            shutil.copyfile(source,audio)
        sections=[dict(label=f'高揚部 {i+1}',startSeconds=round(m.t(a*4),6),endSeconds=round(min(m.t(b*4),m.duration-.5),6),
                       fadeInSeconds=.8,fadeOutSeconds=1.4,intensity=.85 if i==0 else 1) for i,(a,b) in enumerate(m.sections)]
        preview=max(m.sections,key=lambda p:(p[1]-p[0],p[0]))[0]*4
        stage=dict(schemaVersion=1,displayName=spec['title'],composer=spec['composer'],timeOrigin='audio-file-start',
                   audioSha256=hashlib.sha256(audio.read_bytes()).hexdigest(),previewStartSeconds=round(m.t(preview),6),
                   sections=sections,hideBarLines=False)
        save(folder/'stage.json',stage)
        summaries[spec['id']]={}
        for diff in ('easy','normal','hard'):
            chart,plan,evidence,summary=build(m,diff,seed)
            save(folder/f'chart_{diff}.json',chart)
            if diff=='normal':save(folder/'chart.json',chart)
            design=args.output/'Tools/ChartDesign/FiveSongs20261014'/spec['id']
            save(design/f'{diff}-cut-plan.json',plan);save(design/f'{diff}-evidence.json',evidence)
            summaries[spec['id']][diff]=summary
            print(spec['id'],diff,json.dumps({k:v for k,v in summary.items() if k not in ('recoveryAdjustments','developments')},ensure_ascii=False),flush=True)
        save(design/'analysis.json',m.analysis)
    save(args.output/'Tools/ChartDesign/FiveSongs20261014/audit.json',summaries)
    for name in ('songs.json','build_pack.py','analyze.py','verify_pack.py'):
        shutil.copyfile(ROOT/name,args.output/'Tools/ChartDesign/FiveSongs20261014'/name)

if __name__=='__main__':main()
