#!/usr/bin/env python3
"""Mac のゲーム（Unity Player）の events.log から、1回の遊びを曲ごとにまとめる。

読むもの（どれも欠けていてよい）:
  event=perf              10秒ごと。scene / fps / frame-ms-max / frames-over-25ms / pkt/s など
  event=latency-estimate  色ごとに約10秒ごと。c2s / rtt / network / pc / display / exposure / total
  event=judge-timing      判定ごと。tier / hand / judge-ms / song-ms / latency-ms / hit-s
  event=judge-latency     補正の設定（あればそのまま要約する）
  input-silent / input-back / sender-change / session-start / session-quit

使い方:
  python3 play_report.py --last-session
  python3 play_report.py --last-session --baseline           # good-20261020 の状態チェック
  python3 play_report.py --since 16:46 --until 16:50          # ローカル時刻（今日）
  python3 play_report.py --since 2026-10-17T07:46Z --until 2026-10-17T07:50Z --json out.json
  python3 play_report.py --compare 16:30..16:40 16:46..16:50  # 変更の前後を並べる

python3 の標準ライブラリだけで動く。読めない行は黙って飛ばす。
"""
import argparse
from datetime import datetime, timedelta, timezone
import json
import math
import os
import statistics
import sys
import unicodedata

DEFAULT_EVENTS = os.path.expanduser(
    "~/Library/Application Support/DefaultCompany/ServTechSlash/PhoneSaber/events.log")
GAME_SCENES = {"Game"}
PERF_INTERVAL_S = 10.0
JUDGE_GAP_S = 15.0
TETHERING_HOSTS = {"172.20.10.1"}
TIERS = ("Perfect", "Great", "Good", "Bad", "Miss")
COMP_MISMATCH_MS = 25.0
UNKNOWN = {"", "unknown", "none", "nan", "-", "--", "n/a"}
BASELINE = "good-20261020"
BASELINE_LIMITS = {"capture_fps": 58, "red_pkt_s": 50, "blue_pkt_s": 50,
                   "c2s_ms": 40, "total_ms": 65, "both_gaps": 0, "pc_over50": 1}


# ---------------------------------------------------------------- 読み込み

def parse_time(text):
    """ログ・引数の ISO 時刻を UTC の aware datetime にする。読めなければ None。"""
    if not text:
        return None
    s = text.strip()
    if s.endswith("Z") or s.endswith("z"):
        s = s[:-1] + "+00:00"
    try:
        value = datetime.fromisoformat(s)
    except ValueError:
        return None
    if value.tzinfo is None:
        value = value.replace(tzinfo=timezone.utc)
    return value.astimezone(timezone.utc)


def parse_line(line):
    """1行を {'time', 'event', 'station', 'colour', 'fields'} にする。壊れた行は None。"""
    if not isinstance(line, str):
        return None
    parts = line.strip().split()
    if len(parts) < 2:
        return None
    when = parse_time(parts[0])
    if when is None:
        return None
    fields = {}
    for token in parts[1:]:
        if "=" not in token:
            continue
        key, value = token.split("=", 1)
        if key and key not in fields:
            fields[key] = value
    event = fields.get("event")
    if not event:
        return None
    return {"time": when, "event": event, "station": fields.get("station", ""),
            "colour": fields.get("colour", ""), "fields": fields}


def load_events(path):
    """単一ログ＋世代、またはディレクトリ内（archive を含む）の全ファイルを時刻順で読む。"""
    directory = os.path.isdir(path)
    if directory:
        names = sorted(os.path.join(root, name)
                       for root, _, files in os.walk(path) for name in files)
        # archive は複写元の本体ログと同じ行を持つ。それぞれを一群にして比較する。
        groups = [[name for name in names if os.path.basename(name).startswith("events-")],
                  [name for name in names if not os.path.basename(name).startswith("events-")]]
    else:
        groups = [[f"{path}.{n}" for n in range(9, 0, -1) if os.path.isfile(f"{path}.{n}")] + [path]]
    events = []
    seen = {}
    for names in groups:
        occurrences = {}
        for name in names:
            with open(name, "r", encoding="utf-8", errors="replace") as handle:
                for line in handle:
                    row = parse_line(line)
                    if row is None:
                        continue
                    # 複写の二重集計だけを除く。同一群内の同じ行は、世代を跨いでも保持する。
                    key = line.rstrip("\r\n")
                    occurrences[key] = occurrences.get(key, 0) + 1
                    if occurrences[key] > seen.get(key, 0):
                        events.append(row)
        for key, count in occurrences.items():
            seen[key] = max(seen.get(key, 0), count)
    events.sort(key=lambda r: r["time"])  # sort は安定なので同時刻は読んだ順
    return events


def num(value):
    """数値なら float、unknown・空・nan・inf なら None。"""
    if value is None:
        return None
    if isinstance(value, (int, float)):
        out = float(value)
    else:
        if str(value).strip().lower() in UNKNOWN:
            return None
        try:
            out = float(str(value).rstrip("%"))
        except ValueError:
            return None
    return out if math.isfinite(out) else None


def text(value):
    if value is None or str(value).strip().lower() in UNKNOWN:
        return None
    return str(value)


# ---------------------------------------------------------------- 統計

def percentile(values, q):
    vals = sorted(v for v in values if v is not None)
    if not vals:
        return None
    if len(vals) == 1:
        return vals[0]
    pos = (len(vals) - 1) * q / 100.0
    low = math.floor(pos)
    high = math.ceil(pos)
    return vals[low] + (vals[high] - vals[low]) * (pos - low)


def median(values):
    vals = [v for v in values if v is not None]
    return statistics.median(vals) if vals else None


def mean(values):
    vals = [v for v in values if v is not None]
    return statistics.fmean(vals) if vals else None


def distribution(values):
    out = {}
    for v in values:
        key = v if v is not None else "unknown"
        out[key] = out.get(key, 0) + 1
    return dict(sorted(out.items(), key=lambda kv: (-kv[1], str(kv[0]))))


# ---------------------------------------------------------------- 時間窓

def parse_time_arg(value, now=None):
    """ISO（Z なしは UTC）か、今日の 'HH:MM[:SS]'（ローカル。末尾 Z なら UTC）。"""
    if value is None:
        return None
    s = value.strip()
    if not s:
        return None
    if "T" not in s and "-" not in s and ":" in s:
        utc = s[-1:] in ("Z", "z")
        bits = (s[:-1] if utc else s).split(":")
        try:
            nums = [int(b) for b in bits]
        except ValueError:
            raise ValueError(f"時刻が読めません: {value}")
        if not 2 <= len(nums) <= 3:
            raise ValueError(f"時刻が読めません: {value}")
        base = (now or datetime.now(timezone.utc))
        local_now = base.astimezone(timezone.utc) if utc else base.astimezone()
        when = local_now.replace(hour=nums[0], minute=nums[1],
                                 second=nums[2] if len(nums) == 3 else 0, microsecond=0)
        return when.astimezone(timezone.utc)
    when = parse_time(s)
    if when is None:
        raise ValueError(f"時刻が読めません: {value}")
    return when


def parse_window_spec(spec, now=None):
    """'SINCE..UNTIL'（どちらか省略可）を (since, until) にする。"""
    if ".." in spec:
        left, right = spec.split("..", 1)
    else:
        left, right = spec, ""
    return parse_time_arg(left, now) if left else None, parse_time_arg(right, now) if right else None


def last_session_start(events):
    starts = [e["time"] for e in events if e["event"] == "session-start"]
    return starts[-1] if starts else (events[0]["time"] if events else None)


def in_window(events, since, until):
    return [e for e in events
            if (since is None or e["time"] >= since) and (until is None or e["time"] <= until)]


# ---------------------------------------------------------------- 曲の切り出し

def find_songs(events):
    """perf scene=Game の連続、または judge-timing の固まりを1曲とする。[(start, end)]。"""
    intervals = []
    perf = [e for e in events if e["event"] == "perf"]
    run = []
    for index, row in enumerate(perf):
        if row["fields"].get("scene") in GAME_SCENES:
            run.append(row)
            continue
        if run:
            start = run[0]["time"] - timedelta(seconds=PERF_INTERVAL_S)
            end = min(row["time"], run[-1]["time"] + timedelta(seconds=PERF_INTERVAL_S))
            intervals.append([start, end])
            run = []
    if run:
        intervals.append([run[0]["time"] - timedelta(seconds=PERF_INTERVAL_S),
                          run[-1]["time"] + timedelta(seconds=PERF_INTERVAL_S)])

    # judge-timing の固まり（時間が空くか hit-s が巻き戻ったら別の曲）
    bursts = []
    prev = None
    for row in (e for e in events if e["event"] == "judge-timing"):
        hit = num(row["fields"].get("hit-s"))
        new_song = (prev is None
                    or (row["time"] - prev["time"]).total_seconds() > JUDGE_GAP_S
                    or (hit is not None and prev["hit"] is not None and hit + 5.0 < prev["hit"]))
        if new_song:
            bursts.append([row["time"], row["time"]])
        else:
            bursts[-1][1] = row["time"]
        prev = {"time": row["time"], "hit": hit if hit is not None else (prev or {}).get("hit")}

    perf_intervals = list(intervals)
    for start, end in bursts:
        hit_perf = [iv for iv in perf_intervals
                    if start <= iv[1] + timedelta(seconds=1) and end >= iv[0] - timedelta(seconds=1)]
        if hit_perf:
            for iv in hit_perf:
                iv[0] = min(iv[0], start)
                iv[1] = max(iv[1], end)
        else:
            intervals.append([start - timedelta(seconds=1), end + timedelta(seconds=1)])

    intervals.sort()
    merged = []
    for start, end in intervals:
        if merged and start < merged[-1][1]:
            merged[-1][1] = max(merged[-1][1], end)
        else:
            merged.append([start, end])
    return [(s, e) for s, e in merged]


# ---------------------------------------------------------------- 集計

def gap_intervals(events, end):
    """input-silent から input-back までを途切れとして数える（色ごと）。"""
    open_gap = {}
    gaps = {}
    for row in events:
        colour = row["colour"] or "?"
        if row["event"] == "input-silent":
            already = num(row["fields"].get("gap-seconds")) or 0.0
            if colour not in open_gap:
                open_gap[colour] = row["time"] - timedelta(seconds=already)
        elif row["event"] == "input-back" and colour in open_gap:
            gaps.setdefault(colour, []).append((open_gap.pop(colour), row["time"]))
        elif row["event"] in ("session-quit", "session-start"):
            for key in list(open_gap):  # ゲームが止まったら途切れも終わり
                gaps.setdefault(key, []).append((open_gap.pop(key), row["time"]))
    for colour, began in open_gap.items():
        gaps.setdefault(colour, []).append((began, end))
    return gaps


def input_gaps(events, start, end):
    gaps = gap_intervals(events, end)
    out = {}
    for colour, items in gaps.items():
        seconds = 0.0
        count = 0
        for g0, g1 in items:
            lo, hi = max(g0, start), min(g1, end)
            if hi > lo:
                seconds += (hi - lo).total_seconds()
                count += 1
        if count:
            out[colour] = {"count": count, "seconds": round(seconds, 1)}
    return out


def baseline_check(events, start, end):
    """基準外は WARN、重大な発熱・両色同時停止・PC停止超過は BAD。欠測は WARN。

    10秒ごとの悪化を中央値で隠さない。c2s は各標本の中央値の最大値。
    """
    rows = in_window(events, start, end)
    perf = [e["fields"] for e in rows if e["event"] == "perf" and e["fields"].get("scene") in GAME_SCENES]
    estimates = [e["fields"] for e in rows if e["event"] == "latency-estimate"]
    checks = {}

    def metric(name, fields, key, minimum=False):
        values = [num(f.get(key)) for f in fields]
        known = [v for v in values if v is not None and v >= 0]
        value = (min(known) if minimum else max(known)) if known else None
        limit = BASELINE_LIMITS[name]
        ok = value is not None and (value >= limit if minimum else value <= limit)
        checks[name] = {"value": value, "grade": "OK" if ok and len(known) == len(values) else "WARN"}

    metric("capture_fps", estimates, "capture-fps", True)
    metric("red_pkt_s", perf, "red-pkt-per-s", True)
    metric("blue_pkt_s", perf, "blue-pkt-per-s", True)
    metric("c2s_ms", estimates, "c2s-median-ms")
    metric("total_ms", estimates, "total-ms")
    thermal = [f.get("thermal", "unknown") for f in estimates]
    checks["thermal"] = {"value": sorted(set(thermal)), "grade":
        "BAD" if any(v in ("serious", "critical") for v in thermal) else
        "OK" if thermal and all(v == "nominal" for v in thermal) else "WARN"}
    gaps = gap_intervals(events, end)
    both = sum((min(r1, b1, end) - max(r0, b0, start)).total_seconds() > 1
               for r0, r1 in gaps.get("RED", []) for b0, b1 in gaps.get("BLUE", []))
    # perf が無い曲では、途切れゼロも証明できない。
    checks["both_gaps"] = {"value": both, "grade": "BAD" if both else "OK" if perf else "WARN"}
    frames = [num(f.get("frames-over-50ms")) for f in perf]
    count = sum(v for v in frames if v is not None and v >= 0)
    checks["pc_over50"] = {"value": count if frames else None, "grade":
        "BAD" if count > 1 else "OK" if frames and all(v is not None and v >= 0 for v in frames) else "WARN"}
    return {"name": BASELINE, "grade": worst_grade(c["grade"] for c in checks.values()), "checks": checks}


def worst_grade(grades):
    return max(grades, key=("OK", "WARN", "BAD").index, default="WARN")


def baseline_line(summary):
    result = summary["baseline"]
    names = {"capture_fps": "撮影fps最小", "red_pkt_s": "赤pkt/s最小", "blue_pkt_s": "青pkt/s最小",
             "c2s_ms": "c2s中央値最大ms", "total_ms": "合計推定最大ms", "both_gaps": "両色gap>1秒",
             "thermal": "発熱", "pc_over50": "Game中>50ms"}
    details = " / ".join(f"{names[k]}={','.join(c['value']) if isinstance(c['value'], list) else fmt(c['value'])}"
                         + (f"({c['grade']})" if c["grade"] != "OK" else "")
                         for k, c in result["checks"].items())
    return f"状態チェック {summary['label']}: {result['grade']} [{result['name']}] {details}"


def stat(values, digits=1):
    vals = [v for v in values if v is not None]
    if not vals:
        return {"n": 0, "median": None, "p95": None}
    return {"n": len(vals), "median": round(median(vals), digits),
            "p95": round(percentile(vals, 95), digits)}


def summarize(all_events, start, end, label=""):
    events = in_window(all_events, start, end)
    perf = [e["fields"] for e in events if e["event"] == "perf"]
    game_perf = [f for f in perf if f.get("scene") in GAME_SCENES] or perf

    # latency-estimate は同じ送信元・同じ秒・同じ値なら赤青で1回だけ数える
    seen = set()
    est = []
    est_rows = [e for e in events if e["event"] == "latency-estimate"]
    for row in est_rows:
        f = row["fields"]
        key = (f.get("sender"), row["time"].strftime("%Y%m%dT%H%M%S"),
               f.get("c2s-median-ms"), f.get("rtt-ms"), f.get("pc-median-ms"))
        if num(f.get("c2s-median-ms")) is None and num(f.get("rtt-ms")) is None \
                and num(f.get("pc-median-ms")) is None:
            continue  # missing-phone など中身のない行
        if key in seen:
            continue
        seen.add(key)
        est.append(f)

    judges = [e["fields"] for e in events if e["event"] == "judge-timing"]
    tiers = {}
    for f in judges:
        tier = text(f.get("tier")) or "unknown"
        tiers[tier] = tiers.get(tier, 0) + 1
    hits = [f for f in judges if text(f.get("tier")) not in (None, "Miss")]
    # 10-18 昼より前の judge-ms は「確定したフレーム」の時刻で、ゲームの誤差と一致しない（decided-ms 列が無い行）。
    hit_ms = [num(f.get("judge-ms")) for f in hits if "decided-ms" in f]
    hit_ms = [v for v in hit_ms if v is not None]
    song_ms = [num(f.get("song-ms")) for f in hits]
    comps = [num(f.get("latency-ms")) for f in judges]
    comp_values = sorted({v for v in comps if v is not None})
    comp_now = median(comps)
    hit_median = median(hit_ms)

    judge_latency = []
    for e in events:
        if e["event"] == "judge-latency":
            f = e["fields"]
            judge_latency.append({k: f.get(k) for k in ("mode", "applied-ms", "auto-ms", "source")})
    jl_combos = distribution([" ".join(f"{k}={v}" for k, v in j.items() if v is not None)
                              for j in judge_latency])

    senders = distribution([text(f.get("sender")) for f in est])
    sender_changes = [e["fields"].get("to") for e in events if e["event"] == "sender-change"]
    all_senders = set(k for k in senders if k != "unknown") | set(s for s in sender_changes if text(s))

    pkt = {}
    for colour, key in (("RED", "red-pkt-per-s"), ("BLUE", "blue-pkt-per-s")):
        vals = [num(f.get(key)) for f in game_perf]
        vals = [v for v in vals if v is not None]
        pkt[colour] = {"median": round(median(vals), 1) if vals else None,
                       "min": round(min(vals), 1) if vals else None}

    fps_vals = [num(f.get("fps")) for f in game_perf]
    over25 = [num(f.get("frames-over-25ms")) for f in game_perf]
    over50 = [num(f.get("frames-over-50ms")) for f in game_perf]
    fmax = [num(f.get("frame-ms-max")) for f in game_perf]

    summary = {
        "label": label,
        "start": start.isoformat() if start else None,
        "end": end.isoformat() if end else None,
        "duration_s": round((end - start).total_seconds(), 1) if start and end else None,
        "phone": {
            "platform": distribution([text(f.get("platform")) for f in est]),
            "fps_mode": distribution([text(f.get("fps-mode")) for f in est]),
            "thermal": distribution([text(f.get("thermal")) for f in est]),
            "c2s_median_ms": stat([num(f.get("c2s-median-ms")) for f in est]),
            "c2s_p95_ms": stat([num(f.get("c2s-p95-ms")) for f in est]),
            "exposure_ms": stat([num(f.get("exposure-phase-ms")) for f in est]),
            "capture_fps": stat([num(f.get("capture-fps")) for f in est]),
            "pkt_per_s": pkt,
            "input_gaps": input_gaps(all_events, start, end) if start and end else {},
        },
        "network": {
            "rtt_ms": stat([num(f.get("rtt-ms")) for f in est]),
            "network_ms": stat([num(f.get("network-ms")) for f in est]),
            "route": distribution([text(f.get("route")) for f in est]),
            "sender": senders,
            "sender_changes": len(sender_changes),
            "tethering_host": sorted(all_senders & TETHERING_HOSTS),
        },
        "pc": {
            "pc_median_ms": stat([num(f.get("pc-median-ms")) for f in est]),
            "pc_p95_ms": stat([num(f.get("pc-p95-ms")) for f in est]),
            "render_fps": {"median": round(median(fps_vals), 1) if median(fps_vals) is not None else None,
                           "min": min([v for v in fps_vals if v is not None], default=None)},
            "frames_over_25ms": int(sum(v for v in over25 if v is not None)),
            "frames_over_50ms": int(sum(v for v in over50 if v is not None)),
            "frame_ms_max": max([v for v in fmax if v is not None], default=None),
            "perf_samples": len(game_perf),
            "scenes": distribution([text(f.get("scene")) for f in perf]),
        },
        "display_ms": stat([num(f.get("display-ms")) for f in est]),
        "display_source": distribution([text(f.get("display-source")) for f in est]),
        "total_ms": stat([num(f.get("total-ms")) for f in est]),
        "judge": {
            "n": len(judges),
            "tiers": {t: tiers.get(t, 0) for t in TIERS if tiers.get(t)} | {
                k: v for k, v in tiers.items() if k not in TIERS},
            "hits": len(hit_ms),
            "judge_ms_median": r1(hit_median),
            "judge_ms_mean": r1(mean(hit_ms)),
            "judge_ms_p10": r1(percentile(hit_ms, 10)),
            "judge_ms_p90": r1(percentile(hit_ms, 90)),
            "early": sum(1 for v in hit_ms if v < 0),
            "late": sum(1 for v in hit_ms if v > 0),
            "on_time": sum(1 for v in hit_ms if v == 0),
            "song_ms_median": r1(median(song_ms)),
            "compensation_ms_seen": comp_values,
            "compensation_ms": r1(comp_now),
            "best_compensation_ms": r1(best_compensation(comp_now, hit_median)),
        },
        "judge_latency": {"n": len(judge_latency), "settings": jl_combos,
                          "last": judge_latency[-1] if judge_latency else None},
    }
    summary["contributions"] = contributions(summary)
    summary["problems"] = problems(summary)
    return summary


def r1(value):
    return None if value is None else round(value, 1)


def best_compensation(current, hit_median):
    """当たりの中央値を 0 にする補正（推定）。judge = song − 補正 なので 補正 + 中央値。"""
    if current is None or hit_median is None:
        return None
    return current + hit_median


def contributions(s):
    parts = [
        ("スマホ c2s（撮影→送信）", s["phone"]["c2s_median_ms"]["median"]),
        ("露出（シャッター位相）", s["phone"]["exposure_ms"]["median"]),
        ("ネットワーク", s["network"]["network_ms"]["median"]),
        ("PC（受信→描画）", s["pc"]["pc_median_ms"]["median"]),
        ("表示", s["display_ms"]["median"]),
    ]
    parts = [(name, ms) for name, ms in parts if ms is not None]
    total = sum(ms for _, ms in parts)
    parts.sort(key=lambda p: -p[1])
    return [{"name": name, "ms": ms, "share": round(100.0 * ms / total, 1) if total > 0 else None}
            for name, ms in parts]


def problems(s):
    out = []
    phone, net, pc, judge = s["phone"], s["network"], s["pc"], s["judge"]
    low_fps = {k: v for k, v in phone["fps_mode"].items() if num(k) is not None and num(k) <= 30}
    if low_fps:
        n = sum(low_fps.values())
        out.append(f"スマホが 30fps で動いている（{n}/{sum(phone['fps_mode'].values())} 回）。"
                   "c2s が 1 フレーム分（約 17 ms）以上長くなる")
    hot = {k: v for k, v in phone["thermal"].items() if k in ("serious", "critical")}
    if hot:
        out.append("スマホが熱い: " + ", ".join(f"thermal={k} {v} 回" for k, v in hot.items())
                   + "。30fps 落ちや送信の途切れの原因になる")
    if net["tethering_host"]:
        out.append(f"送信元 {', '.join(net['tethering_host'])} は iPhone のインターネット共有（親機）。"
                   "iPhone が電波の親になっていて発熱する。会場の Wi-Fi / ルーターにつなぐ")
    gaps = phone["input_gaps"]
    if gaps:
        out.append("入力の途切れ: " + ", ".join(
            f"{c} {g['count']} 回 計 {g['seconds']:.1f} 秒" for c, g in sorted(gaps.items())))
    for colour, p in phone["pkt_per_s"].items():
        if p["min"] is not None and p["median"] is not None and p["min"] < 0.6 * p["median"]:
            out.append(f"{colour} の受信が一時的に落ちた（最小 {p['min']:.1f} pkt/s、中央値 {p['median']:.1f}）")
    if pc["frames_over_50ms"] > 0 or (pc["frame_ms_max"] or 0) > 50:
        out.append(f"描画の引っかかり: 50 ms 超 {pc['frames_over_50ms']} 回、"
                   f"25 ms 超 {pc['frames_over_25ms']} 回、最大 {fmt(pc['frame_ms_max'])} ms")
    if judge["judge_ms_median"] is not None and judge["compensation_ms"] is not None \
            and abs(judge["judge_ms_median"]) > COMP_MISMATCH_MS:
        side = "早い" if judge["judge_ms_median"] < 0 else "遅い"
        out.append(f"補正ずれ: 当たりが中央値 {judge['judge_ms_median']:+.0f} ms {side}。"
                   f"補正 {judge['compensation_ms']:.0f} ms → 推定 {judge['best_compensation_ms']:.0f} ms が中心")
    total = s["total_ms"]["median"]
    if total is not None and judge["compensation_ms"] is not None \
            and abs(judge["compensation_ms"] - total) > COMP_MISMATCH_MS:
        out.append(f"補正 {judge['compensation_ms']:.0f} ms と遅れの推定合計 {total:.0f} ms が "
                   f"{abs(judge['compensation_ms'] - total):.0f} ms 離れている")
    return out


# ---------------------------------------------------------------- 表示

def fmt(value, digits=1):
    if value is None:
        return "-"
    if isinstance(value, bool) or isinstance(value, int):
        return str(value)
    if isinstance(value, float) and value.is_integer() and digits <= 1:
        return f"{value:.0f}" if abs(value) >= 100 else f"{value:.{digits}f}"
    if isinstance(value, (int, float)):
        return f"{value:.{digits}f}"
    return str(value)


def width(value):
    return sum(2 if unicodedata.east_asian_width(ch) in ("W", "F") else 1 for ch in value)


def pad_left(value, size):
    return value + " " * max(0, size - width(value))


def pad_right(value, size):
    return " " * max(0, size - width(value)) + value


def fmt_dist(dist):
    return ", ".join(f"{k}×{v}" for k, v in dist.items()) if dist else "-"


def fmt_utc(iso):
    when = parse_time(iso) if iso else None
    return when.strftime("%Y-%m-%dT%H:%M:%SZ") if when else "-"


def fmt_local(iso):
    when = parse_time(iso) if iso else None
    if when is None:
        return "-"
    return when.astimezone().strftime("%H:%M:%S")


def render(s):
    phone, net, pc, judge = s["phone"], s["network"], s["pc"], s["judge"]
    lines = [f"■ {s['label']}  {fmt_local(s['start'])}〜{fmt_local(s['end'])}（ローカル）"
             f"  {fmt_utc(s['start'])} 〜 {fmt_utc(s['end'])}  {fmt(s['duration_s'])} 秒"]
    gaps = phone["input_gaps"]
    gap_text = ", ".join(f"{c} {g['count']} 回 {g['seconds']:.1f} 秒" for c, g in sorted(gaps.items())) or "なし"
    pkt = phone["pkt_per_s"]
    lines += [
        "  [スマホ]",
        f"    機種 {fmt_dist(phone['platform'])} / fps {fmt_dist(phone['fps_mode'])} / thermal {fmt_dist(phone['thermal'])}",
        f"    c2s 中央値 {fmt(phone['c2s_median_ms']['median'])} ms / p95 {fmt(phone['c2s_p95_ms']['median'])} ms"
        f"（{phone['c2s_median_ms']['n']} 回）  露出 {fmt(phone['exposure_ms']['median'])} ms",
        f"    受信 pkt/s  赤 {fmt(pkt['RED']['median'])}（最小 {fmt(pkt['RED']['min'])}）"
        f"  青 {fmt(pkt['BLUE']['median'])}（最小 {fmt(pkt['BLUE']['min'])}）",
        f"    入力の途切れ {gap_text}",
        "  [ネットワーク]",
        f"    rtt 中央値 {fmt(net['rtt_ms']['median'])} / p95 {fmt(net['rtt_ms']['p95'])} ms"
        f"  片道 {fmt(net['network_ms']['median'])} / p95 {fmt(net['network_ms']['p95'])} ms",
        f"    経路 {fmt_dist(net['route'])} / 送信元 {fmt_dist(net['sender'])}"
        f" / 送信元の切替 {net['sender_changes']} 回",
    ]
    if net["tethering_host"]:
        lines.append(f"    ！警告: 送信元 {', '.join(net['tethering_host'])} = iPhone がインターネット共有の親機（発熱の原因）")
    lines += [
        "  [PC]",
        f"    pc 中央値 {fmt(pc['pc_median_ms']['median'])} ms / p95 {fmt(pc['pc_p95_ms']['median'])} ms"
        f"  描画 {fmt(pc['render_fps']['median'])} fps（最小 {fmt(pc['render_fps']['min'])}）",
        f"    25 ms 超 {pc['frames_over_25ms']} 回 / 50 ms 超 {pc['frames_over_50ms']} 回"
        f" / 最大 {fmt(pc['frame_ms_max'])} ms（perf {pc['perf_samples']} 回）",
        f"  [合計の推定] 中央値 {fmt(s['total_ms']['median'])} ms / p95 {fmt(s['total_ms']['p95'])} ms"
        f"（表示 {fmt(s['display_ms']['median'])} ms, {fmt_dist(s['display_source'])}）",
        "  [判定]",
    ]
    if judge["n"]:
        tiers = " ".join(f"{k} {v}" for k, v in judge["tiers"].items())
        lines += [
            f"    {judge['n']} 回: {tiers}",
            f"    当たり {judge['hits']} 回の judge-ms: 中央値 {fmt(judge['judge_ms_median'])}"
            f" / 平均 {fmt(judge['judge_ms_mean'])} / p10 {fmt(judge['judge_ms_p10'])}"
            f" / p90 {fmt(judge['judge_ms_p90'])}（正 = 遅い）",
            f"    早い {judge['early']} / 遅い {judge['late']} / ちょうど {judge['on_time']}"
            f"  song-ms 中央値 {fmt(judge['song_ms_median'])}",
            f"    使った補正 latency-ms: {', '.join(fmt(v) for v in judge['compensation_ms_seen']) or '-'}",
            f"    最適な補正（推定）= 今の補正 {fmt(judge['compensation_ms'])} + 当たりの中央値"
            f" {fmt(judge['judge_ms_median'])} = {fmt(judge['best_compensation_ms'])} ms",
        ]
    else:
        lines.append("    判定の記録なし（event=judge-timing がない）")
    jl = s["judge_latency"]
    if jl["n"]:
        lines.append(f"    judge-latency {jl['n']} 回: {fmt_dist(jl['settings'])}")
    lines.append("  [主な遅れの原因]")
    for i, part in enumerate(s["contributions"], 1):
        lines.append(f"    {i}. {part['name']} {fmt(part['ms'])} ms（{fmt(part['share'])}%）")
    if not s["contributions"]:
        lines.append("    遅れの内訳なし（latency-estimate がない）")
    if s["problems"]:
        lines.append("    見つかった問題:")
        lines += [f"     - {p}" for p in s["problems"]]
    else:
        lines.append("    見つかった問題: なし")
    return "\n".join(lines)


def build_report(events, since, until, baseline=False):
    window = in_window(events, since, until)
    start = since or (window[0]["time"] if window else None)
    end = until or (window[-1]["time"] if window else None)
    songs = find_songs(window)
    song_summaries = [summarize(events, max(a, start) if start else a, min(b, end) if end else b,
                                f"曲 {i}") for i, (a, b) in enumerate(songs, 1)]
    overall = summarize(events, start, end, "全体") if start and end else None
    if baseline and overall:
        for song in song_summaries:
            song["baseline"] = baseline_check(events, parse_time(song["start"]), parse_time(song["end"]))
        # 全体は各曲の最悪値。曲間の休憩・発熱を曲中の途切れとして扱わない。
        checks = {}
        for key in (*BASELINE_LIMITS, "thermal"):
            candidates = [s["baseline"]["checks"][key] for s in song_summaries]
            def severity(check):
                value = num(check["value"]) if not isinstance(check["value"], list) else None
                direction = -1 if key in ("capture_fps", "red_pkt_s", "blue_pkt_s") else 1
                return ("OK", "WARN", "BAD").index(check["grade"]), direction * value if value is not None else 0
            checks[key] = max(candidates, key=severity) if candidates else {"value": None, "grade": "WARN"}
        overall["baseline"] = {"name": BASELINE, "checks": checks,
                               "grade": worst_grade(c["grade"] for c in checks.values())}
    return {"since": start.isoformat() if start else None, "until": end.isoformat() if end else None,
            "songs": song_summaries, "overall": overall}


def render_report(report):
    if report["overall"] is None:
        return "この時間に記録がありません。"
    out = [f"PhoneSaber プレイ報告  {len(report['songs'])} 曲"]
    for summary in [*report["songs"], report["overall"]]:
        if "baseline" in summary:
            out.append(baseline_line(summary))
    for song in report["songs"]:
        out += ["", render(song)]
    out += ["", render(report["overall"])]
    out.append("\n※ 最適な補正は判定の中央値から出した推定。曲や人で変わるので、何曲か見てから決める。")
    return "\n".join(out)


# ---------------------------------------------------------------- 比較

COMPARE_ROWS = [
    ("曲数", lambda r: len(r["songs"])),
    ("fps 30 の割合 %", lambda r: share_of(r["overall"]["phone"]["fps_mode"], lambda k: (num(k) or 99) <= 30)),
    ("thermal serious+ の割合 %", lambda r: share_of(r["overall"]["phone"]["thermal"],
                                                   lambda k: k in ("serious", "critical"))),
    ("c2s 中央値 ms", lambda r: r["overall"]["phone"]["c2s_median_ms"]["median"]),
    ("c2s p95 ms", lambda r: r["overall"]["phone"]["c2s_p95_ms"]["median"]),
    ("露出 ms", lambda r: r["overall"]["phone"]["exposure_ms"]["median"]),
    ("赤 pkt/s", lambda r: r["overall"]["phone"]["pkt_per_s"]["RED"]["median"]),
    ("青 pkt/s", lambda r: r["overall"]["phone"]["pkt_per_s"]["BLUE"]["median"]),
    ("途切れ 回", lambda r: sum(g["count"] for g in r["overall"]["phone"]["input_gaps"].values())),
    ("途切れ 秒", lambda r: round(sum(g["seconds"] for g in r["overall"]["phone"]["input_gaps"].values()), 1)),
    ("rtt 中央値 ms", lambda r: r["overall"]["network"]["rtt_ms"]["median"]),
    ("rtt p95 ms", lambda r: r["overall"]["network"]["rtt_ms"]["p95"]),
    ("片道 ms", lambda r: r["overall"]["network"]["network_ms"]["median"]),
    ("pc 中央値 ms", lambda r: r["overall"]["pc"]["pc_median_ms"]["median"]),
    ("pc p95 ms", lambda r: r["overall"]["pc"]["pc_p95_ms"]["median"]),
    ("描画 fps", lambda r: r["overall"]["pc"]["render_fps"]["median"]),
    ("50 ms 超 回", lambda r: r["overall"]["pc"]["frames_over_50ms"]),
    ("合計の推定 ms", lambda r: r["overall"]["total_ms"]["median"]),
    ("判定 回", lambda r: r["overall"]["judge"]["n"]),
    ("Miss 回", lambda r: r["overall"]["judge"]["tiers"].get("Miss", 0)),
    ("Perfect 回", lambda r: r["overall"]["judge"]["tiers"].get("Perfect", 0)),
    ("judge-ms 中央値", lambda r: r["overall"]["judge"]["judge_ms_median"]),
    ("judge-ms p10", lambda r: r["overall"]["judge"]["judge_ms_p10"]),
    ("judge-ms p90", lambda r: r["overall"]["judge"]["judge_ms_p90"]),
    ("補正 ms", lambda r: r["overall"]["judge"]["compensation_ms"]),
    ("最適な補正（推定）ms", lambda r: r["overall"]["judge"]["best_compensation_ms"]),
]


def share_of(dist, pred):
    total = sum(v for k, v in dist.items() if k != "unknown")
    if not total:
        return None
    return round(100.0 * sum(v for k, v in dist.items() if k != "unknown" and pred(k)) / total, 1)


def compare(report_a, report_b):
    rows = []
    for name, getter in COMPARE_ROWS:
        values = []
        for rep in (report_a, report_b):
            try:
                values.append(getter(rep) if rep["overall"] is not None or name == "曲数" else None)
            except (KeyError, TypeError, AttributeError):
                values.append(None)
        a, b = values
        diff = round(b - a, 1) if isinstance(a, (int, float)) and isinstance(b, (int, float)) else None
        rows.append({"name": name, "a": a, "b": b, "diff": diff})
    return rows


def render_compare(report_a, report_b, rows):
    def head(rep, tag):
        return f"{tag}: {fmt_local(rep['since'])}〜{fmt_local(rep['until'])}（{fmt_utc(rep['since'])} 〜 {fmt_utc(rep['until'])}）"
    out = ["PhoneSaber プレイ比較", head(report_a, "A"), head(report_b, "B"), ""]
    size = max(width(r["name"]) for r in rows) + 2
    out.append(pad_left("項目", size) + "".join(pad_right(h, 10) for h in ("A", "B", "B−A")))
    for r in rows:
        diff = "-" if r["diff"] is None else f"{r['diff']:+.1f}"
        out.append(pad_left(r["name"], size)
                   + "".join(pad_right(v, 10) for v in (fmt(r["a"]), fmt(r["b"]), diff)))
    for tag, rep in (("A", report_a), ("B", report_b)):
        probs = rep["overall"]["problems"] if rep["overall"] else []
        out.append(f"\n{tag} の問題:" + (" なし" if not probs else ""))
        out += [f"  - {p}" for p in probs]
    return "\n".join(out)


def trial_segments(events, since, until):
    """F10 の印（event=trial-mark）ごとに [(n, 設定の dict, start, end)] を返す。印が無ければ空。"""
    window = in_window(events, since, until)
    marks = [e for e in window if e["event"] == "trial-mark"]
    if not marks:
        return []
    end_of_window = until or window[-1]["time"]
    segments = []
    for i, mark in enumerate(marks):
        # 次の印と同じ時刻の行を両方の試行に入れない（区間の終わりは次の印の直前）。
        end = marks[i + 1]["time"] - timedelta(milliseconds=1) if i + 1 < len(marks) else end_of_window
        # アプリを起動し直すと印の番号は 1 に戻るので、通し番号を前に付ける。
        n = mark["fields"].get("n", "?")
        segments.append((f"{i + 1}" if n == str(i + 1) else f"{i + 1}（印{n}）", mark["fields"], mark["time"], end))
    return segments


def build_trials(events, since, until, baseline=False):
    return [{"n": n, "settings": {k: v for k, v in fields.items()
                                   if k in ("judge", "offset-ms", "applied-ms", "prediction-red-ms", "prediction-blue-ms", "frame-cap")},
             "report": build_report(events, start, end, baseline)}
            for n, fields, start, end in trial_segments(events, since, until)]


def render_trials(trials):
    if not trials:
        return "この時間に F10 の印（trial-mark）がありません。--since / --last-session だけで見てください。"
    out = [f"PhoneSaber 試行ごとの比較  {len(trials)} 試行"]
    for t in trials:
        settings = " ".join(f"{k}={v}" for k, v in t["settings"].items())
        rep = t["report"]
        out.append(f"試行 {t['n']}: {fmt_local(rep['since'])}〜{fmt_local(rep['until'])}  {settings}")
        for summary in [*rep["songs"], rep["overall"]]:
            if summary and "baseline" in summary:
                out.append(baseline_line(summary))
    out.append("")
    size = max(width(name) for name, _ in COMPARE_ROWS) + 2
    out.append(pad_left("項目", size) + "".join(pad_right(f"試行{t['n']}", 10) for t in trials))
    for name, getter in COMPARE_ROWS:
        cells = []
        for t in trials:
            rep = t["report"]
            try:
                value = getter(rep) if rep["overall"] is not None or name == "曲数" else None
            except (KeyError, TypeError, AttributeError):
                value = None
            cells.append(fmt(value))
        out.append(pad_left(name, size) + "".join(pad_right(c, 10) for c in cells))
    for t in trials:
        probs = t["report"]["overall"]["problems"] if t["report"]["overall"] else []
        out.append(f"\n試行 {t['n']} の問題:" + (" なし" if not probs else ""))
        out += [f"  - {p}" for p in probs]
    return "\n".join(out)


# ---------------------------------------------------------------- CLI

def main(argv=None):
    parser = argparse.ArgumentParser(description="events.log からプレイを曲ごとに報告する")
    parser.add_argument("--events", default=DEFAULT_EVENTS, help="events.log、archive ファイル、またはログのディレクトリ")
    parser.add_argument("--since", help="開始。ISO（Z なしは UTC）かローカルの HH:MM")
    parser.add_argument("--until", help="終了。ISO かローカルの HH:MM")
    parser.add_argument("--last-session", action="store_true", help="最後の session-start から終わりまで")
    parser.add_argument("--baseline", nargs="?", const=BASELINE, choices=[BASELINE],
                        help="良好基準 good-20261020 で曲別・全体を OK/WARN/BAD 判定（欠測はWARN）")
    parser.add_argument("--compare", nargs=2, metavar=("A", "B"),
                        help="'SINCE..UNTIL' を2つ並べて比較（例 16:30..16:40 16:46..16:50）")
    parser.add_argument("--by-trial", action="store_true",
                        help="F10 の印（trial-mark）ごとに分けて並べる（--since / --last-session と併用可）")
    parser.add_argument("--json", metavar="OUT", help="結果を JSON で保存")
    args = parser.parse_args(argv)

    try:
        events = load_events(args.events)
    except OSError as exc:
        print(f"events.log を開けません: {exc}", file=sys.stderr)
        return 2

    try:
        if args.compare:
            windows = [parse_window_spec(spec) for spec in args.compare]
        else:
            since = parse_time_arg(args.since) if args.since else None
            until = parse_time_arg(args.until) if args.until else None
            if args.last_session:
                since = last_session_start(events)
    except ValueError as exc:
        print(str(exc), file=sys.stderr)
        return 2

    if args.compare:
        reports = [build_report(events, a, b, args.baseline) for a, b in windows]
        rows = compare(*reports)
        print(render_compare(reports[0], reports[1], rows))
        if args.baseline:
            for tag, report in zip(("A", "B"), reports):
                for summary in [*report["songs"], report["overall"]]:
                    if summary:
                        print(tag + " " + baseline_line(summary))
        result = {"a": reports[0], "b": reports[1], "compare": rows}
    elif args.by_trial:
        result = {"trials": build_trials(events, since, until, args.baseline)}
        print(render_trials(result["trials"]))
    else:
        result = build_report(events, since, until, args.baseline)
        print(render_report(result))

    if args.json:
        with open(args.json, "w", encoding="utf-8") as handle:
            json.dump(result, handle, ensure_ascii=False, indent=2, default=str)
            handle.write("\n")
    return 0


if __name__ == "__main__":
    sys.exit(main())
