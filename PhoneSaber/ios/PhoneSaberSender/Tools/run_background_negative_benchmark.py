#!/usr/bin/env python3
"""Informational background hard-negative benchmark for the PhoneSaber detector.

Runs the unmodified production detector (the same Swift harness as
run_lossless_regression.py) on private diagnostics-inbox PNGs that show matte
red background objects and no lit saber. Every detection of an expected color
is a false positive. The images are never copied into the repository; the
manifest references them by inbox-relative path and pins their SHA-256.

This benchmark is NOT part of the 40/40 lossless gate. It exits 0 unless
--strict is given.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import tempfile
from pathlib import Path
from typing import Any

TOOLS_DIR = Path(__file__).resolve().parent
if str(TOOLS_DIR) not in sys.path:
    sys.path.insert(0, str(TOOLS_DIR))

import run_lossless_regression as lossless  # noqa: E402

DEFAULT_MANIFEST = TOOLS_DIR / "background_negative_benchmark.json"
DEFAULT_INBOX = Path("~/Library/Application Support/PhoneSaber/diagnostics-inbox")
INBOX_ENV = "PHONESABER_DIAGNOSTICS_INBOX"
STATUSES = ("known_detected", "rejected")
REQUIRED_IMAGE_FIELDS = {
    "id", "path", "sha256", "sourceSession", "frameID",
    "expectedNotDetected", "content", "baselineStatus",
}
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$")
ID_PATTERN = re.compile(r"^[A-Za-z0-9_.-]+$")
FEATURE_KEYS = (
    "peak_value", "mean_value", "high_value_ratio", "color_purity",
    "local_contrast", "core_support", "longitudinal_core_coverage",
    "axial_density", "raw_pca_span", "robust_body_length", "point_count",
)


def load_manifest(manifest: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    """Validate the manifest schema. Image files are not touched here."""
    document = json.loads(manifest.read_text(encoding="utf-8"))
    if document.get("schemaVersion") != 1:
        raise ValueError("manifest schemaVersion must be 1")
    images = document.get("images")
    if not isinstance(images, list) or not images:
        raise ValueError("manifest images must be a non-empty array")
    ids: set[str] = set()
    paths: set[str] = set()
    for image in images:
        if not isinstance(image, dict):
            raise ValueError("each image entry must be an object")
        missing = REQUIRED_IMAGE_FIELDS - image.keys()
        if missing:
            raise ValueError(f"image missing required fields: {', '.join(sorted(missing))}")
        image_id = image["id"]
        if not isinstance(image_id, str) or not ID_PATTERN.match(image_id):
            raise ValueError(f"invalid image id: {image_id!r}")
        if image_id in ids:
            raise ValueError(f"duplicate image id: {image_id}")
        ids.add(image_id)

        raw_path = image["path"]
        if not isinstance(raw_path, str) or not raw_path:
            raise ValueError(f"{image_id}: path must be a non-empty string")
        relative = Path(raw_path)
        if (relative.is_absolute() or raw_path.startswith("~") or ".." in relative.parts
                or relative.suffix.lower() != ".png"):
            raise ValueError(f"{image_id}: path must be a relative PNG path inside the inbox")
        if raw_path in paths:
            raise ValueError(f"{image_id}: duplicate path {raw_path}")
        paths.add(raw_path)

        if not isinstance(image["sha256"], str) or not SHA256_PATTERN.match(image["sha256"]):
            raise ValueError(f"{image_id}: sha256 must be 64 lowercase hex characters")
        if not isinstance(image["sourceSession"], str) or not image["sourceSession"]:
            raise ValueError(f"{image_id}: sourceSession must be a non-empty string")
        if image["frameID"] is not None and not isinstance(image["frameID"], int):
            raise ValueError(f"{image_id}: frameID must be an integer or null")
        if not isinstance(image["content"], str) or not image["content"]:
            raise ValueError(f"{image_id}: content must be a non-empty string")

        colors = image["expectedNotDetected"]
        if (not isinstance(colors, list) or not colors or len(set(colors)) != len(colors)
                or any(color not in lossless.COLORS for color in colors)):
            raise ValueError(f"{image_id}: expectedNotDetected must be unique RED/BLUE values")
        baseline = image["baselineStatus"]
        if not isinstance(baseline, dict) or set(baseline) != set(colors):
            raise ValueError(f"{image_id}: baselineStatus must have one entry per expected color")
        if any(value not in STATUSES for value in baseline.values()):
            raise ValueError(f"{image_id}: baselineStatus values must be one of {STATUSES}")
    return document, images


def resolve_inbox(override: Path | None) -> Path:
    if override is not None:
        return override.expanduser()
    if os.environ.get(INBOX_ENV):
        return Path(os.environ[INBOX_ENV]).expanduser()
    return DEFAULT_INBOX.expanduser()


def locate(image: dict[str, Any], inbox: Path) -> tuple[str, Path | None, str | None]:
    """Return (state, path, error) with state available / missing / error."""
    root = inbox.resolve()
    path = (root / image["path"]).resolve()
    if not path.is_relative_to(root):
        return "error", None, "path escapes the inbox root"
    if not path.is_file():
        return "missing", None, None
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if digest != image["sha256"]:
        return "error", None, f"SHA-256 mismatch (expected {image['sha256']}, got {digest})"
    return "available", path, None


def _centroid(box: dict[str, int] | None) -> dict[str, float] | None:
    if not box:
        return None
    return {"x": (box["min_x"] + box["max_x"]) / 2, "y": (box["min_y"] + box["max_y"]) / 2}


def evaluate_color(color_data: dict[str, Any]) -> dict[str, Any]:
    candidates = color_data["candidates"]
    winner = lossless.selected_candidate(color_data)
    eligible = [candidate for candidate in candidates if candidate["eligible"]]
    result: dict[str, Any] = {
        "detected": color_data["selected"] is not None,
        "selectedEndpoints": color_data["selected"],
        "candidateCount": len(candidates),
        "eligibleCount": len(eligible),
        "eligibleScores": [candidate.get("score") for candidate in eligible],
        "winner": None,
    }
    if winner is not None:
        box = winner.get("bounding_box")
        result["winner"] = {
            "source": winner.get("source"),
            "score": winner.get("score"),
            "boundingBox": box,
            "centroid": _centroid(box),
            "endpoints": winner.get("endpoints"),
            "features": {key: winner[key] for key in FEATURE_KEYS if key in winner},
        }
    return result


def evaluate_image(image: dict[str, Any], analysis: dict[str, Any]) -> dict[str, Any]:
    colors = {
        name: evaluate_color(analysis["colors"][lossless.COLORS[name]])
        for name in lossless.COLORS
    }
    false_positive_colors = [name for name in image["expectedNotDetected"] if colors[name]["detected"]]
    measured = {
        name: "known_detected" if colors[name]["detected"] else "rejected"
        for name in image["expectedNotDetected"]
    }
    return {
        "falsePositive": bool(false_positive_colors),
        "falsePositiveColors": false_positive_colors,
        "measuredStatus": measured,
        "baselineChanged": measured != image["baselineStatus"],
        "colors": colors,
    }


def summarize(rows: list[dict[str, Any]]) -> dict[str, Any]:
    available = [row for row in rows if row["state"] == "available"]
    return {
        "image_count": len(rows),
        "available": len(available),
        "missing": sum(row["state"] == "missing" for row in rows),
        "errors": sum(row["state"] == "error" for row in rows),
        "false_positives": sum(row["result"]["falsePositive"] for row in available),
        "baseline_changed": [row["id"] for row in available if row["result"]["baselineChanged"]],
    }


def run(images: list[dict[str, Any]], inbox: Path, harness_dir: Path | None = None) -> list[dict[str, Any]]:
    rows: list[dict[str, Any]] = []
    for image in images:
        state, path, error = locate(image, inbox)
        rows.append({
            "id": image["id"],
            "path": image["path"],
            "expectedNotDetected": image["expectedNotDetected"],
            "baselineStatus": image["baselineStatus"],
            "state": state,
            "error": error,
            "resolved_path": path,
            "result": None,
        })
    pending = [row for row in rows if row["state"] == "available"]
    if not pending:
        return rows
    for dependency in ("xcrun", "ffmpeg", "ffprobe"):
        if not shutil.which(dependency):
            raise RuntimeError(f"required command not found: {dependency}")
    with tempfile.TemporaryDirectory(prefix="phonesaber-bgneg-", dir=harness_dir) as temporary:
        harness = Path(temporary) / "video-detection-diagnostic"
        lossless.compile_harness(harness)
        by_id = {image["id"]: image for image in images}
        for row in pending:
            analysis = lossless.analyze_png(harness, row["resolved_path"])
            row["result"] = evaluate_image(by_id[row["id"]], analysis)
    return rows


def _fmt(value: Any, digits: int = 2) -> str:
    if value is None:
        return "-"
    if isinstance(value, float):
        return f"{value:.{digits}f}"
    return str(value)


def print_report(rows: list[dict[str, Any]], summary: dict[str, Any], inbox: Path) -> None:
    print(f"inbox {inbox}")
    for row in rows:
        if row["state"] != "available":
            label = "SKIP" if row["state"] == "missing" else "ERROR"
            detail = "image not present" if row["state"] == "missing" else row["error"]
            print(f"{label} {row['id']}: {detail}")
            continue
        result = row["result"]
        label = "FP" if result["falsePositive"] else "OK"
        changed = " (baseline changed)" if result["baselineChanged"] else ""
        print(f"{label} {row['id']} expect-not={'+'.join(row['expectedNotDetected'])}{changed}")
        for name, color in result["colors"].items():
            winner = color["winner"]
            line = (
                f"  {name}: detected={str(color['detected']).lower()} "
                f"eligible={color['eligibleCount']}/{color['candidateCount']}"
            )
            if winner:
                box = winner["boundingBox"] or {}
                centroid = winner["centroid"] or {}
                features = winner["features"]
                line += (
                    f" winner={winner['source']} score={_fmt(winner['score'])} "
                    f"bbox=({box.get('min_x')},{box.get('min_y')})-({box.get('max_x')},{box.get('max_y')}) "
                    f"centroid=({_fmt(centroid.get('x'), 1)},{_fmt(centroid.get('y'), 1)}) "
                    f"peak={_fmt(features.get('peak_value'), 0)} mean={_fmt(features.get('mean_value'), 1)} "
                    f"high={_fmt(features.get('high_value_ratio'))} purity={_fmt(features.get('color_purity'))} "
                    f"core={_fmt(features.get('core_support'))} density={_fmt(features.get('axial_density'))} "
                    f"points={_fmt(features.get('point_count'))}"
                )
            print(line)
    print("SUMMARY")
    print(
        f"false positives {summary['false_positives']} / available {summary['available']} "
        f"(missing {summary['missing']}, errors {summary['errors']})"
    )
    if summary["baseline_changed"]:
        print(f"baseline changed: {', '.join(summary['baseline_changed'])}")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument(
        "--inbox", type=Path,
        help=f"diagnostics inbox root (default: ${INBOX_ENV} or {DEFAULT_INBOX})",
    )
    parser.add_argument("--json", type=Path, help="write full results as JSON to this path ('-' for stdout)")
    parser.add_argument("--strict", action="store_true",
                        help="exit 1 when any false positive or SHA-256 error is found")
    arguments = parser.parse_args(argv)
    inbox = resolve_inbox(arguments.inbox)
    try:
        _, images = load_manifest(arguments.manifest)
        rows = run(images, inbox)
    except (OSError, ValueError, RuntimeError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    summary = summarize(rows)
    serializable = [{key: value for key, value in row.items() if key != "resolved_path"} for row in rows]
    if arguments.json is not None and str(arguments.json) == "-":
        json.dump({"images": serializable, "summary": summary}, sys.stdout, ensure_ascii=False, indent=2)
        sys.stdout.write("\n")
    else:
        print_report(rows, summary, inbox)
        if arguments.json is not None:
            arguments.json.write_text(
                json.dumps({"images": serializable, "summary": summary}, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
    if arguments.strict and (summary["false_positives"] or summary["errors"]):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
