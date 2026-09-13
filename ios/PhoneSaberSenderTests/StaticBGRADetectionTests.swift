import Foundation

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
                        distanceTolerance: Double = 6, angleTolerance: Double = 0.20) {
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
    guard abs(length - expectedLength) <= 12 else { fatalError("\(name): length \(length)") }
}

@main
enum StaticBGRADetectionTests {
    static func main() {
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

        var redBlob = StaticImage(width: 128, height: 80)
        for y in 20...55 {
            for x in 42...77 { redBlob.pixel(x, y, red: 245, green: 40, blue: 35) }
        }
        guard detectSaber(in: redBlob.bytes, width: redBlob.width, height: redBlob.height,
                          bytesPerRow: redBlob.bytesPerRow, color: .red,
                          threshold: ColorThreshold(brightness: 140, dominance: 20)) == nil else {
            fatalError("compact red blob must not be detected as a saber")
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
        }

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
        // three samples plus the five-pixel cap. 0.20 radians rejects a 90-degree error.
        print("Static BGRA detection tests passed: \(cases.count + 14) cases")
    }
}
