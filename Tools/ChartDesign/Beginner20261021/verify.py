"""Independent saved-chart audit. Does not import the chart generator."""
import argparse
from bisect import bisect_left
from collections import Counter
import hashlib
import json
import math
from pathlib import Path

# 例外時刻は time + offsetMs のms。単発・方向なしの指定ノーツだけに適用する。
# ID、曲、難易度、音源時刻、色、担当手、理由。
CHOICE_REASON = "2026-10-23 ユーザーが音に合わせることを選んだ"
CHOICE_EPSILON_MS = 1e-6
AUDIO_CHOICES = (
    ("h06", "Andalusia", "easy", 3949.193115234375, "blue", "blue", CHOICE_REASON),
    ("h15", "logic", "normal", 46434.25, "blue", "blue", CHOICE_REASON),
    ("h16", "logic", "normal", 129403.0, "red", "red", CHOICE_REASON),
    ("h17", "logic", "normal", 138403.0, "red", "red", CHOICE_REASON),
    ("z01", "2_23_AM", "easy", 8075.0, "blue", "blue", CHOICE_REASON),
    ("z01", "2_23_AM", "normal", 8075.0, "red", "red", CHOICE_REASON),
    ("z01", "2_23_AM", "hard", 8075.0, "blue", "blue", CHOICE_REASON),
    ("z02", "logic", "easy", 111453.0, "red", "red", CHOICE_REASON),
    ("z02", "logic", "normal", 111453.0, "red", "red", CHOICE_REASON),
    ("z02", "logic", "hard", 111453.0, "red", "red", CHOICE_REASON),
    ("z02", "logic", "hard", 111453.0, "blue", "blue", CHOICE_REASON),
    ("z03", "logic", "easy", 113454.0, "red", "red", CHOICE_REASON),
    ("z03", "logic", "normal", 113454.0, "red", "red", CHOICE_REASON),
    ("z03", "logic", "hard", 113454.0, "red", "red", CHOICE_REASON),
    ("z03", "logic", "hard", 113454.0, "blue", "blue", CHOICE_REASON),
    ("z04", "BenkyouRemaster", "easy", 24805.0, "red", "red", CHOICE_REASON),
    ("z04", "BenkyouRemaster", "hard", 24805.0, "red", "red", CHOICE_REASON),
    ("z05", "BenkyouRemaster", "normal", 64325.0, "blue", "blue", CHOICE_REASON),
    ("z05", "BenkyouRemaster", "hard", 64325.0, "blue", "blue", CHOICE_REASON),
    ("z06", "RushE", "easy", 118382.0, "gold", "red", CHOICE_REASON),
    ("z06", "RushE", "normal", 118382.0, "gold", "blue", CHOICE_REASON),
    ("z06", "RushE", "normal", 118382.0, "gold", "red", CHOICE_REASON),
    ("z06", "RushE", "hard", 118382.0, "gold", "blue", CHOICE_REASON),
    ("z06", "RushE", "hard", 118382.0, "gold", "red", CHOICE_REASON),
    ("z06", "RushE", "e", 118382.0, "gold", "blue", CHOICE_REASON),
    ("z06", "RushE", "e", 118382.0, "gold", "red", CHOICE_REASON),
    ("z07", "RushE", "easy", 50138.0, "blue", "blue", CHOICE_REASON),
    ("z07", "RushE", "normal", 50138.0, "blue", "blue", CHOICE_REASON),
    ("z07", "RushE", "hard", 50138.0, "blue", "blue", CHOICE_REASON),
    ("z07", "RushE", "e", 50138.0, "red", "red", CHOICE_REASON),
    ("z07", "RushE", "e", 50138.0, "blue", "blue", CHOICE_REASON),
    ("z08", "RushE", "easy", 134770.0, "gold", "red", CHOICE_REASON),
    ("z08", "RushE", "normal", 134770.0, "gold", "red", CHOICE_REASON),
    ("z08", "RushE", "hard", 134770.0, "gold", "red", CHOICE_REASON),
    ("z08", "RushE", "e", 134770.0, "gold", "red", CHOICE_REASON),
)
# 同手間隔はこの隣接2ノーツだけ。281.25ms全般を許可しない。
HAND_CHOICES = (
    ("h15", "logic", "normal", "blue", 46153.0, 46434.25, 281.25, CHOICE_REASON),
)


def note_hand(note):
    if note["color"] in ("red", "blue"):
        return note["color"]
    return "blue" if note["x"] < 0 else "red"


def single_tap(note):
    return (note.get("count", 1) == 1 and note.get("lengthMs", 0) == 0
            and note.get("direction", "none") == "none"
            and note.get("type", "tap") == "tap")


def audio_choice(song, difficulty, chart, note):
    if not single_tap(note):
        return None
    audio_ms = note["time"] + chart.get("offsetMs", 0)
    return next((r for r in AUDIO_CHOICES
                 if (song, difficulty, note["color"], note_hand(note)) ==
                 (r[1], r[2], r[4], r[5])
                 and abs(audio_ms - r[3]) <= CHOICE_EPSILON_MS), None)


def hand_choice(song, difficulty, chart, side, a, b):
    # a/bはcuts()の(time, noteIndex, cutIndex)。ロング中の切断には使わない。
    na, nb = chart["notes"][a[1]], chart["notes"][b[1]]
    if not (a[2] == b[2] == 0 and single_tap(na) and single_tap(nb)
            and na["color"] == nb["color"] == side):
        return None
    offset = chart.get("offsetMs", 0)
    return next((r for r in HAND_CHOICES
                 if (song, difficulty, side) == (r[1], r[2], r[3])
                 and abs(a[0] + offset - r[4]) <= CHOICE_EPSILON_MS
                 and abs(b[0] + offset - r[5]) <= CHOICE_EPSILON_MS
                 and abs(b[0] - a[0] - r[6]) <= CHOICE_EPSILON_MS), None)



# 21A: 青葉騒Normalの冒頭を1回切りにした結果だけ、ロング0個を認める。
LONG_PRESENCE_SONGS = ("ManicSonic", "escapade", "logic", "BenkyouRemaster", "Aobasou")
LONG_PRESENCE_CHOICE = (
    "21A", "Aobasou", "normal", 0.0, "red", .9, -.6,
    "2026-10-23 ユーザーが冒頭を1回切りにすることを選んだ",
)


def is_long(note):
    return note.get("count", 1) > 1 or note.get("type", "").lower() == "long"


def long_presence_choice(song, difficulty, chart):
    row = LONG_PRESENCE_CHOICE
    if (song, difficulty) != row[1:3] or any(is_long(n) for n in chart["notes"]):
        return None
    found = [n for n in chart["notes"]
             if abs(n["time"] + chart.get("offsetMs", 0) - row[3]) <= CHOICE_EPSILON_MS]
    if len(found) != 1:
        return None
    n = found[0]
    return row if (single_tap(n) and
                   (n["color"], n["x"], n["y"]) == row[4:7]) else None

def read(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))

def pulse_map_hash(data):
    # Git の改行変換だけを許容し、空白・数値など内容の変更は検出する。
    return hashlib.sha256(data.replace(b"\r\n", b"\n")).hexdigest()

def cuts(chart):
    result=[]
    for i,n in enumerate(chart["notes"]):
        count=n.get("count",1)
        length=n.get("lengthMs",0)
        for k in range(count):
            result.append((n["time"]+(length*k/(count-1) if count>1 else 0),i,k))
    return sorted(result)

def classify_time(t,pulses,tolerance):
    i=bisect_left(pulses,t)
    indexes=[x for x in (i-1,i) if 0<=x<len(pulses)]
    if not indexes:return None
    nearest=min(indexes,key=lambda x:abs(pulses[x]-t))
    return nearest if abs(pulses[nearest]-t)<=tolerance else None

def hierarchy_issues(easy,normal):
    """This revision's density guard; exact note inclusion is not required."""
    return [f"Normal {key} below Easy" for key in ("notes","flicks")
            if normal[key]<easy[key]]

def audit(chart,difficulty,pulses,quarter_at,subdivisions_at=lambda _: (.5,),music_start=None,song=None):
    issues=[]
    exceptions=[]
    def record(rule, row):
        item={"rule":rule,"choice":row}
        if item not in exceptions:exceptions.append(item)
    def require(value,message):
        if not value:issues.append(message)
    notes=chart["notes"]
    require(bool(notes),"empty chart")
    if not notes:return {"issues":issues}
    require(all(b["time"]>=a["time"] for a,b in zip(notes,notes[1:])),"unsorted notes")
    for i,n in enumerate(notes):
        require(all(math.isfinite(n.get(k,0)) for k in ("time","beat","x","y","lengthMs")),f"non-finite note {i}")
        require(n.get("count",1)>=1,f"invalid count {i}")
        require(abs(n["x"])<=2.5 and abs(n["y"])<=1.5,f"outside field {i}")
        require(n.get("count",1)<=1 or (n["lengthMs"]>0 and n["direction"]=="none"),f"invalid long {i}")
    if song in LONG_PRESENCE_SONGS and not any(is_long(n) for n in notes):
        choice = long_presence_choice(song, difficulty, chart)
        if choice:
            record("long-presence", choice)
        else:
            require(False, "missing long")
    events=cuts(chart)
    times=[]
    groups=[]
    for event in events:
        t=event[0]
        if not times or t-times[-1]>.1:
            times.append(t)
            groups.append([])
        groups[-1].append(event)
    selected=[]
    for group in groups:
        rows=[audio_choice(song,difficulty,chart,notes[i]) if k==0 else None
              for _,i,k in group]
        selected.append(rows if all(rows) else [])
    gaps=[b-a for a,b in zip(times,times[1:])]
    require(max(gaps,default=0)<5000,"gap >= 5000ms")
    if music_start is not None:
        require(times[0]-music_start<5000,"audible introduction gap >= 5000ms")
    phase=[classify_time(t,pulses,min(25,quarter_at(t)*.12)) for t in times]
    off=[]
    for i,(t,p) in enumerate(zip(times,phase)):
        if p is not None:continue
        off.append(t)
        if selected[i]:
            for row in selected[i]:record("fixed-pulse",row)
            continue
        if difficulty=="easy":
            issues.append(f"Easy offbeat at {t:.3f}")
            continue
        valid=(i>0 and i+1<len(times) and phase[i-1] is not None and phase[i+1] is not None
               and phase[i+1]==phase[i-1]+1 and
               min(abs((t-times[i-1])/(times[i+1]-times[i-1])-f)
                   for f in subdivisions_at(t))<=.10)
        require(valid,f"unanchored Normal offbeat at {t:.3f}")
    for i in range(len(phase)-2):
        if not(phase[i] is None and phase[i+1] is not None and phase[i+2] is None):
            continue
        # phaseは変えない。両端とも指定ノーツのときだけ、この拍並びを免除する。
        denied=[j for j in (i,i+2) if not selected[j]]
        if denied:
            issues.append(f"off-on-off at {times[denied[0]]:.3f}")
        else:
            for j in (i,i+2):
                for row in selected[j]:record("off-on-off",row)
    if difficulty=="easy":
        for a,b in zip(times,times[1:]):
            require(b-a>=499.0,f"Easy real-time interval {b-a:.3f} at {a:.3f}")
            # Integrate the local time-varying quarter instead of the headline BPM.
            local_q=min(quarter_at(a+.1),quarter_at(b-.1))
            require(b-a>=local_q-3,f"Easy faster than quarter at {a:.3f}: {b-a:.3f} < {local_q:.3f}")
    flicks=[n for n in notes if n.get("direction","none")!="none"]
    require(len(flicks)>=1,"missing flick")
    require(len(flicks)<=max(1,math.ceil(len(notes)*(.05 if difficulty=="easy" else .12))),"too many flicks")
    for a,b in zip(flicks,flicks[1:]):
        require(b["time"]-a["time"]>= (11000 if difficulty=="easy" else 6000),"flicks too close")
    for n in flicks:
        t=n["time"]
        before=max((u for u in times if u<t-.1),default=t-10000)
        after=min((u for u in times if u>t+.1),default=t+10000)
        require(min(t-before,after-t)>=(739 if difficulty=="easy" else 479),"flick lacks preparation/recovery")
    chord_groups=Counter(round(n["time"],3) for n in notes)
    require(max(chord_groups.values())<=2,"more than two simultaneous notes")
    for side in ("blue","red"):
        sequence=[(t,i,k) for t,i,k in events if
                  notes[i]["color"]==side or
                  (notes[i]["color"] in ("gold","default") and (notes[i]["x"]<0)==(side=="blue"))]
        for a,b in zip(sequence,sequence[1:]):
            dt=b[0]-a[0]
            require(dt>0,f"same-hand chord at {a[0]:.3f}")
            minimum=499 if difficulty=="easy" else 299
            choice=hand_choice(song,difficulty,chart,side,a,b) if dt<minimum else None
            if choice:
                record("same-hand",choice)
            else:
                require(dt>=minimum,f"same hand too fast: {side} {a[0]:.3f} -> {b[0]:.3f}")
    return {
        "notes":len(notes),"cuts":len(events),"attacks":len(times),
        "flicks":len(flicks),"longs":sum(n.get("count",1)>1 for n in notes),
        "chords":sum(x>1 for x in chord_groups.values()),
        "minGapMs":round(min(gaps,default=0),3),"maxGapMs":round(max(gaps,default=0),3),
        "offbeats":len(off),"offbeatTimesMs":off,"issues":issues,"exceptions":exceptions,
        "heights":len(set(round(n["y"],1) for n in notes)),
        "firstMs":times[0],"lastMs":times[-1],
    }

def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--candidate",type=Path,required=True)
    parser.add_argument("--source",type=Path,required=True)
    parser.add_argument("--output",type=Path,required=True)
    args=parser.parse_args()
    design=read(args.candidate/"Tools/ChartDesign/Beginner20261021/design.json")
    results={}
    all_issues=[]
    frozen_path=args.candidate/"Tools/ChartDesign/Beginner20261021/pulse-map.json"
    frozen=read(frozen_path)
    if pulse_map_hash(frozen_path.read_bytes())!=design["pulseMapSha256"]:
        all_issues.append("frozen pulse map hash differs")
    for song,info in design["songs"].items():
        results[song]={}
        if info["pulseTimesMs"]!=frozen["songs"][song]["pulseTimesMs"]:
            all_issues.append(song+": generated pulse list differs from frozen evidence")
        for diff in ("easy","normal"):
            chart=read(args.candidate/"Assets/StreamingAssets/Songs"/song/f"chart_{diff}.json")
            old=read(args.source/song/f"chart_{diff}.json")
            # For tempo changes use actual chart anchors; school song's quarter
            # is reconstructed independently from the preserved original beat/time table.
            anchors=chart.get("editorTempoMap") or []
            if song=="Epilogue":
                pairs={}
                for difficulty in ("easy","normal","hard"):
                    for n in read(args.source/song/f"chart_{difficulty}.json")["notes"]:
                        pairs[n["beat"]]=n["time"]
                bs=sorted(pairs);ts=[pairs[b] for b in bs]
            else:
                bs=[p["beat"] for p in anchors];ts=[p["timeMs"] for p in anchors]
            def quarter_at(t):
                if song=="RushE" and info.get("musicPulseQuarterMs"):
                    j=max(0,min(len(info["musicPulseQuarterMs"])-1,bisect_left(info["pulseTimesMs"],t)-1))
                    return info["musicPulseQuarterMs"][j]
                if len(ts)<2:return 60000/chart["bpm"]
                j=max(0,min(len(ts)-2,bisect_left(ts,t)-1))
                return (ts[j+1]-ts[j])/(bs[j+1]-bs[j])
            def subdivisions_at(t):
                if len(ts)>=2:
                    j=max(0,min(len(ts)-2,bisect_left(ts,t)-1))
                    beat=bs[j]+(t-ts[j])*(bs[j+1]-bs[j])/(ts[j+1]-ts[j])
                else:
                    beat=(t-chart.get("beatZeroMs",0))*chart["bpm"]/60000
                meter=next((s for s in reversed(chart.get("timeSignatures",[])) if s["beat"]<=beat),
                           {"numerator":4,"denominator":4})
                return (1/3,2/3) if meter["numerator"]==6 and meter["denominator"]==8 else (.5,)
            result=audit(chart,diff,info["pulseTimesMs"],quarter_at,subdivisions_at,info["musicStartMs"],song=song)
            for key in ("bpm","beatZeroMs","offsetMs","coordScale","displayLevel","timeSignatures","editorTempoMap"):
                if chart.get(key)!=old.get(key):result["issues"].append("changed timing/level metadata: "+key)
            plan=read(args.candidate/"Tools/ChartDesign/Beginner20261021/plans"/song/f"{diff}.json")
            if len(plan["notes"])!=len(chart["notes"]):result["issues"].append("plan row count")
            else:
                for i,(n,r) in enumerate(zip(chart["notes"],plan["notes"])):
                    actual=[x[0] for x in cuts({"notes":[n]})]
                    if len(actual)!=len(r["timesMs"]) or any(abs(a-b)>.1 for a,b in zip(actual,r["timesMs"])):
                        result["issues"].append(f"long cut plan mismatch {i}")
            if diff=="normal":
                compat=args.candidate/"Assets/StreamingAssets/Songs"/song/"chart.json"
                if compat.read_bytes()!=(args.candidate/"Assets/StreamingAssets/Songs"/song/f"chart_{diff}.json").read_bytes():
                    result["issues"].append("fallback differs from Normal")
            results[song][diff]=result
            all_issues.extend(f"{song}/{diff}: {msg}" for msg in result["issues"])
            print(song,diff,result["notes"],result["flicks"],result["longs"],result["maxGapMs"],
                  "OK" if not result["issues"] else result["issues"][:5],flush=True)
        all_issues.extend(f"{song}: {msg}" for msg in hierarchy_issues(results[song]["easy"],results[song]["normal"]))
    report={"passed":not all_issues,"issues":all_issues,"charts":results}
    args.output.write_text(json.dumps(report,ensure_ascii=False,indent=2)+"\n",encoding="utf-8")
    raise SystemExit(0 if not all_issues else 1)

if __name__=="__main__":
    main()
