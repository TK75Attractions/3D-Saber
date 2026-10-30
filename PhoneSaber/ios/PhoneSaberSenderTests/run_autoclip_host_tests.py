#!/usr/bin/env python3
"""実際の認識・AutoClip・XCTestをmacOSで実行する（Simulator不要）。"""
from pathlib import Path
import subprocess
import tempfile
import re

from run_latency_host_tests import block

SOURCE = Path(__file__).resolve().parents[1] / "PhoneSaberSender/PhoneSaberSender"
TESTS = Path(__file__).resolve().parent


def main():
    processor = (SOURCE / "FrameProcessor.swift").read_text()
    # observer の契約を実ソースから読む。認識・選択・recorder は差し替えない。
    contract = processor[processor.index("struct ObservedFrame {"):processor.index("final class FrameProcessor:")]
    developer = Path(subprocess.check_output(["xcode-select", "-p"], text=True).strip()) / "Platforms/MacOSX.platform/Developer"
    frameworks = developer / "Library/Frameworks"
    with tempfile.TemporaryDirectory(prefix="phonesaber-autoclip-") as directory:
        tmp = Path(directory)
        camera = (SOURCE / "CameraViewModel.swift").read_text()
        exposure = block(camera, "struct AutoExposureController")
        adapter = "enum FrameProcessor {\n" + block(processor, "static func exposureFrame(") + "\n}\n"
        (tmp / "Observer.swift").write_text("import Foundation\nimport CoreVideo\n" + contract + "\n" + exposure + "\n" + adapter)
        core_tests = (TESTS / "DetectionCoreTests.swift").read_text()
        # Simulatorを起動できない環境でも、露出判定の同じXCTestを公式Detection検証で実行する。
        exposure_tests = "import Foundation\nimport XCTest\nfinal class AutoExposureTests: XCTestCase {\n"
        for name in re.findall(r"func (testFixedShutterPlanAutoExposure\w+)\(", core_tests):
            if "Storage" not in name:
                exposure_tests += block(core_tests, "func " + name + "(") + "\n"
        (tmp / "AutoExposureTests.swift").write_text(exposure_tests + "}\n")
        (tmp / "main.swift").write_text('''import XCTest
import Darwin
let suite = XCTestSuite(name: "AutoClip and recognition-driven ISO")
suite.addTest(AutoClipTests.defaultTestSuite)
suite.addTest(AutoExposureTests.defaultTestSuite)
suite.run()
guard let run = suite.testRun, run.executionCount >= 13, run.hasSucceeded else { exit(1) }
''')
        subprocess.run(["xcrun", "swiftc", "-O", "-D", "AUTOCLIP_STANDALONE",
                        *(str(SOURCE / f"{name}.swift") for name in ("DetectionCore", "BGRADetection", "MetricsDatagram", "AutoClip")),
                        str(tmp / "Observer.swift"), str(tmp / "AutoExposureTests.swift"), str(TESTS / "AutoClipTests.swift"), str(tmp / "main.swift"),
                        "-I", str(developer / "usr/lib"), "-L", str(developer / "usr/lib"),
                        "-F", str(frameworks), "-Xlinker", "-rpath", "-Xlinker", str(frameworks),
                        "-Xlinker", "-rpath", "-Xlinker", str(developer / "usr/lib"),
                        "-module-cache-path", str(tmp / "module-cache"), "-o", str(tmp / "tests")], check=True)
        subprocess.run([str(tmp / "tests")], check=True)


if __name__ == "__main__":
    main()
