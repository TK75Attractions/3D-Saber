#!/usr/bin/env python3
"""F9 遅延テストの latency-block 記録を条件ごとに集計する。

Unity の events.log（Mac: ~/Library/Application Support/<会社>/<製品>/PhoneSaber/events.log、
Windows: %USERPROFILE%/AppData/LocalLow/<会社>/<製品>/PhoneSaber/events.log）を読む。
条件 = PC の max-queued-frames × スマホの fps × 経路。
--sender-metrics で iPhone の設定ログを指定する。省略時は赤 pkt/s からの推定。
同じ測定の中で交互に切り替えたブロックだけを比べるので、照明・置き方の差は打ち消される。
台・ログファイル・測定ごとに分離する。測定は block 番号の再開始または終了記録で区切る。
開始記録がない旧ログでは最初に観測したブロックを開始とし、別ファイルとは結合しない。
"""
import argparse
from datetime import datetime, timezone
import glob
import math
import os
import re
import statistics
import sys

CAPTURE_MODE = re.compile(r"\[CAPTURE_METRICS\] (\S+) station=(\S+) requested-fps=(30|60|120) capture-fps=(\d+)")
CONNECTIVITY_CAPTURE_MODE = re.compile(r"^(\S+) \[CAPTURE_MODE\] station=(\S+) requested-fps=(30|60|120) capture-fps=(\d+)")

LINE = re.compile(r"^(?P<time>\S+) (?P<context>.*?)event=(?P<event>latency-block|latency-loop-end) (?P<detail>.*)$")

ESTIMATE_TERMS = ("c2s-median-ms", "network-ms", "pc-median-ms", "display-ms", "total-ms")


def parse_estimates(lines, source=""):
    """同期不要の自動計測。欠測・古い値・途中までの書込みは比較に入れない。"""
    rows = []
    for line in lines:
        fields = dict(item.split("=", 1) for item in line.split()[1:] if "=" in item)
        if fields.get("event") != "latency-estimate" or fields.get("status") != "estimate":
            continue
        try:
            terms = {key: float(fields[key]) for key in ESTIMATE_TERMS}
            ages = [float(fields["phone-age-s"]), float(fields["rtt-age-s"])]
            if (not all(math.isfinite(v) and v >= 0 for v in list(terms.values()) + ages)
                    or ages[0] > 15 or ages[1] > 10
                    or any(int(fields[key]) <= 0 for key in ("c2s-n", "pc-n", "rtt-n"))):
                continue
            rows.append(dict(time=line.split()[0], source=source, station=fields["station"],
                             colour=fields["colour"], fps=int(float(fields["fps-mode"])),
                             platform=fields["platform"], route=fields["route"],
                             end=fields["c2s-end"], terms=terms))
        except (KeyError, ValueError, OverflowError, IndexError):
            continue
    return rows


def summarize_estimates(samples):
    groups = {}
    for row in samples:
        key = tuple(row[k] for k in ("station", "colour", "fps", "platform", "route", "end"))
        groups.setdefault(key, []).append(row)
    result = []
    for key, items in sorted(groups.items()):
        terms = {}
        for term in ESTIMATE_TERMS:
            values = sorted(item["terms"][term] for item in items)
            terms[term] = (statistics.median(values), values[math.ceil(len(values) * .95) - 1])
        result.append(dict(zip(("station", "colour", "fps", "platform", "route", "end"), key),
                           n=len(items), terms=terms))
    return result


def parse(lines, source=""):
    blocks = []
    sessions = {}
    next_session = 0
    for line in lines:
        match = LINE.match(line.strip())
        if not match:
            continue
        fields = dict(item.split("=", 1) for item in match.group("detail").split() if "=" in item)
        context = dict(item.split("=", 1) for item in match.group("context").split() if "=" in item)
        station = context.get("station", "?")
        stream = (station, fields.get("platform", "?"))
        if match.group("event") == "latency-loop-end":
            sessions.pop(stream, None)
            continue
        if match.group("event") == "latency-block" and "misses" not in fields:
            # Unity は misses= を最後に書く。無い行は書込途中で切れた行なので、
            # 切れた route/platform で偽の条件・偽の測定区切りを作らない。
            continue
        try:
            index = int(fields["block"])
            if index < 0:
                continue
            block = {
                "time": match.group("time"),
                "block": index,
                "station": station,
                "source": source,
                "queued": int(fields["max-queued-frames"]),
                "median": float(fields["median-ms"]),
                "p95": float(fields["p95-ms"]),
                "n": int(fields["n"]),
                "rate": float(fields["red-pkt-per-s"]),
                "route": fields.get("route", "?"),
                "platform": fields.get("platform", "?"),
            }
        except (KeyError, ValueError):
            continue
        previous = sessions.get(stream)
        if previous is None or index <= previous["block"]:
            next_session += 1
            session, started = next_session, block["time"]
        else:
            session, started = previous["session"], previous["started"]
        block.update(session=session, started=started,
                     start_time=previous["time"] if previous and previous["session"] == session else None)
        sessions[stream] = block
        blocks.append(block)
    return blocks


def phone_fps(rate):
    """赤の平均受信数からスマホの fps を推定する。切替をまたいだ中間値は None。"""
    if 105 <= rate <= 132:
        return 120
    if 45 <= rate <= 72:
        return 60
    if 15 <= rate <= 36:
        return 30
    return None


def parse_sender_metrics(lines):
    """送信側の設定区間。端末とPCの壁時計を同期して使う。"""
    modes = []
    for line in lines:
        match = CAPTURE_MODE.search(line) or CONNECTIVITY_CAPTURE_MODE.search(line)
        if not match:
            continue
        time, station, requested, actual = match.groups()
        try:
            modes.append({"time": parse_time(time), "station": station,
                          "requested": int(requested), "fps": int(actual)})
        except ValueError:
            continue
    return sorted(modes, key=lambda mode: mode["time"])


def capture_mode_for_block(block, modes):
    # 最初の block は開始時刻が不明。切替をまたぐ block も推定で補わず除外する。
    if block["start_time"] is None:
        return None
    try:
        start, end = parse_time(block["start_time"]), parse_time(block["time"])
    except ValueError:
        return None
    if start >= end:
        return None
    applicable = [mode for mode in modes if mode["station"] == block["station"] and mode["time"] <= end]
    if not applicable or applicable[-1]["time"] > start:
        return None
    fps = applicable[-1]["fps"]
    return fps if fps in (30, 60, 120) else None


def summarize(blocks, sender_metrics=None):
    groups = {}
    for block in blocks:
        fps = (phone_fps(block["rate"]) if sender_metrics is None
               else capture_mode_for_block(block, sender_metrics))
        if fps is None or block["n"] < 5:
            continue
        key = (block["source"], block["station"], block["session"],
               block["platform"], block["route"], fps, block["queued"])
        groups.setdefault(key, []).append(block)
    rows = []
    for key, items in sorted(groups.items()):
        rows.append({
            "source": key[0], "station": key[1], "session": key[2], "started": items[0]["started"],
            "platform": key[3], "route": key[4], "fps": key[5], "queued": key[6],
            "blocks": len(items),
            "median": statistics.median(b["median"] for b in items),
            "p95": statistics.median(b["p95"] for b in items),
        })
    return rows


def parse_time(text):
    """ISO 8601 を aware datetime にする。末尾 Z と +09:00 を受け付け、時差なしは UTC とみなす。"""
    value = text.strip()
    if value.endswith(("Z", "z")):
        value = value[:-1] + "+00:00"
    parsed = datetime.fromisoformat(value)  # ValueError は呼び出し側で扱う
    return parsed if parsed.tzinfo is not None else parsed.replace(tzinfo=timezone.utc)


def after(blocks, since):
    """since 以降のブロック。文字列比較だと +09:00 付きや不正な指定で黙って全件落ちる。"""
    kept = []
    for block in blocks:
        try:
            if parse_time(block["time"]) >= since:
                kept.append(block)
        except ValueError:
            continue
    return kept


def default_logs():
    home = os.path.expanduser("~")
    patterns = [
        os.path.join(home, "Library/Application Support/*/*/PhoneSaber/events.log*"),
        os.path.join(home, "AppData/LocalLow/*/*/PhoneSaber/events.log*"),
    ]
    return sorted(path for pattern in patterns for path in glob.glob(pattern))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("logs", nargs="*", help="events.log（省略時は既定の場所を探す）")
    parser.add_argument("--since", default="",
                        help="この ISO 時刻以降のブロックだけ。時差なしは UTC（ログと同じ）。"
                             "日本時間は +09:00 を付ける（例 2026-10-08T09:00+09:00）")
    parser.add_argument("--sender-metrics", help="iPhone の [CAPTURE_METRICS] ログ。PCと時計同期が必要")
    parser.add_argument("--estimates", action="store_true", help="通常プレイの自動計測を台・色・fps別に集計（PCログのみ）")
    args = parser.parse_args(argv)
    since = None
    if args.since:
        try:
            since = parse_time(args.since)
        except ValueError:
            parser.error(f"--since を ISO 時刻として読めません: {args.since!r}（例 2026-10-08T09:00+09:00）")
    paths = args.logs or default_logs()
    blocks = []
    for path in paths:
        try:
            with open(path, encoding="utf-8", errors="replace") as handle:
                blocks.extend((parse_estimates if args.estimates else parse)(handle, source=path))
        except OSError as error:
            print(f"{path} を読めません: {error}", file=sys.stderr)
            return 2
    if since is not None:
        blocks = after(blocks, since)
    if args.estimates:
        rows = summarize_estimates(blocks)
        if not rows:
            print("有効な latency-estimate がありません（旧アプリ・P2P・欠測・期限切れは集計しません）")
            return 1
        print("各10秒区間の代表値の中央値/p95（ms）。全フレームのp95ではありません。画面は推定、投影機処理は未算入。")
        print("station colour fps platform route n " + " ".join(ESTIMATE_TERMS))
        for row in rows:
            print(f"{row['station']} {row['colour']} {row['fps']} {row['platform']} {row['route']} {row['n']} " +
                  " ".join(f"{row['terms'][term][0]:.1f}/{row['terms'][term][1]:.1f}" for term in ESTIMATE_TERMS))
        return 0
    modes = None
    if args.sender_metrics:
        try:
            with open(args.sender_metrics, encoding="utf-8", errors="replace") as handle:
                modes = parse_sender_metrics(handle)
        except OSError as error:
            print(f"{args.sender_metrics} を読めません: {error}", file=sys.stderr)
            return 2
    rows = summarize(blocks, sender_metrics=modes)
    if not rows:
        print("latency-block がありません（F9 で15回以上測ると1ブロック記録されます）")
        return 1
    print("fps-source=" + ("sender-metrics" if modes is not None else "packet-rate-estimate"))
    previous = None
    for row in rows:
        identity = (row["source"], row["station"], row["session"])
        if identity != previous:
            print(f"log={row['source']} station={row['station']} session={row['session']} started={row['started']}")
            print("platform        route        phone-fps  queued  blocks  median-ms  p95-ms")
            previous = identity
        print(f"{row['platform']:<15} {row['route']:<12} {row['fps']:>9} {row['queued']:>7} "
              f"{row['blocks']:>7} {row['median']:>10.0f} {row['p95']:>7.0f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
