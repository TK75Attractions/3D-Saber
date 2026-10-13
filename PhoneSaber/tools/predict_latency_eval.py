#!/usr/bin/env python3
"""Causal endpoint forecast sweep. Detector trajectories are proxies, not physical truth.

Only numeric metadata is read. Derived recordings/reports must stay in TMPDIR.
No interpolation crosses a missing detection, predicted frame or >50ms truth gap.
"""

import argparse
from collections import defaultdict
import json
import math
from pathlib import Path

import numpy as np

from endpoint_prediction_eval import LegacyPredictor, f32
from real_motion_eval import load_segments


class Candidate(LegacyPredictor):
    """Offline candidates; constants never affect phone recognition."""

    def __init__(self, kind):
        super().__init__()
        self.kind = kind
        self.warmup_samples = 3 if kind == 'guarded' else 2
        self.history = []
        self.position = None
        self.gh_velocity = np.zeros(4)
        self.acceleration = np.zeros(4)

    def add(self, timestamp, endpoints):
        if self.count and timestamp <= self.time:
            return
        gap = timestamp - self.time
        if not self.count or gap > .1:
            self.history = []
            self.position = np.asarray(endpoints)
            self.gh_velocity = np.zeros(4)
            self.acceleration = np.zeros(4)
        old_velocity = np.asarray(getattr(self, 'previous_velocity', (0,) * 4))
        super().add(timestamp, endpoints)
        self.history = (self.history + [(timestamp, self.latest)])[-3:]
        if len(self.history) < 2:
            return
        current = np.asarray(self.previous_velocity)
        if self.kind == 'cv':
            self.velocity = current
        elif self.kind == 'gh':
            prior = self.position + self.gh_velocity * gap
            residual = np.asarray(self.latest) - prior
            self.position = prior + .85 * residual
            self.gh_velocity += .3 * residual / gap
            self.velocity = self.gh_velocity
        elif self.kind == 'acceleration':
            self.acceleration = .5 * self.acceleration + .5 * (current - old_velocity) / gap
            self.velocity = current
        elif self.kind in ('adaptive', 'guarded'):
            # 3点が揃うまでは、復帰直後の1区間のジャンプを延長しない。
            self.velocity = tuple(self.velocity) if len(self.history) == 3 else (0,) * 4
            if len(self.history) == 3:
                for i in (0, 2):
                    u, v = old_velocity[i:i+2], current[i:i+2]
                    speed, previous_speed = np.linalg.norm(v), np.linalg.norm(u)
                    if self.kind == 'adaptive':
                        direction = max(0, float(u @ v) / max(speed * previous_speed, 1e-20))
                        gain = direction * min(1, max(0, (speed - .25) / 1.75))
                    else:
                        gain = 1
                    self.velocity = tuple(x * gain if i <= j < i+2 else x for j, x in enumerate(self.velocity))

    def predict(self, now, horizon_ms):
        if self.kind == 'acceleration' and self.count >= 2:
            velocity = self.velocity
            # 加速度は将来ほど減衰。速度候補も端点ごとに10 world/sに制限。
            self.velocity = tuple(v + a * .03 * (1 - math.exp(-horizon_ms / 30))
                                  for v, a in zip(velocity, self.acceleration))
            result = self._clamped_predict(now, horizon_ms)
            self.velocity = velocity
            return result
        return self._clamped_predict(now, horizon_ms)

    def _clamped_predict(self, now, horizon_ms):
        if self.kind == 'guarded':
            return super().predict(now, horizon_ms)
        velocity = self.velocity
        limited = []
        for i in (0, 2):
            speed = math.hypot(*velocity[i:i+2])
            limited.extend(f32(v * min(1, 10 / max(speed, 1e-20))) for v in velocity[i:i+2])
        if self.count < 2 or now - self.time >= .1:
            return self.latest
        age = max(0, now-self.time)
        decay = min(1, (.1-age)/(.1-1/30))
        result = []
        for i in (0, 2):
            delta = np.asarray(limited[i:i+2]) * horizon_ms / 1000 * decay
            if self.kind == 'gh':
                delta += self.position[i:i+2] - self.latest[i:i+2]
            delta *= min(1, .35 / max(float(np.linalg.norm(delta)), 1e-20))
            result.extend(f32(p+d) for p, d in zip(self.latest[i:i+2], delta))
        return tuple(result)


def distribution(values):
    values = np.concatenate(values) if values else np.asarray([])
    if not len(values):
        return dict(n=0, median=None, p90=None, max=None, rms=None)
    return dict(n=len(values), median=float(np.median(values)), p90=float(np.quantile(values, .9)),
                max=float(np.max(values)), rms=float(np.sqrt(np.mean(values ** 2))))


def evaluate(path, known=False, horizons=tuple(range(20, 121, 10)), kinds=None):
    kinds = kinds or ('hold', 'current', 'cv', 'gh', 'acceleration', 'adaptive', 'guarded')
    segments, inventory = load_segments(path, allow_unknown_prediction=not known, minimum_samples=1)
    errors, overshoot, jitter = defaultdict(list), defaultdict(list), defaultdict(list)
    cadence = defaultdict(int)
    segment_counts = defaultdict(int)
    previous_end = {}
    predictors = {}
    for session, color, samples in segments:
        key = (session, color)
        if key not in predictors:
            predictors[key] = {kind: LegacyPredictor() if kind == 'current' else Candidate(kind) for kind in kinds}
        t = np.asarray([s.capture for s in samples])
        raw = np.asarray([s.endpoints for s in samples])
        if len(t) < 2:
            for predictor in predictors[key].values():
                predictor.add(float(t[0]), tuple(raw[0]))
            previous_end[key] = t[-1]
            cadence['single_sample'] += 1
            continue
        hz = 1 / float(np.median(np.diff(t)))
        rate = '60fps' if 50 <= hz <= 70 else '30fps' if 25 <= hz <= 35 else 'other'
        cadence[rate] += len(t)
        segment_counts[rate] += 1
        after_gap = (session, color) in previous_end
        previous_end[session, color] = t[-1]
        # 分類ラベルだけは未来を使う。予測器へ渡す入力は時刻順の現在点だけ。
        velocity = np.diff(raw, axis=0) / np.diff(t)[:, None]
        speed = np.zeros((len(t), 2))
        speed[1:] = np.linalg.norm(velocity.reshape(-1, 2, 2), axis=2)
        reversal = np.zeros((len(t), 2), dtype=bool)
        if len(t) > 2:
            dots = np.sum(velocity[1:].reshape(-1, 2, 2) * velocity[:-1].reshape(-1, 2, 2), axis=2)
            for index, endpoint in zip(*np.where(dots < 0)):
                reversal[np.abs(t - t[index+1]) <= .1, endpoint] = True
        masks = dict(all=np.ones_like(reversal), slow=speed <= .25, swing=speed >= 2,
                     reversal=reversal, after_dropout=np.repeat((t - t[0] <= .1)[:, None], 2, axis=1) & after_gap)
        # 静止proxyはOFFから固定した非重複15点窓。各候補で選び直さない。
        windows = []
        for start in range(0, len(t)-14, 15):
            sl = slice(start, start+15)
            times = t[sl] - t[sl].mean()
            slope = times @ raw[sl] / (times @ times)
            residual = raw[sl] - raw[sl].mean(axis=0) - times[:, None] * slope
            if (np.max(np.linalg.norm(slope.reshape(2, 2), axis=1)) <= .25 and
                    np.max(np.sqrt(np.mean(np.sum(residual.reshape(-1, 2, 2)**2, axis=2), axis=0))) <= .15):
                windows.append((sl, times))
        low, high = raw.copy(), raw.copy()
        for i, time in enumerate(t):
            local = raw[np.searchsorted(t, time-.1):np.searchsorted(t, time+.1, side='right')]
            low[i], high[i] = local.min(axis=0), local.max(axis=0)
        outputs = {}
        for kind in kinds:
            predictor = predictors[key][kind]
            rows = {h: [] for h in horizons}
            for time, point in zip(t, raw):
                predictor.add(float(time), tuple(point))
                for h in horizons:
                    rows[h].append(point if kind == 'hold' else predictor.predict(float(time), h))
            outputs[kind] = {h: np.asarray(rows[h]) for h in horizons}
        # 各L内の全候補は同じ評価時刻。短い60Hz窓もL別に利用する。
        # 現行の60ms上限は維持して120ms目標とも比較。
        for h in horizons:
            valid = t + h/1000 <= t[-1]
            truth = np.column_stack([np.interp(t+h/1000, t, raw[:, axis]) for axis in range(4)])
            for kind in kinds:
                output = outputs[kind][h]
                extra = np.maximum(low-output, np.maximum(output-high, 0))
                overshoot[rate, h, kind].append(np.linalg.norm(extra.reshape(-1, 2, 2), axis=2)[reversal & valid[:, None]])
                direct = np.sum((output-truth)**2, axis=1)
                swapped = truth[:, [2, 3, 0, 1]]
                target = np.where((np.sum((output-swapped)**2, axis=1) < direct)[:, None], swapped, truth)
                error = np.linalg.norm((output-target).reshape(-1, 2, 2), axis=2)
                for group, mask in masks.items():
                    errors[rate, h, kind, group].append(error[mask & valid[:, None]])
                for sl, times in windows:
                    points = outputs[kind][h][sl]
                    slope = times @ points / (times @ times)
                    residual = points - points.mean(axis=0) - times[:, None] * slope
                    jitter[rate, h, kind].append(np.linalg.norm(residual.reshape(-1, 2, 2), axis=2).ravel())
    return dict(schema=1, units='world XY (assumed 11x6 mapping)', known_prediction_only=known,
                truth='interpolated detector output; no physical ground truth', inventory=inventory,
                cadence=dict(cadence), segments=dict(segment_counts),
                results=[dict(fps=rate, lead_ms=h, predictor=kind, group=group, **distribution(v))
                         for (rate, h, kind, group), v in sorted(errors.items())],
                reversal_overshoot=[dict(fps=rate, lead_ms=h, predictor=kind, **distribution(v))
                                    for (rate, h, kind), v in sorted(overshoot.items())],
                rest_jitter=[dict(fps=rate, lead_ms=h, predictor=kind, **distribution(v))
                             for (rate, h, kind), v in sorted(jitter.items())])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--real', required=True, type=Path)
    parser.add_argument('--json', required=True, type=Path)
    parser.add_argument('--require-known-prediction', action='store_true')
    parser.add_argument('--leads', default=','.join(map(str, range(20, 121, 10))))
    args = parser.parse_args()
    leads = tuple(int(h) for h in args.leads.split(','))
    if not leads or any(h < 20 or h > 120 for h in leads):
        parser.error('leads must be in 20..120 ms')
    report = evaluate(args.real, args.require_known_prediction, leads)
    args.json.write_text(json.dumps(report, indent=2, allow_nan=False) + '\n')
    print(json.dumps(dict(cadence=report['cadence'], segments=report['segments'], results=len(report['results']))))


if __name__ == '__main__':
    main()
