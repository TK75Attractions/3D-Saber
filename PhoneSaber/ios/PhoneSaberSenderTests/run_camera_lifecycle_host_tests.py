#!/usr/bin/env python3
"""GUI不要で実通知ハンドラとXCTestを実行。カメラ起動・通信・UIだけstub。"""
import argparse
from pathlib import Path
import subprocess
import tempfile

from run_latency_host_tests import ROOT, SOURCE, TESTS, block


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--revision", help="指定commitの実ソースで回帰を実行")
    args = parser.parse_args()
    camera = (subprocess.check_output(
        ["git", "show", f"{args.revision}:{(SOURCE / 'CameraViewModel.swift').relative_to(ROOT)}"],
        cwd=ROOT, text=True) if args.revision else (SOURCE / "CameraViewModel.swift").read_text())
    support = '''import Foundation
import QuartzCore
// macOS にない iOS 通知名だけを代用。通知の配送と MainActor Task は本物。
final class AVCaptureSession: NSObject {
    static let wasInterruptedNotification = Notification.Name("camera.interrupted")
    static let interruptionEndedNotification = Notification.Name("camera.interruptionEnded")
    static let runtimeErrorNotification = Notification.Name("camera.runtimeError")
}
let AVCaptureSessionInterruptionReasonKey = "reason"
let AVCaptureSessionErrorKey = "error"
struct PixelPoint { let x: Int; let y: Int }
enum SaberColor { case red, blue }
struct DetectedSaber {
    let endpoints: (PixelPoint, PixelPoint)
    let color: SaberColor
    let isFresh: Bool
    let isPredicted: Bool
}
struct FrameTrace {}
struct UDPSender { init(_ send: (String, Int, (Bool) -> Void) -> Void) {} }
enum ConnectivityLog {
    static let sendGapMonitor = Monitor()
    struct Monitor { func context(_ text: String) {} }
}
'''
    for marker in ["enum CameraLifecycleState:", "struct CameraLifecycleStateMachine",
                   "final class CameraUIPendingState"]:
        support += block(camera, marker) + "\n"
    support += '''
@MainActor final class CameraViewModel {
    let session = AVCaptureSession()
    nonisolated let uiPending = CameraUIPendingState()
    var sessionObserverTokens: [NSObjectProtocol] = []
    var lifecycleGeneration = 0
    var running = false, sceneIsActive = true, debugRecordingActive = false
    var cameraRecoveryInProgress = false
    var cameraLifecycle = CameraLifecycleStateMachine()
    var cameraState = CameraLifecycleState.stopped
    var lastCameraFrameAge: TimeInterval?
    var cameraErrorMessage: String?, errorMessage: String?
    init(sender: UDPSender, p2pEnabled: Bool, idleTimerUpdater: (Bool) -> Void) {
        registerCameraSessionObservers()
    }
    // 実機の startRunning/stopRunning を呼ばず、実装と同じ世代・受理状態を設定する。
    func startForTesting() {
        lifecycleGeneration += 1
        running = true
        uiPending.setAcceptance(running: true, processor: 0, lifecycle: lifecycleGeneration)
        cameraLifecycle.requestStart(at: ProcessInfo.processInfo.systemUptime)
        publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
    }
    func stop() {
        lifecycleGeneration += 1
        running = false
        uiPending.setAcceptance(running: false, processor: 0, lifecycle: lifecycleGeneration)
        cameraLifecycle.stop()
        publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
        cameraErrorMessage = nil
        recomputeErrorMessage()
    }
    func sceneDidChange(isActive: Bool) {
        sceneIsActive = isActive
        _ = cameraLifecycle.setForeground(isActive, at: ProcessInfo.processInfo.systemUptime)
    }
    enum FinishReason { case interruption, runtimeFailure }
    func stopDebugRecording(reason: FinishReason) {}
    func scheduleCameraRecovery(alreadyMarkedRecovering: Bool) {}
    func recomputeErrorMessage() { errorMessage = cameraErrorMessage }
    func assignIfChanged<Value: Equatable>(_ path: ReferenceWritableKeyPath<CameraViewModel, Value>,
                                          _ value: Value) { self[keyPath: path] = value }
    deinit { sessionObserverTokens.forEach(NotificationCenter.default.removeObserver) }
'''
    for marker in ["private func registerCameraSessionObservers(",
                   "private func handleCameraInterruption(", "private func handleCameraInterruptionEnded(",
                   "private func handleCameraRuntimeError(", "private func publishCameraLifecycle("]:
        support += block(camera, marker) + "\n"
    support += "}\n"
    tests = (TESTS / "EventRecoveryTests.swift").read_text()
    focused = "import XCTest\nfinal class CameraNotificationTests: XCTestCase {\n"
    for name in ["testQueuedCameraInterruptionCannotInterruptRestartedRun",
                 "testQueuedCameraRuntimeErrorCannotFailRestartedRun",
                 "testCurrentCameraSessionNotificationsStillApplyAfterRepeatedRestarts"]:
        focused += "@MainActor\n" + block(tests, "func " + name + "(") + "\n"
    focused += "}\n"
    developer = Path(subprocess.check_output(["xcode-select", "-p"], text=True).strip()) / "Platforms/MacOSX.platform/Developer"
    frameworks = developer / "Library/Frameworks"
    with tempfile.TemporaryDirectory(prefix="phonesaber-camera-lifecycle-") as directory:
        tmp = Path(directory)
        (tmp / "Support.swift").write_text(support)
        (tmp / "Tests.swift").write_text(focused)
        (tmp / "main.swift").write_text('''import XCTest
import Darwin
let suite = XCTestSuite(name: "Camera lifecycle host tests")
suite.addTest(CameraNotificationTests.defaultTestSuite)
suite.addTest(EventRecoveryTests.defaultTestSuite)
suite.run()
guard let run = suite.testRun, run.executionCount >= 15, run.hasSucceeded else { exit(1) }
''')
        subprocess.run(["xcrun", "swiftc", "-O", "-D", "EVENT_RECOVERY_STANDALONE",
                        "-module-cache-path", str(tmp / "cache"), str(SOURCE / "EventRecovery.swift"),
                        str(TESTS / "EventRecoveryTests.swift"),
                        str(tmp / "Support.swift"), str(tmp / "Tests.swift"), str(tmp / "main.swift"),
                        "-I", str(developer / "usr/lib"), "-L", str(developer / "usr/lib"),
                        "-F", str(frameworks), "-Xlinker", "-rpath", "-Xlinker", str(frameworks),
                        "-Xlinker", "-rpath", "-Xlinker", str(developer / "usr/lib"),
                        "-o", str(tmp / "tests")], check=True)
        return subprocess.run([str(tmp / "tests")], timeout=30).returncode


if __name__ == "__main__":
    raise SystemExit(main())
