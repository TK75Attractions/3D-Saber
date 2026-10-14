"""既存の素材・長音を残し、5曲の通常フレーズを音源に合わせて作り分ける。"""
import argparse,collections,copy,json,math,sys
from pathlib import Path
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE.parent/'BeatSaberPatternBank'))
from scope_guard import authorize_specs,protected_repo,assert_unchanged
from integrate_patterns import internal,plan_notes,read,digest
from rechart import Music,FORM,DIFFS
from build_pack import save

# 2小節の音楽的な文。機械的な左右反転で応答を作らず、休符と音の強弱を変える。
# R/Lは担当手であり、画面の位置ではない。Bは両手。
PHRASES={
 'ManicSonic':[
  ('R0 L2 R4 L6','R0 L1 R2 L4 R5 L6','R0 L.5 R1 L2 R2.5 L3 B4 R5 L5.5 R6 L7'),
  ('R0 L2 L4 R6','R0 L1 B2 R4 L5 R6','R0 L1 R1.5 L2 R3 L3.5 B4 L5 R6 L6.5 R7'),
  ('R0 L3 R4 L6','B0 R2 L3 R4 L5 R7','B0 R1 L1.5 R2 L3 R4 L4.5 R5 L6 R6.5 L7'),
  ('L0 R2 L4 R7','L0 R1 L3 R4 L6 R7','L0 R.5 L1 R2 L3 R4 L5 R5.5 L6 R7')],
 'escapade':[
  ('R0 R2 L4 L6','R0 R1 L2.5 L4 R5.5 L6.5','R0 R.5 L1.5 R2 L3.5 L4 R5 L5.5 R6.5 L7'),
  ('L0 R3 L4 R7','L0 R1.5 R2.5 L4 L5 R6.5','L0 R.5 R1.5 L2.5 R3 L4 R4.5 L5.5 R6 L7.5'),
  ('R0 L2.5 R4 L6.5','R0 L.5 R2.5 L4 R4.5 L6.5','R0 L.5 R1.5 L2.5 R3.5 L4 R4.5 L5.5 R6.5 L7'),
  ('L0 L2 R4 R6','L0 L1.5 R3 R4 L5.5 R7','L0 L.5 R1.5 L2.5 R3.5 R4 L5 R5.5 L6.5 R7.5')],
 'logic':[
  ('R.5 L2 R4.5 L6','R.5 R2 L3 L4.5 R6 L7','R0 L.5 R1.5 R2.5 L3.5 L4.5 R5 R6 L7 L7.5'),
  ('R0 R2 L4 L6.5','R0 R1.5 L3 L4 R5.5 L7','R0 R.5 L1.5 R2.5 L3 L4 R4.5 L5.5 R6.5 L7'),
  ('L.5 R3 L4.5 R7','L.5 R2 R3 L4.5 L6 R7','L.5 R1 R2 L2.5 R3.5 L4.5 L5.5 R6 L7 L7.5'),
  ('L0 R1.5 L4 R5.5','L0 R1.5 L2.5 R4 L5.5 R6.5','L0 R.5 L1.5 R2.5 L3.5 R4 L5 L5.5 R6.5 R7.5')],
 'BenkyouRemaster':[
  ('B0 L3 R6','B0 L2 R3 L4 R5 L7','B0 R1 L1.5 R2 L2.5 R3 B4 L5 R5.5 L6 R6.5 L7'),
  ('L0 R2 B4','L0 R1 L2 B4 L6 R7','L0 R.5 L1 R1.5 L2 R3 B4 L5 R6 L6.5 R7'),
  ('R0 L2 R4 L7','R0 L1.5 R3 L4 R5.5 L7','R0 L1 R1.5 L2 R2.5 L3 R4 L4.5 R5 L6 R7'),
  ('L0 R3 L4 R6','L0 R1 L3 R4 L5 R7','L0 R.5 L1.5 R2 L3 R4 L4.5 R5.5 L6 R7')],
 'Aobasou':[
  ('L0 R1.5 L4 R6','L0 R1.5 L2.5 R4 L5.5 R7','L0 R1 L1.5 R2.5 L3.5 R4 L5 R5.5 L6.5 R7.5'),
  ('R0 L2 R4 L7','R0 R1.5 L3 L4 R5.5 L7','R0 L.5 R1.5 L2.5 R3 L4 R5 L5.5 R6.5 L7'),
  ('L0 R3 L4 R6.5','L0 R2 L3 R4 L6 R7.5','L0 R1 L2 R2.5 L3.5 R4 L4.5 R5.5 L6 R7.5'),
  ('R0 L1.5 R4 L6.5','R0 L1.5 R3 L4 R6 L7.5','R0 L.5 R1.5 L2 R3 L4 R4.5 L5.5 R6.5 L7.5')]
}
SLOW_BENKYOU=[
 ('R0 L3 R6','R0 L2 R3 L4 R6 L7','R0 L1 R2 L2.5 R3.5 L4 R5 L6 R6.5 L7.5'),
 ('L0 R2 L5','L0 R1.5 L3 R4.5 L6 R7','L0 R.5 L1.5 R2.5 L3 R4 L5 R5.5 L6.5 R7.5')]
INTENT={
 'ManicSonic':'主拍の推進力と両手の着地。半拍交互は短くまとめ、主題の反復は残す。',
 'escapade':'短い同手2打・裏拍・休符の跳ね。190 BPMでも動線を小さくして、一定の交互連打にしない。',
 'logic':'3+3+2のアクセントと非対称な手の応答。8/8と変速の区切りを保持。',
 'BenkyouRemaster':'160 BPMの短い駆け上がりと100 BPMの間を置く応答を対比。70/360 BPMの特殊な終止は保持。',
 'Aobasou':'歌の発音に沿う1.5拍の受け渡しと息継ぎ。広めの横方向の弧をゆっくり戻す。'}
REFERENCES={
 'ManicSonic':['crystal-normal-1','crystal-normal-2','dream-hard-1'],
 'escapade':['misa-expertplus-1','misa-expertplus-2','dream-normal-1'],
 'logic':['bring-it-on-1','bring-it-on-3','lvl-expert-1'],
 'BenkyouRemaster':['bring-it-on-2','bring-it-on-3','lvl-expert-1'],
 'Aobasou':['fire-normal-1','fire-normal-2','burning-hard-1']}

def pose(sid,hand,j,level,variant):
 sign=1 if hand==0 else -1
 paths={
  'ManicSonic':[(.98,-.40),(1.15,.12),(1.32,.42),(1.10,-.08)],
  'escapade':[(1.18,-.26),(.96,.10),(1.12,.28),(.94,-.12)],
  'logic':[(.94,.22),(1.24,.22),(1.24,-.28),(.94,-.28)],
  'BenkyouRemaster':[(1.28,-.38),(1.12,-.10),(.96,.28),(1.14,.48)],
  'Aobasou':[(1.45,-.24),(1.20,.30),(.94,.42),(1.16,-.08)]}
 # 左右同時に上下を反転させず、曲ごとの運動の軸を保つ。
 x,y=paths[sid][(j+variant+hand)%4]
 if level==0:y*=.65;x=1.05+(x-1.05)*.65
 return round(sign*x,4),round(y,4)

def create_phrase(sid,diff,base,variant,m,slow=False,offset=0):
 level=DIFFS.index(diff);family=SLOW_BENKYOU if slow else PHRASES[sid]
 tokens=family[variant%len(family)][level].split();notes=[];seq=[0,0];wanted=set()
 for token in tokens:
  local=float(token[1:])
  if not offset<=local<offset+4:continue
  b=base+local-offset;wanted.add(b)
  if not m.supported(b):continue
  for h in ((0,1) if token[0]=='B' else (0,) if token[0]=='R' else (1,)):
   x,y=pose(sid,h,seq[h],level,variant);seq[h]+=1
   notes.append(dict(beat=b,time=round(m.t(b)*1000,4),x=x,y=y,type='tap',color='red' if h==0 else 'blue',direction='none',count=1,lengthMs=0,
    _hand=h,_cutBeats=[b],_arrow=False,_forced=None,_role='曲別: '+INTENT[sid],_phrase=int(base//16),_material='identity:'+sid))
 supported={n['beat'] for n in notes}
 return notes,len(supported)/len(wanted)

def analyze_identity(charts):
 hist={};metrics={}
 for (sid,diff),chart in charts.items():
  ns=chart['notes'];groups=collections.defaultdict(list)
  for n in ns:groups[int(n['beat']//4)].append(n)
  key=sid+':'+diff;hist[key]={}
  for kind in ['rhythm','hands','positions']:
   counts=collections.Counter()
   for group in groups.values():
    sig=[]
    for n in group:
     row=[round(n['beat']%4,3)]
     if kind!='rhythm':row.append(n['color'])
     if kind=='positions':row.extend([round(n['x'],1),round(n['y'],1)])
     sig.append(tuple(row))
    counts[tuple(sorted(sig))]+=1
   hist[key][kind]=counts
  single=[g[0] for g in groups_by_time(ns).values() if len(g)==1 and g[0]['color']!='gold']
  transitions=[(a,b) for a,b in zip(single,single[1:]) if b['time']-a['time']<1500]
  metrics[key]={'notes':len(ns),'sameHandTransitionPercent':round(100*sum(a['color']==b['color'] for a,b in transitions)/max(1,len(transitions)),2),
   'offbeatPercent':round(100*sum(abs(n['beat']-round(n['beat']))>.01 for n in ns)/len(ns),2),'uniqueRhythms':len(hist[key]['rhythm']),'uniqueHandPhrases':len(hist[key]['hands'])}
 similarities={}
 for diff in DIFFS:
  rows=[]
  ids=list(PHRASES)
  for i,a in enumerate(ids):
   for b in ids[i+1:]:
    row={'songs':[a,b]}
    for kind in ['rhythm','hands','positions']:
     ca,cb=hist[a+':'+diff][kind],hist[b+':'+diff][kind];ta,tb=sum(ca.values()),sum(cb.values())
     row[kind]=round(sum(min(ca[k]/ta,cb[k]/tb) for k in ca.keys()|cb.keys()),4)
    rows.append(row)
  similarities[diff]={'mean':{kind:round(sum(r[kind] for r in rows)/len(rows),4) for kind in ['rhythm','hands','positions']},'pairs':rows}
 return {'metrics':metrics,'similarities':similarities,'method':'非空4拍区間の拍列／拍+色／拍+色+座標(0.1単位)の頻度分布が重なる割合。0=一致なし、1=全て同型。曲の良さを保証する指標ではない。'}

def groups_by_time(notes):
 result=collections.defaultdict(list)
 for n in notes:result[n['time']].append(n)
 return result

def main():
 p=argparse.ArgumentParser();p.add_argument('--repo',type=Path,required=True);p.add_argument('--features',type=Path,required=True);p.add_argument('--output',type=Path,required=True);args=p.parse_args()
 repo=args.repo;design=repo/'Tools/ChartDesign/FiveSongs20261014';bank=repo/'Tools/ChartDesign/BeatSaberPatternBank'
 specs=read(design/'songs.json');authorize_specs(specs);protected=protected_repo(repo)
 appdata=read(bank/'applications.json');apps=appdata['applications'];audit=read(design/'audit.json');before={};after={};changes=[];rejected=[]
 for spec in specs:
  sid=spec['id'];m=Music(spec,args.features)
  audio=next(p for p in (repo/'Assets/StreamingAssets/Songs'/sid).glob('audio.*') if p.suffix!='.meta');assert digest(audio)==m.analysis['sha256']
  for diff in DIFFS:
   chart=read(repo/'Assets/StreamingAssets/Songs'/sid/f'chart_{diff}.json');before[(sid,diff)]=copy.deepcopy(chart)
   notes=internal(chart,read(design/sid/f'{diff}-cut-plan.json'),m,read(design/sid/f'{diff}-evidence.json'))
   current=[a for a in apps if a['songId']==sid and a['difficulty']==diff]
   # 適用済み素材は境界を含めて保持。長音と金の前後にも余白を残す。
   blocked=[(a['startBeat'],a['endBeat']) for a in current]
   blocked.extend((n['beat']-.5,max(n['_cutBeats'])+1) for n in notes if n['type']=='long' or n['color']=='gold')
   count=0
   for si,(start,end,key,motion,label) in enumerate(FORM[sid]):
    if key in ('rest','doubletime','finish') or m.bpm(start)<90:continue
    for base in range(math.ceil(start/4)*4,int(min(end,m.last_beat))-3,4):
     if any(base<b and base+4>a for a,b in blocked) or any(base<b<base+4 for b,_,_ in m.points):continue
     old=[n for n in notes if base<=n['beat']<base+4]
     if not old:continue
     variant=int((base-start)//8)+si
     offset=((base-math.ceil(start/4)*4)//4%2)*4
     phrase,support=create_phrase(sid,diff,base,variant,m,sid=='BenkyouRemaster' and m.bpm(base)==100,offset)
     if support<.75 or len(phrase)<1:continue
     combined=[n for n in notes if not base<=n['beat']<base+4]+phrase
     outcome=plan_notes(combined,m,diff)
     if outcome is None:
      rejected.append({'songId':sid,'difficulty':diff,'startBeat':base,'reason':'前後の動作または既存フリック密度に不適合'});continue
     notes,_=outcome;count+=1
     changes.append({'songId':sid,'difficulty':diff,'startBeat':base,'endBeat':base+4,'startBar':base//4+1,'startSeconds':round(m.t(base),4),'targetBpm':m.bpm(base),'section':label,'design':INTENT[sid],'referencePatterns':REFERENCES[sid],'variant':variant%4,'removedNotes':len(old),'addedNotes':len(phrase),'onsetSupport':support})
   outcome=plan_notes(notes,m,diff);assert outcome is not None;notes,plan=outcome
   chart['notes']=[{k:v for k,v in n.items() if not k.startswith('_')} for n in notes]
   chart['_comment']='曲別のリズム・手順・運動を設計。素材の適用はapplications.json、通常フレーズの変更はidentity-design.json参照。'
   chart['identityDesignVersion']=1
   evidence=[dict(index=i,beat=n['beat'],hand='red' if n['_hand']==0 else 'blue',role=n['_role'],phrase=n['_phrase'],**m.ev(n['beat'])) for i,n in enumerate(notes)]
   for a in current:
    indices=[i for i,n in enumerate(notes) if a['startBeat']<=n['beat']<a['endBeat']]
    applied=[chart['notes'][i] for i in indices];assert applied==a['appliedNotes'],('既存素材変更',a['patternId'])
    a['noteIndices']=indices
   target=args.output/'Assets/StreamingAssets/Songs'/sid;save(target/f'chart_{diff}.json',chart)
   if diff=='normal':save(target/'chart.json',chart)
   target=args.output/'Tools/ChartDesign/FiveSongs20261014'/sid;save(target/f'{diff}-cut-plan.json',{'notes':plan});save(target/f'{diff}-evidence.json',evidence)
   audit[sid][diff].update(notes=len(notes),cuts=sum(n['count'] for n in notes),types=dict(collections.Counter(n['type'] for n in notes)),identityPhrases=count,flowPassed=True)
   after[(sid,diff)]=chart;print(sid,diff,'phrases',count,'notes',len(before[(sid,diff)]['notes']),'->',len(notes),flush=True)
 save(args.output/'Tools/ChartDesign/BeatSaberPatternBank/applications.json',appdata)
 save(args.output/'Tools/ChartDesign/FiveSongs20261014/audit.json',audit)
 save(args.output/'Tools/ChartDesign/FiveSongs20261014/identity-design.json',{'version':1,'intent':INTENT,'changes':changes,'rejected':rejected,'before':analyze_identity(before),'after':analyze_identity(after),'preservedApplications':len(apps)})
 assert_unchanged(protected,protected_repo(repo))
if __name__=='__main__':main()
