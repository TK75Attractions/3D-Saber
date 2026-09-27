"""Shared, dependency-free validation for PhoneSaber Debug Recording metadata."""

from __future__ import annotations

import json
import math
import re
from collections import Counter
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Mapping


CURRENT_FORMAT_VERSION = 1
LEGACY_FORMAT_VERSION = 0


@dataclass(frozen=True)
class Field:
    kind: str
    required: bool = False
    nullable: bool = False
    fields: Mapping[str, "Field"] | None = None
    item: "Field | None" = None


def scalar(kind: str, *, required: bool = False, nullable: bool = False) -> Field:
    return Field(kind=kind, required=required, nullable=nullable)


def object_field(fields: Mapping[str, Field], *, required: bool = False,
                 nullable: bool = False) -> Field:
    return Field(kind="object", required=required, nullable=nullable, fields=fields)


def array_field(item: Field, *, required: bool = False, nullable: bool = False) -> Field:
    return Field(kind="array", required=required, nullable=nullable, item=item)


POINT = object_field({"x": scalar("integer", required=True), "y": scalar("integer", required=True)}, required=True)
ENDPOINTS = object_field({"first": POINT, "second": POINT}, required=True)

SCORE_BREAKDOWN = object_field({
    name: scalar("number", required=True)
    for name in (
        "proposalPenalty", "radiance", "length", "aspect", "extent", "widthConsistency",
        "area", "peakBrightness", "meanBrightness", "highBrightnessRatio", "colorPurity",
        "localContrast", "emitterTexture", "clippedWhite", "longitudinalHighCoverage",
        "coreSupport", "longitudinalCoreCoverage", "total",
    )
}, required=True)

CANDIDATE = object_field({
    "index": scalar("integer", required=True),
    "selected": scalar("boolean", required=True),
    "sourceType": scalar("string", required=True),
    "eligible": scalar("boolean", required=True),
    "finalScore": scalar("number", required=True),
    "scoreBreakdown": SCORE_BREAKDOWN,
    "rawPCAEndpoints": ENDPOINTS,
    "rawPCASpan": scalar("number", required=True),
    "robustMainIntervalEndpoints": object_field(
        {"first": POINT, "second": POINT}, nullable=True
    ),
    "robustMainIntervalLength": scalar("number", required=True),
    "finalOutputEndpoints": ENDPOINTS,
    "continuity": scalar("number", required=True),
    "density": scalar("number", required=True),
    "maxGap": scalar("integer", required=True),
    "componentArea": scalar("integer", required=True),
    "pointCount": scalar("integer", required=True),
    "usedPointLEDFallback": scalar("boolean", required=True),
}, required=True)

COLOR_CANDIDATES = object_field({
    "totalCandidateCount": scalar("integer", required=True),
    "eligibleCandidateCount": scalar("integer", required=True),
    "maskPixelCount": scalar("integer", required=True),
    "morphologyPixelCount": scalar("integer", required=True),
    "connectedComponentCount": scalar("integer", required=True),
    "selectedCandidateIndex": scalar("integer", nullable=True),
    "selectedCandidateType": scalar("string", nullable=True),
    "selectedCandidateFinalScore": scalar("number", nullable=True),
    "selectedCandidateScoreBreakdown": object_field(
        SCORE_BREAKDOWN.fields or {}, nullable=True
    ),
    "selectedCandidate": object_field(CANDIDATE.fields or {}, nullable=True),
    "topCandidates": array_field(CANDIDATE, required=True),
}, required=True)

CANDIDATE_DIAGNOSTICS = object_field({
    "red": COLOR_CANDIDATES,
    "blue": COLOR_CANDIDATES,
}, required=True)

DETECTION = object_field({
    "detected": scalar("boolean", required=True),
    "predicted": scalar("boolean", required=True),
    "x1": scalar("integer", nullable=True),
    "y1": scalar("integer", nullable=True),
    "x2": scalar("integer", nullable=True),
    "y2": scalar("integer", nullable=True),
}, required=True)

FRAME = object_field({
    "frameID": scalar("integer", required=True),
    "presentationTimeSeconds": scalar("number", required=True),
    "red": DETECTION,
    "blue": DETECTION,
    "redDetectionSucceeded": scalar("boolean", required=True),
    "blueDetectionSucceeded": scalar("boolean", required=True),
    "candidateDiagnostics": object_field(CANDIDATE_DIAGNOSTICS.fields or {}, nullable=True),
    "forensicCaptured": scalar("boolean", required=True),
    "forensicFileName": scalar("string", nullable=True),
    "manualCaptured": scalar("boolean", required=True),
    "manualFileName": scalar("string", nullable=True),
    "blueDropoutRole": scalar("string", nullable=True),
    "blueDropoutFileName": scalar("string", nullable=True),
    "redDropoutRole": scalar("string", nullable=True),
    "redDropoutFileName": scalar("string", nullable=True),
}, required=True)

CAMERA_SAMPLE = object_field({
    "frameID": scalar("integer", nullable=True),
    "presentationTimeSeconds": scalar("number", nullable=True),
    "exposureDurationMs": scalar("number", required=True),
    "iso": scalar("number", required=True),
    "whiteBalanceRedGain": scalar("number", required=True),
    "whiteBalanceGreenGain": scalar("number", required=True),
    "whiteBalanceBlueGain": scalar("number", required=True),
    "exposureMode": scalar("string", required=True),
    "whiteBalanceMode": scalar("string", required=True),
    "focusMode": scalar("string", required=True),
    "lensPosition": scalar("number", required=True),
    "activeFormat": scalar("string", required=True),
    "activeFormatFPSRanges": scalar("string", required=True),
    "activeMinFPS": scalar("number", nullable=True),
    "activeMaxFPS": scalar("number", nullable=True),
}, required=True)

ROOT = object_field({
    "sessionID": scalar("string", required=True),
    "width": scalar("integer", required=True),
    "height": scalar("integer", required=True),
    "frames": array_field(FRAME, required=True),
    # Camera state was added by a diagnostic-capable producer. Its absence does
    # not invalidate older sessions or frame-level analysis.
    "cameraSamples": array_field(CAMERA_SAMPLE),
}, required=True)


@dataclass(frozen=True)
class ValidationReport:
    format_version: int | None
    warnings: Mapping[str, int]

    @property
    def warning_count(self) -> int:
        return sum(self.warnings.values())


@dataclass(frozen=True)
class ValidatedMetadata:
    document: dict[str, Any]
    frames: list[dict[str, Any]]
    report: ValidationReport


def _normalized_path(path: str) -> str:
    return re.sub(r"\[\d+\]", "[*]", path)


def _matches_kind(value: Any, kind: str) -> bool:
    if kind == "object":
        return isinstance(value, dict)
    if kind == "array":
        return isinstance(value, list)
    if kind == "string":
        return isinstance(value, str)
    if kind == "boolean":
        return isinstance(value, bool)
    if kind == "integer":
        return isinstance(value, int) and not isinstance(value, bool)
    if kind == "number":
        if not isinstance(value, (int, float)) or isinstance(value, bool):
            return False
        try:
            return math.isfinite(float(value))
        except OverflowError:
            return False
    raise AssertionError(f"unknown schema kind: {kind}")


def validate_document(value: Any) -> ValidatedMetadata:
    """Validate a parsed document and replace malformed fields with unknowns.

    Unversioned object files and legacy top-level frame arrays are accepted as
    version 0. Missing or mistyped fields produce warnings and are omitted from
    the normalized document; only an unusable root/frame array raises ValueError.
    """
    if isinstance(value, list):
        source: dict[str, Any] = {"frames": value}
        format_version: int | None = LEGACY_FORMAT_VERSION
        explicit_version = False
    elif isinstance(value, dict):
        source = dict(value)
        explicit_version = "formatVersion" in source
        format_version = None
        raw_version = source.get("formatVersion")
        if not explicit_version:
            format_version = LEGACY_FORMAT_VERSION
        elif _matches_kind(raw_version, "integer") and raw_version >= 0:
            format_version = raw_version
        else:
            source.pop("formatVersion", None)
    else:
        raise ValueError("metadata root must be an object or a legacy frames array")

    if "frames" not in source or not isinstance(source["frames"], list):
        raise ValueError("metadata must contain a frames array")

    warnings: Counter[str] = Counter()

    def warn(path: str, reason: str) -> None:
        warnings[f"{_normalized_path(path)}: {reason}"] += 1

    if not explicit_version:
        warn("formatVersion", "missing; treated as legacy version 0")
    elif format_version is None:
        warn("formatVersion", "invalid type or value; version is unknown")
    elif format_version not in (LEGACY_FORMAT_VERSION, CURRENT_FORMAT_VERSION):
        warn("formatVersion", f"unsupported version {format_version}; known fields parsed")

    def validate_object(obj: Any, shape: Field, path: str) -> dict[str, Any]:
        if not isinstance(obj, dict):
            warn(path, "expected object; treated as unknown object")
            return {}
        result = dict(obj)
        for key, field in (shape.fields or {}).items():
            field_path = f"{path}.{key}" if path else key
            if key not in obj:
                if field.required:
                    warn(field_path, "required by version 1 writer but absent; treated as unknown")
                continue
            item = obj[key]
            if item is None and field.nullable:
                continue
            if not _matches_kind(item, field.kind):
                warn(field_path, f"expected {field.kind}; treated as unknown")
                result.pop(key, None)
                continue
            if field.kind == "object":
                result[key] = validate_object(item, field, field_path)
            elif field.kind == "array":
                normalized_items = []
                for index, array_item in enumerate(item):
                    item_path = f"{field_path}[{index}]"
                    if field.item and field.item.kind == "object":
                        normalized_items.append(validate_object(array_item, field.item, item_path))
                    elif field.item and not _matches_kind(array_item, field.item.kind):
                        warn(item_path, f"expected {field.item.kind}; treated as unknown")
                        normalized_items.append({} if field.item.kind == "object" else None)
                    else:
                        normalized_items.append(array_item)
                result[key] = normalized_items
        return result

    normalized = validate_object(source, ROOT, "")
    frames = normalized.get("frames")
    if not isinstance(frames, list):
        # Checked above; this is defensive against accidental schema changes.
        raise ValueError("metadata must contain a frames array")
    for index, frame in enumerate(frames):
        for color in ("red", "blue"):
            detection = frame.get(color)
            if not isinstance(detection, dict):
                continue
            coordinate_names = ("x1", "y1", "x2", "y2")
            present = [name in detection for name in coordinate_names]
            if any(present) and not all(present):
                warn(
                    f"frames[{index}].{color}",
                    "partial endpoint coordinates; all coordinates treated as unknown",
                )
                for name in coordinate_names:
                    detection.pop(name, None)
    normalized["frames"] = frames
    report = ValidationReport(format_version=format_version, warnings=dict(sorted(warnings.items())))
    return ValidatedMetadata(document=normalized, frames=frames, report=report)


def load_metadata_file(path: str | Path) -> ValidatedMetadata:
    with Path(path).open(encoding="utf-8") as handle:
        document = json.load(handle)
    return validate_document(document)
