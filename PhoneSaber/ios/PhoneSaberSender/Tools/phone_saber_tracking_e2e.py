#!/usr/bin/env python3
"""Compile actual Mac recording/triage sources and emit deterministic capture bundles.

Run test_phone_saber_tracking_e2e.py for the mocked analysis/repair integration.
Only the CameraViewModel interval type declarations are extracted for this host
build because the rest of that view model imports iOS-only UIKit.
"""
from __future__ import annotations

import os
from pathlib import Path
import subprocess

# checkout 内の共通ビルドスイッチ。認識の既定値は両言語の core にも置く。
import sys as _guard_sys
from pathlib import Path as _GuardPath
_guard_sys.path.insert(0, str(next(p for p in _GuardPath(__file__).resolve().parents if (p / "guard_build.py").is_file())))
import guard_build

REPO = Path(__file__).resolve().parents[3]


def capture_bundles(destination: Path) -> dict:
    import json
    source = REPO / "ios/PhoneSaberSender/PhoneSaberSender"
    destination.mkdir(parents=True, exist_ok=True)
    camera = (source / "CameraViewModel.swift").read_text()
    support = destination / "CameraIntervals.swift"
    support.write_text("import Foundation\n" + camera[camera.index("struct CameraFrameIntervalStatistics:"):
        camera.index("enum CameraLifecycleState:")] + "\nstruct DebugBundleTransfer {\n    static let shared = DebugBundleTransfer()\n    func enqueue(bundleURL: URL, automatic: Bool = true) {} // Harness never uploads captures.\n}\n")
    binary = destination / "capture-harness"
    command = ["xcrun", "swiftc", *guard_build.swift_flags(), "-O", "-D", "DEBUG"] + [str(source / name) for name in (
        "DetectionCore.swift", "BGRADetection.swift", "FrameProcessor.swift",
        "DebugVideoRecorder.swift", "DebugRecordingTriage.swift", "GuidedRecording.swift")] + [str(support),
        str(REPO / "ios/PhoneSaberSenderTests/TrackingDiagnosticsCaptureHarness.swift"), "-o", str(binary)]
    compiled = subprocess.run(command, text=True, capture_output=True, timeout=180)
    if compiled.returncode:
        raise RuntimeError(compiled.stderr)
    env = dict(os.environ, SWIFT_DETERMINISTIC_HASHING="1")
    run = subprocess.run([str(binary), str(destination)], text=True, capture_output=True, env=env, timeout=90)
    if run.returncode:
        raise RuntimeError(run.stdout + run.stderr)
    return json.loads((destination / "capture-index.json").read_text())
