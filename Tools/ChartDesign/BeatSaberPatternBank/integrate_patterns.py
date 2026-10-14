"""音源の発音と可動域を満たす小節を選び、素材を2小節の応答として組み込む。"""
import argparse,collections,copy,hashlib,json,math,sys
from pathlib import Path
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE.parent/'FiveSongs20261014'))
from rechart import Music,FORM,DIFFS,limit_flicks
from analyze import analyze
from build_pack import path_for,save

def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def digest(p):return hashlib.sha256(p.read_bytes()).hexdigest()

def internal(chart,plan,m,evidence):
 result=[]
 for i,old in enumerate(chart['notes']):
  n=copy.deepcopy(old);n.update(_hand=0 if n['x']>0 else 1,_cutBeats=[m.beat(t/1000) for t in plan['notes'][i]['timesMs']],
   _arrow=n['direction']!='none',_forced=n['direction'] if n['direction']!='none' else None,_role=evidence[i]['role'],_phrase=evidence[i]['phrase'],_material=None)
  result.append(n)
 return result

def make_phrase(pattern,base,m,mirror,diff):
 result=[]
 for repeat in range(2):
  for old in pattern['chart']['notes']:
   n=copy.deepcopy(old);n['beat']=base+repeat*4+old['beat'];n['time']=round(m.t(n['beat'])*1000,4)
   if mirror^bool(repeat):
    n['x']=-n['x'];n['color']='red' if n['x']>0 else 'blue'
   n['type']='tap';n['direction']='none'
   # アクセントは2小節の最後の一つ。Easyは方向指定を作らない。
   arrow=diff!='easy' and repeat==1 and old is pattern['chart']['notes'][-1] and pattern['flickCount']>0
   n.update(_hand=0 if n['x']>0 else 1,_cutBeats=[n['beat']],_arrow=arrow,_forced=None,_role='素材: '+pattern['title'],_phrase=-1,_material=pattern['id'])
   result.append(n)
 return result

def plan_notes(notes,m,diff):
 notes=copy.deepcopy(sorted(notes,key=lambda n:(n['time'],n['_hand'])))
 limit_flicks(notes,diff)
 plan,failure=path_for(notes,m)
 # 素材の句末フリックは、前後の動きが成立しなければタップへ戻す。
 if failure and notes[failure[0]]['_material'] and notes[failure[0]]['_arrow']:
  notes[failure[0]]['_arrow']=False;plan,failure=path_for(notes,m)
 if failure:return None
 for i,n in enumerate(notes):
  if n['type']!='long':n['type']='direction' if n['_arrow'] else 'tap'
  n['direction']=plan[i]['directions'][0] if n['_arrow'] else 'none'
 return notes,plan

def main():
 p=argparse.ArgumentParser();p.add_argument('--repo',type=Path,required=True);p.add_argument('--output',type=Path,required=True);args=p.parse_args()
 repo=args.repo;design=repo/'Tools/ChartDesign/FiveSongs20261014';bank=repo/'Tools/ChartDesign/BeatSaberPatternBank';output=args.output
 patterns=read(bank/'converted-patterns.json')['patterns'];prior=read(bank/'applications.json') if (bank/'applications.json').exists() else {'applications':[]}
 done={a['patternId'] for a in prior['applications']};pending=[x for x in patterns if x['id'] not in done]
 states={};musics={};original_hashes={};occupancy=collections.defaultdict(list);applications=list(prior['applications'])
 for a in applications:occupancy[(a['songId'],a['difficulty'])].append((a['startBeat'],a['endBeat']))
 features=output/'Features'
 for spec in read(design/'songs.json'):
  sid=spec['id'];folder=repo/'Assets/StreamingAssets/Songs'/sid;audio=next(p for p in folder.glob('audio.*') if p.suffix!='.meta')
  if not (features/sid/'features.npz').exists():analyze(spec,audio,features/sid)
  m=Music(spec,features);musics[sid]=m;assert digest(audio)==m.analysis['sha256']
  for diff in DIFFS:
   path=folder/f'chart_{diff}.json';chart=read(path);original_hashes[str(path.relative_to(repo)).replace('\\','/')]=digest(path)
   notes=internal(chart,read(design/sid/f'{diff}-cut-plan.json'),m,read(design/sid/f'{diff}-evidence.json'))
   states[(sid,diff)]={'chart':chart,'notes':notes,'m':m}
 # 難しい素材を先に割り当て、低難度の素材を狭い空き区間へ押し込まない。
 pending.sort(key=lambda p:(-DIFFS.index(p['minimumDifficulty']),-p['noteCount'],p['id']))
 for pattern in pending:
  candidates=[];minimum=DIFFS.index(pattern['minimumDifficulty'])
  for (sid,diff),state in states.items():
   level=DIFFS.index(diff)
   if level<minimum:continue
   m=state['m'];ns=state['notes']
   for section,(start,end,rhythm,motion,label) in enumerate(FORM[sid]):
    if rhythm in ('rest','doubletime','finish') or end-start<12:continue
    for base in range(max(8,math.ceil(start/4)*4),int(min(end-8,m.last_beat-8))+1,4):
     if any(base<a[1]+4 and base+8>a[0]-4 for a in occupancy[(sid,diff)]):continue
     if any(base<b<base+8 for b,t,bpm in m.points):continue
     beats=[base+repeat*4+n['beat'] for repeat in range(2) for n in pattern['chart']['notes']]
     if not all(m.supported(b) for b in set(beats)):continue
     t0,t1=m.t(base)*1000,m.t(base+8)*1000
     if any(n['type']=='long' and n['time']<t1+400 and n['time']+n['lengthMs']>t0-400 for n in ns):continue
     old=[n for n in ns if base<=n['beat']<base+8]
     if not old:continue
     bpm=m.bpm(base)
     if abs(math.log2(bpm/pattern['referenceBpm']))>.60:continue
     strength=sum(min(m.ev(b)['strength'],2) for b in set(beats))/len(set(beats))
     score=abs(math.log2(bpm/pattern['referenceBpm']))*8+(level-minimum)*5+len(occupancy[(sid,diff)])*3
     score+=abs(len(old)-len(beats))*.15+(0 if base%16==0 else .2)-strength*.5
     if len(beats)<=4 and rhythm not in ('sparse','slow','outro'):score+=1.5
     candidates.append((score,sid,diff,base,section,label,len(old)))
  candidates.sort();accepted=None
  for score,sid,diff,base,section,label,removed in candidates:
   state=states[(sid,diff)];m=state['m']
   for mirror in (False,True):
    phrase=make_phrase(pattern,base,m,mirror,diff)
    combined=[n for n in state['notes'] if not base<=n['beat']<base+8]+phrase
    outcome=plan_notes(combined,m,diff)
    if outcome:
     accepted=(outcome,sid,diff,base,mirror,section,label,removed);break
   if accepted:break
  if not accepted:raise AssertionError(('配置できる音楽・動作条件がない',pattern['id'],len(candidates)))
  (notes,plan),sid,diff,base,mirror,section,label,removed=accepted
  states[(sid,diff)]['notes']=notes;occupancy[(sid,diff)].append((base,base+8));m=musics[sid]
  application={'patternId':pattern['id'],'songId':sid,'songTitle':m.spec['title'],'difficulty':diff,'startBeat':base,'endBeat':base+8,
   'startBar':base//4+1,'barCount':2,'startSeconds':round(m.t(base),4),'endSeconds':round(m.t(base+8),4),'targetBpm':m.bpm(base),
   'mirrorFirstBar':mirror,'secondBar':'左右を入れ替えた応答','section':label,'removedNotes':removed,'addedNotes':pattern['noteCount']*2,
   'sourceCategory':pattern['category'],'fidelity':pattern['fidelity'],'sourceUrl':pattern['sourceUrl'],
   'viewerUrl':f'https://saber-chart-library.metuji48.chatgpt.site/?song={sid}&difficulty={diff}&t={max(0,m.t(base)-1.5):.2f}'}
  applications.append(application);print(pattern['id'],'->',sid,diff,'bar',base//4+1,flush=True)
 assert {p['id'] for p in patterns}<={a['patternId'] for a in applications}
 summaries=read(design/'audit.json')
 for (sid,diff),state in states.items():
  m=state['m'];outcome=plan_notes(state['notes'],m,diff);assert outcome is not None
  ns,plan=outcome;chart=copy.deepcopy(state['chart']);chart['notes']=[{k:v for k,v in n.items() if not k.startswith('_')} for n in ns]
  chart['_comment']='収集したBeat Saber素材を本作向けに翻案。素材ごとの適用箇所はTools/ChartDesign/BeatSaberPatternBank/applications.json。'
  markers=[v for v in chart.get('editorMarkers',[]) if not v['name'].startswith('素材:')]
  current=[a for a in applications if a['songId']==sid and a['difficulty']==diff]
  for a in current:
   markers.append({'timeMs':round(m.t(a['startBeat'])*1000,4),'name':'素材: '+a['patternId']})
   a['noteIndices']=[i for i,n in enumerate(ns) if a['startBeat']<=n['beat']<a['endBeat']]
   a['appliedNotes']=[chart['notes'][i] for i in a['noteIndices']]
  chart['editorMarkers']=sorted(markers,key=lambda v:v['timeMs'])
  evidence=[dict(index=i,beat=n['beat'],hand='red' if n['_hand']==0 else 'blue',role=n['_role'],phrase=n['_phrase'],**m.ev(n['beat'])) for i,n in enumerate(ns)]
  target=output/'Assets/StreamingAssets/Songs'/sid;save(target/f'chart_{diff}.json',chart)
  if diff=='normal':save(target/'chart.json',chart)
  target=output/'Tools/ChartDesign/FiveSongs20261014'/sid;save(target/f'{diff}-cut-plan.json',{'notes':plan});save(target/f'{diff}-evidence.json',evidence)
  summaries[sid][diff].update(notes=len(ns),cuts=sum(n['count'] for n in ns),types=dict(collections.Counter(n['type'] for n in ns)),materialPatterns=[a['patternId'] for a in current],flowPassed=True)
 save(output/'Tools/ChartDesign/FiveSongs20261014/audit.json',summaries)
 save(output/'Tools/ChartDesign/BeatSaberPatternBank/applications.json',{'version':1,'convertedCount':len(patterns),'appliedCount':len(applications),'applications':applications,'inputChartHashes':original_hashes})
 print(json.dumps({'converted':len(patterns),'applied':len(applications),'charts':{sid+':'+d:len(occupancy[(sid,d)]) for sid,d in states}},ensure_ascii=False))
if __name__=='__main__':main()
