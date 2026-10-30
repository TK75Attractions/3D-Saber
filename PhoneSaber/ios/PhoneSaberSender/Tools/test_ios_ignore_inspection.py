"""個別解除の実ViewModel/FrameProcessorと実BGRA選択経路をhostで検証する。

カメラとUIは起動しない。保存先だけメモリに置換し、既存キー・JSONの往復を検査する。
学習の待機だけ手動制御し、遅延完了・古い画面操作の順序を確定する。
"""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

from phone_saber_auto_iso_host_support import auto_iso_support

SOURCE = Path(__file__).resolve().parents[1] / "PhoneSaberSender"
TESTS = SOURCE.parent.parent / "PhoneSaberSenderTests"


def block(text, marker):
    start = text.index(marker)
    end = text.index("{", start) + 1
    depth = 1
    while depth:
        depth += (text[end] == "{") - (text[end] == "}")
        end += 1
    return text[start:end]


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "macOS/Swift required")
class IgnoreInspectionTests(unittest.TestCase):
    def test_removal_persistence_learning_and_inspection_lifecycle(self):
        camera = (SOURCE / "CameraViewModel.swift").read_text()
        section = camera[camera.index("    private var ignoreZones:"):camera.index("    private static let trackLockKey")]
        section = section.replace(
            "try? await Task.sleep(nanoseconds: UInt64(SaberIgnoreLearner.seconds * 1_000_000_000))",
            "await LearnSleeper.wait()")
        support = SUPPORT + auto_iso_support(SOURCE)
        for marker in ["struct CameraFrameIntervalStatistics:", "struct CameraFrameIntervalWindow"]:
            support += block(camera, marker) + "\n"
        support += "@MainActor final class CameraViewModel {\nlet processor = FrameProcessor(expiryScheduler: nil)\n"
        support += next(line for line in camera.splitlines() if "static let ignoreZonesKey =" in line) + "\n"
        support += section + block(camera, "private func applyIgnoreZones()") + "\n"
        support += "func poll() { publishIgnoreInspection() }\n}\n"
        with tempfile.TemporaryDirectory(prefix="phonesaber-ignore-inspection-") as temporary:
            tmp = Path(temporary)
            (tmp / "Support.swift").write_text(support)
            (tmp / "Main.swift").write_text(MAIN)
            runtime = [SOURCE / f"{name}.swift" for name in (
                "DetectionCore", "BGRADetection", "FrameProcessor", "DebugVideoRecorder",
                "DebugRecordingTriage", "GuidedRecording", "MetricsDatagram")]
            compiled = subprocess.run([
                "xcrun", "swiftc", "-O", "-parse-as-library", "-module-cache-path", str(tmp / "cache"),
                str(tmp / "Support.swift"), str(tmp / "Main.swift"), *map(str, runtime), "-o", str(tmp / "test")
            ], capture_output=True, text=True, timeout=300)
            self.assertEqual(compiled.returncode, 0, compiled.stderr)
            run = subprocess.run([str(tmp / "test")], capture_output=True, text=True, timeout=60)
            self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
            self.assertIn("removal/persistence/lifecycle PASS", run.stdout)
            print(run.stdout)


SUPPORT = r'''
import Foundation
import Combine
import CoreMedia
struct ConnectivityLog {
    static let shared = ConnectivityLog()
    func record(_ tag: String, _ text: String) {}
    func exportTail() -> String { "" }
}
struct DebugBundleTransfer {
    static let shared = DebugBundleTransfer()
    func enqueue(bundleURL: URL, automatic: Bool = true) {}
}
// ユーザーの保存済み設定を上書きせず、実codecと保存キーを検査する。
final class UserDefaults {
    static let standard = UserDefaults()
    var values: [String: Any] = [:]
    func data(forKey key: String) -> Data? { values[key] as? Data }
    func string(forKey key: String) -> String? { values[key] as? String }
    func object(forKey key: String) -> Any? { values[key] }
    func set(_ value: Any, forKey key: String) { values[key] = value }
}
@MainActor enum LearnSleeper {
    static var pending: [CheckedContinuation<Void, Never>] = []
    static func wait() async { await withCheckedContinuation { pending.append($0) } }
    static func finish() async {
        pending.removeFirst().resume()
        for _ in 0..<30 { await Task.yield() }
    }
}
'''

MAIN = r'''
import Foundation
import CoreMedia
import CoreVideo

func check(_ condition: Bool, line: UInt = #line) {
    if !condition { fputs("FAIL at generated Main.swift:\(line)\n", stderr); exit(1) }
}

@main enum Main {
    @MainActor static func main() async throws {
        let rectangle = #"{"minX":0.1,"minY":0.1,"maxX":0.4,"maxY":0.6}"#
        let endpoints = #"{"minX":0.1,"minY":0.1,"maxX":0.4,"maxY":0.6,"objectEndpoints":{"x1":0.3,"y1":0.2,"x2":0.3,"y2":0.5}}"#
        let objects = #"{"minX":0.1,"minY":0.1,"maxX":0.4,"maxY":0.6,"objects":[{"x1":0.3,"y1":0.2,"x2":0.3,"y2":0.5}],"objectEndpoints":{"x1":0.3,"y1":0.2,"x2":0.3,"y2":0.5}}"#
        let original = try JSONDecoder().decode([String: [SaberIgnoreZone]].self,
            from: Data("{\"red\":[\(objects),\(endpoints)],\"blue\":[\(rectangle),\(objects),\(endpoints),\(objects)]}".utf8))
        let key = "PhoneSaber.ignoreZones.v1"
        for station in ["A", "B"] {
            UserDefaults.standard.set(try JSONEncoder().encode(original), forKey: key)
            let model = CameraViewModel()
            let oldSession = model.beginIgnoreInspection()
            let oldEntries = model.ignoreZoneEntries
            let session = model.beginIgnoreInspection()
            // 古い画面の終了・解除は、新しい画面や設定を変更できない。
            model.endIgnoreInspection(oldSession)
            model.removeIgnoreZone(oldEntries[3], session: oldSession)
            check(model.ignoreZoneEntries.count == 6)
            model.ignoreCurrentDetection(.blue)
            while LearnSleeper.pending.isEmpty { await Task.yield() }
            check(model.ignoreLearning)
            model.removeIgnoreZone(model.ignoreZoneEntries[3], session: session)
            check(!model.ignoreLearning)
            // 二重操作・削除前の別添字は、残りの同形ゾーンを消せない。
            model.removeIgnoreZone(oldEntries[3], session: session)
            model.removeIgnoreZone(oldEntries[4], session: session)
            await LearnSleeper.finish()
            let expectedBlue = [original["blue"]![0], original["blue"]![2], original["blue"]![3]]
            let saved = try JSONDecoder().decode([String: [SaberIgnoreZone]].self,
                from: UserDefaults.standard.data(forKey: key)!)
            check(saved["red"] == original["red"] && saved["blue"] == expectedBlue)
            let reload = CameraViewModel()
            check(reload.ignoreZoneEntries.map(\.zone) == original["red"]! + expectedBlue)
            check(reload.ignoreZoneSummary == "赤 2 か所・青 3 か所")
            // FrameProcessorの実選択にも保存した内容が反映されている。
            let frame = try sampleBuffer()
            model.processor.queue.sync { model.processor.process(frame) }
            model.poll()
            check(model.ignoreInspectionSnapshot?.matches.contains { $0.color == .blue } == true)
            let entry = model.ignoreZoneEntries.first { $0.id.color == .blue }!
            model.removeIgnoreZone(entry, session: session)
            check(model.ignoreInspectionSnapshot == nil)
            model.poll()
            check(model.ignoreInspectionSnapshot == nil)
            model.processor.queue.sync { model.processor.process(frame) }
            model.poll()
            check(model.ignoreInspectionSnapshot?.matches.isEmpty == true)
            model.endIgnoreInspection(session)
            model.processor.queue.sync { model.processor.process(frame) }
            model.poll()
            check(model.ignoreInspectionSnapshot == nil)
            // 学習完了が先に保存した場合も、学習前の画面操作で別の登録を消さない。
            let reloadSession = reload.beginIgnoreInspection()
            let beforeLearning = reload.ignoreZoneEntries[2]
            reload.ignoreCurrentDetection(.blue)
            while LearnSleeper.pending.isEmpty { await Task.yield() }
            reload.processor.queue.sync { reload.processor.process(frame) }
            await LearnSleeper.finish()
            while reload.ignoreLearning { await Task.yield() }
            let afterLearning = reload.ignoreZoneEntries.map(\.zone)
            check(afterLearning.count > 5)
            reload.removeIgnoreZone(beforeLearning, session: reloadSession)
            check(reload.ignoreZoneEntries.map(\.zone) == afterLearning)
            reload.endIgnoreInspection(reloadSession)
            print("station=\(station) removal/persistence/lifecycle PASS (rectangle/objectEndpoints/objects)")
        }
    }

    static func sampleBuffer() throws -> CMSampleBuffer {
        let width = 480, height = 640
        var buffer: CVPixelBuffer?
        check(CVPixelBufferCreate(kCFAllocatorDefault, width, height,
            kCVPixelFormatType_32BGRA, nil, &buffer) == kCVReturnSuccess)
        let pixelBuffer = buffer!
        CVPixelBufferLockBaseAddress(pixelBuffer, [])
        let bytes = CVPixelBufferGetBaseAddress(pixelBuffer)!.assumingMemoryBound(to: UInt8.self)
        let row = CVPixelBufferGetBytesPerRow(pixelBuffer)
        memset(bytes, 0, row * height)
        for y in 120...300 { for x in 93...99 {
            let i = y * row + x * 4
            bytes[i] = 245; bytes[i+1] = 45; bytes[i+2] = 30; bytes[i+3] = 255
        } }
        CVPixelBufferUnlockBaseAddress(pixelBuffer, [])
        var format: CMVideoFormatDescription?
        check(CMVideoFormatDescriptionCreateForImageBuffer(allocator: kCFAllocatorDefault,
            imageBuffer: pixelBuffer, formatDescriptionOut: &format) == noErr)
        var timing = CMSampleTimingInfo(duration: CMTime(value: 1, timescale: 60), presentationTimeStamp: .zero,
                                        decodeTimeStamp: .invalid)
        var sample: CMSampleBuffer?
        check(CMSampleBufferCreateReadyWithImageBuffer(allocator: kCFAllocatorDefault,
            imageBuffer: pixelBuffer, formatDescription: format!, sampleTiming: &timing,
            sampleBufferOut: &sample) == noErr)
        return sample!
    }
}
'''

if __name__ == "__main__":
    unittest.main()
