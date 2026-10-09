#!/usr/bin/env python3
"""One Euro grid on recorded detector endpoints, not physical truth or network latency.

Full private inputs/results stay outside Git. Uses numpy only for offline metrics.
"""
import argparse
import itertools
import json
import math
from pathlib import Path

import numpy as np

from real_motion_eval import load_segments
from endpoint_prediction_eval import f32


def distance(a, b):
    dx, dy = a[0] - b[0], a[1] - b[1]
    return math.sqrt(dx * dx + dy * dy)


def match(previous, current):
    # 距離の和。同点は受信順を維持する。
    direct = distance(previous[:2], current[:2]) + distance(previous[2:], current[2:])
    swapped = distance(previous[:2], current[2:]) + distance(previous[2:], current[:2])
    return current[2:] + current[:2] if swapped < direct else current


def alpha(cutoff, dt):
    return 1.0 / (1.0 + 1.0 / (2.0 * math.pi * cutoff * dt))


class EndpointFilter:
    """Independent scalar One Euro axes; derivative uses previous raw coordinate."""
    def __init__(self, min_cutoff, beta, d_cutoff, correction=True):
        self.minimum, self.beta, self.derivative_cutoff = min_cutoff, beta, d_cutoff
        self.correction = correction
        self.reset()

    def reset(self):
        self.time = None

    def apply(self, time, points, enabled=True):
        if not enabled:
            self.reset()
            return points  # OFF は並べ替えも算術も行わない。
        if not math.isfinite(time) or not all(math.isfinite(v) for v in points):
            self.reset()
            return points
        if self.time is not None and time <= self.time:
            return self.filtered
        if self.time is None or time - self.time > 0.1:
            self.time, self.raw, self.filtered = time, points, points
            self.derivative = (0.0,) * 4
            return points
        if self.correction:
            points = match(self.raw, points)
        dt = time - self.time
        da = alpha(self.derivative_cutoff, dt)
        derivative, filtered = [], []
        for value, prior_raw, prior_filtered, prior_d in zip(points, self.raw, self.filtered, self.derivative):
            d = prior_d + da * ((value - prior_raw) / dt - prior_d)
            a = alpha(self.minimum + self.beta * abs(d), dt)
            derivative.append(d)
            filtered.append(prior_filtered + a * (value - prior_filtered))
        self.time, self.raw = time, points
        self.derivative, self.filtered = tuple(derivative), tuple(filtered)
        return self.filtered


def aligned(raw):
    result = [raw[0]]
    for points in raw[1:]:
        result.append(match(result[-1], points))
    return np.asarray(result)


def correspondence(output, reference):
    direct = np.linalg.norm((output - reference).reshape(-1, 2, 2), axis=2).sum(axis=1)
    reverse = output[:, [2, 3, 0, 1]]
    swapped = np.linalg.norm((reverse - reference).reshape(-1, 2, 2), axis=2).sum(axis=1)
    return np.where((swapped < direct)[:, None], reverse, output)


def rms(values):
    return float(np.sqrt(np.mean(np.square(values)))) if len(values) else None


def p95(values):
    return float(np.quantile(values, .95)) if len(values) else None


def prepare(path, known=False):
    segments, inventory = load_segments(path, allow_unknown_prediction=not known)
    prepared = []
    for session, color, samples in segments:
        t = np.asarray([s.capture for s in samples])
        raw = [s.endpoints for s in samples]
        ref = aligned(raw)
        # 静止 proxy: 連続15点(約0.47秒)の線形傾向が両端0.25 world/s以下、
        # 残差RMSが両端0.15以下。OFFだけで固定し、設定ごとに窓を選び直さない。
        windows = []
        for start in range(0, len(t) - 14, 15):
            sl = slice(start, start + 15)
            times = t[sl] - np.mean(t[sl])
            slope = times @ ref[sl] / (times @ times)
            residual = ref[sl] - np.mean(ref[sl], axis=0) - times[:, None] * slope
            if (np.max(np.linalg.norm(slope.reshape(2, 2), axis=1)) <= .25 and
                    np.max(np.sqrt(np.mean(np.sum(residual.reshape(-1, 2, 2)**2, axis=2), axis=0))) <= .15):
                windows.append((sl, times))
        # 速い区間: 前後3点(約200ms)のchord速度が2 world/s以上。
        velocity = np.zeros_like(ref)
        if len(t) > 6:
            velocity[3:-3] = (ref[6:] - ref[:-6]) / (t[6:] - t[:-6])[:, None]
        speed = np.linalg.norm(velocity.reshape(-1, 2, 2), axis=2)
        fast = speed >= 2.0
        fast[:3] = False
        fast[-3:] = False
        futures = {}
        for h in (0, 20, 40):
            target = t + h / 1000
            truth = np.column_stack([np.interp(target, t, ref[:, axis]) for axis in range(4)])
            futures[h] = (truth, fast & (target <= t[-1])[:, None])
        # 現在の前後100msで観測された極値からの追加はみ出し。
        low, high = ref.copy(), ref.copy()
        for i, time in enumerate(t):
            local = ref[np.searchsorted(t, time - .1):np.searchsorted(t, time + .1, side='right')]
            low[i], high[i] = local.min(axis=0), local.max(axis=0)
        prepared.append(dict(session=session, color=color, t=t, raw=raw, ref=ref,
                             windows=windows, velocity=velocity.reshape(-1, 2, 2), fast=fast,
                             futures=futures, low=low, high=high))
    return prepared, inventory


def measure(prepared, parameters=None, correction=True, float32_io=False):
    jitter, lag, overshoot = [], [], []
    errors = {h: [] for h in (0, 20, 40)}
    series_jitter = {}
    for seg in prepared:
        if parameters is None:
            output = seg['ref']
        else:
            filt = EndpointFilter(*parameters, correction=correction)
            output = np.asarray([tuple(f32(v) for v in filt.apply(t, tuple(f32(v) for v in p)))
                                 if float32_io else filt.apply(t, p) for t, p in zip(seg['t'], seg['raw'])])
            output = correspondence(output, seg['ref'])
        key = seg['session'] + '/' + seg['color']
        local_jitter = []
        for sl, times in seg['windows']:
            points = output[sl]
            slope = times @ points / (times @ times)
            residual = points - points.mean(axis=0) - times[:, None] * slope
            local_jitter.extend(np.linalg.norm(residual.reshape(-1, 2, 2), axis=2).ravel())
        jitter.extend(local_jitter)
        series_jitter.setdefault(key, []).extend(local_jitter)
        delta = (seg['ref'] - output).reshape(-1, 2, 2)
        v = seg['velocity']
        squared_speed = np.sum(v * v, axis=2)
        projection = np.sum(delta * v, axis=2) / np.maximum(squared_speed, 1e-20) * 1000
        lag.extend(projection[seg['fast']])
        extra = np.maximum(seg['low'] - output, np.maximum(output - seg['high'], 0))
        overshoot.extend(np.linalg.norm(extra.reshape(-1, 2, 2), axis=2)[seg['fast']])
        for h, (truth, mask) in seg['futures'].items():
            matched = correspondence(output, truth)
            error = np.linalg.norm((matched - truth).reshape(-1, 2, 2), axis=2)
            errors[h].extend(error[mask])
    return dict(parameters=list(parameters) if parameters is not None else None, correction=correction, static_count=len(jitter),
                static_jitter_rms=rms(jitter), fast_count=len(lag),
                fast_lag_mean_ms=float(np.mean(lag)) if len(lag) else None,
                fast_lag_p95_ms=p95(lag), fast_rmse={str(h): rms(e) for h, e in errors.items()},
                overshoot_p95=p95(overshoot), overshoot_max=float(max(overshoot)) if len(overshoot) else None,
                series_static_jitter={k: rms(v) for k, v in sorted(series_jitter.items()) if v})


def pareto(results):
    # 静止残差、H20 RMSE、遅れp95の3目的。過度な遅れはfront外でも除外しない。
    def objectives(row):
        return row['static_jitter_rms'], row['fast_rmse']['20'], row['fast_lag_p95_ms']
    front = []
    for i, row in enumerate(results):
        a = objectives(row)
        if any(all(x <= y for x, y in zip(objectives(other), a)) and
               any(x < y for x, y in zip(objectives(other), a)) for j, other in enumerate(results) if i != j):
            continue
        front.append(i)
    return front


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--real', required=True, type=Path)
    parser.add_argument('--include-report', type=Path, action='append', default=[], help='merge a previous sweep on the identical baseline before Pareto selection')
    parser.add_argument('--json', required=True, type=Path)
    parser.add_argument('--min-cutoffs', default='0.5,1,2,4')
    parser.add_argument('--betas', default='0,1,5,10,20,50')
    parser.add_argument('--d-cutoffs', default='0.5,1,5,10')
    parser.add_argument('--require-known-prediction', action='store_true')
    parser.add_argument('--float32-io', action='store_true', help='round world inputs/outputs like C#, keeping internal double state')
    parser.add_argument('--presets', action='store_true', help='evaluate weak (1,10,10) and medium (0.25,7,60) only')
    args = parser.parse_args()
    grid = [list(map(float, value.split(','))) for value in (args.min_cutoffs, args.betas, args.d_cutoffs)]
    if any(x <= 0 for x in grid[0] + grid[2]) or any(x < 0 for x in grid[1]):
        parser.error('cutoffs > 0, beta >= 0 required')
    data, inventory = prepare(args.real, args.require_known_prediction)
    baseline = measure(data)
    results = []
    parameters_to_try = ((1, 10, 10), (.25, 7, 60)) if args.presets else itertools.product(*grid)
    for parameters in parameters_to_try:
        for correction in (False, True):
            results.append(measure(data, parameters, correction, args.float32_io))
    for path in args.include_report:
        prior = json.loads(path.read_text())
        if prior['baseline'] != baseline or prior['inventory'] != inventory:
            raise ValueError('included sweep has a different baseline/inventory')
        if prior.get('float32_io', False) != args.float32_io:
            raise ValueError('included sweep has different float precision')
        for row in prior['results']:
            if not any(r['parameters'] == row['parameters'] and r['correction'] == row['correction'] for r in results):
                results.append(row)
    front = pareto(results) if baseline['static_count'] and baseline['fast_count'] else []
    report = dict(schema=1, units='11x6 world mapping; capture-relative milliseconds',
                  known_prediction_only=args.require_known_prediction, float32_io=args.float32_io,
                  segments=len(data), valid_samples=sum(len(s['t']) for s in data),
                  static_windows=sum(len(s['windows']) for s in data),
                  static_series=len({(s['session'], s['color']) for s in data if s['windows']}),
                  corrected_order_samples=sum(int(np.sum(np.any(np.asarray(s['raw']) != s['ref'], axis=1))) for s in data),
                  inventory=inventory, baseline=baseline, results=results, pareto_indices=front)
    args.json.write_text(json.dumps(report, indent=2, allow_nan=False) + '\n')
    print(f'segments={len(data)} static_endpoints={baseline["static_count"]} fast_endpoints={baseline["fast_count"]} settings={len(results)} front={len(front)}')
    print('OFF:', json.dumps({k:v for k,v in baseline.items() if k != 'series_static_jitter'}))
    # 大幅改善の比較用目安: jitter25%以上低減、平均遅れ<=5ms/p95<=10ms、H20悪化<=2%。
    for i in front:
        r = results[i]
        if (r['correction'] and r['static_jitter_rms'] <= baseline['static_jitter_rms'] * .75 and
                r['fast_lag_mean_ms'] <= 5 and r['fast_lag_p95_ms'] <= 10 and
                r['fast_rmse']['20'] <= baseline['fast_rmse']['20'] * 1.02):
            print('ambitious_target', i, json.dumps({k:v for k,v in r.items() if k != 'series_static_jitter'}))


if __name__ == '__main__':
    main()
