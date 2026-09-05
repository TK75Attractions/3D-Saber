"""音源解析とElDoradoだけの譜面傾向集計。出力は作業フォルダに限定する。"""
import json
import hashlib
import subprocess
from pathlib import Path
from collections import Counter
import numpy as np
from scipy import signal, ndimage

ROOT = Path(r'C:\Users\shike\dev\3D-Saber')
OUT = Path(__file__).resolve().parents[2] / 'Outputs' / 'YurikagoCharts'
OUT.mkdir(parents=True, exist_ok=True)
FFMPEG = r'C:\pinokio\bin\miniconda\Library\bin\ffmpeg.exe'
SR, HOP = 22050, 128


def read_json(p):
    return json.loads(p.read_text(encoding='utf-8-sig'))


def chart_stats(chart):
    notes = sorted(chart['notes'], key=lambda n: n['time'])
    times = np.array([n['time'] / 1000 for n in notes])
    intervals = np.diff(np.unique(np.round(times, 3)))
    lo, peak = 0, 0
    for hi, t in enumerate(times):
        while t - times[lo] > 2:
            lo += 1
        peak = max(peak, hi - lo + 1)
    longs = [n for n in notes if n.get('count', 1) > 1]
    simultaneous = sum(np.diff(times) < .01)
    return dict(notes=len(notes), cuts=sum(n.get('count', 1) for n in notes),
                first=round(float(times[0]), 3), last=round(float(times[-1]), 3),
                avg_nps=round(len(times)/(times[-1]-times[0]), 3), peak_2s_nps=peak/2,
                types=dict(Counter(n['type'] for n in notes)),
                colors=dict(Counter(n['color'] for n in notes)),
                directions=dict(Counter(n['direction'] for n in notes)),
                simultaneous_extra=int(simultaneous),
                interval_modes=Counter(np.round(intervals, 2)).most_common(12),
                x_hist=Counter(round(n['x'], 2) for n in notes).most_common(12),
                y_hist=Counter(round(n['y'], 2) for n in notes).most_common(12),
                long_details=[dict(time=round(n['time']/1000, 3), count=n['count'],
                                   length=round(n.get('lengthMs', 0)/1000, 3), color=n['color']) for n in longs],
                per_12sec=[dict(start=t, count=int(sum(t <= x < t+12 for x in times))) for t in range(0, int(times[-1])+1, 12)])


references = {}
for diff in ['easy', 'normal', 'hard']:
    file = ROOT / 'Assets/StreamingAssets/Songs/ElDorado' / f'chart_{diff}.json'
    data = read_json(file)
    references[diff] = dict(bpm=data['bpm'], sha256=hashlib.sha256(file.read_bytes()).hexdigest(), **chart_stats(data))
(OUT / 'reference-analysis.json').write_text(json.dumps(references, ensure_ascii=False, indent=2), encoding='utf-8')
print('REFERENCE', json.dumps(references, ensure_ascii=False), flush=True)

audio_path = ROOT / 'Assets/StreamingAssets/Songs/揺籠/audio.mp3'
raw = subprocess.check_output([FFMPEG, '-v', 'error', '-i', str(audio_path), '-f', 'f32le', '-ac', '1', '-ar', str(SR), 'pipe:1'])
y = np.frombuffer(raw, dtype='<f4')
freq, times, z = signal.stft(y, SR, nperseg=1024, noverlap=1024-HOP, boundary='zeros')
mag = np.abs(z).astype(np.float32)
bands = [(40, 180), (180, 650), (650, 2200), (2200, 9000)]
band_flux, band_energy = [], []
for low, high in bands:
    m = mag[(freq >= low) & (freq < high)]
    log = np.log1p(m * 150)
    novelty = np.maximum(0, np.diff(log, axis=1, prepend=log[:, :1])).mean(axis=0)
    novelty /= np.quantile(novelty, .95) + 1e-9
    band_flux.append(novelty)
    band_energy.append(np.sqrt((m*m).sum(axis=0)))
flux = np.array(band_flux)
energy = np.array(band_energy)
onset = (flux[0]*.30 + flux[1]*.20 + flux[2]*.20 + flux[3]*.30)
onset = np.maximum(0, onset - ndimage.median_filter(onset, size=87)*.55)
peaks, properties = signal.find_peaks(onset, distance=int(.065*SR/HOP), prominence=.08)
peak_times = times[peaks]

# 包絡自己相関で反復間隔を探す。既存メモのBPMを解析の入力には使わない。
centered = onset - onset.mean()
corr = signal.fftconvolve(centered, centered[::-1], mode='full')[len(centered)-1:]
corr /= corr[0]
lags, _ = signal.find_peaks(corr[:int(2.1*SR/HOP)], distance=4)
lags = [int(i) for i in lags if .24 < i*HOP/SR < 2.0]
tempo_candidates = sorted([dict(pulse_bpm=round(60*SR/HOP/i, 4), correlation=round(float(corr[i]), 5)) for i in lags], key=lambda r:-r['correlation'])[:12]

# 相関上位のパルスを細かく探索。長い区間全体を使いテンポの累積ずれを抑える。
smooth = ndimage.gaussian_filter1d(onset, sigma=.016*SR/HOP)
coarse = next(c['pulse_bpm'] for c in tempo_candidates if 120 <= c['pulse_bpm'] <= 210)
best = (-1, 0, 0)
for bpm in np.arange(coarse-3, coarse+3, .01):
    period = 60/bpm
    offsets = np.arange(0, period, .002)
    grid = offsets[:, None] + np.arange(0, int(len(y)/SR/period))[None, :]*period
    mask = (grid >= 8) & (grid <= len(y)/SR-10)
    values = np.interp(grid, times, smooth)
    scores = (values*mask).sum(axis=1)/mask.sum(axis=1)
    k = int(np.argmax(scores))
    if scores[k] > best[0]:
        best = (float(scores[k]), float(bpm), float(offsets[k]))
_, pulse_bpm, offset = best
period = 60/pulse_bpm
event_rows = []
for i in peaks:
    event_rows.append(dict(time=round(float(times[i]), 5), strength=round(float(onset[i]), 4),
                           low=round(float(flux[0,i]), 4), mid=round(float(flux[1,i]), 4),
                           vocal=round(float(flux[2,i]), 4), high=round(float(flux[3,i]), 4)))

# 12パルス(複合拍子の4拍相当)単位で、強弱と音色の変化を記録する。
bars = []
for b in range(int((len(y)/SR-offset)/(12*period))+1):
    start, end = offset+b*12*period, offset+(b+1)*12*period
    ix = (times >= start) & (times < end)
    pulse_strengths = []
    for p in range(12):
        near = abs(times-(start+p*period)) < .045
        pulse_strengths.append(round(float(onset[near].max(initial=0)), 3))
    bars.append(dict(bar=b+1, start=round(start, 3), end=round(end, 3),
                     rms=round(float(np.sqrt(np.mean(y[max(0,int(start*SR)):min(len(y),int(end*SR))]**2))), 5),
                     low=round(float(energy[0,ix].mean()), 5), high=round(float(energy[3,ix].mean()), 5),
                     onsets=int(sum((peak_times>=start)&(peak_times<end))), pulses=pulse_strengths))

report = dict(duration=round(len(y)/SR, 5), sample_rate=SR, audio_sha256=hashlib.sha256(audio_path.read_bytes()).hexdigest(),
              tempo_candidates=tempo_candidates, fitted_pulse_bpm=round(pulse_bpm, 5), fitted_phase_sec=offset,
              suggested_compound_beat_bpm=round(pulse_bpm/3, 6), bars=bars, events=event_rows)
(OUT / 'audio-analysis.json').write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding='utf-8')
np.savez_compressed(OUT / 'audio-features.npz', times=times, flux=flux, energy=energy, onset=onset, peaks=peaks, magnitude=mag, freq=freq)
print('TEMPO', json.dumps({k:v for k,v in report.items() if k not in ['events','bars']}, ensure_ascii=False), flush=True)
for row in bars:
    print('BAR', json.dumps(row), flush=True)
