#!/usr/bin/env python3
"""Summarise iPhone auto clips (summaryScope "autoClip") received by the triage receiver.

For each clip: the trigger, and for a confusion trigger the competing candidates'
positions, sizes and scores; for a jump the before/after winner. Optionally writes a
contact sheet PNG with the candidate boxes drawn on a few frames. Python standard
library only (a small PNG reader/writer is included). Read-only on the bundle: the
report and contact sheet are written beside it.

  python3 phone_saber_clip_report.py                    # every clip in the inbox autoclips/ folder
  python3 phone_saber_clip_report.py <clip folder> ...  # one or more clips (or folders of clips)
  python3 phone_saber_clip_report.py <clip> --write     # also write <clip>.report.md and <clip>.contact.png
"""

from __future__ import annotations

import argparse
import json
import math
import os
import struct
import sys
import zlib
from pathlib import Path
from typing import Any

PREFIX = "phone_saber_triage_"
DEFAULT_INBOX = Path.home() / "Library/Application Support/PhoneSaber/diagnostics-inbox"
FLAG_ELIGIBLE, FLAG_SELECTABLE, FLAG_SELECTED, FLAG_COMPETITOR = 1, 2, 4, 8
EVIDENCE_COLUMNS = ("peakValue", "emitterScore", "highValueRatio", "meanValue",
                    "nearSaturationPresent", "colorPurity", "componentArea", "aspect")
# Contact sheet colours (RGB).
SELECTED = (0, 230, 0)
COMPETITOR = (255, 0, 255)
OTHER_ELIGIBLE = (0, 220, 255)
INELIGIBLE = (140, 140, 140)
EVENT_BAR = (255, 210, 0)
CONTEXT_BAR = (70, 70, 70)
TRIGGER_BAR = (255, 255, 255)


# ---------------------------------------------------------------- PNG (stdlib)

def read_png(path: Path) -> tuple[int, int, bytearray]:
    """Decode an 8-bit RGB/RGBA/grey PNG (non-interlaced) to packed RGB rows."""
    data = path.read_bytes()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        raise ValueError(f"not a PNG: {path}")
    position, idat = 8, bytearray()
    width = height = depth = color = interlace = None
    while position + 8 <= len(data):
        length, kind = struct.unpack(">I4s", data[position:position + 8])
        chunk = data[position + 8:position + 8 + length]
        position += 12 + length
        if kind == b"IHDR":
            width, height, depth, color, _, _, interlace = struct.unpack(">IIBBBBB", chunk)
        elif kind == b"IDAT":
            idat += chunk
        elif kind == b"IEND":
            break
    channels = {0: 1, 2: 3, 4: 2, 6: 4}.get(color)
    if width is None or depth != 8 or channels is None or interlace:
        raise ValueError(f"unsupported PNG (depth={depth} color={color} interlace={interlace}): {path}")
    raw = zlib.decompress(bytes(idat))
    stride = width * channels
    pixels = bytearray(height * stride)
    previous = bytearray(stride)
    offset = 0
    for row in range(height):
        kind = raw[offset]
        line = bytearray(raw[offset + 1:offset + 1 + stride])
        offset += 1 + stride
        if kind == 1:
            for i in range(channels, stride):
                line[i] = (line[i] + line[i - channels]) & 255
        elif kind == 2:
            for i in range(stride):
                line[i] = (line[i] + previous[i]) & 255
        elif kind == 3:
            for i in range(stride):
                left = line[i - channels] if i >= channels else 0
                line[i] = (line[i] + ((left + previous[i]) >> 1)) & 255
        elif kind == 4:
            for i in range(stride):
                a = line[i - channels] if i >= channels else 0
                b = previous[i]
                c = previous[i - channels] if i >= channels else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                predictor = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                line[i] = (line[i] + predictor) & 255
        elif kind != 0:
            raise ValueError(f"bad PNG filter {kind}: {path}")
        pixels[row * stride:(row + 1) * stride] = line
        previous = line
    if channels == 3:
        return width, height, pixels
    rgb = bytearray(width * height * 3)
    for index in range(width * height):
        source = index * channels
        if channels >= 3:
            rgb[index * 3:index * 3 + 3] = pixels[source:source + 3]
        else:
            rgb[index * 3:index * 3 + 3] = bytes((pixels[source],)) * 3
    return width, height, rgb


def write_png(path: Path, width: int, height: int, rgb: bytes | bytearray) -> None:
    """Encode packed RGB rows (filter 0)."""
    stride = width * 3
    raw = b"".join(b"\x00" + bytes(rgb[row * stride:(row + 1) * stride]) for row in range(height))

    def chunk(kind: bytes, body: bytes) -> bytes:
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)

    path.write_bytes(b"\x89PNG\r\n\x1a\n"
                     + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
                     + chunk(b"IDAT", zlib.compress(raw, 6)) + chunk(b"IEND", b""))


# ---------------------------------------------------------------- clip model

def is_clip(path: Path) -> bool:
    try:
        summary = json.loads((path / "summary.json").read_text(encoding="utf-8"))
    except (OSError, ValueError):
        return False
    return isinstance(summary, dict) and summary.get("summaryScope") == "autoClip"


def find_clips(paths: list[Path]) -> list[Path]:
    clips: list[Path] = []
    for path in paths:
        if (path / "summary.json").is_file():
            if is_clip(path):
                clips.append(path)
            continue
        if path.is_dir():
            clips += sorted(p for p in path.iterdir()
                            if p.is_dir() and p.name.startswith(PREFIX) and is_clip(p))
    return clips


def _candidates(frame: dict, color: str, columns: list[str], sources: list[str]) -> list[dict]:
    entry = frame.get(color) or {}
    rows = entry.get("c") or []
    result = []
    for row in rows:
        item = dict(zip(columns, row))
        if isinstance(item.get("source"), int) and 0 <= item["source"] < len(sources):
            item["source"] = sources[item["source"]]
        flags = int(item.get("flags", 0))
        item.update(eligible=bool(flags & FLAG_ELIGIBLE), selectable=bool(flags & FLAG_SELECTABLE),
                    selected=bool(flags & FLAG_SELECTED), competitor=bool(flags & FLAG_COMPETITOR))
        x1, y1, x2, y2 = (item.get(k, 0) for k in ("x1", "y1", "x2", "y2"))
        item["mid"] = [round((x1 + x2) / 2, 1), round((y1 + y2) / 2, 1)]
        item["length"] = round(math.hypot(x2 - x1, y2 - y1), 1)
        item["boxSize"] = [item.get("maxX", 0) - item.get("minX", 0), item.get("maxY", 0) - item.get("minY", 0)]
        result.append(item)
    if "sel" in entry:
        _reconcile_selection(result, entry["sel"])
    return result


def _reconcile_selection(candidates: list[dict], endpoints: list[int] | None) -> None:
    # v1 の誤ったフラグも実出力で照合。重複proposalを複数winnerとして表示しない。
    matching = [c for c in candidates if endpoints == [c.get(k) for k in ("x1", "y1", "x2", "y2")]]
    winner = next((c for c in matching if c.get("selected")), matching[0] if matching else None)
    for c in candidates:
        c["recordedSelected"] = c.get("selected", False)
        c["selected"] = c is winner


class ClipIntegrityError(ValueError):
    """画像と認識メタデータを安全に結合できないクリップ。"""


def validate_frame_integrity(bundle: Path, summary: dict) -> None:
    # 同じIDの複数レコードを辞書へ入れる前に拒否する。summaryの間引きは許容する。
    times = {}

    def record(frame_id, timestamp, source):
        if type(frame_id) is not int or frame_id < 0:
            raise ClipIntegrityError(f"{bundle.name}: invalid frame ID ({source})")
        if type(timestamp) not in (int, float) or not math.isfinite(timestamp):
            raise ClipIntegrityError(f"{bundle.name}: invalid timestamp ({source}, frame {frame_id})")
        if frame_id in times and abs(times[frame_id] - timestamp) > 1e-6:
            raise ClipIntegrityError(f"{bundle.name}: timestamp mismatch (frame {frame_id}, {source})")
        times[frame_id] = timestamp

    frames = summary.get("autoClip", {}).get("frames", [])
    ids = set()
    for frame in frames:
        frame_id = frame["f"]
        if frame_id in ids:
            raise ClipIntegrityError(f"{bundle.name}: duplicate frame ID {frame_id} in summary")
        ids.add(frame_id)
        record(frame_id, frame.get("t"), "summary")
    image_ids = set()
    for image in summary.get("images", []):
        frame_id = image["frameID"]
        if frame_id in image_ids:
            raise ClipIntegrityError(f"{bundle.name}: duplicate frame ID {frame_id} in images")
        image_ids.add(frame_id)
        record(frame_id, image.get("timestamp"), image["path"])
        context = json.loads((bundle / image["frameContextPath"]).read_text(encoding="utf-8"))
        context_ids = set()
        for frame in context.get("frames", []):
            context_id = frame["frameID"]
            if context_id in context_ids:
                raise ClipIntegrityError(f"{bundle.name}: duplicate frame ID {context_id} in context")
            context_ids.add(context_id)
            record(context_id, frame.get("timestamp"), image["frameContextPath"])
        if len(context_ids) != 1 or frame_id not in context_ids or context.get("selectedFrameID", frame_id) != frame_id:
            raise ClipIntegrityError(f"{bundle.name}: image/context frame ID mismatch ({image['path']})")


def load_clip(bundle: Path) -> dict[str, Any]:
    summary = json.loads((bundle / "summary.json").read_text(encoding="utf-8"))
    if summary.get("summaryScope") != "autoClip":
        raise ValueError(f"not an auto clip: {bundle}")
    validate_frame_integrity(bundle, summary)
    clip = summary.get("autoClip") or {}
    columns = clip.get("candidateColumns") or []
    sources = clip.get("sources") or []
    frames = {}
    for frame in clip.get("frames") or []:
        frames[int(frame["f"])] = frame
    return {"bundle": bundle, "summary": summary, "clip": clip, "columns": columns,
            "sources": sources, "frames": frames}


def frame_candidates(model: dict, frame_id: int, color: str) -> list[dict]:
    frame = model["frames"].get(frame_id)
    if frame is not None:
        return _candidates(frame, color, model["columns"], model["sources"])
    # Fallback: the image's own context JSON (always present for selected images).
    for image in model["summary"].get("images", []):
        if image.get("frameID") != frame_id:
            continue
        try:
            context = json.loads((model["bundle"] / image["frameContextPath"]).read_text(encoding="utf-8"))
        except (OSError, ValueError, KeyError):
            continue
        entry = (context.get("frames") or [{}])[0].get(color) or {}
        result = []
        for c in entry.get("autoClipCandidates") or []:
            x1, y1, x2, y2 = c.get("endpoints", [0, 0, 0, 0])
            box = c.get("bbox", [0, 0, 0, 0])
            result.append({"rank": c.get("index"), "x1": x1, "y1": y1, "x2": x2, "y2": y2,
                           "minX": box[0], "minY": box[1], "maxX": box[2], "maxY": box[3],
                           "score": c.get("finalScore"), "source": c.get("sourceType"),
                           "eligible": c.get("eligible"), "selectable": c.get("selectable"),
                           "selected": c.get("selected"), "competitor": c.get("competitor"),
                           "mid": [(x1 + x2) / 2, (y1 + y2) / 2],
                           "length": round(math.hypot(x2 - x1, y2 - y1), 1),
                           "boxSize": [box[2] - box[0], box[3] - box[1]]})
        if "endpoint" in entry or entry.get("detected") is False:
            _reconcile_selection(result, entry.get("endpoint"))
        for item, candidate in zip(result, entry.get("autoClipCandidates") or []):
            item.update({key: candidate.get(key) for key in EVIDENCE_COLUMNS})
        return result
    return []


def frame_output(model: dict, frame_id: int, color: str) -> dict:
    frame = model["frames"].get(frame_id)
    if frame is not None:
        entry = frame.get(color) or {}
        return {"selected": entry.get("sel"), "trackLockSuppressed": entry.get("sup")}
    for image in model["summary"].get("images", []):
        if image.get("frameID") != frame_id:
            continue
        try:
            context = json.loads((model["bundle"] / image["frameContextPath"]).read_text(encoding="utf-8"))
            entry = (context.get("frames") or [{}])[0].get(color) or {}
            return {"selected": entry.get("endpoint"), "trackLockSuppressed": entry.get("trackLockSuppressed")}
        except (OSError, ValueError, KeyError):
            continue
    return {"selected": None, "trackLockSuppressed": None}


def _winner_side_counts(model: dict, color: str, saber_mid: list[float] | None,
                        competitor_mid: list[float] | None) -> dict[str, int]:
    """How often the winner sat at the trigger's winner vs. competitor location."""
    counts = {"framesWithCompetitor": 0, "winnerNearTriggerWinner": 0, "winnerNearCompetitor": 0, "frames": 0}
    if not saber_mid or not competitor_mid:
        return counts
    for frame in model["frames"].values():
        entry = frame.get(color) or {}
        counts["frames"] += 1
        if entry.get("k", 0) > 0:
            counts["framesWithCompetitor"] += 1
        selected = entry.get("sel")
        if not selected:
            continue
        mid = [(selected[0] + selected[2]) / 2, (selected[1] + selected[3]) / 2]
        to_saber = math.dist(mid, saber_mid)
        to_competitor = math.dist(mid, competitor_mid)
        if to_competitor < to_saber:
            counts["winnerNearCompetitor"] += 1
        else:
            counts["winnerNearTriggerWinner"] += 1
    return counts


def describe(bundle: Path) -> dict[str, Any]:
    model = load_clip(bundle)
    summary, clip = model["summary"], model["clip"]
    trigger = clip.get("trigger") or {}
    color = trigger.get("color", "both")
    frame_id = trigger.get("frameID")
    result: dict[str, Any] = {
        "formatVersion": clip.get("formatVersion", 1),
        "clipID": summary.get("sessionID"), "path": str(bundle),
        "trigger": {k: trigger.get(k) for k in ("kind", "color", "frameID", "timestamp", "reason",
                                                "competitorCount", "jumpDistancePx", "jumpThresholdPx",
                                                "typicalLengthPx", "note")},
        "window": clip.get("window"), "camera": clip.get("camera"),
        "playArea": clip.get("playArea"), "ignoreZones": clip.get("ignoreZones"),
        "images": [{"path": i.get("path"), "role": i.get("role"), "frameID": i.get("frameID"),
                    "offsetSeconds": i.get("offsetSeconds")} for i in summary.get("images", [])],
        "recordedFrames": summary.get("recordedFrameCount"),
        "thinning": clip.get("thinning"),
    }
    colors = ["red", "blue"] if color not in ("red", "blue") else [color]
    result["triggerFrameCandidates"] = {
        c: frame_candidates(model, frame_id, c) if isinstance(frame_id, int) else [] for c in colors}
    result["triggerFrameOutput"] = {c: frame_output(model, frame_id, c) for c in colors}
    result["suppressedFrames"] = {
        c: sum(bool((f.get(c) or {}).get("sup")) for f in model["frames"].values())
        if clip.get("formatVersion", 1) >= 2 else None for c in ("red", "blue")}
    if trigger.get("kind") == "confusion" and color in ("red", "blue"):
        winner, competitor = trigger.get("winner"), trigger.get("competitor")
        mid = (lambda e: [(e[0] + e[2]) / 2, (e[1] + e[3]) / 2] if e and len(e) == 4 else None)
        result["competition"] = _winner_side_counts(model, color, mid(winner), mid(competitor))
    if trigger.get("kind") == "jump":
        result["jump"] = {"previousWinner": trigger.get("previousWinner"), "winner": trigger.get("winner")}
    return result


def _fmt_candidate(c: dict) -> str:
    role = "WINNER" if c.get("selected") else "COMPETITOR" if c.get("competitor") else \
        "eligible" if c.get("eligible") else "ineligible"
    extra = "" if c.get("selectable") or not c.get("eligible") else (
        " (actual tracked output outside acquisition area)" if c.get("selected") else
        " (not selectable: ignore zone / outside play area)")
    evidence = " ".join(f"{key}={c.get(key) if c.get(key) is not None else 'unknown'}" for key in EVIDENCE_COLUMNS)
    return (f"  - {role:<10} rank {c.get('rank')} {c.get('source')} score {c.get('score')} "
            f"mid ({c['mid'][0]:.0f},{c['mid'][1]:.0f}) length {c['length']:.0f}px "
            f"box {c['boxSize'][0]}x{c['boxSize'][1]} ends [{c.get('x1')},{c.get('y1')}]-[{c.get('x2')},{c.get('y2')}]{extra}\n    evidence: {evidence}")


def render_text(info: dict[str, Any]) -> str:
    trigger = info["trigger"]
    lines = [f"# Auto clip {info['clipID']}", "",
             f"- trigger: **{trigger.get('kind')}** colour={trigger.get('color')} frame={trigger.get('frameID')} "
             f"t={trigger.get('timestamp')}",
             f"- reason: {trigger.get('reason')}"]
    legacy = info.get("formatVersion", 1) < 2
    if legacy:
        lines.append("- format v1: selected candidate flags may differ from actual output (track lock / play area); suppression and emitter evidence unknown. Actual output below uses sel.")
    lines.append(f"- track lock suppressed frames (stored metadata): {info.get('suppressedFrames')}")
    camera = info.get("camera") or {}
    window = info.get("window") or {}
    lines.append(f"- window: {window.get('preSeconds')} s before / {window.get('postSeconds')} s after, "
                 f"{info.get('recordedFrames')} frames; fps {camera.get('captureFPS')} (measured {camera.get('measuredFPS')}), "
                 f"thermal {camera.get('thermalAtTrigger')}→{camera.get('thermalAtSave')}, exposure bias {camera.get('exposureBias')}")
    lines.append(f"- play area: {info.get('playArea')}; ignore zones: {info.get('ignoreZones')}")
    if trigger.get("kind") == "jump":
        jump = info.get("jump") or {}
        lines.append(f"- jump: {trigger.get('jumpDistancePx')} px (threshold {trigger.get('jumpThresholdPx')} px, "
                     f"typical length {trigger.get('typicalLengthPx')} px) from {jump.get('previousWinner')} to {jump.get('winner')}")
    if "competition" in info:
        c = info["competition"]
        lines.append(f"- in the window: {c['framesWithCompetitor']}/{c['frames']} frames had a separated competitor; "
                     f"winner near the trigger winner {c['winnerNearTriggerWinner']}, near the competitor {c['winnerNearCompetitor']}")
    lines += ["", "## Candidates at the trigger frame (full-resolution pixels)"]
    for color, candidates in info["triggerFrameCandidates"].items():
        output = info.get("triggerFrameOutput", {}).get(color, {})
        suppression = output.get("trackLockSuppressed")
        lines.append(f"- {color}: actual winner endpoints={output.get('selected')}; "
                     f"trackLockSuppressed={suppression if suppression is not None else 'unknown'}"
                     + ("" if candidates else " (none stored)"))
        lines += [_fmt_candidate(c) for c in candidates]
    lines += ["", "## Images (time order)"]
    for index, image in enumerate(info["images"], 1):
        lines.append(f"{index}. {image['role']} frame {image['frameID']} ({image['offsetSeconds']:+.2f} s) {image['path']}")
    lines += ["", "Heuristic trigger: it does not prove a recognition fault. Look at the event images "
              "for where the real saber is before changing anything.", ""]
    return "\n".join(lines)


# ---------------------------------------------------------------- contact sheet

def _scaled_tile(path: Path, scale: float, tile_w: int, tile_h: int) -> bytearray:
    width, height, rgb = read_png(path)
    step = 2 if scale >= 1 else 1   # full-resolution images → half resolution (point sample)
    tile = bytearray(tile_w * tile_h * 3)
    for y in range(min(tile_h, height // step)):
        source_row = (y * step) * width
        for x in range(min(tile_w, width // step)):
            s = (source_row + x * step) * 3
            t = (y * tile_w + x) * 3
            tile[t:t + 3] = rgb[s:s + 3]
    return tile


def _rect(tile: bytearray, tile_w: int, tile_h: int, box: tuple[float, float, float, float],
          color: tuple[int, int, int], thickness: int) -> None:
    x0, y0, x1, y1 = (int(round(v)) for v in box)
    x0, x1 = max(0, min(x0, x1)), min(tile_w - 1, max(x0, x1))
    y0, y1 = max(0, min(y0, y1)), min(tile_h - 1, max(y0, y1))
    if x0 > x1 or y0 > y1:
        return
    pixel = bytes(color)
    for t in range(thickness):
        for x in range(x0, x1 + 1):
            for y in (y0 + t, y1 - t):
                if 0 <= y < tile_h:
                    tile[(y * tile_w + x) * 3:(y * tile_w + x) * 3 + 3] = pixel
        for y in range(y0, y1 + 1):
            for x in (x0 + t, x1 - t):
                if 0 <= x < tile_w:
                    tile[(y * tile_w + x) * 3:(y * tile_w + x) * 3 + 3] = pixel


def choose_tiles(model: dict, max_tiles: int = 6) -> list[dict]:
    images = model["summary"].get("images", [])
    events = [i for i in images if i.get("role") == "event"]
    context = sorted((i for i in images if i.get("role") != "event"),
                     key=lambda i: abs(i.get("offsetSeconds") or 0))
    chosen = (events + context)[:max_tiles]
    return sorted(chosen, key=lambda i: (i.get("offsetSeconds") or 0, i.get("role") != "event"))


def contact_sheet(bundle: Path, output: Path, max_tiles: int = 6, columns: int = 3) -> Path:
    model = load_clip(bundle)
    clip = model["clip"]
    size = clip.get("frameSize") or {}
    full_w, full_h = int(size.get("width", 480)), int(size.get("height", 640))
    tile_w, tile_h = (full_w + 1) // 2, (full_h + 1) // 2
    bar, gap = 6, 4
    tiles = choose_tiles(model, max_tiles)
    if not tiles:
        raise ValueError(f"clip has no images: {bundle}")
    trigger = clip.get("trigger") or {}
    color = trigger.get("color")
    colors = [color] if color in ("red", "blue") else ["red", "blue"]
    rows = math.ceil(len(tiles) / columns)
    sheet_w = columns * tile_w + (columns + 1) * gap
    sheet_h = rows * (tile_h + bar) + (rows + 1) * gap
    sheet = bytearray(bytes((24, 24, 24)) * (sheet_w * sheet_h))
    for number, image in enumerate(tiles):
        scale = float(image.get("scale") or (1.0 if image.get("role") == "event" else 0.5))
        tile = _scaled_tile(bundle / image["path"], scale, tile_w, tile_h)
        frame_id = image.get("frameID")
        # Boxes in full-resolution pixels → tile pixels (tiles are half resolution).
        for c in colors:
            for cand in sorted(frame_candidates(model, frame_id, c),
                               key=lambda k: (k.get("selected", False), k.get("competitor", False))):
                style = (SELECTED, 3) if cand.get("selected") else (COMPETITOR, 3) if cand.get("competitor") \
                    else (OTHER_ELIGIBLE, 2) if cand.get("eligible") else (INELIGIBLE, 1)
                _rect(tile, tile_w, tile_h, (cand["minX"] / 2, cand["minY"] / 2, cand["maxX"] / 2, cand["maxY"] / 2),
                      style[0], style[1])
        row, column = divmod(number, columns)
        left = gap + column * (tile_w + gap)
        top = gap + row * (tile_h + bar + gap)
        bar_color = TRIGGER_BAR if frame_id == trigger.get("frameID") else \
            EVENT_BAR if image.get("role") == "event" else CONTEXT_BAR
        for y in range(bar):
            start = ((top + y) * sheet_w + left) * 3
            sheet[start:start + tile_w * 3] = bytes(bar_color) * tile_w
        for y in range(tile_h):
            start = ((top + bar + y) * sheet_w + left) * 3
            sheet[start:start + tile_w * 3] = tile[y * tile_w * 3:(y + 1) * tile_w * 3]
    write_png(output, sheet_w, sheet_h, sheet)
    return output


def contact_sheet_legend(model: dict, max_tiles: int = 6) -> list[str]:
    tiles = choose_tiles(model, max_tiles)
    return [f"{n}. {t.get('role')} frame {t.get('frameID')} ({(t.get('offsetSeconds') or 0):+.2f} s)"
            for n, t in enumerate(tiles, 1)]


def write_clip_report(bundle: Path) -> Path:
    """Report + contact sheet beside the bundle (never inside it). Returns the report path."""
    info = describe(bundle)
    model = load_clip(bundle)
    sheet = bundle.parent / f"{bundle.name}.contact.png"
    text = render_text(info)
    try:
        contact_sheet(bundle, sheet)
        text += "\n## Contact sheet\n\n" + f"`{sheet.name}`: tiles left→right, top→bottom; bar white = trigger frame, " \
            "yellow = full-resolution event frame, grey = half-resolution context. Boxes: green = winner, " \
            "magenta = competitor, cyan = other eligible, grey = ineligible.\n\n" + "\n".join(contact_sheet_legend(model)) + "\n"
    except (OSError, ValueError, zlib.error) as exc:
        text += f"\n(contact sheet failed: {exc})\n"
    report = bundle.parent / f"{bundle.name}.report.md"
    temporary = report.with_suffix(".md.tmp")
    temporary.write_text(text, encoding="utf-8")
    os.replace(temporary, report)
    return report


def render_index(infos: list[dict[str, Any]]) -> str:
    lines = ["| clip | trigger | colour | frame | competitors | jump px |", "|---|---|---|---|---|---|"]
    for info in infos:
        t = info["trigger"]
        lines.append(f"| {info['clipID']} | {t.get('kind')} | {t.get('color')} | {t.get('frameID')} | "
                     f"{t.get('competitorCount')} | {t.get('jumpDistancePx') or ''} |")
    return "\n".join(lines)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("paths", nargs="*", type=Path,
                        help="clip folders or folders of clips (default: <inbox>/autoclips)")
    parser.add_argument("--write", action="store_true", help="write <clip>.report.md and <clip>.contact.png beside each clip")
    parser.add_argument("--json", action="store_true", help="print JSON instead of text")
    args = parser.parse_args(argv)
    inbox = Path(os.environ.get("PHONESABER_DIAGNOSTICS_INBOX", DEFAULT_INBOX))
    clips = find_clips(args.paths or [inbox / "autoclips"])
    if not clips:
        print("no auto clips found", file=sys.stderr)
        return 1
    infos = []
    rejected = 0
    for clip in clips:
        try:
            info = describe(clip)
        except ClipIntegrityError as exc:
            print(f"WARNING: excluded clip: {exc}", file=sys.stderr)
            rejected += 1
            continue
        infos.append(info)
        if args.write:
            info["report"] = str(write_clip_report(clip))
    if args.json:
        print(json.dumps(infos, ensure_ascii=False, indent=1, default=str))
    else:
        if len(infos) > 1:
            print(render_index(infos) + "\n")
        for info in infos:
            print(render_text(info))
            if info.get("report"):
                print(f"report: {info['report']}\n")
    return 2 if rejected else 0


if __name__ == "__main__":
    sys.exit(main())
