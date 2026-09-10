import Foundation

/// Pure BGRA detector. Coordinates remain in the source image's coordinate system.
func detectSaber(in bgra: [UInt8], width: Int, height: Int, bytesPerRow: Int,
                 color: SaberColor, threshold: ColorThreshold, sampleStep: Int = 2) -> (PixelPoint, PixelPoint)? {
    guard width > 0, height > 0, bytesPerRow >= width * 4,
          bgra.count >= bytesPerRow * height else { return nil }
    let step = max(sampleStep, 1)
    var candidates: [PixelPoint] = []
    for y in stride(from: 0, to: height, by: step) {
        for x in stride(from: 0, to: width, by: step) {
            let offset = y * bytesPerRow + x * 4
            if isBright(bgra[offset + 2], bgra[offset + 1], bgra[offset], color: color, threshold: threshold) {
                candidates.append(PixelPoint(x: x / step, y: y / step))
            }
        }
    }
    let cluster = bestColorCluster(in: candidates, width: (width + step - 1) / step, height: (height + step - 1) / step)
    guard let endpoints = principalAxisEndpoints(cluster) else { return nil }
    return (PixelPoint(x: endpoints.0.x * step, y: endpoints.0.y * step), PixelPoint(x: endpoints.1.x * step, y: endpoints.1.y * step))
}
