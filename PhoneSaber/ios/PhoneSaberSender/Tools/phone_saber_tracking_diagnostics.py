"""Bounded tracking evidence from selected PNG contexts; no unselected data access."""
from __future__ import annotations

import json
import math
from typing import Any

from phone_saber_selection_replay import match_metrics
from phone_saber_triage_protocol import BundleError

TRACKING_ROLES = {"before", "onset", "peak", "after", "recovery"}
BRIDGE_ROLES = ("before_success", "dropout", "after_success")
BRIDGE_ANNOTATED_ROLE = "annotated_dropout"
BRIDGE_IMAGE_ROLES = set(BRIDGE_ROLES) | {BRIDGE_ANNOTATED_ROLE}
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
        if image.get("role") in TRACKING_ROLES or "bridgeEventID" in image:
            raise BundleError("tracking image lacks explicit frame mapping")
        return  # Backward-compatible legacy input; never sufficient for tracking repair.
    if "bridgeEventID" in image:
        expected = {"sessionID": session, "frameID": image["frameID"],
                    "timestamp": image.get("timestamp"), "color": image.get("color"),
                    "image": image["path"], "eventID": f"bridge_{image['bridgeEventID']}",
                    "eventRole": image.get("role")}
        if mapping != expected or image.get("sessionID", session) != session:
            raise BundleError("PNG/frame/metadata mapping disagrees with summary")
        matches = [f for f in context["frames"] if f["frameID"] == image["frameID"]]
        if len(matches) != 1 or matches[0]["timestamp"] != mapping["timestamp"]:
            raise BundleError("mapped PNG lacks its exact frame timestamp")
        return
    if image.get("role") in TRACKING_ROLES and (isinstance(image.get("eventIndex"), bool) or
            not isinstance(image.get("eventIndex"), int)):
        raise BundleError("tracking event ID missing")
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


# Optional per-candidate emitter-eligibility evidence (Debug Recording only).
# Older bundles omit it; every key is optional so compact subsets validate too.
EMITTER_NUMBER_KEYS = {
    "emitterScore", "emitterScoreThreshold", "emitterScoreMargin", "peakTerm", "meanTerm",
    "highValueTerm", "purityTerm", "clippedWhiteTerm", "majorLengthSamples", "bladeLengthSupport",
    "localContrast", "emitterTexture", "brightnessVariation", "coreSupport",
    "longitudinalCoreCoverage", "longitudinalHighCoverage", "meanMaxChannel", "meanSecondChannel",
    "meanMinChannel", "nearWhiteFraction", "brightSecondChannelFraction"}
EMITTER_INTEGER_KEYS = {"sampleCount", "colorSampleCount", "maxSecondChannel"}
EMITTER_BOOL_KEYS = {"hasEmitterCore", "coreByHighValueRatio", "coreByPeakAndMean", "coreByClippedWhite",
                     "baseEligible", "compactRedGate"}
WARM_NO_DEEP_RED_KEYS = {"applied", "deepCount", "warmCount", "pixelCount", "warmFrac", "rejected"}


def valid_warm_no_deep_red(value: Any) -> bool:
    """Production RED gate verdict (8363024), recorded in emitterDiagnostics."""
    # rejectionReason is omitted when nil.
    return isinstance(value, dict) and WARM_NO_DEEP_RED_KEYS <= set(value) \
        and set(value) <= WARM_NO_DEEP_RED_KEYS | {"rejectionReason"} \
        and all(isinstance(value[k], bool) for k in ("applied", "rejected")) \
        and all(isinstance(value[k], int) and not isinstance(value[k], bool) and value[k] >= 0
                for k in ("deepCount", "warmCount", "pixelCount")) \
        and value["warmCount"] <= value["pixelCount"] and number(value["warmFrac"]) \
        and 0 <= value["warmFrac"] <= 1 \
        and value.get("rejectionReason") in (None, "warmNoDeepRed") \
        and (value["rejected"] == (value.get("rejectionReason") == "warmNoDeepRed"))


BLUE_NO_DEEP_SUPPORT_KEYS = {"applied", "deepCount", "pixelCount", "rejected"}


def valid_blue_no_deep_support(value: Any) -> bool:
    """Production BLUE verdict; nil rejectionReason may be omitted by Swift."""
    return isinstance(value, dict) and BLUE_NO_DEEP_SUPPORT_KEYS <= set(value) \
        and set(value) <= BLUE_NO_DEEP_SUPPORT_KEYS | {"rejectionReason"} \
        and value["applied"] is True and isinstance(value["rejected"], bool) \
        and all(isinstance(value[k], int) and not isinstance(value[k], bool) and value[k] >= 0
                for k in ("deepCount", "pixelCount")) \
        and value["deepCount"] <= value["pixelCount"] \
        and value["rejected"] == (value["deepCount"] == 0) \
        and value.get("rejectionReason") in (None, "blueNoDeepSupport") \
        and value["rejected"] == (value.get("rejectionReason") == "blueNoDeepSupport")


def validate_emitter_diagnostics(value: Any) -> None:
    # Retired shadow keys are accepted without interpreting their payloads.
    allowed = EMITTER_NUMBER_KEYS | EMITTER_INTEGER_KEYS | EMITTER_BOOL_KEYS \
        | {"shadowR7e", "shadowPF22", "warmNoDeepRed", "blueNoDeepSupport"}
    if isinstance(value, dict) and "warmNoDeepRed" in value \
            and not valid_warm_no_deep_red(value["warmNoDeepRed"]):
        raise BundleError("invalid emitter diagnostics: warmNoDeepRed")
    if isinstance(value, dict) and "blueNoDeepSupport" in value \
            and not valid_blue_no_deep_support(value["blueNoDeepSupport"]):
        raise BundleError("invalid emitter diagnostics: blueNoDeepSupport")
    if not isinstance(value, dict) or not {"emitterScore", "emitterScoreMargin", "hasEmitterCore"} <= set(value) \
            or not set(value) <= allowed \
            or not all(number(value[k]) for k in EMITTER_NUMBER_KEYS & set(value)) \
            or not all(isinstance(value[k], int) and not isinstance(value[k], bool) and value[k] >= 0
                       for k in EMITTER_INTEGER_KEYS & set(value)) \
            or not all(isinstance(value[k], bool) for k in EMITTER_BOOL_KEYS & set(value)):
        raise BundleError("invalid emitter diagnostics")


CAMERA_NUMBER_KEYS = {"iso", "exposureDurationSeconds", "exposureBiasEV", "brightnessValue", "fNumber",
                      "exposureTargetBias", "exposureTargetOffset", "deviceSampleAgeSeconds", "batteryLevel"}
CAMERA_HEALTH_KEYS = {"thermalState", "batteryState"}


def validate_frame_camera(value: Any) -> None:
    """Optional per-frame exposure/health state (`frames[].camera`)."""
    if not isinstance(value, dict) or value.get("source") not in {"exif", "device", "exif+device"} \
            or not set(value) <= CAMERA_NUMBER_KEYS | CAMERA_HEALTH_KEYS | {"source", "whiteBalanceGains"} \
            or not all(number(value[k]) for k in CAMERA_NUMBER_KEYS & set(value)) \
            or ("batteryLevel" in value and not 0 <= value["batteryLevel"] <= 1) \
            or ("thermalState" in value and value["thermalState"] not in ("nominal", "fair", "serious", "critical", "unknown")) \
            or ("batteryState" in value and value["batteryState"] not in ("charging", "full", "unplugged", "unknown")) \
            or ("whiteBalanceGains" in value and not numeric_array(value["whiteBalanceGains"], 3)):
        raise BundleError("invalid frame camera state")


GEOMETRY_MATCH_KEYS = {"centroidDistance", "centroidDistanceNormalized", "bboxIoU", "areaRatio",
                       "spanRatio", "orientationDifference"}
GEOMETRY_ENTRY_KEYS = {"listIndex", "eligible", "eligibleRank", "sourceType", "finalScore", "scoreBreakdown",
                       "centroid", "centroidSource", "bbox", "componentArea", "rawPCASpan",
                       "rawPCAEndpoints", "finalOutputEndpoints", "rejectionReasons", "matchToPreviousWinner",
                       "scoreBreakdownReduced", "emitter", "emitterDiagnosticsReduced"}
GEOMETRY_OPTIONAL_KEYS = {"eligibleRank", "matchToPreviousWinner", "scoreBreakdownReduced", "emitter",
                          "emitterDiagnosticsReduced"}


def validate_candidate_geometry(value: Any, eligible_count: Any = None) -> None:
    """All-eligible candidate geometry for one frame/color, with explicit omission counts."""
    required = {"totalCandidateCount", "eligibleCandidateCount", "savedEligibleCount",
                "eligibleOmittedCount", "savedIneligibleCount", "ineligibleOmittedCount",
                "candidatesTruncated", "candidates", "previousFrameGeometryAvailable"}
    if not isinstance(value, dict) or not required <= set(value) <= required | {"previousWinner"}:
        raise BundleError("invalid candidate geometry block")
    counts = [value[k] for k in required - {"candidatesTruncated", "candidates", "previousFrameGeometryAvailable"}]
    if any(isinstance(c, bool) or not isinstance(c, int) or c < 0 for c in counts) \
            or not isinstance(value["candidatesTruncated"], bool) \
            or not isinstance(value["previousFrameGeometryAvailable"], bool) \
            or not isinstance(value["candidates"], list) or len(value["candidates"]) > 24:
        raise BundleError("invalid candidate geometry counts")
    eligible = [c for c in value["candidates"] if isinstance(c, dict) and c.get("eligible") is True]
    ineligible = [c for c in value["candidates"] if isinstance(c, dict) and c.get("eligible") is False]
    omitted = value["eligibleOmittedCount"] + value["ineligibleOmittedCount"]
    if len(eligible) + len(ineligible) != len(value["candidates"]) \
            or value["savedEligibleCount"] != len(eligible) or value["savedIneligibleCount"] != len(ineligible) \
            or value["eligibleCandidateCount"] != value["savedEligibleCount"] + value["eligibleOmittedCount"] \
            or value["totalCandidateCount"] != value["eligibleCandidateCount"] + value["savedIneligibleCount"] \
                + value["ineligibleOmittedCount"] \
            or value["candidatesTruncated"] != (omitted > 0) \
            or (eligible_count is not None and eligible_count != value["eligibleCandidateCount"]):
        raise BundleError("candidate geometry counts disagree with the recorded eligible count")
    # Swift keeps a rank-ordered prefix of the eligible list, so saved ranks are exactly 1..k.
    ranks = [c.get("eligibleRank") for c in eligible]
    if any(isinstance(r, bool) or not isinstance(r, int) for r in ranks) \
            or ranks != list(range(1, len(eligible) + 1)):
        raise BundleError("invalid eligible ranks")
    for c in value["candidates"]:
        if not isinstance(c, dict) or not (GEOMETRY_ENTRY_KEYS - GEOMETRY_OPTIONAL_KEYS) <= set(c) \
                <= GEOMETRY_ENTRY_KEYS or isinstance(c["listIndex"], bool) or not isinstance(c["listIndex"], int) \
                or not isinstance(c["sourceType"], str) or len(c["sourceType"]) > 100 \
                or c["centroidSource"] not in {"trace", "bboxCenter"} \
                or not number(c["finalScore"]) or not number(c["rawPCASpan"]) or not number(c["componentArea"]) \
                or not numeric_array(c["centroid"], 2) or not numeric_array(c["bbox"], 4) \
                or not numeric_array(c["rawPCAEndpoints"], 4) or not numeric_array(c["finalOutputEndpoints"], 4) \
                or not isinstance(c["scoreBreakdown"], dict) or len(c["scoreBreakdown"]) > 24 \
                or not all(number(x) for x in c["scoreBreakdown"].values()) \
                or not isinstance(c["rejectionReasons"], list) or len(c["rejectionReasons"]) > 20 \
                or not all(isinstance(r, str) and len(r) <= 100 for r in c["rejectionReasons"]) \
                or (c["eligible"] and "eligibleRank" not in c) or (not c["eligible"] and "eligibleRank" in c) \
                or ("scoreBreakdownReduced" in c and c["scoreBreakdownReduced"] is not True) \
                or ("emitterDiagnosticsReduced" in c and (c["emitterDiagnosticsReduced"] is not True
                                                          or "emitter" in c)):
            raise BundleError("invalid candidate geometry entry")
        if "emitter" in c:
            validate_emitter_diagnostics(c["emitter"])
        # Swift writes the full metric set on every entry exactly when a previous winner exists.
        if value["previousFrameGeometryAvailable"]:
            match = c.get("matchToPreviousWinner")
            if not isinstance(match, dict) or set(match) != GEOMETRY_MATCH_KEYS \
                    or not all(number(x) for x in match.values()):
                raise BundleError("invalid candidate correspondence metrics")
        elif "matchToPreviousWinner" in c:
            raise BundleError("candidate correspondence without previous frame geometry")
    if value["previousFrameGeometryAvailable"] != ("previousWinner" in value):
        raise BundleError("previous winner presence disagrees with previousFrameGeometryAvailable")
    winner = value.get("previousWinner")
    if "previousWinner" in value and (not isinstance(winner, dict)
            or not {"frameID", "centroid", "bbox", "componentArea", "rawPCASpan", "rawPCAEndpoints",
                    "finalOutputEndpoints", "listIndex"} <= set(winner)
            or isinstance(winner["frameID"], bool) or not isinstance(winner["frameID"], int)
            or not number(winner["componentArea"]) or not number(winner["rawPCASpan"])
            or not numeric_array(winner["centroid"], 2) or not numeric_array(winner["bbox"], 4)
            or not numeric_array(winner["rawPCAEndpoints"], 4) or not numeric_array(winner["finalOutputEndpoints"], 4)):
        raise BundleError("invalid previous winner geometry")


# Heuristic thresholds for the hint audit only; they never reach recognition or the gate.
AUDIT_CONTINUITY_IOU = 0.2
AUDIT_CONTINUITY_DISTANCE = 0.5
AUDIT_ENDPOINT_STAGE = 0.5


def _continuous(match: dict) -> bool:
    return match.get("bboxIoU", 0) >= AUDIT_CONTINUITY_IOU \
        or match.get("centroidDistanceNormalized", math.inf) <= AUDIT_CONTINUITY_DISTANCE


def _b_cause(geometry: dict, matching: list) -> str:
    if geometry["totalCandidateCount"] == 0:
        return "notGenerated"
    if matching:
        return "ineligible"
    return "unknownTruncated" if geometry["candidatesTruncated"] else "noCandidateMatchesPreviousWinner"


def _endpoints_broken(winner: dict, previous: dict) -> dict | None:
    """Final endpoints of the same-looking winner against the previous winner (geometry only)."""
    a, b = winner["finalOutputEndpoints"], previous["finalOutputEndpoints"]
    direct = math.hypot(a[0] - b[0], a[1] - b[1]) + math.hypot(a[2] - b[2], a[3] - b[3])
    flipped = math.hypot(a[2] - b[0], a[3] - b[1]) + math.hypot(a[0] - b[2], a[1] - b[3])
    if flipped < direct:
        a = [a[2], a[3], a[0], a[1]]
    span = max(math.hypot(b[2] - b[0], b[3] - b[1]), 1)
    movement = max(math.hypot(a[0] - b[0], a[1] - b[1]), math.hypot(a[2] - b[2], a[3] - b[3])) / span
    length_ratio = max(math.hypot(a[2] - a[0], a[3] - a[1]), 1) / span
    angle = abs(math.atan2(a[3] - a[1], a[2] - a[0]) - math.atan2(b[3] - b[1], b[2] - b[0])) % math.pi
    angle = min(angle, math.pi - angle)
    broken = movement >= AUDIT_ENDPOINT_STAGE or not 0.67 <= length_ratio <= 1.5 or angle >= 0.5
    return {"movementOverSpan": round(movement, 4), "lengthRatio": round(length_ratio, 4),
            "orientationDifference": round(angle, 4)} if broken else None


def _valid_geometry(value: Any) -> dict | None:
    """The block when it meets the recorder contract; the audit never reasons about anything else."""
    try:
        validate_candidate_geometry(value)
    except (BundleError, KeyError, TypeError, ValueError, AttributeError):
        return None
    return value


def _matching_ineligible(candidates: list) -> list:
    return [x for x in candidates if x["eligible"] is False and _continuous(x["matchToPreviousWinner"])]


def _recovery(frames: list, color: str, geometry: dict, winner: dict) -> dict | None:
    """Return leg of a one-frame switch-out, so the switch is counted once (at its onset).

    The previous frame's winner was itself not continuous with ITS previous winner (its rank-1
    entry, recorded on the neighbour frame of the same context, carries that correspondence),
    and the current winner continues the winner from two frames back (same geometry metrics).
    """
    previous = geometry["previousWinner"]
    frame = next((f for f in frames if isinstance(f, dict) and f.get("frameID") == previous["frameID"]), None)
    values = frame.get(color) if frame else None
    before = _valid_geometry(values.get("candidateGeometry") if isinstance(values, dict) else None)
    if not before or not before["previousFrameGeometryAvailable"]:
        return None
    switched = next((x for x in before["candidates"] if x["eligible"] and x["eligibleRank"] == 1), None)
    if switched is None or switched["listIndex"] != previous["listIndex"] \
            or _continuous(switched["matchToPreviousWinner"]):
        return None
    earlier = before["previousWinner"]
    match = match_metrics(winner, earlier)
    if not _continuous(match):
        return None
    return {"switchedOutFrameID": previous["frameID"], "returnsToWinnerOfFrameID": earlier["frameID"],
            "matchToWinnerBeforeSwitch": {k: round(v, 4) for k, v in match.items()}}


def _image_color(context: dict) -> str | None:
    mapping = context.get("imageMapping")
    value = context.get("selectedColor") or (mapping.get("color") if isinstance(mapping, dict) else None)
    return value if value in {"red", "blue", "both"} else None


def _audit_color(frames: list, values: dict, geometry: dict, row: dict) -> None:
    candidates = geometry["candidates"]
    winner = next((x for x in candidates if x["eligible"] and x["eligibleRank"] == 1), None)
    row.update(totalCandidateCount=geometry["totalCandidateCount"],
               eligibleCandidateCount=geometry["eligibleCandidateCount"],
               candidatesTruncated=geometry["candidatesTruncated"],
               previousWinnerFrameID=geometry["previousWinner"]["frameID"],
               eligibleOmittedCount=geometry["eligibleOmittedCount"])
    if winner is None:
        matching = _matching_ineligible(candidates)
        if geometry["eligibleCandidateCount"] > 0:
            # Eligible candidates exist but none was saved: nothing can be said about them.
            row.update(hint="unknown", detail="eligible candidates exist but none were saved",
                       bCause="unknownTruncated")
        else:
            row.update(hint="B", detail="no eligible candidate", bCause=_b_cause(geometry, matching))
        row["ineligibleMatchesPreviousWinner"] = [
            {"listIndex": x["listIndex"], "rejectionReasons": x["rejectionReasons"]} for x in matching]
        return
    winner_continuous = _continuous(winner["matchToPreviousWinner"])
    alternatives = [x for x in candidates if x["eligible"] and x is not winner
                    and _continuous(x["matchToPreviousWinner"])]
    tracking = values.get("tracking") if isinstance(values.get("tracking"), dict) else {}
    recovery = None if winner_continuous else _recovery(frames, row["color"], geometry, winner)
    if recovery:
        row.update(hint="recovery",
                   detail="winner returns to the winner before a one-frame switch-out; counted at the onset",
                   winnerListIndex=winner["listIndex"], winnerScore=winner["finalScore"],
                   winnerDistanceFromPreviousWinner=winner["matchToPreviousWinner"]["centroidDistanceNormalized"],
                   **recovery)
    elif not winner_continuous and alternatives:
        best = max(alternatives, key=lambda x: x["finalScore"])
        margin = winner["finalScore"] - best["finalScore"]
        row.update(hint="A", detail="a candidate matching the previous winner stayed eligible but lost",
                   winnerListIndex=winner["listIndex"], winnerScore=winner["finalScore"],
                   matchingAlternativeListIndex=best["listIndex"], matchingAlternativeScore=best["finalScore"],
                   scoreMargin=round(margin, 4),
                   scoreMarginRatio=round(margin / abs(winner["finalScore"]), 4) if winner["finalScore"] else None,
                   winnerDistanceFromPreviousWinner=winner["matchToPreviousWinner"]["centroidDistanceNormalized"])
    elif not winner_continuous:
        matching = _matching_ineligible(candidates)
        # Only omitted ELIGIBLE candidates can hide a continuing eligible one (a would-be A).
        row.update(hint="B" if geometry["eligibleOmittedCount"] == 0 or matching else "unknown",
                   detail="winner does not match the previous winner and no eligible candidate does",
                   bCause=_b_cause(geometry, matching),
                   ineligibleMatchesPreviousWinner=[
                       {"listIndex": x["listIndex"], "rejectionReasons": x["rejectionReasons"]} for x in matching])
    elif _endpoints_broken(winner, geometry["previousWinner"]) or tracking.get("endpointPathChanged"):
        row.update(hint="C", detail="winner matches the previous winner but its endpoints are discontinuous",
                   endpointDiscontinuity=_endpoints_broken(winner, geometry["previousWinner"]),
                   endpointPathChanged=tracking.get("endpointPathChanged"))
    else:
        row.update(hint="none", detail="winner matches the previous winner; endpoints continuous")


def candidate_selection_audit(plan: Any) -> list[dict]:
    """Deterministic hints separating the failure shapes from the selected contexts.

    A: the winner does not continue the previous winner while an eligible candidate that does
       stays eligible and loses. No score-margin threshold is applied: `scoreMargin` and
       `scoreMarginRatio` (margin / |winner score|) are informational. Counting an A as
       evidence still needs a human to confirm the margin is narrow AND that the original PNG
       shows a real saber where the losing candidate is.
    B: no eligible candidate corresponds to the previous winner (ineligible or absent).
    C: the winner corresponds to the previous winner but its endpoints break.
    recovery: the return leg after a one-frame switch-out (the switch is counted once, at A/B).
    unknown: omitted eligible candidates (or an invalid geometry block) prevent a decision.
    Rows carry `subjectColor` (the row's color is the image's selected color) and
    `countForTally` (exactly one row per (frameID, color), preferring the subject color),
    so CASE counts never double count a frame. Hints only: pixels and the analysis decide,
    and nothing here feeds the gate.
    """
    rows = []
    for image in plan.images:
        try:
            c = json.loads(image.context_path.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            continue
        if not isinstance(c, dict):
            continue
        bridge = c.get("bridgeEvent")
        if isinstance(bridge, dict) and bridge.get("auxiliary"):
            continue
        frames = c["frames"] if isinstance(c.get("frames"), list) else []
        frame = next((f for f in frames if isinstance(f, dict) and f.get("frameID") == image.frame_id), None)
        image_color = _image_color(c)
        active = c.get("activeColors")
        colors = [x for x in active if x in {"red", "blue"}] if isinstance(active, list) and active \
            else ["red", "blue"]
        for color in colors:
            values = frame.get(color) if frame else None
            raw = values.get("candidateGeometry") if isinstance(values, dict) else None
            if not raw:
                continue
            row = {"imageID": image.image_id, "frameID": image.frame_id, "color": color,
                   "imageColor": image_color, "subjectColor": image_color in {None, "both", color}}
            geometry = _valid_geometry(raw)
            if geometry is None:
                row.update(hint="unknown", detail="candidate geometry violates the recorder contract")
                rows.append(row)
                continue
            if not geometry["previousFrameGeometryAvailable"]:
                continue
            _audit_color(frames, values, geometry, row)
            rows.append(row)
    chosen: dict = {}
    for index, row in enumerate(rows):
        key = (row["frameID"], row["color"])
        if key not in chosen or (row["subjectColor"] and not rows[chosen[key]]["subjectColor"]):
            chosen[key] = index
    for index, row in enumerate(rows):
        row["countForTally"] = chosen[(row["frameID"], row["color"])] == index
    return rows


def print_candidate_audit(rows: list[dict]) -> None:
    for row in rows:
        print("[AUTO_REPAIR][CANDIDATE_AUDIT] " + " ".join(
            f"{k}={json.dumps(v, ensure_ascii=False, separators=(',', ':'))}" for k, v in row.items()), flush=True)


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


def validate_bridge_context(value: Any, image: dict) -> None:
    """Shape and summary agreement of one bridge image's event block."""
    keys = {"eventID", "role", "auxiliary", "color", "beforeFrameID", "dropoutFrameID",
            "afterFrameID", "beforeTimestamp", "dropoutTimestamp", "afterTimestamp",
            "missingFrameCount", "gapSeconds", "evidenceUnit", "continuity", "annotation"}
    if not isinstance(value, dict) or not {"eventID", "role", "auxiliary", "color", "beforeFrameID",
            "dropoutFrameID", "afterFrameID", "beforeTimestamp", "dropoutTimestamp", "afterTimestamp",
            "missingFrameCount", "gapSeconds", "evidenceUnit", "continuity"} <= set(value) <= keys:
        raise BundleError("invalid bridge event context")
    ids = [value["beforeFrameID"], value["dropoutFrameID"], value["afterFrameID"]]
    times = [value["beforeTimestamp"], value["dropoutTimestamp"], value["afterTimestamp"]]
    if any(isinstance(x, bool) or not isinstance(x, int) for x in ids) or not all(number(x) for x in times) \
            or not ids[0] < ids[1] < ids[2] or not times[0] < times[1] < times[2] \
            or not number(value["gapSeconds"]) or value["gapSeconds"] <= 0 \
            or isinstance(value["missingFrameCount"], bool) or not isinstance(value["missingFrameCount"], int) \
            or value["missingFrameCount"] < 1 or not isinstance(value["continuity"], dict) \
            or value["color"] not in {"red", "blue"}:
        raise BundleError("bridge event frames are not ordered before < dropout < after")
    if value["eventID"] != image.get("bridgeEventID") or value["role"] != image.get("role") \
            or value["auxiliary"] is not (image.get("role") == BRIDGE_ANNOTATED_ROLE) \
            or value["color"] != image.get("color"):
        raise BundleError("bridge event summary and context disagree")
    expected_frame = {"before_success": ids[0], "dropout": ids[1], "annotated_dropout": ids[1],
                      "after_success": ids[2]}[image["role"]]
    if image.get("frameID") != expected_frame:
        raise BundleError("bridge event role does not match its frame")
    if value["auxiliary"] and not isinstance(value.get("annotation"), dict):
        raise BundleError("annotated bridge image lacks annotation metadata")


def bridge_events(plan: Any) -> list[dict]:
    """One entry per bridge dropout. Its frames are ONE temporal evidence event."""
    groups: dict[int, dict] = {}
    for image in plan.images:
        c = json.loads(image.context_path.read_text(encoding="utf-8"))
        bridge = c.get("bridgeEvent")
        if not bridge:
            continue
        event = groups.setdefault(bridge["eventID"], {
            "eventID": bridge["eventID"], "color": bridge["color"],
            "evidenceUnit": f"bridge_dropout_{bridge['eventID']}",
            "gapSeconds": bridge["gapSeconds"], "missingFrameCount": bridge["missingFrameCount"],
            "continuity": bridge["continuity"], "images": {}})
        selected = next((f for f in c["frames"] if f["frameID"] == image.frame_id), {})
        values = selected.get(bridge["color"], {})
        event["images"][bridge["role"]] = {
            "imageID": image.image_id, "frameID": image.frame_id,
            "timestamp": selected.get("timestamp"), "role": bridge["role"],
            "auxiliary": bridge["auxiliary"],
            "detected": values.get("detectionSucceeded", values.get("detected")),
            "candidateCount": values.get("candidateCount"),
            "eligibleCandidateCount": values.get("eligibleCandidateCount"),
            "endpoint": values.get("endpoint")}
    return sorted(groups.values(), key=lambda e: e["eventID"])


def complete_bridge_event(event: dict) -> bool:
    images = event["images"]
    if not all(role in images for role in BRIDGE_ROLES):
        return False
    before, dropout, after = (images[role] for role in BRIDGE_ROLES)
    return before["detected"] is True and after["detected"] is True and dropout["detected"] is False \
        and number(before["timestamp"]) and number(dropout["timestamp"]) and number(after["timestamp"]) \
        and before["frameID"] < dropout["frameID"] < after["frameID"] \
        and before["timestamp"] < dropout["timestamp"] < after["timestamp"]


class PrecheckFailed(BundleError):
    """Selected input cannot support analysis; never substitute another frame."""
    def __init__(self, reasons: list[dict]):
        self.reasons = reasons
        self.reason_codes = sorted({r["code"] for r in reasons})
        super().__init__("PRECHECK_FAILED " + ",".join(self.reason_codes) + ": " +
                         "; ".join(r["detail"] for r in reasons))


def input_failure(error: BundleError) -> PrecheckFailed:
    """Translate the existing strict input contract without relaxing it."""
    detail = str(error)
    lower = detail.lower()
    code = "inputContractInvalid"
    if "event id missing" in lower:
        code = "temporalEvidenceMissing"
    elif "timestamp missing or invalid" in lower:
        code = "timestampMissing"
    elif "not a png" in lower:
        code = "imageFileInvalid"
    elif "missing" in lower and (".png" in lower or "not a png" in lower):
        code = "imageFileMissing"
    elif "endpoint pipeline" in lower or "endpoint pair" in lower:
        code = "endpointHistoryMissing"
    elif "selected tracking candidate" in lower:
        code = "candidateHistoryMissing"
    elif any(x in lower for x in (".json", "mapping", "frame entry", "frameid", "frame context", "timestamp", "duplicate selected", "ambiguous selected")):
        code = "frameMappingMissing"
    elif any(x in lower for x in ("motion", "event", "tracking")):
        code = "temporalEvidenceMissing"
    return PrecheckFailed([{"code": code, "detail": detail}])


def tracking_preflight(plan: Any) -> dict:
    """Check selected evidence before any LLM request, including re-analysis.

    A dropout-only event can use eligibility diagnostics without three selected
    candidates. Geometry/path/switch events need the complete tracking history.
    UDP send-start records are optional: they cannot prove delivery to Unity.
    """
    reasons: list[dict] = []
    def fail(code: str, detail: str) -> None:
        reasons.append({"code": code, "detail": detail})
    groups: dict[tuple[int, str], list[dict]] = {}
    for image in plan.images:
        c = json.loads(image.context_path.read_text(encoding="utf-8"))
        motion = c.get("motionEvent", {})
        if motion.get("role") not in TRACKING_ROLES:
            continue
        key = (motion["eventIndex"], c["selectedColor"])
        groups.setdefault(key, []).append(c)
        if not image.image_path.is_file():
            fail("imageFileMissing", str(image.image_path))
        frames = c["frames"]
        if not all(number(f.get("timestamp")) for f in frames):
            fail("timestampMissing", f"frame={image.frame_id}")
        if any(b["frameID"] <= a["frameID"] or b["timestamp"] <= a["timestamp"]
               for a, b in zip(frames, frames[1:])):
            fail("temporalOrderInvalid", f"context frame={image.frame_id}")
    events = temporal_events(plan)
    summary = json.loads((plan.root / "summary.json").read_text(encoding="utf-8"))
    ledger = {e[0]: e[3] for e in summary.get("motionEventSummary", {}).get("events", [])
              if isinstance(e, list) and len(e) == 4}
    # bridge_priority is the explicit ledger code for a tracking event that was left
    # out whole so bridge dropout events fit the image limit; it is not missing evidence.
    if not events and summary.get("motionEventSummary", {}).get("trackingCapture") \
            and "bridge_priority" not in ledger.values():
        fail("temporalEvidenceMissing", "tracking capture has no selected temporal event")
    modes = []
    for e in events:
        label = f"event={e['eventID']} color={e['color']}"
        fs = e["frames"]
        contexts = groups[(e["eventID"], e["color"])]
        ids = [c["selectedFrameID"] for c in contexts]
        if ids != sorted(set(ids)):
            fail("temporalOrderInvalid", label + " selected PNG order")
        if e["centerFrameID"] is None:
            fail("eventCenterMissing", label)
        peak = next((f for f in fs if f["role"] == "peak"), {})
        t = peak.get("tracking") or {}
        dropout_only = peak.get("detected") is False and not (
            t.get("candidateSwitch") or t.get("endpointPathChanged") or
            t.get("stageDiscontinuities", {}).get("finalSelected", 0) > 0)
        modes.append({"eventID": e["eventID"], "mode": "eligibility-dropout" if dropout_only else "tracking"})
        if dropout_only:
            continue
        if len(fs) < 3 or not e["consecutiveFrames"] or e["centerFrameID"] is None or not (
                fs[0]["frameID"] < e["centerFrameID"] < fs[-1]["frameID"]):
            fail("temporalEvidenceMissing", label + " requires consecutive before/peak/after")
        if any(not number(f.get("timestamp")) for f in fs):
            fail("timestampMissing", label)
        if any(not f.get("tracking") for f in fs):
            fail("trackingTimelineMissing", label)
        observed = [f for f in fs if f.get("detected") is True]
        if len(observed) < 3 or any(not f.get("selectedCandidate") for f in observed):
            fail("candidateHistoryMissing", label)
        pipelines = [(f.get("selectedCandidate") or {}).get("endpointPipeline") or {} for f in observed]
        if len(pipelines) < 3 or any(not p.get("finalSelected") for p in pipelines) or any(
                not numeric_array(f.get("emittedEndpoint"), 4) for f in observed):
            fail("endpointHistoryMissing", label)
        if len(pipelines) < 3 or any(not p.get("endpointSource") for p in pipelines):
            fail("endpointPathHistoryMissing", label)
        if sum(bool((f.get("tracking") or {}).get("stageDiscontinuities")) for f in fs) < 2:
            fail("trackingTimelineMissing", label + " stage history")
        if not sufficient_temporal(e):
            fail("trackingAssessmentDataMissing", label)
    for event in bridge_events(plan):
        label = f"bridge event={event['eventID']} color={event['color']}"
        if not all(role in event["images"] for role in BRIDGE_ROLES):
            fail("bridgeEvidenceIncomplete", label + " needs before_success, dropout and after_success")
        elif not complete_bridge_event(event):
            fail("bridgeEvidenceIncomplete", label + " must be detected, missed, detected in time order")
        else:
            modes.append({"eventID": f"bridge_{event['eventID']}", "mode": "bridge-dropout"})
    return {"status": "PRECHECK_FAILED" if reasons else "PASS",
            "reasonCodes": sorted({r["code"] for r in reasons}), "reasons": reasons,
            "eventModes": modes}


STAGE_LABELS = {"candidate_selection": "candidate-selection", "mask_component": "mask-component",
                "PCA": "raw-pca", "robust_body": "body-endpoint",
                "endpoint_selection": "final-selection", "fallback": "final-selection",
                "downstream": "downstream", "unknown": "unknown"}


def tracking_summary(plan: Any, report: dict, gate: dict | None = None) -> list[dict]:
    assessment = report.get("analysis", {}).get("tracking_assessment", {})
    rows = []
    precheck = tracking_preflight(plan)
    for event in temporal_events(plan):
        peak = next((f for f in event["frames"] if f["role"] == "peak"), {})
        t = peak.get("tracking") or {}
        # The first stage is the analysis conclusion; numeric jumps alone are
        # not proof of a recognition defect.
        refs = set(assessment.get("temporal_image_ids", []))
        temporal_complete = sufficient_temporal(event) and precheck["status"] == "PASS"
        row = {"sessionID": plan.session_id, "selectedEventID": event["eventID"],
            "color": event["color"], "centerFrame": event["centerFrameID"],
            "instabilityScore": t.get("instabilityScore"), "detected": peak.get("detected"),
            "candidateSwitch": t.get("candidateSwitch"), "endpointPathChanged": t.get("endpointPathChanged"),
            "midpointDiscontinuity": t.get("midpointDisplacement"),
            "angleDiscontinuity": t.get("orientationChange"), "lengthDiscontinuity": t.get("lengthChange"),
            "firstUnstableStage": STAGE_LABELS.get(assessment.get("first_unstable_stage"), "unknown") if peak.get("imageID") in refs else "unknown",
            "visualEvidence": "complete" if assessment.get("symptom_confirmed_in_images") is True and supports_temporal_images(event, refs) else "incomplete",
            "temporalEvidence": "complete" if temporal_complete else "incomplete",
            "analysisResult": report.get("precheck", {}).get("status") if not report.get("analysisExecuted") else report.get("analysis", {}).get("repair_assessment", {}).get("decision", "unknown"),
            "reanalysisExecuted": report.get("analysisReanalysisExecuted", False),
            "solEscalationExecuted": report.get("analysisEscalationExecuted", False),
            "gateResult": (gate or {}).get("decision", "pending"),
            "reasonCodes": (gate or report.get("precheck", {})).get("reasonCodes", [])}
        rows.append(row)
    return rows


def bridge_summary(plan: Any, report: dict | None = None) -> list[dict]:
    """One row per bridge event; its three frames are a single evidence unit."""
    rows = []
    for event in bridge_events(plan):
        images = event["images"]
        rows.append({"sessionID": plan.session_id, "bridgeEventID": event["eventID"],
            "color": event["color"], "evidenceUnit": event["evidenceUnit"],
            "frames": [images[r]["frameID"] for r in BRIDGE_ROLES if r in images],
            "missingFrameCount": event["missingFrameCount"], "gapSeconds": event["gapSeconds"],
            "annotatedImage": BRIDGE_ANNOTATED_ROLE in images,
            "temporalEvidence": "complete" if complete_bridge_event(event) else "incomplete",
            "independentExamples": 1})
    return rows


def print_bridge_summary(rows: list[dict]) -> None:
    for row in rows:
        print("[AUTO_REPAIR][BRIDGE_SUMMARY] " + " ".join(
            f"{k}={json.dumps(v, ensure_ascii=False, separators=(',', ':'))}" for k, v in row.items()), flush=True)


def print_tracking_summary(rows: list[dict]) -> None:
    for row in rows:
        print("[AUTO_REPAIR][TRACKING_SUMMARY] " + " ".join(
            f"{k}={json.dumps(v, ensure_ascii=False, separators=(',', ':'))}" for k, v in row.items()), flush=True)
