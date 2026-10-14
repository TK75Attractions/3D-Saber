#!/usr/bin/env python3
"""実EditModeテストをNUnit/.NETで実行(Unityのnative lifecycleはstub)。"""
import argparse
import os
from pathlib import Path
import subprocess

from benchmark_unity_prediction import ROOT
from hit_feel_replay import CSharpReplay

PROGRAM = r'''
using System;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
public static class HostPaths { public static string dataPath; }
class Program {
    static int Main(string[] args) {
        HostPaths.dataPath = args[0];
        int pass=0, fail=0;
        var instance = new PhoneSaberAdaptivePredictionTests();
        foreach(var method in instance.GetType().GetMethods()) {
            if (!method.IsDefined(typeof(TestAttribute),false)) continue;
#if BEFORE
            if (method.Name == "SharedVectorsMatchStandaloneBits") continue;
#endif
            try { method.Invoke(instance,null); pass++; Console.WriteLine("PASS " + method.Name); }
            catch(TargetInvocationException e) { fail++; Console.WriteLine("FAIL " + method.Name + ": " + e.InnerException.Message); }
        }
#if !BEFORE && !MONO
        PhoneSaberEndpointPredictor p=default;
        for(int i=0;i<100;i++) { p.AddSample(i/60.0,new Vector2(i*.03f,0),new Vector2(i*.03f,1)); p.PredictAdaptive(i/60.0,60,out _,out _); }
        long bytes=GC.GetAllocatedBytesForCurrentThread();
        float sink=0;
        for(int i=100;i<300100;i++) { p.AddSample(i/60.0,new Vector2(i*.03f,0),new Vector2(i*.03f,1)); p.PredictAdaptive(i/60.0+.008,60,out var a,out var b); sink+=a.x+b.y; }
        long allocated=GC.GetAllocatedBytesForCurrentThread()-bytes;
        Console.WriteLine("300000 actual predictor frames allocated bytes=" + allocated);
        if(allocated!=0) fail++;
        GC.KeepAlive(sink);
#endif
        Console.WriteLine($"NUnit standalone {pass} PASS / {fail} FAIL");
        return fail==0?0:1;
    }
}
'''


def check(before=None):
    editor = Path(os.environ.get('PHONESABER_UNITY_CONTENTS', '/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents'))
    nunit = editor / 'Resources/PackageManager/BuiltInPackages/com.unity.ext.nunit/net40/unity-custom/nunit.framework.dll'
    source = (ROOT / 'Assets/Tests/Editor/PhoneSaberEndpointPredictorTests.cs').read_text()
    shared_start = source.index('    [Test]\n    public void SharedOfflineVectorsMatchFloatBits()')
    shared_end = source.index('    static void Near(', shared_start)
    shared = source[shared_start:shared_end]
    source = source[:source.index('public class PhoneSaberEndpointPredictorTests')] + source[source.index('public class PhoneSaberAdaptivePredictionTests'):]
    adaptive_source = source.replace('Application.dataPath', 'HostPaths.dataPath')
    if not before:
        source = source.replace('public class PhoneSaberAdaptivePredictionTests\n{', 'public class PhoneSaberAdaptivePredictionTests\n{\n' + shared)
    if before:
        source = source.replace('PhoneSaberEndpointPredictor.AutomaticMaximumDisplacement', '2f')
    source = source.replace('Application.dataPath', 'HostPaths.dataPath')
    replay = CSharpReplay(before, program=PROGRAM, extra_sources={'Tests.cs': source}, references=(nunit,))
    try:
        result = subprocess.run(['dotnet', str(replay.dll), str(ROOT / 'Assets')], env=replay.env, text=True, capture_output=True)
        print(result.stdout, end='')
        print(result.stderr, end='')
        if result.returncode != (1 if before else 0):
            raise RuntimeError('旧版回帰FAIL/新版PASSの期待と不一致')
        if not before:
            # Unity同梱Monoでも同じ固定ベクトルと純粋EditModeテストを実行する。Editorは起動しない。
            mono = editor / 'Resources/Scripting/MonoBleedingEdge'
            exe = replay.folder / 'mono-tests.exe'
            command = [str(mono / 'bin/mono'), str(mono / 'lib/mono/4.5/csc.exe'),
                       '/nologo', '/langversion:9.0', '/define:MONO', '/r:' + str(nunit), '/out:' + str(exe)]
            subprocess.run(command + [str(p) for p in replay.folder.glob('*.cs')], check=True)
            subprocess.run([str(mono / 'bin/mono'), str(exe), str(ROOT / 'Assets')],
                           env=dict(replay.env, MONO_PATH=str(nunit.parent)), check=True)
            # 同梱Vector2の実演算でもbit一致を確認。native lifecycleは起動しない。
            managed = editor / 'Resources/Scripting/Managed/UnityEngine'
            real_tests = replay.folder / 'Tests-real.cs'
            real_tests.write_text(adaptive_source)
            real_exe = replay.folder / 'unity-vector-tests.exe'
            subprocess.run(command[:-1] + ['/out:' + str(real_exe),
                '/r:' + str(managed / 'UnityEngine.CoreModule.dll'),
                '/r:' + str(mono / 'lib/mono/4.5/Facades/netstandard.dll'),
                str(replay.folder / 'Predictor.cs'), str(replay.folder / 'Program.cs'), str(real_tests)], check=True)
            subprocess.run([str(mono / 'bin/mono'), str(real_exe), str(ROOT / 'Assets')],
                           env=dict(replay.env, MONO_PATH=os.pathsep.join((str(nunit.parent), str(managed)))), check=True)
    finally:
        replay.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before-ref')
    args = parser.parse_args()
    if args.before_ref:
        check(args.before_ref)
    check()


if __name__ == '__main__':
    main()
