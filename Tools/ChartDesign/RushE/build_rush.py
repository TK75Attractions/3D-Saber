"""RUSH E原版: 実音の発音時刻を優先した文化祭用の3譜面。

前奏と加速部は自由な時刻配置、拍地図は編集補助の推定値。音源を引き伸ばさない。
"""
import sys
from pathlib import Path
ROOT=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else Path(__file__).resolve().parent
sys.path.insert(0,str(ROOT/'deps'))
import numpy as np
from PIL import Image,ImageDraw,ImageFont
import json, math, collections, hashlib, subprocess, uuid, shutil, os

STAGE=ROOT/'Staging'
SONG=STAGE/'Assets/StreamingAssets/Songs/RushE'
DESIGN=STAGE/'Tools/ChartDesign/RushE'
DESIGN.mkdir(parents=True,exist_ok=True)
report=json.loads((ROOT/'Analysis/analysis.json').read_text(encoding='utf8'))
features=np.load(ROOT/'Analysis/features.npz')
tt,oo=features['times'],features['onset']
events=report['events']
DIRS=('up','down','left','right','upleft','upright','downleft','downright')
VECS=dict(zip(DIRS,[(0,1),(0,-1),(-1,0),(1,0),(-2**-.5,2**-.5),(2**-.5,2**-.5),(-2**-.5,-2**-.5),(2**-.5,-2**-.5)]))
# 音源の局所的な周期から作った編集用グリッド。原譜のテンポ指定とは断定しない。
TEMPOS=[(0.09478,120),(13.52925,70),(18.89705,95),(24.51927,127),
        (30.76508,172),(34.55646,195),(37.43991,228),(39.16599,265),
        (40.05941,155),(65.418,162),(80.66667,120),(87.676,167.3),
        (112.22494,171.5),(125.711,190),(129.13651,200),(135.41224,120)]
MAP=[]
for k,(time,bpm) in enumerate(TEMPOS):
    beat=0 if k==0 else MAP[-1][0]+(time-TEMPOS[k-1][0])*TEMPOS[k-1][1]/60
    MAP.append((beat,time,bpm))

def beat_at(time):
    b,t,bpm=next((p for p in reversed(MAP) if p[1]<=time),MAP[0])
    return b+(time-t)*bpm/60

def bpm_at(time):return next((p[2] for p in reversed(MAP) if p[1]<=time),120)
def save(path,data):
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text(json.dumps(data,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
def closest(time,window=.08):
    choices=[e for e in events if abs(e['time']-time)<=window]
    return max(choices,key=lambda e:e['strength']) if choices else None

def select_rhythm(diff):
    rank=('easy','normal','hard').index(diff)
    rows=[]
    # 曲の出だしのEは、それぞれの明確な発音を拾い、加速する連打は手の回復に合わせて間引く。
    sections=[(0,13.4,(.78,.47,.30)),(13.4,40.05,(.68,.39,.265)),
              (40.05,65.4,(.68,.34,.235)),(65.4,80.5,(.66,.34,.23)),
              (80.5,87.65,(.66,.40,.27)),(87.65,112.1,(.64,.32,.225)),
              (112.1,129,(.63,.32,.225)),(129,135.35,(.72,.40,.26))]
    for start,end,gaps in sections:
        candidates=[dict(e) for e in events if start<=e['time']<end and e['strength']>(.62,.5,.39)[rank]]
        gap=gaps[rank]
        # 強い音を優先する抑制。無音や減衰の細かな揺れからノーツを増やさない。
        chosen=[]
        for e in sorted(candidates,key=lambda e:-e['strength']):
            if all(abs(e['time']-p['time'])>=gap for p in chosen):chosen.append(e)
        rows.extend(chosen)
    rows.sort(key=lambda e:e['time'])
    # 同じ強さの連打を拾い続けるのを避け、前半の旋律と後半の低音アクセントを残す。
    for r in rows:r.update(kind='single',length=0,count=1)
    # 各フレーズの音の伸び・連打を、必要回数を切るロングとしてまとめる。
    holds=[(13.52925,.62),(32.52109,.98),(53.61,.92),(65.418,.96),(80.66667,.86),(111.69615,.82),(136.28027,1.45)]
    if rank>0:holds += [(24.51927,.85),(47.8,.85),(96.28,.94),(123.9,.86)]
    for target,length in holds:
        e=closest(target,.24)
        if e is None:continue
        count=2 if rank==0 else 3
        length=max(length,(count-1)*(.40 if rank==1 else .34))
        padding=(.42,.31,.26)[rank]
        rows=[r for r in rows if not e['time']-padding<r['time']<e['time']+length+padding]
        rows.append(dict(e,kind='long',length=length,count=count))
    # フィナーレの強い和音。最後のロングとぶつからない単発にする。
    final=closest(135.41224)
    if final and all(abs(r['time']-final['time'])>(.52,.35,.28)[rank] for r in rows):
        rows.append(dict(final,kind='pair',length=0,count=1))
    rows.sort(key=lambda e:e['time'])
    last_pair=-10
    for i,r in enumerate(rows):
        if r['kind']!='single' or not i or i==len(rows)-1:continue
        before=r['time']-rows[i-1]['time']-rows[i-1]['length']
        after=rows[i+1]['time']-r['time']
        if r['time']>18 and r['strength']>(1.30,1.25,1.12)[rank] and min(before,after)>=(.61,.40,.29)[rank] and r['time']-last_pair>(13,8,5.5)[rank]:
            r['kind']='pair';last_pair=r['time']
    return rows

def path_for(notes):
    plan=[dict(index=i,timesMs=[round(t*1000,4) for t in n['_cuts']],directions=[]) for i,n in enumerate(notes)]
    for hand in (0,1):
        seq=sorted([(i,k,t) for i,n in enumerate(notes) if n['_hand']==hand for k,t in enumerate(n['_cuts'])],key=lambda p:p[2])
        states={}
        for j,(i,k,t) in enumerate(seq):
            n=notes[i];following={}
            for d in DIRS:
                v=VECS[d]
                if not j:following[d]=(0,[d]);continue
                pi,pk,pt=seq[j-1];prev=notes[pi];gap=t-pt
                delta=(n['x']-prev['x'],n['y']-prev['y']);dist=math.hypot(*delta)
                if n['_arrow'] and (dist<.08 or sum(delta[a]*v[a] for a in (0,1))/dist<.706):continue
                choices=[]
                for pd,(cost,path) in states.items():
                    pv=VECS[pd];turn=sum(pv[a]*v[a] for a in (0,1))
                    limit=-.49 if n['_arrow'] and prev['_arrow'] else .01
                    if gap<120/bpm_at(t) and turn>limit:continue
                    travel=.5+math.hypot(*(delta[a]-.25*(v[a]+pv[a]) for a in (0,1)))
                    if gap<=0 or travel/gap>5.5:continue
                    choices.append((cost+travel+(.05 if d in DIRS[4:] else 0),path+[d]))
                if choices:following[d]=min(choices,key=lambda p:p[0])
            if not following:return None,i
            states=following
        if seq:
            _,directions=min(states.values(),key=lambda p:p[0])
            for (i,k,t),d in zip(seq,directions):plan[i]['directions'].append(d)
    return plan,None

def build(diff):
    rank=('easy','normal','hard').index(diff)
    rows=select_rhythm(diff);notes=[];last=[-10.,-10.];counts=[0,0];hand=0
    for i,r in enumerate(rows):
        t=r['time'];phase=int(t//16)
        if t-last[hand]<(.60,.40,.32)[rank] or i%(2 if rank else 1)==0:hand=1-hand
        pair=r['kind']=='pair'
        for h in ((0,1) if pair else (hand,)):
            path=[(.95,-.30),(1.25,.36),(.82,.10),(1.10,-.44),(1.30,.45),(.98,-.05)]
            if rank==0:path=[(1,-.16),(1.10,.30),(.90,.05),(1.10,-.24)]
            x,y=path[(counts[h]+phase)%len(path)]
            if pair:x,y=1.15,.12 if phase%2 else -.12
            if r['kind']=='long':x,y=1.08,.15
            if h==1:x=-x
            counts[h]+=1
            cut_times=[t+j*r['length']/(r['count']-1) for j in range(r['count'])] if r['count']>1 else [t]
            last[h]=cut_times[-1]
            notes.append(dict(beat=round(beat_at(t),6),time=round(t*1000,4),x=x,y=y,
                type='long' if r['count']>1 else 'tap',color='gold' if pair and phase%2==0 else ('red' if h==0 else 'blue'),
                direction='none',count=r['count'],lengthMs=round((r['length']+.09)*1000,4) if r['count']>1 else 0,
                _hand=h,_cuts=cut_times,_arrow=rank>0 and r['kind']=='single' and i%(5 if rank==1 else 3)==0,
                _strength=r['strength'],_kind=r['kind']))
    notes.sort(key=lambda n:(n['time'],n['_hand']))
    adjustments=[]
    for attempt in range(400):
        plan,bad=path_for(notes)
        if bad is None:break
        n=notes[bad]
        if n['_arrow']:n['_arrow']=False;adjustments.append(dict(time=n['time'],action='free direction'))
        else:
            t=n['time'];notes=[m for m in notes if m['time']!=t]
            adjustments.append(dict(time=t,action='recovery rest'))
    assert bad is None
    for n,p in zip(notes,plan):
        if n['_arrow']:n['direction']=p['directions'][0];n['type']='direction'
    clean=[{k:v for k,v in n.items() if not k.startswith('_')} for n in notes]
    # 譜面の判定時刻は発音ピークに直接合わせる。推定BPMでノーツを移動させない。
    grid=[dict(beat=round(b,6),timeMs=round(t*1000,4)) for b,t,_ in MAP]
    grid.append(dict(beat=round(beat_at(144.2),6),timeMs=144200))
    chart=dict(_comment='公式SoundCloud原版。発音時刻を優先した3難易度。BPM地図は編集補助の区間別推定値で、原譜の指定ではない。',
        bpm=120,coordScale=1,offsetMs=0,beatZeroMs=94.78,displayLevel=(3,6,8)[rank],
        timeSignatures=[dict(beat=0,numerator=4,denominator=4)],editorTempoMap=grid,
        editorMarkers=[dict(timeMs=round(t*1000,4),name=label) for t,label in [(0.09478,'E / 前奏'),(13.52925,'旋律と加速'),(40.05941,'主部'),(65.418,'展開'),(80.66667,'間奏'),(87.676,'主題の再展開'),(112.22494,'最終加速'),(135.41224,'フィナーレ'),(136.28027,'最後の和音')]],notes=clean)
    # 生成器と別の再計算で、時間範囲・同時切り・振りの移動量を確認する。
    max_speed=0.;min_gap=99.;arrows=collections.Counter()
    for n,p in zip(clean,plan):
        assert n['time']>=0 and n['time']+n['lengthMs']<report['duration']*1000
        assert len(p['timesMs'])==len(p['directions'])==n['count']
        assert abs(p['timesMs'][0]-n['time'])<.001
        assert p['timesMs'][-1]<=n['time']+n['lengthMs']+.001
        arrows[n['type']]+=1
    for h in (0,1):
        cuts=sorted([(t/1000,i,d) for i,(n,p) in enumerate(zip(notes,plan)) if n['_hand']==h for t,d in zip(p['timesMs'],p['directions'])])
        for (ta,ia,da),(tb,ib,db) in zip(cuts,cuts[1:]):
            a,b=notes[ia],notes[ib];gap=tb-ta
            delta=(b['x']-a['x'],b['y']-a['y']);va,vb=VECS[da],VECS[db]
            speed=(.5+math.hypot(*(delta[j]-.25*(va[j]+vb[j]) for j in (0,1))))/gap
            assert gap>(.33 if rank else .40) or speed<5.5
            assert speed<=5.501
            max_speed=max(max_speed,speed);min_gap=min(min_gap,gap)
    groups=collections.defaultdict(list)
    for n in clean:groups[n['time']].append(n)
    for group in groups.values():
        assert len(group)<=2
        if len(group)==2:assert group[0]['x']*group[1]['x']<0 and abs(group[0]['x']-group[1]['x'])>1.5
    audit=dict(notes=len(clean),cuts=sum(n['count'] for n in clean),types=dict(arrows),
        simultaneousGroups=sum(len(g)==2 for g in groups.values()),goldNotes=sum(n['color']=='gold' for n in clean),
        maxSwingSpeed=round(max_speed,4),minimumSameHandGapMs=round(min_gap*1000,3),adjustments=adjustments,
        firstNoteMs=clean[0]['time'],lastNoteEndMs=max(n['time']+n['lengthMs'] for n in clean))
    save(SONG/f'chart_{diff}.json',chart)
    if diff=='normal':save(SONG/'chart.json',chart)
    save(DESIGN/f'{diff}-cut-plan.json',dict(notes=plan))
    save(DESIGN/f'{diff}-evidence.json',[dict(index=i,timeMs=n['time'],peakSeconds=n['time']/1000,strength=n['_strength'],role=n['_kind']) for i,n in enumerate(notes)])
    return audit

audit={diff:build(diff) for diff in ('easy','normal','hard')}
save(DESIGN/'audit.json',audit)
save(DESIGN/'timing-analysis.json',{k:v for k,v in report.items() if k!='events'})
save(SONG/'stage.json',dict(schemaVersion=1,displayName='RUSH E',composer='Andrew Wrangell',artist='Sheet Music Boss',
    sourceUrl='https://www.youtube.com/watch?v=Qskm9MTz2V4',requiredAudioFile='audio.wav',listed=True,
    timeOrigin='audio-file-start',audioSha256=report['sha256'],previewStartSeconds=88.395,
    hideBarLines=True,sections=[dict(label=label,startSeconds=a,endSeconds=b,fadeInSeconds=.6,fadeOutSeconds=1.1,intensity=power)
        for label,a,b,power in [('主部',40.05941,64.05941,.78),('主題の再展開',87.676,111.70,.9),('最終加速',112.22494,136.28027,1.0)]]))
source=json.loads((ROOT/'Source/provenance.json').read_text(encoding='utf8'))
save(DESIGN/'source.json',source)
(SONG/'CREDITS.txt').write_text('RUSH E\nComposed by Andrew Wrangell\nMusic by Sheet Music Boss\nOriginal video: https://www.youtube.com/watch?v=Qskm9MTz2V4\nOfficial audio: https://soundcloud.com/sheetmusicboss/rush-e-impossible3\nUsed with permission for the three-day school festival.\n',encoding='utf8')
# ジャケットは独自の文字・図形。公式動画の画像を転載しない。
im=Image.new('RGB',(1024,1024),'#101122');d=ImageDraw.Draw(im)
for y in range(1024):
    d.line((0,y,1024,y),fill=(16+int(y*.022),17+int(y*.010),34+int(y*.030)))
for k in range(12):d.line((k*100-220,0,k*100+300,1024),fill='#353048',width=2)
fontpath=os.environ.get('RUSH_E_COVER_FONT') or next((str(p) for p in (
    Path('C:/Windows/Fonts/arialbd.ttf'), Path('/System/Library/Fonts/Supplemental/Arial Bold.ttf'),
    Path('/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf')) if p.exists()), None)
if not fontpath:
    raise SystemExit('RUSH_E_COVER_FONTに太字TrueTypeフォントを指定してください。')
big=ImageFont.truetype(fontpath,570);small=ImageFont.truetype(fontpath,78);name=ImageFont.truetype(fontpath,26)
d.text((510,70),'RUSH',font=small,fill='#faf5e9',anchor='mt')
for off,col in [(13,'#fa5070'),(-10,'#42ded5'),(0,'#faf5e9')]:d.text((512+off,116),'E',font=big,fill=col,anchor='mt')
for i in range(14):
    x=54+i*65;d.rounded_rectangle((x,818,x+59,960),radius=5,fill='#f4f0e7')
    if i%7 not in (2,6):d.rounded_rectangle((x+45,818,x+76,904),radius=3,fill='#151526')
d.text((512,741),'SHEET MUSIC BOSS',font=name,fill='#f4f0e7',anchor='mt')
im.save(SONG/'cover.png')
for p in list((STAGE/'Assets').rglob('*')):
    if p.suffix=='.meta' or p.name.startswith('audio.'):continue
    meta=Path(str(p)+'.meta')
    if meta.exists() or (Path(__file__).resolve().parents[3]/p.relative_to(STAGE)).exists():continue
    content='fileFormatVersion: 2\nguid: '+uuid.uuid4().hex+'\n'
    if p.is_dir():content+='folderAsset: yes\n'
    content+='DefaultImporter:\n  externalObjects: {}\n  userData:\n  assetBundleName:\n  assetBundleVariant:\n'
    meta.write_text(content,encoding='utf8')
print(json.dumps(audit,ensure_ascii=False,indent=2))
