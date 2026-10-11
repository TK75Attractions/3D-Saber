"""チュートリアル用の控えめな2小節ループ。Python 3 + numpy で再生成できる。

120 BPM / 4拍子 / 8拍 / 48 kHz stereo PCM16。外部サンプル・既存曲は使用しない。
Usage: python Tools/compose_tutorial_loop.py [output.wav]
"""
import json
import sys
import wave
from pathlib import Path

import numpy as np

SR = 48000
BPM = 120
BEAT = 60 / BPM
N = round(8 * BEAT * SR)
rng = np.random.default_rng(12008)
mix = np.zeros((N, 2), dtype=np.float64)


def hz(midi):
    return 440 * 2 ** ((midi - 69) / 12)


def envelope(t, attack, decay):
    # 最後も滑らかに0へ戻し、ループへ折り返した残響に段差を作らない。
    return (1 - np.exp(-t / attack)) * np.exp(-t / decay) * np.minimum(1, (t[-1] - t) / .025)


def add(sound, beat, gain=1, pan=0):
    indices = (np.arange(len(sound)) + round(beat * BEAT * SR)) % N
    angle = (pan + 1) * np.pi / 4
    # 末尾の余韻は先頭へ回す。ファイルをそのままループして継ぎ目が出ない。
    np.add.at(mix[:, 0], indices, sound * gain * np.cos(angle))
    np.add.at(mix[:, 1], indices, sound * gain * np.sin(angle))


def noise(seconds, low, high):
    size = round(seconds * SR)
    frequencies = np.fft.rfftfreq(size, 1 / SR)
    band = (1 - np.exp(-(frequencies / low) ** 4)) * np.exp(-(frequencies / high) ** 4)
    shaped = np.fft.irfft(np.fft.rfft(rng.normal(size=size)) * band, n=size)
    return shaped / max(np.std(shaped), 1e-9)


def kick():
    t = np.arange(round(.32 * SR)) / SR
    phase = 2 * np.pi * (56 * t + 52 * .019 * (1 - np.exp(-t / .019)))
    body = np.sin(phase) + .17 * np.sin(2 * phase)
    return body * envelope(t, .0013, .075)


def rim():
    t = np.arange(round(.16 * SR)) / SR
    woody = np.sin(2 * np.pi * 490 * t) + .45 * np.sin(2 * np.pi * 790 * t)
    return woody * envelope(t, .001, .018) + noise(.16, 700, 4400) * envelope(t, .001, .028) * .19


def hat():
    t = np.arange(round(.10 * SR)) / SR
    return noise(.10, 3500, 8000) * envelope(t, .0008, .015)


def bass(midi):
    t = np.arange(round(.66 * SR)) / SR
    phase = 2 * np.pi * hz(midi) * t
    return (np.sin(phase) + .24 * np.sin(2 * phase) + .06 * np.sin(3 * phase)) * envelope(t, .007, .19)


def key(midi):
    t = np.arange(round(1.20 * SR)) / SR
    phase = 2 * np.pi * hz(midi) * t
    tone = np.sin(phase + .34 * np.sin(2 * phase) * np.exp(-t / .13))
    tone += .1 * np.sin(3 * phase) * np.exp(-t / .12)
    return tone * envelope(t, .006, .24)


for beat in range(8):
    # 四分の低いパルスを全拍に置き、2・4拍の木の打音で小節を聞き取りやすくする。
    add(kick(), beat, .66 if beat % 4 == 0 else .54)
    add(hat(), beat, .036, .12)
    add(hat(), beat + .5, .010, -.12)
    if beat % 2 == 1:
        add(rim(), beat, .15, -.06)

for beat, midi in [(0, 38), (2, 45), (4, 43), (6, 38)]:
    add(bass(midi), beat, .32)

# Dmaj9 → Gmaj9。短い鍵盤の和音だけで、練習を邪魔するリードやフィルは入れない。
for start, chord in [(0, [54, 57, 61, 64]), (4, [55, 59, 62, 66])]:
    for i, midi in enumerate(chord):
        pan = (i - 1.5) * .17
        voice = key(midi)
        add(voice, start, .095, pan)
        add(voice, start + 2, .067, -pan)
        add(voice, start + .062, .012, -pan)
        add(voice, start + .094, .008, pan)

mix -= mix.mean(axis=0)
mix *= .56 / np.max(np.abs(mix))
pcm = np.round(mix * 32767).astype('<i2')
target = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[1] / 'Assets/Resources/Audio/Tutorial_Loop120.wav'
target.parent.mkdir(parents=True, exist_ok=True)
with wave.open(str(target), 'wb') as wav:
    wav.setnchannels(2)
    wav.setsampwidth(2)
    wav.setframerate(SR)
    wav.writeframes(pcm.tobytes())
print(json.dumps({'file': str(target), 'bpm': BPM, 'bars': 2, 'seconds': N / SR,
                  'frames': N, 'peak_dbfs': float(20 * np.log10(np.max(np.abs(mix)))),
                  'rms_dbfs': float(20 * np.log10(np.sqrt(np.mean(mix ** 2)))),
                  'seam_delta': float(np.max(np.abs(mix[0] - mix[-1])))}, ensure_ascii=False, indent=2))
