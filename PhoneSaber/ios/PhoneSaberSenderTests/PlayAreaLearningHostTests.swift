import XCTest
import CoreMedia
import CoreVideo

@MainActor final class PlayAreaLearningHostTests: XCTestCase {
    private let previous = SaberPlayArea(minX: 0, minY: 0, maxX: 0.15, maxY: 1)

    private func command(_ id: Int = 1) -> LANLivenessProbe.PCCommand {
        .init(id: id, ignore: nil, clearIgnore: false, learnAreaSeconds: 10, sourceHost: "192.0.2.1")
    }

    private func begin(_ model: CameraViewModel, id: Int = 1) async {
        let pending = LearnSleeper.pending.count
        model.applyPCCommand(command(id))
        while LearnSleeper.pending.count == pending { await Task.yield() }
    }

    private func seed(_ model: CameraViewModel) {
        model.setPlayArea(.init(minX: previous.minX, minY: previous.minY, maxX: previous.maxX, maxY: previous.maxY))
        model.savedAreas.removeAll()
    }

    // 実BGRA認識のwinnerを実learnerへ採取する。学習成功の代役は使わない。
    private func frame(x: Int) -> CMSampleBuffer {
        var image: CVPixelBuffer?
        CVPixelBufferCreate(kCFAllocatorDefault, 200, 120, kCVPixelFormatType_32BGRA, nil, &image)
        let buffer = image!
        CVPixelBufferLockBaseAddress(buffer, [])
        let stride = CVPixelBufferGetBytesPerRow(buffer)
        let pixels = CVPixelBufferGetBaseAddress(buffer)!.assumingMemoryBound(to: UInt8.self)
        for y in 0..<120 { for dx in 0..<200 {
            let offset = y * stride + dx * 4
            pixels[offset] = 0; pixels[offset + 1] = 0; pixels[offset + 2] = 0; pixels[offset + 3] = 255
        } }
        for y in 30...90 { for dx in -4...4 {
            let offset = y * stride + (x + dx) * 4
            pixels[offset] = 30; pixels[offset + 1] = 45; pixels[offset + 2] = 245
        } }
        CVPixelBufferUnlockBaseAddress(buffer, [])
        var format: CMVideoFormatDescription?
        CMVideoFormatDescriptionCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: buffer, formatDescriptionOut: &format)
        var sample: CMSampleBuffer?
        var timing = CMSampleTimingInfo(duration: .invalid, presentationTimeStamp: .zero, decodeTimeStamp: .invalid)
        CMSampleBufferCreateReadyWithImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: buffer,
            formatDescription: format!, sampleTiming: &timing, sampleBufferOut: &sample)
        return sample!
    }

    @discardableResult private func collect(_ processor: FrameProcessor, x: Int, count: Int = 30) -> SaberPlayAreaLearner {
        var expected = SaberPlayAreaLearner()
        processor.onResult = { results, width, height, _, _, _ in
            for result in results where result.isFresh { expected.add(result.endpoints, width: width, height: height) }
        }
        for i in 0..<count {
            let sample = frame(x: x + i % 5)
            processor.queue.sync { processor.process(sample) }
        }
        processor.onResult = nil
        XCTAssertEqual(expected.samples, count)
        return expected
    }

    func testResetAfterEnoughSamplesPreservesSavedAreaAndFailsOnce() async {
        for station in ["A", "B"] {
            let model = CameraViewModel(); model.station = station; seed(model)
            await begin(model)
            collect(model.processor, x: 45)
            model.running = false
            model.resetForTesting(); model.resetForTesting()
            XCTAssertFalse(model.playAreaLearning)
            XCTAssertEqual(model.playArea, previous)
            XCTAssertTrue(model.savedAreas.isEmpty)
            let failed = "PHONESABER_ACK 1 id=1 station=\(station) platform=ios result=failed detail=samples0"
            XCTAssertEqual(model.lanProbe.acks, ["PHONESABER_ACK 1 id=1 station=\(station) platform=ios result=learning detail=10s", failed])
            await LearnSleeper.finish()
            XCTAssertEqual(model.lanProbe.acks.last, failed)
            XCTAssertEqual(model.lanProbe.acks.count, 2, "中断通知は一度だけ")
            XCTAssertTrue(model.savedAreas.isEmpty)
            model.applyPCCommand(command())
            XCTAssertEqual(model.lanProbe.acks.last, failed, "同じIDは確定済み失敗ACKを再送")
            XCTAssertFalse(model.playAreaLearning)
        }
    }

    func testOldTimerBeforeAndAfterNewCompletionCannotChangeNewLearning() async throws {
        for station in ["A", "B"] { for oldFirst in [true, false] {
            let model = CameraViewModel(); model.station = station; seed(model)
            await begin(model)
            let oldCompletion = model.pendingCompletionForTesting()
            collect(model.processor, x: 45)
            model.resetForTesting()
            model.applyPCCommand(command())
            XCTAssertFalse(model.playAreaLearning, "失敗したIDは再学習しない")
            await begin(model, id: 2)
            let expected = collect(model.processor, x: 155)
            oldCompletion()
            XCTAssertTrue(model.playAreaLearning, "旧コールバックは新学習を完了させない")
            XCTAssertTrue(model.savedAreas.isEmpty)
            if oldFirst {
                await LearnSleeper.finish()
                XCTAssertTrue(model.playAreaLearning)
                XCTAssertTrue(model.savedAreas.isEmpty)
                XCTAssertTrue(model.lanProbe.acks.last!.contains("id=2") && model.lanProbe.acks.last!.contains("result=learning"))
            }
            await LearnSleeper.finish(oldFirst ? 0 : 1)
            XCTAssertEqual(model.playArea, expected.area(), "旧採取値を含まない新しい範囲だけ保存")
            XCTAssertEqual(model.savedAreas.count, 1)
            XCTAssertFalse(model.playAreaLearning)
            let completed = try XCTUnwrap(model.lanProbe.acks.last)
            let raw = LANLivenessProbe.PCCommand.Area(minX: expected.minX * 200 / 199, minY: expected.minY * 120 / 119,
                                                     maxX: expected.maxX * 200 / 199, maxY: expected.maxY * 120 / 119)
            XCTAssertEqual(completed, "PHONESABER_ACK 1 id=2 station=\(station) platform=ios result=learned detail="
                           + raw.learnedDetail(samples: 30, mirrorX: false, mirrorY: false), "PC位置補正も新採取値だけ")
            if !oldFirst { await LearnSleeper.finish() }
            let acknowledgements = model.lanProbe.acks
            oldCompletion()
            XCTAssertEqual(model.lanProbe.acks, acknowledgements)
            XCTAssertEqual(model.lanProbe.acks.last, completed)
            XCTAssertEqual(model.savedAreas.count, 1)
            model.applyPCCommand(command(2))
            XCTAssertEqual(model.lanProbe.acks.last, completed, "通常の学習完了ACK再送も維持")
        } }
    }

    func testProcessorResetDiscardsLearnerAndPositionBounds() {
        let processor = FrameProcessor(expiryScheduler: nil)
        processor.startPlayAreaLearning()
        collect(processor, x: 45)
        processor.reset()
        var bounds: SaberPlayArea?
        XCTAssertNil(processor.finishPlayAreaLearning(reportBounds: { bounds = $0 }))
        XCTAssertNil(bounds)
        processor.startPlayAreaLearning()
        let expected = collect(processor, x: 155)
        XCTAssertEqual(processor.finishPlayAreaLearning(reportBounds: { bounds = $0 }), expected)
        XCTAssertGreaterThan(bounds!.minX, 0.7)
    }

    func testInFlightOldFrameCannotContributeToReplacementLearning() {
        let processor = FrameProcessor(expiryScheduler: nil)
        processor.startPlayAreaLearning()
        let reached = DispatchSemaphore(value: 0), release = DispatchSemaphore(value: 0)
        LearningFrameBarrier.action = { reached.signal(); _ = release.wait(timeout: .now() + 5) }
        let oldFrame = frame(x: 45)
        processor.queue.async(execute: DispatchWorkItem { processor.process(oldFrame) })
        XCTAssertEqual(reached.wait(timeout: .now() + 5), .success)
        _ = processor.finishPlayAreaLearning()
        processor.startPlayAreaLearning()
        release.signal()
        processor.queue.sync {}
        LearningFrameBarrier.action = nil
        let expected = collect(processor, x: 155)
        var bounds: SaberPlayArea?
        XCTAssertEqual(processor.finishPlayAreaLearning(reportBounds: { bounds = $0 }), expected)
        XCTAssertGreaterThan(bounds!.minX, 0.7, "旧フレームの位置補正boundsも混ぜない")
    }

    func testRelearningCancelsPreviousCompletionOnce() async {
        let model = CameraViewModel(); seed(model)
        await begin(model)
        collect(model.processor, x: 45)
        await begin(model, id: 2)
        XCTAssertEqual(model.lanProbe.acks.filter { $0.contains("id=1") && $0.contains("result=failed detail=samples0") }.count, 1)
        await LearnSleeper.finish()
        XCTAssertTrue(model.playAreaLearning)
        XCTAssertTrue(model.savedAreas.isEmpty)
        let expected = collect(model.processor, x: 155)
        await LearnSleeper.finish()
        XCTAssertEqual(model.playArea, expected.area())
    }

    func testClearCancelsAndOldTimerCannotRestoreArea() async {
        let model = CameraViewModel(); seed(model)
        await begin(model)
        collect(model.processor, x: 45)
        model.clearPlayArea(); model.clearPlayArea()
        let acks = model.lanProbe.acks
        XCTAssertTrue(acks.last!.contains("result=failed detail=samples0"))
        await LearnSleeper.finish()
        XCTAssertNil(model.playArea)
        XCTAssertFalse(model.playAreaLearning)
        XCTAssertEqual(model.lanProbe.acks, acks)
        XCTAssertEqual(model.savedAreas.count, 2, "明示clearだけを保存")
        XCTAssertNil(model.processor.finishPlayAreaLearning())
    }

    func testExplicitAreaCancelsAndOldTimerCannotOverwriteMirroredArea() async {
        let model = CameraViewModel(); seed(model)
        await begin(model)
        collect(model.processor, x: 45)
        model.mirrorX = true; model.mirrorY = true
        model.setPlayArea(.init(minX: 0.1, minY: 0.2, maxX: 0.6, maxY: 0.8))
        let area = model.playArea, acks = model.lanProbe.acks
        XCTAssertEqual(area?.minX, 0.4)
        XCTAssertEqual(area?.maxY, 0.8)
        await LearnSleeper.finish()
        XCTAssertEqual(model.playArea, area)
        XCTAssertEqual(model.savedAreas.count, 1)
        XCTAssertEqual(model.lanProbe.acks, acks)
        XCTAssertTrue(acks.last!.contains("result=failed detail=samples0"))
        XCTAssertFalse(model.playAreaLearning)
        XCTAssertNil(model.processor.finishPlayAreaLearning())
    }

    func testInsufficientSamplesAndStoppedRequestKeepPreviousArea() async {
        let model = CameraViewModel(); seed(model)
        await begin(model)
        collect(model.processor, x: 155, count: 29)
        await LearnSleeper.finish()
        XCTAssertEqual(model.playArea, previous)
        XCTAssertTrue(model.savedAreas.isEmpty)
        XCTAssertFalse(model.playAreaLearning)
        XCTAssertTrue(model.lanProbe.acks.last!.contains("result=failed detail=samples29"))
        model.running = false
        model.applyPCCommand(command(2))
        XCTAssertTrue(model.lanProbe.acks.last!.contains("id=2") && model.lanProbe.acks.last!.contains("result=failed detail=samples0"))
        XCTAssertTrue(LearnSleeper.pending.isEmpty)
    }
}
