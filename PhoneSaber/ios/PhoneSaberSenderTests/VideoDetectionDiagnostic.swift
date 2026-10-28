import Foundation

private struct Arguments {
    let width: Int
    let height: Int
    let sampleStep: Int
    let shutterEmitter: ShutterEmitterMode

    init?(_ values: [String]) {
        guard values.count == 4 || values.count == 7,
              let width = Int(values[1]), width > 0,
              let height = Int(values[2]), height > 0,
              let sampleStep = Int(values[3]), sampleStep > 0 else { return nil }
        self.width = width
        self.height = height
        self.sampleStep = sampleStep
        if values.count == 7 {
            guard let shutter = Int(values[4]), shutter >= 0,
                  let iso = Int(values[5]), iso > 0,
                  ["0", "1"].contains(values[6]) else { return nil }
            shutterEmitter = ShutterEmitterMode(shutter: shutter, iso: iso, enabled: values[6] == "1")
        } else {
            shutterEmitter = ShutterEmitterMode()
        }
    }
}

private func pointJSON(_ point: PixelPoint) -> [String: Int] {
    ["x": point.x, "y": point.y]
}

private func candidateJSON(_ candidate: SaberCandidate, emitterMetrics: Bool, bitPatterns: Bool) -> [String: Any] {
    var result: [String: Any] = [
        "blueNoDeepSupport": candidate.blueNoDeepSupport.map { verdict in
            ["applied": verdict.applied, "deepCount": verdict.deepCount,
             "pixelCount": verdict.pixelCount, "rejected": verdict.rejected,
             "rejectionReason": verdict.rejectionReason.map { $0 as Any } ?? NSNull()] as [String: Any]
        } ?? NSNull(),
        "warmNoDeepRed": candidate.warmNoDeepRed.map { verdict in
            ["applied": verdict.applied, "deepCount": verdict.deepCount,
             "warmCount": verdict.warmCount, "pixelCount": verdict.pixelCount,
             "warmFrac": verdict.warmFrac, "rejected": verdict.rejected,
             "rejectionReason": verdict.rejectionReason.map { $0 as Any } ?? NSNull()] as [String: Any]
        } ?? NSNull(),
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
        "longitudinal_continuity": candidate.longitudinalContinuity,
        "largest_longitudinal_gap": candidate.largestLongitudinalGap,
        "retained_body_ratio": candidate.retainedBodyRatio,
        "raw_pca_span": candidate.rawPCASpan,
        "robust_body_length": candidate.robustMainIntervalLength,
        "axial_density": candidate.axialDensity,
        "point_count": candidate.pointCount,
        "used_point_led_fallback": candidate.usedPointLEDFallback,
    ]
    // 分布計測時だけ追加する。通常のbit一致parityの出力形式は維持する。
    if emitterMetrics {
        result["emitter_score"] = candidate.observerEmitterInputs.map { $0.emitterScore as Any } ?? NSNull()
        result["aspect"] = candidate.observerEmitterInputs.map { $0.aspect as Any } ?? NSNull()
    }
    if bitPatterns { result["scoreBits"] = String(candidate.score.bitPattern) }
    if CoreLineGuard.enabled {
        result["coreLineGuard"] = candidate.coreLineGuard.map { v in
            ["supportingIndex": v.supportingIndex, "supportingSource": v.supportingSource,
             "supportingEndpoints": v.supportingEndpoints, "litCoverage": v.litCoverage,
             "supportingLitCoverage": v.supportingLitCoverage,
             "coverageBits": String(v.litCoverage.bitPattern),
             "supportingCoverageBits": String(v.supportingLitCoverage.bitPattern)] as [String: Any]
        } ?? NSNull()
    }
    return result
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
            FileHandle.standardError.write(Data("usage: video-detection-diagnostic WIDTH HEIGHT SAMPLE_STEP [SHUTTER ISO EMITTER_0_OR_1]\n".utf8))
            Foundation.exit(2)
        }
        let emitterMetrics = ProcessInfo.processInfo.environment["PHONESABER_EMITTER_METRICS"] == "1"
        let bitPatterns = ProcessInfo.processInfo.environment["PHONESABER_BIT_PATTERNS"] == "1"
        let components = ProcessInfo.processInfo.environment["PHONESABER_COMPONENT_DIAGNOSTICS"] == "1"
        let lightweight = ProcessInfo.processInfo.environment["PHONESABER_LIGHTWEIGHT"] == "1"
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
                    collectProfile: true,
                    collectPipelineDiagnostics: !lightweight,
                    collectObserverEvidence: emitterMetrics,
                    collectComponentDiagnostics: components,
                    shutterEmitter: arguments.shutterEmitter
                )
            }
            var colors: [String: Any] = [:]
            for (name, color) in [("red", SaberColor.red), ("blue", SaberColor.blue)] {
                colors[name] = [
                    "selected": selectedJSON(analysis.selected[color]),
                    "candidates": (analysis.candidates[color] ?? []).map { candidateJSON($0, emitterMetrics: emitterMetrics, bitPatterns: bitPatterns) },
                ]
            }
            let profile = analysis.profile
            var output: [String: Any] = [
                "frame": frameIndex,
                "shutterEmitter": ["shutter": arguments.shutterEmitter.shutter,
                                   "iso": arguments.shutterEmitter.iso,
                                   "enabled": arguments.shutterEmitter.enabled,
                                   "active": arguments.shutterEmitter.active],
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
                    "line_proposal_count": profile?.lineProposalCount ?? 0,
                ],
                "pipeline": Dictionary(uniqueKeysWithValues: [
                    ("red", SaberColor.red), ("blue", SaberColor.blue)
                ].map { name, color in
                    let value = analysis.pipelineDiagnostics?[color]
                    return (name, [
                        "mask_pixels": value?.maskPixelCount ?? 0,
                        "morphology_pixels": value?.morphologyPixelCount ?? 0,
                        "components": value?.connectedComponentCount ?? 0,
                    ])
                }),
            ]
            if components {
                output["components"] = Dictionary(uniqueKeysWithValues: [("red", SaberColor.red), ("blue", .blue)].map {
                    ($0.0, analysis.componentDiagnostics?[$0.1]?.snapshot.json() ?? ["state": "notCollected"])
                })
            }
            let encoded = try JSONSerialization.data(withJSONObject: output, options: [.sortedKeys])
            FileHandle.standardOutput.write(encoded)
            FileHandle.standardOutput.write(Data([0x0a]))
            frameIndex += 1
        }
    }
}
