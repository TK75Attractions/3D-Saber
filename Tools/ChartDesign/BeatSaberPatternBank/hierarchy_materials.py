"""4置き場の主譜面へ個別素材の対応打点だけを移植し、下位包含を更新する。"""
import argparse,copy,collections,math,sys
from pathlib import Path
HERE=Path(__file__).parent
sys.path.insert(0,str(HERE.parent/'FiveSongs20261014'))
from hierarchy_rework import read,save,Music,FORM,reduced,path_for,section
from integrate_patterns import internal
from scope_guard import authorize_specs

def style_core_valid(ns,anchors):
 """曲の代表フレーズは定時の位置素材で共通形へ戻さない。手は色由来の記録で照合。"""
 lookup={(round(n['time'],4),n['_hand']):(n['x'],n['y']) for n in ns}
 return all(lookup.get((round(a['time'],4),a['hand']))==(a['x'],a['y']) for a in anchors)

def rest_ranges_valid(ns,base,phase,ranges):
 """休みを主題にした素材は、移植先にも同じ位相の休みがあるときだけ使う。"""
 for n in ns:
  for cut in n.get('_cutBeats',[n['beat']]):
   if base<=cut<base+8:
    beat=(cut-base-phase)%4
    if any(start-1e-6<=beat<end-1e-6 for start,end in ranges):return False
 return True

def movement_valid(ns,m):
 for h in (0,1):
  seq=[n for n in ns if n['_hand']==h]
  for a,b in zip(seq,seq[1:]):
   # ロング表示の余白ではなく、記録された最終カットから移動時間を測る。
   gap=b['time']/1000-m.t(a['_cutBeats'][-1]);d=math.dist((a['x'],a['y']),(b['x'],b['y']))
   if d>1.2 and gap<.3-1e-5:return False
 groups=collections.defaultdict(list)
 for n in ns:groups[n['time']].append(n)
 return all(len(g)<2 or math.dist((g[0]['x'],g[0]['y']),(g[1]['x'],g[1]['y']))>1.5 for g in groups.values())

def finalize_preserved(ns,m,diff,metadata):
 """定時の位置移植では打点と方向を再選択せず、そのまま通る経路だけ採用する。"""
 assert len(ns)==len(metadata['notes']),('定時更新による打点増減',m.spec['id'],diff)
 for n,old in zip(ns,metadata['notes']):
  assert all(n[k]==old[k] for k in ('time','beat','color','count','lengthMs')),('確定打点の変更',m.spec['id'],diff)
  n['type']=old['type'];n['direction']=old['direction'];n['_arrow']=old['direction']!='none';n['_forced']=old['direction'] if n['_arrow'] else None
 plan,failure=path_for(ns,m)
 assert failure is None,('既存方向での経路不成立',m.spec['id'],diff,failure)
 chart=copy.deepcopy(metadata);chart['notes']=[{k:v for k,v in n.items() if not k.startswith('_')} for n in ns]
 ev=[dict(index=i,beat=n['beat'],hand='red' if n['_hand']==0 else 'blue',role=FORM[m.spec['id']][section(m,n['beat'])][4],phrase=int(n['beat']//16),sharedAccent=n.get('_key',False),**m.ev(n['beat'])) for i,n in enumerate(ns)]
 return chart,dict(notes=plan),ev

def main():
 p=argparse.ArgumentParser();p.add_argument('--repo',type=Path,required=True);p.add_argument('--features',type=Path,required=True);p.add_argument('--exclude-song',action='append',default=[],help='並行編集中の曲を今回の適用から除外する');args=p.parse_args()
 repo=args.repo;design=repo/'Tools/ChartDesign/FiveSongs20261014';bank=repo/'Tools/ChartDesign/BeatSaberPatternBank';prior=read(bank/'applications.json');apps=prior['applications'];patterns=read(bank/'converted-patterns.json')['patterns'];history=read(design/'hierarchy-design.json');specs=read(design/'songs.json');authorize_specs(specs)
 styles=read(design/'song-style-design.json') if (design/'song-style-design.json').exists() else {'songs':{}}
 excluded=set(args.exclude_song);assert excluded<={s['id'] for s in specs},('Unknown excluded song',excluded)
 states={};pending=[];added=[];done={a['patternId'] for a in apps}
 for spec in specs:
  if spec['id'] in excluded:continue
  sid=spec['id'];m=Music(spec,args.features);chart=read(repo/'Assets/StreamingAssets/Songs'/sid/'chart_hard.json');ev=read(design/sid/'hard-evidence.json');ns=internal(chart,read(design/sid/'hard-cut-plan.json'),m,ev)
  groups=collections.Counter(n['time'] for n in ns)
  for n,e in zip(ns,ev):n['_key']=e.get('sharedAccent',False);n['_pair']=groups[n['time']]==2;n['_arrow']=False;n['_forced']=None
  states[sid]=dict(m=m,chart=chart,ns=ns,changed=False,styleCore=styles['songs'].get(sid,{}).get('protectedCoreHits',[]))
 for pattern in patterns:
  if pattern['id'] in done:continue
  choices=[]
  for sid,state in states.items():
   if pattern.get('preferredSongs') and sid not in pattern['preferredSongs']:continue
   m=state['m'];ns=state['ns'];occupied=[(a['startBeat'],a['endBeat']) for a in apps if a['songId']==sid and a['difficulty']=='hard']
   for base in range(8,int(m.last_beat)-8,4):
    if abs(m.bpm(base)/pattern['referenceBpm']-1)>.10:continue
    if any(base<b and base+8>a for a,b in occupied):continue
    if any(base<b<base+8 for b,t,bpm in m.points):continue
    window=[n for n in ns if base<=n['beat']<base+8]
    if any(n.get('_key') or n['type']=='long' for n in window):continue
    if len(pattern['chart']['notes'])==1 and len(window)>4:continue
    for phase in (0,.5,1,1.5,2,2.5,3,3.5):
     if not rest_ranges_valid(ns,base,phase,pattern.get('requiredRestRanges',[])):continue
     matches=[]
     for r in (0,1):
      for source in pattern['chart']['notes']:
       b=base+r*4+(source['beat']+phase)%4;h=0 if source['color']=='red' else 1
       match=next((i for i,n in enumerate(ns) if abs(n['beat']-b)<.001 and n['_hand']==h and not n['_pair']),None)
       # 両手素材は両手が既にある実音へ合わせる。
       if sum(q['beat']==source['beat'] for q in pattern['chart']['notes'])==2:
        match=next((i for i,n in enumerate(ns) if abs(n['beat']-b)<.001 and n['_hand']==h and n['_pair']),None)
       if match is not None:matches.append((match,source))
     ratio=len(matches)/(2*len(pattern['chart']['notes']))
     if ratio<.75:continue
     score=abs(m.bpm(base)/pattern['referenceBpm']-1)*10-ratio
     choices.append((score,sid,base,phase,matches))
  accepted=None;rejections=collections.Counter()
  for score,sid,base,phase,matches in sorted(choices,key=lambda c:c[:4]):
   state=states[sid];ns=copy.deepcopy(state['ns']);changed=0
   for i,source in matches:
    changed+=(ns[i]['x'],ns[i]['y'])!=(source['x'],source['y']);ns[i]['x']=source['x'];ns[i]['y']=source['y']
   if changed<2:rejections['unchanged']+=1;continue
   if not style_core_valid(ns,state['styleCore']):rejections['songStyleCore']+=1;continue
   if not movement_valid(ns,state['m']):rejections['movementOrSimultaneous']+=1;continue
   _,failure=path_for(ns,state['m'])
   if failure is not None:rejections['cutPath']+=1;continue
   # 下位の共通アクセントを失わず、同じ手・位置で間引けることも先に検査。
   keys=[a['beat'] for a in history['songs'][sid]['sharedAccents']]
   try:
    normal=reduced(ns,state['m'],'normal',keys);easy=reduced(normal,state['m'],'easy',keys)
    for trial_diff,trial_ns in [('hard',ns),('normal',normal),('easy',easy)]:
     metadata=read(repo/'Assets/StreamingAssets/Songs'/sid/f'chart_{trial_diff}.json')
     finalize_preserved(copy.deepcopy(trial_ns),state['m'],trial_diff,metadata)
   except AssertionError:rejections['lowerHierarchy']+=1;continue
   accepted=(sid,base,phase,ns,changed,matches);break
  if accepted is None:
   reason=('近いBPMの未使用区間に、この素材の担当手・打点・両手／単発の並びが十分に一致する箇所がない。音や手順を無理に変更せず、小節素材として保存。' if not choices else '打点の一致候補はあるが、前後の移動・カット経路・下位包含を同時に満たさない。小節素材として保存。')
   if excluded:reason+=' 今回は並行編集中の'+', '.join(sorted(excluded))+'を適合調査・適用から除外。'
   pending.append({'patternId':pattern['id'],'reason':reason,'candidateCount':len(choices),'rejections':dict(rejections)});continue
  sid,base,phase,ns,changed,matches=accepted;state=states[sid];state.update(ns=ns,changed=True);m=state['m']
  app=dict(patternId=pattern['id'],songId=sid,songTitle=m.spec['title'],difficulty='hard',startBeat=base,endBeat=base+8,startBar=base//4+1,barCount=2,startSeconds=round(m.t(base),4),endSeconds=round(m.t(base+8),4),targetBpm=m.bpm(base),referenceBpm=pattern['referenceBpm'],bpmDifferencePercent=round((m.bpm(base)/pattern['referenceBpm']-1)*100,3),sourceUrl=pattern['sourceUrl'],integrationMode='hierarchy-reference',sourcePhaseOffsetBeats=phase,positionChangedNotes=changed,spatialLayout=pattern['layout'],layoutAdaptation='対応する実音の担当手へ、素材固有の置き場だけを移植。休符や中間の発音は本曲を保持。下位は同じ位置から間引く。',fidelity='映像に基づく動きの翻案。原作の正確な採譜ではない。',viewerUrl=f'https://saber-chart-library.metuji48.chatgpt.site/?song={sid}&difficulty=hard&t={max(0,m.t(base)-1):.2f}')
  apps.append(app);added.append(app['patternId']);print(app['patternId'],sid,base,changed,flush=True)
 for sid,state in states.items():
  if not state['changed']:continue
  m=state['m'];hard=state['ns'];keys=[a['beat'] for a in history['songs'][sid]['sharedAccents']];normal=reduced(hard,m,'normal',keys);easy=reduced(normal,m,'easy',keys)
  for diff,ns in [('hard',hard),('normal',normal),('easy',easy)]:
   target=repo/'Assets/StreamingAssets/Songs'/sid;chart,plan,ev=finalize_preserved(ns,m,diff,read(target/f'chart_{diff}.json'));save(target/f'chart_{diff}.json',chart)
   if diff=='normal':save(target/'chart.json',chart)
   save(design/sid/f'{diff}-cut-plan.json',plan);save(design/sid/f'{diff}-evidence.json',ev)
 # 改修前の適用は履歴へ。現在の索引は必ず現在の実ノーツへ対応させる。
 for a in apps:
  if a['songId'] in excluded:continue
  chart=read(repo/'Assets/StreamingAssets/Songs'/a['songId']/f"chart_{a['difficulty']}.json")
  if 'appliedNotes' in a:a.setdefault('preHierarchyAppliedNotes',copy.deepcopy(a['appliedNotes']))
  a['noteIndices']=[i for i,n in enumerate(chart['notes']) if a['startBeat']<=n['beat']<a['endBeat']];a['appliedNotes']=[chart['notes'][i] for i in a['noteIndices']];a['addedNotes']=len(a['appliedNotes'])
  if a['patternId'] not in added:
   a['fidelity']='映像の配置を参照した区間。現在は拍の土台・音の応答・曲別動線・下位包含を優先した設計で、元の翻案そのものとは同一ではない。旧適用は履歴に保持。'
   a['layoutAdaptation']='曲別の音はめと共通アクセント、Hardからの包含を優先して再構成。原作配置のそのままのコピーではない。'
 prior.update(applications=apps,pending=pending,convertedCount=len(patterns),appliedCount=len(apps));save(bank/'applications.json',prior)
 history['materialUpdate']={'added':added,'pending':pending,'excludedSongs':sorted(excluded)};save(design/'hierarchy-design.json',history)
 print({'newApplied':len(added),'pending':len(pending)})
if __name__=='__main__':main()
