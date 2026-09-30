import Foundation
import XCTest
@testable import PhoneSaberSender

final class DebugRecordingTriageTests: XCTestCase {
    private func motionColor(_ detected: Bool, x: Int = 0, length: Int = 100,
                             candidates: Int = 1, eligible: Int = 1,
                             gap: Double? = nil, margin: Double? = nil,
                             type: String? = "color-mask") -> DebugMotionColorSample {
        DebugMotionColorSample(detected: detected,
            endpoints: detected ? (PixelPoint(x: x, y: 0), PixelPoint(x: x, y: length)) : nil,
            candidateCount: candidates, eligibleCount: eligible,
            topScoreGap: gap, failedRuleMargin: margin,
            selectedCandidateType: type)
    }

    private func motionFrame(_ id: UInt64, _ time: Double,
                             red: DebugMotionColorSample? = nil,
                             blue: DebugMotionColorSample? = nil,
                             processing: Double = 0.005) -> DebugMotionSample {
        DebugMotionSample(frameID: id, timestamp: time, processingSeconds: processing,
            red: red ?? motionColor(false), blue: blue ?? motionColor(false))
    }

    func testMotionDropoutFlickerAndColorIndependence() {
        var detector = DebugMotionDetector()
        for index in 0..<3 {
            _ = detector.observe(motionFrame(UInt64(index), Double(index) / 30,
                red: motionColor(true), blue: motionColor(true)))
        }
        let redHit = detector.observe(motionFrame(3, 0.1,
            red: motionColor(false), blue: motionColor(true)))
        XCTAssertTrue(redHit.contains { $0.color == "red"
            && $0.signals.contains { $0.kind == "dropout" } })
        XCTAssertFalse(redHit.contains { $0.color == "blue" })
        _ = detector.observe(motionFrame(4, 4.0 / 30, red: motionColor(true)))
        let flicker = detector.observe(motionFrame(5, 5.0 / 30,
            red: motionColor(false)))
        XCTAssertTrue(flicker.contains { $0.color == "red" })
        XCTAssertEqual(detector.signalCounts["flicker"], 1)
    }

    func testMotionJumpLengthPredictionCandidateAndNearMissSignals() {
        var detector = DebugMotionDetector()
        _ = detector.observe(motionFrame(0, 0, red: motionColor(true, x: 0)))
        _ = detector.observe(motionFrame(1, 1.0 / 30,
            red: motionColor(true, x: 10)))
        let jumped = detector.observe(motionFrame(2, 2.0 / 30,
            red: motionColor(true, x: 200, length: 200)))
        let kinds = Set(jumped.flatMap(\.signals).map(\.kind))
        XCTAssertTrue(kinds.contains("endpoint_jump"))
        XCTAssertTrue(kinds.contains("length_change"))
        XCTAssertTrue(kinds.contains("prediction_error"))
        let ambiguous = detector.observe(motionFrame(3, 3.0 / 30,
            red: motionColor(true, x: 205, length: 200,
                             candidates: 2, eligible: 2, gap: 1)))
        XCTAssertFalse(ambiguous.isEmpty)
        XCTAssertEqual(detector.signalCounts["candidate_ambiguity"], 1)
        XCTAssertEqual(detector.signalCounts["multiple_eligible"], 1)
        _ = detector.observe(motionFrame(4, 4.0 / 30,
            red: motionColor(true, x: 210, length: 200, type: "core-line")))
        XCTAssertEqual(detector.signalCounts["candidate_switch"], 1)
        let nearMiss = detector.observe(motionFrame(5, 5.0 / 30,
            red: motionColor(false, candidates: 1, eligible: 0, margin: 0.02)))
        XCTAssertFalse(nearMiss.isEmpty)
        XCTAssertEqual(detector.signalCounts["near_miss"], 1)
    }

    func testEndpointOrderReversalIsNotMotionAnomaly() {
        var detector = DebugMotionDetector()
        for id in 0..<3 {
            var color = motionColor(true)
            if id % 2 == 1 {
                color = DebugMotionColorSample(detected: true,
                    endpoints: (PixelPoint(x: 0, y: 100), PixelPoint(x: 0, y: 0)),
                    candidateCount: 1, eligibleCount: 1, topScoreGap: nil,
                    failedRuleMargin: nil, selectedCandidateType: "color-mask")
            }
            XCTAssertTrue(detector.observe(motionFrame(UInt64(id), Double(id) / 30,
                red: color)).isEmpty)
        }
    }

    func testMotionDelayMergingAndHistoryBound() {
        var detector = DebugMotionDetector()
        _ = detector.observe(motionFrame(1, 0))
        let first = detector.observe(motionFrame(4, 0.1, processing: 0.05))
        XCTAssertEqual(first.count, 1)
        XCTAssertEqual(first.first?.color, "both")
        XCTAssertEqual(Set(first.flatMap(\.signals).map(\.kind)),
                       ["frame_interval", "frame_gap", "processing_time"])
        let merged = detector.observe(motionFrame(5, 0.13, processing: 0.05))
        XCTAssertEqual(merged.first?.index, first.first?.index)
        for index in 6..<100 {
            _ = detector.observe(motionFrame(UInt64(index), Double(index) / 30))
        }
        XCTAssertEqual(detector.history.count, DebugMotionThresholds.historyFrames)
    }

    func testMotionImageSelectionPrioritizesScoreAndKeepsRolesWithinTwelve() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let frames = (0..<15).map { makeFrame($0) }
        var events: [[String: Any]] = []
        for eventIndex in 0..<5 {
            let images = ["event_pre", "event_at", "event_post"].enumerated().map {
                roleIndex, role -> [String: Any] in
                let frameID = eventIndex * 3 + roleIndex
                let name = "motion_event_\(eventIndex)_\(role)_\(frameID).png"
                try? Data("lossless".utf8).write(to: directory.appendingPathComponent(name))
                return ["role": role, "frameID": frameID, "fileName": name]
            }
            events.append(["eventIndex": eventIndex, "color": "red",
                "peakScore": Double(eventIndex + 1), "images": images,
                "signals": [["kind": "dropout", "color": "red",
                    "value": 3.0, "threshold": 3.0, "score": 1.0]]])
        }
        var document = try XCTUnwrap(JSONSerialization.jsonObject(
            with: metadata(frames)) as? [String: Any])
        document["motionEvents"] = events
        document["motionSummary"] = ["events": (0..<5).map {
            [$0, "red", Double($0 + 1), "retained"] as [Any]
        }]
        let data = try JSONSerialization.data(withJSONObject: document)
        let selection = try DebugRecordingTriageBuilder.selectImages(
            metadataData: data, forensicDirectoryURL: directory).selection
        XCTAssertEqual(selection.images.count, 12)
        XCTAssertEqual(try DebugRecordingTriageBuilder.selectImages(
            metadataData: data, forensicDirectoryURL: directory, maximumImages: 20)
            .selection.images.count, 12)
        XCTAssertEqual(Set(selection.images.compactMap(\.eventIndex)), [1, 2, 3, 4])
        XCTAssertEqual(selection.images.filter { $0.eventIndex == 4 }.map(\.role),
                       ["event_at", "event_pre", "event_post"])
        XCTAssertTrue(selection.images.allSatisfy { !$0.signals.isEmpty && $0.score != nil })
        let bundle = try DebugRecordingTriageBuilder.build(metadataData: data,
            metadataURL: directory.appendingPathComponent("metadata.json"),
            forensicDirectoryURL: directory)
        let summary = try XCTUnwrap(JSONSerialization.jsonObject(with:
            Data(contentsOf: bundle.appendingPathComponent("summary.json"))) as? [String: Any])
        let motionSummary = try XCTUnwrap(summary["motionEventSummary"] as? [String: Any])
        let ledger = try XCTUnwrap(motionSummary["events"] as? [[Any]])
        XCTAssertEqual(ledger[0][3] as? String, "image_limit")
        XCTAssertTrue(ledger.dropFirst().allSatisfy { $0[3] as? String == "selected" })
    }

    func testMotionSelectionHonorsTransportByteBudget() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let large = directory.appendingPathComponent("large.png")
        FileManager.default.createFile(atPath: large.path, contents: Data())
        let handle = try FileHandle(forWritingTo: large)
        try handle.truncate(atOffset: 63 * 1_024 * 1_024)
        try handle.close()
        try Data("small".utf8).write(to: directory.appendingPathComponent("small.png"))
        var document = try XCTUnwrap(JSONSerialization.jsonObject(with:
            metadata([makeFrame(0), makeFrame(1)])) as? [String: Any])
        document["motionEvents"] = [
            ["eventIndex": 0, "color": "red", "peakScore": 4.0,
             "signals": [], "images": [["role": "event_at", "frameID": 0, "fileName": "large.png"]]],
            ["eventIndex": 1, "color": "red", "peakScore": 2.0,
             "signals": [], "images": [["role": "event_at", "frameID": 1, "fileName": "small.png"]]]]
        document["motionSummary"] = ["events": [[0, "red", 4.0, "retained"],
                                                [1, "red", 2.0, "retained"]]]
        let bundle = try DebugRecordingTriageBuilder.build(
            metadataData: JSONSerialization.data(withJSONObject: document),
            metadataURL: directory.appendingPathComponent("metadata.json"),
            forensicDirectoryURL: directory)
        let summary = try XCTUnwrap(JSONSerialization.jsonObject(with:
            Data(contentsOf: bundle.appendingPathComponent("summary.json"))) as? [String: Any])
        XCTAssertEqual(summary["selectedImageCount"] as? Int, 1)
        let motionSummary = try XCTUnwrap(summary["motionEventSummary"] as? [String: Any])
        let ledger = try XCTUnwrap(motionSummary["events"] as? [[Any]])
        XCTAssertEqual(ledger[0][3] as? String, "byte_limit")
        XCTAssertEqual(ledger[1][3] as? String, "selected")
    }

    func testDebugRecordingOffDoesNotEnterRecordingOrStopLifecycle() {
        XCTAssertFalse(DebugRecordingLifecyclePolicy.mayStart(
            enabled: false, cameraRunning: true, active: false, finalizing: false
        ))
        XCTAssertFalse(DebugRecordingLifecyclePolicy.mayStop(active: false, finalizing: false))
    }

    func testBroadCorelessSuspectUsesLongitudinalHighBrightnessCoverage() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        var frame = makeFrame(0)
        frame["forensicCaptured"] = true
        frame["forensicFileName"] = "frame_0.png"
        frame["candidateDiagnostics"] = ["red": [
            "selectedCandidateType": "color-component",
            "selectedCandidate": [
                "sourceType": "color-component", "rawPCASpan": 210.0,
                "robustMainIntervalLength": 125.0,
                "scoreBreakdown": ["coreSupport": 3.0, "longitudinalHighCoverage": 1.0,
                                   "highBrightnessRatio": 18.0]
            ]
        ], "blue": ["totalCandidateCount": 1, "eligibleCandidateCount": 1]] as [String: Any]
        try Data("lossless".utf8).write(to: directory.appendingPathComponent("frame_0.png"))

        let selected = try select([frame], directory: directory)

        XCTAssertTrue(try XCTUnwrap(selected.images.first?.reasons)
            .contains("wide candidate with weak core and high-brightness support"))
    }

    func testManyDropoutsRespectImageAndPerFailureLimitsAndDeduplicateNearbyFrames() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        var frames: [[String: Any]] = []
        for index in 0..<80 {
            let detected = index % 8 < 4
            var frame = makeFrame(index, redDetected: detected, blueDetected: detected)
            if index % 8 == 4 {
                for color in ["red", "blue"] {
                    frame["\(color)DropoutRole"] = "dropout"
                    frame["\(color)DropoutFileName"] = "\(color)_dropout_false_\(index).png"
                    try Data("lossless".utf8).write(to: directory.appendingPathComponent("\(color)_dropout_false_\(index).png"))
                }
            } else if index % 8 == 3 {
                for color in ["red", "blue"] {
                    frame["\(color)DropoutRole"] = "last-detected-before-dropout"
                    frame["\(color)DropoutFileName"] = "\(color)_dropout_last_true_\(index).png"
                    try Data("lossless".utf8).write(to: directory.appendingPathComponent("\(color)_dropout_last_true_\(index).png"))
                }
            }
            frames.append(frame)
        }

        let selected = try select(frames, directory: directory)
        XCTAssertLessThanOrEqual(selected.images.count, DebugRecordingTriageLimits.defaultImageCount)
        XCTAssertLessThanOrEqual(selected.images.count, DebugRecordingTriageLimits.hardImageCount)
        XCTAssertLessThanOrEqual(selected.images.filter { $0.failureType == "dropout" }.count, 2)
        XCTAssertEqual(Set(selected.images.map(\.fileName)).count, selected.images.count)
        XCTAssertTrue(selected.images.contains { $0.fileName.contains("dropout_false") })
    }

    func testRedAndBlueDropoutsAreBothRepresented() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        var frames = (0..<6).map { makeFrame($0, redDetected: true, blueDetected: true) }
        frames[1]["redDetectionSucceeded"] = false
        frames[1]["red"] = detection(detected: false)
        frames[1]["redDropoutRole"] = "dropout"
        frames[1]["redDropoutFileName"] = "red_dropout_false_1.png"
        frames[3]["blueDetectionSucceeded"] = false
        frames[3]["blue"] = detection(detected: false)
        frames[3]["blueDropoutRole"] = "dropout"
        frames[3]["blueDropoutFileName"] = "blue_dropout_false_3.png"
        for name in ["red_dropout_false_1.png", "blue_dropout_false_3.png"] {
            try Data("png".utf8).write(to: directory.appendingPathComponent(name))
        }

        let selected = try select(frames, directory: directory)
        XCTAssertTrue(selected.images.contains { $0.color == "red" })
        XCTAssertTrue(selected.images.contains { $0.color == "blue" })
    }

    func testManualCaptureHasPriorityOverEarlierAnomalyImage() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        var frames = (0..<8).map { makeFrame($0) }
        frames[0]["forensicCaptured"] = true
        frames[0]["forensicFileName"] = "frame_0.png"
        frames[7]["manualCaptured"] = true
        frames[7]["manualFileName"] = "manual_frame_7.png"
        for name in ["frame_0.png", "manual_frame_7.png"] {
            try Data("png".utf8).write(to: directory.appendingPathComponent(name))
        }

        let selected = try select(frames, directory: directory, maximumImages: 1)
        XCTAssertEqual(selected.images.map(\.fileName), ["manual_frame_7.png"])
        XCTAssertEqual(selected.images.first?.failureType, "manual_capture")
    }

    func testNoFailureSessionSelectsNoImages() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let frames = (0..<5).map { makeFrame($0) }

        let selected = try select(frames, directory: directory)
        XCTAssertTrue(selected.images.isEmpty)
        XCTAssertEqual(selected.incidentCount, 0)
    }

    func testMalformedMetadataIsRejected() {
        XCTAssertThrowsError(try DebugRecordingTriageBuilder.selectImages(
            metadataData: Data("{broken".utf8), forensicDirectoryURL: nil
        ))
    }

    func testBundleContainsOnlySelectedPngAndSmallContext() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let forensic = directory.appendingPathComponent("forensic", isDirectory: true)
        try FileManager.default.createDirectory(at: forensic, withIntermediateDirectories: true)
        var frames = (0..<7).map { makeFrame($0) }
        frames[3]["manualCaptured"] = true
        frames[3]["manualFileName"] = "manual_frame_3.png"
        try Data("PNG bytes remain unchanged".utf8).write(to: forensic.appendingPathComponent("manual_frame_3.png"))
        let metadataURL = directory.appendingPathComponent("metadata.json")
        try metadata(frames).write(to: metadataURL)

        let bundle = try DebugRecordingTriageBuilder.build(
            metadataData: try metadata(frames), metadataURL: metadataURL,
            forensicDirectoryURL: forensic
        )
        let relativePaths = try FileManager.default.subpathsOfDirectory(atPath: bundle.path).sorted()
        XCTAssertTrue(relativePaths.contains("summary.json"))
        XCTAssertTrue(relativePaths.contains("prompt.md"))
        XCTAssertEqual(relativePaths.filter { $0.hasSuffix(".png") }.count, 1)
        XCTAssertFalse(relativePaths.contains { $0.hasSuffix(".mp4") || $0 == "metadata.json" })
        let contextURL = try XCTUnwrap(relativePaths.first { $0.hasPrefix("frames/") && $0.hasSuffix(".json") })
        let context = try JSONSerialization.jsonObject(
            with: Data(contentsOf: bundle.appendingPathComponent(contextURL))
        ) as? [String: Any]
        let contextFrames = try XCTUnwrap(context?["frames"] as? [[String: Any]])
        XCTAssertLessThanOrEqual(contextFrames.count, 5)
        XCTAssertNotNil(contextFrames.first?["red"])
        XCTAssertNotNil(contextFrames.first?["blue"])
    }

    func testMissingDiagnosticsRemainUnknownInCodexContext() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        var frame = makeFrame(0)
        frame.removeValue(forKey: "candidateDiagnostics")
        frame.removeValue(forKey: "redDetectionSucceeded")
        frame.removeValue(forKey: "blueDetectionSucceeded")
        frame["red"] = ["predicted": false]
        frame["blue"] = [:]
        frame["manualCaptured"] = true
        frame["manualFileName"] = "manual_frame_0.png"
        try Data("png".utf8).write(to: directory.appendingPathComponent("manual_frame_0.png"))
        let metadataURL = directory.appendingPathComponent("metadata.json")
        let bundle = try DebugRecordingTriageBuilder.build(
            metadataData: metadata([frame]), metadataURL: metadataURL,
            forensicDirectoryURL: directory
        )
        let context = try JSONSerialization.jsonObject(with: Data(contentsOf:
            bundle.appendingPathComponent("frames/frame_0_1.json"))) as? [String: Any]
        let contextFrames = try XCTUnwrap(context?["frames"] as? [[String: Any]])
        let first = try XCTUnwrap(contextFrames.first)
        for color in ["red", "blue"] {
            let value = try XCTUnwrap(first[color] as? [String: Any])
            XCTAssertNil(value["candidateCount"])
            XCTAssertNil(value["eligibleCandidateCount"])
            XCTAssertNil(value["detectionSucceeded"])
            XCTAssertNil(value["detected"])
        }
    }

    func testEligibilityDropoutCopiesFailedRuleAcrossContextFrames() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        var frames = (0..<2).map { makeFrame($0, redDetected: false) }
        let rejected: [String: Any] = [
            "index": 0, "sourceType": "color-mask", "eligible": false,
            "finalScore": 12.5, "rejectionReasons": ["peakValue"],
            "eligibilityRules": [["name": "peakValue", "result": "FAIL",
                                  "value": 210.0, "comparison": ">=", "threshold": 218.0]]
        ]
        for index in frames.indices {
            frames[index]["candidateDiagnostics"] = [
                "red": ["totalCandidateCount": 1, "eligibleCandidateCount": 0,
                        "topCandidates": [rejected]],
                "blue": ["totalCandidateCount": 0, "eligibleCandidateCount": 0]
            ]
        }
        frames[1]["manualCaptured"] = true
        frames[1]["manualFileName"] = "manual_frame_1.png"
        try Data("synthetic".utf8).write(
            to: directory.appendingPathComponent("manual_frame_1.png"))
        let bundle = try DebugRecordingTriageBuilder.build(
            metadataData: metadata(frames),
            metadataURL: directory.appendingPathComponent("metadata.json"),
            forensicDirectoryURL: directory)
        let context = try JSONSerialization.jsonObject(with: Data(contentsOf:
            bundle.appendingPathComponent("frames/frame_1_1.json"))) as? [String: Any]
        let contextFrames = try XCTUnwrap(context?["frames"] as? [[String: Any]])
        XCTAssertEqual(contextFrames.count, 2)
        for frame in contextFrames {
            let red = try XCTUnwrap(frame["red"] as? [String: Any])
            XCTAssertEqual(red["candidateCount"] as? Int, 1)
            XCTAssertEqual(red["eligibleCandidateCount"] as? Int, 0)
            XCTAssertEqual(red["failureStage"] as? String, "eligibility")
            let trace = try XCTUnwrap(red["candidateDecisionTrace"] as? [[String: Any]])
            let rules = try XCTUnwrap(trace.first?["rules"] as? [[String: Any]])
            XCTAssertEqual(rules.first?["name"] as? String, "peakValue")
            XCTAssertEqual(rules.first?["value"] as? Double, 210)
            XCTAssertEqual(rules.first?["threshold"] as? Double, 218)
        }
    }

    func testMissingRequiredPngRejectsBundle() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        var frame = makeFrame(0)
        frame["manualCaptured"] = true
        frame["manualFileName"] = "manual_frame_0.png"
        XCTAssertThrowsError(try DebugRecordingTriageBuilder.build(
            metadataData: metadata([frame]),
            metadataURL: directory.appendingPathComponent("metadata.json"),
            forensicDirectoryURL: directory
        ))
        XCTAssertFalse(FileManager.default.fileExists(atPath:
            directory.appendingPathComponent("phone_saber_triage_test_session").path))
    }

    func testThreeManualImagesBypassFailureTypeLimit() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        var frames = (0..<3).map { makeFrame($0) }
        for index in frames.indices {
            frames[index]["manualCaptured"] = true
            frames[index]["manualFileName"] = "manual_frame_\(index).png"
            try Data("png".utf8).write(to: directory.appendingPathComponent("manual_frame_\(index).png"))
        }
        let selection = try DebugRecordingTriageBuilder.selectImages(
            metadataData: metadata(frames), forensicDirectoryURL: directory,
            maximumImages: 3, perFailureType: 1
        ).selection
        XCTAssertEqual(selection.images.count, 3)
        XCTAssertTrue(selection.images.allSatisfy { $0.failureType == "manual_capture" })
    }

    func testCandidateEligibleJumpRawTailAndIdenticalIncidents() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        var frames = (0..<28).map { makeFrame($0) }
        frames[0]["candidateDiagnostics"] = [
            "red": ["totalCandidateCount": 0, "eligibleCandidateCount": 0],
            "blue": ["totalCandidateCount": 1, "eligibleCandidateCount": 1]
        ]
        frames[5]["candidateDiagnostics"] = [
            "red": ["totalCandidateCount": 2, "eligibleCandidateCount": 0],
            "blue": ["totalCandidateCount": 1, "eligibleCandidateCount": 1]
        ]
        frames[10]["red"] = detection(detected: true, x: 500)
        frames[15]["candidateDiagnostics"] = [
            "red": ["totalCandidateCount": 1, "eligibleCandidateCount": 1,
                    "selectedCandidate": ["sourceType": "core-line", "rawPCASpan": 400.0,
                                          "robustMainIntervalLength": 100.0]],
            "blue": ["totalCandidateCount": 1, "eligibleCandidateCount": 1]
        ]
        for index in 20...23 { frames[index]["red"] = detection(detected: true, x: 50) }
        for index in [0, 5, 10, 15, 23] {
            frames[index]["forensicCaptured"] = true
            frames[index]["forensicFileName"] = "frame_\(index).png"
            try Data("png".utf8).write(to: directory.appendingPathComponent("frame_\(index).png"))
        }
        let selection = try select(frames, directory: directory)
        let types = Set(selection.images.map(\.failureType))
        XCTAssertTrue(types.contains("candidate_zero"))
        XCTAssertTrue(types.contains("eligible_zero"))
        XCTAssertTrue(types.contains("endpoint_jump_ge260"))
        XCTAssertTrue(types.contains("core_line_tail_suspect"))
        XCTAssertTrue(types.contains("identical_endpoint_suspect"))
    }

    func testManualCountsFromZeroThroughThree() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        for count in 0...3 {
            var frames = (0..<3).map { makeFrame($0) }
            for index in 0..<count {
                frames[index]["manualCaptured"] = true
                frames[index]["manualFileName"] = "manual_frame_\(index).png"
                try Data("png".utf8).write(to: directory.appendingPathComponent("manual_frame_\(index).png"))
            }
            XCTAssertEqual(try select(frames, directory: directory).images.count, count)
        }
    }

    func testHardImageLimitCapsLargeSelection() throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        var frames = (0..<150).map { makeFrame($0) }
        for index in stride(from: 0, to: frames.count, by: 5) {
            frames[index]["forensicCaptured"] = true
            frames[index]["forensicFileName"] = "frame_\(index).png"
            try Data("png".utf8).write(to: directory.appendingPathComponent("frame_\(index).png"))
        }
        let selection = try DebugRecordingTriageBuilder.selectImages(
            metadataData: metadata(frames), forensicDirectoryURL: directory,
            maximumImages: 100, perFailureType: 100
        ).selection
        XCTAssertEqual(selection.images.count, DebugRecordingTriageLimits.hardImageCount)
    }

    func testIncidentAccumulatorStaysBoundedForLargeSyntheticRecording() throws {
        var accumulator = DebugRecordingTriageAccumulator()
        for index in 0..<50_000 {
            accumulator.observe(DebugRecordingFrameMetadata(
                frameID: UInt64(index), presentationTimeSeconds: Double(index) / 60,
                red: .notDetected, blue: .notDetected,
                redDetectionSucceeded: false, blueDetectionSucceeded: false,
                candidateDiagnostics: nil, forensicCaptured: false,
                forensicFileName: nil, manualCaptured: true,
                manualFileName: "manual_frame_\(index).png",
                blueDropoutRole: nil, blueDropoutFileName: nil,
                redDropoutRole: nil, redDropoutFileName: nil
            ))
        }
        XCTAssertLessThanOrEqual(accumulator.retainedFrames.count,
                                 DebugRecordingTriageAccumulator.maximumRetainedFrames)
        XCTAssertLessThan(try JSONEncoder().encode(accumulator.retainedFrames).count, 200_000)
    }

    private func select(_ frames: [[String: Any]], directory: URL,
                        maximumImages: Int = DebugRecordingTriageLimits.defaultImageCount) throws
        -> DebugRecordingTriageSelection {
        try DebugRecordingTriageBuilder.selectImages(
            metadataData: metadata(frames), forensicDirectoryURL: directory,
            maximumImages: maximumImages
        ).selection
    }

    private func metadata(_ frames: [[String: Any]]) throws -> Data {
        try JSONSerialization.data(withJSONObject: ["sessionID": "test_session", "frames": frames])
    }

    private func makeFrame(_ index: Int, redDetected: Bool = true,
                           blueDetected: Bool = true) -> [String: Any] {
        [
            "frameID": index,
            "presentationTimeSeconds": Double(index) / 30.0,
            "red": detection(detected: redDetected, x: 10 + index),
            "blue": detection(detected: blueDetected, x: 30 + index),
            "redDetectionSucceeded": redDetected,
            "blueDetectionSucceeded": blueDetected,
            "candidateDiagnostics": [
                "red": ["totalCandidateCount": 1, "eligibleCandidateCount": 1],
                "blue": ["totalCandidateCount": 1, "eligibleCandidateCount": 1]
            ],
            "forensicCaptured": false,
            "manualCaptured": false
        ]
    }

    private func detection(detected: Bool, x: Int = 10) -> [String: Any] {
        ["detected": detected, "predicted": false,
         "x1": x, "y1": 10, "x2": x + 100, "y2": 10]
    }

    private func makeTemporaryDirectory() throws -> URL {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("phonesaber-triage-tests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }
}
