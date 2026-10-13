"""実ソースの overlay 待機を実行する、GUI 不要の macOS 回帰テスト。"""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


SOURCE = Path(__file__).resolve().parents[1] / "PhoneSaberSender"


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "macOS/Swift required")
class IOSOverlayLivenessTests(unittest.TestCase):
    def test_overlay_backpressure_exits_on_terminal_writer(self):
        recorder = (SOURCE / "DebugVideoRecorder.swift").read_text()
        start = recorder.index("            while !input.isReadyForMoreMediaData {")
        end = recorder.index("            let presentationTime", start)
        # 境界が変わったら失敗させる。待機の本体は再実装せず、そのままコンパイルする。
        self.assertEqual(recorder.count("            while !input.isReadyForMoreMediaData {"), 1)
        source = HEAD + recorder[start:end] + TAIL
        for flags in ([], ["-D", "DEBUG"]):
            with self.subTest(flags=flags), tempfile.TemporaryDirectory(
                prefix="phonesaber-overlay-", dir=os.environ.get("TMPDIR")
            ) as temporary:
                directory = Path(temporary)
                fixture = directory / "Fixture.swift"
                fixture.write_text(source)
                binary = directory / "fixture"
                compile_result = subprocess.run(
                    ["xcrun", "swiftc", "-O", "-parse-as-library", *flags,
                     "-module-cache-path", str(directory / "module-cache"),
                     str(fixture), "-o", str(binary)],
                    capture_output=True, text=True, timeout=90,
                )
                self.assertEqual(compile_result.returncode, 0, compile_result.stderr)
                run = subprocess.run([str(binary)], capture_output=True, text=True, timeout=10)
                self.assertEqual(run.returncode, 0, run.stdout + run.stderr)


HEAD = r'''import AVFoundation
import Foundation
import Darwin
enum DebugVideoRecorderError: Error { case appendFailed(String) }
final class TestWriter: AVAssetWriter {
    var injectedStatus: AVAssetWriter.Status = .writing
    var failAfterChecks: Int?
    var statusChecks = 0
    var cancellations = 0
    override var status: AVAssetWriter.Status {
        statusChecks += 1
        if let failAfterChecks, statusChecks > failAfterChecks { return .failed }
        return injectedStatus
    }
    override var error: Error? {
        NSError(domain: "overlay-regression", code: 28,
                userInfo: [NSLocalizedDescriptionKey: "injected write failure"])
    }
    override func cancelWriting() { cancellations += 1 }
}
final class TestInput: AVAssetWriterInput {
    var readsBeforeReady: Int?
    override var isReadyForMoreMediaData: Bool {
        guard let remaining = readsBeforeReady else { return false }
        readsBeforeReady = max(0, remaining - 1)
        return remaining == 0
    }
}
final class TestReader: AVAssetReader {
    var cancellations = 0
    override func cancelReading() { cancellations += 1 }
}
func wait(input: AVAssetWriterInput, writer: AVAssetWriter, reader: AVAssetReader) async throws {
'''

TAIL = r'''
}
@main struct Check {
    static func main() throws {
        var failures = 0
        // 終端で待機に入る場合、待機中に失敗する場合、正常に ready へ戻る場合を分ける。
        for scenario in ["failed", "cancelled", "writing-to-failed", "ready", "backpressure-to-ready"] {
            let writer = try TestWriter(outputURL: FileManager.default.temporaryDirectory
                .appendingPathComponent(UUID().uuidString + ".mp4"), fileType: .mp4)
            let input = TestInput(mediaType: .video, outputSettings: nil)
            let reader = try TestReader(asset: AVMutableComposition())
            let shouldFail = !scenario.hasSuffix("ready")
            switch scenario {
            case "failed": writer.injectedStatus = .failed
            case "cancelled": writer.injectedStatus = .cancelled
            case "writing-to-failed": writer.failAfterChecks = 2
            case "ready": input.readsBeforeReady = 0
            default: input.readsBeforeReady = 3
            }
            let done = DispatchSemaphore(value: 0)
            var correctOutcome = false
            let task = Task.detached {
                do {
                    try await wait(input: input, writer: writer, reader: reader)
                    correctOutcome = !shouldFail
                } catch DebugVideoRecorderError.appendFailed(let message) {
                    correctOutcome = shouldFail && message == "injected write failure"
                } catch {}
                done.signal()
            }
            let returned = done.wait(timeout: .now() + 0.5) == .success
            // タイムアウト時は short-circuit し、実行中 Task の状態を読まない。
            let passed = returned && correctOutcome
                && reader.cancellations == (shouldFail ? 1 : 0)
                && writer.cancellations == (shouldFail ? 1 : 0)
            print(scenario, passed ? "PASS" : "FAIL: timeout, wrong result or cleanup")
            if !passed { failures += 1 }
            task.cancel()
        }
        exit(failures == 0 ? 0 : 1)
    }
}
'''


if __name__ == "__main__":
    unittest.main()
