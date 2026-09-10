import XCTest
@testable import PhoneSaberSender

private final class CompletionBox {
    private var values: [(Int, (Result<TimeInterval, Error>) -> Void)] = []
    private let lock = NSLock()

    func append(port: Int, completion: @escaping (Result<TimeInterval, Error>) -> Void) {
        lock.lock(); defer { lock.unlock() }
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

@MainActor
private func waitUntil(_ condition: @escaping @MainActor () -> Bool, timeout: TimeInterval = 1.0) async -> Bool {
    let deadline = ContinuousClock.now + .seconds(timeout)
    while !condition() && ContinuousClock.now < deadline {
        try? await Task.sleep(for: .milliseconds(2))
    }
    return condition()
}

final class DetectionCoreTests: XCTestCase {
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
        processor.onResult = { detected, _, _, _, _ in results.append(detected) }
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
        let blueFailed = await waitUntil { viewModel.status == "接続エラー" }; XCTAssertTrue(blueFailed)
        XCTAssertEqual(viewModel.status, "接続エラー")
        sender.setStateForTesting(port: 5006, state: "ready", error: nil)
        let ready = await waitUntil { viewModel.status == "送信中" }; XCTAssertTrue(ready)
        XCTAssertEqual(viewModel.status, "送信中")
        let endpoints = (PixelPoint(x: 2, y: 3), PixelPoint(x: 12, y: 13))
        viewModel.processDetectedForTesting([(.red, endpoints), (.blue, endpoints)], at: clock.now, dimensions: (20, 20))
        let completedOrFailed = await waitUntil { viewModel.redErrorCount == 1 && viewModel.blueErrorCount == 1 }; XCTAssertTrue(completedOrFailed)
        XCTAssertEqual(viewModel.redAttemptCount, 1)
        XCTAssertEqual(viewModel.blueAttemptCount, 1)
        XCTAssertEqual(viewModel.redErrorCount, 1)
        XCTAssertEqual(viewModel.blueErrorCount, 1)
        XCTAssertEqual(viewModel.status, "送信エラー")

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
        processor.onResult = { detected, _, _, _, _ in results.append(detected) }
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
        XCTAssertEqual(viewModel.redCompletedCount, 0)
        newCompletion?.1(.success(302))
        let completionAccepted = await waitUntil { viewModel.redCompletedCount == 1 }; XCTAssertTrue(completionAccepted)
        XCTAssertEqual(viewModel.redAttemptCount, 1)
        XCTAssertEqual(viewModel.redCompletedCount, 1)
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
        let currentStates = viewModel.senderStates
        let currentErrors = viewModel.senderErrors
        let oldFrameCallback = viewModel.processor.onResult
        let oldFrameGeneration = viewModel.processor.currentGeneration
        viewModel.stop()
        viewModel.startForTesting()
        for _ in 0..<100 {
            if viewModel.senderStates == currentStates { break }
            try? await Task.sleep(nanoseconds: 5_000_000)
        }
        oldFrameCallback?([DetectedSaber(endpoints: (PixelPoint(x: 1, y: 1), PixelPoint(x: 2, y: 2)), color: .red, isFresh: true)], 20, 20, 301, oldFrameGeneration)
        sender.sendStaleUpdateForTesting(index: 0, states: [5005: "failed"], errors: [5005: "旧接続通知"])
        let staleCallbacksDrained = await waitUntil { viewModel.senderStates == currentStates && viewModel.senderErrors == currentErrors }
        XCTAssertTrue(staleCallbacksDrained)
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
        processor.onResult = { detected, _, _, _, _ in
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
        XCTAssertEqual(results.last?.count, 0)
    }

    func testExpiryUsesIndependentColorDeadlines() {
        let clock = ManualClock(200)
        let processor = FrameProcessor(clock: { clock.now }, expiryScheduler: nil)
        var results: [[DetectedSaber]] = []
        processor.onResult = { detected, _, _, _, _ in results.append(detected) }
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
}
