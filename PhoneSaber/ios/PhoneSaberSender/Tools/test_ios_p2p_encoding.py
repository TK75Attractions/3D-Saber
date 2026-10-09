"""Differential wire-byte check against the original append-based encoder."""
import pathlib
import subprocess
import tempfile
import unittest


class P2PEncodingTests(unittest.TestCase):
    def test_all_header_kinds_and_body_lengths_match_original(self):
        source = pathlib.Path(__file__).resolve().parents[1] / "PhoneSaberSender/P2PProtocol.swift"
        harness = r'''
import Foundation
@main struct EncodingCheck {
    static func main() {
        for kind in [P2PMessageKind.coordinates, .ping, .pong] {
            for color in [P2PColor.red, .blue] {
                for size in 0...PhoneSaberP2P.maximumBodySize {
                    let body = Data((0..<size).map { UInt8(truncatingIfNeeded: $0) })
                    for (session, sequence) in [(UInt32(0), UInt64(0)), (.max, .max), (0x12345678, 0x123456789abcdef0)] {
                        let message = P2PMessage(kind: kind, color: color, session: session, sequence: sequence, body: body)
                        var original = Data(PhoneSaberP2P.magic)
                        original.append(contentsOf: [PhoneSaberP2P.version, kind.rawValue, color.rawValue, 0])
                        withUnsafeBytes(of: session.bigEndian) { original.append(contentsOf: $0) }
                        withUnsafeBytes(of: sequence.bigEndian) { original.append(contentsOf: $0) }
                        original.append(body)
                        precondition(message.encoded() == original)
                    }
                }
            }
        }
        print("4626 wire encodings identical")
    }
}
'''
        with tempfile.TemporaryDirectory(prefix="phonesaber-encoding-") as temporary:
            build = pathlib.Path(temporary)
            swift = build / "EncodingCheck.swift"
            swift.write_text(harness)
            binary = build / "check"
            subprocess.run(["xcrun", "swiftc", "-O", "-module-cache-path", str(build / "cache"), str(source), str(swift), "-o", str(binary)], check=True, capture_output=True, text=True, timeout=120)
            result = subprocess.run([str(binary)], check=True, capture_output=True, text=True, timeout=30)
            self.assertIn("4626 wire encodings identical", result.stdout)
