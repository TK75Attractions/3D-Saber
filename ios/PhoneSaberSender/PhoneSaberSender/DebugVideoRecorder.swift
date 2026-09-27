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
    let durationSeconds: TimeInterval
    let diskUsageBytes: Int64
    let finishReason: DebugRecordingFinishReason
}

enum DebugRecordingFinishReason: Equatable {
    case user
    case maximumDuration
    case maximumDiskUsage
    case maximumMetadataSize
}

enum DebugRecordingLimits {
    static let maximumDurationSeconds: TimeInterval = 5 * 60
    static let maximumDiskUsageBytes: Int64 = 768 * 1_024 * 1_024
    static let minimumFreeSpaceReserveBytes: Int64 = 128 * 1_024 * 1_024
    static let maximumMetadataBytes: Int64 = 220 * 1_024 * 1_024
    static let maximumSingleVideoBytes: Int64 = 220 * 1_024 * 1_024
    static let maximumLosslessImageBytes: Int64 = 64 * 1_024 * 1_024
    static let maximumBufferedLosslessBytes = 128 * 1_024 * 1_024
    static let maximumManualLosslessCaptures = 3
    static let maximumForensicImages = 8
    static let averageVideoBitRate = 4_000_000
    static let metadataFrameReserveBytes: Int64 = 64 * 1_024
}

struct DebugRecordingSessionSummary: Identifiable, Equatable {
    let sessionID: String
    let modifiedAt: Date
    let diskUsageBytes: Int64

    var id: String { sessionID }
}

enum DebugRecordingStorage {
    private static let sessionSuffixes = [
        "_metadata.json.partial", "_metadata.json", "_overlay.mp4", "_raw.mp4", "_forensic"
    ]
    private static let sessionDateFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.timeZone = .current
        formatter.dateFormat = "yyyyMMdd_HHmmss_SSS"
        return formatter
    }()

    static func availableCapacity(at directory: URL) throws -> Int64 {
        let values = try directory.resourceValues(forKeys: [
            .volumeAvailableCapacityForImportantUsageKey,
            .volumeAvailableCapacityKey
        ])
        if let capacity = values.volumeAvailableCapacityForImportantUsage {
            return capacity
        }
        if let capacity = values.volumeAvailableCapacity {
            return Int64(capacity)
        }
        throw CocoaError(.fileReadUnknown)
    }

    static func validateStartCapacity(at directory: URL) throws {
        let required = DebugRecordingLimits.maximumDiskUsageBytes
            + DebugRecordingLimits.minimumFreeSpaceReserveBytes
        let available = try availableCapacity(at: directory)
        guard available >= required else {
            throw DebugVideoRecorderError.insufficientDiskSpace(
                required: required, available: available
            )
        }
    }

    static func sessionSummaries(in directory: URL) -> [DebugRecordingSessionSummary] {
        guard let entries = try? FileManager.default.contentsOfDirectory(
            at: directory, includingPropertiesForKeys: [.isDirectoryKey, .contentModificationDateKey]
        ) else { return [] }
        var sessionIDs = Set<String>()
        for entry in entries {
            let name = entry.lastPathComponent
            guard name.hasPrefix("phonesaber_") else { continue }
            for suffix in sessionSuffixes where name.hasSuffix(suffix) {
                sessionIDs.insert(String(name.dropLast(suffix.count)))
                break
            }
        }
        return sessionIDs.compactMap { sessionID in
            let files = matchingSessionEntries(sessionID: sessionID, entries: entries)
            guard !files.isEmpty else { return nil }
            let modifiedAt = files.compactMap {
                try? $0.resourceValues(forKeys: [.contentModificationDateKey]).contentModificationDate
            }.max() ?? Date.distantPast
            return DebugRecordingSessionSummary(
                sessionID: sessionID,
                modifiedAt: modifiedAt,
                diskUsageBytes: files.reduce(Int64(0)) {
                    $0 + allocatedSize(at: $1)
                }
            )
        }.sorted { lhs, rhs in
            let lhsDate = dateFromSessionID(lhs.sessionID) ?? lhs.modifiedAt
            let rhsDate = dateFromSessionID(rhs.sessionID) ?? rhs.modifiedAt
            return lhsDate == rhsDate
                ? lhs.sessionID > rhs.sessionID
                : lhsDate > rhsDate
        }
    }

    @discardableResult
    static func deleteOlderSessions(in directory: URL) throws -> (count: Int, bytes: Int64) {
        let sessions = sessionSummaries(in: directory)
        guard sessions.count > 1 else { return (0, 0) }
        let newestID = sessions[0].sessionID
        var removedCount = 0
        var removedBytes: Int64 = 0
        let entries = try FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)
        for session in sessions.dropFirst() where session.sessionID != newestID {
            let matching = matchingSessionEntries(sessionID: session.sessionID, entries: entries)
            for entry in matching {
                try FileManager.default.removeItem(at: entry)
            }
            removedCount += 1
            removedBytes += session.diskUsageBytes
        }
        return (removedCount, removedBytes)
    }

    static func diskUsage(sessionID: String, in directory: URL) -> Int64 {
        guard let entries = try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil) else {
            return 0
        }
        return matchingSessionEntries(sessionID: sessionID, entries: entries).reduce(Int64(0)) {
            $0 + allocatedSize(at: $1)
        }
    }

    static func diskUsage(at directory: URL) -> Int64 {
        allocatedSize(at: directory)
    }

    private static func matchingSessionEntries(sessionID: String, entries: [URL]) -> [URL] {
        entries.filter { entry in
            let name = entry.lastPathComponent
            return name == "\(sessionID)_raw.mp4"
                || name == "\(sessionID)_overlay.mp4"
                || name == "\(sessionID)_metadata.json"
                || name == "\(sessionID)_metadata.json.partial"
                || name == "\(sessionID)_forensic"
        }
    }

    private static func dateFromSessionID(_ sessionID: String) -> Date? {
        let prefix = "phonesaber_"
        guard sessionID.hasPrefix(prefix) else { return nil }
        return sessionDateFormatter.date(from: String(sessionID.dropFirst(prefix.count)))
    }

    private static func allocatedSize(at url: URL) -> Int64 {
        let values = try? url.resourceValues(forKeys: [.isDirectoryKey, .fileSizeKey])
        if values?.isDirectory == true,
           let enumerator = FileManager.default.enumerator(at: url, includingPropertiesForKeys: [.fileSizeKey]) {
            return enumerator.compactMap { item -> Int64? in
                guard let item = item as? URL,
                      let size = try? item.resourceValues(forKeys: [.fileSizeKey]).fileSize else { return nil }
                return Int64(size)
            }.reduce(0, +)
        }
        return Int64(values?.fileSize ?? 0)
    }
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
    var redDropoutRole: String?
    var redDropoutFileName: String?
}

struct DebugRecordingMetadata: Codable, Equatable {
    static let currentFormatVersion = 1

    let formatVersion: Int
    let sessionID: String
    let width: Int
    let height: Int
    let frames: [DebugRecordingFrameMetadata]
    let cameraSamples: [DebugRecordingCameraSample]
}

/// Camera state is sampled during a DEBUG recording, without changing capture settings.
struct DebugRecordingCameraSample: Codable, Equatable {
    var frameID: UInt64?
    var presentationTimeSeconds: Double?
    let exposureDurationMs: Double
    let iso: Float
    let whiteBalanceRedGain: Float
    let whiteBalanceGreenGain: Float
    let whiteBalanceBlueGain: Float
    let exposureMode: String
    let whiteBalanceMode: String
    let focusMode: String
    let lensPosition: Float
    let activeFormat: String
    let activeFormatFPSRanges: String
    let activeMinFPS: Double?
    let activeMaxFPS: Double?
}

struct DebugForensicCapturePolicy {
    static let production = DebugForensicCapturePolicy(
        absoluteLengthThreshold: 260,
        relativeLengthThreshold: 180,
        growthRatio: 2.0,
        maximumFrames: DebugRecordingLimits.maximumForensicImages
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

private struct DebugOverlayFrame {
    let red: (PixelPoint, PixelPoint)?
    let blue: (PixelPoint, PixelPoint)?
}

private final class DebugRecordingMetadataStream {
    private let partialURL: URL
    private let finalURL: URL
    private let encoder = JSONEncoder()
    private var handle: FileHandle
    private var firstFrame = true
    private(set) var bytesWritten: Int64

    init(url: URL, sessionID: String, width: Int, height: Int) throws {
        finalURL = url
        partialURL = url.appendingPathExtension("partial")
        encoder.outputFormatting = [.sortedKeys]
        FileManager.default.createFile(atPath: partialURL.path, contents: nil)
        handle = try FileHandle(forWritingTo: partialURL)
        let encodedSessionID = try encoder.encode(sessionID)
        let header = Data("{\"formatVersion\":1,\"sessionID\":\(String(decoding: encodedSessionID, as: UTF8.self)),\"width\":\(width),\"height\":\(height),\"frames\":[".utf8)
        try handle.write(contentsOf: header)
        bytesWritten = Int64(header.count)
    }

    var canReserveNextFrame: Bool {
        bytesWritten + 2 * DebugRecordingLimits.metadataFrameReserveBytes
            <= DebugRecordingLimits.maximumMetadataBytes
    }

    func append(_ frame: DebugRecordingFrameMetadata) throws {
        let encoded = try encoder.encode(frame)
        let separator = firstFrame ? Data() : Data([0x2c])
        let nextSize = bytesWritten + Int64(separator.count + encoded.count)
            + 2 // closing array and object
        guard nextSize <= DebugRecordingLimits.maximumMetadataBytes else {
            throw DebugVideoRecorderError.metadataSizeLimitReached
        }
        try handle.write(contentsOf: separator)
        try handle.write(contentsOf: encoded)
        bytesWritten += Int64(separator.count + encoded.count)
        firstFrame = false
    }

    func finish(cameraSamples: [DebugRecordingCameraSample]) throws {
        let sampleData = try encoder.encode(cameraSamples)
        let suffixCount = Data("],\"cameraSamples\":".utf8).count + sampleData.count + 1
        guard bytesWritten + Int64(suffixCount) <= DebugRecordingLimits.maximumMetadataBytes else {
            throw DebugVideoRecorderError.metadataSizeLimitReached
        }
        try handle.write(contentsOf: Data("],\"cameraSamples\":".utf8))
        try handle.write(contentsOf: sampleData)
        try handle.write(contentsOf: Data("}".utf8))
        try handle.synchronize()
        try handle.close()
        bytesWritten += Int64(suffixCount)
    }

    func publish() throws {
        if FileManager.default.fileExists(atPath: finalURL.path) {
            try FileManager.default.removeItem(at: finalURL)
        }
        try FileManager.default.moveItem(at: partialURL, to: finalURL)
    }

    func discard() {
        try? handle.close()
        try? FileManager.default.removeItem(at: partialURL)
    }
}

private struct DebugRetainedBlueFrame {
    let pixelBuffer: CVPixelBuffer
    let frameID: UInt64
    let width: Int
    let height: Int
}

enum DebugRecordingAppendResult: Equatable {
    case accepted
    case skipped
    case reachedLimit(DebugRecordingFinishReason)
}

enum DebugVideoRecorderError: LocalizedError, Equatable {
    case alreadyStarted
    case cameraNotReady
    case noFrames
    case cannotCreateWriter(String)
    case appendFailed(String)
    case cannotReadRawVideo(String)
    case metadataFrameMismatch(expected: Int, actual: Int)
    case insufficientDiskSpace(required: Int64, available: Int64)
    case manualCaptureLimitReached
    case losslessMemoryLimitReached
    case metadataSizeLimitReached
    case diskUsageLimitReached

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
        case .insufficientDiskSpace(let required, let available):
            return "録画を開始できません。空き容量が不足しています (必要 \(ByteCountFormatter.string(fromByteCount: required, countStyle: .file)) / 空き \(ByteCountFormatter.string(fromByteCount: available, countStyle: .file)))"
        case .manualCaptureLimitReached:
            return "この録画のlossless capture上限に達しました"
        case .losslessMemoryLimitReached:
            return "lossless画像のメモリ上限に達したため、この画像は保存できません"
        case .metadataSizeLimitReached:
            return "metadataのサイズ上限に達しました"
        case .diskUsageLimitReached:
            return "この録画のdisk usage上限に達しました"
        }
    }
}

/// Debug-only-in-use recorder. It stores one raw H.264 stream during capture
/// and the final fresh endpoints paired with every frame that the writer
/// accepted. The overlay is deliberately rendered only after recording stops.
final class DebugVideoRecorder {
    /// Retains at most five minutes of the approximately 1 Hz camera samples.
    static let maximumCameraSamples = 300

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
    private var latestRecordedFrameTiming: (UInt64, Double)?
    private var cameraSamples: [DebugRecordingCameraSample] = []
    private var metadataStream: DebugRecordingMetadataStream?
    private var triageAccumulator = DebugRecordingTriageAccumulator()
    private var pendingMetadataFrame: DebugRecordingFrameMetadata?
    private var overlayFrames: [DebugOverlayFrame] = []
    private var recordedFrameCount = 0
    private var forensicFrames: [DebugForensicFrame] = []
    private var automaticForensicCaptureCount = 0
    private var bufferedLosslessBytes = 0
    private var reservedLosslessDiskBytes: Int64 = 0
    private var manualLosslessCaptureCount = 0
    private var previousLengths: [SaberColor: Double] = [:]
    private var manualCaptureCompletion: ((Result<UInt64, DebugVideoRecorderError>) -> Void)?
    private var lastBlueDetectedFrame: DebugRetainedBlueFrame?
    private var blueDropoutActive = false
    private var lastRedDetectedFrame: DebugRetainedBlueFrame?
    private var redDropoutActive = false
    private let forensicPolicy: DebugForensicCapturePolicy
    private let clock: () -> TimeInterval
    private var startedAt: TimeInterval?
    private var terminalError: Error?
    private var finishReason: DebugRecordingFinishReason = .user
    private(set) var droppedFrameCount = 0
    private var isFinishing = false

    init(directory: URL, date: Date = Date(),
         forensicPolicy: DebugForensicCapturePolicy = .production,
         clock: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }) throws {
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        try DebugRecordingStorage.validateStartCapacity(at: directory)
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
        self.clock = clock
    }

    /// Prepares the encoder before the recording interval begins. This keeps
    /// one-time H.264 setup out of the first measured frame.
    func prepare(width: Int, height: Int) throws {
        if writer == nil { try configureWriter(width: width, height: height) }
        if startedAt == nil { startedAt = clock() }
    }

    /// Arms a one-shot capture for the next frame accepted by the raw writer.
    /// The completion receives the exact frame ID stored in metadata.
    @discardableResult
    func requestManualCapture(
        completion: @escaping (Result<UInt64, DebugVideoRecorderError>) -> Void
    ) -> Bool {
        guard !isFinishing, manualCaptureCompletion == nil else { return false }
        guard manualLosslessCaptureCount < DebugRecordingLimits.maximumManualLosslessCaptures else {
            completion(.failure(.manualCaptureLimitReached))
            return true
        }
        manualCaptureCompletion = completion
        return true
    }

    func appendCameraSample(_ sample: DebugRecordingCameraSample) {
        guard !isFinishing else { return }
        if cameraSamples.count >= Self.maximumCameraSamples {
            cameraSamples.removeFirst()
        }
        cameraSamples.append(sample)
    }

    var latestFrameTiming: (UInt64, Double)? {
        latestRecordedFrameTiming
    }

    /// Called only from FrameProcessor's serial queue. Metadata is appended
    /// only when the exact same pixel buffer was accepted by AVAssetWriter.
    func append(
        pixelBuffer: CVPixelBuffer,
        presentationTime: CMTime,
        frameID: UInt64,
        results: [DetectedSaber],
        analysis: SaberFrameAnalysis? = nil
    ) -> DebugRecordingAppendResult {
        guard !isFinishing else { return .skipped }
        if startedAt == nil { startedAt = clock() }
        if let startedAt, clock() - startedAt >= DebugRecordingLimits.maximumDurationSeconds {
            return .reachedLimit(.maximumDuration)
        }
        if let metadataStream, !metadataStream.canReserveNextFrame {
            return .reachedLimit(.maximumMetadataSize)
        }
        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)
        do {
            if writer == nil { try configureWriter(width: width, height: height) }
        } catch {
            droppedFrameCount += 1
            terminalError = error
            return .skipped
        }
        guard dimensions?.width == width, dimensions?.height == height,
              let writerInput, let adaptor else {
            droppedFrameCount += 1
            return .skipped
        }

        let sourceTime = presentationTime.isValid && presentationTime.isNumeric
            ? presentationTime
            : CMTime(value: Int64(recordedFrameCount), timescale: 30)
        if firstPresentationTime == nil { firstPresentationTime = sourceTime }
        let relativeTime = CMTimeSubtract(sourceTime, firstPresentationTime ?? .zero)
        guard relativeTime.isValid, relativeTime.isNumeric,
              lastPresentationTime.map({ CMTimeCompare(relativeTime, $0) > 0 }) ?? true,
              writerInput.isReadyForMoreMediaData,
              adaptor.append(pixelBuffer, withPresentationTime: relativeTime) else {
            droppedFrameCount += 1
            return .skipped
        }
        lastPresentationTime = relativeTime

        let freshRedResult = results.first { $0.color == .red && $0.isFresh }
        let freshBlueResult = results.first { $0.color == .blue && $0.isFresh }
        let freshRed = freshRedResult?.endpoints
        let freshBlue = freshBlueResult?.endpoints
        let redDetectionSucceeded = freshRedResult.map { !$0.isPredicted } ?? false
        let blueDetectionSucceeded = freshBlueResult.map { !$0.isPredicted } ?? false
        var frame = DebugRecordingFrameMetadata(
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
            blueDropoutFileName: nil,
            redDropoutRole: nil,
            redDropoutFileName: nil
        )
        updateBlueDropoutCapture(
            pixelBuffer: pixelBuffer, frameID: frameID,
            currentMetadata: &frame, previousMetadata: &pendingMetadataFrame,
            width: width, height: height, blueDetected: blueDetectionSucceeded
        )
        updateRedDropoutCapture(
            pixelBuffer: pixelBuffer, frameID: frameID,
            currentMetadata: &frame, previousMetadata: &pendingMetadataFrame,
            width: width, height: height, redDetected: redDetectionSucceeded
        )
        let redIsAnomalous = anomalyDetected(for: .red, endpoints: freshRed)
        let blueIsAnomalous = anomalyDetected(for: .blue, endpoints: freshBlue)
        let manualCompletion = manualCaptureCompletion
        let shouldCaptureAnomaly = (redIsAnomalous || blueIsAnomalous)
            && automaticForensicCaptureCount < forensicPolicy.maximumFrames
        if let manualCompletion {
            let fileName = "manual_frame_\(frameID).png"
            if captureCurrentBGRA(pixelBuffer: pixelBuffer, width: width, height: height,
                                  fileName: fileName, manual: true) {
                frame.manualCaptured = true
                frame.manualFileName = fileName
                manualCaptureCompletion = nil
                manualCompletion(.success(frameID))
            } else {
                manualCaptureCompletion = nil
                manualCompletion(.failure(.losslessMemoryLimitReached))
            }
        } else if shouldCaptureAnomaly {
            let fileName = "frame_\(frameID).png"
            if captureCurrentBGRA(pixelBuffer: pixelBuffer, width: width, height: height,
                                  fileName: fileName) {
                frame.forensicCaptured = true
                frame.forensicFileName = fileName
            }
        }

        do {
            if let previous = pendingMetadataFrame {
                try metadataStream?.append(previous)
                triageAccumulator.observe(previous)
            }
            pendingMetadataFrame = frame
        } catch {
            terminalError = error
            return .reachedLimit(.maximumMetadataSize)
        }
        latestRecordedFrameTiming = (frameID, frame.presentationTimeSeconds)
        overlayFrames.append(DebugOverlayFrame(red: freshRed, blue: freshBlue))
        recordedFrameCount += 1

        if recordedFrameCount % 15 == 0 {
            let rawBytes = fileSize(at: rawVideoURL)
            let sessionBytes = DebugRecordingStorage.diskUsage(sessionID: sessionID,
                                                               in: rawVideoURL.deletingLastPathComponent())
            let remainingCapacity = try? DebugRecordingStorage.availableCapacity(
                at: rawVideoURL.deletingLastPathComponent()
            )
            if rawBytes >= DebugRecordingLimits.maximumSingleVideoBytes
                || sessionBytes >= DebugRecordingLimits.maximumDiskUsageBytes
                || (remainingCapacity ?? 0) <= DebugRecordingLimits.minimumFreeSpaceReserveBytes {
                return .reachedLimit(.maximumDiskUsage)
            }
        }
        return .accepted
    }

    func finish(
        reason: DebugRecordingFinishReason = .user,
        completion: @escaping (Result<DebugRecordingResult, Error>) -> Void
    ) {
        guard !isFinishing else {
            completion(.failure(DebugVideoRecorderError.alreadyStarted))
            return
        }
        isFinishing = true
        finishReason = reason
        lastBlueDetectedFrame = nil
        lastRedDetectedFrame = nil
        guard let writer, let writerInput, let dimensions, recordedFrameCount > 0 else {
            metadataStream?.discard()
            writer?.cancelWriting()
            completion(.failure(DebugVideoRecorderError.noFrames))
            return
        }
        if let terminalError {
            metadataStream?.discard()
            writerInput.markAsFinished()
            writer.finishWriting { completion(.failure(terminalError)) }
            return
        }
        do {
            if let pendingMetadataFrame {
                try metadataStream?.append(pendingMetadataFrame)
                triageAccumulator.observe(pendingMetadataFrame)
                self.pendingMetadataFrame = nil
            }
            try metadataStream?.finish(cameraSamples: cameraSamples)
        } catch {
            metadataStream?.discard()
            writerInput.markAsFinished()
            writer.finishWriting { completion(.failure(error)) }
            return
        }
        let forensicFrames = forensicFrames
        let triageFrames = triageAccumulator.retainedFrames
        let stream = metadataStream
        let dropped = droppedFrameCount
        let frameCount = recordedFrameCount
        let overlayFrames = overlayFrames
        let duration = max(0, clock() - (startedAt ?? clock()))
        let reason = finishReason
        let directory = rawVideoURL.deletingLastPathComponent()
        let currentSessionID = sessionID
        writerInput.markAsFinished()
        writer.finishWriting { [rawVideoURL, overlayVideoURL, metadataURL, forensicDirectoryURL] in
            let writerCompleted = writer.status == .completed
            let writerFailure = writer.error?.localizedDescription
                ?? "writer status \(writer.status.rawValue)"
            // This runs only after Stop. User-initiated priority keeps the
            // explicitly requested export responsive without touching capture.
            Task.detached(priority: .userInitiated) {
                do {
                    guard writerCompleted else {
                        throw DebugVideoRecorderError.appendFailed(writerFailure)
                    }
                    if !forensicFrames.isEmpty {
                        try FileManager.default.createDirectory(
                            at: forensicDirectoryURL, withIntermediateDirectories: true
                        )
                        for frame in forensicFrames {
                            try DebugVideoRecorder.writeForensicPNG(
                                frame, directory: forensicDirectoryURL
                            )
                            guard DebugRecordingStorage.diskUsage(at: forensicDirectoryURL)
                                <= DebugRecordingLimits.maximumLosslessImageBytes,
                                DebugRecordingStorage.diskUsage(
                                    sessionID: currentSessionID, in: directory
                                ) <= DebugRecordingLimits.maximumDiskUsageBytes else {
                                try? FileManager.default.removeItem(
                                    at: forensicDirectoryURL.appendingPathComponent(frame.fileName)
                                )
                                throw DebugVideoRecorderError.diskUsageLimitReached
                            }
                        }
                    }
                    // The complete path appears only after raw and required PNGs succeed.
                    try stream?.publish()
                    let snapshot = DebugRecordingMetadata(
                        formatVersion: DebugRecordingMetadata.currentFormatVersion,
                        sessionID: currentSessionID, width: dimensions.width,
                        height: dimensions.height, frames: triageFrames, cameraSamples: []
                    )
                    var triageBundleURL: URL?
                    var triageErrorMessage: String?
                    if !triageFrames.isEmpty {
                        do {
                            triageBundleURL = try DebugRecordingTriageBuilder.build(
                                metadataData: try JSONEncoder().encode(snapshot),
                                metadataURL: metadataURL,
                                forensicDirectoryURL: forensicFrames.isEmpty ? nil : forensicDirectoryURL,
                                recordedFrameCount: frameCount
                            )
                            if let triageBundleURL {
                                let usage = DebugRecordingStorage.diskUsage(
                                    sessionID: currentSessionID, in: directory
                                )
                                guard usage <= DebugRecordingLimits.maximumDiskUsageBytes else {
                                    try? FileManager.default.removeItem(at: triageBundleURL)
                                    throw DebugVideoRecorderError.diskUsageLimitReached
                                }
                                DebugBundleTransfer.shared.enqueue(bundleURL: triageBundleURL)
                            }
                        } catch {
                            triageErrorMessage = error.localizedDescription
                            print("[DebugTriage] bundle generation failed: \(error.localizedDescription)")
                        }
                    }
                    // Overlay is a viewing aid. Its failure must not invalidate
                    // verified metadata, PNGs, or a finished triage bundle.
                    do {
                        try await DebugVideoRecorder.makeOverlayVideo(
                            rawURL: rawVideoURL, outputURL: overlayVideoURL,
                            width: dimensions.width, height: dimensions.height,
                            frames: overlayFrames
                        )
                    } catch {
                        try? FileManager.default.removeItem(at: overlayVideoURL)
                        print("[DebugRecording] overlay failed: \(error.localizedDescription)")
                    }
                    let diskUsage = DebugRecordingStorage.diskUsage(
                        sessionID: currentSessionID, in: directory
                    )
                    guard diskUsage <= DebugRecordingLimits.maximumDiskUsageBytes else {
                        try? FileManager.default.removeItem(at: overlayVideoURL)
                        throw DebugVideoRecorderError.diskUsageLimitReached
                    }
                    completion(.success(DebugRecordingResult(
                        sessionID: currentSessionID,
                        rawVideoURL: rawVideoURL,
                        overlayVideoURL: overlayVideoURL,
                        metadataURL: metadataURL,
                        forensicDirectoryURL: forensicFrames.isEmpty ? nil : forensicDirectoryURL,
                        recordedFrameCount: frameCount,
                        droppedFrameCount: dropped,
                        triageBundleURL: triageBundleURL,
                        triageErrorMessage: triageErrorMessage,
                        durationSeconds: duration,
                        diskUsageBytes: diskUsage,
                        finishReason: reason
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
        currentMetadata: inout DebugRecordingFrameMetadata,
        previousMetadata: inout DebugRecordingFrameMetadata?,
        width: Int,
        height: Int,
        blueDetected: Bool
    ) {
        if blueDetected {
            if blueDropoutActive {
                let fileName = "blue_dropout_recovered_\(frameID).png"
                if captureCurrentBGRA(pixelBuffer: pixelBuffer, width: width, height: height,
                                      fileName: fileName) {
                    currentMetadata.blueDropoutRole = "recovered"
                    currentMetadata.blueDropoutFileName = fileName
                }
                blueDropoutActive = false
            }
            lastBlueDetectedFrame = DebugRetainedBlueFrame(
                pixelBuffer: pixelBuffer, frameID: frameID,
                width: width, height: height
            )
            return
        }

        guard !blueDropoutActive, let previous = lastBlueDetectedFrame,
              previousMetadata != nil,
              automaticForensicCaptureCount + 3 <= forensicPolicy.maximumFrames else { return }
        let previousFileName = "blue_dropout_last_true_\(previous.frameID).png"
        let dropoutFileName = "blue_dropout_false_\(frameID).png"
        guard captureRetainedBGRA(previous, fileName: previousFileName),
              captureCurrentBGRA(pixelBuffer: pixelBuffer, width: width, height: height,
                                 fileName: dropoutFileName) else { return }
        previousMetadata?.blueDropoutRole = "last-detected-before-dropout"
        previousMetadata?.blueDropoutFileName = previousFileName
        currentMetadata.blueDropoutRole = "dropout"
        currentMetadata.blueDropoutFileName = dropoutFileName
        lastBlueDetectedFrame = nil
        blueDropoutActive = true
    }

    /// RED and BLUE share the same one-frame metadata delay and capture budget.
    private func updateRedDropoutCapture(
        pixelBuffer: CVPixelBuffer,
        frameID: UInt64,
        currentMetadata: inout DebugRecordingFrameMetadata,
        previousMetadata: inout DebugRecordingFrameMetadata?,
        width: Int,
        height: Int,
        redDetected: Bool
    ) {
        if redDetected {
            if redDropoutActive {
                let fileName = "red_dropout_recovered_\(frameID).png"
                if captureCurrentBGRA(pixelBuffer: pixelBuffer, width: width, height: height,
                                      fileName: fileName) {
                    currentMetadata.redDropoutRole = "recovered"
                    currentMetadata.redDropoutFileName = fileName
                }
                redDropoutActive = false
            }
            lastRedDetectedFrame = DebugRetainedBlueFrame(
                pixelBuffer: pixelBuffer, frameID: frameID,
                width: width, height: height
            )
            return
        }
        guard !redDropoutActive, let previous = lastRedDetectedFrame,
              previousMetadata != nil,
              automaticForensicCaptureCount + 3 <= forensicPolicy.maximumFrames else { return }
        let previousFileName = "red_dropout_last_true_\(previous.frameID).png"
        let dropoutFileName = "red_dropout_false_\(frameID).png"
        guard captureRetainedBGRA(previous, fileName: previousFileName),
              captureCurrentBGRA(pixelBuffer: pixelBuffer, width: width, height: height,
                                 fileName: dropoutFileName) else { return }
        previousMetadata?.redDropoutRole = "last-detected-before-dropout"
        previousMetadata?.redDropoutFileName = previousFileName
        currentMetadata.redDropoutRole = "dropout"
        currentMetadata.redDropoutFileName = dropoutFileName
        lastRedDetectedFrame = nil
        redDropoutActive = true
    }

    private func captureCurrentBGRA(
        pixelBuffer: CVPixelBuffer, width: Int, height: Int, fileName: String,
        manual: Bool = false
    ) -> Bool {
        let (pixelCount, overflow) = width.multipliedReportingOverflow(by: height)
        guard !overflow else { return false }
        let (byteCount, byteOverflow) = pixelCount.multipliedReportingOverflow(by: 4)
        guard !byteOverflow,
              (manual || automaticForensicCaptureCount < forensicPolicy.maximumFrames),
              bufferedLosslessBytes + byteCount <= DebugRecordingLimits.maximumBufferedLosslessBytes,
              manualLosslessCaptureCount < DebugRecordingLimits.maximumManualLosslessCaptures || !manual else {
            return false
        }
        let pngBound = Int64(byteCount) + max(Int64(byteCount) / 100, 64 * 1_024)
        guard reservedLosslessDiskBytes + pngBound <= DebugRecordingLimits.maximumLosslessImageBytes else {
            return false
        }
        guard let bytes = copyBGRA(pixelBuffer: pixelBuffer, width: width, height: height) else {
            return false
        }
        forensicFrames.append(DebugForensicFrame(
            fileName: fileName, width: width, height: height, bytes: bytes
        ))
        bufferedLosslessBytes += bytes.count
        reservedLosslessDiskBytes += pngBound
        if manual {
            manualLosslessCaptureCount += 1
        } else {
            automaticForensicCaptureCount += 1
        }
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

    private func fileSize(at url: URL) -> Int64 {
        let values = try? url.resourceValues(forKeys: [.fileSizeKey])
        return Int64(values?.fileSize ?? 0)
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
            AVVideoHeightKey: height,
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: DebugRecordingLimits.averageVideoBitRate
            ]
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
        let metadataStream = try DebugRecordingMetadataStream(
            url: metadataURL, sessionID: sessionID, width: width, height: height
        )
        guard writer.startWriting() else {
            metadataStream.discard()
            throw DebugVideoRecorderError.cannotCreateWriter(writer.error?.localizedDescription ?? "startWriting failed")
        }
        writer.startSession(atSourceTime: .zero)
        self.writer = writer
        writerInput = input
        self.adaptor = adaptor
        dimensions = (width, height)
        self.metadataStream = metadataStream
    }

    private static func makeOverlayVideo(
        rawURL: URL,
        outputURL: URL,
        width: Int,
        height: Int,
        frames: [DebugOverlayFrame]
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
            AVVideoWidthKey: width,
            AVVideoHeightKey: height,
            AVVideoCompressionPropertiesKey: [
                AVVideoAverageBitRateKey: DebugRecordingLimits.averageVideoBitRate
            ]
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
            guard index < frames.count,
                  let pixelBuffer = CMSampleBufferGetImageBuffer(sample) else {
                reader.cancelReading()
                writer.cancelWriting()
                throw DebugVideoRecorderError.metadataFrameMismatch(
                    expected: frames.count, actual: index + 1
                )
            }
            let frame = frames[index]
            OverlayRasterizer.draw(frame.red, color: .red, into: pixelBuffer)
            OverlayRasterizer.draw(frame.blue, color: .blue, into: pixelBuffer)
            while !input.isReadyForMoreMediaData {
                try await Task.sleep(nanoseconds: 1_000_000)
            }
            let presentationTime = CMSampleBufferGetPresentationTimeStamp(sample)
            guard adaptor.append(pixelBuffer, withPresentationTime: presentationTime) else {
                reader.cancelReading()
                writer.cancelWriting()
                throw DebugVideoRecorderError.appendFailed(writer.error?.localizedDescription ?? "overlay append failed")
            }
            index += 1
            if index % 15 == 0 {
                let outputBytes = Int64(
                    (try? outputURL.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0
                )
                let remainingCapacity = try? DebugRecordingStorage.availableCapacity(
                    at: outputURL.deletingLastPathComponent()
                )
                if outputBytes >= DebugRecordingLimits.maximumSingleVideoBytes
                    || (remainingCapacity ?? 0)
                        <= DebugRecordingLimits.minimumFreeSpaceReserveBytes {
                    reader.cancelReading()
                    writer.cancelWriting()
                    try? FileManager.default.removeItem(at: outputURL)
                    throw DebugVideoRecorderError.diskUsageLimitReached
                }
            }
        }
        guard index == frames.count else {
            reader.cancelReading()
            writer.cancelWriting()
            throw DebugVideoRecorderError.metadataFrameMismatch(expected: frames.count, actual: index)
        }
        input.markAsFinished()
        await writer.finishWriting()
        guard reader.status == .completed, writer.status == .completed else {
            try? FileManager.default.removeItem(at: outputURL)
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
