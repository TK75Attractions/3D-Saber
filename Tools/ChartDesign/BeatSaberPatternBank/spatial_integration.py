"""音はめ改修済みHardへ、既存の打点・担当手を保って動きだけを翻案する。"""
import copy,collections,math
from integrate_patterns import read,digest,internal,plan_notes,save,Music,FORM,authorize,protected_repo,assert_unchanged

def invariant(notes):
 return [{k:v for k,v in n.items() if k not in ('x','y') and not k.startswith('_')} for n in notes]

def source_points(pattern,phase,hand):
 return sorted((repeat*4+(n['beat']+phase)%4,n['x'],n['y']) for repeat in (-1,0,1,2) for n in pattern['chart']['notes'] if (0 if n['color']=='red' else 1)==hand)

def interpolate(points,b):
 a=max((p for p in points if p[0]<=b),default=points[0]);z=min((p for p in points if p[0]>=b),default=points[-1])
 f=0 if a[0]==z[0] else (b-a[0])/(z[0]-a[0])
 return a[1]+(z[1]-a[1])*f,a[2]+(z[2]-a[2])*f

def integrate(args):
 repo=args.repo;design=repo/'Tools/ChartDesign/FiveSongs20261014';bank=repo/'Tools/ChartDesign/BeatSaberPatternBank';out=args.output
 protected=protected_repo(repo);prior=read(bank/'applications.json');apps=copy.deepcopy(prior['applications'])
 patterns=read(bank/'converted-patterns.json')['patterns'];done={a['patternId'] for a in apps};pending=[p for p in patterns if p['id'] not in done]
 if not pending:
  print('未適用素材なし。既存譜面は書き換えない。');return
 assert all(p.get('applicationMode')=='spatial-preserve-rhythm' for p in pending),'新規素材の適用方法を明記すること'
 history=read(design/'hard-rhythm-design.json');states={};hashes={};occupancy=collections.defaultdict(list)
 for a in apps:occupancy[a['songId']].append((a['startBeat'],a['endBeat'])) if a['difficulty']=='hard' else None
 for spec in read(design/'songs.json'):
  sid=spec['id'];authorize(sid,'hard');folder=repo/'Assets/StreamingAssets/Songs'/sid;m=Music(spec,args.features)
  audio=next(p for p in folder.glob('audio.*') if p.suffix!='.meta');assert digest(audio)==m.analysis['sha256']
  path=folder/'chart_hard.json';chart=read(path);assert chart.get('hardRhythmDesignVersion')
  hashes[path.relative_to(repo).as_posix()]=digest(path)
  notes=internal(chart,read(design/sid/'hard-cut-plan.json'),m,read(design/sid/'hard-evidence.json'))
  states[sid]={'chart':chart,'notes':notes,'m':m,'before':copy.deepcopy(notes),'changed':False}
 for pattern in sorted(pending,key=lambda p:p['id']):
  candidates=[]
  for sid,state in states.items():
   if pattern.get('preferredSongs') and sid not in pattern['preferredSongs']:continue
   m=state['m'];ns=state['notes']
   for section,(start,end,rhythm,motion,label) in enumerate(FORM[sid]):
    if rhythm in ('rest','doubletime','finish') or end-start<12:continue
    for base in range(max(8,math.ceil(start/4)*4),int(min(end-8,m.last_beat-8))+1,4):
     if any(base<b+4 and base+8>a-4 for a,b in occupancy[sid]):continue
     if any(base<b<base+8 for b,t,bpm in m.points):continue
     # 前回人手で選んだ短い技を今回の素材で上書きしない。
     tech=history['songs'][sid]['techniques']
     if any(t.get('accepted',True) and base<t['endBeat']+2 and base+8>t['startBeat']-2 for t in tech):continue
     old=[n for n in ns if base<=n['beat']<base+8];t0=m.t(base)*1000;t1=m.t(base+8)*1000
     if not old or any(n['type']=='long' and n['time']<t1+400 and n['time']+n['lengthMs']>t0-400 for n in ns):continue
     delta=(m.bpm(base)-pattern['referenceBpm'])/pattern['referenceBpm']*100
     if abs(delta)>pattern.get('maximumBpmDifferencePercent',10):continue
     for phase in (0,.5,1,1.5,2,2.5,3,3.5):
      anchors=[(base+r*4+(n['beat']+phase)%4,0 if n['color']=='red' else 1) for r in (0,1) for n in pattern['chart']['notes']]
      matches=[(b,h) for b,h in anchors if any(abs(n['beat']-b)<.01 and n['_hand']==h for n in old)]
      strong=[(b,h) for b,h in matches if m.ev(b)['strength']>=.45 and abs(m.ev(b)['distanceMs'])<=28]
      if len(matches)/len(anchors)<.6 or len(strong)/len(anchors)<.5:continue
      score=abs(delta)*.2-len(matches)/len(anchors)*4-len(strong)/len(anchors)+len(occupancy[sid])*.05+phase*.01
      candidates.append((score,sid,base,phase,label,matches,strong))
  candidates.sort();accepted=None
  for score,sid,base,phase,label,matches,strong in candidates:
   state=states[sid];m=state['m'];before=[n for n in state['notes'] if base<=n['beat']<base+8]
   for blend in (1,.8,.6):
    ns=copy.deepcopy(state['notes']);points={h:source_points(pattern,phase,h) for h in (0,1)};changed=0
    for n in ns:
     b=n['beat']-base
     if not 0<=b<8 or not points[n['_hand']]:continue
     x,y=interpolate(points[n['_hand']],b)
     # 境界では旧座標を残して前後と接続し、内部で素材の経路へ移る。
     a=blend*min(1,(b+.5)/1.5,(8-b)/1.5)
     nx=round(n['x']+(x-n['x'])*a,3);ny=round(n['y']+(y-n['y'])*a,3)
     changed+=math.hypot(nx-n['x'],ny-n['y'])>.15;n['x']=nx;n['y']=ny
    if changed<4:continue
    outcome=plan_notes(ns,m,'hard')
    if not outcome:continue
    checked,plan=outcome
    if invariant(checked)!=invariant(state['notes']):continue
    accepted=(sid,base,phase,label,blend,matches,strong,checked,plan,before,changed);break
   if accepted:break
  if not accepted:raise AssertionError(('適合箇所なし。打点を変更せず保留すること',pattern['id'],len(candidates)))
  sid,base,phase,label,blend,matches,strong,ns,plan,before,changed=accepted;state=states[sid];m=state['m']
  state.update(notes=ns,changed=True);occupancy[sid].append((base,base+8))
  notes=[{k:v for k,v in n.items() if not k.startswith('_')} for n in ns if base<=n['beat']<base+8]
  app={'patternId':pattern['id'],'songId':sid,'songTitle':m.spec['title'],'difficulty':'hard','startBeat':base,'endBeat':base+8,'startBar':base//4+1,'barCount':2,
   'startSeconds':round(m.t(base),4),'endSeconds':round(m.t(base+8),4),'targetBpm':m.bpm(base),'referenceBpm':pattern['referenceBpm'],
   'bpmDifferencePercent':round((m.bpm(base)/pattern['referenceBpm']-1)*100,3),'mirrorFirstBar':False,'secondBar':'担当手・リズムを保持して動きを続ける。機械的な左右反転はしない。',
   'section':label,'removedNotes':0,'addedNotes':len(notes),'newNotes':0,'positionChangedNotes':changed,'sourceCategory':pattern['category'],
   'integrationMode':'spatial-preserve-rhythm','spatialLayout':pattern['layout'],'layoutAdaptation':'素材の高低差・内外の動きを、曲の既存打点と担当手に沿って補間。入り口と出口は元の動線へ接続し、同時押しやフリックは曲の既存構成を保持。','sourcePhaseOffsetBeats':phase,'blend':blend,'matchedSourceAnchors':matches,'strongMatchedAnchors':strong,
   'beforeNotes':[{k:v for k,v in n.items() if not k.startswith('_')} for n in before],'fidelity':'映像から設計した小節素材の位置・高低差を参照。曲の既存リズム・担当手・フリックは保持。原作の正確な採譜やリズムのコピーではない。',
   'sourceUrl':pattern['sourceUrl'],'viewerUrl':f'https://saber-chart-library.metuji48.chatgpt.site/?song={sid}&difficulty=hard&t={max(0,m.t(base)-1.5):.2f}'}
  apps.append(app);print(pattern['id'],'->',sid,'hard bar',base//4+1,'phase',phase,'positions',changed,flush=True)
 summaries=read(design/'audit.json');updates=[]
 for sid,state in states.items():
  if not state['changed']:continue
  m=state['m'];ns,plan=plan_notes(state['notes'],m,'hard');assert invariant(ns)==invariant(state['before'])
  chart=copy.deepcopy(state['chart']);chart['notes']=[{k:v for k,v in n.items() if not k.startswith('_')} for n in ns]
  current=[a for a in apps if a['songId']==sid and a['difficulty']=='hard'];new=[a for a in current if a['patternId'] not in done]
  for a in current:
   a['noteIndices']=[i for i,n in enumerate(ns) if a['startBeat']<=n['beat']<a['endBeat']];a['appliedNotes']=[chart['notes'][i] for i in a['noteIndices']]
  for a in new:chart.setdefault('editorMarkers',[]).append({'timeMs':round(m.t(a['startBeat'])*1000,4),'name':'素材: '+a['patternId']})
  chart['editorMarkers'].sort(key=lambda v:v['timeMs'])
  save(out/'Assets/StreamingAssets/Songs'/sid/'chart_hard.json',chart);save(out/'Tools/ChartDesign/FiveSongs20261014'/sid/'hard-cut-plan.json',{'notes':plan})
  summaries[sid]['hard'].update(materialPatterns=[a['patternId'] for a in current],flowPassed=True)
  updates.append({'songId':sid,'patternIds':[a['patternId'] for a in new],'rhythmAndHandUnchanged':True,'positionsChanged':sum((a['x'],a['y'])!=(b['x'],b['y']) for a,b in zip(state['before'],ns))})
  after=history['songs'][sid]['after'];after.update(xRange=[min(n['x'] for n in ns),max(n['x'] for n in ns)],yRange=[min(n['y'] for n in ns),max(n['y'] for n in ns)],outerNotes=sum(abs(n['x'])>=1.65 for n in ns),highLowNotes=sum(abs(n['y'])>=.7 for n in ns),oppositeSideNotes=sum(n['x']*(1 if n['_hand']==0 else -1)<-.1 for n in ns))
 # 既存素材の適用内容も、音はめの根拠も消さない。
 for a in apps:
  if a['patternId'] in done:assert a==next(x for x in prior['applications'] if x['patternId']==a['patternId'])
 history.setdefault('materialUpdates',[]).append({'batch':'hourly-20261015-0020','mode':'spatial-preserve-rhythm','changes':updates})
 save(out/'Tools/ChartDesign/FiveSongs20261014/hard-rhythm-design.json',history);save(out/'Tools/ChartDesign/FiveSongs20261014/audit.json',summaries)
 save(out/'Tools/ChartDesign/BeatSaberPatternBank/applications.json',{**prior,'convertedCount':len(patterns),'appliedCount':len(apps),'applications':apps,'inputChartHashes':hashes})
 assert_unchanged(protected,protected_repo(repo))
