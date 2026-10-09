#!/usr/bin/env python3
"""Check shared vectors in Python and actual C# 9 with a minimal Vector2 stub.

Uses the installed .NET SDK offline; all builds/caches stay in a temporary dir.
This is predictor parity, not a Unity Editor or Mono/IL2CPP integration test.
"""

import argparse
import csv
import os
from pathlib import Path
import subprocess
import tempfile

from benchmark_unity_prediction import ROOT, PREDICTOR, VECTOR
from endpoint_prediction_eval import Predictor, BaselinePredictor, float_bits

VECTORS = Path(__file__).with_name("fixtures") / "endpoint_predictor_vectors.csv"


def check_python(path=VECTORS, baseline=False):
    checks = 0
    with path.open(newline="", encoding="utf-8") as stream:
        for row in csv.DictReader(stream):
            operation = row["op"]
            if operation == "new":
                predictor = (BaselinePredictor if baseline else Predictor)(float(row["time"]))
            elif operation == "reset":
                predictor.reset()
            elif operation == "add":
                predictor.add(float(row["time"]), tuple(float(row[k]) for k in ("x1", "y1", "x2", "y2")))
            elif operation == "predict":
                actual = predictor.predict(float(row["time"]), int(row["horizon"]))
                prefix = "baseline" if baseline else "bits"
                expected = tuple(int(row[f"{prefix}{i}"], 16) for i in range(1, 5))
                if tuple(float_bits(v) for v in actual) != expected:
                    raise AssertionError(f"Python parity mismatch: {row}")
                checks += 1
            else:
                raise ValueError(operation)
    return checks


PROGRAM = r'''
using System;
using System.Globalization;
using System.IO;
using UnityEngine;
class Program {
    static void Main(string[] args) {
        PhoneSaberEndpointPredictor predictor = null;
        int count = 0;
        foreach (var line in File.ReadAllLines(args[0])) {
            var r = line.Split(',');
            Func<int, double> number = i => double.Parse(r[i], CultureInfo.InvariantCulture);
            switch (r[0]) {
                case "op": break;
                case "new": predictor = new PhoneSaberEndpointPredictor((float)number(1)); break;
                case "reset": predictor.Reset(); break;
                case "add":
                    predictor.AddSample(number(1), new Vector2((float)number(2),(float)number(3)), new Vector2((float)number(4),(float)number(5)));
                    break;
                case "predict":
                    predictor.Predict(number(1), (int)number(6), out var a, out var b);
                    var actual = new float[] { a.x, a.y, b.x, b.y };
                    for (int i=0;i<4;i++) {
                        uint bits = unchecked((uint)BitConverter.SingleToInt32Bits(actual[i]));
                        uint expected = Convert.ToUInt32(r[i+int.Parse(args[1])],16);
                        if (bits != expected) throw new Exception($"C# mismatch check {count} axis {i}: {bits:x8} != {expected:x8}");
                    }
                    count++;
                    break;
                default: throw new Exception("unknown vector operation");
            }
        }
        Console.WriteLine($"Actual C# 9 / stub bit parity: PASS ({count} predictions, {count*4} float components)");
    }
}
'''


def check_csharp(path=VECTORS, dotnet="dotnet", before_ref=None):
    sdk = subprocess.check_output([dotnet, "--version"], text=True).strip()
    major = int(sdk.split(".")[0])
    with tempfile.TemporaryDirectory(prefix="predict-real-parity-", dir="/tmp") as tmp:
        folder = Path(tmp)
        (folder / "Harness.csproj").write_text(
            f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            f'<TargetFramework>net{major}.0</TargetFramework><LangVersion>9.0</LangVersion>'
            '<EnableNETAnalyzers>false</EnableNETAnalyzers><UseSharedCompilation>false</UseSharedCompilation>'
            '</PropertyGroup></Project>')
        (folder / "NuGet.Config").write_text('<configuration><packageSources><clear /></packageSources></configuration>')
        source = subprocess.check_output(["git", "show", f"{before_ref}:{PREDICTOR}"], cwd=ROOT, text=True) if before_ref else (ROOT / PREDICTOR).read_text()
        (folder / "Predictor.cs").write_text(source)
        (folder / "Vector.cs").write_text(VECTOR)
        (folder / "Program.cs").write_text(PROGRAM)
        env = dict(os.environ, DOTNET_CLI_HOME=str(folder / "cli-home"),
                   DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", DOTNET_CLI_TELEMETRY_OPTOUT="1",
                   NUGET_PACKAGES=str(folder / "packages"), DOTNET_TieredCompilation="0")
        built = subprocess.run([dotnet, "build", "-c", "Release", "--nologo", "-v", "quiet"],
                               cwd=folder, env=env, capture_output=True, text=True)
        if built.returncode:
            raise RuntimeError(built.stdout + built.stderr)
        result = subprocess.check_output([dotnet, str(folder / "bin" / "Release" / f"net{major}.0" / "Harness.dll"), str(path.resolve()), "11" if before_ref else "7"],
                                         cwd=folder, env=env, text=True)
        return result.strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--vectors", type=Path, default=VECTORS)
    parser.add_argument("--dotnet", default="dotnet")
    parser.add_argument("--before-ref", help="also compile the original predictor and check frozen baseline columns")
    args = parser.parse_args()
    print(f"Python float32 bit parity: PASS ({check_python(args.vectors)} predictions)")
    print(check_csharp(args.vectors, args.dotnet))
    if args.before_ref:
        print(f"Python baseline bit parity: PASS ({check_python(args.vectors, True)} predictions)")
        print(check_csharp(args.vectors, args.dotnet, args.before_ref))


if __name__ == "__main__":
    main()
