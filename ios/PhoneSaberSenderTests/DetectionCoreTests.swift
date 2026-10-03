import XCTest
import AVFoundation
import Combine
import UIKit
import Darwin
@testable import PhoneSaberSender

private final class CompletionBox {
    private var values: [(Int, (Result<TimeInterval, Error>) -> Void)] = []
    private(set) var sent: [(String, Int)] = []
    private let lock = NSLock()

    func append(port: Int, text: String? = nil, completion: @escaping (Result<TimeInterval, Error>) -> Void) {
        lock.lock(); defer { lock.unlock() }
        if let text { sent.append((text, port)) }
        values.append((port, completion))
    }

    var valuesCountForTesting: Int {
        lock.lock(); defer { lock.unlock() }
        return values.count
    }

    func removeFirst() -> (Int, (Result<TimeInterval, Error>) -> Void)? {
        lock.lock(); defer { lock.unlock() }
        return values.isEmpty ? nil : values.removeFirst()
    }
}

private final class ManualClock {
    private let lock = NSLock()
    private var value: TimeInterval

    init(_ value: TimeInterval) { self.value = value }

    var now: TimeInterval {
        lock.lock(); defer { lock.unlock() }
        return value
    }

    func advance(by amount: TimeInterval) {
        lock.lock(); value += amount; lock.unlock()
    }
}

// Polls until the condition holds and returns as soon as it does. Every caller
// expects the condition to become true, so the default bound is only a safety
// limit for a loaded host, not a latency requirement.
@MainActor
private func waitUntil(_ condition: @escaping @MainActor () -> Bool, timeout: TimeInterval = 3.0) async -> Bool {
    let deadline = ContinuousClock.now + .seconds(timeout)
    while !condition() && ContinuousClock.now < deadline {
        try? await Task.sleep(for: .milliseconds(2))
    }
    return condition()
}

final class DetectionCoreTests: XCTestCase {
    func testCoreLineCandidateScoringIsIndependentOfPointOrder() throws {
        // A slanted, ragged bright-core proposal: the same pixels in any order (and
        // any Set instance) must score bit-identically. Set iteration order used to
        // reach the floating-point sums and axial bins, so results varied across
        // launches and even between frames.
        var points: [PixelPoint] = []
        for t in 0..<160 {
            let x = 40 + t, y = 60 + t * 3 / 7
            for w in 0...(t % 5 == 0 ? 4 : 2) { points.append(PixelPoint(x: x, y: y + w)) }
        }
        points += points.prefix(30)  // duplicates collapse to the same unique set
        let reference = try XCTUnwrap(saberCandidate(from: points, width: 640, height: 480))
        var generator = SystemRandomNumberGenerator()
        for _ in 0..<30 {
            let candidate = try XCTUnwrap(saberCandidate(from: points.shuffled(using: &generator),
                                                         width: 640, height: 480))
            XCTAssertEqual(candidate.score.bitPattern, reference.score.bitPattern)
            XCTAssertEqual(candidate.rawPCASpan.bitPattern, reference.rawPCASpan.bitPattern)
            XCTAssertEqual(candidate.endpoints.0, reference.endpoints.0)
            XCTAssertEqual(candidate.endpoints.1, reference.endpoints.1)
            XCTAssertEqual(candidate.isEmitterEligible, reference.isEmitterEligible)
        }
    }

    func testCameraFormatSelectionRequires30FPSAndKeepsCompactResolution() {
        let options = [
            CameraFormatOption(index: 0, width: 1920, height: 1080, frameRateRanges: [24...24]),
            CameraFormatOption(index: 1, width: 640, height: 480, frameRateRanges: [1...240]),
            CameraFormatOption(index: 2, width: 640, height: 480, frameRateRanges: [1...30]),
            CameraFormatOption(index: 3, width: 320, height: 240, frameRateRanges: [1...30])
        ]

        XCTAssertEqual(preferredCameraFormatIndex(options: options), 2)
        XCTAssertTrue(supportsFrameRate(30, ranges: options[2].frameRateRanges))
        XCTAssertFalse(supportsFrameRate(30, ranges: options[0].frameRateRanges))
        XCTAssertNil(preferredCameraFormatIndex(options: [options[0]]))
    }

    func testCameraFormatSelectionRequires60FPSAndKeepsCompactResolution() {
        let options = [
            CameraFormatOption(index: 0, width: 640, height: 480, frameRateRanges: [1...30]),
            CameraFormatOption(index: 1, width: 1280, height: 720, frameRateRanges: [1...60]),
            CameraFormatOption(index: 2, width: 640, height: 480, frameRateRanges: [30...60]),
            CameraFormatOption(index: 3, width: 320, height: 240, frameRateRanges: [1...60])
        ]

        XCTAssertEqual(preferredCameraFormatIndex(options: options, targetFPS: 60), 2)
        XCTAssertTrue(supportsFrameRate(60, ranges: options[2].frameRateRanges))
        XCTAssertFalse(supportsFrameRate(60, ranges: options[0].frameRateRanges))
        XCTAssertNil(preferredCameraFormatIndex(options: [options[0]], targetFPS: 60))
    }

    func testFixedFrameDurationIsOnlyCreatedForSupportedRate() throws {
        let duration = try XCTUnwrap(fixedFrameDuration(fps: 30, ranges: [1...30]))
        XCTAssertEqual(CMTimeGetSeconds(duration), 1 / 30.0, accuracy: 0.000_001)
        XCTAssertNil(fixedFrameDuration(fps: 30, ranges: [1...24]))
        let sixty = try XCTUnwrap(fixedFrameDuration(fps: 60, ranges: [30...60]))
        XCTAssertEqual(CMTimeGetSeconds(sixty), 1 / 60.0, accuracy: 0.000_001)
    }


    func testReusingClosedMaskProducesSameCloseOpenResult() {
        let width = 17, height = 13
        var mask = Array(repeating: UInt8(0), count: width * height)
        for x in 2...14 where x != 8 { mask[6 * width + x] = 1 }
        mask[2 * width + 2] = 1
        let closed = closeSaberMask(mask, width: width, height: height, radius: 2)
        XCTAssertEqual(openSaberMask(closed, width: width, height: height),
                       cleanSaberMask(mask, width: width, height: height, closeRadius: 2))
    }

    func testCameraFrameIntervalStatisticsUseLast120PresentationTimestamps() throws {
        var window = CameraFrameIntervalWindow(capacity: 120)
        for index in 0...130 {
            window.record(presentationTime: Double(index) / 30)
        }
        let statistics = try XCTUnwrap(window.statistics)
        XCTAssertEqual(statistics.sampleCount, 120)
        XCTAssertEqual(statistics.medianMs, 1000 / 30.0, accuracy: 0.001)
        XCTAssertEqual(statistics.minimumMs, 1000 / 30.0, accuracy: 0.001)
        XCTAssertEqual(statistics.maximumMs, 1000 / 30.0, accuracy: 0.001)
        XCTAssertEqual(statistics.measuredFPS, 30, accuracy: 0.001)

        window.record(presentationTime: 1)
        XCTAssertNil(window.statistics, "timestamp discontinuities must reset the interval window")
    }

    func testNormalCameraStartupBecomesLiveOnlyAfterAFrameAndThenDetectsStall() {
        var lifecycle = CameraLifecycleStateMachine()
        XCTAssertEqual(lifecycle.state, .stopped)

        lifecycle.requestStart(at: 10)
        lifecycle.sessionStarted(at: 10.05)
        XCTAssertEqual(lifecycle.state, .starting)
        XCTAssertFalse(lifecycle.evaluateStall(at: 12.049))
        XCTAssertTrue(lifecycle.evaluateStall(at: 12.05))
        XCTAssertEqual(lifecycle.state, .stalled)

        lifecycle.receivedFrame(at: 12.08)
        XCTAssertEqual(lifecycle.state, .live)
        XCTAssertEqual(lifecycle.lastFrameAt, 12.08)
    }

    func testThirtyFPSFrameCadenceHasTwoSecondStallGrace() {
        var lifecycle = CameraLifecycleStateMachine()
        lifecycle.requestStart(at: 0)
        lifecycle.receivedFrame(at: 0)

        for frame in 1...59 {
            let time = Double(frame) / 30
            lifecycle.receivedFrame(at: time)
            XCTAssertFalse(lifecycle.evaluateStall(at: time))
        }
        XCTAssertFalse(lifecycle.evaluateStall(at: 59.0 / 30.0 + 1.999))
        XCTAssertTrue(lifecycle.evaluateStall(at: 59.0 / 30.0 + 2.0))
        XCTAssertEqual(lifecycle.state, .stalled)
    }

    func testBackgroundForegroundRecoveryWaitsForFreshFrame() {
        var lifecycle = CameraLifecycleStateMachine()
        lifecycle.requestStart(at: 1)
        lifecycle.receivedFrame(at: 1.03)
        XCTAssertEqual(lifecycle.state, .live)

        XCTAssertFalse(lifecycle.setForeground(false, at: 1.04))
        XCTAssertTrue(lifecycle.setForeground(true, at: 8))
        XCTAssertEqual(lifecycle.state, .recovering)
        lifecycle.restartFinished(succeeded: true, at: 8.1)
        XCTAssertEqual(lifecycle.state, .starting)
        XCTAssertEqual(lifecycle.lastFrameAt, 1.03, "retain the last produced frame until a new one arrives")

        lifecycle.receivedFrame(at: 8.13)
        XCTAssertEqual(lifecycle.state, .live)
        XCTAssertEqual(lifecycle.lastFrameAt, 8.13)
    }

    func testFrameTimestampFromBeforeSessionRestartCannotMarkCameraLive() {
        var lifecycle = CameraLifecycleStateMachine()
        lifecycle.requestStart(at: 0)
        lifecycle.receivedFrame(at: 0.03)
        lifecycle.interruptionBegan(reason: "camera in use")
        XCTAssertTrue(lifecycle.interruptionEnded(at: 2))
        lifecycle.restartFinished(succeeded: true, at: 2.1)

        lifecycle.receivedFrame(at: 2.09)
        XCTAssertEqual(lifecycle.state, .starting)
        XCTAssertEqual(lifecycle.lastFrameAt, 0.03)
        lifecycle.receivedFrame(at: 2.13)
        XCTAssertEqual(lifecycle.state, .live)
        XCTAssertEqual(lifecycle.lastFrameAt, 2.13)
    }

    func testInterruptionRequiresSessionRestartAndNewFrame() {
        var lifecycle = CameraLifecycleStateMachine()
        lifecycle.requestStart(at: 0)
        lifecycle.receivedFrame(at: 0.03)
        lifecycle.interruptionBegan(reason: "camera in use")
        XCTAssertEqual(lifecycle.state, .interrupted("camera in use"))

        lifecycle.receivedFrame(at: 0.08)
        XCTAssertEqual(lifecycle.state, .interrupted("camera in use"))
        XCTAssertTrue(lifecycle.interruptionEnded(at: 1))
        XCTAssertEqual(lifecycle.state, .recovering)
        lifecycle.restartFinished(succeeded: true, at: 1.1)
        XCTAssertEqual(lifecycle.state, .starting)
        lifecycle.receivedFrame(at: 1.14)
        XCTAssertEqual(lifecycle.state, .live)
    }

    func testRuntimeErrorAndFailedSessionRestartNeverReportCameraLive() {
        var lifecycle = CameraLifecycleStateMachine()
        lifecycle.requestStart(at: 0)
        lifecycle.receivedFrame(at: 0.03)
        lifecycle.runtimeError("media services reset")
        XCTAssertEqual(lifecycle.state, .failed("media services reset"))

        XCTAssertTrue(lifecycle.beginRecovery(at: 1))
        lifecycle.restartFinished(succeeded: false, at: 1.1, error: "restart failed")
        XCTAssertEqual(lifecycle.state, .failed("restart failed"))
        lifecycle.receivedFrame(at: 1.2)
        XCTAssertEqual(lifecycle.state, .failed("restart failed"))
    }

    @MainActor
    func testScreenSleepPreventionTracksActiveSendingAndCameraStateIsSeparateFromNetwork() async {
        var idleTimerChanges: [Bool] = []
        let sender = UDPSender { _, _, completion in completion(.success(1)) }
        let viewModel = CameraViewModel(sender: sender, idleTimerUpdater: { idleTimerChanges.append($0) })
        viewModel.startForTesting()
        let ready = await waitUntil { viewModel.networkStateLabel == "NETWORK READY" }
        XCTAssertTrue(ready)
        XCTAssertTrue(viewModel.screenSleepPreventionActive)
        XCTAssertEqual(viewModel.cameraState, .starting)

        viewModel.sceneDidChange(isActive: false)
        XCTAssertFalse(viewModel.screenSleepPreventionActive)
        viewModel.sceneDidChange(isActive: true)
        XCTAssertTrue(viewModel.screenSleepPreventionActive)
        viewModel.stop()
        XCTAssertFalse(viewModel.screenSleepPreventionActive)
        XCTAssertEqual(idleTimerChanges, [true, false, true, false])
        sender.stop()
    }

    @MainActor
    func testBackgroundInterruptionAndRuntimeErrorNotificationsKeepNetworkAndCameraSeparate() async {
        let sender = UDPSender { _, _, completion in completion(.success(1)) }
        let viewModel = CameraViewModel(sender: sender, idleTimerUpdater: { _ in })
        viewModel.startForTesting()
        let ready = await waitUntil { viewModel.networkStateLabel == "NETWORK READY" }
        XCTAssertTrue(ready)

        viewModel.sceneDidChange(isActive: false)
        NotificationCenter.default.post(
            name: AVCaptureSession.wasInterruptedNotification,
            object: viewModel.session,
            userInfo: [AVCaptureSessionInterruptionReasonKey: NSNumber(value: 1)]
        )
        let interrupted = await waitUntil {
            if case .interrupted = viewModel.cameraState { return true }
            return false
        }
        XCTAssertTrue(interrupted)

        NotificationCenter.default.post(
            name: AVCaptureSession.interruptionEndedNotification,
            object: viewModel.session
        )
        try? await Task.sleep(for: .milliseconds(20))
        if case .interrupted = viewModel.cameraState {
            // Ending the interruption in the background is deferred until foreground.
        } else {
            XCTFail("background interruption end must wait for foreground recovery")
        }
        viewModel.sceneDidChange(isActive: true)
        XCTAssertEqual(viewModel.cameraState, .recovering)
        XCTAssertEqual(viewModel.networkStateLabel, "NETWORK READY")

        NotificationCenter.default.post(
            name: AVCaptureSession.runtimeErrorNotification,
            object: viewModel.session,
            userInfo: [AVCaptureSessionErrorKey: NSError(domain: "CameraLifecycleTests", code: 42,
                                                        userInfo: [NSLocalizedDescriptionKey: "runtime failure"])]
        )
        let failed = await waitUntil {
            if case .failed("runtime failure") = viewModel.cameraState { return true }
            return false
        }
        XCTAssertTrue(failed)
        XCTAssertEqual(viewModel.networkStateLabel, "NETWORK READY")
        viewModel.stop()
        sender.stop()
    }

    @MainActor
    func testDebugPerformanceRowsExposeNamedUnavailableMetricsBeforeCapture() {
        let viewModel = CameraViewModel(
            authorizationStatus: { .denied },
            requestAccess: { _ in }
        )

        XCTAssertFalse(viewModel.debugDetailedProfilingEnabled,
                       "collapsed Debug Performance must not enable detailed hot-path timers")
        XCTAssertEqual(viewModel.debugPerformanceRows.filter { $0.category == "Camera" }.count, 5)
        XCTAssertEqual(viewModel.debugPerformanceRows.filter { $0.category == "Processing" }.count, 14)
        XCTAssertEqual(viewModel.debugPerformanceRows.filter { $0.category == "Network" }.count, 3)
        let frameAge = try? XCTUnwrap(viewModel.debugPerformanceRows.first { $0.label == "Frame age" })
        XCTAssertNil(frameAge?.latest)
        XCTAssertNil(frameAge?.median)
        XCTAssertNil(frameAge?.maximum)
        XCTAssertEqual(viewModel.debugPerformanceRows.first { $0.label == "Detection" }?.unit, "ms")
        XCTAssertEqual(viewModel.debugPerformanceRows.first { $0.label == "Send rate" }?.unit, "/s")

        viewModel.recordDebugPerformanceForTesting(name: "Detection", value: 8.5)
        XCTAssertEqual(viewModel.debugPerformanceRows.first { $0.label == "Detection" }?.latest, 8.5)
        XCTAssertEqual(viewModel.debugPerformanceRows.first { $0.label == "Detection" }?.median, 8.5)
        XCTAssertEqual(viewModel.debugPerformanceRows.first { $0.label == "Detection" }?.maximum, 8.5)
        XCTAssertEqual(viewModel.debugPerformancePublishCountForTesting, 1)

        viewModel.recordDebugPerformanceForTesting(name: "Detection", value: 9.5)
        XCTAssertEqual(viewModel.debugPerformancePublishCountForTesting, 1,
                       "performance snapshots must remain capped at 5Hz")
    }

    private func sampleBuffer(width: Int, height: Int, draw: (UnsafeMutableRawPointer, Int) -> Void) -> CMSampleBuffer {
        var pixelBuffer: CVPixelBuffer?
        CVPixelBufferCreate(kCFAllocatorDefault, width, height, kCVPixelFormatType_32BGRA, nil, &pixelBuffer)
        let buffer = pixelBuffer!
        CVPixelBufferLockBaseAddress(buffer, [])
        let stride = CVPixelBufferGetBytesPerRow(buffer)
        let base = CVPixelBufferGetBaseAddress(buffer)!
        let pixel = base.assumingMemoryBound(to: UInt8.self)
        for y in 0..<height {
            for x in 0..<width {
                let offset = y * stride + x * 4
                pixel[offset] = 0
                pixel[offset + 1] = 0
                pixel[offset + 2] = 0
                pixel[offset + 3] = 255
            }
        }
        draw(base, stride)
        CVPixelBufferUnlockBaseAddress(buffer, [])
        var format: CMVideoFormatDescription?
        CMVideoFormatDescriptionCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: buffer, formatDescriptionOut: &format)
        var sample: CMSampleBuffer?
        var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: .zero, decodeTimeStamp: .invalid)
        CMSampleBufferCreateReadyWithImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: buffer, formatDescription: format!, sampleTiming: &timing, sampleBufferOut: &sample)
        return sample!
    }

    private func fixtureImage(_ name: String, subdirectory: String? = nil) throws -> UIImage {
        guard let url = Bundle(for: Self.self).url(forResource: name, withExtension: "png",
                                                   subdirectory: subdirectory),
              let image = UIImage(contentsOfFile: url.path) else {
            throw NSError(domain: "PhoneSaberSenderTests", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "fixture not found: \(name).png"])
        }
        return image
    }

    private func fixtureBGRA(_ name: String, subdirectory: String? = nil) throws -> (bytes: [UInt8], width: Int, height: Int, bytesPerRow: Int) {
        let image = try fixtureImage(name, subdirectory: subdirectory)
        guard let cgImage = image.cgImage else {
            throw NSError(domain: "PhoneSaberSenderTests", code: 1,
                          userInfo: [NSLocalizedDescriptionKey: "fixture has no CGImage: \(name).png"])
        }
        let width = cgImage.width, height = cgImage.height, bytesPerRow = width * 4
        var bytes = Array(repeating: UInt8(0), count: bytesPerRow * height)
        let rendered = bytes.withUnsafeMutableBytes { raw -> Bool in
            guard let base = raw.baseAddress,
                  let context = CGContext(
                    data: base, width: width, height: height, bitsPerComponent: 8,
                    bytesPerRow: bytesPerRow, space: CGColorSpaceCreateDeviceRGB(),
                    bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue
                        | CGBitmapInfo.byteOrder32Little.rawValue
                  ) else { return false }
            context.draw(cgImage, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        guard rendered else {
            throw NSError(domain: "PhoneSaberSenderTests", code: 2,
                          userInfo: [NSLocalizedDescriptionKey: "fixture render failed: \(name).png"])
        }
        return (bytes, width, height, bytesPerRow)
    }

    private func writeAnnotatedDiagnostic(
        name: String,
        image: UIImage,
        candidates: [SaberCandidate],
        selected: (PixelPoint, PixelPoint)?
    ) throws {
        let imageWidth = Int(image.size.width)
        let imageHeight = Int(image.size.height)
        let panelWidth = 920
        let rowHeight = 126
        let canvasHeight = max(imageHeight, candidates.count * rowHeight + 36)
        let format = UIGraphicsImageRendererFormat()
        format.scale = 1
        format.opaque = true
        let renderer = UIGraphicsImageRenderer(
            size: CGSize(width: imageWidth + panelWidth, height: canvasHeight),
            format: format
        )
        let annotated = renderer.image { context in
            UIColor(white: 0.08, alpha: 1).setFill()
            context.fill(CGRect(x: 0, y: 0, width: imageWidth + panelWidth, height: canvasHeight))
            image.draw(in: CGRect(x: 0, y: 0, width: imageWidth, height: imageHeight))
            let palette: [UIColor] = [.systemOrange, .systemPink, .systemYellow,
                                      .systemPurple, .systemTeal, .systemRed]
            let titleAttributes: [NSAttributedString.Key: Any] = [
                .font: UIFont.monospacedSystemFont(ofSize: 14, weight: .bold),
                .foregroundColor: UIColor.white
            ]
            let detailAttributes: [NSAttributedString.Key: Any] = [
                .font: UIFont.monospacedSystemFont(ofSize: 11, weight: .regular),
                .foregroundColor: UIColor(white: 0.92, alpha: 1)
            ]
            for (index, candidate) in candidates.enumerated() {
                let isSelected = selected.map { axisDistance(candidate.endpoints, $0) < 1 } ?? false
                let color = isSelected ? UIColor.systemGreen : palette[index % palette.count]
                color.setStroke()
                color.setFill()
                let box = candidate.boundingBox
                let boxPath = UIBezierPath(rect: CGRect(
                    x: box.minX, y: box.minY,
                    width: max(box.maxX - box.minX, 1),
                    height: max(box.maxY - box.minY, 1)
                ))
                boxPath.lineWidth = isSelected ? 4 : 2
                boxPath.stroke()
                let axisPath = UIBezierPath()
                axisPath.move(to: CGPoint(x: candidate.endpoints.0.x, y: candidate.endpoints.0.y))
                axisPath.addLine(to: CGPoint(x: candidate.endpoints.1.x, y: candidate.endpoints.1.y))
                axisPath.lineWidth = isSelected ? 5 : 2
                axisPath.stroke()
                let number = "\(index + 1)" as NSString
                number.draw(at: CGPoint(x: box.minX + 2, y: max(box.minY - 17, 0)),
                            withAttributes: titleAttributes.merging([.foregroundColor: color]) { _, new in new })

                let s = candidate.scoreBreakdown
                let shape = s.length + s.aspect + s.extent + s.widthConsistency + s.area
                let brightness = s.peakBrightness + s.meanBrightness + s.highBrightnessRatio
                    + s.clippedWhite + s.coreSupport + s.longitudinalCoreCoverage
                let heading = String(format: "candidate %02d%@  score=%6.2f  eligible=%@",
                                     index + 1, isSelected ? "  FINAL" : "", candidate.score,
                                     candidate.isEmitterEligible.description)
                let line1 = String(format: "axis=(%d,%d)-(%d,%d) bbox=[%d,%d,%d,%d]",
                                   candidate.endpoints.0.x, candidate.endpoints.0.y,
                                   candidate.endpoints.1.x, candidate.endpoints.1.y,
                                   box.minX, box.minY, box.maxX, box.maxY)
                let line2 = String(format: "shape=%5.2f brightness=%5.2f color=%5.2f contrast=%5.2f texture=%5.2f",
                                   shape, brightness, s.colorPurity, s.localContrast, s.emitterTexture)
                let line3 = String(format: "peak=%3d mean=%5.1f high=%.3f white=%.3f purity=%.3f local=%.3f",
                                   candidate.peakValue, candidate.meanValue,
                                   candidate.highValueRatio, candidate.clippedWhiteRatio,
                                   candidate.meanColorPurity, candidate.localContrast)
                let line4 = "source=\(candidate.source) radiance=\(String(format: "%.3f", candidate.radiance)) " + String(format: "longHigh=%.3f widthVar=%.3f core=%.3f coreLong=%.3f",
                                   candidate.longitudinalHighCoverage, candidate.widthVariation,
                                   candidate.coreSupportRatio, candidate.longitudinalCoreCoverage)
                let x = imageWidth + 18
                let line5 = String(format: "continuity=%.3f gap=%d retained=%.3f radianceContribution=%.2f proposalPenalty=%.2f",
                                   candidate.longitudinalContinuity,
                                   candidate.largestLongitudinalGap,
                                   candidate.retainedBodyRatio,
                                   s.radiance,
                                   s.proposalPenalty)
                let y = 14 + index * rowHeight
                (heading as NSString).draw(at: CGPoint(x: x, y: y),
                                           withAttributes: titleAttributes.merging([.foregroundColor: color]) { _, new in new })
                ([line1, line2, line3, line4, line5].joined(separator: "\n") as NSString).draw(
                    in: CGRect(x: x, y: y + 20, width: panelWidth - 30, height: rowHeight - 20),
                    withAttributes: detailAttributes
                )
            }
        }
        let repository = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        let outputDirectory = repository.appendingPathComponent("tests/debug_detection", isDirectory: true)
        try FileManager.default.createDirectory(at: outputDirectory,
                                                withIntermediateDirectories: true)
        guard let data = annotated.pngData() else {
            throw NSError(domain: "PhoneSaberSenderTests", code: 3,
                          userInfo: [NSLocalizedDescriptionKey: "annotated PNG encoding failed"])
        }
        try data.write(to: outputDirectory.appendingPathComponent("\(name)-annotated.png"),
                       options: .atomic)
    }

    private func candidateSummary(_ candidates: [SaberCandidate]) -> String {
        candidates.map { candidate in
            let a = candidate.endpoints.0, b = candidate.endpoints.1
            let s = candidate.scoreBreakdown
            let box = candidate.boundingBox
            return String(format: "source=%@ (%d,%d)-(%d,%d) box=[%d,%d,%d,%d] score=%.2f eligible=%@ peak=%d mean=%.1f high=%.3f purity=%.3f white=%.3f variation=%.3f contrast=%.3f longitudinal=%.3f widthVar=%.3f core=%.3f coreLong=%.3f continuity=%.3f gap=%d retained=%.3f raw=%.1f robust=%.1f density=%.2f points=%d fallback=%@ parts[len=%.2f aspect=%.2f extent=%.2f width=%.2f area=%.2f peak=%.2f mean=%.2f high=%.2f purity=%.2f contrast=%.2f texture=%.2f white=%.2f longitudinal=%.2f core=%.2f coreLong=%.2f]",
                          candidate.source,
                          a.x, a.y, b.x, b.y,
                          box.minX, box.minY, box.maxX, box.maxY, candidate.score,
                          candidate.isEmitterEligible.description, candidate.peakValue,
                          candidate.meanValue, candidate.highValueRatio,
                          candidate.meanColorPurity, candidate.clippedWhiteRatio,
                          candidate.brightnessVariation, candidate.localContrast,
                          candidate.longitudinalHighCoverage, candidate.widthVariation,
                          candidate.coreSupportRatio, candidate.longitudinalCoreCoverage,
                          candidate.longitudinalContinuity, candidate.largestLongitudinalGap,
                          candidate.retainedBodyRatio,
                          candidate.rawPCASpan, candidate.robustMainIntervalLength,
                          candidate.axialDensity, candidate.pointCount,
                          candidate.usedPointLEDFallback.description,
                          s.length, s.aspect, s.extent, s.widthConsistency, s.area,
                          s.peakBrightness, s.meanBrightness, s.highBrightnessRatio,
                          s.colorPurity, s.localContrast, s.emitterTexture,
                          s.clippedWhite, s.longitudinalHighCoverage,
                          s.coreSupport, s.longitudinalCoreCoverage)
        }.joined(separator: " | ")
    }

    private func axisDistance(_ actual: (PixelPoint, PixelPoint),
                              _ expected: (PixelPoint, PixelPoint)) -> Double {
        func distance(_ lhs: PixelPoint, _ rhs: PixelPoint) -> Double {
            hypot(Double(lhs.x - rhs.x), Double(lhs.y - rhs.y))
        }
        let forward = distance(actual.0, expected.0) + distance(actual.1, expected.1)
        let reversed = distance(actual.0, expected.1) + distance(actual.1, expected.0)
        return min(forward, reversed) / 2.0
    }

    private func axisMidpointDistance(_ actual: (PixelPoint, PixelPoint),
                                      _ expected: (PixelPoint, PixelPoint)) -> Double {
        let actualX = Double(actual.0.x + actual.1.x) / 2.0
        let actualY = Double(actual.0.y + actual.1.y) / 2.0
        let expectedX = Double(expected.0.x + expected.1.x) / 2.0
        let expectedY = Double(expected.0.y + expected.1.y) / 2.0
        return hypot(actualX - expectedX, actualY - expectedY)
    }

    func testProductionVideoFixturesKeepBladeAxesAndRejectFalseCoreLines() throws {
        typealias Axis = (PixelPoint, PixelPoint)
        let fixtures: [(String, SaberColor, Axis)] = [
            ("frame-0000", .red, (PixelPoint(x: 198, y: 463), PixelPoint(x: 274, y: 455))),
            ("frame-0000", .blue, (PixelPoint(x: 187, y: 554), PixelPoint(x: 280, y: 554))),
            ("frame-0033", .red, (PixelPoint(x: 62, y: 403), PixelPoint(x: 119, y: 413))),
            ("frame-0033", .blue, (PixelPoint(x: 117, y: 537), PixelPoint(x: 209, y: 517))),
            ("frame-0067", .red, (PixelPoint(x: 179, y: 420), PixelPoint(x: 258, y: 427))),
            ("frame-0067", .blue, (PixelPoint(x: 92, y: 478), PixelPoint(x: 159, y: 432))),
            ("frame-0120", .red, (PixelPoint(x: 74, y: 403), PixelPoint(x: 99, y: 477))),
            ("frame-0120", .blue, (PixelPoint(x: 139, y: 485), PixelPoint(x: 220, y: 500))),
            ("frame-0136", .red, (PixelPoint(x: 56, y: 399), PixelPoint(x: 77, y: 476))),
            ("frame-0136", .blue, (PixelPoint(x: 116, y: 489), PixelPoint(x: 198, y: 514))),
            ("frame-0140", .red, (PixelPoint(x: 31, y: 373), PixelPoint(x: 61, y: 450))),
            ("frame-0140", .blue, (PixelPoint(x: 68, y: 499), PixelPoint(x: 147, y: 525))),
            ("frame-0220", .red, (PixelPoint(x: 32, y: 420), PixelPoint(x: 56, y: 400))),
            ("frame-0220", .blue, (PixelPoint(x: 29, y: 571), PixelPoint(x: 59, y: 563))),
            ("frame-0397", .red, (PixelPoint(x: 148, y: 310), PixelPoint(x: 194, y: 258))),
            ("frame-0500", .red, (PixelPoint(x: 114, y: 523), PixelPoint(x: 129, y: 618))),
            ("frame-0500", .blue, (PixelPoint(x: 172, y: 452), PixelPoint(x: 212, y: 398))),
            ("frame-0600", .red, (PixelPoint(x: 86, y: 390), PixelPoint(x: 124, y: 294))),
            ("frame-0600", .blue, (PixelPoint(x: 40, y: 546), PixelPoint(x: 206, y: 539))),
            ("frame-0676", .red, (PixelPoint(x: 205, y: 347), PixelPoint(x: 260, y: 284))),
            ("frame-0704", .red, (PixelPoint(x: 136, y: 500), PixelPoint(x: 148, y: 570))),
            ("frame-0747", .red, (PixelPoint(x: 8, y: 517), PixelPoint(x: 11, y: 454))),
        ]
        let subdirectory = "production-video-IMG_5933"
        var cached: [String: (bytes: [UInt8], width: Int, height: Int, bytesPerRow: Int)] = [:]
        for (name, color, expected) in fixtures {
            let image: (bytes: [UInt8], width: Int, height: Int, bytesPerRow: Int)
            if let existing = cached[name] {
                image = existing
            } else {
                image = try fixtureBGRA(name, subdirectory: subdirectory)
                cached[name] = image
            }
            let analysis = analyzeSabers(in: image.bytes, width: image.width, height: image.height,
                                         bytesPerRow: image.bytesPerRow,
                                         redThreshold: ColorThreshold(), blueThreshold: ColorThreshold())
            guard let selected = analysis.selected[color] else {
                XCTFail("\(name) \(color): expected blade was not selected")
                continue
            }
            XCTAssertLessThanOrEqual(axisDistance(selected, expected), 24,
                                     "\(name) \(color): wrong blade axis \(selected)")
        }
    }

    func testRealBlueLEDFixturesPreferEmitterOverCurtainReflection() throws {
        // These are hand-annotated long axes, not positional recognition rules.
        // Both frames contain the same physical blade and a separate blue
        // curtain reflection, at slightly different camera poses.
        let fixtures: [(String, (PixelPoint, PixelPoint), (PixelPoint, PixelPoint))] = [
            ("blue-led-with-curtain-reflection-01",
             (PixelPoint(x: 224, y: 204), PixelPoint(x: 243, y: 371)),
             (PixelPoint(x: 405, y: 324), PixelPoint(x: 418, y: 404))),
            ("blue-led-with-curtain-reflection-02",
             (PixelPoint(x: 218, y: 252), PixelPoint(x: 241, y: 413)),
             (PixelPoint(x: 285, y: 255), PixelPoint(x: 302, y: 416))),
            ("blue-led-with-left-curtain-reflection-03",
             (PixelPoint(x: 193, y: 297), PixelPoint(x: 218, y: 471)),
             (PixelPoint(x: 74, y: 326), PixelPoint(x: 166, y: 568))),
            ("blue-led-with-left-curtain-reflection-04",
             (PixelPoint(x: 188, y: 156), PixelPoint(x: 225, y: 294)),
             (PixelPoint(x: 77, y: 118), PixelPoint(x: 154, y: 361))),
            ("blue-led-bright-large-05",
             (PixelPoint(x: 281, y: 231), PixelPoint(x: 292, y: 365)),
             (PixelPoint(x: 247, y: 226), PixelPoint(x: 262, y: 361)))
        ]
        let threshold = ColorThreshold()

        for (name, expectedLED, expectedReflection) in fixtures {
            let image = try fixtureBGRA(name)
            let analysis = analyzeSabers(in: image.bytes, width: image.width, height: image.height,
                                         bytesPerRow: image.bytesPerRow,
                                         redThreshold: threshold, blueThreshold: threshold)
            let candidates = analysis.candidates[.blue] ?? []
            let ranked = candidates.filter(\.isEmitterEligible)
            if let winner = ranked.first {
                let runner = ranked.dropFirst().first
                print(String(format: "[WinnerMargin] %@ winner=%.2f runner=%.2f margin=%.2f source=%@", name,
                             winner.score, runner?.score ?? 0, winner.score - (runner?.score ?? 0), winner.source))
            }
            try writeAnnotatedDiagnostic(name: name, image: fixtureImage(name),
                                         candidates: candidates, selected: analysis.selected[.blue])
            let summary = "\(name): \(candidateSummary(candidates))"
            for candidate in candidates {
                XCTAssertEqual(candidate.score, candidate.scoreBreakdown.total,
                               accuracy: 0.000_001, summary)
            }
            let selectedText = analysis.selected[.blue].map {
                "(\($0.0.x),\($0.0.y))-(\($0.1.x),\($0.1.y))"
            } ?? "none"
            print("[FixtureRanking] \(summary) selected=\(selectedText)")
            guard let led = candidates.filter({
                let ledDistance = axisMidpointDistance($0.endpoints, expectedLED)
                return ledDistance < 40
                    && ledDistance < axisMidpointDistance($0.endpoints, expectedReflection)
            }).max(by: { $0.score < $1.score }) else {
                XCTFail("actual LED candidate missing; \(summary)")
                continue
            }
            let reflection = candidates.filter({
                let reflectionDistance = axisMidpointDistance($0.endpoints, expectedReflection)
                return reflectionDistance < 40
                    && reflectionDistance < axisMidpointDistance($0.endpoints, expectedLED)
            }).max(by: { $0.score < $1.score })
            if let reflection {
                print(String(format: "[FixtureGroundTruth] %@ LED=%.2f reflection=%.2f",
                             name, led.score, reflection.score))
            }

            XCTAssertLessThan(axisMidpointDistance(led.endpoints, expectedLED), 40, summary)
            if let reflection {
                XCTAssertLessThan(axisMidpointDistance(reflection.endpoints, expectedReflection), 40, summary)
                XCTAssertGreaterThan(led.score, reflection.score, summary)
            }
            XCTAssertTrue(led.isEmitterEligible, summary)
            guard let selected = analysis.selected[.blue] else {
                XCTFail("blue blade was not selected; \(summary)")
                continue
            }
            XCTAssertLessThan(axisMidpointDistance(selected, expectedLED),
                              axisMidpointDistance(selected, expectedReflection), summary)
            XCTAssertLessThan(axisDistance(selected, expectedLED), 75, summary)

            // Compare a lower-resolution recognition pass without changing
            // the production sampleStep. It must still prefer the emitter to
            // the reflection before it can be considered for a device trial.
            let lowerResolution = analyzeSabers(
                in: image.bytes, width: image.width, height: image.height,
                bytesPerRow: image.bytesPerRow,
                redThreshold: threshold, blueThreshold: threshold,
                sampleStep: 3
            )
            guard let lowerSelected = lowerResolution.selected[.blue] else {
                XCTFail("sampleStep=3 lost the LED: \(name)")
                continue
            }
            print(String(format: "[ResolutionComparison] %@ step2Distance=%.2f step3Distance=%.2f",
                         name, axisDistance(selected, expectedLED),
                         axisDistance(lowerSelected, expectedLED)))
            XCTAssertLessThan(axisMidpointDistance(lowerSelected, expectedLED),
                              axisMidpointDistance(lowerSelected, expectedReflection), name)
        }
    }

    func testFirstValidFrameRecognizesImmediateLargePositionChange() {
        let width = 200, height = 120
        func blueFrame(x: Int) -> CMSampleBuffer {
            sampleBuffer(width: width, height: height) { base, stride in
                let pixels = base.assumingMemoryBound(to: UInt8.self)
                for y in 18...102 {
                    for dx in -4...4 {
                        let offset = y * stride + (x + dx) * 4
                        pixels[offset] = 250
                        pixels[offset + 1] = 55
                        pixels[offset + 2] = 25
                        pixels[offset + 3] = 255
                    }
                }
            }
        }

        let processor = FrameProcessor(expiryScheduler: nil)
        var freshCenters: [Double] = []
        processor.onResult = { results, _, _, _, _, _ in
            if let blue = results.first(where: { $0.color == .blue && $0.isFresh }) {
                freshCenters.append(Double(blue.endpoints.0.x + blue.endpoints.1.x) / 2)
            }
        }
        processor.process(blueFrame(x: 30))
        processor.process(blueFrame(x: 168))

        XCTAssertEqual(freshCenters.count, 2)
        XCTAssertEqual(freshCenters[0], 30, accuracy: 8)
        XCTAssertEqual(freshCenters[1], 168, accuracy: 8,
                       "the first valid frame after a large jump must not wait for confirmation")
    }

    func testPerturbedRealFixtureStability() throws {
        let names = ["blue-led-with-curtain-reflection-01", "blue-led-with-curtain-reflection-02",
                     "blue-led-with-left-curtain-reflection-03", "blue-led-with-left-curtain-reflection-04",
                     "blue-led-bright-large-05"]
        for name in names {
            let image = try fixtureBGRA(name)
            var reference: (PixelPoint, PixelPoint)?
            var maxEndpoint = 0.0, maxCenter = 0.0, maxAngle = 0.0
            for frame in 0..<9 {
                var bytes = image.bytes
                let exposure = 1.0 + Double(frame - 4) * 0.005
                for y in 0..<image.height {
                    for x in 0..<image.width {
                        let offset = y * image.bytesPerRow + x * 4
                        for channel in 0..<3 {
                            let noise = (x * 17 + y * 31 + frame * 13 + channel * 7) % 5 - 2
                            bytes[offset + channel] = UInt8(clamping: Int((Double(bytes[offset + channel]) * exposure).rounded()) + noise)
                        }
                    }
                }
                let result = analyzeSabers(in: bytes, width: image.width, height: image.height,
                                          bytesPerRow: image.bytesPerRow,
                                          redThreshold: ColorThreshold(), blueThreshold: ColorThreshold())
                let winner = try XCTUnwrap(result.candidates[.blue]?.first(where: \.isEmitterEligible), name)
                let endpoints = winner.endpoints
                for candidate in result.candidates[.blue] ?? [] { XCTAssertTrue(candidate.score.isFinite, name) }
                let other = result.candidates[.blue]?.filter { $0.isEmitterEligible }.dropFirst().first
                if let previous = reference {
                    maxEndpoint = max(maxEndpoint, axisDistance(endpoints, previous))
                    maxCenter = max(maxCenter, axisMidpointDistance(endpoints, previous))
                    let a = atan2(Double(endpoints.1.y - endpoints.0.y), Double(endpoints.1.x - endpoints.0.x))
                    let b = atan2(Double(previous.1.y - previous.0.y), Double(previous.1.x - previous.0.x))
                    let difference = abs(a - b).truncatingRemainder(dividingBy: .pi)
                    maxAngle = max(maxAngle, min(difference, .pi - difference) * 180 / .pi)
                } else { reference = endpoints }
                let dx = Double(endpoints.1.x - endpoints.0.x), dy = Double(endpoints.1.y - endpoints.0.y)
                let center = "\(Double(endpoints.0.x + endpoints.1.x) / 2),\(Double(endpoints.0.y + endpoints.1.y) / 2)"
                print("[JitterFrame] \(name) frame=\(frame) source=\(winner.source) center=\(center) angle=\(atan2(dy, dx) * 180 / .pi) length=\(hypot(dx, dy)) endpoints=\(endpoints) score=\(winner.score) runner=\(other?.score ?? 0) margin=\(winner.score - (other?.score ?? 0))")
            }
            print(String(format: "[JitterSummary] %@ endpoint=%.2f center=%.2f angle=%.2f", name, maxEndpoint, maxCenter, maxAngle))
            XCTAssertLessThan(maxEndpoint, 12, name)
            XCTAssertLessThan(maxCenter, 10, name)
            XCTAssertLessThan(maxAngle, 3, name)
        }
    }

    func testOfflineBrightFrameTimingBaseline() throws {
        let bright = try fixtureBGRA("blue-led-bright-large-05")
        let blank = Array(repeating: UInt8(0), count: bright.bytes.count)
        let threshold = ColorThreshold()
        func timing(_ bytes: [UInt8], iterations: Int = 12,
                    collectProfile: Bool = false)
            -> (average: Double, median: Double, maximum: Double, p90: Double) {
            var values: [Double] = []
            for _ in 0..<iterations {
                let start = ProcessInfo.processInfo.systemUptime
                _ = analyzeSabers(in: bytes, width: bright.width, height: bright.height,
                                  bytesPerRow: bright.bytesPerRow,
                                  redThreshold: threshold, blueThreshold: threshold,
                                  collectProfile: collectProfile)
                values.append((ProcessInfo.processInfo.systemUptime - start) * 1000)
            }
            let ordered = values.sorted()
            let p90Index = Int(ceil(Double(ordered.count) * 0.9)) - 1
            return (values.reduce(0, +) / Double(values.count), ordered[ordered.count / 2],
                    values.max() ?? 0, ordered[p90Index])
        }
        _ = timing(bright.bytes, iterations: 2)
        let emptyResult = timing(blank)
        let brightResult = timing(bright.bytes)
        let brightProfiledResult = timing(bright.bytes, collectProfile: true)
        print(String(format: "[PerformanceBaseline] empty avg=%.3f median=%.3f max=%.3f ms; bright avg=%.3f median=%.3f max=%.3f ms",
                     emptyResult.average, emptyResult.median, emptyResult.maximum,
                     brightResult.average, brightResult.median, brightResult.maximum))
        print(String(format: "[PerformanceProfileOverhead] bright unprofiled avg=%.3f median=%.3f ms; profiled avg=%.3f median=%.3f ms",
                     brightResult.average, brightResult.median,
                     brightProfiledResult.average, brightProfiledResult.median))
        // A single simulator scheduling pause is not a detector regression;
        // the 90th percentile still catches sustained frame-time overruns.
        XCTAssertLessThan(brightResult.p90, 35)
        // Empty-mask fast paths make the no-target case much cheaper. Compare
        // medians (same margins as before): one host-load pause of ~100 ms in
        // a 12-sample batch shifts an average by ~8 ms, but a sustained
        // per-frame regression still moves the median.
        XCTAssertLessThan(brightResult.median, emptyResult.median + 30.0)
        XCTAssertLessThan(brightProfiledResult.median, brightResult.median * 1.5)

        let profiled = analyzeSabers(in: bright.bytes, width: bright.width, height: bright.height,
                                     bytesPerRow: bright.bytesPerRow,
                                     redThreshold: threshold, blueThreshold: threshold,
                                     collectProfile: true)
        let profile = try XCTUnwrap(profiled.profile)
        print(String(format: "[PerformanceStages] total=%.3f scanHSVMask=%.3f morphology=%.3f componentsScorePCA=%.3f traversal=%.3f shapeAxis=%.3f brightnessContrastColor=%.3f endpointsBounds=%.3f lineProposals=%.3f lineScore=%.3f selection=%.3f pixels=%d cores=%d proposals=%d candidates=%d",
                     profile.totalMs, profile.pixelScanHSVMaskMs, profile.morphologyMs,
                     profile.componentAndScoreMs, profile.componentTraversalAndProposalOverheadMs,
                     profile.shapeAndAxisMs, profile.brightnessContrastColorMs,
                     profile.endpointAndBoundsMs, profile.lineProposalMs, profile.lineScoreMs,
                     profile.selectionMs, profile.colorPixelCount, profile.brightCorePixelCount,
                     profile.lineProposalCount, profile.candidateCount))

        let fixtureTimingSamples = 5
        for name in ["blue-led-with-curtain-reflection-01",
                     "blue-led-with-curtain-reflection-02",
                     "blue-led-with-left-curtain-reflection-03",
                     "blue-led-with-left-curtain-reflection-04",
                     "blue-led-bright-large-05"] {
            let fixture = try fixtureBGRA(name)
            func profiledRun() throws -> SaberDetectionProfile {
                let result = analyzeSabers(in: fixture.bytes, width: fixture.width,
                                           height: fixture.height, bytesPerRow: fixture.bytesPerRow,
                                           redThreshold: threshold, blueThreshold: threshold,
                                           collectProfile: true)
                return try XCTUnwrap(result.profile, name)
            }
            // One unrecorded warm-up, then the median of several runs. A single
            // sample let one host scheduling pause (e.g. 99 ms while other
            // fixtures ran at 20-30 ms) fail the frame budget; the median still
            // fails if the detector's typical cost exceeds the same 50 ms budget.
            _ = try profiledRun()
            let samples = try (0..<fixtureTimingSamples).map { _ in try profiledRun() }
            let totals = samples.map(\.totalMs).sorted()
            let medianTotal = totals[totals.count / 2]
            let fixtureProfile = samples[0]
            print(String(format: "[FixturePerformance] %@ total median=%.3f min=%.3f max=%.3f n=%d proposals=%d candidates=%d",
                         name, medianTotal, totals.first ?? 0, totals.last ?? 0, totals.count,
                         fixtureProfile.lineProposalCount, fixtureProfile.candidateCount))
            XCTAssertLessThan(medianTotal, 50, name)
        }
    }

    func testBuiltApplicationDeclaresBonjourServices() {
        // Inspect the host app's generated plist, not the project settings or
        // the test bundle: the missing generated array caused device discovery failure.
        XCTAssertEqual(Bundle.main.object(forInfoDictionaryKey: "NSBonjourServices") as? [String],
                       ["_phonesaber._udp", "_phonesaber-p2p._udp", "_phonesaber-dp2p._tcp", "_phonesaber-diag._tcp"])
        XCTAssertFalse((Bundle.main.object(forInfoDictionaryKey: "NSLocalNetworkUsageDescription")
                        as? String ?? "").isEmpty)
    }

    func testFrameMailboxProcessesOnlyNewestWaitingFrame() {
        let processor = FrameProcessor(expiryScheduler: nil)
        let delivered = expectation(description: "newest frame delivered")
        delivered.expectedFulfillmentCount = 1
        var results: [[DetectedSaber]] = []
        processor.onResult = { detected, _, _, _, _, _ in
            results.append(detected)
            delivered.fulfill()
        }
        func coloredFrame(_ color: SaberColor) -> CMSampleBuffer {
            sampleBuffer(width: 96, height: 64) { base, stride in
                let pixel = base.assumingMemoryBound(to: UInt8.self)
                for x in 12...82 {
                    for y in 28...36 {
                        let offset = y * stride + x * 4
                        pixel[offset] = color == .blue ? 245 : 35
                        pixel[offset + 1] = 45
                        pixel[offset + 2] = color == .red ? 245 : 35
                    }
                }
            }
        }
        processor.queue.suspend()
        processor.submit(coloredFrame(.red))
        processor.submit(coloredFrame(.red))
        processor.submit(coloredFrame(.blue))
        XCTAssertEqual(processor.replacedPendingFrameCountForTesting, 2)
        processor.queue.resume()
        wait(for: [delivered], timeout: 2)
        XCTAssertEqual(results.count, 1)
        XCTAssertEqual(results[0].count, 1)
        XCTAssertEqual(results[0].first?.color, .blue)
    }

    func testContinuouslyFullMailboxYieldsToGenerationRead() {
        let processor = FrameProcessor(expiryScheduler: nil)
        let frame = sampleBuffer(width: 16, height: 16) { _, _ in }
        let started = expectation(description: "processing started")
        let generationRead = expectation(description: "generation read while mailbox stays full")
        let queuedControl = expectation(description: "queued control runs while mailbox stays full")
        let feedLock = NSLock()
        var keepFeeding = true
        var frameCount = 0
        processor.onResult = { _, _, _, _, _, _ in
            frameCount += 1
            if frameCount == 1 { started.fulfill() }
            feedLock.lock()
            let shouldFeed = keepFeeding
            feedLock.unlock()
            if shouldFeed { processor.submit(frame) }
        }
        processor.submit(frame)
        wait(for: [started], timeout: 2)
        DispatchQueue.global(qos: .userInitiated).async {
            _ = processor.currentGeneration
            generationRead.fulfill()
        }
        processor.queue.async { queuedControl.fulfill() }
        // Safety bound only: a monopolizing drain loop starves both waits for
        // as long as feeding continues, so a longer bound still fails it while
        // tolerating host scheduling delays.
        let result = XCTWaiter.wait(for: [generationRead, queuedControl], timeout: 3)
        feedLock.lock(); keepFeeding = false; feedLock.unlock()
        processor.queue.sync { processor.onResult = nil }
        XCTAssertEqual(result, .completed,
                       "a saturated detector must not block generation checks or queued control")
    }

    func testDebugCameraSampleMailboxCoalescesWhileProcessorQueueIsBusy() {
        let processor = FrameProcessor(expiryScheduler: nil)
        let queueBlocked = expectation(description: "processor queue blocked")
        let releaseQueue = DispatchSemaphore(value: 0)
        processor.queue.async {
            queueBlocked.fulfill()
            releaseQueue.wait()
        }
        wait(for: [queueBlocked], timeout: 1)

        for index in 0..<1_000 {
            processor.recordDebugCameraSample(DebugRecordingCameraSample(
                frameID: UInt64(index), presentationTimeSeconds: Double(index),
                exposureDurationMs: 8, iso: 200,
                whiteBalanceRedGain: 1, whiteBalanceGreenGain: 1, whiteBalanceBlueGain: 1,
                exposureMode: "Auto", whiteBalanceMode: "Auto", focusMode: "Auto",
                lensPosition: 0.5, activeFormat: "640×480",
                activeFormatFPSRanges: "30…60", activeMinFPS: 30, activeMaxFPS: 60
            ))
        }
        XCTAssertEqual(processor.pendingDebugCameraSampleCountForTesting, 1)

        releaseQueue.signal()
        processor.queue.sync {}
        XCTAssertEqual(processor.pendingDebugCameraSampleCountForTesting, 0)
    }

    func testFrameTraceUsesOnlyNonNegativeHostClockDurations() {
        let valid = FrameTrace(sequence: 1, captureHostTime: 10, callbackHostTime: 10.075,
                               processingStart: 10.080, detectionEnd: 10.091,
                               receivedFrames: 1, processedFrames: 1, replacedFrames: 0,
                               inputFrameIntervalStatistics: nil)
        XCTAssertEqual(try XCTUnwrap(valid.cameraAgeMs), 75, accuracy: 0.0001)
        XCTAssertEqual(valid.queueWaitMs, 5, accuracy: 0.0001)
        XCTAssertEqual(valid.detectionMs, 11, accuracy: 0.0001)
        let invalid = FrameTrace(sequence: 2, captureHostTime: 20, callbackHostTime: 19.9,
                                 processingStart: 19.8, detectionEnd: 19.7,
                                 receivedFrames: 2, processedFrames: 2, replacedFrames: 0,
                                 inputFrameIntervalStatistics: nil)
        XCTAssertNil(invalid.cameraAgeMs)
        XCTAssertEqual(invalid.queueWaitMs, 0)
        XCTAssertEqual(invalid.detectionMs, 0)
    }

    func testRawFrameSaveIsExplicitAndOneShot() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberRawFrameTests-\(UUID().uuidString)", isDirectory: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: directory) }
        let processor = FrameProcessor(expiryScheduler: nil, rawFrameDirectory: { directory })
        let saved = expectation(description: "raw frame saved")
        processor.onRawFrameSaved = { result in
            switch result {
            case .success(let url):
                XCTAssertTrue(FileManager.default.fileExists(atPath: url.path))
                XCTAssertGreaterThan((try? Data(contentsOf: url).count) ?? 0, 0)
            case .failure(let error):
                XCTFail("raw frame save failed: \(error)")
            }
            saved.fulfill()
        }
        let frame = sampleBuffer(width: 32, height: 24) { base, stride in
            let pixels = base.assumingMemoryBound(to: UInt8.self)
            for y in 0..<24 {
                for x in 0..<32 {
                    let offset = y * stride + x * 4
                    pixels[offset] = 20; pixels[offset + 1] = 40
                    pixels[offset + 2] = 220; pixels[offset + 3] = 255
                }
            }
        }
        processor.requestRawFrameSave()
        processor.submit(frame)
        wait(for: [saved], timeout: 3)
        processor.submit(frame)
        processor.queue.sync {}
        let files = try FileManager.default.contentsOfDirectory(at: directory,
                                                                 includingPropertiesForKeys: nil)
        XCTAssertEqual(files.count, 1)
    }

    @MainActor
    func testUDPSenderKeepsOnlyLatestWaitingPayloadPerPort() async {
        let completions = CompletionBox()
        let sender = UDPSender { text, port, completion in
            completions.append(port: port, text: text, completion: completion)
        }
        sender.configure(host: "127.0.0.1", ports: [5005])
        var completed: [String] = []
        sender.send("first", to: 5005) { _ in completed.append("first") }
        let firstStarted = await waitUntil { completions.valuesCountForTesting == 1 }
        XCTAssertTrue(firstStarted)
        sender.send("obsolete", to: 5005) { _ in completed.append("obsolete") }
        sender.send("latest", to: 5005) { _ in completed.append("latest") }
        let replaced = await waitUntil { sender.supersededPendingCountForTesting == 1 }
        XCTAssertTrue(replaced)
        XCTAssertEqual(completions.sent.map(\.0), ["first"])
        completions.removeFirst()?.1(.success(10))
        let latestStarted = await waitUntil { completions.valuesCountForTesting == 1 }
        XCTAssertTrue(latestStarted)
        XCTAssertEqual(completions.sent.map(\.0), ["first", "latest"])
        completions.removeFirst()?.1(.success(11))
        let latestCompleted = await waitUntil { completed == ["first", "latest"] }
        XCTAssertTrue(latestCompleted)
    }

    func testBonjourHostSelectionTracksIPv4ChangesForSameService() {
        var selection = DestinationHostSelection()
        XCTAssertTrue(selection.applyBonjour(host: "192.168.1.20", serviceName: "Festival Mac"))
        XCTAssertEqual(selection.host, "192.168.1.20")
        XCTAssertEqual(selection.source, .bonjour)
        XCTAssertTrue(selection.applyBonjour(host: "172.20.10.2", serviceName: "Festival Mac"))
        XCTAssertEqual(selection.host, "172.20.10.2")
        XCTAssertFalse(selection.applyBonjour(host: "10.0.0.8", serviceName: "Other Mac"))
        XCTAssertEqual(selection.host, "172.20.10.2")
    }

    func testManualHostIsNotOverwrittenByBonjour() {
        var selection = DestinationHostSelection()
        selection.setManual("10.0.0.42", resolvedHost: "", serviceName: "")
        XCTAssertFalse(selection.applyBonjour(host: "192.168.1.20", serviceName: "Festival Mac"))
        XCTAssertEqual(selection.host, "10.0.0.42")
        XCTAssertEqual(selection.source, .manual)
    }

    @MainActor
    func testRunningBonjourHostUpdateRebuildsBothDestinations() async {
        let sender = UDPSender { _, _, completion in completion(.success(1)) }
        let viewModel = CameraViewModel(sender: sender)
        viewModel.applyBonjourForTesting(host: "192.168.1.20", serviceName: "Festival Mac")
        viewModel.startForTesting(host: "192.168.1.20", manual: false)
        let redBefore = sender.connectionGenerationForTesting(port: 5005)
        let blueBefore = sender.connectionGenerationForTesting(port: 5006)

        viewModel.applyBonjourForTesting(host: "172.20.10.2", serviceName: "Festival Mac")

        let rebuilt = await waitUntil {
            sender.connectionGenerationForTesting(port: 5005) > redBefore &&
            sender.connectionGenerationForTesting(port: 5006) > blueBefore
        }
        XCTAssertTrue(rebuilt)
        XCTAssertEqual(viewModel.host, "172.20.10.2")
        XCTAssertEqual(viewModel.connectionMode, "Auto (Bonjour)")
        XCTAssertEqual(viewModel.activeDestination, "172.20.10.2:5005 / 172.20.10.2:5006")
        viewModel.stop()
    }

    @MainActor
    func testFailedConnectionReconnectsOnlyFailedPort() async {
        let timing = UDPSender.Timing(waitingTimeout: 0.05, sendWatchdogTimeout: 1,
                                      reconnectInitialDelay: 0.01, reconnectMaximumDelay: 0.02)
        let sender = UDPSender(sendHandler: { _, _, _ in }, timing: timing)
        sender.configure(host: "127.0.0.1")
        let redBefore = sender.connectionGenerationForTesting(port: 5005)
        let blueBefore = sender.connectionGenerationForTesting(port: 5006)

        sender.simulateConnectionStateForTesting(port: 5005, state: .failed, reason: "route lost")

        let reconnected = await waitUntil { sender.reconnectCountForTesting(port: 5005) == 1 }
        XCTAssertTrue(reconnected)
        XCTAssertGreaterThan(sender.connectionGenerationForTesting(port: 5005), redBefore)
        XCTAssertEqual(sender.connectionGenerationForTesting(port: 5006), blueBefore)
        XCTAssertEqual(sender.reconnectCountForTesting(port: 5006), 0)
        sender.stop()
    }

    @MainActor
    func testWaitingTimeoutReconnects() async {
        let timing = UDPSender.Timing(waitingTimeout: 0.02, sendWatchdogTimeout: 1,
                                      reconnectInitialDelay: 0.01, reconnectMaximumDelay: 0.02)
        let sender = UDPSender(sendHandler: { _, _, _ in }, timing: timing)
        sender.configure(host: "127.0.0.1", ports: [5005])
        sender.simulateConnectionStateForTesting(port: 5005, state: .waiting, reason: "no route")
        let reconnected = await waitUntil { sender.reconnectCountForTesting(port: 5005) == 1 }
        XCTAssertTrue(reconnected)
        sender.stop()
    }

    @MainActor
    func testSendWatchdogKeepsLatestAndRejectsStaleCompletion() async {
        let completions = CompletionBox()
        let timing = UDPSender.Timing(waitingTimeout: 1, sendWatchdogTimeout: 0.02,
                                      reconnectInitialDelay: 0.01, reconnectMaximumDelay: 0.02)
        let sender = UDPSender(sendHandler: { text, port, completion in
            completions.append(port: port, text: text, completion: completion)
        }, timing: timing)
        sender.configure(host: "127.0.0.1", ports: [5005])
        var accepted: [String] = []
        sender.send("first", to: 5005) { _ in accepted.append("first") }
        let firstStarted = await waitUntil { completions.valuesCountForTesting == 1 }
        XCTAssertTrue(firstStarted)
        let staleCompletion = completions.removeFirst()?.1
        sender.send("obsolete", to: 5005) { _ in accepted.append("obsolete") }
        sender.send("latest", to: 5005) { _ in accepted.append("latest") }

        let recovered = await waitUntil({ completions.sent.count == 2 }, timeout: 0.5)
        XCTAssertTrue(recovered)
        XCTAssertEqual(completions.sent.map(\.0), ["first", "latest"])
        XCTAssertEqual(sender.watchdogTimeoutCountForTesting(port: 5005), 1)
        let generationAfterRecovery = sender.connectionGenerationForTesting(port: 5005)
        staleCompletion?(.success(10))
        let staleRejected = await waitUntil { sender.rejectedCompletionCountForTesting == 1 }
        XCTAssertTrue(staleRejected)
        XCTAssertEqual(sender.connectionGenerationForTesting(port: 5005), generationAfterRecovery)
        completions.removeFirst()?.1(.success(11))
        let latestAccepted = await waitUntil { accepted == ["latest"] }
        XCTAssertTrue(latestAccepted)
        sender.stop()
    }

    @MainActor
    func testStopCancelsScheduledReconnect() async {
        let timing = UDPSender.Timing(waitingTimeout: 1, sendWatchdogTimeout: 1,
                                      reconnectInitialDelay: 0.05, reconnectMaximumDelay: 0.05)
        let sender = UDPSender(sendHandler: { _, _, _ in }, timing: timing)
        sender.configure(host: "127.0.0.1", ports: [5005])
        sender.simulateConnectionStateForTesting(port: 5005, state: .failed)
        let failureProcessed = await waitUntil { sender.connectionGenerationForTesting(port: 5005) == 1 }
        XCTAssertTrue(failureProcessed)
        sender.stop()
        try? await Task.sleep(for: .milliseconds(100))
        XCTAssertEqual(sender.reconnectCountForTesting(port: 5005), 0)
        XCTAssertEqual(sender.connectionGenerationForTesting(port: 5005), 1)
    }

    @MainActor
    func testForegroundValidationLeavesHealthyPortsAlone() async {
        let sender = UDPSender(sendHandler: { _, _, completion in completion(.success(1)) })
        sender.configure(host: "127.0.0.1")
        let redGeneration = sender.connectionGenerationForTesting(port: 5005)
        let blueGeneration = sender.connectionGenerationForTesting(port: 5006)
        sender.recoverIfNeeded()
        try? await Task.sleep(for: .milliseconds(20))
        XCTAssertEqual(sender.connectionGenerationForTesting(port: 5005), redGeneration)
        XCTAssertEqual(sender.connectionGenerationForTesting(port: 5006), blueGeneration)
        XCTAssertEqual(sender.reconnectCountForTesting(port: 5005), 0)
        XCTAssertEqual(sender.reconnectCountForTesting(port: 5006), 0)
        sender.stop()
    }

    @MainActor
    func testProductionBGRAFrameReachesViewModelEndpointsAndPayload() async {
        let completions = CompletionBox()
        let sender = UDPSender { text, port, completion in
            completions.append(port: port, text: text, completion: completion)
        }
        let processor = FrameProcessor(expiryScheduler: nil)
        let viewModel = CameraViewModel(processor: processor, sender: sender)
        viewModel.startForTesting()
        let buffer = sampleBuffer(width: 96, height: 64) { base, stride in
            func put(_ x: Int, _ y: Int, _ red: UInt8, _ green: UInt8, _ blue: UInt8) {
                let offset = y * stride + x * 4
                let pixel = base.assumingMemoryBound(to: UInt8.self)
                pixel[offset] = blue; pixel[offset + 1] = green; pixel[offset + 2] = red; pixel[offset + 3] = 255
            }
            for i in 0...34 {
                let x = 10 + i * 2, y = 8 + i
                for dx in -2...2 { for dy in -2...2 { put(x + dx, y + dy, 245, 45, 35) } }
            }
            for i in 0...44 {
                let x = 80, y = 10 + i
                for dx in -2...2 { for dy in -2...2 { put(x + dx, y + dy, 35, 45, 245) } }
            }
        }
        processor.process(buffer)
        let detected = await waitUntil {
            viewModel.redDetectionCount == 1 && viewModel.blueDetectionCount == 1 &&
            viewModel.redEndpoints != nil && viewModel.blueEndpoints != nil
        }
        XCTAssertTrue(detected)
        guard let redEndpoints = viewModel.redEndpoints, let blueEndpoints = viewModel.blueEndpoints else { return }
        func assertEndpoints(_ actual: (PixelPoint, PixelPoint), expected: (PixelPoint, PixelPoint), angle: Double) {
            let direct = hypot(Double(actual.0.x - expected.0.x), Double(actual.0.y - expected.0.y))
                + hypot(Double(actual.1.x - expected.1.x), Double(actual.1.y - expected.1.y))
            let reversed = hypot(Double(actual.0.x - expected.1.x), Double(actual.0.y - expected.1.y))
                + hypot(Double(actual.1.x - expected.0.x), Double(actual.1.y - expected.0.y))
            XCTAssertLessThanOrEqual(min(direct, reversed) / 2, 6)
            let actualAngle = atan2(Double(actual.1.y - actual.0.y), Double(actual.1.x - actual.0.x))
            var error = abs(actualAngle - angle).truncatingRemainder(dividingBy: .pi)
            if error > .pi / 2 { error = .pi - error }
            XCTAssertLessThanOrEqual(error, 0.20)
        }
        assertEndpoints(redEndpoints, expected: (PixelPoint(x: 10, y: 8), PixelPoint(x: 78, y: 42)), angle: atan2(34, 68))
        assertEndpoints(blueEndpoints, expected: (PixelPoint(x: 80, y: 10), PixelPoint(x: 80, y: 54)), angle: .pi / 2)
        XCTAssertEqual(viewModel.sourceDimensions.width, 96)
        XCTAssertEqual(viewModel.sourceDimensions.height, 64)
        XCTAssertEqual(viewModel.redAttemptCount, 1)
        XCTAssertEqual(viewModel.blueAttemptCount, 1)
        let queued = await waitUntil { completions.valuesCountForTesting == 2 }
        XCTAssertTrue(queued)
        XCTAssertEqual(completions.sent.count, 2)
        XCTAssertEqual(Set(completions.sent.map(\.1)), Set([5005, 5006]))
        XCTAssertEqual(completions.sent.map(\.1).sorted(), [5005, 5006])
        // The known values below are the rounded scaled coordinates of the
        // oriented component's long-axis endpoints, using
        // (output - 1) / (source - 1). Keep a one-pixel rounding tolerance.
        func assertKnownPayload(_ text: String, known: [Int], tolerance: Int = 1) {
            let values = text.split(separator: ",").compactMap { Int($0) }
            XCTAssertEqual(values.count, 4)
            guard values.count == 4 else { return }
            for (actual, expected) in zip(values, known) {
                XCTAssertLessThanOrEqual(abs(actual - expected), tolerance,
                                         "payload=\(text), expected=\(known)")
            }
        }
        for (text, port) in completions.sent {
            if port == 5005 {
                // Projection onto the PCA/min-area-rectangle long axis gives
                // source endpoints (8, 8)...(76, 40).
                assertKnownPayload(text, known: [162, 137, 1535, 685])
            } else {
                XCTAssertEqual(port, 5006)
                // The blue centerline is x=80, y=10...54. Its +/-2px fill reaches
                // source y=8...56, so independent endpoint scaling yields
                // [1616, 137, 1616, 959].
                assertKnownPayload(text, known: [1616, 137, 1616, 959])
            }
        }
        let first = completions.removeFirst()
        first?.1(.success(10.250))
        let second = completions.removeFirst()
        second?.1(.success(10.251))
        let completed = await waitUntil { viewModel.redCompletedCount == 1 && viewModel.blueCompletedCount == 1 }
        XCTAssertTrue(completed)
        XCTAssertEqual(viewModel.redEndpoints?.0, redEndpoints.0)
        XCTAssertEqual(viewModel.redEndpoints?.1, redEndpoints.1)

        // These expectations are derived from independent source coordinates,
        // not from the detector's endpoint output above.
        XCTAssertEqual(scaledPoint(PixelPoint(x: 10, y: 8), from: (96, 64), to: (960, 540), mirrorX: false, mirrorY: false), PixelPoint(x: 101, y: 68))
        XCTAssertEqual(scaledPoint(PixelPoint(x: 10, y: 8), from: (96, 64), to: (960, 540), mirrorX: true, mirrorY: false), PixelPoint(x: 858, y: 68))
        XCTAssertEqual(scaledPoint(PixelPoint(x: 10, y: 8), from: (96, 64), to: (960, 540), mirrorX: false, mirrorY: true), PixelPoint(x: 101, y: 471))
        XCTAssertEqual(scaledPoint(PixelPoint(x: 10, y: 8), from: (96, 64), to: (960, 540), mirrorX: true, mirrorY: true), PixelPoint(x: 858, y: 471))
        let fillLeft = aspectFillPoint(PixelPoint(x: 0, y: 32), source: (96, 64), view: (400, 400))
        let fillRight = aspectFillPoint(PixelPoint(x: 95, y: 32), source: (96, 64), view: (400, 400))
        XCTAssertEqual(fillLeft.x, -100, accuracy: 0.001)
        XCTAssertEqual(fillLeft.y, 200, accuracy: 0.001)
        XCTAssertEqual(fillRight.x, 493.75, accuracy: 0.001)
        XCTAssertEqual(fillRight.y, 200, accuracy: 0.001)
    }

    @MainActor
    func testFailureHoldAndOldCompletionPreserveLatestPublishedLatency() async {
        let completions = CompletionBox()
        let sender = UDPSender { _, port, completion in
            completions.append(port: port, completion: completion)
        }
        let clock = ManualClock(100)
        let processor = FrameProcessor(clock: { clock.now }, expiryScheduler: nil)
        let viewModel = CameraViewModel(processor: processor, sender: sender)
        viewModel.startForTesting()
        var published: [Double] = []
        let subscription = viewModel.$lastLocalSendMs.sink { value in
            if let value { published.append(value) }
        }
        let endpoints = (PixelPoint(x: 2, y: 3), PixelPoint(x: 12, y: 13))

        viewModel.processDetectedForTesting([(.red, endpoints)], at: clock.now, dimensions: (20, 20))
        let firstQueued = await waitUntil { completions.valuesCountForTesting == 1 }
        XCTAssertTrue(firstQueued)
        completions.removeFirst()?.1(.success(clock.now + 0.125))
        let firstPublished = await waitUntil { viewModel.lastLocalSendMs == 125 }
        XCTAssertTrue(firstPublished)
        let baselinePublishedCount = published.count

        clock.advance(by: 0.1)
        viewModel.processDetectedForTesting([(.red, endpoints)], at: clock.now, dimensions: (20, 20))
        let failedQueued = await waitUntil { completions.valuesCountForTesting == 1 }
        XCTAssertTrue(failedQueued)
        let failedBefore = viewModel.lastLocalSendMs
        completions.removeFirst()?.1(.failure(NSError(domain: "test", code: 1)))
        let failureProcessed = await waitUntil { viewModel.redErrorCount == 1 }
        XCTAssertTrue(failureProcessed)
        XCTAssertEqual(viewModel.lastLocalSendMs, failedBefore)
        XCTAssertEqual(published.count, baselinePublishedCount)

        let endpointBeforeHold = viewModel.redEndpoints
        let attemptsBeforeHold = viewModel.redAttemptCount
        let completionsBeforeHold = completions.valuesCountForTesting
        let publishedBeforeHold = published.count
        let lastLocalSendBeforeHold = viewModel.lastLocalSendMs
        clock.advance(by: 0.1)
        viewModel.processDetectedForTesting([(.red, nil)], at: clock.now, dimensions: (20, 20))
        let holdProcessed = await waitUntil { viewModel.processedFrameCount == 3 }
        XCTAssertTrue(holdProcessed)
        XCTAssertEqual(viewModel.redEndpoints?.0, endpointBeforeHold?.0)
        XCTAssertEqual(viewModel.redEndpoints?.1, endpointBeforeHold?.1)
        XCTAssertEqual(viewModel.redAttemptCount, attemptsBeforeHold + 1)
        XCTAssertEqual(completions.valuesCountForTesting, completionsBeforeHold + 1)
        completions.removeFirst()?.1(.failure(NSError(domain: "prediction-test", code: 2)))
        let predictedFailureProcessed = await waitUntil { viewModel.redErrorCount == 2 }
        XCTAssertTrue(predictedFailureProcessed)
        XCTAssertEqual(published.count, publishedBeforeHold)
        XCTAssertEqual(viewModel.lastLocalSendMs, lastLocalSendBeforeHold)
        XCTAssertEqual(viewModel.lastLocalSendMs, failedBefore)
        XCTAssertEqual(published.count, baselinePublishedCount)

        clock.advance(by: 0.1)
        viewModel.processDetectedForTesting([(.red, endpoints)], at: clock.now, dimensions: (20, 20))
        let oldQueued = await waitUntil { completions.valuesCountForTesting == 1 }
        XCTAssertTrue(oldQueued)
        let oldCompletion = completions.removeFirst()
        viewModel.stop(); viewModel.startForTesting()
        clock.advance(by: 99.7)
        viewModel.processDetectedForTesting([(.red, endpoints)], at: clock.now, dimensions: (20, 20))
        let newQueued = await waitUntil { completions.valuesCountForTesting == 1 }
        XCTAssertTrue(newQueued)
        let newCompletion = completions.removeFirst()
        newCompletion?.1(.success(clock.now + 0.250))
        let newCompletionProcessed = await waitUntil { viewModel.lastLocalSendMs == 250 }
        XCTAssertTrue(newCompletionProcessed)
        oldCompletion?.1(.success(999))
        let oldCompletionRejected = await waitUntil { sender.rejectedCompletionCountForTesting == 1 }
        XCTAssertTrue(oldCompletionRejected)
        XCTAssertEqual(viewModel.lastLocalSendMs, 250)
        _ = subscription
    }

    @MainActor
    func testSuccessfulCompletionPublishesLatestLocalLatencyWithoutAnotherFrame() async {
        let completions = CompletionBox()
        let sender = UDPSender { _, port, completion in completions.append(port: port, completion: completion) }
        let viewModel = CameraViewModel(sender: sender)
        viewModel.startForTesting()
        let endpoints = (PixelPoint(x: 2, y: 3), PixelPoint(x: 12, y: 13))
        var published: [Double] = []
        let subscription = viewModel.$lastLocalSendMs.sink { value in
            if let value { published.append(value) }
        }
        viewModel.processDetectedForTesting([(.red, endpoints)], at: 100, dimensions: (20, 20))
        let firstQueued = await waitUntil { completions.valuesCountForTesting == 1 }
        XCTAssertTrue(firstQueued)
        completions.removeFirst()?.1(.success(100.125))
        let firstPublished = await waitUntil { viewModel.lastLocalSendMs != nil }
        XCTAssertTrue(firstPublished)
        guard let firstValue = viewModel.lastLocalSendMs, let firstNotification = published.last else { return }
        XCTAssertEqual(firstValue, 125, accuracy: 0.000001)
        XCTAssertEqual(firstNotification, 125, accuracy: 0.000001)
        viewModel.processDetectedForTesting([(.red, endpoints)], at: 200, dimensions: (20, 20))
        let secondQueued = await waitUntil { completions.valuesCountForTesting == 1 }
        XCTAssertTrue(secondQueued)
        completions.removeFirst()?.1(.success(200.031))
        let latestPublished = await waitUntil { viewModel.lastLocalSendMs != nil && abs(viewModel.lastLocalSendMs! - 31) < 0.000001 }
        XCTAssertTrue(latestPublished)
        guard let latestValue = viewModel.lastLocalSendMs else { return }
        XCTAssertEqual(latestValue, 31, accuracy: 0.000001)
        let publishedBoth = await waitUntil { published.count == 2 }
        XCTAssertTrue(publishedBoth)
        XCTAssertEqual(published[0], 125, accuracy: 0.000001)
        XCTAssertEqual(published[1], 31, accuracy: 0.000001)
        _ = subscription
    }

    func testBrightRedAndBluePixels() {
        let threshold = ColorThreshold(brightness: 170, dominance: 35)
        XCTAssertTrue(isBright(255, 20, 20, color: .red, threshold: threshold))
        XCTAssertTrue(isBright(20, 20, 255, color: .blue, threshold: threshold))
        XCTAssertFalse(isBright(150, 120, 120, color: .red, threshold: threshold))
    }

    func testBrightColorDoesNotOverflowWithHighOtherChannels() {
        let threshold = ColorThreshold(brightness: 170, dominance: 35)
        XCTAssertFalse(isBright(255, 250, 250, color: .red, threshold: threshold))
        XCTAssertFalse(isBright(250, 250, 255, color: .blue, threshold: threshold))
    }

    func testColorNeedsSaturationSoWhiteHighlightsAreIgnored() {
        let threshold = ColorThreshold(brightness: 140, dominance: 20)
        XCTAssertFalse(isBright(255, 250, 250, color: .red, threshold: threshold))
        XCTAssertTrue(isBright(235, 90, 100, color: .red, threshold: threshold))
    }

    func testDetectionFindsPrincipalAxisEndpoints() {
        let width = 8, height = 6, stride = width * 4
        var bytes = Array(repeating: UInt8(0), count: stride * height)
        for x in 1...6 {
            let offset = 3 * stride + x * 4
            bytes[offset] = 20; bytes[offset + 1] = 20; bytes[offset + 2] = 255; bytes[offset + 3] = 255
        }
        let result = detectSaber(in: bytes, width: width, height: height, bytesPerRow: stride, color: .red, threshold: ColorThreshold(brightness: 170, dominance: 35), sampleStep: 1)
        XCTAssertEqual(result?.0, PixelPoint(x: 1, y: 3))
        XCTAssertEqual(result?.1, PixelPoint(x: 6, y: 3))
    }

    func testMediumBrightnessElongatedReflectionIsRejectedForBothColors() {
        let width = 120, height = 64, rowBytes = width * 4
        for color in [SaberColor.red, .blue] {
            var bytes = Array(repeating: UInt8(0), count: rowBytes * height)
            for x in 8...110 {
                for y in 28...34 {
                    let offset = y * rowBytes + x * 4
                    bytes[offset] = color == .blue ? 214 : 70
                    bytes[offset + 1] = 80
                    bytes[offset + 2] = color == .red ? 214 : 70
                    bytes[offset + 3] = 255
                }
            }
            XCTAssertNil(detectSaber(in: bytes, width: width, height: height,
                                     bytesPerRow: rowBytes, color: color,
                                     threshold: ColorThreshold(brightness: 140, dominance: 20)),
                         "a long smooth \(color) reflection is not an emitter")
        }
    }

    func testBrightGappedEmitterWinsAgainstLongerReflectionForBothColors() {
        let width = 160, height = 80, rowBytes = width * 4
        for color in [SaberColor.red, .blue] {
            var bytes = Array(repeating: UInt8(0), count: rowBytes * height)
            func put(_ x: Int, _ y: Int, _ red: UInt8, _ green: UInt8, _ blue: UInt8) {
                guard x >= 0, x < width, y >= 0, y < height else { return }
                let offset = y * rowBytes + x * 4
                bytes[offset] = blue; bytes[offset + 1] = green
                bytes[offset + 2] = red; bytes[offset + 3] = 255
            }
            // Longer, smoother curtain reflection.
            for x in 5...152 {
                for y in 60...66 {
                    put(x, y, color == .red ? 214 : 70, 80, color == .blue ? 214 : 70)
                }
            }
            // Shorter LED emitter with a dark gap and a clipped-white gap.
            for x in 20...118 {
                for y in 16...24 {
                    put(x, y, color == .red ? 248 : 32, 42, color == .blue ? 248 : 32)
                }
            }
            for x in 50...52 { for y in 14...26 { put(x, y, 0, 0, 0) } }
            for x in 84...86 { for y in 14...26 { put(x, y, 255, 255, 255) } }

            let result = detectSaber(in: bytes, width: width, height: height,
                                     bytesPerRow: rowBytes, color: color,
                                     threshold: ColorThreshold(brightness: 140, dominance: 20))
            XCTAssertNotNil(result)
            guard let result else { continue }
            XCTAssertLessThan(abs(result.0.y - 20), 6)
            XCTAssertLessThan(abs(result.1.y - 20), 6)
            XCTAssertGreaterThan(abs(result.1.x - result.0.x), 80)
        }
    }

    func testBlueDiagonalBarSurvivesBlurAndShortNoise() {
        let width = 40, height = 30, rowBytes = width * 4
        var bytes = Array(repeating: UInt8(0), count: rowBytes * height)
        for i in 3...24 {
            let x = 6 + i / 2, y = 3 + i
            let offset = y * rowBytes + x * 4
            bytes[offset] = 245; bytes[offset + 1] = 70; bytes[offset + 2] = 30
            if i.isMultiple(of: 4), x + 1 < width {
                bytes[y * rowBytes + (x + 1) * 4] = 180
                bytes[y * rowBytes + (x + 1) * 4 + 1] = 60
                bytes[y * rowBytes + (x + 1) * 4 + 2] = 30
            }
        }
        let result = detectSaber(in: bytes, width: width, height: height, bytesPerRow: rowBytes, color: .blue, threshold: ColorThreshold(brightness: 140, dominance: 20), sampleStep: 1)
        XCTAssertNotNil(result)
        XCTAssertGreaterThan(abs(result!.1.x - result!.0.x), 8)
        XCTAssertGreaterThan(abs(result!.1.y - result!.0.y), 12)
    }

    func testLargestColorClusterConnectsHorizontalNeighbors() {
        let cluster = largestColorCluster(in: [
            PixelPoint(x: 1, y: 2),
            PixelPoint(x: 2, y: 2),
            PixelPoint(x: 3, y: 2)
        ], width: 8, height: 6)

        XCTAssertEqual(cluster.count, 3)
    }

    func testLargestColorClusterDoesNotWrapAtImageEdges() {
        let cluster = largestColorCluster(in: [
            PixelPoint(x: 0, y: 1),
            PixelPoint(x: 2, y: 0)
        ], width: 3, height: 2)

        XCTAssertEqual(cluster.count, 1)
    }

    func testBestColorClusterPrefersLongThinBarOverShortBlob() {
        let bar = (0..<18).map { PixelPoint(x: $0 + 4, y: 8) }
        let blob = [PixelPoint(x: 28, y: 5), PixelPoint(x: 29, y: 5), PixelPoint(x: 28, y: 6), PixelPoint(x: 29, y: 6), PixelPoint(x: 30, y: 5), PixelPoint(x: 30, y: 6)]
        let result = bestColorCluster(in: bar + blob, width: 40, height: 20)
        XCTAssertEqual(result.count, bar.count)
        XCTAssertEqual(principalAxisEndpoints(result)?.0, PixelPoint(x: 4, y: 8))
        XCTAssertEqual(principalAxisEndpoints(result)?.1, PixelPoint(x: 21, y: 8))
    }

    func testCandidateEndpointsUseDenseContinuousBodyInsteadOfAxialSpillAndReflection() throws {
        var points: [PixelPoint] = []
        for x in 20...100 {
            for y in 45...55 { points.append(PixelPoint(x: x, y: y)) }
        }
        for x in 101...215 { points.append(PixelPoint(x: x, y: 50)) }
        for x in 216...228 {
            for y in 47...53 { points.append(PixelPoint(x: x, y: y)) }
        }

        let untrimmed = try XCTUnwrap(principalAxisEndpoints(points))
        let candidate = try XCTUnwrap(saberCandidate(from: points, width: 260, height: 100))
        let untrimmedLength = hypot(Double(untrimmed.1.x - untrimmed.0.x),
                                    Double(untrimmed.1.y - untrimmed.0.y))
        let finalLength = hypot(Double(candidate.endpoints.1.x - candidate.endpoints.0.x),
                                Double(candidate.endpoints.1.y - candidate.endpoints.0.y))

        XCTAssertGreaterThan(untrimmedLength, 190, "raw projection reproduces the long-line failure")
        XCTAssertLessThan(finalLength, 100, "distant reflection must not stretch final endpoints")
        XCTAssertGreaterThan(candidate.longitudinalContinuity, 0.90)
        XCTAssertLessThan(candidate.retainedBodyRatio, 0.90)
    }

    func testLongCoreLineGeometryPenaltyPrefersCompleteBladeEvidence() throws {
        var bridged: [PixelPoint] = []
        for x in 20...70 {
            for y in 47...53 { bridged.append(PixelPoint(x: x, y: y)) }
        }
        for x in 71...225 { bridged.append(PixelPoint(x: x, y: 50)) }
        var complete: [PixelPoint] = []
        for x in 20...105 {
            for y in 17...23 { complete.append(PixelPoint(x: x, y: y)) }
        }
        let falseLine = try XCTUnwrap(saberCandidate(
            from: bridged, width: 260, height: 100, source: "core-line"
        ))
        let completeBlade = try XCTUnwrap(saberCandidate(
            from: complete, width: 260, height: 100, source: "color-mask"
        ))
        let penalty = coreLineRobustGeometryPenalty(
            falseLine, minimumArea: 13, minimumFrameDimension: 100
        )
        XCTAssertGreaterThan(penalty, 10)
        XCTAssertLessThan(falseLine.score - penalty, completeBlade.score)

        let coherentLine = try XCTUnwrap(saberCandidate(
            from: complete, width: 260, height: 100, source: "core-line"
        ))
        XCTAssertLessThan(coreLineRobustGeometryPenalty(
            coherentLine, minimumArea: 13, minimumFrameDimension: 100
        ), 1)
    }

    func testForensicDiffuserFramesUseContinuousRobustBodyInsteadOfRawSpan() throws {
        let cases: [(String, SaberColor, Double)] = [
            ("frame_941", .blue, 428.356),
            ("frame_1000", .red, 415.783),
            ("frame_1026", .blue, 484.768),
            ("frame_1048", .red, 473.009),
            ("frame_1073", .blue, 305.196)
        ]
        for (name, color, expectedRawSpan) in cases {
            let fixture = try fixtureBGRA(name, subdirectory: "forensic-20260921")
            let analysis = analyzeSabers(
                in: fixture.bytes, width: fixture.width, height: fixture.height,
                bytesPerRow: fixture.bytesPerRow,
                redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(),
                collectProfile: false
            )
            let candidates = analysis.candidates[color] ?? []
            let selected = try XCTUnwrap(candidates.first(where: \.isEmitterEligible), name)
            let bridgedCandidate = candidates.first(where: {
                $0.source.hasPrefix("core-line") && $0.rawPCASpan >= expectedRawSpan * 0.75
            })
            let length = hypot(Double(selected.endpoints.1.x - selected.endpoints.0.x),
                               Double(selected.endpoints.1.y - selected.endpoints.0.y))
            print(String(format: "[ForensicAfter] %@ %@ length=%.3f raw=%.3f robust=%.3f continuity=%.3f density=%.3f retained=%.3f source=%@ fallback=%@",
                         name, String(describing: color), length, selected.rawPCASpan,
                         selected.robustMainIntervalLength, selected.longitudinalContinuity,
                         selected.axialDensity, selected.retainedBodyRatio, selected.source,
                         selected.usedPointLEDFallback.description))
            if let bridgedCandidate {
                XCTAssertGreaterThan(coreLineRobustGeometryPenalty(
                    bridgedCandidate,
                    minimumArea: max(4, (fixture.width / 2) * (fixture.height / 2) / 2_000),
                    minimumFrameDimension: min(fixture.width, fixture.height) / 2
                ), 0, name)
                XCTAssertGreaterThanOrEqual(selected.score, bridgedCandidate.score, name)
            }
            XCTAssertLessThan(selected.rawPCASpan, expectedRawSpan * 0.5, name)
            XCTAssertLessThan(length, 160, name)
        }
    }

    func testPointLEDFixtureFallbackCharacteristics() throws {
        for name in ["blue-led-with-curtain-reflection-01",
                     "blue-led-with-curtain-reflection-02"] {
            let fixture = try fixtureBGRA(name)
            let analysis = analyzeSabers(
                in: fixture.bytes, width: fixture.width, height: fixture.height,
                bytesPerRow: fixture.bytesPerRow,
                redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(),
                collectProfile: false
            )
            let selected = try XCTUnwrap(
                (analysis.candidates[.blue] ?? []).first(where: \.isEmitterEligible), name
            )
            print(String(format: "[PointLED] %@ raw=%.3f robust=%.3f ratio=%.3f continuity=%.3f density=%.3f retained=%.3f source=%@ fallback=%@",
                         name, selected.rawPCASpan, selected.robustMainIntervalLength,
                         selected.robustMainIntervalLength / max(selected.rawPCASpan, 1),
                         selected.longitudinalContinuity, selected.axialDensity,
                         selected.retainedBodyRatio, selected.source,
                         selected.usedPointLEDFallback.description))
            XCTAssertTrue(selected.usedPointLEDFallback, name)
            XCTAssertGreaterThan(selected.retainedBodyRatio, 0.95, name)
        }
    }

    func testSecondForensicFallback() throws {
        let cases: [(String, SaberColor, Double)] = [
            ("frame_519", .blue, 100),
            ("frame_919", .red, 60),
            ("frame_1091", .red, 70),
            ("frame_1091", .blue, 180)
        ]
        for (name, color, maximumLength) in cases {
            let fixture = try fixtureBGRA(name, subdirectory: "forensic-20260921-211845")
            let analysis = analyzeSabers(
                in: fixture.bytes, width: fixture.width, height: fixture.height,
                bytesPerRow: fixture.bytesPerRow,
                redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(),
                collectProfile: false
            )
            let selected = try XCTUnwrap(
                (analysis.candidates[color] ?? []).first(where: \.isEmitterEligible), name
            )
            let length = hypot(Double(selected.endpoints.1.x - selected.endpoints.0.x),
                               Double(selected.endpoints.1.y - selected.endpoints.0.y))
            let bodyPointCount = Int((Double(selected.pointCount)
                * selected.retainedBodyRatio).rounded())
            print(String(format: "[SecondForensicAfter] %@ %@ length=%.3f raw=%.3f robust=%.3f continuity=%.3f density=%.3f gap=%d bodyPoints=%d retained=%.3f fallback=%@",
                         name, String(describing: color), length, selected.rawPCASpan,
                         selected.robustMainIntervalLength, selected.longitudinalContinuity,
                         selected.axialDensity, selected.largestLongitudinalGap,
                         bodyPointCount, selected.retainedBodyRatio,
                         selected.usedPointLEDFallback.description))
            XCTAssertLessThan(length, maximumLength, name)
            if !selected.usedPointLEDFallback {
                XCTAssertEqual(length, selected.robustMainIntervalLength, accuracy: 12, name)
            }
        }
    }

    func testScaleAndPayload() {
        let value = payload(for: (PixelPoint(x: 0, y: 0), PixelPoint(x: 639, y: 479)), source: (640, 480), output: (1920, 1080), mirrorX: false, mirrorY: false)
        XCTAssertEqual(value, "0,0,1919,1079")
        let mirrored = payload(for: (PixelPoint(x: 0, y: 0), PixelPoint(x: 639, y: 479)), source: (640, 480), output: (1920, 1080), mirrorX: true, mirrorY: true)
        XCTAssertEqual(mirrored, "1919,1079,0,0")
    }

    func testTimestampedPayloadHasExplicitPrefix() {
        XCTAssertEqual(timestampedPayload("1,2,3,4", timestamp: 123.5), "ts=123.500000;1,2,3,4")
    }

    func testDiagonalBarSurvivesShortNoiseAndWhiteReflection() {
        let width = 32, height = 24, rowBytes = width * 4
        var bytes = Array(repeating: UInt8(0), count: rowBytes * height)
        for i in 2...20 {
            let x = 4 + i / 2, y = 2 + i
            let offset = y * rowBytes + x * 4
            bytes[offset] = 25; bytes[offset + 1] = 45; bytes[offset + 2] = 245; bytes[offset + 3] = 255
        }
        // 棒を描いた後、実際にsampleStep=1で読む棒上へ白反射を重ねる。
        for point in [PixelPoint(x: 5, y: 5), PixelPoint(x: 6, y: 6), PixelPoint(x: 7, y: 7)] {
            let offset = point.y * rowBytes + point.x * 4
            bytes[offset] = 250; bytes[offset + 1] = 250; bytes[offset + 2] = 250
        }
        let result = detectSaber(in: bytes, width: width, height: height, bytesPerRow: rowBytes, color: .red, threshold: ColorThreshold(brightness: 140, dominance: 20), sampleStep: 1)
        XCTAssertNotNil(result)
        XCTAssertGreaterThan(abs((result!.1.x - result!.0.x)), 6)
        XCTAssertGreaterThan(abs((result!.1.y - result!.0.y)), 10)
    }

    func testAspectFillCentersAndCropsSource() {
        let point = aspectFillPoint(PixelPoint(x: 0, y: 240), source: (640, 480), view: (280, 280))
        XCTAssertEqual(point.x, -46.6666666667, accuracy: 0.01)
        XCTAssertEqual(point.y, 140, accuracy: 0.01)
    }

    func testAspectFillCropsTopAndBottomForWideView() {
        let point = aspectFillPoint(PixelPoint(x: 320, y: 0), source: (640, 480), view: (400, 200))
        XCTAssertEqual(point.x, 200, accuracy: 0.01)
        XCTAssertEqual(point.y, -50, accuracy: 0.01)
    }

    func testRedAndBlueBarsWithBlurReflectionNoiseUseProductionSampleStep() {
        let width = 48, height = 32, rowBytes = width * 4
        let threshold = ColorThreshold(brightness: 140, dominance: 20)

        func image(for color: SaberColor) -> [UInt8] {
            var bytes = Array(repeating: UInt8(0), count: rowBytes * height)
            func put(_ x: Int, _ y: Int, _ red: UInt8, _ green: UInt8, _ blue: UInt8) {
                guard x >= 0, x < width, y >= 0, y < height else { return }
                let offset = y * rowBytes + x * 4
                bytes[offset] = blue; bytes[offset + 1] = green; bytes[offset + 2] = red; bytes[offset + 3] = 255
            }
            // sampleStep=2 reads even source coordinates. Keep the bar continuous
            // on that sample grid so reflections cannot turn it into a short blob.
            for i in 0...10 {
                let x = 10 + i * 2, y = 6 + i * 2
                let channels: (UInt8, UInt8, UInt8) = color == .red ? (245, 55, 45) : (45, 55, 245)
                put(x, y, channels.0, channels.1, channels.2)
            }
            // Blur spills into neighboring source pixels, including sample positions.
            for i in 0...10 {
                let x = 10 + i * 2, y = 6 + i * 2
                let channels: (UInt8, UInt8, UInt8) = color == .red ? (205, 45, 40) : (40, 45, 205)
                put(x + 1, y, channels.0, channels.1, channels.2)
                put(x, y + 1, channels.0, channels.1, channels.2)
            }
            // White reflections cover real sample positions. Blur at neighboring
            // sample positions keeps each gap connected without changing detector thresholds.
            let barChannels: (UInt8, UInt8, UInt8) = color == .red ? (205, 45, 40) : (40, 45, 205)
            for point in [(14, 10), (22, 18), (30, 26)] {
                put(point.0 - 2, point.1, barChannels.0, barChannels.1, barChannels.2)
                put(point.0, point.1 - 2, barChannels.0, barChannels.1, barChannels.2)
                put(point.0, point.1, 255, 255, 255)
            }
            for point in [(40, 2), (41, 2), (40, 3)] {
                let channels: (UInt8, UInt8, UInt8) = color == .red ? (245, 55, 45) : (45, 55, 245)
                put(point.0, point.1, channels.0, channels.1, channels.2)
            }
            return bytes
        }

        let red = detectSaber(in: image(for: .red), width: width, height: height, bytesPerRow: rowBytes, color: .red, threshold: threshold, sampleStep: 2)
        let blue = detectSaber(in: image(for: .blue), width: width, height: height, bytesPerRow: rowBytes, color: .blue, threshold: threshold, sampleStep: 2)
        XCTAssertNotNil(red)
        XCTAssertNotNil(blue)
        XCTAssertGreaterThan(abs((red!.1.x - red!.0.x)), 12)
        XCTAssertGreaterThan(abs((red!.1.y - red!.0.y)), 8)
        XCTAssertGreaterThan(abs((blue!.1.x - blue!.0.x)), 12)
        XCTAssertGreaterThan(abs((blue!.1.y - blue!.0.y)), 8)
        XCTAssertLessThan(red!.0.x, red!.1.x)
        XCTAssertLessThan(red!.0.y, red!.1.y)
        XCTAssertLessThan(blue!.0.x, blue!.1.x)
        XCTAssertLessThan(blue!.0.y, blue!.1.y)
        XCTAssertGreaterThanOrEqual(red!.0.x, 8)
        XCTAssertLessThanOrEqual(red!.1.x, 34)
        XCTAssertGreaterThanOrEqual(blue!.0.y, 4)
        XCTAssertLessThanOrEqual(blue!.1.y, 30)
    }

    func testAspectFillCoordinatesIncreaseRightAndDown() {
        let origin = aspectFillPoint(PixelPoint(x: 10, y: 20), source: (640, 480), view: (640, 480))
        let right = aspectFillPoint(PixelPoint(x: 11, y: 20), source: (640, 480), view: (640, 480))
        let down = aspectFillPoint(PixelPoint(x: 10, y: 21), source: (640, 480), view: (640, 480))
        XCTAssertGreaterThan(right.x, origin.x)
        XCTAssertEqual(right.y, origin.y, accuracy: 0.001)
        XCTAssertGreaterThan(down.y, origin.y)
        XCTAssertEqual(down.x, origin.x, accuracy: 0.001)
    }

    func testHoldReportsStaleEndpointWithManualClockWithoutSchedulingATimer() {
        let clock = ManualClock(100)
        let processor = FrameProcessor(clock: { clock.now }, expiryScheduler: nil)
        var results: [[DetectedSaber]] = []
        processor.onResult = { detected, _, _, _, _, _ in results.append(detected) }
        let endpoints = (PixelPoint(x: 2, y: 3), PixelPoint(x: 12, y: 13))

        processor.processDetectedForTesting([(.red, endpoints)], at: clock.now, dimensions: (20, 20))
        clock.advance(by: 0.10)
        processor.processDetectedForTesting([(.red, nil)], at: clock.now, dimensions: (20, 20))

        XCTAssertEqual(results.last?.count, 1)
        XCTAssertEqual(results.last?.first?.endpoints.0, endpoints.0)
        XCTAssertEqual(results.last?.first?.isFresh, false)
    }

    @MainActor
    func testHoldDoesNotSendOrChangeCountersAndRecoversEachPortIndependently() async {
        let clock = ManualClock(100)
        let processor = FrameProcessor(clock: { clock.now }, expiryScheduler: nil)
        var outcomes: [Int: [Result<TimeInterval, Error>]] = [
            5005: [.failure(NSError(domain: "test", code: 1)), .success(20)],
            5006: [.failure(NSError(domain: "test", code: 2)), .success(21)]
        ]
        let sender = UDPSender { _, port, completion in
            completion(outcomes[port, default: [.success(ProcessInfo.processInfo.systemUptime)]].removeFirst())
        }
        let viewModel = CameraViewModel(processor: processor, sender: sender)
        viewModel.startForTesting()
        viewModel.measurementMode = true
        sender.setStateForTesting(port: 5005, state: "failed", error: "赤ポート障害")
        let redFailed = await waitUntil { viewModel.status == "接続エラー" }; XCTAssertTrue(redFailed)
        XCTAssertEqual(viewModel.status, "接続エラー")
        sender.setStateForTesting(port: 5005, state: "ready", error: nil)
        sender.setStateForTesting(port: 5006, state: "failed", error: "青ポート障害")
        let blueFailed = await waitUntil { viewModel.senderStates[5006] == "failed" && viewModel.senderErrors[5006] == "青ポート障害" }; XCTAssertTrue(blueFailed)
        XCTAssertEqual(viewModel.status, "接続エラー")
        sender.setStateForTesting(port: 5006, state: "ready", error: nil)
        let ready = await waitUntil { viewModel.senderStates[5005] == "ready" && viewModel.senderStates[5006] == "ready" && viewModel.senderErrors.isEmpty }; XCTAssertTrue(ready)
        XCTAssertEqual(viewModel.status, "送信中")
        XCTAssertEqual(viewModel.status, "送信中")
        let endpoints = (PixelPoint(x: 2, y: 3), PixelPoint(x: 12, y: 13))
        viewModel.processDetectedForTesting([(.red, endpoints), (.blue, endpoints)], at: clock.now, dimensions: (20, 20))
        let completedOrFailed = await waitUntil { viewModel.redErrorCount == 1 && viewModel.blueErrorCount == 1 }; XCTAssertTrue(completedOrFailed)
        XCTAssertEqual(viewModel.redAttemptCount, 1)
        XCTAssertEqual(viewModel.blueAttemptCount, 1)
        XCTAssertEqual(viewModel.redErrorCount, 1)
        XCTAssertEqual(viewModel.blueErrorCount, 1)
        XCTAssertEqual(viewModel.status, "送信エラー")
        XCTAssertNil(viewModel.lastLocalSendMs)

        let redDetectionBeforeHold = viewModel.redDetectionCount
        let blueDetectionBeforeHold = viewModel.blueDetectionCount
        let redAttemptBeforeHold = viewModel.redAttemptCount
        let blueAttemptBeforeHold = viewModel.blueAttemptCount
        let redCompletedBeforeHold = viewModel.redCompletedCount
        let blueCompletedBeforeHold = viewModel.blueCompletedCount
        let redErrorBeforeHold = viewModel.redErrorCount
        let blueErrorBeforeHold = viewModel.blueErrorCount
        let lastSentEpochBeforeHold = viewModel.lastSentEpoch
        XCTAssertNotNil(lastSentEpochBeforeHold)

        clock.advance(by: 0.10)
        viewModel.processDetectedForTesting([(.red, nil), (.blue, nil)], at: clock.now, dimensions: (20, 20))
        let held = await waitUntil { viewModel.processedFrameCount == 2 }; XCTAssertTrue(held)
        XCTAssertEqual(viewModel.redEndpoints?.0, endpoints.0)
        XCTAssertEqual(viewModel.blueEndpoints?.1, endpoints.1)
        XCTAssertEqual(viewModel.redDetectionCount, redDetectionBeforeHold)
        XCTAssertEqual(viewModel.blueDetectionCount, blueDetectionBeforeHold)
        XCTAssertEqual(viewModel.redAttemptCount, redAttemptBeforeHold)
        XCTAssertEqual(viewModel.blueAttemptCount, blueAttemptBeforeHold)
        XCTAssertEqual(viewModel.redCompletedCount, redCompletedBeforeHold)
        XCTAssertEqual(viewModel.blueCompletedCount, blueCompletedBeforeHold)
        XCTAssertEqual(viewModel.redErrorCount, redErrorBeforeHold)
        XCTAssertEqual(viewModel.blueErrorCount, blueErrorBeforeHold)
        XCTAssertEqual(viewModel.lastSentEpoch, lastSentEpochBeforeHold)
        XCTAssertNil(viewModel.lastLocalSendMs)

        clock.advance(by: 0.02)
        viewModel.processDetectedForTesting([(.red, endpoints)], at: clock.now, dimensions: (20, 20))
        let redCompleted = await waitUntil { viewModel.redCompletedCount == 1 }; XCTAssertTrue(redCompleted)
        XCTAssertEqual(viewModel.redCompletedCount, 1)
        XCTAssertEqual(viewModel.blueErrorCount, 1)
        XCTAssertEqual(viewModel.status, "送信エラー")

        viewModel.processDetectedForTesting([(.blue, endpoints)], at: 100.13, dimensions: (20, 20))
        let blueCompleted = await waitUntil { viewModel.blueCompletedCount == 1 }; XCTAssertTrue(blueCompleted)
        XCTAssertEqual(viewModel.blueCompletedCount, 1)
        XCTAssertEqual(viewModel.status, "送信中")
    }

    func testDimensionChangeClearsHeldFrameBeforeProcessing() {
        let processor = FrameProcessor()
        let base = ProcessInfo.processInfo.systemUptime
        var results: [[DetectedSaber]] = []
        processor.onResult = { detected, _, _, _, _, _ in results.append(detected) }
        processor.processDetectedForTesting([(.blue, (PixelPoint(x: 1, y: 1), PixelPoint(x: 8, y: 8)))], at: base, dimensions: (20, 20))
        processor.processDetectedForTesting([(.blue, nil)], at: base + 0.01, dimensions: (30, 20))
        XCTAssertEqual(results.last?.count, 0)
    }

    @MainActor
    func testStopAndRestartRejectsOldSendCompletion() async {
        let completions = CompletionBox()
        let sender = UDPSender { _, port, completion in
            completions.append(port: port, completion: completion)
        }
        let viewModel = CameraViewModel(sender: sender)
        let endpoints = (PixelPoint(x: 2, y: 3), PixelPoint(x: 12, y: 13))
        viewModel.startForTesting()
        viewModel.processDetectedForTesting([(.red, endpoints)], at: 300, dimensions: (20, 20))
        let oldQueued = await waitUntil { completions.valuesCountForTesting > 0 }; XCTAssertTrue(oldQueued)
        let oldCompletion = completions.removeFirst()
        XCTAssertEqual(viewModel.redAttemptCount, 1)
        viewModel.stop()
        viewModel.startForTesting()
        viewModel.processDetectedForTesting([(.red, endpoints)], at: 301, dimensions: (20, 20))
        let newQueued = await waitUntil { completions.valuesCountForTesting > 0 }; XCTAssertTrue(newQueued)
        let newCompletion = completions.removeFirst()
        oldCompletion?.1(.success(301))
        let oldCompletionProcessed = await waitUntil { sender.rejectedCompletionCountForTesting == 1 }; XCTAssertTrue(oldCompletionProcessed)
        XCTAssertNil(viewModel.lastLocalSendMs)
        XCTAssertEqual(viewModel.redCompletedCount, 0)
        newCompletion?.1(.success(302))
        let completionAccepted = await waitUntil { viewModel.redCompletedCount == 1 }; XCTAssertTrue(completionAccepted)
        XCTAssertEqual(viewModel.redAttemptCount, 1)
        XCTAssertEqual(viewModel.redCompletedCount, 1)
    }

    @MainActor
    func testStartMeasurementEnablesTimestampModeBeforeStarting() {
        let viewModel = CameraViewModel(
            authorizationStatus: { .denied },
            requestAccess: { _ in }
        )

        viewModel.startMeasurement()

        XCTAssertTrue(viewModel.measurementMode)
        XCTAssertFalse(viewModel.running)
        XCTAssertEqual(viewModel.status, "カメラエラー")
        XCTAssertEqual(viewModel.errorMessage, "設定アプリでカメラ権限を許可してください")
    }

    @MainActor
    func testStopAndRestartRejectsOldFramePermissionAndConnectionCallbacks() async {
        var permissionCompletions: [(Bool) -> Void] = []
        let sender = UDPSender { _, _, _ in }
        let viewModel = CameraViewModel(
            sender: sender,
            authorizationStatus: { .notDetermined },
            requestAccess: { completion in permissionCompletions.append(completion) }
        )
        viewModel.start()
        viewModel.stop()
        viewModel.start()
        XCTAssertEqual(permissionCompletions.count, 2)
        permissionCompletions[0](false)
        let oldPermissionProcessed = await waitUntil { viewModel.authorizationCallbackCount == 1 }
        XCTAssertTrue(oldPermissionProcessed)
        XCTAssertFalse(viewModel.running)
        XCTAssertNil(viewModel.errorMessage)

        viewModel.startForTesting()
        var currentReady = false
        for _ in 0..<100 {
            if viewModel.senderStates == [5005: "ready (テスト送信)", 5006: "ready (テスト送信)"] {
                currentReady = true
                break
            }
            try? await Task.sleep(nanoseconds: 5_000_000)
        }
        XCTAssertTrue(currentReady)
        let firstReadyGeneration = viewModel.acceptedSenderUpdateGeneration
        let currentStates = viewModel.senderStates
        let currentErrors = viewModel.senderErrors
        let oldFrameCallback = viewModel.processor.onResult
        let oldFrameGeneration = viewModel.processor.currentGeneration
        viewModel.stop()
        viewModel.startForTesting()
        let currentReadyAfterRestart = await waitUntil { viewModel.acceptedSenderUpdateGeneration > firstReadyGeneration && viewModel.senderStates == currentStates }
        XCTAssertTrue(currentReadyAfterRestart)
        let acceptedBeforeStale = viewModel.acceptedSenderUpdateCount
        let rejectedFrameBeforeStale = viewModel.rejectedFrameCallbackCount
        let rejectedSenderBeforeStale = viewModel.rejectedSenderUpdateCount
        oldFrameCallback?([DetectedSaber(endpoints: (PixelPoint(x: 1, y: 1), PixelPoint(x: 2, y: 2)), color: .red, isFresh: true)], 20, 20, 301, oldFrameGeneration, nil)
        sender.sendStaleUpdateForTesting(index: 0, states: [5005: "failed"], errors: [5005: "旧接続通知"])
        let staleCallbackProcessed = await waitUntil { viewModel.rejectedFrameCallbackCount > rejectedFrameBeforeStale && viewModel.rejectedSenderUpdateCount > rejectedSenderBeforeStale }
        XCTAssertTrue(staleCallbackProcessed)
        XCTAssertEqual(viewModel.acceptedSenderUpdateCount, acceptedBeforeStale)
        XCTAssertNil(viewModel.redEndpoints)
        XCTAssertEqual(viewModel.redDetectionCount, 0)
        XCTAssertEqual(viewModel.senderStates, currentStates)
        XCTAssertEqual(viewModel.senderErrors, currentErrors)
    }

    func testExpiryAfterLastMissingFrameClearsStoppedStream() {
        let clock = ManualClock(100)
        let processor = FrameProcessor(clock: { clock.now }, expiryScheduler: nil)
        let expired = expectation(description: "held frame expires")
        var results: [[DetectedSaber]] = []
        processor.onResult = { detected, _, _, _, _, _ in
            results.append(detected)
            if detected.isEmpty { expired.fulfill() }
        }
        let endpoints = (PixelPoint(x: 2, y: 2), PixelPoint(x: 10, y: 10))
        processor.processDetectedForTesting([(.red, endpoints)], at: clock.now, dimensions: (20, 20))
        clock.advance(by: 0.179)
        processor.processDetectedForTesting([(.red, nil)], at: clock.now, dimensions: (20, 20))
        XCTAssertEqual(results.last?.first?.endpoints.0, endpoints.0)
        clock.advance(by: 0.002)
        processor.expireForTesting()
        wait(for: [expired], timeout: 0)
        XCTAssertEqual(results.last?.count, 0)
    }

    func testExpiryUsesIndependentColorDeadlines() {
        let clock = ManualClock(200)
        let processor = FrameProcessor(clock: { clock.now }, expiryScheduler: nil)
        var results: [[DetectedSaber]] = []
        processor.onResult = { detected, _, _, _, _, _ in results.append(detected) }
        let red = (PixelPoint(x: 1, y: 1), PixelPoint(x: 8, y: 8))
        let blue = (PixelPoint(x: 2, y: 2), PixelPoint(x: 9, y: 9))
        processor.processDetectedForTesting([(.red, red)], at: clock.now, dimensions: (20, 20))
        clock.advance(by: 0.10)
        processor.processDetectedForTesting([(.blue, blue)], at: clock.now, dimensions: (20, 20))
        clock.advance(by: 0.081)
        processor.processDetectedForTesting([(.red, nil), (.blue, nil)], at: clock.now, dimensions: (20, 20))
        XCTAssertEqual(results.last?.count, 1)
        XCTAssertEqual(results.last?.first?.color, .blue)
    }

    func testShortDropoutPredictionStopsAfterThreeFramesAndRecoversImmediately() throws {
        let clock = ManualClock(300)
        let processor = FrameProcessor(clock: { clock.now }, expiryScheduler: nil)
        var results: [[DetectedSaber]] = []
        processor.onResult = { detected, _, _, _, _, _ in results.append(detected) }
        let first = (PixelPoint(x: 10, y: 10), PixelPoint(x: 20, y: 10))
        let second = (PixelPoint(x: 12, y: 11), PixelPoint(x: 22, y: 11))

        processor.processDetectedForTesting([(.blue, first)], at: clock.now,
                                            dimensions: (100, 80))
        XCTAssertEqual(results.last?.first?.endpoints.0, first.0)
        XCTAssertFalse(try XCTUnwrap(results.last?.first).isPredicted)
        clock.advance(by: 1.0 / 30.0)
        processor.processDetectedForTesting([(.blue, second)], at: clock.now,
                                            dimensions: (100, 80))
        XCTAssertEqual(results.last?.first?.endpoints.0, second.0)
        XCTAssertFalse(try XCTUnwrap(results.last?.first).isPredicted)

        for missingFrame in 1...3 {
            clock.advance(by: 1.0 / 30.0)
            processor.processDetectedForTesting([(.blue, nil)], at: clock.now,
                                                dimensions: (100, 80))
            let predicted = try XCTUnwrap(results.last?.first)
            XCTAssertTrue(predicted.isFresh)
            XCTAssertTrue(predicted.isPredicted)
            XCTAssertEqual(predicted.endpoints.0,
                           PixelPoint(x: 12 + 2 * missingFrame,
                                      y: 11 + missingFrame))
            XCTAssertEqual(predicted.endpoints.1,
                           PixelPoint(x: 22 + 2 * missingFrame,
                                      y: 11 + missingFrame))
        }

        clock.advance(by: 1.0 / 30.0)
        processor.processDetectedForTesting([(.blue, nil)], at: clock.now,
                                            dimensions: (100, 80))
        let fourth = try XCTUnwrap(results.last?.first)
        XCTAssertFalse(fourth.isFresh)
        XCTAssertFalse(fourth.isPredicted)

        let recovered = (PixelPoint(x: 30, y: 20), PixelPoint(x: 40, y: 20))
        clock.advance(by: 1.0 / 30.0)
        processor.processDetectedForTesting([(.blue, recovered)], at: clock.now,
                                            dimensions: (100, 80))
        let recoveryResult = try XCTUnwrap(results.last?.first)
        XCTAssertTrue(recoveryResult.isFresh)
        XCTAssertFalse(recoveryResult.isPredicted)
        XCTAssertEqual(recoveryResult.endpoints.0, recovered.0)
        XCTAssertEqual(recoveryResult.endpoints.1, recovered.1)
    }

    func testDebugRecordingCreatesSynchronizedRawOverlayAndMetadata() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberRecordingTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let recorder = try DebugVideoRecorder(directory: directory, date: Date(timeIntervalSince1970: 0))
        let red = DetectedSaber(
            endpoints: (PixelPoint(x: 8, y: 10), PixelPoint(x: 52, y: 10)),
            color: .red, isFresh: true
        )
        let blue = DetectedSaber(
            endpoints: (PixelPoint(x: 8, y: 34), PixelPoint(x: 52, y: 34)),
            color: .blue, isFresh: true
        )

        recorder.append(pixelBuffer: solidPixelBuffer(width: 64, height: 48),
                        presentationTime: CMTime(value: 0, timescale: 30),
                        frameID: 100, results: [red])
        Thread.sleep(forTimeInterval: 0.04)
        recorder.append(pixelBuffer: solidPixelBuffer(width: 64, height: 48),
                        presentationTime: CMTime(value: 1, timescale: 30),
                        frameID: 101, results: [blue])
        Thread.sleep(forTimeInterval: 0.04)
        let staleRed = DetectedSaber(endpoints: red.endpoints, color: .red, isFresh: false)
        recorder.append(pixelBuffer: solidPixelBuffer(width: 64, height: 48),
                        presentationTime: CMTime(value: 2, timescale: 30),
                        frameID: 102, results: [staleRed])

        let recording: DebugRecordingResult = try await withCheckedThrowingContinuation { continuation in
            recorder.finish { continuation.resume(with: $0) }
        }
        XCTAssertEqual(recording.recordedFrameCount, 3)
        XCTAssertEqual(recording.droppedFrameCount, 0)
        XCTAssertTrue(FileManager.default.fileExists(atPath: recording.rawVideoURL.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: recording.overlayVideoURL.path))

        let metadata = try JSONDecoder().decode(
            DebugRecordingMetadata.self,
            from: Data(contentsOf: recording.metadataURL)
        )
        XCTAssertEqual(metadata.formatVersion, DebugRecordingMetadata.currentFormatVersion)
        XCTAssertEqual(metadata.frames.map(\.frameID), [100, 101, 102])
        XCTAssertTrue(metadata.frames[0].red.detected)
        XCTAssertFalse(metadata.frames[0].blue.detected)
        XCTAssertFalse(metadata.frames[1].red.detected)
        XCTAssertTrue(metadata.frames[1].blue.detected)
        XCTAssertFalse(metadata.frames[2].red.detected)
        XCTAssertFalse(metadata.frames[2].blue.detected)

        let rawFrames = try decodedVideoSamples(recording.rawVideoURL)
        let overlayFrames = try decodedVideoSamples(recording.overlayVideoURL)
        XCTAssertEqual(rawFrames.count, 3)
        XCTAssertEqual(overlayFrames.count, 3)
        XCTAssertEqual(rawFrames.map(\.timestamp), overlayFrames.map(\.timestamp))
        XCTAssertLessThan(rawFrames[0].pixel(30, 10).r, 80)
        XCTAssertGreaterThan(overlayFrames[0].pixel(30, 10).r, 150)
        XCTAssertLessThan(rawFrames[1].pixel(30, 34).b, 80)
        XCTAssertGreaterThan(overlayFrames[1].pixel(30, 34).b, 150)
        XCTAssertLessThan(overlayFrames[2].pixel(30, 10).r, 80,
                          "detected=false must not draw a retained red line")
        XCTAssertLessThan(overlayFrames[2].pixel(30, 34).b, 80,
                          "detected=false must not draw a retained blue line")
    }

    func testDebugRecordingCameraSamplesAreEncodedAndBounded() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberCameraSampleTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let recorder = try DebugVideoRecorder(directory: directory, date: Date(timeIntervalSince1970: 0))
        recorder.append(pixelBuffer: solidPixelBuffer(width: 64, height: 48),
                        presentationTime: CMTime(value: 0, timescale: 30),
                        frameID: 500, results: [])
        try await Task.sleep(for: .milliseconds(40))

        for index in 0..<(DebugVideoRecorder.maximumCameraSamples + 5) {
            recorder.appendCameraSample(DebugRecordingCameraSample(
                frameID: UInt64(index), presentationTimeSeconds: Double(index) / 30,
                exposureDurationMs: 8.5, iso: 320,
                whiteBalanceRedGain: 1.1, whiteBalanceGreenGain: 1.2, whiteBalanceBlueGain: 1.3,
                exposureMode: "Continuous auto", whiteBalanceMode: "Continuous auto",
                focusMode: "Continuous auto", lensPosition: 0.4,
                activeFormat: "640×480", activeFormatFPSRanges: "30…60",
                activeMinFPS: 30, activeMaxFPS: 60
            ))
        }

        let recording: DebugRecordingResult = try await withCheckedThrowingContinuation { continuation in
            recorder.finish { continuation.resume(with: $0) }
        }
        let encoded = try Data(contentsOf: recording.metadataURL)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: encoded) as? [String: Any])
        let cameraSamplesObject = try XCTUnwrap(object["cameraSamples"] as? [[String: Any]])
        let metadata = try JSONDecoder().decode(DebugRecordingMetadata.self, from: encoded)
        XCTAssertEqual(cameraSamplesObject.count, DebugVideoRecorder.maximumCameraSamples)
        XCTAssertEqual(metadata.cameraSamples.map(\.frameID), (5..<305).map { UInt64($0) })
        let firstSample = try XCTUnwrap(metadata.cameraSamples.first)
        XCTAssertEqual(firstSample.frameID, 5)
        XCTAssertEqual(firstSample.presentationTimeSeconds ?? -1, 5.0 / 30.0, accuracy: 0.000_001)
        XCTAssertEqual(firstSample.exposureDurationMs, 8.5)
        XCTAssertEqual(firstSample.iso, 320)
        XCTAssertEqual(firstSample.whiteBalanceRedGain, 1.1)
        XCTAssertEqual(firstSample.whiteBalanceGreenGain, 1.2)
        XCTAssertEqual(firstSample.whiteBalanceBlueGain, 1.3)
        XCTAssertEqual(firstSample.exposureMode, "Continuous auto")
        XCTAssertEqual(firstSample.whiteBalanceMode, "Continuous auto")
        XCTAssertEqual(firstSample.focusMode, "Continuous auto")
        XCTAssertEqual(firstSample.lensPosition, 0.4)
        XCTAssertEqual(firstSample.activeFormat, "640×480")
        XCTAssertEqual(firstSample.activeFormatFPSRanges, "30…60")
        XCTAssertEqual(firstSample.activeMinFPS, 30)
        XCTAssertEqual(firstSample.activeMaxFPS, 60)
        XCTAssertEqual(metadata.frames.map(\.frameID), [500])
    }

    @MainActor
    func testDebugRecordingIsCompletelyOffByDefault() {
        let processor = FrameProcessor(expiryScheduler: nil)
        var freshResults = 0
        processor.onResult = { results, _, _, _, _, _ in
            let red = results.first { $0.color == .red && $0.isFresh && !$0.isPredicted }
            XCTAssertNotNil(red)
            XCTAssertNil(red?.diagnosticSendStarted)
            freshResults += red == nil ? 0 : 1
        }
        // Exercise repeated successful recognition, including a moving blade,
        // rather than checking OFF with only an empty frame.
        for offset in 0..<25 {
            let frame = sampleBuffer(width: 192, height: 96) { base, stride in
                let pixels = base.assumingMemoryBound(to: UInt8.self)
                for y in 30...38 { for x in (10 + offset)...(90 + offset) {
                    let index = y * stride + x * 4
                    pixels[index] = 35; pixels[index + 1] = 45
                    pixels[index + 2] = 245; pixels[index + 3] = 255
                } }
            }
            processor.process(frame)
            XCTAssertEqual(processor.motionHistoryCountForTesting, 0)
        }
        XCTAssertEqual(freshResults, 25)
        let viewModel = CameraViewModel(
            authorizationStatus: { .denied },
            requestAccess: { _ in }
        )
        XCTAssertFalse(viewModel.debugRecordingEnabled)
        XCTAssertFalse(viewModel.debugRecordingActive)
        XCTAssertFalse(viewModel.debugRecordingFinalizing)
        XCTAssertEqual(viewModel.debugRecordingStatus, "OFF")
        XCTAssertNil(viewModel.lastDebugRecordingResult)
#if DEBUG
        XCTAssertFalse(viewModel.freezeDiagnosticsEnabled)
#endif
    }

    func testCandidateDiagnosticsCopiesSelectedAndTopThreeWithoutRecalculation() throws {
        let fixture = try fixtureBGRA("blue-led-bright-large-05")
        let analysis = analyzeSabers(
            in: fixture.bytes, width: fixture.width, height: fixture.height,
            bytesPerRow: fixture.bytesPerRow,
            redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(),
            collectProfile: false, collectPipelineDiagnostics: true
        )
        let diagnostics = DebugRecordingCandidateDiagnostics(analysis: analysis)
        let blueCandidates = analysis.candidates[.blue] ?? []
        let selectedIndex = blueCandidates.firstIndex { $0.isEmitterEligible }

        XCTAssertEqual(diagnostics.blue.totalCandidateCount, blueCandidates.count)
        XCTAssertEqual(diagnostics.blue.eligibleCandidateCount,
                       blueCandidates.filter(\.isEmitterEligible).count)
        XCTAssertGreaterThan(diagnostics.blue.maskPixelCount, 0)
        XCTAssertGreaterThan(diagnostics.blue.morphologyPixelCount, 0)
        XCTAssertGreaterThan(diagnostics.blue.connectedComponentCount, 0)
        XCTAssertEqual(diagnostics.blue.selectedCandidateIndex, selectedIndex)
        XCTAssertEqual(diagnostics.blue.topCandidates.count, min(3, blueCandidates.count))
        let index = try XCTUnwrap(selectedIndex)
        let selected = try XCTUnwrap(diagnostics.blue.selectedCandidate)
        XCTAssertEqual(selected.finalScore, blueCandidates[index].score)
        XCTAssertEqual(selected.scoreBreakdown.total, blueCandidates[index].scoreBreakdown.total)
        XCTAssertEqual(selected.rawPCASpan, blueCandidates[index].rawPCASpan)
        XCTAssertEqual(selected.continuity, blueCandidates[index].longitudinalContinuity)
        XCTAssertTrue(selected.eligibilityRules.contains { $0.name == "peakValue" })
    }

    func testNormalDetectionDoesNotBuildRejectionSiteDiagnostics() throws {
        let fixture = try fixtureBGRA("blue-led-bright-large-05")
        for profiling in [false, true] {
            let analysis = analyzeSabers(
                in: fixture.bytes, width: fixture.width, height: fixture.height,
                bytesPerRow: fixture.bytesPerRow,
                redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(),
                collectProfile: profiling, collectPipelineDiagnostics: false
            )
            XCTAssertNil(analysis.pipelineDiagnostics)
            XCTAssertFalse(analysis.candidates.values.flatMap { $0 }.isEmpty)
            XCTAssertTrue(analysis.candidates.values.flatMap { $0 }.allSatisfy {
                $0.diagnosticRejections.isEmpty && $0.endpointDiagnosticTrace == nil
            })
        }
    }

    func testSparseBlueMaskSurvivesDestructiveOpeningThroughRawFallback() throws {
        let width = 480, height = 640, bytesPerRow = width * 4
        var bytes = Array(repeating: UInt8(0), count: bytesPerRow * height)
        for pixel in stride(from: 3, to: bytes.count, by: 4) { bytes[pixel] = 255 }
        for sampleX in 50..<69 {
            let x = sampleX * 2, y = 220
            let offset = y * bytesPerRow + x * 4
            bytes[offset] = 255
            bytes[offset + 1] = 55
            bytes[offset + 2] = 20
        }
        let analysis = analyzeSabers(
            in: bytes, width: width, height: height, bytesPerRow: bytesPerRow,
            redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(),
            collectPipelineDiagnostics: true
        )
        let pipeline = try XCTUnwrap(analysis.pipelineDiagnostics?[.blue])
        XCTAssertEqual(pipeline.maskPixelCount, 19)
        XCTAssertEqual(pipeline.morphologyPixelCount, 0)
        XCTAssertEqual(analysis.candidates[.blue]?.first?.source, "color-sparse-raw")
        XCTAssertNotNil(analysis.selected[.blue])
    }

    func testNormalMaskDoesNotEnterSparseRawFallback() throws {
        let fixture = try fixtureBGRA("blue-led-bright-large-05")
        let analysis = analyzeSabers(
            in: fixture.bytes, width: fixture.width, height: fixture.height,
            bytesPerRow: fixture.bytesPerRow,
            redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(),
            collectPipelineDiagnostics: true
        )
        let pipeline = try XCTUnwrap(analysis.pipelineDiagnostics?[.blue])
        XCTAssertGreaterThan(pipeline.morphologyPixelCount * 5,
                             pipeline.maskPixelCount * 3)
        XCTAssertFalse((analysis.candidates[.blue] ?? []).contains {
            $0.source == "color-sparse-raw"
        })
    }

    func testBlueDiffuserColorModelRecoversMeasuredPaleAndBlurredPixels() {
        let threshold = ColorThreshold()
        let lowSaturation = (red: UInt8(220), green: UInt8(230), blue: UInt8(245))
        let whiteClipped = (red: UInt8(245), green: UInt8(248), blue: UInt8(255))
        let motionBlurred = (red: UInt8(70), green: UInt8(95), blue: UInt8(135))

        for pixel in [lowSaturation, whiteClipped, motionBlurred] {
            XCTAssertFalse(matchesSaberHSV(
                pixel.red, pixel.green, pixel.blue, color: .blue, threshold: threshold
            ))
            XCTAssertTrue(matchesBlueDiffuserPixel(
                pixel.red, pixel.green, pixel.blue, threshold: threshold
            ))
        }
        XCTAssertFalse(matchesBlueDiffuserPixel(245, 245, 245, threshold: threshold))
        XCTAssertFalse(matchesBlueDiffuserPixel(245, 80, 70, threshold: threshold),
                       "normal red must not enter the blue auxiliary path")
    }

    func testBlueDiffuserSupportExpandsOnlyNextToStrictBlue() {
        let width = 15, height = 7
        var strict = Array(repeating: UInt8(0), count: width * height)
        var relaxed = Array(repeating: UInt8(0), count: width * height)
        strict[3 * width + 3] = 1
        relaxed[3 * width + 5] = 1
        relaxed[3 * width + 12] = 1

        let supported = supportedBlueDiffuserMask(
            strictMask: strict, relaxedMask: relaxed, width: width, height: height
        )
        XCTAssertEqual(supported[3 * width + 3], 1)
        XCTAssertEqual(supported[3 * width + 5], 1)
        XCTAssertEqual(supported[3 * width + 12], 0,
                       "unrelated low-saturation background must remain excluded")
    }

    func testDiffusedBlueHaloIncreasesMaskWithoutChangingRedDetection() throws {
        let width = 160, height = 80, bytesPerRow = width * 4
        var bytes = Array(repeating: UInt8(0), count: bytesPerRow * height)
        func put(_ x: Int, _ y: Int, red: UInt8, green: UInt8, blue: UInt8) {
            let offset = y * bytesPerRow + x * 4
            bytes[offset] = blue; bytes[offset + 1] = green
            bytes[offset + 2] = red; bytes[offset + 3] = 255
        }
        for x in 18...138 {
            for y in 34...46 { put(x, y, red: 220, green: 230, blue: 245) }
            if x.isMultiple(of: 8) {
                for y in 37...43 { put(x, y, red: 35, green: 75, blue: 248) }
            }
        }
        for y in 10...68 {
            for x in 146...154 { put(x, y, red: 248, green: 45, blue: 35) }
        }
        let analysis = analyzeSabers(
            in: bytes, width: width, height: height, bytesPerRow: bytesPerRow,
            redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(),
            collectPipelineDiagnostics: true
        )
        let bluePipeline = try XCTUnwrap(analysis.pipelineDiagnostics?[.blue])
        XCTAssertGreaterThan(bluePipeline.maskPixelCount, 250,
                             "the pale paper diffuser must join the sparse strict-blue anchors")
        XCTAssertNotNil(analysis.selected[.blue])
        XCTAssertNotNil(analysis.selected[.red])
    }

    func testRelaxedBlueBackgroundWithoutStrictAnchorIsNotDetected() {
        let width = 160, height = 80, bytesPerRow = width * 4
        var bytes = Array(repeating: UInt8(0), count: bytesPerRow * height)
        for x in 10...148 {
            for y in 35...44 {
                let offset = y * bytesPerRow + x * 4
                bytes[offset] = 195; bytes[offset + 1] = 190
                bytes[offset + 2] = 170; bytes[offset + 3] = 255
            }
        }
        XCTAssertNil(detectSaber(
            in: bytes, width: width, height: height, bytesPerRow: bytesPerRow,
            color: .blue, threshold: ColorThreshold()
        ))
    }

    func testForensicCaptureWritesOnlyAnomalousAcceptedFrameAfterStop() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberForensicTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let policy = DebugForensicCapturePolicy(
            absoluteLengthThreshold: 50, relativeLengthThreshold: 40,
            growthRatio: 2, maximumFrames: 1
        )
        let recorder = try DebugVideoRecorder(directory: directory, forensicPolicy: policy)
        let normal = DetectedSaber(
            endpoints: (PixelPoint(x: 4, y: 10), PixelPoint(x: 24, y: 10)),
            color: .red, isFresh: true
        )
        let anomalous = DetectedSaber(
            endpoints: (PixelPoint(x: 2, y: 12), PixelPoint(x: 62, y: 12)),
            color: .red, isFresh: true
        )
        recorder.append(pixelBuffer: solidPixelBuffer(width: 64, height: 48),
                        presentationTime: CMTime(value: 0, timescale: 30),
                        frameID: 200, results: [normal])
        Thread.sleep(forTimeInterval: 0.04)
        let anomalyBuffer = solidPixelBuffer(width: 64, height: 48)
        CVPixelBufferLockBaseAddress(anomalyBuffer, .readOnly)
        recorder.append(pixelBuffer: anomalyBuffer,
                        presentationTime: CMTime(value: 1, timescale: 30),
                        frameID: 201, results: [anomalous])
        CVPixelBufferUnlockBaseAddress(anomalyBuffer, .readOnly)
        Thread.sleep(forTimeInterval: 0.04)
        let secondAnomalyBuffer = solidPixelBuffer(width: 64, height: 48)
        CVPixelBufferLockBaseAddress(secondAnomalyBuffer, .readOnly)
        recorder.append(pixelBuffer: secondAnomalyBuffer,
                        presentationTime: CMTime(value: 2, timescale: 30),
                        frameID: 202, results: [anomalous])
        CVPixelBufferUnlockBaseAddress(secondAnomalyBuffer, .readOnly)

        XCTAssertFalse(FileManager.default.fileExists(atPath: recorder.forensicDirectoryURL.path),
                       "recording must not encode or write forensic PNGs")
        let recording: DebugRecordingResult = try await withCheckedThrowingContinuation { continuation in
            recorder.finish { continuation.resume(with: $0) }
        }
        let metadata = try JSONDecoder().decode(
            DebugRecordingMetadata.self, from: Data(contentsOf: recording.metadataURL)
        )
        XCTAssertFalse(metadata.frames[0].forensicCaptured)
        XCTAssertNil(metadata.frames[0].forensicFileName)
        XCTAssertTrue(metadata.frames[1].forensicCaptured)
        XCTAssertEqual(metadata.frames[1].forensicFileName, "frame_201.png")
        XCTAssertFalse(metadata.frames[2].forensicCaptured, "session capture limit must be enforced")
        XCTAssertNil(metadata.frames[2].forensicFileName)
        let forensicDirectory = try XCTUnwrap(recording.forensicDirectoryURL)
        let pngURL = forensicDirectory.appendingPathComponent("frame_201.png")
        XCTAssertTrue(FileManager.default.fileExists(atPath: pngURL.path))
        let image = try XCTUnwrap(UIImage(contentsOfFile: pngURL.path))
        XCTAssertEqual(Int(image.size.width), 64)
        XCTAssertEqual(Int(image.size.height), 48)
    }

    func testManualLosslessCaptureUsesNextAcceptedFrameAndMatchingMetadata() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberManualCaptureTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let recorder = try DebugVideoRecorder(directory: directory)
        var capturedFrameID: UInt64?
        XCTAssertTrue(recorder.requestManualCapture { result in
            capturedFrameID = try? result.get()
        })
        XCTAssertFalse(recorder.requestManualCapture { _ in },
                       "a pending one-shot request must not capture multiple frames")
        let analysis = SaberFrameAnalysis(candidates: [.red: [], .blue: []], selected: [:])
        let buffer = solidPixelBuffer(width: 64, height: 48)
        CVPixelBufferLockBaseAddress(buffer, .readOnly)
        recorder.append(pixelBuffer: buffer,
                        presentationTime: CMTime(value: 0, timescale: 30),
                        frameID: 301, results: [], analysis: analysis)
        CVPixelBufferUnlockBaseAddress(buffer, .readOnly)
        XCTAssertEqual(capturedFrameID, 301)

        let recording: DebugRecordingResult = try await withCheckedThrowingContinuation { continuation in
            recorder.finish { continuation.resume(with: $0) }
        }
        let metadata = try JSONDecoder().decode(
            DebugRecordingMetadata.self, from: Data(contentsOf: recording.metadataURL)
        )
        XCTAssertEqual(metadata.frames.count, 1)
        XCTAssertEqual(metadata.frames[0].frameID, 301)
        XCTAssertTrue(metadata.frames[0].manualCaptured)
        XCTAssertEqual(metadata.frames[0].manualFileName, "manual_frame_301.png")
        XCTAssertNotNil(metadata.frames[0].candidateDiagnostics)
        let forensicDirectory = try XCTUnwrap(recording.forensicDirectoryURL)
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: forensicDirectory.appendingPathComponent("manual_frame_301.png").path
        ))
    }

    func testDebugRecordingAutomaticallyStopsAtDurationLimit() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberDurationLimitTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        var now: TimeInterval = 10
        let recorder = try DebugVideoRecorder(directory: directory, clock: { now })
        try recorder.prepare(width: 64, height: 48)
        let first = solidPixelBuffer(width: 64, height: 48)
        CVPixelBufferLockBaseAddress(first, .readOnly)
        XCTAssertEqual(recorder.append(
            pixelBuffer: first, presentationTime: CMTime(value: 0, timescale: 30),
            frameID: 501, results: []
        ), .accepted)
        CVPixelBufferUnlockBaseAddress(first, .readOnly)
        Thread.sleep(forTimeInterval: 0.04)

        now += DebugRecordingLimits.maximumDurationSeconds + 1
        let second = solidPixelBuffer(width: 64, height: 48)
        CVPixelBufferLockBaseAddress(second, .readOnly)
        let result = recorder.append(
            pixelBuffer: second, presentationTime: CMTime(value: 1, timescale: 30),
            frameID: 502, results: []
        )
        CVPixelBufferUnlockBaseAddress(second, .readOnly)
        guard case .reachedLimit(.maximumDuration) = result else {
            return XCTFail("Expected the duration cap to stop the recording")
        }

        let recording: DebugRecordingResult = try await withCheckedThrowingContinuation { continuation in
            recorder.finish(reason: .maximumDuration) { continuation.resume(with: $0) }
        }
        XCTAssertEqual(recording.recordedFrameCount, 1)
        XCTAssertEqual(recording.finishReason, .maximumDuration)
    }

    func testDebugRecordingBudgetsAreFiniteAndFitSessionLimit() {
        let reservedMaximum = 2 * DebugRecordingLimits.maximumSingleVideoBytes
            + DebugRecordingLimits.maximumMetadataBytes
            + DebugRecordingLimits.maximumLosslessImageBytes
            + DebugRecordingTriageLimits.maximumBundleBytes * 2
        XCTAssertEqual(DebugRecordingLimits.maximumDurationSeconds, 300)
        XCTAssertLessThanOrEqual(reservedMaximum, DebugRecordingLimits.maximumDiskUsageBytes)
        XCTAssertEqual(DebugForensicCapturePolicy.production.maximumFrames,
                       DebugRecordingLimits.maximumForensicImages)
        XCTAssertEqual(DebugRecordingLimits.maximumManualLosslessCaptures, 3)
        XCTAssertLessThan(Int64(DebugRecordingLimits.maximumBufferedLosslessBytes),
                          DebugRecordingLimits.maximumDiskUsageBytes)
    }

    func testManualLosslessCaptureHasSessionLimitAndMetadataRemainsCompatible() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberManualLimitTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let recorder = try DebugVideoRecorder(directory: directory)

        for index in 0..<DebugRecordingLimits.maximumManualLosslessCaptures {
            var captureResult: Result<UInt64, DebugVideoRecorderError>?
            XCTAssertTrue(recorder.requestManualCapture { captureResult = $0 })
            let buffer = solidPixelBuffer(width: 64, height: 48)
            CVPixelBufferLockBaseAddress(buffer, .readOnly)
            _ = recorder.append(
                pixelBuffer: buffer,
                presentationTime: CMTime(value: CMTimeValue(index), timescale: 30),
                frameID: UInt64(600 + index), results: []
            )
            CVPixelBufferUnlockBaseAddress(buffer, .readOnly)
            XCTAssertEqual(try captureResult?.get(), UInt64(600 + index))
            Thread.sleep(forTimeInterval: 0.04)
        }

        var rejectedCapture: Result<UInt64, DebugVideoRecorderError>?
        XCTAssertTrue(recorder.requestManualCapture { rejectedCapture = $0 })
        XCTAssertThrowsError(try rejectedCapture?.get()) { error in
            XCTAssertEqual(error as? DebugVideoRecorderError, .manualCaptureLimitReached)
        }

        let recording: DebugRecordingResult = try await withCheckedThrowingContinuation { continuation in
            recorder.finish { continuation.resume(with: $0) }
        }
        let metadataData = try Data(contentsOf: recording.metadataURL)
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: metadataData) as? [String: Any])
        XCTAssertEqual(Set(json.keys), Set(["formatVersion", "sessionID", "width", "height", "frames", "cameraSamples", "motionEvents", "motionSummary", "udpTransmissions", "activeColors", "diagnosticWindows", "bridgeDropoutEvents", "bridgeDropoutSummary",
                                                "segmentMarkers", "segmentSummary"]))
        // Existing Codable readers ignore the additive motion root fields.
        let metadata = try JSONDecoder().decode(DebugRecordingMetadata.self, from: metadataData)
        XCTAssertEqual(metadata.frames.filter(\.manualCaptured).count,
                       DebugRecordingLimits.maximumManualLosslessCaptures)
        XCTAssertLessThanOrEqual(recording.diskUsageBytes,
                                 DebugRecordingLimits.maximumDiskUsageBytes)
        let firstLosslessURL = try XCTUnwrap(recording.forensicDirectoryURL)
            .appendingPathComponent("manual_frame_600.png")
        let losslessImage = try XCTUnwrap(UIImage(contentsOfFile: firstLosslessURL.path)?.cgImage)
        let losslessPixels = try XCTUnwrap(losslessImage.dataProvider?.data)
        let firstPixel = try XCTUnwrap(CFDataGetBytePtr(losslessPixels))
        XCTAssertEqual(firstPixel[0], 16)
        XCTAssertEqual(firstPixel[1], 16)
        XCTAssertEqual(firstPixel[2], 16)
        XCTAssertEqual(firstPixel[3], 255)
    }

    func testRecordingFinishReasonsUseOneFinalizePath() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberFinishReasons-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let reasons: [DebugRecordingFinishReason] = [
            .user, .maximumDuration, .maximumDiskUsage, .maximumMetadataSize,
            .diskLow, .background, .interruption, .runtimeFailure
        ]
        for (index, reason) in reasons.enumerated() {
            let recorder = try DebugVideoRecorder(
                directory: directory, date: Date(timeIntervalSince1970: Double(index + 1))
            )
            let buffer = solidPixelBuffer(width: 64, height: 48)
            CVPixelBufferLockBaseAddress(buffer, .readOnly)
            let append = recorder.append(pixelBuffer: buffer,
                presentationTime: CMTime(value: 1, timescale: 30),
                frameID: UInt64(index + 1), results: [])
            CVPixelBufferUnlockBaseAddress(buffer, .readOnly)
            XCTAssertEqual(append, .accepted)
            let recording: DebugRecordingResult = try await withCheckedThrowingContinuation { continuation in
                recorder.finish(reason: reason) { continuation.resume(with: $0) }
            }
            XCTAssertEqual(recording.finishReason, reason)
            let metadata = try JSONDecoder().decode(DebugRecordingMetadata.self,
                from: Data(contentsOf: recording.metadataURL))
            XCTAssertEqual(metadata.formatVersion, 1)
            XCTAssertEqual(metadata.frames.count, 1)
        }
    }

    func testRawMetadataAndPngFailureDoNotPublishOrTransfer() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberFinalizeFailures-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        for (index, stage) in [DebugRecordingFailureStage.metadata, .raw, .png].enumerated() {
            let recorder = try DebugVideoRecorder(
                directory: directory, date: Date(timeIntervalSince1970: Double(index + 20))
            )
            recorder.injectedFailureStageForTesting = stage
            XCTAssertTrue(recorder.requestManualCapture { _ in })
            let buffer = solidPixelBuffer(width: 64, height: 48)
            CVPixelBufferLockBaseAddress(buffer, .readOnly)
            XCTAssertEqual(recorder.append(pixelBuffer: buffer,
                presentationTime: CMTime(value: 1, timescale: 30),
                frameID: UInt64(index + 20), results: []), .accepted)
            CVPixelBufferUnlockBaseAddress(buffer, .readOnly)
            let result: Result<DebugRecordingResult, Error> = await withCheckedContinuation { continuation in
                recorder.finish { continuation.resume(returning: $0) }
            }
            if case .success = result { XCTFail("\(stage) unexpectedly finalized") }
            XCTAssertFalse(FileManager.default.fileExists(atPath: recorder.metadataURL.path))
            XCTAssertFalse(FileManager.default.fileExists(atPath:
                directory.appendingPathComponent("phone_saber_triage_\(recorder.sessionID)").path))
        }
    }

    func testOverlayFailureKeepsValidMetadataPngAndTriage() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberOverlayFailure-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let oldPreference = UserDefaults.standard.object(forKey: DebugBundleTransfer.preferenceKey)
        UserDefaults.standard.set(false, forKey: DebugBundleTransfer.preferenceKey)
        defer { UserDefaults.standard.set(oldPreference, forKey: DebugBundleTransfer.preferenceKey) }
        let recorder = try DebugVideoRecorder(directory: directory)
        recorder.injectedFailureStageForTesting = .overlay
        XCTAssertTrue(recorder.requestManualCapture { _ in })
        let buffer = solidPixelBuffer(width: 64, height: 48)
        CVPixelBufferLockBaseAddress(buffer, .readOnly)
        XCTAssertEqual(recorder.append(pixelBuffer: buffer,
            presentationTime: CMTime(value: 1, timescale: 30),
            frameID: 1, results: []), .accepted)
        CVPixelBufferUnlockBaseAddress(buffer, .readOnly)
        let recording: DebugRecordingResult = try await withCheckedThrowingContinuation { continuation in
            recorder.finish { continuation.resume(with: $0) }
        }
        XCTAssertTrue(FileManager.default.fileExists(atPath: recording.metadataURL.path))
        XCTAssertTrue(FileManager.default.fileExists(atPath:
            try XCTUnwrap(recording.forensicDirectoryURL).appendingPathComponent("manual_frame_1.png").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: try XCTUnwrap(recording.triageBundleURL).path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: recording.overlayVideoURL.path))
    }

    func testOlderSessionCleanupRequiresCallAndKeepsNewestAndUnrelatedFrames() throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberCleanupTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let sessions = [
            "phonesaber_20260101_120000_000",
            "phonesaber_20260102_120000_000",
            "phonesaber_20260103_120000_000"
        ]
        for (index, sessionID) in sessions.enumerated() {
            let url = directory.appendingPathComponent("\(sessionID)_raw.mp4")
            try Data(repeating: UInt8(index + 1), count: index + 10).write(to: url)
            try FileManager.default.setAttributes(
                [.modificationDate: Date(timeIntervalSince1970: TimeInterval(index + 1))],
                ofItemAtPath: url.path
            )
        }
        for sessionID in [sessions[0], sessions[2]] {
            let bundle = directory.appendingPathComponent("phone_saber_triage_\(sessionID)",
                                                         isDirectory: true)
            try FileManager.default.createDirectory(at: bundle, withIntermediateDirectories: true)
            try Data("bundle".utf8).write(to: bundle.appendingPathComponent("summary.json"))
        }
        try Data("temporary".utf8).write(to:
            directory.appendingPathComponent("\(sessions[0])_triage_transfer.psbt"))
        let unrelated = directory.appendingPathComponent("phone-saber-raw-1.png")
        try Data("keep".utf8).write(to: unrelated)

        XCTAssertEqual(DebugRecordingStorage.sessionSummaries(in: directory).count, 3)
        let removed = try DebugRecordingStorage.deleteOlderSessions(in: directory)
        XCTAssertEqual(removed.count, 2)
        XCTAssertFalse(FileManager.default.fileExists(
            atPath: directory.appendingPathComponent("\(sessions[0])_raw.mp4").path
        ))
        XCTAssertTrue(FileManager.default.fileExists(
            atPath: directory.appendingPathComponent("\(sessions[2])_raw.mp4").path
        ))
        XCTAssertFalse(FileManager.default.fileExists(atPath:
            directory.appendingPathComponent("phone_saber_triage_\(sessions[0])").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath:
            directory.appendingPathComponent("\(sessions[0])_triage_transfer.psbt").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath:
            directory.appendingPathComponent("phone_saber_triage_\(sessions[2])").path))
        XCTAssertTrue(FileManager.default.fileExists(atPath: unrelated.path))
    }

    // MARK: Bridge dropout recording

    private typealias BridgeFrame = (id: UInt64, time: Int, sabers: [DetectedSaber])

    private func bridgeSaber(_ color: SaberColor, _ x1: Int = 8, _ y1: Int = 24,
                             _ x2: Int = 52, _ y2: Int = 24) -> DetectedSaber {
        DetectedSaber(endpoints: (PixelPoint(x: x1, y: y1), PixelPoint(x: x2, y: y2)),
                      color: color, isFresh: true)
    }

    private func recordBridge(_ frames: [BridgeFrame], colors: DebugDiagnosticColors = .both,
                              width: Int = 64, height: Int = 48,
                              configure: (DebugVideoRecorder) -> Void = { _ in },
                              labels: [UInt64: DebugSegmentLabel] = [:],
                              afterFrame: (UInt64, DebugVideoRecorder) -> Void = { _, _ in })
        async throws -> (recording: DebugRecordingResult, metadata: [String: Any]) {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberBridgeTests-\(UUID().uuidString)", isDirectory: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: directory) }
        let recorder = try DebugVideoRecorder(directory: directory, diagnosticColors: colors)
        configure(recorder)
        for frame in frames {
            let buffer = solidPixelBuffer(width: width, height: height)
            CVPixelBufferLockBaseAddress(buffer, .readOnly)
            recorder.append(pixelBuffer: buffer,
                presentationTime: CMTime(value: CMTimeValue(frame.time), timescale: 30),
                frameID: frame.id, results: frame.sabers,
                analysis: SaberFrameAnalysis(candidates: [.red: [], .blue: []], selected: [:]),
                segmentLabel: labels[frame.id] ?? .unlabeled)
            CVPixelBufferUnlockBaseAddress(buffer, .readOnly)
            afterFrame(frame.id, recorder)
            Thread.sleep(forTimeInterval: 0.02)
        }
        let recording: DebugRecordingResult = try await withCheckedThrowingContinuation { continuation in
            recorder.finish { continuation.resume(with: $0) }
        }
        let metadata = try XCTUnwrap(JSONSerialization.jsonObject(
            with: Data(contentsOf: recording.metadataURL)) as? [String: Any])
        return (recording, metadata)
    }

    private func bridgeEvents(_ metadata: [String: Any]) -> [[String: Any]] {
        metadata["bridgeDropoutEvents"] as? [[String: Any]] ?? []
    }

    func testBridgeDropoutKeepsBeforeDropoutAfterOriginalsAndAnnotatedPNG() async throws {
        let blue = bridgeSaber(.blue)
        let (recording, metadata) = try await recordBridge([
            (401, 0, [blue]), (402, 1, []), (403, 2, [blue]), (404, 3, [blue]), (405, 4, [blue])])
        XCTAssertEqual(metadata["activeColors"] as? [String], ["red", "blue"])
        let events = bridgeEvents(metadata)
        XCTAssertEqual(events.count, 1)
        let event = try XCTUnwrap(events.first)
        XCTAssertEqual(event["color"] as? String, "blue")
        XCTAssertEqual([event["beforeFrameID"], event["dropoutFrameID"], event["afterFrameID"]]
            .compactMap { $0 as? Int }, [401, 402, 403])
        XCTAssertEqual(event["missingFrameCount"] as? Int, 1)
        let windows = try XCTUnwrap(metadata["diagnosticWindows"] as? [String: Any])
        XCTAssertNil(windows["red"])
        XCTAssertEqual((windows["blue"] as? [String: Any])?["firstSuccessFrameID"] as? Int, 401)
        XCTAssertEqual((windows["blue"] as? [String: Any])?["lastSuccessFrameID"] as? Int, 405)
        let forensic = try XCTUnwrap(recording.forensicDirectoryURL)
        let images = try XCTUnwrap(event["images"] as? [[String: Any]])
        XCTAssertEqual(images.compactMap { $0["role"] as? String },
                       ["before_success", "dropout", "after_success"])
        for image in images {
            let name = try XCTUnwrap(image["fileName"] as? String)
            XCTAssertTrue(FileManager.default.fileExists(atPath: forensic.appendingPathComponent(name).path), name)
        }
        // The original dropout PNG is untouched; the annotated copy differs from it.
        let original = try XCTUnwrap(images[1]["fileName"] as? String)
        let annotated = try XCTUnwrap(images[1]["annotatedFileName"] as? String)
        XCTAssertNotEqual(original, annotated)
        func pixel(_ name: String) throws -> [UInt8] {
            let cg = try XCTUnwrap(UIImage(contentsOfFile: forensic.appendingPathComponent(name).path)?.cgImage)
            let data = try XCTUnwrap(cg.dataProvider?.data as Data?)
            let offset = 24 * cg.bytesPerRow + 16 * (cg.bitsPerPixel / 8)
            return Array(data[offset..<offset + 3])
        }
        XCTAssertNotEqual(try pixel(original), try pixel(annotated))
        XCTAssertEqual(try pixel(original), try pixel(try XCTUnwrap(images[0]["fileName"] as? String)))
        let annotation = try XCTUnwrap(event["annotation"] as? [String: Any])
        XCTAssertEqual(annotation["groundTruth"] as? Bool, false)

        // The triage bundle carries ONE event: three originals plus a derived annotated image.
        let bundle = try XCTUnwrap(recording.triageBundleURL)
        let summary = try XCTUnwrap(JSONSerialization.jsonObject(with:
            Data(contentsOf: bundle.appendingPathComponent("summary.json"))) as? [String: Any])
        let entries = try XCTUnwrap(summary["images"] as? [[String: Any]])
        XCTAssertEqual(entries.compactMap { $0["role"] as? String },
                       ["before_success", "dropout", "annotated_dropout", "after_success"])
        XCTAssertEqual(Set(entries.compactMap { $0["bridgeEventID"] as? Int }).count, 1)
        XCTAssertEqual(Set(entries.compactMap { $0["evidenceUnit"] as? String }).count, 1)
        XCTAssertEqual(entries.filter { $0["auxiliary"] as? Bool == true }.count, 1)
        XCTAssertNotNil(entries[2]["derivedFromImage"] as? String)
        for entry in entries {
            let context = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf:
                bundle.appendingPathComponent(try XCTUnwrap(entry["frameContextPath"] as? String)))) as? [String: Any])
            XCTAssertNotNil(context["bridgeEvent"])
            XCTAssertLessThan(try Data(contentsOf: bundle.appendingPathComponent(
                try XCTUnwrap(entry["frameContextPath"] as? String))).count, 32 * 1024)
        }
    }

    func testBridgeDropoutsAreTrackedPerColorAcrossSeveralMissingFrames() async throws {
        let red = bridgeSaber(.red)
        let blue = bridgeSaber(.blue, 8, 20, 52, 20)
        let (_, metadata) = try await recordBridge([
            (501, 0, [red, blue]), (502, 1, []), (503, 2, []), (504, 3, [red, blue])])
        let events = bridgeEvents(metadata)
        XCTAssertEqual(Set(events.compactMap { $0["color"] as? String }), ["red", "blue"])
        XCTAssertTrue(events.allSatisfy { $0["missingFrameCount"] as? Int == 2 })
        let windows = try XCTUnwrap(metadata["diagnosticWindows"] as? [String: Any])
        XCTAssertNotNil(windows["red"])
        XCTAssertNotNil(windows["blue"])
    }

    func testAbsenceBeforeFirstAndAfterLastSuccessIsNeverCaptured() async throws {
        let red = bridgeSaber(.red)
        let (recording, metadata) = try await recordBridge([
            (601, 0, []), (602, 1, []), (603, 2, []),
            (604, 3, [red]), (605, 4, [red]), (606, 5, [red]),
            (607, 6, []), (608, 7, []), (609, 8, [])])
        XCTAssertTrue(bridgeEvents(metadata).isEmpty)
        let summary = try XCTUnwrap(metadata["bridgeDropoutSummary"] as? [String: Any])
        XCTAssertEqual(summary["unclosedAtStop"] as? Int, 1)
        XCTAssertEqual(summary["accepted"] as? Int, 0)
        let windows = try XCTUnwrap(metadata["diagnosticWindows"] as? [String: Any])
        let window = try XCTUnwrap(windows["red"] as? [String: Any])
        XCTAssertEqual(window["firstSuccessFrameID"] as? Int, 604)
        XCTAssertEqual(window["lastSuccessFrameID"] as? Int, 606)
        // Start/end absence leaves no candidate=0 evidence image behind.
        if let bundle = recording.triageBundleURL {
            let summary = try XCTUnwrap(JSONSerialization.jsonObject(with:
                Data(contentsOf: bundle.appendingPathComponent("summary.json"))) as? [String: Any])
            let images = summary["images"] as? [[String: Any]] ?? []
            XCTAssertTrue(images.allSatisfy { ($0["failureType"] as? String)?.contains("candidate_zero") != true })
            XCTAssertTrue(images.isEmpty)
        }
    }

    func testLongAbsenceBetweenSuccessesIsNotABridgeDropout() async throws {
        let red = bridgeSaber(.red)
        // The saber is gone for 3 s of presentation time before it returns.
        let (_, metadata) = try await recordBridge([
            (701, 0, [red]), (702, 1, []), (703, 2, []), (704, 95, [red])])
        XCTAssertTrue(bridgeEvents(metadata).isEmpty)
        let summary = try XCTUnwrap(metadata["bridgeDropoutSummary"] as? [String: Any])
        XCTAssertEqual((summary["rejected"] as? [String: Int])?["gap_too_long"], 1)
    }

    func testBridgeCopiesAreReleasedOnceTheLossOutlastsTheMaximumGap() async throws {
        let red = bridgeSaber(.red)
        var retained: [UInt64: Int] = [:]
        // Lost at 1/30 s, still lost at 1.0 s, then at 2.33 s (> maximumGapSeconds),
        // and back at 2.4 s: that return can only be rejected as gap_too_long.
        let (_, metadata) = try await recordBridge(
            [(901, 0, [red]), (902, 1, []), (903, 30, []), (904, 70, []), (905, 72, [red])],
            colors: .red, afterFrame: { id, recorder in retained[id] = recorder.bridgeRetainedBytesForTesting })
        XCTAssertEqual(retained[901], 0)
        XCTAssertGreaterThanOrEqual(retained[902] ?? 0, 2 * 64 * 48 * 4, "before + dropout BGRA copies")
        XCTAssertEqual(retained[903], retained[902], "still a possible bridge dropout")
        XCTAssertEqual(retained[904], 0, "copies released once the gap exceeds the outer bound")
        XCTAssertEqual(retained[905], 0)
        XCTAssertTrue(bridgeEvents(metadata).isEmpty)
        let summary = try XCTUnwrap(metadata["bridgeDropoutSummary"] as? [String: Any])
        XCTAssertEqual(summary["observedDropouts"] as? Int, 1)
        XCTAssertEqual((summary["rejected"] as? [String: Int])?["gap_too_long"], 1)
        XCTAssertNil((summary["rejected"] as? [String: Int])?["memory_unavailable"])
        XCTAssertEqual(summary["unclosedAtStop"] as? Int, 0)
    }

    func testColorThatNeverReturnsReleasesItsBridgeCopiesBeforeStop() async throws {
        let red = bridgeSaber(.red)
        var retained: [UInt64: Int] = [:]
        let (_, metadata) = try await recordBridge(
            [(911, 0, [red]), (912, 1, []), (913, 61, []), (914, 62, [])],
            colors: .red, afterFrame: { id, recorder in retained[id] = recorder.bridgeRetainedBytesForTesting })
        XCTAssertGreaterThan(retained[912] ?? 0, 0)
        XCTAssertEqual(retained[913], 0)
        XCTAssertEqual(retained[914], 0)
        let summary = try XCTUnwrap(metadata["bridgeDropoutSummary"] as? [String: Any])
        XCTAssertEqual(summary["unclosedAtStop"] as? Int, 1)
    }

    func testFailedAfterCopyNeverEvictsAStoredBridgeEvent() async throws {
        let red = bridgeSaber(.red)
        // Two stored events (gaps 2/30 and 3/30); a longer third one would evict the
        // shorter, but its after-success copy fails: both stored events must remain.
        let (_, metadata) = try await recordBridge([
            (1, 0, [red]), (2, 1, []), (3, 2, [red]),
            (4, 3, []), (5, 4, []), (6, 5, [red]),
            (7, 6, []), (8, 7, []), (9, 8, []), (10, 9, [red])],
            colors: .red, configure: { $0.injectedBridgeAfterCopyFailureFrameIDForTesting = 10 })
        let events = bridgeEvents(metadata)
        XCTAssertEqual(events.compactMap { $0["beforeFrameID"] as? Int }, [1, 3])
        let summary = try XCTUnwrap(metadata["bridgeDropoutSummary"] as? [String: Any])
        XCTAssertEqual(summary["accepted"] as? Int, 2)
        XCTAssertEqual(summary["evictedForLongerEvent"] as? Int, 0)
        XCTAssertEqual((summary["rejected"] as? [String: Int])?["memory_unavailable"], 1)
        XCTAssertEqual(summary["retained"] as? Int, 2)
    }

    func testLongerBridgeEventEvictsTheShortestOnlyAfterItsCopySucceeds() async throws {
        let red = bridgeSaber(.red)
        let (_, metadata) = try await recordBridge([
            (1, 0, [red]), (2, 1, []), (3, 2, [red]),
            (4, 3, []), (5, 4, []), (6, 5, [red]),
            (7, 6, []), (8, 7, []), (9, 8, []), (10, 9, [red])], colors: .red)
        XCTAssertEqual(bridgeEvents(metadata).compactMap { $0["beforeFrameID"] as? Int }, [3, 6])
        let summary = try XCTUnwrap(metadata["bridgeDropoutSummary"] as? [String: Any])
        XCTAssertEqual(summary["evictedForLongerEvent"] as? Int, 1)
        XCTAssertEqual(summary["accepted"] as? Int, 3)
    }

    func testDiscontinuousReappearanceIsNotABridgeDropout() async throws {
        let before = bridgeSaber(.red, 8, 24, 52, 24)
        let elsewhere = bridgeSaber(.red, 600, 400, 560, 440)
        let (_, metadata) = try await recordBridge([
            (751, 0, [before]), (752, 1, []), (753, 2, [elsewhere])], width: 640, height: 480)
        XCTAssertTrue(bridgeEvents(metadata).isEmpty)
        let summary = try XCTUnwrap(metadata["bridgeDropoutSummary"] as? [String: Any])
        XCTAssertEqual((summary["rejected"] as? [String: Int])?["discontinuous"], 1)
    }

    func testDiagnosticColorSelectionIgnoresTheOtherColorsAbsence() async throws {
        let red = bridgeSaber(.red)
        let blue = bridgeSaber(.blue, 8, 20, 52, 20)
        let sequence: [BridgeFrame] = [
            (801, 0, [red, blue]), (802, 1, []), (803, 2, [red, blue])]
        let redOnly = try await recordBridge(sequence, colors: .red).metadata
        XCTAssertEqual(redOnly["activeColors"] as? [String], ["red"])
        XCTAssertEqual(bridgeEvents(redOnly).compactMap { $0["color"] as? String }, ["red"])
        XCTAssertNil((redOnly["diagnosticWindows"] as? [String: Any])?["blue"])
        let blueOnly = try await recordBridge(sequence, colors: .blue).metadata
        XCTAssertEqual(blueOnly["activeColors"] as? [String], ["blue"])
        XCTAssertEqual(bridgeEvents(blueOnly).compactMap { $0["color"] as? String }, ["blue"])
        // RED mode: the saber that is only ever missing on BLUE is not a failure.
        let blueMissing = try await recordBridge([
            (811, 0, [red]), (812, 1, [red]), (813, 2, [red])], colors: .red).metadata
        XCTAssertTrue(bridgeEvents(blueMissing).isEmpty)
        let redMissing = try await recordBridge([
            (821, 0, [blue]), (822, 1, [blue]), (823, 2, [blue])], colors: .blue).metadata
        XCTAssertTrue(bridgeEvents(redMissing).isEmpty)
        let both = try await recordBridge(sequence, colors: .both).metadata
        XCTAssertEqual(Set(bridgeEvents(both).compactMap { $0["color"] as? String }), ["red", "blue"])
    }

    func testMotionEventKeepsMatchingPreAtPostLosslessFrames() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberMotionCaptureTests-\(UUID().uuidString)",
                                    isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let recorder = try DebugVideoRecorder(directory: directory)
        let red = DetectedSaber(
            endpoints: (PixelPoint(x: 8, y: 24), PixelPoint(x: 52, y: 24)),
            color: .red, isFresh: true)
        // One slow frame is a processing_time event on a saber that stays visible;
        // absence at the end of a recording is deliberately not an event image.
        for offset in 0..<45 {
            let buffer = solidPixelBuffer(width: 64, height: 48)
            CVPixelBufferLockBaseAddress(buffer, .readOnly)
            let result = recorder.append(pixelBuffer: buffer,
                presentationTime: CMTime(value: CMTimeValue(offset), timescale: 30),
                frameID: UInt64(100 + offset), results: [red],
                processingTimeSeconds: offset == 20 ? 0.12 : 0.004)
            CVPixelBufferUnlockBaseAddress(buffer, .readOnly)
            XCTAssertEqual(result, .accepted)
            Thread.sleep(forTimeInterval: 0.02)
        }
        let recording: DebugRecordingResult = try await withCheckedThrowingContinuation {
            continuation in recorder.finish { continuation.resume(with: $0) }
        }
        let fullMetadata = try XCTUnwrap(JSONSerialization.jsonObject(with:
            Data(contentsOf: recording.metadataURL)) as? [String: Any])
        let motionSummary = try XCTUnwrap(fullMetadata["motionSummary"] as? [String: Any])
        let distributions = try XCTUnwrap(motionSummary["signalDistributions"] as? [String: [String: Any]])
        XCTAssertEqual(distributions["processing_time"]?["count"] as? Int, 1)
        XCTAssertEqual(distributions["dropout"]?["count"] as? Int, 0)
        XCTAssertEqual(distributions["near_miss"]?["count"] as? Int, 0)
        let runtime = try XCTUnwrap(motionSummary["runtime"] as? [String: Any])
        let peakBytes = try XCTUnwrap(runtime["peakRetainedBGRABytes"] as? Int)
        XCTAssertLessThanOrEqual(peakBytes,
            DebugMotionThresholds.maximumRetainedBGRABytes)
        let headroom = try XCTUnwrap(runtime["memoryHeadroom"] as? [String: Any])
        XCTAssertEqual(headroom["source"] as? String, "os_proc_available_memory")
        if headroom["available"] as? Bool == true {
            XCTAssertGreaterThan(try XCTUnwrap(headroom["minimumAvailableBytes"] as? Int), 0)
        } else {
            XCTAssertNil(headroom["minimumAvailableBytes"])
        }
        print("MOTION_PROFILING frames=\(runtime["observedFrames"] ?? 0) "
            + "mean_ms=\(runtime["meanObservationMs"] ?? 0) "
            + "max_ms=\(runtime["maxObservationMs"] ?? 0) "
            + "peak_bgra_bytes=\(peakBytes)")
        let bundle = try XCTUnwrap(recording.triageBundleURL)
        let summary = try XCTUnwrap(JSONSerialization.jsonObject(with:
            Data(contentsOf: bundle.appendingPathComponent("summary.json"))) as? [String: Any])
        let eventSummary = try XCTUnwrap(summary["motionEventSummary"] as? [String: Any])
        let ledger = try XCTUnwrap(eventSummary["events"] as? [[Any]])
        XCTAssertEqual(ledger.first?[3] as? String, "selected")
        let images = try XCTUnwrap(summary["images"] as? [[String: Any]])
        let motion = images.filter { $0["eventIndex"] as? Int != nil }
        XCTAssertEqual(motion.count, 3)
        XCTAssertEqual(motion.compactMap { $0["role"] as? String },
                       ["event_at", "event_pre", "event_post"])
        XCTAssertEqual(motion.compactMap { $0["frameID"] as? Int }, [120, 115, 135])
        let forensic = try XCTUnwrap(recording.forensicDirectoryURL)
        XCTAssertTrue(FileManager.default.fileExists(atPath: forensic
            .appendingPathComponent("motion_event_0_overlay_120.png").path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: bundle
            .appendingPathComponent("images/motion_event_0_overlay_120.png").path))
        for image in motion {
            let path = try XCTUnwrap(image["path"] as? String)
            let contextPath = try XCTUnwrap(image["frameContextPath"] as? String)
            let id = try XCTUnwrap(image["frameID"] as? Int)
            let context = try XCTUnwrap(JSONSerialization.jsonObject(with:
                Data(contentsOf: bundle.appendingPathComponent(contextPath))) as? [String: Any])
            XCTAssertEqual(context["selectedFrameID"] as? Int, id)
            XCTAssertNotNil(UIImage(contentsOfFile: bundle.appendingPathComponent(path).path))
        }
    }

    func testCandidateDiagnosticsCopyPerformanceSample() throws {
        let fixture = try fixtureBGRA("blue-led-bright-large-05")
        var withoutDiagnostics: [Double] = []
        var withDiagnostics: [Double] = []
        for _ in 0..<20 {
            var start = ProcessInfo.processInfo.systemUptime
            _ = analyzeSabers(in: fixture.bytes, width: fixture.width, height: fixture.height,
                              bytesPerRow: fixture.bytesPerRow,
                              redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(),
                              collectProfile: false)
            withoutDiagnostics.append((ProcessInfo.processInfo.systemUptime - start) * 1000)
            start = ProcessInfo.processInfo.systemUptime
            let analysis = analyzeSabers(in: fixture.bytes, width: fixture.width,
                                         height: fixture.height,
                                         bytesPerRow: fixture.bytesPerRow,
                                         redThreshold: ColorThreshold(),
                                         blueThreshold: ColorThreshold(),
                                         collectProfile: false)
            _ = DebugRecordingCandidateDiagnostics(analysis: analysis)
            withDiagnostics.append((ProcessInfo.processInfo.systemUptime - start) * 1000)
        }
        func summary(_ values: [Double]) -> (median: Double, p95: Double, max: Double) {
            let sorted = values.sorted()
            return (sorted[sorted.count / 2],
                    sorted[min(sorted.count - 1, Int(Double(sorted.count) * 0.95))],
                    sorted.last ?? 0)
        }
        let baseline = summary(withoutDiagnostics)
        let recorded = summary(withDiagnostics)
        print(String(format: "[CandidateDiagnosticsPerformance] simulator OFF median=%.3f p95=%.3f max=%.3f ms; ON median=%.3f p95=%.3f max=%.3f ms (n=20; not device FPS)",
                     baseline.median, baseline.p95, baseline.max,
                     recorded.median, recorded.p95, recorded.max))
    }

    func testMotionPreRollIsThinnedBoundedAndExpires() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberMotionRing-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let recorder = try DebugVideoRecorder(directory: directory)
        try recorder.prepare(width: 64, height: 48)
        for index in 0..<76 {
            let outcome = recorder.append(pixelBuffer: solidPixelBuffer(width: 64, height: 48),
                presentationTime: CMTime(value: CMTimeValue(index), timescale: 30),
                frameID: UInt64(index), results: [])
            XCTAssertEqual(outcome, .accepted)
            XCTAssertLessThanOrEqual(recorder.motionPreRollFrameIDsForTesting.count,
                                     DebugMotionThresholds.preRollBuffers)
            XCTAssertLessThanOrEqual(recorder.motionRetainedBytesForTesting,
                                     DebugMotionThresholds.maximumRetainedBGRABytes)
            Thread.sleep(forTimeInterval: 1.0 / 30.0)
        }
        XCTAssertEqual(recorder.motionPreRollFrameIDsForTesting, [30, 45, 60, 75])
        // A presentation-time discontinuity expires older pre-roll frames.
        recorder.append(pixelBuffer: solidPixelBuffer(width: 64, height: 48),
            presentationTime: CMTime(value: 180, timescale: 30), frameID: 180, results: [])
        // Expiration occurs every sampling tick; check policy at the next tick.
        for index in 77...90 {
            Thread.sleep(forTimeInterval: 1.0 / 30.0)
            recorder.append(pixelBuffer: solidPixelBuffer(width: 64, height: 48),
                presentationTime: CMTime(value: CMTimeValue(index + 104), timescale: 30),
                frameID: UInt64(index + 104), results: [])
        }
        XCTAssertTrue(recorder.motionPreRollFrameIDsForTesting.allSatisfy { $0 >= 180 })
        _ = try await withCheckedThrowingContinuation { continuation in
            recorder.finish { continuation.resume(with: $0) }
        } as DebugRecordingResult
    }

    private func residentBytesForPerformance() -> UInt64 {
        var info = mach_task_basic_info()
        var count = mach_msg_type_number_t(MemoryLayout<mach_task_basic_info>.size
                                          / MemoryLayout<integer_t>.size)
        let result = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                task_info(mach_task_self_, task_flavor_t(MACH_TASK_BASIC_INFO), $0, &count)
            }
        }
        return result == KERN_SUCCESS ? info.resident_size : 0
    }

    func testDebugRecordingAppendPerformanceSample() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberRecordingPerformance-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let recorder = try DebugVideoRecorder(directory: directory)
        let initialResidentBytes = residentBytesForPerformance()
        var peakResidentBytes = initialResidentBytes
        let prepareStart = ProcessInfo.processInfo.systemUptime
        try recorder.prepare(width: 480, height: 640)
        let prepareMilliseconds = (ProcessInfo.processInfo.systemUptime - prepareStart) * 1000
        var appendMilliseconds: [Double] = []
        for index in 0..<15 {
            let pixelBuffer = solidPixelBuffer(width: 480, height: 640)
            let start = ProcessInfo.processInfo.systemUptime
            recorder.append(pixelBuffer: pixelBuffer,
                            presentationTime: CMTime(value: CMTimeValue(index), timescale: 30),
                            frameID: UInt64(index), results: [])
            appendMilliseconds.append((ProcessInfo.processInfo.systemUptime - start) * 1000)
            peakResidentBytes = max(peakResidentBytes, residentBytesForPerformance())
            Thread.sleep(forTimeInterval: 1.0 / 30.0)
        }
        let recording: DebugRecordingResult = try await withCheckedThrowingContinuation { continuation in
            recorder.finish { continuation.resume(with: $0) }
        }
        XCTAssertEqual(recording.recordedFrameCount, 15)
        XCTAssertEqual(recording.droppedFrameCount, 0)
        let sorted = appendMilliseconds.sorted()
        let p95 = sorted[min(sorted.count - 1, Int(Double(sorted.count) * 0.95))]
        print(String(format: "[RecordingPerformance] simulator one-time prepare=%.3f ms; append median=%.3f p95=%.3f max=%.3f ms (480x640, n=%d; not device FPS)",
                     prepareMilliseconds, sorted[sorted.count / 2], p95,
                     sorted.max() ?? 0, sorted.count))
        print("[RecordingMemory] simulator resident_start=\(initialResidentBytes) resident_peak=\(peakResidentBytes) delta=\(peakResidentBytes - initialResidentBytes) bytes; includes writer and runtime")
    }

    private struct DecodedVideoFrame {
        let timestamp: CMTime
        let width: Int
        let height: Int
        let bytesPerRow: Int
        let bytes: [UInt8]

        func pixel(_ x: Int, _ y: Int) -> (b: UInt8, g: UInt8, r: UInt8) {
            let offset = y * bytesPerRow + x * 4
            return (bytes[offset], bytes[offset + 1], bytes[offset + 2])
        }
    }

    func testTrackingRecordingSelectsConsecutivePeakWindowAndExactPNGMapping() async throws {
        for missingFrameID: UInt64? in [nil, 1_011] {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberTrackingWindow-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let recorder = try DebugVideoRecorder(directory: directory)
        recorder.injectedTrackingCopyFailureFrameIDForTesting = missingFrameID
        for offset in 0..<25 {
            let buffer = solidPixelBuffer(width: 64, height: 48)
            CVPixelBufferLockBaseAddress(buffer, [])
            if let base = CVPixelBufferGetBaseAddress(buffer) {
                let bytes = base.assumingMemoryBound(to: UInt8.self)
                for row in 0..<48 {
                    for column in 0..<64 {
                        for channel in 0..<3 {
                            bytes[row * CVPixelBufferGetBytesPerRow(buffer) + column * 4 + channel] = UInt8(16 + offset)
                        }
                    }
                }
            }
            let points = (8...52).flatMap { x in (20...24).map { y in
                offset == 10 ? PixelPoint(x: 30 + (y - 22), y: x - 5) : PixelPoint(x: x, y: y)
            } }
            let candidate = try XCTUnwrap(saberCandidate(from: points, width: 64, height: 48,
                                                         collectEndpointDiagnostics: true))
            let analysis = SaberFrameAnalysis(candidates: [.red: [candidate]], selected: [.red: candidate.endpoints])
            let result = DetectedSaber(endpoints: candidate.endpoints, color: .red, isFresh: true)
            XCTAssertEqual(recorder.append(pixelBuffer: buffer,
                presentationTime: CMTime(value: CMTimeValue(offset), timescale: 30),
                frameID: UInt64(1_000 + offset), results: [result], analysis: analysis), .accepted)
            CVPixelBufferUnlockBaseAddress(buffer, [])
            recorder.recordTransmission(frameID: UInt64(1_000 + offset), color: "red",
                coordinates: "8,22,52,22", sourceEndpoints: candidate.endpoints)
            Thread.sleep(forTimeInterval: 0.04)
        }
        let recording: DebugRecordingResult = try await withCheckedThrowingContinuation {
            continuation in recorder.finish { continuation.resume(with: $0) }
        }
        let metadata = try JSONDecoder().decode(DebugRecordingMetadata.self, from: Data(contentsOf: recording.metadataURL))
        let maximum = try XCTUnwrap(metadata.frames.max {
            ($0.tracking["red"]?.instabilityScore ?? 0) < ($1.tracking["red"]?.instabilityScore ?? 0)
        })
        let bundle = try XCTUnwrap(recording.triageBundleURL, recording.triageErrorMessage ?? "missing bundle")
        let summary = try XCTUnwrap(JSONSerialization.jsonObject(with:
            Data(contentsOf: bundle.appendingPathComponent("summary.json"))) as? [String: Any])
        let images = try XCTUnwrap(summary["images"] as? [[String: Any]])
            .filter { $0["eventIndex"] as? Int == 1_000_000_000 }
        let motion = try XCTUnwrap(summary["motionEventSummary"] as? [String: Any])
        let capture = try XCTUnwrap(motion["trackingCapture"] as? [String: Any])
        let center = try XCTUnwrap(capture["retainedPeakFrameID"] as? UInt64)
        let retainedScore = try XCTUnwrap(capture["retainedPeakScore"] as? Double)
        XCTAssertEqual(retainedScore, metadata.frames.first { $0.frameID == center }?.tracking["red"]?.instabilityScore)
        if missingFrameID != nil {
            XCTAssertEqual(capture["recordingMaxFrameID"] as? UInt64, maximum.frameID)
            XCTAssertTrue(try XCTUnwrap(capture["highestRankedFrameMissing"] as? Bool))
            XCTAssertNotEqual(center, maximum.frameID)
        } else {
            XCTAssertEqual(center, maximum.frameID)
        }
        let expected = ((center - 5)...(center + 5)).filter { $0 != missingFrameID }
        XCTAssertEqual(images.count, expected.count)
        XCTAssertEqual(images.compactMap { $0["frameID"] as? UInt64 }, expected)
        XCTAssertEqual(images[5]["anomalyScore"] as? Double, retainedScore)
        XCTAssertEqual(images[5]["role"] as? String, "peak")
        for image in images {
            let frameID = try XCTUnwrap(image["frameID"] as? Int)
            let path = try XCTUnwrap(image["path"] as? String)
            let contextPath = try XCTUnwrap(image["frameContextPath"] as? String)
            let context = try XCTUnwrap(JSONSerialization.jsonObject(with:
                Data(contentsOf: bundle.appendingPathComponent(contextPath))) as? [String: Any])
            let mapping = try XCTUnwrap(context["imageMapping"] as? [String: Any])
            XCTAssertEqual(mapping["frameID"] as? Int, frameID)
            XCTAssertEqual(mapping["image"] as? String, path)
            XCTAssertEqual(mapping["sessionID"] as? String, recording.sessionID)
            XCTAssertEqual(mapping["eventRole"] as? String, image["role"] as? String)
            let transmissions = try XCTUnwrap(context["udpTransmissions"] as? [[String: Any]])
            XCTAssertEqual(transmissions.first?["frameID"] as? Int, frameID)
            let png = try XCTUnwrap(UIImage(contentsOfFile: bundle.appendingPathComponent(path).path)?.cgImage)
            let data = try XCTUnwrap(png.dataProvider?.data)
            let pixels = try XCTUnwrap(CFDataGetBytePtr(data))
            XCTAssertEqual(pixels[0], UInt8(16 + frameID - 1_000))
        }
        }
    }

    private func solidPixelBuffer(width: Int, height: Int) -> CVPixelBuffer {
        var buffer: CVPixelBuffer?
        CVPixelBufferCreate(kCFAllocatorDefault, width, height, kCVPixelFormatType_32BGRA,
                            [kCVPixelBufferIOSurfacePropertiesKey as String: [:]] as CFDictionary,
                            &buffer)
        let pixelBuffer = buffer!
        CVPixelBufferLockBaseAddress(pixelBuffer, [])
        if let base = CVPixelBufferGetBaseAddress(pixelBuffer) {
            memset(base, 16, CVPixelBufferGetBytesPerRow(pixelBuffer) * height)
            let bytes = base.assumingMemoryBound(to: UInt8.self)
            for y in 0..<height {
                for x in 0..<width { bytes[y * CVPixelBufferGetBytesPerRow(pixelBuffer) + x * 4 + 3] = 255 }
            }
        }
        CVPixelBufferUnlockBaseAddress(pixelBuffer, [])
        return pixelBuffer
    }

    private func decodedVideoSamples(_ url: URL) throws -> [DecodedVideoFrame] {
        let asset = AVURLAsset(url: url)
        let track = try XCTUnwrap(asset.tracks(withMediaType: .video).first)
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA
        ])
        output.alwaysCopiesSampleData = true
        reader.add(output)
        XCTAssertTrue(reader.startReading())
        var frames: [DecodedVideoFrame] = []
        while let sample = output.copyNextSampleBuffer(),
              let pixelBuffer = CMSampleBufferGetImageBuffer(sample) {
            CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly)
            let height = CVPixelBufferGetHeight(pixelBuffer)
            let bytesPerRow = CVPixelBufferGetBytesPerRow(pixelBuffer)
            let data = Data(bytes: CVPixelBufferGetBaseAddress(pixelBuffer)!, count: bytesPerRow * height)
            CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly)
            frames.append(DecodedVideoFrame(
                timestamp: CMSampleBufferGetPresentationTimeStamp(sample),
                width: CVPixelBufferGetWidth(pixelBuffer), height: height,
                bytesPerRow: bytesPerRow, bytes: Array(data)
            ))
        }
        XCTAssertEqual(reader.status, .completed)
        return frames
    }
}

// MARK: - Emitter diagnostics and shadow verdicts (Debug Recording only)

extension DetectionCoreTests {
    /// Every fixture image bundled with the tests, plus synthetic red bars.
    private func diagnosticParityImages() throws -> [(String, (bytes: [UInt8], width: Int, height: Int, bytesPerRow: Int))] {
        var images: [(String, (bytes: [UInt8], width: Int, height: Int, bytesPerRow: Int))] = []
        for name in ["blue-led-with-curtain-reflection-01", "blue-led-with-curtain-reflection-02",
                     "blue-led-with-left-curtain-reflection-03", "blue-led-with-left-curtain-reflection-04",
                     "blue-led-bright-large-05"] {
            images.append((name, try fixtureBGRA(name)))
        }
        for subdirectory in ["production-video-IMG_5933", "forensic-20260921", "forensic-20260921-211845"] {
            let urls = Bundle(for: Self.self).urls(forResourcesWithExtension: "png", subdirectory: subdirectory) ?? []
            XCTAssertFalse(urls.isEmpty, "\(subdirectory) fixtures must be bundled")
            for url in urls.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) {
                let name = url.deletingPathExtension().lastPathComponent
                images.append(("\(subdirectory)/\(name)", try fixtureBGRA(name, subdirectory: subdirectory)))
            }
        }
        images.append(("synthetic-thin-red", syntheticRedBar(thickness: 6)))
        images.append(("synthetic-thick-red", syntheticRedBar(thickness: 24)))
        return images
    }

    /// A uniform saturated red bar (R 250, G 40, B 30) on black at 480x640.
    private func syntheticRedBar(thickness: Int, width: Int = 480, height: Int = 640)
        -> (bytes: [UInt8], width: Int, height: Int, bytesPerRow: Int) {
        let stride = width * 4
        var bytes = Array(repeating: UInt8(0), count: stride * height)
        for y in 300..<(300 + thickness) {
            for x in 100..<300 {
                let offset = y * stride + x * 4
                bytes[offset] = 30; bytes[offset + 1] = 40; bytes[offset + 2] = 250; bytes[offset + 3] = 255
            }
        }
        return (bytes, width, height, stride)
    }

    /// Diagnostics on/off must agree bit for bit on every candidate. Core-line
    /// scoring no longer depends on Set iteration order (saberCandidate(from:)
    /// sorts its points), so no tolerance is needed even without a fixed hash
    /// seed; a tolerance here would hide that non-determinism returning.
    private func assertIdenticalRecognition(_ lhs: SaberFrameAnalysis, _ rhs: SaberFrameAnalysis,
                                            _ label: String) {
        func key(_ e: (PixelPoint, PixelPoint)?) -> [Int]? { e.map { [$0.0.x, $0.0.y, $0.1.x, $0.1.y] } }
        for color in [SaberColor.red, .blue] {
            let a = lhs.candidates[color] ?? [], b = rhs.candidates[color] ?? []
            XCTAssertEqual(key(lhs.selected[color]), key(rhs.selected[color]), "\(label) \(color) output")
            XCTAssertEqual(a.count, b.count, "\(label) \(color) candidate count")
            for (x, y) in zip(a, b) {
                XCTAssertEqual(x.source, y.source, label)
                XCTAssertEqual(x.isEmitterEligible, y.isEmitterEligible, "\(label) \(color) eligibility")
                XCTAssertEqual(x.isCompactRed, y.isCompactRed, label)
                XCTAssertEqual(x.peakValue, y.peakValue, label)
                XCTAssertEqual(x.pointCount, y.pointCount, label)
                XCTAssertEqual(x.usedPointLEDFallback, y.usedPointLEDFallback, label)
                XCTAssertEqual(key(x.endpoints), key(y.endpoints), "\(label) \(color) endpoints")
                XCTAssertEqual(key(x.comparisonEndpoints), key(y.comparisonEndpoints), label)
                for (name, u, v) in [("score", x.score, y.score),
                                     ("total", x.scoreBreakdown.total, y.scoreBreakdown.total),
                                     ("meanValue", x.meanValue, y.meanValue),
                                     ("highValueRatio", x.highValueRatio, y.highValueRatio),
                                     ("meanColorPurity", x.meanColorPurity, y.meanColorPurity),
                                     ("clippedWhiteRatio", x.clippedWhiteRatio, y.clippedWhiteRatio),
                                     ("localContrast", x.localContrast, y.localContrast),
                                     ("coreSupportRatio", x.coreSupportRatio, y.coreSupportRatio),
                                     ("axialDensity", x.axialDensity, y.axialDensity),
                                     ("rawPCASpan", x.rawPCASpan, y.rawPCASpan)] {
                    XCTAssertEqual(u.bitPattern, v.bitPattern, "\(label) \(color) \(x.source) \(name)")
                }
            }
        }
    }

    func testDiagnosticCollectionLeavesRecognitionUnchanged() throws {
        let images = try diagnosticParityImages()
        XCTAssertGreaterThanOrEqual(images.count, 20)
        var emitterTraces = 0
        for (name, image) in images {
            func run(_ diagnostics: Bool, _ profile: Bool) -> SaberFrameAnalysis {
                analyzeSabers(in: image.bytes, width: image.width, height: image.height,
                              bytesPerRow: image.bytesPerRow, redThreshold: ColorThreshold(),
                              blueThreshold: ColorThreshold(), collectProfile: profile,
                              collectPipelineDiagnostics: diagnostics)
            }
            let off = run(false, false)
            let on = run(true, false)
            assertIdenticalRecognition(off, on, "\(name) diagnostics")
            assertIdenticalRecognition(off, run(true, true), "\(name) diagnostics+profile")
            // The production entry point used when Debug Recording is OFF agrees too.
            let production = image.bytes.withUnsafeBufferPointer { buffer in
                detectSabers(baseAddress: buffer.baseAddress!, width: image.width, height: image.height,
                             bytesPerRow: image.bytesPerRow, redThreshold: ColorThreshold(),
                             blueThreshold: ColorThreshold())
            }
            for color in [SaberColor.red, .blue] {
                XCTAssertEqual(production[color].map { [$0.0.x, $0.0.y, $0.1.x, $0.1.y] },
                               on.selected[color].map { [$0.0.x, $0.0.y, $0.1.x, $0.1.y] }, "\(name) \(color)")
            }
            XCTAssertTrue(off.candidates.values.joined().allSatisfy { $0.endpointDiagnosticTrace == nil })
            emitterTraces += on.candidates.values.joined().filter {
                $0.endpointDiagnosticTrace?.emitter != nil }.count
        }
        XCTAssertGreaterThan(emitterTraces, 0)
    }

    func testEmitterDiagnosticsMatchRecomputationForSyntheticRedComponent() throws {
        let image = syntheticRedBar(thickness: 6)
        let analysis = analyzeSabers(in: image.bytes, width: image.width, height: image.height,
                                     bytesPerRow: image.bytesPerRow, redThreshold: ColorThreshold(),
                                     blueThreshold: ColorThreshold(), collectPipelineDiagnostics: true)
        let red = analysis.candidates[.red] ?? []
        XCTAssertFalse(red.isEmpty)
        let step = 2.0
        for candidate in red {
            let emitter = try XCTUnwrap(candidate.endpointDiagnosticTrace?.emitter, candidate.source)
            let peak = Double(candidate.peakValue)
            XCTAssertEqual(emitter.peakTerm, min(max((peak - 200) / 55, 0), 1) * 0.32)
            XCTAssertEqual(emitter.meanTerm, min(max((candidate.meanValue - 160) / 95, 0), 1) * 0.23)
            XCTAssertEqual(emitter.highValueTerm, candidate.highValueRatio * 0.28)
            XCTAssertEqual(emitter.purityTerm, candidate.meanColorPurity * 0.12)
            XCTAssertEqual(emitter.clippedWhiteTerm, candidate.clippedWhiteRatio * 0.05)
            XCTAssertEqual(emitter.emitterScore,
                           emitter.peakTerm + emitter.meanTerm + emitter.highValueTerm
                            + emitter.purityTerm + emitter.clippedWhiteTerm, accuracy: 1e-12)
            XCTAssertEqual(emitter.emitterScoreMargin, emitter.emitterScore - 0.42, accuracy: 1e-12)
            XCTAssertEqual(emitter.hasEmitterCore, emitter.coreByHighValueRatio
                           || emitter.coreByPeakAndMean || emitter.coreByClippedWhite)
            XCTAssertEqual(emitter.coreByHighValueRatio, candidate.highValueRatio >= 0.08)
            XCTAssertEqual(emitter.compactRedGate, candidate.isCompactRed)
            // No later source rule rejected this clean bar: the scoring-site verdict is final.
            XCTAssertEqual(emitter.baseEligible, candidate.isEmitterEligible)
            if !candidate.isCompactRed {
                XCTAssertEqual(emitter.baseEligible, candidate.peakValue >= 218 && emitter.hasEmitterCore
                               && emitter.emitterScore >= 0.42)
            }
            XCTAssertEqual(emitter.localContrast, candidate.localContrast)
            XCTAssertEqual(emitter.coreSupport, candidate.coreSupportRatio)
            XCTAssertEqual(emitter.brightnessVariation, candidate.brightnessVariation)
            XCTAssertEqual(emitter.majorLengthSamples, candidate.rawPCASpan / step)
            let shortSide = Double(min(image.width, image.height)) / step
            let support = min(max((emitter.majorLengthSamples / shortSide - 0.08) / 0.22, 0), 1)
            XCTAssertEqual(emitter.bladeLengthSupport, support, accuracy: 1e-12)
            XCTAssertEqual(candidate.scoreBreakdown.coreSupport,
                           candidate.coreSupportRatio * 12.0 * emitter.bladeLengthSupport, accuracy: 1e-12)
            // Uniform R 250 / G 40 / B 30: the sorted channels are exact.
            XCTAssertEqual(emitter.meanMaxChannel, 250)
            XCTAssertEqual(emitter.meanSecondChannel, 40)
            XCTAssertEqual(emitter.maxSecondChannel, 40)
            XCTAssertEqual(emitter.meanMinChannel, 30)
            XCTAssertEqual(emitter.nearWhiteFraction, 0)
            XCTAssertEqual(emitter.brightSecondChannelFraction, 0)
            XCTAssertEqual(emitter.colorSampleCount, emitter.sampleCount)
            XCTAssertEqual(emitter.sampleCount, candidate.pointCount)
            // The recorded JSON form carries the same values.
            let recorded = try XCTUnwrap(DebugRecordingCandidate(index: 0, candidate: candidate,
                                                                 selectedIndex: 0).emitterDiagnostics)
            XCTAssertEqual(recorded.emitterScoreThreshold, 0.42)
            XCTAssertEqual(recorded.emitterScore, emitter.emitterScore, accuracy: 1e-6)
            XCTAssertEqual(recorded.emitterScoreMargin, emitter.emitterScore - 0.42, accuracy: 1e-6)
            XCTAssertEqual(recorded.bladeLengthSupport, emitter.bladeLengthSupport, accuracy: 1e-6)
            XCTAssertEqual(recorded.meanSecondChannel, 40)
        }
        XCTAssertTrue((analysis.candidates[.blue] ?? []).isEmpty)
    }

    func testShadowR7eVerdictIsRecordedButNeverChangesEligibility() throws {
        // A thin (3-sample) matte-like red bar: eligible in production, without
        // clipped white and with a thin body, so the shadow R7e rule rejects it.
        let thin = syntheticRedBar(thickness: 6)
        func analysis(_ diagnostics: Bool, _ image: (bytes: [UInt8], width: Int, height: Int, bytesPerRow: Int))
            -> SaberFrameAnalysis {
            analyzeSabers(in: image.bytes, width: image.width, height: image.height,
                          bytesPerRow: image.bytesPerRow, redThreshold: ColorThreshold(),
                          blueThreshold: ColorThreshold(), collectPipelineDiagnostics: diagnostics)
        }
        let on = analysis(true, thin), off = analysis(false, thin)
        assertIdenticalRecognition(off, on, "thin red")
        XCTAssertNotNil(off.selected[.red], "production still selects the thin bar")
        let winner = try XCTUnwrap(on.candidates[.red]?.first(where: \.isEmitterEligible))
        let emitter = try XCTUnwrap(winner.endpointDiagnosticTrace?.emitter)
        let shadow = try XCTUnwrap(emitter.shadowR7e)
        XCTAssertTrue(winner.isEmitterEligible)
        XCTAssertTrue(emitter.baseEligible)
        XCTAssertFalse(shadow.ruleSatisfied)
        XCTAssertFalse(shadow.shadowEligible, "the shadow verdict differs from production")
        XCTAssertEqual(shadow.clippedWhiteRatio, winner.clippedWhiteRatio)
        XCTAssertEqual(shadow.meanColorPurity, winner.meanColorPurity)
        // Recomputation from the published candidate (mask units: step 2).
        let maskShort = Double(min(thin.width, thin.height) / 2)
        let bodyDensity = winner.axialDensity * 2
        XCTAssertEqual(shadow.density, bodyDensity)
        XCTAssertEqual(shadow.fallbackDensity, Double(winner.pointCount) / max(winner.rawPCASpan / 2, 1))
        let chosen = bodyDensity > 0 ? bodyDensity : shadow.fallbackDensity
        XCTAssertEqual(shadow.usedFallbackDensity, !(bodyDensity > 0))
        XCTAssertEqual(shadow.d240, chosen * 240 / maskShort, accuracy: 1e-12)
        XCTAssertLessThan(shadow.d240, 3.5)
        XCTAssertEqual(shadow.thickBodyMargin, shadow.d240 - 4.2, accuracy: 1e-12)
        XCTAssertEqual(shadow.clippedWhiteMargin, shadow.clippedWhiteRatio - 0.35, accuracy: 1e-12)
        let recorded = try XCTUnwrap(DebugRecordingCandidate(index: 0, candidate: winner,
                                                             selectedIndex: 0).emitterDiagnostics?.shadowR7e)
        XCTAssertFalse(recorded.applied)
        XCTAssertFalse(recorded.shadowR7eEligible)
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(recorded)) as? [String: Any])
        XCTAssertEqual(Set(json.keys), ["applied", "density", "fallbackDensity", "usedFallbackDensity", "d240",
            "clippedWhiteRatio", "meanColorPurity", "clippedWhiteMargin", "thickBodyMargin",
            "saturatedBodyDensityMargin", "saturatedBodyPurityMargin", "ruleSatisfied", "shadowR7eEligible"])

        // A thick bar satisfies the rule; production is again unchanged by collection.
        let thick = syntheticRedBar(thickness: 24)
        let thickOn = analysis(true, thick)
        assertIdenticalRecognition(analysis(false, thick), thickOn, "thick red")
        let thickWinner = try XCTUnwrap(thickOn.candidates[.red]?.first(where: \.isEmitterEligible))
        let thickShadow = try XCTUnwrap(thickWinner.endpointDiagnosticTrace?.emitter?.shadowR7e)
        XCTAssertTrue(thickShadow.ruleSatisfied)
        XCTAssertTrue(thickShadow.shadowEligible)

        // Blue candidates never carry the red-only shadow verdict.
        let blue = try fixtureBGRA("blue-led-bright-large-05")
        let blueCandidates = analysis(true, blue).candidates[.blue] ?? []
        XCTAssertFalse(blueCandidates.isEmpty)
        XCTAssertTrue(blueCandidates.allSatisfy {
            $0.endpointDiagnosticTrace?.emitter != nil && $0.endpointDiagnosticTrace?.emitter?.shadowR7e == nil })
    }

    func testShadowPF22VerdictBoundaries() {
        typealias PF22 = SaberShadowPurityFloorVerdict
        XCTAssertEqual(PF22.purityThreshold, 0.22)
        XCTAssertEqual(PF22.clippedWhiteExemptionThreshold, 0.35)
        // Purity floor: 0.22 itself passes, just below fails without the exemption.
        XCTAssertTrue(PF22(meanColorPurity: 0.22, clippedWhiteRatio: 0, baseEligible: true).shadowEligible)
        let below = PF22(meanColorPurity: 0.2199, clippedWhiteRatio: 0, baseEligible: true)
        XCTAssertFalse(below.ruleSatisfied)
        XCTAssertFalse(below.shadowEligible)
        XCTAssertEqual(below.purityMargin, 0.2199 - 0.22, accuracy: 1e-12)
        // Clipped-white exemption: 0.35 itself exempts a low-purity candidate, just below does not.
        let exempt = PF22(meanColorPurity: 0.15, clippedWhiteRatio: 0.35, baseEligible: true)
        XCTAssertTrue(exempt.ruleSatisfied)
        XCTAssertTrue(exempt.shadowEligible)
        XCTAssertEqual(exempt.clippedWhiteMargin, 0, accuracy: 1e-12)
        XCTAssertFalse(PF22(meanColorPurity: 0.15, clippedWhiteRatio: 0.3499, baseEligible: true).shadowEligible)
        // The wall-label winner of 20261003_144936_295 f2552 (purity 0.18, clip 0) would be rejected.
        XCTAssertFalse(PF22(meanColorPurity: 0.18, clippedWhiteRatio: 0, baseEligible: true).shadowEligible)
        // Never more eligible than production.
        let ineligible = PF22(meanColorPurity: 0.9, clippedWhiteRatio: 1, baseEligible: false)
        XCTAssertTrue(ineligible.ruleSatisfied)
        XCTAssertFalse(ineligible.shadowEligible)
    }

    func testShadowPF22VerdictIsRecordedButNeverChangesEligibility() throws {
        func analysis(_ diagnostics: Bool, _ image: (bytes: [UInt8], width: Int, height: Int, bytesPerRow: Int))
            -> SaberFrameAnalysis {
            analyzeSabers(in: image.bytes, width: image.width, height: image.height,
                          bytesPerRow: image.bytesPerRow, redThreshold: ColorThreshold(),
                          blueThreshold: ColorThreshold(), collectPipelineDiagnostics: diagnostics)
        }
        for thickness in [6, 24] {
            let image = syntheticRedBar(thickness: thickness)
            let on = analysis(true, image), off = analysis(false, image)
            assertIdenticalRecognition(off, on, "red bar \(thickness)")
            for candidate in on.candidates[.red] ?? [] {
                let emitter = try XCTUnwrap(candidate.endpointDiagnosticTrace?.emitter)
                let shadow = try XCTUnwrap(emitter.shadowPF22)
                // Inputs are the production values of the candidate itself.
                XCTAssertEqual(shadow.meanColorPurity, candidate.meanColorPurity)
                XCTAssertEqual(shadow.clippedWhiteRatio, candidate.clippedWhiteRatio)
                XCTAssertEqual(shadow.ruleSatisfied, candidate.meanColorPurity >= 0.22
                               || candidate.clippedWhiteRatio >= 0.35)
                XCTAssertEqual(shadow.shadowEligible, emitter.baseEligible && shadow.ruleSatisfied)
            }
        }
        let winner = try XCTUnwrap(analysis(true, syntheticRedBar(thickness: 6)).candidates[.red]?
            .first(where: \.isEmitterEligible))
        let verdict = try XCTUnwrap(winner.endpointDiagnosticTrace?.emitter?.shadowPF22)
        let recorded = try XCTUnwrap(DebugRecordingCandidate(index: 0, candidate: winner,
                                                             selectedIndex: 0).emitterDiagnostics?.shadowPF22)
        XCTAssertFalse(recorded.applied)
        XCTAssertEqual(recorded.shadowPF22Eligible, verdict.shadowEligible)
        let json = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(recorded)) as? [String: Any])
        XCTAssertEqual(Set(json.keys), ["applied", "ruleSatisfied", "shadowPF22Eligible", "meanColorPurity",
                                        "clippedWhiteRatio", "purityMargin", "clippedWhiteMargin"])
        let streamedEncoder = JSONEncoder()
        streamedEncoder.userInfo[.debugRecordingStreamedMetadata] = true
        let streamed = try XCTUnwrap(JSONSerialization.jsonObject(with: streamedEncoder.encode(recorded))
            as? [String: Any])
        XCTAssertEqual(Set(streamed.keys), ["applied", "ruleSatisfied", "shadowPF22Eligible"])
        // Older bundles without the field still decode.
        let emitterJSON = try JSONEncoder().encode(try XCTUnwrap(DebugRecordingCandidate(
            index: 0, candidate: winner, selectedIndex: 0).emitterDiagnostics))
        var legacy = try XCTUnwrap(JSONSerialization.jsonObject(with: emitterJSON) as? [String: Any])
        XCTAssertNotNil(legacy.removeValue(forKey: "shadowPF22"))
        let decoded = try JSONDecoder().decode(DebugRecordingEmitterDiagnostics.self,
                                               from: JSONSerialization.data(withJSONObject: legacy))
        XCTAssertNil(decoded.shadowPF22)
        // Triage candidate geometry carries the compact verdict.
        let geometry = DebugCandidateGeometry.emitterDictionary(try XCTUnwrap(winner.endpointDiagnosticTrace?.emitter))
        let compact = try XCTUnwrap(geometry["shadowPF22"] as? [String: Any])
        XCTAssertEqual(compact["applied"] as? Bool, false)
        XCTAssertEqual(compact["shadowPF22Eligible"] as? Bool, verdict.shadowEligible)

        // Blue candidates never carry the red-only shadow verdict.
        let blueCandidates = analysis(true, try fixtureBGRA("blue-led-bright-large-05")).candidates[.blue] ?? []
        XCTAssertFalse(blueCandidates.isEmpty)
        XCTAssertTrue(blueCandidates.allSatisfy {
            $0.endpointDiagnosticTrace?.emitter != nil && $0.endpointDiagnosticTrace?.emitter?.shadowPF22 == nil })
    }

    func testFrameCameraCombinesExifAndDeviceStateWithoutInventingValues() throws {
        XCTAssertNil(DebugRecordingFrameCamera.make(exif: nil, device: nil, now: 10))
        let exif: [String: Any] = [kCGImagePropertyExifISOSpeedRatings as String: [320],
                                   kCGImagePropertyExifExposureTime as String: 0.008333,
                                   kCGImagePropertyExifExposureBiasValue as String: -0.5,
                                   kCGImagePropertyExifBrightnessValue as String: 2.25,
                                   kCGImagePropertyExifFNumber as String: 1.78]
        let exifOnly = try XCTUnwrap(DebugRecordingFrameCamera.make(exif: exif, device: nil, now: 10))
        XCTAssertEqual(exifOnly.source, "exif")
        XCTAssertEqual(exifOnly.iso, 320)
        XCTAssertEqual(exifOnly.exposureDurationSeconds, 0.008333)
        XCTAssertEqual(exifOnly.exposureBiasEV, -0.5)
        XCTAssertEqual(exifOnly.brightnessValue, 2.25)
        XCTAssertNil(exifOnly.whiteBalanceGains)
        XCTAssertNil(exifOnly.deviceSampleAgeSeconds)
        let device = DebugCameraDeviceState(iso: 400, exposureDurationSeconds: 0.01, exposureTargetBias: 0,
                                            exposureTargetOffset: -0.25, whiteBalanceGains: [1.9, 1, 2.1],
                                            sampledAt: 9.75)
        let both = try XCTUnwrap(DebugRecordingFrameCamera.make(exif: exif, device: device, now: 10))
        XCTAssertEqual(both.source, "exif+device")
        XCTAssertEqual(both.iso, 320, "the frame's own Exif wins over the device snapshot")
        XCTAssertEqual(both.exposureTargetOffset, -0.25)
        XCTAssertEqual(both.whiteBalanceGains, [1.9, 1, 2.1])
        XCTAssertEqual(both.deviceSampleAgeSeconds, 0.25)
        let deviceOnly = try XCTUnwrap(DebugRecordingFrameCamera.make(exif: [:], device: device, now: 10))
        XCTAssertEqual(deviceOnly.source, "device")
        XCTAssertEqual(deviceOnly.iso, 400)
        XCTAssertEqual(deviceOnly.exposureDurationSeconds, 0.01)
    }

    func testRecordedFramesCarryCameraStateAndEmitterEvidence() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberFrameCameraTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let processor = FrameProcessor(expiryScheduler: nil, rawFrameDirectory: { directory })
        func frame() -> CMSampleBuffer {
            sampleBuffer(width: 192, height: 96) { base, stride in
                let pixels = base.assumingMemoryBound(to: UInt8.self)
                for y in 30...38 { for x in 10...90 {
                    let index = y * stride + x * 4
                    pixels[index] = 35; pixels[index + 1] = 45; pixels[index + 2] = 245; pixels[index + 3] = 255
                } }
            }
        }
        processor.process(frame())  // establishes the dimensions; not recorded
        let _: String = try await withCheckedThrowingContinuation { continuation in
            processor.startDebugRecording(diagnosticColors: .both) { continuation.resume(with: $0) }
        }
        processor.updateDebugCameraDeviceState(DebugCameraDeviceState(
            iso: 250, exposureDurationSeconds: 1.0 / 120, exposureTargetBias: 0.5,
            exposureTargetOffset: 0.125, whiteBalanceGains: [2, 1, 1.5], sampledAt: HostMonotonicClock.now()))
        for _ in 0..<3 {
            processor.process(frame())
            try await Task.sleep(for: .milliseconds(40))
        }
        let result: DebugRecordingResult = try await withCheckedThrowingContinuation { continuation in
            processor.stopDebugRecording { continuation.resume(with: $0) }
        }
        let data = try Data(contentsOf: result.metadataURL)
        let object = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let frames = try XCTUnwrap(object["frames"] as? [[String: Any]])
        XCTAssertFalse(frames.isEmpty)
        for recorded in frames {
            let camera = try XCTUnwrap(recorded["camera"] as? [String: Any])
            XCTAssertEqual(camera["source"] as? String, "device")
            XCTAssertEqual(camera["iso"] as? Double, 250)
            XCTAssertEqual(camera["exposureTargetBias"] as? Double, 0.5)
            XCTAssertEqual(camera["exposureTargetOffset"] as? Double, 0.125)
            XCTAssertEqual(camera["whiteBalanceGains"] as? [Double], [2, 1, 1.5])
            XCTAssertGreaterThanOrEqual(camera["deviceSampleAgeSeconds"] as? Double ?? -1, 0)
            // The recorded red winner carries its emitter evidence and shadow verdict.
            let red = (recorded["candidateDiagnostics"] as? [String: Any])?["red"] as? [String: Any]
            let selected = try XCTUnwrap(red?["selectedCandidate"] as? [String: Any])
            let emitter = try XCTUnwrap(selected["emitterDiagnostics"] as? [String: Any])
            XCTAssertNotNil(emitter["emitterScoreMargin"] as? Double)
            XCTAssertNotNil(emitter["bladeLengthSupport"] as? Double)
            // The streamed file keeps the compact subset; the triage snapshot has the rest.
            XCTAssertNil(emitter["emitterScoreThreshold"])
            XCTAssertNil(emitter["sampleCount"])
            let shadow = try XCTUnwrap(emitter["shadowR7e"] as? [String: Any])
            XCTAssertEqual(shadow["applied"] as? Bool, false)
            XCTAssertNotNil(shadow["shadowR7eEligible"] as? Bool)
            XCTAssertNil(shadow["thickBodyMargin"])
            let pf22 = try XCTUnwrap(emitter["shadowPF22"] as? [String: Any])
            XCTAssertEqual(pf22["applied"] as? Bool, false)
            XCTAssertNotNil(pf22["shadowPF22Eligible"] as? Bool)
            XCTAssertNil(pf22["purityMargin"])
        }
        let decoded = try JSONDecoder().decode(DebugRecordingMetadata.self, from: data)
        XCTAssertEqual(decoded.frames.first?.camera?.iso, 250)
    }

    // MARK: Segment markers (operator ground truth; metadata only)

    func testSegmentLedgerRecordsMarkersPerLabelCountsAndFalsePositives() throws {
        var ledger = DebugSegmentLedger()
        func observe(_ id: UInt64, _ label: DebugSegmentLabel, red: Bool = false, blue: Bool = false,
                     predictedRed: Bool = false) {
            ledger.observe(frameID: id, timestamp: Double(id) / 30, label: label,
                           detected: ["red": red || predictedRed, "blue": blue],
                           measured: ["red": red, "blue": blue])
        }
        observe(1, .unlabeled, red: true)
        observe(2, .sabersVisible, red: true, blue: true)
        observe(3, .sabersVisible, blue: true)
        observe(4, .noSaber, red: true)
        observe(5, .noSaber, predictedRed: true)
        observe(6, .noSaber)
        observe(7, .noSaberCovered, blue: true)
        observe(8, .noSaberCovered)
        observe(9, .noSaber)
        XCTAssertEqual(ledger.markers.map(\.frameID), [2, 4, 7, 9])
        XCTAssertEqual(ledger.markers.map(\.label), [.sabersVisible, .noSaber, .noSaberCovered, .noSaber])
        XCTAssertEqual(ledger.markers.first?.timestamp ?? 0, 2.0 / 30, accuracy: 1e-12)
        let summary = ledger.summary
        XCTAssertEqual(summary["totalFrames"] as? Int, 9)
        let byLabel = try XCTUnwrap(summary["byLabel"] as? [String: [String: Any]])
        XCTAssertEqual(Set(byLabel.keys), Set(DebugSegmentLabel.allCases.map(\.rawValue)))
        XCTAssertEqual(byLabel["unlabeled"]?["frames"] as? Int, 1)
        XCTAssertEqual(byLabel["sabersVisible"]?["frames"] as? Int, 2)
        XCTAssertEqual(byLabel["noSaber"]?["frames"] as? Int, 4)
        XCTAssertEqual(byLabel["noSaberCovered"]?["frames"] as? Int, 2)
        XCTAssertEqual((byLabel["noSaber"]?["red"] as? [String: Int])?["detectedFrames"], 2)
        XCTAssertEqual((byLabel["noSaber"]?["red"] as? [String: Int])?["measuredFrames"], 1)
        XCTAssertEqual((byLabel["sabersVisible"]?["blue"] as? [String: Int])?["detectedFrames"], 2)
        let falsePositives = try XCTUnwrap(summary["falsePositiveFrames"] as? [String: [String: Int]])
        XCTAssertEqual(falsePositives["noSaber"], ["red": 2, "blue": 0])
        XCTAssertEqual(falsePositives["noSaberCovered"], ["red": 0, "blue": 1])
        XCTAssertNil(falsePositives["sabersVisible"])
        XCTAssertEqual(summary["markerCount"] as? Int, 4)
        XCTAssertEqual(summary["droppedMarkerCount"] as? Int, 0)
        XCTAssertNoThrow(try JSONSerialization.data(withJSONObject: summary))
        XCTAssertNoThrow(try JSONSerialization.data(withJSONObject: ledger.markerEntries))

        // Markers are bounded; frames keep being counted under the newest label.
        var flapping = DebugSegmentLedger()
        for id in 0..<(DebugSegmentLedger.maximumMarkers + 10) {
            flapping.observe(frameID: UInt64(id), timestamp: Double(id),
                             label: id % 2 == 0 ? .noSaber : .sabersVisible,
                             detected: [:], measured: [:])
        }
        XCTAssertEqual(flapping.markers.count, DebugSegmentLedger.maximumMarkers)
        XCTAssertEqual(flapping.droppedMarkerCount, 10)
        XCTAssertEqual(flapping.frameCount(.noSaber) + flapping.frameCount(.sabersVisible),
                       DebugSegmentLedger.maximumMarkers + 10)
    }

    func testUnlabeledRecordingHasNoMarkersAndCountsEveryFrameAsUnlabeled() async throws {
        let blue = bridgeSaber(.blue)
        let (_, metadata) = try await recordBridge([(301, 0, [blue]), (302, 1, [blue]), (303, 2, [])])
        XCTAssertEqual((metadata["segmentMarkers"] as? [Any])?.count, 0)
        let summary = try XCTUnwrap(metadata["segmentSummary"] as? [String: Any])
        let frames = try XCTUnwrap(metadata["frames"] as? [[String: Any]])
        XCTAssertEqual(summary["totalFrames"] as? Int, frames.count)
        let byLabel = try XCTUnwrap(summary["byLabel"] as? [String: [String: Any]])
        XCTAssertEqual(byLabel["unlabeled"]?["frames"] as? Int, frames.count)
        XCTAssertEqual(byLabel["noSaber"]?["frames"] as? Int, 0)
        // The streamed per-frame shape is unchanged: labels live only in root markers.
        XCTAssertTrue(frames.allSatisfy { $0["segmentLabel"] == nil })
    }

    func testSegmentMarkersReachMetadataSummaryAndCompactContexts() async throws {
        let blue = bridgeSaber(.blue)
        let red = bridgeSaber(.red)
        let labels: [UInt64: DebugSegmentLabel] = [
            402: .sabersVisible, 403: .sabersVisible, 404: .noSaber, 405: .noSaber,
            406: .noSaberCovered, 407: .noSaberCovered]
        let (recording, metadata) = try await recordBridge([
            (401, 0, [blue]), (402, 1, []), (403, 2, [blue]), (404, 3, [blue]), (405, 4, [blue]),
            (406, 5, []), (407, 6, [red])], labels: labels)
        let frames = try XCTUnwrap(metadata["frames"] as? [[String: Any]])
        XCTAssertEqual(frames.compactMap { $0["frameID"] as? Int }, [401, 402, 403, 404, 405, 406, 407])
        let markers = try XCTUnwrap(metadata["segmentMarkers"] as? [[String: Any]])
        XCTAssertEqual(markers.compactMap { $0["frameID"] as? Int }, [402, 404, 406])
        XCTAssertEqual(markers.compactMap { $0["label"] as? String },
                       ["sabersVisible", "noSaber", "noSaberCovered"])
        for marker in markers {
            let frame = try XCTUnwrap(frames.first { $0["frameID"] as? Int == marker["frameID"] as? Int })
            XCTAssertEqual(marker["timestamp"] as? Double, frame["presentationTimeSeconds"] as? Double)
        }
        let summary = try XCTUnwrap(metadata["segmentSummary"] as? [String: Any])
        let byLabel = try XCTUnwrap(summary["byLabel"] as? [String: [String: Any]])
        XCTAssertEqual(DebugSegmentLabel.allCases.map { byLabel[$0.rawValue]?["frames"] as? Int },
                       [1, 2, 2, 2])
        let falsePositives = try XCTUnwrap(summary["falsePositiveFrames"] as? [String: [String: Int]])
        XCTAssertEqual(falsePositives["noSaber"], ["red": 0, "blue": 2])
        XCTAssertEqual(falsePositives["noSaberCovered"], ["red": 1, "blue": 0])

        // summary.json carries the whole-session counts plus the markers.
        let bundle = try XCTUnwrap(recording.triageBundleURL)
        let triageSummary = try XCTUnwrap(JSONSerialization.jsonObject(with:
            Data(contentsOf: bundle.appendingPathComponent("summary.json"))) as? [String: Any])
        let segments = try XCTUnwrap(triageSummary["segmentSummary"] as? [String: Any])
        XCTAssertEqual(segments["totalFrames"] as? Int, 7)
        XCTAssertEqual((segments["markers"] as? [[String: Any]])?.compactMap { $0["frameID"] as? Int },
                       [402, 404, 406])
        // Each compact context states the label of its selected frame and stays small.
        let contexts = try FileManager.default.contentsOfDirectory(
            at: bundle.appendingPathComponent("frames"), includingPropertiesForKeys: nil)
        XCTAssertFalse(contexts.isEmpty)
        for url in contexts {
            let data = try Data(contentsOf: url)
            XCTAssertLessThan(data.count, 32 * 1_024)
            let context = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
            let id = try XCTUnwrap(context["selectedFrameID"] as? Int)
            XCTAssertEqual(context["segmentLabel"] as? String,
                           (labels[UInt64(id)] ?? .unlabeled).rawValue, "frame \(id)")
        }
    }

    func testSegmentLabelBuilderLookupUsesNewestMarkerAndIsAbsentForOldBundles() {
        let markers: [[String: Any]] = [["frameID": 10, "timestamp": 0.3, "label": "noSaber"],
                                        ["frameID": 20, "timestamp": 0.6, "label": "sabersVisible"]]
        let metadata: [String: Any] = ["segmentMarkers": markers]
        XCTAssertEqual(DebugRecordingTriageBuilder.segmentLabel(metadata, frameID: 9), "unlabeled")
        XCTAssertEqual(DebugRecordingTriageBuilder.segmentLabel(metadata, frameID: 10), "noSaber")
        XCTAssertEqual(DebugRecordingTriageBuilder.segmentLabel(metadata, frameID: 19), "noSaber")
        XCTAssertEqual(DebugRecordingTriageBuilder.segmentLabel(metadata, frameID: 25), "sabersVisible")
        XCTAssertNil(DebugRecordingTriageBuilder.segmentLabel([:], frameID: 10))
    }

    func testSettingTheSegmentLabelNeverWaitsForTheProcessingQueue() {
        let processor = FrameProcessor(expiryScheduler: nil)
        let queueBlocked = expectation(description: "processor queue blocked")
        let releaseQueue = DispatchSemaphore(value: 0)
        processor.queue.async {
            queueBlocked.fulfill()
            releaseQueue.wait()
        }
        wait(for: [queueBlocked], timeout: 1)
        let started = Date()
        for label in DebugSegmentLabel.allCases + [.noSaber] { processor.setDebugSegmentLabel(label) }
        XCTAssertLessThan(Date().timeIntervalSince(started), 0.5)
        XCTAssertEqual(processor.debugSegmentLabelForTesting, .noSaber)
        releaseQueue.signal()
        processor.queue.sync {}
    }

    func testSegmentLabelsLeaveRecognitionAndOutputUnchanged() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberSegmentTests-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        func frame(_ offset: Int) -> CMSampleBuffer {
            sampleBuffer(width: 192, height: 96) { base, stride in
                let pixels = base.assumingMemoryBound(to: UInt8.self)
                for y in 30...38 { for x in (10 + offset)...(90 + offset) {
                    let index = y * stride + x * 4
                    pixels[index] = 35; pixels[index + 1] = 45; pixels[index + 2] = 245; pixels[index + 3] = 255
                } }
            }
        }
        final class Outputs { var values: [String] = [] }
        func describe(_ results: [DetectedSaber]) -> String {
            results.map { saber -> String in
                let (first, second) = saber.endpoints
                return "\(saber.color) \(first.x),\(first.y)-\(second.x),\(second.y) "
                    + "fresh=\(saber.isFresh) predicted=\(saber.isPredicted)"
            }.sorted().joined(separator: "|")
        }
        // The same frames three ways: not recording, recording unlabeled, recording with label changes.
        func run(record: Bool, label: Bool) async throws -> (outputs: [String], metadata: [String: Any]?) {
            let processor = FrameProcessor(expiryScheduler: nil, rawFrameDirectory: { directory })
            let outputs = Outputs()
            processor.onResult = { results, _, _, _, _, _ in outputs.values.append(describe(results)) }
            processor.process(frame(0))
            if record {
                let _: String = try await withCheckedThrowingContinuation { continuation in
                    processor.startDebugRecording(diagnosticColors: .both) { continuation.resume(with: $0) }
                }
            }
            for index in 1...6 {
                if label && index == 3 { processor.setDebugSegmentLabel(.noSaber) }
                if label && index == 5 { processor.setDebugSegmentLabel(.noSaberCovered) }
                processor.process(frame(index * 3))
                try await Task.sleep(for: .milliseconds(40))
            }
            guard record else { return (outputs.values, nil) }
            let result: DebugRecordingResult = try await withCheckedThrowingContinuation { continuation in
                processor.stopDebugRecording { continuation.resume(with: $0) }
            }
            let object = try JSONSerialization.jsonObject(with: Data(contentsOf: result.metadataURL))
            return (outputs.values, object as? [String: Any])
        }
        let off = try await run(record: false, label: false)
        let unlabeled = try await run(record: true, label: false)
        let labeled = try await run(record: true, label: true)
        XCTAssertEqual(off.outputs.count, 7)
        XCTAssertTrue(off.outputs.allSatisfy { $0.contains("red") && $0.contains("fresh=true") })
        XCTAssertEqual(unlabeled.outputs, off.outputs)
        XCTAssertEqual(labeled.outputs, off.outputs)

        let metadata = try XCTUnwrap(labeled.metadata)
        let frames = try XCTUnwrap(metadata["frames"] as? [[String: Any]])
        XCTAssertFalse(frames.isEmpty)
        // Frame IDs 2...7 are recorded; the labels apply from frames 4 and 6 on.
        func expected(_ id: Int) -> DebugSegmentLabel {
            id >= 6 ? .noSaberCovered : (id >= 4 ? .noSaber : .unlabeled)
        }
        let ids = frames.compactMap { $0["frameID"] as? Int }
        var expectedMarkers: [Int] = []
        var previous = DebugSegmentLabel.unlabeled
        for id in ids where expected(id) != previous {
            expectedMarkers.append(id)
            previous = expected(id)
        }
        let markers = try XCTUnwrap(metadata["segmentMarkers"] as? [[String: Any]])
        XCTAssertEqual(markers.compactMap { $0["frameID"] as? Int }, expectedMarkers)
        let segmentSummary = try XCTUnwrap(metadata["segmentSummary"] as? [String: Any])
        let byLabel = try XCTUnwrap(segmentSummary["byLabel"] as? [String: [String: Any]])
        for label in DebugSegmentLabel.allCases {
            XCTAssertEqual(byLabel[label.rawValue]?["frames"] as? Int,
                           ids.filter { expected($0) == label }.count, label.rawValue)
        }
        let falsePositives = try XCTUnwrap(segmentSummary["falsePositiveFrames"] as? [String: [String: Int]])
        XCTAssertEqual(falsePositives["noSaber"]?["red"], ids.filter { expected($0) == .noSaber }.count)
        XCTAssertEqual((unlabeled.metadata?["segmentMarkers"] as? [Any])?.count, 0)
    }
}

extension DetectionCoreTests {
    func testDebugRecordingRecordsCameraExposureExperimentOnlyWhenProvided() async throws {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberExposureExperiment-\(UUID().uuidString)", isDirectory: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let oldPreference = UserDefaults.standard.object(forKey: DebugBundleTransfer.preferenceKey)
        UserDefaults.standard.set(false, forKey: DebugBundleTransfer.preferenceKey)
        defer { UserDefaults.standard.set(oldPreference, forKey: DebugBundleTransfer.preferenceKey) }
        let state = CameraExposureExperimentState(
            setting: .maxShutter1_100, status: .applied, requestedMaxExposureSeconds: 0.01,
            appliedMaxExposureSeconds: 0.01, defaultMaxExposureSeconds: 1.0 / 30,
            observedMaxExposureSeconds: 0.01)
        func record(_ experiment: CameraExposureExperimentState?, date: Date) async throws -> [String: Any] {
            let recorder = try DebugVideoRecorder(directory: directory, date: date,
                                                  cameraExposureExperiment: experiment)
            let buffer = solidPixelBuffer(width: 64, height: 48)
            CVPixelBufferLockBaseAddress(buffer, .readOnly)
            _ = recorder.append(pixelBuffer: buffer, presentationTime: CMTime(value: 1, timescale: 30),
                                frameID: 1, results: [])
            CVPixelBufferUnlockBaseAddress(buffer, .readOnly)
            let recording: DebugRecordingResult = try await withCheckedThrowingContinuation { continuation in
                recorder.finish { continuation.resume(with: $0) }
            }
            let data = try Data(contentsOf: recording.metadataURL)
            // Existing Codable readers ignore the additive root field.
            XCTAssertNoThrow(try JSONDecoder().decode(DebugRecordingMetadata.self, from: data))
            return try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        }
        let withExperiment = try await record(state, date: Date(timeIntervalSince1970: 10))
        let recorded = try XCTUnwrap(withExperiment["cameraExposureExperiment"] as? [String: Any])
        XCTAssertEqual(recorded["setting"] as? String, "maxShutter1_100")
        XCTAssertEqual(recorded["status"] as? String, "applied")
        XCTAssertEqual(recorded["capActive"] as? Bool, true)
        XCTAssertEqual(try XCTUnwrap(recorded["appliedMaxExposureSeconds"] as? Double), 0.01, accuracy: 1e-12)
        let withoutExperiment = try await record(nil, date: Date(timeIntervalSince1970: 20))
        XCTAssertNil(withoutExperiment["cameraExposureExperiment"])
    }
}
