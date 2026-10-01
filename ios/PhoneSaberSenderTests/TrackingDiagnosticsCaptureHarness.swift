// Synthetic capture input for the real recording/triage pipeline. No LLMs.
import AVFoundation
import CoreImage
import Foundation

@main
struct TrackingDiagnosticsCaptureHarness {
    static func candidate(x: Int, rotated: Bool = false, fallback: Bool = true) throws -> SaberCandidate {
        let points = (0...60).flatMap { a in (0...4).map { b in
            rotated ? PixelPoint(x: x + b, y: 12 + a) : PixelPoint(x: x + a, y: 30 + b)
        } }
        guard let base = saberCandidate(from: points, width: 192, height: 96,
                                        collectEndpointDiagnostics: true) else {
            throw NSError(domain: "fixtureCandidate", code: 1)
        }
        // Fixture changes only a captured diagnostic path, never production logic.
        return SaberCandidate(
            source: base.source,
            radiance: base.radiance,
            comparisonEndpoints: base.comparisonEndpoints,
            endpoints: base.endpoints,
            boundingBox: base.boundingBox,
            score: 80,
            scoreBreakdown: base.scoreBreakdown,
            isEmitterEligible: true,
            isCompactRed: base.isCompactRed,
            peakValue: base.peakValue,
            meanValue: base.meanValue,
            highValueRatio: base.highValueRatio,
            meanColorPurity: base.meanColorPurity,
            clippedWhiteRatio: base.clippedWhiteRatio,
            brightnessVariation: base.brightnessVariation,
            localContrast: base.localContrast,
            longitudinalHighCoverage: base.longitudinalHighCoverage,
            widthVariation: base.widthVariation,
            coreSupportRatio: base.coreSupportRatio,
            longitudinalCoreCoverage: base.longitudinalCoreCoverage,
            longitudinalContinuity: base.longitudinalContinuity,
            largestLongitudinalGap: base.largestLongitudinalGap,
            retainedBodyRatio: base.retainedBodyRatio,
            rawPCASpan: base.rawPCASpan,
            robustMainIntervalEndpoints: base.robustMainIntervalEndpoints,
            robustMainIntervalLength: base.robustMainIntervalLength,
            axialDensity: base.axialDensity,
            componentArea: base.componentArea,
            pointCount: base.pointCount,
            usedPointLEDFallback: fallback,
            diagnosticRejections: base.diagnosticRejections,
            endpointDiagnosticTrace: base.endpointDiagnosticTrace)
    }

    static func buffer(_ selected: SaberCandidate, alternatives: [SaberCandidate], id: Int) throws -> CVPixelBuffer {
        var output: CVPixelBuffer?
        guard CVPixelBufferCreate(kCFAllocatorDefault, 192, 96, kCVPixelFormatType_32BGRA,
            nil, &output) == kCVReturnSuccess, let output else { throw NSError(domain: "buffer", code: 1) }
        CVPixelBufferLockBaseAddress(output, [])
        defer { CVPixelBufferUnlockBaseAddress(output, []) }
        let stride = CVPixelBufferGetBytesPerRow(output)
        let bytes = CVPixelBufferGetBaseAddress(output)!.assumingMemoryBound(to: UInt8.self)
        memset(bytes, 0, stride * 96)
        for y in 0..<96 { for x in 0..<192 { bytes[y * stride + x * 4 + 3] = 255 } }
        for c in [selected] + alternatives {
            let box = c.boundingBox
            for y in max(0, box.minY)...min(95, box.maxY) {
                for x in max(0, box.minX)...min(191, box.maxX) {
                    bytes[y * stride + x * 4 + 2] = 255
                }
            }
        }
        // One identifiable pixel verifies that PNG contents match their metadata.
        for y in 0..<96 { bytes[y * stride] = UInt8(id) }
        return output
    }

    static func main() async throws {
        let root = URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
        var output: [String: Any] = [:]
        for scenario in ["stable", "candidate-switch", "raw-jump", "path-switch", "emitted-stable",
                         "bridge", "edge-absence", "bridge-switch"] {
            let directory = root.appendingPathComponent(scenario, isDirectory: true)
            let recorder = try DebugVideoRecorder(directory: directory, date: Date(timeIntervalSince1970: 1_700_000_000))
            try recorder.prepare(width: 192, height: 96)
            for offset in 0..<25 {
                // "bridge-switch": a short loss AND a later candidate-selection switch.
                let switched = (scenario == "candidate-switch" && offset >= 10)
                    || (scenario == "bridge-switch" && offset >= 15)
                let rotated = scenario == "raw-jump" && offset >= 10
                let path = scenario == "path-switch" && offset >= 10
                let selected = try candidate(x: switched ? 110 : 10, rotated: rotated, fallback: !path)
                let alternatives = ["candidate-switch", "bridge-switch"].contains(scenario)
                    ? [try candidate(x: switched ? 10 : 110)] : []
                let pixels = try buffer(selected, alternatives: alternatives, id: offset)
                // "bridge": the saber is lost for two frames between two detections.
                // "edge-absence": it is only in view in the middle of the recording.
                let present = scenario == "bridge" ? !(10...11).contains(offset)
                    : (scenario == "bridge-switch" ? !(5...6).contains(offset)
                    : (scenario == "edge-absence" ? (6...18).contains(offset) : true))
                let analysis = present
                    ? SaberFrameAnalysis(candidates: [.red: [selected] + alternatives], selected: [.red: selected.endpoints])
                    : SaberFrameAnalysis(candidates: [.red: []], selected: [:])
                let result = DetectedSaber(endpoints: selected.endpoints, color: .red, isFresh: true)
                CVPixelBufferLockBaseAddress(pixels, .readOnly)
                let appended = recorder.append(pixelBuffer: pixels,
                    presentationTime: CMTime(value: Int64(offset), timescale: 30), frameID: UInt64(1000 + offset),
                    results: present ? [result] : [], analysis: analysis, processingTimeSeconds: 0.005)
                CVPixelBufferUnlockBaseAddress(pixels, .readOnly)
                guard appended == .accepted else { throw NSError(domain: "appendRejected-\(scenario)-\(offset)", code: 1) }
                let p = selected.endpoints
                if present {
                    recorder.recordTransmission(frameID: UInt64(1000 + offset), color: "red",
                        coordinates: "\(p.0.x),\(p.0.y),\(p.1.x),\(p.1.y)", sourceEndpoints: p)
                }
                // Let the real video writer drain; frame IDs/timestamps stay fixed.
                try await Task.sleep(nanoseconds: 40_000_000)
            }
            let recording: DebugRecordingResult = try await withCheckedThrowingContinuation { continuation in
                recorder.finish { continuation.resume(with: $0) }
            }
            guard let bundle = recording.triageBundleURL else {
                // Absence outside a bridge is never an evidence image, so no bundle exists.
                guard scenario == "edge-absence" else {
                    throw NSError(domain: recording.triageErrorMessage ?? "triageFailed", code: 1)
                }
                output[scenario] = ["bundle": NSNull(), "metadata": recording.metadataURL.path,
                                    "forensic": (recording.forensicDirectoryURL?.path as Any?) ?? NSNull()]
                continue
            }
            let summary = try JSONSerialization.jsonObject(with: Data(contentsOf:
                bundle.appendingPathComponent("summary.json"))) as! [String: Any]
            let images = summary["images"] as! [[String: Any]]
            let ciContext = CIContext(options: [.cacheIntermediates: false])
            for entry in images {
                let frameID = entry["frameID"] as! Int
                let image = CIImage(contentsOf: bundle.appendingPathComponent(entry["path"] as! String))!
                var pixel = [UInt8](repeating: 0, count: 4)
                ciContext.render(image, toBitmap: &pixel, rowBytes: 4,
                    bounds: CGRect(x: 0, y: 0, width: 1, height: 1), format: .BGRA8,
                    colorSpace: CGColorSpaceCreateDeviceRGB())
                guard pixel[0] == UInt8(frameID - 1000) else {
                    throw NSError(domain: "pngMappingMismatch-\(frameID)-\(pixel)", code: 1)
                }
            }
            output[scenario] = ["bundle": bundle.path, "metadata": recording.metadataURL.path,
                                "forensic": (recording.forensicDirectoryURL?.path as Any?) ?? NSNull(),
                                "pngMappingVerified": images.count]
        }
        let data = try JSONSerialization.data(withJSONObject: output, options: [.sortedKeys])
        try data.write(to: root.appendingPathComponent("capture-index.json"))
    }
}
