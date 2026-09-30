#!/usr/bin/env python3
"""Conservative, restart-safe PhoneSaber recognition repair on main."""

from __future__ import annotations

import argparse
import fcntl
import hashlib
import json
import math
import os
import shutil
import subprocess
import sys
import time
import re
from pathlib import Path
from typing import Any

from phone_saber_triage_codex import (
    ANALYSIS_MODEL,
    ANALYSIS_REASONING_EFFORT,
    ESCALATION_MODEL,
    ESCALATION_REASONING_EFFORT,
    CODEX_TIMEOUT_SECONDS,
    MAX_REPORT_BYTES,
    MAX_CODEX_CONTEXT_BYTES,
    CodexModelUnavailable,
    _validate_analysis,
    find_codex_binary,
    input_plan,
    is_model_unavailable,
)
from phone_saber_triage_protocol import BundleError
from phone_saber_tracking_diagnostics import temporal_events, sufficient_temporal, supports_temporal_images, tracking_repair_required


REPO_ROOT = Path(__file__).resolve().parents[3]
MAX_ATTEMPTS = 2
VERIFY_TIMEOUT_SECONDS = 3600
GIT_TIMEOUT_SECONDS = 90
REPAIR_MODEL = "gpt-6-sol"
REPAIR_REASONING_EFFORT = "high"
REPAIR_CODEX_TIMEOUT_SECONDS = 900
REVIEW_MODEL = "gpt-6-sol"
REVIEW_REASONING_EFFORT = "high"
REVIEW_CODEX_TIMEOUT_SECONDS = 900
REVIEW_NEGATIVE_CONTROLS = {
    "BLUE": ("no_blade_blue_103", "background_blue_131", "blue_broad_coreless_64"),
    "RED": ("no_blade_red_103", "background_red_131", "red_hard_negative_348"),
}
TERMINAL_STATUSES = {
    "needs_capture", "repair_failed", "blocked", "blocked_remote_changed",
    "repair_pushed", "dry_run", "MODEL_UNAVAILABLE", "BLOCKED_BASELINE_UNSTABLE",
}
REPAIR_FILES = (
    "ios/PhoneSaberSender/PhoneSaberSender/DetectionCore.swift",
    "ios/PhoneSaberSender/PhoneSaberSender/BGRADetection.swift",
    "ios/PhoneSaberSenderTests/DetectionCoreTests.swift",
    "ios/PhoneSaberSenderTests/StaticBGRADetectionTests.swift",
)
REQUIRED_VERIFY_STAGES = (
    "iOS XCTest", "Detection", "Lossless", "Tools", "iOS Release", "Diff Check",
)
REPAIR_RESPONSE_SCHEMA = {
    "type": "object", "additionalProperties": False,
    "properties": {
        "decision": {"type": "string", "enum": ["changes_made", "needs_more_evidence"]},
        "summary": {"type": "string"},
    },
    "required": ["decision", "summary"],
}
REVIEW_RESPONSE_SCHEMA = {
    "type": "object", "additionalProperties": False,
    "properties": {
        "decision": {"type": "string", "enum": ["approved", "rejected", "needs_more_evidence"]},
        "reason": {"type": "string"},
        "risk_notes": {"type": "array", "items": {"type": "string"}},
    },
    "required": ["decision", "reason", "risk_notes"],
}


class RepairError(RuntimeError):
    """A repair step failed without authorizing a production commit."""


class RepairTimeout(RepairError):
    pass


class RepairModelUnavailable(RepairError):
    pass


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for chunk in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _json_file(path: Path, maximum: int = MAX_REPORT_BYTES) -> Any:
    if path.is_symlink() or not path.is_file() or path.stat().st_size > maximum:
        raise RepairError(f"missing or oversized JSON: {path.name}")
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise RepairError(f"malformed JSON: {path.name}") from exc


def _atomic_text(path: Path, content: str) -> None:
    temporary = path.with_name(f".{path.name}.tmp")
    with temporary.open("w", encoding="utf-8") as output:
        output.write(content)
        output.flush()
        os.fsync(output.fileno())
    os.replace(temporary, path)


def _atomic_json(path: Path, value: Any) -> None:
    _atomic_text(path, json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n")


def _analysis_sections() -> tuple[str, ...]:
    return ("false_negatives", "wrong_candidate_and_endpoint_errors",
            "false_positive_suspects", "other_findings")


def load_analysis(bundle: Path, plan: Any) -> dict[str, Any]:
    """Reject a forged or stale report before using it as a repair input."""
    report = _json_file(bundle / "analysis_report.json")
    markdown = bundle / "analysis_report.md"
    if markdown.is_symlink() or not markdown.is_file() or markdown.stat().st_size > MAX_REPORT_BYTES:
        raise RepairError("analysis_report.md is missing or oversized")
    if not isinstance(report, dict) or report.get("formatVersion") not in {2, 3} \
            or report.get("sessionID") != plan.session_id:
        raise RepairError("analysis report version or session does not match the bundle")
    details = report.get("input")
    if not isinstance(details, dict) or details.get("imageCount") != len(plan.images) \
            or details.get("videoIncluded") is not False \
            or details.get("fullMetadataIncluded") is not False:
        raise RepairError("analysis report input declaration is invalid")
    references = details.get("imageReferences")
    expected = [
        {"id": image.image_id, "path": image.image_path.relative_to(plan.root).as_posix(),
         "contextPath": image.context_path.relative_to(plan.root).as_posix(),
         "frameID": image.frame_id, "incidentType": image.failure_type}
        for image in plan.images
    ]
    if references != expected or details.get("imagePaths") != [item["path"] for item in expected] \
            or details.get("contextPaths") != [item["contextPath"] for item in expected]:
        raise RepairError("analysis report image IDs do not match selected PNGs and contexts")
    analysis = report.get("analysis")
    if not isinstance(analysis, dict):
        raise RepairError("analysis report lacks structured findings")
    checked = analysis if report["formatVersion"] == 3 else {
        **analysis,
        "repair_assessment": {
            "decision": "needs_capture", "visible_saber_confirmed": False,
            "production_change_supported": False, "root_cause_stage": "unknown",
            "diagnosis_consistent_with_metadata": False, "change_type": "none",
            "independent_visual_examples": 0, "affected_colors": [],
            "evidence_image_ids": [], "reason": "Legacy report has no structured repair assessment.",
        },
    }
    try:
        _validate_analysis(checked, set(plan.image_ids))
    except BundleError as exc:
        raise RepairError(f"invalid analysis report: {exc}") from exc
    return report


def _corpus_coverage(repo: Path, colors: list[str], stage: str) -> tuple[bool, str]:
    manifest = repo / "ios/PhoneSaberSender/Tools/lossless_regression_manifest.json"
    try:
        document = _json_file(manifest, 2 * 1024 * 1024)
        fixtures = document["fixtures"]
        if document.get("schemaVersion") != 1 or not isinstance(fixtures, list) \
                or len(fixtures) < 40 or set(document.get("failureClasses", {})) != set("ABCDEFG"):
            return False, "formal 40-case corpus is incomplete"
        for color in colors:
            if not any(item.get("color") == color and item.get("truth") == "positive"
                       for item in fixtures) or not any(
                           item.get("color") == color and item.get("truth") == "negative"
                           for item in fixtures):
                return False, f"formal corpus lacks {color} positive or negative coverage"
        stage_classes = {
            "segmentation": {"A", "E"},
            "candidate_generation": {"B", "C", "D", "F", "G"},
            "eligibility": {"C", "D", "F", "G"},
            "ranking": {"C", "D", "F", "G"},
            "endpoint": {"D", "G"},
        }
        required = stage_classes.get(stage)
        if required is None:
            return False, f"no formal corpus policy for {stage}"
        for color in colors:
            if not any(item.get("color") == color and item.get("failureClass") in required
                       and item.get("truth") == "positive" for item in fixtures):
                return False, f"formal corpus lacks {color} {stage} positive coverage"
    except (AttributeError, KeyError, TypeError, RepairError, OSError) as exc:
        return False, f"formal corpus could not be validated: {exc}"
    return True, f"{len(fixtures)} formal fixtures cover {', '.join(colors)} {stage}"


def repair_gate(report: dict[str, Any], plan: Any, repo: Path) -> dict[str, Any]:
    """Require explicit visual and regression evidence; legacy reports fail closed."""
    analysis = report["analysis"]
    assessment = analysis.get("repair_assessment")
    reasons: list[str] = []
    reason_codes: set[str] = set()
    if report["formatVersion"] != 3 or not isinstance(assessment, dict):
        reasons.append("legacy analysis lacks machine-readable repair evidence")
    else:
        if report.get("analysisModel") != ANALYSIS_MODEL \
                or report.get("analysisReasoningEffort") != ANALYSIS_REASONING_EFFORT \
                or report.get("analysisExecuted") is not True:
            reasons.append("analysis model provenance is missing or differs from the pinned role")
        if report.get("analysisEscalationExecuted") is True \
                and (report.get("analysisEscalationModel") != ESCALATION_MODEL
                     or report.get("analysisEscalationReasoningEffort")
                        != ESCALATION_REASONING_EFFORT):
            reasons.append("second-opinion model provenance is invalid")
        selected = {}
        latency_only_images: set[str] = set()
        for image in plan.images:
            context = _json_file(image.context_path, MAX_CODEX_CONTEXT_BYTES)
            selected[image.image_id] = (image.frame_id, str(context.get("selectedColor", "")).upper())
            motion = context.get("motionEvent")
            if isinstance(motion, dict) and motion.get("signals") and all(
                    item.get("kind") in {"frame_interval", "frame_gap", "processing_time"}
                    for item in motion["signals"]):
                latency_only_images.add(image.image_id)
        if assessment["decision"] != "actionable":
            reasons.append("analysis requests more evidence")
        if not assessment["visible_saber_confirmed"]:
            reasons.append("selected lossless PNGs do not confirm a real saber")
        if not assessment["production_change_supported"]:
            reasons.append("analysis does not support a production change")
        if not assessment["diagnosis_consistent_with_metadata"]:
            reasons.append("visual diagnosis and compact metadata were not reconciled")
        if assessment["root_cause_stage"] not in {
                "segmentation", "candidate_generation", "eligibility", "ranking", "endpoint"}:
            reasons.append("recognition root-cause stage is unknown or outside repair scope")
        if assessment["change_type"] == "none":
            reasons.append("no concrete recognition change is proposed")
        if not assessment["affected_colors"] or not assessment["evidence_image_ids"]:
            reasons.append("affected color or selected visual evidence is missing")
        if assessment["evidence_image_ids"] and all(
                image_id in latency_only_images
                for image_id in assessment["evidence_image_ids"]):
            reasons.append("latency-only events do not support recognition repair")
        for color in assessment["affected_colors"]:
            if not any(image_id not in latency_only_images
                       and selected.get(image_id, (None, None))[1] == color
                       for image_id in assessment["evidence_image_ids"]):
                reasons.append(f"no selected {color} PNG supports the proposed repair")
        findings = [finding for section in _analysis_sections()
                    for finding in analysis[section]]
        supported = [finding for finding in findings
                     if finding["confidence"] in {"high", "medium"}
                     and finding["color"] in set(assessment["affected_colors"]) | {"BOTH"}
                     and any(image_id not in latency_only_images
                             and image_id in assessment["evidence_image_ids"]
                             and selected[image_id][0] in finding["frame_ids"]
                             for image_id in finding["image_ids"])]
        if not supported:
            reasons.append("all relevant findings are low confidence or lack selected PNG references")
        if assessment["change_type"] == "threshold":
            frames = {selected[image_id][0] for finding in supported
                      for image_id in finding["image_ids"]
                      if image_id in assessment["evidence_image_ids"]
                      and image_id not in latency_only_images}
            if assessment["independent_visual_examples"] < 2 \
                    or len(assessment["evidence_image_ids"]) < 2 or len(frames) < 2:
                reasons.append("a threshold change is supported by fewer than two distinct examples")
        if assessment["affected_colors"]:
            covered, detail = _corpus_coverage(repo, assessment["affected_colors"],
                                               assessment["root_cause_stage"])
            if not covered:
                reasons.append(detail)
        else:
            detail = "formal corpus not assessed without an affected color"
    events = temporal_events(plan)
    tracking_required = tracking_repair_required(analysis, events)
    if tracking_required:
        proposal = analysis.get("tracking_assessment", {})
        references = set(proposal.get("temporal_image_ids", []))
        selected_events = [e for e in events if e["color"].upper() in (assessment or {}).get("affected_colors", [])]
        if not events:
            reason_codes.add("frameMappingMissing")
        if not selected_events or not any(sufficient_temporal(e) for e in selected_events):
            reason_codes.add("temporalEvidenceMissing")
        if not any(sum(bool(f["selectedCandidate"]) for f in e["frames"]) >= 3 for e in selected_events):
            reason_codes.add("insufficientCandidateHistory")
        if not any(sum(bool((f["selectedCandidate"] or {}).get("endpointPipeline"))
                       for f in e["frames"]) >= 3 for e in selected_events):
            reason_codes.add("insufficientEndpointHistory")
        if not proposal.get("symptom_confirmed_in_images") or not any(
                supports_temporal_images(e, references) for e in selected_events):
            reason_codes.add("visualEvidenceMissing")
        stage = proposal.get("first_unstable_stage", "unknown")
        expected_coarse = {"candidate_selection": {"ranking", "candidate_generation"},
            "mask_component": {"segmentation", "candidate_generation"}, "PCA": {"endpoint"},
            "robust_body": {"endpoint"}, "endpoint_selection": {"endpoint"},
            "fallback": {"endpoint"}}
        if stage not in expected_coarse:
            reason_codes.add("rootCauseStageUnknown" if stage == "unknown" else "productionChangeUnsupported")
        elif assessment.get("root_cause_stage") not in expected_coarse[stage]:
            reason_codes.add("rootCauseStageUnknown")
        if not all(isinstance(proposal.get(key), str) and proposal[key].strip()
                   and proposal[key].strip().lower() not in {"unknown", "none", "n/a"}
                   for key in ("concrete_cause", "concrete_production_change", "expected_effect", "regression_risk")):
            reason_codes.add("noConcreteRepairProposed")
        for code in sorted(reason_codes):
            reasons.append(f"tracking evidence requirement not satisfied: {code}")
    for reason in reasons:
        text = reason.lower()
        if "do not confirm a real saber" in text or "selected visual evidence is missing" in text:
            reason_codes.add("visualEvidenceMissing")
        if "root-cause stage" in text:
            reason_codes.add("rootCauseStageUnknown")
        if "no concrete" in text:
            reason_codes.add("noConcreteRepairProposed")
        if "production change" in text or "latency-only" in text:
            reason_codes.add("productionChangeUnsupported")
        if "model provenance" in text or "pinned role" in text:
            reason_codes.add("analysisProvenanceInvalid")
        if "corpus" in text and "not assessed" not in text:
            reason_codes.add("regressionCoverageMissing")
        if "threshold change" in text:
            reason_codes.add("insufficientIndependentVisualExamples")
        if "low confidence" in text or "not reconciled" in text:
            reason_codes.add("diagnosisUnsupported")
        if "more evidence" in text or "legacy analysis" in text:
            reason_codes.add("evidenceAssessmentIncomplete")
    return {
        "decision": "actionable" if not reasons else "needs_capture",
        "reasons": reasons,
        "reasonCodes": sorted(reason_codes),
        "corpus": locals().get("detail", "formal corpus not assessed"),
    }


def _git(repo: Path, *arguments: str, timeout: int = GIT_TIMEOUT_SECONDS) -> str:
    try:
        result = subprocess.run(["git", *arguments], cwd=repo, capture_output=True,
                                text=True, timeout=timeout, check=False)
    except (OSError, subprocess.TimeoutExpired) as exc:
        raise RepairError(f"git {' '.join(arguments[:2])} could not complete: {exc}") from exc
    if result.returncode:
        raise RepairError(f"git {' '.join(arguments[:2])} failed: {result.stderr.strip()[:300]}")
    return result.stdout.rstrip("\n")


def main_safety_gate(repo: Path) -> dict[str, str]:
    """Run the required branch, cleanliness, fetch, and ahead/behind checks."""
    branch = _git(repo, "branch", "--show-current")
    porcelain = _git(repo, "status", "--porcelain")
    _git(repo, "fetch", "origin")
    distance = _git(repo, "rev-list", "--left-right", "--count", "HEAD...origin/main")
    head = _git(repo, "rev-parse", "HEAD")
    origin = _git(repo, "rev-parse", "origin/main")
    if branch != "main" or porcelain or distance.split() != ["0", "0"] or head != origin:
        raise RepairError(
            f"BLOCKED main safety gate: branch={branch}, dirty={bool(porcelain)}, "
            f"ahead/behind={distance}, head={head}, origin={origin}"
        )
    return {"branch": branch, "porcelain": porcelain, "distance": distance,
            "head": head, "origin": origin}


def _progress(start: float, phase: str, message: str) -> None:
    print(f"[AUTO_REPAIR][{phase}] elapsed={time.monotonic() - start:.1f}s {message}", flush=True)


def _terminal_report(bundle: Path, state: dict[str, Any], gate: dict[str, Any],
                     attempts: list[dict[str, Any]], review: dict[str, Any],
                     verification: dict[str, Any], *, reason: str) -> dict[str, Any]:
    status = state["status"]
    label = {
        "needs_capture": "NEEDS MORE EVIDENCE", "repair_pushed": "REPAIR PUSHED",
        "dry_run": "DRY RUN", "blocked": "BLOCKED",
        "blocked_remote_changed": "BLOCKED_REMOTE_CHANGED",
        "repair_failed": "REPAIR FAILED",
        "MODEL_UNAVAILABLE": "MODEL_UNAVAILABLE",
        "BLOCKED_BASELINE_UNSTABLE": "BLOCKED_BASELINE_UNSTABLE",
    }[status]
    models = state.get("models", {})
    model_fields = {
        "analysisModel": models.get("analysis", {}).get("model", ANALYSIS_MODEL),
        "analysisReasoningEffort": models.get("analysis", {}).get(
            "reasoningEffort", ANALYSIS_REASONING_EFFORT),
        "analysisExecuted": models.get("analysis", {}).get("executed"),
        "analysisAttempted": models.get("analysis", {}).get("attempted", False),
        "analysisReanalysisExecuted": models.get("analysis", {}).get("reanalysisExecuted", False),
        "analysisEscalationModel": models.get("analysisEscalation", {}).get("model", ESCALATION_MODEL),
        "analysisEscalationReasoningEffort": models.get("analysisEscalation", {}).get(
            "reasoningEffort", ESCALATION_REASONING_EFFORT),
        "analysisEscalationExecuted": models.get("analysisEscalation", {}).get("executed", False),
        "repairModel": models.get("repair", {}).get("model", REPAIR_MODEL),
        "repairReasoningEffort": models.get("repair", {}).get(
            "reasoningEffort", REPAIR_REASONING_EFFORT),
        "repairExecuted": models.get("repair", {}).get("executed", False),
        "repairAttempted": models.get("repair", {}).get("attempted", False),
        "reviewModel": models.get("review", {}).get("model", REVIEW_MODEL),
        "reviewReasoningEffort": models.get("review", {}).get(
            "reasoningEffort", REVIEW_REASONING_EFFORT),
        "reviewExecuted": models.get("review", {}).get("executed", False),
        "reviewAttempted": models.get("review", {}).get("attempted", False),
    }
    payload = {"sessionID": state["sessionID"], "status": status,
               "reason": reason, "reasonCodes": gate.get("reasonCodes", []), "attempts": len(attempts), "commit": state.get("commit"),
               "originMain": state.get("pushedOriginMain"), "dryRun": state.get("dryRun", False),
               **model_fields}
    final_payload = {**payload, "result": label,
                     "tests": "PASS" if verification.get("passed") else "NOT PASSED",
                     "review": review.get("decision", "not_run").upper(),
                     "verification": verification}
    _atomic_json(bundle / "repair_status.json", payload)
    repair_report = {"sessionID": state["sessionID"], "gate": gate,
                     "attempts": attempts, "verification": verification,
                     "formalBaseline": state.get("formalBaseline"),
                     "result": status, "reason": reason,
                     "dryRunPlan": state.get("dryRunPlan"), **model_fields}
    _atomic_json(bundle / "repair_report.json", repair_report)
    lines = ["# PhoneSaber repair", "", f"Status: {status}", "", f"Reason: {reason}", "",
             f"Gate: {gate.get('decision', 'not_run')}", f"Corpus: {gate.get('corpus', 'not_run')}",
             f"Base main: {state.get('baseHead', '-')}", f"Attempts: {len(attempts)}"]
    for item in attempts:
        lines.append(f"- Attempt {item['attempt']}: {item['result']} — "
                     f"{item.get('reason', item.get('summary', ''))}")
    if verification:
        lines.extend(["", "## Verification", ""])
        lines.extend(f"- {name}: {value}" for name, value in verification.get("stages", {}).items())
        comparison = verification.get("formalComparison", {})
        if comparison:
            lines.extend([
                f"- Formal regression before: {comparison['beforePassed']}/{comparison['fixtures']}",
                f"- Formal regression after: {comparison['afterPassed']}/{comparison['fixtures']}",
                f"- Preserved previous passes: {comparison['preservedPasses']}/{comparison['beforePassed']}",
                f"- Target failures repaired: {comparison['targetFailuresRepaired']}/{comparison['targetFailures']}",
                f"- New regressions: {len(comparison['newRegressions'])}",
            ])
        lines.append(f"- Protected fixture output changes: {len(comparison.get('outputChanges', []))}")
        lines.append(f"- Candidate diagnostic changes: "
                     f"{len(comparison.get('candidateDiagnosticChanges', []))}")
    if state.get("dryRunPlan"):
        lines.extend(["", "## Dry-run plan", "",
                      "```json", json.dumps(state["dryRunPlan"], ensure_ascii=False, indent=2), "```"])
    _atomic_text(bundle / "repair_report.md", "\n".join(lines) + "\n")
    _atomic_json(bundle / "review_report.json", review)
    _atomic_text(bundle / "review_report.md", "# PhoneSaber review\n\n"
                 f"Decision: {review.get('decision', 'not_run')}\n\n"
                 f"Reason: {review.get('reason', 'not run')}\n\n"
                 + "\n".join(f"- {note}" for note in review.get("risk_notes", [])) + "\n")
    _atomic_text(bundle / "final_report.md", f"# PhoneSaber automatic repair\n\n"
                 f"Result: {label}\n\nSession: {state['sessionID']}\n\n"
                 f"Reason: {reason}\n\ncommit:\n{state.get('commit') or '-'}\n\n"
                 f"origin/main:\n{state.get('pushedOriginMain') or '-'}\n\n"
                 f"tests:\n{'PASS' if verification.get('passed') else 'NOT PASSED'}\n\n"
                 f"review:\n{review.get('decision', 'not_run').upper()}\n\n"
                 f"analysisModel: {model_fields['analysisModel']}\n"
                 f"analysisReasoningEffort: {model_fields['analysisReasoningEffort']}\n"
                 f"analysisExecuted: {model_fields['analysisExecuted']}\n"
                 f"analysisAttempted: {model_fields['analysisAttempted']}\n\n"
                 f"analysisReanalysisExecuted: {model_fields['analysisReanalysisExecuted']}\n"
                 f"analysisEscalationModel: {model_fields['analysisEscalationModel']}\n"
                 f"analysisEscalationReasoningEffort: {model_fields['analysisEscalationReasoningEffort']}\n"
                 f"analysisEscalationExecuted: {model_fields['analysisEscalationExecuted']}\n\n"
                 f"repairModel: {model_fields['repairModel']}\n"
                 f"repairReasoningEffort: {model_fields['repairReasoningEffort']}\n"
                 f"repairExecuted: {model_fields['repairExecuted']}\n"
                 f"repairAttempted: {model_fields['repairAttempted']}\n\n"
                 f"reviewModel: {model_fields['reviewModel']}\n"
                 f"reviewReasoningEffort: {model_fields['reviewReasoningEffort']}\n"
                 f"reviewExecuted: {model_fields['reviewExecuted']}\n"
                 f"reviewAttempted: {model_fields['reviewAttempted']}\n")
    _atomic_json(bundle / "final_report.json", final_payload)
    state["phase"] = "done"
    state["updatedAt"] = time.time()
    _atomic_json(bundle / "state.json", state)
    started = state.get("startedAt")
    elapsed = max(0.0, time.time() - started) if isinstance(started, (int, float)) and started > 0 else 0.0
    print(f"[AUTO_REPAIR][DONE] elapsed={elapsed:.1f}s "
          f"subprocess=none result={label}", flush=True)
    return payload


def _save_state(bundle: Path, state: dict[str, Any], phase: str) -> None:
    state["phase"] = phase
    state["updatedAt"] = time.time()
    _atomic_json(bundle / "state.json", state)


def _run(command: list[str], *, cwd: Path, timeout: int, stdin: str | None = None,
         environment: dict[str, str] | None = None) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(command, cwd=cwd, input=stdin, capture_output=True,
                              text=True, timeout=timeout, env=environment, check=False)
    except subprocess.TimeoutExpired as exc:
        raise RepairTimeout(f"{Path(command[0]).name} timed out after {timeout}s") from exc
    except OSError as exc:
        raise RepairError(f"{Path(command[0]).name} could not start: {exc}") from exc


def _codex_call(binary: str, scratch: Path, prompt: str, schema: dict[str, Any],
                images: list[Path], *, role: str, writable: bool, timeout: int) -> dict[str, Any]:
    if role == "repair":
        model, effort = REPAIR_MODEL, REPAIR_REASONING_EFFORT
    elif role == "review":
        model, effort = REVIEW_MODEL, REVIEW_REASONING_EFFORT
    else:
        raise ValueError(f"unsupported Codex role: {role}")
    schema_path = scratch.parent / "codex_output_schema.json"
    response_path = scratch.parent / "codex_last_message.json"
    _atomic_json(schema_path, schema)
    response_path.unlink(missing_ok=True)
    command = [binary, "exec", "--model", model, "-c",
               f"model_reasoning_effort={json.dumps(effort)}", "--ephemeral", "--sandbox",
               "workspace-write" if writable else "read-only", "--skip-git-repo-check",
               "--cd", str(scratch), "--output-schema", str(schema_path),
               "--output-last-message", str(response_path)]
    for image_path in images:
        command.extend(["--image", str(image_path)])
    command.append("-")
    print(f"[AUTO_REPAIR][{role.upper()}] model={model} effort={effort} "
          f"subprocess=codex sandbox={'workspace-write' if writable else 'read-only'} "
          "result=starting", flush=True)
    completed = _run(command, cwd=scratch, timeout=timeout, stdin=prompt)
    if completed.returncode:
        diagnostic = (completed.stderr or completed.stdout).strip().splitlines()
        detail = f"Codex exit {completed.returncode}: {(diagnostic[-1] if diagnostic else 'no diagnostic')[:500]}"
        if is_model_unavailable(completed.stderr + "\n" + completed.stdout):
            print(f"[AUTO_REPAIR][{role.upper()}] model={model} effort={effort} "
                  "subprocess=codex result=MODEL_UNAVAILABLE", flush=True)
            raise RepairModelUnavailable(f"MODEL_UNAVAILABLE: {role} requires {model}/{effort}: {detail}")
        raise RepairError(detail)
    response = _json_file(response_path)
    if not isinstance(response, dict) or set(response) != set(schema["required"]):
        raise RepairError("Codex response has an invalid structure")
    for key, spec in schema["properties"].items():
        value = response[key]
        if spec["type"] == "string" and (not isinstance(value, str) or len(value) > 8000
                                           or ("enum" in spec and value not in spec["enum"])):
            raise RepairError(f"Codex response has invalid {key}")
        if spec["type"] == "array" and (not isinstance(value, list) or len(value) > 30
                                          or not all(isinstance(item, str) and len(item) <= 1000
                                                     for item in value)):
            raise RepairError(f"Codex response has invalid {key}")
    print(f"[AUTO_REPAIR][{role.upper()}] model={model} effort={effort} "
          "subprocess=codex result=complete", flush=True)
    return response


def _copy_inputs(scratch: Path, repo: Path, plan: Any, report: dict[str, Any]) -> tuple[dict[str, str], dict[str, str], list[Path]]:
    """Expose only editable recognition files and bounded selected evidence."""
    originals: dict[str, str] = {}
    for relative in REPAIR_FILES:
        source = repo / relative
        if source.is_symlink() or not source.is_file():
            raise RepairError(f"allowed recognition file unavailable: {relative}")
        target = scratch / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, target)
        originals[relative] = _sha256(target)
    evidence = scratch / "evidence"
    evidence.mkdir()
    _atomic_json(evidence / "analysis_report.json", report)
    shutil.copyfile(plan.root / "analysis_report.md", evidence / "analysis_report.md")
    shutil.copyfile(plan.root / "summary.json", evidence / "summary.json")
    reference = repo / "camera.py"
    if reference.is_symlink() or not reference.is_file():
        raise RepairError("checked-in camera.py reference is unavailable")
    shutil.copyfile(reference, evidence / "camera_reference.py")
    images: list[Path] = []
    for item in plan.images:
        for source in (item.image_path, item.context_path):
            destination = evidence / source.relative_to(plan.root)
            destination.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, destination)
        images.append(evidence / item.image_path.relative_to(plan.root))
    evidence_hashes = {path.relative_to(scratch).as_posix(): _sha256(path)
                       for path in evidence.rglob("*") if path.is_file()}
    return originals, evidence_hashes, images


def _candidate_files(scratch: Path, originals: dict[str, str],
                     evidence_hashes: dict[str, str]) -> list[str]:
    allowed = set(originals)
    expected = allowed | set(evidence_hashes)
    actual = set()
    for path in scratch.rglob("*"):
        if path.is_symlink():
            raise RepairError(f"Codex created a symlink: {path.relative_to(scratch)}")
        if path.is_file():
            relative = path.relative_to(scratch).as_posix()
            actual.add(relative)
            if relative not in expected:
                raise RepairError(f"Codex created an unauthorized file: {relative}")
    if actual != expected:
        raise RepairError("Codex removed an input or allowed source file")
    for relative, digest in evidence_hashes.items():
        if _sha256(scratch / relative) != digest:
            raise RepairError(f"Codex modified read-only evidence: {relative}")
    return [relative for relative in REPAIR_FILES if relative in originals
            and _sha256(scratch / relative) != originals[relative]]


def _baseline(repo: Path, private: Path) -> dict[str, Any]:
    runner = repo / "ios/PhoneSaberSender/Tools/run_lossless_regression.py"
    manifest = repo / "ios/PhoneSaberSender/Tools/lossless_regression_manifest.json"
    shutil.copyfile(runner, private / "pinned_lossless_runner.py")
    shutil.copyfile(manifest, private / "pinned_lossless_manifest.json")
    return _formal_regression(repo, private, "baseline")


def _protect_formal_manifest(private: Path, scratch: Path) -> None:
    """New fixtures are allowed; checked-in baseline expectations cannot be weakened."""
    baseline = _json_file(private / "pinned_lossless_manifest.json", 2 * 1024 * 1024)
    candidate = _json_file(
        scratch / "ios/PhoneSaberSender/Tools/lossless_regression_manifest.json", 2 * 1024 * 1024)
    if not isinstance(baseline, dict) or not isinstance(candidate, dict) \
            or candidate.get("schemaVersion") != baseline.get("schemaVersion") \
            or candidate.get("failureClasses") != baseline.get("failureClasses") \
            or not isinstance(candidate.get("fixtures"), list):
        raise RepairError("formal manifest schema or failure classes changed")
    preserved = {item["name"]: item for item in candidate["fixtures"] if isinstance(item, dict)
                 and isinstance(item.get("name"), str)}
    if len(preserved) != len(candidate["fixtures"]):
        raise RepairError("formal manifest has missing or duplicate fixture names")
    for fixture in baseline["fixtures"]:
        if preserved.get(fixture["name"]) != fixture:
            raise RepairError(f"protected fixture changed or removed: {fixture['name']}")


def _formal_regression(repo: Path, private: Path, name: str) -> dict[str, Any]:
    output = private / f"{name}_lossless.json"
    environment = {**os.environ, "PHONESABER_REGRESSION_REPO": str(repo)}
    completed = _run([sys.executable, str(private / "pinned_lossless_runner.py"),
                      "--manifest", str(private / "pinned_lossless_manifest.json"),
                      "--json", str(output)], cwd=repo, timeout=VERIFY_TIMEOUT_SECONDS,
                     environment=environment)
    if completed.returncode not in {0, 1} or not output.is_file():
        raise RepairError(f"BLOCKED formal lossless {name} infrastructure failed: "
                          f"{(completed.stderr or completed.stdout)[-1000:]}")
    result = _json_file(output, 8 * 1024 * 1024)
    if not isinstance(result, dict) or not isinstance(result.get("fixtures"), list) \
            or len(result["fixtures"]) != 40 or not isinstance(result.get("summary"), dict):
        raise RepairError(f"BLOCKED formal lossless {name} has malformed or incomplete corpus")
    rows = result["fixtures"]
    names = [row.get("name") for row in rows if isinstance(row, dict)]
    if len(names) != 40 or len(set(names)) != 40 or any(not isinstance(name, str) for name in names) \
            or any(not isinstance(row.get("passed"), bool) for row in rows) \
            or any(not isinstance(row.get("candidateScores"), list) for row in rows) \
            or result["summary"].get("fixture_count") != 40 \
            or result["summary"].get("passed") != sum(row["passed"] for row in rows) \
            or result["summary"].get("failed") != sum(not row["passed"] for row in rows) \
            or (completed.returncode == 0) != (result["summary"]["failed"] == 0):
        raise RepairError(f"BLOCKED formal lossless {name} result is inconsistent")
    return result


def _classify_baseline(before: dict[str, Any], plan: Any, repo: Path,
                       report: dict[str, Any]) -> dict[str, str]:
    """Relate failures to actual selected PNGs and the analysis affected color."""
    selected = {}
    confirmed_ids = set(report["analysis"]["repair_assessment"]["evidence_image_ids"])
    for image in plan.images:
        if image.image_id not in confirmed_ids:
            continue
        context = _json_file(image.context_path, MAX_CODEX_CONTEXT_BYTES)
        selected[_sha256(image.image_path)] = str(context["selectedColor"]).upper()
    manifest = _json_file(repo / "ios/PhoneSaberSender/Tools/lossless_regression_manifest.json",
                          2 * 1024 * 1024)
    evidence = {(item["name"], item["color"]) for item in manifest["fixtures"]
                if item.get("sha256") in selected and
                selected[item["sha256"]] in {item["color"], "BOTH"}}
    direct = {name for name, _color in evidence}
    rows = before["fixtures"]
    related_scenarios = {(row["color"], row["scenario"])
                         for row in rows if row["name"] in direct and not row["passed"]}
    related_classes = {(row["color"], row["failureClass"])
                       for row in rows if row["name"] in direct and not row["passed"]}
    affected = {color for _name, color in evidence}
    classified = {}
    for row in rows:
        if row["passed"]:
            continue
        if row["name"] in direct or (row["color"], row["scenario"]) in related_scenarios \
                or (row["color"], row["failureClass"]) in related_classes:
            classified[row["name"]] = "target-related"
        elif row["color"] not in affected:
            classified[row["name"]] = "unrelated-existing"
        else:
            classified[row["name"]] = "unknown"
    return classified


def _compare_regression(before: dict[str, Any], after: dict[str, Any],
                        classifications: dict[str, str] | None = None) -> dict[str, Any]:
    previous = {row["name"]: row for row in before["fixtures"]}
    current = {row["name"]: row for row in after["fixtures"]}
    if previous.keys() != current.keys():
        raise RepairError("formal fixture set changed between baseline and candidate")
    output_changes, diagnostic_changes, case_diff = [], [], []
    classifications = classifications or {}
    new_regressions, target_repaired, worsened = [], [], []
    for name in sorted(previous):
        left, right = previous[name], current[name]
        if left["passed"] and not right["passed"]:
            new_regressions.append(name)
        if not left["passed"] and right["passed"] and classifications.get(name) == "target-related":
            target_repaired.append(name)
        if not left["passed"] and not right["passed"]:
            expected = left.get("expectedDetected")
            newly_detected_positive = expected is True and not left["detected"] and right["detected"]
            removed_negative_false_positive = expected is False and left["detected"] and not right["detected"]
            def error_keys(row: dict[str, Any]) -> set[str]:
                keys = set()
                for error in row.get("errors", []):
                    if error.startswith("endpoint error "):
                        keys.add("endpoint error")
                    elif error.startswith("candidate type expected "):
                        keys.add("candidate type")
                    elif error.startswith("detected expected "):
                        keys.add("detected")
                    else:
                        keys.add(error)
                return keys
            new_errors = error_keys(right) - error_keys(left)
            endpoint_worse = (expected is True and left["detected"] and right["detected"]
                              and left.get("endpointErrorPx") is not None
                              and right.get("endpointErrorPx") is not None
                              and right["endpointErrorPx"] > left["endpointErrorPx"] + 1e-6)
            candidates_worse = (expected is True and not left["detected"]
                                and not right["detected"]
                                and right.get("candidateCount", 0) < left.get("candidateCount", 0))
            if (new_errors and not (newly_detected_positive or removed_negative_false_positive)) \
                    or endpoint_worse or candidates_worse:
                worsened.append(name)
        case_diff.append({"fixture": name, "classification": classifications.get(name, "previous-pass"),
                          "beforePassed": left["passed"], "afterPassed": right["passed"],
                          "before": {key: left.get(key) for key in
                                     ("detected", "candidateCount", "candidateType", "endpoint",
                                      "endpointErrorPx", "candidateScores", "selectedScore", "errors")},
                          "after": {key: right.get(key) for key in
                                    ("detected", "candidateCount", "candidateType", "endpoint",
                                     "endpointErrorPx", "candidateScores", "selectedScore", "errors")}})
        keys = ("detected", "candidateType", "endpoint")
        if left["passed"] and any(left[key] != right[key] for key in keys):
            output_changes.append(name)
        keys = ("candidateCount", "candidateScores", "selectedScore")
        left_scores, right_scores = left.get("candidateScores"), right.get("candidateScores")
        if not isinstance(left_scores, list) or not isinstance(right_scores, list):
            raise RepairError(f"formal fixture has invalid candidate scores: {name}")
        try:
            score_lists_equal = len(left_scores) == len(right_scores) and all(
                math.isfinite(float(a)) and math.isfinite(float(b))
                and math.isclose(float(a), float(b), rel_tol=1e-9, abs_tol=1e-6)
                for a, b in zip(left_scores, right_scores))
            selected_left, selected_right = left.get("selectedScore"), right.get("selectedScore")
            selected_equal = (selected_left is None and selected_right is None) or (
                selected_left is not None and selected_right is not None
                and math.isfinite(float(selected_left)) and math.isfinite(float(selected_right))
                and math.isclose(float(selected_left), float(selected_right), rel_tol=1e-9, abs_tol=1e-6))
        except (TypeError, ValueError, OverflowError) as exc:
            raise RepairError(f"formal fixture has nonnumeric candidate scores: {name}") from exc
        if left.get("candidateCount") != right.get("candidateCount") \
                or not score_lists_equal or not selected_equal:
            diagnostic_changes.append({
                "fixture": name,
                "before": {key: left.get(key) for key in keys},
                "after": {key: right.get(key) for key in keys},
            })
    def details(names: list[str]) -> str:
        selected = [row for row in case_diff if row["fixture"] in names]
        return json.dumps(selected, ensure_ascii=False, separators=(",", ":"))[:2400]

    if new_regressions:
        raise RepairError(f"new formal regressions: {details(new_regressions)}")
    if output_changes:
        raise RepairError(f"protected recognition outputs changed: {details(output_changes)}")
    if worsened:
        raise RepairError(f"baseline failures worsened: {details(worsened)}")
    targets = [name for name, kind in classifications.items() if kind == "target-related"]
    if targets and not target_repaired:
        raise RepairError(f"target formal failures did not improve into PASS: {details(targets)}")
    if any(not row["passed"] for row in previous.values()) and not targets:
        raise RepairError("BLOCKED_BASELINE_UNSTABLE: no baseline failure relates to incident")
    remaining = [name for name, row in current.items() if not row["passed"]]
    if remaining:
        raise RepairError(f"post-repair formal failures remain: {details(remaining)}")
    return {"fixtures": len(previous), "beforePassed": sum(row["passed"] for row in previous.values()),
            "afterPassed": sum(row["passed"] for row in current.values()),
            "preservedPasses": sum(previous[name]["passed"] and current[name]["passed"] for name in previous),
            "targetFailures": len(targets), "targetFailuresRepaired": len(target_repaired),
            "newRegressions": new_regressions, "worsenedBaselineFailures": worsened,
            "caseDiff": case_diff, "outputChanges": output_changes,
            "candidateDiagnosticChanges": diagnostic_changes}


def _verify(repo: Path, private: Path, baseline: dict[str, Any], attempt: int,
            classifications: dict[str, str] | None = None) -> dict[str, Any]:
    after = _formal_regression(repo, private, f"attempt{attempt}")
    comparison = _compare_regression(baseline, after, classifications)
    completed = _run([str(repo / "tools/verify_phone_saber.sh")], cwd=repo,
                     timeout=VERIFY_TIMEOUT_SECONDS)
    output = completed.stdout + "\n" + completed.stderr
    _atomic_text(private / f"attempt{attempt}_verification.log", output[-200000:])
    stages = {}
    for stage in (*REQUIRED_VERIFY_STAGES, "Unity EditMode", "Unity PlayMode",
                  "Unity Compile", "Unity capability"):
        matches = re.findall(rf"(?m)^{re.escape(stage)}\s+(PASS|FAIL|BLOCKED|NOT_RUN)\b", output)
        stages[stage] = matches[-1] if matches else "MISSING"
    if any(stages[stage] != "PASS" for stage in REQUIRED_VERIFY_STAGES) \
            or any(stages[stage] == "FAIL" for stage in stages if stage.startswith("Unity")) \
            or completed.returncode not in {0, 2}:
        diagnostics = []
        match = re.search(r"(?m)^Logs:\s*(.+)$", output)
        if match:
            log_dir = Path(match.group(1).strip()).resolve()
            if log_dir.is_relative_to(repo.resolve()):
                for name in ("ios-xctest.stdout.log", "detection-tests.stderr.log",
                             "detection-tests.stdout.log"):
                    path = log_dir / name
                    if path.is_file() and not path.is_symlink():
                        lines = path.read_text(encoding="utf-8", errors="replace").splitlines()
                        diagnostics.extend(line.strip()[:300] for line in lines
                                           if re.search(r"Test Case .* failed|error:|Assertion Failure|FAILED", line))
        raise RepairError(f"verification stages did not pass: {stages}; exit={completed.returncode}; "
                          f"diagnostics={diagnostics[:8]}")
    return {"passed": True, "stages": stages, "formalComparison": comparison,
            "verifyExitCode": completed.returncode}


def _restore_owned(repo: Path, private: Path, state: dict[str, Any]) -> None:
    for relative, hashes in state.get("ownedFiles", {}).items():
        target = repo / relative
        original = private / "backup" / relative
        if not original.is_file() or _sha256(original) != hashes["original"]:
            raise RepairError(f"cannot verify backup for rollback: {relative}")
        if not target.is_file() or _sha256(target) not in {
                hashes["original"], hashes["planned"], hashes.get("applied")}:
            raise RepairError(f"owned file was changed externally; refusing rollback: {relative}")
        if _sha256(target) != hashes["original"]:
            temporary = target.with_name(f".{target.name}.phonesaber-restore")
            shutil.copy2(original, temporary)
            os.replace(temporary, target)
    state["ownedFiles"] = {}


def _apply_candidate(repo: Path, scratch: Path, private: Path, state: dict[str, Any],
                     changed: list[str], bundle: Path) -> None:
    backup = private / "backup"
    for relative in changed:
        target = repo / relative
        saved = backup / relative
        saved.parent.mkdir(parents=True, exist_ok=True)
        if not saved.exists():
            shutil.copy2(target, saved)
        state.setdefault("ownedFiles", {})[relative] = {
            "original": _sha256(saved), "planned": _sha256(scratch / relative),
        }
    _save_state(bundle, state, "applying")
    for relative in changed:
        target = repo / relative
        hashes = state["ownedFiles"][relative]
        if _sha256(target) != hashes["original"]:
            raise RepairError(f"source changed during repair: {relative}")
        temporary = target.with_name(f".{target.name}.phonesaber-apply")
        shutil.copy2(scratch / relative, temporary)
        os.replace(temporary, target)
        hashes["applied"] = _sha256(target)
        _save_state(bundle, state, "applying")
    _save_state(bundle, state, "verifying")


def _current_diff(repo: Path, changed: list[str]) -> str:
    result = _git(repo, "diff", "--", *changed)
    if not result:
        raise RepairError("Codex candidate has no tracked source diff")
    return result


def _review(binary: str, scratch: Path, plan: Any, report: dict[str, Any],
            diff: str, verification: dict[str, Any], images: list[Path],
            repo: Path = REPO_ROOT) -> dict[str, Any]:
    manifest = _json_file(repo / "ios/PhoneSaberSender/Tools/lossless_regression_manifest.json",
                          2 * 1024 * 1024)
    fixtures = {item["name"]: item for item in manifest["fixtures"]}
    affected = report["analysis"]["repair_assessment"]["affected_colors"]
    if not isinstance(affected, list) or not affected or any(
            color not in REVIEW_NEGATIVE_CONTROLS for color in affected):
        raise RepairError("review requires recognized affected colors")
    control_names = tuple(name for color in dict.fromkeys(affected)
                          for name in REVIEW_NEGATIVE_CONTROLS[color])
    controls: list[Path] = []
    fixture_root = (repo / "ios/PhoneSaberSenderTests/Fixtures").resolve()
    for name in control_names:
        item = fixtures[name]
        if item["truth"] != "negative" or item["expectedDetected"] is not False:
            raise RepairError(f"review control is not a protected negative: {name}")
        source = fixture_root / item["path"]
        path = source.resolve()
        if source.is_symlink() or not path.is_relative_to(fixture_root) \
                or not path.is_file() or _sha256(path) != item["sha256"]:
            raise RepairError(f"review control is missing or changed: {name}")
        controls.append(path)
    compact_verification = dict(verification)
    comparison = dict(compact_verification.get("formalComparison", {}))
    comparison["caseDiff"] = [row for row in comparison.get("caseDiff", [])
                              if not row["beforePassed"] or row["before"] != row["after"]]
    compact_verification["formalComparison"] = comparison
    prompt = ("Independent read-only review of a proposed PhoneSaber recognition repair. "
              "Inspect the selected incident PNGs, compact contexts, structured analysis, "
              "and the attached formal negative PNG controls "
              f"({', '.join(control_names)}), "
              "source changes, and verification results. Inspect every formal case diff, "
              "including candidate counts, selected endpoints and score changes on previous "
              "passes; judge candidate-count changes against the negative PNG controls and "
              "formal rejected-candidate expectations, then reject unreasonable changes. "
              "A candidate-count change alone is diagnostic and does not imply a detection. "
              "Reject uncertain visual ground truth, "
              "untested side effects, threshold changes supported by fewer than two independent "
              "examples, gameplay/UDP changes, or incomplete coverage. Respond in the schema.\n\n"
              f"Analysis: {json.dumps(report['analysis'], ensure_ascii=False)}\n\n"
              f"Verification: {json.dumps(compact_verification, ensure_ascii=False)}\n\n"
              f"Diff:\n{diff[:120000]}")
    return _codex_call(binary, scratch, prompt, REVIEW_RESPONSE_SCHEMA, [*images, *controls],
                       role="review", writable=False,
                       timeout=REVIEW_CODEX_TIMEOUT_SECONDS)


def _commit_push(repo: Path, bundle: Path, state: dict[str, Any], changed: list[str]) -> str:
    base = state["baseHead"]
    if _git(repo, "branch", "--show-current") != "main" or _git(repo, "rev-parse", "HEAD") != base:
        raise RepairError("main HEAD changed before commit")
    status = _git(repo, "status", "--porcelain")
    touched = {line[3:] for line in status.splitlines() if line}
    if touched != set(changed):
        raise RepairError(f"unexpected changes before commit: {sorted(touched ^ set(changed))}")
    _git(repo, "fetch", "origin")
    if _git(repo, "rev-parse", "origin/main") != state["originHead"]:
        raise RepairError("BLOCKED_REMOTE_CHANGED: origin/main moved before push")
    _git(repo, "add", "--", *changed)
    _git(repo, "commit", "-m", f"Repair PhoneSaber recognition from {state['sessionID']}")
    commit = _git(repo, "rev-parse", "HEAD")
    state["commit"] = commit
    _save_state(bundle, state, "pushing")
    print(f"[AUTO_REPAIR][PUSH] elapsed={max(0.0, time.time() - state['startedAt']):.1f}s "
          "subprocess=git push result=starting", flush=True)
    result = _run(["git", "push", "origin", "main"], cwd=repo, timeout=GIT_TIMEOUT_SECONDS)
    _git(repo, "fetch", "origin")
    remote = _git(repo, "rev-parse", "origin/main")
    if result.returncode or remote != commit:
        if remote == commit:
            state["pushedOriginMain"] = remote
            return commit
        raise RepairError(f"BLOCKED_REMOTE_CHANGED: push failed or origin moved: "
                          f"{result.stderr.strip()[:300]}")
    state["pushedOriginMain"] = remote
    return commit


def _uncommit_unpushed(repo: Path, state: dict[str, Any], changed: list[str]) -> None:
    commit = state.get("commit")
    if not commit:
        return
    if _git(repo, "rev-parse", "HEAD") != commit:
        raise RepairError("cannot undo unpushed commit: HEAD moved externally")
    _git(repo, "fetch", "origin")
    if _git(repo, "rev-parse", "origin/main") == commit:
        raise RepairError("cannot undo commit already present on origin/main")
    _git(repo, "update-ref", "refs/heads/main", state["baseHead"], commit)
    _git(repo, "restore", "--source", state["baseHead"], "--staged", "--", *changed)
    state["commit"] = None


def _discover_owned_commit(repo: Path, state: dict[str, Any]) -> None:
    """Handle a crash between git commit success and writing its hash to state.json."""
    if state.get("commit") or state.get("phase") != "committing":
        return
    head = _git(repo, "rev-parse", "HEAD")
    if head == state.get("baseHead"):
        return
    parent = _git(repo, "rev-parse", "HEAD^")
    message = _git(repo, "log", "-1", "--format=%s")
    changed = set(_git(repo, "diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD").splitlines())
    if parent != state.get("baseHead") or message != f"Repair PhoneSaber recognition from {state['sessionID']}" \
            or changed != set(state.get("ownedFiles", {})):
        raise RepairError("HEAD changed after interrupted commit; refusing to alter it")
    state["commit"] = head


def repair_bundle(bundle: Path, *, repo: Path = REPO_ROOT, codex_path: str | None = None,
                  dry_run: bool = False, max_images: int = 12) -> dict[str, Any]:
    """Run at most two repairs under one inbox lock; terminal state is never replayed."""
    start = time.monotonic()
    if bundle.is_symlink():
        raise RepairError("bundle path must not be a symlink")
    bundle = bundle.resolve(strict=True)
    repo = repo.resolve(strict=True)
    lock_path = bundle.parent / ".phonesaber-auto-repair.lock"
    with lock_path.open("a+b") as lock:
        fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
        existing = bundle / "state.json"
        if existing.exists():
            previous = _json_file(existing)
            if previous.get("status") in TERMINAL_STATUSES \
                    and not (previous.get("status") == "dry_run" and not dry_run):
                _progress(start, "SKIP", "terminal state already exists")
                return _json_file(bundle / "repair_status.json")
            if previous.get("status") == "dry_run" and not dry_run:
                previous = None
            if previous is None:
                pass
            if previous is not None:
                # No automatic second repair after a crash. Restore only our own source bytes.
                private = bundle.parent / f".phonesaber-repair-{previous['sessionID']}"
                try:
                    _discover_owned_commit(repo, previous)
                    if previous.get("commit"):
                        _git(repo, "fetch", "origin")
                        if _git(repo, "rev-parse", "origin/main") == previous["commit"]:
                            previous["pushedOriginMain"] = previous["commit"]
                            previous["status"] = "repair_pushed"
                            return _terminal_report(bundle, previous, {}, [], {}, {},
                                                    reason="Verified pushed commit recovered after receiver restart")
                    if previous.get("commit") and not previous.get("pushedOriginMain"):
                        _uncommit_unpushed(repo, previous, list(previous.get("ownedFiles", {})))
                    if previous.get("ownedFiles"):
                        if previous.get("baseHead") and _git(repo, "rev-parse", "HEAD") == previous["baseHead"]:
                            _git(repo, "restore", "--staged", "--", *previous["ownedFiles"])
                        _restore_owned(repo, private, previous)
                    status, reason = "repair_failed", "Interrupted run recovered; no duplicate repair started"
                except RepairError as exc:
                    status, reason = "blocked", f"Interrupted run requires manual inspection: {exc}"
                previous["status"] = status
                return _terminal_report(bundle, previous, {}, [], {}, {}, reason=reason)

        plan = input_plan(bundle, max_images=max_images, allow_reports=True)
        report = load_analysis(bundle, plan)
        gate = repair_gate(report, plan, repo)
        state: dict[str, Any] = {"sessionID": plan.session_id, "status": "running",
                                 "phase": "gate", "attempt": 0, "dryRun": dry_run,
                                 "ownedFiles": {}, "startedAt": time.time(),
                                 "models": {
                                     "analysis": {
                                         "model": report.get("analysisModel", ANALYSIS_MODEL),
                                         "reasoningEffort": report.get("analysisReasoningEffort",
                                                                        ANALYSIS_REASONING_EFFORT),
                                         "executed": report.get("analysisExecuted"),
                                         "attempted": report.get("analysisExecuted") is True,
                                         "reanalysisExecuted": report.get("analysisReanalysisExecuted", False),
                                     },
                                     "analysisEscalation": {
                                         "model": report.get("analysisEscalationModel", ESCALATION_MODEL),
                                         "reasoningEffort": report.get("analysisEscalationReasoningEffort",
                                                                        ESCALATION_REASONING_EFFORT),
                                         "executed": report.get("analysisEscalationExecuted", False),
                                         "attempted": report.get("analysisEscalationExecuted", False),
                                     },
                                     "repair": {"model": REPAIR_MODEL,
                                                "reasoningEffort": REPAIR_REASONING_EFFORT,
                                                "executed": False, "attempted": False},
                                     "review": {"model": REVIEW_MODEL,
                                                "reasoningEffort": REVIEW_REASONING_EFFORT,
                                                "executed": False, "attempted": False},
                                 }}
        _save_state(bundle, state, "gate")
        attempts: list[dict[str, Any]] = []
        review: dict[str, Any] = {}
        verification: dict[str, Any] = {}
        private = bundle.parent / f".phonesaber-repair-{plan.session_id}"
        changed: list[str] = []
        try:
            if gate["decision"] != "actionable":
                state["status"] = "needs_capture"
                _progress(start, "GATE", "subprocess=none result=NEEDS MORE EVIDENCE")
                return _terminal_report(bundle, state, gate, attempts, review, verification,
                                        reason="; ".join(gate["reasons"]))
            try:
                safety = main_safety_gate(repo)
            except RepairError as exc:
                state["status"] = "blocked"
                return _terminal_report(bundle, state, gate, attempts, review, verification,
                                        reason=str(exc))
            state.update({"baseHead": safety["head"], "originHead": safety["origin"],
                          "initialGitDiff": safety.get("porcelain", "")})
            _save_state(bundle, state, "preflight")
            _progress(start, "PREFLIGHT", f"subprocess=git result=PASS main={safety['head'][:12]}")
            if dry_run:
                state["dryRunPlan"] = {
                    "mainCommit": safety["head"],
                    "models": {
                        "analysis": {"model": ANALYSIS_MODEL,
                                     "reasoningEffort": ANALYSIS_REASONING_EFFORT,
                                     "executed": state["models"]["analysis"]["executed"]},
                        "analysisEscalation": state["models"]["analysisEscalation"],
                        "repair": {"model": REPAIR_MODEL,
                                   "reasoningEffort": REPAIR_REASONING_EFFORT,
                                   "executed": False},
                        "review": {"model": REVIEW_MODEL,
                                   "reasoningEffort": REVIEW_REASONING_EFFORT,
                                   "executed": False},
                    },
                    "selectedImageIDs": list(plan.image_ids),
                    "repairPrompt": (
                        "Inspect selected lossless PNGs, compact contexts, analysis, and "
                        "recognition sources again. Change only allowlisted recognition files "
                        "when the visual diagnosis is sound; otherwise request more evidence."),
                    "repairAllowlist": list(REPAIR_FILES),
                    "verificationCommands": [
                        "python3 ios/PhoneSaberSender/Tools/run_lossless_regression.py",
                        "./tools/verify_phone_saber.sh",
                        "git diff --check",
                    ],
                    "review": "separate read-only Codex checks diff, visual evidence, tests and risk",
                    "commitPush": "only after every required gate passes and origin/main is unchanged",
                }
                _progress(start, "GATE", f"actionable; dry-run plan: {json.dumps(state['dryRunPlan'], ensure_ascii=False)}")
                state["status"] = "dry_run"
                return _terminal_report(bundle, state, gate, attempts, review, verification,
                                        reason="Gate actionable; repair Codex, source edit, commit and push skipped")
            binary = find_codex_binary(codex_path)
            if binary is None:
                raise RepairError("Codex CLI unavailable")
            private.mkdir(mode=0o700, exist_ok=False)
            scratch = private / "candidate"
            scratch.mkdir()
            originals, evidence_hashes, images = _copy_inputs(scratch, repo, plan, report)
            baseline = _baseline(repo, private)
            _atomic_json(bundle / "baseline_regression.json", baseline)
            classifications = _classify_baseline(baseline, plan, repo, report)
            baseline_evidence = scratch / "evidence/formal_baseline.json"
            _atomic_json(baseline_evidence, {"corpus": baseline, "classifications": classifications})
            evidence_hashes["evidence/formal_baseline.json"] = _sha256(baseline_evidence)
            state["formalBaseline"] = {"passed": baseline["summary"]["passed"],
                                        "failed": baseline["summary"]["failed"],
                                        "classifications": classifications}
            _save_state(bundle, state, "baseline")
            failures = baseline["summary"]["failed"]
            unknown = sum(kind == "unknown" for kind in classifications.values())
            related = sum(kind == "target-related" for kind in classifications.values())
            if failures and (not related or unknown > max(2, failures * 3 // 4)):
                raise RepairError("BLOCKED_BASELINE_UNSTABLE: formal failures are not sufficiently "
                                  f"related to incident (target={related}, unknown={unknown}, total={failures})")
            _progress(start, "BASELINE", f"subprocess=lossless result={baseline['summary']['passed']}/40 PASS")
            failure_detail = ""
            for attempt in range(1, MAX_ATTEMPTS + 1):
                state["attempt"] = attempt
                _save_state(bundle, state, "repairing")
                _progress(start, "REPAIR", f"attempt {attempt}/{MAX_ATTEMPTS} "
                          f"model={REPAIR_MODEL} effort={REPAIR_REASONING_EFFORT} subprocess=codex result=starting")
                prompt = (f"Repair PhoneSaber recognition from main commit {safety['head']}. "
                          "Read evidence/analysis_report.json, evidence/analysis_report.md, "
                          "evidence/formal_baseline.json, evidence/camera_reference.py, "
                          "evidence/summary.json, selected compact frame contexts, and the "
                          "attached lossless PNGs. Verify the visual diagnosis against metadata "
                          "and recognition source before editing. "
                          "Verification after editing will run the pinned 40-case lossless "
                          "regression and ./tools/verify_phone_saber.sh (Tools, Detection "
                          "XCTest, Static BGRA, Release, git diff --check). "
                          "Edit only the existing paths under ios/ listed below. "
                          "Do not change UDP, gameplay, camera, recording, or unrelated files. "
                          "Use the attached selected lossless PNGs, compact contexts, actual formal "
                          "results, and checked-in camera.py as reference for the HSV convention. "
                          "Do not assume absence of metadata means zero/false. Do not alter formal "
                          "regression expectations to hide a failure. If evidence cannot support a "
                          "safe repair, return needs_more_evidence without edits. "
                          "A threshold change needs two independent visual examples; simple "
                          "threshold relaxation without a precise false-positive argument is forbidden. "
                          f"Allowed: {', '.join(REPAIR_FILES)}. "
                          f"Prior verification failure: {failure_detail[:3000] or 'none'}. "
                          "Return a concise JSON decision and summary.")
                state["models"]["repair"]["attempted"] = True
                _save_state(bundle, state, "repairing")
                response = _codex_call(binary, scratch, prompt, REPAIR_RESPONSE_SCHEMA, images,
                                       role="repair", writable=True,
                                       timeout=REPAIR_CODEX_TIMEOUT_SECONDS)
                state["models"]["repair"]["executed"] = True
                _save_state(bundle, state, "repairing")
                changed = _candidate_files(scratch, originals, evidence_hashes)
                if response["decision"] == "needs_more_evidence":
                    state["status"] = "needs_capture"
                    return _terminal_report(bundle, state, gate, attempts, review, verification,
                                            reason=response["summary"])
                if not changed or not any(path.endswith(("DetectionCore.swift", "BGRADetection.swift"))
                                          for path in changed):
                    raise RepairError("Codex made no recognition source change")
                if _git(repo, "status", "--porcelain"):
                    raise RepairError("repository changed while Codex prepared the candidate")
                _apply_candidate(repo, scratch, private, state, changed, bundle)
                try:
                    _progress(start, "VERIFY", f"attempt {attempt}; subprocess=lossless+verify_phone_saber.sh")
                    verification = _verify(repo, private, baseline, attempt, classifications)
                except RepairError as exc:
                    failure_detail = str(exc)
                    verification = {"passed": False, "reason": failure_detail,
                                    "attempt": attempt}
                    attempts.append({"attempt": attempt, "result": "failed", "files": changed,
                                     "reason": failure_detail})
                    _progress(start, "VERIFY", f"attempt {attempt} subprocess=verification result=FAIL {failure_detail[:200]}")
                    _restore_owned(repo, private, state)
                    _save_state(bundle, state, "repairing")
                    if attempt == MAX_ATTEMPTS:
                        raise
                    # Codex receives failure context in the next attempt; scratch retains edits.
                    continue
                diff = _current_diff(repo, changed)
                _progress(start, "REVIEW", f"model={REVIEW_MODEL} effort={REVIEW_REASONING_EFFORT} "
                          "subprocess=codex read-only; starting")
                state["models"]["review"]["attempted"] = True
                _save_state(bundle, state, "reviewing")
                review = _review(binary, scratch, plan, report, diff, verification, images, repo)
                state["models"]["review"]["executed"] = True
                _save_state(bundle, state, "reviewing")
                if review["decision"] != "approved":
                    attempts.append({"attempt": attempt, "result": "review_" + review["decision"],
                                     "files": changed, "reason": review["reason"]})
                    _restore_owned(repo, private, state)
                    if review["decision"] == "needs_more_evidence":
                        state["status"] = "needs_capture"
                        return _terminal_report(bundle, state, gate, attempts, review, verification,
                                                reason=f"independent review needs evidence: {review['reason']}")
                    raise RepairError(f"independent review rejected: {review['reason']}")
                attempts.append({"attempt": attempt, "result": "passed", "files": changed,
                                 "summary": response["summary"]})
                _progress(start, "REVIEW", "subprocess=codex read-only result=APPROVED")
                break
            _save_state(bundle, state, "committing")
            _progress(start, "COMMIT", f"subprocess=git files={len(changed)}; origin recheck and normal commit")
            commit = _commit_push(repo, bundle, state, changed)
            state["status"] = "repair_pushed"
            _progress(start, "PUSH", f"subprocess=git push result=PASS origin/main={commit}")
            return _terminal_report(bundle, state, gate, attempts, review, verification,
                                    reason="Verified repair reviewed, committed and pushed")
        except (RepairError, BundleError, OSError, ValueError) as exc:
            reason = str(exc)
            if isinstance(exc, RepairModelUnavailable):
                state["status"] = "MODEL_UNAVAILABLE"
            elif reason.startswith("BLOCKED_REMOTE_CHANGED"):
                state["status"] = "blocked_remote_changed"
            elif reason.startswith("BLOCKED_BASELINE_UNSTABLE"):
                state["status"] = "BLOCKED_BASELINE_UNSTABLE"
            elif "BLOCKED main safety gate" in reason or "main HEAD changed" in reason \
                    or "unexpected changes" in reason or reason.startswith("BLOCKED formal"):
                state["status"] = "blocked"
            else:
                state["status"] = "repair_failed"
            try:
                if state.get("pushedOriginMain"):
                    state["status"] = "repair_pushed"
                    return _terminal_report(bundle, state, gate, attempts, review, verification,
                                            reason="Push succeeded; report generation was interrupted")
                _discover_owned_commit(repo, state)
                if state.get("commit"):
                    _git(repo, "fetch", "origin")
                    if _git(repo, "rev-parse", "origin/main") == state["commit"]:
                        state["pushedOriginMain"] = state["commit"]
                        state["status"] = "repair_pushed"
                        return _terminal_report(bundle, state, gate, attempts, review, verification,
                                                reason="Push completed despite interrupted response")
                if state.get("commit") and not state.get("pushedOriginMain"):
                    _uncommit_unpushed(repo, state, changed)
                if state.get("ownedFiles"):
                    if state.get("baseHead") and _git(repo, "rev-parse", "HEAD") == state["baseHead"]:
                        _git(repo, "restore", "--staged", "--", *state["ownedFiles"])
                    _restore_owned(repo, private, state)
            except RepairError as rollback_error:
                state["status"] = "blocked"
                reason += f"; rollback requires manual inspection: {rollback_error}"
            _progress(start, "STOP", f"subprocess=none result={state['status']}: {reason[:250]}")
            return _terminal_report(bundle, state, gate, attempts, review, verification,
                                    reason=reason)


def record_analysis_model_unavailable(bundle: Path, reason: str) -> dict[str, Any]:
    """Persist a terminal model failure without entering repair or touching the repo."""
    if bundle.is_symlink():
        raise RepairError("bundle path must not be a symlink")
    bundle = bundle.resolve(strict=True)
    plan = input_plan(bundle, allow_reports=True)
    state = {
        "sessionID": plan.session_id, "status": "MODEL_UNAVAILABLE", "phase": "done",
        "attempt": 0, "dryRun": False, "ownedFiles": {}, "startedAt": time.time(),
        "models": {
            "analysis": {"model": ANALYSIS_MODEL,
                         "reasoningEffort": ANALYSIS_REASONING_EFFORT,
                         "attempted": True, "executed": False},
            "repair": {"model": REPAIR_MODEL, "reasoningEffort": REPAIR_REASONING_EFFORT,
                       "attempted": False, "executed": False},
            "review": {"model": REVIEW_MODEL, "reasoningEffort": REVIEW_REASONING_EFFORT,
                       "attempted": False, "executed": False},
        },
    }
    _save_state(bundle, state, "done")
    return _terminal_report(bundle, state, {}, [], {}, {}, reason=reason)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("--repair-dry-run", action="store_true")
    parser.add_argument("--codex-path")
    parser.add_argument("--max-images", type=int, default=12)
    arguments = parser.parse_args(argv)
    try:
        result = repair_bundle(arguments.bundle, codex_path=arguments.codex_path,
                               dry_run=arguments.repair_dry_run, max_images=arguments.max_images)
    except (RepairError, BundleError, OSError, ValueError) as exc:
        print(f"[AUTO_REPAIR][ERROR] {exc}", file=sys.stderr)
        return 2
    print(json.dumps(result, ensure_ascii=False))
    return 0 if result["status"] in {"needs_capture", "repair_pushed", "dry_run"} else 2


if __name__ == "__main__":
    raise SystemExit(main())
