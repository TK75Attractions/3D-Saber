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
                        requestedFPS: Double, running: Bool, batteryLevel: Double? = nil,
                        batteryState: String = "unknown", lowPowerMode: Bool = false) -> String? {
        var reasons: [String] = []
        if thermal == .high || thermal == .dangerous { reasons.append("発熱が\(thermal.title)") }
        if running && rates.ready && requestedFPS > 0 &&
            min(rates.cameraFPS, rates.processingFPS) < requestedFPS * 0.7 {
            reasons.append("fpsが要求値の70%未満")
        }
        reasons += DevicePowerWarningPolicy.reasons(level: batteryLevel, state: batteryState,
                                                  lowPowerMode: lowPowerMode, sending: running)
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
    var p95CaptureToSendMs: Double?
    var redSendFPS = 0.0
    var blueSendFPS = 0.0
}

// 数値入力不要の比較ログ。停止・再設定後は新しい10秒区間から始める。
struct CaptureLatencyLogPolicy {
    private var lastLog: TimeInterval?

    mutating func shouldLog(at time: TimeInterval, sending: Bool) -> Bool {
        guard sending else { lastLog = nil; return false }
        guard let lastLog else { self.lastLog = time; return false }
        guard time - lastLog >= 10 else { return false }
        self.lastLog = time
        return true
    }

    static func line(route: String, captureModeFPS: Double, rates: DeviceHealthRates,
                     thermal: DeviceThermalLevel) -> String {
        let median = rates.medianCaptureToSendMs.map { String(format: "%.1f", $0) } ?? "unavailable"
        let p95 = rates.p95CaptureToSendMs.map { String(format: "%.1f", $0) } ?? "unavailable"
        return String(format: "route=%@ capture-mode-fps=%.0f capture-fps=%.1f processed-fps=%.1f send-fps-red=%.1f send-fps-blue=%.1f capture-to-send-median-ms=%@ capture-to-send-p95-ms=%@ thermal=%@ window-s=5",
                      route, captureModeFPS, rates.cameraFPS, rates.processingFPS, rates.redSendFPS,
                      rates.blueSendFPS, median, p95, thermal.metadataValue)
    }
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
    private var sends: [(time: TimeInterval, port: Int)] = []
    private let window = 5.0

    func reset(at time: TimeInterval, generation: Int) {
        lock.lock(); defer { lock.unlock() }
        started = time; self.generation = generation
        cameraTimes.removeAll(keepingCapacity: true); processing.removeAll(keepingCapacity: true)
        captureToSend.removeAll(keepingCapacity: true)
        sends.removeAll(keepingCapacity: true)
    }

    // ローカル送信完了の頻度。PCでの受信確認を意味しない。
    func sent(at time: TimeInterval, port: Int, generation: Int) {
        lock.lock(); defer { lock.unlock() }
        guard generation == self.generation, port == 5005 || port == 5006 else { return }
        sends.append((time, port))
        sends.removeAll { $0.time <= time - window }
        if sends.count > 2400 { sends.removeFirst(sends.count - 2400) }
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
        // UIのソート中に検出・capture・UDPキューを待たせない。
        lock.lock()
        let started = started
        let cameraTimes = cameraTimes
        let processing = processing
        let captureToSend = captureToSend
        let sends = sends
        lock.unlock()
        guard let started, time > started else { return DeviceHealthRates() }
        let liveCamera = cameraTimes.filter { $0 > time - window }
        let liveProcessing = processing.filter { $0.time > time - window }
        let ages = captureToSend.filter { $0.time > time - window }.map(\.ms).sorted()
        let liveSends = sends.filter { $0.time > time - window }
        let duration = min(window, time - started)
        return DeviceHealthRates(cameraFPS: Double(liveCamera.count) / duration,
                                 processingFPS: Double(liveProcessing.count) / duration,
                                 medianProcessingMs: Self.median(liveProcessing.map(\.ms)), ready: time - started >= 3,
                                 medianCaptureToSendMs: Self.median(ages),
                                 p95CaptureToSendMs: ages.isEmpty ? nil : ages[Int(ceil(Double(ages.count) * 0.95)) - 1],
                                 redSendFPS: Double(liveSends.filter { $0.port == 5005 }.count) / duration,
                                 blueSendFPS: Double(liveSends.filter { $0.port == 5006 }.count) / duration)
    }
}

// 単調時計で冷却の継続時間と切替間隔を判定する。保存済み fps は変更しない。
struct ThermalFPSPolicy {
    private(set) var reduced = false
    private var coolSince: TimeInterval?
    private var lastChange: TimeInterval?
    static let minimumInterval: TimeInterval = 120

    static func shouldReconfigure(currentFPS: Double, effectiveFPS: Int, sending: Bool,
                                  foreground: Bool, cameraLive: Bool, recovering: Bool) -> Bool {
        sending && foreground && cameraLive && !recovering && currentFPS != Double(effectiveFPS)
    }

    mutating func effectiveFPS(thermal: DeviceThermalLevel, at time: TimeInterval, userFPS: Int) -> Int {
        let hot = thermal == .high || thermal == .dangerous
        let cool = thermal == .normal || thermal == .elevated
        if cool {
            if coolSince == nil { coolSince = time }
        } else {
            coolSince = nil
        }
        guard userFPS == 60 || userFPS == 120 else { return userFPS }
        let canChange = lastChange.map { time - $0 >= Self.minimumInterval } ?? true
        if !reduced && hot && (canChange || userFPS == 120) {
            reduced = true
            lastChange = time
        } else if reduced && cool, let coolSince,
                  time - coolSince >= Self.minimumInterval && canChange {
            reduced = false
            lastChange = time
        }
        return reduced ? 30 : userFPS
    }
}

enum DevicePowerWarningPolicy {
    static func reasons(level: Double?, state: String, lowPowerMode: Bool, sending: Bool) -> [String] {
        var reasons: [String] = []
        let charging = state == "charging" || state == "full"
        if sending && state == "unplugged" { reasons.append("充電されていません。ケーブルを確認") }
        if let level, level.isFinite, (0...1).contains(level) {
            if level <= 0.10 {
                reasons.append("電池が10%以下です。電源・ケーブルを確認")
            } else if level <= 0.20 && !charging {
                reasons.append("電池が20%以下で充電されていません。電源・ケーブルを確認")
            }
        }
        if lowPowerMode { reasons.append("低電力モードがONです。設定でOFFにしてください") }
        return reasons
    }
}
