import CoreGraphics
import Darwin
import Foundation
import ImageIO

@_silgen_name("ps_alloc_start") func allocationStart()
@_silgen_name("ps_alloc_count") func allocationCount() -> UInt64

// 自動クリップ（AutoClip.swift）の1コマあたりの追加コストを、本番と同じ analyzeSabers の
// 結果（実 fixture）で測る。カメラ・UDP・UI は含まない。
func loadBGRA(_ path: String) -> (bytes: [UInt8], width: Int, height: Int) {
    guard let source = CGImageSourceCreateWithURL(URL(fileURLWithPath: path) as CFURL, nil),
          let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else { fatalError("cannot read \(path)") }
    let width = image.width, height = image.height
    var bytes = [UInt8](repeating: 0, count: width * height * 4)
    bytes.withUnsafeMutableBytes { raw in
        let context = CGContext(data: raw.baseAddress, width: width, height: height, bitsPerComponent: 8,
                                bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
                                bitmapInfo: CGBitmapInfo.byteOrder32Little.rawValue | CGImageAlphaInfo.premultipliedFirst.rawValue)
        context?.draw(image, in: CGRect(x: 0, y: 0, width: width, height: height))
    }
    return (bytes, width, height)
}

func percentile(_ values: [Double], _ p: Double) -> Double {
    guard !values.isEmpty else { return .nan }
    let sorted = values.sorted()
    return sorted[min(sorted.count - 1, Int((Double(sorted.count - 1) * p).rounded()))]
}

func now() -> Double { ProcessInfo.processInfo.systemUptime }

let arguments = CommandLine.arguments
let fixture = arguments[1]
let frames = Int(arguments[2]) ?? 1200
let output = URL(fileURLWithPath: arguments[3])
let image = loadBGRA(fixture)

var detectionMs: [Double] = []
var analysis = SaberFrameAnalysis(candidates: [:], selected: [:])
for _ in 0..<20 {
    let start = now()
    analysis = analyzeSabers(in: image.bytes, width: image.width, height: image.height, bytesPerRow: image.width * 4,
                             redThreshold: ColorThreshold(), blueThreshold: ColorThreshold())
    detectionMs.append((now() - start) * 1000)
}

let recorder = AutoClipRecorder(directory: output, enabled: true, clipNamer: { "harness" })
let written = DispatchSemaphore(value: 0)
var writeSeconds = 0.0
var writeStart = 0.0
recorder.onClipSaved = { result in
    writeSeconds = now() - writeStart
    if case .failure(let error) = result { print("write failed: \(error)") }
    written.signal()
}

var plain: [Double] = [], context: [Double] = [], event: [Double] = []
var plainAllocations: [UInt64] = []
allocationStart()
image.bytes.withUnsafeBufferPointer { buffer in
    let base = buffer.baseAddress!
    var lastContext = -Double.infinity
    for frame in 0..<frames {
        let time = 1000 + Double(frame) / 60
        if frame == frames / 2 { recorder.requestClip(reason: "harness") }
        let triggerFrames = frame >= frames / 2 && frame < frames / 2 + 3
        let isContext = time - lastContext >= AutoClipLimits.contextInterval
        if isContext { lastContext = time }
        let observed = ObservedFrame(baseAddress: base, width: image.width, height: image.height,
                                     bytesPerRow: image.width * 4, sequence: UInt64(frame + 1), timestamp: time,
                                     processingSeconds: 0.012, analysis: analysis, ignoreZones: [:], playArea: nil,
                                     captureFPS: 60)
        let allocationsBefore = allocationCount()
        let start = now()
        if frame == frames / 2 + 120 { writeStart = start }   // the post window ends here
        recorder.observe(observed)
        let elapsed = (now() - start) * 1000
        let allocations = allocationCount() - allocationsBefore
        if frame < 5 { continue }   // first frame allocates the ring once
        if triggerFrames { event.append(elapsed) }
        else if isContext { context.append(elapsed) }
        else if frame != frames / 2 + 120 {
            plain.append(elapsed)
            // malloc_logger counts every thread; after the trigger the writer encodes PNGs.
            if frame < frames / 2 { plainAllocations.append(allocations) }
        }
    }
}
_ = written.wait(timeout: .now() + 60)

let buffers = AutoClipBuffers(width: image.width, height: image.height, captureFPS: 60)
func line(_ name: String, _ values: [Double]) -> String {
    String(format: "%@ n=%d median=%.4f ms p99=%.4f ms max=%.4f ms", name, values.count,
           percentile(values, 0.5), percentile(values, 0.99), values.max() ?? .nan)
}
print(String(format: "frame=%dx%d red=%d blue=%d candidates (fixture)", image.width, image.height,
             analysis.candidates[.red]?.count ?? 0, analysis.candidates[.blue]?.count ?? 0))
print(line("analyzeSabers (reference)", detectionMs))
print(line("observe plain frame", plain))
print(line("observe + context copy", context))
print(line("observe + event copy", event))
print("allocations per plain frame before the clip: max=\(plainAllocations.max() ?? 0) total=\(plainAllocations.reduce(0, +)) over \(plainAllocations.count) frames")
let perSecond = percentile(plain, 0.5) * (60 - 1 / AutoClipLimits.contextInterval)
    + percentile(context, 0.5) / AutoClipLimits.contextInterval
print(String(format: "detection-thread cost per second at 60 fps: %.3f ms (%.3f%% of one core)", perSecond, perSecond / 10))
print(String(format: "ring memory: %.1f MiB pixels + %d frame records", Double(buffers.byteCount) / 1_048_576, buffers.frameCapacity))
print(String(format: "clip write (utility queue): %.0f ms", writeSeconds * 1000))
