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

    mutating func clearManual(sending: Bool) -> Bool {
        guard !sending, source == .manual else { return false }
        self = DestinationHostSelection()
        return true
    }

    mutating func restoreBonjourService(_ name: String) { bonjourServiceName = name }

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

struct DestinationDisplayPolicy {
    private(set) var status = "Discovering..."
    private(set) var otherPC = ""

    mutating func receive(name: String, ip: String, status: String, removed: Bool,
                          selection: DestinationHostSelection) {
        if name.isEmpty || (selection.source != .manual && name == selection.bonjourServiceName) {
            self.status = status
        } else if !ip.isEmpty {
            otherPC = "\(name)　IP: \(ip)"
        } else if removed && otherPC.hasPrefix(name + "　IP:") {
            otherPC = ""
        }
    }

    static func selectedName(_ selection: DestinationHostSelection) -> String {
        selection.source == .manual ? "手動 IP" : selection.bonjourServiceName
    }
}

struct CameraFormatOption: Equatable {
    let index: Int
    let width: Int32
    let height: Int32
    let frameRateRanges: [ClosedRange<Double>]
    var fieldOfView: Float = 0
    var isBinned = false

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

// 60fps と同じ解析寸法・画角だけを許可する。未知の画角は互換とみなさない。
func highSpeedCameraFormatIndex(options: [CameraFormatOption]) -> Int? {
    guard let baselineIndex = preferredCameraFormatIndex(options: options, targetFPS: 60),
          let baseline = options.first(where: { $0.index == baselineIndex }),
          baseline.fieldOfView.isFinite, baseline.fieldOfView > 0 else { return nil }
    return options.filter {
        $0.width == baseline.width && $0.height == baseline.height &&
        $0.fieldOfView == baseline.fieldOfView && supportsFrameRate(120, ranges: $0.frameRateRanges)
    }.sorted { lhs, rhs in
        let left = lhs.frameRateRanges.map(\.upperBound).max() ?? 0
        let right = rhs.frameRateRanges.map(\.upperBound).max() ?? 0
        if left != right { return left > right }
        return lhs.index < rhs.index
    }.first?.index
}

func storedCameraFPS(_ value: Int) -> Int {
    [30, 60, 120].contains(value) ? value : 60
}

// 通常は main と同じ低照度設定だけを書く。HDR・画角を変える設定は明示 opt-in。
func configureCameraPostProcessing(_ camera: AVCaptureDevice, experimental: Bool) -> Bool {
    if camera.isLowLightBoostSupported {
        camera.automaticallyEnablesLowLightBoostWhenAvailable = false
    }
    return !experimental || disableCameraPostProcessing(camera)
}

// 試験終了後に同じ device を再利用しても、試験の設定が通常撮影に残らないようにする。
struct CameraPostProcessingState {
    let format: AVCaptureDevice.Format
    let colorSpace: AVCaptureColorSpace
    let automaticHDR: Bool
    let hdr: Bool
    let distortionCorrection: Bool?

    init(_ camera: AVCaptureDevice) {
        format = camera.activeFormat
        colorSpace = camera.activeColorSpace
        automaticHDR = camera.automaticallyAdjustsVideoHDREnabled
        hdr = camera.isVideoHDREnabled
        distortionCorrection = camera.isGeometricDistortionCorrectionSupported
            ? camera.isGeometricDistortionCorrectionEnabled : nil
    }

    func restore(_ camera: AVCaptureDevice) {
        camera.activeFormat = format
        camera.activeColorSpace = colorSpace
        camera.automaticallyAdjustsVideoHDREnabled = false
        camera.isVideoHDREnabled = hdr
        camera.automaticallyAdjustsVideoHDREnabled = automaticHDR
        if let distortionCorrection { camera.isGeometricDistortionCorrectionEnabled = distortionCorrection }
    }
}

// activeFormat を設定した後、同じ configuration lock 内で呼ぶ。
@discardableResult
func disableCameraPostProcessing(_ camera: AVCaptureDevice) -> Bool {
    // HLG では videoHDREnabled=false が無視されるため、先に SDR へ戻す。
    if camera.activeColorSpace == .HLG_BT2020 {
        guard camera.activeFormat.supportedColorSpaces.contains(.sRGB) else { return false }
        camera.activeColorSpace = .sRGB
    }
    camera.automaticallyAdjustsVideoHDREnabled = false
    camera.isVideoHDREnabled = false
    if camera.isGeometricDistortionCorrectionSupported {
        camera.isGeometricDistortionCorrectionEnabled = false
    }
    return true
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
        // 遅れて届く起動完了で、中断・失敗・復旧の状態を上書きしない。
        guard isSending, state == .starting else { return }
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
    mutating func setForeground(_ active: Bool, at time: TimeInterval, allowRecovery: Bool = true) -> Bool {
        isForeground = active
        guard active, isSending, allowRecovery else { return false }
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

// 最新の表示値と累積カウンタを保持する。UI 処理中はこのロックを保持しない。
// フレーム・送信の各通知は Task を作らず、最新値・累積値と診断の既存窓を更新する。
final class CameraUIPendingState: @unchecked Sendable {
    struct Snapshot {
        var processed = 0
        var rejected = 0
        var redDetections = 0
        var blueDetections = 0
        var redAttempts = 0
        var blueAttempts = 0
        var redCompleted = 0
        var blueCompleted = 0
        var redErrors = 0
        var blueErrors = 0
        var noRoute = 0
        var redEndpoints: (PixelPoint, PixelPoint)?
        var blueEndpoints: (PixelPoint, PixelPoint)?
        var dimensions = (width: 1, height: 1)
        var lastEpoch: TimeInterval?
        var lastLocalSendMs: Double?
        var sendErrors: [Int: String] = [:]
        var firstCameraFrame: TimeInterval?
        var lastCameraFrame: TimeInterval?
        var fps = 0.0
#if DEBUG
        var performanceMetrics: [String: PerformanceMetric] = [:]
        var frameIntervalStatistics: CameraFrameIntervalStatistics?
#endif
    }

    private let lock = NSLock()
    private var state = Snapshot()
    private var dirty = false
    private var running = false
    private var processorGeneration = 0
    private var lifecycleGeneration = 0
    private var frameCount = 0
    private var fpsStart = CACurrentMediaTime()
#if DEBUG
    private var previousTrace: FrameTrace?
    private var diagnosticEventTimes: [String: [TimeInterval]] = [:]
#endif

    func setAcceptance(running: Bool, processor: Int, lifecycle: Int) {
        lock.lock(); defer { lock.unlock() }
        self.running = running
        processorGeneration = processor
        lifecycleGeneration = lifecycle
    }

    // 通知元のキューで世代を固定し、停止・再開前の通知を新しい run に持ち込まない。
    func cameraNotificationGeneration() -> Int? {
        lock.lock(); defer { lock.unlock() }
        return running ? lifecycleGeneration : nil
    }

    func reset() {
        lock.lock(); defer { lock.unlock() }
        let noRoute = state.noRoute
        state = Snapshot()
        state.noRoute = noRoute
        dirty = false
        frameCount = 0
        fpsStart = CACurrentMediaTime()
#if DEBUG
        previousTrace = nil
        diagnosticEventTimes = [:]
#endif
    }

    func takeSnapshot() -> Snapshot? {
        lock.lock(); defer { lock.unlock() }
        guard dirty else { return nil }
        let snapshot = state
        state.firstCameraFrame = nil
        state.lastCameraFrame = nil
        dirty = false
        return snapshot
    }

    func cameraFrame(at time: TimeInterval) {
        lock.lock(); defer { lock.unlock() }
        guard running else { return }
        if state.firstCameraFrame == nil { state.firstCameraFrame = time }
        state.lastCameraFrame = time
        dirty = true
    }

    func frame(_ results: [DetectedSaber], width: Int, height: Int, generation: Int,
               lifecycle: Int?, redEpoch: TimeInterval?, blueEpoch: TimeInterval?, trace: FrameTrace?,
               redEnqueuedAt: TimeInterval?, blueEnqueuedAt: TimeInterval?, requestMs: Double) {
        lock.lock(); defer { lock.unlock() }
        dirty = true
        guard running, generation == processorGeneration, lifecycle == lifecycleGeneration else {
            state.rejected += 1
            return
        }
        state.processed += 1
        state.dimensions = (width, height)
        state.redEndpoints = nil
        state.blueEndpoints = nil
        for result in results {
            switch result.color {
            case .red:
                state.redEndpoints = result.endpoints
                if result.isFresh {
                    state.redAttempts += 1
                    if !result.isPredicted { state.redDetections += 1 }
                }
            case .blue:
                state.blueEndpoints = result.endpoints
                if result.isFresh {
                    state.blueAttempts += 1
                    if !result.isPredicted { state.blueDetections += 1 }
                }
            }
            if result.isFresh { state.lastEpoch = result.color == .red ? redEpoch : blueEpoch }
        }
        frameCount += 1
        let elapsed = CACurrentMediaTime() - fpsStart
        if elapsed >= 1 { state.fps = Double(frameCount) / elapsed; frameCount = 0; fpsStart = CACurrentMediaTime() }
#if DEBUG
        if let trace { recordFrameTrace(trace) }
        for enqueueAt in [redEnqueuedAt, blueEnqueuedAt].compactMap({ $0 }) {
            recordEventRate("UDP enqueue rate")
            if let trace {
                addPerformance("Detection → UDP enqueue", value: max(0, (enqueueAt - trace.detectionEnd) * 1000))
                addPerformance("Post-capture total", value: max(0, (enqueueAt - trace.callbackHostTime) * 1000))
            }
        }
        addPerformance("UDP request", value: requestMs)
#endif
    }

    func completed(port: Int, generation: Int, result: Result<TimeInterval, Error>, processingStart: TimeInterval) {
        // Error の文字列化はロック外で行う。
        let message: String?
        if case .failure(let error) = result { message = error.localizedDescription } else { message = nil }
        lock.lock(); defer { lock.unlock() }
        guard running, generation == lifecycleGeneration else { return }
        dirty = true
        switch result {
        case .success(let completedAt):
            state.sendErrors[port] = nil
            if port == 5005 { state.redCompleted += 1 } else { state.blueCompleted += 1 }
            state.lastLocalSendMs = max(0, (completedAt - processingStart) * 1000)
        case .failure:
            if port == 5005 { state.redErrors += 1 } else { state.blueErrors += 1 }
            state.sendErrors[port] = message
        }
    }

    func noRoute(generation: Int) {
        lock.lock(); defer { lock.unlock() }
        guard running, generation == lifecycleGeneration else { return }
        state.noRoute += 1
        dirty = true
    }
#if DEBUG
    func resetPerformance() {
        lock.lock(); defer { lock.unlock() }
        state.performanceMetrics = [:]
        state.frameIntervalStatistics = nil
        previousTrace = nil
        diagnosticEventTimes = [:]
    }

    func performanceSnapshot() -> (metrics: [String: PerformanceMetric], intervals: CameraFrameIntervalStatistics?) {
        lock.lock(); defer { lock.unlock() }
        return (state.performanceMetrics, state.frameIntervalStatistics)
    }

    func recordUIWork(_ milliseconds: Double) {
        lock.lock(); defer { lock.unlock() }
        addPerformance("UI / overlay state", value: milliseconds)
    }

    func recordDebugPerformanceForTesting(name: String, value: Double) {
        lock.lock(); defer { lock.unlock() }
        addPerformance(name, value: value)
    }

    func udpQueueStart(_ wait: TimeInterval, replaced: Int, generation: Int) {
        lock.lock(); defer { lock.unlock() }
        guard running, generation == lifecycleGeneration else { return }
        recordUDPQueueStart(wait, replaced: replaced)
        dirty = true
    }

    func recordPerformance(_ sample: FramePerformanceSample) {
        lock.lock(); defer { lock.unlock() }
        dirty = true
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
    }

    private func recordFrameTrace(_ trace: FrameTrace) {
        addPerformance("Capture mode FPS", value: Double(trace.captureFPS))
        if let capture = trace.captureHostTime, trace.callbackHostTime >= capture {
            addPerformance("Camera / AVFoundation age", value: (trace.callbackHostTime - capture) * 1000)
        }
        state.frameIntervalStatistics = trace.inputFrameIntervalStatistics
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
    }

    private func recordUDPQueueStart(_ wait: TimeInterval, replaced: Int) {
        addPerformance("UDP queue wait", value: wait * 1000)
        addPerformance("UDP replaced sends", value: Double(replaced))
        recordEventRate("UDP actual send rate")
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
        var metric = state.performanceMetrics[name, default: PerformanceMetric()]
        metric.latest = value
        metric.values.append(value)
        if metric.values.count > 120 { metric.values.removeFirst() }
        metric.maximum = max(metric.maximum, value)
        state.performanceMetrics[name] = metric
    }

#endif
}

@MainActor
final class CameraViewModel: NSObject, ObservableObject {
    let session = AVCaptureSession()
    nonisolated let processor: FrameProcessor
    /// 自動クリップ（AutoClip.swift）。送信中、問題らしい瞬間の前後だけを保存して Mac へ送る。
    nonisolated let autoClip: AutoClipRecorder
    @Published private(set) var autoClipEnabled = false
    @Published private(set) var autoClipStatusLine = ""
    /// 最後に座標を送っていた時刻（自動クリップの転送を遊んでいる間は待たせる）。
    private var lastLiveTrafficAt: TimeInterval?
    private let output = AVCaptureVideoDataOutput()
    private let captureQueue = DispatchQueue(label: "PhoneSaberSender.capture", qos: .userInteractive)
    private lazy var sessionRunner = CaptureSessionRunner(session: session)
    private let sender: UDPSender
    /// Optional peer-to-peer Wi-Fi link to the Mac P2P bridge. Coordinates use it
    /// only while the bridge answers pings; otherwise the LAN `sender` is used.
    private let p2pSender: P2PSender
    private static let p2pEnabledKey = "PhoneSaber.p2pEnabled"
    private static let ignoreZonesKey = "PhoneSaber.ignoreZones.v1"
    private static let mirrorXKey = "PhoneSaber.mirrorX"
    private static let mirrorYKey = "PhoneSaber.mirrorY"
    /// True once `sender` has a LAN destination for the current run.
    private var lanConfigured = false { didSet { updateDeliverySettings() } }
    private let lanProbe: LANLivenessProbe
    /// Coordinates dropped because neither P2P nor LAN could take them.
    private var p2pNoRouteCount = 0
    nonisolated let uiPending = CameraUIPendingState()
    private var uiTimer: DispatchSourceTimer?
    private(set) var uiSnapshotCountForTesting = 0
    static let uiPublishInterval: TimeInterval = 1.0 / 12
    nonisolated private let delivery: CoordinateDelivery
    private let pathMonitor = NWPathMonitor()
    private let bonjourDiscovery = BonjourDiscovery()
    @Published var running = false { didSet { updateDeliverySettings() } }
    @Published var redEndpoints: (PixelPoint, PixelPoint)?
    @Published var blueEndpoints: (PixelPoint, PixelPoint)?
    nonisolated private let healthMeter = DeviceHealthMeter()
    @Published private(set) var deviceHealthLine = "端末状態を確認中"
    @Published private(set) var deviceHealthWarning: String?
    private var lastHealthPublish = 0.0
    private var lastThermalState: ProcessInfo.ThermalState?
    private var requestedHealthFPS = 30.0
    private var thermalFPSPolicy = ThermalFPSPolicy()
    @Published private(set) var thermalFPSMessage = ""
    @Published var fps = 0.0
    @Published var status = "停止中"
    @Published var errorMessage: String?
    @Published private(set) var cameraState: CameraLifecycleState = .stopped
    @Published private(set) var lastCameraFrameAge: TimeInterval?
    @Published private(set) var screenSleepPreventionActive = false
    /// 低発熱表示（送信中に20秒触れなければ preview を止めて暗くする）。表示だけで、認識・送信は変えない。
    let lowHeatDisplay: LowHeatDisplayController
    @Published private(set) var lowHeatDisplayActive = false
    @Published private(set) var lowHeatDisplayEnabled = true
    @Published private(set) var lowHeatStatusLine = ""
    private var lowHeatInhibitedByUI = false
    @Published private(set) var pathStatus = "判定中"
    @Published private(set) var pathInterface = ""
    @Published private(set) var host = ""
    @Published private(set) var networkDiscoveryStatus = "Discovering..."
    @Published private(set) var discoveredMacName = ""
    @Published private(set) var discoveredMacIP = ""
    @Published private(set) var otherDiscoveredPC = ""
    private var destinationDisplay = DestinationDisplayPolicy()
    private var latestResolvedMac = (name: "", ip: "")
    var manualHostActive: Bool { hostSelection.source == .manual }
    @Published private(set) var connectionMode = "Auto (Bonjour)"
    @Published private(set) var station = ""
    @Published private(set) var p2pEnabled = true { didSet { updateDeliverySettings() } }
    @Published private(set) var p2pState: P2PLinkState = .disabled
    /// 送信中なのに Unity の応答も P2P も無い状態が続いている（見出しは NETWORK READY のままになる）。
    @Published private(set) var pcReplyMissing = false
    var pcReplyMissingMessage: String { PCReplyWatch.message(manual: hostSelection.source == .manual) }
    /// 通信ログの直近（画面用）。
    @Published private(set) var connectivityLogTail: [String] = []
    private var pcReplyWatch = PCReplyWatch()
    private let connectivityClock: () -> TimeInterval
    private var connectivityLogRevision = -1
    static let connectivityLogTailCount = 12
    @Published private(set) var p2pRoundTrip: P2PRoundTripStats.Summary?
    /// Opt-in exposure experiment (default auto = device exposure untouched).
    @Published private(set) var cameraExposureExperiment: CameraExposureExperiment = .auto
    @Published private(set) var cameraExposureExperimentState = CameraExposureExperimentState.initial
    @Published var threshold = 145
    @Published var dominance = 25
    @Published var measurementMode = false { didSet { updateDeliverySettings() } }
    @Published var outputWidth = 1920 { didSet { updateDeliverySettings() } }
    @Published var outputHeight = 1080 { didSet { updateDeliverySettings() } }
    @Published var mirrorX = false {
        didSet {
            UserDefaults.standard.set(mirrorX, forKey: Self.mirrorXKey)
            updateDeliverySettings()
        }
    }
    @Published var mirrorY = false {
        didSet {
            UserDefaults.standard.set(mirrorY, forKey: Self.mirrorYKey)
            updateDeliverySettings()
        }
    }
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
    @Published private(set) var debugRecordingActive = false { didSet { updateLowHeatDisplay() } }
    /// Operator ground truth for the current part of a Debug Recording
    /// (metadata only). Reset to unlabeled at every Start.
    @Published var debugSegmentLabel: DebugSegmentLabel = .unlabeled {
        didSet { processor.setDebugSegmentLabel(debugSegmentLabel) }
    }
    @Published private(set) var debugRecordingFinalizing = false
    @Published private(set) var manualLosslessCapturePending = false
    @Published private(set) var debugManualLosslessCaptureCount = 0
    @Published private(set) var debugRecordingStatus = "OFF"
    @Published private(set) var debugTransferEntry: DebugBundleOutbox.Entry?
    @Published private(set) var debugTransferPendingCount = 0
    @Published private(set) var lastDebugRecordingResult: DebugRecordingResult?
    @Published private(set) var debugRecordingSessions: [DebugRecordingSessionSummary] = []
    @Published private(set) var debugRecordingCleanupStatus = ""
    /// Opt-in guided recording (ガイド付き録画). Manual recording is unchanged
    /// and stays the default; these are only set by `startGuidedRecording`.
    @Published private(set) var guidedRecordingRunning = false { didSet { updateLowHeatDisplay() } }
    @Published private(set) var guidedRecordingStatus: GuidedRecordingStatus?
    /// The full plan shown in the UI; `startGuidedRecording(script:)` may run another one.
    let guidedRecordingScript = GuidedRecordingScript.shootingPlanV3
    let swingOnlyRecordingScript = GuidedRecordingScript.swingOnlyV1
    private var activeGuidedScript = GuidedRecordingScript.shootingPlanV3
    /// Replaced in tests so nothing is spoken.
    var makeGuidedCuePlayer: @MainActor () -> GuidedRecordingCuePlaying = { SpeechGuidedRecordingCuePlayer() }
    private var guidedScheduler: GuidedRecordingScheduler?
    private var guidedStartTime: TimeInterval = 0
    private var guidedTask: Task<Void, Never>?
    /// Kept after the guide ends so the closing cue can finish speaking.
    private var guidedCues: GuidedRecordingCuePlaying?
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
    @Published private(set) var debugSupports60FPS = false
    /// F9 遅延テスト用: 選択した2つのfpsを30秒ごとに切り替える（保存しない）。
    /// PC 側は赤の受信数（約30/約60/約120 件/秒）でどちらの区間かを判別する。
    @Published var debugAlternateFPS = false {
        didSet { scheduleFPSAlternation() }
    }
    @Published var debugComparisonHighFPS = 60 {
        didSet { if debugAlternateFPS { scheduleFPSAlternation() } }
    }
    private var fpsAlternationTask: Task<Void, Never>?
    private var fpsComparisonPolicy = FPSComparisonPolicy()
    @Published private(set) var debug60FPSFormats = "Start the camera to inspect this device"
    @Published var debugDetailedProfilingEnabled = false {
        didSet { processor.setDetailedProfilingEnabled(debugDetailedProfilingEnabled) }
    }
    private var lastPerformancePublish = 0.0
    private var lastCameraSampleTime = 0.0
    private(set) var debugPerformancePublishCountForTesting = 0
    private weak var activeCamera: AVCaptureDevice?
#endif
    private var lifecycleGeneration = 0 { didSet { updateDeliverySettings() } }
    private var cameraErrorMessage: String?
    private var connectionErrorMessage: String?
    private var sendErrorMessages: [Int: String] = [:]
    private var hostSelection = DestinationHostSelection() { didSet { updateDeliverySettings() } }
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
    private var cameraBackoff = RecoveryBackoff()
    private var cameraRecoveryTask: Task<Void, Never>?
    private var resumePolicy = SendingResumePolicy()
    private var permissionPending = false
    private var automaticResumePending = false
    private var lastDiscoveryRefresh = 0.0
    @Published var autoStartSending = false {
        didSet {
            if NSClassFromString("XCTestCase") == nil {
                UserDefaults.standard.set(autoStartSending, forKey: SendingResumePolicy.autoStartKey)
            }
        }
    }
    /// 本番のカメラ fps（既定60）。2026-10-07 の Mac+iPhone 実測で撮影→送信が 50→37ms。
    /// 60fps の形式がない端末は30fpsで動かす。認識・UDP形式は変えない。
    static let cameraFPSKey = "cameraFPS"
    @Published private(set) var cameraFPS: Int = storedCameraFPS(UserDefaults.standard.integer(forKey: CameraViewModel.cameraFPSKey))
    @Published private(set) var supports60FPS = true
    @Published private(set) var supports120FPS = false
    @Published private(set) var lowLatencyCameraSettings = false
    static let lowLatencyCameraLaunchKey = "PhoneSaber.experiment.lowLatencyCamera"
    private var cameraPostProcessingState: CameraPostProcessingState?
    private var cameraWideColorAutomatic: Bool?
    private var latencyLogPolicy = CaptureLatencyLogPolicy()
    private var metricsCadence = MetricsCadence()
    private var activeCaptureFPS = 0.0
    @Published private(set) var automaticResumeMessage = ""
    @Published private(set) var cameraRecoveryMessage = ""
    private var cameraRecoveryGeneration = 0
    private var cameraLifecycleEnabled = true
    private let automaticLANDiscoveryEnabled: Bool
    /// Camera the exposure experiment was applied to, and its own default
    /// activeMaxExposureDuration read before this app capped it.
    private weak var exposureExperimentCamera: AVCaptureDevice?
    private var exposureExperimentDefaultMaximum = CMTime.invalid
    private var exposureExperimentCapActive = false

    init(
        processor: FrameProcessor = FrameProcessor(),
        sender: UDPSender = UDPSender(),
        p2pSender: P2PSender = P2PSender(),
        p2pEnabled: Bool? = nil,
        connectivityClock: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
        cameraExposureExperiment: CameraExposureExperiment? = nil,
        authorizationStatus: @escaping () -> AVAuthorizationStatus = { AVCaptureDevice.authorizationStatus(for: .video) },
        requestAccess: @escaping (@escaping (Bool) -> Void) -> Void = { completion in AVCaptureDevice.requestAccess(for: .video, completionHandler: completion) },
        idleTimerUpdater: @escaping @MainActor (Bool) -> Void = { disabled in
            UIApplication.shared.isIdleTimerDisabled = disabled
        },
        lowHeatDisplay: LowHeatDisplayController? = nil
    ) {
        self.processor = processor
        self.sender = sender
        self.p2pSender = p2pSender
        self.connectivityClock = connectivityClock
        // XCTest では本物の Unity（この Mac で Play 中の Editor や起動中のゲーム）の 5007 応答を拾わない。
        // 拾うと LAN が確認済みになり、P2P 優先を確かめるテストが環境次第で落ちる（2026-10-12 の verify）。
        let probePort = NSClassFromString("XCTestCase") == nil ? LANLivenessProbe.port : LANLivenessProbe.unusedPortForTests
        let probe = LANLivenessProbe(port: probePort, clock: connectivityClock)
        self.lanProbe = probe
        self.delivery = CoordinateDelivery(sender: sender, p2pSender: p2pSender, lanProbe: probe)
        // Unit tests opt in explicitly, so a bridge running on the developer's Mac
        // can never reroute the existing LAN tests.
        let underTest = NSClassFromString("XCTestCase") != nil
        // 単体テストの送信先は applyBonjourForTesting で明示する。実 Unity の広告を拾わない。
        self.automaticLANDiscoveryEnabled = !underTest
        self.autoStartSending = !underTest && UserDefaults.standard.bool(forKey: SendingResumePolicy.autoStartKey)
        self.resumePolicy = SendingResumePolicy(wasSending: !underTest && UserDefaults.standard.bool(forKey: SendingResumePolicy.wasSendingKey))
        // 無人の比較計測用: 起動引数 `-PhoneSaber.experiment.lowLatencyCamera YES` のときだけ試験設定を ON で始める。
        // アプリはこの鍵を保存しないので、引数なしの通常起動では必ず OFF。
        self.lowLatencyCameraSettings = !underTest && UserDefaults.standard.bool(forKey: Self.lowLatencyCameraLaunchKey)
        self.mirrorX = underTest ? false : UserDefaults.standard.bool(forKey: Self.mirrorXKey)
        self.mirrorY = underTest ? false : UserDefaults.standard.bool(forKey: Self.mirrorYKey)
        self.p2pEnabled = p2pEnabled
            ?? (underTest ? false : UserDefaults.standard.object(forKey: Self.p2pEnabledKey) as? Bool ?? true)
        self.station = underTest ? "" : (UserDefaults.standard.string(forKey: PhoneSaberStation.preferenceKey)
            .flatMap { PhoneSaberStation.choices.contains($0) ? $0 : nil } ?? "")
        // Tests always start from auto unless they opt in, like P2P above.
        let exposureExperiment = cameraExposureExperiment
            ?? (underTest ? .auto : CameraExposureExperiment.stored(in: .standard))
        self.cameraExposureExperiment = exposureExperiment
        self.cameraExposureExperimentState = exposureExperiment == .auto
            ? .initial : CameraExposureExperimentState(setting: exposureExperiment, status: .pending)
        self.authorizationStatus = authorizationStatus
        self.requestAccess = requestAccess
        self.idleTimerUpdater = idleTimerUpdater
        // 既定 ON。起動引数 `-PhoneSaber.lowHeatDisplay NO` でもその起動だけ OFF（文字列の NO も bool(forKey:) で読む）。
        // XCTest は画面輝度に触れないよう、注入しない限り OFF・保存なし。
        let lowHeatStored = UserDefaults.standard.object(forKey: LowHeatDisplayPolicy.preferenceKey) == nil
            || UserDefaults.standard.bool(forKey: LowHeatDisplayPolicy.preferenceKey)
        self.lowHeatDisplay = lowHeatDisplay ?? LowHeatDisplayController(
            enabled: !underTest && lowHeatStored, now: ProcessInfo.processInfo.systemUptime,
            defaults: underTest ? nil : .standard, observeApplication: !underTest)
        self.lowHeatDisplayEnabled = self.lowHeatDisplay.isEnabled
        // 既定 ON。XCTest では保存値を読まず OFF（端末に clip を書かない）。
        let autoClipOn = !underTest && AutoClipSettings.storedEnabled()
        self.autoClip = AutoClipRecorder(
            directory: FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first
                ?? FileManager.default.temporaryDirectory,
            enabled: autoClipOn)
        self.autoClipEnabled = autoClipOn
        super.init()
        self.lowHeatDisplay.onDimmedChange = { [weak self] dimmed in self?.lowHeatDimmedChanged(dimmed) }
        if !underTest {
            let manual = UserDefaults.standard.string(forKey: "PhoneSaber.manualHost") ?? ""
            self.hostSelection.setManual(manual, resolvedHost: "", serviceName: "")
            if manual.isEmpty {
                self.hostSelection.restoreBonjourService(UserDefaults.standard.string(forKey: "PhoneSaber.bonjourService") ?? "")
            }
            self.host = self.hostSelection.host
            self.connectionMode = self.hostSelection.source.rawValue
            updateSelectedDestinationDisplay()
        }
        let timer = DispatchSource.makeTimerSource(queue: .main)
        timer.schedule(deadline: .now() + Self.uiPublishInterval, repeating: Self.uiPublishInterval,
                       leeway: .milliseconds(5))
        timer.setEventHandler { [weak self] in
            MainActor.assumeIsolated {
                self?.publishPendingUI()
                self?.publishDeviceHealth(at: ProcessInfo.processInfo.systemUptime)
            }
        }
        uiTimer = timer
        timer.resume()
        registerCameraSessionObservers()
        UIDevice.current.isBatteryMonitoringEnabled = true
        sessionObserverTokens.append(NotificationCenter.default.addObserver(
            forName: ProcessInfo.thermalStateDidChangeNotification, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.publishDeviceHealth(at: ProcessInfo.processInfo.systemUptime, force: true) }
        })
        publishDeviceHealth(at: ProcessInfo.processInfo.systemUptime, force: true)
        pathMonitor.pathUpdateHandler = { [weak self] path in
            let status: String
            switch path.status {
            case .satisfied: status = path.usesInterfaceType(.wifi) ? "Wi-Fi経路あり" : path.usesInterfaceType(.cellular) ? "セルラー経路あり（5G等とは断定しません）" : "経路あり"
            case .unsatisfied: status = "経路なし"
            case .requiresConnection: status = "接続判定中"
            @unknown default: status = "判定中"
            }
            let interface = path.availableInterfaces.map { String(describing: $0.type) }.joined(separator: ", ")
            Task { @MainActor in
                guard let self else { return }
                self.receiveNetworkPathUpdate(status: status, interface: interface)
            }
        }
        pathMonitor.start(queue: DispatchQueue(label: "PhoneSaberSender.path"))
        lanProbe.onCommand = { [weak self] command in
            Task { @MainActor in self?.applyPCCommand(command) }
        }
        bonjourDiscovery.onUpdate = { [weak self] update in
            Task { @MainActor in
                self?.receiveBonjourUpdate(update)
            }
        }
        bonjourDiscovery.station = station
        updateDiagnosticDestination()
        if automaticLANDiscoveryEnabled { bonjourDiscovery.start() }
        if self.p2pEnabled { startP2P() }
        processor.onProcessingActivity = { active in
            ConnectivityLog.sendGapMonitor.processing(active: active)
        }
        processor.onHealthSample = { [healthMeter] time, milliseconds, generation, captureToSendMs in
            healthMeter.processed(at: time, milliseconds: milliseconds, generation: generation,
                                  captureToSendMs: captureToSendMs)
        }
        updateDeliverySettings()
        processor.onResult = { [weak self] results, width, height, processingStart, generation, trace in
            guard let self else { return }
            let sent = self.sendResults(results, width: width, height: height,
                                        processingStart: processingStart, generation: generation)
#if DEBUG
            let redEnqueuedAt = sent?.redEnqueuedAt
            let blueEnqueuedAt = sent?.blueEnqueuedAt
            let requestMs = sent?.requestMs ?? 0
#else
            let redEnqueuedAt: TimeInterval? = nil
            let blueEnqueuedAt: TimeInterval? = nil
            let requestMs = 0.0
#endif
            self.uiPending.frame(results, width: width, height: height, generation: generation,
                                 lifecycle: sent?.lifecycleGeneration, redEpoch: sent?.redEpoch,
                                 blueEpoch: sent?.blueEpoch, trace: trace, redEnqueuedAt: redEnqueuedAt,
                                 blueEnqueuedAt: blueEnqueuedAt, requestMs: requestMs)
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
        autoClip.onLog = { line in ConnectivityLog.shared.record("AUTOCLIP", line) }
        autoClip.onClipSaved = { [weak self] result in
            Task { @MainActor in self?.autoClipSaved(result) }
        }
        processor.setAnalysisObserver(autoClip)
        refreshAutoClipStatus()
        refreshDebugTransferStatus()
        sessionObserverTokens.append(NotificationCenter.default.addObserver(
            forName: DebugBundleTransfer.didChange, object: nil, queue: .main
        ) { [weak self] _ in
            Task { @MainActor in self?.refreshDebugTransferStatus() }
        })
        sessionObserverTokens.append(NotificationCenter.default.addObserver(
            forName: UIApplication.didBecomeActiveNotification, object: nil, queue: .main
        ) { _ in
            DebugBundleTransfer.shared.retryPending()
        })
        processor.onDebugRecordingLimitReached = { [weak self] reason in
            Task { @MainActor in
                guard let self else { return }
                self.debugRecordingActive = false
                DebugBundleTransfer.shared.setRecordingActive(false)
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
            self?.uiPending.recordPerformance(sample)
        }
#endif
    }

    var networkStateLabel: String {
        guard running else {
            return host.isEmpty && !p2pState.isConnected ? "DISCOVERING" : "NETWORK IDLE"
        }
        // 経路選択（CoordinateDelivery）と同じく、Unity 応答で確認済みの LAN を P2P より優先して表示する。
        if p2pState.isConnected && !(lanConfigured && lanProbe.isAlive) { return "NETWORK READY (P2P)" }
        if !lanConfigured { return p2pEnabled ? "NETWORK SEARCHING (P2P)" : "NETWORK CONNECTING" }
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
        // session の同期停止中も通知元を main 待ちにしない。状態更新は既存の MainActor Task へ渡す。
        let center = NotificationCenter.default
        sessionObserverTokens.append(center.addObserver(
            forName: AVCaptureSession.wasInterruptedNotification,
            object: session,
            queue: nil
        ) { [weak self] notification in
            guard let generation = self?.uiPending.cameraNotificationGeneration() else { return }
            let reason = notification.userInfo?[AVCaptureSessionInterruptionReasonKey]
                .map { String(describing: $0) } ?? "理由不明"
            Task { @MainActor [weak self] in
                guard let self, self.lifecycleGeneration == generation else { return }
                self.handleCameraInterruption(reason: reason)
            }
        })
        sessionObserverTokens.append(center.addObserver(
            forName: AVCaptureSession.interruptionEndedNotification,
            object: session,
            queue: nil
        ) { [weak self] _ in
            guard let generation = self?.uiPending.cameraNotificationGeneration() else { return }
            Task { @MainActor [weak self] in
                guard let self, self.lifecycleGeneration == generation else { return }
                self.handleCameraInterruptionEnded()
            }
        })
        sessionObserverTokens.append(center.addObserver(
            forName: AVCaptureSession.runtimeErrorNotification,
            object: session,
            queue: nil
        ) { [weak self] notification in
            guard let generation = self?.uiPending.cameraNotificationGeneration() else { return }
            let error = notification.userInfo?[AVCaptureSessionErrorKey] as? NSError
            let message = error?.localizedDescription ?? "AVCaptureSession runtime error"
            Task { @MainActor [weak self] in
                guard let self, self.lifecycleGeneration == generation else { return }
                self.handleCameraRuntimeError(message)
            }
        })
    }

    func sceneDidChange(isActive: Bool) {
        if !isActive && debugRecordingActive { stopDebugRecording(reason: .background) }
        let wasInactive = !sceneIsActive
        sceneIsActive = isActive
        updateIdleTimerPolicy()
        let now = ProcessInfo.processInfo.systemUptime
        let recoveryRequested = cameraLifecycle.setForeground(isActive, at: now, allowRecovery: true)
        let shouldRecover = cameraLifecycleEnabled && recoveryRequested
        publishCameraLifecycle(at: now)
        guard isActive else { return }
        if resumePolicy.foreground(autoStart: autoStartSending), !running {
            automaticResumePending = true
            start()
        } else if running && automaticResumePending == false && wasInactive {
            automaticResumePending = true
        }
        refreshNetworkDiscovery()
        bonjourDiscovery.ensureRunning()
        if p2pEnabled { p2pSender.recoverIfNeeded() }
        guard running else { return }
        sender.recoverIfNeeded()
        if shouldRecover {
            scheduleCameraRecovery(alreadyMarkedRecovering: true)
        }
    }

    func retryCameraRecovery() {
        guard running, sceneIsActive, cameraLifecycleEnabled else { return }
        cameraBackoff.reset()
        scheduleCameraRecovery(alreadyMarkedRecovering: false)
    }

    private func handleCameraInterruption(reason: String) {
        guard running else { return }
        if debugRecordingActive { stopDebugRecording(reason: .interruption) }
        ConnectivityLog.sendGapMonitor.context("camera interruption: \(reason)")
        cameraLifecycle.interruptionBegan(reason: reason)
        publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
    }

    private func handleCameraInterruptionEnded() {
        guard running else { return }
        if sceneIsActive {
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
        ConnectivityLog.sendGapMonitor.context("camera runtime error: \(message)")
        cameraLifecycle.runtimeError(message)
        cameraErrorMessage = message
        publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
        recomputeErrorMessage()
        if sceneIsActive {
            scheduleCameraRecovery(alreadyMarkedRecovering: false)
        }
    }

    private func scheduleCameraRecovery(alreadyMarkedRecovering: Bool) {
        guard running, sceneIsActive, cameraLifecycleEnabled, !cameraRecoveryInProgress else { return }
        if debugRecordingActive { stopDebugRecording(reason: .runtimeFailure) }
        let now = ProcessInfo.processInfo.systemUptime
        if !alreadyMarkedRecovering && !cameraLifecycle.beginRecovery(at: now) { return }
        guard let delay = cameraBackoff.nextDelay(at: now) else { return }
        cameraRecoveryInProgress = true
        ConnectivityLog.sendGapMonitor.context("camera recovery backoff=\(delay)s")
        cameraRecoveryMessage = "カメラを再接続中…（\(Int(delay))秒後に再試行）。続く場合は箱の通風・カメラ権限を確認"
        cameraRecoveryGeneration += 1
        let recoveryGeneration = cameraRecoveryGeneration
        let runGeneration = lifecycleGeneration
        resetProcessor()
        publishCameraLifecycle(at: now)
        cameraRecoveryTask = Task { @MainActor [weak self] in
            try? await Task.sleep(for: .seconds(delay))
            guard let self, !Task.isCancelled,
                  self.lifecycleGeneration == runGeneration,
                  self.cameraRecoveryGeneration == recoveryGeneration, self.running else { return }
            guard self.sceneIsActive else {
                self.cameraRecoveryInProgress = false
                self.cameraLifecycle.runtimeError("前面への復帰を待っています")
                self.cameraRecoveryMessage = "前面への復帰を待っています。アプリを開いてください"
                self.publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
                return
            }
            self.sessionRunner.stop { [weak self] in
                guard let self, self.lifecycleGeneration == runGeneration,
                      self.cameraRecoveryGeneration == recoveryGeneration, self.running else { return }
                guard CameraRecoveryCompletionPolicy.canConfigure(sending: self.resumePolicy.wantsSending,
                                                                  foreground: self.sceneIsActive) else {
                    self.cameraRecoveryInProgress = false
                    self.cameraLifecycle.runtimeError("前面への復帰を待っています")
                    self.cameraRecoveryMessage = "前面への復帰を待っています。アプリを開いてください"
                    self.publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
                    return
                }
                // 待機中の中断通知で状態が変わっても、今回の再起動の完了を受理できるようにする。
                _ = self.cameraLifecycle.beginRecovery(at: ProcessInfo.processInfo.systemUptime)
                self.configureAndStart(isRecovery: true, recoveryGeneration: recoveryGeneration)
            }
        }
    }

    private func finishCameraStart(succeeded: Bool, lifecycle: Int,
                                   recovery: Int?, isRecovery: Bool) {
        switch CameraStartCompletionPolicy.action(completedLifecycle: lifecycle,
                                                  currentLifecycle: lifecycleGeneration,
                                                  running: running) {
        case .apply:
            break
        case .ignore:
            return
        case .stopSession:
            // 次の start() の beginConfiguration と停止処理を競合させない。
            // 次の start() の設定区間に、この停止を持ち越さない。
            sessionRunner.stopSynchronously()
            return
        }
        // 初回起動の遅い完了通知で、その後の中断・エラー・復旧状態を上書きしない。
        guard isRecovery || cameraLifecycle.state == .starting || cameraLifecycle.state == .live else { return }
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
        publishDeviceHealth(at: time)
        guard cameraLifecycleEnabled, running else { return }
        let becameStalled = cameraLifecycle.evaluateStall(at: time)
        publishCameraLifecycle(at: time)
        if sceneIsActive && time - lastDiscoveryRefresh >= 30 { refreshNetworkDiscovery() }
        if sceneIsActive && cameraState.canRetry && !cameraRecoveryInProgress {
            scheduleCameraRecovery(alreadyMarkedRecovering: false)
        }
        if becameStalled {
            cameraErrorMessage = "カメラframeが2秒以上届いていません"
            recomputeErrorMessage()
            if sceneIsActive {
                scheduleCameraRecovery(alreadyMarkedRecovering: false)
            }
        }
    }

    private func publishDeviceHealth(at time: TimeInterval, force: Bool = false) {
        guard force || time - lastHealthPublish >= 1 else { return }
        lastHealthPublish = time
        let state = ProcessInfo.processInfo.thermalState
        let thermal = DeviceThermalLevel(state: state)
        if let previous = lastThermalState, previous != state {
            print("[DeviceHealth] 発熱: \(DeviceThermalLevel(state: previous).title) → \(thermal.title)")
        }
        lastThermalState = state
        let rates = running ? healthMeter.snapshot(at: time) : DeviceHealthRates()
        let battery = deviceBatterySnapshot()
        if latencyLogPolicy.shouldLog(at: time, sending: running) {
            ConnectivityLog.shared.recordToFileOnly("CAPTURE_METRICS", CaptureLatencyLogPolicy.line(
                route: delivery.currentRoute.rawValue, captureModeFPS: activeCaptureFPS,
                rates: rates, thermal: thermal, brightness: Double(UIScreen.main.brightness),
                batteryLevel: battery.level, batteryState: battery.state))
        }
        if metricsCadence.due(at: time, sending: running),
           delivery.currentRoute == .lanVerified || delivery.currentRoute == .lanUnverified {
            let rtt = lanProbe.metricsRoundTrip(at: time)
            var metrics = MetricsDatagram()
            metrics.station = station
            metrics.app = (Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String) ?? "unknown"
            metrics.fpsMode = activeCaptureFPS
            metrics.captureFPS = rates.cameraFPS; metrics.redFPS = rates.redSendFPS; metrics.blueFPS = rates.blueSendFPS
            metrics.median = rates.medianCaptureToSendMs; metrics.p95 = rates.p95CaptureToSendMs
            metrics.samples = rates.captureToSendSamples
            metrics.rtt = rtt.median; metrics.rttSamples = rtt.count; metrics.rttAge = rtt.age
            metrics.thermal = thermal.metadataValue; metrics.battery = battery.level.map { $0 * 100 }
            metrics.lowLatencyTest = lowLatencyCameraSettings
            if let line = metrics.line() { lanProbe.sendMetrics(line) }
        }
        if debugRecordingActive {
            processor.updateDebugDeviceHealthState(DebugDeviceHealthState(
                thermalState: thermal.metadataValue, batteryLevel: battery.level, batteryState: battery.state))
        }
        let line = "発熱 \(thermal.title) / \(DeviceHealthText.battery(level: battery.level, state: battery.title))\n" + DeviceHealthText.timing(rates)
        assignIfChanged(\.deviceHealthLine, line)
        updateLowHeatDisplay(at: time)
        lowHeatDisplay.tick(at: time)
        if lowHeatDisplayActive {
            // 暗い画面の1行はこの1秒ごとの更新でだけ書き換える。
            assignIfChanged(\.lowHeatStatusLine, LowHeatDisplayPolicy.statusLine(
                state: lowHeatStateLabel, cameraFPS: rates.cameraFPS, thermal: thermal.title))
        }
        assignIfChanged(\.deviceHealthWarning, DeviceHealthText.warning(thermal: thermal, rates: rates,
                                                       requestedFPS: requestedHealthFPS, running: running,
                                                       batteryLevel: battery.level, batteryState: battery.state,
                                                       lowPowerMode: ProcessInfo.processInfo.isLowPowerModeEnabled))
        // 自動クリップ: 発熱・fps 低下（前回の判定）で止め、転送はプレイの合間に回す。
        updateAutoClipPolicy(thermalState: state, thermal: thermal, rates: rates, at: time)
        let effectiveFPS = effectiveCameraFPS(at: time)
        // 復旧中の停止・再設定を割り込まず、live になってから fps を反映する。
        if cameraLifecycleEnabled && ThermalFPSPolicy.shouldReconfigure(
            currentFPS: requestedHealthFPS, effectiveFPS: effectiveFPS, sending: running,
            foreground: sceneIsActive, cameraLive: cameraLifecycle.state == .live,
            recovering: cameraRecoveryInProgress) {
            restartCameraForFPSChange()
        }
    }

    private func deviceBatterySnapshot() -> (level: Double?, state: String, title: String?) {
        let value = Double(UIDevice.current.batteryLevel)
        let level = value.isFinite && (0...1).contains(value) ? value : nil
        switch UIDevice.current.batteryState {
        case .charging: return (level, "charging", "充電中")
        case .full: return (level, "full", "満充電")
        case .unplugged: return (level, "unplugged", "未充電")
        case .unknown: return (level, "unknown", nil)
        @unknown default: return (level, "unknown", nil)
        }
    }

    private func receiveCameraFrame(at time: TimeInterval) {
        guard running, cameraLifecycleEnabled else { return }
        cameraLifecycle.receivedFrame(at: time)
        guard cameraLifecycle.state == .live else { return }
        cameraBackoff.receivedFrame(at: time)
        assignIfChanged(\.cameraRecoveryMessage, "")
        if automaticResumePending {
            automaticResumeMessage = "自動で送信を再開しました"
            automaticResumePending = false
        }
        cameraErrorMessage = nil
        publishCameraLifecycle(at: time)
        recomputeErrorMessage()
    }

    private func publishCameraLifecycle(at time: TimeInterval) {
        assignIfChanged(\.cameraState, cameraLifecycle.state)
        if let lastFrameAt = cameraLifecycle.lastFrameAt, time >= lastFrameAt {
            assignIfChanged(\.lastCameraFrameAge, time - lastFrameAt)
        } else {
            assignIfChanged(\.lastCameraFrameAge, nil)
        }
    }

    private func updateIdleTimerPolicy() {
        updateLowHeatDisplay()
        let shouldPreventSleep = (running || resumePolicy.wantsSending) && sceneIsActive
        guard screenSleepPreventionActive != shouldPreventSleep else { return }
        screenSleepPreventionActive = shouldPreventSleep
        idleTimerUpdater(shouldPreventSleep)
    }

    deinit {
        uiTimer?.cancel()
        cameraWatchdogTask?.cancel()
        cameraRecoveryTask?.cancel()
        pathMonitor.cancel()
        sessionObserverTokens.forEach(NotificationCenter.default.removeObserver)
    }

    func start() {
        guard !running, !permissionPending else { return }
        DebugBundleTransfer.shared.retryPending()
        autoClip.beginSession()
        resumePolicy.start()
        ConnectivityLog.sendGapMonitor.setIntent(true)
        persistSendingIntent()
        updateIdleTimerPolicy()
        lifecycleGeneration += 1
        let requestedGeneration = lifecycleGeneration
        switch authorizationStatus() {
        case .authorized: configureAndStart()
        case .notDetermined:
            permissionPending = true
            requestAccess { [weak self] granted in
                Task { @MainActor in
                    guard let self else { return }
                    self.authorizationCallbackCount += 1
                    guard self.lifecycleGeneration == requestedGeneration, !self.running else { return }
                    self.permissionPending = false
                    guard self.sceneIsActive else { return }
                    granted ? self.configureAndStart() : self.fail("カメラ権限がありません")
                }
            }
        default: fail("設定アプリでカメラ権限を許可してください")
        }
    }

    // MARK: 低発熱表示

    private var lowHeatStateLabel: String {
        guard running else { return "停止中" }
        if cameraState != .live { return cameraState.displayLabel }
        return pcReplyMissing ? "送信中・PC応答なし" : "送信中"
    }

    private var reconfiguringCamera = false

    private func updateLowHeatDisplay(at time: TimeInterval = ProcessInfo.processInfo.systemUptime) {
        lowHeatDisplay.update(sending: running || reconfiguringCamera, foreground: sceneIsActive,
                              inhibited: debugRecordingActive || guidedRecordingRunning || lowHeatInhibitedByUI,
                              at: time)
    }

    private func lowHeatDimmedChanged(_ dimmed: Bool) {
        assignIfChanged(\.lowHeatDisplayActive, dimmed)
        if !dimmed { assignIfChanged(\.lowHeatStatusLine, "") }
    }

    /// 画面のどこかに触れた（window 全体の touch）。暗い間なら元に戻す。
    func lowHeatTouch(at time: TimeInterval = ProcessInfo.processInfo.systemUptime) {
        lowHeatDisplay.touch(at: time)
    }

    // MARK: 無視する赤・青の物（会場のコーンなど。SaberIgnoreZone）

    private var ignoreZones: [SaberColor: [SaberIgnoreZone]] = CameraViewModel.storedIgnoreZones()
    @Published private(set) var ignoreZoneSummary = CameraViewModel.summary(CameraViewModel.storedIgnoreZones())
    static let maximumIgnoreZones = 4

    nonisolated private static func storedIgnoreZones() -> [SaberColor: [SaberIgnoreZone]] {
        guard NSClassFromString("XCTestCase") == nil,
              let data = UserDefaults.standard.data(forKey: ignoreZonesKey),
              let stored = try? JSONDecoder().decode([String: [SaberIgnoreZone]].self, from: data) else { return [:] }
        var zones: [SaberColor: [SaberIgnoreZone]] = [:]
        if let red = stored["red"], !red.isEmpty { zones[.red] = Array(red.suffix(maximumIgnoreZones)) }
        if let blue = stored["blue"], !blue.isEmpty { zones[.blue] = Array(blue.suffix(maximumIgnoreZones)) }
        return zones
    }

    nonisolated private static func summary(_ zones: [SaberColor: [SaberIgnoreZone]]) -> String {
        let red = zones[.red]?.count ?? 0, blue = zones[.blue]?.count ?? 0
        return red + blue == 0 ? "なし" : "赤 \(red) か所・青 \(blue) か所"
    }

    /// 剣を消し（カメラの外・電源 OFF）、コーンなど置きっぱなしの物だけが検出されている状態で押す。
    /// 今その色で選ばれている候補の両端を囲む範囲を覚え、以後その範囲の候補は選ばない（次の候補を選ぶ）。
    @discardableResult
    func ignoreCurrentDetection(_ color: SaberColor) -> Bool {
        guard let endpoints = color == .red ? redEndpoints : blueEndpoints else { return false }
        let zone = SaberIgnoreZone(around: endpoints, width: sourceDimensions.width, height: sourceDimensions.height)
        var list = ignoreZones[color] ?? []
        list.append(zone)
        ignoreZones[color] = Array(list.suffix(Self.maximumIgnoreZones))
        applyIgnoreZones()
        ConnectivityLog.shared.record("IGNORE_ZONE", "add color=\(color == .red ? "red" : "blue") zone=\(zone.minX),\(zone.minY),\(zone.maxX),\(zone.maxY)")
        return true
    }

    func clearIgnoreZones() {
        ignoreZones = [:]
        applyIgnoreZones()
        ConnectivityLog.shared.record("IGNORE_ZONE", "clear")
    }

    private static let trackLockKey = "PhoneSaber.trackLock.v1"
    @Published private(set) var trackLockEnabled = NSClassFromString("XCTestCase") == nil
        && UserDefaults.standard.bool(forKey: CameraViewModel.trackLockKey)

    func setTrackLockEnabled(_ enabled: Bool) {
        trackLockEnabled = enabled
        processor.setTrackLockEnabled(enabled)
        if NSClassFromString("XCTestCase") == nil { UserDefaults.standard.set(enabled, forKey: Self.trackLockKey) }
    }

    private var seenPCCommandIDs: Set<Int> = []

    /// Mac の F8 から届いた指示（同じ id は一度だけ）。
    func applyPCCommand(_ command: LANLivenessProbe.PCCommand) {
        guard seenPCCommandIDs.insert(command.id).inserted else { return }
        if command.clearIgnore { clearIgnoreZones() }
        if let name = command.ignore {
            let colors: [SaberColor] = name == "both" ? [.red, .blue] : [name == "blue" ? .blue : .red]
            for color in colors where !ignoreCurrentDetection(color) {
                ConnectivityLog.shared.record("IGNORE_ZONE", "pc command id=\(command.id): no current \(color == .red ? "red" : "blue") detection")
            }
        }
        if let seconds = command.learnAreaSeconds { learnPlayArea(seconds: seconds, commandID: command.id) }
        if command.clearArea { clearPlayArea(commandID: command.id) }
        if let enabled = command.trackLock { setTrackLockEnabled(enabled) }
        if let bias = command.exposureBias { setExposureBias(bias, commandID: command.id) }
        if let enabled = command.autoClip { setAutoClipEnabled(enabled, commandID: command.id) }
        if command.clip { requestAutoClip(commandID: command.id) }
    }

    // MARK: 自動クリップ（AutoClip.swift）

    func setAutoClipEnabled(_ enabled: Bool, commandID: Int? = nil) {
        autoClipEnabled = enabled
        if NSClassFromString("XCTestCase") == nil { UserDefaults.standard.set(enabled, forKey: AutoClipSettings.enabledKey) }
        autoClip.setEnabled(enabled)
        ConnectivityLog.shared.record("AUTOCLIP", "\(enabled ? "on" : "off") id=\(commandID.map(String.init) ?? "-")")
        refreshAutoClipStatus()
    }

    /// PC の clip=1。送信中だけ、次のコマの前後を保存する。
    func requestAutoClip(commandID: Int? = nil) {
        let request = commandID.map { "pc-command id=\($0)" } ?? "manual"
        guard running else {
            ConnectivityLog.shared.record("AUTOCLIP", "clip skipped reason=notSending request=\(request)")
            return
        }
        autoClip.requestClip(reason: request)
    }

    private func autoClipSaved(_ result: Result<URL, Error>) {
        switch result {
        case .success(let url):
            ConnectivityLog.shared.record("AUTOCLIP", "saved \(url.lastPathComponent)")
            // 座標を送っている間は転送を始めない（DebugBundleTransfer の deferDuringPlay）。
            DebugBundleTransfer.shared.enqueue(bundleURL: url, automatic: true, deferDuringPlay: true)
        case .failure(let error):
            ConnectivityLog.shared.record("AUTOCLIP", "save failed: \(error.localizedDescription)")
        }
        refreshAutoClipStatus()
    }

    /// 1秒ごと（publishDeviceHealth）。発熱・fps 低下・Debug Recording 中は止め、転送はプレイの合間に回す。
    private func updateAutoClipPolicy(thermalState: ProcessInfo.ThermalState, thermal: DeviceThermalLevel,
                                      rates: DeviceHealthRates, at time: TimeInterval) {
        autoClip.updateEnvironment(AutoClipEnvironment(
            thermal: thermal.metadataValue, exposureBias: exposureBias,
            appVersion: (Bundle.main.object(forInfoDictionaryKey: "CFBundleVersion") as? String) ?? "unknown"))
        autoClip.setSuspended(AutoClipSuspendPolicy.reason(
            thermal: thermalState, fpsReduced: thermalFPSPolicy.reduced,
            debugRecording: debugRecordingActive || debugRecordingFinalizing))
        if running && (rates.redSendFPS > 0 || rates.blueSendFPS > 0) { lastLiveTrafficAt = time }
        let live = running && lastLiveTrafficAt.map { time - $0 < Self.autoClipQuietSeconds } ?? false
        DebugBundleTransfer.shared.setLiveTrafficActive(live)
        refreshAutoClipStatus()
    }

    /// 座標の送信がこの秒数止まったら（曲の合間・停止後）、保存した clip を転送する。
    static let autoClipQuietSeconds: TimeInterval = 5

    private func refreshAutoClipStatus() {
        let status = autoClip.statusSnapshot
        var line: String
        if !status.enabled {
            line = "自動クリップ: OFF"
        } else if let reason = status.suspendedReason {
            line = "自動クリップ: 一時停止（\(Self.autoClipSuspendText(reason))）"
        } else {
            line = "自動クリップ: ON（この送信で \(status.savedThisSession) 件保存）"
        }
        if let error = status.lastError { line += " / 保存失敗: \(error)" }
        assignIfChanged(\.autoClipStatusLine, line)
    }

    private static func autoClipSuspendText(_ reason: String) -> String {
        switch reason {
        case "debugRecording": return "Debug Recording 中"
        case "thermalSerious", "thermalCritical", "thermalUnknown": return "発熱"
        case "fpsReduced": return "発熱で fps を下げている間"
        default: return reason
        }
    }

    // MARK: プレイ範囲（剣を振る場所。SaberPlayArea）

    /// 保存キーと定数（actor に属さない。nonisolated の読み込みから使う）。
    enum PlayAreaSettings {
        static let playAreaKey = "PhoneSaber.playArea.v1"
        static let exposureBiasKey = "PhoneSaber.exposureBias.v1"
        static let learnSeconds = 10
    }

    @Published private(set) var playArea: SaberPlayArea? = CameraViewModel.storedPlayArea()
    @Published private(set) var playAreaLearning = false
    private var playAreaLearningTask: Task<Void, Never>?
    static let playAreaLearnSeconds = PlayAreaSettings.learnSeconds

    nonisolated private static func storedPlayArea() -> SaberPlayArea? {
        guard NSClassFromString("XCTestCase") == nil,
              let data = UserDefaults.standard.data(forKey: PlayAreaSettings.playAreaKey),
              let area = try? JSONDecoder().decode(SaberPlayArea.self, from: data), area.isValid else { return nil }
        return area
    }

    var playAreaSummary: String {
        let area = playArea.map {
            String(format: "x %.0f〜%.0f%% / y %.0f〜%.0f%%", $0.minX * 100, $0.maxX * 100, $0.minY * 100, $0.maxY * 100)
        } ?? "なし（全体）"
        return playAreaLearning ? "\(area)　覚えています…（\(Self.playAreaLearnSeconds)秒、範囲いっぱいに振る）" : area
    }

    /// 送信中に [seconds] 秒、選ばれた winner（赤・青）の両端を数え、足りれば（30 コマ以上）範囲にして保存する。
    /// 足りなければ前の範囲を残す。学習中は範囲で絞らない（無視範囲は効く）。
    func learnPlayArea(seconds: Int = PlayAreaSettings.learnSeconds, commandID: Int? = nil) {
        let id = commandID.map(String.init) ?? "-"
        guard running else {
            ConnectivityLog.shared.record("PLAY_AREA", "learn id=\(id): not sending")
            return
        }
        playAreaLearningTask?.cancel()
        processor.startPlayAreaLearning()
        playAreaLearning = true
        ConnectivityLog.shared.record("PLAY_AREA", "learn start id=\(id) seconds=\(seconds)")
        playAreaLearningTask = Task { @MainActor [weak self] in
            try? await Task.sleep(nanoseconds: UInt64(seconds) * 1_000_000_000)
            guard let self, !Task.isCancelled else { return }
            self.finishPlayAreaLearning(id: id)
        }
    }

    private func finishPlayAreaLearning(id: String) {
        playAreaLearningTask = nil
        playAreaLearning = false
        let previous = playArea.map { "\($0.minX),\($0.minY),\($0.maxX),\($0.maxY)" } ?? "none"
        guard let learner = processor.finishPlayAreaLearning() else {
            ConnectivityLog.shared.record("PLAY_AREA", "learn id=\(id): not learning; kept \(previous)")
            return
        }
        guard let area = learner.area() else {
            ConnectivityLog.shared.record("PLAY_AREA", "learn id=\(id): only \(learner.samples) samples (< \(SaberPlayArea.minimumSamples)); kept \(previous)")
            return
        }
        applyPlayArea(area)
        ConnectivityLog.shared.record("PLAY_AREA", "learn id=\(id) samples=\(learner.samples) area=\(area.minX),\(area.minY),\(area.maxX),\(area.maxY)")
    }

    func clearPlayArea(commandID: Int? = nil) {
        if playAreaLearningTask != nil {
            playAreaLearningTask?.cancel(); playAreaLearningTask = nil
            playAreaLearning = false
            _ = processor.finishPlayAreaLearning()
        }
        applyPlayArea(nil)
        ConnectivityLog.shared.record("PLAY_AREA", "clear id=\(commandID.map(String.init) ?? "-")")
    }

    private func applyPlayArea(_ area: SaberPlayArea?) {
        playArea = area
        processor.setPlayArea(area)
        guard NSClassFromString("XCTestCase") == nil else { return }
        if let area, let data = try? JSONEncoder().encode(area) {
            UserDefaults.standard.set(data, forKey: PlayAreaSettings.playAreaKey)
        } else {
            UserDefaults.standard.removeObject(forKey: PlayAreaSettings.playAreaKey)
        }
    }

    // MARK: 露出補正（光らない物を暗くする試験スイッチ）

    /// 既定 0 = OFF で、0 の間はカメラの露出に一切触れない。シャッター上限（CameraExposureExperiment）だけでは
    /// 自動露出が ISO を上げて戻すため、負の露出補正（setExposureTargetBias）で服などを暗くし、自発光の LED 剣だけを
    /// 明るく残す狙い。0 以外では認識への入力（画素）が変わる。閾値は変えない。実機で比べるための試験設定。
    static let exposureBiasChoices = LANLivenessProbe.exposureBiasChoices
    @Published private(set) var exposureBias: Int = CameraViewModel.storedExposureBias()
    @Published private(set) var exposureBiasStatus = "露出補正: 0（OFF）"
    private weak var exposureBiasCamera: AVCaptureDevice?
    /// このアプリが 0 以外を設定したか（0 に戻すときだけ 0 を書き込む）。
    private var exposureBiasTouched = false

    nonisolated private static func storedExposureBias() -> Int {
        guard NSClassFromString("XCTestCase") == nil else { return 0 }
        let value = UserDefaults.standard.integer(forKey: PlayAreaSettings.exposureBiasKey)
        return LANLivenessProbe.exposureBiasChoices.contains(value) ? value : 0
    }

    static func exposureBiasLabel(_ ev: Int) -> String { ev == 0 ? "0（OFF）" : "−\(-ev) EV" }

    func setExposureBias(_ ev: Int, commandID: Int? = nil) {
        guard Self.exposureBiasChoices.contains(ev) else { return }
        exposureBias = ev
        if NSClassFromString("XCTestCase") == nil { UserDefaults.standard.set(ev, forKey: PlayAreaSettings.exposureBiasKey) }
        ConnectivityLog.shared.record("EXPOSURE_BIAS", "set ev=\(ev) id=\(commandID.map(String.init) ?? "-")")
        if let camera = exposureBiasCamera {
            applyExposureBias(to: camera, reason: "set")
        } else {
            exposureBiasStatus = "露出補正: \(Self.exposureBiasLabel(ev))" + (ev == 0 ? "" : "（送信開始時に適用）")
        }
    }

    /// カメラ設定の後（形式変更・熱による fps 変更・復旧のたび）と、値の変更時に呼ぶ。カメラは止めない。
    private func applyExposureBias(to camera: AVCaptureDevice, reason: String) {
        if exposureBiasCamera !== camera { exposureBiasTouched = false }
        exposureBiasCamera = camera
        let ev = exposureBias
        guard ev != 0 || exposureBiasTouched else {
            exposureBiasStatus = "露出補正: \(Self.exposureBiasLabel(0))"
            return
        }
        let low = Double(camera.minExposureTargetBias), high = Double(camera.maxExposureTargetBias)
        let bias = Float(min(max(Double(ev), low), high))
        do {
            try camera.lockForConfiguration()
            defer { camera.unlockForConfiguration() }
            camera.setExposureTargetBias(bias, completionHandler: nil)
            exposureBiasTouched = ev != 0
            exposureBiasStatus = String(format: "露出補正: %@（適用 %+.1f EV、範囲 %.1f〜%.1f）",
                                        Self.exposureBiasLabel(ev), Double(bias), low, high)
            ConnectivityLog.shared.record("EXPOSURE_BIAS", "applied ev=\(ev) bias=\(bias) range=\(low)...\(high) reason=\(reason)")
        } catch {
            exposureBiasStatus = "露出補正: \(Self.exposureBiasLabel(ev)) を適用できません（\(error.localizedDescription)）"
            ConnectivityLog.shared.record("EXPOSURE_BIAS", "failed ev=\(ev) reason=\(reason) error=\(error.localizedDescription)")
        }
    }

    private func applyIgnoreZones() {
        processor.setIgnoreZones(ignoreZones)
        ignoreZoneSummary = Self.summary(ignoreZones)
        guard NSClassFromString("XCTestCase") == nil else { return }
        var stored: [String: [SaberIgnoreZone]] = [:]
        if let red = ignoreZones[.red] { stored["red"] = red }
        if let blue = ignoreZones[.blue] { stored["blue"] = blue }
        if let data = try? JSONEncoder().encode(stored) { UserDefaults.standard.set(data, forKey: Self.ignoreZonesKey) }
    }

    func setLowHeatDisplayEnabled(_ enabled: Bool) {
        guard enabled != lowHeatDisplayEnabled else { return }
        lowHeatDisplayEnabled = enabled
        if NSClassFromString("XCTestCase") == nil {
            UserDefaults.standard.set(enabled, forKey: LowHeatDisplayPolicy.preferenceKey)
        }
        lowHeatDisplay.setEnabled(enabled, at: ProcessInfo.processInfo.systemUptime)
    }

    /// 録画の再生（QuickLook）など、画面を見ている間は暗くしない。
    func setLowHeatInhibitedByUI(_ inhibited: Bool) {
        guard inhibited != lowHeatInhibitedByUI else { return }
        lowHeatInhibitedByUI = inhibited
        updateLowHeatDisplay()
    }

    /// 1秒ごとの更新と同じ判定を、注入した時刻で行う。
    func lowHeatTickForTesting(at time: TimeInterval) {
        updateLowHeatDisplay(at: time)
        lowHeatDisplay.tick(at: time)
    }

    func retryDiscovery() {
        guard !running else { return }
        if automaticLANDiscoveryEnabled { bonjourDiscovery.start() }
    }

    // 台変更は停止中のみ。旧台の自動送信先と P2P の lock を破棄する。
    func setStation(_ value: String) {
        guard !running, value != station, PhoneSaberStation.choices.contains(value) else { return }
        p2pSender.stop()
        p2pState = .disabled
        p2pRoundTrip = nil
        station = value
        UserDefaults.standard.removeObject(forKey: "PhoneSaber.bonjourService")
        UserDefaults.standard.set(value, forKey: PhoneSaberStation.preferenceKey)
        latestResolvedMac = (name: "", ip: "")
        if hostSelection.source != .manual {
            hostSelection = DestinationHostSelection()
            host = ""
            connectionMode = hostSelection.source.rawValue
        }
        destinationDisplay = DestinationDisplayPolicy()
        otherDiscoveredPC = ""
        updateSelectedDestinationDisplay()
        updateDiagnosticDestination()
        bonjourDiscovery.station = value
        if automaticLANDiscoveryEnabled { bonjourDiscovery.start() }
        if p2pEnabled { startP2P() }
    }

    private func updateDiagnosticDestination() {
        P2PPreferredMac.configure(station: station, lanHost: host, manual: hostSelection.source == .manual)
    }

    func clearManualHost() {
        guard hostSelection.clearManual(sending: sendingRequested) else { return }
        UserDefaults.standard.removeObject(forKey: "PhoneSaber.manualHost")
        UserDefaults.standard.removeObject(forKey: "PhoneSaber.bonjourService")
        host = ""
        connectionMode = hostSelection.source.rawValue
        destinationDisplay = DestinationDisplayPolicy()
        otherDiscoveredPC = ""
        updateSelectedDestinationDisplay()
        networkDiscoveryStatus = destinationDisplay.status
        latestResolvedMac = (name: "", ip: "")
        updateDiagnosticDestination()
        bonjourDiscovery.station = station
        if automaticLANDiscoveryEnabled { bonjourDiscovery.start() }
        if p2pEnabled { startP2P() }
    }

    private func updateSelectedDestinationDisplay() {
        discoveredMacName = DestinationDisplayPolicy.selectedName(hostSelection)
        discoveredMacIP = hostSelection.host
    }

    func setManualHost(_ value: String) {
        if NSClassFromString("XCTestCase") == nil {
            UserDefaults.standard.set(value, forKey: "PhoneSaber.manualHost")
            UserDefaults.standard.removeObject(forKey: "PhoneSaber.bonjourService")
        }
        hostSelection.setManual(value, resolvedHost: latestResolvedMac.ip, serviceName: latestResolvedMac.name)
        host = hostSelection.host
        connectionMode = hostSelection.source.rawValue
        updateSelectedDestinationDisplay()
        updateDiagnosticDestination()
        if hostSelection.source == .manual {
            p2pSender.stop()
            p2pState = .disabled
            p2pRoundTrip = nil
        } else if p2pEnabled { startP2P() }
#if DEBUG
        print("[Host] source=\(hostSelection.source.rawValue) resolved=\(discoveredMacIP)")
#endif
    }

    private func receiveBonjourUpdate(_ update: BonjourDiscoveryUpdate) {
        guard update.name.isEmpty || PhoneSaberStation.matches(service: update.name, station: station) else { return }
        latestResolvedMac = (name: update.name, ip: update.ip)
        if update.removed && hostSelection.source != .manual && hostSelection.bonjourServiceName == update.name {
            lanConfigured = false
            sender.stop(); lanProbe.stop()
        }
        if hostSelection.applyBonjour(host: update.ip, serviceName: update.name) ||
            (!update.ip.isEmpty && hostSelection.source != .manual && hostSelection.bonjourServiceName == update.name && !lanConfigured) {
            if NSClassFromString("XCTestCase") == nil {
                UserDefaults.standard.set(update.name, forKey: "PhoneSaber.bonjourService")
            }
            host = hostSelection.host
            connectionMode = hostSelection.source.rawValue
            updateDiagnosticDestination()
#if DEBUG
            print("[Host] source=\(hostSelection.source.rawValue) resolved=\(update.ip) service=\(update.name)")
#endif
            if running {
                if lanConfigured {
                    sender.updateHost(update.ip)
                    // 生存確認も新しい IP へ向ける。旧 IP のままだと LAN が確認済みにならず、
                    // LAN で届くのに遅い P2P へ回り続ける。
                    ConnectivityLog.shared.record("LAN", "宛先の IP が変わった \(update.ip)")
                    lanProbe.start(host: update.ip)
                } else {
                    // Started on P2P alone; the LAN fallback becomes available now.
                    configureLAN(host: update.ip, generation: lifecycleGeneration)
                }
                activeDestination = "\(update.ip):5005 / \(update.ip):5006"
            }
        }
        destinationDisplay.receive(name: update.name, ip: update.ip, status: update.status,
                                   removed: update.removed, selection: hostSelection)
        updateSelectedDestinationDisplay()
        networkDiscoveryStatus = destinationDisplay.status
        otherDiscoveredPC = destinationDisplay.otherPC
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

    func selectCameraFPS(_ fps: Int, persist: Bool = true) {
        guard [30, 60, 120].contains(fps), fps != cameraFPS,
              fps != 120 || supports120FPS else { return }
        cameraFPS = fps
        if persist && NSClassFromString("XCTestCase") == nil {
            UserDefaults.standard.set(fps, forKey: Self.cameraFPSKey)
        }
        if running && sceneIsActive {
            // 背面での自動比較終了は設定だけ戻し、再設定は前面復帰まで待つ。
            // A full stop/reconfigure keeps activeFormat, frame durations, the
            // output connection and the one-slot processor generation atomic.
            restartCameraForFPSChange()
        }
    }

    private func restartCameraForFPSChange() {
        ConnectivityLog.sendGapMonitor.context("camera stop/reconfigure/start requested-fps=\(cameraFPS)")
        // 内部再設定も送信の意思が続く一つの区間として診断する。
        ConnectivityLog.sendGapMonitor.beginReconfiguration()
        // 発熱による fps 切替などの内部再設定では、低発熱表示を「送信停止」とみなして明るく戻さない。
        reconfiguringCamera = true
        stop()
        start()
        reconfiguringCamera = false
        updateLowHeatDisplay()
        ConnectivityLog.sendGapMonitor.endReconfiguration()
    }

    func setLowLatencyCameraSettings(_ enabled: Bool) {
        guard enabled != lowLatencyCameraSettings, cameraExposureExperimentEditable else { return }
        lowLatencyCameraSettings = enabled
        if running && sceneIsActive { restartCameraForFPSChange() }
    }

    private func effectiveCameraFPS(at time: TimeInterval) -> Int {
        // 保存値が端末の対応 fps を超える場合も、実際の撮影 fps を基準に熱対策する。
        let supportedFPS = cameraFPS == 120 && supports120FPS ? 120
            : cameraFPS >= 60 && supports60FPS ? 60 : 30
        let effectiveFPS = thermalFPSPolicy.effectiveFPS(
            thermal: DeviceThermalLevel(state: ProcessInfo.processInfo.thermalState), at: time, userFPS: supportedFPS)
        thermalFPSMessage = effectiveFPS < supportedFPS
            ? "発熱のため \(effectiveFPS)fps に下げています。箱を開けて通風を確認" : ""
        return effectiveFPS
    }

#if DEBUG
    private func scheduleFPSAlternation() {
        fpsAlternationTask?.cancel()
        fpsAlternationTask = nil
        fpsComparisonPolicy.setEnabled(debugAlternateFPS, at: ProcessInfo.processInfo.systemUptime)
        guard debugAlternateFPS else {
            // 終了したら保存済みの設定へ戻す。
            let saved = storedCameraFPS(UserDefaults.standard.integer(forKey: Self.cameraFPSKey))
            selectCameraFPS(saved == 120 && !supports120FPS ? 60 : saved, persist: false)
            return
        }
        if debugComparisonHighFPS == 120 && supports120FPS && cameraFPS == 30 {
            selectCameraFPS(60, persist: false)
        }
        fpsAlternationTask = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 30_000_000_000)
                guard let self, !Task.isCancelled, self.debugAlternateFPS else { return }
                if self.fpsComparisonPolicy.shouldDisable(at: ProcessInfo.processInfo.systemUptime) {
                    self.debugAlternateFPS = false
                    return
                }
                if self.running && self.supports60FPS && self.sceneIsActive {
                    self.selectCameraFPS(FPSComparisonPolicy.nextFPS(current: self.cameraFPS, highFPS: self.debugComparisonHighFPS,
                                                                  supports120: self.supports120FPS), persist: false)
                }
            }
        }
    }
#endif

    private func persistSendingIntent() {
        if NSClassFromString("XCTestCase") == nil {
            UserDefaults.standard.set(resumePolicy.wantsSending, forKey: SendingResumePolicy.wasSendingKey)
        }
    }

    func receiveNetworkPathUpdate(status: String, interface: String) {
        // 2026-10-18: iPhone がテザリングの親機のとき、中身が同じ経路の通知が 3〜6 秒おきに届く。
        // そのたびに Bonjour の探索と P2P の復旧をやり直すと電波を取り合い、座標が 0.2〜1 秒届かなかった。
        // 状態も経路も変わっていない通知では何もしない。
        guard status != pathStatus || interface != pathInterface else { return }
        pathStatus = status; pathInterface = interface
        // NWConnection が経路変更を追う。広告の再解決中も既知の LAN 宛先を失わない。
        if running { sender.recoverIfNeeded() }
        ConnectivityLog.shared.record("PATH", "\(status) interface=\(interface)")
        refreshNetworkDiscovery()
    }

    private func refreshNetworkDiscovery() {
        lastDiscoveryRefresh = ProcessInfo.processInfo.systemUptime
        if automaticLANDiscoveryEnabled { bonjourDiscovery.start() }
        if p2pEnabled { p2pSender.recoverIfNeeded() }
        if running { sender.recoverIfNeeded() }
    }

    var sendingRequested: Bool { running || resumePolicy.wantsSending }

    var pcReconnectMessage: String? {
        guard running, !(hostSelection.source != .manual && p2pState.isConnected),
              pathStatus == "経路なし" || !lanConfigured || senderStates.values.contains(where: { $0 == "waiting" || $0 == "failed" }) else { return nil }
        return "PC を再接続中…"
    }

    func stop() {
        // 表示の最終集計でfps比較が期限切れになっても、停止中のカメラを再起動させない。
        running = false
        publishPendingUI()
        // 内部再設定の途中で終了しても、次回起動時の送信再開を失わない。
        if !reconfiguringCamera { resumePolicy.stop() }
        ConnectivityLog.sendGapMonitor.setIntent(false)
        persistSendingIntent()
        permissionPending = false
        automaticResumePending = false
        automaticResumeMessage = ""
        cameraRecoveryMessage = ""
        cameraRecoveryTask?.cancel()
        cameraRecoveryTask = nil
        lifecycleGeneration += 1
        cameraRecoveryGeneration += 1
        cameraRecoveryInProgress = false
        cameraBackoff.reset()
        if debugRecordingActive { stopDebugRecording() }
        refreshConnectivityStatus()
        publishDeviceHealth(at: ProcessInfo.processInfo.systemUptime, force: true)
        cameraWatchdogTask?.cancel()
        cameraWatchdogTask = nil
        cameraLifecycle.stop()
        cameraLifecycleEnabled = true
        publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
        updateIdleTimerPolicy()
        sessionRunner.stopSynchronously()
        sender.stop(); lanConfigured = false; lanProbe.stop(); resetProcessor(); activeDestination = "未設定"; status = "停止中"; redEndpoints = nil; blueEndpoints = nil; senderStates = [:]; senderErrors = [:]; cameraErrorMessage = nil; connectionErrorMessage = nil; sendErrorMessages = [:]; recomputeErrorMessage()
    }

    private func configureAndStart(isRecovery: Bool = false, recoveryGeneration: Int? = nil) {
        processor.setIgnoreZones(ignoreZones)
        processor.setPlayArea(playArea)
        processor.setTrackLockEnabled(trackLockEnabled)
        let configuredHost = host.trimmingCharacters(in: .whitespacesAndNewlines)
        cameraErrorMessage = nil
        connectionErrorMessage = nil
        sendErrorMessages = [:]
#if DEBUG
        resetDebugPerformance()
#endif
        recomputeErrorMessage()
        if !isRecovery {
            cameraLifecycle.requestStart(at: ProcessInfo.processInfo.systemUptime)
        }
        running = true
        cameraLifecycleEnabled = true
        updateIdleTimerPolicy()
        startCameraWatchdog()
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
        if let automatic = cameraWideColorAutomatic {
            session.automaticallyConfiguresCaptureDeviceForWideColor = automatic
            cameraWideColorAutomatic = nil
        }
        if lowLatencyCameraSettings {
            cameraWideColorAutomatic = session.automaticallyConfiguresCaptureDeviceForWideColor
            session.automaticallyConfiguresCaptureDeviceForWideColor = false
        }
        session.addInput(input)
        let formatOptions = camera.formats.enumerated().map { index, format in
            let size = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            return CameraFormatOption(
                index: index,
                width: size.width,
                height: size.height,
                frameRateRanges: format.videoSupportedFrameRateRanges.map { $0.minFrameRate...$0.maxFrameRate },
                fieldOfView: format.videoFieldOfView, isBinned: format.isVideoBinned
            )
        }
        let sixtyFPSOptions = formatOptions.filter { supportsFrameRate(60, ranges: $0.frameRateRanges) }
        supports60FPS = !sixtyFPSOptions.isEmpty
        let highSpeedIndex = highSpeedCameraFormatIndex(options: formatOptions)
        supports120FPS = highSpeedIndex != nil
#if DEBUG
        debugSupports60FPS = supports60FPS
        debug60FPSFormats = Self.formatSummary(sixtyFPSOptions)
#endif
        let targetFPS = Double(effectiveCameraFPS(at: ProcessInfo.processInfo.systemUptime))
        requestedHealthFPS = targetFPS
        let selectedIndex = targetFPS == 120 ? highSpeedIndex
            : preferredCameraFormatIndex(options: formatOptions, targetFPS: targetFPS)
        let format = selectedIndex.map { camera.formats[$0] } ?? camera.formats.min { left, right in
            let lhs = CMVideoFormatDescriptionGetDimensions(left.formatDescription)
            let rhs = CMVideoFormatDescriptionGetDimensions(right.formatDescription)
            return Int64(lhs.width) * Int64(lhs.height) < Int64(rhs.width) * Int64(rhs.height)
        }
        if let format {
            do {
                try camera.lockForConfiguration()
                defer { camera.unlockForConfiguration() }
                if let saved = cameraPostProcessingState {
                    saved.restore(camera)
                    cameraPostProcessingState = nil
                }
                camera.activeFormat = format
                if #available(iOS 18.0, *), format.isAutoVideoFrameRateSupported {
                    camera.isAutoVideoFrameRateEnabled = false
                }
                if lowLatencyCameraSettings { cameraPostProcessingState = CameraPostProcessingState(camera) }
                guard configureCameraPostProcessing(camera, experimental: lowLatencyCameraSettings) else {
                    throw NSError(domain: "PhoneSaberCamera", code: 1,
                                  userInfo: [NSLocalizedDescriptionKey: "このカメラ形式ではHDRを無効にできません"])
                }
                let supportedRanges = format.videoSupportedFrameRateRanges.map { $0.minFrameRate...$0.maxFrameRate }
                if let duration = fixedFrameDuration(fps: targetFPS, ranges: supportedRanges) {
                    camera.activeVideoMinFrameDuration = duration
                    camera.activeVideoMaxFrameDuration = duration
                }
            } catch {
                if lowLatencyCameraSettings || cameraPostProcessingState != nil {
                    session.commitConfiguration()
                    cameraRecoveryInProgress = false
                    fail("カメラ形式を設定できません: \(error.localizedDescription)")
                    return
                }
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
        // 再設定で preview の connection は作り直される。低発熱表示の間は止めたままにする。
        lowHeatDisplay.reapplyPreviewState()
        // After the format is committed: a format change resets the device's
        // activeMaxExposureDuration, so the experiment is (re)applied here.
        // With the default auto setting this does not touch the device.
        applyCameraExposureExperiment(to: camera)
        // 露出補正（既定 0 では触れない）。形式の再設定・熱による fps 変更はここを通るので毎回かけ直す。
        applyExposureBias(to: camera, reason: isRecovery ? "recovery" : "configure")
        let actualDuration = CMTimeGetSeconds(camera.activeVideoMaxFrameDuration)
        let actualFPS = actualDuration > 0 && actualDuration.isFinite ? Int((1 / actualDuration).rounded()) : 0
        activeCaptureFPS = Double(actualFPS)
#if DEBUG
        activeCamera = camera
        updateCameraConfiguration(camera)
        // F9 の PC ログと照合する設定区間。座標 datagram には何も追加しない。
        print("[CAPTURE_METRICS] \(ISO8601DateFormatter().string(from: Date())) station=\(station.isEmpty ? "?" : station) requested-fps=\(cameraFPS) capture-fps=\(actualFPS)")
#endif
        processor.configureCaptureFPS(actualFPS)
        let activeFormatIndex = camera.formats.firstIndex(of: camera.activeFormat).map { String($0) } ?? "?"
        ConnectivityLog.shared.record("CAPTURE_MODE", "station=\(station.isEmpty ? "?" : station) requested-fps=\(cameraFPS) capture-fps=\(actualFPS) low-latency=\(lowLatencyCameraSettings) format-index=\(activeFormatIndex) format=\(CMVideoFormatDescriptionGetDimensions(camera.activeFormat.formatDescription)) fov=\(camera.activeFormat.videoFieldOfView) binned=\(camera.activeFormat.isVideoBinned) BGRA portrait")
        processor.configureCaptureSynchronizationClock(session.synchronizationClock)
        let currentGeneration = lifecycleGeneration
        if configuredHost.isEmpty {
            lanConfigured = false; lanProbe.stop()
            activeDestination = "PC を再接続中…"
        } else {
            configureLAN(host: configuredHost, generation: currentGeneration)
            activeDestination = "\(configuredHost):5005 / \(configuredHost):5006"
        }
        host = configuredHost
        connectionMode = hostSelection.source.rawValue
        resetStartState()
        processor.configureThresholds(red: configuredThreshold, blue: configuredThreshold)
        resetProcessor(); status = "送信中"
        healthMeter.reset(at: ProcessInfo.processInfo.systemUptime, generation: processor.currentGeneration)
        publishDeviceHealth(at: ProcessInfo.processInfo.systemUptime, force: true)
        publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
        sessionRunner.start { [weak self] succeeded in
            self?.finishCameraStart(succeeded: succeeded, lifecycle: currentGeneration,
                                    recovery: recoveryGeneration, isRecovery: isRecovery)
        }
    }

    private struct SendMetrics {
        let lifecycleGeneration: Int
        var redEpoch: TimeInterval?
        var blueEpoch: TimeInterval?
#if DEBUG
        var redEnqueuedAt: TimeInterval?
        var blueEnqueuedAt: TimeInterval?
        var requestMs = 0.0
#endif
    }

    private func resetProcessor() {
        // reset が検出キューの終了を待つ間も旧世代の送信・fallback を拒否する。
        delivery.suspend()
        _ = processor.reset()
        updateDeliverySettings()
    }

    private func updateDeliverySettings() {
        uiPending.setAcceptance(running: running, processor: processor.currentGeneration, lifecycle: lifecycleGeneration)
        var settings = CoordinateDelivery.Settings()
        settings.running = running
        settings.processorGeneration = processor.currentGeneration
        settings.lifecycleGeneration = lifecycleGeneration
        settings.outputWidth = outputWidth
        settings.outputHeight = outputHeight
        settings.mirrorX = mirrorX
        settings.mirrorY = mirrorY
        settings.measurementMode = measurementMode
        settings.manual = hostSelection.source == .manual
        settings.p2pEnabled = p2pEnabled
        settings.lanConfigured = lanConfigured
        delivery.update(settings)
    }

    // 検出キューで payload と経路を確定。UI の描画・診断更新は送信を待たせない。
    nonisolated private func sendResults(_ results: [DetectedSaber], width: Int, height: Int,
                                        processingStart: TimeInterval, generation: Int) -> SendMetrics? {
        ConnectivityLog.sendGapMonitor.results(fresh: results.contains { $0.isFresh })
        guard let settings = delivery.snapshot(generation: generation) else { return nil }
        var metrics = SendMetrics(lifecycleGeneration: settings.lifecycleGeneration)
        for result in results where result.isFresh {
            let coordinates = payload(for: result.endpoints, source: (width, height),
                                      output: (settings.outputWidth, settings.outputHeight),
                                      mirrorX: settings.mirrorX, mirrorY: settings.mirrorY)
            let text: String
            if settings.measurementMode {
                let epoch = Date().timeIntervalSince1970
                if result.color == .red { metrics.redEpoch = epoch }
                else { metrics.blueEpoch = epoch }
                text = timestampedPayload(coordinates, timestamp: epoch)
            } else { text = coordinates }
            let port = result.color == .red ? 5005 : 5006
            let sendGeneration = settings.lifecycleGeneration
#if DEBUG
            let enqueueAt = HostMonotonicClock.now()
            if result.color == .red { metrics.redEnqueuedAt = enqueueAt }
            else { metrics.blueEnqueuedAt = enqueueAt }
            let sendRequestStart = ProcessInfo.processInfo.systemUptime
#endif
            let completion: (Result<TimeInterval, Error>) -> Void = { [weak self] result in
                switch result {
                case .success: ConnectivityLog.sendGapMonitor.sent()
                case .failure(let error):
                    ConnectivityLog.sendGapMonitor.blocked("send error", detail: error.localizedDescription)
                }
                if case .success = result {
                    self?.healthMeter.sent(at: ProcessInfo.processInfo.systemUptime, port: port, generation: generation)
                }
                self?.uiPending.completed(port: port, generation: sendGeneration, result: result,
                                          processingStart: processingStart)
            }
#if DEBUG
            let onSendStarted: ((TimeInterval, Int) -> Void)? = { [weak self] queueWait, replaced in
                result.diagnosticSendStarted?(coordinates)
                self?.uiPending.udpQueueStart(queueWait, replaced: replaced, generation: sendGeneration)
            }
#else
            let onSendStarted: ((TimeInterval, Int) -> Void)? = result.diagnosticSendStarted.map { callback in
                { _, _ in callback(coordinates) }
            }
#endif
            delivery.route(text, port: port, settings: settings, onSendStarted: onSendStarted,
                           completion: completion, noRoute: { [weak self] in
                               self?.uiPending.noRoute(generation: sendGeneration)
                           })
#if DEBUG
            metrics.requestMs += (ProcessInfo.processInfo.systemUptime - sendRequestStart) * 1000
#endif
        }
        return metrics
    }

    /// PC へ届いている証拠の有無と通信ログを画面へ反映する（12Hz の UI 更新から呼ぶ。変化時だけ publish）。
    private func refreshConnectivityStatus() {
        let now = connectivityClock()
        let manual = hostSelection.source == .manual
        let missing = pcReplyWatch.update(sending: running, lanVerified: lanConfigured && lanProbe.isAlive,
                                          p2pConnected: !manual && p2pState.isConnected, now: now)
        if missing != pcReplyMissing {
            pcReplyMissing = missing
            ConnectivityLog.shared.record("状態", missing ? "PC から応答なし（Unity 応答なし・P2P 未接続）" : "PC への経路が回復")
        }
        let revision = ConnectivityLog.shared.revision
        if revision != connectivityLogRevision {
            connectivityLogRevision = revision
            connectivityLogTail = ConnectivityLog.shared.displayLines(last: Self.connectivityLogTailCount)
        }
    }

    // UI ロックを解放したスナップショットだけを MainActor で公開する。
    func publishPendingUI() {
#if DEBUG
        let uiStart = ProcessInfo.processInfo.systemUptime
        if debugAlternateFPS && fpsComparisonPolicy.shouldDisable(at: uiStart) { debugAlternateFPS = false }
#endif
        refreshConnectivityStatus()
        guard let snapshot = uiPending.takeSnapshot() else { return }
        uiSnapshotCountForTesting += 1
        assignIfChanged(\.processedFrameCount, snapshot.processed)
        assignIfChanged(\.rejectedFrameCallbackCount, snapshot.rejected)
        assignIfChanged(\.redDetectionCount, snapshot.redDetections)
        assignIfChanged(\.blueDetectionCount, snapshot.blueDetections)
        assignIfChanged(\.redAttemptCount, snapshot.redAttempts)
        assignIfChanged(\.blueAttemptCount, snapshot.blueAttempts)
        assignIfChanged(\.redCompletedCount, snapshot.redCompleted)
        assignIfChanged(\.blueCompletedCount, snapshot.blueCompleted)
        assignIfChanged(\.redErrorCount, snapshot.redErrors)
        assignIfChanged(\.blueErrorCount, snapshot.blueErrors)
        p2pNoRouteCount = snapshot.noRoute
        if running, snapshot.processed > 0 {
            assignEndpointsIfChanged(\.redEndpoints, snapshot.redEndpoints)
            assignEndpointsIfChanged(\.blueEndpoints, snapshot.blueEndpoints)
            if sourceDimensions.width != snapshot.dimensions.width || sourceDimensions.height != snapshot.dimensions.height {
                sourceDimensions = snapshot.dimensions
            }
            assignIfChanged(\.lastSentEpoch, snapshot.lastEpoch)
            assignIfChanged(\.fps, snapshot.fps)
        }
        assignIfChanged(\.lastLocalSendMs, snapshot.lastLocalSendMs)
        if running {
            sendErrorMessages = [:]
            for port in snapshot.sendErrors.keys.sorted() {
                if let message = snapshot.sendErrors[port] {
                    sendErrorMessages[port] = "UDP送信失敗 (\(host):\(port)): \(message)"
                }
            }
            if let first = snapshot.firstCameraFrame { receiveCameraFrame(at: first) }
            if let last = snapshot.lastCameraFrame, last != snapshot.firstCameraFrame { receiveCameraFrame(at: last) }
        }
        recomputeErrorMessage()
#if DEBUG
        uiPending.recordUIWork((ProcessInfo.processInfo.systemUptime - uiStart) * 1000)
        publishPerformanceIfNeeded()
#endif
    }

    private func assignIfChanged<Value: Equatable>(_ keyPath: ReferenceWritableKeyPath<CameraViewModel, Value>, _ value: Value) {
        if self[keyPath: keyPath] != value { self[keyPath: keyPath] = value }
    }

    private func assignEndpointsIfChanged(_ keyPath: ReferenceWritableKeyPath<CameraViewModel, (PixelPoint, PixelPoint)?>,
                                          _ value: (PixelPoint, PixelPoint)?) {
        let previous = self[keyPath: keyPath]
        if previous?.0 != value?.0 || previous?.1 != value?.1 { self[keyPath: keyPath] = value }
    }

#if DEBUG
    private func resetDebugPerformance() {
        uiPending.resetPerformance()
        debugPerformanceRows = DebugPerformanceRow.placeholders
        debugFrameIntervalStatistics = nil
        lastPerformancePublish = 0
        lastCameraSampleTime = 0
        debugPerformancePublishCountForTesting = 0
    }

    func recordDebugPerformanceForTesting(name: String, value: Double) {
        uiPending.recordDebugPerformanceForTesting(name: name, value: value)
        publishPerformanceIfNeeded()
    }

    private func publishPerformanceIfNeeded() {
        let now = ProcessInfo.processInfo.systemUptime
        guard now - lastPerformancePublish >= 0.2 else { return }
        lastPerformancePublish = now
        debugPerformancePublishCountForTesting += 1
        if let activeCamera { updateCameraConfiguration(activeCamera) }
        let snapshot = uiPending.performanceSnapshot()
        assignIfChanged(\.debugFrameIntervalStatistics, snapshot.intervals)
        assignIfChanged(\.debugPerformanceRows, DebugPerformanceRow.rows(from: snapshot.metrics))
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
            format: "#\(camera.formats.firstIndex(of: format).map { String($0) } ?? "?") \(size.width)×\(size.height) / FOV \(format.videoFieldOfView) / binned \(format.isVideoBinned)",
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
            hdr: "GDC \(camera.isGeometricDistortionCorrectionEnabled ? "On" : "Off"), wide auto \(session.automaticallyConfiguresCaptureDeviceForWideColor ? "On" : "Off"), format \(format.isVideoHDRSupported ? "supported" : "unsupported"), active \(camera.isVideoHDREnabled ? "On" : "Off"), auto \(camera.automaticallyAdjustsVideoHDREnabled ? "On" : "Off")",
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
            let battery = deviceBatterySnapshot()
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
                activeMaxFPS: minDuration > 0 && minDuration.isFinite ? 1 / minDuration : nil,
                thermalState: DeviceThermalLevel(state: ProcessInfo.processInfo.thermalState).metadataValue,
                batteryLevel: battery.level,
                batteryState: battery.state
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

    // MARK: Transport (LAN 確認済みを優先、P2P へ退避)

    private func configureLAN(host: String, generation: Int) {
        lanConfigured = false
        ConnectivityLog.shared.record("LAN", "宛先 \(host)")
        lanProbe.start(host: host)
        sender.configure(host: host) { [weak self] states, errors, _ in
            Task { @MainActor in
                self?.applySenderUpdate(states: states, errors: errors, generation: generation)
            }
        }
        lanConfigured = true
    }

    private func startP2P() {
        guard hostSelection.source != .manual else { return }
        let requestedStation = station
        p2pSender.start(station: station, onState: { [weak self] state in
            Task { @MainActor in
                guard let self, self.p2pEnabled, self.hostSelection.source != .manual,
                      self.station == requestedStation else { return }
                self.p2pState = state
                // LAN 待機座標の破棄は、検出キューが実際に P2P を選んだときだけ行う。
            }
        }, onStats: { [weak self] summary in
            Task { @MainActor in
                guard let self, self.p2pEnabled, self.hostSelection.source != .manual,
                      self.station == requestedStation else { return }
                self.p2pRoundTrip = summary
            }
        })
    }

    // MARK: Camera exposure experiment

    /// Whether the exposure experiment picker may change now (one setting per
    /// Debug Recording session).
    var cameraExposureExperimentEditable: Bool {
        !debugRecordingActive && !debugRecordingFinalizing
    }

    func setCameraExposureExperiment(_ setting: CameraExposureExperiment) {
        guard setting != cameraExposureExperiment, cameraExposureExperimentEditable else { return }
        cameraExposureExperiment = setting
        if NSClassFromString("XCTestCase") == nil { setting.store(in: .standard) }
        if running, let camera = exposureExperimentCamera {
            applyCameraExposureExperiment(to: camera)
        } else if setting == .auto && !exposureExperimentCapActive {
            cameraExposureExperimentState = .initial
        } else {
            cameraExposureExperimentState = CameraExposureExperimentState(setting: setting, status: .pending)
        }
        print("[Exposure] experiment=\(setting.rawValue) by user")
    }

    /// Applies the current experiment. Auto with no cap of ours on the device
    /// returns without touching the device (the pre-experiment behaviour).
    /// A cap this app set is always reset to the device default first, so the
    /// default is re-read for the current format before a new cap is planned.
    private func applyCameraExposureExperiment(to camera: AVCaptureDevice) {
        if exposureExperimentCamera !== camera {
            exposureExperimentCapActive = false
            exposureExperimentDefaultMaximum = .invalid
        }
        exposureExperimentCamera = camera
        let setting = cameraExposureExperiment
        guard setting != .auto || exposureExperimentCapActive else {
            cameraExposureExperimentState = .initial
            return
        }
        do {
            try camera.lockForConfiguration()
            defer { camera.unlockForConfiguration() }
            let wasCapped = exposureExperimentCapActive
            if wasCapped || setting != .auto {
                // kCMTimeInvalid restores the device default for the active format. Also done
                // before every capped setting: a cap left on the device (earlier run, before a
                // reconfiguration) was read as the "default" on 2026-10-06 and the experiment
                // skipped itself as notNeeded while the camera then ran at 1/30 s.
                camera.activeMaxExposureDuration = .invalid
                exposureExperimentCapActive = false
            }
            exposureExperimentDefaultMaximum = camera.activeMaxExposureDuration
            let format = camera.activeFormat
            let autoExposure = camera.exposureMode == .continuousAutoExposure
                || camera.exposureMode == .autoExpose
            var (action, state) = CameraExposureExperimentPlanner.plan(
                setting: setting, capActive: false,
                formatMinimum: format.minExposureDuration, formatMaximum: format.maxExposureDuration,
                defaultMaximum: exposureExperimentDefaultMaximum, autoExposureActive: autoExposure)
            if case .setMaximum(let duration) = action {
                camera.activeMaxExposureDuration = duration
                exposureExperimentCapActive = true
            }
            if setting == .auto && wasCapped { state.status = .autoRestored }
            state.observedMaxExposureSeconds = CameraExposureExperimentPlanner.seconds(
                camera.activeMaxExposureDuration)
            cameraExposureExperimentState = state
        } catch {
            cameraExposureExperimentState = CameraExposureExperimentState(
                setting: setting, status: .failed,
                requestedMaxExposureSeconds: setting.requestedMaxExposureDuration
                    .flatMap(CameraExposureExperimentPlanner.seconds),
                detail: error.localizedDescription)
        }
        let state = cameraExposureExperimentState
        print("[Exposure] setting=\(setting.rawValue) status=\(state.status.rawValue) "
              + "applied=\(state.appliedMaxExposureSeconds.map { String(format: "%.5f", $0) } ?? "-")s "
              + "default=\(state.defaultMaxExposureSeconds.map { String(format: "%.5f", $0) } ?? "-")s "
              + "observed=\(state.observedMaxExposureSeconds.map { String(format: "%.5f", $0) } ?? "-")s"
              + (state.detail.map { " detail=\($0)" } ?? ""))
    }

    /// The experiment state for Debug Recording metadata, with the device's
    /// current activeMaxExposureDuration read back (read only).
    private func cameraExposureExperimentSnapshot() -> CameraExposureExperimentState {
        // A camera reconfiguration can reset activeMaxExposureDuration after the cap was
        // applied; re-apply when the device no longer honours the requested cap.
        if let camera = exposureExperimentCamera,
           let requested = cameraExposureExperiment.requestedMaxExposureDuration,
           let observed = CameraExposureExperimentPlanner.seconds(camera.activeMaxExposureDuration),
           let wanted = CameraExposureExperimentPlanner.seconds(requested),
           observed > wanted * 1.02 {
            print("[Exposure] cap not in effect at recording start (\(observed)s > \(wanted)s); re-applying")
            applyCameraExposureExperiment(to: camera)
        }
        var state = cameraExposureExperimentState
        if let camera = exposureExperimentCamera {
            state.observedMaxExposureSeconds = CameraExposureExperimentPlanner.seconds(
                camera.activeMaxExposureDuration)
        }
        return state
    }

    func setP2PEnabled(_ enabled: Bool) {
        guard enabled != p2pEnabled else { return }
        p2pEnabled = enabled
        UserDefaults.standard.set(enabled, forKey: Self.p2pEnabledKey)
        if enabled {
            startP2P()
        } else {
            p2pSender.stop()
            p2pState = .disabled
            p2pRoundTrip = nil
        }
#if DEBUG
        print("[P2P] \(enabled ? "enabled" : "disabled") by user")
#endif
    }

    /// The path coordinates take right now, for the UI.
    var transportLabel: String {
        if hostSelection.source != .manual && p2pEnabled && p2pState.isConnected && !(lanConfigured && lanProbe.isAlive) {
            return p2pState.label
        }
        guard running else { return hostSelection.source == .manual ? "Manual IP" : p2pEnabled ? p2pState.label : "停止中" }
        if lanConfigured {
            let portStates = [senderStates[5005], senderStates[5006]].compactMap { $0 }
            let lan = hostSelection.source == .manual ? "Manual IP" : "LAN Connected"
            if portStates.count == 2 && portStates.allSatisfy({ $0.hasPrefix("ready") }) { return lan }
            if portStates.contains("failed") { return "Failed" }
            return "Reconnecting"
        }
        return p2pEnabled ? p2pState.label : "Failed (送信先なし)"
    }

    var p2pNoRouteCountForTesting: Int { p2pNoRouteCount }
    var lanConfiguredForTesting: Bool { lanConfigured }
    var lifecycleGenerationForTesting: Int { lifecycleGeneration }
    var lanProbeHostForTesting: String { lanProbe.hostForTesting }
    func recordLANReplyForTesting(_ text: String = LANLivenessProbe.replyPrefix) {
        lanProbe.recordReplyForTesting(text)
    }

    /// UI タイマーを待たず、LAN 応答の有効期限と警告を同じ注入時計で評価する。
    func refreshConnectivityStatusForTesting() { refreshConnectivityStatus() }

    private func applySenderUpdate(states: [Int: String], errors: [Int: String], generation: Int) {
        guard running, lifecycleGeneration == generation else {
            rejectedSenderUpdateCount += 1
            return
        }
        acceptedSenderUpdateCount += 1
        acceptedSenderUpdateGeneration = generation
        assignIfChanged(\.senderStates, states)
        assignIfChanged(\.senderErrors, errors)
        let hasConnectionIssue = states.values.contains { $0 == "waiting" || $0 == "failed" }
        connectionErrorMessage = hasConnectionIssue ? states.keys.sorted().compactMap { errors[$0] }.first(where: { !$0.isEmpty }) : nil
        recomputeErrorMessage()
    }

    private func recomputeErrorMessage() {
        assignIfChanged(\.errorMessage, cameraErrorMessage ?? connectionErrorMessage ?? sendErrorMessages.values.first)
        if cameraErrorMessage != nil {
            assignIfChanged(\.status, "カメラエラー")
        } else if connectionErrorMessage != nil {
            assignIfChanged(\.status, "接続エラー")
        } else if !sendErrorMessages.isEmpty {
            assignIfChanged(\.status, "送信エラー")
        } else if running {
            assignIfChanged(\.status, "送信中")
        } else {
            assignIfChanged(\.status, "停止中")
        }
    }

    private func fail(_ message: String, cameraFailure: Bool = true) {
        cameraErrorMessage = message
        if cameraFailure {
            cameraRecoveryInProgress = false
            cameraLifecycle.fail(message)
            publishCameraLifecycle(at: ProcessInfo.processInfo.systemUptime)
        }
        recomputeErrorMessage()
    }

    private func resetStartState() {
        uiPending.reset()
        uiSnapshotCountForTesting = 0
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
        // An empty host mirrors a P2P-only start (no LAN destination yet).
        if host.isEmpty { lanConfigured = false; lanProbe.stop() } else { configureLAN(host: host, generation: currentGeneration) }
        resetProcessor()
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

    func startDebugRecording(guidedScript: GuidedRecordingScript? = nil) {
        guard DebugRecordingLifecyclePolicy.mayStart(
            enabled: debugRecordingEnabled, cameraRunning: running,
            active: debugRecordingActive, finalizing: debugRecordingFinalizing
        ) else { return }
        // 発熱による再起動を、recorder 作成前の active 予約で録画終了と誤認させない。
        publishDeviceHealth(at: ProcessInfo.processInfo.systemUptime, force: true)
        guard running else { teardownGuidedRecording(stopAudioImmediately: true); return }
        lastDebugRecordingResult = nil
        debugManualLosslessCaptureCount = 0
        // Reserve the state immediately so a rapid app Stop queues recorder
        // finalization after recorder creation instead of leaving it orphaned.
        debugRecordingActive = true
        DebugBundleTransfer.shared.setRecordingActive(true)
        debugRecordingStatus = "録画を開始しています…"
        processor.updateDebugCameraDeviceState(nil)
        debugSegmentLabel = .unlabeled
        processor.setDebugGuidedPhase(nil)
        processor.startDebugRecording(diagnosticColors: debugDiagnosticColors,
                                      cameraExposureExperiment: cameraExposureExperimentSnapshot(),
                                      guidedScript: guidedScript) { [weak self] result in
            Task { @MainActor in
                guard let self else { return }
                switch result {
                case .success(let sessionID):
                    guard self.debugRecordingActive, !self.debugRecordingFinalizing else { return }
                    self.debugRecordingStatus = "録画中: \(sessionID)"
                    self.refreshDebugRecordingSessions()
                    self.scheduleDebugRecordingMaximumDurationStop()
                    if guidedScript != nil { self.beginGuidedSchedule() }
                case .failure(let error):
                    guard self.debugRecordingActive, !self.debugRecordingFinalizing else { return }
                    self.debugRecordingActive = false
                    DebugBundleTransfer.shared.setRecordingActive(false)
                    self.debugRecordingStatus = "録画開始失敗: \(error.localizedDescription)"
                    self.teardownGuidedRecording(stopAudioImmediately: true)
                }
            }
        }
    }

    func stopDebugRecording(reason: DebugRecordingFinishReason = .user) {
        guard DebugRecordingLifecyclePolicy.mayStop(active: debugRecordingActive, finalizing: debugRecordingFinalizing) else { return }
        // Any other stop (background, interruption, limit) ends the guide too;
        // the metadata outcome then stays "incomplete".
        if guidedRecordingRunning { teardownGuidedRecording(stopAudioImmediately: true) }
        debugRecordingActive = false
        DebugBundleTransfer.shared.setRecordingActive(false)
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

    private func refreshDebugTransferStatus() {
        let outbox = DebugBundleTransfer.shared.outbox
        debugTransferEntry = outbox.latest
        debugTransferPendingCount = outbox.pendingCount
    }

    func sendPendingDebugRecordings() {
        guard !debugRecordingActive else { return }
        DebugBundleTransfer.shared.retryPending(immediately: true)
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
        if guidedRecordingRunning { teardownGuidedRecording(stopAudioImmediately: true) }
        endRecordingBackgroundTask()
        debugRecordingMaximumDurationTask?.cancel()
        debugRecordingActive = false
        debugRecordingFinalizing = false
        DebugBundleTransfer.shared.setRecordingActive(false)
        manualLosslessCapturePending = false
        switch result {
        case .success(let recording):
            lastDebugRecordingResult = recording
            debugRecordingStatus = String(
                format: "端末に録画を保存: %d frames / %@ / %.0f秒（Mac受領は下の転送状態を確認）",
                recording.recordedFrameCount,
                formattedBytes(recording.diskUsageBytes),
                recording.durationSeconds
            )
        case .failure(let error):
            debugRecordingStatus = "録画処理失敗: \(error.localizedDescription)"
        }
        refreshDebugRecordingSessions()
        refreshDebugTransferStatus()
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

// MARK: - Guided recording (ガイド付き録画)

extension CameraViewModel {
    /// Starts a Debug Recording that follows `guidedRecordingScript`: labels,
    /// spoken cues, countdowns and swing lossless captures run by themselves,
    /// and the recording stops (and auto-transfers as usual) at the end.
    func startGuidedRecording() { startGuidedRecording(script: guidedRecordingScript) }

    /// 振りだけ録画（約50秒）。手順は同じ仕組みで進み、最後に自動で止まる。
    func startSwingOnlyRecording() { startGuidedRecording(script: swingOnlyRecordingScript) }

    func startGuidedRecording(script: GuidedRecordingScript) {
        guard !guidedRecordingRunning, script.validationErrors.isEmpty,
              DebugRecordingLifecyclePolicy.mayStart(
                enabled: debugRecordingEnabled, cameraRunning: running,
                active: debugRecordingActive, finalizing: debugRecordingFinalizing) else { return }
        guidedRecordingRunning = true
        guidedRecordingStatus = nil
        guidedCues?.cancel()
        let cues = makeGuidedCuePlayer()
        guidedCues = cues
        cues.begin()
        activeGuidedScript = script
        startDebugRecording(guidedScript: script)
    }

    /// Stops the guide and the recording at once; frames so far are kept and
    /// the metadata outcome is "cancelled".
    func cancelGuidedRecording() {
        guard guidedRecordingRunning else { return }
        guidedScheduler?.cancel()
        finishGuidedRecording(.cancelled)
    }

    private func beginGuidedSchedule() {
        guard guidedRecordingRunning, guidedScheduler == nil else { return }
        guidedScheduler = GuidedRecordingScheduler(script: activeGuidedScript)
        guidedStartTime = ProcessInfo.processInfo.systemUptime
        tickGuidedRecording(at: guidedStartTime)
        guidedTask = Task { @MainActor [weak self] in
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 100_000_000)
                guard let self, !Task.isCancelled else { return }
                self.tickGuidedRecording(at: ProcessInfo.processInfo.systemUptime)
            }
        }
    }

    private func tickGuidedRecording(at now: TimeInterval) {
        guard var scheduler = guidedScheduler else { return }
        guard debugRecordingActive, !debugRecordingFinalizing else {
            teardownGuidedRecording(stopAudioImmediately: true)
            return
        }
        let elapsed = now - guidedStartTime
        let actions = scheduler.advance(to: elapsed)
        guidedScheduler = scheduler
        for action in actions where guidedRecordingRunning {
            applyGuided(action)
        }
        guard guidedRecordingRunning else { return }
        let status = scheduler.status(at: elapsed)
        if status != guidedRecordingStatus { guidedRecordingStatus = status }
    }

    private func applyGuided(_ action: GuidedRecordingAction) {
        switch action {
        case .setPhase(let phase):
            processor.setDebugGuidedPhase(phase)
        case .setLabel(let label):
            // The same path as the manual 区間ラベル picker (didSet → processor).
            if debugSegmentLabel != label { debugSegmentLabel = label }
        case .speak(let text):
            guidedCues?.speak(text)
        case .countdown(let value):
            guidedCues?.countdown(value)
        case .captureLossless:
            // The existing one-shot capture API; skipped while one is pending
            // or when the guided limit is used up (the recorder enforces the
            // limit and the memory/disk caps again).
            if !manualLosslessCapturePending,
               debugManualLosslessCaptureCount < DebugRecordingLimits.maximumGuidedLosslessCaptures {
                captureNextLosslessFrame()
            }
        case .finish:
            finishGuidedRecording(.completed)
        }
    }

    private func finishGuidedRecording(_ outcome: DebugGuidedOutcome) {
        // Queued on the processing queue ahead of the Stop below.
        processor.setDebugGuidedOutcome(outcome)
        teardownGuidedRecording(stopAudioImmediately: outcome != .completed)
        stopDebugRecording(reason: .user)
    }

    private func teardownGuidedRecording(stopAudioImmediately: Bool) {
        guidedTask?.cancel()
        guidedTask = nil
        guidedScheduler = nil
        guidedRecordingStatus = nil
        guidedRecordingRunning = false
        processor.setDebugGuidedPhase(nil)
        if stopAudioImmediately { guidedCues?.cancel() } else { guidedCues?.end() }
    }
}

extension CameraViewModel: AVCaptureVideoDataOutputSampleBufferDelegate {
    nonisolated func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) {
        let frameTime = ProcessInfo.processInfo.systemUptime
        ConnectivityLog.sendGapMonitor.cameraFrame()
        healthMeter.cameraFrame(at: frameTime)
        processor.submit(sampleBuffer)
        uiPending.cameraFrame(at: frameTime)
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

struct PerformanceMetric {
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

struct DebugPerformanceRow: Identifiable, Equatable {
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
        ("Camera", "Capture mode", "Capture mode FPS", "fps"),
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
    var removed = false
}

/// Discovery runs on the main run loop, outside the capture and UDP queues.
/// The service advertises red UDP 5005; blue remains the existing 5006 on the
/// resolved host, preserving the payload and transport contract.
private final class BonjourDiscovery: NSObject, NetServiceBrowserDelegate, NetServiceDelegate {
    var station = ""
    var onUpdate: ((BonjourDiscoveryUpdate) -> Void)?
    private let browser = NetServiceBrowser()
    private var resolving: Set<NetService> = []
    private var searchTimeout: DispatchWorkItem?
    private var isSearching = false

    private func report(_ status: String, name: String = "", ip: String = "", removed: Bool = false) {
#if DEBUG
        print("[Bonjour] \(status) main=\(Thread.isMainThread)")
#endif
        onUpdate?(BonjourDiscoveryUpdate(status: status, name: name, ip: ip, removed: removed))
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
            self.report(self.station.isEmpty ? "No service found; Manual IP required (探索継続中)"
                : "台\(self.station)の PC が見つかりません（探索継続中）")
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
        guard PhoneSaberStation.matches(service: service.name, station: station) else { return }
        resolving.remove(service)
        service.stop()
        report("PC を再接続中…", name: service.name, removed: true)
    }

    func netServiceWillResolve(_ sender: NetService) {
        report("Resolving: \(sender.name)", name: sender.name)
    }

    func netServiceBrowser(_ browser: NetServiceBrowser, didFind service: NetService,
                           moreComing: Bool) {
        guard PhoneSaberStation.matches(service: service.name, station: station),
              !resolving.contains(service) else { return }
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
        guard resolving.contains(sender), PhoneSaberStation.matches(service: sender.name, station: station) else { return }
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
