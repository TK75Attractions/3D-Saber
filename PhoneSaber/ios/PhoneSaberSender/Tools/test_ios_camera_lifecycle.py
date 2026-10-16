"""遅れた起動完了と再設定の競合を実ソース・直列キューで検証する。GUI不要。"""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


SOURCE = Path(__file__).resolve().parents[1] / "PhoneSaberSender"


def block(source, marker):
    start = source.index(marker)
    end = source.index("{", start) + 1
    depth = 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "macOS/Swift required")
class IOSCameraLifecycleTests(unittest.TestCase):
    def test_late_start_completion_finishes_stop_before_next_configuration(self):
        camera = (SOURCE / "CameraViewModel.swift").read_text()
        # 完了処理・sessionキュー・状態遷移を再実装せず、そのままコンパイルする。
        fixture = HEAD
        for marker in ["enum CameraLifecycleState:", "struct CameraLifecycleStateMachine",
                       "private final class CaptureSessionRunner:"]:
            fixture += block(camera, marker) + "\n"
        fixture += MODEL + block(camera, "private func finishCameraStart(") + "\n}\n" + MAIN
        with tempfile.TemporaryDirectory(prefix="phonesaber-camera-stop-") as temporary:
            directory = Path(temporary)
            source = directory / "Fixture.swift"
            source.write_text(fixture)
            for flags in ([], ["-D", "DEBUG"]):
                with self.subTest(flags=flags):
                    binary = directory / "fixture"
                    compile_result = subprocess.run(
                        ["xcrun", "swiftc", "-O", "-parse-as-library", *flags,
                         "-module-cache-path", str(directory / "cache"),
                         str(SOURCE / "EventRecovery.swift"), str(source), "-o", str(binary)],
                        capture_output=True, text=True, timeout=90,
                    )
                    self.assertEqual(compile_result.returncode, 0, compile_result.stderr)
                    run = subprocess.run([str(binary)], capture_output=True, text=True, timeout=10)
                    self.assertEqual(run.returncode, 0, run.stdout + run.stderr)


HEAD = r'''import Foundation
import Darwin

// AVFoundationの制約を検査する。カメラやアプリは起動しない。
final class AVCaptureSession: @unchecked Sendable {
    private let lock = NSLock()
    private var configuring = false
    private var overlapped = false
    private var stops = 0
    let stopEntered = DispatchSemaphore(value: 0)
    let configurationBegan = DispatchSemaphore(value: 0)
    let releaseStop = DispatchSemaphore(value: 0)
    let stopFinished = DispatchSemaphore(value: 0)
    var isRunning = false
    func startRunning() { isRunning = true }
    func beginConfiguration() {
        lock.lock(); configuring = true; lock.unlock()
        configurationBegan.signal()
    }
    func commitConfiguration() { lock.lock(); configuring = false; lock.unlock() }
    func stopRunning() {
        stopEntered.signal()
        precondition(releaseStop.wait(timeout: .now() + 3) == .success)
        lock.lock()
        overlapped = overlapped || configuring
        stops += 1
        lock.unlock()
        stopFinished.signal()
    }
    var result: (overlapped: Bool, stops: Int) {
        lock.lock(); defer { lock.unlock() }
        return (overlapped, stops)
    }
}
'''

MODEL = r'''
@MainActor final class CameraViewModel {
    let session = AVCaptureSession()
    private lazy var sessionRunner = CaptureSessionRunner(session: session)
    var lifecycleGeneration = 2, cameraRecoveryGeneration = 0
    var running = false, cameraRecoveryInProgress = false
    var cameraLifecycle = CameraLifecycleStateMachine()
    var cameraErrorMessage: String?
    func publishCameraLifecycle(at time: TimeInterval) {}
    func recomputeErrorMessage() {}
    func complete(lifecycle: Int) {
        finishCameraStart(succeeded: true, lifecycle: lifecycle, recovery: nil, isRecovery: false)
    }
'''

MAIN = r'''
@main struct Check {
    @MainActor static func main() {
        var failures = 0
        // stop()後、古いstart完了→直後のstart()のbeginConfigurationという順序。
        for lifecycle in [1, 2] {
            let model = CameraViewModel()
            let session = model.session
            DispatchQueue.global().async {
                precondition(session.stopEntered.wait(timeout: .now() + 3) == .success)
                // 非同期stopなら再設定の開始を待つ。同期stopなら、戻る前に解放する。
                _ = session.configurationBegan.wait(timeout: .now() + 0.1)
                session.releaseStop.signal()
            }
            model.complete(lifecycle: lifecycle)
            session.beginConfiguration()
            precondition(session.stopFinished.wait(timeout: .now() + 3) == .success)
            session.commitConfiguration()
            let result = session.result
            let passed = result.stops == 1 && !result.overlapped
            print("stopped completion lifecycle=\(lifecycle):", passed ? "PASS" : "FAIL: stopRunning overlapped beginConfiguration")
            if !passed { failures += 1 }
        }
        for lifecycle in [1, 2] {
            let model = CameraViewModel()
            model.running = true
            model.cameraLifecycle.requestStart(at: 0)
            model.complete(lifecycle: lifecycle)
            // 旧runの完了は無視し、現runの成功は適用する。いずれもstopしない。
            let passed = model.session.stopEntered.wait(timeout: .now() + 0.1) == .timedOut
                && model.cameraLifecycle.state == .starting
            print("running completion lifecycle=\(lifecycle):", passed ? "PASS" : "FAIL")
            if !passed { failures += 1 }
        }
        exit(failures == 0 ? 0 : 1)
    }
}
'''


if __name__ == "__main__":
    unittest.main()
