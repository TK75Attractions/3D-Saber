#!/usr/bin/env python3
"""実際の状態チェックNUnit全件をUnity同梱Monoで実行（Editor・実機は不要）。"""
import argparse
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
INPUT = ROOT / 'Assets/Scripts/Managers/Inputsystem/Input'

# Unityの保存・ログ・操作は試験対象外。呼ばれた場合は失敗させ、設定変更を見逃さない。
HOST = r'''
using System;
using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;
namespace UnityEngine {
    public static class Debug {
        public static void Log(object value) { throw new InvalidOperationException("Unexpected Unity log"); }
        public static void LogWarning(object value) { throw new InvalidOperationException("Unexpected Unity log"); }
    }
    public static class PlayerPrefs {
        public static string GetString(string key, string fallback) { throw new InvalidOperationException("Unexpected preferences read"); }
        public static void SetString(string key, string value) { throw new InvalidOperationException("Unexpected preferences write"); }
        public static void Save() { throw new InvalidOperationException("Unexpected preferences save"); }
    }
}
public static class PhoneSaberP2PBridgeProcess { public static bool AutoStartEnabled = false; }
public static class PhoneSaberOperatorKeys {
    public static void ResetDefaultsState() { throw new InvalidOperationException("Unexpected defaults command"); }
}
public static class PhoneSaberEventLog {
    public static void Record(string station, string colour, string action, string detail) {
        throw new InvalidOperationException("Unexpected event log");
    }
}
public static class SwingMonotonicClock {
    public static long Timestamp => Stopwatch.GetTimestamp();
    public static double ToSeconds(long ticks) => ticks / (double)Stopwatch.Frequency;
}
class Run {
    static int Main() {
        int passed = 0, failed = 0;
        foreach (var method in typeof(PhoneSaberControlPanelHealthTests).GetMethods()) {
            Action<object[]> run = args => {
                string name = method.Name + "(" + string.Join(",", args) + ")";
                try {
                    method.Invoke(new PhoneSaberControlPanelHealthTests(), args);
                    Console.WriteLine("PASS " + name); passed++;
                } catch (TargetInvocationException error) {
                    Console.WriteLine("FAIL " + name + ": " + error.InnerException); failed++;
                }
            };
            if (method.IsDefined(typeof(TestAttribute), false)) run(new object[0]);
            foreach (TestCaseAttribute test in method.GetCustomAttributes(typeof(TestCaseAttribute), false))
                run(test.Arguments);
        }
        Console.WriteLine($"Control panel health NUnit host: {passed} PASS / {failed} FAIL (Unity native not executed)");
        return failed == 0 && passed > 0 ? 0 : 1;
    }
}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before-ref', help='回帰確認用に状態クラスだけ指定Git版へ差し替える')
    args = parser.parse_args()
    editor = Path(os.environ.get('PHONESABER_UNITY_CONTENTS',
                                '/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents'))
    mono = editor / 'Resources/Scripting/MonoBleedingEdge'
    nunit = editor / 'Resources/PackageManager/BuiltInPackages/com.unity.ext.nunit/net40/unity-custom/nunit.framework.dll'
    with tempfile.TemporaryDirectory(prefix='phonesaber-health-') as temporary:
        folder = Path(temporary)
        host = folder / 'Host.cs'
        host.write_text(HOST, encoding='utf-8')
        state = INPUT / 'PhoneSaberControlPanelState.cs'
        if args.before_ref:
            state = folder / state.name
            state.write_bytes(subprocess.check_output([
                'git', 'show', args.before_ref + ':Assets/Scripts/Managers/Inputsystem/Input/' + state.name], cwd=ROOT))
        binary = folder / 'tests.exe'
        sources = [host, state, INPUT / 'PhoneSaberInputStats.cs', INPUT / 'PhoneSaberLatencyEstimate.cs',
                   INPUT / 'PhoneSaberDiscoveryResponder.cs', ROOT / 'Assets/Tests/Editor/PhoneSaberControlPanelHealthTests.cs']
        subprocess.run([str(mono / 'bin/mono'), str(mono / 'lib/mono/4.5/csc.exe'),
                        '/nologo', '/langversion:9.0', '/r:' + str(nunit), '/out:' + str(binary),
                        *map(str, sources)], cwd=ROOT, check=True, timeout=120)
        return subprocess.run([str(mono / 'bin/mono'), str(binary)], cwd=ROOT, timeout=120,
                              env=dict(os.environ, MONO_PATH=str(nunit.parent))).returncode


if __name__ == '__main__':
    raise SystemExit(main())
