import XCTest
import CoreMedia
import CoreVideo
#if !HIGH_SPEED_STANDALONE
@testable import PhoneSaberSender
#endif

// 却下した異画角HFPSの回帰。検出入力と熱退避前後の座標系を従来どおり保つ。
final class HighSpeedCaptureTests: XCTestCase {
    func testRejectsFormatsThatChangeCalibrationOnThermalFallback() {
        let baseline = CameraFormatOption(index: 0, width: 640, height: 480,
                                         frameRateRanges: [1...60], fieldOfView: 65)
        let incompatible = CameraFormatOption(index: 1, width: 1280, height: 720,
                                             frameRateRanges: [1...240], fieldOfView: 58)
        let options = [baseline, incompatible]
        XCTAssertEqual(preferredCameraFormatIndex(options: options, targetFPS: 60), baseline.index)
#if REJECTED_HFPS_REVISION
        // 旧revisionでは実際のopt-in選択経路を通し、異画角を受理する欠陥を再現する。
        for fps in [120, 240] {
            XCTAssertNil(highSpeedCameraFormatIndex(options: options, targetFPS: fps, experimental: true))
        }
#else
        XCTAssertNil(highSpeedCameraFormatIndex(options: options))
#endif
        let compatible = CameraFormatOption(index: 2, width: 640, height: 480,
                                           frameRateRanges: [1...120], fieldOfView: 65)
        XCTAssertEqual(highSpeedCameraFormatIndex(options: [baseline, compatible]), compatible.index)
    }

    func testNativeFrameKeepsEdgeSaberAndDimensionsAcrossThermalFallback() throws {
        // 旧中央cropの外側にある赤棒。cropすると候補が消え、熱退避後だけ再出現する。
        let buffer = try image(width: 720, height: 1280) { x, y in
            (150...550).contains(x) && (50...58).contains(y) ? (35, 45, 245) : (0, 0, 0)
        }
        var description: CMVideoFormatDescription?
        CMVideoFormatDescriptionCreateForImageBuffer(allocator: nil, imageBuffer: buffer, formatDescriptionOut: &description)
        var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: .zero, decodeTimeStamp: .invalid)
        var sample: CMSampleBuffer?
        CMSampleBufferCreateReadyWithImageBuffer(allocator: nil, imageBuffer: buffer,
            formatDescription: try XCTUnwrap(description), sampleTiming: &timing, sampleBufferOut: &sample)
        let frame = try XCTUnwrap(sample)
        let processor = FrameProcessor(expiryScheduler: nil)
        var results: [[DetectedSaber]] = []
        processor.onResult = { detected, width, height, _, _, _ in
            XCTAssertEqual(width, 720); XCTAssertEqual(height, 1280)
            XCTAssertFalse(detected.isEmpty)
            results.append(detected)
        }
#if REJECTED_HFPS_REVISION
        let geometry = try XCTUnwrap(HighSpeedFrameGeometry(sourceWidth: 720, sourceHeight: 1280, width: 480, height: 640))
        XCTAssertTrue(processor.configureHighSpeedFrame(geometry))
#endif
        processor.configureCaptureFPS(120)
        processor.queue.sync { processor.process(frame) }
#if REJECTED_HFPS_REVISION
        XCTAssertTrue(processor.configureHighSpeedFrame(nil))
#endif
        processor.configureCaptureFPS(60)
        processor.queue.sync { processor.process(frame) }
        XCTAssertEqual(results.count, 2)
        XCTAssertEqual(results.first?.map { $0.endpoints.0 }, results.last?.map { $0.endpoints.0 })
        XCTAssertEqual(results.first?.map { $0.endpoints.1 }, results.last?.map { $0.endpoints.1 })
    }

    private func image(width: Int, height: Int, pixel: (Int, Int) -> (UInt8, UInt8, UInt8)) throws -> CVPixelBuffer {
        var buffer: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(nil, width, height, kCVPixelFormatType_32BGRA, nil, &buffer), kCVReturnSuccess)
        let result = try XCTUnwrap(buffer)
        CVPixelBufferLockBaseAddress(result, [])
        defer { CVPixelBufferUnlockBaseAddress(result, []) }
        let bytes = CVPixelBufferGetBaseAddress(result)!.assumingMemoryBound(to: UInt8.self)
        let stride = CVPixelBufferGetBytesPerRow(result)
        for y in 0..<height { for x in 0..<width {
            let p = pixel(x, y), i = y * stride + x * 4
            bytes[i] = p.0; bytes[i + 1] = p.1; bytes[i + 2] = p.2; bytes[i + 3] = 255
        } }
        return result
    }

}
