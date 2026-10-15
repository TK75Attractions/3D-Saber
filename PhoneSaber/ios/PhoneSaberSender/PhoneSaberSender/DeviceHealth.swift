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
    var captureToSendSamples = 0
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
                     thermal: DeviceThermalLevel, brightness: Double? = nil,
                     batteryLevel: Double? = nil, batteryState: String = "unknown") -> String {
        let median = rates.medianCaptureToSendMs.map { String(format: "%.1f", $0) } ?? "unavailable"
        let p95 = rates.p95CaptureToSendMs.map { String(format: "%.1f", $0) } ?? "unavailable"
        // 充電条件と画面輝度も同じ時刻に残し、実機の発熱比較に使う。設定は変更しない。
        func unit(_ value: Double?, scale: Double) -> String {
            guard let value, value.isFinite, (0...1).contains(value) else { return "unknown" }
            return String(format: "%.1f", value * scale)
        }
        let state = ["charging", "full", "unplugged", "unknown"].contains(batteryState) ? batteryState : "unknown"
        return String(format: "route=%@ capture-mode-fps=%.0f capture-fps=%.1f processed-fps=%.1f send-fps-red=%.1f send-fps-blue=%.1f capture-to-send-median-ms=%@ capture-to-send-p95-ms=%@ thermal=%@ window-s=5",
                      route, captureModeFPS, rates.cameraFPS, rates.processingFPS, rates.redSendFPS,
                      rates.blueSendFPS, median, p95, thermal.metadataValue) +
            " battery-pct=\(unit(batteryLevel, scale: 100)) battery-state=\(state) screen-brightness=\(unit(brightness, scale: 1))"
    }
}

// 固定容量の観測窓。時刻順なら毎フレームの配列走査・移動・再確保を避ける。
// 時刻の絞り込みは snapshot 側でも行う（別キューの完了順が前後しても数えない）。
struct DeviceHealthSampleWindow<Value> {
    private var slots: [(time: TimeInterval, value: Value)?]
    private var head = 0
    private var count = 0
    private var ordered = true

    init(capacity: Int) {
        precondition(capacity > 0)
        slots = Array(repeating: nil, count: capacity)
    }

    mutating func append(at time: TimeInterval, value: Value, window: TimeInterval) {
        expire(at: time, window: window)
        if count > 0, let last = slots[(head + count - 1) % slots.count], !(last.time <= time) {
            ordered = false
        }
        if count == slots.count {
            slots[head] = nil
            head = (head + 1) % slots.count
            count -= 1
        }
        slots[(head + count) % slots.count] = (time, value)
        count += 1
    }

    mutating func expire(at time: TimeInterval, window: TimeInterval) {
        while count > 0, let oldest = slots[head], oldest.time <= time - window {
            slots[head] = nil
            head = (head + 1) % slots.count
            count -= 1
        }
        // 完了順が逆転した窓だけ全体を詰める。削除済みの標本を遅れた集計で復活させない。
        if !ordered {
            var retained = 0
            var previous: TimeInterval?
            ordered = true
            for offset in 0..<count {
                if let sample = slots[(head + offset) % slots.count], !(sample.time <= time - window) {
                    if let previous, !(previous <= sample.time) { ordered = false }
                    previous = sample.time
                    slots[(head + retained) % slots.count] = sample
                    retained += 1
                }
            }
            for offset in retained..<count { slots[(head + offset) % slots.count] = nil }
            count = retained
        }
        if count == 0 { ordered = true }
    }

    mutating func reset() {
        for index in slots.indices { slots[index] = nil }
        head = 0; count = 0; ordered = true
    }

    func recent(at time: TimeInterval, window: TimeInterval) -> [(time: TimeInterval, value: Value)] {
        var values: [(time: TimeInterval, value: Value)] = []
        values.reserveCapacity(count)
        for offset in 0..<count {
            if let sample = slots[(head + offset) % slots.count], sample.time > time - window {
                values.append(sample)
            }
        }
        return values
    }
}

// キャプチャ・処理・UIの各キューから観測するだけ。認識や送信には使わない。
final class DeviceHealthMeter: @unchecked Sendable {
    private let lock = NSLock()
    private var started: TimeInterval?
    private var generation = 0
    private var cameraTimes = DeviceHealthSampleWindow<Void>(capacity: 1200)
    private var processing = DeviceHealthSampleWindow<Double>(capacity: 1200)
    // 撮影時刻（AVCapture のホスト時計）から認識完了＝送信直前まで。0〜2秒の範囲外は捨てる。
    private var captureToSend = DeviceHealthSampleWindow<Double>(capacity: 1200)
    private var sends = DeviceHealthSampleWindow<Int>(capacity: 2400)
    private let window = 5.0

    func reset(at time: TimeInterval, generation: Int) {
        lock.lock(); defer { lock.unlock() }
        started = time; self.generation = generation
        cameraTimes.reset(); processing.reset()
        captureToSend.reset(); sends.reset()
    }

    // ローカル送信完了の頻度。PCでの受信確認を意味しない。
    func sent(at time: TimeInterval, port: Int, generation: Int) {
        lock.lock(); defer { lock.unlock() }
        guard generation == self.generation, port == 5005 || port == 5006 else { return }
        sends.append(at: time, value: port, window: window)
    }

    func cameraFrame(at time: TimeInterval) {
        lock.lock(); defer { lock.unlock() }
        cameraTimes.append(at: time, value: (), window: window)
    }

    func processed(at time: TimeInterval, milliseconds: Double, generation: Int, captureToSendMs: Double? = nil) {
        lock.lock(); defer { lock.unlock() }
        guard generation == self.generation, milliseconds.isFinite, milliseconds >= 0 else { return }
        processing.append(at: time, value: milliseconds, window: window)
        if let age = captureToSendMs, age.isFinite, (0...2000).contains(age) {
            captureToSend.append(at: time, value: age, window: window)
        } else {
            captureToSend.expire(at: time, window: window)
        }
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
        let liveCamera = cameraTimes.recent(at: time, window: window)
        let liveProcessing = processing.recent(at: time, window: window)
        let captureToSend = captureToSend.recent(at: time, window: window)
        let liveSends = sends.recent(at: time, window: window)
        lock.unlock()
        guard let started, time > started else { return DeviceHealthRates() }
        let ages = captureToSend.map(\.value).sorted()
        let duration = min(window, time - started)
        return DeviceHealthRates(cameraFPS: Double(liveCamera.count) / duration,
                                 processingFPS: Double(liveProcessing.count) / duration,
                                 medianProcessingMs: Self.median(liveProcessing.map(\.value)), ready: time - started >= 3,
                                 medianCaptureToSendMs: Self.median(ages),
                                 p95CaptureToSendMs: ages.isEmpty ? nil : ages[Int(ceil(Double(ages.count) * 0.95)) - 1],
                                 redSendFPS: Double(liveSends.filter { $0.value == 5005 }.count) / duration,
                                 blueSendFPS: Double(liveSends.filter { $0.value == 5006 }.count) / duration,
                                 captureToSendSamples: ages.count)
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
