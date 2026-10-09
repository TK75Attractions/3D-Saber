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
    guard !strictMask.isEmpty else { return strictMask }
    let support = dilateSaberMask(strictMask, width: width, height: height, radius: radius)
    var result = Array(repeating: UInt8(0), count: strictMask.count)
    strictMask.withUnsafeBufferPointer { strict in
        relaxedMask.withUnsafeBufferPointer { relaxed in
            support.withUnsafeBufferPointer { support in
                result.withUnsafeMutableBufferPointer { result in
                    let strict = strict.baseAddress!, relaxed = relaxed.baseAddress!
                    let support = support.baseAddress!, result = result.baseAddress!
                    for index in strictMask.indices {
                        result[index] = strict[index] != 0
                            || (relaxed[index] != 0 && support[index] != 0) ? 1 : 0
                    }
                }
            }
        }
    }
    return result
}

private func binaryDilate(_ input: [UInt8], width: Int, height: Int, radius: Int) -> [UInt8] {
    guard radius > 0 else { return input }
    var output = Array(repeating: UInt8(0), count: input.count)
    input.withUnsafeBufferPointer { source in
        output.withUnsafeMutableBufferPointer { destination in
            let source = source.baseAddress!
            let destination = destination.baseAddress!
            for y in 0..<height {
                let row = source + y * width
                var x = 0
                while x < width {
                    guard row[x] != 0 else { x += 1; continue }
                    let start = x
                    repeat { x += 1 } while x < width && row[x] != 0
                    // 連続した非ゼロ画素の diamond の和集合は、各行で一つの区間になる。
                    for ny in max(0, y - radius)...min(height - 1, y + radius) {
                        let reach = radius - abs(ny - y)
                        let first = max(0, start - reach)
                        let last = min(width - 1, x - 1 + reach)
                        memset(destination + ny * width + first, 1, last - first + 1)
                    }
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
    input.withUnsafeBufferPointer { source in
        output.withUnsafeMutableBufferPointer { destination in
            let source = source.baseAddress!
            let destination = destination.baseAddress!
            for y in radius..<(height - radius) {
                for x in radius..<(width - radius) {
                    // 中心も必須の近傍要素なので、0 なら残りの読み出しは不要。
                    let index = y * width + x
                    guard source[index] != 0 else { continue }
                    // よく使う半径の同じ Manhattan 近傍を展開し、画素ごとのループを省く。
                    if radius == 1 {
                        if source[index - width] != 0 && source[index - 1] != 0
                            && source[index + 1] != 0 && source[index + width] != 0 {
                            destination[index] = 1
                        }
                        continue
                    }
                    if radius == 2 {
                        if source[index - 2 * width] != 0
                            && source[index - width - 1] != 0 && source[index - width] != 0
                            && source[index - width + 1] != 0
                            && source[index - 2] != 0 && source[index - 1] != 0
                            && source[index + 1] != 0 && source[index + 2] != 0
                            && source[index + width - 1] != 0 && source[index + width] != 0
                            && source[index + width + 1] != 0 && source[index + 2 * width] != 0 {
                            destination[index] = 1
                        }
                        continue
                    }
                    var survives = true
                    for ny in (y - radius)...(y + radius) {
                        let reach = radius - abs(ny - y)
                        for nx in (x - reach)...(x + reach) {
                            guard source[ny * width + nx] == 0 else { continue }
                            survives = false
                            break
                        }
                        if !survives { break }
                    }
                    if survives { destination[y * width + x] = 1 }
                }
            }
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
    /// Emitter-eligibility evidence; nil when the candidate had no valid evidence.
    var emitter: SaberEmitterDiagnostics?

    init(centroidX: Double, centroidY: Double, bodyEndpoints: (PixelPoint, PixelPoint)?,
         minimumArea: Int, bodyPointCount: Int, establishedContinuousBody: Bool,
         denseTrimmedCoreLine: Bool, stronglyTrimmedCoreLine: Bool, diffusedBlueBody: Bool,
         gatingValues: [String: Double], emitter: SaberEmitterDiagnostics? = nil) {
        self.centroidX = centroidX; self.centroidY = centroidY; self.bodyEndpoints = bodyEndpoints
        self.minimumArea = minimumArea; self.bodyPointCount = bodyPointCount
        self.establishedContinuousBody = establishedContinuousBody
        self.denseTrimmedCoreLine = denseTrimmedCoreLine
        self.stronglyTrimmedCoreLine = stronglyTrimmedCoreLine; self.diffusedBlueBody = diffusedBlueBody
        self.gatingValues = gatingValues
        self.emitter = emitter
    }
}

/// Emitter-eligibility evidence of one candidate, for Debug Recording only.
///
/// Built exclusively inside `scoredSaberComponent`'s
/// `collectEndpointDiagnostics` branch, after the candidate (score,
/// eligibility, endpoints) is already final. Fields named after production
/// variables are copies of those variables; the `*Term` split and the
/// `coreBy*` inputs repeat the production formula on the copied inputs; the
/// channel statistics are a separate read-only pass over the component's own
/// mask samples. Nothing here is read by recognition.
struct SaberEmitterDiagnostics: Equatable {
    /// Production `emitterScore` (copied, not recomputed).
    let emitterScore: Double
    /// `clamp01((peak - 200) / 55) * 0.32` and the other four addends, in production order.
    let peakTerm: Double
    let meanTerm: Double
    let highValueTerm: Double
    let purityTerm: Double
    let clippedWhiteTerm: Double
    /// hasEmitterCore = coreByHighValueRatio || coreByPeakAndMean || coreByClippedWhite.
    let hasEmitterCore: Bool
    let coreByHighValueRatio: Bool
    let coreByPeakAndMean: Bool
    let coreByClippedWhite: Bool
    /// Production `isEmitterEligible` at the scoring site (before any later
    /// source-specific rejection in BGRADetection).
    let baseEligible: Bool
    let compactRedGate: Bool
    /// Production `majorLength` (mask samples) and `bladeLengthSupport`.
    let majorLengthSamples: Double
    let bladeLengthSupport: Double
    let localContrast: Double
    let emitterTexture: Double
    let brightnessVariation: Double
    let coreSupport: Double
    let longitudinalCoreCoverage: Double
    let longitudinalHighCoverage: Double
    /// Sorted-channel statistics over every component sample (max channel =
    /// `value`; second channel = the radiance map; min channel = value - chroma).
    let sampleCount: Int
    let colorSampleCount: Int
    let meanMaxChannel: Double
    let meanSecondChannel: Double?
    let maxSecondChannel: Int?
    let meanMinChannel: Double
    /// value >= 245 && chroma <= 38: the production clipped-white pixel test.
    let nearWhiteFraction: Double
    /// second channel >= 100: the production bright-core second-channel floor.
    let brightSecondChannelFraction: Double?

    /// Applied final RED selection gate, copied from the original BGRA support pass.
    var warmNoDeepRed: SaberWarmNoDeepRedVerdict? = nil
    /// 最終の青支持判定。元画像の整数 count をそのまま診断へコピーする。
    var blueNoDeepSupport: SaberBlueNoDeepSupportVerdict? = nil

    static let emitterScoreThreshold = 0.42
    var emitterScoreMargin: Double { emitterScore - Self.emitterScoreThreshold }
}

/// Production RED-only verdict on unique original pixels in the dilate1 domain.
/// Fractions are diagnostic values; the decision uses exact integer comparisons.
struct SaberWarmNoDeepRedVerdict: Codable, Equatable {
    let applied: Bool
    let deepCount: Int
    let warmCount: Int
    let pixelCount: Int
    let warmFrac: Double
    let rejected: Bool
    let rejectionReason: String?

    init(deepCount: Int, warmCount: Int, pixelCount: Int) {
        applied = true
        self.deepCount = deepCount
        self.warmCount = warmCount
        self.pixelCount = pixelCount
        warmFrac = pixelCount > 0 ? Double(warmCount) / Double(pixelCount) : 0
        rejected = deepCount == 0 && pixelCount > 0 && warmCount * 100 >= pixelCount * 30
        rejectionReason = rejected ? "warmNoDeepRed" : nil
    }
}

/// 元画像の dilate1 領域に濃い青があるかを整数だけで判定する。
struct SaberBlueNoDeepSupportVerdict: Codable, Equatable {
    let applied: Bool
    let deepCount: Int
    let pixelCount: Int
    let rejected: Bool
    let rejectionReason: String?

    init(deepCount: Int, pixelCount: Int) {
        applied = true
        self.deepCount = deepCount
        self.pixelCount = pixelCount
        rejected = deepCount == 0
        rejectionReason = rejected ? "blueNoDeepSupport" : nil
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
    /// 最終の赤・青支持判定まで保持する mask 座標の production sample points。
    var supportSamplePoints: [PixelPoint] = []
    var warmNoDeepRed: SaberWarmNoDeepRedVerdict? = nil
    var blueNoDeepSupport: SaberBlueNoDeepSupportVerdict? = nil
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

// 所属判定専用。全画素 mask と bbox 内だけの proposal mask に同じ座標でアクセスする。
private struct SaberComponentMask {
    let pixels: [UInt8]
    let minX: Int
    let minY: Int
    let width: Int
    let height: Int

    @inline(__always) func localIndex(x: Int, y: Int) -> Int? {
        let x = x - minX, y = y - minY
        guard x >= 0, x < width, y >= 0, y < height else { return nil }
        return y * width + x
    }
}

private struct SaberEvidencePixels {
    let value: UnsafePointer<UInt8>
    let chroma: UnsafePointer<UInt8>
    let colorMask: UnsafePointer<UInt8>
    let coreMask: UnsafePointer<UInt8>
    let radiance: UnsafePointer<UInt8>?
}

private extension SaberEvidence {
    // isValid を確認した呼び出し元だけで使い、ポインタはこのクロージャの外へ出さない。
    func withUnsafePixels(_ body: (SaberEvidencePixels) -> Void) {
        value.withUnsafeBufferPointer { value in
            chroma.withUnsafeBufferPointer { chroma in
                colorMask.withUnsafeBufferPointer { colorMask in
                    coreMask.withUnsafeBufferPointer { coreMask in
                        radiance.withUnsafeBufferPointer { radiance in
                            body(SaberEvidencePixels(
                                value: value.baseAddress!, chroma: chroma.baseAddress!,
                                colorMask: colorMask.baseAddress!, coreMask: coreMask.baseAddress!,
                                radiance: radiance.count == value.count ? radiance.baseAddress : nil
                            ))
                        }
                    }
                }
            }
        }
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

// 元の dy→dx の菱形走査と同じ順。内部画素は境界判定を一度にまとめる。
private let clippedDiamondOffsets = [(0, -2), (-1, -1), (0, -1), (1, -1),
    (-2, 0), (-1, 0), (0, 0), (1, 0), (2, 0), (-1, 1), (0, 1), (1, 1), (0, 2)]

private func clamp01(_ value: Double) -> Double { min(max(value, 0), 1) }

private struct DominantLongitudinalBody {
    let points: [PixelPoint]
    let pointCount: Int
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
    projections: [Double],
    meanX: Double,
    meanY: Double,
    axis: (Double, Double)
) -> DominantLongitudinalBody {
    guard points.count >= 6 else {
        return DominantLongitudinalBody(points: [], pointCount: points.count, continuity: 1,
                                        largestGap: 0, retainedRatio: 1,
                                        intervalEndpoints: nil,
                                        intervalLength: 0, density: 0)
    }
    guard let minProjection = projections.min(), let maxProjection = projections.max() else {
        return DominantLongitudinalBody(points: [], pointCount: points.count, continuity: 1,
                                        largestGap: 0, retainedRatio: 1,
                                        intervalEndpoints: nil,
                                        intervalLength: 0, density: 0)
    }
    let binCount = max(1, Int((maxProjection - minProjection).rounded(.up)) + 1)
    guard binCount >= 8 else {
        return DominantLongitudinalBody(points: [], pointCount: points.count, continuity: 1,
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
            points: [], pointCount: points.count, continuity: Double(occupied) / Double(binCount),
            largestGap: largestZeroRun(in: counts, range: 0..<binCount), retainedRatio: 1,
            intervalEndpoints: intervalEndpoints(0, binCount - 1),
            intervalLength: Double(binCount),
            density: Double(points.count) / Double(binCount)
        )
    }
    let denseThreshold = max(2, Int((Double(peakCount) * 0.30).rounded(.up)))
    let denseBins = counts.indices.filter { counts[$0] >= denseThreshold }
    guard let firstDense = denseBins.first else {
        return DominantLongitudinalBody(points: [], pointCount: points.count, continuity: 0,
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
    let bodyPointCount = counts[bodyRange].reduce(0, +)
    guard bodyPointCount >= 4 else {
        return DominantLongitudinalBody(points: [], pointCount: points.count, continuity: 0,
                                        largestGap: binCount, retainedRatio: 1,
                                        intervalEndpoints: intervalEndpoints(0, binCount - 1),
                                        intervalLength: Double(binCount),
                                        density: Double(points.count) / Double(binCount))
    }
    // retained=1 の経路は点数だけを読む。PCA は retained<0.85 のときだけ選択点を使う。
    let bodyPoints = bodyPointCount == points.count ? []
        : points.indices.compactMap { bodyRange.contains(pointBins[$0]) ? points[$0] : nil }
    let denseInBody = counts[bodyRange].filter { $0 >= denseThreshold }.count
    let continuity = Double(denseInBody) / Double(bodyRange.count)
    return DominantLongitudinalBody(
        points: bodyPoints, pointCount: bodyPointCount,
        continuity: continuity,
        largestGap: largestZeroRun(in: counts, range: bodyStart..<(bodyEnd + 1)),
        retainedRatio: Double(bodyPointCount) / Double(points.count),
        intervalEndpoints: intervalEndpoints(bodyStart, bodyEnd),
        intervalLength: Double(bodyRange.count),
        density: Double(bodyPointCount) / Double(bodyRange.count)
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
                                  componentMask: SaberComponentMask,
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
    // 同じ式・同じ点順の binary64 投影を一度だけ求め、body と軸 bin で再利用する。
    var projections: [Double] = []
    projections.reserveCapacity(points.count)
    for point in points {
        let dx = Double(point.x) - meanX
        let dy = Double(point.y) - meanY
        let major = dx * axis.0 + dy * axis.1
        projections.append(major)
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
    let body = dominantLongitudinalBody(in: points, projections: projections, meanX: meanX, meanY: meanY, axis: axis)

    let binCount = max(4, min(12, Int(majorLength.rounded(.up))))
    // bin は従来どおり 4...12。固定長の作業域を使い、縮約は元と同じ逐次加算にする。
    var binMin = SIMD16<Double>(repeating: Double.greatestFiniteMagnitude)
    var binMax = SIMD16<Double>(repeating: -Double.greatestFiniteMagnitude)
    var axialBins = Array(repeating: UInt8(0), count: points.count)
    for index in points.indices {
        let point = points[index]
        let dx = Double(point.x) - meanX
        let dy = Double(point.y) - meanY
        let minor = dx * normal.0 + dy * normal.1
        let normalized = clamp01((projections[index] - minMajor) / max(maxMajor - minMajor, 1.0))
        let bin = min(binCount - 1, Int(normalized * Double(binCount)))
        axialBins[index] = UInt8(bin)
        binMin[bin] = min(binMin[bin], minor)
        binMax[bin] = max(binMax[bin], minor)
    }
    var widths = SIMD16<Double>(repeating: 0)
    var widthCount = 0
    for index in 0..<binCount where binMax[index] >= binMin[index] {
        widths[widthCount] = binMax[index] - binMin[index] + 1.0
        widthCount += 1
    }
    var widthSum = 0.0
    for index in 0..<widthCount { widthSum += widths[index] }
    let meanWidth = widthSum / Double(max(widthCount, 1))
    var widthVarianceSum = 0.0
    for index in 0..<widthCount { widthVarianceSum += pow(widths[index] - meanWidth, 2) }
    let widthVariance = widthVarianceSum / Double(max(widthCount, 1))
    let widthVariation = sqrt(widthVariance) / max(meanWidth, 1.0)

    if let stageProfile {
        stageProfile.shapeAndAxisMs += (ProcessInfo.processInfo.systemUptime - shapeStart) * 1000
    }
    let evidenceStart = stageProfile == nil ? 0 : ProcessInfo.processInfo.systemUptime

    var boxMinX = Int.max, boxMinY = Int.max, boxMaxX = Int.min, boxMaxY = Int.min
    for point in points {
        boxMinX = min(boxMinX, point.x); boxMinY = min(boxMinY, point.y)
        boxMaxX = max(boxMaxX, point.x); boxMaxY = max(boxMaxY, point.y)
    }
    let boundingBox = SaberBoundingBox(minX: boxMinX, minY: boxMinY,
                                       maxX: boxMaxX, maxY: boxMaxY)

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
        // 訪問する全画素は bbox + 半径2の範囲内。frame 全域を候補ごとに確保しない。
        let clippedMinX = max(0, boxMinX - 2), clippedMinY = max(0, boxMinY - 2)
        let clippedWidth = min(width - 1, boxMaxX + 2) - clippedMinX + 1
        let clippedHeight = min(height - 1, boxMaxY + 2) - clippedMinY + 1
        var clippedWhiteMask = Array(repeating: false, count: clippedWidth * clippedHeight)
        var clippedWhiteCount = 0
        var outsideValueSum = 0.0
        var outsideCount = 0
        var highAxisBins = SIMD16<UInt8>(repeating: 0)
        var coreAxisBins = SIMD16<UInt8>(repeating: 0)
        var coreCount = 0
        let neighborOffsets = [(-2, 0), (2, 0), (0, -2), (0, 2)]

        evidence.withUnsafePixels { pixels in
            componentMask.pixels.withUnsafeBufferPointer { componentPixels in
                clippedWhiteMask.withUnsafeMutableBufferPointer { clippedWhite in
                    let clippedWhite = clippedWhite.baseAddress!
                    let componentPixels = componentPixels.baseAddress!
                    for pointIndex in points.indices {
                        let point = points[pointIndex]
                        let axialBin = Int(axialBins[pointIndex])
                        let index = point.y * width + point.x
                        if let radiance = pixels.radiance {
                            radianceSum += pow(Double(radiance[index]) / 255.0, 2)
                        }
                        let value = Int(pixels.value[index])
                        let chroma = Int(pixels.chroma[index])
                        if pixels.coreMask[index] != 0 {
                            coreCount += 1
                            coreAxisBins[axialBin] = 1
                        }
                        if pixels.colorMask[index] != 0 {
                            rawCount += 1
                            valueSum += Double(value)
                            valueSquareSum += Double(value * value)
                            puritySum += Double(chroma) / Double(max(value, 1))
                            measuredPeakValue = max(measuredPeakValue, value)
                            if value >= 220 {
                                highCount += 1
                                highAxisBins[axialBin] = 1
                            }
                        } else if value >= 245 && chroma <= 38 {
                            // A clipped LED becomes white and falls outside the color mask.
                            // close can bridge it back into the selected component.
                            let clippedIndex = (point.y - clippedMinY) * clippedWidth + point.x - clippedMinX
                            if !clippedWhite[clippedIndex] {
                                clippedWhite[clippedIndex] = true
                                clippedWhiteCount += 1
                            }
                            highAxisBins[axialBin] = 1
                        }
                        // White-clipped LED centers often sit immediately beside, rather
                        // than inside, the HSV color component. Count each nearby sample
                        // once so a diffuse reflection cannot gain simply from its area.
                        @inline(__always) func visitClipped(_ nx: Int, _ ny: Int) {
                            let neighbor = ny * width + nx
                            if pixels.colorMask[neighbor] == 0,
                               pixels.value[neighbor] >= 245,
                               pixels.chroma[neighbor] <= 45 {
                                let clippedIndex = (ny - clippedMinY) * clippedWidth + nx - clippedMinX
                                if !clippedWhite[clippedIndex] {
                                    clippedWhite[clippedIndex] = true
                                    clippedWhiteCount += 1
                                }
                            }
                        }
                        if point.x >= 2, point.x < width - 2, point.y >= 2, point.y < height - 2 {
                            for (dx, dy) in clippedDiamondOffsets {
                                visitClipped(point.x + dx, point.y + dy)
                            }
                        } else {
                            for (dx, dy) in clippedDiamondOffsets {
                                let nx = point.x + dx, ny = point.y + dy
                                guard nx >= 0, nx < width, ny >= 0, ny < height else { continue }
                                visitClipped(nx, ny)
                            }
                        }
                        for (dx, dy) in neighborOffsets {
                            let nx = point.x + dx, ny = point.y + dy
                            guard nx >= 0, nx < width, ny >= 0, ny < height else { continue }
                            let neighbor = ny * width + nx
                            let belongsToComponent = componentMask.localIndex(x: nx, y: ny)
                                .map { componentPixels[$0] != 0 } ?? false
                            guard !belongsToComponent else { continue }
                            outsideValueSum += Double(pixels.value[neighbor])
                            outsideCount += 1
                        }
                    }
                }
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
        longitudinalHighCoverage = Double((0..<binCount).reduce(0) { $0 + Int(highAxisBins[$1]) }) / Double(binCount)
        coreSupportRatio = Double(coreCount) / Double(max(points.count, 1))
        longitudinalCoreCoverage = Double((0..<binCount).reduce(0) { $0 + Int(coreAxisBins[$1]) }) / Double(binCount)
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
        && body.pointCount >= minimumArea
    let hasDiffusedBlueCoreLineBody = evidence?.color == .blue
        && source == "core-line"
        && body.retainedRatio < EndpointSelectionThresholds.diffuserRetainedRatio
        && body.continuity >= EndpointSelectionThresholds.diffuserContinuity
        && body.largestGap <= EndpointSelectionThresholds.coreLineMaximumGap
        && body.pointCount >= minimumArea
        && meanPurity >= EndpointSelectionThresholds.diffuserColorPurity
        && coreSupportRatio >= EndpointSelectionThresholds.diffuserCoreSupport
    if body.pointCount >= minimumArea,
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
    if evidence != nil { candidate.supportSamplePoints = points }
    if collectEndpointDiagnostics {
        candidate.endpointDiagnosticTrace = SaberEndpointDiagnosticTrace(
            centroidX: meanX, centroidY: meanY,
            bodyEndpoints: adoptedBodyEndpoints,
            minimumArea: minimumArea, bodyPointCount: body.pointCount,
            establishedContinuousBody: hasEstablishedContinuousBody,
            denseTrimmedCoreLine: hasDenseTrimmedCoreLine,
            stronglyTrimmedCoreLine: hasStronglyTrimmedSupportedCoreLine,
            diffusedBlueBody: hasDiffusedBlueCoreLineBody,
            gatingValues: ["bodyPointCount": Double(body.pointCount), "minimumArea": Double(minimumArea),
                "retainedBodyRatio": body.retainedRatio, "retainedBodyLimit": 0.85,
                "continuity": body.continuity, "connectedBodyContinuityThreshold": EndpointSelectionThresholds.connectedBodyContinuity,
                "largestGap": Double(body.largestGap), "coreLineMaximumGapThreshold": Double(EndpointSelectionThresholds.coreLineMaximumGap),
                "density": body.density, "denseCoreLineDensityThreshold": EndpointSelectionThresholds.denseCoreLineDensity,
                "stronglyTrimmedRetainedRatioThreshold": EndpointSelectionThresholds.stronglyTrimmedRetainedRatio,
                "diffuserRetainedRatioThreshold": EndpointSelectionThresholds.diffuserRetainedRatio,
                "diffuserContinuityThreshold": EndpointSelectionThresholds.diffuserContinuity,
                "colorPurity": meanPurity, "diffuserColorPurityThreshold": EndpointSelectionThresholds.diffuserColorPurity,
                "coreSupport": coreSupportRatio, "diffuserCoreSupportThreshold": EndpointSelectionThresholds.diffuserCoreSupport],
            emitter: evidence.flatMap { evidence in
                evidence.isValid(width: width, height: height) ? saberEmitterDiagnostics(
                    points: points, width: width, height: height, evidence: evidence,
                    emitterScore: emitterScore, peakValue: peakValue, meanValue: meanValue,
                    highRatio: highRatio, meanPurity: meanPurity, clippedRatio: clippedRatio,
                    baseEligible: isEmitterEligible, isCompactRed: isCompactRed,
                    majorLength: majorLength, bladeLengthSupport: bladeLengthSupport,
                    localContrast: localContrast, emitterTexture: emitterTexture,
                    brightnessVariation: brightnessVariation, coreSupport: coreSupportRatio,
                    longitudinalCoreCoverage: longitudinalCoreCoverage,
                    longitudinalHighCoverage: longitudinalHighCoverage) : nil
            })
    }
    return candidate
}

/// Debug Recording only: see `SaberEmitterDiagnostics`. Every argument is a
/// final production value of the candidate; this function only splits the
/// emitter score into its addends and reads the component's own samples.
private func saberEmitterDiagnostics(
    points: [PixelPoint], width: Int, height: Int, evidence: SaberEvidence,
    emitterScore: Double, peakValue: Int, meanValue: Double, highRatio: Double,
    meanPurity: Double, clippedRatio: Double, baseEligible: Bool, isCompactRed: Bool,
    majorLength: Double, bladeLengthSupport: Double, localContrast: Double,
    emitterTexture: Double, brightnessVariation: Double, coreSupport: Double,
    longitudinalCoreCoverage: Double, longitudinalHighCoverage: Double
) -> SaberEmitterDiagnostics {
    let hasRadiance = evidence.radiance.count == evidence.value.count
    var colorSamples = 0, nearWhite = 0, brightSecond = 0, maxSecond = 0
    var maxSum = 0, secondSum = 0, minSum = 0
    for point in points {
        let index = point.y * width + point.x
        let value = Int(evidence.value[index]), chroma = Int(evidence.chroma[index])
        if evidence.colorMask[index] != 0 { colorSamples += 1 }
        if value >= 245 && chroma <= 38 { nearWhite += 1 }
        maxSum += value
        minSum += max(0, value - chroma)
        if hasRadiance {
            let second = Int(evidence.radiance[index])
            secondSum += second
            maxSecond = max(maxSecond, second)
            if second >= 100 { brightSecond += 1 }
        }
    }
    let count = Double(max(points.count, 1))
    return SaberEmitterDiagnostics(
        emitterScore: emitterScore,
        peakTerm: clamp01((Double(peakValue) - 200.0) / 55.0) * 0.32,
        meanTerm: clamp01((meanValue - 160.0) / 95.0) * 0.23,
        highValueTerm: highRatio * 0.28,
        purityTerm: meanPurity * 0.12,
        clippedWhiteTerm: clippedRatio * 0.05,
        hasEmitterCore: highRatio >= 0.08 || (peakValue >= 242 && meanValue >= 190) || clippedRatio > 0,
        coreByHighValueRatio: highRatio >= 0.08,
        coreByPeakAndMean: peakValue >= 242 && meanValue >= 190,
        // clippedRatio > 0 exactly when the production clippedWhiteCount > 0.
        coreByClippedWhite: clippedRatio > 0,
        baseEligible: baseEligible, compactRedGate: isCompactRed,
        majorLengthSamples: majorLength, bladeLengthSupport: bladeLengthSupport,
        localContrast: localContrast, emitterTexture: emitterTexture,
        brightnessVariation: brightnessVariation, coreSupport: coreSupport,
        longitudinalCoreCoverage: longitudinalCoreCoverage,
        longitudinalHighCoverage: longitudinalHighCoverage,
        sampleCount: points.count, colorSampleCount: colorSamples,
        meanMaxChannel: Double(maxSum) / count,
        meanSecondChannel: hasRadiance ? Double(secondSum) / count : nil,
        maxSecondChannel: hasRadiance ? maxSecond : nil,
        meanMinChannel: Double(minSum) / count,
        nearWhiteFraction: Double(nearWhite) / count,
        brightSecondChannelFraction: hasRadiance ? Double(brightSecond) / count : nil
    )
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
    var queue: [PixelPoint] = []
    remaining.withUnsafeMutableBufferPointer { buffer in
        // 画素数と近傍座標を検証済み。訪問順は元と同じ seed 順・BFS 順を維持する。
        let remaining = buffer.baseAddress!
        for seed in 0..<mask.count where remaining[seed] != 0 {
            let traversalStart = stageProfile == nil ? 0 : ProcessInfo.processInfo.systemUptime
            remaining[seed] = 0
            queue.removeAll(keepingCapacity: true)
            queue.append(PixelPoint(x: seed % width, y: seed / width))
            var head = 0
            // FIFO 自体を採点用の画素列にする。元の取り出し順と同じで、二重の append が不要。
            while head < queue.count {
                let point = queue[head]; head += 1
                let x = point.x, y = point.y
                for ny in max(0, y - 1)...min(height - 1, y + 1) {
                    for nx in max(0, x - 1)...min(width - 1, x + 1) {
                        let neighbor = ny * width + nx
                        if remaining[neighbor] != 0 {
                            remaining[neighbor] = 0
                            queue.append(PixelPoint(x: nx, y: ny))
                        }
                    }
                }
            }
            if let stageProfile {
                stageProfile.connectedComponentsMs +=
                    (ProcessInfo.processInfo.systemUptime - traversalStart) * 1000
            }
            componentObserver?(queue.count)
            let scoreStart = stageProfile == nil ? 0 : ProcessInfo.processInfo.systemUptime
            let candidate = scoredSaberComponent(queue, width: width, height: height,
                                                 componentMask: SaberComponentMask(
                                                    pixels: mask, minX: 0, minY: 0, width: width, height: height
                                                 ), evidence: evidence,
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
    }
    return candidates.sorted { $0.score > $1.score }
}

// 色ごと・フレームごとの proposal 群で再利用する。候補はこの作業域を保持しない。
struct SaberProposalWorkspace {
    var membership: [UInt8] = []
    var uniqueIndices: [Int] = []
}

/// 接続済み proposal を直接採点する。bitmap は重複・所属判定専用。
func saberCandidate(from points: [PixelPoint], width: Int, height: Int,
                    evidence: SaberEvidence? = nil,
                    source: String = "color-mask",
                    stageProfile: SaberCandidateStageProfile? = nil,
                    collectEndpointDiagnostics: Bool = false) -> SaberCandidate? {
    var workspace = SaberProposalWorkspace()
    return saberCandidate(from: points, width: width, height: height, evidence: evidence,
                          workspace: &workspace, source: source, stageProfile: stageProfile,
                          collectEndpointDiagnostics: collectEndpointDiagnostics)
}

func saberCandidate(from points: [PixelPoint], width: Int, height: Int,
                    evidence: SaberEvidence? = nil,
                    workspace: inout SaberProposalWorkspace,
                    source: String = "color-mask",
                    stageProfile: SaberCandidateStageProfile? = nil,
                    collectEndpointDiagnostics: Bool = false) -> SaberCandidate? {
    guard width > 0, height > 0 else { return nil }
    let validPoints = points.filter { $0.x >= 0 && $0.x < width && $0.y >= 0 && $0.y < height }
    guard let first = validPoints.first else { return nil }
    var minX = first.x, minY = first.y, maxX = first.x, maxY = first.y
    for point in validPoints {
        minX = min(minX, point.x); minY = min(minY, point.y)
        maxX = max(maxX, point.x); maxY = max(maxY, point.y)
    }
    let localWidth = maxX - minX + 1, localHeight = maxY - minY + 1
    let area = localWidth * localHeight
    if workspace.membership.count < area {
        workspace.membership.append(contentsOf: repeatElement(0, count: area - workspace.membership.count))
    }
    workspace.uniqueIndices.removeAll(keepingCapacity: true)
    workspace.uniqueIndices.reserveCapacity(validPoints.count)
    workspace.membership.withUnsafeMutableBufferPointer { buffer in
        let mask = buffer.baseAddress!
        mask.update(repeating: 0, count: area)
        for point in validPoints {
            let localIndex = (point.y - minY) * localWidth + point.x - minX
            if mask[localIndex] == 0 {
                mask[localIndex] = 1
                workspace.uniqueIndices.append(point.y * width + point.x)
            }
        }
    }
    let componentMask = SaberComponentMask(pixels: workspace.membership, minX: minX, minY: minY,
                                            width: localWidth, height: localHeight)
    // bitmap は所属判定専用。従来と同じ row-major 順で全浮動小数点演算を行う。
    workspace.uniqueIndices.sort()
    let uniquePoints = workspace.uniqueIndices.map { PixelPoint(x: $0 % width, y: $0 / width) }
    return scoredSaberComponent(uniquePoints, width: width, height: height,
                                componentMask: componentMask,
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
