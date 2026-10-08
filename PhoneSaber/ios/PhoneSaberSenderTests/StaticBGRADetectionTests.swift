import Foundation
import CoreGraphics
import ImageIO

private struct StaticImage {
    let width: Int
    let height: Int
    let bytesPerRow: Int
    var bytes: [UInt8]

    init(width: Int, height: Int, padding: Int = 0) {
        self.width = width
        self.height = height
        bytesPerRow = width * 4 + padding
        bytes = Array(repeating: 0, count: bytesPerRow * height)
    }

    mutating func pixel(_ x: Int, _ y: Int, red: UInt8, green: UInt8, blue: UInt8, alpha: UInt8 = 255) {
        guard x >= 0, x < width, y >= 0, y < height else { return }
        let offset = y * bytesPerRow + x * 4
        bytes[offset] = blue
        bytes[offset + 1] = green
        bytes[offset + 2] = red
        bytes[offset + 3] = alpha
    }

    mutating func bar(from start: PixelPoint, to end: PixelPoint, color: SaberColor, thickness: Int = 5) {
        let red: UInt8 = color == .red ? 245 : 30
        let blue: UInt8 = color == .blue ? 245 : 30
        coloredBar(from: start, to: end, red: red, green: 45, blue: blue, thickness: thickness)
    }

    mutating func coloredBar(from start: PixelPoint, to end: PixelPoint,
                             red: UInt8, green: UInt8, blue: UInt8, thickness: Int = 5) {
        let steps = max(abs(end.x - start.x), abs(end.y - start.y))
        for step in 0...steps {
            let t = steps == 0 ? 0.0 : Double(step) / Double(steps)
            let x = Int((Double(start.x) + Double(end.x - start.x) * t).rounded())
            let y = Int((Double(start.y) + Double(end.y - start.y) * t).rounded())
            for dy in -thickness...thickness {
                for dx in -thickness...thickness where dx * dx + dy * dy <= thickness * thickness {
                    pixel(x + dx, y + dy, red: red, green: green, blue: blue)
                }
            }
        }
    }

    mutating func blurredBar(from start: PixelPoint, to end: PixelPoint, color: SaberColor) {
        let blur: [(UInt8, UInt8, UInt8)] = color == .red
            ? [(205, 40, 35), (160, 38, 35), (115, 36, 35)]
            : [(35, 40, 205), (35, 38, 160), (35, 36, 115)]
        // Draw the solid core first.  The blur samples must remain outside the
        // five-pixel core or the final bar draw would erase them completely.
        bar(from: start, to: end, color: color)
        let steps = max(abs(end.x - start.x), abs(end.y - start.y))
        for step in 0...steps {
            let t = steps == 0 ? 0.0 : Double(step) / Double(steps)
            let x = Int((Double(start.x) + Double(end.x - start.x) * t).rounded())
            let y = Int((Double(start.y) + Double(end.y - start.y) * t).rounded())
            pixel(x - 6, y, red: blur[0].0, green: blur[0].1, blue: blur[0].2)
            pixel(x + 6, y, red: blur[1].0, green: blur[1].1, blue: blur[1].2)
            pixel(x, y - 6, red: blur[1].0, green: blur[1].1, blue: blur[1].2)
            pixel(x, y + 6, red: blur[2].0, green: blur[2].1, blue: blur[2].2)
        }
    }

    /// White paper diffuser: a broad pale halo with a continuous colored core.
    mutating func diffusedPaperBlade(from start: PixelPoint, to end: PixelPoint,
                                    color: SaberColor, thickness: Int = 6) {
        let halo: (UInt8, UInt8, UInt8) = color == .red
            ? (255, 145, 140) : (135, 175, 255)
        coloredBar(from: start, to: end, red: halo.0, green: halo.1, blue: halo.2,
                   thickness: thickness + 3)
        let core: (UInt8, UInt8, UInt8) = color == .red
            ? (250, 65, 58) : (58, 105, 250)
        coloredBar(from: start, to: end, red: core.0, green: core.1, blue: core.2,
                   thickness: thickness)
    }
}

private func endpointDistance(_ result: (PixelPoint, PixelPoint), _ expected: (PixelPoint, PixelPoint)) -> Double {
    let direct = hypot(Double(result.0.x - expected.0.x), Double(result.0.y - expected.0.y))
        + hypot(Double(result.1.x - expected.1.x), Double(result.1.y - expected.1.y))
    let reversed = hypot(Double(result.0.x - expected.1.x), Double(result.0.y - expected.1.y))
        + hypot(Double(result.1.x - expected.0.x), Double(result.1.y - expected.0.y))
    return min(direct, reversed) / 2
}

private func axisAngle(_ endpoints: (PixelPoint, PixelPoint)) -> Double {
    atan2(Double(endpoints.1.y - endpoints.0.y), Double(endpoints.1.x - endpoints.0.x))
}

private func angleError(_ actual: Double, _ expected: Double) -> Double {
    var value = abs(actual - expected).truncatingRemainder(dividingBy: .pi)
    if value > .pi / 2 { value = .pi - value }
    return value
}

private func assertCase(_ name: String, image: StaticImage, color: SaberColor,
                        expected: (PixelPoint, PixelPoint), expectedAngle: Double,
                        distanceTolerance: Double = 6, angleTolerance: Double = 0.20,
                        lengthTolerance: Double = 12) {
    guard let actual = detectSaber(in: image.bytes, width: image.width, height: image.height,
                                   bytesPerRow: image.bytesPerRow, color: color,
                                   threshold: ColorThreshold(brightness: 140, dominance: 20)) else {
        fatalError("\(name): no detection")
    }
    let distance = endpointDistance(actual, expected)
    let angle = angleError(axisAngle(actual), expectedAngle)
    guard distance <= distanceTolerance else {
        let analysis = analyzeSabers(in: image.bytes, width: image.width, height: image.height,
                                     bytesPerRow: image.bytesPerRow,
                                     redThreshold: ColorThreshold(brightness: 140, dominance: 20),
                                     blueThreshold: ColorThreshold(brightness: 140, dominance: 20))
        let details = (analysis.candidates[color] ?? []).map {
            let scoreText = String(format: "%.2f", $0.score)
            return "\($0.endpoints.0)-\($0.endpoints.1) score=\(scoreText)"
        }.joined(separator: ", ")
        fatalError("\(name): endpoint distance \(distance); candidates: \(details)")
    }
    guard angle <= angleTolerance else { fatalError("\(name): angle error \(angle)") }
    let length = hypot(Double(actual.1.x - actual.0.x), Double(actual.1.y - actual.0.y))
    let expectedLength = hypot(Double(expected.1.x - expected.0.x), Double(expected.1.y - expected.0.y))
    // The filled five-pixel-radius caps extend the principal-axis extrema;
    // this explicit allowance is tied to the drawing geometry, not a loose position check.
    guard abs(length - expectedLength) <= lengthTolerance else { fatalError("\(name): length \(length)") }
}

// simulator を使わずに新 gate の元画素領域と最終選択を検証する。
private func assertBlueNoDeepSupport() {
    func support(_ rgb: (UInt8, UInt8, UInt8)) -> SaberBlueNoDeepSupportVerdict {
        let bytes = [rgb.2, rgb.1, rgb.0, 255]
        return bytes.withUnsafeBufferPointer {
            blueNoDeepSupport(points: [PixelPoint(x: 0, y: 0)], baseAddress: $0.baseAddress!,
                              width: 1, height: 1, bytesPerRow: 4, sampleStep: 2)
        }
    }
    precondition(support((202, 234, 245)).rejected)
    for rgb in [(71, 116, 180), (79, 129, 200)] as [(UInt8, UInt8, UInt8)] {
        precondition(support(rgb).deepCount == 1 && !support(rgb).rejected)
    }
    for rgb in [(0, 0, 179), (80, 129, 200), (79, 130, 200)] as [(UInt8, UInt8, UInt8)] {
        precondition(support(rgb).deepCount == 0 && support(rgb).rejected)
    }
    var tiny = StaticImage(width: 3, height: 2, padding: 16)
    tiny.pixel(1, 0, red: 71, green: 116, blue: 180)
    let verdict = tiny.bytes.withUnsafeBufferPointer {
        blueNoDeepSupport(points: [PixelPoint(x: 0, y: 0), PixelPoint(x: 1, y: 0),
                                   PixelPoint(x: 0, y: 0), PixelPoint(x: -1, y: 0), PixelPoint(x: 2, y: 0)],
                          baseAddress: $0.baseAddress!, width: 3, height: 2,
                          bytesPerRow: tiny.bytesPerRow, sampleStep: 2)
    }
    precondition(verdict.pixelCount == 6 && verdict.deepCount == 1 && !verdict.rejected)
    func blade(_ halo: (UInt8, UInt8, UInt8)) -> StaticImage {
        var image = StaticImage(width: 480, height: 640, padding: 13)
        for y in 300..<320 {
            for x in 60..<420 {
                let rgb = (306..<314).contains(y) ? (255, 255, 255) : halo
                image.pixel(x, y, red: rgb.0, green: rgb.1, blue: rgb.2)
            }
        }
        return image
    }
    func analyze(_ image: StaticImage, diagnostics: Bool = true) -> SaberFrameAnalysis {
        analyzeSabers(in: image.bytes, width: image.width, height: image.height,
                      bytesPerRow: image.bytesPerRow, redThreshold: ColorThreshold(),
                      blueThreshold: ColorThreshold(), collectPipelineDiagnostics: diagnostics)
    }
    let pale = blade((140, 170, 250))
    let rejected = analyze(pale)
    precondition(rejected.selected[.blue] == nil)
    precondition(rejected.candidates[.blue]!.first!.blueNoDeepSupport!.rejected)
    precondition(rejected.candidates[.blue]!.first!.diagnosticRejections.contains { $0.name == "blueNoDeepSupport" })
    precondition(rejected.candidates[.blue]!.first!.endpointDiagnosticTrace!.emitter!.blueNoDeepSupport!.rejected)
    var oneDeep = pale
    oneDeep.pixel(61, 301, red: 30, green: 40, blue: 250)
    for image in [blade((30, 40, 250)), oneDeep] {
        for diagnostics in [false, true] {
            let kept = analyze(image, diagnostics: diagnostics)
            precondition(kept.selected[.blue] != nil)
            let winner = kept.candidates[.blue]!.first { $0.isEmitterEligible }!
            precondition(winner.blueNoDeepSupport!.deepCount > 0)
        }
    }
    let red = analyze(blade((250, 40, 30)))
    precondition(red.selected[.red] != nil)
    precondition(red.candidates[.red]!.allSatisfy { $0.blueNoDeepSupport == nil })
    precondition(analyze(blade((250, 170, 140))).selected[.red] == nil)
    var fallback = pale
    for y in 400..<416 {
        for x in 60..<160 { fallback.pixel(x, y, red: 30, green: 40, blue: 250) }
    }
    let next = analyze(fallback)
    precondition(next.candidates[.blue]!.first!.blueNoDeepSupport!.rejected)
    let winner = next.candidates[.blue]!.first { $0.isEmitterEligible }!
    precondition(winner.endpoints.0.y > 390 && winner.blueNoDeepSupport!.deepCount > 0)
    precondition(next.selected[.blue]!.0 == winner.endpoints.0 && next.selected[.blue]!.1 == winner.endpoints.1)
    print("blueNoDeepSupport: integer boundaries, original pixels, diagnostics, RED and fall-through passed")
}

// 菱形の定義から全画素を調べる参照実装。行区間化・unsafe アクセスの境界を検証する。
private func assertLosslessMorphology() {
    func reference(_ mask: [UInt8], _ width: Int, _ height: Int,
                   _ radius: Int, erode: Bool) -> [UInt8] {
        guard radius > 0 else { return mask }
        return mask.indices.map { index in
            let x = index % width, y = index / width
            if erode && (x < radius || y < radius
                || x >= width - radius || y >= height - radius) { return 0 }
            let neighbors = mask.indices.filter {
                abs($0 % width - x) + abs($0 / width - y) <= radius
            }
            let matches = erode ? neighbors.allSatisfy { mask[$0] != 0 }
                : neighbors.contains { mask[$0] != 0 }
            return matches ? 1 : 0
        }
    }
    func check(_ mask: [UInt8], width: Int, height: Int) {
        for radius in 0...4 {
            let dilated = reference(mask, width, height, radius, erode: false)
            let eroded = reference(mask, width, height, radius, erode: true)
            precondition(dilateSaberMask(mask, width: width, height: height, radius: radius) == dilated)
            precondition(closeSaberMask(mask, width: width, height: height, radius: radius)
                == reference(dilated, width, height, radius, erode: true))
            precondition(openSaberMask(mask, width: width, height: height, radius: radius)
                == reference(eroded, width, height, radius, erode: false))
        }
    }
    // 3x3 の全パターンで、空・密・端・過大半径と非二値入力を網羅する。
    for pattern in 0..<512 {
        check((0..<9).map { pattern & (1 << $0) == 0 ? 0 : 255 }, width: 3, height: 3)
    }
    var state: UInt64 = 0x51abe2
    for (width, height) in [(1, 9), (9, 1), (7, 11), (13, 9)] {
        for density in [1, 3, 7] {
            let mask: [UInt8] = (0..<(width * height)).map { _ in
                state = state &* 6364136223846793005 &+ 1
                return (state >> 32) % 8 < density ? UInt8(truncatingIfNeeded: state) | 1 : 0
            }
            check(mask, width: width, height: height)
        }
    }
    print("lossless morphology: exhaustive masks, diamond radii 0...4, edges and nonbinary inputs passed")
}

// 任意の PNG を XCTest と同じ CoreGraphics BGRA 経路で読み込む native -O 計測用。
private func loadStaticPNG(_ path: String) -> StaticImage {
    let url = URL(fileURLWithPath: path)
    guard let source = CGImageSourceCreateWithURL(url as CFURL, nil),
          let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
        fatalError("cannot load PNG: \(path)")
    }
    var result = StaticImage(width: image.width, height: image.height)
    result.bytes.withUnsafeMutableBytes { raw in
        let context = CGContext(data: raw.baseAddress!, width: image.width, height: image.height,
                                bitsPerComponent: 8, bytesPerRow: image.width * 4,
                                space: CGColorSpaceCreateDeviceRGB(),
                                bitmapInfo: CGImageAlphaInfo.premultipliedFirst.rawValue
                                    | CGBitmapInfo.byteOrder32Little.rawValue)!
        context.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
    }
    return result
}

// Double を十進文字列に丸めず、候補の全 stored property（診断も含む）を比較する。
// profile の実時間だけを除外する。Dictionary の反復順は使用しない。
private func losslessSignature(_ value: Any) -> String {
    if let double = value as? Double { return "Double:\(double.bitPattern)" }
    let mirror = Mirror(reflecting: value)
    let type = String(reflecting: type(of: value))
    if mirror.children.isEmpty { return "\(type):\(String(reflecting: value))" }
    if mirror.displayStyle == .dictionary || mirror.displayStyle == .set {
        return type + "[" + mirror.children.map { losslessSignature($0.value) }.sorted()
            .joined(separator: ";") + "]"
    }
    return type + "[" + mirror.children.map {
        ($0.label ?? "") + "=" + losslessSignature($0.value)
    }.joined(separator: ";") + "]"
}

private func frameSignature(_ analysis: SaberFrameAnalysis) -> String {
    [SaberColor.red, .blue].map { color in
        losslessSignature(analysis.candidates[color] ?? [])
            + losslessSignature(analysis.selected[color] as Any)
            + losslessSignature(analysis.pipelineDiagnostics?[color] as Any)
    }.joined(separator: "\n")
}

private func profileCountSignature(_ profile: SaberDetectionProfile) -> String {
    losslessSignature([profile.colorPixelCount, profile.brightCorePixelCount,
                       profile.lineProposalCount, profile.candidateCount, profile.connectedComponentCount])
}

private func staticAnalysis(_ image: StaticImage, step: Int = 2,
                            threshold: ColorThreshold = ColorThreshold(),
                            profile: Bool = false) -> SaberFrameAnalysis {
    analyzeSabers(in: image.bytes, width: image.width, height: image.height,
                  bytesPerRow: image.bytesPerRow, redThreshold: threshold, blueThreshold: threshold,
                  sampleStep: step, collectProfile: profile, collectPipelineDiagnostics: true)
}

private func nativeBenchmark(path: String, iterations: Int) {
    precondition(iterations > 0)
    let bright = loadStaticPNG(path)
    let blank = StaticImage(width: bright.width, height: bright.height)
    let threshold = ColorThreshold()
    for (name, image) in [("empty", blank), ("bright", bright)] {
        func run(profile: Bool = false) -> SaberFrameAnalysis {
            analyzeSabers(in: image.bytes, width: image.width, height: image.height,
                          bytesPerRow: image.bytesPerRow, redThreshold: threshold,
                          blueThreshold: threshold, collectProfile: profile)
        }
        for _ in 0..<5 { _ = run() }
        var samples: [Double] = []
        var candidateCount = 0
        for _ in 0..<iterations {
            let start = ProcessInfo.processInfo.systemUptime
            let analysis = run()
            samples.append((ProcessInfo.processInfo.systemUptime - start) * 1000)
            for color in [SaberColor.red, .blue] { candidateCount += analysis.candidates[color]?.count ?? 0 }
        }
        let ordered = samples.sorted()
        print(String(format: "[NativeTiming] %@ %dx%d n=%d avg=%.3f median=%.3f p90=%.3f max=%.3f ms candidates=%d",
                     name, image.width, image.height, iterations,
                     samples.reduce(0, +) / Double(iterations), ordered[iterations / 2],
                     ordered[Int(ceil(Double(iterations) * 0.9)) - 1], ordered.last!, candidateCount))
        var scan = 0.0, morphology = 0.0, components = 0.0, evidence = 0.0, traversal = 0.0
        var lineProposal = 0.0, lineScore = 0.0
        for _ in 0..<iterations {
            let profile = run(profile: true).profile!
            scan += profile.pixelScanHSVMaskMs; morphology += profile.morphologyMs
            components += profile.componentAndScoreMs; evidence += profile.brightnessContrastColorMs
            traversal += profile.connectedComponentsMs
            lineProposal += profile.lineProposalMs; lineScore += profile.lineScoreMs
        }
        let n = Double(iterations)
        print(String(format: "[NativeStages] %@ scanMask=%.3f morphology=%.3f components=%.3f BFS=%.3f evidence=%.3f lineProposal=%.3f lineScore=%.3f ms",
                     name, scan / n, morphology / n, components / n, traversal / n,
                     evidence / n, lineProposal / n, lineScore / n))
    }
}

private func assertLosslessPixelScan() {
    precondition(supportedBlueDiffuserMask(strictMask: [], relaxedMask: [], width: 0, height: 4) == [])
    let palette: [(UInt8, UInt8, UInt8)] = [
        (0, 0, 0), (255, 255, 255), (110, 110, 110), (145, 145, 145),
        (235, 100, 100), (255, 0, 0), (0, 0, 255), (255, 85, 0),
        (255, 0, 85), (0, 255, 255), (85, 0, 255), (0, 85, 255),
        (90, 108, 110), (102, 108, 110), (140, 170, 250), (145, 120, 120)
    ]
    let thresholds = [ColorThreshold(), ColorThreshold(brightness: 0, dominance: 0, saturation: 0),
                      ColorThreshold(brightness: 255, dominance: 255, saturation: 255),
                      ColorThreshold(brightness: 110, dominance: 8, saturation: 10)]
    for (width, height) in [(1, 1), (1, 19), (23, 1), (37, 29)] {
        var image = StaticImage(width: width, height: height, padding: 13)
        for y in 0..<height {
            for x in 0..<width {
                let rgb = palette[(x + y * width) % palette.count]
                image.pixel(x, y, red: rgb.0, green: rgb.1, blue: rgb.2)
            }
        }
        for step in [1, 2, 3, 8, 64] {
            for threshold in thresholds {
                let maskWidth = (width + step - 1) / step, maskHeight = (height + step - 1) / step
                var red: [UInt8] = [], blue: [UInt8] = [], relaxed: [UInt8] = []
                var cores = 0
                for y in stride(from: 0, to: height, by: step) {
                    for x in stride(from: 0, to: width, by: step) {
                        let offset = y * image.bytesPerRow + x * 4
                        let b = image.bytes[offset], g = image.bytes[offset + 1], r = image.bytes[offset + 2]
                        let hsv = saberHSV(r, g, b)
                        red.append(matchesSaberHSV(hsv, color: .red, threshold: threshold) ? 1 : 0)
                        blue.append(matchesSaberHSV(hsv, color: .blue, threshold: threshold) ? 1 : 0)
                        relaxed.append(matchesBlueDiffuserPixel(r, g, b, hsv: hsv, threshold: threshold) ? 1 : 0)
                        let second = Int(r) + Int(g) + Int(b) - Int(max(r, g, b)) - Int(min(r, g, b))
                        if hsv.value >= 235 && second >= 100 { cores += 1 }
                    }
                }
                let supported = supportedBlueDiffuserMask(strictMask: blue, relaxedMask: relaxed,
                                                          width: maskWidth, height: maskHeight)
                let actual = staticAnalysis(image, step: step, threshold: threshold, profile: true)
                let redCount = red.reduce(0) { $0 + Int($1) }, blueCount = supported.reduce(0) { $0 + Int($1) }
                precondition(actual.pipelineDiagnostics?[.red]?.maskPixelCount == redCount)
                precondition(actual.pipelineDiagnostics?[.blue]?.maskPixelCount == blueCount)
                precondition(actual.profile?.colorPixelCount == redCount + blueCount)
                precondition(actual.profile?.brightCorePixelCount == cores)
            }
        }
    }
    print("lossless pixel scan: reference HSV, threshold extremes, odd sizes, padding and sample steps passed")
}

private func assertLosslessProposalMembership() {
    let width = 73, height = 59
    for x in [0, 1, 31, width - 2, width - 1] {
        let points = (0..<height).map { PixelPoint(x: x, y: $0) }
        var mask = Array(repeating: UInt8(0), count: width * height)
        for point in points { mask[point.y * width + point.x] = 1 }
        let evidence = SaberEvidence(color: .blue,
                                     radiance: Array(repeating: 100, count: mask.count),
                                     value: Array(repeating: 255, count: mask.count),
                                     chroma: Array(repeating: 0, count: mask.count),
                                     colorMask: mask, coreMask: mask)
        let canonical = saberCandidate(from: points, width: width, height: height,
                                       evidence: evidence, collectEndpointDiagnostics: true)
        precondition(canonical != nil)
        let fullFrame = saberCandidates(in: mask, width: width, height: height,
                                        evidence: evidence, collectEndpointDiagnostics: true)
        precondition(fullFrame.count == 1)
        precondition(losslessSignature(canonical!) == losslessSignature(fullFrame[0]))
        let duplicated = points.reversed() + points + [PixelPoint(x: -1, y: 0),
                                                       PixelPoint(x: width, y: height - 1)]
        let actual = saberCandidate(from: duplicated, width: width, height: height,
                                    evidence: evidence, collectEndpointDiagnostics: true)
        precondition(losslessSignature(canonical as Any) == losslessSignature(actual as Any))
    }
    precondition(saberCandidate(from: [], width: width, height: height) == nil)
    precondition(saberCandidate(from: [PixelPoint(x: -1, y: 0)], width: width, height: height) == nil)
    print("lossless proposal membership: duplicates, invalid coordinates, clipped-white bbox and frame edges passed")
}

// 同じテストソースを変更前・変更後の core とリンクし、出力を cmp で厳密に比較できる。
private func printLosslessSignatures(paths: [String]) {
    var state: UInt64 = 0x10_5de7ec72
    for index in 0..<48 {
        var image = StaticImage(width: 33 + index % 5, height: 41 + index % 7, padding: 13)
        for y in 0..<image.height {
            for x in 0..<image.width {
                state = state &* 6364136223846793005 &+ 1
                let value = UInt8(truncatingIfNeeded: state >> 32)
                image.pixel(x, y, red: value, green: UInt8(truncatingIfNeeded: state >> 40),
                            blue: UInt8(truncatingIfNeeded: state >> 48))
            }
        }
        image.bar(from: PixelPoint(x: index % 5, y: 0),
                  to: PixelPoint(x: image.width / 2, y: image.height - 1),
                  color: index % 2 == 0 ? .red : .blue)
        let thresholds = [ColorThreshold(), ColorThreshold(brightness: 0, dominance: 0, saturation: 0),
                          ColorThreshold(brightness: 255, dominance: 255, saturation: 255),
                          ColorThreshold(brightness: 110, dominance: 8, saturation: 10)]
        let step = [1, 2, 3, 8][index % 4]
        let result = staticAnalysis(image, step: step, threshold: thresholds[(index / 4) % 4])
        let profiled = staticAnalysis(image, step: step, threshold: thresholds[(index / 4) % 4], profile: true)
        precondition(frameSignature(result) == frameSignature(profiled))
        print("synthetic \(index):" + frameSignature(result) + profileCountSignature(profiled.profile!))
    }
    for path in paths {
        let image = loadStaticPNG(path)
        let analysis = staticAnalysis(image, profile: true)
        print(path + ":" + frameSignature(analysis) + profileCountSignature(analysis.profile!))
    }
}

@main
enum StaticBGRADetectionTests {
    static func main() {
        if CommandLine.arguments.count >= 3, CommandLine.arguments[1] == "--benchmark" {
            nativeBenchmark(path: CommandLine.arguments[2],
                            iterations: CommandLine.arguments.count > 3 ? Int(CommandLine.arguments[3])! : 100)
            return
        }
        if CommandLine.arguments.dropFirst().first == "--lossless-signatures" {
            printLosslessSignatures(paths: Array(CommandLine.arguments.dropFirst(2)))
            return
        }
        assertLosslessPixelScan()
        assertLosslessProposalMembership()
        assertLosslessMorphology()
        assertBlueNoDeepSupport()
        let cases: [(String, SaberColor, PixelPoint, PixelPoint, Int, Int)] = [
            ("red horizontal", .red, PixelPoint(x: 12, y: 20), PixelPoint(x: 92, y: 20), 128, 64),
            ("red vertical", .red, PixelPoint(x: 24, y: 8), PixelPoint(x: 24, y: 56), 128, 64),
            ("red positive diagonal", .red, PixelPoint(x: 12, y: 10), PixelPoint(x: 82, y: 50), 128, 64),
            ("red negative diagonal", .red, PixelPoint(x: 12, y: 52), PixelPoint(x: 82, y: 12), 128, 64),
            ("blue horizontal", .blue, PixelPoint(x: 30, y: 40), PixelPoint(x: 112, y: 40), 128, 64),
            ("blue vertical", .blue, PixelPoint(x: 92, y: 8), PixelPoint(x: 92, y: 56), 128, 64),
            ("blue positive diagonal", .blue, PixelPoint(x: 12, y: 8), PixelPoint(x: 112, y: 52), 128, 64),
            ("blue negative diagonal", .blue, PixelPoint(x: 12, y: 52), PixelPoint(x: 112, y: 8), 128, 64),
            ("red near edge", .red, PixelPoint(x: 3, y: 4), PixelPoint(x: 52, y: 4), 80, 48),
            ("blue near edge", .blue, PixelPoint(x: 26, y: 3), PixelPoint(x: 26, y: 44), 80, 48)
        ]
        for (name, color, start, end, width, height) in cases {
            var image = StaticImage(width: width, height: height, padding: 12)
            image.bar(from: start, to: end, color: color)
            let expectedAngle = atan2(Double(end.y - start.y), Double(end.x - start.x))
            assertCase(name, image: image, color: color, expected: (start, end), expectedAngle: expectedAngle)
        }

        var both = StaticImage(width: 160, height: 90, padding: 20)
        both.bar(from: PixelPoint(x: 10, y: 15), to: PixelPoint(x: 70, y: 65), color: .red)
        both.bar(from: PixelPoint(x: 92, y: 70), to: PixelPoint(x: 145, y: 12), color: .blue)
        assertCase("red and blue simultaneous", image: both, color: .red,
                   expected: (PixelPoint(x: 10, y: 15), PixelPoint(x: 70, y: 65)), expectedAngle: atan2(50, 60))
        assertCase("blue and red simultaneous", image: both, color: .blue,
                   expected: (PixelPoint(x: 92, y: 70), PixelPoint(x: 145, y: 12)), expectedAngle: atan2(-58, 53))

        var noisy = StaticImage(width: 140, height: 80, padding: 28)
        let noisyStart = PixelPoint(x: 15, y: 18), noisyEnd = PixelPoint(x: 116, y: 58)
        noisy.blurredBar(from: noisyStart, to: noisyEnd, color: .red)
        // Each reflection has area and includes even coordinates, so the
        // sampleStep=2 detector cannot skip the entire highlight.
        for point in [PixelPoint(x: 35, y: 26), PixelPoint(x: 75, y: 42)] {
            for y in point.y...point.y + 2 {
                for x in point.x...point.x + 2 {
                    noisy.pixel(x, y, red: 255, green: 255, blue: 255)
                }
            }
        }
        // A separated short patch has area on the sampleStep=2 grid but is not long enough.
        for y in stride(from: 28, through: 34, by: 2) {
            for x in stride(from: 124, through: 130, by: 2) {
                noisy.pixel(x, y, red: 245, green: 40, blue: 35)
            }
        }
        assertCase("blur reflection and separated noise", image: noisy, color: .red,
                   expected: (noisyStart, noisyEnd), expectedAngle: atan2(40, 101))

        var broken = StaticImage(width: 128, height: 64, padding: 8)
        let brokenStart = PixelPoint(x: 12, y: 32), brokenEnd = PixelPoint(x: 112, y: 32)
        broken.bar(from: brokenStart, to: brokenEnd, color: .blue, thickness: 4)
        // Two source pixels (one sampled-mask cell) are missing. Morphological
        // close should reconnect the blade before component scoring.
        for y in 26...38 {
            for x in 62...63 { broken.pixel(x, y, red: 0, green: 0, blue: 0) }
        }
        assertCase("blue blade with short gap", image: broken, color: .blue,
                   expected: (brokenStart, brokenEnd), expectedAngle: 0)

        // Reproduces the endpoint failure mode independently of candidate
        // ranking: a dense continuous blade body, a one-cell color-spill tail,
        // and a detached dense reflection on the same axis.
        var bodyWithTail: [PixelPoint] = []
        for x in 20...100 { for y in 45...55 { bodyWithTail.append(PixelPoint(x: x, y: y)) } }
        for x in 101...215 { bodyWithTail.append(PixelPoint(x: x, y: 50)) }
        for x in 216...228 { for y in 47...53 { bodyWithTail.append(PixelPoint(x: x, y: y)) } }
        guard let legacyEndpoints = principalAxisEndpoints(bodyWithTail),
              let trimmedCandidate = saberCandidate(from: bodyWithTail, width: 260, height: 100) else {
            fatalError("continuous-body endpoint regression: no candidate")
        }
        let legacyLength = hypot(Double(legacyEndpoints.1.x - legacyEndpoints.0.x),
                                 Double(legacyEndpoints.1.y - legacyEndpoints.0.y))
        let trimmedLength = hypot(Double(trimmedCandidate.endpoints.1.x - trimmedCandidate.endpoints.0.x),
                                  Double(trimmedCandidate.endpoints.1.y - trimmedCandidate.endpoints.0.y))
        guard legacyLength > 190, trimmedLength < 100,
              trimmedCandidate.longitudinalContinuity > 0.90,
              trimmedCandidate.retainedBodyRatio < 0.90 else {
            fatalError("continuous-body endpoint regression: legacy=\(legacyLength) trimmed=\(trimmedLength) continuity=\(trimmedCandidate.longitudinalContinuity) retained=\(trimmedCandidate.retainedBodyRatio)")
        }

        // Compact RED acceptance depends on strong emission evidence, not
        // compactness alone. Keep a strong control and a same-size moderate
        // reflection control; real captured short-blade positives are covered
        // by lossless-regression class F.
        var strongCompactRed = StaticImage(width: 128, height: 80)
        for y in 20...55 {
            for x in 42...77 { strongCompactRed.pixel(x, y, red: 245, green: 40, blue: 35) }
        }
        let compactRedThreshold = ColorThreshold(brightness: 140, dominance: 20)
        guard detectSaber(in: strongCompactRed.bytes, width: strongCompactRed.width,
                          height: strongCompactRed.height,
                          bytesPerRow: strongCompactRed.bytesPerRow, color: .red,
                          threshold: compactRedThreshold) != nil else {
            fatalError("compact red with strong emission evidence must remain detectable")
        }

        var weakCompactRed = strongCompactRed
        for y in 20...55 {
            for x in 42...77 { weakCompactRed.pixel(x, y, red: 214, green: 80, blue: 70) }
        }
        guard detectSaber(in: weakCompactRed.bytes, width: weakCompactRed.width,
                          height: weakCompactRed.height,
                          bytesPerRow: weakCompactRed.bytesPerRow, color: .red,
                          threshold: compactRedThreshold) == nil else {
            fatalError("weak-emission compact red reflection must not be detected as a saber")
        }

        guard matchesSaberHSV(245, 30, 60, color: .red,
                              threshold: ColorThreshold(brightness: 140, dominance: 20)),
              matchesSaberHSV(245, 60, 30, color: .red,
                              threshold: ColorThreshold(brightness: 140, dominance: 20)) else {
            fatalError("red hue wrap ranges must both be accepted")
        }
        guard !matchesSaberHSV(245, 245, 245, color: .red,
                               threshold: ColorThreshold(brightness: 140, dominance: 20)) else {
            fatalError("bright white must not pass the red HSV gate")
        }

        for color in [SaberColor.red, .blue] {
            var reflection = StaticImage(width: 180, height: 96, padding: 12)
            let channels: (UInt8, UInt8, UInt8) = color == .red
                ? (214, 80, 70) : (70, 80, 214)
            reflection.coloredBar(from: PixelPoint(x: 8, y: 70), to: PixelPoint(x: 168, y: 45),
                                  red: channels.0, green: channels.1, blue: channels.2,
                                  thickness: 3)
            guard detectSaber(in: reflection.bytes, width: reflection.width, height: reflection.height,
                              bytesPerRow: reflection.bytesPerRow, color: color,
                              threshold: ColorThreshold(brightness: 140, dominance: 20)) == nil else {
                fatalError("\(color) medium-bright reflection must not be detected")
            }

            var combined = reflection
            let realStart = PixelPoint(x: 22, y: 22)
            let realEnd = PixelPoint(x: 112, y: 22)
            combined.bar(from: realStart, to: realEnd, color: color, thickness: 4)
            for y in 16...28 {
                for x in 54...56 { combined.pixel(x, y, red: 0, green: 0, blue: 0) }
                for x in 84...86 { combined.pixel(x, y, red: 255, green: 255, blue: 255) }
            }
            assertCase("\(color) emitter beats longer reflection", image: combined, color: color,
                       expected: (realStart, realEnd), expectedAngle: 0, distanceTolerance: 8)

            var dotted = StaticImage(width: 140, height: 64, padding: 8)
            let dotChannels: (UInt8, UInt8, UInt8) = color == .red
                ? (248, 40, 32) : (32, 40, 248)
            for startX in stride(from: 14, through: 110, by: 8) {
                for y in 26...34 {
                    for x in startX...(startX + 4) {
                        dotted.pixel(x, y, red: dotChannels.0, green: dotChannels.1,
                                     blue: dotChannels.2)
                    }
                }
            }
            // A clipped pixel inside one LED dot must not split the array.
            dotted.pixel(66, 30, red: 255, green: 255, blue: 255)
            assertCase("\(color) dotted LED array", image: dotted, color: color,
                       expected: (PixelPoint(x: 14, y: 30), PixelPoint(x: 114, y: 30)),
                       expectedAngle: 0, distanceTolerance: 8)

            var paper = StaticImage(width: 280, height: 120, padding: 16)
            let paperStart = PixelPoint(x: 22, y: 58)
            let paperEnd = PixelPoint(x: 104, y: 58)
            paper.diffusedPaperBlade(from: paperStart, to: paperEnd, color: color)
            // Low-density spill can remain in the HSV mask between the blade
            // and a visually separate reflection. It is not continuous blade body.
            paper.coloredBar(from: PixelPoint(x: 112, y: 58),
                             to: PixelPoint(x: 232, y: 58),
                             red: color == .red ? 160 : 45,
                             green: 58,
                             blue: color == .blue ? 160 : 45,
                             thickness: 0)
            // A small bright reflection lies on the same infinite axis but is
            // separated by a large unsupported gap. It must not extend either endpoint.
            paper.coloredBar(from: PixelPoint(x: 232, y: 58),
                             to: PixelPoint(x: 244, y: 58),
                             red: color == .red ? 245 : 45,
                             green: 75,
                             blue: color == .blue ? 245 : 45,
                             thickness: 3)
            assertCase("\(color) paper diffuser ignores separated axial reflection",
                       image: paper, color: color,
                       expected: (PixelPoint(x: 13, y: 58), PixelPoint(x: 113, y: 58)),
                       expectedAngle: 0, distanceTolerance: 10)

            var nearby = StaticImage(width: 190, height: 110)
            let nearStart = PixelPoint(x: 24, y: 34), nearEnd = PixelPoint(x: 136, y: 34)
            nearby.diffusedPaperBlade(from: nearStart, to: nearEnd, color: color, thickness: 5)
            nearby.coloredBar(from: PixelPoint(x: 76, y: 54), to: PixelPoint(x: 120, y: 60),
                              red: color == .red ? 205 : 55, green: 70,
                              blue: color == .blue ? 205 : 55, thickness: 2)
            assertCase("\(color) paper diffuser beats nearby same-color region",
                       image: nearby, color: color,
                       expected: (PixelPoint(x: 16, y: 34), PixelPoint(x: 144, y: 34)),
                       expectedAngle: 0, distanceTolerance: 10)

            var motion = StaticImage(width: 180, height: 100)
            let motionStart = PixelPoint(x: 18, y: 72), motionEnd = PixelPoint(x: 150, y: 28)
            motion.blurredBar(from: motionStart, to: motionEnd, color: color)
            assertCase("\(color) paper-like motion blur", image: motion, color: color,
                       expected: (motionStart, motionEnd),
                       expectedAngle: atan2(-44, 132), distanceTolerance: 9,
                       lengthTolerance: 16)

            var short = StaticImage(width: 120, height: 90)
            let shortStart = PixelPoint(x: 48, y: 35), shortEnd = PixelPoint(x: 72, y: 49)
            short.diffusedPaperBlade(from: shortStart, to: shortEnd, color: color, thickness: 3)
            assertCase("\(color) foreshortened paper blade", image: short, color: color,
                       expected: (shortStart, shortEnd), expectedAngle: atan2(14, 24),
                       distanceTolerance: 10, angleTolerance: 0.24)
        }

        var crossing = StaticImage(width: 190, height: 120, padding: 8)
        let crossingRed = (PixelPoint(x: 22, y: 20), PixelPoint(x: 156, y: 98))
        let crossingBlue = (PixelPoint(x: 22, y: 98), PixelPoint(x: 156, y: 20))
        crossing.diffusedPaperBlade(from: crossingRed.0, to: crossingRed.1, color: .red, thickness: 4)
        crossing.diffusedPaperBlade(from: crossingBlue.0, to: crossingBlue.1, color: .blue, thickness: 4)
        assertCase("crossing red paper blade", image: crossing, color: .red,
                   expected: crossingRed, expectedAngle: atan2(78, 134), distanceTolerance: 10,
                   lengthTolerance: 16)
        assertCase("crossing blue paper blade", image: crossing, color: .blue,
                   expected: crossingBlue, expectedAngle: atan2(-78, 134), distanceTolerance: 10,
                   lengthTolerance: 16)

        var empty = StaticImage(width: 72, height: 41, padding: 16)
        for y in stride(from: 4, through: 8, by: 2) {
            for x in stride(from: 4, through: 8, by: 2) {
                empty.pixel(x, y, red: 250, green: 40, blue: 35)
            }
        }
        for y in stride(from: 30, through: 34, by: 2) {
            for x in stride(from: 50, through: 54, by: 2) {
                empty.pixel(x, y, red: 40, green: 45, blue: 250)
            }
        }
        empty.pixel(50, 30, red: 255, green: 255, blue: 255)
        for color in [SaberColor.red, .blue] {
            guard detectSaber(in: empty.bytes, width: empty.width, height: empty.height,
                              bytesPerRow: empty.bytesPerRow, color: color,
                              threshold: ColorThreshold(brightness: 140, dominance: 20)) == nil else {
                fatalError("short noise must not be detected as \(color)")
            }
        }
        // sampleStep=2 quantizes coordinates by two pixels; this tolerance covers
        // No temporal history is allowed to delay a real, large position jump.
        for color in [SaberColor.red, .blue] {
            for x in [24, 132, 40, 156, 24] {
                var moving = StaticImage(width: 200, height: 120)
                let a = PixelPoint(x: x, y: 20), b = PixelPoint(x: x, y: 100)
                moving.bar(from: a, to: b, color: color, thickness: 4)
                assertCase("immediate moving \(color) x=\(x)", image: moving, color: color,
                           expected: (a, b), expectedAngle: .pi / 2)
            }
        }
        print("Fast movement: 10 frames, immediate single-frame detection passed")
        // sampleStep=2 quantizes coordinates by two pixels; this tolerance covers
        // three samples plus the five-pixel cap. 0.20 radians rejects a 90-degree error.
        print("Static BGRA detection tests passed")
    }
}
