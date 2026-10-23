#!/usr/bin/env python3
"""PC制御の実メソッドをhost XCTestで検証。待機だけ手動で進め、認識・通信・UIは代役。"""
import argparse
from pathlib import Path
import subprocess
import tempfile

from run_latency_host_tests import ROOT, SOURCE, block
from run_play_area_host_tests import run as run_play_area_tests


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--revision', help='修正前commitの実ソースでも同じテストを実行')
    args = parser.parse_args()

    def source(name):
        path = SOURCE / name
        return (subprocess.check_output(['git', 'show', f'{args.revision}:{path.relative_to(ROOT)}'],
                                        cwd=ROOT, text=True) if args.revision else path.read_text())

    camera, frames, udp = map(source, ['CameraViewModel.swift', 'FrameProcessor.swift', 'UDPSender.swift'])
    support = '''import Foundation
import XCTest
// 学習待機の完了順序だけを制御する。実装の完了クロージャはそのまま実行する。
@MainActor enum LearnSleeper {
    static var pending: [CheckedContinuation<Void, Never>] = []
    static func wait() async { await withCheckedContinuation { pending.append($0) } }
    static func finish(_ index: Int = 0) async {
        pending.remove(at: index).resume()
        for _ in 0..<20 { await Task.yield() }
    }
}
enum SaberColor: Hashable { case red, blue }
struct SaberIgnoreZone: Equatable { let value: Int }
struct SaberIgnoreLearner {
    static let seconds = 3.0
    var zones: [SaberColor: [SaberIgnoreZone]]
    init(startedAt: Double, colors: [SaberColor]) {
        zones = Dictionary(uniqueKeysWithValues: colors.map { ($0, [SaberIgnoreZone(value: Int(startedAt))]) })
    }
}
final class FrameProcessor {
    let pendingLock = NSLock()
    var ignoreLearners: [UUID: SaberIgnoreLearner] = [:]
    var serial = 0.0
    var trackChanges = 0
    func setTrackLockEnabled(_ enabled: Bool) { trackChanges += 1 }
    func clock() -> Double { serial += 1; return serial }
    func reset() -> Int { cancelIgnoreLearning(); return 0 }
    func finishPlayAreaLearning() -> Int? { nil }
'''
    for marker in ['func startIgnoreLearning(', 'func finishIgnoreLearning(', 'func cancelIgnoreLearning(']:
        support += block(frames, marker) + '\n'
    support += '''}
struct ConnectivityLog {
    static let shared = ConnectivityLog()
    func record(_ tag: String, _ text: String) {}
}
final class LANLivenessProbe {
    var acks: [String] = []
    func sendAck(to host: String, line: String) { acks.append(line) }
'''
    for marker in ['struct PCCommand:', 'static func formatAck(']:
        support += block(udp, marker) + '\n'
    support += '''}
struct Delivery { func suspend() {} }
final class AVCaptureDevice {}
struct ShutterController { var active = false; var appliedISO: Float? }
struct DeviceThermalLevel {
    init(state: ProcessInfo.ThermalState) {}
    var metadataValue: String { "nominal" }
}
enum PlayAreaSettings { static let exposureBiasKey = "unused" }
@MainActor final class CameraViewModel {
    let processor = FrameProcessor(), lanProbe = LANLivenessProbe()
    let delivery = Delivery()
    var now = 0.0
    var connectivityClock: () -> Double { { self.now } }
    var station = "A", host = "10.0.0.1"
    var lifecycleGeneration = 0, applyCount = 0
    var running = true, autoClipEnabled = false
    var ignoreZones: [SaberColor: [SaberIgnoreZone]] = [:]
    var ignoreLearning = false
    static let maximumIgnoreZones = 8
    var clipAcknowledgements: [String: (String, String) -> Void] = [:]
    var trackLockEnabled = true, exposureBias = -2, shutter = 2000, shutterISO = 2000
    var commandsApplied: Int { processor.trackChanges }
    static let trackLockKey = "unused", shutterISOKey = "unused", shutterKey = "unused"
    static let shutterISOChoices = [2000, 250, 200, 160, 125, 100, 80, 64], shutterChoices = [0, 500, 1000, 2000]
    static let exposureBiasChoices = [0, -1, -2]
    var shutterStatus = "", exposureBiasStatus = ""
    var exposureBiasCamera: AVCaptureDevice?
    var shutterController = ShutterController()
    func applyShutter(_ value: Int, to camera: AVCaptureDevice, requestedISO: Int? = nil) -> Bool {
        shutterController.active = value != 0
        shutterController.appliedISO = value == 0 ? nil : max(80.5, Float(requestedISO ?? shutterISO))
        return true
    }
    func applyCameraExposureExperiment(to camera: AVCaptureDevice) {}
    func applyExposureBias(to camera: AVCaptureDevice, reason: String) {}
    func refreshShutterStatus() {}
    func updateAutoClipEnvironment(thermal: String) {}
    func setShutterEmitter(_ value: Bool) {}
    func setAutoClipEnabled(_ value: Bool, commandID: Int? = nil) {}
    func requestAutoClip(commandID: Int? = nil, reason: String? = nil) {}
    var mirrorX = false, mirrorY = false
    func setPlayArea(_ area: LANLivenessProbe.PCCommand.Area) {}
    func clearPlayArea(commandID: Int?) {}
    func learnPlayArea(seconds: Int, commandID: Int?, completion: (Int, Bool, LANLivenessProbe.PCCommand.Area?) -> Void) {}
    func applyIgnoreZones() { applyCount += 1 }
    func updateDeliverySettings() {}
    func begin(_ colors: [SaberColor], completion: ((Int, Int) -> Void)? = nil) {
        learnIgnoreZones(colors, completion: completion)
    }
    // stop / configureLAN / camera recovery が共有する実 resetProcessor を呼ぶ。
    func resetForTesting() { lifecycleGeneration += 1; resetProcessor() }
'''
    # 状態の宣言も修正前後の実ソースを使う。
    support += next(line for line in camera.splitlines() if 'private var ignoreLearningIDs:' in line) + '\n'
    for line in camera.splitlines():
        if any(marker in line for marker in ['private var playAreaLearningTask:', 'private var playAreaLearningID:',
                                             'private var playAreaAcknowledgement:']):
            support += line + '\n'
    support += 'var playAreaLearning = false\n'
    setter = block(camera, 'func setTrackLockEnabled(')
    start = camera.index(setter) + len(setter)
    end = camera.index('    func applyPCCommand(', start)
    support += camera[start:end]
    for marker in ['private func learnIgnoreZones(', 'func clearIgnoreZones(', 'func applyPCCommand(',
                   'private func resetProcessor(', 'func setTrackLockEnabled(', 'func setExposureBias(',
                   'func setShutterISO(', 'private func shutterISOAckDetail(', 'func setShutter(', 'static func exposureBiasLabel(']:
        method = block(camera, marker)
        method = method.replace('try? await Task.sleep(nanoseconds: UInt64(SaberIgnoreLearner.seconds * 1_000_000_000))',
                                'await LearnSleeper.wait()')
        support += method + '\n'
    if 'private func cancelIgnoreLearning(' in camera:
        support += block(camera, 'private func cancelIgnoreLearning(') + '\n'
    if 'private func cancelPlayAreaLearning(' in camera:
        support += block(camera, 'private func cancelPlayAreaLearning(') + '\n'
    support += '}\n'
    tests = '''import XCTest
@MainActor final class PCControlTests: XCTestCase {
    func testShutterISOAckReportsAppliedValueForBothStations() {
        for station in ["A", "B"] {
            let model = CameraViewModel()
            model.station = station
            model.exposureBiasCamera = AVCaptureDevice()
            for (id, iso) in [2000, 250, 200, 160, 125, 100, 80, 64].enumerated() {
                model.applyPCCommand(.init(id: id, ignore: nil, clearIgnore: false, shutterISO: iso, sourceHost: model.host))
                XCTAssertEqual(model.shutterISO, iso, "選択値は保存・UI・ctl用に維持")
                let actual = max(80.5, Float(iso))
                XCTAssertTrue(model.lanProbe.acks.last?.hasSuffix("result=shutter-iso detail=" + String(actual)) == true)
                XCTAssertTrue(model.lanProbe.acks.last?.contains("station=" + station) == true)
            }
            model.running = false
            model.applyPCCommand(.init(id: 100, ignore: nil, clearIgnore: false, shutterISO: 64, sourceHost: model.host))
            XCTAssertTrue(model.lanProbe.acks.last?.hasSuffix("detail=64") == true, "停止中は予約した選択値")
        }
    }
    func begin(_ model: CameraViewModel, _ color: SaberColor, completion: ((Int, Int) -> Void)? = nil) async {
        model.begin([color], completion: completion)
        for _ in 0..<20 { await Task.yield() }
    }
    func testInterruptedLearningCannotCompleteAfterProcessorReset() async {
        let model = CameraViewModel()
        var completed = false
        await begin(model, .red) { _, _ in completed = true }
        model.resetForTesting()
        await LearnSleeper.finish()
        XCTAssertFalse(completed, "停止・カメラ再設定後に旧完了ACKを返さない")
        XCTAssertEqual(model.applyCount, 0, "中断した学習から設定を再適用しない")
        XCTAssertFalse(model.ignoreLearning)
        XCTAssertTrue(model.ignoreZones.isEmpty)
    }
    func testNewLearningSupersedesOldLearningEvenWhenOldTaskFinishesFirst() async {
        let model = CameraViewModel()
        var oldCompleted = false, newCompleted = false
        await begin(model, .red) { _, _ in oldCompleted = true }
        await begin(model, .blue) { _, _ in newCompleted = true }
        await LearnSleeper.finish()
        XCTAssertFalse(oldCompleted)
        XCTAssertTrue(model.ignoreLearning, "新しい学習の表示を旧完了で消さない")
        XCTAssertNil(model.ignoreZones[.red], "旧学習の範囲を保存しない")
        await LearnSleeper.finish()
        XCTAssertTrue(newCompleted)
        XCTAssertEqual(model.ignoreZones[.blue], [SaberIgnoreZone(value: 2)])
        XCTAssertEqual(model.applyCount, 1)
        XCTAssertFalse(model.ignoreLearning)
    }
    func testOldLearningCannotOverwriteZonesAfterNewLearningHasCompleted() async {
        let model = CameraViewModel()
        var oldCompleted = false
        await begin(model, .red) { _, _ in oldCompleted = true }
        await begin(model, .blue)
        await LearnSleeper.finish(1)
        XCTAssertEqual(model.ignoreZones[.blue], [SaberIgnoreZone(value: 2)])
        await LearnSleeper.finish()
        XCTAssertFalse(oldCompleted)
        XCTAssertNil(model.ignoreZones[.red])
        XCTAssertEqual(model.applyCount, 1)
        XCTAssertFalse(model.ignoreLearning)
    }
    func testClearInvalidatesPendingCompletion() async {
        let model = CameraViewModel()
        var completed = false
        await begin(model, .red) { _, _ in completed = true }
        model.clearIgnoreZones()
        await LearnSleeper.finish()
        XCTAssertFalse(completed)
        XCTAssertEqual(model.applyCount, 1, "clearの1回だけ")
        XCTAssertTrue(model.ignoreZones.isEmpty)
    }
    func command(_ id: Int = 10, host: String = "10.0.0.1") -> LANLivenessProbe.PCCommand {
        .init(id: id, ignore: nil, clearIgnore: false, trackLock: false, sourceHost: host)
    }
    func testSameIDFromAnotherPCIsNewCommand() {
        let model = CameraViewModel()
        model.applyPCCommand(command())
        model.applyPCCommand(command(host: "10.0.0.2"))
        XCTAssertEqual(model.commandsApplied, 2)
    }
    func testSameIDAfterStationChangeIsNewCommand() {
        let model = CameraViewModel()
        model.applyPCCommand(command())
        model.station = "B"
        model.applyPCCommand(command())
        XCTAssertEqual(model.commandsApplied, 2)
    }
    func testIDReuseAfterPCCommandWindowAndBoundedDeduplication() {
        let model = CameraViewModel()
        model.applyPCCommand(command())
        model.now = 9.9
        model.applyPCCommand(command())
        XCTAssertEqual(model.commandsApplied, 1, "10秒以内の再送は実行しない")
        model.now = 11
        model.applyPCCommand(command())
        XCTAssertEqual(model.commandsApplied, 2, "PC再起動後のID再利用を永久拒否しない")
        model.applyPCCommand(command())
        XCTAssertEqual(model.commandsApplied, 2)
    }
}
'''
    developer = Path(subprocess.check_output(['xcode-select', '-p'], text=True).strip()) / 'Platforms/MacOSX.platform/Developer'
    with tempfile.TemporaryDirectory(prefix='phonesaber-pc-control-') as directory:
        tmp = Path(directory)
        (tmp / 'Support.swift').write_text(support)
        (tmp / 'Tests.swift').write_text(tests)
        (tmp / 'main.swift').write_text('''import XCTest
import Darwin
let suite = PCControlTests.defaultTestSuite
suite.run()
guard let run = suite.testRun, run.executionCount >= 7, run.hasSucceeded else { exit(1) }
''')
        subprocess.run(['xcrun', 'swiftc', '-O', '-module-cache-path', str(tmp / 'cache'),
                        *[str(tmp / name) for name in ['Support.swift', 'Tests.swift', 'main.swift']],
                        '-I', str(developer / 'usr/lib'), '-L', str(developer / 'usr/lib'),
                        '-F', str(developer / 'Library/Frameworks'), '-Xlinker', '-rpath', '-Xlinker', str(developer / 'Library/Frameworks'),
                        '-Xlinker', '-rpath', '-Xlinker', str(developer / 'usr/lib'), '-o', str(tmp / 'tests')], check=True)
        control_result = subprocess.run([str(tmp / 'tests')], timeout=30).returncode
    area_result = run_play_area_tests(args.revision)
    return control_result or area_result


if __name__ == '__main__':
    raise SystemExit(main())
