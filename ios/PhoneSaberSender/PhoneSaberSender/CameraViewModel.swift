import AVFoundation
import Foundation
import Network
import SwiftUI
import Darwin

@MainActor
final class CameraViewModel: NSObject, ObservableObject {
    let session = AVCaptureSession()
    nonisolated let processor: FrameProcessor
    private let output = AVCaptureVideoDataOutput()
    private let captureQueue = DispatchQueue(label: "PhoneSaberSender.capture", qos: .userInteractive)
    private let sender: UDPSender
    private let pathMonitor = NWPathMonitor()
    private let bonjourDiscovery = BonjourDiscovery()
    @Published var running = false
    @Published var redEndpoints: (PixelPoint, PixelPoint)?
    @Published var blueEndpoints: (PixelPoint, PixelPoint)?
    @Published var fps = 0.0
    @Published var status = "停止中"
    @Published var errorMessage: String?
    @Published private(set) var pathStatus = "判定中"
    @Published private(set) var pathInterface = ""
    @Published var host = ""
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
#if DEBUG
    @Published private(set) var debugPerformanceRows: [String] = []
    private var performanceMetrics: [String: PerformanceMetric] = [:]
    private var lastPerformancePublish = 0.0
#endif
    private var frameCount = 0
    private var fpsStart = CACurrentMediaTime()
    private var lifecycleGeneration = 0
    private var cameraErrorMessage: String?
    private var connectionErrorMessage: String?
    private var sendErrorMessages: [Int: String] = [:]
    private let authorizationStatus: () -> AVAuthorizationStatus
    private let requestAccess: (@escaping (Bool) -> Void) -> Void

    init(
        processor: FrameProcessor = FrameProcessor(),
        sender: UDPSender = UDPSender(),
        authorizationStatus: @escaping () -> AVAuthorizationStatus = { AVCaptureDevice.authorizationStatus(for: .video) },
        requestAccess: @escaping (@escaping (Bool) -> Void) -> Void = { completion in AVCaptureDevice.requestAccess(for: .video, completionHandler: completion) }
    ) {
        self.processor = processor
        self.sender = sender
        self.authorizationStatus = authorizationStatus
        self.requestAccess = requestAccess
        super.init()
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
                guard let self else { return }
                self.networkDiscoveryStatus = update.status
                self.discoveredMacName = update.name
                self.discoveredMacIP = update.ip
                if !update.ip.isEmpty && self.host.isEmpty {
                    self.host = update.ip
                    self.connectionMode = "Auto (Bonjour)"
                }
            }
        }
        bonjourDiscovery.start()
        processor.onResult = { [weak self] results, width, height, processingStart, generation in
            Task { @MainActor in self?.handle(results, width: width, height: height, processingStart: processingStart, generation: generation) }
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
#if DEBUG
        processor.onPerformance = { [weak self] sample in
            Task { @MainActor in self?.recordPerformance(sample) }
        }
#endif
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

    func startMeasurement() {
        measurementMode = true
        start()
    }

    func stop() {
        lifecycleGeneration += 1
        session.stopRunning(); sender.stop(); _ = processor.reset(); running = false; activeDestination = "未設定"; status = "停止中"; redEndpoints = nil; blueEndpoints = nil; senderStates = [:]; senderErrors = [:]; cameraErrorMessage = nil; connectionErrorMessage = nil; sendErrorMessages = [:]; recomputeErrorMessage()
    }

    private func configureAndStart() {
        let configuredHost = host.trimmingCharacters(in: .whitespacesAndNewlines)
        cameraErrorMessage = nil
        connectionErrorMessage = nil
        sendErrorMessages = [:]
        recomputeErrorMessage()
        guard !configuredHost.isEmpty else {
            fail("Macを検索中です。見つからない場合は手動IPを入力してください")
            return
        }
        let configuredThreshold = ColorThreshold(brightness: UInt8(clamping: threshold), dominance: UInt8(clamping: dominance))
        session.beginConfiguration()
        session.inputs.forEach { session.removeInput($0) }
        session.outputs.forEach { session.removeOutput($0) }
        guard let camera = AVCaptureDevice.default(.builtInWideAngleCamera, for: .video, position: .back), let input = try? AVCaptureDeviceInput(device: camera), session.canAddInput(input), session.canAddOutput(output) else { session.commitConfiguration(); fail("カメラを初期化できません"); return }
        session.addInput(input)
        let preferredFormats = camera.formats.filter { format in
            let size = CMVideoFormatDescriptionGetDimensions(format.formatDescription)
            return size.width <= 640 && size.height <= 480
        }
        let format = preferredFormats.max { a, b in
            let left = CMVideoFormatDescriptionGetDimensions(a.formatDescription)
            let right = CMVideoFormatDescriptionGetDimensions(b.formatDescription)
            return Int64(left.width) * Int64(left.height) < Int64(right.width) * Int64(right.height)
        } ?? camera.formats.min { a, b in
            let left = CMVideoFormatDescriptionGetDimensions(a.formatDescription)
            let right = CMVideoFormatDescriptionGetDimensions(b.formatDescription)
            return Int64(left.width) * Int64(left.height) < Int64(right.width) * Int64(right.height)
        }
        if let format {
            do {
                try camera.lockForConfiguration()
                camera.activeFormat = format
                camera.unlockForConfiguration()
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
        }
        session.commitConfiguration()
        let currentGeneration = lifecycleGeneration
        running = true
        sender.configure(host: configuredHost) { [weak self] states, errors, lastError in
            Task { @MainActor in
                self?.applySenderUpdate(states: states, errors: errors, generation: currentGeneration)
            }
        }
        host = configuredHost
        connectionMode = configuredHost == discoveredMacIP && !discoveredMacIP.isEmpty
            ? "Auto (Bonjour)" : "Manual"
        activeDestination = "\(configuredHost):5005 / \(configuredHost):5006"
        resetStartState()
        processor.configureThresholds(red: configuredThreshold, blue: configuredThreshold)
        frameCount = 0; fpsStart = CACurrentMediaTime(); _ = processor.reset(); status = "送信中"; session.startRunning()
    }

    private func handle(_ results: [DetectedSaber], width: Int, height: Int, processingStart: TimeInterval, generation: Int) {
#if DEBUG
        let uiStart = ProcessInfo.processInfo.systemUptime
        var udpRequestMs = 0.0
#endif
        guard running, generation == processor.currentGeneration else {
            rejectedFrameCallbackCount += 1
            return
        }
        processedFrameCount += 1
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
                redEndpoints = result.endpoints; redDetectionCount += 1; port = 5005
                redSeen = true
            case .blue:
                blueEndpoints = result.endpoints; blueDetectionCount += 1; port = 5006
                blueSeen = true
            }
            if result.color == .red { redAttemptCount += 1 } else { blueAttemptCount += 1 }
            let sendGeneration = lifecycleGeneration
#if DEBUG
            let sendRequestStart = ProcessInfo.processInfo.systemUptime
#endif
            sender.send(text, to: port) { [weak self] result in
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
    private func recordPerformance(_ sample: FramePerformanceSample) {
        let profile = sample.detector
        addPerformance("Pixel buffer access", value: sample.pixelBufferAccessMs)
        addPerformance("BGRA + RGB→HSV + masks", value: profile.pixelScanHSVMaskMs)
        addPerformance("Close / open", value: profile.morphologyMs)
        addPerformance("Components + shape/brightness/contrast/PCA", value: profile.componentAndScoreMs)
        addPerformance("Bright-core proposals", value: profile.lineProposalMs)
        addPerformance("Proposal detailed score", value: profile.lineScoreMs)
        addPerformance("Final selection / scaling", value: profile.selectionMs)
        addPerformance("Detection total", value: profile.totalMs)
        publishPerformanceIfNeeded()
    }

    private func addPerformance(_ name: String, value: Double) {
        var metric = performanceMetrics[name, default: PerformanceMetric()]
        metric.latest = value
        metric.total += value
        metric.maximum = max(metric.maximum, value)
        metric.count += 1
        performanceMetrics[name] = metric
    }

    private func publishPerformanceIfNeeded() {
        let now = ProcessInfo.processInfo.systemUptime
        guard now - lastPerformancePublish >= 0.2 else { return }
        lastPerformancePublish = now
        let order = ["Pixel buffer access", "BGRA + RGB→HSV + masks", "Close / open",
                     "Components + shape/brightness/contrast/PCA", "Bright-core proposals",
                     "Proposal detailed score", "Final selection / scaling",
                     "Detection total", "UI / overlay state", "UDP request"]
        debugPerformanceRows = order.compactMap { name in
            guard let metric = performanceMetrics[name] else { return nil }
            return String(format: "%@: now %.2f / avg %.2f / max %.2f ms",
                          name, metric.latest, metric.total / Double(metric.count), metric.maximum)
        }
    }
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

    private func fail(_ message: String) { cameraErrorMessage = message; recomputeErrorMessage() }

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
    }

    // Test entry point: this uses the same FrameProcessor callback and UDP completion path as camera frames.
    func startForTesting(host: String = "127.0.0.1") {
        lifecycleGeneration += 1
        resetStartState()
        running = true
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
}

extension CameraViewModel: AVCaptureVideoDataOutputSampleBufferDelegate {
    nonisolated func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) { processor.submit(sampleBuffer) }
}

#if DEBUG
private struct PerformanceMetric {
    var latest = 0.0
    var total = 0.0
    var maximum = 0.0
    var count = 0
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
        let timeout = DispatchWorkItem { [weak self] in
            guard let self, self.resolving.isEmpty else { return }
            self.report("No service found; Manual IP required (探索継続中)")
        }
        searchTimeout = timeout
        DispatchQueue.main.asyncAfter(deadline: .now() + 10, execute: timeout)
    }

    func netServiceBrowserWillSearch(_ browser: NetServiceBrowser) {
        report("Browsing")
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
