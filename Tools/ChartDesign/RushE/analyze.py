"""公式原版の実音から時間軸を調べる。音声データは解析フォルダーだけに保存。"""
import sys
from pathlib import Path
ROOT = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT / 'deps'))
import hashlib, json, subprocess, os, shutil
import numpy as np

OUT = ROOT / 'Analysis'
OUT.mkdir(parents=True, exist_ok=True)
SOURCE = ROOT / 'Source/rush-e.m4a'
GAME = ROOT / 'Staging/Assets/StreamingAssets/Songs/RushE'
GAME.mkdir(parents=True, exist_ok=True)
FF = os.environ.get('RUSH_E_FFMPEG') or shutil.which('ffmpeg')
if not FF:
    raise SystemExit('ffmpeg をPATHに追加するか、RUSH_E_FFMPEGに実行ファイルを指定してください。')
subprocess.run([FF, '-v', 'error', '-y', '-i', str(SOURCE), '-map', '0:a:0', '-c:a', 'pcm_s16le', '-ar', '44100', '-ac', '2', str(GAME/'audio.wav')], check=True)
raw = subprocess.check_output([FF, '-v', 'error', '-i', str(GAME/'audio.wav'), '-ac', '1', '-ar', '22050', '-f', 'f32le', 'pipe:1'])
y = np.frombuffer(raw, '<f4')
sr, hop = 22050, 110
window = np.hanning(1024)
frames = np.lib.stride_tricks.sliding_window_view(np.pad(y, (512,512)),1024)[::hop]
mag = np.abs(np.fft.rfft(frames*window,axis=1)).T/window.sum()
freq = np.fft.rfftfreq(1024,1/sr)
times = np.arange(mag.shape[1])*hop/sr
flux, energy = [], []
for lo, hi in [(35,180),(180,650),(650,2200),(2200,10000)]:
    m = mag[(freq >= lo) & (freq < hi)]
    log = np.log1p(m*180)
    f = np.maximum(0, np.diff(log, axis=1, prepend=log[:,:1])).mean(axis=0)
    f /= np.quantile(f,.95) + 1e-9
    flux.append(f)
    energy.append(np.sqrt((m*m).sum(axis=0)))
flux, energy = np.array(flux), np.array(energy)
onset = np.average(flux, axis=0, weights=[.35,.30,.20,.15])
onset = np.maximum(0, onset-.40*np.convolve(onset,np.ones(51)/51,mode='same'))
candidates = np.where((onset[1:-1] > onset[:-2]) & (onset[1:-1] >= onset[2:]) & (onset[1:-1] > .18))[0]+1
kept=[]
for i in sorted(candidates,key=lambda i:-onset[i]):
    if all(abs(i-j)*hop/sr >= .06 for j in kept): kept.append(int(i))
peaks=np.array(sorted(kept))
np.savez_compressed(OUT/'features.npz', times=times, onset=onset, flux=flux, energy=energy, peaks=peaks)
kernel=np.exp(-.5*(np.arange(-6,7)/(.008*sr/hop))**2)
smoothed=np.convolve(onset,kernel/kernel.sum(),mode='same')
fits=[]
for begin in range(0, 136, 8):
    end=min(begin+8,len(y)/sr-1)
    candidates=[]
    for bpm in np.arange(80,241,.25):
        period=60/bpm
        phase=np.arange(0,period,.005)
        grid=begin+phase[:,None]+np.arange(int((end-begin)/period))[None,:]*period
        scores=np.interp(grid,times,smoothed).mean(axis=1)
        at=int(np.argmax(scores))
        candidates.append((float(scores[at]), float(bpm), round(float(begin+phase[at]),4)))
    selected=[]
    for score,bpm,phase in sorted(candidates,reverse=True):
        if all(abs(bpm-other['bpm'])>3 for other in selected):
            selected.append(dict(bpm=bpm,phase=phase,score=round(score,3)))
        if len(selected)==4: break
    fits.append(dict(start=begin,end=end,fits=selected))
active=np.where(np.abs(y)>.005)[0]
events=[dict(time=round(float(times[i]),5),strength=round(float(onset[i]),3)) for i in peaks]
report=dict(duration=len(y)/sr,firstActive=float(active[0]/sr),lastActive=float(active[-1]/sr),
    sha256=hashlib.sha256((GAME/'audio.wav').read_bytes()).hexdigest(),segments=fits,events=events)
(OUT/'analysis.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf8')
print(json.dumps({k:v for k,v in report.items() if k!='events'},ensure_ascii=False,indent=2))
print('FIRST EVENTS',events[:65])
