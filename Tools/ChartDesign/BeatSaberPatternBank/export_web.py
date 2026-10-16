"""素材プレビューと更新した5曲だけを既存のスマホサイトへ反映する。"""
import argparse,copy,hashlib,json,shutil,zipfile,datetime
from pathlib import Path
from scope_guard import ALLOWED,authorize,protected_web,assert_unchanged
HERE=Path(__file__).resolve().parent
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def save(p,d):p.parent.mkdir(parents=True,exist_ok=True);p.write_text(json.dumps(d,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
def main():
 p=argparse.ArgumentParser();p.add_argument('--pattern-site',type=Path,required=True);p.add_argument('--chart-site',type=Path,required=True);p.add_argument('--exclude-song',action='append',default=[],help='並行編集中の曲の公開ファイルを保持する');args=p.parse_args()
 repo=HERE.parents[2];dist=args.pattern_site/'dist';data=read(HERE/'converted-patterns.json');apps=read(HERE/'applications.json')['applications']
 protected=protected_web(args.chart_site/'dist')
 for a in apps:authorize(a['songId'],a['difficulty'])
 for source in (HERE/'web').iterdir():shutil.copy2(source,dist/source.name)
 # 既存の動画一覧から、選択した素材の変換結果へ直接移動できるようにする。
 page=dist/'index.html';html=page.read_text(encoding='utf-8')
 if 'id="conversion-link"' not in html:
  html=html.replace('<div class="detail-actions"><a id="download"','<div class="detail-actions"><a id="conversion-link" class="button" href="patterns.html">本作の譜面を見る →</a><a id="download"')
  html=html.replace('<div class="download-links">','<div class="download-links"><a href="patterns.html">変換した全譜面 →</a><a href="patterns-3d-saber.zip" download>本作の譜面JSON ↓</a>')
 html=html.replace('3D-Saber に取り入れるなら','3D-Saber への変換')
 page.write_text(html,encoding='utf-8')
 script=dist/'app.js';js=script.read_text(encoding='utf-8')
 if "$('#conversion-link')" not in js:
  anchor="$('#status').textContent='';$('[data-mode"
  assert anchor in js
  js=js.replace(anchor,"$('#conversion-link').href='patterns.html#'+selected.id;"+anchor)
  script.write_text(js,encoding='utf-8')
 for name in ['core.js','renderer.js']:
  (dist/'pattern-renderer').mkdir(exist_ok=True);shutil.copy2(repo/'Tools/MobileChartLibrary/web'/name,dist/'pattern-renderer'/name)
 for pattern in data['patterns']:
  pattern['applications']=[a for a in apps if a['patternId']==pattern['id']]
  pattern['pending']=next((p for p in read(HERE/'applications.json').get('pending',[]) if p['patternId']==pattern['id']),None)
  save(dist/pattern['chartFile'],pattern['chart'])
  for a in pattern['applications']:
   chart=read(repo/'Assets/StreamingAssets/Songs'/a['songId']/f"chart_{a['difficulty']}.json")
   evidence=read(repo/'Tools/ChartDesign/FiveSongs20261014'/a['songId']/f"{a['difficulty']}-evidence.json")
   a['hands']=[evidence[i]['hand'] for i in a['noteIndices']]
   applied={'_comment':'曲へ実際に組み込んだ2小節。時刻と拍を区間先頭へ正規化。','bpm':a['targetBpm'],'coordScale':1,'offsetMs':0,'beatZeroMs':0,
    'displayLevel':chart['displayLevel'],'timeSignatures':chart['timeSignatures'],'editorTempoMap':[],
    'notes':[{**n,'beat':n['beat']-a['startBeat'],'time':round(n['time']-a['startSeconds']*1000,3)} for n in a['appliedNotes']]}
   # 開始時刻表示の丸め差を再生時刻へ持ち込まない。
   for n in applied['notes']:n['time']=round(n['beat']*60000/a['targetBpm'],4)
   a['chart']=applied;a['chartFile']='applied/'+pattern['id']+'.json';save(dist/a['chartFile'],applied)
 # 保留へ戻った素材の旧適用JSONを、現行ZIPへ混ぜない。原素材の4拍JSONは保持。
 current_applied={a['patternId']+'.json' for a in apps}
 for obsolete in (dist/'applied').glob('*.json'):
  if obsolete.name not in current_applied:obsolete.unlink()
 save(dist/'converted-patterns.json',data)
 with zipfile.ZipFile(dist/'patterns-3d-saber.zip','w',zipfile.ZIP_DEFLATED) as archive:
  archive.write(dist/'converted-patterns.json','converted-patterns.json')
  for folder in ['patterns','applied']:
   for f in (dist/folder).glob('*.json'):archive.write(f,f.relative_to(dist))
  archive.writestr('README.txt','3D-Saber形式の4拍素材と、5曲へ実際に組み込んだ2小節の譜面です。\n動画に基づく翻案であり、完全な原作採譜ではありません。\n適用先・出典・変更理由はconverted-patterns.jsonを参照。')
 chartdist=args.chart_site/'dist';catalog=read(chartdist/'catalog.json');excluded=set(args.exclude_song);assert excluded<=ALLOWED;ids=ALLOWED-excluded;updates=0
 excluded_before={s['sourceId']:copy.deepcopy(s) for s in catalog['songs'] if s['sourceId'] in excluded}
 excluded_hashes={c['path']:hashlib.sha256((chartdist/c['path']).read_bytes()).hexdigest() for s in catalog['songs'] if s['sourceId'] in excluded for c in s['charts']}
 for song in catalog['songs']:
  if song['sourceId'] not in ids:continue
  for entry in song['charts']:
   authorize(song['sourceId'],Path(entry['path']).stem.removeprefix('chart_'))
   source=repo/'Assets/StreamingAssets/Songs'/song['sourceId']/Path(entry['path']).name
   chart=read(source);ns=chart['notes']
   if source.read_bytes()!=(chartdist/entry['path']).read_bytes():
    shutil.copy2(source,chartdist/entry['path']);updates+=1
   entry.update(sha256=hashlib.sha256(source.read_bytes()).hexdigest(),level=chart['displayLevel'],count=len(ns),flicks=sum(n['direction']!='none' for n in ns),longs=sum(n['count']>1 for n in ns),gold=sum(n['color']=='gold' for n in ns))
 catalog['updated']=datetime.date.today().isoformat();save(chartdist/'catalog.json',catalog)
 assert all(s==excluded_before[s['sourceId']] for s in catalog['songs'] if s['sourceId'] in excluded)
 assert all(hashlib.sha256((chartdist/k).read_bytes()).hexdigest()==v for k,v in excluded_hashes.items())
 assert_unchanged(protected,protected_web(chartdist))
 save(HERE/'export-scope-verification.json',{'passed':True,'protectedCharts':len(protected['hashes']),'allowedSongs':sorted(ALLOWED),'protectedHashes':protected['hashes']})
 # Hosting has a 256 MiB expanded archive limit. Keep readable canonical
 # research JSON in the repository, but do not ship redundant indentation.
 # This does not modify any video, poster, ZIP, or mobile game chart.
 compacted=0
 for asset in dist.rglob('*.json'):
  # 分類ZIPのメタデータは保存一覧のSHA-256を保持する。package.pyが生成・検証する。
  if 'packages' in asset.relative_to(dist).parts:continue
  original=asset.read_bytes();value=read(asset)
  compact=(json.dumps(value,ensure_ascii=False,separators=(',',':'))+'\n').encode('utf-8')
  assert json.loads(compact)==value
  if len(compact)<len(original):asset.write_bytes(compact);compacted+=len(original)-len(compact)
 expanded=sum(asset.stat().st_size for asset in dist.rglob('*') if asset.is_file())
 save(HERE/'hosting-size-verification.json',{'expandedStaticBytes':expanded,'limitBytes':256*1024*1024,'jsonWhitespaceBytesRemoved':compacted,'mediaAndZipContentsUnchanged':True})
 assert expanded<256*1024*1024-512*1024,('Hosting archive needs more capacity; retain the verified materials without overwriting production.',expanded)
 print(json.dumps({'patterns':len(data['patterns']),'applications':len(apps),'updatedCharts':updates}))
if __name__=='__main__':main()
