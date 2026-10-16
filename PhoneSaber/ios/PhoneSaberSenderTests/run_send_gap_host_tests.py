#!/usr/bin/env python3
"""GUI不要の送信停止回帰。実ソース、既存XCTest、カメラ/UI/探索だけstub。"""
from pathlib import Path
import argparse
import re
import subprocess
import tempfile

from run_latency_host_tests import ROOT, SOURCE, TESTS, block


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--revision", help="指定commitの実ソースで同じ回帰を実行（修正前の失敗確認用）")
    parser.add_argument("--before-gap-fixes", action="store_true", help="前回WIPより前の修正対象関数を現行ソースに戻して回帰を確認")
    args = parser.parse_args()

    def source(name):
        if args.revision:
            return subprocess.check_output(["git", "show", f"{args.revision}:{(SOURCE / name).relative_to(ROOT)}"], cwd=ROOT, text=True)
        current = (SOURCE / name).read_text()
        if args.before_gap_fixes and name in {"UDPSender.swift", "P2PSender.swift", "DebugBundleTransfer.swift", "CameraViewModel.swift"}:
            old = subprocess.check_output(["git", "show", f"440e818^:{(SOURCE / name).relative_to(ROOT)}"], cwd=ROOT, text=True)
            markers = {
                "UDPSender.swift": ["func configure("],
                "P2PSender.swift": ["private func transmit(", "private func finish("],
                "DebugBundleTransfer.swift": ["static func makePackage("],
                "CameraViewModel.swift": ["private func startP2P("],
            }.get(name, [])
            for marker in markers:
                current = current.replace(block(current, marker), block(old, marker))
            if name == "UDPSender.swift":
                head = subprocess.check_output(["git", "show", f"HEAD:{(SOURCE / name).relative_to(ROOT)}"], cwd=ROOT, text=True)
                current = current.replace(block(current, "final class SendGapMonitor:"), block(head, "final class SendGapMonitor:"))
            if name == "CameraViewModel.swift":
                # 初回WIP前はNWPathMonitorのinline処理だった。関数名だけ現行testへ接続する。
                start = old.index("                self.pathStatus = status; self.pathInterface = interface")
                end = old.index("\n            }\n        }\n        pathMonitor.start", start)
                current = current.replace(block(current, "func receiveNetworkPathUpdate("),
                    "func receiveNetworkPathUpdate(status: String, interface: String) {\n" + old[start:end] + "\n}")
        return current

    camera = source("CameraViewModel.swift")
    transfer = source("DebugBundleTransfer.swift")
    tests = (TESTS / "P2PTransportTests.swift").read_text()
    support = "import Foundation\nimport Network\nimport CoreMedia\nimport CryptoKit\n"
    support += block((SOURCE / "FrameProcessor.swift").read_text(), "enum HostMonotonicClock") + "\n"
    for marker in ["enum DestinationHostSource", "struct DestinationHostSelection", "struct DestinationDisplayPolicy",
                   "private struct BonjourDiscoveryUpdate"]:
        support += block(camera, marker).replace("private struct", "struct") + "\n"
    support += '''
struct StubProcessor { let currentGeneration = 7 }
struct StubDiscovery { func start() {} }
// AVCaptureSession・UI の代役。宛先更新・経路通知・P2P callback の関数本体は下の実ソース。
@MainActor final class CameraViewModel {
    let sender: UDPSender
    let p2pSender: P2PSender
    let lanProbe = LANLivenessProbe(port: 1)
    let processor = StubProcessor()
    let bonjourDiscovery = StubDiscovery()
    var running = false
    var cameraLifecycleEnabled = true
    var lanConfigured = false
    var p2pEnabled: Bool
    var p2pState = P2PLinkState.disabled
    var p2pRoundTrip: P2PRoundTripStats.Summary?
    var hostSelection = DestinationHostSelection()
    var host = ""
    var station = ""
    var lifecycleGenerationForTesting: Int { lifecycleGeneration }
    var lifecycleGeneration = 1
    var latestResolvedMac = (name: "", ip: "")
    var destinationDisplay = DestinationDisplayPolicy()
    var connectionMode = "", activeDestination = "", networkDiscoveryStatus = "", otherDiscoveredPC = ""
    var pathStatus = "", pathInterface = ""
    var lastDiscoveryRefresh = 0.0
    var automaticLANDiscoveryEnabled = false
    init(sender: UDPSender, p2pSender: P2PSender, p2pEnabled: Bool, idleTimerUpdater: (Bool) -> Void) {
        self.sender = sender; self.p2pSender = p2pSender; self.p2pEnabled = p2pEnabled
        if p2pEnabled { startP2P() }
    }
    func startForTesting(host: String = "127.0.0.1", manual: Bool = true) {
        running = true; self.host = host
        if manual { hostSelection.setManual(host, resolvedHost: "", serviceName: "") }
        if !host.isEmpty { configureLAN(host: host, generation: lifecycleGeneration) }
    }
    func stop() { running = false; sender.stop(); lanProbe.stop() }
    func updateDiagnosticDestination() {}
    func updateSelectedDestinationDisplay() {}
    func applySenderUpdate(states: [Int: String], errors: [Int: String], generation: Int) {}
'''
    for marker in ["private func receiveBonjourUpdate(", "func applyBonjourForTesting(", "func receiveNetworkPathUpdate(",
                   "private func refreshNetworkDiscovery(", "private func configureLAN(", "private func startP2P(",
                   "func recordLANReplyForTesting("]:
        support += block(camera, marker) + "\n"
    support += "}\n"
    for marker in ["private struct DebugBundleManifestFile", "private struct DebugBundleManifest:", "private enum DebugBundleTransferError"]:
        support += block(transfer, marker) + "\n"
    support += '''
enum DebugRecordingLimits { static let maximumDiskUsageBytes: Int64 = 1024 * 1024 * 1024 }
// パッケージ後の録画ストレージ全体の容量だけstub。file一覧/size/hash/本文は実装を実行。
enum DebugRecordingStorage { static func diskUsage(sessionID: String, in directory: URL) -> Int64 { 0 } }
final class DebugBundleTransfer {
'''
    for line in transfer.splitlines():
        if re.match(r"    private static let maximum(Images|Files|Bytes) =", line):
            support += line + "\n"
    # maximumBytes は型注釈付き。
    support += "    private static let maximumBytes: Int64 = 64 * 1024 * 1024\n"
    for marker in ["static func makePackage(", "private static func checkedRelativePath(", "private static func sha256("]:
        support += block(transfer, marker) + "\n"
    support += "}\n"
    for marker in ["final class LockedBox", "final class FakeBridge"]:
        support += block(tests, marker) + "\n"
    focused = "import XCTest\nimport Network\nimport Foundation\nfinal class SendGapNetworkTests: XCTestCase {\n"
    for name in ["testExpiredPongsBeforeTheTimerDoNotRestoreTheLinkOrCountAsSuccessful",
                 "testDuplicatePongsDoNotRestoreAStaleLink",
                 "testUnchangedBonjourAndPathRefreshPreserveBothLANConnections",
                 "testP2PConnectedNotificationPreservesPendingVerifiedLANCoordinates",
                 "testCoordinateSendFailureInvalidatesP2PAndFallsBackToLAN",
                 "testLateP2PFailureDoesNotSendOlderCoordinatesAfterNewerLANCoordinates",
                 "testRecoveredP2PDiscardsCoordinatesQueuedByTheLANFallback",
                 "testPendingP2PCoordinateFallsBackWhenPongsExpireBeforeCompletion",
                 "testUDPNetworkSendFailureReconnectsOnlyTheFailedPortAndKeepsTheLatestCoordinate"]:
        method = block(tests, "func " + name + "(")
        if "@MainActor\n    func " + name in tests:
            method = "@MainActor\n" + method
        focused += method + "\n"
    for marker in ["private func fastTiming(", "private func waitFor(", "private func waitUntilMain("]:
        focused += ("@MainActor\n" if "waitUntilMain" in marker else "") + block(tests, marker) + "\n"
    focused += "}\n"
    for marker in ["final class SendGapTests", "final class DebugBundleLogTests", "final class CoordinateDeliveryTests", "private final class PipelineLockedValue"]:
        focused += ("@MainActor\n" if "waitUntilMain" in marker else "") + block(tests, marker) + "\n"
    focused += "final class CallbackHistoryTests: XCTestCase {\n" + block(
        (TESTS / "DetectionCoreTests.swift").read_text(), "func testUDPSenderCallbackHistoryIsBoundedAndKeepsAbsoluteIndices(") + "\n}\n"
    developer = Path(subprocess.check_output(["xcode-select", "-p"], text=True).strip()) / "Platforms/MacOSX.platform/Developer"
    frameworks = developer / "Library/Frameworks"
    with tempfile.TemporaryDirectory(prefix="phonesaber-send-gaps-") as directory:
        tmp = Path(directory)
        (tmp / "Support.swift").write_text(support)
        (tmp / "Tests.swift").write_text(focused)
        sources = []
        for name in ["UDPSender.swift", "P2PSender.swift", "P2PProtocol.swift", "MetricsDatagram.swift"]:
            path = tmp / name
            path.write_text(source(name))
            sources.append(str(path))
        (tmp / "main.swift").write_text('''import XCTest
import Darwin
let suite = XCTestSuite(name: "Send gaps (host; camera/UI/discovery stubs)")
suite.addTest(SendGapTests.defaultTestSuite)
suite.addTest(SendGapNetworkTests.defaultTestSuite)
suite.addTest(DebugBundleLogTests.defaultTestSuite)
suite.addTest(CoordinateDeliveryTests.defaultTestSuite)
suite.addTest(CallbackHistoryTests.defaultTestSuite)
suite.run()
guard let run = suite.testRun, run.executionCount >= 17, run.hasSucceeded else { exit(1) }
''')
        subprocess.run(["xcrun", "swiftc", "-O", "-D", "DEBUG", "-D", "PIPELINE_ROUTING_STANDALONE",
                        *sources, str(tmp / "Support.swift"), str(tmp / "Tests.swift"),
                        str(tmp / "main.swift"),
                        "-I", str(developer / "usr/lib"), "-L", str(developer / "usr/lib"), "-F", str(frameworks),
                        "-Xlinker", "-rpath", "-Xlinker", str(frameworks), "-Xlinker", "-rpath", "-Xlinker", str(developer / "usr/lib"),
                        "-module-cache-path", str(tmp / "cache"), "-o", str(tmp / "tests")], check=True)
        return subprocess.run([str(tmp / "tests")]).returncode


if __name__ == "__main__":
    raise SystemExit(main())
