import Foundation

private struct Arguments {
    let width: Int
    let height: Int
    let sampleStep: Int

    init?(_ values: [String]) {
        guard values.count == 4,
              let width = Int(values[1]), width > 0,
              let height = Int(values[2]), height > 0,
              let sampleStep = Int(values[3]), sampleStep > 0 else { return nil }
        self.width = width
        self.height = height
        self.sampleStep = sampleStep
    }
}

private func pointJSON(_ point: PixelPoint) -> [String: Int] {
    ["x": point.x, "y": point.y]
}

private func candidateJSON(_ candidate: SaberCandidate) -> [String: Any] {
    [
        "source": candidate.source,
        "score": candidate.score,
        "eligible": candidate.isEmitterEligible,
        "endpoints": [pointJSON(candidate.endpoints.0), pointJSON(candidate.endpoints.1)],
        "bounding_box": [
            "min_x": candidate.boundingBox.minX,
            "min_y": candidate.boundingBox.minY,
            "max_x": candidate.boundingBox.maxX,
            "max_y": candidate.boundingBox.maxY,
        ],
        "peak_value": candidate.peakValue,
        "mean_value": candidate.meanValue,
        "high_value_ratio": candidate.highValueRatio,
        "color_purity": candidate.meanColorPurity,
        "local_contrast": candidate.localContrast,
        "core_support": candidate.coreSupportRatio,
        "longitudinal_core_coverage": candidate.longitudinalCoreCoverage,
    ]
}

private func selectedJSON(_ endpoints: (PixelPoint, PixelPoint)?) -> Any {
    guard let endpoints else { return NSNull() }
    return [pointJSON(endpoints.0), pointJSON(endpoints.1)]
}

private func readExactly(_ handle: FileHandle, count: Int) throws -> Data? {
    var data = Data()
    data.reserveCapacity(count)
    while data.count < count {
        let chunk = try handle.read(upToCount: count - data.count) ?? Data()
        if chunk.isEmpty { return data.isEmpty ? nil : data }
        data.append(chunk)
    }
    return data
}

@main
private enum VideoDetectionDiagnostic {
    static func main() throws {
        guard let arguments = Arguments(CommandLine.arguments) else {
            FileHandle.standardError.write(Data("usage: video-detection-diagnostic WIDTH HEIGHT SAMPLE_STEP\n".utf8))
            Foundation.exit(2)
        }
        let frameByteCount = arguments.width * arguments.height * 4
        var frameIndex = 0
        while let frame = try readExactly(FileHandle.standardInput, count: frameByteCount) {
            guard frame.count == frameByteCount else {
                FileHandle.standardError.write(Data("truncated BGRA frame at index \(frameIndex)\n".utf8))
                Foundation.exit(3)
            }
            let analysis = frame.withUnsafeBytes { rawBuffer -> SaberFrameAnalysis in
                analyzeSabers(
                    baseAddress: rawBuffer.bindMemory(to: UInt8.self).baseAddress!,
                    width: arguments.width,
                    height: arguments.height,
                    bytesPerRow: arguments.width * 4,
                    redThreshold: ColorThreshold(),
                    blueThreshold: ColorThreshold(),
                    sampleStep: arguments.sampleStep,
                    collectProfile: true
                )
            }
            var colors: [String: Any] = [:]
            for (name, color) in [("red", SaberColor.red), ("blue", SaberColor.blue)] {
                colors[name] = [
                    "selected": selectedJSON(analysis.selected[color]),
                    "candidates": (analysis.candidates[color] ?? []).map(candidateJSON),
                ]
            }
            let profile = analysis.profile
            let output: [String: Any] = [
                "frame": frameIndex,
                "colors": colors,
                "profile": [
                    "total_ms": profile?.totalMs ?? 0,
                    "scan_ms": profile?.pixelScanHSVMaskMs ?? 0,
                    "morphology_ms": profile?.morphologyMs ?? 0,
                    "component_score_ms": profile?.componentAndScoreMs ?? 0,
                    "line_proposal_ms": profile?.lineProposalMs ?? 0,
                    "line_score_ms": profile?.lineScoreMs ?? 0,
                    "selection_ms": profile?.selectionMs ?? 0,
                    "candidate_count": profile?.candidateCount ?? 0,
                ],
            ]
            let encoded = try JSONSerialization.data(withJSONObject: output, options: [.sortedKeys])
            FileHandle.standardOutput.write(encoded)
            FileHandle.standardOutput.write(Data([0x0a]))
            frameIndex += 1
        }
    }
}
