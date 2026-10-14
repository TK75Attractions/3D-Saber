"""参考映像の要素を本作の4拍譜面に翻案する。原作の完全な採譜ではない。"""
import argparse,copy,json,sys
from pathlib import Path
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE.parent/'FiveSongs20261014'))
from build_pack import path_for,save
from spatial_layout import rotate

# 通常配置を主体にし、短い特徴的なフレーズだけ縦・反転・斜めを使う。
LAYOUT={
 'reason-hard-1':0,
 'reason-hard-2':0,
 'reason-hard-3':0,
 'crab-normal-1':0,
 'crab-normal-2':0,
 'crab-normal-3':0,

 'need-normal-1':0,
 'need-normal-2':0,
 'need-normal-3':0,
 'rum-normal-1':0,
 'rum-normal-2':0,
 'rum-normal-3':0,

 'dream-normal-1': 0,
 'dream-normal-2': 90,
 'dream-normal-3': 0,
 'dream-normal-4': 0,
 'dream-hard-1': 0,
 'dream-hard-2': -90,
 'dream-hard-3': 180,
 'dream-hard-4': 0,
 'burning-hard-1': 0,
 'burning-hard-2': 0,
 'burning-hard-3': 0,
 'burning-hard-4': 45,
 'unlimited-hard-1': 0,
 'unlimited-hard-2': 0,
 'unlimited-hard-3': 0,
 'unlimited-hard-4': 0,
 'lvl-expert-1': 0,
 'lvl-expert-2': 0,
 'lvl-expert-3': 0,
 'lvl-expert-4': 0,
 'bring-it-on-1': 0,
 'bring-it-on-2': 0,
 'bring-it-on-3': 180,
 'bring-it-on-4': 0,
 'shut-up-dance-1': 0,
 'shut-up-dance-2': -90,
 'shut-up-dance-3': 0,
 'shut-up-dance-4': 0,
 'avoid-hand-clap': 0,
 'avoid-central-wall': 0,
 'avoid-face-punch': 0,
 'avoid-vision-block': 0,
 'avoid-hammer': 0,
 'avoid-double-direction': 0,
 'elixia-hard-1': 0,
 'elixia-hard-2': 0,
 'elixia-hard-3': 0,
 'there-normal-1': 0,
 'there-normal-2': -90,
 'there-normal-3': 0,
 'escape-normal-1': 0,
 'escape-normal-2': 0,
 'escape-normal-3': 0,
 'beat-hard-1': 0,
 'beat-hard-2': 0,
 'beat-hard-3': 0
}

# 映像で確認した単発・休符・担当手の順を、素材ごとに個別に翻案する。
# 座標は本作用に設計済み。原作映像のセイバー色から担当手を確認し、本作の担当色（左=青）へ変換する。
EXPLICIT={
 'reason-hard-1':('easy', '右から左2打の内外移動', [(0, 'right', 1.15, -0.2), (1, 'left', -1.1, -0.15), (3, 'left', -1.35, -0.15)]),
 'reason-hard-2':('hard', '半拍交互から横の両手へ', [(0, 'left', -1.15, -0.25), (0.5, 'right', 1.15, 0.25), (1, 'left', -1.2, 0.25), (1.5, 'right', 1.2, -0.25), (2, 'left', -1.1, -0.25), (2.5, 'right', 1.1, 0.25), (3, 'left', -1.25, -0.2), (3, 'right', 1.25, -0.2)]),
 'reason-hard-3':('hard', '単発から右寄り上下の両手へ', [(1, 'right', 1.2, 0.2), (2, 'left', -1.05, -0.2), (3.5, 'left', 0.75, -0.9), (3.5, 'right', 0.75, 0.9)]),
 'crab-normal-1':('normal', '単発から低い両手2組', [(0, 'left', -1.15, -0.15), (1, 'right', 1.15, -0.15), (2, 'left', -1.3, -0.3), (2, 'right', 1.3, -0.3), (3, 'left', -1.25, -0.1), (3, 'right', 1.25, -0.1)]),
 'crab-normal-2':('normal', '右から低い両手を反復し左へ', [(0, 'right', 1.15, -0.3), (1.5, 'left', -1.25, -0.2), (1.5, 'right', 1.25, -0.2), (2.5, 'left', -1.2, -0.35), (2.5, 'right', 1.2, -0.35), (3.5, 'left', -1.05, -0.2)]),
 'crab-normal-3':('normal', '左の横移動と両手の応答', [(0, 'left', -1.3, 0.05), (1, 'left', -1.05, 0.05), (2, 'left', -1.2, -0.2), (2, 'right', 1.2, -0.2), (3, 'left', -1.3, 0.1)]),

 'need-normal-1':('normal', '左手の小さな上下から右へ', [(0, 'left', -1.2, -0.25), (1, 'left', -1.2, 0.25), (3, 'right', 1.15, -0.2)]),
 'need-normal-2':('normal', '横の両手・片手・両手', [(0, 'left', -1.25, 0.25), (0, 'right', 1.25, 0.25), (1, 'left', -1.15, -0.25), (3, 'left', -1.25, -0.25), (3, 'right', 1.25, -0.25)]),
 'need-normal-3':('easy', '3拍半の余白から単発へ', [(3.5, 'left', -1.15, 0.15)]),
 'rum-normal-1':('easy', '1拍・3拍の交互', [(1, 'right', 1.25, 0.1), (3, 'left', -1.2, -0.25)]),
 'rum-normal-2':('normal', '片手3打の上下と内外', [(0, 'left', -1.25, 0.25), (1, 'left', -1.25, -0.25), (2, 'left', -1.05, 0.15), (3, 'right', 1.2, -0.2)]),
 'rum-normal-3':('easy', '右手の小さな横往復', [(1, 'right', 1.05, 0.1), (3, 'right', 1.3, 0.1)]),

 'escape-normal-1':('easy','単発タップと余白',[(0,'left',-1.2,-.25)]),
 'escape-normal-2':('easy','左右反転した交互',[(0,'left',1.0,.5),(2,'right',-1.0,-.5)]),
 'escape-normal-3':('easy','同じ側の上下を間隔を空けて渡す',[(0,'left',.65,.85),(3,'right',.65,-.85)]),
 'beat-hard-1':('normal','左右の交互と小さな上下動',[(0,'right',1.2,.25),(1,'left',-1.2,-.25),(2,'right',1.1,-.25),(3,'left',-1.1,.25)]),
 'beat-hard-2':('normal','横の両手アクセントから単発へ',[(0,'left',-1.2,.25),(0,'right',1.2,.25),(3,'left',-1.1,-.25)]),
 'beat-hard-3':('hard','片手の2打を斜めで折り返す',[(0,'left',-.9,.45),(1,'right',.2,-.9),(2,'right',.9,.45),(3,'left',-.2,.9)]),
}

# 動画で確認した手の受け渡し・密度差を記述する。位置は本作の可動域へ縮める。
# L/R=手、B=両手、末尾の数字=四分拍。原作の矢印はタップへ置き換える。
MANUAL={
 'dream-normal-1':('easy','R0 L2','vertical'),
 'dream-normal-2':('normal','B0 R2 L3','vertical'),
 'dream-normal-3':('easy','L0 L2','arc'),
 'dream-normal-4':('easy','B0','rest'),
 'dream-hard-1':('normal','L0 R1 L2 R3','diagonal'),
 'dream-hard-2':('normal','B0 L1 R2 L3','vertical'),
 'dream-hard-3':('hard','B0 L1 R1.5 L2 R3','vertical'),
 'dream-hard-4':('normal','R0 L1 R2 L3','arc'),
 'burning-hard-1':('normal','R0 L1 R2 L3','diagonal'),
 'burning-hard-2':('hard','R0 R0.5 L1 R2 R2.5 L3','vertical'),
 'burning-hard-3':('normal','L0 R1 L2 R3','height'),
 'burning-hard-4':('hard','R0 R0.5 L1 R2 B3','arc'),
 'unlimited-hard-1':('normal','R0 L1 R2 L3','compact'),
 'unlimited-hard-2':('normal','L0 R1 R2 L3','compact'),
 'unlimited-hard-3':('hard','L0 R0.5 L1 L2 R3','vertical'),
 'unlimited-hard-4':('hard','L0 R1 L2 B3','compact'),
 'lvl-expert-1':('normal','L0 R1 L2 R3','diagonal'),
 'lvl-expert-2':('hard','L0 R0.5 L1 R2 L2.5 R3','horizontal'),
 'lvl-expert-3':('normal','L0 R1 L2 R3','wide'),
 'lvl-expert-4':('hard','L0 R1 L1.5 R2 L2.5 R3 L3.5','horizontal'),
 'elixia-hard-1':('normal','R0 L1 R2 L3','elixia-turn'),
 'elixia-hard-2':('hard','L0 R1 L2 R3 L3.5','elixia-turn'),
 'elixia-hard-3':('hard','R0 R0.5 L1.5 R2.5 L3.5','elixia-turn'),
 'there-normal-1':('easy','L2','rest'),
 'there-normal-2':('normal','B0 B2','separated'),
 'there-normal-3':('easy','R0 R2','compact'),
 'avoid-hand-clap':('normal','B0 L2 R3','separated'),
 'avoid-central-wall':('easy','L0 R2','compact'),
 'avoid-face-punch':('normal','L0 R1 L2 R3','separated'),
 'avoid-vision-block':('normal','L0 R1 L2 R3','wide'),
 'avoid-hammer':('normal','L0 R1 L2 R3','vertical'),
 'avoid-double-direction':('hard','L0 R0.5 L1 R2 L2.5 R3','vertical'),
}
class Clock:
 def __init__(self,bpm):self.value=bpm
 def t(self,b):return b*60/self.value
 def bpm(self,b):return self.value

def manual_events(tokens,motion):
 events=[];phase={'L':0,'R':0}
 for token in tokens.split():
  hand=token[0];beat=float(token[1:])
  for h in ('L','R') if hand=='B' else (hand,):
   parity=phase[h]%2;sign=-1 if h=='L' else 1
   x=1.18;y=(-.25,.25)[parity]
   if motion=='diagonal':x+=(.12,-.12)[parity]
   if motion=='arc':x+=(-.12,.04,.12,.04)[phase[h]%4]
   if motion=='compact':x=1.12;y*=.65
   if motion=='height':y=.25 if h=='R' else -.25;x+=(-.10,.10)[parity]
   if motion=='horizontal':x+=(.15,-.15)[parity];y=0
   if motion=='wide':x=1.4
   if motion=='separated':x=1.3;y=(-.16,.16)[parity]
   if motion=='rest':y=-.18
   if motion=='elixia-turn':
    x=(1.05,1.35)[parity] if h=='R' else (1.30,1.05)[parity]
    y=(-.28,.12)[parity] if h=='R' else (.12,-.28)[parity]
   events.append({'beat':beat,'hand':'left' if h=='L' else 'right','x':round(sign*x,3),'y':round(y,3)})
   phase[h]+=1
 return events

def convert(item):
 source=item.get('sourceNotes');identifier=item['id']
 if source:
  diff={'Easy':'easy','Normal':'normal','Hard':'hard','Expert':'hard','Expert+':'hard'}[item['difficulty']]
  events=[]
  for n in source:
   left=n['hand']=='left'
   # 内外・高低の基本形を作り、後段で素材ごとの画面の軸へ展開する。
   inner=1.05;outer=1.35
   x=-(outer if n['column']==0 else inner) if left else (outer if n['column']==3 else inner)
   events.append({'beat':n['beat'],'hand':n['hand'],'x':x,'y':[-.24,0,.24][n['row']]})
  fidelity='譜面データの拍と担当手を保持。位置は簡略化し、縦・斜め・左右反転を含む本作向けの配置へ翻案。'
 elif identifier in EXPLICIT:
  diff,_,positions=EXPLICIT[identifier]
  events=[dict(beat=b,hand=h,x=x,y=y) for b,h,x,y in positions]
  fidelity='映像で確認した担当手の順・休符・密度から設計した翻案。位置は本作独自で、原作の正確な採譜ではない。'
 else:
  diff,tokens,motion=MANUAL[identifier];events=manual_events(tokens,motion)
  fidelity='映像の配置要素から設計した簡略化・翻案。原作の全ノーツの正確な採譜ではない。'
 if item['category']=='avoid':fidelity='問題点を取り除いた修正版。解説映像そのものを再現する譜面ではない。'
 angle=LAYOUT[identifier]
 for e in events:e['x'],e['y']=rotate(e['x'],e['y'],angle)
 layout=EXPLICIT[identifier][1] if identifier in EXPLICIT else '左右反転' if angle==180 else '縦並び' if abs(angle)==90 else '通常の左右配置' if angle==0 else '斜めの受け渡し'
 bpm=item['bpm'] or 140;clock=Clock(bpm);notes=[]
 for e in events:
  h=1 if e['hand']=='left' else 0
  notes.append(dict(beat=e['beat'],time=round(clock.t(e['beat'])*1000,4),x=e['x'],y=e['y'],type='tap',color='blue' if h==1 else 'red',direction='none',count=1,lengthMs=0,
   _hand=h,_cutBeats=[e['beat']],_arrow=False,_forced=None))
 notes.sort(key=lambda n:(n['time'],n['_hand']))
 if diff!='easy' and item['suggestedFlicksMax'] and len(notes)>=3:
  notes[-1]['_arrow']=True
 plan,failure=path_for(notes,clock)
 if failure:
  # 方向指定で入り口が成立しない場合だけ自由タップへ戻す。
  for n in notes:n['_arrow']=False
  plan,failure=path_for(notes,clock)
 assert failure is None,(identifier,failure)
 for i,n in enumerate(notes):
  if n['_arrow']:n['direction']=plan[i]['directions'][0];n['type']='direction'
 chart={'_comment':fidelity,'bpm':bpm,'coordScale':1,'offsetMs':0,'beatZeroMs':0,'displayLevel':{'easy':3,'normal':5,'hard':7}[diff],
  'timeSignatures':[{'beat':0,'numerator':4,'denominator':4}],'editorTempoMap':[],
  'notes':[{k:v for k,v in n.items() if not k.startswith('_')} for n in notes]}
 return {'id':identifier,'title':item['title'],'category':item['category'],'minimumDifficulty':diff,'referenceBpm':bpm,'sourceUrl':item['sourceUrl'],
  'adaptation':item['adaptation']+' 画面配置は'+layout+'を使用。色は担当手を示し、画面の左右に絶対固定せず、特殊配置は短い技として選ぶ。',
  'layout':layout,'orientationDegrees':angle,'fidelity':fidelity,'sourceTiming':item['timing'],'chartFile':'patterns/'+identifier+'.json','beats':4,
  'noteCount':len(notes),'flickCount':sum(n['_arrow'] for n in notes),'chart':chart}

def main():
 p=argparse.ArgumentParser();p.add_argument('--collection',type=Path,default=HERE/'collection.json');p.add_argument('--output',type=Path,default=HERE);args=p.parse_args()
 collection=json.loads(args.collection.read_text(encoding='utf-8'))
 patterns=[convert(item) for item in collection['items']]
 assert len({p['id'] for p in patterns})==len(collection['items'])
 for item in patterns:save(args.output/item['chartFile'],item['chart'])
 save(args.output/'converted-patterns.json',{'schemaVersion':1,'count':len(patterns),'patterns':patterns})
 print(json.dumps({'converted':len(patterns),'sourceData':sum('sourceNotes' in c for c in collection['items']),'videoAdaptations':sum('sourceNotes' not in c and c['category']!='avoid' for c in collection['items']),'corrected':sum(c['category']=='avoid' for c in collection['items'])}))
if __name__=='__main__':main()
