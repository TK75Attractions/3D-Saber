import Foundation
import XCTest
#if !LOW_HEAT_DISPLAY_STANDALONE
import UIKit
@testable import PhoneSaberSender
#endif

/// 低発熱表示の状態機械。UIKit なしで host test（run_low_heat_display_host_tests.py）でも動く。
final class LowHeatDisplayPolicyTests: XCTestCase {
    private func sendingPolicy(at time: TimeInterval = 100) -> LowHeatDisplayPolicy {
        var policy = LowHeatDisplayPolicy(enabled: true, now: 0)
        XCTAssertEqual(policy.update(sending: true, foreground: true, inhibited: false, at: time), .none)
        return policy
    }

    func testDimsOnlyAfterTwentySecondsOfSendingWithoutTouch() {
        var policy = sendingPolicy()
        XCTAssertEqual(LowHeatDisplayPolicy.idleSeconds, 20)
        XCTAssertEqual(policy.tick(at: 119.9), .none)
        XCTAssertFalse(policy.dimmed)
        XCTAssertEqual(policy.tick(at: 120), .dim)
        XCTAssertTrue(policy.dimmed)
        // 暗いままの tick では何度も暗くしない。
        XCTAssertEqual(policy.tick(at: 121), .none)
        XCTAssertEqual(policy.tick(at: 500), .none)
    }

    func testTouchRestoresAndRestartsTheIdleCount() {
        var policy = sendingPolicy()
        XCTAssertEqual(policy.touch(at: 110), .none)
        XCTAssertEqual(policy.tick(at: 129.9), .none)
        XCTAssertEqual(policy.tick(at: 130), .dim)
        XCTAssertEqual(policy.touch(at: 140), .restore)
        XCTAssertFalse(policy.dimmed)
        XCTAssertEqual(policy.touch(at: 141), .none)
        XCTAssertEqual(policy.tick(at: 160.9), .none)
        XCTAssertEqual(policy.tick(at: 161), .dim)
    }

    func testNeverDimsWhileNotSendingAndCountsFromSendStart() {
        var policy = LowHeatDisplayPolicy(enabled: true, now: 0)
        XCTAssertEqual(policy.tick(at: 1000), .none)
        // 起動から長く経っていても、送信開始から20秒を数える。
        XCTAssertEqual(policy.update(sending: true, foreground: true, inhibited: false, at: 1000), .none)
        XCTAssertEqual(policy.tick(at: 1019), .none)
        XCTAssertEqual(policy.tick(at: 1020), .dim)
    }

    func testStopRestoresImmediately() {
        var policy = sendingPolicy()
        XCTAssertEqual(policy.tick(at: 130), .dim)
        XCTAssertEqual(policy.update(sending: false, foreground: true, inhibited: false, at: 131), .restore)
        XCTAssertEqual(policy.tick(at: 1000), .none)
    }

    func testLeavingForegroundRestoresAndReturningRestartsTheCount() {
        var policy = sendingPolicy()
        XCTAssertEqual(policy.tick(at: 130), .dim)
        XCTAssertEqual(policy.update(sending: true, foreground: false, inhibited: false, at: 131), .restore)
        XCTAssertEqual(policy.tick(at: 200), .none)
        XCTAssertEqual(policy.update(sending: true, foreground: true, inhibited: false, at: 300), .none)
        XCTAssertEqual(policy.tick(at: 319), .none)
        XCTAssertEqual(policy.tick(at: 320), .dim)
    }

    func testInhibitionRestoresAndBlocksDimmingUntilReleased() {
        var policy = sendingPolicy()
        XCTAssertEqual(policy.tick(at: 130), .dim)
        // ガイド付き録画・Debug Recording・録画の再生中は人が画面を見る。
        XCTAssertEqual(policy.update(sending: true, foreground: true, inhibited: true, at: 131), .restore)
        XCTAssertEqual(policy.tick(at: 400), .none)
        XCTAssertEqual(policy.update(sending: true, foreground: true, inhibited: false, at: 400), .none)
        XCTAssertEqual(policy.tick(at: 419), .none)
        XCTAssertEqual(policy.tick(at: 420), .dim)
    }

    func testDisabledNeverDimsAndDisablingWhileDimmedRestores() {
        var policy = sendingPolicy()
        XCTAssertEqual(policy.tick(at: 130), .dim)
        XCTAssertEqual(policy.setEnabled(false, at: 131), .restore)
        XCTAssertEqual(policy.tick(at: 1000), .none)
        XCTAssertEqual(policy.setEnabled(true, at: 1000), .none)
        XCTAssertEqual(policy.tick(at: 1019), .none)
        XCTAssertEqual(policy.tick(at: 1020), .dim)

        var off = LowHeatDisplayPolicy(enabled: false, now: 0)
        XCTAssertEqual(off.update(sending: true, foreground: true, inhibited: false, at: 0), .none)
        XCTAssertEqual(off.tick(at: 10_000), .none)
        XCTAssertEqual(off.touch(at: 10_001), .none)
    }

    func testRepeatedUpdatesWithoutTransitionsDoNotPostponeDimming() {
        var policy = sendingPolicy()
        for time in stride(from: 101.0, through: 119, by: 1) {
            XCTAssertEqual(policy.update(sending: true, foreground: true, inhibited: false, at: time), .none)
            XCTAssertEqual(policy.tick(at: time), .none)
        }
        XCTAssertEqual(policy.update(sending: true, foreground: true, inhibited: false, at: 120), .none)
        XCTAssertEqual(policy.tick(at: 120), .dim)
    }

    func testStatusLineAndPendingBrightness() {
        XCTAssertEqual(LowHeatDisplayPolicy.statusLine(state: "送信中", cameraFPS: 59.6, thermal: "高い"),
                       "送信中　カメラ 60 fps　発熱 高い　（触れると元に戻ります）")
        XCTAssertEqual(LowHeatDisplayPolicy.pendingRestoreBrightness(NSNumber(value: 0.62)), 0.62)
        XCTAssertEqual(LowHeatDisplayPolicy.pendingRestoreBrightness(0.0), 0)
        XCTAssertNil(LowHeatDisplayPolicy.pendingRestoreBrightness(nil))
        XCTAssertNil(LowHeatDisplayPolicy.pendingRestoreBrightness(1.5))
        XCTAssertNil(LowHeatDisplayPolicy.pendingRestoreBrightness(Double.nan))
        XCTAssertNil(LowHeatDisplayPolicy.pendingRestoreBrightness("0.5"))
    }
}

#if !LOW_HEAT_DISPLAY_STANDALONE
/// 輝度の保存・復元と、CameraViewModel の送信・前面・停止との接続。
@MainActor
final class LowHeatDisplayControllerTests: XCTestCase {
    private final class FakeScreen {
        var brightness = 0.8
        var writes: [Double] = []
        var screen: LowHeatDisplayController.Screen {
            LowHeatDisplayController.Screen(
                brightness: { [unowned self] in self.brightness },
                setBrightness: { [unowned self] in self.brightness = $0; self.writes.append($0) })
        }
    }

    private var suiteName = ""
    private var defaults: UserDefaults!

    override func setUp() async throws {
        suiteName = "LowHeatDisplayTests-\(UUID().uuidString)"
        defaults = UserDefaults(suiteName: suiteName)
    }

    override func tearDown() async throws {
        defaults.removePersistentDomain(forName: suiteName)
    }

    func testDimLowersBrightnessAndRestoreReturnsThePreviousValue() {
        let fake = FakeScreen()
        let controller = LowHeatDisplayController(enabled: true, now: 0, screen: fake.screen, defaults: defaults,
                                                  observeApplication: false)
        var changes: [Bool] = []
        controller.onDimmedChange = { changes.append($0) }
        controller.update(sending: true, foreground: true, inhibited: false, at: 0)
        XCTAssertFalse(controller.tick(at: 19))
        XCTAssertTrue(controller.tick(at: 20))
        XCTAssertTrue(controller.isDimmed)
        XCTAssertEqual(fake.brightness, LowHeatDisplayPolicy.dimmedBrightness)
        // 異常終了に備えて元の輝度を残す。
        XCTAssertEqual(defaults.double(forKey: LowHeatDisplayPolicy.savedBrightnessKey), 0.8)
        XCTAssertTrue(controller.touch(at: 21))
        XCTAssertEqual(fake.brightness, 0.8)
        XCTAssertNil(defaults.object(forKey: LowHeatDisplayPolicy.savedBrightnessKey))
        XCTAssertNil(controller.savedBrightness)
        XCTAssertEqual(changes, [true, false])
    }

    func testBrightnessAlreadyBelowTheDimLevelIsNotRaised() {
        let fake = FakeScreen()
        fake.brightness = 0.01
        let controller = LowHeatDisplayController(enabled: true, now: 0, screen: fake.screen, defaults: nil,
                                                  observeApplication: false)
        controller.update(sending: true, foreground: true, inhibited: false, at: 0)
        controller.tick(at: 20)
        XCTAssertTrue(controller.isDimmed)
        XCTAssertEqual(fake.brightness, 0.01)
        controller.touch(at: 30)
        XCTAssertEqual(fake.brightness, 0.01)
    }

    func testApplicationLeavingForegroundRestoresBrightness() {
        for name in [UIApplication.willResignActiveNotification, UIApplication.didEnterBackgroundNotification,
                     UIApplication.willTerminateNotification] {
            let fake = FakeScreen()
            let controller = LowHeatDisplayController(enabled: true, now: 0, screen: fake.screen, defaults: defaults,
                                                      observeApplication: true)
            controller.update(sending: true, foreground: true, inhibited: false, at: 0)
            controller.tick(at: 20)
            XCTAssertEqual(fake.brightness, LowHeatDisplayPolicy.dimmedBrightness, name.rawValue)
            NotificationCenter.default.post(name: name, object: nil)
            XCTAssertFalse(controller.isDimmed, name.rawValue)
            XCTAssertEqual(fake.brightness, 0.8, name.rawValue)
            XCTAssertNil(defaults.object(forKey: LowHeatDisplayPolicy.savedBrightnessKey), name.rawValue)
        }
    }

    func testBrightnessLeftLowByAPreviousCrashIsRestoredAtLaunch() {
        defaults.set(0.7, forKey: LowHeatDisplayPolicy.savedBrightnessKey)
        let fake = FakeScreen()
        fake.brightness = LowHeatDisplayPolicy.dimmedBrightness
        _ = LowHeatDisplayController(enabled: true, now: 0, screen: fake.screen, defaults: defaults,
                                     observeApplication: false)
        XCTAssertEqual(fake.brightness, 0.7)
        XCTAssertNil(defaults.object(forKey: LowHeatDisplayPolicy.savedBrightnessKey))
    }

    func testViewModelDimsWhileSendingAndRestoresOnTouchBackgroundAndStop() {
        let fake = FakeScreen()
        let controller = LowHeatDisplayController(enabled: true, now: 0, screen: fake.screen, defaults: nil,
                                                  observeApplication: false)
        let model = CameraViewModel(sender: UDPSender { _, _, _ in }, p2pEnabled: false,
                                    idleTimerUpdater: { _ in }, lowHeatDisplay: controller)
        defer { model.stop() }
        XCTAssertTrue(model.lowHeatDisplayEnabled)
        let start = ProcessInfo.processInfo.systemUptime
        model.lowHeatTickForTesting(at: start + 60)
        XCTAssertFalse(model.lowHeatDisplayActive, "送信していない間は暗くしない")

        model.startForTesting()
        let sending = ProcessInfo.processInfo.systemUptime
        model.lowHeatTickForTesting(at: sending + 19)
        XCTAssertFalse(model.lowHeatDisplayActive)
        model.lowHeatTickForTesting(at: sending + 20)
        XCTAssertTrue(model.lowHeatDisplayActive)
        XCTAssertEqual(fake.brightness, LowHeatDisplayPolicy.dimmedBrightness)
        XCTAssertTrue(model.running, "暗くしても送信は止めない")

        model.lowHeatTouch(at: sending + 21)
        XCTAssertFalse(model.lowHeatDisplayActive)
        XCTAssertEqual(fake.brightness, 0.8)

        model.lowHeatTickForTesting(at: sending + 41)
        XCTAssertTrue(model.lowHeatDisplayActive)
        model.sceneDidChange(isActive: false)
        XCTAssertFalse(model.lowHeatDisplayActive)
        XCTAssertEqual(fake.brightness, 0.8)

        model.sceneDidChange(isActive: true)
        let foreground = ProcessInfo.processInfo.systemUptime
        model.lowHeatTickForTesting(at: foreground + 20)
        XCTAssertTrue(model.lowHeatDisplayActive)
        model.stop()
        XCTAssertFalse(model.lowHeatDisplayActive)
        XCTAssertEqual(fake.brightness, 0.8)
        XCTAssertEqual(model.lowHeatStatusLine, "")
    }

    func testTurningTheSettingOffRestoresAndKeepsTheScreenOn() {
        let fake = FakeScreen()
        let controller = LowHeatDisplayController(enabled: true, now: 0, screen: fake.screen, defaults: nil,
                                                  observeApplication: false)
        let model = CameraViewModel(sender: UDPSender { _, _, _ in }, p2pEnabled: false,
                                    idleTimerUpdater: { _ in }, lowHeatDisplay: controller)
        defer { model.stop() }
        model.startForTesting()
        let sending = ProcessInfo.processInfo.systemUptime
        model.lowHeatTickForTesting(at: sending + 20)
        XCTAssertTrue(model.lowHeatDisplayActive)
        model.setLowHeatDisplayEnabled(false)
        XCTAssertFalse(model.lowHeatDisplayEnabled)
        XCTAssertFalse(model.lowHeatDisplayActive)
        XCTAssertEqual(fake.brightness, 0.8)
        model.lowHeatTickForTesting(at: sending + 1000)
        XCTAssertFalse(model.lowHeatDisplayActive)
    }

    func testRecordingPreviewInhibitsDimming() {
        let fake = FakeScreen()
        let controller = LowHeatDisplayController(enabled: true, now: 0, screen: fake.screen, defaults: nil,
                                                  observeApplication: false)
        let model = CameraViewModel(sender: UDPSender { _, _, _ in }, p2pEnabled: false,
                                    idleTimerUpdater: { _ in }, lowHeatDisplay: controller)
        defer { model.stop() }
        model.startForTesting()
        model.setLowHeatInhibitedByUI(true)
        let sending = ProcessInfo.processInfo.systemUptime
        model.lowHeatTickForTesting(at: sending + 100)
        XCTAssertFalse(model.lowHeatDisplayActive)
        model.setLowHeatInhibitedByUI(false)
        let released = ProcessInfo.processInfo.systemUptime
        model.lowHeatTickForTesting(at: released + 20)
        XCTAssertTrue(model.lowHeatDisplayActive)
    }

    func testUnderTestTheDefaultControllerIsOffAndNeverTouchesTheScreen() {
        let model = CameraViewModel(sender: UDPSender { _, _, _ in }, p2pEnabled: false, idleTimerUpdater: { _ in })
        defer { model.stop() }
        XCTAssertFalse(model.lowHeatDisplayEnabled)
        model.startForTesting()
        model.lowHeatTickForTesting(at: ProcessInfo.processInfo.systemUptime + 1000)
        XCTAssertFalse(model.lowHeatDisplayActive)
    }
}
#endif
