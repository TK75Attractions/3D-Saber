import os
from pathlib import Path
import subprocess
import tempfile
import unittest

import check_pc_latency as check


class FrameRegressionGateTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        editor = Path(os.environ.get('PHONESABER_UNITY_CONTENTS', '/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents'))
        mono = editor / 'Resources/Scripting/MonoBleedingEdge'
        cls.mono = mono / 'bin/mono'
        if not cls.mono.is_file():
            raise unittest.SkipTest('Unity Mono is required')
        nunit = editor / 'Resources/PackageManager/BuiltInPackages/com.unity.ext.nunit/net40/unity-custom/nunit.framework.dll'
        cls.temporary = tempfile.TemporaryDirectory(prefix='phonesaber-frame-gate-')
        cls.addClassCleanup(cls.temporary.cleanup)
        build = Path(cls.temporary.name)
        fixture = build / 'Fixtures.cs'
        # 実 runner に失敗を注入する。件数が同じ別テストの失敗や準備失敗を成功扱いしてはいけない。
        fixture.write_text('''using System;
using NUnit.Framework;
public class InputPoint {}
public class PhoneSaberReceiverStateReadTests {
 public static InputPoint CreateIdleInput() => new InputPoint();
 public static Action BindIdleRead(InputPoint input) => () => {};
 public static Action BindPacket(InputPoint input, bool blue) => () => {};
 public static void DestroyIdleInput(InputPoint input) {}
 public void IdleCoordinatesDoNotWaitForReceiverLock(string field) {
  string failure = Environment.GetEnvironmentVariable("FRAME_GATE_FAILURE");
  if (field != (failure == "blue" ? "lockObj2" : "lockObj")) return;
  if (failure == "exception") throw new InvalidOperationException("fixture crashed");
  if (failure == "setup") Assert.IsTrue(false, "テスト用 thread が lock を取得できない");
  if (failure == "red" || failure == "blue")
   Assert.Less(1500, 500, "新着のない色の受信 lock で main thread を止めない");
 }
 public void IdleCoordinatesAllocateZeroBytes() {}
 public void BothColoursConsumePacketsAndAllocateZeroBytesBetweenPackets() {}
 public void PendingDiagnosticIsDrainedEvenWithoutNewCoordinates(string field) {}
}
public class PhoneSaberPacketStatistics {
 public void Record(double now, string sender, bool parsed) {}
 public void PollSenderGate(double now) {}
}
public class PhoneSaberInputStatsTests {
 public void NoConflictPollingDoesNotWaitForStatisticsWriter() {}
 public void PollingStillExpiresConflictsAfterResetAndRebind() {}
}
public class PhoneSaberSenderGateTests {}
''')
        cls.binary = build / 'frames.exe'
        compiled = subprocess.run([str(cls.mono), str(mono / 'lib/mono/4.5/csc.exe'),
                                   '/nologo', '/langversion:9.0', '/define:PHONESABER_FRAME_HOST', '/r:' + str(nunit),
                                   '/out:' + str(cls.binary), str(check.HOST / 'Host.cs'), str(fixture)],
                                  capture_output=True, text=True, timeout=60)
        if compiled.returncode:
            raise AssertionError(compiled.stdout + compiled.stderr)
        cls.environment = dict(os.environ, MONO_PATH=str(nunit.parent))

    def run_gate(self, failure, expected):
        return subprocess.run([str(self.mono), str(self.binary), str(expected)],
                              env=dict(self.environment, FRAME_GATE_FAILURE=failure),
                              capture_output=True, text=True, timeout=30)

    def test_after_and_exact_before_failure_are_accepted(self):
        for failure, expected in [('', 0), ('red', 1)]:
            with self.subTest(failure=failure):
                result = self.run_gate(failure, expected)
                self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_same_count_wrong_target_is_rejected(self):
        result = self.run_gate('blue', 1)
        self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_unexpected_exception_and_setup_failure_are_rejected(self):
        for failure in ['exception', 'setup']:
            with self.subTest(failure=failure):
                result = self.run_gate(failure, 1)
                self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)


if __name__ == '__main__':
    unittest.main()
