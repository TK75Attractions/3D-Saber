#!/usr/bin/env python3
"""F9 遅延テストの latency-block 記録を条件ごとに集計する。

Unity の events.log（Mac: ~/Library/Application Support/<会社>/<製品>/PhoneSaber/events.log、
Windows: %USERPROFILE%/AppData/LocalLow/<会社>/<製品>/PhoneSaber/events.log）を読む。
条件 = PC の max-queued-frames × スマホの fps（ブロック平均の赤 pkt/s から推定）× 経路。
同じ測定の中で交互に切り替えたブロックだけを比べるので、照明・置き方の差は打ち消される。
"""
import argparse
import glob
import os
import re
import statistics
import sys

LINE = re.compile(r"^(?P<time>\S+) .*event=latency-block (?P<detail>.*)$")


def parse(lines):
    blocks = []
    for line in lines:
        match = LINE.match(line.strip())
        if not match:
            continue
        fields = dict(item.split("=", 1) for item in match.group("detail").split() if "=" in item)
        try:
            blocks.append({
                "time": match.group("time"),
                "queued": int(fields["max-queued-frames"]),
                "median": float(fields["median-ms"]),
                "p95": float(fields["p95-ms"]),
                "n": int(fields["n"]),
                "rate": float(fields["red-pkt-per-s"]),
                "route": fields.get("route", "?"),
                "platform": fields.get("platform", "?"),
            })
        except (KeyError, ValueError):
            continue
    return blocks


def phone_fps(rate):
    """赤の平均受信数からスマホの fps を推定する。切替をまたいだ中間値は None。"""
    if rate >= 45:
        return 60
    if 15 <= rate <= 36:
        return 30
    return None


def summarize(blocks):
    groups = {}
    for block in blocks:
        fps = phone_fps(block["rate"])
        if fps is None or block["n"] < 5:
            continue
        key = (block["platform"], block["route"], fps, block["queued"])
        groups.setdefault(key, []).append(block)
    rows = []
    for key, items in sorted(groups.items()):
        rows.append({
            "platform": key[0], "route": key[1], "fps": key[2], "queued": key[3],
            "blocks": len(items),
            "median": statistics.median(b["median"] for b in items),
            "p95": statistics.median(b["p95"] for b in items),
        })
    return rows


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
    parser.add_argument("--since", default="", help="この ISO 時刻以降のブロックだけ（例 2026-10-08T00:00）")
    args = parser.parse_args(argv)
    paths = args.logs or default_logs()
    lines = []
    for path in paths:
        with open(path, encoding="utf-8", errors="replace") as handle:
            lines.extend(handle)
    blocks = [b for b in parse(lines) if b["time"] >= args.since]
    rows = summarize(blocks)
    if not rows:
        print("latency-block がありません（F9 で15回以上測ると1ブロック記録されます）")
        return 1
    print("platform        route        phone-fps  queued  blocks  median-ms  p95-ms")
    for row in rows:
        print(f"{row['platform']:<15} {row['route']:<12} {row['fps']:>9} {row['queued']:>7} "
              f"{row['blocks']:>7} {row['median']:>10.0f} {row['p95']:>7.0f}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
