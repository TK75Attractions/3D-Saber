#!/usr/bin/env python3
"""PC指示の実ハンドラをhostで検証。カメラ操作・ACK送信だけを記録するstub。"""
import argparse
from pathlib import Path
import subprocess
import tempfile

from run_latency_host_tests import ROOT, SOURCE, block


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--revision", help="旧版の実ハンドラで同じ回帰を実行")
    parser.add_argument("--retry-schedule", type=Path, help="実PC responderが偽時計で出した指示を配送する")
    parser.add_argument("--ack-trace", type=Path, help="偽配送したACKをPCホストへ返すTSV")
    args = parser.parse_args()
    if bool(args.retry_schedule) != bool(args.ack_trace):
        parser.error("--retry-schedule と --ack-trace は組で指定してください")
    camera = (subprocess.check_output(
        ["git", "show", f"{args.revision}:{(SOURCE / 'CameraViewModel.swift').relative_to(ROOT)}"],
        cwd=ROOT, text=True) if args.revision else (SOURCE / "CameraViewModel.swift").read_text())
    udp = (SOURCE / "UDPSender.swift").read_text()
    support = "import Foundation\nfinal class LANLivenessProbe {\n"
    # 指示の型とACKの整形は実ソース。ネットワークへ出す直前のhost・本文を観測する。
    support += block(udp, "struct PCCommand:") + "\n"
    start = udp.index("    static let commandPrefix =", udp.index("struct PCCommand:"))
    support += udp[start:udp.index("    static func formatAck(", start)]
    support += block(udp, "static func formatAck(") + "\n"
    support += '''
    var replies: [(host: String, line: String)] = []
    func sendAck(to host: String, line: String) { replies.append((host, line)) }
}
@MainActor final class CameraViewModel {
    let lanProbe = LANLivenessProbe()
    var station = "A", running = true, autoClipEnabled = true
    var now = 0.0, host = "192.168.1.2"
    var connectivityClock: () -> Double { { self.now } }
    var requestedReasons: [String] = []
    var effects: [String] = []
    var ignoreCompletion: ((Int, Int) -> Void)?
    var areaCompletion: ((Int, Bool, LANLivenessProbe.PCCommand.Area?) -> Void)?
    var clipAcknowledgements: [String: (String, String) -> Void] = [:]
    enum Color { case red, blue }
    func setShutterISO(_ value: Int) -> Bool { effects.append("iso"); return true }
    func shutterISOAckDetail() -> String { "2000" }
    func setShutter(_ value: Int) -> Bool { effects.append("shutter"); return true }
    func setShutterEmitter(_ value: Bool) { effects.append("emitter") }
    func setTrackLockEnabled(_ value: Bool) { effects.append("tracklock") }
    func setExposureBias(_ value: Int, commandID: Int?) { effects.append("exposure") }
    func clearIgnoreZones() { effects.append("clear-ignore") }
    func learnIgnoreZones(_ colors: [Color], completion: @escaping (Int, Int) -> Void) {
        effects.append("ignore"); ignoreCompletion = completion
    }
    func learnPlayArea(seconds: Int, commandID: Int?, completion: @escaping (Int, Bool, LANLivenessProbe.PCCommand.Area?) -> Void) {
        effects.append("area"); areaCompletion = completion
    }
    var mirrorX = false, mirrorY = false
    func setPlayArea(_ area: LANLivenessProbe.PCCommand.Area) {}
    func clearPlayArea(commandID: Int?) { effects.append("clear-area") }
    func setAutoClipEnabled(_ value: Bool, commandID: Int?) { effects.append("auto-clip") }
    func requestAutoClip(commandID: Int?, reason: String? = nil) { effects.append("clip"); requestedReasons.append(reason ?? "pc-command id=\\(commandID!)") }
'''
    # 重複排除の保存状態もそのまま抽出し、旧版・修正版のどちらにも同じ試験を与える。
    setter = block(camera, "func setTrackLockEnabled(")
    start = camera.index(setter) + len(setter)
    support += camera[start:camera.index("    func applyPCCommand(", start)]
    support += block(camera, "func applyPCCommand(") + "\n}\n"
    tests = '''import XCTest
final class PCCommandRecoveryTests: XCTestCase {
    @MainActor func testLearningCacheOutlivesFiniteRecoveryWindowWithoutRelearning() {
        for station in ["A", "B", ""] {
            for seconds in [1, 10, 60] {
                let model = CameraViewModel(); model.station = station
                let command = LANLivenessProbe.PCCommand(id: 95, ignore: nil, clearIgnore: false,
                                                       learnAreaSeconds: seconds, sourceHost: "192.0.2.1")
                model.applyPCCommand(command)
                let started = model.lanProbe.replies.last!.line
                model.now = Double(seconds)
                model.applyPCCommand(command)
                XCTAssertEqual(model.lanProbe.replies.last!.line, started)
                model.areaCompletion?(300, true, .init(minX: 0.2, minY: 0.1, maxX: 0.8, maxY: 0.9))
                let completed = model.lanProbe.replies.last!.line
                model.lanProbe.replies.removeAll() // 完了ACK全3通を偽配送で落とす。
                for time in [Double(seconds) + 0.5, Double(seconds) + 5] {
                    model.now = time
                    model.applyPCCommand(command)
                    XCTAssertEqual(model.lanProbe.replies.last!.line, completed)
                    XCTAssertEqual(model.effects, ["area"])
                }
            }
        }
    }
    @MainActor func testSwingLearningRepliesWithBoundsAndRunsOnlyOnceForEachStation() {
        for station in ["A", "B"] {
            let model = CameraViewModel(); model.station = station
            model.mirrorX = true
            let command = LANLivenessProbe.PCCommand(id: 81, ignore: nil, clearIgnore: false, learnAreaSeconds: 10, sourceHost: "192.0.2.1")
            model.applyPCCommand(command); model.applyPCCommand(command)
            XCTAssertEqual(model.effects, ["area"])
            model.areaCompletion?(300, true, .init(minX: 0.1, minY: 0.2, maxX: 0.7, maxY: 0.9))
            let line = model.lanProbe.replies.last?.line
            XCTAssertEqual(line, "PHONESABER_ACK 1 id=81 station=\(station) platform=ios result=learned detail=samples300-area0.300000_0.200000_0.900000_0.900000")
            model.applyPCCommand(command)
            XCTAssertEqual(model.effects, ["area"])
            XCTAssertEqual(model.lanProbe.replies.last?.line, line)
        }
    }
    @MainActor func testLostAckIsReplayedWithoutRepeatingTheOperation() {
        for station in ["A", "B"] {
            let model = CameraViewModel()
            model.station = station
            let command = LANLivenessProbe.PCCommand(id: 71, ignore: nil, clearIgnore: true, sourceHost: "192.168.1.2")
            model.applyPCCommand(command)
            let first = model.lanProbe.replies.first?.line
            XCTAssertTrue(first?.contains("station=\\(station)") == true)
            model.lanProbe.replies.removeAll() // Wi-Fi断で最初のACK全3通を失ったPCを再現。
            model.applyPCCommand(command)
            XCTAssertEqual(model.effects, ["clear-ignore"])
            XCTAssertEqual(model.lanProbe.replies.first?.line, first, "再送された指示には保存済みACKを返す")
        }
    }
    @MainActor func testNewLearningAtCameraEdgesRepliesWithValidReplacementBounds() {
        for station in ["A", "B", ""] {
            for mirrorX in [false, true] {
                for mirrorY in [false, true] {
                    let model = CameraViewModel(); model.station = station
                    model.mirrorX = mirrorX; model.mirrorY = mirrorY
                    let areas: [LANLivenessProbe.PCCommand.Area] = [
                        .init(minX: 0.1, minY: 0.1, maxX: 0.9, maxY: 0.9),
                        .init(minX: -4.0/479, minY: -2.0/639, maxX: 482.0/479, maxY: 644.0/639),
                        .init(minX: 0.4, minY: 0.5, maxX: 0.6, maxY: 0.7)]
                    for (index, area) in areas.enumerated() {
                        let command = LANLivenessProbe.PCCommand(id: 81 + index, ignore: nil, clearIgnore: false,
                                                               learnAreaSeconds: 10, sourceHost: "192.0.2.1")
                        model.applyPCCommand(command)
                        model.areaCompletion?(600, true, area)
                        let line = model.lanProbe.replies.last!.line
                        XCTAssertTrue(line.contains("result=learned"))
                        let detail = String(line.components(separatedBy: "-area").last!)
                        let parsed = LANLivenessProbe.PCCommand.Area.parse(detail.replacingOccurrences(of: "_", with: ","))
                        XCTAssertNotNil(parsed, "PCが拒否する画面外の学習ACK: \(line)")
                        if index == 1 { XCTAssertEqual(parsed, .init(minX: 0, minY: 0, maxX: 1, maxY: 1)) }
                        if index == 2 {
                            XCTAssertEqual(parsed?.minX, 0.4)
                            XCTAssertEqual(parsed?.minY, mirrorY ? 0.3 : 0.5)
                            XCTAssertEqual(parsed?.maxX, 0.6)
                            XCTAssertEqual(parsed?.maxY, mirrorY ? 0.5 : 0.7)
                        }
                        model.applyPCCommand(command)
                        XCTAssertEqual(model.lanProbe.replies.last?.line, line)
                        XCTAssertEqual(model.effects.count, index + 1)
                    }
                }
            }
        }
    }
    @MainActor func testLearningReplaysLatestAck() {
        let model = CameraViewModel()
        let command = LANLivenessProbe.PCCommand(id: 72, ignore: "red", clearIgnore: false, sourceHost: "169.254.1.2")
        model.applyPCCommand(command)
        let learning = model.lanProbe.replies.first?.line
        model.lanProbe.replies.removeAll()
        model.applyPCCommand(command)
        XCTAssertEqual(model.lanProbe.replies.first?.line, learning)
        model.ignoreCompletion?(2, 0)
        let completed = model.lanProbe.replies.last?.line
        XCTAssertTrue(completed?.contains("result=ignored") == true)
        model.lanProbe.replies.removeAll()
        model.applyPCCommand(command)
        XCTAssertEqual(model.effects, ["ignore"])
        XCTAssertEqual(model.lanProbe.replies.first?.line, completed)
    }
    @MainActor func testSourcesAreIndependentAndReplayTheirOwnAck() {
        let model = CameraViewModel()
        let a = LANLivenessProbe.PCCommand(id: 73, ignore: nil, clearIgnore: true, sourceHost: "192.168.1.2")
        let b = LANLivenessProbe.PCCommand(id: 73, ignore: nil, clearIgnore: false, trackLock: false, sourceHost: "192.168.1.3")
        for command in [a, b, a, b] { model.applyPCCommand(command) }
        XCTAssertEqual(model.effects, ["clear-ignore", "tracklock"])
        XCTAssertEqual(model.lanProbe.replies.map(\\.host), [a.sourceHost!, b.sourceHost!, a.sourceHost!, b.sourceHost!])
        XCTAssertEqual(model.lanProbe.replies[0].line, model.lanProbe.replies[2].line)
        XCTAssertEqual(model.lanProbe.replies[1].line, model.lanProbe.replies[3].line)
    }
    @MainActor func testPendingClipDoesNotRepeatAndReplaysItsCompletion() {
        let model = CameraViewModel()
        let command = LANLivenessProbe.PCCommand(id: 74, ignore: nil, clearIgnore: false, clip: true, sourceHost: "192.168.1.2")
        model.applyPCCommand(command)
        model.applyPCCommand(command)
        XCTAssertEqual(model.effects, ["clip"])
        XCTAssertTrue(model.lanProbe.replies.isEmpty)
        model.clipAcknowledgements.removeValue(forKey: model.clipAcknowledgements.keys.first!)?("clip-saved", "saved")
        let completed = model.lanProbe.replies.first?.line
        XCTAssertNotNil(completed)
        model.lanProbe.replies.removeAll()
        model.applyPCCommand(command)
        XCTAssertEqual(model.effects, ["clip"])
        XCTAssertEqual(model.lanProbe.replies.first?.line, completed)
    }
    @MainActor func testFailedLearningAckIsReplayedWithoutStartingAgain() {
        let model = CameraViewModel()
        let command = LANLivenessProbe.PCCommand(id: 75, ignore: nil, clearIgnore: false, learnAreaSeconds: 10, sourceHost: "192.168.1.2")
        model.applyPCCommand(command)
        model.areaCompletion?(2, false, nil)
        let failed = model.lanProbe.replies.last?.line
        XCTAssertTrue(failed?.contains("result=failed") == true)
        model.lanProbe.replies.removeAll()
        model.applyPCCommand(command)
        XCTAssertEqual(model.effects, ["area"])
        XCTAssertEqual(model.lanProbe.replies.first?.line, failed)
    }
    @MainActor func testSameClipIDFromTwoPCsKeepsSeparateCompletions() {
        let model = CameraViewModel()
        for source in ["192.168.1.2", "192.168.1.3"] {
            model.applyPCCommand(.init(id: 90, ignore: nil, clearIgnore: false, clip: true, sourceHost: source))
        }
        XCTAssertEqual(model.requestedReasons.count, 2)
        guard model.requestedReasons.count == 2 else { return }
        XCTAssertNotEqual(model.requestedReasons[0], model.requestedReasons[1])
        model.clipAcknowledgements.removeValue(forKey: model.requestedReasons[0])?("clip-saved", "saved")
        model.clipAcknowledgements.removeValue(forKey: model.requestedReasons[1])?("clip-off", "off")
        XCTAssertEqual(model.lanProbe.replies.map(\\.host), ["192.168.1.2", "192.168.1.3"])
        XCTAssertTrue(model.lanProbe.replies[0].line.contains("result=clip-saved"))
        XCTAssertTrue(model.lanProbe.replies[1].line.contains("result=clip-off"))
    }
    @MainActor func testLongLearningKeepsAckAndOldCompletionCannotOverwriteReusedID() {
        let model = CameraViewModel()
        let command = LANLivenessProbe.PCCommand(id: 91, ignore: nil, clearIgnore: false, learnAreaSeconds: 60, sourceHost: "192.168.1.2")
        model.applyPCCommand(command)
        let oldCompletion = model.areaCompletion
        model.now = 60
        model.areaCompletion?(100, true, nil)
        let completed = model.lanProbe.replies.last?.line
        model.applyPCCommand(command)
        XCTAssertEqual(model.effects, ["area"])
        XCTAssertEqual(model.lanProbe.replies.last?.line, completed)
        model.now = 71
        model.applyPCCommand(command)
        XCTAssertEqual(model.effects, ["area", "area"])
        let count = model.lanProbe.replies.count
        oldCompletion?(0, false, nil)
        XCTAssertEqual(model.lanProbe.replies.count, count)
        model.applyPCCommand(command)
        XCTAssertTrue(model.lanProbe.replies.last?.line.contains("result=learning") == true)
    }
    @MainActor func testNewIDsExecuteAndCommandsWithoutAReplyHostStillDeduplicate() {
        let model = CameraViewModel()
        for id in [76, 76, 77, 77] {
            model.applyPCCommand(.init(id: id, ignore: nil, clearIgnore: true))
        }
        XCTAssertEqual(model.effects, ["clear-ignore", "clear-ignore"])
        XCTAssertTrue(model.lanProbe.replies.isEmpty)
    }
}

// 指示は実C#が生成し、ACKは実Swiftが生成する。通信・カメラ・時計だけを代用する。
@MainActor func replayRetrySchedule(_ schedule: String, output: String) throws {
    let steps = try String(contentsOfFile: schedule, encoding: .utf8).split(separator: "\\n").map {
        $0.split(separator: "\\t", maxSplits: 1, omittingEmptySubsequences: false).map(String.init)
    }
    var trace: [String] = []
    for pcStation in ["A", "B"] {
        for phoneStation in [pcStation, ""] {
            for loseAll in [false, true] {
                let model = CameraViewModel(); model.station = phoneStation
                trace.append("case\\t\\(pcStation)\\t\\(phoneStation.isEmpty ? "none" : phoneStation)\\t\\(loseAll ? "lost" : "recover")")
                let commandText = steps.first![1]
                trace.append("queue\\t0\\t\\(commandText)")
                var finishesAt: Double?, completed = false, firstCompletion = true, dropped = 0
                func drain(_ time: Double) {
                    for reply in model.lanProbe.replies {
                        let learned = reply.line.contains("result=learned")
                        let drop = learned && (firstCompletion || loseAll)
                        for _ in 0..<3 {
                            trace.append("\\(drop ? "drop" : "ack")\\t\\(time)\\t\\(reply.line)")
                            if drop { dropped += 1 }
                        }
                        if learned { firstCompletion = false }
                    }
                    model.lanProbe.replies.removeAll()
                }
                for step in steps {
                    let time = Double(step[0])!
                    if let finish = finishesAt, time >= finish, !completed {
                        model.now = finish
                        model.areaCompletion?(300, true, .init(minX: 0.2, minY: 0.1, maxX: 0.8, maxY: 0.9))
                        completed = true
                        drain(finish)
                    }
                    model.now = time
                    if !step[1].isEmpty {
                        var command = LANLivenessProbe.parseCommand(step[1])!
                        command.sourceHost = "192.0.2.1"
                        model.applyPCCommand(command)
                        if finishesAt == nil { finishesAt = time + Double(command.learnAreaSeconds!) }
                        drain(time)
                    }
                }
                precondition(model.effects == ["area"], "同じIDの再送で再学習しない")
                precondition(dropped >= 3, "初回の完了ACK全3通を落とす")
                trace.append("end")
            }
        }
    }
    try (trace.joined(separator: "\\n") + "\\n").write(toFile: output, atomically: true, encoding: .utf8)
}
'''
    developer = Path(subprocess.check_output(["xcode-select", "-p"], text=True).strip()) / "Platforms/MacOSX.platform/Developer"
    frameworks = developer / "Library/Frameworks"
    with tempfile.TemporaryDirectory(prefix="phonesaber-pc-command-") as directory:
        tmp = Path(directory)
        (tmp / "Support.swift").write_text(support)
        (tmp / "Tests.swift").write_text(tests)
        (tmp / "main.swift").write_text('''import XCTest
import Darwin
let suite = PCCommandRecoveryTests.defaultTestSuite
suite.run()
guard let run = suite.testRun, run.executionCount == 11, run.hasSucceeded else { exit(1) }
if CommandLine.arguments.count == 3 {
    try MainActor.assumeIsolated { try replayRetrySchedule(CommandLine.arguments[1], output: CommandLine.arguments[2]) }
}
''')
        subprocess.run(["xcrun", "swiftc", "-O", "-module-cache-path", str(tmp / "cache"),
                        str(tmp / "Support.swift"), str(tmp / "Tests.swift"), str(tmp / "main.swift"),
                        "-I", str(developer / "usr/lib"), "-L", str(developer / "usr/lib"), "-F", str(frameworks),
                        "-Xlinker", "-rpath", "-Xlinker", str(frameworks),
                        "-Xlinker", "-rpath", "-Xlinker", str(developer / "usr/lib"),
                        "-o", str(tmp / "tests")], check=True)
        replay = [str(args.retry_schedule), str(args.ack_trace)] if args.retry_schedule else []
        return subprocess.run([str(tmp / "tests")] + replay, timeout=30).returncode


if __name__ == "__main__":
    raise SystemExit(main())
