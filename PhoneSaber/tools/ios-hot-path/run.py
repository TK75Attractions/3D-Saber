#!/usr/bin/env python3
"""Mac Release harness: production senders + extracted non-camera frame/UI methods."""
import argparse
import pathlib
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[3]


def block(text, marker):
    start = text.index(marker)
    opening = text.index("{", start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (text[end] == "{") - (text[end] == "}")
        end += 1
    return text[start:end]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-root", type=pathlib.Path, default=ROOT)
    parser.add_argument("--revision", help="read source from a Git revision (e.g. origin/main)")
    parser.add_argument("--frames", type=int, default=240)
    args = parser.parse_args()
    source = args.source_root / "PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender"
    def read_source(name):
        if args.revision:
            relative = (source / name).relative_to(args.source_root)
            return subprocess.check_output(["git", "-C", str(args.source_root), "show", f"{args.revision}:{relative}"], text=True)
        return (source / name).read_text()

    core = read_source("DetectionCore.swift")
    frame = read_source("FrameProcessor.swift")
    camera = read_source("CameraViewModel.swift")
    # 検出・カメラ・録画は除外。結果生成と expiry の本番メソッドはそのまま使う。
    scaffold = '''import Foundation
import QuartzCore
import CoreMedia
import CoreVideo
final class DebugVideoRecorder {
    func recordTransmission(frameID: UInt64, color: String, coordinates: String,
                            sourceEndpoints: (PixelPoint, PixelPoint)) {}
}
'''
    for marker in ["struct PixelPoint:", "enum SaberColor:", "func scaledPoint(", "func payload("]:
        scaffold += block(core, marker) + "\n"
    for marker in ["struct CameraFrameIntervalStatistics:", "struct DetectedSaber {", "struct FrameTrace {", "enum HostMonotonicClock {"]:
        scaffold += block(camera if marker.startswith("struct CameraFrame") else frame, marker) + "\n"
    scaffold += block(camera, "final class CameraUIPendingState:") + "\n"
    scaffold += '''final class FrameProcessor {
    let queue = DispatchQueue(label: "harness.frames")
    var onResult: (([DetectedSaber], Int, Int, TimeInterval, Int, FrameTrace?) -> Void)?
    private var tracks: [SaberColor: Track] = [.red: Track(), .blue: Track()]
    private let holdDuration: TimeInterval = 0.18
    private let clock: () -> TimeInterval = { HostMonotonicClock.now() }
    private let expiryScheduler: ((DispatchQueue, TimeInterval, DispatchWorkItem) -> Void)? = {
        queue, delay, item in queue.asyncAfter(deadline: .now() + delay, execute: item)
    }
    private var expiryWorkItem: DispatchWorkItem?
    private var lastDimensions: (Int, Int)?
    private var generation = 0
    private let pendingLock = NSLock()
    private var pendingFrame: PendingFrame?
    private var pendingFrameShouldSave = false
    private var rawFrameSaveRequested = false
    private var workerScheduled = false
    private var receivedFrames = 0
    private var replacedPendingFrames = 0
    private var nextFrameSequence: UInt64 = 0
    // 検出だけを固定結果へ置き換える。submit/drain/emit は本番メソッド。
    private func process(_ sampleBuffer: CMSampleBuffer, saveRequestedRawFrame: Bool,
                         sequence: UInt64, callbackHostTime: TimeInterval,
                         captureHostTime: TimeInterval?, callbackIntervalMs: Double?, callbackWorkMs: Double?) {
        let a = PixelPoint(x: 10, y: 20), b = PixelPoint(x: 110, y: 20)
        emitResults([(.red, (a,b)), (.blue, (a,b))], width: 640, height: 480,
                    processingStart: clock(), generation: generation)
    }
    private var debugVideoRecorder: DebugVideoRecorder? = nil
'''
    for marker in ["private struct PendingFrame {", "func submit(", "private func drainLatestFrames(", "private struct Track {", "private func emitResults(", "private func trackedResult(", "func processDetectedForTesting(", "private func scheduleExpiry(", "private func expireHeldFrames(", "private func stableEndpoints(", "private func predictedEndpoints(", "private func distance("]:
        if marker == "private func trackedResult(" and marker not in frame:
            continue
        scaffold += ("@discardableResult\n" if marker == "private func emitResults(" else "") + block(frame, marker) + "\n"
    scaffold += "}\n"
    with tempfile.TemporaryDirectory(prefix="phonesaber-hot-path-") as temporary:
        build = pathlib.Path(temporary)
        (build / "Scaffold.swift").write_text(scaffold)
        subprocess.run(["xcrun", "clang", "-O2", "-c", str(pathlib.Path(__file__).with_name("allocations.c")), "-o", str(build / "allocations.o")], check=True)
        sender_sources = []
        for name in ["UDPSender.swift", "P2PSender.swift", "P2PProtocol.swift"]:
            copied = build / name
            copied.write_text(read_source(name))
            sender_sources.append(str(copied))
        subprocess.run(["xcrun", "swiftc", "-O", "-whole-module-optimization", "-module-cache-path", str(build / "cache"), *sender_sources, str(build / "Scaffold.swift"), str(pathlib.Path(__file__).with_name("Harness.swift")), str(build / "allocations.o"), "-o", str(build / "harness")], check=True)
        subprocess.run([str(build / "harness"), str(args.frames)], check=True)


if __name__ == "__main__":
    main()
