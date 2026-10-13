"""提供された小節・BPM表を実音の立ち上がりと照合する。音源は変更しない。"""
import argparse
import hashlib
import json
import subprocess
import os
import shutil
from pathlib import Path
import numpy as np
from scipy import ndimage, signal

FFMPEG = os.environ.get('FFMPEG') or shutil.which('ffmpeg') or 'C:/pinokio/bin/miniconda/Library/bin/ffmpeg.exe'

def anchors(tempos, origin=0):
    points=[]; t=origin; previous=0; bpm=tempos[0][1]
    for beat,next_bpm in tempos:
        t+=(beat-previous)*60/bpm
        points.append((beat,t,next_bpm));previous,bpm=beat,next_bpm
    return points

def time_at(beat, points):
    b,t,bpm=next(p for p in reversed(points) if p[0]<=beat+1e-8)
    return t+(beat-b)*60/bpm

def analyze(spec,source,out):
    sr=22050; hop=110
    pcm=subprocess.check_output([FFMPEG,'-v','error','-i',str(source),'-ac','1','-ar',str(sr),'-f','f32le','pipe:1'])
    y=np.frombuffer(pcm,dtype='<f4');duration=len(y)/sr
    f,t,z=signal.stft(y,sr,nperseg=1024,noverlap=1024-hop,boundary='zeros')
    m=np.abs(z);flux=[];energy=[]
    for low,high in [(35,180),(180,650),(650,2200),(2200,10000)]:
        band=m[(f>=low)&(f<high)]
        compressed=np.log1p(band*150)
        rise=np.maximum(0,np.diff(compressed,axis=1,prepend=compressed[:,:1])).mean(axis=0)
        flux.append(rise/(np.quantile(rise,.95)+1e-9))
        energy.append(np.sqrt((band*band).sum(axis=0)))
    flux,energy=np.array(flux),np.array(energy)
    onset=np.average(flux,axis=0,weights=[.30,.15,.2,.35])
    onset=np.maximum(0,onset-.5*ndimage.median_filter(onset,size=81))
    smooth=ndimage.gaussian_filter1d(onset,.008*sr/hop)
    points=anchors(spec['tempos'])
    beats=np.arange(0,1200,.5);grid=np.array([time_at(b,points) for b in beats]);grid=grid[grid<duration-1]
    # 変速曲は変更点によって小節の位相も特定できる。一定テンポ曲は冒頭の強拍も別に確認する。
    phases=np.arange(-.15,3.001,.002)
    scores=[]
    for phase in phases:
        times=grid+phase;valid=(times>1)&(times<duration-1)
        scores.append(float(np.mean(np.interp(times[valid],t,smooth))))
    order=np.argsort(scores)[::-1];best=[]
    for i in order:
        if all(abs(phases[i]-p['offsetSeconds'])>.04 for p in best):
            best.append(dict(offsetSeconds=round(float(phases[i]),4),score=round(scores[i],5)))
        if len(best)==12:break
    peaks,_=signal.find_peaks(onset,distance=round(.07*sr/hop),prominence=.12)
    active=np.where(np.sqrt(np.maximum(0,ndimage.uniform_filter1d(y*y,221)))>.009)[0]
    regions=[]
    for start in np.arange(0,duration,4):
        mask=(t>=start)&(t<start+4)
        regions.append(dict(startSeconds=float(start),energy=round(float(energy[:,mask].sum(axis=0).mean()),5),
                            onsetMean=round(float(onset[mask].mean()),4)))
    report=dict(id=spec['id'],source=source.name,sha256=hashlib.sha256(source.read_bytes()).hexdigest(),duration=duration,
                firstActiveSeconds=float(active[0]/sr),lastActiveSeconds=float(active[-1]/sr),
                suppliedTempoAnchors=[dict(beat=b,relativeSeconds=t,bpm=bpm) for b,t,bpm in points],
                phaseCandidates=best,regions=regions,
                earlyOnsets=[dict(time=round(float(t[i]),5),strength=round(float(onset[i]),3)) for i in peaks if t[i]<6])
    out.mkdir(parents=True,exist_ok=True)
    np.savez_compressed(out/'features.npz',times=t,onset=onset,flux=flux,energy=energy,peaks=peaks)
    (out/'analysis.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
    print(json.dumps({k:v for k,v in report.items() if k not in ('regions','earlyOnsets')},ensure_ascii=False),flush=True)

if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--sources',type=Path,required=True);parser.add_argument('--output',type=Path,required=True)
    args=parser.parse_args()
    for spec in json.loads(Path(__file__).with_name('songs.json').read_text(encoding='utf8')):
        analyze(spec,args.sources/spec['source'],args.output/spec['id'])
