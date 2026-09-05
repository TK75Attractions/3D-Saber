"""解析済み特徴から、拍の立ち上がりと展開境界を精査する。"""
import json
from pathlib import Path
import numpy as np
from scipy import ndimage

OUT = Path(__file__).resolve().parents[2] / 'Outputs/YurikagoCharts'
f = np.load(OUT / 'audio-features.npz')
t, onset, flux, energy = [f[k] for k in ('times', 'onset', 'flux', 'energy')]
period = 60/172
rows = []
for p in range(528):
    grid = p*period
    ix = np.flatnonzero(abs(t-grid-.02)<.08)
    k = ix[np.argmax(onset[ix])]
    # スペクトル変化量の山ではなく、その手前の立ち上がりを探す。
    j = k
    while j > 0 and k-j < 12 and onset[j-1] > onset[k]*.20:
        j -= 1
    window = (t >= grid+.06) & (t < grid+period-.02)
    row = dict(pulse=p, beat=round(p/3, 4), time=round(grid, 5), peak_time=round(float(t[k]), 5),
               attack_time=round(float(t[j]), 5), strength=round(float(onset[k]), 4),
               low=round(float(flux[0,k]), 4), mid=round(float(flux[1,k]), 4),
               upper_mid=round(float(flux[2,k]), 4), high=round(float(flux[3,k]), 4),
               energy=[round(float(e[window].mean()), 5) for e in energy])
    rows.append(row)
(OUT/'pulse-analysis.json').write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding='utf-8')
for start in range(0, 180, 30):
    group = [r for r in rows if start <= r['time'] < start+30 and r['strength'] > .55]
    print('TIMING', start, 'median_peak_ms', round(np.median([r['peak_time']-r['time'] for r in group])*1000, 2),
          'median_attack_ms', round(np.median([r['attack_time']-r['time'] for r in group])*1000, 2))
for phase in range(3):
    group = [r['strength'] for r in rows[72:474] if r['pulse']%3==phase]
    print('TRIPLET', phase, round(float(np.median(group)), 3))
for start, end in [(22,27),(65,69),(90,94),(101,105),(118,121),(130,134),(138,143),(164,168),(178,182)]:
    print('BOUNDARY', start, end)
    for r in rows:
        if start <= r['time'] < end:
            print(r['pulse'], r['time'], r['strength'], r['energy'])
