import Foundation
import XCTest
#if !DEVICE_HEALTH_STANDALONE
@testable import PhoneSaberSender
#endif

final class DeviceHealthTests: XCTestCase {
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
}
