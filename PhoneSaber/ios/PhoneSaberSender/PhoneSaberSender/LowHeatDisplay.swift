import AVFoundation
import SwiftUI
import UIKit
import UIKit.UIGestureRecognizerSubclass

/// 低発熱表示の副作用（画面輝度と preview の connection）を持つ。判断は LowHeatDisplayPolicy。
/// preview の layer は作り直さず使い続ける。running 中の session に preview layer を足し引きすると
/// session の再構成になり、座標の送信が途切れることがあるため。
@MainActor
final class LowHeatDisplayController {
    struct Screen {
        var brightness: @MainActor () -> Double
        var setBrightness: @MainActor (Double) -> Void

        static let main = Screen(
            brightness: { Double(UIScreen.main.brightness) },
            setBrightness: { UIScreen.main.brightness = CGFloat($0) }
        )
    }

    private(set) var policy: LowHeatDisplayPolicy
    private let screen: Screen
    private let defaults: UserDefaults?
    private(set) var savedBrightness: Double?
    private var storedPreviewView: PreviewView?
    private var applicationObservers: [NSObjectProtocol] = []
    /// 暗い/明るいが変わったとき（通知経由の復帰も含む）に呼ぶ。
    var onDimmedChange: (@MainActor (Bool) -> Void)?

    var isDimmed: Bool { policy.dimmed }
    var isEnabled: Bool { policy.enabled }

    /// - Parameters:
    ///   - defaults: 設定と、異常終了時の輝度の退避先。nil ならどちらも保存しない（テスト用）。
    ///   - observeApplication: 前面を離れる・終了する通知で必ず輝度を戻す。
    init(enabled: Bool, now: TimeInterval, screen: Screen = .main, defaults: UserDefaults? = .standard,
         observeApplication: Bool = true, idleSeconds: TimeInterval = LowHeatDisplayPolicy.idleSeconds) {
        policy = LowHeatDisplayPolicy(enabled: enabled, now: now, idleSeconds: idleSeconds)
        self.screen = screen
        self.defaults = defaults
        // 前回、暗いまま異常終了していたら輝度を戻す。
        if let defaults, let pending = LowHeatDisplayPolicy.pendingRestoreBrightness(
            defaults.object(forKey: LowHeatDisplayPolicy.savedBrightnessKey)) {
            screen.setBrightness(pending)
            defaults.removeObject(forKey: LowHeatDisplayPolicy.savedBrightnessKey)
        }
        guard observeApplication else { return }
        let center = NotificationCenter.default
        for name in [UIApplication.willResignActiveNotification, UIApplication.didEnterBackgroundNotification,
                     UIApplication.willTerminateNotification] {
            applicationObservers.append(center.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in
                MainActor.assumeIsolated { self?.leaveForeground() }
            })
        }
    }

    deinit {
        applicationObservers.forEach(NotificationCenter.default.removeObserver)
    }

    /// 使い続ける preview view。最初に表示されたときに作る。
    var previewView: PreviewView {
        if let storedPreviewView { return storedPreviewView }
        let view = PreviewView()
        storedPreviewView = view
        return view
    }

    @discardableResult
    func touch(at time: TimeInterval) -> Bool { apply(policy.touch(at: time)) }

    @discardableResult
    func update(sending: Bool, foreground: Bool, inhibited: Bool, at time: TimeInterval) -> Bool {
        apply(policy.update(sending: sending, foreground: foreground, inhibited: inhibited, at: time))
    }

    @discardableResult
    func setEnabled(_ enabled: Bool, at time: TimeInterval) -> Bool {
        apply(policy.setEnabled(enabled, at: time))
    }

    @discardableResult
    func tick(at time: TimeInterval) -> Bool { apply(policy.tick(at: time)) }

    /// 前面を離れる・終了するときは、SwiftUI の scenePhase を待たずに輝度を戻す。
    func leaveForeground() {
        apply(policy.update(sending: policy.sending, foreground: false, inhibited: policy.inhibited,
                            at: ProcessInfo.processInfo.systemUptime))
    }

    /// session の再設定で preview の connection は作り直される。暗い間は止めたままにする。
    func reapplyPreviewState() {
        setPreviewEnabled(!policy.dimmed)
    }

    /// - Returns: 暗い/明るいが変わったら true。
    @discardableResult
    private func apply(_ change: LowHeatDisplayPolicy.Change) -> Bool {
        switch change {
        case .none:
            return false
        case .dim:
            let current = screen.brightness()
            savedBrightness = current
            defaults?.set(current, forKey: LowHeatDisplayPolicy.savedBrightnessKey)
            if current > LowHeatDisplayPolicy.dimmedBrightness {
                screen.setBrightness(LowHeatDisplayPolicy.dimmedBrightness)
            }
            setPreviewEnabled(false)
            ConnectivityLog.shared.recordToFileOnly("LOW_HEAT_DISPLAY", String(format: "on saved-brightness=%.2f", current))
        case .restore:
            if let savedBrightness { screen.setBrightness(savedBrightness) }
            ConnectivityLog.shared.recordToFileOnly("LOW_HEAT_DISPLAY", String(format: "off restored-brightness=%.2f",
                                                                             savedBrightness ?? -1))
            savedBrightness = nil
            defaults?.removeObject(forKey: LowHeatDisplayPolicy.savedBrightnessKey)
            setPreviewEnabled(true)
        }
        onDimmedChange?(policy.dimmed)
        return true
    }

    private func setPreviewEnabled(_ enabled: Bool) {
        // connection を止めるだけ。session・video data output・layer は変更しない。
        guard let connection = storedPreviewView?.videoPreviewLayer.connection,
              connection.isEnabled != enabled else { return }
        connection.isEnabled = enabled
    }
}

/// 低発熱表示中の画面。黒地に状態の1行だけを出す。どこに触れても元に戻る。
struct LowHeatScreen: View {
    let line: String
    let wake: () -> Void

    var body: some View {
        Color.black
            .ignoresSafeArea()
            .overlay {
                Text(line)
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(Color.white.opacity(0.7))
                    .multilineTextAlignment(.center)
                    .padding()
            }
            .contentShape(Rectangle())
            .onTapGesture(perform: wake)
            .accessibilityIdentifier("lowHeatDisplay")
    }
}

/// window 全体の touch を、ほかの操作を邪魔せずに知らせる。ScrollView・Button・sheet 内の touch も含む。
struct TouchActivityObserver: UIViewRepresentable {
    let onTouch: @MainActor () -> Void

    func makeUIView(context: Context) -> TouchActivityView {
        let view = TouchActivityView()
        view.onTouch = onTouch
        return view
    }

    func updateUIView(_ uiView: TouchActivityView, context: Context) {
        uiView.onTouch = onTouch
    }
}

final class TouchActivityView: UIView {
    var onTouch: (@MainActor () -> Void)? {
        didSet { recognizer.onTouch = onTouch }
    }
    private let recognizer = TouchActivityRecognizer()

    override init(frame: CGRect) {
        super.init(frame: frame)
        isUserInteractionEnabled = false
        isHidden = true
    }

    required init?(coder: NSCoder) { fatalError("init(coder:) has not been implemented") }

    override func didMoveToWindow() {
        super.didMoveToWindow()
        recognizer.view?.removeGestureRecognizer(recognizer)
        window?.addGestureRecognizer(recognizer)
    }
}

/// touch を受けたらすぐ failed になる。touch を取り消さず、遅らせず、ほかの recognizer とも同時に動く。
final class TouchActivityRecognizer: UIGestureRecognizer, UIGestureRecognizerDelegate {
    var onTouch: (@MainActor () -> Void)?

    init() {
        super.init(target: nil, action: nil)
        cancelsTouchesInView = false
        delaysTouchesBegan = false
        delaysTouchesEnded = false
        delegate = self
    }

    override func touchesBegan(_ touches: Set<UITouch>, with event: UIEvent) {
        super.touchesBegan(touches, with: event)
        onTouch?()
        state = .failed
    }

    func gestureRecognizer(_ gestureRecognizer: UIGestureRecognizer,
                           shouldRecognizeSimultaneouslyWith otherGestureRecognizer: UIGestureRecognizer) -> Bool {
        true
    }
}
