#!/usr/bin/env python3
"""Read-only RED skin/LED study; never changes the production detector or PNGs.

Reuses phone_saber_rule_study's temporary detector and gain/label routines.
Only the temporary diagnostic copies export actual candidate sample coordinates
and color-mask membership. Output JSON pins original paths and SHA-256; keep
local reports outside the repository. No third-party Python dependencies.
"""
from __future__ import annotations

import argparse
import copy
import hashlib
import json
import os
import statistics
import subprocess
import tempfile
from collections import Counter
from pathlib import Path

import phone_saber_rule_study as study
import run_lossless_regression as lossless
import run_background_negative_benchmark as background

LABELS = study.REPO / 'docs/claude/analysis/2026-10-03_background_fp_labels.csv'
SESSION = '20261004_232850_471'
GAINS = (0.92, 1.0, 1.08)
# Boxes in original PNG coordinates, visually reviewed on 2026-10-05.
# Not inferred from annotated winners. Mixed proposals can contain non-saber pixels.
EXTRA_TRUTHS = {
    **{frame: None for frame in range(1248, 1256)},
    3409: [282, 340, 326, 404], 3888: [256, 260, 320, 356],
    5449: [86, 334, 192, 394], 6288: None,
}
SKIN_LIGHT_REGIONS = {
    **{(SESSION, frame): [('skin', [175, 570, 235, 640]),
                         ('skin', [50, 110, 250, 175]),
                         ('skin', [95, 355, 290, 415]),
                         ('light', [105, 415, 165, 490])]
       for frame in [*range(1248, 1256), 3409, 3888, 5449, 6288]},
}


# Skin regions reviewed in ORIGINALS; midpoint assigns non-saber candidates.
for _session, _frames, _regions in [
    ('20260929_152037_074', [93, 96], [('skin', [300, 0, 480, 285])]),
    ('20261003_155919_297', range(5216, 5224), [('skin', [0, 380, 290, 640])]),
    ('20261003_155919_297', [5321, 5322, 5331],
     [('skin', [10, 0, 300, 405]), ('skin', [320, 70, 470, 335]), ('skin', [150, 445, 275, 640])]),
    ('20261003_144936_295', [1907, 1908, 1911], [('skin', [0, 480, 40, 640])]),
    ('20261004_005325_638', range(198, 206), [('skin', [260, 55, 360, 145])]),
    ('20261004_005855_691', [12632], [('skin', [180, 210, 470, 400])]),
    ('20260930_234740_348', [2439, 2441],
     [('skin', [0, 490, 215, 570]), ('skin', [320, 340, 440, 475])]),
    ('20261001_003921_526', [18215], [('skin', [340, 490, 480, 640])]),
    ('20261001_002509_264', [76899],
     [('skin', [245, 450, 330, 530]), ('skin', [0, 30, 85, 120]), ('skin', [280, 60, 330, 110])]),
]:
    for _frame in _frames:
        SKIN_LIGHT_REGIONS[(_session, _frame)] = _regions
# Independent visual boxes, including RED visible in BLUE-targeted fixtures.
# Manifest expectations remain untouched and are separately evaluated.
FORMAL_SABER_BOXES = {
    'frame_390': [140, 230, 210, 265], 'frame_397': [230, 220, 295, 265],
    'blue_dropout_false_1249': [155, 300, 220, 360],
    'frame-0220': [20, 380, 65, 430], 'frame-0600': [75, 280, 135, 400],
    'frame_251': [265, 250, 295, 315], 'frame_254': [265, 255, 295, 320],
    'frame_256': [265, 260, 295, 325], 'blue_dropout_false_361': [265, 335, 295, 380],
    'red_dropout_last_true_82': [425, 570, 480, 640],
    'red_dropout_last_true_89': [425, 600, 480, 640],
    'red_dropout_recovered_84': [425, 575, 480, 640],
    'red_dropout_recovered_112': [240, 560, 290, 640],
    'red_dropout_recovered_351': [220, 340, 270, 400],
    'frame_1000': [315, 365, 410, 415], 'frame_1048': [280, 480, 320, 540],
    'frame_919': [300, 480, 345, 530], 'frame_1091': [305, 450, 340, 510],
}
SKIN_LIGHT_REGIONS[('formal', 'red_dropout_last_true_89')] = [('light', [405, 110, 480, 210])]
SKIN_LIGHT_REGIONS[('formal', 'frame_390')] = [('light', [80, 140, 110, 200])]
SKIN_LIGHT_REGIONS[('formal', 'frame_1091')] = [('skin', [240, 340, 310, 430])]


def replace_once(text: str, anchor: str, replacement: str) -> str:
    if text.count(anchor) != 1:
        raise ValueError(f'ambiguous/missing study anchor: {anchor[:70]}')
    return text.replace(anchor, replacement, 1)


def build_harness(directory: Path) -> Path:
    """Instrument temporary copies, retaining all production eligibility/ranking."""
    core = study.patch_detection_core((study.SOURCES / 'DetectionCore.swift').read_text())
    core = replace_once(core, '    var diagnosticRejections: [SaberEligibilityDecision] = []',
                        '    var studyPoints: [[Int]] = []\n    var studyColorFlags: [Bool] = []\n'
                        '    var diagnosticRejections: [SaberEligibilityDecision] = []')
    core = replace_once(core, '    candidate.endpointDiagnosticTrace =',
                        '    candidate.studyPoints = points.map { [$0.x, $0.y] }\n'
                        '    candidate.studyColorFlags = points.map { (evidence?.colorMask[$0.y * width + $0.x] ?? 0) != 0 }\n'
                        '    candidate.endpointDiagnosticTrace =')
    bgra = (study.SOURCES / 'BGRADetection.swift').read_text()
    bgra = replace_once(bgra, '                diagnosticRejections: candidate.diagnosticRejections,',
                        '                studyPoints: candidate.studyPoints, studyColorFlags: candidate.studyColorFlags,\n'
                        '                diagnosticRejections: candidate.diagnosticRejections,')
    diag = study.DIAGNOSTIC.read_text()
    diag = replace_once(diag, '        "source": candidate.source,',
                        '        "study_points": candidate.studyPoints,\n'
                        '        "study_color_flags": candidate.studyColorFlags,\n'
                        '        "study_rejections": candidate.diagnosticRejections.map { ["name": $0.name, "value": $0.value.map { $0 as Any } ?? NSNull(), "comparison": $0.comparison.map { $0 as Any } ?? NSNull(), "threshold": $0.threshold.map { $0 as Any } ?? NSNull()] },\n'
                        '        "study_gates": candidate.endpointDiagnosticTrace?.gatingValues ?? [:],\n'
                        '        "study_compact_red": candidate.isCompactRed,\n'
                        '        "study_base_eligible": candidate.endpointDiagnosticTrace?.emitter?.baseEligible ?? candidate.isEmitterEligible,\n'
                        '        "study_emitter_score": candidate.endpointDiagnosticTrace?.emitter?.emitterScore ?? 0,\n'
                        '        "clipped_white": candidate.clippedWhiteRatio,\n'
                        '        "brightness_variation": candidate.brightnessVariation,\n'
                        '        "width_variation": candidate.widthVariation,\n'
                        '        "source": candidate.source,')
    directory.mkdir(parents=True, exist_ok=True)
    sources = []
    for name, text in [('DetectionCore.swift', core), ('BGRADetection.swift', bgra),
                       ('VideoDetectionDiagnostic.swift', diag)]:
        path = directory / name
        path.write_text(text)
        sources.append(str(path))
    binary = directory / 'skin-study-diagnostic'
    env = dict(os.environ, CLANG_MODULE_CACHE_PATH=str(directory / 'clang-cache'),
               SWIFT_MODULECACHE_PATH=str(directory / 'swift-cache'))
    subprocess.run(['xcrun', 'swiftc', '-O', *sources, '-o', str(binary)],
                   env=env, capture_output=True, check=True)
    return binary


def percentile(values: list[float], q: float) -> float:
    if not values:
        raise ValueError('empty percentile')
    values = sorted(values)
    position = (len(values) - 1) * q
    lo = int(position)
    hi = min(lo + 1, len(values) - 1)
    return values[lo] + (values[hi] - values[lo]) * (position - lo)


def pixel_features(rgb: list[tuple[int, int, int]]) -> dict[str, float]:
    """HSV and channel ratios on exact production component samples."""
    if not rgb:
        return {}
    gr, br, hue, sat, val, chroma = [], [], [], [], [], []
    for r, g, b in rgb:
        maximum, minimum = max(r, g, b), min(r, g, b)
        delta = maximum - minimum
        h = 0.0
        if delta:
            if maximum == r:
                h = 60 * ((g - b) / delta) % 360
            elif maximum == g:
                h = 60 * ((b - r) / delta + 2)
            else:
                h = 60 * ((r - g) / delta + 4)
        # Signed hue avoids the 0/360 discontinuity for red.
        hue.append(h if h <= 180 else h - 360)
        gr.append(g / max(r, 1)); br.append(b / max(r, 1))
        sat.append(delta / max(maximum, 1)); val.append(float(maximum)); chroma.append(float(delta))
    out = {}
    for key, values in [('gr', gr), ('br', br), ('hue', hue), ('saturation', sat), ('value', val), ('chroma', chroma)]:
        out[key + '_mean'] = statistics.fmean(values)
        for q in (0.1, 0.5, 0.9):
            out[key + f'_p{int(q * 100)}'] = percentile(values, q)
    out['value_std'] = statistics.pstdev(val)
    return out


def candidate_features(candidate: dict, raw: bytes, width: int, height: int) -> dict:
    points = candidate['study_points']
    flags = candidate['study_color_flags']
    if len(points) != len(flags):
        raise ValueError('point/mask mismatch')
    all_rgb, color_rgb, gradient = [], [], []
    for (x, y), colored in zip(points, flags):
        x *= 2; y *= 2
        i = (y * width + x) * 4
        b, g, r = raw[i:i + 3]
        all_rgb.append((r, g, b))
        if colored:
            color_rgb.append((r, g, b))
        if x + 2 < width:
            j = (y * width + x + 2) * 4
            gradient.append(abs(max(r, g, b) - max(raw[j:j + 3])))
    out = {'all_' + k: v for k, v in pixel_features(all_rgb).items()}
    out.update({'color_' + k: v for k, v in pixel_features(color_rgb).items()})
    for key in ('mean_value', 'color_purity', 'clipped_white', 'local_contrast',
                'core_support', 'longitudinal_core_coverage', 'high_value_ratio',
                'brightness_variation', 'width_variation', 'axial_density', 'point_count'):
        out[key] = candidate[key]
    box = study._box(candidate)
    sides = (box[2] - box[0] + 1, box[3] - box[1] + 1)
    out['box_aspect'] = max(sides) / min(sides)
    out['area'] = candidate['point_count'] * 4
    density = candidate['axial_density']
    if density <= 0:
        density = candidate['point_count'] / max(candidate['raw_pca_span'], 2.0)
    out['d240'] = density * 4 * 240 / min(width, height)
    out['gradient_mean'] = statistics.fmean(gradient) if gradient else 0
    return out


def auc(positive: list[float], negative: list[float]) -> float | None:
    """Pairwise ROC AUC, LED higher; ties contribute one half."""
    if not positive or not negative:
        return None
    return sum((a > b) + 0.5 * (a == b) for a in positive for b in negative) / (len(positive) * len(negative))


def label(candidate: dict, truth, session: str, frame: int) -> str:
    kind = study.label_candidate(candidate, truth)
    if kind == 'real':
        return 'saber'
    if kind == 'unk':
        return 'unknown'
    mx, my = study.midpoint(candidate)
    for category, box in SKIN_LIGHT_REGIONS.get((session, frame), []):
        if box[0] <= mx <= box[2] and box[1] <= my <= box[3]:
            return category
    return 'other'


def shadow_accept(features: dict) -> bool:
    # Exploratory definition frozen after analysis; no production application.
    return features['color_hue_mean'] <= 11.0 or features['core_support'] >= 0.28


def filtered(analysis: dict, features: list[dict], predicate=shadow_accept) -> dict:
    """Final-candidate shadow only: reselect first surviving eligible RED."""
    out = copy.deepcopy(analysis)
    red = copy.deepcopy(analysis['colors']['red'])
    out['colors']['red'] = red
    for candidate, values in zip(red['candidates'], features):
        candidate['eligible'] = candidate['eligible'] and predicate(values)
    best = study.winner(red)
    red['selected'] = best['endpoints'] if best else None
    return out


def inputs(inbox: Path) -> tuple[list[dict], list[dict]]:
    labels = study.load_labels(LABELS)
    for frame, truth in EXTRA_TRUTHS.items():
        labels[(SESSION, frame)] = {'red': truth, 'blue': None}
    entries = []
    for (session, frame), truths in labels.items():
        path = study.resolve_image(inbox, session, frame)
        if path is None:
            raise FileNotFoundError(f'labelled original missing: {session}/{frame}')
        entries.append({'source': 'labels', 'session': session, 'frame': frame,
                        'path': str(path), 'truth': truths['red']})
    _, fixtures = lossless.load_fixtures(lossless.DEFAULT_MANIFEST)
    for path in dict.fromkeys(f['resolved_path'] for f in fixtures):
        relevant = [f for f in fixtures if f['resolved_path'] == path]
        red = [f for f in relevant if f['color'] == 'RED']
        # Visual truth is independent of the frozen formal output expectation.
        entries.append({'source': 'formal', 'session': 'formal', 'frame': path.stem,
                        'path': str(path), 'fixtures': [f['name'] for f in relevant],
                        'red_positive': any(f['expectedDetected'] for f in red),
                        'truth': FORMAL_SABER_BOXES.get(path.stem)})
    _, negatives = background.load_manifest(background.DEFAULT_MANIFEST)
    existing = {e['path'] for e in entries}
    for image in negatives:
        state, path, error = background.locate(image, inbox)
        if state != 'available':
            raise ValueError(f'background original {image["id"]}: {state} {error}')
        if str(path) not in existing:
            entries.append({'source': 'benchmark', 'session': image['sourceSession'].replace('phonesaber_', ''),
                            'frame': image['frameID'], 'path': str(path), 'truth': None})
    return entries, fixtures


def shadow_score(features: dict) -> float:
    """Signed diagnostic score: >=0 passes; scales are only for ROC reporting."""
    return max(11.0 - features['color_hue_mean'], 100 * (features['core_support'] - 0.28))


def summarize(rows: list[dict], frames: list[dict], formal: list[dict]) -> dict:
    """Do not count adjacent frames, proposals or gain variants as independent."""
    result = {'by_gain': {}, 'sessions': {}, 'features': {}}
    for gain in GAINS:
        candidates = [r for r in rows if r['gain'] == gain and r['candidate']['eligible']]
        real_frames = [f for f in frames if f['gain'] == gain and f['source'] == 'labels'
                       and f['truth'] not in (None, '?')]
        result['by_gain'][str(gain)] = {
            'eligible_labels': dict(Counter(r['label'] for r in candidates)),
            'shadow_rejected_labels': dict(Counter(r['label'] for r in candidates
                                                  if not shadow_accept(r['features']))),
            'visible_red_frames': len(real_frames),
            'baseline_real_frames': sum(f['before'] == 'saber' for f in real_frames),
            'shadow_real_frames': sum(f['after'] == 'saber' for f in real_frames),
            'formal_base': sum(f['base']['passed'] for f in formal if f['gain'] == gain),
            'formal_shadow': sum(f['shadow']['passed'] for f in formal if f['gain'] == gain),
            'real_lost': [{'session': f['session'], 'frame': f['frame']} for f in frames
                          if f['gain'] == gain and f['before'] == 'saber' and f['after'] != 'saber'],
        }
    for session in dict.fromkeys(f['session'] for f in frames):
        fs = [f for f in frames if f['session'] == session and f['gain'] == 1]
        rs = [r for r in rows if r['session'] == session and r['gain'] == 1 and r['candidate']['eligible']]
        result['sessions'][session] = {
            'frames': len(fs), 'eligible_labels': dict(Counter(r['label'] for r in rs)),
            'before_bg': sum(f['before'] in ('skin', 'light', 'other') for f in fs),
            'after_bg': sum(f['after'] in ('skin', 'light', 'other') for f in fs),
            'before_real': sum(f['before'] == 'saber' for f in fs),
            'after_real': sum(f['after'] == 'saber' for f in fs),
        }
    eligible = [r for r in rows if r['gain'] == 1 and r['candidate']['eligible']]
    for key, sign in [('color_gr_mean', -1), ('color_br_mean', -1), ('color_hue_mean', -1),
                      ('color_value_p90', 1), ('color_purity', 1), ('clipped_white', 1),
                      ('core_support', 1), ('d240', 1), ('box_aspect', 1), ('area', 1),
                      ('local_contrast', 1), ('gradient_mean', 1), ('brightness_variation', 1)]:
        positive = [r['features'][key] for r in eligible if r['label'] == 'saber']
        negative = [r['features'][key] for r in eligible if r['label'] in ('skin', 'light')]
        result['features'][key] = {
            'auc_led_higher': auc([sign * p for p in positive], [sign * n for n in negative]),
            'direction': sign, 'led_range': [min(positive), max(positive)],
            'skin_light_range': [min(negative), max(negative)],
            'separation_gap': min(sign * p for p in positive) - max(sign * n for n in negative),
        }
    for gains in [(1.0,), GAINS]:
        rs = [r for r in rows if r['gain'] in gains and r['candidate']['eligible']]
        positive = [shadow_score(r['features']) for r in rs if r['label'] == 'saber']
        negative = [shadow_score(r['features']) for r in rs if r['label'] in ('skin', 'light')]
        result['features']['shadow_' + str(gains)] = {
            'auc_led_higher': auc(positive, negative),
            'separation_gap': min(positive) - max(negative),
            'led_range': [min(positive), max(positive)],
            'skin_light_range': [min(negative), max(negative)],
        }
    return result


def run(inbox: Path, output: Path) -> None:
    entries, fixtures = inputs(inbox)
    rows, frames, formal = [], [], []
    with tempfile.TemporaryDirectory(prefix='phonesaber-skin-') as tmp:
        binary = build_harness(Path(tmp))
        for index, entry in enumerate(entries):
            path = Path(entry['path'])
            width, height = lossless.dimensions(path)
            original = lossless.command_output(['ffmpeg', '-loglevel', 'error', '-i', str(path),
                                                '-frames:v', '1', '-f', 'rawvideo', '-pix_fmt', 'bgra', '-'])
            sha = hashlib.sha256(path.read_bytes()).hexdigest()
            for gain in GAINS:
                raw = study.apply_gain(original, gain)
                env = {k: v for k, v in os.environ.items() if k not in study.ENV_KEYS}
                analysis = json.loads(subprocess.run([str(binary), str(width), str(height), '2'], input=raw,
                                                     env=env, capture_output=True, check=True).stdout.splitlines()[0])
                features = [candidate_features(c, raw, width, height) for c in analysis['colors']['red']['candidates']]
                truth = entry['truth']
                for rank, (candidate, values) in enumerate(zip(analysis['colors']['red']['candidates'], features)):
                    rows.append({'session': entry['session'], 'frame': entry['frame'], 'source': entry['source'],
                                 'path': entry['path'], 'sha256': sha, 'gain': gain, 'rank': rank,
                                 'label': label(candidate, truth, entry['session'], entry['frame']),
                                 'candidate': {k: v for k, v in candidate.items() if not k.startswith('study_')},
                                 'features': values})
                gated = filtered(analysis, features)
                before, after = study.winner(analysis['colors']['red']), study.winner(gated['colors']['red'])
                frames.append({**entry, 'sha256': sha, 'gain': gain,
                               'before': label(before, truth, entry['session'], entry['frame']) if before else 'none',
                               'after': label(after, truth, entry['session'], entry['frame']) if after else 'none',
                               'eligible': sum(c['eligible'] for c in analysis['colors']['red']['candidates'])})
                for fixture in fixtures:
                    if str(fixture['resolved_path']) == entry['path']:
                        formal.append({'name': fixture['name'], 'gain': gain,
                                       'base': lossless.evaluate_fixture(fixture, analysis),
                                       'shadow': lossless.evaluate_fixture(fixture, gated)})
            if index % 20 == 0:
                print(f'{index + 1}/{len(entries)} originals replayed', flush=True)
    report = {'gains': GAINS, 'rows': rows, 'frames': frames, 'formal': formal,
              'summary': summarize(rows, frames, formal),
              'sources_sha256': {str(p.relative_to(study.REPO)): hashlib.sha256(p.read_bytes()).hexdigest()
                                 for p in [LABELS, study.SOURCES / 'DetectionCore.swift',
                                           study.SOURCES / 'BGRADetection.swift', study.DIAGNOSTIC,
                                           lossless.DEFAULT_MANIFEST, background.DEFAULT_MANIFEST]}}
    output.write_text(json.dumps(report, indent=2) + '\n')
    print(f'{len(entries)} originals, {len(rows)} candidate observations -> {output}')


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--inbox', type=Path, default=study.DEFAULT_INBOX)
    parser.add_argument('--json', type=Path, required=True)
    args = parser.parse_args()
    if args.json.resolve().is_relative_to(study.REPO):
        parser.error('--json must be outside the repository (private evidence metadata)')
    run(args.inbox, args.json)


if __name__ == '__main__':
    main()
