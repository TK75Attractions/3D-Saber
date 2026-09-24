#!/usr/bin/env python3
"""Summarize PhoneSaberSender Debug Recording metadata without re-running detection."""

from __future__ import annotations

import argparse
import json
import math
import statistics
import sys
from collections import Counter
from pathlib import Path
from typing import Any


COLORS = ("blue", "red")
RAW_ROBUST_RATIO = 2.0
RAW_ROBUST_DELTA_PX = 80.0


def percentile(values: list[float], fraction: float) -> float | None:
    if not values:
        return None
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, math.ceil(len(ordered) * fraction) - 1)]


def detection_status(frame: dict[str, Any], color: str) -> bool | None:
    """Return fresh detector success, excluding prediction-only output."""
    explicit = frame.get(f"{color}DetectionSucceeded")
    if isinstance(explicit, bool):
        return explicit
    value = frame.get(color)
    if not isinstance(value, dict) or not isinstance(value.get("detected"), bool):
        return None
    if value.get("predicted") is True:
        return False
    return value["detected"]


def endpoint_tuple(frame: dict[str, Any], color: str) -> tuple[float, float, float, float] | None:
    value = frame.get(color) or {}
    if detection_status(frame, color) is False or not value.get("detected"):
        return None
    coordinates = (value.get("x1"), value.get("y1"), value.get("x2"), value.get("y2"))
    if not all(isinstance(item, (int, float)) for item in coordinates):
        return None
    return tuple(float(item) for item in coordinates)  # type: ignore[return-value]


def endpoint_jump(a: tuple[float, ...], b: tuple[float, ...]) -> float:
    def distance(ax: float, ay: float, bx: float, by: float) -> float:
        return math.hypot(ax - bx, ay - by)

    forward = max(distance(a[0], a[1], b[0], b[1]), distance(a[2], a[3], b[2], b[3]))
    reverse = max(distance(a[0], a[1], b[2], b[3]), distance(a[2], a[3], b[0], b[1]))
    return min(forward, reverse)


def contiguous_runs(flags: list[bool]) -> list[tuple[int, int]]:
    runs: list[tuple[int, int]] = []
    start: int | None = None
    for index, enabled in enumerate(flags + [False]):
        if enabled and start is None:
            start = index
        elif not enabled and start is not None:
            runs.append((start, index - 1))
            start = None
    return runs


def run_duration_ms(frames: list[dict[str, Any]], start: int, end: int) -> float | None:
    timestamps = [frame.get("presentationTimeSeconds") for frame in frames]
    first = timestamps[start]
    if not isinstance(first, (int, float)):
        return None
    if end + 1 < len(frames) and isinstance(timestamps[end + 1], (int, float)):
        return max(0.0, (float(timestamps[end + 1]) - float(first)) * 1000.0)
    last = timestamps[end]
    if not isinstance(last, (int, float)):
        return None
    intervals = [
        float(right) - float(left)
        for left, right in zip(timestamps, timestamps[1:])
        if isinstance(left, (int, float)) and isinstance(right, (int, float)) and right > left
    ]
    tail = statistics.median(intervals) if intervals else 0.0
    return max(0.0, (float(last) - float(first) + tail) * 1000.0)


def selected_candidate(frame: dict[str, Any], color: str) -> dict[str, Any] | None:
    diagnostics = ((frame.get("candidateDiagnostics") or {}).get(color) or {})
    candidate = diagnostics.get("selectedCandidate")
    return candidate if isinstance(candidate, dict) else None


def timing_value(frame: dict[str, Any]) -> float | None:
    containers = [frame, frame.get("timing") or {}, frame.get("candidateDiagnostics") or {}]
    keys = (
        "processingTimeMilliseconds", "processingTimeMs", "detectionProcessingMilliseconds",
        "detectionTimeMilliseconds", "detectionTimeMs",
    )
    for container in containers:
        if not isinstance(container, dict):
            continue
        for key in keys:
            value = container.get(key)
            if isinstance(value, (int, float)):
                return float(value)
    return None


def analyze_color(frames: list[dict[str, Any]], color: str) -> dict[str, Any]:
    statuses = [detection_status(frame, color) for frame in frames]
    known_statuses = [status for status in statuses if status is not None]
    # Unknown frames break dropout runs instead of being counted as misses.
    dropout_runs = contiguous_runs([status is False for status in statuses])
    dropout_lengths = [end - start + 1 for start, end in dropout_runs]
    dropout_durations = [run_duration_ms(frames, start, end) for start, end in dropout_runs]

    endpoints = [endpoint_tuple(frame, color) for frame in frames]
    stuck_runs: list[tuple[int, int]] = []
    run_start: int | None = None
    for index in range(1, len(endpoints) + 1):
        same = index < len(endpoints) and endpoints[index] is not None \
            and endpoints[index - 1] is not None and endpoints[index] == endpoints[index - 1]
        if same and run_start is None:
            run_start = index - 1
        if not same and run_start is not None:
            stuck_runs.append((run_start, index - 1))
            run_start = None

    jumps: list[float] = []
    previous: tuple[float, ...] | None = None
    for endpoint in endpoints:
        if endpoint is not None:
            if previous is not None:
                jumps.append(endpoint_jump(previous, endpoint))
            previous = endpoint
        else:
            # Count frame-to-frame jumps only. A reacquisition after a
            # dropout has no observed intermediate trajectory to compare.
            previous = None

    sources: Counter[str] = Counter()
    core_line = 0
    raw_robust_divergence = 0
    eligible_zero = 0
    candidate_zero = 0
    candidate_present_eligible_zero = 0
    mask_lost_in_morphology = 0
    diagnostic_frames = 0
    for frame in frames:
        diagnostics = ((frame.get("candidateDiagnostics") or {}).get(color) or {})
        if diagnostics:
            diagnostic_frames += 1
        total_count = diagnostics.get("totalCandidateCount")
        eligible_count = diagnostics.get("eligibleCandidateCount")
        has_candidate_counts = isinstance(total_count, (int, float)) and isinstance(eligible_count, (int, float))
        if has_candidate_counts and eligible_count == 0:
            eligible_zero += 1
            if total_count == 0:
                candidate_zero += 1
            elif total_count > 0:
                candidate_present_eligible_zero += 1
        mask_count = diagnostics.get("maskPixelCount")
        morphology_count = diagnostics.get("morphologyPixelCount")
        if isinstance(mask_count, (int, float)) and mask_count > 0 and morphology_count == 0:
            mask_lost_in_morphology += 1
        candidate = selected_candidate(frame, color)
        if candidate:
            source = str(candidate.get("sourceType", "unknown"))
            sources[source] += 1
            if source.startswith("core-line"):
                core_line += 1
            raw = candidate.get("rawPCASpan")
            robust = candidate.get("robustMainIntervalLength")
            if isinstance(raw, (int, float)) and isinstance(robust, (int, float)) \
                    and raw >= robust * RAW_ROBUST_RATIO and raw - robust >= RAW_ROBUST_DELTA_PX:
                raw_robust_divergence += 1

    timings = [value for frame in frames if (value := timing_value(frame)) is not None]
    longest_dropout_index = max(range(len(dropout_runs)), key=lambda i: dropout_lengths[i]) \
        if dropout_runs else None
    longest_stuck_suspect = max((end - start + 1 for start, end in stuck_runs), default=0)
    detected_count = sum(known_statuses)
    return {
        "frames": len(frames),
        "frames_with_detection_status": len(known_statuses),
        "unknown_detection_status_frames": len(frames) - len(known_statuses),
        "detection_status_coverage_percent": 100.0 * len(known_statuses) / len(frames) if frames else 0.0,
        "detected_frames": detected_count,
        "detection_rate_percent": 100.0 * detected_count / len(known_statuses) if known_statuses else None,
        "not_detected_frames": sum(status is False for status in statuses),
        "dropout_runs": len(dropout_runs),
        "longest_dropout_frames": dropout_lengths[longest_dropout_index] if longest_dropout_index is not None else 0,
        "longest_dropout_milliseconds": dropout_durations[longest_dropout_index] if longest_dropout_index is not None else 0.0,
        "dropout_length_counts": {
            "1": dropout_lengths.count(1), "2": dropout_lengths.count(2),
            "3": dropout_lengths.count(3), "4_or_more": sum(length >= 4 for length in dropout_lengths),
        },
        "identical_endpoint_suspect_runs": len(stuck_runs),
        "stuck_suspect_runs_4_or_more": sum(end - start + 1 >= 4 for start, end in stuck_runs),
        "longest_stuck_suspect_frames": longest_stuck_suspect,
        "jumps_180_or_more": sum(value >= 180 for value in jumps),
        "jumps_260_or_more": sum(value >= 260 for value in jumps),
        "selected_candidate_types": dict(sorted(sources.items())),
        "core_line_selected": core_line,
        "raw_robust_large_divergence": raw_robust_divergence,
        "eligible_candidate_zero_frames": eligible_zero,
        "total_candidate_zero_frames": candidate_zero,
        "candidate_present_eligible_zero_frames": candidate_present_eligible_zero,
        "mask_present_morphology_zero_frames": mask_lost_in_morphology,
        "candidate_diagnostic_frames": diagnostic_frames,
        "processing_timing_ms": {
            "available_frames": len(timings),
            "median": statistics.median(timings) if timings else None,
            "p95": percentile(timings, 0.95),
            "max": max(timings) if timings else None,
        },
    }


def analyze_file(path: Path) -> dict[str, Any]:
    with path.open(encoding="utf-8") as handle:
        document = json.load(handle)
    frames = document.get("frames") if isinstance(document, dict) else document
    if not isinstance(frames, list):
        raise ValueError("metadata must contain a frames array")
    return {
        "file": str(path),
        "session_id": document.get("sessionID") if isinstance(document, dict) else None,
        "colors": {color: analyze_color(frames, color) for color in COLORS},
        "prediction_bridge": {
            "available": any(
                isinstance(frame.get(f"{color}DetectionSucceeded"), bool)
                or isinstance((frame.get(color) or {}).get("predicted"), bool)
                for frame in frames for color in COLORS
            ),
            "note": "detectionSucceeded counts fresh detector output; prediction-only coordinates are excluded",
        },
        "ground_truth": {
            "object_presence": "unknown from metadata; an undetected frame may have the object outside the camera view",
            "dropouts": "metadata detection misses only; not confirmed object-recognition failures",
        },
        "definitions": {
            "jump": "minimum endpoint-order maximum displacement in pixels",
            "raw_robust_divergence": {
                "ratio_at_least": RAW_ROBUST_RATIO, "difference_pixels_at_least": RAW_ROBUST_DELTA_PX,
            },
        },
    }


def print_color(color: str, result: dict[str, Any]) -> None:
    rate = result["detection_rate_percent"]
    duration = result["longest_dropout_milliseconds"]
    duration_text = "n/a" if duration is None else f"{duration:.1f}ms"
    rate_text = "n/a" if rate is None else f"{rate:.2f}%"
    counts = result["dropout_length_counts"]
    print(color.upper())
    print(f"frames: {result['frames']}")
    print(f"actual detected: {result['detected_frames']} / {result['frames_with_detection_status']} = {rate_text}")
    print(f"detection status coverage: {result['detection_status_coverage_percent']:.2f}% "
          f"(unknown {result['unknown_detection_status_frames']})")
    print(f"detected=false: {result['not_detected_frames']}")
    print(f"dropout runs: {result['dropout_runs']}")
    print(f"longest dropout: {result['longest_dropout_frames']} frames / {duration_text}")
    print(f"1f: {counts['1']}  2f: {counts['2']}  3f: {counts['3']}  >=4f: {counts['4_or_more']}")
    print(f"identical-endpoint suspect runs: {result['identical_endpoint_suspect_runs']}")
    print(f"stuckSuspect >=4f: {result['stuck_suspect_runs_4_or_more']}  "
          f"longest stuckSuspect: {result['longest_stuck_suspect_frames']} frames")
    print(f"jumps >=180px: {result['jumps_180_or_more']}  jumps >=260px: {result['jumps_260_or_more']}")
    print(f"selected types: {json.dumps(result['selected_candidate_types'], ensure_ascii=False, sort_keys=True)}")
    print(f"core-line selected: {result['core_line_selected']}")
    print(f"raw/robust large divergence: {result['raw_robust_large_divergence']}")
    print(f"eligible=0: {result['eligible_candidate_zero_frames']}")
    print(f"candidate=0: {result['total_candidate_zero_frames']}  "
          f"candidate>0 eligible=0: {result['candidate_present_eligible_zero_frames']}")
    print(f"mask>0 morphology=0: {result['mask_present_morphology_zero_frames']}")
    timing = result["processing_timing_ms"]
    if timing["available_frames"]:
        print(f"processing ms: median={timing['median']:.3f} p95={timing['p95']:.3f} max={timing['max']:.3f}")
    else:
        print("processing ms: unavailable in current metadata")


def print_analysis(result: dict[str, Any]) -> None:
    print(f"session: {result.get('session_id') or Path(result['file']).stem}")
    for color in COLORS:
        print_color(color, result["colors"][color])
    print("prediction bridge: 現在のmetadataでは判定不能")


def comparison(before: dict[str, Any], after: dict[str, Any]) -> dict[str, Any]:
    metrics = {
        "detection_rate_percent": lambda value: value["detection_rate_percent"],
        "dropout_runs": lambda value: value["dropout_runs"],
        "longest_dropout_frames": lambda value: value["longest_dropout_frames"],
        "dropout_1_frame_runs": lambda value: value["dropout_length_counts"]["1"],
        "dropout_2_frame_runs": lambda value: value["dropout_length_counts"]["2"],
        "dropout_3_frame_runs": lambda value: value["dropout_length_counts"]["3"],
        "dropout_4_or_more": lambda value: value["dropout_length_counts"]["4_or_more"],
        "stuck_suspect_runs_4_or_more": lambda value: value["stuck_suspect_runs_4_or_more"],
        "identical_endpoint_suspect_4f_runs": lambda value: value["stuck_suspect_runs_4_or_more"],
        "jumps_180_or_more": lambda value: value["jumps_180_or_more"],
        "jumps_260_or_more": lambda value: value["jumps_260_or_more"],
        "candidate_zero_frames": lambda value: value["total_candidate_zero_frames"],
        "candidate_present_eligible_zero_frames": lambda value: value["candidate_present_eligible_zero_frames"],
        "core_line_selected": lambda value: value["core_line_selected"],
    }
    color_diffs: dict[str, Any] = {}
    for color in COLORS:
        values: dict[str, Any] = {}
        for name, extractor in metrics.items():
            left = extractor(before["colors"][color])
            right = extractor(after["colors"][color])
            delta = right - left if left is not None and right is not None else None
            values[name] = {
                "before": left, "after": right, "delta": delta,
                "percent_change": (100.0 * delta / abs(left)) if delta is not None and left != 0 else None,
                "delta_percentage_points": delta if name == "detection_rate_percent" else None,
            }
        source_types = set(before["colors"][color]["selected_candidate_types"])
        source_types.update(after["colors"][color]["selected_candidate_types"])
        for source in sorted(source_types):
            left = before["colors"][color]["selected_candidate_types"].get(source, 0)
            right = after["colors"][color]["selected_candidate_types"].get(source, 0)
            delta = right - left
            values[f"candidate_type:{source}"] = {
                "before": left, "after": right, "delta": delta,
                "percent_change": (100.0 * delta / abs(left)) if left != 0 else None,
                "delta_percentage_points": None,
            }
        color_diffs[color] = values
    return {
        "before": before["file"], "after": after["file"],
        "colors": color_diffs,
    }


def print_comparison(result: dict[str, Any]) -> None:
    for color in COLORS:
        print(color.upper())
        for key, values in result["colors"][color].items():
            left, right, delta = values["before"], values["after"], values["delta"]
            if left is None or right is None or delta is None:
                print(f"{key}: before={left} after={right} delta=n/a")
                continue
            percent = "n/a" if values["percent_change"] is None else f"{values['percent_change']:+.1f}%"
            unit = "pp" if key == "detection_rate_percent" else ""
            print(f"{key}: {left:.2f} → {right:.2f} delta={delta:+.2f}{unit} ({percent})")


def write_json(result: dict[str, Any], destination: str | None) -> None:
    if destination is None:
        return
    encoded = json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True) + "\n"
    if destination == "-":
        print(encoded, end="")
    else:
        Path(destination).write_text(encoded, encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("metadata", nargs="?", type=Path)
    parser.add_argument("--compare", nargs=2, metavar=("BEFORE", "AFTER"), type=Path)
    parser.add_argument("--json", nargs="?", const="-", dest="json_destination",
                        help="write JSON to PATH, or stdout when PATH is omitted")
    arguments = parser.parse_args()
    try:
        if arguments.compare:
            result = comparison(analyze_file(arguments.compare[0]), analyze_file(arguments.compare[1]))
            if arguments.json_destination != "-":
                print_comparison(result)
        elif arguments.metadata:
            result = analyze_file(arguments.metadata)
            if arguments.json_destination != "-":
                print_analysis(result)
        else:
            parser.error("provide METADATA or --compare BEFORE AFTER")
        write_json(result, arguments.json_destination)
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
