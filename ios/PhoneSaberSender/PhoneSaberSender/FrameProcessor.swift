import AVFoundation
import CoreImage
import CoreVideo

struct DetectedSaber {
    let endpoints: (PixelPoint, PixelPoint)
    let color: SaberColor
    let isFresh: Bool
}

/// All values in this trace are iPhone host-monotonic seconds. `captureHostTime`
/// is nil when AVCapture's synchronization clock cannot be converted safely.
struct FrameTrace {
    let sequence: UInt64
    let captureHostTime: TimeInterval?
    let callbackHostTime: TimeInterval
    let processingStart: TimeInterval
    let detectionEnd: TimeInterval
    let receivedFrames: Int
    let processedFrames: Int
    let replacedFrames: Int
    let inputFrameIntervalStatistics: CameraFrameIntervalStatistics?

    var cameraAgeMs: Double? {
        guard let captureHostTime, callbackHostTime >= captureHostTime else { return nil }
        return (callbackHostTime - captureHostTime) * 1000
    }

    var queueWaitMs: Double { max(0, (processingStart - callbackHostTime) * 1000) }
    var detectionMs: Double { max(0, (detectionEnd - processingStart) * 1000) }
}

enum HostMonotonicClock {
    static func now() -> TimeInterval {
        CMTimeGetSeconds(CMClockGetTime(CMClockGetHostTimeClock()))
    }
}

final class FrameProcessor: @unchecked Sendable {
    let queue = DispatchQueue(label: "PhoneSaberSender.frames", qos: .userInteractive)
    var onResult: (([DetectedSaber], Int, Int, TimeInterval, Int, FrameTrace?) -> Void)?
    var onRawFrameSaved: ((Result<URL, Error>) -> Void)?
#if DEBUG
    var onPerformance: ((FramePerformanceSample) -> Void)?
#endif
    var redThreshold = ColorThreshold()
    var blueThreshold = ColorThreshold()
    private var tracks: [SaberColor: Track] = [.red: Track(), .blue: Track()]
    private let holdDuration: TimeInterval = 0.18
    private let clock: () -> TimeInterval
    private let expiryScheduler: ((DispatchQueue, TimeInterval, DispatchWorkItem) -> Void)?
    private var expiryWorkItem: DispatchWorkItem?
    private var lastDimensions: (Int, Int)?
    private var generation = 0
    private let pendingLock = NSLock()
    private struct PendingFrame {
        let sampleBuffer: CMSampleBuffer
        let sequence: UInt64
        let callbackHostTime: TimeInterval
        let captureHostTime: TimeInterval?
    }
    private var pendingFrame: PendingFrame?
    private var pendingFrameShouldSave = false
    private var rawFrameSaveRequested = false
    private var workerScheduled = false
    private var replacedPendingFrames = 0
    private var receivedFrames = 0
    private var processedFrames = 0
    private var nextFrameSequence: UInt64 = 0
    private var captureSynchronizationClock: CMClock?
#if DEBUG
    private var inputFrameIntervalWindow = CameraFrameIntervalWindow(capacity: 120)
    private var detailedProfilingEnabled = true
#endif
    private let rawFrameDirectory: () throws -> URL
    private lazy var rawFrameContext = CIContext(options: [.cacheIntermediates: false])
    var currentGeneration: Int { queue.sync { generation } }
    var replacedPendingFrameCountForTesting: Int {
        pendingLock.lock(); defer { pendingLock.unlock() }
        return replacedPendingFrames
    }

    init(
        clock: @escaping () -> TimeInterval = { HostMonotonicClock.now() },
        expiryScheduler: ((DispatchQueue, TimeInterval, DispatchWorkItem) -> Void)? = { queue, delay, workItem in
            queue.asyncAfter(deadline: .now() + delay, execute: workItem)
        },
        rawFrameDirectory: @escaping () throws -> URL = {
            guard let url = FileManager.default.urls(for: .documentDirectory, in: .userDomainMask).first else {
                throw CocoaError(.fileNoSuchFile)
            }
            return url
        }
    ) {
        self.clock = clock
        self.expiryScheduler = expiryScheduler
        self.rawFrameDirectory = rawFrameDirectory
    }

    private struct Track {
        var endpoints: (PixelPoint, PixelPoint)?
        var lastSeen: TimeInterval = 0
    }

    @discardableResult
    func reset() -> Int {
        pendingLock.lock()
        pendingFrame = nil
        pendingFrameShouldSave = false
        rawFrameSaveRequested = false
        replacedPendingFrames = 0
        receivedFrames = 0
        processedFrames = 0
        nextFrameSequence = 0
#if DEBUG
        inputFrameIntervalWindow.reset()
#endif
        pendingLock.unlock()
        return queue.sync {
            expiryWorkItem?.cancel()
            expiryWorkItem = nil
            tracks = [.red: Track(), .blue: Track()]
            lastDimensions = nil
            generation += 1
            return generation
        }
    }

    func configureThresholds(red: ColorThreshold, blue: ColorThreshold) {
        queue.sync {
            redThreshold = red
            blueThreshold = blue
        }
    }

    /// AVCaptureSession documents that its output PTS values use this clock's
    /// timebase. Keeping it here lets the callback convert to host time before
    /// it enters the one-slot mailbox.
    func configureCaptureSynchronizationClock(_ clock: CMClock?) {
        pendingLock.lock(); captureSynchronizationClock = clock; pendingLock.unlock()
    }

#if DEBUG
    func setDetailedProfilingEnabled(_ enabled: Bool) {
        pendingLock.lock(); detailedProfilingEnabled = enabled; pendingLock.unlock()
    }
#endif

    /// Capture callbacks only replace this one-slot mailbox. While one frame is
    /// processing, any number of older waiting frames collapse to the newest.
    func submit(_ sampleBuffer: CMSampleBuffer) {
        pendingLock.lock()
        receivedFrames += 1
        nextFrameSequence += 1
#if DEBUG
        let callbackHostTime = clock()
        let presentationTime = validPresentationTime(sampleBuffer)
        inputFrameIntervalWindow.record(presentationTime: presentationTime)
        let captureHostTime = convertedCaptureHostTime(sampleBuffer)
#else
        let callbackHostTime: TimeInterval = 0
        let captureHostTime: TimeInterval? = nil
#endif
        if pendingFrame != nil { replacedPendingFrames += 1 }
        pendingFrame = PendingFrame(sampleBuffer: sampleBuffer, sequence: nextFrameSequence,
                                    callbackHostTime: callbackHostTime, captureHostTime: captureHostTime)
        if rawFrameSaveRequested {
            pendingFrameShouldSave = true
            rawFrameSaveRequested = false
        }
        let shouldSchedule = !workerScheduled
        if shouldSchedule { workerScheduled = true }
        pendingLock.unlock()
        if shouldSchedule { queue.async { [weak self] in self?.drainLatestFrames() } }
    }

    private func drainLatestFrames() {
        while true {
            pendingLock.lock()
            guard let next = pendingFrame else {
                workerScheduled = false
                pendingLock.unlock()
                return
            }
            pendingFrame = nil
            let shouldSave = pendingFrameShouldSave
            pendingFrameShouldSave = false
            pendingLock.unlock()
            process(next.sampleBuffer, saveRequestedRawFrame: shouldSave, sequence: next.sequence,
                    callbackHostTime: next.callbackHostTime,
                    captureHostTime: next.captureHostTime)
        }
    }

    func process(_ sampleBuffer: CMSampleBuffer) {
        pendingLock.lock(); nextFrameSequence += 1; receivedFrames += 1
#if DEBUG
        let sequence = nextFrameSequence; let callbackHostTime = clock()
        let presentationTime = validPresentationTime(sampleBuffer)
        inputFrameIntervalWindow.record(presentationTime: presentationTime)
        let captureHostTime = convertedCaptureHostTime(sampleBuffer)
#else
        let sequence = nextFrameSequence; let callbackHostTime: TimeInterval = 0
        let captureHostTime: TimeInterval? = nil
#endif
        pendingLock.unlock()
        process(sampleBuffer, saveRequestedRawFrame: false, sequence: sequence,
                callbackHostTime: callbackHostTime,
                captureHostTime: captureHostTime)
    }

    /// The request is consumed by the next submitted camera frame. Normal
    /// operation adds no new per-frame synchronization beyond the mailbox lock.
    func requestRawFrameSave() {
        pendingLock.lock()
        rawFrameSaveRequested = true
        pendingLock.unlock()
    }

    private func process(_ sampleBuffer: CMSampleBuffer, saveRequestedRawFrame: Bool,
                         sequence: UInt64, callbackHostTime: TimeInterval,
                         captureHostTime: TimeInterval?) {
        let processingStart = clock()
        let accessStart = processingStart
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly) }
        guard let base = CVPixelBufferGetBaseAddress(pixelBuffer) else { return }
        let width = CVPixelBufferGetWidth(pixelBuffer)
        let height = CVPixelBufferGetHeight(pixelBuffer)
        if lastDimensions.map({ $0 != (width, height) }) ?? true {
            expiryWorkItem?.cancel()
            expiryWorkItem = nil
            tracks = [.red: Track(), .blue: Track()]
            lastDimensions = (width, height)
        }
        let bytesPerRow = CVPixelBufferGetBytesPerRow(pixelBuffer)
        let bytes = UnsafePointer(base.assumingMemoryBound(to: UInt8.self))
#if DEBUG
        let accessMs = (ProcessInfo.processInfo.systemUptime - accessStart) * 1000
        pendingLock.lock()
        let collectProfile = detailedProfilingEnabled
        pendingLock.unlock()
        let analysis = analyzeSabers(baseAddress: bytes, width: width, height: height,
                                     bytesPerRow: bytesPerRow, redThreshold: redThreshold,
                                     blueThreshold: blueThreshold, collectProfile: collectProfile)
        let sabers = analysis.selected
#else
        let sabers = detectSabers(baseAddress: bytes, width: width, height: height,
                                  bytesPerRow: bytesPerRow,
                                  redThreshold: redThreshold, blueThreshold: blueThreshold)
#endif
        let detected: [(SaberColor, (PixelPoint, PixelPoint)?)] = [
            (.red, sabers[.red]),
            (.blue, sabers[.blue])
        ]
        #if DEBUG
        let detectionEnd = clock()
        pendingLock.lock()
        processedFrames += 1
        let traceReceivedFrames = receivedFrames
        let traceProcessedFrames = processedFrames
        let traceReplacedFrames = replacedPendingFrames
        let inputIntervalSamples = inputFrameIntervalWindow.samples
        pendingLock.unlock()
        let trace = FrameTrace(sequence: sequence, captureHostTime: captureHostTime,
                               callbackHostTime: callbackHostTime, processingStart: processingStart,
                               detectionEnd: detectionEnd, receivedFrames: traceReceivedFrames,
                               processedFrames: traceProcessedFrames, replacedFrames: traceReplacedFrames,
                               inputFrameIntervalStatistics: CameraFrameIntervalWindow.statistics(for: inputIntervalSamples))
        #else
        let trace: FrameTrace? = nil
        #endif
        emitResults(detected, width: width, height: height, processingStart: processingStart,
                    generation: generation, trace: trace)
#if DEBUG
        if let profile = analysis.profile {
            onPerformance?(FramePerformanceSample(pixelBufferAccessMs: accessMs, detector: profile))
        }
#endif
        if saveRequestedRawFrame { saveRawFrame(pixelBuffer) }
    }

    private func saveRawFrame(_ pixelBuffer: CVPixelBuffer) {
        do {
            let directory = try rawFrameDirectory()
            try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
            let milliseconds = Int64((Date().timeIntervalSince1970 * 1000.0).rounded())
            let url = directory.appendingPathComponent("phone-saber-raw-\(milliseconds).png")
            try rawFrameContext.writePNGRepresentation(
                of: CIImage(cvPixelBuffer: pixelBuffer), to: url,
                format: .RGBA8, colorSpace: CGColorSpaceCreateDeviceRGB()
            )
            onRawFrameSaved?(.success(url))
        } catch {
            onRawFrameSaved?(.failure(error))
        }
    }

    private func emitResults(_ detected: [(SaberColor, (PixelPoint, PixelPoint)?)], width: Int, height: Int, processingStart: TimeInterval, generation: Int, trace: FrameTrace? = nil) {
        let results = detected.compactMap { color, current -> DetectedSaber? in
            var track = tracks[color, default: Track()]
            if let current {
                let endpoints = stableEndpoints(current, previous: track.endpoints)
                track.endpoints = endpoints
                track.lastSeen = processingStart
                tracks[color] = track
                return DetectedSaber(endpoints: endpoints, color: color, isFresh: true)
            }
            // Held endpoints are preview-only: CameraViewModel sends only
            // isFresh results, so the game never receives a 180 ms old point.
            guard let held = track.endpoints, processingStart - track.lastSeen <= holdDuration else {
                tracks[color] = Track()
                return nil
            }
            tracks[color] = track
            return DetectedSaber(endpoints: held, color: color, isFresh: false)
        }
        onResult?(results, width, height, processingStart, generation, trace)
        scheduleExpiry(width: width, height: height, generation: generation)
    }

    // Test-only entry point that exercises the same state transition as camera frames.
    func processDetectedForTesting(_ detected: [(SaberColor, (PixelPoint, PixelPoint)?)], at time: TimeInterval, dimensions: (width: Int, height: Int)) {
        queue.sync {
            if lastDimensions.map({ $0 != dimensions }) ?? true {
                expiryWorkItem?.cancel()
                expiryWorkItem = nil
                tracks = [.red: Track(), .blue: Track()]
                lastDimensions = dimensions
            }
            emitResults(detected, width: dimensions.width, height: dimensions.height, processingStart: time, generation: generation)
        }
    }

    func expireForTesting() {
        queue.sync { expireHeldFrames(at: clock(), width: lastDimensions?.0 ?? 0, height: lastDimensions?.1 ?? 0, generation: generation) }
    }

    private func scheduleExpiry(width: Int, height: Int, generation: Int) {
        expiryWorkItem?.cancel()
        guard let expiry = tracks.values.compactMap({ track in
            track.endpoints.map { _ in track.lastSeen + holdDuration }
        }).min() else { return }
        let item = DispatchWorkItem { [weak self] in
            guard let self else { return }
            self.expireHeldFrames(at: self.clock(), width: width, height: height, generation: generation)
        }
        expiryWorkItem = item
        guard let expiryScheduler else { return }
        let delay = max(0, expiry - clock())
        expiryScheduler(queue, delay, item)
    }

    private func expireHeldFrames(at now: TimeInterval, width: Int, height: Int, generation: Int) {
        guard self.generation == generation else { return }
        var remaining = false
        for color in [SaberColor.red, .blue] {
            if let track = tracks[color], track.endpoints != nil, now >= track.lastSeen + holdDuration {
                tracks[color] = Track()
            } else if tracks[color]?.endpoints != nil {
                remaining = true
            }
        }
        let held = [SaberColor.red, .blue].compactMap { color -> DetectedSaber? in
            guard let track = tracks[color], let endpoints = track.endpoints else { return nil }
            return DetectedSaber(endpoints: endpoints, color: color, isFresh: false)
        }
        onResult?(held, width, height, now, generation, nil)
        if remaining { scheduleExpiry(width: width, height: height, generation: generation) }
    }

    private func stableEndpoints(_ current: (PixelPoint, PixelPoint), previous: (PixelPoint, PixelPoint)?) -> (PixelPoint, PixelPoint) {
        guard let previous else { return current }
        let direct = distance(current.0, previous.0) + distance(current.1, previous.1)
        let reversed = distance(current.0, previous.1) + distance(current.1, previous.0)
        return reversed < direct ? (current.1, current.0) : current
    }

    private func distance(_ first: PixelPoint, _ second: PixelPoint) -> Double {
        hypot(Double(first.x - second.x), Double(first.y - second.y))
    }

    private func convertedCaptureHostTime(_ sampleBuffer: CMSampleBuffer) -> TimeInterval? {
        guard let captureClock = captureSynchronizationClock else { return nil }
        let presentation = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        guard presentation.isValid else { return nil }
        let host = CMSyncConvertTime(presentation, from: captureClock, to: CMClockGetHostTimeClock())
        guard host.isValid else { return nil }
        let seconds = CMTimeGetSeconds(host)
        return seconds.isFinite ? seconds : nil
    }

    private func validPresentationTime(_ sampleBuffer: CMSampleBuffer) -> TimeInterval? {
        let presentation = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        guard presentation.isValid, presentation.isNumeric else { return nil }
        let seconds = CMTimeGetSeconds(presentation)
        return seconds.isFinite ? seconds : nil
    }
}

#if DEBUG
struct FramePerformanceSample {
    let pixelBufferAccessMs: Double
    let detector: SaberDetectionProfile
}
#endif
