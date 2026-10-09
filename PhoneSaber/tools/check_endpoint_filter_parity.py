#!/usr/bin/env python3
"""Actual C#9 filter / Python parity and CPU cost; does not run Unity or sockets."""
import argparse
import csv
import math
import os
from pathlib import Path
import subprocess
import tempfile

from benchmark_unity_prediction import ROOT, VECTOR
from endpoint_prediction_eval import f32, float_bits
from endpoint_filter_eval import EndpointFilter, prepare

FILTER = 'Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberEndpointFilter.cs'
SETTINGS = 'Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberFilterSettings.cs'
VECTORS = Path(__file__).with_name('fixtures') / 'endpoint_filter_vectors.csv'
PRESETS = {1: (1, 10, 10), 2: (.25, 7, 60)}
FIELDS = ['time', 'x1', 'y1', 'x2', 'y2', 'mode', 'bits1', 'bits2', 'bits3', 'bits4']


class PresetFilter:
    def __init__(self):
        self.mode, self.filter = 0, None

    def apply(self, timestamp, points, mode):
        if mode not in PRESETS:
            self.mode, self.filter = 0, None
            return points
        if self.mode != mode:
            self.mode, self.filter = mode, EndpointFilter(*PRESETS[mode])
        return self.filter.apply(timestamp, points)


def generate(path):
    filt = PresetFilter()
    rows, time = [], 0
    for mode in (0, 1, 2, 1, 0):
        for i in range(48):
            time += .2 if i == 17 else (0 if i == 23 else (.02, .03, .04)[i % 3])
            points = tuple(f32(v) for v in (math.sin(time * 3) - .5, .2 * math.cos(time * 2),
                                            math.sin(time * 3) + .5, -.2 * math.cos(time * 2)))
            if i % 3 == 1:
                points = points[2:] + points[:2]
            if mode == 0 and i == 0:
                points = (-0.0, 1.2345670461654663, 4.5, -2.75)
            expected = filt.apply(time, points, mode)
            rows.append([time, *points, mode, *[f'{float_bits(v):08x}' for v in expected]])
    with path.open('w', newline='') as stream:
        writer = csv.writer(stream)
        writer.writerow(FIELDS)
        writer.writerows(rows)


def check_python(path):
    filt, count = PresetFilter(), 0
    for r in csv.DictReader(path.open()):
        actual = filt.apply(float(r['time']), tuple(float(r[k]) for k in FIELDS[1:5]), int(r['mode']))
        assert [f'{float_bits(v):08x}' for v in actual] == [r[k] for k in FIELDS[6:]], r
        count += 1
    return count


PROGRAM = r'''
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using UnityEngine;
class Program {
    static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    static void Main(string[] args) {
        var filter = new PhoneSaberEndpointFilter(); int count=0;
        foreach(var line in File.ReadAllLines(args[0])) {
            var r=line.Split(','); if(r[0]=="time") continue;
            Func<int,double> n=i=>double.Parse(r[i],CultureInfo.InvariantCulture);
            filter.Apply(n(0),new Vector2((float)n(1),(float)n(2)),new Vector2((float)n(3),(float)n(4)),(int)n(5),out var a,out var b);
            float[] actual={a.x,a.y,b.x,b.y};
            for(int i=0;i<4;i++) Require(unchecked((uint)BitConverter.SingleToInt32Bits(actual[i]))==Convert.ToUInt32(r[i+6],16),$"vector {count} axis {i}");
            count++;
        }
        Console.WriteLine($"C#9 / Python bit parity PASS: {count} samples, {count*4} float components");
        foreach(int mode in new[]{1,2}) {
            filter.Reset(); var first=new Vector2(-1,.25f); var second=new Vector2(1,.5f);
            for(int i=0;i<120;i++) {
                filter.Apply(i/30.0,i%2==0?first:second,i%2==0?second:first,mode,out var a,out var b);
                Require(a.x==first.x&&a.y==first.y&&b.x==second.x&&b.y==second.y,"constant/swap");
            }
            filter.Reset();filter.Apply(0,first,second,mode,out var pa,out var pb);
            for(int i=1;i<90;i++) {
                filter.Apply(i/30.0,new Vector2(0,1),new Vector2(2,1.5f),mode,out var a,out var b);
                Require(a.x>=pa.x&&a.x<=0&&a.y>=pa.y&&a.y<=1&&b.x>=pb.x&&b.x<=2&&b.y>=pb.y&&b.y<=1.5f,"step bounds");
                pa=a;pb=b;
            }
            Console.WriteLine($"step final max residual mode={mode}: {Math.Max(Math.Max(Math.Abs(pa.x),Math.Abs(pa.y-1)),Math.Max(Math.Abs(pb.x-2),Math.Abs(pb.y-1.5))):g9}");
            filter.Apply(4,new Vector2(3,2),new Vector2(4,2),mode,out var gapA,out var gapB);
            Require(gapA.x==3&&gapB.x==4,"gap");
            filter.Apply(4.03,new Vector2(float.NaN,0),new Vector2(0,0),mode,out _,out _);
            filter.Apply(4.04,new Vector2(5,0),new Vector2(6,0),mode,out var invalidA,out var invalidB);
            Require(invalidA.x==5&&invalidB.x==6,"invalid resets");
        }
        Require(PhoneSaberFilterSettings.Load("stationA")==0,"default OFF");
        PhoneSaberFilterSettings.Save("stationA",1);
        Require(PhoneSaberFilterSettings.Load("stationA")==1&&PhoneSaberFilterSettings.Load("stationB")==0,"station isolation");
        PhoneSaberFilterSettings.Save("stationB",99);Require(PhoneSaberFilterSettings.Load("stationB")==2,"clamp");
        Console.WriteLine("C#9 smoke PASS: constant, swap, step bounds, gap, invalid reset, settings (PlayerPrefs stub)");
        Bench(-1);Bench(0);Bench(1);Bench(2);
    }
    static double sink;
    static void Bench(int mode) {
        const int count=300000;
        var filter=new PhoneSaberEndpointFilter();
        var times=new double[7];var allocations=new double[7];
        for(int round=-1;round<7;round++) {
            filter.Reset();GC.Collect();
            long allocated=GC.GetAllocatedBytesForCurrentThread(), start=Stopwatch.GetTimestamp();
            for(int i=0;i<count;i++) {
                var a=new Vector2((i%257)*.001f,0);var b=new Vector2(a.x+1,0);
                if(mode>=0) filter.Apply(i/30.0,a,b,mode,out a,out b);
                sink+=a.x+b.x;
            }
            if(round>=0) {
                times[round]=(Stopwatch.GetTimestamp()-start)*1e9/Stopwatch.Frequency/count;
                allocations[round]=(GC.GetAllocatedBytesForCurrentThread()-allocated)/(double)count;
            }
        }
        Array.Sort(times);Array.Sort(allocations);
        Console.WriteLine($"mode={mode} median_ns_per_sample={times[3]:F3} bytes_per_sample={allocations[3]:F3}");
        GC.KeepAlive(sink);
    }
}
'''

STUB = r'''
namespace UnityEngine {
    public static class Mathf { public static int Clamp(int x,int a,int b)=>System.Math.Max(a,System.Math.Min(b,x)); }
    public static class PlayerPrefs {
        static readonly System.Collections.Generic.Dictionary<string,int> ints=new System.Collections.Generic.Dictionary<string,int>();
        public static int GetInt(string k,int fallback)=>ints.TryGetValue(k,out int v)?v:fallback;
        public static void SetInt(string k,int v)=>ints[k]=v;
        public static void Save() {}
        public static string GetString(string k,string fallback)=>fallback;
    }
}
'''


def check_csharp(path, dotnet):
    major = int(subprocess.check_output([dotnet, '--version'], text=True).split('.')[0])
    with tempfile.TemporaryDirectory(prefix='jitter-filter-csharp-', dir='/tmp') as tmp:
        folder = Path(tmp)
        (folder / 'Harness.csproj').write_text(f'<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net{major}.0</TargetFramework><LangVersion>9.0</LangVersion><EnableNETAnalyzers>false</EnableNETAnalyzers><UseSharedCompilation>false</UseSharedCompilation></PropertyGroup></Project>')
        (folder / 'NuGet.Config').write_text('<configuration><packageSources><clear /></packageSources></configuration>')
        (folder / 'Filter.cs').write_text((ROOT / FILTER).read_text())
        (folder / 'Settings.cs').write_text((ROOT / SETTINGS).read_text())
        station = (ROOT / 'Assets/Scripts/Managers/Inputsystem/Input/PhoneSaberDiscoveryResponder.cs').read_text().split('public static class PhoneSaberStation', 1)[1]
        (folder / 'Station.cs').write_text('using System; using System.Text; using UnityEngine; public static class PhoneSaberStation' + station)
        (folder / 'Stub.cs').write_text(VECTOR + STUB)
        (folder / 'Program.cs').write_text(PROGRAM)
        env = dict(os.environ, DOTNET_CLI_HOME=str(folder / 'cli-home'), DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
                   DOTNET_CLI_TELEMETRY_OPTOUT='1', NUGET_PACKAGES=str(folder / 'packages'), DOTNET_TieredCompilation='0')
        result = subprocess.run([dotnet, 'build', '-c', 'Release', '--nologo', '-v', 'quiet'], cwd=folder, env=env, capture_output=True, text=True)
        if result.returncode:
            raise RuntimeError(result.stdout + result.stderr)
        return subprocess.check_output([dotnet, str(folder / 'bin/Release' / f'net{major}.0/Harness.dll'), str(path.resolve())], cwd=folder, env=env, text=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--vectors', type=Path, default=VECTORS)
    parser.add_argument('--generate', action='store_true', help='explicitly regenerate synthetic frozen vectors')
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--real', type=Path, help='also check actual C# bits for every valid recorded endpoint (both presets)')
    args = parser.parse_args()
    if args.generate:
        generate(args.vectors)
    print(f'Python shared vectors PASS: {check_python(args.vectors)} samples')
    print(check_csharp(args.vectors, args.dotnet), end='')
    if args.real:
        data, _ = prepare(args.real)
        with tempfile.TemporaryDirectory(prefix='jitter-filter-real-vectors-', dir='/tmp') as tmp:
            path = Path(tmp) / 'real-vectors.csv'
            with path.open('w', newline='') as stream:
                writer = csv.writer(stream)
                writer.writerow(FIELDS)
                for mode in PRESETS:
                    for seg in data:
                        filt = PresetFilter()
                        writer.writerow([0, 0, 0, 0, 0, 0, '00000000', '00000000', '00000000', '00000000'])
                        for time, raw in zip(seg['t'], seg['raw']):
                            points = tuple(f32(v) for v in raw)
                            expected = filt.apply(float(time), points, mode)
                            writer.writerow([time, *points, mode, *[f'{float_bits(v):08x}' for v in expected]])
            print('Real data (float32 world inputs / outputs; double state):')
            print(check_csharp(path, args.dotnet), end='')


if __name__ == '__main__':
    main()
