"""実Swift formatter → 実PC parserの長さ契約。ソケット・カメラ・Unity起動は不要。"""
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[2]
IOS = ROOT / 'PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender'
TESTS = ROOT / 'PhoneSaber/ios/PhoneSaberSenderTests'
INPUT = ROOT / 'Assets/Scripts/Managers/Inputsystem/Input'
EDITOR = Path(os.environ.get('PHONESABER_UNITY_CONTENTS',
    '/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents'))
MONO = EDITOR / 'Resources/Scripting/MonoBleedingEdge'
sys.path.insert(0, str(TESTS))
from run_latency_host_tests import block


class MetricsLengthTests(unittest.TestCase):
    @unittest.skipUnless(sys.platform == 'darwin' and shutil.which('xcrun') and
                         (MONO / 'bin/mono').is_file(), 'macOS SwiftとUnity Monoが必要')
    def test_worst_case_and_camera_values_parse_on_pc(self):
        developer = Path(subprocess.check_output(['xcode-select', '-p'], text=True).strip())
        developer /= 'Platforms/MacOSX.platform/Developer'
        frameworks = developer / 'Library/Frameworks'
        nunit = EDITOR / 'Resources/PackageManager/BuiltInPackages/com.unity.ext.nunit/net40/unity-custom/nunit.framework.dll'
        with tempfile.TemporaryDirectory(prefix='phonesaber-metrics-length-') as directory:
            tmp = Path(directory)

            def run(command, **kwargs):
                result = subprocess.run(list(map(str, command)), cwd=ROOT, text=True,
                                        capture_output=True, timeout=120, **kwargs)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
                return result.stdout + result.stderr

            (tmp / 'Clock.swift').write_text('import Foundation\nimport CoreMedia\n' +
                block((IOS / 'FrameProcessor.swift').read_text(), 'enum HostMonotonicClock'))
            (tmp / 'main.swift').write_text(r'''import Foundation
import XCTest
import Darwin
let suite = MetricsDatagramTests.defaultTestSuite
suite.run()
guard let result = suite.testRun, result.executionCount > 0, result.hasSucceeded else { exit(1) }
var lines = [try XCTUnwrap(MetricsDatagramTests.worstCaseMetrics.line())]
// CameraViewModelの計測スナップショット相当。appはversion.build、未指定台はnone。
for (station, model) in [("A", "iPhone14,5"), ("B", "iPhone16,2"), ("", "iPhone16,2")] {
    let version = "1.2.3", build = "20261025"
    var m = MetricsDatagram()
    m.station = station.isEmpty ? "none" : station
    m.app = "\(version).\(build)"; m.model = model
    m.fpsMode = 60; m.captureFPS = 59.8; m.redFPS = 59.7; m.blueFPS = 59.6
    m.median = 36.9; m.p95 = 44.1; m.samples = 298
    m.rtt = 3.2; m.rttSamples = 8; m.rttAge = 0.2
    m.thermal = "nominal"; m.battery = 0.82 * 100
    m.controls = MetricsDatagram.controlState(trackLock: true, exposureBias: -2, shutter: 2000, shutterISO: 160)
    lines.append(try XCTUnwrap(m.line()))
}
try lines.joined(separator: "\n").write(toFile: CommandLine.arguments[1], atomically: true, encoding: .ascii)
''')
            run(['xcrun', 'swiftc', '-O', '-D', 'METRICS_STANDALONE',
                 *[IOS / name for name in ['MetricsDatagram.swift', 'UDPSender.swift', 'P2PSender.swift', 'P2PProtocol.swift']],
                 TESTS / 'MetricsDatagramTests.swift', tmp / 'Clock.swift', tmp / 'main.swift',
                 '-module-cache-path', tmp / 'cache', '-I', developer / 'usr/lib',
                 '-L', developer / 'usr/lib', '-F', frameworks,
                 '-Xlinker', '-rpath', '-Xlinker', frameworks,
                 '-Xlinker', '-rpath', '-Xlinker', developer / 'usr/lib', '-o', tmp / 'swift-tests'])
            swift_output = run([tmp / 'swift-tests', tmp / 'lines.txt'])
            self.assertIn('0 failures', swift_output)

            # parserと対応NUnitテストを実ソースからそのまま使用。Unity依存部分は含めない。
            metrics = (INPUT / 'PhoneSaberLatencyEstimate.cs').read_text()
            (tmp / 'Metrics.cs').write_text(metrics[:metrics.index('// 受信 thread')])
            tests = (ROOT / 'Assets/Tests/Editor/PhoneSaberLatencyEstimateTests.cs').read_text()
            worst = tests[tests.index('    public static string WorstCaseLine =>'):
                          tests.index('    [Test] public void SharedContractVectors()')]
            (tmp / 'Check.cs').write_text('using System; using System.IO; using System.Text; '
                'using System.Globalization; using NUnit.Framework;\nclass Check {\n' + worst + r'''
    static void Main(string[] args) {
        new Check().WorstCaseIPhoneMetricsAndByteBoundaryParse();
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        var lines = File.ReadAllLines(args[0]);
        Assert.AreEqual(4, lines.Length);
        Assert.AreEqual(WorstCaseLine, lines[0]);
        for (int i = 0; i < lines.Length; i++) {
            var bytes = Encoding.ASCII.GetBytes(lines[i]);
            Assert.LessOrEqual(bytes.Length, PhoneSaberMetrics.MaximumBytes);
            Assert.IsTrue(PhoneSaberMetrics.TryParse(bytes, bytes.Length, out var m), lines[i]);
            if (i == 0) continue;
            Assert.AreEqual(new[] {"A", "B", "none"}[i - 1], m.Station);
            Assert.AreEqual(i == 1 ? "iPhone14_5" : "iPhone16_2", m.Model);
            Assert.AreEqual("ios", m.Platform); Assert.AreEqual("1.2.3.20261025", m.App);
            Assert.AreEqual("1233", m.Controls); Assert.AreEqual("LAN", m.Route);
            Assert.AreEqual("detection", m.C2sEnd); Assert.AreEqual("nominal", m.Thermal);
            Assert.AreEqual(36.9, m.C2sMedian); Assert.AreEqual(44.1, m.C2sP95); Assert.AreEqual(298, m.C2sCount);
            Assert.AreEqual(3.2, m.Rtt); Assert.AreEqual(8, m.RttCount); Assert.AreEqual(0.2, m.RttAge);
            Assert.AreEqual(59.8, m.CaptureFps); Assert.AreEqual(59.7, m.RedFps); Assert.AreEqual(59.6, m.BlueFps);
            Assert.AreEqual(60, m.FpsMode); Assert.AreEqual(82, m.Battery); Assert.AreEqual(0, m.LowLatencyTest);
        }
        Console.WriteLine("PASS Swift -> PC: worst-case 372 bytes + A/B/none camera values; PC 399/400 boundary");
    }
}
''')
            run([MONO / 'bin/mono', MONO / 'lib/mono/4.5/csc.exe', '/nologo', '/langversion:9.0',
                 '/r:' + str(nunit), '/out:' + str(tmp / 'check.exe'), tmp / 'Metrics.cs', tmp / 'Check.cs'])
            output = run([MONO / 'bin/mono', tmp / 'check.exe', tmp / 'lines.txt'],
                         env=dict(os.environ, MONO_PATH=str(nunit.parent)))
            self.assertIn('PASS Swift -> PC', output)
            print(swift_output.strip())
            print(output.strip())


if __name__ == '__main__':
    unittest.main()
