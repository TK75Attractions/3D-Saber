#!/usr/bin/env python3
"""Read-only two-colour residual study on original pixels; private JSON outside repo.

Final-ranked candidate shadow gates do not change production generation/ranking.
Reuses the skin instrumentation and verifies every candidate against an unmodified
harness, at all three gains. Rings are unique full-resolution pixels outside the
one-pixel dilation, never bbox fills. Requires NumPy.
"""
from __future__ import annotations

import argparse
import copy
import csv
import itertools
import hashlib
import json
import os
from pathlib import Path
import subprocess
import tempfile

import phone_saber_skin_study as skin
import phone_saber_deep_red_study as deep


# Independent ORIGINAL visual review, not fixture expected endpoints.
EXTRA_BLUE_TRUTHS = {6288: [285, 435, 390, 485]}

FORMAL_BLUE_SABER_BOXES = {
'blue-led-with-left-curtain-reflection-03':[175,325,235,495],
'blue-led-with-left-curtain-reflection-04':[175,145,240,310],
'blue-led-bright-large-05':[260,235,295,395],
'frame_390':[120,195,180,235], 'frame_397':[190,250,290,325],
'blue_dropout_false_1249':[175,310,250,395],
'frame-0220':[20,540,70,635], 'frame-0600':[20,515,225,565],
'blue-led-with-curtain-reflection-01':[210,195,250,375],
'blue-led-with-curtain-reflection-02':[220,275,265,430],
'frame_251':[160,245,195,310], 'frame_254':[160,255,195,325],
'frame_256':[160,260,200,330], 'blue_dropout_false_361':[155,335,200,390],
'red_dropout_false_83':[420,510,480,575],
'red_dropout_false_90':[425,530,480,580],
'red_dropout_false_348':[175,345,230,405],
'red_dropout_last_true_82':[435,490,480,570],
'red_dropout_last_true_89':[435,515,480,570],
'red_dropout_recovered_84':[435,510,480,570],
'red_dropout_recovered_112':[175,550,265,640],
'red_dropout_recovered_351':[175,350,240,400],
'frame_1048':[40,490,140,605], 'frame_919':[135,535,175,590],
'frame_1091':[85,535,150,630],
}


def domain_indices(points, width, height, radius):
    import numpy as np
    p = np.asarray(points, dtype=int).reshape(-1, 2) * 2
    if radius < 0:
        raise ValueError('negative dilation radius')
    if not len(p):
        return np.array([], dtype=int)
    if radius <= 1:
        return np.asarray(deep.pixel_indices(points, width, height, radius), dtype=int)
    if np.any(p < 0) or np.any(p[:, 0] >= width) or np.any(p[:, 1] >= height):
        raise ValueError('sample outside original')
    offsets = np.array([(x, y) for y in range(-radius, radius + 1)
                        for x in range(-radius, radius + 1)])
    expanded = (p[:, None, :] + offsets).reshape(-1, 2)
    valid = ((expanded[:, 0] >= 0) & (expanded[:, 0] < width)
             & (expanded[:, 1] >= 0) & (expanded[:, 1] < height))
    return np.unique(expanded[valid, 1] * width + expanded[valid, 0])


def pixel_metrics(raw, indices, color):
    import numpy as np
    p = np.frombuffer(raw, dtype=np.uint8).reshape(-1, 4)[indices, :3].astype(float)
    if not len(p):
        return dict(size=0, deep_count=0, deep_fraction=0, warm_fraction=0,
                    neutral_fraction=0, clipped_fraction=0, halo_fraction=0,
                    cool_fraction=0, median_gr=None, median_bg=None)
    b, g, r = p.T
    gr = np.divide(g, r, out=np.zeros_like(g), where=r > 0)
    bg = np.divide(b, g, out=np.zeros_like(g), where=g > 0)
    rb = np.divide(r, b, out=np.zeros_like(r), where=b > 0)
    gb = np.divide(g, b, out=np.zeros_like(g), where=b > 0)
    br = np.divide(b, r, out=np.zeros_like(b), where=r > 0)
    mx, mn = p.max(axis=1), p.min(axis=1)
    neutral = (mx >= 180) & (mn >= .85 * mx)
    clipped = mn >= 245
    deep_mask = ((r >= 180) & (g * 100 < 40 * r) & (b * 100 < 65 * r) if color == 'red'
                 else (b >= 180) & (r * 100 < 40 * b) & (g * 100 < 65 * b))
    halo = ((r >= 120) & (gr < .65) & (br < .80) if color == 'red'
            else (b >= 120) & (rb < .65) & (gb < .80))
    warm = (r >= 120) & (g * 100 >= 45 * r) & (g * 100 <= 92 * r) & (b * 100 <= 95 * g)
    cool = (b >= 120) & (rb >= .45) & (rb <= .92) & (gb >= .65) & (gb <= .98) & (r <= g)
    bright = (r if color == 'red' else b) >= 120
    fraction = lambda mask: float(np.mean(mask))
    return dict(size=len(p), deep_count=int(deep_mask.sum()), deep_fraction=fraction(deep_mask),
                warm_fraction=fraction(warm), neutral_fraction=fraction(neutral),
                clipped_fraction=fraction(clipped), halo_fraction=fraction(halo),
                cool_fraction=fraction(cool),
                median_gr=float(np.median(gr[bright & (r > 0)])) if np.any(bright & (r > 0)) else None,
                median_bg=float(np.median(bg[bright & (g > 0)])) if np.any(bright & (g > 0)) else None)


def features(candidate, raw, width, height, color):
    import numpy as np
    base = domain_indices(candidate['study_points'], width, height, 1)
    out = pixel_metrics(raw, base, color)
    for radius in (2, 4, 8):
        ring = np.setdiff1d(domain_indices(candidate['study_points'], width, height, radius), base)
        out['ring' + str(radius)] = pixel_metrics(raw, ring, color)
    box = skin.study._box(candidate)
    sides = (box[2] - box[0] + 1, box[3] - box[1] + 1)
    out['area'] = candidate['point_count'] * 4
    out['aspect'] = max(sides) / min(sides)
    density = candidate['axial_density']
    if density <= 0:
        density = candidate['point_count'] / max(candidate['raw_pca_span'], 2.0)
    out['thickness'] = density * 4
    return out


def filtered(analysis, rows, predicate):
    out = copy.deepcopy(analysis)
    for color in skin.study.COLORS:
        candidates = out['colors'][color]['candidates']
        for c, row in zip(candidates, rows[color]):
            c['eligible'] = c['eligible'] and predicate(row)
        best = skin.study.winner(out['colors'][color])
        out['colors'][color]['selected'] = best['endpoints'] if best else None
    return out


def run(inbox, output):
    entries, fixtures = skin.inputs(inbox)
    labels = skin.study.load_labels(skin.LABELS)
    frames = []
    with tempfile.TemporaryDirectory(prefix='phonesaber-residual-') as tmp:
        directory = Path(tmp)
        binary = skin.build_harness(directory / 'instrumented')
        os.environ['CLANG_MODULE_CACHE_PATH'] = str(directory / 'control-clang')
        os.environ['SWIFT_MODULECACHE_PATH'] = str(directory / 'control-swift')
        control = directory / 'control'
        skin.lossless.compile_harness(control)
        env = {k: v for k, v in os.environ.items() if k not in skin.study.ENV_KEYS}
        for i, entry in enumerate(entries):
            path = Path(entry['path'])
            width, height = skin.lossless.dimensions(path)
            raw = skin.lossless.command_output(['ffmpeg', '-loglevel', 'error', '-i', str(path),
                '-frames:v', '1', '-f', 'rawvideo', '-pix_fmt', 'bgra', '-'])
            truths = {'red': entry['truth'], 'blue': None}
            if entry['source'] == 'labels' and (entry['session'], entry['frame']) in labels:
                truths = labels[(entry['session'], entry['frame'])]
            elif entry['session'] == skin.SESSION:
                truths['blue'] = EXTRA_BLUE_TRUTHS.get(entry['frame'])
            elif entry['source'] == 'formal':
                # Independent visual truth, not frozen detector output expectations.
                truths['blue'] = FORMAL_BLUE_SABER_BOXES.get(path.stem)
            for gain in skin.GAINS:
                gained = skin.study.apply_gain(raw, gain)
                args = [str(width), str(height), '2']
                analysis = json.loads(subprocess.run([str(binary), *args], input=gained,
                    env=env, capture_output=True, check=True).stdout.splitlines()[0])
                baseline = json.loads(subprocess.run([str(control), *args], input=gained,
                    env=env, capture_output=True, check=True).stdout.splitlines()[0])
                rows = {}
                for color in skin.study.COLORS:
                    actual, expected = analysis['colors'][color], baseline['colors'][color]
                    assert actual['selected'] == expected['selected'], (path, gain, color)
                    assert len(actual['candidates']) == len(expected['candidates'])
                    rows[color] = []
                    for rank, (c, b) in enumerate(zip(actual['candidates'], expected['candidates'])):
                        assert all(c[k] == v for k, v in b.items()), (path, gain, color, rank)
                        # Only eligible proposals can affect a final-ranked gate.
                        values = features(c, gained, width, height, color) if c['eligible'] else None
                        if values is not None and color == 'red':
                            verdict = c['warmNoDeepRed']
                            assert values['deep_count'] == verdict['deepCount']
                            assert values['warm_fraction'] == verdict['warmFrac']
                        rows[color].append(dict(color=color, rank=rank, candidate={k: v for k, v in c.items()
                                       if k not in ('study_points', 'study_color_flags')},
                            features=values, label=skin.study.label_candidate(c, truths[color])))
                frames.append(dict(**entry, truths=truths, gain=gain, rows=rows,
                    analysis=baseline, sha256=hashlib.sha256(path.read_bytes()).hexdigest(),
                    formal_base=[skin.lossless.evaluate_fixture(f, baseline) for f in fixtures
                                 if str(f['resolved_path']) == str(path)]))
            if i % 10 == 0:
                print(f'{i + 1}/{len(entries)} originals; all-gain two-colour parity PASS', flush=True)
    sources = [skin.study.SOURCES / 'DetectionCore.swift', skin.study.SOURCES / 'BGRADetection.swift',
               skin.LABELS, skin.lossless.DEFAULT_MANIFEST, Path(__file__)]
    output.write_text(json.dumps(dict(frames=frames, parity_replays=len(frames),
        sources_sha256={str(p.relative_to(skin.study.REPO)): hashlib.sha256(p.read_bytes()).hexdigest()
                        for p in sources}), separators=(',', ':')) + '\n')


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--inbox', type=Path, default=skin.study.DEFAULT_INBOX)
    parser.add_argument('--json', type=Path, required=True)
    parser.add_argument('--summarize-from', type=Path)
    parser.add_argument('--rule-json', type=Path, help='JSON study-only gate definition')
    parser.add_argument('--explore', action='store_true', help='repeat the documented grids')
    args = parser.parse_args()
    if args.json.resolve().is_relative_to(skin.study.REPO):
        parser.error('--json must be outside the repository')
    if args.summarize_from:
        report = json.loads(args.summarize_from.read_text())
        for path in (skin.study.SOURCES / 'DetectionCore.swift',
                     skin.study.SOURCES / 'BGRADetection.swift', skin.LABELS,
                     skin.lossless.DEFAULT_MANIFEST):
            digest = report['sources_sha256'][str(path.relative_to(skin.study.REPO))]
            if hashlib.sha256(path.read_bytes()).hexdigest() != digest:
                parser.error(f'replay source changed: {path.name}')
        _, fixtures = skin.lossless.load_fixtures(skin.lossless.DEFAULT_MANIFEST)
        rule = json.loads(args.rule_json.read_text()) if args.rule_json else DEFAULT_RULE
        result = explore(report, fixtures) if args.explore else evaluate(report, fixtures, rule)
        args.json.write_text(json.dumps(result, indent=2) + '\n')
    else:
        run(args.inbox, args.json)

# Study-only definitions applied AFTER production eligibility and final ranking.
def accepts(row, rule):
    f = row['features']
    if f is None:
        return True
    color = row['color']
    if color == 'blue' and rule.get('blue_deep_count') is not None:
        if f['deep_count'] < rule['blue_deep_count']:
            return False
    if color == 'blue' and rule.get('blue_deep_fraction') is not None:
        if f['deep_fraction'] < rule['blue_deep_fraction']:
            return False
    if color == 'blue' and rule.get('blue_cool') is not None:
        if f['deep_count'] == 0 and f['cool_fraction'] >= rule['blue_cool']:
            return False
    compact = rule.get('compact', {}).get(color)
    if compact and (f['area'] <= compact['area']
                    and f['thickness'] <= compact['thickness']
                    and row['candidate']['clipped_white'] <= compact['clippedWhite']):
        return False
    white = rule.get('white', {}).get(color)
    if white:
        if (f[white['metric']] >= white['core']
                and f['ring' + str(white['radius'])]['halo_fraction'] <= white['halo']):
            return False
    return True


def evaluate(report, fixtures, rule):
    by_name = {f['name']: f for f in fixtures}
    summary = {'rule': rule, 'gains': {}, 'formal': [], 'outcomes': []}
    for gain in skin.GAINS:
        counts = {color: {'correct_kept': 0, 'correct_base': 0, 'absent_base': 0,
                         'absent_after': 0, 'wrong_base': 0, 'wrong_after': 0,
                         'visible_base': 0, 'visible_after': 0, 'visible_n': 0,
                         'lost': [], 'unknown_base': 0, 'unknown_kept': 0,
                         'unknown_changed': []} for color in skin.study.COLORS}
        for frame in report['frames']:
            if frame['gain'] != gain:
                continue
            for color in skin.study.COLORS:
                rows = frame['rows'][color]
                before = next((r for r in rows if r['candidate']['eligible']), None)
                after = next((r for r in rows if r['candidate']['eligible'] and accepts(r, rule)), None)
                c = counts[color]
                if before and before['label'] == 'real':
                    c['correct_base'] += 1
                    c['correct_kept'] += before is after
                    if before is not after:
                        c['lost'].append([frame['session'], frame['frame'], before['rank']])
                truth = frame['truths'][color]
                if truth == '?' and before:
                    c['unknown_base'] += 1
                    c['unknown_kept'] += before is after
                    if before is not after:
                        c['unknown_changed'].append(dict(session=frame['session'],
                            frame=frame['frame'], bbox=skin.study._box(before['candidate']),
                            deep_count=before['features']['deep_count'],
                            before_endpoints=before['candidate']['endpoints'],
                            after_endpoints=after['candidate']['endpoints'] if after else None))
                if truth is None:
                    c['absent_base'] += before is not None
                    c['absent_after'] += after is not None
                elif truth != '?':
                    c['visible_n'] += 1
                    c['visible_base'] += before is not None and before['label'] == 'real'
                    c['visible_after'] += after is not None and after['label'] == 'real'
                    c['wrong_base'] += before is not None and before['label'] == 'bg'
                    c['wrong_after'] += after is not None and after['label'] == 'bg'
                summary['outcomes'].append(dict(session=frame['session'], frame=frame['frame'],
                    source=frame['source'], color=color, gain=gain, truth=truth,
                    before_rank=before['rank'] if before else None,
                    after_rank=after['rank'] if after else None,
                    before=before['label'] if before else 'none',
                    after=after['label'] if after else 'none',
                    endpoints=after['candidate']['endpoints'] if after else None))
            if frame['formal_base']:
                gated = filtered(frame['analysis'], frame['rows'], lambda row: accepts(row, rule))
                for base in frame['formal_base']:
                    summary['formal'].append(dict(gain=gain, name=base['name'], base=base['passed'],
                        shadow=skin.lossless.evaluate_fixture(by_name[base['name']], gated)))
        formal = [f for f in summary['formal'] if f['gain'] == gain]
        except89 = [f for f in formal if f['name'] != 'red_short_component_last_true_89']
        summary['gains'][str(gain)] = dict(colors=counts,
            formal_base=sum(f['base'] for f in formal),
            formal_after=sum(f['shadow']['passed'] for f in formal),
            formal_except89=[sum(f['base'] for f in except89), sum(f['shadow']['passed'] for f in except89)],
            fixture89=[f for f in formal if f['name'] == 'red_short_component_last_true_89'])
    summary['preserves_correct'] = all(c['correct_kept'] == c['correct_base']
        for gain in summary['gains'].values() for c in gain['colors'].values())
    summary['preserves_formal39'] = summary['gains']['1.0']['formal_except89'] == [39, 39]
    return summary


DEFAULT_RULE = {'blue_deep_count': 1, 'white': {'red': {
    'metric': 'neutral_fraction', 'core': .8, 'halo': .05, 'radius': 4}}}


def explore(report, fixtures):
    """Repeat finite grids; annotations never feed the detector or fixture evaluator."""
    correct = [winner for frame in report['frames'] for color in skin.study.COLORS
               for winner in [next((r for r in frame['rows'][color]
                                    if r['candidate']['eligible']), None)]
               if winner and winner['label'] == 'real']
    inventory = skin.study.REPO / 'docs/claude/analysis/2026-10-05_residual_fp_inventory.csv'
    with inventory.open(newline='') as handle:
        classes = {(r['original_sha256'], r['color']): r['class'] for r in csv.DictReader(handle)}
    negatives = {color: [] for color in skin.study.COLORS}
    for frame in report['frames']:
        if frame['gain'] != 1:
            continue
        for color in skin.study.COLORS:
            winner = next((r for r in frame['rows'][color] if r['candidate']['eligible']), None)
            category = classes.get((frame['sha256'], color))
            if winner and category in ('other', 'clothing/fabric'):
                negatives[color].append((category, winner))
    fields = ('deep_count', 'deep_fraction', 'warm_fraction', 'neutral_fraction',
              'clipped_fraction', 'cool_fraction', 'area', 'aspect', 'thickness',
              'emitterScore', 'clippedWhite')

    def value(row, key):
        if key == 'emitterScore':
            return row['candidate']['study_emitter_score']
        if key == 'clippedWhite':
            return row['candidate']['clipped_white']
        return row['features'][key]

    gaps, compact_results, white_valid = [], {}, []
    for color in skin.study.COLORS:
        positive = [r for r in correct if r['color'] == color]
        for category in ('other', 'clothing/fabric'):
            negative = [r for c, r in negatives[color] if c == category]
            if not positive or not negative:
                continue
            for key in fields:
                if color == 'blue' and key == 'warm_fraction':
                    continue  # RED production warm locus is not a BLUE feature.
                p, n = [value(r, key) for r in positive], [value(r, key) for r in negative]
                gaps.append(dict(color=color, category=category, feature=key,
                    positive_range=[min(p), max(p)], negative_range=[min(n), max(n)],
                    best_direction_gap=max(min(p) - max(n), min(n) - max(p))))
        valid = []
        for area, thickness, clipped in itertools.product(
                (48, 80, 120, 160, 240, 320, 480, 640, 960),
                (2, 3, 4, 6, 8, 10, 15, 20), (0, .01, .05, .1, .2)):
            rule = {'compact': {color: dict(area=area, thickness=thickness, clippedWhite=clipped)}}
            if all(accepts(r, rule) for r in correct):
                valid.append((sum(not accepts(r, rule) for c, r in negatives[color]), rule))
        best = max(valid, key=lambda pair: pair[0]) if valid else None
        compact_results[color] = dict(grid_n=360, retaining_n=len(valid),
            target_n=len(negatives[color]), maximum_rejected=best[0] if best else None,
            best=evaluate(report, fixtures, best[1]) if best else None)
    for color, metric, core, halo, radius in itertools.product(skin.study.COLORS,
            ('neutral_fraction', 'clipped_fraction'), (.3, .4, .5, .6, .7, .8, .9),
            (0, .01, .03, .05, .1, .2), (2, 4, 8)):
        rule = {'white': {color: dict(metric=metric, core=core, halo=halo, radius=radius)}}
        if all(accepts(r, rule) for r in correct):
            white_valid.append(rule)
    rules = [{}, *({'blue_deep_count': n} for n in (1, 5, 10, 14, 15, 20)),
             *({'blue_deep_fraction': n} for n in (.001, .002, .004, .005, .01)),
             *({'blue_cool': n} for n in (.3, .5, .7, .8, .85, .9)),
             {'white': DEFAULT_RULE['white']}, DEFAULT_RULE]
    return dict(single_feature_gaps=gaps, compact=compact_results,
                white_grid_n=504, white_retaining_n=len(white_valid),
                white_retaining_rules=white_valid,
                evaluated=[evaluate(report, fixtures, rule) for rule in rules])


if __name__ == '__main__':
    main()
