#!/usr/bin/env python3
"""追従の実測(-phonesaberTrackingProbe)の解析。

画面の赤い棒を決まった軌道で動かし、スマホ経由で戻ってきた座標と突き合わせた CSV を読む。
棒の本当の位置が毎フレーム分かるので、次の2つが実データで出る。

1. 見た目の遅れ: 戻ってきた位置が、棒の指令位置から何 ms 遅れているか(相互相関の最大)。
   画面の表示・カメラ・認識・通信・受信の全部を含む。実際の剣でも合計は同じ(表示が最後に来るだけ)。
2. 先読みの効き: 描画の瞬間に「いま表示する位置」を各方式・各先読み量で作り、同じ瞬間の指令位置との
   ずれを測る(描いた絵が光る頃に、指令した棒も光っているので、同じ時刻どうしを比べればよい)。

  python3 PhoneSaber/tools/tracking_report.py                 # 最新の tracking-*.csv
  python3 PhoneSaber/tools/tracking_report.py file.csv --json out.json

ずれは「棒の移動幅(端から端)に対する %」で示す。--actual-csharp は実C#をworld XYで再生する。
"""
import argparse
import json
import math
from pathlib import Path
import sys

import numpy as np

FOLDER = Path.home() / "Library/Application Support/DefaultCompany/ServTechSlash/PhoneSaber"
SEGMENT = 8.0
FREQUENCIES = (0.5, 1.0, 0.0, 1.5, 2.0, 0.75, 0.0, 2.5)
LEADS = (0, 20, 40, 60, 80, 100, 120, 140, 160)


def load(path):
    frames, packets = [], []
    for line in Path(path).read_text(encoding="utf-8").splitlines():
        parts = line.split(",")
        try:
            if parts[0] == "F":
                frames.append((float(parts[1]), float(parts[2])))
            elif parts[0] == "R":
                packets.append(tuple(float(value) for value in parts[1:6]))
        except (ValueError, IndexError):
            continue
    return np.array(frames), np.array(packets)


def project(packets):
    """棒の中点を、動きの主軸へ射影した1次元の値にする(スマホの向きに依らない)。"""
    middle = np.column_stack(((packets[:, 1] + packets[:, 3]) / 2, (packets[:, 2] + packets[:, 4]) / 2))
    centred = middle - middle.mean(axis=0)
    _, _, axes = np.linalg.svd(centred, full_matrices=False)
    return centred @ axes[0]


def best_lag(times, values, frame_times, frame_x, mask=None):
    """戻ってきた値が、指令位置から何秒遅れているか。相関が最大になる遅れと、そのときの相関。"""
    if mask is not None:
        times, values = times[mask], values[mask]
    if len(times) < 30 or np.std(values) < 1e-6:
        return None, 0.0
    best = (None, 0.0)
    for lag_ms in range(0, 401, 2):
        reference = np.interp(times - lag_ms / 1000, frame_times, frame_x)
        if np.std(reference) < 1e-9:
            continue
        correlation = abs(float(np.corrcoef(values, reference)[0, 1]))
        if correlation > best[1]:
            best = (lag_ms, correlation)
    return best


def segment_of(t):
    return FREQUENCIES[int(t // SEGMENT) % len(FREQUENCIES)]


class Shown:
    """描画の瞬間に表示する値を作る方式。samples は (受信時刻, 値) の昇順。"""

    def __init__(self, kind, lead_ms):
        self.kind, self.lead = kind, lead_ms / 1000.0

    def value(self, now, times, values, index):
        # index: now 以前に受信した最後のサンプル
        latest_t, latest = times[index], values[index]
        if self.lead <= 0 or index < 2 or now - latest_t > 0.1:
            return latest
        t1, t2 = times[index - 1], times[index - 2]
        v1 = (latest - values[index - 1]) / max(latest_t - t1, 1e-4)
        v0 = (values[index - 1] - values[index - 2]) / max(t1 - t2, 1e-4)
        if latest_t - t1 > 0.1 or t1 - t2 > 0.1:
            return latest
        horizon = self.lead + (now - latest_t if self.kind.endswith("+age") else 0.0)
        kind = self.kind.replace("+age", "")
        if kind == "conservative":
            # 本番の予測器と同じ考え方: 向きが変わったら 0、加速中は前の速さ、減速中はさらに弱める。
            if v0 == 0 or v1 == 0 or (v0 > 0) != (v1 > 0):
                velocity = 0.0
            elif abs(v1) >= abs(v0):
                velocity = v0
            else:
                velocity = v1 * abs(v1 / v0)
            return latest + velocity * min(self.lead, 0.06)
        if kind == "linear":
            return latest + v1 * horizon
        if kind == "linear3":
            # 直近3点の最小二乗の傾き(雑音に強い)。
            slope = np.polyfit(times[index - 2:index + 1] - latest_t, values[index - 2:index + 1], 1)[0]
            return latest + slope * horizon
        if kind == "quadratic":
            acceleration = (v1 - v0) / max((latest_t - t2) / 2, 1e-4)
            return latest + v1 * horizon + 0.5 * acceleration * horizon * horizon
        raise ValueError(kind)


def evaluate(frames, times, values, scale, offset, kinds, leads):
    frame_times, target = frames[:, 0], frames[:, 1] * scale + offset
    travel = abs(scale) * 0.6  # 端から端(振幅 0.3 の2倍)
    start = max(frame_times[0], times[2] + 0.05)
    use = frame_times >= start
    indexes = np.searchsorted(times, frame_times, side="right") - 1
    results = []
    for kind in kinds:
        for lead in ((0,) if kind == "hold" else leads):
            shown = Shown("linear" if kind == "hold" else kind, 0 if kind == "hold" else lead)
            errors, moving, still = [], [], []
            for i in np.nonzero(use)[0]:
                index = indexes[i]
                if index < 2 or frame_times[i] - times[index] > 0.25:
                    continue
                error = (shown.value(frame_times[i], times, values, index) - target[i]) / travel * 100
                errors.append(error)
                (still if segment_of(frame_times[i]) == 0 else moving).append(error)
            if not errors:
                continue
            def rms(items):
                return float(np.sqrt(np.mean(np.square(items)))) if items else None
            results.append({
                "kind": kind, "lead_ms": lead, "n": len(errors), "rms_all": rms(errors), "rms_moving": rms(moving),
                "p95_moving": float(np.percentile(np.abs(moving), 95)) if moving else None,
                "jitter_still": float(np.std(still)) if still else None,
            })
    return results


def evaluate_csharp(frames, packets, replay, leads=(20, 40, 60, 90, 120), render_hz=None):
    from hit_feel_replay import world_packets
    world = world_packets(packets)
    middle = (world[:, 1:3] + world[:, 3:5]) / 2
    centre = middle.mean(axis=0)
    _, _, axes = np.linalg.svd(middle - centre, full_matrices=False)
    axis = axes[0]
    values = (middle - centre) @ axis
    lag, correlation = best_lag(world[:, 0], values, frames[:, 0], frames[:, 1])
    if lag is None or correlation < .5:
        raise ValueError("world写像後の棒と指令位置が対応しません")
    scale, offset = np.polyfit(np.interp(world[:, 0] - lag / 1000, frames[:, 0], frames[:, 1]), values, 1)
    travel = abs(scale) * .6
    times = frames[:, 0] if render_hz is None else np.arange(frames[0, 0], frames[-1, 0], 1 / render_hz)
    command = np.interp(times, frames[:, 0], frames[:, 1])
    target = command * scale + offset
    index = np.searchsorted(world[:, 0], times, side="right") - 1
    valid = (times >= max(times[0], world[2, 0] + .05)) & (index >= 2)
    valid &= times - world[np.maximum(index, 0), 0] <= .25
    frequency = np.array([segment_of(t) for t in times])
    # 指令の折り返し±100msだけで、可動範囲を越えた外向き誤差を集計する。
    direction = np.sign(np.diff(command))
    turns = times[1:-1][direction[:-1] * direction[1:] < 0]
    near_turn = np.zeros(len(times), dtype=bool)
    for turn in turns:
        near_turn |= abs(times - turn) <= .1
    low, high = sorted((.2 * scale + offset, .8 * scale + offset))
    results = []
    for mode in replay.modes:
        for lead in ((0,) if mode == "off" else leads):
            output = replay.run(times, world, lead, mode)
            blade = (output[:, 1:3] + output[:, 3:5]) / 2
            shown = (blade - centre) @ axis
            error = shown - target
            by_frequency = {}
            for hz in sorted(set(FREQUENCIES)):
                mask = valid & (frequency == hz)
                moving_error = error[mask]
                if not len(moving_error):
                    continue
                turn_mask = mask & near_turn
                excess = np.maximum(0, np.maximum(low - shown[turn_mask], shown[turn_mask] - high))
                by_frequency[str(hz)] = dict(
                    n=int(mask.sum()), rms_world=float(np.sqrt(np.mean(moving_error ** 2))),
                    rms_percent=float(np.sqrt(np.mean(moving_error ** 2)) / travel * 100),
                    p95_percent=float(np.percentile(abs(moving_error), 95) / travel * 100),
                    jitter_percent=float(np.std(moving_error) / travel * 100) if hz == 0 else None,
                    overshoot_max_percent=float(excess.max() / travel * 100) if len(excess) else None)
            # 60Hzの表示にサンプルし、描画fpsの違いでjerkの単位を変えない。
            display_times = np.arange(times[0], times[-1], 1 / 60)
            display_index = np.maximum(0, np.searchsorted(times, display_times, side="right") - 1)
            jerk = np.diff(blade[display_index], n=3, axis=0) * 60 ** 3
            results.append(dict(kind=mode, lead_ms=lead, by_frequency=by_frequency,
                                jerk_rms_world_s3=float(np.sqrt(np.mean(np.sum(jerk ** 2, axis=1))))))
    return dict(lag_ms=lag, travel_world=travel, calibration="direct worldScale=5.5,3 sensitivity=2 calibration=off; affine fit",
                render_hz=render_hz, excluded_frames=int((~valid).sum()), results=results)


def print_csharp(report):
    print(f"\n実C# / world移動幅 {report['travel_world']:.3f} / 除外 {report['excluded_frames']} frame")
    for item in report["results"]:
        print(f"{item['kind']:8} {item['lead_ms']:3}ms " + " / ".join(
            f"{hz}Hz RMS/p95={v['rms_percent']:.1f}/{v['p95_percent']:.1f} turn-max={v['overshoot_max_percent']}"
            if hz != "0.0" else f"静止jitter={v['jitter_percent']:.2f}%"
            for hz, v in item["by_frequency"].items()) + f" jerk60={item['jerk_rms_world_s3']:.0f}")


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("csv", nargs="?")
    parser.add_argument("--json")
    parser.add_argument("--actual-csharp", action="store_true")
    parser.add_argument("--before-ref", help="実C#の旧版で同じ表を再生")
    parser.add_argument("--render-hz", type=float, help="実C#再生の描画Hz(省略時は実記録のF時刻)")
    parser.add_argument("--candidate-maximum-ms", type=int, choices=(60, 90, 120), help="上限変更候補の一時C# build(本番の上限は変えない)")
    args = parser.parse_args()
    if not args.actual_csharp and any(value is not None for value in
                                     (args.before_ref, args.render_hz, args.candidate_maximum_ms)):
        parser.error('--before-ref / --render-hz / --candidate-maximum-ms は --actual-csharp が必要')
    if args.before_ref and args.candidate_maximum_ms is not None:
        parser.error('--before-ref と --candidate-maximum-ms は併用できません')
    if args.render_hz is not None and (not math.isfinite(args.render_hz) or args.render_hz <= 0):
        parser.error("--render-hz は有限の正数")
    path = Path(args.csv) if args.csv else max(FOLDER.glob("tracking-*.csv"), key=lambda p: p.stat().st_mtime, default=None)
    if path is None or not path.exists():
        print("tracking-*.csv がありません。-phonesaberTrackingProbe で計測してください。")
        return 2
    frames, packets = load(path)
    if len(frames) < 300 or len(packets) < 150:
        print(f"記録が足りません(描画 {len(frames)} / 受信 {len(packets)})。スマホが画面を向いているか確認してください。")
        return 1
    times, values = packets[:, 0], project(packets)
    seconds = frames[-1, 0] - frames[0, 0]
    gaps = np.diff(times)
    lag_ms, correlation = best_lag(times, values, frames[:, 0], frames[:, 1])
    if lag_ms is None or correlation < 0.5:
        print(f"棒の動きと受信した座標が対応しません(相関 {correlation:.2f})。画面に赤い棒だけが映るようにしてください。")
        return 1
    reference = np.interp(times - lag_ms / 1000, frames[:, 0], frames[:, 1])
    scale, offset = np.polyfit(reference, values, 1)
    residual = values - (reference * scale + offset)
    travel = abs(scale) * 0.6
    if args.actual_csharp:
        from hit_feel_replay import CSharpReplay
        replay = CSharpReplay(args.before_ref, candidate_maximum_ms=args.candidate_maximum_ms)
        try:
            report = evaluate_csharp(frames, packets, replay, render_hz=args.render_hz)
            report['candidate_maximum_ms'] = args.candidate_maximum_ms
        finally:
            replay.close()
        print_csharp(report)
        if args.json:
            Path(args.json).write_text(json.dumps(report, indent=1), encoding="utf-8")
        return 0
    print(f"{path.name}: {seconds:.0f} 秒 / 描画 {len(frames)} 回 ({len(frames) / seconds:.0f}/s) / 受信 {len(packets)} 件 "
          f"({len(packets) / seconds:.1f}/s、間隔の中央値 {np.median(gaps) * 1000:.0f} ms、100 ms 超の途切れ {int(np.sum(gaps > 0.1))} 回)")
    print(f"見た目の遅れ: {lag_ms} ms(相関 {correlation:.3f})。遅れを合わせた後の残り(認識の揺れ): "
          f"移動幅の {np.std(residual) / travel * 100:.1f}%")
    by_frequency = {}
    for frequency in sorted(set(FREQUENCIES)):
        mask = np.array([segment_of(t) == frequency for t in times])
        if frequency == 0:
            still = values[mask]
            by_frequency["0"] = {"jitter_percent": float(np.std(still - np.median(still)) / travel * 100) if len(still) else None}
            continue
        lag, corr = best_lag(times, values, frames[:, 0], frames[:, 1], mask)
        seconds_in = sum(1 for t in frames[:, 0] if segment_of(t) == frequency) / max(len(frames) / seconds, 1e-9)
        by_frequency[str(frequency)] = {"lag_ms": lag, "correlation": corr,
                                        "packets_per_s": float(np.sum(mask) / max(seconds_in, 1e-9))}
    print("速さ別: " + " / ".join(
        f"{frequency} Hz: 遅れ {item['lag_ms']} ms・受信 {item['packets_per_s']:.0f}/s" if frequency != "0"
        else f"静止: 揺れ {item['jitter_percent']:.1f}%" for frequency, item in by_frequency.items()
        if (frequency == "0" and item["jitter_percent"] is not None) or (frequency != "0" and item["lag_ms"] is not None)))
    kinds = ("hold", "conservative", "linear", "linear+age", "linear3+age", "quadratic+age")
    results = evaluate(frames, times, values, scale, offset, kinds, LEADS[1:])
    hold = next(item for item in results if item["kind"] == "hold")
    print(f"\n表示のずれ(移動幅に対する %、小さいほど良い)。先読みなし: 動いている間 RMS {hold['rms_moving']:.1f}% / "
          f"95% 点 {hold['p95_moving']:.1f}% / 静止時の揺れ {hold['jitter_still']:.2f}%")
    print(f"{'方式':16}" + "".join(f"{lead:>7}" for lead in LEADS[1:]) + "  ms 先読み")
    for kind in kinds[1:]:
        row = {item["lead_ms"]: item for item in results if item["kind"] == kind}
        print(f"{kind:16}" + "".join(f"{row[lead]['rms_moving']:7.1f}" if lead in row else "      -" for lead in LEADS[1:])
              + "  動 RMS")
        print(f"{'':16}" + "".join(f"{row[lead]['jitter_still']:7.2f}" if lead in row else "      -" for lead in LEADS[1:])
              + "  静止の揺れ")
    best = min((item for item in results if item["kind"] != "hold"), key=lambda item: item["rms_moving"])
    print(f"\n最小: {best['kind']} {best['lead_ms']} ms → 動 RMS {best['rms_moving']:.1f}%(先読みなし {hold['rms_moving']:.1f}%)、"
          f"静止の揺れ {best['jitter_still']:.2f}%(先読みなし {hold['jitter_still']:.2f}%)")
    if args.json:
        Path(args.json).write_text(json.dumps({
            "file": path.name, "lag_ms": lag_ms, "correlation": correlation, "by_frequency": by_frequency,
            "residual_percent": float(np.std(residual) / travel * 100), "results": results}, indent=1), encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
