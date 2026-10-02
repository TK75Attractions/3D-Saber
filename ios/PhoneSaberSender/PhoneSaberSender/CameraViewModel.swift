import AVFoundation
import Foundation
import Network
import SwiftUI
import Darwin
import UIKit

enum DestinationHostSource: String, Equatable {
    case automatic = "Auto (Bonjour待機)"
    case bonjour = "Auto (Bonjour)"
    case manual = "Manual"
}

struct DestinationHostSelection: Equatable {
    private(set) var host = ""
    private(set) var source: DestinationHostSource = .automatic
    private(set) var bonjourServiceName = ""

    mutating func setManual(_ value: String, resolvedHost: String, serviceName: String) {
        host = value
        if value.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
            if resolvedHost.isEmpty {
                source = .automatic
                bonjourServiceName = ""
            } else {
                host = resolvedHost
                source = .bonjour
                bonjourServiceName = serviceName
            }
        } else {
            source = .manual
            bonjourServiceName = ""
        }
    }

    @discardableResult
    mutating func applyBonjour(host resolvedHost: String, serviceName: String) -> Bool {
        guard !resolvedHost.isEmpty, source != .manual,
              bonjourServiceName.isEmpty || bonjourServiceName == serviceName else { return false }
        let changed = host != resolvedHost
        host = resolvedHost
        source = .bonjour
        bonjourServiceName = serviceName
        return changed
    }
}

struct CameraFormatOption: Equatable {
    let index: Int
    let width: Int32
    let height: Int32
    let frameRateRanges: [ClosedRange<Double>]

    var pixelCount: Int64 { Int64(width) * Int64(height) }
}

func supportsFrameRate(_ fps: Double, ranges: [ClosedRange<Double>]) -> Bool {
    ranges.contains { range in
        range.lowerBound - 0.01 <= fps && fps <= range.upperBound + 0.01
    }
}

func fixedFrameDuration(fps: Double, ranges: [ClosedRange<Double>]) -> CMTime? {
    guard fps > 0, supportsFrameRate(fps, ranges: ranges) else { return nil }
    return CMTime(seconds: 1 / fps, preferredTimescale: 60_000)
}

func preferredCameraFormatIndex(
    options: [CameraFormatOption],
    targetFPS: Double = 30,
    maximumWidth: Int32 = 640,
    maximumHeight: Int32 = 480
) -> Int? {
    let supported = options.filter { supportsFrameRate(targetFPS, ranges: $0.frameRateRanges) }
    guard !supported.isEmpty else { return nil }
    let compact = supported.filter { $0.width <= maximumWidth && $0.height <= maximumHeight }
    let usesCompactPool = !compact.isEmpty
    let pool = usesCompactPool ? compact : supported
    return pool.sorted { lhs, rhs in
        if lhs.pixelCount != rhs.pixelCount {
            return usesCompactPool ? lhs.pixelCount > rhs.pixelCount : lhs.pixelCount < rhs.pixelCount
        }
        let lhsCeiling = lhs.frameRateRanges.filter { supportsFrameRate(targetFPS, ranges: [$0]) }.map(\.upperBound).min() ?? .infinity
        let rhsCeiling = rhs.frameRateRanges.filter { supportsFrameRate(targetFPS, ranges: [$0]) }.map(\.upperBound).min() ?? .infinity
        if lhsCeiling != rhsCeiling { return lhsCeiling < rhsCeiling }
        return lhs.index < rhs.index
    }.first?.index
}

struct CameraFrameIntervalStatistics: Equatable {
    let sampleCount: Int
    let latestMs: Double
    let medianMs: Double
    let minimumMs: Double
    let maximumMs: Double
    var measuredFPS: Double { medianMs > 0 ? 1000 / medianMs : 0 }
}

struct CameraFrameIntervalWindow {
    private let capacity: Int
    private var previousPresentationTime: TimeInterval?
    private var intervalsMs: [Double] = []

    init(capacity: Int = 120) { self.capacity = max(1, capacity) }

    mutating func reset() {
        previousPresentationTime = nil
        intervalsMs = []
    }

    mutating func record(presentationTime: TimeInterval?) {
        guard let presentationTime, presentationTime.isFinite else { return }
        defer { previousPresentationTime = presentationTime }
        guard let previousPresentationTime else { return }
        let interval = presentationTime - previousPresentationTime
        guard interval > 0, interval < 1 else {
            intervalsMs = []
            return
        }
        intervalsMs.append(interval * 1000)
        if intervalsMs.count > capacity { intervalsMs.removeFirst(intervalsMs.count - capacity) }
    }

    var statistics: CameraFrameIntervalStatistics? {
        Self.statistics(for: intervalsMs)
    }

    var samples: [Double] { intervalsMs }

    static func statistics(for intervalsMs: [Double]) -> CameraFrameIntervalStatistics? {
        guard !intervalsMs.isEmpty else { return nil }
        let sorted = intervalsMs.sorted()
        let middle = sorted.count / 2
        let median = sorted.count.isMultiple(of: 2)
            ? (sorted[middle - 1] + sorted[middle]) / 2
            : sorted[middle]
        return CameraFrameIntervalStatistics(
            sampleCount: intervalsMs.count,
            latestMs: intervalsMs.last ?? 0,
            medianMs: median,
            minimumMs: sorted.first ?? 0,
            maximumMs: sorted.last ?? 0
        )
    }
}

enum CameraLifecycleState: Equatable {
    case stopped
    case starting
    case live
    case stalled
    case interrupted(String)
    case recovering
    case failed(String)

    var displayLabel: String {
        switch self {
        case .stopped: return "CAMERA STOPPED"
        case .starting: return "CAMERA STARTING"
        case .live: return "CAMERA LIVE"
        case .stalled: return "CAMERA STALLED"
        case .interrupted: return "CAMERA INTERRUPTED"
        case .recovering: return "CAMERA RECOVERING"
        case .failed: return "CAMERA FAILED"
        }
    }

    var detail: String? {
        switch self {
        case .interrupted(let reason), .failed(let reason): return reason
        case .starting: return "最初のcamera frameを待っています"
        case .stalled: return "最終frameから2秒以上経過しました"
        case .recovering: return "capture sessionを再設定しています"
        case .stopped, .live: return nil
        }
    }

    var canRetry: Bool {
        switch self {
        case .stalled, .interrupted(_), .failed(_): return true
        case .stopped, .starting, .live, .recovering: return false
        }
    }
}

/// Tracks whether AVCapture is actively producing frames. Network readiness is
/// deliberately kept outside this state machine.
struct CameraLifecycleStateMachine {
    static let frameStallTimeout: TimeInterval = 2.0

    private(set) var state: CameraLifecycleState = .stopped
    private(set) var lastFrameAt: TimeInterval?
    private(set) var isForeground = true
    private(set) var isSending = false
    private var captureStartAt: TimeInterval?

    mutating func requestStart(at time: TimeInterval) {
        isSending = true
        state = .starting
        lastFrameAt = nil
        captureStartAt = time
    }

    mutating func sessionStarted(at time: TimeInterval) {
        guard isSending else { return }
        if state == .live { return }
        state = .starting
        captureStartAt = time
    }

    mutating func receivedFrame(at time: TimeInterval) {
        guard isSending else { return }
        switch state {
        case .starting:
            guard let startTime = captureStartAt, time >= startTime else { return }
            lastFrameAt = time
            captureStartAt = nil
            state = .live
        case .live, .stalled:
            if let lastFrameAt, time < lastFrameAt { return }
            lastFrameAt = time
            captureStartAt = nil
            state = .live
        case .stopped, .interrupted, .recovering, .failed:
            return
        }
    }

    @discardableResult
    mutating func evaluateStall(at time: TimeInterval,
                                timeout: TimeInterval = frameStallTimeout) -> Bool {
        guard isSending, isForeground else { return false }
        let reference: TimeInterval?
        switch state {
        case .starting:
            reference = captureStartAt
        case .live:
            reference = lastFrameAt
        case .stopped, .stalled, .interrupted, .recovering, .failed:
            return false
        }
        guard let reference, time >= reference, time - reference >= timeout else { return false }
        state = .stalled
        return true
    }

    mutating func interruptionBegan(reason: String) {
        guard isSending else { return }
        state = .interrupted(reason)
    }

    @discardableResult
    mutating func interruptionEnded(at time: TimeInterval) -> Bool {
        guard case .interrupted = state else { return false }
        return beginRecovery(at: time)
    }

    mutating func runtimeError(_ message: String) {
        guard isSending else { return }
        state = .failed(message)
    }

    mutating func fail(_ message: String) {
        state = .failed(message)
    }

    @discardableResult
    mutating func beginRecovery(at time: TimeInterval) -> Bool {
        guard isSending, isForeground else { return false }
        state = .recovering
        captureStartAt = time
        return true
    }

    mutating func restartFinished(succeeded: Bool, at time: TimeInterval, error: String? = nil) {
        guard isSending, state == .recovering else { return }
        if succeeded {
            state = .starting
            captureStartAt = time
        } else {
            state = .failed(error ?? "capture sessionを再開できませんでした")
        }
    }

    @discardableResult
    mutating func setForeground(_ active: Bool, at time: TimeInterval) -> Bool {
        isForeground = active
        guard active, isSending else { return false }
        _ = evaluateStall(at: time)
        switch state {
        case .stalled, .interrupted, .failed:
            return beginRecovery(at: time)
        case .starting:
            guard let captureStartAt, time - captureStartAt >= Self.frameStallTimeout else { return false }
            return beginRecovery(at: time)
        case .stopped, .live, .recovering:
            return false
        }
    }

    mutating func stop() {
        isSending = false
        state = .stopped
        lastFrameAt = nil
        captureStartAt = nil
    }
}

private final class CaptureSessionRunner: @unchecked Sendable {
    private let session: AVCaptureSession
    private let queue = DispatchQueue(label: "PhoneSaberSender.session", qos: .userInitiated)

    init(session: AVCaptureSession) {
        self.session = session
    }

    func stop(completion: (@MainActor @Sendable () -> Void)? = nil) {
        queue.async { [self] in
            session.stopRunning()
            guard let completion else { return }
            Task { @MainActor in completion() }
        }
    }

    func stopSynchronously() {
        queue.sync { session.stopRunning() }
    }

    func start(completion: @escaping @MainActor @Sendable (Bool) -> Void) {
        queue.async { [self] in
            session.startRunning()
            let succeeded = session.isRunning
            Task { @MainActor in completion(succeeded) }
        }
    }
}

@MainActor
final class CameraViewModel: NSObject, ObservableObject {
    let session = AVCaptureSession()
    nonisolated let processor: FrameProcessor
    private let output = AVCaptureVideoDataOutput()
    private let captureQueue = DispatchQueue(label: "PhoneSaberSender.capture", qos: .userInteractive)
    private lazy var sessionRunner = CaptureSessionRunner(session: session)
    private let sender: UDPSender
    private let pathMonitor = NWPathMonitor()
    private let bonjourDiscovery = BonjourDiscovery()
    @Published var running = false
    @Published var redEndpoints: (PixelPoint, PixelPoint)?
    @Published var blueEndpoints: (PixelPoint, PixelPoint)?
    @Published var fps = 0.0
    @Published var status = "停止中"
    @Published var errorMessage: String?
    @Published private(set) var cameraState: CameraLifecycleState = .stopped
    @Published private(set) var lastCameraFrameAge: TimeInterval?
    @Published private(set) var screenSleepPreventionActive = false
    @Published private(set) var pathStatus = "判定中"
    @Published private(set) var pathInterface = ""
    @Published private(set) var host = ""
    @Published private(set) var networkDiscoveryStatus = "Discovering..."
    @Published private(set) var discoveredMacName = ""
    @Published private(set) var discoveredMacIP = ""
    @Published private(set) var connectionMode = "Auto (Bonjour)"
    @Published var threshold = 145
    @Published var dominance = 25
    @Published var measurementMode = false
    @Published var outputWidth = 1920
    @Published var outputHeight = 1080
    @Published var mirrorX = false
    @Published var mirrorY = false
    @Published private(set) var activeDestination = "未設定"
    @Published private(set) var redDetectionCount = 0
    @Published private(set) var blueDetectionCount = 0
    @Published private(set) var redAttemptCount = 0
    @Published private(set) var blueAttemptCount = 0
    @Published private(set) var redCompletedCount = 0
    @Published private(set) var blueCompletedCount = 0
    @Published private(set) var redErrorCount = 0
    @Published private(set) var blueErrorCount = 0
    @Published private(set) var senderStates: [Int: String] = [:]
    @Published private(set) var senderErrors: [Int: String] = [:]
    @Published private(set) var sourceDimensions = (width: 1, height: 1)
    @Published private(set) var lastLocalSendMs: Double?
    @Published private(set) var lastSentEpoch: TimeInterval?
    // テストと診断用。検出数ではなく、現世代で処理を完了したフレーム数。
    @Published private(set) var processedFrameCount = 0
    @Published private(set) var authorizationCallbackCount = 0
    @Published private(set) var acceptedSenderUpdateCount = 0
    @Published private(set) var acceptedSenderUpdateGeneration = 0
    @Published private(set) var rejectedFrameCallbackCount = 0
    @Published private(set) var rejectedSenderUpdateCount = 0
    @Published private(set) var rawFrameSaveMessage = ""
    @Published private(set) var lastRawFrameURL: URL?
    @Published var debugRecordingEnabled = false
    /// Colors whose absence and anomalies count as diagnostic failures. Recognition
    /// and UDP output always cover both colors.
    @Published var debugDiagnosticColors: DebugDiagnosticColors = .both
    @Published private(set) var debugRecordingActive = false
    /// Operator ground truth for the current part of a Debug Recording
    /// (metadata only). Reset to unlabeled at every Start.
    @Published var debugSegmentLabel: DebugSegmentLabel = .unlabeled {
        didSet { processor.setDebugSegmentLabel(debugSegmentLabel) }
    }
    @Published private(set) var debugRecordingFinalizing = false
    @Published private(set) var manualLosslessCapturePending = false
    @Published private(set) var debugManualLosslessCaptureCount = 0
    @Published private(set) var debugRecordingStatus = "OFF"
    @Published private(set) var lastDebugRecordingResult: DebugRecordingResult?
    @Published private(set) var debugRecordingSessions: [DebugRecordingSessionSummary] = []
    @Published private(set) var debugRecordingCleanupStatus = ""
#if DEBUG
    @Published var freezeDiagnosticsEnabled = false {
        didSet {
            processor.setFreezeDiagnosticsEnabled(freezeDiagnosticsEnabled)
            sender.setFreezeDiagnosticsEnabled(freezeDiagnosticsEnabled)
        }
    }
    @Published private(set) var debugPerformanceRows = DebugPerformanceRow.placeholders
    @Published private(set) var debugCameraConfiguration = DebugCameraConfiguration.unavailable
    @Published private(set) var debugFrameIntervalStatistics: CameraFrameIntervalStatistics?
    @Published private(set) var debugRequestedFPS = 30
    @Published private(set) var debugSupports60FPS = false
    @Published private(set) var debug60FPSFormats = "Start the camera to inspect this device"
    @Published var debugDetailedProfilingEnabled = false {
        didSet { processor.setDetailedProfilingEnabled(debugDetailedProfilingEnabled) }
    }
    private var performanceMetrics: [String: PerformanceMetric] = [:]
    private var lastPerformancePublish = 0.0
    private var lastCameraSampleTime = 0.0
    private var previousTrace: FrameTrace?
    private var diagnosticEventTimes: [String: [TimeInterval]] = [:]
    private var latestDiagnosticSequence: UInt64 = 0
    private var latestFrameCounts = (received: 0, processed: 0, replaced: 0)
    private(set) var debugPerformancePublishCountForTesting = 0
    private weak var activeCamera: AVCaptureDevice?
    private var latestFrameIntervalStatistics: CameraFrameIntervalStatistics?
#endif
    private var frameCount = 0
    private var fpsStart = CACurrentMediaTime()
    private var lifecycleGeneration = 0
    private var cameraErrorMessage: String?
    private var connectionErrorMessage: String?
    private var sendErrorMessages: [Int: String] = [:]
    private var hostSelection = DestinationHostSelection()
    private var debugRecordingMaximumDurationTask: Task<Void, Never>?
    private let authorizationStatus: () -> AVAuthorizationStatus
    private let requestAccess: (@escaping (Bool) -> Void) -> Void
    private let idleTimerUpdater: @MainActor (Bool) -> Void
    private var cameraLifecycle = CameraLifecycleStateMachine()
    private var recordingBackgroundTask: UIBackgroundTaskIdentifier = .invalid
    private var cameraWatchdogTask: Task<Void, Never>?
    private var sessionObserverTokens: [NSObjectProtocol] = []
    private var sceneIsActive = true
    private var cameraRecoveryInProgress = false
    private var cameraRecoveryAttempted = false
    private var cameraRecoveryGeneration = 0
    private var cameraLifecycleEnabled = true

    init(
        processor: FrameProcessor = FrameProcessor(),
        sender: UDPSender = UDPSender(),
        authorizationStatus: @escaping () -> AVAuthorizationStatus = { AVCaptureDevice.authorizationStatus(for: .video) },
        requestAccess: @escaping (@escaping (Bool) -> Void) -> Void = { completion in AVCaptureDevice.requestAccess(for: .video, completionHandler: completion) },
        idleTimerUpdater: @escaping @MainActor (Bool) -> Void = { disabled in
            UIApplication.shared.isIdleTimerDisabled = disabled
        }
    ) {
        self.processor = processor
        self.sender = sender
        self.authorizationStatus = authorizationStatus
        self.requestAccess = requestAccess
        self.idleTimerUpdater = idleTimerUpdater
        super.init()
        registerCameraSessionObservers()
        pathMonitor.pathUpdateHandler = { [weak self] path in
            let status: String
            switch path.status {
            case .satisfied: status = path.usesInterfaceType(.wifi) ? "Wi-Fi経路あり" : path.usesInterfaceType(.cellular) ? "セルラー経路あり（5G等とは断定しません）" : "経路あり"
            case .unsatisfied: status = "経路なし"
            case .requiresConnection: status = "接続判定中"
            @unknown default: status = "判定中"
            }
            let interface = path.availableInterfaces.map { String(describing: $0.type) }.joined(separator: ", ")
            Task { @MainActor in self?.pathStatus = status; self?.pathInterface = interface }
        }
        pathMonitor.start(queue: DispatchQueue(label: "PhoneSaberSender.path"))
        bonjourDiscovery.onUpdate = { [weak self] update in
            Task { @MainActor in
                self?.receiveBonjourUpdate(update)
            }
        }
        bonjourDiscovery.start()
        processor.onResult = { [weak self] results, width, height, processingStart, generation, trace in
            Task { @MainActor in self?.handle(results, width: width, height: height, processingStart: processingStart, generation: generation, trace: trace) }
        }
        processor.onRawFrameSaved = { [weak self] result in
            Task { @MainActor in
                switch result {
                case .success(let url):
                    self?.lastRawFrameURL = url
                    self?.rawFrameSaveMessage = "認識前フレームを保存しました"
                case .failure(let error):
                    self?.rawFrameSaveMessage = "フレーム保存失敗: \(error.localizedDescription)"
                }
            }
        }
        processor.onDebugRecordingLimitReached = { [weak self] reason in
            Task { @MainActor in
                guard let self else { return }
                self.debugRecordingActive = false
                self.manualLosslessCapturePending = false
                self.debugRecordingFinalizing = true
                self.debugRecordingMaximumDurationTask?.cancel()
                self.debugRecordingStatus = "\(self.recordingLimitDescription(reason))。録画を自動停止して保存中…"
            }
        }
        processor.onDebugRecordingAutoFinished = { [weak self] result in
            Task { @MainActor in self?.completeDebugRecording(result) }
        }
#if DEBUG
        processor.onPerformance = { [weak self] sample in
            Task { @MainActor in self?.recordPerformance(sample) }
        }
#endif
    }

    var networkStateLabel: String {
        guard running else {
            return host.isEmpty ? "DISCOVERING" : "NETWORK IDLE"
        }
        let portStates = [senderStates[5005], senderStates[5006]].compactMap { $0 }
        if portStates.count == 2 && portStates.allSatisfy({ $0.hasPrefix("ready") }) {
            return "NETWORK READY"
        }
        if portStates.contains(where: { $0 == "waiting" || $0 == "failed" }) {
            return "NETWORK WAITING"
        }
        return "NETWORK CONNECTING"
    }

    private func registerCameraSessionObservers() {
        let center = NotificationCenter.default
        sessionObserverTokens.append(center.addObserver(
            forName: AVCaptureSession.wasInterruptedNotification,
            object: session,
            queue: .main
        ) { [weak self] notification in
            let reason = notification.userInfo?[AVCaptureSessionInterruptionReasonKey]
                .map { String(describing: $0) } ?? "理由不明"
            Task { @MainActor in self?.handleCameraInterruption(reason: reason) }
        })
        sessionObserverTokens.append(center.addObserver(
            forName: AVCaptureSession.interruptionEndedNotification,
            object: session,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.handleCameraInterruptionEnded() }
        })
        sessionObserverTokens.append(center.addObserver(
            forName: AVCaptureSession.runtimeErrorNotification,
            object: session,
            queue: .main
        ) { [weak self] notification in
            let error = notification.userInfo?[AVCaptureSessionErrorKey] as? NSError
            let message = error?.localizedDescription ?? "AVCaptureSession runtime error"
            Task { @MainActor in self?.handleCameraRuntimeError(message) }
        })
    }

    func sceneDidChange(isActive: Bool) {
        if !isActive && debugRecordingActive { stopDebugRecording(reason: .background) }
        sceneIsActive = isActive
        updateIdleTimerPolicy()
        let now = ProcessInfo.processInfo.systemUptime
        let recoveryRequested = cameraLifecycle.setForeground(isActive, at: now)
        let shouldRecover = cameraLifecycleEnabled && recoveryRequested
        publishCameraLifecycle(at: now)
        guard isActive else { return }
        bonjourDiscovery.ensureRunning()
        guard running else { return }
        sender.recoverIfNeeded()
        if shouldRecover {
            cameraRecoveryAttempted = false
            scheduleCameraRecovery(alreadyMarkedRecovering: true)
        }
    }

    func retryCameraRecovery() {
        guard running, sceneIsActive, cameraLifecycleEnabled else { return }
        cameraRecoveryAttempted = false
        scheduleCameraRecovery(alreadyMarkedRecovering: false)
    }

    private func handleCameraInterruption(reason: String) {
        guard running else { return }
        if debugRecordingActive { stopDebugRecording(reason: .interruption) }
        cameraLifecycle.interruptionBegan(reason: reason)
        publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
    }

    private func handleCameraInterruptionEnded() {
        guard running else { return }
        if sceneIsActive {
            cameraRecoveryAttempted = false
            let now = ProcessInfo.processInfo.systemUptime
            if cameraLifecycle.interruptionEnded(at: now) {
                publishCameraLifecycle(at: now)
                scheduleCameraRecovery(alreadyMarkedRecovering: true)
            }
        }
    }

    private func handleCameraRuntimeError(_ message: String) {
        guard running else { return }
        if debugRecordingActive { stopDebugRecording(reason: .runtimeFailure) }
        if cameraRecoveryInProgress {
            cameraErrorMessage = message
            recomputeErrorMessage()
            return
        }
        cameraLifecycle.runtimeError(message)
        cameraErrorMessage = message
        publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
        recomputeErrorMessage()
        if sceneIsActive && !cameraRecoveryAttempted {
            scheduleCameraRecovery(alreadyMarkedRecovering: false)
        }
    }

    private func scheduleCameraRecovery(alreadyMarkedRecovering: Bool) {
        guard running, sceneIsActive, cameraLifecycleEnabled, !cameraRecoveryInProgress else { return }
        if debugRecordingActive { stopDebugRecording(reason: .runtimeFailure) }
        let now = ProcessInfo.processInfo.systemUptime
        if !alreadyMarkedRecovering && !cameraLifecycle.beginRecovery(at: now) { return }
        cameraRecoveryInProgress = true
        cameraRecoveryAttempted = true
        cameraRecoveryGeneration += 1
        let recoveryGeneration = cameraRecoveryGeneration
        let runGeneration = lifecycleGeneration
        _ = processor.reset()
        publishCameraLifecycle(at: now)
        sessionRunner.stop { [weak self] in
            guard let self,
                  self.lifecycleGeneration == runGeneration,
                  self.cameraRecoveryGeneration == recoveryGeneration,
                  self.running else { return }
            self.configureAndStart(isRecovery: true, recoveryGeneration: recoveryGeneration)
        }
    }

    private func finishCameraStart(succeeded: Bool, lifecycle: Int,
                                   recovery: Int?, isRecovery: Bool) {
        guard lifecycleGeneration == lifecycle, running else {
            sessionRunner.stop()
            return
        }
        let now = ProcessInfo.processInfo.systemUptime
        if isRecovery {
            guard recovery == cameraRecoveryGeneration else { return }
            cameraRecoveryInProgress = false
            cameraLifecycle.restartFinished(
                succeeded: succeeded,
                at: now,
                error: "capture sessionを再開できませんでした"
            )
            if succeeded {
                cameraErrorMessage = nil
            } else {
                cameraErrorMessage = "capture sessionを再開できませんでした"
            }
        } else if succeeded {
            cameraLifecycle.sessionStarted(at: now)
            cameraErrorMessage = nil
        } else {
            cameraLifecycle.runtimeError("capture sessionを開始できませんでした")
            cameraErrorMessage = "capture sessionを開始できませんでした"
        }
        publishCameraLifecycle(at: now)
        recomputeErrorMessage()
    }

    private func startCameraWatchdog() {
        cameraWatchdogTask?.cancel()
        cameraWatchdogTask = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(for: .milliseconds(250))
                guard let self, !Task.isCancelled else { return }
                self.pollCameraFrameHealth(at: ProcessInfo.processInfo.systemUptime)
            }
        }
    }

    private func pollCameraFrameHealth(at time: TimeInterval) {
        guard cameraLifecycleEnabled, running else { return }
        let becameStalled = cameraLifecycle.evaluateStall(at: time)
        publishCameraLifecycle(at: time)
        if becameStalled {
            cameraErrorMessage = "カメラframeが2秒以上届いていません"
            recomputeErrorMessage()
            if sceneIsActive && !cameraRecoveryAttempted {
                scheduleCameraRecovery(alreadyMarkedRecovering: false)
            }
        }
    }

    private func receiveCameraFrame(at time: TimeInterval) {
        guard running, cameraLifecycleEnabled else { return }
        cameraLifecycle.receivedFrame(at: time)
        guard cameraLifecycle.state == .live else { return }
        cameraRecoveryAttempted = false
        cameraErrorMessage = nil
        publishCameraLifecycle(at: time)
        recomputeErrorMessage()
    }

    private func publishCameraLifecycle(at time: TimeInterval) {
        cameraState = cameraLifecycle.state
        if let lastFrameAt = cameraLifecycle.lastFrameAt, time >= lastFrameAt {
            lastCameraFrameAge = time - lastFrameAt
        } else {
            lastCameraFrameAge = nil
        }
    }

    private func updateIdleTimerPolicy() {
        let shouldPreventSleep = running && sceneIsActive
        guard screenSleepPreventionActive != shouldPreventSleep else { return }
        screenSleepPreventionActive = shouldPreventSleep
        idleTimerUpdater(shouldPreventSleep)
    }

    deinit {
        cameraWatchdogTask?.cancel()
        sessionObserverTokens.forEach(NotificationCenter.default.removeObserver)
    }

    func start() {
        guard !running else { return }
        lifecycleGeneration += 1
        let requestedGeneration = lifecycleGeneration
        switch authorizationStatus() {
        case .authorized: configureAndStart()
        case .notDetermined:
            requestAccess { [weak self] granted in
                Task { @MainActor in
                    guard let self else { return }
                    self.authorizationCallbackCount += 1
                    guard self.lifecycleGeneration == requestedGeneration, !self.running else { return }
                    granted ? self.configureAndStart() : self.fail("カメラ権限がありません")
                }
            }
        default: fail("設定アプリでカメラ権限を許可してください")
        }
    }

    func retryDiscovery() {
        guard !running else { return }
        bonjourDiscovery.start()
    }

    func setManualHost(_ value: String) {
        hostSelection.setManual(value, resolvedHost: discoveredMacIP, serviceName: discoveredMacName)
        host = hostSelection.host
        connectionMode = hostSelection.source.rawValue
#if DEBUG
        print("[Host] source=\(hostSelection.source.rawValue) resolved=\(discoveredMacIP)")
#endif
    }

    private func receiveBonjourUpdate(_ update: BonjourDiscoveryUpdate) {
        networkDiscoveryStatus = update.status
        discoveredMacName = update.name
        discoveredMacIP = update.ip
        if hostSelection.applyBonjour(host: update.ip, serviceName: update.name) {
            host = hostSelection.host
            connectionMode = hostSelection.source.rawValue
#if DEBUG
            print("[Host] source=\(hostSelection.source.rawValue) resolved=\(update.ip) service=\(update.name)")
#endif
            if running {
                sender.updateHost(update.ip)
                activeDestination = "\(update.ip):5005 / \(update.ip):5006"
            }
        }
    }

    func applyBonjourForTesting(host: String, serviceName: String = "Test Mac") {
        receiveBonjourUpdate(BonjourDiscoveryUpdate(status: "Resolved", name: serviceName, ip: host))
    }

    func recoverFromForeground() {
        sceneDidChange(isActive: true)
    }

    func startMeasurement() {
        measurementMode = true
        start()
    }

#if DEBUG
    func selectDebugCameraFPS(_ fps: Int) {
        guard fps == 30 || fps == 60, fps != debugRequestedFPS else { return }
        guard fps != 60 || debugSupports60FPS else {
            cameraErrorMessage = "This back wide camera has no selected-format candidate for 60 FPS"
            recomputeErrorMessage()
            return
        }
        debugRequestedFPS = fps
        if running {
            // A full stop/reconfigure keeps activeFormat, frame durations, the
            // output connection and the one-slot processor generation atomic.
            stop()
            start()
        }
    }
#endif

    func stop() {
        lifecycleGeneration += 1
        cameraRecoveryGeneration += 1
        cameraRecoveryInProgress = false
        cameraRecoveryAttempted = false
        if debugRecordingActive { stopDebugRecording() }
        running = false
        cameraWatchdogTask?.cancel()
        cameraWatchdogTask = nil
        cameraLifecycle.stop()
        cameraLifecycleEnabled = true
        publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
        updateIdleTimerPolicy()
        sessionRunner.stopSynchronously()
        sender.stop(); _ = processor.reset(); activeDestination = "未設定"; status = "停止中"; redEndpoints = nil; blueEndpoints = nil; senderStates = [:]; senderErrors = [:]; cameraErrorMessage = nil; connectionErrorMessage = nil; sendErrorMessages = [:]; recomputeErrorMessage()
    }

    private func configureAndStart(isRecovery: Bool = false, recoveryGeneration: Int? = nil) {
        let configuredHost = host.trimmingCharacters(in: .whitespacesAndNewlines)
        cameraErrorMessage = nil
        connectionErrorMessage = nil
        sendErrorMessages = [:]
#if DEBUG
        resetDebugPerformance()
#endif
        recomputeErrorMessage()
        guard !configuredHost.isEmpty else {
            if isRecovery {
                cameraRecoveryInProgress = false
                cameraLifecycle.restartFinished(succeeded: false, at: ProcessInfo.processInfo.systemUptime,
                                                error: "送信先Macが設定されていません")
                publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
            } else {
                cameraLifecycle.stop()
                publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
            }
            fail("Macを検索中です。見つからない場合は手動IPを入力してください", cameraFailure: false)
            return
        }
        let configuredThreshold = ColorThreshold(brightness: UInt8(clamping: threshold), dominance: UInt8(clamping: dominance))
        session.beginConfiguration()
        session.inputs.forEach { session.removeInput($0) }
        session.outputs.forEach { session.removeOutput($0) }
        guard let camera = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .back), let input = try? AVCaptureDeviceInput(device: camera), session.canAddInput(input), session.canAddOutput(output) else {
            session.commitConfiguration()
            cameraRecoveryInProgress = false
            fail("カメラを初期化できません")
            return
        }
        session.addInput(input)
        let formatOptions = camera.formats.enumerated().map { index, format in
            let size = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            return CameraFormatOption(
                index: index,
                width: size.width,
                height: size.height,
                frameRateRanges: format.videoSupportedFrameRateRanges.map { $0.minFrameRate...$0.maxFrameRate }
            )
        }
#if DEBUG
        let sixtyFPSOptions = formatOptions.filter { supportsFrameRate(60, ranges: $0.frameRateRanges) }
        debugSupports60FPS = !sixtyFPSOptions.isEmpty
        debug60FPSFormats = Self.formatSummary(sixtyFPSOptions)
        if debugRequestedFPS == 60 && !debugSupports60FPS { debugRequestedFPS = 30 }
        let targetFPS = Double(debugRequestedFPS)
#else
        let targetFPS = 30.0
#endif
        let selectedIndex = preferredCameraFormatIndex(options: formatOptions, targetFPS: targetFPS)
        let format = selectedIndex.map { camera.formats[$0] } ?? camera.formats.min { left, right in
            let lhs = CMVideoFormatDescriptionGetDimensions(left.formatDescription)
            let rhs = CMVideoFormatDescriptionGetDimensions(right.formatDescription)
            return Int64(lhs.width) * Int64(lhs.height) < Int64(rhs.width) * Int64(rhs.height)
        }
        if let format {
            do {
                try camera.lockForConfiguration()
                defer { camera.unlockForConfiguration() }
                camera.activeFormat = format
                if #available(iOS 18.0, *), format.isAutoVideoFrameRateSupported {
                    camera.isAutoVideoFrameRateEnabled = false
                }
                if camera.isLowLightBoostSupported {
                    camera.automaticallyEnablesLowLightBoostWhenAvailable = false
                }
                let supportedRanges = format.videoSupportedFrameRateRanges.map { $0.minFrameRate...$0.maxFrameRate }
                if let duration = fixedFrameDuration(fps: targetFPS, ranges: supportedRanges) {
                    camera.activeVideoMinFrameDuration = duration
                    camera.activeVideoMaxFrameDuration = duration
                }
            } catch {
                errorMessage = "カメラ形式を設定できません: \(error.localizedDescription)"
            }
        }
        output.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA]
        output.alwaysDiscardsLateVideoFrames = true
        // Keep the AVFoundation callback short. FrameProcessor owns a one-slot
        // latest-frame mailbox and drops any superseded waiting frame.
        output.setSampleBufferDelegate(self, queue: captureQueue)
        session.addOutput(output)
        if let connection = output.connection(with: .video) {
            connection.videoOrientation = .portrait
            connection.isVideoMirrored = false
            // Apple's default is off and stabilization adds latency for the
            // non-low-latency modes. Make the intended capture path explicit.
            if connection.isVideoStabilizationSupported {
                connection.preferredVideoStabilizationMode = .off
            }
        }
        session.commitConfiguration()
#if DEBUG
        activeCamera = camera
        updateCameraConfiguration(camera)
#endif
        processor.configureCaptureSynchronizationClock(session.synchronizationClock)
        let currentGeneration = lifecycleGeneration
        if !isRecovery {
            cameraLifecycle.requestStart(at: ProcessInfo.processInfo.systemUptime)
        }
        cameraLifecycleEnabled = true
        running = true
        updateIdleTimerPolicy()
        sender.configure(host: configuredHost) { [weak self] states, errors, lastError in
            Task { @MainActor in
                self?.applySenderUpdate(states: states, errors: errors, generation: currentGeneration)
            }
        }
        host = configuredHost
        connectionMode = hostSelection.source.rawValue
        activeDestination = "\(configuredHost):5005 / \(configuredHost):5006"
        resetStartState()
        processor.configureThresholds(red: configuredThreshold, blue: configuredThreshold)
        frameCount = 0; fpsStart = CACurrentMediaTime(); _ = processor.reset(); status = "送信中"
        startCameraWatchdog()
        publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
        sessionRunner.start { [weak self] succeeded in
            self?.finishCameraStart(succeeded: succeeded, lifecycle: currentGeneration,
                                    recovery: recoveryGeneration, isRecovery: isRecovery)
        }
    }

    private func handle(_ results: [DetectedSaber], width: Int, height: Int, processingStart: TimeInterval, generation: Int, trace: FrameTrace?) {
#if DEBUG
        let uiStart = ProcessInfo.processInfo.systemUptime
        var udpRequestMs = 0.0
#endif
        guard running, generation == processor.currentGeneration else {
            rejectedFrameCallbackCount += 1
            return
        }
        processedFrameCount += 1
#if DEBUG
        if let trace { recordFrameTrace(trace) }
#endif
        sourceDimensions = (width, height)
        var redSeen = false
        var blueSeen = false
        for result in results {
            guard result.isFresh else {
                switch result.color {
                case .red: redEndpoints = result.endpoints; redSeen = true
                case .blue: blueEndpoints = result.endpoints; blueSeen = true
                }
                continue
            }
            let coordinates = payload(for: result.endpoints, source: (width, height), output: (outputWidth, outputHeight), mirrorX: mirrorX, mirrorY: mirrorY)
            let text: String
            if measurementMode {
                let sentEpoch = Date().timeIntervalSince1970
                lastSentEpoch = sentEpoch
                text = timestampedPayload(coordinates, timestamp: sentEpoch)
            } else {
                lastSentEpoch = nil
                text = coordinates
            }
            let port: Int
            switch result.color {
            case .red:
                redEndpoints = result.endpoints
                if !result.isPredicted { redDetectionCount += 1 }
                port = 5005
                redSeen = true
            case .blue:
                blueEndpoints = result.endpoints
                if !result.isPredicted { blueDetectionCount += 1 }
                port = 5006
                blueSeen = true
            }
            if result.color == .red { redAttemptCount += 1 } else { blueAttemptCount += 1 }
            let sendGeneration = lifecycleGeneration
#if DEBUG
            let enqueueAt = HostMonotonicClock.now()
            recordEventRate("UDP enqueue rate")
            if let trace {
                addPerformance("Detection → UDP enqueue", value: max(0, (enqueueAt - trace.detectionEnd) * 1000))
                addPerformance("Post-capture total", value: max(0, (enqueueAt - trace.callbackHostTime) * 1000))
            }
#endif
#if DEBUG
            let sendRequestStart = ProcessInfo.processInfo.systemUptime
#endif
            let completion: (Result<TimeInterval, Error>) -> Void = { [weak self] result in
                Task { @MainActor in
                    guard let self, self.running, self.lifecycleGeneration == sendGeneration else { return }
                    switch result {
                    case .success(let completedAt):
                        self.sendErrorMessages[port] = nil
                        if port == 5005 { self.redCompletedCount += 1 } else { self.blueCompletedCount += 1 }
                        self.lastLocalSendMs = max(0, (completedAt - processingStart) * 1000)
                    case .failure(let error):
                        if port == 5005 { self.redErrorCount += 1 } else { self.blueErrorCount += 1 }
                        self.sendErrorMessages[port] = "UDP送信失敗 (\(self.host):\(port)): \(error.localizedDescription)"
                        self.status = "送信エラー"
                    }
                    self.recomputeErrorMessage()
                }
            }
#if DEBUG
            sender.send(text, to: port, onSendStarted: { [weak self] queueWait, replaced in
                result.diagnosticSendStarted?(coordinates)
                Task { @MainActor in self?.recordUDPQueueStart(queueWait, replaced: replaced) }
            }, completion: completion)
#else
            sender.send(text, to: port, onSendStarted: result.diagnosticSendStarted.map { callback in
                { _, _ in callback(coordinates) }
            }, completion: completion)
#endif
#if DEBUG
            udpRequestMs += (ProcessInfo.processInfo.systemUptime - sendRequestStart) * 1000
#endif
        }
        if !redSeen { redEndpoints = nil }
        if !blueSeen { blueEndpoints = nil }
        frameCount += 1
        let elapsed = CACurrentMediaTime() - fpsStart
        if elapsed >= 1 { fps = Double(frameCount) / elapsed; frameCount = 0; fpsStart = CACurrentMediaTime() }
#if DEBUG
        addPerformance("UDP request", value: udpRequestMs)
        addPerformance("UI / overlay state", value: (ProcessInfo.processInfo.systemUptime - uiStart) * 1000)
        publishPerformanceIfNeeded()
#endif
    }

#if DEBUG
    private func resetDebugPerformance() {
        performanceMetrics = [:]
        previousTrace = nil
        diagnosticEventTimes = [:]
        latestDiagnosticSequence = 0
        latestFrameCounts = (0, 0, 0)
        debugPerformanceRows = DebugPerformanceRow.placeholders
        debugFrameIntervalStatistics = nil
        latestFrameIntervalStatistics = nil
        lastPerformancePublish = 0
        lastCameraSampleTime = 0
        debugPerformancePublishCountForTesting = 0
    }

    private func recordPerformance(_ sample: FramePerformanceSample) {
        let profile = sample.detector
        addPerformance("Pixel buffer access", value: sample.pixelBufferAccessMs)
        addPerformance("BGRA + RGB→HSV + masks", value: profile.pixelScanHSVMaskMs)
        addPerformance("Close / open", value: profile.morphologyMs)
        addPerformance("Components + shape/brightness/contrast/PCA", value: profile.componentAndScoreMs)
        addPerformance("Component traversal / proposal overhead", value: profile.componentTraversalAndProposalOverheadMs)
        addPerformance("Shape + PCA axis", value: profile.shapeAndAxisMs)
        addPerformance("Brightness / contrast / color score", value: profile.brightnessContrastColorMs)
        addPerformance("Endpoints + bounds", value: profile.endpointAndBoundsMs)
        addPerformance("Bright-core proposals", value: profile.lineProposalMs)
        addPerformance("Proposal detailed score", value: profile.lineScoreMs)
        addPerformance("Final selection / scaling", value: profile.selectionMs)
        addPerformance("Detection total", value: profile.totalMs)
        publishPerformanceIfNeeded()
    }

    private func recordFrameTrace(_ trace: FrameTrace) {
        latestDiagnosticSequence = trace.sequence
        latestFrameCounts = (trace.receivedFrames, trace.processedFrames, trace.replacedFrames)
        if let capture = trace.captureHostTime, trace.callbackHostTime >= capture {
            addPerformance("Camera / AVFoundation age", value: (trace.callbackHostTime - capture) * 1000)
        }
        latestFrameIntervalStatistics = trace.inputFrameIntervalStatistics
        addPerformance("Callback → processing start", value: max(0, (trace.processingStart - trace.callbackHostTime) * 1000))
        addPerformance("Detection", value: max(0, (trace.detectionEnd - trace.processingStart) * 1000))
        if let previous = previousTrace {
            let elapsed = trace.callbackHostTime - previous.callbackHostTime
            if elapsed > 0 {
                addPerformance("Input FPS", value: Double(trace.receivedFrames - previous.receivedFrames) / elapsed)
                addPerformance("Processed FPS", value: Double(trace.processedFrames - previous.processedFrames) / elapsed)
                addPerformance("Replaced frames", value: Double(trace.replacedFrames - previous.replacedFrames) / elapsed)
            }
        }
        previousTrace = trace
        publishPerformanceIfNeeded()
    }

    private func recordUDPQueueStart(_ wait: TimeInterval, replaced: Int) {
        addPerformance("UDP queue wait", value: wait * 1000)
        addPerformance("UDP replaced sends", value: Double(replaced))
        recordEventRate("UDP actual send rate")
        publishPerformanceIfNeeded()
    }

    private func recordEventRate(_ name: String) {
        let now = HostMonotonicClock.now()
        var events = diagnosticEventTimes[name, default: []]
        events.append(now)
        events = events.filter { now - $0 <= 2 }
        diagnosticEventTimes[name] = events
        guard let first = events.first, now > first else { return }
        addPerformance(name, value: Double(events.count - 1) / (now - first))
    }

    private func addPerformance(_ name: String, value: Double) {
        var metric = performanceMetrics[name, default: PerformanceMetric()]
        metric.latest = value
        metric.values.append(value)
        if metric.values.count > 120 { metric.values.removeFirst() }
        metric.maximum = max(metric.maximum, value)
        performanceMetrics[name] = metric
    }

    func recordDebugPerformanceForTesting(name: String, value: Double) {
        addPerformance(name, value: value)
        publishPerformanceIfNeeded()
    }

    private func publishPerformanceIfNeeded() {
        let now = ProcessInfo.processInfo.systemUptime
        guard now - lastPerformancePublish >= 0.2 else { return }
        lastPerformancePublish = now
        debugPerformancePublishCountForTesting += 1
        if let activeCamera { updateCameraConfiguration(activeCamera) }
        if debugFrameIntervalStatistics != latestFrameIntervalStatistics {
            debugFrameIntervalStatistics = latestFrameIntervalStatistics
        }
        debugPerformanceRows = DebugPerformanceRow.rows(from: performanceMetrics)
    }

    private func updateCameraConfiguration(_ camera: AVCaptureDevice) {
        let format = camera.activeFormat
        let size = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
        let ranges = format.videoSupportedFrameRateRanges
            .map { String(format: "%.1f…%.1f", $0.minFrameRate, $0.maxFrameRate) }
            .joined(separator: ", ")
        let autoFrameRate: String
        if #available(iOS 18.0, *) {
            autoFrameRate = format.isAutoVideoFrameRateSupported
                ? (camera.isAutoVideoFrameRateEnabled ? "Enabled" : "Disabled")
                : "Unsupported"
        } else {
            autoFrameRate = "Unavailable before iOS 18"
        }
        let updated = DebugCameraConfiguration(
            device: camera.localizedName,
            position: camera.position == .back ? "Back" : camera.position == .front ? "Front" : "Unspecified",
            format: "\(size.width)×\(size.height)",
            fpsRanges: ranges.isEmpty ? "Not available" : ranges,
            minimumDuration: Self.durationDescription(camera.activeVideoMinFrameDuration),
            maximumDuration: Self.durationDescription(camera.activeVideoMaxFrameDuration),
            sessionPreset: session.sessionPreset.rawValue,
            pixelFormat: "32BGRA",
            discardsLateFrames: output.alwaysDiscardsLateVideoFrames,
            autoFrameRate: autoFrameRate,
            exposure: String(format: "%@ / %.2f ms / ISO %.0f", Self.exposureModeDescription(camera.exposureMode), CMTimeGetSeconds(camera.exposureDuration) * 1000, camera.iso),
            exposureBudget: Self.exposureBudgetDescription(
                exposure: camera.exposureDuration,
                frameDuration: camera.activeVideoMaxFrameDuration
            ),
            hdr: "format \(format.isVideoHDRSupported ? "supported" : "unsupported"), active \(camera.isVideoHDREnabled ? "On" : "Off"), auto \(camera.automaticallyAdjustsVideoHDREnabled ? "On" : "Off")",
            lowLightBoost: camera.isLowLightBoostSupported
                ? "active \(camera.isLowLightBoostEnabled ? "Yes" : "No"), auto \(camera.automaticallyEnablesLowLightBoostWhenAvailable ? "On" : "Off")"
                : "Unsupported",
            systemPressure: Self.systemPressureDescription(camera.systemPressureState.level),
            thermalState: Self.thermalStateDescription(ProcessInfo.processInfo.thermalState),
            stabilization: Self.stabilizationDescription(output.connection(with: .video)?.activeVideoStabilizationMode)
        )
        if debugCameraConfiguration != updated { debugCameraConfiguration = updated }
#if os(iOS)
        if debugRecordingActive {
            // Same throttled main-actor read as above; the processor only keeps
            // the newest value and attaches it, with its age, to each frame.
            let deviceGains = camera.deviceWhiteBalanceGains
            processor.updateDebugCameraDeviceState(DebugCameraDeviceState(
                iso: Double(camera.iso),
                exposureDurationSeconds: CMTimeGetSeconds(camera.exposureDuration),
                exposureTargetBias: Double(camera.exposureTargetBias),
                exposureTargetOffset: Double(camera.exposureTargetOffset),
                whiteBalanceGains: [Double(deviceGains.redGain), Double(deviceGains.greenGain),
                                    Double(deviceGains.blueGain)],
                sampledAt: HostMonotonicClock.now()
            ))
        }
#endif
        let now = ProcessInfo.processInfo.systemUptime
        if debugRecordingActive && now - lastCameraSampleTime >= 1.0 {
            lastCameraSampleTime = now
            let gains = camera.deviceWhiteBalanceGains
            let minDuration = CMTimeGetSeconds(camera.activeVideoMinFrameDuration)
            let maxDuration = CMTimeGetSeconds(camera.activeVideoMaxFrameDuration)
            processor.recordDebugCameraSample(DebugRecordingCameraSample(
                frameID: nil, presentationTimeSeconds: nil,
                exposureDurationMs: CMTimeGetSeconds(camera.exposureDuration) * 1000,
                iso: camera.iso,
                whiteBalanceRedGain: gains.redGain,
                whiteBalanceGreenGain: gains.greenGain,
                whiteBalanceBlueGain: gains.blueGain,
                exposureMode: Self.exposureModeDescription(camera.exposureMode),
                whiteBalanceMode: Self.whiteBalanceModeDescription(camera.whiteBalanceMode),
                focusMode: Self.focusModeDescription(camera.focusMode),
                lensPosition: camera.lensPosition,
                activeFormat: "\(size.width)×\(size.height)",
                activeFormatFPSRanges: ranges.isEmpty ? "Not available" : ranges,
                activeMinFPS: maxDuration > 0 && maxDuration.isFinite ? 1 / maxDuration : nil,
                activeMaxFPS: minDuration > 0 && minDuration.isFinite ? 1 / minDuration : nil
            ))
        }
    }

    private static func durationDescription(_ duration: CMTime) -> String {
        guard duration.isValid, duration.isNumeric else { return "Not available" }
        let seconds = CMTimeGetSeconds(duration)
        guard seconds.isFinite, seconds > 0 else { return "Not available" }
        return String(format: "%.2f ms (%.2f fps)", seconds * 1000, 1 / seconds)
    }

    private static func exposureModeDescription(_ mode: AVCaptureDevice.ExposureMode) -> String {
        switch mode {
        case .locked: return "Locked"
        case .autoExpose: return "Auto"
        case .continuousAutoExposure: return "Continuous auto"
        case .custom: return "Custom"
        @unknown default: return "Unknown"
        }
    }

    private static func whiteBalanceModeDescription(_ mode: AVCaptureDevice.WhiteBalanceMode) -> String {
        switch mode {
        case .locked: return "Locked"
        case .autoWhiteBalance: return "Auto"
        case .continuousAutoWhiteBalance: return "Continuous auto"
        @unknown default: return "Unknown"
        }
    }

    private static func focusModeDescription(_ mode: AVCaptureDevice.FocusMode) -> String {
        switch mode {
        case .locked: return "Locked"
        case .autoFocus: return "Auto"
        case .continuousAutoFocus: return "Continuous auto"
        @unknown default: return "Unknown"
        }
    }

    private static func exposureBudgetDescription(exposure: CMTime, frameDuration: CMTime) -> String {
        let exposureMs = CMTimeGetSeconds(exposure) * 1000
        let budgetMs = CMTimeGetSeconds(frameDuration) * 1000
        guard exposureMs.isFinite, budgetMs.isFinite, exposureMs >= 0, budgetMs > 0 else {
            return "Not available"
        }
        return String(format: "%@ (%.2f / %.2f ms)",
                      exposureMs <= budgetMs + 0.05 ? "Within frame" : "OVER FRAME",
                      exposureMs, budgetMs)
    }

    private static func systemPressureDescription(_ level: AVCaptureDevice.SystemPressureState.Level) -> String {
        switch level {
        case .nominal: return "Nominal"
        case .fair: return "Fair"
        case .serious: return "Serious"
        case .critical: return "Critical"
        case .shutdown: return "Shutdown"
        default: return "Unknown"
        }
    }

    private static func thermalStateDescription(_ state: ProcessInfo.ThermalState) -> String {
        switch state {
        case .nominal: return "Nominal"
        case .fair: return "Fair"
        case .serious: return "Serious"
        case .critical: return "Critical"
        @unknown default: return "Unknown"
        }
    }

    private static func stabilizationDescription(_ mode: AVCaptureVideoStabilizationMode?) -> String {
        guard let mode else { return "Not available" }
        switch mode {
        case .off: return "Off"
        case .standard: return "Standard"
        case .cinematic: return "Cinematic"
        case .cinematicExtended: return "Cinematic extended"
        case .auto: return "Auto"
        case .lowLatency: return "Low latency"
        case .previewOptimized: return "Preview optimized"
        case .cinematicExtendedEnhanced: return "Cinematic extended enhanced"
        @unknown default: return "Unknown"
        }
    }

    private static func formatSummary(_ options: [CameraFormatOption]) -> String {
        let unique = Set(options.map { "\($0.width)×\($0.height)" })
        return unique.isEmpty ? "Not supported" : unique.sorted().joined(separator: ", ")
    }
#endif

#if !DEBUG
    private func recordUDPQueueStart(_ wait: TimeInterval, replaced: Int) { }
#endif

    private func applySenderUpdate(states: [Int: String], errors: [Int: String], generation: Int) {
        guard running, lifecycleGeneration == generation else {
            rejectedSenderUpdateCount += 1
            return
        }
        acceptedSenderUpdateCount += 1
        acceptedSenderUpdateGeneration = generation
        senderStates = states
        senderErrors = errors
        let hasConnectionIssue = states.values.contains { $0 == "waiting" || $0 == "failed" }
        connectionErrorMessage = hasConnectionIssue ? states.keys.sorted().compactMap { errors[$0] }.first(where: { !$0.isEmpty }) : nil
        recomputeErrorMessage()
    }

    private func recomputeErrorMessage() {
        errorMessage = cameraErrorMessage ?? connectionErrorMessage ?? sendErrorMessages.values.first
        if cameraErrorMessage != nil {
            status = "カメラエラー"
        } else if connectionErrorMessage != nil {
            status = "接続エラー"
        } else if !sendErrorMessages.isEmpty {
            status = "送信エラー"
        } else if running {
            status = "送信中"
        } else {
            status = "停止中"
        }
    }

    private func fail(_ message: String, cameraFailure: Bool = true) {
        cameraErrorMessage = message
        if cameraFailure {
            cameraLifecycle.fail(message)
            publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
        }
        recomputeErrorMessage()
    }

    private func resetStartState() {
        redDetectionCount = 0; blueDetectionCount = 0
        redAttemptCount = 0; blueAttemptCount = 0
        redCompletedCount = 0; blueCompletedCount = 0
        redErrorCount = 0; blueErrorCount = 0
        lastLocalSendMs = nil; lastSentEpoch = nil
        processedFrameCount = 0
        authorizationCallbackCount = 0
        acceptedSenderUpdateCount = 0
        acceptedSenderUpdateGeneration = 0
        rejectedFrameCallbackCount = 0
        rejectedSenderUpdateCount = 0
        redEndpoints = nil; blueEndpoints = nil
        senderStates = [:]; senderErrors = [:]
        sendErrorMessages = [:]
#if DEBUG
        resetDebugPerformance()
#endif
    }

    // Test entry point: this uses the same FrameProcessor callback and UDP completion path as camera frames.
    func startForTesting(host: String = "127.0.0.1", manual: Bool = true) {
        lifecycleGeneration += 1
        cameraLifecycleEnabled = false
        cameraLifecycle.requestStart(at: ProcessInfo.processInfo.systemUptime)
        cameraState = .starting
        lastCameraFrameAge = nil
        resetStartState()
        running = true
        updateIdleTimerPolicy()
        if manual { hostSelection.setManual(host, resolvedHost: "", serviceName: "") }
        self.host = host
        let currentGeneration = lifecycleGeneration
        sender.configure(host: host) { [weak self] states, errors, _ in
            Task { @MainActor in
                self?.applySenderUpdate(states: states, errors: errors, generation: currentGeneration)
            }
        }
        _ = processor.reset()
        recomputeErrorMessage()
    }

    func processDetectedForTesting(_ detected: [(SaberColor, (PixelPoint, PixelPoint)?)], at time: TimeInterval, dimensions: (width: Int, height: Int)) {
        processor.processDetectedForTesting(detected, at: time, dimensions: dimensions)
    }

    func saveNextRawFrame() {
        lastRawFrameURL = nil
        rawFrameSaveMessage = "次の認識前フレームを1枚だけ保存します"
        processor.requestRawFrameSave()
    }

    func startDebugRecording() {
        guard DebugRecordingLifecyclePolicy.mayStart(
            enabled: debugRecordingEnabled, cameraRunning: running,
            active: debugRecordingActive, finalizing: debugRecordingFinalizing
        ) else { return }
        lastDebugRecordingResult = nil
        debugManualLosslessCaptureCount = 0
        // Reserve the state immediately so a rapid app Stop queues recorder
        // finalization after recorder creation instead of leaving it orphaned.
        debugRecordingActive = true
        debugRecordingStatus = "録画を開始しています…"
        processor.updateDebugCameraDeviceState(nil)
        debugSegmentLabel = .unlabeled
        processor.startDebugRecording(diagnosticColors: debugDiagnosticColors) { [weak self] result in
            Task { @MainActor in
                guard let self else { return }
                switch result {
                case .success(let sessionID):
                    guard self.debugRecordingActive, !self.debugRecordingFinalizing else { return }
                    self.debugRecordingStatus = "録画中: \(sessionID)"
                    self.refreshDebugRecordingSessions()
                    self.scheduleDebugRecordingMaximumDurationStop()
                case .failure(let error):
                    guard self.debugRecordingActive, !self.debugRecordingFinalizing else { return }
                    self.debugRecordingActive = false
                    self.debugRecordingStatus = "録画開始失敗: \(error.localizedDescription)"
                }
            }
        }
    }

    func stopDebugRecording(reason: DebugRecordingFinishReason = .user) {
        guard DebugRecordingLifecyclePolicy.mayStop(active: debugRecordingActive, finalizing: debugRecordingFinalizing) else { return }
        debugRecordingActive = false
        manualLosslessCapturePending = false
        debugRecordingFinalizing = true
        processor.updateDebugCameraDeviceState(nil)
        if reason == .background && recordingBackgroundTask == .invalid {
            recordingBackgroundTask = UIApplication.shared.beginBackgroundTask(
                withName: "PhoneSaber recording finalize"
            ) { [weak self] in
                Task { @MainActor in self?.endRecordingBackgroundTask() }
            }
        }
        debugRecordingMaximumDurationTask?.cancel()
        debugRecordingStatus = reason == .user
            ? "raw動画を確定し、overlay動画を生成中…"
            : "\(recordingLimitDescription(reason))。録画を自動停止して保存中…"
        processor.stopDebugRecording(reason: reason) { [weak self] result in
            Task { @MainActor in self?.completeDebugRecording(result) }
        }
    }

    func refreshDebugRecordingSessions() {
        guard let directory = debugRecordingDirectory() else {
            debugRecordingSessions = []
            return
        }
        debugRecordingSessions = DebugRecordingStorage.sessionSummaries(in: directory)
    }

    func cleanupOlderDebugRecordingSessions() {
        guard let directory = debugRecordingDirectory() else { return }
        do {
            let removed = try DebugRecordingStorage.deleteOlderSessions(in: directory)
            debugRecordingCleanupStatus = removed.count == 0
                ? "整理対象の古いsessionはありません"
                : "古いsession \(removed.count) 件を整理しました（\(formattedBytes(removed.bytes))）"
            refreshDebugRecordingSessions()
        } catch {
            debugRecordingCleanupStatus = "整理失敗: \(error.localizedDescription)"
        }
    }

    private func debugRecordingDirectory() -> URL? {
        FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first
    }

    private func endRecordingBackgroundTask() {
        guard recordingBackgroundTask != .invalid else { return }
        UIApplication.shared.endBackgroundTask(recordingBackgroundTask)
        recordingBackgroundTask = .invalid
    }

    private func completeDebugRecording(_ result: Result<DebugRecordingResult, Error>) {
        endRecordingBackgroundTask()
        debugRecordingMaximumDurationTask?.cancel()
        debugRecordingActive = false
        debugRecordingFinalizing = false
        manualLosslessCapturePending = false
        switch result {
        case .success(let recording):
            lastDebugRecordingResult = recording
            debugRecordingStatus = String(
                format: "完了: %d frames / %@ / %.0f秒",
                recording.recordedFrameCount,
                formattedBytes(recording.diskUsageBytes),
                recording.durationSeconds
            )
        case .failure(let error):
            debugRecordingStatus = "録画処理失敗: \(error.localizedDescription)"
        }
        refreshDebugRecordingSessions()
    }

    private func recordingLimitDescription(_ reason: DebugRecordingFinishReason) -> String {
        switch reason {
        case .user: return "手動停止"
        case .maximumDuration: return "5分の録画時間上限"
        case .maximumDiskUsage: return "録画容量上限"
        case .maximumMetadataSize: return "metadata容量上限"
        case .diskLow: return "空き容量不足"
        case .background: return "アプリのバックグラウンド移行"
        case .interruption: return "カメラの中断"
        case .runtimeFailure: return "カメラ障害"
        case .rawWriterFailure: return "raw動画書込み失敗"
        case .metadataWriterFailure: return "metadata書込み失敗"
        }
    }

    private func formattedBytes(_ bytes: Int64) -> String {
        ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)
    }

    private func scheduleDebugRecordingMaximumDurationStop() {
        debugRecordingMaximumDurationTask?.cancel()
        let duration = UInt64(DebugRecordingLimits.maximumDurationSeconds * 1_000_000_000)
        debugRecordingMaximumDurationTask = Task { [weak self] in
            do {
                try await Task.sleep(nanoseconds: duration)
            } catch {
                return
            }
            guard let self, self.debugRecordingActive, !self.debugRecordingFinalizing else { return }
            self.stopDebugRecording(reason: .maximumDuration)
        }
    }

    func captureNextLosslessFrame() {
        guard debugRecordingEnabled, debugRecordingActive,
              !debugRecordingFinalizing, !manualLosslessCapturePending else { return }
        manualLosslessCapturePending = true
        debugRecordingStatus = "次の録画フレームをlossless capture待機中…"
        processor.requestManualLosslessFrame { [weak self] result in
            Task { @MainActor in
                guard let self else { return }
                self.manualLosslessCapturePending = false
                switch result {
                case .success(let frameID):
                    self.debugManualLosslessCaptureCount += 1
                    self.debugRecordingStatus = "録画中: manual_frame_\(frameID).png をStop後に保存"
                case .failure(let error):
                    self.debugRecordingStatus = "Lossless capture失敗: \(error.localizedDescription)"
                }
            }
        }
    }
}

extension CameraViewModel: AVCaptureVideoDataOutputSampleBufferDelegate {
    nonisolated func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        let frameTime = ProcessInfo.processInfo.systemUptime
        processor.submit(sampleBuffer)
        Task { @MainActor [weak self] in self?.receiveCameraFrame(at: frameTime) }
    }
}

#if DEBUG
struct DebugCameraConfiguration: Equatable {
    let device: String
    let position: String
    let format: String
    let fpsRanges: String
    let minimumDuration: String
    let maximumDuration: String
    let sessionPreset: String
    let pixelFormat: String
    let discardsLateFrames: Bool
    let autoFrameRate: String
    let exposure: String
    let exposureBudget: String
    let hdr: String
    let lowLightBoost: String
    let systemPressure: String
    let thermalState: String
    let stabilization: String

    static let unavailable = DebugCameraConfiguration(
        device: "Not available", position: "Not available", format: "Not available",
        fpsRanges: "Not available", minimumDuration: "Not available",
        maximumDuration: "Not available", sessionPreset: "Not available",
        pixelFormat: "32BGRA", discardsLateFrames: true,
        autoFrameRate: "Not available", exposure: "Not available",
        exposureBudget: "Not available", hdr: "Not available",
        lowLightBoost: "Not available", systemPressure: "Not available",
        thermalState: "Not available", stabilization: "Not available"
    )
}

fileprivate struct PerformanceMetric {
    var latest = 0.0
    var maximum = 0.0
    var values: [Double] = []
    var average: Double { values.isEmpty ? 0 : values.reduce(0, +) / Double(values.count) }
    var median: Double {
        guard !values.isEmpty else { return 0 }
        let sorted = values.sorted()
        return sorted[sorted.count / 2]
    }
}

struct DebugPerformanceRow: Identifiable {
    let category: String
    let label: String
    let source: String
    let unit: String
    let latest: Double?
    let median: Double?
    let maximum: Double?

    var id: String { source }

    static let definitions: [(category: String, label: String, source: String, unit: String)] = [
        ("Camera", "Frame age", "Camera / AVFoundation age", "ms"),
        ("Camera", "Frame queue", "Callback → processing start", "ms"),
        ("Camera", "Input FPS", "Input FPS", "fps"),
        ("Camera", "Processed FPS", "Processed FPS", "fps"),
        ("Camera", "Replaced FPS", "Replaced frames", "fps"),
        ("Processing", "Detection", "Detection", "ms"),
        ("Processing", "Pixel + HSV + masks", "BGRA + RGB→HSV + masks", "ms"),
        ("Processing", "Morphology", "Close / open", "ms"),
        ("Processing", "Components + score + axis", "Components + shape/brightness/contrast/PCA", "ms"),
        ("Processing", "Component traversal", "Component traversal / proposal overhead", "ms"),
        ("Processing", "Shape + PCA axis", "Shape + PCA axis", "ms"),
        ("Processing", "Brightness / contrast / color", "Brightness / contrast / color score", "ms"),
        ("Processing", "Endpoints + bounds", "Endpoints + bounds", "ms"),
        ("Processing", "Bright-core proposals", "Bright-core proposals", "ms"),
        ("Processing", "Proposal scoring", "Proposal detailed score", "ms"),
        ("Processing", "Winner selection", "Final selection / scaling", "ms"),
        ("Processing", "Profiled detector total", "Detection total", "ms"),
        ("Processing", "Detection → UDP", "Detection → UDP enqueue", "ms"),
        ("Processing", "Post-capture total", "Post-capture total", "ms"),
        ("Network", "UDP queue", "UDP queue wait", "ms"),
        ("Network", "Send rate", "UDP actual send rate", "/s"),
        ("Network", "Replaced sends", "UDP replaced sends", "count")
    ]

    static var placeholders: [DebugPerformanceRow] {
        definitions.map { definition in
            DebugPerformanceRow(category: definition.category, label: definition.label,
                                source: definition.source, unit: definition.unit,
                                latest: nil, median: nil, maximum: nil)
        }
    }

    fileprivate static func rows(from metrics: [String: PerformanceMetric]) -> [DebugPerformanceRow] {
        definitions.map { definition in
            let metric = metrics[definition.source]
            return DebugPerformanceRow(category: definition.category, label: definition.label,
                                       source: definition.source, unit: definition.unit,
                                       latest: metric?.latest, median: metric?.median,
                                       maximum: metric?.maximum)
        }
    }
}
#endif

private struct BonjourDiscoveryUpdate {
    let status: String
    let name: String
    let ip: String
}

/// Discovery runs on the main run loop, outside the capture and UDP queues.
/// The service advertises red UDP 5005; blue remains the existing 5006 on the
/// resolved host, preserving the payload and transport contract.
private final class BonjourDiscovery: NSObject, NetServiceBrowserDelegate, NetServiceDelegate {
    var onUpdate: ((BonjourDiscoveryUpdate) -> Void)?
    private let browser = NetServiceBrowser()
    private var resolving: Set<NetService> = []
    private var searchTimeout: DispatchWorkItem?
    private var isSearching = false

    private func report(_ status: String, name: String = "", ip: String = "") {
#if DEBUG
        print("[Bonjour] \(status) main=\(Thread.isMainThread)")
#endif
        onUpdate?(BonjourDiscoveryUpdate(status: status, name: name, ip: ip))
    }

    func start() {
        precondition(Thread.isMainThread)
        searchTimeout?.cancel()
        browser.stop()
        resolving.forEach { $0.stop() }
        resolving.removeAll()
        browser.delegate = self
        report("Discovery starting")
        browser.searchForServices(ofType: "_phonesaber._udp.", inDomain: "local.")
        isSearching = true
        let timeout = DispatchWorkItem { [weak self] in
            guard let self, self.resolving.isEmpty else { return }
            self.report("No service found; Manual IP required (探索継続中)")
        }
        searchTimeout = timeout
        DispatchQueue.main.asyncAfter(deadline: .now() + 10, execute: timeout)
    }

    func ensureRunning() {
        precondition(Thread.isMainThread)
        if !isSearching { start() }
    }

    func netServiceBrowserWillSearch(_ browser: NetServiceBrowser) {
        isSearching = true
        report("Browsing")
    }

    func netServiceBrowserDidStopSearch(_ browser: NetServiceBrowser) {
        isSearching = false
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didRemove service: NetService, moreComing: Bool) {
        resolving.remove(service)
        service.stop()
        report("Service removed: \(service.name); Manual IP available")
    }

    func netServiceWillResolve(_ sender: NetService) {
        report("Resolving: \(sender.name)", name: sender.name)
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didFind service: NetService,
                           moreComing: Bool) {
        guard !resolving.contains(service) else { return }
        searchTimeout?.cancel()
        report("Found: \(service.name)", name: service.name)
        resolving.insert(service)
        service.delegate = self
        service.resolve(withTimeout: 5)
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didNotSearch errorDict: [String: NSNumber]) {
        isSearching = false
        searchTimeout?.cancel()
        report("Browse failed: \(errorDict); Local Network設定を確認 / Manual IP required")
    }

    func netServiceDidResolveAddress(_ sender: NetService) {
        let ip = sender.addresses?.compactMap(ipv4Address).first ?? ""
        let name = sender.name
        let status = ip.isEmpty ? "Resolve failed: IPv4なし; Manual IP required" : "Resolved: \(ip) / Ready"
        report(status, name: name, ip: ip)
    }

    func netService(_ sender: NetService, didNotResolve errorDict: [String: NSNumber]) {
        resolving.remove(sender)
        report("Resolve failed: \(errorDict); Manual IP required", name: sender.name)
    }

    private func ipv4Address(_ address: Data) -> String? {
        address.withUnsafeBytes { rawBuffer in
            guard rawBuffer.count >= MemoryLayout<sockaddr_in>.size,
                  let sockaddrPointer = rawBuffer.baseAddress?.assumingMemoryBound(to: sockaddr.self),
                  sockaddrPointer.pointee.sa_family == sa_family_t(AF_INET) else { return nil }
            var internetAddress = sockaddrPointer.withMemoryRebound(to: sockaddr_in.self, capacity: 1) { $0.pointee }
            var host = [CChar](repeating: 0, count: Int(INET_ADDRSTRLEN))
            guard inet_ntop(AF_INET, &internetAddress.sin_addr, &host, socklen_t(INET_ADDRSTRLEN)) != nil else { return nil }
            return String(cString: host)
        }
    }
}
