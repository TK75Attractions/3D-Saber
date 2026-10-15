#!/usr/bin/env python3
"""GUI不要の発熱調査。実ソースのメトリクス・検出と preview 更新の setter 回帰。"""
import argparse
from pathlib import Path
import subprocess
import tempfile

from run import ROOT, block


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--revision", help="比較する Git revision")
    parser.add_argument("--frames", type=int, default=6000)
    args = parser.parse_args()
    if args.frames < 600:
        parser.error("--frames must be at least 600")
    source = ROOT / "PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender"

    def read(name):
        if args.revision:
            return subprocess.check_output(["git", "show", f"{args.revision}:{(source / name).relative_to(ROOT)}"], cwd=ROOT, text=True)
        return (source / name).read_text()

    # UIKit を起動せず、実際の updateUIView 本体で再接続回数を確認する。
    preview = '''import Foundation
final class Session {}
final class Layer {
    var assignments = 0
    var session: Session? { didSet { assignments += 1 } }
}
final class PreviewView { let videoPreviewLayer = Layer() }
struct PreviewUpdate {
    typealias Context = Void
    let session: Session
'''
    preview += block(read("CameraPreview.swift"), "func updateUIView(") + "\n}\n"
    with tempfile.TemporaryDirectory(prefix="phonesaber-heat-") as directory:
        tmp = Path(directory)
        sources = []
        for name in ["DetectionCore.swift", "BGRADetection.swift", "DeviceHealth.swift"]:
            path = tmp / name
            path.write_text(read(name))
            sources.append(str(path))
        (tmp / "Preview.swift").write_text(preview)
        subprocess.run(["xcrun", "clang", "-O2", "-c", str(Path(__file__).with_name("allocations.c")), "-o", str(tmp / "allocations.o")], check=True)
        subprocess.run(["xcrun", "swiftc", "-O", "-whole-module-optimization", *sources,
                        str(tmp / "Preview.swift"), str(Path(__file__).with_name("HeatHarness.swift")),
                        str(tmp / "allocations.o"), "-module-cache-path", str(tmp / "cache"),
                        "-o", str(tmp / "heat")], check=True)
        return subprocess.run([str(tmp / "heat"), str(args.frames)]).returncode


if __name__ == "__main__":
    raise SystemExit(main())
