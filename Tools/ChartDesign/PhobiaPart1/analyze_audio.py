"""ffmpeg・NumPy・SciPyによる音源解析。生波形と特徴量は指定出力先だけへ保存。"""
import argparse
import hashlib
import json
import subprocess
from pathlib import Path
import numpy as np
from scipy import signal, ndimage

parser = argparse.ArgumentParser()
parser.add_argument('--audio', type=Path, default=Path(__file__).resolve().parents[3] / 'Assets/StreamingAssets/Songs/Phobia PARTⅠ/audio.mp3')
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
ROOT, SOURCE = args.output, args.audio
ROOT.mkdir(parents=True, exist_ok=True)
SR, HOP = 22050, 110
raw = subprocess.check_output(['ffmpeg', '-v', 'error', '-i', str(SOURCE), '-ac', '1', '-ar', str(SR), '-f', 'f32le', 'pipe:1'])
y = np.frombuffer(raw, dtype='<f4')
y.tofile(ROOT / 'audio.pcm')
freq, times, z = signal.stft(y, SR, nperseg=1024, noverlap=1024-HOP, boundary='zeros')
mag = np.abs(z)
flux, energy = [], []
for lo, hi in [(35,180),(180,650),(650,2200),(2200,10000)]:
    m = mag[(freq>=lo)&(freq<hi)]
    log = np.log1p(m*150)
    f = np.maximum(0, np.diff(log,axis=1,prepend=log[:,:1])).mean(axis=0)
    f /= np.quantile(f,.95)+1e-9
    flux.append(f)
    energy.append(np.sqrt((m*m).sum(axis=0)))
flux, energy = np.array(flux), np.array(energy)
onset = np.average(flux, axis=0, weights=[.30,.15,.2,.35])
onset = np.maximum(0,onset-.5*ndimage.median_filter(onset,size=81))
peaks, props = signal.find_peaks(onset,distance=round(.07*SR/HOP),prominence=.10)
centered = onset - np.mean(onset)
corr = signal.fftconvolve(centered,centered[::-1],mode='full')[len(centered)-1:]
corr /= corr[0]
lags,_=signal.find_peaks(corr[:round(2.1*SR/HOP)],distance=3)
candidates=sorted([{'bpm':float(60*SR/HOP/i),'correlation':float(corr[i])} for i in lags if .25<i*HOP/SR<1.5], key=lambda x:-x['correlation'])[:12]
smoothed=ndimage.gaussian_filter1d(onset,.014*SR/HOP)
fits=[]
for bpm in np.arange(70,225,.05):
    period=60/bpm
    offsets=np.arange(0,period,.01)
    grid=offsets[:,None]+np.arange(0,len(y)/SR/period)[None,:]*period
    mask=(grid>5)&(grid<len(y)/SR-5)
    values=np.interp(grid,times,smoothed)
    scores=(values*mask).sum(axis=1)/mask.sum(axis=1)
    idx=int(np.argmax(scores))
    fits.append({'bpm':round(float(bpm),3),'phase':float(offsets[idx]),'score':float(scores[idx])})
ranked=sorted(fits,key=lambda v:-v['score'])
distinct=[]
for r in ranked:
    if all(abs(r['bpm']-v['bpm'])>1 for v in distinct): distinct.append(r)
    if len(distinct)>=10: break
events=[{'time':round(float(times[i]),6),'strength':round(float(onset[i]),4),'bands':[round(float(v),4) for v in flux[:,i]]} for i in peaks]
regions=[]
for start in np.arange(0,len(y)/SR,4):
    mask=(times>=start)&(times<start+4)
    region=y[int(start*SR):int((start+4)*SR)]
    regions.append({'start':float(start),'rms':round(float(np.sqrt(np.mean(region**2))),4),'onsets':int(sum((times[peaks]>=start)&(times[peaks]<start+4))),'energy':[round(float(v),4) for v in energy[:,mask].mean(axis=1)]})
report={'duration':len(y)/SR,'sha256':hashlib.sha256(SOURCE.read_bytes()).hexdigest(),'autocorrelation':candidates,'tempo_fits':distinct,'regions':regions,'events':events}
segment_fits = []
for begin, end, guess in [(2,35,96), (43,73,132), (89,103,132)]:
    best = (-1, 0, 0)
    for bpm in np.arange(guess-.5, guess+.501, .01):
        period = 60/bpm
        phases = np.arange(0, period, .001)
        grid = phases[:,None] + np.arange(int(begin/period)+1, int(end/period))[None,:]*period
        scores = np.interp(grid, times, smoothed).mean(axis=1)
        idx = int(np.argmax(scores))
        if scores[idx] > best[0]: best = (float(scores[idx]), float(bpm), float(phases[idx]))
    segment_fits.append(dict(startSeconds=begin,endSeconds=end,fittedBpm=round(best[1],4),spectralPeakPhaseSeconds=round(best[2],4)))
report['segmentFits'] = segment_fits
report['authoredGrid'] = dict(bpmBefore=96,bpmAfter=132,changeSeconds=40,offsetMs=16,
    note='40秒の境界は持続音中に置いた制作上の格子。後半の明確な入りは41.818秒。楽器分離・作曲プロジェクトに基づく拍子確定ではない。')
(ROOT/'analysis.json').write_text(json.dumps(report,ensure_ascii=False,indent=2),encoding='utf-8')
np.savez_compressed(ROOT/'features.npz',times=times,flux=flux,energy=energy,onset=onset,peaks=peaks)
print(json.dumps({k:v for k,v in report.items() if k not in ('events','regions')},ensure_ascii=False,indent=2))
