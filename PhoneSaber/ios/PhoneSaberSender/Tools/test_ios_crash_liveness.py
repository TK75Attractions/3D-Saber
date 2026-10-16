"""実際のカメラ設定・通知登録と overlay 待機を抜き出して実行する、macOS 用回帰テスト。"""
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

    def test_camera_configuration_lock_failure_preserves_default_and_experimental_modes(self):
        camera = (SOURCE / "CameraViewModel.swift").read_text()
        lifecycle = camera[camera.index("enum CameraLifecycleState:"):
                           camera.index("private final class CaptureSessionRunner")]
        start = camera.index("        if let format {", camera.index("    private func configureAndStart("))
        end = camera.index("        output.videoSettings =", start)
        setup = camera[start:end]
        start = camera.index("    private func fail(")
        end = camera.index("    private func resetStartState()", start)
        failure = camera[start:end]
        self.run_swift("import Foundation\n" + lifecycle + FORMAT_FAILURE_HEAD + setup +
                       FORMAT_FAILURE_MIDDLE + failure + FORMAT_FAILURE_TAIL)

    def test_stopped_start_completion_cannot_stop_during_next_configuration(self):
        camera = (SOURCE / "CameraViewModel.swift").read_text()
        lifecycle = camera[camera.index("enum CameraLifecycleState:"):
                           camera.index("private final class CaptureSessionRunner")]
        runner = camera[camera.index("private final class CaptureSessionRunner"):
                        camera.index("// 最新の表示値と累積カウンタ")]
        runner = runner.replace("private final class", "final class", 1)
        start = camera.index("    private func finishCameraStart(")
        end = camera.index("    private func startCameraWatchdog()", start)
        method = camera[start:end].replace("private func finish", "func finish")
        head = START_COMPLETION_HEAD[START_COMPLETION_HEAD.index("@MainActor final class"):]
        head = head.replace("let sessionRunner = SessionRunner()",
                            "let sessionRunner: CaptureSessionRunner\n"
                            " init(session: AVCaptureSession) { sessionRunner = CaptureSessionRunner(session: session) }")
        recovery = (SOURCE / "EventRecovery.swift").read_text()
        self.run_swift(recovery + lifecycle + STOP_COMPLETION_SESSION + runner +
                       STOP_COMPLETION_QUEUE + head + method + STOP_COMPLETION_TAIL)

    def test_stopped_start_completion_finishes_before_next_configuration(self):
        camera = (SOURCE / "CameraViewModel.swift").read_text()
        lifecycle = camera[camera.index("enum CameraLifecycleState:"):
                           camera.index("private final class CaptureSessionRunner")]
        start = camera.index("private final class CaptureSessionRunner")
        end = camera.index("// 最新の表示値", start)
        runner = camera[start:end].replace("private final class", "final class", 1)
        start = camera.index("    private func finishCameraStart(")
        end = camera.index("    private func startCameraWatchdog()", start)
        method = camera[start:end].replace("private func finish", "func finish")
        recovery = (SOURCE / "EventRecovery.swift").read_text()
        self.run_swift(recovery + lifecycle + STOP_CONFIGURATION_HEAD + runner +
                       STOP_CONFIGURATION_MODEL + method + STOP_CONFIGURATION_TAIL)


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

FORMAT_FAILURE_HEAD = r'''
import Darwin
// カメラは使わず、設定ロックの一時的な失敗だけを注入する。
final class Camera {
 struct Range { let minFrameRate = 1.0, maxFrameRate = 120.0 }
 struct Format {
  let isAutoVideoFrameRateSupported = true
  let videoSupportedFrameRateRanges = [Range()]
 }
 var shouldFail = true, isAutoVideoFrameRateEnabled = true
 var activeFormat = Format()
 var activeVideoMinFrameDuration = 1.0 / 60, activeVideoMaxFrameDuration = 1.0 / 60
 var unlocks = 0, restores = 0
 func lockForConfiguration() throws {
  if shouldFail { throw NSError(domain: "CameraLockRegression", code: 1) }
 }
 func unlockForConfiguration() { unlocks += 1 }
}
struct CameraPostProcessingState {
 init(_ camera: Camera) {}
 func restore(_ camera: Camera) { camera.restores += 1 }
}
func configureCameraPostProcessing(_ camera: Camera, experimental: Bool) -> Bool { true }
func fixedFrameDuration(fps: Double, ranges: [ClosedRange<Double>]) -> Double? { 1 / fps }
final class Session {
 var commits = 0
 func commitConfiguration() { commits += 1 }
}
final class CameraViewModel {
 let session = Session(), camera = Camera()
 var cameraLifecycle = CameraLifecycleStateMachine()
 var cameraRecoveryInProgress = false, lowLatencyCameraSettings = false
 var cameraPostProcessingState: CameraPostProcessingState?
 var cameraErrorMessage: String?, errorMessage: String?
 var starts = 0
 func publishCameraLifecycle(at time: TimeInterval) {}
 func recomputeErrorMessage() { errorMessage = cameraErrorMessage }
 func configure(targetFPS: Double) {
  let camera = camera
  let format: Camera.Format? = camera.activeFormat
'''

FORMAT_FAILURE_MIDDLE = r'''
  session.commitConfiguration()
  starts += 1
 }
'''

FORMAT_FAILURE_TAIL = r'''
}
@main struct Audit {
 static func main() {
  var failures = 0
  for (experimental, restoring) in [(false, false), (true, false), (false, true)] {
   for recovery in [false, true] {
    let model = CameraViewModel()
    model.lowLatencyCameraSettings = experimental
    if restoring { model.cameraPostProcessingState = CameraPostProcessingState(model.camera) }
    model.cameraLifecycle.requestStart(at: 0)
    if recovery { _ = model.cameraLifecycle.beginRecovery(at: 1) }
    model.cameraRecoveryInProgress = recovery
    model.configure(targetFPS: 30)
    let requiresRetry = experimental || restoring
    // 通常モードの既存の起動継続と、実験設定・復元中の再試行を区別する。
    let passed = model.starts == (requiresRetry ? 0 : 1) && model.session.commits == 1 &&
     model.cameraLifecycle.state.canRetry == requiresRetry &&
     model.cameraRecoveryInProgress == (requiresRetry ? false : recovery) &&
     model.errorMessage?.hasPrefix("カメラ形式を設定できません:") == true &&
     (model.cameraErrorMessage != nil) == requiresRetry && model.camera.unlocks == 0 &&
     model.camera.restores == 0 &&
     model.camera.activeVideoMinFrameDuration == 1.0 / 60 &&
     model.camera.activeVideoMaxFrameDuration == 1.0 / 60 &&
     (model.cameraPostProcessingState != nil) == restoring
    print("camera lock failure", experimental, restoring, recovery, passed ? "PASS" : "FAIL: changed configuration failure policy")
    if !passed { failures += 1 }
    if requiresRetry {
     guard model.cameraLifecycle.beginRecovery(at: 2) else { failures += 1; continue }
    }
    // 次の設定では要求fps・ロック解放と、保存設定の復元呼出しを確認する。
    model.camera.shouldFail = false
    model.configure(targetFPS: 30)
    if requiresRetry || recovery { model.cameraLifecycle.restartFinished(succeeded: true, at: 3) }
    model.cameraLifecycle.receivedFrame(at: 3.1)
    let recovered = model.starts == (requiresRetry ? 1 : 2) && model.session.commits == 2 &&
     model.camera.activeVideoMinFrameDuration == 1.0 / 30 &&
     model.camera.activeVideoMaxFrameDuration == 1.0 / 30 &&
     model.camera.unlocks == 1 && model.camera.restores == (restoring ? 1 : 0) &&
     model.cameraLifecycle.state == .live &&
     (model.cameraPostProcessingState != nil) == experimental
    print("camera lock subsequent configuration", experimental, restoring, recovery, recovered ? "PASS" : "FAIL")
    if !recovered { failures += 1 }
   }
  }
  exit(failures == 0 ? 0 : 1)
 }
}
'''

START_COMPLETION_HEAD = r'''
import Darwin
final class SessionRunner {
 var stops = 0
 func stop() { stops += 1 }
 func stopSynchronously() { stops += 1 }
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

STOP_COMPLETION_SESSION = r'''
import Darwin
// 実カメラは開かず、AVFoundation が例外にする設定中の stopRunning を記録する。
final class AVCaptureSession: @unchecked Sendable {
 let lock = NSLock()
 var configuring = false
 var stops = 0
 var invalidStops = 0
 var isRunning: Bool { false }
 func startRunning() {}
 func stopRunning() {
  lock.lock(); defer { lock.unlock() }
  stops += 1
  if configuring { invalidStops += 1 }
 }
 func beginConfiguration() { lock.lock(); configuring = true; lock.unlock() }
 func commitConfiguration() { lock.lock(); configuring = false; lock.unlock() }
}
'''

STOP_COMPLETION_QUEUE = r'''
extension CaptureSessionRunner {
 // キューの実行を遅らせ、古い停止要求と次の設定の交差を決定的に再現する。
 func holdForTesting(entered: DispatchSemaphore, release: DispatchSemaphore) {
  queue.async { entered.signal(); release.wait() }
 }
 func drainForTesting() { queue.sync {} }
}
'''

STOP_COMPLETION_TAIL = r'''
}
@main struct Audit {
 @MainActor static func main() {
  let session = AVCaptureSession()
  let model = CameraViewModel(session: session)
  // stop() は起動キューを同期で排出し、セッションを停止済み。
  model.sessionRunner.stopSynchronously()
  model.running = false
  model.lifecycleGeneration = 2
  let entered = DispatchSemaphore(value: 0)
  let release = DispatchSemaphore(value: 0)
  model.sessionRunner.holdForTesting(entered: entered, release: release)
  guard entered.wait(timeout: .now() + 1) == .success else { exit(2) }
  // 非同期停止なら次の設定開始まで待ち、同期停止なら設定前にキューを解放する。
  let configured = DispatchSemaphore(value: 0)
  DispatchQueue.global().async {
   _ = configured.wait(timeout: .now() + 0.1)
   release.signal()
  }
  // MainActor に遅れて届いた、停止前の startRunning の完了通知。
  model.finishCameraStart(succeeded: true, lifecycle: 1, recovery: nil, isRecovery: false)
  // 次の start() の設定区間に、古い通知が積んだ stop を実行させる。
  session.beginConfiguration()
  configured.signal()
  model.sessionRunner.drainForTesting()
  session.commitConfiguration()
  let passed = session.invalidStops == 0 && session.stops == 2
  print("late stopped start: stops=\(session.stops) during-configuration=\(session.invalidStops)")
  exit(passed ? 0 : 1)
 }
}
'''

STOP_CONFIGURATION_HEAD = r'''
import Darwin
// SDK の「設定中の stopRunning は NSGenericException」を記録して検証する。
// 停止の開始を遅らせ、古い完了通知の直後に次の beginConfiguration を挟む。
final class AVCaptureSession: @unchecked Sendable {
 let allowStop = DispatchSemaphore(value: 0)
 let stopped = DispatchSemaphore(value: 0)
 private let lock = NSLock()
 private var configuring = false
 private var overlap = false
 var isRunning: Bool { false }
 func startRunning() {}
 func stopRunning() {
  _ = allowStop.wait(timeout: .now() + 0.1)
  lock.lock()
  overlap = overlap || configuring
  lock.unlock()
  stopped.signal()
 }
 func beginConfiguration() {
  lock.lock(); configuring = true; lock.unlock()
 }
 func commitConfiguration() {
  lock.lock(); configuring = false; lock.unlock()
 }
 var stoppedDuringConfiguration: Bool {
  lock.lock(); defer { lock.unlock() }; return overlap
 }
}
'''

STOP_CONFIGURATION_MODEL = r'''
@MainActor final class CameraViewModel {
 let session: AVCaptureSession
 let sessionRunner: CaptureSessionRunner
 var cameraLifecycle = CameraLifecycleStateMachine()
 var lifecycleGeneration = 2
 var cameraRecoveryGeneration = 0
 var running = false
 var cameraRecoveryInProgress = false
 var cameraErrorMessage: String?
 init(session: AVCaptureSession) {
  self.session = session
  sessionRunner = CaptureSessionRunner(session: session)
 }
 func publishCameraLifecycle(at time: TimeInterval) {}
 func recomputeErrorMessage() {}
'''

STOP_CONFIGURATION_TAIL = r'''
}
@main struct Audit {
 @MainActor static func main() {
  let session = AVCaptureSession()
  let model = CameraViewModel(session: session)
  // stop() が元の startRunning を同期停止済みで、古い完了 Task だけが残る状態。
  model.finishCameraStart(succeeded: true, lifecycle: 1, recovery: nil, isRecovery: false)
  // 直後の start() が MainActor 上で実行する設定区間。
  session.beginConfiguration()
  session.allowStop.signal()
  let completed = session.stopped.wait(timeout: .now() + 1) == .success
  session.commitConfiguration()
  let passed = completed && !session.stoppedDuringConfiguration
  print("stopped start completion before next configuration", passed ? "PASS" : "FAIL: stopRunning overlaps begin/commitConfiguration")
  exit(passed ? 0 : 1)
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
