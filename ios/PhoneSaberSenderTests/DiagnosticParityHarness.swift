import CoreGraphics
import Foundation
import ImageIO

/// Host-side check that Debug Recording diagnostics never change recognition.
///
/// Compiled with only DetectionCore.swift and BGRADetection.swift by
/// Tools/test_diagnostic_parity.py and run both with Swift's normal hash seed and
/// with SWIFT_DETERMINISTIC_HASHING=1. Diagnostics on/off must agree bit for bit
/// on every candidate (core-line scoring no longer follows Set iteration order).
@main
enum DiagnosticParityHarness {
    private static func load(_ url: URL) -> (bytes: [UInt8], width: Int, height: Int)? {
        guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
              let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { return nil }
        let width = image.width, height = image.height
        var bytes = Array(repeating: UInt8(0), count: width * height * 4)
        let rendered = bytes.withUnsafeMutableBytes { raw -> Bool in
            guard let context = CGContext(data: raw.baseAddress, width: width, height: height,
                                          bitsPerComponent: 8, bytesPerRow: width * 4,
                                          space: CGColorSpaceCreateDeviceRGB(),
                                          bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue
                                            | CGBitmapInfo.byteOrder32Little.rawValue) else { return false }
            context.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
            return true
        }
        return rendered ? (bytes, width, height) : nil
    }

    /// Every recognition-relevant value, floats as exact bit patterns.
    private static func signature(_ analysis: SaberFrameAnalysis) -> [String] {
        var lines: [String] = []
        for color in [SaberColor.red, .blue] {
            if let s = analysis.selected[color] {
                lines.append("\(color) selected \(s.0.x),\(s.0.y),\(s.1.x),\(s.1.y)")
            }
            for c in analysis.candidates[color] ?? [] {
                let floats = [c.score, c.scoreBreakdown.total, c.radiance, c.meanValue, c.highValueRatio,
                              c.meanColorPurity, c.clippedWhiteRatio, c.brightnessVariation, c.localContrast,
                              c.longitudinalHighCoverage, c.widthVariation, c.coreSupportRatio,
                              c.longitudinalCoreCoverage, c.longitudinalContinuity, c.retainedBodyRatio,
                              c.rawPCASpan, c.robustMainIntervalLength, c.axialDensity]
                    .map { String($0.bitPattern) }.joined(separator: ",")
                lines.append("\(color) \(c.source) eligible=\(c.isEmitterEligible) compact=\(c.isCompactRed) "
                    + "end=\(c.endpoints.0.x),\(c.endpoints.0.y),\(c.endpoints.1.x),\(c.endpoints.1.y) "
                    + "raw=\(c.comparisonEndpoints.0.x),\(c.comparisonEndpoints.0.y),"
                    + "\(c.comparisonEndpoints.1.x),\(c.comparisonEndpoints.1.y) "
                    + "peak=\(c.peakValue) points=\(c.pointCount) gap=\(c.largestLongitudinalGap) "
                    + "fallback=\(c.usedPointLEDFallback) f=\(floats)")
            }
        }
        return lines
    }

    static func main() throws {
        guard CommandLine.arguments.count == 2 else {
            FileHandle.standardError.write(Data("usage: DiagnosticParityHarness <fixture-root>\n".utf8))
            exit(2)
        }
        let root = URL(fileURLWithPath: CommandLine.arguments[1])
        let files = (FileManager.default.enumerator(at: root, includingPropertiesForKeys: nil)?
            .compactMap { $0 as? URL } ?? [])
            .filter { $0.pathExtension.lowercased() == "png" }
            .sorted { $0.path < $1.path }
        var compared: [String] = [], mismatched: [String] = []
        var emitterTraces = 0, shadowVerdicts = 0
        for url in files {
            guard let image = load(url) else { continue }
            func run(_ diagnostics: Bool, _ profile: Bool) -> SaberFrameAnalysis {
                analyzeSabers(in: image.bytes, width: image.width, height: image.height,
                              bytesPerRow: image.width * 4, redThreshold: ColorThreshold(),
                              blueThreshold: ColorThreshold(), collectProfile: profile,
                              collectPipelineDiagnostics: diagnostics)
            }
            let name = url.path.replacingOccurrences(of: root.path + "/", with: "")
            compared.append(name)
            let off = run(false, false), on = run(true, false)
            if signature(off) != signature(on) || signature(off) != signature(run(true, true)) {
                mismatched.append(name)
            }
            for candidate in on.candidates.values.joined() {
                guard let emitter = candidate.endpointDiagnosticTrace?.emitter else { continue }
                emitterTraces += 1
                if emitter.shadowR7e != nil { shadowVerdicts += 1 }
            }
        }
        let summary: [String: Any] = ["compared": compared, "mismatched": mismatched,
                                      "emitterTraces": emitterTraces, "shadowVerdicts": shadowVerdicts]
        let data = try JSONSerialization.data(withJSONObject: summary, options: [.sortedKeys])
        FileHandle.standardOutput.write(data)
    }
}
