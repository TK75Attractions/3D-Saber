"""保存済み15譜面を独立に照合する。生成時の探索結果は信用せず実JSONを読む。"""
import argparse,collections,json,math
from pathlib import Path
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def inspect(repo):
 d=repo/'Tools/ChartDesign/FiveSongs20261014';design=read(d/'hierarchy-design.json');result={}
 for sid,review in design['songs'].items():
  charts={};hands={};result[sid]={}
  for diff in ('hard','normal','easy'):
   ns=read(repo/'Assets/StreamingAssets/Songs'/sid/f'chart_{diff}.json')['notes'];ev=read(d/sid/f'{diff}-evidence.json');hs=[e['hand'] for e in ev];charts[diff]=ns;hands[diff]=hs
   plan=read(d/sid/f'{diff}-cut-plan.json')['notes'];lastcut={id(n):p['timesMs'][-1] for n,p in zip(ns,plan)}
   groups=collections.Counter(n['time'] for n in ns);distances=[];all_distances=[];same_fast=0;gate={'hard':.3,'normal':.45,'easy':.85}[diff]
   for h in ('red','blue'):
    seq=[n for n,q in zip(ns,hs) if q==h]
    for a,b in zip(seq,seq[1:]):
     gap=(b['time']-a['time'])/1000;dist=math.dist((a['x'],a['y']),(b['x'],b['y']));all_distances.append(dist)
     if gap<=1:distances.append(dist)
     if gap<.25:same_fast+=1
     if dist>1.2:assert (b['time']-lastcut[id(a)])/1000>=gate-1e-5,(sid,diff,'最終カットから大移動までの待ち時間',a,b)
   if diff=='easy':assert all(n['x']>=0 if h=='red' else n['x']<=0 for n,h in zip(ns,hs))
   for accent in review['sharedAccents']:
    g=[n for n in ns if abs(n['time']/1000-accent['seconds'])<.001];assert len(g)==2,(sid,diff,'共通アクセント欠落',accent)
   if diff!='hard':
    upper='hard' if diff=='normal' else 'normal';lookup={(n['time'],h):(n) for n,h in zip(charts[upper],hands[upper])}
    for n,h in zip(ns,hs):
     q=lookup[(n['time'],h)]
     for k in ('beat','x','y','color','count','lengthMs'):assert n[k]==q[k],(sid,diff,k,n,q)
     assert n['direction']=='none' or n['direction']==q['direction']
   bars=collections.defaultdict(list)
   for n,h in zip(ns,hs):bars[int(n['beat']//4)].append((round(n['beat']%4,5),h,n['x'],n['y'],n['type']))
   signatures=[tuple(v) for v in bars.values()];repeat=(len(signatures)-len(set(signatures)))/len(signatures)
   singlehands=[hs[i] for i,n in enumerate(ns) if groups[n['time']]==1]
   result[sid][diff]={'notes':len(ns),'pairs':sum(v==2 for v in groups.values()),'pairNotePercent':round(sum(v for v in groups.values() if v==2)/len(ns)*100,2),'movementOver1_2Within1s':round(sum(x>1.2 for x in distances)/max(1,len(distances))*100,2),'movementWithin1sDenominator':len(distances),'movementOver1_2AllGaps':round(sum(x>1.2 for x in all_distances)/len(all_distances)*100,2),'fastSameHandUnder250ms':same_fast,'sameHandSingleTransitionPercent':round(sum(a==b for a,b in zip(singlehands,singlehands[1:]))/max(1,len(singlehands)-1)*100,2),'identicalBarRepeatPercent':round(repeat*100,2),'inheritedTimeHandPositionPercent':100 if diff!='hard' else None,'sharedAccents':len(review['sharedAccents'])}
 return result
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--repo',type=Path,required=True);p.add_argument('--output',type=Path,required=True);a=p.parse_args();r=inspect(a.repo);a.output.write_text(json.dumps(r,ensure_ascii=False,indent=2)+'\n',encoding='utf8');print(json.dumps(r,ensure_ascii=False))
