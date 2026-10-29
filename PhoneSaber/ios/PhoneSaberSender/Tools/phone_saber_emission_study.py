#!/usr/bin/env python3
"""AutoClip の原画像と手動ラベルから発光/反射の分布を測る（認識は変更しない）。

python3 phone_saber_emission_study.py CLIP... --labels /tmp/labels.json --output /tmp/study
ラベルは原画像を目視して作る。sel/eligible/annotated を正解にはしない。
labels.json: {"schemaVersion": 1, "reviewedOriginal": true, "candidates": [{
  "clip": "phone_saber_triage_autoclip_...", "frame": 123, "color": "red",
  "source": "color-mask", "endpoints": [10,20,30,40],
  "imageSHA256": "...", "label": "blade"}]}
未ラベル/曖昧な候補は unknown のまま残す。統計の単位は候補で、独立な撮影回数ではない。
画素統計は候補軸周辺の測定用マスク。production の支持画素/HSVマスクとは異なる。
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
from pathlib import Path

import numpy as np
from PIL import Image


LABELS = {
    "blade", "cone", "cone_lamp_bridge", "blade_background_bridge",
    "fabric_reflection", "fabric_red", "skin_reflection", "surface_reflection",
    "projection", "indicator", "background_bridge", "background_surface", "unknown",
}
SAVED_METRICS = (
    "peakValue", "emitterScore", "highValueRatio", "meanValue", "nearSaturationPresent",
    "colorPurity", "componentArea", "aspect",
)
PIXEL_METRICS = (
    "pixelCount", "pixelPeak", "pixelMean", "pixelSat", "pixelCV", "deepRedRatio",
    "RG", "RB", "BG", "BR", "centerValue", "edgeValue", "centerEdgeValue",
    "centerEdgeSat", "axial220", "axial180", "axialRelative", "fill", "axisPeakCV",
    "axisSlope", "axisRoughness", "surfaceGradient",
)


def private_output(path: Path) -> Path:
    path = path.expanduser().resolve()
    if any((parent / ".git").exists() for parent in (path, *path.parents)):
        raise ValueError("原画像由来の解析結果は Git 外に保存する")
    return path


def candidate_key(clip: str, frame: int, color: str, source: str, endpoints: list) -> tuple:
    return clip, frame, color, source, *endpoints


def load_labels(path: Path | None) -> dict:
    if path is None:
        return {}
    document = json.loads(path.read_text())
    if document.get("schemaVersion") != 1 or document.get("reviewedOriginal") is not True:
        raise ValueError("schemaVersion=1 / reviewedOriginal=true の原画像目視ラベルが必要")
    labels = {}
    for row in document["candidates"]:
        if (row["label"] not in LABELS or row["color"] not in ("red", "blue")
                or len(row["endpoints"]) != 4 or len(row["imageSHA256"]) != 64):
            raise ValueError("候補ラベルの形式が不正")
        key = candidate_key(row["clip"], row["frame"], row["color"], row["source"], row["endpoints"])
        if key in labels:
            raise ValueError(f"候補ラベルの重複: {key}")
        labels[key] = row
    return labels


def pixel_features(rgb: np.ndarray, endpoints: list, box: list, area: float, color: str) -> dict:
    """原画素の測定。値は8bit符号値で、放射輝度や材質の推定値ではない。"""
    h, w = rgb.shape[:2]
    a, b = np.asarray(endpoints[:2]), np.asarray(endpoints[2:])
    length = float(np.linalg.norm(b - a))
    width = area / max(length, 1)
    result = {"length": length, "width": width, **dict.fromkeys(PIXEL_METRICS)}
    if length <= 0:
        return result
    axis = (b - a) / length
    normal = np.array([-axis[1], axis[0]])
    x0, y0, x1, y1 = map(int, box)
    y, x = np.mgrid[max(y0 - 3, 0):min(y1 + 4, h), max(x0 - 3, 0):min(x1 + 4, w)]
    if not x.size:
        return result
    pixels = rgb[y, x].astype(float)
    delta = np.stack([x - a[0], y - a[1]], axis=-1)
    along, across = delta @ axis, delta @ normal
    value = pixels.max(-1)
    saturation = (value - pixels.min(-1)) / np.maximum(value, 1)
    r, g, b = pixels[:, :, 0], pixels[:, :, 1], pixels[:, :, 2]
    chromatic = ((r > g * 1.2) & (r > b * 1.2) if color == "red"
                 else (b > r * 1.2) & (b > g * 1.05)) & (saturation >= .25) & (value >= 32)
    half_width = float(np.clip(width / 2, 1, 12))
    region = (along >= 0) & (along <= length) & (abs(across) <= half_width + 2)
    mask = chromatic & region
    values, sats, selected = value[mask], saturation[mask], pixels[mask]
    if not len(values):
        result["pixelCount"] = 0
        return result

    def median(v):
        return float(np.median(v)) if len(v) else None

    def variation(v):
        return float(np.std(v) / max(np.mean(v), 1))

    center = mask & (abs(across) <= max(half_width * .3, 1))
    edge = mask & (abs(across) >= half_width * .7)
    cv, ev = median(value[center]), median(value[edge])
    cs, es = median(saturation[center]), median(saturation[edge])
    # 短い軸に16binを強制すると、画素のないbinを暗い隙間と誤認する。
    bins = min(16, max(1, math.ceil(length / 2)))
    indices = np.clip((along / length * bins).astype(int), 0, bins - 1)
    peaks = np.array([float(value[mask & (indices == i)].max())
                      if np.any(mask & (indices == i)) else 0 for i in range(bins)])
    # 長軸の傾き/隣接差と、色付き面内の隣接差。明暗分布のCVとは別に測る。
    peak_scale = max(float(peaks.max()), 1)
    axis_slope = (abs(float(np.polyfit(np.linspace(0, 1, bins), peaks, 1)[0])) / peak_scale
                  if bins > 1 else 0)
    differences = []
    for left, right, valid in ((value[:, 1:], value[:, :-1], mask[:, 1:] & mask[:, :-1]),
                               (value[1:, :], value[:-1, :], mask[1:, :] & mask[:-1, :])):
        differences.extend(abs(left[valid] - right[valid]).tolist())
    result.update(
        pixelCount=len(values), pixelPeak=float(values.max()), pixelMean=float(values.mean()),
        pixelSat=median(sats), pixelCV=variation(values),
        deepRedRatio=float(np.mean((selected[:, 0] >= 180) & (selected[:, 1] < .4 * selected[:, 0])
                                  & (selected[:, 2] < .65 * selected[:, 0]))),
        RG=median(selected[:, 0] / np.maximum(selected[:, 1], 1)),
        RB=median(selected[:, 0] / np.maximum(selected[:, 2], 1)),
        BG=median(selected[:, 2] / np.maximum(selected[:, 1], 1)),
        BR=median(selected[:, 2] / np.maximum(selected[:, 0], 1)),
        centerValue=cv, edgeValue=ev,
        centerEdgeValue=None if cv is None or ev is None else cv - ev,
        centerEdgeSat=None if cs is None or es is None else cs - es,
        axial220=float(np.mean(peaks >= 220)), axial180=float(np.mean(peaks >= 180)),
        axialRelative=float(np.mean(peaks >= max(values.max() * .8, 64))),
        fill=float(mask.sum() / max(region.sum(), 1)), axisPeakCV=variation(peaks),
        axisSlope=axis_slope,
        axisRoughness=float(np.mean(abs(np.diff(peaks))) / peak_scale) if bins > 1 else 0,
        surfaceGradient=float(np.mean(differences) / 255) if differences else None,
    )
    return result


def collect(clips: list[Path], labels: dict) -> list[dict]:
    rows, used = [], set()
    for clip in clips:
        summary = json.loads((clip / "summary.json").read_text())
        metadata = summary["autoClip"]
        frames = {f["f"]: f for f in metadata["frames"]}
        for image in summary["images"]:
            if image.get("role") != "event":
                continue
            path = (clip / image["path"]).resolve()
            if not path.is_relative_to((clip / "images").resolve()) or "annotated" in path.name.lower():
                raise ValueError("images 内の原画像 event PNG が必要")
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            rgb = np.asarray(Image.open(path).convert("RGB"))
            size = metadata["frameSize"]
            if image.get("scale") != 1 or rgb.shape[:2] != (size["height"], size["width"]):
                raise ValueError(f"フル解像度の原画像が必要: {path}")
            frame = frames[image["frameID"]]
            for color in ("red", "blue"):
                for values in frame[color]["c"]:
                    candidate = dict(zip(metadata["candidateColumns"], values))
                    endpoints = [candidate[k] for k in ("x1", "y1", "x2", "y2")]
                    source = metadata["sources"][candidate["source"]]
                    key = candidate_key(clip.name, frame["f"], color, source, endpoints)
                    label = labels.get(key)
                    if label and label["imageSHA256"] != digest:
                        raise ValueError(f"目視した原画像のSHA256が異なる: {path}")
                    if label:
                        used.add(key)
                    row = {"clip": clip.name, "frame": frame["f"], "color": color,
                           "source": source, "endpoints": endpoints, "image": str(path),
                           "imageSHA256": digest, "label": label["label"] if label else "unknown",
                           "eligible": bool(candidate["flags"] & 1), "rank": candidate["rank"],
                           "backgroundMedian": frame.get("backgroundMedian"),
                           "appliedISO": frame.get("appliedISO"),
                           **{key: candidate.get(key) for key in SAVED_METRICS}}
                    box = [candidate[k] for k in ("minX", "minY", "maxX", "maxY")]
                    row.update(pixel_features(rgb, endpoints, box, candidate["componentArea"], color))
                    rows.append(row)
    if unused := labels.keys() - used:
        raise ValueError(f"入力clipに存在しない候補ラベル: {len(unused)}件")
    return rows


def distributions(rows: list[dict]) -> str:
    metrics = (*SAVED_METRICS, "length", "width", *PIXEL_METRICS)
    lines = ["各セル: min / P10 / P50 / P90 / max。unknownは統計から除外。", ""]
    for color in ("red", "blue"):
        lines += [f"## {color}", "", "|class|n / eligible|" + "|".join(metrics) + "|",
                  "|---|---:|" + "|".join(["---:"] * len(metrics)) + "|"]
        for label in sorted({r["label"] for r in rows} - {"unknown"}):
            group = [r for r in rows if r["color"] == color and r["label"] == label]
            if not group:
                continue
            cells = []
            for metric in metrics:
                values = [float(r[metric]) for r in group if r[metric] is not None]
                cells.append("/".join(f"{v:.4g}" for v in np.percentile(values, [0, 10, 50, 90, 100]))
                             if values else "-")
            lines.append(f"|{label}|{len(group)} / {sum(r['eligible'] for r in group)}|" + "|".join(cells) + "|")
        lines.append("")
    return "\n".join(lines)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("clips", nargs="+", type=Path)
    parser.add_argument("--labels", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    output = private_output(args.output)
    output.mkdir(parents=True, exist_ok=True)
    rows = collect(args.clips, load_labels(args.labels))
    source = Path(__file__).resolve().parents[1] / "PhoneSaberSender"
    originals = []
    for clip in args.clips:
        summary = json.loads((clip / "summary.json").read_text())
        originals.extend({"clip": clip.name, "frame": image["frameID"], "image": image["path"],
                          "sha256": hashlib.sha256((clip / image["path"]).read_bytes()).hexdigest()}
                         for image in summary["images"] if image.get("role") == "event")
    manifest = {
        "schemaVersion": 1, "scriptSHA256": hashlib.sha256(Path(__file__).read_bytes()).hexdigest(),
        "labelsSHA256": hashlib.sha256(args.labels.read_bytes()).hexdigest() if args.labels else None,
        "detectorSourcesSHA256": {name: hashlib.sha256((source / name).read_bytes()).hexdigest()
                                  for name in ("DetectionCore.swift", "BGRADetection.swift")},
        "summariesSHA256": {str(clip): hashlib.sha256((clip / "summary.json").read_bytes()).hexdigest()
                            for clip in args.clips},
        "originals": originals,
    }
    # 既存の測定結果を暗黙に上書きしない。
    for filename, body in (("manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2)),
                           ("candidates.json", json.dumps(rows, ensure_ascii=False, indent=2, allow_nan=False)),
                           ("distributions.md", distributions(rows))):
        with (output / filename).open("x") as handle:
            handle.write(body + "\n")
    with (output / "candidates.csv").open("x", newline="") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0]) if rows else [])
        writer.writeheader()
        writer.writerows(rows)
    print(f"{len(rows)}候補、目視ラベル{sum(r['label'] != 'unknown' for r in rows)}: {output}")


if __name__ == "__main__":
    main()
