import Foundation
import CoreGraphics

struct PixelPoint: Equatable {
    let x: Int
    let y: Int
}

struct ColorThreshold: Equatable {
    // Shared strict-mask defaults. Hue windows are named below because they
    // differ by color; these three values remain Inspector/runtime tunables.
    var brightness: UInt8 = 145
    var dominance: UInt8 = 25
    var saturation: UInt8 = 30
}

enum SaberColor: Hashable {
    case red
    case blue
}

enum SaberColorModelThresholds {
    static let strictRedHueLower = 340.0
    static let strictRedHueUpper = 20.0
    static let strictBlueHueLower = 198.0
    static let strictBlueHueUpper = 248.0

    static let diffuserBlueHueLower = 190.0
    static let diffuserBlueHueUpper = 260.0
    static let diffuserBlueMinimumBrightness = 110
    static let diffuserBlueBrightnessOffset = 25
    static let diffuserBlueMinimumRedDominance = 8
    static let diffuserBlueDominanceOffset = 17
    static let diffuserBlueMinimumGreenDominance = 2
    static let diffuserBlueMinimumSaturation = 10
    static let diffuserBlueSaturationOffset = 20
    static let diffuserNeighborhoodRadius = 2
}

private enum EndpointSelectionThresholds {
    static let connectedBodyContinuity = 0.90
    static let coreLineMaximumGap = 2
    static let denseCoreLineDensity = 4.0
    static let stronglyTrimmedRetainedRatio = 0.30
    static let diffuserRetainedRatio = 0.65
    static let diffuserContinuity = 0.80
    static let diffuserColorPurity = 0.40
    static let diffuserCoreSupport = 0.35
}

func isBright(_ red: UInt8, _ green: UInt8, _ blue: UInt8, color: SaberColor, threshold: ColorThreshold) -> Bool {
    let redValue = Int(red)
    let greenValue = Int(green)
    let blueValue = Int(blue)
    let brightnessThreshold = Int(threshold.brightness)
    let dominanceThreshold = Int(threshold.dominance)
    let saturationThreshold = Int(threshold.saturation)
    let maximum = max(redValue, max(greenValue, blueValue))
    let minimum = min(redValue, min(greenValue, blueValue))

    switch color {
    case .red:
        return redValue >= brightnessThreshold && redValue - max(greenValue, blueValue) >= dominanceThreshold && maximum - minimum >= saturationThreshold
    case .blue:
        return blueValue >= brightnessThreshold && blueValue - max(redValue, greenValue) >= dominanceThreshold && maximum - minimum >= saturationThreshold
    }
}

struct SaberHSV {
    let hue: Double
    let saturation: Double
    let value: Double
    let chroma: Double
}

func saberHSV(_ red: UInt8, _ green: UInt8, _ blue: UInt8) -> SaberHSV {
    let r = Double(red), g = Double(green), b = Double(blue)
    let maximum = max(r, max(g, b))
    let minimum = min(r, min(g, b))
    let delta = maximum - minimum
    let hue: Double
    if delta == 0 {
        hue = 0
    } else if maximum == r {
        hue = 60.0 * ((g - b) / delta).truncatingRemainder(dividingBy: 6.0)
    } else if maximum == g {
        hue = 60.0 * ((b - r) / delta + 2.0)
    } else {
        hue = 60.0 * ((r - g) / delta + 4.0)
    }
    let normalizedHue = hue < 0 ? hue + 360.0 : hue
    return SaberHSV(hue: normalizedHue,
                    saturation: maximum > 0 ? delta * 255.0 / maximum : 0,
                    value: maximum, chroma: delta)
}

/// HSV gate matching the useful ranges from camera.py. Hue is expressed in
/// degrees here (OpenCV's stored hue values are multiplied by two).
func matchesSaberHSV(_ hsv: SaberHSV, color: SaberColor, threshold: ColorThreshold) -> Bool {
    guard hsv.value >= Double(threshold.brightness),
          hsv.chroma >= Double(threshold.dominance),
          hsv.saturation >= Double(threshold.saturation) else { return false }
    switch color {
    case .red:
        return hsv.hue >= SaberColorModelThresholds.strictRedHueLower
            || hsv.hue <= SaberColorModelThresholds.strictRedHueUpper
    case .blue:
        return hsv.hue >= SaberColorModelThresholds.strictBlueHueLower
            && hsv.hue <= SaberColorModelThresholds.strictBlueHueUpper
    }
}

func matchesSaberHSV(_ red: UInt8, _ green: UInt8, _ blue: UInt8,
                     color: SaberColor, threshold: ColorThreshold) -> Bool {
    matchesSaberHSV(saberHSV(red, green, blue), color: color, threshold: threshold)
}

/// Secondary color model for the current paper-diffused blue blade. It admits
/// cyan/near-white and motion-darkened blue pixels, but callers must still
/// require nearby support from the established HSV blue mask. Keeping that
/// spatial requirement outside this per-pixel function prevents a permissive
/// full-frame blue mask from turning clothes or daylight into candidates.
func matchesBlueDiffuserPixel(_ red: UInt8, _ green: UInt8, _ blue: UInt8,
                              threshold: ColorThreshold) -> Bool {
    matchesBlueDiffuserPixel(red, green, blue, hsv: saberHSV(red, green, blue),
                             threshold: threshold)
}

/// Reuse the HSV conversion already made by the production BGRA scan.
func matchesBlueDiffuserPixel(_ red: UInt8, _ green: UInt8, _ blue: UInt8,
                              hsv: SaberHSV, threshold: ColorThreshold) -> Bool {
    let redValue = Int(red), greenValue = Int(green), blueValue = Int(blue)
    let relaxedBrightness = max(SaberColorModelThresholds.diffuserBlueMinimumBrightness,
                                Int(threshold.brightness) - SaberColorModelThresholds.diffuserBlueBrightnessOffset)
    let relaxedDominance = max(SaberColorModelThresholds.diffuserBlueMinimumRedDominance,
                               Int(threshold.dominance) - SaberColorModelThresholds.diffuserBlueDominanceOffset)
    let greenDominance = max(SaberColorModelThresholds.diffuserBlueMinimumGreenDominance,
                             relaxedDominance / 4)
    return blueValue >= relaxedBrightness
        && blueValue - redValue >= relaxedDominance
        && blueValue - greenValue >= greenDominance
        && hsv.saturation >= Double(max(SaberColorModelThresholds.diffuserBlueMinimumSaturation,
                                        Int(threshold.saturation) - SaberColorModelThresholds.diffuserBlueSaturationOffset))
        && hsv.hue >= SaberColorModelThresholds.diffuserBlueHueLower
        && hsv.hue <= SaberColorModelThresholds.diffuserBlueHueUpper
}

/// Expand only around pixels already accepted by the strict blue model. This
/// recovers the pale diffuser halo without admitting unrelated relaxed-color
/// regions elsewhere in the frame.
func supportedBlueDiffuserMask(strictMask: [UInt8], relaxedMask: [UInt8],
                               width: Int, height: Int,
                               radius: Int = SaberColorModelThresholds.diffuserNeighborhoodRadius) -> [UInt8] {
    guard strictMask.count == width * height, relaxedMask.count == strictMask.count else {
        return strictMask
    }
    let support = dilateSaberMask(strictMask, width: width, height: height, radius: radius)
    return strictMask.indices.map { index in
        strictMask[index] != 0 || (relaxedMask[index] != 0 && support[index] != 0) ? 1 : 0
    }
}

private func binaryDilate(_ input: [UInt8], width: Int, height: Int, radius: Int) -> [UInt8] {
    guard radius > 0 else { return input }
    var output = Array(repeating: UInt8(0), count: input.count)
    for y in 0..<height {
        for x in 0..<width where input[y * width + x] != 0 {
            for ny in max(0, y - radius)...min(height - 1, y + radius) {
                for nx in max(0, x - radius)...min(width - 1, x + radius) {
                    guard abs(nx - x) + abs(ny - y) <= radius else { continue }
                    output[ny * width + nx] = 1
                }
            }
        }
    }
    return output
}

private func binaryErode(_ input: [UInt8], width: Int, height: Int, radius: Int) -> [UInt8] {
    guard radius > 0 else { return input }
    var output = Array(repeating: UInt8(0), count: input.count)
    guard width > radius * 2, height > radius * 2 else { return output }
    for y in radius..<(height - radius) {
        for x in radius..<(width - radius) {
            var survives = true
            for ny in (y - radius)...(y + radius) {
                for nx in (x - radius)...(x + radius) {
                    guard abs(nx - x) + abs(ny - y) <= radius else { continue }
                    guard input[ny * width + nx] == 0 else { continue }
                    survives = false
                    break
                }
                if !survives { break }
            }
            if survives { output[y * width + x] = 1 }
        }
    }
    return output
}

func closeSaberMask(_ mask: [UInt8], width: Int, height: Int, radius: Int = 1) -> [UInt8] {
    guard width > 0, height > 0, mask.count == width * height else { return [] }
    return binaryErode(binaryDilate(mask, width: width, height: height, radius: radius),
                       width: width, height: height, radius: radius)
}

func dilateSaberMask(_ mask: [UInt8], width: Int, height: Int, radius: Int) -> [UInt8] {
    guard width > 0, height > 0, mask.count == width * height else { return [] }
    return binaryDilate(mask, width: width, height: height, radius: radius)
}

/// Close fills short gaps in a glowing blade; open removes isolated colored noise.
func cleanSaberMask(_ mask: [UInt8], width: Int, height: Int, closeRadius: Int = 1,
                    openRadius: Int = 1) -> [UInt8] {
    guard width > 0, height > 0, mask.count == width * height else { return [] }
    let closed = closeSaberMask(mask, width: width, height: height, radius: closeRadius)
    return openSaberMask(closed, width: width, height: height, radius: openRadius)
}

/// Open a mask that has already been closed. The production detector needs
/// both the close-only and close+open variants, so accepting the intermediate
/// mask avoids repeating the same full-frame close pass.
func openSaberMask(_ mask: [UInt8], width: Int, height: Int, radius: Int = 1) -> [UInt8] {
    guard width > 0, height > 0, mask.count == width * height else { return [] }
    return binaryDilate(binaryErode(mask, width: width, height: height, radius: radius),
                        width: width, height: height, radius: radius)
}

struct SaberEligibilityDecision {
    let name: String
    let value: Double?
    let comparison: String?
    let threshold: Double?
}

final class SaberEndpointDiagnosticTrace {
    let centroidX: Double
    let centroidY: Double
    let bodyEndpoints: (PixelPoint, PixelPoint)?
    let minimumArea: Int
    let bodyPointCount: Int
    let establishedContinuousBody: Bool
    let denseTrimmedCoreLine: Bool
    let stronglyTrimmedCoreLine: Bool
    let diffusedBlueBody: Bool
    let gatingValues: [String: Double]

    init(centroidX: Double, centroidY: Double, bodyEndpoints: (PixelPoint, PixelPoint)?,
         minimumArea: Int, bodyPointCount: Int, establishedContinuousBody: Bool,
         denseTrimmedCoreLine: Bool, stronglyTrimmedCoreLine: Bool, diffusedBlueBody: Bool,
         gatingValues: [String: Double]) {
        self.centroidX = centroidX; self.centroidY = centroidY; self.bodyEndpoints = bodyEndpoints
        self.minimumArea = minimumArea; self.bodyPointCount = bodyPointCount
        self.establishedContinuousBody = establishedContinuousBody
        self.denseTrimmedCoreLine = denseTrimmedCoreLine
        self.stronglyTrimmedCoreLine = stronglyTrimmedCoreLine; self.diffusedBlueBody = diffusedBlueBody
        self.gatingValues = gatingValues
    }
}

struct SaberCandidate {
    var source: String = "color-mask"
    var radiance: Double = 0
    /// Untrimmed component axis used only for proposal de-duplication and
    /// ranking compatibility. `endpoints` are the physical body output.
    let comparisonEndpoints: (PixelPoint, PixelPoint)
    let endpoints: (PixelPoint, PixelPoint)
    let boundingBox: SaberBoundingBox
    var score: Double
    var scoreBreakdown: SaberScoreBreakdown
    var isEmitterEligible: Bool
    let isCompactRed: Bool
    let peakValue: Int
    let meanValue: Double
    let highValueRatio: Double
    let meanColorPurity: Double
    let clippedWhiteRatio: Double
    let brightnessVariation: Double
    let localContrast: Double
    let longitudinalHighCoverage: Double
    let widthVariation: Double
    let coreSupportRatio: Double
    let longitudinalCoreCoverage: Double
    let longitudinalContinuity: Double
    let largestLongitudinalGap: Int
    let retainedBodyRatio: Double
    /// Values already produced while estimating the component axis/body.
    /// Debug recording copies these; it must never recompute detection work.
    let rawPCASpan: Double
    let robustMainIntervalEndpoints: (PixelPoint, PixelPoint)?
    let robustMainIntervalLength: Double
    let axialDensity: Double
    let componentArea: Int
    let pointCount: Int
    let usedPointLEDFallback: Bool
    /// Populated only for Debug Recording, at the production rejection site.
    var diagnosticRejections: [SaberEligibilityDecision] = []
    var endpointDiagnosticTrace: SaberEndpointDiagnosticTrace? = nil
}

struct SaberBoundingBox {
    let minX: Int
    let minY: Int
    let maxX: Int
    let maxY: Int
}

struct SaberScoreBreakdown {
    var proposalPenalty: Double = 0
    var radiance: Double = 0
    let length: Double
    let aspect: Double
    let extent: Double
    let widthConsistency: Double
    let area: Double
    let peakBrightness: Double
    let meanBrightness: Double
    let highBrightnessRatio: Double
    let colorPurity: Double
    let localContrast: Double
    let emitterTexture: Double
    let clippedWhite: Double
    let longitudinalHighCoverage: Double
    let coreSupport: Double
    let longitudinalCoreCoverage: Double

    var total: Double {
        proposalPenalty + radiance + length + aspect + extent + widthConsistency + area
            + peakBrightness + meanBrightness + highBrightnessRatio
            + colorPurity + localContrast + emitterTexture + clippedWhite
            + longitudinalHighCoverage
            + coreSupport + longitudinalCoreCoverage
    }
}

struct SaberEvidence {
    let color: SaberColor
    var radiance: [UInt8] = []
    let value: [UInt8]
    let chroma: [UInt8]
    let colorMask: [UInt8]
    let coreMask: [UInt8]

    func isValid(width: Int, height: Int) -> Bool {
        let count = width * height
        return value.count == count && chroma.count == count
            && colorMask.count == count && coreMask.count == count
    }
}

/// DEBUG profiling accumulator supplied only when a caller explicitly asks
/// for a detailed detector profile. Keeping it nil leaves Release and normal
/// unprofiled detection without per-candidate clock reads.
final class SaberCandidateStageProfile {
    var connectedComponentsMs = 0.0
    var candidateScoringMs = 0.0
    var shapeAndAxisMs = 0.0
    var brightnessContrastColorMs = 0.0
    var endpointAndBoundsMs = 0.0
}

private func clamp01(_ value: Double) -> Double { min(max(value, 0), 1) }

private struct DominantLongitudinalBody {
    let points: [PixelPoint]
    let continuity: Double
    let largestGap: Int
    let retainedRatio: Double
    let intervalEndpoints: (PixelPoint, PixelPoint)?
    let intervalLength: Double
    let density: Double
}

/// Removes a low-density axial tail without imposing a fixed blade length.
/// A paper diffuser produces several transverse samples in most bins, whereas
/// morphology/Hough bridges and color spill are commonly only one sample wide.
/// Small gaps remain inside the selected interval and are therefore preserved.
private func dominantLongitudinalBody(
    in points: [PixelPoint],
    meanX: Double,
    meanY: Double,
    axis: (Double, Double)
) -> DominantLongitudinalBody {
    guard points.count >= 6 else {
        return DominantLongitudinalBody(points: points, continuity: 1,
                                        largestGap: 0, retainedRatio: 1,
                                        intervalEndpoints: nil,
                                        intervalLength: 0, density: 0)
    }
    let projections = points.map {
        (Double($0.x) - meanX) * axis.0 + (Double($0.y) - meanY) * axis.1
    }
    guard let minProjection = projections.min(), let maxProjection = projections.max() else {
        return DominantLongitudinalBody(points: points, continuity: 1,
                                        largestGap: 0, retainedRatio: 1,
                                        intervalEndpoints: nil,
                                        intervalLength: 0, density: 0)
    }
    let binCount = max(1, Int((maxProjection - minProjection).rounded(.up)) + 1)
    guard binCount >= 8 else {
        return DominantLongitudinalBody(points: points, continuity: 1,
                                        largestGap: 0, retainedRatio: 1,
                                        intervalEndpoints: nil,
                                        intervalLength: 0, density: 0)
    }
    var counts = Array(repeating: 0, count: binCount)
    var pointBins = Array(repeating: 0, count: points.count)
    for index in points.indices {
        let bin = min(binCount - 1,
                      max(0, Int((projections[index] - minProjection).rounded())))
        pointBins[index] = bin
        counts[bin] += 1
    }
    let peakCount = counts.max() ?? 0
    func intervalEndpoints(_ start: Int, _ end: Int) -> (PixelPoint, PixelPoint) {
        let firstProjection = minProjection + Double(start)
        let lastProjection = minProjection + Double(end)
        return (
            PixelPoint(x: Int((meanX + axis.0 * firstProjection).rounded()),
                       y: Int((meanY + axis.1 * firstProjection).rounded())),
            PixelPoint(x: Int((meanX + axis.0 * lastProjection).rounded()),
                       y: Int((meanY + axis.1 * lastProjection).rounded()))
        )
    }
    // One-pixel-wide genuine blades remain valid. Robust trimming activates
    // only when the candidate has a visibly wider continuous body.
    guard peakCount >= 4 else {
        let occupied = counts.filter { $0 > 0 }.count
        return DominantLongitudinalBody(
            points: points, continuity: Double(occupied) / Double(binCount),
            largestGap: largestZeroRun(in: counts, range: 0..<binCount), retainedRatio: 1,
            intervalEndpoints: intervalEndpoints(0, binCount - 1),
            intervalLength: Double(binCount),
            density: Double(points.count) / Double(binCount)
        )
    }
    let denseThreshold = max(2, Int((Double(peakCount) * 0.30).rounded(.up)))
    let denseBins = counts.indices.filter { counts[$0] >= denseThreshold }
    guard let firstDense = denseBins.first else {
        return DominantLongitudinalBody(points: points, continuity: 0,
                                        largestGap: binCount, retainedRatio: 1,
                                        intervalEndpoints: intervalEndpoints(0, binCount - 1),
                                        intervalLength: Double(binCount),
                                        density: Double(points.count) / Double(binCount))
    }
    let allowedInternalGap = 2
    var groups: [ClosedRange<Int>] = []
    var start = firstDense, previous = firstDense
    for bin in denseBins.dropFirst() {
        if bin - previous - 1 > allowedInternalGap {
            groups.append(start...previous)
            start = bin
        }
        previous = bin
    }
    groups.append(start...previous)

    let best = groups.max { lhs, rhs in
        func score(_ range: ClosedRange<Int>) -> Double {
            let support = counts[range].reduce(0, +)
            return Double(support) * sqrt(Double(range.count))
        }
        return score(lhs) < score(rhs)
    } ?? (firstDense...firstDense)
    // Restore rounded diffuser caps, but never absorb a long thin tail.
    let edgeAllowance = min(3, max(1, Int((Double(peakCount) * 0.22).rounded())))
    let bodyStart = max(0, best.lowerBound - edgeAllowance)
    let bodyEnd = min(binCount - 1, best.upperBound + edgeAllowance)
    let bodyRange = bodyStart...bodyEnd
    let bodyPoints = points.indices.compactMap { bodyRange.contains(pointBins[$0]) ? points[$0] : nil }
    guard bodyPoints.count >= 4 else {
        return DominantLongitudinalBody(points: points, continuity: 0,
                                        largestGap: binCount, retainedRatio: 1,
                                        intervalEndpoints: intervalEndpoints(0, binCount - 1),
                                        intervalLength: Double(binCount),
                                        density: Double(points.count) / Double(binCount))
    }
    let denseInBody = counts[bodyRange].filter { $0 >= denseThreshold }.count
    let continuity = Double(denseInBody) / Double(bodyRange.count)
    return DominantLongitudinalBody(
        points: bodyPoints,
        continuity: continuity,
        largestGap: largestZeroRun(in: counts, range: bodyStart..<(bodyEnd + 1)),
        retainedRatio: Double(bodyPoints.count) / Double(points.count),
        intervalEndpoints: intervalEndpoints(bodyStart, bodyEnd),
        intervalLength: Double(bodyRange.count),
        density: Double(bodyPoints.count) / Double(bodyRange.count)
    )
}

private func largestZeroRun(in counts: [Int], range: Range<Int>) -> Int {
    var current = 0, largest = 0
    for index in range {
        if counts[index] == 0 {
            current += 1
            largest = max(largest, current)
        } else {
            current = 0
        }
    }
    return largest
}

private func scoredSaberComponent(_ points: [PixelPoint], width: Int, height: Int,
                                  componentMask: [UInt8]?, componentIndices: Set<Int>? = nil,
                                  evidence: SaberEvidence?,
                                  source: String = "color-mask",
                                  stageProfile: SaberCandidateStageProfile? = nil,
                                  minimumAreaOverride: Int? = nil,
                                  collectEndpointDiagnostics: Bool = false) -> SaberCandidate? {
    let shapeStart = stageProfile == nil ? 0 : ProcessInfo.processInfo.systemUptime
    let standardMinimumArea = max(4, minimumAreaOverride
        ?? Int(Double(width * height) * 0.0005))
    // A nearby, foreshortened red blade can be compact at the sampled
    // resolution. Evaluate its existing emitter evidence before discarding it.
    let minimumArea = evidence?.color == .red ? min(standardMinimumArea, 20) : standardMinimumArea
    guard points.count >= minimumArea else { return nil }
    let frameArea = max(Double(width * height), 1)
    let areaRatio = Double(points.count) / frameArea
    guard areaRatio <= 0.22 else { return nil }

    let meanX = points.reduce(0.0) { $0 + Double($1.x) } / Double(points.count)
    let meanY = points.reduce(0.0) { $0 + Double($1.y) } / Double(points.count)
    var xx = 0.0, yy = 0.0, xy = 0.0
    for point in points {
        let dx = Double(point.x) - meanX
        let dy = Double(point.y) - meanY
        xx += dx * dx; yy += dy * dy; xy += dx * dy
    }
    let angle = 0.5 * atan2(2.0 * xy, xx - yy)
    let axis = (cos(angle), sin(angle))
    let normal = (-axis.1, axis.0)
    var minMajor = Double.greatestFiniteMagnitude
    var maxMajor = -Double.greatestFiniteMagnitude
    var minMinor = Double.greatestFiniteMagnitude
    var maxMinor = -Double.greatestFiniteMagnitude
    for point in points {
        let dx = Double(point.x) - meanX
        let dy = Double(point.y) - meanY
        let major = dx * axis.0 + dy * axis.1
        let minor = dx * normal.0 + dy * normal.1
        minMajor = min(minMajor, major); maxMajor = max(maxMajor, major)
        minMinor = min(minMinor, minor); maxMinor = max(maxMinor, minor)
    }
    let majorLength = maxMajor - minMajor + 1.0
    let minorLength = maxMinor - minMinor + 1.0
    let aspect = majorLength / max(minorLength, 1.0)
    let isCompactRed = evidence?.color == .red
        && (points.count < standardMinimumArea || aspect < 1.5)
    let extent = Double(points.count) / max(majorLength * minorLength, 1.0)
    guard majorLength >= max(4.0, Double(min(width, height)) * 0.025),
          aspect >= (evidence?.color == .red ? 1.0 : 1.5),
          extent >= 0.10 else { return nil }
    // The additional longitudinal histogram is only useful for candidates
    // that already pass the cheap geometric gate.
    let body = dominantLongitudinalBody(in: points, meanX: meanX, meanY: meanY, axis: axis)

    let binCount = max(4, min(12, Int(majorLength.rounded(.up))))
    var binMin = Array(repeating: Double.greatestFiniteMagnitude, count: binCount)
    var binMax = Array(repeating: -Double.greatestFiniteMagnitude, count: binCount)
    for point in points {
        let dx = Double(point.x) - meanX
        let dy = Double(point.y) - meanY
        let major = dx * axis.0 + dy * axis.1
        let minor = dx * normal.0 + dy * normal.1
        let normalized = clamp01((major - minMajor) / max(maxMajor - minMajor, 1.0))
        let bin = min(binCount - 1, Int(normalized * Double(binCount)))
        binMin[bin] = min(binMin[bin], minor)
        binMax[bin] = max(binMax[bin], minor)
    }
    let widths = binMin.indices.compactMap { index -> Double? in
        // Empty bins use finite sentinels; isFinite alone admitted them and
        // overflowed the variance to NaN, violating the score sort ordering.
        guard binMax[index] >= binMin[index] else { return nil }
        return binMax[index] - binMin[index] + 1.0
    }
    let meanWidth = widths.reduce(0, +) / Double(max(widths.count, 1))
    let widthVariance = widths.reduce(0) { $0 + pow($1 - meanWidth, 2) } / Double(max(widths.count, 1))
    let widthVariation = sqrt(widthVariance) / max(meanWidth, 1.0)

    if let stageProfile {
        stageProfile.shapeAndAxisMs += (ProcessInfo.processInfo.systemUptime - shapeStart) * 1000
    }
    let evidenceStart = stageProfile == nil ? 0 : ProcessInfo.processInfo.systemUptime

    var lightScore = 0.0
    var emitterScore = 0.0
    var peakValue = 0
    var meanValue = 0.0
    var highRatio = 0.0
    var meanPurity = 0.0
    var clippedRatio = 0.0
    var brightnessVariation = 0.0
    var localContrast = 0.0
    var emitterTexture = 0.0
    var longitudinalHighCoverage = 0.0
    var coreSupportRatio = 0.0
    var longitudinalCoreCoverage = 0.0
    var isEmitterEligible = evidence == nil
    var radianceSum = 0.0
    if let evidence, evidence.isValid(width: width, height: height) {
        var rawCount = 0
        var valueSum = 0.0
        var valueSquareSum = 0.0
        var puritySum = 0.0
        var measuredPeakValue = 0
        var highCount = 0
        var clippedWhiteIndices = Set<Int>()
        var outsideValueSum = 0.0
        var outsideCount = 0
        var highAxisBins = Array(repeating: false, count: binCount)
        var coreAxisBins = Array(repeating: false, count: binCount)
        var coreCount = 0
        let neighborOffsets = [(-2, 0), (2, 0), (0, -2), (0, 2)]

        for point in points {
            let index = point.y * width + point.x
            if evidence.radiance.count == evidence.value.count {
                radianceSum += pow(Double(evidence.radiance[index]) / 255.0, 2)
            }
            let value = Int(evidence.value[index])
            let chroma = Int(evidence.chroma[index])
            if evidence.coreMask[index] != 0 {
                coreCount += 1
                let dx = Double(point.x) - meanX
                let dy = Double(point.y) - meanY
                let major = dx * axis.0 + dy * axis.1
                let normalized = clamp01((major - minMajor) / max(maxMajor - minMajor, 1.0))
                coreAxisBins[min(binCount - 1, Int(normalized * Double(binCount)))] = true
            }
            if evidence.colorMask[index] != 0 {
                rawCount += 1
                valueSum += Double(value)
                valueSquareSum += Double(value * value)
                puritySum += Double(chroma) / Double(max(value, 1))
                measuredPeakValue = max(measuredPeakValue, value)
                if value >= 220 {
                    highCount += 1
                    let dx = Double(point.x) - meanX
                    let dy = Double(point.y) - meanY
                    let major = dx * axis.0 + dy * axis.1
                    let normalized = clamp01((major - minMajor) / max(maxMajor - minMajor, 1.0))
                    highAxisBins[min(binCount - 1, Int(normalized * Double(binCount)))] = true
                }
            } else if value >= 245 && chroma <= 38 {
                // A clipped LED becomes white and falls outside the color mask.
                // close can bridge it back into the selected component.
                clippedWhiteIndices.insert(index)
                let dx = Double(point.x) - meanX
                let dy = Double(point.y) - meanY
                let major = dx * axis.0 + dy * axis.1
                let normalized = clamp01((major - minMajor) / max(maxMajor - minMajor, 1.0))
                highAxisBins[min(binCount - 1, Int(normalized * Double(binCount)))] = true
            }
            // White-clipped LED centers often sit immediately beside, rather
            // than inside, the HSV color component. Count each nearby sample
            // once so a diffuse reflection cannot gain simply from its area.
            for dy in -2...2 {
                for dx in -2...2 where abs(dx) + abs(dy) <= 2 {
                    let nx = point.x + dx, ny = point.y + dy
                    guard nx >= 0, nx < width, ny >= 0, ny < height else { continue }
                    let neighbor = ny * width + nx
                    if evidence.colorMask[neighbor] == 0,
                       evidence.value[neighbor] >= 245,
                       evidence.chroma[neighbor] <= 45 {
                        clippedWhiteIndices.insert(neighbor)
                    }
                }
            }
            for (dx, dy) in neighborOffsets {
                let nx = point.x + dx, ny = point.y + dy
                guard nx >= 0, nx < width, ny >= 0, ny < height else { continue }
                let neighbor = ny * width + nx
                let belongsToComponent = componentMask.map { $0[neighbor] != 0 }
                    ?? componentIndices?.contains(neighbor) ?? false
                guard !belongsToComponent else { continue }
                outsideValueSum += Double(evidence.value[neighbor])
                outsideCount += 1
            }
        }
        guard rawCount >= 3 else { return nil }
        meanValue = valueSum / Double(rawCount)
        highRatio = Double(highCount) / Double(rawCount)
        meanPurity = puritySum / Double(rawCount)
        peakValue = measuredPeakValue
        let variance = max(0, valueSquareSum / Double(rawCount) - meanValue * meanValue)
        brightnessVariation = clamp01(sqrt(variance) / 55.0)
        // LED packages create a moderately modulated intensity profile (bright
        // emitters separated by darker gaps). A smooth reflection has too
        // little variation, while a broad mixed background component has too
        // much. Reward the useful band rather than merely preferring variance.
        emitterTexture = clamp01((brightnessVariation - 0.22) / 0.10)
            * clamp01((0.65 - brightnessVariation) / 0.20)
        let outsideMean = outsideCount > 0 ? outsideValueSum / Double(outsideCount) : 0
        localContrast = clamp01((meanValue - outsideMean) / 100.0)
        longitudinalHighCoverage = Double(highAxisBins.filter { $0 }.count) / Double(binCount)
        coreSupportRatio = Double(coreCount) / Double(max(points.count, 1))
        longitudinalCoreCoverage = Double(coreAxisBins.filter { $0 }.count) / Double(binCount)
        let clippedWhiteCount = clippedWhiteIndices.count
        clippedRatio = min(Double(clippedWhiteCount) / Double(rawCount + clippedWhiteCount), 0.25) / 0.25
        emitterScore = clamp01((Double(peakValue) - 200.0) / 55.0) * 0.32
            + clamp01((meanValue - 160.0) / 95.0) * 0.23
            + highRatio * 0.28
            + meanPurity * 0.12
            + clippedRatio * 0.05
        let hasEmitterCore = highRatio >= 0.08
            || (peakValue >= 242 && meanValue >= 190)
            || clippedWhiteCount > 0
        // This is deliberately candidate evidence rather than a tighter HSV
        // mask: moderately bright reflections may form candidates, but cannot
        // win without a concentrated LED-like emitter core.
        isEmitterEligible = peakValue >= 218 && hasEmitterCore && emitterScore >= 0.42
        if isCompactRed {
            isEmitterEligible = isEmitterEligible && peakValue >= 230
                && highRatio >= 0.50 && meanPurity >= 0.50
        }
        lightScore = emitterScore * 38.0 + highRatio * 14.0 + meanPurity * 6.0
            + localContrast * 8.0 + emitterTexture * 13.0 + clippedRatio * 8.0
            + longitudinalHighCoverage * 3.0
            + coreSupportRatio * 12.0 + longitudinalCoreCoverage * 8.0
    }

    if let stageProfile {
        stageProfile.brightnessContrastColorMs += (ProcessInfo.processInfo.systemUptime - evidenceStart) * 1000
    }
    let endpointStart = stageProfile == nil ? 0 : ProcessInfo.processInfo.systemUptime

    // Keep ranking evidence based on the full connected candidate so the
    // established real-image ordering remains unchanged. Endpoints alone use
    // the dense continuous body, preventing a thin spill tail or a remote
    // reflection from becoming a min/max projection extreme.
    let rawEndpoints = (
        PixelPoint(x: Int((meanX + axis.0 * minMajor).rounded()),
                   y: Int((meanY + axis.1 * minMajor).rounded())),
        PixelPoint(x: Int((meanX + axis.0 * maxMajor).rounded()),
                   y: Int((meanY + axis.1 * maxMajor).rounded()))
    )
    let finalEndpoints: (PixelPoint, PixelPoint)
    var adoptedBodyEndpoints: (PixelPoint, PixelPoint)?
    let usedPointLEDFallback: Bool
    // A fragmented core-line can have a numerically continuous *local* body
    // while the discarded raw samples are still the real point-LED blade.
    // Therefore continuity alone is sufficient only for connected/color
    // candidates; line proposals need the stronger support checks below.
    let hasEstablishedContinuousBody = source != "core-line"
        && body.continuity >= EndpointSelectionThresholds.connectedBodyContinuity
    // Core-line proposals may contain a long bridge between unrelated emitters.
    // A paper diffuser's retained body still has several samples per axial bin;
    // fragmented point LEDs and sparse proposals continue to use the raw axis.
    let hasDenseTrimmedCoreLine = source == "core-line"
        && body.largestGap <= EndpointSelectionThresholds.coreLineMaximumGap
        && body.density >= EndpointSelectionThresholds.denseCoreLineDensity
    let hasStronglyTrimmedSupportedCoreLine = source == "core-line"
        && body.retainedRatio <= EndpointSelectionThresholds.stronglyTrimmedRetainedRatio
        && body.largestGap <= EndpointSelectionThresholds.coreLineMaximumGap
        && body.points.count >= minimumArea
    let hasDiffusedBlueCoreLineBody = evidence?.color == .blue
        && source == "core-line"
        && body.retainedRatio < EndpointSelectionThresholds.diffuserRetainedRatio
        && body.continuity >= EndpointSelectionThresholds.diffuserContinuity
        && body.largestGap <= EndpointSelectionThresholds.coreLineMaximumGap
        && body.points.count >= minimumArea
        && meanPurity >= EndpointSelectionThresholds.diffuserColorPurity
        && coreSupportRatio >= EndpointSelectionThresholds.diffuserCoreSupport
    if body.points.count >= minimumArea,
       body.retainedRatio < 0.85,
       (hasEstablishedContinuousBody
        || hasDenseTrimmedCoreLine
        || hasStronglyTrimmedSupportedCoreLine
        || hasDiffusedBlueCoreLineBody),
       let bodyEndpoints = principalAxisEndpoints(body.points) {
        finalEndpoints = bodyEndpoints
        adoptedBodyEndpoints = bodyEndpoints
        usedPointLEDFallback = false
    } else {
        // Disconnected legacy LED packages and mild edge-density changes are
        // not paper-diffused continuous bodies; keep their established axis.
        finalEndpoints = rawEndpoints
        usedPointLEDFallback = true
    }
    let first = finalEndpoints.0
    let second = finalEndpoints.1
    var boxMinX = Int.max, boxMinY = Int.max, boxMaxX = Int.min, boxMaxY = Int.min
    for point in points {
        boxMinX = min(boxMinX, point.x); boxMinY = min(boxMinY, point.y)
        boxMaxX = max(boxMaxX, point.x); boxMaxY = max(boxMaxY, point.y)
    }
    let boundingBox = SaberBoundingBox(minX: boxMinX, minY: boxMinY,
                                       maxX: boxMaxX, maxY: boxMaxY)
    let frameDiagonal = hypot(Double(width), Double(height))
    let peakNormalized = clamp01((Double(peakValue) - 200.0) / 55.0)
    let meanNormalized = clamp01((meanValue - 160.0) / 95.0)
    let normalizedCoreSupport = clamp01(coreSupportRatio)
    let contrastWeight = 8.0 + normalizedCoreSupport * 16.0
    let textureWeight = 13.0 * (1.0 - normalizedCoreSupport)
    let bladeLengthSupport = clamp01(
        (majorLength / Double(max(min(width, height), 1)) - 0.08) / 0.22
    )
    let breakdown = SaberScoreBreakdown(
        radiance: radianceSum / Double(points.count) * 65.0 * bladeLengthSupport,
        length: min(majorLength / max(frameDiagonal, 1.0), 1.0) * 8.0,
        aspect: min(log2(max(aspect, 1.0)), 4.0) * 2.0,
        extent: extent * 2.0,
        widthConsistency: -min(widthVariation, 2.0) * 4.0,
        area: -areaRatio * 8.0,
        peakBrightness: peakNormalized * 0.32 * 38.0,
        meanBrightness: meanNormalized * 0.23 * 38.0,
        highBrightnessRatio: highRatio * (0.28 * 38.0 + 14.0),
        colorPurity: meanPurity * (0.12 * 38.0 + 6.0),
        localContrast: localContrast * contrastWeight,
        emitterTexture: emitterTexture * textureWeight,
        // A clipped white/cyan center is uncommon in a diffuse reflection but
        // expected from an exposed LED package. Keep it as a soft bonus: a
        // blade without clipping can still win on its other emitter evidence.
        clippedWhite: clippedRatio * (0.05 * 38.0 + 72.0) * bladeLengthSupport,
        longitudinalHighCoverage: longitudinalHighCoverage * 3.0,
        coreSupport: coreSupportRatio * 12.0 * bladeLengthSupport,
        longitudinalCoreCoverage: longitudinalCoreCoverage * 8.0 * bladeLengthSupport
    )
    let score = evidence == nil ? lightScore
        + min(majorLength / max(frameDiagonal, 1.0), 1.0) * 8.0
        + min(log2(max(aspect, 1.0)), 4.0) * 2.0
        + extent * 2.0 - min(widthVariation, 2.0) * 4.0 - areaRatio * 8.0
        : breakdown.total
    if let stageProfile {
        stageProfile.endpointAndBoundsMs += (ProcessInfo.processInfo.systemUptime - endpointStart) * 1000
    }
    var candidate = SaberCandidate(source: source,
                          radiance: radianceSum / Double(points.count),
                          comparisonEndpoints: rawEndpoints,
                          endpoints: (first, second), boundingBox: boundingBox, score: score,
                          scoreBreakdown: breakdown,
                          isEmitterEligible: isEmitterEligible,
                          isCompactRed: isCompactRed,
                          peakValue: peakValue, meanValue: meanValue,
                          highValueRatio: highRatio, meanColorPurity: meanPurity,
                          clippedWhiteRatio: clippedRatio,
                          brightnessVariation: brightnessVariation,
                          localContrast: localContrast,
                          longitudinalHighCoverage: longitudinalHighCoverage,
                          widthVariation: widthVariation,
                          coreSupportRatio: coreSupportRatio,
                          longitudinalCoreCoverage: longitudinalCoreCoverage,
                          longitudinalContinuity: body.continuity,
                          largestLongitudinalGap: body.largestGap,
                          retainedBodyRatio: body.retainedRatio,
                          rawPCASpan: majorLength,
                          robustMainIntervalEndpoints: body.intervalEndpoints,
                          robustMainIntervalLength: body.intervalLength,
                          axialDensity: body.density,
                          componentArea: points.count,
                          pointCount: points.count,
                          usedPointLEDFallback: usedPointLEDFallback)
    if collectEndpointDiagnostics {
        candidate.endpointDiagnosticTrace = SaberEndpointDiagnosticTrace(
            centroidX: meanX, centroidY: meanY,
            bodyEndpoints: adoptedBodyEndpoints,
            minimumArea: minimumArea, bodyPointCount: body.points.count,
            establishedContinuousBody: hasEstablishedContinuousBody,
            denseTrimmedCoreLine: hasDenseTrimmedCoreLine,
            stronglyTrimmedCoreLine: hasStronglyTrimmedSupportedCoreLine,
            diffusedBlueBody: hasDiffusedBlueCoreLineBody,
            gatingValues: ["bodyPointCount": Double(body.points.count), "minimumArea": Double(minimumArea),
                "retainedBodyRatio": body.retainedRatio, "retainedBodyLimit": 0.85,
                "continuity": body.continuity, "connectedBodyContinuityThreshold": EndpointSelectionThresholds.connectedBodyContinuity,
                "largestGap": Double(body.largestGap), "coreLineMaximumGapThreshold": Double(EndpointSelectionThresholds.coreLineMaximumGap),
                "density": body.density, "denseCoreLineDensityThreshold": EndpointSelectionThresholds.denseCoreLineDensity,
                "stronglyTrimmedRetainedRatioThreshold": EndpointSelectionThresholds.stronglyTrimmedRetainedRatio,
                "diffuserRetainedRatioThreshold": EndpointSelectionThresholds.diffuserRetainedRatio,
                "diffuserContinuityThreshold": EndpointSelectionThresholds.diffuserContinuity,
                "colorPurity": meanPurity, "diffuserColorPurityThreshold": EndpointSelectionThresholds.diffuserColorPurity,
                "coreSupport": coreSupportRatio, "diffuserCoreSupportThreshold": EndpointSelectionThresholds.diffuserCoreSupport])
    }
    return candidate
}

/// Returns every shape-valid component so fixture tests can compare the chosen
/// emitter with rejected reflections using the same production score.
func saberCandidates(in mask: [UInt8], width: Int, height: Int,
                     evidence: SaberEvidence? = nil,
                     stageProfile: SaberCandidateStageProfile? = nil,
                     componentObserver: ((Int) -> Void)? = nil,
                     minimumAreaOverride: Int? = nil,
                     collectEndpointDiagnostics: Bool = false) -> [SaberCandidate] {
    guard width > 0, height > 0, mask.count == width * height else { return [] }
    var remaining = mask
    var candidates: [SaberCandidate] = []
    var queue: [Int] = []
    for seed in remaining.indices where remaining[seed] != 0 {
        let traversalStart = stageProfile == nil ? 0 : ProcessInfo.processInfo.systemUptime
        remaining[seed] = 0
        queue.removeAll(keepingCapacity: true)
        queue.append(seed)
        var head = 0
        var points: [PixelPoint] = []
        while head < queue.count {
            let value = queue[head]; head += 1
            let x = value % width, y = value / width
            points.append(PixelPoint(x: x, y: y))
            for ny in max(0, y - 1)...min(height - 1, y + 1) {
                for nx in max(0, x - 1)...min(width - 1, x + 1) {
                    let neighbor = ny * width + nx
                    if remaining[neighbor] != 0 {
                        remaining[neighbor] = 0
                        queue.append(neighbor)
                    }
                }
            }
        }
        if let stageProfile {
            stageProfile.connectedComponentsMs +=
                (ProcessInfo.processInfo.systemUptime - traversalStart) * 1000
        }
        componentObserver?(points.count)
        let scoreStart = stageProfile == nil ? 0 : ProcessInfo.processInfo.systemUptime
        let candidate = scoredSaberComponent(points, width: width, height: height,
                                             componentMask: mask, evidence: evidence,
                                             stageProfile: stageProfile,
                                             minimumAreaOverride: minimumAreaOverride,
                                             collectEndpointDiagnostics: collectEndpointDiagnostics)
        if let stageProfile {
            stageProfile.candidateScoringMs +=
                (ProcessInfo.processInfo.systemUptime - scoreStart) * 1000
        }
        guard let candidate else { continue }
        candidates.append(candidate)
    }
    return candidates.sorted { $0.score > $1.score }
}

/// Scores an already-connected compact proposal without allocating and
/// rescanning a full-frame mask. Used by bright-core line proposals only.
func saberCandidate(from points: [PixelPoint], width: Int, height: Int,
                    evidence: SaberEvidence? = nil,
                    source: String = "color-mask",
                    stageProfile: SaberCandidateStageProfile? = nil,
                    collectEndpointDiagnostics: Bool = false) -> SaberCandidate? {
    let uniqueIndices = Set(points.compactMap { point -> Int? in
        guard point.x >= 0, point.x < width, point.y >= 0, point.y < height else { return nil }
        return point.y * width + point.x
    })
    let uniquePoints = uniqueIndices.map { PixelPoint(x: $0 % width, y: $0 / width) }
    return scoredSaberComponent(uniquePoints, width: width, height: height,
                                componentMask: nil, componentIndices: uniqueIndices,
                                evidence: evidence, source: source,
                                stageProfile: stageProfile,
                                collectEndpointDiagnostics: collectEndpointDiagnostics)
}

/// Select one elongated external component, rather than simply the largest color patch.
func saberEndpoints(in mask: [UInt8], width: Int, height: Int,
                    evidence: SaberEvidence? = nil) -> (PixelPoint, PixelPoint)? {
    saberCandidates(in: mask, width: width, height: height, evidence: evidence)
        .first(where: { $0.isEmitterEligible })?.endpoints
}

func bestColorCluster(in points: [PixelPoint], width: Int, height: Int, connectionRadius: Int = 1) -> [PixelPoint] {
    guard width > 0, height > 0, !points.isEmpty else { return [] }
    let radius = max(connectionRadius, 1)
    var remaining = Set(points.compactMap { point -> Int? in
        guard point.x >= 0, point.x < width, point.y >= 0, point.y < height else { return nil }
        return point.y * width + point.x
    })
    var candidates: [[PixelPoint]] = []

    while let seed = remaining.first {
        remaining.remove(seed)
        var queue = [seed]
        var component: [PixelPoint] = []
        while let value = queue.popLast() {
            let point = PixelPoint(x: value % width, y: value / width)
            component.append(point)
            for dy in -radius...radius {
                for dx in -radius...radius where !(dx == 0 && dy == 0) {
                    let nx = point.x + dx
                    let ny = point.y + dy
                    guard nx >= 0, nx < width, ny >= 0, ny < height else { continue }
                    if remaining.remove(ny * width + nx) != nil {
                        queue.append(ny * width + nx)
                    }
                }
            }
        }
        candidates.append(component)
    }

    let minimumLength = max(4.0, Double(min(width, height)) * 0.025)
    return candidates.compactMap { component -> (Double, [PixelPoint])? in
        guard component.count >= 4, let endpoints = principalAxisEndpoints(component) else { return nil }
        let dx = Double(endpoints.1.x - endpoints.0.x)
        let dy = Double(endpoints.1.y - endpoints.0.y)
        let majorLength = hypot(dx, dy)
        guard majorLength >= minimumLength else { return nil }

        let axis = (dx / max(majorLength, 1.0), dy / max(majorLength, 1.0))
        let normal = (-axis.1, axis.0)
        let normalValues = component.map { Double($0.x) * normal.0 + Double($0.y) * normal.1 }
        let minorLength = (normalValues.max() ?? 0) - (normalValues.min() ?? 0)
        let aspect = majorLength / max(minorLength, 2.0)
        guard aspect >= 1.35 else { return nil }
        let score = majorLength * sqrt(min(aspect, 12.0)) * log(Double(component.count) + 1.0)
        return (score, component)
    }.sorted { lhs, rhs in
        if abs(lhs.0 - rhs.0) > 0.0001 { return lhs.0 > rhs.0 }
        if lhs.1.count != rhs.1.count { return lhs.1.count > rhs.1.count }
        return (lhs.1.map { $0.y * width + $0.x }.min() ?? 0) < (rhs.1.map { $0.y * width + $0.x }.min() ?? 0)
    }.first?.1 ?? []
}

func largestColorCluster(in points: [PixelPoint], width: Int, height: Int) -> [PixelPoint] {
    guard width > 0, height > 0 else { return [] }
    var remaining = Set(points.compactMap { point -> Int? in
        guard point.x >= 0, point.x < width, point.y >= 0, point.y < height else { return nil }
        return point.y * width + point.x
    })
    var largest: [PixelPoint] = []
    while let seed = remaining.first {
        remaining.remove(seed)
        var queue = [seed]
        var component: [PixelPoint] = []
        while let value = queue.popLast() {
            let point = PixelPoint(x: value % width, y: value / width)
            component.append(point)
            for dy in -1...1 {
                for dx in -1...1 where !(dx == 0 && dy == 0) {
                    let nx = point.x + dx, ny = point.y + dy
                    guard nx >= 0, nx < width, ny >= 0, ny < height else { continue }
                    if remaining.remove(ny * width + nx) != nil { queue.append(ny * width + nx) }
                }
            }
        }
        if component.count > largest.count { largest = component }
    }
    return largest
}

// Source x increases to the right and y increases downward; the centered crop
// is subtracted after scaling to match SwiftUI's resizeAspectFill coordinates.
func aspectFillPoint(_ point: PixelPoint, source: (width: Int, height: Int), view: (width: Double, height: Double)) -> CGPoint {
    guard source.width > 0, source.height > 0, view.width > 0, view.height > 0 else { return .zero }
    let scale = max(view.width / Double(source.width), view.height / Double(source.height))
    let displayedWidth = Double(source.width) * scale
    let displayedHeight = Double(source.height) * scale
    return CGPoint(x: (Double(point.x) * scale) - (displayedWidth - view.width) / 2,
                   y: (Double(point.y) * scale) - (displayedHeight - view.height) / 2)
}

func scaledPoint(_ point: PixelPoint, from source: (width: Int, height: Int), to output: (width: Int, height: Int), mirrorX: Bool, mirrorY: Bool) -> PixelPoint {
    let x = Double(point.x) / Double(max(source.width - 1, 1)) * Double(max(output.width - 1, 0))
    let y = Double(point.y) / Double(max(source.height - 1, 1)) * Double(max(output.height - 1, 0))
    return PixelPoint(x: Int((mirrorX ? Double(output.width - 1) - x : x).rounded()),
                      y: Int((mirrorY ? Double(output.height - 1) - y : y).rounded()))
}

func payload(for endpoints: (PixelPoint, PixelPoint), source: (width: Int, height: Int), output: (width: Int, height: Int), mirrorX: Bool, mirrorY: Bool) -> String {
    let p1 = scaledPoint(endpoints.0, from: source, to: output, mirrorX: mirrorX, mirrorY: mirrorY)
    let p2 = scaledPoint(endpoints.1, from: source, to: output, mirrorX: mirrorX, mirrorY: mirrorY)
    return "\(p1.x),\(p1.y),\(p2.x),\(p2.y)"
}

func principalAxisEndpoints(_ points: [PixelPoint]) -> (PixelPoint, PixelPoint)? {
    guard points.count >= 2 else { return points.first.map { ($0, $0) } }
    let meanX = points.reduce(0.0) { $0 + Double($1.x) } / Double(points.count)
    let meanY = points.reduce(0.0) { $0 + Double($1.y) } / Double(points.count)
    let xx = points.reduce(0.0) { $0 + (Double($1.x) - meanX) * (Double($1.x) - meanX) }
    let yy = points.reduce(0.0) { $0 + (Double($1.y) - meanY) * (Double($1.y) - meanY) }
    let xy = points.reduce(0.0) { $0 + (Double($1.x) - meanX) * (Double($1.y) - meanY) }
    let angle = 0.5 * atan2(2.0 * xy, xx - yy)
    let axis = (cos(angle), sin(angle))
    let projections = points.map { Double($0.x) * axis.0 + Double($0.y) * axis.1 }
    guard let minValue = projections.min(), let maxValue = projections.max() else { return nil }
    let minIndex = projections.firstIndex(of: minValue) ?? 0
    let maxIndex = projections.firstIndex(of: maxValue) ?? 0
    return (points[minIndex], points[maxIndex])
}

func timestampedPayload(_ coordinates: String, timestamp: TimeInterval = Date().timeIntervalSince1970) -> String {
    "ts=\(String(format: "%.6f", timestamp));\(coordinates)"
}
