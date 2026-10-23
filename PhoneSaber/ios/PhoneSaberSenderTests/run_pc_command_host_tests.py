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
    args = parser.parse_args()
    camera = (subprocess.check_output(
        ["git", "show", f"{args.revision}:{(SOURCE / 'CameraViewModel.swift').relative_to(ROOT)}"],
        cwd=ROOT, text=True) if args.revision else (SOURCE / "CameraViewModel.swift").read_text())
    udp = (SOURCE / "UDPSender.swift").read_text()
    support = "import Foundation\nfinal class LANLivenessProbe {\n"
    # 指示の型とACKの整形は実ソース。ネットワークへ出す直前のhost・本文を観測する。
    support += block(udp, "struct PCCommand:") + "\n"
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
    var areaCompletion: ((Int, Bool) -> Void)?
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
    func learnPlayArea(seconds: Int, commandID: Int?, completion: @escaping (Int, Bool) -> Void) {
        effects.append("area"); areaCompletion = completion
    }
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
        model.areaCompletion?(2, false)
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
        model.areaCompletion?(100, true)
        let completed = model.lanProbe.replies.last?.line
        model.applyPCCommand(command)
        XCTAssertEqual(model.effects, ["area"])
        XCTAssertEqual(model.lanProbe.replies.last?.line, completed)
        model.now = 71
        model.applyPCCommand(command)
        XCTAssertEqual(model.effects, ["area", "area"])
        let count = model.lanProbe.replies.count
        oldCompletion?(0, false)
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
guard let run = suite.testRun, run.executionCount == 8, run.hasSucceeded else { exit(1) }
''')
        subprocess.run(["xcrun", "swiftc", "-O", "-module-cache-path", str(tmp / "cache"),
                        str(tmp / "Support.swift"), str(tmp / "Tests.swift"), str(tmp / "main.swift"),
                        "-I", str(developer / "usr/lib"), "-L", str(developer / "usr/lib"), "-F", str(frameworks),
                        "-Xlinker", "-rpath", "-Xlinker", str(frameworks),
                        "-Xlinker", "-rpath", "-Xlinker", str(developer / "usr/lib"),
                        "-o", str(tmp / "tests")], check=True)
        return subprocess.run([str(tmp / "tests")], timeout=30).returncode


if __name__ == "__main__":
    raise SystemExit(main())
