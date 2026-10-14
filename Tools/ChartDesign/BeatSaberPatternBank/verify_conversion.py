"""全素材の変換漏れと、適用記録が本編のノーツに一致することを検証する。"""
import json,math
from pathlib import Path
HERE=Path(__file__).resolve().parent
REPO=HERE.parents[2]
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def main():
 refs=read(HERE/'collection.json')['items'];converted=read(HERE/'converted-patterns.json')['patterns'];apps=read(HERE/'applications.json')['applications']
 pending=read(HERE/'applications.json').get('pending',[])
 ids={x['id'] for x in refs};assert ids=={x['id'] for x in converted}==({a['patternId'] for a in apps}|{a['patternId'] for a in pending})
 assert len(ids)==len(refs)==len(converted)==len(apps)+len(pending)
 assert all(p.get('reason') for p in pending)
 for item in converted:
  chart=read(HERE/item['chartFile']);assert chart==item['chart']
  assert chart['notes'] and chart['notes']==sorted(chart['notes'],key=lambda n:(n['time'],0 if n['color']=='red' else 1))
  assert sum(n['direction']!='none' for n in chart['notes'])<=1
  for n in chart['notes']:
   assert all(math.isfinite(n[k]) for k in ['beat','time','x','y'])
   assert 0<=n['beat']<4 and abs(n['time']-n['beat']*60000/chart['bpm'])<.001
   assert n['color'] in ('red','blue')
   assert abs(n['x'])<=2.5 and abs(n['y'])<=1.5
 for a in apps:
  chart=read(REPO/'Assets/StreamingAssets/Songs'/a['songId']/f"chart_{a['difficulty']}.json")
  actual=[chart['notes'][i] for i in a['noteIndices']]
  assert actual==a['appliedNotes']
  assert actual==[n for n in chart['notes'] if a['startBeat']<=n['beat']<a['endBeat']]
  assert len(actual)==a['addedNotes'] and a['endBeat']-a['startBeat']==8
  assert a['startBar']==a['startBeat']//4+1
 assert len({(a['songId'],a['difficulty']) for a in apps})==15
 print(json.dumps({'sourceMaterials':len(refs),'converted':len(converted),'applied':len(apps),'pending':len(pending),'affectedCharts':15,'passed':True}))
if __name__=='__main__':main()
