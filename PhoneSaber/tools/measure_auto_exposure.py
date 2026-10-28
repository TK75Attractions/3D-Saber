#!/usr/bin/env python3
"""AutoClipの既存PNG/候補でISO変更を近似測定する。画像・結果はGit外へ保存する。

contextは検出器と同じstep=2の点サンプル済み。eventだけ[::2, ::2]を使い、
valueMap=max(R,G,B)の上側中央値を求める。sRGB線形化×ISO比→再符号化は
固定シャッター・固定照明の近似であり、実機のISP/ノイズ/認識の改善を保証しない。
255のpeakは未飽和放射量が不明なので推定も下限。条件を満たさなくても不可とは断定しない。
"""
from __future__ import annotations

import argparse
from dataclasses import dataclass, field
import json
import math
from pathlib import Path
import statistics

LEVELS = (2000, 1600, 1280, 1000, 800, 640, 500, 400, 320, 250, 200, 160,
          125, 100, 80, 64, 50, 40, 32)
WINDOW, LOWER_HOLD, RAISE_HOLD, COOLDOWN, SETTLING = 1.5, 2.0, 4.0, 3.0, 1.0
LOWER_THRESHOLD, RAISE_THRESHOLD = 46.0, 30.0


@dataclass
class AutoExposureController:
    applied_iso: float | None = None
    last_change: dict | None = None
    ceiling: float | None = None
    floor: float | None = None
    samples: list = field(default_factory=list)
    previous_time: float | None = None
    high_since: float | None = None
    low_since: float | None = None
    discard_until: float = -math.inf

    def observe(self, time, background, ceiling_iso, min_iso, enabled=True, emitter=True, shutter=True):
        if not (enabled and emitter and shutter):
            self.__init__()
            return None
        if not (math.isfinite(time) and 0 <= background <= 255 and math.isfinite(ceiling_iso)
                and math.isfinite(min_iso) and min_iso > 0 and ceiling_iso > 0):
            return None
        minimum = max(min_iso, 32)
        maximum = max(minimum, ceiling_iso)
        if self.ceiling != maximum or self.floor != minimum:
            old = self.applied_iso
            self.__init__()
            self.ceiling, self.floor, self.applied_iso = maximum, minimum, maximum
            if old is not None and old != maximum:
                self.last_change = dict(time=time, **{'from': old}, to=maximum,
                                        background=float(background), reason='reset')
                self.discard_until = time + SETTLING
                return self.last_change
        if self.previous_time is not None:
            if time <= self.previous_time:
                return None
            if time - self.previous_time > 1:
                self.samples, self.high_since, self.low_since = [], None, None
        self.previous_time = time
        if time < self.discard_until:
            return None
        self.samples = [(t, v) for t, v in self.samples if t >= time - WINDOW]
        self.samples.append((time, background))
        filtered = statistics.median(v for _, v in self.samples)
        self.high_since = (self.high_since if self.high_since is not None else time) if filtered > LOWER_THRESHOLD else None
        self.low_since = (self.low_since if self.low_since is not None else time) if filtered < RAISE_THRESHOLD else None
        if time - (self.last_change['time'] if self.last_change else -math.inf) < COOLDOWN:
            return None
        current = self.applied_iso
        if self.high_since is not None and time - self.high_since >= LOWER_HOLD:
            nxt, reason = max(minimum, next((v for v in LEVELS if v < current), minimum)), 'lower'
        elif self.low_since is not None and time - self.low_since >= RAISE_HOLD:
            nxt, reason = min(maximum, next((v for v in reversed(LEVELS) if v > current), maximum)), 'raise'
        else:
            return None
        if nxt == current:
            return None
        self.last_change = dict(time=time, **{'from': current}, to=nxt, background=float(filtered), reason=reason)
        self.applied_iso = nxt
        self.samples, self.high_since, self.low_since = [], None, None
        self.discard_until = time + SETTLING
        return self.last_change


def scale_srgb(value, ratio):
    value = value / 255.0
    linear = value / 12.92 if value <= 0.04045 else ((value + 0.055) / 1.055) ** 2.4
    linear *= ratio
    encoded = linear * 12.92 if linear <= 0.0031308 else 1.055 * linear ** (1 / 2.4) - 0.055
    return max(0.0, min(255.0, encoded * 255))


def background_median(path, role):
    import numpy as np
    from PIL import Image
    with Image.open(path) as image:
        rgb = np.asarray(image.convert('RGB'))
    if role == 'event':
        rgb = rgb[::2, ::2]
    values = rgb.max(axis=2).ravel()
    # Swift saberBackgroundMedian: count > n/2（偶数でも平均しない）。
    return int(np.partition(values, len(values) // 2)[len(values) // 2])


def simulate(observations, ceiling, min_iso=32, enabled=True, emitter=True, shutter=True):
    controller = AutoExposureController()
    changes = []
    first = observations[0]['time'] if observations else 0
    last_sample = -math.inf
    for row in observations:
        # FrameProcessorと同じ最大4Hz。PNGのない時刻の画像は補間しない。
        if row['time'] - last_sample < 0.25:
            continue
        last_sample = row['time']
        applied = controller.applied_iso or ceiling
        background = int(scale_srgb(row['backgroundMedian'], applied / ceiling) + 0.5)
        change = controller.observe(row['time'] - first, background, ceiling, min_iso, enabled, emitter, shutter)
        if change:
            changes.append(change.copy())
    reversals = sum(a['reason'] == 'lower' and b['reason'] == 'raise' and b['time'] - a['time'] <= 8
                    for a, b in zip(changes, changes[1:]))
    return dict(changes=changes, changeCount=len(changes), finalISO=controller.applied_iso or ceiling,
                lowerThenRaiseWithin8Seconds=reversals)


def estimate_table(observations, ceiling, min_iso=32):
    table = []
    minimum = max(min_iso, 32)
    for iso in sorted({max(minimum, ceiling), minimum, *(v for v in LEVELS if minimum <= v <= ceiling)}):
        backgrounds = [scale_srgb(r['backgroundMedian'], iso / ceiling) for r in observations]
        entry = dict(iso=iso, backgroundMedian=statistics.median(backgrounds), backgroundMax=max(backgrounds))
        for color in ('red', 'blue'):
            peaks = [r['selectedPeak'][color] for r in observations if r['selectedPeak'][color] is not None]
            estimates = [scale_srgb(p, iso / ceiling) for p in peaks]
            entry[color] = dict(selectedFrames=len(peaks), saturatedFrames=sum(p == 255 for p in peaks),
                                peakMin=min(estimates) if estimates else None,
                                peakMedian=statistics.median(estimates) if estimates else None)
        table.append(entry)
    minimum = {}
    for color in ('red', 'blue'):
        minimum[color] = next((row['iso'] for row in table if row['backgroundMax'] <= 40
                               and row[color]['peakMin'] is not None and row[color]['peakMin'] >= 200), None)
    return table, minimum


def measure_clip(path, min_iso=32):
    summary = json.loads((path / 'summary.json').read_text())
    auto = summary['autoClip']
    camera = auto['camera']
    observations = []
    for image in summary['images']:
        if image.get('role') not in ('event', 'context'):
            continue
        context = json.loads((path / image['frameContextPath']).read_text())
        frame = next(f for f in context['frames'] if f['frameID'] == image['frameID'])
        candidates = {c: frame[c].get('autoClipCandidates', []) for c in ('red', 'blue')}
        selected = {c: next((v['peakValue'] for v in candidates[c] if v.get('selected')), None) for c in candidates}
        observations.append(dict(frameID=image['frameID'], time=image['timestamp'], role=image['role'],
                                 image=image['path'], backgroundMedian=background_median(path / image['path'], image['role']),
                                 selectedPeak=selected,
                                 candidatePeaks={c: [v['peakValue'] for v in rows] for c, rows in candidates.items()}))
    # 同一時刻のeventを優先。クリップ間の途切れを繋げない。
    unique = {r['time']: r for r in sorted(observations, key=lambda r: r['role'], reverse=True)}
    observations = sorted(unique.values(), key=lambda r: r['time'])
    if not observations:
        raise ValueError(f'No event/context PNG: {path}')
    ceiling = camera['shutterISO']
    table, minimum = estimate_table(observations, ceiling, min_iso)
    for observation in observations:
        observation['minimumISO'] = estimate_table([observation], ceiling, min_iso)[1]
    median = statistics.median(r['backgroundMedian'] for r in observations)
    return dict(clip=path.name, camera=camera, station=auto.get('phone', {}).get('station'),
                backgroundMedian=median, backgroundMax=max(r['backgroundMedian'] for r in observations),
                observations=observations, estimates=table, minimumISO=minimum,
                simulation=simulate(observations, ceiling, min_iso, enabled=ceiling < 2000,
                                    emitter=camera.get("shutterEmitter", False), shutter=camera.get("shutter", 0) != 0))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path.home() / 'Library/Application Support/PhoneSaber/diagnostics-inbox/autoclips')
    parser.add_argument('--dates', nargs='+', default=['20261028', '20261024', '20261027'])
    parser.add_argument('--before', help='保存名のYYYYMMDD_HHMMSS上限（この時刻未満だけで再測定）')
    parser.add_argument('--min-iso', type=float, default=32, help='実機のformat.minISO。未測定時の32は楽観的な下限')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    clips = [measure_clip(p, args.min_iso) for p in sorted(args.root.iterdir())
             if p.is_dir() and any(date in p.name for date in args.dates) and (p / 'summary.json').exists()
             and (not args.before or p.name.split('autoclip_')[-1][:15] < args.before)]
    target = [c for c in clips if '20261028' in c['clip']]
    dark = [c for c in clips if '20261028' not in c['clip'] and c['camera']['shutterISO'] in (160, 250)
            and c['backgroundMedian'] <= 30]
    dark_strict = [c for c in dark if c['backgroundMax'] <= 30]
    bright = [c for c in target if c['backgroundMedian'] > 46]
    def counts(rows):
        return dict(clips=len(rows), changes=sum(c['simulation']['changeCount'] for c in rows),
                    reversals=sum(c['simulation']['lowerThenRaiseWithin8Seconds'] for c in rows),
                    finalISOs={str(v): sum(c['simulation']['finalISO'] == v for c in rows)
                               for v in sorted({c['simulation']['finalISO'] for c in rows})})
    result = dict(notes=__doc__, minISO=args.min_iso, darkDefinition='clip median of PNG background medians <=30; ISO160/250; 10-24/27; strict subset: all PNG medians <=30',
                  target=counts(target), bright=counts(bright), dark=counts(dark), darkStrict=counts(dark_strict), clips=clips)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + '\n')
    print(json.dumps({k: result[k] for k in ('target', 'bright', 'dark', 'darkStrict')}, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
