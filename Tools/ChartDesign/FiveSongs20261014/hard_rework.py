"""Hardの実音の打点を補い、発音間隔に合う広い経路を候補へ出力する。"""
import argparse,collections,copy,json,math,sys
from pathlib import Path
import numpy as np
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE.parent/'BeatSaberPatternBank'))
from integrate_patterns import internal,read,digest,plan_notes
from scope_guard import authorize_specs
from rechart import Music,FORM
from build_pack import path_for,save

# 曲の展開に対応する短い技。前後2拍を入口・出口に使い、通常配置へ戻す。
TECHNIQUES={
 'ManicSonic':[(48,56,'vertical'),(104,112,'cross'),(248,256,'vertical'),(336,344,'cross')],
 'escapade':[(40,48,'cross'),(112,120,'vertical'),(184,192,'same-side'),(216,224,'cross'),(288,296,'vertical')],
 'logic':[(64,72,'vertical'),(120,128,'cross'),(192,200,'same-side'),(264,272,'vertical'),(352,360,'cross'),(424,432,'vertical')],
 'BenkyouRemaster':[(48,56,'vertical'),(92,100,'cross'),(144,152,'same-side'),(224,232,'cross'),(264,272,'vertical')],
 'Aobasou':[(40,48,'same-side'),(104,112,'vertical'),(192,200,'cross'),(232,240,'vertical'),(280,288,'cross')]
}
# 曲ごとの高低の文章。拍ごとに上下反転せず、2小節の中で弧・段・応答を描く。
CONTOURS={
 'ManicSonic':[[-.65,-.15,.85,.35,-.65],[.7,.7,-.5,-.5,.7]],
 'escapade':[[-.75,.35,.35,-.75,-.75],[.65,.65,-.35,-.75,.65]],
 'logic':[[.9,.9,-.65,-.65,.9],[-.8,.15,.15,.8,-.8],[.2,-.75,.8,.8,.2]],
 'BenkyouRemaster':[[-.85,-.25,.5,.95,-.85],[.8,.1,-.75,-.2,.8]],
 'Aobasou':[[-.65,.35,.95,.35,-.65],[.7,.1,-.75,-.1,.7]]
}
INTENT={
 'ManicSonic':'主拍と実音の半拍をつなぎ、主題の山で外側・上段へ開く。',
 'escapade':'裏拍の跳ねと明瞭な8分打点を回収。速い連打は近く、句の切れ目で交差する。',
 'logic':'変速と発音の空白を保ち、8/8の細分と段差の応答を区別する。',
 'BenkyouRemaster':'強い細分を短いフィルにし、100 BPMの間では移動に余裕を置く。',
 'Aobasou':'主拍・裏拍の強い発音を追い、歌のフレーズを長い高低の弧で受け渡す。'}

def ordered(ns):return sorted(ns,key=lambda n:(n['time'],n['_hand']))
def clean(n):return {k:v for k,v in n.items() if not k.startswith('_')}
def detail(m,b):return dict(beat=float(b),seconds=round(m.t(b),6),**m.ev(float(b)))

def salient(m):
 # 強い8分と、より強い16分だけを候補にする。旋律の正確な採譜ではない。
 rows=[]
 for b in np.arange(0,m.last_beat,.25):
  b=float(b)
  if m.bpm(b)<90 or m.bpm(b)>205 or not m.supported(b):continue
  ev=m.ev(b);half=abs(b*2-round(b*2))<1e-6
  if abs(ev['distanceMs'])>28 or ev['strength']<(.60 if half else .95):continue
  # 16分を常時敷き詰めず、実際に強い細分がある小節後半だけをフィル候補にする。
  if not half and b%4<2:continue
  if not half and ev['strength']<1.1*max(m.ev(b-.25)['strength'],m.ev(b+.25)['strength']):continue
  rows.append(dict(detail(m,b),subdivision=8 if half else 16))
 return rows

def add_rhythm(ns,m):
 removed=[];added=[];rejected=[]
 groups=collections.Counter(n['beat'] for n in ns)
 # 弱く拍から外れた既存の単打だけを除く。両手・長音・矢印の意味は保つ。
 keep=[]
 for n in ns:
  e=m.ev(n['beat'])
  if n['type']=='tap' and n['color']!='gold' and groups[n['beat']]==1 and e['strength']<.38 and abs(e['distanceMs'])>25:
   removed.append(dict(detail(m,n['beat']),reason='弱い発音かつ拍とのずれが大きい定型打点',note=clean(n)))
  else:keep.append(n)
 # 削除で矢印の直前が変わる場合はその削除を取り消す。
 if plan_notes(keep,m,'hard') is not None:ns=keep
 else:removed=[]
 candidates=salient(m)
 for e in sorted(candidates,key=lambda e:-e['strength']):
  b=e['beat'];t=m.t(b)*1000
  if any(abs(n['beat']-b)<.0001 for n in ns):continue
  options=[]
  for h in (0,1):
   seq=sorted([n for n in ns if n['_hand']==h],key=lambda n:n['time']);prev=next((n for n in reversed(seq) if n['time']<t),None);nxt=next((n for n in seq if n['time']>t),None)
   gap0=(t-prev['time']-prev['lengthMs'])/1000 if prev else 10
   gap1=(nxt['time']-t)/1000 if nxt else 10
   if min(gap0,gap1)<.145:continue
   if prev and nxt:
    ratio=(t-prev['time'])/(nxt['time']-prev['time']);x=prev['x']*(1-ratio)+nxt['x']*ratio;y=prev['y']*(1-ratio)+nxt['y']*ratio
   else:x=(1 if h==0 else -1)*1.2;y=0
   n=dict(beat=b,time=round(t,4),x=round(x,4),y=round(y,4),type='tap',color='red' if h==0 else 'blue',direction='none',count=1,lengthMs=0,
    _hand=h,_cutBeats=[b],_arrow=False,_forced=None,_role='Hard実音: '+('半拍' if e['subdivision']==8 else '短い16分フィル'),_phrase=int(b//16),_material='hard-audio')
   options.append((min(gap0,gap1),n))
  accepted=False
  for _,n in sorted(options,key=lambda x:-x[0]):
   outcome=plan_notes(ns+[n],m,'hard')
   if outcome is not None:
    ns,_=outcome;added.append(dict(e,hand=n['color'],reason='明瞭な発音に既存の打点がなかった'));accepted=True;break
  if not accepted:rejected.append(dict(e,reason='ロング占有または前後の振りの余裕を優先'))
 return ns,dict(added=added,removed=removed,rejected=rejected,candidates=candidates)

def spatial(ns,m,sid):
 base=copy.deepcopy(ns);desired=copy.deepcopy(ns)
 # 強弱は動く幅へ反映。半拍以下の密集部分は後段で経路に応じて縮める。
 for n in desired:
  b=n['beat'];h=n['_hand'];sign=1 if h==0 else -1
  si,section=next(((i,s) for i,s in enumerate(FORM[sid]) if s[0]<=b<s[1]),(0,FORM[sid][0]))
  q=(b-section[0])%8
  contour=CONTOURS[sid][si%len(CONTOURS[sid])]
  y=float(np.interp(q,[0,2,4,6,8],contour));y=(y if h==0 else -.75*y)+(.13 if h==0 else -.13)
  strength=min(1,max(0,(m.ev(b)['strength']-.3)/1.1))
  width=1.30+.58*strength+.18*math.sin(math.pi*q/8)
  if section[2] in ('sparse','slow','outro'):width+=.12
  n['x']=round(sign*min(2.13,width),4);n['y']=round(max(-1.15,min(1.15,y)),4)
 # 既存の長音・強い矢印の動線は、その入口も含め保護する。
 locked=set()
 for h in (0,1):
  indices=[i for i,n in enumerate(ns) if n['_hand']==h]
  for j,i in enumerate(indices):
   if ns[i]['_arrow'] or ns[i]['type']=='long':
    for k in indices[max(0,j-1):j+2]:locked.add(k)
 for i in locked:desired[i]['x'],desired[i]['y']=base[i]['x'],base[i]['y']
 # 広い候補がつながらないとき、その境界付近だけ元の経路へ寄せる。
 scales=[1.0]*len(ns)
 for attempt in range(800):
  plan,fail=path_for(desired,m)
  if not fail:break
  idx,_,hand,_=fail;ids=[i for i,n in enumerate(desired) if n['_hand']==hand];j=ids.index(idx)
  for k in ids[max(0,j-2):j+2]:
   scales[k]*=.68
   if scales[k]<.015:scales[k]=0
   for axis in ('x','y'):desired[k][axis]=round(base[k][axis]+(ns_desired(sid,m,base[k],axis)-base[k][axis])*scales[k],4) if k not in locked else base[k][axis]
 else:raise AssertionError(('広い通常配置の経路が成立しない',sid))
 ordinary=copy.deepcopy(desired);tech=[]
 for start,end,kind in TECHNIQUES[sid]:
  accepted=False
  for amplitude in (1,.85,.7):
   trial=copy.deepcopy(desired)
   for i,n in enumerate(trial):
    if not start<=n['beat']<end or i in locked:continue
    q=(n['beat']-start)/(end-start);blend=min(1,q*4,(1-q)*4)*amplitude;h=n['_hand'];sgn=1 if h==0 else -1
    if kind=='vertical':x=.1;y=sgn*1.12
    elif kind=='cross':x=-sgn*1.08;y=sgn*.85
    else:x=.95+(h*.25);y=sgn*1.05
    n['x']=round(n['x']*(1-blend)+x*blend,4);n['y']=round(n['y']*(1-blend)+y*blend,4)
   pairs=collections.defaultdict(list)
   for n in trial:pairs[n['time']].append(n)
   if any(len(g)==2 and math.dist((g[0]['x'],g[0]['y']),(g[1]['x'],g[1]['y']))<=1.5 for g in pairs.values()):continue
   plan,fail=path_for(trial,m)
   if fail:continue
   desired=trial;accepted=True;tech.append(dict(startBeat=start,endBeat=end,startBar=start//4+1,startSeconds=round(m.t(start),4),kind=kind,amplitude=amplitude,reason=INTENT[sid]));break
  if not accepted:tech.append(dict(startBeat=start,endBeat=end,kind=kind,accepted=False,reason='両手間隔または前後の速度で不成立。広い通常配置を保持'))
 outcome=plan_notes(desired,m,'hard');assert outcome is not None
 return outcome,tech

def ns_desired(sid,m,n,axis):
 # 境界の縮小時も同じ候補座標へ収束させ、累積的な座標ドリフトを避ける。
 b=n['beat'];h=n['_hand'];si,s=next(((i,s) for i,s in enumerate(FORM[sid]) if s[0]<=b<s[1]),(0,FORM[sid][0]));q=(b-s[0])%8
 if axis=='x':return (1 if h==0 else -1)*min(2.13,1.30+.58*min(1,max(0,(m.ev(b)['strength']-.3)/1.1))+.18*math.sin(math.pi*q/8)+(.12 if s[2] in ('sparse','slow','outro') else 0))
 y=float(np.interp(q,[0,2,4,6,8],CONTOURS[sid][si%len(CONTOURS[sid])]))
 return max(-1.15,min(1.15,(y if h==0 else -.75*y)+(.13 if h==0 else -.13)))

def metrics(ns,m,candidates):
 present={round(n['beat'],4) for n in ns};half=[e for e in candidates if e['subdivision']==8]
 return dict(notes=len(ns),strongHalfBeats=len(half),strongHalfBeatsCovered=sum(round(e['beat'],4) in present for e in half),
  meanOnsetDistanceMs=round(float(np.mean([abs(m.ev(n['beat'])['distanceMs']) for n in ns])),3),
  xRange=[min(n['x'] for n in ns),max(n['x'] for n in ns)],yRange=[min(n['y'] for n in ns),max(n['y'] for n in ns)],
  outerNotes=sum(abs(n['x'])>=1.65 for n in ns),highLowNotes=sum(abs(n['y'])>=.7 for n in ns),
  oppositeSideNotes=sum(n['x']*(1 if n['_hand']==0 else -1)<-.1 for n in ns),
  rhythmSignatures=len({tuple(sorted({round(n['beat']%4,3) for n in ns if int(n['beat']//4)==bar})) for bar in {int(n['beat']//4) for n in ns}}))

def main():
 p=argparse.ArgumentParser();p.add_argument('--repo',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--features',type=Path,required=True);a=p.parse_args()
 design=a.repo/'Tools/ChartDesign/FiveSongs20261014';bank=a.repo/'Tools/ChartDesign/BeatSaberPatternBank';specs=read(design/'songs.json');authorize_specs(specs)
 apps=read(bank/'applications.json');audit=read(design/'audit.json');report={'version':1,'method':'音源の発音解析に基づくHard改修。原作の正確な採譜や実機プレイ評価ではない。','scope':'許可された5曲のHardのみ','songs':{}}
 for spec in specs:
  sid=spec['id'];m=Music(spec,a.features);folder=a.repo/'Assets/StreamingAssets/Songs'/sid
  audio=next(p for p in folder.glob('audio.*') if p.suffix!='.meta');assert digest(audio)==m.analysis['sha256']
  chart=read(folder/'chart_hard.json')
  if chart.get('hardRhythmDesignVersion'):raise ValueError('改修済みHardを一括再生成しない。現在の譜面との差分を個別に設計する。')
  old=internal(chart,read(design/sid/'hard-cut-plan.json'),m,read(design/sid/'hard-evidence.json'))
  ns,rhythm=add_rhythm(copy.deepcopy(old),m);print(sid,'rhythm',len(old),'->',len(ns),flush=True)
  (ns,plan),tech=spatial(ns,m,sid)
  chart['notes']=[clean(n) for n in ns];chart['hardRhythmDesignVersion']=1;chart['_comment']='Hard実音の打点・広い高低差・短い縦と交差。hard-rhythm-design.jsonに変更根拠、applications.jsonに素材との対応を記録。'
  evidence=[dict(index=i,beat=n['beat'],hand='red' if n['_hand']==0 else 'blue',role=n['_role'],phrase=n['_phrase'],**m.ev(n['beat'])) for i,n in enumerate(ns)]
  changed_apps=[]
  for app in apps['applications']:
   if app['songId']!=sid or app['difficulty']!='hard':continue
   indices=[i for i,n in enumerate(ns) if app['startBeat']<=n['beat']<app['endBeat']];applied=[chart['notes'][i] for i in indices]
   if applied!=app['appliedNotes']:
    app.setdefault('referenceAppliedNotes',app['appliedNotes']);app['hardAdaptation']='元の素材を起点に、この曲の強い発音を追加・弱い打点を整理し、広い高低差と前後の動線へ再構成。原作の直接変換ではない。';app['hardDesignReference']='hard-rhythm-design.json';changed_apps.append(app['patternId'])
   app['noteIndices']=indices;app['appliedNotes']=applied
  save(a.output/'Assets/StreamingAssets/Songs'/sid/'chart_hard.json',chart)
  target=a.output/'Tools/ChartDesign/FiveSongs20261014'/sid;save(target/'hard-cut-plan.json',{'notes':plan});save(target/'hard-evidence.json',evidence)
  report['songs'][sid]=dict(intent=INTENT[sid],audioSha256=digest(audio),before=metrics(old,m,rhythm['candidates']),after=metrics(ns,m,rhythm['candidates']),rhythm=rhythm,techniques=tech,updatedApplications=changed_apps)
  audit[sid]['hard'].update(notes=len(ns),cuts=sum(n['count'] for n in ns),types=dict(collections.Counter(n['type'] for n in ns)),hardRhythmDesignVersion=1,flowPassed=True)
  print(sid,json.dumps(report['songs'][sid]['after']),flush=True)
 save(a.output/'Tools/ChartDesign/FiveSongs20261014/hard-rhythm-design.json',report);save(a.output/'Tools/ChartDesign/FiveSongs20261014/audit.json',audit);save(a.output/'Tools/ChartDesign/BeatSaberPatternBank/applications.json',apps)
if __name__=='__main__':main()
