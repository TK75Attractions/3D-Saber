#!/usr/bin/env python3
"""原寸AutoClip v1の検証・候補読み込み・可逆PNGへの取り出し（標準ライブラリ）。"""
from __future__ import annotations

import argparse
import json
import math
from pathlib import Path

FILES = {'fullres.bgra', 'fullres.jsonl'}
MAX_METADATA_BYTES = 64 * 1024 * 1024
MAX_LINE_BYTES = 256 * 1024
MAX_FRAME_BYTES = 8 * 1024 * 1024


def _integer(value, low, high):
    return type(value) is int and low <= value <= high


def _finite(value):
    return type(value) in (int, float) and math.isfinite(value)


def read_frames(root: Path, summary: dict) -> list[dict]:
    """画素全体は読まず、固定パス・サイズ・全候補・順序を検証する。"""
    info = summary.get('fullResolution')
    if info is not None and not isinstance(info, dict):
        raise ValueError('invalid full resolution description')
    if not info or info.get('state') != 'captured':
        if any((root / name).exists() for name in FILES):
            raise ValueError('unreferenced full resolution files')
        return []
    if (info.get('version') != 1 or info.get('pixelFormat') != 'BGRA8'
            or info.get('pixelsPath') != 'fullres.bgra' or info.get('metadataPath') != 'fullres.jsonl'):
        raise ValueError('unsupported full resolution schema')
    w, h, count = (info.get(k) for k in ('width', 'height', 'frameCount'))
    if not (_integer(w, 1, 4096) and _integer(h, 1, 4096) and _integer(count, 1, 1024)):
        raise ValueError('invalid full resolution dimensions/count')
    frame_bytes = w * h * 4
    if frame_bytes > MAX_FRAME_BYTES or info.get('frameBytes') != frame_bytes or info.get('bytesPerRow') != w * 4:
        raise ValueError('invalid packed BGRA stride')
    for name in FILES:
        if (root / name).is_symlink() or not (root / name).is_file():
            raise ValueError('full resolution files must be regular files')
    if (root / 'fullres.bgra').stat().st_size != frame_bytes * count:
        raise ValueError('BGRA byte count does not match frame count')
    if (root / 'fullres.jsonl').stat().st_size > MAX_METADATA_BYTES:
        raise ValueError('full resolution metadata exceeds limit')
    columns = info.get('candidateColumns')
    if not isinstance(columns, list) or len(columns) != 21 or columns != summary.get('autoClip', {}).get('candidateColumns'):
        raise ValueError('full resolution candidate columns mismatch')
    start, end = info.get('startTimestamp'), info.get('endTimestamp')
    if not (_finite(start) and _finite(end) and 0 < end - start <= 4.001):
        raise ValueError('invalid full resolution time window')
    frames = []
    with (root / 'fullres.jsonl').open('rb') as source:
        while line := source.readline(MAX_LINE_BYTES + 2):
            if len(line) > MAX_LINE_BYTES + 1 or not line.endswith(b'\n') or len(frames) >= count:
                raise ValueError('full resolution metadata line exceeds limit/count')
            row = json.loads(line)
            if (not isinstance(row, dict) or not _integer(row.get('f'), 0, 2**63 - 1)
                    or not _finite(row.get('t')) or not start <= row['t'] <= end):
                raise ValueError('invalid frame id/timestamp')
            if frames and (row['f'] <= frames[-1]['f'] or row['t'] <= frames[-1]['t']):
                raise ValueError('full resolution frame order/identity mismatch')
            for key in ('appliedISO', 'exposureDurationSeconds'):
                if key not in row or (row[key] is not None and not _finite(row[key])):
                    raise ValueError('invalid/missing per-frame exposure')
            for color in ('red', 'blue'):
                entry = row.get(color)
                if not isinstance(entry, dict) or not isinstance(entry.get('c'), list) or entry.get('n') != len(entry['c']):
                    raise ValueError('full resolution candidates are truncated')
                for rank, candidate in enumerate(entry['c']):
                    if (not isinstance(candidate, list) or len(candidate) != len(columns)
                            or candidate[0] != rank or not isinstance(candidate[11], str)
                            or not _integer(candidate[10], 0, 15)):
                        raise ValueError('malformed full resolution candidate')
                    for index in range(1, 10):
                        if not _finite(candidate[index]):
                            raise ValueError('non-finite full resolution candidate')
            frames.append(row)
    if len(frames) != count:
        raise ValueError('full resolution metadata frame count mismatch')
    missing = sum(b['f'] - a['f'] - 1 for a, b in zip(frames, frames[1:]))
    if info.get('missingSequenceFrames') != missing:
        raise ValueError('incorrect missing frame count')
    fps = info.get('captureFPS')
    if not _integer(fps, 1, 240):
        raise ValueError('invalid capture FPS')
    pre = frames[0]['t'] <= start + 1.5 / fps
    post = frames[-1]['t'] >= end - 1.5 / fps
    if (info.get('preComplete') != pre or info.get('postComplete') != post
            or info.get('complete') != (missing == 0 and pre and post)):
        raise ValueError('incorrect full resolution completeness')
    return frames


def load_summary(root: Path) -> dict:
    """既存compact schemaへ全候補を展開し、旧clipも同じ入口で扱う。"""
    summary = json.loads((root / 'summary.json').read_text(encoding='utf-8'))
    frames = read_frames(root, summary)
    if frames:
        clip = summary['autoClip']
        sources = list(clip.get('sources', []))
        for row in frames:
            for color in ('red', 'blue'):
                for candidate in row[color]['c']:
                    name = candidate[11]
                    if name not in sources:
                        sources.append(name)
                    candidate[11] = sources.index(name)
        clip['sources'] = sources
        clip['frames'] = frames
        clip['thinning'] = 0
        clip['window'] = {**clip.get('window', {}), 'recordedFrames': len(frames),
                          'startTimestamp': summary['fullResolution']['startTimestamp'],
                          'endTimestamp': summary['fullResolution']['endTimestamp']}
    return summary


def read_rgb(root: Path, info: dict, index: int) -> bytearray:
    if not 0 <= index < info['frameCount']:
        raise ValueError('frame index outside recording')
    size = info['frameBytes']
    with (root / 'fullres.bgra').open('rb') as source:
        source.seek(index * size)
        raw = source.read(size)
    if len(raw) != size:
        raise ValueError('truncated BGRA frame')
    rgb = bytearray(size // 4 * 3)
    rgb[0::3], rgb[1::3], rgb[2::3] = raw[2::4], raw[1::4], raw[0::4]
    return rgb


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('bundle', type=Path)
    parser.add_argument('--output', type=Path, help='原寸PNG全枚とmetadataを出力する新規ディレクトリ')
    args = parser.parse_args()
    summary = json.loads((args.bundle / 'summary.json').read_text())
    frames = read_frames(args.bundle, summary)
    if not frames:
        parser.error('原寸記録なし（OFF・上限・中断）')
    info = summary['fullResolution']
    print(json.dumps(info, ensure_ascii=False, indent=2))
    if args.output:
        from phone_saber_clip_report import write_png
        args.output.mkdir(parents=True, exist_ok=False)
        for index, frame in enumerate(frames):
            write_png(args.output / f"frame_{frame['f']}.png", info['width'], info['height'], read_rgb(args.bundle, info, index))
        (args.output / 'frames.jsonl').write_bytes((args.bundle / 'fullres.jsonl').read_bytes())


if __name__ == '__main__':
    main()
