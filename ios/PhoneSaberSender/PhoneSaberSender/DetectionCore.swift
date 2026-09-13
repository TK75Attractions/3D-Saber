import Foundation
import CoreGraphics

struct PixelPoint: Equatable {
    let x: Int
    let y: Int
}

struct ColorThreshold: Equatable {
    var brightness: UInt8 = 145
    var dominance: UInt8 = 25
    var saturation: UInt8 = 30
}

enum SaberColor: Hashable {
    case red
    case blue
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
        return hsv.hue >= 340.0 || hsv.hue <= 20.0
    case .blue:
        return hsv.hue >= 198.0 && hsv.hue <= 248.0
    }
}

func matchesSaberHSV(_ red: UInt8, _ green: UInt8, _ blue: UInt8,
                     color: SaberColor, threshold: ColorThreshold) -> Bool {
    matchesSaberHSV(saberHSV(red, green, blue), color: color, threshold: threshold)
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
    return binaryDilate(binaryErode(closed, width: width, height: height, radius: openRadius),
                        width: width, height: height, radius: openRadius)
}

struct SaberCandidate {
    let endpoints: (PixelPoint, PixelPoint)
    let boundingBox: SaberBoundingBox
    let score: Double
    let scoreBreakdown: SaberScoreBreakdown
    let isEmitterEligible: Bool
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
}

struct SaberBoundingBox {
    let minX: Int
    let minY: Int
    let maxX: Int
    let maxY: Int
}

struct SaberScoreBreakdown {
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
        length + aspect + extent + widthConsistency + area
            + peakBrightness + meanBrightness + highBrightnessRatio
            + colorPurity + localContrast + emitterTexture + clippedWhite
            + longitudinalHighCoverage
            + coreSupport + longitudinalCoreCoverage
    }
}

struct SaberEvidence {
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

private func clamp01(_ value: Double) -> Double { min(max(value, 0), 1) }

private func scoredSaberComponent(_ points: [PixelPoint], width: Int, height: Int,
                                  componentMask: [UInt8]?, componentIndices: Set<Int>? = nil,
                                  evidence: SaberEvidence?) -> SaberCandidate? {
    let minimumArea = max(4, Int(Double(width * height) * 0.0005))
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
    let extent = Double(points.count) / max(majorLength * minorLength, 1.0)
    guard majorLength >= max(4.0, Double(min(width, height)) * 0.025),
          aspect >= 1.5, extent >= 0.10 else { return nil }

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
        guard binMin[index].isFinite, binMax[index].isFinite else { return nil }
        return binMax[index] - binMin[index] + 1.0
    }
    let meanWidth = widths.reduce(0, +) / Double(max(widths.count, 1))
    let widthVariance = widths.reduce(0) { $0 + pow($1 - meanWidth, 2) } / Double(max(widths.count, 1))
    let widthVariation = sqrt(widthVariance) / max(meanWidth, 1.0)

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
        lightScore = emitterScore * 38.0 + highRatio * 14.0 + meanPurity * 6.0
            + localContrast * 8.0 + emitterTexture * 13.0 + clippedRatio * 8.0
            + longitudinalHighCoverage * 3.0
            + coreSupportRatio * 12.0 + longitudinalCoreCoverage * 8.0
    }

    // The endpoints are the long-axis ends of the oriented component box,
    // equivalent to the long side of camera.py's minAreaRect.
    let first = PixelPoint(x: Int((meanX + axis.0 * minMajor).rounded()),
                           y: Int((meanY + axis.1 * minMajor).rounded()))
    let second = PixelPoint(x: Int((meanX + axis.0 * maxMajor).rounded()),
                            y: Int((meanY + axis.1 * maxMajor).rounded()))
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
        clippedWhite: clippedRatio * (0.05 * 38.0 + 72.0),
        longitudinalHighCoverage: longitudinalHighCoverage * 3.0,
        coreSupport: coreSupportRatio * 12.0 * bladeLengthSupport,
        longitudinalCoreCoverage: longitudinalCoreCoverage * 8.0 * bladeLengthSupport
    )
    let score = evidence == nil ? lightScore
        + min(majorLength / max(frameDiagonal, 1.0), 1.0) * 8.0
        + min(log2(max(aspect, 1.0)), 4.0) * 2.0
        + extent * 2.0 - min(widthVariation, 2.0) * 4.0 - areaRatio * 8.0
        : breakdown.total
    return SaberCandidate(endpoints: (first, second), boundingBox: boundingBox, score: score,
                          scoreBreakdown: breakdown,
                          isEmitterEligible: isEmitterEligible,
                          peakValue: peakValue, meanValue: meanValue,
                          highValueRatio: highRatio, meanColorPurity: meanPurity,
                          clippedWhiteRatio: clippedRatio,
                          brightnessVariation: brightnessVariation,
                          localContrast: localContrast,
                          longitudinalHighCoverage: longitudinalHighCoverage,
                          widthVariation: widthVariation,
                          coreSupportRatio: coreSupportRatio,
                          longitudinalCoreCoverage: longitudinalCoreCoverage)
}

/// Returns every shape-valid component so fixture tests can compare the chosen
/// emitter with rejected reflections using the same production score.
func saberCandidates(in mask: [UInt8], width: Int, height: Int,
                     evidence: SaberEvidence? = nil) -> [SaberCandidate] {
    guard width > 0, height > 0, mask.count == width * height else { return [] }
    var remaining = mask
    var candidates: [SaberCandidate] = []
    var queue: [Int] = []
    for seed in remaining.indices where remaining[seed] != 0 {
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
        guard let candidate = scoredSaberComponent(points, width: width, height: height,
                                                   componentMask: mask, evidence: evidence) else { continue }
        candidates.append(candidate)
    }
    return candidates.sorted { $0.score > $1.score }
}

/// Scores an already-connected compact proposal without allocating and
/// rescanning a full-frame mask. Used by bright-core line proposals only.
func saberCandidate(from points: [PixelPoint], width: Int, height: Int,
                    evidence: SaberEvidence? = nil) -> SaberCandidate? {
    let uniqueIndices = Set(points.compactMap { point -> Int? in
        guard point.x >= 0, point.x < width, point.y >= 0, point.y < height else { return nil }
        return point.y * width + point.x
    })
    let uniquePoints = uniqueIndices.map { PixelPoint(x: $0 % width, y: $0 / width) }
    return scoredSaberComponent(uniquePoints, width: width, height: height,
                                componentMask: nil, componentIndices: uniqueIndices,
                                evidence: evidence)
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
