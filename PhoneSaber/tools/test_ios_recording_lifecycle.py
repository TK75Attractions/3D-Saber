"""実際の録画開始メソッドを host XCTest で実行（カメラ・recorder・転送だけ stub）。"""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

SOURCE = Path(__file__).resolve().parents[1] / 'ios/PhoneSaberSender/PhoneSaberSender'


def block(text, marker):
    start = text.index(marker)
    end = text.index('{', start) + 1
    depth = 1
    while depth:
        depth += (text[end] == '{') - (text[end] == '}')
        end += 1
    return text[start:end]


@unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun'), 'macOS XCTest が必要')
class RecordingLifecycleTests(unittest.TestCase):
    def test_thermal_restart_during_manual_and_guided_recording_start(self):
        camera = (SOURCE / 'CameraViewModel.swift').read_text()
        recorder = (SOURCE / 'DebugRecordingTriage.swift').read_text()
        support = '''import Foundation
import XCTest
struct GuidedRecordingScript {}
enum Segment { case unlabeled }
final class DebugBundleTransfer {
    static let shared = DebugBundleTransfer()
    var active = false
    func setRecordingActive(_ active: Bool) { self.active = active }
}
final class Processor {
    var starts = 0
    func updateDebugCameraDeviceState(_ value: Int?) {}
    func setDebugGuidedPhase(_ value: Int?) {}
    func startDebugRecording(diagnosticColors: Int, cameraExposureExperiment: Int?,
                            guidedScript: GuidedRecordingScript?, completion: (Result<String, Error>) -> Void) {
        starts += 1
    }
}
final class CameraViewModel {
    var debugRecordingEnabled = true, running = true, debugRecordingActive = false
    var debugRecordingFinalizing = false, lastDebugRecordingResult: Int?
    var debugManualLosslessCaptureCount = 0, debugDiagnosticColors = 0
    var debugRecordingStatus = "", debugSegmentLabel = Segment.unlabeled
    var stops = 0, healthRequiresRestart = true, restartSucceeds = true
    let processor = Processor()
    // 発熱判定の再起動は、本物と同じく active な録画を終了し、送信を再開する。
    func publishDeviceHealth(at: TimeInterval, force: Bool) {
        if healthRequiresRestart {
            healthRequiresRestart = false
            if debugRecordingActive {
                stops += 1; debugRecordingActive = false; debugRecordingFinalizing = true
            }
            running = restartSucceeds
        }
    }
    func cameraExposureExperimentSnapshot() -> Int? { nil }
    func refreshDebugRecordingSessions() {}
    func scheduleDebugRecordingMaximumDurationStop() {}
    func beginGuidedSchedule() {}
    func teardownGuidedRecording(stopAudioImmediately: Bool) {}
'''
        support += block(camera, 'func startDebugRecording(guidedScript:') + '\n}\n'
        support += block(recorder, 'enum DebugRecordingLifecyclePolicy') + '\n'
        support += '''final class RecordingStartTests: XCTestCase {
    func testThermalRestartDoesNotFinalizeAnUncreatedRecorder() {
        for guided in [false, true] {
            let model = CameraViewModel()
            model.startDebugRecording(guidedScript: guided ? GuidedRecordingScript() : nil)
            XCTAssertEqual(model.stops, 0)
            XCTAssertEqual(model.processor.starts, 1)
            XCTAssertTrue(model.debugRecordingActive)
            XCTAssertFalse(model.debugRecordingFinalizing)
            XCTAssertTrue(DebugBundleTransfer.shared.active)
        }
    }
    func testFailedThermalRestartDoesNotStartRecordingOnAStoppedCamera() {
        let model = CameraViewModel()
        model.restartSucceeds = false
        DebugBundleTransfer.shared.active = false
        model.startDebugRecording()
        XCTAssertEqual(model.processor.starts, 0)
        XCTAssertFalse(model.debugRecordingActive)
        XCTAssertFalse(model.debugRecordingFinalizing)
        XCTAssertFalse(DebugBundleTransfer.shared.active)
    }
}
'''
        developer = Path(subprocess.check_output(['xcode-select', '-p'], text=True).strip()) / 'Platforms/MacOSX.platform/Developer'
        frameworks = developer / 'Library/Frameworks'
        with tempfile.TemporaryDirectory(prefix='phonesaber-recording-start-') as directory:
            tmp = Path(directory)
            (tmp / 'Support.swift').write_text(support)
            (tmp / 'main.swift').write_text('''import XCTest
import Darwin
let suite = RecordingStartTests.defaultTestSuite
suite.run()
guard let run = suite.testRun, run.executionCount == 2, run.hasSucceeded else { exit(1) }
''')
            command = ['xcrun', 'swiftc', '-O', '-module-cache-path', str(tmp / 'cache'),
                       str(tmp / 'Support.swift'), str(tmp / 'main.swift'),
                       '-I', str(developer / 'usr/lib'), '-L', str(developer / 'usr/lib'),
                       '-F', str(frameworks), '-Xlinker', '-rpath', '-Xlinker', str(frameworks),
                       '-Xlinker', '-rpath', '-Xlinker', str(developer / 'usr/lib'), '-o', str(tmp / 'tests')]
            result = subprocess.run(command, capture_output=True, text=True, timeout=120)
            self.assertEqual(result.returncode, 0, result.stderr)
            result = subprocess.run([str(tmp / 'tests')], capture_output=True, text=True, timeout=30)
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
