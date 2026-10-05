import CoreMedia
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
        candidates.append(contentsOf: try (0..<14).map { try saber(x: 300 + $0 * 20, eligible: false, score: 10) })
        let set = DebugCandidateGeometrySet(candidates)
        XCTAssertEqual(set.totalCandidateCount, 30)
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

    func testTrimmedIneligibleCandidatesKeepTheOnesNearestThePreviousWinner() throws {
        // A rejected real saber (near the previous winner at x≈100) listed after far
        // rejects must survive any trimming: it is what separates CASE B causes.
        func entry(_ index: Int, x: Double) -> [String: Any] {
            ["listIndex": index, "eligible": false, "centroid": [x, 30.0]]
        }
        let ordered = [entry(0, x: 600), entry(1, x: 500), entry(2, x: 400), entry(3, x: 105)]
        let nearest = DebugRecordingTriageBuilder.nearestFirst(ordered, to: [100, 30])
        XCTAssertEqual(nearest.compactMap { $0["listIndex"] as? Int }, [3, 2, 1, 0])
        XCTAssertEqual(DebugRecordingTriageBuilder.nearestFirst(ordered, to: []).compactMap { $0["listIndex"] as? Int },
                       [0, 1, 2, 3], "no previous winner: list order")
        let tie = [entry(0, x: 90), entry(1, x: 110)]
        XCTAssertEqual(DebugRecordingTriageBuilder.nearestFirst(tie, to: [100, 30]).compactMap { $0["listIndex"] as? Int },
                       [0, 1], "equal distance keeps list order")
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
        let withIneligible = try (0..<14).map { try saber(x: 300 + $0 * 20, eligible: false, score: 5) }
        let second = try geometryBundle(lists: Dictionary(uniqueKeysWithValues: (8...14).map {
            ($0, [try saber(x: 10)] + withIneligible) }))
        let context = try XCTUnwrap(try contexts(second).first {
            ($0["bridgeEvent"] as? [String: Any])?["role"] as? String == "dropout" })
        let ineligible = try redGeometry(context, frame: 11)
        let savedIneligible = try XCTUnwrap(ineligible["savedIneligibleCount"] as? Int)
        XCTAssertLessThanOrEqual(savedIneligible, DebugCandidateGeometrySet.ineligibleLimit)
        XCTAssertEqual(savedIneligible + (ineligible["ineligibleOmittedCount"] as? Int ?? -1), 14)
        XCTAssertEqual(ineligible["totalCandidateCount"] as? Int, 15)
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

// MARK: - Emitter diagnostics and camera state in compact contexts

extension DebugBridgeDropoutTests {
    private static let redEvidence: SaberEvidence = {
        let count = 640 * 480
        return SaberEvidence(color: .red, radiance: Array(repeating: 40, count: count),
                             value: Array(repeating: 250, count: count),
                             chroma: Array(repeating: 220, count: count),
                             colorMask: Array(repeating: 1, count: count),
                             coreMask: Array(repeating: 0, count: count))
    }()

    private func emitterSaber(x: Int, y: Int = 30, eligible: Bool = true, score: Double = 80) throws -> SaberCandidate {
        let points = (0...60).flatMap { a in (0...4).map { PixelPoint(x: x + a, y: y + $0) } }
        var candidate = try XCTUnwrap(saberCandidate(from: points, width: 640, height: 480,
                                                    evidence: Self.redEvidence,
                                                    collectEndpointDiagnostics: true))
        candidate.isEmitterEligible = eligible
        candidate.score = score
        return candidate
    }

    private func emitterBundle(red: [SaberCandidate], blue: [SaberCandidate]) throws -> URL {
        let directory = try temporaryDirectory()
        let events = [event(1, color: "red", start: 10)]
        try writeImages(events, to: directory)
        let encoder = JSONEncoder()
        func colorDiagnostics(_ list: [SaberCandidate]) throws -> Any {
            try JSONSerialization.jsonObject(with: encoder.encode(DebugRecordingColorCandidates(list, pipeline: nil)))
        }
        let camera: [String: Any] = ["source": "exif+device", "iso": 320, "exposureDurationSeconds": 0.008333,
                                     "exposureBiasEV": 0, "brightnessValue": 1.5, "fNumber": 1.78,
                                     "exposureTargetBias": 0, "exposureTargetOffset": -0.125,
                                     "whiteBalanceGains": [1.9, 1, 2.1], "deviceSampleAgeSeconds": 0.1]
        let frames = try (8...14).map { id -> [String: Any] in
            ["frameID": id, "presentationTimeSeconds": Double(id) / 30,
             "red": detection([100, 100, 200, 100]), "blue": detection([100, 300, 200, 300]),
             "redDetectionSucceeded": true, "blueDetectionSucceeded": true,
             "candidateDiagnostics": ["red": try colorDiagnostics(red), "blue": try colorDiagnostics(blue)],
             "forensicCaptured": false, "manualCaptured": false, "camera": camera]
        }
        let entries = (8...14).map { id -> [String: Any] in
            ["frameID": id, "red": DebugCandidateGeometrySet(red).dictionary,
             "blue": DebugCandidateGeometrySet(blue).dictionary]
        }
        let data = try addGeometry(document(frames: frames, events: events, active: ["red", "blue"]), entries)
        return try DebugRecordingTriageBuilder.build(
            metadataData: data, metadataURL: directory.appendingPathComponent("metadata.json"),
            forensicDirectoryURL: directory)
    }

    func testWarmNoDeepRedContextsKeepAppliedEvidenceWithinPreflightLimit() throws {
        func candidate(x: Int, rejected: Bool = false) throws -> SaberCandidate {
            var value = try emitterSaber(x: x, eligible: !rejected)
            let verdict = SaberWarmNoDeepRedVerdict(deepCount: rejected ? 0 : 1,
                                                    warmCount: 8, pixelCount: 10)
            value.warmNoDeepRed = verdict
            value.endpointDiagnosticTrace?.emitter?.warmNoDeepRed = verdict
            if rejected {
                value.diagnosticRejections.append(SaberEligibilityDecision(
                    name: "warmNoDeepRed", value: verdict.warmFrac, comparison: "<", threshold: 0.30))
            }
            return value
        }
        let small = try emitterBundle(red: [candidate(x: 200, rejected: true), candidate(x: 10)], blue: [])
        let context = try XCTUnwrap(try contexts(small).first {
            ($0["bridgeEvent"] as? [String: Any])?["role"] as? String == "dropout" })
        let frames = try XCTUnwrap(context["frames"] as? [[String: Any]])
        let selected = try XCTUnwrap(frames.first { $0["frameID"] as? Int == 11 })
        let red = try XCTUnwrap(selected["red"] as? [String: Any])
        let traces = try XCTUnwrap(red["candidateDecisionTrace"] as? [[String: Any]])
        let rejected = try XCTUnwrap(traces.first {
            ($0["rejectionReasons"] as? [String])?.contains("warmNoDeepRed") == true })
        let emitter = try XCTUnwrap(rejected["emitterDiagnostics"] as? [String: Any])
        let rule = try XCTUnwrap(emitter["warmNoDeepRed"] as? [String: Any])
        XCTAssertEqual(rule["applied"] as? Bool, true)
        XCTAssertEqual(rule["deepCount"] as? Int, 0)
        XCTAssertEqual(rule["warmFrac"] as? Double, 0.8)
        XCTAssertEqual(rule["rejectionReason"] as? String, "warmNoDeepRed")
        let geometry = try redGeometry(context, frame: 11)
        let entries = try XCTUnwrap(geometry["candidates"] as? [[String: Any]])
        XCTAssertTrue(entries.contains {
            (($0["emitter"] as? [String: Any])?["warmNoDeepRed"] as? [String: Any])?["applied"] as? Bool == true })

        let many = try (0..<12).map { try candidate(x: 10 + $0 * 30) }
            + (try (0..<6).map { try candidate(x: 450 + $0 * 20, rejected: true) })
        let blue = try (0..<12).map { try emitterSaber(x: 10 + $0 * 30) }
        let large = try emitterBundle(red: many, blue: blue)
        for bundle in [small, large] {
            for context in try contexts(bundle) {
                XCTAssertLessThan(try JSONSerialization.data(withJSONObject: context).count, 32 * 1024)
            }
        }
    }

    func testContextsCarryEmitterEvidenceAndCameraWithinTheLimit() throws {
        // Small frame: everything fits, nothing is reduced.
        let small = try emitterBundle(red: [try emitterSaber(x: 10), try emitterSaber(x: 200, score: 70)], blue: [])
        let context = try XCTUnwrap(try contexts(small).first {
            ($0["bridgeEvent"] as? [String: Any])?["role"] as? String == "dropout" })
        XCTAssertNil(context["compaction"])
        let frames = try XCTUnwrap(context["frames"] as? [[String: Any]])
        let selected = try XCTUnwrap(frames.first { $0["frameID"] as? Int == 11 })
        XCTAssertEqual((selected["camera"] as? [String: Any])?["iso"] as? Double, 320)
        XCTAssertTrue(frames.filter { $0["frameID"] as? Int != 11 }.allSatisfy { $0["camera"] == nil },
                      "camera state is attached to the selected frame only")
        let red = try XCTUnwrap(selected["red"] as? [String: Any])
        let trace = try XCTUnwrap((red["candidateDecisionTrace"] as? [[String: Any]])?.first)
        let emitter = try XCTUnwrap(trace["emitterDiagnostics"] as? [String: Any])
        XCTAssertEqual(emitter["emitterScoreThreshold"] as? Double, 0.42)
        XCTAssertNotNil(emitter["bladeLengthSupport"] as? Double)
        let neighbour = try XCTUnwrap(frames.first { $0["frameID"] as? Int == 12 }?["red"] as? [String: Any])
        XCTAssertTrue((neighbour["candidateDecisionTrace"] as? [[String: Any]] ?? []).allSatisfy {
            $0["emitterDiagnostics"] == nil })
        let geometry = try redGeometry(context, frame: 11)
        let entries = try XCTUnwrap(geometry["candidates"] as? [[String: Any]])
        XCTAssertTrue(entries.allSatisfy { ($0["emitter"] as? [String: Any])?["emitterScore"] is Double })
        if let neighbourGeometry = try? redGeometry(context, frame: 12) {
            XCTAssertTrue((neighbourGeometry["candidates"] as? [[String: Any]] ?? []).allSatisfy {
                $0["emitter"] == nil })
        }

        // Many candidates on both colors: still under 32 KiB, and every reduction is stated.
        let many = try (0..<12).map { try emitterSaber(x: 10 + $0 * 30, score: 90 - Double($0)) }
            + (try (0..<6).map { try emitterSaber(x: 450 + $0 * 20, eligible: false, score: 5) })
        let large = try emitterBundle(red: many, blue: many)
        for context in try contexts(large) {
            let data = try JSONSerialization.data(withJSONObject: context)
            XCTAssertLessThan(data.count, 32 * 1024)
            let compaction = context["compaction"] as? [String] ?? []
            XCTAssertTrue(!compaction.isEmpty || data.count <= 24 * 1024)
            guard (context["bridgeEvent"] as? [String: Any])?["auxiliary"] as? Bool != true,
                  let id = context["selectedFrameID"] as? Int else { continue }
            let geometry = try redGeometry(context, frame: id)
            for entry in geometry["candidates"] as? [[String: Any]] ?? [] {
                XCTAssertTrue((entry["emitter"] != nil) != (entry["emitterDiagnosticsReduced"] as? Bool == true),
                              "each entry keeps its emitter evidence or says it was reduced")
            }
            if (geometry["candidates"] as? [[String: Any]] ?? []).contains(where: { $0["emitterDiagnosticsReduced"] != nil }) {
                XCTAssertTrue(compaction.contains("selectedEmitterDiagnosticsLimitedToLeadingFour"))
            }
            let frames = try XCTUnwrap(context["frames"] as? [[String: Any]])
            let selectedRed = try XCTUnwrap(frames.first { $0["frameID"] as? Int == id }?["red"] as? [String: Any])
            let traced = (selectedRed["candidateDecisionTrace"] as? [[String: Any]] ?? []).contains {
                $0["emitterDiagnostics"] != nil }
            XCTAssertTrue(traced || compaction.contains("selectedDecisionTraceEmitterDiagnosticsDropped"))
        }
    }
}

// MARK: - Camera exposure experiment (opt-in; never touches recognition)

final class CameraExposureExperimentTests: XCTestCase {
    private typealias Planner = CameraExposureExperimentPlanner
    private let formatMin = CMTime(value: 1, timescale: 10_000)      // 0.1 ms
    private let formatMax = CMTime(value: 1, timescale: 2)           // 500 ms
    private let defaultMax = CMTime(value: 1, timescale: 30)         // 33.3 ms

    func testDefaultIsAutoAndUnknownStoredValuesDecodeToAuto() {
        XCTAssertEqual(CameraExposureExperiment(storedValue: nil), .auto)
        XCTAssertEqual(CameraExposureExperiment(storedValue: "bogus"), .auto)
        XCTAssertEqual(CameraExposureExperiment(storedValue: 100), .auto)
        XCTAssertEqual(CameraExposureExperiment(storedValue: "maxShutter1_120"), .maxShutter1_120)
        XCTAssertEqual(CameraExposureExperimentState.initial.setting, .auto)
        XCTAssertEqual(CameraExposureExperimentState.initial.status, .auto)
        XCTAssertFalse(CameraExposureExperimentState.initial.capActive)
    }

    func testPersistenceRoundTripUsesStableKey() throws {
        let suite = "PhoneSaberExposureTests-\(UUID().uuidString)"
        let defaults = try XCTUnwrap(UserDefaults(suiteName: suite))
        defer { defaults.removePersistentDomain(forName: suite) }
        XCTAssertEqual(CameraExposureExperiment.stored(in: defaults), .auto)
        for setting in CameraExposureExperiment.allCases {
            setting.store(in: defaults)
            XCTAssertEqual(CameraExposureExperiment.stored(in: defaults), setting)
            XCTAssertEqual(defaults.string(forKey: "PhoneSaber.cameraExposureExperiment"), setting.rawValue)
        }
    }

    func testOptionMapsToExactShutterDuration() {
        XCTAssertNil(CameraExposureExperiment.auto.requestedMaxExposureDuration)
        XCTAssertEqual(CameraExposureExperiment.maxShutter1_100.requestedMaxExposureDuration,
                       CMTime(value: 1, timescale: 100))
        XCTAssertEqual(CameraExposureExperiment.maxShutter1_120.requestedMaxExposureDuration,
                       CMTime(value: 1, timescale: 120))
        XCTAssertEqual(CameraExposureExperiment.maxShutter1_240.requestedMaxExposureDuration,
                       CMTime(value: 1, timescale: 240))
        XCTAssertEqual(Set(CameraExposureExperiment.allCases.map(\.title)).count,
                       CameraExposureExperiment.allCases.count)
    }

    func testAutoLeavesDeviceUntouchedUnlessThisAppCappedIt() {
        let untouched = Planner.plan(setting: .auto, capActive: false, formatMinimum: formatMin,
                                     formatMaximum: formatMax, defaultMaximum: defaultMax,
                                     autoExposureActive: true)
        XCTAssertEqual(untouched.action, .leaveUntouched)
        XCTAssertEqual(untouched.state.status, .auto)
        let restored = Planner.plan(setting: .auto, capActive: true, formatMinimum: formatMin,
                                    formatMaximum: formatMax, defaultMaximum: defaultMax,
                                    autoExposureActive: true)
        XCTAssertEqual(restored.action, .resetToDefault)
        XCTAssertEqual(restored.state.status, .autoRestored)
    }

    func testCapAppliedWithinFormatRange() {
        let result = Planner.plan(setting: .maxShutter1_120, capActive: false, formatMinimum: formatMin,
                                  formatMaximum: formatMax, defaultMaximum: defaultMax,
                                  autoExposureActive: true)
        XCTAssertEqual(result.action, .setMaximum(CMTime(value: 1, timescale: 120)))
        XCTAssertEqual(result.state.status, .applied)
        XCTAssertTrue(result.state.capActive)
        XCTAssertEqual(try XCTUnwrap(result.state.appliedMaxExposureSeconds), 1.0 / 120, accuracy: 1e-12)
        XCTAssertEqual(try XCTUnwrap(result.state.defaultMaxExposureSeconds), 1.0 / 30, accuracy: 1e-12)
    }

    func testCapIsClampedToFormatMinimumAndMaximum() {
        let narrowMin = CMTime(value: 1, timescale: 200)  // 5 ms floor
        let floor = Planner.plan(setting: .maxShutter1_240, capActive: false, formatMinimum: narrowMin,
                                 formatMaximum: formatMax, defaultMaximum: defaultMax,
                                 autoExposureActive: true)
        XCTAssertEqual(floor.action, .setMaximum(narrowMin))
        XCTAssertEqual(floor.state.status, .clamped)
        XCTAssertEqual(Planner.clamp(CMTime(value: 1, timescale: 10), minimum: formatMin,
                                     maximum: CMTime(value: 1, timescale: 50)),
                       CMTime(value: 1, timescale: 50))
        XCTAssertNil(Planner.clamp(CMTime(value: 1, timescale: 100), minimum: .invalid, maximum: formatMax))
        XCTAssertNil(Planner.clamp(CMTime(value: 1, timescale: 100), minimum: formatMax, maximum: formatMin))
    }

    func testFallsBackToAutoWhenUnsupportedOrNotNeeded() {
        let invalidFormat = Planner.plan(setting: .maxShutter1_100, capActive: false, formatMinimum: .invalid,
                                         formatMaximum: .invalid, defaultMaximum: defaultMax,
                                         autoExposureActive: true)
        XCTAssertEqual(invalidFormat.action, .leaveUntouched)
        XCTAssertEqual(invalidFormat.state.status, .unsupported)
        XCTAssertFalse(invalidFormat.state.capActive)
        let manualExposure = Planner.plan(setting: .maxShutter1_100, capActive: true, formatMinimum: formatMin,
                                          formatMaximum: formatMax, defaultMaximum: defaultMax,
                                          autoExposureActive: false)
        XCTAssertEqual(manualExposure.action, .resetToDefault)
        XCTAssertEqual(manualExposure.state.status, .unsupported)
        // A device default already shorter than the request is left alone, so
        // the experiment can never lengthen the shutter.
        let shortDefault = Planner.plan(setting: .maxShutter1_100, capActive: false, formatMinimum: formatMin,
                                        formatMaximum: formatMax, defaultMaximum: CMTime(value: 1, timescale: 125),
                                        autoExposureActive: true)
        XCTAssertEqual(shortDefault.action, .leaveUntouched)
        XCTAssertEqual(shortDefault.state.status, .notNeeded)
    }

    func testMetadataDictionaryOmitsNilFieldsAndIsJSONSerializable() throws {
        let auto = CameraExposureExperimentState.initial.metadataDictionary
        XCTAssertEqual(auto["setting"] as? String, "auto")
        XCTAssertEqual(auto["status"] as? String, "auto")
        XCTAssertEqual(auto["capActive"] as? Bool, false)
        XCTAssertNil(auto["appliedMaxExposureSeconds"])
        let applied = Planner.plan(setting: .maxShutter1_100, capActive: false, formatMinimum: formatMin,
                                   formatMaximum: formatMax, defaultMaximum: defaultMax,
                                   autoExposureActive: true).state.metadataDictionary
        XCTAssertEqual(applied["setting"] as? String, "maxShutter1_100")
        XCTAssertEqual(applied["capActive"] as? Bool, true)
        XCTAssertEqual(try XCTUnwrap(applied["appliedMaxExposureSeconds"] as? Double), 0.01, accuracy: 1e-12)
        XCTAssertTrue(JSONSerialization.isValidJSONObject(applied))
    }

    @MainActor
    func testViewModelStartsAutoUnderTestAndPersistsNothing() {
        let model = CameraViewModel(p2pEnabled: false, idleTimerUpdater: { _ in })
        XCTAssertEqual(model.cameraExposureExperiment, .auto)
        XCTAssertEqual(model.cameraExposureExperimentState.status, .auto)
        XCTAssertTrue(model.cameraExposureExperimentEditable)
        let before = UserDefaults.standard.object(forKey: CameraExposureExperiment.storageKey) as? String
        model.setCameraExposureExperiment(.maxShutter1_120)
        XCTAssertEqual(model.cameraExposureExperiment, .maxShutter1_120)
        // Camera not running: applied at the next start.
        XCTAssertEqual(model.cameraExposureExperimentState.status, .pending)
        model.setCameraExposureExperiment(.auto)
        XCTAssertEqual(model.cameraExposureExperimentState.status, .auto)
        XCTAssertEqual(UserDefaults.standard.object(forKey: CameraExposureExperiment.storageKey) as? String, before)
    }
}

extension DebugBridgeDropoutTests {
    func testTriageSummaryCopiesCameraExposureExperimentWhenPresent() throws {
        let directory = try temporaryDirectory()
        let frames = (0..<8).map { frame($0, red: line(100), blue: line(200)) }
        var document = try XCTUnwrap(JSONSerialization.jsonObject(
            with: self.document(frames: frames, events: [])) as? [String: Any])
        let experiment = CameraExposureExperimentState(
            setting: .maxShutter1_120, status: .applied, requestedMaxExposureSeconds: 1.0 / 120,
            appliedMaxExposureSeconds: 1.0 / 120).metadataDictionary
        document["cameraExposureExperiment"] = experiment
        let bundle = try DebugRecordingTriageBuilder.build(
            metadataData: JSONSerialization.data(withJSONObject: document),
            metadataURL: directory.appendingPathComponent("metadata.json"),
            forensicDirectoryURL: directory)
        let summary = try XCTUnwrap(JSONSerialization.jsonObject(with:
            Data(contentsOf: bundle.appendingPathComponent("summary.json"))) as? [String: Any])
        let copied = try XCTUnwrap(summary["cameraExposureExperiment"] as? [String: Any])
        XCTAssertEqual(copied["setting"] as? String, "maxShutter1_120")
        XCTAssertEqual(copied["status"] as? String, "applied")

        let legacyDirectory = try temporaryDirectory()
        let legacy = try DebugRecordingTriageBuilder.build(
            metadataData: self.document(frames: frames, events: []),
            metadataURL: legacyDirectory.appendingPathComponent("metadata.json"),
            forensicDirectoryURL: legacyDirectory)
        let legacySummary = try XCTUnwrap(JSONSerialization.jsonObject(with:
            Data(contentsOf: legacy.appendingPathComponent("summary.json"))) as? [String: Any])
        XCTAssertNil(legacySummary["cameraExposureExperiment"])
    }

}

// MARK: Guided recording reserve (ガイド付き録画)

extension DebugBridgeDropoutTests {
    private func guidedSelection(guided: Bool) throws -> DebugRecordingTriageSelection {
        let directory = try temporaryDirectory()
        let events = (0..<2).map { event($0 + 1, color: "red", start: 10 + $0 * 10) }
        try writeImages(events, to: directory)
        for frame in 100...110 { try Data("png".utf8).write(to: directory.appendingPathComponent("motion_\(frame).png")) }
        let manual: [(frameID: Int, step: Int)] = [(40, 5), (50, 6), (60, 6), (70, 6), (80, 10), (120, 12)]
        var frames = (0..<130).map { frame($0, red: line(100), blue: line(100, 300)) }
        for capture in manual {
            frames[capture.frameID]["manualCaptured"] = true
            frames[capture.frameID]["manualFileName"] = "manual_frame_\(capture.frameID).png"
            try Data("png".utf8).write(to: directory.appendingPathComponent("manual_frame_\(capture.frameID).png"))
        }
        var document = try XCTUnwrap(JSONSerialization.jsonObject(with: self.document(
            frames: frames, events: events)) as? [String: Any])
        document["motionEvents"] = [trackingMotionEvent(peak: 105, score: 2.0)]
        document["motionSummary"] = ["events": [[DebugRecordingTriageLimits.trackingEventIndex, "red", 2.0, "retained"]]]
        if guided {
            document["guidedRecording"] = ["formatVersion": 1, "scriptID": "s", "scriptVersion": 1,
                "losslessCaptures": manual.map { ["stepIndex": $0.step, "frameID": $0.frameID] }]
        }
        return try DebugRecordingTriageBuilder.selectImages(
            metadataData: JSONSerialization.data(withJSONObject: document), forensicDirectoryURL: directory).selection
    }

    func testGuidedRecordingKeepsOneSwingFramePerStepAheadOfEventUnits() throws {
        let selection = try guidedSelection(guided: true)
        let manual = selection.images.filter { $0.fileName.hasPrefix("manual_frame_") }
        // One per swing step first (steps 5, 6, 10, 12), within the reserve.
        XCTAssertEqual(manual.map(\.frameID).sorted(), [40, 50, 80, 120])
        XCTAssertLessThanOrEqual(selection.images.count, DebugRecordingTriageLimits.defaultImageCount)
        // The tracking event still keeps a contiguous window with its peak.
        let tracking = selection.images.filter { $0.eventIndex == DebugRecordingTriageLimits.trackingEventIndex }
        XCTAssertGreaterThanOrEqual(tracking.count, DebugRecordingTriageLimits.minimumTrackingWindow)
        let ids = tracking.map(\.frameID).sorted()
        XCTAssertEqual(ids, Array(ids[0]...(ids[0] + UInt64(ids.count - 1))))
        XCTAssertTrue(tracking.contains { $0.role == "peak" })
        XCTAssertEqual(selection.trackingWindow?["selected"], tracking.count)
    }

    func testManualRecordingSelectionIsUnchangedByTheGuidedReserve() throws {
        // Without `guidedRecording` the same manual frames get no reserved slot:
        // the bridge event and the tracking window come first, as before.
        let selection = try guidedSelection(guided: false)
        let tracking = selection.images.filter { $0.eventIndex == DebugRecordingTriageLimits.trackingEventIndex }
        XCTAssertGreaterThanOrEqual(tracking.count, DebugRecordingTriageLimits.minimumTrackingWindow)
        XCTAssertFalse(selection.images.filter { $0.bridge != nil }.isEmpty)
        XCTAssertLessThan(selection.images.filter { $0.fileName.hasPrefix("manual_frame_") }.count, 4)
    }
}

// MARK: - Start/stop handling periods (synthetic frame streams)

final class DebugHandlingPeriodTests: XCTestCase {
    private typealias Retention = DebugHandlingAwareRetention<UInt64>
    private typealias Frame = (id: UInt64, time: Double, score: Double, lit: Bool)

    /// Feeds ranked frames the way the recorder does, then resolves at Stop.
    private func run(_ frames: [Frame], capacity: Int = 1, end: Double? = nil) -> Retention {
        var retention = Retention(capacity: capacity)
        for frame in frames {
            retention.advance(to: frame.time)
            let period = DebugHandlingPeriod.period(at: frame.time, end: nil)
            let preference = DebugEventPreference(handlingPeriod: period != nil, score: frame.score,
                                                  sabersVisible: frame.lit)
            if retention.wouldRetain(timestamp: frame.time, preference: preference) {
                retention.insert(.init(payload: frame.id, frameID: frame.id, timestamp: frame.time,
                                       period: period, preference: preference))
            }
        }
        retention.finish(end: end ?? frames.last?.time ?? 0)
        return retention
    }

    /// 30 fps stream with background score `base` and spikes at the given frames.
    private func stream(seconds: Double, base: Double = 1,
                        spikes: [UInt64: Double] = [:], lit: Set<UInt64> = []) -> [Frame] {
        (0..<UInt64(seconds * 30)).map { id in (id, Double(id) / 30, spikes[id] ?? base, lit.contains(id)) }
    }

    func testPeriodBoundaries() {
        XCTAssertEqual(DebugHandlingPeriod.period(at: 0, end: nil), "start")
        XCTAssertEqual(DebugHandlingPeriod.period(at: 2.99, end: nil), "start")
        XCTAssertNil(DebugHandlingPeriod.period(at: 3.0, end: nil))
        XCTAssertNil(DebugHandlingPeriod.period(at: 50, end: nil), "the stop period is unknown while recording")
        XCTAssertNil(DebugHandlingPeriod.period(at: 85, end: 90))
        XCTAssertEqual(DebugHandlingPeriod.period(at: 85.01, end: 90), "stop")
        XCTAssertEqual(DebugHandlingPeriod.period(at: 1, end: 2), "start", "start wins in very short recordings")
    }

    // The 10-04 pattern: background-to-background jumps while walking to / from
    // the phone outrank every swing. The swing in the middle must be chosen.
    func testSwingBeatsHigherStartAndStopJumps() throws {
        let chosen = try XCTUnwrap(run(stream(seconds: 90, spikes: [72: 82.7, 1_500: 20, 2_682: 61.7])).best)
        XCTAssertEqual(chosen.frameID, 1_500)
        XCTAssertNil(chosen.period)
        XCTAssertFalse(chosen.preference.handlingPeriod)
    }

    func testStopJumpLosesEvenAfterLeadingForSeconds() {
        // Leader at 85.3 s is still pending at Stop (89.97 s): it is in the stop period.
        XCTAssertEqual(run(stream(seconds: 90, spikes: [1_200: 10, 2_560: 99])).best?.frameID, 1_200)
        // The same jump before the last 5 s settles and wins on score.
        XCTAssertEqual(run(stream(seconds: 90, spikes: [1_200: 10, 2_500: 99])).best?.frameID, 2_500)
    }

    func testLeaderSettledJustBeforeTheStopPeriodSurvivesRisingStopJumps() {
        // Rising scores near the end: the oldest pending leader is spared, so the
        // best frame older than the last 5 s (84.0 s) is still available at Stop.
        let retention = run(stream(seconds: 90, spikes: [2_400: 10, 2_520: 20, 2_580: 30, 2_640: 40, 2_690: 50]))
        XCTAssertEqual(retention.best?.frameID, 2_520)
        XCTAssertNil(retention.best?.period)
    }

    func testHandlingEventIsKeptWhenNothingElseQualifies() throws {
        // Every frame of a 6 s recording lies in a handling period: plain score
        // order, and the kept event is marked as in a handling period.
        let start = try XCTUnwrap(run(stream(seconds: 6, spikes: [10: 40, 120: 30])).best)
        XCTAssertEqual(start.frameID, 10)
        XCTAssertEqual(start.period, "start")
        XCTAssertTrue(start.preference.handlingPeriod)
        let stop = try XCTUnwrap(run(stream(seconds: 6, spikes: [10: 40, 150: 50])).best)
        XCTAssertEqual(stop.frameID, 150)
        XCTAssertEqual(stop.period, "stop")
        XCTAssertTrue(stop.preference.handlingPeriod)
    }

    func testShortRecordingsKeepThePlainTopScoresWithEarlierFirstOnTies() {
        // The behaviour before handling periods existed, whenever every event is in one.
        var generator = SystemRandomNumberGenerator()
        for _ in 0..<50 {
            let frames: [Frame] = (0..<60).map {
                (UInt64($0), Double($0) / 30, Double(Int.random(in: 0...8, using: &generator)), false)
            }
            let expected = frames.reduce(frames[0]) { $1.score > $0.score ? $1 : $0 }.id
            XCTAssertEqual(run(frames).best?.frameID, expected)
            let ranked = frames.sorted { $0.score != $1.score ? $0.score > $1.score : $0.id < $1.id }
            XCTAssertEqual(run(frames, capacity: 2).settled.map(\.frameID).sorted(),
                           ranked.prefix(2).map(\.id).sorted())
        }
    }

    func testSabersVisibleLabelIsOnlyATieBreak() {
        // Equal scores: the lit-saber frame wins although it is later.
        XCTAssertEqual(run(stream(seconds: 30, spikes: [300: 20, 600: 20], lit: [600])).best?.frameID, 600)
        // Different scores: the score decides, not the label.
        XCTAssertEqual(run(stream(seconds: 30, spikes: [300: 21, 600: 20], lit: [600])).best?.frameID, 300)
        // Unlabelled equal scores: the earlier frame stays.
        XCTAssertEqual(run(stream(seconds: 30, spikes: [300: 20, 600: 20])).best?.frameID, 300)
    }

    func testBridgeRetentionPrefersTwoEventsOutsideTheHandlingPeriods() {
        // Gaps as scores: a start loss, two mid-recording losses, a long switch-off at the end.
        let events: [Frame] = [(15, 0.5, 0.40, false), (900, 30, 0.10, false),
                               (1_800, 60, 0.20, false), (2_670, 89, 1.23, false)]
        let retention = run(events, capacity: 2, end: 90.4)
        XCTAssertEqual(retention.settled.map(\.frameID).sorted(), [900, 1_800])
        XCTAssertTrue(retention.settled.allSatisfy { $0.period == nil })
        // One event outside: the better handling event still fills the second slot.
        let sparse = run([(15, 0.5, 0.40, false), (900, 30, 0.10, false), (2_670, 89, 1.23, false)],
                         capacity: 2, end: 90.4)
        XCTAssertEqual(sparse.settled.map(\.frameID).sorted(), [900, 2_670])
        XCTAssertEqual(sparse.settled.first { $0.frameID == 2_670 }?.period, "stop")
    }

    func testDominatedEventsAreRejectedAndPendingStaysBounded() {
        var retention = Retention(capacity: 1)
        retention.insert(.init(payload: 1, frameID: 1, timestamp: 10, period: nil,
                               preference: DebugEventPreference(handlingPeriod: false, score: 10)))
        XCTAssertFalse(retention.wouldRetain(timestamp: 11, preference: DebugEventPreference(handlingPeriod: false, score: 5)))
        XCTAssertFalse(retention.wouldRetain(timestamp: 11, preference: DebugEventPreference(handlingPeriod: false, score: 10)),
                       "the earlier event wins ties")
        XCTAssertTrue(retention.wouldRetain(timestamp: 11, preference: DebugEventPreference(handlingPeriod: false, score: 11)))
        for index in 2...20 {
            retention.insert(.init(payload: UInt64(index), frameID: UInt64(index), timestamp: 10 + Double(index) / 30,
                                   period: nil, preference: DebugEventPreference(handlingPeriod: false, score: Double(10 + index))))
            XCTAssertLessThanOrEqual(retention.pending.count, 2)
            XCTAssertEqual(retention.pending.first?.frameID, 1, "the oldest leader is spared")
        }
    }
}
