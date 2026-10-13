"""Zeroの実音から、追加打点の照合に使う帯域別の立ち上がりを抽出する。

BPMやOFFSETの推定には使わず、既存のBPM98・拍原点・変拍子マップを維持する。
依存: Python、NumPy、SciPy、PATH上のffmpeg。
"""
import argparse
import hashlib
import subprocess
from pathlib import Path

import numpy as np
from scipy import ndimage, signal


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--audio', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    expected = '5effa6bd84cfa597ac0c85802ddffb004f1a9f713bf503ed2891aea7b3805041'
    actual = hashlib.sha256(args.audio.read_bytes()).hexdigest()
    if actual != expected:
        raise ValueError('改作時のZero音源と異なるため、打点の再確認が必要: ' + actual)
    sr, hop = 22050, 110
    pcm = subprocess.check_output([
        'ffmpeg', '-v', 'error', '-i', str(args.audio), '-ac', '1',
        '-ar', str(sr), '-f', 'f32le', 'pipe:1',
    ])
    y = np.frombuffer(pcm, dtype='<f4')
    f, times, z = signal.stft(y, sr, nperseg=1024, noverlap=1024-hop, boundary='zeros')
    mag = np.abs(z)
    flux, energy = [], []
    for lo, hi in [(35, 180), (180, 650), (650, 2200), (2200, 10000)]:
        band = mag[(f >= lo) & (f < hi)]
        compressed = np.log1p(band * 150)
        rise = np.maximum(0, np.diff(compressed, axis=1, prepend=compressed[:, :1])).mean(axis=0)
        flux.append(rise / (np.quantile(rise, .95) + 1e-9))
        energy.append(np.sqrt((band * band).sum(axis=0)))
    flux, energy = np.array(flux), np.array(energy)
    onset = np.average(flux, axis=0, weights=[.30, .15, .2, .35])
    onset = np.maximum(0, onset - .5 * ndimage.median_filter(onset, size=81))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(args.output, times=times, onset=onset, flux=flux, energy=energy)
    print(f'{args.output}: {len(times)} frames, {len(y) / sr:.3f}s, SHA-256 {actual}')


if __name__ == '__main__':
    main()
