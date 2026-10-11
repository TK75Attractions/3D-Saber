#!/usr/bin/env python3
"""Long-running (soak) check of the iPhone sender pipeline on the Mac.

Builds PhoneSaber/ios/PhoneSaberSenderTests/SenderSoakHarness.swift together with
the production FrameProcessor / payload / CoordinateDelivery / UDPSender /
LANLivenessProbe sources (`xcrun swiftc -O`, optionally with -D DEBUG like an
Xcode Run build) and runs it at 60 fps against loopback UDP receivers on
automatically assigned ports (never Unity's 5005/5006/5007). The harness writes
one CSV row every --sample seconds; this script then prints and stores the
start / end / slope-per-minute of each column.

    python3 PhoneSaber/tools/sender_soak.py --out /tmp/sender-soak --seconds 1800

Camera capture, the wireless link, P2P and the SwiftUI layer are not part of
this measurement (see PhoneSaber/docs/claude/soak.md).
"""
from __future__ import annotations

import argparse
import csv
import json
import subprocess
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
SOURCE = REPO / "ios/PhoneSaberSender/PhoneSaberSender"
HARNESS = REPO / "ios/PhoneSaberSenderTests/SenderSoakHarness.swift"
FIXTURE = REPO / "ios/PhoneSaberSenderTests/Fixtures/lossless-regression/phonesaber_20260922_101530_202/frame_390.png"
SOURCES = ("DetectionCore.swift", "BGRADetection.swift", "FrameProcessor.swift", "DebugVideoRecorder.swift",
           "DebugRecordingTriage.swift", "GuidedRecording.swift", "UDPSender.swift", "P2PSender.swift",
           "P2PProtocol.swift")
WARMUP_MINUTES = 5.0


def support_source(destination: Path) -> Path:
    """CameraViewModel.swift imports UIKit; copy only the interval types FrameProcessor needs."""
    camera = (SOURCE / "CameraViewModel.swift").read_text(encoding="utf-8")
    start = camera.index("struct CameraFrameIntervalStatistics:")
    end = camera.index("enum CameraLifecycleState:")
    support = destination / "CameraIntervals.swift"
    support.write_text("import Foundation\n" + camera[start:end] +
                       "\nstruct DebugBundleTransfer {\n    static let shared = DebugBundleTransfer()\n"
                       "    func enqueue(bundleURL: URL) {} // The soak never uploads captures.\n}\n",
                       encoding="utf-8")
    return support


def build(destination: Path, debug: bool) -> Path:
    destination.mkdir(parents=True, exist_ok=True)
    binary = destination / ("sender-soak-debug" if debug else "sender-soak-release")
    command = ["xcrun", "swiftc", "-O", "-D", "SENDER_SOAK_HARNESS", "-parse-as-library",
               "-module-cache-path", str(destination / "module-cache")]
    if debug:
        command += ["-D", "DEBUG"]
    command += [str(SOURCE / name) for name in SOURCES] + [str(support_source(destination)), str(HARNESS),
                                                           "-o", str(binary)]
    subprocess.run(command, check=True, timeout=600)
    return binary


def slope(xs: list[float], ys: list[float]) -> float:
    n = len(xs)
    if n < 2:
        return 0.0
    mx, my = sum(xs) / n, sum(ys) / n
    sxx = sum((x - mx) ** 2 for x in xs)
    return sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / sxx if sxx else 0.0


def summarize(path: Path, warmup_minutes: float = WARMUP_MINUTES) -> dict[str, dict[str, float]]:
    with path.open(newline="", encoding="utf-8") as handle:
        rows = [{key: float(value) for key, value in row.items()} for row in csv.DictReader(handle)]
    if len(rows) < 3:
        raise ValueError(f"{path}: need at least 3 samples, got {len(rows)}")
    times = [row["t_min"] for row in rows]
    late = [index for index, t in enumerate(times) if t >= warmup_minutes]
    result: dict[str, dict[str, float]] = {}
    for key in rows[0]:
        if key == "t_min":
            continue
        values = [row[key] for row in rows]
        edge = min(3, len(values))
        result[key] = {
            "start": sum(values[:edge]) / edge,
            "end": sum(values[-edge:]) / edge,
            "min": min(values),
            "max": max(values),
            "slope_per_min": slope(times, values),
            "slope_per_min_after_warmup": slope([times[i] for i in late], [values[i] for i in late]),
        }
    result["_meta"] = {"samples": len(rows), "minutes": times[-1], "warmup_minutes": warmup_minutes}
    return result


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--seconds", type=float, default=1800)
    parser.add_argument("--sample", type=float, default=10)
    parser.add_argument("--variants", nargs="+", default=["release", "debug"], choices=["release", "debug"])
    parser.add_argument("--scenarios", nargs="+", default=["lan-reply", "lan-silent"],
                        choices=["lan-reply", "lan-silent"])
    parser.add_argument("--source", default="frames", choices=["frames", "direct"])
    parser.add_argument("--summarize-only", action="store_true", help="only summarize existing CSV files in --out")
    args = parser.parse_args(argv)
    args.out.mkdir(parents=True, exist_ok=True)
    runs = [(variant, scenario) for variant in args.variants for scenario in args.scenarios]
    if not args.summarize_only:
        binaries = {variant: build(args.out / "build", variant == "debug") for variant in args.variants}
        processes = []
        for variant, scenario in runs:
            name = f"{variant}-{scenario}-{args.source}"
            log = (args.out / f"{name}.log").open("w", encoding="utf-8")
            processes.append((name, log, subprocess.Popen(
                [str(binaries[variant]), "--seconds", str(args.seconds), "--sample", str(args.sample),
                 "--scenario", scenario, "--source", args.source, "--fixture", str(FIXTURE),
                 "--out", str(args.out / f"{name}.csv")],
                stdout=log, stderr=subprocess.STDOUT)))
        failed = False
        for name, log, process in processes:
            code = process.wait()
            log.close()
            if code:
                print(f"{name}: exit {code}", file=sys.stderr)
                failed = True
        if failed:
            return 1
    summary = {}
    for variant, scenario in runs:
        name = f"{variant}-{scenario}-{args.source}"
        summary[name] = summarize(args.out / f"{name}.csv")
        meta = summary[name]["_meta"]
        print(f"== {name} ({meta['samples']} samples, {meta['minutes']:.1f} min)")
        for key in ("resident_bytes", "footprint_bytes", "threads", "fds", "routed_hz", "received_red_hz",
                    "route_complete_ms_p95", "route_receive_ms_p95", "probe_alive_ratio", "probe_connections"):
            item = summary[name][key]
            print(f"  {key:24s} start={item['start']:.6g} end={item['end']:.6g} "
                  f"slope/min={item['slope_per_min']:.4g} late slope/min={item['slope_per_min_after_warmup']:.4g}")
    (args.out / "summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    sys.exit(main())
