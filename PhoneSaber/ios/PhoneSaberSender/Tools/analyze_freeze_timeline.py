#!/usr/bin/env python3
"""Extract suspicious PhoneSaber intervals from one recorded metadata JSON file."""

from __future__ import annotations

import argparse
import json
import math
import re
import statistics
import sys
from pathlib import Path
from typing import Any

from analyze_session_metadata import (
    COLORS, RAW_ROBUST_DELTA_PX, RAW_ROBUST_RATIO, contiguous_runs,
    detection_status, endpoint_jump, endpoint_tuple, selected_candidate,
)
from phone_saber_metadata_schema import load_metadata_file


UNITY_GAP = re.compile(
    r"\[FREEZE\]\[Unity (RX|APPLY)\]\[(BLUE|RED)\].*?"
    r"\bgap=([0-9]+(?:\.[0-9]+)?)ms\b.*?"
    r"\b(receive|apply)=([0-9]+(?:\.[0-9]+)?)\b"
)
FREEZE_SUMMARY = re.compile(
    r"\[FREEZE_DIAG\]\[SUMMARY\]\s+"
    r"cameraGap p50/p95/max=(?P<camera>[^\s]+)ms\s+"
    r"queueWait p50/p95/max=(?P<queue>[^\s]+)ms\s+"
    r"detection p50/p95/max=(?P<detection>[^\s]+)ms\s+"
    r"mainDelay p50/p95/max=(?P<main>[^\s]+)ms"
)
FREEZE_UDP = re.compile(
    r"\[FREEZE_DIAG\]\[UDP\]\s+sendGap=(?P<gap>[0-9]+(?:\.[0-9]+)?)ms\s+"
    r"color=(?P<color>RED|BLUE)\b"
)


def number(value: Any) -> float | None:
    if not isinstance(value, (int, float)) or isinstance(value, bool):
        return None
    try:
        if math.isfinite(float(value)):
            return float(value)
    except OverflowError:
        pass
    return None


def frame_times(frames: list[dict[str, Any]]) -> list[float | None]:
    """Return known recording-relative times; invalid entries become unknown.

    A non-increasing timestamp is isolated as unknown so one bad or absent
    value cannot discard timeline results for every other frame.
    """
    result: list[float | None] = []
    previous: float | None = None
    for frame in frames:
        value = number(frame.get("presentationTimeSeconds")) if isinstance(frame, dict) else None
        if value is not None and previous is not None and value <= previous:
            value = None
        result.append(value)
        if value is not None:
            previous = value
    return result


def analyze_frames(frames: list[dict[str, Any]]) -> list[dict[str, Any]]:
    times = frame_times(frames)
    intervals = [
        right - left for left, right in zip(times, times[1:])
        if left is not None and right is not None and right > left
    ]
    tail = statistics.median(intervals) if intervals else 0.0
    ends: list[float | None] = []
    for index, value in enumerate(times):
        next_value = times[index + 1] if index + 1 < len(times) else None
        if value is None:
            ends.append(None)
        elif next_value is not None and next_value > value:
            ends.append(next_value)
        else:
            ends.append(value + tail)
    events: list[dict[str, Any]] = []

    def add(color: str, kind: str, start: int, end: int, **details: Any) -> None:
        frame = frames[start]
        start_time = times[start]
        end_time = ends[end]
        if start_time is None or end_time is None:
            return
        detection = frame.get(color)
        if not isinstance(detection, dict):
            detection = {}
        all_diagnostics = frame.get("candidateDiagnostics")
        diagnostics = all_diagnostics.get(color) if isinstance(all_diagnostics, dict) else None
        if not isinstance(diagnostics, dict):
            diagnostics = {}
        candidate = selected_candidate(frame, color) or {}
        endpoint = endpoint_tuple(frame, color)
        events.append({
            "color": color.upper(), "eventType": kind,
            "frameID": frame.get("frameID"), "endFrameID": frames[end].get("frameID"),
            "frameCount": end - start + 1,
            "startTimeSeconds": start_time, "endTimeSeconds": end_time,
            "durationMs": (end_time - start_time) * 1000,
            "detected": detection_status(frame, color),
            "selectedCandidateType": diagnostics.get("selectedCandidateType") or candidate.get("sourceType"),
            "endpoint": list(endpoint) if endpoint else None,
            "maskPixelCount": diagnostics.get("maskPixelCount"),
            "morphologyPixelCount": diagnostics.get("morphologyPixelCount"),
            "candidateCount": diagnostics.get("totalCandidateCount"),
            "eligibleCount": diagnostics.get("eligibleCandidateCount"),
            **details,
        })

    for color in COLORS:
        statuses = [detection_status(frame, color) for frame in frames]
        endpoints = [endpoint_tuple(frame, color) for frame in frames]
        dropout_flags = [
            False if status is None or times[index] is None else not status
            for index, status in enumerate(statuses)
        ]
        for start, end in contiguous_runs(dropout_flags):
            add(color, "detected_false", start, end)

        identical_start: int | None = None
        for index in range(len(frames) + 1):
            same_as_previous = (
                index < len(frames) and index > 0
                and endpoints[index] is not None
                and times[index] is not None and times[index - 1] is not None
                and endpoints[index] == endpoints[index - 1]
            )
            if index == 0:
                identical_start = 0 if endpoints and endpoints[0] is not None and times[0] is not None else None
            elif same_as_previous:
                continue
            else:
                if identical_start is not None and index - identical_start >= 4:
                    add(color, "identical_endpoint", identical_start, index - 1)
                identical_start = (
                    index if index < len(frames) and endpoints[index] is not None
                    and times[index] is not None else None
                )

        for index in range(1, len(frames)):
            if endpoints[index] is None or endpoints[index - 1] is None \
                    or times[index] is None or times[index - 1] is None:
                continue
            jump = endpoint_jump(endpoints[index - 1], endpoints[index])
            if jump >= 180:
                add(color, "endpoint_jump_180", index, index, jumpPx=jump)
            if jump >= 260:
                add(color, "endpoint_jump_260", index, index, jumpPx=jump)

        flags: dict[str, list[bool]] = {
            key: [] for key in (
                "candidate_zero", "candidate_present_eligible_zero",
                "mask_present_morphology_zero", "selected_core_line",
                "raw_robust_divergence",
            )
        }
        for frame_index, frame in enumerate(frames):
            all_diagnostics = frame.get("candidateDiagnostics")
            diagnostics = all_diagnostics.get(color) if isinstance(all_diagnostics, dict) else None
            if not isinstance(diagnostics, dict):
                diagnostics = {}
            time_known = times[frame_index] is not None
            total = number(diagnostics.get("totalCandidateCount"))
            eligible = number(diagnostics.get("eligibleCandidateCount"))
            mask = number(diagnostics.get("maskPixelCount"))
            morphology = number(diagnostics.get("morphologyPixelCount"))
            candidate = selected_candidate(frame, color) or {}
            source = diagnostics.get("selectedCandidateType") or candidate.get("sourceType")
            raw = number(candidate.get("rawPCASpan"))
            robust = number(candidate.get("robustMainIntervalLength"))
            flags["candidate_zero"].append(time_known and total == 0)
            flags["candidate_present_eligible_zero"].append(
                time_known and total is not None and total > 0 and eligible == 0
            )
            flags["mask_present_morphology_zero"].append(
                time_known and mask is not None and mask > 0 and morphology == 0
            )
            flags["selected_core_line"].append(
                time_known and isinstance(source, str) and source.startswith("core-line")
            )
            flags["raw_robust_divergence"].append(
                time_known and raw is not None and robust is not None
                and raw >= robust * RAW_ROBUST_RATIO
                and raw - robust >= RAW_ROBUST_DELTA_PX
            )
        for kind, values in flags.items():
            for start, end in contiguous_runs(values):
                add(color, kind, start, end)

    return sorted(events, key=lambda item: (item["startTimeSeconds"], item["endTimeSeconds"], item["color"], item["eventType"]))


def incidents(events: list[dict[str, Any]]) -> list[dict[str, Any]]:
    result: list[dict[str, Any]] = []
    for event in events:
        if result and event["startTimeSeconds"] < result[-1]["endTimeSeconds"]:
            incident = result[-1]
            incident["endTimeSeconds"] = max(incident["endTimeSeconds"], event["endTimeSeconds"])
            incident["durationMs"] = (incident["endTimeSeconds"] - incident["startTimeSeconds"]) * 1000
            incident["events"].append(event)
        else:
            result.append({
                "number": len(result) + 1,
                "startTimeSeconds": event["startTimeSeconds"],
                "endTimeSeconds": event["endTimeSeconds"],
                "durationMs": event["durationMs"], "events": [event],
            })
    return result


def read_unity_gaps(path: Path) -> list[dict[str, Any]]:
    gaps = []
    for line_number, line in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
        match = UNITY_GAP.search(line)
        if not match:
            continue
        gap = float(match.group(3))
        end = float(match.group(5))
        if math.isfinite(gap) and math.isfinite(end) and gap >= 0:
            gaps.append({
                "eventType": "unity_" + match.group(1).lower() + "_gap",
                "color": match.group(2), "gapMs": gap,
                "unityStartSeconds": end - gap / 1000,
                "unityEndSeconds": end, "line": line_number,
            })
    return gaps


def _summary_triplet(value: str) -> dict[str, float | None]:
    parts = value.split("/")
    if len(parts) != 3:
        return {"p50_ms": None, "p95_ms": None, "max_ms": None}
    parsed = [float(part) if part != "-" else None for part in parts]
    return dict(zip(("p50_ms", "p95_ms", "max_ms"), parsed))


def read_freeze_diagnostics(path: Path) -> dict[str, Any]:
    """Summarize existing iOS FREEZE_DIAG summary windows and UDP events."""
    summary_windows: list[dict[str, Any]] = []
    udp_gaps: list[dict[str, Any]] = []
    for line_number, line in enumerate(path.read_text(encoding="utf-8", errors="replace").splitlines(), 1):
        match = FREEZE_SUMMARY.search(line)
        if match:
            summary_windows.append({
                "line": line_number,
                "cameraGap": _summary_triplet(match.group("camera")),
                "queueWait": _summary_triplet(match.group("queue")),
                "detection": _summary_triplet(match.group("detection")),
                "mainDelay": _summary_triplet(match.group("main")),
            })
        udp = FREEZE_UDP.search(line)
        if udp:
            udp_gaps.append({
                "line": line_number, "color": udp.group("color"), "sendGapMs": float(udp.group("gap")),
            })

    # The app logs window summaries rather than every timing sample. Keep those
    # values explicit: p50 is the median of window p50s; p95 and max use the
    # maximum reported window values, which is useful for spotting bad windows
    # but is not a recomputed percentile over all frames.
    aggregate: dict[str, Any] = {}
    for metric in ("cameraGap", "queueWait", "detection", "mainDelay"):
        windows = [window[metric] for window in summary_windows]
        fields = {}
        for key in ("p50_ms", "p95_ms", "max_ms"):
            values = [window[key] for window in windows if window[key] is not None]
            if not values:
                fields[key] = None
            elif key == "p50_ms":
                fields[key] = statistics.median(values)
            else:
                fields[key] = max(values)
        aggregate[metric] = fields

    udp_by_color = {
        color: {
            "event_count": sum(item["color"] == color for item in udp_gaps),
            "max_send_gap_ms": max(
                (item["sendGapMs"] for item in udp_gaps if item["color"] == color), default=None
            ),
        }
        for color in ("RED", "BLUE")
    }
    return {
        "file": str(path), "summary_window_count": len(summary_windows),
        "summary_windows": summary_windows, "window_aggregate": aggregate,
        "udp_send_gap_event_count": len(udp_gaps), "udp_send_gap_by_color": udp_by_color,
        "aggregation_note": (
            "p50_ms is the median of logged window p50 values; p95_ms and max_ms are maxima "
            "of logged window summaries, not recomputed from raw frame samples"
        ),
    }


def analyze_file(path: Path, unity_log: Path | None = None) -> dict[str, Any]:
    validated = load_metadata_file(path)
    document = validated.document
    frames = validated.frames
    events = analyze_frames(frames)
    result = {
        "file": str(path), "sessionID": document.get("sessionID"),
        "events": events, "incidents": incidents(events),
    }
    if unity_log is not None:
        # iPhone presentationTimeSeconds is recording-relative; Unity uses its
        # own Stopwatch epoch. Neither format provides a shared clock anchor.
        result["unityCorrelation"] = "correlation unavailable"
        result["unityGaps"] = read_unity_gaps(unity_log)
    return result


def print_analysis(result: dict[str, Any]) -> None:
    print(f"session: {result['sessionID'] or Path(result['file']).stem}")
    for event in result["events"]:
        print(
            f"{event['startTimeSeconds']:.3f}s - {event['endTimeSeconds']:.3f}s "
            f"({event['durationMs']:.1f}ms) {event['color']} {event['eventType']} "
            f"frameID={event['frameID']}..{event['endFrameID']} "
            f"detected={event['detected']} selectedCandidateType={event['selectedCandidateType']} "
            f"endpoint={event['endpoint']} maskPixelCount={event['maskPixelCount']} "
            f"morphologyPixelCount={event['morphologyPixelCount']} "
            f"candidateCount={event['candidateCount']} eligibleCount={event['eligibleCount']}"
            + (f" jump={event['jumpPx']:.1f}px" if "jumpPx" in event else "")
        )
    print("\nINCIDENTS")
    for incident in result["incidents"]:
        print(f"INCIDENT {incident['number']}")
        print(f"time: {incident['startTimeSeconds']:.3f}s - {incident['endTimeSeconds']:.3f}s")
        print(f"duration: {incident['durationMs']:.1f}ms")
        for color in ("BLUE", "RED"):
            color_events = [event for event in incident["events"] if event["color"] == color]
            if color_events:
                print(f"{color}:")
                for event in color_events:
                    extra = f" {event['frameCount']}f" if event["eventType"] == "identical_endpoint" else ""
                    extra += f" {event['jumpPx']:.1f}px" if "jumpPx" in event else ""
                    print(f"  {event['eventType']}{extra} frameID={event['frameID']}..{event['endFrameID']} "
                          f"selected={event['selectedCandidateType']} "
                          f"candidate count={event['candidateCount']} eligible={event['eligibleCount']}")
    if "unityCorrelation" in result:
        print(f"\nUnity: {result['unityCorrelation']}")
        for gap in result["unityGaps"]:
            print(f"  {gap['color']} {gap['eventType']} {gap['gapMs']:.1f}ms "
                  f"Unity clock {gap['unityStartSeconds']:.6f}s - {gap['unityEndSeconds']:.6f}s")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("metadata", type=Path)
    parser.add_argument("--unity-log", type=Path)
    parser.add_argument("--json", nargs="?", const="-", metavar="PATH",
                        help="write JSON to PATH, or stdout when PATH is omitted")
    args = parser.parse_args()
    try:
        result = analyze_file(args.metadata, args.unity_log)
        if args.json == "-":
            print(json.dumps(result, ensure_ascii=False, indent=2))
        else:
            print_analysis(result)
            if args.json:
                Path(args.json).write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    except (OSError, ValueError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
