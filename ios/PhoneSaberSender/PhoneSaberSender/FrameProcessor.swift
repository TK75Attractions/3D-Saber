import AVFoundation
import CoreVideo

struct DetectedSaber {
    let endpoints: (PixelPoint, PixelPoint)
    let color: SaberColor
    let isFresh: Bool
}

final class FrameProcessor {
    let queue = DispatchQueue(label: "PhoneSaberSender.frames", qos: .userInteractive)
    var onResult: (([DetectedSaber], Int, Int, TimeInterval, Int) -> Void)?
    var redThreshold = ColorThreshold()
    var blueThreshold = ColorThreshold()
    private var tracks: [SaberColor: Track] = [.red: Track(), .blue: Track()]
    private let holdDuration: TimeInterval = 0.18
    private let clock: () -> TimeInterval
    private let expiryScheduler: ((DispatchQueue, TimeInterval, DispatchWorkItem) -> Void)?
    private var expiryWorkItem: DispatchWorkItem?
    private var lastDimensions: (Int, Int)?
    private var generation = 0
    var currentGeneration: Int { queue.sync { generation } }

    init(
        clock: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
        expiryScheduler: ((DispatchQueue, TimeInterval, DispatchWorkItem) -> Void)? = { queue, delay, workItem in
            queue.asyncAfter(deadline: .now() + delay, execute: workItem)
        }
    ) {
        self.clock = clock
        self.expiryScheduler = expiryScheduler
    }

    private struct Track {
        var endpoints: (PixelPoint, PixelPoint)?
        var lastSeen: TimeInterval = 0
    }

    @discardableResult
    func reset() -> Int {
        queue.sync {
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

    func process(_ sampleBuffer: CMSampleBuffer) {
        let processingStart = ProcessInfo.processInfo.systemUptime
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
        let bytes = base.assumingMemoryBound(to: UInt8.self)
        let bgra = Array(UnsafeBufferPointer(start: bytes, count: bytesPerRow * height))
        let detected: [(SaberColor, (PixelPoint, PixelPoint)?)] = [
            (.red, detectSaber(in: bgra, width: width, height: height, bytesPerRow: bytesPerRow, color: .red, threshold: redThreshold)),
            (.blue, detectSaber(in: bgra, width: width, height: height, bytesPerRow: bytesPerRow, color: .blue, threshold: blueThreshold))
        ]
        emitResults(detected, width: width, height: height, processingStart: processingStart, generation: generation)
    }

    private func emitResults(_ detected: [(SaberColor, (PixelPoint, PixelPoint)?)], width: Int, height: Int, processingStart: TimeInterval, generation: Int) {
        let results = detected.compactMap { color, current -> DetectedSaber? in
            var track = tracks[color, default: Track()]
            if let current {
                let endpoints = stableEndpoints(current, previous: track.endpoints)
                track.endpoints = endpoints
                track.lastSeen = processingStart
                tracks[color] = track
                return DetectedSaber(endpoints: endpoints, color: color, isFresh: true)
            }
            guard let held = track.endpoints, processingStart - track.lastSeen <= holdDuration else {
                tracks[color] = Track()
                return nil
            }
            tracks[color] = track
            return DetectedSaber(endpoints: held, color: color, isFresh: false)
        }
        onResult?(results, width, height, processingStart, generation)
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
        onResult?(held, width, height, now, generation)
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
}
