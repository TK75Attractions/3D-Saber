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
    def test_persistent_fps_configuration_failure_does_not_repeat_restarts(self):
        camera = (SOURCE / "CameraViewModel.swift").read_text()
        health = block(camera, "private func publishDeviceHealth(")
        # 実際の公開処理で再設定要求を数え、設定失敗が続く場合も検証する。
        start = health.index("        let effectiveFPS = effectiveCameraFPS(at: time)")
        fixture = "import Foundation\nimport Darwin\n"
        fixture += block(camera, "enum CameraLifecycleState:") + "\n"
        fixture += block(camera, "struct CameraLifecycleStateMachine") + "\n"
        fixture += THERMAL_RETRY_MODEL + health[start:] + "\n}\n" + THERMAL_RETRY_MAIN
        with tempfile.TemporaryDirectory(prefix="phonesaber-thermal-retry-") as temporary:
            directory = Path(temporary)
            source = directory / "Fixture.swift"
            source.write_text(fixture)
            for flags in ([], ["-D", "DEBUG"]):
                with self.subTest(flags=flags):
                    binary = directory / "fixture"
                    compiled = subprocess.run(
                        ["xcrun", "swiftc", "-O", "-parse-as-library", *flags,
                         "-module-cache-path", str(directory / "cache"),
                         str(SOURCE / "DeviceHealth.swift"), str(source), "-o", str(binary)],
                        capture_output=True, text=True, timeout=90,
                    )
                    self.assertEqual(compiled.returncode, 0, compiled.stderr)
                    run = subprocess.run([str(binary)], capture_output=True, text=True, timeout=10)
                    self.assertEqual(run.returncode, 0, run.stdout + run.stderr)

    def test_stop_at_fps_comparison_expiry_does_not_restart_capture(self):
        camera = (SOURCE / "CameraViewModel.swift").read_text()
        fixture = "import Foundation\nimport Darwin\n"
        for marker in ["enum CameraLifecycleState:", "struct CameraLifecycleStateMachine",
                       "func storedCameraFPS("]:
            fixture += block(camera, marker) + "\n"
        fixture += STOP_COMPARISON_MODEL
        for marker in ["func stop()", "func selectCameraFPS(", "private func restartCameraForFPSChange(",
                       "private func scheduleFPSAlternation("]:
            # 状態機械のstopではなく、ViewModelの実停止処理を使う。
            source = camera[camera.index("final class CameraViewModel:"):]
            fixture += block(source, marker) + "\n"
        # UI集計には触れず、停止が呼ぶ公開処理の先頭にある期限切れ処理を実行する。
        fixture += block(camera, "func publishPendingUI()").split("        refreshConnectivityStatus()", 1)[0]
        fixture += "}\n}\n" + STOP_COMPARISON_MAIN
        with tempfile.TemporaryDirectory(prefix="phonesaber-stop-comparison-") as temporary:
            directory = Path(temporary)
            source = directory / "Fixture.swift"
            source.write_text(fixture)
            for flags in ([], ["-D", "DEBUG"]):
                with self.subTest(flags=flags):
                    binary = directory / "fixture"
                    compiled = subprocess.run(
                        ["xcrun", "swiftc", "-O", "-parse-as-library", *flags,
                         "-module-cache-path", str(directory / "cache"),
                         str(SOURCE / "EventRecovery.swift"), str(source), "-o", str(binary)],
                        capture_output=True, text=True, timeout=90,
                    )
                    self.assertEqual(compiled.returncode, 0, compiled.stderr)
                    run = subprocess.run([str(binary)], capture_output=True, text=True, timeout=10)
                    self.assertEqual(run.returncode, 0, run.stdout + run.stderr)

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


THERMAL_RETRY_MODEL = r'''
final class CameraViewModel {
    var requestedHealthFPS = 30.0, activeCaptureFPS = 60.0
    var running = true, sceneIsActive = true, cameraLifecycleEnabled = true
    var cameraRecoveryInProgress = false, restarts = 0
    var cameraLifecycle = CameraLifecycleStateMachine()
    var targetFPS = 30
    var appliesFPS = false
    func effectiveCameraFPS(at time: TimeInterval) -> Int { targetFPS }
    // 再設定時は要求値だけ更新し、設定ロック失敗時の実設定値を維持する。
    func restartCameraForFPSChange() {
        restarts += 1
        requestedHealthFPS = Double(targetFPS)
        if appliesFPS { activeCaptureFPS = Double(targetFPS) }
    }
    func publishDeviceHealth(at time: TimeInterval) {
'''

THERMAL_RETRY_MAIN = r'''
@main struct Check {
    static func main() {
        var failures = 0
        for actual in [0.0, 30, 60, 120] {
            let model = CameraViewModel()
            model.activeCaptureFPS = actual
            model.cameraLifecycle.requestStart(at: 0)
            model.cameraLifecycle.receivedFrame(at: 0.1)
            // 既に要求したfpsが反映されなくても、liveの送信を毎秒止めない。
            for time in 1...300 { model.publishDeviceHealth(at: Double(time)) }
            let passed = model.restarts == 0
            print("persistent requested=30 actual=\(actual):", passed ? "PASS" : "FAIL: repeated restarts=\(model.restarts)")
            if !passed { failures += 1 }
        }
        for appliesFPS in [false, true] {
            let model = CameraViewModel()
            model.appliesFPS = appliesFPS
            model.requestedHealthFPS = 60
            model.cameraLifecycle.requestStart(at: 0)
            model.cameraLifecycle.receivedFrame(at: 0.1)
            // 発熱→冷却→再発熱では、成否にかかわらず要求変更ごとに一度だけ再設定する。
            for (index, target) in [30, 60, 30].enumerated() {
                model.targetFPS = target
                for time in 1...300 { model.publishDeviceHealth(at: Double(index * 300 + time)) }
                let passed = model.restarts == index + 1
                    && model.requestedHealthFPS == Double(target)
                    && model.activeCaptureFPS == (appliesFPS ? Double(target) : 60)
                print("applies=\(appliesFPS) target=\(target):", passed ? "PASS" : "FAIL: restarts=\(model.restarts)")
                if !passed { failures += 1 }
            }
        }
        // 前面・送信中・live・復旧中でない、という既存の制約を保つ。
        for blocked in 0..<5 {
            let model = CameraViewModel()
            model.cameraLifecycle.requestStart(at: 0)
            if blocked != 0 { model.cameraLifecycle.receivedFrame(at: 0.1) }
            if blocked == 1 { model.running = false }
            if blocked == 2 { model.sceneIsActive = false }
            if blocked == 3 { model.cameraRecoveryInProgress = true }
            if blocked == 4 { model.cameraLifecycleEnabled = false }
            model.requestedHealthFPS = 60
            model.publishDeviceHealth(at: 1)
            if model.restarts != 0 { failures += 1 }
        }
        exit(failures == 0 ? 0 : 1)
    }
}
'''

STOP_COMPARISON_MODEL = r'''
// カメラ・通信・表示は起動せず、再起動要求と停止回数だけを数える。
enum ConnectivityLog {
    static let sendGapMonitor = Monitor()
    struct Monitor {
        func context(_ value: String) {}
        func setIntent(_ value: Bool) {}
        func beginReconfiguration() {}
        func endReconfiguration() {}
    }
}
final class SessionRunner {
    var stops = 0
    func stopSynchronously() { stops += 1 }
}
struct Transport { func stop() {} }
@MainActor final class CameraViewModel {
    static let cameraFPSKey = "PhoneSaber.tests.cameraFPS"
    var running = true, sceneIsActive = true, permissionPending = false
    var automaticResumePending = false, cameraRecoveryInProgress = false
    var cameraLifecycleEnabled = true, debugRecordingActive = false
    var lanConfigured = true, supports120FPS = false, supports60FPS = true
    var cameraFPS = 30, starts = 0, lifecycleGeneration = 1, cameraRecoveryGeneration = 0
    var automaticResumeMessage = "", cameraRecoveryMessage = "", activeDestination = "", status = ""
    var redEndpoints: Int?, blueEndpoints: Int?
    var cameraErrorMessage: String?, connectionErrorMessage: String?
    var senderStates: [Int: String] = [:], senderErrors: [Int: String] = [:], sendErrorMessages: [Int: String] = [:]
    var cameraRecoveryTask: Task<Void, Never>?, cameraWatchdogTask: Task<Void, Never>?
    var resumePolicy = SendingResumePolicy(wasSending: true)
    var cameraLifecycle = CameraLifecycleStateMachine(), cameraBackoff = RecoveryBackoff()
    let sessionRunner = SessionRunner(), sender = Transport(), lanProbe = Transport()
    var fpsAlternationTask: Task<Void, Never>?
    var fpsComparisonPolicy = FPSComparisonPolicy()
    var debugComparisonHighFPS = 60
    var debugAlternateFPS = true { didSet { scheduleFPSAlternation() } }
    func start() { starts += 1; running = true; resumePolicy.start() }
    func persistSendingIntent() {}
    func stopDebugRecording() {}
    func refreshConnectivityStatus() {}
    func publishDeviceHealth(at time: TimeInterval, force: Bool) {}
    func publishCameraLifecycle(at time: TimeInterval) {}
    func updateIdleTimerPolicy() {}
    func resetProcessor() {}
    func recomputeErrorMessage() {}
'''

STOP_COMPARISON_MAIN = r'''
@main struct Check {
    @MainActor static func main() {
        // 実アプリの保存値は読み書きしない。このプロセス内だけの既定値。
        UserDefaults.standard.register(defaults: [CameraViewModel.cameraFPSKey: 60])
        var failures = 0
        for expired in [false, true] {
            let model = CameraViewModel()
            model.cameraLifecycle.requestStart(at: ProcessInfo.processInfo.systemUptime)
            model.fpsComparisonPolicy.setEnabled(true, at: ProcessInfo.processInfo.systemUptime - (expired ? 301 : 0))
            model.stop()
            var passed = model.starts == 0 && model.sessionRunner.stops == 1 && !model.running
                && !model.resumePolicy.wantsSending && model.cameraLifecycle.state == .stopped
#if DEBUG
            passed = passed && model.debugAlternateFPS == !expired && model.cameraFPS == (expired ? 60 : 30)
#endif
            print("stop comparison expired=\(expired):", passed ? "PASS" : "FAIL: starts=\(model.starts), stops=\(model.sessionRunner.stops)")
            if !passed { failures += 1 }
        }
#if DEBUG
        // 送信を続けている通常のUI更新では、期限切れ時に保存値へ戻して再設定する。
        let model = CameraViewModel()
        model.fpsComparisonPolicy.setEnabled(true, at: ProcessInfo.processInfo.systemUptime - 301)
        model.publishPendingUI()
        let passed = model.starts == 1 && model.running && !model.debugAlternateFPS && model.cameraFPS == 60
        print("periodic comparison expiry:", passed ? "PASS" : "FAIL")
        if !passed { failures += 1 }
        model.stop()
#endif
        exit(failures == 0 ? 0 : 1)
    }
}
'''

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
