#!/usr/bin/env python3
"""GUI不要の転送回帰。転送本体・容量計算・既存XCTestを macOS で実行する。"""
from pathlib import Path
import subprocess
import tempfile

from run_latency_host_tests import SOURCE, TESTS, block


def main():
    recorder = (SOURCE / "DebugVideoRecorder.swift").read_text()
    support = "import Foundation\nimport CoreMedia\n"
    # UIKit / カメラ / 動画エンコーダには依存しない実際のストレージ型を使用する。
    for marker in ["enum DebugRecordingLimits", "struct DebugRecordingSessionSummary",
                   "enum DebugRecordingStorage", "enum DebugVideoRecorderError"]:
        support += block(recorder, marker) + "\n"
    support += block((SOURCE / "FrameProcessor.swift").read_text(), "enum HostMonotonicClock") + "\n"
    tests = (TESTS / "P2PTransportTests.swift").read_text()
    focused = "import XCTest\nimport Network\nimport Foundation\n"
    for marker in ["final class DebugBundleOutboxTests", "final class DebugBundleLogTests"]:
        focused += block(tests, marker) + "\n"
    developer = Path(subprocess.check_output(["xcode-select", "-p"], text=True).strip()) / "Platforms/MacOSX.platform/Developer"
    frameworks = developer / "Library/Frameworks"
    with tempfile.TemporaryDirectory(prefix="phonesaber-debug-transfer-") as directory:
        tmp = Path(directory)
        (tmp / "Support.swift").write_text(support)
        (tmp / "Tests.swift").write_text(focused)
        (tmp / "main.swift").write_text('''import XCTest
import Darwin
let suite = XCTestSuite(name: "Debug transfer (host; no discovery or upload)")
suite.addTest(DebugBundleOutboxTests.defaultTestSuite)
suite.addTest(DebugBundleLogTests.defaultTestSuite)
suite.run()
guard let run = suite.testRun, run.executionCount >= 7, run.hasSucceeded else { exit(1) }
''')
        sources = [str(SOURCE / name) for name in [
            "DebugBundleTransfer.swift", "UDPSender.swift", "P2PSender.swift",
            "P2PProtocol.swift", "MetricsDatagram.swift"]]
        subprocess.run(["xcrun", "swiftc", "-O", "-D", "DEBUG",
                        *sources, str(tmp / "Support.swift"), str(tmp / "Tests.swift"),
                        str(tmp / "main.swift"),
                        "-I", str(developer / "usr/lib"), "-L", str(developer / "usr/lib"), "-F", str(frameworks),
                        "-Xlinker", "-rpath", "-Xlinker", str(frameworks), "-Xlinker", "-rpath", "-Xlinker", str(developer / "usr/lib"),
                        "-module-cache-path", str(tmp / "cache"), "-o", str(tmp / "tests")], check=True)
        return subprocess.run([str(tmp / "tests")]).returncode


if __name__ == "__main__":
    raise SystemExit(main())
