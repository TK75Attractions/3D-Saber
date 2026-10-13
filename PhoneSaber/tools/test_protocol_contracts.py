"""共通ベクトルで実際の Swift/C#/Python の座標プロトコルを照合する。"""
import csv
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
VECTORS = ROOT / "PhoneSaber/tools/fixtures/protocol_coordinates.tsv"
INPUT = ROOT / "Assets/Scripts/Managers/Inputsystem/Input"
IOS = ROOT / "PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender"
MONO = Path("/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents/Resources/Scripting/MonoBleedingEdge")
sys.path.insert(0, str(ROOT / "PhoneSaber"))
import udp_receive_probe as probe
sys.path.insert(0, str(ROOT / "PhoneSaber/tools"))
import saber_motion_replay as replay


class ProtocolContractTests(unittest.TestCase):
    def test_python_discovery_contract_vectors(self):
        with (VECTORS.parent / "protocol_discovery.tsv").open() as source:
            for row in csv.DictReader(source, delimiter="\t"):
                with self.subTest(vector=row["id"]):
                    try:
                        reply = replay.parse_discovery(row["reply"].encode("ascii"), "127.0.0.1")
                    except (ValueError, KeyError):
                        reply = None
                    self.assertEqual(reply is not None, row["replay_parsed"] == "1")

    def test_python_measurement_prefixes(self):
        with VECTORS.open() as source:
            for row in csv.DictReader(source, delimiter="\t"):
                if row["epoch"] == "-":
                    continue
                with self.subTest(vector=row["id"]):
                    parsed = probe.parse_packet(row["payload"].encode("ascii"))
                    self.assertEqual(parsed.timestamp, float(row["epoch"]))
                    self.assertEqual(parsed.payload, row["payload"].split(";", 1)[1])

    @unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "macOS Swift が必要")
    def test_swift_phone_and_bridge_payload_validator(self):
        harness = r'''
import Foundation
@main struct Check {
    static func main() throws {
        let text = try String(contentsOfFile: CommandLine.arguments[1], encoding: .utf8)
        var failures = 0
        for row in text.split(separator: "\n").dropFirst() {
            let fields = row.split(separator: "\t", omittingEmptySubsequences: false).map(String.init)
            let body = Data(fields[1].utf8)
            let expected = fields[3] == "1"
            if P2PPayload.isValid(body) != expected {
                print("FAIL \(fields[0]): expected valid=\(expected)"); failures += 1
            }
            if expected {
                guard let message = P2PMessage.coordinates(fields[1], color: .blue, session: 7, sequence: 9) else {
                    print("FAIL \(fields[0]): valid body could not be encoded"); failures += 1; continue
                }
                let decoded = try P2PMessage.decode(message.encoded()).get()
                precondition(decoded.body == body && decoded.session == 7 && decoded.sequence == 9)
            }
        }
        guard failures == 0 else { exit(1) }
        print("PASS Swift shared payload vectors")
    }
}
'''
        with tempfile.TemporaryDirectory(prefix="phonesaber-contract-swift-") as temporary:
            build = Path(temporary)
            source = build / "Check.swift"
            source.write_text(harness)
            binary = build / "check"
            self.run_checked(["xcrun", "swiftc", "-O", "-module-cache-path", str(build / "cache"),
                              str(IOS / "P2PProtocol.swift"), str(source), "-o", str(binary)])
            self.run_checked([str(binary), str(VECTORS)])

    @unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "macOS Swift が必要")
    def test_ios_discovery_contract_vectors_record_existing_mismatches(self):
        # 現行の差を記録する監査。経路選択の変更はせず、Android の期待値とは別列で照合する。
        clock = (IOS / "FrameProcessor.swift").read_text().split("enum HostMonotonicClock {", 1)[1]
        clock = clock.split("\nfinal class FrameProcessor", 1)[0]
        harness = r'''
@main struct Check {
    static func main() throws {
        let text = try String(contentsOfFile: CommandLine.arguments[1], encoding: .utf8)
        for row in text.split(separator: "\n").dropFirst() {
            let fields = row.split(separator: "\t", omittingEmptySubsequences: false).map(String.init)
            let probe = LANLivenessProbe(clock: { 0 })
            probe.recordReplyForTesting(fields[1])
            guard probe.isAlive == (fields[5] == "1") else {
                print("FAIL \(fields[0]): iOS_alive=\(probe.isAlive)"); exit(1)
            }
        }
        print("PASS iOS existing discovery acceptance (includes documented mismatches)")
    }
}
'''
        with tempfile.TemporaryDirectory(prefix="phonesaber-contract-discovery-") as temporary:
            build = Path(temporary)
            source = build / "Check.swift"
            source.write_text("import Foundation\nimport CoreMedia\nenum HostMonotonicClock {" + clock + harness)
            binary = build / "check"
            self.run_checked(["xcrun", "swiftc", "-O", "-module-cache-path", str(build / "cache"),
                              *(str(IOS / name) for name in ("UDPSender.swift", "P2PSender.swift", "P2PProtocol.swift", "MetricsDatagram.swift")),
                              str(source), "-o", str(binary)])
            self.run_checked([str(binary), str(VECTORS.parent / "protocol_discovery.tsv")])

    @unittest.skipUnless((MONO / "bin/mono").is_file(), "Unity Mono が必要")
    def test_csharp_receiver_payload_and_measurement(self):
        harness = r'''
using System;
using System.IO;
using System.Text;
using System.Globalization;
class Check {
    static int Main(string[] args) {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        var parser = new PhoneSaberPacketParser();
        foreach (var packet in PhoneSaberParserTestCorpus.Packets())
            PhoneSaberParserTestCorpus.Compare(parser, packet);
        int failures = 0;
        foreach (var row in File.ReadAllLines(args[0])) {
            var fields = row.Split('\t');
            if (fields[0] == "id") continue;
            bool parsed = parser.TryParse(Encoding.ASCII.GetBytes(fields[1]), out bool stick,
                out float a, out float b, out float c, out float d, out double? epoch);
            bool usable = parsed && PhoneSaberPacketParser.IsUsableCoordinates(stick, a, b, c, d);
            double? expectedEpoch = fields[4] == "-" ? (double?)null :
                double.Parse(fields[4], CultureInfo.InvariantCulture);
            if (usable != (fields[2] == "1") || epoch != expectedEpoch) {
                Console.WriteLine("FAIL " + fields[0] + " usable=" + usable + " epoch=" + epoch);
                failures++;
            }
        }
        return failures == 0 ? 0 : 1;
    }
}
'''
        with tempfile.TemporaryDirectory(prefix="phonesaber-contract-mono-") as temporary:
            build = Path(temporary)
            source = build / "Check.cs"
            source.write_text(harness)
            binary = build / "check.exe"
            self.run_checked([str(MONO / "bin/mono"), str(MONO / "lib/mono/4.5/csc.exe"),
                              "/nologo", "/langversion:9.0", "/define:PHONESABER_ALLOC_BENCH",
                              f"/out:{binary}", str(source),
                              str(ROOT / "Assets/Tests/Editor/PhoneSaberPacketParserTests.cs"),
                              *(str(INPUT / name) for name in ("PhoneSaberPacketParser.cs",
                                  "PhoneSaberInputStats.cs", "PhoneSaberEventLog.cs"))])
            self.run_checked([str(MONO / "bin/mono"), str(binary), str(VECTORS)])

    def run_checked(self, command):
        env = dict(os.environ, CLANG_MODULE_CACHE_PATH="/private/tmp/phonesaber-contract-clang")
        result = subprocess.run(command, capture_output=True, text=True, timeout=120, env=env)
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main()
