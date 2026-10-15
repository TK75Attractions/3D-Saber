import Foundation
import Darwin

@_silgen_name("ps_alloc_start") func allocationStart()
@_silgen_name("ps_alloc_count") func allocationCount() -> UInt64
@_silgen_name("ps_cpu_seconds") func cpuSeconds() -> Double

@main
enum HeatHarness {
    static func main() {
        let frames = Int(CommandLine.arguments[1])!
        precondition(frames >= 600)
        allocationStart()
        // ランタイムの初回処理を、回帰対象の meter より先に暖める。
        let warm = DeviceHealthMeter()
        warm.reset(at: 0, generation: 1)
        record(warm, at: 1)
        _ = warm.snapshot(at: 1)
        let meter = DeviceHealthMeter()
        meter.reset(at: 0, generation: 1)
        let before = allocationCount()
        let cpu = cpuSeconds()
        for frame in 1...frames { record(meter, at: Double(frame) / 60) }
        let count = allocationCount() - before
        let used = (cpuSeconds() - cpu) * 1_000_000 / Double(frames)
        print(String(format: "health-collection CPU_us/frame=%.3f malloc=%llu frames=%d", used, count, frames))
        // snapshot は低頻度の UI 側。各フレームの collector に確保がないことを要求する。
        var failures = 0
        if count != 0 { print("FAIL health collection allocated \(count) times"); failures += 1 }
        let rates = meter.snapshot(at: Double(frames) / 60)
        precondition(rates.cameraFPS == 60 && rates.processingFPS == 60)
        precondition(rates.redSendFPS == 60 && rates.blueSendFPS == 60)
        precondition(rates.medianProcessingMs == 3 && rates.medianCaptureToSendMs == 20)
        var afterSnapshot: UInt64 = 0
        for frame in 1...600 {
            let time = Double(frames + frame) / 60
            if frame.isMultiple(of: 60) { _ = meter.snapshot(at: time) }
            let beforeFrame = allocationCount()
            record(meter, at: time)
            afterSnapshot += allocationCount() - beforeFrame
        }
        print("health collection with 1Hz snapshots malloc=\(afterSnapshot)")
        if afterSnapshot != 0 { print("FAIL collection after snapshots allocated"); failures += 1 }
        let view = PreviewView()
        let session = Session()
        view.videoPreviewLayer.session = session
        let initial = view.videoPreviewLayer.assignments
        for _ in 0..<120 { PreviewUpdate(session: session).updateUIView(view, context: ()) }
        let repeats = view.videoPreviewLayer.assignments - initial
        print("preview identical-session assignments=\(repeats)")
        if repeats != 0 { print("FAIL identical session rebound"); failures += 1 }
        let replacement = Session()
        PreviewUpdate(session: replacement).updateUIView(view, context: ())
        precondition(view.videoPreviewLayer.session === replacement)
        precondition(view.videoPreviewLayer.assignments == initial + repeats + 1)
        view.videoPreviewLayer.session = nil
        PreviewUpdate(session: replacement).updateUIView(view, context: ())
        precondition(view.videoPreviewLayer.session === replacement)
        measureDetection()
        print("heat regressions: \(failures == 0 ? "PASS" : "FAIL")")
        exit(failures == 0 ? 0 : 1)
    }

    static func record(_ meter: DeviceHealthMeter, at time: Double) {
        meter.cameraFrame(at: time)
        meter.processed(at: time, milliseconds: 3, generation: 1, captureToSendMs: 20)
        meter.sent(at: time, port: 5005, generation: 1)
        meter.sent(at: time, port: 5006, generation: 1)
    }

    static func measureDetection() {
        // 合成 BGRA は本番と同じ 640×480。確保と色変換は計測区間の外。
        let width = 640, height = 480, stride = width * 4
        var bgra = [UInt8](repeating: 0, count: stride * height)
        for blade in [false, true] {
            if blade {
                for y in 200...210 { for x in 100...500 {
                    let offset = y * stride + x * 4
                    bgra[offset] = 35; bgra[offset + 1] = 45; bgra[offset + 2] = 245
                    bgra[offset + 3] = 255
                } }
            }
            bgra.withUnsafeBufferPointer { pixels in
                func detect() -> [SaberColor: (PixelPoint, PixelPoint)] {
                    detectSabers(baseAddress: pixels.baseAddress!, width: width, height: height,
                                 bytesPerRow: stride, redThreshold: ColorThreshold(), blueThreshold: ColorThreshold())
                }
                let expected = detect()
                let frames = 60
                let before = allocationCount(), cpu = cpuSeconds()
                for _ in 0..<frames {
                    let result = detect()
                    precondition(result[.red]?.0 == expected[.red]?.0 && result[.red]?.1 == expected[.red]?.1)
                }
                print(String(format: "detector blade=%@ CPU_ms/frame=%.3f malloc/frame=%.1f",
                             blade ? "red" : "none", (cpuSeconds() - cpu) * 1000 / Double(frames),
                             Double(allocationCount() - before) / Double(frames)))
            }
        }
    }
}
