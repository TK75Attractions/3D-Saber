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

extension DebugRecordingTriageTests {
    private func trackingCandidate(x: Int = 0, index: Int = 0, finalLength: Int = 100,
                                   source: String = "fallbackPCA") throws -> DebugRecordingCandidate {
        let points = (0...100).flatMap { x in (0...5).map { PixelPoint(x: x, y: $0) } }
        let proposal = try XCTUnwrap(saberCandidate(from: points, width: 640, height: 480,
                                                   collectEndpointDiagnostics: true))
        let candidate = DebugRecordingCandidate(index: index, candidate: proposal, selectedIndex: index)
        var object = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(candidate)) as? [String: Any])
        let raw: [String: Any] = ["first": ["x": x, "y": 2], "second": ["x": x + 100, "y": 2]]
        let final: [String: Any] = ["first": ["x": x, "y": 2], "second": ["x": x + finalLength, "y": 2]]
        object["rawPCAEndpoints"] = raw
        object["finalOutputEndpoints"] = final
        object["centroid"] = [Double(x + 50), 2.5]
        object["bbox"] = [x, 0, x + 100, 5]
        var pipeline = try XCTUnwrap(object["endpointPipeline"] as? [String: Any])
        pipeline["rawPCA"] = raw; pipeline["robustInterval"] = raw
        pipeline["finalSelected"] = final; pipeline["endpointSource"] = source
        pipeline["bodyAdopted"] = source == "bodyPCA"
        if source == "bodyPCA" { pipeline["body"] = final; pipeline.removeValue(forKey: "fallbackReason") }
        else { pipeline["fallbackReason"] = "bodyNotStronglyTrimmed" }
        object["endpointPipeline"] = pipeline
        return try JSONDecoder().decode(DebugRecordingCandidate.self, from: JSONSerialization.data(withJSONObject: object))
    }

    private func trackingFrame(_ id: UInt64, candidate: DebugRecordingCandidate?,
                               alternatives: [DebugRecordingCandidate] = []) throws -> DebugRecordingFrameMetadata {
        let empty = DebugRecordingColorCandidates([], pipeline: nil)
        var object = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(empty)) as? [String: Any])
        let candidates = candidate.map { [$0] + alternatives } ?? alternatives
        object["totalCandidateCount"] = candidates.count
        object["eligibleCandidateCount"] = candidates.count
        object["topCandidates"] = try candidates.map { try JSONSerialization.jsonObject(with: JSONEncoder().encode($0)) }
        if let candidate {
            object["selectedCandidate"] = try JSONSerialization.jsonObject(with: JSONEncoder().encode(candidate))
            object["selectedCandidateIndex"] = candidate.index
            object["selectedCandidateType"] = candidate.sourceType
            object["selectedCandidateFinalScore"] = candidate.finalScore
        }
        let red = try JSONDecoder().decode(DebugRecordingColorCandidates.self,
            from: JSONSerialization.data(withJSONObject: object))
        var diagnosticsObject: [String: Any] = [:]
        diagnosticsObject["red"] = object
        diagnosticsObject["blue"] = try JSONSerialization.jsonObject(with: JSONEncoder().encode(empty))
        let diagnostics = try JSONDecoder().decode(DebugRecordingCandidateDiagnostics.self,
            from: JSONSerialization.data(withJSONObject: diagnosticsObject))
        let p = candidate?.finalOutputEndpoints
        let endpoints = p.map { (PixelPoint(x: $0.first.x, y: $0.first.y), PixelPoint(x: $0.second.x, y: $0.second.y)) }
        XCTAssertEqual(red.selectedCandidateIndex, candidate?.index)
        return DebugRecordingFrameMetadata(frameID: id, presentationTimeSeconds: Double(id) / 30,
            red: DebugRecordingDetection(endpoints: endpoints), blue: .notDetected,
            redDetectionSucceeded: candidate != nil, blueDetectionSucceeded: false,
            candidateDiagnostics: diagnostics, forensicCaptured: false, forensicFileName: nil,
            manualCaptured: false, manualFileName: nil)
    }

    func testTrackingStableAndFastSmoothTranslationHaveLowInstability() throws {
        for speed in [0, 160] {
            let older = try trackingFrame(0, candidate: trackingCandidate(x: 0))
            let previous = try trackingFrame(1, candidate: trackingCandidate(x: speed))
            let current = try trackingFrame(2, candidate: trackingCandidate(x: speed * 2))
            let values = DebugTrackingDiagnostics.measure(current, previous: previous, older: older, color: "red")
            XCTAssertEqual(values.instabilityScore, 0, accuracy: 0.00001)
            XCTAssertFalse(values.candidateSwitch)
        }
    }

    func testTrackingCandidateSwitchUsesGeometryAndIgnoresListReordering() throws {
        let a = try trackingCandidate(x: 0, index: 0), b = try trackingCandidate(x: 300, index: 1)
        let previous = try trackingFrame(1, candidate: a, alternatives: [b])
        let switched = try trackingFrame(2, candidate: trackingCandidate(x: 300, index: 0),
                                        alternatives: [trackingCandidate(x: 0, index: 1)])
        let signal = DebugTrackingDiagnostics.measure(switched, previous: previous, older: nil, color: "red")
        XCTAssertTrue(signal.candidateSwitch)
        let reordered = try trackingFrame(2, candidate: trackingCandidate(x: 0, index: 1),
                                          alternatives: [trackingCandidate(x: 300, index: 0)])
        XCTAssertFalse(DebugTrackingDiagnostics.measure(reordered, previous: previous, older: nil, color: "red").candidateSwitch)
    }

    func testTrackingEndpointJumpIsLocalizedAfterStableRawAndRobustGeometry() throws {
        let older = try trackingFrame(0, candidate: trackingCandidate())
        let previous = try trackingFrame(1, candidate: trackingCandidate())
        let current = try trackingFrame(2, candidate: trackingCandidate(finalLength: 350))
        let values = DebugTrackingDiagnostics.measure(current, previous: previous, older: older, color: "red")
        XCTAssertFalse(values.candidateSwitch)
        XCTAssertEqual(values.stageDiscontinuities["rawPCA"], 0)
        XCTAssertEqual(values.stageDiscontinuities["robustInterval"], 0)
        XCTAssertGreaterThan(values.stageDiscontinuities["finalSelected"] ?? 0, 1)
        XCTAssertGreaterThan(values.instabilityScore, 1)
    }

    func testTrackingEndpointSourceAndDropoutRemainExplicit() throws {
        let previous = try trackingFrame(1, candidate: trackingCandidate())
        let changed = try trackingFrame(2, candidate: trackingCandidate(source: "bodyPCA"))
        let values = DebugTrackingDiagnostics.measure(changed, previous: previous, older: nil, color: "red")
        XCTAssertTrue(values.endpointPathChanged)
        XCTAssertEqual(values.instabilityScore, 1)
        let missing = try trackingFrame(3, candidate: nil)
        let dropout = DebugTrackingDiagnostics.measure(missing, previous: changed, older: previous, color: "red")
        XCTAssertTrue(dropout.detectedToggle)
        let recovered = DebugTrackingDiagnostics.measure(changed, previous: missing, older: previous, color: "red")
        XCTAssertTrue(recovered.detectedToggle)
    }

    func testCompoundWeakBridgeShowsSatisfiedTriggerConditions() throws {
        let points = (0...100).flatMap { x in (0...5).map { PixelPoint(x: x, y: $0) } }
        var candidate = try XCTUnwrap(saberCandidate(from: points, width: 640, height: 480))
        candidate.source = "core-line-weak-bridge"
        candidate.isEmitterEligible = false
        candidate.diagnosticRejections = [
            SaberEligibilityDecision(name: "core-line-weak-bridge.span", value: 78.3, comparison: ">=", threshold: 32.4),
            SaberEligibilityDecision(name: "core-line-weak-bridge.retainedBody", value: 0.56, comparison: "<", threshold: 0.65),
            SaberEligibilityDecision(name: "core-line-weak-bridge.bodyPurity", value: 0.625, comparison: ">=", threshold: 0.495)]
        let diagnostic = DebugRecordingCandidate(index: 0, candidate: candidate, selectedIndex: nil)
        XCTAssertTrue(diagnostic.rejectionReasons.contains("core-line-weak-bridge"))
        XCTAssertFalse(diagnostic.eligibilityRules.contains { $0.name == "core-line-weak-bridge.bodyPurity" })
        XCTAssertEqual(diagnostic.compoundRejections.first?.conditions.map(\.satisfied), [true, true, true])
        XCTAssertEqual(diagnostic.compoundRejections.first?.conditions.map(\.comparison), [">=", "<", ">="])
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let frame = try trackingFrame(1, candidate: nil, alternatives: [diagnostic])
        var object = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(frame)) as? [String: Any])
        object["forensicCaptured"] = true; object["forensicFileName"] = "compound.png"
        try Data("lossless".utf8).write(to: directory.appendingPathComponent("compound.png"))
        let document: [String: Any] = ["sessionID": "compound_contract", "frames": [object]]
        let bundle = try DebugRecordingTriageBuilder.build(
            metadataData: JSONSerialization.data(withJSONObject: document),
            metadataURL: directory.appendingPathComponent("metadata.json"), forensicDirectoryURL: directory)
        let summary = try XCTUnwrap(JSONSerialization.jsonObject(with:
            Data(contentsOf: bundle.appendingPathComponent("summary.json"))) as? [String: Any])
        let image = try XCTUnwrap((summary["images"] as? [[String: Any]])?.first)
        let contextPath = try XCTUnwrap(image["frameContextPath"] as? String)
        let context = try XCTUnwrap(JSONSerialization.jsonObject(with:
            Data(contentsOf: bundle.appendingPathComponent(contextPath))) as? [String: Any])
        let contextFrame = try XCTUnwrap((context["frames"] as? [[String: Any]])?.first)
        let red = try XCTUnwrap(contextFrame["red"] as? [String: Any])
        let trace = try XCTUnwrap((red["candidateDecisionTrace"] as? [[String: Any]])?.first)
        let rules = try XCTUnwrap(trace["rules"] as? [[String: Any]])
        let parent = try XCTUnwrap(rules.first { $0["name"] as? String == "core-line-weak-bridge" })
        XCTAssertEqual(Set(parent.keys), Set(["name", "result", "value", "comparison", "threshold"]))
        XCTAssertTrue(parent["value"] is NSNull)
        XCTAssertEqual((trace["compoundRejections"] as? [[String: Any]])?.count, 1)
    }
}

// MARK: - Bridge dropouts, active colors and compact contexts

final class DebugBridgeDropoutTests: XCTestCase {
    private func sample(_ id: UInt64, _ time: Double, _ points: [Double]?) -> DebugBridgeSample {
        DebugBridgeSample(frameID: id, timestamp: time, succeeded: points != nil, endpoints: points)
    }

    private func line(_ x: Double, _ y: Double = 100, length: Double = 100,
                      vertical: Bool = false) -> [Double] {
        vertical ? [x, y, x, y + length] : [x, y, x + length, y]
    }

    // MARK: Assessment

    func testShortLossOfTheSameSaberIsABridgeDropout() {
        let result = DebugBridgeDropout.assess(
            prior: [sample(1, 0, line(100))],
            missing: [sample(2, 1.0 / 30, nil)],
            after: sample(3, 2.0 / 30, line(108)))
        XCTAssertTrue(result.accepted, result.rejection ?? "")
        XCTAssertEqual(try XCTUnwrap(result.measurements["gapSeconds"]), 2.0 / 30, accuracy: 1e-9)
        XCTAssertEqual(result.measurements["missingFrameCount"], 1)
        XCTAssertNotNil(result.measurements["midpointDisplacement"])
        XCTAssertNotNil(result.measurements["orientationChangeRadians"])
    }

    func testLongAbsenceIsNotABridgeDropout() {
        let result = DebugBridgeDropout.assess(
            prior: [sample(1, 0, line(100))],
            missing: [sample(2, 1.0 / 30, nil)],
            after: sample(90, 3.0, line(100)))
        XCTAssertFalse(result.accepted)
        XCTAssertEqual(result.rejection, "gap_too_long")
    }

    func testBoundaryWithoutSuccessOnEitherSideIsRejected() {
        XCTAssertEqual(DebugBridgeDropout.assess(
            prior: [], missing: [sample(2, 0.03, nil)], after: sample(3, 0.06, line(100))).rejection,
            "missing_success_on_one_side")
        XCTAssertEqual(DebugBridgeDropout.assess(
            prior: [sample(1, 0, line(100))], missing: [sample(2, 0.03, nil)],
            after: sample(3, 0.06, nil)).rejection, "missing_success_on_one_side")
        XCTAssertEqual(DebugBridgeDropout.assess(
            prior: [sample(1, 0, line(100))], missing: [],
            after: sample(3, 0.06, line(100))).rejection, "missing_success_on_one_side")
    }

    func testImplausibleSpeedLengthAndOrientationAreDiscontinuities() {
        let jump = DebugBridgeDropout.assess(
            prior: [sample(1, 0, line(100))], missing: [sample(2, 0.03, nil)],
            after: sample(3, 0.06, line(900)))
        XCTAssertEqual(jump.rejection, "discontinuous_speed")
        let shorter = DebugBridgeDropout.assess(
            prior: [sample(1, 0, line(100, length: 100))], missing: [sample(2, 0.03, nil)],
            after: sample(3, 0.06, line(100, length: 20)))
        XCTAssertEqual(shorter.rejection, "discontinuous_length_change")
        let turned = DebugBridgeDropout.assess(
            prior: [sample(1, 0, line(100))], missing: [sample(2, 0.03, nil)],
            after: sample(3, 0.06, line(100, vertical: true)))
        XCTAssertEqual(turned.rejection, "discontinuous_orientation_change")
    }

    func testEndpointOrderDoesNotMatter() {
        let result = DebugBridgeDropout.assess(
            prior: [sample(1, 0, [100, 100, 200, 100])], missing: [sample(2, 0.03, nil)],
            after: sample(3, 0.06, [205, 100, 105, 100]))
        XCTAssertTrue(result.accepted, result.rejection ?? "")
    }

    func testPriorVelocityRejectsAReappearanceOnTheWrongSideOfTheMotion() {
        // Moving right at ~1000 px/s, then back at x=100 after 0.2 s: plausible
        // by speed alone, but far from where steady motion would put it.
        let prior = [sample(1, -0.033, line(66)), sample(2, 0, line(100))]
        let wrongWay = DebugBridgeDropout.assess(
            prior: prior, missing: [sample(3, 0.1, nil)], after: sample(4, 0.2, line(-200)))
        XCTAssertEqual(wrongWay.rejection, "discontinuous_prediction_residual")
        XCTAssertNotNil(wrongWay.measurements["predictionResidual"])
        let onTrack = DebugBridgeDropout.assess(
            prior: prior, missing: [sample(3, 0.1, nil)], after: sample(4, 0.2, line(300)))
        XCTAssertTrue(onTrack.accepted, onTrack.rejection ?? "")
    }

    func testExpectedPositionInterpolatesBetweenTheTwoSuccesses() throws {
        let expected = try XCTUnwrap(DebugBridgeDropout.predictedEndpoints(
            before: sample(1, 0, line(100)), after: sample(3, 0.2, line(200)), at: 0.1))
        XCTAssertEqual(expected, [150, 100, 250, 100])
    }

    // MARK: Tracker

    func testTrackerNeverStartsOrClosesACandidateOutsideTheSuccessWindow() {
        var tracker = DebugBridgeTracker()
        for id in 0..<3 {
            XCTAssertEqual(tracker.observe(sample(UInt64(id), Double(id) * 0.03, nil)), .none)
        }
        XCTAssertNil(tracker.firstSuccess)
        _ = tracker.observe(sample(3, 0.09, line(100)))
        XCTAssertEqual(tracker.firstSuccess?.frameID, 3)
        guard case .dropoutStarted(let run) = tracker.observe(sample(4, 0.12, nil)) else {
            return XCTFail("expected a dropout to start after a success")
        }
        XCTAssertEqual(run.prior.map(\.frameID), [3])
        XCTAssertEqual(tracker.observe(sample(5, 0.15, nil)), .none)
        guard case .recovered(let closed, let after) = tracker.observe(sample(6, 0.18, line(104))) else {
            return XCTFail("expected the dropout to close on the next success")
        }
        XCTAssertEqual(closed.missing.map(\.frameID), [4, 5])
        XCTAssertEqual(after.frameID, 6)
        _ = tracker.observe(sample(7, 0.21, nil))
        _ = tracker.observe(sample(8, 0.24, nil))
        // The recording ends in absence: it is never closed, so never a candidate.
        XCTAssertNotNil(tracker.unclosedRun)
        XCTAssertEqual(tracker.lastSuccess?.frameID, 6)
    }

    // MARK: Selection from metadata

    private func temporaryDirectory() throws -> URL {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("phonesaber-bridge-tests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        addTeardownBlock { try? FileManager.default.removeItem(at: directory) }
        return directory
    }

    private func detection(_ points: [Double]?) -> [String: Any] {
        guard let points else { return ["detected": false, "predicted": false] }
        return ["detected": true, "predicted": false, "x1": Int(points[0]), "y1": Int(points[1]),
                "x2": Int(points[2]), "y2": Int(points[3])]
    }

    private func rules(_ count: Int) -> [[String: Any]] {
        (0..<count).map { ["name": "rule\($0)", "result": "FAIL", "value": 0.2 + Double($0) / 100,
                           "comparison": ">=", "threshold": 0.5] }
    }

    private func diagnostics(candidates: Int, eligible: Int, rules ruleCount: Int = 0) -> [String: Any] {
        var result: [String: Any] = ["totalCandidateCount": candidates, "eligibleCandidateCount": eligible]
        if candidates > 0 {
            result["topCandidates"] = (0..<min(3, candidates)).map { index -> [String: Any] in
                ["index": index, "sourceType": "color-component", "eligible": false,
                 "finalScore": 10.0 - Double(index), "rejectionReasons": ["rule0"],
                 "eligibilityRules": rules(ruleCount), "peakValue": 210, "componentArea": 1800]
            }
        }
        return result
    }

    private func frame(_ id: Int, red: [Double]?, blue: [Double]?, heavy: Bool = false,
                       redCandidates: Int = 0, blueCandidates: Int = 0) -> [String: Any] {
        ["frameID": id, "presentationTimeSeconds": Double(id) / 30,
         "red": detection(red), "blue": detection(blue),
         "redDetectionSucceeded": red != nil, "blueDetectionSucceeded": blue != nil,
         "candidateDiagnostics": [
            "red": diagnostics(candidates: red == nil ? redCandidates : 1, eligible: red == nil ? 0 : 1,
                               rules: heavy ? 20 : 3),
            "blue": diagnostics(candidates: blue == nil ? blueCandidates : 1, eligible: blue == nil ? 0 : 1,
                                rules: heavy ? 20 : 3)],
         "forensicCaptured": false, "manualCaptured": false]
    }

    private func event(_ id: Int, color: String, start: Int, gap: Int = 1,
                       annotated: Bool = true) -> [String: Any] {
        let dropout = start + 1
        let after = start + 1 + gap
        func name(_ role: String, _ frame: Int) -> String { "bridge_event_\(id)_\(role)_\(frame).png" }
        var dropoutImage: [String: Any] = ["role": "dropout", "frameID": dropout,
            "timestamp": Double(dropout) / 30, "fileName": name("dropout", dropout)]
        if annotated { dropoutImage["annotatedFileName"] = "bridge_event_\(id)_dropout_annotated_\(dropout).png" }
        return ["eventID": id, "color": color, "beforeFrameID": start, "dropoutFrameID": dropout,
                "afterFrameID": after, "beforeTimestamp": Double(start) / 30,
                "dropoutTimestamp": Double(dropout) / 30, "afterTimestamp": Double(after) / 30,
                "missingFrameCount": gap, "gapSeconds": Double(gap + 1) / 30,
                "assessment": ["accepted": true, "measurements": ["gapSeconds": Double(gap + 1) / 30],
                               "thresholds": DebugBridgeThresholds.dictionary],
                "images": [
                    ["role": "before_success", "frameID": start, "timestamp": Double(start) / 30,
                     "fileName": name("before_success", start)],
                    dropoutImage,
                    ["role": "after_success", "frameID": after, "timestamp": Double(after) / 30,
                     "fileName": name("after_success", after)]],
                "annotation": ["groundTruth": false, "expectedEndpoints": [100, 100, 200, 100]]]
    }

    private func writeImages(_ events: [[String: Any]], to directory: URL,
                             skip: Set<String> = []) throws {
        for event in events {
            for image in event["images"] as? [[String: Any]] ?? [] {
                for key in ["fileName", "annotatedFileName"] {
                    if let name = image[key] as? String, !skip.contains(name) {
                        try Data("png".utf8).write(to: directory.appendingPathComponent(name))
                    }
                }
            }
        }
    }

    private func document(frames: [[String: Any]], events: [[String: Any]],
                          active: [String] = ["red", "blue"],
                          windows: [String: Any]? = nil) throws -> Data {
        var document: [String: Any] = ["sessionID": "bridge_session", "frames": frames,
            "activeColors": active, "bridgeDropoutEvents": events,
            "bridgeDropoutSummary": ["observedDropouts": events.count, "accepted": events.count,
                                     "rejected": [String: Int](), "unclosedAtStop": 0]]
        document["diagnosticWindows"] = windows ?? Dictionary(uniqueKeysWithValues: active.map {
            ($0, ["firstSuccessFrameID": 0, "lastSuccessFrameID": 100,
                  "firstSuccessTime": 0.0, "lastSuccessTime": 100.0] as [String: Any])
        })
        return try JSONSerialization.data(withJSONObject: document)
    }

    private func bridgeFrames(missingColor: String = "red", id: Int = 10,
                              heavy: Bool = false) -> [[String: Any]] {
        (id - 2...id + 4).map { index in
            let missing = index == id + 1
            return frame(index,
                red: missingColor == "red" && missing ? nil : line(100 + Double(index)),
                blue: missingColor == "blue" && missing ? nil : line(100 + Double(index), 300),
                heavy: heavy, redCandidates: 3, blueCandidates: 3)
        }
    }

    func testBridgeEventIsSelectedAsOneTemporalUnitWithOriginalsFirst() throws {
        let directory = try temporaryDirectory()
        let events = [event(1, color: "red", start: 10)]
        try writeImages(events, to: directory)
        let selection = try DebugRecordingTriageBuilder.selectImages(
            metadataData: document(frames: bridgeFrames(), events: events),
            forensicDirectoryURL: directory).selection
        XCTAssertEqual(selection.images.compactMap { $0.bridge?.role },
                       ["before_success", "dropout", "annotated_dropout", "after_success"])
        XCTAssertEqual(Set(selection.images.compactMap { $0.bridge?.eventID }), [1])
        XCTAssertEqual(selection.images.map(\.frameID), [10, 11, 11, 12])
        XCTAssertEqual(selection.images.filter { $0.bridge?.auxiliary == true }.count, 1)
        XCTAssertEqual(selection.images[2].bridge?.derivedFromFileName,
                       "bridge_event_1_dropout_11.png")
    }

    func testMissingAnnotatedImageStillKeepsTheThreeOriginals() throws {
        let directory = try temporaryDirectory()
        let events = [event(1, color: "red", start: 10)]
        try writeImages(events, to: directory, skip: ["bridge_event_1_dropout_annotated_11.png"])
        let selection = try DebugRecordingTriageBuilder.selectImages(
            metadataData: document(frames: bridgeFrames(), events: events),
            forensicDirectoryURL: directory).selection
        XCTAssertEqual(selection.images.compactMap { $0.bridge?.role },
                       ["before_success", "dropout", "after_success"])
    }

    func testEventMissingAnyOriginalOrAfterSuccessIsNeverPartiallySelected() throws {
        let directory = try temporaryDirectory()
        var events = [event(1, color: "red", start: 10)]
        try writeImages(events, to: directory, skip: ["bridge_event_1_after_success_12.png"])
        XCTAssertTrue(try DebugRecordingTriageBuilder.selectImages(
            metadataData: document(frames: bridgeFrames(), events: events),
            forensicDirectoryURL: directory).selection.images.isEmpty)
        // No after-success image entry at all (e.g. the recording ended first).
        events = [event(2, color: "red", start: 10)]
        var images = events[0]["images"] as! [[String: Any]]
        images.removeAll { $0["role"] as? String == "after_success" }
        events[0]["images"] = images
        try writeImages(events, to: directory)
        XCTAssertTrue(try DebugRecordingTriageBuilder.selectImages(
            metadataData: document(frames: bridgeFrames(), events: events),
            forensicDirectoryURL: directory).selection.images.isEmpty)
    }

    func testAbsenceBeforeTheFirstAndAfterTheLastSuccessIsNotSelected() throws {
        let directory = try temporaryDirectory()
        // candidate=0 on both ends; a forensic capture on those frames must not
        // turn them into candidate_zero / eligible_zero evidence.
        var frames: [[String: Any]] = []
        for index in 0..<12 {
            let detected = (4...8).contains(index)
            var item = frame(index, red: detected ? line(100) : nil, blue: nil)
            if index == 1 || index == 10 {
                item["forensicCaptured"] = true
                item["forensicFileName"] = "frame_\(index).png"
                try Data("png".utf8).write(to: directory.appendingPathComponent("frame_\(index).png"))
            }
            frames.append(item)
        }
        let windows: [String: Any] = ["red": ["firstSuccessFrameID": 4, "lastSuccessFrameID": 8,
            "firstSuccessTime": 4.0 / 30, "lastSuccessTime": 8.0 / 30]]
        let selection = try DebugRecordingTriageBuilder.selectImages(
            metadataData: document(frames: frames, events: [], active: ["red"], windows: windows),
            forensicDirectoryURL: directory).selection
        for image in selection.images {
            XCTAssertFalse(image.failureType.contains("candidate_zero"))
            XCTAssertFalse(image.failureType.contains("eligible_zero"))
            XCTAssertFalse(image.failureType == "dropout")
            XCTAssertFalse(image.reasons.contains("candidate=0"))
        }
    }

    func testOnlyActiveColorEventsAreSelected() throws {
        let directory = try temporaryDirectory()
        let events = [event(1, color: "red", start: 10), event(2, color: "blue", start: 10)]
        try writeImages(events, to: directory)
        let frames = bridgeFrames()
        func colors(_ active: [String]) throws -> Set<String> {
            Set(try DebugRecordingTriageBuilder.selectImages(
                metadataData: document(frames: frames, events: events, active: active),
                forensicDirectoryURL: directory).selection.images.map(\.color))
        }
        XCTAssertEqual(try colors(["red"]), ["red"])
        XCTAssertEqual(try colors(["blue"]), ["blue"])
        XCTAssertEqual(try colors(["red", "blue"]), ["red", "blue"])
    }

    func testAtMostTwoEventsAreSelectedPreferringTheLongerLoss() throws {
        let directory = try temporaryDirectory()
        let events = [event(1, color: "red", start: 10, gap: 1),
                      event(2, color: "red", start: 20, gap: 4),
                      event(3, color: "red", start: 40, gap: 2)]
        try writeImages(events, to: directory)
        let frames = (0..<60).map { frame($0, red: line(100), blue: line(100, 300)) }
        let selection = try DebugRecordingTriageBuilder.selectImages(
            metadataData: document(frames: frames, events: events),
            forensicDirectoryURL: directory).selection
        XCTAssertEqual(Set(selection.images.compactMap { $0.bridge?.eventID }), [2, 3])
        XCTAssertLessThanOrEqual(selection.images.count, DebugRecordingTriageLimits.defaultImageCount)
    }

    func testRedModeDoesNotCountBlueAbsenceInSummaries() throws {
        let directory = try temporaryDirectory()
        let frames = (0..<8).map { frame($0, red: line(100), blue: nil, blueCandidates: 0) }
        let bundle = try DebugRecordingTriageBuilder.build(
            metadataData: document(frames: frames, events: [], active: ["red"]),
            metadataURL: directory.appendingPathComponent("metadata.json"),
            forensicDirectoryURL: directory)
        let summary = try XCTUnwrap(JSONSerialization.jsonObject(with:
            Data(contentsOf: bundle.appendingPathComponent("summary.json"))) as? [String: Any])
        let detection = try XCTUnwrap(summary["redBlueDetectionSummary"] as? [String: [String: Any]])
        XCTAssertEqual(detection["blue"]?["missedFrames"] as? Int, 0)
        XCTAssertEqual(detection["blue"]?["candidateZeroFrames"] as? Int, 0)
        XCTAssertEqual(detection["blue"]?["excludedFromDiagnosis"] as? Bool, true)
        XCTAssertNil(detection["red"]?["excludedFromDiagnosis"])
        XCTAssertEqual(summary["activeColors"] as? [String], ["red"])
        let dropout = try XCTUnwrap(summary["dropoutSummary"] as? [String: [String: Any]])
        XCTAssertEqual(dropout["blue"]?["falseFrames"] as? Int, 0)
        let incidents = summary["incidents"] as? [[String: Any]] ?? []
        XCTAssertTrue(incidents.allSatisfy { $0["color"] as? String != "blue" })
    }

    // MARK: Compact context

    func testCompactContextsStayUnderTheConsumerLimitEvenWithHeavyTraces() throws {
        let directory = try temporaryDirectory()
        let events = [event(1, color: "red", start: 10)]
        try writeImages(events, to: directory)
        let bundle = try DebugRecordingTriageBuilder.build(
            metadataData: document(frames: bridgeFrames(heavy: true), events: events),
            metadataURL: directory.appendingPathComponent("metadata.json"),
            forensicDirectoryURL: directory)
        let frameDirectory = bundle.appendingPathComponent("frames")
        let files = try FileManager.default.contentsOfDirectory(at: frameDirectory,
                                                                 includingPropertiesForKeys: nil)
        XCTAssertEqual(files.count, 4)
        for file in files {
            let data = try Data(contentsOf: file)
            XCTAssertLessThan(data.count, 32 * 1024, file.lastPathComponent)
            XCTAssertFalse(String(decoding: data, as: UTF8.self).contains("\n  "),
                           "contexts are written compactly")
        }
        // Everything the diagnosis needs survives on the selected frame.
        let dropoutContext = try XCTUnwrap(files.first { $0.lastPathComponent.hasPrefix("frame_11_2") })
        let context = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: dropoutContext))
            as? [String: Any])
        let mapping = try XCTUnwrap(context["imageMapping"] as? [String: Any])
        XCTAssertEqual(mapping["eventID"] as? String, "bridge_1")
        XCTAssertEqual(mapping["eventRole"] as? String, "dropout")
        let framesList = try XCTUnwrap(context["frames"] as? [[String: Any]])
        let selected = try XCTUnwrap(framesList.first { $0["frameID"] as? Int == 11 })
        let red = try XCTUnwrap(selected["red"] as? [String: Any])
        let trace = try XCTUnwrap(red["candidateDecisionTrace"] as? [[String: Any]])
        XCTAssertEqual(trace.count, 3)
        let firstRules = try XCTUnwrap(trace[0]["rules"] as? [[String: Any]])
        XCTAssertEqual(firstRules.count, 20)
        XCTAssertNotNil(firstRules[0]["value"])
        XCTAssertNotNil(firstRules[0]["threshold"])
        XCTAssertEqual(red["failureStage"] as? String, "eligibility")
        // Neighbours keep only the leading candidate's trace.
        let neighbour = try XCTUnwrap(framesList.first { $0["frameID"] as? Int == 12 })
        XCTAssertLessThanOrEqual(((neighbour["red"] as? [String: Any])?["candidateDecisionTrace"]
            as? [[String: Any]] ?? []).count, 1)
        // The color outside the diagnosis (none here) would be empty; both are active.
        let bridge = try XCTUnwrap(context["bridgeEvent"] as? [String: Any])
        XCTAssertEqual(bridge["beforeFrameID"] as? Int, 10)
        XCTAssertEqual(bridge["afterFrameID"] as? Int, 12)
    }

    func testInactiveColorIsEmptyInsideContexts() throws {
        let directory = try temporaryDirectory()
        let events = [event(1, color: "red", start: 10)]
        try writeImages(events, to: directory)
        let bundle = try DebugRecordingTriageBuilder.build(
            metadataData: document(frames: bridgeFrames(heavy: true), events: events, active: ["red"]),
            metadataURL: directory.appendingPathComponent("metadata.json"),
            forensicDirectoryURL: directory)
        let files = try FileManager.default.contentsOfDirectory(
            at: bundle.appendingPathComponent("frames"), includingPropertiesForKeys: nil)
        for file in files {
            let context = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: file))
                as? [String: Any])
            XCTAssertEqual(context["activeColors"] as? [String], ["red"])
            for frame in context["frames"] as? [[String: Any]] ?? [] {
                XCTAssertTrue((frame["blue"] as? [String: Any])?.isEmpty == true)
            }
        }
    }
}

// MARK: - Candidate geometry observability and event priority

extension DebugBridgeDropoutTests {
    private func saber(x: Int, y: Int = 30, eligible: Bool = true, score: Double = 80,
                       length: Int = 60) throws -> SaberCandidate {
        let points = (0...length).flatMap { a in (0...4).map { PixelPoint(x: x + a, y: y + $0) } }
        var candidate = try XCTUnwrap(saberCandidate(from: points, width: 640, height: 480,
                                                    collectEndpointDiagnostics: true))
        candidate.isEmitterEligible = eligible
        candidate.score = score
        return candidate
    }

    private func geometryEntry(_ id: Int, red: [SaberCandidate], blue: [SaberCandidate] = []) -> [String: Any] {
        ["frameID": id, "red": DebugCandidateGeometrySet(red).dictionary,
         "blue": DebugCandidateGeometrySet(blue).dictionary]
    }

    /// A frame dictionary whose recorded eligible count matches its candidate list.
    private func geometryFrame(_ id: Int, red: [SaberCandidate], blue: [SaberCandidate] = []) -> [String: Any] {
        func diag(_ list: [SaberCandidate]) -> [String: Any] {
            ["totalCandidateCount": list.count, "eligibleCandidateCount": list.filter(\.isEmitterEligible).count]
        }
        return ["frameID": id, "presentationTimeSeconds": Double(id) / 30,
                "red": detection([100, 100, 200, 100]), "blue": detection([100, 300, 200, 300]),
                "redDetectionSucceeded": true, "blueDetectionSucceeded": true,
                "candidateDiagnostics": ["red": diag(red), "blue": diag(blue)],
                "forensicCaptured": false, "manualCaptured": false]
    }

    private func addGeometry(_ data: Data, _ entries: [[String: Any]]) throws -> Data {
        var document = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        document["candidateGeometry"] = entries
        return try JSONSerialization.data(withJSONObject: document)
    }

    private func contexts(_ bundle: URL) throws -> [[String: Any]] {
        try FileManager.default.contentsOfDirectory(at: bundle.appendingPathComponent("frames"),
                                                    includingPropertiesForKeys: nil).sorted {
            $0.lastPathComponent < $1.lastPathComponent
        }.map { try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: $0)) as? [String: Any]) }
    }

    private func redGeometry(_ context: [String: Any], frame id: Int) throws -> [String: Any] {
        let frames = try XCTUnwrap(context["frames"] as? [[String: Any]])
        let frame = try XCTUnwrap(frames.first { $0["frameID"] as? Int == id })
        return try XCTUnwrap((frame["red"] as? [String: Any])?["candidateGeometry"] as? [String: Any])
    }

    /// Frames 8...13 (event 1 drops at frame 11), with each frame's candidate list.
    private func geometryBundle(lists: [Int: [SaberCandidate]], blue: [Int: [SaberCandidate]] = [:],
                                active: [String] = ["red", "blue"],
                                heavyRules: Bool = false) throws -> URL {
        let directory = try temporaryDirectory()
        let events = [event(1, color: "red", start: 10)]
        try writeImages(events, to: directory)
        let frames = (8...14).map { geometryFrame($0, red: lists[$0] ?? [], blue: blue[$0] ?? []) }
        let entries = (8...14).map { geometryEntry($0, red: lists[$0] ?? [], blue: blue[$0] ?? []) }
        let data = try addGeometry(document(frames: frames, events: events, active: active), entries)
        return try DebugRecordingTriageBuilder.build(
            metadataData: data, metadataURL: directory.appendingPathComponent("metadata.json"),
            forensicDirectoryURL: directory)
    }

    func testGeometryRecordsEveryEligibleCandidateWithRanksAndStatesOmission() throws {
        var candidates = try (0..<15).map { try saber(x: 10 + $0 * 30, score: 90 - Double($0)) }
        candidates.insert(try saber(x: 5, eligible: false, score: 99), at: 0)
        candidates.append(contentsOf: try (0..<8).map { try saber(x: 400 + $0 * 20, eligible: false, score: 10) })
        let set = DebugCandidateGeometrySet(candidates)
        XCTAssertEqual(set.totalCandidateCount, 24)
        XCTAssertEqual(set.eligibleCandidateCount, 15)
        let eligible = set.candidates.filter(\.eligible)
        XCTAssertEqual(eligible.count, DebugCandidateGeometrySet.eligibleLimit)
        XCTAssertEqual(eligible.map { $0.eligibleRank }, (1...12).map { Optional($0) })
        XCTAssertEqual(set.candidates.filter { !$0.eligible }.count, DebugCandidateGeometrySet.ineligibleLimit)
        // The ineligible leader keeps its list position; rank counts eligible ones only.
        XCTAssertEqual(set.candidates[0].listIndex, 0)
        XCTAssertFalse(set.candidates[0].eligible)
        XCTAssertNil(set.candidates[0].eligibleRank)
        let first = try XCTUnwrap(eligible.first)
        XCTAssertEqual(first.bbox.count, 4)
        XCTAssertEqual(first.centroid.count, 2)
        XCTAssertEqual(first.rawPCAEndpoints.count, 4)
        XCTAssertEqual(first.finalOutputEndpoints.count, 4)
        XCTAssertEqual(first.scoreBreakdown.count, 18)
        XCTAssertGreaterThan(first.componentArea, 0)
        XCTAssertFalse(first.sourceType.isEmpty)
        XCTAssertFalse(set.candidates.first { !$0.eligible }?.rejectionReasons.isEmpty ?? true)
    }

    func testContextsReconcileEligibleCountsAndFlagTruncationExplicitly() throws {
        // Fifteen eligible candidates on a quiet frame: the selected frame keeps the
        // first twelve and says three were left out.
        let many = try (0..<15).map { try saber(x: 10 + $0 * 30, score: 90 - Double($0)) }
        let bundle = try geometryBundle(lists: Dictionary(uniqueKeysWithValues: (8...14).map { ($0, many) }))
        let dropoutContext = try XCTUnwrap(try contexts(bundle).first {
            ($0["bridgeEvent"] as? [String: Any])?["role"] as? String == "dropout" })
        let selected = try redGeometry(dropoutContext, frame: 11)
        XCTAssertEqual(selected["eligibleCandidateCount"] as? Int, 15)
        XCTAssertEqual(selected["savedEligibleCount"] as? Int, DebugCandidateGeometrySet.eligibleLimit)
        XCTAssertEqual(selected["eligibleOmittedCount"] as? Int, 3)
        XCTAssertEqual(selected["candidatesTruncated"] as? Bool, true)
        XCTAssertEqual((selected["savedEligibleCount"] as? Int ?? 0) + (selected["eligibleOmittedCount"] as? Int ?? 0),
                       selected["eligibleCandidateCount"] as? Int)
        // The recorded count and the context's own diagnostics agree.
        let frames = try XCTUnwrap(dropoutContext["frames"] as? [[String: Any]])
        let red = try XCTUnwrap(frames.first { $0["frameID"] as? Int == 11 }?["red"] as? [String: Any])
        XCTAssertEqual(red["eligibleCandidateCount"] as? Int, selected["eligibleCandidateCount"] as? Int)
        // Any neighbour geometry that survives is limited and says so; when the context had
        // to shed it, the compaction record says that instead.
        if let neighbour = try? redGeometry(dropoutContext, frame: 12) {
            XCTAssertLessThanOrEqual(neighbour["savedEligibleCount"] as? Int ?? 99, 3)
            XCTAssertEqual(neighbour["candidatesTruncated"] as? Bool, true)
            XCTAssertEqual((neighbour["savedEligibleCount"] as? Int ?? 0) + (neighbour["eligibleOmittedCount"] as? Int ?? 0), 15)
        } else {
            XCTAssertTrue((dropoutContext["compaction"] as? [String] ?? []).contains {
                $0.hasPrefix("neighbourCandidateGeometry") })
        }
        // Ineligible candidates are accounted for the same way.
        let withIneligible = try (0..<8).map { try saber(x: 450 + $0 * 20, eligible: false, score: 5) }
        let second = try geometryBundle(lists: Dictionary(uniqueKeysWithValues: (8...14).map {
            ($0, [try saber(x: 10)] + withIneligible) }))
        let context = try XCTUnwrap(try contexts(second).first {
            ($0["bridgeEvent"] as? [String: Any])?["role"] as? String == "dropout" })
        let ineligible = try redGeometry(context, frame: 11)
        XCTAssertEqual(ineligible["savedIneligibleCount"] as? Int, DebugCandidateGeometrySet.ineligibleLimit)
        XCTAssertEqual(ineligible["ineligibleOmittedCount"] as? Int, 2)
        XCTAssertEqual(ineligible["totalCandidateCount"] as? Int, 9)
        // Nothing is omitted when everything fits.
        let small = try geometryBundle(lists: Dictionary(uniqueKeysWithValues: (8...14).map {
            ($0, [try saber(x: 10), try saber(x: 200)]) }))
        let smallContext = try XCTUnwrap(try contexts(small).first {
            ($0["bridgeEvent"] as? [String: Any])?["role"] as? String == "dropout" })
        let whole = try redGeometry(smallContext, frame: 11)
        XCTAssertEqual(whole["candidatesTruncated"] as? Bool, false)
        XCTAssertEqual(whole["eligibleOmittedCount"] as? Int, 0)
        XCTAssertEqual((whole["candidates"] as? [[String: Any]])?.count, 2)
    }

    func testGeometryIdentityIsCheckableWhenCandidateOrderChanges() throws {
        let near = try saber(x: 10), far = try saber(x: 400, score: 79)
        var lists: [Int: [SaberCandidate]] = [:]
        for id in 8...11 { lists[id] = [near, far] }          // the near candidate wins
        for id in 12...14 { lists[id] = [far, near] }         // the far one now wins; order swapped
        let bundle = try geometryBundle(lists: lists)
        let context = try XCTUnwrap(try contexts(bundle).first {
            ($0["bridgeEvent"] as? [String: Any])?["role"] as? String == "after_success" })
        let geometry = try redGeometry(context, frame: 12)
        let candidates = try XCTUnwrap(geometry["candidates"] as? [[String: Any]])
        let winner = try XCTUnwrap(candidates.first { $0["eligibleRank"] as? Int == 1 })
        let other = try XCTUnwrap(candidates.first { $0["eligibleRank"] as? Int == 2 })
        // List index 0 is now the far candidate, yet geometry still finds the old winner.
        XCTAssertEqual(winner["listIndex"] as? Int, 0)
        let winnerMatch = try XCTUnwrap(winner["matchToPreviousWinner"] as? [String: Any])
        let otherMatch = try XCTUnwrap(other["matchToPreviousWinner"] as? [String: Any])
        XCTAssertGreaterThan(try XCTUnwrap(winnerMatch["centroidDistance"] as? Double), 300)
        XCTAssertEqual(winnerMatch["bboxIoU"] as? Double, 0)
        XCTAssertEqual(otherMatch["centroidDistance"] as? Double, 0)
        XCTAssertEqual(otherMatch["bboxIoU"] as? Double, 1)
        XCTAssertEqual(otherMatch["areaRatio"] as? Double, 1)
        XCTAssertEqual(otherMatch["spanRatio"] as? Double, 1)
        XCTAssertEqual(otherMatch["orientationDifference"] as? Double, 0)
        let previous = try XCTUnwrap(geometry["previousWinner"] as? [String: Any])
        XCTAssertEqual(previous["frameID"] as? Int, 11)
        XCTAssertEqual(geometry["previousFrameGeometryAvailable"] as? Bool, true)
    }

    func testCompactContextWithFullGeometryOnBothColorsStaysWithinTheLimit() throws {
        let many = try (0..<12).map { try saber(x: 10 + $0 * 30, score: 90 - Double($0)) }
            + (try (0..<6).map { try saber(x: 450 + $0 * 20, eligible: false, score: 5) })
        var lists: [Int: [SaberCandidate]] = [:]
        for id in 8...14 { lists[id] = many }
        let bundle = try geometryBundle(lists: lists, blue: lists)
        for context in try contexts(bundle) {
            let data = try JSONSerialization.data(withJSONObject: context)
            XCTAssertLessThan(data.count, 32 * 1024)
            // When detail had to go, the context says what was dropped.
            let compaction = context["compaction"] as? [String] ?? []
            XCTAssertLessThan(data.count, 24 * 1024 + 2048, "guard keeps contexts near the budget")
            XCTAssertTrue(!compaction.isEmpty || data.count <= 24 * 1024,
                          "an oversized context is only acceptable when its reductions are recorded")
            // The selected frame keeps all its eligible geometry for both colors.
            if context["bridgeEvent"] != nil, (context["bridgeEvent"] as? [String: Any])?["auxiliary"] as? Bool != true,
               let id = context["selectedFrameID"] as? Int {
                let selected = try redGeometry(context, frame: id)
                // Whatever was shed is stated, and the counts still reconcile.
                XCTAssertEqual((selected["savedEligibleCount"] as? Int ?? 0) + (selected["eligibleOmittedCount"] as? Int ?? 0), 12)
                XCTAssertEqual(selected["eligibleCandidateCount"] as? Int, 12)
                XCTAssertEqual(selected["candidatesTruncated"] as? Bool,
                               (selected["eligibleOmittedCount"] as? Int ?? 0) + (selected["ineligibleOmittedCount"] as? Int ?? 0) > 0)
            }
        }
    }

    // MARK: Event priority between bridge and tracking evidence

    private func trackingMotionEvent(peak: Int, score: Double) -> [String: Any] {
        let roles = ["before", "before", "before", "before", "onset", "peak", "after", "after", "after", "after", "recovery"]
        let start = peak - 5
        let images = roles.enumerated().map { offset, role -> [String: Any] in
            ["role": role, "frameID": start + offset, "fileName": "motion_\(start + offset).png"]
        }
        return ["eventIndex": DebugRecordingTriageLimits.trackingEventIndex, "color": "red",
                "peakScore": score, "signals": [], "images": images]
    }

    private func prioritySelection(trackingScore: Double, bridgeEvents: Int,
                                   overlapping: [[String: Any]] = []) throws
        -> (selection: DebugRecordingTriageSelection, summary: [String: Any]) {
        let directory = try temporaryDirectory()
        var events: [[String: Any]] = overlapping
        for index in 0..<bridgeEvents {
            events.append(event(overlapping.count + index + 1, color: "red", start: 10 + index * 10))
        }
        try writeImages(events, to: directory)
        for frame in 100...110 { try Data("png".utf8).write(to: directory.appendingPathComponent("motion_\(frame).png")) }
        let frames = (0..<130).map { frame($0, red: line(100), blue: line(100, 300)) }
        var document = try XCTUnwrap(JSONSerialization.jsonObject(with: self.document(
            frames: frames, events: events)) as? [String: Any])
        document["motionEvents"] = [trackingMotionEvent(peak: 105, score: trackingScore)]
        document["motionSummary"] = ["events": [[DebugRecordingTriageLimits.trackingEventIndex, "red", trackingScore, "retained"]]]
        let data = try JSONSerialization.data(withJSONObject: document)
        let selection = try DebugRecordingTriageBuilder.selectImages(
            metadataData: data, forensicDirectoryURL: directory).selection
        return (selection, document)
    }

    func testSignificantTrackingInstabilityKeepsAWindowBesideBridgeEvents() throws {
        let (selection, _) = try prioritySelection(trackingScore: 2.0, bridgeEvents: 2)
        let tracking = selection.images.filter { $0.eventIndex == DebugRecordingTriageLimits.trackingEventIndex }
        XCTAssertEqual(selection.images.compactMap { $0.bridge?.eventID }.reduce(into: Set<Int>()) { $0.insert($1) }.count, 1)
        XCTAssertLessThanOrEqual(selection.images.count, DebugRecordingTriageLimits.defaultImageCount)
        XCTAssertGreaterThanOrEqual(tracking.count, DebugRecordingTriageLimits.minimumTrackingWindow)
        XCTAssertEqual(selection.trackingWindow?["available"], 11)
        XCTAssertEqual(selection.trackingWindow?["selected"], tracking.count)
        let ids = tracking.map(\.frameID).sorted()
        XCTAssertEqual(ids, Array(ids[0]...(ids[0] + UInt64(ids.count - 1))), "contiguous window")
        XCTAssertTrue(tracking.contains { $0.role == "peak" })
        XCTAssertTrue(tracking.contains { $0.role == "before" } && tracking.contains { $0.role == "after" })
        XCTAssertTrue(selection.bridgePriorityEventIDs.isEmpty)
    }

    // A bridge event whose frames fall inside the tracking window (the after-success
    // is often the instability peak) must not remove tracking frames: the bridge and
    // tracking PNGs of one frame are different files.
    func testBridgeEventOverlappingTheTrackingWindowKeepsTheWholeWindowAndPeak() throws {
        let peak = 105
        for dropout in [peak - 1, peak, peak + 1] {
            for (gap, others) in [(1, 0), (2, 1)] {
                let label = "dropout \(dropout) gap \(gap) others \(others)"
                let overlap = event(1, color: "red", start: dropout - 1, gap: gap)
                let (selection, _) = try prioritySelection(trackingScore: 2.0, bridgeEvents: others,
                                                           overlapping: [overlap])
                let bridge = selection.images.filter { $0.bridge != nil }
                XCTAssertEqual(Set(bridge.compactMap { $0.bridge?.eventID }), [1], label)
                XCTAssertEqual(bridge.count, 4, label)
                let tracking = selection.images.filter {
                    $0.eventIndex == DebugRecordingTriageLimits.trackingEventIndex
                }
                XCTAssertEqual(selection.images.count, DebugRecordingTriageLimits.defaultImageCount, label)
                XCTAssertEqual(tracking.count, DebugRecordingTriageLimits.defaultImageCount - bridge.count, label)
                XCTAssertEqual(selection.trackingWindow?["selected"], tracking.count, label)
                XCTAssertEqual(selection.trackingWindow?["available"], 11, label)
                let ids = tracking.map(\.frameID).sorted()
                XCTAssertEqual(ids, Array(ids[0]...(ids[0] + UInt64(ids.count - 1))), "contiguous \(label)")
                XCTAssertTrue(tracking.contains { $0.role == "peak" && $0.frameID == UInt64(peak) }, label)
                XCTAssertTrue(ids.first! < UInt64(peak) && UInt64(peak) < ids.last!, label)
                // The overlap is real: some tracking frames share a frame with the bridge event.
                XCTAssertFalse(Set(ids).isDisjoint(with: bridge.map(\.frameID)), label)
                XCTAssertEqual(Set(selection.images.map(\.fileName)).count, selection.images.count, label)
                XCTAssertTrue(selection.bridgePriorityEventIDs.isEmpty, label)
            }
        }
    }

    // Regression for phonesaber_20261002_005850_489 (peak 2538, score 74.52) and
    // phonesaber_20261002_013205_087 (peak 256, score 90.30): the switch-out frame
    // is the onset just before the return-jump peak and must stay in the window.
    func testRecordedCandidateSwitchOnsetStaysInTheWindowBesideABridgeEvent() throws {
        for score in [74.52087241706056, 90.29987096046787] {
            let (selection, _) = try prioritySelection(trackingScore: score, bridgeEvents: 2)
            let tracking = selection.images.filter { $0.eventIndex == DebugRecordingTriageLimits.trackingEventIndex }
            XCTAssertTrue(tracking.contains { $0.role == "onset" && $0.frameID == 104 }, "score \(score)")
            XCTAssertTrue(tracking.contains { $0.role == "peak" && $0.frameID == 105 }, "score \(score)")
            XCTAssertTrue(selection.bridgePriorityEventIDs.isEmpty)
        }
    }

    func testQuietTrackingEventYieldsToBridgeEventsAndIsStillWholeWhenAlone() throws {
        let (withBridge, _) = try prioritySelection(trackingScore: 0, bridgeEvents: 2)
        XCTAssertEqual(withBridge.images.compactMap { $0.bridge?.eventID }.reduce(into: Set<Int>()) { $0.insert($1) }.count, 2)
        XCTAssertTrue(withBridge.images.allSatisfy { $0.eventIndex == nil })
        XCTAssertEqual(withBridge.bridgePriorityEventIDs, [DebugRecordingTriageLimits.trackingEventIndex])
        let (alone, _) = try prioritySelection(trackingScore: 0, bridgeEvents: 0)
        XCTAssertEqual(alone.images.filter { $0.eventIndex == DebugRecordingTriageLimits.trackingEventIndex }.count, 11)
        XCTAssertNil(alone.trackingWindow)
        let (significantAlone, _) = try prioritySelection(trackingScore: 2.0, bridgeEvents: 0)
        XCTAssertEqual(significantAlone.images.count, 11)
    }
}
