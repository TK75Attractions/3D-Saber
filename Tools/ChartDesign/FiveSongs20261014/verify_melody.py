"""確定した主旋律の採譜時刻・担当手・位置を実譜面と照合する。"""
import json
from pathlib import Path

HERE=Path(__file__).resolve().parent
REPO=HERE.parents[2]
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))

def verify():
 design=read(HERE/'melody-priority-design.json')['editReport']
 style=read(HERE/'song-style-design.json');report={}
 for sid,entry in design['songs'].items():
  assert sid in ('escapade','logic')
  chart=read(REPO/'Assets/StreamingAssets/Songs'/sid/'chart_hard.json')
  assert chart['displayLevel']=={'escapade':9,'logic':7}[sid]
  markers=0
  for r in entry['changes']['requests']:
   assert r['action'] in ('added','matched')
   n=r['applied'];assert n in chart['notes'],(sid,'主旋律の打点変更',n)
   if r.get('humanMarker'):
    assert n['time']==r['time'] and n['color'] in ('red','blue');markers+=1
   assert any(p['time']==n['time'] and p['hand']==(0 if n['color']=='red' else 1) and p['x']==n['x'] and p['y']==n['y'] for p in style['songs'][sid]['protectedCoreHits'])
  assert markers==(24 if sid=='escapade' else 0)
  report[sid]={'melodyAttacks':len(entry['changes']['requests']),'humanTimesPreserved':markers,'passed':True}
 return report

if __name__=='__main__':print(json.dumps(verify(),ensure_ascii=False))
