#!/usr/bin/env python3
"""Per-segment detection rates from operator segment labels (ground truth).

Read-only and offline. During a Debug Recording the operator marks intervals as
``sabersVisible`` (lit sabers in view), ``noSaber`` (no saber / sabers off,
background only), ``noSaberCovered`` (background only, red background objects
covered) or leaves them ``unlabeled``. This tool prints, per label, the frame
count and per-color detection rates. A detection while the label is ``noSaber``
or ``noSaberCovered`` is a false positive, so its rate there is the false-positive
rate; comparing ``noSaber`` with ``noSaberCovered`` shows whether covering the
red background objects removed the RED false positives.

Input:

* a triage bundle directory or its ``summary.json``: uses ``segmentSummary``
  (whole-session counts written by the recorder);
* a full ``*_metadata.json``: recomputes the counts from ``frames`` and
  ``segmentMarkers`` (frames before the first marker are unlabeled) and checks
  them against the recorded ``segmentSummary``. Older recordings without markers
  count every frame as unlabeled.

``detectedFrames`` is the fresh detector output including prediction;
``measuredFrames`` excludes predicted frames. Rates are fractions of the
label's frames. Exit status: 0 success, 2 unusable input.
"""
from __future__ import annotations

import argparse
import bisect
import json
import sys
from pathlib import Path
from typing import Any

from phone_saber_metadata_schema import (
    FALSE_POSITIVE_SEGMENT_LABELS,
    SEGMENT_LABELS,
    segment_marker_errors,
    segment_summary_errors,
    validate_document,
)

COLORS = ("red", "blue")


class SegmentInputError(ValueError):
    pass


def _empty_counts() -> dict[str, dict[str, Any]]:
    return {label: {"frames": 0, **{color: {"detectedFrames": 0, "measuredFrames": 0} for color in COLORS}}
            for label in SEGMENT_LABELS}


def counts_from_metadata(document: dict[str, Any]) -> tuple[dict[str, Any], list[dict[str, Any]], list[str]]:
    """Recompute per-label counts from frames and segmentMarkers."""
    warnings: list[str] = []
    markers = document.get("segmentMarkers")
    if markers is None:
        markers = []
        warnings.append("no segmentMarkers (recorded before segment labels): every frame is unlabeled")
    errors = segment_marker_errors(markers)
    if errors:
        raise SegmentInputError("; ".join(errors[:3]))
    marker_ids = [marker["frameID"] for marker in markers]
    by_label = _empty_counts()
    for frame in document.get("frames", []):
        frame_id = frame.get("frameID")
        if not isinstance(frame_id, int) or isinstance(frame_id, bool):
            warnings.append("frame without an integer frameID skipped")
            continue
        position = bisect.bisect_right(marker_ids, frame_id) - 1
        label = markers[position]["label"] if position >= 0 else "unlabeled"
        entry = by_label[label]
        entry["frames"] += 1
        for color in COLORS:
            detection = frame.get(color)
            if isinstance(detection, dict) and detection.get("detected") is True:
                entry[color]["detectedFrames"] += 1
            if frame.get(f"{color}DetectionSucceeded") is True:
                entry[color]["measuredFrames"] += 1
    recorded = document.get("segmentSummary")
    if isinstance(recorded, dict):
        if (recorded.get("droppedMarkerCount") or 0) > 0:
            warnings.append(f"{recorded['droppedMarkerCount']} label changes exceeded the marker limit; "
                            "recomputed labels after the last marker are approximate")
        recorded_by_label = recorded.get("byLabel")
        if isinstance(recorded_by_label, dict) and any(
                recorded_by_label.get(label) != by_label[label] for label in SEGMENT_LABELS):
            warnings.append("recomputed counts differ from the recorded segmentSummary")
    return by_label, list(markers), warnings


def counts_from_summary(summary: dict[str, Any]) -> tuple[dict[str, Any], list[dict[str, Any]], list[str]]:
    if "segmentSummary" not in summary:
        raise SegmentInputError("summary.json has no segmentSummary (recorded before segment labels)")
    segment = summary["segmentSummary"]
    errors = segment_summary_errors(segment)
    if errors:
        raise SegmentInputError("; ".join(errors[:3]))
    by_label = _empty_counts()
    for label, entry in segment["byLabel"].items():
        by_label[label]["frames"] = entry["frames"]
        for color in COLORS:
            if isinstance(entry.get(color), dict):
                by_label[label][color] = dict(entry[color])
    warnings = []
    if segment.get("droppedMarkerCount"):
        warnings.append(f"{segment['droppedMarkerCount']} label changes exceeded the marker limit "
                        "(counts stay exact; marker list is incomplete)")
    return by_label, list(segment.get("markers", [])), warnings


def _rate(count: int, frames: int) -> float | None:
    return round(count / frames, 6) if frames else None


def analyze(path: Path) -> dict[str, Any]:
    """Load a bundle, summary.json or metadata.json and return the segment report."""
    if path.is_dir():
        path = path / "summary.json"
    try:
        document = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise SegmentInputError(f"cannot read {path}: {exc}") from exc
    if isinstance(document, list) or (isinstance(document, dict) and "frames" in document):
        try:
            validated = validate_document(document)
        except ValueError as exc:
            raise SegmentInputError(str(exc)) from exc
        by_label, markers, warnings = counts_from_metadata(validated.document)
        source = "metadata"
        session_id = validated.document.get("sessionID")
    elif isinstance(document, dict):
        by_label, markers, warnings = counts_from_summary(document)
        source = "summary"
        session_id = document.get("sessionID")
    else:
        raise SegmentInputError("input is neither a metadata document nor a triage summary")
    labels = {}
    for label in SEGMENT_LABELS:
        entry = by_label[label]
        labels[label] = {"frames": entry["frames"], **{
            color: {**entry[color],
                    "detectionRate": _rate(entry[color]["detectedFrames"], entry["frames"]),
                    "measuredRate": _rate(entry[color]["measuredFrames"], entry["frames"])}
            for color in COLORS}}
    false_positive = {label: {color: {"frames": labels[label]["frames"],
                                      "falsePositiveFrames": labels[label][color]["detectedFrames"],
                                      "falsePositiveRate": labels[label][color]["detectionRate"]}
                              for color in COLORS}
                      for label in FALSE_POSITIVE_SEGMENT_LABELS}
    return {"source": source, "path": str(path), "sessionID": session_id,
            "totalFrames": sum(entry["frames"] for entry in labels.values()),
            "labels": labels, "falsePositive": false_positive,
            "markers": markers, "warnings": warnings}


def _percent(rate: float | None) -> str:
    return "   n/a" if rate is None else f"{rate * 100:5.1f}%"


def render_text(report: dict[str, Any]) -> str:
    lines = [f"session {report['sessionID'] or '?'}  source={report['source']}  "
             f"frames={report['totalFrames']}  markers={len(report['markers'])}",
             f"{'label':<16}{'frames':>7}  {'RED det':>8} {'rate':>6}  {'BLUE det':>8} {'rate':>6}"]
    for label, entry in report["labels"].items():
        lines.append(f"{label:<16}{entry['frames']:>7}  "
                     f"{entry['red']['detectedFrames']:>8} {_percent(entry['red']['detectionRate'])}  "
                     f"{entry['blue']['detectedFrames']:>8} {_percent(entry['blue']['detectionRate'])}")
    lines.append("False positives (any detection while no saber is in view):")
    for label, colors in report["falsePositive"].items():
        if not colors["red"]["frames"]:
            lines.append(f"  {label}: no frames")
            continue
        lines.append(f"  {label}: " + ", ".join(
            f"{color.upper()} {values['falsePositiveFrames']}/{values['frames']} "
            f"({_percent(values['falsePositiveRate']).strip()})" for color, values in colors.items()))
    if report["markers"]:
        lines.append("Segments:")
        for marker in report["markers"]:
            lines.append(f"  from frame {marker.get('frameID')} @ {marker.get('timestamp', 0):.3f}s -> "
                         f"{marker.get('label')}")
    for warning in report["warnings"]:
        lines.append(f"warning: {warning}")
    return "\n".join(lines)


def parse_args(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("input", type=Path, help="triage bundle directory, summary.json or *_metadata.json")
    parser.add_argument("--json", action="store_true", help="print the report as JSON")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        report = analyze(args.input)
    except SegmentInputError as exc:
        print(f"phone_saber_segments: {exc}", file=sys.stderr)
        return 2
    print(json.dumps(report, ensure_ascii=False) if args.json else render_text(report))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
