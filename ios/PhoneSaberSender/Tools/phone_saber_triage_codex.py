#!/usr/bin/env python3
"""Analyze only selected PhoneSaber lossless PNGs with the local Codex CLI."""

from __future__ import annotations

import argparse
import copy
import json
import math
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
from dataclasses import dataclass
from pathlib import Path, PurePosixPath
from typing import Any

from phone_saber_triage_protocol import (
    MAX_BUNDLE_BYTES,
    MAX_CONTEXT_BYTES,
    MAX_IMAGES,
    MAX_PROMPT_BYTES,
    MAX_SUMMARY_BYTES,
    SESSION_RE,
    BundleError,
)


DEFAULT_MAX_IMAGES = 12
PER_FAILURE_TYPE = 2
EXPECTED_SUMMARY_SCOPE = "retained incident candidates and nearby context"
MAX_REPORT_BYTES = 512 * 1024
MAX_CODEX_SUMMARY_BYTES = 256 * 1024
MAX_CODEX_CONTEXT_BYTES = 32 * 1024
MAX_CODEX_METADATA_BYTES = 768 * 1024
CODEX_TIMEOUT_SECONDS = 600
ANALYSIS_MODEL = "gpt-6-luna"
ANALYSIS_REASONING_EFFORT = "max"
ESCALATION_MODEL = "gpt-6-sol"
ESCALATION_REASONING_EFFORT = "high"
MODEL_UNAVAILABLE_PATTERNS = (
    "unknown model", "unsupported model", "invalid model", "model not found",
    "model is not available", "model unavailable", "model is unavailable",
    "model is unsupported", "model is not supported", "not a valid model",
    "model does not exist",
    "unsupported reasoning effort", "invalid reasoning effort",
    "unknown reasoning effort", "reasoning effort is not supported",
)
GENERATED_REPORT_FILES = {
    "analysis_report.json", "analysis_report.md", "repair_status.json", "state.json",
    "repair_report.json", "repair_report.md", "review_report.json", "review_report.md",
    "final_report.md", "final_report.json", "baseline_regression.json",
}

REPAIR_ASSESSMENT_SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "properties": {
        "decision": {"type": "string", "enum": ["actionable", "needs_capture"]},
        "visible_saber_confirmed": {"type": "boolean"},
        "production_change_supported": {"type": "boolean"},
        "root_cause_stage": {"type": "string", "enum": [
            "segmentation", "candidate_generation", "eligibility", "ranking", "endpoint",
            "temporal", "capture", "unknown",
        ]},
        "diagnosis_consistent_with_metadata": {"type": "boolean"},
        "change_type": {"type": "string", "enum": [
            "none", "threshold", "candidate_logic", "endpoint_logic", "other_recognition",
        ]},
        "independent_visual_examples": {"type": "integer"},
        "affected_colors": {"type": "array", "items": {"type": "string", "enum": ["RED", "BLUE"]}},
        "evidence_image_ids": {"type": "array", "items": {"type": "string"}},
        "reason": {"type": "string"},
    },
    "required": ["decision", "visible_saber_confirmed", "production_change_supported",
                 "root_cause_stage", "diagnosis_consistent_with_metadata", "change_type",
                 "independent_visual_examples", "affected_colors", "evidence_image_ids", "reason"],
}

FINDING_SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "properties": {
        "classification": {"type": "array", "items": {"type": "string", "enum": list("ABCDEFG")}},
        "color": {"type": "string", "enum": ["RED", "BLUE", "BOTH", "UNKNOWN"]},
        "frame_ids": {"type": "array", "items": {"type": "integer"}},
        "image_ids": {"type": "array", "items": {"type": "string"}},
        "issue_type": {"type": "string"},
        "observation": {"type": "string"},
        "interpretation": {"type": "string"},
        "confidence": {"type": "string", "enum": ["high", "medium", "low"]},
    },
    "required": ["classification", "color", "frame_ids", "image_ids", "issue_type",
                 "observation", "interpretation", "confidence"],
}

OUTPUT_SCHEMA = {
    "type": "object",
    "additionalProperties": False,
    "properties": {
        "session_summary": {"type": "string"},
        "false_negatives": {"type": "array", "items": FINDING_SCHEMA},
        "wrong_candidate_and_endpoint_errors": {"type": "array", "items": FINDING_SCHEMA},
        "false_positive_suspects": {"type": "array", "items": FINDING_SCHEMA},
        "other_findings": {"type": "array", "items": FINDING_SCHEMA},
        "limitations": {"type": "array", "items": {"type": "string"}},
        "repair_assessment": REPAIR_ASSESSMENT_SCHEMA,
    },
    "required": ["session_summary", "false_negatives", "wrong_candidate_and_endpoint_errors",
                 "false_positive_suspects", "other_findings", "limitations", "repair_assessment"],
}


@dataclass(frozen=True)
class CodexInputImage:
    image_id: str
    image_path: Path
    context_path: Path
    frame_id: int
    failure_type: str


@dataclass(frozen=True)
class CodexInputPlan:
    session_id: str
    root: Path
    images: tuple[CodexInputImage, ...]

    @property
    def image_paths(self) -> tuple[Path, ...]:
        return tuple(image.image_path for image in self.images)

    @property
    def context_paths(self) -> tuple[Path, ...]:
        return tuple(image.context_path for image in self.images)

    @property
    def image_ids(self) -> tuple[str, ...]:
        return tuple(image.image_id for image in self.images)


class CodexUnavailable(RuntimeError):
    pass


class CodexFailed(RuntimeError):
    pass


class CodexModelUnavailable(CodexFailed):
    """The pinned model or reasoning effort was rejected; never retry with defaults."""


def is_model_unavailable(diagnostic: str) -> bool:
    normalized = diagnostic.casefold()
    if any(pattern in normalized for pattern in MODEL_UNAVAILABLE_PATTERNS):
        return True
    # CLI/API diagnostics often put the requested model ID between these words.
    if re.search(r"\bmodel\b.{0,120}\b(?:unavailable|not available|not supported|unsupported|"
                 r"not found|does not exist|unknown|invalid)\b", normalized, re.DOTALL):
        return True
    if "model_reasoning_effort" in normalized and any(
            word in normalized for word in ("invalid", "unknown", "unsupported", "not supported")):
        return True
    return False


def input_plan(bundle_dir: Path, max_images: int = DEFAULT_MAX_IMAGES, *,
               allow_reports: bool = False) -> CodexInputPlan:
    if not 0 <= max_images <= MAX_IMAGES:
        raise ValueError(f"max_images must be between 0 and {MAX_IMAGES}")
    if bundle_dir.is_symlink():
        raise BundleError("Codex input must be a real bundle directory")
    root = bundle_dir.resolve(strict=True)
    if not root.is_dir():
        raise BundleError("Codex input must be a real bundle directory")
    summary_path = root / "summary.json"
    prompt_path = root / "prompt.md"
    if summary_path.is_symlink() or not summary_path.is_file() \
            or prompt_path.is_symlink() or not prompt_path.is_file():
        raise BundleError("summary.json or prompt.md is missing")
    if summary_path.stat().st_size > min(MAX_SUMMARY_BYTES, MAX_CODEX_SUMMARY_BYTES) \
            or prompt_path.stat().st_size > MAX_PROMPT_BYTES:
        raise BundleError("summary.json or prompt.md exceeds its size limit")
    try:
        summary = json.loads(summary_path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise BundleError("summary.json is malformed") from exc
    if not isinstance(summary, dict) or summary.get("formatVersion") != 1:
        raise BundleError("summary.json schema is unsupported")
    allowed_summary_keys = {"formatVersion", "sessionID", "recordedFrameCount",
                            "redBlueDetectionSummary", "dropoutSummary", "selectedImageCount",
                            "incidentCount", "incidents", "images", "limits", "groundTruth",
                            "summaryScope", "retainedIncidentContextFrames",
                            "motionEventSummary"}
    if not set(summary).issubset(allowed_summary_keys):
        raise BundleError("summary.json contains non-triage or full-session metadata")
    motion_summary = summary.get("motionEventSummary")
    if motion_summary is not None:
        if not isinstance(motion_summary, dict) or not isinstance(
                motion_summary.get("events"), list):
            raise BundleError("motion event summary is malformed")
        seen_event_ids: set[int] = set()
        for event in motion_summary["events"]:
            if not isinstance(event, list) or len(event) != 4 \
                    or isinstance(event[0], bool) or not isinstance(event[0], int) \
                    or event[0] in seen_event_ids \
                    or event[1] not in {"red", "blue", "both"} \
                    or not isinstance(event[2], (int, float)) \
                    or isinstance(event[2], bool) or not math.isfinite(event[2]) \
                    or event[3] not in {"selected", "lower_score", "memory",
                                         "duplicate", "image_limit", "byte_limit"}:
                raise BundleError("motion event selection ledger is malformed")
            seen_event_ids.add(event[0])
    summary_scope = summary.get("summaryScope")
    if not isinstance(summary_scope, str) or summary_scope != EXPECTED_SUMMARY_SCOPE:
        raise BundleError("summary.json has an invalid summaryScope")
    retained_context_frames = summary.get("retainedIncidentContextFrames")
    if isinstance(retained_context_frames, bool) or not isinstance(retained_context_frames, int) \
            or retained_context_frames < 0:
        raise BundleError("summary.json has an invalid retainedIncidentContextFrames")
    session_id = summary.get("sessionID")
    images = summary.get("images")
    count = summary.get("selectedImageCount")
    for key in ("redBlueDetectionSummary", "dropoutSummary"):
        if not isinstance(summary.get(key), dict) or not {"red", "blue"}.issubset(summary[key]):
            raise BundleError(f"summary.json has no RED/BLUE {key}")
    incident_count = summary.get("incidentCount")
    if isinstance(incident_count, bool) or not isinstance(incident_count, int) or incident_count < 0:
        raise BundleError("summary.json has an invalid incidentCount")
    if not isinstance(session_id, str) or not SESSION_RE.fullmatch(session_id) \
            or not isinstance(images, list) or isinstance(count, bool) or not isinstance(count, int) \
            or count < 0 or count != len(images) or count > max_images:
        raise BundleError("selected image count or session id exceeds the configured limit")
    selected_images: list[CodexInputImage] = []
    unique_image_paths: set[Path] = set()
    unique_context_paths: set[Path] = set()
    unique_image_basenames: set[str] = set()
    type_counts: dict[str, int] = {}
    motion_roles: set[tuple[int, str]] = set()
    for image in images:
        if not isinstance(image, dict):
            raise BundleError("summary image entry is invalid")
        image_relative = _safe_relative(image.get("path"), "images", ".png")
        context_relative = _safe_relative(image.get("frameContextPath"), "frames", ".json")
        image_path = root / image_relative
        context_path = root / context_relative
        for candidate in (image_path, context_path):
            if candidate.is_symlink() or candidate.parent.is_symlink() or not candidate.is_file():
                raise BundleError(f"selected Codex input is missing or is a symlink: {candidate.name}")
            if not candidate.resolve(strict=True).is_relative_to(root):
                raise BundleError(f"selected Codex input escapes the bundle: {candidate.name}")
        if image_path in unique_image_paths or context_path in unique_context_paths:
            raise BundleError("summary contains duplicate selected file paths")
        normalized_basename = image_path.name.casefold()
        if normalized_basename in unique_image_basenames:
            raise BundleError("summary contains ambiguous selected image basenames")
        unique_image_paths.add(image_path)
        unique_context_paths.add(context_path)
        unique_image_basenames.add(normalized_basename)
        if context_path.stat().st_size > min(MAX_CONTEXT_BYTES, MAX_CODEX_CONTEXT_BYTES):
            raise BundleError("frame context exceeds its size limit")
        _validate_context(context_path, session_id, image.get("frameID"))
        with image_path.open("rb") as image_file:
            if image_file.read(8) != b"\x89PNG\r\n\x1a\n":
                raise BundleError(f"selected lossless image is not a PNG: {image_path.name}")
        failure_type = image.get("failureType")
        if not isinstance(failure_type, str) or not failure_type or len(failure_type) > 100:
            raise BundleError("summary image has no failure type")
        frame_id = image.get("frameID")
        if isinstance(frame_id, bool) or not isinstance(frame_id, int):
            raise BundleError("summary image has an invalid frameID")
        type_counts[failure_type] = type_counts.get(failure_type, 0) + 1
        if failure_type.startswith("motion_event_") and "eventIndex" not in image:
            raise BundleError("motion event failure type lacks validated event metadata")
        if "eventIndex" in image:
            if not failure_type.startswith("motion_event_") \
                    or image.get("role") not in {"event_pre", "event_at", "event_post"} \
                    or isinstance(image["eventIndex"], bool) \
                    or not isinstance(image["eventIndex"], int) \
                    or isinstance(image.get("anomalyScore"), bool) \
                    or not isinstance(image.get("anomalyScore"), (int, float)) \
                    or not isinstance(image.get("signals"), list) \
                    or image.get("signalAggregation") != "event_max_per_kind":
                raise BundleError("motion event image metadata is invalid")
            if not math.isfinite(image["anomalyScore"]) \
                    or not _valid_motion_signals(image["signals"]) \
                    or (image["eventIndex"], image["role"]) in motion_roles:
                raise BundleError("motion event image signal or role is invalid")
            motion_roles.add((image["eventIndex"], image["role"]))
            context_event = json.loads(context_path.read_text(encoding="utf-8")).get("motionEvent")
            if context_event != {"eventIndex": image["eventIndex"],
                                 "role": image["role"],
                                 "score": image["anomalyScore"],
                                 "signals": image["signals"],
                                 "signalAggregation": "event_max_per_kind"}:
                raise BundleError("motion event summary and context disagree")
        elif any(key in image for key in (
                "role", "anomalyScore", "signals", "signalAggregation")):
            raise BundleError("motion event image fields have no event index")
        selected_images.append(CodexInputImage(
            image_id=f"image_{len(selected_images) + 1:03d}",
            image_path=image_path,
            context_path=context_path,
            frame_id=frame_id,
            failure_type=failure_type,
        ))
    if any(count > (3 if kind.startswith("motion_event_") else PER_FAILURE_TYPE)
           for kind, count in type_counts.items()):
        raise BundleError("per-failure-type image limit exceeds two")
    if motion_roles:
        ledger = {item[0]: item for item in motion_summary["events"]} if motion_summary else {}
        if any(event_id not in ledger or ledger[event_id][3] != "selected"
               for event_id, _ in motion_roles):
            raise BundleError("selected motion images lack a matching event ledger entry")

    expected = {"summary.json", "prompt.md"}
    expected.update(image.image_path.relative_to(root).as_posix() for image in selected_images)
    expected.update(image.context_path.relative_to(root).as_posix() for image in selected_images)
    actual: set[str] = set()
    for path in root.rglob("*"):
        if path.is_symlink():
            raise BundleError("Codex bundle cannot contain symlinks or special files")
        if path.is_dir():
            continue
        if not path.is_file():
            raise BundleError("Codex bundle cannot contain symlinks or special files")
        actual.add(path.relative_to(root).as_posix())
    if allow_reports:
        generated = actual & GENERATED_REPORT_FILES
        if any((root / name).stat().st_size > MAX_REPORT_BYTES for name in generated):
            raise BundleError("generated triage report exceeds its size limit")
        expected.update(generated)
    if actual != expected:
        raise BundleError("Codex bundle contains files outside the selected summary, PNG, and contexts")
    metadata_bytes = summary_path.stat().st_size + sum(
        image.context_path.stat().st_size for image in selected_images
    )
    total_bytes = metadata_bytes + prompt_path.stat().st_size + sum(
        image.image_path.stat().st_size for image in selected_images
    )
    if metadata_bytes > MAX_CODEX_METADATA_BYTES:
        raise BundleError("selected metadata exceeds the Codex context budget")
    if total_bytes > MAX_BUNDLE_BYTES:
        raise BundleError("Codex input exceeds the bundle size limit")
    return CodexInputPlan(session_id, root, tuple(selected_images))


def dry_run_text(bundle_dir: Path, max_images: int = DEFAULT_MAX_IMAGES) -> str:
    plan = input_plan(bundle_dir, max_images=max_images)
    files = ["summary.json"]
    files.extend(path.relative_to(plan.root).as_posix() for path in plan.context_paths)
    files.extend(path.relative_to(plan.root).as_posix() for path in plan.image_paths)
    lines = [
        f"DRY RUN — Codex call skipped for {plan.session_id}",
        f"Selected lossless PNGs: {len(plan.image_paths)} / limit {max_images} (hard limit {MAX_IMAGES})",
        "Codex --image attachments:",
    ]
    lines.extend(f"  {path.relative_to(plan.root).as_posix()}" for path in plan.image_paths)
    lines.append("Image reference IDs provided to Codex:")
    lines.extend(
        f"  {image.image_id} -> {image.image_path.name} "
        f"(incident={image.failure_type}, context={image.context_path.relative_to(plan.root).as_posix()})"
        for image in plan.images
    )
    lines.append("Compact metadata files:")
    lines.extend(f"  {name}" for name in files if not name.endswith(".png"))
    lines.extend([
        "Prompt: inline A–G instructions sent through stdin; bundle prompt.md is not attached",
        "Excluded: video, full metadata.json, unselected PNGs",
    ])
    return "\n".join(lines)


def find_codex_binary(explicit: str | None = None) -> str | None:
    if explicit:
        path = Path(explicit).expanduser()
        return str(path) if path.is_file() and os.access(path, os.X_OK) else None
    found = shutil.which("codex")
    if found:
        return found
    app_relative = Path("Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex")
    candidates = [
        Path("/Applications/ChatGPT.app") / app_relative,
        Path.home() / "Applications/ChatGPT.app" / app_relative,
        Path("/opt/homebrew/bin/codex"),
        Path("/usr/local/bin/codex"),
    ]
    return next((str(path) for path in candidates if path.is_file() and os.access(path, os.X_OK)), None)


def _decision_trace_summary(plan: Any) -> list[dict[str, Any]]:
    """Read only already-selected compact contexts; do not infer absent metrics."""
    summary: list[dict[str, Any]] = []
    seen: set[tuple[int, str, int, str]] = set()
    for image in plan.images:
        context = json.loads(image.context_path.read_text(encoding="utf-8"))
        for frame in context["frames"]:
            for color in ("red", "blue"):
                for candidate in frame[color].get("candidateDecisionTrace", []):
                    for rule in candidate["rules"]:
                        key = (frame["frameID"], color, candidate["index"], rule["name"])
                        if key in seen:
                            continue
                        seen.add(key)
                        summary.append({"frameID": frame["frameID"], "color": color,
                            "candidate": candidate["index"], "source": candidate["sourceType"],
                            "failedRule": rule["name"], "value": rule["value"],
                            "comparison": rule["comparison"], "threshold": rule["threshold"]})
    return summary[:60]


def _needs_internal_diagnostics(analysis: dict[str, Any]) -> bool:
    assessment = analysis["repair_assessment"]
    if assessment["decision"] != "needs_capture" \
            or assessment["root_cause_stage"] != "eligibility":
        return False
    reason = " ".join([assessment["reason"], *analysis["limitations"]]).lower()
    return any(term in reason for term in (
        "missing rejection", "missing eligibility", "missing candidate trace",
        "rejection detail", "rejection reason", "eligibility detail",
        "rejection diagnostic", "eligibility rule", "candidate-level rejection",
        "decision trace", "failed rule", "reject rule"))


def _needs_second_opinion(analysis: dict[str, Any], traces: list[dict[str, Any]]) -> bool:
    assessment = analysis["repair_assessment"]
    reason = assessment["reason"].lower()
    colors = set(assessment["affected_colors"])
    return assessment["decision"] == "needs_capture" \
        and assessment["visible_saber_confirmed"] \
        and assessment["root_cause_stage"] in {
            "segmentation", "candidate_generation", "eligibility", "ranking", "endpoint"} \
        and bool(assessment["evidence_image_ids"]) \
        and any(item["color"].upper() in colors
                and item["value"] is not None and item["threshold"] is not None
                for item in traces) \
        and any(term in reason for term in (
            "uncertain", "ambiguous", "cannot determine", "cannot conclude",
            "unclear which", "判断でき", "曖昧")) \
        and not any(term in reason for term in (
            "new capture", "more images", "additional images", "not visible",
            "occluded", "insufficient visual examples")) \
        and not _needs_internal_diagnostics(analysis)


def analyze_bundle(
    bundle_dir: Path,
    *,
    max_images: int = DEFAULT_MAX_IMAGES,
    codex_path: str | None = None,
    timeout_seconds: int = CODEX_TIMEOUT_SECONDS,
    dry_run: bool = False,
) -> dict[str, Any]:
    plan = input_plan(bundle_dir, max_images=max_images)
    bundle_dir = plan.root
    if dry_run:
        print(dry_run_text(bundle_dir, max_images=max_images), flush=True)
        return {"status": "dry_run", "sessionID": plan.session_id}
    if not plan.image_paths:
        return _write_no_image_report(bundle_dir, plan)

    binary = find_codex_binary(codex_path)
    if binary is None:
        raise CodexUnavailable("Codex CLI not found; received bundle is preserved")

    final_json = bundle_dir / "analysis_report.json"
    final_markdown = bundle_dir / "analysis_report.md"
    if final_json.exists() or final_markdown.exists():
        raise CodexFailed("analysis report already exists; refusing to overwrite it")

    with tempfile.TemporaryDirectory(prefix="phonesaber-codex-") as temporary_name:
        temporary = Path(temporary_name)
        input_root = temporary / "input"
        input_root.mkdir()
        shutil.copyfile(plan.root / "summary.json", input_root / "summary.json")
        selected_images: list[Path] = []
        for image in plan.images:
            source = image.image_path
            relative = source.relative_to(plan.root)
            target = input_root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)
            selected_images.append(target)
        for source in plan.context_paths:
            relative = source.relative_to(plan.root)
            target = input_root / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(source, target)

        schema_path = temporary / "analysis_schema.json"
        response_path = temporary / "codex_response.json"
        schema_path.write_text(
            json.dumps(_output_schema(plan.image_ids), ensure_ascii=False), encoding="utf-8"
        )
        prompt = _codex_prompt(plan.session_id, plan.images, plan.root)
        command = [
            binary,
            "exec",
            "--model", ANALYSIS_MODEL,
            "-c", f"model_reasoning_effort={json.dumps(ANALYSIS_REASONING_EFFORT)}",
            "--ephemeral",
            "--sandbox", "read-only",
            "--skip-git-repo-check",
            "--cd", str(input_root),
            "--output-schema", str(schema_path),
            "--output-last-message", str(response_path),
        ]
        for image_path in selected_images:
            command.extend(["--image", str(image_path)])
        command.append("-")
        def run_analysis(model: str, effort: str, text_prompt: str, label: str) -> dict[str, Any]:
            configured = command.copy()
            configured[configured.index(ANALYSIS_MODEL)] = model
            configured[configured.index(f"model_reasoning_effort={json.dumps(ANALYSIS_REASONING_EFFORT)}")] = (
                f"model_reasoning_effort={json.dumps(effort)}")
            started = time.monotonic()
            print(f"[AUTO_REPAIR][{label}] model={model} effort={effort} "
                  "subprocess=codex read-only result=starting", flush=True)
            try:
                completed = subprocess.run(
                    configured, cwd=input_root, input=text_prompt,
                    stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                    timeout=timeout_seconds, check=False,
                )
            except subprocess.TimeoutExpired as exc:
                raise CodexFailed(f"Codex CLI timed out after {timeout_seconds} seconds") from exc
            except OSError as exc:
                raise CodexFailed(f"Codex CLI could not start: {exc}") from exc
            if completed.returncode != 0:
                diagnostic = (completed.stderr or completed.stdout).strip().splitlines()
                detail = diagnostic[-1][:300] if diagnostic else f"exit {completed.returncode}"
                if is_model_unavailable(completed.stderr + "\n" + completed.stdout):
                    raise CodexModelUnavailable(
                        f"MODEL_UNAVAILABLE: analysis requires {model}/{effort}: {detail}")
                raise CodexFailed(f"Codex CLI failed ({completed.returncode}): {detail}")
            if not response_path.is_file() or response_path.stat().st_size > MAX_REPORT_BYTES:
                raise CodexFailed("Codex CLI returned no bounded final report")
            try:
                result = json.loads(response_path.read_text(encoding="utf-8"))
                _validate_analysis(result, set(plan.image_ids))
            except (OSError, UnicodeError, json.JSONDecodeError, BundleError, TypeError) as exc:
                raise CodexFailed(f"Codex CLI returned invalid structured output: {exc}") from exc
            print(f"[AUTO_REPAIR][{label}] model={model} effort={effort} "
                  f"subprocess=codex read-only result=complete "
                  f"elapsed={time.monotonic() - started:.1f}s", flush=True)
            return result

        analysis = run_analysis(ANALYSIS_MODEL, ANALYSIS_REASONING_EFFORT, prompt, "ANALYSIS")
        traces = _decision_trace_summary(plan)
        affected = set(analysis["repair_assessment"]["affected_colors"])
        relevant_traces = [item for item in traces if item["color"].upper() in affected]
        initial_assessment = copy.deepcopy(analysis["repair_assessment"])
        reanalysis_executed = False
        escalation_executed = False
        for item in traces[:12]:
            print(f"[AUTO_REPAIR][DIAGNOSTICS] stage=eligibility "
                  f"frame={item['frameID']} color={item['color']} "
                  f"candidate={item['candidate']} failedRule={item['failedRule']} "
                  f"value={item['value']} threshold={item['threshold']}", flush=True)
        if _needs_internal_diagnostics(analysis) and relevant_traces:
            enriched_prompt = (prompt + "\n\nDiagnostics enrichment: The following failed "
                "eligibility rules were extracted from the selected compact frame contexts. "
                "Re-evaluate once using these actual values and thresholds; keep the "
                "visual and repair safety requirements.\n" +
                json.dumps(relevant_traces, ensure_ascii=False))
            analysis = run_analysis(ANALYSIS_MODEL, ANALYSIS_REASONING_EFFORT,
                                    enriched_prompt, "REANALYSIS")
            reanalysis_executed = True
        if _needs_second_opinion(analysis, traces):
            reason = " ".join(analysis["repair_assessment"]["reason"].split())[:200]
            print(f"[AUTO_REPAIR][ESCALATION] model={ESCALATION_MODEL} "
                  f"effort={ESCALATION_REASONING_EFFORT} role=second-opinion "
                  f"reason={reason}", flush=True)
            second_prompt = (prompt + "\n\nSECOND_OPINION_ANALYSIS. Read-only. "
                "A prior Luna/MAX analysis could not decide despite available visual "
                "and eligibility evidence. Analyze independently. Do not edit source. "
                "Use the same conservative repair assessment and cite only selected images. "
                "Prior assessment and measured decision trace:\n" +
                json.dumps({"prior": analysis["repair_assessment"], "trace": traces},
                           ensure_ascii=False))
            try:
                analysis = run_analysis(ESCALATION_MODEL, ESCALATION_REASONING_EFFORT,
                                        second_prompt, "ESCALATION")
                escalation_executed = True
            except (CodexModelUnavailable, CodexFailed) as exc:
                print(f"[AUTO_REPAIR][ESCALATION] result=unavailable reason={exc}", flush=True)

    report = {
        "formatVersion": 3,
        "sessionID": plan.session_id,
        "analysisModel": ANALYSIS_MODEL,
        "analysisReasoningEffort": ANALYSIS_REASONING_EFFORT,
        "analysisExecuted": True,
        "analysisReanalysisExecuted": reanalysis_executed,
        "analysisEscalationModel": ESCALATION_MODEL,
        "analysisEscalationReasoningEffort": ESCALATION_REASONING_EFFORT,
        "analysisEscalationExecuted": escalation_executed,
        "analysisInitialAssessment": initial_assessment,
        "input": {
            "imageCount": len(plan.image_paths),
            "imagePaths": [path.relative_to(plan.root).as_posix() for path in plan.image_paths],
            "contextPaths": [path.relative_to(plan.root).as_posix() for path in plan.context_paths],
            "imageReferences": [
                {
                    "id": image.image_id,
                    "path": image.image_path.relative_to(plan.root).as_posix(),
                    "contextPath": image.context_path.relative_to(plan.root).as_posix(),
                    "frameID": image.frame_id,
                    "incidentType": image.failure_type,
                }
                for image in plan.images
            ],
            "videoIncluded": False,
            "fullMetadataIncluded": False,
        },
        "analysis": analysis,
    }
    markdown = render_markdown(plan.session_id, report["input"], analysis)
    _write_reports(bundle_dir, report, markdown)
    return {"status": "completed", "sessionID": plan.session_id,
            "imageCount": len(plan.image_paths), "jsonPath": str(final_json),
            "markdownPath": str(final_markdown)}


def _write_no_image_report(bundle_dir: Path, plan: CodexInputPlan) -> dict[str, Any]:
    if (bundle_dir / "analysis_report.json").exists() or (bundle_dir / "analysis_report.md").exists():
        raise CodexFailed("analysis report already exists; refusing to overwrite it")
    report = {
        "formatVersion": 3,
        "sessionID": plan.session_id,
        "analysisModel": ANALYSIS_MODEL,
        "analysisReasoningEffort": ANALYSIS_REASONING_EFFORT,
        "analysisExecuted": False,
        "input": {"imageCount": 0, "imagePaths": [], "contextPaths": [],
                  "imageReferences": [], "videoIncluded": False, "fullMetadataIncluded": False},
        "analysis": {
            "session_summary": "No lossless PNG met the triage selection rules. No Codex request was made.",
            "false_negatives": [], "wrong_candidate_and_endpoint_errors": [],
            "false_positive_suspects": [], "other_findings": [],
            "limitations": ["There were no selected lossless images to inspect."],
            "repair_assessment": {
                "decision": "needs_capture", "visible_saber_confirmed": False,
                "production_change_supported": False, "root_cause_stage": "unknown",
                "diagnosis_consistent_with_metadata": False, "change_type": "none",
                "independent_visual_examples": 0, "affected_colors": [],
                "evidence_image_ids": [], "reason": "No selected lossless image is available.",
            },
        },
    }
    markdown = render_markdown(plan.session_id, report["input"], report["analysis"])
    _write_reports(bundle_dir, report, markdown)
    return {"status": "no_images", "sessionID": plan.session_id, "imageCount": 0}


def _write_reports(bundle_dir: Path, report: dict[str, Any], markdown: str) -> None:
    json_path = bundle_dir / "analysis_report.json"
    markdown_path = bundle_dir / "analysis_report.md"
    temporary = Path(tempfile.mkdtemp(prefix=".phonesaber-report-", dir=bundle_dir))
    try:
        (temporary / json_path.name).write_text(
            json.dumps(report, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8"
        )
        (temporary / markdown_path.name).write_text(markdown, encoding="utf-8")
        os.replace(temporary / json_path.name, json_path)
        os.replace(temporary / markdown_path.name, markdown_path)
    except Exception:
        json_path.unlink(missing_ok=True)
        markdown_path.unlink(missing_ok=True)
        raise
    finally:
        shutil.rmtree(temporary, ignore_errors=True)


def _validate_analysis(value: Any, allowed_images: set[str]) -> None:
    required = {"session_summary", "false_negatives", "wrong_candidate_and_endpoint_errors",
                "false_positive_suspects", "other_findings", "limitations", "repair_assessment"}
    if not isinstance(value, dict) or set(value) != required:
        raise BundleError("response must contain exactly the required report sections")
    if not isinstance(value["session_summary"], str) or len(value["session_summary"]) > 16_000:
        raise BundleError("response has an invalid session summary")
    finding_keys = {"classification", "color", "frame_ids", "image_ids", "issue_type",
                    "observation", "interpretation", "confidence"}
    for key in ("false_negatives", "wrong_candidate_and_endpoint_errors",
                "false_positive_suspects", "other_findings"):
        if not isinstance(value[key], list) or len(value[key]) > 40:
            raise BundleError(f"response section is invalid: {key}")
        for finding in value[key]:
            if not isinstance(finding, dict) or set(finding) != finding_keys:
                raise BundleError(f"finding has an invalid shape: {key}")
            classifications = finding["classification"]
            if not isinstance(classifications, list) or not classifications \
                    or not all(isinstance(item, str) and len(item) == 1 and item in "ABCDEFG"
                               for item in classifications):
                raise BundleError(f"finding has an invalid A–G classification: {key}")
            if not isinstance(finding["color"], str) \
                    or finding["color"] not in {"RED", "BLUE", "BOTH", "UNKNOWN"} \
                    or not isinstance(finding["confidence"], str) \
                    or finding["confidence"] not in {"high", "medium", "low"}:
                raise BundleError(f"finding has an invalid color or confidence: {key}")
            if not isinstance(finding["frame_ids"], list) or not all(
                    isinstance(item, int) and not isinstance(item, bool) for item in finding["frame_ids"]):
                raise BundleError(f"finding has invalid frame ids: {key}")
            if not isinstance(finding["image_ids"], list) or not all(
                    isinstance(item, str) for item in finding["image_ids"]):
                raise BundleError(f"finding has invalid image IDs: {key}")
            if len(finding["image_ids"]) != len(set(finding["image_ids"])):
                raise BundleError(f"finding contains duplicate image IDs: {key}")
            if not set(finding["image_ids"]).issubset(allowed_images):
                raise BundleError(f"finding references an image ID that was not sent: {key}")
            for field in ("issue_type", "observation", "interpretation"):
                if not isinstance(finding[field], str) or len(finding[field]) > 8_000:
                    raise BundleError(f"finding has invalid {field}: {key}")
    if not isinstance(value["limitations"], list) or len(value["limitations"]) > 100 \
            or not all(isinstance(item, str) and len(item) <= 3_000 for item in value["limitations"]):
        raise BundleError("limitations must be a bounded list of strings")
    assessment = value["repair_assessment"]
    if not isinstance(assessment, dict) or set(assessment) != set(REPAIR_ASSESSMENT_SCHEMA["required"]):
        raise BundleError("repair assessment has an invalid shape")
    for key in ("decision", "root_cause_stage", "change_type"):
        if assessment[key] not in REPAIR_ASSESSMENT_SCHEMA["properties"][key]["enum"]:
            raise BundleError(f"repair assessment has an invalid {key}")
    for key in ("visible_saber_confirmed", "production_change_supported",
                "diagnosis_consistent_with_metadata"):
        if not isinstance(assessment[key], bool):
            raise BundleError(f"repair assessment has an invalid {key}")
    examples = assessment["independent_visual_examples"]
    if isinstance(examples, bool) or not isinstance(examples, int) or not 0 <= examples <= MAX_IMAGES:
        raise BundleError("repair assessment has an invalid independent_visual_examples")
    for key, allowed in (("affected_colors", {"RED", "BLUE"}),
                         ("evidence_image_ids", allowed_images)):
        items = assessment[key]
        if not isinstance(items, list) or not all(isinstance(item, str) for item in items) \
                or len(items) != len(set(items)) or not set(items).issubset(allowed):
            raise BundleError(f"repair assessment has invalid {key}")
    if not isinstance(assessment["reason"], str) or not 1 <= len(assessment["reason"]) <= 3_000:
        raise BundleError("repair assessment has an invalid reason")


def render_markdown(session_id: str, input_details: dict[str, Any], analysis: dict[str, Any]) -> str:
    lines = [
        f"# PhoneSaber analysis: {session_id}",
        "",
        f"Selected lossless PNGs: {input_details['imageCount']}",
        "Video included: no",
        "Full metadata included: no",
        "",
        "## Selected image references",
        "",
    ]
    for image in input_details.get("imageReferences", []):
        lines.append(
            f"- {image['id']} — `{image['path']}` "
            f"(incident: {image['incidentType']}; context: `{image['contextPath']}`)"
        )
    if not input_details.get("imageReferences"):
        lines.append("None selected.")
    lines.extend([
        "",
        "## Session summary",
        "",
        analysis["session_summary"],
    ])
    sections = [
        ("False negatives", "false_negatives"),
        ("Wrong candidate and endpoint errors", "wrong_candidate_and_endpoint_errors"),
        ("False positive suspects", "false_positive_suspects"),
        ("Other findings", "other_findings"),
    ]
    for title, key in sections:
        lines.extend(["", f"## {title}", ""])
        findings = analysis[key]
        if not findings:
            lines.append("None reported.")
            continue
        for finding in findings:
            classifications = ", ".join(finding.get("classification", [])) or "G"
            frame_ids = ", ".join(str(item) for item in finding.get("frame_ids", [])) or "unknown"
            images = ", ".join(finding.get("image_ids", [])) or "none"
            lines.extend([
                f"### {finding.get('color', 'UNKNOWN')} — {finding.get('issue_type', 'Finding')} ({classifications})",
                f"Frames: {frame_ids}; images: {images}; confidence: {finding.get('confidence', 'low')}.",
                "",
                f"Observation: {finding.get('observation', '')}",
                "",
                f"Interpretation: {finding.get('interpretation', '')}",
            ])
    lines.extend(["", "## Limitations", ""])
    lines.extend(f"- {item}" for item in analysis["limitations"])
    assessment = analysis["repair_assessment"]
    lines.extend(["", "## Repair assessment", "",
                  f"Decision: {assessment['decision']}",
                  f"Reason: {assessment['reason']}"])
    lines.append("")
    return "\n".join(lines)


def _output_schema(image_ids: tuple[str, ...]) -> dict[str, Any]:
    schema = copy.deepcopy(OUTPUT_SCHEMA)
    for section in ("false_negatives", "wrong_candidate_and_endpoint_errors",
                    "false_positive_suspects", "other_findings"):
        schema["properties"][section]["items"]["properties"]["image_ids"]["items"] = {
            "type": "string", "enum": list(image_ids),
        }
    schema["properties"]["repair_assessment"]["properties"]["evidence_image_ids"]["items"] = {
        "type": "string", "enum": list(image_ids),
    }
    return schema


def _codex_prompt(session_id: str, images: tuple[CodexInputImage, ...], bundle_root: Path) -> str:
    image_map = "\n".join(
        f"- Image ID: {image.image_id}\n"
        f"  Filename: {image.image_path.name}\n"
        f"  Incident type: {image.failure_type}\n"
        f"  Context filename: {image.context_path.relative_to(bundle_root).as_posix()}"
        for image in images
    ) or "- No images were selected."
    return f"""Analyze PhoneSaber Debug Recording {session_id} using exactly {len(images)} attached selected lossless PNGs.

Evidence rules:
- The selected lossless PNGs are pixel ground truth. H.264 video is never ground truth.
- Read only summary.json and the selected JSON files under frames/. Do not seek, request, or infer from video, full-session metadata, or unselected frames.
- Treat metadata, including detected=false, as detector output; it does not prove that a saber was physically absent or outside the camera view.
- Treat the selected lossless PNG pixels as visual ground truth. State what is visible in each selected PNG before interpreting metadata.
- Analyze only the selected image attachments listed below and their listed compact context files. Do not add images that were not selected.
- In every finding, image_ids may contain only the exact Image ID values listed below. Never put a filesystem path, filename, temporary path, alias, shortened name, or invented identifier in image_ids. Do not guess, abbreviate, or rewrite an image reference.
- Use an empty image_ids array when no selected image supports a finding. Do not duplicate an Image ID within a finding.
- Keep false negatives, wrong-candidate/endpoint errors, and false-positive suspects in separate output sections.
- Classify findings: A = capture/data artifact; B = false negative or candidate=0; C = candidate exists but eligible=0; D = wrong candidate or endpoint jump; E = false-positive suspect (broad/coreless or identical endpoint); F = temporal dropout/continuity; G = insufficient evidence or other.
- If evidence is insufficient, say so and do not recommend production recognition changes.
- For eligibility dropouts, inspect candidateDecisionTrace for each selected frame. Name the failed rule, actual measured value and threshold, whether it repeats across independent visible examples, and false-positive risk. A threshold must not be loosened solely because a candidate was rejected.
- For motion events, compare event_pre, event_at, and event_post PNGs with the score, measured signals, detected states, and endpoints in their compact contexts. Signals marked event_max_per_kind may peak on different frames within the merged event. The signal is a selection heuristic, not proof of a saber or a recognition error. A frame gap, processing delay, or saber outside the image is a capture/latency finding, not a recognition repair target; say this explicitly in the analysis.
- Do not edit, create, or propose applying production code. Return a concise JSON object matching the supplied schema exactly.
- Complete repair_assessment conservatively. Mark actionable only when selected PNG pixels visibly confirm a real saber, the metadata supports a specific recognition-stage cause, and the evidence supports a production change. Otherwise use needs_capture. A detector dropout or endpoint jump alone is not visual proof.
- For a threshold proposal, count independent visually supported examples; a single example is insufficient. Use only listed Image IDs in evidence_image_ids. Describe uncertainty in reason. Regression coverage is checked separately by the Mac gate.

Selected image reference map:
{image_map}
"""


def _validate_context(path: Path, session_id: Any, selected_frame_id: Any) -> None:
    try:
        context = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise BundleError(f"frame context is malformed: {path.name}") from exc
    required_top = {"sessionID", "selectedFrameID", "selectedColor", "selectedFailureType",
                    "selectedReasons", "contextRadiusFrames", "frames"}
    allowed_top = required_top | {"motionEvent"}
    if not isinstance(context, dict) or not required_top.issubset(context) \
            or not set(context).issubset(allowed_top) \
            or context.get("sessionID") != session_id or context.get("selectedFrameID") != selected_frame_id \
            or not isinstance(context.get("selectedFrameID"), int) \
            or isinstance(context.get("selectedFrameID"), bool) \
            or not isinstance(context.get("selectedColor"), str) \
            or context.get("selectedColor") not in {"red", "blue", "both"} \
            or not isinstance(context.get("selectedFailureType"), str) \
            or len(context.get("selectedFailureType", "")) > 100 \
            or not isinstance(context.get("contextRadiusFrames"), int) \
            or context.get("contextRadiusFrames") != 2 \
            or not isinstance(context.get("selectedReasons"), list) \
            or len(context.get("selectedReasons", [])) > 12 \
            or not all(isinstance(item, str) and len(item) <= 500 for item in context["selectedReasons"]) \
            or not isinstance(context.get("frames"), list) or not 1 <= len(context["frames"]) <= 5:
        raise BundleError(f"frame context is outside the permitted compact shape: {path.name}")
    if "motionEvent" in context:
        event = context["motionEvent"]
        if not isinstance(event, dict) or set(event) != {
                "eventIndex", "role", "score", "signals", "signalAggregation"} \
                or isinstance(event["eventIndex"], bool) \
                or not isinstance(event["eventIndex"], int) \
                or event["role"] not in {"event_pre", "event_at", "event_post"} \
                or isinstance(event["score"], bool) \
                or not isinstance(event["score"], (int, float)) \
                or not math.isfinite(event["score"]) \
                or event["signalAggregation"] != "event_max_per_kind" \
                or not _valid_motion_signals(event["signals"]):
            raise BundleError(f"invalid motion event context: {path.name}")
    allowed_frame = {"frameID", "timestamp", "red", "blue",
                     "processingTimeSeconds", "motionEventIndex"}
    allowed_color = {"detected", "predictionUsed", "detectionSucceeded", "maskPixelCount",
                     "morphologyPixelCount", "connectedComponentCount", "candidateCount",
                     "eligibleCandidateCount", "selectedCandidateType", "score", "endpoint",
                     "selectedCandidateIndex", "topScoreGap", "minimumFailedRuleMargin",
                     "rawPCASpan", "robustMainIntervalLength", "continuity", "density",
                     "colorPurity", "coreSupport", "highBrightnessCoverage",
                     "candidateDecisionTrace", "failureStage"}
    for frame in context["frames"]:
        if not isinstance(frame, dict) or not {"frameID", "timestamp", "red", "blue"}.issubset(frame) \
                or not set(frame).issubset(allowed_frame) \
                or not isinstance(frame.get("frameID"), int) \
                or isinstance(frame.get("frameID"), bool) \
                or not isinstance(frame.get("timestamp"), (int, float)):
            raise BundleError(f"frame context has an invalid frame entry: {path.name}")
        if ("processingTimeSeconds" in frame and (
                not isinstance(frame["processingTimeSeconds"], (int, float))
                or isinstance(frame["processingTimeSeconds"], bool)
                or not math.isfinite(frame["processingTimeSeconds"])
                or frame["processingTimeSeconds"] < 0)) \
                or ("motionEventIndex" in frame and (
                    isinstance(frame["motionEventIndex"], bool)
                    or not isinstance(frame["motionEventIndex"], int))):
            raise BundleError(f"frame context has invalid motion metrics: {path.name}")
        for color in ("red", "blue"):
            values = frame.get(color)
            if not isinstance(values, dict) or not set(values).issubset(allowed_color):
                raise BundleError(f"frame context has unexpected {color} metadata: {path.name}")
            if "endpoint" in values and (not isinstance(values["endpoint"], list)
                    or len(values["endpoint"]) != 4
                    or not all(isinstance(point, (int, float)) and not isinstance(point, bool)
                               for point in values["endpoint"])):
                raise BundleError(f"frame context has an invalid endpoint: {path.name}")
            for key, value in values.items():
                if key == "candidateDecisionTrace":
                    _validate_decision_trace(value, path.name)
                elif key not in {"endpoint", "selectedCandidateType", "failureStage"} \
                        and not isinstance(value, (int, float, bool)):
                    raise BundleError(f"frame context has a non-numeric metric: {path.name}")
                if key == "selectedCandidateType" and (not isinstance(value, str) or len(value) > 100):
                    raise BundleError(f"frame context has an invalid candidate type: {path.name}")
                if key == "failureStage" and value != "eligibility":
                    raise BundleError(f"frame context has an invalid failure stage: {path.name}")


def _valid_motion_signals(value: Any) -> bool:
    if not isinstance(value, list) or not 1 <= len(value) <= 12:
        return False
    for signal in value:
        if not isinstance(signal, dict) or set(signal) != {
                "kind", "color", "value", "threshold", "score"}:
            return False
        if not isinstance(signal["kind"], str) or signal["kind"] not in {
                "dropout", "flicker", "endpoint_jump", "length_change", "prediction_error",
                "multiple_eligible", "candidate_ambiguity", "candidate_switch", "near_miss",
                "frame_interval", "frame_gap", "processing_time"} \
                or signal["color"] not in {"red", "blue", "both"}:
            return False
        for key in ("value", "threshold", "score"):
            if not isinstance(signal[key], (int, float)) or isinstance(signal[key], bool) \
                    or not math.isfinite(signal[key]):
                return False
    return True


def _validate_decision_trace(value: Any, name: str) -> None:
    if not isinstance(value, list) or len(value) > 3:
        raise BundleError(f"invalid candidate decision trace: {name}")
    for candidate in value:
        if not isinstance(candidate, dict) or not set(candidate).issubset({
                "index", "sourceType", "eligible", "finalScore", "rejectionReasons", "rules",
                "peakValue", "meanValue", "highValueRatio", "meanColorPurity",
                "clippedWhiteRatio", "isCompactRed", "rawPCASpan",
                "robustMainIntervalLength", "continuity", "density",
                "componentArea", "pointCount"}):
            raise BundleError(f"invalid candidate decision trace: {name}")
        if not isinstance(candidate.get("index"), int) or isinstance(candidate["index"], bool) \
                or not isinstance(candidate.get("sourceType"), str) \
                or len(candidate["sourceType"]) > 100 \
                or not isinstance(candidate.get("eligible"), bool) \
                or not isinstance(candidate.get("finalScore"), (int, float)) \
                or isinstance(candidate["finalScore"], bool) \
                or not math.isfinite(candidate["finalScore"]) \
                or not isinstance(candidate.get("rejectionReasons"), list) \
                or len(candidate["rejectionReasons"]) > 20 \
                or not all(isinstance(rule, str) and len(rule) <= 100
                           for rule in candidate["rejectionReasons"]):
            raise BundleError(f"invalid candidate decision fields: {name}")
        if any(key in candidate and (not isinstance(candidate[key], (int, float))
                   or isinstance(candidate[key], bool)) for key in (
                       "peakValue", "meanValue", "highValueRatio",
                       "meanColorPurity", "clippedWhiteRatio", "rawPCASpan",
                       "robustMainIntervalLength", "continuity", "density",
                       "componentArea", "pointCount")) \
                or any(key in candidate and not math.isfinite(candidate[key])
                       for key in ("peakValue", "meanValue", "highValueRatio",
                                   "meanColorPurity", "clippedWhiteRatio", "rawPCASpan",
                                   "robustMainIntervalLength", "continuity", "density",
                                   "componentArea", "pointCount")) \
                or ("isCompactRed" in candidate
                    and not isinstance(candidate["isCompactRed"], bool)):
            raise BundleError(f"invalid candidate measurement: {name}")
        rules = candidate.get("rules")
        if not isinstance(rules, list) or len(rules) > 20:
            raise BundleError(f"invalid candidate rules: {name}")
        for rule in rules:
            if not isinstance(rule, dict) or set(rule) != {
                    "name", "result", "value", "comparison", "threshold"} \
                    or not isinstance(rule["name"], str) or len(rule["name"]) > 100 \
                    or rule["result"] != "FAIL" \
                    or rule["comparison"] not in {None, ">=", "<=", "==", ">", "<"} \
                    or any(item is not None and (not isinstance(item, (int, float))
                            or isinstance(item, bool) or not math.isfinite(item)) for item in
                           (rule["value"], rule["threshold"])):
                raise BundleError(f"invalid candidate rule: {name}")


def _safe_relative(value: Any, parent: str, suffix: str) -> str:
    if not isinstance(value, str) or "\\" in value:
        raise BundleError("image or context path is invalid")
    path = PurePosixPath(value)
    if path.is_absolute() or len(path.parts) != 2 or path.parts[0] != parent \
            or path.name in {".", ".."} or not path.name.lower().endswith(suffix.lower()):
        raise BundleError(f"unsafe selected path: {value}")
    return path.as_posix()


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("--max-images", type=int, default=DEFAULT_MAX_IMAGES)
    parser.add_argument("--codex-path")
    parser.add_argument("--timeout", type=int, default=CODEX_TIMEOUT_SECONDS)
    parser.add_argument("--dry-run", action="store_true")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    try:
        result = analyze_bundle(args.bundle, max_images=args.max_images, codex_path=args.codex_path,
                                timeout_seconds=args.timeout, dry_run=args.dry_run)
    except (BundleError, CodexUnavailable, CodexFailed, ValueError, OSError) as exc:
        print(f"[triage-codex] {exc}", file=sys.stderr, flush=True)
        return 2
    print(json.dumps(result, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
