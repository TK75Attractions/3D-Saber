#!/usr/bin/env python3
"""Mac harness: per-frame cost of the iPhone auto clip recorder (AutoClip.swift).

Compiles the production DetectionCore/BGRADetection/AutoClip sources plus the
ObservedFrame/FrameAnalysisObserver declarations extracted from FrameProcessor.swift,
runs analyzeSabers once on a real lossless fixture and feeds that analysis to the
recorder for --frames frames at 60 fps (one PC-style clip request in the middle).
The project sets no SWIFT_OPTIMIZATION_LEVEL, so the Xcode Debug build installed on
the phone compiles with -O plus the DEBUG condition: --opt debug (default) matches it.
"""
import argparse
import pathlib
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[3]
SOURCE = ROOT / "PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender"
FIXTURE = ROOT / ("PhoneSaber/ios/PhoneSaberSenderTests/Fixtures/lossless-regression/"
                  "phonesaber_20260923_143446_247/frame_59.png")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--frames", type=int, default=1200)
    parser.add_argument("--opt", choices=["debug", "release", "onone"], default="debug")
    parser.add_argument("--fixture", type=pathlib.Path, default=FIXTURE)
    args = parser.parse_args()
    frame = (SOURCE / "FrameProcessor.swift").read_text()
    start = frame.index("/// One processed camera frame")
    end = frame.index("final class FrameProcessor")
    here = pathlib.Path(__file__).resolve().parent
    with tempfile.TemporaryDirectory(prefix="phonesaber-autoclip-") as temporary:
        build = pathlib.Path(temporary)
        (build / "Observer.swift").write_text("import Foundation\n" + frame[start:end])
        # Top-level code must live in main.swift when several files are compiled.
        (build / "main.swift").write_text((here / "AutoClipHarness.swift").read_text())
        subprocess.run(["xcrun", "clang", "-O2", "-c", str(here / "allocations.c"), "-o", str(build / "allocations.o")],
                       check=True)
        flags = {"debug": ["-O", "-D", "DEBUG"], "release": ["-O", "-whole-module-optimization"],
                 "onone": ["-Onone", "-D", "DEBUG"]}[args.opt]
        subprocess.run(["xcrun", "swiftc", *flags, "-module-cache-path", str(build / "cache"),
                        str(SOURCE / "DetectionCore.swift"), str(SOURCE / "BGRADetection.swift"),
                        str(build / "Observer.swift"), str(SOURCE / "AutoClip.swift"), str(SOURCE / "MetricsDatagram.swift"),
                        str(build / "main.swift"), str(build / "allocations.o"),
                        "-o", str(build / "harness")], check=True)
        print("build: " + {"debug": "-O -D DEBUG (Xcode Debug as installed on the phone)",
                           "release": "-O WMO (Release)", "onone": "-Onone (not used by the app)"}[args.opt])
        subprocess.run([str(build / "harness"), str(args.fixture), str(args.frames), str(build / "out")], check=True)


if __name__ == "__main__":
    main()
