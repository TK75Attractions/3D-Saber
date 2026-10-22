#!/usr/bin/env python3
"""iPhone AutoClip の記録だけを読む ISO 比較（標準ライブラリのみ）。

  python3 PhoneSaber/tools/iso_compare.py --date 2026-10-23
  python3 PhoneSaber/tools/iso_compare.py A=/private/clips-a B=/private/clips-b
  python3 PhoneSaber/tools/iso_compare.py /private/clip --labels /private/labels.json

非公開 field-regression の sidecar または {"frames": [...]} をラベルとして読む。
記録に station がなければ A=/path / B=/path で指定する。端末は iPhone のみ。
露光/ISO は実測値で分類し、要求値 shutterISO から補わない。現在の AutoClip の
camera はトリガー時スナップショットであり、窓内の設定変更は検証できない。
真値を未ラベルフレームへ伝播せず、認識・採点・画像の再解析は実行しない。
"""
from __future__ import annotations

import argparse
from collections import defaultdict
from datetime import date
import hashlib
import json
import math
from pathlib import Path
import re
import statistics
import sys

TOOLS = Path(__file__).resolve().parents[1] / 'ios/PhoneSaberSender/Tools'
sys.path.insert(0, str(TOOLS))
from phone_saber_clip_report import find_clips, load_clip, frame_candidates, frame_output

PRIVATE = Path.home() / 'Library/Application Support/PhoneSaber'
COLORS = ('red', 'blue')
METRICS = ('meanValue', 'peakValue', 'colorPurity', 'highValueRatio',
           'emitterScore', 'nearSaturationPresent')


def number(value):
    return float(value) if type(value) in (int, float) and math.isfinite(value) else None


def median(values):
    return statistics.median(values) if values else None


def quantile10(values):
    return sorted(values)[max(0, math.ceil(len(values) * .1) - 1)] if values else None


def endpoints(candidate):
    return [candidate[k] for k in ('x1', 'y1', 'x2', 'y2')]


def midpoint(points):
    return ((points[0] + points[2]) / 2, (points[1] + points[3]) / 2)


def length(points):
    return math.dist(points[:2], points[2:])


def endpoint_error(a, b):
    # 非公開 field_regression.evaluate と同じ、向きに依存しない両端の最大誤差。
    return min(max(math.dist(a[:2], b[:2]), math.dist(a[2:], b[2:])),
               max(math.dist(a[:2], b[2:]), math.dist(a[2:], b[:2])))


def tolerance(truth):
    return max(12.0, .15 * length(truth))


def point_segment_distance(point, segment):
    a, b = segment[:2], segment[2:]
    dx, dy = b[0] - a[0], b[1] - a[1]
    scale = dx * dx + dy * dy
    u = max(0, min(1, ((point[0]-a[0])*dx + (point[1]-a[1])*dy) / scale)) if scale else 0
    return math.dist(point, (a[0] + u * dx, a[1] + u * dy))


def identity(points, truth):
    if truth == 'absent':
        return 'competitor'
    if truth is None:
        return 'unknown'
    if endpoint_error(points, truth) <= tolerance(truth):
        return 'blade'
    # 剣の部分候補を背景と誤認しない。線分近傍の不一致は保留する。
    if min(point_segment_distance(p, truth) for p in
           (points[:2], points[2:], midpoint(points))) > tolerance(truth):
        return 'competitor'
    return 'unknown'


def load_labels(path):
    labels = {}
    paths = sorted(path.glob('*.json')) if path.is_dir() else ([path] if path.exists() else [])
    for source in paths:
        doc = json.loads(source.read_text(encoding='utf-8'))
        for item in doc.get('frames', [doc]):
            key = (item['sourceClip'], item['image'])
            if key in labels and labels[key] != item:
                raise ValueError(f'競合するラベル: {key}')
            for color in COLORS:
                truth = item.get(color)
                if truth == 'absent' or truth is None:
                    continue
                if (not isinstance(truth, list) or len(truth) != 2 or
                        any(number(p.get(k)) is None for p in truth for k in ('x', 'y'))):
                    raise ValueError(f'不正な端点ラベル: {key} {color}')
            labels[key] = item
    return labels


def frame_labels(model, labels):
    found = {}
    for image in model['summary'].get('images', []):
        label = labels.get((model['bundle'].name, Path(image['path']).name))
        if label is None:
            continue
        if label.get('sha256'):
            digest = hashlib.sha256((model['bundle'] / image['path']).read_bytes()).hexdigest()
            if digest != label['sha256']:
                raise ValueError(f'ラベルと原画像のハッシュ不一致: {image["path"]}')
        frame_id = image['frameID']
        if frame_id in found and found[frame_id] != label:
            raise ValueError(f'フレームのラベルが競合: {frame_id}')
        found[frame_id] = label
    return found


def observations(bundle, station, labels):
    model = load_clip(bundle)
    clip = model['clip']
    labelled = frame_labels(model, labels)
    contexts = {}
    # summary の間引きで消えた画像フレームも、保存済み context から回収する。
    for image in model['summary'].get('images', []):
        context_path = image.get('frameContextPath')
        if context_path:
            doc = json.loads((bundle / context_path).read_text(encoding='utf-8'))
            contexts.update((f['frameID'], f) for f in doc.get('frames', []))
    ids = sorted(set(model['frames']) | set(contexts))
    camera = clip.get('camera', {})
    for frame_id in ids:
        frame = model['frames'].get(frame_id, {})
        context = contexts.get(frame_id, {})
        per_camera = frame.get('camera') or context.get('camera') or {}
        measured = {**camera, **per_camera}
        duration, iso = (number(measured.get(k)) for k in ('exposureDurationSeconds', 'iso'))
        emitter = measured.get('shutterEmitter')
        emitter = emitter if type(emitter) is bool else None
        group_station = station or clip.get('station') or model['summary'].get('station') or '?'
        if group_station not in ('A', 'B', '?'):
            raise ValueError(f'iPhone station は A/B のみ: {group_station}')
        for color in COLORS:
            candidates = frame_candidates(model, frame_id, color)
            entry = frame.get(color) or {}
            ctx_entry = context.get(color) or {}
            total = entry.get('n', ctx_entry.get('candidateCount'))
            eligible = entry.get('e', ctx_entry.get('eligibleCandidateCount'))
            # 画像contextは間引き前の全保存候補。summaryが薄いときだけ置き換える。
            if len(ctx_entry.get('autoClipCandidates', [])) > len(candidates):
                fallback = {**model, 'frames': {}}
                candidates = frame_candidates(fallback, frame_id, color)
            truth = labelled.get(frame_id, {}).get(color)
            if isinstance(truth, list):
                truth = [p[k] for p in truth for k in ('x', 'y')]
            yield dict(key=(group_station, duration, iso, emitter, color), frame=frame_id,
                       time=frame.get('t', context.get('timestamp')), clip=bundle.name,
                       candidates=candidates, total=total, eligible=eligible, truth=truth,
                       selected=frame_output(model, frame_id, color)['selected'],
                       snapshot=not all(k in per_camera for k in
                                        ('exposureDurationSeconds', 'iso', 'shutterEmitter')),
                       truncated=total is None or total > len(candidates))


def collect_evidence(result, blade, competitors, prefix=''):
    values = {}
    for side, candidates in (('blade', [blade] if blade else []), ('competitor', competitors)):
        values[side] = {}
        for metric in METRICS:
            samples = [float(c[metric]) for c in candidates if
                       (type(c.get(metric)) is bool if metric == 'nearSaturationPresent'
                        else number(c.get(metric)) is not None)]
            if samples:
                value = max(samples)
                result[prefix + side][metric].append(value)
                values[side][metric] = value
    if all('meanValue' in values[side] for side in ('blade', 'competitor')):
        margin = values['blade']['meanValue'] - values['competitor']['meanValue']
        result[prefix + 'margins'].append(margin)
        return margin
    return None


def summarize(rows):
    result = dict(frames=len(rows), selected=sum(r['selected'] is not None for r in rows),
                  clips=len({r['clip'] for r in rows}), labelled=0, present=0, absent=0,
                  missed=0, no_output=0, wrong_output=0, uncertain_output=0,
                  unknown=0, truncated=0, snapshot=0, switches=0, switch_pairs=0,
                  switch_proxy=0, proxy_pairs=0, jitter=[], motion_proxy=[],
                  eligible=[], blade=defaultdict(list), competitor=defaultdict(list),
                  margins=[], complete_margins=[], proxy_margins=[],
                  proxy_blade=defaultdict(list), proxy_competitor=defaultdict(list))
    previous = previous2 = None
    for row in sorted(rows, key=lambda r: (r['clip'], r['frame'])):
        truth, selected = row['truth'], row['selected']
        result['truncated'] += row['truncated']
        result['snapshot'] += row['snapshot']
        if number(row['eligible']) is not None:
            result['eligible'].append(row['eligible'])
        if truth is None:
            result['unknown'] += 1
        else:
            result['labelled'] += 1
            result['absent' if truth == 'absent' else 'present'] += 1
            if truth != 'absent':
                kind = identity(selected, truth) if selected is not None else None
                if selected is None or kind == 'competitor':
                    result['missed'] += 1
                    result['no_output' if selected is None else 'wrong_output'] += 1
                elif kind == 'unknown':
                    result['uncertain_output'] += 1
        classified = [(c, identity(endpoints(c), truth)) for c in row['candidates']]
        blades = [c for c, kind in classified if kind == 'blade']
        competitors = [c for c, kind in classified if kind == 'competitor']
        blade = min(blades, key=lambda c: endpoint_error(endpoints(c), truth)) if blades else None
        margin = collect_evidence(result, blade, competitors)
        if margin is not None and not row['truncated'] and all(
                number(c.get('meanValue')) is not None for c in competitors):
            result['complete_margins'].append(margin)
        if truth is None and selected is not None:
            # 真値がなくても記録を確認できる参考値。剣と断定せず推奨には使わない。
            winner = next((c for c in row['candidates'] if c.get('selected')), None)
            others = [c for c in row['candidates']
                      if identity(endpoints(c), selected) == 'competitor']
            collect_evidence(result, winner, others, 'proxy_')
        # クリップ/条件/欠測/時間の境界をまたいで差分を取らない。
        adjacent = (previous is not None and row['clip'] == previous['clip'] and
                    row['frame'] == previous['frame'] + 1 and
                    number(row['time']) is not None and number(previous['time']) is not None and
                    0 < row['time'] - previous['time'] <= .1)
        if adjacent and selected is not None and previous['selected'] is not None:
            prior = previous['selected']
            old_kind = identity(prior, previous['truth'])
            new_kind = identity(selected, truth)
            if old_kind in ('blade', 'competitor') and new_kind in ('blade', 'competitor'):
                result['switch_pairs'] += 1
                result['switches'] += old_kind != new_kind
            # 過去winnerに近い現候補が残り、実出力が別の遠方候補になった回数。
            # rank/indexをidentityにしない。高速運動でも起き得るためproxyと明記する。
            result['proxy_pairs'] += 1
            near = any(endpoint_error(endpoints(c), prior) <= max(12, .5 * length(prior))
                       for c in row['candidates'] if c.get('eligible'))
            result['switch_proxy'] += near and math.dist(midpoint(prior), midpoint(selected)) > max(
                12, (length(prior) + length(selected)) / 2)
            if old_kind == new_kind == 'blade':
                a = tuple(x-y for x, y in zip(midpoint(prior), midpoint(previous['truth'])))
                b = tuple(x-y for x, y in zip(midpoint(selected), midpoint(truth)))
                result['jitter'].append(math.dist(a, b))
            if previous2 is not None and previous2['selected'] is not None:
                dt = row['time'] - previous2['time']
                u = (previous['time'] - previous2['time']) / dt
                a, b = midpoint(previous2['selected']), midpoint(selected)
                interpolated = tuple(x + u * (y-x) for x, y in zip(a, b))
                result['motion_proxy'].append(math.dist(midpoint(prior), interpolated))
        previous2 = previous if adjacent else None
        previous = row
    return result


def recommend(groups, min_labelled=5):
    # A/Bは混ぜない。赤青の弱い側のp10明度差を最大化し、両色の検出を保つ。
    by_stratum = defaultdict(list)
    conditions = defaultdict(dict)
    for key, group in groups.items():
        conditions[key[:-1]][key[-1]] = group
    for condition, colors in conditions.items():
        if condition[0] not in ('A', 'B') or any(v is None for v in condition[1:]):
            continue
        if all(color in colors and colors[color]['present'] >= min_labelled and
               colors[color]['missed'] == colors[color]['uncertain_output'] == 0 and
               len(colors[color]['complete_margins']) >= min_labelled for color in COLORS):
            score = min(quantile10(colors[c]['complete_margins']) for c in COLORS)
            if score > 0:
                by_stratum[(condition[0], condition[1], condition[3])].append((score, condition))
    return {stratum: max(items, key=lambda item: (item[0], -item[1][2]))
            for stratum, items in by_stratum.items()}


def fmt(value):
    return '—' if value is None else f'{value:.6g}'


def metric_pair(group, metric, prefix=''):
    return '/'.join(f'{fmt(median(group[prefix + side][metric]))}({len(group[prefix + side][metric])})'
                    for side in ('blade', 'competitor'))


def condition_text(key):
    station, duration, iso, emitter = key[:4]
    return (f'{station} | {fmt(duration * 1000 if duration else None)} | {fmt(iso)} | '
            f'{"不明" if emitter is None else "ON" if emitter else "OFF"}')


def render(groups, min_labelled=5):
    lines = ['ISO比較（iPhone A/B、明度=V 0..255、値は中央値(n)、真値行=剣/競合・参考行=winner/遠方候補）',
             '|局|露光ms|実測ISO|emitter|色|根拠|frames/出力/真値あり|平均V|peakV|純度|HVR|発光score|飽和率|差p10(n)|',
             '|---|---:|---:|---|---|---|---:|---:|---:|---:|---:|---:|---:|---:|']
    for key, g in sorted(groups.items(), key=lambda item: str(item[0])):
        sources = ([''] if g['labelled'] else []) + (['proxy_'] if g['unknown'] else [])
        for prefix in sources:
            fields = '|'.join(metric_pair(g, m, prefix) for m in METRICS)
            evidence = '未ラベル参考' if prefix else '真値'
            margins = g[prefix + 'margins']
            lines.append(f'|{condition_text(key)}|{key[-1]}|{evidence}|'
                         f'{g["frames"]}/{g["selected"]}/{g["labelled"]}|{fields}|'
                         f'{fmt(quantile10(margins))}({len(margins)})|')
    lines += ['', '|局|露光ms|実測ISO|emitter|色|eligible中央/最大|未検出(未出力+競合)/端点保留/剣あり真値|真値切替/対|切替proxy/対|jitter px(n)|運動proxy px(n)|真値不明/候補欠落/露光snapshot|',
              '|---|---:|---:|---|---|---:|---:|---:|---:|---:|---:|---:|']
    for key, g in sorted(groups.items(), key=lambda item: str(item[0])):
        lines.append(f'|{condition_text(key)}|{key[-1]}|{fmt(median(g["eligible"]))}/'
                     f'{fmt(max(g["eligible"]) if g["eligible"] else None)}|'
                     f'{g["missed"]}({g["no_output"]}+{g["wrong_output"]})/{g["uncertain_output"]}/{g["present"]}|'
                     f'{g["switches"]}/{g["switch_pairs"]}|{g["switch_proxy"]}/{g["proxy_pairs"]}|'
                     f'{fmt(median(g["jitter"]))}({len(g["jitter"])})|'
                     f'{fmt(median(g["motion_proxy"]))}({len(g["motion_proxy"])})|'
                     f'{g["unknown"]}/{g["truncated"]}/{g["snapshot"]}|')
    lines += ['', '未ラベル参考は実winnerとその遠方候補の記録。winnerを本物の剣とは断定せず、推奨には使わない。',
              '競合は真値の剣線分から離れた保存候補（ineligible含む）。反射・衣服・背景の内訳はラベルがなく不明。',
              '剣は端点誤差≤max(12px,剣長×15%)。近傍の部分候補/出力は保留し未検出に算入しない。競合値は指標ごとのフレーム内最大。',
              '差は同じフレームの剣平均V−最大競合平均V。欠測は0にせず—。候補欠落時の差は暫定。',
              '真値切替は剣↔競合。切替proxyは過去winner相当のeligible候補が残る遠方移動（運動でも発生）。',
              'jitterは真値に対する中点誤差の隣接差。運動proxyは3フレームの中点補間残差で、手ぶれ/運動も含む。',
              '隣接frameIDかつ時間差≤100msのみ。クリップ/条件境界は分離。重複した入力フォルダは1回だけ集計。',
              '露光snapshotはトリガー時の値を使用した枚数。条件ごとに設定が安定してから手動保存する。',
              f'推奨規則: 同じ局・実測露光・emitter状態ごとに赤青各{min_labelled}枚以上の剣あり真値・完全候補の同時比較があり、未検出/端点保留0、',
              '赤青の弱い側の明度差p10が正となる条件から最大を選ぶ。同点なら低ISO。真値のある標本内の暫定推奨。']
    choices = recommend(groups, min_labelled)
    for stratum in sorted({(k[0], k[1], k[3]) for k in groups}, key=str):
        if stratum in choices:
            score, condition = choices[stratum]
            lines.append(f'推奨 {condition_text(condition)}（弱い側の差p10={fmt(score)}）')
        else:
            station, duration, emitter = stratum
            context = condition_text((station, duration, None, emitter))
            lines.append(f'推奨 {context}: 保留（局・真値・完全候補・設定記録・検出維持・正の差を満たす条件なし）')
    if not groups:
        lines.append('対象クリップなし。推奨は保留。')
    return '\n'.join(lines)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('paths', nargs='*', help='[A=|B=]クリップまたはその親フォルダ')
    parser.add_argument('--labels', type=Path, default=PRIVATE / 'field-regression')
    parser.add_argument('--date', type=date.fromisoformat, help='収録日 YYYY-MM-DD（ファイルmtimeではない）')
    parser.add_argument('--min-labelled', type=int, default=5, help='推奨に必要な色ごとの真値/完全比較数（既定5）')
    args = parser.parse_args(argv)
    if args.min_labelled < 1:
        parser.error('--min-labelled は1以上が必要です')
    labels = load_labels(args.labels)
    groups = defaultdict(list)
    seen = {}
    clip_count = 0
    for value in args.paths or [str(PRIVATE / 'diagnostics-inbox/autoclips')]:
        station = value[0] if value.startswith(('A=', 'B=')) else None
        path = Path(value[2:] if station else value).expanduser()
        if not path.exists():
            raise ValueError(f'入力がありません: {path}')
        for bundle in find_clips([path]):
            bundle = bundle.resolve()
            match = re.search(r'autoclip_(\d{8})_', bundle.name)
            if args.date and (not match or match[1] != args.date.strftime('%Y%m%d')):
                continue
            if bundle in seen:
                if seen[bundle] != station:
                    raise ValueError(f'同一クリップの局指定が競合: {bundle}')
                continue
            seen[bundle] = station
            clip_count += 1
            for row in observations(bundle, station, labels):
                groups[row['key']].append(row)
    print(f'対象: {clip_count} clips / {args.date or "全収録日"}')
    print(render({key: summarize(rows) for key, rows in groups.items()}, args.min_labelled))
    return 0


if __name__ == '__main__':
    try:
        sys.exit(main())
    except (OSError, ValueError, KeyError, TypeError) as error:
        print(f'ISO比較エラー: {error}', file=sys.stderr)
        sys.exit(2)
