#!/usr/bin/env python3
"""固定シャッターの実parser・formatter・ACK・操作の既存XCTest/NUnitをGUIなしで実行。"""
import os
from pathlib import Path
import subprocess
import sys
import tempfile

ROOT = Path(__file__).resolve().parents[2]
TESTS = ROOT / 'PhoneSaber/ios/PhoneSaberSenderTests'
sys.path.insert(0, str(TESTS))
from run_latency_host_tests import block


def run(command, **kwargs):
    subprocess.run(list(map(str, command)), check=True, cwd=ROOT, **kwargs)


def main():
    ios = ROOT / 'PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender'
    inputs = ROOT / 'Assets/Scripts/Managers/Inputsystem/Input'
    tests = ROOT / 'Assets/Tests/Editor'
    editor = Path('/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents')
    mono = editor / 'Resources/Scripting/MonoBleedingEdge'
    managed = editor / 'Resources/Scripting/Managed/UnityEngine'
    template = editor / 'Resources/PackageManager/ProjectTemplates/libcache/com.unity.template.2d-cross-platform-2d-6.1.2/ScriptAssemblies'
    nunit = editor / 'Resources/PackageManager/BuiltInPackages/com.unity.ext.nunit/net40/unity-custom/nunit.framework.dll'
    developer = Path(subprocess.check_output(['xcode-select', '-p'], text=True).strip()) / 'Platforms/MacOSX.platform/Developer'
    with tempfile.TemporaryDirectory(prefix='phonesaber-shutter-') as name:
        tmp = Path(name)
        (tmp / 'Clock.swift').write_text('import Foundation\nimport CoreMedia\n' + block((ios / 'FrameProcessor.swift').read_text(), 'enum HostMonotonicClock'))
        (tmp / 'main.swift').write_text('''import XCTest
import Darwin
let suite = MetricsDatagramTests.defaultTestSuite
suite.run()
guard let run = suite.testRun, run.executionCount > 0, run.hasSucceeded else { exit(1) }
''')
        run(['xcrun', 'swiftc', '-O', '-D', 'METRICS_STANDALONE',
             *[ios / n for n in ['MetricsDatagram.swift', 'UDPSender.swift', 'P2PSender.swift', 'P2PProtocol.swift']],
             TESTS / 'MetricsDatagramTests.swift', tmp / 'Clock.swift', tmp / 'main.swift',
             '-module-cache-path', tmp / 'cache', '-I', developer / 'usr/lib', '-L', developer / 'usr/lib',
             '-F', developer / 'Library/Frameworks', '-Xlinker', '-rpath', '-Xlinker', developer / 'Library/Frameworks',
             '-Xlinker', '-rpath', '-Xlinker', developer / 'usr/lib', '-o', tmp / 'swift-tests'])
        run([tmp / 'swift-tests'])
        # Unity描画・UDPループ・ログだけ代役。キー/ACK/コマンド生成は実ソースを使う。
        (tmp / 'Host.cs').write_text('''using System; using System.Diagnostics; using System.Net;
public static class PhoneSaberP2PBridgeProcess { public static bool AutoStartEnabled=false; }
public static class PhoneSaberEventLog { public static void Record(string a,string b,string c,string d) {} }
public static class SwingMonotonicClock { public static long Timestamp=>Stopwatch.GetTimestamp(); public static double ToSeconds(long t)=>t/(double)Stopwatch.Frequency; }
public static class PhoneSaberMetrics { public const int MaximumBytes=400; }
public class PhoneSaberMetricsStore { public void Receive(byte[] d,int n,IPAddress a,double t) {} }
''')
        actions = block((inputs / 'PhoneSaberControlPanelState.cs').read_text(), 'public static class PhoneSaberControlPanelActions')
        (tmp / 'Actions.cs').write_text('using System; using System.Collections.Generic;\n' + actions)
        panel = block((tests / 'PhoneSaberControlPanelTests.cs').read_text(), 'public void ShutterButtonsDispatchOnlySupportedValues()')
        (tmp / 'PanelTests.cs').write_text('using System; using System.Collections.Generic; using NUnit.Framework;\npublic class PanelTests { [Test]\n' + panel + '\n}')
        (tmp / 'Run.cs').write_text('''using System; using System.Reflection; using NUnit.Framework;
class Run { static int Main() {
 int passed=0, failed=0;
 foreach(var type in Assembly.GetExecutingAssembly().GetTypes()) foreach(var method in type.GetMethods()) {
  Action<object[]> check = args => { try { method.Invoke(Activator.CreateInstance(type),args); passed++; }
   catch(Exception e) { Console.WriteLine("FAIL "+method.Name+": "+(e.InnerException??e)); failed++; } };
  if(method.IsDefined(typeof(TestAttribute),false)) check(new object[0]);
  foreach(TestCaseAttribute test in method.GetCustomAttributes(typeof(TestCaseAttribute),false)) check(test.Arguments);
 }
 Console.WriteLine("Shutter/operator NUnit host: "+passed+" PASS / "+failed+" FAIL");
 return failed==0 && passed>0 ? 0:1;
} }
''')
        refs = ['/r:' + str(p) for p in managed.glob('*.dll')]
        refs += ['/r:' + str(template / 'Unity.InputSystem.dll'), '/r:' + str(mono / 'lib/mono/4.5/Facades/netstandard.dll'), '/r:' + str(nunit)]
        run([mono / 'bin/mono', mono / 'lib/mono/4.5/csc.exe', '/nologo', '/langversion:9.0', '/out:' + str(tmp / 'tests.exe'), *refs,
             tmp / 'Host.cs', tmp / 'Actions.cs', tmp / 'PanelTests.cs', tmp / 'Run.cs',
             inputs / 'PhoneSaberDiscoveryResponder.cs', inputs / 'PhoneSaberOperatorKeys.cs',
             *[tests / n for n in ['PhoneSaberOperatorKeysTests.cs', 'PhoneSaberAcknowledgementTests.cs', 'PhoneSaberPhoneCommandTests.cs']]])
        env = dict(os.environ, MONO_PATH=os.pathsep.join(map(str, [nunit.parent, managed, template])))
        run([mono / 'bin/mono', tmp / 'tests.exe'], env=env)


if __name__ == '__main__':
    main()
