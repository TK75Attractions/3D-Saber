#!/usr/bin/env python3
"""ローカル events.log に届いた相手の診断を、元の台の時刻・曲ごとに集計する。"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from datetime import datetime
import json
import math
from pathlib import Path
import re
import statistics
from urllib.parse import unquote

STAMP = r"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z"
PEER = re.compile(
    rf"^({STAMP}) station=\S+ colour=PEER event=peer-log from-station=([A-Za-z0-9_-]{{1,16}}) "
    rf"({STAMP}) station=([A-Za-z0-9_-]{{1,16}}) colour=(\S+) event=([a-z0-9-]+) (.*)$"
)
FIELD = re.compile(r"(?:^|\s)([A-Za-z0-9_-]+)=([^\s]*)")


def peer_rows(lines, station="B"):
    """ローカル行や別台、包みと元の station が不一致の行は採用しない。"""
    seen = set()
    for line in lines:
        if len(line) > 16384:
            continue
        match = PEER.fullmatch(line.rstrip("\r\n"))
        if not match or match[2] != station or match[4] != station:
            continue
        try:
            datetime.fromisoformat(match[3].replace("Z", "+00:00"))
        except ValueError:
            continue
        identity = match.groups()[2:]  # 受信側時刻が異なる再取り込みも二重集計しない。
        if identity in seen:
            continue
        seen.add(identity)
        yield {"at": match[3], "colour": match[5], "event": match[6], "fields": dict(FIELD.findall(match[7]))}


def numeric(fields, name):
    try:
        value = float(fields[name])
        return value if math.isfinite(value) else None
    except (KeyError, ValueError):
        return None


def metric(values):
    if not values:
        return {"n": 0, "median": None, "max": None}
    return {"n": len(values), "median": round(statistics.median(values), 3), "max": round(max(values), 3)}


def summarize(lines, station="B"):
    rows = sorted(peer_rows(lines, station), key=lambda row: row["at"])
    groups = []
    current = None
    scene = ""
    run_counts = Counter()

    def begin(song):
        run_counts[song] += 1
        group = {"song": song, "run": run_counts[song], "start": None, "end": None,
                 "events": Counter(), "samples": defaultdict(list)}
        groups.append(group)
        return group

    for row in rows:
        kind, fields = row["event"], row["fields"]
        if kind == "session-start":
            current, scene = None, ""
        if kind == "peer-context":
            next_scene = fields.get("scene", "")
            song = unquote(fields.get("song", "")) or "曲不明"
            if next_scene == "Game":
                if current is None or scene != "Game" or current["song"] != song:
                    current = begin(song)
            else:
                current = None
            scene = next_scene
        if current is None:
            # 曲外の遅延も失わず残す。Game の文脈が届かない場合は不明と明記する。
            if not groups or groups[-1]["song"] != "曲外・不明":
                current = begin("曲外・不明")
            else:
                current = groups[-1]
        current["start"] = current["start"] or row["at"]
        current["end"] = row["at"]
        current["events"][kind] += 1
        samples = current["samples"]
        names = {}
        if kind == "latency-estimate":
            names = {key: f"{row['colour']}.{key}" for key in ("total-ms", "pc-median-ms", "pc-p95-ms", "c2s-median-ms", "network-ms")}
        elif kind == "perf":
            names = {key: key for key in ("fps", "red-pkt-per-s", "blue-pkt-per-s", "frame-ms-max")}
        elif kind == "input-gap":
            names = {"gap-seconds": "gap-seconds"}
        elif kind == "hitch":
            names = {"interval-ms": "hitch-ms"}
        elif kind == "judge-latency":
            names = {"applied-ms": "judge-compensation-ms"}
        elif kind == "judge-timing":
            names = {"judge-ms": "judge-error-ms"}
        for source, target in names.items():
            value = numeric(fields, source)
            if value is not None:
                samples[target].append(value)
        if kind == "session-quit":
            current, scene = None, ""

    result = []
    for group in groups:
        samples = group.pop("samples")
        group["metrics"] = {name: metric(values) for name, values in sorted(samples.items())}
        group["events"] = dict(group["events"])
        group["interruptions"] = {name: group["events"].get(name, 0) for name in ("input-gap", "input-silent", "input-back", "sender-change", "hitch")}
        result.append(group)
    return {"station": station, "peer_rows": len(rows), "songs": result}


def markdown(report):
    out = [f"# 台{report['station']} の peer-log 診断", "",
           f"受信済み {report['peer_rows']} 行。遅延は total-ms の推定値、判定補正とは別。",
           "元の台の UTC 時刻で集計。UDP の欠落・行数制限があるため、途切れの件数は下限。", "",
           "| 曲 / 回 | 元の台の UTC | 推定遅延 R/B (ms、中央値) | packet/s R/B (中央値) | gap / silent / back / sender / hitch |", "|---|---|---|---|---|"]

    def value(group, name):
        result = group["metrics"].get(name, {}).get("median")
        return "不明" if result is None else f"{result:g}"

    for group in report["songs"]:
        counts = group["interruptions"]
        interruptions = " / ".join(str(counts[name]) for name in ("input-gap", "input-silent", "input-back", "sender-change", "hitch"))
        title = group["song"].replace("|", "\\|").replace("\n", " ")
        out.append(f"| {title} / {group['run']} | {group['start']} ～ {group['end']} | "
                   f"{value(group, 'RED.total-ms')} / {value(group, 'BLUE.total-ms')} | "
                   f"{value(group, 'red-pkt-per-s')} / {value(group, 'blue-pkt-per-s')} | {interruptions} |")
    return "\n".join(out) + "\n"


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", type=Path, nargs="+", help="events.log と必要なら世代ファイル")
    parser.add_argument("--station", default="B", help="取り出す相手の台名（既定 B、台B側では A）")
    parser.add_argument("--json", action="store_true", help="最大値・サンプル数・判定補正なども JSON で出力")
    args = parser.parse_args(argv)
    if not re.fullmatch(r"[A-Za-z0-9_-]{1,16}", args.station):
        parser.error("station は英数字・ハイフン・下線の1〜16文字")

    def lines():
        for path in args.logs:
            with path.open(encoding="utf-8", errors="replace") as stream:
                yield from stream
    try:
        report = summarize(lines(), args.station)
    except OSError as error:
        parser.exit(1, f"ログを読めません: {error}\n")
    print(json.dumps(report, ensure_ascii=False, indent=2) if args.json else markdown(report), end="\n" if args.json else "")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
