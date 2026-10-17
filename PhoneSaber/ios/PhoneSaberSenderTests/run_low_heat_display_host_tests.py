#!/usr/bin/env python3
"""GUI不要で低発熱表示の状態機械（実ソース）と XCTest を macOS 上で実行する。"""
import argparse
from pathlib import Path
import subprocess
import tempfile

SOURCE = Path(__file__).resolve().parents[1] / "PhoneSaberSender" / "PhoneSaberSender"
TESTS = Path(__file__).resolve().parent


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.parse_args()
    developer = Path(subprocess.check_output(["xcode-select", "-p"], text=True).strip()) / "Platforms/MacOSX.platform/Developer"
    frameworks = developer / "Library/Frameworks"
    with tempfile.TemporaryDirectory(prefix="phonesaber-low-heat-") as directory:
        tmp = Path(directory)
        (tmp / "main.swift").write_text('''import XCTest
import Darwin
let suite = LowHeatDisplayPolicyTests.defaultTestSuite
suite.run()
guard let run = suite.testRun, run.executionCount >= 9, run.hasSucceeded else { exit(1) }
print("low-heat display host tests: PASS (\\(run.executionCount) tests)")
''')
        subprocess.run(["xcrun", "swiftc", "-O", "-D", "LOW_HEAT_DISPLAY_STANDALONE",
                        "-module-cache-path", str(tmp / "cache"),
                        str(SOURCE / "LowHeatDisplayPolicy.swift"), str(TESTS / "LowHeatDisplayTests.swift"),
                        str(tmp / "main.swift"),
                        "-I", str(developer / "usr/lib"), "-L", str(developer / "usr/lib"),
                        "-F", str(frameworks), "-Xlinker", "-rpath", "-Xlinker", str(frameworks),
                        "-Xlinker", "-rpath", "-Xlinker", str(developer / "usr/lib"),
                        "-o", str(tmp / "tests")], check=True)
        return subprocess.run([str(tmp / "tests")], timeout=30).returncode


if __name__ == "__main__":
    raise SystemExit(main())
