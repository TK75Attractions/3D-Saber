#!/usr/bin/env python3
"""Compare the portable core with the unchanged iPhone production Swift harness.

Only nondeterministic timing/diagnostic pipeline counters are excluded. All
selected output and every candidate field exposed by VideoDetectionDiagnostic
are compared exactly (IEEE-754 bits for numbers, no tolerance). Formal fixture
expectations are additionally checked by the existing lossless evaluator.
"""
from __future__ import annotations

import argparse
import importlib.util
import json
import os
import struct
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Any
import frame_reference

CORE = Path(__file__).resolve().parents[1]
REPO = CORE.parents[1]
INBOX = Path.home() / "Library/Application Support/PhoneSaber/diagnostics-inbox"


def reference_module():
    path = REPO / "ios/PhoneSaberSender/Tools/run_lossless_regression.py"
    spec = importlib.util.spec_from_file_location("lossless_reference", path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def run(command: list[str], data: bytes | None = None, env=None) -> bytes:
    result = subprocess.run(command, input=data, capture_output=True, env=env)
    if result.returncode:
        raise RuntimeError(f"command failed: {' '.join(command)}\n{result.stderr.decode(errors='replace')}")
    return result.stdout


def differences(expected: Any, actual: Any, path: str = "colors") -> list[str]:
    """Recursively compare without rounding or tolerances, including signed zero."""
    if isinstance(expected, bool) or isinstance(actual, bool):
        return [] if type(expected) is type(actual) and expected == actual else [f"{path}: {expected!r} != {actual!r}"]
    if isinstance(expected, (float, int)) and isinstance(actual, (float, int)):
        equal = struct.pack(">d", float(expected)) == struct.pack(">d", float(actual))
        return [] if equal else [f"{path}: {expected!r} != {actual!r}"]
    if isinstance(expected, dict) and isinstance(actual, dict):
        if expected.keys() != actual.keys():
            return [f"{path}: keys differ ({sorted(expected)} != {sorted(actual)})"]
        return [error for key in sorted(expected) for error in differences(expected[key], actual[key], f"{path}.{key}")]
    if isinstance(expected, list) and isinstance(actual, list):
        if len(expected) != len(actual):
            return [f"{path}: lengths {len(expected)} != {len(actual)}"]
        return [error for i, (left, right) in enumerate(zip(expected, actual)) for error in differences(left, right, f"{path}[{i}]")]
    return [] if type(expected) is type(actual) and expected == actual else [f"{path}: {expected!r} != {actual!r}"]


def original_pngs(inbox: Path) -> list[Path]:
    # Annotated bridge images are drawings, never recognition input. Do not
    # deduplicate original files: every received original PNG is verified.
    return sorted(path for path in inbox.glob("*/images/*.png") if "annotated" not in path.stem.lower())


def rgba_padded(bgra: bytes, width: int, height: int) -> bytes:
    stride = width * 4
    result = bytearray((stride + 13) * height)
    for y in range(height):
        row = bytearray(bgra[y * stride:(y + 1) * stride])
        row[0::4], row[2::4] = row[2::4], row[0::4]
        result[y * (stride + 13):y * (stride + 13) + stride] = row
        result[y * (stride + 13) + stride:(y + 1) * (stride + 13)] = b"\xa5" * 13
    return bytes(result)


def build(destination: Path) -> tuple[Path, Path]:
    cli, swift = destination / "phonesaber-png", destination / "swift-reference"
    run(["clang++", "-std=c++17", "-O2", "-Wall", "-Wextra", "-Werror", "-ffp-contract=off",
         "-I" + str(CORE / "include"), *(str(CORE / "src" / name) for name in ("detection.cpp", "pipeline.cpp", "frame_processor.cpp")),
         str(CORE / "tools/png_cli.cpp"), str(CORE / "tools/png.cpp"), "-lz", "-o", str(cli)])
    env = {**os.environ, "CLANG_MODULE_CACHE_PATH": str(destination / "module-cache")}
    # Add a test-only signature to the existing reference runner in /tmp.
    # JSONSerialization numeric text can hide -0 or decimal rounding; strings
    # of Double.bitPattern prove every persisted production double bit-for-bit.
    original_runner = REPO / "ios/PhoneSaberSenderTests/VideoDetectionDiagnostic.swift"
    runner = destination / "VideoDetectionDiagnostic.swift"
    signature = """        "production": [
            "comparison_endpoints": [pointJSON(candidate.comparisonEndpoints.0), pointJSON(candidate.comparisonEndpoints.1)],
            "robust_endpoints": candidate.robustMainIntervalEndpoints.map { [pointJSON($0.0), pointJSON($0.1)] as Any } ?? NSNull(),
            "component_area": candidate.componentArea,
            "compact_red": candidate.isCompactRed,
            "warm_fraction_bits": candidate.warmNoDeepRed.map { String($0.warmFrac.bitPattern) as Any } ?? NSNull(),
            "double_bits": [candidate.score, candidate.scoreBreakdown.total, candidate.radiance,
                candidate.meanValue, candidate.highValueRatio, candidate.meanColorPurity,
                candidate.clippedWhiteRatio, candidate.brightnessVariation, candidate.localContrast,
                candidate.longitudinalHighCoverage, candidate.widthVariation, candidate.coreSupportRatio,
                candidate.longitudinalCoreCoverage, candidate.longitudinalContinuity, candidate.retainedBodyRatio,
                candidate.rawPCASpan, candidate.robustMainIntervalLength, candidate.axialDensity,
                candidate.scoreBreakdown.proposalPenalty, candidate.scoreBreakdown.radiance,
                candidate.scoreBreakdown.length, candidate.scoreBreakdown.aspect, candidate.scoreBreakdown.extent,
                candidate.scoreBreakdown.widthConsistency, candidate.scoreBreakdown.area,
                candidate.scoreBreakdown.peakBrightness, candidate.scoreBreakdown.meanBrightness,
                candidate.scoreBreakdown.highBrightnessRatio, candidate.scoreBreakdown.colorPurity,
                candidate.scoreBreakdown.localContrast, candidate.scoreBreakdown.emitterTexture,
                candidate.scoreBreakdown.clippedWhite, candidate.scoreBreakdown.longitudinalHighCoverage,
                candidate.scoreBreakdown.coreSupport, candidate.scoreBreakdown.longitudinalCoreCoverage].map { String($0.bitPattern) }
        ],
"""
    runner_text = original_runner.read_text()
    marker = '        "warmNoDeepRed": candidate.warmNoDeepRed.map'
    if runner_text.count(marker) != 1:
        raise RuntimeError("Swift runner signature insertion marker changed")
    runner.write_text(runner_text.replace(marker, signature + marker, 1))
    sources = [REPO / "ios/PhoneSaberSender/PhoneSaberSender/DetectionCore.swift",
               REPO / "ios/PhoneSaberSender/PhoneSaberSender/BGRADetection.swift",
               runner]
    run(["xcrun", "swiftc", "-O", *(str(path) for path in sources), "-o", str(swift)], env=env)
    return cli, swift


def compare_image(cli: Path, swift: Path, path: Path, ref) -> tuple[dict, list[str]]:
    # Independent decoder cross-check: ffmpeg is the existing Swift runner's
    # lossless input path. The C++ CLI decodes PNG itself, with zlib only.
    width, height = ref.dimensions(path)
    bgra = run(["ffmpeg", "-loglevel", "error", "-i", str(path), "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "bgra", "-"])
    decoded = run([str(cli), "--decode-bgra", str(path)])
    if decoded != bgra:
        return {}, ["PNG decoder bytes differ from Swift runner ffmpeg input"]
    expected = json.loads(run([str(swift), str(width), str(height), "2"], bgra).splitlines()[0])
    actual = json.loads(run([str(cli), str(path)]))
    errors = differences(expected["colors"], actual["colors"])
    # Same recognition with BGRA and an odd padded RGBA row stride. Both scans
    # and the RED gate must read the specified format/stride.
    for format_name, stride, pixels in (("bgra", width * 4, bgra), ("rgba", width * 4 + 13, rgba_padded(bgra, width, height))):
        variant = json.loads(run([str(cli), "--raw", str(width), str(height), "2", format_name, str(stride)], pixels))
        errors.extend(differences(expected["colors"], variant["colors"], format_name))
    return actual, errors


def compare_synthetic(cli: Path, swift: Path) -> tuple[int, list[str]]:
    width, height = 97, 73
    errors = []
    cases = ("empty", "red", "blue", "clipped", "warm", "point-led",
             "sky-blue", "pale-one-deep", "blue-fallthrough")
    for case in cases:
        bgra = bytearray(bytes((18, 18, 18, 255)) * (width * height))
        for y in range(12, 62):
            for x in range(43, 50):
                if case == "empty" or (case == "point-led" and y % 10 >= 4):
                    continue
                r, g, b = (255, 20, 20) if case in ("red", "point-led") else (80, 100, 255)
                if case == "warm":
                    r, g, b = 255, 140, 95
                if case in ("sky-blue", "pale-one-deep", "blue-fallthrough"):
                    r, g, b = 140, 170, 250
                if case in ("clipped", "sky-blue", "pale-one-deep", "blue-fallthrough") and x in (45, 46):
                    r, g, b = 255, 255, 255
                offset = (y * width + x) * 4
                bgra[offset:offset + 4] = bytes((b, g, r, (x + y) % 256))
        if case == "pale-one-deep":
            # step=2/3 では非 sample だが dilate1 内にある唯一の濃青。
            offset = (13 * width + 46) * 4
            bgra[offset:offset + 4] = bytes((180, 116, 71, 255))
        if case == "blue-fallthrough":
            for y in range(24, 48):
                for x in range(75, 82):
                    offset = (y * width + x) * 4
                    bgra[offset:offset + 4] = bytes((250, 40, 30, 255))
        for step in (1, 2, 3):
            expected = json.loads(run([str(swift), str(width), str(height), str(step)], bytes(bgra)).splitlines()[0])
            for format_name, stride, pixels in (("bgra", width * 4, bytes(bgra)), ("rgba", width * 4 + 13, rgba_padded(bytes(bgra), width, height))):
                actual = json.loads(run([str(cli), "--raw", str(width), str(height), str(step), format_name, str(stride)], pixels))
                errors.extend(differences(expected["colors"], actual["colors"], f"synthetic.{case}.step{step}.{format_name}"))
    return len(cases) * 3, errors


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--inbox", type=Path, default=INBOX)
    parser.add_argument("--json", type=Path, help="write local report (do not commit)")
    arguments = parser.parse_args()
    ref = reference_module()
    _, fixtures = ref.load_fixtures(ref.DEFAULT_MANIFEST)
    originals = original_pngs(arguments.inbox)
    if not originals:
        raise RuntimeError(f"no original inbox PNGs: {arguments.inbox}")
    fixture_paths = sorted({f["resolved_path"] for f in fixtures})
    images = sorted(set(fixture_paths + originals))
    errors_by_path = {}
    fixture_errors = []
    with tempfile.TemporaryDirectory(prefix="phonesaber-cpp-parity-") as temp:
        cli, swift = build(Path(temp))
        frame_count, frame_errors = frame_reference.verify(Path(temp), REPO, CORE, run, differences)
        print(f"FrameProcessor transitions={frame_count}; mismatches={len(frame_errors)}", flush=True)
        for error in frame_errors[:10]:
            print(error, flush=True)
        synthetic_count, synthetic_errors = compare_synthetic(cli, swift)
        print(f"Synthetic cases={synthetic_count}; mismatches={len(synthetic_errors)}", flush=True)
        for error in synthetic_errors[:10]:
            print(error, flush=True)
        analyses = {}
        for i, path in enumerate(images):
            analysis, errors = compare_image(cli, swift, path, ref)
            analyses[path] = analysis
            if errors:
                errors_by_path[str(path)] = errors
                print(f"FAIL {path}: {errors[0]}", flush=True)
            if (i + 1) % 10 == 0 or i + 1 == len(images):
                print(f"Compared {i + 1}/{len(images)} PNGs; mismatches={len(errors_by_path)}", flush=True)
        for fixture in fixtures:
            analysis = analyses[fixture["resolved_path"]]
            if not analysis:
                fixture_errors.append({"name": fixture["name"], "errors": ["decoder mismatch"]})
            else:
                # The reference evaluator requires a timing field; timing is
                # immaterial to its expected recognition assertions.
                row = ref.evaluate_fixture(fixture, {**analysis, "profile": {"total_ms": 0}})
                if not row["passed"]:
                    fixture_errors.append({"name": fixture["name"], "errors": row["errors"]})
    report = {"fixture_count": len(fixtures), "fixture_png_count": len(fixture_paths),
              "fixture_failures": len(fixture_errors), "frame_transition_count": frame_count,
              "frame_transition_mismatches": len(frame_errors), "frame_transition_errors": frame_errors, "inbox_original_png_count": len(originals),
              "inbox_annotated_excluded": len(list(arguments.inbox.glob("*/images/*.png"))) - len(originals),
              "compared_png_count": len(images), "mismatches": len(errors_by_path),
              "errors": errors_by_path, "fixture_errors": fixture_errors,
              "synthetic_case_count": synthetic_count, "synthetic_mismatches": len(synthetic_errors),
              "synthetic_errors": synthetic_errors}
    print(json.dumps({key: value for key, value in report.items() if key not in ("errors", "fixture_errors", "frame_transition_errors", "synthetic_errors")}, sort_keys=True), flush=True)
    if arguments.json:
        arguments.json.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n")
    return int(bool(errors_by_path or fixture_errors or frame_errors or synthetic_errors))


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, RuntimeError) as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(2)
