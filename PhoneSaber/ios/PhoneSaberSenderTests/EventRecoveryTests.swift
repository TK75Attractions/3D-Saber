import XCTest
#if !EVENT_RECOVERY_STANDALONE
@testable import PhoneSaberSender
#endif

final class EventRecoveryTests: XCTestCase {
    func testDefaultStartupAndOptIn() {
        var manual = SendingResumePolicy()
        XCTAssertFalse(manual.foreground(autoStart: false))
        var automatic = SendingResumePolicy()
        XCTAssertTrue(automatic.foreground(autoStart: true))
    }

    func testForegroundAndRelaunchRetainSendingIntent() {
        var policy = SendingResumePolicy()
        policy.start()
        XCTAssertTrue(policy.foreground(autoStart: false))
        var relaunched = SendingResumePolicy(wasSending: policy.wantsSending)
        XCTAssertTrue(relaunched.foreground(autoStart: false))
        // 権限・カメラ・通信待ちでも送信の意思は残る。
        XCTAssertTrue(relaunched.foreground(autoStart: false))
    }

    func testStopPreventsForegroundRestartEvenWithAutoStart() {
        var policy = SendingResumePolicy()
        XCTAssertTrue(policy.foreground(autoStart: true))
        policy.stop()
        XCTAssertFalse(policy.wantsSending)
        XCTAssertFalse(policy.foreground(autoStart: true))
        var relaunched = SendingResumePolicy(wasSending: policy.wantsSending)
        XCTAssertFalse(relaunched.foreground(autoStart: false))
        var optedInRelaunch = SendingResumePolicy(wasSending: policy.wantsSending)
        XCTAssertTrue(optedInRelaunch.foreground(autoStart: true))
    }

    func testBackoffCapsAtThirtySeconds() {
        var retry = RecoveryBackoff()
        var time = 100.0
        for expected in [1.0, 2, 4, 8, 16, 30, 30, 30] {
            XCTAssertEqual(retry.nextDelay(at: time), expected)
            time += expected
        }
    }

    func testBackoffKeepsRetryingEveryMinuteAfterLongFailureAndCanReset() {
        var retry = RecoveryBackoff()
        var time = 0.0
        while time < 900 {
            let delay = retry.nextDelay(at: time)!
            XCTAssertGreaterThan(delay, 0)
            XCTAssertLessThanOrEqual(delay, 30)
            time += delay
        }
        XCTAssertEqual(time, 900)
        let lateDelays = (0...465).map { retry.nextDelay(at: 900 + Double($0) * 60) }
        XCTAssertEqual(lateDelays, Array(repeating: 60, count: 466))
        retry.reset()
        XCTAssertEqual(retry.nextDelay(at: 901), 1)
    }

    func testOneFrameDoesNotResetFailureBudget() {
        var retry = RecoveryBackoff(limit: 20)
        XCTAssertEqual(retry.nextDelay(at: 0), 1)
        retry.receivedFrame(at: 1)
        XCTAssertEqual(retry.nextDelay(at: 2), 2)
        retry.receivedFrame(at: 3)
        XCTAssertEqual(retry.nextDelay(at: 20), 60)
    }

    func testExhaustedCameraStaysRetryableOnForegroundReturn() {
        var camera = CameraLifecycleStateMachine()
        camera.requestStart(at: 0)
        camera.runtimeError("復旧を待っています")
        XCTAssertFalse(camera.setForeground(false, at: 900))
        XCTAssertFalse(camera.setForeground(true, at: 910, allowRecovery: false))
        XCTAssertTrue(camera.state.canRetry)
        XCTAssertTrue(camera.beginRecovery(at: 911))
        XCTAssertEqual(camera.state, .recovering)
    }

    // 停止→即再開（fps 切替を含む）で、前回 start の遅い完了通知が新しい run の session を止めない。
    func testStaleCameraStartCompletionNeverStopsANewerRun() {
        XCTAssertEqual(CameraStartCompletionPolicy.action(completedLifecycle: 4, currentLifecycle: 4, running: true),
                       .apply)
        XCTAssertEqual(CameraStartCompletionPolicy.action(completedLifecycle: 3, currentLifecycle: 5, running: true),
                       .ignore, "新しい run が startRunning 済み。ここで止めると新しい run のカメラが止まる")
        XCTAssertEqual(CameraStartCompletionPolicy.action(completedLifecycle: 3, currentLifecycle: 4, running: false),
                       .stopSession, "送信停止中に完了した start は止める")
        XCTAssertEqual(CameraStartCompletionPolicy.action(completedLifecycle: 4, currentLifecycle: 4, running: false),
                       .stopSession)
    }

    func testStableFramesResetBackoff() {
        var retry = RecoveryBackoff()
        _ = retry.nextDelay(at: 0)
        _ = retry.nextDelay(at: 1)
        for time in 3...13 { retry.receivedFrame(at: Double(time)) }
        XCTAssertEqual(retry.attempts, 0)
        XCTAssertEqual(retry.nextDelay(at: 14), 1)
    }

    func testRecoveryStopCompletionDefersUntilForegroundAndSending() {
        XCTAssertTrue(CameraRecoveryCompletionPolicy.canConfigure(sending: true, foreground: true))
        XCTAssertFalse(CameraRecoveryCompletionPolicy.canConfigure(sending: true, foreground: false))
        XCTAssertFalse(CameraRecoveryCompletionPolicy.canConfigure(sending: false, foreground: true))
        var camera = CameraLifecycleStateMachine()
        camera.requestStart(at: 0)
        XCTAssertTrue(camera.beginRecovery(at: 2))
        _ = camera.setForeground(false, at: 3)
        // stop完了で背面なら失敗状態に戻す。前面復帰で改めて復旧に入れる。
        camera.runtimeError("前面への復帰を待っています")
        XCTAssertTrue(camera.setForeground(true, at: 4))
        XCTAssertEqual(camera.state, .recovering)
    }

    func testFPSComparisonSupports60Versus120AndFallsBackOnUnsupportedDevices() {
        XCTAssertEqual(FPSComparisonPolicy.nextFPS(current: 60, highFPS: 120, supports120: true), 120)
        XCTAssertEqual(FPSComparisonPolicy.nextFPS(current: 120, highFPS: 120, supports120: true), 60)
        XCTAssertEqual(FPSComparisonPolicy.nextFPS(current: 30, highFPS: 120, supports120: true), 60)
        XCTAssertEqual(FPSComparisonPolicy.nextFPS(current: 30, highFPS: 120, supports120: false), 60)
        XCTAssertEqual(FPSComparisonPolicy.nextFPS(current: 60, highFPS: 120, supports120: false), 30)
        XCTAssertEqual(FPSComparisonPolicy.nextFPS(current: 60, highFPS: 60, supports120: true), 30)
    }

    func testFPSComparisonExpiresAfterFiveMinutesEvenWhenNotSending() {
        var policy = FPSComparisonPolicy()
        XCTAssertFalse(policy.shouldDisable(at: 1000))
        policy.setEnabled(true, at: 100)
        XCTAssertFalse(policy.shouldDisable(at: 399.999))
        XCTAssertTrue(policy.shouldDisable(at: 400))
        XCTAssertTrue(policy.shouldDisable(at: 8 * 3600))
        policy.setEnabled(false, at: 400)
        XCTAssertFalse(policy.shouldDisable(at: 1000))
        policy.setEnabled(true, at: 1000)
        XCTAssertFalse(policy.shouldDisable(at: 1299))
        XCTAssertTrue(policy.shouldDisable(at: 1300))
    }

#if !EVENT_RECOVERY_STANDALONE
    @MainActor
    func testFPSRestoreInBackgroundDefersCaptureRestart() {
        var permissionRequests = 0
        let model = CameraViewModel(sender: UDPSender { _, _, _ in }, p2pEnabled: false,
                                    authorizationStatus: { .notDetermined },
                                    requestAccess: { _ in permissionRequests += 1 })
        model.selectCameraFPS(60, persist: false)
        model.startForTesting()
        model.sceneDidChange(isActive: false)
        // 自動比較の期限切れで保存値を戻す場合と同じ呼び出し。
        model.selectCameraFPS(30, persist: false)
        XCTAssertEqual(model.cameraFPS, 30)
        XCTAssertTrue(model.running)
        XCTAssertEqual(permissionRequests, 0)
        model.stop()
    }
#endif

}
