import Foundation

// 送信の意思と実際のカメラ・通信状態を分離する。停止操作だけが意思を消す。
struct SendingResumePolicy {
    static let wasSendingKey = "PhoneSaber.wasSending"
    static let autoStartKey = "PhoneSaber.autoStartSending"
    private(set) var wantsSending: Bool
    private var evaluatedStartup = false

    init(wasSending: Bool = false) { wantsSending = wasSending }

    mutating func foreground(autoStart: Bool) -> Bool {
        if !evaluatedStartup {
            evaluatedStartup = true
            wantsSending = wantsSending || autoStart
        }
        return wantsSending
    }

    mutating func start() { evaluatedStartup = true; wantsSending = true }
    mutating func stop() { evaluatedStartup = true; wantsSending = false }
}

// 再試行間隔は1・2・4…秒、最大30秒。連続15分以降も60秒ごとに復旧を試す。
struct RecoveryBackoff {
    private(set) var attempts = 0
    private var failedSince: TimeInterval?
    private var healthySince: TimeInterval?
    private var lastFrameAt: TimeInterval?
    let limit: TimeInterval

    init(limit: TimeInterval = 15 * 60) { self.limit = limit }

    mutating func nextDelay(at time: TimeInterval) -> TimeInterval? {
        healthySince = nil
        lastFrameAt = nil
        if failedSince == nil { failedSince = time }
        let remaining = limit - (time - failedSince!)
        guard remaining > 0 else { return 60 }
        let delay = min(30, pow(2, Double(min(attempts, 5))))
        attempts += 1
        return min(delay, remaining)
    }

    // 1フレームだけの復帰では失敗期間をリセットしない。
    mutating func receivedFrame(at time: TimeInterval) {
        // watchdogの処理が遅れても、フレームの途切れを安定稼働に含めない。
        if let lastFrameAt, time - lastFrameAt >= CameraLifecycleStateMachine.frameStallTimeout {
            healthySince = nil
        }
        lastFrameAt = time
        if healthySince == nil { healthySince = time }
        if time - healthySince! >= 10 { reset() }
    }

    mutating func reset() { attempts = 0; failedSince = nil; healthySince = nil; lastFrameAt = nil }
}

/// 古い startRunning の完了通知の扱い。停止→即再開や fps 切替では、前回の start が
/// 終わる前に新しい run が始まる。そのとき古い完了通知で session を止めると、直列キュー上で
/// 新しい startRunning の後に stopRunning が入り、新しい run のカメラが止まる
/// （2秒の stall 検出と backoff 再起動まで座標が途切れる）。止めるのは送信停止中だけ。
enum CameraStartCompletionPolicy {
    enum Action: Equatable { case apply, ignore, stopSession }

    static func action(completedLifecycle: Int, currentLifecycle: Int, running: Bool) -> Action {
        if completedLifecycle == currentLifecycle && running { return .apply }
        // 新しい run が session を持っている。stop() は既に同期で止めてから再開している。
        if running { return .ignore }
        return .stopSession
    }
}

// 非同期 stop の前後で前面状態が変わっても、背面では再設定しない。
enum CameraRecoveryCompletionPolicy {
    static func canConfigure(sending: Bool, foreground: Bool) -> Bool { sending && foreground }
}

struct FPSComparisonPolicy {
    static func lowerFPS(highFPS: Int, supports120: Bool) -> Int {
        highFPS == 120 && supports120 ? 60 : 30
    }
    static func nextFPS(current: Int, highFPS: Int, supports120: Bool) -> Int {
        let lower = lowerFPS(highFPS: highFPS, supports120: supports120)
        return current == lower ? lower * 2 : lower
    }

    private var enabledAt: TimeInterval?
    mutating func setEnabled(_ enabled: Bool, at time: TimeInterval) { enabledAt = enabled ? time : nil }
    func shouldDisable(at time: TimeInterval) -> Bool {
        enabledAt.map { time - $0 >= 5 * 60 } ?? false
    }
}
