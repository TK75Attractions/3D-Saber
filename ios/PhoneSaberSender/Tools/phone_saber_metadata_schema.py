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

SHADOW_R7E = object_field({
    **{name: scalar("number") for name in (
        "density", "fallbackDensity", "d240", "clippedWhiteRatio", "meanColorPurity",
        "clippedWhiteMargin", "thickBodyMargin", "saturatedBodyDensityMargin",
        "saturatedBodyPurityMargin")},
    **{name: scalar("boolean") for name in (
        "applied", "usedFallbackDensity", "ruleSatisfied", "shadowR7eEligible")},
})

# Debug Recording diagnostic path only; absent in older bundles.
EMITTER_DIAGNOSTICS = object_field({
    **{name: scalar("number") for name in (
        "emitterScore", "emitterScoreThreshold", "emitterScoreMargin", "peakTerm", "meanTerm",
        "highValueTerm", "purityTerm", "clippedWhiteTerm", "majorLengthSamples",
        "bladeLengthSupport", "localContrast", "emitterTexture", "brightnessVariation",
        "coreSupport", "longitudinalCoreCoverage", "longitudinalHighCoverage",
        "meanMaxChannel", "meanMinChannel", "nearWhiteFraction")},
    "meanSecondChannel": scalar("number", nullable=True),
    "brightSecondChannelFraction": scalar("number", nullable=True),
    "maxSecondChannel": scalar("integer", nullable=True),
    "sampleCount": scalar("integer"),
    "colorSampleCount": scalar("integer"),
    **{name: scalar("boolean") for name in (
        "hasEmitterCore", "coreByHighValueRatio", "coreByPeakAndMean", "coreByClippedWhite",
        "baseEligible", "compactRedGate")},
    "shadowR7e": SHADOW_R7E,
}, nullable=True)

FRAME_CAMERA = object_field({
    "source": scalar("string"),
    **{name: scalar("number", nullable=True) for name in (
        "iso", "exposureDurationSeconds", "exposureBiasEV", "brightnessValue", "fNumber",
        "exposureTargetBias", "exposureTargetOffset", "deviceSampleAgeSeconds")},
    "whiteBalanceGains": array_field(scalar("number"), nullable=True),
}, nullable=True)

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
    "centroid": array_field(scalar("number")),
    "bbox": array_field(scalar("integer")),
    "endpointPipeline": object_field({}),
    "compoundRejections": array_field(object_field({})),
    "emitterDiagnostics": EMITTER_DIAGNOSTICS,
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
    "secondBestScore": scalar("number", nullable=True),
    "scoreMargin": scalar("number", nullable=True),
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
    "processingTimeSeconds": scalar("number", nullable=True),
    "motionEventIndex": scalar("integer", nullable=True),
    "tracking": object_field({"red": object_field({}), "blue": object_field({})}),
    "camera": FRAME_CAMERA,
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

# Opt-in camera exposure experiment chosen at Debug Recording Start (root and
# summary.json `cameraExposureExperiment`). Absent in older recordings, which
# always used plain auto exposure. Settings and statuses mirror the Swift
# CameraExposureExperiment / CameraExposureExperimentState.Status raw values.
CAMERA_EXPOSURE_EXPERIMENT_SETTINGS = ("auto", "maxShutter1_100", "maxShutter1_120", "maxShutter1_240")
CAMERA_EXPOSURE_EXPERIMENT_STATUSES = ("auto", "autoRestored", "applied", "clamped", "notNeeded",
                                       "unsupported", "failed", "pending")
CAMERA_EXPOSURE_EXPERIMENT = object_field({
    "formatVersion": scalar("integer"),
    "setting": scalar("string", required=True),
    "status": scalar("string", required=True),
    "capActive": scalar("boolean"),
    **{name: scalar("number") for name in (
        "requestedMaxExposureSeconds", "appliedMaxExposureSeconds", "defaultMaxExposureSeconds",
        "formatMinExposureSeconds", "formatMaxExposureSeconds", "observedMaxExposureSeconds")},
    "detail": scalar("string"),
})


def camera_exposure_experiment_errors(value: Any) -> list[str]:
    """Problems with a `cameraExposureExperiment` object (empty list = valid)."""
    if not isinstance(value, dict):
        return ["cameraExposureExperiment must be an object"]
    errors: list[str] = []
    if value.get("setting") not in CAMERA_EXPOSURE_EXPERIMENT_SETTINGS:
        errors.append("unknown cameraExposureExperiment.setting")
    if value.get("status") not in CAMERA_EXPOSURE_EXPERIMENT_STATUSES:
        errors.append("unknown cameraExposureExperiment.status")
    for key, field in (CAMERA_EXPOSURE_EXPERIMENT.fields or {}).items():
        if key in value and key not in ("setting", "status") and not _matches_kind(value[key], field.kind):
            errors.append(f"cameraExposureExperiment.{key} must be {field.kind}")
    unknown = set(value) - set(CAMERA_EXPOSURE_EXPERIMENT.fields or {})
    if unknown:
        errors.append("unknown cameraExposureExperiment keys: " + ", ".join(sorted(unknown)))
    return errors


SEGMENT_LABELS = ("unlabeled", "sabersVisible", "noSaber", "noSaberCovered")
# Labels under which every detection of a color is a false positive.
FALSE_POSITIVE_SEGMENT_LABELS = ("noSaber", "noSaberCovered")

SEGMENT_MARKER = object_field({
    "frameID": scalar("integer", required=True),
    "timestamp": scalar("number", required=True),
    "label": scalar("string", required=True),
}, required=True)

SEGMENT_COLOR_COUNTS = object_field({
    "detectedFrames": scalar("integer", required=True),
    "measuredFrames": scalar("integer", required=True),
})

SEGMENT_SUMMARY = object_field({
    "formatVersion": scalar("integer"),
    "totalFrames": scalar("integer", required=True),
    "byLabel": object_field({
        label: object_field({
            "frames": scalar("integer", required=True),
            "red": SEGMENT_COLOR_COUNTS,
            "blue": SEGMENT_COLOR_COUNTS,
        }) for label in SEGMENT_LABELS
    }, required=True),
    "falsePositiveFrames": object_field({
        label: object_field({"red": scalar("integer"), "blue": scalar("integer")})
        for label in FALSE_POSITIVE_SEGMENT_LABELS
    }),
    "markerCount": scalar("integer"),
    "droppedMarkerCount": scalar("integer"),
    "definition": scalar("string"),
    # summary.json only: a copy of the root segmentMarkers.
    "markers": array_field(SEGMENT_MARKER),
})

ROOT = object_field({
    "sessionID": scalar("string", required=True),
    "width": scalar("integer", required=True),
    "height": scalar("integer", required=True),
    "frames": array_field(FRAME, required=True),
    # Camera state was added by a diagnostic-capable producer. Its absence does
    # not invalidate older sessions or frame-level analysis.
    "cameraSamples": array_field(CAMERA_SAMPLE),
    "motionEvents": array_field(object_field({})),
    "motionSummary": object_field({}),
    "udpTransmissions": array_field(object_field({})),
    # Diagnostic color selection, success windows and bridge dropout events.
    "activeColors": array_field(scalar("string")),
    "diagnosticWindows": object_field({}),
    "bridgeDropoutEvents": array_field(object_field({})),
    "bridgeDropoutSummary": object_field({}),
    # Operator segment labels (ground truth); absent in older recordings.
    "segmentMarkers": array_field(SEGMENT_MARKER),
    "segmentSummary": SEGMENT_SUMMARY,
    # Opt-in exposure experiment at Start; absent in older recordings (auto).
    "cameraExposureExperiment": CAMERA_EXPOSURE_EXPERIMENT,
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
    markers = normalized.get("segmentMarkers")
    if isinstance(markers, list):
        for index, marker in enumerate(markers):
            if isinstance(marker, dict) and isinstance(marker.get("label"), str) \
                    and marker["label"] not in SEGMENT_LABELS:
                warn(f"segmentMarkers[{index}].label", "unknown segment label; treated as unknown")
                marker.pop("label", None)
    normalized["frames"] = frames
    report = ValidationReport(format_version=format_version, warnings=dict(sorted(warnings.items())))
    return ValidatedMetadata(document=normalized, frames=frames, report=report)


def load_metadata_file(path: str | Path) -> ValidatedMetadata:
    with Path(path).open(encoding="utf-8") as handle:
        document = json.load(handle)
    return validate_document(document)


def _count(value: Any) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0


def segment_marker_errors(markers: Any) -> list[str]:
    """Strict check of segmentMarkers: known labels, strictly increasing frame IDs."""
    if not isinstance(markers, list):
        return ["segmentMarkers must be an array"]
    errors = []
    previous = None
    for index, marker in enumerate(markers):
        if not isinstance(marker, dict) or set(marker) != {"frameID", "timestamp", "label"} \
                or not _count(marker["frameID"]) or not _matches_kind(marker["timestamp"], "number") \
                or marker["label"] not in SEGMENT_LABELS:
            errors.append(f"segmentMarkers[{index}] is malformed")
            continue
        if previous is not None and marker["frameID"] <= previous:
            errors.append(f"segmentMarkers[{index}] frameID is not increasing")
        previous = marker["frameID"]
    return errors


def segment_summary_errors(summary: Any) -> list[str]:
    """Strict check of segmentSummary (metadata root or summary.json).

    Counts are non-negative integers, a color is never detected on more frames
    than its label has, frames add up to totalFrames, and every false-positive
    count equals the detectedFrames of its label and color.
    """
    if not isinstance(summary, dict):
        return ["segmentSummary must be an object"]
    by_label = summary.get("byLabel")
    if not isinstance(by_label, dict) or not set(by_label) <= set(SEGMENT_LABELS):
        return ["segmentSummary.byLabel is malformed"]
    errors = []
    total = 0
    for label, entry in by_label.items():
        if not isinstance(entry, dict) or not _count(entry.get("frames")) \
                or not set(entry) <= {"frames", "red", "blue"}:
            errors.append(f"segmentSummary.byLabel.{label} is malformed")
            continue
        total += entry["frames"]
        for color in ("red", "blue"):
            counts = entry.get(color)
            if counts is None:
                continue
            if not isinstance(counts, dict) or set(counts) != {"detectedFrames", "measuredFrames"} \
                    or not all(_count(value) and value <= entry["frames"] for value in counts.values()):
                errors.append(f"segmentSummary.byLabel.{label}.{color} is malformed")
    if not _count(summary.get("totalFrames")) or summary["totalFrames"] != total:
        errors.append("segmentSummary.totalFrames does not match byLabel frames")
    false_positives = summary.get("falsePositiveFrames", {})
    if not isinstance(false_positives, dict) \
            or not set(false_positives) <= set(FALSE_POSITIVE_SEGMENT_LABELS):
        errors.append("segmentSummary.falsePositiveFrames is malformed")
    else:
        for label, counts in false_positives.items():
            entry = by_label.get(label) if isinstance(by_label.get(label), dict) else {}
            if not isinstance(counts, dict) or not set(counts) <= {"red", "blue"} or any(
                    not _count(value)
                    or value != (entry.get(color) if isinstance(entry.get(color), dict) else {})
                    .get("detectedFrames", 0)
                    for color, value in counts.items()):
                errors.append(f"segmentSummary.falsePositiveFrames.{label} is inconsistent")
    for key in ("formatVersion", "markerCount", "droppedMarkerCount"):
        if key in summary and not _count(summary[key]):
            errors.append(f"segmentSummary.{key} is malformed")
    if "definition" in summary and (not isinstance(summary["definition"], str)
                                    or len(summary["definition"]) > 1000):
        errors.append("segmentSummary.definition is malformed")
    if "markers" in summary:
        errors.extend(segment_marker_errors(summary["markers"]))
        if isinstance(summary["markers"], list) and "markerCount" in summary \
                and summary["markerCount"] != len(summary["markers"]):
            errors.append("segmentSummary.markerCount does not match markers")
    unknown = set(summary) - {"formatVersion", "totalFrames", "byLabel", "falsePositiveFrames",
                              "markerCount", "droppedMarkerCount", "definition", "markers"}
    if unknown:
        errors.append(f"segmentSummary has unknown keys: {sorted(unknown)}")
    return errors
