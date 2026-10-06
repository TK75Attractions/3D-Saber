#!/usr/bin/env python3
"""Bounded, checksummed transport format for PhoneSaber triage bundles."""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import struct
import tempfile
from pathlib import Path, PurePosixPath
from typing import BinaryIO, Any


MAGIC = b"PSBT"
FORMAT_VERSION = 1
CONTENT_TYPE = "application/vnd.phonesaber.triage-v1"
MAX_IMAGES = 20
DEFAULT_MAX_IMAGES = 12
MAX_FILES = 2 + MAX_IMAGES * 2
MAX_BUNDLE_BYTES = 512 * 1024 * 1024
MAX_MANIFEST_BYTES = 128 * 1024
MAX_SUMMARY_BYTES = 512 * 1024
MAX_PROMPT_BYTES = 64 * 1024
MAX_CONTEXT_BYTES = 256 * 1024
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
SESSION_RE = re.compile(r"^[A-Za-z0-9_-]{1,100}$")
FRAME_RE = re.compile(r"^frames/frame_[0-9]+_[0-9]+\.json$")


class BundleError(ValueError):
    """The uploaded bundle is malformed or violates resource limits."""


def _allowed_path(value: Any) -> str:
    if not isinstance(value, str) or not value or "\\" in value:
        raise BundleError("manifest contains an invalid path")
    path = PurePosixPath(value)
    if path.is_absolute() or any(part in {"", ".", ".."} for part in path.parts):
        raise BundleError("manifest path escapes the bundle")
    normalized = path.as_posix()
    if normalized in {"summary.json", "prompt.md"}:
        return normalized
    if normalized.startswith("images/") and normalized.lower().endswith(".png"):
        if len(path.parts) != 2 or path.name != normalized.split("/", 1)[1]:
            raise BundleError("image paths must be flat under images/")
        return normalized
    if FRAME_RE.fullmatch(normalized):
        return normalized
    raise BundleError(f"file is not allowed in a triage bundle: {normalized}")


def _content_type(path: str) -> str:
    if path.endswith(".png"):
        return "image/png"
    if path.endswith(".md"):
        return "text/markdown; charset=utf-8"
    return "application/json; charset=utf-8"


def _manifest_files(root: Path) -> tuple[str, list[dict[str, Any]]]:
    if not root.is_dir() or root.is_symlink():
        raise BundleError("bundle root must be a real directory")
    paths = sorted(root.rglob("*"))
    entries: list[dict[str, Any]] = []
    session_id: str | None = None
    for file_path in paths:
        if file_path.is_dir():
            continue
        if file_path.is_symlink() or not file_path.is_file():
            raise BundleError("symlinks and special files are not allowed")
        relative = file_path.relative_to(root).as_posix()
        normalized = _allowed_path(relative)
        size = file_path.stat().st_size
        if normalized == "summary.json":
            if size > MAX_SUMMARY_BYTES:
                raise BundleError("summary.json is too large")
            try:
                summary = json.loads(file_path.read_text(encoding="utf-8"))
            except (OSError, UnicodeError, json.JSONDecodeError) as exc:
                raise BundleError("summary.json is malformed") from exc
            session_id = _validate_summary(summary, set())
        elif normalized == "prompt.md" and size > MAX_PROMPT_BYTES:
            raise BundleError("prompt.md is too large")
        elif normalized.startswith("frames/") and size > MAX_CONTEXT_BYTES:
            raise BundleError("frame context is too large")
        digest = _sha256(file_path)
        entries.append({
            "path": normalized,
            "size": size,
            "sha256": digest,
            "contentType": _content_type(normalized),
        })

    if session_id is None:
        raise BundleError("summary.json is required")
    file_paths = {entry["path"] for entry in entries}
    _validate_summary_file_set(root, file_paths)
    if sum(entry["size"] for entry in entries) > MAX_BUNDLE_BYTES:
        raise BundleError("bundle exceeds the maximum size")
    return session_id, entries


def pack_bundle(bundle_dir: Path, destination: Path) -> dict[str, Any]:
    """Write a PSBT envelope without recompressing or changing selected PNGs."""
    root = bundle_dir.resolve(strict=True)
    session_id, entries = _manifest_files(root)
    manifest = json.dumps({
        "formatVersion": FORMAT_VERSION,
        "sessionID": session_id,
        "files": entries,
    }, ensure_ascii=False, separators=(",", ":"), sort_keys=True).encode("utf-8")
    if len(manifest) > MAX_MANIFEST_BYTES:
        raise BundleError("bundle manifest is too large")
    total = len(MAGIC) + 4 + len(manifest) + sum(item["size"] for item in entries)
    if total > MAX_BUNDLE_BYTES:
        raise BundleError("bundle exceeds the maximum size")

    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open("wb") as output:
        output.write(MAGIC)
        output.write(struct.pack(">I", len(manifest)))
        output.write(manifest)
        for entry in entries:
            with (root / entry["path"]).open("rb") as source:
                shutil.copyfileobj(source, output, length=1024 * 1024)
    return {"sessionID": session_id, "fileCount": len(entries), "byteCount": total}


def receive_bundle(source: BinaryIO, content_length: int, inbox: Path) -> Path:
    """Read and validate one bounded envelope into an atomic inbox directory."""
    if content_length < 8 or content_length > MAX_BUNDLE_BYTES:
        raise BundleError("request size is outside the permitted range")
    if _read_exact(source, 4) != MAGIC:
        raise BundleError("invalid bundle signature")
    manifest_size = struct.unpack(">I", _read_exact(source, 4))[0]
    if not 2 <= manifest_size <= MAX_MANIFEST_BYTES:
        raise BundleError("manifest size is outside the permitted range")
    try:
        manifest = json.loads(_read_exact(source, manifest_size).decode("utf-8"))
    except (UnicodeError, json.JSONDecodeError) as exc:
        raise BundleError("manifest JSON is malformed") from exc
    if not isinstance(manifest, dict) or manifest.get("formatVersion") != FORMAT_VERSION:
        raise BundleError("unsupported bundle format")
    session_id = manifest.get("sessionID")
    if not isinstance(session_id, str) or not SESSION_RE.fullmatch(session_id):
        raise BundleError("invalid session id")
    files = manifest.get("files")
    if not isinstance(files, list) or not 2 <= len(files) <= MAX_FILES:
        raise BundleError("file count is outside the permitted range")

    checked: list[dict[str, Any]] = []
    seen: set[str] = set()
    image_count = 0
    announced_size = 8 + manifest_size
    for entry in files:
        if not isinstance(entry, dict):
            raise BundleError("manifest file entry must be an object")
        relative = _allowed_path(entry.get("path"))
        if relative in seen:
            raise BundleError("manifest contains a duplicate path")
        seen.add(relative)
        size = entry.get("size")
        if isinstance(size, bool) or not isinstance(size, int) or size < 0:
            raise BundleError("manifest contains an invalid file size")
        digest = entry.get("sha256")
        if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-f]{64}", digest):
            raise BundleError("manifest contains an invalid SHA-256")
        if entry.get("contentType") != _content_type(relative):
            raise BundleError("manifest content type does not match its file")
        if relative == "summary.json" and size > MAX_SUMMARY_BYTES:
            raise BundleError("summary.json is too large")
        if relative == "prompt.md" and size > MAX_PROMPT_BYTES:
            raise BundleError("prompt.md is too large")
        if relative.startswith("frames/") and size > MAX_CONTEXT_BYTES:
            raise BundleError("frame context is too large")
        if relative.startswith("images/"):
            image_count += 1
            if size < 24:
                raise BundleError("PNG file is too small")
        announced_size += size
        checked.append({"path": relative, "size": size, "sha256": digest})
    if image_count > MAX_IMAGES:
        raise BundleError("selected image count exceeds the hard limit")
    if announced_size != content_length or announced_size > MAX_BUNDLE_BYTES:
        raise BundleError("content length does not match the manifest")
    expected = {"summary.json", "prompt.md"}
    if not expected.issubset(seen):
        raise BundleError("summary.json and prompt.md are required")
    if sum(path.startswith("frames/") for path in seen) != image_count:
        raise BundleError("each selected image must have one context JSON")

    inbox.mkdir(parents=True, exist_ok=True)
    if inbox.is_symlink() or not inbox.is_dir():
        raise BundleError("inbox must be a real directory")
    temporary = Path(tempfile.mkdtemp(prefix=".phonesaber-receive-", dir=inbox))
    try:
        for item in checked:
            path = temporary / item["path"]
            path.parent.mkdir(parents=True, exist_ok=True)
            hasher = hashlib.sha256()
            remaining = item["size"]
            with path.open("xb") as output:
                while remaining:
                    chunk = _read_exact(source, min(1024 * 1024, remaining))
                    output.write(chunk)
                    hasher.update(chunk)
                    remaining -= len(chunk)
            if hasher.hexdigest() != item["sha256"]:
                raise BundleError(f"SHA-256 mismatch: {item['path']}")
            if item["path"].startswith("images/"):
                _validate_png(path)

        summary_path = temporary / "summary.json"
        try:
            summary = json.loads(summary_path.read_text(encoding="utf-8"))
        except (OSError, UnicodeError, json.JSONDecodeError) as exc:
            raise BundleError("summary.json is malformed") from exc
        _validate_summary(summary, seen)
        _validate_summary_file_set(temporary, seen)
        _validate_context_files(temporary, seen)

        final = inbox / f"phone_saber_triage_{session_id}"
        if final.exists():
            raise FileExistsError(f"bundle already exists: {final.name}")
        os.replace(temporary, final)
        return final
    except Exception:
        shutil.rmtree(temporary, ignore_errors=True)
        raise


def _validate_summary(summary: Any, file_paths: set[str]) -> str:
    if not isinstance(summary, dict) or summary.get("formatVersion") != FORMAT_VERSION:
        raise BundleError("summary.json has an unsupported schema")
    session_id = summary.get("sessionID")
    if not isinstance(session_id, str) or not SESSION_RE.fullmatch(session_id):
        raise BundleError("summary.json has an invalid sessionID")
    selected_count = summary.get("selectedImageCount")
    if isinstance(selected_count, bool) or not isinstance(selected_count, int) \
            or not 0 <= selected_count <= MAX_IMAGES:
        raise BundleError("summary.json has an invalid selectedImageCount")
    images = summary.get("images")
    if not isinstance(images, list) or len(images) != selected_count:
        raise BundleError("summary image list does not match selectedImageCount")
    if not isinstance(summary.get("incidentCount"), int) or isinstance(summary.get("incidentCount"), bool) \
            or summary["incidentCount"] < 0:
        raise BundleError("summary.json has an invalid incidentCount")
    if not isinstance(summary.get("redBlueDetectionSummary"), dict) \
            or not {"red", "blue"}.issubset(summary["redBlueDetectionSummary"]):
        raise BundleError("summary.json needs RED and BLUE detection summaries")
    if not isinstance(summary.get("dropoutSummary"), dict) \
            or not {"red", "blue"}.issubset(summary["dropoutSummary"]):
        raise BundleError("summary.json needs RED and BLUE dropout summaries")
    image_paths: set[str] = set()
    context_paths: set[str] = set()
    for image in images:
        if not isinstance(image, dict):
            raise BundleError("summary image entry must be an object")
        image_path = _allowed_path(image.get("path"))
        context_path = _allowed_path(image.get("frameContextPath"))
        if not image_path.startswith("images/") or not context_path.startswith("frames/"):
            raise BundleError("summary image references an invalid path")
        if image_path in image_paths:
            raise BundleError("summary references an image more than once")
        if context_path in context_paths:
            raise BundleError("summary references a frame context more than once")
        image_paths.add(image_path)
        context_paths.add(context_path)
        if not isinstance(image.get("frameID"), int) or isinstance(image.get("frameID"), bool):
            raise BundleError("summary image entry has no frameID")
        if image.get("color") not in {"red", "blue", "both"}:
            raise BundleError("summary image entry has an invalid color")
        if not isinstance(image.get("failureType"), str) or not isinstance(image.get("reason"), str):
            raise BundleError("summary image entry has no selection reason")
        if file_paths and (image_path not in file_paths or context_path not in file_paths):
            raise BundleError("summary references a missing image or frame context")
    if file_paths:
        actual_images = {path for path in file_paths if path.startswith("images/")}
        if actual_images != image_paths:
            raise BundleError("summary image list does not match uploaded PNG files")
    return session_id


def _validate_summary_file_set(root: Path, file_paths: set[str]) -> None:
    if "summary.json" not in file_paths or "prompt.md" not in file_paths:
        raise BundleError("summary.json and prompt.md are required")
    image_count = sum(path.startswith("images/") for path in file_paths)
    context_count = sum(path.startswith("frames/") for path in file_paths)
    if image_count != context_count or image_count > MAX_IMAGES:
        raise BundleError("image/context count is invalid")
    if image_count:
        summary = json.loads((root / "summary.json").read_text(encoding="utf-8"))
        _validate_summary(summary, file_paths)
        expected_contexts = {image["frameContextPath"] for image in summary["images"]}
        actual_contexts = {path for path in file_paths if path.startswith("frames/")}
        if expected_contexts != actual_contexts:
            raise BundleError("summary frame context list does not match uploaded JSON files")
    elif root.exists():
        summary = json.loads((root / "summary.json").read_text(encoding="utf-8"))
        if summary.get("selectedImageCount") != 0 or summary.get("images") != []:
            raise BundleError("empty bundle summary has non-empty image selection")


def _validate_png(path: Path) -> None:
    with path.open("rb") as handle:
        header = handle.read(24)
    if len(header) < 24 or header[:8] != PNG_SIGNATURE:
        raise BundleError(f"selected image is not a PNG: {path.name}")
    width, height = struct.unpack(">II", header[16:24])
    if not width or not height or width > 20_000 or height > 20_000:
        raise BundleError(f"PNG dimensions are invalid: {path.name}")


def _validate_context_files(root: Path, paths: set[str]) -> None:
    for relative in paths:
        if not relative.startswith("frames/"):
            continue
        try:
            context = json.loads((root / relative).read_text(encoding="utf-8"))
        except (OSError, UnicodeError, json.JSONDecodeError) as exc:
            raise BundleError(f"frame context is malformed: {relative}") from exc
        if not isinstance(context, dict) or not isinstance(context.get("frames"), list) \
                or not 1 <= len(context["frames"]) <= 5:
            raise BundleError(f"frame context is outside the permitted shape: {relative}")
        if not isinstance(context.get("selectedFrameID"), int) \
                or not isinstance(context.get("sessionID"), str):
            raise BundleError(f"frame context lacks its session/frame id: {relative}")
        for frame in context["frames"]:
            if not isinstance(frame, dict) or not isinstance(frame.get("frameID"), int) \
                    or not isinstance(frame.get("timestamp"), (int, float)) \
                    or not isinstance(frame.get("red"), dict) or not isinstance(frame.get("blue"), dict):
                raise BundleError(f"frame context lacks compact RED/BLUE metadata: {relative}")


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def _read_exact(source: BinaryIO, count: int) -> bytes:
    chunks = bytearray()
    while len(chunks) < count:
        part = source.read(count - len(chunks))
        if not part:
            raise BundleError("bundle ended before all declared bytes arrived")
        chunks.extend(part)
    return bytes(chunks)
