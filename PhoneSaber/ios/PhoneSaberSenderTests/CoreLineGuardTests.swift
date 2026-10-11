import Foundation
#if CORELINE_GUARD_DIAGNOSTICS
import CoreGraphics
import ImageIO
#endif

// 写真を使わず、C++ と同じ合成支持点と mask で guard を直接検証する。
@main
enum CoreLineGuardTests {
    static func candidate(line: Bool, points: [PixelPoint],
                          endpoints: (PixelPoint, PixelPoint), fallback: Bool = true,
                          comparison: (PixelPoint, PixelPoint)? = nil) -> SaberCandidate {
        let score = SaberScoreBreakdown(length: 0, aspect: 0, extent: 0, widthConsistency: 0, area: 0, peakBrightness: 0, meanBrightness: 0, highBrightnessRatio: 0, colorPurity: 0, localContrast: 0, emitterTexture: 0, clippedWhite: 0, longitudinalHighCoverage: 0, coreSupport: 0, longitudinalCoreCoverage: 0)
        return SaberCandidate(source: line ? "core-line" : "core-halo",
            comparisonEndpoints: comparison ?? endpoints, endpoints: endpoints,
            boundingBox: SaberBoundingBox(minX: 0, minY: 20, maxX: 99, maxY: 20),
            score: line ? 100 : 50, scoreBreakdown: score,
            isEmitterEligible: true, isCompactRed: false, peakValue: 255, meanValue: 255,
            highValueRatio: 0.35, meanColorPurity: 1, clippedWhiteRatio: 0,
            brightnessVariation: 0, localContrast: 1, longitudinalHighCoverage: 1,
            widthVariation: 0, coreSupportRatio: 0.40, longitudinalCoreCoverage: 1,
            longitudinalContinuity: 1, largestLongitudinalGap: 0, retainedBodyRatio: 1,
            rawPCASpan: 100, robustMainIntervalEndpoints: nil, robustMainIntervalLength: 100,
            axialDensity: 1, componentArea: 100, pointCount: points.count,
            usedPointLEDFallback: fallback, supportSamplePoints: points)
    }
    static func main() async throws {
        let w = 160, h = 40
        var mask = [UInt8](repeating: 0, count: w*h)
        for x in 0..<100 where x < 20 || x == 99 { mask[20*w+x] = 1 }
        let values = [UInt8](repeating: 255, count: w*h)
        var evidence = SaberEvidence(color: .red, value: values, chroma: values,
                                     colorMask: mask, coreMask: mask)
        let span = (0..<100).map { PixelPoint(x: $0, y: 20) }
        let compact = Array(span.prefix(20))
        let ends = (span.first!, span.last!)
        let line = candidate(line: true, points: span, endpoints: ends)
        var body = candidate(line: false, points: compact, endpoints: (compact.first!, compact.last!))
        func run(_ l: SaberCandidate, _ b: SaberCandidate) -> [SaberCandidate] {
            var c = [l,b]
            applyCoreLineGuard(&c, evidence: evidence, width: w, step: 2)
            return c
        }
        let rejected = run(line, body)
        precondition(!rejected[0].isEmitterEligible && rejected[1].isEmitterEligible)
        precondition(rejected[0].coreLineGuard?.supportingIndex == 1)
        precondition(rejected[0].coreLineGuard?.supportingEndpoints == [0,40,38,40])
        precondition(rejected[0].coreLineGuard?.litCoverage == 0.21)
        precondition(rejected[0].score == 100)
#if CORELINE_GUARD_DIAGNOSTICS
        // 実 serializer / triage を通し、除外候補と支持候補の snapshot を保存する。
        try await diagnostics(line: line, body: body, rejected: rejected)
#endif
        body.isEmitterEligible = false
        precondition(run(line,body)[0].isEmitterEligible)
        body.isEmitterEligible = true; body.source = "core-line"
        precondition(run(line,body)[0].isEmitterEligible); body.source = "core-halo"
        let atBoundary = candidate(line: false, points: compact,
            endpoints: (PixelPoint(x: 0,y: 35),PixelPoint(x: 19,y: 35)))
        precondition(!run(line,atBoundary)[0].isEmitterEligible)
        let far = candidate(line: false, points: compact,
            endpoints: (PixelPoint(x: 0,y: 36),PixelPoint(x: 19,y: 36)))
        precondition(run(line,far)[0].isEmitterEligible)
        let shortened = candidate(line: true, points: span,
            endpoints: (compact.first!, compact.last!), fallback: false, comparison: ends)
        precondition(run(shortened,body)[0].isEmitterEligible)
        for x in 0..<100 { mask[20*w+x] = x < 59 || x == 99 ? 1 : 0 }
        evidence = SaberEvidence(color: .red, value: values, chroma: values, colorMask: mask, coreMask: mask)
        precondition(fullLitCoverage(points: span,evidence: evidence,width: w) == 0.60)
        precondition(run(line,body)[0].isEmitterEligible)
        for x in 0..<100 { mask[20*w+x] = x < 20 || x % 4 != 3 ? 1 : 0 }
        evidence = SaberEvidence(color: .red, value: values, chroma: values, colorMask: mask, coreMask: mask)
        precondition(fullLitCoverage(points: span,evidence: evidence,width: w) == 0.80)
        precondition(run(line,body)[0].isEmitterEligible)
        print("Core-line guard Swift tests PASS (five cases, real masks, boundaries)")
    }
#if CORELINE_GUARD_DIAGNOSTICS
    static func diagnostics(line: SaberCandidate, body: SaberCandidate,
                            rejected: [SaberCandidate]) async throws {
        let root = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true)
        // 動画 recorder の disk gate は触らず、合成 lossless metadata を入力する。
        let context = CGContext(data: nil, width: 192, height: 96, bitsPerComponent: 8,
            bytesPerRow: 192 * 4, space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue)!
        context.setFillColor(CGColor(red: 1, green: 0, blue: 0, alpha: 1))
        context.fill(CGRect(x: 0, y: 20, width: 20, height: 2))
        let png = root.appendingPathComponent("guard.png")
        let destination = CGImageDestinationCreateWithURL(png as CFURL, "public.png" as CFString, 1, nil)!
        CGImageDestinationAddImage(destination, context.makeImage()!, nil)
        precondition(CGImageDestinationFinalize(destination))
        let analysis = SaberFrameAnalysis(candidates: [.red: rejected, .blue: rejected],
                                         selected: [.red: body.endpoints, .blue: body.endpoints])
        let diagnosticData = try JSONEncoder().encode(DebugRecordingCandidateDiagnostics(analysis: analysis))
        let candidateDiagnostics = try JSONSerialization.jsonObject(with: diagnosticData)
        let detectionData = try JSONEncoder().encode(DebugRecordingDetection(endpoints: body.endpoints))
        let detection = try JSONSerialization.jsonObject(with: detectionData)
        let frames: [[String: Any]] = (0..<5).map { i in
            ["frameID": 1000+i, "presentationTimeSeconds": Double(i)/30,
             "red": detection, "blue": detection,
             "redDetectionSucceeded": true, "blueDetectionSucceeded": true,
             "candidateDiagnostics": candidateDiagnostics,
             "manualCaptured": i == 2, "manualFileName": "guard.png"]
        }
        let metadata: [String: Any] = ["sessionID": "phonesaber_guard_synthetic",
            "activeColors": ["red","blue"], "frames": frames]
        let data = try JSONSerialization.data(withJSONObject: metadata)
        let metadataURL = root.appendingPathComponent("metadata.json")
        try data.write(to: metadataURL)
        let bundle = try DebugRecordingTriageBuilder.build(metadataData: data,
            metadataURL: metadataURL, forensicDirectoryURL: root)
        print("Guard diagnostics bundle: \(bundle.path)")
        let encoded = try JSONEncoder().encode(DebugRecordingCandidate(index: 0,
            candidate: rejected[0], selectedIndex: 1))
        let diagnostic = try JSONSerialization.jsonObject(with: encoded) as! [String: Any]
        precondition((diagnostic["rejectionReasons"] as! [String]).contains("core-line-overlinked"))
        precondition((diagnostic["coreLineGuard"] as! [String: Any])["supportingIndex"] as? Int == 1)
    }
#endif

}
