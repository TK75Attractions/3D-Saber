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
    var cases = 0
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
        cases += 1
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
    // SIMD の開始幅・16画素境界・端数を、密な非二値 mask と各 lane の穴でも照合する。
    for width in [17, 18, 19, 20, 31, 32, 33, 34, 35, 36, 49, 50] {
        let height = 9
        check(Array(repeating: 255, count: width * height), width: width, height: height)
        for lane in 0..<16 {
            var mask = Array(repeating: UInt8(127), count: width * height)
            mask[4 * width + 1 + lane] = 0
            mask[2 * width + width - 2] = 0
            check(mask, width: width, height: height)
        }
    }
    // ゼロ探索の8画素境界・非整列の行頭・最終行の端数を独立した参照実装で照合する。
    for width in [7, 8, 9, 15, 16, 17, 23, 24, 25, 63, 64, 65] {
        let height = 5, count = width * height
        check(Array(repeating: 0, count: count), width: width, height: height)
        check(Array(repeating: 127, count: count), width: width, height: height)
        for x in 0..<width {
            var mask = Array(repeating: UInt8(0), count: count)
            for y in 0..<height { mask[y * width + x] = [1, 127, 255][y % 3] }
            check(mask, width: width, height: height)
        }
        for start in [0, 7, 8] where start < width {
            var mask = Array(repeating: UInt8(0), count: count)
            for y in 0..<height {
                for x in start..<width { mask[y * width + x] = [1, 127, 255][x % 3] }
            }
            check(mask, width: width, height: height)
        }
        for density in [1, 3, 7] {
            let mask: [UInt8] = (0..<count).map { _ in
                state = state &* 6364136223846793005 &+ 1
                return (state >> 32) % 8 < density ? UInt8(truncatingIfNeeded: state) | 1 : 0
            }
            check(mask, width: width, height: height)
        }
    }
    // 収縮の中心ブロックだけがゼロで周囲は非ゼロ、または境界付近の1画素だけが非ゼロ。
    // 半径1/2の先頭・次ブロック、非整列行頭と端数を、値1/127/255で照合する。
    for width in [18, 19, 20, 33, 34, 35, 36, 49, 50, 51] {
        let height = 9, count = width * height
        for value: UInt8 in [1, 127, 255] {
            for start in [1, 2, 17, 18] where start + 16 < width {
                var mask = Array(repeating: value, count: count)
                for x in start..<(start + 16) { mask[4 * width + x] = 0 }
                check(mask, width: width, height: height)
            }
            for x in [0, 1, 2, 16, 17, 18, width - 2, width - 1] where x < width {
                var mask = Array(repeating: UInt8(0), count: count)
                mask[4 * width + x] = value
                check(mask, width: width, height: height)
            }
        }
    }
    print("lossless morphology: \(cases) masks, diamond radii 0...4, SIMD/zero-block lanes/tails, edges and nonbinary inputs passed")
}

// PNG 読込や段階計測から独立した、小規模な膨張・close・open のホスト計測。
private func nativeMorphologyBenchmark(iterations: Int) {
    precondition(iterations > 0)
    let width = 240, height = 320, count = width * height
    for shape in ["zero", "sparse", "dense"] {
        var mask = Array(repeating: UInt8(shape == "dense" ? 127 : 0), count: count)
        if shape == "sparse" {
            for y in 40..<280 {
                for x in 116..<124 { mask[y * width + x] = 255 }
            }
        }
        for operation in ["dilate", "close", "open"] {
            func run() -> [UInt8] {
                switch operation {
                case "dilate": return dilateSaberMask(mask, width: width, height: height, radius: 1)
                case "close": return closeSaberMask(mask, width: width, height: height, radius: 1)
                default: return openSaberMask(mask, width: width, height: height, radius: 1)
                }
            }
            for _ in 0..<5 { _ = run() }
            var samples: [Double] = [], checksum = 0
            for iteration in 0..<iterations {
                let start = ProcessInfo.processInfo.systemUptime
                let result = run()
                samples.append((ProcessInfo.processInfo.systemUptime - start) * 1000)
                checksum += Int(result[(iteration * 997) % count])
            }
            let record: [String: Any] = ["shape": shape, "operation": operation,
                "width": width, "height": height, "radius": 1, "samplesMs": samples, "checksum": checksum]
            print("[MorphologyJSON] " + String(data: try! JSONSerialization.data(withJSONObject: record,
                options: [.sortedKeys]), encoding: .utf8)!)
        }
    }
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
    }.joined(separator: "\n") + losslessSignature(analysis.trackLockSuppressed)
}

private func profileCountSignature(_ profile: SaberDetectionProfile) -> String {
    losslessSignature([profile.colorPixelCount, profile.brightCorePixelCount,
                       profile.lineProposalCount, profile.candidateCount, profile.connectedComponentCount])
}

private func staticAnalysis(_ image: StaticImage, step: Int = 2,
                            threshold: ColorThreshold = ColorThreshold(),
                            profile: Bool = false,
                            mode: ShutterEmitterMode = ShutterEmitterMode()) -> SaberFrameAnalysis {
    analyzeSabers(in: image.bytes, width: image.width, height: image.height,
                  bytesPerRow: image.bytesPerRow, redThreshold: threshold, blueThreshold: threshold,
                  sampleStep: step, collectProfile: profile, collectPipelineDiagnostics: true, shutterEmitter: mode)
}

private func nativeBenchmark(path: String, iterations: Int, mode: ShutterEmitterMode, step: Int) {
    precondition(iterations > 0)
    let bright = loadStaticPNG(path)
    let blank = StaticImage(width: bright.width, height: bright.height)
    let threshold = ColorThreshold()
    for (name, image) in [("empty", blank), ("bright", bright)] {
        func run(profile: Bool = false) -> SaberFrameAnalysis {
            analyzeSabers(in: image.bytes, width: image.width, height: image.height,
                          bytesPerRow: image.bytesPerRow, redThreshold: threshold,
                          blueThreshold: threshold, sampleStep: step, collectProfile: profile, shutterEmitter: mode)
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
        print(String(format: "[NativeTiming] %@ %dx%d n=%d avg=%.3f median=%.3f p95=%.3f max=%.3f ms candidates=%d",
                     name, image.width, image.height, iterations,
                     samples.reduce(0, +) / Double(iterations), ordered[iterations / 2],
                     ordered[Int(ceil(Double(iterations) * 0.95)) - 1], ordered.last!, candidateCount))
        let audit = staticAnalysis(image, step: step, mode: mode)
        let skipped = emptyNormalLowISOColors(image, step: step, mode: mode, analysis: audit)
        let record: [String: Any] = ["image": path, "case": name, "width": image.width,
            "height": image.height, "shutter": mode.shutter, "iso": mode.iso,
            "emitter": mode.enabled, "active": mode.active, "sampleStep": step,
            "samplesMs": samples, "emptyNormalLowISOColors": skipped,
            "candidateCount": candidateCount]
        print("[NativeJSON] " + String(data: try! JSONSerialization.data(withJSONObject: record, options: [.sortedKeys]), encoding: .utf8)!)
        var scan = 0.0, morphology = 0.0, components = 0.0, evidence = 0.0, traversal = 0.0
        var morphologySamples: [Double] = []
        var lineProposal = 0.0, lineScore = 0.0, shape = 0.0, endpoints = 0.0
        for _ in 0..<iterations {
            let profile = run(profile: true).profile!
            scan += profile.pixelScanHSVMaskMs; morphology += profile.morphologyMs
            morphologySamples.append(profile.morphologyMs)
            components += profile.componentAndScoreMs; evidence += profile.brightnessContrastColorMs
            traversal += profile.connectedComponentsMs
            shape += profile.shapeAndAxisMs; endpoints += profile.endpointAndBoundsMs
            lineProposal += profile.lineProposalMs; lineScore += profile.lineScoreMs
        }
        let n = Double(iterations)
        let profileRecord: [String: Any] = ["image": path, "case": name, "samplesMs": morphologySamples]
        print("[MorphologyProfileJSON] " + String(data: try! JSONSerialization.data(withJSONObject: profileRecord,
            options: [.sortedKeys]), encoding: .utf8)!)
        print(String(format: "[NativeStages] %@ scanMask=%.3f morphology=%.3f components=%.3f BFS=%.3f shapeAxis=%.3f evidence=%.3f endpoints=%.3f lineProposal=%.3f lineScore=%.3f ms",
                     name, scan / n, morphology / n, components / n, traversal / n,
                     shape / n, evidence / n, endpoints / n, lineProposal / n, lineScore / n))
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

// 菱形の元の y→x 走査を独立に再現し、端/内部と全点保持の診断を検証する。
private func assertLosslessScoringScratch() {
    let width = 73, height = 59
    for x in [0, 1, 2, 31, width - 3, width - 2, width - 1] {
        var points: [PixelPoint] = []
        var mask = Array(repeating: UInt8(0), count: width * height)
        var value = mask, chroma = mask
        for y in 0..<height {
            for xx in x...min(width - 1, x + 4) {
                points.append(PixelPoint(x: xx, y: y))
                let index = y * width + xx
                mask[index] = 1; value[index] = 250; chroma[index] = 160
            }
        }
        for y in 0..<height {
            for xx in 0..<width where mask[y * width + xx] == 0 && (xx + y * 3) % 13 == 0 {
                value[y * width + xx] = 255; chroma[y * width + xx] = 45
            }
        }
        var seen = Array(repeating: false, count: mask.count), clippedCount = 0
        for point in points {
            for dy in -2...2 {
                for dx in -2...2 where abs(dx) + abs(dy) <= 2 {
                    let xx = point.x + dx, yy = point.y + dy
                    guard xx >= 0, xx < width, yy >= 0, yy < height else { continue }
                    let index = yy * width + xx
                    if mask[index] == 0 && value[index] >= 245 && chroma[index] <= 45 && !seen[index] {
                        seen[index] = true; clippedCount += 1
                    }
                }
            }
        }
        let evidence = SaberEvidence(color: .blue, value: value, chroma: chroma,
                                     colorMask: mask, coreMask: mask)
        let candidate = saberCandidate(from: points, width: width, height: height,
                                      evidence: evidence, collectEndpointDiagnostics: true)!
        let expectedRatio = min(Double(clippedCount) / Double(points.count + clippedCount), 0.25) / 0.25
        precondition(candidate.clippedWhiteRatio.bitPattern == expectedRatio.bitPattern)
        precondition(candidate.retainedBodyRatio == 1)
        precondition(candidate.endpointDiagnosticTrace!.bodyPointCount == points.count)
        precondition(candidate.usedPointLEDFallback)
    }
    var trimmed: [PixelPoint] = []
    for x in 20...100 { for y in 45...55 { trimmed.append(PixelPoint(x: x, y: y)) } }
    for x in 101...215 { trimmed.append(PixelPoint(x: x, y: 50)) }
    for x in 216...228 { for y in 47...53 { trimmed.append(PixelPoint(x: x, y: y)) } }
    let candidate = saberCandidate(from: trimmed, width: 260, height: 100,
                                  collectEndpointDiagnostics: true)!
    precondition(candidate.retainedBodyRatio < 0.85 && !candidate.usedPointLEDFallback)
    precondition(candidate.endpointDiagnosticTrace!.bodyEndpoints != nil)
    precondition(candidate.endpointDiagnosticTrace!.bodyPointCount < trimmed.count)
    print("lossless scoring scratch: clipped-white diamond at edges/interior, retained count and trimmed PCA passed")
}

// seed 走査の境界・非二値入力を、独立した画素単位 BFS と旧 Core の署名で検証する。
private func assertSeedScan(signatures: Bool = false) {
    var cases = 0
    for (width, height) in [(1, 1), (7, 1), (8, 1), (9, 1), (17, 3), (31, 19), (64, 32), (73, 59)] {
        for value: UInt8 in [1, 127, 255] {
            for pattern in 0..<8 {
                var mask = Array(repeating: UInt8(0), count: width * height)
                for y in 0..<height { for x in 0..<width {
                    let i = y * width + x
                    let active: Bool
                    switch pattern {
                    case 0: active = false
                    case 1: active = true
                    case 2: active = x % 4 == 0 && y % 4 == 0
                    case 3: active = x == y
                    case 4: active = y == height / 2 || x == width / 2
                    case 5: active = i == mask.count - 1
                    case 6: active = (y == 2 || y == height - 3) && x >= 2 && x < width - 2
                    default: active = (i &* 37 &+ y &* 13) % 101 < 17
                    }
                    if active { mask[i] = value }
                } }
                let original = mask
                var reference = mask, sizes: [Int] = []
                for seed in reference.indices where reference[seed] != 0 {
                    reference[seed] = 0
                    var queue = [seed], head = 0
                    while head < queue.count {
                        let i = queue[head]; head += 1
                        for y in max(0, i / width - 1)...min(height - 1, i / width + 1) {
                            for x in max(0, i % width - 1)...min(width - 1, i % width + 1) {
                                let j = y * width + x
                                if reference[j] != 0 { reference[j] = 0; queue.append(j) }
                            }
                        }
                    }
                    sizes.append(queue.count)
                }
                for color: SaberColor? in [nil, .red, .blue] {
                    let evidence = color.map { SaberEvidence(color: $0,
                        radiance: Array(repeating: 120, count: mask.count),
                        value: Array(repeating: 250, count: mask.count),
                        chroma: Array(repeating: 140, count: mask.count),
                        colorMask: mask, coreMask: mask, collectObserverEvidence: true) }
                    var observed: [Int] = []
                    let candidates = saberCandidates(in: mask, width: width, height: height,
                        evidence: evidence, componentObserver: { observed.append($0) },
                        collectEndpointDiagnostics: true)
                    precondition(observed == sizes && mask == original)
                    var profiledObserved: [Int] = []
                    let profiled = saberCandidates(in: mask, width: width, height: height,
                        evidence: evidence, stageProfile: SaberCandidateStageProfile(),
                        componentObserver: { profiledObserved.append($0) }, collectEndpointDiagnostics: true)
                    precondition(profiledObserved == observed)
                    precondition(losslessSignature(candidates) == losslessSignature(profiled))
                    if signatures {
                        print("seed \(width)x\(height) value=\(value) pattern=\(pattern) color=\(String(describing: color)):"
                            + losslessSignature(candidates) + losslessSignature(observed))
                    }
                    cases += 1
                }
            }
        }
    }
    print("Seed scan: \(cases) cases, zero/dense/isolated/diagonal/boundary/tail/ties, values 1/127/255, observer and profile PASS")
}

// 同じテストソースを変更前・変更後の core とリンクし、出力を cmp で厳密に比較できる。
private func printLosslessSignatures(paths: [String], mode: ShutterEmitterMode, step: Int) {
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
        let result = staticAnalysis(image, step: step, threshold: thresholds[(index / 4) % 4], mode: mode)
        let profiled = staticAnalysis(image, step: step, threshold: thresholds[(index / 4) % 4], profile: true, mode: mode)
        precondition(frameSignature(result) == frameSignature(profiled))
        print("synthetic \(index):" + frameSignature(result) + profileCountSignature(profiled.profile!))
    }
    for index in 0..<24 {
        var image = StaticImage(width: 97, height: 73, padding: 13)
        let x = [0, 1, 2, 48, 94, 96][index % 6]
        let color: SaberColor = index % 2 == 0 ? .red : .blue
        image.diffusedPaperBlade(from: PixelPoint(x: x, y: 0),
                                 to: PixelPoint(x: x, y: 72), color: color, thickness: 3)
        for y in stride(from: 0, to: image.height, by: 7) {
            image.pixel(x, y, red: 255, green: 255, blue: 255)
        }
        image.bar(from: PixelPoint(x: 10, y: 50), to: PixelPoint(x: 80, y: 20),
                  color: color, thickness: 2)
        let result = staticAnalysis(image, step: 1 + index / 12, profile: true, mode: mode)
        print("scoring-scratch \(index):" + frameSignature(result) + profileCountSignature(result.profile!))
    }
    for path in paths {
        let image = loadStaticPNG(path)
        let analysis = staticAnalysis(image, step: step, profile: true, mode: mode)
        print(path + ":" + frameSignature(analysis) + profileCountSignature(analysis.profile!))
    }
}

private func assertCorelessBlueSurface() {
    let width = 100, height = 80
    let points = (30...34).flatMap { y in (20...70).map { PixelPoint(x: $0, y: y) } }
    func candidate(peak: UInt8, core: UInt8) -> SaberCandidate {
        let evidence = SaberEvidence(color: .blue, radiance: [],
            value: Array(repeating: peak, count: width * height),
            chroma: Array(repeating: 140, count: width * height),
            colorMask: Array(repeating: 1, count: width * height),
            coreMask: Array(repeating: core, count: width * height))
        return saberCandidate(from: points, width: width, height: height,
                              evidence: evidence, source: "color-emitter")!
    }
    precondition(isCorelessBlueSurface(candidate(peak: 237, core: 0)))
    // 低輝度 LED の既存 connected/color 候補や、飽和した純青 LED は残す。
    var connected = candidate(peak: 237, core: 0)
    connected.source = "color-mask"
    precondition(!isCorelessBlueSurface(connected))
    precondition(!isCorelessBlueSurface(candidate(peak: 242, core: 0)))
    precondition(!isCorelessBlueSurface(candidate(peak: 237, core: 1)))
    print("Coreless blue surface: diffuse emission slice rejected; connected, clipped and supported emitters preserved")
}

// 通常 mask の実測診断と低ISO画素の独立走査で、省略条件の色数を数える（計測外）。
private func emptyNormalLowISOColors(_ image: StaticImage, step: Int, mode: ShutterEmitterMode,
                                     analysis: SaberFrameAnalysis) -> [String] {
    guard mode.active else { return [] }
    var pixels: [(UInt8, UInt8, UInt8)] = []
    var values: [Int] = []
    for y in stride(from: 0, to: image.height, by: step) {
        for x in stride(from: 0, to: image.width, by: step) {
            let i = y * image.bytesPerRow + x * 4
            let rgb = (image.bytes[i + 2], image.bytes[i + 1], image.bytes[i])
            pixels.append(rgb); values.append(Int(max(rgb.0, max(rgb.1, rgb.2))))
        }
    }
    let background = values.sorted()[values.count / 2]
    guard background <= ShutterEmitterThresholds.maximumBackground else { return [] }
    let floor = max(ShutterEmitterThresholds.minimumValue,
                    background * ShutterEmitterThresholds.minimumBackgroundRatio)
    var colors: Set<SaberColor> = []
    for (i, rgb) in pixels.enumerated() {
        let value = values[i], chroma = value - Int(min(rgb.0, min(rgb.1, rgb.2)))
        guard value >= floor, value <= ShutterEmitterThresholds.maximumValue,
              Double(chroma) / Double(max(value, 1)) >= ShutterEmitterThresholds.minimumPurity else { continue }
        let hue = saberHSV(rgb.0, rgb.1, rgb.2).hue
        if hue <= ShutterEmitterThresholds.redHueEdge || hue >= 360 - ShutterEmitterThresholds.redHueEdge { colors.insert(.red) }
        if ShutterEmitterThresholds.blueHueRange.contains(hue) { colors.insert(.blue) }
    }
    return [SaberColor.red, .blue].filter {
        colors.contains($0) && analysis.pipelineDiagnostics?[$0]?.maskPixelCount == 0
    }.map { String(describing: $0) }
}

// 同じソースを前後双方にリンクする。全 stored property・順序・Double bitPattern を比較する。
private func assertEmptyNormalMasks(mode: ShutterEmitterMode, signatures: Bool = false) {
    var count = 0
    for padding in [0, 13] { for step in [1, 2, 3] {
        let empty = StaticImage(width: 321, height: 241, padding: padding)
        var red = empty, blue = empty, both = empty, mixed = empty, rejected = empty
        red.coloredBar(from: PixelPoint(x: 40, y: 60), to: PixelPoint(x: 220, y: 60), red: 96, green: 8, blue: 8, thickness: 2)
        blue.coloredBar(from: PixelPoint(x: 70, y: 170), to: PixelPoint(x: 270, y: 170), red: 8, green: 16, blue: 96, thickness: 2)
        both = red
        both.coloredBar(from: PixelPoint(x: 70, y: 170), to: PixelPoint(x: 270, y: 170), red: 8, green: 16, blue: 96, thickness: 2)
        mixed = both
        for color in [SaberColor.red, .blue] {
            mixed.bar(from: PixelPoint(x: 30, y: color == .red ? 110 : 210),
                      to: PixelPoint(x: 230, y: color == .red ? 110 : 210), color: color, thickness: 3)
        }
        rejected.coloredBar(from: PixelPoint(x: 100, y: 80), to: PixelPoint(x: 100, y: 80), red: 96, green: 8, blue: 8, thickness: 12)
        rejected.coloredBar(from: PixelPoint(x: 200, y: 160), to: PixelPoint(x: 200, y: 160), red: 8, green: 16, blue: 96, thickness: 12)
        let cases = [("red", red), ("blue", blue), ("both", both), ("mixed", mixed), ("rejected", rejected), ("empty", empty)]
        for (name, image) in cases {
            let result = staticAnalysis(image, step: step, profile: true, mode: mode)
            let normal = staticAnalysis(image, step: step)
            if ["red", "blue", "both", "rejected", "empty"].contains(name) {
                precondition(normal.candidates.values.allSatisfy { $0.isEmpty })
                precondition(result.candidates.values.allSatisfy { $0.allSatisfy { $0.source == "shutter-color-emitter" } })
                precondition(result.pipelineDiagnostics!.values.allSatisfy {
                    $0.maskPixelCount == 0 && $0.morphologyPixelCount == 0 && $0.connectedComponentCount == 0
                })
                precondition(normal.pipelineDiagnostics!.values.allSatisfy {
                    $0.maskPixelCount == 0 && $0.morphologyPixelCount == 0 && $0.connectedComponentCount == 0
                })
            }
            if mode.active {
                let expected = name == "red" ? ["red"] : name == "blue" ? ["blue"]
                    : ["both", "rejected"].contains(name) ? ["red", "blue"] : []
                precondition(emptyNormalLowISOColors(image, step: step, mode: mode, analysis: result) == expected)
                if name == "rejected" || name == "empty" {
                    precondition(result.candidates.values.allSatisfy { $0.isEmpty })
                } else {
                    for color in [SaberColor.red, .blue] where name == "both" || name == "mixed" || name == String(describing: color) {
                        precondition(result.candidates[color]!.contains { $0.source == "shutter-color-emitter" })
                        precondition(result.selected[color] != nil)
                    }
                }
            }
            if signatures {
                print("case \(name) padding=\(padding) step=\(step):" + frameSignature(result) + profileCountSignature(result.profile!))
                for tracking in [false, true] { for constraint in 0...2 {
                    let lock = tracking ? SaberTrackLock() : nil
                    let zones: [SaberColor: [SaberIgnoreZone]] = constraint == 1
                        ? [.red: [SaberIgnoreZone(minX: 0, minY: 0, maxX: 1, maxY: 1)],
                           .blue: [SaberIgnoreZone(minX: 0, minY: 0, maxX: 1, maxY: 1)]] : [:]
                    let area = constraint == 2 ? SaberPlayArea(minX: 0.8, minY: 0.8, maxX: 1, maxY: 1) : nil
                    // 60fps の短い欠落・3回欠落後の取得・0.25秒を超えた再取得を分けて比較する。
                    let sequence = [image, image, empty, image, empty, empty, empty, image, empty, image]
                    let times: [Double] = [0, 1.0 / 60, 2.0 / 60, 3.0 / 60, 4.0 / 60,
                                 5.0 / 60, 6.0 / 60, 7.0 / 60, 0.5, 0.52]
                    for (frame, scene) in sequence.enumerated() {
                        let analysis = analyzeSabers(in: scene.bytes, width: scene.width, height: scene.height,
                            bytesPerRow: scene.bytesPerRow, redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(),
                            sampleStep: step, collectProfile: true, collectPipelineDiagnostics: true,
                            collectObserverEvidence: true, ignoreZones: zones, playArea: area,
                            trackLock: lock, now: times[frame], shutterEmitter: mode)
                        print("sequence \(name) padding=\(padding) step=\(step) tracking=\(tracking) constraint=\(constraint) frame=\(frame) now=\(times[frame]):"
                            + frameSignature(analysis) + profileCountSignature(analysis.profile!))
                    }
                } }
            }
            count += 1
        }
    } }
    print("Empty normal masks: \(count) cases, odd sizes / padding / sampleStep, lowISO accepted/rejected PASS")
}

@main
enum StaticBGRADetectionTests {
    static func main() {
        var arguments = Array(CommandLine.arguments.dropFirst())
        var mode = ShutterEmitterMode()
        var step = 2
        for option in ["--shutter", "--iso", "--emitter", "--step"] {
            if let index = arguments.firstIndex(of: option) {
                precondition(index + 1 < arguments.count)
                let value = arguments[index + 1]
                switch option {
                case "--shutter": mode.shutter = Int(value)!
                case "--iso": mode.iso = Int(value)!
                case "--step": step = Int(value)!; precondition(step > 0)
                default: precondition(["on", "off"].contains(value)); mode.enabled = value == "on"
                }
                arguments.removeSubrange(index...index + 1)
            }
        }
        if arguments.first == "--benchmark" || arguments.first == "--lossless-signatures"
            || arguments.first == "--empty-normal-signatures" {
            print("[Settings] shutter=\(mode.shutter) iso=\(mode.iso) emitter=\(mode.enabled) active=\(mode.active) pngSampleStep=\(step) emptyNormalSteps=1,2,3")
        }
        if arguments.first == "--seed-scan-signatures" {
            assertSeedScan(signatures: true)
            return
        }
        if arguments.first == "--benchmark" {
            nativeBenchmark(path: arguments[1], iterations: arguments.count > 2 ? Int(arguments[2])! : 100,
                            mode: mode, step: step)
            return
        }
        if arguments.first == "--morphology-benchmark" {
            nativeMorphologyBenchmark(iterations: arguments.count > 1 ? Int(arguments[1])! : 100)
            return
        }
        if arguments.first == "--lossless-signatures" {
            printLosslessSignatures(paths: Array(arguments.dropFirst()), mode: mode, step: step)
            return
        }
        if arguments.first == "--empty-normal-signatures" {
            assertEmptyNormalMasks(mode: mode, signatures: true)
            return
        }
        precondition(arguments.isEmpty, "unknown arguments")
        assertEmptyNormalMasks(mode: ShutterEmitterMode(shutter: 2000, iso: 160, enabled: true))
        assertSeedScan()
        assertLosslessPixelScan()
        assertLosslessProposalMembership()
        assertLosslessScoringScratch()
        assertLosslessMorphology()
        assertBlueNoDeepSupport()
        assertCorelessBlueSurface()
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
        // C++ core_test と同じ2本の棒・時刻。赤青とも、遠い高得点へ飛ばない。
        for color in [SaberColor.red, .blue] {
            func image(_ near: Bool, _ far: Bool, nearX: Int = 80, nearY: Int = 200) -> StaticImage {
                var result = StaticImage(width: 640, height: 480)
                func bar(_ x: Int, _ y1: Int, _ y2: Int) {
                    for y in y1...y2 { for dx in -4...4 {
                        result.pixel(x + dx, y, red: color == .red ? 245 : 30, green: 45,
                                     blue: color == .blue ? 245 : 30)
                    } }
                }
                if near { bar(nearX, nearY, nearY + 70) }
                if far { bar(540, 160, 270) }
                return result
            }
            let state = SaberTrackLock()
            let threshold = ColorThreshold(brightness: 140, dominance: 20)
            func analyze(_ scene: StaticImage, _ now: Double, _ lock: SaberTrackLock?,
                         zones: [SaberColor: [SaberIgnoreZone]] = [:], area: SaberPlayArea? = nil,
                         width: Int = 640) -> SaberFrameAnalysis {
                analyzeSabers(in: scene.bytes, width: width, height: scene.height, bytesPerRow: scene.bytesPerRow,
                              redThreshold: threshold, blueThreshold: threshold, ignoreZones: zones, playArea: area,
                              trackLock: lock, now: now)
            }
            let near = image(true, false), both = image(true, true), far = image(false, true)
            precondition(analyze(near, 1, state).selected[color]!.0.x < 100)
            let legacy = analyze(both, 1.02, nil), kept = analyze(both, 1.02, state)
            precondition(legacy.selected[color]!.0.x > 500 && kept.selected[color]!.0.x < 100)
            precondition(losslessSignature(legacy.candidates) == losslessSignature(kept.candidates))
            let off = analyzeSabers(in: both.bytes, width: both.width, height: both.height,
                                    bytesPerRow: both.bytesPerRow, redThreshold: threshold, blueThreshold: threshold)
            precondition(frameSignature(off) == frameSignature(legacy))
            for missing in 1...3 {
                let absent = analyze(far, 1.02 + Double(missing) * 0.02, state)
                precondition(absent.selected[color] == nil && state.suppressed.contains(color))
            }
            precondition(analyze(far, 1.10, state).selected[color]!.0.x > 500)
            state.reset(); _ = analyze(near, 1, state)
            for missing in 1...3 {
                precondition(analyze(image(false, false), 1 + Double(missing) * 0.02, state).selected[color] == nil)
                precondition(state.suppressed.contains(color))
            }
            precondition(analyze(both, 1.08, state).selected[color]!.0.x > 500)
            state.reset(); _ = analyze(near, 2, state)
            precondition(analyze(both, 2.251, state).selected[color]!.0.x > 500)
            state.reset(); _ = analyze(near, 3, state)
            precondition(analyze(both, 3.02, state, width: 639).selected[color]!.0.x > 500)
            state.reset(); _ = analyze(near, 4, state); state.reset()
            precondition(analyze(both, 4.02, state).selected[color]!.0.x > 500)
            state.reset(); let seed = analyze(near, 5, state).selected[color]!
            let zone = SaberIgnoreZone(around: seed, width: 640, height: 480)
            precondition(analyze(both, 5.02, state, zones: [color: [zone]]).selected[color] == nil)
            state.reset(); _ = analyze(near, 6, state)
            precondition(analyze(both, 6.02, state, area: SaberPlayArea(minX: 0.7, minY: 0, maxX: 1, maxY: 1)).selected[color]!.0.x < 100)
            state.reset(); _ = analyze(near, 7, state)
            _ = analyze(image(true, false, nearX: 100), 7.02, state)
            precondition(analyze(image(true, false, nearX: 220), 7.14, state).selected[color] != nil)
            // 上80%で取得して約0.9まで下げても追跡し、OFFでは従来通り範囲外を選ばない。
            let area = SaberPlayArea(minX: 0, minY: 0, maxX: 1, maxY: 0.8)
            state.reset()
            for (index, y) in [330, 350, 370, 390, 410, 430].enumerated() {
                let scene = image(true, false, nearY: y - 35), now = 8 + Double(index) * 0.02
                let unrestricted = analyze(scene, now, nil)
                let tracked = analyze(scene, now, state, area: area)
                precondition(tracked.selected[color] != nil && !state.suppressed.contains(color))
                precondition(endpointDistance(tracked.selected[color]!, unrestricted.selected[color]!) == 0)
                precondition(losslessSignature(tracked.candidates) == losslessSignature(unrestricted.candidates))
                let off = analyze(scene, now, nil, area: area)
                if area.containsMidpoint(unrestricted.selected[color]!, width: 640, height: 480) {
                    precondition(frameSignature(off) == frameSignature(unrestricted))
                } else {
                    precondition(off.selected[color] == nil)
                    precondition(losslessSignature(off.candidates) == losslessSignature(unrestricted.candidates))
                }
            }
            let outside = image(true, false, nearY: 395)
            let outsideSeed = analyze(outside, 8.1, nil).selected[color]!
            let outsideZone = SaberIgnoreZone(around: outsideSeed, width: 640, height: 480)
            precondition(analyze(outside, 8.12, state, zones: [color: [outsideZone]], area: area).selected[color] == nil)
            // 期限切れ・3ミス・reset後の再取得にも範囲を適用する。
            for now in [8.4, 8.42] {
                precondition(analyze(outside, now, state, area: area).selected[color] == nil)
                precondition(state.suppressed.contains(color))
            }
            state.reset(); _ = analyze(near, 9, state, area: area)
            for missing in 1...3 {
                precondition(analyze(image(false, false), 9 + Double(missing) * 0.02, state, area: area).selected[color] == nil)
            }
            precondition(analyze(outside, 9.08, state, area: area).selected[color] == nil)
            state.reset()
            precondition(analyze(outside, 10, state, area: area).selected[color] == nil)
            // 取得時は範囲内の最高得点。範囲外の高得点候補へ飛ばない。
            precondition(analyze(both, 10.02, state, area: SaberPlayArea(minX: 0, minY: 0, maxX: 0.3, maxY: 1)).selected[color]!.0.x < 100)
        }
        print("Track lock: both colors, gating, 3 misses, resets, ignore zones, acquisition-only play area and velocity passed")
        // 追跡が0.5秒続いてから拡張。取得条件・候補一覧には手を加えない。
        for color in [SaberColor.red, .blue] {
            let initial = SaberPlayArea(minX: 0.05, minY: 0.1, maxX: 0.3, maxY: 0.7)
            func scene(_ x: Int) -> StaticImage {
                var image = StaticImage(width: 640, height: 480)
                image.bar(from: PixelPoint(x: x, y: 200), to: PixelPoint(x: x, y: 270), color: color)
                return image
            }
            let threshold = ColorThreshold(brightness: 140, dominance: 20)
            func step(_ image: StaticImage, _ now: Double, _ lock: SaberTrackLock?, _ area: inout SaberPlayArea?) -> SaberFrameAnalysis {
                let analysis = analyzeSabers(in: image.bytes, width: 640, height: 480, bytesPerRow: image.bytesPerRow,
                                             redThreshold: threshold, blueThreshold: threshold,
                                             playArea: area, trackLock: lock, now: now)
                if let lock { area = lock.grownPlayArea(area, selected: analysis.selected, width: 640, height: 480, now: now) }
                return analysis
            }
            let lock = SaberTrackLock()
            var area: SaberPlayArea? = initial
            for i in 0...20 {
                let before = area!
                let result = step(scene(80 + i * 16), 20 + Double(i) * 0.05, lock, &area)
                precondition(result.selected[color] != nil)
                if i < 10 { precondition(area == initial) }
                else {
                    precondition(area!.minX <= before.minX && area!.minY <= before.minY)
                    precondition(area!.maxX >= before.maxX && area!.maxY >= before.maxY)
                    let e = result.selected[color]!
                    precondition(area!.maxX >= Double(max(e.0.x, e.1.x)) / 640)
                }
            }
            precondition(area!.maxX > initial.maxX && area!.minX >= 0 && area!.maxX <= 1)
            let grown = area
            lock.reset()
            precondition(step(scene(580), 22, lock, &area).selected[color] == nil && area == grown)
            // 再取得・1ミスで連続選択の計時をやり直す。
            _ = step(scene(400), 23, lock, &area)
            _ = step(scene(400), 23.2, lock, &area)
            let empty = StaticImage(width: 640, height: 480)
            _ = step(empty, 23.3, lock, &area)
            _ = step(scene(440), 23.4, lock, &area)
            _ = step(scene(480), 23.6, lock, &area)
            _ = step(scene(520), 23.8, lock, &area)
            precondition(area == grown)
            lock.reset()
            var unset: SaberPlayArea?
            var off: SaberPlayArea? = initial
            for i in 0...20 {
                _ = step(scene(80 + i * 16), 30 + Double(i) * 0.05, lock, &unset)
                _ = step(scene(80 + i * 16), 30 + Double(i) * 0.05, nil, &off)
            }
            precondition(unset == nil && off == initial)
            // 画面端でも作成時と同じ margin / minimumSize / clamp。
            var edge: SaberFrameAnalysis?
            for i in 21...35 { edge = step(scene(min(635, 80 + i * 16)), 30 + Double(i) * 0.05, lock, &unset) }
            precondition(edge!.selected[color] != nil)
            let bounded = lock.grownPlayArea(grown, selected: edge!.selected, width: 640, height: 480, now: 31.75)!
            precondition(bounded.maxX == 1 && bounded.minX >= 0)
        }
        print("Play area growth: both colors, 0.5s continuity, misses, expand-only, acquisition, OFF and unset passed")
        // 新しい無視範囲は矩形だけでなく登録物体の両端も照合。旧JSONは従来の矩形判定。
        for color in [SaberColor.red, .blue] {
            let threshold = ColorThreshold(brightness: 140, dominance: 20)
            var original = StaticImage(width: 640, height: 480)
            original.bar(from: PixelPoint(x: 150, y: 100), to: PixelPoint(x: 400, y: 350), color: color)
            func analyze(_ image: StaticImage, _ zone: SaberIgnoreZone?, lock: SaberTrackLock? = nil) -> SaberFrameAnalysis {
                analyzeSabers(in: image.bytes, width: 640, height: 480, bytesPerRow: image.bytesPerRow,
                              redThreshold: threshold, blueThreshold: threshold,
                              ignoreZones: zone.map { [color: [$0]] } ?? [:], trackLock: lock, now: 1)
            }
            let originalAnalysis = analyze(original, nil)
            let endpoints = originalAnalysis.selected[color]!
            let zone = SaberIgnoreZone(around: endpoints, width: 640, height: 480,
                                       candidates: originalAnalysis.candidates[color] ?? [])
            let oldData = Data("{\"minX\":\(zone.minX),\"minY\":\(zone.minY),\"maxX\":\(zone.maxX),\"maxY\":\(zone.maxY)}".utf8)
            let old = try! JSONDecoder().decode(SaberIgnoreZone.self, from: oldData)
            let restored = try! JSONDecoder().decode(SaberIgnoreZone.self, from: JSONEncoder().encode(zone))
            precondition(restored == zone && restored.objectEndpoints != nil && old.objectEndpoints == nil)
            precondition(try! JSONDecoder().decode(SaberIgnoreZone.self, from: JSONEncoder().encode(old)) == old)
            precondition(saberIgnored((endpoints.1, endpoints.0), width: 640, height: 480, zones: [restored]))
            var crossing = StaticImage(width: 640, height: 480)
            crossing.bar(from: PixelPoint(x: 170, y: 320), to: PixelPoint(x: 380, y: 130), color: color)
            let crossingEndpoints = analyze(crossing, nil).selected[color]!
            precondition(zone.contains(crossingEndpoints.0, width: 640, height: 480)
                         && zone.contains(crossingEndpoints.1, width: 640, height: 480))
            for lock in [nil, SaberTrackLock()] as [SaberTrackLock?] {
                precondition(analyze(original, restored, lock: lock).selected[color] == nil)
                lock?.reset()
                precondition(analyze(crossing, restored, lock: lock).selected[color] != nil)
                lock?.reset()
                precondition(analyze(crossing, old, lock: lock).selected[color] == nil)
            }
            // 正規化距離の許容値境界と、矩形条件を別々に確認する。
            let short = SaberIgnoreZone(around: (PixelPoint(x: 500, y: 500), PixelPoint(x: 550, y: 500)), width: 1000, height: 1000)
            precondition(saberIgnored((PixelPoint(x: 549, y: 549), PixelPoint(x: 599, y: 549)), width: 1000, height: 1000, zones: [short]))
            precondition(!saberIgnored((PixelPoint(x: 550, y: 550), PixelPoint(x: 600, y: 550)), width: 1000, height: 1000, zones: [short]))
        }
        print("Object ignore zones: both colors, same/reversed object, crossing saber, ON/OFF and Codable old/new passed")
        // 固定カメラの蛍光灯上を移動する色付きの光。winnerやeligibleだけでは覚え切れない。
        for color in [SaberColor.red, .blue] {
            let threshold = ColorThreshold(brightness: 140, dominance: 20)
            func analyze(_ image: StaticImage, zones: [SaberIgnoreZone] = []) -> SaberFrameAnalysis {
                analyzeSabers(in: image.bytes, width: 480, height: 640, bytesPerRow: image.bytesPerRow,
                              redThreshold: threshold, blueThreshold: threshold, ignoreZones: [color: zones])
            }
            var learner = SaberIgnoreLearner(startedAt: 10, colors: [color])
            var frames: [StaticImage] = []
            var expectedMinX = 1.0, expectedMaxX = 0.0
            for i in 0..<60 {
                var image = StaticImage(width: 480, height: 640)
                image.bar(from: PixelPoint(x: 20 + i * 3, y: 380),
                          to: PixelPoint(x: 80 + i * 3, y: 400), color: color, thickness: 4)
                let analysis = analyze(image)
                precondition(!(analysis.candidates[color] ?? []).isEmpty)
                for candidate in analysis.candidates[color]! {
                    expectedMinX = min(expectedMinX, Double(min(candidate.endpoints.0.x, candidate.endpoints.1.x)) / 480 - SaberIgnoreZone.margin)
                    expectedMaxX = max(expectedMaxX, Double(max(candidate.endpoints.0.x, candidate.endpoints.1.x)) / 480 + SaberIgnoreZone.margin)
                }
                // 非eligible・winnerなしでも同じように学習する。
                let candidates = analysis.candidates.mapValues { $0.map { value -> SaberCandidate in
                    var value = value; value.isEmitterEligible = false; return value
                } }
                learner.add(SaberFrameAnalysis(candidates: candidates, selected: [:]),
                            width: 480, height: 640, now: 10 + Double(i) / 20)
                frames.append(image)
            }
            let zones = learner.zones[color]!
            precondition(zones.count == 1 && zones[0].objects!.count > 12)
            precondition(abs(zones[0].minX - expectedMinX) < 1e-12 && abs(zones[0].maxX - expectedMaxX) < 1e-12)
            let restored = try! JSONDecoder().decode([SaberIgnoreZone].self, from: JSONEncoder().encode(zones))
            for image in frames {
                precondition(analyze(image, zones: restored).selected[color] == nil)
                for candidate in analyze(image).candidates[color]! {
                    precondition(saberIgnored(candidate.endpoints, width: 480, height: 640, zones: restored))
                }
            }
            var crossing = StaticImage(width: 480, height: 640)
            crossing.bar(from: PixelPoint(x: 80, y: 426), to: PixelPoint(x: 270, y: 353), color: color)
            precondition(analyze(crossing, zones: restored).selected[color] != nil)
            var elsewhere = frames.last!
            elsewhere.bar(from: PixelPoint(x: 380, y: 150), to: PixelPoint(x: 390, y: 330), color: color)
            let selected = analyze(elsewhere, zones: restored).selected[color]!
            precondition(min(selected.0.x, selected.1.x) > 350)
            // 離れた物は別の群。期間外・対象外色・空コマは追加しない。
            let far = analyze(crossing)
            let before = learner.zones
            learner.add(far, width: 480, height: 640, now: 9.99)
            learner.add(far, width: 480, height: 640, now: 13)
            precondition(learner.zones == before)
            var separate = StaticImage(width: 480, height: 640)
            separate.bar(from: PixelPoint(x: 400, y: 80), to: PixelPoint(x: 410, y: 180), color: color)
            learner.add(analyze(separate), width: 480, height: 640, now: 12.99)
            precondition(learner.zones[color]!.count == 2)
            precondition(learner.zones[color == .red ? .blue : .red] == nil)
        }
        print("Ignore learning: drifting glow, all ineligible candidates, 3s boundary, clusters, crossing/elsewhere saber and Codable passed")
        // 会場の赤いコーン（2026-10-18）: 無視する範囲に両端が入る候補は選ばず、同じコマの別の候補を選ぶ。
        do {
            var scene = StaticImage(width: 200, height: 120)
            scene.bar(from: PixelPoint(x: 150, y: 15), to: PixelPoint(x: 150, y: 105), color: .red, thickness: 4)
            scene.bar(from: PixelPoint(x: 30, y: 35), to: PixelPoint(x: 45, y: 105), color: .red, thickness: 4)
            let threshold = ColorThreshold(brightness: 140, dominance: 20)
            func select(_ zones: [SaberColor: [SaberIgnoreZone]]) -> (PixelPoint, PixelPoint)? {
                analyzeSabers(in: scene.bytes, width: scene.width, height: scene.height,
                              bytesPerRow: scene.bytesPerRow, redThreshold: threshold,
                              blueThreshold: threshold, ignoreZones: zones).selected[.red]
            }
            guard let first = select([:]) else { fatalError("ignore zone: nothing detected") }
            let zone = SaberIgnoreZone(around: first, width: scene.width, height: scene.height)
            guard let second = select([.red: [zone]]) else { fatalError("ignore zone: the other bar was not selected") }
            guard endpointDistance(second, first) > 40,
                  !saberIgnored(second, width: scene.width, height: scene.height, zones: [zone]) else {
                fatalError("ignore zone: selected the ignored candidate again \(second)")
            }
            let far = SaberIgnoreZone(minX: 0.9, minY: 0.9, maxX: 1, maxY: 1)
            guard let same = select([.red: [far]]), endpointDistance(same, first) < 1 else {
                fatalError("ignore zone: an unrelated zone changed the selection")
            }
            guard select([.blue: [zone]]).map({ endpointDistance($0, first) < 1 }) == true else {
                fatalError("ignore zone: a blue zone changed the red selection")
            }
            print("Ignore zone: the ignored candidate is skipped and the other bar is selected")
        }
        // プレイ範囲（2026-10-18、赤い服の人など動く物の対策）: 両端の中点が範囲外の候補は選ばず、同じコマの
        // 範囲内の候補を選ぶ。範囲が無ければ従来と同じ。C++ core_test の play_area_tests と同じ場面・同じ数値。
        do {
            var scene = StaticImage(width: 200, height: 120)
            scene.bar(from: PixelPoint(x: 150, y: 15), to: PixelPoint(x: 150, y: 105), color: .red, thickness: 4)
            scene.bar(from: PixelPoint(x: 30, y: 35), to: PixelPoint(x: 45, y: 105), color: .red, thickness: 4)
            let threshold = ColorThreshold(brightness: 140, dominance: 20)
            func analyze(_ area: SaberPlayArea?) -> SaberFrameAnalysis {
                analyzeSabers(in: scene.bytes, width: scene.width, height: scene.height,
                              bytesPerRow: scene.bytesPerRow, redThreshold: threshold,
                              blueThreshold: threshold, playArea: area)
            }
            let legacy = analyzeSabers(in: scene.bytes, width: scene.width, height: scene.height,
                                       bytesPerRow: scene.bytesPerRow, redThreshold: threshold, blueThreshold: threshold)
            let plain = analyze(nil)
            guard let first = plain.selected[.red], let legacyFirst = legacy.selected[.red],
                  endpointDistance(first, legacyFirst) == 0 else { fatalError("play area: nil area changed the selection") }
            func sameCandidates(_ a: SaberFrameAnalysis, _ b: SaberFrameAnalysis) -> Bool {
                let x = a.candidates[.red] ?? [], y = b.candidates[.red] ?? []
                return x.count == y.count && zip(x, y).allSatisfy {
                    $0.score == $1.score && $0.isEmitterEligible == $1.isEmitterEligible && endpointDistance($0.endpoints, $1.endpoints) == 0
                }
            }
            let midX = (Double(first.0.x) + Double(first.1.x)) / 2 / Double(scene.width)
            let other = midX > 0.5 ? SaberPlayArea(minX: 0, minY: 0, maxX: 0.5, maxY: 1)
                                   : SaberPlayArea(minX: 0.5, minY: 0, maxX: 1, maxY: 1)
            let inside = analyze(other)
            guard sameCandidates(plain, inside), sameCandidates(plain, legacy),
                  let second = inside.selected[.red], endpointDistance(second, first) > 40,
                  other.containsMidpoint(second, width: scene.width, height: scene.height) else {
                fatalError("play area: the bar inside the area was not selected")
            }
            guard let whole = analyze(SaberPlayArea(minX: 0, minY: 0, maxX: 1, maxY: 1)).selected[.red],
                  endpointDistance(whole, first) == 0 else { fatalError("play area: the whole frame changed the selection") }
            guard analyze(SaberPlayArea(minX: 0.9, minY: 0.9, maxX: 1, maxY: 1)).selected[.red] == nil else {
                fatalError("play area: a candidate outside the area was selected")
            }
            // 中点の規則: 片端がはみ出しても中点が範囲内なら使う。境界上は範囲内。
            let left = SaberPlayArea(minX: 0, minY: 0, maxX: 0.5, maxY: 1)
            guard left.containsMidpoint((PixelPoint(x: 10, y: 10), PixelPoint(x: 150, y: 10)), width: 200, height: 120),
                  !left.containsMidpoint((PixelPoint(x: 90, y: 10), PixelPoint(x: 150, y: 10)), width: 200, height: 120),
                  left.containsMidpoint((PixelPoint(x: 100, y: 10), PixelPoint(x: 100, y: 100)), width: 200, height: 120) else {
                fatalError("play area: midpoint rule")
            }
            // 学習: 30 未満は使わない。両端の min/max + 0.08、最小 0.3、0..1 に収める。
            var learner = SaberPlayAreaLearner()
            for _ in 0..<29 { learner.add((PixelPoint(x: 40, y: 24), PixelPoint(x: 120, y: 96)), width: 200, height: 120) }
            guard learner.area() == nil else { fatalError("play area: learned from too few samples") }
            learner.add((PixelPoint(x: 40, y: 24), PixelPoint(x: 120, y: 96)), width: 200, height: 120)
            guard let learned = learner.area(), learner.samples == 30,
                  abs(learned.minX - 0.12) < 1e-12, abs(learned.maxX - 0.68) < 1e-12,
                  abs(learned.minY - 0.12) < 1e-12, abs(learned.maxY - 0.88) < 1e-12 else {
                fatalError("play area: learned area \(String(describing: learner.area()))")
            }
            let small = SaberPlayArea.around(minX: 0.5, minY: 0.5, maxX: 0.52, maxY: 0.5)
            let edge = SaberPlayArea.around(minX: 0.95, minY: 0, maxX: 1, maxY: 0.02)
            guard abs((small.maxX - small.minX) - 0.3) < 1e-12, abs(small.minX - 0.36) < 1e-12,
                  edge.maxX == 1, abs(edge.minX - 0.7) < 1e-12, edge.minY == 0, abs(edge.maxY - 0.3) < 1e-12,
                  SaberPlayArea.around(minX: 0, minY: 0, maxX: 1, maxY: 1) == SaberPlayArea(minX: 0, minY: 0, maxX: 1, maxY: 1) else {
                fatalError("play area: margin / minimum size / clamp")
            }
            print("Play area: the candidate whose midpoint is outside is skipped and the inside bar is selected")
        }
        // ISO実験の暫定閾値はこの合成グリッドを根拠にする。実機の再現性を保証するものではない。
        do {
            let on = ShutterEmitterMode(shutter: 2000, iso: 500, enabled: true)
            func scene(_ rgb: (UInt8, UInt8, UInt8), background: UInt8 = 8,
                       length: Int = 100, thickness: Int = 2) -> StaticImage {
                var image = StaticImage(width: 240, height: 160, padding: 16)
                for y in 0..<image.height { for x in 0..<image.width {
                    image.pixel(x, y, red: background, green: background, blue: background)
                } }
                image.coloredBar(from: PixelPoint(x: 50, y: 60), to: PixelPoint(x: 50 + length, y: 60),
                                 red: rgb.0, green: rgb.1, blue: rgb.2, thickness: thickness)
                return image
            }
            func run(_ image: StaticImage, _ mode: ShutterEmitterMode = ShutterEmitterMode(),
                     step: Int = 2, zones: [SaberColor: [SaberIgnoreZone]] = [:],
                     area: SaberPlayArea? = nil) -> SaberFrameAnalysis {
                analyzeSabers(in: image.bytes, width: image.width, height: image.height,
                              bytesPerRow: image.bytesPerRow, redThreshold: ColorThreshold(), blueThreshold: ColorThreshold(),
                              sampleStep: step, collectObserverEvidence: true, ignoreZones: zones, playArea: area,
                              shutterEmitter: mode)
            }
            func emitters(_ a: SaberFrameAnalysis, _ color: SaberColor) -> [SaberCandidate] {
                (a.candidates[color] ?? []).filter { $0.source == "shutter-color-emitter" }
            }
            var positive = 0, negative = 0
            for color in [SaberColor.red, .blue] {
                for background in [0, 8, 24, 48] {
                    for value in [64, 80, 96, 128, 144, 200, 234] {
                        let rgb: (UInt8, UInt8, UInt8) = color == .red ? (UInt8(value), 12, 12) : (12, 24, UInt8(value))
                        let image = scene(rgb, background: UInt8(background))
                        let expected = value >= max(64, background + 40, background * 3)
                        let result = run(image, on)
                        guard !emitters(result, color).isEmpty == expected else {
                            fatalError("shutter emitter grid color=\(color) bg=\(background) value=\(value) expected=\(expected)")
                        }
                        if expected {
                            guard let selected = result.selected[color],
                                  endpointDistance(selected, (PixelPoint(x: 50, y: 60), PixelPoint(x: 150, y: 60))) <= 8 else {
                                fatalError("shutter emitter endpoints")
                            }
                            positive += 1
                        } else { negative += 1 }
                    }
                }
                let image = scene(color == .red ? (128, 12, 12) : (12, 24, 128))
                let baseline = run(image)
                for shutter in [0, 500, 1000, 2000] { for iso in [2000, 250, 200, 160, 125, 100, 80, 64] { for enabled in [false, true] {
                    let mode = ShutterEmitterMode(shutter: shutter, iso: iso, enabled: enabled)
                    guard mode.active == (enabled && shutter != 0 && iso < 2000) else { fatalError("shutter emitter mode") }
                    let result = run(image, mode)
                    if mode.active {
                        guard !emitters(result, color).isEmpty else { fatalError("low ISO opt-in missing") }
                    } else {
                        let a = baseline.candidates[color] ?? [], b = result.candidates[color] ?? []
                        guard emitters(result, color).isEmpty, a.count == b.count,
                              zip(a, b).allSatisfy({ $0.score.bitPattern == $1.score.bitPattern
                                && $0.isEmitterEligible == $1.isEmitterEligible && endpointDistance($0.endpoints, $1.endpoints) == 0 }) else {
                            fatalError("auto/ISO2000/OFF parity")
                        }
                    }
                } } }
                // 別sample stepでも同じ物体を選び、無視範囲とプレイ範囲は追加経路にも適用する。
                guard !emitters(run(image, on, step: 1), color).isEmpty,
                      run(image, on, area: SaberPlayArea(minX: 0.8, minY: 0.8, maxX: 1, maxY: 1)).selected[color] == nil,
                      run(image, on, zones: [color: [SaberIgnoreZone(minX: 0, minY: 0, maxX: 1, maxY: 1)]]).selected[color] == nil else {
                    fatalError("shutter emitter area/ignore/step")
                }
            }
            let negatives = [
                scene((128, 12, 12), background: 49), // 明るい背景
                scene((12, 24, 128), background: 49),
                scene((63, 8, 8)), scene((8, 12, 63)), // 暗すぎる
                scene((235, 12, 12)), scene((12, 24, 235)), // 飽和に近い画素は既存経路の仕事
                scene((128, 80, 80)), scene((80, 80, 128)), // 色の薄い服
                scene((128, 60, 12)), scene((12, 128, 128)), // 暖色・シアン反射
                scene((128, 12, 128)), scene((12, 128, 12)), scene((200, 200, 200)),
                scene((128, 12, 12), thickness: 12), scene((12, 24, 128), thickness: 12), // 広い服・反射
                scene((128, 12, 12), length: 4, thickness: 3), scene((12, 24, 128), length: 4, thickness: 3) // 小さな点
            ]
            for image in negatives {
                let result = run(image, on)
                guard emitters(result, .red).isEmpty, emitters(result, .blue).isEmpty else { fatalError("shutter emitter negative") }
                negative += 1
            }
            // 各暫定定数の境界を他の条件から独立させる（暗背景8、step1、矩形の画素数既知）。
            func rectangle(_ length: Int, _ thickness: Int, rgb: (UInt8, UInt8, UInt8) = (128, 12, 12)) -> StaticImage {
                var image = scene((8, 8, 8))
                for y in 40..<(40 + thickness) { for x in 30..<(30 + length) {
                    image.pixel(x, y, red: rgb.0, green: rgb.1, blue: rgb.2)
                } }
                return image
            }
            let blueRGB: (UInt8, UInt8, UInt8) = (12, 24, 128)
            let shapeCases: [(Int, Int, SaberColor, Bool)] = [
                (6, 1, .red, true), (5, 1, .red, false), // 最小6画素
                (24, 6, .blue, true), (23, 6, .blue, false), // 青は PCA長/平均幅4
                (15, 6, .red, true), (14, 6, .red, false), // 赤は32画素以上なら2.4（傾いて短く写る刃）
                (11, 3, .red, true), (10, 3, .red, false), // 33画素は2.4、30画素の赤は4のまま
                (8, 2, .red, true), (7, 2, .red, false),
                (100, 11, .red, true), (90, 12, .red, false), // 平均幅は短辺の7%以内
                (150, 7, .red, true), (166, 7, .red, false)] // 画像面積の3%以内
            for (length, thickness, color, expected) in shapeCases {
                let image = color == .red ? rectangle(length, thickness) : rectangle(length, thickness, rgb: blueRGB)
                let result = run(image, on, step: 1)
                guard !emitters(result, color).isEmpty == expected else {
                    fatalError("shutter emitter shape boundary \(color) \(length)x\(thickness), expected \(expected)")
                }
            }
            let colorCases: [((UInt8, UInt8, UInt8), SaberColor, Bool)] = [
                ((200, 70, 70), .red, true), ((200, 71, 71), .red, false), // 純度0.65
                ((70, 70, 200), .blue, true), ((71, 71, 200), .blue, false),
                ((200, 50, 0), .red, true), ((200, 60, 0), .red, false), // 赤0±15度
                ((200, 0, 50), .red, true), ((200, 0, 60), .red, false),
                ((0, 120, 180), .blue, true), ((0, 123, 180), .blue, false), // 青200..250度
                ((30, 0, 180), .blue, true), ((33, 0, 180), .blue, false)
            ]
            for (rgb, color, expected) in colorCases {
                guard !emitters(run(rectangle(100, 3, rgb: rgb), on, step: 1), color).isEmpty == expected else {
                    fatalError("shutter emitter color boundary \(rgb), expected \(expected)")
                }
            }
            print("Shutter emitter synthetic: \(positive) positive / \(negative) negative, mode/area/ignore/stride parity + \(shapeCases.count + colorCases.count) boundaries PASS")
        }
        print("Static BGRA detection tests passed")
    }
}
