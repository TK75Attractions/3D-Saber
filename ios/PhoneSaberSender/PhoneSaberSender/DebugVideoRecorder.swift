import AVFoundation
import CoreImage
import CoreVideo
import Foundation

struct DebugRecordingResult {
    let sessionID: String
    let rawVideoURL: URL
    let overlayVideoURL: URL
    let metadataURL: URL
    let forensicDirectoryURL: URL?
    let recordedFrameCount: Int
    let droppedFrameCount: Int
    let triageBundleURL: URL?
    let triageErrorMessage: String?
}

struct DebugRecordingPoint: Codable, Equatable {
    let x: Int
    let y: Int

    init(_ point: PixelPoint) {
        x = point.x
        y = point.y
    }
}

struct DebugRecordingEndpoints: Codable, Equatable {
    let first: DebugRecordingPoint
    let second: DebugRecordingPoint

    init(_ endpoints: (PixelPoint, PixelPoint)) {
        first = DebugRecordingPoint(endpoints.0)
        second = DebugRecordingPoint(endpoints.1)
    }
}

struct DebugRecordingScoreBreakdown: Codable, Equatable {
    let proposalPenalty: Double
    let radiance: Double
    let length: Double
    let aspect: Double
    let extent: Double
    let widthConsistency: Double
    let area: Double
    let peakBrightness: Double
    let meanBrightness: Double
    let highBrightnessRatio: Double
    let colorPurity: Double
    let localContrast: Double
    let emitterTexture: Double
    let clippedWhite: Double
    let longitudinalHighCoverage: Double
    let coreSupport: Double
    let longitudinalCoreCoverage: Double
    let total: Double

    init(_ value: SaberScoreBreakdown) {
        proposalPenalty = value.proposalPenalty
        radiance = value.radiance
        length = value.length
        aspect = value.aspect
        extent = value.extent
        widthConsistency = value.widthConsistency
        area = value.area
        peakBrightness = value.peakBrightness
        meanBrightness = value.meanBrightness
        highBrightnessRatio = value.highBrightnessRatio
        colorPurity = value.colorPurity
        localContrast = value.localContrast
        emitterTexture = value.emitterTexture
        clippedWhite = value.clippedWhite
        longitudinalHighCoverage = value.longitudinalHighCoverage
        coreSupport = value.coreSupport
        longitudinalCoreCoverage = value.longitudinalCoreCoverage
        total = value.total
    }
}

struct DebugRecordingCandidate: Codable, Equatable {
    let index: Int
    let selected: Bool
    let sourceType: String
    let eligible: Bool
    let finalScore: Double
    let scoreBreakdown: DebugRecordingScoreBreakdown
    let rawPCAEndpoints: DebugRecordingEndpoints
    let rawPCASpan: Double
    let robustMainIntervalEndpoints: DebugRecordingEndpoints?
    let robustMainIntervalLength: Double
    let finalOutputEndpoints: DebugRecordingEndpoints
    let continuity: Double
    let density: Double
    let maxGap: Int
    let componentArea: Int
    let pointCount: Int
    let usedPointLEDFallback: Bool

    init(index: Int, candidate: SaberCandidate, selectedIndex: Int?) {
        self.index = index
        selected = index == selectedIndex
        sourceType = candidate.source
        eligible = candidate.isEmitterEligible
        finalScore = candidate.score
        scoreBreakdown = DebugRecordingScoreBreakdown(candidate.scoreBreakdown)
        rawPCAEndpoints = DebugRecordingEndpoints(candidate.comparisonEndpoints)
        rawPCASpan = candidate.rawPCASpan
        robustMainIntervalEndpoints = candidate.robustMainIntervalEndpoints.map(DebugRecordingEndpoints.init)
        robustMainIntervalLength = candidate.robustMainIntervalLength
        finalOutputEndpoints = DebugRecordingEndpoints(candidate.endpoints)
        continuity = candidate.longitudinalContinuity
        density = candidate.axialDensity
        maxGap = candidate.largestLongitudinalGap
        componentArea = candidate.componentArea
        pointCount = candidate.pointCount
        usedPointLEDFallback = candidate.usedPointLEDFallback
    }
}

struct DebugRecordingColorCandidates: Codable, Equatable {
    static let savedCandidateLimit = 3

    let totalCandidateCount: Int
    let eligibleCandidateCount: Int
    let maskPixelCount: Int
    let morphologyPixelCount: Int
    let connectedComponentCount: Int
    let selectedCandidateIndex: Int?
    let selectedCandidateType: String?
    let selectedCandidateFinalScore: Double?
    let selectedCandidateScoreBreakdown: DebugRecordingScoreBreakdown?
    let selectedCandidate: DebugRecordingCandidate?
    let topCandidates: [DebugRecordingCandidate]

    init(_ candidates: [SaberCandidate], pipeline: SaberColorPipelineDiagnostics?) {
        totalCandidateCount = candidates.count
        eligibleCandidateCount = candidates.filter(\.isEmitterEligible).count
        maskPixelCount = pipeline?.maskPixelCount ?? 0
        morphologyPixelCount = pipeline?.morphologyPixelCount ?? 0
        connectedComponentCount = pipeline?.connectedComponentCount ?? 0
        let selectedIndex = candidates.firstIndex(where: \.isEmitterEligible)
        selectedCandidateIndex = selectedIndex
        selectedCandidateType = selectedIndex.map { candidates[$0].source }
        selectedCandidateFinalScore = selectedIndex.map { candidates[$0].score }
        selectedCandidateScoreBreakdown = selectedIndex.map {
            DebugRecordingScoreBreakdown(candidates[$0].scoreBreakdown)
        }
        selectedCandidate = selectedIndex.map {
            DebugRecordingCandidate(index: $0, candidate: candidates[$0], selectedIndex: $0)
        }
        topCandidates = candidates.prefix(Self.savedCandidateLimit).enumerated().map {
            DebugRecordingCandidate(index: $0.offset, candidate: $0.element,
                                    selectedIndex: selectedIndex)
        }
    }
}

struct DebugRecordingCandidateDiagnostics: Codable, Equatable {
    let red: DebugRecordingColorCandidates
    let blue: DebugRecordingColorCandidates

    init(analysis: SaberFrameAnalysis) {
        red = DebugRecordingColorCandidates(
            analysis.candidates[.red] ?? [], pipeline: analysis.pipelineDiagnostics?[.red]
        )
        blue = DebugRecordingColorCandidates(
            analysis.candidates[.blue] ?? [], pipeline: analysis.pipelineDiagnostics?[.blue]
        )
    }
}

struct DebugRecordingDetection: Codable, Equatable {
    let detected: Bool
    let predicted: Bool
    let x1: Int?
    let y1: Int?
    let x2: Int?
    let y2: Int?

    static let notDetected = DebugRecordingDetection(
        detected: false, predicted: false, x1: nil, y1: nil, x2: nil, y2: nil
    )

    init(endpoints: (PixelPoint, PixelPoint)?, predicted: Bool = false) {
        guard let endpoints else {
            self = .notDetected
            return
        }
        detected = true
        self.predicted = predicted
        x1 = endpoints.0.x
        y1 = endpoints.0.y
        x2 = endpoints.1.x
        y2 = endpoints.1.y
    }

    private init(detected: Bool, predicted: Bool,
                 x1: Int?, y1: Int?, x2: Int?, y2: Int?) {
        self.detected = detected
        self.predicted = predicted
        self.x1 = x1
        self.y1 = y1
        self.x2 = x2
        self.y2 = y2
    }

    var endpoints: (PixelPoint, PixelPoint)? {
        guard detected, let x1, let y1, let x2, let y2 else { return nil }
        return (PixelPoint(x: x1, y: y1), PixelPoint(x: x2, y: y2))
    }
}

struct DebugRecordingFrameMetadata: Codable, Equatable {
    let frameID: UInt64
    let presentationTimeSeconds: Double
    let red: DebugRecordingDetection
    let blue: DebugRecordingDetection
    let redDetectionSucceeded: Bool
    let blueDetectionSucceeded: Bool
    let candidateDiagnostics: DebugRecordingCandidateDiagnostics?
    var forensicCaptured: Bool
    var forensicFileName: String?
    var manualCaptured: Bool
    var manualFileName: String?
    var blueDropoutRole: String?
    var blueDropoutFileName: String?
}

struct DebugRecordingMetadata: Codable, Equatable {
    let sessionID: String
    let width: Int
    let height: Int
    let frames: [DebugRecordingFrameMetadata]
}

struct DebugForensicCapturePolicy {
    static let production = DebugForensicCapturePolicy(
        absoluteLengthThreshold: 260,
        relativeLengthThreshold: 180,
        growthRatio: 2.0,
        maximumFrames: 24
    )

    let absoluteLengthThreshold: Double
    let relativeLengthThreshold: Double
    let growthRatio: Double
    let maximumFrames: Int
}

private struct DebugForensicFrame {
    let fileName: String
    let width: Int
    let height: Int
    let bytes: Data
}

private struct DebugRetainedBlueFrame {
    let pixelBuffer: CVPixelBuffer
    let frameID: UInt64
    let metadataIndex: Int
    let width: Int
    let height: Int
}

enum DebugVideoRecorderError: LocalizedError {
    case alreadyStarted
    case cameraNotReady
    case noFrames
    case cannotCreateWriter(String)
    case appendFailed(String)
    case cannotReadRawVideo(String)
    case metadataFrameMismatch(expected: Int, actual: Int)

    var errorDescription: String? {
        switch self {
        case .alreadyStarted: return "録画はすでに開始されています"
        case .cameraNotReady: return "カメラフレームの準備完了後にもう一度Start Recordingを押してください"
        case .noFrames: return "録画されたフレームがありません"
        case .cannotCreateWriter(let message): return "raw動画を作成できません: \(message)"
        case .appendFailed(let message): return "raw動画への追加に失敗しました: \(message)"
        case .cannotReadRawVideo(let message): return "raw動画を読み込めません: \(message)"
        case .metadataFrameMismatch(let expected, let actual):
            return "動画と検出結果のフレーム数が一致しません (metadata \(expected), video \(actual))"
        }
    }
}

/// Debug-only-in-use recorder. It stores one raw H.264 stream during capture
/// and the final fresh endpoints paired with every frame that the writer
/// accepted. The overlay is deliberately rendered only after recording stops.
final class DebugVideoRecorder {
    let sessionID: String
    let rawVideoURL: URL
    let overlayVideoURL: URL
    let metadataURL: URL
    let forensicDirectoryURL: URL

    private var writer: AVAssetWriter?
    private var writerInput: AVAssetWriterInput?
    private var adaptor: AVAssetWriterInputPixelBufferAdaptor?
    private var firstPresentationTime: CMTime?
    private var lastPresentationTime: CMTime?
    private var dimensions: (width: Int, height: Int)?
    private var frames: [DebugRecordingFrameMetadata] = []
    private var forensicFrames: [DebugForensicFrame] = []
    private var previousLengths: [SaberColor: Double] = [:]
    private var manualCaptureCompletion: ((UInt64) -> Void)?
    private var lastBlueDetectedFrame: DebugRetainedBlueFrame?
    private var blueDropoutActive = false
    private let forensicPolicy: DebugForensicCapturePolicy
    private(set) var droppedFrameCount = 0
    private var isFinishing = false

    init(directory: URL, date: Date = Date(),
         forensicPolicy: DebugForensicCapturePolicy = .production) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = .current
        formatter.dateFormat = "yyyyMMdd_HHmmss_SSS"
        sessionID = "phonesaber_\(formatter.string(from: date))"
        rawVideoURL = directory.appendingPathComponent("\(sessionID)_raw.mp4")
        overlayVideoURL = directory.appendingPathComponent("\(sessionID)_overlay.mp4")
        metadataURL = directory.appendingPathComponent("\(sessionID)_metadata.json")
        forensicDirectoryURL = directory.appendingPathComponent("\(sessionID)_forensic", isDirectory: true)
        self.forensicPolicy = forensicPolicy
    }

    /// Prepares the encoder before the recording interval begins. This keeps
    /// one-time H.264 setup out of the first measured frame.
    func prepare(width: Int, height: Int) throws {
        if writer == nil { try configureWriter(width: width, height: height) }
    }

    /// Arms a one-shot capture for the next frame accepted by the raw writer.
    /// The completion receives the exact frame ID stored in metadata.
    @discardableResult
    func requestManualCapture(completion: @escaping (UInt64) -> Void) -> Bool {
        guard !isFinishing, manualCaptureCompletion == nil else { return false }
        manualCaptureCompletion = completion
        return true
    }

    /// Called only from FrameProcessor's serial queue. Metadata is appended
    /// only when the exact same pixel buffer was accepted by AVAssetWriter.
    func append(
        pixelBuffer: CVPixelBuffer,
        presentationTime: CMTime,
        frameID: UInt64,
        results: [DetectedSaber],
        analysis: SaberFrameAnalysis? = nil
    ) {
        guard !isFinishing else { return }
        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)
        do {
            if writer == nil { try configureWriter(width: width, height: height) }
        } catch {
            droppedFrameCount += 1
            return
        }
        guard dimensions?.width == width, dimensions?.height == height,
              let writerInput, let adaptor else {
            droppedFrameCount += 1
            return
        }

        let sourceTime = presentationTime.isValid && presentationTime.isNumeric
            ? presentationTime
            : CMTime(value: Int64(frames.count), timescale: 30)
        if firstPresentationTime == nil { firstPresentationTime = sourceTime }
        let relativeTime = CMTimeSubtract(sourceTime, firstPresentationTime ?? .zero)
        guard relativeTime.isValid, relativeTime.isNumeric,
              lastPresentationTime.map({ CMTimeCompare(relativeTime, $0) > 0 }) ?? true,
              writerInput.isReadyForMoreMediaData,
              adaptor.append(pixelBuffer, withPresentationTime: relativeTime) else {
            droppedFrameCount += 1
            return
        }
        lastPresentationTime = relativeTime

        let freshRedResult = results.first { $0.color == .red && $0.isFresh }
        let freshBlueResult = results.first { $0.color == .blue && $0.isFresh }
        let freshRed = freshRedResult?.endpoints
        let freshBlue = freshBlueResult?.endpoints
        let redDetectionSucceeded = freshRedResult.map { !$0.isPredicted } ?? false
        let blueDetectionSucceeded = freshBlueResult.map { !$0.isPredicted } ?? false
        let metadataIndex = frames.count
        frames.append(DebugRecordingFrameMetadata(
            frameID: frameID,
            presentationTimeSeconds: CMTimeGetSeconds(relativeTime),
            red: DebugRecordingDetection(
                endpoints: freshRed, predicted: freshRedResult?.isPredicted ?? false
            ),
            blue: DebugRecordingDetection(
                endpoints: freshBlue, predicted: freshBlueResult?.isPredicted ?? false
            ),
            redDetectionSucceeded: redDetectionSucceeded,
            blueDetectionSucceeded: blueDetectionSucceeded,
            candidateDiagnostics: analysis.map(DebugRecordingCandidateDiagnostics.init),
            forensicCaptured: false,
            forensicFileName: nil,
            manualCaptured: false,
            manualFileName: nil,
            blueDropoutRole: nil,
            blueDropoutFileName: nil
        ))
        updateBlueDropoutCapture(
            pixelBuffer: pixelBuffer, frameID: frameID, metadataIndex: metadataIndex,
            width: width, height: height, blueDetected: blueDetectionSucceeded
        )
        let redIsAnomalous = anomalyDetected(for: .red, endpoints: freshRed)
        let blueIsAnomalous = anomalyDetected(for: .blue, endpoints: freshBlue)
        let manualCompletion = manualCaptureCompletion
        let shouldCaptureAnomaly = (redIsAnomalous || blueIsAnomalous)
            && forensicFrames.count < forensicPolicy.maximumFrames
        if (manualCompletion != nil || shouldCaptureAnomaly),
           let bytes = copyBGRA(pixelBuffer: pixelBuffer, width: width, height: height) {
            let fileName: String
            if manualCompletion != nil {
                fileName = "manual_frame_\(frameID).png"
                frames[metadataIndex].manualCaptured = true
                frames[metadataIndex].manualFileName = fileName
                manualCaptureCompletion = nil
            } else {
                fileName = "frame_\(frameID).png"
                frames[metadataIndex].forensicCaptured = true
                frames[metadataIndex].forensicFileName = fileName
            }
            forensicFrames.append(DebugForensicFrame(
                fileName: fileName,
                width: width, height: height, bytes: bytes
            ))
            manualCompletion?(frameID)
        }
    }

    func finish(completion: @escaping (Result<DebugRecordingResult, Error>) -> Void) {
        guard !isFinishing else {
            completion(.failure(DebugVideoRecorderError.alreadyStarted))
            return
        }
        isFinishing = true
        lastBlueDetectedFrame = nil
        guard let writer, let writerInput, let dimensions, !frames.isEmpty else {
            completion(.failure(DebugVideoRecorderError.noFrames))
            return
        }
        let metadata = DebugRecordingMetadata(
            sessionID: sessionID, width: dimensions.width, height: dimensions.height, frames: frames
        )
        let forensicFrames = forensicFrames
        let dropped = droppedFrameCount
        writerInput.markAsFinished()
        writer.finishWriting { [rawVideoURL, overlayVideoURL, metadataURL, forensicDirectoryURL] in
            // This runs only after Stop. User-initiated priority keeps the
            // explicitly requested export responsive without touching capture.
            Task.detached(priority: .userInitiated) {
                do {
                    guard writer.status == .completed else {
                        throw DebugVideoRecorderError.appendFailed(writer.error?.localizedDescription ?? "writer status \(writer.status.rawValue)")
                    }
                    if !forensicFrames.isEmpty {
                        try FileManager.default.createDirectory(
                            at: forensicDirectoryURL, withIntermediateDirectories: true
                        )
                        for frame in forensicFrames {
                            try DebugVideoRecorder.writeForensicPNG(
                                frame, directory: forensicDirectoryURL
                            )
                        }
                    }
                    let data = try JSONEncoder.prettyPrinted.encode(metadata)
                    try data.write(to: metadataURL, options: .atomic)
                    var triageBundleURL: URL?
                    var triageErrorMessage: String?
                    do {
                        triageBundleURL = try DebugRecordingTriageBuilder.build(
                            metadataURL: metadataURL,
                            forensicDirectoryURL: forensicFrames.isEmpty ? nil : forensicDirectoryURL
                        )
                        if let triageBundleURL {
                            DebugBundleTransfer.shared.enqueue(bundleURL: triageBundleURL)
                        }
                    } catch {
                        // Triage is best-effort and must not turn a completed recording into a failure.
                        triageErrorMessage = error.localizedDescription
                        print("[DebugTriage] bundle generation failed: \(error.localizedDescription)")
                    }
                    try await DebugVideoRecorder.makeOverlayVideo(
                        rawURL: rawVideoURL,
                        outputURL: overlayVideoURL,
                        metadata: metadata
                    )
                    completion(.success(DebugRecordingResult(
                        sessionID: metadata.sessionID,
                        rawVideoURL: rawVideoURL,
                        overlayVideoURL: overlayVideoURL,
                        metadataURL: metadataURL,
                        forensicDirectoryURL: forensicFrames.isEmpty ? nil : forensicDirectoryURL,
                        recordedFrameCount: metadata.frames.count,
                        droppedFrameCount: dropped,
                        triageBundleURL: triageBundleURL,
                        triageErrorMessage: triageErrorMessage
                    )))
                } catch {
                    completion(.failure(error))
                }
            }
        }
    }

    private func anomalyDetected(
        for color: SaberColor,
        endpoints: (PixelPoint, PixelPoint)?
    ) -> Bool {
        guard let endpoints else { return false }
        let length = hypot(Double(endpoints.1.x - endpoints.0.x),
                           Double(endpoints.1.y - endpoints.0.y))
        let previous = previousLengths[color]
        previousLengths[color] = length
        if length >= forensicPolicy.absoluteLengthThreshold { return true }
        return length >= forensicPolicy.relativeLengthThreshold
            && previous.map { length >= $0 * forensicPolicy.growthRatio } == true
    }

    /// Keeps only one retained camera buffer while BLUE is detected. A BGRA
    /// copy happens only on the true→false transition and on recovery.
    private func updateBlueDropoutCapture(
        pixelBuffer: CVPixelBuffer,
        frameID: UInt64,
        metadataIndex: Int,
        width: Int,
        height: Int,
        blueDetected: Bool
    ) {
        if blueDetected {
            if blueDropoutActive {
                let fileName = "blue_dropout_recovered_\(frameID).png"
                if captureCurrentBGRA(pixelBuffer: pixelBuffer, width: width, height: height,
                                      fileName: fileName) {
                    frames[metadataIndex].blueDropoutRole = "recovered"
                    frames[metadataIndex].blueDropoutFileName = fileName
                }
                blueDropoutActive = false
            }
            lastBlueDetectedFrame = DebugRetainedBlueFrame(
                pixelBuffer: pixelBuffer, frameID: frameID, metadataIndex: metadataIndex,
                width: width, height: height
            )
            return
        }

        guard !blueDropoutActive, let previous = lastBlueDetectedFrame,
              forensicFrames.count + 3 <= forensicPolicy.maximumFrames else { return }
        let previousFileName = "blue_dropout_last_true_\(previous.frameID).png"
        let dropoutFileName = "blue_dropout_false_\(frameID).png"
        guard captureRetainedBGRA(previous, fileName: previousFileName),
              captureCurrentBGRA(pixelBuffer: pixelBuffer, width: width, height: height,
                                 fileName: dropoutFileName) else { return }
        frames[previous.metadataIndex].blueDropoutRole = "last-detected-before-dropout"
        frames[previous.metadataIndex].blueDropoutFileName = previousFileName
        frames[metadataIndex].blueDropoutRole = "dropout"
        frames[metadataIndex].blueDropoutFileName = dropoutFileName
        lastBlueDetectedFrame = nil
        blueDropoutActive = true
    }

    private func captureCurrentBGRA(
        pixelBuffer: CVPixelBuffer, width: Int, height: Int, fileName: String
    ) -> Bool {
        guard let bytes = copyBGRA(pixelBuffer: pixelBuffer, width: width, height: height) else {
            return false
        }
        forensicFrames.append(DebugForensicFrame(
            fileName: fileName, width: width, height: height, bytes: bytes
        ))
        return true
    }

    private func captureRetainedBGRA(
        _ frame: DebugRetainedBlueFrame, fileName: String
    ) -> Bool {
        CVPixelBufferLockBaseAddress(frame.pixelBuffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(frame.pixelBuffer, .readOnly) }
        return captureCurrentBGRA(
            pixelBuffer: frame.pixelBuffer, width: frame.width,
            height: frame.height, fileName: fileName
        )
    }

    /// The caller already has the camera BGRA buffer locked. Only anomalous,
    /// writer-accepted frames reach this copy; encoding and disk I/O wait for Stop.
    private func copyBGRA(pixelBuffer: CVPixelBuffer, width: Int, height: Int) -> Data? {
        guard CVPixelBufferGetPixelFormatType(pixelBuffer) == kCVPixelFormatType_32BGRA,
              let base = CVPixelBufferGetBaseAddress(pixelBuffer) else { return nil }
        let sourceStride = CVPixelBufferGetBytesPerRow(pixelBuffer)
        let destinationStride = width * 4
        var data = Data(count: destinationStride * height)
        data.withUnsafeMutableBytes { destination in
            guard let destinationBase = destination.baseAddress else { return }
            for row in 0..<height {
                memcpy(destinationBase.advanced(by: row * destinationStride),
                       base.advanced(by: row * sourceStride), destinationStride)
            }
        }
        return data
    }

    private static func writeForensicPNG(
        _ frame: DebugForensicFrame,
        directory: URL
    ) throws {
        let bytesPerRow = frame.width * 4
        let image = CIImage(
            bitmapData: frame.bytes,
            bytesPerRow: bytesPerRow,
            size: CGSize(width: frame.width, height: frame.height),
            format: .BGRA8,
            colorSpace: CGColorSpaceCreateDeviceRGB()
        )
        try CIContext(options: [.cacheIntermediates: false]).writePNGRepresentation(
            of: image,
            to: directory.appendingPathComponent(frame.fileName),
            format: .RGBA8,
            colorSpace: CGColorSpaceCreateDeviceRGB()
        )
    }

    private func configureWriter(width: Int, height: Int) throws {
        guard writer == nil else { throw DebugVideoRecorderError.alreadyStarted }
        let writer = try AVAssetWriter(outputURL: rawVideoURL, fileType: .mp4)
        let settings: [String: Any] = [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: width,
            AVVideoHeightKey: height
        ]
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: settings)
        input.expectsMediaDataInRealTime = true
        guard writer.canAdd(input) else {
            throw DebugVideoRecorderError.cannotCreateWriter("video input is unsupported")
        }
        writer.add(input)
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(
            assetWriterInput: input,
            sourcePixelBufferAttributes: [
                kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA,
                kCVPixelBufferWidthKey as String: width,
                kCVPixelBufferHeightKey as String: height
            ]
        )
        guard writer.startWriting() else {
            throw DebugVideoRecorderError.cannotCreateWriter(writer.error?.localizedDescription ?? "startWriting failed")
        }
        writer.startSession(atSourceTime: .zero)
        self.writer = writer
        writerInput = input
        self.adaptor = adaptor
        dimensions = (width, height)
    }

    private static func makeOverlayVideo(
        rawURL: URL,
        outputURL: URL,
        metadata: DebugRecordingMetadata
    ) async throws {
        let asset = AVURLAsset(url: rawURL)
        guard let track = try await asset.loadTracks(withMediaType: .video).first else {
            throw DebugVideoRecorderError.cannotReadRawVideo("video track not found")
        }
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [
            kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA
        ])
        output.alwaysCopiesSampleData = true
        guard reader.canAdd(output) else {
            throw DebugVideoRecorderError.cannotReadRawVideo("reader output is unsupported")
        }
        reader.add(output)

        try? FileManager.default.removeItem(at: outputURL)
        let writer = try AVAssetWriter(outputURL: outputURL, fileType: .mp4)
        let input = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: metadata.width,
            AVVideoHeightKey: metadata.height
        ])
        input.expectsMediaDataInRealTime = false
        input.transform = try await track.load(.preferredTransform)
        let adaptor = AVAssetWriterInputPixelBufferAdaptor(assetWriterInput: input)
        guard writer.canAdd(input) else {
            throw DebugVideoRecorderError.cannotCreateWriter("overlay input is unsupported")
        }
        writer.add(input)
        guard reader.startReading(), writer.startWriting() else {
            throw DebugVideoRecorderError.cannotReadRawVideo(
                reader.error?.localizedDescription ?? writer.error?.localizedDescription ?? "reader/writer start failed"
            )
        }
        writer.startSession(atSourceTime: .zero)

        var index = 0
        while let sample = output.copyNextSampleBuffer() {
            guard index < metadata.frames.count,
                  let pixelBuffer = CMSampleBufferGetImageBuffer(sample) else {
                reader.cancelReading()
                writer.cancelWriting()
                throw DebugVideoRecorderError.metadataFrameMismatch(
                    expected: metadata.frames.count, actual: index + 1
                )
            }
            let frame = metadata.frames[index]
            OverlayRasterizer.draw(frame.red.endpoints, color: .red, into: pixelBuffer)
            OverlayRasterizer.draw(frame.blue.endpoints, color: .blue, into: pixelBuffer)
            while !input.isReadyForMoreMediaData { Thread.sleep(forTimeInterval: 0.001) }
            let presentationTime = CMSampleBufferGetPresentationTimeStamp(sample)
            guard adaptor.append(pixelBuffer, withPresentationTime: presentationTime) else {
                reader.cancelReading()
                writer.cancelWriting()
                throw DebugVideoRecorderError.appendFailed(writer.error?.localizedDescription ?? "overlay append failed")
            }
            index += 1
        }
        guard index == metadata.frames.count else {
            reader.cancelReading()
            writer.cancelWriting()
            throw DebugVideoRecorderError.metadataFrameMismatch(expected: metadata.frames.count, actual: index)
        }
        input.markAsFinished()
        await writer.finishWriting()
        guard reader.status == .completed, writer.status == .completed else {
            throw DebugVideoRecorderError.appendFailed(
                reader.error?.localizedDescription ?? writer.error?.localizedDescription ?? "overlay finalize failed"
            )
        }
    }
}

enum OverlayColor {
    case red
    case blue

    var bgra: (UInt8, UInt8, UInt8, UInt8) {
        switch self {
        case .red: return (0, 0, 255, 255)
        case .blue: return (255, 80, 0, 255)
        }
    }
}

enum OverlayRasterizer {
    static func draw(_ endpoints: (PixelPoint, PixelPoint)?, color: OverlayColor,
                     into pixelBuffer: CVPixelBuffer) {
        guard let endpoints else { return }
        CVPixelBufferLockBaseAddress(pixelBuffer, [])
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, []) }
        guard let base = CVPixelBufferGetBaseAddress(pixelBuffer) else { return }
        draw(endpoints, color: color,
             baseAddress: base.assumingMemoryBound(to: UInt8.self),
             width: CVPixelBufferGetWidth(pixelBuffer),
             height: CVPixelBufferGetHeight(pixelBuffer),
             bytesPerRow: CVPixelBufferGetBytesPerRow(pixelBuffer))
    }

    static func draw(_ endpoints: (PixelPoint, PixelPoint)?, color: OverlayColor,
                     baseAddress: UnsafeMutablePointer<UInt8>, width: Int, height: Int,
                     bytesPerRow: Int) {
        guard let endpoints, width > 0, height > 0 else { return }
        let x0 = endpoints.0.x
        let y0 = endpoints.0.y
        let x1 = endpoints.1.x
        let y1 = endpoints.1.y
        let steps = max(abs(x1 - x0), abs(y1 - y0), 1)
        for step in 0...steps {
            let fraction = Double(step) / Double(steps)
            let x = Int((Double(x0) + Double(x1 - x0) * fraction).rounded())
            let y = Int((Double(y0) + Double(y1 - y0) * fraction).rounded())
            paintDisk(x: x, y: y, radius: 2, color: color.bgra,
                      baseAddress: baseAddress, width: width, height: height,
                      bytesPerRow: bytesPerRow)
        }
        paintDisk(x: x0, y: y0, radius: 5, color: color.bgra,
                  baseAddress: baseAddress, width: width, height: height,
                  bytesPerRow: bytesPerRow)
        paintDisk(x: x1, y: y1, radius: 5, color: color.bgra,
                  baseAddress: baseAddress, width: width, height: height,
                  bytesPerRow: bytesPerRow)
    }

    private static func paintDisk(x: Int, y: Int, radius: Int,
                                  color: (UInt8, UInt8, UInt8, UInt8),
                                  baseAddress: UnsafeMutablePointer<UInt8>, width: Int, height: Int,
                                  bytesPerRow: Int) {
        let minY = max(0, y - radius)
        let maxY = min(height - 1, y + radius)
        let minX = max(0, x - radius)
        let maxX = min(width - 1, x + radius)
        guard minX <= maxX, minY <= maxY else { return }
        for py in minY...maxY where (py - y) * (py - y) <= radius * radius {
            for px in minX...maxX where (px - x) * (px - x) + (py - y) * (py - y) <= radius * radius {
                let offset = py * bytesPerRow + px * 4
                baseAddress[offset] = color.0
                baseAddress[offset + 1] = color.1
                baseAddress[offset + 2] = color.2
                baseAddress[offset + 3] = color.3
            }
        }
    }
}

private extension JSONEncoder {
    static var prettyPrinted: JSONEncoder {
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        return encoder
    }
}
