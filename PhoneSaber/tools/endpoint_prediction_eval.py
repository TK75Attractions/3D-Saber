#!/usr/bin/env python3
"""Deterministic offline replay of PhoneSaberEndpointPredictor (world XY units)."""

import argparse
import bisect
import csv
import json
import math
import random
import struct
from dataclasses import dataclass
from pathlib import Path

HORIZONS_MS = (0, 20, 40, 60)
MAX_GAP = 0.1
DECAY_START = 1 / 30
MAX_DISPLACEMENT = 0.35


def f32(value):
    """Round each Unity float operation separately; no reassociation / FMA."""
    try:
        return struct.unpack("<f", struct.pack("<f", value))[0]
    except OverflowError:
        return math.copysign(math.inf, value)


def float_bits(value):
    return struct.unpack("<I", struct.pack("<f", value))[0]


def float_divide(value, denominator):
    if denominator == 0:
        return math.nan if value == 0 else math.copysign(math.inf, value * math.copysign(1, denominator))
    return f32(value / denominator)


class Predictor:
    """Same ordered, conservative two-interval estimate as the C# implementation.

    Timestamps use doubles and Vector2 operations round to float32 in C# order.
    Shared-vector tests compile the actual C# with a Vector2 stub (no Unity).
    No detector, payload, or recognition code runs here.
    """

    def __init__(self, maximum_displacement=MAX_DISPLACEMENT):
        maximum_displacement = f32(maximum_displacement)
        if not math.isfinite(maximum_displacement) or maximum_displacement < 0:
            raise ValueError("maximum_displacement must be finite and nonnegative")
        self.limit = f32(maximum_displacement)
        self.latest = (0.0,) * 4
        self.time = 0.0
        self.reset()

    def reset(self):
        self.count = 0

    def add(self, timestamp, endpoints):
        endpoints = tuple(f32(v) for v in endpoints)
        if len(endpoints) != 4:
            raise ValueError("expected ax, ay, bx, by")
        if not all(math.isfinite(v) for v in (timestamp, *endpoints)):
            return
        if self.count and timestamp <= self.time:
            return
        gap = timestamp - self.time
        if not self.count or gap > MAX_GAP:
            self.count = 1
            self.velocity = self.previous_velocity = (0.0,) * 4
        else:
            current = tuple(float_divide(f32(v - p), f32(gap)) for v, p in zip(endpoints, self.latest))
            self.velocity = current if self.count == 1 else tuple(
                self.conservative_axis(p, v) for p, v in zip(self.previous_velocity, current))
            self.previous_velocity = current
            self.count = 2
        self.latest = endpoints
        self.time = timestamp

    @staticmethod
    def conservative_axis(previous, current):
        if (not math.isfinite(previous) or not math.isfinite(current) or previous == 0 or current == 0 or
                (previous > 0) != (current > 0)):
            return 0.0
        if abs(current) >= abs(previous):
            return previous
        ratio = abs(float_divide(current, previous))
        return f32(current * ratio)

    def predict(self, now, horizon_ms):
        if horizon_ms <= 0 or self.count < 2 or not math.isfinite(now):
            return self.latest
        age = max(0.0, now - self.time)
        if age >= MAX_GAP:
            return self.latest
        decay = 1 if age <= DECAY_START else (MAX_GAP - age) / (MAX_GAP - DECAY_START)
        seconds = f32(min(horizon_ms, 60) * 0.001 * decay)
        result = []
        for i in (0, 2):
            if not all(math.isfinite(v) for v in self.velocity[i:i + 2]):
                result.extend(f32(v + 0.0) for v in self.latest[i:i + 2])
                continue
            dx, dy = f32(self.velocity[i] * seconds), f32(self.velocity[i + 1] * seconds)
            length = math.sqrt(dx * dx + dy * dy)
            if length > self.limit:
                factor = f32(self.limit / length)
                dx = f32(dx * factor)
                dy = f32(dy * factor)
            result.extend((f32(self.latest[i] + dx), f32(self.latest[i + 1] + dy)))
        return tuple(result)


class BaselinePredictor(Predictor):
    """Frozen 977d5c6 estimator for reproducible before/after real replays."""

    @staticmethod
    def conservative_axis(previous, current):
        if (not math.isfinite(previous) or not math.isfinite(current) or previous == 0 or current == 0 or
                (previous > 0) != (current > 0)):
            return 0.0
        return current if abs(current) < abs(previous) else previous


@dataclass(frozen=True)
class Sample:
    capture: float
    receive: float
    endpoints: tuple


def synthetic_position(path, t):
    phase = t / 1.2
    x = math.sin(2 * math.pi * phase) if path == "sinusoid" else 1 - 4 * abs((phase % 1) - 0.5)
    return (2.2 * x, -0.6, 3.0 * x, 0.6)


def synthetic_samples(path, hz, duration, jitter_ms, latency_ms, seed):
    rng = random.Random(seed)
    return [Sample(i / hz, i / hz + latency_ms / 1000 + rng.uniform(-jitter_ms, jitter_ms) / 1000,
                   synthetic_position(path, i / hz)) for i in range(int(duration * hz) + 1)]


def read_csv(path):
    """CSV is a truth trajectory; capture_s, receive_s, ax, ay, bx, by.

    Use ground truth endpoints, not detector output presented as ground truth.
    receive_s may include a constant latency plus per-sample delivery jitter.
    """
    with Path(path).open(newline="", encoding="utf-8") as stream:
        rows = list(csv.DictReader(stream))
    samples = [Sample(float(r["capture_s"]), float(r["receive_s"]),
                      tuple(float(r[k]) for k in ("ax", "ay", "bx", "by"))) for r in rows]
    if len(samples) < 3:
        raise ValueError("at least three samples are required")
    for i, sample in enumerate(samples):
        if not all(math.isfinite(v) for v in (sample.capture, sample.receive, *sample.endpoints)):
            raise ValueError("CSV values must be finite")
        if sample.receive < sample.capture:
            raise ValueError("receive_s must not precede capture_s")
        if i and (sample.capture <= samples[i - 1].capture or sample.receive <= samples[i - 1].receive):
            raise ValueError("capture_s and receive_s must strictly increase")
    return samples


def interpolated_truth(samples):
    times = [s.capture for s in samples]

    def truth(t):
        if t < times[0] or t > times[-1]:
            return None
        i = min(bisect.bisect_right(times, t), len(samples) - 1)
        if i == 0:
            return samples[0].endpoints
        first, second = samples[i - 1], samples[i]
        alpha = (t - first.capture) / (second.capture - first.capture)
        return tuple(a + (b - a) * alpha for a, b in zip(first.endpoints, second.endpoints))

    return truth


def reversals(samples):
    # X/Y の符号反転を端点別に順序どおり検出。CSV ではサンプル分解能の折り返し。
    result = []
    for i in range(1, len(samples) - 1):
        for axis in range(4):
            before = samples[i].endpoints[axis] - samples[i - 1].endpoints[axis]
            after = samples[i + 1].endpoints[axis] - samples[i].endpoints[axis]
            if before * after < 0:
                result.append((samples[i].capture, axis, samples[i].endpoints[axis], 1 if before > 0 else -1))
    return result


def rms(values):
    return math.sqrt(sum(v * v for v in values) / len(values)) if values else None


def percentile(values, fraction):
    return sorted(values)[max(0, math.ceil(len(values) * fraction) - 1)] if values else None


def evaluate(samples, truth, render_hz=30):
    if render_hz <= 0:
        raise ValueError("render_hz must be positive")
    turns = reversals(samples)
    # 描画の各フレームではその時点の最新 packet だけを適用（Unity と同じ間引き）。
    output = []
    for horizon in HORIZONS_MS:
        predictor = Predictor()
        future_errors, baseline_errors, live_errors, overshoots = [], [], [], []
        applied_samples = 0
        previous_capture = None
        latest = None
        index = 0
        for frame in range(math.ceil(samples[0].receive * render_hz),
                           math.ceil(samples[-1].receive * render_hz) + 1):
            now = frame / render_hz
            while index < len(samples) and samples[index].receive <= now:
                latest = samples[index]
                index += 1
            if latest is None:
                continue
            predictor.add(latest.receive, latest.endpoints)
            predicted = predictor.predict(now, horizon)
            target = truth(latest.capture + horizon / 1000)
            live = truth(now)
            if target is None:
                continue
            if latest.capture != previous_capture:
                applied_samples += 1
                previous_capture = latest.capture
            for endpoint in (0, 2):
                future_errors.append(math.hypot(predicted[endpoint] - target[endpoint],
                                                predicted[endpoint + 1] - target[endpoint + 1]))
                baseline_errors.append(math.hypot(latest.endpoints[endpoint] - target[endpoint],
                                                  latest.endpoints[endpoint + 1] - target[endpoint + 1]))
                if live is not None:
                    live_errors.append(math.hypot(predicted[endpoint] - live[endpoint],
                                                  predicted[endpoint + 1] - live[endpoint + 1]))
            # 折り返しの前後100 msで、極値を外へ超えた距離を測る（未来誤差とは別）。
            for turn_time, axis, extreme, direction in turns:
                if abs(latest.capture - turn_time) <= 0.1:
                    overshoots.append(max(0.0, direction * (predicted[axis] - extreme)))
        output.append(dict(horizon_ms=horizon, endpoint_observations=len(future_errors),
                           applied_samples=applied_samples, future_rmse=rms(future_errors),
                           off_future_rmse=rms(baseline_errors), future_p95=percentile(future_errors, 0.95),
                           live_rmse=rms(live_errors), reversal_overshoot_max=max(overshoots, default=0),
                           reversal_overshoot_p95=percentile(overshoots, 0.95),
                           reversal_observations=len(overshoots)))
    return output


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--csv", type=Path, help="truth trajectory CSV: capture_s,receive_s,ax,ay,bx,by")
    parser.add_argument("--real", type=Path, help="real_motion_extract CSV/JSON (detector reference, not physical truth)")
    parser.add_argument("--horizons", default=",".join(str(h) for h in range(61)), help="real replay horizons, default every integer 0..60")
    parser.add_argument("--estimators", default="baseline,current,ls3,adaptive", help="real replay estimators (ls3/adaptive are offline only)")
    parser.add_argument("--phases", default="0,0.25,0.5,0.75", help="render phase fractions for real replay")
    parser.add_argument("--world-width", type=float, default=11)
    parser.add_argument("--world-height", type=float, default=6)
    parser.add_argument("--max-truth-gap", type=float, default=0.05)
    parser.add_argument("--require-known-prediction", action="store_true")
    parser.add_argument("--align-endpoints", action="store_true", help="diagnostic only: causal nearest-end matching, unlike runtime")
    parser.add_argument("--render-hz", type=int, default=30)
    parser.add_argument("--duration", type=float, default=12)
    parser.add_argument("--jitter-ms", type=float, default=6)
    parser.add_argument("--latency-ms", type=float, default=140)
    parser.add_argument("--seed", type=int, default=20261009)
    parser.add_argument("--json", type=Path, help="optional output outside version control")
    args = parser.parse_args(argv)
    if args.real:
        if args.csv:
            parser.error("--real and --csv are mutually exclusive")
        from real_motion_eval import evaluate_real
        estimators = tuple(args.estimators.split(","))
        if any(name not in ("baseline", "current", "ls3", "adaptive") for name in estimators):
            parser.error("unknown estimator")
        report = evaluate_real(args.real, tuple(int(h) for h in args.horizons.split(",")), estimators,
                               phases=tuple(float(p) for p in args.phases.split(",")), latency_ms=args.latency_ms,
                               world_width=args.world_width, world_height=args.world_height,
                               max_truth_gap=args.max_truth_gap, allow_unknown_prediction=not args.require_known_prediction,
                               align_endpoints=args.align_endpoints)
        if args.json:
            args.json.write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
        print(report["limitation"])
        print("render Hz estimator H future RMSE OFF same target live RMSE added reversal max")
        for result in report["results"]:
            def value(key):
                number = result[key]
                return f"{number:.6f}" if number is not None else "n/a"
            print(f"{result['render_hz']:2} {result['estimator']:8} {result['horizon_ms']:2} "
                  f"{value('future_rmse')} {value('off_future_rmse')} {value('live_rmse')} {value('added_overshoot_max')}")
        return report
    if (args.render_hz <= 0 or args.duration <= 0 or args.jitter_ms < 0 or
            args.jitter_ms >= 1000 / 60 / 2 or args.latency_ms < args.jitter_ms):
        parser.error("require render-hz/duration > 0, 0 <= jitter-ms < 8.333, latency-ms >= jitter-ms")
    cases = []
    if args.csv:
        samples = read_csv(args.csv)
        cases.append(dict(path=str(args.csv), sample_hz="CSV", results=evaluate(
            samples, interpolated_truth(samples), args.render_hz)))
    else:
        for path in ("sinusoid", "swing"):
            for hz in (30, 60):
                samples = synthetic_samples(path, hz, args.duration, args.jitter_ms, args.latency_ms, args.seed)
                cases.append(dict(path=path, sample_hz=hz, results=evaluate(
                    samples, lambda t, path=path: synthetic_position(path, t), args.render_hz)))
    report = dict(configuration=vars(args) | {"csv": str(args.csv) if args.csv else None,
                                             "json": str(args.json) if args.json else None},
                  units="world XY; Euclidean error per endpoint; overshoot per coordinate",
                  precision="float32 Vector2 operations / float64 clocks; not physical latency measurement",
                  cases=cases)
    if args.json:
        args.json.write_text(json.dumps(report, indent=2, allow_nan=False) + "\n", encoding="utf-8")
    print("world units | future target: capture+H | live target: actual render time")
    print("path      Hz   H  future RMSE  OFF same target  live RMSE  reversal max")
    for case in cases:
        for result in case["results"]:
            def value(key):
                number = result[key]
                return f"{number:.6f}" if number is not None else "n/a"
            print(f"{case['path']:9} {str(case['sample_hz']):>2} {result['horizon_ms']:3}  "
                  f"{value('future_rmse'):>11}  {value('off_future_rmse'):>15}  "
                  f"{value('live_rmse'):>9}  {value('reversal_overshoot_max'):>12}")
    return report


if __name__ == "__main__":
    main()
