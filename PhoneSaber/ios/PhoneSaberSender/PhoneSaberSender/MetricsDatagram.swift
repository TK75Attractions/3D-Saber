import Foundation

// 診断専用の純粋な整形。座標を通すキューからは呼ばない。
struct MetricsDatagram {
    var station = "?", platform = "ios", app = "unknown"
    var fpsMode = 60.0, redFPS = 0.0, blueFPS = 0.0
    var captureFPS: Double? = 0.0
    var median: Double?, p95: Double?, rtt: Double?, rttAge: Double?
    var samples = 0, rttSamples = 0
    var thermal = "unknown", battery: Double?, lowLatencyTest = false
    var c2sEnd = "detection"
    var controls: String?

    // 399 byte上限内で現在値を通知。順に追跡0/1、EVの符号反転、シャッター・ISOの選択index。
    static func controlState(trackLock: Bool, exposureBias: Int, shutter: Int, shutterISO: Int) -> String? {
        guard (0...2).contains(-exposureBias),
              let shutterIndex = [0, 500, 1000, 2000].firstIndex(of: shutter),
              let isoIndex = [2000, 1000, 500, 250].firstIndex(of: shutterISO) else { return nil }
        return "\(trackLock ? 1 : 0)\(-exposureBias)\(shutterIndex)\(isoIndex)"
    }

    private static func atom(_ value: String, limit: Int) -> String {
        let allowed = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.-?"
        let text = String(value.prefix(limit).map { allowed.contains($0) ? $0 : "_" })
        return text.isEmpty ? "?" : text
    }
    private static func number(_ value: Double?, maximum: Double) -> String {
        guard let value, value.isFinite, (0...maximum).contains(value) else { return "unknown" }
        let tenths = Int((value * 10 + 0.5).rounded(.down))
        return "\(tenths / 10).\(tenths % 10)"
    }
    func line() -> String? {
        guard fpsMode.isFinite, (1...240).contains(fpsMode) else { return nil }
        let c2sValid = median != nil && p95 != nil && samples > 0 && samples <= 2400 &&
            Self.number(median, maximum: 2000) != "unknown" && Self.number(p95, maximum: 2000) != "unknown" && p95! >= median!
        let rttValid = rtt != nil && rttAge != nil && rttSamples > 0 && rttSamples <= 2400 &&
            Self.number(rtt, maximum: 1000) != "unknown" && Self.number(rttAge, maximum: 10) != "unknown"
        var text = "PHONESABER_METRICS 1 station=\(Self.atom(station, limit: 16)) platform=\(platform) app=\(Self.atom(app, limit: 24))" +
            " fps-mode=\(Int(fpsMode.rounded())) capture-fps=\(Self.number(captureFPS, maximum: 1000))" +
            " send-red-fps=\(Self.number(redFPS, maximum: 1000)) send-blue-fps=\(Self.number(blueFPS, maximum: 1000))" +
            " c2s-median-ms=\(Self.number(c2sValid ? median : nil, maximum: 2000)) c2s-p95-ms=\(Self.number(c2sValid ? p95 : nil, maximum: 2000)) c2s-n=\(c2sValid ? samples : 0)" +
            " rtt-ms=\(Self.number(rttValid ? rtt : nil, maximum: 1000)) rtt-n=\(rttValid ? rttSamples : 0) rtt-age-s=\(Self.number(rttValid ? rttAge : nil, maximum: 10))" +
            " route=LAN thermal=\(Self.atom(thermal, limit: 16)) battery=\(Self.number(battery, maximum: 100)) low-latency-test=\(lowLatencyTest ? 1 : 0) c2s-end=\(c2sEnd)"
        if let controls {
            guard controls.count == 4, controls.first == "0" || controls.first == "1",
                  controls.dropFirst().first.map({ "012".contains($0) }) == true,
                  controls.suffix(2).allSatisfy({ "0123".contains($0) }) else { return nil }
            text += " ctl=\(controls)"
        }
        return text.utf8.count < 400 && text.data(using: .ascii) != nil ? text : nil
    }
}

struct MetricsCadence {
    private var last: Double?
    mutating func due(at now: Double, sending: Bool) -> Bool {
        guard sending else { last = nil; return false }
        guard let last else { self.last = now; return false }
        guard now - last >= 5 else { return false }
        self.last = now; return true
    }
}

// 直近10秒・最大16応答。時計同期不要。失敗と重複応答は標本にしない。
struct ProbeRoundTrip {
    private var pending: Double?
    private var values: [(at: Double, ms: Double)] = []
    mutating func sent(at now: Double) { pending = now }
    mutating func reply(at now: Double, timeout: Double = 0.45) {
        defer { pending = nil }
        guard let pending, now >= pending, now - pending <= timeout else { return }
        values.append((now, (now - pending) * 1000))
        values.removeAll { $0.at <= now - 10 }
        if values.count > 16 { values.removeFirst(values.count - 16) }
    }
    func snapshot(at now: Double) -> (median: Double?, count: Int, age: Double?) {
        // 呼出側の時刻取得と lock 取得の間に届いた応答は、次の集計で使う。
        let live = values.filter { $0.at <= now && now - $0.at <= 10 }
        let sorted = live.map(\.ms).sorted(), n = live.count
        guard n > 0, let last = live.last else { return (nil, 0, nil) }
        return (n.isMultiple(of: 2) ? (sorted[n / 2 - 1] + sorted[n / 2]) / 2 : sorted[n / 2], n, now - last.at)
    }
}
