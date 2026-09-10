import Foundation

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
