"""実C#予測器の窓なしoffline build/run。生成物・数値記録はTMPDIRだけに置く。"""
import os
from pathlib import Path
import subprocess
import tempfile

import numpy as np

from benchmark_unity_prediction import ROOT, PREDICTOR, VECTOR

BRIDGE_VECTOR = '''
namespace UnityEngine {
    public struct Vector3 {
        public float x,y,z;
        public Vector3(float x,float y,float z) { this.x=x; this.y=y; this.z=z; }
        public static Vector3 operator +(Vector3 a,Vector3 b)=>new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator *(Vector3 a,float b)=>new Vector3(a.x*b,a.y*b,a.z*b);
    }
    public static class Mathf { public static float Clamp(float x,float lo,float hi)=>System.Math.Max(lo,System.Math.Min(x,hi)); }
}
'''


REPLAY_PROGRAM = r'''
using System;
using System.Globalization;
using System.IO;
using UnityEngine;

// Unityを起動しない実コードの再生。入力はworld XY、Fは描画時刻、Sは受信サンプル。
class Program
{
    static void Main(string[] args)
    {
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        PhoneSaberEndpointPredictor predictor = default;
        int lead = int.Parse(args[1]);
        string mode = args[2];
        double stamp = 0;
        Vector2 receivedA = default, receivedB = default;
        bool pending = false;
        NoteModel note = args.Length > 3 ? new NoteModel(double.Parse(args[3])) : null;
        foreach (string line in File.ReadLines(args[0]))
        {
            string[] row = line.Split(',');
            double t = double.Parse(row[1]);
            if (row[0] == "S")
            {
                stamp = t;
                receivedA = new Vector2(float.Parse(row[2]), float.Parse(row[3]));
                receivedB = new Vector2(float.Parse(row[4]), float.Parse(row[5]));
                pending = true;
            }
            else if (row[0] == "F")
            {
                // InputPointのlatest-only mailboxと同じ。描画間に来た中間packetは追いかけない。
                if (pending) { predictor.AddSample(stamp, receivedA, receivedB); pending = false; }
                Vector2 a, b;
#if BEFORE
                predictor.Predict(t, mode == "off" ? 0 : lead, out a, out b);
#else
                if (mode == "adaptive") predictor.PredictAdaptive(t, lead, out a, out b);
                else predictor.Predict(t, mode == "off" ? 0 : lead, out a, out b);
#endif
                var clamped = BridgeOwnedPath.ClampBladeKeepingLength(new Vector3(a.x,a.y,0),
                    new Vector3(b.x,b.y,0), new Vector2(-5.5f,-3), new Vector2(5.5f,3));
                a = new Vector2(clamped.a.x,clamped.a.y); b = new Vector2(clamped.b.x,clamped.b.y);
                string result = $"{t:R},{a.x:R},{a.y:R},{b.x:R},{b.y:R}";
                if (note != null) result += note.Tick(t, a, b);
                Console.WriteLine(result);
            }
        }
    }
}

// SaberCutJudgeの簡易モデル: 原点の単発・方向なしnoteを縦刃で横に払う。
// 半径.32+.88、速度2、中心の最大step5、pass travel1、受付±.2025秒、判定時計-.12秒。
// Update判定は前回LateUpdateの速度を使う。実ゲームの全判定・IMUを代替しない。
class NoteModel
{
    double previousTime, physicsTime, physicsStep;
    float previous, speed, anchor, sweep, physicsX;
    bool started, pending, hasSweep, finished;
    float stepX;
    double stepTime, crossing = double.NaN;
    public NoteModel(double physicsHz) { physicsStep = 1 / physicsHz; }
    public string Tick(double t, Vector2 a, Vector2 b)
    {
        float x = (a.x + b.x) * .5f;
        bool registered = false;
        double judged = double.NaN;
        // 物理50Hzでは位置を観測するだけ。ゲームの切断はFixedUpdate/triggerを使わない。
        while (physicsTime <= t) { physicsX = previous; physicsTime += physicsStep; }
        bool window = Math.Abs(t - .12 - .4) <= .2025;
        if (x != stepX)
        {
            double span = t - stepTime;
            if (started && stepX * x <= 0 && x != stepX && span <= .05)
                crossing = stepTime + span * (-stepX / (x - stepX)) - .12;
            stepX = x; stepTime = t;
        }
        if (!finished && window)
        {
            float distance = Math.Abs(x);
            if (!pending && speed >= 2)
            {
                bool swept = hasSweep && Math.Abs(x - sweep) <= 5 && Math.Min(x, sweep) <= 1.2f && Math.Max(x, sweep) >= -1.2f;
                if (distance <= 1.2f || swept) { pending = true; anchor = swept ? sweep : x; }
                sweep = x; hasSweep = true;
            }
            if (pending && (distance > 1.2f || (Math.Abs(x - anchor) >= 1 && anchor * (x - anchor) < 0 && x * (x - anchor) > 0)))
            {
                registered = finished = true;
                judged = double.IsNaN(crossing) ? t - .12 : crossing;
            }
        }
        speed = started ? Math.Abs(x - previous) / (float)Math.Max(t - previousTime, .0001) : 0;
        previous = x; previousTime = t; started = true;
        return $",{(registered ? 1 : 0)},{judged:R},{speed:R},{physicsX:R}";
    }
}
'''


def bridge_clamp_source():
    source = (ROOT / 'Assets/Scripts/Saber/SaberInputBridge.cs').read_text()
    start = source.index('    public static (Vector3 a, Vector3 b) ClampBladeKeepingLength(')
    end = source.index('{', start) + 1
    depth = 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return 'using UnityEngine; public static class BridgeOwnedPath {\n' + source[start:end] + '\n}'


class CSharpReplay:
    def __init__(self, before_ref=None, program=None, extra_sources=None, references=(), candidate_maximum_ms=None, source_transform=None):
        if before_ref and candidate_maximum_ms is not None:
            raise ValueError('旧版buildには自動予測の上限候補を適用できません')
        self.modes = ('off', 'current') if before_ref else ('off', 'current', 'adaptive')
        self.temporary = tempfile.TemporaryDirectory(prefix="hit-feel-csharp-")
        self.folder = Path(self.temporary.name)
        major = int(subprocess.check_output(["dotnet", "--version"], text=True).split('.')[0])
        self.env = dict(os.environ, DOTNET_CLI_HOME=str(self.folder / "cli"),
                        DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", DOTNET_CLI_TELEMETRY_OPTOUT="1",
                        NUGET_PACKAGES=str(self.folder / "packages"), DOTNET_TieredCompilation="0")
        (self.folder / "Replay.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>'
            f'<TargetFramework>net{major}.0</TargetFramework><LangVersion>9.0</LangVersion>'
            '<UseSharedCompilation>false</UseSharedCompilation><EnableNETAnalyzers>false</EnableNETAnalyzers>'
            + ('<DefineConstants>BEFORE;PHONESABER_BEFORE</DefineConstants>' if before_ref else '')
            + '</PropertyGroup><ItemGroup>'
            + ''.join(f'<Reference Include="{Path(p).stem}"><HintPath>{p}</HintPath></Reference>' for p in references)
            + '</ItemGroup></Project>')
        (self.folder / "NuGet.Config").write_text('<configuration><packageSources><clear /></packageSources></configuration>')
        source = subprocess.check_output(["git", "show", f"{before_ref}:{PREDICTOR}"], cwd=ROOT, text=True) if before_ref else (ROOT / PREDICTOR).read_text()
        if source_transform is not None:
            source = source_transform(source)
        if candidate_maximum_ms is not None:
            # 上限の採否を実C#で評価するための一時build。repoの実装・手動予測は変更しない。
            source = source.replace('Math.Min(horizonMilliseconds, MaximumHorizonMilliseconds) * .001 +',
                                    f'Math.Min(horizonMilliseconds, {int(candidate_maximum_ms)}) * .001 +')
        (self.folder / "Predictor.cs").write_text(source)
        (self.folder / "Vector.cs").write_text(VECTOR)
        (self.folder / "BridgeVector.cs").write_text(BRIDGE_VECTOR)
        (self.folder / "BridgeClamp.cs").write_text(bridge_clamp_source())
        (self.folder / "Program.cs").write_text(program or REPLAY_PROGRAM)
        for name, content in (extra_sources or {}).items():
            (self.folder / name).write_text(content)
        result = subprocess.run(['dotnet', 'build', '-c', 'Release', '--nologo', '-v', 'quiet'],
                                cwd=self.folder, env=self.env, capture_output=True, text=True)
        if result.returncode:
            self.close()
            raise RuntimeError(result.stdout + result.stderr)
        self.dll = self.folder / 'bin' / 'Release' / f'net{major}.0' / 'Replay.dll'

    def close(self):
        self.temporary.cleanup()

    def run(self, frame_times, packets, lead=60, mode="adaptive", physics_hz=None):
        if mode not in self.modes:
            raise ValueError('このbuildで再生できない方式: ' + mode)
        # 同時刻は受信→描画。同じpacketを各描画でAddしない(Bridgeと同じ履歴)。
        events = [(float(row[0]), 0, row[1:]) for row in packets]
        events += [(float(t), 1, ()) for t in frame_times]
        path = self.folder / 'events.csv'
        path.write_text('\n'.join(
            ','.join(["S" if kind == 0 else "F", repr(t)] + [repr(float(v)) for v in values])
            for t, kind, values in sorted(events, key=lambda item: (item[0], item[1]))) + '\n')
        command = ['dotnet', str(self.dll), str(path), str(lead), mode]
        if physics_hz is not None:
            command.append(str(physics_hz))
        output = subprocess.check_output(command,
                                         cwd=self.folder, env=self.env, text=True)
        return np.array([[float(v) for v in line.split(',')] for line in output.splitlines()])


def world_packets(packets, sensitivity=2.0):
    """InputPointのdirect写像(中点の感度→clamp→同量平行移動→worldScale)。校正OFF。"""
    raw = np.asarray(packets[:, 1:], dtype=np.float32).reshape(-1, 2, 2)
    middle = (raw[:, 0] + raw[:, 1]) * np.float32(.5)
    shift = np.clip(middle * np.float32(sensitivity), -1, 1) - middle
    world = (raw + shift[:, None, :]) * np.array([5.5, 3], dtype=np.float32)
    return np.column_stack((packets[:, 0], world.reshape(-1, 4)))


def synthetic_hits(replay, packet_hz=30, render_hz=900, physics_hz=50, trials=20):
    results = []
    for mode in replay.modes:
        hits, false, delays, jerks, scored = 0, 0, [], [], []
        for trial in range(trials):
            receive = np.arange(trial / trials / packet_hz, 1.1, 1 / packet_hz)
            capture = receive - .112
            random = np.random.default_rng(trial)
            for moving in (True, False):
                # 静止は当たりの端に構えた剣。30/60Hzで世界位置にσ=.08の計測雑音。
                x = np.clip((capture - .4) * 10, -3, 3) if moving else 1.17 + random.normal(0, .08, len(capture))
                samples = np.column_stack((receive, x, np.full(len(x), -.5), x, np.full(len(x), .5)))
                times = np.arange(0, 1, 1 / render_hz)
                output = replay.run(times, samples, 60, mode, physics_hz)
                registered = output[:, 5] == 1
                if moving:
                    hits += int(registered.any())
                    delays.extend((output[registered, 0] - .4) * 1000)
                    scored.extend((output[registered, 6] - .4) * 1000)
                    display_index = np.searchsorted(times, np.arange(0, 1, 1 / 60), side='right') - 1
                    jerk = np.diff(output[display_index, 1], n=3) * 60 ** 3
                    jerks.append(float(np.sqrt(np.mean(jerk ** 2))))
                else:
                    false += int(registered.any())
        results.append(dict(mode=mode, packet_hz=packet_hz, render_hz=render_hz, physics_hz=physics_hz,
                            trials=trials, hit_rate=hits / trials, false_hits=false,
                            registration_ms_median=float(np.median(delays)) if delays else None,
                            registration_ms_p95=float(np.percentile(delays, 95)) if delays else None,
                            judged_error_ms_median=float(np.median(scored)) if scored else None,
                            jerk60_world_s3=float(np.mean(jerks))))
    return results


def main():
    import argparse
    import json
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--packet-hz', type=float, default=30)
    parser.add_argument('--render-hz', type=float, default=900)
    parser.add_argument('--physics-hz', type=float, default=50)
    parser.add_argument('--trials', type=int, default=20)
    args = parser.parse_args()
    if any(not np.isfinite(hz) or hz <= 0 for hz in (args.packet_hz, args.render_hz, args.physics_hz)) or args.trials <= 0:
        parser.error('Hz/trialsは正の有限値')
    replay = CSharpReplay()
    try:
        print(json.dumps(synthetic_hits(replay, args.packet_hz, args.render_hz, args.physics_hz, args.trials), indent=1))
    finally:
        replay.close()


if __name__ == '__main__':
    main()
