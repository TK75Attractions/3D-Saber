"""実際の通知登録と overlay 待機を抜き出して実行する、macOS 用回帰テスト。"""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

SOURCE = Path(__file__).resolve().parents[1] / "PhoneSaberSender"


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "Xcode/macOS required")
class IOSCrashLivenessTests(unittest.TestCase):
    def run_swift(self, source):
        for flags in ([], ["-D", "DEBUG"]):
            with self.subTest(flags=flags), tempfile.TemporaryDirectory(
                prefix="phonesaber-liveness-", dir=os.environ.get("TMPDIR")
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

    def test_camera_notifications_do_not_wait_for_main_queue(self):
        camera = (SOURCE / "CameraViewModel.swift").read_text()
        start = camera.index("    private func registerCameraSessionObservers()")
        end = camera.index("    func sceneDidChange", start)
        method = camera[start:end].replace("private func register", "func register")
        # iOS 専用定数を文字列へ置き換える。配信キューと Task の本体は元コードのまま。
        method = method.replace("AVCaptureSessionInterruptionReasonKey",
                                '"AVCaptureSessionInterruptionReasonKey"')
        self.run_swift(NOTIFICATION_HEAD + method + NOTIFICATION_TAIL)

    def test_late_initial_camera_start_keeps_newer_lifecycle_events(self):
        camera = (SOURCE / "CameraViewModel.swift").read_text()
        lifecycle = camera[camera.index("enum CameraLifecycleState:"):
                           camera.index("private final class CaptureSessionRunner")]
        start = camera.index("    private func finishCameraStart(")
        end = camera.index("    private func startCameraWatchdog()", start)
        method = camera[start:end].replace("private func finish", "func finish")
        recovery = (SOURCE / "EventRecovery.swift").read_text()
        self.run_swift(recovery + lifecycle + START_COMPLETION_HEAD + method + START_COMPLETION_TAIL)

    def test_terminal_overlay_writer_does_not_wait_forever(self):
        recorder = (SOURCE / "DebugVideoRecorder.swift").read_text()
        start = recorder.index("            while !input.isReadyForMoreMediaData {")
        end = recorder.index("            let presentationTime", start)
        self.run_swift(OVERLAY_HEAD + recorder[start:end] + OVERLAY_TAIL)


NOTIFICATION_HEAD = r'''import Foundation
import Darwin
import AVFoundation
@MainActor final class CameraViewModel {
 let session = AVCaptureSession()
 var sessionObserverTokens: [NSObjectProtocol] = []
 // 通知元のキューから読む世代（本体は CameraUIPendingState。停止中は nil）。
 final class Pending: @unchecked Sendable { func cameraNotificationGeneration() -> Int? { 1 } }
 nonisolated let uiPending = Pending()
 var lifecycleGeneration = 1
 func handleCameraInterruption(reason: String) {}
 func handleCameraInterruptionEnded() {}
 func handleCameraRuntimeError(_ message: String) {}
'''

NOTIFICATION_TAIL = r'''
}
@main struct Audit {
 @MainActor static func main() {
  let model = CameraViewModel()
  model.registerCameraSessionObservers()
  var failures = 0
  for name in [AVCaptureSession.wasInterruptedNotification, AVCaptureSession.interruptionEndedNotification, AVCaptureSession.runtimeErrorNotification] {
   let done = DispatchSemaphore(value: 0)
   let session = model.session
   DispatchQueue.global().async {
    NotificationCenter.default.post(name: name, object: session)
    done.signal()
   }
   let passed = done.wait(timeout: .now() + 0.25) == .success
   if !passed { failures += 1 }
   print(name.rawValue, passed ? "PASS" : "FAIL: poster waits for main")
  }
  model.sessionObserverTokens.forEach(NotificationCenter.default.removeObserver)
  exit(failures == 0 ? 0 : 1)
 }
}
'''

START_COMPLETION_HEAD = r'''
import Darwin
final class SessionRunner {
 var stops = 0
 func stop() { stops += 1 }
}
@MainActor final class CameraViewModel {
 let sessionRunner = SessionRunner()
 var cameraLifecycle = CameraLifecycleStateMachine()
 var lifecycleGeneration = 1
 var cameraRecoveryGeneration = 1
 var running = true
 var cameraRecoveryInProgress = true
 var cameraErrorMessage: String? = "latest error"
 func publishCameraLifecycle(at time: TimeInterval) {}
 func recomputeErrorMessage() {}
'''

START_COMPLETION_TAIL = r'''
}
@main struct Audit {
 @MainActor static func main() {
  var failures = 0
  for event in 0...2 {
   for succeeded in [false, true] {
    let model = CameraViewModel()
    model.cameraLifecycle.requestStart(at: 0)
    switch event {
    case 0: model.cameraLifecycle.interruptionBegan(reason: "camera in use")
    case 1: model.cameraLifecycle.runtimeError("media services reset")
    default: _ = model.cameraLifecycle.beginRecovery(at: 2)
    }
    let expected = model.cameraLifecycle.state
    model.finishCameraStart(succeeded: succeeded, lifecycle: 1, recovery: nil, isRecovery: false)
    let passed = model.cameraLifecycle.state == expected && model.cameraErrorMessage == "latest error" &&
     model.cameraRecoveryInProgress && model.sessionRunner.stops == 0
    print("late initial start", event, succeeded, passed ? "PASS" : "FAIL: newer lifecycle event overwritten")
    if !passed { failures += 1 }
    if event == 2 {
     model.finishCameraStart(succeeded: false, lifecycle: 1, recovery: 1, isRecovery: true)
     if model.cameraLifecycle.state != .failed("capture sessionを再開できませんでした") ||
        model.cameraRecoveryInProgress { failures += 1 }
    }
   }
  }
  exit(failures == 0 ? 0 : 1)
 }
}
'''

OVERLAY_HEAD = r'''import AVFoundation
import Foundation
import Darwin
enum DebugVideoRecorderError: Error { case appendFailed(String) }
final class TerminalWriter: AVAssetWriter {
 var terminalStatus: AVAssetWriter.Status = .failed
 override var status: AVAssetWriter.Status { terminalStatus }
 override var error: Error? { NSError(domain: "overlay-regression", code: 28) }
}
final class BlockedInput: AVAssetWriterInput {
 override var isReadyForMoreMediaData: Bool { false }
}
func wait(input: AVAssetWriterInput, writer: AVAssetWriter, reader: AVAssetReader) async throws {
'''

OVERLAY_TAIL = r'''
}
@main struct Audit {
 static func main() throws {
  var failures = 0
  for status in [AVAssetWriter.Status.failed, .cancelled] {
   let writer = try TerminalWriter(outputURL: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".mp4"), fileType: .mp4)
   writer.terminalStatus = status
   let input = BlockedInput(mediaType: .video, outputSettings: nil)
   let reader = try AVAssetReader(asset: AVMutableComposition())
   let done = DispatchSemaphore(value: 0)
   let task = Task.detached {
    do {
     try await wait(input: input, writer: writer, reader: reader)
    } catch DebugVideoRecorderError.appendFailed {
     done.signal()
    } catch {}
   }
   let passed = done.wait(timeout: .now() + 0.5) == .success
   print("terminal writer",status.rawValue,passed ? "PASS" : "FAIL: wait never reports failure")
   if !passed { failures += 1 }
   task.cancel()
  }
  exit(failures == 0 ? 0 : 1)
 }
}
'''

if __name__ == "__main__":
    unittest.main()
