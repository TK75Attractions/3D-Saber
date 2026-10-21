#!/usr/bin/env python3
"""AutoClipの実selected時系列を実C#揺れ止め/フィルタで再生（画像は開かず複写しない）。

時刻は撮影時刻、写像は未校正11×6。受信jitter・欠測補間・予測・採点は再現しない。
lagは動き方向へのフィルタ誤差/実測速度。物理的な遅延測定や認識精度ではない。
"""
import argparse
import json
import math
import os
from pathlib import Path
import statistics
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
INPUT = ROOT / "Assets/Scripts/Managers/Inputsystem/Input"


def method(source, name):
    start = source.index("    public void " + name + "(")
    opening = source.index("{", start)
    end, depth = opening + 1, 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end]


PROGRAM = r'''
using System;
using System.IO;
using System.Globalization;
using UnityEngine;
class Program {
    static void Main(string[] args) {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        var tests = new ReplayTests();
        foreach(int mode in new[]{1,2,3,4}) tests.ConstantInputAndSwapsRemainConstant(mode);
        foreach(int mode in new[]{1,2,4}) tests.StepResponseIsMonotonicAndBounded(mode);
        foreach(int fps in new[]{30,60,120}) tests.ResponsiveHalvesConstantSwingLagAndResetsOnSwitch(fps);
        Console.Error.WriteLine("Actual Editor filter methods / Unity Vector2 / NUnit: 10 PASS");
        var stabilizer = new PhoneSaberBladeStabilizer();
        var medium = new PhoneSaberEndpointFilter();
        var responsive = new PhoneSaberEndpointFilter();
        Vector2 previous = default;
        double previousTime = 0;
        bool has = false;
        foreach(var line in File.ReadLines(args[0])) {
            var row = line.Split(',');
            if(row[0] == "reset") { stabilizer.Reset(); medium.Reset(); responsive.Reset(); has=false; continue; }
            double t=double.Parse(row[0]);
            var a=new Vector2(float.Parse(row[1]),float.Parse(row[2]));
            var b=new Vector2(float.Parse(row[3]),float.Parse(row[4]));
            stabilizer.Apply(t,a,b,out a,out b);
            medium.Apply(t,a,b,2,out var ma,out var mb);
            responsive.Apply(t,a,b,4,out var ra,out var rb);
            var mid=(a+b)*.5f;
            var velocity=has && t>previousTime && t-previousTime<=.1 ? (mid-previous)/(float)(t-previousTime) : Vector2.zero;
            double ml=double.NaN, rl=double.NaN;
            if(velocity.sqrMagnitude>1) {
                ml=Vector2.Dot(mid-(ma+mb)*.5f,velocity)/velocity.sqrMagnitude*1000;
                rl=Vector2.Dot(mid-(ra+rb)*.5f,velocity)/velocity.sqrMagnitude*1000;
            }
            Console.WriteLine($"{stabilizer.RejectedJumps},{stabilizer.LimitedLengths},{ml:R},{rl:R}");
            previous=mid; previousTime=t; has=true;
        }
    }
}
'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--clips", type=Path, required=True)
    parser.add_argument("--date", default="20261021")
    args = parser.parse_args()
    editor = Path(os.environ.get("PHONESABER_UNITY_CONTENTS",
                                "/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents"))
    mono = editor / "Resources/Scripting/MonoBleedingEdge"
    managed = editor / "Resources/Scripting/Managed/UnityEngine"
    nunit = editor / "Resources/PackageManager/BuiltInPackages/com.unity.ext.nunit/net40/unity-custom/nunit.framework.dll"
    series, colours, counts = [], [], {}
    for directory in sorted(args.clips.glob(f"*{args.date}*")):
        if not directory.is_dir():
            continue
        data = json.loads((directory / "summary.json").read_text())["autoClip"]
        width, height = data["frameSize"]["width"], data["frameSize"]["height"]
        for colour in ("red", "blue"):
            series.append("reset")
            for frame in data["frames"]:
                points = frame[colour].get("sel")
                state = "selected" if points else "eligible-no-selection" if frame[colour]["e"] else "no-eligible"
                key = colour + "/" + state
                counts[key] = counts.get(key, 0) + 1
                if points:
                    mapped = [(points[i] / (width - 1) - .5) * 11 if i % 2 == 0 else
                              (.5 - points[i] / (height - 1)) * 6 for i in range(4)]
                    series.append(",".join(map(str, (frame["t"], *mapped))))
                    colours.append(colour)
    if not colours:
        raise ValueError("対象時系列なし")
    tests = (ROOT / "Assets/Tests/Editor/PhoneSaberEndpointFilterTests.cs").read_text()
    pure_tests = "using NUnit.Framework; using UnityEngine; class ReplayTests {\n" + "\n".join(
        method(tests, name) for name in ("ConstantInputAndSwapsRemainConstant", "StepResponseIsMonotonicAndBounded",
                                      "ResponsiveHalvesConstantSwingLagAndResetsOnSwitch")) + "\n}"
    settings = '''public static class PhoneSaberPredictionSettings {
        public static bool LoadAutomatic(string s)=>true; public static int Load(string s)=>0; }
        public static class PhoneSaberStation { public static string Normalize(string s)=>s; }'''
    actions = (INPUT / "PhoneSaberControlPanelState.cs").read_text().split("public sealed class PhoneSaberControlPanelState")[0]
    with tempfile.TemporaryDirectory(prefix="phonesaber-freeze-replay-") as directory:
        tmp = Path(directory)
        (tmp / "series.csv").write_text("\n".join(series))
        (tmp / "Program.cs").write_text(PROGRAM)
        (tmp / "Tests.cs").write_text(pure_tests)
        (tmp / "Settings.cs").write_text(settings)
        (tmp / "Actions.cs").write_text(actions)
        (tmp / "Stabilizer.cs").write_text((INPUT / "PhoneSaberBladeStabilizer.cs").read_text().split(
            "public static class PhoneSaberBladeStabilizerSettings")[0])
        subprocess.run([str(mono / "bin/mono"), str(mono / "lib/mono/4.5/csc.exe"), "/nologo", "/langversion:9.0",
                        "/r:" + str(managed / "UnityEngine.CoreModule.dll"), "/r:" + str(nunit),
                        "/r:" + str(mono / "lib/mono/4.5/Facades/netstandard.dll"), "/out:" + str(tmp / "run.exe"),
                        *map(str, tmp.glob("*.cs")), str(INPUT / "PhoneSaberEndpointFilter.cs"),
                        str(INPUT / "PhoneSaberFilterSettings.cs")], check=True)
        result = subprocess.run([str(mono / "bin/mono"), str(tmp / "run.exe"), str(tmp / "series.csv")],
                                env=dict(os.environ, MONO_PATH=os.pathsep.join((str(managed), str(nunit.parent)))),
                                text=True, capture_output=True, check=True)
    print(result.stderr, end="")
    rows = [[float(x) for x in line.split(",")] for line in result.stdout.splitlines()]
    print("selected samples", len(rows), "rejected-jumps", int(rows[-1][0]), "limited-lengths", int(rows[-1][1]))
    print("Recorded output counts (includes unused colours):", json.dumps(counts, sort_keys=True))
    for colour in ("red", "blue", "both"):
        pairs = [(r[2], r[3]) for c, r in zip(colours, rows) if (c == colour or colour == "both") and
                 math.isfinite(r[2]) and math.isfinite(r[3]) and min(r[2:]) >= 0]
        if pairs:
            def summary(index):
                values = sorted(p[index] for p in pairs)
                return f"median={statistics.median(values):.3f} p95={values[math.ceil(.95*len(values))-1]:.3f}ms"
            print(colour, "paired forward-motion samples", len(pairs), "medium", summary(0), "responsive", summary(1))


if __name__ == "__main__":
    main()
