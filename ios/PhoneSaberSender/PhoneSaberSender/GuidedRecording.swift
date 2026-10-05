import Foundation
#if canImport(UIKit)
import AVFoundation
import AudioToolbox
import UIKit
#endif

// Guided Debug Recording (opt-in, ガイド付き録画).
//
// The operator stands in front of the camera holding the sabers, so they cannot
// touch the iPhone to switch segment labels or press "Capture Lossless Frame".
// A guided recording follows a fixed, versioned step script: it switches the
// segment label at each step boundary (through the same path as the manual
// picker), speaks a short Japanese cue, counts down, keeps a few lossless frames
// during swing steps and stops by itself at the end.
//
// Diagnostic only: nothing here reaches recognition, tracking or the UDP payload.
// Pure (Foundation-only) types live here so the host-built tracking e2e harness
// that compiles DebugVideoRecorder.swift also compiles them; the speech/haptic
// cue player is iOS-only (UIKit) at the bottom of the file.

/// Which part of a guided step the recorded frame belongs to.
enum DebugGuidedPhase: Equatable, Sendable {
    /// Transition: the cue is spoken and counted down. Frames are `unlabeled`.
    case leadIn(Int)
    /// The step itself: frames carry the step's segment label.
    case hold(Int)

    var stepIndex: Int {
        switch self {
        case .leadIn(let index), .hold(let index): return index
        }
    }
}

/// How a guided recording ended (metadata `guidedRecording.outcome`).
enum DebugGuidedOutcome: String, Equatable, Sendable {
    /// Every step ran and the script stopped the recording.
    case completed
    /// The operator pressed Cancel; frames up to that point are kept.
    case cancelled
    /// The recording ended before the script did (limit, background, camera
    /// interruption ...). The default when nothing else was reported.
    case incomplete
}

struct GuidedRecordingStep: Equatable, Sendable {
    /// Stable metadata key (snake_case, unique in the script).
    let id: String
    /// Large on-screen text.
    let title: String
    /// Spoken at the start of the lead-in.
    let cue: String
    /// Unlabeled transition before the step: cue, then a 3-2-1 countdown.
    let leadInSeconds: Double
    /// Labelled duration of the step.
    let holdSeconds: Double
    let label: DebugSegmentLabel
    /// Lossless frames requested during the hold (swing steps only).
    let losslessCaptures: Int
}

struct GuidedRecordingScript: Equatable, Sendable {
    let id: String
    let version: Int
    let steps: [GuidedRecordingStep]
    /// Spoken when the last step ends, just before the recording stops.
    let closingCue: String

    var totalSeconds: Double { steps.reduce(0) { $0 + $1.leadInSeconds + $1.holdSeconds } }
    var plannedLosslessCaptures: Int { steps.reduce(0) { $0 + $1.losslessCaptures } }

    /// Shooting plan v2 (2026-10-05). No assumption about objects in the scene:
    /// v1 asked for red labels / a carabiner / an outlet label and for covering
    /// them, which the operator's room does not have. saberなし 20 s → red only:
    /// still at 0.5 / 1.5 / 3 m, end-on, slow swings, fast swings (2 thrusts),
    /// a swing across the whole frame → blue only: still, swings → both lit,
    /// swings → saberなし 10 s. Still holds are 4 s (3 s plus ~1 s reaction
    /// time). Lead-ins include the spoken cue, walking time and the 3-2-1
    /// countdown. Changing any value requires a new `version` (metadata records it).
    /// v3 (2026-10-06): one lossless frame in the first saberなし hold (what the red false
    /// positive is was not visible in the v2 bundles), so the fast swing keeps 2 of its 3.
    static let shootingPlanV3 = GuidedRecordingScript(
        id: "shooting_plan_2026_10_06",
        version: 3,
        steps: [
            GuidedRecordingStep(id: "no_saber", title: "saberなし（2本とも消灯）",
                cue: "ガイド付き録画を始めます。セイバーを2本とも消して、カメラの前で待ってください。",
                leadInSeconds: 10, holdSeconds: 20, label: .noSaber, losslessCaptures: 1),
            GuidedRecordingStep(id: "red_still_0_5m", title: "赤だけ 0.5m 静止",
                cue: "赤だけ点灯して、カメラから50センチで止めてください。",
                leadInSeconds: 9, holdSeconds: 4, label: .sabersVisible, losslessCaptures: 0),
            GuidedRecordingStep(id: "red_still_1_5m", title: "赤 1.5m 静止",
                cue: "1.5メートルまで下がって、止めてください。",
                leadInSeconds: 9, holdSeconds: 4, label: .sabersVisible, losslessCaptures: 0),
            GuidedRecordingStep(id: "red_still_3m", title: "赤 3m 静止",
                cue: "3メートルまで下がって、止めてください。",
                leadInSeconds: 9, holdSeconds: 4, label: .sabersVisible, losslessCaptures: 0),
            GuidedRecordingStep(id: "red_end_on", title: "赤 先端をカメラへ向けて静止",
                cue: "1.5メートルに戻って、先端をカメラに向けて止めてください。",
                leadInSeconds: 9, holdSeconds: 4, label: .sabersVisible, losslessCaptures: 0),
            GuidedRecordingStep(id: "red_slow_swing", title: "赤 ゆっくり大きく振る",
                cue: "赤を、ゆっくり大きく振ってください。",
                leadInSeconds: 7, holdSeconds: 8, label: .sabersVisible, losslessCaptures: 1),
            GuidedRecordingStep(id: "red_fast_swing", title: "赤 速く5回（2回は突く）",
                cue: "赤を速く5回振ってください。そのうち2回は、カメラに向けて突いてください。",
                leadInSeconds: 8, holdSeconds: 8, label: .sabersVisible, losslessCaptures: 2),
            GuidedRecordingStep(id: "red_cross_swing", title: "赤 画面の端から端まで横切って振る",
                cue: "赤を、画面の端から端まで横切るように振ってください。",
                leadInSeconds: 7, holdSeconds: 8, label: .sabersVisible, losslessCaptures: 1),
            GuidedRecordingStep(id: "blue_still_1_5m", title: "青だけ 1.5m 静止",
                cue: "赤を消して、青だけ点灯して、1.5メートルで止めてください。",
                leadInSeconds: 9, holdSeconds: 4, label: .sabersVisible, losslessCaptures: 0),
            GuidedRecordingStep(id: "blue_swing", title: "青 ゆっくり→速く振る",
                cue: "青を、ゆっくり振ってから、速く振ってください。",
                leadInSeconds: 7, holdSeconds: 8, label: .sabersVisible, losslessCaptures: 1),
            GuidedRecordingStep(id: "both_swing", title: "赤と青 両方点灯して振る",
                cue: "赤も点灯して、両方を自由に振ってください。",
                leadInSeconds: 8, holdSeconds: 10, label: .sabersVisible, losslessCaptures: 0),
            GuidedRecordingStep(id: "no_saber_end", title: "saberなし（2本とも消灯）",
                cue: "両方消して、カメラの前で待ってください。",
                leadInSeconds: 8, holdSeconds: 10, label: .noSaber, losslessCaptures: 0),
        ],
        closingCue: "録画を終わります。おつかれさまでした。"
    )

    /// Problems that would make the script unsafe to run (empty = valid).
    var validationErrors: [String] {
        var errors: [String] = []
        if steps.isEmpty { errors.append("no steps") }
        if Set(steps.map(\.id)).count != steps.count { errors.append("duplicate step id") }
        for step in steps {
            if step.leadInSeconds < GuidedRecordingScheduler.countdownSeconds + 1 {
                errors.append("\(step.id): lead-in shorter than the countdown")
            }
            if step.holdSeconds <= 0 { errors.append("\(step.id): empty hold") }
            if step.losslessCaptures < 0 { errors.append("\(step.id): negative captures") }
        }
        if plannedLosslessCaptures > DebugRecordingLimits.maximumGuidedLosslessCaptures {
            errors.append("more lossless captures than the guided limit")
        }
        // Leave a margin under the 5-minute recording limit for Start/Stop latency.
        if totalSeconds > DebugRecordingLimits.maximumDurationSeconds - 30 {
            errors.append("script too long for one recording")
        }
        return errors
    }
}

extension DebugRecordingLimits {
    /// Lossless frames a guided recording may request (manual recordings keep
    /// `maximumManualLosslessCaptures`). At 1920x1080 six frames plus the two
    /// automatic anomaly frames fit the unchanged 64 MiB lossless disk reservation;
    /// the buffered/retained BGRA memory caps still apply to every capture.
    static let maximumGuidedLosslessCaptures = 6
}

extension DebugRecordingTriageLimits {
    /// Triage-bundle slots a guided recording keeps for its lossless frames (swings and the
    /// first saberなし hold, one per step first)
    /// ahead of bridge and tracking units (out of `defaultImageCount`).
    static let guidedManualImageReserve = 5
}

enum GuidedRecordingAction: Equatable {
    case setPhase(DebugGuidedPhase?)
    case setLabel(DebugSegmentLabel)
    case speak(String)
    /// 3, 2, 1 before a hold; 0 when the hold starts.
    case countdown(Int)
    case captureLossless(stepIndex: Int)
    /// Last step done: stop the recording.
    case finish
}

/// What the large on-screen guide shows.
struct GuidedRecordingStatus: Equatable {
    let stepIndex: Int
    let stepCount: Int
    let phase: DebugGuidedPhase
    let title: String
    let label: DebugSegmentLabel
    /// Whole seconds left in the current phase (lead-in or hold), rounded up.
    let secondsRemaining: Int
    let totalSecondsRemaining: Int
}

/// Time-driven step scheduler. Pure: the caller passes the elapsed time since
/// the recording started (manual clock in tests), applies the returned actions
/// in order and polls again. Each action is returned at most once.
struct GuidedRecordingScheduler {
    static let countdownSeconds = 3.0
    /// Spacing of lossless requests inside a swing hold; the first comes one
    /// interval after the hold starts so the swing is under way.
    static let captureIntervalSeconds = 1.5
    /// No capture is requested this close to the end of a hold.
    static let captureEndMarginSeconds = 0.5
    /// After a stall (e.g. the app was busy), late cues are dropped instead of
    /// being spoken out of step; labels, phases and the finish are never dropped.
    static let staleCueSeconds = 1.0
    static let staleCaptureSeconds = 0.5

    private struct Timed {
        let time: Double
        let action: GuidedRecordingAction
    }

    let script: GuidedRecordingScript
    private let timeline: [Timed]
    private let stepStarts: [Double]
    private var nextIndex = 0
    private(set) var isFinished = false
    private(set) var isCancelled = false
    private(set) var requestedCaptureCount = 0

    init(script: GuidedRecordingScript) {
        self.script = script
        var timeline: [Timed] = []
        var starts: [Double] = []
        var start = 0.0
        var captureBudget = DebugRecordingLimits.maximumGuidedLosslessCaptures
        for (index, step) in script.steps.enumerated() {
            starts.append(start)
            let holdStart = start + step.leadInSeconds
            let holdEnd = holdStart + step.holdSeconds
            timeline.append(Timed(time: start, action: .setPhase(.leadIn(index))))
            timeline.append(Timed(time: start, action: .setLabel(.unlabeled)))
            timeline.append(Timed(time: start, action: .speak(step.cue)))
            for count in stride(from: Int(Self.countdownSeconds), through: 1, by: -1) {
                let time = holdStart - Double(count)
                if time > start { timeline.append(Timed(time: time, action: .countdown(count))) }
            }
            timeline.append(Timed(time: holdStart, action: .setPhase(.hold(index))))
            timeline.append(Timed(time: holdStart, action: .setLabel(step.label)))
            timeline.append(Timed(time: holdStart, action: .countdown(0)))
            var captureTime = holdStart + Self.captureIntervalSeconds
            var planned = 0
            while planned < step.losslessCaptures, captureBudget > 0,
                  captureTime <= holdEnd - Self.captureEndMarginSeconds {
                timeline.append(Timed(time: captureTime, action: .captureLossless(stepIndex: index)))
                planned += 1
                captureBudget -= 1
                captureTime += Self.captureIntervalSeconds
            }
            start = holdEnd
        }
        timeline.append(Timed(time: start, action: .setPhase(nil)))
        timeline.append(Timed(time: start, action: .speak(script.closingCue)))
        timeline.append(Timed(time: start, action: .finish))
        // Stable order: by time, then by insertion (phase before label before cue).
        self.timeline = timeline.enumerated().sorted {
            $0.element.time != $1.element.time ? $0.element.time < $1.element.time : $0.offset < $1.offset
        }.map(\.element)
        self.stepStarts = starts
    }

    var totalSeconds: Double { script.totalSeconds }

    /// Absolute boundaries (seconds from Start) of every step: lead-in start,
    /// hold start and hold end.
    var boundaries: [(leadIn: Double, hold: Double, end: Double)] {
        script.steps.enumerated().map { index, step in
            let start = stepStarts[index]
            return (start, start + step.leadInSeconds, start + step.leadInSeconds + step.holdSeconds)
        }
    }

    /// Lossless requests the timeline will issue (bounded by the guided limit).
    var plannedCaptureTimes: [Double] {
        timeline.compactMap { if case .captureLossless = $0.action { return $0.time }; return nil }
    }

    /// Returns the actions due at `elapsed` seconds since Start, in order.
    mutating func advance(to elapsed: Double) -> [GuidedRecordingAction] {
        guard !isFinished, !isCancelled else { return [] }
        var due: [GuidedRecordingAction] = []
        while nextIndex < timeline.count, timeline[nextIndex].time <= elapsed {
            let item = timeline[nextIndex]
            nextIndex += 1
            let lateness = elapsed - item.time
            switch item.action {
            case .speak, .countdown:
                if lateness > Self.staleCueSeconds { continue }
            case .captureLossless:
                if lateness > Self.staleCaptureSeconds { continue }
                requestedCaptureCount += 1
            case .finish:
                isFinished = true
            case .setPhase, .setLabel:
                break
            }
            due.append(item.action)
        }
        return due
    }

    /// Stops the schedule; later `advance` calls return nothing.
    mutating func cancel() {
        guard !isFinished else { return }
        isCancelled = true
    }

    func status(at elapsed: Double) -> GuidedRecordingStatus? {
        guard !script.steps.isEmpty, !isFinished, !isCancelled else { return nil }
        let clamped = max(0, elapsed)
        guard clamped < totalSeconds else { return nil }
        var index = script.steps.count - 1
        for (candidate, start) in stepStarts.enumerated().reversed() where start <= clamped {
            index = candidate
            break
        }
        let step = script.steps[index]
        let holdStart = stepStarts[index] + step.leadInSeconds
        let inHold = clamped >= holdStart
        let phaseEnd = inHold ? holdStart + step.holdSeconds : holdStart
        return GuidedRecordingStatus(
            stepIndex: index, stepCount: script.steps.count,
            phase: inHold ? .hold(index) : .leadIn(index),
            title: step.title, label: step.label,
            secondsRemaining: max(0, Int((phaseEnd - clamped).rounded(.up))),
            totalSecondsRemaining: max(0, Int((totalSeconds - clamped).rounded(.up))))
    }
}

// MARK: - Metadata

/// Root metadata and summary.json `guidedRecording` (format version 1). Optional:
/// recordings without a guided script omit it. Codable so the shape is pinned.
struct DebugGuidedRecordingSummary: Codable, Equatable {
    static let currentFormatVersion = 1
    static let definition = "Opt-in guided Debug Recording. Each step has an unlabeled lead-in (spoken cue, countdown) and a hold carrying the step label. Per-step counts cover hold frames only; detectedFrames = fresh output incl. prediction, measuredFrames = without prediction. Under noSaber/noSaberCovered every detected frame is a false positive. Frame IDs are the first lead-in / first and last hold frames actually recorded; absent when the step was not reached."

    struct ColorCounts: Codable, Equatable {
        var detectedFrames: Int
        var measuredFrames: Int
    }

    struct Step: Codable, Equatable {
        let index: Int
        let id: String
        let title: String
        let label: String
        let plannedLeadInSeconds: Double
        let plannedHoldSeconds: Double
        let plannedLosslessCaptures: Int
        var leadInStartFrameID: UInt64?
        var leadInStartTimestamp: Double?
        var holdStartFrameID: UInt64?
        var holdStartTimestamp: Double?
        var holdEndFrameID: UInt64?
        var holdEndTimestamp: Double?
        var frames: Int
        var red: ColorCounts
        var blue: ColorCounts
    }

    struct Capture: Codable, Equatable {
        let stepIndex: Int
        let frameID: UInt64
    }

    let formatVersion: Int
    let scriptID: String
    let scriptVersion: Int
    var outcome: String
    let plannedSeconds: Double
    var steps: [Step]
    var losslessCaptures: [Capture]
    let definition: String

    /// JSONSerialization-compatible dictionary for the streamed metadata root.
    var jsonObject: [String: Any] {
        guard let data = try? JSONEncoder().encode(self),
              let object = try? JSONSerialization.jsonObject(with: data) as? [String: Any] else { return [:] }
        return object
    }
}

/// Per-step counts and boundaries, updated once per recorded frame on the
/// processing queue (O(1) per frame, bounded by the fixed script).
struct DebugGuidedLedger {
    static let maximumCaptures = 32

    private(set) var summary: DebugGuidedRecordingSummary

    init(script: GuidedRecordingScript) {
        summary = DebugGuidedRecordingSummary(
            formatVersion: DebugGuidedRecordingSummary.currentFormatVersion,
            scriptID: script.id, scriptVersion: script.version,
            outcome: DebugGuidedOutcome.incomplete.rawValue,
            plannedSeconds: script.totalSeconds,
            steps: script.steps.enumerated().map { index, step in
                DebugGuidedRecordingSummary.Step(
                    index: index, id: step.id, title: step.title, label: step.label.rawValue,
                    plannedLeadInSeconds: step.leadInSeconds, plannedHoldSeconds: step.holdSeconds,
                    plannedLosslessCaptures: step.losslessCaptures,
                    frames: 0, red: .init(detectedFrames: 0, measuredFrames: 0),
                    blue: .init(detectedFrames: 0, measuredFrames: 0))
            },
            losslessCaptures: [], definition: DebugGuidedRecordingSummary.definition)
    }

    mutating func observe(frameID: UInt64, timestamp: Double, phase: DebugGuidedPhase?,
                          detected: [String: Bool], measured: [String: Bool], losslessCaptured: Bool) {
        guard let phase, summary.steps.indices.contains(phase.stepIndex) else { return }
        let index = phase.stepIndex
        if losslessCaptured, summary.losslessCaptures.count < Self.maximumCaptures {
            summary.losslessCaptures.append(.init(stepIndex: index, frameID: frameID))
        }
        switch phase {
        case .leadIn:
            if summary.steps[index].leadInStartFrameID == nil {
                summary.steps[index].leadInStartFrameID = frameID
                summary.steps[index].leadInStartTimestamp = timestamp
            }
        case .hold:
            var step = summary.steps[index]
            if step.holdStartFrameID == nil {
                step.holdStartFrameID = frameID
                step.holdStartTimestamp = timestamp
            }
            step.holdEndFrameID = frameID
            step.holdEndTimestamp = timestamp
            step.frames += 1
            if detected["red"] == true { step.red.detectedFrames += 1 }
            if measured["red"] == true { step.red.measuredFrames += 1 }
            if detected["blue"] == true { step.blue.detectedFrames += 1 }
            if measured["blue"] == true { step.blue.measuredFrames += 1 }
            summary.steps[index] = step
        }
    }

    mutating func setOutcome(_ outcome: DebugGuidedOutcome) {
        summary.outcome = outcome.rawValue
    }
}

/// Chooses which guided lossless frames keep the reserved triage slots: one per
/// step in script order first (so every swing step is represented), then the
/// rest round-robin. Frames without a step attribution come last. Pure.
enum DebugGuidedTriageReserve {
    static func pick(manualFrameIDs: [UInt64], guided: [String: Any]?, limit: Int) -> [UInt64] {
        guard let guided, limit > 0, !manualFrameIDs.isEmpty else { return [] }
        let available = Set(manualFrameIDs)
        var stepByFrame: [UInt64: Int] = [:]
        for capture in guided["losslessCaptures"] as? [[String: Any]] ?? [] {
            guard let frameID = (capture["frameID"] as? NSNumber)?.uint64Value,
                  let step = (capture["stepIndex"] as? NSNumber)?.intValue,
                  available.contains(frameID) else { continue }
            stepByFrame[frameID] = step
        }
        var queues: [Int: [UInt64]] = [:]
        for frameID in available.sorted() {
            queues[stepByFrame[frameID] ?? Int.max, default: []].append(frameID)
        }
        let order = queues.keys.sorted()
        var picked: [UInt64] = []
        var round = 0
        while picked.count < limit {
            var progressed = false
            for step in order where round < queues[step]!.count {
                picked.append(queues[step]![round])
                progressed = true
                if picked.count == limit { break }
            }
            if !progressed { break }
            round += 1
        }
        return picked.sorted()
    }
}

#if canImport(UIKit)
/// Audio and haptic cues for a guided recording. Abstract so tests can record
/// the calls instead of speaking.
@MainActor
protocol GuidedRecordingCuePlaying: AnyObject {
    func begin()
    func speak(_ text: String)
    func countdown(_ value: Int)
    /// Lets queued speech finish, then releases the audio session.
    func end()
    /// Stops at once and releases the audio session.
    func cancel()
}

/// AVSpeechSynthesizer (ja-JP) plus a system tick and a haptic.
///
/// The capture session is video-only (no audio input), so it does not own the
/// audio session. The app uses `.playback` + `.voicePrompt` with `.duckOthers`
/// (which also mixes) only while a guided recording runs, so the cue is audible
/// with the ring/silent switch on, and deactivates it afterwards with
/// `.notifyOthersOnDeactivation`. Failures only disable the audio cues; the
/// on-screen guide and the recording continue.
@MainActor
final class SpeechGuidedRecordingCuePlayer: NSObject, GuidedRecordingCuePlaying, AVSpeechSynthesizerDelegate {
    private let synthesizer = AVSpeechSynthesizer()
    private let haptic = UINotificationFeedbackGenerator()
    private let voice = AVSpeechSynthesisVoice(language: "ja-JP")
    private var sessionActive = false
    private var releaseWhenIdle = false
    private(set) var lastError: String?

    override init() {
        super.init()
        synthesizer.delegate = self
    }

    func begin() {
        releaseWhenIdle = false
        haptic.prepare()
        do {
            let session = AVAudioSession.sharedInstance()
            try session.setCategory(.playback, mode: .voicePrompt, options: [.duckOthers])
            try session.setActive(true)
            sessionActive = true
        } catch {
            lastError = error.localizedDescription
        }
    }

    func speak(_ text: String) {
        let utterance = AVSpeechUtterance(string: text)
        utterance.voice = voice
        utterance.rate = AVSpeechUtteranceDefaultSpeechRate
        synthesizer.speak(utterance)
    }

    func countdown(_ value: Int) {
        if value > 0 {
            AudioServicesPlaySystemSound(1103) // short tick
            haptic.notificationOccurred(.warning)
        } else {
            // The hold starts now: cut any late cue so "はい" is on time.
            if synthesizer.isSpeaking { synthesizer.stopSpeaking(at: .word) }
            AudioServicesPlaySystemSound(1057)
            haptic.notificationOccurred(.success)
            speak("はい")
        }
    }

    func end() {
        releaseWhenIdle = true
        if !synthesizer.isSpeaking { releaseSession() }
    }

    func cancel() {
        releaseWhenIdle = true
        synthesizer.stopSpeaking(at: .immediate)
        releaseSession()
    }

    private func releaseSession() {
        guard sessionActive else { return }
        sessionActive = false
        try? AVAudioSession.sharedInstance().setActive(false, options: .notifyOthersOnDeactivation)
    }

    nonisolated func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didFinish utterance: AVSpeechUtterance) {
        Task { @MainActor in self.releaseIfIdle() }
    }

    nonisolated func speechSynthesizer(_ synthesizer: AVSpeechSynthesizer, didCancel utterance: AVSpeechUtterance) {
        Task { @MainActor in self.releaseIfIdle() }
    }

    private func releaseIfIdle() {
        if releaseWhenIdle && !synthesizer.isSpeaking { releaseSession() }
    }
}
#endif
