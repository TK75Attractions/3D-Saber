#!/usr/bin/env python3
"""Offline deep-red support grid on original candidate pixels (requires NumPy).

Reuses skin-study labels and temporary Swift instrumentation. Production sources,
fixtures and original PNGs are read only. Private JSON must be outside the repo.
Counts are unique full-resolution pixels, not bbox pixels or estimated areas.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import subprocess
import tempfile
from collections import Counter
from pathlib import Path

import phone_saber_skin_study as skin

GR_LIMITS = (0.35, 0.40, 0.45, 0.50, 0.55)
BR_LIMITS = (0.45, 0.55, 0.65)
BRIGHTNESS = (('absolute', 120), ('absolute', 150), ('absolute', 180),
              ('percentile', 50), ('percentile', 75), ('percentile', 90))
GRID = [{'gr': gr, 'br': br, 'brightness': kind, 'r': value}
        for gr in GR_LIMITS for br in BR_LIMITS for kind, value in BRIGHTNESS]
DOMAINS = ('sample', 'dilate1')
SHADOW_GRID_INDEX = GRID.index({'gr': 0.40, 'br': 0.65, 'brightness': 'absolute', 'r': 180})
WARM_BG_LIMITS = (0.85, 0.90, 0.95)
WARM_THRESHOLDS = (0.3, 0.5, 0.7)
WARM_GRID = [{'bg': bg, 'fraction': fraction}
             for bg in WARM_BG_LIMITS for fraction in WARM_THRESHOLDS]


def pixel_indices(points: list[list[int]], width: int, height: int,
                  radius: int = 0, sample_step: int = 2) -> list[int]:
    """Square/Chebyshev dilation, clipped to frame, unique original pixels."""
    if radius < 0 or sample_step < 1:
        raise ValueError('invalid radius/sample step')
    indices = set()
    for x, y in points:
        x *= sample_step
        y *= sample_step
        if not (0 <= x < width and 0 <= y < height):
            raise ValueError('sample outside original frame')
        for yy in range(max(0, y - radius), min(height, y + radius + 1)):
            for xx in range(max(0, x - radius), min(width, x + radius + 1)):
                indices.add(yy * width + xx)
    return sorted(indices)


def support_features(raw: bytes, indices: list[int], grid: list[dict] = GRID) -> dict:
    import numpy as np

    if len(raw) % 4:
        raise ValueError('invalid BGRA byte count')
    if indices and (min(indices) < 0 or max(indices) >= len(raw) // 4):
        raise ValueError('pixel outside buffer')
    if len(indices) != len(set(indices)):
        raise ValueError('support domain must contain unique pixels')
    pixels = np.frombuffer(raw, dtype=np.uint8).reshape(-1, 4)[indices, :3].astype(float)
    if not len(pixels):
        return {'size': 0, 'counts': [0] * len(grid), 'fractions': [0.0] * len(grid),
                'clipped_channels': 0, 'r_percentiles': {},
                'warm_counts': [0] * len(WARM_BG_LIMITS),
                'warm_fractions': [0.0] * len(WARM_BG_LIMITS),
                'r120_count': 0, 'r120_zero_g': 0, 'median_bg': None, 'median_gr': None}
    b, g, r = pixels.T
    gr = np.divide(g, r, out=np.zeros_like(g), where=r > 0)
    br = np.divide(b, r, out=np.zeros_like(b), where=r > 0)
    # B/G is undefined at G=0. Exclude those pixels only from its median,
    # record their count, and retain them in the warmFrac denominator.
    bg = np.divide(b, g, out=np.zeros_like(b), where=g > 0)
    bright = r >= 120
    warm = bright & (gr >= 0.45) & (gr <= 0.92)
    warm_counts = [int(np.count_nonzero(warm & (bg <= limit))) for limit in WARM_BG_LIMITS]
    percentiles = {str(p): float(np.percentile(r, p)) for p in (50, 75, 90)}
    counts = []
    for rule in grid:
        floor = rule['r'] if rule['brightness'] == 'absolute' else percentiles[str(rule['r'])]
        # Compare actual ratios: multiplication can include exact boundaries
        # accidentally (e.g. 200 * 0.55 evaluates slightly above 110).
        mask = (r > 0) & (gr < rule['gr']) & (br < rule['br']) & (r >= floor)
        counts.append(int(np.count_nonzero(mask)))
    return {'size': len(indices), 'counts': counts,
            'fractions': [c / len(indices) for c in counts],
            'clipped_channels': int(np.count_nonzero(pixels == 255)),
            'r_percentiles': percentiles,
            'warm_counts': warm_counts, 'warm_fractions': [c / len(indices) for c in warm_counts],
            'r120_count': int(np.count_nonzero(bright)),
            'r120_zero_g': int(np.count_nonzero(bright & (g == 0))),
            'median_bg': float(np.median(bg[bright & (g > 0)])) if np.any(bright & (g > 0)) else None,
            'median_gr': float(np.median(gr[bright])) if np.any(bright) else None}


def deep_accept(features: dict, domain: str, grid_index: int, metric: str,
                threshold: float) -> bool:
    """Final ranked eligible candidate gate; caller retains original eligibility."""
    return features[domain][metric][grid_index] >= threshold


def warm_accept(features: dict, bg: float, fraction: float) -> bool:
    """Keep deep-supported or insufficiently warm candidates; eligibility is external."""
    values = features['dilate1']
    return (values['counts'][SHADOW_GRID_INDEX] > 0
            or values['warm_fractions'][WARM_BG_LIMITS.index(bg)] < fraction)


def summarize(report: dict, fixtures: list[dict], domain: str = 'dilate1',
              grid_index: int = SHADOW_GRID_INDEX, metric: str = 'counts',
              threshold: float = 1, warm_rule: dict | None = None,
              include_grid_margins: bool = True) -> dict:
    """Measure frozen shadow; zero-support sabers remain in all recall denominators."""
    rows = report['rows']
    by_name = {f['name']: f for f in fixtures}
    predicate = (lambda v: warm_accept(v, warm_rule['bg'], warm_rule['fraction'])) if warm_rule else (
        lambda v: deep_accept(v, domain, grid_index, metric, threshold))

    def diagnostic(row):
        if row is None:
            return None
        values = row['features'][domain]
        return {'rank': row['rank'], 'label': row['label'],
                'eligible': row['candidate']['eligible'], 'score': row['candidate'].get('score'),
                'support': values[metric][grid_index],
                'warm_fractions': values.get('warm_fractions'),
                'median_bg': values.get('median_bg'), 'median_gr': values.get('median_gr'),
                'rejections': row['candidate'].get('study_rejections', [])}

    outcomes, formal = [], []
    for frame in report['frames']:
        rs = [rows[i] for i in frame['rows']]
        gated = skin.filtered(frame['analysis'], [r['features'] for r in rs], predicate)
        before = next((r for r in rs if r['candidate']['eligible']), None)
        after = next((r for r in rs if r['candidate']['eligible'] and predicate(r['features'])), None)
        eligible_real = [r for r in rs if r['candidate']['eligible'] and r['label'] == 'saber']
        outcomes.append({k: frame[k] for k in ('session', 'frame', 'source', 'gain', 'truth')}
                        | {'before': before['label'] if before else 'none',
                           'after': after['label'] if after else 'none',
                           'before_rank': before['rank'] if before else None,
                           'after_rank': after['rank'] if after else None,
                           'endpoint': after['candidate']['endpoints'] if after else None,
                           'eligible_sabers': len(eligible_real),
                           'max_saber_support': max((r['features'][domain][metric][grid_index]
                                                     for r in eligible_real), default=None),
                           'winner_support': after['features'][domain][metric][grid_index] if after else None,
                           'before_diagnostic': diagnostic(before), 'after_diagnostic': diagnostic(after),
                           'saber_diagnostics': [diagnostic(r) for r in rs if r['label'] == 'saber']})
        for base in frame['formal_base']:
            formal.append({'gain': frame['gain'], 'name': base['name'], 'base': base,
                           'shadow': skin.lossless.evaluate_fixture(by_name[base['name']], gated)})
    summary = {'rule': warm_rule or {'domain': domain, 'grid': report['grid'][grid_index],
                                    'metric': metric, 'threshold': threshold},
               'gains': {}, 'sessions': {}, 'outcomes': outcomes, 'formal': formal, 'grid_margins': []}
    for gain in report['gains']:
        rs = [r for r in rows if r['gain'] == gain and r['candidate']['eligible']]
        fs = [f for f in outcomes if f['gain'] == gain]
        labelled = [f for f in fs if f['source'] == 'labels' and f['truth'] not in (None, '?')]
        absent = [f for f in fs if f['source'] == 'labels' and f['truth'] is None]
        summary['gains'][str(gain)] = {
            'eligible': dict(Counter(r['label'] for r in rs)),
            'rejected': dict(Counter(r['label'] for r in rs
                                    if not predicate(r['features']))),
            'labelled_recall': [sum(f['after'] == 'saber' for f in labelled), len(labelled)],
            'baseline_labelled_recall': sum(f['before'] == 'saber' for f in labelled),
            'absent_fp': [sum(f['before'] != 'none' for f in absent),
                          sum(f['after'] != 'none' for f in absent)],
            'correct_retention': [sum(f['before'] == f['after'] == 'saber' for f in fs),
                                  sum(f['before'] == 'saber' for f in fs)],
            'correct_output_retention': [sum(f['before'] == 'saber' and f['before_rank'] == f['after_rank']
                                             for f in fs), sum(f['before'] == 'saber' for f in fs)],
            'correct_by_source': {source: [sum(f['before'] == 'saber' and f['before_rank'] == f['after_rank']
                                              for f in fs if f['source'] == source),
                                          sum(f['before'] == 'saber' for f in fs if f['source'] == source)]
                                  for source in ('labels', 'formal')},
            'formal': [sum(f['base']['passed'] for f in formal if f['gain'] == gain),
                       sum(f['shadow']['passed'] for f in formal if f['gain'] == gain)],
        }
    for session in dict.fromkeys(f['session'] for f in outcomes):
        fs = [f for f in outcomes if f['session'] == session and f['gain'] == 1]
        rs = [r for r in rows if r['session'] == session and r['gain'] == 1 and r['candidate']['eligible']]
        summary['sessions'][session] = {
            'frames': len(fs), 'visible': sum(f['truth'] not in (None, '?') for f in fs),
            'real': [sum(f['before'] == 'saber' for f in fs), sum(f['after'] == 'saber' for f in fs)],
            'bg': [sum(f['before'] in ('skin', 'light', 'other') for f in fs),
                   sum(f['after'] in ('skin', 'light', 'other') for f in fs)],
            'eligible': dict(Counter(r['label'] for r in rs)),
            'rejected': dict(Counter(r['label'] for r in rs
                                    if not predicate(r['features']))),
        }
    # No zero-support exclusions: all positive/negative extrema are retained.
    for gains in ([(1.0,), tuple(report['gains'])] if include_grid_margins else []):
        rs = [r for r in rows if r['gain'] in gains and r['candidate']['eligible']]
        for d in report['domains']:
            for m in ('counts', 'fractions'):
                for k, rule in enumerate(report['grid']):
                    p = [r['features'][d][m][k] for r in rs if r['label'] == 'saber']
                    n = [r['features'][d][m][k] for r in rs if r['label'] in ('skin', 'light')]
                    summary['grid_margins'].append({'gains': gains, 'domain': d, 'metric': m, 'grid': rule,
                                                   'positive_min': min(p), 'negative_max': max(n),
                                                   'gap': min(p) - max(n)})
    return summary


def distribution(values: list[float | None]) -> dict:
    """Ranges and quantiles include zero fractions; missing medians are explicit."""
    import numpy as np

    present = [v for v in values if v is not None]
    return {'n': len(values), 'missing': len(values) - len(present),
            'min_p10_p50_p90_max': [float(v) for v in np.percentile(present, [0, 10, 50, 90, 100])]
            if present else None}


def summarize_warm(report: dict, fixtures: list[dict]) -> dict:
    """Nine frozen warm gates, original expectations, and exact winner retention.

    Margin = min(w - maximum warmFrac of currently-correct zero-deep winners,
                 minimum warmFrac of zero-deep skin/light - w), across all gains.
    Deep-supported negatives cannot be removed by this rule and are reported.
    A negative margin is not a separating definition, even if winner recall holds.
    """
    rows = report['rows']
    correct = [rows[next(i for i in frame['rows'] if rows[i]['candidate']['eligible'])]
               for frame in report['frames']
               if any(rows[i]['candidate']['eligible'] for i in frame['rows'])]
    correct = [r for r in correct if r['label'] == 'saber']
    zero_deep = lambda r: r['features']['dilate1']['counts'][SHADOW_GRID_INDEX] == 0
    negatives = [r for r in rows if r['candidate']['eligible'] and r['label'] in ('skin', 'light')]
    result = {'warm_bg_limits': WARM_BG_LIMITS, 'warm_thresholds': WARM_THRESHOLDS,
              'distributions': {}, 'rules': [],
              'deep_supported_skin_light': len([r for r in negatives if not zero_deep(r)]),
              'r120_zero_g_pixels': sum(r['features']['dilate1']['r120_zero_g'] for r in rows)}
    for population in ('all', 'eligible'):
        result['distributions'][population] = {}
        for gain in report['gains']:
            result['distributions'][population][str(gain)] = {}
            for label in ('saber', 'skin', 'light', 'other', 'unknown'):
                rs = [r for r in rows if r['gain'] == gain and r['label'] == label
                      and (population == 'all' or r['candidate']['eligible'])]
                result['distributions'][population][str(gain)][label] = {
                    'warm_fractions': {str(bg): distribution([r['features']['dilate1']['warm_fractions'][k]
                                                             for r in rs])
                                       for k, bg in enumerate(WARM_BG_LIMITS)},
                    **{key: distribution([r['features']['dilate1'][key] for r in rs])
                       for key in ('median_bg', 'median_gr')}}
    for rule in WARM_GRID:
        summary = summarize(report, fixtures, warm_rule=rule, include_grid_margins=False)
        k = WARM_BG_LIMITS.index(rule['bg'])
        positives = [r['features']['dilate1']['warm_fractions'][k] for r in correct if zero_deep(r)]
        neg = [r['features']['dilate1']['warm_fractions'][k] for r in negatives if zero_deep(r)]
        pmax, nmin = max(positives, default=0), min(neg, default=1)
        summary['margin'] = {'correct_zero_deep_n': len(positives), 'negative_zero_deep_n': len(neg),
                             'correct_max': pmax, 'negative_min': nmin, 'class_gap': nmin - pmax,
                             'retention_clearance': rule['fraction'] - pmax,
                             'rejection_clearance': nmin - rule['fraction'],
                             'worst_clearance': min(rule['fraction'] - pmax, nmin - rule['fraction'])}
        for gain in report['gains']:
            formal = [f for f in summary['formal'] if f['gain'] == gain]
            is_89 = lambda f: Path(f['base']['path']).stem == 'red_dropout_last_true_89'
            unchanged = [f for f in formal if not is_89(f)]
            summary['gains'][str(gain)]['formal_except89'] = [
                sum(f['base']['passed'] for f in unchanged),
                sum(f['shadow']['passed'] for f in unchanged), len(unchanged)]
            summary['gains'][str(gain)]['fixture89'] = [f for f in formal if is_89(f)]
        summary['formal_sessions'] = {}
        for source in dict.fromkeys(f['base']['sourceSession'] for f in summary['formal']):
            fs = [f for f in summary['formal'] if f['gain'] == 1 and f['base']['sourceSession'] == source]
            summary['formal_sessions'][source] = [sum(f['base']['passed'] for f in fs),
                                                   sum(f['shadow']['passed'] for f in fs), len(fs)]
        summary['preserves_correct_outputs'] = all(v['correct_output_retention'][0] == v['correct_output_retention'][1]
                                                   for v in summary['gains'].values())
        nominal = summary['gains']['1.0']['formal_except89']
        summary['preserves_nominal_formal_except89'] = nominal[0] == nominal[1] == nominal[2]
        failures = {'real_frames': [], 'absent_fp': [], 'rejected_saber_candidates': [],
                    'retained_skin_light_candidates': [], 'formal': []}
        for outcome in summary['outcomes']:
            if outcome['truth'] not in (None, '?') and outcome['after'] != 'saber':
                sabers = outcome['saber_diagnostics']
                eligible = [r for r in sabers if r['eligible']]
                surviving = [r for r in eligible if r['support'] > 0 or r['warm_fractions'][k] < rule['fraction']]
                reason = ('no_saber_generated' if not sabers else 'all_sabers_ineligible' if not eligible
                          else 'all_eligible_sabers_warm_rejected' if not surviving else 'surviving_saber_loses_score_rank')
                failures['real_frames'].append({'reason': reason, **outcome})
            if outcome['source'] == 'labels' and outcome['truth'] is None and outcome['after'] != 'none':
                winner = outcome['after_diagnostic']
                reason = 'deep_support_exemption' if winner['support'] > 0 else 'warm_fraction_below_threshold'
                failures['absent_fp'].append({'reason': reason, **outcome})
        for index, row in enumerate(rows):
            if not row['candidate']['eligible']:
                continue
            accept = warm_accept(row['features'], rule['bg'], rule['fraction'])
            values = row['features']['dilate1']
            details = {'row': index, 'session': row['session'], 'frame': row['frame'],
                       'gain': row['gain'], 'rank': row['rank'], 'label': row['label'],
                       'deep_count': values['counts'][SHADOW_GRID_INDEX],
                       'warm_fraction': values['warm_fractions'][k],
                       'median_bg': values['median_bg'], 'median_gr': values['median_gr'],
                       'score': row['candidate'].get('score')}
            if row['label'] == 'saber' and not accept:
                failures['rejected_saber_candidates'].append({'reason': 'zero_deep_and_warm', **details})
            if row['label'] in ('skin', 'light') and accept:
                reason = 'deep_support_exemption' if details['deep_count'] > 0 else 'warm_fraction_below_threshold'
                failures['retained_skin_light_candidates'].append({'reason': reason, **details})
        failures['formal'] = [{'reason': 'changed_formal_output' if f['base']['passed'] else 'baseline_already_failed', **f}
                              for f in summary['formal'] if not f['shadow']['passed']]
        summary['failures'] = failures
        result['rules'].append(summary)
    valid = [s for s in result['rules'] if s['preserves_correct_outputs'] and s['preserves_nominal_formal_except89']]
    chosen = max(valid, key=lambda s: (s['margin']['worst_clearance'],
                                      sum(sum(v['rejected'].get(c, 0) for c in ('skin', 'light'))
                                          for v in s['gains'].values())), default=None)
    result['chosen'] = chosen['rule'] if chosen else None
    result['positive_separation_margin_exists'] = any(s['margin']['worst_clearance'] > 0 for s in valid)
    recall_margin = max(valid, key=lambda s: s['margin']['retention_clearance'], default=None)
    result['widest_retention_clearance_rule'] = recall_margin['rule'] if recall_margin else None
    return result


def run(inbox: Path, output: Path) -> None:
    entries, fixtures = skin.inputs(inbox)
    rows, frames = [], []
    with tempfile.TemporaryDirectory(prefix='phonesaber-deep-red-') as tmp:
        directory = Path(tmp)
        binary = skin.build_harness(directory / 'instrumented')
        # A separately compiled unmodified harness verifies every replay, all gains.
        os.environ['CLANG_MODULE_CACHE_PATH'] = str(directory / 'control-clang')
        os.environ['SWIFT_MODULECACHE_PATH'] = str(directory / 'control-swift')
        control = directory / 'control'
        skin.lossless.compile_harness(control)
        env = {k: v for k, v in os.environ.items() if k not in skin.study.ENV_KEYS}
        for index, entry in enumerate(entries):
            path = Path(entry['path'])
            width, height = skin.lossless.dimensions(path)
            original = skin.lossless.command_output(
                ['ffmpeg', '-loglevel', 'error', '-i', str(path), '-frames:v', '1',
                 '-f', 'rawvideo', '-pix_fmt', 'bgra', '-'])
            sha = hashlib.sha256(path.read_bytes()).hexdigest()
            gained = {gain: skin.study.apply_gain(original, gain) for gain in skin.GAINS}
            for gain in skin.GAINS:
                raw = gained[gain]
                args = [str(width), str(height), '2']
                analysis = json.loads(subprocess.run([str(binary), *args], input=raw,
                                      env=env, capture_output=True, check=True).stdout.splitlines()[0])
                baseline = json.loads(subprocess.run([str(control), *args], input=raw,
                                      env=env, capture_output=True, check=True).stdout.splitlines()[0])
                for color in ('red', 'blue'):
                    actual, expected = analysis['colors'][color], baseline['colors'][color]
                    assert actual['selected'] == expected['selected'], (path, gain, color, 'selected')
                    assert len(actual['candidates']) == len(expected['candidates']), (path, gain, color)
                    for c, b in zip(actual['candidates'], expected['candidates']):
                        assert all(c[k] == v for k, v in b.items()), (path, gain, color, 'parity')
                row_indices = []
                for rank, candidate in enumerate(analysis['colors']['red']['candidates']):
                    domains = {name: pixel_indices(candidate['study_points'], width, height, radius)
                               for name, radius in zip(DOMAINS, (0, 1))}
                    values = {name: support_features(raw, indices) for name, indices in domains.items()}
                    fixed = ({str(g): {name: support_features(gained[g], indices)
                                      for name, indices in domains.items()} for g in skin.GAINS}
                             if gain == 1 else None)
                    row_indices.append(len(rows))
                    rows.append({'session': entry['session'], 'frame': entry['frame'],
                                 'source': entry['source'], 'path': str(path), 'sha256': sha,
                                 'gain': gain, 'rank': rank,
                                 'label': skin.label(candidate, entry['truth'], entry['session'], entry['frame']),
                                 'candidate': {k: v for k, v in candidate.items()
                                               if k not in ('study_points', 'study_color_flags')},
                                 'features': values, 'fixed_domain_gains': fixed})
                # Keep full baseline diagnostics for exact formal evaluation later.
                frames.append({**entry, 'sha256': sha, 'gain': gain, 'width': width, 'height': height,
                               'analysis': baseline, 'rows': row_indices,
                               'formal_base': [skin.lossless.evaluate_fixture(f, baseline) for f in fixtures
                                               if str(f['resolved_path']) == entry['path']]})
            if index % 10 == 0:
                print(f'{index + 1}/{len(entries)} originals; all-gain RED/BLUE parity PASS', flush=True)
    sources = [skin.LABELS, skin.study.SOURCES / 'DetectionCore.swift',
               skin.study.SOURCES / 'BGRADetection.swift', skin.study.DIAGNOSTIC,
               skin.lossless.DEFAULT_MANIFEST, skin.background.DEFAULT_MANIFEST,
               Path(skin.__file__), Path(__file__)]
    report = {'grid': GRID, 'domains': DOMAINS, 'gains': skin.GAINS,
              'rows': rows, 'frames': frames, 'parity_replays': len(frames),
              'sources_sha256': {str(p.relative_to(skin.study.REPO)):
                                 hashlib.sha256(p.read_bytes()).hexdigest() for p in sources}}
    report['summary'] = summarize(report, fixtures)
    report['warm_study'] = summarize_warm(report, fixtures)
    output.write_text(json.dumps(report, separators=(',', ':')) + '\n')
    print(f'{len(entries)} originals; {len(rows)} RED candidates -> {output}', flush=True)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--inbox', type=Path, default=skin.study.DEFAULT_INBOX)
    parser.add_argument('--json', type=Path, required=True)
    parser.add_argument('--summarize-from', type=Path,
                        help='recompute summary from a previously exported private replay JSON')
    args = parser.parse_args()
    if args.json.resolve().is_relative_to(skin.study.REPO):
        parser.error('--json must be outside the repository (private evidence metadata)')
    if args.summarize_from:
        _, fixtures = skin.lossless.load_fixtures(skin.lossless.DEFAULT_MANIFEST)
        report = json.loads(args.summarize_from.read_text())
        summary = {'deep': summarize(report, fixtures),
                   'replay_sources_sha256': report['sources_sha256'],
                   'summary_source_sha256': hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
                   'parity_replays': report['parity_replays']}
        if report['rows'] and 'warm_fractions' in report['rows'][0]['features']['dilate1']:
            summary['warm'] = summarize_warm(report, fixtures)
        args.json.write_text(json.dumps(summary, indent=2) + '\n')
    else:
        run(args.inbox, args.json)


if __name__ == '__main__':
    main()
