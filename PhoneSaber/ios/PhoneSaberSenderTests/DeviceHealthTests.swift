import Foundation
import XCTest
#if !DEVICE_HEALTH_STANDALONE
@testable import PhoneSaberSender
#endif

final class DeviceHealthTests: XCTestCase {
    func testRatesAfterEightHoursUseOnlyTheRecentWindow() {
        let meter = DeviceHealthMeter()
        meter.reset(at: 0, generation: 1)
        meter.cameraFrame(at: 1)
        meter.processed(at: 1, milliseconds: 900, generation: 1, captureToSendMs: 1000)
        let eightHours = 8.0 * 60 * 60
        for frame in 1...300 {
            let time = eightHours + Double(frame) / 60
            meter.cameraFrame(at: time)
            meter.processed(at: time, milliseconds: 12, generation: 1, captureToSendMs: 40)
        }
        let rates = meter.snapshot(at: eightHours + 5)
        XCTAssertEqual(rates.cameraFPS, 60)
        XCTAssertEqual(rates.processingFPS, 60)
        XCTAssertEqual(rates.medianProcessingMs, 12)
        XCTAssertEqual(rates.medianCaptureToSendMs, 40)
        XCTAssertTrue(rates.ready)
        XCTAssertEqual(meter.snapshot(at: eightHours + 10).cameraFPS, 0)
        XCTAssertEqual(meter.snapshot(at: eightHours + 10).processingFPS, 0)
    }

    func testThermalClassificationAndMetadata() {
        let states: [ProcessInfo.ThermalState] = [.nominal, .fair, .serious, .critical]
        XCTAssertEqual(states.map { DeviceThermalLevel(state: $0).title }, ["正常", "やや高い", "高い", "危険"])
        XCTAssertEqual(states.map { DeviceThermalLevel(state: $0).metadataValue }, ["nominal", "fair", "serious", "critical"])
    }

    func testBatteryAndTimingFormattingHandlesUnavailableValues() {
        XCTAssertEqual(DeviceHealthText.battery(level: 0.425, state: "充電中"), "電池 43% / 充電中")
        XCTAssertEqual(DeviceHealthText.battery(level: 1, state: "満充電"), "電池 100% / 満充電")
        for value in [-1.0, 1.1, .nan, .infinity] {
            XCTAssertEqual(DeviceHealthText.battery(level: value, state: nil), "電池 不明 / 状態不明")
        }
        XCTAssertTrue(DeviceHealthText.timing(DeviceHealthRates()).contains("未計測"))
        XCTAssertTrue(DeviceHealthText.timing(DeviceHealthRates(medianProcessingMs: 12.25)).contains("12.2 ms"))
    }

    func testWarningBoundaryWarmupAndIndependentCameraProcessingRates() {
        let good = DeviceHealthRates(cameraFPS: 21, processingFPS: 21, ready: true)
        XCTAssertNil(DeviceHealthText.warning(thermal: .normal, rates: good, requestedFPS: 30, running: true))
        for low in [DeviceHealthRates(cameraFPS: 20.9, processingFPS: 30, ready: true),
                    DeviceHealthRates(cameraFPS: 30, processingFPS: 20.9, ready: true)] {
            XCTAssertTrue(DeviceHealthText.warning(thermal: .normal, rates: low, requestedFPS: 30, running: true)!.contains("70%未満"))
            XCTAssertNil(DeviceHealthText.warning(thermal: .normal, rates: low, requestedFPS: 30, running: false))
        }
        XCTAssertNil(DeviceHealthText.warning(thermal: .elevated, rates: DeviceHealthRates(), requestedFPS: 30, running: true))
        XCTAssertNotNil(DeviceHealthText.warning(thermal: .normal, rates: good, requestedFPS: 60, running: true))
        for state in [DeviceThermalLevel.high, .dangerous] {
            XCTAssertTrue(DeviceHealthText.warning(thermal: state, rates: good, requestedFPS: 30, running: false)!.contains(state.title))
        }
    }

    func testRollingMedianRatesStallAndSessionReset() {
        let meter = DeviceHealthMeter()
        meter.reset(at: 100, generation: 1)
        for (time, ms) in [(101.0, 90.0), (102, 10), (103, 30), (104, 20)] {
            meter.cameraFrame(at: time)
            meter.processed(at: time, milliseconds: ms, generation: 1)
        }
        let rates = meter.snapshot(at: 104)
        XCTAssertEqual(rates.cameraFPS, 1)
        XCTAssertEqual(rates.processingFPS, 1)
        XCTAssertEqual(rates.medianProcessingMs, 25)
        XCTAssertTrue(rates.ready)
        XCTAssertEqual(meter.snapshot(at: 106).medianProcessingMs, 20)
        XCTAssertEqual(meter.snapshot(at: 110).processingFPS, 0)
        XCTAssertNil(meter.snapshot(at: 110).medianProcessingMs)
        meter.reset(at: 110, generation: 2)
        meter.processed(at: 111, milliseconds: 30, generation: 1)
        meter.processed(at: 111, milliseconds: .nan, generation: 2)
        XCTAssertEqual(meter.snapshot(at: 111).processingFPS, 0)
        XCTAssertFalse(meter.snapshot(at: 111).ready)
    }


    func testCaptureToSendMedianIgnoresUnconvertibleAndOutOfRangeValues() {
        let meter = DeviceHealthMeter()
        meter.reset(at: 100, generation: 1)
        meter.processed(at: 101.0, milliseconds: 10, generation: 1, captureToSendMs: 60)
        meter.processed(at: 101.1, milliseconds: 10, generation: 1, captureToSendMs: 80)
        meter.processed(at: 101.2, milliseconds: 10, generation: 1, captureToSendMs: -5)
        meter.processed(at: 101.3, milliseconds: 10, generation: 1, captureToSendMs: 1e7)
        meter.processed(at: 101.4, milliseconds: 10, generation: 1)
        XCTAssertEqual(meter.snapshot(at: 102).medianCaptureToSendMs, 70)
        XCTAssertTrue(DeviceHealthText.timing(meter.snapshot(at: 102)).contains("撮影→送信 70 ms"))
        XCTAssertNil(meter.snapshot(at: 107).medianCaptureToSendMs)
        XCTAssertFalse(DeviceHealthText.timing(DeviceHealthRates()).contains("撮影"))
    }

#if !DEVICE_HEALTH_STANDALONE
    func testRecordingHealthIsOptionalAndEncodesWithoutExposureSnapshot() throws {
        let health = DebugDeviceHealthState(thermalState: "serious", batteryLevel: 0.42, batteryState: "charging")
        let camera = try XCTUnwrap(DebugRecordingFrameCamera.make(exif: nil, device: nil, now: 10, health: health))
        let encoded = try JSONEncoder().encode(camera)
        let decoded = try JSONDecoder().decode(DebugRecordingFrameCamera.self, from: encoded)
        XCTAssertEqual(decoded.thermalState, "serious")
        XCTAssertEqual(decoded.batteryLevel, 0.42)
        XCTAssertEqual(decoded.batteryState, "charging")
        let old = try JSONDecoder().decode(DebugRecordingFrameCamera.self, from: Data(#"{"source":"device","iso":100}"#.utf8))
        XCTAssertNil(old.thermalState)
        XCTAssertNil(old.batteryLevel)
        XCTAssertNil(old.batteryState)
        let unknown = DebugDeviceHealthState(thermalState: "nominal", batteryLevel: nil, batteryState: "unknown")
        let unknownCamera = try XCTUnwrap(DebugRecordingFrameCamera.make(exif: nil, device: nil, now: 10, health: unknown))
        let fields = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(unknownCamera)) as? [String: Any])
        XCTAssertNil(fields["batteryLevel"])
    }
#endif

    func testThermalFPSReductionCoolingAndNoFlapping() {
        var policy = ThermalFPSPolicy()
        XCTAssertEqual(policy.effectiveFPS(thermal: .normal, at: 0, userFPS: 60), 60)
        XCTAssertEqual(policy.effectiveFPS(thermal: .high, at: 1, userFPS: 60), 30)
        XCTAssertEqual(policy.effectiveFPS(thermal: .elevated, at: 2, userFPS: 60), 30)
        XCTAssertEqual(policy.effectiveFPS(thermal: .normal, at: 121, userFPS: 60), 30)
        XCTAssertEqual(policy.effectiveFPS(thermal: .normal, at: 122, userFPS: 60), 60)
        // 復帰直後の再発熱でも、切替は2分以上空ける。
        XCTAssertEqual(policy.effectiveFPS(thermal: .dangerous, at: 123, userFPS: 60), 60)
        XCTAssertEqual(policy.effectiveFPS(thermal: .dangerous, at: 242, userFPS: 60), 30)
    }

    func testThermalCoolingMustBeContinuousAndThirtyFPSIsUnchanged() {
        var policy = ThermalFPSPolicy()
        XCTAssertEqual(policy.effectiveFPS(thermal: .dangerous, at: 0, userFPS: 60), 30)
        XCTAssertEqual(policy.effectiveFPS(thermal: .normal, at: 1, userFPS: 60), 30)
        XCTAssertEqual(policy.effectiveFPS(thermal: .high, at: 120, userFPS: 60), 30)
        XCTAssertEqual(policy.effectiveFPS(thermal: .elevated, at: 121, userFPS: 60), 30)
        XCTAssertEqual(policy.effectiveFPS(thermal: .unknown, at: 240, userFPS: 60), 30)
        XCTAssertEqual(policy.effectiveFPS(thermal: .normal, at: 241, userFPS: 60), 30)
        XCTAssertEqual(policy.effectiveFPS(thermal: .normal, at: 360, userFPS: 60), 30)
        XCTAssertEqual(policy.effectiveFPS(thermal: .normal, at: 361, userFPS: 60), 60)
        var thirty = ThermalFPSPolicy()
        for thermal in [DeviceThermalLevel.normal, .elevated, .high, .dangerous] {
            XCTAssertEqual(thirty.effectiveFPS(thermal: thermal, at: 1000, userFPS: 30), 30)
            XCTAssertFalse(thirty.reduced)
        }
    }

    func testBatteryAndLowPowerWarningsAppearInTheExistingHealthWarning() {
        func warning(_ level: Double?, _ state: String, sending: Bool = true, lowPower: Bool = false) -> String? {
            DeviceHealthText.warning(thermal: .normal, rates: DeviceHealthRates(), requestedFPS: 60,
                                     running: sending, batteryLevel: level, batteryState: state, lowPowerMode: lowPower)
        }
        XCTAssertTrue((warning(0.8, "unplugged") ?? "").contains("充電されていません。ケーブルを確認"))
        XCTAssertNil(warning(0.8, "unplugged", sending: false))
        XCTAssertTrue((warning(0.20, "unplugged") ?? "").contains("20%以下"))
        XCTAssertFalse((warning(0.201, "unplugged") ?? "").contains("20%以下"))
        XCTAssertTrue((warning(0.20, "unplugged", sending: false) ?? "").contains("20%以下"))
        XCTAssertNil(warning(0.20, "charging"))
        XCTAssertNil(warning(0.20, "full"))
        for state in ["charging", "full", "unplugged", "unknown"] {
            XCTAssertTrue((warning(0.10, state, sending: false) ?? "").contains("10%以下"))
        }
        XCTAssertNil(warning(0.101, "charging"))
        XCTAssertTrue((warning(nil, "unknown", sending: false, lowPower: true) ?? "").contains("設定でOFF"))
        for invalid in [-1.0, 1.1, .nan, .infinity] { XCTAssertNil(warning(invalid, "unknown")) }
    }

    func testThermalFPSReconfigurationWaitsForLiveForegroundCamera() {
        func shouldRestart(sending: Bool = true, foreground: Bool = true,
                           live: Bool = true, recovering: Bool = false, effective: Int = 30) -> Bool {
            ThermalFPSPolicy.shouldReconfigure(currentFPS: 60, effectiveFPS: effective, sending: sending,
                                               foreground: foreground, cameraLive: live, recovering: recovering)
        }
        XCTAssertTrue(shouldRestart())
        XCTAssertFalse(shouldRestart(sending: false))
        XCTAssertFalse(shouldRestart(foreground: false))
        // 起動・復旧中は設定へ反映されるまで待ち、stop/start で backoff をリセットしない。
        XCTAssertFalse(shouldRestart(live: false))
        XCTAssertFalse(shouldRestart(recovering: true))
        XCTAssertFalse(shouldRestart(effective: 60))
    }

}
