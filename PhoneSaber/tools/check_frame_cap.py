#!/usr/bin/env python3
"""GUIなしで実DisplaySyncの回帰・policy NUnit・Updateのhostコストを測る。"""
import argparse
import os
from pathlib import Path
import re
import subprocess
import tempfile

from check_pc_latency import ROOT, INPUT, HOST, run


UI = r'''
namespace UnityEngine {
    public enum TextAnchor { MiddleCenter }
    public struct Color { public static Color yellow; }
    public struct Rect { public Rect(float x,float y,float w,float h) {} }
    public class GUIStyleState { public Color textColor; }
    public class GUIStyle {
        public TextAnchor alignment; public bool wordWrap; public int fontSize;
        public GUIStyleState normal = new GUIStyleState(); public GUIStyle(GUIStyle other) {}
    }
    public class GUISkin { public GUIStyle box; }
    public static class GUI { public static GUISkin skin; public static void Box(Rect r,string t,GUIStyle s) {} }
}
namespace UnityEditor {
    public struct EditorBuildSettingsScene { public string path; }
    public static class EditorBuildSettings {
        public static EditorBuildSettingsScene[] scenes = { new EditorBuildSettingsScene {path="Assets/Scenes/Game.unity"} };
    }
}
'''

PROGRAM = r'''
using System;
using System.Diagnostics;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
public class FrameCapHost {
    const BindingFlags Private = BindingFlags.Static | BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly MethodInfo Create = typeof(PhoneSaberDisplaySync).GetMethod("CreateAtStartup", Private);
    static readonly MethodInfo Update = typeof(PhoneSaberDisplaySync).GetMethod("Update", Private);
    static Action Start(int cap, int sync = 0) {
        PlayerPrefs.SetInt("PhoneSaber.FrameCap.TEST.v1", cap);
        PlayerPrefs.SetInt("PhoneSaber.DisplaySync.TEST.v1", sync);
        PhoneSaberP2PBridgeProcess.AutoStartEnabled = true;
        Create.Invoke(null, null);
        typeof(PhoneSaberDisplaySync).GetField("notice", Private).SetValue(GameObject.LastSync, null);
        // 注意のテスト用Hzはフレーム上限の実Hzに混ぜない。
        PhoneSaberDisplaySync.RefreshHzOverride = 30;
        return (Action)Delegate.CreateDelegate(typeof(Action), GameObject.LastSync, Update);
    }
    [Test] public void DefaultDoesNotWriteEitherGameSetting() {
        var tick = Start(0);
        foreach (int sync in new[] {0,1,2}) foreach (int target in new[] {-1,30,240}) {
            QualitySettings.vSyncCount=sync; Application.targetFrameRate=target; tick();
            Assert.AreEqual(sync, QualitySettings.vSyncCount); Assert.AreEqual(target, Application.targetFrameRate);
        }
    }
    [Test] public void PersistedFourTimesAppliesAfterEverySceneRewriteWithoutChangingVSync() {
        var tick = Start(1);
        foreach (int sync in new[] {0,1,2}) {
            QualitySettings.vSyncCount=sync; Application.targetFrameRate=-1; tick();
            Assert.AreEqual(240, Application.targetFrameRate); Assert.AreEqual(sync, QualitySettings.vSyncCount);
            Application.targetFrameRate=30; tick(); Assert.AreEqual(240, Application.targetFrameRate);
        }
        Screen.currentResolution = new Resolution {refreshRateRatio=new Rate {value=144}};
        tick(); Assert.AreEqual(576, Application.targetFrameRate);
        Screen.currentResolution = new Resolution {refreshRateRatio=new Rate {value=60}};
    }
    [Test] public void TwoTimesAndExplicitVSyncRemainIndependent() {
        var tick = Start(2,1); QualitySettings.vSyncCount=0; Application.targetFrameRate=-1; tick();
        Assert.AreEqual(1, QualitySettings.vSyncCount); Assert.AreEqual(120, Application.targetFrameRate);
    }
#if AFTER
    [Test] public void F8ChoicePersistsAndDefaultStopsWritingUntilGameRestoresIt() {
        var tick=Start(0); Application.targetFrameRate=-1; QualitySettings.vSyncCount=2;
        PhoneSaberDisplaySync.SetFrameCapMode(PhoneSaberFrameCapSettings.FourTimes); tick();
        Assert.AreEqual(240,Application.targetFrameRate);
        Assert.AreEqual(1,PlayerPrefs.GetInt("PhoneSaber.FrameCap.TEST.v1",0));
        PhoneSaberDisplaySync.SetFrameCapMode(PhoneSaberFrameCapSettings.TwoTimes); tick();
        Assert.AreEqual(120,Application.targetFrameRate);
        Create.Invoke(null,null); tick=(Action)Delegate.CreateDelegate(typeof(Action),GameObject.LastSync,Update);
        Application.targetFrameRate=-1; tick(); Assert.AreEqual(120,Application.targetFrameRate);
        PhoneSaberDisplaySync.SetFrameCapMode(PhoneSaberFrameCapSettings.GameDefault); tick();
        Assert.AreEqual(120,Application.targetFrameRate);
        Application.targetFrameRate=-1; tick(); Assert.AreEqual(-1,Application.targetFrameRate);
        Assert.AreEqual(2,QualitySettings.vSyncCount);
    }
#endif
    static int passed, failed;
    static void Check(object tests, MethodInfo method, object[] args) {
        try { method.Invoke(tests,args); passed++; }
        catch(Exception e) { failed++; Console.WriteLine("FAIL "+method.Name+" "+(e.InnerException??e).Message); }
    }
    static void Tests(Type type) {
        object tests=Activator.CreateInstance(type);
        foreach(var method in type.GetMethods()) {
            foreach(TestCaseAttribute test in method.GetCustomAttributes(typeof(TestCaseAttribute),false))
                Check(tests,method,test.Arguments);
            if(Attribute.IsDefined(method,typeof(TestAttribute)) && method.Name!="GameSceneNameMatchesTheBuildSettings")
                Check(tests,method,null);
        }
    }
    static void Bench(int mode) {
        var tick=Start(mode);
        const int frames=1000000, rounds=15;
        for(int i=0;i<20000;i++) tick();
        var ns=new double[rounds]; var bytes=new double[rounds];
        for(int round=0;round<rounds;round++) {
            GC.Collect(); long allocated=GC.GetAllocatedBytesForCurrentThread(), start=Stopwatch.GetTimestamp();
            for(int i=0;i<frames;i++) {
                // 4096フレームごとにシーン側が書き換える状態機械。同じ入力列を全runで使う。
                if((i&4095)==0) { Application.targetFrameRate=(i&4096)==0?-1:30; QualitySettings.vSyncCount=0; }
                Time.realtimeSinceStartupAsDouble=i/1000.0; tick();
            }
            ns[round]=(Stopwatch.GetTimestamp()-start)*1e9/Stopwatch.Frequency/frames;
            bytes[round]=(GC.GetAllocatedBytesForCurrentThread()-allocated)/(double)frames;
        }
        Array.Sort(ns); Array.Sort(bytes);
        Console.WriteLine($"Update host mode={mode}: median-ns={ns[7]:F2} p95-ns={ns[14]:F2} bytes/frame={bytes[7]:F2}");
    }
    public static int Main() {
        Tests(typeof(FrameCapHost));
#if AFTER
        Tests(typeof(PhoneSaberDisplaySyncTests));
#endif
        Console.WriteLine($"NUnit host {passed} PASS / {failed} FAIL (Unity native/UI/prefs/rendering substituted; build-settings test omitted)");
        Console.WriteLine("benchmark: 20000 warmup; 15 x 1000000 frames; scene-reset/4096; game target=-1/30; real-hz=60; notice=null");
        Bench(0); Bench(1); Bench(2);
        return failed==0?0:1;
    }
}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--before-ref', help='旧DisplaySyncの同一host回帰FAILとbefore計測も確認')
    args = parser.parse_args()
    editor = Path(os.environ.get('PHONESABER_UNITY_CONTENTS', '/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents'))
    mono = editor / 'Resources/Scripting/MonoBleedingEdge'
    nunit = editor / 'Resources/PackageManager/BuiltInPackages/com.unity.ext.nunit/net40/unity-custom/nunit.framework.dll'
    compiler = [str(mono / 'bin/mono'), str(mono / 'lib/mono/4.5/csc.exe'), '/nologo', '/langversion:9.0']
    source = INPUT / 'PhoneSaberDisplaySync.cs'
    with tempfile.TemporaryDirectory(prefix='phonesaber-frame-cap-') as tmp:
        build = Path(tmp)
        host = build / 'Host.cs'
        stub = (HOST / 'Host.cs').read_text().replace('public class MonoBehaviour {', 'public class MonoBehaviour { public bool enabled;')
        stub = stub.replace('public class GameObject {', 'public class GameObject { public static PhoneSaberDisplaySync LastSync;')
        stub = stub.replace('public T AddComponent<T>() where T:new() => new T();',
                            'public T AddComponent<T>() where T:new() { var c=new T(); if(c is PhoneSaberDisplaySync s) LastSync=s; return c; }')
        stub = stub.replace('public static class Mathf {', 'public static class Mathf { public static int Max(int a,int b) => Math.Max(a,b);')
        # このhostは座標/予測を使わず、AFTERのPerfLog依存stubは不要。
        start = stub.index('#if AFTER')
        end = stub.index('#endif', start) + len('#endif')
        stub = stub[:start] + stub[end:]
        host.write_text(stub + UI)
        program = build / 'Program.cs'
        program.write_text(PROGRAM)
        sources = [('after', source)]
        if args.before_ref:
            before = build / 'Before.cs'
            before.write_bytes(subprocess.check_output(['git', 'show', args.before_ref + ':' + str(source.relative_to(ROOT))], cwd=ROOT))
            sources.insert(0, ('before', before))
        for label, path in sources:
            exe = build / (label + '.exe')
            inputs = [host, program, path, INPUT / 'PhoneSaberInputStats.cs']
            flags = []
            if label == 'after':
                inputs += [ROOT / 'Assets/Tests/Editor/PhoneSaberDisplaySyncTests.cs']
                flags = ['/define:AFTER']
            if run(compiler + flags + ['/r:' + str(nunit), '/out:' + str(exe)] + list(map(str, inputs))):
                return 1
            print(label, flush=True)
            command = [str(mono / 'bin/mono'), str(exe)]
            env = dict(os.environ, MONO_PATH=str(nunit.parent))
            if label == 'before':
                result = subprocess.run(command, env=env, text=True, capture_output=True)
                print(result.stdout, end='')
                print(result.stderr, end='')
                # exit=1 だけでは crash や既定動作の破壊まで「回帰再現」と誤認する。
                failures = re.findall(r'^FAIL (\w+)', result.stdout, re.MULTILINE)
                summaries = re.findall(r'^NUnit host (\d+) PASS / (\d+) FAIL', result.stdout, re.MULTILINE)
                if sorted(failures) != ['PersistedFourTimesAppliesAfterEverySceneRewriteWithoutChangingVSync',
                                'TwoTimesAndExplicitVSyncRemainIndependent'] or summaries != [('1', '2')] or result.stderr.strip():
                    print('ERROR: expected only the two frame-cap regressions')
                    return 1
                code = result.returncode
            else:
                code = run(command, env=env)
            if code != (1 if label == 'before' else 0):
                print('ERROR: unexpected regression result ' + label)
                return 1
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
