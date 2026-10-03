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

# Shadow verdict of the offline red purity-floor rule PF22 (evidence only,
# applied:false). Absent from bundles recorded before it existed.
SHADOW_PF22 = object_field({
    **{name: scalar("number") for name in (
        "meanColorPurity", "clippedWhiteRatio", "purityMargin", "clippedWhiteMargin")},
    **{name: scalar("boolean") for name in ("applied", "ruleSatisfied", "shadowPF22Eligible")},
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
    "shadowPF22": SHADOW_PF22,
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

# Opt-in guided Debug Recording (root and summary.json `guidedRecording`, Swift
# DebugGuidedRecordingSummary). Absent in manual recordings and in recordings
# made before guided mode existed. Per-step counts cover hold frames only.
GUIDED_OUTCOMES = ("completed", "cancelled", "incomplete")
GUIDED_STEP = object_field({
    "index": scalar("integer", required=True),
    "id": scalar("string", required=True),
    "title": scalar("string"),
    "label": scalar("string", required=True),
    "plannedLeadInSeconds": scalar("number"),
    "plannedHoldSeconds": scalar("number"),
    "plannedLosslessCaptures": scalar("integer"),
    **{name: scalar("integer") for name in ("leadInStartFrameID", "holdStartFrameID", "holdEndFrameID")},
    **{name: scalar("number") for name in ("leadInStartTimestamp", "holdStartTimestamp", "holdEndTimestamp")},
    "frames": scalar("integer", required=True),
    "red": SEGMENT_COLOR_COUNTS,
    "blue": SEGMENT_COLOR_COUNTS,
}, required=True)
GUIDED_RECORDING = object_field({
    "formatVersion": scalar("integer"),
    "scriptID": scalar("string", required=True),
    "scriptVersion": scalar("integer", required=True),
    "outcome": scalar("string", required=True),
    "plannedSeconds": scalar("number"),
    "steps": array_field(GUIDED_STEP, required=True),
    "losslessCaptures": array_field(object_field({
        "stepIndex": scalar("integer", required=True),
        "frameID": scalar("integer", required=True),
    }, required=True)),
    "definition": scalar("string"),
})


def guided_recording_errors(value: Any) -> list[str]:
    """Strict check of a `guidedRecording` object (empty list = valid)."""
    if not isinstance(value, dict):
        return ["guidedRecording must be an object"]
    errors: list[str] = []
    fields = GUIDED_RECORDING.fields or {}
    for key, field in fields.items():
        if key not in value:
            if field.required:
                errors.append(f"guidedRecording.{key} is missing")
        elif not _matches_kind(value[key], field.kind):
            errors.append(f"guidedRecording.{key} must be {field.kind}")
    unknown = set(value) - set(fields)
    if unknown:
        errors.append("unknown guidedRecording keys: " + ", ".join(sorted(unknown)))
    if "outcome" in value and value["outcome"] not in GUIDED_OUTCOMES:
        errors.append("unknown guidedRecording.outcome")
    steps = value.get("steps") if isinstance(value.get("steps"), list) else []
    step_fields = GUIDED_STEP.fields or {}
    for position, step in enumerate(steps):
        if not isinstance(step, dict):
            errors.append(f"guidedRecording.steps[{position}] is malformed")
            continue
        for key, field in step_fields.items():
            if key not in step:
                if field.required:
                    errors.append(f"guidedRecording.steps[{position}].{key} is missing")
            elif field.kind == "object":
                counts = step[key]
                if not isinstance(counts, dict) or any(
                        not _matches_kind(counts.get(name), "integer")
                        for name in ("detectedFrames", "measuredFrames")):
                    errors.append(f"guidedRecording.steps[{position}].{key} is malformed")
            elif not _matches_kind(step[key], field.kind):
                errors.append(f"guidedRecording.steps[{position}].{key} must be {field.kind}")
        if step.get("index") != position:
            errors.append(f"guidedRecording.steps[{position}].index is out of order")
        if "label" in step and step["label"] not in SEGMENT_LABELS:
            errors.append(f"guidedRecording.steps[{position}].label is unknown")
        unknown_step = set(step) - set(step_fields)
        if unknown_step:
            errors.append(f"unknown guidedRecording.steps[{position}] keys: " + ", ".join(sorted(unknown_step)))
    captures = value.get("losslessCaptures", [])
    if isinstance(captures, list):
        for position, capture in enumerate(captures):
            if not isinstance(capture, dict) or not _matches_kind(capture.get("frameID"), "integer") \
                    or not _matches_kind(capture.get("stepIndex"), "integer") \
                    or not 0 <= capture["stepIndex"] < max(len(steps), 1):
                errors.append(f"guidedRecording.losslessCaptures[{position}] is malformed")
    return errors


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
    # Whole-session shadow R7e / PF22 counters; absent in older recordings.
    # The shape is checked strictly by shadow_rule_tally_errors().
    "shadowRuleTally": object_field({}),
    # Opt-in guided recording (script, step boundaries, per-step counts).
    "guidedRecording": GUIDED_RECORDING,
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
    tally = normalized.get("shadowRuleTally")
    if tally is not None:
        tally_errors = shadow_rule_tally_errors(tally)
        if tally_errors:
            warn("shadowRuleTally", "inconsistent (" + "; ".join(tally_errors[:3]) + "); treated as unknown")
            normalized.pop("shadowRuleTally", None)
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


# --- Whole-session shadow rule tally (root and summary.json `shadowRuleTally`) ---

SHADOW_RULES = ("r7e", "pf22")
SHADOW_EXPOSURE_BUCKETS = ("le1_240", "le1_120", "le1_60", "gt1_60", "unknown")
SHADOW_RULE_COUNT_KEYS = ("winnersJudged", "winnersRejected", "eligibleJudged", "eligibleRejected",
                          "noEligibleLeft")
SHADOW_TALLY_KEYS = {"formatVersion", "applied", "rules", "colors", "totalFrames", "total", "byLabel",
                     "byExposure", "winnerRejectionSamples", "winnerRejectionsOffered", "sampleLimit",
                     "definition", "exposureExperimentSetting"}
SHADOW_SAMPLE_KEYS = {"frameID", "timestamp", "label", "color", "exposureBucket", "meanColorPurity",
                      "clippedWhiteRatio", "d240", "shadowR7eEligible", "shadowPF22Eligible",
                      # summary.json only (DebugRecordingTriageBuilder.annotatedShadowRuleTally).
                      "retainedContext", "image", "frameContextPath"}


def _shadow_color_errors(counts: Any, frames: int, path: str) -> list[str]:
    if not isinstance(counts, dict) or set(counts) != {"winners", "eligibleCandidates", "r7e", "pf22", "both"}:
        return [f"{path} is malformed"]
    winners, eligible = counts["winners"], counts["eligibleCandidates"]
    if not _count(winners) or not _count(eligible) or winners > frames or eligible < winners:
        return [f"{path} winners/eligibleCandidates are inconsistent"]
    errors = []
    for rule in SHADOW_RULES:
        rule_counts = counts[rule]
        if not isinstance(rule_counts, dict) or set(rule_counts) != set(SHADOW_RULE_COUNT_KEYS) \
                or not all(_count(rule_counts[key]) for key in SHADOW_RULE_COUNT_KEYS):
            errors.append(f"{path}.{rule} is malformed")
            continue
        c = rule_counts
        if not (c["winnersRejected"] <= c["winnersJudged"] <= winners
                and c["eligibleRejected"] <= c["eligibleJudged"] <= eligible
                and c["winnersJudged"] <= c["eligibleJudged"] and c["winnersRejected"] <= c["eligibleRejected"]
                and c["noEligibleLeft"] <= c["winnersRejected"]):
            errors.append(f"{path}.{rule} counts are inconsistent")
    both = counts["both"]
    if not isinstance(both, dict) or set(both) != {"winnersJudged", "winnersRejected"} \
            or not all(_count(value) for value in both.values()):
        errors.append(f"{path}.both is malformed")
    elif not errors and not (both["winnersRejected"] <= both["winnersJudged"] <= winners
                             and all(both["winnersRejected"] <= counts[r]["winnersRejected"] for r in SHADOW_RULES)):
        errors.append(f"{path}.both counts are inconsistent")
    return errors


def _shadow_bucket_errors(bucket: Any, colors: list[str], path: str) -> list[str]:
    if not isinstance(bucket, dict) or not _count(bucket.get("frames")) \
            or set(bucket) != {"frames", *colors}:
        return [f"{path} is malformed"]
    errors = []
    for color in colors:
        errors.extend(_shadow_color_errors(bucket[color], bucket["frames"], f"{path}.{color}"))
    return errors


def _shadow_leaves(bucket: dict, colors: list[str]) -> list[int]:
    """Every counter of a valid bucket in a fixed order (for sum checks)."""
    values = [bucket["frames"]]
    for color in colors:
        counts = bucket[color]
        values += [counts["winners"], counts["eligibleCandidates"]]
        for rule in SHADOW_RULES:
            values += [counts[rule][key] for key in SHADOW_RULE_COUNT_KEYS]
        values += [counts["both"]["winnersJudged"], counts["both"]["winnersRejected"]]
    return values


def shadow_rule_tally_errors(tally: Any) -> list[str]:
    """Strict check of shadowRuleTally (metadata root or summary.json); empty list = valid.

    Counts are non-negative integers that nest (rejected <= judged <= winners),
    byLabel and byExposure each add up to `total`, and the listed samples are
    bounded, outside noSaber / noSaberCovered and never more than were offered.
    """
    if not isinstance(tally, dict):
        return ["shadowRuleTally must be an object"]
    unknown = set(tally) - SHADOW_TALLY_KEYS
    if unknown:
        return [f"shadowRuleTally has unknown keys: {sorted(unknown)}"]
    if tally.get("applied") is not False:
        return ["shadowRuleTally.applied must be false (evidence only)"]
    colors = tally.get("colors")
    if not isinstance(colors, list) or not colors or len(set(colors)) != len(colors) \
            or not set(colors) <= {"red", "blue"}:
        return ["shadowRuleTally.colors is malformed"]
    if tally.get("rules") != list(SHADOW_RULES):
        return ["shadowRuleTally.rules is malformed"]
    for key in ("formatVersion", "totalFrames", "sampleLimit"):
        if not _count(tally.get(key)):
            return [f"shadowRuleTally.{key} is malformed"]
    errors = _shadow_bucket_errors(tally.get("total"), colors, "shadowRuleTally.total")
    if errors:
        return errors
    total = tally["total"]
    if total["frames"] != tally["totalFrames"]:
        errors.append("shadowRuleTally.totalFrames does not match total.frames")
    for group, allowed in (("byLabel", SEGMENT_LABELS), ("byExposure", SHADOW_EXPOSURE_BUCKETS)):
        buckets = tally.get(group)
        if not isinstance(buckets, dict) or not set(buckets) <= set(allowed):
            errors.append(f"shadowRuleTally.{group} is malformed")
            continue
        group_errors = []
        for name in allowed:
            if name in buckets:
                group_errors.extend(_shadow_bucket_errors(buckets[name], colors, f"shadowRuleTally.{group}.{name}"))
        errors.extend(group_errors)
        if not group_errors:
            sums = [sum(values) for values in zip(*(_shadow_leaves(buckets[n], colors)
                                                   for n in allowed if n in buckets))] \
                if buckets else [0] * len(_shadow_leaves(total, colors))
            if sums != _shadow_leaves(total, colors):
                errors.append(f"shadowRuleTally.{group} does not add up to total")
    samples, offered = tally.get("winnerRejectionSamples"), tally.get("winnerRejectionsOffered")
    if not isinstance(samples, dict) or set(samples) != set(SHADOW_RULES) \
            or not isinstance(offered, dict) or set(offered) != set(SHADOW_RULES):
        errors.append("shadowRuleTally samples are malformed")
        return errors
    for rule in SHADOW_RULES:
        rejected = sum(total[color][rule]["winnersRejected"] for color in colors)
        listed = samples[rule]
        if not _count(offered[rule]) or offered[rule] > rejected or not isinstance(listed, list) \
                or len(listed) > min(tally["sampleLimit"], offered[rule]):
            errors.append(f"shadowRuleTally.winnerRejectionSamples.{rule} exceeds its bound")
            continue
        previous = None
        for index, sample in enumerate(listed):
            path = f"shadowRuleTally.winnerRejectionSamples.{rule}[{index}]"
            if not isinstance(sample, dict) or not set(sample) <= SHADOW_SAMPLE_KEYS \
                    or not _count(sample.get("frameID")) or not _matches_kind(sample.get("timestamp"), "number") \
                    or sample.get("label") not in SEGMENT_LABELS \
                    or sample.get("label") in FALSE_POSITIVE_SEGMENT_LABELS \
                    or sample.get("color") not in colors \
                    or sample.get("exposureBucket", "unknown") not in SHADOW_EXPOSURE_BUCKETS:
                errors.append(f"{path} is malformed")
                continue
            if previous is not None and sample["frameID"] <= previous:
                errors.append(f"{path} frameID is not increasing")
            previous = sample["frameID"]
    if "exposureExperimentSetting" in tally \
            and tally["exposureExperimentSetting"] not in CAMERA_EXPOSURE_EXPERIMENT_SETTINGS:
        errors.append("shadowRuleTally.exposureExperimentSetting is unknown")
    if "definition" in tally and (not isinstance(tally["definition"], str) or len(tally["definition"]) > 2000):
        errors.append("shadowRuleTally.definition is malformed")
    return errors
