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
    static let maximumRetainedBGRABytes = 128 * 1_024 * 1_024
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
               (currentType != previousType
                || (current.selectedCandidateIndex != nil
                    && previous.selectedCandidateIndex != nil
                    && current.selectedCandidateIndex != previous.selectedCandidateIndex)) {
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
}

struct DebugRecordingTriageSelection: Equatable {
    let images: [DebugRecordingTriageImage]
    let incidentCount: Int
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

    mutating func observe(_ frame: DebugRecordingFrameMetadata) {
        let previous = recent.last
        let redIdentical = identical(frame.red, previous?.red) && frame.redDetectionSucceeded
        let blueIdentical = identical(frame.blue, previous?.blue) && frame.blueDetectionSucceeded
        identicalRed = redIdentical ? identicalRed + 1 : 0
        identicalBlue = blueIdentical ? identicalBlue + 1 : 0
        let incident = frame.manualCaptured || frame.forensicCaptured
            || frame.motionEventIndex != nil
            || frame.redDropoutRole != nil || frame.blueDropoutRole != nil
            || Self.isCaptureCandidate(frame)
            || jump(frame.red, previous?.red) >= 180
            || jump(frame.blue, previous?.blue) >= 180
            || identicalRed >= 3 || identicalBlue >= 3
            || (previous?.redDetectionSucceeded == true && !frame.redDetectionSucceeded)
            || (previous?.blueDetectionSucceeded == true && !frame.blueDetectionSucceeded)
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
        suspicious(frame.candidateDiagnostics?.red) || suspicious(frame.candidateDiagnostics?.blue)
    }

    private static func suspicious(_ diagnostics: DebugRecordingColorCandidates?) -> Bool {
        guard let diagnostics else { return false }
        if diagnostics.totalCandidateCount == 0 || diagnostics.eligibleCandidateCount == 0 { return true }
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

    static func selectImages(
        metadataData: Data,
        forensicDirectoryURL: URL?,
        maximumImages: Int = DebugRecordingTriageLimits.defaultImageCount,
        perFailureType: Int = DebugRecordingTriageLimits.perFailureType
    ) throws -> (metadata: [String: Any], selection: DebugRecordingTriageSelection) {
        let metadata = try decodeMetadata(metadataData)
        let frames = metadata["frames"] as! [[String: Any]]
        let directory = forensicDirectoryURL
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

            for color in colors {
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

            for color in colors {
                reasonsByFrame[index].append(contentsOf: reasons(for: frames, at: index, color: color))
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
                for image in images {
                    guard let frameID = integer(image["frameID"]),
                          let frameIndex = indexByID[frameID],
                          let fileName = string(image["fileName"]),
                          let role = string(image["role"]),
                          ["event_pre", "event_at", "event_post"].contains(role) else { continue }
                    references.append(ImageReference(frameIndex: frameIndex,
                        frameID: frameID, color: color, fileName: fileName,
                        isManual: false, eventIndex: eventIndex, role: role,
                        score: score, signals: signals))
                }
            }
        }

        addIdenticalEndpointReasons(frames: frames, reasonsByFrame: &reasonsByFrame)

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

        let sortedReferences = uniqueByName.values.sorted { lhs, rhs in
            if (lhs.eventIndex != nil) != (rhs.eventIndex != nil) {
                return lhs.eventIndex != nil
            }
            if let leftScore = lhs.score, let rightScore = rhs.score,
               leftScore != rightScore { return leftScore > rightScore }
            if lhs.eventIndex != rhs.eventIndex {
                return (lhs.eventIndex ?? Int.max) < (rhs.eventIndex ?? Int.max)
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

        var selected: [DebugRecordingTriageImage] = []
        var countByType: [String: Int] = [:]
        var selectedBytes: Int64 = 0
        // Leave room for the compact contexts, summary and prompt under the
        // unchanged 64 MiB transport budget. Selection is performed after Stop.
        let imageBudget = DebugRecordingTriageLimits.maximumBundleBytes - 2 * 1_024 * 1_024
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
            incidentCount: incidentCount + motionCount))
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

                let framePayload = contextPayload(metadata: metadata, selected: item)
                let frameName = "frame_\(item.frameID)_\(offset + 1).json"
                try jsonData(framePayload).write(to: frameDirectory.appendingPathComponent(frameName),
                                                options: .atomic)
                var imageEntry: [String: Any] = [
                    "path": "images/\(targetName)", "frameContextPath": "frames/\(frameName)",
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
            try jsonData(summary).write(to: bundleURL.appendingPathComponent("summary.json"),
                                        options: .atomic)
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
                                color: String) -> [Reason] {
        let frame = frames[index]
        let diagnostic = colorDiagnostics(frame, color: color)
        let candidate = selectedCandidate(frame, color: color)
        let candidateCount = integer(diagnostic["totalCandidateCount"])
        let eligibleCount = integer(diagnostic["eligibleCandidateCount"])
        var result: [Reason] = []

        if candidateCount == 0 {
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
                                                    reasonsByFrame: inout [[Reason]]) {
        for color in colors {
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

    private static func reasonList(for reference: ImageReference,
                                   reasonsByFrame: [[Reason]]) -> [Reason] {
        let sameFrame = reasonsByFrame[reference.frameIndex]
        let matching = sameFrame.filter { reference.color == "both" || $0.color == reference.color || $0.color == "both" }
        return matching
    }

    private static func contextPayload(metadata: [String: Any],
                                       selected: DebugRecordingTriageImage) -> [String: Any] {
        let allFrames = frames(metadata)
        let lower = max(0, selected.frameIndex - DebugRecordingTriageLimits.contextRadius)
        let upper = min(allFrames.count - 1, selected.frameIndex + DebugRecordingTriageLimits.contextRadius)
        let context = lower...upper
        var result: [String: Any] = [
            "sessionID": string(metadata["sessionID"]) ?? "",
            "selectedFrameID": selected.frameID,
            "selectedColor": selected.color,
            "selectedFailureType": selected.failureType,
            "selectedReasons": selected.reasons,
            "contextRadiusFrames": DebugRecordingTriageLimits.contextRadius,
            "frames": context.map { minimalFrame(allFrames[$0], index: $0) }
        ]
        if let eventIndex = selected.eventIndex, let role = selected.role,
           let score = selected.score {
            result["motionEvent"] = ["eventIndex": eventIndex, "role": role,
                "score": score, "signals": selected.signals.map(\.dictionary),
                "signalAggregation": "event_max_per_kind"]
        }
        return result
    }

    private static func minimalFrame(_ frame: [String: Any], index: Int) -> [String: Any] {
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
                let limit = integer(diagnostic["eligibleCandidateCount"]) == 0 ? 3 : 1
                colorData["candidateDecisionTrace"] = top.prefix(limit).map { entry -> [String: Any] in
                    var trace: [String: Any] = [:]
                    for key in ["index", "sourceType", "eligible", "finalScore",
                                "rejectionReasons", "peakValue", "meanValue",
                                "highValueRatio", "meanColorPurity", "clippedWhiteRatio",
                                "isCompactRed", "rawPCASpan", "robustMainIntervalLength",
                                "continuity", "density", "componentArea", "pointCount"] {
                        if let value = entry[key] { trace[key] = value }
                    }
                    let rules = entry["eligibilityRules"] as? [[String: Any]] ?? []
                    trace["rules"] = rules.filter { string($0["result"]) == "FAIL" }
                    return trace
                }
            }
            output[color] = colorData
        }
        return output
    }

    private static func detectionSummary(metadata: [String: Any]) -> [String: Any] {
        let allFrames = frames(metadata)
        return Dictionary(uniqueKeysWithValues: colors.map { color in
            let detected = allFrames.filter { detectionStatus($0, color: color) == true }.count
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
        return Dictionary(uniqueKeysWithValues: colors.map { color in
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
        var grouped: [String: (String, String, [Int], Int)] = [:]
        var reasonsByFrame: [[Reason]] = Array(repeating: [], count: allFrames.count)
        for (index, frame) in allFrames.enumerated() {
            if frame["manualCaptured"] as? Bool == true {
                reasonsByFrame[index].append(Reason(color: "both", type: "manual_capture",
                                                    priority: 0, detail: "manual lossless capture"))
            }
            for color in colors { reasonsByFrame[index].append(contentsOf: reasons(for: allFrames, at: index, color: color)) }
            for color in colors where string(frame["\(color)DropoutRole"]) == "dropout"
                || isFirstFalseAfterDetection(allFrames, index: index, color: color) {
                reasonsByFrame[index].append(Reason(color: color, type: "dropout", priority: 1,
                                                    detail: "first false frame after a detected run"))
            }
        }
        addIdenticalEndpointReasons(frames: allFrames, reasonsByFrame: &reasonsByFrame)
        for (index, reasons) in reasonsByFrame.enumerated() {
            for reason in reasons {
                let key = "\(reason.color)|\(reason.type)"
                var item = grouped[key] ?? (reason.color, reason.type, [], reason.priority)
                item.2.append(index)
                grouped[key] = item
            }
        }
        return grouped.values.map { color, type, indexes, _ in
            let uniqueIndexes = Array(Set(indexes)).sorted()
            var incidents = 0
            var previous: Int?
            for index in uniqueIndexes {
                if previous.map({ index > $0 + 1 }) ?? true { incidents += 1 }
                previous = index
            }
            return ["color": color, "type": type, "incidentCount": incidents,
                    "affectedFrames": uniqueIndexes.count]
        }.sorted {
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

    private static func jsonData(_ object: Any) throws -> Data {
        try JSONSerialization.data(withJSONObject: object, options: [.prettyPrinted, .sortedKeys])
    }
}
