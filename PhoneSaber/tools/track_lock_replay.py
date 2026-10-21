#!/usr/bin/env python3
"""保存AutoClipを旧/現Swift track-lockで再選択する（原画像・ラベルは変更しない）。

本番BGRADetection.swiftの選択クラスを直接コンパイルする。
候補生成/認識を再現しない。clip先頭は未取得から開始。省略eligibleがあるframeは
両履歴をresetし対象外とする（省略候補を存在しないと扱わない）。source切替は物体の
identityではない。endpoint一階差分/二階差分は実運動も含むjitter proxy。
"""
import argparse
import hashlib
from collections import Counter
import json
import math
from pathlib import Path
import re
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / 'PhoneSaber/ios/PhoneSaberSender/PhoneSaberSender/BGRADetection.swift'
BASELINE = '2ba0fe91ea3fc3c3424e82b6388eb02d35c63149'
PRIVATE = Path.home() / 'Library/Application Support/PhoneSaber'


def block(source, marker):
    start = source.index(marker)
    opening = source.index('{', start)
    depth, end = 1, opening + 1
    while depth:
        depth += (source[end] == '{') - (source[end] == '}')
        end += 1
    return source[start:end]


SWIFT = r'''
import Foundation
struct PixelPoint: Equatable { let x: Int, y: Int }
enum SaberColor: Hashable { case red, blue }
// 選択が読むフィールドだけ。認識・score計算のstubは評価しない。
struct SaberCandidate { let endpoints: (PixelPoint, PixelPoint); let score: Double; let id: Int; let highValueRatio: Double }
func candidate(_ x: Int, _ y: Int, _ u: Int, _ v: Int, _ score: Double, _ id: Int, _ highValueRatio: Double = 0) -> SaberCandidate {
    SaberCandidate(endpoints: (PixelPoint(x:x,y:y),PixelPoint(x:u,y:v)),score:score,id:id,highValueRatio:highValueRatio)
}
func require(_ value: @autoclosure () -> Bool, line: Int = #line) {
    if !value() { fputs("check failed at \(line)\n",stderr); exit(1) }
}
func checks() {
    let blade = candidate(100,100,200,100,70,1)
    let reflection = candidate(100,160,200,160,90,2,1)
    let reverse = candidate(200,100,100,100,70,3)
    func pure(_ list: [SaberCandidate], _ allow: Bool = true) -> Int? {
        SaberTrackLock.continuingWinner(list,predictedX:150,predictedY:100,length:100,angle:0,
                                       radius:150,allowPreference:allow)?.id
    }
    require(pure([reflection,blade]) == 1)
    require(pure([candidate(100,160,200,160,95,2),blade]) == 1) // 境界25は維持
    require(pure([candidate(100,160,200,160,95.001,2),blade]) == 2)
    require(pure([reflection]) == 2) // 対応消失時は即切替
    require(pure([reflection,reverse]) == 3) // 端点入替不変
    require(pure([reflection,blade],false) == 2)
    require(pure([candidate(100,500,200,500,200,4)]) == nil)
    require(SaberTrackLock.continuingWinner([blade],predictedX:150,predictedY:100,
                  length:0,angle:0,radius:150)?.id == 1)
    for color in [SaberColor.red,.blue] {
        let lock = SaberTrackLock()
        func select(_ cs: [SaberCandidate], _ now: Double) -> Int? {
            lock.select(cs,color:color,width:480,height:640,now:now)?.id
        }
        require(select([blade],1) == 1)
        for n in 1...2 { require(select([reflection,blade],1+Double(n)/60) == 1) }
        require(select([reflection,blade],1.05) == 2) // 高輝度支持の持続で復帰
        lock.reset(); _ = select([blade],1)
        let slight = candidate(100,160,200,160,72,2)
        for n in 1...15 { require(select([slight,blade],1+Double(n)/60) == 1) }
        require(select([slight,blade],1.28) == 2) // override期限は更新しない
        lock.reset(); require(select([reflection],2) == 2)
        let strongBlade = candidate(100,100,200,100,116,1)
        require(select([strongBlade,reflection],2.02) == 1) // 誤取得後の明確な復帰
        lock.reset(); _ = select([blade],3)
        for n in 1...3 { require(select([],3+Double(n)/60) == nil) }
        require(select([reflection],3.08) == 2)
        lock.reset(); _ = select([blade],4)
        require(select([reflection,blade],4.251) == 2)
        lock.reset(); _ = select([blade],5)
        require(select([reflection,blade],4.99) == 2)
    }
    // 誤取得から僅差の実刃へ。支持率差0.06・得点8点の境界と短窓を独立に確認する。
    for color in [SaberColor.red,.blue] {
        let lock = SaberTrackLock()
        let dim = candidate(100,160,200,160,70,2,0.90)
        let lit = candidate(100,100,200,100,73,1,0.99)
        func select(_ cs: [SaberCandidate], _ n: Int) -> Int? {
            lock.select(cs,color:color,width:480,height:640,now:10+Double(n)/60)?.id
        }
        _ = select([dim],0)
        require(select([lit,dim],1) == 2)
        require(select([lit,dim],2) == 2)
        let reversed = candidate(200,100,100,100,73,9,0.99)
        require(select([reversed,dim],3) == 9) // identityにrank/id/端点順を使わない
        lock.reset(); _ = select([dim],0)
        let weak = candidate(100,100,200,100,73,1,0.959)
        for n in 1...3 { require(select([weak,dim],n) == 2) }
        lock.reset(); _ = select([dim],0)
        let lower = candidate(100,100,200,100,69,1,0.99)
        for n in 1...2 { require(select([dim,lower],n) == 2) }
        require(select([dim,lower],3) == 1) // 順位外でも強い光支持が続けば復帰
        lock.reset(); _ = select([dim],0)
        _ = select([lit,dim],1); _ = select([dim],2)
        require(select([lit,dim],3) == 2)
        require(select([lit,dim],4) == 2)
        require(select([lit,dim],5) == 1) // 持続が途切れたら窓を捨てる
        lock.reset(); _ = select([dim],0)
        let scoreOnly = candidate(100,100,200,100,78,1,0.90)
        for n in 1...2 { require(select([scoreOnly,dim],n) == 2) }
        require(select([scoreOnly,dim],3) == 1) // 8点の持続、光支持の劣化なし
        lock.reset(); _ = select([dim],0)
        let belowScore = candidate(100,100,200,100,77.999,1,0.90)
        for n in 1...3 { require(select([belowScore,dim],n) == 2) }
        lock.reset(); _ = select([dim],0)
        let poorer = candidate(100,100,200,100,80,1,0.859)
        for n in 1...3 { require(select([poorer,dim],n) == 2) }
        lock.reset(); _ = select([dim],0)
        let along = candidate(150,160,250,160,73,1,0.99)
        for n in 1...3 { require(select([along,dim],n) == 2) } // 同じ軸上の端点違いは復帰にしない
        lock.reset(); _ = select([dim],0)
        _ = select([lit,dim],1); _ = select([lit,dim],2)
        require(select([],3) == nil)
        require(select([lit,dim],4) == 2) // 欠測後に古い窓で戻さない
        lock.reset(); _ = select([dim],0)
        _ = select([lit,dim],1); _ = select([lit,dim],2)
        lock.reset(); _ = select([dim],3)
        require(select([lit,dim],4) == 2)
    }
    let shared = SaberTrackLock()
    let dim = candidate(100,160,200,160,70,2,0.90)
    let lit = candidate(100,100,200,100,73,1,0.99)
    for color in [SaberColor.red,.blue] {
        _ = shared.select([dim],color:color,width:480,height:640,now:20)
    }
    for n in 1...3 {
        require(shared.select([lit,dim],color:.red,width:480,height:640,now:20+Double(n)/60)?.id == (n<3 ? 2 : 1))
    }
    require(shared.select([lit,dim],color:.blue,width:480,height:640,now:20.05)?.id == 2)
    // 角度差の0/180度境界、予測位置、長さ比でも対応を決める。
    let vertical = candidate(150,50,150,150,90,4)
    require(pure([vertical,blade]) == 1)
    let extended = candidate(0,100,300,100,90,5)
    require(pure([extended,blade]) == 1)
    require(SaberTrackLock.continuingWinner([reflection,blade],predictedX:150,predictedY:160,
                  length:100,angle:0,radius:150)?.id == 2)
    fputs("Track-lock host checks PASS\n",stderr)
}
checks()
let input = try JSONSerialization.jsonObject(with: Data(contentsOf: URL(fileURLWithPath:CommandLine.arguments[1]))) as! [[String:Any]]
var result: [[[Int]]] = []
for sequence in input {
    let old = OldSaberTrackLock(), new = SaberTrackLock()
    let width = sequence["width"] as! Int, height = sequence["height"] as! Int
    let color: SaberColor = sequence["color"] as! String == "red" ? .red : .blue
    let area = (sequence["area"] as? [String:Double]).map {
        SaberPlayArea(minX:$0["minX"]!,minY:$0["minY"]!,maxX:$0["maxX"]!,maxY:$0["maxY"]!)
    }
    let zones = try JSONDecoder().decode([SaberIgnoreZone].self,
        from: JSONSerialization.data(withJSONObject:sequence["zones"]!))
    // 評価専用の誤取得状態。保存された反射候補を実selectで1回取得し、その次から全候補を渡す。
    if let seed = sequence["seed"] as? [String:Any], let values = seed["candidate"] as? [Double] {
        let c = candidate(Int(values[1]),Int(values[2]),Int(values[3]),Int(values[4]),values[9],Int(values[0]),values[14])
        let t = seed["t"] as! Double
        _ = old.select([c],color:color,width:width,height:height,now:t,playArea:area)
        _ = new.select([c],color:color,width:width,height:height,now:t,playArea:area)
    }
    var pairs: [[Int]] = []
    for frame in sequence["frames"] as! [[String:Any]] {
        if frame["reset"] as! Bool { old.reset(); new.reset(); pairs.append([-2,-2]); continue }
        let now = frame["t"] as! Double
        let cs = (frame["cs"] as! [[Double]]).map {
            candidate(Int($0[1]),Int($0[2]),Int($0[3]),Int($0[4]),$0[9],Int($0[0]), $0.count > 14 ? $0[14] : 0)
        }.filter { !saberIgnored($0.endpoints,width:width,height:height,zones:zones) }
        pairs.append([old.select(cs,color:color,width:width,height:height,now:now,playArea:area)?.id ?? -1,
                      new.select(cs,color:color,width:width,height:height,now:now,playArea:area)?.id ?? -1])
    }
    result.append(pairs)
}
let sample = (0..<12).map { candidate(100,$0*8+100,200,$0*8+100,Double(100-$0),$0) }
var checksum = 0
for mode in 0...1 {
    let old = OldSaberTrackLock(), new = SaberTrackLock()
    let start = ProcessInfo.processInfo.systemUptime
    for i in 0..<100000 {
        let time = Double(i)/60
        checksum += (mode == 0 ? old.select(sample,color:.blue,width:480,height:640,now:time) :
                                new.select(sample,color:.blue,width:480,height:640,now:time))?.id ?? -1
    }
    fputs("benchmark \(mode == 0 ? "old" : "new") us/select: \((ProcessInfo.processInfo.systemUptime-start)*10)\n",stderr)
}
fputs("checksum \(checksum)\n",stderr)
print(String(data:try JSONSerialization.data(withJSONObject:result),encoding:.utf8)!)
'''


def replay(sequences, baseline=BASELINE, trial=None, recovery_trial=None):
    current = SOURCE.read_text()
    old = subprocess.run(['git', 'show', f'{baseline}:{SOURCE.relative_to(ROOT)}'], cwd=ROOT,
                         check=True, capture_output=True, text=True).stdout
    support = current[current.index('struct SaberIgnoreZone:'):current.index('/// FrameProcessor がフレームキュー上で所有')]
    selection = block(current, 'final class SaberTrackLock')
    program = SWIFT
    if trial is not None:
        selection = selection.replace('matching.score <= 25', f'matching.score <= {trial}')
        program = program.replace('\nchecks()\n', '\n')
    if recovery_trial is not None:
        replacements = {'count >= 3': f"count >= {recovery_trial.get('frames', 3)}",
                        'winner.score + 8': f"winner.score + {recovery_trial.get('score', 8)}",
                        'candidate.highValueRatio >= 0.97': f"candidate.highValueRatio >= {recovery_trial.get('high_value', .97)}",
                        'winner.highValueRatio + 0.06': f"winner.highValueRatio + {recovery_trial.get('advantage', .06)}"}
        for before, after in replacements.items(): selection = selection.replace(before, after)
        program = program.replace('\nchecks()\n', '\n')
    source = program + '\n'  + support + '\n' + selection + '\n' + \
        block(old, 'final class SaberTrackLock').replace('SaberTrackLock', 'OldSaberTrackLock')
    with tempfile.TemporaryDirectory(prefix='phonesaber-continuity-') as directory:
        tmp = Path(directory)
        (tmp/'main.swift').write_text(source)
        (tmp/'input.json').write_text(json.dumps(sequences))
        subprocess.run(['xcrun','swiftc','-O','-module-cache-path',str(tmp/'cache'),
                        str(tmp/'main.swift'),'-o',str(tmp/'run')],check=True)
        run = subprocess.run([str(tmp/'run'),str(tmp/'input.json')],capture_output=True,text=True)
        if run.returncode:
            raise RuntimeError(run.stderr)
    print(run.stderr,end='')
    return json.loads(run.stdout), run.stderr


def load(clips):
    sequences, metadata = [], []
    for path in sorted(clips.glob('*/summary.json')):
        if not any(date in path.parent.name for date in ('20261019','20261020','20261021')):
            continue
        data = json.loads(path.read_text())['autoClip']
        for color in ('red','blue'):
            frames = []
            for frame in data['frames']:
                cs = sorted([c for c in frame[color]['c'] if c[10]&1],key=lambda c:c[0])
                frames.append(dict(t=frame['t'], cs=cs, reset=len(cs)!=frame[color]['e']))
            def rectangle(values):
                return dict(zip(('minX','minY','maxX','maxY'),values))
            sequences.append(dict(**data['frameSize'],color=color,frames=frames,
                                  area=rectangle(data['playArea']) if data.get('playArea') else None,
                                  zones=[rectangle(z) for z in data.get('ignoreZones',{}).get(color,[])]))
            metadata.append((path.parent.name,data,color))
    return sequences, metadata


def stats(values):
    a = sorted(values)
    return dict(n=len(a), median=a[len(a)//2] if a else None,
                p90=a[math.ceil(len(a)*.9)-1] if a else None,max=a[-1] if a else None)


def aligned_distance(a,b):
    return min(max(math.dist(a[:2],b[:2]),math.dist(a[2:],b[2:])),
               max(math.dist(a[:2],b[2:]),math.dist(a[2:],b[:2])))


def summarize(sequences, metadata, output, field):
    import sys
    sys.path.insert(0,str(ROOT/'PhoneSaber/ios/PhoneSaberSender/Tools'))
    from field_regression import evaluate
    # fieldの手動端点は点ラベルのまま用いる。time seriesへ伝播・補間しない。
    labels = {}
    for path in field.glob('*.json'):
        label = json.loads(path.read_text())
        match = re.search(r'_(\d+)\.png$',label['image'])
        if match: labels[(label['sourceClip'],int(match[1]))] = label
    metrics = {(color,mode):dict(source_switches=0,geometry_breaks=0,large_midpoint_jumps=0,selected=0,
                               endpoint_step_px=[],endpoint_flicker_px=[]) for color in ('red','blue') for mode in (0,1)}
    label_rows, reflection, details = [], [], []
    incomplete = Counter()
    recorded_equal = Counter()
    for sequence,(name,data,color),rows in zip(sequences,metadata,output):
        previous = [None,None]; earlier = [None,None]; motions=[(0,0),(0,0)]
        for index,(frame,pair) in enumerate(zip(data['frames'],rows)):
            cs = {c[0]:c for c in frame[color]['c']}
            if pair[0]==-2:
                incomplete[color]+=1;previous=[None,None];earlier=[None,None];motions=[(0,0),(0,0)];continue
            winners = [cs.get(k) for k in pair]
            recorded_equal['compared']+=1
            recorded_equal['equal']+=((winners[0][1:5] if winners[0] else None)==frame[color]['sel'])
            for mode,c in enumerate(winners):
                m=metrics[color,mode]
                if c:
                    m['selected']+=1
                    if previous[mode] and frame['f']==previous[mode][0]+1:
                        p=previous[mode][1];a,b=c[1:5],p[1:5]
                        dt=frame['t']-previous[mode][2]
                        ax,ay=(a[0]+a[2])/2,(a[1]+a[3])/2
                        bx,by=(b[0]+b[2])/2,(b[1]+b[3])/2
                        al,bl=max(math.dist(a[:2],a[2:]),1),max(math.dist(b[:2],b[2:]),1)
                        aa,ba=math.atan2(a[3]-a[1],a[2]-a[0]),math.atan2(b[3]-b[1],b[2]-b[0])
                        angle=abs((aa-ba+math.pi/2)%math.pi-math.pi/2)/(math.pi/2)
                        vx,vy=motions[mode]
                        cost=.8*math.hypot(ax-bx-vx*dt,ay-by-vy*dt)/bl+.18*angle+.02*abs(math.log2(al/bl))
                        m['geometry_breaks']+=cost>.5
                        motions[mode]=((ax-bx)/dt,(ay-by)/dt) if dt>0 else (0,0)
                        m['source_switches']+=c[11]!=p[11]
                        m['large_midpoint_jumps']+=math.dist(((a[0]+a[2])/2,(a[1]+a[3])/2),((b[0]+b[2])/2,(b[1]+b[3])/2))>=50
                        # 端点順を前frameに揃え、一階差分と二階差分/2を採る。
                        if math.dist(a[:2],b[:2])+math.dist(a[2:],b[2:])>math.dist(a[:2],b[2:])+math.dist(a[2:],b[:2]):a=a[2:]+a[:2]
                        delta=[a[i]-b[i] for i in range(4)]
                        m['endpoint_step_px'].append(max(math.hypot(*delta[:2]),math.hypot(*delta[2:])))
                        if earlier[mode] is not None:
                            dd=[(delta[i]-earlier[mode][i])/2 for i in range(4)]
                            m['endpoint_flicker_px'].append(max(math.hypot(*dd[:2]),math.hypot(*dd[2:])))
                        # 次frameとの対応用に、現在frameの端点順も維持。
                        c=c.copy();c[1:5]=a;earlier[mode]=delta
                    else:earlier[mode]=None;motions[mode]=(0,0)
                    previous[mode]=(frame['f'],c,frame['t'])
                else:previous[mode]=None;earlier[mode]=None
            label=labels.get((name,frame['f']))
            if label:
                truth=label[color]
                target=None if truth=='absent' else [v for p in truth for v in (p['x'],p['y'])]
                errors=[aligned_distance(c[1:5],target) if c and target else None for c in winners]
                label_rows.append(dict(clip=name,frame=frame['f'],color=color,truth=truth,
                                       old=pair[0],new=pair[1],errors=errors,
                                       statuses=[evaluate(label,color,
                                           [dict(x=c[1],y=c[2]),dict(x=c[3],y=c[4])] if c else None)['status']
                                           for c in winners]))
            if '170253_418' in name and color=='blue' and frame['f']<=122:
                # 原画像19/38/57/76/95の白筒上。対応する細い長線を保守的に数える。
                # sourceはラベル抽出にだけ使用し、productionの対応関数には渡さない。
                refs=[bool(c and data['sources'][c[11]]=='color-emitter' and min(c[2],c[4])>390) for c in winners]
                reflection.append(dict(frame=frame['f'],old=refs[0],new=refs[1],
                                       endpoints=[c[1:5] if c else None for c in winners]))
            if pair[0]!=pair[1]:details.append(dict(clip=name,color=color,frame=frame['f'],old=pair[0],new=pair[1]))
    result={f'{color}/{mode}':{k:stats(v) if isinstance(v,list) else v for k,v in m.items()}
            for (color,mode),m in metrics.items()}
    return dict(metrics=result,incomplete_frames=dict(incomplete),old_vs_recorded=dict(recorded_equal),
                field_label_frames=label_rows,reflection=reflection,changed=details)


def is_reflection(candidate, data):
    # 元画像に対応した保守的ラベル。候補の生成・本番選択には使用しない。
    return bool(candidate and data['sources'][candidate[11]] == 'color-emitter'
                and min(candidate[2], candidate[4]) > 390)


def recovery_probes(sequences, metadata, baseline, recovery_trial=None):
    """17:02の全反射候補を誤取得した状態から再生。元候補とラベルを変更しない。"""
    index = next(i for i,m in enumerate(metadata) if '170253_418' in m[0] and m[2]=='blue')
    original = sequences[index]; data = metadata[index][1]
    probes, starts = [], []
    for offset, frame in enumerate(data['frames']):
        if frame['f'] > 122: break
        refs = [c for c in frame['blue']['c'] if c[10]&1 and is_reflection(c, data)]
        if not refs: continue
        c = min(refs, key=lambda c:c[0])
        starts.append((offset, frame['f']))
        probes.append(dict(original, seed=dict(t=frame['t'], candidate=c),
                           frames=original['frames'][offset+1:]))
    output, _ = replay(probes, baseline, recovery_trial=recovery_trial)
    rows = []
    for (offset, start), pairs in zip(starts, output):
        modes = []
        for mode in (0, 1):
            held, recovered, no_output = [], None, []
            for delta, (frame, pair) in enumerate(zip(data['frames'][offset+1:], pairs), 1):
                if frame['f'] > 122: break
                c = next((c for c in frame['blue']['c'] if c[0]==pair[mode]), None)
                if is_reflection(c, data): held.append(frame['f'])
                elif c and recovered is None:
                    recovered = dict(frame=frame['f'], frames=delta,
                        ms=1000*(frame['t']-data['frames'][offset]['t']))
                elif not c: no_output.append(frame['f'])
            modes.append(dict(first_non_reflection=recovered, reflection_after_start=held,
                              no_output=no_output,
                              reentries=[f for f in held if recovered and f > recovered['frame']]))
        rows.append(dict(seed_frame=start, old=modes[0], new=modes[1]))
    return dict(cases=rows, worst_ms={name:max(row[name]['first_non_reflection']['ms']
                for row in rows if row[name]['first_non_reflection']) for name in ('old','new')},
                unresolved={name:[row['seed_frame'] for row in rows
                    if row[name]['first_non_reflection'] is None] for name in ('old','new')})


def recovery_gate(probes):
    """反射の保守的判別に限った短窓ゲート。実刃の手動ラベル合格とは区別する。"""
    rows = probes['cases']
    longest = 0
    for row in rows:
        previous, run = None, 0
        for frame in row['new']['reflection_after_start']:
            run = run + 1 if previous is not None and frame == previous + 1 else 1
            longest = max(longest, run); previous = frame
    max_frames = max((row['new']['first_non_reflection']['frames'] for row in rows
                      if row['new']['first_non_reflection']), default=None)
    passed = bool(rows) and not probes['unresolved']['new'] and max_frames <= 3 and longest <= 2
    return dict(status='PASS' if passed else 'FAIL', cases=len(rows), max_frames=max_frames,
                worst_ms=probes['worst_ms']['new'], max_reflection_run_after_seed=longest,
                scope='保守的反射14件を基準とする既知反射の復帰評価（operator受入済み）')


def production_benchmark(baseline):
    """実SaberCandidateを使い、同じ12候補で旧/新selectのコストを測る。"""
    old=subprocess.check_output(['git','show',f'{baseline}:{SOURCE.relative_to(ROOT)}'],cwd=ROOT,text=True)
    program=block(old,'final class SaberTrackLock').replace('SaberTrackLock','OldSaberTrackLock')+r"""
@main struct Benchmark {
    static func main() {
        let width=480,height=640
        var bytes=Array(repeating:UInt8(0),count:width*height*4)
        for y in 100..<240 { for x in 100..<108 {
            let i=(y*width+x)*4;bytes[i]=245;bytes[i+1]=45;bytes[i+2]=30;bytes[i+3]=255
        } }
        let threshold=ColorThreshold(brightness:140,dominance:20)
        let frame=analyzeSabers(in:bytes,width:width,height:height,bytesPerRow:width*4,
                               redThreshold:threshold,blueThreshold:threshold)
        let candidate=frame.candidates[.blue]!.first { $0.isEmitterEligible }!
        let list=Array(repeating:candidate,count:12)
        var sums=[0.0,0.0], checksum=0
        for run in 0..<7 {
            for mode in 0...1 {
                let old=OldSaberTrackLock(),new=SaberTrackLock()
                let start=ProcessInfo.processInfo.systemUptime
                for i in 0..<100000 {
                    let t=Double(i)/60
                    let w=mode==0 ? old.select(list,color:.blue,width:width,height:height,now:t) :
                                    new.select(list,color:.blue,width:width,height:height,now:t)
                    checksum += w!.endpoints.0.x
                }
                let us=(ProcessInfo.processInfo.systemUptime-start)*10
                sums[mode]+=us
                print("\(run),\(mode),\(us)")
            }
        }
        print("checksum,\(checksum),\(sums[0]+sums[1])")
    }
}
"""
    with tempfile.TemporaryDirectory(prefix='phonesaber-continuity-perf-') as directory:
        tmp=Path(directory);(tmp/'Benchmark.swift').write_text('import Foundation\n'+program)
        subprocess.run(['xcrun','swiftc','-O','-module-cache-path',str(tmp/'cache'),
            str(SOURCE.parent/'DetectionCore.swift'),str(SOURCE),str(tmp/'Benchmark.swift'),
            '-o',str(tmp/'run')],check=True)
        output=subprocess.check_output([str(tmp/'run')],text=True)
    rows=[line.split(',') for line in output.splitlines() if not line.startswith('checksum')]
    return {name:stats([float(r[2]) for r in rows if r[1]==str(mode)])
            for mode,name in enumerate(('old_us','new_us'))}


def off_parity(clips, field, baseline):
    """OFFの全候補/score/端点/診断を旧版と厳密比較。画像はpipe上だけで展開する。"""
    import hashlib
    import sys
    sys.path.insert(0,str(ROOT/'PhoneSaber/ios/PhoneSaberSender/Tools'))
    import run_lossless_regression as lossless
    source_dir=SOURCE.parent
    fixtures=ROOT/'PhoneSaber/ios/PhoneSaberSenderTests/Fixtures'
    paths=sorted(field.glob('*.png')) + sorted(fixtures.rglob('*.png')) + [
        p for p in sorted(clips.glob('*/images/*.png'))
        if any(d in p.parent.parent.name for d in ('20261019','20261020','20261021'))]
    unique={hashlib.sha256(p.read_bytes()).hexdigest():p for p in paths}
    digest=hashlib.sha256()
    with tempfile.TemporaryDirectory(prefix='phonesaber-off-parity-') as directory:
        tmp=Path(directory)
        for name in ('DetectionCore.swift','BGRADetection.swift'):
            (tmp/name).write_bytes(subprocess.check_output(
                ['git','show',f'{baseline}:{source_dir.relative_to(ROOT)/name}'],cwd=ROOT))
        diagnostic=ROOT/'PhoneSaber/ios/PhoneSaberSenderTests/VideoDetectionDiagnostic.swift'
        for label,sources in (('old',tmp),('new',source_dir)):
            subprocess.run(['xcrun','swiftc','-O','-module-cache-path',str(tmp/'cache'),
                str(sources/'DetectionCore.swift'),str(sources/'BGRADetection.swift') if label=='old' else str(SOURCE),
                str(diagnostic),'-o',str(tmp/label)],check=True)
        for path in unique.values():
            width,height=lossless.dimensions(path)
            raw=lossless.command_output(['ffmpeg','-loglevel','error','-i',str(path),
                '-frames:v','1','-f','rawvideo','-pix_fmt','bgra','-'])
            signatures=[]
            for mode in ('old','new'):
                row=json.loads(lossless.command_output([str(tmp/mode),str(width),str(height),'2'],
                    input_data=raw).splitlines()[0])
                signatures.append(json.dumps({k:row[k] for k in ('colors','pipeline')},
                    sort_keys=True,separators=(',',':')))
            if signatures[0]!=signatures[1]:raise AssertionError(f'OFF parity: {path.name}')
            digest.update(signatures[0].encode())
    return dict(pngs=len(unique),exact=True,sha256=digest.hexdigest())


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--clips',type=Path,default=PRIVATE/'diagnostics-inbox/autoclips')
    parser.add_argument('--field',type=Path,default=PRIVATE/'field-regression')
    parser.add_argument('--baseline',default=BASELINE)
    parser.add_argument('--json',type=Path,required=True)
    parser.add_argument('--off-parity',action='store_true',help='保存PNGのOFF完全一致も検証')
    parser.add_argument('--recovery-sweep',action='store_true',help='復帰の窓長・得点差感度を34本で比較')
    parser.add_argument('--sweep',action='store_true',help='17:02のmargin感度を実Swiftで比較')
    args=parser.parse_args()
    target=args.json.expanduser().resolve()
    if target.is_relative_to(ROOT):raise ValueError('非公開の結果はGit外へ保存する')
    sequences,metadata=load(args.clips)
    output,host=replay(sequences,args.baseline)
    report=summarize(sequences,metadata,output,args.field)
    report['recovery_probes']=recovery_probes(sequences,metadata,args.baseline)
    report['recovery_proxy_gate'] = recovery_gate(report['recovery_probes'])
    report['previous_continuity_recovery'] = recovery_probes(
        sequences,metadata,args.baseline,recovery_trial=dict(frames=2147483647))
    report['production_class_sha256'] = hashlib.sha256(block(SOURCE.read_text(), 'final class SaberTrackLock').encode()).hexdigest()
    report['production_applied'] = True
    report['label_gate'] = 'CLOSED: operator accepted conservative 14; rough count 35 superseded'
    report['production_benchmark']=production_benchmark(args.baseline)
    report['by_date']={}
    for date in ('20261019','20261020','20261021'):
        ids=[i for i,m in enumerate(metadata) if date in m[0]]
        part=summarize([sequences[i] for i in ids],[metadata[i] for i in ids],
                       [output[i] for i in ids],args.field)
        report['by_date'][date]={k:part[k] for k in ('metrics','incomplete_frames','old_vs_recorded')}
    if args.off_parity:report['off_parity']=off_parity(args.clips,args.field,args.baseline)
    if args.recovery_sweep:
        sensitivity = []
        for frames, score in ((1,8),(2,8),(3,3),(3,5),(3,8),(3,10),(4,8)):
            trial = dict(frames=frames,score=score)
            oo,_ = replay(sequences,args.baseline,recovery_trial=trial)
            rr = summarize(sequences,metadata,oo,args.field)
            probes = recovery_probes(sequences,metadata,args.baseline,recovery_trial=trial)
            sensitivity.append(dict(**trial, metrics=rr['metrics'],
                reflection=sum(x['new'] for x in rr['reflection']),
                recovery_worst_ms=probes['worst_ms']['new']))
        report['recovery_sweep'] = sensitivity
    if args.sweep:
        sweep=[]
        focus=[i for i,m in enumerate(metadata) if '170253_418' in m[0] and m[2]=='blue']
        for margin in (5,10,15,20,23,25,30,40):
            ss=[sequences[i] for i in focus]; mm=[metadata[i] for i in focus]
            oo,_=replay(ss,args.baseline,trial=margin)
            rr=summarize(ss,mm,oo,args.field)
            sweep.append(dict(margin=margin,reflection=sum(x['new'] for x in rr['reflection'])))
        report['margin_sweep']=sweep
    report.update(clips=len(metadata)//2,frames=sum(len(s['frames']) for s in sequences)//2,host=host,baseline=args.baseline)
    target.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n')
    print(json.dumps({k:v for k,v in report.items() if k not in ('field_label_frames','reflection','changed','host','by_date')},indent=2))
    print('reflection',sum(r['old'] for r in report['reflection']),'->',sum(r['new'] for r in report['reflection']))


if __name__=='__main__':main()
