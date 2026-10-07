import Foundation

enum DeviceThermalLevel: Int {
    case normal, elevated, high, dangerous, unknown

    init(state: ProcessInfo.ThermalState) {
        switch state {
        case .nominal: self = .normal
        case .fair: self = .elevated
        case .serious: self = .high
        case .critical: self = .dangerous
        @unknown default: self = .unknown
        }
    }

    var metadataValue: String {
        switch self {
        case .normal: return "nominal"
        case .elevated: return "fair"
        case .high: return "serious"
        case .dangerous: return "critical"
        case .unknown: return "unknown"
        }
    }

    var title: String {
        switch self {
        case .normal: return "正常"
        case .elevated: return "やや高い"
        case .high: return "高い"
        case .dangerous: return "危険"
        case .unknown: return "不明"
        }
    }
}

enum DeviceHealthText {
    static func battery(level: Double?, state: String?) -> String {
        let percent = level.flatMap { $0.isFinite && (0...1).contains($0) ? Int(($0 * 100).rounded()) : nil }
        return "電池 \(percent.map { "\($0)%" } ?? "不明") / \(state ?? "状態不明")"
    }

    static func warning(thermal: DeviceThermalLevel, rates: DeviceHealthRates,
                        requestedFPS: Double, running: Bool) -> String? {
        var reasons: [String] = []
        if thermal == .high || thermal == .dangerous { reasons.append("発熱が\(thermal.title)") }
        if running && rates.ready && requestedFPS > 0 &&
            min(rates.cameraFPS, rates.processingFPS) < requestedFPS * 0.7 {
            reasons.append("fpsが要求値の70%未満")
        }
        return reasons.isEmpty ? nil : "注意: " + reasons.joined(separator: " / ") + "。箱を開けて通風・電源を確認"
    }

    static func timing(_ rates: DeviceHealthRates) -> String {
        let median = rates.medianProcessingMs.map { String(format: "%.1f ms", $0) } ?? "未計測"
        let age = rates.medianCaptureToSendMs.map { String(format: "　撮影→送信 %.0f ms", $0) } ?? ""
        return String(format: "カメラ %.1f / 処理 %.1f fps　処理中央値 %@%@（直近5秒）",
                      rates.cameraFPS, rates.processingFPS, median, age)
    }
}

struct DeviceHealthRates: Equatable {
    var cameraFPS = 0.0
    var processingFPS = 0.0
    var medianProcessingMs: Double?
    var ready = false
    var medianCaptureToSendMs: Double?
}

// キャプチャ・処理・UIの各キューから観測するだけ。認識や送信には使わない。
final class DeviceHealthMeter: @unchecked Sendable {
    private let lock = NSLock()
    private var started: TimeInterval?
    private var generation = 0
    private var cameraTimes: [TimeInterval] = []
    private var processing: [(time: TimeInterval, ms: Double)] = []
    // 撮影時刻（AVCapture のホスト時計）から認識完了＝送信直前まで。0〜2秒の範囲外は捨てる。
    private var captureToSend: [(time: TimeInterval, ms: Double)] = []
    private let window = 5.0

    func reset(at time: TimeInterval, generation: Int) {
        lock.lock(); defer { lock.unlock() }
        started = time; self.generation = generation
        cameraTimes.removeAll(keepingCapacity: true); processing.removeAll(keepingCapacity: true)
        captureToSend.removeAll(keepingCapacity: true)
    }

    func cameraFrame(at time: TimeInterval) {
        lock.lock(); defer { lock.unlock() }
        cameraTimes.append(time)
        cameraTimes.removeAll { $0 <= time - window }
        if cameraTimes.count > 1200 { cameraTimes.removeFirst(cameraTimes.count - 1200) }
    }

    func processed(at time: TimeInterval, milliseconds: Double, generation: Int, captureToSendMs: Double? = nil) {
        lock.lock(); defer { lock.unlock() }
        guard generation == self.generation, milliseconds.isFinite, milliseconds >= 0 else { return }
        processing.append((time, milliseconds))
        processing.removeAll { $0.time <= time - window }
        if processing.count > 1200 { processing.removeFirst(processing.count - 1200) }
        if let age = captureToSendMs, age.isFinite, (0...2000).contains(age) {
            captureToSend.append((time, age))
        }
        captureToSend.removeAll { $0.time <= time - window }
        if captureToSend.count > 1200 { captureToSend.removeFirst(captureToSend.count - 1200) }
    }

    private static func median(_ values: [Double]) -> Double? {
        let sorted = values.sorted()
        let count = sorted.count
        return count == 0 ? nil : count.isMultiple(of: 2)
            ? (sorted[count / 2 - 1] + sorted[count / 2]) / 2 : sorted[count / 2]
    }

    func snapshot(at time: TimeInterval) -> DeviceHealthRates {
        lock.lock(); defer { lock.unlock() }
        guard let started, time > started else { return DeviceHealthRates() }
        cameraTimes.removeAll { $0 <= time - window }
        processing.removeAll { $0.time <= time - window }
        captureToSend.removeAll { $0.time <= time - window }
        let duration = min(window, time - started)
        return DeviceHealthRates(cameraFPS: Double(cameraTimes.count) / duration,
                                 processingFPS: Double(processing.count) / duration,
                                 medianProcessingMs: Self.median(processing.map(\.ms)), ready: time - started >= 3,
                                 medianCaptureToSendMs: Self.median(captureToSend.map(\.ms)))
    }
}
