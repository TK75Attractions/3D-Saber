import CoreFoundation
import Foundation

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
    static let maximumBundleBytes: Int64 = 512 * 1024 * 1024
}

struct DebugRecordingTriageImage: Equatable {
    let frameIndex: Int
    let frameID: UInt64
    let color: String
    let fileName: String
    let failureType: String
    let priority: Int
    let reasons: [String]
}

struct DebugRecordingTriageSelection: Equatable {
    let images: [DebugRecordingTriageImage]
    let incidentCount: Int
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

        addIdenticalEndpointReasons(frames: frames, reasonsByFrame: &reasonsByFrame)

        let incidentCount = incidentSummary(metadata: metadata).reduce(0) {
            $0 + Int(integer($1["incidentCount"]) ?? 0)
        }
        let requestedLimit = max(0, min(maximumImages, DebugRecordingTriageLimits.hardImageCount))
        let failureLimit = max(0, min(perFailureType, DebugRecordingTriageLimits.hardImageCount))

        let existingReferences = try references.filter { reference in
            guard safePNGName(reference.fileName) else {
                throw DebugRecordingTriageError.unsafeImageName(reference.fileName)
            }
            guard let directory else { return false }
            return FileManager.default.fileExists(
                atPath: directory.appendingPathComponent(reference.fileName).path
            )
        }
        var uniqueByName: [String: ImageReference] = [:]
        for reference in existingReferences {
            if uniqueByName[reference.fileName] == nil { uniqueByName[reference.fileName] = reference }
        }

        let sortedReferences = uniqueByName.values.sorted { lhs, rhs in
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
        for reference in sortedReferences {
            guard selected.count < requestedLimit else { break }
            let referenceReasons = reasonList(for: reference, reasonsByFrame: reasonsByFrame)
            let primary = referenceReasons.sorted {
                if $0.priority != $1.priority { return $0.priority < $1.priority }
                return $0.type < $1.type
            }.first ?? Reason(color: reference.color, type: "lossless_anomaly",
                              priority: 7, detail: "recorder forensic capture")
            let buckets = Set(referenceReasons.map(\.type)).union([primary.type])
            guard buckets.allSatisfy({ countByType[$0, default: 0] < failureLimit }) else { continue }
            let duplicatesNearby = selected.contains { item in
                buckets.contains(item.failureType)
                    && item.color == reference.color
                    && abs(item.frameIndex - reference.frameIndex) <= 3
                    && item.fileName != reference.fileName
            }
            guard !duplicatesNearby else { continue }

            selected.append(DebugRecordingTriageImage(
                frameIndex: reference.frameIndex,
                frameID: reference.frameID,
                color: reference.color,
                fileName: reference.fileName,
                failureType: primary.type,
                priority: primary.priority,
                reasons: Array(Set(referenceReasons.map(\.detail))).sorted()
            ))
            for bucket in buckets { countByType[bucket, default: 0] += 1 }
        }

        return (metadata, DebugRecordingTriageSelection(images: selected,
                                                         incidentCount: incidentCount))
    }

    static func build(
        metadataURL: URL,
        forensicDirectoryURL: URL?,
        maximumImages: Int = DebugRecordingTriageLimits.defaultImageCount
    ) throws -> URL {
        let data = try Data(contentsOf: metadataURL)
        let (metadata, selection) = try selectImages(
            metadataData: data, forensicDirectoryURL: forensicDirectoryURL,
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
                imageEntries.append([
                    "path": "images/\(targetName)", "frameContextPath": "frames/\(frameName)",
                    "frameID": item.frameID, "timestamp": number(frames(metadata)[item.frameIndex]["presentationTimeSeconds"]) ?? 0,
                    "color": item.color, "failureType": item.failureType,
                    "reason": item.reasons.joined(separator: "; "), "sourceFile": item.fileName
                ])
            }

            let incidentEntries = incidentSummary(metadata: metadata)
            let summary: [String: Any] = [
                "formatVersion": 1,
                "sessionID": sessionID,
                "recordedFrameCount": frames(metadata).count,
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
            try jsonData(summary).write(to: bundleURL.appendingPathComponent("summary.json"),
                                        options: .atomic)
            try promptText(sessionID: sessionID, imageCount: selection.images.count)
                .write(to: bundleURL.appendingPathComponent("prompt.md"), atomically: true,
                       encoding: .utf8)
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
            let highCoverage = number(breakdown["highBrightnessRatio"])
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
        return [
            "sessionID": string(metadata["sessionID"]) ?? "",
            "selectedFrameID": selected.frameID,
            "selectedColor": selected.color,
            "selectedFailureType": selected.failureType,
            "selectedReasons": selected.reasons,
            "contextRadiusFrames": DebugRecordingTriageLimits.contextRadius,
            "frames": context.map { minimalFrame(allFrames[$0], index: $0) }
        ]
    }

    private static func minimalFrame(_ frame: [String: Any], index: Int) -> [String: Any] {
        var output: [String: Any] = [
            "frameID": integer(frame["frameID"]) ?? UInt64(index),
            "timestamp": number(frame["presentationTimeSeconds"]) ?? 0
        ]
        for color in colors {
            let detection = frame[color] as? [String: Any] ?? [:]
            let diagnostic = colorDiagnostics(frame, color: color)
            let candidate = selectedCandidate(frame, color: color)
            let breakdown = candidate?["scoreBreakdown"] as? [String: Any] ?? [:]
            var colorData: [String: Any] = [
                "detected": detection["detected"] as? Bool ?? false,
                "predictionUsed": detection["predicted"] as? Bool ?? false,
                "detectionSucceeded": frame["\(color)DetectionSucceeded"] as? Bool
                    ?? isDetectionSuccess(frame, color: color),
                "maskPixelCount": integer(diagnostic["maskPixelCount"]) ?? 0,
                "morphologyPixelCount": integer(diagnostic["morphologyPixelCount"]) ?? 0,
                "connectedComponentCount": integer(diagnostic["connectedComponentCount"]) ?? 0,
                "candidateCount": integer(diagnostic["totalCandidateCount"]) ?? 0,
                "eligibleCandidateCount": integer(diagnostic["eligibleCandidateCount"]) ?? 0
            ]
            if let source = string(diagnostic["selectedCandidateType"]) ?? string(candidate?["sourceType"]) {
                colorData["selectedCandidateType"] = source
            }
            if let score = number(diagnostic["selectedCandidateFinalScore"]) ?? number(candidate?["finalScore"]) {
                colorData["score"] = score
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
            output[color] = colorData
        }
        return output
    }

    private static func detectionSummary(metadata: [String: Any]) -> [String: Any] {
        let allFrames = frames(metadata)
        return Dictionary(uniqueKeysWithValues: colors.map { color in
            let detected = allFrames.filter { isDetectionSuccess($0, color: color) }.count
            let candidates = allFrames.map { integer(colorDiagnostics($0, color: color)["totalCandidateCount"]) }
            let eligible = allFrames.map { integer(colorDiagnostics($0, color: color)["eligibleCandidateCount"]) }
            return (color, [
                "frames": allFrames.count,
                "detectedFrames": detected,
                "missedFrames": allFrames.count - detected,
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
            let missed = allFrames.filter { !isDetectionSuccess($0, color: color) }.count
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
        - Do not edit, create, or propose applying production code. Return analysis findings only.

        ## Required output
        Produce a concise diagnostic report with: session summary; findings grouped by RED/BLUE; false negatives, wrong-candidate/endpoint errors, and false positives in separate sections; A–G classifications; confidence and missing evidence; and no code patches. The Mac receiver writes analysis_report.md and analysis_report.json from your response.
        """
    }

    private static func endpointJumpAt(_ frames: [[String: Any]], index: Int,
                                       color: String) -> Double? {
        guard index > 0, let current = endpointTuple(frames[index], color: color),
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

    private static func isDetectionSuccess(_ frame: [String: Any], color: String) -> Bool {
        if let explicit = frame["\(color)DetectionSucceeded"] as? Bool { return explicit }
        guard let detection = frame[color] as? [String: Any], detection["detected"] as? Bool == true,
              detection["predicted"] as? Bool != true else { return false }
        return true
    }

    private static func isFirstFalseAfterDetection(_ frames: [[String: Any]], index: Int,
                                                   color: String) -> Bool {
        index > 0 && !isDetectionSuccess(frames[index], color: color)
            && isDetectionSuccess(frames[index - 1], color: color)
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
