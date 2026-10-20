import CoreGraphics
import Foundation
import ImageIO
import XCTest
#if !AUTOCLIP_STANDALONE
@testable import PhoneSaberSender
#endif

/// Auto clip (AutoClip.swift): pure trigger rules, rate limiting, suspend policy,
/// PC commands, deferred upload and one synthetic clip written end to end.
final class AutoClipTests: XCTestCase {
    private typealias P = PixelPoint

    static func candidate(_ a: (Int, Int), _ b: (Int, Int), eligible: Bool = true,
                          score: Double = 100, source: String = "color-mask") -> SaberCandidate {
        let breakdown = SaberScoreBreakdown(length: 0, aspect: 0, extent: 0, widthConsistency: 0, area: 0,
                                            peakBrightness: 0, meanBrightness: 0, highBrightnessRatio: 0,
                                            colorPurity: 0, localContrast: 0, emitterTexture: 0, clippedWhite: 0,
                                            longitudinalHighCoverage: 0, coreSupport: 0, longitudinalCoreCoverage: 0)
        let ends = (P(x: a.0, y: a.1), P(x: b.0, y: b.1))
        return SaberCandidate(
            source: source, comparisonEndpoints: ends, endpoints: ends,
            boundingBox: SaberBoundingBox(minX: min(a.0, b.0) - 3, minY: min(a.1, b.1) - 3,
                                          maxX: max(a.0, b.0) + 3, maxY: max(a.1, b.1) + 3),
            score: score, scoreBreakdown: breakdown, isEmitterEligible: eligible, isCompactRed: false,
            peakValue: 255, meanValue: 240, highValueRatio: 0.5, meanColorPurity: 0.6, clippedWhiteRatio: 0,
            brightnessVariation: 0, localContrast: 1, longitudinalHighCoverage: 1, widthVariation: 0,
            coreSupportRatio: 0.5, longitudinalCoreCoverage: 1, longitudinalContinuity: 1,
            largestLongitudinalGap: 0, retainedBodyRatio: 1, rawPCASpan: 100, robustMainIntervalEndpoints: nil,
            robustMainIntervalLength: 100, axialDensity: 1, componentArea: 100, pointCount: 50,
            usedPointLEDFallback: false)
    }

    // MARK: Competition

    func testObjectIgnoreZoneSelectableKeepsCrossingSaber() {
        let cone = Self.candidate((150, 100), (400, 350))
        let crossing = Self.candidate((170, 320), (380, 130))
        let zone = SaberIgnoreZone(around: cone.endpoints, width: 640, height: 480)
        let old = SaberIgnoreZone(minX: zone.minX, minY: zone.minY, maxX: zone.maxX, maxY: zone.maxY)
        XCTAssertFalse(AutoClipCompetition.selectable(cone, width: 640, height: 480, zones: [zone], playArea: nil))
        XCTAssertTrue(AutoClipCompetition.selectable(crossing, width: 640, height: 480, zones: [zone], playArea: nil))
        XCTAssertFalse(AutoClipCompetition.selectable(crossing, width: 640, height: 480, zones: [old], playArea: nil))
    }

    func testCompetitionCountsOnlySeparatedSelectableEligibleCandidates() {
        let saber = Self.candidate((100, 100), (100, 300))
        let duplicate = Self.candidate((102, 120), (101, 280), source: "core-line")   // same blade
        let cone = Self.candidate((400, 600), (420, 630))                               // far away
        let dim = Self.candidate((300, 100), (300, 200), eligible: false)
        var result = AutoClipCompetition.evaluate([saber, duplicate, dim, cone], selected: saber.endpoints, width: 480, height: 640,
                                                  zones: [], playArea: nil)
        XCTAssertEqual(result.winnerIndex, 0)
        XCTAssertEqual(result.competitorCount, 1)
        XCTAssertEqual(result.competitorIndex, 3)
        XCTAssertEqual(result.eligibleCount, 3)

        // A cone inside an ignore zone or outside the play area cannot be selected: no competitor.
        let zone = SaberIgnoreZone(around: cone.endpoints, width: 480, height: 640)
        result = AutoClipCompetition.evaluate([saber, cone], selected: saber.endpoints, width: 480, height: 640, zones: [zone], playArea: nil)
        XCTAssertEqual(result.competitorCount, 0)
        let area = SaberPlayArea(minX: 0, minY: 0, maxX: 0.5, maxY: 0.6)
        result = AutoClipCompetition.evaluate([saber, cone], selected: saber.endpoints, width: 480, height: 640, zones: [], playArea: area)
        XCTAssertEqual(result.competitorCount, 0)
        // 実出力を渡し、無視領域内の上位候補を winner と誤記しない。
        result = AutoClipCompetition.evaluate([cone, saber], selected: saber.endpoints, width: 480, height: 640, zones: [zone], playArea: nil)
        XCTAssertEqual(result.winnerIndex, 1)
    }

    #if !AUTOCLIP_STANDALONE
    func testCompetitionWinnerMatchesProductionSelection() throws {
        // Real fixture: whatever analyzeSabers selects must be the evaluated winner.
        let url = try XCTUnwrap(Bundle(for: Self.self).url(forResource: "blue-led-with-curtain-reflection-01",
                                                            withExtension: "png"))
        let source = try XCTUnwrap(CGImageSourceCreateWithURL(url as CFURL, nil))
        let image = try XCTUnwrap(CGImageSourceCreateImageAtIndex(source, 0, nil))
        let width = image.width, height = image.height
        var bgra = [UInt8](repeating: 0, count: width * height * 4)
        bgra.withUnsafeMutableBytes { raw in
            let context = CGContext(data: raw.baseAddress, width: width, height: height, bitsPerComponent: 8,
                                    bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
                                    bitmapInfo: CGBitmapInfo.byteOrder32Little.rawValue
                                        | CGImageAlphaInfo.premultipliedFirst.rawValue)
            context?.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
        }
        let analysis = analyzeSabers(in: bgra, width: width, height: height, bytesPerRow: width * 4,
                                     redThreshold: ColorThreshold(), blueThreshold: ColorThreshold())
        for color in [SaberColor.red, .blue] {
            let list = analysis.candidates[color] ?? []
            let result = AutoClipCompetition.evaluate(list, selected: analysis.selected[color], width: width, height: height, zones: [], playArea: nil)
            if let selected = analysis.selected[color] {
                let winner = try XCTUnwrap(result.winnerIndex)
                XCTAssertEqual(list[winner].endpoints.0, selected.0)
                XCTAssertEqual(list[winner].endpoints.1, selected.1)
            } else {
                XCTAssertNil(result.winnerIndex)
            }
        }
    }

    #endif

    // 実際の analyzeSabers → recorder の経路を両色・追跡・範囲・無視領域で確認する。
    func testRecordedWinnerEqualsActualOutputWithTrackAreaAndIgnoreZones() throws {
        let width = 480, height = 640
        func pixels(_ x: Int, length: Int = 40) -> [UInt8] {
            var bytes = [UInt8](repeating: 0, count: width * height * 4)
            for y in 150..<(150 + length) { for column in x..<(x + 8) {
                let offset = (y * width + column) * 4
                bytes[offset + 2] = 255
                bytes[((y + 200) * width + column) * 4] = 255
                bytes[offset + 3] = 255
            }}
            return bytes
        }
        let threshold = ColorThreshold()
        let area = SaberPlayArea(minX: 0, minY: 0, maxX: 0.3, maxY: 1)
        let lock = SaberTrackLock()
        let buffers = AutoClipBuffers(width: width, height: height, captureFPS: 30)
        func check(_ bytes: [UInt8], time: Double, area: SaberPlayArea?, zones: [SaberColor: [SaberIgnoreZone]] = [:],
                   track: SaberTrackLock? = nil, suppressed: Bool? = nil) throws -> SaberFrameAnalysis {
            let analysis = analyzeSabers(in: bytes, width: width, height: height, bytesPerRow: width * 4,
                redThreshold: threshold, blueThreshold: threshold, collectObserverEvidence: true,
                ignoreZones: zones, playArea: area, trackLock: track, now: time)
            bytes.withUnsafeBufferPointer { raw in
                let frame = ObservedFrame(baseAddress: raw.baseAddress!, width: width, height: height,
                    bytesPerRow: width * 4, sequence: UInt64(time * 100), timestamp: time,
                    processingSeconds: 0, analysis: analysis, ignoreZones: zones, playArea: area, captureFPS: 30)
                let red = AutoClipCompetition.evaluate(analysis.candidates[.red] ?? [], selected: analysis.selected[.red],
                    width: width, height: height, zones: zones[.red] ?? [], playArea: area)
                let blue = AutoClipCompetition.evaluate(analysis.candidates[.blue] ?? [], selected: analysis.selected[.blue],
                    width: width, height: height, zones: zones[.blue] ?? [], playArea: area)
                buffers.storeRecord(frame, red: red, blue: blue)
            }
            let slot = (buffers.frameNext + buffers.frameCapacity - 1) % buffers.frameCapacity
            for (colorIndex, color) in [SaberColor.red, .blue].enumerated() {
                let record = color == .red ? buffers.frames[slot].red : buffers.frames[slot].blue
                let base = (slot * 2 + colorIndex) * AutoClipLimits.candidatesPerColor
                let winners = (0..<Int(record.stored)).map { buffers.candidates[base + $0] }
                    .filter { $0.flags & AutoClipCandidateRecord.selected != 0 }
                XCTAssertEqual(winners.count, analysis.selected[color] == nil ? 0 : 1)
                XCTAssertEqual(record.trackLockSuppressed, analysis.trackLockSuppressed.contains(color))
                if let suppressed { XCTAssertEqual(record.trackLockSuppressed, suppressed) }
                if let output = analysis.selected[color], let winner = winners.first {
                    XCTAssertEqual([Int(winner.x1), Int(winner.y1), Int(winner.x2), Int(winner.y2)],
                                   AutoClipCompetition.endpointsArray(output))
                    XCTAssertNotNil(winner.emitterScore)
                    XCTAssertNotNil(winner.aspect)
                    XCTAssertNotNil(winner.nearSaturationPresent)
                }
            }
            return analysis
        }
        let acquired = try check(pixels(100), time: 1, area: area, track: lock, suppressed: false)
        XCTAssertEqual(acquired.selected.count, 2)
        // 高scoreの遠方候補が先頭に来ても、track lockが選ぶ実候補を記録する。
        let two = zip(pixels(100), pixels(400, length: 80)).map { max($0, $1) }
        let tracked = try check(two, time: 1.01, area: nil, track: lock, suppressed: false)
        for color in [SaberColor.red, .blue] {
            let output = try XCTUnwrap(tracked.selected[color])
            let first = try XCTUnwrap(tracked.candidates[color]?.first)
            XCTAssertNotEqual(first.endpoints.0, output.0)
            XCTAssertEqual(output.0, acquired.selected[color]?.0)
            XCTAssertEqual(output.1, acquired.selected[color]?.1)
        }
        // 生きた追跡では範囲外に出ても実出力を winner と記録する。
        let outside = try check(pixels(150), time: 1.03, area: area, track: lock, suppressed: false)
        XCTAssertEqual(outside.selected.count, 2)
        let blocked = try check(pixels(400), time: 1.06, area: nil, track: lock, suppressed: true)
        XCTAssertTrue(blocked.selected.isEmpty)
        XCTAssertFalse((blocked.candidates[.red] ?? []).isEmpty)
        let areaBlocked = try check(pixels(400), time: 2, area: area)
        XCTAssertTrue(areaBlocked.selected.isEmpty)
        var zones: [SaberColor: [SaberIgnoreZone]] = [:]
        for color in [SaberColor.red, .blue] {
            zones[color] = [SaberIgnoreZone(around: try XCTUnwrap(acquired.selected[color]), width: width, height: height)]
        }
        XCTAssertTrue(try check(pixels(100), time: 3, area: nil, zones: zones).selected.isEmpty)
    }

    func testStoragePrioritizesActualWinnerAndEverySeparatedCompetitor() {
        let winner = Self.candidate((100, 100), (100, 300))
        let competitor = Self.candidate((400, 500), (400, 600))
        let dim = Self.candidate((20, 20), (20, 40), eligible: false)
        // 先頭に多数の不適格・重複候補、winner は低rank。競合を末尾に置く。
        let list = Array(repeating: dim, count: 4) + Array(repeating: winner, count: 14) + [competitor, Self.candidate((300, 500), (300, 600)), Self.candidate((200, 500), (200, 600))]
        let analysis = SaberFrameAnalysis(candidates: [.red: list], selected: [.red: winner.endpoints])
        let buffers = AutoClipBuffers(width: 480, height: 640, captureFPS: 30)
        let competition = AutoClipCompetition.evaluate(list, selected: analysis.selected[.red], width: 480,
                                                       height: 640, zones: [], playArea: nil)
        var pixel: UInt8 = 0
        withUnsafePointer(to: &pixel) { base in
            buffers.storeRecord(ObservedFrame(baseAddress: base, width: 480, height: 640, bytesPerRow: 1920,
                sequence: 1, timestamp: 1, processingSeconds: 0, analysis: analysis, ignoreZones: [:],
                playArea: nil, captureFPS: 30), red: competition, blue: AutoClipCompetition())
        }
        XCTAssertEqual(buffers.candidates[0].rank, 4)
        XCTAssertEqual(buffers.candidates[0].flags & AutoClipCandidateRecord.selected, AutoClipCandidateRecord.selected)
        XCTAssertEqual(buffers.candidates[1].rank, 18)
        XCTAssertEqual(buffers.candidates[1].flags & AutoClipCandidateRecord.competitor, AutoClipCandidateRecord.competitor)
        XCTAssertEqual(buffers.frames[0].red.stored, 12)
        XCTAssertEqual(competition.competitorCount, 3)
        for storedIndex in 1...3 {
            XCTAssertEqual(buffers.candidates[storedIndex].rank, Int16(17 + storedIndex))
            XCTAssertEqual(buffers.candidates[storedIndex].flags & AutoClipCandidateRecord.competitor,
                           AutoClipCandidateRecord.competitor)
        }
        // 先頭のselectableではない、追跡で選ばれた低順位の実出力にも一致する。
        let lower = AutoClipCompetition.evaluate([competitor, winner], selected: winner.endpoints,
                                                 width: 480, height: 640, zones: [], playArea: nil)
        XCTAssertEqual(lower.winnerIndex, 1)
        XCTAssertEqual(lower.competitorIndex, 0)
        XCTAssertEqual(lower.competitorCount, 1)
        XCTAssertNil(AutoClipCompetition.evaluate(list, selected: nil, width: 480, height: 640,
                                                 zones: [], playArea: nil).winnerIndex)
    }

    func testBundleMetadataSerializesRealOutputSuppressionAndEvidence() throws {
        var winner = Self.candidate((100, 100), (100, 300))
        winner.observerEmitterInputs = SaberObserverEmitterInputs(emitterScore: 0.8,
                                                                 nearSaturationPresent: true, aspect: 12.5)
        let competitor = Self.candidate((400, 500), (400, 600))
        let analysis = SaberFrameAnalysis(candidates: [.red: [competitor, winner], .blue: [competitor]],
            selected: [.red: winner.endpoints], trackLockSuppressed: [.blue])
        let buffers = AutoClipBuffers(width: 480, height: 640, captureFPS: 30)
        var pixel: UInt8 = 0
        withUnsafePointer(to: &pixel) { base in
            buffers.storeRecord(ObservedFrame(baseAddress: base, width: 480, height: 640, bytesPerRow: 1920,
                sequence: 1, timestamp: 1, processingSeconds: 0, analysis: analysis, ignoreZones: [:],
                playArea: nil, captureFPS: 30),
                red: AutoClipCompetition.evaluate(analysis.candidates[.red]!, selected: analysis.selected[.red],
                    width: 480, height: 640, zones: [], playArea: nil), blue: AutoClipCompetition())
        }
        let capture = AutoClipCapture(clipID: "test", trigger: AutoClipTriggerEvent(kind: .manual, frameID: 1, timestamp: 1),
            buffers: buffers, windowStart: 0, windowEnd: 2, captureFPS: 30,
            environmentAtTrigger: AutoClipEnvironment(signingExpiryRemainingHours: 49),
            environmentAtSave: AutoClipEnvironment(signingExpiryRemainingHours: 48),
            ignoreZones: [:], playArea: nil, sessionClipIndex: 1, rejectedTriggers: [:])
        let content = AutoClipBundleContent(capture: capture, slots: [0])
        let data = try content.summaryData(images: [])
        let summary = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        // 転送envelopeは互換v1、内部AutoClipのschemaだけv2。
        XCTAssertEqual(summary["formatVersion"] as? Int, 1)
        let clip = try XCTUnwrap(summary["autoClip"] as? [String: Any])
        XCTAssertEqual(clip["formatVersion"] as? Int, 2)
        XCTAssertEqual((clip["camera"] as? [String: Any])?["signingExpiryRemainingHours"] as? Int, 48)
        let frames = try XCTUnwrap(clip["frames"] as? [[String: Any]])
        let red = try XCTUnwrap(frames.first?["red"] as? [String: Any])
        let blue = try XCTUnwrap(frames.first?["blue"] as? [String: Any])
        XCTAssertEqual(red["sel"] as? [Int], [100, 100, 100, 300])
        XCTAssertEqual(blue["sup"] as? Bool, true)
        XCTAssertTrue(blue["sel"] is NSNull)
        let rows = try XCTUnwrap(red["c"] as? [[Any]])
        let columns = AutoClipBundleContent.candidateColumns
        XCTAssertEqual(rows[0][0] as? Int, 1) // 実winnerは候補順位1
        XCTAssertEqual(rows[0][columns.firstIndex(of: "emitterScore")!] as? Double, 0.8)
        XCTAssertEqual(rows[0][columns.firstIndex(of: "nearSaturationPresent")!] as? Bool, true)
        XCTAssertTrue(rows[1][columns.firstIndex(of: "emitterScore")!] is NSNull)
        XCTAssertNoThrow(try JSONSerialization.data(withJSONObject: content.frameContext(frameID: 1, sessionID: "test")))
    }

    func testDenseHighFPSMetadataKeepsTriggerUnderSummaryLimit() throws {
        let buffers = AutoClipBuffers(width: 480, height: 640, captureFPS: 120)
        let count = 600
        for slot in 0..<count {
            buffers.frames[slot].frameID = UInt64(slot + 1)
            buffers.frames[slot].timestamp = Double(slot) / 120
            for colorIndex in 0..<2 {
                var record = AutoClipColorRecord()
                record.stored = 12; record.hasSelection = true
                record.candidateCount = 12; record.eligibleCount = 12; record.competitorCount = 11
                if colorIndex == 0 { buffers.frames[slot].red = record } else { buffers.frames[slot].blue = record }
                for index in 0..<12 {
                    let base = (slot * 2 + colorIndex) * 12 + index
                    buffers.candidates[base] = AutoClipCandidateRecord(rank: Int16(index), x1: 100, y1: 100,
                        x2: 100, y2: 300, minX: 90, minY: 90, maxX: 110, maxY: 310,
                        score: 123.45, peakValue: 255, emitterScore: 0.8765, highValueRatio: 0.4567,
                        meanValue: 234.5, colorPurity: 0.5678, nearSaturationPresent: true,
                        componentArea: 480, aspect: 12.3456, flags: index == 0 ? 7 : 11, source: "color-sparse-raw")
                }
            }
        }
        let capture = AutoClipCapture(clipID: "dense", trigger: AutoClipTriggerEvent(kind: .manual, frameID: 361, timestamp: 3),
            buffers: buffers, windowStart: 0, windowEnd: 5, captureFPS: 120,
            environmentAtTrigger: AutoClipEnvironment(), environmentAtSave: AutoClipEnvironment(),
            ignoreZones: [:], playArea: nil, sessionClipIndex: 1, rejectedTriggers: [:])
        let data = try AutoClipBundleContent(capture: capture, slots: Array(0..<count)).summaryData(images: [])
        XCTAssertLessThanOrEqual(data.count, AutoClipLimits.maximumSummaryBytes)
        let summary = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let clip = try XCTUnwrap(summary["autoClip"] as? [String: Any])
        XCTAssertTrue((clip["camera"] as? [String: Any])?["signingExpiryRemainingHours"] is NSNull)
        let frames = try XCTUnwrap(clip["frames"] as? [[String: Any]])
        let trigger = try XCTUnwrap(frames.first { $0["f"] as? Int == 361 })
        for color in ["red", "blue"] {
            let record = try XCTUnwrap(trigger[color] as? [String: Any])
            XCTAssertEqual((record["c"] as? [[Any]])?.count, 12)
        }
    }

    // MARK: Trigger detector

    private func observe(_ detector: inout AutoClipTriggerDetector, _ sequence: UInt64,
                         winner: (P, P)?, competitors: Int = 0) -> AutoClipTriggerEvent? {
        detector.observe(color: .red, sequence: sequence, timestamp: Double(sequence) / 60, winner: winner,
                         competitorCount: competitors,
                         competitor: competitors > 0 ? (P(x: 400, y: 600), P(x: 420, y: 630)) : nil,
                         width: 480, height: 640)
    }

    func testConfusionFiresOnceAfterThreeConsecutiveFrames() {
        var detector = AutoClipTriggerDetector()
        let saber = (P(x: 100, y: 100), P(x: 100, y: 300))
        XCTAssertNil(observe(&detector, 1, winner: saber, competitors: 1))
        XCTAssertNil(observe(&detector, 2, winner: saber, competitors: 1))
        let event = observe(&detector, 3, winner: saber, competitors: 2)
        XCTAssertEqual(event?.kind, .confusion)
        XCTAssertEqual(event?.color, .red)
        XCTAssertEqual(event?.competitorCount, 2)
        XCTAssertEqual(event?.competitor, [400, 600, 420, 630])
        XCTAssertNil(observe(&detector, 4, winner: saber, competitors: 1))   // no refire while the run continues
        XCTAssertNil(observe(&detector, 5, winner: saber))                   // run broken
        XCTAssertNil(observe(&detector, 6, winner: saber, competitors: 1))
        XCTAssertNil(observe(&detector, 7, winner: saber, competitors: 1))
        XCTAssertEqual(observe(&detector, 8, winner: saber, competitors: 1)?.kind, .confusion)
    }

    func testConfusionNeedsConsecutiveFrames() {
        var detector = AutoClipTriggerDetector()
        let saber = (P(x: 100, y: 100), P(x: 100, y: 300))
        XCTAssertNil(observe(&detector, 1, winner: saber, competitors: 1))
        XCTAssertNil(observe(&detector, 2, winner: saber, competitors: 1))
        XCTAssertNil(observe(&detector, 9, winner: saber, competitors: 1))  // gap > 2: run restarts
        XCTAssertNil(observe(&detector, 10, winner: saber, competitors: 1))
        XCTAssertEqual(observe(&detector, 11, winner: saber, competitors: 1)?.kind, .confusion)
        // Without a winner there is no confusion.
        var empty = AutoClipTriggerDetector()
        for sequence in 1...5 { XCTAssertNil(observe(&empty, UInt64(sequence), winner: nil, competitors: 3)) }
    }

    func testJumpUsesTypicalLengthAndFrameDiagonalFloor() {
        var detector = AutoClipTriggerDetector()
        // 200 px blade moving 30 px per frame: normal fast swing, no trigger.
        for step in 0..<10 {
            let x = 40 + step * 30
            XCTAssertNil(observe(&detector, UInt64(step + 1), winner: (P(x: x, y: 100), P(x: x, y: 300))))
        }
        // Jump of the midpoint by ~300 px (> 200 px typical length and > 120 px floor).
        let event = observe(&detector, 11, winner: (P(x: 330, y: 420), P(x: 330, y: 620)))
        XCTAssertEqual(event?.kind, .jump)
        XCTAssertEqual(event?.previousWinner, [310, 100, 310, 300])
        XCTAssertEqual(event?.jumpThreshold ?? 0, 200, accuracy: 0.5)
        XCTAssertGreaterThan(event?.jumpDistance ?? 0, 300)
        // Jumping back is a jump too; the far winner did not shrink the typical length.
        let back = observe(&detector, 12, winner: (P(x: 310, y: 100), P(x: 310, y: 300)))
        XCTAssertEqual(back?.kind, .jump)
        XCTAssertEqual(back?.typicalLength ?? 0, 200, accuracy: 0.5)
    }

    func testJumpFloorForShortBladesAndGaps() {
        var detector = AutoClipTriggerDetector()
        // 40 px blade: the floor 0.15 × 800 = 120 px applies, so a 100 px move is not a jump.
        XCTAssertNil(observe(&detector, 1, winner: (P(x: 100, y: 100), P(x: 100, y: 140))))
        XCTAssertNil(observe(&detector, 2, winner: (P(x: 200, y: 100), P(x: 200, y: 140))))
        XCTAssertEqual(observe(&detector, 3, winner: (P(x: 330, y: 100), P(x: 330, y: 140)))?.kind, .jump)
        // Not consecutive (gap 3) or after a missing frame: no jump.
        XCTAssertNil(observe(&detector, 6, winner: (P(x: 30, y: 500), P(x: 30, y: 540))))
        XCTAssertNil(observe(&detector, 7, winner: nil))
        XCTAssertNil(observe(&detector, 8, winner: (P(x: 400, y: 100), P(x: 400, y: 140))))
    }

    // MARK: Rate limiter

    private func event(_ kind: AutoClipTriggerKind, at time: Double, competitor: [Int]? = [400, 600, 420, 630]) -> AutoClipTriggerEvent {
        AutoClipTriggerEvent(kind: kind, color: kind == .manual ? nil : .red, frameID: UInt64(time * 60),
                             timestamp: time, competitor: kind == .confusion ? competitor : nil)
    }

    func testRateLimiterCooldownSessionLimitAndRepeatLocation() {
        var limiter = AutoClipRateLimiter()
        XCTAssertEqual(limiter.admit(event(.confusion, at: 10), busy: false, width: 480, height: 640), .accept)
        XCTAssertEqual(limiter.admit(event(.jump, at: 25), busy: false, width: 480, height: 640), .reject("cooldown"))
        XCTAssertEqual(limiter.admit(event(.jump, at: 31), busy: false, width: 480, height: 640), .accept)
        // Same cone location again: skipped even after the cooldown.
        XCTAssertEqual(limiter.admit(event(.confusion, at: 60, competitor: [405, 602, 421, 628]), busy: false, width: 480, height: 640),
                       .reject("repeatLocation"))
        XCTAssertEqual(limiter.admit(event(.confusion, at: 61, competitor: [100, 100, 120, 200]), busy: false, width: 480, height: 640),
                       .accept)
        XCTAssertEqual(limiter.admit(event(.jump, at: 90), busy: true, width: 480, height: 640), .reject("busy"))
        for index in 0..<3 {
            XCTAssertEqual(limiter.admit(event(.jump, at: 100 + Double(index) * 30), busy: false, width: 480, height: 640), .accept)
        }
        XCTAssertEqual(limiter.automaticClipsThisSession, AutoClipLimits.maximumAutomaticClipsPerSession)
        XCTAssertEqual(limiter.admit(event(.jump, at: 400), busy: false, width: 480, height: 640), .reject("sessionLimit"))
        // A PC request still works, at most every 5 s.
        XCTAssertEqual(limiter.admit(event(.manual, at: 401), busy: false, width: 480, height: 640), .accept)
        XCTAssertEqual(limiter.admit(event(.manual, at: 403), busy: false, width: 480, height: 640), .reject("manualInterval"))
        XCTAssertEqual(limiter.admit(event(.manual, at: 406.5), busy: false, width: 480, height: 640), .accept)
        XCTAssertEqual(limiter.rejected["cooldown"], 1)
        XCTAssertEqual(limiter.rejected["sessionLimit"], 1)
        limiter.beginSession()
        XCTAssertEqual(limiter.automaticClipsThisSession, 0)
        XCTAssertEqual(limiter.admit(event(.confusion, at: 500), busy: false, width: 480, height: 640), .accept)
    }

    func testSuspendPolicy() {
        XCTAssertNil(AutoClipSuspendPolicy.reason(thermal: .nominal, fpsReduced: false, debugRecording: false))
        XCTAssertNil(AutoClipSuspendPolicy.reason(thermal: .fair, fpsReduced: false, debugRecording: false))
        XCTAssertEqual(AutoClipSuspendPolicy.reason(thermal: .serious, fpsReduced: false, debugRecording: false), "thermalSerious")
        XCTAssertEqual(AutoClipSuspendPolicy.reason(thermal: .critical, fpsReduced: true, debugRecording: false), "thermalCritical")
        XCTAssertEqual(AutoClipSuspendPolicy.reason(thermal: .fair, fpsReduced: true, debugRecording: false), "fpsReduced")
        XCTAssertEqual(AutoClipSuspendPolicy.reason(thermal: .nominal, fpsReduced: false, debugRecording: true), "debugRecording")
    }

    #if !AUTOCLIP_STANDALONE
    // MARK: PC commands and transfer

    func testPCCommandParsingClip() {
        typealias C = LANLivenessProbe.PCCommand
        XCTAssertEqual(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=21 clip=1"),
                       C(id: 21, ignore: nil, clearIgnore: false, clip: true))
        XCTAssertEqual(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=22 auto-clip=0"),
                       C(id: 22, ignore: nil, clearIgnore: false, autoClip: false))
        XCTAssertEqual(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=23 auto-clip=1"),
                       C(id: 23, ignore: nil, clearIgnore: false, autoClip: true))
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=24 clip=0"))
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=24 auto-clip=2"))
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 clip=1"))
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=25 clip=1 auto-clip=1"))
        XCTAssertNil(LANLivenessProbe.parseCommand("PHONESABER_COMMAND 1 id=25 clip=1 ignore=red"))
    }

    func testDeferredClipUploadsWaitForQuietTraffic() {
        var outbox = DebugBundleOutbox()
        let now = Date()
        outbox.register(sessionID: "autoclip_a", bundlePath: "AutoClips/phone_saber_triage_autoclip_a",
                        now: now, deferDuringPlay: true)
        XCTAssertNil(outbox.beginNext(now: now, recording: false, automatic: true, liveTraffic: true))
        XCTAssertEqual(outbox.beginNext(now: now, recording: false, automatic: true, liveTraffic: false)?.sessionID,
                       "autoclip_a")
        // Debug Recording bundles keep their behaviour while playing.
        var recordings = DebugBundleOutbox()
        recordings.register(sessionID: "phonesaber_x", bundlePath: "phone_saber_triage_phonesaber_x", now: now)
        XCTAssertEqual(recordings.beginNext(now: now, recording: false, automatic: true, liveTraffic: true)?.sessionID,
                       "phonesaber_x")
        // Older outboxes without the new key still decode.
        let legacy = #"{"entries":[{"sessionID":"s","bundlePath":"p","createdAt":0,"state":"pending","attempts":0,"nextAttemptAt":0,"automatic":true}],"prunedBefore":0}"#
        XCTAssertNoThrow(try JSONDecoder().decode(DebugBundleOutbox.self, from: Data(legacy.utf8)))

        let documents = URL(fileURLWithPath: "/var/mobile/Documents")
        XCTAssertEqual(DebugBundleTransfer.storedPath(
            for: documents.appendingPathComponent("AutoClips/phone_saber_triage_autoclip_a"), in: documents),
                       "AutoClips/phone_saber_triage_autoclip_a")
        XCTAssertEqual(DebugBundleTransfer.storedPath(
            for: documents.appendingPathComponent("phone_saber_triage_x"), in: documents), "phone_saber_triage_x")
        XCTAssertEqual(DebugBundleTransfer.storedPath(for: URL(fileURLWithPath: "/tmp/other"), in: documents), "/tmp/other")
    }

    #endif

    // MARK: End to end (synthetic frames → bundle on disk)

    func testRecorderWritesAValidBundleAroundAConfusionTrigger() throws {
        #if AUTOCLIP_STANDALONE
        if try DebugRecordingStorageProbe.availableCapacity(at: FileManager.default.temporaryDirectory) == 0 {
            throw XCTSkip("ホストsandboxで空き容量APIが0を返すため保存ガードに阻まれる。ガードは維持する。")
        }
        #endif
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("autoclip-tests-\(UUID().uuidString)")
        defer { try? FileManager.default.removeItem(at: directory) }
        let recorder = AutoClipRecorder(directory: directory, enabled: true, clipNamer: { "20261019_120000_000" })
        let saved = expectation(description: "clip saved")
        var savedURL: URL?
        recorder.onClipSaved = { result in
            switch result {
            case .success(let url): savedURL = url
            case .failure(let error): XCTFail("clip writer: \(error)")
            }
            saved.fulfill()
        }
        let width = 480, height = 640
        var pixels = [UInt8](repeating: 0, count: width * height * 4)
        let saber = Self.candidate((100, 100), (100, 300), score: 120)
        let cone = Self.candidate((400, 560), (420, 620), score: 90, source: "core-halo")
        let fps = 60.0
        pixels.withUnsafeMutableBufferPointer { buffer in
            for frame in 0..<480 {   // 8 s at 60 fps; the cone becomes eligible at 4 s
                let base = UnsafePointer(buffer.baseAddress!)
                buffer[(frame % height) * width * 4 + 2] = UInt8(frame % 256)   // vary pixels a little
                let time = 100 + Double(frame) / fps
                let candidates = frame >= 240 ? [saber, cone] : [saber]
                let analysis = SaberFrameAnalysis(candidates: [.red: candidates, .blue: []],
                                                  selected: [.red: saber.endpoints])
                recorder.observe(ObservedFrame(baseAddress: base, width: width, height: height,
                                               bytesPerRow: width * 4, sequence: UInt64(frame + 1), timestamp: time,
                                               processingSeconds: 0.012, analysis: analysis,
                                               ignoreZones: [:], playArea: nil, captureFPS: 60))
            }
        }
        wait(for: [saved], timeout: 30)
        let bundle = try XCTUnwrap(savedURL)
        XCTAssertEqual(bundle.lastPathComponent, "phone_saber_triage_autoclip_20261019_120000_000_confusion_red")
        let summary = try XCTUnwrap(JSONSerialization.jsonObject(
            with: Data(contentsOf: bundle.appendingPathComponent("summary.json"))) as? [String: Any])
        XCTAssertEqual(summary["summaryScope"] as? String, "autoClip")
        XCTAssertEqual(summary["formatVersion"] as? Int, 1)
        let images = try XCTUnwrap(summary["images"] as? [[String: Any]])
        XCTAssertEqual(summary["selectedImageCount"] as? Int, images.count)
        XCTAssertLessThanOrEqual(images.count, 20)
        XCTAssertEqual(images.filter { $0["role"] as? String == "event" }.count, 3)
        XCTAssertGreaterThanOrEqual(images.filter { $0["role"] as? String == "context" }.count, 14)
        for image in images {
            let path = try XCTUnwrap(image["path"] as? String)
            let context = try XCTUnwrap(image["frameContextPath"] as? String)
            let png = try Data(contentsOf: bundle.appendingPathComponent(path))
            XCTAssertEqual(Array(png.prefix(8)), [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
            let contextJSON = try XCTUnwrap(JSONSerialization.jsonObject(
                with: Data(contentsOf: bundle.appendingPathComponent(context))) as? [String: Any])
            XCTAssertEqual((contextJSON["frames"] as? [Any])?.count, 1)
            XCTAssertEqual(image["color"] as? String, "red")
        }
        let clip = try XCTUnwrap(summary["autoClip"] as? [String: Any])
        let trigger = try XCTUnwrap(clip["trigger"] as? [String: Any])
        XCTAssertEqual(trigger["kind"] as? String, "confusion")
        XCTAssertEqual(trigger["frameID"] as? Int, 243)      // third frame with the cone
        XCTAssertEqual(trigger["competitor"] as? [Int], [400, 560, 420, 620])
        let frames = try XCTUnwrap(clip["frames"] as? [[String: Any]])
        XCTAssertTrue((299...301).contains(frames.count), "3 s before + 2 s after at 60 fps: \(frames.count)")
        let last = try XCTUnwrap(frames.last?["red"] as? [String: Any])
        XCTAssertEqual(last["k"] as? Int, 1)
        XCTAssertEqual((last["c"] as? [[Any]])?.count, 2)
        let data = try Data(contentsOf: bundle.appendingPathComponent("summary.json"))
        XCTAssertLessThan(data.count, 512 * 1024)
    }
}
