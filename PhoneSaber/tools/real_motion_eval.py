"""Offline detector-trajectory evaluation; neither physical truth nor a latency test."""

import bisect
import math
import statistics
from array import array
from collections import defaultdict

from endpoint_prediction_eval import Predictor, BaselinePredictor, Sample, f32, interpolated_truth, reversals, percentile
from real_motion_extract import read_rows


class Metric:
    """Exact quantiles in compact arrays; RMS-only metrics do not retain samples."""

    def __init__(self, quantile=False):
        self.values = array("d") if quantile else None
        self.count = 0
        self.squares = 0.0
        self.maximum = 0.0

    def append(self, value):
        self.count += 1
        self.squares += value * value
        self.maximum = max(self.maximum, value)
        if self.values is not None:
            self.values.append(value)

    def merge(self, other):
        self.count += other.count
        self.squares += other.squares
        self.maximum = max(self.maximum, other.maximum)
        if self.values is not None:
            self.values.extend(other.values)

    def rmse(self):
        return math.sqrt(self.squares / self.count) if self.count else None

    def p95(self):
        return percentile(self.values, 0.95)


def metrics():
    return {key: Metric(key in ("future", "overshoot", "added_overshoot"))
            for key in ("future", "off_future", "live", "reversal_error", "overshoot", "added_overshoot")}


class ExperimentalPredictor(BaselinePredictor):
    """Offline alternatives only. Constants here never affect runtime recognition."""

    def __init__(self, estimator):
        super().__init__()
        self.estimator = estimator
        self.history = []

    def add(self, timestamp, endpoints):
        if self.count and timestamp <= self.time:
            return
        if not self.count or timestamp - self.time > 0.1:
            self.history = []
        super().add(timestamp, endpoints)
        self.history = (self.history + [(timestamp, self.latest)])[-3:]
        if self.estimator == "ls3" and len(self.history) == 3:
            # 不等間隔の3点最小二乗。double 計算の後に float32 へ丸める実験。
            times = [t - timestamp for t, _ in self.history]
            mean = sum(times) / 3
            denominator = sum((t - mean) ** 2 for t in times)
            self.velocity = tuple(f32(sum((t - mean) * p[axis] for t, (_, p) in zip(times, self.history)) / denominator)
                                  for axis in range(4))
        elif self.estimator == "adaptive":
            self.velocity = tuple(f32(v * min(1.0, math.hypot(*self.velocity[i:i + 2]) / 2.0))
                                  for i in (0, 2) for v in self.velocity[i:i + 2])


def load_segments(path, world_width=11.0, world_height=6.0, max_truth_gap=0.05,
                  allow_unknown_prediction=True, align_endpoints=False):
    """Do not interpolate across missing/predicted endpoints, frame holes or sessions."""
    groups = defaultdict(list)
    for row in read_rows(path):
        if not math.isfinite(row["time_s"]):
            raise ValueError("nonfinite time")
        groups[(row["session"], row["color"])].append(row)
    segments, inventory = [], []
    for (session, color), rows in sorted(groups.items()):
        rows.sort(key=lambda r: r["time_s"])
        current = []
        unknown = invalid = swaps = 0
        last_time = None
        for row in rows:
            if last_time is not None and row["time_s"] <= last_time:
                raise ValueError("duplicate/nonincreasing capture time")
            last_time = row["time_s"]
            unknown += row["predicted"] is None
            valid = (row["detected"] is True and row["predicted"] is not True and
                     (allow_unknown_prediction or row["predicted"] is False) and
                     all(row[k] is not None and math.isfinite(row[k]) for k in ("x1", "y1", "x2", "y2")))
            if valid:
                if not row["width"] or not row["height"] or row["width"] <= 0 or row["height"] <= 0:
                    raise ValueError("source dimensions required for pixel-to-world mapping")
                endpoints = tuple((row[k] / row[dimension] - 0.5) * extent * sign
                                  for k, dimension, extent, sign in
                                  (("x1", "width", world_width, 1), ("y1", "height", world_height, -1),
                                   ("x2", "width", world_width, 1), ("y2", "height", world_height, -1)))
                sample = Sample(row["time_s"], row["time_s"], endpoints)
            if not valid or (current and sample.capture - current[-1].capture > max_truth_gap):
                if len(current) >= 3:
                    segments.append((session, color, current))
                current = []
            if valid:
                if current:
                    prior = current[-1].endpoints
                    swapped = endpoints[2:] + endpoints[:2]
                    if sum((a-b)**2 for a, b in zip(swapped, prior)) < sum((a-b)**2 for a, b in zip(endpoints, prior)):
                        swaps += 1
                        if align_endpoints:
                            sample = Sample(sample.capture, sample.receive, swapped)
                current.append(sample)
            else:
                invalid += 1
        if len(current) >= 3:
            segments.append((session, color, current))
        inventory.append(dict(session=session, color=color, rows=len(rows), invalid_rows=invalid,
                              unknown_prediction_flags=unknown, nearest_endpoint_swaps=swaps))
    return segments, inventory


def evaluate_real(path, horizons=range(61), estimators=("baseline", "current", "ls3", "adaptive"),
                  render_rates=(30, 60), phases=(0.0, 0.25, 0.5, 0.75), latency_ms=140,
                  world_width=11, world_height=6, max_truth_gap=0.05,
                  allow_unknown_prediction=True, align_endpoints=False):
    horizons = tuple(horizons)
    if not horizons or any(h < 0 or h > 60 for h in horizons):
        raise ValueError("horizons must be in 0..60 ms")
    if latency_ms < 0 or max_truth_gap <= 0 or world_width <= 0 or world_height <= 0:
        raise ValueError("latency >= 0, extents/gap > 0 required")
    if any(hz <= 0 for hz in render_rates) or any(p < 0 or p >= 1 for p in phases):
        raise ValueError("positive render rates and phases in [0,1) required")
    segments, inventory = load_segments(path, world_width, world_height, max_truth_gap,
                                        allow_unknown_prediction, align_endpoints)
    stats = defaultdict(metrics)
    segment_inventory = []
    for session, color, raw in segments:
        segment_inventory.append(dict(session=session, color=color, samples=len(raw),
                                      duration_s=raw[-1].capture-raw[0].capture,
                                      median_sample_hz=1/statistics.median([b.capture-a.capture for a,b in zip(raw,raw[1:])])))
        # capture のみ実測。受信遅延は固定値の仮定で、ジッタは捏造しない。
        samples = [Sample(s.capture, s.capture + latency_ms / 1000, s.endpoints) for s in raw]
        truth = interpolated_truth(samples)
        turns = reversals(samples)
        turn_times = [t[0] for t in turns]
        for hz in render_rates:
            for phase in phases:
                predictors = {name: Predictor() if name == "current" else BaselinePredictor() if name == "baseline"
                              else ExperimentalPredictor(name) for name in estimators}
                index = 0
                latest = None
                for frame in range(math.ceil(samples[0].receive * hz - phase),
                                   math.floor(samples[-1].receive * hz - phase) + 1):
                    now = (frame + phase) / hz
                    previous_index = index
                    while index < len(samples) and samples[index].receive <= now:
                        latest = samples[index]
                        index += 1
                    if latest is None:
                        continue
                    if index != previous_index:
                        for predictor in predictors.values():
                            predictor.add(latest.receive, latest.endpoints)
                    # 全Hを同一観測集合で比較。端/欠測で長いHだけ不利にしない。
                    if truth(latest.capture + max(horizons)/1000) is None:
                        continue
                    live = truth(now)
                    nearby = turns[bisect.bisect_left(turn_times, latest.capture-0.1):
                                   bisect.bisect_right(turn_times, latest.capture+0.1)]
                    for horizon in horizons:
                        target = truth(latest.capture + horizon / 1000)
                        for name, predictor in predictors.items():
                            predicted = predictor.predict(now, horizon)
                            values = stats[(session, color, hz, name, horizon)]
                            for endpoint in (0, 2):
                                error = math.hypot(predicted[endpoint]-target[endpoint], predicted[endpoint+1]-target[endpoint+1])
                                values["future"].append(error)
                                values["off_future"].append(math.hypot(latest.endpoints[endpoint]-target[endpoint], latest.endpoints[endpoint+1]-target[endpoint+1]))
                                if nearby:
                                    values["reversal_error"].append(error)
                                if live is not None:
                                    values["live"].append(math.hypot(predicted[endpoint]-live[endpoint], predicted[endpoint+1]-live[endpoint+1]))
                            for _, axis, extreme, direction in nearby:
                                overshoot = max(0.0, direction * (predicted[axis]-extreme))
                                off = max(0.0, direction * (latest.endpoints[axis]-extreme))
                                values["overshoot"].append(overshoot)
                                values["added_overshoot"].append(max(0.0, overshoot-off))
    # 全体だけでなく session/color ごとも残し、長い一録画の支配を可視化する。
    aggregate = defaultdict(metrics)
    for (_, _, hz, name, horizon), values in stats.items():
        for key, entries in values.items():
            aggregate[(hz, name, horizon)][key].merge(entries)

    def summarize(key, values, per_session=False):
        hz, name, horizon = key[-3:]
        result = dict(render_hz=hz, estimator=name, horizon_ms=horizon,
                      endpoint_observations=values["future"].count, live_observations=values["live"].count,
                      future_rmse=values["future"].rmse(), off_future_rmse=values["off_future"].rmse(),
                      future_p95=values["future"].p95(), live_rmse=values["live"].rmse(),
                      reversal_rmse=values["reversal_error"].rmse(), reversal_observations=values["overshoot"].count)
        for metric in ("overshoot", "added_overshoot"):
            result[metric+"_max"] = values[metric].maximum
            result[metric+"_p95"] = values[metric].p95()
        if per_session:
            result.update(session=key[0], color=key[1])
        return result

    if not aggregate:
        raise ValueError("no evaluable continuous observed endpoint segments")
    return dict(units="world XY (assumed pixel mapping); Euclidean error per endpoint",
                limitation="Detector outputs are reference trajectories, not labelled physical ground truth. Receive timing is simulated. 60 Hz is render rate, not new camera truth.",
                configuration=dict(horizons_ms=horizons, estimators=estimators, render_rates=render_rates,
                                   phases=phases, assumed_latency_ms=latency_ms, world_width=world_width,
                                   world_height=world_height, max_truth_gap_s=max_truth_gap,
                                   allow_unknown_prediction=allow_unknown_prediction, align_endpoints=align_endpoints),
                inventory=inventory, segments=segment_inventory,
                results=[summarize(k,v) for k,v in sorted(aggregate.items())],
                per_session=[summarize(k,v,True) for k,v in sorted(stats.items())])
