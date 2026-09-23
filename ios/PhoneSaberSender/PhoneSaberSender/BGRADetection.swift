import Foundation

struct SaberFrameAnalysis {
    let candidates: [SaberColor: [SaberCandidate]]
    let selected: [SaberColor: (PixelPoint, PixelPoint)]
    let profile: SaberDetectionProfile?
    let pipelineDiagnostics: [SaberColor: SaberColorPipelineDiagnostics]?

    init(candidates: [SaberColor: [SaberCandidate]],
         selected: [SaberColor: (PixelPoint, PixelPoint)],
         profile: SaberDetectionProfile? = nil,
         pipelineDiagnostics: [SaberColor: SaberColorPipelineDiagnostics]? = nil) {
        self.candidates = candidates
        self.selected = selected
        self.profile = profile
        self.pipelineDiagnostics = pipelineDiagnostics
    }
}

/// Counts copied from work the detector already performs. They are populated
/// only while Debug Recording is active and never participate in detection.
struct SaberColorPipelineDiagnostics: Equatable {
    let maskPixelCount: Int
    let morphologyPixelCount: Int
    let connectedComponentCount: Int
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

private enum BlueCandidateRankingThresholds {
    static let longSpanStart = 0.32
    static let longSpanRamp = 0.10
    static let trustedContinuity = 0.80
    static let connectedTrustedContinuity = 0.85
    static let pointLineRetainedRatio = 0.75
    static let trustedHighValueRatio = 0.70
    static let trustedColorPurity = 0.55
    static let trustedCoreSupport = 0.30
    static let connectedCoreSupport = 0.55
    static let connectedHighValueRatio = 0.75
    static let connectedColorPurity = 0.35
    static let paleConnectedColorPurity = 0.13
    static let paleConnectedContrast = 0.30
    static let haloTrustedContinuity = 0.90
    static let haloTrustedRetainedRatio = 0.95
    static let haloTrustedCoreCoverage = 0.95
    static let haloTrustedCoreSupport = 0.35
    static let haloTrustedSpanFraction = 0.18
    static let weakCompetitorCoreSupport = 0.50
    static let weakCompetitorHighValueRatio = 0.80
    static let longWeakCompetitorSpan = 0.37
    static let highValueAdvantage = 0.20
    static let coreSupportAdvantage = 0.10
    static let colorPurityAdvantage = 0.05
}

private enum CoreLineProposalThresholds {
    // A Hough proposal must be supported by the target color and retain a
    // coherent axial body. Bright background slices can score highly on
    // radiance/aspect alone, but do not satisfy all three signals together.
    static let minimumColorPurity = 0.25
    static let minimumRetainedBodyRatio = 0.35
    static let minimumLongitudinalContinuity = 0.70
    static let minimumHighValueRatio = 0.35
}

func hasSufficientCoreLineProposalEvidence(_ candidate: SaberCandidate) -> Bool {
    guard candidate.source.hasPrefix("core-line") else { return true }
    return candidate.meanColorPurity >= CoreLineProposalThresholds.minimumColorPurity
        && candidate.retainedBodyRatio >= CoreLineProposalThresholds.minimumRetainedBodyRatio
        && candidate.longitudinalContinuity
            >= CoreLineProposalThresholds.minimumLongitudinalContinuity
        && candidate.highValueRatio >= CoreLineProposalThresholds.minimumHighValueRatio
}

/// A rejected line is evidence against its own geometry, not every blue
/// object in the frame. Reuse the existing core-line evidence floors to keep
/// an independently coherent candidate eligible. A pale connected core can
/// also survive when it has direct bright-core support; the ordinary
/// trusted-emitter contrast rule remains stricter for ranking.
private func hasIndependentBlueEvidence(_ candidate: SaberCandidate) -> Bool {
    if candidate.meanColorPurity >= CoreLineProposalThresholds.minimumColorPurity
        && candidate.retainedBodyRatio >= CoreLineProposalThresholds.minimumRetainedBodyRatio
        && candidate.longitudinalContinuity >= CoreLineProposalThresholds.minimumLongitudinalContinuity
        && candidate.highValueRatio >= CoreLineProposalThresholds.minimumHighValueRatio {
        return true
    }
    return candidate.source == "connected-core"
        && candidate.meanColorPurity >= BlueCandidateRankingThresholds.paleConnectedColorPurity
        && candidate.longitudinalContinuity >= BlueCandidateRankingThresholds.connectedTrustedContinuity
        && candidate.coreSupportRatio >= BlueCandidateRankingThresholds.connectedCoreSupport
        && candidate.highValueRatio >= BlueCandidateRankingThresholds.connectedHighValueRatio
}

private func candidateAxisDistance(_ lhs: SaberCandidate, _ rhs: SaberCandidate) -> Double {
    func distance(_ a: PixelPoint, _ b: PixelPoint) -> Double {
        hypot(Double(a.x - b.x), Double(a.y - b.y))
    }
    let forward = distance(lhs.comparisonEndpoints.0, rhs.comparisonEndpoints.0)
        + distance(lhs.comparisonEndpoints.1, rhs.comparisonEndpoints.1)
    let reversed = distance(lhs.comparisonEndpoints.0, rhs.comparisonEndpoints.1)
        + distance(lhs.comparisonEndpoints.1, rhs.comparisonEndpoints.0)
    return min(forward, reversed) / 2.0
}

private func candidateIsSubsegment(_ shorter: SaberCandidate, of longer: SaberCandidate) -> Bool {
    let longDX = Double(longer.comparisonEndpoints.1.x - longer.comparisonEndpoints.0.x)
    let longDY = Double(longer.comparisonEndpoints.1.y - longer.comparisonEndpoints.0.y)
    let longLength = hypot(longDX, longDY)
    let shortDX = Double(shorter.comparisonEndpoints.1.x - shorter.comparisonEndpoints.0.x)
    let shortDY = Double(shorter.comparisonEndpoints.1.y - shorter.comparisonEndpoints.0.y)
    let shortLength = hypot(shortDX, shortDY)
    guard longLength >= shortLength * 1.50, shortLength > 0 else { return false }
    let axisX = longDX / longLength, axisY = longDY / longLength
    let shortAxisX = shortDX / shortLength, shortAxisY = shortDY / shortLength
    guard abs(axisX * shortAxisX + axisY * shortAxisY) >= 0.94 else { return false }
    let normalX = -axisY, normalY = axisX
    return [shorter.comparisonEndpoints.0, shorter.comparisonEndpoints.1].allSatisfy { point in
        let dx = Double(point.x - longer.comparisonEndpoints.0.x)
        let dy = Double(point.y - longer.comparisonEndpoints.0.y)
        let along = dx * axisX + dy * axisY
        let across = abs(dx * normalX + dy * normalY)
        return along >= -6.0 && along <= longLength + 6.0 && across <= 6.0
    }
}

/// A core-line that keeps only a small, well-supported local body is usually
/// bridging unrelated emitters. This continuous penalty stays near zero for
/// legacy point LEDs whose raw and robust geometry agree.
func coreLineRobustGeometryPenalty(_ candidate: SaberCandidate, minimumArea: Int,
                                   minimumFrameDimension: Int) -> Double {
    guard candidate.source.hasPrefix("core-line"), candidate.rawPCASpan > 0 else { return 0 }
    // Short proposals can legitimately be a blade subsegment (including the
    // known long point-LED control). Reserve this ranking correction for a
    // line that spans a large fraction of the frame as well as discarding most
    // of its own geometry. This is scale-relative, not a color/pixel hack.
    let normalizedSpan = candidate.rawPCASpan / Double(max(minimumFrameDimension, 1))
    // A blade can plausibly occupy roughly a third of the short frame
    // dimension. Apply the penalty only beyond that scale, then require the
    // candidate to also discard its own span/points and lack body support.
    let longSpanWeight = min(max(
        (normalizedSpan - BlueCandidateRankingThresholds.longSpanStart)
            / BlueCandidateRankingThresholds.longSpanRamp, 0
    ), 1)
    let spanDiscard = max(0, 1 - candidate.robustMainIntervalLength / candidate.rawPCASpan)
    let pointDiscard = max(0, 1 - candidate.retainedBodyRatio)
    let bodyPointCount = Double(candidate.pointCount) * candidate.retainedBodyRatio
    let support = min(bodyPointCount / Double(max(minimumArea, 1)), 1)
    let continuity = min(max(candidate.longitudinalContinuity, 0), 1)
    let density = min(max(candidate.axialDensity / 2.0, 0), 1)
    return 55.0 * longSpanWeight * spanDiscard * pointDiscard
        * support * continuity * density
}

/// When a blue frame contains direct, coherent emitter evidence, keep weaker
/// bridge/halo/background candidates from outranking it on raw length or
/// clipped-white score alone. This is deliberately comparative: no candidate
/// is rejected when a trustworthy emitter is absent.
private func isTrustedBlueEmitter(_ candidate: SaberCandidate,
                                  minimumFrameDimension: Int) -> Bool {
    guard candidate.isEmitterEligible,
          candidate.longitudinalContinuity >= BlueCandidateRankingThresholds.trustedContinuity else { return false }
    if candidate.source == "core-line" {
        // A compact, mostly retained line with strong direct color/brightness
        // evidence is a real sparse LED blade, not a long bridge proposal.
        return candidate.retainedBodyRatio >= BlueCandidateRankingThresholds.pointLineRetainedRatio
            && candidate.highValueRatio >= BlueCandidateRankingThresholds.trustedHighValueRatio
            && candidate.meanColorPurity >= BlueCandidateRankingThresholds.trustedColorPurity
            && candidate.coreSupportRatio >= BlueCandidateRankingThresholds.trustedCoreSupport
            && candidate.largestLongitudinalGap <= 1
    }
    if candidate.source == "core-halo" {
        // A white-clipped diffuser can have modest blue purity, but a real
        // blade halo remains continuous and is supported along the full axis.
        return candidate.longitudinalContinuity
                >= BlueCandidateRankingThresholds.haloTrustedContinuity
            && candidate.retainedBodyRatio
                >= BlueCandidateRankingThresholds.haloTrustedRetainedRatio
            && candidate.longitudinalCoreCoverage
                >= BlueCandidateRankingThresholds.haloTrustedCoreCoverage
            && candidate.coreSupportRatio
                >= BlueCandidateRankingThresholds.haloTrustedCoreSupport
            && candidate.rawPCASpan / Double(max(minimumFrameDimension, 1))
                >= BlueCandidateRankingThresholds.haloTrustedSpanFraction
    }
    guard candidate.source == "color-emitter" || candidate.source == "connected-core",
          candidate.longitudinalContinuity >= BlueCandidateRankingThresholds.connectedTrustedContinuity,
          candidate.coreSupportRatio >= BlueCandidateRankingThresholds.connectedCoreSupport,
          candidate.highValueRatio >= BlueCandidateRankingThresholds.connectedHighValueRatio else { return false }
    return candidate.meanColorPurity >= BlueCandidateRankingThresholds.connectedColorPurity
        || (candidate.source == "connected-core"
            && candidate.meanColorPurity >= BlueCandidateRankingThresholds.paleConnectedColorPurity
            && candidate.localContrast >= BlueCandidateRankingThresholds.paleConnectedContrast)
}

func preferTrustedBlueEmitter(in candidates: [SaberCandidate],
                              minimumFrameDimension: Int) -> [SaberCandidate] {
    let trustedIndices = candidates.indices.filter { index in
        isTrustedBlueEmitter(candidates[index], minimumFrameDimension: minimumFrameDimension)
    }
    guard let trustedIndex = trustedIndices.max(by: {
        candidates[$0].score < candidates[$1].score
    }) else { return candidates }
    let trustedScore = candidates[trustedIndex].score
    var adjusted = candidates
    for index in adjusted.indices where index != trustedIndex
        && adjusted[index].isEmitterEligible
        && adjusted[index].coreSupportRatio < BlueCandidateRankingThresholds.weakCompetitorCoreSupport
        && adjusted[index].highValueRatio < BlueCandidateRankingThresholds.weakCompetitorHighValueRatio
        && adjusted[index].score >= trustedScore {
        let longWeakCompetitor = adjusted[index].rawPCASpan
            / Double(max(minimumFrameDimension, 1)) >= BlueCandidateRankingThresholds.longWeakCompetitorSpan
        let strongerEmitterEvidence = candidates[trustedIndex].highValueRatio
                >= adjusted[index].highValueRatio + BlueCandidateRankingThresholds.highValueAdvantage
            && candidates[trustedIndex].coreSupportRatio
                >= adjusted[index].coreSupportRatio + BlueCandidateRankingThresholds.coreSupportAdvantage
            && candidates[trustedIndex].meanColorPurity
                >= adjusted[index].meanColorPurity + BlueCandidateRankingThresholds.colorPurityAdvantage
        guard longWeakCompetitor || strongerEmitterEvidence else { continue }
        let requiredPenalty = adjusted[index].score - trustedScore + 1.0
        adjusted[index].scoreBreakdown.proposalPenalty -= requiredPenalty
        adjusted[index].score = adjusted[index].scoreBreakdown.total
    }
    return adjusted
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
                   sampleStep: Int = 2, collectProfile: Bool = false,
                   collectPipelineDiagnostics: Bool = false) -> SaberFrameAnalysis {
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
    var blueDiffuserMask = Array(repeating: UInt8(0), count: maskWidth * maskHeight)
    var redEmitterMask = Array(repeating: UInt8(0), count: maskWidth * maskHeight)
    var blueEmitterMask = Array(repeating: UInt8(0), count: maskWidth * maskHeight)
    var brightCoreMask = Array(repeating: UInt8(0), count: maskWidth * maskHeight)
    var valueMap = Array(repeating: UInt8(0), count: maskWidth * maskHeight)
    var chromaMap = Array(repeating: UInt8(0), count: maskWidth * maskHeight)
    var radianceMap = Array(repeating: UInt8(0), count: maskWidth * maskHeight)
    var redMaskPixelCount = 0
    var blueMaskPixelCount = 0
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
                redMaskPixelCount += 1
                if isEmitterPixel { redEmitterMask[index] = 1 }
            }
            if matchesSaberHSV(hsv, color: .blue, threshold: blueThreshold) {
                blueMask[index] = 1
                blueMaskPixelCount += 1
                if isEmitterPixel { blueEmitterMask[index] = 1 }
            }
            if matchesBlueDiffuserPixel(red, green, blue, threshold: blueThreshold) {
                blueDiffuserMask[index] = 1
            }
        }
    }
    let strictBlueMask = blueMask
    blueMask = supportedBlueDiffuserMask(
        strictMask: strictBlueMask, relaxedMask: blueDiffuserMask,
        width: maskWidth, height: maskHeight
    )
    blueMaskPixelCount += blueMask.indices.reduce(0) { count, index in
        count + (blueMask[index] != 0 && strictBlueMask[index] == 0 ? 1 : 0)
    }
    if collectProfile {
        profile.pixelScanHSVMaskMs = (ProcessInfo.processInfo.systemUptime - scanStart) * 1000
        profile.colorPixelCount = redMask.reduce(0) { $0 + Int($1) }
            + blueMask.reduce(0) { $0 + Int($1) }
        profile.brightCorePixelCount = brightCoreMask.reduce(0) { $0 + Int($1) }
    }
    var allCandidates: [SaberColor: [SaberCandidate]] = [:]
    var selected: [SaberColor: (PixelPoint, PixelPoint)] = [:]
    var pipelineDiagnostics: [SaberColor: SaberColorPipelineDiagnostics] = [:]
    let masks: [(SaberColor, [UInt8], [UInt8], Int)] = [
        (.red, redMask, redEmitterMask, redMaskPixelCount),
        (.blue, blueMask, blueEmitterMask, blueMaskPixelCount)
    ]
    let closeRadius = min(maskWidth, maskHeight) >= 16 ? 2 : 1
    for (color, rawMask, emitterMask, maskPixelCount) in masks {
        // Most live frames contain at most one saber color. Do not run several
        // full-mask morphology/component passes for an absent color; this is
        // an exact empty-mask fast path and does not alter candidate scoring.
        guard rawMask.contains(1) else {
            allCandidates[color] = []
            if collectPipelineDiagnostics {
                pipelineDiagnostics[color] = SaberColorPipelineDiagnostics(
                    maskPixelCount: 0, morphologyPixelCount: 0,
                    connectedComponentCount: 0
                )
            }
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
        let evidence = SaberEvidence(color: color, radiance: radianceMap,
                                     value: valueMap, chroma: chromaMap,
                                     colorMask: rawMask, coreMask: associatedCore)
        // A dotted or one-sample-wide LED blade can vanish under opening while
        // a smooth reflection survives it. Always score close-only components
        // as well, then discard duplicates of components already found in the
        // cleaned mask. Shape and emitter evidence reject isolated noise.
        let componentStart = collectProfile ? ProcessInfo.processInfo.systemUptime : 0
        var morphologyPixelCount = 0
        var connectedComponentCount = 0
        let componentObserver: (Int) -> Void = { count in
            morphologyPixelCount += count
            if collectPipelineDiagnostics { connectedComponentCount += 1 }
        }
        var candidates = saberCandidates(in: cleaned, width: maskWidth, height: maskHeight,
                                         evidence: evidence, stageProfile: candidateStageProfile,
                                         componentObserver: componentObserver)
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
        // Opening can erase a thin but elongated diffuser blade when only a
        // small number of target-color samples survive exposure or motion.
        // Only after a severe morphology loss, score the already-built raw
        // mask as a low-area fallback. Normal masks keep the established path.
        let standardMinimumArea = max(4, Int(Double(maskWidth * maskHeight) * 0.0005))
        let sparseMinimumArea = max(6, standardMinimumArea / 3)
        let morphologyLostMostPixels = morphologyPixelCount * 5 < maskPixelCount * 3
        if maskPixelCount >= sparseMinimumArea, morphologyLostMostPixels {
            let sparseCandidates = saberCandidates(
                in: rawMask, width: maskWidth, height: maskHeight,
                evidence: evidence, stageProfile: candidateStageProfile,
                minimumAreaOverride: sparseMinimumArea
            )
            for var candidate in sparseCandidates {
                candidate.source = "color-sparse-raw"
                if let duplicate = candidates.firstIndex(where: {
                    candidateAxisDistance($0, candidate) <= 3.0
                }) {
                    if candidate.isEmitterEligible && !candidates[duplicate].isEmitterEligible {
                        candidates[duplicate] = candidate
                    }
                } else {
                    candidates.append(candidate)
                }
            }
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
                let length = hypot(Double(candidate.comparisonEndpoints.1.x - candidate.comparisonEndpoints.0.x),
                                   Double(candidate.comparisonEndpoints.1.y - candidate.comparisonEndpoints.0.y))
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
                                              source: "core-line",
                                              stageProfile: candidateStageProfile),
               !candidates.contains(where: {
                candidateAxisDistance($0, candidate) <= 10.0
                    || candidateIsSubsegment(candidate, of: $0)
               }) {
                candidate.source = "core-line"
                // A Hough line can join two unrelated bright objects and then
                // win on length alone. Keep it available as a fallback for a
                // genuinely fragmented/foreshortened blade, but rank it below
                // a complete candidate unless bright core support is spread
                // along most of the proposed axis.
                candidate.scoreBreakdown.proposalPenalty -=
                    (1.0 - candidate.longitudinalCoreCoverage) * 45.0
                candidate.scoreBreakdown.proposalPenalty -= coreLineRobustGeometryPenalty(
                    candidate, minimumArea: standardMinimumArea,
                    minimumFrameDimension: min(maskWidth, maskHeight)
                )
                candidate.score = candidate.scoreBreakdown.total
                if !hasSufficientCoreLineProposalEvidence(candidate) {
                    candidate.isEmitterEligible = false
                    candidate.source = "core-line-low-confidence"
                } else if candidate.longitudinalCoreCoverage < 0.30,
                   candidate.coreSupportRatio < 0.18 {
                    candidate.isEmitterEligible = false
                    candidate.source = "core-line-sparse"
                }
                // A line proposal is a fallback for disconnected LEDs. Avoid
                // cutting a complete connected emitter into competing slices.
                let overlapsCore = candidates.contains { existing in
                    guard existing.source == "connected-core" else { return false }
                    let a = existing.comparisonEndpoints.0, b = existing.comparisonEndpoints.1
                    let dx = Double(b.x - a.x), dy = Double(b.y - a.y)
                    let length = hypot(dx, dy)
                    let cx = Double(candidate.comparisonEndpoints.0.x + candidate.comparisonEndpoints.1.x) / 2 - Double(a.x)
                    let cy = Double(candidate.comparisonEndpoints.0.y + candidate.comparisonEndpoints.1.y) / 2 - Double(a.y)
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
        if color == .blue {
            candidates = preferTrustedBlueEmitter(
                in: candidates, minimumFrameDimension: min(maskWidth, maskHeight)
            )
            // A stronger unsupported Hough slice can expose weak background
            // candidates. Reject only candidates without their own coherent
            // color/body or bright connected-core evidence. The rejected line
            // itself stays ineligible under the existing proposal quality gate.
            let rejectedLines = candidates.filter { $0.source == "core-line-low-confidence" }
            let rejectedLineScore = rejectedLines.map(\.score).max()
            let eligibleScore = candidates.filter(\.isEmitterEligible).map(\.score).max()
            let hasTrustedEmitter = candidates.contains {
                isTrustedBlueEmitter($0, minimumFrameDimension: min(maskWidth, maskHeight))
            }
            if let rejectedLineScore, let eligibleScore,
               rejectedLineScore >= eligibleScore, !hasTrustedEmitter {
                for index in candidates.indices where candidates[index].isEmitterEligible
                    && !hasIndependentBlueEvidence(candidates[index]) {
                    candidates[index].isEmitterEligible = false
                    candidates[index].source += "-unsupported-core-line-candidate"
                }
            }
        }
        let completeCandidates = candidates
        for index in candidates.indices where candidates[index].isEmitterEligible
            && candidates[index].source != "connected-core" {
            if color == .blue,
               isTrustedBlueEmitter(candidates[index],
                                    minimumFrameDimension: min(maskWidth, maskHeight)) { continue }
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
                comparisonEndpoints: (
                    PixelPoint(x: candidate.comparisonEndpoints.0.x * step,
                               y: candidate.comparisonEndpoints.0.y * step),
                    PixelPoint(x: candidate.comparisonEndpoints.1.x * step,
                               y: candidate.comparisonEndpoints.1.y * step)
                ),
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
                longitudinalCoreCoverage: candidate.longitudinalCoreCoverage,
                longitudinalContinuity: candidate.longitudinalContinuity,
                largestLongitudinalGap: candidate.largestLongitudinalGap,
                retainedBodyRatio: candidate.retainedBodyRatio,
                rawPCASpan: candidate.rawPCASpan * Double(step),
                robustMainIntervalEndpoints: candidate.robustMainIntervalEndpoints.map {
                    (PixelPoint(x: $0.0.x * step, y: $0.0.y * step),
                     PixelPoint(x: $0.1.x * step, y: $0.1.y * step))
                },
                robustMainIntervalLength: candidate.robustMainIntervalLength * Double(step),
                axialDensity: candidate.axialDensity / Double(max(step, 1)),
                componentArea: candidate.componentArea * step * step,
                pointCount: candidate.pointCount,
                usedPointLEDFallback: candidate.usedPointLEDFallback
            )
        }
        allCandidates[color] = scaled
        if collectPipelineDiagnostics {
            pipelineDiagnostics[color] = SaberColorPipelineDiagnostics(
                maskPixelCount: maskPixelCount,
                morphologyPixelCount: morphologyPixelCount,
                connectedComponentCount: connectedComponentCount
            )
        }
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
                              profile: collectProfile ? profile : nil,
                              pipelineDiagnostics: collectPipelineDiagnostics
                                  ? pipelineDiagnostics : nil)
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
                   sampleStep: Int = 2, collectProfile: Bool = false,
                   collectPipelineDiagnostics: Bool = false) -> SaberFrameAnalysis {
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
                             collectProfile: collectProfile,
                             collectPipelineDiagnostics: collectPipelineDiagnostics)
    }
}
