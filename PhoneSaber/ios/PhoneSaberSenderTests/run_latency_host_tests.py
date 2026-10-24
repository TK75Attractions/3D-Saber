#!/usr/bin/env python3
"""実ソースと既存XCTestを macOS で実行する（AVCapture設定・認識だけstub）。"""
from pathlib import Path
import argparse
import re
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[3]
SOURCE = ROOT / "PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender"
TESTS = Path(__file__).resolve().parent


def block(text, marker):
    start = text.index(marker)
    opening = text.index("{", start)
    depth, end = 1, opening + 1
    while depth:
        depth += (text[end] == "{") - (text[end] == "}")
        end += 1
    return text[start:end]


def capture_setup(camera, name):
    # main/実装の書込みブロックをそのままコンパイルし、30/60fpsの全setter履歴を比較する。
    start = camera.index("        session.addInput(input)")
    wide_start = camera.rfind("        if let automatic = cameraWideColorAutomatic", 0, start)
    if wide_start < 0:
        wide_start = camera.rfind("        session.automaticallyConfiguresCaptureDeviceForWideColor = false", 0, start)
    wide = camera[wide_start:start] if wide_start >= 0 else ""
    lock_start = camera.index("                try camera.lockForConfiguration()", start)
    device = camera[lock_start:camera.index("            } catch {", lock_start)]
    output_start = camera.index("        output.videoSettings =", lock_start)
    output_end = camera.index("        session.commitConfiguration()", output_start)
    output = camera[output_start:output_end]
    return f'''final class {name} {{
    let session = AVCaptureSession()
    let output = AVCaptureVideoDataOutput()
    let captureQueue = DispatchQueue(label: "stub.capture")
    var lowLatencyCameraSettings = false
    var cameraPostProcessingState: CameraPostProcessingState?
    var cameraWideColorAutomatic: Bool?
    func configure(_ camera: AVCaptureDevice, fps: Int) throws {{
        let targetFPS = Double(fps)
        let options = camera.formats.enumerated().map {{ index, format in
            CameraFormatOption(index: index, width: 640, height: 480,
                               frameRateRanges: format.videoSupportedFrameRateRanges.map {{ $0.minFrameRate...$0.maxFrameRate }})
        }}
        let format = camera.formats[preferredCameraFormatIndex(options: options, targetFPS: targetFPS)!]
{wide}
{device}
{output}
    }}
}}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--baseline", help="比較する旧ソースのディレクトリ（*-before.swift）")
    parser.add_argument("--benchmark", action="store_true", help="統計待ち3msを与えた送信までのhost比較を出す")
    args = parser.parse_args()

    def source(name):
        return (Path(args.baseline) / (name.removesuffix('.swift') + '-before.swift') if args.baseline
                else SOURCE / name).read_text()

    camera = source("CameraViewModel.swift")
    main_camera = subprocess.check_output(["git", "show", "main:PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift"], cwd=ROOT, text=True)
    support = '''import Foundation
import CoreMedia
import CoreVideo
// カメラの初期値・setter順序を再現する。実機の画質・速度を再現しない。
enum AVCaptureColorSpace { case sRGB, HLG_BT2020 }
struct StubRange { let minFrameRate: Double; let maxFrameRate: Double }
final class AVCaptureDevice {
    struct Format {
        var minExposureDuration = CMTime(value: 1, timescale: 100_000)
        var maxExposureDuration = CMTime(value: 1, timescale: 1)
        var minISO: Float = 32, maxISO: Float = 1000
        var id = 0
        var supportedColorSpaces: [AVCaptureColorSpace] = [.sRGB]
        var isAutoVideoFrameRateSupported = true
        var videoSupportedFrameRateRanges = [StubRange(minFrameRate: 1, maxFrameRate: 60)]
    }
    enum ExposureMode { case custom, continuousAutoExposure }
    var exposureMode = ExposureMode.continuousAutoExposure { didSet { writes.append("exposure=\\(exposureMode)") } }
    var customSupported = true
    var lockFails = false
    var customDuration = CMTime.invalid
    var customISO: Float = 0
    func isExposureModeSupported(_ mode: ExposureMode) -> Bool { mode != .custom || customSupported }
    func setExposureModeCustom(duration: CMTime, iso: Float, completionHandler: ((CMTime) -> Void)?) {
        exposureMode = .custom; customDuration = duration; customISO = iso
        writes.append("custom")
    }
    var writes: [String] = []
    var formats = [Format()]
    var activeFormat = Format() { didSet { writes.append("format=\\(activeFormat.id)") } }
    var activeColorSpace = AVCaptureColorSpace.sRGB { didSet { writes.append("color=\\(activeColorSpace)") } }
    var automaticallyAdjustsVideoHDREnabled = true { didSet { writes.append("autoHDR=\\(automaticallyAdjustsVideoHDREnabled)") } }
    var isVideoHDREnabled = true {
        didSet { precondition(!automaticallyAdjustsVideoHDREnabled); writes.append("HDR=\\(isVideoHDREnabled)") }
    }
    var isLowLightBoostSupported = true
    var automaticallyEnablesLowLightBoostWhenAvailable = true {
        didSet { writes.append("lowLight=\\(automaticallyEnablesLowLightBoostWhenAvailable)"); if !isLowLightBoostSupported { unsupportedWrites += 1 } }
    }
    var isGeometricDistortionCorrectionSupported = true
    var isGeometricDistortionCorrectionEnabled = true {
        didSet { writes.append("GDC=\\(isGeometricDistortionCorrectionEnabled)"); if !isGeometricDistortionCorrectionSupported { unsupportedWrites += 1 } }
    }
    var isAutoVideoFrameRateEnabled = true { didSet { writes.append("autoFPS=\\(isAutoVideoFrameRateEnabled)") } }
    var activeVideoMinFrameDuration = CMTime.invalid { didSet { writes.append("min=\\(CMTimeGetSeconds(activeVideoMinFrameDuration))") } }
    var activeVideoMaxFrameDuration = CMTime.invalid { didSet { writes.append("max=\\(CMTimeGetSeconds(activeVideoMaxFrameDuration))") } }
    var unsupportedWrites = 0
    func lockForConfiguration() throws { if lockFails { throw NSError(domain: "fake", code: 1) } }
    func unlockForConfiguration() {}
}
final class AVCaptureSession {
    var writes: [String] = []
    var automaticallyConfiguresCaptureDeviceForWideColor = true { didSet { writes.append("wide=\\(automaticallyConfiguresCaptureDeviceForWideColor)") } }
}
enum StubMedia { case video }
enum StubOrientation { case portrait }
enum StubStabilization { case off }
final class StubConnection {
    var writes: [String] = []
    var videoOrientation = StubOrientation.portrait { didSet { writes.append("orientation=\\(videoOrientation)") } }
    var isVideoMirrored = true { didSet { writes.append("mirror=\\(isVideoMirrored)") } }
    var isVideoStabilizationSupported = true
    var preferredVideoStabilizationMode = StubStabilization.off { didSet { writes.append("stabilization=\\(preferredVideoStabilizationMode)") } }
}
final class AVCaptureVideoDataOutput {
    var writes: [String] = []
    var videoSettings: [String: UInt32] = [:] { didSet { writes.append("pixels=\\(videoSettings)") } }
    var alwaysDiscardsLateVideoFrames = false { didSet { writes.append("discard=\\(alwaysDiscardsLateVideoFrames)") } }
    let connectionValue = StubConnection()
    func connection(with: StubMedia) -> StubConnection? { connectionValue }
    func setSampleBufferDelegate(_ delegate: Any, queue: DispatchQueue) { writes.append("delegate") }
}
extension AVCaptureSession { func addOutput(_ output: AVCaptureVideoDataOutput) {} }
'''
    for marker in ["struct CameraFormatOption:", "func supportsFrameRate(", "func fixedFrameDuration(",
                   "func preferredCameraFormatIndex(", "func highSpeedCameraFormatIndex(",
                   "func storedCameraFPS(", "func disableCameraPostProcessing("]:
        support += block(camera, marker) + "\n"
    support += block(camera, "struct CameraShutterPlan:") + "\n"
    support += block(camera, "struct CameraShutterController") + "\n"
    shutter_key = next(line for line in camera.splitlines() if 'static let shutterKey =' in line)
    support += 'enum LANLivenessProbe { static let shutterChoices = [0, 500, 1000, 2000]; static let shutterISOChoices = [2000, 250, 200, 160, 125, 100, 80, 64] }\n'
    support += 'enum CameraViewModel {\n' + shutter_key + '\n'
    for key in ['shutterISOKey', 'shutterEmitterKey']:
        support += next(line for line in camera.splitlines() if f'static let {key} =' in line) + '\n'
    for marker in ['nonisolated static func storedShutter(', 'nonisolated static func storedShutterISO(',
                   'nonisolated static func storedShutterEmitter(']:
        support += block(camera, marker) + '\n'
    support += '}\n'
    current_camera = (SOURCE / "CameraViewModel.swift").read_text()
    support += block(current_camera, "struct CameraPostProcessingState") + "\n"
    if "func configureCameraPostProcessing(" in camera:
        support += block(camera, "func configureCameraPostProcessing(") + "\n"
    else:
        # WIPはopt-inを持たず、通常起動でも無条件にこの実関数を呼んでいた。
        support += "func configureCameraPostProcessing(_ camera: AVCaptureDevice, experimental: Bool) -> Bool { disableCameraPostProcessing(camera) }\n"
    support += capture_setup(camera, "CaptureSetup") + capture_setup(main_camera, "MainCaptureSetup")
    frames = source("FrameProcessor.swift")
    for marker in ["struct CameraFrameIntervalStatistics:", "struct CameraFrameIntervalWindow"]:
        support += block(camera, marker) + "\n"
    support += "struct DebugBundleTransfer { static let shared = DebugBundleTransfer(); func enqueue(bundleURL: URL, automatic: Bool = true) {} }\n"
    support += "enum ConnectivityLog { static let shared = ConnectivityLogStub() }; struct ConnectivityLogStub { func exportTail() -> String { \"\" } }\n"
    support += block(source("EventRecovery.swift"), "struct FPSComparisonPolicy") + "\n"
    core = (TESTS / "DetectionCoreTests.swift").read_text()
    markers = ["func testFixedShutterStored", "func testFixedShutterISOStorage", "func testFixedShutterISOAndEmitterPreserveStoredChoices", "func testFixedShutterPlan", "func testCameraFormatSelection", "func test120FPSFormat", "func testLatestFramePolicy", "func testPostProcessing",
               "func testDefaultCameraSettings", "func testFrameSendPrecedes", "func testCaptureHealthUses", "func testPlayAreaLearning",
               "func test120FPSMailboxPreserves", "func testDebugRecordingBundleIncludesConnectivityTail", "func testPlayAreaGrowth", "func testIgnoreZoneSnapshot"]
    focused = "import XCTest\nimport CoreMedia\nimport CoreVideo\nimport AVFoundation\nfinal class CameraLatencyTests: XCTestCase {\n"
    for name in re.findall(r"func (test\w+)\(", core):
        if any(("func " + name).startswith(marker) for marker in markers):
            focused += block(core, "func " + name + "(") + "\n"
    focused += r'''func testFixedShutterDeviceWritesAndRevert() throws {
        let camera = AVCaptureDevice()
        camera.activeVideoMinFrameDuration = CMTime(value: 1, timescale: 60)
        camera.activeVideoMaxFrameDuration = camera.activeVideoMinFrameDuration
        let before = camera.writes
        var controller = CameraShutterController()
        XCTAssertTrue(try controller.apply(0, to: camera))
        XCTAssertEqual(camera.writes, before)
        for fps in [60, 30, 60] {
            camera.activeVideoMinFrameDuration = CMTime(value: 1, timescale: Int32(fps))
            camera.activeVideoMaxFrameDuration = camera.activeVideoMinFrameDuration
            for denominator in [500, 1000, 2000] {
                XCTAssertTrue(try controller.apply(denominator, to: camera))
                XCTAssertEqual(camera.exposureMode, .custom)
                XCTAssertEqual(camera.customISO, min(1000, Float(denominator)))
                XCTAssertEqual(CMTimeGetSeconds(camera.customDuration), 1 / Double(denominator), accuracy: 1e-9)
                XCTAssertEqual(camera.activeVideoMinFrameDuration, CMTime(value: 1, timescale: Int32(fps)))
                XCTAssertEqual(camera.activeVideoMaxFrameDuration, camera.activeVideoMinFrameDuration)
            }
            camera.lockFails = true
            XCTAssertThrowsError(try controller.apply(0, to: camera))
            XCTAssertTrue(controller.active)
            camera.lockFails = false
            XCTAssertTrue(try controller.apply(0, to: camera))
            XCTAssertFalse(controller.active)
            XCTAssertEqual(camera.exposureMode, .continuousAutoExposure)
        }
        for iso in [2000, 250, 200, 160, 125, 100, 80, 64] {
            camera.activeFormat.maxISO = 3000
            XCTAssertTrue(try controller.apply(2000, to: camera, requestedISO: iso))
            XCTAssertEqual(camera.customISO, Float(iso))
            XCTAssertEqual(controller.appliedISO, Float(iso))
            XCTAssertEqual(CMTimeGetSeconds(camera.customDuration), 0.0005, accuracy: 1e-9)
        }
        camera.activeFormat.minISO = 80.5
        XCTAssertTrue(try controller.apply(2000, to: camera, requestedISO: 64))
        XCTAssertEqual(camera.customISO, 80.5)
        XCTAssertEqual(controller.appliedISO, 80.5)
        XCTAssertTrue(try controller.apply(0, to: camera))
        XCTAssertNil(controller.appliedISO)
        camera.customSupported = false
        let restored = camera.writes
        XCTAssertFalse(try controller.apply(500, to: camera))
        XCTAssertEqual(camera.writes, restored)
        camera.customSupported = true
        camera.lockFails = true
        XCTAssertThrowsError(try controller.apply(500, to: camera))
        XCTAssertFalse(controller.active)
        XCTAssertEqual(camera.writes, restored)
    }
''' + "\n"
    focused += block(core, "private func sampleBuffer(") + "\n"
    focused += block((TESTS / "EventRecoveryTests.swift").read_text(), "func testFPSComparisonSupports60") + "\n}\n"
    health_tests = (TESTS / "DeviceHealthTests.swift").read_text()
    if args.baseline:
        health_tests = "import XCTest\nfinal class DeviceHealthTests: XCTestCase {\n" + "\n".join(
            block(health_tests, "func " + name + "(") for name in re.findall(r"func (test\w+)\(", health_tests)
            if "CaptureLatencyLog" not in name and "RecordingHealth" not in name) + "\n}\n"
    xcode = subprocess.check_output(["xcode-select", "-p"], text=True).strip()
    developer = Path(xcode) / "Platforms/MacOSX.platform/Developer"
    frameworks = developer / "Library/Frameworks"
    with tempfile.TemporaryDirectory(prefix="phonesaber-latency-") as directory:
        tmp = Path(directory)
        (tmp / "Support.swift").write_text(support)
        (tmp / "Tests.swift").write_text(focused)
        (tmp / "Health.swift").write_text(source("DeviceHealth.swift"))
        (tmp / "HealthTests.swift").write_text(health_tests)
        benchmark = ""
        if args.benchmark:
            benchmark = block(core, "private func sampleBuffer(").replace("private func", "func", 1) + '''
let frame = sampleBuffer(width: 16, height: 16) { _, _ in }
let processor = FrameProcessor(expiryScheduler: nil)
var sendMs: [Double] = []
processor.onResult = { _, _, _, start, _, _ in sendMs.append((HostMonotonicClock.now() - start) * 1000) }
processor.onHealthSample = { _, _, _, _ in Thread.sleep(forTimeInterval: 0.003) }
for _ in 0..<120 { processor.queue.sync { processor.process(frame) } }
let sorted = sendMs.sorted()
print(String(format: "BENCH health-wait=3ms frames=%d processing-start-to-send median=%.3fms p95=%.3fms",
             sorted.count, sorted[sorted.count / 2], sorted[Int(ceil(Double(sorted.count) * 0.95)) - 1]))
'''
        (tmp / "main.swift").write_text('''import XCTest
import Darwin
import Foundation
import CoreVideo
import CoreMedia
''' + benchmark + '''
let suite = XCTestSuite(name: "Camera latency policies (Release host + AVCapture stubs)")
suite.addTest(CameraLatencyTests.defaultTestSuite)
suite.addTest(DeviceHealthTests.defaultTestSuite)
suite.run()
guard let run = suite.testRun, run.executionCount > 0, run.hasSucceeded else { exit(1) }
''')
        runtime_sources = []
        for name in ["DetectionCore.swift", "BGRADetection.swift", "FrameProcessor.swift",
                     "DebugVideoRecorder.swift", "DebugRecordingTriage.swift", "GuidedRecording.swift", "MetricsDatagram.swift"]:
            copied = tmp / name
            copied.write_text(source(name) if name == "FrameProcessor.swift" else (SOURCE / name).read_text())
            runtime_sources.append(str(copied))
        subprocess.run(["xcrun", "swiftc", "-O", "-D", "DEVICE_HEALTH_STANDALONE",
                        str(tmp / "Support.swift"), str(tmp / "Tests.swift"), str(tmp / "Health.swift"),
                        str(tmp / "HealthTests.swift"), str(tmp / "main.swift"), *runtime_sources,
                        "-I", str(developer / "usr/lib"), "-L", str(developer / "usr/lib"),
                        "-F", str(frameworks), "-Xlinker", "-rpath", "-Xlinker", str(frameworks),
                        "-Xlinker", "-rpath", "-Xlinker", str(developer / "usr/lib"),
                        "-module-cache-path", str(tmp / "cache"), "-o", str(tmp / "tests")], check=True)
        camera_result = subprocess.run([str(tmp / "tests")]).returncode
        # P2P internalsは変更せず、本番の経路選択とLAN退避時のログを既存XCTestで検証する。
        routing_sources = []
        for name in ["UDPSender.swift", "P2PSender.swift", "P2PProtocol.swift", "MetricsDatagram.swift"]:
            copied = tmp / name
            content = source(name) if name == "UDPSender.swift" else (SOURCE / name).read_text()
            if "var currentRoute:" not in content and name == "UDPSender.swift":
                # 旧lastRouteを読み出すだけのadapter。旧route本体は変更しない。
                getter = block((SOURCE / name).read_text(), "var currentRoute:")
                content = content.replace("    func update(_ settings: Settings)", getter + "\n    func update(_ settings: Settings)")
            copied.write_text(content)
            routing_sources.append(str(copied))
        (tmp / "Clock.swift").write_text("import CoreMedia\nimport Foundation\n" + block(frames, "enum HostMonotonicClock"))
        (tmp / "main.swift").write_text('''import XCTest
import Darwin
let suite = CoordinateDeliveryTests.defaultTestSuite
suite.run()
guard let run = suite.testRun, run.executionCount > 0, run.hasSucceeded else { exit(1) }
''')
        subprocess.run(["xcrun", "swiftc", "-O", "-D", "DEBUG", "-D", "PIPELINE_ROUTING_STANDALONE",
                        *routing_sources, str(tmp / "Clock.swift"), str(TESTS / "P2PTransportTests.swift"),
                        str(tmp / "main.swift"), "-I", str(developer / "usr/lib"), "-L", str(developer / "usr/lib"),
                        "-F", str(frameworks), "-Xlinker", "-rpath", "-Xlinker", str(frameworks),
                        "-Xlinker", "-rpath", "-Xlinker", str(developer / "usr/lib"),
                        "-module-cache-path", str(tmp / "cache"), "-o", str(tmp / "routing-tests")], check=True)
        routing_result = subprocess.run([str(tmp / "routing-tests")]).returncode
        return camera_result or routing_result


if __name__ == "__main__":
    raise SystemExit(main())
