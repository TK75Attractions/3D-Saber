#!/usr/bin/env python3
"""One-page, local, read-only summary of a PhoneSaber Debug Recording triage bundle.

Free and offline: no Codex or other model call. It reuses the existing helpers
(input contract, bridge summary, CASE A/B/C audit, tracking preflight and the
offline selection replay) and never writes into the bundle, because the bundle
input contract rejects any extra file. Fields absent from older bundles are
shown as ``n/a``.

    phone_saber_session_report.py <bundle_dir> [--output report.md] [--json]

Everything here is evidence for a human. Replay margins/holds are comparison
inputs, not production values, and the CASE hints never feed the repair gate.
"""
from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
from collections import Counter
from pathlib import Path
from typing import Any, Callable

from phone_saber_hotspots import static_hotspots
from phone_saber_selection_replay import Policy, gap_distribution, load_sequences, quantiles, replay
from phone_saber_tracking_diagnostics import (
    BRIDGE_ANNOTATED_ROLE,
    BRIDGE_ROLES,
    bridge_summary,
    candidate_selection_audit,
    tracking_preflight,
)
from phone_saber_triage_codex import input_plan
from phone_saber_triage_protocol import MAX_IMAGES, MAX_SUMMARY_BYTES

NA = "n/a"
MIB = 1024 * 1024
# DebugRecordingTriageLimits.maximumRetainedBGRABytes; a bundle's own
# motionEventSummary.thresholds.maximumRetainedBGRABytes is preferred when present.
DEFAULT_MEMORY_BUDGET_BYTES = 256 * MIB
DEFAULT_REPLAY_MARGINS = (0.5, 1.0, 2.0, 5.0)
DEFAULT_REPLAY_HOLDS = (1, 3)
REPLAY_MAX_DISTANCE = 0.5
REPLAY_MIN_IOU = 0.2
REPLAY_JUMP_PX = 100.0
REPORT_SUFFIX = ".report.md"


def _get(value: Any, *keys: str) -> Any:
    for key in keys:
        if not isinstance(value, dict):
            return None
        value = value.get(key)
    return value


def _fmt(value: Any) -> str:
    if value is None:
        return NA
    if isinstance(value, bool):
        return "true" if value else "false"
    if isinstance(value, float):
        return f"{value:.4g}"
    if isinstance(value, (dict, list)):
        return json.dumps(value, ensure_ascii=False, separators=(",", ":"))
    return str(value)


def _mib(value: Any) -> str:
    if isinstance(value, bool) or not isinstance(value, (int, float)):
        return NA
    return f"{value / MIB:.1f} MiB"


def _guard(errors: list[str], label: str, function: Callable[[], Any]) -> Any:
    """Run one section; a failure is recorded and shown as n/a, never raised."""
    try:
        return function()
    except Exception as exc:  # noqa: BLE001 - a report must survive any old bundle
        errors.append(f"{label}: {type(exc).__name__}: {exc}")
        return None


def _read_summary(bundle: Path) -> dict:
    path = bundle / "summary.json"
    if path.is_symlink() or not path.is_file():
        raise ValueError("summary.json is missing")
    if path.stat().st_size > MAX_SUMMARY_BYTES:
        raise ValueError("summary.json exceeds its size limit")
    value = json.loads(path.read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise ValueError("summary.json is not an object")
    return value


def memory_section(summary: dict) -> dict:
    runtime = _get(summary, "motionEventSummary", "runtime")
    budget = _get(summary, "motionEventSummary", "thresholds", "maximumRetainedBGRABytes")
    source = "motionEventSummary.thresholds.maximumRetainedBGRABytes"
    if isinstance(budget, bool) or not isinstance(budget, int) or budget <= 0:
        budget, source = DEFAULT_MEMORY_BUDGET_BYTES, "current Swift default (not recorded in bundle)"
    headroom = _get(runtime, "memoryHeadroom")
    result = {"peakRetainedBGRABytes": _get(runtime, "peakRetainedBGRABytes"),
              "budgetBytes": budget, "budgetSource": source, "memoryHeadroom": headroom}
    if not isinstance(runtime, dict):
        result["verdict"] = "n/a — motionEventSummary.runtime is not in this bundle"
    elif not isinstance(headroom, dict):
        result["verdict"] = "n/a — bundle predates memoryHeadroom (no os_proc_available_memory sample)"
    elif headroom.get("available") is not True:
        result["verdict"] = ("unavailable — os_proc_available_memory is not provided "
                             "(Simulator / macOS host); judge headroom on a real iPhone recording")
    else:
        minimum = headroom.get("minimumAvailableBytes")
        retained = headroom.get("retainedBGRABytesAtMinimum")
        if isinstance(minimum, bool) or not isinstance(minimum, (int, float)):
            result["verdict"] = "n/a — memoryHeadroom.available=true but minimumAvailableBytes is missing"
        else:
            retained = retained if isinstance(retained, (int, float)) and not isinstance(retained, bool) else 0
            unused = max(budget - retained, 0)
            if minimum >= budget:
                level = "OK"
                detail = f"minimum available {_mib(minimum)} >= whole budget {_mib(budget)}"
            elif minimum >= unused:
                level = "OK (tight)"
                detail = (f"minimum available {_mib(minimum)} covers the unused budget "
                          f"{_mib(unused)} (budget {_mib(budget)} - retained {_mib(retained)})")
            else:
                level = "WARNING"
                detail = (f"minimum available {_mib(minimum)} < unused budget {_mib(unused)} "
                          f"(budget {_mib(budget)} - retained {_mib(retained)}); filling the "
                          "retained-BGRA budget could exhaust memory")
            result["verdict"] = (f"{level} — {detail}; at frame "
                                 f"{_fmt(headroom.get('minimumFrameID'))}")
    return result


def _frame_key(image: dict) -> tuple[int, str]:
    frame_id = image.get("frameID")
    if isinstance(frame_id, int) and not isinstance(frame_id, bool):
        return (frame_id, "")
    return (sys.maxsize, str(frame_id))


def tracking_section(summary: dict) -> dict:
    motion = _get(summary, "motionEventSummary") or {}
    ledger = motion.get("events") if isinstance(motion, dict) else None
    ledger = [e for e in ledger if isinstance(e, list) and len(e) == 4] if isinstance(ledger, list) else []
    images = summary.get("images") if isinstance(summary.get("images"), list) else []
    tracking_images = [i for i in images if isinstance(i, dict)
                       and str(i.get("failureType", "")).endswith("tracking_instability")]
    return {
        "trackingCapture": motion.get("trackingCapture") if isinstance(motion, dict) else None,
        "trackingWindow": _get(summary, "bridgeDropoutSummary", "trackingWindow"),
        "selectedTrackingFrames": [{"frameID": i.get("frameID"), "role": i.get("role"),
                                    "color": i.get("color"), "eventIndex": i.get("eventIndex")}
                                   for i in sorted(tracking_images, key=_frame_key)],
        "ledgerCodes": dict(Counter(str(e[3]) for e in ledger)) if ledger else None,
        "bridgePriorityEvents": [{"eventIndex": e[0], "color": e[1], "peakScore": e[2]}
                                 for e in ledger if e[3] == "bridge_priority"],
    }


def replay_section(bundle: Path, margins: tuple[float, ...], holds: tuple[int, ...]) -> dict:
    sequences = load_sequences([bundle])
    correspondence = Policy(margins[0] if margins else 0.0, REPLAY_MAX_DISTANCE, REPLAY_MIN_IOU, 1)
    rows = gap_distribution(sequences, correspondence)
    runs = []
    for margin in margins:
        for hold in holds:
            outcome = replay(sequences, Policy(margin, REPLAY_MAX_DISTANCE, REPLAY_MIN_IOU, hold),
                             REPLAY_JUMP_PX)
            runs.append({"margin": margin, "hold": hold,
                         "framesCompared": outcome["framesCompared"],
                         "jumpsRecorded": outcome[f"jumpsAtLeast{REPLAY_JUMP_PX:g}pxRecorded"],
                         "jumpsReplay": outcome[f"jumpsAtLeast{REPLAY_JUMP_PX:g}pxReplay"],
                         "changedFrames": [c["frameID"] for c in outcome["changedFrames"]]})
    return {"sequences": len(sequences),
            "framesWithCompleteEligibleLists": sum(len(v) for v in sequences.values()),
            "correspondence": f"maxDistance={REPLAY_MAX_DISTANCE:g} minIoU={REPLAY_MIN_IOU:g}",
            "jumpPx": REPLAY_JUMP_PX,
            "switchEvents": rows,
            "switchOutScoreGap": quantiles([r["scoreGap"] for r in rows if not r["returnsToEarlierWinner"]]),
            "sweep": runs}


def images_to_open(summary: dict, audit: list[dict] | None, plan: Any) -> dict:
    images = [i for i in summary.get("images") or [] if isinstance(i, dict)]
    def names(predicate: Callable[[dict], bool]) -> list[str]:
        return [str(i.get("path")) for i in images if predicate(i)]
    tracking_first = names(lambda i: i.get("role") in {"peak", "onset"}
                           and str(i.get("failureType", "")).endswith("tracking_instability"))
    bridge_originals = names(lambda i: "bridgeEventID" in i and i.get("role") in BRIDGE_ROLES)
    annotated = names(lambda i: "bridgeEventID" in i and i.get("role") == BRIDGE_ANNOTATED_ROLE)
    case_images: list[str] = []
    if audit and plan is not None:
        by_id = {image.image_id: image for image in plan.images}
        for row in audit:
            image = by_id.get(row.get("imageID"))
            if row.get("hint") in {"A", "B", "C"} and image is not None:
                path = image.image_path.relative_to(plan.root).as_posix()
                if path not in case_images:
                    case_images.append(path)
    listed = set(tracking_first) | set(bridge_originals) | set(annotated) | set(case_images)
    return {"trackingPeakOnset": tracking_first, "bridgeOriginals": bridge_originals,
            "caseHintFrames": case_images, "annotatedViewingAidOnly": annotated,
            "other": [p for p in names(lambda i: True) if p not in listed]}


def build_report(bundle: Path, *, margins: tuple[float, ...] = DEFAULT_REPLAY_MARGINS,
                 holds: tuple[int, ...] = DEFAULT_REPLAY_HOLDS) -> dict:
    """Read-only. Every section degrades to None/n/a instead of raising."""
    errors: list[str] = []
    summary = _guard(errors, "summary.json", lambda: _read_summary(bundle)) or {}
    plan = None
    contract_error = None
    try:
        plan = input_plan(bundle, max_images=MAX_IMAGES, allow_reports=True)
    except Exception as exc:  # noqa: BLE001 - shown in the report
        contract_error = f"{type(exc).__name__}: {exc}"
    audit = _guard(errors, "candidate_selection_audit",
                   lambda: candidate_selection_audit(plan)) if plan is not None else None
    # One row per (frameID, color): a frame selected for two roles (e.g. bridge and
    # tracking) must not be counted twice — the audit marks the row to tally.
    tallied = [r for r in audit or [] if r.get("countForTally", True)]
    hint_counts = dict(Counter(str(r.get("hint")) for r in tallied)) if audit is not None else None
    report = {
        "bundle": str(bundle),
        "sessionID": summary.get("sessionID") or (plan.session_id if plan else None),
        "recordedFrameCount": summary.get("recordedFrameCount"),
        "retainedIncidentContextFrames": summary.get("retainedIncidentContextFrames"),
        "selectedImageCount": summary.get("selectedImageCount"),
        "activeColors": summary.get("activeColors"),
        "redBlueDetectionSummary": summary.get("redBlueDetectionSummary"),
        "dropoutSummary": summary.get("dropoutSummary"),
        "inputContract": "PASS" if plan is not None else f"FAIL ({contract_error})",
        "memory": _guard(errors, "memory", lambda: memory_section(summary)),
        "tracking": _guard(errors, "tracking", lambda: tracking_section(summary)),
        "bridgeDropoutSummary": summary.get("bridgeDropoutSummary"),
        "bridgeEvents": _guard(errors, "bridge_summary",
                               lambda: bridge_summary(plan)) if plan is not None else None,
        "caseHintCounts": hint_counts,
        "caseHints": [r for r in tallied if r.get("hint") != "none"] if audit is not None else None,
        "selectionReplay": _guard(errors, "selection_replay",
                                  lambda: replay_section(bundle, margins, holds)),
        "staticHotspots": _guard(errors, "static_hotspots", lambda: static_hotspots(bundle)),
        "trackingPreflight": _guard(errors, "tracking_preflight",
                                    lambda: tracking_preflight(plan)) if plan is not None else None,
        "imagesToOpen": _guard(errors, "images", lambda: images_to_open(summary, audit, plan)),
        "errors": errors,
    }
    return report


# --- Markdown ---------------------------------------------------------------

def _color_table(report: dict) -> list[str]:
    detection, dropout = report.get("redBlueDetectionSummary"), report.get("dropoutSummary")
    if not isinstance(detection, dict) and not isinstance(dropout, dict):
        return ["redBlueDetectionSummary / dropoutSummary: n/a"]
    lines = ["| color | frames | detectedFrames | missedFrames | candidateZeroFrames | eligibleZeroFrames "
             "| dropoutTransitions | recoveredTransitions | falseFrames |",
             "| --- | --- | --- | --- | --- | --- | --- | --- | --- |"]
    for color in ("red", "blue"):
        d, o = _get(detection, color) or {}, _get(dropout, color) or {}
        if d.get("excludedFromDiagnosis") or o.get("excludedFromDiagnosis"):
            lines.append(f"| {color} | excludedFromDiagnosis | | | | | | | |")
            continue
        lines.append("| " + " | ".join([color] + [_fmt(d.get(k)) for k in (
            "frames", "detectedFrames", "missedFrames", "candidateZeroFrames", "eligibleZeroFrames")]
            + [_fmt(o.get(k)) for k in ("dropoutTransitions", "recoveredTransitions", "falseFrames")]) + " |")
    return lines


HOTSPOT_LIST_MIN_FRAMES = 3
HOTSPOT_LIST_MAX_PER_COLOR = 6


def _hotspot_lines(hotspots: Any) -> list[str]:
    lines = ["## 静的ホットスポット(背景誤検出の候補 — hint のみ、gate には使わない)", ""]
    try:
        if not isinstance(hotspots, dict):
            return lines + ["- n/a"]
        if not hotspots.get("framesWithPositions"):
            return lines + ["- n/a — no candidate geometry or winner positions in this bundle"]
        size = hotspots.get("imageSize") or [None, None]
        lines.append(f"- frame-color pairs with positions: {hotspots['framesWithPositions']} "
                     f"(sources {_fmt(hotspots.get('frameSources'))}), image {_fmt(size[0])}x{_fmt(size[1])}, "
                     f"likelyBackground clusters: {hotspots.get('likelyBackgroundCount')}")
        if hotspots.get("winnersOnly"):
            lines.append("- winners only (bundle predates candidateGeometry): a static object that was "
                         "eligible but lost is not visible here")
        lines.append("- likelyBackground = 同じ位置に長く居座る eligible/winning 候補。静止した本物の saber も"
                     "該当し得るので ORIGINAL PNG で確認する")
        for color in ("red", "blue"):
            clusters = (hotspots.get("clusters") or {}).get(color) or []
            shown = [c for c in clusters if c.get("likelyBackground")
                     or c.get("framesPresent", 0) >= HOTSPOT_LIST_MIN_FRAMES][:HOTSPOT_LIST_MAX_PER_COLOR]
            if not shown:
                lines.append(f"- {color}: no cluster with ≥{HOTSPOT_LIST_MIN_FRAMES} frames "
                             f"({len(clusters)} clusters total)")
                continue
            for c in shown:
                flag = "**LIKELY BACKGROUND**" if c.get("likelyBackground") else "static? no"
                failed = [k for k, ok in (c.get("checks") or {}).items() if not ok]
                lines.append(
                    f"- {c.get('clusterID')} {flag}: centroid {_fmt(c.get('centroid'))} bbox {_fmt(c.get('bbox'))}, "
                    f"frames {c.get('framesPresent')} (windows {c.get('windows')}, span {_fmt(c.get('spanSeconds'))}s, "
                    f"coverage {_fmt(c.get('coverage'))}), "
                    f"eligible {_fmt(c.get('eligibleFraction'))}, winning {_fmt(c.get('winningFraction'))}, "
                    f"score max/median {_fmt(c.get('maxFinalScore'))}/{_fmt(c.get('medianFinalScore'))}, "
                    f"jitter {_fmt(c.get('centroidJitterPx'))}px, types {_fmt(c.get('sourceTypes'))}"
                    + (f", failed checks {_fmt(failed)}" if failed else ""))
                elsewhere = c.get("winnerElsewhereFrames") or []
                if elsewhere:
                    lines.append(f"  - same-color winner elsewhere in this span: frames {_fmt(elsewhere)}")
                images = c.get("selectedImages") or []
                lines.append("  - selected images: " + (", ".join(f"`{p}`" for p in images) if images else "none"))
    except Exception as exc:  # noqa: BLE001 - rendering must never break the report
        lines.append(f"- n/a ({type(exc).__name__}: {exc})")
    return lines


def render_markdown(report: dict) -> str:
    out: list[str] = []
    add = out.append
    add(f"# PhoneSaber session report — {_fmt(report.get('sessionID'))}")
    add("")
    add("ローカル・無料・read-only の要約(Codex/有料 model 呼び出しなし)。数値は根拠であり、"
        "production の閾値ではない。判断は ORIGINAL PNG で行う。")
    add("")
    add(f"- bundle: `{report.get('bundle')}`")
    add(f"- input contract (`input_plan`, allow_reports): {report.get('inputContract')}")
    add("")
    add("## セッション概要")
    add("")
    for key in ("sessionID", "recordedFrameCount", "retainedIncidentContextFrames",
                "selectedImageCount", "activeColors"):
        add(f"- {key}: {_fmt(report.get(key))}")
    add("")
    out.extend(_color_table(report))
    add("")

    add("## メモリ (motionEventSummary.runtime)")
    add("")
    memory = report.get("memory") or {}
    headroom = memory.get("memoryHeadroom") if isinstance(memory.get("memoryHeadroom"), dict) else {}
    add(f"- peakRetainedBGRABytes: {_mib(memory.get('peakRetainedBGRABytes'))}")
    add(f"- retained-BGRA budget: {_mib(memory.get('budgetBytes'))} ({memory.get('budgetSource', NA)}; "
        f"current Swift budget {_mib(DEFAULT_MEMORY_BUDGET_BYTES)})")
    add(f"- memoryHeadroom.available: {_fmt(headroom.get('available'))} "
        f"(samples {_fmt(headroom.get('samples'))})")
    add(f"- memoryHeadroom.minimumAvailableBytes: {_mib(headroom.get('minimumAvailableBytes'))}")
    add(f"- memoryHeadroom.minimumFrameID: {_fmt(headroom.get('minimumFrameID'))}")
    add(f"- memoryHeadroom.retainedBGRABytesAtMinimum: {_mib(headroom.get('retainedBGRABytesAtMinimum'))}")
    add(f"- **verdict: {memory.get('verdict', NA)}**")
    add("")

    add("## Tracking event")
    add("")
    tracking = report.get("tracking") or {}
    capture = tracking.get("trackingCapture") if isinstance(tracking.get("trackingCapture"), dict) else None
    if capture is None:
        add("- trackingCapture: n/a")
    else:
        add(f"- recording max: frame {_fmt(capture.get('recordingMaxFrameID'))} "
            f"{_fmt(capture.get('recordingMaxColor'))} score {_fmt(capture.get('recordingMaxScore'))}")
        add(f"- retained peak: frame {_fmt(capture.get('retainedPeakFrameID'))} "
            f"{_fmt(capture.get('retainedPeakColor'))} score {_fmt(capture.get('retainedPeakScore'))}")
        add(f"- temporalFramesRetained: {_fmt(capture.get('temporalFramesRetained'))}, "
            f"contextIncomplete: {_fmt(capture.get('contextIncomplete'))}, "
            f"highestRankedFrameMissing: {_fmt(capture.get('highestRankedFrameMissing'))}")
    window = tracking.get("trackingWindow")
    add(f"- bridgeDropoutSummary.trackingWindow: "
        + (f"selected {_fmt(window.get('selected'))} / available {_fmt(window.get('available'))}"
           if isinstance(window, dict) else "n/a (not trimmed, or bundle predates bridge selection)"))
    frames = tracking.get("selectedTrackingFrames") or []
    add("- selected tracking frames: " + (", ".join(
        f"{_fmt(f['frameID'])}({_fmt(f['role'])})" for f in frames) if frames else "none"))
    add(f"- ledger selection codes: {_fmt(tracking.get('ledgerCodes'))}")
    priority = tracking.get("bridgePriorityEvents") or []
    add("- bridge_priority: " + (", ".join(
        f"event {e['eventIndex']} {e['color']} score {_fmt(e['peakScore'])}" for e in priority)
        if priority else ("n/a (no selection-code ledger in this bundle)" if tracking.get("ledgerCodes") is None
                          else "none (tracking event was not yielded to bridge events)")))
    add("")

    add("## Bridge dropout events")
    add("")
    bridge_meta = report.get("bridgeDropoutSummary")
    if isinstance(bridge_meta, dict):
        add("- bridgeDropoutSummary: " + _fmt({k: v for k, v in bridge_meta.items() if k != "trackingWindow"}))
    else:
        add("- bridgeDropoutSummary: n/a (bundle predates bridge dropout diagnostics)")
    rows = report.get("bridgeEvents")
    if rows is None:
        add("- bridge events: n/a")
    elif not rows:
        add("- bridge events: none selected")
    else:
        add("")
        add("| bridgeEventID | color | frames (before/dropout/after) | missingFrameCount | gapSeconds "
            "| annotatedImage | temporalEvidence |")
        add("| --- | --- | --- | --- | --- | --- | --- |")
        for row in rows:
            add(f"| {row['bridgeEventID']} | {row['color']} | {'/'.join(map(str, row['frames']))} | "
                f"{_fmt(row['missingFrameCount'])} | {_fmt(row['gapSeconds'])} | "
                f"{_fmt(row['annotatedImage'])} | {row['temporalEvidence']} |")
    add("")

    add("## CASE hints (candidate_selection_audit — hint のみ、gate には使わない)")
    add("")
    counts = report.get("caseHintCounts")
    if counts is None:
        add("- n/a")
    elif not counts:
        add("- no rows (no selected frame has candidate geometry with a previous winner; "
            "bundles before geometry recording are always empty)")
    else:
        add("- counts: " + ", ".join(f"{k}={v}" for k, v in sorted(counts.items())))
        for row in report.get("caseHints") or []:
            extra = {k: row[k] for k in ("scoreMargin", "bCause", "endpointDiscontinuity",
                                         "candidatesTruncated") if k in row}
            add(f"  - CASE {row['hint']} frame {row['frameID']} {row['color']} ({row['imageID']}): "
                f"{row.get('detail')} {_fmt(extra)}")
    add("")

    add("## Selection replay (evidence only — NOT production values)")
    add("")
    replay_info = report.get("selectionReplay")
    if not isinstance(replay_info, dict):
        add("- n/a")
    elif not replay_info["sequences"]:
        add("- n/a — no complete eligible candidate lists (bundle predates candidate geometry recording)")
    else:
        add(f"- sequences: {replay_info['sequences']}, frame-color pairs with complete eligible lists: "
            f"{replay_info['framesWithCompleteEligibleLists']}, correspondence: {replay_info['correspondence']}")
        add(f"- recorded switches (R breaks continuity, eligible M continues it): "
            f"{len(replay_info['switchEvents'])}; switch-out score gap R−M: "
            f"{_fmt(replay_info['switchOutScoreGap'])}")
        for event in replay_info["switchEvents"]:
            add(f"  - frame {event['frameID']} {event['color']}: scoreGap {event['scoreGap']}, "
                f"winnerJump {event['winnerJumpPx']}px vs matching {event['matchingJumpPx']}px"
                + (" (returns to earlier winner)" if event["returnsToEarlierWinner"] else ""))
        add("")
        add(f"| margin | hold | framesCompared | jumps ≥{replay_info['jumpPx']:g}px recorded | replay "
            "| changed frames |")
        add("| --- | --- | --- | --- | --- | --- |")
        for run in replay_info["sweep"]:
            add(f"| {run['margin']:g} | {run['hold']} | {run['framesCompared']} | {run['jumpsRecorded']} | "
                f"{run['jumpsReplay']} | {_fmt(run['changedFrames'])} |")
    add("")

    out.extend(_hotspot_lines(report.get("staticHotspots")))
    add("")

    add("## tracking_preflight")
    add("")
    preflight = report.get("trackingPreflight")
    if not isinstance(preflight, dict):
        add("- n/a")
    else:
        add(f"- status: **{preflight.get('status')}**")
        add(f"- reasonCodes: {_fmt(preflight.get('reasonCodes'))}")
        add(f"- eventModes: {_fmt(preflight.get('eventModes'))}")
    add("")

    add("## 確認チェックリスト (CLAUDE.md §5)")
    add("")
    add("- [ ] 1. bridge / tracking event が意図どおり選ばれているか(tracking 窓の枚数、bridge_priority)")
    add("- [ ] 2. **ORIGINAL PNG** で本物の点灯 saber の位置を確認する。annotated は ground truth ではない")
    add("- [ ] 3. event frame の全 eligible candidate と winner を照合する(eligibleRank、matchToPreviousWinner、score 差)")
    add("- [ ] 4. CASE A / B / C を分類する(unknownTruncated は断定しない)")
    add("- [ ] 5. 判定と根拠(session 名、frame、数値)を STATUS.md に追記する")
    add("- [ ] 6. CASE B が多ければ、candidate 生成前に落ちた component の記録を次の diagnostics にする")
    add("")
    add("### 最初に開く画像")
    add("")
    images = report.get("imagesToOpen")
    if not isinstance(images, dict):
        add("- n/a")
    else:
        for key, label in (("trackingPeakOnset", "tracking peak / onset (original)"),
                           ("bridgeOriginals", "bridge dropout originals"),
                           ("caseHintFrames", "CASE A/B/C hint frames"),
                           ("annotatedViewingAidOnly", "annotated (viewing aid only, NOT ground truth)"),
                           ("other", "other selected images")):
            paths = images.get(key) or []
            add(f"- {label}: " + (", ".join(f"`{p}`" for p in paths) if paths else "none"))
    if report.get("errors"):
        add("")
        add("## Report notes")
        add("")
        for error in report["errors"]:
            add(f"- {error}")
    add("")
    return "\n".join(out)


# --- Output -----------------------------------------------------------------

def _inside(path: Path, root: Path) -> bool:
    try:
        return path.resolve().is_relative_to(root.resolve())
    except OSError:
        return False


def write_text_atomic(path: Path, text: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    handle, temporary = tempfile.mkstemp(prefix=f".{path.name}.", suffix=".tmp", dir=path.parent)
    try:
        with os.fdopen(handle, "w", encoding="utf-8") as stream:
            stream.write(text)
        os.replace(temporary, path)
    except BaseException:
        Path(temporary).unlink(missing_ok=True)
        raise


def report_path_for(bundle: Path) -> Path:
    """Sibling of the bundle, never inside it (the bundle contract rejects extra files)."""
    return bundle.parent / f"{bundle.name}{REPORT_SUFFIX}"


def write_session_report(bundle: Path, destination: Path | None = None) -> Path:
    destination = destination or report_path_for(bundle)
    if _inside(destination, bundle):
        raise ValueError("the report must be written outside the bundle")
    write_text_atomic(destination, render_markdown(build_report(bundle)))
    return destination


def _floats(text: str) -> tuple[float, ...]:
    return tuple(float(x) for x in text.split(",") if x.strip())


def parse_args(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("bundle", type=Path, help="triage bundle directory (read-only)")
    parser.add_argument("--output", type=Path, help="write the Markdown report here (outside the bundle)")
    parser.add_argument("--json", action="store_true", help="print the report data as JSON instead of Markdown")
    parser.add_argument("--margins", default=",".join(f"{m:g}" for m in DEFAULT_REPLAY_MARGINS),
                        help="replay score margins to compare (evidence only)")
    parser.add_argument("--holds", default=",".join(str(h) for h in DEFAULT_REPLAY_HOLDS),
                        help="replay hold frames to compare (evidence only)")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if not args.bundle.is_dir():
        print(f"not a bundle directory: {args.bundle}", file=sys.stderr)
        return 2
    if args.output is not None and _inside(args.output, args.bundle):
        print("--output must be outside the bundle (the bundle input contract rejects extra files)",
              file=sys.stderr)
        return 2
    try:
        margins = _floats(args.margins)
        holds = tuple(int(h) for h in _floats(args.holds))
    except ValueError:
        print("--margins / --holds must be comma separated numbers", file=sys.stderr)
        return 2
    report = build_report(args.bundle, margins=margins, holds=holds)
    if args.json:
        text = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
    else:
        text = render_markdown(report)
    if args.output is not None:
        write_text_atomic(args.output, text)
        print(f"report written: {args.output}")
    else:
        sys.stdout.write(text)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
