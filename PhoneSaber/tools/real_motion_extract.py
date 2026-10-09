#!/usr/bin/env python3
"""Read Debug Recording metadata / triage contexts without reading any images.

Outputs source-image pixels, relative capture times and anonymous session IDs.
Missing flags remain unknown (empty CSV fields / JSON null), never invented.
Summary/state aggregates and forensic images cannot supply per-frame motion.
"""

import argparse
import csv
import json
import math
from pathlib import Path

FIELDS = ("session", "frame", "time_s", "x1", "y1", "x2", "y2", "color",
          "detected", "predicted", "width", "height", "time_source", "label")


def recording_files(paths):
    # 明示されたファイル、metadata、triage の frames のみ。画像には触れない。
    found = set()
    for path in paths:
        path = Path(path).expanduser()
        if path.is_file():
            found.add(path.resolve())
        elif path.is_dir():
            found.update(p.resolve() for p in path.rglob("*_metadata.json"))
            found.update(p.resolve() for p in path.rglob("*.json") if p.parent.name == "frames")
            found.update(p.resolve() for p in path.rglob("*.jsonl"))
        else:
            raise ValueError(f"input does not exist: {path}")
    return sorted(found)


def endpoint_values(color):
    if all(k in color for k in ("x1", "y1", "x2", "y2")):
        values = [color[k] for k in ("x1", "y1", "x2", "y2")]
    else:
        values = color.get("endpoint", color.get("endpoints"))
        if isinstance(values, dict) and "first" in values and "second" in values:
            values = [values[p][k] for p in ("first", "second") for k in ("x", "y")]
    if not isinstance(values, (list, tuple)) or len(values) != 4:
        return (None,) * 4
    values = tuple(float(v) for v in values)
    return values if all(math.isfinite(v) for v in values) else (None,) * 4


def extract(paths, image_width=None, image_height=None):
    records, sources = {}, {}
    skipped_time = 0
    for path in recording_files(paths):
        with path.open(encoding="utf-8") as stream:
            documents = [json.loads(line) for line in stream if line.strip()] if path.suffix == ".jsonl" else [json.load(stream)]
        for document in documents:
            if isinstance(document, list):
                document = {"frames": document}
            if not isinstance(document, dict):
                continue
            session = document.get("sessionID", path.parent.parent.name if path.parent.name == "frames" else path.stem.removesuffix("_metadata"))
            session = session.removeprefix("phone_saber_triage_")
            frames = document.get("frames", [document] if "frameID" in document else [])
            for frame in frames:
                time_key = next((k for k in ("presentationTimeSeconds", "captureTimeSeconds", "timestamp")
                                 if isinstance(frame.get(k), (int, float)) and math.isfinite(frame[k])), None)
                if time_key is None:
                    skipped_time += 1
                    continue
                for color in ("red", "blue"):
                    state = frame.get(color)
                    if not isinstance(state, dict):
                        continue
                    predicted = state.get("predicted", state.get("predictionUsed"))
                    detected = state.get("detected")
                    if detected is not None and not isinstance(detected, bool):
                        raise ValueError("detected must be boolean")
                    if predicted is not None and not isinstance(predicted, bool):
                        raise ValueError("predicted must be boolean")
                    row = dict(zip(("x1", "y1", "x2", "y2"), endpoint_values(state)))
                    row.update(session=session, frame=frame.get("frameID"), time_s=float(frame[time_key]),
                               color=color, detected=detected, predicted=predicted,
                               width=document.get("width", image_width), height=document.get("height", image_height),
                               time_source=time_key, label=frame.get("segmentLabel", document.get("segmentLabel", "unlabeled")))
                    key = (session, row["time_s"], color)
                    previous = records.get(key)
                    if previous:
                        for field in ("frame", "x1", "y1", "x2", "y2", "detected", "predicted", "width", "height"):
                            if previous[field] is not None and row[field] is not None and previous[field] != row[field]:
                                raise ValueError(f"conflicting duplicate frame: {session} {row['frame']} {color} {field}")
                            if row[field] is None:
                                row[field] = previous[field]
                    records[key] = row
                    sources.setdefault(session, set()).add(str(path))
    names = sorted(sources)
    aliases = {name: f"s{i:03d}" for i, name in enumerate(names)}
    starts = {name: min(r["time_s"] for r in records.values() if r["session"] == name) for name in names}
    rows = sorted(records.values(), key=lambda r: (r["session"], r["time_s"], r["color"]))
    for row in rows:
        row["time_s"] -= starts[row["session"]]
        row["session"] = aliases[row["session"]]
    manifest = {"schema": 1, "units": "source-image pixels; relative capture seconds",
                "rows": len(rows), "skipped_missing_time": skipped_time,
                "sessions": [{"alias": aliases[name], "source_session": name, "files": sorted(sources[name])}
                             for name in names]}
    return rows, manifest


def write_rows(path, rows):
    if path.suffix == ".json":
        path.write_text(json.dumps(rows, separators=(",", ":"), allow_nan=False) + "\n", encoding="utf-8")
    else:
        with path.open("w", newline="", encoding="utf-8") as stream:
            writer = csv.DictWriter(stream, fieldnames=FIELDS)
            writer.writeheader()
            writer.writerows(rows)


def read_rows(path):
    if Path(path).suffix == ".json":
        return json.loads(Path(path).read_text(encoding="utf-8"))
    with Path(path).open(newline="", encoding="utf-8") as stream:
        rows = list(csv.DictReader(stream))
    for row in rows:
        for field in ("time_s", "x1", "y1", "x2", "y2", "width", "height"):
            row[field] = float(row[field]) if row[field] else None
        for field in ("detected", "predicted"):
            value = row[field].lower()
            if value not in ("true", "false", ""):
                raise ValueError(f"invalid flag: {field}={value}")
            row[field] = {"true": True, "false": False, "": None}[value]
    return rows


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("paths", nargs="+", type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--manifest", type=Path, help="private provenance; keep outside Git")
    parser.add_argument("--image-width", type=int, help="explicit fallback for dimensionless triage contexts")
    parser.add_argument("--image-height", type=int)
    args = parser.parse_args(argv)
    rows, manifest = extract(args.paths, args.image_width, args.image_height)
    write_rows(args.output, rows)
    if args.manifest:
        args.manifest.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"sessions={len(manifest['sessions'])} rows={len(rows)} bytes={args.output.stat().st_size} skipped_missing_time={manifest['skipped_missing_time']}")


if __name__ == "__main__":
    main()
