import CoreGraphics
import CoreVideo
import Foundation
import ImageIO
import XCTest
#if !AUTOCLIP_STANDALONE
@testable import PhoneSaberSender
#endif

/// Auto clip (AutoClip.swift): pure trigger rules, rate limiting, suspend policy,
/// PC commands, deferred upload and one synthetic clip written end to end.
final class AutoClipTests: XCTestCase {
    func testAutoISOReadsDimBladeWithoutChangingRecognition() throws {
        let width = 480, height = 640
        for color in [SaberColor.red, .blue] {
            var pixels = [UInt8](repeating: 4, count: width * height * 4)
            for y in 200...202 { for x in 200...248 {
                let offset = (y * width + x) * 4
                pixels[offset] = color == .blue ? 146 : 4
                pixels[offset + 1] = 8
                pixels[offset + 2] = color == .red ? 146 : 4
            } }
            func analyze(_ evidence: Bool) -> SaberFrameAnalysis {
                analyzeSabers(in: pixels, width: width, height: height, bytesPerRow: width * 4,
                    redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(),
                    collectObserverEvidence: evidence,
                    shutterEmitter: ShutterEmitterMode(shutter: 2000, iso: 160, enabled: true))
            }
            let off = analyze(false), on = analyze(true)
            // 自動ISO ONで追加取得する証拠が、候補・採点・選択結果を変えないことも確認する。
            for key in [SaberColor.red, .blue] {
                XCTAssertEqual(off.selected[key]?.0, on.selected[key]?.0)
                XCTAssertEqual(off.selected[key]?.1, on.selected[key]?.1)
                let a = off.candidates[key] ?? [], b = on.candidates[key] ?? []
                XCTAssertEqual(a.count, b.count)
                for (lhs, rhs) in zip(a, b) {
                    XCTAssertEqual(lhs.endpoints.0, rhs.endpoints.0); XCTAssertEqual(lhs.endpoints.1, rhs.endpoints.1)
                    XCTAssertEqual(lhs.score.bitPattern, rhs.score.bitPattern)
                    XCTAssertEqual(lhs.isEmitterEligible, rhs.isEmitterEligible)
                }
            }
            XCTAssertNotNil(on.selected[color])
            let frame = FrameProcessor.exposureFrame(on, width: width, height: height, zones: [:], area: nil)
            var controller = AutoExposureController()
            var changes: [AutoExposureController.Change] = []
            for i in 0...165 {
                if let change = controller.observe(time: Double(i) / 30, background: 4, ceilingISO: 160,
                    minISO: 32, frame: frame) { changes.append(change) }
            }
            XCTAssertEqual(changes.count, 1)
            XCTAssertEqual(changes.first?.reason, "blade-weak"); XCTAssertEqual(changes.first?.to, 200)
        }
    }

    func testAutoISOFrameUsesActualWinnerAndDistantSelectableEvidence() throws {
        let winner = Self.candidate((100, 100), (100, 300))
        let duplicate = Self.candidate((105, 100), (105, 300))
        let far = Self.candidate((400, 100), (400, 300))
        var ineligible = Self.candidate((600, 100), (600, 300)); ineligible.isEmitterEligible = false
        let list = [far, duplicate, winner, ineligible]
        var analysis = SaberFrameAnalysis(candidates: [.blue: list], selected: [.blue: winner.endpoints])
        let frame = FrameProcessor.exposureFrame(analysis, width: 800, height: 640, zones: [:], area: nil)
        XCTAssertFalse(frame.colors[0].selected)
        let color = frame.colors[1]
        XCTAssertTrue(color.selected); XCTAssertEqual(color.peakValue, winner.peakValue)
        XCTAssertEqual(color.highValueRatio, winner.highValueRatio); XCTAssertEqual(color.meanValue, winner.meanValue)
        XCTAssertEqual(color.colorPurity, winner.meanColorPurity)
        XCTAssertEqual(color.distantEligible, 1); XCTAssertEqual(color.competitors, 1)
        let zone = SaberIgnoreZone(minX: 0.45, minY: 0.1, maxX: 0.55, maxY: 0.6)
        XCTAssertEqual(FrameProcessor.exposureFrame(analysis, width: 800, height: 640, zones: [.blue: [zone]], area: nil).colors[1].competitors, 0)
        analysis = SaberFrameAnalysis(candidates: [.blue: list], selected: [:], trackLockSuppressed: [.blue])
        XCTAssertFalse(FrameProcessor.exposureFrame(analysis, width: 800, height: 640, zones: [:], area: nil).colors[1].selected)
        // 保存用は最後の変更と、変更しなかった競合窓を別々に保持する。
        let saved = AutoClipAutoExposure(enabled: true, ceilingISO: 400, initialISO: 250, appliedISO: 320,
            lastChangeTime: 5, lastChangeReason: "blade-weak", lastChangeMetrics: ["weak": 0.4, "lost": 0.1],
            lastDecisionReason: "conflict", windowMetrics: ["distract": 0.3])
        let json = try XCTUnwrap(try JSONSerialization.jsonObject(with: JSONSerialization.data(withJSONObject: saved.jsonObject)) as? [String: Any])
        XCTAssertEqual(json["maximumAutomaticISO"] as? Int, 400)
        XCTAssertEqual(json["lastChangeReason"] as? String, "blade-weak")
        XCTAssertEqual((json["lastChangeMetrics"] as? [String: Double])?["weak"], 0.4)
        XCTAssertEqual(json["lastDecisionReason"] as? String, "conflict")
        XCTAssertEqual((json["windowMetrics"] as? [String: Double])?["distract"], 0.3)
    }

    func testLegacyIdentityAndIgnoreZoneDecode() throws {
        let legacy = #"{"deviceModel":"iPhone14,5","deviceID":"old","appVersion":"1.0","appBuild":"20261024","station":"A","destination":"Mac","manualIPOverride":false}"#
        let phone = try JSONDecoder().decode(PhoneSaberIdentity.self, from: Data(legacy.utf8))
        for key in ["gitCommit", "gitDirty", "buildTime"] {
            XCTAssertEqual(phone.jsonObject[key] as? String, "unknown")
        }
        XCTAssertNil(phone.gitCommit)
        let rectangle = #"{"minX":0.1,"minY":0.2,"maxX":0.8,"maxY":0.9}"#
        let zone = try JSONDecoder().decode(SaberIgnoreZone.self, from: Data(rectangle.utf8))
        XCTAssertNil(zone.objectEndpoints)
        XCTAssertNil(zone.objects)
        XCTAssertTrue(zone.ignores((P(x: 20, y: 30), P(x: 20, y: 70)), width: 100, height: 100))
    }

    func testExclusionReasonsUseCompetitionPredicateAndSerialize() throws {
        let ignored = Self.candidate((20, 30), (20, 70))
        let outside = Self.candidate((90, 30), (90, 70))
        var ineligible = ignored
        ineligible.isEmitterEligible = false
        let inside = Self.candidate((50, 30), (50, 70))
        let zone = SaberIgnoreZone(minX: 0.1, minY: 0.2, maxX: 0.3, maxY: 0.8)
        let area = SaberPlayArea(minX: 0.1, minY: 0.1, maxX: 0.8, maxY: 0.9)
        let list = [ignored, outside, ineligible, inside]
        let expected = ["ignore:0,1", "outsidePlayArea", "ineligible", "none"]
        for (candidate, reason) in zip(list, expected) {
            let result = AutoClipCompetition.exclusionReason(candidate, width: 100, height: 100,
                zones: [zone, zone], playArea: area)
            XCTAssertEqual(result ?? "none", reason)
            XCTAssertEqual(AutoClipCompetition.selectable(candidate, width: 100, height: 100,
                zones: [zone, zone], playArea: area), result == nil)
        }
        let buffers = AutoClipBuffers(width: 100, height: 100, captureFPS: 30)
        let analysis = SaberFrameAnalysis(candidates: [.red: list], selected: [:])
        var pixel: UInt8 = 0
        withUnsafePointer(to: &pixel) { base in
            buffers.storeRecord(ObservedFrame(baseAddress: base, width: 100, height: 100, bytesPerRow: 400,
                sequence: 1, timestamp: 1, processingSeconds: 0, analysis: analysis,
                ignoreZones: [.red: [zone, zone]], playArea: area, captureFPS: 30),
                red: AutoClipCompetition(), blue: AutoClipCompetition())
        }
        let capture = AutoClipCapture(clipID: "reasons", trigger: AutoClipTriggerEvent(kind: .manual, frameID: 1, timestamp: 1),
            buffers: buffers, windowStart: 0, windowEnd: 2, captureFPS: 30,
            environmentAtTrigger: AutoClipEnvironment(), environmentAtSave: AutoClipEnvironment(),
            ignoreZones: [.red: [zone, zone]], playArea: area, sessionClipIndex: 1, rejectedTriggers: [:])
        let content = AutoClipBundleContent(capture: capture, slots: [0])
        var sources: [String] = []
        let entry = content.frameEntries(level: 0, sources: &sources)[0]["red"] as! [String: Any]
        let column = AutoClipBundleContent.candidateColumns.firstIndex(of: "exclusionReason")!
        for row in entry["c"] as! [[Any]] {
            XCTAssertEqual(row[column] as? String, expected[row[0] as! Int])
        }
        let frame = (content.frameContext(frameID: 1, sessionID: "reasons")["frames"] as! [[String: Any]])[0]
        for candidate in (frame["red"] as! [String: Any])["autoClipCandidates"] as! [[String: Any]] {
            XCTAssertEqual(candidate["exclusionReason"] as? String, expected[candidate["index"] as! Int])
        }
    }

    func testIgnoreZoneDetailsKeepShapesAndBoundTotalObjects() throws {
        let buffers = AutoClipBuffers(width: 100, height: 100, captureFPS: 30)
        let endpoints = SaberIgnoreZone.ObjectEndpoints(x1: 0.123456789, y1: 0.2, x2: 0.3, y2: 0.4)
        var zone = SaberIgnoreZone(minX: 0.1, minY: 0.2, maxX: 0.8, maxY: 0.9)
        zone.objectEndpoints = endpoints
        zone.objects = Array(repeating: endpoints, count: 40)
        let oldZone = SaberIgnoreZone(minX: 0.1, minY: 0.2, maxX: 0.8, maxY: 0.9)
        let capture = AutoClipCapture(clipID: "shapes", trigger: AutoClipTriggerEvent(kind: .manual, frameID: 1, timestamp: 1),
            buffers: buffers, windowStart: 0, windowEnd: 2, captureFPS: 30,
            environmentAtTrigger: AutoClipEnvironment(), environmentAtSave: AutoClipEnvironment(),
            ignoreZones: [.red: [zone, zone, zone], .blue: [zone, zone, oldZone]], playArea: nil,
            sessionClipIndex: 1, rejectedTriggers: [:])
        let content = AutoClipBundleContent(capture: capture, slots: [])
        let json = try JSONSerialization.jsonObject(with: content.summaryData(images: [])) as! [String: Any]
        let clip = json["autoClip"] as! [String: Any]
        XCTAssertEqual((clip["ignoreZones"] as! [String: Any])["red"] as? [[Double]], Array(repeating: [0.1, 0.2, 0.8, 0.9], count: 3))
        let details = clip["ignoreZoneDetails"] as! [String: Any]
        XCTAssertEqual(details["objectCount"] as? Int, 200)
        XCTAssertEqual(details["truncated"] as? Int, 72)
        let red = details["red"] as! [[String: Any]], blue = details["blue"] as! [[String: Any]]
        XCTAssertEqual((red[0]["objects"] as? [[Double]])?.count, 32)
        XCTAssertEqual((red[0]["objects"] as? [[Double]])?.first?.first, endpoints.x1)
        XCTAssertEqual(red[0]["objectEndpoints"] as? [Double], [endpoints.x1, endpoints.y1, endpoints.x2, endpoints.y2])
        XCTAssertEqual(red[0]["truncated"] as? Int, 8)
        XCTAssertEqual((blue[1]["objects"] as? [[Double]])?.count, 0)
        XCTAssertEqual(blue[1]["truncated"] as? Int, 40)
        XCTAssertTrue(blue[2]["objects"] is NSNull)
        XCTAssertTrue(blue[2]["objectEndpoints"] is NSNull)
        XCTAssertNil(content.frameContext(frameID: 1, sessionID: "shapes")["ignoreZoneDetails"])
    }

    func testEmitterLimitRetainsTerminalDecisionsInsteadOfIntermediatePasses() throws {
        for color in [SaberColor.red, .blue] {
            let width = 240, height = 160
            var bytes = [UInt8](repeating: 8, count: width * height * 4)
            for n in 0...SaberComponentDiagnostics.limit {
                for y in 30...31 { for x in (10 + n * 16)...(19 + n * 16) {
                    let i = (y * width + x) * 4
                    bytes[i] = color == .blue ? 128 : 12
                    bytes[i+1] = 24; bytes[i+2] = color == .red ? 128 : 12
                } }
            }
            let analysis = analyzeSabers(in: bytes, width: width, height: height, bytesPerRow: width * 4,
                redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(), sampleStep: 1,
                collectComponentDiagnostics: true, shutterEmitter: ShutterEmitterMode(shutter: 2000, iso: 250, enabled: true))
            let diagnostic = try XCTUnwrap(analysis.componentDiagnostics?[color])
            XCTAssertEqual(diagnostic.rows.count, SaberComponentDiagnostics.limit)
            XCTAssertEqual(diagnostic.omitted, 1)
            XCTAssertTrue(diagnostic.rows.allSatisfy { $0.stage == "emitter" && $0.reason == "passed" })
        }
    }

    func testStationaryDiagnosticsPreserveBothColorTrackHistories() throws {
        for color in [SaberColor.red, .blue] {
            let off = SaberTrackLock(), on = SaberTrackLock()
            let still = Self.candidate((100, 40), (100, 130), score: 100)
            var sawStationary = false
            for frame in 0..<200 {
                let now = Double(frame) / 60
                var candidates = [still]
                if frame >= 30 && frame < 180 {
                    let i = frame - 30
                    let x = 300 + (i % 40 < 20 ? i % 20 : 20 - i % 20) * 12
                    candidates.append(Self.candidate((x, 250), (x, 330), score: 99))
                }
                let diagnostics = SaberComponentDiagnostics()
                let a = off.select(candidates, color: color, width: 640, height: 480, now: now)
                let b = on.select(candidates, color: color, width: 640, height: 480, now: now,
                                  componentDiagnostics: diagnostics)
                XCTAssertEqual(a?.endpoints.0, b?.endpoints.0)
                XCTAssertEqual(a?.endpoints.1, b?.endpoints.1)
                XCTAssertEqual(a?.score.bitPattern, b?.score.bitPattern)
                if let row = diagnostics.rows.first(where: { $0.reason == "stationary" }) {
                    sawStationary = true
                    XCTAssertEqual(row.stage, "postGeneration")
                    // fixtureの候補bboxは端点の外側に3pxの幅を持つ。
                    XCTAssertEqual(row.bbox, [97, 37, 103, 133])
                }
            }
            XCTAssertTrue(sawStationary)
        }
    }

    func testPostGenerationDiagnosticsUseActualIgnoreAreaAndEligibilityDecisions() throws {
        let width = 240, height = 160
        var bytes = [UInt8](repeating: 8, count: width * height * 4)
        func rectangle(_ x1: Int, _ x2: Int, _ y1: Int, _ y2: Int, _ rgb: (UInt8, UInt8, UInt8)) {
            for y in y1...y2 { for x in x1...x2 {
                let i = (y * width + x) * 4
                bytes[i] = rgb.2; bytes[i+1] = rgb.1; bytes[i+2] = rgb.0; bytes[i+3] = 255
            } }
        }
        rectangle(20, 100, 20, 23, (128, 24, 12))
        let mode = ShutterEmitterMode(shutter: 2000, iso: 250, enabled: true)
        for reason in ["ignore", "playArea"] {
            let zones: [SaberColor: [SaberIgnoreZone]] = reason == "ignore"
                ? [.red: [SaberIgnoreZone(minX: 0, minY: 0, maxX: 1, maxY: 1)]] : [:]
            let area = reason == "playArea" ? SaberPlayArea(minX: 0.7, minY: 0.7, maxX: 1, maxY: 1) : nil
            func run(_ on: Bool) -> SaberFrameAnalysis {
                analyzeSabers(in: bytes, width: width, height: height, bytesPerRow: width * 4,
                    redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(),
                    collectComponentDiagnostics: on, ignoreZones: zones, playArea: area, shutterEmitter: mode)
            }
            let off = run(false), on = run(true)
            XCTAssertNil(off.selected[.red]); XCTAssertNil(on.selected[.red])
            let row = try XCTUnwrap(on.componentDiagnostics?[.red]?.rows.first { $0.reason == reason })
            XCTAssertEqual(row.stage, "postGeneration")
            XCTAssertEqual(row.bbox, [10, 10, 50, 11])
            XCTAssertEqual(off.candidates[.red]?.map { $0.score.bitPattern }, on.candidates[.red]?.map { $0.score.bitPattern })
            XCTAssertTrue(on.candidates[.red]?.contains { $0.isEmitterEligible } == true)
        }
        // 暗い青と離れた明るい実刃。mainにないauto28の反射抑制を導入しない。
        bytes = [UInt8](repeating: 8, count: bytes.count)
        for y in 50...60 { for x in 40...76 {
            if (x-58)*(x-58)*25 + (y-55)*(y-55)*324 <= 324*25 {
                rectangle(x, x, y, y, (40, 50, 170))
            }
        } }
        rectangle(130, 205, 95, 99, (8, 12, 255))
        func blue(_ on: Bool) -> SaberFrameAnalysis {
            analyzeSabers(in: bytes, width: width, height: height, bytesPerRow: width * 4,
                redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(), sampleStep: 1,
                collectComponentDiagnostics: on, shutterEmitter: mode)
        }
        let off = blue(false), on = blue(true)
        XCTAssertEqual(off.selected[.blue]?.0, on.selected[.blue]?.0)
        XCTAssertEqual(off.selected[.blue]?.1, on.selected[.blue]?.1)
        XCTAssertEqual(off.candidates[.blue]?.map { $0.score.bitPattern }, on.candidates[.blue]?.map { $0.score.bitPattern })
        XCTAssertEqual(off.candidates[.blue]?.map { $0.isEmitterEligible }, on.candidates[.blue]?.map { $0.isEmitterEligible })
        let blueDiagnostics = try XCTUnwrap(on.componentDiagnostics?[.blue])
        let post = blueDiagnostics.rows.filter { $0.stage == "postGeneration" }
        XCTAssertFalse(post.contains { $0.reason == "blueSurface" })
        XCTAssertTrue(on.candidates[.blue]?.contains {
            $0.source == "shutter-color-emitter" && $0.peakValue == 170 && $0.isEmitterEligible
        } == true)
        XCTAssertTrue(post.contains { $0.reason == "ineligible" })
        // 上限にも重複行を使わず、実際の不適格候補数だけを記録する。
        XCTAssertEqual(post.count, on.candidates[.blue]?.filter { !$0.isEmitterEligible }.count)
        XCTAssertEqual(blueDiagnostics.omitted, 0)
    }

    func testComponentRejectionReasonsAndBoundedCollection() throws {
        func check(_ points: [PixelPoint], _ reason: String) {
            var mask = [UInt8](repeating: 0, count: 100 * 100)
            for p in points { mask[p.y * 100 + p.x] = 1 }
            let diagnostics = SaberComponentDiagnostics()
            _ = saberCandidates(in: mask, width: 100, height: 100, componentDiagnostics: diagnostics)
            XCTAssertEqual(diagnostics.rows.first?.reason, reason)
            XCTAssertEqual(diagnostics.rows.first?.pixels, points.count)
            XCTAssertTrue(diagnostics.rows.allSatisfy { $0.length.isFinite && $0.extent.isFinite })
        }
        func rectangle(_ w: Int, _ h: Int) -> [PixelPoint] {
            (10..<(10+h)).flatMap { y in (10..<(10+w)).map { PixelPoint(x: $0, y: y) } }
        }
        check([P(x: 10, y: 10)], "area")
        check(rectangle(50, 50), "areaRatio")
        check(rectangle(3, 3), "length")
        check(rectangle(8, 8), "aspect")
        check((10...90).map { P(x: $0, y: 10) } + (11...90).map { P(x: 10, y: $0) }, "extent")
        check(rectangle(30, 3), "passed")
        let diagnostics = SaberComponentDiagnostics()
        var mask = [UInt8](repeating: 0, count: 10000)
        for x in stride(from: 0, to: 90, by: 3) { mask[10 * 100 + x] = 1 }
        _ = saberCandidates(in: mask, width: 100, height: 100, componentDiagnostics: diagnostics)
        XCTAssertEqual(diagnostics.rows.count, SaberComponentDiagnostics.limit)
        XCTAssertEqual(diagnostics.omitted, 30 - SaberComponentDiagnostics.limit)
    }

    func testEmitterDiagnosticReasonsAndRecognitionIdentity() throws {
        let width = 160, height = 240
        let mode = ShutterEmitterMode(shutter: 2000, iso: 250, enabled: true)
        for color in [SaberColor.red, .blue] {
            for (length, thickness, reason) in [(5, 1, "area"), (7, 2, "emitterAspect"),
                (90, 12, "emitterWidth"), (166, 7, "emitterArea"), (100, 3, "passed")] {
                // 面積比境界だけ長辺方向へ配置する。
                var bytes = [UInt8](repeating: 8, count: width * height * 4)
                for y in 20..<(20+length) { for x in 20..<(20+thickness) {
                    let i = (y * width + x) * 4
                    bytes[i] = color == .blue ? 128 : 12
                    bytes[i+1] = 24; bytes[i+2] = color == .red ? 128 : 12
                } }
                bytes.withUnsafeBufferPointer { b in
                    func run(_ on: Bool) -> SaberFrameAnalysis {
                        analyzeSabers(baseAddress: b.baseAddress!, width: width, height: height,
                            bytesPerRow: width * 4, redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(),
                            sampleStep: 1, collectComponentDiagnostics: on, shutterEmitter: mode)
                    }
                    let off = run(false), on = run(true)
                    let rows = on.componentDiagnostics?[color]?.rows ?? []
                    XCTAssertTrue(rows.contains { $0.reason == reason }, "\(color) \(length)x\(thickness): \(rows)")
                    XCTAssertNil(off.componentDiagnostics)
                    for c in [SaberColor.red, .blue] {
                        let a = off.candidates[c] ?? [], b = on.candidates[c] ?? []
                        XCTAssertEqual(a.count, b.count)
                        for (a, b) in zip(a, b) {
                            XCTAssertEqual(a.score.bitPattern, b.score.bitPattern)
                            XCTAssertEqual(a.isEmitterEligible, b.isEmitterEligible)
                            XCTAssertEqual(a.endpoints.0, b.endpoints.0); XCTAssertEqual(a.endpoints.1, b.endpoints.1)
                        }
                        XCTAssertEqual(off.selected[c]?.0, on.selected[c]?.0)
                        XCTAssertEqual(off.selected[c]?.1, on.selected[c]?.1)
                    }
                }
            }
        }
    }

    func testMainPeakFloorAndMagentaDiagnosticsPreserveRecognition() throws {
        let width = 240, height = 160
        let mode = ShutterEmitterMode(shutter: 2000, iso: 250, enabled: true)
        let cases: [(SaberColor, UInt8, (UInt8, UInt8, UInt8), String, String?)] = [
            (.red, 40, (160, 24, 12), "emitterPeak", nil),
            (.blue, 40, (12, 24, 160), "emitterPeak", nil),
            (.red, 40, (220, 24, 12), "passed", "shutter-color-emitter"),
            (.blue, 40, (12, 24, 220), "passed", "shutter-color-emitter"),
            (.red, 8, (220, 30, 100), "passed", "shutter-magenta-emitter")
        ]
        for (color, background, rgb, reason, source) in cases {
            var bytes = [UInt8](repeating: background, count: width * height * 4)
            for y in 30...32 { for x in 30...109 {
                let i = (y * width + x) * 4
                bytes[i] = rgb.2; bytes[i+1] = rgb.1; bytes[i+2] = rgb.0; bytes[i+3] = 255
            } }
            func run(_ on: Bool) -> SaberFrameAnalysis {
                analyzeSabers(in: bytes, width: width, height: height, bytesPerRow: width * 4,
                    redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(), sampleStep: 1,
                    collectComponentDiagnostics: on, shutterEmitter: mode)
            }
            let off = run(false), on = run(true)
            let diagnostic = try XCTUnwrap(on.componentDiagnostics?[color])
            XCTAssertTrue(diagnostic.rows.contains { $0.stage == "emitter" && $0.reason == reason })
            XCTAssertEqual(diagnostic.snapshot.json()["state"] as? String, "collected")
            if let source {
                XCTAssertTrue(on.candidates[color]?.contains { $0.source == source && $0.isEmitterEligible } == true)
            } else {
                XCTAssertFalse(on.candidates[color]?.contains { $0.source == "shutter-color-emitter" } == true)
            }
            for c in [SaberColor.red, .blue] {
                XCTAssertEqual(off.selected[c]?.0, on.selected[c]?.0)
                XCTAssertEqual(off.selected[c]?.1, on.selected[c]?.1)
                let a = off.candidates[c] ?? [], b = on.candidates[c] ?? []
                XCTAssertEqual(a.count, b.count)
                for (a, b) in zip(a, b) {
                    XCTAssertEqual(a.score.bitPattern, b.score.bitPattern)
                    XCTAssertEqual(a.isEmitterEligible, b.isEmitterEligible)
                    XCTAssertEqual(a.endpoints.0, b.endpoints.0)
                    XCTAssertEqual(a.endpoints.1, b.endpoints.1)
                }
            }
        }
    }

    func testComponentCollectionScheduleLegacyStateAndSavedFrameIdentity() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: directory) }
        let off = AutoClipRecorder(directory: directory, enabled: true, componentDiagnostics: false)
        XCTAssertFalse(off.wantsComponentDiagnostics(timestamp: 1))
        let recorder = AutoClipRecorder(directory: directory, enabled: true, componentDiagnostics: true)
        XCTAssertTrue(recorder.wantsComponentDiagnostics(timestamp: 1))
        XCTAssertEqual(SaberComponentDiagnosticSnapshot.state(in: [:]), "unknown")
        let defaults = UserDefaults(suiteName: UUID().uuidString)!
        XCTAssertFalse(AutoClipSettings.storedComponentDiagnostics(defaults))
        let pixels = [UInt8](repeating: 0, count: 16 * 16 * 4)
        let buffers = AutoClipBuffers(width: 16, height: 16, captureFPS: 30)
        pixels.withUnsafeBufferPointer { b in
            let analysis = analyzeSabers(baseAddress: b.baseAddress!, width: 16, height: 16, bytesPerRow: 64,
                redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(), collectComponentDiagnostics: true)
            XCTAssertEqual(analysis.componentDiagnostics?[.red]?.snapshot.json()["state"] as? String, "maskEmpty")
            let frame = ObservedFrame(baseAddress: b.baseAddress!, width: 16, height: 16, bytesPerRow: 64,
                sequence: 47, timestamp: 1, processingSeconds: 0, analysis: analysis,
                ignoreZones: [:], playArea: nil, captureFPS: 30)
            buffers.storeRecord(frame, red: AutoClipCompetition(), blue: AutoClipCompetition())
            buffers.storeEvent(frame)
            recorder.observe(frame)
            XCTAssertFalse(recorder.wantsComponentDiagnostics(timestamp: 1.1))
            XCTAssertTrue(recorder.wantsComponentDiagnostics(timestamp: 1.31))
        }
        let capture = AutoClipCapture(clipID: "diagnostics", trigger: event(.manual, at: 1), buffers: buffers,
            windowStart: 0, windowEnd: 2, captureFPS: 30, environmentAtTrigger: AutoClipEnvironment(),
            environmentAtSave: AutoClipEnvironment(), ignoreZones: [:], playArea: nil,
            sessionClipIndex: 1, rejectedTriggers: [:], componentDiagnosticsEnabled: true)
        let content = AutoClipBundleContent(capture: capture, slots: [0])
        try content.validateFrameIdentity()
        let data = try JSONSerialization.data(withJSONObject: content.frameContext(frameID: 47, sessionID: "diagnostics"))
        XCTAssertLessThan(data.count, 32 * 1024)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let path = directory.appendingPathComponent("frame_47.json")
        try data.write(to: path)
        let saved = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: path)) as? [String: Any])
        let frame = try XCTUnwrap((saved["frames"] as? [[String: Any]])?.first)
        XCTAssertEqual(frame["frameID"] as? Int, 47)
        XCTAssertEqual(frame["timestamp"] as? Double, 1)
        XCTAssertEqual(SaberComponentDiagnosticSnapshot.state(in: frame["red"] as? [String: Any] ?? [:]), "maskEmpty")
        let snapshot = try XCTUnwrap(buffers.frames[0].componentDiagnostics?[.red])
        XCTAssertEqual(try JSONDecoder().decode(SaberComponentDiagnosticSnapshot.self,
            from: JSONEncoder().encode(snapshot)), snapshot)
    }

    func testComponentPreflightBoundAndPredictableGapTrigger() throws {
        let buffers = AutoClipBuffers(width: 480, height: 640, captureFPS: 60)
        let diagnostics = SaberComponentDiagnostics()
        for path in ["color-mask", "shutter-color-emitter"] {
            for i in 0..<20 {
                diagnostics.append(SaberComponentDiagnostic(path: path, stage: "component", reason: "aspect",
                    bbox: [i, i, 240, 320], pixels: 500, length: 123.45678901234567,
                    meanWidth: 12.345678901234567, aspect: 1.2345678901234567, extent: 0.12345678901234567))
            }
        }
        for _ in 0..<20 {
            diagnostics.append(SaberComponentDiagnostic(path: "shutter-color-emitter", stage: "postGeneration", reason: "stationary",
                bbox: [0, 0, 240, 320], pixels: 500, length: 123.45678901234567,
                meanWidth: 12.345678901234567, aspect: 1.2345678901234567, extent: 0.12345678901234567))
        }
        XCTAssertEqual(diagnostics.rows.count, 3 * SaberComponentDiagnostics.limit)
        XCTAssertEqual(diagnostics.omitted, 60 - diagnostics.rows.count)
        diagnostics.maskPixels = 500
        var analysis = SaberFrameAnalysis(candidates: [.red: (0..<12).map { _ in Self.candidate((0, 0), (400, 500)) },
                                                       .blue: (0..<12).map { _ in Self.candidate((0, 0), (400, 500)) }], selected: [:])
        analysis.componentDiagnostics = [.red: diagnostics, .blue: diagnostics]
        var pixel: UInt8 = 0
        withUnsafePointer(to: &pixel) { b in
            buffers.storeRecord(ObservedFrame(baseAddress: b, width: 480, height: 640, bytesPerRow: 1920,
                sequence: 1, timestamp: 1, processingSeconds: 0, analysis: analysis,
                ignoreZones: [:], playArea: nil, captureFPS: 60), red: AutoClipCompetition(), blue: AutoClipCompetition())
        }
        let capture = AutoClipCapture(clipID: "bound", trigger: event(.manual, at: 1), buffers: buffers,
            windowStart: 0, windowEnd: 2, captureFPS: 60, environmentAtTrigger: AutoClipEnvironment(),
            environmentAtSave: AutoClipEnvironment(), ignoreZones: [:], playArea: nil,
            sessionClipIndex: 1, rejectedTriggers: [:], componentDiagnosticsEnabled: true)
        let content = AutoClipBundleContent(capture: capture, slots: [0])
        // 実アプリの上限は片色8zone。全zone一致の最長コードを24候補に入れても32KiB内。
        for i in 0..<24 {
            buffers.candidates[i].exclusionReason = "ignore:0,1,2,3,4,5,6,7"
            buffers.candidates[i].peakValue = 255
            buffers.candidates[i].emitterScore = 0.8765
            buffers.candidates[i].highValueRatio = 0.4567
            buffers.candidates[i].meanValue = 234.5
            buffers.candidates[i].nearSaturationPresent = true
            buffers.candidates[i].colorPurity = 0.5678
            buffers.candidates[i].componentArea = 480
            buffers.candidates[i].aspect = 12.3456
        }
        let data = try JSONSerialization.data(withJSONObject: content.frameContext(frameID: 1, sessionID: "bound"))
        XCTAssertLessThanOrEqual(data.count, 32 * 1024)
        print("component-preflight worst fixture bytes=\(data.count), rows/color=\(diagnostics.rows.count)")
        var detector = AutoClipTriggerDetector()
        let winner = (P(x: 10, y: 10), P(x: 100, y: 100))
        for i in 0...60 {
            _ = detector.observe(color: .red, sequence: UInt64(i+1), timestamp: Double(i)/60,
                winner: winner, competitorCount: 0, competitor: nil, width: 480, height: 640, otherSelected: true)
        }
        for i in 1...7 {
            XCTAssertFalse(detector.mayTriggerNextFrame)
            _ = detector.observe(color: .red, sequence: UInt64(61+i), timestamp: 1+Double(i)/60,
                winner: nil, competitorCount: 0, competitor: nil, width: 480, height: 640, otherSelected: true)
        }
        XCTAssertTrue(detector.mayTriggerNextFrame)
    }

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

    func testIgnoreZoneStoresAllThreeTubeCandidatesIncludingIneligible() throws {
        // 実録 20261022_113757 frame 8090。winner だけでは別形状が再び選ばれる。
        let candidates = [Self.candidate((18, 372), (48, 410), source: "core-halo"),
                          Self.candidate((12, 390), (34, 342)),
                          Self.candidate((18, 404), (28, 340), eligible: false, source: "color-close")]
        let zone = SaberIgnoreZone(around: candidates[0].endpoints, width: 480, height: 640,
                                   candidates: candidates)
        XCTAssertEqual(zone.objects?.count, 3)
        let restored = try JSONDecoder().decode(SaberIgnoreZone.self, from: JSONEncoder().encode(zone))
        XCTAssertEqual(restored, zone)
        for candidate in candidates {
            XCTAssertTrue(restored.ignores(candidate.endpoints, width: 480, height: 640))
            XCTAssertTrue(restored.ignores((candidate.endpoints.1, candidate.endpoints.0), width: 480, height: 640))
        }
        // 実録の flicker: 最寄りの登録両端から正規化距離 0.06068。
        XCTAssertTrue(restored.ignores(Self.candidate((12, 350), (72, 432)).endpoints, width: 480, height: 640))
        let crossing = Self.candidate((0, 440), (70, 345))
        XCTAssertTrue(zone.contains(crossing.endpoints.0, width: 480, height: 640))
        XCTAssertTrue(zone.contains(crossing.endpoints.1, width: 480, height: 640))
        XCTAssertTrue(AutoClipCompetition.selectable(crossing, width: 480, height: 640, zones: [zone], playArea: nil))
        var legacy = zone
        legacy.objects = nil
        let old = try JSONDecoder().decode(SaberIgnoreZone.self, from: JSONEncoder().encode(legacy))
        XCTAssertEqual(old.objectEndpoints, zone.objectEndpoints)
        XCTAssertTrue(old.ignores(candidates[0].endpoints, width: 480, height: 640))
    }

    func testIgnoreZoneObjectsAreBoundedDeduplicatedAndInsideRectangle() {
        let seed = Self.candidate((200, 200), (400, 400))
        let reversed = Self.candidate((400, 400), (200, 200), eligible: false)
        let outside = Self.candidate((200, 200), (900, 900))
        let many = (1...(SaberIgnoreZone.maximumObjects + 10)).map {
            Self.candidate((200 + $0 % 100, 200 + $0 / 100), (400, 400))
        }
        let zone = SaberIgnoreZone(around: seed.endpoints, width: 1000, height: 1000,
                                   candidates: [seed, reversed, outside] + many)
        XCTAssertEqual(zone.objects?.count, SaberIgnoreZone.maximumObjects)
        XCTAssertEqual(zone.objects?.first, zone.objectEndpoints)
        XCTAssertEqual(zone.objects?[1].x1, 0.201)
        XCTAssertFalse(zone.ignores(outside.endpoints, width: 1000, height: 1000))
    }

    func testIgnoreLearningClustersBridgeAndBoundsObjects() throws {
        var learner = SaberIgnoreLearner(startedAt: 1, colors: [.red])
        func add(_ candidates: [SaberCandidate], at now: Double = 1) {
            learner.add(SaberFrameAnalysis(candidates: [.red: candidates, .blue: candidates], selected: [:]),
                        width: 1000, height: 1000, now: now)
        }
        add([])
        XCTAssertTrue(learner.zones.isEmpty)
        add([Self.candidate((100, 100), (150, 150)), Self.candidate((350, 100), (400, 150))])
        XCTAssertEqual(learner.zones[.red]?.count, 2)
        // 後で両群に接する候補が出たら1群に結合。対象外の青は収集しない。
        add([Self.candidate((200, 100), (300, 150), eligible: false)])
        XCTAssertEqual(learner.zones[.red]?.count, 1)
        XCTAssertNil(learner.zones[.blue])
        let candidates = (0...(SaberIgnoreZone.maximumObjects + 20)).map {
            Self.candidate((100 + $0 % 200, 100 + $0 / 200), (400, 150), eligible: false)
        }
        add(candidates, at: 3.99)
        let zone = try XCTUnwrap(learner.zones[.red]?.first)
        XCTAssertEqual(zone.objects?.count, SaberIgnoreZone.maximumObjects)
        XCTAssertEqual(zone.minX, 0.05, accuracy: 1e-12)
        XCTAssertEqual(zone.maxX, 0.45, accuracy: 1e-12)
        for candidate in candidates {
            XCTAssertTrue(zone.ignores(candidate.endpoints, width: 1000, height: 1000))
        }
    }

    func testIgnoreSafetyRefusesRecentMotionAndMotionDuringLearning() {
        for color in [SaberColor.red, .blue] {
            for size in [480, 960] {
                let scale = size / 480
                let safety = SaberTrackLock()
                func frame(_ x: Int) -> SaberFrameAnalysis {
                    .init(candidates: [color: [Self.candidate((x * scale, 100 * scale),
                                                             (x * scale, 200 * scale))]], selected: [:])
                }
                _ = safety.observeIgnoreMotion(frame(100), width: size, height: size, now: 10)
                var learner = SaberIgnoreLearner(startedAt: 10, colors: [color])
                learner.add(frame(100), width: size, height: size, now: 10)
                XCTAssertFalse(learner.zones.isEmpty)
                XCTAssertTrue(safety.observeIgnoreMotion(frame(123), width: size, height: size, now: 10.05).isEmpty,
                              "24px未満の移動では拒否しない")
                let moving = safety.observeIgnoreMotion(frame(124), width: size, height: size, now: 10.1)
                XCTAssertEqual(moving, [color], "短辺480px換算24px以上の移動を拒否")
                var recent = SaberIgnoreLearner(startedAt: 10.4, colors: [color])
                recent.refuseMoving(safety.recentlyMovingColors(now: 10.4))
                recent.add(frame(124), width: size, height: size, now: 10.4)
                XCTAssertTrue(recent.movingBladeRefused, "開始前1.5秒以内の移動も拒否")
                XCTAssertTrue(recent.zones.isEmpty)
                learner.add(frame(124), width: size, height: size, now: 10.1, movingColors: moving)
                XCTAssertTrue(learner.movingBladeRefused)
                XCTAssertTrue(learner.zones.isEmpty, "移動前に集めた範囲も取り消す")
                for i in 2...18 {
                    _ = safety.observeIgnoreMotion(frame(124), width: size, height: size, now: 10 + Double(i) / 10)
                    if i <= 16 {
                        XCTAssertEqual(safety.recentlyMovingColors(now: 10 + Double(i) / 10), [color])
                    }
                }
                XCTAssertTrue(safety.recentlyMovingColors(now: 11.8).isEmpty, "1.5秒より前の移動は拒否しない")
                learner.add(frame(124), width: size, height: size, now: 11.8)
                XCTAssertTrue(learner.zones.isEmpty, "動きの期限後も拒否した学習を復活させない")
                XCTAssertTrue(safety.recentlyMovingColors(now: 12.1).isEmpty, "古い観測は使用しない")
                safety.reset()
                XCTAssertTrue(safety.recentlyMovingColors(now: 10.1).isEmpty)
            }
        }
    }

    func testIgnoreSafetyRegistersStationaryObjectsAndOnlyChecksRequestedColors() {
        for color in [SaberColor.red, .blue] {
            let safety = SaberTrackLock()
            var learner = SaberIgnoreLearner(startedAt: 10, colors: [color])
            for i in 0..<30 {
                let now = 10 + Double(i) / 10
                let x = 100 + i % 3
                let frame = SaberFrameAnalysis(candidates: [color: [Self.candidate((x, 100), (x, 200), eligible: false)]], selected: [:])
                let moving = safety.observeIgnoreMotion(frame, width: 480, height: 640, now: now)
                learner.add(frame, width: 480, height: 640, now: now, movingColors: moving)
            }
            XCTAssertFalse(learner.movingBladeRefused)
            XCTAssertEqual(learner.zones[color]?.count, 1, "eligibleでない静止物も従来通り登録")
            learner.refuseMoving([color == .red ? .blue : .red])
            XCTAssertFalse(learner.movingBladeRefused, "指定していない色の動きは影響させない")
            var both = SaberIgnoreLearner(startedAt: 10, colors: [.red, .blue])
            both.refuseMoving([color])
            XCTAssertTrue(both.movingBladeRefused, "bothでは片色の移動でも全登録を拒否")
        }
    }

    func testSaberCrossingTubeRectangleIsStillSelected() throws {
        let candidates = [Self.candidate((18, 372), (48, 410)), Self.candidate((12, 390), (34, 342)),
                          Self.candidate((18, 404), (28, 340), eligible: false)]
        let zone = SaberIgnoreZone(around: candidates[0].endpoints, width: 480, height: 640, candidates: candidates)
        for color in [SaberColor.red, .blue] {
            var bytes = [UInt8](repeating: 0, count: 480 * 640 * 4)
            for t in 0...95 {
                let x = Int((70.0 * Double(t) / 95).rounded()), y = 440 - t
                for dy in -2...2 { for dx in -2...2 where x + dx >= 0 {
                    let i = ((y + dy) * 480 + x + dx) * 4
                    bytes[i] = color == .blue ? 245 : 30
                    bytes[i + 1] = 45
                    bytes[i + 2] = color == .red ? 245 : 30
                    bytes[i + 3] = 255
                } }
            }
            let threshold = ColorThreshold(brightness: 140, dominance: 20)
            for lock in [nil, SaberTrackLock()] as [SaberTrackLock?] {
                let analysis = analyzeSabers(in: bytes, width: 480, height: 640, bytesPerRow: 480 * 4,
                    redThreshold: threshold, blueThreshold: threshold, ignoreZones: [color: [zone]],
                    trackLock: lock, now: 1)
                let selected = try XCTUnwrap(analysis.selected[color])
                XCTAssertTrue(zone.contains(selected.0, width: 480, height: 640))
                XCTAssertTrue(zone.contains(selected.1, width: 480, height: 640))
                XCTAssertFalse(zone.ignores(selected, width: 480, height: 640))
            }
        }
    }

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
        let identity = PhoneSaberIdentity(deviceModel: "iPhone16,2", deviceID: "test-device", appVersion: "1.0",
            appBuild: "20261024", station: "B", destination: "192.168.1.2:5005/5006", manualIPOverride: true,
            gitCommit: "abcdef123456", gitDirty: "true", buildTime: "2026-10-28T01:02:03Z")
        var winner = Self.candidate((100, 100), (100, 300))
        winner.observerEmitterInputs = SaberObserverEmitterInputs(emitterScore: 0.8,
                                                                 nearSaturationPresent: true, aspect: 12.5)
        let competitor = Self.candidate((400, 500), (400, 600))
        var analysis = SaberFrameAnalysis(candidates: [.red: [competitor, winner], .blue: [competitor]],
            selected: [.red: winner.endpoints], trackLockSuppressed: [.blue])
        analysis.backgroundMedian = 57
        let componentDiagnostics = SaberComponentDiagnostics()
        componentDiagnostics.maskPixels = 1
        componentDiagnostics.component([P(x: 4, y: 5)], path: "color-close", reason: "area")
        analysis.componentDiagnostics = [.red: componentDiagnostics]
        let buffers = AutoClipBuffers(width: 480, height: 640, captureFPS: 30)
        var pixel: UInt8 = 0
        withUnsafePointer(to: &pixel) { base in
            buffers.storeRecord(ObservedFrame(baseAddress: base, width: 480, height: 640, bytesPerRow: 1920,
                sequence: 1, timestamp: 1, processingSeconds: 0, analysis: analysis, ignoreZones: [:],
                playArea: nil, captureFPS: 30, appliedISO: 50),
                red: AutoClipCompetition.evaluate(analysis.candidates[.red]!, selected: analysis.selected[.red],
                    width: 480, height: 640, zones: [], playArea: nil), blue: AutoClipCompetition())
        }
        let capture = AutoClipCapture(clipID: "test", trigger: AutoClipTriggerEvent(kind: .manual, frameID: 1, timestamp: 1),
            buffers: buffers, windowStart: 0, windowEnd: 2, captureFPS: 30,
            environmentAtTrigger: AutoClipEnvironment(shutter: 1000, shutterISO: 64, shutterEmitter: true, autoExposure: AutoClipAutoExposure(enabled: true, ceilingISO: 64, appliedISO: 50, lastChangeTime: 123, lastChangeReason: "lower"), exposureDurationSeconds: 0.001, iso: 80.5, appVersion: identity.appVersion, identity: identity, signingExpiryRemainingHours: 49),
            environmentAtSave: AutoClipEnvironment(signingExpiryRemainingHours: 48),
            ignoreZones: [:], playArea: nil, sessionClipIndex: 1, rejectedTriggers: [:], componentDiagnosticsEnabled: true)
        let content = AutoClipBundleContent(capture: capture, slots: [0])
        let data = try content.summaryData(images: [])
        let summary = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        // 転送envelopeは互換v1、内部AutoClipのschemaだけv2。
        XCTAssertEqual(summary["formatVersion"] as? Int, 1)
        let clip = try XCTUnwrap(summary["autoClip"] as? [String: Any])
        XCTAssertEqual(clip["formatVersion"] as? Int, 2)
        XCTAssertEqual((clip["camera"] as? [String: Any])?["signingExpiryRemainingHours"] as? Int, 48)
        let camera = try XCTUnwrap(clip["camera"] as? [String: Any])
        XCTAssertEqual(camera["appVersion"] as? String, "1.0")
        let phone = try XCTUnwrap(clip["phone"] as? [String: Any])
        XCTAssertEqual(phone as NSDictionary, identity.jsonObject as NSDictionary)
        XCTAssertEqual(phone["gitCommit"] as? String, "abcdef123456")
        XCTAssertEqual(phone["gitDirty"] as? String, "true")
        XCTAssertEqual(phone["buildTime"] as? String, "2026-10-28T01:02:03Z")
        XCTAssertEqual(camera["shutter"] as? Int, 1000)
        XCTAssertEqual(camera["shutterISO"] as? Int, 64)
        XCTAssertEqual(camera["shutterEmitter"] as? Bool, true)
        XCTAssertEqual(camera["exposureDurationSeconds"] as? Double, 0.001)
        XCTAssertEqual(camera["iso"] as? Double, 80.5)
        let exposure = try XCTUnwrap(camera["autoExposure"] as? [String: Any])
        XCTAssertEqual(exposure["enabled"] as? Bool, true)
        XCTAssertEqual(exposure["ceilingISO"] as? Int, 64)
        XCTAssertEqual(exposure["appliedISO"] as? Double, 50)
        XCTAssertEqual(exposure["lastChangeHostTime"] as? Double, 123)
        XCTAssertEqual(exposure["lastChangeReason"] as? String, "lower")
        let frames = try XCTUnwrap(clip["frames"] as? [[String: Any]])
        XCTAssertEqual(frames.first?["backgroundMedian"] as? Int, 57)
        XCTAssertEqual(frames.first?["appliedISO"] as? Double, 50)
        let contextFrames = try XCTUnwrap(content.frameContext(frameID: 1, sessionID: "test")["frames"] as? [[String: Any]])
        XCTAssertEqual(contextFrames.first?["backgroundMedian"] as? Int, 57)
        XCTAssertEqual(contextFrames.first?["appliedISO"] as? Double, 50)
        let componentColor = try XCTUnwrap(contextFrames.first?["red"] as? [String: Any])
        let componentJSON = try XCTUnwrap(componentColor["components"] as? [String: Any])
        let componentRows = try XCTUnwrap(componentJSON["rows"] as? [[Any]])
        XCTAssertEqual(componentRows.first?[2] as? String, "area")
        XCTAssertEqual(componentRows.first?[7] as? Int, 1)
        XCTAssertEqual(SaberComponentDiagnosticSnapshot.state(in: contextFrames.first?["blue"] as? [String: Any] ?? [:]), "notCollected")
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
        let content = AutoClipBundleContent(capture: capture, slots: Array(0..<count))
        let data = try content.summaryData(images: [])
        XCTAssertLessThanOrEqual(data.count, AutoClipLimits.maximumSummaryBytes)
        let summary = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [String: Any])
        let clip = try XCTUnwrap(summary["autoClip"] as? [String: Any])
        XCTAssertTrue((clip["camera"] as? [String: Any])?["signingExpiryRemainingHours"] is NSNull)
        XCTAssertEqual((clip["componentDiagnostics"] as? [String: Bool])?["enabled"], false)
        let frames = try XCTUnwrap(clip["frames"] as? [[String: Any]])
        // OFFではframe数・候補数によらずclip単位の小さなメタデータだけを足す。
        for frame in frames {
            for color in ["red", "blue"] {
                XCTAssertNil((frame[color] as? [String: Any])?["components"])
            }
        }
        var legacy = summary
        var legacyClip = clip
        legacyClip.removeValue(forKey: "componentDiagnostics")
        legacy["autoClip"] = legacyClip
        let legacyData = try JSONSerialization.data(withJSONObject: legacy, options: [.sortedKeys])
        XCTAssertGreaterThan(data.count - legacyData.count, 0)
        XCTAssertLessThan(data.count - legacyData.count, 64)
        let context = content.frameContext(frameID: 361, sessionID: "dense")
        let contextFrame = try XCTUnwrap((context["frames"] as? [[String: Any]])?.first)
        for color in ["red", "blue"] {
            XCTAssertNil((contextFrame[color] as? [String: Any])?["components"])
        }
        // ONの予測外フレームは、旧形式のunknownと混ぜず明示する。
        var enabledCapture = capture
        enabledCapture.componentDiagnosticsEnabled = true
        var sources: [String] = []
        let enabledFrames = AutoClipBundleContent(capture: enabledCapture, slots: [360]).frameEntries(level: 0, sources: &sources)
        for color in ["red", "blue"] {
            let entry = try XCTUnwrap(enabledFrames.first?[color] as? [String: Any])
            XCTAssertEqual((entry["components"] as? [String: String])?["state"], "notCollected")
        }
        let enabledContext = AutoClipBundleContent(capture: enabledCapture, slots: [360])
            .frameContext(frameID: 361, sessionID: "dense")
        let enabledContextFrame = try XCTUnwrap((enabledContext["frames"] as? [[String: Any]])?.first)
        for color in ["red", "blue"] {
            let entry = try XCTUnwrap(enabledContextFrame[color] as? [String: Any])
            XCTAssertEqual((entry["components"] as? [String: String])?["state"], "notCollected")
        }
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

    // MARK: Gap

    private func gapObserve(_ detector: inout AutoClipTriggerDetector, _ frame: Int,
                            color: SaberColor = .red, winner: (P, P)?, other: Bool = true,
                            time: Double? = nil, sequence: UInt64? = nil) -> AutoClipTriggerEvent? {
        detector.observe(color: color, sequence: sequence ?? UInt64(frame + 1),
            timestamp: time ?? Double(frame) / 30, winner: winner, competitorCount: 0, competitor: nil,
            width: 480, height: 640, otherSelected: other)
    }

    func testGapFiresOnEighthMissingFramePerColourAndOnlyOnce() {
        for color in [SaberColor.red, .blue] {
            var detector = AutoClipTriggerDetector()
            let saber = (P(x: 100, y: 100), P(x: 100, y: 300))
            for frame in 0...30 { XCTAssertNil(gapObserve(&detector, frame, color: color, winner: saber)) }
            for frame in 31...37 { XCTAssertNil(gapObserve(&detector, frame, color: color, winner: nil)) }
            let event = gapObserve(&detector, 38, color: color, winner: nil)
            XCTAssertEqual(event?.kind, .gap)
            XCTAssertEqual(event?.color, color)
            XCTAssertEqual(event?.frameID, 39)
            XCTAssertEqual(event?.previousWinner, [100, 100, 100, 300])
            XCTAssertNil(event?.winner)
            for frame in 39...60 { XCTAssertNil(gapObserve(&detector, frame, color: color, winner: nil)) }
            // 再取得後にもう1秒連続選択されれば再度発火できる。
            for frame in 61...91 { XCTAssertNil(gapObserve(&detector, frame, color: color, winner: saber)) }
            for frame in 92...98 { XCTAssertNil(gapObserve(&detector, frame, color: color, winner: nil)) }
            XCTAssertEqual(gapObserve(&detector, 99, color: color, winner: nil)?.kind, .gap)
        }
    }

    func testGapNeedsOneSecondContinuousSelectionAndEightContinuousMissingFrames() {
        let saber = (P(x: 100, y: 100), P(x: 100, y: 300))
        var short = AutoClipTriggerDetector()
        for frame in 0...99 {
            XCTAssertNil(gapObserve(&short, frame, winner: saber, time: Double(frame) / 100))
        }
        for frame in 100...120 {
            XCTAssertNil(gapObserve(&short, frame, winner: nil, time: Double(frame) / 100))
        }
        var interrupted = AutoClipTriggerDetector()
        for frame in 0...30 { _ = gapObserve(&interrupted, frame, winner: saber) }
        for frame in 31...37 { XCTAssertNil(gapObserve(&interrupted, frame, winner: nil)) }
        XCTAssertNil(gapObserve(&interrupted, 38, winner: saber))
        for frame in 39...55 { XCTAssertNil(gapObserve(&interrupted, frame, winner: nil)) }
    }

    func testGapRequiresOtherColourOrRecentEndpointMotion() {
        let saber = (P(x: 100, y: 100), P(x: 100, y: 300))
        for mode in ["stationary", "moving", "rotation", "swapped", "oldMotion"] {
            var detector = AutoClipTriggerDetector()
            for frame in 0...30 {
                var winner = saber
                if mode == "moving" && frame == 30 { winner = (P(x: 110, y: 100), P(x: 110, y: 300)) }
                if mode == "rotation" && frame == 30 { winner = (P(x: 90, y: 100), P(x: 110, y: 300)) }
                if mode == "swapped" && frame % 2 == 0 { winner = (saber.1, saber.0) }
                if mode == "oldMotion" && frame < 10 { winner = (P(x: 110, y: 100), P(x: 110, y: 300)) }
                XCTAssertNil(gapObserve(&detector, frame, winner: winner, other: false))
            }
            for frame in 31...37 { XCTAssertNil(gapObserve(&detector, frame, winner: nil, other: false)) }
            let event = gapObserve(&detector, 38, winner: nil, other: false)
            XCTAssertEqual(event?.kind, mode == "moving" || mode == "rotation" ? .gap : nil, mode)
        }
    }

    func testGapDiscontinuityAndResetDisarmSelectionAndMissingRun() {
        let saber = (P(x: 100, y: 100), P(x: 100, y: 300))
        for mode in ["reset", "sequence", "time", "backward"] {
            var detector = AutoClipTriggerDetector()
            for frame in 0...30 { _ = gapObserve(&detector, frame, winner: saber) }
            for frame in 31...37 { _ = gapObserve(&detector, frame, winner: nil) }
            if mode == "reset" { detector.reset() }
            let time = mode == "time" ? 3.0 : mode == "backward" ? 0.0 : 38.0 / 30
            XCTAssertNil(gapObserve(&detector, 38, winner: nil, time: time,
                                    sequence: mode == "sequence" ? 30 : 39))
        }
    }

    func testGapCountsProcessedFramesEvenWhenCameraFramesAreSkipped() {
        var detector = AutoClipTriggerDetector()
        let saber = (P(x: 100, y: 100), P(x: 100, y: 300))
        for frame in 0...37 {
            XCTAssertNil(gapObserve(&detector, frame, winner: frame <= 30 ? saber : nil,
                                    sequence: UInt64(frame * 4 + 1)))
        }
        XCTAssertEqual(gapObserve(&detector, 38, winner: nil, sequence: 153)?.kind, .gap)
    }

    func testAutomaticSessionBudgetCoversEntireFestivalDay() {
        // 15秒間隔でも24時間は5760件。10/30の60件を越えて保存を継続できる。
        XCTAssertGreaterThan(AutoClipLimits.maximumAutomaticClipsPerSession, 24 * 60 * 60 / 15)
        XCTAssertEqual(AutoClipLimits.cooldownSeconds, 15)
        var limiter = AutoClipRateLimiter()
        for index in 0..<5760 {
            XCTAssertEqual(limiter.admit(event(.gap, at: Double(index) * 15), busy: false,
                width: 480, height: 640), .accept)
        }
        XCTAssertEqual(limiter.automaticClipsThisSession, 5760)
    }

    func testGapUsesAutomaticBusyCooldownAndSessionBudget() {
        var limiter = AutoClipRateLimiter()
        XCTAssertEqual(limiter.admit(event(.gap, at: 0), busy: true, width: 480, height: 640), .reject("busy"))
        for index in 0..<AutoClipLimits.maximumAutomaticClipsPerSession {
            XCTAssertEqual(limiter.admit(event(.gap, at: Double(index) * 20), busy: false, width: 480, height: 640), .accept)
            XCTAssertEqual(limiter.admit(event(.gap, at: Double(index) * 20 + 1), busy: false, width: 480, height: 640),
                           .reject(index == AutoClipLimits.maximumAutomaticClipsPerSession - 1 ? "sessionLimit" : "cooldown"))
        }
        XCTAssertEqual(limiter.admit(event(.gap, at: Double(AutoClipLimits.maximumAutomaticClipsPerSession) * 20 + 100), busy: false, width: 480, height: 640), .reject("sessionLimit"))
    }

    func testRecorderGapUsesActualMissingSelectionEvenWithEligibleCandidates() throws {
        for color in [SaberColor.red, .blue] {
            let recorder = AutoClipRecorder(directory: FileManager.default.temporaryDirectory, enabled: true)
            var logs: [String] = []
            recorder.onLog = { logs.append($0) }
            let saber = Self.candidate((10, 10), (10, 30))
            let other: SaberColor = color == .red ? .blue : .red
            let pixels = [UInt8](repeating: 0, count: 48 * 64 * 4)
            pixels.withUnsafeBufferPointer { raw in
                for frame in 0...38 {
                    var selected = [other: saber.endpoints]
                    if frame <= 30 { selected[color] = saber.endpoints }
                    recorder.observe(ObservedFrame(baseAddress: raw.baseAddress!, width: 48, height: 64,
                        bytesPerRow: 48 * 4, sequence: UInt64(frame + 1), timestamp: Double(frame) / 30,
                        processingSeconds: 0, analysis: SaberFrameAnalysis(candidates: [color: [saber]], selected: selected),
                        ignoreZones: [:], playArea: nil, captureFPS: 30))
                }
            }
            XCTAssertEqual(logs.filter { $0.hasPrefix("trigger") }, ["trigger kind=gap color=\(AutoClipRecorder.colorName(color)) frame=39"])
            // post window 完了前なのでディスク保存は不要。kind/reason/rules の転送形式は同じ。
            let buffers = AutoClipBuffers(width: 48, height: 64, captureFPS: 30)
            let capture = AutoClipCapture(clipID: "gap", trigger: event(.gap, at: 2), buffers: buffers,
                windowStart: -1, windowEnd: 4, captureFPS: 30, environmentAtTrigger: AutoClipEnvironment(),
                environmentAtSave: AutoClipEnvironment(), ignoreZones: [:], playArea: nil,
                sessionClipIndex: 1, rejectedTriggers: [:])
            let content = AutoClipBundleContent(capture: capture, slots: [])
            let json = try XCTUnwrap(JSONSerialization.jsonObject(with: content.summaryData(images: [])) as? [String: Any])
            let clip = try XCTUnwrap(json["autoClip"] as? [String: Any])
            XCTAssertEqual((clip["trigger"] as? [String: Any])?["kind"] as? String, "gap")
            XCTAssertNotNil((clip["rules"] as? [String: Any])?["gap"])
            XCTAssertTrue(content.reasonText.contains("8 consecutive processed frames"))
        }
    }

    // MARK: Rate limiter

    private func event(_ kind: AutoClipTriggerKind, at time: Double, competitor: [Int]? = [400, 600, 420, 630]) -> AutoClipTriggerEvent {
        AutoClipTriggerEvent(kind: kind, color: kind == .manual ? nil : .red, frameID: UInt64(time * 60),
                             timestamp: time, competitor: kind == .confusion ? competitor : nil)
    }

    func testRateLimiterCooldownSessionLimitAndRepeatLocation() {
        var limiter = AutoClipRateLimiter()
        XCTAssertEqual(limiter.admit(event(.confusion, at: 10), busy: false, width: 480, height: 640), .accept)
        XCTAssertEqual(limiter.admit(event(.jump, at: 24), busy: false, width: 480, height: 640), .reject("cooldown"))
        XCTAssertEqual(limiter.admit(event(.jump, at: 31), busy: false, width: 480, height: 640), .accept)
        // Same cone location again: skipped even after the cooldown.
        XCTAssertEqual(limiter.admit(event(.confusion, at: 60, competitor: [405, 602, 421, 628]), busy: false, width: 480, height: 640),
                       .reject("repeatLocation"))
        XCTAssertEqual(limiter.admit(event(.confusion, at: 61, competitor: [100, 100, 120, 200]), busy: false, width: 480, height: 640),
                       .accept)
        XCTAssertEqual(limiter.admit(event(.jump, at: 90), busy: true, width: 480, height: 640), .reject("busy"))
        for index in 0..<(AutoClipLimits.maximumAutomaticClipsPerSession - 3) {
            XCTAssertEqual(limiter.admit(event(.jump, at: 100 + Double(index) * 30), busy: false, width: 480, height: 640), .accept)
        }
        XCTAssertEqual(limiter.automaticClipsThisSession, AutoClipLimits.maximumAutomaticClipsPerSession)
        XCTAssertEqual(limiter.admit(event(.jump, at: Double(AutoClipLimits.maximumAutomaticClipsPerSession) * 30 + 100), busy: false, width: 480, height: 640), .reject("sessionLimit"))
        // A PC request still works, at most every 5 s.
        XCTAssertEqual(limiter.admit(event(.manual, at: Double(AutoClipLimits.maximumAutomaticClipsPerSession) * 30 + 100 + 1), busy: false, width: 480, height: 640), .accept)
        XCTAssertEqual(limiter.admit(event(.manual, at: Double(AutoClipLimits.maximumAutomaticClipsPerSession) * 30 + 100 + 3), busy: false, width: 480, height: 640), .reject("manualInterval"))
        XCTAssertEqual(limiter.admit(event(.manual, at: Double(AutoClipLimits.maximumAutomaticClipsPerSession) * 30 + 100 + 6.5), busy: false, width: 480, height: 640), .accept)
        XCTAssertEqual(limiter.rejected["cooldown"], 1)
        XCTAssertEqual(limiter.rejected["sessionLimit"], 1)
        limiter.beginSession()
        XCTAssertEqual(limiter.automaticClipsThisSession, 0)
        XCTAssertEqual(limiter.admit(event(.confusion, at: Double(AutoClipLimits.maximumAutomaticClipsPerSession) * 30 + 100 + 200), busy: false, width: 480, height: 640), .accept)
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
        // 「今すぐ送る」（retryNow → manualIDs）は座標送信中でも送る。
        var pressed = DebugBundleOutbox()
        pressed.register(sessionID: "autoclip_b", bundlePath: "AutoClips/phone_saber_triage_autoclip_b",
                         now: now, deferDuringPlay: true)
        let manual = pressed.retryNow()
        XCTAssertEqual(manual, ["autoclip_b"])
        XCTAssertNil(pressed.beginNext(now: now, recording: false, automatic: true, liveTraffic: true))
        XCTAssertEqual(pressed.beginNext(now: now, recording: false, automatic: true, manualIDs: manual,
                                         liveTraffic: true)?.sessionID, "autoclip_b")
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

    func testNewSessionDropsPendingClipAndAcceptsItsOwnRequest() {
        let recorder = AutoClipRecorder(directory: FileManager.default.temporaryDirectory, enabled: true)
        var log: [String] = []
        var cancelled: [String] = []
        recorder.onLog = { log.append($0) }
        recorder.onManualClipResult = { reason, saved in
            XCTAssertFalse(saved)
            cancelled.append(reason)
        }
        let pixels = [UInt8](repeating: 0, count: 16 * 16 * 4)
        func frame(_ time: Double, _ sequence: UInt64) {
            pixels.withUnsafeBufferPointer { buffer in
                recorder.observe(ObservedFrame(baseAddress: buffer.baseAddress!, width: 16, height: 16,
                    bytesPerRow: 16 * 4, sequence: sequence, timestamp: time, processingSeconds: 0,
                    analysis: SaberFrameAnalysis(candidates: [:], selected: [:]),
                    ignoreZones: [:], playArea: nil, captureFPS: 60))
            }
        }
        recorder.requestClip(reason: "old-session")
        frame(100, 1)
        // 停止後すぐ再開するとPTSの差は1秒未満。時間ギャップ任せでは旧clipが残る。
        recorder.beginSession()
        recorder.requestClip(reason: "new-session")
        frame(100.2, 1)
        XCTAssertEqual(cancelled, ["old-session"])
        XCTAssertTrue(log.contains { $0.hasPrefix("trigger kind=manual") && $0.contains("note=new-session") })
        XCTAssertFalse(log.contains { $0.contains("clip skipped") })
    }

    func testSequenceReuseDropsPendingClipAndKeepsDiagnosticIDsMonotonic() {
        let recorder = AutoClipRecorder(directory: FileManager.default.temporaryDirectory, enabled: true)
        var log: [String] = []
        var cancelled: [String] = []
        recorder.onLog = { log.append($0) }
        recorder.onManualClipResult = { reason, saved in
            XCTAssertFalse(saved)
            cancelled.append(reason)
        }
        let pixels = [UInt8](repeating: 0, count: 16 * 16 * 4)
        func frame(_ time: Double, _ sequence: UInt64) {
            pixels.withUnsafeBufferPointer { buffer in
                recorder.observe(ObservedFrame(baseAddress: buffer.baseAddress!, width: 16, height: 16,
                    bytesPerRow: 64, sequence: sequence, timestamp: time, processingSeconds: 0,
                    analysis: SaberFrameAnalysis(candidates: [:], selected: [:]),
                    ignoreZones: [:], playArea: nil, captureFPS: 60))
            }
        }
        frame(100, 52)
        recorder.requestClip(reason: "before-reset")
        frame(100.02, 53)
        // 実録と同じ、時刻は連続したままprocessorだけが1へ戻る。
        frame(100.04, 1)
        XCTAssertEqual(cancelled, ["before-reset"])
        recorder.beginSession()
        recorder.requestClip(reason: "after-reset")
        frame(100.06, 1)
        XCTAssertTrue(log.contains { $0.contains("frame=55") && $0.contains("note=after-reset") })
        XCTAssertFalse(log.contains { $0.contains("clip skipped") })
        // 同じIDの再入力でも、セッション通知なしで収集中の窓を破棄する。
        frame(100.08, 1)
        XCTAssertEqual(cancelled, ["before-reset", "after-reset"])
    }

    func testWriterRejectsDuplicateIDsMissingRecordsAndImageTimeMismatch() throws {
        let buffers = AutoClipBuffers(width: 16, height: 16, captureFPS: 30)
        buffers.frames[0] = AutoClipFrameRecord(frameID: 7, timestamp: 100)
        buffers.frames[1] = AutoClipFrameRecord(frameID: 7, timestamp: 101.22)
        buffers.eventCount = 1
        buffers.eventFrameIDs[0] = 7
        buffers.eventTimes[0] = 100
        let capture = AutoClipCapture(clipID: "reuse", trigger: event(.manual, at: 100), buffers: buffers,
            windowStart: 99, windowEnd: 102, captureFPS: 30,
            environmentAtTrigger: AutoClipEnvironment(), environmentAtSave: AutoClipEnvironment(),
            ignoreZones: [:], playArea: nil, sessionClipIndex: 1, rejectedTriggers: [:])
        // 実録の辞書上書きと、重複が間引かれた場合の時刻不一致の両方を拒否。
        XCTAssertThrowsError(try AutoClipBundleContent(capture: capture, slots: [0, 1]).validateFrameIdentity())
        XCTAssertThrowsError(try AutoClipBundleContent(capture: capture, slots: [1]).validateFrameIdentity())
        XCTAssertThrowsError(try AutoClipBundleContent(capture: capture, slots: []).validateFrameIdentity())
        XCTAssertNoThrow(try AutoClipBundleContent(capture: capture, slots: [0]).validateFrameIdentity())
        buffers.eventCount = 0
        buffers.contextValid[0] = true
        buffers.contextFrameIDs[0] = 7
        buffers.contextTimes[0] = 100
        XCTAssertThrowsError(try AutoClipBundleContent(capture: capture, slots: [1]).validateFrameIdentity())
    }

    func testNewSessionDoesNotInheritClipCooldowns() {
        var limiter = AutoClipRateLimiter()
        XCTAssertEqual(limiter.admit(event(.jump, at: 100), busy: false, width: 480, height: 640), .accept)
        XCTAssertEqual(limiter.admit(event(.manual, at: 100), busy: false, width: 480, height: 640), .accept)
        limiter.beginSession()
        XCTAssertEqual(limiter.admit(event(.jump, at: 100.2), busy: false, width: 480, height: 640), .accept)
        XCTAssertEqual(limiter.admit(event(.manual, at: 100.2), busy: false, width: 480, height: 640), .accept)
    }

    func testNewSessionCancelsRequestStillWaitingForItsFirstFrame() {
        let recorder = AutoClipRecorder(directory: FileManager.default.temporaryDirectory, enabled: true)
        var cancelled: [String] = []
        var log: [String] = []
        recorder.onLog = { log.append($0) }
        recorder.onManualClipResult = { reason, saved in
            XCTAssertFalse(saved)
            cancelled.append(reason)
        }
        recorder.requestClip(reason: "old-session")
        recorder.beginSession()
        [UInt8](repeating: 0, count: 16 * 16 * 4).withUnsafeBufferPointer { buffer in
            recorder.observe(ObservedFrame(baseAddress: buffer.baseAddress!, width: 16, height: 16,
                bytesPerRow: 16 * 4, sequence: 1, timestamp: 100, processingSeconds: 0,
                analysis: SaberFrameAnalysis(candidates: [:], selected: [:]),
                ignoreZones: [:], playArea: nil, captureFPS: 60))
        }
        XCTAssertEqual(cancelled, ["old-session"])
        XCTAssertFalse(log.contains { $0.hasPrefix("trigger") })
    }

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
            let frame = try XCTUnwrap((contextJSON["frames"] as? [[String: Any]])?.first)
            XCTAssertEqual(image["frameID"] as? Int, frame["frameID"] as? Int)
            XCTAssertEqual(image["timestamp"] as? Double, frame["timestamp"] as? Double)
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

extension AutoClipTests {
    func testIgnoreInspectionUsesOnlySameColorEligibleShapeMatchesInPlayArea() throws {
        let target = Self.candidate((150, 100), (400, 350))
        let crossing = Self.candidate((170, 320), (380, 130))
        let ineligible = Self.candidate((150, 100), (400, 350), eligible: false)
        let outside = Self.candidate((500, 400), (600, 450))
        let zone = SaberIgnoreZone(around: target.endpoints, width: 640, height: 480)
        let outer = SaberIgnoreZone(around: outside.endpoints, width: 640, height: 480)
        let analysis = SaberFrameAnalysis(candidates: [.red: [target], .blue: [target, crossing, ineligible, outside]],
                                          selected: [:], trackLockSuppressed: [.blue])
        let snapshot = SaberIgnoreInspectionSnapshot(analysis: analysis, width: 640, height: 480, sequence: 7,
            zones: [.blue: [zone, zone, outer]], playArea: .init(minX: 0, minY: 0, maxX: 0.7, maxY: 1))
        XCTAssertEqual(snapshot.matches.count, 1)
        let match = try XCTUnwrap(snapshot.matches.first)
        XCTAssertEqual(match.color, .blue)
        XCTAssertEqual(match.candidateIndex, 0)
        XCTAssertEqual(match.zoneIndices, [0, 1])
        XCTAssertEqual(match.label, "青候補1")
        // 左上原点・反転なし。矩形と候補は同じ width/height の正規化を使う。
        XCTAssertEqual(snapshot.diagramPoint(target.endpoints.0).x, 150.0 / 640)
        XCTAssertEqual(snapshot.diagramPoint(target.endpoints.1).y, 350.0 / 480)
        XCTAssertEqual(snapshot.diagramPoint(PixelPoint(x: 0, y: 0)).x, 0)
        XCTAssertEqual(snapshot.diagramPoint(PixelPoint(x: 640, y: 480)).y, 1)
        let trackingOnly = SaberIgnoreInspectionSnapshot(analysis: analysis, width: 640, height: 480, sequence: 8,
                                                         zones: [:], playArea: nil)
        XCTAssertTrue(trackingOnly.matches.isEmpty)
    }

    func testIgnoreInspectionMailboxRejectsOldFramesAndKeepsOnlyLatest() throws {
        var mailbox = SaberIgnoreInspectionMailbox()
        let snapshot = SaberIgnoreInspectionSnapshot(analysis: .init(candidates: [:], selected: [:]),
            width: 480, height: 640, sequence: 1, zones: [:], playArea: nil)
        XCTAssertNil(mailbox.ticket)
        let session = UUID()
        mailbox.open(session)
        let first = try XCTUnwrap(mailbox.ticket)
        for frame in 0..<1000 {
            let latest = SaberIgnoreInspectionSnapshot(analysis: .init(candidates: [:], selected: [:]),
                width: 480, height: 640, sequence: UInt64(frame), zones: [:], playArea: nil)
            mailbox.offer(latest, ticket: first, at: 10)
            XCTAssertEqual(mailbox.pendingCount, 1)
        }
        XCTAssertEqual(mailbox.take(session: session, at: 10.1)?.sequence, 999)
        XCTAssertEqual(mailbox.pendingCount, 0)
        mailbox.invalidate()
        mailbox.offer(snapshot, ticket: first, at: 10.2)
        XCTAssertNil(mailbox.take(session: session, at: 10.2))
        let changed = try XCTUnwrap(mailbox.ticket)
        mailbox.offer(snapshot, ticket: changed, at: 11)
        XCTAssertNil(mailbox.take(session: session, at: 11.5))
        mailbox.open(nil)
        mailbox.offer(snapshot, ticket: changed, at: 12)
        XCTAssertEqual(mailbox.pendingCount, 0)
        let reopened = UUID()
        mailbox.open(reopened)
        mailbox.offer(snapshot, ticket: changed, at: 12)
        XCTAssertNil(mailbox.take(session: reopened, at: 12))
        mailbox.offer(snapshot, ticket: try XCTUnwrap(mailbox.ticket), at: 12)
        XCTAssertNil(mailbox.take(session: session, at: 12))
        XCTAssertEqual(mailbox.take(session: reopened, at: 12)?.sequence, 1)
    }

    func testBlueIgnoreRemovalRecoversNextProductionFrameAndRetainsBackgroundAndRed() throws {
        let width = 480, height = 640
        var bytes = [UInt8](repeating: 0, count: width * height * 4)
        func paint(_ x: Int, _ y1: Int, _ y2: Int, color: SaberColor) {
            for y in y1...y2 { for dx in -3...3 {
                let i = (y * width + x + dx) * 4
                bytes[i] = color == .blue ? 245 : 30
                bytes[i + 1] = 45
                bytes[i + 2] = color == .red ? 245 : 30
                bytes[i + 3] = 255
            } }
        }
        paint(100, 100, 300, color: .blue)
        paint(380, 450, 600, color: .blue)
        paint(260, 100, 300, color: .red)
        let threshold = ColorThreshold(brightness: 140, dominance: 20)
        let original = analyzeSabers(in: bytes, width: width, height: height, bytesPerRow: width * 4,
                                    redThreshold: threshold, blueThreshold: threshold)
        let blue = original.candidates[.blue] ?? []
        let target = try XCTUnwrap(blue.first { $0.isEmitterEligible && $0.endpoints.0.x < 200 })
        let background = try XCTUnwrap(blue.first { $0.isEmitterEligible && $0.endpoints.0.x > 300 })
        let red = try XCTUnwrap(original.candidates[.red]?.first { $0.isEmitterEligible })
        let targetZone = SaberIgnoreZone(around: target.endpoints, width: width, height: height, candidates: blue)
        let backgroundZone = SaberIgnoreZone(around: background.endpoints, width: width, height: height, candidates: blue)
        let redZone = SaberIgnoreZone(around: red.endpoints, width: width, height: height, candidates: original.candidates[.red] ?? [])
        for track in [nil, SaberTrackLock()] as [SaberTrackLock?] {
            var zones: [SaberColor: [SaberIgnoreZone]] = [.red: [redZone], .blue: [targetZone, backgroundZone]]
            func process(_ frame: Int, inspect: Bool = true) -> SaberFrameAnalysis {
                let a = analyzeSabers(in: bytes, width: width, height: height, bytesPerRow: width * 4,
                    redThreshold: threshold, blueThreshold: threshold, ignoreZones: zones,
                    trackLock: track, now: Double(frame) / 60)
                if inspect {
                    _ = SaberIgnoreInspectionSnapshot(analysis: a, width: width, height: height,
                        sequence: UInt64(frame), zones: zones, playArea: nil)
                }
                return a
            }
            var before = 0, after = 0
            for frame in 1...25 { if process(frame).selected[.blue] != nil { before += 1 } }
            XCTAssertEqual(before, 0)
            zones[.blue]?.remove(at: 0)
            for frame in 26...50 {
                let a = process(frame)
                let selected = try XCTUnwrap(a.selected[.blue], "解除直後の26コマ目から選択可能")
                XCTAssertLessThan(selected.0.x, 200)
                XCTAssertNil(a.selected[.red])
                XCTAssertTrue(backgroundZone.ignores(background.endpoints, width: width, height: height))
                after += 1
                // 確認処理を行わない同じ本番経路とも、候補・端点・UDP座標が一致する。
                let plain = analyzeSabers(in: bytes, width: width, height: height, bytesPerRow: width * 4,
                    redThreshold: threshold, blueThreshold: threshold, ignoreZones: zones)
                XCTAssertEqual(a.candidates[.blue]?.map(\.score), plain.candidates[.blue]?.map(\.score))
                XCTAssertEqual(payload(for: selected, source: (width, height), output: (1920, 1080), mirrorX: true, mirrorY: false),
                    payload(for: try XCTUnwrap(plain.selected[.blue]), source: (width, height), output: (1920, 1080), mirrorX: true, mirrorY: false))
            }
            XCTAssertEqual(after, 25)
            zones[.blue] = [targetZone, targetZone, backgroundZone]
            zones[.blue]?.remove(at: 0)
            XCTAssertNil(process(51).selected[.blue], "重複する登録が残れば引き続き除外")
            print("ignore-removal track=\(track != nil) blue output before=\(before)/25 after=\(after)/25 next-frame=26 overlap=0/1")
        }
    }
    private func fullRecorder(memory: Int = AutoClipFullLimits.memoryBytes) -> (AutoClipFullRecorder, DispatchQueue, UserDefaults) {
        let defaults = UserDefaults(suiteName: "fullrec-tests-" + UUID().uuidString)!
        let queue = DispatchQueue(label: "fullrec-tests")
        return (AutoClipFullRecorder(defaults: defaults, queue: queue, maximumMemoryBytes: memory), queue, defaults)
    }

    private func fullFrame(_ id: UInt64, time: Double, count: Int = 20) throws -> ObservedFrame {
        var buffer: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(nil, 4, 4, kCVPixelFormatType_32BGRA, nil, &buffer), kCVReturnSuccess)
        let pixelBuffer = try XCTUnwrap(buffer)
        CVPixelBufferLockBaseAddress(pixelBuffer, [])
        let base = CVPixelBufferGetBaseAddress(pixelBuffer)!.assumingMemoryBound(to: UInt8.self)
        let stride = CVPixelBufferGetBytesPerRow(pixelBuffer)
        // row paddingが混入しないこととBGRA全byteの保持を検証する。
        for y in 0..<4 { for x in 0..<stride { base[y * stride + x] = UInt8((y * 16 + x) % 256) } }
        CVPixelBufferUnlockBaseAddress(pixelBuffer, [])
        var candidate = Self.candidate((1, 1), (2, 3))
        candidate.isEmitterEligible = false
        return ObservedFrame(baseAddress: UnsafePointer(base), width: 4, height: 4, bytesPerRow: stride,
            sequence: id, timestamp: time, processingSeconds: 0.001,
            analysis: SaberFrameAnalysis(candidates: [.red: Array(repeating: candidate, count: count)], selected: [:]),
            ignoreZones: [:], playArea: nil, captureFPS: 2, pixelBuffer: pixelBuffer,
            cameraISO: 50, exposureDurationSeconds: 0.0005, cameraMetadataSource: "exif")
    }

    func testFullResolutionWindowKeepsEveryPixelAndAllIneligibleCandidates() throws {
        let (recorder, queue, _) = fullRecorder()
        XCTAssertFalse(recorder.isEnabled)
        recorder.setEnabled(true)
        let event = AutoClipTriggerEvent(kind: .gap, color: .red, frameID: 5, timestamp: 2)
        for i in 0...8 {
            recorder.offer(try fullFrame(UInt64(i + 1), time: Double(i) / 2), trigger: i == 4 ? event : nil)
            queue.sync {}
        }
        var saved: AutoClipFullSnapshot?
        recorder.finish(event: event) { saved = $0 }; queue.sync {}
        let snapshot = try XCTUnwrap(saved)
        XCTAssertEqual(snapshot.frames.map(\.id), Array(1...9).map(UInt64.init))
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let info = try snapshot.write(to: directory)
        XCTAssertEqual(info["complete"] as? Bool, true)
        XCTAssertEqual(info["frameCount"] as? Int, 9)
        let raw = try Data(contentsOf: directory.appendingPathComponent("fullres.bgra"))
        XCTAssertEqual(raw.count, 9 * 64)
        XCTAssertEqual(Array(raw.prefix(64)), Array(0..<64).map(UInt8.init))
        let row = try JSONSerialization.jsonObject(with: snapshot.frames[0].metadata) as! [String: Any]
        let red = row["red"] as! [String: Any]
        XCTAssertEqual((red["c"] as? [[Any]])?.count, 20, "既存12件/非eligible2件の制限を適用しない")
        XCTAssertEqual(row["appliedISO"] as? Double, 50)
        XCTAssertEqual(row["exposureDurationSeconds"] as? Double, 0.0005)
        XCTAssertEqual(row["cameraMetadataSource"] as? String, "exif")
        // 任意の出力先へ本番Swiftのwire fixtureを残し、Mac readerとの相互運用を確認できる。
        if let path = ProcessInfo.processInfo.environment["PHONESABER_FULLRES_FIXTURE_DIR"] {
            let target = URL(fileURLWithPath: path)
            try FileManager.default.createDirectory(at: target, withIntermediateDirectories: true)
            let description = try snapshot.write(to: target)
            let summary: [String: Any] = ["formatVersion": 1, "summaryScope": "autoClip",
                "sessionID": "autoclip_20261029_120000_000_gap_red", "selectedImageCount": 0, "images": [],
                "incidentCount": 1, "redBlueDetectionSummary": ["red": [:], "blue": [:]],
                "dropoutSummary": ["red": [:], "blue": [:]], "fullResolution": description,
                "autoClip": ["formatVersion": 2, "candidateColumns": AutoClipBundleContent.candidateColumns,
                    "frames": [], "sources": [], "frameSize": ["width": 4, "height": 4],
                    "trigger": ["kind": "gap", "frameID": 5, "timestamp": 2]]]
            try JSONSerialization.data(withJSONObject: summary).write(to: target.appendingPathComponent("summary.json"))
            try Data("Swift fullres fixture".utf8).write(to: target.appendingPathComponent("prompt.md"))
        }
        recorder.releaseSnapshot(); queue.sync {}
    }

    func testFullResolutionBacklogNeverWaitsAndReportsCameraSequenceGaps() throws {
        let (recorder, queue, _) = fullRecorder()
        recorder.setEnabled(true); queue.sync {}
        let event = AutoClipTriggerEvent(kind: .jump, color: .blue, frameID: 1, timestamp: 0)
        queue.suspend()
        recorder.offer(try fullFrame(1, time: 0), trigger: event)
        recorder.offer(try fullFrame(3, time: 0.5), trigger: nil)
        // queueを止めたままでも3枚目以降は待たずに捨てる。
        for i in 4...100 { recorder.offer(try fullFrame(UInt64(i), time: 1), trigger: nil) }
        queue.resume(); queue.sync {}
        var saved: AutoClipFullSnapshot?
        recorder.finish(event: event) { saved = $0 }; queue.sync {}
        let snapshot = try XCTUnwrap(saved)
        XCTAssertEqual(snapshot.frames.map(\.id), [1, 3])
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        let info = try snapshot.write(to: directory)
        XCTAssertEqual(info["missingSequenceFrames"] as? Int, 1)
        XCTAssertEqual(info["complete"] as? Bool, false)
    }

    func testFullResolutionEvictsOldestAndPersistsHourlyLimit() throws {
        let (recorder, queue, defaults) = fullRecorder(memory: 1000)
        recorder.setEnabled(true); queue.sync {}
        let event = AutoClipTriggerEvent(kind: .confusion, color: .red, frameID: 1, timestamp: 0)
        for i in 0...4 {
            recorder.offer(try fullFrame(UInt64(i + 1), time: Double(i) / 2, count: 0), trigger: i == 0 ? event : nil)
            queue.sync {}
        }
        var saved: AutoClipFullSnapshot?
        recorder.finish(event: event) { saved = $0 }; queue.sync {}
        let snapshot = try XCTUnwrap(saved)
        XCTAssertLessThanOrEqual(snapshot.frames.reduce(0) { $0 + $1.pixels.count + $1.metadata.count }, 1000)
        XCTAssertGreaterThan(try XCTUnwrap(snapshot.frames.first).id, 1)
        recorder.releaseSnapshot(); queue.sync {}
        defaults.set(Array(repeating: Date().timeIntervalSince1970, count: 4), forKey: AutoClipFullLimits.historyKey)
        recorder.setEnabled(false); recorder.setEnabled(true); queue.sync {}
        recorder.offer(try fullFrame(20, time: 10), trigger: AutoClipTriggerEvent(kind: .manual, frameID: 20, timestamp: 10))
        queue.sync {}
        saved = nil
        recorder.finish(event: AutoClipTriggerEvent(kind: .manual, frameID: 20, timestamp: 10)) { saved = $0 }; queue.sync {}
        XCTAssertNil(saved, "ON/OFFや新セッションで1時間の上限を回避しない")
    }

    func testFullResolutionSuspendDropsPendingAndDoesNotInventExposure() throws {
        let (recorder, queue, _) = fullRecorder()
        recorder.setEnabled(true); queue.sync {}
        let event = AutoClipTriggerEvent(kind: .gap, color: .red, frameID: 1, timestamp: 0)
        var frame = try fullFrame(1, time: 0)
        frame.cameraISO = nil; frame.exposureDurationSeconds = nil; frame.cameraMetadataSource = nil
        recorder.offer(frame, trigger: event); queue.sync {}
        recorder.updatePolicy(reason: "thermalSerious", directory: FileManager.default.temporaryDirectory)
        queue.sync {}
        var saved: AutoClipFullSnapshot?
        recorder.finish(event: event) { saved = $0 }; queue.sync {}
        XCTAssertNil(saved)
        XCTAssertTrue(recorder.statusLine.contains("thermalSerious"))
    }

    func testFullResolutionRetentionPrunesOldestIncludingTransferPackage() throws {
        let directory = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: directory) }
        var entries: [URL] = []
        for i in 1...4 {
            let entry = directory.appendingPathComponent("phone_saber_triage_autoclip_0\(i)")
            try FileManager.default.createDirectory(at: entry, withIntermediateDirectories: true)
            let file = entry.appendingPathComponent("fullres.bgra")
            FileManager.default.createFile(atPath: file.path, contents: nil)
            let output = try FileHandle(forWritingTo: file)
            try output.truncate(atOffset: 300 * 1024 * 1024)
            try output.close()
            entries.append(entry)
        }
        let package = directory.appendingPathComponent("autoclip_01_triage_transfer.psbt")
        try Data([1]).write(to: package)
        AutoClipWriter(directory: directory).prune(keeping: entries.last)
        XCTAssertFalse(FileManager.default.fileExists(atPath: entries[0].path))
        XCTAssertFalse(FileManager.default.fileExists(atPath: package.path))
        XCTAssertTrue(entries.dropFirst().allSatisfy { FileManager.default.fileExists(atPath: $0.path) })
        XCTAssertLessThanOrEqual(AutoClipWriter.allocatedSize(directory), AutoClipLimits.maximumStoredBytes)
    }

}
