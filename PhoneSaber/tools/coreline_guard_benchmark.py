#!/usr/bin/env python3
"""C++ / Swift の ON/OFF host 時間。デコードを除き、派生物は TMPDIR 限定。"""
from __future__ import annotations

import argparse
import csv
import io
import json
import os
from pathlib import Path
import statistics
import subprocess
import sys
import tempfile

import coreline_shadow as shadow
import recognition_benchmark as benchmark


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--iterations',type=int,default=100)
    args = parser.parse_args()
    root = Path(os.environ.get('TMPDIR',tempfile.gettempdir())).resolve()
    if not args.output.resolve().is_relative_to(root) or args.iterations < 1:
        parser.error('output must be under TMPDIR; iterations must be positive')
    args.output.mkdir(parents=True,exist_ok=True)
    sys.path.insert(0,str(benchmark.PHONE/'ios/PhoneSaberSender/Tools'))
    import run_lossless_regression as formal
    _,fixtures = formal.load_fixtures(benchmark.MANIFEST)
    inbox = Path.home()/'Library/Application Support/PhoneSaber/diagnostics-inbox'
    paths = sorted({f['resolved_path'] for f in fixtures} | {p for p in inbox.glob('*/images/*.png')
        if any('phonesaber_20261010_'+s in str(p) for s in shadow.SESSIONS) and benchmark.original(p)})
    for mode in ('off','on'):
        destination = args.output / mode
        subprocess.run(['make','-C',str(shadow.CORE),'PHONESABER_CORELINE_GUARD='+str(int(mode=='on')),
                        'BUILD='+str(destination),str(destination/'core-benchmark')],check=True,capture_output=True)
        source = benchmark.PHONE/'ios/PhoneSaberSender/PhoneSaberSender'
        subprocess.run(['xcrun','swiftc','-O',
                        *([] if mode=='on' else ['-D','PHONESABER_DISABLE_CORELINE_GUARD']),
                        '-module-cache-path',str(args.output/'cache'),
                        str(source/'DetectionCore.swift'),str(source/'BGRADetection.swift'),
                        str(Path(__file__).with_name('CoreLineGuardBenchmark.swift')),
                        '-o',str(destination/'swift-benchmark')],check=True,capture_output=True)
    from PIL import Image
    rows, swift_rows = [], []
    # 画像ごとに OFF/ON/ON/OFF を隣接させ、長い全画像 sweep の負荷変動を抑える。
    for i,path in enumerate(paths):
        times = {'off':[],'on':[]}
        for mode in ('off','on','on','off'):
            run = subprocess.run([str(args.output/mode/'core-benchmark'),str(args.iterations),str(path)],
                                 check=True,capture_output=True,text=True)
            row = next(csv.DictReader(io.StringIO(run.stdout)))
            times[mode].append(float(row['median_ms']))
        rows.append({'path':str(path),'width':int(row['width']),'height':int(row['height']),
                     'off_ms':statistics.mean(times['off']),'on_ms':statistics.mean(times['on']),
                     'passes':times})
        # 両言語で同じ PNG / BGRA / step=2。Swift は tracking を含まない。
        with Image.open(path) as image:
            width,height = image.size
            pixels = image.convert('RGBA').tobytes('raw','BGRA')
        times = {'off':[],'on':[]}
        for mode in ('off','on','on','off'):
            run = subprocess.run([str(args.output/mode/'swift-benchmark'),str(width),str(height),str(args.iterations)],
                                 input=pixels,check=True,capture_output=True)
            times[mode].append(float(run.stdout))
        swift_rows.append({'path':str(path),'width':width,'height':height,
                           'off_ms':statistics.mean(times['off']),'on_ms':statistics.mean(times['on']),
                           'passes':times})
        if (i+1)%10==0: print('measured',i+1,'/',len(paths),flush=True)
    a,b = (statistics.mean(row[m+'_ms'] for row in rows) for m in ('off','on'))
    result = {'images':len(paths),'iterations_per_image_per_pass':args.iterations,'warmup_per_pass':10,
              'order':['off','on','on','off'],'off_ms':a,'on_ms':b,'delta_ms':b-a,
              'delta_percent':(b/a-1)*100,'rows':rows}
    (args.output/'cost.json').write_text(json.dumps(result,indent=2))
    print('C++',json.dumps({k:v for k,v in result.items() if k!='rows'}),flush=True)
    a,b = (statistics.mean(row[m+'_ms'] for row in swift_rows) for m in ('off','on'))
    result.update(off_ms=a,on_ms=b,delta_ms=b-a,delta_percent=(b/a-1)*100,rows=swift_rows)
    (args.output/'swift-cost.json').write_text(json.dumps(result,indent=2))
    print('Swift',json.dumps({k:v for k,v in result.items() if k!='rows'}),flush=True)


if __name__ == '__main__':
    main()
