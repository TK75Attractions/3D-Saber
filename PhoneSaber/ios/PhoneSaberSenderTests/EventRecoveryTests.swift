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

    func testBackoffExhaustsAfterLongFailureAndCanReset() {
        var retry = RecoveryBackoff()
        var time = 0.0
        while let delay = retry.nextDelay(at: time) {
            XCTAssertGreaterThan(delay, 0)
            XCTAssertLessThanOrEqual(delay, 30)
            time += delay
        }
        XCTAssertEqual(time, 900)
        retry.reset()
        XCTAssertEqual(retry.nextDelay(at: 901), 1)
    }

    func testOneFrameDoesNotResetFailureBudget() {
        var retry = RecoveryBackoff(limit: 20)
        XCTAssertEqual(retry.nextDelay(at: 0), 1)
        retry.receivedFrame(at: 1)
        XCTAssertEqual(retry.nextDelay(at: 2), 2)
        retry.receivedFrame(at: 3)
        XCTAssertNil(retry.nextDelay(at: 20))
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
}
