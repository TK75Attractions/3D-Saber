#!/usr/bin/env python3
"""Compile the actual predictor and benchmark actual old/new timestamp stripping.

This isolated .NET harness uses a tiny Vector2 stub, not Unity. It does not open
sockets or measure glass-to-receive latency. Generated files stay in /tmp.
"""

import argparse
import os
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
INPUT = "Assets/Scripts/Managers/Inputsystem/Input/InputPoint.cs"
PREDICTOR = "Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberEndpointPredictor.cs"


def method(source, name):
    start = source.index("    static string StripOptionalTimestamp(")
    brace = source.index("{", start)
    depth = 1
    end = brace + 1
    while depth:
        depth += (source[end] == "{") - (source[end] == "}")
        end += 1
    return source[start:end].replace("StripOptionalTimestamp", name)


VECTOR = """
namespace UnityEngine {
    public struct Vector2 {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public static Vector2 zero => new Vector2(0, 0);
        public static Vector2 operator +(Vector2 a, Vector2 b) => new Vector2(a.x+b.x, a.y+b.y);
        public static Vector2 operator -(Vector2 a, Vector2 b) => new Vector2(a.x-b.x, a.y-b.y);
        public static Vector2 operator *(Vector2 a, float b) => new Vector2(a.x*b, a.y*b);
        public static Vector2 operator /(Vector2 a, float b) => new Vector2(a.x/b, a.y/b);
    }
}
"""

PROGRAM = """
using System;
using System.Diagnostics;
using UnityEngine;
class Program {
    static void Near(double expected, double actual) {
        if (Math.Abs(expected-actual)>1e-5) throw new Exception($"expected={expected} actual={actual}");
    }
    static void Smoke() {
        var p = new PhoneSaberEndpointPredictor();
        p.AddSample(0, new Vector2(0,0), new Vector2(1,1));
        p.AddSample(0.02, new Vector2(0.02f,0), new Vector2(1,1.02f));
        p.AddSample(0.04, new Vector2(0.04f,0), new Vector2(1,1.04f));
        p.Predict(0.04,40,out var a,out var b); Near(0.08,a.x); Near(1.08,b.y);
        p.Predict(0.04,0,out a,out b); Near(0.04,a.x); Near(1.04,b.y);
        p.Predict(0.04+(1.0/30+0.1)/2,60,out a,out b); Near(0.07,a.x);
        p.Predict(0.141,60,out a,out b); Near(0.04,a.x);
        p.AddSample(0.141,new Vector2(1,0),new Vector2(1,1));
        p.Predict(0.141,60,out a,out b); Near(1,a.x);
        p.AddSample(0.161,new Vector2(1.02f,0),new Vector2(1,1));
        p.AddSample(0.181,new Vector2(1.01f,0),new Vector2(1,1));
        p.Predict(0.181,60,out a,out b); Near(1.01,a.x);
        p.Reset(); p.AddSample(0,new Vector2(0,0),new Vector2(0,0));
        p.AddSample(0.02,new Vector2(3,4),new Vector2(-4,3));
        p.Predict(0.02,60,out a,out b); Near(3.21,a.x); Near(4.28,a.y);
        float negativeZero=BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        p.Reset(); p.AddSample(1,new Vector2(negativeZero,0),new Vector2(1,1));
        p.Predict(1,0,out a,out b);
        if (BitConverter.SingleToInt32Bits(a.x)!=unchecked((int)0x80000000)) throw new Exception("OFF bits");
        Console.WriteLine("Actual predictor Roslyn/stub smoke: PASS (constant, OFF bits, clamp, gap, reversal, decay)");
    }
    static readonly string[] Messages = {
        "1,2", "1,2,3,4", "ts=123.25;1,2,3,4", "timestamp=123.25;1,2",
        "ts=;1,2", "timestamp=", "ts=123", "timestamp=;1,2", "ts=1;;1,2"
    };
    static int sink;
    static void Bench(string name, Func<string,string> strip) {
        const int count=1000000;
        for(int i=0;i<20000;i++) sink+=strip(Messages[i%Messages.Length]).Length;
        var elapsed=new double[7]; var allocated=new double[7];
        for(int round=0;round<7;round++) {
            GC.Collect();
            long bytes=GC.GetAllocatedBytesForCurrentThread();
            long start=Stopwatch.GetTimestamp();
            for(int i=0;i<count;i++) sink+=strip(Messages[i%Messages.Length]).Length;
            elapsed[round]=(Stopwatch.GetTimestamp()-start)*1e9/Stopwatch.Frequency/count;
            allocated[round]=(GC.GetAllocatedBytesForCurrentThread()-bytes)/(double)count;
        }
        Array.Sort(elapsed); Array.Sort(allocated);
        Console.WriteLine($"{name}: median_ns_per_packet={elapsed[3]:F3} allocated_bytes_per_packet={allocated[3]:F3}");
    }
    static void Main() {
        Smoke();
        foreach(var message in Messages) if(Before(message)!=After(message)) throw new Exception("prefix parity");
        string[] prefixes={"", "ts=", "timestamp=", "other=", "TS="};
        string[] bodies={"", "0", "1,2", "1,2,3,4", ";", ";1,2", "123;1,2", "1;;3,4"};
        int cases=0;
        foreach(var prefix in prefixes) foreach(var body in bodies) {
            string message=prefix+body;
            if(Before(message)!=After(message)) throw new Exception("prefix parity: "+message);
            cases++;
        }
        Console.WriteLine($"Timestamp helper old/new exact string parity: PASS ({cases+Messages.Length} cases)");
        Bench("before",Before); Bench("after",After);
        GC.KeepAlive(sink);
    }
METHODS
}
"""


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before-ref", required=True, help="Git ref before receive-path tightening")
    parser.add_argument("--dotnet", default="dotnet")
    args = parser.parse_args()
    before = subprocess.check_output(["git", "show", f"{args.before_ref}:{INPUT}"], cwd=ROOT, text=True)
    after = (ROOT / INPUT).read_text()
    sdk = subprocess.check_output([args.dotnet, "--version"], text=True).strip()
    major = int(sdk.split(".")[0])
    with tempfile.TemporaryDirectory(prefix="unity-predict-", dir="/tmp") as tmp:
        folder = Path(tmp)
        (folder / "Harness.csproj").write_text(
            f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            f'<TargetFramework>net{major}.0</TargetFramework><EnableNETAnalyzers>false</EnableNETAnalyzers>'
            '<UseSharedCompilation>false</UseSharedCompilation></PropertyGroup></Project>')
        (folder / "NuGet.Config").write_text('<configuration><packageSources><clear /></packageSources></configuration>')
        (folder / "Predictor.cs").write_text((ROOT / PREDICTOR).read_text())
        (folder / "Vector.cs").write_text(VECTOR)
        (folder / "Program.cs").write_text(PROGRAM.replace("METHODS", method(before, "Before") + method(after, "After")))
        env = dict(os.environ, DOTNET_CLI_HOME=str(folder / "cli-home"),
                   DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", DOTNET_CLI_TELEMETRY_OPTOUT="1",
                   NUGET_PACKAGES=str(folder / "packages"), DOTNET_TieredCompilation="0")
        built = subprocess.run([args.dotnet, "build", "-c", "Release", "--nologo", "-v", "quiet"],
                               cwd=folder, env=env, capture_output=True, text=True)
        if built.returncode:
            raise RuntimeError(built.stdout + built.stderr)
        print(f"SDK={sdk}; before={args.before_ref}; tiered compilation=OFF; 7x1,000,000 iterations", flush=True)
        subprocess.run([args.dotnet, str(folder / "bin" / "Release" / f"net{major}.0" / "Harness.dll")],
                       cwd=folder, env=env, check=True)


if __name__ == "__main__":
    main()
