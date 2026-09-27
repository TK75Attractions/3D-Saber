#!/usr/bin/env python3
"""Analyze only selected PhoneSaber lossless PNGs with the local Codex CLI."""

from __future__ import annotations

import argparse
import copy
import json
import os
import shutil
import subprocess
import sys
import tempfile
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
MAX_CODEX_SUMMARY_BYTES = 128 * 1024
MAX_CODEX_CONTEXT_BYTES = 16 * 1024
MAX_CODEX_METADATA_BYTES = 512 * 1024
CODEX_TIMEOUT_SECONDS = 600

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
    },
    "required": ["session_summary", "false_negatives", "wrong_candidate_and_endpoint_errors",
                 "false_positive_suspects", "other_findings", "limitations"],
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


def input_plan(bundle_dir: Path, max_images: int = DEFAULT_MAX_IMAGES) -> CodexInputPlan:
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
                            "summaryScope", "retainedIncidentContextFrames"}
    if not set(summary).issubset(allowed_summary_keys):
        raise BundleError("summary.json contains non-triage or full-session metadata")
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
        selected_images.append(CodexInputImage(
            image_id=f"image_{len(selected_images) + 1:03d}",
            image_path=image_path,
            context_path=context_path,
            frame_id=frame_id,
            failure_type=failure_type,
        ))
    if any(count > PER_FAILURE_TYPE for count in type_counts.values()):
        raise BundleError("per-failure-type image limit exceeds two")

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

        try:
            completed = subprocess.run(
                command,
                cwd=input_root,
                input=prompt,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                text=True,
                timeout=timeout_seconds,
                check=False,
            )
        except subprocess.TimeoutExpired as exc:
            raise CodexFailed(f"Codex CLI timed out after {timeout_seconds} seconds") from exc
        except OSError as exc:
            raise CodexFailed(f"Codex CLI could not start: {exc}") from exc
        if completed.returncode != 0:
            diagnostic = (completed.stderr or completed.stdout).strip().splitlines()
            detail = diagnostic[-1][:300] if diagnostic else f"exit {completed.returncode}"
            raise CodexFailed(f"Codex CLI failed ({completed.returncode}): {detail}")
        if not response_path.is_file() or response_path.stat().st_size > MAX_REPORT_BYTES:
            raise CodexFailed("Codex CLI returned no bounded final report")
        try:
            analysis = json.loads(response_path.read_text(encoding="utf-8"))
            _validate_analysis(analysis, set(plan.image_ids))
        except (OSError, UnicodeError, json.JSONDecodeError, BundleError, TypeError) as exc:
            raise CodexFailed(f"Codex CLI returned invalid structured output: {exc}") from exc

    report = {
        "formatVersion": 2,
        "sessionID": plan.session_id,
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
        "formatVersion": 2,
        "sessionID": plan.session_id,
        "input": {"imageCount": 0, "imagePaths": [], "contextPaths": [],
                  "imageReferences": [], "videoIncluded": False, "fullMetadataIncluded": False},
        "analysis": {
            "session_summary": "No lossless PNG met the triage selection rules. No Codex request was made.",
            "false_negatives": [], "wrong_candidate_and_endpoint_errors": [],
            "false_positive_suspects": [], "other_findings": [],
            "limitations": ["There were no selected lossless images to inspect."],
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
                "false_positive_suspects", "other_findings", "limitations"}
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
    lines.append("")
    return "\n".join(lines)


def _output_schema(image_ids: tuple[str, ...]) -> dict[str, Any]:
    schema = copy.deepcopy(OUTPUT_SCHEMA)
    for section in ("false_negatives", "wrong_candidate_and_endpoint_errors",
                    "false_positive_suspects", "other_findings"):
        schema["properties"][section]["items"]["properties"]["image_ids"]["items"] = {
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
- Do not edit, create, or propose applying production code. Return a concise JSON object matching the supplied schema exactly.

Selected image reference map:
{image_map}
"""


def _validate_context(path: Path, session_id: Any, selected_frame_id: Any) -> None:
    try:
        context = json.loads(path.read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise BundleError(f"frame context is malformed: {path.name}") from exc
    allowed_top = {"sessionID", "selectedFrameID", "selectedColor", "selectedFailureType",
                   "selectedReasons", "contextRadiusFrames", "frames"}
    required_top = allowed_top
    if not isinstance(context, dict) or set(context) != required_top \
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
    allowed_frame = {"frameID", "timestamp", "red", "blue"}
    allowed_color = {"detected", "predictionUsed", "detectionSucceeded", "maskPixelCount",
                     "morphologyPixelCount", "connectedComponentCount", "candidateCount",
                     "eligibleCandidateCount", "selectedCandidateType", "score", "endpoint",
                     "rawPCASpan", "robustMainIntervalLength", "continuity", "density",
                     "colorPurity", "coreSupport", "highBrightnessCoverage"}
    for frame in context["frames"]:
        if not isinstance(frame, dict) or set(frame) != allowed_frame \
                or not isinstance(frame.get("frameID"), int) \
                or isinstance(frame.get("frameID"), bool) \
                or not isinstance(frame.get("timestamp"), (int, float)):
            raise BundleError(f"frame context has an invalid frame entry: {path.name}")
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
                if key not in {"endpoint", "selectedCandidateType"} \
                        and not isinstance(value, (int, float, bool)):
                    raise BundleError(f"frame context has a non-numeric metric: {path.name}")
                if key == "selectedCandidateType" and (not isinstance(value, str) or len(value) > 100):
                    raise BundleError(f"frame context has an invalid candidate type: {path.name}")


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
