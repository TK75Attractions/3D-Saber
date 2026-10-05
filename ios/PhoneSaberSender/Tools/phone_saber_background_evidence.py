#!/usr/bin/env python3
"""Background false-positive evidence: emitter terms, hotspots and exposure.

Read-only and offline. Selected-frame candidate evidence and camera state are
joined with static hotspots. All evidence needs confirmation in ORIGINAL PNGs.
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
# A selected-frame tracking event: candidateSwitch, or a winner midpoint jump of
# at least this many pixels (the session report's REPLAY_JUMP_PX).
EVENT_JUMP_PX = 100.0
EXTRA_KEYS = ("baseEligible", "bladeLengthSupport", "localContrast", "emitterTexture", "coreSupport",
              "meanSecondChannel", "meanMinChannel", "nearWhiteFraction")
CAMERA_RANGE_KEYS = ("iso", "exposureDurationSeconds", "exposureBiasEV", "brightnessValue",
                     "exposureTargetBias", "exposureTargetOffset")
CAMERA_ROW_KEYS = ("iso", "exposureDurationSeconds", "exposureBiasEV", "brightnessValue")
# peakTerm = clamp01((peak - 200) / 55) * 0.32
PEAK_TERM_WEIGHT, PEAK_FLOOR, PEAK_RANGE = 0.32, 200.0, 55.0
FEW_FRAMES = 3
NOTE = "evidence only, NOT ground truth: likelyBackground is a hint; confirm in ORIGINAL PNGs"


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
              "coreInputs": core or None}
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
    events: dict[tuple[str, int], dict] = {}

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
                for entry in data.get("candidateDecisionTrace") or []:
                    index = _int(entry.get("index")) if isinstance(entry, dict) else None
                    if index is None:
                        continue
                    key = (color, frame_id, index)
                    if _number(entry.get("peakValue")) is not None:
                        peaks.setdefault(key, _number(entry["peakValue"]))
                    normalized = normalize_emitter(entry.get("emitterDiagnostics"), "decisionTrace")
                    if normalized is not None:
                        evidence[key] = normalized  # full evidence always wins
                selected = data.get("selectedCandidate")
                if isinstance(selected, dict) and _int(selected.get("index")) is not None \
                        and _number(selected.get("peakValue")) is not None:
                    peaks.setdefault((color, frame_id, selected["index"]), _number(selected["peakValue"]))
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
            "events": events,
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
    def by(predicate) -> list[dict]:
        return [r for r in rows if predicate(r)]

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
        "exposureByEligibility": {"eligible": _group_stats(by(lambda r: r["eligible"])),
                                  "ineligible": _group_stats(by(lambda r: not r["eligible"]))},
        "frames": [r for r in rows if r["evidence"] or r["camera"]],
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
    for (color, frame_id), winner in sorted(collected["winners"].items(), key=lambda i: (i[0][1], i[0][0])):
        key = (color, frame_id, winner["candidateIndex"])
        evidence = collected["evidence"].get(key)
        cluster = by_member.get(key)
        peak = collected["peaks"].get(key)
        winners.append({"frameID": frame_id, "color": color, **winner,
                        "clusterID": cluster.get("clusterID") if cluster else None,
                        "likelyBackground": cluster.get("likelyBackground") is True if cluster else None,
                        "evidence": evidence,
                        "peakValue": peak if peak is not None else peak_from_term(evidence),
                        "camera": _camera_row(collected["cameras"].get(frame_id))})
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
        "events": [{"frameID": frame_id, "color": color, **event}
                   for (color, frame_id), event in sorted(collected["events"].items(),
                                                          key=lambda item: (item[0][1], item[0][0]))],
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


def _group_line(label: str, stats: dict) -> str:
    if not stats["frames"]:
        return f"{label}: 0 frames"
    few = " — few frames, do not read a trend" if stats["framesWithCamera"] < FEW_FRAMES else ""
    return (f"{label}: {stats['frames']} frames ({stats['framesWithCamera']} with camera), median ISO "
            f"{_f(stats['medianISO'])}, exposure {_exposure(stats['medianExposureDurationSeconds'])}, bias "
            f"{_f(stats['medianExposureBiasEV'])} EV, peak {_f(stats['medianPeakValue'])} "
            f"({stats['peakValueFrames']} frames){few}")


def render_lines(section: Any, max_winner_rows: int = 24, max_other_clusters: int = 6) -> list[str]:
    lines = ["## 背景誤検出の証拠(emitter / 露出)", ""]
    try:
        if not isinstance(section, dict):
            return lines + ["- n/a"]
        lines.append("- **evidence only, NOT ground truth**: likelyBackground は hint。ORIGINAL PNG で確認する")
        if not section["emitterEvidenceAvailable"] and not section["exposure"]["available"]:
            lines.append(f"- n/a — no `emitterDiagnostics` / geometry `emitter` / frame `camera` in "
                         f"{section['contextsRead']} contexts (bundle predates 31ad94c, the recorder had no "
                         "emitter evidence or camera source, or compaction dropped them); selected-frame winners "
                         f"{len(section['winners'])}, none with evidence")
            if section["emitterCompaction"]:
                lines.append(f"- emitter compaction steps: {_f(json.dumps(section['emitterCompaction']))}")
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
        winners = section["winners"]
        lines.append(f"### selected-frame winners ({len(winners)})")
        lines.append("")
        if not winners:
            lines.append("- none (no selected frame with a detected winner)")
        else:
            lines.append("| frame | color | cluster | src | emitterScore | margin to 0.42 | dominant terms "
                         "| hasEmitterCore (hv/pm/cw) | peak "
                         "| ISO | exposure |")
            lines.append("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |")
            for w in winners[:max_winner_rows]:
                e = w["evidence"]
                cluster = "n/a" if w["clusterID"] is None else (
                    f"{w['clusterID']}" + (" **BG?**" if w["likelyBackground"] else ""))
                camera = w["camera"] or {}
                if e is None:
                    lines.append(f"| {w['frameID']} | {w['color']} | {cluster} | n/a | n/a | n/a | n/a | n/a "
                                 f"| {_f(w['peakValue'])} | {_f(camera.get('iso'))} "
                                 f"| {_exposure(camera.get('exposureDurationSeconds'))} |")
                    continue
                lines.append(f"| {w['frameID']} | {w['color']} | {cluster} | "
                             f"{'full' if e['evidenceSource'] == 'decisionTrace' else 'compact'} | "
                             f"{_f(e['emitterScore'])} | {_signed(e['emitterScoreMargin'])} | {_dominant(e)} | "
                             f"{_core(e)} | {_f(w['peakValue'])} | "
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
            for label, key, groups in (("exposure by production eligibility", "exposureByEligibility",
                                        ("eligible", "ineligible")),):
                split = c[key]
                if not any(split[g]["framesWithCamera"] for g in groups):
                    lines.append(f"  - {label}: n/a (no camera on this cluster's frames)")
                    continue
                lines.append(f"  - {label} (evidence, not ground truth):")
                for group in groups:
                    lines.append(f"    - {_group_line(group, split[group])}")
        for c in others[:max_other_clusters]:
            score = c["emitterScore"] or {}
            lines.append(f"- {c['clusterID']} (not flagged, holds a winner): frames {c['framesPresent']}, "
                         f"emitterScore median {_f(score.get('median'))}, dominant term "
                         f"{json.dumps(c['dominantTermCounts'])}")
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
