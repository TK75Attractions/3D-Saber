"""Bounded tracking evidence from selected PNG contexts; no unselected data access."""
from __future__ import annotations

import json
import math
from typing import Any

from phone_saber_triage_protocol import BundleError

TRACKING_ROLES = {"before", "onset", "peak", "after", "recovery"}
PIPELINE_STAGES = {"candidate_selection", "mask_component", "PCA", "robust_body",
                   "endpoint_selection", "fallback", "downstream", "unknown"}
TRACKING_ASSESSMENT_SCHEMA = {
    "type": "object", "additionalProperties": False,
    "properties": {
        "symptom_confirmed_in_images": {"type": "boolean"},
        "first_unstable_stage": {"type": "string", "enum": sorted(PIPELINE_STAGES)},
        "temporal_image_ids": {"type": "array", "items": {"type": "string"}},
        **{key: {"type": "string"} for key in (
            "concrete_cause", "concrete_production_change", "expected_effect", "regression_risk")},
    },
    "required": ["symptom_confirmed_in_images", "first_unstable_stage", "temporal_image_ids",
                 "concrete_cause", "concrete_production_change", "expected_effect", "regression_risk"],
}


def number(value: Any) -> bool:
    return isinstance(value, (float, int)) and not isinstance(value, bool) and math.isfinite(value)


def numeric_array(value: Any, size: int) -> bool:
    return isinstance(value, list) and len(value) == size and all(number(x) for x in value)


def validate_mapping(context: dict, image: dict, session: str) -> None:
    mapping = context.get("imageMapping")
    if mapping is None:
        if image.get("role") in TRACKING_ROLES:
            raise BundleError("tracking image lacks explicit frame mapping")
        return  # Backward-compatible legacy input; never sufficient for tracking repair.
    expected = {"sessionID": session, "frameID": image["frameID"],
                "timestamp": image.get("timestamp"), "color": image.get("color"),
                "image": image["path"], "eventID": str(image.get("eventIndex", "none")),
                "eventRole": image.get("role", "single")}
    if mapping != expected or image.get("sessionID", session) != session:
        raise BundleError("PNG/frame/metadata mapping disagrees with summary")
    matches = [f for f in context["frames"] if f["frameID"] == image["frameID"]]
    if len(matches) != 1 or matches[0]["timestamp"] != mapping["timestamp"]:
        raise BundleError("mapped PNG lacks its exact frame timestamp")


def validate_compound(value: Any) -> None:
    if not isinstance(value, list) or len(value) > 8:
        raise BundleError("invalid compound rejection list")
    for group in value:
        if not isinstance(group, dict) or set(group) != {"rejectionRule", "conditions"} \
                or not isinstance(group["rejectionRule"], str) or len(group["rejectionRule"]) > 100 \
                or not isinstance(group["conditions"], list) or not 1 <= len(group["conditions"]) <= 16:
            raise BundleError("invalid compound rejection")
        for c in group["conditions"]:
            if not isinstance(c, dict) or set(c) != {"condition", "value", "comparison", "threshold", "satisfied"} \
                    or not isinstance(c["condition"], str) or len(c["condition"]) > 100 \
                    or not number(c["value"]) or not number(c["threshold"]) \
                    or c["comparison"] not in {">=", "<=", ">", "<", "=="} \
                    or not isinstance(c["satisfied"], bool):
                raise BundleError("invalid compound condition")
            a, b = c["value"], c["threshold"]
            expected = {">=": a >= b, "<=": a <= b, ">": a > b, "<": a < b, "==": a == b}[c["comparison"]]
            if c["satisfied"] != expected:
                raise BundleError("compound condition satisfaction disagrees with comparator")


def validate_tracking(value: Any) -> None:
    required = {"candidateSwitch", "candidateMatchConfidence", "endpointPathChanged", "detectedToggle",
                "stageDiscontinuities", "instabilityScore"}
    optional = {"midpoint", "segmentLength", "orientationRadians", "endpointDisplacement",
                "midpointDisplacement", "lengthChange", "orientationChange", "scoreChange", "scoreMarginCollapse", "scoreComponents"}
    if not isinstance(value, dict) or not required.issubset(value) or not set(value) <= required | optional:
        raise BundleError("invalid tracking measurements")
    for key in ("candidateSwitch", "endpointPathChanged", "detectedToggle"):
        if not isinstance(value[key], bool):
            raise BundleError("invalid tracking switch")
    if value["candidateMatchConfidence"] not in {"unavailable", "alternativeGeometryMatch", "geometryMatch", "ambiguousMotion"}:
        raise BundleError("invalid candidate correspondence")
    for key in (optional - {"midpoint", "scoreComponents"}) | {"instabilityScore"}:
        if key in value and not number(value[key]):
            raise BundleError("invalid tracking number")
    if "midpoint" in value and not numeric_array(value["midpoint"], 2):
        raise BundleError("invalid tracking midpoint")
    if "scoreComponents" in value:
        components = value["scoreComponents"]
        if not isinstance(components, dict) or set(components) != {
                "geometryDiscontinuity", "candidateSwitch", "endpointPathChanged", "detectedToggle", "scoreChange", "scoreMarginCollapse"} \
                or not all(number(x) and x >= 0 for x in components.values()) \
                or not math.isclose(sum(components.values()), value["instabilityScore"], abs_tol=1e-9):
            raise BundleError("invalid instability score components")
    stages = value["stageDiscontinuities"]
    if not isinstance(stages, dict) or not set(stages) <= {"rawPCA", "robustInterval", "body", "finalSelected"} \
            or not all(number(x) and x >= 0 for x in stages.values()) or value["instabilityScore"] < 0:
        raise BundleError("invalid stage discontinuities")


def validate_selected(value: Any) -> None:
    required = {"index", "sourceType", "finalScore", "bbox", "componentArea", "pointCount",
                "peakValue", "meanValue", "highValueRatio", "meanColorPurity", "clippedWhiteRatio",
                "continuity", "density", "maxGap", "endpointPipeline"}
    if not isinstance(value, dict) or not required.issubset(value) or not set(value) <= required | {"centroid"}:
        raise BundleError("invalid selected tracking candidate")
    if not isinstance(value["sourceType"], str) or len(value["sourceType"]) > 100 \
            or not numeric_array(value["bbox"], 4) \
            or ("centroid" in value and not numeric_array(value["centroid"], 2)):
        raise BundleError("invalid candidate geometry")
    if not all(number(value[k]) for k in required - {"sourceType", "bbox", "endpointPipeline"}):
        raise BundleError("invalid candidate metrics")
    p = value["endpointPipeline"]
    keys = {"coordinateSpace", "rawPCA", "finalSelected", "endpointSource", "bodyAdopted",
            "robustIntervalAdopted", "gatingValues"}
    if not isinstance(p, dict) or not keys.issubset(p) \
            or not set(p) <= keys | {"body", "robustInterval", "fallback", "fallbackReason", "gatingCoordinateSpace"} \
            or p["coordinateSpace"] != "sourceImagePixels" \
            or p["endpointSource"] not in {"fallbackPCA", "bodyPCA"} \
            or not all(isinstance(p[k], bool) for k in ("bodyAdopted", "robustIntervalAdopted")):
        raise BundleError("invalid endpoint pipeline")
    for key in ("rawPCA", "finalSelected", "body", "robustInterval", "fallback"):
        if key not in p:
            continue
        pair = p[key]
        if not isinstance(pair, dict) or set(pair) != {"first", "second"}:
            raise BundleError("invalid endpoint pair")
        for point in pair.values():
            if not isinstance(point, dict) or set(point) != {"x", "y"} or not all(number(x) for x in point.values()):
                raise BundleError("invalid endpoint point")
    if "gatingCoordinateSpace" in p and p["gatingCoordinateSpace"] not in {"sourceImageMetricsOnly", "maskGridAtDecision"}:
        raise BundleError("invalid endpoint gating coordinate space")
    if "fallbackReason" in p and p["fallbackReason"] not in {
            "insufficientBodyPoints", "bodyNotStronglyTrimmed", "bodySupportOrPCAUnavailable"}:
        raise BundleError("invalid fallback reason")
    if not isinstance(p["gatingValues"], dict) or len(p["gatingValues"]) > 32 \
            or not all(isinstance(k, str) and len(k) <= 100 and number(v) for k, v in p["gatingValues"].items()):
        raise BundleError("invalid endpoint gating values")


def validate_transmissions(records: Any, frame_id: int) -> None:
    if not isinstance(records, list) or len(records) > 4:
        raise BundleError("invalid transmission history")
    for r in records:
        if not isinstance(r, dict) or set(r) != {"frameID", "color", "endpoint", "sourceEndpoint",
                                               "coordinateSpace", "state", "hostTimestamp"} \
                or r["frameID"] != frame_id or r["color"] not in {"red", "blue"} \
                or not numeric_array(r["endpoint"], 4) or not numeric_array(r["sourceEndpoint"], 4) \
                or r["coordinateSpace"] != "configuredUDPOutputPixels" \
                or r["state"] != "sendStarted" or not number(r["hostTimestamp"]):
            raise BundleError("invalid transmitted endpoints")


def validate_assessment(value: Any, images: set[str]) -> None:
    if not isinstance(value, dict) or set(value) != set(TRACKING_ASSESSMENT_SCHEMA["required"]) \
            or not isinstance(value["symptom_confirmed_in_images"], bool) \
            or value["first_unstable_stage"] not in PIPELINE_STAGES:
        raise BundleError("invalid tracking assessment")
    refs = value["temporal_image_ids"]
    if not isinstance(refs, list) or not all(isinstance(x, str) for x in refs) \
            or len(refs) != len(set(refs)) or not set(refs) <= images:
        raise BundleError("invalid tracking assessment image references")
    for key in ("concrete_cause", "concrete_production_change", "expected_effect", "regression_risk"):
        if not isinstance(value[key], str) or len(value[key]) > 3000:
            raise BundleError("invalid tracking repair proposal")


def temporal_events(plan: Any) -> list[dict]:
    groups: dict[tuple[int, str], list[dict]] = {}
    for image in plan.images:
        c = json.loads(image.context_path.read_text(encoding="utf-8"))
        mapping = c.get("imageMapping")
        motion = c.get("motionEvent", {})
        if not mapping or motion.get("role") not in TRACKING_ROLES:
            continue
        selected = next(f for f in c["frames"] if f["frameID"] == image.frame_id)
        color = c["selectedColor"]
        values = selected.get(color, {})
        groups.setdefault((motion["eventIndex"], color), []).append({
            "imageID": image.image_id, "image": mapping["image"], "frameID": image.frame_id,
            "timestamp": selected["timestamp"], "role": motion["role"],
            "detected": values.get("detectionSucceeded", values.get("detected")),
            "candidateCount": values.get("candidateCount"), "eligibleCandidateCount": values.get("eligibleCandidateCount"),
            "selectedCandidate": values.get("selectedCandidate"),
            "secondBestScore": values.get("secondBestScore"), "scoreMargin": values.get("scoreMargin"),
            "tracking": values.get("tracking"), "emittedEndpoint": values.get("endpoint"),
            "udpTransmissions": [r for r in c.get("udpTransmissions", []) if r["color"] == color],
            "transmissionEvidence": "observedSendStart" if any(r["color"] == color for r in c.get("udpTransmissions", [])) else "unavailableAtRecordingStop",
        })
    events = []
    for (event_id, color), frames in groups.items():
        frames.sort(key=lambda f: f["frameID"])
        peaks = [f for f in frames if f["role"] == "peak"]
        contiguous = all(b["frameID"] == a["frameID"] + 1 and b["timestamp"] > a["timestamp"]
                         for a, b in zip(frames, frames[1:]))
        events.append({"eventID": event_id, "color": color, "symptom": "trackingInstability",
                       "centerFrameID": peaks[0]["frameID"] if len(peaks) == 1 else None,
                       "consecutiveFrames": contiguous, "frames": frames})
    return events


def sufficient_temporal(event: dict) -> bool:
    frames = event["frames"]
    return len(frames) >= 3 and event["consecutiveFrames"] and event["centerFrameID"] is not None \
        and frames[0]["frameID"] < event["centerFrameID"] < frames[-1]["frameID"] \
        and sum(bool(f.get("selectedCandidate")) for f in frames) >= 3 \
        and sum(bool(f.get("tracking", {}).get("stageDiscontinuities")) for f in frames if f.get("tracking")) >= 2


def supports_temporal_images(event: dict, references: set[str]) -> bool:
    if not sufficient_temporal(event) or len(references) < 3:
        return False
    frames = event["frames"]
    if not references <= {f["imageID"] for f in frames}:
        return False
    center = event["centerFrameID"]
    return all(any(f["imageID"] in references and relation(f["frameID"], center) for f in frames)
               for relation in (lambda a, b: a < b, lambda a, b: a == b, lambda a, b: a > b))


def has_tracking_discontinuity(event: dict) -> bool:
    return any(f["role"] == "peak" and f.get("tracking") and (
        f["tracking"]["candidateSwitch"] or f["tracking"]["endpointPathChanged"]
        or f["tracking"]["stageDiscontinuities"].get("finalSelected", 0) > 0)
        for f in event["frames"])


def tracking_repair_required(analysis: dict, events: list[dict]) -> bool:
    tracking_images = {f["imageID"] for e in events for f in e["frames"]}
    assessment = analysis.get("repair_assessment") or {}
    evidence_ids = set(assessment.get("evidence_image_ids", []))
    targeted_finding = any("D" in finding["classification"]
        and evidence_ids.intersection(finding["image_ids"]).intersection(tracking_images)
        for finding in analysis["wrong_candidate_and_endpoint_errors"])
    numeric_target = any(has_tracking_discontinuity(e) and any(
        f["role"] == "peak" and f["imageID"] in evidence_ids for f in e["frames"]) for e in events)
    return bool(tracking_images & evidence_ids) and (targeted_finding or numeric_target
        or assessment.get("root_cause_stage") == "endpoint"
        or analysis.get("tracking_assessment", {}).get("symptom_confirmed_in_images") is True)
