"""録画終了時の容量超過と bundle 公開を検証する、GUI 不要の macOS 回帰テスト。"""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


SOURCE = Path(__file__).resolve().parents[1] / "PhoneSaberSender"


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "macOS/Swift required")
class IOSRecordingStorageTests(unittest.TestCase):
    def test_session_limit_never_publishes_a_deleted_triage_bundle(self):
        with tempfile.TemporaryDirectory(
            prefix="phonesaber-recording-storage-", dir=os.environ.get("TMPDIR")
        ) as temporary:
            directory = Path(temporary)
            camera = (SOURCE / "CameraViewModel.swift").read_text()
            support = directory / "Support.swift"
            # UIKit の view model はビルドせず、既存のホスト録画テストと同じ型定義を使う。
            support.write_text("import Foundation\n" + camera[
                camera.index("struct CameraFrameIntervalStatistics:"):
                camera.index("enum CameraLifecycleState:")
            ] + SUPPORT)
            original = (SOURCE / "DebugVideoRecorder.swift").read_text()
            start = original.index("                    var triageBundleURL: URL?")
            end = original.index("#if DEBUG\n                    beforeOverlay?()", start)
            # 動画エンコーダを起動せず、finish の bundle 生成・公開部分をそのまま実行する。
            fixture = directory / "Fixture.swift"
            fixture.write_text(HEAD + original[start:end] + TAIL)
            binary = directory / "fixture"
            compiled = subprocess.run(
                ["xcrun", "swiftc", "-O", "-D", "DEBUG",
                 "-module-cache-path", str(directory / "module-cache"),
                 *[str(SOURCE / name) for name in (
                     "DetectionCore.swift", "BGRADetection.swift", "FrameProcessor.swift",
                     "DebugVideoRecorder.swift", "DebugRecordingTriage.swift", "GuidedRecording.swift")],
                 str(support), str(fixture), "-o", str(binary)],
                capture_output=True, text=True, timeout=180,
            )
            self.assertEqual(compiled.returncode, 0, compiled.stderr)
            run = subprocess.run([str(binary), str(directory)], capture_output=True,
                                 text=True, timeout=60)
            self.assertEqual(run.returncode, 0, run.stdout + run.stderr)
            self.assertIn("within-limit PASS", run.stdout)
            self.assertIn("over-limit PASS", run.stdout)


# ネットワーク送信だけを記録し、端末で許される最大長の connectivity log を供給する。
SUPPORT = r'''
final class DebugBundleTransfer {
    static let shared = DebugBundleTransfer()
    var enqueued: [URL] = []
    func enqueue(bundleURL: URL, automatic: Bool = true) { enqueued.append(bundleURL) }
}
final class ConnectivityLog {
    static let shared = ConnectivityLog()
    func exportTail() -> String { String(repeating: "x", count: 64 * 1024) }
}
'''


HEAD = r'''import Foundation
import Darwin

@main struct RecordingStorageCheck {
    static func check(_ condition: Bool, _ message: String) {
        if !condition { print("FAIL: " + message); exit(1) }
    }

    static func finalizeBundle(directory: URL, currentSessionID: String,
                               metadataURL: URL, snapshotData: Data,
                               forensicDirectoryURL: URL?) -> (URL?, String?) {
        let triageFrames = [1]
        let hasStopImages = true
        let frameCount = 1
'''


TAIL = r'''
        return (triageBundleURL, triageErrorMessage)
    }

    static func main() throws {
        for nearLimit in [false, true] {
            let directory = URL(fileURLWithPath: CommandLine.arguments[1])
                .appendingPathComponent(nearLimit ? "over-limit" : "within-limit")
            let sessionID = "phonesaber_20261014_120000_000"
            let forensic = directory.appendingPathComponent(sessionID + "_forensic")
            try FileManager.default.createDirectory(at: forensic, withIntermediateDirectories: true)
            let image = forensic.appendingPathComponent("manual_frame_1.png")
            let imageBytes = Data([137, 80, 78, 71, 13, 10, 26, 10])
            try imageBytes.write(to: image)
            let metadataURL = directory.appendingPathComponent(sessionID + "_metadata.json")
            let snapshotData = try JSONSerialization.data(withJSONObject: [
                "formatVersion": DebugRecordingMetadata.currentFormatVersion,
                "sessionID": sessionID, "width": 64, "height": 48,
                "frames": [["frameID": 1, "presentationTimeSeconds": 0,
                            "red": ["detected": false], "blue": ["detected": false],
                            "manualCaptured": true, "manualFileName": "manual_frame_1.png"]]])
            try snapshotData.write(to: metadataURL)
            DebugBundleTransfer.shared.enqueued.removeAll()
            if nearLimit {
                // 容量境界用の既存セッション使用量を sparse file で再現する。実容量を消費しない。
                let url = directory.appendingPathComponent(sessionID + "_triage_transfer.psbt")
                check(FileManager.default.createFile(atPath: url.path, contents: nil), "create sparse file")
                let handle = try FileHandle(forWritingTo: url)
                try handle.truncate(atOffset: UInt64(DebugRecordingLimits.maximumDiskUsageBytes - 32 * 1024))
                try handle.close()
            }
            check(DebugRecordingStorage.diskUsage(sessionID: sessionID, in: directory)
                  < DebugRecordingLimits.maximumDiskUsageBytes, "recording fits before triage")
            let (bundleURL, errorMessage) = finalizeBundle(directory: directory, currentSessionID: sessionID,
                metadataURL: metadataURL, snapshotData: snapshotData, forensicDirectoryURL: forensic)
            let bundle = directory.appendingPathComponent("phone_saber_triage_" + sessionID)
            check(try Data(contentsOf: metadataURL) == snapshotData, "metadata retained")
            check(try Data(contentsOf: image) == imageBytes, "lossless source retained")
            check(DebugRecordingStorage.diskUsage(sessionID: sessionID, in: directory)
                  <= DebugRecordingLimits.maximumDiskUsageBytes, "session cap retained")
            if nearLimit {
                check(!FileManager.default.fileExists(atPath: bundle.path), "oversize bundle removed")
                check(errorMessage == DebugVideoRecorderError.diskUsageLimitReached.errorDescription,
                      "session cap was the cause of bundle failure")
                check(bundleURL == nil, "deleted bundle must not be offered for sharing")
                check(DebugBundleTransfer.shared.enqueued.isEmpty, "deleted bundle must not be enqueued")
            } else {
                check(bundleURL == bundle && errorMessage == nil, "valid bundle remains shareable")
                check(DebugBundleTransfer.shared.enqueued == [bundle], "valid bundle enqueued once")
                let summary = try JSONSerialization.jsonObject(
                    with: Data(contentsOf: bundle.appendingPathComponent("summary.json"))) as! [String: Any]
                check(summary["selectedImageCount"] as? Int == 1, "manual image copied")
            }
            print(nearLimit ? "over-limit PASS" : "within-limit PASS")
        }
    }
}
'''


if __name__ == "__main__":
    unittest.main()
