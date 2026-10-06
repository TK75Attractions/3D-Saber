#!/usr/bin/env python3
"""Compare two PhoneSaberSender device sessions from saved diagnostics."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any

import analyze_freeze_timeline as freeze_timeline
import analyze_session_metadata as session_metadata


COLORS = ("red", "blue")


def numeric_comparison(baseline: Any, experimental: Any) -> dict[str, Any]:
    if not isinstance(baseline, (int, float)) or isinstance(baseline, bool) \
            or not isinstance(experimental, (int, float)) or isinstance(experimental, bool):
        return {"baseline": baseline, "experimental": experimental, "delta": None, "percent_change": None}
    delta = experimental - baseline
    return {
        "baseline": baseline,
        "experimental": experimental,
        "delta": delta,
        "percent_change": 100.0 * delta / abs(baseline) if baseline != 0 else None,
    }


def _freeze_metrics(summary: dict[str, Any] | None) -> dict[str, Any] | None:
    if summary is None:
        return None
    metrics: dict[str, Any] = {}
    for metric, values in summary["window_aggregate"].items():
        for percentile in ("p50_ms", "p95_ms", "max_ms"):
            metrics[f"{metric}.{percentile}"] = values[percentile]
    metrics["udp_send_gap_event_count"] = summary["udp_send_gap_event_count"]
    for color in ("RED", "BLUE"):
        values = summary["udp_send_gap_by_color"][color]
        metrics[f"udp_{color.lower()}_send_gap_event_count"] = values["event_count"]
        metrics[f"udp_{color.lower()}_max_send_gap_ms"] = values["max_send_gap_ms"]
    return metrics


def compare_freeze(
    baseline: dict[str, Any] | None,
    experimental: dict[str, Any] | None,
) -> dict[str, Any]:
    before = _freeze_metrics(baseline)
    after = _freeze_metrics(experimental)
    if before is None and after is None:
        return {"available": False, "baseline": None, "experimental": None, "metrics": {}}
    names = set(before or {}) | set(after or {})
    return {
        "available": True,
        "baseline": baseline,
        "experimental": experimental,
        "metrics": {
            name: numeric_comparison((before or {}).get(name), (after or {}).get(name))
            for name in sorted(names)
        },
    }


def compare_sessions(
    baseline_metadata: Path,
    experimental_metadata: Path,
    baseline_freeze_log: Path | None = None,
    experimental_freeze_log: Path | None = None,
) -> dict[str, Any]:
    baseline = session_metadata.analyze_file(baseline_metadata)
    experimental = session_metadata.analyze_file(experimental_metadata)
    baseline_timeline = freeze_timeline.analyze_file(baseline_metadata)
    experimental_timeline = freeze_timeline.analyze_file(experimental_metadata)

    baseline_freeze = (
        freeze_timeline.read_freeze_diagnostics(baseline_freeze_log)
        if baseline_freeze_log is not None else None
    )
    experimental_freeze = (
        freeze_timeline.read_freeze_diagnostics(experimental_freeze_log)
        if experimental_freeze_log is not None else None
    )
    metadata_differences = session_metadata.comparison(baseline, experimental)
    # The timeline analyzer is the canonical event detector for long identical
    # endpoints. Use its event runs in the A/B result to avoid a second detector.
    for color in COLORS:
        for side, timeline in (("before", baseline_timeline), ("after", experimental_timeline)):
            runs = sum(
                event["color"] == color.upper()
                and event["eventType"] == "identical_endpoint"
                and event["frameCount"] >= 4
                for event in timeline["events"]
            )
            metadata_differences["colors"][color]["identical_endpoint_suspect_4f_runs"][side] = runs
        entry = metadata_differences["colors"][color]["identical_endpoint_suspect_4f_runs"]
        entry["delta"] = entry["after"] - entry["before"]
        entry["percent_change"] = (
            100.0 * entry["delta"] / abs(entry["before"]) if entry["before"] != 0 else None
        )

    return {
        "sessions": {
            "baseline": baseline,
            "experimental": experimental,
        },
        "ground_truth": {
            "source": "metadata only",
            "detection": (
                "actual fresh detector success uses redDetectionSucceeded/blueDetectionSucceeded "
                "when present; otherwise detected=true with predicted=true is excluded"
            ),
            "object_presence": (
                "unknown from metadata; a detection miss may mean the object was outside the camera view"
            ),
        },
        "comparison": {
            "colors": metadata_differences["colors"],
            "freeze": compare_freeze(baseline_freeze, experimental_freeze),
        },
    }


def format_number(value: Any, digits: int = 2) -> str:
    if value is None:
        return "n/a"
    if isinstance(value, float):
        return f"{value:.{digits}f}"
    return str(value)


def print_metric(name: str, result: dict[str, Any], *, unit: str = "") -> None:
    baseline = result["before"]
    experimental = result["after"]
    delta = result["delta"]
    percent = result["percent_change"]
    if baseline is None or experimental is None or delta is None:
        print(f"  {name}: baseline={format_number(baseline)} experimental={format_number(experimental)}")
        return
    relative = "n/a" if percent is None else f"{percent:+.1f}%"
    print(
        f"  {name}: {format_number(baseline)} → {format_number(experimental)} "
        f"(Δ {delta:+.2f}{unit}; {relative})"
    )


def print_report(result: dict[str, Any]) -> None:
    sessions = result["sessions"]
    print(
        f"baseline: {sessions['baseline']['session_id'] or Path(sessions['baseline']['file']).name}"
    )
    print(
        f"experimental: {sessions['experimental']['session_id'] or Path(sessions['experimental']['file']).name}"
    )
    print("Ground truth: metadata detector output only; object presence/off-screen status is unknown.")
    comparison = result["comparison"]["colors"]
    for color in COLORS:
        values = comparison[color]
        print(color.upper())
        print_metric("actual detection rate", values["detection_rate_percent"], unit=" pp")
        print_metric("dropout runs", values["dropout_runs"])
        print_metric("longest dropout frames", values["longest_dropout_frames"])
        for metric, label in (
            ("dropout_1_frame_runs", "1f dropout runs"),
            ("dropout_2_frame_runs", "2f dropout runs"),
            ("dropout_3_frame_runs", "3f dropout runs"),
            ("dropout_4_or_more", ">=4f dropout runs"),
            ("candidate_zero_frames", "candidate=0 frames"),
            ("candidate_present_eligible_zero_frames", "candidate>0 eligible=0 frames"),
            ("jumps_180_or_more", "endpoint jumps >=180px"),
            ("jumps_260_or_more", "endpoint jumps >=260px"),
            ("identical_endpoint_suspect_4f_runs", "identical endpoint suspect >=4f"),
            ("core_line_selected", "core-line selected"),
        ):
            print_metric(label, values[metric])
        print("  selected candidate types:")
        for metric, stats in values.items():
            if metric.startswith("candidate_type:"):
                print_metric(metric.partition(":")[2], stats)

    freeze = result["comparison"]["freeze"]
    print("FREEZE DIAGNOSTICS")
    if not freeze["available"]:
        print("  no freeze logs supplied")
        return
    for name, stats in freeze["metrics"].items():
        print_metric(name, stats, unit="ms" if name.endswith("_ms") else "")
    for side in ("baseline", "experimental"):
        report = freeze[side]
        if report is not None:
            print(f"  {side} parsed summary windows: {report['summary_window_count']}")
    example = next((freeze[side] for side in ("baseline", "experimental") if freeze[side]), None)
    if example:
        print(f"  note: {example['aggregation_note']}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("baseline_metadata", type=Path)
    parser.add_argument("experimental_metadata", type=Path)
    parser.add_argument("--baseline-freeze-log", type=Path)
    parser.add_argument("--experimental-freeze-log", type=Path)
    parser.add_argument(
        "--json", nargs="?", const="-", metavar="PATH",
        help="write JSON to PATH, or stdout when PATH is omitted",
    )
    arguments = parser.parse_args()
    try:
        result = compare_sessions(
            arguments.baseline_metadata,
            arguments.experimental_metadata,
            arguments.baseline_freeze_log,
            arguments.experimental_freeze_log,
        )
        if arguments.json == "-":
            print(json.dumps(result, ensure_ascii=False, indent=2))
        else:
            print_report(result)
            if arguments.json:
                Path(arguments.json).write_text(
                    json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
                )
    except (OSError, ValueError, KeyError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
