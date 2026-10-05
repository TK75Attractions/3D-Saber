import CoreMedia
import CoreVideo
import Foundation
import XCTest
@testable import PhoneSaberSender

final class GuidedRecordingTests: XCTestCase {
    /// Two short steps: A (lead-in 5 s, hold 4 s, noSaber) and B (lead-in 4 s,
    /// hold 5 s, sabersVisible, 3 lossless captures). Total 18 s.
    private let tinyScript = GuidedRecordingScript(
        id: "test_script", version: 7,
        steps: [
            GuidedRecordingStep(id: "a", title: "A", cue: "cue A", leadInSeconds: 5, holdSeconds: 4,
                                label: .noSaber, losslessCaptures: 0),
            GuidedRecordingStep(id: "b", title: "B", cue: "cue B", leadInSeconds: 4, holdSeconds: 5,
                                label: .sabersVisible, losslessCaptures: 3),
        ],
        closingCue: "end")

    /// Drives the scheduler with a manual clock in 0.1 s steps and returns every
    /// action with the (rounded) time at which it was returned.
    private func run(_ scheduler: inout GuidedRecordingScheduler, until end: Double,
                     step: Double = 0.1) -> [(time: Double, action: GuidedRecordingAction)] {
        var result: [(Double, GuidedRecordingAction)] = []
        var tick = 0
        while Double(tick) * step <= end + 1e-9 {
            let now = (Double(tick) * step * 1000).rounded() / 1000
            result += scheduler.advance(to: now).map { (now, $0) }
            tick += 1
        }
        return result
    }

    // MARK: Script

    func testShootingPlanScriptIsValidAndMatchesTheRequestedPlan() {
        let script = GuidedRecordingScript.shootingPlanV3
        XCTAssertEqual(script.validationErrors, [])
        XCTAssertEqual(script.version, 3)
        XCTAssertEqual(Set(script.steps.map(\.id)).count, script.steps.count)
        XCTAssertLessThanOrEqual(script.totalSeconds, DebugRecordingLimits.maximumDurationSeconds - 30)
        XCTAssertEqual(script.plannedLosslessCaptures, DebugRecordingLimits.maximumGuidedLosslessCaptures)
        // saberなし 20 s first, saberなし 10 s last, saberあり in between; no step
        // assumes particular objects in the scene (v1 asked for red labels).
        XCTAssertEqual(script.steps.first?.label, .noSaber)
        XCTAssertEqual(script.steps.first?.holdSeconds, 20)
        XCTAssertEqual(script.steps.last?.label, .noSaber)
        XCTAssertEqual(script.steps.last?.holdSeconds, 10)
        XCTAssertTrue(script.steps.dropFirst().dropLast().allSatisfy { $0.label == .sabersVisible })
        let ids = script.steps.map(\.id)
        for required in ["red_still_0_5m", "red_still_1_5m", "red_still_3m", "red_end_on",
                         "red_slow_swing", "red_fast_swing", "red_cross_swing",
                         "blue_still_1_5m", "blue_swing", "both_swing"] {
            XCTAssertTrue(ids.contains(required), required)
        }
        XCTAssertFalse(script.steps.contains { $0.label == .noSaberCovered })
        XCTAssertFalse(script.steps.contains { $0.cue.contains("ラベル") || $0.cue.contains("カラビナ") || $0.cue.contains("赤い物") })
        // Lossless captures only during swing steps.
        // Lossless frames in swing steps, plus one in the first saberなし hold (what the
        // false positive is), so every guided bundle shows the background.
        XCTAssertTrue(script.steps.filter { $0.losslessCaptures > 0 }.allSatisfy {
            $0.id.hasSuffix("swing") || $0.id == "no_saber" })
        XCTAssertEqual(script.steps.first?.losslessCaptures, 1)
        XCTAssertTrue(script.steps.allSatisfy { !$0.cue.isEmpty && !$0.title.isEmpty })
        // The scheduler really issues every planned capture.
        XCTAssertEqual(GuidedRecordingScheduler(script: script).plannedCaptureTimes.count,
                       script.plannedLosslessCaptures)
    }

    func testValidationRejectsUnsafeScripts() {
        let tooLong = GuidedRecordingScript(id: "x", version: 1, steps: [
            GuidedRecordingStep(id: "a", title: "A", cue: "c", leadInSeconds: 2, holdSeconds: 400,
                                label: .noSaber, losslessCaptures: 9)], closingCue: "")
        let errors = tooLong.validationErrors
        XCTAssertTrue(errors.contains { $0.contains("countdown") })
        XCTAssertTrue(errors.contains { $0.contains("guided limit") })
        XCTAssertTrue(errors.contains { $0.contains("too long") })
    }

    // MARK: Scheduler

    func testSchedulerStepBoundariesLabelsCountdownAndEndStop() {
        var scheduler = GuidedRecordingScheduler(script: tinyScript)
        XCTAssertEqual(scheduler.totalSeconds, 18)
        XCTAssertEqual(scheduler.boundaries.map { [$0.leadIn, $0.hold, $0.end] }, [[0, 5, 9], [9, 13, 18]])
        let actions = run(&scheduler, until: 20)

        XCTAssertEqual(Array(actions.prefix(3)).map(\.action),
                       [.setPhase(.leadIn(0)), .setLabel(.unlabeled), .speak("cue A")])
        XCTAssertTrue(actions.prefix(3).allSatisfy { $0.time == 0 })

        func times(_ action: GuidedRecordingAction) -> [Double] {
            actions.filter { $0.action == action }.map(\.time)
        }
        XCTAssertEqual(times(.countdown(3)), [2, 10])
        XCTAssertEqual(times(.countdown(2)), [3, 11])
        XCTAssertEqual(times(.countdown(1)), [4, 12])
        XCTAssertEqual(times(.countdown(0)), [5, 13])
        XCTAssertEqual(times(.setPhase(.hold(0))), [5])
        XCTAssertEqual(times(.setPhase(.leadIn(1))), [9])
        XCTAssertEqual(times(.setPhase(.hold(1))), [13])
        XCTAssertEqual(times(.speak("cue B")), [9])

        // Labels switch exactly at the boundaries, through unlabeled lead-ins.
        let labels = actions.compactMap { item -> (Double, DebugSegmentLabel)? in
            if case .setLabel(let label) = item.action { return (item.time, label) }
            return nil
        }
        XCTAssertEqual(labels.map(\.1), [.unlabeled, .noSaber, .unlabeled, .sabersVisible])
        XCTAssertEqual(labels.map(\.0), [0, 5, 9, 13])
        // The label is set in the same tick as the phase, phase first.
        let holdIndex = actions.firstIndex { $0.action == .setPhase(.hold(1)) }!
        XCTAssertEqual(actions[holdIndex + 1].action, .setLabel(.sabersVisible))

        // End: phase cleared, closing cue, finish — once, at 18 s.
        XCTAssertEqual(Array(actions.suffix(3)).map(\.action), [.setPhase(nil), .speak("end"), .finish])
        XCTAssertTrue(actions.suffix(3).allSatisfy { $0.time == 18 })
        XCTAssertTrue(scheduler.isFinished)
        XCTAssertEqual(scheduler.advance(to: 30), [])
        XCTAssertNil(scheduler.status(at: 19))
    }

    func testSchedulerLosslessCadenceIsBoundedToSwingHolds() {
        var scheduler = GuidedRecordingScheduler(script: tinyScript)
        XCTAssertEqual(scheduler.plannedCaptureTimes, [14.5, 16, 17.5])
        let actions = run(&scheduler, until: 20)
        let captures = actions.filter { $0.action == .captureLossless(stepIndex: 1) }
        XCTAssertEqual(captures.map(\.time), [14.5, 16, 17.5])
        XCTAssertEqual(actions.filter {
            if case .captureLossless = $0.action { return true }; return false
        }.count, 3)
        XCTAssertEqual(scheduler.requestedCaptureCount, 3)

        // A hold too short for all requests gets only what fits before its end margin,
        // and the whole script never exceeds the guided limit.
        let greedy = GuidedRecordingScript(id: "g", version: 1, steps: [
            GuidedRecordingStep(id: "short", title: "S", cue: "c", leadInSeconds: 4, holdSeconds: 2.5,
                                label: .sabersVisible, losslessCaptures: 5),
            GuidedRecordingStep(id: "long", title: "L", cue: "c", leadInSeconds: 4, holdSeconds: 60,
                                label: .sabersVisible, losslessCaptures: 20),
        ], closingCue: "")
        let planned = GuidedRecordingScheduler(script: greedy).plannedCaptureTimes
        XCTAssertEqual(planned.first, 5.5)
        XCTAssertEqual(planned.count, DebugRecordingLimits.maximumGuidedLosslessCaptures)
        XCTAssertEqual(planned.filter { $0 < 6.5 }.count, 1)
        XCTAssertEqual(zip(planned.dropFirst(2), planned.dropFirst()).map { $0 - $1 }.allSatisfy {
            abs($0 - GuidedRecordingScheduler.captureIntervalSeconds) < 1e-9 }, true)
    }

    func testSchedulerCancelStopsEverything() {
        var scheduler = GuidedRecordingScheduler(script: tinyScript)
        _ = run(&scheduler, until: 6)
        XCTAssertEqual(scheduler.status(at: 6)?.phase, .hold(0))
        scheduler.cancel()
        XCTAssertTrue(scheduler.isCancelled)
        XCTAssertFalse(scheduler.isFinished)
        XCTAssertEqual(scheduler.advance(to: 7), [])
        XCTAssertEqual(scheduler.advance(to: 100), [])
        XCTAssertNil(scheduler.status(at: 7))
        XCTAssertEqual(scheduler.requestedCaptureCount, 0)
    }

    func testSchedulerDropsStaleCuesAndCapturesButNeverLabelsOrFinish() {
        var scheduler = GuidedRecordingScheduler(script: tinyScript)
        // One huge stall: everything is due at once.
        let actions = scheduler.advance(to: 100)
        XCTAssertFalse(actions.contains { if case .speak = $0 { return true }; return false })
        XCTAssertFalse(actions.contains { if case .countdown = $0 { return true }; return false })
        XCTAssertFalse(actions.contains { if case .captureLossless = $0 { return true }; return false })
        XCTAssertEqual(actions, [.setPhase(.leadIn(0)), .setLabel(.unlabeled),
                                 .setPhase(.hold(0)), .setLabel(.noSaber),
                                 .setPhase(.leadIn(1)), .setLabel(.unlabeled),
                                 .setPhase(.hold(1)), .setLabel(.sabersVisible),
                                 .setPhase(nil), .finish])
        XCTAssertTrue(scheduler.isFinished)
        // A short delay inside the tolerance keeps the cue.
        var late = GuidedRecordingScheduler(script: tinyScript)
        XCTAssertTrue(late.advance(to: 0.8).contains(.speak("cue A")))
    }

    func testSchedulerStatusShowsStepPhaseAndSecondsRemaining() throws {
        let scheduler = GuidedRecordingScheduler(script: tinyScript)
        let start = try XCTUnwrap(scheduler.status(at: 0))
        XCTAssertEqual(start.stepIndex, 0)
        XCTAssertEqual(start.stepCount, 2)
        XCTAssertEqual(start.phase, .leadIn(0))
        XCTAssertEqual(start.secondsRemaining, 5)
        XCTAssertEqual(start.totalSecondsRemaining, 18)
        XCTAssertEqual(start.label, .noSaber)
        let hold = try XCTUnwrap(scheduler.status(at: 5.2))
        XCTAssertEqual(hold.phase, .hold(0))
        XCTAssertEqual(hold.secondsRemaining, 4)
        let next = try XCTUnwrap(scheduler.status(at: 9.5))
        XCTAssertEqual(next.phase, .leadIn(1))
        XCTAssertEqual(next.title, "B")
        XCTAssertEqual(next.secondsRemaining, 4)
        XCTAssertEqual(try XCTUnwrap(scheduler.status(at: 17.9)).secondsRemaining, 1)
        XCTAssertNil(scheduler.status(at: 18))
    }

    // MARK: Ledger and metadata

    func testLedgerCountsHoldFramesPerStepAndRoundTripsThroughJSON() throws {
        var ledger = DebugGuidedLedger(script: tinyScript)
        func observe(_ id: UInt64, _ phase: DebugGuidedPhase?, red: Bool = false, blue: Bool = false,
                     measuredRed: Bool? = nil, captured: Bool = false) {
            ledger.observe(frameID: id, timestamp: Double(id) / 30, phase: phase,
                           detected: ["red": red, "blue": blue],
                           measured: ["red": measuredRed ?? red, "blue": blue], losslessCaptured: captured)
        }
        observe(1, nil, red: true)                 // before the guide: ignored
        observe(2, .leadIn(0), red: true)          // lead-in: boundary only
        observe(3, .leadIn(0))
        observe(4, .hold(0), red: true)            // noSaber + red = false positive
        observe(5, .hold(0), red: true, measuredRed: false)
        observe(6, .hold(0))
        observe(7, .leadIn(1))
        observe(8, .hold(1), red: true, blue: true, captured: true)
        observe(9, .hold(1), red: true)
        ledger.setOutcome(.cancelled)

        let summary = ledger.summary
        XCTAssertEqual(summary.scriptID, "test_script")
        XCTAssertEqual(summary.scriptVersion, 7)
        XCTAssertEqual(summary.outcome, "cancelled")
        XCTAssertEqual(summary.plannedSeconds, 18)
        let a = summary.steps[0], b = summary.steps[1]
        XCTAssertEqual(a.label, "noSaber")
        XCTAssertEqual([a.leadInStartFrameID, a.holdStartFrameID, a.holdEndFrameID], [2, 4, 6])
        XCTAssertEqual(a.frames, 3)
        XCTAssertEqual(a.red, .init(detectedFrames: 2, measuredFrames: 1))
        XCTAssertEqual(a.blue, .init(detectedFrames: 0, measuredFrames: 0))
        XCTAssertEqual([b.leadInStartFrameID, b.holdStartFrameID, b.holdEndFrameID], [7, 8, 9])
        XCTAssertEqual(b.frames, 2)
        XCTAssertEqual(b.red.detectedFrames, 2)
        XCTAssertEqual(b.blue.detectedFrames, 1)
        XCTAssertEqual(summary.losslessCaptures, [.init(stepIndex: 1, frameID: 8)])

        // Encode → JSONSerialization (metadata root) → decode gives the same value.
        let object = summary.jsonObject
        XCTAssertEqual(object["formatVersion"] as? Int, 1)
        let data = try JSONSerialization.data(withJSONObject: object)
        XCTAssertEqual(try JSONDecoder().decode(DebugGuidedRecordingSummary.self, from: data), summary)

        // An unreached step omits its frame boundaries instead of writing zeros.
        let fresh = DebugGuidedLedger(script: tinyScript).summary.jsonObject
        let steps = try XCTUnwrap(fresh["steps"] as? [[String: Any]])
        XCTAssertNil(steps[0]["holdStartFrameID"])
        XCTAssertEqual(steps[0]["frames"] as? Int, 0)
        XCTAssertEqual(fresh["outcome"] as? String, "incomplete")
    }

    func testTriageReserveTakesOneFramePerStepFirst() {
        let guided: [String: Any] = ["losslessCaptures": [
            ["stepIndex": 5, "frameID": 100], ["stepIndex": 6, "frameID": 200],
            ["stepIndex": 6, "frameID": 210], ["stepIndex": 6, "frameID": 220],
            ["stepIndex": 10, "frameID": 300], ["stepIndex": 12, "frameID": 400]]]
        let all: [UInt64] = [400, 100, 220, 210, 300, 200]
        XCTAssertEqual(DebugGuidedTriageReserve.pick(manualFrameIDs: all, guided: guided, limit: 4),
                       [100, 200, 300, 400])
        XCTAssertEqual(DebugGuidedTriageReserve.pick(manualFrameIDs: all, guided: guided, limit: 5),
                       [100, 200, 210, 300, 400])
        XCTAssertEqual(DebugGuidedTriageReserve.pick(manualFrameIDs: all, guided: guided, limit: 10).count, 6)
        // Manual recordings (no guidedRecording) reserve nothing.
        XCTAssertEqual(DebugGuidedTriageReserve.pick(manualFrameIDs: all, guided: nil, limit: 4), [])
        // Unattributed frames come after attributed ones.
        XCTAssertEqual(DebugGuidedTriageReserve.pick(manualFrameIDs: [1, 100], guided: guided, limit: 1), [100])
    }

    // MARK: Recorder integration

    private func pixelBuffer(width: Int = 64, height: Int = 48) -> CVPixelBuffer {
        var buffer: CVPixelBuffer?
        CVPixelBufferCreate(kCFAllocatorDefault, width, height, kCVPixelFormatType_32BGRA,
                            [kCVPixelBufferIOSurfacePropertiesKey as String: [:]] as CFDictionary, &buffer)
        let pixelBuffer = buffer!
        CVPixelBufferLockBaseAddress(pixelBuffer, [])
        if let base = CVPixelBufferGetBaseAddress(pixelBuffer) {
            memset(base, 16, CVPixelBufferGetBytesPerRow(pixelBuffer) * height)
        }
        CVPixelBufferUnlockBaseAddress(pixelBuffer, [])
        return pixelBuffer
    }

    private func record(guided: GuidedRecordingScript?, frames: [(UInt64, DebugGuidedPhase?, Bool)],
                        outcome: DebugGuidedOutcome? = nil, captures: Set<UInt64> = [])
        async throws -> (metadata: [String: Any], summary: [String: Any]?, manualResults: [Bool]) {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("PhoneSaberGuided-\(UUID().uuidString)", isDirectory: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: directory) }
        let oldPreference = UserDefaults.standard.object(forKey: DebugBundleTransfer.preferenceKey)
        UserDefaults.standard.set(false, forKey: DebugBundleTransfer.preferenceKey)
        addTeardownBlock { UserDefaults.standard.set(oldPreference, forKey: DebugBundleTransfer.preferenceKey) }
        let recorder = try DebugVideoRecorder(directory: directory, guidedScript: guided)
        var manualResults: [Bool] = []
        for (index, (id, phase, red)) in frames.enumerated() {
            if captures.contains(id) {
                recorder.requestManualCapture { manualResults.append((try? $0.get()) != nil) }
            }
            let buffer = pixelBuffer()
            CVPixelBufferLockBaseAddress(buffer, .readOnly)
            let saber = DetectedSaber(endpoints: (PixelPoint(x: 8, y: 24), PixelPoint(x: 52, y: 24)),
                                      color: .red, isFresh: true)
            _ = recorder.append(pixelBuffer: buffer,
                                presentationTime: CMTime(value: CMTimeValue(index), timescale: 30),
                                frameID: id, results: red ? [saber] : [],
                                segmentLabel: phase.map { phase -> DebugSegmentLabel in
                                    if case .hold(let step) = phase, let guided { return guided.steps[step].label }
                                    return .unlabeled
                                } ?? .unlabeled,
                                guidedPhase: phase)
            CVPixelBufferUnlockBaseAddress(buffer, .readOnly)
            // Let the H.264 writer accept the next frame (a skipped frame would
            // leave the one-shot capture armed), as the other recorder tests do.
            Thread.sleep(forTimeInterval: 0.02)
        }
        if let outcome { recorder.setGuidedOutcome(outcome) }
        let recording: DebugRecordingResult = try await withCheckedThrowingContinuation { continuation in
            recorder.finish { continuation.resume(with: $0) }
        }
        let data = try Data(contentsOf: recording.metadataURL)
        // Existing Codable readers ignore the additive root field.
        XCTAssertNoThrow(try JSONDecoder().decode(DebugRecordingMetadata.self, from: data))
        let metadata = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        var summary: [String: Any]?
        if let bundle = recording.triageBundleURL {
            summary = try JSONSerialization.jsonObject(
                with: Data(contentsOf: bundle.appendingPathComponent("summary.json"))) as? [String: Any]
        }
        return (metadata, summary, manualResults)
    }

    func testGuidedRecorderWritesStepMetadataAndAllowsTheGuidedCaptureLimit() async throws {
        var frames: [(UInt64, DebugGuidedPhase?, Bool)] = [(1, .leadIn(0), false), (2, .leadIn(0), false)]
        frames += (3...5).map { ($0, .hold(0), $0 == 4) }       // noSaber: one red false positive
        frames += [(6, .leadIn(1), true)]
        frames += (7...20).map { ($0, .hold(1), true) }          // swing step: captures
        let captures: Set<UInt64> = [8, 10, 12, 14, 16, 18, 20]  // seven requests, limit six
        let (metadata, summary, manual) = try await record(
            guided: tinyScript, frames: frames, outcome: .completed, captures: captures)
        XCTAssertEqual(manual.filter { $0 }.count, DebugRecordingLimits.maximumGuidedLosslessCaptures)
        XCTAssertEqual(manual.last, false)

        let guided = try XCTUnwrap(metadata["guidedRecording"] as? [String: Any])
        let decoded = try JSONDecoder().decode(DebugGuidedRecordingSummary.self,
                                               from: JSONSerialization.data(withJSONObject: guided))
        XCTAssertEqual(decoded.outcome, "completed")
        XCTAssertEqual(decoded.scriptID, "test_script")
        XCTAssertEqual(decoded.steps[0].frames, 3)
        XCTAssertEqual(decoded.steps[0].red.detectedFrames, 1)
        XCTAssertEqual(decoded.steps[0].holdStartFrameID, 3)
        XCTAssertEqual(decoded.steps[1].leadInStartFrameID, 6)
        XCTAssertEqual(decoded.steps[1].frames, 14)
        XCTAssertEqual(decoded.losslessCaptures.map(\.frameID), [8, 10, 12, 14, 16, 18])
        XCTAssertTrue(decoded.losslessCaptures.allSatisfy { $0.stepIndex == 1 })
        // The labels went through the ordinary segment marker path.
        let markers = try XCTUnwrap(metadata["segmentMarkers"] as? [[String: Any]])
        XCTAssertEqual(markers.compactMap { $0["label"] as? String }, ["noSaber", "unlabeled", "sabersVisible"])

        // summary.json carries the same object, and the reserved swing frames are in the bundle.
        let triage = try XCTUnwrap(summary)
        XCTAssertNotNil(triage["guidedRecording"] as? [String: Any])
        let images = try XCTUnwrap(triage["images"] as? [[String: Any]])
        let manualIDs = images.filter { ($0["path"] as? String)?.contains("manual_frame_") == true }
            .compactMap { $0["frameID"] as? Int }
        XCTAssertGreaterThanOrEqual(manualIDs.count, DebugRecordingTriageLimits.guidedManualImageReserve)
    }

    func testManualRecorderIsUnchangedWithoutAGuidedScript() async throws {
        let frames: [(UInt64, DebugGuidedPhase?, Bool)] = (1...10).map { ($0, nil, true) }
        let (metadata, summary, manual) = try await record(guided: nil, frames: frames,
                                                           captures: [2, 4, 6, 8])
        XCTAssertNil(metadata["guidedRecording"])
        XCTAssertNil(summary?["guidedRecording"])
        XCTAssertEqual(manual, [true, true, true, false])
    }

    func testGuidedRecorderWithoutOutcomeIsIncomplete() async throws {
        let (metadata, _, _) = try await record(
            guided: tinyScript, frames: [(1, .leadIn(0), false), (2, .hold(0), false)])
        let guided = try XCTUnwrap(metadata["guidedRecording"] as? [String: Any])
        XCTAssertEqual(guided["outcome"] as? String, "incomplete")
        XCTAssertEqual((guided["steps"] as? [[String: Any]])?[1]["holdStartFrameID"] as? Int, nil)
    }

    // MARK: Audio cues

    @MainActor
    func testSpeechCuePlayerRunsOnTheSimulatorWithoutCrashing() async throws {
        let player = SpeechGuidedRecordingCuePlayer()
        player.begin()
        player.speak("テスト")
        player.countdown(3)
        player.countdown(0)
        try await Task.sleep(nanoseconds: 200_000_000)
        player.cancel()
        player.begin()
        player.speak("終わり")
        player.end()
        try await Task.sleep(nanoseconds: 100_000_000)
        player.cancel()
    }

    @MainActor
    func testGuidedStartIsRefusedWhileTheCameraIsStopped() {
        final class Recorder: GuidedRecordingCuePlaying {
            var calls: [String] = []
            func begin() { calls.append("begin") }
            func speak(_ text: String) { calls.append("speak") }
            func countdown(_ value: Int) { calls.append("countdown") }
            func end() { calls.append("end") }
            func cancel() { calls.append("cancel") }
        }
        let model = CameraViewModel()
        let cues = Recorder()
        model.makeGuidedCuePlayer = { cues }
        model.debugRecordingEnabled = true
        model.startGuidedRecording()           // camera not running: refused like Start Recording
        XCTAssertFalse(model.guidedRecordingRunning)
        XCTAssertFalse(model.debugRecordingActive)
        XCTAssertEqual(cues.calls, [])
        model.cancelGuidedRecording()          // no-op
        XCTAssertFalse(model.guidedRecordingRunning)
        XCTAssertEqual(model.debugSegmentLabel, .unlabeled)
    }
}
