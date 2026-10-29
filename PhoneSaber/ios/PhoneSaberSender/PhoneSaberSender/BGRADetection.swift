import Foundation

struct SaberFrameAnalysis {
    var componentDiagnostics: [SaberColor: SaberComponentDiagnostics]? = nil
    var backgroundMedian: Int? = nil
    let candidates: [SaberColor: [SaberCandidate]]
    let selected: [SaberColor: (PixelPoint, PixelPoint)]
    let trackLockSuppressed: Set<SaberColor>
    let profile: SaberDetectionProfile?
    let pipelineDiagnostics: [SaberColor: SaberColorPipelineDiagnostics]?

    init(candidates: [SaberColor: [SaberCandidate]],
         selected: [SaberColor: (PixelPoint, PixelPoint)],
         trackLockSuppressed: Set<SaberColor> = [],
         profile: SaberDetectionProfile? = nil,
         pipelineDiagnostics: [SaberColor: SaberColorPipelineDiagnostics]? = nil) {
        self.candidates = candidates
        self.selected = selected
        self.trackLockSuppressed = trackLockSuppressed
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
    var bgraScanMs = 0.0
    var maskGenerationMs = 0.0
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
    var connectedComponentCount = 0
    var connectedComponentsMs = 0.0
    var candidateScoringMs = 0.0
    var candidateGenerationMs = 0.0
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
    candidateIsSubsegment(shorter.comparisonEndpoints, of: longer.comparisonEndpoints)
}

private func candidateIsSubsegment(_ shorter: (PixelPoint, PixelPoint), of longer: (PixelPoint, PixelPoint)) -> Bool {
    let longDX = Double(longer.1.x - longer.0.x)
    let longDY = Double(longer.1.y - longer.0.y)
    let longLength = hypot(longDX, longDY)
    let shortDX = Double(shorter.1.x - shorter.0.x)
    let shortDY = Double(shorter.1.y - shorter.0.y)
    let shortLength = hypot(shortDX, shortDY)
    guard longLength >= shortLength * 1.50, shortLength > 0 else { return false }
    let axisX = longDX / longLength, axisY = longDY / longLength
    let shortAxisX = shortDX / shortLength, shortAxisY = shortDY / shortLength
    guard abs(axisX * shortAxisX + axisY * shortAxisY) >= 0.94 else { return false }
    let normalX = -axisY, normalY = axisX
    return [shorter.0, shorter.1].allSatisfy { point in
        let dx = Double(point.x - longer.0.x)
        let dy = Double(point.y - longer.0.y)
        let along = dx * axisX + dy * axisY
        let across = abs(dx * normalX + dy * normalY)
        return along >= -6.0 && along <= longLength + 6.0 && across <= 6.0
    }
}

// eligibility/source はスナップショット作成時に絞り込み、読み取る geometry だけを所有する。
private struct CandidateBodySnapshot {
    let source: String
    let comparisonEndpoints: (PixelPoint, PixelPoint)
    let boundingBox: SaberBoundingBox
    let rawPCASpan: Double
    let meanColorPurity: Double
    let highValueRatio: Double
    let longitudinalContinuity: Double
    let axialDensity: Double

    init(_ candidate: SaberCandidate) {
        source = candidate.source
        comparisonEndpoints = candidate.comparisonEndpoints
        boundingBox = candidate.boundingBox
        rawPCASpan = candidate.rawPCASpan
        meanColorPurity = candidate.meanColorPurity
        highValueRatio = candidate.highValueRatio
        longitudinalContinuity = candidate.longitudinalContinuity
        axialDensity = candidate.axialDensity
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

// 反射対策（独立して取り外せる）: 色抽出で明るい面だけを切り出しても、
// 近飽和のピークも集中した core も無い青い面は LED の証拠にならない。
// 242 は既存の peak/mean core 判定、0.05 は既存の broad-coreless 判定と同じ。
func isCorelessBlueSurface(_ candidate: SaberCandidate) -> Bool {
    candidate.source == "color-emitter"
        && candidate.peakValue < 242
        && candidate.coreSupportRatio < 0.05
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
    var votes = Array(repeating: 0, count: diagonal * 2 + 1)
    for degrees in stride(from: 0, to: 180, by: 10) {
        let radians = Double(degrees) * .pi / 180.0
        let axisX = cos(radians), axisY = sin(radians)
        let normalX = -axisY, normalY = axisX
        votes.withUnsafeMutableBufferPointer { $0.baseAddress!.update(repeating: 0, count: $0.count) }
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
    // proposal ごとの一時配列を再利用し、点の走査順と逐次加算を保つ。
    var neighborhood: [PixelPoint] = [], inliers: [PixelPoint] = [], projections: [Double] = []
    neighborhood.reserveCapacity(points.count)
    inliers.reserveCapacity(points.count)
    projections.reserveCapacity(points.count)
    var examinedCount = 0
    for initialPeak in peaks {
        guard proposals.count < 18, examinedCount < 180 else { break }
        examinedCount += 1
        // Refit a local core cloud rather than returning the 10-degree Hough
        // bin as geometry. A cross-section through a wide glow must not become
        // an independent short blade just because it has a high mean value.
        neighborhood.removeAll(keepingCapacity: true)
        for point in points where abs(Double(point.x) * -initialPeak.axisY
            + Double(point.y) * initialPeak.axisX - Double(initialPeak.rho)) <= 6 {
            neighborhood.append(point)
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
        inliers.removeAll(keepingCapacity: true)
        for point in points where abs(Double(point.x) * normalX
            + Double(point.y) * normalY - Double(peak.rho)) <= 2.0 {
            inliers.append(point)
        }
        guard inliers.count >= minimumVotes else { continue }
        projections.removeAll(keepingCapacity: true)
        for point in inliers { projections.append(Double(point.x) * peak.axisX + Double(point.y) * peak.axisY) }
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
                // 非支持画素は整数 mask で先に除外する。投影の式は変えない。
                let index = y * width + x
                guard coreMask[index] != 0 || colorMask[index] != 0 else { continue }
                let t = Double(x) * peak.axisX + Double(y) * peak.axisY
                guard t >= minT - 1, t <= maxT + 1 else { continue }
                let distance = abs(Double(x) * normalX + Double(y) * normalY - Double(peak.rho))
                if distance <= 3.0 {
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

/// Unique-pixel bookkeeping for the dilate1 domain: a bitmap over the domain's bounding box
/// instead of a hashed Set (same membership, same visiting order, much cheaper per pixel).
struct Dilate1Visited {
    let x0: Int, y0: Int, w: Int
    var bits: [Bool]

    init(points: [PixelPoint], sampleStep: Int, width: Int, height: Int) {
        var minX = Int.max, minY = Int.max, maxX = Int.min, maxY = Int.min
        for point in points {
            let x = point.x * sampleStep, y = point.y * sampleStep
            guard x >= 0, x < width, y >= 0, y < height else { continue }
            minX = min(minX, x); maxX = max(maxX, x); minY = min(minY, y); maxY = max(maxY, y)
        }
        if minX > maxX {
            x0 = 0; y0 = 0; w = 0; bits = []
            return
        }
        x0 = max(0, minX - 1); y0 = max(0, minY - 1)
        w = min(width - 1, maxX + 1) - x0 + 1
        let h = min(height - 1, maxY + 1) - y0 + 1
        bits = Array(repeating: false, count: w * h)
    }

    /// True the first time a pixel is seen (like Set.insert(_:).inserted).
    @inline(__always) mutating func insert(x: Int, y: Int) -> Bool {
        let index = (y - y0) * w + (x - x0)
        if bits[index] { return false }
        bits[index] = true
        return true
    }
}

/// Read the study's dilate1 domain directly from the original (possibly padded) BGRA.
/// Membership via Dilate1Visited (never iterated); all decisions use integer counts.
func redWarmNoDeepRedSupport(points: [PixelPoint], baseAddress: UnsafePointer<UInt8>,
                            width: Int, height: Int, bytesPerRow: Int,
                            sampleStep: Int) -> SaberWarmNoDeepRedVerdict {
    var seen = Dilate1Visited(points: points, sampleStep: sampleStep, width: width, height: height)
    var deepCount = 0, warmCount = 0, pixelCount = 0
    for point in points {
        let x = point.x * sampleStep, y = point.y * sampleStep
        guard x >= 0, x < width, y >= 0, y < height else { continue }
        for yy in max(0, y - 1)...min(height - 1, y + 1) {
            for xx in max(0, x - 1)...min(width - 1, x + 1) {
                guard seen.insert(x: xx, y: yy) else { continue }
                let offset = yy * bytesPerRow + xx * 4
                let b = Int(baseAddress[offset]), g = Int(baseAddress[offset + 1])
                let r = Int(baseAddress[offset + 2])
                pixelCount += 1
                if r >= 180 && g * 100 < 40 * r && b * 100 < 65 * r {
                    deepCount += 1
                }
                if r >= 120 && g * 100 >= 45 * r && g * 100 <= 92 * r
                    && b * 100 <= 95 * g {
                    warmCount += 1
                }
            }
        }
    }
    return SaberWarmNoDeepRedVerdict(deepCount: deepCount, warmCount: warmCount,
                                    pixelCount: pixelCount)
}

/// 赤と同じ元画像の dilate1 領域。重複判定は Dilate1Visited(反復しない)。
func blueNoDeepSupport(points: [PixelPoint], baseAddress: UnsafePointer<UInt8>,
                       width: Int, height: Int, bytesPerRow: Int,
                       sampleStep: Int) -> SaberBlueNoDeepSupportVerdict {
    var seen = Dilate1Visited(points: points, sampleStep: sampleStep, width: width, height: height)
    var deepCount = 0, pixelCount = 0
    for point in points {
        let x = point.x * sampleStep, y = point.y * sampleStep
        guard x >= 0, x < width, y >= 0, y < height else { continue }
        for yy in max(0, y - 1)...min(height - 1, y + 1) {
            for xx in max(0, x - 1)...min(width - 1, x + 1) {
                guard seen.insert(x: xx, y: yy) else { continue }
                let offset = yy * bytesPerRow + xx * 4
                let b = Int(baseAddress[offset]), g = Int(baseAddress[offset + 1])
                let r = Int(baseAddress[offset + 2])
                pixelCount += 1
                if b >= 180 && r * 100 < 40 * b && g * 100 < 65 * b {
                    deepCount += 1
                }
            }
        }
    }
    return SaberBlueNoDeepSupportVerdict(deepCount: deepCount, pixelCount: pixelCount)
}

/// Scan BGRA once for both colors, then clean and score the two compact masks.
/// Coordinates remain in the source image's coordinate system.
/// 会場に置かれたままの赤・青の物（コーンなど）を「無視する範囲」に登録したもの。座標はフレームの 0..1。
/// 2026-10-18: 実験場所の赤いコーンが剣より選ばれるコマがあり、そのコマは本物の剣が送られなかった。
/// 選ぶ前に外せば、同じコマで次の候補（本物の剣）が選ばれる。範囲が無ければ選び方は従来と同じ。
struct SaberIgnoreZone: Equatable, Codable {
    var minX: Double, minY: Double, maxX: Double, maxY: Double
    struct ObjectEndpoints: Equatable, Codable {
        var x1: Double, y1: Double, x2: Double, y2: Double
    }
    // optional の合成 Codable により、両端のない旧保存形式もそのまま読める。
    var objectEndpoints: ObjectEndpoints?
    var objects: [ObjectEndpoints]?
    static let margin = 0.05, minimumSize = 0.06
    static let maximumObjects = 512

    /// 両端（フレームのピクセル）を囲み、少し広げた範囲。
    init(around endpoints: (PixelPoint, PixelPoint), width: Int, height: Int,
         candidates: [SaberCandidate] = []) {
        let w = Double(max(width, 1)), h = Double(max(height, 1))
        objectEndpoints = ObjectEndpoints(
            x1: min(1, max(0, Double(endpoints.0.x) / w)), y1: min(1, max(0, Double(endpoints.0.y) / h)),
            x2: min(1, max(0, Double(endpoints.1.x) / w)), y2: min(1, max(0, Double(endpoints.1.y) / h)))
        var lowX = Double(min(endpoints.0.x, endpoints.1.x)) / w - Self.margin
        var highX = Double(max(endpoints.0.x, endpoints.1.x)) / w + Self.margin
        var lowY = Double(min(endpoints.0.y, endpoints.1.y)) / h - Self.margin
        var highY = Double(max(endpoints.0.y, endpoints.1.y)) / h + Self.margin
        if highX - lowX < Self.minimumSize { let c = (lowX + highX) / 2; lowX = c - Self.minimumSize / 2; highX = c + Self.minimumSize / 2 }
        if highY - lowY < Self.minimumSize { let c = (lowY + highY) / 2; lowY = c - Self.minimumSize / 2; highY = c + Self.minimumSize / 2 }
        minX = lowX; maxX = highX; minY = lowY; maxY = highY
        // winner を先頭に残し、同じフレーム・同じ色の矩形内候補を eligibility に依らず覚える。
        var stored = [objectEndpoints!]
        for candidate in candidates {
            let ends = candidate.endpoints
            guard contains(ends.0, width: width, height: height),
                  contains(ends.1, width: width, height: height) else { continue }
            let object = ObjectEndpoints(x1: Double(ends.0.x) / w, y1: Double(ends.0.y) / h,
                                         x2: Double(ends.1.x) / w, y2: Double(ends.1.y) / h)
            let reversed = ObjectEndpoints(x1: object.x2, y1: object.y2, x2: object.x1, y2: object.y1)
            if !stored.contains(object) && !stored.contains(reversed) { stored.append(object) }
            if stored.count == Self.maximumObjects { break }
        }
        objects = stored
    }

    init(minX: Double, minY: Double, maxX: Double, maxY: Double) {
        self.minX = minX; self.minY = minY; self.maxX = maxX; self.maxY = maxY
    }

    func contains(_ point: PixelPoint, width: Int, height: Int) -> Bool {
        let x = Double(point.x) / Double(max(width, 1)), y = Double(point.y) / Double(max(height, 1))
        return x >= minX && x <= maxX && y >= minY && y <= maxY
    }

    func ignores(_ endpoints: (PixelPoint, PixelPoint), width: Int, height: Int) -> Bool {
        guard contains(endpoints.0, width: width, height: height),
              contains(endpoints.1, width: width, height: height) else { return false }
        // 配列のない旧 objectEndpoints と、両方ない旧矩形形式も維持する。
        let stored = objects.flatMap { $0.isEmpty ? nil : $0 } ?? objectEndpoints.map { [$0] }
        guard let stored else { return true }
        let w = Double(max(width, 1)), h = Double(max(height, 1))
        let x1 = Double(endpoints.0.x) / w, y1 = Double(endpoints.0.y) / h
        let x2 = Double(endpoints.1.x) / w, y2 = Double(endpoints.1.y) / h
        return stored.contains { object in
            // 蛍光灯 8090 登録時の矩形内変動は最大 0.06068（全候補、順序不問）。
            let tolerance = max(0.07, 0.25 * hypot(object.x2 - object.x1, object.y2 - object.y1))
            return (hypot(x1 - object.x1, y1 - object.y1) <= tolerance
                    && hypot(x2 - object.x2, y2 - object.y2) <= tolerance)
                || (hypot(x1 - object.x2, y1 - object.y2) <= tolerance
                    && hypot(x2 - object.x1, y2 - object.y1) <= tolerance)
        }
    }
}

/// 3秒間、選択・eligibility に関係なく全候補を収集する。画像は保持しない。
struct SaberIgnoreLearner {
    static let seconds = 3.0
    let startedAt: TimeInterval
    let colors: [SaberColor]
    private(set) var zones: [SaberColor: [SaberIgnoreZone]] = [:]

    private(set) var movingBladeRefused = false

    // 3秒の途中で動きが判明した場合も、先に集めた範囲を含めて保存しない。
    mutating func refuseMoving(_ movingColors: Set<SaberColor>) {
        guard colors.contains(where: { movingColors.contains($0) }) else { return }
        movingBladeRefused = true
        zones = [:]
    }

    mutating func add(_ analysis: SaberFrameAnalysis, width: Int, height: Int, now: TimeInterval,
                      movingColors: Set<SaberColor> = []) {
        guard now >= startedAt, now < startedAt + Self.seconds else { return }
        refuseMoving(movingColors)
        guard !movingBladeRefused else { return }
        for color in colors {
            for candidate in analysis.candidates[color] ?? [] {
                var zone = SaberIgnoreZone(around: candidate.endpoints, width: width, height: height)
                var clusters = zones[color] ?? []
                // 既存marginを付けた矩形が重なる候補は同じ群。結合で近付いた群も再確認する。
                var index = 0
                while index < clusters.count {
                    let other = clusters[index]
                    guard zone.minX <= other.maxX, other.minX <= zone.maxX,
                          zone.minY <= other.maxY, other.minY <= zone.maxY else { index += 1; continue }
                    zone.minX = min(zone.minX, other.minX); zone.maxX = max(zone.maxX, other.maxX)
                    zone.minY = min(zone.minY, other.minY); zone.maxY = max(zone.maxY, other.maxY)
                    var objects = other.objects ?? []
                    for object in zone.objects ?? [] {
                        let reversed = SaberIgnoreZone.ObjectEndpoints(x1: object.x2, y1: object.y2, x2: object.x1, y2: object.y1)
                        if !objects.contains(object), !objects.contains(reversed) { objects.append(object) }
                    }
                    // 通常の3秒分を収めつつ、上限時も新しい位置を覚える。矩形は全候補の和を維持する。
                    zone.objects = Array(objects.suffix(SaberIgnoreZone.maximumObjects))
                    zone.objectEndpoints = zone.objects?.first
                    clusters.remove(at: index)
                    index = 0
                }
                clusters.append(zone)
                zones[color] = clusters
            }
        }
    }
}

/// 矩形内かつ登録物体と両端が近い候補だけ無視。両端のない旧範囲は矩形だけで判定する。
func saberIgnored(_ endpoints: (PixelPoint, PixelPoint), width: Int, height: Int, zones: [SaberIgnoreZone]) -> Bool {
    zones.contains { $0.ignores(endpoints, width: width, height: height) }
}

/// プレイ範囲（剣を振る場所。色に依らず1つ）。座標は SaberIgnoreZone と同じフレームの 0..1。
/// 2026-10-18: 赤い服の人など動く赤・青の物は無視範囲では外せない。範囲を設定すると、両端の中点（midpoint）が
/// 範囲の外にある候補は選ばず、同じコマの次の eligible 候補を選ぶ（無視範囲と同じ場所。候補の一覧・順位・
/// score・eligible は変えない）。中点で判定するのは、剣の先が範囲から少しはみ出す振りを捨てないため。
/// 境界上は範囲内。範囲が無ければ選び方は従来と同じ。Android の phonesaber::PlayArea と同じ意味・同じ数値。
struct SaberPlayArea: Equatable, Codable {
    var minX: Double, minY: Double, maxX: Double, maxY: Double
    /// 覚えた範囲 = 選ばれた両端の min/max + 各辺 margin、各辺は最小 minimumSize、フレームの 0..1 に収める。
    static let margin = 0.08, minimumSize = 0.3
    /// これより少ない winner しか見えなかった学習は使わない（前の範囲を残す）。
    static let minimumSamples = 30

    init(minX: Double, minY: Double, maxX: Double, maxY: Double) {
        self.minX = minX; self.minY = minY; self.maxX = maxX; self.maxY = maxY
    }

    /// 0..1 の箱（両端の min/max）に margin・minimumSize・0..1 への収めを適用した範囲。
    static func around(minX: Double, minY: Double, maxX: Double, maxY: Double) -> SaberPlayArea {
        func fit(_ lowValue: Double, _ highValue: Double) -> (Double, Double) {
            var low = lowValue - margin, high = highValue + margin
            if high - low < minimumSize { let c = (low + high) / 2; low = c - minimumSize / 2; high = c + minimumSize / 2 }
            if low < 0 { high -= low; low = 0 }
            if high > 1 { low -= high - 1; high = 1 }
            return (max(low, 0), min(high, 1))
        }
        let x = fit(minX, maxX), y = fit(minY, maxY)
        return SaberPlayArea(minX: x.0, minY: y.0, maxX: x.1, maxY: y.1)
    }

    var isValid: Bool {
        [minX, minY, maxX, maxY].allSatisfy(\.isFinite) && minX < maxX && minY < maxY
    }

    func containsMidpoint(_ endpoints: (PixelPoint, PixelPoint), width: Int, height: Int) -> Bool {
        let x = (Double(endpoints.0.x) + Double(endpoints.1.x)) / 2 / Double(max(width, 1))
        let y = (Double(endpoints.0.y) + Double(endpoints.1.y)) / 2 / Double(max(height, 1))
        return x >= minX && x <= maxX && y >= minY && y <= maxY
    }
}

/// プレイ範囲の学習: 期間中に選ばれた winner（両色）の両端の min/max を数える（フレームの 0..1）。
struct SaberPlayAreaLearner: Equatable {
    private(set) var samples = 0
    private(set) var minX = 1.0, minY = 1.0, maxX = 0.0, maxY = 0.0

    mutating func add(_ endpoints: (PixelPoint, PixelPoint), width: Int, height: Int) {
        let w = Double(max(width, 1)), h = Double(max(height, 1))
        for point in [endpoints.0, endpoints.1] {
            let x = Double(point.x) / w, y = Double(point.y) / h
            minX = min(minX, x); maxX = max(maxX, x); minY = min(minY, y); maxY = max(maxY, y)
        }
        samples += 1
    }

    /// samples が minimumSamples 未満なら nil。
    func area(minimumSamples: Int = SaberPlayArea.minimumSamples) -> SaberPlayArea? {
        guard samples >= minimumSamples, samples > 0 else { return nil }
        return SaberPlayArea.around(minX: minX, minY: minY, maxX: maxX, maxY: maxY)
    }
}

/// FrameProcessor がフレームキュー上で所有する色別の選択履歴。補間・プレビューの Track とは別の寿命。
final class SaberTrackLock {
    private struct Track {
        var x: Double, y: Double, length: Double, angle: Double, updated: Double
        var selectedSince: Double?
        var preferenceSince: Double?
        var challenger: Challenge?
        var vx = 0.0, vy = 0.0, misses = 0
    }
    private struct Challenge {
        var x: Double, y: Double, length: Double, angle: Double
        var frames: Int
    }
    private var tracks: [SaberColor: Track] = [:]
    private struct EndOnSample {
        var endpoints: (PixelPoint, PixelPoint)
        var length: Double, time: Double
    }
    private struct EndOn {
        var history: [EndOnSample] = []
        var last: EndOnSample?
        var previous: EndOnSample?
        var deadline: Double?
    }
    private var endOn: [SaberColor: EndOn] = [:]
    // 10/29の短縮区間。短辺480px換算。期限は救済や短いeligibleの瞬間復帰では延長しない。
    static let endOnSeconds = 0.5
    static let endOnRecentSeconds = 0.10

    private static func endOnAnchors(_ endpoints: (PixelPoint, PixelPoint)) -> [PixelPoint] {
        [endpoints.0, endpoints.1,
         PixelPoint(x: (endpoints.0.x + endpoints.1.x) / 2, y: (endpoints.0.y + endpoints.1.y) / 2)]
    }

    /// 形状だけの不適格を救う入口。色支持のsafety gate・無視範囲は呼出側で別途必須。
    static func isEndOnCandidate(_ candidate: SaberCandidate, scale: Double) -> Bool {
        let e = candidate.endpoints
        let length = hypot(Double(e.1.x - e.0.x), Double(e.1.y - e.0.y))
        return !candidate.isEmitterEligible
            && ["color-mask", "color-close", "color-sparse-raw"].contains(candidate.source)
            && length >= 10 * scale && length <= 20 * scale
            && Double(max(candidate.boundingBox.maxX - candidate.boundingBox.minX,
                          candidate.boundingBox.maxY - candidate.boundingBox.minY)) <= 24 * scale
            && candidate.peakValue >= 210 && candidate.meanColorPurity >= 0.65
            && candidate.warmNoDeepRed?.rejected != true && candidate.blueNoDeepSupport?.rejected != true
    }

    private func rememberEndOn(_ winner: SaberCandidate, color: SaberColor, scale: Double, now: Double,
                               rescued: Bool) {
        let e = winner.endpoints
        let sample = EndOnSample(endpoints: e,
            length: hypot(Double(e.1.x - e.0.x), Double(e.1.y - e.0.y)), time: now)
        var state = endOn[color] ?? EndOn()
        if let last = state.last, now < last.time || now - last.time > Self.endOnRecentSeconds {
            state = EndOn()
        }
        if let last = state.last {
            let x = Double(e.0.x + e.1.x) / 2, y = Double(e.0.y + e.1.y) / 2
            // 通常選択が別の物体へ移った時に、その長さの差を短縮履歴として貸さない。
            if Self.endOnAnchors(last.endpoints).allSatisfy({
                hypot(x - Double($0.x), y - Double($0.y)) > 48 * scale
            }) { state = EndOn() }
        }
        if !rescued {
            // 長い刃へ戻るまで同じ短縮episode。短いeligibleの点滅で期限を更新しない。
            if state.deadline != nil && sample.length >= 32 * scale { state = EndOn() }
            state.history = state.history.filter { now >= $0.time && now - $0.time <= 0.15 }
            state.history.append(sample)
            let lengths = state.history.map { $0.length }
            let decreases = zip(lengths, lengths.dropFirst()).filter { $0 - $1 >= 2 * scale }.count
            if state.deadline == nil, lengths.count >= 3,
               let longest = lengths.max(), longest >= 32 * scale,
               sample.length <= 30 * scale, sample.length <= longest * 0.7, decreases >= 2 {
                state.deadline = now + Self.endOnSeconds
            }
        }
        state.previous = state.last
        state.last = sample
        endOn[color] = state
    }

    private func endOnWinner(_ candidates: [SaberCandidate], color: SaberColor,
                             scale: Double, now: Double) -> SaberCandidate? {
        guard let state = endOn[color], let deadline = state.deadline, now < deadline,
              let last = state.last, now >= last.time,
              now - last.time <= Self.endOnRecentSeconds else { return nil }
        let a = last.endpoints
        let anchors = Self.endOnAnchors(a)
        var closest = 20 * scale
        var winner: SaberCandidate?
        for candidate in candidates where Self.isEndOnCandidate(candidate, scale: scale) {
            let e = candidate.endpoints
            let x = Double(e.0.x + e.1.x) / 2, y = Double(e.0.y + e.1.y) / 2
            // 既に降格した静止成分には救済でも戻らない。
            if (stationary[color] ?? []).contains(where: {
                $0.excluded && hypot(x - $0.x, y - $0.y) <= max(16 * scale, $0.length / 2)
            }) { continue }
            let measuredDistance = anchors.map { hypot(x - Double($0.x), y - Double($0.y)) }.min()!
            var distance = measuredDistance
            // n0を挟んだ2〜3コマだけ直前の実測移動を併用。予測座標そのものは出力しない。
            if now - last.time >= 0.025, let previous = state.previous, last.time > previous.time {
                let b = previous.endpoints
                let factor = min(now - last.time, 0.05) / (last.time - previous.time)
                let dx = Double(a.0.x + a.1.x - b.0.x - b.1.x) / 2 * factor
                let dy = Double(a.0.y + a.1.y - b.0.y - b.1.y) / 2 * factor
                distance = min(distance, anchors.map { hypot(x - Double($0.x) - dx, y - Double($0.y) - dy) }.min()!)
            }
            if measuredDistance <= 48 * scale && distance <= 20 * scale
                && (winner == nil || distance < closest) { closest = distance; winner = candidate }
        }
        return winner
    }
    // 移動するeligibleの履歴と、同じ場所に見え続ける成分の履歴を分ける。
    // eligibilityが途切れたり遠いproposalが現れても、静止時間を奪われない。
    private struct Motion {
        var x: Double, y: Double, length: Double, angle: Double, updated: Double
        var originX: Double, originY: Double, originTime: Double
        var movedAt: Double? = nil
        var eligible = false
    }
    private struct Still {
        var x: Double, y: Double, length: Double, since: Double, updated: Double
        var box: SaberBoundingBox
        var sawMovingAlternative = false
        var excluded = false
    }
    // 10/28の13clipと過去69clipのsweep: 1.5s/8px/24pxは過去の選択・欠落の変更0。
    // 0.5〜1sの短縮や12px以上への拡大では過去出力が変わるため採用しない。
    static let stationarySeconds = 1.5
    static let stationaryRadius = 8.0 // 480px短辺でのpx。解像度に比例させる。
    static let movingDistance = 24.0
    private var motions: [SaberColor: [Motion]] = [:]
    private var stationary: [SaberColor: [Still]] = [:]
    private var dimensions: (Int, Int)?
    private(set) var suppressed: Set<SaberColor> = []

    func reset() { tracks = [:]; endOn = [:]; motions = [:]; stationary = [:]; dimensions = nil; suppressed = [] }

    /// 無視登録専用のインスタンスで既存の動き記録を再利用する。認識用の状態には触れない。
    func observeIgnoreMotion(_ analysis: SaberFrameAnalysis, width: Int, height: Int, now: Double) -> Set<SaberColor> {
        if dimensions.map({ $0 != (width, height) }) ?? true {
            reset()
            dimensions = (width, height)
        }
        for color in [SaberColor.red, .blue] {
            let candidates = analysis.candidates[color] ?? []
            _ = movingAlternatives(candidates.filter { $0.isEmitterEligible }, observed: candidates, color: color,
                                   width: width, height: height, now: now)
        }
        return recentlyMovingColors(now: now)
    }

    func recentlyMovingColors(now: Double) -> Set<SaberColor> {
        Set(motions.compactMap { color, history in
            history.contains { motion in
                now >= motion.updated
                    && motion.movedAt.map { now >= $0 && now - $0 <= Self.stationarySeconds } == true
            } ? color : nil
        })
    }

    /// 単独の静止刃は維持する。離れたeligibleの実測移動がある時だけ静止物を降格する。
    /// 降格後にその物体しか残らない時は送らず、消失・移動・resetで忘れる。
    private func movingAlternatives(_ eligible: [SaberCandidate], observed: [SaberCandidate], color: SaberColor,
                                    width: Int, height: Int, now: Double,
                                    componentDiagnostics: SaberComponentDiagnostics? = nil) -> [SaberCandidate] {
        let scale = Double(min(width, height)) / 480
        let tolerance = Self.stationaryRadius * scale
        let prior = (motions[color] ?? []).filter { now >= $0.updated && now - $0.updated <= 0.25 }
        var used: Set<Int> = [], moving: [Motion] = []
        // eligibilityが揺れた間の移動も観測する。移動証拠として使うのは現在eligibleの物体だけ。
        let observations = eligible + observed.filter { candidate in
            !eligible.contains { $0.endpoints.0 == candidate.endpoints.0 && $0.endpoints.1 == candidate.endpoints.1 }
        }
        for candidate in observations {
            let e = candidate.endpoints
            let allowed = eligible.contains { $0.endpoints.0 == e.0 && $0.endpoints.1 == e.1 }
            let x = Double(e.0.x + e.1.x) / 2, y = Double(e.0.y + e.1.y) / 2
            if moving.contains(where: { hypot(x - $0.x, y - $0.y) <= tolerance }) { continue }
            let length = max(hypot(Double(e.1.x - e.0.x), Double(e.1.y - e.0.y)), 1)
            let angle = atan2(Double(e.1.y - e.0.y), Double(e.1.x - e.0.x))
            var match: Int?, closest = Double.infinity
            for i in prior.indices where !used.contains(i) {
                let p = prior[i], distance = hypot(x - prior[i].x, y - prior[i].y)
                let raw = abs(angle - p.angle).truncatingRemainder(dividingBy: .pi)
                let cost = 0.80 * distance / p.length + 0.18 * min(raw, .pi - raw) / (.pi / 2)
                    + 0.02 * abs(log2(length / p.length))
                if cost <= 0.5 && distance < closest { closest = distance; match = i }
            }
            var m = Motion(x: x, y: y, length: length, angle: angle, updated: now,
                           originX: x, originY: y, originTime: now)
            if let match {
                used.insert(match); m = prior[match]
                if hypot(x - m.originX, y - m.originY) >= Self.movingDistance * scale {
                    m.movedAt = now
                    m.originX = x; m.originY = y; m.originTime = now
                } else if now - m.originTime > Self.stationarySeconds {
                    m.originX = x; m.originY = y; m.originTime = now
                }
                m.x = x; m.y = y; m.length = length; m.angle = angle; m.updated = now
            }
            m.eligible = allowed
            moving.append(m)
        }
        motions[color] = moving
        var still = (stationary[color] ?? []).filter { now >= $0.updated && now - $0.updated <= 0.25 }
        func sameObject(_ box: SaberBoundingBox, x: Double, y: Double, length: Double, _ s: Still) -> Bool {
            let distance = hypot(x - s.x, y - s.y)
            if distance <= tolerance { return true }
            // 固定した成分の全体/部分bodyが交替しても履歴を引き継ぐ。
            // 同寸の刃が並進するだけでは包含にならない。anchorの箱は広げない。
            func contains(_ a: SaberBoundingBox, _ b: SaberBoundingBox) -> Bool {
                a.minX <= b.minX && a.minY <= b.minY && a.maxX >= b.maxX && a.maxY >= b.maxY
            }
            let area = Double(max(1, box.maxX - box.minX + 1) * max(1, box.maxY - box.minY + 1))
            let oldArea = Double(max(1, s.box.maxX - s.box.minX + 1) * max(1, s.box.maxY - s.box.minY + 1))
            return distance <= (length + s.length) / 4 && max(area, oldArea) <= 4 * min(area, oldArea)
                && (contains(box, s.box) || contains(s.box, box))
        }
        for candidate in observed {
            // 部分bodyへの端点切替を移動と誤認しないよう、成分bboxの中点を測る。
            let box = candidate.boundingBox
            let x = Double(box.minX + box.maxX) / 2, y = Double(box.minY + box.maxY) / 2
            let e = candidate.endpoints
            let length = hypot(Double(e.1.x - e.0.x), Double(e.1.y - e.0.y))
            if let i = still.indices.filter({ sameObject(box, x: x, y: y, length: length, still[$0]) }).min(by: {
                hypot(x - still[$0].x, y - still[$0].y) < hypot(x - still[$1].x, y - still[$1].y)
            }) {
                still[i].updated = now
            } else {
                still.append(Still(x: x, y: y, length: length, since: now, updated: now, box: box))
            }
        }
        for i in still.indices where still[i].updated == now {
            let a = still[i]
            if moving.contains(where: { b in
                b.eligible && b.movedAt.map { now - $0 <= 0.75 } == true
                    && hypot(a.x - b.x, a.y - b.y) > max(3 * tolerance, (a.length + b.length) / 2)
            }) { still[i].sawMovingAlternative = true }
            if now - a.since >= Self.stationarySeconds && still[i].sawMovingAlternative {
                still[i].excluded = true
            }
        }
        stationary[color] = still
        func excluded(_ candidate: SaberCandidate) -> Bool {
            let box = candidate.boundingBox
            let x = Double(box.minX + box.maxX) / 2, y = Double(box.minY + box.maxY) / 2
            let e = candidate.endpoints
            let length = hypot(Double(e.1.x - e.0.x), Double(e.1.y - e.0.y))
            return still.contains { $0.excluded && $0.updated == now && sameObject(box, x: x, y: y, length: length, $0) }
        }

        let rejected = eligible.filter(excluded)
        if let componentDiagnostics {
            for candidate in rejected { componentDiagnostics.candidate(candidate, stage: "postGeneration", reason: "stationary") }
        }
        // 固定物への継続・復帰を切り、同frameで遠い実刃を取得できるようにする。
        if let track = tracks[color], rejected.contains(where: {
            hypot(Double($0.endpoints.0.x + $0.endpoints.1.x) / 2 - track.x,
                  Double($0.endpoints.0.y + $0.endpoints.1.y) / 2 - track.y) <= max(2 * tolerance, track.length / 2)
        }) { tracks[color] = nil }
        return eligible.filter { !excluded($0) }
    }

    /// 選択が途切れず0.5秒続いた色の当該フレームのwinnerだけで、既存範囲を広げる。
    /// FrameProcessor の作業用範囲に適用する。未設定なら新しく作らない。
    func grownPlayArea(_ area: SaberPlayArea?, selected: [SaberColor: (PixelPoint, PixelPoint)],
                       width: Int, height: Int, now: Double) -> SaberPlayArea? {
        guard var area else { return nil }
        for color in [SaberColor.red, .blue] {
            guard let track = tracks[color], track.updated == now,
                  let since = track.selectedSince, now - since >= 0.5,
                  let endpoints = selected[color] else { continue }
            let w = Double(max(width, 1)), h = Double(max(height, 1))
            let bounds = SaberPlayArea.around(
                minX: Double(min(endpoints.0.x, endpoints.1.x)) / w,
                minY: Double(min(endpoints.0.y, endpoints.1.y)) / h,
                maxX: Double(max(endpoints.0.x, endpoints.1.x)) / w,
                maxY: Double(max(endpoints.0.y, endpoints.1.y)) / h)
            area.minX = max(0, min(area.minX, bounds.minX)); area.minY = max(0, min(area.minY, bounds.minY))
            area.maxX = min(1, max(area.maxX, bounds.maxX)); area.maxY = min(1, max(area.maxY, bounds.maxY))
        }
        return area
    }

    /// 現在のゲート内だけで対応を取る純粋関数。index/sourceや発光特徴をidentityにしない。
    /// コスト = 0.80×予測中点距離/前回長 + 0.18×軸角差/90度 + 0.02×|log2(長さ比)|。
    /// 端点入替に不変。中点を主にし、短い/部分的な刃も長さだけで別物にしない。
    /// 10/21 17:02の反射challengerは対応する実刃より最大21.2点高い。margin sweepの
    /// 5/10/15/20点では反射が残り、25〜30点で消えたため25点とする（2026-10-22の全34clip評価）。
    /// コスト0.5以下だけを対応とみなす。それを失えば現frameの順位へ即座に戻る。
    static func continuingWinner(_ eligible: [SaberCandidate], predictedX: Double, predictedY: Double,
                                 length: Double, angle: Double, radius: Double,
                                 allowPreference: Bool = true) -> SaberCandidate? {
        var ranked: SaberCandidate?, matching: SaberCandidate?
        var bestCost = Double.infinity
        for candidate in eligible {
            let e = candidate.endpoints
            let distance = hypot((Double(e.0.x) + Double(e.1.x)) / 2 - predictedX,
                                 (Double(e.0.y) + Double(e.1.y)) / 2 - predictedY)
            guard distance <= radius else { continue }
            if ranked == nil { ranked = candidate }
            guard allowPreference else { continue }
            let dx = Double(e.1.x - e.0.x), dy = Double(e.1.y - e.0.y)
            let candidateLength = max(hypot(dx, dy), 1), previousLength = max(length, 1)
            let rawAngle = abs(atan2(dy, dx) - angle).truncatingRemainder(dividingBy: .pi)
            let angleDifference = min(rawAngle, .pi - rawAngle) / (.pi / 2)
            let cost = 0.80 * distance / previousLength + 0.18 * angleDifference
                + 0.02 * abs(log2(candidateLength / previousLength))
            // 同値なら既存順位を保つ。Set/Dictionaryの反復順に依存させない。
            if cost < bestCost { bestCost = cost; matching = candidate }
        }
        guard let ranked, let matching, bestCost <= 0.5,
              ranked.score - matching.score <= 25 else { return ranked }
        return matching
    }

    /// 得点または既存の高輝度支持が連続して強い別候補だけに復帰する。履歴は色別・幾何対応。
    /// 17:02の反射取得20→21は得点差3.8が1コマだけ。19〜25の実刃は高輝度比0.987以上、
    /// 反射との差0.082以上が続く。高輝度比0.97・差0.06、または得点差8（輝度比の低下0.04以内）
    /// が3コマ続けば復帰する。軸に垂直な距離が長さの0.20以上の候補だけを対象とし、
    /// 同じ刃のsource/端点の違いを復帰にしない。34clipの窓長・得点差sweepで確認した。
    private static func recoveringWinner(_ eligible: [SaberCandidate], winner: SaberCandidate,
                                         predictedX: Double, predictedY: Double, radius: Double,
                                         track: inout Track) -> SaberCandidate {
        let e = winner.endpoints
        let wx = Double(e.0.x + e.1.x) / 2, wy = Double(e.0.y + e.1.y) / 2
        let length = max(hypot(Double(e.1.x - e.0.x), Double(e.1.y - e.0.y)), 1)
        // 順位は変更せず、同じ刃の端点/sourceの違いだけでは復帰履歴を作らない。
        let challengers = eligible.filter { candidate in
            let c = candidate.endpoints
            let x = Double(c.0.x + c.1.x) / 2, y = Double(c.0.y + c.1.y) / 2
            return hypot(x - predictedX, y - predictedY) <= radius
                && abs((x - wx) * Double(e.1.y - e.0.y)
                    - (y - wy) * Double(e.1.x - e.0.x)) / (length * length) >= 0.20
                && candidate.score >= winner.score - 25
                && ((candidate.score >= winner.score + 8
                        && candidate.highValueRatio >= winner.highValueRatio - 0.04)
                    || (candidate.highValueRatio >= 0.97
                        && candidate.highValueRatio >= winner.highValueRatio + 0.06))
        }
        let challenger: SaberCandidate?
        if let prior = track.challenger {
            challenger = continuingWinner(challengers, predictedX: prior.x, predictedY: prior.y,
                length: prior.length, angle: prior.angle, radius: radius)
        } else { challenger = challengers.first }
        guard let challenger else { track.challenger = nil; return winner }
        let c = challenger.endpoints
        let x = Double(c.0.x + c.1.x) / 2, y = Double(c.0.y + c.1.y) / 2
        let angle = atan2(Double(c.1.y - c.0.y), Double(c.1.x - c.0.x))
        let candidateLength = max(hypot(Double(c.1.x - c.0.x), Double(c.1.y - c.0.y)), 1)
        var count = 1
        if let prior = track.challenger {
            let raw = abs(angle - prior.angle).truncatingRemainder(dividingBy: .pi)
            let cost = 0.80 * hypot(x - prior.x, y - prior.y) / max(prior.length, 1)
                + 0.18 * min(raw, .pi - raw) / (.pi / 2)
                + 0.02 * abs(log2(candidateLength / max(prior.length, 1)))
            if cost <= 0.5 { count = prior.frames + 1 }
        }
        track.challenger = Challenge(x: x, y: y, length: candidateLength, angle: angle, frames: count)
        guard count >= 3 else { return winner }
        track.challenger = nil
        track.preferenceSince = nil
        return challenger
    }

    /// 継続・復帰ルールの入口。select内のこの呼出し1か所で適用を切り離せる。
    /// 候補生成・得点・順位は変えず、現在のゲート内のeligibleだけから選ぶ。
    private static func continuityWinner(_ eligible: [SaberCandidate], predictedX x: Double,
                                         predictedY y: Double, radius: Double, now: Double,
                                         track t: inout Track) -> SaberCandidate? {
        var winner = Self.continuingWinner(eligible, predictedX: x, predictedY: y,
            length: t.length, angle: t.angle, radius: radius,
            allowPreference: t.preferenceSince.map { now - $0 < 0.25 } ?? true)
        if let current = winner {
            winner = Self.recoveringWinner(eligible, winner: current,
                predictedX: x, predictedY: y, radius: radius, track: &t)
        }
        if let winner {
            let ranked = eligible.first {
                let e = $0.endpoints
                return hypot((Double(e.0.x) + Double(e.1.x)) / 2 - x,
                             (Double(e.0.y) + Double(e.1.y)) / 2 - y) <= radius
            }
            // 連続した順位overrideは既存の履歴期限と同じ0.25秒まで。overrideで期限を延ばさない。
            t.preferenceSince = ranked.map {
                $0.endpoints.0 == winner.endpoints.0 && $0.endpoints.1 == winner.endpoints.1
            } == true ? nil : (t.preferenceSince ?? now)
        }
        return winner
    }

    func select(_ eligible: [SaberCandidate], color: SaberColor,
                width: Int, height: Int, now: Double, playArea: SaberPlayArea? = nil,
                observedCandidates: [SaberCandidate]? = nil,
                continuityCandidates: [SaberCandidate] = [],
                componentDiagnostics: SaberComponentDiagnostics? = nil) -> SaberCandidate? {
        if dimensions.map({ $0 != (width, height) }) ?? true {
            reset(); dimensions = (width, height)
        }
        let eligible = movingAlternatives(eligible, observed: observedCandidates ?? eligible,
                                          color: color, width: width, height: height, now: now, componentDiagnostics: componentDiagnostics)
        suppressed.remove(color)
        var track = tracks[color]
        if let t = track, now - t.updated < 0 || now - t.updated > 0.25 { track = nil }
        // 診断は実際に取得制約が有効なときだけ記録する。
        if track == nil, let componentDiagnostics, let playArea {
            for c in eligible where !playArea.containsMidpoint(c.endpoints, width: width, height: height) {
                componentDiagnostics.candidate(c, stage: "postGeneration", reason: "playArea")
            }
        }
        // プレイ範囲は取得時だけに適用し、追跡中は範囲外でもゲート内の候補を選ぶ。
        var winner = track == nil ? eligible.first {
            playArea?.containsMidpoint($0.endpoints, width: width, height: height) ?? true
        } : nil
        if var t = track {
            let dt = now - t.updated
            let x = t.x + t.vx * dt, y = t.y + t.vy * dt
            let radius = max(1.5 * t.length, 0.12 * hypot(Double(width), Double(height)))
            winner = Self.continuityWinner(eligible, predictedX: x, predictedY: y,
                radius: radius, now: now, track: &t)
            track = t
        }
        let scale = Double(min(width, height)) / 480
        // eligibleがあれば通常の選択・ゲートへ戻す。救済は現frameの不適格候補だけ。
        let rescued = winner == nil && eligible.isEmpty
            ? endOnWinner(continuityCandidates, color: color, scale: scale, now: now) : nil
        if let rescued { rememberEndOn(rescued, color: color, scale: scale, now: now, rescued: true) }
        if winner == nil, var t = track {
            // 3コマ目で通常履歴を捨てる。短縮履歴にも実測候補がなければ送らない。
            t.challenger = nil
            t.misses += 1
            t.selectedSince = nil
            tracks[color] = t.misses >= 3 ? nil : t
            if rescued == nil { suppressed.insert(color) }
            return rescued
        }
        if let winner {
            rememberEndOn(winner, color: color, scale: scale, now: now, rescued: false)
            let e = winner.endpoints
            let x = (Double(e.0.x) + Double(e.1.x)) / 2, y = (Double(e.0.y) + Double(e.1.y)) / 2
            var next = Track(x: x, y: y, length: hypot(Double(e.0.x - e.1.x), Double(e.0.y - e.1.y)),
                             angle: atan2(Double(e.1.y - e.0.y), Double(e.1.x - e.0.x)), updated: now)
            next.selectedSince = track?.selectedSince ?? now
            next.preferenceSince = track?.preferenceSince
            next.challenger = track?.challenger
            if let t = track, now > t.updated {
                // 直前2回の実測中点の差分。未選択・予測では更新しない。
                next.vx = (x - t.x) / (now - t.updated); next.vy = (y - t.y) / (now - t.updated)
            }
            track = next
        }
        tracks[color] = track
        if winner == nil && rescued == nil { suppressed.insert(color) } // ON の未選択では過去座標の補間を送らない。
        return winner ?? rescued
    }
}

// 低ISO専用の明示opt-in。自動露出・ISO2000では追加走査も候補も作らない。
struct ShutterEmitterMode: Equatable {
    var shutter = 0
    var iso = 2000
    var enabled = false
    var active: Bool { enabled && [500, 1000, 2000].contains(shutter) && iso > 0 && iso < 2000 }
}

// 合成境界テスト（StaticBGRADetectionTests）の仮設定。実機クリップで再調整する場所を集約。
// 白飛び芯を要求しない代わりに、暗背景・狭い色相・高純度・背景比・細長さ・小面積を全て要求する。
enum ShutterEmitterThresholds {
    static let maximumBackground = 48
    static let minimumValue = 64
    static let maximumValue = 234
    static let minimumBackgroundRatio = 3
    static let minimumPurity = 0.65
    // 10/28 明るい部屋（ISO自動で背景42〜48）: 刃の光で照らされた服・カーテンは純度0.8〜0.99でも
    // 候補の最大値が135〜202、芯なし。同じ時の本物の刃は229〜234。暗い会場（背景0〜6）の刃は77〜208。
    // 背景に応じた比（24以下は3、40以上は5、上限220）を候補の最大値の下限にすると、照り返しは落ち、どちらの刃も残る。
    static let minimumPeakBackgroundRatio = 5.0
    static let maximumPeakFloor = 220.0
    static let redHueEdge = 15.0
    // 10/28 夕方の暗い部屋（背景3〜6、ISO250）: 赤刃は芯の外側が色相330〜349度（桃色寄り）に写り、
    // ±15度では遅い振りやカメラ側へ傾いた刃（最大値170〜210）が候補にならなかった。
    // 桃色側を広げただけのマスクは周りのにじみで刃が太くなり、今取れている短い刃を落とすので、
    // 通常の赤マスクはそのままにし、桃色まで含めたマスクの候補は重ならない場所にだけ足す。
    // 青（200〜250度）とは重ならない。
    static let redMagentaHueStart = 330.0
    // 桃色マスクだけで見つかった候補の下限。10/24 の近い撮影で赤い光に照らされた顔・髪
    // （最大67〜91、純度0.69〜0.71、周囲との差0.03〜0.05）は落とし、暗い部屋の刃
    // （最大140〜232、純度0.85〜0.99、周囲との差0.30〜1.0）は残す。
    static let redMagentaMinimumPeak = 120
    static let redMagentaMinimumPurity = 0.80
    static let redMagentaMinimumContrast = 0.20
    // HSVのbinary64計算で250度が1ULP上になる境界だけ含める。
    static let blueHueRange = 200.0...250.0.nextUp
    static let minimumPixels = 6
    static let maximumAreaRatio = 0.03
    static let minimumAspect = 4.0
    // 10/27 実機: 赤刃がカメラ側へ傾くと短く写り、PCA長/平均幅が 2.5〜3.9 で全フレームの約2割が消えた。
    // 背景の固定した橙の点は 19〜29 画素だったので、赤で 32 画素以上のときだけ 2.4 まで認める
    // （録画の再計算: 赤が取れるフレーム 10/24 77%→95%、10/27 79%→99%）。
    static let relaxedRedAspect = 2.4
    static let relaxedRedAspectMinimumPixels = 32
    static let maximumWidthRatio = 0.07
}

// 同じ上側中央値を暗背景マスクと明背景の赤ゲートで共有する。
func saberBackgroundMedian(_ values: [UInt8]) -> Int {
    var histogram = Array(repeating: 0, count: 256)
    for value in values { histogram[Int(value)] += 1 }
    var count = 0, background = 0
    for value in histogram.indices {
        count += histogram[value]
        if count > values.count / 2 { background = value; break }
    }
    return background
}

// カメラのフレームキューが所有する。1/4 EMA と2階調のデッドバンドで境界の往復を抑える。
final class SaberBackgroundState {
    private var filtered: Double?
    private var stable: Double = 0

    func reset() { filtered = nil; stable = 0 }

    func update(_ background: Int) -> Double {
        guard let previous = filtered else {
            filtered = Double(background); stable = Double(background)
            return stable
        }
        let next = previous + (Double(background) - previous) * 0.25
        filtered = next
        if abs(next - stable) >= 2 { stable = next }
        return stable
    }
}

private func shutterEmitterMasks(baseAddress: UnsafePointer<UInt8>, width: Int, height: Int,
                                  bytesPerRow: Int, step: Int, values: [UInt8],
                                  chroma: [UInt8], background: Double,
                                  redMagenta: inout [UInt8]) -> [SaberColor: [UInt8]] {
    guard background <= Double(ShutterEmitterThresholds.maximumBackground) else { return [:] }
    let floor = max(Double(ShutterEmitterThresholds.minimumValue),
                    background * Double(ShutterEmitterThresholds.minimumBackgroundRatio))
    var red = Array(repeating: UInt8(0), count: values.count), blue = red
    var magenta = red
    for i in values.indices {
        let value = Int(values[i])
        guard Double(value) >= floor, value <= ShutterEmitterThresholds.maximumValue,
              Double(chroma[i]) / Double(max(value, 1)) >= ShutterEmitterThresholds.minimumPurity else { continue }
        let offset = (i / width) * step * bytesPerRow + (i % width) * step * 4
        let hsv = saberHSV(baseAddress[offset + 2], baseAddress[offset + 1], baseAddress[offset])
        if hsv.hue <= ShutterEmitterThresholds.redHueEdge || hsv.hue >= 360 - ShutterEmitterThresholds.redHueEdge {
            red[i] = 1; magenta[i] = 1
        } else if hsv.hue >= ShutterEmitterThresholds.redMagentaHueStart {
            magenta[i] = 1
        }
        if ShutterEmitterThresholds.blueHueRange.contains(hsv.hue) { blue[i] = 1 }
    }
    redMagenta = magenta
    return [.red: red, .blue: blue]
}

/// 候補の最大値の下限。背景24以下は画素の下限（背景×3）と同じで暗い会場は変えない。
/// 背景24→40で比を3→5へ上げ、220で頭打ち（明るい部屋の照り返しだけを落とす）。
func shutterEmitterPeakFloor(background: Double) -> Double {
    let ramp = min(max((background - 24) / 16, 0), 1)
    let ratio = Double(ShutterEmitterThresholds.minimumBackgroundRatio)
        + (ShutterEmitterThresholds.minimumPeakBackgroundRatio - Double(ShutterEmitterThresholds.minimumBackgroundRatio)) * ramp
    return min(background * ratio, ShutterEmitterThresholds.maximumPeakFloor)
}

func minimumShutterEmitterAspect(color: SaberColor, pixels: Int) -> Double {
    color == .red && pixels >= ShutterEmitterThresholds.relaxedRedAspectMinimumPixels
        ? ShutterEmitterThresholds.relaxedRedAspect : ShutterEmitterThresholds.minimumAspect
}

private func shutterEmitterCandidates(mask: [UInt8], width: Int, height: Int,
                                      color: SaberColor, values: [UInt8], chroma: [UInt8],
                                      radiance: [UInt8], collectEvidence: Bool,
                                      background: Double = 0,
                                      componentDiagnostics: SaberComponentDiagnostics? = nil) -> [SaberCandidate] {
    let peakFloor = shutterEmitterPeakFloor(background: background)
    let evidence = SaberEvidence(color: color, radiance: radiance, value: values, chroma: chroma,
                                 colorMask: mask, coreMask: Array(repeating: 0, count: mask.count),
                                 collectObserverEvidence: collectEvidence)
    return saberCandidates(in: mask, width: width, height: height, evidence: evidence,
                           minimumAreaOverride: ShutterEmitterThresholds.minimumPixels,
                           componentDiagnostics: componentDiagnostics, diagnosticPath: "shutter-color-emitter").compactMap { candidate in
        let length = max(candidate.rawPCASpan, 1)
        let meanWidth = Double(candidate.pointCount) / length
        guard Double(candidate.peakValue) >= peakFloor else {
            componentDiagnostics?.candidate(candidate, stage: "emitter", reason: "emitterPeak",
                path: "shutter-color-emitter", length: length, aspect: length / max(meanWidth, 1))
            return nil
        }
        guard Double(candidate.pointCount) <= Double(width * height) * ShutterEmitterThresholds.maximumAreaRatio else {
            componentDiagnostics?.candidate(candidate, stage: "emitter", reason: "emitterArea",
                path: "shutter-color-emitter", length: length, aspect: length / max(meanWidth, 1))
            return nil
        }
        guard length / max(meanWidth, 1) >= minimumShutterEmitterAspect(color: color, pixels: candidate.pointCount) else {
            componentDiagnostics?.candidate(candidate, stage: "emitter", reason: "emitterAspect",
                path: "shutter-color-emitter", length: length, aspect: length / max(meanWidth, 1))
            return nil
        }
        guard meanWidth <= Double(min(width, height)) * ShutterEmitterThresholds.maximumWidthRatio else {
            componentDiagnostics?.candidate(candidate, stage: "emitter", reason: "emitterWidth",
                path: "shutter-color-emitter", length: length, aspect: length / max(meanWidth, 1))
            return nil
        }
        componentDiagnostics?.candidate(candidate, stage: "emitter", reason: "passed",
            path: "shutter-color-emitter", length: length, aspect: length / max(meanWidth, 1))
        var emitter = candidate
        emitter.source = "shutter-color-emitter"
        emitter.isEmitterEligible = true
        return emitter
    }
}

func analyzeSabers(baseAddress: UnsafePointer<UInt8>, width: Int, height: Int, bytesPerRow: Int,
                   redThreshold: ColorThreshold, blueThreshold: ColorThreshold,
                   sampleStep: Int = 2, collectProfile: Bool = false,
                   collectPipelineDiagnostics: Bool = false,
                   collectObserverEvidence: Bool = false,
                   collectComponentDiagnostics: Bool = false,
                   ignoreZones: [SaberColor: [SaberIgnoreZone]] = [:],
                   playArea: SaberPlayArea? = nil,
                  trackLock: SaberTrackLock? = nil, now: Double = 0,
                   shutterEmitter: ShutterEmitterMode = ShutterEmitterMode(),
                   backgroundState: SaberBackgroundState? = nil) -> SaberFrameAnalysis {
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
    // 整数の明度・chroma は HSV の Double 値と厳密に同じ。必要な画素だけ hue を計算する。
    let relaxedBrightness = max(SaberColorModelThresholds.diffuserBlueMinimumBrightness,
                                Int(blueThreshold.brightness) - SaberColorModelThresholds.diffuserBlueBrightnessOffset)
    let relaxedDominance = max(SaberColorModelThresholds.diffuserBlueMinimumRedDominance,
                              Int(blueThreshold.dominance) - SaberColorModelThresholds.diffuserBlueDominanceOffset)
    let greenDominance = max(SaberColorModelThresholds.diffuserBlueMinimumGreenDominance,
                            relaxedDominance / 4)
    redMask.withUnsafeMutableBufferPointer { redMaskBuffer in
        blueMask.withUnsafeMutableBufferPointer { blueMaskBuffer in
            blueDiffuserMask.withUnsafeMutableBufferPointer { blueDiffuserMaskBuffer in
                redEmitterMask.withUnsafeMutableBufferPointer { redEmitterMaskBuffer in
                    blueEmitterMask.withUnsafeMutableBufferPointer { blueEmitterMaskBuffer in
                        brightCoreMask.withUnsafeMutableBufferPointer { brightCoreMaskBuffer in
                            valueMap.withUnsafeMutableBufferPointer { valueMapBuffer in
                                chromaMap.withUnsafeMutableBufferPointer { chromaMapBuffer in
                                    radianceMap.withUnsafeMutableBufferPointer { radianceMapBuffer in
                                        let redMask = redMaskBuffer.baseAddress!
                                        let blueMask = blueMaskBuffer.baseAddress!
                                        let blueDiffuserMask = blueDiffuserMaskBuffer.baseAddress!
                                        let redEmitterMask = redEmitterMaskBuffer.baseAddress!
                                        let blueEmitterMask = blueEmitterMaskBuffer.baseAddress!
                                        let brightCoreMask = brightCoreMaskBuffer.baseAddress!
                                        let valueMap = valueMapBuffer.baseAddress!
                                        let chromaMap = chromaMapBuffer.baseAddress!
                                        let radianceMap = radianceMapBuffer.baseAddress!
                                        for sampleY in 0..<maskHeight {
                                            let row = baseAddress + sampleY * step * bytesPerRow
                                            let maskRow = sampleY * maskWidth
                                            for sampleX in 0..<maskWidth {
                                                let offset = sampleX * step * 4
                                                let blue = row[offset], green = row[offset + 1], red = row[offset + 2]
                                                let index = maskRow + sampleX
                                                let maximum = max(red, max(green, blue))
                                                let minimum = min(red, min(green, blue))
                                                let chroma = maximum - minimum
                                                let secondChannel = Int(red) + Int(green) + Int(blue)
                                                    - Int(maximum) - Int(minimum)
                                                radianceMap[index] = UInt8(secondChannel)
                                                valueMap[index] = maximum
                                                chromaMap[index] = chroma
                                                if maximum >= 235 && secondChannel >= 100 { brightCoreMask[index] = 1 }
                                                let possibleRed = maximum >= redThreshold.brightness && chroma >= redThreshold.dominance
                                                let possibleBlue = maximum >= blueThreshold.brightness && chroma >= blueThreshold.dominance
                                                let possibleDiffuser = Int(blue) >= relaxedBrightness
                                                    && Int(blue) - Int(red) >= relaxedDominance
                                                    && Int(blue) - Int(green) >= greenDominance
                                                guard possibleRed || possibleBlue || possibleDiffuser else { continue }
                                                let hsv = saberHSV(red, green, blue)
                                                let isEmitterPixel = hsv.value >= 215
                                                    && hsv.chroma / max(hsv.value, 1) >= 0.35
                                                if possibleRed && matchesSaberHSV(hsv, color: .red, threshold: redThreshold) {
                                                    redMask[index] = 1
                                                    redMaskPixelCount += 1
                                                    if isEmitterPixel { redEmitterMask[index] = 1 }
                                                }
                                                if possibleBlue && matchesSaberHSV(hsv, color: .blue, threshold: blueThreshold) {
                                                    blueMask[index] = 1
                                                    blueMaskPixelCount += 1
                                                    if isEmitterPixel { blueEmitterMask[index] = 1 }
                                                }
                                                if possibleDiffuser && matchesBlueDiffuserPixel(red, green, blue, hsv: hsv,
                                                                                               threshold: blueThreshold) {
                                                    blueDiffuserMask[index] = 1
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
    let background = shutterEmitter.active ? saberBackgroundMedian(valueMap) : 0
    if !shutterEmitter.active { backgroundState?.reset() }
    let redBackground = shutterEmitter.active
        ? (backgroundState?.update(background) ?? Double(background)) : 0
    var lowISORedMagentaMask: [UInt8] = []
    let lowISOMasks = shutterEmitter.active
        ? shutterEmitterMasks(baseAddress: baseAddress, width: maskWidth, height: maskHeight,
                              bytesPerRow: bytesPerRow, step: step, values: valueMap, chroma: chromaMap,
                              background: redBackground, redMagenta: &lowISORedMagentaMask) : [:]
    let scanEnd = collectProfile ? ProcessInfo.processInfo.systemUptime : 0
    let strictBlueMask = blueMask
    blueMask = supportedBlueDiffuserMask(
        strictMask: strictBlueMask, relaxedMask: blueDiffuserMask,
        width: maskWidth, height: maskHeight
    )
    blueMaskPixelCount += blueMask.indices.reduce(0) { count, index in
        count + (blueMask[index] != 0 && strictBlueMask[index] == 0 ? 1 : 0)
    }
    if collectProfile {
        let maskEnd = ProcessInfo.processInfo.systemUptime
        profile.bgraScanMs = (scanEnd - scanStart) * 1000
        profile.maskGenerationMs = (maskEnd - scanEnd) * 1000
        profile.pixelScanHSVMaskMs = (maskEnd - scanStart) * 1000
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
    var componentDiagnostics: [SaberColor: SaberComponentDiagnostics] = [:]
    for (color, rawMask, emitterMask, maskPixelCount) in masks {
        let rejection = collectComponentDiagnostics ? SaberComponentDiagnostics() : nil
        if let rejection {
            rejection.sampleStep = step
            rejection.maskPixels = maskPixelCount
            // mainの桃色拡張マスクも含め、赤マスクが空でもmaskEmptyと誤記しない。
            let emitterDiagnosticMask = color == .red ? lowISORedMagentaMask : (lowISOMasks[color] ?? [])
            rejection.emitterMaskPixels = emitterDiagnosticMask.reduce(0) { $0 + Int($1) }
            componentDiagnostics[color] = rejection
        }
        // Most live frames contain at most one saber color. Do not run several
        // full-mask morphology/component passes for an absent color; this is
        // an exact empty-mask fast path and does not alter candidate scoring.
        // 走査済みの通常画素数は rawMask の非空判定と同じ（青の補助画素も加算済み）。
        guard maskPixelCount > 0 || lowISOMasks[color]?.contains(1) == true
                || (color == .red && lowISORedMagentaMask.contains(1)) else {
            _ = trackLock?.select([], color: color, width: width, height: height, now: now)
            allCandidates[color] = []
            if collectPipelineDiagnostics {
                pipelineDiagnostics[color] = SaberColorPipelineDiagnostics(
                    maskPixelCount: 0, morphologyPixelCount: 0,
                    connectedComponentCount: 0
                )
            }
            continue
        }
        var candidates: [SaberCandidate] = []
        var selectionStart = collectProfile ? ProcessInfo.processInfo.systemUptime : 0
        var morphologyPixelCount = 0
        var connectedComponentCount = 0
        // 通常マスクが空なら膨張・収縮・芯の関連付けも空で、通常候補は生成できない。
        // 低ISO候補と後段の診断・座標拡大・追跡・範囲制約は共通経路を通す。
        if maskPixelCount > 0 {
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
                                         colorMask: rawMask, coreMask: associatedCore,
                                         collectObserverEvidence: collectObserverEvidence,
                                         redBackground: redBackground)
            // A dotted or one-sample-wide LED blade can vanish under opening while
            // a smooth reflection survives it. Always score close-only components
            // as well, then discard duplicates of components already found in the
            // cleaned mask. Shape and emitter evidence reject isolated noise.
            let componentStart = collectProfile ? ProcessInfo.processInfo.systemUptime : 0
            let componentObserver: (Int) -> Void = { count in
                morphologyPixelCount += count
                if collectPipelineDiagnostics { connectedComponentCount += 1 }
                if collectProfile { profile.connectedComponentCount += 1 }
            }
            let additionalComponentObserver: ((Int) -> Void)? = collectProfile
                ? { _ in profile.connectedComponentCount += 1 } : nil
            candidates = saberCandidates(in: cleaned, width: maskWidth, height: maskHeight,
                                             evidence: evidence, stageProfile: candidateStageProfile,
                                             componentObserver: componentObserver, collectEndpointDiagnostics: collectPipelineDiagnostics, componentDiagnostics: rejection, diagnosticPath: "color-mask")
            let closedCandidates = saberCandidates(
                in: closed,
                width: maskWidth, height: maskHeight, evidence: evidence,
                stageProfile: candidateStageProfile,
                componentObserver: additionalComponentObserver
            , collectEndpointDiagnostics: collectPipelineDiagnostics, componentDiagnostics: rejection, diagnosticPath: "color-close")
            for var candidate in closedCandidates {
                if let duplicate = candidates.firstIndex(where: { candidateAxisDistance($0, candidate) <= 3.0 }) {
                    // 10/28の交差した赤刃: openが細い先端を8px削る。明背景ゲートで認めた同じ刃だけ、
                    // 既存のclose候補が15%以上長く、bodyも85%以上残るときに重複元を置き換える。
                    guard candidate.acceptedBrightBackgroundRed,
                          !candidates[duplicate].isEmitterEligible || candidates[duplicate].acceptedBrightBackgroundRed,
                          candidate.rawPCASpan > candidates[duplicate].rawPCASpan * 1.15,
                          candidate.retainedBodyRatio >= 0.85 else { continue }
                    candidate.source = "color-close"
                    candidates[duplicate] = candidate
                    continue
                }
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
                    componentObserver: additionalComponentObserver,
                    minimumAreaOverride: sparseMinimumArea
                , collectEndpointDiagnostics: collectPipelineDiagnostics, componentDiagnostics: rejection, diagnosticPath: "color-sparse-raw")
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
                    stageProfile: candidateStageProfile,
                    componentObserver: additionalComponentObserver
                , collectEndpointDiagnostics: collectPipelineDiagnostics, componentDiagnostics: rejection, diagnosticPath: "color-emitter") : []
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
                    stageProfile: candidateStageProfile,
                    componentObserver: additionalComponentObserver
                , collectEndpointDiagnostics: collectPipelineDiagnostics, componentDiagnostics: rejection, diagnosticPath: "core-halo") : []
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
                                                      evidence: evidence, stageProfile: candidateStageProfile,
                                                      componentObserver: additionalComponentObserver, collectEndpointDiagnostics: collectPipelineDiagnostics, componentDiagnostics: rejection, diagnosticPath: "connected-core") {
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
            var proposalWorkspace = SaberProposalWorkspace()
            for proposal in proposals {
                if var candidate = saberCandidate(from: proposal, width: maskWidth,
                                                  height: maskHeight, evidence: evidence,
                                                  workspace: &proposalWorkspace, source: "core-line",
                                                  stageProfile: candidateStageProfile, collectEndpointDiagnostics: collectPipelineDiagnostics, componentDiagnostics: rejection, diagnosticPath: "core-line"),
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
                        if collectPipelineDiagnostics {
                            if let body = candidates.first(where: { existing in
                                guard existing.source == "connected-core" else { return false }
                                let a = existing.comparisonEndpoints.0
                                let b = existing.comparisonEndpoints.1
                                let dx = Double(b.x - a.x), dy = Double(b.y - a.y)
                                let length = hypot(dx, dy)
                                guard length > 0 else { return false }
                                let cx = Double(candidate.comparisonEndpoints.0.x
                                    + candidate.comparisonEndpoints.1.x) / 2 - Double(a.x)
                                let cy = Double(candidate.comparisonEndpoints.0.y
                                    + candidate.comparisonEndpoints.1.y) / 2 - Double(a.y)
                                let along = (cx * dx + cy * dy) / length
                                let across = abs(cx * dy - cy * dx) / length
                                return along >= -4 && along <= length + 4 && across <= 8
                            }) {
                                let a = body.comparisonEndpoints.0, b = body.comparisonEndpoints.1
                                let dx = Double(b.x - a.x), dy = Double(b.y - a.y)
                                let length = hypot(dx, dy)
                                if length > 0 {
                                    let cx = Double(candidate.comparisonEndpoints.0.x
                                        + candidate.comparisonEndpoints.1.x) / 2 - Double(a.x)
                                    let cy = Double(candidate.comparisonEndpoints.0.y
                                        + candidate.comparisonEndpoints.1.y) / 2 - Double(a.y)
                                    let along = (cx * dx + cy * dy) / length
                                    candidate.diagnosticRejections += [
                                        SaberEligibilityDecision(name: "core-line-overlap.alongBefore",
                                            value: along, comparison: "<", threshold: -4),
                                        SaberEligibilityDecision(name: "core-line-overlap.alongAfter",
                                            value: along, comparison: ">", threshold: length + 4),
                                        SaberEligibilityDecision(name: "core-line-overlap.across",
                                            value: abs(cx * dy - cy * dx) / length,
                                            comparison: ">", threshold: 8)
                                    ]
                                }
                            }
                        }
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
            selectionStart = collectProfile ? ProcessInfo.processInfo.systemUptime : 0
            if color == .blue {
                candidates = preferTrustedBlueEmitter(
                    in: candidates, minimumFrameDimension: min(maskWidth, maskHeight)
                )
                // A stronger unsupported Hough slice can expose weak background
                // candidates. Reject only candidates without their own coherent
                // color/body or bright connected-core evidence. The rejected line
                // itself stays ineligible under the existing proposal quality gate.
                let rejectedLineScore = candidates.lazy.filter { $0.source == "core-line-low-confidence" }.map(\.score).max()
                let eligibleScore = candidates.lazy.filter(\.isEmitterEligible).map(\.score).max()
                let hasTrustedEmitter = candidates.contains {
                    isTrustedBlueEmitter($0, minimumFrameDimension: min(maskWidth, maskHeight))
                }
                if let rejectedLineScore, let eligibleScore,
                   rejectedLineScore >= eligibleScore, !hasTrustedEmitter {
                    for index in candidates.indices where candidates[index].isEmitterEligible
                        && !hasIndependentBlueEvidence(candidates[index]) {
                        candidates[index].isEmitterEligible = false
                        candidates[index].source += "-unsupported-core-line-candidate"
                        if collectPipelineDiagnostics {
                            candidates[index].diagnosticRejections.append(SaberEligibilityDecision(
                                name: "unsupported-core-line-candidate.rejectedLineScore",
                                value: rejectedLineScore, comparison: "<", threshold: eligibleScore))
                            candidates[index].diagnosticRejections += [
                                SaberEligibilityDecision(name: "unsupported-core-line-candidate.trustedEmitter",
                                    value: hasTrustedEmitter ? 1 : 0, comparison: "==", threshold: 1),
                                SaberEligibilityDecision(name: "unsupported-core-line-candidate.independentEvidence",
                                    value: 0, comparison: "==", threshold: 1)
                            ]
                        }
                    }
                }
            }
            if color == .blue {
                for index in candidates.indices where candidates[index].isEmitterEligible
                    && candidates[index].source == "color-emitter" {
                    let emitter = candidates[index]
                    let coversBroadRegion = Double(emitter.pointCount)
                        > Double(maskWidth * maskHeight) * 0.03
                    if coversBroadRegion && emitter.coreSupportRatio < 0.05
                        && emitter.axialDensity > 8.0 {
                        // A broad saturated surface can leave a long bright
                        // emitter-shaped component without a concentrated core.
                        candidates[index].isEmitterEligible = false
                        candidates[index].source = "color-emitter-broad-coreless"
                        if collectPipelineDiagnostics {
                            candidates[index].diagnosticRejections += [
                                SaberEligibilityDecision(name: "color-emitter-broad-coreless.area",
                                    value: Double(emitter.pointCount), comparison: "<=",
                                    threshold: Double(maskWidth * maskHeight) * 0.03),
                                SaberEligibilityDecision(name: "color-emitter-broad-coreless.coreSupport",
                                    value: emitter.coreSupportRatio, comparison: ">=", threshold: 0.05),
                                SaberEligibilityDecision(name: "color-emitter-broad-coreless.density",
                                    value: emitter.axialDensity, comparison: "<=", threshold: 8.0)
                            ]
                        }
                    }
                }
            }
            if color == .blue {
                for index in candidates.indices where candidates[index].isEmitterEligible
                    && candidates[index].source == "core-line" {
                    let line = candidates[index]
                    guard line.usedPointLEDFallback,
                          line.rawPCASpan >= line.robustMainIntervalLength * 4.0,
                          line.retainedBodyRatio < 0.50,
                          line.coreSupportRatio < 0.30 else { continue }
                    let hasSupportedLocalBody = candidates.contains { body in
                        guard body.isEmitterEligible,
                              body.coreSupportRatio >= line.coreSupportRatio + 0.10,
                              body.highValueRatio >= 0.35,
                              body.robustMainIntervalLength
                                >= line.robustMainIntervalLength * 0.60 else { return false }
                        let endpoints = [line.endpoints.0, line.endpoints.1]
                        let bodyEndpoints = [body.endpoints.0, body.endpoints.1]
                        return endpoints.contains { endpoint in
                            bodyEndpoints.contains { supported in
                                hypot(Double(endpoint.x - supported.x),
                                      Double(endpoint.y - supported.y)) <= 30.0
                            }
                        }
                    }
                    if hasSupportedLocalBody {
                        // A sparse raw extension reaches far beyond the local
                        // bright body. A separate, supported proposal covers the
                        // actual emitter near one end of that extension.
                        candidates[index].isEmitterEligible = false
                        candidates[index].source = "core-line-weak-raw-tail"
                        if collectPipelineDiagnostics {
                            candidates[index].diagnosticRejections.append(SaberEligibilityDecision(
                                name: "core-line-weak-raw-tail.span",
                                value: line.rawPCASpan, comparison: "<",
                                threshold: line.robustMainIntervalLength * 4.0))
                            candidates[index].diagnosticRejections += [
                                SaberEligibilityDecision(name: "core-line-weak-raw-tail.pointFallback",
                                    value: line.usedPointLEDFallback ? 1 : 0,
                                    comparison: "==", threshold: 0),
                                SaberEligibilityDecision(name: "core-line-weak-raw-tail.retainedBody",
                                    value: line.retainedBodyRatio, comparison: ">=", threshold: 0.50),
                                SaberEligibilityDecision(name: "core-line-weak-raw-tail.coreSupport",
                                    value: line.coreSupportRatio, comparison: ">=", threshold: 0.30),
                                SaberEligibilityDecision(name: "core-line-weak-raw-tail.localBody",
                                    value: hasSupportedLocalBody ? 1 : 0,
                                    comparison: "==", threshold: 0)
                            ]
                        }
                    }
                }
            }
            if color == .red {
                let connected = Array(candidates.lazy.filter {
                    $0.isEmitterEligible && !$0.source.hasPrefix("core-line")
                }.map(CandidateBodySnapshot.init))
                for index in candidates.indices where candidates[index].isEmitterEligible {
                    let proposal = candidates[index]
                    if proposal.source == "core-line",
                       proposal.rawPCASpan >= proposal.robustMainIntervalLength * 1.8,
                       proposal.retainedBodyRatio < 0.65,
                       connected.contains(where: { body in
                           body.rawPCASpan >= proposal.robustMainIntervalLength * 0.6
                               && body.meanColorPurity >= proposal.meanColorPurity - 0.10
                               && body.highValueRatio >= proposal.highValueRatio - 0.20
                               && body.longitudinalContinuity >= proposal.longitudinalContinuity
                               && body.axialDensity >= proposal.axialDensity
                       }) {
                        // The proposal joins distant bright objects while a
                        // coherent connected body already explains the blade.
                        candidates[index].isEmitterEligible = false
                        candidates[index].source = "core-line-weak-bridge"
                        if collectPipelineDiagnostics {
                            candidates[index].diagnosticRejections.append(SaberEligibilityDecision(
                                name: "core-line-weak-bridge.span",
                                value: proposal.rawPCASpan, comparison: ">=",
                                threshold: proposal.robustMainIntervalLength * 1.8))
                            candidates[index].diagnosticRejections.append(SaberEligibilityDecision(
                                name: "core-line-weak-bridge.retainedBody",
                                value: proposal.retainedBodyRatio, comparison: "<", threshold: 0.65))
                            if let body = connected.first(where: { body in
                                body.rawPCASpan >= proposal.robustMainIntervalLength * 0.6
                                    && body.meanColorPurity >= proposal.meanColorPurity - 0.10
                                    && body.highValueRatio >= proposal.highValueRatio - 0.20
                                    && body.longitudinalContinuity >= proposal.longitudinalContinuity
                                    && body.axialDensity >= proposal.axialDensity
                            }) {
                                candidates[index].diagnosticRejections += [
                                    SaberEligibilityDecision(name: "core-line-weak-bridge.bodySpan",
                                        value: body.rawPCASpan, comparison: ">=",
                                        threshold: proposal.robustMainIntervalLength * 0.6),
                                    SaberEligibilityDecision(name: "core-line-weak-bridge.bodyPurity",
                                        value: body.meanColorPurity, comparison: ">=",
                                        threshold: proposal.meanColorPurity - 0.10),
                                    SaberEligibilityDecision(name: "core-line-weak-bridge.bodyHighValue",
                                        value: body.highValueRatio, comparison: ">=",
                                        threshold: proposal.highValueRatio - 0.20),
                                    SaberEligibilityDecision(name: "core-line-weak-bridge.bodyContinuity",
                                        value: body.longitudinalContinuity, comparison: ">=",
                                        threshold: proposal.longitudinalContinuity),
                                    SaberEligibilityDecision(name: "core-line-weak-bridge.bodyDensity",
                                        value: body.axialDensity, comparison: ">=",
                                        threshold: proposal.axialDensity)
                                ]
                            }
                        }
                    } else if proposal.source == "core-halo",
                              connected.contains(where: { body in
                                  body.source == "color-mask"
                                      && body.rawPCASpan >= proposal.rawPCASpan * 1.4
                                      && body.highValueRatio >= 0.50
                                      && body.meanColorPurity >= 0.50
                                      && proposal.boundingBox.minX >= body.boundingBox.minX - 4
                                      && proposal.boundingBox.maxX <= body.boundingBox.maxX + 4
                                      && proposal.boundingBox.minY >= body.boundingBox.minY - 4
                                      && proposal.boundingBox.maxY <= body.boundingBox.maxY + 4
                              }) {
                        // A bright core is only a short part of the same red
                        // component; keep the full color body for endpoints.
                        candidates[index].isEmitterEligible = false
                        candidates[index].source = "core-halo-short-subsegment"
                        if collectPipelineDiagnostics {
                            if let body = connected.first(where: { body in
                                body.source == "color-mask"
                                    && body.rawPCASpan >= proposal.rawPCASpan * 1.4
                                    && body.highValueRatio >= 0.50
                                    && body.meanColorPurity >= 0.50
                                    && proposal.boundingBox.minX >= body.boundingBox.minX - 4
                                    && proposal.boundingBox.maxX <= body.boundingBox.maxX + 4
                                    && proposal.boundingBox.minY >= body.boundingBox.minY - 4
                                    && proposal.boundingBox.maxY <= body.boundingBox.maxY + 4
                            }) {
                                candidates[index].diagnosticRejections += [
                                    SaberEligibilityDecision(name: "core-halo-short-subsegment.span",
                                        value: body.rawPCASpan, comparison: "<",
                                        threshold: proposal.rawPCASpan * 1.4),
                                    SaberEligibilityDecision(name: "core-halo-short-subsegment.highValue",
                                        value: body.highValueRatio, comparison: "<", threshold: 0.50),
                                    SaberEligibilityDecision(name: "core-halo-short-subsegment.purity",
                                        value: body.meanColorPurity, comparison: "<", threshold: 0.50),
                                    SaberEligibilityDecision(name: "core-halo-short-subsegment.minX",
                                        value: Double(proposal.boundingBox.minX), comparison: "<",
                                        threshold: Double(body.boundingBox.minX - 4)),
                                    SaberEligibilityDecision(name: "core-halo-short-subsegment.maxX",
                                        value: Double(proposal.boundingBox.maxX), comparison: ">",
                                        threshold: Double(body.boundingBox.maxX + 4)),
                                    SaberEligibilityDecision(name: "core-halo-short-subsegment.minY",
                                        value: Double(proposal.boundingBox.minY), comparison: "<",
                                        threshold: Double(body.boundingBox.minY - 4)),
                                    SaberEligibilityDecision(name: "core-halo-short-subsegment.maxY",
                                        value: Double(proposal.boundingBox.maxY), comparison: ">",
                                        threshold: Double(body.boundingBox.maxY + 4))
                                ]
                            }
                        }
                    }
                }
            }
            let completeCandidates = Array(candidates.lazy.filter {
                $0.isEmitterEligible && !$0.source.hasPrefix("core-line")
            }.map(CandidateBodySnapshot.init))
            for index in candidates.indices where candidates[index].isEmitterEligible
                && candidates[index].source != "connected-core" {
                if color == .blue,
                   isTrustedBlueEmitter(candidates[index],
                                        minimumFrameDimension: min(maskWidth, maskHeight)) { continue }
                if completeCandidates.contains(where: {
                    candidateIsSubsegment(candidates[index].comparisonEndpoints, of: $0.comparisonEndpoints)
                }) {
                    candidates[index].isEmitterEligible = false
                    candidates[index].source += "-subsegment"
                    if collectPipelineDiagnostics {
                        if let body = completeCandidates.first(where: {
                            candidateIsSubsegment(candidates[index].comparisonEndpoints, of: $0.comparisonEndpoints)
                        }) {
                            let long = hypot(Double(body.comparisonEndpoints.1.x
                                - body.comparisonEndpoints.0.x),
                                Double(body.comparisonEndpoints.1.y
                                - body.comparisonEndpoints.0.y))
                            let short = hypot(Double(candidates[index].comparisonEndpoints.1.x
                                - candidates[index].comparisonEndpoints.0.x),
                                Double(candidates[index].comparisonEndpoints.1.y
                                - candidates[index].comparisonEndpoints.0.y))
                            candidates[index].diagnosticRejections.append(SaberEligibilityDecision(
                                name: "-subsegment.length", value: long,
                                comparison: "<", threshold: short * 1.50))
                            if long > 0 && short > 0 {
                                let longX = Double(body.comparisonEndpoints.1.x
                                    - body.comparisonEndpoints.0.x) / long
                                let longY = Double(body.comparisonEndpoints.1.y
                                    - body.comparisonEndpoints.0.y) / long
                                let shortX = Double(candidates[index].comparisonEndpoints.1.x
                                    - candidates[index].comparisonEndpoints.0.x) / short
                                let shortY = Double(candidates[index].comparisonEndpoints.1.y
                                    - candidates[index].comparisonEndpoints.0.y) / short
                                candidates[index].diagnosticRejections.append(SaberEligibilityDecision(
                                    name: "-subsegment.axisAlignment",
                                    value: abs(longX * shortX + longY * shortY),
                                    comparison: "<", threshold: 0.94))
                                for (endpoint, point) in [candidates[index].comparisonEndpoints.0,
                                                          candidates[index].comparisonEndpoints.1].enumerated() {
                                    let dx = Double(point.x - body.comparisonEndpoints.0.x)
                                    let dy = Double(point.y - body.comparisonEndpoints.0.y)
                                    let along = dx * longX + dy * longY
                                    let across = abs(-dx * longY + dy * longX)
                                    let prefix = "-subsegment.endpoint\(endpoint)"
                                    candidates[index].diagnosticRejections += [
                                        SaberEligibilityDecision(name: "\(prefix).before",
                                            value: along, comparison: "<", threshold: -6),
                                        SaberEligibilityDecision(name: "\(prefix).after",
                                            value: along, comparison: ">", threshold: long + 6),
                                        SaberEligibilityDecision(name: "\(prefix).across",
                                            value: across, comparison: ">", threshold: 6)
                                    ]
                                }
                            }
                        }
                    }
                    candidates[index].scoreBreakdown.proposalPenalty = -candidates[index].score * 0.5
                    candidates[index].score = candidates[index].scoreBreakdown.total
                }
            }
            candidates.sort { $0.score > $1.score }
            // Apply only after every existing eligibility gate and ranking. Scores and
            // ranked order stay intact; selection below takes the first surviving RED.
            if color == .red {
                for index in candidates.indices where candidates[index].isEmitterEligible {
                    let verdict = redWarmNoDeepRedSupport(
                        points: candidates[index].supportSamplePoints, baseAddress: baseAddress,
                        width: width, height: height, bytesPerRow: bytesPerRow, sampleStep: step)
                    candidates[index].warmNoDeepRed = verdict
                    candidates[index].endpointDiagnosticTrace?.emitter?.warmNoDeepRed = verdict
                    if verdict.rejected {
                        candidates[index].isEmitterEligible = false
                        if collectPipelineDiagnostics {
                            candidates[index].diagnosticRejections.append(SaberEligibilityDecision(
                                name: "warmNoDeepRed", value: verdict.warmFrac,
                                comparison: "<", threshold: 0.30))
                        }
                    }
                }
            }
            // 既存 gate と順位を確定してから青支持を確認し、次の eligible を選ぶ。
            if color == .blue {
                for index in candidates.indices where candidates[index].isEmitterEligible {
                    let verdict = blueNoDeepSupport(
                        points: candidates[index].supportSamplePoints, baseAddress: baseAddress,
                        width: width, height: height, bytesPerRow: bytesPerRow, sampleStep: step)
                    candidates[index].blueNoDeepSupport = verdict
                    candidates[index].endpointDiagnosticTrace?.emitter?.blueNoDeepSupport = verdict
                    if verdict.rejected {
                        candidates[index].isEmitterEligible = false
                        if collectPipelineDiagnostics {
                            candidates[index].diagnosticRejections.append(SaberEligibilityDecision(
                                name: "blueNoDeepSupport", value: Double(verdict.deepCount),
                                comparison: ">=", threshold: 1))
                        }
                    }
                }
            }
            if CoreLineGuard.enabled {
                applyCoreLineGuard(&candidates, evidence: evidence, width: maskWidth, step: step)
            }
            if color == .blue {
                for index in candidates.indices where candidates[index].isEmitterEligible
                    && isCorelessBlueSurface(candidates[index]) {
                    candidates[index].isEmitterEligible = false
                    if collectPipelineDiagnostics {
                        candidates[index].diagnosticRejections += [
                            SaberEligibilityDecision(name: "coreless-blue-surface.peak",
                                value: Double(candidates[index].peakValue), comparison: ">=", threshold: 242),
                            SaberEligibilityDecision(name: "coreless-blue-surface.coreSupport",
                                value: candidates[index].coreSupportRatio, comparison: ">=", threshold: 0.05)
                        ]
                    }
                }
            }
        }
        if let mask = lowISOMasks[color] {
            var additional = mask.contains(1)
                ? shutterEmitterCandidates(mask: mask, width: maskWidth, height: maskHeight,
                    color: color, values: valueMap, chroma: chromaMap, radiance: radianceMap,
                    collectEvidence: collectObserverEvidence, background: redBackground, componentDiagnostics: rejection)
                : []
            if color == .red, lowISORedMagentaMask != mask, lowISORedMagentaMask.contains(1) {
                // 通常の赤マスクの候補と重なる場所には足さない（今取れている刃の形を変えない）。
                let magenta = shutterEmitterCandidates(mask: lowISORedMagentaMask, width: maskWidth,
                    height: maskHeight, color: color, values: valueMap, chroma: chromaMap,
                    radiance: radianceMap, collectEvidence: collectObserverEvidence,
                    background: redBackground, componentDiagnostics: rejection)
                for var candidate in magenta where candidate.peakValue >= ShutterEmitterThresholds.redMagentaMinimumPeak
                    && candidate.meanColorPurity >= ShutterEmitterThresholds.redMagentaMinimumPurity
                    && candidate.localContrast >= ShutterEmitterThresholds.redMagentaMinimumContrast
                    && !additional.contains(where: {
                    $0.boundingBox.minX <= candidate.boundingBox.maxX
                        && candidate.boundingBox.minX <= $0.boundingBox.maxX
                        && $0.boundingBox.minY <= candidate.boundingBox.maxY
                        && candidate.boundingBox.minY <= $0.boundingBox.maxY
                }) {
                    candidate.source = "shutter-magenta-emitter"
                    additional.append(candidate)
                }
            }
            if !additional.isEmpty {
                candidates += additional
                candidates.sort { $0.score > $1.score }
            }
        }
        // 現在の認識処理が不適格とした候補だけを記録する。
        if let rejection {
            for c in candidates where !c.isEmitterEligible {
                rejection.candidate(c, stage: "postGeneration", reason: "ineligible")
            }
        }
        // 新しい継続経路にも既存の赤コーン・青緑面のsafety gateを同じ元画素で適用する。
        let continuityIndices: Set<Int> = trackLock == nil ? [] : Set(candidates.indices.filter { index in
            let c = candidates[index]
            guard SaberTrackLock.isEndOnCandidate(c, scale: Double(min(width, height)) / 480 / Double(step)),
                  !c.supportSamplePoints.isEmpty else { return false }
            if color == .red {
                return !redWarmNoDeepRedSupport(points: c.supportSamplePoints, baseAddress: baseAddress,
                    width: width, height: height, bytesPerRow: bytesPerRow, sampleStep: step).rejected
            }
            return !isCorelessBlueSurface(c)
                && !blueNoDeepSupport(points: c.supportSamplePoints, baseAddress: baseAddress,
                    width: width, height: height, bytesPerRow: bytesPerRow, sampleStep: step).rejected
        })
        let scaled = candidates.map { candidate in
            let endpoints = candidate.endpoints
            return SaberCandidate(
                source: candidate.source,
                radiance: candidate.radiance,
                comparisonEndpoints: (
                    PixelPoint(x: candidate.comparisonEndpoints.0.x * step,
                               y: candidate.comparisonEndpoints.0.y * step),
                    PixelPoint(x: candidate.comparisonEndpoints.1.x * step,
                               y: candidate.comparisonEndpoints.1.y * step)
                ),
                endpoints: (PixelPoint(x: endpoints.0.x * step,
                                       y: endpoints.0.y * step),
                            PixelPoint(x: endpoints.1.x * step,
                                       y: endpoints.1.y * step)),
                boundingBox: SaberBoundingBox(
                    minX: candidate.boundingBox.minX * step,
                    minY: candidate.boundingBox.minY * step,
                    maxX: candidate.boundingBox.maxX * step,
                    maxY: candidate.boundingBox.maxY * step
                ),
                score: candidate.score, scoreBreakdown: candidate.scoreBreakdown,
                isEmitterEligible: candidate.isEmitterEligible,
                isCompactRed: candidate.isCompactRed,
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
                usedPointLEDFallback: candidate.usedPointLEDFallback,
                coreLineGuard: candidate.coreLineGuard,
                warmNoDeepRed: candidate.warmNoDeepRed,
                blueNoDeepSupport: candidate.blueNoDeepSupport,
                diagnosticRejections: candidate.diagnosticRejections,
                observerEmitterInputs: candidate.observerEmitterInputs,
                endpointDiagnosticTrace: candidate.endpointDiagnosticTrace.map { trace in
                    SaberEndpointDiagnosticTrace(
                        centroidX: trace.centroidX * Double(step),
                        centroidY: trace.centroidY * Double(step),
                        bodyEndpoints: trace.bodyEndpoints.map {
                            (PixelPoint(x: $0.0.x * step, y: $0.0.y * step),
                             PixelPoint(x: $0.1.x * step, y: $0.1.y * step))
                        }, minimumArea: trace.minimumArea, bodyPointCount: trace.bodyPointCount,
                        establishedContinuousBody: trace.establishedContinuousBody,
                        denseTrimmedCoreLine: trace.denseTrimmedCoreLine,
                        stronglyTrimmedCoreLine: trace.stronglyTrimmedCoreLine,
                        diffusedBlueBody: trace.diffusedBlueBody, gatingValues: trace.gatingValues,
                        emitter: trace.emitter)
                }, acceptedBrightBackgroundRed: candidate.acceptedBrightBackgroundRed
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
        let zones = ignoreZones[color] ?? []
        if let rejection {
            for c in scaled where c.isEmitterEligible {
                if saberIgnored(c.endpoints, width: width, height: height, zones: zones) {
                    rejection.candidate(c, stage: "postGeneration", reason: "ignore", scale: step)
                } else if trackLock == nil, let playArea,
                          !playArea.containsMidpoint(c.endpoints, width: width, height: height) {
                    rejection.candidate(c, stage: "postGeneration", reason: "playArea", scale: step)
                }
            }
        }
        // 無視範囲は常に適用する。追跡ONのプレイ範囲は取得時だけに適用する。
        let allowed: (SaberCandidate) -> Bool = {
            $0.isEmitterEligible && (zones.isEmpty || !saberIgnored($0.endpoints, width: width, height: height, zones: zones))
        }
        let winner: SaberCandidate?
        if let trackLock {
            rejection?.candidateScale = step
            winner = trackLock.select(scaled.filter(allowed), color: color, width: width, height: height,
                                      now: now, playArea: playArea, observedCandidates: scaled,
                                      continuityCandidates: scaled.enumerated().compactMap { index, c in
                                          continuityIndices.contains(index)
                                              && !saberIgnored(c.endpoints, width: width, height: height, zones: zones) ? c : nil
                                      }, componentDiagnostics: rejection)
        } else {
            winner = scaled.first {
                allowed($0) && (playArea?.containsMidpoint($0.endpoints, width: width, height: height) ?? true)
            }
        }
        if let winner {
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
        profile.connectedComponentsMs = candidateStageProfile?.connectedComponentsMs ?? 0
        profile.candidateScoringMs = candidateStageProfile?.candidateScoringMs ?? 0
        profile.candidateGenerationMs = max(0, profile.componentAndScoreMs
            - profile.connectedComponentsMs - profile.candidateScoringMs)
        profile.componentTraversalAndProposalOverheadMs = max(
            0, profile.componentAndScoreMs + profile.lineScoreMs
                - profile.shapeAndAxisMs - profile.brightnessContrastColorMs
                - profile.endpointAndBoundsMs
        )
    }
    var result = SaberFrameAnalysis(candidates: allCandidates, selected: selected,
                              trackLockSuppressed: trackLock?.suppressed ?? [],
                              profile: collectProfile ? profile : nil,
                              pipelineDiagnostics: collectPipelineDiagnostics
                                  ? pipelineDiagnostics : nil)
    result.componentDiagnostics = collectComponentDiagnostics ? componentDiagnostics : nil
    result.backgroundMedian = shutterEmitter.active ? background : nil
    return result
}

func detectSabers(baseAddress: UnsafePointer<UInt8>, width: Int, height: Int, bytesPerRow: Int,
                  redThreshold: ColorThreshold, blueThreshold: ColorThreshold,
                  sampleStep: Int = 2,
                  ignoreZones: [SaberColor: [SaberIgnoreZone]] = [:],
                  playArea: SaberPlayArea? = nil,
                   trackLock: SaberTrackLock? = nil, now: Double = 0) -> [SaberColor: (PixelPoint, PixelPoint)] {
    analyzeSabers(baseAddress: baseAddress, width: width, height: height, bytesPerRow: bytesPerRow,
                  redThreshold: redThreshold, blueThreshold: blueThreshold,
                  sampleStep: sampleStep, ignoreZones: ignoreZones, playArea: playArea, trackLock: trackLock, now: now).selected
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
                   collectPipelineDiagnostics: Bool = false,
                   collectObserverEvidence: Bool = false,
                   collectComponentDiagnostics: Bool = false,
                   ignoreZones: [SaberColor: [SaberIgnoreZone]] = [:],
                   playArea: SaberPlayArea? = nil,
                   trackLock: SaberTrackLock? = nil, now: Double = 0,
                   shutterEmitter: ShutterEmitterMode = ShutterEmitterMode(),
                   backgroundState: SaberBackgroundState? = nil) -> SaberFrameAnalysis {
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
                             collectPipelineDiagnostics: collectPipelineDiagnostics,
                             collectObserverEvidence: collectObserverEvidence,
                             collectComponentDiagnostics: collectComponentDiagnostics, ignoreZones: ignoreZones,
                             playArea: playArea, trackLock: trackLock, now: now, shutterEmitter: shutterEmitter,
                             backgroundState: backgroundState)
    }
}

// 座標はmask格子へ揃える。JSON保存時にsampleStepを別途付ける。
extension SaberComponentDiagnostics {
    func candidate(_ c: SaberCandidate, stage: String, reason: String, path: String? = nil,
                   length: Double? = nil, aspect: Double? = nil, scale: Int? = nil) {
        // 上限後はbbox配列や数値の計算も省く。
        guard hasCapacity(path: path ?? c.source, stage: stage) else { omitted += 1; return }
        let scale = scale ?? candidateScale
        let d = Double(max(scale, 1)), box = c.boundingBox
        let span = length ?? max(c.rawPCASpan / d, 1)
        append(SaberComponentDiagnostic(path: path ?? c.source, stage: stage, reason: reason,
            bbox: [box.minX / scale, box.minY / scale, box.maxX / scale, box.maxY / scale],
            pixels: c.pointCount, length: span, meanWidth: Double(c.pointCount) / span,
            aspect: aspect ?? (c.scoreBreakdown.extent / 2 * pow(c.rawPCASpan / d, 2) / Double(max(c.pointCount, 1))),
            extent: c.scoreBreakdown.extent / 2))
    }
}
