#!/usr/bin/env python3
"""Verify lossless PhoneSaber fixtures against the checked-in detector."""

from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any


TOOLS_DIR = Path(__file__).resolve().parent
REPO = Path(os.environ.get("PHONESABER_REGRESSION_REPO", TOOLS_DIR.parents[2])).resolve()
FIXTURES_DIR = REPO / "ios/PhoneSaberSenderTests/Fixtures"
DEFAULT_MANIFEST = TOOLS_DIR / "lossless_regression_manifest.json"
COLORS = {"RED": "red", "BLUE": "blue"}
REQUIRED_FIXTURE_FIELDS = {
    "name", "path", "color", "truth", "failureClass", "scenario",
    "sourceSession", "frameID", "expectedDetected", "expectedCandidateType",
    "expectedEndpoint", "endpointTolerancePx", "expectedRejectedCandidateTypes",
    "regressionProtected", "sha256",
}


def command_output(command: list[str], *, input_data: bytes | None = None) -> bytes:
    result = subprocess.run(command, input=input_data, capture_output=True, check=False)
    if result.returncode:
        raise RuntimeError(
            f"command failed ({result.returncode}): {' '.join(command)}\n"
            f"{result.stderr.decode(errors='replace')}"
        )
    return result.stdout


def compile_harness(destination: Path) -> None:
    sources = [
        REPO / "ios/PhoneSaberSender/PhoneSaberSender/DetectionCore.swift",
        REPO / "ios/PhoneSaberSender/PhoneSaberSender/BGRADetection.swift",
        REPO / "ios/PhoneSaberSenderTests/VideoDetectionDiagnostic.swift",
    ]
    command_output(["xcrun", "swiftc", "-O", *(str(path) for path in sources), "-o", str(destination)])


def dimensions(path: Path) -> tuple[int, int]:
    output = command_output([
        "ffprobe", "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=width,height", "-of", "csv=p=0", str(path),
    ]).decode().strip()
    width, height = output.split(",")
    return int(width), int(height)


def analyze_png(harness: Path, path: Path) -> dict[str, Any]:
    width, height = dimensions(path)
    raw = command_output([
        "ffmpeg", "-loglevel", "error", "-i", str(path),
        "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "-",
    ])
    line = command_output([str(harness), str(width), str(height), "2"], input_data=raw).splitlines()[0]
    return json.loads(line)


def _validate_endpoint(value: Any, label: str) -> None:
    if not isinstance(value, list) or len(value) != 2:
        raise ValueError(f"{label}: expected two endpoint objects")
    for point in value:
        if not isinstance(point, dict) or not isinstance(point.get("x"), int) or not isinstance(point.get("y"), int):
            raise ValueError(f"{label}: each endpoint must contain integer x and y")


def load_fixtures(manifest: Path) -> tuple[dict[str, Any], list[dict[str, Any]]]:
    document = json.loads(manifest.read_text(encoding="utf-8"))
    if document.get("schemaVersion") != 1:
        raise ValueError("manifest schemaVersion must be 1")
    classes = document.get("failureClasses")
    if not isinstance(classes, dict) or set(classes) != set("ABCDEFG"):
        raise ValueError("manifest must define failure classes A through G")

    fixtures = document.get("fixtures")
    if not isinstance(fixtures, list) or not fixtures:
        raise ValueError("manifest fixtures must be a non-empty array")
    names: set[str] = set()
    resolved_fixtures: list[dict[str, Any]] = []
    fixtures_root = FIXTURES_DIR.resolve()

    for fixture in fixtures:
        missing = REQUIRED_FIXTURE_FIELDS - fixture.keys()
        if missing:
            raise ValueError(f"fixture missing required fields: {', '.join(sorted(missing))}")
        if fixture["name"] in names:
            raise ValueError(f"duplicate fixture name: {fixture['name']}")
        names.add(fixture["name"])
        if fixture["color"] not in COLORS:
            raise ValueError(f"{fixture['name']}: color must be RED or BLUE")
        if fixture["truth"] not in ("positive", "negative"):
            raise ValueError(f"{fixture['name']}: truth must be positive or negative")
        if fixture["failureClass"] not in classes:
            raise ValueError(f"{fixture['name']}: unknown failureClass {fixture['failureClass']}")
        if not isinstance(fixture["expectedDetected"], bool):
            raise ValueError(f"{fixture['name']}: expectedDetected must be a boolean")
        if fixture["expectedDetected"] != (fixture["truth"] == "positive"):
            raise ValueError(f"{fixture['name']}: truth and expectedDetected disagree")
        if not isinstance(fixture["expectedRejectedCandidateTypes"], list) or any(
            not isinstance(value, str) or not value
            for value in fixture["expectedRejectedCandidateTypes"]
        ):
            raise ValueError(f"{fixture['name']}: expectedRejectedCandidateTypes must be strings")
        if not isinstance(fixture["regressionProtected"], str) or not fixture["regressionProtected"].strip():
            raise ValueError(f"{fixture['name']}: regressionProtected must explain the protected behavior")

        if fixture["expectedDetected"]:
            if not isinstance(fixture["expectedCandidateType"], str) or not fixture["expectedCandidateType"]:
                raise ValueError(f"{fixture['name']}: a positive fixture needs expectedCandidateType")
            _validate_endpoint(fixture["expectedEndpoint"], fixture["name"])
            tolerance = fixture["endpointTolerancePx"]
            if not isinstance(tolerance, (int, float)) or not math.isfinite(tolerance) or tolerance < 0:
                raise ValueError(f"{fixture['name']}: positive endpointTolerancePx must be non-negative")
        elif (fixture["expectedCandidateType"] is not None
              or fixture["expectedEndpoint"] is not None
              or fixture["endpointTolerancePx"] is not None):
            raise ValueError(f"{fixture['name']}: a negative fixture must expect no candidate or endpoint")

        relative = Path(fixture["path"])
        if relative.is_absolute() or ".." in relative.parts or relative.suffix.lower() != ".png":
            raise ValueError(f"{fixture['name']}: path must be a relative PNG path within Fixtures")
        path = (fixtures_root / relative).resolve()
        if not path.is_relative_to(fixtures_root):
            raise ValueError(f"{fixture['name']}: path escapes the Fixtures directory")
        if not path.is_file():
            raise FileNotFoundError(path)
        digest = hashlib.sha256(path.read_bytes()).hexdigest()
        if digest != fixture["sha256"]:
            raise ValueError(f"{fixture['name']}: SHA-256 mismatch for {fixture['path']}")
        if fixture["frameID"] is not None and not isinstance(fixture["frameID"], int):
            raise ValueError(f"{fixture['name']}: frameID must be an integer or null")
        if not isinstance(fixture["sourceSession"], str) or not fixture["sourceSession"]:
            raise ValueError(f"{fixture['name']}: sourceSession must be a non-empty string")
        resolved_fixtures.append({**fixture, "resolved_path": path})
    return document, resolved_fixtures


def selected_candidate(color_data: dict[str, Any]) -> dict[str, Any] | None:
    return next((candidate for candidate in color_data["candidates"] if candidate["eligible"]), None)


def endpoint_distance(actual: Any, expected: Any) -> float:
    """Return the smaller mean endpoint error, allowing endpoint order reversal."""
    _validate_endpoint(actual, "actual endpoint")
    _validate_endpoint(expected, "expected endpoint")

    def point_distance(left: dict[str, int], right: dict[str, int]) -> float:
        return math.hypot(left["x"] - right["x"], left["y"] - right["y"])

    direct = (point_distance(actual[0], expected[0]) + point_distance(actual[1], expected[1])) / 2
    reverse = (point_distance(actual[0], expected[1]) + point_distance(actual[1], expected[0])) / 2
    return min(direct, reverse)


def endpoint_text(endpoints: Any) -> str:
    if not isinstance(endpoints, list):
        return "-"
    return f"({endpoints[0]['x']},{endpoints[0]['y']})->({endpoints[1]['x']},{endpoints[1]['y']})"


def evaluate_fixture(fixture: dict[str, Any], analysis: dict[str, Any]) -> dict[str, Any]:
    color = COLORS[fixture["color"]]
    color_data = analysis["colors"][color]
    endpoints = color_data["selected"]
    detected = endpoints is not None
    selected = selected_candidate(color_data)
    candidate_type = selected["source"] if selected else None
    errors: list[str] = []

    if detected != fixture["expectedDetected"]:
        errors.append(f"detected expected {fixture['expectedDetected']} got {detected}")
    if candidate_type != fixture["expectedCandidateType"]:
        errors.append(f"candidate type expected {fixture['expectedCandidateType']!r} got {candidate_type!r}")
    if detected and fixture["expectedEndpoint"] is not None:
        error_px = endpoint_distance(endpoints, fixture["expectedEndpoint"])
        if error_px > fixture["endpointTolerancePx"]:
            errors.append(
                f"endpoint error {error_px:.2f}px exceeds {fixture['endpointTolerancePx']}px "
                f"(expected {endpoint_text(fixture['expectedEndpoint'])}, got {endpoint_text(endpoints)})"
            )
    else:
        error_px = None

    rejected = {
        candidate["source"] for candidate in color_data["candidates"]
        if not candidate["eligible"]
    }
    for expected_source in fixture["expectedRejectedCandidateTypes"]:
        if expected_source not in rejected:
            errors.append(f"rejected candidate {expected_source!r} was not present")

    return {
        "name": fixture["name"],
        "path": fixture["path"],
        "sourceSession": fixture.get("sourceSession"),
        "frameID": fixture.get("frameID"),
        "color": fixture["color"],
        "truth": fixture["truth"],
        "failureClass": fixture["failureClass"],
        "scenario": fixture["scenario"],
        "expectedDetected": fixture["expectedDetected"],
        "detected": detected,
        "expectedCandidateType": fixture["expectedCandidateType"],
        "candidateType": candidate_type,
        "candidateCount": len(color_data["candidates"]),
        "candidateScores": [candidate.get("score") for candidate in color_data["candidates"]],
        "selectedScore": selected.get("score") if selected else None,
        "expectedEndpoint": fixture["expectedEndpoint"],
        "endpoint": endpoints,
        "endpointErrorPx": error_px,
        "endpointTolerancePx": fixture["endpointTolerancePx"],
        "expectedRejectedCandidateTypes": fixture["expectedRejectedCandidateTypes"],
        "processingMs": analysis["profile"]["total_ms"],
        "passed": not errors,
        "errors": errors,
    }


def summarize(rows: list[dict[str, Any]]) -> dict[str, Any]:
    by_class: dict[str, dict[str, int]] = {}
    for class_id in "ABCDEFG":
        class_rows = [row for row in rows if row["failureClass"] == class_id]
        by_class[class_id] = {
            "fixture_count": len(class_rows),
            "positive_count": sum(row["truth"] == "positive" for row in class_rows),
            "negative_count": sum(row["truth"] == "negative" for row in class_rows),
            "passed": sum(row["passed"] for row in class_rows),
            "failed": sum(not row["passed"] for row in class_rows),
        }
    return {
        "fixture_count": len(rows),
        "unique_png_count": len({row["path"] for row in rows}),
        "passed": sum(row["passed"] for row in rows),
        "failed": sum(not row["passed"] for row in rows),
        "by_failure_class": by_class,
        "positive_detected": sum(row["truth"] == "positive" and row["detected"] for row in rows),
        "positive_count": sum(row["truth"] == "positive" for row in rows),
        "negative_false_positives": sum(row["truth"] == "negative" and row["detected"] for row in rows),
        "negative_count": sum(row["truth"] == "negative" for row in rows),
    }


def print_summary(rows: list[dict[str, Any]], result: dict[str, Any], classes: dict[str, Any]) -> None:
    for row in rows:
        status = "PASS" if row["passed"] else "FAIL"
        endpoint = endpoint_text(row["endpoint"])
        print(
            f"{status} {row['name']} [{row['failureClass']}/{row['color']} {row['truth']}] "
            f"detected={str(row['detected']).lower()} type={row['candidateType'] or '-'} "
            f"endpoint={endpoint}"
        )
        for error in row["errors"]:
            print(f"  {error}")

    print("SUMMARY")
    print(f"fixtures {result['fixture_count']} ({result['unique_png_count']} unique PNGs)")
    for class_id in "ABCDEFG":
        counts = result["by_failure_class"][class_id]
        print(
            f"{class_id} {classes[class_id]['name']}: {counts['fixture_count']} fixtures "
            f"({counts['positive_count']} positive, {counts['negative_count']} negative; "
            f"{counts['passed']} passed, {counts['failed']} failed)"
        )
    print(
        f"positive detections {result['positive_detected']}/{result['positive_count']}; "
        f"negative false positives {result['negative_false_positives']}/{result['negative_count']}"
    )
    print(f"result {result['passed']} passed, {result['failed']} failed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--json", type=Path, help="save full per-fixture results as JSON")
    parser.add_argument("--csv", type=Path, help="save per-fixture results as CSV")
    arguments = parser.parse_args()
    for dependency in ("xcrun", "ffmpeg", "ffprobe"):
        if not shutil.which(dependency):
            print(f"error: required command not found: {dependency}", file=sys.stderr)
            return 2
    try:
        document, fixtures = load_fixtures(arguments.manifest)
        analyses: dict[Path, dict[str, Any]] = {}
        with tempfile.TemporaryDirectory(prefix="phonesaber-lossless-") as temporary:
            harness = Path(temporary) / "video-detection-diagnostic"
            compile_harness(harness)
            rows = []
            for fixture in fixtures:
                path = fixture["resolved_path"]
                if path not in analyses:
                    analyses[path] = analyze_png(harness, path)
                rows.append(evaluate_fixture(fixture, analyses[path]))
        result = summarize(rows)
        print_summary(rows, result, document["failureClasses"])
        if arguments.json:
            arguments.json.write_text(
                json.dumps({"fixtures": rows, "summary": result}, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
        if arguments.csv:
            with arguments.csv.open("w", newline="", encoding="utf-8") as handle:
                writer = csv.DictWriter(handle, fieldnames=list(rows[0]))
                writer.writeheader()
                for row in rows:
                    writer.writerow({
                        key: json.dumps(value, ensure_ascii=False) if isinstance(value, (list, dict))
                        else value for key, value in row.items()
                    })
    except (OSError, ValueError, RuntimeError, json.JSONDecodeError) as error:
        print(f"error: {error}", file=sys.stderr)
        return 2
    return 1 if result["failed"] else 0


if __name__ == "__main__":
    raise SystemExit(main())
