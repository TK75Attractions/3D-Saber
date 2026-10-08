#!/usr/bin/env python3
"""Mac 上で本番 UI メソッドを抽出し、60fps 時の Combine 通知数を測る。

認識・ネットワーク・SwiftUI 描画は測らない。生成 Swift は一時ディレクトリのみ。
変更前: --source /tmp/ios-ui-load-before.swift、変更後: 引数なし。
"""
import argparse
import pathlib
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parents[3]
SOURCE = ROOT / 'PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift'


def block(text, marker):
    start = text.index(marker)
    opening = text.index('{', start)
    depth = 1
    end = opening + 1
    while depth:
        depth += (text[end] == '{') - (text[end] == '}')
        end += 1
    return text[start:end]


def run_checks(source):
    production = SOURCE.parent
    frame_processor = (production / 'FrameProcessor.swift').read_text()
    detection = (production / 'DetectionCore.swift').read_text()
    tests = (ROOT / 'PhoneSaber/ios/PhoneSaberSenderTests/DetectionCoreTests.swift').read_text()
    names = ['testUIPendingStatePreservesFreshPredictedHeldMissingAndCompletionSemantics',
             'testUIBatchRetainsAllDiagnosticSamples',
             'testUIConsumerSnapshotDoesNotHoldProducerLock']
    swift_source = 'import Foundation\nimport QuartzCore\nimport XCTest\n'
    for contents, marker in [
        (detection, 'struct PixelPoint:'), (detection, 'enum SaberColor:'),
        (frame_processor, 'struct DetectedSaber'), (frame_processor, 'struct FrameTrace'),
        (source, 'struct CameraFrameIntervalStatistics:'),
        ((production / 'BGRADetection.swift').read_text(), 'struct SaberDetectionProfile'),
        (frame_processor, 'struct FramePerformanceSample'), (source, 'struct PerformanceMetric'),
        (source, 'final class CameraUIPendingState')
    ]:
        swift_source += block(contents, marker) + '\n'
    swift_source += 'enum HostMonotonicClock { static func now() -> Double { CACurrentMediaTime() } }\n'
    swift_source += 'final class UIStateTests: XCTestCase {\n'
    for name in names:
        swift_source += block(tests, 'func ' + name + '(') + '\n'
    swift_source += '}\nlet suite = UIStateTests.defaultTestSuite\nsuite.run()\n'
    swift_source += 'guard let run = suite.testRun, run.executionCount == 3, run.hasSucceeded else { fatalError("UI checks failed") }\n'
    with tempfile.TemporaryDirectory(prefix='ios-ui-checks-') as tmp:
        swift = pathlib.Path(tmp) / 'Checks.swift'
        swift.write_text(swift_source)
        executable = pathlib.Path(tmp) / 'checks'
        developer = subprocess.check_output(['xcrun', '--sdk', 'macosx', '--show-sdk-platform-path'], text=True).strip() + '/Developer'
        frameworks = str(pathlib.Path(developer) / 'Library/Frameworks')
        subprocess.run(['xcrun', 'swiftc', '-O', '-D', 'DEBUG', '-F', frameworks,
                        '-Xlinker', '-rpath', '-Xlinker', frameworks,
                        '-I', developer + '/usr/lib', '-L', developer + '/usr/lib',
                        '-Xlinker', '-rpath', '-Xlinker', developer + '/usr/lib', '-module-cache-path',
                        str(pathlib.Path(tmp) / 'cache'), str(swift), '-o', str(executable)], check=True)
        subprocess.run([str(executable)], check=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=pathlib.Path, default=SOURCE)
    parser.add_argument('--checks', action='store_true', help='集約器の XCTest を Mac で実行（DEBUG 診断も含む）')
    args = parser.parse_args()
    source = args.source.read_text()
    coalesced = 'class CameraUIPendingState' in source
    if args.checks:
        run_checks(source)
        return
    common = '''import Foundation
import Combine
import QuartzCore
import Darwin
struct PixelPoint: Equatable { let x: Int; let y: Int }
enum SaberColor { case red, blue }
struct DetectedSaber {
    let endpoints: (PixelPoint, PixelPoint)
    let color: SaberColor
    let isFresh: Bool
    var isPredicted = false
}
struct FrameTrace {}
final class FrameProcessor { let currentGeneration = 1 }
'''
    common += block(source, 'enum CameraLifecycleState:') + '\n'
    common += block(source, 'struct CameraLifecycleStateMachine') + '\n'
    common += block((SOURCE.parent / 'EventRecovery.swift').read_text(), 'struct RecoveryBackoff') + '\n'
    fields = '''
    let processor = FrameProcessor()
    var lifecycleGeneration = 1
    var cameraLifecycleEnabled = true
    var cameraLifecycle = CameraLifecycleStateMachine()
    var cameraBackoff = RecoveryBackoff()
    var cameraErrorMessage: String?
    var connectionErrorMessage: String?
    var sendErrorMessages: [Int: String] = [:]
    var automaticResumePending = false
    var host = "127.0.0.1"
    var p2pNoRouteCount = 0
'''
    names = ['running', 'redEndpoints', 'blueEndpoints', 'fps', 'status', 'errorMessage', 'cameraState',
             'lastCameraFrameAge', 'cameraRecoveryMessage', 'automaticResumeMessage', 'redDetectionCount',
             'blueDetectionCount', 'redAttemptCount', 'blueAttemptCount', 'redCompletedCount',
             'blueCompletedCount', 'redErrorCount', 'blueErrorCount', 'sourceDimensions', 'lastLocalSendMs',
             'lastSentEpoch', 'processedFrameCount', 'rejectedFrameCallbackCount']
    for name in names:
        import re
        line = re.search(r'^    @Published[^\n]*\bvar ' + name + r'[^\n]*', source, re.M)[0]
        # running の didSet は送信設定のみなので、この UI 専用ハーネスには不要。
        if name == 'running':
            line = '    @Published var running = true'
        fields += line + '\n'
    metrics = block(source, 'private struct SendMetrics').replace('private struct', 'struct', 1)
    ui = block(source, 'private func receiveCameraFrame') + '\n' + block(source, 'private func publishCameraLifecycle')
    ui += '\n' + block(source, 'private func recomputeErrorMessage')
    if coalesced:
        common += block(source, 'final class CameraUIPendingState') + '\n'
        fields += '''
    let uiPending = CameraUIPendingState()
    var uiSnapshotCountForTesting = 0
'''
        ui += '\n' + block(source, 'func publishPendingUI')
        ui += '\n' + block(source, 'private func assignIfChanged')
        ui += '\n' + block(source, 'private func assignEndpointsIfChanged')
        setup = 'uiPending.setAcceptance(running: true, processor: 1, lifecycle: 1)'
        frame = '''pending.cameraFrame(at: now)
            pending.frame(results, width: 640, height: 480, generation: 1,
                          lifecycle: 1, redEpoch: nil, blueEpoch: nil, trace: nil,
                          redEnqueuedAt: nil, blueEnqueuedAt: nil, requestMs: 0)
            pending.completed(port: 5005, generation: 1, result: .success(now), processingStart: now)
            pending.completed(port: 5006, generation: 1, result: .success(now), processingStart: now)'''
        main_update = 'model.publishPendingUI()'
        final_update = 'model.publishPendingUI()'
        timer_rate = 12
        updates = 'model.uiSnapshotCountForTesting'
    else:
        fields += '    var frameCount = 0\n    var fpsStart = CACurrentMediaTime()\n    var updateCount = 0\n'
        ui += '\n' + block(source, 'private func handle(').replace('private func handle(', 'func handle(', 1)
        completion = block(source, 'let completion: (Result<TimeInterval, Error>) -> Void')
        body = block(completion, 'Task { @MainActor in')[len('Task { @MainActor in'):-1]
        body = body.replace('guard let self, self.running', 'guard self.running')
        ui += '\nfunc complete(port: Int, result: Result<TimeInterval, Error>, processingStart: TimeInterval) { let sendGeneration = 1\n' + body + '\n}'
        ui += '\nfunc camera(at time: TimeInterval) { receiveCameraFrame(at: time) }'
        setup = ''
        frame = '''Task { @MainActor in
                model.updateCount += 1
                model.camera(at: now)
            }
            Task { @MainActor in
                model.updateCount += 1
                model.handle(results, width: 640, height: 480, generation: 1, trace: nil,
                             sent: Model.SendMetrics(lifecycleGeneration: 1))
                }
            Task { @MainActor in
                model.updateCount += 1
                model.complete(port: 5005, result: .success(now), processingStart: now)
                }
            Task { @MainActor in
                model.updateCount += 1
                model.complete(port: 5006, result: .success(now), processingStart: now)
            }'''
        main_update = ''
        final_update = ''
        timer_rate = 12
        updates = 'model.updateCount'
    model = '@MainActor final class Model: ObservableObject {\n' + fields + metrics + '\n' + ui + '\nfunc setup() { cameraLifecycle.requestStart(at: 0); ' + setup + ' }\n}\n'
    driver = '''
@main struct Run {
    @MainActor static func main() async throws {
        let model = Model()
        model.setup()
        var assignments = 0
        let observer = model.objectWillChange.sink { assignments += 1 }
        ''' + ('let pending = model.uiPending' if coalesced else '') + '''
        let start = ProcessInfo.processInfo.systemUptime
        let producer = Task.detached {
            for i in 0..<180 {
                let deadline = start + (Double(i) + 0.5) / 60
                while ProcessInfo.processInfo.systemUptime < deadline { usleep(200) }
                let now = ProcessInfo.processInfo.systemUptime
                let endpoints = (PixelPoint(x: i, y: 10), PixelPoint(x: i + 100, y: 10))
                let results = [DetectedSaber(endpoints: endpoints, color: .red, isFresh: true),
                               DetectedSaber(endpoints: endpoints, color: .blue, isFresh: true)]
                ''' + frame + '''
            }
        }
        for tick in 1...36 {
            let delay = max(0, start + Double(tick) / ''' + str(timer_rate) + ''' - ProcessInfo.processInfo.systemUptime)
            try await Task.sleep(nanoseconds: UInt64(delay * 1e9))
            ''' + main_update + '''
        }
        await producer.value
        try await Task.sleep(nanoseconds: 30_000_000)
        ''' + final_update + '''
        precondition(model.processedFrameCount == 180)
        precondition(model.redAttemptCount == 180 && model.blueAttemptCount == 180)
        precondition(model.redCompletedCount == 180 && model.blueCompletedCount == 180)
        print("mode=''' + ('coalesced' if coalesced else 'before') + ''' frames=180 duration_s=3 updates=\\(''' + updates + ''') updates_per_s=\\(Double(''' + updates + ''') / 3) published_assignments=\\(assignments) assignments_per_s=\\(Double(assignments) / 3)")
        withExtendedLifetime(observer) {}
    }
}
'''
    with tempfile.TemporaryDirectory(prefix='ios-ui-load-') as tmp:
        swift = pathlib.Path(tmp) / 'Harness.swift'
        swift.write_text((common + model + driver).replace('ReferenceWritableKeyPath<CameraViewModel,', 'ReferenceWritableKeyPath<Model,'))
        executable = pathlib.Path(tmp) / 'harness'
        subprocess.run(['xcrun', 'swiftc', '-O', '-parse-as-library', '-module-cache-path', str(pathlib.Path(tmp) / 'cache'), str(swift), '-o', str(executable)], check=True)
        subprocess.run([str(executable)], check=True)


if __name__ == '__main__':
    main()
