"""実音のHardを主譜面に、内外4点の動線と包含する下位譜面を候補生成する。"""
import argparse,collections,copy,hashlib,json,math
from pathlib import Path
import numpy as np
from rechart import Music,FORM,limit_flicks
from build_pack import save,path_for,DIRS
from verify_pack import D
from scope_guard import authorize_specs

# ユーザーが指定した音源上の時刻。±45msの発音を検査して拍へ対応付ける。
KEYS={
 'ManicSonic':[.99,13.66,14.98,16.31,108.15,109.48,110.82,143.48],
 'escapade':[5.06,99.96,100.59,101.86,105.02],
 'logic':[1.13,11.64,30.54,54.35,105.92,107.92],
 'BenkyouRemaster':[68.99,70.30,133.23],
 'Aobasou':[82.28,109.28],
}
GOLD={'ManicSonic':[143.48],'escapade':[5.06],'logic':[105.92,107.92],'BenkyouRemaster':[133.23],'Aobasou':[82.28,109.28]}
GATE={'hard':.3,'normal':.45,'easy':.85}
DIFFS=('hard','normal','easy')
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def section(m,b):return next((i for i,s in enumerate(FORM[m.spec['id']]) if s[0]<=b<s[1]),len(FORM[m.spec['id']])-1)
def snapped(m,t):
 b=round(m.beat(t)*4)/4
 if abs(m.t(b)-t)>.045 or not m.supported(b):
  # 指定時刻と拍の四分割が一致しない音も、勝手に近い表拍へ移さない。
  ids=np.where(abs(m.times-t)<=.045)[0];i=ids[np.argmax(m.onset[ids])];b=round(m.beat(float(m.times[i])),6)
 assert m.ev(b)['strength']>=.20,(m.spec['id'],t,m.ev(b))
 return b
def load_notes(repo,m):
 sid=m.spec['id'];d=repo/'Tools/ChartDesign/FiveSongs20261014'/sid
 chart=read(repo/'Assets/StreamingAssets/Songs'/sid/'chart_hard.json');ev=read(d/'hard-evidence.json');plan=read(d/'hard-cut-plan.json')
 ns=[]
 for n,e,p in zip(chart['notes'],ev,plan['notes']):
  n=copy.deepcopy(n);n.update(_hand=0 if e['hand']=='red' else 1,_cutBeats=[m.beat(t/1000) for t in p['timesMs']],_arrow=False,_forced=None,_source=n.copy())
  n['type']='long' if n['count']==2 else 'tap';n['direction']='none';ns.append(n)
 return chart,ns
def tap(m,b,h,gold=False):
 return dict(beat=b,time=round(m.t(b)*1000,4),x=0,y=0,type='tap',color='gold' if gold else ('red' if h==0 else 'blue'),direction='none',count=1,lengthMs=0,_hand=h,_cutBeats=[b],_arrow=False,_forced=None)
def make_master(repo,m):
 sid=m.spec['id'];chart,ns=load_notes(repo,m);changes=[];keybeats=[snapped(m,t) for t in KEYS[sid]];goldbeats=[snapped(m,t) for t in GOLD[sid]]
 # 静かな導入を拍単位へ。発音がない新規の拍は作らない。
 if sid in ('ManicSonic','BenkyouRemaster'):
  end=10.7 if sid=='ManicSonic' else 12
  removed=[n for n in ns if n['time']<end*1000 and abs(n['beat']-round(n['beat']))>.01 and all(abs(n['beat']-b)>.05 for b in keybeats)]
  ns=[n for n in ns if n not in removed];changes.append({'reason':'小さい導入は整数拍の骨格へ','removed':len(removed),'untilSeconds':end})
 # 指定された細分・決めの音。発音から検証できたものだけを加える。
 extra=[]
 if sid=='escapade':extra=[61.71,62.18,62.65,63.02,63.49]
 if sid=='logic':extra=[106.42,106.92,107.42]
 if sid=='Aobasou':extra=[105.85,106.28,106.92,107.56,108.21,108.64]
 for t in extra:
  b=snapped(m,t)
  if m.ev(b)['strength']<.60:continue
  if not any(abs(n['time']/1000-m.t(b))<.065 for n in ns):ns.append(tap(m,b,0));changes.append({'addedBeat':b,'requestedSeconds':t,'reason':'指定された裏拍・決めの実音','evidence':m.ev(b)})
 # 大事な瞬間は全難易度の共通アンカー。近い単打をその一撃へまとめる。
 pairbeats=set(keybeats)
 if sid=='logic':pairbeats.add(snapped(m,106.92))
 if sid=='BenkyouRemaster':
  for b in np.arange(280,304,2):
   if m.supported(float(b)):pairbeats.add(float(b))
 # 強い既存打点を選び、両手ノーツの比率を約14%にする。連続した両手列にはしない。
 candidates=sorted({n['beat'] for n in ns if n['type']!='long'},key=lambda b:-m.ev(b)['strength'])
 target=round(len(ns)*.14/2)
 for b in candidates:
  if len(pairbeats)>=target:break
  if any(abs(m.t(b)-m.t(q))<1.15 for q in pairbeats):continue
  if any(.001<abs(n['time']/1000-m.t(b))<.095 for n in ns):continue
  if sid in ('ManicSonic','BenkyouRemaster') and m.t(b)<12:continue
  if m.ev(b)['strength']<.65:continue
  pairbeats.add(b)
 # 既存の両手は一度通常色へ整理し、共通アクセント計画にまとめる。
 groups=collections.defaultdict(list)
 for n in ns:groups[n['time']].append(n)
 ns=[g[0] if len(g)>1 else g[0] for g in groups.values()]
 for n in ns:
  if n['color']=='gold':n['color']='red' if n['_hand']==0 else 'blue'
 for b in sorted(pairbeats):
  t=m.t(b);near=[n for n in ns if abs(n['time']/1000-t)<(.16 if b in keybeats else .075)]
  ns=[n for n in ns if n not in near]
  for h in (0,1):
   n=tap(m,b,h,b in goldbeats);n.update(_pair=True,_key=b in keybeats);ns.append(n)
 # ロング占有を優先して手順を選ぶ。高速の片手列は空いている逆手へ渡す。
 ns.sort(key=lambda n:(n['time'],n['_hand']));last=[-99.,-99.];occupied=[-99.,-99.];last_h=1;run=0
 for n in ns:
  t=n['time']/1000;h=n['_hand']
  if n.get('_pair'):
   # 新しい強拍と重なるロングは開始打点を残したタップへまとめる。
   for old in ns:
    if old is n:break
    if old['_hand']==h and old['type']=='long' and old['time']/1000<t<old['time']/1000+old['lengthMs']/1000+.10:
     old.update(type='tap',count=1,lengthMs=0,_cutBeats=[old['beat']]);occupied[h]=-99
  else:
   choices=[q for q in (0,1) if t>occupied[q]+.10]
   if not choices:raise AssertionError(('両手占有',sid,t))
   if h not in choices or (t-last[h]<.27 or (h==last_h and run>=2)):
    h=max(choices,key=lambda q:(t-last[q],q!=last_h))
   if h!=n['_hand']:changes.append({'beat':n['beat'],'reason':'高速の片手連打を空いている逆手へ','fromHand':n['_hand'],'toHand':h})
   n['_hand']=h;n['color']='red' if h==0 else 'blue'
  run=run+1 if h==last_h else 1;last_h=h;last[h]=t
  if n['type']=='long':occupied[h]=t+n['lengthMs']/1000;last[h]=m.t(n['_cutBeats'][-1])
 # ロングの持ち替え後、他のノーツと重なる場合は開始の発音だけを残す。
 for n in ns:
  if n['type']=='long' and any(q['_hand']==n['_hand'] and n['time']<q['time']<=n['time']+n['lengthMs']+100 for q in ns):
   n.update(type='tap',count=1,lengthMs=0,_cutBeats=[n['beat']])
 return chart,ns,keybeats,changes

def position_path(ns,m):
 """各手の4置き場×8振り方向を探索。微小な座標乱数は使わない。"""
 for h in (0,1):
  sign=1 if h==0 else -1;palette=[(sign*x,y) for x,y in ((.6,-.6),(.6,.3),(1.9,-.6),(1.9,.3))]
  seq=[(i,k,m.t(b)) for i,n in enumerate(ns) if n['_hand']==h for k,b in enumerate(n['_cutBeats'])]
  seq.sort(key=lambda x:x[2]);states={};history=[]
  for j,(i,k,t) in enumerate(seq):
   n=ns[i];following={};sec=section(m,n['beat']);label=FORM[m.spec['id']][sec][4];phrase=int(n['beat']//16)
   # 上昇・展開を上段、応答と余韻を低くする。4小節間は同じ重心を保つ。
   upper=('展開' in label or '発展' in label or '回帰' in label);outer=any(x in label for x in ('主題','主拍','発展','BPM70','シンコペーション'))
   preferred=(2 if outer else 0)+(1 if upper else 0)
   candidates=[2,3] if n.get('_pair') else range(4)
   for p in candidates:
    xy=palette[p]
    for d in DIRS:
     v=D[d];options=[]
     if not j:following[(p,d)]=(0.0,None);continue
     pi,pk,pt=seq[j-1];prev=ns[pi];gap=t-pt
     for (pp,pd),(cost,_) in states.items():
      if k and p!=pp:continue
      delta=(xy[0]-palette[pp][0],xy[1]-palette[pp][1]);dist=math.hypot(*delta);pv=D[pd]
      if dist>1.2 and gap<GATE['hard']-1e-6:continue
      if gap<120/m.bpm(n['beat'])-1e-6 and sum(v[a]*pv[a] for a in (0,1))>.01:continue
      travel=.5+math.hypot(*(delta[a]-.25*(v[a]+pv[a]) for a in (0,1)))
      if gap<=0 or travel/gap>5.48:continue
      # 動きの目安は長い移動と低高往復の両方。速い連打は同じ列で処理する。
      want_big=(phrase+int(n['beat'])+h)%5<4 and gap>=.3
      mismatch=0 if (dist>1.2)==want_big else .8
      score=cost+mismatch+(.12 if p!=preferred else 0)+(.18 if p==pp and not k else 0)+travel*.035
      options.append((score,(pp,pd)))
     if options:following[(p,d)]=min(options,key=lambda z:z[0])
   if not following:raise AssertionError(('4点経路が不成立',m.spec['id'],h,i,t,ns[i]))
   history.append(following);states=following
  state=min(states,key=lambda s:states[s][0])
  for j in range(len(seq)-1,-1,-1):
   i,k,t=seq[j];p,d=state;ns[i]['x'],ns[i]['y']=palette[p];state=history[j][state][1]
 return ns

def reduced(ns,m,diff,keybeats,movement_penalty=None):
 """主譜面の打点・担当手・位置を残して間引く。強い裏拍は表拍より優先できる。"""
 # 曲別に確定した応答と休符を、平均間隔へ引き戻さない。
 rhythm_policy=Path(__file__).with_name('rhythm-contrast-design.json')
 if rhythm_policy.exists():
  from rhythm_selection import select_retained
  return select_retained(ns,m,diff,read(rhythm_policy))
 if movement_penalty is None:
  candidates=[]
  for penalty in (0,.025,.05,.075,.10,.15,.25):
   result=reduced(ns,m,diff,keybeats,penalty);distances=[]
   for h in (0,1):
    seq=[n for n in result if n['_hand']==h]
    distances += [math.dist((a['x'],a['y']),(b['x'],b['y'])) for a,b in zip(seq,seq[1:]) if b['time']-a['time']<=1000]
   ratio=sum(d>1.2 for d in distances)/max(1,len(distances));target=.25 if diff=='easy' else .45
   candidates.append((abs(ratio-target),penalty,result))
  return min(candidates,key=lambda q:q[:2])[2]
 sid=m.spec['id'];selected=[];groups=collections.defaultdict(list)
 for n in ns:groups[n['time']].append(n)
 # 全難易度共通の一撃と各手最低1本の長音を必須にする。
 required={id(n) for n in ns if n.get('_key')}
 old_count={'ManicSonic':(190,316),'escapade':(157,257),'logic':(213,352),'BenkyouRemaster':(146,253),'Aobasou':(163,286)}[sid][0 if diff=='easy' else 1]
 pair_target=max(sum(any(n.get('_key') for n in g) for g in groups.values()),round(old_count*(.07 if diff=='easy' else .10)/2))
 forced_times=[t for t,g in groups.items() if any(n.get('_key') for n in g)]
 for t,g in sorted(groups.items(),key=lambda item:-m.ev(item[1][0]['beat'])['strength']):
  if len(forced_times)>=pair_target:break
  if len(g)!=2 or any(n.get('_technique') for n in g) or any(abs(t-q)<(6500 if diff=='easy' else 3500) for q in forced_times):continue
  if sid=='logic' and abs(t/1000-106.92)<.1:continue
  forced_times.append(t);required.update(id(n) for n in g)
 for h in (0,1):
  longs=[n for n in ns if n['_hand']==h and n['type']=='long' and all(abs(n['time']-q)>1100 and abs(n['time']+n['lengthMs']-q)>1100 for q in forced_times)]
  if longs:required.add(id(max(longs,key=lambda n:m.ev(n['beat'])['strength'])))
 for h in (0,1):
  seq=[n for n in ns if n['_hand']==h];req=[i for i,n in enumerate(seq) if id(n) in required]
  # 区間ごとに音量と曲の役割に応じた密度を与える。
  def interval(n):
   t=n['time']/1000;label=FORM[sid][section(m,n['beat'])][4];base=1.6 if diff=='easy' else 1.0
   if sid=='ManicSonic' and t<10.7:base=1.5 if diff=='easy' else 1.30
   if sid=='BenkyouRemaster' and t<12:base=1.55 if diff=='easy' else 1.35
   if sid=='escapade' and (45.5<=t<50.5 or 75.8<=t<85.9):base*=1.9
   if sid=='escapade' and 65.7<=t<75.8:base*=.82
   if sid=='logic' and 12<=t<30:base*=1.35
   if sid=='logic' and 70.4<=t<94.4:base*=.78
   if sid=='Aobasou' and 13.7<=t<27.4:base*=1.35
   if sid=='Aobasou' and 104<=t<111:base*=.8
   if sid=='BenkyouRemaster' and 112.7<=t<133.3:base*=.83
   return base
  # 必須ノーツを飛び越えない最適部分列。経路と大移動の待ち時間を両方満たす。
  states={-1:(0,[])}
  for i,n in enumerate(seq):
   t=n['time']/1000;options=[]
   if diff=='easy' and ((h==0 and n['x']<0) or (h==1 and n['x']>0)):continue
   # 歌の前の一小節は下位譜面の休み。指定された一撃は残す。
   if sid=='Aobasou' and diff in ('easy','normal') and any(a<=t<a+1.714 for a in (53.14,73.71)) and id(n) not in required:continue
   for j,(cost,path) in states.items():
    if any(j<q<i for q in req):continue
    if j<0:gap=interval(n);dist=0
    else:
     prev=seq[j];gap=(n['time']-(prev['time']+prev['lengthMs']))/1000;dist=math.dist((n['x'],n['y']),(prev['x'],prev['y']))
     if gap<=.10:continue
     if dist>1.2 and gap<GATE[diff]-1e-5:continue
     if gap<(.43 if diff=='easy' else .26):continue
     if (.5+dist)/gap>5.4:continue
    target=interval(n);strength=m.ev(n['beat'])['strength']
    # 密度だけでなく、指定音と強い裏拍を優先する。
    penalty=((gap-target)/target)**2-.15-min(strength,2)*.06
    if gap<=1 and dist>1.2:penalty+=movement_penalty
    if len(groups[n['time']])==2 and not n.get('_key'):penalty+=.60 if diff=='easy' else .22
    options.append((cost+penalty,path+[i]))
   if options:states[i]=min(options,key=lambda z:z[0])
  tail=[v for i,v in states.items() if i>=max(req,default=-1) and i>=len(seq)-10]
  assert tail,('下位包含経路不成立',sid,diff,h,req)
  chosen=min(tail,key=lambda z:z[0])[1];selected.extend(copy.deepcopy(seq[i]) for i in chosen)
 selected.sort(key=lambda n:(n['time'],n['_hand']))
 # 金は片側だけを残せない。共通金アンカーは必須なので通常は両側そろう。
 for n in selected:
  if n['color']=='gold':assert sum(q['time']==n['time'] for q in selected)==2
 return selected

def short_techniques(ns,m):
 """間のあるフレーズに一度だけ技を試し、前後の経路が通る場合だけ残す。"""
 if m.spec['id'] in ('escapade','logic'):return []
 report=[];groups=collections.defaultdict(list)
 for n in ns:groups[n['time']].append(n)
 mode='vertical' if m.spec['id'] in ('ManicSonic','Aobasou') else 'cross'
 for t,g in groups.items():
  if t<30000 or any(n.get('_key') or n['type']=='long' for n in g):continue
  if mode=='vertical' and len(g)!=2:continue
  if mode=='cross' and len(g)!=1:continue
  prior=[(n,n['x'],n['y']) for n in g]
  for n in g:
   if mode=='vertical':n['x']=0;n['y']=.95 if n['_hand']==0 else -.95
   else:n['x']=-.6 if n['_hand']==0 else .6;n['y']=.3
  valid=True
  for n in g:
   seq=[q for q in ns if q['_hand']==n['_hand']];i=seq.index(n)
   for a,b in ([(seq[i-1],n)] if i else [])+([(n,seq[i+1])] if i+1<len(seq) else []):
    gap=(b['time']-a['time']-a['lengthMs'])/1000
    if math.dist((a['x'],a['y']),(b['x'],b['y']))>1.2 and gap<.3:valid=False
  if valid:
   _,failure=path_for(ns,m);valid=failure is None
  if valid:
   for n in g:n['_technique']=mode
   report.append({'seconds':t/1000,'type':mode,'notes':len(g),'reason':'十分な前後間隔を持つ一度の技。実機追跡は未確認。'});break
  for n,x,y in prior:n['x']=x;n['y']=y
 return report

def direction_training_candidate(ns,source,preserve_hits):
 """方向練習のために確定済み休符を埋めず、上位の方向を固定する。"""
 candidate=copy.deepcopy(ns)
 existing=next((n for n in candidate if n['time']==source['time'] and n['_hand']==source['_hand']),None)
 if existing is None:
  if preserve_hits:return None
  existing=copy.deepcopy(source);candidate.append(existing)
 existing.update(_arrow=True,_forced=source['direction'])
 return candidate

def finalize(ns,m,diff,metadata,upper=None):
 for n in ns:n['_arrow']=False;n['_forced']=None;n['direction']='none';n['type']='long' if n['count']==2 else 'tap'
 # フリックは少数の強い区切りだけ。下位は同じ座標で方向指定を簡略化できる。
 if diff=='normal' and upper is not None:
  for n in ns:
   source=next(q for q in upper if q['time']==n['time'] and q['_hand']==n['_hand'])
   n['_arrow']=source['_arrow'];n['_forced']=source['direction'] if source['_arrow'] else None
 elif diff!='easy':
  last=-999
  for n in ns:
   if not n.get('_pair') and n['type']=='tap' and n['time']-last>(12000 if diff=='normal' else 6000) and m.ev(n['beat'])['strength']>=.8:
    n['_arrow']=True;last=n['time']
 limit_flicks(ns,diff)
 while True:
  plan,failure=path_for(ns,m)
  if failure is None:break
  i=failure[0]
  if ns[i]['_arrow']:ns[i]['_arrow']=False
  else:
   prior=[n for n in ns[:i] if n['_hand']==ns[i]['_hand'] and n['_arrow']]
   if prior:prior[-1]['_arrow']=False
   else:raise AssertionError(('確定経路不成立',m.spec['id'],diff,failure))
 if diff=='normal' and upper is not None and not any(n['_arrow'] for n in ns):
  # 方向指定の練習を最低一つ残す。Hardの既存打点を同じ手・位置・方向で採用する。
  for source in upper:
   if not source['_arrow'] or source.get('_pair'):continue
   candidate=direction_training_candidate(ns,source,(Path(__file__).parent/'rhythm-contrast-design.json').exists())
   if candidate is None:continue
   candidate.sort(key=lambda n:(n['time'],n['_hand']));valid=True
   seq=[n for n in candidate if n['_hand']==source['_hand']]
   for a,b in zip(seq,seq[1:]):
    gap=(b['time']-a['time']-a['lengthMs'])/1000;dist=math.dist((a['x'],a['y']),(b['x'],b['y']))
    if gap<=.10 or (dist>1.2 and gap<.45):valid=False
   if not valid:continue
   possible,failure=path_for(candidate,m)
   if failure is None:ns[:]=candidate;plan=possible;break
  assert any(n['_arrow'] for n in ns),('Normalの方向練習を選び直す',m.spec['id'])
 for i,n in enumerate(ns):
  if n['_arrow']:n['direction']=plan[i]['directions'][0];n['type']='direction'
 chart=copy.deepcopy(metadata);chart['notes']=[{k:v for k,v in n.items() if not k.startswith('_')} for n in ns]
 chart['hierarchyDesignVersion']=1;chart['_comment']='Hard主譜面から下位へ打点・手・位置を包含。4置き場と共通アクセント。hierarchy-design.json参照。'
 chart['displayLevel']={'ManicSonic':(3,6,10),'escapade':(3,6,10),'logic':(3,6,9),'BenkyouRemaster':(3,6,9),'Aobasou':(3,6,10)}[m.spec['id']][('easy','normal','hard').index(diff)]
 ev=[dict(index=i,beat=n['beat'],hand='red' if n['_hand']==0 else 'blue',role=FORM[m.spec['id']][section(m,n['beat'])][4],phrase=int(n['beat']//16),sharedAccent=n.get('_key',False),**m.ev(n['beat'])) for i,n in enumerate(ns)]
 return chart,dict(notes=plan),ev

def main():
 p=argparse.ArgumentParser();p.add_argument('--repo',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--features',type=Path,required=True);args=p.parse_args();design=Path(__file__).parent
 specs=read(design/'songs.json');authorize_specs(specs);report={'version':1,'movementDefinition':'同じ担当手の1秒以内の中心間距離>1.2。判定領域の非重複や実機での運動を保証する指標ではない。','reachTest':'未実施。2.7〜3.1mでの到達とカメラ追跡は利用者の試遊が必要。','songs':{}}
 for spec in specs:
  m=Music(spec,args.features);meta,hard,keys,changes=make_master(args.repo,m);position_path(hard,m);techniques=short_techniques(hard,m)
  normal=reduced(hard,m,'normal',keys);easy=reduced(normal,m,'easy',keys)
  result={'sharedAccents':[{'beat':b,'seconds':m.t(b),**m.ev(b)} for b in keys],'changes':changes,'shortTechniques':techniques,'charts':{}}
  for diff,ns in [('hard',hard),('normal',normal),('easy',easy)]:
   metadata=read(args.repo/'Assets/StreamingAssets/Songs'/spec['id']/f'chart_{diff}.json')
   chart,plan,ev=finalize(ns,m,diff,metadata,hard if diff=='normal' else None);target=args.output/'Assets/StreamingAssets/Songs'/spec['id'];save(target/f'chart_{diff}.json',chart)
   if diff=='normal':save(target/'chart.json',chart)
   d=args.output/'Tools/ChartDesign/FiveSongs20261014'/spec['id'];save(d/f'{diff}-cut-plan.json',plan);save(d/f'{diff}-evidence.json',ev)
   result['charts'][diff]={'notes':len(ns),'source':'Hard' if diff=='normal' else 'Normal' if diff=='easy' else '実音に合わせた既存Hardと指定アクセント'}
   print(spec['id'],diff,len(ns),flush=True)
  report['songs'][spec['id']]=result
 save(args.output/'Tools/ChartDesign/FiveSongs20261014/hierarchy-design.json',report)
if __name__=='__main__':main()
