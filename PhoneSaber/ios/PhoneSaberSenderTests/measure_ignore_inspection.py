#!/usr/bin/env python3
"""実FrameProcessorの変更前/閉/開を比較。カメラ・ネットワーク・UI描画は含まない。"""
import json
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[3]
SOURCE = ROOT / "PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender"
sys.path.insert(0, str(SOURCE.parent / "Tools"))
from test_ios_ignore_inspection import MAIN, SUPPORT, block  # noqa: E402
from phone_saber_auto_iso_host_support import auto_iso_support  # noqa: E402


def main():
    camera = (SOURCE / "CameraViewModel.swift").read_text()
    support = SUPPORT[:SUPPORT.index("@MainActor enum LearnSleeper")] + auto_iso_support(SOURCE)
    for marker in ["struct CameraFrameIntervalStatistics:", "struct CameraFrameIntervalWindow"]:
        support += block(camera, marker) + "\n"
    swift = BENCHMARK + block(MAIN, "static func sampleBuffer()").replace("check(", "precondition(") + "\n}\n"
    results = {}
    with tempfile.TemporaryDirectory(prefix="phonesaber-ignore-benchmark-") as temporary:
        tmp = Path(temporary)
        (tmp / "Support.swift").write_text(support)
        runtime = [str(SOURCE / f"{name}.swift") for name in (
            "DetectionCore", "BGRADetection", "DebugVideoRecorder", "DebugRecordingTriage", "GuidedRecording", "MetricsDatagram")]
        for variant in ["before", "after"]:
            frame = SOURCE / "FrameProcessor.swift"
            content = subprocess.check_output(["git", "show", f"HEAD:{frame.relative_to(ROOT)}"], cwd=ROOT, text=True) \
                if variant == "before" else frame.read_text()
            (tmp / "FrameProcessor.swift").write_text(content)
            (tmp / "Main.swift").write_text(swift.replace("INSPECTION_CODE", "" if variant == "before" else INSPECTION)
                .replace("INSPECTION_CHECK", "" if variant == "before" else CHECK))
            binary = tmp / f"{variant}-benchmark"
            subprocess.run(["xcrun", "swiftc", "-O", "-parse-as-library", "-module-name", "PhoneSaberBenchmark", "-module-cache-path", str(tmp / "cache"),
                            str(tmp / "Support.swift"), str(tmp / "Main.swift"), str(tmp / "FrameProcessor.swift"),
                            *runtime, "-o", str(binary)], check=True)
        conditions = [("before", "closed"), ("after", "closed"), ("after", "open")]
        for trial in range(3):
            # コンパイル終了後に交互に計測する。順序も回して温度・負荷の偏りを抑える。
            for variant, mode in conditions[trial:] + conditions[:trial]:
                binary = tmp / f"{variant}-benchmark"
                completed = subprocess.run([str(binary), mode], text=True, capture_output=True, check=True, timeout=60)
                result = json.loads(completed.stdout)
                key = f"{variant}-{mode}"
                if key not in results:
                    results[key] = result
                else:
                    previous = results[key]
                    for digest in ["candidateDigest", "selectedDigest", "udpDigest"]:
                        assert result[digest] == previous[digest], digest
                    previous["samplesMs"].extend(result["samplesMs"])
        for key, result in results.items():
            samples = sorted(result.pop("samplesMs"))
            result.update(n=len(samples), p50Ms=samples[len(samples) // 2], p95Ms=samples[int((len(samples) - 1) * 0.95)])
            print(key, json.dumps(result, sort_keys=True), flush=True)
        reference = results["before-closed"]
        for result in results.values():
            assert result["candidateDigest"] == reference["candidateDigest"], "候補が変化"
            assert result["selectedDigest"] == reference["selectedDigest"], "選択端点が変化"
            assert result["udpDigest"] == reference["udpDigest"], "UDP座標文字列が変化"
        print("before/after: all candidates, selected endpoints and UDP coordinate strings identical")
        return results


INSPECTION = r'''
        let session = UUID()
        if open { processor.setIgnoreInspectionSession(session) }
        defer { processor.setIgnoreInspectionSession(nil) }
'''

CHECK = r'''
        // mainが停止していても最新1件だけ。1000フレーム後の1回の取得が最新となる。
        if open {
            let snapshot = processor.takeIgnoreInspection(session: session)!
            precondition(snapshot.width == 480 && snapshot.height == 640)
            precondition(snapshot.sequence == 1040)
            precondition(processor.takeIgnoreInspection(session: session) == nil)
        }
'''

BENCHMARK = r'''
import Foundation
import CoreVideo
import CoreMedia
func canonical(_ value: Any) -> String {
    if let dictionary = value as? [String: Double] {
        return dictionary.keys.sorted().map { "\($0):\(dictionary[$0]!.bitPattern)" }.joined(separator: ";")
    }
    if let d = value as? Double { return "d\(d.bitPattern)" }
    if let i = value as? Int { return "i\(i)" }
    if let b = value as? Bool { return "b\(b)" }
    if let s = value as? String { return s }
    let mirror = Mirror(reflecting: value)
    if mirror.children.isEmpty { return String(describing: value) }
    return mirror.children.map { ($0.label ?? "_") + ":" + canonical($0.value) }.joined(separator: ";")
}
func digest(_ text: String, into value: inout UInt64) {
    for byte in text.utf8 { value = (value ^ UInt64(byte)) &* 1099511628211 }
}
final class Observer: FrameAnalysisObserver {
    var candidates: UInt64 = 14695981039346656037
    var selected: UInt64 = 14695981039346656037
    func observe(_ frame: ObservedFrame) {
        for color: SaberColor in [.red, .blue] {
            digest(canonical(frame.analysis.candidates[color] ?? []), into: &candidates)
            digest(canonical(frame.analysis.selected[color] as Any), into: &selected)
        }
    }
}
@main enum Benchmark {
    static func main() throws {
        let frame = try sampleBuffer()
        let pixels = CMSampleBufferGetImageBuffer(frame)!
        CVPixelBufferLockBaseAddress(pixels, [])
        let bytes = CVPixelBufferGetBaseAddress(pixels)!.assumingMemoryBound(to: UInt8.self)
        let row = CVPixelBufferGetBytesPerRow(pixels)
        for y in 350...550 { for x in 353...359 {
            let i = y * row + x * 4
            bytes[i] = 30; bytes[i+1] = 45; bytes[i+2] = 245; bytes[i+3] = 255
        } }
        for y in 100...300 { for x in 373...379 {
            let i = y * row + x * 4
            bytes[i] = 245; bytes[i+1] = 45; bytes[i+2] = 30; bytes[i+3] = 255
        } }
        CVPixelBufferUnlockBaseAddress(pixels, [])
        var clock = 1000.0
        let processor = FrameProcessor(clock: { clock }, expiryScheduler: nil)
        let zone = SaberIgnoreZone(minX: 0.1, minY: 0.1, maxX: 0.4, maxY: 0.6)
        processor.setIgnoreZones([.red: [zone, zone], .blue: [zone, zone]])
        processor.setTrackLockEnabled(true)
        let observer = Observer()
        var udp: UInt64 = 14695981039346656037
        processor.onResult = { results, width, height, _, _, _ in
            for result in results {
                digest("\(result.color) \(result.isFresh) \(result.isPredicted) " + payload(for: result.endpoints,
                    source: (width, height), output: (1920, 1080), mirrorX: true, mirrorY: false), into: &udp)
            }
        }
        let open = CommandLine.arguments[1] == "open"
        _ = open
        INSPECTION_CODE
        processor.setAnalysisObserver(observer)
        for _ in 0..<40 {
            clock += 1.0 / 60
            processor.queue.sync { processor.process(frame) }
        }
        // 計時には候補の文字列化を含めない。
        processor.setAnalysisObserver(nil)
        var elapsed: [Double] = []
        for i in 0..<1000 {
            clock += 1.0 / 60
            let start = ProcessInfo.processInfo.systemUptime
            processor.queue.sync { processor.process(frame) }
            if i >= 50 { elapsed.append((ProcessInfo.processInfo.systemUptime - start) * 1000) }
        }
        elapsed.sort()
        INSPECTION_CHECK
        let output: [String: Any] = ["n": elapsed.count, "p50Ms": elapsed[elapsed.count / 2],
            "p95Ms": elapsed[Int(Double(elapsed.count - 1) * 0.95)],
            "samplesMs": elapsed,
            "candidateDigest": String(observer.candidates), "selectedDigest": String(observer.selected), "udpDigest": String(udp)]
        print(String(data: try JSONSerialization.data(withJSONObject: output, options: [.sortedKeys]), encoding: .utf8)!)
    }
'''

if __name__ == "__main__":
    main()
