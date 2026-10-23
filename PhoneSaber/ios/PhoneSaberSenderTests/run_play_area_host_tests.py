#!/usr/bin/env python3
"""プレイ範囲の実学習・中断・ACKをhostで検証。待機とフレーム境界だけ手動制御する。"""
import argparse
from pathlib import Path
import subprocess
import tempfile

from run_latency_host_tests import ROOT, SOURCE, TESTS, block


def run(revision=None):
    def source(name):
        path = SOURCE / name
        return (subprocess.check_output(['git', 'show', f'{revision}:{path.relative_to(ROOT)}'],
                                        cwd=ROOT, text=True) if revision else path.read_text())

    camera, frames, udp = map(source, ['CameraViewModel.swift', 'FrameProcessor.swift', 'UDPSender.swift'])
    support = '''import Foundation
import CoreMedia
import XCTest
@MainActor enum LearnSleeper {
    static var pending: [CheckedContinuation<Void, Never>] = []
    static var finishing: CheckedContinuation<Void, Never>?
    static func wait() async { await withCheckedContinuation { pending.append($0) } }
    static func finish(_ index: Int = 0) async {
        await withCheckedContinuation {
            finishing = $0
            pending.remove(at: index).resume()
        }
    }
    static func completed() { let done = finishing; finishing = nil; done?.resume() }
}
// 検出直後・学習の書き戻し直前で止め、旧フレームの配送順を決める。
enum LearningFrameBarrier {
    static var action: (() -> Void)?
    static func reach() { action?() }
}
struct ConnectivityLog {
    static let shared = ConnectivityLog()
    func record(_ tag: String, _ text: String) {}
    func exportTail() -> String { "" }
}
struct DebugBundleTransfer {
    static let shared = DebugBundleTransfer()
    func enqueue(bundleURL: URL, automatic: Bool = true) {}
}
struct Delivery { func suspend() {} }
final class LANLivenessProbe {
    var acks: [String] = []
    func sendAck(to host: String, line: String) { acks.append(line) }
'''
    for marker in ['struct PCCommand:', 'static func formatAck(']:
        support += block(udp, marker) + '\n'
    support += '''}
@MainActor final class CameraViewModel {
    let processor = FrameProcessor(expiryScheduler: nil), lanProbe = LANLivenessProbe()
    let delivery = Delivery()
    var now = 0.0, station = "A", host = "192.0.2.1"
    var connectivityClock: () -> Double { { self.now } }
    var running = true, autoClipEnabled = false, mirrorX = false, mirrorY = false
    var playArea: SaberPlayArea?, playAreaLearning = false
    var savedAreas: [SaberPlayArea?] = []
    var clipAcknowledgements: [String: (String, String) -> Void] = [:]
    func persistPlayArea(_ area: SaberPlayArea?) { playArea = area; savedAreas.append(area) }
    func cancelIgnoreLearning() { processor.cancelIgnoreLearning() }
    func updateDeliverySettings() {}
    func resetForTesting() { resetProcessor() }
    func setShutterISO(_ value: Int) -> Bool { true }
    func setShutter(_ value: Int) -> Bool { true }
    func shutterISOAckDetail() -> String { "2000" }
    func setShutterEmitter(_ value: Bool) {}
    func setTrackLockEnabled(_ value: Bool) {}
    func setExposureBias(_ value: Int, commandID: Int?) {}
    func clearIgnoreZones() {}
    func learnIgnoreZones(_ colors: [SaberColor], completion: @escaping (Int, Int) -> Void) {}
    func setAutoClipEnabled(_ enabled: Bool, commandID: Int?) {}
    func requestAutoClip(commandID: Int?, reason: String?) {}
'''
    # 状態と学習メソッドは本番から抽出し、保存先・通信先だけ記録に置換する。
    for line in camera.splitlines():
        if any(marker in line for marker in ['private var playAreaLearningTask:',
                                             'private var playAreaAcknowledgement:',
                                             'private var playAreaLearningID:', 'private var pcCommands:']):
            support += line + '\n'
    support += block(camera, 'enum PlayAreaSettings') + '\n'
    for marker in ['func learnPlayArea(', 'private func finishPlayAreaLearning(',
                   'func clearPlayArea(', 'func setPlayArea(', 'private func applyPlayArea(',
                   'private func resetProcessor(', 'func applyPCCommand(']:
        support += block(camera, marker).replace(
            'try? await Task.sleep(nanoseconds: UInt64(seconds) * 1_000_000_000)',
            'await LearnSleeper.wait(); defer { LearnSleeper.completed() }') + '\n'
    if 'private func cancelPlayAreaLearning(' in camera:
        support += block(camera, 'private func cancelPlayAreaLearning(') + '\n'
    # 予約時点の完了コールバックを保持し、Taskのcancel確認を通過済みでも安全か検証する。
    if 'private var playAreaLearningID:' in camera:
        support += '''func pendingCompletionForTesting() -> () -> Void {
            let learningID = playAreaLearningID!
            return { self.finishPlayAreaLearning(id: "test", learningID: learningID) }
        }
'''
    else:
        support += '''func pendingCompletionForTesting() -> () -> Void {
            { self.finishPlayAreaLearning(id: "test") }
        }
'''
    support += '}\n'
    for marker in ['struct CameraFrameIntervalStatistics:', 'struct CameraFrameIntervalWindow']:
        support += block(camera, marker) + '\n'

    developer = Path(subprocess.check_output(['xcode-select', '-p'], text=True).strip()) / 'Platforms/MacOSX.platform/Developer'
    with tempfile.TemporaryDirectory(prefix='phonesaber-play-area-') as directory:
        tmp = Path(directory)
        (tmp / 'Support.swift').write_text(support)
        (tmp / 'Tests.swift').write_text((TESTS / 'PlayAreaLearningHostTests.swift').read_text())
        # テスト専用の停止点。認識・学習・revision判定自体は変更しない。
        boundary = '        let detected: [(SaberColor, (PixelPoint, PixelPoint)?)] = ['
        assert frames.count(boundary) == 1
        (tmp / 'FrameProcessor.swift').write_text(frames.replace(boundary, '        LearningFrameBarrier.reach()\n' + boundary))
        (tmp / 'main.swift').write_text('''import XCTest
import Darwin
let suite = PlayAreaLearningHostTests.defaultTestSuite
suite.run()
guard let run = suite.testRun, run.executionCount >= 8, run.hasSucceeded else { exit(1) }
''')
        runtime = [str(SOURCE / name) for name in ['DetectionCore.swift', 'BGRADetection.swift',
                   'DebugVideoRecorder.swift', 'DebugRecordingTriage.swift', 'GuidedRecording.swift']]
        subprocess.run(['xcrun', 'swiftc', '-O', str(tmp / 'Support.swift'), str(tmp / 'FrameProcessor.swift'),
                        str(tmp / 'Tests.swift'), str(tmp / 'main.swift'), *runtime,
                        '-I', str(developer / 'usr/lib'), '-L', str(developer / 'usr/lib'),
                        '-F', str(developer / 'Library/Frameworks'), '-Xlinker', '-rpath', '-Xlinker', str(developer / 'Library/Frameworks'),
                        '-Xlinker', '-rpath', '-Xlinker', str(developer / 'usr/lib'),
                        '-module-cache-path', str(tmp / 'cache'), '-o', str(tmp / 'tests')], check=True)
        return subprocess.run([str(tmp / 'tests')], timeout=60).returncode


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--revision', help='修正前commitの学習・中断処理でも同じテストを実行')
    raise SystemExit(run(parser.parse_args().revision))
