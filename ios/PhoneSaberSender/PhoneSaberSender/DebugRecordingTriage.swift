import CoreFoundation
import Foundation

/// Diagnostic thresholds are deliberately independent of recognition scoring.
/// All geometry values use source-image pixels and presentation timestamps.
enum DebugMotionThresholds {
    static let dropoutEnabled = true
    static let flickerEnabled = true
    static let jumpEnabled = true
    static let lengthEnabled = true
    static let predictionEnabled = true
    static let candidateAmbiguityEnabled = true
    static let nearMissEnabled = true
    static let latencyEnabled = true
    static let dropoutRunFrames = 3
    static let flickerWindowFrames = 8
    static let flickerTransitions = 3
    static let endpointSpeedPixelsPerSecond = 3_600.0
    static let endpointAccelerationPixelsPerSecondSquared = 90_000.0
    static let lengthChangeFraction = 0.35
    static let predictionErrorPixels = 80.0
    static let multipleEligibleCandidates = 2
    static let candidateScoreGap = 5.0
    static let failedRuleMarginFraction = 0.08
    static let frameIntervalSeconds = 0.055
    static let processingSeconds = 0.040
    static let eventScore = 1.0
    static let mergeIntervalSeconds = 0.20
    static let historyFrames = 64
    static let preRollSeconds = 2.0
    static let preRollStride = 15 // Four sampled 1080p frames cover about two seconds.
    static let preRollBuffers = 4
    static let postRollSeconds = 0.5
    static let maximumSelectedEvents = 3
    static let maximumEventImages = 9
    static let maximumRetainedBGRABytes = 256 * 1_024 * 1_024 // Bridge originals share this budget.
}

struct DebugMotionColorSample {
    let detected: Bool
    let endpoints: (PixelPoint, PixelPoint)?
    let candidateCount: Int
    let eligibleCount: Int
    let topScoreGap: Double?
    let failedRuleMargin: Double?
    let selectedCandidateType: String?
    var selectedCandidateIndex: Int? = nil
    var candidateSwitch: Bool? = nil
}

struct DebugMotionSample {
    let frameID: UInt64
    let timestamp: Double
    let processingSeconds: Double
    let red: DebugMotionColorSample
    let blue: DebugMotionColorSample
}

struct DebugMotionSignal: Equatable {
    let kind: String
    let color: String
    let value: Double
    let threshold: Double
    let score: Double

    var dictionary: [String: Any] {
        ["kind": kind, "color": color, "value": value,
         "threshold": threshold, "score": score]
    }
}

struct DebugMotionEvent {
    let index: Int
    let color: String
    var startFrameID: UInt64
    var endFrameID: UInt64
    var startTime: Double
    var endTime: Double
    var peakScore: Double
    var signals: [DebugMotionSignal]

    var dictionary: [String: Any] {
        ["eventIndex": index, "color": color,
         "startFrameID": startFrameID, "endFrameID": endFrameID,
         "startTime": startTime, "endTime": endTime, "peakScore": peakScore,
         "signals": signals.map(\.dictionary)]
    }
}

/// Receives only writer-accepted Debug Recording frames. Nothing is retained
/// by FrameProcessor when recording is off.
struct DebugMotionDetector {
    private var historyStorage: [DebugMotionSample?] = Array(
        repeating: nil, count: DebugMotionThresholds.historyFrames)
    private var historyCursor = 0
    private(set) var historyCount = 0
    // Ordered materialization is used by tests only, never by observe.
    var history: [DebugMotionSample] {
        (0..<historyCount).compactMap {
            historyStorage[(historyCursor - historyCount + $0 + historyStorage.count)
                % historyStorage.count]
        }
    }
    private(set) var events: [DebugMotionEvent] = []
    private(set) var signalCounts: [String: Int] = [:]
    private(set) var signalScoreSums: [String: Double] = [:]
    private(set) var signalScoreMaxima: [String: Double] = [:]
    private var lastEventIndex: [String: Int] = [:]
    private var previous: DebugMotionSample?
    private var beforePrevious: DebugMotionSample?
    private var trueRun: [String: Int] = ["red": 0, "blue": 0]
    private var transitionMasks: [String: UInt16] = ["red": 0, "blue": 0]

    mutating func observe(_ sample: DebugMotionSample) -> [DebugMotionEvent] {
        var emitted: [DebugMotionEvent] = []
        for color in ["red", "blue"] {
            let current = color == "red" ? sample.red : sample.blue
            let previousColor = color == "red" ? previous?.red : previous?.blue
            let olderColor = color == "red" ? beforePrevious?.red : beforePrevious?.blue
            let signals = colorSignals(current, previous: previousColor, older: olderColor,
                                       currentTime: sample.timestamp,
                                       previousTime: previous?.timestamp,
                                       olderTime: beforePrevious?.timestamp, color: color)
            if let event = record(signals, color: color, sample: sample) { emitted.append(event) }
        }
        var delaySignals: [DebugMotionSignal] = []
        if DebugMotionThresholds.latencyEnabled, let previous {
            let interval = sample.timestamp - previous.timestamp
            if interval > DebugMotionThresholds.frameIntervalSeconds {
                delaySignals.append(signal("frame_interval", "both", interval,
                                           DebugMotionThresholds.frameIntervalSeconds))
            }
            if sample.frameID > previous.frameID + 1 {
                delaySignals.append(signal("frame_gap", "both",
                                           Double(sample.frameID - previous.frameID), 1))
            }
        }
        if DebugMotionThresholds.latencyEnabled,
           sample.processingSeconds > DebugMotionThresholds.processingSeconds {
            delaySignals.append(signal("processing_time", "both", sample.processingSeconds,
                                       DebugMotionThresholds.processingSeconds))
        }
        if let event = record(delaySignals, color: "both", sample: sample) { emitted.append(event) }
        historyStorage[historyCursor] = sample
        historyCursor = (historyCursor + 1) % historyStorage.count
        historyCount = min(historyCount + 1, historyStorage.count)
        beforePrevious = previous
        previous = sample
        return emitted
    }

    private mutating func colorSignals(
        _ current: DebugMotionColorSample, previous: DebugMotionColorSample?,
        older: DebugMotionColorSample?, currentTime: Double,
        previousTime: Double?, olderTime: Double?, color: String
    ) -> [DebugMotionSignal] {
        var result: [DebugMotionSignal] = []
        let run = trueRun[color, default: 0]
        if DebugMotionThresholds.dropoutEnabled, let previous,
           previous.detected && !current.detected,
           run >= DebugMotionThresholds.dropoutRunFrames {
            result.append(signal("dropout", color, Double(run),
                                 Double(DebugMotionThresholds.dropoutRunFrames)))
        }
        trueRun[color] = current.detected ? run + 1 : 0
        let transition = previous.map { $0.detected != current.detected } ?? false
        let mask = ((transitionMasks[color, default: 0] << 1) | (transition ? 1 : 0))
            & UInt16((1 << DebugMotionThresholds.flickerWindowFrames) - 1)
        transitionMasks[color] = mask
        if DebugMotionThresholds.flickerEnabled,
           mask.nonzeroBitCount >= DebugMotionThresholds.flickerTransitions, transition {
            result.append(signal("flicker", color, Double(mask.nonzeroBitCount),
                                 Double(DebugMotionThresholds.flickerTransitions)))
        }
        if let endpoints = current.endpoints, let previousEndpoints = previous?.endpoints,
           let previousTime, currentTime > previousTime {
            // Endpoint order is not physical identity. Align by minimum movement.
            let prior = aligned(previousEndpoints, to: endpoints)
            let dt = currentTime - previousTime
            let displacement = max(distance(endpoints.0, prior.0),
                                   distance(endpoints.1, prior.1))
            let speed = displacement / dt
            var jumpRatio = speed / DebugMotionThresholds.endpointSpeedPixelsPerSecond
            if let olderEndpoints = older?.endpoints, let olderTime, previousTime > olderTime {
                let older = aligned(olderEndpoints, to: prior)
                let previousDt = previousTime - olderTime
                func endpointAcceleration(_ now: PixelPoint, _ prior: PixelPoint,
                                  _ older: PixelPoint) -> Double {
                    hypot(Double(now.x - prior.x) / dt - Double(prior.x - older.x) / previousDt,
                          Double(now.y - prior.y) / dt - Double(prior.y - older.y) / previousDt) / dt
                }
                let acceleration = max(endpointAcceleration(endpoints.0, prior.0, older.0),
                                       endpointAcceleration(endpoints.1, prior.1, older.1))
                jumpRatio = max(jumpRatio,
                    acceleration / DebugMotionThresholds.endpointAccelerationPixelsPerSecondSquared)
                if DebugMotionThresholds.predictionEnabled {
                    func residual(_ now: PixelPoint, _ prior: PixelPoint,
                                  _ older: PixelPoint) -> Double {
                        let factor = dt / previousDt
                        return hypot(Double(now.x - prior.x) - Double(prior.x - older.x) * factor,
                                     Double(now.y - prior.y) - Double(prior.y - older.y) * factor)
                    }
                    let error = max(residual(endpoints.0, prior.0, older.0),
                                    residual(endpoints.1, prior.1, older.1))
                    if error > DebugMotionThresholds.predictionErrorPixels {
                        result.append(signal("prediction_error", color, error,
                            DebugMotionThresholds.predictionErrorPixels))
                    }
                }
            }
            if DebugMotionThresholds.jumpEnabled, jumpRatio > 1 {
                result.append(signal("endpoint_jump", color, jumpRatio, 1))
            }
            if DebugMotionThresholds.lengthEnabled {
                let priorLength = distance(prior.0, prior.1)
                let change = abs(distance(endpoints.0, endpoints.1) - priorLength)
                    / max(priorLength, 1)
                if change > DebugMotionThresholds.lengthChangeFraction {
                    result.append(signal("length_change", color, change,
                        DebugMotionThresholds.lengthChangeFraction))
                }
            }
        }
        if DebugMotionThresholds.candidateAmbiguityEnabled {
            if current.eligibleCount >= DebugMotionThresholds.multipleEligibleCandidates {
                result.append(signal("multiple_eligible", color, Double(current.eligibleCount),
                    Double(DebugMotionThresholds.multipleEligibleCandidates)))
            }
            if current.eligibleCount >= 2, let gap = current.topScoreGap,
               gap < DebugMotionThresholds.candidateScoreGap {
                result.append(signal("candidate_ambiguity", color,
                    DebugMotionThresholds.candidateScoreGap - gap,
                    DebugMotionThresholds.candidateScoreGap))
            }
            if current.detected, let previous, previous.detected,
               let currentType = current.selectedCandidateType,
               let previousType = previous.selectedCandidateType,
               (current.candidateSwitch ?? (currentType != previousType
                || (current.selectedCandidateIndex != nil
                    && previous.selectedCandidateIndex != nil
                    && current.selectedCandidateIndex != previous.selectedCandidateIndex))) {
                result.append(signal("candidate_switch", color, 1, 1))
            }
        }
        if DebugMotionThresholds.nearMissEnabled, !current.detected,
           current.candidateCount > 0, current.eligibleCount == 0,
           let margin = current.failedRuleMargin,
           margin <= DebugMotionThresholds.failedRuleMarginFraction {
            result.append(signal("near_miss", color,
                DebugMotionThresholds.failedRuleMarginFraction - margin,
                DebugMotionThresholds.failedRuleMarginFraction))
        }
        return result
    }

    private mutating func record(_ signals: [DebugMotionSignal], color: String,
                                 sample: DebugMotionSample) -> DebugMotionEvent? {
        guard !signals.isEmpty else { return nil }
        let score = signals.reduce(0) { $0 + $1.score }
        guard score >= DebugMotionThresholds.eventScore else { return nil }
        for item in signals {
            signalCounts[item.kind, default: 0] += 1
            signalScoreSums[item.kind, default: 0] += item.score
            signalScoreMaxima[item.kind] = max(signalScoreMaxima[item.kind] ?? 0, item.score)
        }
        if let index = lastEventIndex[color],
           sample.timestamp - events[index].endTime <= DebugMotionThresholds.mergeIntervalSeconds {
            events[index].endFrameID = sample.frameID
            events[index].endTime = sample.timestamp
            events[index].peakScore = max(events[index].peakScore, score)
            for item in signals {
                if let existing = events[index].signals.firstIndex(where: { $0.kind == item.kind }) {
                    if item.score > events[index].signals[existing].score {
                        events[index].signals[existing] = item
                    }
                } else {
                    events[index].signals.append(item)
                }
            }
            return events[index]
        }
        let event = DebugMotionEvent(index: events.count, color: color,
            startFrameID: sample.frameID, endFrameID: sample.frameID,
            startTime: sample.timestamp, endTime: sample.timestamp,
            peakScore: score, signals: signals)
        events.append(event)
        lastEventIndex[color] = event.index
        return event
    }

    private func signal(_ kind: String, _ color: String, _ value: Double,
                        _ threshold: Double) -> DebugMotionSignal {
        let ratio = value / threshold
        let score = kind == "near_miss" || kind == "candidate_ambiguity"
            ? 1 + min(1, max(0, ratio)) : min(4, max(1, ratio))
        return DebugMotionSignal(kind: kind, color: color, value: value,
                                 threshold: threshold, score: score)
    }

    private func aligned(_ points: (PixelPoint, PixelPoint),
                         to reference: (PixelPoint, PixelPoint)) -> (PixelPoint, PixelPoint) {
        let direct = distance(points.0, reference.0) + distance(points.1, reference.1)
        let reversed = distance(points.1, reference.0) + distance(points.0, reference.1)
        return direct <= reversed ? points : (points.1, points.0)
    }

    private func distance(_ a: PixelPoint, _ b: PixelPoint) -> Double {
        hypot(Double(a.x - b.x), Double(a.y - b.y))
    }
}

enum DebugRecordingTriageError: LocalizedError {
    case malformedMetadata(String)
    case unsafeImageName(String)
    case imageMissing(String)
    case bundleAlreadyExists
    case bundleTooLarge

    var errorDescription: String? {
        switch self {
        case .malformedMetadata(let detail): return "triage metadata is invalid: \(detail)"
        case .unsafeImageName(let name): return "unsafe lossless image name: \(name)"
        case .imageMissing(let name): return "lossless image is missing: \(name)"
        case .bundleAlreadyExists: return "triage bundle already exists"
        case .bundleTooLarge: return "selected triage bundle exceeds the size limit"
        }
    }
}

struct DebugRecordingTriageLimits {
    static let trackingEventIndex = 1_000_000_000
    /// Smallest contiguous tracking window (peak plus two frames each side) kept
    /// beside bridge events.
    static let minimumTrackingWindow = 5
    static let defaultImageCount = 12
    static let hardImageCount = 20
    static let perFailureType = 2
    static let contextRadius = 2
    static let maximumBundleBytes: Int64 = 64 * 1024 * 1024
}

enum DebugRecordingLifecyclePolicy {
    static func mayStart(enabled: Bool, cameraRunning: Bool, active: Bool, finalizing: Bool) -> Bool {
        enabled && cameraRunning && !active && !finalizing
    }

    static func mayStop(active: Bool, finalizing: Bool) -> Bool {
        active && !finalizing
    }
}

struct DebugRecordingTriageImage: Equatable {
    let frameIndex: Int
    let frameID: UInt64
    let color: String
    let fileName: String
    let failureType: String
    let priority: Int
    let reasons: [String]
    var eventIndex: Int? = nil
    var role: String? = nil
    var score: Double? = nil
    var signals: [DebugMotionSignal] = []
    var bridge: DebugBridgeImageInfo? = nil
}

/// The before-success / dropout / after-success frames of one bridge dropout are
/// ONE temporal evidence event; the annotated dropout PNG is a derived viewing aid.
struct DebugBridgeImageInfo: Equatable {
    static let roles = ["before_success", "dropout", "after_success"]
    static let annotatedRole = "annotated_dropout"
    let eventID: Int
    let role: String
    var auxiliary: Bool { role == Self.annotatedRole }
    /// Original dropout PNG file name that an annotated image was drawn from.
    let derivedFromFileName: String?
}

struct DebugRecordingTriageSelection: Equatable {
    let images: [DebugRecordingTriageImage]
    let incidentCount: Int
    /// Temporal events left out whole because bridge dropout events took the image budget.
    var bridgePriorityEventIDs: Set<Int> = []
    /// Set when the tracking event was cut to a window around its peak: selected/available.
    var trackingWindow: [String: Int]? = nil
}


/// Stores incident candidates and at most two adjacent context frames while recording.
/// The bound is independent of the streaming metadata file size.
struct DebugRecordingTriageAccumulator {
    static let maximumRetainedFrames = 256
    private(set) var retainedFrames: [DebugRecordingFrameMetadata] = []
    private var retainedIDs: Set<UInt64> = []
    private var recent: [DebugRecordingFrameMetadata] = []
    private var followingContext = 0
    private var identicalRed = 0
    private var identicalBlue = 0

    /// Colors whose anomalies count. Legacy recordings diagnose both.
    var activeColors: Set<String> = ["red", "blue"]
    /// New recordings report absences only through bridge dropout events, so a
    /// missing color is not an incident by itself.
    var absenceIsIncident = true

    mutating func observe(_ frame: DebugRecordingFrameMetadata) {
        let previous = recent.last
        let redActive = activeColors.contains("red")
        let blueActive = activeColors.contains("blue")
        let redIdentical = redActive && identical(frame.red, previous?.red) && frame.redDetectionSucceeded
        let blueIdentical = blueActive && identical(frame.blue, previous?.blue) && frame.blueDetectionSucceeded
        identicalRed = redIdentical ? identicalRed + 1 : 0
        identicalBlue = blueIdentical ? identicalBlue + 1 : 0
        let absence = absenceIsIncident
            && ((redActive && (frame.redDropoutRole != nil
                    || (previous?.redDetectionSucceeded == true && !frame.redDetectionSucceeded)))
                || (blueActive && (frame.blueDropoutRole != nil
                    || (previous?.blueDetectionSucceeded == true && !frame.blueDetectionSucceeded))))
        let incident = frame.manualCaptured || frame.forensicCaptured
            || frame.motionEventIndex != nil
            || absence
            || Self.isCaptureCandidate(frame, activeColors: activeColors,
                                       includeAbsence: absenceIsIncident)
            || (redActive && jump(frame.red, previous?.red) >= 180)
            || (blueActive && jump(frame.blue, previous?.blue) >= 180)
            || identicalRed >= 3 || identicalBlue >= 3
        if incident {
            for context in recent { retain(context) }
            retain(frame)
            followingContext = 2
        } else if followingContext > 0 {
            retain(frame)
            followingContext -= 1
        }
        recent.append(frame)
        if recent.count > 2 { recent.removeFirst() }
    }

    private mutating func retain(_ frame: DebugRecordingFrameMetadata) {
        guard retainedIDs.insert(frame.frameID).inserted else { return }
        retainedFrames.append(frame)
        if retainedFrames.count > Self.maximumRetainedFrames {
            let removable = retainedFrames.firstIndex {
                !$0.manualCaptured && !$0.forensicCaptured
                    && $0.redDropoutFileName == nil && $0.blueDropoutFileName == nil
            } ?? 0
            retainedIDs.remove(retainedFrames.remove(at: removable).frameID)
        }
    }

    static func isCaptureCandidate(_ frame: DebugRecordingFrameMetadata) -> Bool {
        isCaptureCandidate(frame, activeColors: ["red", "blue"], includeAbsence: true)
    }

    /// `includeAbsence == false` ignores candidate=0 / eligible=0, which only
    /// say that a saber was not found, and keeps the suspicious-geometry checks.
    static func isCaptureCandidate(_ frame: DebugRecordingFrameMetadata,
                                   activeColors: Set<String>, includeAbsence: Bool) -> Bool {
        (activeColors.contains("red") && suspicious(frame.candidateDiagnostics?.red, includeAbsence: includeAbsence))
            || (activeColors.contains("blue") && suspicious(frame.candidateDiagnostics?.blue, includeAbsence: includeAbsence))
    }

    private static func suspicious(_ diagnostics: DebugRecordingColorCandidates?,
                                   includeAbsence: Bool) -> Bool {
        guard let diagnostics else { return false }
        if includeAbsence,
           diagnostics.totalCandidateCount == 0 || diagnostics.eligibleCandidateCount == 0 { return true }
        guard let candidate = diagnostics.selectedCandidate else { return false }
        let raw = candidate.rawPCASpan
        let robust = candidate.robustMainIntervalLength
        let tail = raw >= robust * 2 && raw - robust >= 80
        let broad = raw >= 180 && candidate.scoreBreakdown.coreSupport <= 4.2
            && candidate.scoreBreakdown.longitudinalHighCoverage <= 2
        return (candidate.sourceType == "core-line" && (raw >= 180 || tail)) || broad
    }

    private func identical(_ lhs: DebugRecordingDetection, _ rhs: DebugRecordingDetection?) -> Bool {
        guard let rhs, lhs.detected, rhs.detected else { return false }
        return lhs.x1 == rhs.x1 && lhs.y1 == rhs.y1 && lhs.x2 == rhs.x2 && lhs.y2 == rhs.y2
    }

    private func jump(_ lhs: DebugRecordingDetection, _ rhs: DebugRecordingDetection?) -> Double {
        guard let rhs, let a = lhs.endpoints, let b = rhs.endpoints else { return 0 }
        func distance(_ x: PixelPoint, _ y: PixelPoint) -> Double {
            hypot(Double(x.x - y.x), Double(x.y - y.y))
        }
        return min(max(distance(a.0, b.0), distance(a.1, b.1)),
                   max(distance(a.0, b.1), distance(a.1, b.0)))
    }
}

/// Builds a compact, image-only diagnostic bundle after recording has stopped.
/// The H.264 files and full metadata are never copied into this directory.
enum DebugRecordingTriageBuilder {
    private static let colors = ["red", "blue"]

    private struct Reason: Equatable {
        let color: String
        let type: String
        let priority: Int
        let detail: String
    }

    private struct ImageReference {
        let frameIndex: Int
        let frameID: UInt64
        let color: String
        let fileName: String
        let isManual: Bool
        var eventIndex: Int? = nil
        var role: String? = nil
        var score: Double? = nil
        var signals: [DebugMotionSignal] = []
    }

    /// Colors whose failures count. Metadata without `activeColors` predates
    /// diagnostic color selection and diagnoses both colors as before.
    static func activeColorSet(_ metadata: [String: Any]) -> Set<String> {
        guard let names = metadata["activeColors"] as? [String] else { return Set(colors) }
        return Set(names).intersection(colors)
    }

    private static func inDiagnosticWindow(_ metadata: [String: Any], color: String,
                                           timestamp: Double) -> Bool {
        guard let windows = metadata["diagnosticWindows"] as? [String: Any],
              let window = windows[color] as? [String: Any],
              let first = number(window["firstSuccessTime"]),
              let last = number(window["lastSuccessTime"]) else { return false }
        return timestamp >= first && timestamp <= last
    }

    static func selectImages(
        metadataData: Data,
        forensicDirectoryURL: URL?,
        maximumImages: Int = DebugRecordingTriageLimits.defaultImageCount,
        perFailureType: Int = DebugRecordingTriageLimits.perFailureType
    ) throws -> (metadata: [String: Any], selection: DebugRecordingTriageSelection) {
        let metadata = try decodeMetadata(metadataData)
        let frames = metadata["frames"] as! [[String: Any]]
        let directory = forensicDirectoryURL
        // New recordings report absence only through bridge events bounded by a
        // success on both sides; the legacy absence reasons stay for old files.
        let boundedAbsence = metadata["activeColors"] != nil
        let active = activeColorSet(metadata)
        var reasonsByFrame: [[Reason]] = Array(repeating: [], count: frames.count)
        var references: [ImageReference] = []

        for (index, frame) in frames.enumerated() {
            let frameID = integer(frame["frameID"]) ?? UInt64(index)
            if let manual = string(frame["manualFileName"]), frame["manualCaptured"] as? Bool == true {
                references.append(ImageReference(frameIndex: index, frameID: frameID,
                                                 color: "both", fileName: manual, isManual: true))
                reasonsByFrame[index].append(Reason(color: "both", type: "manual_capture",
                                                    priority: 0, detail: "manual lossless capture"))
            }

            for color in colors where active.contains(color) && !boundedAbsence {
                let dropoutRole = string(frame["\(color)DropoutRole"])
                let dropoutFile = string(frame["\(color)DropoutFileName"])
                if let dropoutFile, let dropoutRole,
                   ["dropout", "last-detected-before-dropout"].contains(dropoutRole) {
                    references.append(ImageReference(frameIndex: index, frameID: frameID,
                                                     color: color, fileName: dropoutFile, isManual: false))
                }
                if dropoutRole == "dropout" || isFirstFalseAfterDetection(frames, index: index, color: color) {
                    reasonsByFrame[index].append(Reason(color: color, type: "dropout",
                                                        priority: 1,
                                                        detail: dropoutRole == "dropout"
                                                            ? "first false frame after a detected run"
                                                            : "fresh detection changed from true to false"))
                }
                if let dropoutRole, dropoutRole == "last-detected-before-dropout" {
                    reasonsByFrame[index].append(Reason(color: color, type: "dropout",
                                                        priority: 2,
                                                        detail: "last true frame before dropout"))
                }
            }

            if frame["forensicCaptured"] as? Bool == true,
               let forensic = string(frame["forensicFileName"]) {
                references.append(ImageReference(frameIndex: index, frameID: frameID,
                                                 color: "both", fileName: forensic, isManual: false))
            }

            for color in colors where active.contains(color) {
                var found = reasons(for: frames, at: index, color: color,
                                    includeAbsence: !boundedAbsence)
                if boundedAbsence, !inDiagnosticWindow(
                    metadata, color: color,
                    timestamp: number(frame["presentationTimeSeconds"]) ?? -1) {
                    // Before the first or after the last success nothing can be
                    // attributed to recognition.
                    found = []
                }
                reasonsByFrame[index].append(contentsOf: found)
            }
        }

        if let motionEvents = metadata["motionEvents"] as? [[String: Any]] {
            let indexByID = Dictionary(uniqueKeysWithValues: frames.enumerated().compactMap {
                index, frame -> (UInt64, Int)? in
                guard let id = integer(frame["frameID"]) else { return nil }
                return (id, index)
            })
            for event in motionEvents {
                guard let eventIndex = event["eventIndex"] as? Int,
                      let color = string(event["color"]),
                      let score = number(event["peakScore"]),
                      let images = event["images"] as? [[String: Any]] else { continue }
                if !active.contains(color) && color != "both" { continue }
                let signals = (event["signals"] as? [[String: Any]] ?? []).compactMap {
                    signal -> DebugMotionSignal? in
                    guard let kind = string(signal["kind"]),
                          let signalColor = string(signal["color"]),
                          let value = number(signal["value"]),
                          let threshold = number(signal["threshold"]),
                          let signalScore = number(signal["score"]) else { return nil }
                    return DebugMotionSignal(kind: kind, color: signalColor,
                        value: value, threshold: threshold, score: signalScore)
                }
                // Presence/absence signals are covered by bridge events, which
                // require a success on both sides.
                if boundedAbsence, !signals.isEmpty,
                   signals.allSatisfy({ ["dropout", "flicker"].contains($0.kind) }) { continue }
                for image in images {
                    guard let frameID = integer(image["frameID"]),
                          let frameIndex = indexByID[frameID],
                          let fileName = string(image["fileName"]),
                          let role = string(image["role"]),
                          ["event_pre", "event_at", "event_post", "before", "onset", "peak", "after", "recovery"].contains(role) else { continue }
                    references.append(ImageReference(frameIndex: frameIndex,
                        frameID: frameID, color: color, fileName: fileName,
                        isManual: false, eventIndex: eventIndex, role: role,
                        score: score, signals: signals))
                }
            }
        }

        addIdenticalEndpointReasons(frames: frames, reasonsByFrame: &reasonsByFrame, active: active)

        let incidentCount = incidentSummary(metadata: metadata).reduce(0) {
            $0 + Int(integer($1["incidentCount"]) ?? 0)
        }
        let hasMotionEvents = !(metadata["motionEvents"] as? [[String: Any]] ?? []).isEmpty
        let ceiling = hasMotionEvents ? DebugRecordingTriageLimits.defaultImageCount
            : DebugRecordingTriageLimits.hardImageCount
        let requestedLimit = max(0, min(maximumImages, ceiling))
        let failureLimit = max(0, min(perFailureType, DebugRecordingTriageLimits.hardImageCount))

        let existingReferences = try references.map { reference -> ImageReference in
            guard safePNGName(reference.fileName) else {
                throw DebugRecordingTriageError.unsafeImageName(reference.fileName)
            }
            guard let directory,
                  FileManager.default.fileExists(
                    atPath: directory.appendingPathComponent(reference.fileName).path
                  ) else {
                throw DebugRecordingTriageError.imageMissing(reference.fileName)
            }
            return reference
        }
        var uniqueByName: [String: ImageReference] = [:]
        for reference in existingReferences {
            if uniqueByName[reference.fileName] == nil { uniqueByName[reference.fileName] = reference }
        }

        var selected: [DebugRecordingTriageImage] = []
        var countByType: [String: Int] = [:]
        var selectedBytes: Int64 = 0
        // Leave room for the compact contexts, summary and prompt under the
        // unchanged 64 MiB transport budget. Selection is performed after Stop.
        let imageBudget = DebugRecordingTriageLimits.maximumBundleBytes - 2 * 1_024 * 1_024

        // A tracking-instability event that actually shows instability (event score at
        // or above the diagnostic event threshold) is never crowded out by bridge
        // events: it keeps at least a five-frame window around its peak.
        let trackingReferences = uniqueByName.values.filter {
            $0.eventIndex == DebugRecordingTriageLimits.trackingEventIndex
        }.sorted { $0.frameID < $1.frameID }
        let trackingSignificant = (trackingReferences.first?.score ?? 0) >= DebugMotionThresholds.eventScore
        let bridgeImageBudget = trackingSignificant
            ? requestedLimit - min(trackingReferences.count, DebugRecordingTriageLimits.minimumTrackingWindow)
            : requestedLimit

        // Bridge events are complete temporal units: all three originals or nothing.
        if boundedAbsence, let directory {
            let indexByID = Dictionary(uniqueKeysWithValues: frames.enumerated().compactMap {
                index, frame -> (UInt64, Int)? in
                guard let id = integer(frame["frameID"]) else { return nil }
                return (id, index)
            })
            let events = (metadata["bridgeDropoutEvents"] as? [[String: Any]] ?? []).sorted {
                (number($0["gapSeconds"]) ?? 0) > (number($1["gapSeconds"]) ?? 0)
            }
            var taken = 0
            for event in events where taken < DebugBridgeThresholds.maximumEvents {
                guard let unit = bridgeUnit(event, active: active, indexByID: indexByID,
                                            directory: directory) else { continue }
                let bytes = unit.reduce(Int64(0)) { $0 + $1.1 }
                guard selected.count + unit.count <= bridgeImageBudget,
                      selectedBytes + bytes <= imageBudget else { continue }
                selected.append(contentsOf: unit.map(\.0))
                selectedBytes += bytes
                taken += 1
            }
        }
        let bridgeFrameIDs = Set(selected.map(\.frameID))

        // The tracking event is a temporal unit as well: a contiguous window around its
        // peak (all eleven frames when they fit), never a scattered subset.
        let slots = requestedLimit - selected.count
        let trackingCount = trackingReferences.count
        var trackingWindow: Set<UInt64>?
        var trackingWindowInfo: [String: Int]?
        if trackingCount > 0, trackingCount > slots, trackingSignificant, slots >= 3 {
            let size = min(trackingCount, slots)
            let peak = trackingReferences.firstIndex { $0.role == "peak" } ?? trackingCount / 2
            var lower = peak - (size - 1) / 2
            lower = max(0, min(lower, trackingCount - size))
            trackingWindow = Set(trackingReferences[lower..<lower + size].map(\.frameID))
            trackingWindowInfo = ["selected": size, "available": trackingCount]
        }
        let trackingFits = trackingCount <= slots || trackingWindow != nil
        let bridgePriority: Set<Int> = (!trackingFits && !selected.isEmpty && trackingCount > 0)
            ? [DebugRecordingTriageLimits.trackingEventIndex] : []

        let sortedReferences = uniqueByName.values.filter {
            ($0.eventIndex != DebugRecordingTriageLimits.trackingEventIndex
                || (trackingFits && (trackingWindow?.contains($0.frameID) ?? true)))
                && !bridgeFrameIDs.contains($0.frameID)
        }.sorted { lhs, rhs in
            if (lhs.eventIndex != nil) != (rhs.eventIndex != nil) {
                return lhs.eventIndex != nil
            }
            if let leftScore = lhs.score, let rightScore = rhs.score,
               leftScore != rightScore { return leftScore > rightScore }
            if lhs.eventIndex != rhs.eventIndex {
                return (lhs.eventIndex ?? Int.max) < (rhs.eventIndex ?? Int.max)
            }
            if lhs.signals.contains(where: { $0.kind == "tracking_instability" }),
               rhs.signals.contains(where: { $0.kind == "tracking_instability" }),
               lhs.eventIndex == rhs.eventIndex {
                return lhs.frameID < rhs.frameID
            }
            let roles = ["event_at": 0, "event_pre": 1, "event_post": 2]
            if lhs.role != rhs.role {
                return (roles[lhs.role ?? ""] ?? 3) < (roles[rhs.role ?? ""] ?? 3)
            }
            let leftReasons = reasonList(for: lhs, reasonsByFrame: reasonsByFrame)
            let rightReasons = reasonList(for: rhs, reasonsByFrame: reasonsByFrame)
            let leftPriority = leftReasons.map(\.priority).min() ?? (lhs.isManual ? 0 : 7)
            let rightPriority = rightReasons.map(\.priority).min() ?? (rhs.isManual ? 0 : 7)
            if leftPriority != rightPriority { return leftPriority < rightPriority }
            if lhs.frameIndex != rhs.frameIndex { return lhs.frameIndex < rhs.frameIndex }
            return lhs.fileName < rhs.fileName
        }

        for reference in sortedReferences {
            guard selected.count < requestedLimit else { break }
            if selected.contains(where: { $0.frameID == reference.frameID }) { continue }
            let referenceReasons = reasonList(for: reference, reasonsByFrame: reasonsByFrame)
            let primary = referenceReasons.sorted {
                if $0.priority != $1.priority { return $0.priority < $1.priority }
                return $0.type < $1.type
            }.first ?? Reason(color: reference.color, type: "lossless_anomaly",
                              priority: 7, detail: "recorder forensic capture")
            let buckets = Set(referenceReasons.map(\.type)).union([primary.type])
            guard reference.eventIndex != nil || reference.isManual
                || buckets.allSatisfy({ countByType[$0, default: 0] < failureLimit }) else { continue }
            let duplicatesNearby = selected.contains { item in
                buckets.contains(item.failureType)
                    && item.color == reference.color
                    && abs(item.frameIndex - reference.frameIndex) <= 3
                    && item.fileName != reference.fileName
            }
            guard reference.eventIndex != nil || reference.isManual || !duplicatesNearby else { continue }

            let eventType = reference.eventIndex.map { index in
                "motion_event_\(index)_\(reference.signals.first?.kind ?? "anomaly")"
            }
            let eventReasons = reference.signals.map {
                "\($0.kind) \($0.value) threshold \($0.threshold) score \($0.score)"
            }

            let attributes = try FileManager.default.attributesOfItem(
                atPath: directory!.appendingPathComponent(reference.fileName).path)
            let size = (attributes[.size] as? NSNumber)?.int64Value ?? 0
            guard selectedBytes + size <= imageBudget else { continue }
            selectedBytes += size
            selected.append(DebugRecordingTriageImage(
                frameIndex: reference.frameIndex,
                frameID: reference.frameID,
                color: reference.color,
                fileName: reference.fileName,
                failureType: eventType ?? primary.type,
                priority: primary.priority,
                reasons: reference.eventIndex == nil
                    ? Array(Set(referenceReasons.map(\.detail))).sorted()
                    : ["\(reference.role ?? "event_at") for event \(reference.eventIndex!)"] + eventReasons,
                eventIndex: reference.eventIndex, role: reference.role,
                score: reference.score, signals: reference.signals
            ))
            if !reference.isManual && reference.eventIndex == nil {
                for bucket in buckets { countByType[bucket, default: 0] += 1 }
            }
        }

        let motionSummary = metadata["motionSummary"] as? [String: Any]
        let motionCount = (motionSummary?["events"] as? [[Any]])?.count ?? 0
        return (metadata, DebugRecordingTriageSelection(images: selected,
            incidentCount: incidentCount + motionCount, bridgePriorityEventIDs: bridgePriority,
            trackingWindow: trackingWindowInfo))
    }

    static func build(
        metadataData: Data,
        metadataURL: URL,
        forensicDirectoryURL: URL?,
        recordedFrameCount: Int? = nil,
        maximumImages: Int = DebugRecordingTriageLimits.defaultImageCount
    ) throws -> URL {
        let (metadata, selection) = try selectImages(
            metadataData: metadataData, forensicDirectoryURL: forensicDirectoryURL,
            maximumImages: maximumImages
        )
        guard let sessionID = string(metadata["sessionID"]), !sessionID.isEmpty else {
            throw DebugRecordingTriageError.malformedMetadata("sessionID is missing")
        }
        let active = activeColorSet(metadata)

        let bundleURL = metadataURL.deletingLastPathComponent()
            .appendingPathComponent("phone_saber_triage_\(safeSessionName(sessionID))",
                                    isDirectory: true)
        guard !FileManager.default.fileExists(atPath: bundleURL.path) else {
            throw DebugRecordingTriageError.bundleAlreadyExists
        }
        let imageDirectory = bundleURL.appendingPathComponent("images", isDirectory: true)
        let frameDirectory = bundleURL.appendingPathComponent("frames", isDirectory: true)
        try FileManager.default.createDirectory(at: imageDirectory, withIntermediateDirectories: true)
        try FileManager.default.createDirectory(at: frameDirectory, withIntermediateDirectories: true)

        do {
            var totalBytes: Int64 = 0
            var imageEntries: [[String: Any]] = []
            for (offset, item) in selection.images.enumerated() {
                guard let forensicDirectoryURL,
                      safePNGName(item.fileName) else {
                    throw DebugRecordingTriageError.unsafeImageName(item.fileName)
                }
                let sourceURL = forensicDirectoryURL.appendingPathComponent(item.fileName)
                guard FileManager.default.fileExists(atPath: sourceURL.path) else {
                    throw DebugRecordingTriageError.imageMissing(item.fileName)
                }
                let imageAttributes = try FileManager.default.attributesOfItem(atPath: sourceURL.path)
                let imageSize = (imageAttributes[.size] as? NSNumber)?.int64Value ?? 0
                totalBytes += imageSize
                guard totalBytes <= DebugRecordingTriageLimits.maximumBundleBytes else {
                    throw DebugRecordingTriageError.bundleTooLarge
                }
                let targetName = String(format: "image_%02d_%@", offset + 1, item.fileName)
                try FileManager.default.copyItem(at: sourceURL,
                                                 to: imageDirectory.appendingPathComponent(targetName))

                var framePayload = contextPayload(metadata: metadata, selected: item, active: active)
                framePayload["imageMapping"] = ["sessionID": sessionID,
                    "frameID": item.frameID,
                    "timestamp": number(frames(metadata)[item.frameIndex]["presentationTimeSeconds"]) ?? 0,
                    "color": item.color, "image": "images/\(targetName)",
                    "eventID": item.bridge.map { "bridge_\($0.eventID)" }
                        ?? item.eventIndex.map { String($0) } ?? "none",
                    "eventRole": item.bridge?.role ?? item.role ?? "single"]
                let frameName = "frame_\(item.frameID)_\(offset + 1).json"
                try jsonData(framePayload, compact: true).write(
                    to: frameDirectory.appendingPathComponent(frameName), options: .atomic)
                var imageEntry: [String: Any] = [
                    "path": "images/\(targetName)", "frameContextPath": "frames/\(frameName)", "sessionID": sessionID,
                    "frameID": item.frameID, "timestamp": number(frames(metadata)[item.frameIndex]["presentationTimeSeconds"]) ?? 0,
                    "color": item.color, "failureType": item.failureType,
                    "reason": item.reasons.joined(separator: "; "), "sourceFile": item.fileName
                ]
                if let eventIndex = item.eventIndex, let role = item.role,
                   let score = item.score {
                    imageEntry["eventIndex"] = eventIndex
                    imageEntry["role"] = role
                    imageEntry["anomalyScore"] = score
                    imageEntry["signals"] = item.signals.map(\.dictionary)
                    imageEntry["signalAggregation"] = "event_max_per_kind"
                }
                if let bridge = item.bridge {
                    imageEntry["bridgeEventID"] = bridge.eventID
                    imageEntry["role"] = bridge.role
                    imageEntry["evidenceUnit"] = "bridge_dropout_\(bridge.eventID)"
                    imageEntry["auxiliary"] = bridge.auxiliary
                    if let derived = bridge.derivedFromFileName,
                       let original = selection.images.firstIndex(where: {
                           $0.fileName == derived && $0.bridge?.eventID == bridge.eventID }) {
                        imageEntry["derivedFromImage"] = String(
                            format: "images/image_%02d_%@", original + 1, derived)
                    }
                }
                imageEntries.append(imageEntry)
            }

            let incidentEntries = incidentSummary(metadata: metadata)
            var motionSummary = metadata["motionSummary"] as? [String: Any] ?? [:]
            if var events = motionSummary["events"] as? [[Any]] {
                let selectedIDs = Set(selection.images.compactMap(\.eventIndex))
                let retainedEvents = (metadata["motionEvents"] as? [[String: Any]] ?? [])
                let retainedIDs = Set(retainedEvents.compactMap { $0["eventIndex"] as? Int })
                for index in events.indices {
                    guard events[index].count == 4,
                          let eventID = events[index][0] as? Int else { continue }
                    if selectedIDs.contains(eventID) {
                        events[index][3] = "selected"
                    } else if selection.bridgePriorityEventIDs.contains(eventID) {
                        events[index][3] = "bridge_priority"
                    } else if retainedIDs.contains(eventID) {
                        let retained = retainedEvents.first {
                            $0["eventIndex"] as? Int == eventID
                        }
                        let imageIDs = (retained?["images"] as? [[String: Any]] ?? [])
                            .compactMap { integer($0["frameID"]) }
                        let alreadySelected = Set(selection.images.map(\.frameID))
                        events[index][3] = imageIDs.allSatisfy {
                            alreadySelected.contains($0)
                        } ? "duplicate" : (selection.images.count >= maximumImages
                            ? "image_limit" : "byte_limit")
                    }
                }
                motionSummary["events"] = events
            }
            var summary: [String: Any] = [
                "formatVersion": 1,
                "sessionID": sessionID,
                "recordedFrameCount": recordedFrameCount ?? frames(metadata).count,
                "retainedIncidentContextFrames": frames(metadata).count,
                "summaryScope": "retained incident candidates and nearby context",
                "redBlueDetectionSummary": detectionSummary(metadata: metadata),
                "dropoutSummary": dropoutSummary(metadata: metadata),
                "selectedImageCount": selection.images.count,
                "incidentCount": selection.incidentCount,
                "incidents": incidentEntries,
                "images": imageEntries,
                "limits": [
                    "defaultMaxImages": DebugRecordingTriageLimits.defaultImageCount,
                    "hardMaxImages": DebugRecordingTriageLimits.hardImageCount,
                    "perFailureTypeMax": DebugRecordingTriageLimits.perFailureType,
                    "contextRadiusFrames": DebugRecordingTriageLimits.contextRadius
                ],
                "groundTruth": "lossless PNG only; H.264 video is excluded"
            ]
            if !motionSummary.isEmpty { summary["motionEventSummary"] = motionSummary }
            if metadata["activeColors"] != nil {
                summary["activeColors"] = colors.filter { active.contains($0) }
                var bridge = metadata["bridgeDropoutSummary"] as? [String: Any] ?? [:]
                bridge["selectedEventIDs"] = Array(Set(selection.images.compactMap { $0.bridge?.eventID })).sorted()
                if let window = selection.trackingWindow { bridge["trackingWindow"] = window }
                summary["bridgeDropoutSummary"] = bridge
            }
            try jsonData(summary, compact: true).write(
                to: bundleURL.appendingPathComponent("summary.json"), options: .atomic)
            try promptText(sessionID: sessionID, imageCount: selection.images.count)
                .write(to: bundleURL.appendingPathComponent("prompt.md"), atomically: true,
                       encoding: .utf8)
            guard DebugRecordingStorage.diskUsage(at: bundleURL)
                <= DebugRecordingTriageLimits.maximumBundleBytes else {
                throw DebugRecordingTriageError.bundleTooLarge
            }
            return bundleURL
        } catch {
            try? FileManager.default.removeItem(at: bundleURL)
            throw error
        }
    }

    private static func decodeMetadata(_ data: Data) throws -> [String: Any] {
        let object: Any
        do { object = try JSONSerialization.jsonObject(with: data) }
        catch { throw DebugRecordingTriageError.malformedMetadata(error.localizedDescription) }
        guard let metadata = object as? [String: Any],
              string(metadata["sessionID"]) != nil,
              let frameValues = metadata["frames"] as? [Any],
              frameValues.allSatisfy({ $0 is [String: Any] }) else {
            throw DebugRecordingTriageError.malformedMetadata("expected sessionID and frames[] of objects")
        }
        for (index, frameValue) in frameValues.enumerated() {
            guard let frame = frameValue as? [String: Any],
                  number(frame["presentationTimeSeconds"]) != nil else {
                throw DebugRecordingTriageError.malformedMetadata("frame \(index) has no timestamp")
            }
        }
        return metadata
    }

    private static func reasons(for frames: [[String: Any]], at index: Int,
                                color: String, includeAbsence: Bool = true) -> [Reason] {
        let frame = frames[index]
        let diagnostic = colorDiagnostics(frame, color: color)
        let candidate = selectedCandidate(frame, color: color)
        let candidateCount = integer(diagnostic["totalCandidateCount"])
        let eligibleCount = integer(diagnostic["eligibleCandidateCount"])
        var result: [Reason] = []

        if !includeAbsence {
            // candidate=0 / eligible=0 only say the saber was not found.
        } else if candidateCount == 0 {
            result.append(Reason(color: color, type: "candidate_zero", priority: 5,
                                 detail: "candidate=0"))
        } else if let candidateCount, candidateCount > 0, eligibleCount == 0 {
            result.append(Reason(color: color, type: "eligible_zero", priority: 4,
                                 detail: "candidate exists but eligibleCandidateCount=0"))
        }

        if let jump = endpointJumpAt(frames, index: index, color: color), jump >= 180 {
            result.append(Reason(color: color, type: "endpoint_jump_ge180", priority: 3,
                                 detail: String(format: "endpoint jump >=180 px (%.1f px)", jump)))
            if jump >= 260 {
                result.append(Reason(color: color, type: "endpoint_jump_ge260", priority: 2,
                                     detail: String(format: "endpoint jump >=260 px (%.1f px)", jump)))
            }
        }

        if let candidate {
            let source = string(candidate["sourceType"])
                ?? string(diagnostic["selectedCandidateType"]) ?? ""
            let raw = number(candidate["rawPCASpan"])
            let robust = number(candidate["robustMainIntervalLength"])
            let breakdown = candidate["scoreBreakdown"] as? [String: Any] ?? [:]
            if source == "core-line", let raw,
               raw >= 180 || rawRobustDivergence(raw: raw, robust: robust) {
                result.append(Reason(color: color, type: "core_line_tail_suspect", priority: 6,
                                     detail: "long/raw-tail core-line candidate"))
            }
            if let raw, rawRobustDivergence(raw: raw, robust: robust) {
                result.append(Reason(color: color, type: "raw_robust_span_divergence", priority: 6,
                                     detail: "raw PCA span diverges from robust interval"))
            }
            let support = number(breakdown["coreSupport"])
            let highCoverage = number(breakdown["longitudinalHighCoverage"])
            if let raw, raw >= 180, let support, support <= 4.2,
               let highCoverage, highCoverage <= 2.0 {
                result.append(Reason(color: color, type: "broad_coreless_emitter_suspect", priority: 7,
                                     detail: "wide candidate with weak core and high-brightness support"))
            }
        }
        return result
    }

    private static func addIdenticalEndpointReasons(frames: [[String: Any]],
                                                    reasonsByFrame: inout [[Reason]],
                                                    active: Set<String> = Set(colors)) {
        for color in colors where active.contains(color) {
            var start: Int?
            for index in 0...frames.count {
                let same = index > 0 && index < frames.count
                    && areAdjacent(frames[index - 1], frames[index])
                    && isDetectionSuccess(frames[index], color: color)
                    && endpointTuple(frames[index], color: color) != nil
                    && endpointTuple(frames[index], color: color)
                        == endpointTuple(frames[index - 1], color: color)
                if index == 0 {
                    start = endpointTuple(frames.first ?? [:], color: color) == nil ? nil : 0
                } else if same {
                    continue
                } else {
                    if let first = start, index - first >= 4 {
                        for frameIndex in first..<index {
                            reasonsByFrame[frameIndex].append(Reason(
                                color: color, type: "identical_endpoint_suspect", priority: 7,
                                detail: "identical endpoint for \(index - first) consecutive frames"
                            ))
                        }
                    }
                    start = index < frames.count && endpointTuple(frames[index], color: color) != nil
                        ? index : nil
                }
            }
        }
    }

    private static func fileSize(_ directory: URL, _ name: String) -> Int64? {
        guard let attributes = try? FileManager.default.attributesOfItem(
            atPath: directory.appendingPathComponent(name).path) else { return nil }
        return (attributes[.size] as? NSNumber)?.int64Value
    }

    /// before_success, dropout, annotated_dropout (optional), after_success for one
    /// event, or nil when any original is missing; never a partial event.
    private static func bridgeUnit(_ event: [String: Any], active: Set<String>,
                                   indexByID: [UInt64: Int],
                                   directory: URL) -> [(DebugRecordingTriageImage, Int64)]? {
        guard let id = integer(event["eventID"]).map({ Int($0) }), let color = string(event["color"]),
              active.contains(color), let images = event["images"] as? [[String: Any]] else { return nil }
        let gap = number(event["gapSeconds"]) ?? 0
        var result: [(DebugRecordingTriageImage, Int64)] = []
        for role in DebugBridgeImageInfo.roles {
            guard let image = images.first(where: { string($0["role"]) == role }),
                  let frameID = integer(image["frameID"]), let frameIndex = indexByID[frameID],
                  let fileName = string(image["fileName"]), safePNGName(fileName),
                  let size = fileSize(directory, fileName) else { return nil }
            func entry(_ name: String, _ imageRole: String, derived: String?) -> DebugRecordingTriageImage {
                DebugRecordingTriageImage(
                    frameIndex: frameIndex, frameID: frameID, color: color, fileName: name,
                    failureType: "bridge_dropout_\(id)", priority: 1,
                    reasons: ["bridge dropout event \(id) \(imageRole)",
                              String(format: "same %@ saber lost for %.3f s between two successful detections", color, gap)],
                    bridge: DebugBridgeImageInfo(eventID: id, role: imageRole, derivedFromFileName: derived))
            }
            result.append((entry(fileName, role, derived: nil), size))
            if role == "dropout", let annotated = string(image["annotatedFileName"]),
               safePNGName(annotated), let annotatedSize = fileSize(directory, annotated) {
                result.append((entry(annotated, DebugBridgeImageInfo.annotatedRole, derived: fileName),
                               annotatedSize))
            }
        }
        return result
    }

    private static func reasonList(for reference: ImageReference,
                                   reasonsByFrame: [[Reason]]) -> [Reason] {
        let sameFrame = reasonsByFrame[reference.frameIndex]
        let matching = sameFrame.filter { reference.color == "both" || $0.color == reference.color || $0.color == "both" }
        return matching
    }

    /// Consumers reject a context above 32 KiB; stay well below it.
    private static let contextByteBudget = 24 * 1_024

    private static func contextPayload(metadata: [String: Any],
                                       selected: DebugRecordingTriageImage,
                                       active: Set<String>) -> [String: Any] {
        let allFrames = frames(metadata)
        let radius = DebugRecordingTriageLimits.contextRadius
        let geometry = geometryTable(metadata)
        var indexes: [Int]
        if let bridge = selected.bridge {
            // Bridge contexts use real frame-ID neighbours, not neighbouring retained frames.
            indexes = bridge.auxiliary ? [selected.frameIndex] : allFrames.indices.filter {
                guard let id = integer(allFrames[$0]["frameID"]) else { return false }
                return abs(Int64(id) - Int64(selected.frameID)) <= Int64(radius)
            }
        } else {
            let lower = max(0, selected.frameIndex - radius)
            let upper = min(allFrames.count - 1, selected.frameIndex + radius)
            indexes = Array(lower...upper)
        }
        let auxiliary = selected.bridge?.auxiliary == true
        /// Neighbour candidate geometry: 0 = leading three eligible, 1 = winner only, 2 = none.
        func makePayload(_ indexes: [Int], neighbourLevel: Int,
                         selectedLimits: (eligible: Int, ineligible: Int, breakdown: Int)) -> [String: Any] {
            var result: [String: Any] = [
                "sessionID": string(metadata["sessionID"]) ?? "",
                "selectedFrameID": selected.frameID,
                "selectedColor": selected.color,
                "selectedFailureType": selected.failureType,
                "selectedReasons": selected.reasons,
                "contextRadiusFrames": radius,
                "frames": indexes.map { index -> [String: Any] in
                    let isSelected = index == selected.frameIndex
                    let limits: (Int, Int, Int)? = auxiliary ? nil
                        : (isSelected ? (selectedLimits.eligible, selectedLimits.ineligible, selectedLimits.breakdown)
                            : (neighbourLevel == 0 ? (3, 2, 3) : (neighbourLevel == 1 ? (1, 0, 1) : nil)))
                    return minimalFrame(allFrames[index], index: index,
                        includeTracking: isSelected, isSelected: isSelected, active: active,
                        geometry: geometry, geometryLimits: limits)
                }
            ]
            if metadata["activeColors"] != nil {
                result["activeColors"] = colors.filter { active.contains($0) }
            }
            if let eventIndex = selected.eventIndex, let role = selected.role,
               let score = selected.score {
                result["motionEvent"] = ["eventIndex": eventIndex, "role": role,
                    "score": score, "signals": selected.signals.map(\.dictionary),
                    "signalAggregation": "event_max_per_kind"]
            }
            if let bridge = selected.bridge,
               let event = (metadata["bridgeDropoutEvents"] as? [[String: Any]])?.first(where: {
                   integer($0["eventID"]).map { Int($0) } == bridge.eventID }) {
                result["bridgeEvent"] = bridgeContext(event, role: bridge.role,
                                                      auxiliary: bridge.auxiliary,
                                                      selectedFrameID: selected.frameID)
            }
            if let transmissions = metadata["udpTransmissions"] as? [[String: Any]] {
                result["udpTransmissions"] = transmissions.filter {
                    integer($0["frameID"]) == selected.frameID
                }
            }
            return result
        }
        var selectedLimits = (eligible: DebugCandidateGeometrySet.eligibleLimit,
                              ineligible: DebugCandidateGeometrySet.ineligibleLimit, breakdown: Int.max)
        var payload = makePayload(indexes, neighbourLevel: 0, selectedLimits: selectedLimits)
        func size(_ value: [String: Any]) -> Int {
            (try? jsonData(value, compact: true).count) ?? Int.max
        }
        // Reduce detail progressively, always saying so. The selected frame keeps
        // its temporal mapping, candidate trace, all eligible candidate geometry,
        // endpoint pipeline and tracking.
        var steps: [String] = []
        var level = 0
        for step in ["neighbourCandidateGeometryWinnerOnly", "neighbourCandidateGeometryDropped",
                     "selectedScoreBreakdownLimitedToLeadingEligible", "selectedIneligibleLimitedToTwo",
                     "selectedEligibleLimitedToEight",
                     "neighbourFramesWithin1", "neighbourFramesWithin0"] where size(payload) > contextByteBudget {
            switch step {
            case "neighbourCandidateGeometryWinnerOnly": level = 1
            case "neighbourCandidateGeometryDropped": level = 2
            case "selectedScoreBreakdownLimitedToLeadingEligible": selectedLimits.breakdown = 4
            case "selectedIneligibleLimitedToTwo": selectedLimits.ineligible = 2
            case "selectedEligibleLimitedToEight": selectedLimits.eligible = 8
            case "neighbourFramesWithin1": indexes = indexes.filter { abs($0 - selected.frameIndex) <= 1 }
            default: indexes = indexes.filter { $0 == selected.frameIndex }
            }
            payload = makePayload(indexes, neighbourLevel: level, selectedLimits: selectedLimits)
            steps.append(step)
        }
        if !steps.isEmpty { payload["compaction"] = steps }
        return payload
    }

    // MARK: Candidate geometry in contexts

    private typealias GeometryTable = [UInt64: [String: [String: Any]]]

    private static func geometryTable(_ metadata: [String: Any]) -> GeometryTable {
        var table: GeometryTable = [:]
        for entry in metadata["candidateGeometry"] as? [[String: Any]] ?? [] {
            guard let id = integer(entry["frameID"]) else { continue }
            var colorsForFrame: [String: [String: Any]] = [:]
            for color in colors { if let set = entry[color] as? [String: Any] { colorsForFrame[color] = set } }
            table[id] = colorsForFrame
        }
        return table
    }

    private static func doubles(_ value: Any?) -> [Double] {
        (value as? [Any])?.compactMap { number($0) } ?? []
    }

    /// Geometry correspondence of one candidate to the previous frame's winner:
    /// independent of list order and candidate index.
    private static func matchMetrics(_ candidate: [String: Any], _ winner: [String: Any]) -> [String: Any] {
        let c = doubles(candidate["centroid"]), w = doubles(winner["centroid"])
        let cb = doubles(candidate["bbox"]), wb = doubles(winner["bbox"])
        let cr = doubles(candidate["rawPCAEndpoints"]), wr = doubles(winner["rawPCAEndpoints"])
        var result: [String: Any] = [:]
        let r = DebugCandidateGeometry.round4
        let winnerSpan = max(number(winner["rawPCASpan"]) ?? 0, 1)
        if c.count == 2, w.count == 2 {
            let distance = hypot(c[0] - w[0], c[1] - w[1])
            result["centroidDistance"] = r(distance)
            result["centroidDistanceNormalized"] = r(distance / winnerSpan)
        }
        if cb.count == 4, wb.count == 4 {
            let ix = max(0, min(cb[2], wb[2]) - max(cb[0], wb[0]) + 1)
            let iy = max(0, min(cb[3], wb[3]) - max(cb[1], wb[1]) + 1)
            let intersection = ix * iy
            let areaC = (cb[2] - cb[0] + 1) * (cb[3] - cb[1] + 1)
            let areaW = (wb[2] - wb[0] + 1) * (wb[3] - wb[1] + 1)
            let union = areaC + areaW - intersection
            result["bboxIoU"] = r(union > 0 ? intersection / union : 0)
        }
        if let a = number(candidate["componentArea"]), let b = number(winner["componentArea"]) {
            result["areaRatio"] = r(a / max(b, 1))
        }
        if let a = number(candidate["rawPCASpan"]), let b = number(winner["rawPCASpan"]) {
            result["spanRatio"] = r(a / max(b, 1))
        }
        if cr.count == 4, wr.count == 4 {
            result["orientationDifference"] = r(DebugTrackingDiagnostics.angleChange(
                DebugTrackingDiagnostics.angle(cr), DebugTrackingDiagnostics.angle(wr)))
        }
        return result
    }

    /// All eligible candidates (up to the limits) of one frame and color, with an
    /// explicit statement of what was left out.
    private static func geometryContext(_ table: GeometryTable, frameID: UInt64, color: String,
                                        eligibleLimit: Int, ineligibleLimit: Int,
                                        breakdownLimit: Int = .max) -> [String: Any]? {
        guard let set = table[frameID]?[color],
              let stored = set["candidates"] as? [[String: Any]],
              let total = integer(set["totalCandidateCount"]).map({ Int($0) }),
              let eligibleTotal = integer(set["eligibleCandidateCount"]).map({ Int($0) }) else { return nil }
        let eligible = stored.filter { $0["eligible"] as? Bool == true }.sorted {
            (integer($0["eligibleRank"]) ?? 0) < (integer($1["eligibleRank"]) ?? 0)
        }
        let ineligible = stored.filter { $0["eligible"] as? Bool != true }
        let keptEligible = Array(eligible.prefix(eligibleLimit))
        let keptIneligible = Array(ineligible.prefix(ineligibleLimit))
        var previous: (UInt64, [String: Any])?
        for offset in 1...3 where frameID >= UInt64(offset) {
            let id = frameID - UInt64(offset)
            if let candidates = table[id]?[color]?["candidates"] as? [[String: Any]] {
                previous = candidates.first { integer($0["eligibleRank"]) == 1 }.map { (id, $0) }
                break
            }
        }
        let entries = (keptEligible + keptIneligible).enumerated().map { position, candidate -> [String: Any] in
            var entry = candidate
            if let previous { entry["matchToPreviousWinner"] = matchMetrics(candidate, previous.1) }
            // Lower-ranked candidates keep their total score; the omission is stated per entry.
            if position >= breakdownLimit, let breakdown = candidate["scoreBreakdown"] as? [String: Any] {
                entry["scoreBreakdown"] = ["total": breakdown["total"] ?? 0]
                entry["scoreBreakdownReduced"] = true
            }
            return entry
        }
        let eligibleOmitted = eligibleTotal - keptEligible.count
        let ineligibleOmitted = (total - eligibleTotal) - keptIneligible.count
        var result: [String: Any] = ["totalCandidateCount": total,
            "eligibleCandidateCount": eligibleTotal,
            "savedEligibleCount": keptEligible.count, "eligibleOmittedCount": eligibleOmitted,
            "savedIneligibleCount": keptIneligible.count, "ineligibleOmittedCount": ineligibleOmitted,
            "candidatesTruncated": eligibleOmitted > 0 || ineligibleOmitted > 0,
            "candidates": entries, "previousFrameGeometryAvailable": previous != nil]
        if let previous {
            let winner = previous.1
            result["previousWinner"] = ["frameID": previous.0, "listIndex": winner["listIndex"] ?? 0,
                "centroid": winner["centroid"] ?? [], "bbox": winner["bbox"] ?? [],
                "componentArea": winner["componentArea"] ?? 0, "rawPCASpan": winner["rawPCASpan"] ?? 0,
                "rawPCAEndpoints": winner["rawPCAEndpoints"] ?? [],
                "finalOutputEndpoints": winner["finalOutputEndpoints"] ?? []] as [String: Any]
        }
        return result
    }

    /// Event-level facts shared by the three originals and the annotated image.
    private static func bridgeContext(_ event: [String: Any], role: String, auxiliary: Bool,
                                      selectedFrameID: UInt64) -> [String: Any] {
        var result: [String: Any] = ["eventID": event["eventID"] ?? 0, "role": role,
            "auxiliary": auxiliary, "color": event["color"] ?? "",
            "beforeFrameID": event["beforeFrameID"] ?? 0,
            "dropoutFrameID": event["dropoutFrameID"] ?? 0,
            "afterFrameID": event["afterFrameID"] ?? 0,
            "beforeTimestamp": event["beforeTimestamp"] ?? 0,
            "dropoutTimestamp": event["dropoutTimestamp"] ?? 0,
            "afterTimestamp": event["afterTimestamp"] ?? 0,
            "missingFrameCount": event["missingFrameCount"] ?? 0,
            "gapSeconds": event["gapSeconds"] ?? 0,
            "evidenceUnit": "one temporal event; its frames are not independent failure examples",
            "continuity": event["assessment"] ?? [:]]
        if auxiliary {
            result["annotation"] = event["annotation"] ?? [:]
        }
        return result
    }

    private static func minimalFrame(_ frame: [String: Any], index: Int,
                                     includeTracking: Bool = false,
                                     isSelected: Bool = true,
                                     active: Set<String> = Set(colors),
                                     geometry: GeometryTable = [:],
                                     geometryLimits: (Int, Int, Int)? = nil) -> [String: Any] {
        var output: [String: Any] = [
            "frameID": integer(frame["frameID"]) ?? UInt64(index),
            "timestamp": number(frame["presentationTimeSeconds"]) ?? 0
        ]
        if let processing = number(frame["processingTimeSeconds"]) {
            output["processingTimeSeconds"] = processing
        }
        if let event = integer(frame["motionEventIndex"]) {
            output["motionEventIndex"] = event
        }
        for color in colors {
            // A color outside the diagnosis never contributes absence or traces.
            guard active.contains(color) else { output[color] = [String: Any](); continue }
            let detection = frame[color] as? [String: Any] ?? [:]
            let diagnostic = colorDiagnostics(frame, color: color)
            let candidate = selectedCandidate(frame, color: color)
            let breakdown = candidate?["scoreBreakdown"] as? [String: Any] ?? [:]
            var colorData: [String: Any] = [:]
            if let detected = detection["detected"] as? Bool { colorData["detected"] = detected }
            if let predicted = detection["predicted"] as? Bool { colorData["predictionUsed"] = predicted }
            if let success = frame["\(color)DetectionSucceeded"] as? Bool {
                colorData["detectionSucceeded"] = success
            }
            for (source, target) in [
                ("maskPixelCount", "maskPixelCount"),
                ("morphologyPixelCount", "morphologyPixelCount"),
                ("connectedComponentCount", "connectedComponentCount"),
                ("totalCandidateCount", "candidateCount"),
                ("eligibleCandidateCount", "eligibleCandidateCount")
            ] {
                if let value = integer(diagnostic[source]) { colorData[target] = value }
            }
            if let count = integer(diagnostic["totalCandidateCount"]), count > 0,
               integer(diagnostic["eligibleCandidateCount"]) == 0 {
                colorData["failureStage"] = "eligibility"
            }
            if let source = string(diagnostic["selectedCandidateType"]) ?? string(candidate?["sourceType"]) {
                colorData["selectedCandidateType"] = source
            }
            if let selectedIndex = integer(diagnostic["selectedCandidateIndex"]) {
                colorData["selectedCandidateIndex"] = selectedIndex
            }
            if let score = number(diagnostic["selectedCandidateFinalScore"]) ?? number(candidate?["finalScore"]) {
                colorData["score"] = score
            }
            if let top = diagnostic["topCandidates"] as? [[String: Any]] {
                let scores = top.filter { $0["eligible"] as? Bool == true }
                    .compactMap { number($0["finalScore"]) }.sorted(by: >)
                if scores.count >= 2 { colorData["topScoreGap"] = scores[0] - scores[1] }
                let margins = top.flatMap { ($0["eligibilityRules"] as? [[String: Any]]) ?? [] }
                    .compactMap { rule -> Double? in
                        guard string(rule["result"]) == "FAIL",
                              string(rule["comparison"]) != "==",
                              let value = number(rule["value"]),
                              let threshold = number(rule["threshold"]),
                              threshold != 0 else { return nil }
                        return abs(value - threshold) / abs(threshold)
                    }
                if let smallest = margins.min() {
                    colorData["minimumFailedRuleMargin"] = smallest
                }
            }
            if let endpoint = detectionEndpoint(detection) { colorData["endpoint"] = endpoint }
            if let candidate {
                copyNumber(candidate, key: "rawPCASpan", into: &colorData)
                copyNumber(candidate, key: "robustMainIntervalLength", into: &colorData)
                copyNumber(candidate, key: "continuity", into: &colorData)
                copyNumber(candidate, key: "density", into: &colorData)
                if let purity = number(breakdown["colorPurity"]) {
                    colorData["colorPurity"] = purity / 10.56
                }
                if let support = number(breakdown["coreSupport"]) {
                    colorData["coreSupport"] = support / 12.0
                }
                if let coverage = number(breakdown["longitudinalHighCoverage"]) {
                    colorData["highBrightnessCoverage"] = coverage / 3.0
                }
            }
            if let top = diagnostic["topCandidates"] as? [[String: Any]], !top.isEmpty {
                let limit = isSelected && integer(diagnostic["eligibleCandidateCount"]) == 0 ? 3 : 1
                colorData["candidateDecisionTrace"] = top.prefix(limit).map { entry -> [String: Any] in
                    var trace: [String: Any] = [:]
                    for key in ["index", "sourceType", "eligible", "finalScore",
                                "rejectionReasons", "peakValue", "meanValue",
                                "highValueRatio", "meanColorPurity", "clippedWhiteRatio",
                                "isCompactRed", "rawPCASpan", "robustMainIntervalLength",
                                "continuity", "density", "componentArea", "pointCount", "compoundRejections"] {
                        if let value = entry[key] { trace[key] = value }
                    }
                    let rules = entry["eligibilityRules"] as? [[String: Any]] ?? []
                    trace["rules"] = rules.filter { string($0["result"]) == "FAIL" }.map { rule in
                        // Codable omits nil optionals; compact rules use explicit nulls
                        // to preserve the consumer's exact decision-trace contract.
                        ["name": rule["name"] ?? "", "result": "FAIL",
                         "value": rule["value"] ?? NSNull(),
                         "comparison": rule["comparison"] ?? NSNull(),
                         "threshold": rule["threshold"] ?? NSNull()] as [String: Any]
                    }
                    return trace
                }
            }
            if let geometryLimits, let id = integer(frame["frameID"]),
               let context = geometryContext(geometry, frameID: id, color: color,
                                             eligibleLimit: geometryLimits.0,
                                             ineligibleLimit: geometryLimits.1,
                                             breakdownLimit: geometryLimits.2) {
                colorData["candidateGeometry"] = context
            }
            if includeTracking {
                if let tracking = frame["tracking"] as? [String: Any], let measurement = tracking[color] {
                    colorData["tracking"] = measurement
                }
                if let candidate {
                    var selected: [String: Any] = [:]
                    for key in ["index", "sourceType", "finalScore", "centroid", "bbox", "componentArea",
                                "pointCount", "peakValue", "meanValue", "highValueRatio", "meanColorPurity",
                                "clippedWhiteRatio", "continuity", "density", "maxGap", "endpointPipeline"] {
                        if let value = candidate[key] { selected[key] = value }
                    }
                    colorData["selectedCandidate"] = selected
                }
                for key in ["secondBestScore", "scoreMargin"] {
                    if let value = diagnostic[key] { colorData[key] = value }
                }
            }
            output[color] = colorData
        }
        return output
    }

    private static func detectionSummary(metadata: [String: Any]) -> [String: Any] {
        let allFrames = frames(metadata)
        let active = activeColorSet(metadata)
        return Dictionary(uniqueKeysWithValues: colors.map { color in
            let detected = allFrames.filter { detectionStatus($0, color: color) == true }.count
            guard active.contains(color) else {
                // Not diagnosed: absence of this color is not a failure.
                return (color, ["frames": allFrames.count, "detectedFrames": detected,
                                "missedFrames": 0, "unknownDetectionFrames": allFrames.count - detected,
                                "candidateZeroFrames": 0, "eligibleZeroFrames": 0,
                                "excludedFromDiagnosis": true] as [String: Any])
            }
            let missed = allFrames.filter { detectionStatus($0, color: color) == false }.count
            let candidates = allFrames.map { integer(colorDiagnostics($0, color: color)["totalCandidateCount"]) }
            let eligible = allFrames.map { integer(colorDiagnostics($0, color: color)["eligibleCandidateCount"]) }
            return (color, [
                "frames": allFrames.count,
                "detectedFrames": detected,
                "missedFrames": missed,
                "unknownDetectionFrames": allFrames.count - detected - missed,
                "candidateZeroFrames": candidates.filter { $0 == 0 }.count,
                "eligibleZeroFrames": zip(candidates, eligible).filter { $0.0 != nil && $0.1 == 0 }.count
            ] as [String: Any])
        })
    }

    private static func dropoutSummary(metadata: [String: Any]) -> [String: Any] {
        let allFrames = frames(metadata)
        let active = activeColorSet(metadata)
        return Dictionary(uniqueKeysWithValues: colors.map { color in
            guard active.contains(color) else {
                return (color, ["falseFrames": 0, "dropoutTransitions": 0, "recoveredTransitions": 0,
                                "excludedFromDiagnosis": true] as [String: Any])
            }
            let transitions = allFrames.filter {
                string($0["\(color)DropoutRole"]) == "dropout"
            }.count
            let recovered = allFrames.filter {
                string($0["\(color)DropoutRole"]) == "recovered"
            }.count
            let missed = allFrames.filter { detectionStatus($0, color: color) == false }.count
            return (color, ["falseFrames": missed, "dropoutTransitions": transitions,
                            "recoveredTransitions": recovered] as [String: Any])
        })
    }

    private static func incidentSummary(metadata: [String: Any]) -> [[String: Any]] {
        let allFrames = frames(metadata)
        let boundedAbsence = metadata["activeColors"] != nil
        let active = activeColorSet(metadata)
        var grouped: [String: (String, String, [Int], Int)] = [:]
        var reasonsByFrame: [[Reason]] = Array(repeating: [], count: allFrames.count)
        for (index, frame) in allFrames.enumerated() {
            if frame["manualCaptured"] as? Bool == true {
                reasonsByFrame[index].append(Reason(color: "both", type: "manual_capture",
                                                    priority: 0, detail: "manual lossless capture"))
            }
            for color in colors where active.contains(color) {
                reasonsByFrame[index].append(contentsOf: reasons(
                    for: allFrames, at: index, color: color, includeAbsence: !boundedAbsence))
            }
            for color in colors where !boundedAbsence && active.contains(color)
                && (string(frame["\(color)DropoutRole"]) == "dropout"
                    || isFirstFalseAfterDetection(allFrames, index: index, color: color)) {
                reasonsByFrame[index].append(Reason(color: color, type: "dropout", priority: 1,
                                                    detail: "first false frame after a detected run"))
            }
        }
        addIdenticalEndpointReasons(frames: allFrames, reasonsByFrame: &reasonsByFrame, active: active)
        for (index, reasons) in reasonsByFrame.enumerated() {
            for reason in reasons {
                let key = "\(reason.color)|\(reason.type)"
                var item = grouped[key] ?? (reason.color, reason.type, [], reason.priority)
                item.2.append(index)
                grouped[key] = item
            }
        }
        var entries = grouped.values.map { color, type, indexes, _ in
            let uniqueIndexes = Array(Set(indexes)).sorted()
            var incidents = 0
            var previous: Int?
            for index in uniqueIndexes {
                if previous.map({ index > $0 + 1 }) ?? true { incidents += 1 }
                previous = index
            }
            return ["color": color, "type": type, "incidentCount": incidents,
                    "affectedFrames": uniqueIndexes.count] as [String: Any]
        }
        for color in colors where active.contains(color) {
            let events = (metadata["bridgeDropoutEvents"] as? [[String: Any]] ?? []).filter {
                string($0["color"]) == color
            }
            if !events.isEmpty {
                entries.append(["color": color, "type": "bridge_dropout",
                                "incidentCount": events.count,
                                "affectedFrames": events.reduce(0) {
                                    $0 + Int(integer($1["missingFrameCount"]) ?? 0) }])
            }
        }
        return entries.sorted {
            let lhs = "\($0["color"] ?? "")|\($0["type"] ?? "")"
            let rhs = "\($1["color"] ?? "")|\($1["type"] ?? "")"
            return lhs < rhs
        }
    }

    private static func promptText(sessionID: String, imageCount: Int) -> String {
        """
        # PhoneSaber triage: \(sessionID)

        Analyze only the selected lossless PNGs attached to this request and the compact JSON contexts in this bundle. There are \(imageCount) selected images. Do not request or infer from a video or full-session metadata.

        ## Evidence rules
        - The attached lossless PNG is the ground truth for pixels. H.264 video is never ground truth and is not included.
        - Treat metadata as detector output, not proof that a saber was physically present or inside the camera view.
        - Separate false negatives, wrong-candidate/endpoint errors, and false positives in the report.
        - Use these A–G labels: A = capture/data artifact; B = false negative or candidate=0; C = candidate exists but eligible=0; D = wrong candidate or endpoint jump; E = false positive suspect (broad/coreless or identical endpoint); F = temporal dropout/continuity; G = insufficient evidence or other.
        - State what is visible in each PNG before interpreting the metadata. Cite image and frame IDs.
        - If evidence is insufficient, say so and do not recommend production recognition-code changes.
        - For eligibility dropouts, inspect candidateDecisionTrace: identify failed production rules, measured values and thresholds, and whether failures repeat across frames. Assess false-positive risk before supporting a production change; do not simply loosen a threshold.
        - A bridge dropout is ONE temporal event: before_success (detected), dropout (missed) and after_success (detected) bracket a short loss of the same saber. Count it as one example, never as three independent failures. The annotated_dropout PNG is a viewing aid with an interpolated expected position; it is not ground truth and never replaces the original dropout PNG.
        - Colors not listed in activeColors are outside this diagnosis; their absence is not a failure.
        - Do not edit, create, or propose applying production code. Return analysis findings only.

        ## Required output
        Produce a concise diagnostic report with: session summary; findings grouped by RED/BLUE; false negatives, wrong-candidate/endpoint errors, and false positives in separate sections; A–G classifications; confidence and missing evidence; and no code patches. The Mac receiver writes analysis_report.md and analysis_report.json from your response.
        """
    }

    private static func endpointJumpAt(_ frames: [[String: Any]], index: Int,
                                       color: String) -> Double? {
        guard index > 0, areAdjacent(frames[index - 1], frames[index]) else { return nil }
        guard let current = endpointTuple(frames[index], color: color),
              let previous = endpointTuple(frames[index - 1], color: color) else { return nil }
        func distance(_ a: (Double, Double), _ b: (Double, Double)) -> Double {
            hypot(a.0 - b.0, a.1 - b.1)
        }
        let forward = max(distance((previous[0], previous[1]), (current[0], current[1])),
                          distance((previous[2], previous[3]), (current[2], current[3])))
        let reverse = max(distance((previous[0], previous[1]), (current[2], current[3])),
                          distance((previous[2], previous[3]), (current[0], current[1])))
        return min(forward, reverse)
    }

    private static func endpointTuple(_ frame: [String: Any], color: String) -> [Double]? {
        guard isDetectionSuccess(frame, color: color),
              let detection = frame[color] as? [String: Any],
              let x1 = number(detection["x1"]), let y1 = number(detection["y1"]),
              let x2 = number(detection["x2"]), let y2 = number(detection["y2"]) else { return nil }
        return [x1, y1, x2, y2]
    }

    private static func detectionEndpoint(_ detection: [String: Any]) -> [Int]? {
        guard let x1 = integer(detection["x1"]), let y1 = integer(detection["y1"]),
              let x2 = integer(detection["x2"]), let y2 = integer(detection["y2"]) else { return nil }
        return [Int(x1), Int(y1), Int(x2), Int(y2)]
    }

    private static func detectionStatus(_ frame: [String: Any], color: String) -> Bool? {
        if let explicit = frame["\(color)DetectionSucceeded"] as? Bool { return explicit }
        guard let detection = frame[color] as? [String: Any],
              let detected = detection["detected"] as? Bool else { return nil }
        if !detected { return false }
        guard let predicted = detection["predicted"] as? Bool else { return nil }
        return !predicted
    }

    private static func isDetectionSuccess(_ frame: [String: Any], color: String) -> Bool {
        detectionStatus(frame, color: color) == true
    }

    private static func areAdjacent(_ previous: [String: Any], _ current: [String: Any]) -> Bool {
        guard let before = integer(previous["frameID"]),
              let after = integer(current["frameID"]) else { return false }
        return after == before + 1
    }

    private static func isFirstFalseAfterDetection(_ frames: [[String: Any]], index: Int,
                                                   color: String) -> Bool {
        index > 0 && areAdjacent(frames[index - 1], frames[index])
            && detectionStatus(frames[index], color: color) == false
            && detectionStatus(frames[index - 1], color: color) == true
    }

    private static func rawRobustDivergence(raw: Double, robust: Double?) -> Bool {
        guard let robust else { return false }
        return raw >= robust * 2.0 && raw - robust >= 80.0
    }

    private static func selectedCandidate(_ frame: [String: Any], color: String) -> [String: Any]? {
        colorDiagnostics(frame, color: color)["selectedCandidate"] as? [String: Any]
    }

    private static func colorDiagnostics(_ frame: [String: Any], color: String) -> [String: Any] {
        ((frame["candidateDiagnostics"] as? [String: Any])?[color] as? [String: Any]) ?? [:]
    }

    private static func frames(_ metadata: [String: Any]) -> [[String: Any]] {
        metadata["frames"] as? [[String: Any]] ?? []
    }

    private static func copyNumber(_ source: [String: Any], key: String,
                                   into destination: inout [String: Any]) {
        if let value = number(source[key]) { destination[key] = value }
    }

    private static func number(_ value: Any?) -> Double? {
        guard let value, !(value is NSNull), let number = value as? NSNumber,
              CFGetTypeID(number) != CFBooleanGetTypeID() else { return nil }
        let result = number.doubleValue
        return result.isFinite ? result : nil
    }

    private static func integer(_ value: Any?) -> UInt64? {
        guard let number = number(value), number >= 0, number.rounded(.towardZero) == number,
              number <= Double(UInt64.max) else { return nil }
        return UInt64(number)
    }

    private static func string(_ value: Any?) -> String? {
        guard let value = value as? String, !value.isEmpty else { return nil }
        return value
    }

    private static func safePNGName(_ name: String) -> Bool {
        !name.isEmpty && name == URL(fileURLWithPath: name).lastPathComponent
            && name.lowercased().hasSuffix(".png") && !name.contains("..")
    }

    private static func safeSessionName(_ value: String) -> String {
        let allowed = CharacterSet(charactersIn: "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_-")
        let cleaned = String(value.unicodeScalars.map { allowed.contains($0) ? Character($0) : "_" })
        return String(cleaned.prefix(100))
    }

    private static func jsonData(_ object: Any, compact: Bool = false) throws -> Data {
        try JSONSerialization.data(withJSONObject: object,
                                   options: compact ? [.sortedKeys] : [.prettyPrinted, .sortedKeys])
    }
}

/// Recording-only measurements. Scores rank discontinuities, never detection eligibility.
struct DebugTrackingFrame: Codable, Equatable {
    let midpoint: [Double]?
    let segmentLength: Double?
    let orientationRadians: Double?
    let endpointDisplacement: Double?
    let midpointDisplacement: Double?
    let lengthChange: Double?
    let orientationChange: Double?
    let candidateSwitch: Bool
    let candidateMatchConfidence: String
    let endpointPathChanged: Bool
    let detectedToggle: Bool
    let scoreChange: Double?
    let scoreMarginCollapse: Double?
    let stageDiscontinuities: [String: Double]
    let scoreComponents: [String: Double]
    let instabilityScore: Double
}

enum DebugTrackingDiagnostics {
    static func rankingScore(_ components: [String: Double]) -> Double {
        // Fixed order keeps online retention and Stop-time ranking bit-for-bit consistent.
        ["geometryDiscontinuity", "candidateSwitch", "endpointPathChanged", "detectedToggle",
         "scoreChange", "scoreMarginCollapse"].reduce(0) { $0 + (components[$1] ?? 0) }
    }
    static func compare(_ value: Double?, _ comparison: String?, _ threshold: Double?) -> Bool? {
        guard let value, let threshold else { return nil }
        switch comparison {
        case ">=": return value >= threshold
        case "<=": return value <= threshold
        case ">": return value > threshold
        case "<": return value < threshold
        case "==": return value == threshold
        default: return nil
        }
    }

    static func values(_ points: DebugRecordingEndpoints?) -> [Double]? {
        guard let points else { return nil }
        return [Double(points.first.x), Double(points.first.y),
                Double(points.second.x), Double(points.second.y)]
    }
    static func length(_ p: [Double]) -> Double { hypot(p[2] - p[0], p[3] - p[1]) }
    static func angle(_ p: [Double]) -> Double { atan2(p[3] - p[1], p[2] - p[0]) }
    static func angleChange(_ a: Double, _ b: Double) -> Double {
        let delta = abs(a - b).truncatingRemainder(dividingBy: .pi)
        return min(delta, .pi - delta)
    }
    static func aligned(_ p: [Double], _ q: [Double]) -> [Double] {
        let direct = hypot(p[0] - q[0], p[1] - q[1]) + hypot(p[2] - q[2], p[3] - q[3])
        let reverse = hypot(p[2] - q[0], p[3] - q[1]) + hypot(p[0] - q[2], p[1] - q[3])
        return direct <= reverse ? p : [p[2], p[3], p[0], p[1]]
    }
    static func movement(_ p: [Double], _ q: [Double]) -> Double {
        let a = aligned(p, q)
        return max(hypot(a[0] - q[0], a[1] - q[1]), hypot(a[2] - q[2], a[3] - q[3]))
    }
    static func matchCost(_ a: DebugRecordingCandidate, _ b: DebugRecordingCandidate) -> Double {
        // List index and proposal source are not persistent physical identity.
        let ac = a.centroid ?? [(Double(a.bbox[0]) + Double(a.bbox[2])) / 2,
                               (Double(a.bbox[1]) + Double(a.bbox[3])) / 2]
        let bc = b.centroid ?? [(Double(b.bbox[0]) + Double(b.bbox[2])) / 2,
                               (Double(b.bbox[1]) + Double(b.bbox[3])) / 2]
        let scale = max(a.rawPCASpan, b.rawPCASpan, 1)
        let overlap = Double(max(0, min(a.bbox[2], b.bbox[2]) - max(a.bbox[0], b.bbox[0])))
            * Double(max(0, min(a.bbox[3], b.bbox[3]) - max(a.bbox[1], b.bbox[1])))
        let areaA = Double(max(1, a.bbox[2] - a.bbox[0]) * max(1, a.bbox[3] - a.bbox[1]))
        let areaB = Double(max(1, b.bbox[2] - b.bbox[0]) * max(1, b.bbox[3] - b.bbox[1]))
        let iou = overlap / max(1, areaA + areaB - overlap)
        let geometry = abs(log(max(1, a.rawPCASpan) / max(1, b.rawPCASpan)))
            + abs(log(Double(max(1, a.componentArea)) / Double(max(1, b.componentArea)))) * 0.25
        let orientation = angleChange(angle(values(a.rawPCAEndpoints)!), angle(values(b.rawPCAEndpoints)!)) / .pi
        return hypot(ac[0] - bc[0], ac[1] - bc[1]) / scale + geometry + orientation + (1 - iou) * 0.25
    }
    static func candidateSwitch(_ current: DebugRecordingColorCandidates?,
                                _ previous: DebugRecordingColorCandidates?) -> (Bool, String) {
        guard let a = previous?.selectedCandidate, let b = current?.selectedCandidate else {
            return (false, "unavailable")
        }
        let cost = matchCost(a, b)
        let forward = current!.topCandidates.filter { $0.index != b.index }.map { matchCost(a, $0) }.min()
        let backward = previous!.topCandidates.filter { $0.index != a.index }.map { matchCost($0, b) }.min()
        // Only mark a switch when another observed proposal explains the correspondence
        // substantially better; translation of an isolated fast-moving saber is ambiguous.
        let alternate = min(forward ?? cost, backward ?? cost)
        let switched = cost > 0.55 && alternate < 0.45 && alternate + 0.30 < cost
        return (switched, switched ? "alternativeGeometryMatch" : (cost < 0.55 ? "geometryMatch" : "ambiguousMotion"))
    }
    static func measure(_ frame: DebugRecordingFrameMetadata,
                        previous: DebugRecordingFrameMetadata?, older: DebugRecordingFrameMetadata?,
                        color: String) -> DebugTrackingFrame {
        func candidates(_ f: DebugRecordingFrameMetadata?) -> DebugRecordingColorCandidates? {
            color == "red" ? f?.candidateDiagnostics?.red : f?.candidateDiagnostics?.blue
        }
        func succeeded(_ f: DebugRecordingFrameMetadata?) -> Bool {
            color == "red" ? f?.redDetectionSucceeded == true : f?.blueDetectionSucceeded == true
        }
        let current = candidates(frame), prior = candidates(previous), old = candidates(older)
        let selected = current?.selectedCandidate, previousSelected = prior?.selectedCandidate
        let p = values(selected?.finalOutputEndpoints), q = values(previousSelected?.finalOutputEndpoints)
        let switchResult = candidateSwitch(current, prior)
        let pathChanged = selected != nil && previousSelected != nil && (
            selected!.endpointPipeline.endpointSource != previousSelected!.endpointPipeline.endpointSource
            || selected!.endpointPipeline.fallbackReason != previousSelected!.endpointPipeline.fallbackReason)
        let toggle = previous != nil && succeeded(frame) != succeeded(previous)
        let scoreChange = selected.flatMap { a in previousSelected.map { a.finalScore - $0.finalScore } }
        let collapse = current?.scoreMargin.flatMap { a in prior?.scoreMargin.map { max(0, $0 - a) / max(1, abs($0)) } }
        var stages: [String: Double] = [:]
        let dt = frame.presentationTimeSeconds - (previous?.presentationTimeSeconds ?? frame.presentationTimeSeconds)
        let oldDT = (previous?.presentationTimeSeconds ?? 0) - (older?.presentationTimeSeconds ?? 0)
        for stage in ["rawPCA", "robustInterval", "body", "finalSelected"] {
            func points(_ c: DebugRecordingCandidate?) -> [Double]? {
                guard let c else { return nil }
                switch stage {
                case "rawPCA": return values(c.endpointPipeline.rawPCA)
                case "robustInterval": return values(c.endpointPipeline.robustInterval)
                case "body": return values(c.endpointPipeline.body)
                default: return values(c.endpointPipeline.finalSelected)
                }
            }
            if let now = points(selected), let before = points(previousSelected),
               let earliest = points(old?.selectedCandidate), dt > 0, oldDT > 0 {
                let n = aligned(now, before), o = aligned(earliest, before)
                let expected = zip(before, o).map { $0 + ($0 - $1) * dt / oldDT }
                let scale = max(length(before), 1)
                let residual = movement(n, expected) / scale
                let lengthJump = abs(log(max(1, length(now)) / max(1, length(before))))
                let angleJump = angleChange(angle(now), angle(before)) / .pi
                stages[stage] = residual + lengthJump + angleJump
            }
        }
        let components = ["geometryDiscontinuity": stages["finalSelected"] ?? 0,
            "candidateSwitch": switchResult.0 ? 2.0 : 0,
            "endpointPathChanged": pathChanged ? 1.0 : 0,
            "detectedToggle": toggle ? 2.0 : 0,
            "scoreChange": min(1, abs(scoreChange ?? 0) / max(1, abs(previousSelected?.finalScore ?? 1))) * 0.25,
            "scoreMarginCollapse": min(1, collapse ?? 0) * 0.5]
        let score = rankingScore(components)
        return DebugTrackingFrame(
            midpoint: p.map { [($0[0] + $0[2]) / 2, ($0[1] + $0[3]) / 2] },
            segmentLength: p.map(length), orientationRadians: p.map(angle),
            endpointDisplacement: p.flatMap { p in q.map { movement(p, $0) } },
            midpointDisplacement: p.flatMap { p in q.map { hypot((p[0] + p[2] - $0[0] - $0[2]) / 2, (p[1] + p[3] - $0[1] - $0[3]) / 2) } },
            lengthChange: p.flatMap { p in q.map { length(p) - length($0) } },
            orientationChange: p.flatMap { p in q.map { angleChange(angle(p), angle($0)) } },
            candidateSwitch: switchResult.0, candidateMatchConfidence: switchResult.1,
            endpointPathChanged: pathChanged, detectedToggle: toggle,
            scoreChange: scoreChange, scoreMarginCollapse: collapse,
            stageDiscontinuities: stages, scoreComponents: components, instabilityScore: score)
    }
}

// MARK: - Diagnostic colors and bridge dropouts

/// Colors Debug Recording diagnoses. Recognition and UDP always run for both
/// colors; this only decides which absences and anomalies count as failures.
enum DebugDiagnosticColors: String, CaseIterable, Identifiable, Equatable {
    case red = "RED"
    case blue = "BLUE"
    case both = "BOTH"

    var id: String { rawValue }

    var colorNames: [String] {
        switch self {
        case .red: return ["red"]
        case .blue: return ["blue"]
        case .both: return ["red", "blue"]
        }
    }

    func includes(_ color: String) -> Bool { colorNames.contains(color) }
}

/// Thresholds for choosing diagnostic images only. They never reach recognition,
/// scoring, eligibility or UDP output, and are stored with every selected event.
enum DebugBridgeThresholds {
    /// Outer temporal bound; motion plausibility below is the real criterion.
    static let maximumGapSeconds = 2.0
    static let maximumSpeedPixelsPerSecond = DebugMotionThresholds.endpointSpeedPixelsPerSecond
    static let lengthChangeBase = DebugMotionThresholds.lengthChangeFraction
    static let lengthChangeCap = 0.7
    static let orientationBaseRadians = 0.5
    static let orientationRateRadiansPerSecond = 8.0
    static let orientationCapRadians = 1.4
    static let residualLengthFraction = 0.5
    static let residualLengthFractionPerSecond = 1.5
    /// Two prior successes closer than this give a usable midpoint velocity.
    static let velocityWindowSeconds = 0.25
    static let maximumEvents = 2
    static let followingContextFrames = 2

    static var dictionary: [String: Double] {
        ["maximumGapSeconds": maximumGapSeconds,
         "maximumSpeedPixelsPerSecond": maximumSpeedPixelsPerSecond,
         "lengthChangeBase": lengthChangeBase, "lengthChangeCap": lengthChangeCap,
         "orientationBaseRadians": orientationBaseRadians,
         "orientationRateRadiansPerSecond": orientationRateRadiansPerSecond,
         "orientationCapRadians": orientationCapRadians,
         "residualLengthFraction": residualLengthFraction,
         "residualLengthFractionPerSecond": residualLengthFractionPerSecond,
         "velocityWindowSeconds": velocityWindowSeconds]
    }
}

struct DebugBridgeSample: Equatable {
    let frameID: UInt64
    let timestamp: Double
    /// Fresh, non-predicted detection of the color.
    let succeeded: Bool
    /// x1,y1,x2,y2 of a successful detection.
    let endpoints: [Double]?
}

struct DebugBridgeAssessment: Equatable {
    let accepted: Bool
    let rejection: String?
    let measurements: [String: Double]
}

enum DebugBridgeDropout {
    /// Decides whether `missing` is a short loss of the same saber, bounded by a
    /// successful detection on each side, rather than the saber leaving the view.
    /// Uses the existing tracking-diagnostic geometry (midpoint, length,
    /// undirected orientation, constant-velocity prediction) and the temporal
    /// interval between the two successes.
    static func assess(prior: [DebugBridgeSample], missing: [DebugBridgeSample],
                       after: DebugBridgeSample) -> DebugBridgeAssessment {
        func reject(_ reason: String, _ values: [String: Double] = [:]) -> DebugBridgeAssessment {
            DebugBridgeAssessment(accepted: false, rejection: reason, measurements: values)
        }
        guard let before = prior.last, before.succeeded, let beforePoints = before.endpoints,
              after.succeeded, let afterRaw = after.endpoints,
              beforePoints.count == 4, afterRaw.count == 4,
              let firstMissing = missing.first, let lastMissing = missing.last,
              missing.allSatisfy({ !$0.succeeded }) else {
            return reject("missing_success_on_one_side")
        }
        let gap = after.timestamp - before.timestamp
        guard before.timestamp < firstMissing.timestamp, lastMissing.timestamp < after.timestamp,
              gap > 0, gap.isFinite else {
            return reject("temporal_order_invalid")
        }
        var values: [String: Double] = ["gapSeconds": gap, "missingFrameCount": Double(missing.count)]
        guard gap <= DebugBridgeThresholds.maximumGapSeconds else {
            return reject("gap_too_long", values)
        }

        let afterPoints = DebugTrackingDiagnostics.aligned(afterRaw, beforePoints)
        func midpoint(_ p: [Double]) -> (Double, Double) { ((p[0] + p[2]) / 2, (p[1] + p[3]) / 2) }
        let beforeMid = midpoint(beforePoints)
        let afterMid = midpoint(afterPoints)
        let displacement = hypot(afterMid.0 - beforeMid.0, afterMid.1 - beforeMid.1)
        let beforeLength = DebugTrackingDiagnostics.length(beforePoints)
        let afterLength = DebugTrackingDiagnostics.length(afterPoints)
        let reference = max(beforeLength, afterLength, 1)
        let speed = displacement / gap
        let lengthChange = abs(afterLength - beforeLength) / reference
        let orientationChange = DebugTrackingDiagnostics.angleChange(
            DebugTrackingDiagnostics.angle(beforePoints), DebugTrackingDiagnostics.angle(afterPoints))
        let lengthLimit = min(DebugBridgeThresholds.lengthChangeCap,
                              DebugBridgeThresholds.lengthChangeBase + gap)
        let orientationLimit = min(DebugBridgeThresholds.orientationCapRadians,
            DebugBridgeThresholds.orientationBaseRadians
                + DebugBridgeThresholds.orientationRateRadiansPerSecond * gap)
        values["midpointDisplacement"] = displacement
        values["speedPixelsPerSecond"] = speed
        values["speedLimitPixelsPerSecond"] = DebugBridgeThresholds.maximumSpeedPixelsPerSecond
        values["lengthChange"] = lengthChange
        values["lengthChangeLimit"] = lengthLimit
        values["orientationChangeRadians"] = orientationChange
        values["orientationChangeLimitRadians"] = orientationLimit
        values["beforeLength"] = beforeLength
        values["afterLength"] = afterLength

        var failures: [String] = []
        if speed > DebugBridgeThresholds.maximumSpeedPixelsPerSecond { failures.append("speed") }
        if lengthChange > lengthLimit { failures.append("length_change") }
        if orientationChange > orientationLimit { failures.append("orientation_change") }

        if prior.count >= 2, let olderPoints = prior[prior.count - 2].endpoints,
           olderPoints.count == 4, prior[prior.count - 2].succeeded {
            let older = prior[prior.count - 2]
            let interval = before.timestamp - older.timestamp
            if interval > 0, interval <= DebugBridgeThresholds.velocityWindowSeconds {
                let alignedOlder = DebugTrackingDiagnostics.aligned(olderPoints, beforePoints)
                let olderMid = midpoint(alignedOlder)
                let velocity = ((beforeMid.0 - olderMid.0) / interval,
                                (beforeMid.1 - olderMid.1) / interval)
                let predicted = (beforeMid.0 + velocity.0 * gap, beforeMid.1 + velocity.1 * gap)
                let residual = hypot(afterMid.0 - predicted.0, afterMid.1 - predicted.1)
                let tolerance = reference * (DebugBridgeThresholds.residualLengthFraction
                    + DebugBridgeThresholds.residualLengthFractionPerSecond * gap)
                values["predictionResidual"] = residual
                values["predictionResidualTolerance"] = tolerance
                if residual > tolerance { failures.append("prediction_residual") }
            }
        }
        return DebugBridgeAssessment(accepted: failures.isEmpty,
            rejection: failures.isEmpty ? nil : "discontinuous_" + failures.joined(separator: "+"),
            measurements: values)
    }

    /// Location where the saber would be if it moved steadily between the two
    /// successful detections. A viewing aid, never ground truth.
    static func predictedEndpoints(before: DebugBridgeSample, after: DebugBridgeSample,
                                   at timestamp: Double) -> [Double]? {
        guard let b = before.endpoints, let a0 = after.endpoints, b.count == 4, a0.count == 4,
              after.timestamp > before.timestamp else { return nil }
        let a = DebugTrackingDiagnostics.aligned(a0, b)
        let fraction = min(1, max(0, (timestamp - before.timestamp) / (after.timestamp - before.timestamp)))
        return (0..<4).map { b[$0] + (a[$0] - b[$0]) * fraction }
    }
}

/// Follows one color's success/missing sequence. A bridge candidate starts only
/// after a success and is reported only when a later success closes it, so
/// frames before the first or after the last success are never candidates.
struct DebugBridgeTracker {
    struct Run: Equatable {
        var prior: [DebugBridgeSample]
        var missing: [DebugBridgeSample]
    }

    enum Outcome: Equatable {
        case none
        case dropoutStarted(Run)
        case recovered(Run, after: DebugBridgeSample)
    }

    private var recentSuccesses: [DebugBridgeSample] = []
    private(set) var run: Run?
    private(set) var firstSuccess: DebugBridgeSample?
    private(set) var lastSuccess: DebugBridgeSample?

    mutating func observe(_ sample: DebugBridgeSample) -> Outcome {
        if sample.succeeded {
            if firstSuccess == nil { firstSuccess = sample }
            lastSuccess = sample
            let closed = run
            run = nil
            recentSuccesses.append(sample)
            if recentSuccesses.count > 2 { recentSuccesses.removeFirst() }
            if let closed { return .recovered(closed, after: sample) }
            return .none
        }
        if var open = run {
            open.missing.append(sample)
            run = open
            return .none
        }
        guard !recentSuccesses.isEmpty else { return .none }
        let started = Run(prior: recentSuccesses, missing: [sample])
        run = started
        return .dropoutStarted(started)
    }

    /// Candidate that never saw a later success (recording ended first).
    var unclosedRun: Run? { run }
}

// MARK: - Candidate geometry (diagnostics only)

/// Geometry of one candidate, enough to follow a candidate across frames even when
/// the candidate list reorders. Observability only: nothing here feeds recognition.
struct DebugCandidateGeometry: Equatable {
    let listIndex: Int
    let eligible: Bool
    /// 1-based position among eligible candidates in production order; rank 1 won.
    let eligibleRank: Int?
    let sourceType: String
    let finalScore: Double
    let scoreBreakdown: [String: Double]
    let centroid: [Double]
    /// "trace" when measured from the mask, "bboxCenter" when no trace was collected.
    let centroidSource: String
    let bbox: [Int]
    let componentArea: Int
    let rawPCASpan: Double
    let rawPCAEndpoints: [Double]
    let finalOutputEndpoints: [Double]
    let rejectionReasons: [String]

    static func round4(_ value: Double) -> Double { (value * 10_000).rounded() / 10_000 }

    var dictionary: [String: Any] {
        var result: [String: Any] = ["listIndex": listIndex, "eligible": eligible,
            "sourceType": sourceType, "finalScore": Self.round4(finalScore),
            "scoreBreakdown": scoreBreakdown, "centroid": centroid, "centroidSource": centroidSource,
            "bbox": bbox, "componentArea": componentArea, "rawPCASpan": Self.round4(rawPCASpan),
            "rawPCAEndpoints": rawPCAEndpoints, "finalOutputEndpoints": finalOutputEndpoints,
            "rejectionReasons": rejectionReasons]
        if let eligibleRank { result["eligibleRank"] = eligibleRank }
        return result
    }
}

/// Candidates of one color in one frame. Every eligible candidate is saved up to
/// `eligibleLimit`; what was left out is stated explicitly, never implied.
struct DebugCandidateGeometrySet: Equatable {
    static let eligibleLimit = 12
    static let ineligibleLimit = 6

    let totalCandidateCount: Int
    let eligibleCandidateCount: Int
    let candidates: [DebugCandidateGeometry]

    init(_ source: [SaberCandidate]) {
        totalCandidateCount = source.count
        eligibleCandidateCount = source.filter(\.isEmitterEligible).count
        var rank = 0
        var saved: [DebugCandidateGeometry] = []
        var ineligibleSaved = 0
        for (index, candidate) in source.enumerated() {
            if candidate.isEmitterEligible { rank += 1 }
            let keep = candidate.isEmitterEligible ? rank <= Self.eligibleLimit
                : ineligibleSaved < Self.ineligibleLimit
            guard keep else { continue }
            if !candidate.isEmitterEligible { ineligibleSaved += 1 }
            let breakdown = DebugRecordingScoreBreakdown(candidate.scoreBreakdown)
            var scores: [String: Double] = [:]
            for (key, value) in [("proposalPenalty", breakdown.proposalPenalty), ("radiance", breakdown.radiance),
                ("length", breakdown.length), ("aspect", breakdown.aspect), ("extent", breakdown.extent),
                ("widthConsistency", breakdown.widthConsistency), ("area", breakdown.area),
                ("peakBrightness", breakdown.peakBrightness), ("meanBrightness", breakdown.meanBrightness),
                ("highBrightnessRatio", breakdown.highBrightnessRatio), ("colorPurity", breakdown.colorPurity),
                ("localContrast", breakdown.localContrast), ("emitterTexture", breakdown.emitterTexture),
                ("clippedWhite", breakdown.clippedWhite),
                ("longitudinalHighCoverage", breakdown.longitudinalHighCoverage),
                ("coreSupport", breakdown.coreSupport),
                ("longitudinalCoreCoverage", breakdown.longitudinalCoreCoverage),
                ("total", breakdown.total)] {
                scores[key] = DebugCandidateGeometry.round4(value)
            }
            let box = candidate.boundingBox
            let trace = candidate.endpointDiagnosticTrace
            let centroid = trace.map { [DebugCandidateGeometry.round4($0.centroidX),
                                        DebugCandidateGeometry.round4($0.centroidY)] }
                ?? [Double(box.minX + box.maxX) / 2, Double(box.minY + box.maxY) / 2]
            let raw = candidate.comparisonEndpoints, final = candidate.endpoints
            var reasons: [String] = []
            if !candidate.isEmitterEligible {
                reasons = DebugRecordingCandidate(index: index, candidate: candidate,
                                                  selectedIndex: nil).rejectionReasons
            }
            saved.append(DebugCandidateGeometry(
                listIndex: index, eligible: candidate.isEmitterEligible,
                eligibleRank: candidate.isEmitterEligible ? rank : nil,
                sourceType: candidate.source, finalScore: candidate.score, scoreBreakdown: scores,
                centroid: centroid, centroidSource: trace == nil ? "bboxCenter" : "trace",
                bbox: [box.minX, box.minY, box.maxX, box.maxY], componentArea: candidate.componentArea,
                rawPCASpan: candidate.rawPCASpan,
                rawPCAEndpoints: [Double(raw.0.x), Double(raw.0.y), Double(raw.1.x), Double(raw.1.y)],
                finalOutputEndpoints: [Double(final.0.x), Double(final.0.y), Double(final.1.x), Double(final.1.y)],
                rejectionReasons: reasons))
        }
        candidates = saved
    }

    var dictionary: [String: Any] {
        ["totalCandidateCount": totalCandidateCount, "eligibleCandidateCount": eligibleCandidateCount,
         "candidates": candidates.map(\.dictionary)]
    }
}

/// Side data carried with a frame's metadata but never streamed into the full
/// metadata file; it reaches only the triage snapshot of retained event frames.
struct DebugFrameGeometry: Equatable {
    var red: DebugCandidateGeometrySet?
    var blue: DebugCandidateGeometrySet?

    init(_ analysis: SaberFrameAnalysis, active: [String]) {
        red = active.contains("red") ? DebugCandidateGeometrySet(analysis.candidates[.red] ?? []) : nil
        blue = active.contains("blue") ? DebugCandidateGeometrySet(analysis.candidates[.blue] ?? []) : nil
    }

    var dictionary: [String: Any] {
        var result: [String: Any] = [:]
        if let red { result["red"] = red.dictionary }
        if let blue { result["blue"] = blue.dictionary }
        return result
    }
}
