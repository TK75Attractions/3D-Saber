import Foundation

struct SaberFrameAnalysis {
    let candidates: [SaberColor: [SaberCandidate]]
    let selected: [SaberColor: (PixelPoint, PixelPoint)]
    let profile: SaberDetectionProfile?

    init(candidates: [SaberColor: [SaberCandidate]],
         selected: [SaberColor: (PixelPoint, PixelPoint)],
         profile: SaberDetectionProfile? = nil) {
        self.candidates = candidates
        self.selected = selected
        self.profile = profile
    }
}

struct SaberDetectionProfile {
    var pixelScanHSVMaskMs = 0.0
    var morphologyMs = 0.0
    var componentAndScoreMs = 0.0
    var lineProposalMs = 0.0
    var lineScoreMs = 0.0
    var selectionMs = 0.0
    var shapeAndAxisMs = 0.0
    var brightnessContrastColorMs = 0.0
    var endpointAndBoundsMs = 0.0
    var componentTraversalAndProposalOverheadMs = 0.0
    var totalMs = 0.0
    var colorPixelCount = 0
    var brightCorePixelCount = 0
    var lineProposalCount = 0
    var candidateCount = 0
}

private func candidateAxisDistance(_ lhs: SaberCandidate, _ rhs: SaberCandidate) -> Double {
    func distance(_ a: PixelPoint, _ b: PixelPoint) -> Double {
        hypot(Double(a.x - b.x), Double(a.y - b.y))
    }
    let forward = distance(lhs.endpoints.0, rhs.endpoints.0)
        + distance(lhs.endpoints.1, rhs.endpoints.1)
    let reversed = distance(lhs.endpoints.0, rhs.endpoints.1)
        + distance(lhs.endpoints.1, rhs.endpoints.0)
    return min(forward, reversed) / 2.0
}

private func candidateIsSubsegment(_ shorter: SaberCandidate, of longer: SaberCandidate) -> Bool {
    let longDX = Double(longer.endpoints.1.x - longer.endpoints.0.x)
    let longDY = Double(longer.endpoints.1.y - longer.endpoints.0.y)
    let longLength = hypot(longDX, longDY)
    let shortDX = Double(shorter.endpoints.1.x - shorter.endpoints.0.x)
    let shortDY = Double(shorter.endpoints.1.y - shorter.endpoints.0.y)
    let shortLength = hypot(shortDX, shortDY)
    guard longLength >= shortLength * 1.50, shortLength > 0 else { return false }
    let axisX = longDX / longLength, axisY = longDY / longLength
    let shortAxisX = shortDX / shortLength, shortAxisY = shortDY / shortLength
    guard abs(axisX * shortAxisX + axisY * shortAxisY) >= 0.94 else { return false }
    let normalX = -axisY, normalY = axisX
    return [shorter.endpoints.0, shorter.endpoints.1].allSatisfy { point in
        let dx = Double(point.x - longer.endpoints.0.x)
        let dy = Double(point.y - longer.endpoints.0.y)
        let along = dx * axisX + dy * axisY
        let across = abs(dx * normalX + dy * normalY)
        return along >= -6.0 && along <= longLength + 6.0 && across <= 6.0
    }
}

private struct CoreLinePeak {
    let axisX: Double
    let axisY: Double
    let rho: Int
    let votes: Int
}

/// Hough-like line proposals from color-supported bright cores. This does not
/// choose a winner; it only turns aligned, possibly disconnected LED packages
/// into components that the common candidate scorer can compare.
private func coreLineProposals(coreMask: [UInt8], colorMask: [UInt8],
                               width: Int, height: Int) -> [[PixelPoint]] {
    let points = coreMask.indices.compactMap { index -> PixelPoint? in
        coreMask[index] == 0 ? nil : PixelPoint(x: index % width, y: index / width)
    }
    guard points.count >= 4 else { return [] }
    let diagonal = Int(ceil(hypot(Double(width), Double(height))))
    let minimumVotes = max(4, Int(Double(min(width, height)) * 0.025))
    var peaks: [CoreLinePeak] = []
    for degrees in stride(from: 0, to: 180, by: 10) {
        let radians = Double(degrees) * .pi / 180.0
        let axisX = cos(radians), axisY = sin(radians)
        let normalX = -axisY, normalY = axisX
        var votes = Array(repeating: 0, count: diagonal * 2 + 1)
        for point in points {
            let rho = Int((Double(point.x) * normalX + Double(point.y) * normalY).rounded())
            votes[rho + diagonal] += 1
        }
        for index in votes.indices where votes[index] >= minimumVotes {
            peaks.append(CoreLinePeak(axisX: axisX, axisY: axisY,
                                      rho: index - diagonal, votes: votes[index]))
        }
    }
    peaks.sort { $0.votes > $1.votes }
    var proposals: [[PixelPoint]] = []
    var accepted: [CoreLinePeak] = []
    var examinedCount = 0
    for initialPeak in peaks {
        guard proposals.count < 18, examinedCount < 180 else { break }
        examinedCount += 1
        // Refit a local core cloud rather than returning the 10-degree Hough
        // bin as geometry. A cross-section through a wide glow must not become
        // an independent short blade just because it has a high mean value.
        let neighborhood = points.filter {
            abs(Double($0.x) * -initialPeak.axisY + Double($0.y) * initialPeak.axisX - Double(initialPeak.rho)) <= 6
        }
        guard neighborhood.count >= minimumVotes else { continue }
        let mx = neighborhood.reduce(0.0) { $0 + Double($1.x) } / Double(neighborhood.count)
        let my = neighborhood.reduce(0.0) { $0 + Double($1.y) } / Double(neighborhood.count)
        var xx = 0.0, yy = 0.0, xy = 0.0
        for p in neighborhood {
            let dx = Double(p.x) - mx, dy = Double(p.y) - my
            xx += dx * dx; yy += dy * dy; xy += dx * dy
        }
        let fittedAngle = 0.5 * atan2(2 * xy, xx - yy)
        let ax = cos(fittedAngle), ay = sin(fittedAngle)
        let peak = CoreLinePeak(axisX: ax, axisY: ay,
                                rho: Int((-ay * mx + ax * my).rounded()), votes: initialPeak.votes)
        let duplicate = accepted.contains {
            abs($0.rho - peak.rho) <= 3
                && abs($0.axisX * peak.axisY - $0.axisY * peak.axisX) < 0.18
        }
        if duplicate { continue }
        let normalX = -peak.axisY, normalY = peak.axisX
        let inliers = points.filter {
            abs(Double($0.x) * normalX + Double($0.y) * normalY - Double(peak.rho)) <= 2.0
        }
        guard inliers.count >= minimumVotes else { continue }
        let projections = inliers.map { Double($0.x) * peak.axisX + Double($0.y) * peak.axisY }
        guard let minT = projections.min(), let maxT = projections.max(),
              maxT - minT >= max(Double(min(width, height)) * 0.06, 24.0) else { continue }
        let nearbyCoreCount = points.reduce(into: 0) { count, point in
            let t = Double(point.x) * peak.axisX + Double(point.y) * peak.axisY
            let distance = abs(Double(point.x) * normalX + Double(point.y) * normalY
                               - Double(peak.rho))
            if t >= minT, t <= maxT, distance <= 8.0 { count += 1 }
        }
        // A square bright object yields Hough votes on arbitrary slices. A
        // blade has most nearby core pixels concentrated around one axis.
        guard Double(inliers.count) / Double(max(nearbyCoreCount, 1)) >= 0.28 else { continue }
        accepted.append(peak)
        var minPointX = width, minPointY = height, maxPointX = 0, maxPointY = 0
        for point in inliers {
            minPointX = min(minPointX, point.x); minPointY = min(minPointY, point.y)
            maxPointX = max(maxPointX, point.x); maxPointY = max(maxPointY, point.y)
        }
        let minX = max(0, minPointX - 4), maxX = min(width - 1, maxPointX + 4)
        let minY = max(0, minPointY - 4), maxY = min(height - 1, maxPointY + 4)
        var proposal: [PixelPoint] = []
        proposal.reserveCapacity(inliers.count * 2)
        for y in minY...maxY {
            for x in minX...maxX {
                let t = Double(x) * peak.axisX + Double(y) * peak.axisY
                guard t >= minT - 1, t <= maxT + 1 else { continue }
                let distance = abs(Double(x) * normalX + Double(y) * normalY - Double(peak.rho))
                let index = y * width + x
                if distance <= 3.0 && (coreMask[index] != 0 || colorMask[index] != 0) {
                    proposal.append(PixelPoint(x: x, y: y))
                }
            }
        }
        // Bridge missing/white LED packages along the fitted core axis.
        var t = minT
        while t <= maxT {
            let x = Int((normalX * Double(peak.rho) + peak.axisX * t).rounded())
            let y = Int((normalY * Double(peak.rho) + peak.axisY * t).rounded())
            if x >= 0, x < width, y >= 0, y < height {
                proposal.append(PixelPoint(x: x, y: y))
            }
            t += 0.75
        }
        proposals.append(proposal)
    }
    return proposals
}

/// Scan BGRA once for both colors, then clean and score the two compact masks.
/// Coordinates remain in the source image's coordinate system.
func analyzeSabers(baseAddress: UnsafePointer<UInt8>, width: Int, height: Int, bytesPerRow: Int,
                   redThreshold: ColorThreshold, blueThreshold: ColorThreshold,
                   sampleStep: Int = 2, collectProfile: Bool = false) -> SaberFrameAnalysis {
    let totalStart = collectProfile ? ProcessInfo.processInfo.systemUptime : 0
    var profile = SaberDetectionProfile()
    let candidateStageProfile = collectProfile ? SaberCandidateStageProfile() : nil
    guard width > 0, height > 0, bytesPerRow >= width * 4 else {
        return SaberFrameAnalysis(candidates: [:], selected: [:])
    }
    let step = max(sampleStep, 1)
    let maskWidth = (width + step - 1) / step
    let maskHeight = (height + step - 1) / step
    var redMask = Array(repeating: UInt8(0), count: maskWidth * maskHeight)
    var blueMask = Array(repeating: UInt8(0), count: maskWidth * maskHeight)
    var redEmitterMask = Array(repeating: UInt8(0), count: maskWidth * maskHeight)
    var blueEmitterMask = Array(repeating: UInt8(0), count: maskWidth * maskHeight)
    var brightCoreMask = Array(repeating: UInt8(0), count: maskWidth * maskHeight)
    var valueMap = Array(repeating: UInt8(0), count: maskWidth * maskHeight)
    var chromaMap = Array(repeating: UInt8(0), count: maskWidth * maskHeight)
    var radianceMap = Array(repeating: UInt8(0), count: maskWidth * maskHeight)
    let scanStart = collectProfile ? ProcessInfo.processInfo.systemUptime : 0
    for sampleY in 0..<maskHeight {
        let y = sampleY * step
        for sampleX in 0..<maskWidth {
            let x = sampleX * step
            let offset = y * bytesPerRow + x * 4
            let blue = baseAddress[offset]
            let green = baseAddress[offset + 1]
            let red = baseAddress[offset + 2]
            let index = sampleY * maskWidth + sampleX
            let hsv = saberHSV(red, green, blue)
            // The second strongest channel distinguishes a white/cyan LED
            // core from single-channel clipping in a saturated reflection.
            let secondChannel = Int(red) + Int(green) + Int(blue)
                - Int(max(red, green, blue)) - Int(min(red, green, blue))
            radianceMap[index] = UInt8(secondChannel)
            valueMap[index] = UInt8(clamping: Int(hsv.value.rounded()))
            chromaMap[index] = UInt8(clamping: Int(hsv.chroma.rounded()))
            if hsv.value >= 235 && secondChannel >= 100 { brightCoreMask[index] = 1 }
            let isEmitterPixel = hsv.value >= 215
                && hsv.chroma / max(hsv.value, 1) >= 0.35
            if matchesSaberHSV(hsv, color: .red, threshold: redThreshold) {
                redMask[index] = 1
                if isEmitterPixel { redEmitterMask[index] = 1 }
            }
            if matchesSaberHSV(hsv, color: .blue, threshold: blueThreshold) {
                blueMask[index] = 1
                if isEmitterPixel { blueEmitterMask[index] = 1 }
            }
        }
    }
    if collectProfile {
        profile.pixelScanHSVMaskMs = (ProcessInfo.processInfo.systemUptime - scanStart) * 1000
        profile.colorPixelCount = redMask.reduce(0) { $0 + Int($1) }
            + blueMask.reduce(0) { $0 + Int($1) }
        profile.brightCorePixelCount = brightCoreMask.reduce(0) { $0 + Int($1) }
    }
    var allCandidates: [SaberColor: [SaberCandidate]] = [:]
    var selected: [SaberColor: (PixelPoint, PixelPoint)] = [:]
    let masks: [(SaberColor, [UInt8], [UInt8])] = [
        (.red, redMask, redEmitterMask),
        (.blue, blueMask, blueEmitterMask)
    ]
    let closeRadius = min(maskWidth, maskHeight) >= 16 ? 2 : 1
    for (color, rawMask, emitterMask) in masks {
        // Most live frames contain at most one saber color. Do not run several
        // full-mask morphology/component passes for an absent color; this is
        // an exact empty-mask fast path and does not alter candidate scoring.
        guard rawMask.contains(1) else {
            allCandidates[color] = []
            continue
        }
        let morphologyStart = collectProfile ? ProcessInfo.processInfo.systemUptime : 0
        let colorNeighborhood = dilateSaberMask(rawMask, width: maskWidth,
                                                height: maskHeight, radius: 2)
        var associatedCore = Array(repeating: UInt8(0), count: rawMask.count)
        for index in rawMask.indices where brightCoreMask[index] != 0
            && colorNeighborhood[index] != 0 {
            associatedCore[index] = 1
        }
        let coreNeighborhood = dilateSaberMask(associatedCore, width: maskWidth,
                                               height: maskHeight, radius: 3)
        var coreAndHaloMask = associatedCore
        for index in rawMask.indices where rawMask[index] != 0
            && coreNeighborhood[index] != 0 {
            coreAndHaloMask[index] = 1
        }
        let closed = closeSaberMask(rawMask, width: maskWidth, height: maskHeight,
                                    radius: closeRadius)
        let cleaned = openSaberMask(closed, width: maskWidth, height: maskHeight)
        if collectProfile {
            profile.morphologyMs += (ProcessInfo.processInfo.systemUptime - morphologyStart) * 1000
        }
        let evidence = SaberEvidence(radiance: radianceMap, value: valueMap, chroma: chromaMap,
                                     colorMask: rawMask, coreMask: associatedCore)
        // A dotted or one-sample-wide LED blade can vanish under opening while
        // a smooth reflection survives it. Always score close-only components
        // as well, then discard duplicates of components already found in the
        // cleaned mask. Shape and emitter evidence reject isolated noise.
        let componentStart = collectProfile ? ProcessInfo.processInfo.systemUptime : 0
        var candidates = saberCandidates(in: cleaned, width: maskWidth, height: maskHeight,
                                         evidence: evidence, stageProfile: candidateStageProfile)
        let closedCandidates = saberCandidates(
            in: closed,
            width: maskWidth, height: maskHeight, evidence: evidence,
            stageProfile: candidateStageProfile
        )
        for var candidate in closedCandidates where !candidates.contains(where: {
            candidateAxisDistance($0, candidate) <= 3.0
        }) {
            candidate.source = "color-close"
            candidates.append(candidate)
        }
        // A bright LED array can be embedded in a broad colored glow that is
        // not itself blade-shaped. Extract concentrated, high-purity emitter
        // pixels as an additional geometry proposal; this changes candidate
        // generation, not the configurable HSV color gate.
        let emitterCandidates = emitterMask.contains(1)
            ? saberCandidates(
                in: closeSaberMask(emitterMask, width: maskWidth, height: maskHeight,
                                   radius: closeRadius),
                width: maskWidth, height: maskHeight, evidence: evidence,
                stageProfile: candidateStageProfile
            ) : []
        for var candidate in emitterCandidates where !candidates.contains(where: {
            candidateAxisDistance($0, candidate) <= 3.0
        }) {
            candidate.source = "color-emitter"
            candidates.append(candidate)
        }
        // Start an additional proposal from bright/white cores, then attach
        // only the nearby target-color halo. This preserves the saturated LED
        // center that is intentionally absent from a pure HSV color mask.
        let hasAssociatedCore = associatedCore.contains(1)
        let coreCandidates = hasAssociatedCore
            ? saberCandidates(
                in: closeSaberMask(coreAndHaloMask, width: maskWidth, height: maskHeight,
                                   radius: closeRadius),
                width: maskWidth, height: maskHeight, evidence: evidence,
                stageProfile: candidateStageProfile
            ) : []
        for var candidate in coreCandidates where !candidates.contains(where: {
            candidateAxisDistance($0, candidate) <= 3.0
        }) {
            candidate.source = "core-halo"
            candidates.append(candidate)
        }
        // An empty core cannot produce a candidate; avoid an unnecessary close pass.
        if hasAssociatedCore {
            let connectedCore = closeSaberMask(associatedCore, width: maskWidth, height: maskHeight, radius: 4)
            for var candidate in saberCandidates(in: connectedCore, width: maskWidth, height: maskHeight,
                                                  evidence: evidence, stageProfile: candidateStageProfile) {
                let length = hypot(Double(candidate.endpoints.1.x - candidate.endpoints.0.x),
                                   Double(candidate.endpoints.1.y - candidate.endpoints.0.y))
                guard length >= max(12, Double(min(maskWidth, maskHeight)) * 0.10) else { continue }
                candidate.source = "connected-core"
                candidates.append(candidate)
            }
        }
        if collectProfile {
            profile.componentAndScoreMs += (ProcessInfo.processInfo.systemUptime - componentStart) * 1000
        }
        let proposalStart = collectProfile ? ProcessInfo.processInfo.systemUptime : 0
        let proposals = coreLineProposals(coreMask: associatedCore, colorMask: rawMask,
                                          width: maskWidth, height: maskHeight)
        if collectProfile {
            profile.lineProposalMs += (ProcessInfo.processInfo.systemUptime - proposalStart) * 1000
            profile.lineProposalCount += proposals.count
        }
        let lineScoreStart = collectProfile ? ProcessInfo.processInfo.systemUptime : 0
        for proposal in proposals {
            if var candidate = saberCandidate(from: proposal, width: maskWidth,
                                              height: maskHeight, evidence: evidence,
                                              stageProfile: candidateStageProfile),
               !candidates.contains(where: {
                candidateAxisDistance($0, candidate) <= 10.0
                    || candidateIsSubsegment(candidate, of: $0)
               }) {
                candidate.source = "core-line"
                // A line proposal is a fallback for disconnected LEDs. Avoid
                // cutting a complete connected emitter into competing slices.
                let overlapsCore = candidates.contains { existing in
                    guard existing.source == "connected-core" else { return false }
                    let a = existing.endpoints.0, b = existing.endpoints.1
                    let dx = Double(b.x - a.x), dy = Double(b.y - a.y)
                    let length = hypot(dx, dy)
                    let cx = Double(candidate.endpoints.0.x + candidate.endpoints.1.x) / 2 - Double(a.x)
                    let cy = Double(candidate.endpoints.0.y + candidate.endpoints.1.y) / 2 - Double(a.y)
                    let along = (cx * dx + cy * dy) / length
                    let across = abs(cx * dy - cy * dx) / length
                    return along >= -4 && along <= length + 4 && across <= 8
                }
                if overlapsCore {
                    candidate.isEmitterEligible = false
                    candidate.source = "core-line-overlap"
                    candidate.scoreBreakdown.proposalPenalty = -candidate.score * 0.5
                    candidate.score = candidate.scoreBreakdown.total
                }
                candidates.append(candidate)
            }
        }
        if collectProfile {
            profile.lineScoreMs += (ProcessInfo.processInfo.systemUptime - lineScoreStart) * 1000
            profile.candidateCount += candidates.count
        }
        let selectionStart = collectProfile ? ProcessInfo.processInfo.systemUptime : 0
        let completeCandidates = candidates
        for index in candidates.indices where candidates[index].isEmitterEligible
            && candidates[index].source != "connected-core" {
            if completeCandidates.contains(where: {
                $0.isEmitterEligible && !$0.source.hasPrefix("core-line")
                    && candidateIsSubsegment(candidates[index], of: $0)
            }) {
                candidates[index].isEmitterEligible = false
                candidates[index].source += "-subsegment"
                candidates[index].scoreBreakdown.proposalPenalty = -candidates[index].score * 0.5
                candidates[index].score = candidates[index].scoreBreakdown.total
            }
        }
        candidates.sort { $0.score > $1.score }
        let scaled = candidates.map { candidate in
            SaberCandidate(
                source: candidate.source,
                radiance: candidate.radiance,
                endpoints: (PixelPoint(x: candidate.endpoints.0.x * step,
                                       y: candidate.endpoints.0.y * step),
                            PixelPoint(x: candidate.endpoints.1.x * step,
                                       y: candidate.endpoints.1.y * step)),
                boundingBox: SaberBoundingBox(
                    minX: candidate.boundingBox.minX * step,
                    minY: candidate.boundingBox.minY * step,
                    maxX: candidate.boundingBox.maxX * step,
                    maxY: candidate.boundingBox.maxY * step
                ),
                score: candidate.score, scoreBreakdown: candidate.scoreBreakdown,
                isEmitterEligible: candidate.isEmitterEligible,
                peakValue: candidate.peakValue, meanValue: candidate.meanValue,
                highValueRatio: candidate.highValueRatio,
                meanColorPurity: candidate.meanColorPurity,
                clippedWhiteRatio: candidate.clippedWhiteRatio,
                brightnessVariation: candidate.brightnessVariation,
                localContrast: candidate.localContrast,
                longitudinalHighCoverage: candidate.longitudinalHighCoverage,
                widthVariation: candidate.widthVariation,
                coreSupportRatio: candidate.coreSupportRatio,
                longitudinalCoreCoverage: candidate.longitudinalCoreCoverage
            )
        }
        allCandidates[color] = scaled
        if let winner = scaled.first(where: \.isEmitterEligible) {
            selected[color] = winner.endpoints
        }
        if collectProfile {
            profile.selectionMs += (ProcessInfo.processInfo.systemUptime - selectionStart) * 1000
        }
    }
    if collectProfile {
        profile.totalMs = (ProcessInfo.processInfo.systemUptime - totalStart) * 1000
        profile.shapeAndAxisMs = candidateStageProfile?.shapeAndAxisMs ?? 0
        profile.brightnessContrastColorMs = candidateStageProfile?.brightnessContrastColorMs ?? 0
        profile.endpointAndBoundsMs = candidateStageProfile?.endpointAndBoundsMs ?? 0
        profile.componentTraversalAndProposalOverheadMs = max(
            0, profile.componentAndScoreMs + profile.lineScoreMs
                - profile.shapeAndAxisMs - profile.brightnessContrastColorMs
                - profile.endpointAndBoundsMs
        )
    }
    return SaberFrameAnalysis(candidates: allCandidates, selected: selected,
                              profile: collectProfile ? profile : nil)
}

func detectSabers(baseAddress: UnsafePointer<UInt8>, width: Int, height: Int, bytesPerRow: Int,
                  redThreshold: ColorThreshold, blueThreshold: ColorThreshold,
                  sampleStep: Int = 2) -> [SaberColor: (PixelPoint, PixelPoint)] {
    analyzeSabers(baseAddress: baseAddress, width: width, height: height, bytesPerRow: bytesPerRow,
                  redThreshold: redThreshold, blueThreshold: blueThreshold,
                  sampleStep: sampleStep).selected
}

/// Array wrapper retained for pure Swift/static tests. Production passes the
/// locked CVPixelBuffer base address directly and does not copy a full frame.
func detectSaber(in bgra: [UInt8], width: Int, height: Int, bytesPerRow: Int,
                 color: SaberColor, threshold: ColorThreshold, sampleStep: Int = 2) -> (PixelPoint, PixelPoint)? {
    guard width > 0, height > 0, bytesPerRow >= width * 4,
          bgra.count >= bytesPerRow * height else { return nil }
    return bgra.withUnsafeBufferPointer { buffer in
        guard let base = buffer.baseAddress else { return nil }
        let detections = detectSabers(baseAddress: base, width: width, height: height,
                                      bytesPerRow: bytesPerRow,
                                      redThreshold: threshold, blueThreshold: threshold,
                                      sampleStep: sampleStep)
        return detections[color]
    }
}

func analyzeSabers(in bgra: [UInt8], width: Int, height: Int, bytesPerRow: Int,
                   redThreshold: ColorThreshold, blueThreshold: ColorThreshold,
                   sampleStep: Int = 2, collectProfile: Bool = false) -> SaberFrameAnalysis {
    guard width > 0, height > 0, bytesPerRow >= width * 4,
          bgra.count >= bytesPerRow * height else {
        return SaberFrameAnalysis(candidates: [:], selected: [:])
    }
    return bgra.withUnsafeBufferPointer { buffer in
        guard let base = buffer.baseAddress else {
            return SaberFrameAnalysis(candidates: [:], selected: [:])
        }
        return analyzeSabers(baseAddress: base, width: width, height: height,
                             bytesPerRow: bytesPerRow, redThreshold: redThreshold,
                             blueThreshold: blueThreshold, sampleStep: sampleStep,
                             collectProfile: collectProfile)
    }
}
