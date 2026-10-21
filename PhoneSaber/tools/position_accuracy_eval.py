#!/usr/bin/env python3
"""非公開ラベルとAutoClipを評価。実C#を.NETで実行し、結果はGit外に保存する。

クリップには運動の正解ラベルがない。steady/movingは250ms窓の端点変位速度の
下位/上位四分位という固定proxy。移動の差分はノイズだけではない。
capture時刻をreceive時刻と仮定し、11x6 world写像で再生する。
予測の未来出力差は検出系列に対する差であり、物理的精度・通信遅延ではない。
"""
import argparse
import csv
import io
import json
from pathlib import Path
import re
import tempfile

import numpy as np

from benchmark_unity_prediction import ROOT, VECTOR
from check_endpoint_predictor_parity import check_csharp

INPUT = ROOT / 'Assets/Scripts/Managers/Inputsystem/Input'
PRIVATE = Path.home() / 'Library/Application Support/PhoneSaber'


def stats(values):
    values = np.asarray(values, dtype=float)
    if not values.size:
        return dict(n=0, median=None, p90=None, max=None)
    return dict(n=len(values), median=float(np.median(values)),
                p90=float(np.quantile(values, .9, method='inverted_cdf')), max=float(np.max(values)))


def geometry(points):
    points = np.asarray(points).reshape(-1, 2, 2)
    axis = points[:, 1] - points[:, 0]
    return points.mean(axis=1), np.arctan2(axis[:, 1], axis[:, 0]), np.linalg.norm(axis, axis=1)


def angle_delta(a, b):
    return np.abs((a - b + np.pi / 2) % np.pi - np.pi / 2) * 180 / np.pi


def errors(actual, truth):
    actual, truth = np.asarray(actual).reshape(-1, 2, 2), np.asarray(truth).reshape(-1, 2, 2)
    direct = np.linalg.norm(actual - truth, axis=2)
    reverse = np.linalg.norm(actual[:, ::-1] - truth, axis=2)
    distances = np.where((reverse.sum(axis=1) < direct.sum(axis=1))[:, None], reverse, direct)
    am, aa, al = geometry(actual)
    tm, ta, tl = geometry(truth)
    return dict(endpoint_px=stats(distances.ravel()), worst_endpoint_px=stats(distances.max(axis=1)),
                midpoint_px=stats(np.linalg.norm(am - tm, axis=1)), angle_deg=stats(angle_delta(aa, ta)),
                length_px=stats(np.abs(al - tl)), signed_length_px=stats(al - tl))


def flatten(points):
    return [p[k] for p in points for k in ('x', 'y')]


def labelled_report(rows, corpus=False, statuses=True):
    report = {}
    for color in ('red', 'blue'):
        selected = [r for r in rows if r['color'].lower() == color]
        paired = [r for r in selected if isinstance(r['expectedEndpoint' if corpus else 'truth'], list)
                  and r['endpoint' if corpus else 'selected'] is not None]
        actual = [flatten(r['endpoint' if corpus else 'selected']) for r in paired]
        truth = [flatten(r['expectedEndpoint' if corpus else 'truth']) for r in paired]
        report[color] = dict(paired_frames=len(paired), metrics=errors(actual, truth) if paired else None,
                             counts={key: sum(r.get('status') == key for r in selected)
                                     for key in ('PASS', 'XFAIL', 'FAIL', 'XPASS')} if not corpus and statuses else {})
        if not corpus:
            present = [r for r in selected if isinstance(r['truth'], list)]
            report[color].update(present=len(present), missed=sum(r['selected'] is None for r in present),
                                 false_positives=sum(r['truth'] == 'absent' and r['selected'] is not None for r in selected))
    return report


# 時計・Vector2だけ代用し、フィルタ／揺れ止め／予測器の実ソースをcompileする。
STUB = VECTOR.replace('public struct Vector2', 'public partial struct Vector2') + r'''
namespace UnityEngine {
 public partial struct Vector2 {
  public float sqrMagnitude => x*x+y*y;
  public float magnitude => (float)System.Math.Sqrt(sqrMagnitude);
  public static Vector2 Lerp(Vector2 a,Vector2 b,float t)=>a+(b-a)*t;
 }
 public static class Mathf {
  public static float Max(float a,float b)=>System.Math.Max(a,b);
  public static float Clamp(float x,float a,float b)=>System.Math.Max(a,System.Math.Min(b,x));
 }
}
'''

PROGRAM = r'''
using System;
using System.IO;
using System.Globalization;
using System.Reflection;
using UnityEngine;
class Program {
 static void Main(string[] args) {
  CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
  var filter=new PhoneSaberEndpointFilter();
  var manualFilter=new PhoneSaberEndpointFilter();
  var stabilizer=new PhoneSaberBladeStabilizer();
  PhoneSaberEndpointPredictor predictor=default;
  PhoneSaberEndpointPredictor manualPredictor=default;
  string previous="";
  foreach(var line in File.ReadLines(args[0])) {
   var r=line.Split(','); string group=r[0];
   if(group!=previous) {filter.Reset();manualFilter.Reset();stabilizer.Reset();predictor.Reset();manualPredictor.Reset();previous=group;}
   double t=double.Parse(r[1]);
   var a=new Vector2(float.Parse(r[2]),float.Parse(r[3]));
   var b=new Vector2(float.Parse(r[4]),float.Parse(r[5]));
   double lengthRatio=double.NaN,jumpRatio=double.NaN;
   if(Read<bool>(stabilizer,"hasSample") && t-Read<double>(stabilizer,"lastTime")<=.25) {
    float priorLength=Read<float>(stabilizer,"lastLength");
    double since=t-Read<double>(stabilizer,"acceptedTime");
    var expected=Read<Vector2>(stabilizer,"lastMid")+Read<Vector2>(stabilizer,"velocity")*(float)since;
    double gate=Math.Max(priorLength,Read<float>(stabilizer,"typicalLength"))*2*Math.Max(1,since*60);
    lengthRatio=(b-a).magnitude/priorLength;
    jumpRatio=((a+b)*.5f-expected).magnitude/gate;
   }
   int rejected=stabilizer.RejectedJumps,limited=stabilizer.LimitedLengths;
   stabilizer.Apply(t,a,b,out var sa,out var sb); Emit(group,t,"stabilizer",sa,sb);
   if(!double.IsNaN(lengthRatio))
    Console.WriteLine($"{group},{t:R},gates,{lengthRatio:R},{jumpRatio:R},{stabilizer.RejectedJumps-rejected},{stabilizer.LimitedLengths-limited}");
   filter.Apply(t,sa,sb,FILTER_MODE,out var fa,out var fb); Emit(group,t,"filtered",fa,fb);
   predictor.AddSample(t,fa,fb);
   // 本番設定と同じく、手動予測20/40/60では形状安定を「中」へ戻す。
   manualFilter.Apply(t,sa,sb,MANUAL_MODE,out var ma,out var mb);
   manualPredictor.AddSample(t,ma,mb);
   foreach(int lead in new[]{20,40,60}) {
    manualPredictor.Predict(t,lead,out var pa,out var pb);Emit(group,t,"manual"+lead,pa,pb);
    predictor.PredictAdaptive(t,lead,out pa,out pb);Emit(group,t,"auto"+lead,pa,pb);
   }
  }
 }
 static T Read<T>(object obj,string name)=>(T)obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(obj);
 static void Emit(string g,double t,string stage,Vector2 a,Vector2 b) {
  Console.WriteLine($"{g},{t:R},{stage},{a.x:R},{a.y:R},{b.x:R},{b.y:R}");
 }
}
'''


def clips(path):
    groups = []
    for p in sorted(path.glob('*/summary.json')):
        data = json.loads(p.read_text())['autoClip']
        w, h = (data['frameSize'][k] for k in ('width', 'height'))
        for color in ('red', 'blue'):
            frames = data['frames']
            valid = [f for f in frames if f[color]['sel'] is not None]
            if not valid:
                continue
            t = np.asarray([f['t'] for f in valid])
            raw = np.asarray([f[color]['sel'] for f in valid], dtype=float)
            # 撮影画像座標へ逆写像してからpx/度で評価する。
            scale = np.asarray([11 * 1919 / (1920 * (w-1)), -6 * 1079 / (1080 * (h-1))] * 2)
            offset = np.asarray([-5.5, 3] * 2)
            payload_scale = np.asarray([1919 / (w-1), 1079 / (h-1)] * 2)
            quantized = np.floor(raw * payload_scale + .5) / payload_scale
            groups.append(dict(name=p.parent.name + '/' + color, color=color, t=t, raw=raw,
                               frame=np.asarray([f['f'] for f in valid]),
                               world=quantized * scale + offset, scale=scale, offset=offset,
                               missing=len(frames)-len(valid)))
    return groups


def replay(groups, dotnet, derivative_cutoff=None, mode=2, shape_cutoff=None, shape_beta=None):
    with tempfile.TemporaryDirectory(prefix='phonesaber-position-') as tmp:
        path = Path(tmp) / 'samples.csv'
        with path.open('w') as f:
            writer = csv.writer(f)
            for i, group in enumerate(groups):
                for t, endpoints in zip(group['t'], group['world']):
                    writer.writerow([i, t, *endpoints])
        filt = (INPUT / 'PhoneSaberEndpointFilter.cs').read_text()
        if derivative_cutoff is not None:
            # オフライン比較専用。runtimeファイルを変更しない。
            filt, n = re.subn(r'Alpha\((?:mode == Steady \? 1\.0 : )?mode == Weak \? 10\.0 : 60\.0, dt\)',
                             f'Alpha(mode == Weak ? 10.0 : {float(derivative_cutoff)}, dt)', filt)
            if n != 1:
                raise ValueError('filter source changed: review experimental substitution')
        if shape_cutoff is not None:
            filt, n = re.subn(r'shapeAlpha = Alpha\(2\.0, dt\)',
                             f'shapeAlpha = Alpha({float(shape_cutoff)}, dt)', filt)
            if n != 1:
                raise ValueError('shape source changed: review experimental substitution')
        if shape_beta is not None:
            filt = filt.replace('minimum, beta, shapeAlpha)', f'minimum, {float(shape_beta)}, shapeAlpha)')
        extra = {'Filter.cs': filt,
                 'Stabilizer.cs': (INPUT / 'PhoneSaberBladeStabilizer.cs').read_text().split(
                     'public static class PhoneSaberBladeStabilizerSettings')[0],
                 'Vector.cs': STUB}
        program = PROGRAM.replace('FILTER_MODE', str(mode)).replace('MANUAL_MODE', str(2 if mode == 3 else mode))
        output = check_csharp(path, dotnet, extra_sources=extra, program=program)
    results = [{} for _ in groups]
    for row in csv.reader(io.StringIO(output)):
        i, _, stage = row[:3]
        results[int(i)].setdefault(stage, []).append([float(v) for v in row[3:]])
    for group, result in zip(groups, results):
        for stage, values in result.items():
            if stage == 'gates':
                result[stage] = np.asarray(values)
                continue
            result[stage] = (np.asarray(values) - group['offset']) / group['scale']
        result['raw'] = group['raw']
    return results


def windows(groups):
    rows = []
    for i, g in enumerate(groups):
        # 15区間=250ms。欠測・間引き・フレーム穴を跨がない。
        for start in range(0, len(g['t']) - 15, 16):
            stop = start + 16
            if np.any(np.diff(g['frame'][start:stop]) != 1):
                continue
            ends = g['raw'][[start, stop-1]].reshape(2, 2, 2)
            direct = np.linalg.norm(ends[1] - ends[0], axis=1).max()
            reverse = np.linalg.norm(ends[1, ::-1] - ends[0], axis=1).max()
            speed = min(direct, reverse) / (g['t'][stop-1] - g['t'][start])
            rows.append((i, start, stop, speed))
    return rows


def temporal_report(groups, results):
    wins = windows(groups)
    speeds = [w[3] for w in wins]
    low, high = np.quantile(speeds, [.25, .75])
    shape_trends = []
    for i, start, stop, speed in wins:
        if speed < high:
            continue
        _, a, length = geometry(groups[i]['raw'])
        angle = np.unwrap(a * 2) / 2
        dt = groups[i]['t'][stop-1] - groups[i]['t'][start]
        shape_trends.append((i, start, stop, (angle[stop-1]-angle[start])/dt,
                             (length[stop-1]-length[start])/dt))
    angle_floor, length_floor = (float(np.quantile([abs(w[k]) for w in shape_trends], .75)) for k in (3, 4))
    report = dict(window_seconds=.25, window_speed_px_s=stats(speeds), steady_max_speed=float(low),
                  moving_min_speed=float(high), stages={})
    report.update(angle_lag_min_speed_deg_s=angle_floor*180/np.pi, length_lag_min_speed_px_s=length_floor)
    for stage in results[0]:
        if stage == 'gates':
            continue
        stage_report = {}
        for color in ('red', 'blue'):
            for state in ('steady', 'moving'):
                selected = [w for w in wins if groups[w[0]]['color'] == color
                            and (w[3] <= low if state == 'steady' else w[3] >= high)]
                mid_jitter, angle_jitter, length_jitter = [], [], []
                mid_flicker, angle_flicker, length_flicker = [], [], []
                for i, start, stop, _ in selected:
                    m, a, length = geometry(results[i][stage][start:stop])
                    mid_jitter.extend(np.linalg.norm(np.diff(m, axis=0), axis=1))
                    angle_jitter.extend(angle_delta(a[1:], a[:-1]))
                    length_jitter.extend(np.abs(np.diff(length)))
                    # 移動中の差分には運動が含まれる。二階差分/2も併記し、定速成分を除く。
                    mid_flicker.extend(np.linalg.norm(np.diff(m, n=2, axis=0), axis=1) / 2)
                    signed_angle = np.unwrap(a * 2) / 2
                    angle_flicker.extend(np.abs(np.diff(signed_angle, n=2)) * 90 / np.pi)
                    length_flicker.extend(np.abs(np.diff(length, n=2)) / 2)
                stage_report[color + '/' + state] = dict(windows=len(selected),
                    midpoint_px=stats(mid_jitter), angle_deg=stats(angle_jitter), length_px=stats(length_jitter),
                    midpoint_flicker_px=stats(mid_flicker), angle_flicker_deg=stats(angle_flicker),
                    length_flicker_px=stats(length_flicker))
        # 現在の検出系列に対する変更量と速度方向への投影遅れ。物理的な誤差ではない。
        lag, future_error = [], []
        for i, start, stop, speed in wins:
            if speed < high:
                continue
            g = groups[i]
            raw_m, _, _ = geometry(g['raw'])
            m, _, _ = geometry(results[i][stage])
            v = (raw_m[stop-1] - raw_m[start]) / (g['t'][stop-1] - g['t'][start])
            if v @ v > 0:
                lag.extend((raw_m[start:stop] - m[start:stop]) @ v / (v @ v) * 1000)
            # H40の未来の観測出力と比較。50ms超の穴は使用しない。
            for j in range(start, stop):
                target = g['t'][j] + .04
                k = np.searchsorted(g['t'], target)
                if k >= len(g['t']) or g['t'][k] - g['t'][k-1] > .05:
                    continue
                weight = (target - g['t'][k-1]) / (g['t'][k] - g['t'][k-1])
                ref = raw_m[k-1] * (1-weight) + raw_m[k] * weight
                future_error.append(np.linalg.norm(m[j] - ref))
        stage_report['moving_projection_lag_ms'] = stats(lag)
        stage_report['moving_future40_midpoint_px'] = stats(future_error)
        angle_lag, length_lag = [], []
        for i, start, stop, angular_speed, length_speed in shape_trends:
            _, a, length = geometry(groups[i]['raw'])
            _, oa, ol = geometry(results[i][stage])
            if abs(angular_speed) >= angle_floor and angular_speed != 0:
                delta = (a[start:stop]-oa[start:stop]+np.pi/2) % np.pi - np.pi/2
                angle_lag.extend(delta/angular_speed*1000)
            if abs(length_speed) >= length_floor and length_speed != 0:
                length_lag.extend((length[start:stop]-ol[start:stop])/length_speed*1000)
        stage_report['angle_projection_lag_ms'] = stats(angle_lag)
        stage_report['length_projection_lag_ms'] = stats(length_lag)
        report['stages'][stage] = stage_report
    return report


def clip_labels(groups, results, rows):
    report = {}
    for stage in results[0]:
        if stage == 'gates':
            continue
        records = []
        for row in rows:
            clip, image = row['frame'].split('--')
            frame = int(image.split('_event_')[1])
            selected = None
            for g, result in zip(groups, results):
                if g['name'] != clip + '/' + row['color']:
                    continue
                indices = np.flatnonzero(g['frame'] == frame)
                if not len(indices):
                    continue
                values = result[stage][indices[0]]
                selected = [dict(x=values[0], y=values[1]), dict(x=values[2], y=values[3])]
            records.append(dict(row, selected=selected))
        report[stage] = labelled_report(records, statuses=False)
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--field-results', type=Path, required=True)
    parser.add_argument('--corpus-results', type=Path, required=True)
    parser.add_argument('--clips', type=Path, default=PRIVATE / 'diagnostics-inbox/autoclips')
    parser.add_argument('--json', type=Path, required=True)
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--trial-d-cutoff', type=float)
    parser.add_argument('--filter-mode', type=int, choices=(0, 1, 2, 3), default=2)
    parser.add_argument('--trial-shape-cutoff', type=float)
    parser.add_argument('--trial-shape-beta', type=float)
    args = parser.parse_args()
    destination = args.json.expanduser().resolve()
    if any((p / '.git').exists() for p in (destination, *destination.parents)):
        parser.error('private results must stay outside Git')
    groups = clips(args.clips)
    results = replay(groups, args.dotnet, args.trial_d_cutoff, args.filter_mode, args.trial_shape_cutoff, args.trial_shape_beta)
    rows = json.loads(args.field_results.read_text())['rows']
    gates = np.concatenate([r['gates'] for r in results if 'gates' in r])
    report = dict(field=labelled_report(rows), clip_labelled=clip_labels(groups, results, rows),
                  corpus_baseline_difference=labelled_report(json.loads(args.corpus_results.read_text())['fixtures'], True),
                  clip_groups=len(groups), samples=sum(len(g['t']) for g in groups),
                  missing=sum(sum(f[c]['sel'] is None for f in json.loads(p.read_text())['autoClip']['frames'])
                              for p in args.clips.glob('*/summary.json') for c in ('red', 'blue')),
                  trial_d_cutoff=args.trial_d_cutoff, trial_shape_cutoff=args.trial_shape_cutoff,
                  trial_shape_beta=args.trial_shape_beta,
                  filter_mode=args.filter_mode, length_ratio=stats(gates[:, 0]), jump_gate_ratio=stats(gates[:, 1]),
                  length_limited=int(np.count_nonzero((gates[:, 0] < .85) | (gates[:, 0] > 1.15))),
                  actual_length_limited=int(gates[:, 3].sum()), rejected_jumps=int(gates[:, 2].sum()),
                  outside_jump_gate=int(np.count_nonzero(gates[:, 1] > 1)),
                  temporal=temporal_report(groups, results))
    destination.write_text(json.dumps(report, indent=2, allow_nan=False) + '\n')
    print(f'groups={len(groups)} samples={report["samples"]} missing={report["missing"]}; report={destination}')


if __name__ == '__main__':
    main()
