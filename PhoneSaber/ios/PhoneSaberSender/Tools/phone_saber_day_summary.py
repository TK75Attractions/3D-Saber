#!/usr/bin/env python3
"""受信済みiPhone AutoClipを指定日の時刻範囲で集計する（標準ライブラリのみ）。

  python3 PhoneSaber/ios/PhoneSaberSender/Tools/phone_saber_day_summary.py \\
      --since 14:48 --until 15:19 --images /tmp/day-images > /tmp/day.md

--date YYYY-MM-DD の既定は実行端末の今日。時刻はsessionIDの端末ローカル時刻。
--until は指定した分を含む。省略時はその日の終わりまで。録画は読み取り専用。
"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from datetime import date, datetime, time, timedelta
import json
import math
from pathlib import Path
import re
import statistics
import sys
from urllib.parse import quote
import zlib

import phone_saber_clip_report as report
import phone_saber_stationary_replay as replay

COLORS = ('red', 'blue')
GRID = 40
STATIONARY_SECONDS = 1.5
STATIONARY_RADIUS = 20


def clip_time(name: str) -> datetime:
    match = re.search(r'autoclip_(\d{8})_(\d{6})_(\d{3})', name)
    if not match:
        raise ValueError(f'クリップ日時不明: {name}')
    return datetime.strptime(''.join(match.groups()), '%Y%m%d%H%M%S%f')


def minute(value: str) -> time:
    if not re.fullmatch(r'\d{2}:\d{2}', value):
        raise argparse.ArgumentTypeError('時刻は HH:MM')
    try:
        return time.fromisoformat(value)
    except ValueError as error:
        raise argparse.ArgumentTypeError('時刻は 00:00〜23:59') from error


def cell(mid) -> tuple[int, int]:
    return tuple(math.floor(v / GRID) * GRID for v in mid)


def midpoint(endpoints):
    return [(endpoints[0] + endpoints[2]) / 2, (endpoints[1] + endpoints[3]) / 2]


def number(value) -> str:
    return f'{value:.3g}' if isinstance(value, (int, float)) else 'unknown'


def median(values):
    values = [v for v in values if isinstance(v, (int, float)) and math.isfinite(v)]
    return statistics.median(values) if values else None


def evidence(candidate: dict | None) -> str:
    if not candidate:
        return 'source/peak/purity/形=unknown（保存候補に実選択なし）'
    return (f"source={candidate.get('source', 'unknown')}, peak={number(candidate.get('peakValue'))}, "
            f"purity={number(candidate.get('colorPurity'))}, "
            f"形: length={number(candidate.get('length'))}px, "
            f"box={'×'.join(number(v) for v in candidate['boxSize'])}px, "
            f"aspect={number(candidate.get('aspect'))}, area={number(candidate.get('componentArea'))}")


def color_stats():
    return dict(frames=0, selected=0, jumps=0, dropouts=0, missing=0,
                discontinuities=0, truncated=0, ignored=0, unknown_exclusion=0,
                zones=Counter(), component_states=Counter(), reasons=Counter(), omitted=0,
                hotspots=defaultdict(list), stationary=[])


def stationary_runs(samples: list[dict]) -> list[dict]:
    """eligible候補の中点を始点半径20px内で一対一対応。順位は同一性に使わない。"""
    active, finished = [], []

    def finish(track):
        if track['end'] - track['start'] + 1e-9 >= STATIONARY_SECONDS:
            finished.append(track)

    for sample in samples:
        if sample['reset']:
            for track in active:
                finish(track)
            active = []
        # 同じ40pxセルの重複proposalは一つ。選択候補の証拠を優先する。
        candidates = {}
        for candidate in sorted(sample['candidates'], key=lambda c: (not c['selected'], -(c.get('score') or 0))):
            if candidate['eligible']:
                candidates.setdefault(cell(candidate['mid']), candidate)
        unused = list(candidates.values())
        continuing = []
        for track in active:
            matching = [c for c in unused if math.dist(track['anchor'], c['mid']) <= STATIONARY_RADIUS]
            if not matching:
                finish(track)
                continue
            chosen = min(matching, key=lambda c: math.dist(track['anchor'], c['mid']))
            unused.remove(chosen)
            track.update(end=sample['t'], last_frame=sample['f'])
            track['selected_frames'] += int(chosen['selected'])
            track['ignored_frames'] += int(str(chosen.get('exclusionReason')).startswith('ignore:'))
            continuing.append(track)
        for candidate in unused:
            continuing.append(dict(anchor=candidate['mid'], start=sample['t'], end=sample['t'],
                                   first_frame=sample['f'], last_frame=sample['f'], candidate=candidate,
                                   selected_frames=int(candidate['selected']),
                                   ignored_frames=int(str(candidate.get('exclusionReason')).startswith('ignore:'))))
        active = continuing
    for track in active:
        finish(track)
    return finished


def analyse(bundle: Path) -> dict:
    model = report.load_clip(bundle)
    summary, clip = model['summary'], model['clip']
    # replayの欠落・timestamp断絶検出を静止候補の連続性にも再利用する。Swift再生は不要。
    replay_frames = {f['f']: f for f in replay.load_clip(bundle)['frames']}
    contexts = {}
    for image in summary.get('images', []):
        context = json.loads((bundle / image['frameContextPath']).read_text(encoding='utf-8'))
        contexts.update({f['frameID']: f for f in context.get('frames', [])})
    ids = sorted(set(model['frames']) | set(contexts), key=lambda f: (
        model['frames'].get(f, {}).get('t', contexts.get(f, {}).get('timestamp')), f))
    result = dict(bundle=bundle, model=model, time=clip_time(summary.get('sessionID') or bundle.name),
                  colors={}, background=None, iso=None)
    records = [model['frames'].get(f, contexts.get(f)) for f in ids]
    result['background'] = median([f.get('backgroundMedian') for f in records])
    result['iso'] = median([f.get('appliedISO') for f in records])
    if result['iso'] is None:
        result['iso'] = (clip.get('camera') or {}).get('iso')
    size = clip['frameSize']
    threshold_floor = .15 * math.hypot(size['width'], size['height'])
    for color in COLORS:
        stats = color_stats()
        previous, previous_selected, ema = None, None, None
        samples = []
        for frame_id in ids:
            frame = model['frames'].get(frame_id) or contexts[frame_id]
            timestamp = frame.get('t', frame.get('timestamp'))
            entry = frame.get(color) or {}
            selected = report.frame_output(model, frame_id, color)['selected']
            candidates = report.frame_candidates(model, frame_id, color)
            winner = next((c for c in candidates if c['selected']), None)
            stats['frames'] += 1
            stats['selected'] += bool(selected)
            stats['missing'] += not bool(selected)
            connected = previous is not None and frame_id > previous[0] and 0 < timestamp - previous[1] <= 1
            jump_connected = connected and frame_id - previous[0] <= 2
            if previous is not None and not jump_connected:
                stats['discontinuities'] += 1
                ema = None
            if connected and previous_selected and not selected:
                stats['dropouts'] += 1
            jumped = False
            if selected:
                if jump_connected and previous_selected:
                    jumped = math.dist(midpoint(previous_selected), midpoint(selected)) > max(ema or 0, threshold_floor)
                    if jumped:
                        stats['jumps'] += 1
                        stats['hotspots'][('jump', *cell(midpoint(selected)))].append((frame_id, winner))
                length = math.dist(selected[:2], selected[2:])
                if not jumped:
                    ema = length if ema is None else ema + .2 * (length - ema)
                if entry.get('k', entry.get('competitorCount', 0)):
                    stats['hotspots'][('competition', *cell(midpoint(selected)))].append((frame_id, winner))
            stats['truncated'] += entry.get('n', entry.get('candidateCount', len(candidates))) > len(candidates)
            for candidate in candidates:
                if not candidate['eligible']:
                    continue
                reason = candidate['exclusionReason']
                if reason == 'unknown':
                    stats['unknown_exclusion'] += 1
                elif isinstance(reason, str) and reason.startswith('ignore:'):
                    stats['ignored'] += 1
                    stats['zones'].update({int(z) for z in reason[7:].split(',') if z.isdigit()})
                    stats['hotspots'][('ignore', *cell(candidate['mid']))].append((frame_id, candidate))
            components = report.frame_components(model, frame_id, color)
            stats['component_states'][components.get('state', 'unknown')] += 1
            stats['omitted'] += components.get('omitted', 0)
            for row in components.get('rows', []):
                item = dict(zip(components.get('columns', []), row))
                if item.get('reason') != 'passed':
                    stats['reasons'][(item.get('path', 'unknown'), item.get('stage', 'unknown'),
                                      item.get('reason', 'unknown'))] += 1
            complete = bool(replay_frames.get(frame_id, {}).get('observed', {}).get(color))
            samples.append(dict(f=frame_id, t=timestamp, candidates=candidates if complete else [],
                                reset=replay_frames.get(frame_id, {}).get('reset', True)))
            previous, previous_selected = (frame_id, timestamp), selected
        stats['stationary'] = stationary_runs(samples)
        result['colors'][color] = stats
    return result


def representative_image(model: dict, output: Path) -> int:
    """トリガーに最も近いcontext（なければevent）を元の解像度で描画する。"""
    images = model['summary'].get('images', [])
    contexts = [i for i in images if i.get('role') == 'context']
    if not images:
        raise ValueError('保存画像なし')
    image = min(contexts or images, key=lambda i: (abs(i.get('offsetSeconds') or 0), i['frameID']))
    width, height, pixels = report.read_png(model['bundle'] / image['path'])
    size = model['clip']['frameSize']
    sx, sy = width / size['width'], height / size['height']
    for color in COLORS:
        for c in report.frame_candidates(model, image['frameID'], color):
            style = report.OTHER_ELIGIBLE if c['eligible'] else report.INELIGIBLE
            report._rect(pixels, width, height,
                         (c['minX'] * sx, c['minY'] * sy, c['maxX'] * sx, c['maxY'] * sy), style, 1)
        selected = report.frame_output(model, image['frameID'], color)['selected']
        if selected:
            a, b = (selected[0] * sx, selected[1] * sy), (selected[2] * sx, selected[3] * sy)
            ink = (255, 70, 70) if color == 'red' else (70, 140, 255)
            steps = max(1, math.ceil(math.dist(a, b)))
            for i in range(steps + 1):
                x, y = [a[k] + (b[k] - a[k]) * i / steps for k in (0, 1)]
                report._rect(pixels, width, height, (x - 1, y - 1, x + 1, y + 1), ink, 1)
    report.write_png(output, width, height, pixels)
    return image['frameID']


def build_id(phone: dict) -> str:
    commit = phone.get('gitCommit') or 'unknown'
    dirty = phone.get('gitDirty', 'unknown')
    return (f"{commit} / dirty={dirty} / {phone.get('buildTime') or 'unknown'} "
            f"(appBuild={phone.get('appBuild') or 'unknown'}, station={phone.get('station') or 'unknown'})")


def md(value) -> str:
    return str(value).replace('|', '\\|').replace('\n', ' ')


def render(results: list[dict], start: datetime, end: datetime, images: Path | None = None,
           errors: list[str] | None = None) -> str:
    if errors is None:
        errors = []
    lines = [f'# PhoneSaber 日次要約 {start:%Y-%m-%d}', '',
             f'対象: {start:%H:%M}〜{end - timedelta(minutes=1):%H:%M}（終了分を含む）、{len(results)} clips。', '',
             '記録窓の集計（全実験時間の検出率ではない）。重なるクリップは別窓として数える。',
             '選択は実出力sel。ジャンプは直前の刃長EMA(α=0.2)と対角×0.15の大きい方を超えた中点移動。',
             'ジャンプ時はEMAを更新しない。frame差≤2・時刻差≤1秒のみ比較。窓の開始前のEMAは不明。',
             '途切れは選択あり→なしの回数。窓頭の未選択は含めず、未選択frame数も併記。',
             '競合時の選択位置は誤選択の確認候補であり正解ラベルではない。無視・棄却数は保存分の下限。',
             '静止はeligible候補の始点半径20px内・1.5秒以上。同一40pxセルのproposalを統合し、',
             'eligible候補欠落・frame非連続・時刻差>0.25秒で区切る。静止だけでは背景と断定しない。', '',
             '## クリップ一覧', '',
             '| 時刻 / clip | 種類 | ISO中央値 | 背景中央値 | ビルドID | 無視範囲 赤/青 |',
             '|---|---|---:|---:|---|---|']
    for result in results:
        clip = result['model']['clip']
        zones = clip.get('ignoreZones') or {}
        lines.append(f"| {result['time']:%H:%M:%S} / {md(result['bundle'].name)} | "
                     f"{md(clip.get('trigger', {}).get('kind', 'unknown'))} | {number(result['iso'])} | "
                     f"{number(result['background'])} | {md(build_id(clip.get('phone') or {}))} | "
                     f"{len(zones.get('red') or [])}/{len(zones.get('blue') or [])} |")
    lines += ['', '## 色別集計', '', '| 色 | 選択 / frame（率） | ジャンプ | 途切れ | 未選択frame | 比較断絶 | 候補省略frame |',
              '|---|---:|---:|---:|---:|---:|---:|']
    for color in COLORS:
        stats = [r['colors'][color] for r in results]
        total = Counter({key: sum(s[key] for s in stats) for key in (
            'frames', 'selected', 'jumps', 'dropouts', 'missing', 'discontinuities', 'truncated')})
        rate = f"{100 * total['selected'] / total['frames']:.1f}%" if total['frames'] else 'unknown'
        lines.append(f"| {color} | {total['selected']}/{total['frames']}（{rate}） | {total['jumps']} | "
                     f"{total['dropouts']} | {total['missing']} | {total['discontinuities']} | {total['truncated']} |")
    for color in COLORS:
        stats = [r['colors'][color] for r in results]
        ignored = sum(s['ignored'] for s in stats)
        unknown = sum(s['unknown_exclusion'] for s in stats)
        zones, states, reasons = Counter(), Counter(), Counter()
        hotspots = defaultdict(list)
        for result, s in zip(results, stats):
            zones.update({(result['bundle'].name, z): n for z, n in s['zones'].items()})
            states.update(s['component_states'])
            reasons.update(s['reasons'])
            for key, entries in s['hotspots'].items():
                hotspots[key] += [(result['bundle'].name, f, c) for f, c in entries]
        lines += ['', f'### {color}: 無視・componentDiagnostics', '',
                  f'無視範囲で落ちたeligible候補={ignored}、理由unknownのeligible候補={unknown}。',
                  '刃を無視したかは下記zoneと原画像で確認（矩形からの推測はしない）。']
        for (clip_name, zone), count in zones.most_common():
            lines.append(f'- {clip_name}: zone {zone} = {count} 候補frame')
        lines.append(f'\ncomponent状態(frame): {dict(states)}、省略行={sum(s["omitted"] for s in stats)}。詳細行のあるframeのみ理由を集計。')
        for (path, stage, reason), count in reasons.most_common(10):
            lines.append(f'- {path}/{stage}/{reason}: {count}')
        if not reasons:
            lines.append('- 保存棄却行なし（未収集・unknownのframeは状態欄参照）')
        lines += ['', f'### {color}: 上位位置（40px格子、原寸、左上原点）', '']
        for kind, label in (('jump', 'ジャンプ先'), ('competition', '競合時の選択（誤選択の確認候補）'),
                            ('ignore', '無視されたeligible候補（刃の巻き込み確認）')):
            bins = sorted(((key, entries) for key, entries in hotspots.items() if key[0] == kind),
                          key=lambda item: (-len(item[1]), item[0]))[:10]
            lines.append(f'- {label}: {sum(len(v) for k, v in hotspots.items() if k[0] == kind)} 件')
            for (_, x, y), entries in bins:
                clip_name, frame_id, candidate = entries[0]
                lines.append(f'  - [{x},{x + GRID})×[{y},{y + GRID}): {len(entries)}件; '
                             f'例 {clip_name} f={frame_id}; {evidence(candidate)}')
        lines += ['', f'### {color}: 静止候補一覧', '']
        stationary_count = 0
        for result in results:
            for track in result['colors'][color]['stationary']:
                stationary_count += 1
                lines.append(f"- {result['bundle'].name} f={track['first_frame']}〜{track['last_frame']}: "
                             f"{track['end'] - track['start']:.2f}秒、mid={track['anchor']}、"
                             f"選択={track['selected_frames']}frame、無視={track['ignored_frames']}frame; "
                             f"{evidence(track['candidate'])}")
        if not stationary_count:
            lines.append('- 該当なし（欠落・間引きを含む保存範囲内）')
    if images:
        if any(images.resolve().is_relative_to(r['bundle'].resolve()) for r in results):
            raise ValueError('--images は録画bundleの外を指定してください')
        images.mkdir(parents=True, exist_ok=True)
        lines += ['', '## 代表context画像', '', '線: 赤/青=実選択。枠: 水色=eligible、灰色=ineligible。', '']
        for result in results:
            output = images / f"{result['bundle'].name}.png"
            try:
                frame_id = representative_image(result['model'], output)
                lines += [f"{result['bundle'].name} f={frame_id}", '', f'![context]({quote(str(output.resolve()))})', '']
            except (OSError, ValueError, zlib.error) as error:
                errors.append(f'{result["bundle"].name}: 代表画像: {error}')
    if errors:
        lines += ['', '## 読み取りエラー（集計未完了）', ''] + [f'- {md(e)}' for e in errors]
    return '\n'.join(lines) + '\n'


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--inbox', type=Path, default=report.DEFAULT_INBOX / 'autoclips')
    parser.add_argument('--date', type=date.fromisoformat, default=date.today(), help='YYYY-MM-DD（既定: 今日）')
    parser.add_argument('--since', type=minute, default=time(0, 0))
    parser.add_argument('--until', type=minute, default=time(23, 59), help='HH:MM、この分を含む')
    parser.add_argument('--images', type=Path, help='代表context PNGの出力先')
    args = parser.parse_args(argv)
    if args.until < args.since:
        parser.error('--until は --since 以降を指定してください')
    start = datetime.combine(args.date, args.since)
    end = datetime.combine(args.date, args.until) + timedelta(minutes=1)
    results, errors = [], []
    try:
        for bundle in sorted(args.inbox.expanduser().iterdir()):
            if not bundle.is_dir() or not bundle.name.startswith(report.PREFIX + 'autoclip_'):
                continue
            # 到着途中の壊れたsummaryも対象時刻内なら黙って落とさない。
            timestamp = clip_time(bundle.name)
            if not start <= timestamp < end:
                continue
            try:
                results.append(analyse(bundle))
            except (OSError, ValueError, KeyError, TypeError, AssertionError) as error:
                errors.append(f'{bundle.name}: {error}')
        results.sort(key=lambda r: (r['time'], r['bundle'].name))
        print(render(results, start, end, args.images.expanduser() if args.images else None, errors), end='')
    except (OSError, ValueError) as error:
        print(f'エラー: {error}', file=sys.stderr)
        return 2
    return 2 if errors else 0


if __name__ == '__main__':
    raise SystemExit(main())
