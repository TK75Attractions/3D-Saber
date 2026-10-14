"""P2P 転送の実メソッドで読み取り失敗と EOF を区別する、GUI 不要の回帰テスト。"""
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest


SOURCE = Path(__file__).resolve().parents[1] / "PhoneSaberSender"


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "macOS/Swift required")
class DebugTransferReadErrorTests(unittest.TestCase):
    def test_read_failure_finishes_without_sending_a_truncated_request(self):
        transfer = (SOURCE / "DebugBundleTransfer.swift").read_text()
        start = transfer.index("    private func sendNextChunk() {")
        end = transfer.index("    private func receiveResponse() {", start)
        finish_start = transfer.index("    private func finish(_ outcome: Outcome) {")
        finish_end = transfer.index("\n}\n", finish_start)
        # ファイル読み取りと送信処理は実コード。ネットワークの境界だけを spy にする。
        source = HEAD + transfer[start:end] + transfer[finish_start:finish_end] + TAIL
        for flags in ([], ["-D", "DEBUG"]):
            with self.subTest(flags=flags), tempfile.TemporaryDirectory(
                prefix="phonesaber-transfer-read-"
            ) as temporary:
                directory = Path(temporary)
                fixture = directory / "Fixture.swift"
                fixture.write_text(source)
                binary = directory / "fixture"
                compiled = subprocess.run(
                    ["xcrun", "swiftc", "-O", *flags,
                     "-module-cache-path", str(directory / "cache"),
                     str(fixture), "-o", str(binary)],
                    capture_output=True, text=True, timeout=90,
                )
                self.assertEqual(compiled.returncode, 0, compiled.stderr)
                run = subprocess.run([str(binary), str(directory)],
                                     capture_output=True, text=True, timeout=10)
                self.assertEqual(run.returncode, 0, run.stdout + run.stderr)


HEAD = r'''import Foundation
import Darwin
final class SpyConnection {
    enum Context { case finalMessage }
    enum Completion { case contentProcessed((Error?) -> Void) }
    var payload = Data()
    var eofCount = 0
    var cancelled = false
    var afterChunk: (() -> Void)?
    func cancel() { cancelled = true }
    func send(content: Data?, contentContext: Context? = nil, isComplete: Bool = false,
              completion: Completion) {
        if let content { payload.append(content); afterChunk?() }
        if contentContext == .finalMessage { eofCount += 1 }
        if case .contentProcessed(let callback) = completion { callback(nil) }
    }
}
final class Uploader {
    enum Outcome { case failed(Error) }
    var file: FileHandle?
    var browser: SpyConnection?
    var connection: SpyConnection?
    var keepAlive: Uploader?
    var completion: ((Outcome) -> Void)?
    var error: NSError?
    var finishCount = 0
    func run() { sendNextChunk() }
'''

TAIL = r'''
}
let directory = URL(fileURLWithPath: CommandLine.arguments[1])
var failures = 0
for scenario in ["read-error", "mid-read-error", "eof", "chunks"] {
    let url = directory.appendingPathComponent(scenario)
    let payload = scenario == "chunks" || scenario == "mid-read-error"
        ? Data(repeating: 0x5a, count: 256 * 1024 + 17) : Data()
    try payload.write(to: url)
    let file = try FileHandle(forReadingFrom: url)
    defer { try? file.close() }
    var expectedError: NSError?
    if scenario.hasSuffix("read-error") {
        // open 後に read が失敗する実 FileHandle（EBADF）。I/O 障害と同じ throw 経路。
        let probe = try FileHandle(forReadingFrom: url)
        try probe.close()
        do { _ = try probe.read(upToCount: 256 * 1024) }
        catch { expectedError = error as NSError }
        precondition(expectedError != nil)
    }
    let connection = SpyConnection()
    if scenario == "read-error" { try file.close() }
    if scenario == "mid-read-error" { connection.afterChunk = { try? file.close() } }
    let uploader = Uploader()
    uploader.file = file
    uploader.connection = connection
    uploader.completion = { [weak uploader] outcome in
        if case .failed(let error) = outcome { uploader?.error = error as NSError }
        uploader?.finishCount += 1
    }
    uploader.run()
    let passed: Bool
    if let expectedError {
        passed = uploader.finishCount == 1 && uploader.error?.domain == expectedError.domain
            && uploader.error?.code == expectedError.code && connection.eofCount == 0
            && connection.payload == (scenario == "mid-read-error" ? Data(payload.prefix(256 * 1024)) : Data())
            && connection.cancelled
            && uploader.file == nil && uploader.connection == nil
    } else {
        passed = uploader.finishCount == 0 && uploader.error == nil && connection.eofCount == 1
            && connection.payload == payload && !connection.cancelled
    }
    print(scenario, passed ? "PASS" : "FAIL")
    if !passed { failures += 1 }
}
exit(failures == 0 ? 0 : 1)
'''


if __name__ == "__main__":
    unittest.main()
