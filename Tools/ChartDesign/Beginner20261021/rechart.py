"""Re-author beginner charts from a frozen source, keeping audio timestamps.

No writes to the source repository. Candidate assets, pulse evidence, and a
complete planned-cut ledger are emitted to --output for independent review.
"""
import argparse
from bisect import bisect_left
from collections import Counter, defaultdict
from copy import deepcopy
import hashlib
import json
import math
from pathlib import Path

import numpy as np

PROFILES = {
    "2_23_AM": ("跳ねる旋律を表拍の短い問いと応答へ。両手アクセントを句頭に残す。", 3, 7),
    "Andalusia": ("6/8の複合拍と4/4の主打を使い分け、低い弧と句末ロングで応答。", 2, 6),
    "Aobasou": ("歌の主題の上下を残し、同じフレーズの帰りで手と高さを応答。", 3, 6),
    "BenkyouRemaster": ("BPM100の旋律と速い主部を対比。360区間は実時間でも減速。", 3, 6),
    "ElDorado": ("ゆっくりした主打と要所の両手着地。長い欠落は低密度の拍で接続。", 3, 7),
    "Epilogue": ("楽譜由来の主旋律・伴奏を維持し、上下の歌い回しと句末の余裕を残す。", 3, 8),
    "escapade": ("速い裏拍主題を表拍の短いまとまりへ。反復する輪郭と休みを残す。", 2, 5),
    "logic": ("変速前後の主旋律を表拍で受け、短い連打と休みで応答。", 4, 9),
    "ManicSonic": ("速い主部とBPM60の間を対比し、低密度でも次の入りを見失わせない。", 3, 7),
    "Morning": ("日常的なループの主題を小さい上下で反復し、再登場だけアクセントを変える。", 3, 7),
    "Phobia PARTⅠ": ("静かな前半と132BPMの主部を対比。3+3+2の裏打ちは表拍へ解決。", 2, 5),
    "RushE": ("Eの反復を遅い同音型として残し、和音区間の左右・両手で展開を示す。", 3, 7),
    "揺籠": ("既存の遅い複合拍を維持し、広すぎない往復と2回ロングで揺れを作る。", 3, 8),
    "製作中": ("変拍子の小節頭を優先。5/8・11/8の短い末尾をEasyで連続させない。", 3, 7),
}

def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))

def write(path, value):
    Path(path).parent.mkdir(parents=True, exist_ok=True)
    Path(path).write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

def hand(n):
    return n["color"] if n["color"] in ("blue", "red") else ("blue" if n["x"] < 0 else "red")

def interp(x, xs, ys):
    i = max(0, min(len(xs)-2, bisect_left(xs, x)-1))
    return ys[i] + (x-xs[i])*(ys[i+1]-ys[i])/(xs[i+1]-xs[i])

class Clock:
    def __init__(self, song, charts):
        self.chart = charts["normal"]
        self.offset = self.chart.get("offsetMs", 0)
        points = self.chart.get("editorTempoMap", [])
        self.method = "existing editorTempoMap" if points else "existing bpm/beatZeroMs"
        if song == "Epilogue":
            # The original score-aligned timestamps are non-linear; never
            # reconstruct this song at its initial 92 BPM.
            values = defaultdict(list)
            for chart in charts.values():
                for n in chart["notes"]:
                    values[round(n["beat"], 5)].append(n["time"])
            points = [{"beat": b, "timeMs": float(np.median(v))} for b, v in sorted(values.items())]
            self.method = "score-aligned existing beat/time anchors (all source difficulties)"
        if not points:
            origin = self.chart.get("beatZeroMs", 0)
            points = [{"beat": 0, "timeMs": origin},
                      {"beat": 1, "timeMs": origin+60000/self.chart["bpm"]}]
        self.beats = [p["beat"] for p in points]
        self.times = [p["timeMs"] for p in points]

    def time(self, b):
        return interp(b, self.beats, self.times)

    def beat(self, t):
        return interp(t, self.times, self.beats)

    def quarter(self, t):
        b = self.beat(t)
        return self.time(b+1)-self.time(b)

class Song:
    def __init__(self, song, source, features):
        self.name = song
        self.charts = {d: read(source/song/f"chart_{d}.json") for d in ("easy", "normal", "hard")}
        self.clock = Clock(song, self.charts)
        with np.load(features/(song+".npz")) as packed:
            self.audio = {k:packed[k] for k in packed.files}
        self.flux_scale = max(float(np.percentile(self.audio["flux"], 90)), 1e-8)
        self.energy_scale = max(float(np.percentile(self.audio["energy"], 90)), 1e-8)
        active=self.audio["t"][self.audio["energy"]>self.audio["energy"].max()*.01]
        self.music_start=float(active[0])*1000-self.clock.offset
        self.end = max(n["time"]+n.get("lengthMs", 0) for c in self.charts.values() for n in c["notes"])
        self.start = min(n["time"] for c in self.charts.values() for n in c["notes"])
        self.pulses, self.heads = self.make_pulses()
        self.pulse_times = [self.clock.time(b) for b in self.pulses]
        self.music_evidence = None
        if song=="RushE":
            self.music_evidence=read(features.parent/"rush-music-pulses.json")
            self.pulse_times=self.music_evidence["pulseTimesMs"]
            self.pulses=[self.clock.beat(t) for t in self.pulse_times]
            self.heads=set()
        self.pool = []
        source_charts=dict(self.charts)
        if song=="RushE":
            source_charts["reference"]=read(source/song/"chart_e.json")
        for diff, chart in source_charts.items():
            for index, n in enumerate(chart["notes"]):
                count = max(1, n.get("count", 1))
                length = n.get("lengthMs", 0)
                for k in range(count):
                    x = deepcopy(n)
                    t = n["time"] + (length*k/(count-1) if count > 1 else 0)
                    # Source offsets differ for the author's Hard in Zero.
                    t += chart.get("offsetMs", 0)-self.clock.offset
                    x.update(time=round(t, 4), beat=round(self.clock.beat(t), 6),
                             type="tap", direction="none", count=1, lengthMs=0)
                    self.pool.append({
                        "note": x, "source": diff, "index": index,
                        "originalLong": f"{diff}:{index}" if count > 1 else None,
                        "longCut": k, "originalFlick": n.get("direction", "none"),
                        "originalType": n.get("type", "tap"), "added": False,
                    })
        for r in self.pool:
            r["pulse"], r["phaseErrorMs"] = self.classify(r["note"]["time"])
        self.repairs = []

    def make_pulses(self):
        end_b = self.clock.beat(self.end)+8
        signatures = self.charts["normal"].get("timeSignatures") or [{"beat":0,"numerator":4,"denominator":4}]
        pulses, heads = [], []
        for si, sig in enumerate(signatures):
            start = sig["beat"]
            stop = signatures[si+1]["beat"] if si+1 < len(signatures) else end_b
            num, den = sig["numerator"], sig["denominator"]
            barlen = num*4/den
            if den == 8 and num == 6:
                offsets = [0, 1.5]  # compound main pulses, not every eighth
            elif den == 8 and num in (5, 11):
                # The metronome establishes bar heads, but does not establish
                # 2+3 versus 3+2 or the internal 11/8 grouping. Use the common
                # strong point of every hypothesis instead of inventing one.
                offsets = [0]
            else:
                offsets = list(range(math.ceil(barlen)))
            b = start
            while b < stop-1e-5:
                heads.append(b)
                pulses.extend(b+o for o in offsets if b+o < stop-1e-5)
                b += barlen
        return sorted(set(round(x,6) for x in pulses)), set(round(x,6) for x in heads)

    def classify(self, t):
        i = bisect_left(self.pulse_times, t)
        options = [j for j in (i-1, i) if 0 <= j < len(self.pulse_times)]
        j = min(options, key=lambda k: abs(self.pulse_times[k]-t))
        # Actual attacks in Andalusia are not mechanically quantized.
        tolerance = min(25, self.clock.quarter(t)*.12) if self.name == "Andalusia" else 3.5
        error = t-self.pulse_times[j]
        return (j if abs(error) <= tolerance else None), error

    def strength(self, t):
        t = (t+self.clock.offset)/1000
        ts = self.audio["t"]
        a, b = np.searchsorted(ts, [t-.035, t+.060])
        flux = self.audio["flux"][a:b]
        return min(3, float(flux.max())/self.flux_scale) if len(flux) else 0

    def audible(self, t):
        sec = (t+self.clock.offset)/1000
        a,b = np.searchsorted(self.audio["t"], [sec-.08, sec+.08])
        energy = self.audio["energy"][a:b]
        return len(energy) > 0 and float(energy.max()) > self.energy_scale*.025

    def weight(self, row, diff):
        n = row["note"]
        b = self.pulses[row["pulse"]] if row.get("pulse") is not None else self.clock.beat(n["time"])
        return (5 if row["source"] == diff else 1) + self.strength(n["time"])*1.7 + (
            2 if round(b, 6) in self.heads else 0) + (1 if row.get("originalLong") else 0)

    def gap_ok(self, a, b, diff):
        dt = b-a
        if diff == "easy":
            # Both exact local beat span and real seconds matter.
            if self.music_evidence:
                indexes=list(range(len(self.pulse_times)))
                beat_span=interp(b,self.pulse_times,indexes)-interp(a,self.pulse_times,indexes)
            else:
                beat_span=self.clock.beat(b)-self.clock.beat(a)
            return dt >= 499.5 and beat_span >= .999
        return dt >= 179.5

    def groups(self, diff):
        groups = defaultdict(list)
        for row in self.pool:
            if row["source"] == diff and row["pulse"] is not None:
                groups[row["pulse"]].append(row)
        result = []
        for pi, rows in sorted(groups.items()):
            best = max(rows, key=lambda r:self.weight(r,diff))
            group = [deepcopy(best)]
            # Keep intentional source chords, do not synthesize chord accents.
            for r in rows:
                if r is best or abs(r["note"]["time"]-best["note"]["time"]) > .05:
                    continue
                if hand(r["note"]) != hand(best["note"]):
                    group.append(deepcopy(r))
                    break
            result.append(group)
        return result

    def spaced(self, groups, diff):
        # Weighted interval selection on actual time, not a blind every-Nth-note
        # filter; musical accents win when adjacent attacks are too dense.
        times = [g[0]["note"]["time"] for g in groups]
        scores = [0.]
        take, prevs = [], []
        for i, group in enumerate(groups):
            j = i-1
            while j >= 0 and not self.gap_ok(times[j],times[i],diff):
                j -= 1
            value = scores[j+1] + self.weight(group[0],diff)
            yes = value > scores[i]
            scores.append(value if yes else scores[i])
            take.append(yes)
            prevs.append(j)
        chosen, i = [], len(groups)-1
        while i >= 0:
            if take[i]:
                chosen.append(groups[i]);i=prevs[i]
            else:
                i -= 1
        return list(reversed(chosen))

    def fill_gaps(self, groups, diff):
        # Actual intro silence stays untouched, but an audible introduction
        # cannot be silently exempted from the user's five-second condition.
        first=groups[0][0]["note"]["time"]
        if first-self.music_start>=4950:
            choices=[r for r in self.pool if r["pulse"] is not None
                     and self.music_start<=r["note"]["time"]<self.music_start+4950
                     and self.gap_ok(r["note"]["time"],first,diff)]
            if not choices:
                raise RuntimeError(f"{self.name}/{diff}: no evidenced opening attack")
            row=deepcopy(min(choices,key=lambda r:r["note"]["time"]))
            groups.insert(0,[row])
            self.repairs.append({"difficulty":diff,"timeMs":row["note"]["time"],
                                 "reason":"音のある導入で5秒以上待たせない既存表拍"})
        # The user explicitly excludes the final reverb.
        for _ in range(500):
            gaps = [(b[0]["note"]["time"]-a[0]["note"]["time"], i)
                    for i,(a,b) in enumerate(zip(groups,groups[1:]))]
            if not gaps or max(gaps)[0] < 4950:
                break
            _, i = max(gaps)
            left, right = groups[i][0]["note"]["time"], groups[i+1][0]["note"]["time"]
            target = (left+right)/2
            choices = [r for r in self.pool if r["pulse"] is not None and
                       self.gap_ok(left,r["note"]["time"],diff) and
                       self.gap_ok(r["note"]["time"],right,diff)]
            # Prefer a pre-existing attack close to the middle, keeping
            # source timestamps. The fallback also requires a measured attack.
            if choices:
                row = deepcopy(max(choices,key=lambda r:self.weight(r,diff)-abs(r["note"]["time"]-target)/650))
                reason = "既存譜面の表拍を補完"
            else:
                options = [(pi,t) for pi,t in enumerate(self.pulse_times)
                           if self.gap_ok(left,t,diff) and self.gap_ok(t,right,diff)
                           and self.audible(t) and self.strength(t)>=.30]
                if not options:
                    raise RuntimeError(f"{self.name}/{diff}: no evidenced pulse attack in gap {left}..{right}")
                pi,t = max(options,key=lambda p:self.strength(p[1])*2-abs(p[1]-target)/650)
                nearest = min(self.pool,key=lambda r:abs(r["note"]["time"]-t))
                row = deepcopy(nearest)
                row["note"].update(time=round(t,4),beat=round(self.clock.beat(t),6),
                                   type="tap",direction="none",count=1,lengthMs=0)
                row.update(pulse=pi,phaseErrorMs=0,source="audio-pulse",index=None,
                           originalLong=None,originalFlick="none",added=True)
                reason = "立ち上がりを確認した主拍を低密度で補完"
            self.repairs.append({"difficulty":diff,"gap":[left,right],"timeMs":row["note"]["time"],
                                 "reason":reason,"onsetStrength":round(self.strength(row["note"]["time"]),3)})
            groups.insert(i+1,[row])
        else:
            raise RuntimeError("Gap insertion did not converge")
        return groups

    def rebuild_depleted_phrases(self, groups, diff):
        # An offbeat melody cannot just disappear after filtering. Rebuild its
        # short phrase on evidenced main pulses, with the original phrase
        # density as a ceiling so quiet passages still breathe.
        bins=defaultdict(list)
        for r in self.pool:
            if r["source"]==diff:
                bins[int(self.clock.beat(r["note"]["time"])//16)].append(r)
        for block,original in sorted(bins.items()):
            lo,hi=self.clock.time(block*16),self.clock.time((block+1)*16)
            old_count=len(set(round(r["note"]["time"],2) for r in original))
            seconds=(hi-lo)/1000
            desired=min(old_count, max(2,round(seconds*(.85 if diff=="easy" else 1.4))))
            present=[g for g in groups if lo<=g[0]["note"]["time"]<hi]
            if len(present)>=desired*.80:
                continue
            candidates=[deepcopy(r) for r in self.pool if r["pulse"] is not None
                        and lo<=r["note"]["time"]<hi]
            # Add measured main-pulse candidates only where the audio supplies
            # an attack; a sustain alone is not enough even for gap repairs.
            for pi,t in enumerate(self.pulse_times):
                if self.music_evidence:
                    continue  # only existing, source-aligned attacks for RUSH E
                if not max(lo,self.start)<=t<min(hi,self.end) or self.strength(t)<.38:
                    continue
                if any(abs(r["note"]["time"]-t)<45 for r in candidates):
                    continue
                near=min(original,key=lambda r:abs(r["note"]["time"]-t))
                r=deepcopy(near)
                r["note"].update(time=round(t,4),beat=round(self.clock.beat(t),6),
                                 type="tap",direction="none",count=1,lengthMs=0)
                r.update(pulse=pi,phaseErrorMs=0,source="audio-onset",index=None,
                         originalLong=None,originalFlick="none",added=True)
                candidates.append(r)
            for r in sorted(candidates,key=lambda r:self.weight(r,diff),reverse=True):
                t=r["note"]["time"]
                if any(not self.gap_ok(min(t,g[0]["note"]["time"]),max(t,g[0]["note"]["time"]),diff)
                       for g in groups):
                    continue
                groups.append([r]);present.append([r])
                self.repairs.append({"difficulty":diff,"phraseBeats":[block*16,(block+1)*16],
                                     "timeMs":t,"reason":"裏拍整理後の楽句を根拠のある表拍で再構成",
                                     "source":r["source"],"onsetStrength":round(self.strength(t),3)})
                if len(present)>=desired:
                    break
        return sorted(groups,key=lambda g:g[0]["note"]["time"])

    def add_normal_cells(self, groups):
        present = {g[0]["pulse"]:g for g in groups}
        candidates = []
        for row in self.pool:
            if row["source"] != "normal" or row["pulse"] is not None or row.get("longCut",0):
                continue
            t = row["note"]["time"]
            i = bisect_left(self.pulse_times,t)-1
            if i not in present or i+1 not in present:
                continue
            a,b = present[i][0]["note"]["time"],present[i+1][0]["note"]["time"]
            signatures=self.charts["normal"].get("timeSignatures") or [{"beat":0,"numerator":4,"denominator":4}]
            meter=max((s for s in signatures if s["beat"]<=self.clock.beat(a)+.001),
                      key=lambda s:s["beat"])
            if meter["denominator"]==8 and meter["numerator"] in (5,11):
                continue
            boundaries=[self.clock.time(s["beat"]) for s in signatures[1:]]
            boundaries += self.clock.times[1:-1] if self.name!="Epilogue" else []
            if any(a-.1<=boundary<=b+.1 for boundary in boundaries):
                continue
            if not (self.gap_ok(a,t,"normal") and self.gap_ok(t,b,"normal")):
                continue
            if min(t-a,b-t)<199.5:
                continue
            fraction = (t-a)/(b-a)
            subdivisions=(1/3,2/3) if meter["denominator"]==8 and meter["numerator"]==6 else (.5,)
            if min(abs(fraction-f) for f in subdivisions)>.10:
                continue
            candidates.append((self.strength(t),i,deepcopy(row)))
        selected = []
        for strength,i,row in sorted(candidates,reverse=True,key=lambda x:x[0]):
            if any(abs(i-j)<3 or abs(row["note"]["time"]-r["note"]["time"])<2800 for j,r in selected):
                continue
            # Off-on-off remains forbidden even when two cells touch.
            selected.append((i,row))
            if len(selected) >= max(2, len(groups)//12):
                break
        for i,row in selected:
            row["cell"] = [present[i][0]["note"]["time"],row["note"]["time"],present[i+1][0]["note"]["time"]]
            groups.append([row])
        return sorted(groups,key=lambda g:g[0]["note"]["time"])

    def phrase_breaths(self,groups,diff):
        # Removing syncopation must not leave a minute-long metronome stream.
        # Keep short repetitions, but let weak phrase endings breathe. A rest
        # never exceeds 5s and neither a chord nor an original long is removed.
        limit=10 if diff=="easy" else 14
        for _ in range(100):
            run_start=0
            found=None
            for i in range(2,len(groups)):
                a,b,c=(groups[j][0]["note"]["time"] for j in (i-2,i-1,i))
                if abs((b-a)-(c-b))>=3:
                    run_start=i-1
                if i-run_start+1>limit:
                    found=(run_start,i)
                    break
            if found is None:
                break
            start,end=found
            options=[]
            for i in range(start+5,end):
                row=groups[i][0]
                if len(groups[i])>1 or row.get("originalLong"):
                    continue
                if groups[i+1][0]["note"]["time"]-groups[i-1][0]["note"]["time"]>=4900:
                    continue
                beat=self.clock.beat(row["note"]["time"])
                if abs(beat/4-round(beat/4))<.001:
                    continue
                phrase_end=1 if abs(beat%4-3)<.08 else 0
                options.append((self.strength(row["note"]["time"])-phrase_end*.8,i))
            if not options:
                break
            _,i=min(options)
            row=groups.pop(i)
            self.repairs.append({"difficulty":diff,"timeMs":row[0]["note"]["time"],
                                 "reason":"表拍の長い均一列を弱い句末の休みで区切る"})
        return groups

    def layout(self, groups, diff):
        previous = {}
        recovery = 600 if diff == "easy" else (450 if self.name=="Andalusia" else 300)
        speed = 1.45 if self.name == "Andalusia" else (2.3 if diff == "easy" else 3.4)
        result = []
        for group in groups:
            for row in sorted(group,key=lambda r:r["note"]["x"]):
                n = row["note"]
                side = hand(n)
                other = "red" if side == "blue" else "blue"
                if (len(group)==1 and side in previous and other in previous
                    and n["time"]-previous[side]["time"]<recovery
                    and n["time"]-previous[other]["time"]<recovery):
                    # A preceding two-hand accent must not consume both hands
                    # immediately before the next musical attack. Keep that
                    # accent on one hand and free the following hand.
                    last_time=max(p["time"] for p in previous.values())
                    chord=[r for r in result if abs(r["note"]["time"]-last_time)<.1]
                    if len(chord)==2:
                        victim=next(r for r in chord if r["hand"]==side)
                        result.remove(victim)
                        earlier=[r["note"] for r in result if r["hand"]==side]
                        if earlier:previous[side]=earlier[-1]
                        else:previous.pop(side,None)
                if len(group)==1 and side in previous:
                    dt = n["time"]-previous[side]["time"]
                    if dt < recovery and (other not in previous or n["time"]-previous[other]["time"] >= recovery):
                        side = other
                        n["color"] = side
                        row["handRepair"] = True
                # Gold remains an accent only when either choice has space.
                if n["color"] in ("gold","default") and len(group)==1:
                    if any(n["time"]-p["time"]<recovery for p in previous.values()):
                        n["color"]=side
                sign = -1 if side=="blue" else 1
                n["x"] = round(sign*min(1.9 if diff=="normal" else 1.65,
                                       max(.9,abs(n["x"]))),4)
                n["y"] = round(max(-.9,min(.9,n["y"])),4)
                if side in previous:
                    p=previous[side]
                    seconds=max(0,(n["time"]-p["time"])/1000)
                    distance=math.hypot(n["x"]-p["x"],n["y"]-p["y"])
                    limit=seconds*speed
                    if distance>limit and distance>0:
                        scale=limit/distance
                        n["x"]=round(p["x"]+(n["x"]-p["x"])*scale,4)
                        n["y"]=round(p["y"]+(n["y"]-p["y"])*scale,4)
                previous[side]=n
                row["hand"]=side
                result.append(row)
        return sorted(result,key=lambda r:(r["note"]["time"],r["note"]["x"],r["note"]["y"]))

    def pack_longs(self, rows, diff):
        # Longs are real repeated cuts. Only combine two selected on-pulse
        # attacks with no intervening demand; never use length to mask a gap.
        result=[]
        i=0
        last_long=-10000
        while i<len(rows):
            a=rows[i]
            if i+1<len(rows):
                b=rows[i+1]
                same_origin=a.get("originalLong") and a.get("originalLong")==b.get("originalLong")
                safe=(a["pulse"] is not None and b["pulse"] is not None
                      and a["hand"]==b["hand"] and self.gap_ok(a["note"]["time"],b["note"]["time"],"easy")
                      and 500 <= b["note"]["time"]-a["note"]["time"] <= 2500
                      and a["note"]["time"]-last_long>=7000)
                before=rows[i-1]["note"]["time"] if i else -10000
                after=rows[i+2]["note"]["time"] if i+2<len(rows) else self.end+10000
                isolated=(a["note"]["time"]-before>=600 and after-b["note"]["time"]>=600)
                if same_origin and safe and isolated:
                    n=a["note"]
                    n.update(type="long",direction="none",count=2,
                             lengthMs=round(b["note"]["time"]-n["time"],4))
                    a["cutTimes"]=[n["time"],b["note"]["time"]]
                    a["endSource"]=b
                    result.append(a)
                    last_long=n["time"];i+=2;continue
            a["cutTimes"]=[a["note"]["time"]]
            result.append(a);i+=1
        if not any(r["note"]["count"]>1 for r in result):
            # When every old long contained a forbidden subdivision, use one
            # exposed pair of existing main attacks as a slow two-cut cadence.
            choices=[]
            for j,(a,b) in enumerate(zip(result,result[1:])):
                t,u=a["note"]["time"],b["note"]["time"]
                before=result[j-1]["cutTimes"][-1] if j else -10000
                after=result[j+2]["note"]["time"] if j+2<len(result) else self.end+10000
                if (a["pulse"] is not None and b["pulse"] is not None and
                    self.gap_ok(t,u,"easy") and 600<=u-t<=2300 and
                    t-before>=750 and after-u>=750):
                    choices.append((self.strength(t)+self.strength(u),j))
            if choices:
                _,j=max(choices)
                a,b=result[j:j+2]
                a["note"].update(type="long",count=2,lengthMs=round(b["note"]["time"]-a["note"]["time"],4))
                a["cutTimes"]=[a["note"]["time"],b["note"]["time"]]
                a["longRephrase"]="既存の離れた表拍2打をゆっくりした同手連打へ"
                result.pop(j+1)
        return result

    def flicks(self, rows, diff):
        target = PROFILES[self.name][1 if diff=="easy" else 2]
        target = min(target,max(1,int(len(rows)*(.04 if diff=="easy" else .09))))
        end=rows[-1]["cutTimes"][-1]
        start=rows[0]["note"]["time"]
        chosen=[]
        count_by_time=Counter(r["note"]["time"] for r in rows)
        for slot in range(target):
            desired=start+(end-start)*(slot+1)/(target+1)
            candidates=[]
            for i,r in enumerate(rows):
                n=r["note"];t=n["time"]
                if n["count"]>1 or r["pulse"] is None or count_by_time[t]>1:
                    continue
                if any(abs(t-rows[j]["note"]["time"])<(12000 if diff=="easy" else 7000) for j in chosen):
                    continue
                if any(abs(i-j)<16 for j in chosen):
                    continue
                prev=rows[i-1]["cutTimes"][-1] if i else -10000
                next_t=rows[i+1]["note"]["time"] if i+1<len(rows) else end+10000
                if min(t-prev,next_t-t)<(740 if diff=="easy" else 480):
                    continue
                same=[r2 for r2 in rows if r2 is not r and r2["hand"]==r["hand"]]
                before_hand=max((r2["cutTimes"][-1] for r2 in same if r2["note"]["time"]<t),default=-10000)
                after_hand=min((r2["note"]["time"] for r2 in same if r2["note"]["time"]>t),default=end+10000)
                if min(t-before_hand,after_hand-t)<(1000 if diff=="easy" else 600):
                    continue
                if any(r2["note"]["count"]>1 and
                       min(abs(t-r2["note"]["time"]),abs(t-r2["cutTimes"][-1]))<1000 for r2 in rows):
                    continue
                value=self.strength(t)+(1.4 if r["originalFlick"]!="none" else 0)-abs(t-desired)/1800
                candidates.append((value,i))
            if candidates:
                _,i=max(candidates)
                chosen.append(i)
        if not chosen:
            raise RuntimeError(f"No exposed flick slot: {self.name}/{diff}")
        for k,i in enumerate(sorted(chosen)):
            r=rows[i]
            # Direction follows the available swing space and phrase contour,
            # not the ordinal number of the chosen flick.
            n=r["note"]
            before=rows[i-1]["note"] if i else n
            direction="up" if n["y"]<.2 and n["y"]>before["y"]+.12 else "down"
            if n["y"]<-.3:direction="up"
            if n["y"]>.3:direction="down"
            n.update(type="direction",direction=direction)
        return rows

    def make(self,diff):
        groups=self.spaced(self.groups(diff),diff)
        groups=self.rebuild_depleted_phrases(groups,diff)
        groups=self.fill_gaps(groups,diff)
        groups=self.phrase_breaths(groups,diff)
        if diff=="normal":
            groups=self.add_normal_cells(groups)
        rows=self.flicks(self.pack_longs(self.layout(groups,diff),diff),diff)
        chart=deepcopy(self.charts[diff])
        chart["notes"]=[r["note"] for r in rows]
        chart["_comment"]=f"2026-10-21初心者向け改訂 / {PROFILES[self.name][0]} 表拍主体、Normal裏拍は表裏表に限定。Easyは四分以上・実時間500ms以上。ロングも切断間隔を検証。"
        # Old provenance flags must not claim the new chart still follows an
        # earlier Hard-subset or melody contract.
        for k in list(chart):
            if k.endswith("DesignVersion") or k=="spatialLayoutVersion":
                del chart[k]
        ledger=[]
        for i,r in enumerate(rows):
            n=r["note"]
            ledger.append({
                "index":i,"timesMs":r["cutTimes"],
                "directions":[n["direction"] if n["direction"]!="none" else ("down" if k%2==0 else "up")
                              for k in range(len(r["cutTimes"]))],
                "hand":r["hand"],"source":r["source"],"sourceIndex":r["index"],
                "sourceLong":r["originalLong"],"added":r["added"],
                "phaseErrorMs":round(r["phaseErrorMs"],4),
                "cell":r.get("cell"),"handRepair":r.get("handRepair",False),
            })
        return chart,{"song":self.name,"difficulty":diff,"musicStartMs":self.music_start,"notes":ledger}

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--source",type=Path,required=True)
    parser.add_argument("--features",type=Path,required=True)
    parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args()
    frozen_path=args.features.parent/"frozen-pulses.json"
    frozen=read(frozen_path)
    design={"date":"2026-10-21","songs":{},
            "pulseMapSha256":hashlib.sha256(frozen_path.read_bytes()).hexdigest(),
            "tailPolicy":"User: leave final silence/reverb unchanged; <5s applies to audible introduction and between musical cuts."}
    for name in PROFILES:
        song=Song(name,args.source,args.features)
        if [round(t,4) for t in song.pulse_times]!=frozen["songs"][name]["pulseTimesMs"]:
            raise RuntimeError("Musical pulse map changed after review: "+name)
        design["songs"][name]={
            "intent":PROFILES[name][0],"clockMethod":song.clock.method,
            "musicStartMs":song.music_start,
            "pulseTimesMs":[round(t,4) for t in song.pulse_times],
            "pulseBeats":song.pulses,
            "sourceHashes":{d:hashlib.sha256((args.source/name/f"chart_{d}.json").read_bytes()).hexdigest() for d in song.charts},
        }
        if song.music_evidence:
            design["songs"][name]["musicalPulseEvidence"]=song.music_evidence
            design["songs"][name]["musicPulseQuarterMs"]=[
                min(1000,b-a) for a,b in zip(song.pulse_times,song.pulse_times[1:])]
            design["songs"][name]["musicPulseQuarterMs"].append(design["songs"][name]["musicPulseQuarterMs"][-1])
        for diff in ("easy","normal"):
            chart,ledger=song.make(diff)
            write(args.output/"Assets/StreamingAssets/Songs"/name/f"chart_{diff}.json",chart)
            if diff=="normal":
                write(args.output/"Assets/StreamingAssets/Songs"/name/"chart.json",chart)
            write(args.output/"Tools/ChartDesign/Beginner20261021/plans"/name/f"{diff}.json",ledger)
            print(name,diff,len(chart["notes"]),sum(n["count"]>1 for n in chart["notes"]),
                  sum(n["direction"]!="none" for n in chart["notes"]),flush=True)
        design["songs"][name]["gapRepairs"]=song.repairs
    write(args.output/"Tools/ChartDesign/Beginner20261021/design.json",design)
    (args.output/"Tools/ChartDesign/Beginner20261021/pulse-map.json").write_bytes(frozen_path.read_bytes())

if __name__=="__main__":
    main()
