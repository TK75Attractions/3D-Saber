"""承認されたフレーズの打点を定時の配置更新でも保持する。"""
import copy,math

def select_retained(ns,m,diff,policy):
 desired={(round(t,4),0 if h=='red' else 1) for t,h in policy['songs'][m.spec['id']]['charts'][diff]['retainedHits']}
 lookup={(round(n['time'],4),n['_hand']):n for n in ns}
 assert desired<=lookup.keys(),('フレーズの必須打点が上位から失われている',m.spec['id'],diff)
 result=sorted((copy.deepcopy(lookup[k]) for k in desired),key=lambda n:(n['time'],n['_hand']))
 gate={'easy':.85,'normal':.45}[diff]
 for hand in (0,1):
  seq=[n for n in result if n['_hand']==hand]
  if diff=='easy':assert all(n['x']>=0 if hand==0 else n['x']<=0 for n in seq),('Easy交差',m.spec['id'])
  for a,b in zip(seq,seq[1:]):
   gap=b['time']/1000-m.t(a['_cutBeats'][-1])
   assert gap>0
   if math.dist((a['x'],a['y']),(b['x'],b['y']))>1.2:assert gap>=gate-1e-5,('保持したフレーズの移動時間不足',m.spec['id'],diff,a['beat'],b['beat'])
 return result
