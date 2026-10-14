"""時刻・音型・色を保ち、既存15譜面の画面配置とカット計画を作り直す。"""
import argparse,copy,collections,json,sys
from pathlib import Path
from integrate_patterns import read,digest,internal,plan_notes,save,Music,DIFFS
from spatial_layout import apply_layout,angle_at

def main():
 p=argparse.ArgumentParser();p.add_argument('--repo',type=Path,required=True);p.add_argument('--features',type=Path,required=True);p.add_argument('--output',type=Path,required=True);p.add_argument('--baseline',type=Path);args=p.parse_args()
 repo=args.repo;design=repo/'Tools/ChartDesign/FiveSongs20261014';bank=repo/'Tools/ChartDesign/BeatSaberPatternBank';output=args.output
 applications=read(bank/'applications.json');summary=read(design/'audit.json');report={};hashes={}
 for spec in read(design/'songs.json'):
  sid=spec['id'];m=Music(spec,args.features);report[sid]={}
  for diff in DIFFS:
   path=repo/'Assets/StreamingAssets/Songs'/sid/f'chart_{diff}.json'
   baseline=args.baseline or repo;chart=read(baseline/path.relative_to(repo))
   assert chart.get('spatialLayoutVersion',0)==0,'既に反映済み。再配置前のバックアップを入力にする。'
   hashes[str(path.relative_to(repo)).replace('\\','/')]=digest(path)
   base_design=baseline/'Tools/ChartDesign/FiveSongs20261014'
   evidence=read(base_design/sid/f'{diff}-evidence.json');notes=internal(chart,read(base_design/sid/f'{diff}-cut-plan.json'),m,evidence)
   for scale in (.80,.78,.76,.74,.72):
    candidate=apply_layout(copy.deepcopy(notes),sid,diff,scale)
    # 新しい位置で成立しない方向だけ解除し、元の音数とタップ位置は削らない。
    removed=[]
    from build_pack import path_for
    for _ in range(len(candidate)):
     plan,failure=path_for(candidate,m)
     if failure is None:break
     i=failure[0]
     if not candidate[i]['_arrow']:break
     candidate[i]['_arrow']=False;removed.append(i)
    if failure is None:break
   assert failure is None,(sid,diff,failure)
   for i,n in enumerate(candidate):
    n['direction']=plan[i]['directions'][0] if n['_arrow'] else 'none'
    if n['type']!='long':n['type']='direction' if n['_arrow'] else 'tap'
   chart['notes']=[{k:v for k,v in n.items() if not k.startswith('_')} for n in candidate]
   chart['spatialLayoutVersion']=1;chart['_comment']='色と画面位置を分離。横・縦・斜め・左右反転をフレーズで展開し、色ごとのカット経路を検証。'
   for a in applications['applications']:
    if (a['songId'],a['difficulty'])!=(sid,diff):continue
    a['noteIndices']=[i for i,n in enumerate(candidate) if a['startBeat']<=n['beat']<a['endBeat']]
    a['appliedNotes']=[chart['notes'][i] for i in a['noteIndices']]
    a['spatialLayout']='色の担当を保ち、曲のフレーズに合わせて縦・斜め・左右反転へ展開'
   target=output/'Assets/StreamingAssets/Songs'/sid;save(target/f'chart_{diff}.json',chart)
   if diff=='normal':save(target/'chart.json',chart)
   target=output/'Tools/ChartDesign/FiveSongs20261014'/sid;save(target/f'{diff}-cut-plan.json',{'notes':plan});save(target/f'{diff}-evidence.json',evidence)
   summary[sid][diff]['types']=dict(collections.Counter(n['type'] for n in candidate))
   summary[sid][diff]['spatialLayout']={'version':1,'scale':scale,'flicksConvertedToTaps':removed}
   report[sid][diff]={'scale':scale,'notes':len(candidate),'flicksConvertedToTaps':len(removed)}
   print(sid,diff,report[sid][diff],flush=True)
 applications['inputChartHashes']=hashes;applications['spatialLayoutVersion']=1
 save(output/'Tools/ChartDesign/BeatSaberPatternBank/applications.json',applications)
 save(output/'Tools/ChartDesign/FiveSongs20261014/audit.json',summary)
 save(output/'Tools/ChartDesign/BeatSaberPatternBank/spatial-layout-generation.json',report)
if __name__=='__main__':main()
