import AVFoundation
import CoreImage
import CoreVideo

struct DetectedSaber {
    let endpoints: (PixelPoint, PixelPoint)
    let color: SaberColor
    let isFresh: Bool
    let isPredicted: Bool
    var diagnosticSendStarted: ((String) -> Void)? = nil

    init(endpoints: (PixelPoint, PixelPoint), color: SaberColor,
         isFresh: Bool, isPredicted: Bool = false) {
        self.endpoints = endpoints
        self.color = color
        self.isFresh = isFresh
        self.isPredicted = isPredicted
    }
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
    // Published under pendingLock so a MainActor result callback never waits
    // for the detector queue just to reject an obsolete generation.
    private var generationSnapshot = 0
    private let pendingLock = NSLock()
    private struct PendingFrame {
        let sampleBuffer: CMSampleBuffer
        let sequence: UInt64
        let callbackHostTime: TimeInterval
        let captureHostTime: TimeInterval?
        let callbackIntervalMs: Double?
        let callbackWorkMs: Double?
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
    private var debugVideoRecorder: DebugVideoRecorder?
    private var debugRecordingIsFinalizing = false
    private var debugRecordingFinishCallbacks: [
        (Result<DebugRecordingResult, Error>) -> Void
    ] = []
    var onDebugRecordingLimitReached: ((DebugRecordingFinishReason) -> Void)?
    var onDebugRecordingAutoFinished: ((Result<DebugRecordingResult, Error>) -> Void)?
#if DEBUG
    private var inputFrameIntervalWindow = CameraFrameIntervalWindow(capacity: 120)
    private var detailedProfilingEnabled = false
    private var freezeDiagnosticsEnabled = false
    private var lastCameraCallbackTime: TimeInterval?
    private let freezeWindow = FrameFreezeWindow()
    private var pendingDebugCameraSample: DebugRecordingCameraSample?
    private var debugCameraSampleDrainScheduled = false
#endif
    /// Newest AVCaptureDevice exposure state, guarded by pendingLock. Read
    /// only while a Debug Recording is active.
    private var debugCameraDeviceState: DebugCameraDeviceState?
    /// Operator segment label for Debug Recording metadata, guarded by
    /// pendingLock. Never read by recognition, tracking or UDP output.
    private var debugSegmentLabel: DebugSegmentLabel = .unlabeled
    private let rawFrameDirectory: () throws -> URL
    private lazy var rawFrameContext = CIContext(options: [.cacheIntermediates: false])
    var currentGeneration: Int {
        pendingLock.lock(); defer { pendingLock.unlock() }
        return generationSnapshot
    }
    var replacedPendingFrameCountForTesting: Int {
        pendingLock.lock(); defer { pendingLock.unlock() }
        return replacedPendingFrames
    }
#if DEBUG
    var pendingDebugCameraSampleCountForTesting: Int {
        pendingLock.lock(); defer { pendingLock.unlock() }
        return pendingDebugCameraSample == nil ? 0 : 1
    }
    var motionHistoryCountForTesting: Int {
        queue.sync { debugVideoRecorder?.motionHistoryCountForTesting ?? 0 }
    }
#endif

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
        var previousEndpoints: (PixelPoint, PixelPoint)?
        var lastSeen: TimeInterval = 0
        var missingFrameCount = 0
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
        lastCameraCallbackTime = nil
#endif
        pendingLock.unlock()
        return queue.sync {
            expiryWorkItem?.cancel()
            expiryWorkItem = nil
            tracks = [.red: Track(), .blue: Track()]
            lastDimensions = nil
#if DEBUG
            freezeWindow.reset()
#endif
            generation += 1
            pendingLock.lock()
            generationSnapshot = generation
            pendingLock.unlock()
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

    func setFreezeDiagnosticsEnabled(_ enabled: Bool) {
        pendingLock.lock()
        freezeDiagnosticsEnabled = enabled
        lastCameraCallbackTime = nil
        pendingLock.unlock()
        freezeWindow.reset()
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
        let callbackIntervalMs = freezeDiagnosticsEnabled
            ? lastCameraCallbackTime.map { (callbackHostTime - $0) * 1000 } : nil
        if freezeDiagnosticsEnabled { lastCameraCallbackTime = callbackHostTime }
        let callbackWorkMs: Double? = freezeDiagnosticsEnabled
            ? max(0, (clock() - callbackHostTime) * 1000) : nil
#else
        let callbackHostTime: TimeInterval = 0
        let captureHostTime: TimeInterval? = nil
        let callbackIntervalMs: Double? = nil
        let callbackWorkMs: Double? = nil
#endif
        if pendingFrame != nil { replacedPendingFrames += 1 }
        pendingFrame = PendingFrame(sampleBuffer: sampleBuffer, sequence: nextFrameSequence,
                                    callbackHostTime: callbackHostTime, captureHostTime: captureHostTime,
                                    callbackIntervalMs: callbackIntervalMs,
                                    callbackWorkMs: callbackWorkMs)
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
                captureHostTime: next.captureHostTime,
                callbackIntervalMs: next.callbackIntervalMs,
                callbackWorkMs: next.callbackWorkMs)

        // A continuously full mailbox must not monopolize this queue. In
        // particular, CameraViewModel checks currentGeneration via queue.sync
        // on MainActor; give that check and reset/configuration calls a turn
        // between frames while still retaining only the newest pending frame.
        pendingLock.lock()
        let hasPendingFrame = pendingFrame != nil
        if !hasPendingFrame { workerScheduled = false }
        pendingLock.unlock()
        if hasPendingFrame {
            queue.async { [weak self] in self?.drainLatestFrames() }
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
                captureHostTime: captureHostTime,
                callbackIntervalMs: nil, callbackWorkMs: nil)
    }

    /// The request is consumed by the next submitted camera frame. Normal
    /// operation adds no new per-frame synchronization beyond the mailbox lock.
    func requestRawFrameSave() {
        pendingLock.lock()
        rawFrameSaveRequested = true
        pendingLock.unlock()
    }

    func startDebugRecording(diagnosticColors: DebugDiagnosticColors = .both,
                             cameraExposureExperiment: CameraExposureExperimentState? = nil,
                             completion: @escaping (Result<String, Error>) -> Void) {
        queue.async { [weak self] in
            guard let self else { return }
            do {
                guard self.debugVideoRecorder == nil, !self.debugRecordingIsFinalizing else {
                    throw DebugVideoRecorderError.alreadyStarted
                }
                guard let dimensions = self.lastDimensions else {
                    throw DebugVideoRecorderError.cameraNotReady
                }
                let recorder = try DebugVideoRecorder(directory: self.rawFrameDirectory(),
                                                      diagnosticColors: diagnosticColors,
                                                      cameraExposureExperiment: cameraExposureExperiment)
                try recorder.prepare(width: dimensions.0, height: dimensions.1)
                self.debugVideoRecorder = recorder
                completion(.success(recorder.sessionID))
            } catch {
                completion(.failure(error))
            }
        }
    }

#if DEBUG
    func recordDebugCameraSample(_ sample: DebugRecordingCameraSample) {
        pendingLock.lock()
        pendingDebugCameraSample = sample
        let shouldScheduleDrain = !debugCameraSampleDrainScheduled
        if shouldScheduleDrain { debugCameraSampleDrainScheduled = true }
        pendingLock.unlock()
        guard shouldScheduleDrain else { return }
        queue.async { [weak self] in self?.drainDebugCameraSample() }
    }

    private func drainDebugCameraSample() {
        pendingLock.lock()
        let sample = pendingDebugCameraSample
        pendingDebugCameraSample = nil
        debugCameraSampleDrainScheduled = false
        pendingLock.unlock()

        guard let sample, let recorder = debugVideoRecorder else { return }
        var timed = sample
        if let (frameID, seconds) = recorder.latestFrameTiming {
            timed.frameID = frameID
            timed.presentationTimeSeconds = seconds
        }
        recorder.appendCameraSample(timed)
    }
#endif

    /// Stores the newest device exposure state for per-frame Debug Recording
    /// metadata. A single slot under the mailbox lock: callers never wait for
    /// the processing queue, and a newer state simply replaces an older one.
    func updateDebugCameraDeviceState(_ state: DebugCameraDeviceState?) {
        pendingLock.lock()
        debugCameraDeviceState = state
        pendingLock.unlock()
    }

    /// Sets the ground-truth label stamped on the following recorded frames.
    /// A single slot under the mailbox lock: the caller never waits for the
    /// processing queue, and the first frame read after this call carries it.
    func setDebugSegmentLabel(_ label: DebugSegmentLabel) {
        pendingLock.lock()
        debugSegmentLabel = label
        pendingLock.unlock()
    }

    var debugSegmentLabelForTesting: DebugSegmentLabel {
        pendingLock.lock(); defer { pendingLock.unlock() }
        return debugSegmentLabel
    }

    func stopDebugRecording(
        reason: DebugRecordingFinishReason = .user,
        completion: @escaping (Result<DebugRecordingResult, Error>) -> Void
    ) {
        queue.async { [weak self] in
            guard let self else {
                completion(.failure(DebugVideoRecorderError.noFrames))
                return
            }
            if self.debugRecordingIsFinalizing {
                self.debugRecordingFinishCallbacks.append(completion)
                return
            }
            guard let recorder = self.debugVideoRecorder else {
                completion(.failure(DebugVideoRecorderError.noFrames))
                return
            }
            self.finishDebugRecordingOnQueue(recorder, reason: reason,
                                             automatic: false, completion: completion)
        }
    }

    /// Called on the frame queue for manual and automatic endings alike.
    private func finishDebugRecordingOnQueue(
        _ recorder: DebugVideoRecorder, reason: DebugRecordingFinishReason,
        automatic: Bool,
        completion: ((Result<DebugRecordingResult, Error>) -> Void)?
    ) {
        debugVideoRecorder = nil
        debugRecordingIsFinalizing = true
        recorder.finish(reason: reason) { [weak self] result in
            guard let self else { completion?(result); return }
            self.queue.async {
                self.debugRecordingIsFinalizing = false
                if automatic { self.onDebugRecordingAutoFinished?(result) }
                completion?(result)
                let callbacks = self.debugRecordingFinishCallbacks
                self.debugRecordingFinishCallbacks.removeAll()
                callbacks.forEach { $0(result) }
            }
        }
    }

    func requestManualLosslessFrame(
        completion: @escaping (Result<UInt64, DebugVideoRecorderError>) -> Void
    ) {
        queue.async { [weak self] in
            guard let recorder = self?.debugVideoRecorder else {
                completion(.failure(.noFrames))
                return
            }
            let armed = recorder.requestManualCapture(completion: completion)
            if !armed {
                completion(.failure(.alreadyStarted))
            }
        }
    }

    private func process(_ sampleBuffer: CMSampleBuffer, saveRequestedRawFrame: Bool,
                         sequence: UInt64, callbackHostTime: TimeInterval,
                         captureHostTime: TimeInterval?,
                         callbackIntervalMs: Double?, callbackWorkMs: Double?) {
        let processingStart = clock()
        let accessStart = processingStart
        guard let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }
        guard CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly) == kCVReturnSuccess else { return }
#if DEBUG
        let lockStart = clock()
#endif
        var isLocked = true
        defer { if isLocked { CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly) } }
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
        let collectProfile = detailedProfilingEnabled || freezeDiagnosticsEnabled
        let collectFreeze = freezeDiagnosticsEnabled
        let sendPerformance = detailedProfilingEnabled
        pendingLock.unlock()
        let analysis = analyzeSabers(baseAddress: bytes, width: width, height: height,
                                     bytesPerRow: bytesPerRow, redThreshold: redThreshold,
                                     blueThreshold: blueThreshold, collectProfile: collectProfile,
                                     collectPipelineDiagnostics: debugVideoRecorder != nil)
        let sabers = analysis.selected
#else
        let analysis: SaberFrameAnalysis?
        let sabers: [SaberColor: (PixelPoint, PixelPoint)]
        if debugVideoRecorder != nil {
            let recordedAnalysis = analyzeSabers(
                baseAddress: bytes, width: width, height: height,
                bytesPerRow: bytesPerRow, redThreshold: redThreshold,
                blueThreshold: blueThreshold, collectProfile: false,
                collectPipelineDiagnostics: true
            )
            analysis = recordedAnalysis
            sabers = recordedAnalysis.selected
        } else {
            analysis = nil
            sabers = detectSabers(baseAddress: bytes, width: width, height: height,
                                  bytesPerRow: bytesPerRow,
                                  redThreshold: redThreshold, blueThreshold: blueThreshold)
        }
#endif
        let detected: [(SaberColor, (PixelPoint, PixelPoint)?)] = [
            (.red, sabers[.red]),
            (.blue, sabers[.blue])
        ]
#if DEBUG
        let detectionEnd = clock()
#endif
        // Detection has finished reading BGRA. Keep the lock only when a
        // requested recording or raw save still needs the pixel buffer.
#if DEBUG
        var lockHoldMs = 0.0
#endif
        if debugVideoRecorder == nil && !saveRequestedRawFrame {
#if DEBUG
            lockHoldMs = (clock() - lockStart) * 1000
#endif
            CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly)
            isLocked = false
        }
#if DEBUG
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
        let emitted = emitResults(detected, width: width, height: height,
                                  processingStart: processingStart,
                                  generation: generation, trace: trace, recordingFrameID: sequence)
        if let debugVideoRecorder = debugVideoRecorder {
            let processingTimeSeconds = max(0, clock() - processingStart)
            // Recording only: this frame's Exif attachment plus the newest
            // pushed device state. Neither read can block on the camera.
            pendingLock.lock()
            let deviceState = debugCameraDeviceState
            let segmentLabel = debugSegmentLabel
            pendingLock.unlock()
            let camera = DebugRecordingFrameCamera.make(
                exif: DebugRecordingFrameCamera.exifAttachment(of: sampleBuffer),
                device: deviceState, now: clock())
            let appendResult = debugVideoRecorder.append(
                pixelBuffer: pixelBuffer,
                presentationTime: CMSampleBufferGetPresentationTimeStamp(sampleBuffer),
                frameID: sequence,
                results: emitted,
                analysis: analysis,
                processingTimeSeconds: processingTimeSeconds,
                camera: camera,
                segmentLabel: segmentLabel
            )
            if case .reachedLimit(let reason) = appendResult {
                onDebugRecordingLimitReached?(reason)
                finishDebugRecordingOnQueue(debugVideoRecorder, reason: reason,
                                            automatic: true, completion: nil)
            }
        }
        if saveRequestedRawFrame { saveRawFrame(pixelBuffer) }
        if isLocked {
#if DEBUG
            lockHoldMs = (clock() - lockStart) * 1000
#endif
            CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly)
            isLocked = false
        }
#if DEBUG
        if sendPerformance, let profile = analysis.profile {
            onPerformance?(FramePerformanceSample(pixelBufferAccessMs: accessMs, detector: profile))
        }
        if collectFreeze, let profile = analysis.profile {
            let completed = clock()
            freezeWindow.record(
                detected: !sabers.isEmpty, callbackIntervalMs: callbackIntervalMs,
                callbackWorkMs: callbackWorkMs,
                queueWaitMs: max(0, (processingStart - callbackHostTime) * 1000),
                pixelLockMs: lockHoldMs,
                profile: profile,
                detectionMs: (detectionEnd - processingStart) * 1000,
                callbackToCompletionMs: (completed - callbackHostTime) * 1000,
                replacedFrames: traceReplacedFrames
            )
            if freezeWindow.shouldProbeMainQueue(at: completed) {
                DispatchQueue.main.async { [weak self] in
                    self?.freezeWindow.recordMainQueueLatency(
                        (HostMonotonicClock.now() - completed) * 1000
                    )
                }
            }
        }
#endif
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

    @discardableResult
    private func emitResults(_ detected: [(SaberColor, (PixelPoint, PixelPoint)?)], width: Int, height: Int, processingStart: TimeInterval, generation: Int, trace: FrameTrace? = nil, recordingFrameID: UInt64? = nil) -> [DetectedSaber] {
        var results = detected.compactMap { color, current -> DetectedSaber? in
            var track = tracks[color, default: Track()]
            if let current {
                let endpoints = stableEndpoints(current, previous: track.endpoints)
                track.previousEndpoints = track.endpoints
                track.endpoints = endpoints
                track.lastSeen = processingStart
                track.missingFrameCount = 0
                tracks[color] = track
                return DetectedSaber(endpoints: endpoints, color: color, isFresh: true)
            }
            track.missingFrameCount += 1
            if track.missingFrameCount <= 3,
               let previous = track.previousEndpoints,
               let latest = track.endpoints {
                let predicted = predictedEndpoints(
                    previous: previous, latest: latest,
                    missingFrames: track.missingFrameCount,
                    width: width, height: height
                )
                tracks[color] = track
                return DetectedSaber(
                    endpoints: predicted, color: color,
                    isFresh: true, isPredicted: true
                )
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
        if let recorder = debugVideoRecorder, let frameID = recordingFrameID {
            for index in results.indices where results[index].isFresh {
                let color = results[index].color == .red ? "red" : "blue"
                let source = results[index].endpoints
                results[index].diagnosticSendStarted = { [weak self, weak recorder] coordinates in
                    self?.queue.async {
                        recorder?.recordTransmission(frameID: frameID, color: color,
                            coordinates: coordinates, sourceEndpoints: source)
                    }
                }
            }
        }
        onResult?(results, width, height, processingStart, generation, trace)
        scheduleExpiry(width: width, height: height, generation: generation)
        return results
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

    private func predictedEndpoints(
        previous: (PixelPoint, PixelPoint),
        latest: (PixelPoint, PixelPoint),
        missingFrames: Int,
        width: Int,
        height: Int
    ) -> (PixelPoint, PixelPoint) {
        func extrapolate(_ previous: PixelPoint, _ latest: PixelPoint) -> PixelPoint {
            let x = latest.x + (latest.x - previous.x) * missingFrames
            let y = latest.y + (latest.y - previous.y) * missingFrames
            return PixelPoint(
                x: min(max(x, 0), max(width - 1, 0)),
                y: min(max(y, 0), max(height - 1, 0))
            )
        }
        return (extrapolate(previous.0, latest.0),
                extrapolate(previous.1, latest.1))
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
/// Bounded, five-second aggregate. Main queue pings are sampled once per
/// report, so diagnostics cannot create a per-frame MainActor backlog.
private final class FrameFreezeWindow: @unchecked Sendable {
    private let lock = NSLock()
    private var groups: [Bool: [String: [Double]]] = [:]
    private var mainQueueLatency: [Double] = []
    private var reportStartedAt = 0.0
    private var probePending = false
    private var previousReplacedFrames = 0
    private var frameCount = 0

    func reset() {
        lock.lock(); defer { lock.unlock() }
        groups = [:]
        mainQueueLatency = []
        reportStartedAt = 0
        probePending = false
        previousReplacedFrames = 0
        frameCount = 0
    }

    func record(detected: Bool, callbackIntervalMs: Double?, callbackWorkMs: Double?,
                queueWaitMs: Double, pixelLockMs: Double, profile: SaberDetectionProfile,
                detectionMs: Double, callbackToCompletionMs: Double,
                replacedFrames: Int) {
        lock.lock(); defer { lock.unlock() }
        if reportStartedAt == 0 { reportStartedAt = HostMonotonicClock.now() }
        frameCount += 1
        func add(_ name: String, _ value: Double?) {
            guard let value, value.isFinite else { return }
            groups[detected, default: [:]][name, default: []].append(value)
        }
        add("cameraInterval", callbackIntervalMs)
        add("cameraCallbackWork", callbackWorkMs)
        add("queueWait", queueWaitMs)
        add("pixelLock", pixelLockMs)
        add("bgraScan", profile.bgraScanMs)
        add("maskGeneration", profile.maskGenerationMs)
        add("morphology", profile.morphologyMs)
        add("components", profile.connectedComponentsMs)
        add("candidateGeneration", profile.candidateGenerationMs)
        add("coreLineProposal", profile.lineProposalMs)
        add("scoreRanking", profile.candidateScoringMs + profile.lineScoreMs + profile.selectionMs)
        add("endpoint", profile.endpointAndBoundsMs)
        add("detection", detectionMs)
        add("callbackToProcessorDone", callbackToCompletionMs)
        add("candidateCount", Double(profile.candidateCount))
        add("componentCount", Double(profile.connectedComponentCount))
        add("coreLineCount", Double(profile.lineProposalCount))
        add("mailboxReplaced", Double(max(0, replacedFrames - previousReplacedFrames)))
        previousReplacedFrames = replacedFrames

        if let callbackIntervalMs, callbackIntervalMs > 100 {
            print(String(format: "[FREEZE_DIAG][CAMERA] callbackGap=%.1fms", callbackIntervalMs))
        }
        if queueWaitMs > 100 {
            print(String(format: "[FREEZE_DIAG][QUEUE] wait=%.1fms", queueWaitMs))
        }
        if detectionMs > 100 {
            print(String(format: "[FREEZE_DIAG][DETECTION] duration=%.1fms", detectionMs))
        }
    }

    func recordMainQueueLatency(_ value: Double) {
        lock.lock(); defer { lock.unlock() }
        if value.isFinite {
            mainQueueLatency.append(value)
            if value > 100 {
                print(String(format: "[FREEZE_DIAG][MAIN] delay=%.1fms", value))
            }
        }
        probePending = false
    }

    func shouldProbeMainQueue(at now: TimeInterval) -> Bool {
        lock.lock()
        guard reportStartedAt > 0, now - reportStartedAt >= 5 else {
            lock.unlock()
            return false
        }
        let snapshot = groups
        let main = mainQueueLatency
        let frames = frameCount
        frameCount = 0
        groups = [:]
        mainQueueLatency = []
        reportStartedAt = now
        let shouldProbe = !probePending
        if shouldProbe { probePending = true }
        lock.unlock()

        func stats(_ values: [Double]?) -> String {
            guard let values, !values.isEmpty else { return "-" }
            let sorted = values.sorted()
            let middle = sorted.count / 2
            let median = sorted.count.isMultiple(of: 2)
                ? (sorted[middle - 1] + sorted[middle]) / 2 : sorted[middle]
            let p95 = sorted[max(0, Int(ceil(Double(sorted.count) * 0.95)) - 1)]
            return String(format: "%.2f/%.2f/%.2f", median, p95, sorted.last ?? 0)
        }
        let all = snapshot.values.reduce(into: [String: [Double]]()) { result, group in
            for (key, values) in group { result[key, default: []].append(contentsOf: values) }
        }
        print("[FREEZE_DIAG][SUMMARY] cameraGap p50/p95/max=\(stats(all["cameraInterval"]))ms queueWait p50/p95/max=\(stats(all["queueWait"]))ms detection p50/p95/max=\(stats(all["detection"]))ms mainDelay p50/p95/max=\(stats(main))ms frames=\(frames)")
        return shouldProbe
    }
}

struct FramePerformanceSample {
    let pixelBufferAccessMs: Double
    let detector: SaberDetectionProfile
}
#endif
