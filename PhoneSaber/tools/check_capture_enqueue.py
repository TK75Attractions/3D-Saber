#!/usr/bin/env python3
"""実iPhone callbackの順序回帰と統計待ち3msのhost比較。カメラ・統計はstub。"""
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SOURCE = "PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/CameraViewModel.swift"


def callback(source):
    start = source.index("    nonisolated func captureOutput(")
    end = source.index("\n    }", start) + len("\n    }")
    return source[start:end]


def main():
    current = callback((ROOT / SOURCE).read_text())
    before = callback(subprocess.check_output(["git", "show", f"HEAD:{SOURCE}"], cwd=ROOT, text=True))
    support = r'''
import Foundation
final class AVCaptureOutput {}
final class CMSampleBuffer {}
final class AVCaptureConnection {}
enum Trace {
    static var order: [String] = []
    static var submitted = 0.0
    static var healthWait = 0.0
}
final class Processor {
    func submit(_ frame: CMSampleBuffer) {
        Trace.submitted = ProcessInfo.processInfo.systemUptime
        Trace.order.append("submit")
    }
}
final class Health {
    func cameraFrame(at time: TimeInterval) {
        Thread.sleep(forTimeInterval: Trace.healthWait)
        Trace.order.append("health")
    }
}
final class Pending { func cameraFrame(at time: TimeInterval) { Trace.order.append("ui") } }
enum ConnectivityLog { static let sendGapMonitor = Monitor() }
final class Monitor { func cameraFrame() { Trace.order.append("monitor") } }
'''
    for name, body in (("Current", current), ("Before", before)):
        support += f"final class {name} {{ let processor = Processor(); let healthMeter = Health(); let uiPending = Pending();\n{body}\n}}\n"
    support += r'''
func bench(_ run: () -> Void) -> Double {
    Trace.healthWait = 0.003
    var elapsed: [Double] = []
    for _ in 0..<40 {
        Trace.order = []
        let start = ProcessInfo.processInfo.systemUptime
        run()
        elapsed.append((Trace.submitted - start) * 1000)
    }
    return elapsed.sorted()[20]
}
let frame = CMSampleBuffer(), output = AVCaptureOutput(), connection = AVCaptureConnection()
let current = Current(), before = Before()
let old = bench { before.captureOutput(output, didOutput: frame, from: connection) }
let new = bench { current.captureOutput(output, didOutput: frame, from: connection) }
guard Trace.order == ["submit", "monitor", "health", "ui"] else {
    fatalError("統計より先に最新画像を処理へ渡す: \(Trace.order)")
}
print(String(format: "PASS actual callback / stub statistics: health-wait=3ms enqueue median before=%.3fms after=%.3fms", old, new))
'''
    with tempfile.TemporaryDirectory(prefix="phonesaber-capture-enqueue-") as directory:
        tmp = Path(directory)
        source = tmp / "main.swift"
        source.write_text(support)
        binary = tmp / "test"
        subprocess.run(["xcrun", "swiftc", "-O", "-module-cache-path", str(tmp / "cache"),
                        str(source), "-o", str(binary)], check=True)
        subprocess.run([str(binary)], check=True)


if __name__ == "__main__":
    main()
