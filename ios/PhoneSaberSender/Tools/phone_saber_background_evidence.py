#!/usr/bin/env python3
"""Background false-positive evidence: emitter terms, shadow R7e / PF22 verdicts and exposure.

Read-only, free and offline (no model call). Used by the session report section
「背景誤検出の証拠(emitter / shadow R7e / 露出)」. It reads the triage contexts of a
bundle (``frames/*.json``) and joins, per candidate, the evidence recorded since
commit 31ad94c (see PHONE_SABER_DEBUG_METADATA_SCHEMA.md):

* ``candidateDecisionTrace[].emitterDiagnostics`` (full, selected frame only);
* ``candidateGeometry.candidates[].emitter`` (compact subset, selected frame only);
* ``frames[].camera`` (selected frame only).

Winners are the production-selected candidate (``selectedCandidateIndex``, the
first eligible candidate) of each context's selected frame. Static hotspot
clusters come from ``phone_saber_hotspots.static_hotspots(include_members=True)``.

Everything here is EVIDENCE, not ground truth: ``likelyBackground`` is a
heuristic hint, and ``shadowR7e`` is a shadow verdict (``applied: false``) of an
offline rule that production eligibility, ranking and UDP never read. A
"would reject" count says only that the recorded R7e expression was false for a
candidate production accepted. Bundles without these fields yield ``n/a``.

``shadowPF22`` is the second shadow verdict (red purity floor:
``meanColorPurity >= 0.22 OR clippedWhiteRatio >= 0.35``, also ``applied: false``).
Its inputs are plain candidate fields, so where a bundle predates the recorded
verdict it is RECOMPUTED offline from the decision-trace / selectedCandidate
``meanColorPurity`` + ``clippedWhiteRatio`` (or the geometry ``shadowR7e``
copies, rounded to 4 decimals) of production-eligible red candidates. The PF22
tally covers every red winner in the contexts (not only selected frames), the
eligible candidates, and the selected-frame red jump / candidateSwitch events:
an event "becomes no-detection" only when the rule rejects every eligible
candidate of that frame and the frame's eligible list is fully known.
"""
from __future__ import annotations

import json
import math
from collections import Counter
from pathlib import Path
from statistics import median
from typing import Any

COLORS = ("red", "blue")
EMITTER_SCORE_THRESHOLD = 0.42
TERM_KEYS = ("peakTerm", "meanTerm", "highValueTerm", "purityTerm", "clippedWhiteTerm")
TERM_LABELS = {"peakTerm": "peak", "meanTerm": "mean", "highValueTerm": "highValue",
               "purityTerm": "purity", "clippedWhiteTerm": "clippedWhite"}
CORE_KEYS = ("coreByHighValueRatio", "coreByPeakAndMean", "coreByClippedWhite")
# Shadow R7e: clippedWhiteRatio >= 0.35 OR d240 >= 4.2 OR (d240 >= 3.5 AND meanColorPurity >= 0.60).
R7E_CLIPPED_WHITE = 0.35
R7E_THICK_BODY_D240 = 4.2
R7E_SATURATED_D240 = 3.5
R7E_SATURATED_PURITY = 0.60
# Shadow PF22 (red purity floor): meanColorPurity >= 0.22 OR clippedWhiteRatio >= 0.35.
PF22_PURITY = 0.22
PF22_CLIPPED_WHITE = 0.35
# A selected-frame tracking event: candidateSwitch, or a winner midpoint jump of
# at least this many pixels (the session report's REPLAY_JUMP_PX).
EVENT_JUMP_PX = 100.0
EVENT_OUTCOMES = ("unchanged", "winnerChanges", "noDetection", "unknown", "n/a")
R7E_MARGIN_KEYS = ("clippedWhiteMargin", "thickBodyMargin", "saturatedBodyDensityMargin",
                   "saturatedBodyPurityMargin")
EXTRA_KEYS = ("baseEligible", "bladeLengthSupport", "localContrast", "emitterTexture", "coreSupport",
              "meanSecondChannel", "meanMinChannel", "nearWhiteFraction")
CAMERA_RANGE_KEYS = ("iso", "exposureDurationSeconds", "exposureBiasEV", "brightnessValue",
                     "exposureTargetBias", "exposureTargetOffset")
CAMERA_ROW_KEYS = ("iso", "exposureDurationSeconds", "exposureBiasEV", "brightnessValue")
# peakTerm = clamp01((peak - 200) / 55) * 0.32
PEAK_TERM_WEIGHT, PEAK_FLOOR, PEAK_RANGE = 0.32, 200.0, 55.0
FEW_FRAMES = 3
NOTE = ("evidence only, NOT ground truth: likelyBackground is a hint and shadowR7e / shadowPF22 are shadow "
        "verdicts (applied:false) that production eligibility/ranking/UDP never read")


def _number(value: Any) -> float | None:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        return None
    return float(value)


def _int(value: Any) -> int | None:
    return value if isinstance(value, int) and not isinstance(value, bool) else None


def _bool(value: Any) -> bool | None:
    return value if isinstance(value, bool) else None


def _round(value: float | None, digits: int = 4) -> float | None:
    return None if value is None else round(value, digits)


def _median(values: list[float]) -> float | None:
    return _round(median(values), 6) if values else None


def _range(values: list[float]) -> list[float] | None:
    return [min(values), max(values)] if values else None


# --- per-candidate normalisation --------------------------------------------

def normalize_shadow(raw: Any) -> dict | None:
    """Shadow R7e verdict with all four margins (recorded, or computed from the
    compact geometry subset with the documented thresholds)."""
    if not isinstance(raw, dict):
        return None
    d240 = _number(raw.get("d240"))
    clipped = _number(raw.get("clippedWhiteRatio"))
    purity = _number(raw.get("meanColorPurity"))
    recorded = {k: _number(raw.get(k)) for k in R7E_MARGIN_KEYS}
    if all(v is not None for v in recorded.values()):
        margins, source = recorded, "recorded"
    else:
        margins = {"clippedWhiteMargin": None if clipped is None else clipped - R7E_CLIPPED_WHITE,
                   "thickBodyMargin": None if d240 is None else d240 - R7E_THICK_BODY_D240,
                   "saturatedBodyDensityMargin": None if d240 is None else d240 - R7E_SATURATED_D240,
                   "saturatedBodyPurityMargin": None if purity is None else purity - R7E_SATURATED_PURITY}
        source = "computed"
    rule = _bool(raw.get("ruleSatisfied"))
    if rule is None and None not in (d240, clipped, purity):
        rule = (clipped >= R7E_CLIPPED_WHITE or d240 >= R7E_THICK_BODY_D240
                or (d240 >= R7E_SATURATED_D240 and purity >= R7E_SATURATED_PURITY))
    eligible = _bool(raw.get("shadowR7eEligible"))
    return {"applied": _bool(raw.get("applied")), "shadowR7eEligible": eligible,
            "verdict": "n/a" if eligible is None else ("keep" if eligible else "reject"),
            "ruleSatisfied": rule, "d240": d240, "clippedWhiteRatio": clipped, "meanColorPurity": purity,
            "usedFallbackDensity": _bool(raw.get("usedFallbackDensity")),
            "margins": {k: _round(v) for k, v in margins.items()}, "marginsSource": source}


def pf22_verdict(raw: Any, purity: Any = None, clipped: Any = None, eligible: Any = None) -> dict | None:
    """Shadow PF22 verdict: the recorded ``shadowPF22`` when present, otherwise
    recomputed from the candidate's meanColorPurity / clippedWhiteRatio and its
    production eligibility. None when neither is available."""
    purity, clipped = _number(purity), _number(clipped)
    if isinstance(raw, dict) and isinstance(raw.get("shadowPF22Eligible"), bool):
        purity = _number(raw.get("meanColorPurity")) if _number(raw.get("meanColorPurity")) is not None else purity
        clipped = _number(raw.get("clippedWhiteRatio")) if _number(raw.get("clippedWhiteRatio")) is not None \
            else clipped
        eligible_after, source = raw["shadowPF22Eligible"], "recorded"
        rule = _bool(raw.get("ruleSatisfied"))
        if rule is None and None not in (purity, clipped):
            rule = purity >= PF22_PURITY or clipped >= PF22_CLIPPED_WHITE
    elif None not in (purity, clipped) and isinstance(eligible, bool):
        rule = purity >= PF22_PURITY or clipped >= PF22_CLIPPED_WHITE
        eligible_after, source = eligible and rule, "recomputed"
    else:
        return None
    return {"applied": False, "source": source, "shadowPF22Eligible": eligible_after,
            "verdict": "keep" if eligible_after else "reject", "ruleSatisfied": rule,
            "meanColorPurity": purity, "clippedWhiteRatio": clipped,
            "purityMargin": None if purity is None else _round(purity - PF22_PURITY),
            "clippedWhiteMargin": None if clipped is None else _round(clipped - PF22_CLIPPED_WHITE)}


def normalize_emitter(raw: Any, source: str) -> dict | None:
    """One candidate's emitter evidence. ``source`` is ``decisionTrace`` (full
    ``emitterDiagnostics``) or ``candidateGeometry`` (compact ``emitter``)."""
    if not isinstance(raw, dict):
        return None
    score = _number(raw.get("emitterScore"))
    margin = _number(raw.get("emitterScoreMargin"))
    if margin is None and score is not None:
        margin = score - EMITTER_SCORE_THRESHOLD
    terms = {k: _number(raw.get(k)) for k in TERM_KEYS if _number(raw.get(k)) is not None}
    total = sum(terms.values())
    dominant = [{"term": TERM_LABELS[k], "value": _round(v),
                 "share": _round(v / total, 3) if total > 0 else None}
                for k, v in sorted(terms.items(), key=lambda item: (-item[1], TERM_KEYS.index(item[0])))]
    core = {k: raw[k] for k in CORE_KEYS if isinstance(raw.get(k), bool)}
    result = {"evidenceSource": source, "emitterScore": score, "emitterScoreMargin": _round(margin),
              "terms": {TERM_LABELS[k]: v for k, v in terms.items()}, "dominantTerms": dominant,
              "hasEmitterCore": _bool(raw.get("hasEmitterCore")),
              "coreInputs": core or None,
              "shadowR7e": normalize_shadow(raw.get("shadowR7e")),
              "shadowPF22Recorded": isinstance(raw.get("shadowPF22"), dict)}
    for key in EXTRA_KEYS:
        value = raw.get(key)
        result[key] = value if isinstance(value, bool) else _number(value)
    return result


def peak_from_term(evidence: dict | None) -> float | None:
    """Invert peakTerm when it is not clamped (rounded terms give about ±1 level)."""
    term = _number(((evidence or {}).get("terms") or {}).get("peak"))
    if term is None or term <= 0:
        return None
    if term >= PEAK_TERM_WEIGHT:
        return 255.0  # clamped at the top: an 8-bit peak of 255
    return round(PEAK_FLOOR + term / PEAK_TERM_WEIGHT * PEAK_RANGE, 1)


# --- collecting the bundle ----------------------------------------------------

def _contexts(root: Path):
    frames_dir = root / "frames"
    for path in sorted(frames_dir.glob("*.json")) if frames_dir.is_dir() else []:
        if path.is_symlink():
            continue
        try:
            context = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            continue
        if isinstance(context, dict) and isinstance(context.get("frames"), list):
            yield path.name, context


def _first_eligible_in_prefix(data: dict) -> int | None:
    """First eligible decision-trace index when the trace covers indices 0..k without a gap."""
    entries = {e["index"]: e for e in data.get("candidateDecisionTrace") or []
               if isinstance(e, dict) and _int(e.get("index")) is not None}
    position = 0
    while position in entries:
        if entries[position].get("eligible") is True:
            return position
        position += 1
    return None


def collect(root: Path) -> dict:
    """Index evidence by (color, frameID, candidateIndex); cameras by frameID."""
    evidence: dict[tuple[str, int, int], dict] = {}
    peaks: dict[tuple[str, int, int], float] = {}
    cameras: dict[int, dict] = {}
    winners: dict[tuple[str, int], dict] = {}
    # PF22 (red only): verdict per candidate, preferring recorded over full-precision
    # recomputation over the 4-decimal geometry copies.
    pf22: dict[tuple[str, int, int], dict] = {}
    pf22_rank: dict[tuple[str, int, int], int] = {}
    eligible_flags: dict[tuple[str, int, int], bool] = {}
    eligible_counts: dict[tuple[str, int], int] = {}
    frame_winners: dict[tuple[str, int], int] = {}
    events: dict[tuple[str, int], dict] = {}

    def offer_pf22(key: tuple[str, int, int], verdict: dict | None, rank: int) -> None:
        if verdict is not None and rank > pf22_rank.get(key, -1):
            pf22[key], pf22_rank[key] = verdict, rank

    reduced = 0
    compaction: Counter = Counter()
    contexts = 0
    for _, context in _contexts(root):
        contexts += 1
        for step in context.get("compaction") or []:
            if isinstance(step, str):
                compaction[step] += 1
        selected_id = _int(context.get("selectedFrameID"))
        for frame in context["frames"]:
            frame_id = _int(frame.get("frameID")) if isinstance(frame, dict) else None
            if frame_id is None:
                continue
            if isinstance(frame.get("camera"), dict):
                cameras.setdefault(frame_id, frame["camera"])
            for color in COLORS:
                data = frame.get(color)
                if not isinstance(data, dict):
                    continue
                if _int(data.get("eligibleCandidateCount")) is not None:
                    eligible_counts[(color, frame_id)] = max(eligible_counts.get((color, frame_id), 0),
                                                             data["eligibleCandidateCount"])
                for entry in data.get("candidateDecisionTrace") or []:
                    index = _int(entry.get("index")) if isinstance(entry, dict) else None
                    if index is None:
                        continue
                    key = (color, frame_id, index)
                    if isinstance(entry.get("eligible"), bool):
                        eligible_flags[key] = entry["eligible"]
                    if color == "red":
                        diagnostics = entry.get("emitterDiagnostics")
                        recorded = diagnostics.get("shadowPF22") if isinstance(diagnostics, dict) else None
                        offer_pf22(key, pf22_verdict(recorded, entry.get("meanColorPurity"),
                                                     entry.get("clippedWhiteRatio"),
                                                     _bool(entry.get("eligible"))),
                                   3 if isinstance(recorded, dict) else 2)
                    if _number(entry.get("peakValue")) is not None:
                        peaks.setdefault(key, _number(entry["peakValue"]))
                    normalized = normalize_emitter(entry.get("emitterDiagnostics"), "decisionTrace")
                    if normalized is not None:
                        evidence[key] = normalized  # full evidence always wins
                selected = data.get("selectedCandidate")
                if isinstance(selected, dict) and _int(selected.get("index")) is not None \
                        and _number(selected.get("peakValue")) is not None:
                    peaks.setdefault((color, frame_id, selected["index"]), _number(selected["peakValue"]))
                if color == "red" and isinstance(selected, dict) and _int(selected.get("index")) is not None:
                    key = (color, frame_id, selected["index"])
                    eligible_flags.setdefault(key, True)  # the selected candidate is production-eligible
                    diagnostics = selected.get("emitterDiagnostics")
                    recorded = diagnostics.get("shadowPF22") if isinstance(diagnostics, dict) else None
                    offer_pf22(key, pf22_verdict(recorded, selected.get("meanColorPurity"),
                                                 selected.get("clippedWhiteRatio"), True),
                               3 if isinstance(recorded, dict) else 2)
                geometry = data.get("candidateGeometry")
                candidates = geometry.get("candidates") if isinstance(geometry, dict) else None
                rank_one = None
                for entry in candidates if isinstance(candidates, list) else []:
                    index = _int(entry.get("listIndex")) if isinstance(entry, dict) else None
                    if index is None:
                        continue
                    if entry.get("eligible") is True and entry.get("eligibleRank") == 1:
                        rank_one = index
                    if entry.get("emitterDiagnosticsReduced") is True:
                        reduced += 1
                    key = (color, frame_id, index)
                    if isinstance(entry.get("eligible"), bool):
                        eligible_flags.setdefault(key, entry["eligible"])
                    emitter = entry.get("emitter")
                    if color == "red" and isinstance(emitter, dict):
                        recorded = emitter.get("shadowPF22")
                        r7e = emitter.get("shadowR7e") if isinstance(emitter.get("shadowR7e"), dict) else {}
                        offer_pf22(key, pf22_verdict(recorded, r7e.get("meanColorPurity"),
                                                     r7e.get("clippedWhiteRatio"), _bool(entry.get("eligible"))),
                                   3 if isinstance(recorded, dict) else 1)
                    normalized = normalize_emitter(entry.get("emitter"), "candidateGeometry")
                    if normalized is not None and key not in evidence:
                        evidence[key] = normalized
                if data.get("detected") is not True or data.get("predictionUsed") is True:
                    continue
                index = _int(data.get("selectedCandidateIndex"))
                index = rank_one if index is None else index
                # Older contexts lack selectedCandidateIndex: production selects the first
                # eligible candidate in list order, known when the trace is a list prefix.
                winner = index if index is not None else _first_eligible_in_prefix(data)
                if winner is not None:
                    frame_winners.setdefault((color, frame_id), winner)
                if frame_id != selected_id:
                    continue
                tracking = data.get("tracking") if isinstance(data.get("tracking"), dict) else {}
                jump = _number(tracking.get("midpointDisplacement"))
                if winner is not None and (tracking.get("candidateSwitch") is True
                                           or (jump is not None and jump >= EVENT_JUMP_PX)):
                    events.setdefault((color, frame_id), {
                        "candidateSwitch": tracking.get("candidateSwitch") is True,
                        "midpointDisplacementPx": _round(jump, 1), "candidateIndex": winner,
                        "selectedCandidateType": data.get("selectedCandidateType")})
                if index is not None:
                    winners.setdefault((color, frame_id), {
                        "candidateIndex": index, "selectedCandidateType": data.get("selectedCandidateType"),
                        "finalScore": _number(data.get("score"))})
    return {"contexts": contexts, "evidence": evidence, "peaks": peaks, "cameras": cameras,
            "winners": winners, "emitterDiagnosticsReduced": reduced,
            "pf22": pf22, "eligibleFlags": eligible_flags, "eligibleCounts": eligible_counts,
            "frameWinners": frame_winners, "events": events,
            "emitterCompaction": {k: v for k, v in compaction.items() if "Emitter" in k}}


# --- aggregation --------------------------------------------------------------

def _camera_row(camera: dict | None) -> dict | None:
    if not isinstance(camera, dict):
        return None
    row = {k: _number(camera.get(k)) for k in CAMERA_ROW_KEYS if _number(camera.get(k)) is not None}
    row["source"] = camera.get("source") if isinstance(camera.get("source"), str) else None
    return row


def exposure_section(cameras: dict[int, dict]) -> dict:
    if not cameras:
        return {"available": False, "framesWithCamera": 0}
    result: dict[str, Any] = {"available": True, "framesWithCamera": len(cameras),
                              "sources": dict(Counter(str(c.get("source")) for c in cameras.values()))}
    for key in CAMERA_RANGE_KEYS:
        values = [v for v in (_number(c.get(key)) for c in cameras.values()) if v is not None]
        result[key] = {"range": _range(values), "median": _median(values), "frames": len(values)}
    ages = [v for v in (_number(c.get("deviceSampleAgeSeconds")) for c in cameras.values()) if v is not None]
    result["maxDeviceSampleAgeSeconds"] = max(ages) if ages else None
    return result


def _group_stats(rows: list[dict]) -> dict:
    def values(key: str) -> list[float]:
        return [r[key] for r in rows if r.get(key) is not None]
    return {"frames": len(rows),
            "framesWithCamera": sum(1 for r in rows if r.get("camera")),
            "medianISO": _median([r["camera"]["iso"] for r in rows
                                  if r.get("camera") and r["camera"].get("iso") is not None]),
            "medianExposureDurationSeconds": _median(
                [r["camera"]["exposureDurationSeconds"] for r in rows
                 if r.get("camera") and r["camera"].get("exposureDurationSeconds") is not None]),
            "medianExposureBiasEV": _median([r["camera"]["exposureBiasEV"] for r in rows
                                             if r.get("camera") and r["camera"].get("exposureBiasEV") is not None]),
            "medianPeakValue": _median(values("peakValue")),
            "peakValueFrames": len(values("peakValue"))}


def _cluster_evidence(cluster: dict, collected: dict) -> dict:
    color = cluster.get("color")
    per_frame: dict[int, dict] = {}
    for member in cluster.get("members") or []:
        frame_id, index = member.get("frameID"), member.get("candidateIndex")
        key = (color, frame_id, index)
        evidence = collected["evidence"].get(key) if index is not None else None
        rank = (member.get("winning") is True, member.get("eligible") is True, member.get("finalScore") or 0)
        kept = per_frame.get(frame_id)
        if kept is not None and kept["_rank"] >= rank:
            continue
        peak = collected["peaks"].get(key)
        peak_source = "recorded" if peak is not None else None
        if peak is None and peak_from_term(evidence) is not None:
            peak, peak_source = peak_from_term(evidence), "fromPeakTerm"
        per_frame[frame_id] = {"_rank": rank, "frameID": frame_id, "candidateIndex": index,
                               "eligible": member.get("eligible") is True,
                               "winning": member.get("winning") is True, "evidence": evidence,
                               "camera": _camera_row(collected["cameras"].get(frame_id)),
                               "peakValue": peak, "peakValueSource": peak_source}
    rows = [per_frame[f] for f in sorted(per_frame)]
    for row in rows:
        row.pop("_rank")
    with_evidence = [r for r in rows if r["evidence"]]
    scores = [r["evidence"]["emitterScore"] for r in with_evidence if r["evidence"]["emitterScore"] is not None]
    margins = [r["evidence"]["emitterScoreMargin"] for r in with_evidence
               if r["evidence"]["emitterScoreMargin"] is not None]
    core_rows = [r for r in with_evidence if r["evidence"]["coreInputs"]]
    shadows = [r["evidence"]["shadowR7e"] for r in with_evidence if r["evidence"]["shadowR7e"]]
    verdicts = Counter(s["verdict"] for s in shadows)
    shadow_margins = {k: _median([s["margins"][k] for s in shadows if s["margins"].get(k) is not None])
                      for k in R7E_MARGIN_KEYS}

    def by(predicate) -> list[dict]:
        return [r for r in rows if predicate(r)]

    def r7e(row: dict) -> str | None:
        shadow = (row["evidence"] or {}).get("shadowR7e")
        return shadow["verdict"] if shadow else None

    return {
        "clusterID": cluster.get("clusterID"), "color": color,
        "likelyBackground": cluster.get("likelyBackground") is True,
        "framesPresent": len(rows), "framesWithEvidence": len(with_evidence),
        "framesWithFullEvidence": sum(1 for r in with_evidence
                                      if r["evidence"]["evidenceSource"] == "decisionTrace"),
        "framesWithCamera": sum(1 for r in rows if r["camera"]),
        "emitterScore": {"min": min(scores), "median": _median(scores), "max": max(scores)} if scores else None,
        "emitterScoreMargin": {"min": min(margins), "median": _median(margins)} if margins else None,
        "termMedians": {TERM_LABELS[k]: _median([r["evidence"]["terms"][TERM_LABELS[k]] for r in with_evidence
                                                 if TERM_LABELS[k] in r["evidence"]["terms"]])
                        for k in TERM_KEYS} if with_evidence else None,
        "dominantTermCounts": dict(Counter(r["evidence"]["dominantTerms"][0]["term"]
                                           for r in with_evidence if r["evidence"]["dominantTerms"])),
        "hasEmitterCore": dict(Counter(str(r["evidence"]["hasEmitterCore"]).lower() for r in with_evidence)),
        "coreInputsTrue": {k: sum(1 for r in core_rows if r["evidence"]["coreInputs"].get(k) is True)
                           for k in CORE_KEYS} if core_rows else None,
        "framesWithCoreInputs": len(core_rows),
        "shadowR7e": {"keep": verdicts.get("keep", 0), "reject": verdicts.get("reject", 0),
                      "naInEvidence": len(with_evidence) - len(shadows),
                      "medianD240": _median([s["d240"] for s in shadows if s["d240"] is not None]),
                      "medianMargins": shadow_margins} if with_evidence else None,
        "exposureByEligibility": {"eligible": _group_stats(by(lambda r: r["eligible"])),
                                  "ineligible": _group_stats(by(lambda r: not r["eligible"]))},
        "exposureByShadowR7e": {"keep": _group_stats(by(lambda r: r7e(r) == "keep")),
                                "reject": _group_stats(by(lambda r: r7e(r) == "reject"))},
        "frames": [r for r in rows if r["evidence"] or r["camera"]],
    }


TALLY_GROUPS = ("likelyBackground", "notLikelyBackground", "unclustered")


def _r7e_of(collected: dict, key: tuple[str, int, int]) -> str | None:
    shadow = (collected["evidence"].get(key) or {}).get("shadowR7e")
    return None if not shadow or shadow["verdict"] == "n/a" else shadow["verdict"]


def _pf22_of(collected: dict, key: tuple[str, int, int]) -> str | None:
    verdict = collected["pf22"].get(key)
    return verdict["verdict"] if verdict else None


def event_outcome(collected: dict, color: str, frame_id: int, winner: int, verdict_of) -> str:
    """What a shadow rule would have done to a frame's output: ``unchanged`` (keeps
    the winner), ``winnerChanges`` (another eligible candidate survives),
    ``noDetection`` (every eligible candidate rejected), ``unknown`` (winner
    rejected but the eligible list or a verdict is missing), ``n/a`` (no winner verdict)."""
    winner_verdict = verdict_of(collected, (color, frame_id, winner))
    if winner_verdict is None:
        return "n/a"
    if winner_verdict == "keep":
        return "unchanged"
    eligible = [index for (c, f, index), flag in collected["eligibleFlags"].items()
                if c == color and f == frame_id and flag]
    if winner not in eligible:
        eligible.append(winner)
    count = collected["eligibleCounts"].get((color, frame_id))
    verdicts = [verdict_of(collected, (color, frame_id, index)) for index in sorted(eligible)]
    if any(v == "keep" for v in verdicts):
        return "winnerChanges"
    if count is None or len(eligible) < count or any(v is None for v in verdicts):
        return "unknown"
    return "noDetection"


def pf22_tally(collected: dict) -> dict:
    """Red-only PF22 counts over every winner, eligible candidate and selected-frame event."""
    red_winners = sorted((f, i) for (c, f), i in collected["frameWinners"].items() if c == "red")
    winner_verdicts = Counter(_pf22_of(collected, ("red", f, i)) or "n/a" for f, i in red_winners)
    sources = Counter(collected["pf22"][("red", f, i)]["source"] for f, i in red_winners
                      if ("red", f, i) in collected["pf22"])
    eligible_keys = sorted(k for k, flag in collected["eligibleFlags"].items() if flag and k[0] == "red")
    eligible_verdicts = Counter(_pf22_of(collected, k) or "n/a" for k in eligible_keys)
    outcomes = Counter()
    rows = []
    for (color, frame_id), event in sorted(collected["events"].items(), key=lambda i: (i[0][1], i[0][0])):
        row = {"frameID": frame_id, "color": color, **event}
        if color == "red":
            winner = event["candidateIndex"]
            pf22 = collected["pf22"].get(("red", frame_id, winner))
            row.update(pf22Outcome=event_outcome(collected, color, frame_id, winner, _pf22_of),
                       r7eOutcome=event_outcome(collected, color, frame_id, winner, _r7e_of),
                       winnerMeanColorPurity=_round((pf22 or {}).get("meanColorPurity")),
                       winnerClippedWhiteRatio=_round((pf22 or {}).get("clippedWhiteRatio")))
            outcomes[row["pf22Outcome"]] += 1
        else:
            row.update(pf22Outcome="n/a", r7eOutcome="n/a")
        rows.append(row)
    red_events = [r for r in rows if r["color"] == "red"]
    return {
        "definition": ("red only. winners = every detected (not predicted) red frame in the contexts; eligible = "
                       "production-eligible red candidates; events = selected-frame red candidateSwitch or "
                       f"midpoint jump >= {EVENT_JUMP_PX:g}px. Verdicts are recorded shadowPF22 or recomputed from "
                       "meanColorPurity/clippedWhiteRatio"),
        "winners": {"frames": len(red_winners), "withVerdict": len(red_winners) - winner_verdicts.get("n/a", 0),
                    "pf22WouldReject": winner_verdicts.get("reject", 0), "pf22Keeps": winner_verdicts.get("keep", 0),
                    "pf22NA": winner_verdicts.get("n/a", 0), "verdictSources": dict(sources)},
        "eligibleCandidates": {"candidates": len(eligible_keys),
                               "withVerdict": len(eligible_keys) - eligible_verdicts.get("n/a", 0),
                               "pf22WouldReject": eligible_verdicts.get("reject", 0),
                               "pf22Keeps": eligible_verdicts.get("keep", 0)},
        "events": {"red": len(red_events), "blueNotApplicable": len(rows) - len(red_events),
                   "pf22Outcomes": {k: outcomes.get(k, 0) for k in EVENT_OUTCOMES},
                   "r7eOutcomes": {k: sum(1 for r in red_events if r["r7eOutcome"] == k) for k in EVENT_OUTCOMES},
                   "rows": rows},
    }


def background_evidence(bundle: Path, hotspots: dict | None) -> dict:
    """The report section as data. ``hotspots`` must come from
    ``static_hotspots(..., include_members=True)``; None means unavailable."""
    collected = collect(Path(bundle))
    clusters = [c for color in COLORS for c in ((hotspots or {}).get("clusters") or {}).get(color) or []]
    by_member: dict[tuple[str, int, int], dict] = {}
    for cluster in clusters:
        for member in cluster.get("members") or []:
            if member.get("candidateIndex") is not None:
                by_member.setdefault((cluster.get("color"), member.get("frameID"), member["candidateIndex"]),
                                     cluster)
    winners = []
    tally = {group: {"winners": 0, "withEvidence": 0, "r7eWouldReject": 0, "r7eKeeps": 0, "r7eNA": 0}
             for group in TALLY_GROUPS}
    pf22_groups = {group: {"winners": 0, "pf22WouldReject": 0, "pf22Keeps": 0, "pf22NA": 0}
                   for group in TALLY_GROUPS}
    for (color, frame_id), winner in sorted(collected["winners"].items(), key=lambda i: (i[0][1], i[0][0])):
        key = (color, frame_id, winner["candidateIndex"])
        evidence = collected["evidence"].get(key)
        cluster = by_member.get(key)
        group = "unclustered" if cluster is None else (
            "likelyBackground" if cluster.get("likelyBackground") else "notLikelyBackground")
        shadow = (evidence or {}).get("shadowR7e")
        verdict = shadow["verdict"] if shadow else "n/a"
        bucket = tally[group]
        bucket["winners"] += 1
        bucket["withEvidence"] += evidence is not None
        bucket[{"reject": "r7eWouldReject", "keep": "r7eKeeps"}.get(verdict, "r7eNA")] += 1
        pf22_verdict_text = (_pf22_of(collected, key) or "n/a") if color == "red" else "n/a"
        pf22_groups[group]["winners"] += 1
        pf22_groups[group][{"reject": "pf22WouldReject", "keep": "pf22Keeps"}.get(pf22_verdict_text,
                                                                                  "pf22NA")] += 1
        peak = collected["peaks"].get(key)
        winners.append({"frameID": frame_id, "color": color, **winner,
                        "clusterID": cluster.get("clusterID") if cluster else None,
                        "likelyBackground": cluster.get("likelyBackground") is True if cluster else None,
                        "evidence": evidence, "r7eVerdict": verdict, "pf22Verdict": pf22_verdict_text,
                        "pf22": collected["pf22"].get(key),
                        "peakValue": peak if peak is not None else peak_from_term(evidence),
                        "camera": _camera_row(collected["cameras"].get(frame_id))})
    totals = {k: sum(tally[g][k] for g in TALLY_GROUPS) for k in tally["unclustered"]}
    pf22_totals = {k: sum(pf22_groups[g][k] for g in TALLY_GROUPS) for k in pf22_groups["unclustered"]}
    shown = [c for c in clusters if c.get("likelyBackground")
             or any(w["clusterID"] == c.get("clusterID") and w["evidence"] for w in winners)]
    return {
        "note": NOTE,
        "contextsRead": collected["contexts"],
        "emitterEvidenceAvailable": bool(collected["evidence"]),
        "candidatesWithEvidence": len(collected["evidence"]),
        "candidatesWithFullEvidence": sum(1 for e in collected["evidence"].values()
                                          if e["evidenceSource"] == "decisionTrace"),
        "emitterDiagnosticsReduced": collected["emitterDiagnosticsReduced"],
        "emitterCompaction": collected["emitterCompaction"],
        "hotspotsAvailable": hotspots is not None,
        "winners": winners,
        "shadowR7eTally": {"byCluster": tally, "total": totals,
                           "definition": "selected-frame winners (production eligible) whose recorded "
                                         "shadowR7eEligible is false = R7e would have changed the output"},
        "shadowPF22Tally": {**pf22_tally(collected),
                            "selectedWinnersByCluster": pf22_groups, "selectedWinnersTotal": pf22_totals},
        "clusters": [_cluster_evidence(c, collected) for c in shown],
        "exposure": exposure_section(collected["cameras"]),
    }


# --- Markdown -----------------------------------------------------------------

def _f(value: Any, digits: int = 3) -> str:
    if value is None:
        return "n/a"
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, float):
        if value == 0:
            return "0"
        if abs(value) >= 1000:
            return f"{value:.0f}"
        if abs(value) >= 1:
            return f"{value:.4g}"
        return f"{value:.{digits}f}".rstrip("0").rstrip(".")
    return str(value)


def _signed(value: Any) -> str:
    number = _number(value)
    return "n/a" if number is None else f"{number:+.3f}"


def _exposure(seconds: Any) -> str:
    value = _number(seconds)
    if value is None or value <= 0:
        return "n/a"
    return f"1/{1 / value:.0f}s" if value < 1 else f"{value:.3g}s"


def _range_text(entry: Any, formatter=_f) -> str:
    if not isinstance(entry, dict) or not entry.get("range"):
        return "n/a"
    low, high = entry["range"]
    return f"{formatter(low)}–{formatter(high)} (median {formatter(entry.get('median'))}, {entry['frames']} frames)"


def _dominant(evidence: dict, count: int = 2) -> str:
    parts = [f"{d['term']} {_f(d['value'])}" + (f" ({d['share']:.0%})" if d.get("share") is not None else "")
             for d in (evidence.get("dominantTerms") or [])[:count]]
    return ", ".join(parts) or "n/a"


def _core(evidence: dict) -> str:
    core = evidence.get("coreInputs")
    head = _f(evidence.get("hasEmitterCore"))
    if not core:
        return f"{head} (inputs n/a: compact)"
    marks = "/".join("T" if core.get(k) else ("F" if k in core else "?") for k in CORE_KEYS)
    return f"{head} ({marks})"


def _shadow_text(shadow: dict | None) -> tuple[str, str]:
    if not shadow:
        return "n/a", "n/a"
    m = shadow["margins"]
    margins = " / ".join(_signed(m.get(k)) for k in R7E_MARGIN_KEYS)
    if shadow["marginsSource"] == "computed":
        margins += " (computed)"
    verdict = {"reject": "**would reject**", "keep": "keep"}.get(shadow["verdict"], "n/a")
    return verdict, f"{margins}, d240 {_f(shadow.get('d240'))}"


def _group_line(label: str, stats: dict) -> str:
    if not stats["frames"]:
        return f"{label}: 0 frames"
    few = " — few frames, do not read a trend" if stats["framesWithCamera"] < FEW_FRAMES else ""
    return (f"{label}: {stats['frames']} frames ({stats['framesWithCamera']} with camera), median ISO "
            f"{_f(stats['medianISO'])}, exposure {_exposure(stats['medianExposureDurationSeconds'])}, bias "
            f"{_f(stats['medianExposureBiasEV'])} EV, peak {_f(stats['medianPeakValue'])} "
            f"({stats['peakValueFrames']} frames){few}")


def pf22_lines(tally: Any, max_event_rows: int = 12) -> list[str]:
    """The shadow PF22 subsection; also shown for bundles without emitter evidence,
    because PF22 is recomputable from plain candidate fields."""
    if not isinstance(tally, dict):
        return []
    w, e, ev = tally["winners"], tally["eligibleCandidates"], tally["events"]
    lines = ["### shadow PF22 tally (red purity ≥ 0.22 unless clippedWhite ≥ 0.35; applied:false)", "",
             "`would reject` = production が eligible とした赤 candidate で、PF22 の式が false"
             "(適用していたら eligible から外れた)。記録がない bundle は meanColorPurity / clippedWhiteRatio から再計算。", ""]
    sources = ", ".join(f"{k} {v}" for k, v in sorted(w["verdictSources"].items())) or "none"
    lines.append(f"- red winners (all context frames): {w['frames']}, with verdict {w['withVerdict']} ({sources}): "
                 f"**would reject {w['pf22WouldReject']}**, keep {w['pf22Keeps']}, n/a {w['pf22NA']}")
    lines.append(f"- eligible red candidates: {e['candidates']}, with verdict {e['withVerdict']}: "
                 f"would reject {e['pf22WouldReject']}, keep {e['pf22Keeps']}")
    sel = tally.get("selectedWinnersTotal")
    if sel:
        lines.append(f"- selected-frame winners (same rows as the R7e table): {sel['winners']}, PF22 would reject "
                     f"{sel['pf22WouldReject']}, keep {sel['pf22Keeps']}, n/a {sel['pf22NA']}")
    pf, r7 = ev["pf22Outcomes"], ev["r7eOutcomes"]
    lines.append(f"- selected-frame red jump (≥{EVENT_JUMP_PX:g}px) / candidateSwitch events: {ev['red']}"
                 + (f" (blue {ev['blueNotApplicable']}: n/a, red-only rules)" if ev["blueNotApplicable"] else ""))
    lines.append("  - PF22: " + ", ".join(f"{k} {pf[k]}" for k in EVENT_OUTCOMES))
    lines.append("  - R7e: " + ", ".join(f"{k} {r7[k]}" for k in EVENT_OUTCOMES))
    red_rows = [r for r in ev["rows"] if r["color"] == "red"]
    if red_rows:
        lines.append("")
        lines.append("| frame | switch | jump px | winner type | purity | clippedWhite | PF22 | R7e |")
        lines.append("| --- | --- | --- | --- | --- | --- | --- | --- |")
        for r in red_rows[:max_event_rows]:
            lines.append(f"| {r['frameID']} | {_f(r['candidateSwitch'])} | {_f(r['midpointDisplacementPx'])} | "
                         f"{_f(r.get('selectedCandidateType'))} | {_f(r.get('winnerMeanColorPurity'))} | "
                         f"{_f(r.get('winnerClippedWhiteRatio'))} | {r['pf22Outcome']} | {r['r7eOutcome']} |")
        if len(red_rows) > max_event_rows:
            lines.append(f"- … {len(red_rows) - max_event_rows} more events in `--json`")
    lines.append("")
    return lines


def render_lines(section: Any, max_winner_rows: int = 24, max_other_clusters: int = 6) -> list[str]:
    lines = ["## 背景誤検出の証拠(emitter / shadow R7e / 露出)", ""]
    try:
        if not isinstance(section, dict):
            return lines + ["- n/a"]
        lines.append("- **evidence only, NOT ground truth**: shadow R7e は `applied:false` の影の判定"
                     "(production の eligibility / ranking / UDP は読まない)。likelyBackground も hint。"
                     "ORIGINAL PNG で確認する")
        if not section["emitterEvidenceAvailable"] and not section["exposure"]["available"]:
            lines.append(f"- n/a — no `emitterDiagnostics` / geometry `emitter` / frame `camera` in "
                         f"{section['contextsRead']} contexts (bundle predates 31ad94c, the recorder had no "
                         "emitter evidence or camera source, or compaction dropped them); selected-frame winners "
                         f"{len(section['winners'])}, none with evidence")
            if section["emitterCompaction"]:
                lines.append(f"- emitter compaction steps: {_f(json.dumps(section['emitterCompaction']))}")
            pf22 = pf22_lines(section.get("shadowPF22Tally"))
            if pf22:
                lines.extend([""] + pf22)
            return lines
        lines.append(f"- contexts {section['contextsRead']}, candidates with emitter evidence "
                     f"{section['candidatesWithEvidence']} (full {section['candidatesWithFullEvidence']}, "
                     f"compact geometry {section['candidatesWithEvidence'] - section['candidatesWithFullEvidence']})"
                     + (f", emitterDiagnosticsReduced {section['emitterDiagnosticsReduced']}"
                        if section["emitterDiagnosticsReduced"] else "")
                     + (f", emitter compaction {json.dumps(section['emitterCompaction'])}"
                        if section["emitterCompaction"] else ""))
        if not section["hotspotsAvailable"]:
            lines.append("- static hotspots n/a: every winner counts as unclustered")
        lines.append("")
        lines.append("### shadow R7e tally (selected-frame winners)")
        lines.append("")
        lines.append("`would reject` = production の winner だが記録された `shadowR7eEligible=false`"
                     "(R7e を適用していたら出力が変わった)。blue は R7e なし(n/a)。")
        lines.append("")
        lines.append("| winner cluster | winners | with evidence | R7e would reject | R7e keeps | R7e n/a |")
        lines.append("| --- | --- | --- | --- | --- | --- |")
        tally = section["shadowR7eTally"]
        for group in TALLY_GROUPS + ("total",):
            row = tally["total"] if group == "total" else tally["byCluster"][group]
            lines.append(f"| {group} | {row['winners']} | {row['withEvidence']} | {row['r7eWouldReject']} "
                         f"| {row['r7eKeeps']} | {row['r7eNA']} |")
        lines.append("")
        lines.extend(pf22_lines(section.get("shadowPF22Tally")))
        winners = section["winners"]
        lines.append(f"### selected-frame winners ({len(winners)})")
        lines.append("")
        if not winners:
            lines.append("- none (no selected frame with a detected winner)")
        else:
            lines.append("| frame | color | cluster | src | emitterScore | margin to 0.42 | dominant terms "
                         "| hasEmitterCore (hv/pm/cw) | R7e | R7e margins cw / thick / satD / satP | peak "
                         "| ISO | exposure |")
            lines.append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |")
            for w in winners[:max_winner_rows]:
                e = w["evidence"]
                cluster = "n/a" if w["clusterID"] is None else (
                    f"{w['clusterID']}" + (" **BG?**" if w["likelyBackground"] else ""))
                camera = w["camera"] or {}
                if e is None:
                    lines.append(f"| {w['frameID']} | {w['color']} | {cluster} | n/a | n/a | n/a | n/a | n/a | n/a "
                                 f"| n/a | {_f(w['peakValue'])} | {_f(camera.get('iso'))} "
                                 f"| {_exposure(camera.get('exposureDurationSeconds'))} |")
                    continue
                verdict, margins = _shadow_text(e["shadowR7e"])
                lines.append(f"| {w['frameID']} | {w['color']} | {cluster} | "
                             f"{'full' if e['evidenceSource'] == 'decisionTrace' else 'compact'} | "
                             f"{_f(e['emitterScore'])} | {_signed(e['emitterScoreMargin'])} | {_dominant(e)} | "
                             f"{_core(e)} | {verdict} | {margins} | {_f(w['peakValue'])} | "
                             f"{_f(camera.get('iso'))} | {_exposure(camera.get('exposureDurationSeconds'))} |")
            if len(winners) > max_winner_rows:
                lines.append(f"- … {len(winners) - max_winner_rows} more rows in `--json`")
        lines.append("")
        lines.append("### hotspot clusters (likelyBackground, and clusters holding a winner with evidence)")
        lines.append("")
        clusters = section["clusters"]
        background = [c for c in clusters if c["likelyBackground"]]
        others = [c for c in clusters if not c["likelyBackground"]]
        if not clusters:
            lines.append("- none" + ("" if section["hotspotsAvailable"] else " (hotspots n/a)"))
        elif not background:
            lines.append("- no likelyBackground cluster in this bundle")
        for c in background:
            lines.append(f"- {c['clusterID']} **LIKELY BACKGROUND**: frames {c['framesPresent']}, with emitter evidence "
                         f"{c['framesWithEvidence']} (full {c['framesWithFullEvidence']}), with camera "
                         f"{c['framesWithCamera']}")
            if not c["framesWithEvidence"]:
                lines.append("  - emitter: n/a (no evidence on this cluster's frames)")
            else:
                score, margin = c["emitterScore"] or {}, c["emitterScoreMargin"] or {}
                lines.append(f"  - emitterScore min/median/max {_f(score.get('min'))}/{_f(score.get('median'))}/"
                             f"{_f(score.get('max'))}, margin to 0.42 min/median {_signed(margin.get('min'))}/"
                             f"{_signed(margin.get('median'))}; dominant term {_f(json.dumps(c['dominantTermCounts']))}; "
                             f"term medians {json.dumps(c['termMedians'])}")
                core = c["coreInputsTrue"]
                lines.append(f"  - hasEmitterCore {json.dumps(c['hasEmitterCore'])}; inputs true "
                             + (f"hv {core['coreByHighValueRatio']} / pm {core['coreByPeakAndMean']} / cw "
                                f"{core['coreByClippedWhite']} of {c['framesWithCoreInputs']} full"
                                if core else "n/a (compact evidence only)"))
                shadow = c["shadowR7e"] or {}
                medians = shadow.get("medianMargins") or {}
                lines.append(f"  - shadow R7e: keep {shadow.get('keep', 0)}, would reject {shadow.get('reject', 0)}, "
                             f"n/a {shadow.get('naInEvidence', 0)}; median d240 {_f(shadow.get('medianD240'))}, "
                             "median margins cw/thick/satD/satP "
                             + " / ".join(_signed(medians.get(k)) for k in R7E_MARGIN_KEYS))
            for label, key, groups in (("exposure by production eligibility", "exposureByEligibility",
                                        ("eligible", "ineligible")),
                                       ("exposure by shadow R7e", "exposureByShadowR7e", ("keep", "reject"))):
                split = c[key]
                if not any(split[g]["framesWithCamera"] for g in groups):
                    lines.append(f"  - {label}: n/a (no camera on this cluster's frames)")
                    continue
                lines.append(f"  - {label} (evidence, not ground truth):")
                for group in groups:
                    lines.append(f"    - {_group_line(group, split[group])}")
        for c in others[:max_other_clusters]:
            score, shadow = c["emitterScore"] or {}, c["shadowR7e"] or {}
            lines.append(f"- {c['clusterID']} (not flagged, holds a winner): frames {c['framesPresent']}, "
                         f"emitterScore median {_f(score.get('median'))}, dominant term "
                         f"{json.dumps(c['dominantTermCounts'])}, R7e keep {shadow.get('keep', 0)} / would reject "
                         f"{shadow.get('reject', 0)} / n/a {shadow.get('naInEvidence', 0)}")
        if len(others) > max_other_clusters:
            lines.append(f"- … {len(others) - max_other_clusters} more non-flagged clusters in `--json`")
        lines.append("")
        lines.append("### exposure per bundle (selected frames only)")
        lines.append("")
        exposure = section["exposure"]
        if not exposure["available"]:
            lines.append("- n/a — no frame `camera` in this bundle (predates 31ad94c)")
        else:
            lines.append(f"- frames with camera {exposure['framesWithCamera']}, sources "
                         f"{json.dumps(exposure['sources'])}, max device sample age "
                         f"{_f(exposure['maxDeviceSampleAgeSeconds'])}s")
            lines.append(f"- ISO {_range_text(exposure['iso'])}")
            lines.append(f"- exposure time {_range_text(exposure['exposureDurationSeconds'], _exposure)}")
            for key, label in (("exposureBiasEV", "Exif exposure bias EV"),
                               ("brightnessValue", "Exif brightness (APEX)"),
                               ("exposureTargetBias", "device target bias EV"),
                               ("exposureTargetOffset", "device target offset EV")):
                lines.append(f"- {label} {_range_text(exposure[key])}")
    except Exception as exc:  # noqa: BLE001 - rendering must never break the report
        lines.append(f"- n/a ({type(exc).__name__}: {exc})")
    return lines
