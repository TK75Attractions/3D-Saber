import CoreGraphics
import Foundation
import ImageIO

// MARK: - Auto clip (2026-10-19)
//
// While sending, keep a small rolling record of the recent past and save only the
// moments that look problematic, as a triage-protocol bundle the Mac receiver
// accepts (≤20 PNGs, one context JSON per PNG, compact per-frame metadata in
// summary.json). Read-only with respect to recognition: it consumes the analysis
// FrameProcessor already produced and never changes selection, thresholds, UDP or
// the camera.
//
// Cost model (see tools/ios-hot-path/autoclip.py for the host measurement):
// - every frame: a fixed-size metadata record (≤12 candidates per colour) written
//   into a preallocated ring, plus the trigger state machine. No allocation.
// - every 0.3 s: one half-resolution point-sampled BGRA copy (the pixels the
//   detector's step-2 mask scan reads) into a preallocated 21-slot ring.
// - only when a clip is taken: three full-resolution copies (trigger frame and the
//   next two), then PNG encoding on a utility queue after the 2 s post window.
// Memory at 480×640: 21 × 300 KiB + 3 × 1.2 MiB + ≈1 MiB metadata ≈ 11 MiB.

enum AutoClipSettings {
    static let enabledKey = "PhoneSaber.autoClip.enabled.v1"
    static let directoryName = "AutoClips"

    /// Default ON.
    static func storedEnabled(_ defaults: UserDefaults = .standard) -> Bool {
        defaults.object(forKey: enabledKey) as? Bool ?? true
    }
}

enum AutoClipLimits {
    static let preSeconds = 3.0
    static let postSeconds = 2.0
    /// Context frame spacing in the ring (≈3.3 fps): 10 before + 7 after fit,
    /// with the 3 full-resolution event frames, in the 20-PNG bundle limit.
    static let contextInterval = 0.3
    static let contextSlots = 21
    static let eventFrames = 3
    static let maximumImages = 20
    static let candidatesPerColor = 12
    static let ineligibleCandidatesPerColor = 2
    // 2026-10-21: 6本では1時間で使い切り、青の誤検出の場面が残らなかった。15秒間隔で1曲（約2.5分）最大10本、
    // 送信1回あたり60本まで（曲を続けても残る）。
    static let cooldownSeconds = 15.0
    static let maximumAutomaticClipsPerSession = 60
    static let manualMinimumInterval = 5.0
    /// A confusion trigger whose competitor sits where an earlier clip's competitor
    /// was (a static cone, a poster) is not saved again in the same session.
    static let repeatLocationDiagonalFraction = 0.06
    static let repeatLocationMemory = 8
    static let maximumStoredClips = 12
    static let maximumStoredBytes: Int64 = 160 * 1_024 * 1_024
    static let minimumFreeSpaceBytes: Int64 = 512 * 1_024 * 1_024
    /// Below the receiver's 512 KiB summary.json limit.
    static let maximumSummaryBytes = 480 * 1_024
}

/// Pure suspend rule: capture runs only at nominal/fair thermal state, at the
/// user's fps (not reduced by the heat policy), and outside Debug Recording.
enum AutoClipSuspendPolicy {
    static func reason(thermal: ProcessInfo.ThermalState, fpsReduced: Bool,
                       debugRecording: Bool) -> String? {
        if debugRecording { return "debugRecording" }
        switch thermal {
        case .nominal, .fair: break
        case .serious: return "thermalSerious"
        case .critical: return "thermalCritical"
        @unknown default: return "thermalUnknown"
        }
        return fpsReduced ? "fpsReduced" : nil
    }
}

// MARK: - Trigger rules (pure)

enum AutoClipTriggerPolicy {
    static let gapSelectedSeconds = 1.0
    static let gapFrames = 8
    // 消失直前0.25秒の端点移動（端の入替は除外）をプレイ継続の補助証拠にする。
    static let gapMotionSeconds = 0.25
    static let gapMotionDiagonalFraction = 0.01
    /// Frames in a row with a separated, eligible, selectable competitor.
    static let confusionFrames = 3
    /// The jump threshold is max(typical blade length, this × frame diagonal).
    static let minimumJumpDiagonalFraction = 0.15
    static let typicalLengthAlpha = 0.2
    /// Consecutive processed frames may skip one camera frame (mailbox replacement).
    static let maximumSequenceGap: UInt64 = 2
    static let boxMarginPixels = 4
}

enum AutoClipTriggerKind: String {
    case confusion, jump, gap, manual
}

struct AutoClipTriggerEvent: Equatable {
    var kind: AutoClipTriggerKind
    var color: SaberColor?
    var frameID: UInt64
    var timestamp: Double
    var winner: [Int]? = nil
    var competitor: [Int]? = nil
    var competitorCount = 0
    var previousWinner: [Int]? = nil
    var jumpDistance: Double? = nil
    var jumpThreshold: Double? = nil
    var typicalLength: Double? = nil
    var note: String? = nil
}

/// Result of comparing the production winner with the other candidates of one colour.
struct AutoClipCompetition: Equatable {
    var winnerIndex: Int?
    var competitorCount = 0
    var competitorIndex: Int?
    var eligibleCount = 0

    static func endpointsArray(_ endpoints: (PixelPoint, PixelPoint)) -> [Int] {
        [endpoints.0.x, endpoints.0.y, endpoints.1.x, endpoints.1.y]
    }

    /// 競合の範囲判定。追跡中の実winnerは範囲外にも出るため、ここから推定しない。
    static func selectable(_ candidate: SaberCandidate, width: Int, height: Int,
                           zones: [SaberIgnoreZone], playArea: SaberPlayArea?) -> Bool {
        candidate.isEmitterEligible
            && (zones.isEmpty || !saberIgnored(candidate.endpoints, width: width, height: height, zones: zones))
            && (playArea?.containsMidpoint(candidate.endpoints, width: width, height: height) ?? true)
    }

    /// Two candidates are different objects when their boxes do not touch, or their
    /// midpoints are farther apart than their mean length (a duplicate proposal of
    /// one blade overlaps it and shares its middle).
    static func separated(_ a: SaberCandidate, _ b: SaberCandidate) -> Bool {
        let m = AutoClipTriggerPolicy.boxMarginPixels
        let boxA = a.boundingBox, boxB = b.boundingBox
        if boxA.maxX + m < boxB.minX || boxB.maxX + m < boxA.minX
            || boxA.maxY + m < boxB.minY || boxB.maxY + m < boxA.minY { return true }
        let distance = midpointDistance(a.endpoints, b.endpoints)
        return distance > (length(a.endpoints) + length(b.endpoints)) / 2
    }

    // winner は必ず analyzeSabers の実出力から照合する。nil は出力なし。
    static func evaluate(_ candidates: [SaberCandidate], selected: (PixelPoint, PixelPoint)?,
                         width: Int, height: Int,
                         zones: [SaberIgnoreZone], playArea: SaberPlayArea?) -> AutoClipCompetition {
        var result = AutoClipCompetition()
        result.winnerIndex = selected.flatMap { endpoints in
            candidates.firstIndex { $0.endpoints.0 == endpoints.0 && $0.endpoints.1 == endpoints.1 }
        }
        for index in candidates.indices {
            let candidate = candidates[index]
            if candidate.isEmitterEligible { result.eligibleCount += 1 }
            guard let winner = result.winnerIndex, index != winner,
                  selectable(candidate, width: width, height: height, zones: zones, playArea: playArea),
                  separated(candidates[winner], candidate) else { continue }
            result.competitorCount += 1
            if result.competitorIndex == nil { result.competitorIndex = index }
        }
        return result
    }

    static func length(_ endpoints: (PixelPoint, PixelPoint)) -> Double {
        let dx = Double(endpoints.1.x - endpoints.0.x), dy = Double(endpoints.1.y - endpoints.0.y)
        return (dx * dx + dy * dy).squareRoot()
    }

    static func midpointDistance(_ a: (PixelPoint, PixelPoint), _ b: (PixelPoint, PixelPoint)) -> Double {
        let dx = Double(a.0.x + a.1.x - b.0.x - b.1.x) / 2
        let dy = Double(a.0.y + a.1.y - b.0.y - b.1.y) / 2
        return (dx * dx + dy * dy).squareRoot()
    }
}

/// Per-colour trigger state machine. Pure value type: no clock, no I/O.
struct AutoClipTriggerDetector {
    private struct ColorState {
        var confusionRun = 0
        var lastWinner: (PixelPoint, PixelPoint)?
        var lastSequence: UInt64?
        var typicalLength: Double?
        var lastTimestamp: Double?
        var selectedSince: Double?
        var lastMovingAt: Double?
        var gapArmed = false
        var gapWasMoving = false
        var missingRun = 0
        var gapWinner: (PixelPoint, PixelPoint)?
    }
    private var red = ColorState()
    private var blue = ColorState()

    mutating func reset() { red = ColorState(); blue = ColorState() }

    /// `winner` is the production selection; `competitor` the best-ranked
    /// separated eligible selectable candidate (nil when `competitorCount` is 0).
    mutating func observe(color: SaberColor, sequence: UInt64, timestamp: Double,
                          winner: (PixelPoint, PixelPoint)?, competitorCount: Int,
                          competitor: (PixelPoint, PixelPoint)?,
                          width: Int, height: Int, otherSelected: Bool = false) -> AutoClipTriggerEvent? {
        var state = color == .red ? red : blue
        defer { if color == .red { red = state } else { blue = state } }
        let consecutive = state.lastSequence.map { sequence > $0 && sequence - $0 <= AutoClipTriggerPolicy.maximumSequenceGap } ?? false
        var event: AutoClipTriggerEvent?

        // gap は実選択だけを見る。候補が残っていても出力がなければ消失として扱う。
        // camera frame の間引きでは途絶をリセットせず、処理されたフレームを数える。
        let gapConsecutive = state.lastSequence.map { sequence > $0 } == true && timestamp.isFinite && state.lastTimestamp.map {
            timestamp > $0 && timestamp - $0 <= 1
        } == true
        if !gapConsecutive {
            state.selectedSince = nil
            state.lastMovingAt = nil
            state.gapArmed = false
            state.gapWasMoving = false
            state.missingRun = 0
            state.gapWinner = nil
        }
        if let winner {
            if gapConsecutive, let previous = state.lastWinner {
                func distance(_ a: PixelPoint, _ b: PixelPoint) -> Double {
                    hypot(Double(a.x - b.x), Double(a.y - b.y))
                }
                let movement = min(max(distance(previous.0, winner.0), distance(previous.1, winner.1)),
                                   max(distance(previous.0, winner.1), distance(previous.1, winner.0)))
                let threshold = AutoClipTriggerPolicy.gapMotionDiagonalFraction * hypot(Double(width), Double(height))
                if movement >= threshold { state.lastMovingAt = timestamp }
            }
            state.selectedSince = state.selectedSince ?? timestamp
            state.gapArmed = timestamp - (state.selectedSince ?? timestamp) >= AutoClipTriggerPolicy.gapSelectedSeconds
            state.gapWasMoving = state.lastMovingAt.map { timestamp - $0 <= AutoClipTriggerPolicy.gapMotionSeconds } ?? false
            state.gapWinner = winner
            state.missingRun = 0
        } else {
            state.selectedSince = nil
            if state.gapArmed && (otherSelected || state.gapWasMoving) {
                state.missingRun += 1
                if state.missingRun == AutoClipTriggerPolicy.gapFrames {
                    event = AutoClipTriggerEvent(kind: .gap, color: color, frameID: sequence, timestamp: timestamp,
                        previousWinner: state.gapWinner.map(AutoClipCompetition.endpointsArray))
                    state.gapArmed = false
                }
            } else {
                state.gapArmed = false
                state.missingRun = 0
            }
        }

        if winner != nil && competitorCount > 0 && (consecutive || state.confusionRun == 0) {
            state.confusionRun += 1
        } else {
            state.confusionRun = winner != nil && competitorCount > 0 ? 1 : 0
        }
        if state.confusionRun == AutoClipTriggerPolicy.confusionFrames, let winner {
            event = AutoClipTriggerEvent(
                kind: .confusion, color: color, frameID: sequence, timestamp: timestamp,
                winner: AutoClipCompetition.endpointsArray(winner),
                competitor: competitor.map(AutoClipCompetition.endpointsArray),
                competitorCount: competitorCount)
        }

        if let winner {
            let length = AutoClipCompetition.length(winner)
            var jumped = false
            if consecutive, let previous = state.lastWinner {
                let diagonal = (Double(width * width + height * height)).squareRoot()
                let threshold = max(state.typicalLength ?? 0,
                                    AutoClipTriggerPolicy.minimumJumpDiagonalFraction * diagonal)
                let distance = AutoClipCompetition.midpointDistance(previous, winner)
                if distance > threshold {
                    jumped = true
                    if event == nil {
                        event = AutoClipTriggerEvent(
                            kind: .jump, color: color, frameID: sequence, timestamp: timestamp,
                            winner: AutoClipCompetition.endpointsArray(winner),
                            competitor: competitor.map(AutoClipCompetition.endpointsArray),
                            competitorCount: competitorCount,
                            previousWinner: AutoClipCompetition.endpointsArray(previous),
                            jumpDistance: distance, jumpThreshold: threshold,
                            typicalLength: state.typicalLength)
                    }
                }
            }
            // A jump frame's (possibly tiny, far) winner does not teach the typical length.
            if !jumped {
                let alpha = AutoClipTriggerPolicy.typicalLengthAlpha
                state.typicalLength = state.typicalLength.map { $0 + alpha * (length - $0) } ?? length
            }
            state.lastWinner = winner
        } else {
            state.lastWinner = nil
        }
        state.lastSequence = sequence
        state.lastTimestamp = timestamp
        return event
    }
}

/// Pure rate limiting and repeat suppression.
struct AutoClipRateLimiter {
    enum Decision: Equatable {
        case accept
        case reject(String)
    }

    private(set) var automaticClipsThisSession = 0
    private var lastAutomaticTime: Double?
    private var lastManualTime: Double?
    private var competitorLocations: [(color: SaberColor, x: Double, y: Double)] = []
    private(set) var rejected: [String: Int] = [:]

    mutating func beginSession() {
        automaticClipsThisSession = 0
        lastAutomaticTime = nil
        lastManualTime = nil
        competitorLocations.removeAll(keepingCapacity: true)
        rejected.removeAll(keepingCapacity: true)
    }

    /// `busy`: a clip is still collecting its post window or being written.
    mutating func admit(_ event: AutoClipTriggerEvent, busy: Bool, width: Int, height: Int) -> Decision {
        let decision = evaluate(event, busy: busy, width: width, height: height)
        switch decision {
        case .accept:
            if event.kind == .manual {
                lastManualTime = event.timestamp
            } else {
                lastAutomaticTime = event.timestamp
                automaticClipsThisSession += 1
                if event.kind == .confusion, let color = event.color, let point = Self.midpoint(event.competitor) {
                    competitorLocations.append((color, point.x, point.y))
                    if competitorLocations.count > AutoClipLimits.repeatLocationMemory {
                        competitorLocations.removeFirst()
                    }
                }
            }
        case .reject(let reason):
            rejected[reason, default: 0] += 1
        }
        return decision
    }

    private func evaluate(_ event: AutoClipTriggerEvent, busy: Bool, width: Int, height: Int) -> Decision {
        if busy { return .reject("busy") }
        if event.kind == .manual {
            if let last = lastManualTime, event.timestamp >= last,
               event.timestamp - last < AutoClipLimits.manualMinimumInterval {
                return .reject("manualInterval")
            }
            return .accept
        }
        if automaticClipsThisSession >= AutoClipLimits.maximumAutomaticClipsPerSession {
            return .reject("sessionLimit")
        }
        if let last = lastAutomaticTime, event.timestamp >= last,
           event.timestamp - last < AutoClipLimits.cooldownSeconds {
            return .reject("cooldown")
        }
        if event.kind == .confusion, let color = event.color, let point = Self.midpoint(event.competitor) {
            let radius = AutoClipLimits.repeatLocationDiagonalFraction
                * (Double(width * width + height * height)).squareRoot()
            if competitorLocations.contains(where: {
                $0.color == color && (($0.x - point.x) * ($0.x - point.x) + ($0.y - point.y) * ($0.y - point.y)).squareRoot() <= radius
            }) {
                return .reject("repeatLocation")
            }
        }
        return .accept
    }

    private static func midpoint(_ endpoints: [Int]?) -> (x: Double, y: Double)? {
        guard let e = endpoints, e.count == 4 else { return nil }
        return (Double(e[0] + e[2]) / 2, Double(e[1] + e[3]) / 2)
    }
}

// MARK: - Fixed-size per-frame records

struct AutoClipCandidateRecord {
    static let eligible: UInt8 = 1, selectable: UInt8 = 2, selected: UInt8 = 4, competitor: UInt8 = 8

    var rank: Int16 = 0
    var x1: Int32 = 0, y1: Int32 = 0, x2: Int32 = 0, y2: Int32 = 0
    var minX: Int32 = 0, minY: Int32 = 0, maxX: Int32 = 0, maxY: Int32 = 0
    var score: Float = 0
    var peakValue: Int16 = 0
    var emitterScore: Float? = nil
    var highValueRatio: Float = 0, meanValue: Float = 0, colorPurity: Float = 0
    var nearSaturationPresent: Bool? = nil
    var componentArea: Int32 = 0
    var aspect: Float? = nil
    var flags: UInt8 = 0
    var source = ""
}

struct AutoClipColorRecord {
    var candidateCount: Int16 = 0
    var eligibleCount: Int16 = 0
    var competitorCount: Int16 = 0
    var stored: UInt8 = 0
    var trackLockSuppressed = false
    var hasSelection = false
    var x1: Int32 = 0, y1: Int32 = 0, x2: Int32 = 0, y2: Int32 = 0
}

struct AutoClipFrameRecord {
    var frameID: UInt64 = 0
    var timestamp: Double = 0
    var processingMs: Float = 0
    var red = AutoClipColorRecord()
    var blue = AutoClipColorRecord()
}

/// Preallocated buffers. The detection thread owns an instance until a clip is
/// finalized; the instance then moves to the writer and a fresh one is allocated.
final class AutoClipBuffers {
    let width: Int
    let height: Int
    let halfWidth: Int
    let halfHeight: Int
    let frameCapacity: Int
    let contextPixels: UnsafeMutablePointer<UInt32>
    let eventPixels: UnsafeMutablePointer<UInt32>
    var contextFrameIDs: [UInt64]
    var contextTimes: [Double]
    var contextValid: [Bool]
    var contextNext = 0
    var lastContextTime = -Double.infinity
    var eventFrameIDs: [UInt64]
    var eventTimes: [Double]
    var eventCount = 0
    var frames: [AutoClipFrameRecord]
    var candidates: [AutoClipCandidateRecord]
    var frameNext = 0
    var frameCount = 0

    var halfPixelCount: Int { halfWidth * halfHeight }
    var byteCount: Int {
        (AutoClipLimits.contextSlots * halfPixelCount + AutoClipLimits.eventFrames * width * height) * 4
    }

    init(width: Int, height: Int, captureFPS: Int) {
        self.width = width
        self.height = height
        halfWidth = (width + 1) / 2
        halfHeight = (height + 1) / 2
        frameCapacity = Int(((AutoClipLimits.preSeconds + AutoClipLimits.postSeconds + 0.5)
                             * Double(max(min(captureFPS, 240), 30))).rounded(.up))
        // Uninitialized: pages are committed only when a frame is written.
        contextPixels = .allocate(capacity: AutoClipLimits.contextSlots * halfWidth * halfHeight)
        eventPixels = .allocate(capacity: AutoClipLimits.eventFrames * width * height)
        contextFrameIDs = Array(repeating: 0, count: AutoClipLimits.contextSlots)
        contextTimes = Array(repeating: 0, count: AutoClipLimits.contextSlots)
        contextValid = Array(repeating: false, count: AutoClipLimits.contextSlots)
        eventFrameIDs = Array(repeating: 0, count: AutoClipLimits.eventFrames)
        eventTimes = Array(repeating: 0, count: AutoClipLimits.eventFrames)
        frames = Array(repeating: AutoClipFrameRecord(), count: frameCapacity)
        candidates = Array(repeating: AutoClipCandidateRecord(),
                           count: frameCapacity * 2 * AutoClipLimits.candidatesPerColor)
    }

    deinit {
        contextPixels.deallocate()
        eventPixels.deallocate()
    }

    /// Half resolution by taking every second pixel of every second row: exactly the
    /// samples the detector's step-2 mask scan reads.
    func storeContext(_ frame: ObservedFrame) {
        let slot = contextNext
        let destination = contextPixels + slot * halfPixelCount
        for row in 0..<halfHeight {
            let source = UnsafeRawPointer(frame.baseAddress + row * 2 * frame.bytesPerRow)
                .assumingMemoryBound(to: UInt32.self)
            let output = destination + row * halfWidth
            for column in 0..<halfWidth { output[column] = source[column * 2] }
        }
        contextFrameIDs[slot] = frame.sequence
        contextTimes[slot] = frame.timestamp
        contextValid[slot] = true
        contextNext = (slot + 1) % AutoClipLimits.contextSlots
        lastContextTime = frame.timestamp
    }

    func storeEvent(_ frame: ObservedFrame) {
        guard eventCount < AutoClipLimits.eventFrames else { return }
        let destination = UnsafeMutableRawPointer(eventPixels + eventCount * width * height)
        for row in 0..<height {
            memcpy(destination + row * width * 4, frame.baseAddress + row * frame.bytesPerRow, width * 4)
        }
        eventFrameIDs[eventCount] = frame.sequence
        eventTimes[eventCount] = frame.timestamp
        eventCount += 1
    }

    /// Writes the metadata record of this frame in place (no allocation).
    func storeRecord(_ frame: ObservedFrame, red: AutoClipCompetition, blue: AutoClipCompetition) {
        let slot = frameNext
        frames[slot].frameID = frame.sequence
        frames[slot].timestamp = frame.timestamp
        frames[slot].processingMs = Float(frame.processingSeconds * 1000)
        fill(&frames[slot].red, color: .red, competition: red, frame: frame, slot: slot, colorIndex: 0)
        fill(&frames[slot].blue, color: .blue, competition: blue, frame: frame, slot: slot, colorIndex: 1)
        frameNext = (slot + 1) % frameCapacity
        frameCount = min(frameCount + 1, frameCapacity)
    }

    private func fill(_ record: inout AutoClipColorRecord, color: SaberColor, competition: AutoClipCompetition,
                      frame: ObservedFrame, slot: Int, colorIndex: Int) {
        let list = frame.analysis.candidates[color] ?? []
        record.trackLockSuppressed = frame.analysis.trackLockSuppressed.contains(color)
        record.candidateCount = Int16(clamping: list.count)
        record.eligibleCount = Int16(clamping: competition.eligibleCount)
        record.competitorCount = Int16(clamping: competition.competitorCount)
        if let selected = frame.analysis.selected[color] {
            record.hasSelection = true
            record.x1 = Int32(clamping: selected.0.x); record.y1 = Int32(clamping: selected.0.y)
            record.x2 = Int32(clamping: selected.1.x); record.y2 = Int32(clamping: selected.1.y)
        } else {
            record.hasSelection = false
        }
        let zones = frame.ignoreZones[color] ?? []
        let base = (slot * 2 + colorIndex) * AutoClipLimits.candidatesPerColor
        var stored = 0
        // 固定リングを維持し、winner → 全分離競合 → その他eligible → ineligible の順に保存。
        // 各段内は元のrank順。競合が上限を超えた場合も k と stored 数から欠落を判別できる。
        for priority in 0...3 {
            var ineligible = 0
            for index in list.indices where stored < AutoClipLimits.candidatesPerColor {
                let candidate = list[index]
                let selectable = AutoClipCompetition.selectable(candidate, width: frame.width, height: frame.height,
                                                                zones: zones, playArea: frame.playArea)
                let selected = index == competition.winnerIndex
                let competitor = !selected && selectable && competition.winnerIndex.map {
                    AutoClipCompetition.separated(list[$0], candidate)
                } == true
                let category = selected ? 0 : competitor ? 1 : candidate.isEmitterEligible ? 2 : 3
                guard category == priority else { continue }
                if category == 3 {
                    guard ineligible < AutoClipLimits.ineligibleCandidatesPerColor else { continue }
                    ineligible += 1
                }
                var flags: UInt8 = 0
                if candidate.isEmitterEligible { flags |= AutoClipCandidateRecord.eligible }
                if selectable { flags |= AutoClipCandidateRecord.selectable }
                if selected { flags |= AutoClipCandidateRecord.selected }
                if competitor { flags |= AutoClipCandidateRecord.competitor }
                let target = base + stored
                candidates[target].rank = Int16(clamping: index)
                candidates[target].x1 = Int32(clamping: candidate.endpoints.0.x)
                candidates[target].y1 = Int32(clamping: candidate.endpoints.0.y)
                candidates[target].x2 = Int32(clamping: candidate.endpoints.1.x)
                candidates[target].y2 = Int32(clamping: candidate.endpoints.1.y)
                candidates[target].minX = Int32(clamping: candidate.boundingBox.minX)
                candidates[target].minY = Int32(clamping: candidate.boundingBox.minY)
                candidates[target].maxX = Int32(clamping: candidate.boundingBox.maxX)
                candidates[target].maxY = Int32(clamping: candidate.boundingBox.maxY)
                candidates[target].score = Float(candidate.score)
                candidates[target].peakValue = Int16(clamping: candidate.peakValue)
                candidates[target].emitterScore = candidate.observerEmitterInputs.map { Float($0.emitterScore) }
                candidates[target].highValueRatio = Float(candidate.highValueRatio)
                candidates[target].meanValue = Float(candidate.meanValue)
                candidates[target].nearSaturationPresent = candidate.observerEmitterInputs?.nearSaturationPresent
                candidates[target].colorPurity = Float(candidate.meanColorPurity)
                candidates[target].componentArea = Int32(clamping: candidate.componentArea)
                candidates[target].aspect = candidate.observerEmitterInputs.map { Float($0.aspect) }
                candidates[target].flags = flags
                candidates[target].source = candidate.source
                stored += 1
            }
        }
        record.stored = UInt8(stored)
    }

    /// Ring slots in time order whose timestamps fall inside [start, end].
    func frameSlots(from start: Double, to end: Double) -> [Int] {
        var slots: [Int] = []
        slots.reserveCapacity(frameCount)
        let first = frameCount < frameCapacity ? 0 : frameNext
        for offset in 0..<frameCount {
            let slot = (first + offset) % frameCapacity
            let time = frames[slot].timestamp
            if time >= start && time <= end { slots.append(slot) }
        }
        return slots
    }

    func contextSlots(from start: Double, to end: Double) -> [Int] {
        (0..<AutoClipLimits.contextSlots)
            .filter { contextValid[$0] && contextTimes[$0] >= start && contextTimes[$0] <= end }
            .sorted { contextTimes[$0] < contextTimes[$1] }
    }
}

// MARK: - Recorder (frame queue)

struct AutoClipEnvironment: Equatable {
    var thermal = "nominal"
    var exposureBias = 0
    var shutter = 0
    var shutterISO = 2000
    var shutterEmitter = false
    var exposureDurationSeconds: Double?
    var iso: Double?
    var appVersion = ""
    var signingExpiryRemainingHours: Int?
}

struct AutoClipStatus: Equatable {
    var enabled = false
    var suspendedReason: String?
    var active = false
    var savedThisSession = 0
    var savedTotal = 0
    var lastSavedClipID: String?
    var lastError: String?
    var rejected: [String: Int] = [:]
}

/// Everything the writer needs for one clip; owns its buffers.
struct AutoClipCapture {
    let clipID: String
    let trigger: AutoClipTriggerEvent
    let buffers: AutoClipBuffers
    let windowStart: Double
    let windowEnd: Double
    let captureFPS: Int
    let environmentAtTrigger: AutoClipEnvironment
    let environmentAtSave: AutoClipEnvironment
    let ignoreZones: [SaberColor: [SaberIgnoreZone]]
    let playArea: SaberPlayArea?
    let sessionClipIndex: Int
    let rejectedTriggers: [String: Int]
}

final class AutoClipRecorder: FrameAnalysisObserver {
    private struct Pending {
        let event: AutoClipTriggerEvent
        let end: Double
        let environment: AutoClipEnvironment
        let zones: [SaberColor: [SaberIgnoreZone]]
        let area: SaberPlayArea?
        let captureFPS: Int
    }

    private let lock = NSLock()
    // Guarded by `lock` (written from the main actor / writer queue, read on the frame queue).
    private var enabled: Bool
    private var suspendedReason: String?
    private var manualRequest: String?
    private var environment = AutoClipEnvironment()
    private var sessionResetPending = false
    private var writesInFlight = 0
    private var status = AutoClipStatus()

    // Frame queue only.
    private var buffers: AutoClipBuffers?
    private var detector = AutoClipTriggerDetector()
    private var limiter = AutoClipRateLimiter()
    private var pending: Pending?
    private var lastTimestamp: Double?
    private let writer: AutoClipWriter
    private let clipNamer: () -> String

    /// Called on the writer queue after a clip bundle was published (or failed).
    var onClipSaved: ((Result<URL, Error>) -> Void)?
    /// 手動指示ごとの完了。検出・writer のキューを待たせない通知だけを行う。
    var onManualClipResult: ((String, Bool) -> Void)?
    /// Short log lines (trigger accepted/rejected, suspend changes); any thread.
    var onLog: ((String) -> Void)?

    init(directory: URL, enabled: Bool,
         clipNamer: @escaping () -> String = AutoClipRecorder.timestampName) {
        self.enabled = enabled
        self.writer = AutoClipWriter(directory: directory.appendingPathComponent(AutoClipSettings.directoryName))
        self.clipNamer = clipNamer
        status.enabled = enabled
    }

    static func timestampName() -> String {
        let formatter = DateFormatter()
        formatter.locale = Locale(identifier: "en_US_POSIX")
        formatter.dateFormat = "yyyyMMdd_HHmmss_SSS"
        return formatter.string(from: Date())
    }

    var directory: URL { writer.directory }

    // MARK: Control (any thread)

    func setEnabled(_ value: Bool) {
        lock.lock(); enabled = value; status.enabled = value; lock.unlock()
    }

    func setSuspended(_ reason: String?) {
        lock.lock()
        let changed = suspendedReason != reason
        suspendedReason = reason
        status.suspendedReason = reason
        lock.unlock()
        if changed { onLog?(reason.map { "suspend reason=\($0)" } ?? "resume") }
    }

    func updateEnvironment(_ value: AutoClipEnvironment) {
        lock.lock(); environment = value; lock.unlock()
    }

    /// A new sending session: per-session clip counts start again.
    func beginSession() {
        lock.lock()
        let previous = manualRequest
        manualRequest = nil
        sessionResetPending = true; status.savedThisSession = 0
        lock.unlock()
        if let previous { onManualClipResult?(previous, false) }
    }

    /// Save a clip around the next frame (PC command).
    func requestClip(reason: String) {
        lock.lock(); let previous = manualRequest; manualRequest = reason; lock.unlock()
        if let previous { onManualClipResult?(previous, false) }
    }

    var statusSnapshot: AutoClipStatus {
        lock.lock(); defer { lock.unlock() }
        return status
    }

    // MARK: Frame queue

    func observe(_ frame: ObservedFrame) {
        lock.lock()
        let isEnabled = enabled
        let suspended = suspendedReason
        let manual = manualRequest
        manualRequest = nil
        let environmentNow = environment
        let resetSession = sessionResetPending
        sessionResetPending = false
        let writerBusy = writesInFlight > 1
        status.active = isEnabled && suspended == nil
        lock.unlock()

        // 停止直後の再開でも旧セッションの収集中clip・画像を持ち越さない。
        if resetSession { dropState(); limiter.beginSession() }
        guard isEnabled, suspended == nil, frame.timestamp.isFinite else {
            dropState()
            if let manual {
                onLog?("clip skipped reason=\(!isEnabled ? "disabled" : suspended ?? "invalidTimestamp") request=\(manual)")
                onManualClipResult?(manual, false)
            }
            return
        }
        // A new format or a time discontinuity invalidates the recorded past.
        if let current = buffers, current.width != frame.width || current.height != frame.height {
            dropState()
        }
        if let last = lastTimestamp, !(frame.timestamp > last) || frame.timestamp - last > 1 {
            dropState()
        }
        lastTimestamp = frame.timestamp
        let buffers: AutoClipBuffers
        if let existing = self.buffers {
            buffers = existing
        } else {
            buffers = AutoClipBuffers(width: frame.width, height: frame.height, captureFPS: frame.captureFPS)
            self.buffers = buffers
        }

        let redCandidates = frame.analysis.candidates[.red] ?? []
        let blueCandidates = frame.analysis.candidates[.blue] ?? []
        let red = AutoClipCompetition.evaluate(redCandidates, selected: frame.analysis.selected[.red], width: frame.width, height: frame.height,
                                               zones: frame.ignoreZones[.red] ?? [], playArea: frame.playArea)
        let blue = AutoClipCompetition.evaluate(blueCandidates, selected: frame.analysis.selected[.blue], width: frame.width, height: frame.height,
                                                zones: frame.ignoreZones[.blue] ?? [], playArea: frame.playArea)
        buffers.storeRecord(frame, red: red, blue: blue)
        if frame.timestamp - buffers.lastContextTime >= AutoClipLimits.contextInterval {
            buffers.storeContext(frame)
        }

        // The detector always advances (its state stays current during a pending clip).
        let redEvent = detector.observe(
            color: .red, sequence: frame.sequence, timestamp: frame.timestamp,
            winner: frame.analysis.selected[.red], competitorCount: red.competitorCount,
            competitor: red.competitorIndex.map { redCandidates[$0].endpoints },
            width: frame.width, height: frame.height, otherSelected: frame.analysis.selected[.blue] != nil)
        let blueEvent = detector.observe(
            color: .blue, sequence: frame.sequence, timestamp: frame.timestamp,
            winner: frame.analysis.selected[.blue], competitorCount: blue.competitorCount,
            competitor: blue.competitorIndex.map { blueCandidates[$0].endpoints },
            width: frame.width, height: frame.height, otherSelected: frame.analysis.selected[.red] != nil)
        let manualEvent = manual.map {
            AutoClipTriggerEvent(kind: .manual, color: nil, frameID: frame.sequence,
                                 timestamp: frame.timestamp, note: $0)
        }

        if let current = pending {
            buffers.storeEvent(frame)
            if manualEvent != nil || redEvent != nil || blueEvent != nil {
                for event in [manualEvent, redEvent, blueEvent].compactMap({ $0 }) {
                    _ = limiter.admit(event, busy: true, width: frame.width, height: frame.height)
                    if event.kind == .manual {
                        onLog?("clip skipped reason=busy request=\(event.note ?? "")")
                        onManualClipResult?(event.note ?? "", false)
                    }
                }
            }
            if frame.timestamp >= current.end { finalize(current, buffers: buffers, environmentNow: environmentNow) }
        } else if manualEvent != nil || redEvent != nil || blueEvent != nil {
            for event in [manualEvent, redEvent, blueEvent].compactMap({ $0 }) {
                let decision = limiter.admit(event, busy: writerBusy, width: frame.width, height: frame.height)
                if case .reject(let reason) = decision {
                    if event.kind == .manual {
                        onLog?("clip skipped reason=\(reason) request=\(event.note ?? "")")
                        onManualClipResult?(event.note ?? "", false)
                    }
                    continue
                }
                pending = Pending(event: event, end: frame.timestamp + AutoClipLimits.postSeconds,
                                  environment: environmentNow, zones: frame.ignoreZones,
                                  area: frame.playArea, captureFPS: frame.captureFPS)
                buffers.storeEvent(frame)
                onLog?("trigger kind=\(event.kind.rawValue) color=\(Self.colorName(event.color)) frame=\(event.frameID)"
                       + (event.jumpDistance.map { String(format: " jump=%.0fpx", $0) } ?? "")
                       + (event.kind == .confusion ? " competitors=\(event.competitorCount)" : "")
                       + (event.note.map { " note=\($0)" } ?? ""))
                break
            }
            lock.lock(); status.rejected = limiter.rejected; lock.unlock()
        }
    }

    private func dropState() {
        if let pending {
            onLog?("clip dropped kind=\(pending.event.kind.rawValue) (suspended, format change or time gap)")
            if pending.event.kind == .manual { onManualClipResult?(pending.event.note ?? "", false) }
        }
        pending = nil
        buffers = nil
        lastTimestamp = nil
        detector.reset()
    }

    private func finalize(_ current: Pending, buffers: AutoClipBuffers, environmentNow: AutoClipEnvironment) {
        pending = nil
        self.buffers = nil   // the next frame allocates a fresh ring; this one moves to the writer
        let suffix = current.event.kind.rawValue + (current.event.color.map { "_" + Self.colorName($0) } ?? "")
        let capture = AutoClipCapture(
            clipID: "autoclip_\(clipNamer())_\(suffix)", trigger: current.event, buffers: buffers,
            windowStart: current.event.timestamp - AutoClipLimits.preSeconds, windowEnd: current.end,
            captureFPS: current.captureFPS,
            environmentAtTrigger: current.environment, environmentAtSave: environmentNow,
            ignoreZones: current.zones, playArea: current.area,
            sessionClipIndex: limiter.automaticClipsThisSession, rejectedTriggers: limiter.rejected)
        lock.lock(); writesInFlight += 1; lock.unlock()
        writer.write(capture) { [weak self] result in
            guard let self else { return }
            self.lock.lock()
            self.writesInFlight = max(0, self.writesInFlight - 1)
            switch result {
            case .success(let url):
                self.status.savedThisSession += 1
                self.status.savedTotal += 1
                self.status.lastSavedClipID = url.lastPathComponent
                self.status.lastError = nil
            case .failure(let error):
                self.status.lastError = error.localizedDescription
            }
            self.lock.unlock()
            self.onClipSaved?(result)
            if current.event.kind == .manual {
                if case .success = result { self.onManualClipResult?(current.event.note ?? "", true) }
                else { self.onManualClipResult?(current.event.note ?? "", false) }
            }
        }
    }

    static func colorName(_ color: SaberColor?) -> String {
        switch color {
        case .red?: return "red"
        case .blue?: return "blue"
        case nil: return "both"
        }
    }
}

// MARK: - Writer (utility queue)

enum AutoClipWriterError: LocalizedError {
    case lowDiskSpace(Int64)
    case noFrames
    case encodeFailed(String)

    var errorDescription: String? {
        switch self {
        case .lowDiskSpace(let bytes): return "空き容量不足（\(bytes / 1_048_576) MiB）"
        case .noFrames: return "保存するフレームがありません"
        case .encodeFailed(let name): return "PNG を書けません（\(name)）"
        }
    }
}

final class AutoClipWriter {
    let directory: URL
    private let queue = DispatchQueue(label: "PhoneSaberSender.autoclip-writer", qos: .utility)

    init(directory: URL) {
        self.directory = directory
    }

    func write(_ capture: AutoClipCapture, completion: @escaping (Result<URL, Error>) -> Void) {
        queue.async { [self] in
            do {
                completion(.success(try writeSynchronously(capture)))
            } catch {
                completion(.failure(error))
            }
        }
    }

    /// Writes and atomically publishes `phone_saber_triage_<clipID>`.
    func writeSynchronously(_ capture: AutoClipCapture) throws -> URL {
        let fileManager = FileManager.default
        try fileManager.createDirectory(at: directory, withIntermediateDirectories: true)
        removeLeftovers()
        if let available = try? DebugRecordingStorageProbe.availableCapacity(at: directory),
           available < AutoClipLimits.minimumFreeSpaceBytes {
            throw AutoClipWriterError.lowDiskSpace(available)
        }
        let buffers = capture.buffers
        let slots = buffers.frameSlots(from: capture.windowStart, to: capture.windowEnd)
        guard !slots.isEmpty else { throw AutoClipWriterError.noFrames }
        let temporary = directory.appendingPathComponent(".partial_\(capture.clipID)")
        let final = directory.appendingPathComponent("phone_saber_triage_\(capture.clipID)")
        try? fileManager.removeItem(at: temporary)
        try fileManager.createDirectory(at: temporary.appendingPathComponent("images"), withIntermediateDirectories: true)
        try fileManager.createDirectory(at: temporary.appendingPathComponent("frames"), withIntermediateDirectories: true)
        do {
            let content = AutoClipBundleContent(capture: capture, slots: slots)
            var images: [[String: Any]] = []
            for (number, image) in content.images.enumerated() {
                let index = number + 1
                let name = String(format: "image_%02d_%@_%llu.png", index, image.role, image.frameID)
                let pixels = image.role == "event"
                    ? buffers.eventPixels + image.slot * buffers.width * buffers.height
                    : buffers.contextPixels + image.slot * buffers.halfPixelCount
                let width = image.role == "event" ? buffers.width : buffers.halfWidth
                let height = image.role == "event" ? buffers.height : buffers.halfHeight
                try writePNG(pixels, width: width, height: height,
                             to: temporary.appendingPathComponent("images/\(name)"))
                let contextPath = "frames/frame_\(image.frameID)_\(index).json"
                let contextData = try JSONSerialization.data(
                    withJSONObject: content.frameContext(frameID: image.frameID, sessionID: capture.clipID),
                    options: [.sortedKeys])
                try contextData.write(to: temporary.appendingPathComponent(contextPath))
                images.append(content.imageEntry(image, path: "images/\(name)", contextPath: contextPath,
                                                 sessionID: capture.clipID))
            }
            let summary = try content.summaryData(images: images)
            try summary.write(to: temporary.appendingPathComponent("summary.json"))
            try Data(content.prompt().utf8).write(to: temporary.appendingPathComponent("prompt.md"))
            try? fileManager.removeItem(at: final)
            try fileManager.moveItem(at: temporary, to: final)
        } catch {
            try? fileManager.removeItem(at: temporary)
            throw error
        }
        prune(keeping: final)
        return final
    }

    /// Lossless PNG straight from the BGRA bytes with ImageIO on the CPU (no Core
    /// Image / GPU context to set up; camera alpha is always 255, so RGB is exact).
    private func writePNG(_ pixels: UnsafeMutablePointer<UInt32>, width: Int, height: Int, to url: URL) throws {
        let data = Data(bytesNoCopy: UnsafeMutableRawPointer(pixels), count: width * height * 4, deallocator: .none)
        guard let provider = CGDataProvider(data: data as CFData),
              let image = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32,
                                  bytesPerRow: width * 4, space: CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGBitmapInfo(rawValue: CGBitmapInfo.byteOrder32Little.rawValue
                                                           | CGImageAlphaInfo.noneSkipFirst.rawValue),
                                  provider: provider, decode: nil, shouldInterpolate: false, intent: .defaultIntent),
              let destination = CGImageDestinationCreateWithURL(url as CFURL, "public.png" as CFString, 1, nil) else {
            throw AutoClipWriterError.encodeFailed(url.lastPathComponent)
        }
        CGImageDestinationAddImage(destination, image, nil)
        guard CGImageDestinationFinalize(destination) else { throw AutoClipWriterError.encodeFailed(url.lastPathComponent) }
    }

    private func removeLeftovers() {
        for url in (try? FileManager.default.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? []
            where url.lastPathComponent.hasPrefix(".partial_") {
            try? FileManager.default.removeItem(at: url)
        }
    }

    /// Keeps the newest clips within the count and byte caps (names sort by time).
    func prune(keeping newest: URL? = nil) {
        let fileManager = FileManager.default
        let entries = ((try? fileManager.contentsOfDirectory(at: directory, includingPropertiesForKeys: nil)) ?? [])
            .filter { $0.lastPathComponent.hasPrefix("phone_saber_triage_autoclip_") }
            .sorted { $0.lastPathComponent > $1.lastPathComponent }
        var total: Int64 = 0
        for (index, entry) in entries.enumerated() {
            let size = Self.allocatedSize(entry)
            total += size
            let keep = entry == newest
                || (index < AutoClipLimits.maximumStoredClips && total <= AutoClipLimits.maximumStoredBytes)
            guard !keep else { continue }
            try? fileManager.removeItem(at: entry)
            let id = String(entry.lastPathComponent.dropFirst("phone_saber_triage_".count))
            try? fileManager.removeItem(at: directory.appendingPathComponent("\(id)_triage_transfer.psbt"))
        }
    }

    static func allocatedSize(_ url: URL) -> Int64 {
        guard let enumerator = FileManager.default.enumerator(at: url, includingPropertiesForKeys: [.fileSizeKey]) else { return 0 }
        var total: Int64 = 0
        for case let item as URL in enumerator {
            total += Int64((try? item.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0)
        }
        return total
    }
}

/// Kept separate from DebugRecordingStorage so host builds need only this file.
enum DebugRecordingStorageProbe {
    static func availableCapacity(at directory: URL) throws -> Int64 {
        let values = try directory.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey,
                                                            .volumeAvailableCapacityKey])
        if let capacity = values.volumeAvailableCapacityForImportantUsage { return capacity }
        if let capacity = values.volumeAvailableCapacity { return Int64(capacity) }
        throw CocoaError(.fileReadUnknown)
    }
}

// MARK: - Bundle content (summary.json, frame contexts, prompt)

struct AutoClipBundleContent {
    struct Image {
        let role: String      // "event" (full resolution) or "context" (half resolution)
        let slot: Int
        let frameID: UInt64
        let timestamp: Double
    }

    static let candidateColumns = ["rank", "x1", "y1", "x2", "y2", "minX", "minY", "maxX", "maxY",
                                   "score", "flags", "source", "peakValue", "emitterScore", "highValueRatio",
                                   "meanValue", "nearSaturationPresent", "colorPurity", "componentArea", "aspect"]

    let capture: AutoClipCapture
    let slots: [Int]
    let images: [Image]
    private let slotByFrameID: [UInt64: Int]

    init(capture: AutoClipCapture, slots: [Int]) {
        self.capture = capture
        self.slots = slots
        let buffers = capture.buffers
        var byID: [UInt64: Int] = [:]
        for slot in slots { byID[buffers.frames[slot].frameID] = slot }
        slotByFrameID = byID
        var chosen: [Image] = (0..<buffers.eventCount).map {
            Image(role: "event", slot: $0, frameID: buffers.eventFrameIDs[$0], timestamp: buffers.eventTimes[$0])
        }
        let eventIDs = Set(chosen.map(\.frameID))
        let context = buffers.contextSlots(from: capture.windowStart, to: capture.windowEnd)
            .filter { !eventIDs.contains(buffers.contextFrameIDs[$0]) }
            .map { Image(role: "context", slot: $0, frameID: buffers.contextFrameIDs[$0], timestamp: buffers.contextTimes[$0]) }
        chosen += context.suffix(AutoClipLimits.maximumImages - chosen.count)
        images = chosen.sorted { $0.timestamp == $1.timestamp ? $0.role > $1.role : $0.timestamp < $1.timestamp }
    }

    private var triggerColor: String { AutoClipRecorder.colorName(capture.trigger.color) }

    func imageEntry(_ image: Image, path: String, contextPath: String, sessionID: String) -> [String: Any] {
        let offset = image.timestamp - capture.trigger.timestamp
        return ["path": path, "frameContextPath": contextPath, "frameID": Int(image.frameID),
                "color": triggerColor, "failureType": "autoclip_\(capture.trigger.kind.rawValue)",
                "reason": reasonText + String(format: " (%+.2f s)", offset),
                "role": image.role, "scale": image.role == "event" ? 1.0 : 0.5,
                "timestamp": image.timestamp, "offsetSeconds": round3(offset),
                "sessionID": sessionID, "auxiliary": image.role != "event"]
    }

    var reasonText: String {
        let trigger = capture.trigger
        switch trigger.kind {
        case .confusion:
            return "auto clip: \(triggerColor) had \(trigger.competitorCount + 1) separated eligible candidates for \(AutoClipTriggerPolicy.confusionFrames) frames in a row"
        case .jump:
            return String(format: "auto clip: %@ winner midpoint jumped %.0f px (threshold %.0f px) between consecutive frames",
                          triggerColor, trigger.jumpDistance ?? 0, trigger.jumpThreshold ?? 0)
        case .gap:
            return "auto clip: \(triggerColor) selected for at least \(AutoClipTriggerPolicy.gapSelectedSeconds) s then absent for \(AutoClipTriggerPolicy.gapFrames) consecutive processed frames while the other colour was selected or this blade was moving"
        case .manual:
            return "auto clip: requested (\(trigger.note ?? "manual"))"
        }
    }

    private func colorEntry(_ record: AutoClipColorRecord, slot: Int, colorIndex: Int,
                            sources: inout [String], sourceIndex: inout [String: Int],
                            includeIneligible: Bool) -> [String: Any] {
        let buffers = capture.buffers
        var rows: [[Any]] = []
        let base = (slot * 2 + colorIndex) * AutoClipLimits.candidatesPerColor
        for index in 0..<Int(record.stored) {
            let c = buffers.candidates[base + index]
            if !includeIneligible && c.flags & AutoClipCandidateRecord.eligible == 0 { continue }
            let source: Int
            if let known = sourceIndex[c.source] { source = known } else {
                source = sources.count; sourceIndex[c.source] = source; sources.append(c.source)
            }
            rows.append([Int(c.rank), Int(c.x1), Int(c.y1), Int(c.x2), Int(c.y2), Int(c.minX), Int(c.minY),
                         Int(c.maxX), Int(c.maxY), round1(Double(c.score)), Int(c.flags), source,
                         Int(c.peakValue), c.emitterScore.map { round4(Double($0)) } ?? NSNull(),
                         round4(Double(c.highValueRatio)), round1(Double(c.meanValue)),
                         c.nearSaturationPresent ?? NSNull(), round4(Double(c.colorPurity)),
                         Int(c.componentArea), c.aspect.map { round4(Double($0)) } ?? NSNull()])
        }
        var entry: [String: Any] = ["n": Int(record.candidateCount), "e": Int(record.eligibleCount),
                                    "k": Int(record.competitorCount), "sup": record.trackLockSuppressed, "c": rows]
        entry["sel"] = record.hasSelection ? [Int(record.x1), Int(record.y1), Int(record.x2), Int(record.y2)] : NSNull()
        return entry
    }

    /// Thinning keeps summary.json under the receiver limit: level 1 drops ineligible
    /// candidates away from the trigger, level 2 also keeps every second frame there.
    func frameEntries(level: Int, sources: inout [String]) -> [[String: Any]] {
        let buffers = capture.buffers
        var sourceIndex: [String: Int] = [:]
        for (index, name) in sources.enumerated() { sourceIndex[name] = index }
        var entries: [[String: Any]] = []
        for (position, slot) in slots.enumerated() {
            let frame = buffers.frames[slot]
            let near = abs(frame.timestamp - capture.trigger.timestamp) <= 0.5
            if level >= 2 && !near && position % 2 == 1 { continue }
            let includeIneligible = level == 0 || near
            entries.append([
                "f": Int(frame.frameID), "t": frame.timestamp, "ms": round1(Double(frame.processingMs)),
                "red": colorEntry(frame.red, slot: slot, colorIndex: 0, sources: &sources,
                                  sourceIndex: &sourceIndex, includeIneligible: includeIneligible),
                "blue": colorEntry(frame.blue, slot: slot, colorIndex: 1, sources: &sources,
                                   sourceIndex: &sourceIndex, includeIneligible: includeIneligible)
            ])
        }
        return entries
    }

    func summaryData(images: [[String: Any]]) throws -> Data {
        var lastData = Data()
        for level in 0...2 {
            var sources: [String] = []
            let frames = frameEntries(level: level, sources: &sources)
            let summary = summaryObject(images: images, frames: frames, sources: sources, thinning: level)
            lastData = try JSONSerialization.data(withJSONObject: summary, options: [.sortedKeys])
            if lastData.count <= AutoClipLimits.maximumSummaryBytes { return lastData }
        }
        // 高fps・両色12候補でもトリガーの証拠は落とさない。画像contextは全て別JSONに残る。
        var sources: [String] = []
        let frames = frameEntries(level: 0, sources: &sources)
        let nearest = frames.min {
            abs(($0["t"] as? Double ?? 0) - capture.trigger.timestamp)
                < abs(($1["t"] as? Double ?? 0) - capture.trigger.timestamp)
        }?["f"] as? Int
        for radius in [0.5, 0.25, 0.1, 0.0] {
            let retained = frames.filter {
                abs(($0["t"] as? Double ?? 0) - capture.trigger.timestamp) <= radius
                    || ($0["f"] as? Int) == nearest
            }
            let summary = summaryObject(images: images, frames: retained, sources: sources, thinning: 3)
            lastData = try JSONSerialization.data(withJSONObject: summary, options: [.sortedKeys])
            if lastData.count <= AutoClipLimits.maximumSummaryBytes { return lastData }
        }
        return lastData
    }

    private func summaryObject(images: [[String: Any]], frames: [[String: Any]], sources: [String],
                               thinning: Int) -> [String: Any] {
        let buffers = capture.buffers
        func count(_ keyPath: KeyPath<AutoClipFrameRecord, AutoClipColorRecord>) -> [String: Any] {
            let records = slots.map { buffers.frames[$0][keyPath: keyPath] }
            return ["frames": records.count,
                    "detectedFrames": records.filter(\.hasSelection).count,
                    "framesWithCompetitor": records.filter { $0.competitorCount > 0 }.count]
        }
        func dropouts(_ keyPath: KeyPath<AutoClipFrameRecord, AutoClipColorRecord>) -> [String: Any] {
            var count = 0
            var previous = false
            for slot in slots {
                let has = buffers.frames[slot][keyPath: keyPath].hasSelection
                if previous && !has { count += 1 }
                previous = has
            }
            return ["dropoutStarts": count]
        }
        let first = slots.first.map { buffers.frames[$0].timestamp } ?? capture.windowStart
        let last = slots.last.map { buffers.frames[$0].timestamp } ?? capture.windowEnd
        let measuredFPS = slots.count > 1 && last > first ? Double(slots.count - 1) / (last - first) : 0
        let trigger = capture.trigger
        var triggerEntry: [String: Any] = [
            "kind": trigger.kind.rawValue, "color": triggerColor, "frameID": Int(trigger.frameID),
            "timestamp": trigger.timestamp, "reason": reasonText,
            "competitorCount": trigger.competitorCount
        ]
        triggerEntry["winner"] = trigger.winner ?? NSNull()
        triggerEntry["competitor"] = trigger.competitor ?? NSNull()
        triggerEntry["previousWinner"] = trigger.previousWinner ?? NSNull()
        triggerEntry["jumpDistancePx"] = trigger.jumpDistance.map(round1) ?? NSNull()
        triggerEntry["jumpThresholdPx"] = trigger.jumpThreshold.map(round1) ?? NSNull()
        triggerEntry["typicalLengthPx"] = trigger.typicalLength.map(round1) ?? NSNull()
        triggerEntry["note"] = trigger.note ?? NSNull()
        func zones(_ color: SaberColor) -> [[Double]] {
            (capture.ignoreZones[color] ?? []).map { [round4($0.minX), round4($0.minY), round4($0.maxX), round4($0.maxY)] }
        }
        let area: Any = capture.playArea.map { [round4($0.minX), round4($0.minY), round4($0.maxX), round4($0.maxY)] } ?? NSNull()
        let autoClip: [String: Any] = [
            // 転送summaryのv1 envelopeを保ち、AutoClip内部のschemaを更新する。
            "formatVersion": 2,
            "trigger": triggerEntry,
            "rules": [
                "confusion": "same colour: the actual production winner plus at least one other eligible candidate (not in an ignore zone, midpoint inside the acquisition play area; this is a competition heuristic, not a track-lock gate replay), separated from the winner (boxes apart by >\(AutoClipTriggerPolicy.boxMarginPixels) px or midpoints farther than their mean length), for \(AutoClipTriggerPolicy.confusionFrames) consecutive processed frames",
                "jump": "same colour: winner midpoint moves more than max(typical blade length (EMA \(AutoClipTriggerPolicy.typicalLengthAlpha)), \(AutoClipTriggerPolicy.minimumJumpDiagonalFraction) × frame diagonal) between consecutive processed frames (sequence gap ≤ \(AutoClipTriggerPolicy.maximumSequenceGap))",
                "manual": "PC command clip=1",
                "gap": "same colour: actual selected output continuously for ≥\(AutoClipTriggerPolicy.gapSelectedSeconds) s, then no selected output for \(AutoClipTriggerPolicy.gapFrames) consecutive processed frames; other colour selected throughout the gap or vanished blade endpoint motion ≥\(AutoClipTriggerPolicy.gapMotionDiagonalFraction) × frame diagonal within \(AutoClipTriggerPolicy.gapMotionSeconds) s before loss (endpoint order independent); increasing sequence/timestamps, camera frame skips allowed, time gap ≤1 s",
                "rateLimit": "cooldown \(Int(AutoClipLimits.cooldownSeconds)) s, ≤\(AutoClipLimits.maximumAutomaticClipsPerSession) automatic clips per sending session, confusion at a previously clipped competitor location skipped, manual ≥\(Int(AutoClipLimits.manualMinimumInterval)) s apart"
            ],
            "window": ["startTimestamp": capture.windowStart, "endTimestamp": capture.windowEnd,
                       "preSeconds": AutoClipLimits.preSeconds, "postSeconds": AutoClipLimits.postSeconds,
                       "recordedFrames": slots.count],
            "frameSize": ["width": buffers.width, "height": buffers.height],
            "contextImages": ["scale": 0.5, "width": buffers.halfWidth, "height": buffers.halfHeight,
                              "intervalSeconds": AutoClipLimits.contextInterval,
                              "sampling": "point sample of every second pixel and row (the pixels the step-2 detector mask scan reads); coordinates in metadata are full-resolution"],
            "camera": ["captureFPS": capture.captureFPS, "measuredFPS": round1(measuredFPS),
                       "signingExpiryRemainingHours": capture.environmentAtSave.signingExpiryRemainingHours ?? NSNull(),
                       "exposureBias": capture.environmentAtTrigger.exposureBias,
                       "shutter": capture.environmentAtTrigger.shutter,
                       "exposureDurationSeconds": capture.environmentAtTrigger.exposureDurationSeconds ?? NSNull(),
                       "iso": capture.environmentAtTrigger.iso ?? NSNull(),
                       "shutterISO": capture.environmentAtTrigger.shutterISO,
                       "shutterEmitter": capture.environmentAtTrigger.shutterEmitter,
                       "thermalAtTrigger": capture.environmentAtTrigger.thermal,
                       "thermalAtSave": capture.environmentAtSave.thermal,
                       "appVersion": capture.environmentAtTrigger.appVersion],
            "ignoreZones": ["red": zones(.red), "blue": zones(.blue), "coordinateSpace": "frame 0..1"],
            "playArea": area,
            "sessionClipIndex": capture.sessionClipIndex,
            "rejectedTriggersThisSession": capture.rejectedTriggers,
            "candidateColumns": Self.candidateColumns,
            "candidateEvidence": "Existing DetectionCore inputs only; emitterScore, nearSaturationPresent and aspect are retained only with an analysis observer. No new pixel work. nearSaturationPresent means production clippedWhiteCount > 0; componentArea is full-resolution pixel area; aspect is the original mask PCA aspect. Missing evidence is null.",
            "candidateFlags": ["eligible": Int(AutoClipCandidateRecord.eligible),
                               "selectable": Int(AutoClipCandidateRecord.selectable),
                               "selected": Int(AutoClipCandidateRecord.selected),
                               "competitor": Int(AutoClipCandidateRecord.competitor)],
            "candidateLimit": ["perColor": AutoClipLimits.candidatesPerColor,
                               "ineligiblePerColor": AutoClipLimits.ineligibleCandidatesPerColor],
            "frameKeys": ["f": "frameID", "t": "timestamp (s)", "ms": "detection ms",
                          "n": "candidates", "e": "eligible", "k": "separated competitors",
                          "sup": "track lock suppressed output", "sel": "actual analyzeSabers selected endpoints", "c": "stored candidates (candidateColumns)"],
            "thinning": thinning,
            "sources": sources,
            "frames": frames
        ]
        return [
            "formatVersion": 1,
            "sessionID": capture.clipID,
            "summaryScope": "autoClip",
            "activeColors": ["red", "blue"],
            "selectedImageCount": images.count,
            "images": images,
            "incidentCount": 1,
            "recordedFrameCount": slots.count,
            "redBlueDetectionSummary": ["red": count(\.red), "blue": count(\.blue)],
            "dropoutSummary": ["red": dropouts(\.red), "blue": dropouts(\.blue)],
            "autoClip": autoClip
        ]
    }

    func frameContext(frameID: UInt64, sessionID: String) -> [String: Any] {
        var frameEntry: [String: Any] = ["frameID": Int(frameID), "timestamp": 0.0,
                                         "red": ["detected": false], "blue": ["detected": false]]
        if let slot = slotByFrameID[frameID] {
            let buffers = capture.buffers
            let frame = buffers.frames[slot]
            frameEntry["timestamp"] = frame.timestamp
            frameEntry["processingTimeSeconds"] = round4(Double(frame.processingMs) / 1000)
            frameEntry["red"] = contextColor(frame.red, slot: slot, colorIndex: 0)
            frameEntry["blue"] = contextColor(frame.blue, slot: slot, colorIndex: 1)
        }
        return ["sessionID": sessionID, "selectedFrameID": Int(frameID), "contextRadiusFrames": 0,
                "source": "autoClip", "frames": [frameEntry]]
    }

    private func contextColor(_ record: AutoClipColorRecord, slot: Int, colorIndex: Int) -> [String: Any] {
        let buffers = capture.buffers
        let base = (slot * 2 + colorIndex) * AutoClipLimits.candidatesPerColor
        var candidates: [[String: Any]] = []
        var selectedType: Any = NSNull()
        for index in 0..<Int(record.stored) {
            let c = buffers.candidates[base + index]
            if c.flags & AutoClipCandidateRecord.selected != 0 { selectedType = c.source }
            candidates.append([
                "index": Int(c.rank), "sourceType": c.source, "finalScore": round1(Double(c.score)),
                "eligible": c.flags & AutoClipCandidateRecord.eligible != 0,
                "selectable": c.flags & AutoClipCandidateRecord.selectable != 0,
                "selected": c.flags & AutoClipCandidateRecord.selected != 0,
                "competitor": c.flags & AutoClipCandidateRecord.competitor != 0,
                "endpoints": [Int(c.x1), Int(c.y1), Int(c.x2), Int(c.y2)],
                "bbox": [Int(c.minX), Int(c.minY), Int(c.maxX), Int(c.maxY)],
                "peakValue": Int(c.peakValue), "emitterScore": c.emitterScore.map { round4(Double($0)) } ?? NSNull(),
                "highValueRatio": round4(Double(c.highValueRatio)), "meanValue": round1(Double(c.meanValue)),
                "nearSaturationPresent": c.nearSaturationPresent ?? NSNull(),
                "colorPurity": round4(Double(c.colorPurity)), "componentArea": Int(c.componentArea),
                "aspect": c.aspect.map { round4(Double($0)) } ?? NSNull()
            ])
        }
        var entry: [String: Any] = [
            "detected": record.hasSelection, "detectionSucceeded": record.hasSelection,
            "predictionUsed": false, "trackLockSuppressed": record.trackLockSuppressed,
            "candidateCount": Int(record.candidateCount),
            "eligibleCandidateCount": Int(record.eligibleCount),
            "competitorCount": Int(record.competitorCount),
            "selectedCandidateType": selectedType,
            "autoClipCandidates": candidates
        ]
        if record.hasSelection {
            entry["endpoint"] = [Int(record.x1), Int(record.y1), Int(record.x2), Int(record.y2)]
        }
        return entry
    }

    func prompt() -> String {
        """
        # PhoneSaber auto clip \(capture.clipID)

        Saved automatically while sending (no operator action). \(reasonText).

        - Images: `event` = full-resolution lossless frames at and right after the trigger;
          `context` = half-resolution (point-sampled) frames every \(AutoClipLimits.contextInterval) s from
          \(Int(AutoClipLimits.preSeconds)) s before to \(Int(AutoClipLimits.postSeconds)) s after.
        - `summary.json` → `autoClip.frames` has every processed frame of the window with the
          production winner and up to \(AutoClipLimits.candidatesPerColor) candidates per colour
          (columns in `autoClip.candidateColumns`, coordinates in full-resolution pixels).
        - Heuristic trigger only: it does not prove a recognition fault. Check the
          original images for where the real saber is before drawing conclusions.
        - Summarise on the Mac: `python3 ios/PhoneSaberSender/Tools/phone_saber_clip_report.py <this folder>`
        """
    }

    private func round1(_ value: Double) -> Double { (value * 10).rounded() / 10 }
    private func round3(_ value: Double) -> Double { (value * 1000).rounded() / 1000 }
    private func round4(_ value: Double) -> Double { (value * 10000).rounded() / 10000 }
}
