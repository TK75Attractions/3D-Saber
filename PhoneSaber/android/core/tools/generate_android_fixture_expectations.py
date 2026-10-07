#!/usr/bin/env python3
"""Generate the checked-in Android JNI fixture oracle on an arm64 Mac.

From the Git root:
    python3 -B PhoneSaber/android/core/tools/generate_android_fixture_expectations.py
"""
from __future__ import annotations

import hashlib
import importlib.util
import json
import platform
import struct
import subprocess
import tempfile
from pathlib import Path

CORE = Path(__file__).resolve().parents[1]
PHONE_SABER = CORE.parents[1]
OUTPUT = CORE.parent / "app/src/androidTest/assets/native_fixture_expectations.json"
COMMAND = "python3 -B PhoneSaber/android/core/tools/generate_android_fixture_expectations.py"


def run(command: list[str], data: bytes | None = None) -> bytes:
    result = subprocess.run(command, input=data, capture_output=True, check=False)
    if result.returncode:
        raise RuntimeError(f"command failed: {' '.join(command)}\n{result.stderr.decode(errors='replace')}")
    return result.stdout


def main() -> None:
    if platform.system() != "Darwin" or platform.machine() != "arm64":
        raise RuntimeError("Generate the oracle on an arm64 Mac (Apple libm)")
    spec = importlib.util.spec_from_file_location(
        "lossless_reference", PHONE_SABER / "ios/PhoneSaberSender/Tools/run_lossless_regression.py")
    reference = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(reference)
    # 外部regression設定より、このworktreeのfixtureを優先する。
    reference.FIXTURES_DIR = PHONE_SABER / "ios/PhoneSaberSenderTests/Fixtures"
    _, fixtures = reference.load_fixtures(reference.DEFAULT_MANIFEST)
    records = []
    with tempfile.TemporaryDirectory(prefix="phonesaber-android-oracle-") as temp:
        build = Path(temp)
        cli, frame_cli = build / "phonesaber-png", build / "phonesaber-frame"
        run(["make", "-C", str(CORE), f"BUILD={build}", str(cli)])
        run(["clang++", "-std=c++17", "-O2", "-Wall", "-Wextra", "-Werror",
             "-ffp-contract=off", "-I" + str(CORE / "include"),
             *(str(CORE / "src" / name) for name in ("detection.cpp", "pipeline.cpp", "frame_processor.cpp")),
             str(CORE / "tools/frame_cli.cpp"), "-o", str(frame_cli)])
        for path in sorted({fixture["resolved_path"] for fixture in fixtures}):
            analysis = json.loads(run([str(cli), str(path)]))
            for fixture in fixtures:
                if fixture["resolved_path"] == path:
                    row = reference.evaluate_fixture(fixture, {**analysis, "profile": {"total_ms": 0}})
                    if not row["passed"]:
                        raise RuntimeError(f"{fixture['name']}: {row['errors']}")
            width, height = struct.unpack(">II", path.read_bytes()[16:24])
            bgra = run([str(cli), "--decode-bgra", str(path)])
            rgba = bytearray(bgra)
            rgba[0::4], rgba[2::4] = bgra[2::4], bgra[0::4]
            if len(rgba) != width * height * 4 or any(alpha != 255 for alpha in rgba[3::4]):
                raise RuntimeError(f"fixture must be opaque: {path}")
            # 既存frame CLIで既定1920x1080のpayloadを生成し、座標変換をPythonに複製しない。
            fields = ["F", "1", str(width), str(height), "1920", "1080", "0", "0", "0", "0", "0"]
            colors = {}
            for color in ("red", "blue"):
                color_data = analysis["colors"][color]
                selected = color_data["selected"]
                candidate = reference.selected_candidate(color_data)
                colors[color] = {"selected": selected,
                                 "candidateType": candidate["source"] if candidate else None,
                                 "payload": None}
                fields.append("1" if selected else "0")
                fields.extend(str(point[axis]) for point in (selected or [{"x": 0, "y": 0}] * 2)
                              for axis in ("x", "y"))
            frame = json.loads(run([str(frame_cli)], (" ".join(fields) + "\n").encode()))
            for result in frame["results"]:
                colors[result["color"]]["payload"] = result["text"]
            records.append({"path": str(path.relative_to(reference.FIXTURES_DIR)),
                            "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                            "width": width, "height": height,
                            "rgbaSha256": hashlib.sha256(rgba).hexdigest(), "colors": colors})
    document = {"schemaVersion": 1, "generatorCommand": COMMAND,
                "outputWidth": 1920, "outputHeight": 1080,
                "formalFixtureCount": len(fixtures), "images": records}
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    OUTPUT.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {len(records)} PNG expectations; formal {len(fixtures)}/{len(fixtures)} passed: {OUTPUT}")


if __name__ == "__main__":
    main()
