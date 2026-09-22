#!/usr/bin/env python3
"""Compile the real DetectionCore harness and run the lossless fixture manifest."""

from __future__ import annotations

import argparse
import csv
import json
import math
import shutil
import statistics
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any


TOOLS_DIR = Path(__file__).resolve().parent
REPO = TOOLS_DIR.parents[2]
DEFAULT_MANIFEST = TOOLS_DIR / "lossless_regression_manifest.json"


def percentile(values: list[float], fraction: float) -> float:
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, math.ceil(len(ordered) * fraction) - 1)]


def command_output(command: list[str], *, input_data: bytes | None = None) -> bytes:
    result = subprocess.run(command, input=input_data, capture_output=True, check=False)
    if result.returncode:
        raise RuntimeError(
            f"command failed ({result.returncode}): {' '.join(command)}\n"
            f"{result.stderr.decode(errors='replace')}"
        )
    return result.stdout


def compile_harness(destination: Path) -> None:
    sources = [
        REPO / "ios/PhoneSaberSender/PhoneSaberSender/DetectionCore.swift",
        REPO / "ios/PhoneSaberSender/PhoneSaberSender/BGRADetection.swift",
        REPO / "ios/PhoneSaberSenderTests/VideoDetectionDiagnostic.swift",
    ]
    command_output(["xcrun", "swiftc", "-O", *(str(path) for path in sources), "-o", str(destination)])


def dimensions(path: Path) -> tuple[int, int]:
    output = command_output([
        "ffprobe", "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=width,height", "-of", "csv=p=0", str(path),
    ]).decode().strip()
    width, height = output.split(",")
    return int(width), int(height)


def analyze_png(harness: Path, path: Path) -> dict[str, Any]:
    width, height = dimensions(path)
    raw = command_output([
        "ffmpeg", "-loglevel", "error", "-i", str(path),
        "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "-",
    ])
    line = command_output([str(harness), str(width), str(height), "2"], input_data=raw).splitlines()[0]
    return json.loads(line)


def selected_candidate(color_data: dict[str, Any]) -> dict[str, Any] | None:
    return next((candidate for candidate in color_data["candidates"] if candidate["eligible"]), None)


def endpoint_text(endpoints: Any) -> str:
    if not isinstance(endpoints, list):
        return "-"
    return f"({endpoints[0]['x']},{endpoints[0]['y']})->({endpoints[1]['x']},{endpoints[1]['y']})"


def endpoint_length(endpoints: Any) -> float | None:
    if not isinstance(endpoints, list):
        return None
    return math.hypot(endpoints[1]["x"] - endpoints[0]["x"],
                      endpoints[1]["y"] - endpoints[0]["y"])


def load_fixtures(manifest: Path) -> tuple[list[dict[str, Any]], list[str]]:
    document = json.loads(manifest.read_text(encoding="utf-8"))
    fixtures: list[dict[str, Any]] = []
    skipped: list[str] = []
    for fixture in document["fixtures"]:
        expanded = fixture["path"].replace("{REPO}", str(REPO)).replace("{HOME}", str(Path.home()))
        path = Path(expanded)
        if not path.exists():
            if fixture.get("optional"):
                skipped.append(fixture["name"])
                continue
            raise FileNotFoundError(path)
        fixtures.append({**fixture, "resolved_path": path})
    return fixtures, skipped


def build_row(fixture: dict[str, Any], analysis: dict[str, Any]) -> dict[str, Any]:
    row: dict[str, Any] = {
        "fixture": fixture["name"], "category": fixture["category"],
        "path": str(fixture["resolved_path"]), "processing_ms": analysis["profile"]["total_ms"],
    }
    for color in ("red", "blue"):
        data = analysis["colors"][color]
        selected = selected_candidate(data)
        endpoints = data["selected"]
        pipeline = analysis["pipeline"][color]
        row.update({
            f"expected_{color}": fixture.get(color),
            f"detected_{color}": endpoints is not None,
            f"selected_type_{color}": selected["source"] if selected else None,
            f"selected_score_{color}": selected["score"] if selected else None,
            f"endpoint_{color}": endpoint_text(endpoints),
            f"endpoint_length_{color}": endpoint_length(endpoints),
            f"mask_pixels_{color}": pipeline["mask_pixels"],
            f"morphology_pixels_{color}": pipeline["morphology_pixels"],
            f"candidate_count_{color}": len(data["candidates"]),
            f"eligible_count_{color}": sum(bool(item["eligible"]) for item in data["candidates"]),
        })
    return row


def summary(rows: list[dict[str, Any]], skipped: list[str]) -> dict[str, Any]:
    result: dict[str, Any] = {"fixture_count": len(rows), "skipped_optional": skipped}
    for color in ("blue", "red"):
        positives = [row for row in rows if row[f"expected_{color}"] is True]
        negatives = [row for row in rows if row[f"expected_{color}"] is False]
        result[color] = {
            "positive_detected": sum(row[f"detected_{color}"] for row in positives),
            "positive_total": len(positives),
            "negative_detected": sum(row[f"detected_{color}"] for row in negatives),
            "negative_total": len(negatives),
        }
    timings = [float(row["processing_ms"]) for row in rows]
    result["processing_ms"] = {
        "median": statistics.median(timings), "p95": percentile(timings, 0.95), "max": max(timings),
    }
    return result


def print_rows(rows: list[dict[str, Any]], result: dict[str, Any]) -> None:
    for row in rows:
        print(f"{row['fixture']} [{row['category']}] {row['processing_ms']:.3f}ms")
        for color in ("red", "blue"):
            print(
                f"  {color.upper()} detected={str(row[f'detected_{color}']).lower()} "
                f"type={row[f'selected_type_{color}'] or '-'} "
                f"score={row[f'selected_score_{color}'] if row[f'selected_score_{color}'] is not None else '-'} "
                f"endpoint={row[f'endpoint_{color}']} "
                f"length={row[f'endpoint_length_{color}'] if row[f'endpoint_length_{color}'] is not None else '-'} "
                f"mask={row[f'mask_pixels_{color}']} morph={row[f'morphology_pixels_{color}']} "
                f"candidates={row[f'candidate_count_{color}']} eligible={row[f'eligible_count_{color}']}"
            )
    print("SUMMARY")
    print(f"BLUE positive detected {result['blue']['positive_detected']}/{result['blue']['positive_total']}")
    print(f"RED positive detected {result['red']['positive_detected']}/{result['red']['positive_total']}")
    negative_detected = result["blue"]["negative_detected"] + result["red"]["negative_detected"]
    negative_total = result["blue"]["negative_total"] + result["red"]["negative_total"]
    print(f"negative detected {negative_detected}/{negative_total}")
    timing = result["processing_ms"]
    print(f"processing ms median={timing['median']:.3f} p95={timing['p95']:.3f} max={timing['max']:.3f}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--json", type=Path, help="save full results as JSON")
    parser.add_argument("--csv", type=Path, help="save per-fixture results as CSV")
    arguments = parser.parse_args()
    for dependency in ("xcrun", "ffmpeg", "ffprobe"):
        if not shutil.which(dependency):
            print(f"error: required command not found: {dependency}", file=sys.stderr)
            return 2
    try:
        fixtures, skipped = load_fixtures(arguments.manifest)
        with tempfile.TemporaryDirectory(prefix="phonesaber-lossless-") as temporary:
            harness = Path(temporary) / "video-detection-diagnostic"
            compile_harness(harness)
            rows = [build_row(fixture, analyze_png(harness, fixture["resolved_path"]))
                    for fixture in fixtures]
        result = summary(rows, skipped)
        print_rows(rows, result)
        if arguments.json:
            arguments.json.write_text(json.dumps({"fixtures": rows, "summary": result},
                                                  ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        if arguments.csv:
            with arguments.csv.open("w", newline="", encoding="utf-8") as handle:
                writer = csv.DictWriter(handle, fieldnames=list(rows[0]))
                writer.writeheader()
                writer.writerows(rows)
    except (OSError, ValueError, RuntimeError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
