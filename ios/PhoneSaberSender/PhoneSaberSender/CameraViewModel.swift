import AVFoundation
import Foundation
import Network
import SwiftUI

@MainActor
final class CameraViewModel: NSObject, ObservableObject {
    let session = AVCaptureSession()
    let processor: FrameProcessor
    private let output = AVCaptureVideoDataOutput()
    private let sender: UDPSender
    private let pathMonitor = NWPathMonitor()
    @Published var running = false
    @Published var redEndpoints: (PixelPoint, PixelPoint)?
    @Published var blueEndpoints: (PixelPoint, PixelPoint)?
    @Published var fps = 0.0
    @Published var status = "停止中"
    @Published var errorMessage: String?
    @Published private(set) var pathStatus = "判定中"
    @Published private(set) var pathInterface = ""
    @Published var host = ""
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
        processor.onResult = { [weak self] results, width, height, processingStart, generation in
            Task { @MainActor in self?.handle(results, width: width, height: height, processingStart: processingStart, generation: generation) }
        }
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
            fail("送信先MacのIPアドレスまたはホスト名を入力してください")
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
        output.setSampleBufferDelegate(self, queue: processor.queue)
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
        activeDestination = "\(configuredHost):5005 / \(configuredHost):5006"
        resetStartState()
        processor.configureThresholds(red: configuredThreshold, blue: configuredThreshold)
        frameCount = 0; fpsStart = CACurrentMediaTime(); _ = processor.reset(); status = "送信中"; session.startRunning()
    }

    private func handle(_ results: [DetectedSaber], width: Int, height: Int, processingStart: TimeInterval, generation: Int) {
        guard running, generation == processor.currentGeneration else { return }
        processedFrameCount += 1
        let currentGeneration = lifecycleGeneration
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
        }
        if !redSeen { redEndpoints = nil }
        if !blueSeen { blueEndpoints = nil }
        frameCount += 1
        let elapsed = CACurrentMediaTime() - fpsStart
        if elapsed >= 1 { fps = Double(frameCount) / elapsed; frameCount = 0; fpsStart = CACurrentMediaTime() }
        sender.snapshot { [weak self] snapshot in
            Task { @MainActor in
                guard let self, self.running, self.lifecycleGeneration == currentGeneration else { return }
                self.applySenderUpdate(states: snapshot.states, errors: snapshot.errors, generation: currentGeneration)
            }
        }
    }

    private func applySenderUpdate(states: [Int: String], errors: [Int: String], generation: Int) {
        guard running, lifecycleGeneration == generation else { return }
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
}

extension CameraViewModel: AVCaptureVideoDataOutputSampleBufferDelegate {
    nonisolated func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer, from connection: AVCaptureConnection) { processor.process(sampleBuffer) }
}
