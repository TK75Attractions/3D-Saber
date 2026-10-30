"""曲を前もって大きくした再生用の音源(master.ogg)を作り、音量と時間のずれを実測する(2026-10-30)。

v2.5 の MusicBoost(+12 dB)は、出口の MasterLimiter(先読みなし)に山の立ち上がりと低音の1周期ごとの頭を削らせて音割れした。
ここでは原音を ffmpeg で float にデコードし(周波数・チャンネルはそのまま)、
  30 Hz のゼロ位相ハイパス → 遅いレベラー(dB 域・ソフトニー・数 dB まで) → 先読みリミッター(左右連動・4倍オーバーサンプルの真のピーク)
をオフラインで遅れ0で掛け(出力[n] = 入力[n] × 倍率[n])、ogg vorbis で書く。大きさは choose_makeup の決まり
(v2.5 での実際の大きさを下回らない・-8 LUFS より小さい曲は下げ幅の範囲で上げる)。天井の内側で割れずに -7 LUFS に届く曲は
ほとんど無かった(v2.5 は出口で削って大きく聞こえていた)。原音 audio.*・譜面は変えない。
loudness.json(gainDb=0、playbackFile=master.ogg)で本編・試聴・譜面エディターが鳴らす。
"""
import argparse
import concurrent.futures
import hashlib
import json
import re
import shutil
import subprocess
from pathlib import Path

import numba
import numpy as np
from scipy import ndimage, signal

# v2.5 の再現(Assets/Scripts/Game/MasterLimiter.cs・MusicBoost.cs と同じ値)。
LIMITER_THRESHOLD = .891
LIMITER_RELEASE_SECONDS = .15
V25_BOOST_DB = 12.0

# 目標の決まり(choose_makeup): v2.5 の大きさを下回らない。-8 LUFS より小さい曲は、下げ幅(95% の時間)7.5 dB までで -8 へ上げる。
FLOOR_LUFS = -8.0
GR95_BUDGET_DB = 7.5
GENTLE_GR95_DB = 2.0
TUTORIAL_TARGET_LUFS = -11.5
CEILING_DBTP = -1.5          # 処理の天井(真のピーク)
MAX_TRUE_PEAK_DBTP = -1.0    # ogg にしたあとの上限
MAX_SAMPLE = .89             # ogg にしたあとのサンプルの最大(出口の MasterLimiter の閾値 .891 に触れない)
HIGHPASS_HZ = 30.0
VORBIS_QUALITY = 8
# 大きい版(2026-10-30 夜、--softclip-margin-db): レベラーのあと、曲全体の大きさ + この dB を超える山の角を、4倍オーバーサンプルで
# なめらかに丸める(knee 倍から曲がり始め、超えない)。None なら掛けない(v2.6 の本番の音源)。
SOFTCLIP_MARGIN_DB = None
SOFTCLIP_KNEE = .7

# レベラー: 1.5 秒の窓の大きさが曲全体から knee を超えて離れた分を ratio で縮める。下げも上げも 6 dB まで
# (校歌・RUSH E のように静かな所と盛り上がりの差が大きい曲で、静かな所を持ち上げる)。
LEVEL_WINDOW_SECONDS = 1.5
LEVEL_SMOOTH_SECONDS = .6
LEVEL_KNEE_DB = 2.0
LEVEL_RATIO = 3.0
LEVEL_MAX_CUT_DB = 6.0
LEVEL_MAX_BOOST_DB = 6.0
# 上げるのは曲全体より 15 dB 下まで(25 dB 下で0)。無音・フェードの底は持ち上げない。
LEVEL_GATE_FULL_DB, LEVEL_GATE_ZERO_DB = 15.0, 25.0

# リミッター: 先読み 5 ms(2本の箱型の平均で S 字に下げる)、保持 5 ms、2段の戻り(dB 域の指数。速い方 25 ms、
# 遅い方は下げ幅の 7 割を 30 ms で追って 300 ms で戻る)。
LOOKAHEAD_SECONDS = .005
HOLD_SECONDS = .005
FAST_RELEASE_SECONDS = .025
SLOW_ATTACK_SECONDS = .03
SLOW_RELEASE_SECONDS = .3
SLOW_SHARE = .7
OVERSAMPLE = 4

AUDIO_NAMES = ('audio.ogg', 'audio.wav', 'audio.mp3')


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def probe(ffprobe, path):
    out = subprocess.run([ffprobe, '-v', 'error', '-select_streams', 'a:0', '-show_entries', 'stream=sample_rate,channels',
                          '-of', 'json', str(path)], capture_output=True, text=True, check=True).stdout
    stream = json.loads(out)['streams'][0]
    return int(stream['sample_rate']), int(stream['channels'])


def decode(ffmpeg, ffprobe, path):
    rate, channels = probe(ffprobe, path)
    raw = subprocess.run([ffmpeg, '-v', 'error', '-i', str(path), '-vn', '-f', 'f32le', '-acodec', 'pcm_f32le', 'pipe:1'],
                         capture_output=True, check=True).stdout
    return np.frombuffer(raw, dtype='<f4').reshape(-1, channels).astype(np.float64), rate


def ffmpeg_measure(ffmpeg, x, rate):
    """ffmpeg の ebur128 で、全編の大きさ(LUFS)・LRA・真のピーク(dBTP)を測る。"""
    data = np.ascontiguousarray(x, dtype='<f4').tobytes()
    err = subprocess.run([ffmpeg, '-hide_banner', '-nostats', '-f', 'f32le', '-ar', str(rate), '-ac', str(x.shape[1]),
                          '-i', 'pipe:0', '-af', 'ebur128=peak=true:framelog=quiet', '-f', 'null', '-'],
                         input=data, capture_output=True, check=True).stderr.decode('utf8', 'replace')
    summary = err[err.rfind('Summary:'):]
    integrated = float(re.search(r'I:\s+(-?[\d.]+) LUFS', summary).group(1))
    lra = float(re.search(r'LRA:\s+(-?[\d.]+) LU', summary).group(1))
    peak = re.search(r'Peak:\s+(-?[\d.]+|-inf) dBFS', summary).group(1)
    return integrated, lra, float(peak)


# ---- 大きさ(BS.1770)の速い見積もり。探すときだけ使い、最後は ffmpeg で測る ----

def k_weighting_sos(rate):
    """BS.1770 の K 特性を、その周波数の2段の biquad で(pyloudnorm と同じ式)。"""
    def biquad(kind, gain_db, q, fc):
        a_gain = 10 ** (gain_db / 40)
        w0 = 2 * np.pi * fc / rate
        alpha = np.sin(w0) / (2 * q)
        cos = np.cos(w0)
        if kind == 'shelf':
            b = [a_gain * ((a_gain + 1) + (a_gain - 1) * cos + 2 * np.sqrt(a_gain) * alpha),
                 -2 * a_gain * ((a_gain - 1) + (a_gain + 1) * cos),
                 a_gain * ((a_gain + 1) + (a_gain - 1) * cos - 2 * np.sqrt(a_gain) * alpha)]
            a = [(a_gain + 1) - (a_gain - 1) * cos + 2 * np.sqrt(a_gain) * alpha,
                 2 * ((a_gain - 1) - (a_gain + 1) * cos),
                 (a_gain + 1) - (a_gain - 1) * cos - 2 * np.sqrt(a_gain) * alpha]
        else:
            b = [(1 + cos) / 2, -(1 + cos), (1 + cos) / 2]
            a = [1 + alpha, -2 * cos, 1 - alpha]
        return np.array(b + a) / a[0]
    return np.vstack([biquad('shelf', 3.99984385397, .7071752369554193, 1681.974450955533),
                      biquad('pass', 0, .5003270373253953, 38.13547087613982)])


def k_power(x, rate):
    """K 特性を掛けた各チャンネルの2乗の和(サンプルごと)。"""
    weighted = signal.sosfilt(k_weighting_sos(rate), x, axis=0)
    return (weighted * weighted).sum(axis=1)


def gated_loudness(power, rate):
    """400 ms・75% 重ねのブロックで、-70 LUFS と相対 -10 LU のゲートを通した全編の大きさ。"""
    block, hop = int(round(.4 * rate)), int(round(.1 * rate))
    if len(power) < block:
        return -np.inf
    cumulative = np.concatenate([[0.0], np.cumsum(power)])
    starts = np.arange(0, len(power) - block + 1, hop)
    means = (cumulative[starts + block] - cumulative[starts]) / block
    with np.errstate(divide='ignore'):
        levels = -.691 + 10 * np.log10(means)
    kept = means[levels > -70]
    if len(kept) == 0:
        return -np.inf
    relative = -.691 + 10 * np.log10(kept.mean()) - 10
    kept = means[(levels > -70) & (levels > relative)]
    return -.691 + 10 * np.log10(kept.mean())


def speaker_lufs(y, rate):
    """台B のスピーカー(150 Hz 未満が出ない)で聞こえる大きさの目安。150 Hz のゼロ位相ハイパスを掛けてから測る(大きい版の報告用)。"""
    sos = signal.butter(4, 150, 'highpass', fs=rate, output='sos')
    return gated_loudness(k_power(signal.sosfiltfilt(sos, y, axis=0), rate), rate)


# ---- v2.5 の再現 ----

def simulate_v25(x, rate, gain_db):
    """loudness.json の倍率 × MusicBoost(+12 dB)→ MasterLimiter.Process(C# と同じ式)。
    倍率の式 gain = min(limit, gain + (1 - gain) × release) は、h = 1 - gain で h[n] = max(1 - limit[n], h[n-1] × d)
    (d = 1 - release)になり、log の累積最大で一度に解ける(1 にそろえる .99999 の丸めは省く。差は 1e-5 倍未満)。"""
    z = x * 10 ** ((gain_db + V25_BOOST_DB) / 20)
    peak = np.abs(z).max(axis=1)
    over = np.where(peak > LIMITER_THRESHOLD, 1 - LIMITER_THRESHOLD / np.maximum(peak, 1e-30), 0.0)
    log_d = -1.0 / (LIMITER_RELEASE_SECONDS * rate)
    h = decaying_max(over, log_d)
    gain = 1 - h
    previous = np.concatenate([[0.0], h[:-1]])
    # その場で下げたサンプル(出力が閾値ちょうど = 頭が平らに削られた)。
    flattened = int(np.count_nonzero((over > 0) & (over >= previous * np.exp(log_d) - 1e-15)))
    return z * gain[:, None], flattened, float(-20 * np.log10(max(gain.min(), 1e-9)))


def decaying_max(values, log_decay):
    """y[n] = max(values[n], y[n-1] × e^log_decay)、y[-1] = 0(values ≥ 0)。log 域の累積最大で解く。"""
    n = np.arange(len(values), dtype=np.float64)
    with np.errstate(divide='ignore'):
        shifted = np.log(values) - n * log_decay
    return np.exp(n * log_decay + np.maximum.accumulate(shifted))


# ---- マスタリング ----

def highpass(x, rate):
    sos = signal.butter(2, HIGHPASS_HZ, 'highpass', fs=rate, output='sos')
    return signal.sosfiltfilt(sos, x, axis=0)


def true_peak_envelope(x, chunk=1 << 19, margin=512):
    """各サンプルの区間 [n, n+1) の真のピーク(4倍オーバーサンプル、全チャンネルの最大)。resample_poly はゼロ位相。"""
    frames = len(x)
    out = np.empty(frames)
    for start in range(0, frames, chunk):
        end = min(frames, start + chunk)
        s0, e0 = max(0, start - margin), min(frames, end + margin)
        up = signal.resample_poly(x[s0:e0], OVERSAMPLE, 1, axis=0)
        env = np.abs(up).max(axis=1).reshape(-1, OVERSAMPLE).max(axis=1)
        out[start:end] = env[start - s0:end - s0]
    return np.maximum(out, np.abs(x).max(axis=1))


def leveler_gain(power, rate):
    """遅いレベラーの倍率(サンプルごと、線形)。1.5 秒の窓の大きさを、曲全体の大きさとの差で ratio 2 に縮める(下げ 4 dB・上げ 3 dB まで)。"""
    integrated = gated_loudness(power, rate)
    hop = rate // 100
    window = int(LEVEL_WINDOW_SECONDS * rate)
    cumulative = np.concatenate([[0.0], np.cumsum(power)])
    centers = np.arange(0, len(power), hop)
    lo = np.clip(centers - window // 2, 0, len(power))
    hi = np.clip(centers + window // 2, 0, len(power))
    means = (cumulative[hi] - cumulative[lo]) / np.maximum(hi - lo, 1)
    with np.errstate(divide='ignore'):
        level = -.691 + 10 * np.log10(np.maximum(means, 1e-30))
    deviation = level - integrated
    slope = 1 - 1 / LEVEL_RATIO
    cut = LEVEL_MAX_CUT_DB * np.tanh(slope * np.maximum(deviation - LEVEL_KNEE_DB, 0) / LEVEL_MAX_CUT_DB)
    boost = LEVEL_MAX_BOOST_DB * np.tanh(slope * np.maximum(-deviation - LEVEL_KNEE_DB, 0) / LEVEL_MAX_BOOST_DB)
    gate = np.clip((level - (integrated - LEVEL_GATE_ZERO_DB)) / (LEVEL_GATE_ZERO_DB - LEVEL_GATE_FULL_DB), 0, 1)
    gain_db = boost * gate - cut
    taps = max(3, int(LEVEL_SMOOTH_SECONDS * 100) | 1)
    kernel = np.hanning(taps + 2)[1:-1]
    padded = np.pad(gain_db, taps, mode='edge')
    gain_db = np.convolve(padded, kernel / kernel.sum(), mode='same')[taps:-taps]
    frame_gain_db = np.interp(np.arange(len(power)), centers, gain_db)
    return 10 ** (frame_gain_db / 20)


def box_mean_backward(values, length):
    """y[n] = values[n-length+1 .. n] の平均(先頭より前は values[0] が続くとみなす。曲の頭の山でも天井を守る)。"""
    padded = np.concatenate([np.full(length, values[0]), values])
    cumulative = np.concatenate([[0.0], np.cumsum(padded)])
    return (cumulative[length + 1:] - cumulative[1:len(values) + 1]) / length


@numba.njit(cache=False)
def _dual_release(held, fast_coef, slow_attack, slow_release, slow_share):
    """2段の戻り(dB 域)。速い方は山の直後から FAST で戻る。遅い方は要る下げ幅の SLOW_SHARE 倍を SLOW_ATTACK で追い SLOW_RELEASE で戻る。
    続く大きい音(長い和音・低音)は遅い方が下げ幅の大半を持つので、低音の1周期ごとに倍率が揺れない。単発の山は速く戻って痩せない。"""
    out = np.empty_like(held)
    fast = 0.0
    slow = 0.0
    for n in range(held.shape[0]):
        need = held[n]
        fast = max(need, fast * fast_coef)
        target = need * slow_share
        slow = target + (slow - target) * (slow_attack if target > slow else slow_release)
        out[n] = max(fast, slow)
    return out


def limiter_gain(peak, rate, ceiling):
    """先読みリミッターの倍率(線形)。必要な下げ幅(dB)に保持 → 2段の戻り → 先読み 5 ms の前向きの最大 →
    同じ幅に分けた2本の箱型の平均。平均する値はどれも、そのサンプルで要る下げ幅以上なので、ceiling を超えない(遅れ0)。"""
    need = np.maximum(0.0, 20 * np.log10(np.maximum(peak, 1e-30) / ceiling))
    hold = max(1, int(round(HOLD_SECONDS * rate)))
    held = ndimage.maximum_filter1d(need, hold, origin=(hold - 1) // 2, mode='constant', cval=0.0)  # [n-hold+1, n]
    released = _dual_release(held, np.exp(-1 / (FAST_RELEASE_SECONDS * rate)), np.exp(-1 / (SLOW_ATTACK_SECONDS * rate)),
                             np.exp(-1 / (SLOW_RELEASE_SECONDS * rate)), SLOW_SHARE)
    look = max(2, int(round(LOOKAHEAD_SECONDS * rate)))
    first = (look + 1) // 2
    second = look + 1 - first
    ahead = ndimage.maximum_filter1d(released, look, origin=-(look // 2), mode='constant', cval=0.0)  # [n, n+look-1]
    # 累積和の丸め(1e-7 dB 程度)の分だけ余分に下げる。
    smooth = box_mean_backward(box_mean_backward(ahead, first), second) + 1e-5
    assert np.all(smooth >= need), '先読みの平均が要る下げ幅を下回った'
    return 10 ** (-smooth / 20)


def soft_clip(y, rate):
    """山の角を丸める(大きい版だけ)。4倍に上げてから曲げて戻す(resample_poly はゼロ位相なので時間はずれない)。
    返すのは (丸めた音, 曲げたサンプルの割合)。"""
    level = gated_loudness(k_power(y, rate), rate)
    ceiling = 10 ** ((level + SOFTCLIP_MARGIN_DB) / 20)
    knee = SOFTCLIP_KNEE * ceiling
    up = signal.resample_poly(y, OVERSAMPLE, 1, axis=0)
    size = np.abs(up)
    bent = size > knee
    shaped = np.where(bent, knee + (ceiling - knee) * np.tanh((size - knee) / (ceiling - knee)), size)
    return signal.resample_poly(np.sign(up) * shaped, 1, OVERSAMPLE, axis=0)[:len(y)], float(np.mean(bent))


class Prepared:
    """30 Hz ハイパスとレベラーまで掛けた音と、倍率を探すのに使う値(K 特性の2乗の和・真のピーク)。"""

    def __init__(self, x, rate):
        filtered = highpass(x, rate)
        self.rate = rate
        self.leveled = filtered * leveler_gain(k_power(filtered, rate), rate)[:, None]
        self.clipped_share = 0.0
        if SOFTCLIP_MARGIN_DB is not None:
            self.leveled, self.clipped_share = soft_clip(self.leveled, rate)
        self.power = k_power(self.leveled, rate)
        self.peak = true_peak_envelope(self.leveled)
        self.base = gated_loudness(self.power, rate)
        # ogg にしたあと天井をはみ出した場所だけ下げるための倍率(真のピークの見積もりに掛ける。全体の天井は下げない)。
        self.scale = np.ones(len(x))

    def run(self, makeup_db, ceiling_dbtp):
        """makeup_db 上げてリミッターを掛けたときの大きさの見積もり(LUFS)と、リミッターの倍率。"""
        g = 10 ** (makeup_db / 20)
        limit = limiter_gain(self.peak * self.scale * g, self.rate, 10 ** (ceiling_dbtp / 20))
        return gated_loudness(self.power * (g * limit) ** 2, self.rate), limit

    def output(self, makeup_db, limit):
        return self.leveled * (10 ** (makeup_db / 20) * limit)[:, None]


def search(f, lo, hi, tolerance=.02):
    """f(上げ幅) が単調に増えるとき、f >= 0 になる一番小さい上げ幅。hi でも届かなければ None。"""
    if f(hi) < 0:
        return None
    if f(lo) >= 0:
        return lo
    while hi - lo > tolerance:
        mid = (lo + hi) / 2
        if f(mid) < 0:
            lo = mid
        else:
            hi = mid
    return hi


def reduction_stats(limit):
    reduction = -20 * np.log10(limit)
    return {f'limitP{p}Db': round(float(np.percentile(reduction, p)), 2) for p in (50, 95, 99)} | dict(
        limitMaxDb=round(float(reduction.max()), 2))


def choose_makeup(prep, ceiling_dbtp, v25, offset):
    """目標の決まり(2026-10-30): v2.5 の大きさ v25 を下回らない。FLOOR_LUFS より小さい曲は、リミッターの下げ幅が 95% の時間で
    GR95_BUDGET_DB 以内に収まる範囲で FLOOR_LUFS へ向けて上げる。v25 に天井の内側で届かない曲(青葉騒)は、下げ幅 GENTLE_GR95_DB まで。
    offset は ffmpeg の測定と見積もりの差(測定 = 見積もり + offset)。返すのは上げ幅(dB)。"""
    lo = (min(v25, FLOOR_LUFS) - offset) - prep.base - 1
    hi = lo + 32
    loud = lambda goal: (lambda m: prep.run(m, ceiling_dbtp)[0] - (goal - offset))
    gr95 = lambda budget: (lambda m: reduction_stats(prep.run(m, ceiling_dbtp)[1])['limitP95Db'] - budget)
    at_v25 = search(loud(v25), lo, hi)
    if at_v25 is None:
        return search(gr95(GENTLE_GR95_DB), lo, hi) or hi
    if v25 >= FLOOR_LUFS:
        return at_v25
    at_floor = search(loud(FLOOR_LUFS), lo, hi)
    at_budget = search(gr95(GR95_BUDGET_DB), lo, hi)
    cap = min(m for m in (at_floor, at_budget, hi) if m is not None)
    return max(at_v25, cap)


def louder_makeup(prep, ceiling_dbtp, louder, offset):
    """大きい版の上げ幅(2026-10-30 夜「それよりもっと大きくしたバージョンの音源だけ用意しといて」)。全曲を同じ大きさ louder['target']
    (測定の LUFS)へ上げる。ただしリミッターの下げ幅(95% の時間)が louder['gr95Cap'] dB に達したらそこで止める
    (このリミッターでは、上げても大きさが頭打ちになり、下げ幅だけが増える)。"""
    lo = (min(louder['target'], -12.0) - offset) - prep.base - 1
    hi = lo + 32
    at_target = search(lambda m: prep.run(m, ceiling_dbtp)[0] - (louder['target'] - offset), lo, hi)
    at_cap = search(lambda m: reduction_stats(prep.run(m, ceiling_dbtp)[1])['limitP95Db'] - louder['gr95Cap'], lo, hi)
    return min(m for m in (at_target, at_cap, hi) if m is not None)


def master_to(x, rate, target, ceiling_dbtp=CEILING_DBTP):
    """決まった大きさ target(見積もり)まで上げる(練習音源用)。返すのは (出力, 情報)。"""
    prep = Prepared(x, rate)
    lo = target - prep.base - 1
    makeup = search(lambda m: prep.run(m, ceiling_dbtp)[0] - target, lo, lo + 32)
    if makeup is None:
        raise RuntimeError(f'目標 {target} LUFS に届かない')
    loud, limit = prep.run(makeup, ceiling_dbtp)
    return prep.output(makeup, limit), dict(makeupDb=round(makeup, 2), estimateLufs=round(loud, 2), **reduction_stats(limit))


def encode_ogg(ffmpeg_vorbis, y, rate, path):
    data = np.ascontiguousarray(y, dtype='<f4').tobytes()
    subprocess.run([ffmpeg_vorbis, '-v', 'error', '-y', '-f', 'f32le', '-ar', str(rate), '-ac', str(y.shape[1]), '-i', 'pipe:0',
                    '-c:a', 'libvorbis', '-q:a', str(VORBIS_QUALITY), '-map_metadata', '-1', str(path)], input=data, check=True)


def derivative_lag(a, b, rate, at_seconds, window_seconds=1.0, max_lag=2048):
    """a と b の差分信号(左右の和)の相互相関の山の時間差(サンプル)と、時間差0での相関係数。"""
    start = int(at_seconds * rate)
    length = int(window_seconds * rate)
    seg_a = np.diff(a[start:start + length].sum(axis=1))
    seg_b = np.diff(b[start - max_lag:start + length + max_lag].sum(axis=1))
    corr = signal.correlate(seg_b, seg_a, mode='valid', method='fft')
    lag = int(np.argmax(corr)) - max_lag
    zero = seg_b[max_lag:max_lag + len(seg_a)]
    coefficient = float(np.dot(seg_a, zero) / np.sqrt(np.dot(seg_a, seg_a) * np.dot(zero, zero) + 1e-30))
    return lag, coefficient


# ---- チュートリアルの練習音源(Assets/Resources/Audio/Tutorial_Loop120.wav、4 秒のループ) ----
# TutorialMusicTests と同じ測り方: 48 kHz の K 特性で2周させた2周目の大きさ、150 Hz のハイパス2段を足した大きさ、
# 各拍(0.5 秒ごと)の立ち上がり 0〜0.1 秒と余韻 0.32〜0.42 秒の RMS、サンプルの最大、継ぎ目の段差。

def loop_loudness(y, high_pass_hz=0.0):
    biquads = [((1.53512485958697, -2.69169618940638, 1.19839281085285), (1, -1.69065929318241, .73248077421585)),
               ((1, -2, 1), (1, -1.99004745483398, .99007225036621))]
    if high_pass_hz > 0:
        k = np.tan(np.pi * high_pass_hz / 48000)
        norm = 1 / (1 + np.sqrt(2) * k + k * k)
        hp = ((norm, -2 * norm, norm), (1, 2 * (k * k - 1) * norm, (1 - np.sqrt(2) * k + k * k) * norm))
        biquads += [hp, hp]
    sos = np.array([list(b) + list(a) for b, a in biquads])
    twice = np.concatenate([y, y])
    weighted = signal.sosfilt(sos, twice, axis=0)[len(y):]
    return -.691 + 10 * np.log10((weighted * weighted).sum() / len(y))


def loop_checks(y, rate=48000):
    def rms(start, length):
        a, n = int(start * rate), int(length * rate)
        part = y[a:a + n]
        return float(np.sqrt((part * part).mean()))
    beats = [(rms(b * .5, .10), rms(b * .5 + .32, .10)) for b in range(8)]
    peak = float(np.abs(y).max())
    seam = float(np.abs(y[0] - y[-1]).max())
    ok = .3 <= peak <= .89 and seam < .002 and all(on > .03 and on > tail * 2 for on, tail in beats)
    return ok, dict(peak=round(peak, 4), seam=round(seam, 5), minOnsetOverTail=round(min(on / tail for on, tail in beats), 2),
                    loudness=round(loop_loudness(y), 2), smallSpeakerDrop=round(loop_loudness(y) - loop_loudness(y, 150), 2))


def master_loop(path, target, ceiling_dbtp=CEILING_DBTP):
    """ループを3つつなげて処理し、真ん中を取る(継ぎ目で倍率が切れない)。PCM16 で同じ長さ・周波数のまま。"""
    import soundfile
    x, rate = soundfile.read(str(path), dtype='float64', always_2d=True)
    assert rate == 48000, '練習音源は 48 kHz'
    frames = len(x)
    y, info = master_to(np.concatenate([x, x, x]), rate, target, ceiling_dbtp)
    middle = y[frames:2 * frames]
    return np.round(np.clip(middle, -1, 1) * 32767) / 32767, info


def build_tutorial(path, target, write):
    """テスト(TutorialMusicTests)を余裕つき(立ち上がり/余韻 2.1 倍以上・最大 .885 以下)で満たす一番大きい目標を、target 以下で
    二分探索する。ループは拍ごとに下げ幅が揺れると余韻が持ち上がるので、遅い方を 9 割・0.6 秒にする(曲より拍がはっきり残る)。
    write なら上書きする(.meta は変えない)。"""
    import soundfile
    global SLOW_SHARE, SLOW_RELEASE_SECONDS
    SLOW_SHARE, SLOW_RELEASE_SECONDS = .9, .6
    x, _ = soundfile.read(str(path), dtype='float64', always_2d=True)
    print('練習音源の今:', loop_checks(x)[1], flush=True)

    def passes(aim):
        try:
            y, info = master_loop(path, aim)
        except RuntimeError:
            return False, None
        ok, checks = loop_checks(y)
        ok = ok and checks['minOnsetOverTail'] >= 2.1 and checks['peak'] <= .885 and checks['seam'] < .0015
        print(f'練習音源 目標 {aim:.2f}: {ok} {checks} {info}', flush=True)
        return ok, (y, checks, info)

    ok, best = passes(target)
    lo, hi = target - 8, target
    if ok:
        lo = target
    else:
        ok, best = passes(lo)
        if not ok:
            raise RuntimeError('練習音源がテストを満たせない')
        while hi - lo > .1:
            mid = (lo + hi) / 2
            ok, result = passes(mid)
            if ok:
                lo, best = mid, result
            else:
                hi = mid
    y, checks, info = best
    if write:
        soundfile.write(str(path), y, 48000, subtype='PCM_16')
    return dict(target=round(lo, 2), **checks, **info)


def sources(folder):
    """(原音, loudness の名前, 再生用の名前)。build_loudness.py と同じ決まり(audio.* と audio_<難易度>.*)。"""
    found = []
    main = next((folder / n for n in AUDIO_NAMES if (folder / n).exists()), None)
    if main is not None:
        found.append((main, 'loudness.json', 'master.ogg'))
    variants = {}
    for path in sorted(folder.glob('audio_*.*')):
        suffix = path.stem[len('audio'):].lower()
        if path.suffix.lower() in ('.ogg', '.wav', '.mp3') and suffix not in variants:
            variants[suffix] = path
    for suffix in sorted(variants):
        found.append((variants[suffix], f'loudness{suffix}.json', f'master{suffix}.ogg'))
    return found


REPORT = Path(__file__).with_name('master_report.json')


def v25_input(folder, source, metadata):
    """v2.5 で鳴っていた音源と倍率(loudness.json の playbackFile があればそちら)。"""
    playback = metadata.get('playbackFile')
    if playback and (folder / playback).exists():
        return folder / playback, float(metadata.get('gainDb', 0))
    return source, float(metadata.get('gainDb', 0))


def v25_from_report(folder, source):
    """作り直すとき(loudness がもう master を指している)は、v2.5 の測定を前の master_report.json から読む。"""
    for row in json.loads(REPORT.read_text(encoding='utf8')) if REPORT.exists() else []:
        if row['songId'] == folder.name and row['source'] == source.name:
            return row['v25Lufs'], row['v25TruePeakDbtp'], row['v25FlattenedSamples'], row['v25MaxCutDb']
    raise RuntimeError(f'{folder.name}/{source.name}: v2.5 の測定が {REPORT.name} に無い')


def build_one(tools, folder, source, metadata_name, playback_name, output, apply, louder=None):
    global HIGHPASS_HZ, SOFTCLIP_MARGIN_DB
    ffmpeg, ffprobe, ffmpeg_vorbis = tools
    if louder is not None:
        # 処理の子プロセスごとに設定する(Windows の子プロセスは、親の書き換えた値を引き継がない)。
        HIGHPASS_HZ, SOFTCLIP_MARGIN_DB = louder['highpassHz'], louder['softclipMarginDb']
    metadata = json.loads((folder / metadata_name).read_text(encoding='utf8'))
    assert metadata.get('sourceSha256') == sha(source), f'{folder.name}: loudness が今の原音のものではない'

    if str(metadata.get('playbackFile', '')).startswith('master'):
        v25_lufs, v25_tp, flattened, v25_cut = v25_from_report(folder, source)
    else:
        old_path, old_gain = v25_input(folder, source, metadata)
        old, old_rate = decode(ffmpeg, ffprobe, old_path)
        v25, flattened, v25_cut = simulate_v25(old, old_rate, old_gain)
        v25_lufs, _, v25_tp = ffmpeg_measure(ffmpeg, v25, old_rate)
        del v25, old

    x, rate = decode(ffmpeg, ffprobe, source)
    source_lufs, source_lra, _ = ffmpeg_measure(ffmpeg, x, rate)
    prep = Prepared(x, rate)
    dest = output / folder.name
    dest.mkdir(parents=True, exist_ok=True)
    ogg = dest / playback_name
    # offset は見積もりと ffmpeg の測定の差(ずれたら上げ幅を探し直す)。ogg のはみ出し(密な曲でサンプル +1 dB ほど)は、
    # その場所だけ prep.scale を掛け重ねて下げ、作り直す(全体の天井を下げると、続く大きい音で届く大きさが下がり、下げ幅が増える)。
    offset, ceiling = 0.0, CEILING_DBTP
    pick = (lambda off: choose_makeup(prep, ceiling, v25_lufs, off)) if louder is None else \
        (lambda off: louder_makeup(prep, ceiling, louder, off))
    makeup = pick(offset)
    for attempt in range(12):
        estimate, limit = prep.run(makeup, ceiling)
        encode_ogg(ffmpeg_vorbis, prep.output(makeup, limit), rate, ogg)
        decoded, decoded_rate = decode(ffmpeg, ffprobe, ogg)
        out_lufs, out_lra, out_tp = ffmpeg_measure(ffmpeg, decoded, decoded_rate)
        sample_max = float(np.abs(decoded).max())
        measured_offset = out_lufs - estimate
        offset_ok = abs(measured_offset - offset) <= .1
        peak_ok = out_tp <= MAX_TRUE_PEAK_DBTP and sample_max <= MAX_SAMPLE
        print(f'  {folder.name}/{source.name} {attempt + 1}: 上げ {makeup:.2f} 見積もり {estimate:.2f} 測定 {out_lufs:.2f} '
              f'真のピーク {out_tp:.2f} 最大 {sample_max:.3f} 差 {measured_offset:+.2f}', flush=True)
        if offset_ok and peak_ok:
            break
        if not peak_ok:
            over = np.maximum(true_peak_envelope(decoded) / 10 ** ((MAX_TRUE_PEAK_DBTP - .15) / 20),
                              np.abs(decoded).max(axis=1) / (MAX_SAMPLE - .005))
            prep.scale *= np.maximum(over * 1.01, 1.0)
        if not offset_ok:
            offset = measured_offset
            makeup = pick(offset)
    else:
        raise RuntimeError(f'{folder.name}/{source.name}: 大きさ {out_lufs} / 真のピーク {out_tp} / 最大 {sample_max} が外れた')
    assert decoded_rate == rate and decoded.shape == x.shape, (folder.name, decoded.shape, x.shape)
    info = dict(makeupDb=round(makeup, 2), estimateLufs=round(estimate, 2), **reduction_stats(limit),
                attempts=attempt + 1, locallyLoweredSamples=int(np.count_nonzero(prep.scale > 1)))
    if louder is not None:
        info.update(louder=louder, softclippedShare=round(prep.clipped_share, 4), speakerLufs=round(speaker_lufs(decoded, rate), 2))

    duration = len(x) / rate
    lags = [derivative_lag(x, decoded, rate, duration * f) for f in (.15, .35, .55, .75, .9)]
    assert all(lag == 0 for lag, _ in lags), (folder.name, lags)

    profile = dict(version=1, sourceFile=source.name, sourceSha256=metadata['sourceSha256'],
                   targetLufs=round(out_lufs, 1),
                   inputLufs=metadata['inputLufs'], inputTruePeakDbtp=metadata['inputTruePeakDbtp'],
                   gainDb=0, playbackFile=playback_name, playbackSha256=sha(ogg),
                   outputLufs=round(out_lufs, 2), outputTruePeakDbtp=round(out_tp, 2))
    (dest / metadata_name).write_text(json.dumps(profile, ensure_ascii=False, indent=2) + '\n', encoding='utf8')
    report = dict(songId=folder.name, source=source.name, rate=rate, frames=len(x),
                  sourceLufs=source_lufs, sourceLra=source_lra,
                  v25Lufs=v25_lufs, v25TruePeakDbtp=v25_tp, v25FlattenedSamples=flattened, v25MaxCutDb=round(v25_cut, 2),
                  targetLufs=profile['targetLufs'], outputLufs=out_lufs, outputLra=out_lra, outputTruePeakDbtp=out_tp,
                  outputSampleMax=round(sample_max, 4), ceilingDbtp=round(ceiling, 2), **info,
                  lags=[dict(lag=lag, correlation=round(c, 4)) for lag, c in lags], oggBytes=ogg.stat().st_size)
    print(f'{folder.name}/{source.name}: v2.5 {v25_lufs:.1f} LUFS (削れ {flattened}) -> {out_lufs:.1f} LUFS, '
          f'{out_tp:.2f} dBTP, max {sample_max:.3f}, 下げ 95% {info["limitP95Db"]} dB, 時間差 {[l for l, _ in lags]}', flush=True)
    if apply:
        shutil.copyfile(ogg, folder / playback_name)
        shutil.copyfile(dest / metadata_name, folder / metadata_name)
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--songs', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--ffmpeg', default='ffmpeg', help='デコードと測定')
    parser.add_argument('--ffprobe', default='ffprobe')
    parser.add_argument('--ffmpeg-vorbis', default=None, help='libvorbis の入った ffmpeg(省略時は --ffmpeg)')
    parser.add_argument('--only', nargs='*', default=None, help='曲フォルダーの名前だけ')
    parser.add_argument('--skip', nargs='*', default=['NewSong'], help='作らない曲(NewSong はテンプレート)')
    parser.add_argument('--apply', action='store_true', help='確かめたあと曲フォルダーへ master*.ogg と loudness*.json を置く')
    parser.add_argument('--tutorial', type=Path, default=None, help='練習音源の wav(曲は作らず、これだけ処理する)')
    parser.add_argument('--tutorial-target', type=float, default=TUTORIAL_TARGET_LUFS)
    parser.add_argument('--uniform-target', type=float, default=None,
                        help='大きい版: 全曲をこの大きさ(LUFS)へ上げる(v2.5 の決まりを使わない。--gr95-cap で止める)')
    parser.add_argument('--gr95-cap', type=float, default=10.0, help='大きい版: リミッターの下げ幅(95%% の時間)の上限 dB')
    parser.add_argument('--highpass-hz', type=float, default=HIGHPASS_HZ, help='大きい版: ハイパスの周波数')
    parser.add_argument('--softclip-margin-db', type=float, default=None, help='大きい版: 曲全体 + この dB を超える山の角を丸める')
    parser.add_argument('--workers', type=int, default=2, help='同時に処理する曲の数(長い曲は1本で数 GB 使う。メモリが足りなければ 1)')
    args = parser.parse_args()
    louder = None
    if args.uniform_target is not None:
        if args.apply:
            parser.error('大きい版は候補フォルダーにだけ作る(--apply と一緒に使わない)')
        louder = dict(target=args.uniform_target, gr95Cap=args.gr95_cap, highpassHz=args.highpass_hz,
                      softclipMarginDb=args.softclip_margin_db)
    if args.tutorial is not None:
        result = build_tutorial(args.tutorial, args.tutorial_target, args.apply)
        args.output.mkdir(parents=True, exist_ok=True)
        (args.output / 'tutorial_report.json').write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n', encoding='utf8')
        raise SystemExit(0)
    tools = (args.ffmpeg, args.ffprobe, args.ffmpeg_vorbis or args.ffmpeg)
    folders = sorted(p for p in args.songs.iterdir() if p.is_dir() and p.name not in args.skip
                     and (args.only is None or p.name in args.only))
    args.output.mkdir(parents=True, exist_ok=True)
    jobs = [(tools, folder, source, metadata_name, playback_name, args.output, args.apply, louder)
            for folder in folders for source, metadata_name, playback_name in sources(folder) if (folder / metadata_name).exists()]
    with concurrent.futures.ProcessPoolExecutor(max_workers=max(1, args.workers)) as pool:
        reports = list(pool.map(build_one, *zip(*jobs)))
    text = json.dumps(reports, ensure_ascii=False, indent=2) + '\n'
    (args.output / 'master_report.json').write_text(text, encoding='utf8')
    if args.apply and args.only is None:
        REPORT.write_text(text, encoding='utf8')
