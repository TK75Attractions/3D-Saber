"""Test-only adapter extracted from FrameProcessor.swift, without AVFoundation.

Extraction reads the production Track, compactMap state transition, endpoint
ordering/prediction helpers and expiry loop on each run. No independent Swift
copy of the algorithm is maintained and no production Swift files are edited.
"""
from pathlib import Path
import re
import random
import json


def declaration(source: str, marker: str) -> str:
    start = source.index(marker)
    opening = source.index("{", start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


def swift_source(repo: Path) -> str:
    text = (repo / "ios/PhoneSaberSender/PhoneSaberSender/FrameProcessor.swift").read_text()
    track = declaration(text, "private struct Track")
    transition = declaration(text, "var results = detected.compactMap")
    transition = transition.replace("var results =", "let results =", 1)
    helpers = "\n".join(declaration(text, "private func " + name) for name in ("stableEndpoints", "predictedEndpoints", "distance"))
    expiry_loop = declaration(text, "for color in [SaberColor.red, .blue]")
    hold = re.search(r"private let holdDuration: TimeInterval = ([0-9.]+)", text).group(1)
    return '''import Foundation
struct DetectedSaber {
    let endpoints: (PixelPoint, PixelPoint)
    let color: SaberColor
    var isFresh = true
    var isPredicted = false
}
final class ReferenceProcessor {
    private var tracks: [SaberColor: Track] = [.red: Track(), .blue: Track()]
    private var dimensions: (Int, Int)?
    private let holdDuration: TimeInterval = HOLD
    TRACK
    HELPERS
    func reset() { tracks = [.red: Track(), .blue: Track()]; dimensions = nil }
    func process(_ detected: [(SaberColor, (PixelPoint, PixelPoint)?)], width: Int, height: Int, processingStart: TimeInterval) -> [DetectedSaber] {
        if dimensions.map({ $0 != (width, height) }) ?? true {
            tracks = [.red: Track(), .blue: Track()]; dimensions = (width, height)
        }
        TRANSITION
        return results
    }
    func expire(_ now: Double) -> [DetectedSaber] {
        var remaining = false
        EXPIRY
        _ = remaining
        return [SaberColor.red, .blue].compactMap { color -> DetectedSaber? in
            guard let track = tracks[color], let endpoints = track.endpoints else { return nil }
            return DetectedSaber(endpoints: endpoints, color: color, isFresh: false)
        }
    }
    var expiry: Double? {
        tracks.values.compactMap { track in track.endpoints.map { _ in track.lastSeen + holdDuration } }.min()
    }
}
@main enum FrameReference {
    static func main() throws {
        let processor = ReferenceProcessor()
        while let line = readLine() {
            let tokens = line.split(separator: " ").map(String.init)
            var index = 1
            func integer() -> Int { defer { index += 1 }; return Int(tokens[index])! }
            func number() -> Double { defer { index += 1 }; return Double(tokens[index])! }
            var results: [DetectedSaber] = []
            var w = 1, h = 1, ow = 1920, oh = 1080
            var mx = false, my = false, measurement = false
            var epochs = [0.0, 0.0]
            if tokens[0] == "R" { processor.reset() }
            else if tokens[0] == "E" { results = processor.expire(number()) }
            else {
                let now = number(); w = integer(); h = integer(); ow = integer(); oh = integer()
                mx = integer() != 0; my = integer() != 0; measurement = integer() != 0
                epochs = [number(), number()]
                var detected: [(SaberColor, (PixelPoint, PixelPoint)?)] = []
                for color in [SaberColor.red, .blue] {
                    let present = integer() != 0
                    let a = PixelPoint(x: integer(), y: integer()), b = PixelPoint(x: integer(), y: integer())
                    detected.append((color, present ? (a,b) : nil))
                }
                results = processor.process(detected, width: w, height: h, processingStart: now)
            }
            let rows: [[String: Any]] = results.map { r in
                let coordinates = payload(for: r.endpoints, source: (w,h), output: (ow,oh), mirrorX: mx, mirrorY: my)
                let colorIndex = r.color == .red ? 0 : 1
                let text: Any = r.isFresh ? (measurement ? timestampedPayload(coordinates, timestamp: epochs[colorIndex]) : coordinates) : NSNull()
                return ["color": r.color == .red ? "red" : "blue",
                    "endpoints": [["x": r.endpoints.0.x, "y": r.endpoints.0.y], ["x": r.endpoints.1.x, "y": r.endpoints.1.y]],
                    "fresh": r.isFresh, "predicted": r.isPredicted,
                    "port": colorIndex == 0 ? 5005 : 5006, "text": text]
            }
            let object: [String: Any] = ["results": rows, "expiry": processor.expiry.map { $0 as Any } ?? NSNull()]
            let data = try JSONSerialization.data(withJSONObject: object, options: [.sortedKeys])
            print(String(data: data, encoding: .utf8)!)
        }
    }
}
'''.replace("HOLD", hold).replace("TRACK", track).replace("HELPERS", helpers).replace("TRANSITION", transition).replace("EXPIRY", expiry_loop)


def commands() -> bytes:
    rng = random.Random(20261006)
    rows = ["R"]
    # Boundary/order tests followed by deterministic stateful randomized frames.
    def frame(now, w, h, red, blue, mx=0, my=0, measurement=0):
        data = ["F", now, w, h, 1920, 1080, mx, my, measurement, 1791234567.1234567, 1791234567.9876543]
        for endpoints in (red, blue):
            data.extend([int(endpoints is not None), *(endpoints or (0, 0, 0, 0))])
        rows.append(" ".join(map(str, data)))
    frame(1, 100, 100, (10,20,30,40), (90,90,70,70))
    frame(1.01, 100, 100, (35,45,15,25), (60,60,80,80), 1, 1, 1)
    for i in range(1, 5):
        frame(1.01 + i * .01, 100, 100, None, None, i % 2, 1, 1)
    rows.extend(["E 1.189999999", "E 1.19", "R"])
    frame(2, 1, 1, (0,0,0,0), None, 1, 1, 1)
    frame(2.01, 2, 2, None, None)
    now = 10.0
    for i in range(500):
        now += rng.choice((.005, .016, .030, .18, .2))
        w, h = (1080, 1920) if i % 33 else (720, 1280)
        points = [tuple(rng.randrange(-50, max(w, h) + 50) for _ in range(4)) if rng.random() < .55 else None for _ in range(2)]
        frame(now, w, h, *points, i % 2, (i // 2) % 2, i % 3 == 0)
        if i % 25 == 0:
            rows.append("E " + str(now + .18))
        if i % 57 == 0:
            rows.append("R")
    return ("\n".join(rows) + "\n").replace("True", "1").replace("False", "0").encode()


def verify(destination, repo, core, run, differences):
    swift_path = destination / "FrameReference.swift"
    swift_path.write_text(swift_source(repo))
    reference, cli = destination / "frame-reference", destination / "frame-cli"
    import os
    env = {**os.environ, "CLANG_MODULE_CACHE_PATH": str(destination / "module-cache")}
    run(["xcrun", "swiftc", "-O", str(repo / "ios/PhoneSaberSender/PhoneSaberSender/DetectionCore.swift"), str(swift_path), "-o", str(reference)], env=env)
    run(["clang++", "-std=c++17", "-O2", "-Wall", "-Wextra", "-Werror", "-ffp-contract=off", "-I" + str(core / "include"),
         *(str(core / "src" / name) for name in ("detection.cpp", "pipeline.cpp", "frame_processor.cpp")), str(core / "tools/frame_cli.cpp"), "-o", str(cli)])
    inputs = commands()
    expected = [json.loads(line) for line in run([str(reference)], inputs).splitlines()]
    actual = [json.loads(line) for line in run([str(cli)], inputs).splitlines()]
    errors = differences(expected, actual, "frame_processor")
    return len(expected), errors
