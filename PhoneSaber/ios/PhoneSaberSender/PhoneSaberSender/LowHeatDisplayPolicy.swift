import Foundation

/// 低発熱表示: 送信中に画面へ一定時間触れなければ、カメラ preview の描画を止めて画面を暗くする。
/// 本番の iPhone は閉じた箱の中にあり、プレイ中は誰も画面を見ない（2026-10-17 実測で発熱 serious が続き 60→30fps に落ちた）。
/// capture session・video data output・認識・UDP 送信には触れない。表示だけの状態機械で、UIKit なしで host test できる。
struct LowHeatDisplayPolicy: Equatable {
    static let idleSeconds: TimeInterval = 20
    /// 暗くしたときの画面輝度（0...1）。OLED の黒は消灯なので、状態の1行が読める程度に残す。
    static let dimmedBrightness = 0.05
    /// 設定の保存先。起動引数 `-PhoneSaber.lowHeatDisplay NO` でもその起動だけ OFF にできる。
    static let preferenceKey = "PhoneSaber.lowHeatDisplay"
    /// 暗くする前の輝度。異常終了で戻せなかったとき、次の起動で戻すために残す。
    static let savedBrightnessKey = "PhoneSaber.lowHeatDisplay.savedBrightness"

    enum Change: Equatable { case none, dim, restore }

    let idleSeconds: TimeInterval
    private(set) var enabled: Bool
    private(set) var sending = false
    private(set) var foreground = true
    /// ガイド付き録画・Debug Recording・録画の再生中など、人が画面を見ている間は暗くしない。
    private(set) var inhibited = false
    private(set) var lastActivity: TimeInterval
    private(set) var dimmed = false

    init(enabled: Bool, now: TimeInterval, idleSeconds: TimeInterval = LowHeatDisplayPolicy.idleSeconds) {
        self.enabled = enabled
        self.lastActivity = now
        self.idleSeconds = idleSeconds
    }

    /// 暗くしてよい条件。どれか1つでも外れたら即座に元へ戻す。
    var eligible: Bool { enabled && sending && foreground && !inhibited }

    /// 画面に触れた。暗い間なら元に戻し、待ち時間を数え直す。
    mutating func touch(at time: TimeInterval) -> Change {
        lastActivity = time
        return restoreIfDimmed()
    }

    /// 送信・前面・抑止の状態を反映する。送信開始・前面復帰・抑止の解除から20秒を数え直す。
    mutating func update(sending: Bool, foreground: Bool, inhibited: Bool, at time: TimeInterval) -> Change {
        if (sending && !self.sending) || (foreground && !self.foreground) || (!inhibited && self.inhibited) {
            lastActivity = time
        }
        self.sending = sending
        self.foreground = foreground
        self.inhibited = inhibited
        return eligible ? .none : restoreIfDimmed()
    }

    mutating func setEnabled(_ enabled: Bool, at time: TimeInterval) -> Change {
        if enabled && !self.enabled { lastActivity = time }
        self.enabled = enabled
        return eligible ? .none : restoreIfDimmed()
    }

    /// 定期的（1秒ごと）に呼ぶ。条件を満たしたまま idleSeconds 経てば暗くする。
    mutating func tick(at time: TimeInterval) -> Change {
        guard eligible, !dimmed, time - lastActivity >= idleSeconds else { return .none }
        dimmed = true
        return .dim
    }

    private mutating func restoreIfDimmed() -> Change {
        guard dimmed else { return .none }
        dimmed = false
        return .restore
    }

    /// 暗い画面に出す1行。更新は呼び出し側で1秒に1回まで。
    static func statusLine(state: String, cameraFPS: Double, thermal: String) -> String {
        "\(state)　カメラ \(Int(cameraFPS.rounded())) fps　発熱 \(thermal)　（触れると元に戻ります）"
    }

    /// 前回の異常終了で戻せなかった輝度。範囲外の値は捨てる。
    static func pendingRestoreBrightness(_ stored: Any?) -> Double? {
        guard let value = (stored as? NSNumber)?.doubleValue, value.isFinite, (0...1).contains(value) else { return nil }
        return value
    }
}
