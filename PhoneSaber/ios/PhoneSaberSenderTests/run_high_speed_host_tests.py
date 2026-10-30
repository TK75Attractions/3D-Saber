#!/usr/bin/env python3
"""却下したHFPSの2欠陥を実ソースで再現・回帰確認する（GUIなし）。"""
import argparse
from pathlib import Path
import subprocess
import tempfile

from run_latency_host_tests import ROOT, SOURCE, TESTS, block


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--revision", help="指定commitの実装に対して同じ回帰テストを実行")
    parser.add_argument("--debug", action="store_true", help="DEBUGの実検出経路も実行")
    args = parser.parse_args()

    def source(name):
        if args.revision:
            return subprocess.check_output(["git", "show", f"{args.revision}:{(SOURCE / name).relative_to(ROOT)}"], cwd=ROOT, text=True)
        return (SOURCE / name).read_text()

    camera = source("CameraViewModel.swift")
    frames = source("FrameProcessor.swift")
    rejected = "configureHighSpeedFrame(" in frames
    exposure_sampling = "private var exposureMissingFrames" in frames
    support = "import Foundation\nimport CoreMedia\n"
    for marker in ["struct CameraFormatOption:", "func supportsFrameRate(", "func preferredCameraFormatIndex(",
                   "func highSpeedCameraFormatIndex(", "struct CameraFrameIntervalStatistics:", "struct CameraFrameIntervalWindow"]:
        support += block(camera, marker) + "\n"
    support += "struct DebugBundleTransfer { static let shared = DebugBundleTransfer(); func enqueue(bundleURL: URL, automatic: Bool = true) {} }\n"
    support += 'enum ConnectivityLog { static let shared = Log() }; struct Log { func exportTail() -> String { "" } }\n'
    if "static func exposureFrame(" in frames:
        support += block(camera, "struct AutoExposureController") + "\n"
        for marker in ["enum AutoClipTriggerPolicy", "struct AutoClipCompetition"]:
            support += block(source("AutoClip.swift"), marker) + "\n"
    developer = Path(subprocess.check_output(["xcode-select", "-p"], text=True).strip()) / "Platforms/MacOSX.platform/Developer"
    frameworks = developer / "Library/Frameworks"
    with tempfile.TemporaryDirectory(prefix="phonesaber-high-speed-regression-") as directory:
        tmp = Path(directory)
        (tmp / "Support.swift").write_text(support)
        (tmp / "main.swift").write_text('''import XCTest
import Darwin
let suite = HighSpeedCaptureTests.defaultTestSuite
suite.run()
guard let run = suite.testRun, run.executionCount == EXPECTED, run.hasSucceeded else { exit(1) }
'''.replace('EXPECTED', '3' if exposure_sampling else '2'))
        (tmp / "HighSpeedCaptureTests.swift").write_text((TESTS / "HighSpeedCaptureTests.swift").read_text())
        inputs = [tmp / "Support.swift", tmp / "main.swift", tmp / "HighSpeedCaptureTests.swift"]
        names = ["FrameProcessor.swift", "DetectionCore.swift", "BGRADetection.swift", "DebugVideoRecorder.swift",
                 "DebugRecordingTriage.swift", "GuidedRecording.swift", "MetricsDatagram.swift"]
        if rejected:
            names.append("HighSpeedCapture.swift")
        for name in names:
            (tmp / name).write_text(source(name))
            inputs.append(tmp / name)
        subprocess.run(["xcrun", "swiftc", "-O", *(["-D", "DEBUG"] if args.debug else []),
                        *(["-D", "REJECTED_HFPS_REVISION"] if rejected else []),
                        *(["-D", "AUTO_ISO_OBSERVATIONS"] if exposure_sampling else []), "-D", "HIGH_SPEED_STANDALONE",
                        *map(str, inputs), "-I", str(developer / "usr/lib"), "-L", str(developer / "usr/lib"),
                        "-F", str(frameworks), "-Xlinker", "-rpath", "-Xlinker", str(frameworks),
                        "-Xlinker", "-rpath", "-Xlinker", str(developer / "usr/lib"),
                        "-module-cache-path", str(tmp / "cache"), "-o", str(tmp / "tests")], check=True)
        return subprocess.run([str(tmp / "tests")], timeout=60).returncode


if __name__ == "__main__":
    raise SystemExit(main())
