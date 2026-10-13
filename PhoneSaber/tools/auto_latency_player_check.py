#!/usr/bin/env python3
"""ビルド済み Player で「遅延の目安」(event=latency-estimate) が実際に出るかを、スマホなしで確かめる。

Player を隠して起動し(-phonesaberSmoke)、この Mac の LAN アドレスからスマホ役として
座標(5005/5006)と診断(PHONESABER_METRICS、5007)を送る。127.0.0.1 から送ると PC 側は
「P2P 経由」とみなして欠測にするので、必ず LAN アドレスを使う。

  python3 PhoneSaber/tools/auto_latency_player_check.py            # 窓なし(batchmode)。画面には何も出ない
  python3 PhoneSaber/tools/auto_latency_player_check.py --windowed # 窓ありを隠して起動(通常は不要)

窓なしでも4項目(スマホ・通信・PC・画面)がそろう(2026-10-13 に確認)。
--windowed は Player が自分で前面に出ようとするたびに隠し直す(一瞬見えることがある)。
ゲームを起動中のときは使わない(同じ UDP port を使う)。
"""
import argparse
import os
from pathlib import Path
import re
import socket
import subprocess
import sys
import threading
import time

ROOT = Path(__file__).resolve().parents[2]
EVENTS = Path.home() / "Library/Application Support/DefaultCompany/ServTechSlash/PhoneSaber/events.log"
METRICS = ("PHONESABER_METRICS 1 station=A platform=ios app=check fps-mode=60 capture-fps=59.8 send-red-fps=59.7 "
           "send-blue-fps=59.6 c2s-median-ms=36.9 c2s-p95-ms=44.1 c2s-n=298 rtt-ms=3.2 rtt-n=8 rtt-age-s=0.2 "
           "route=LAN thermal=nominal battery=82.0 low-latency-test=0 c2s-end=detection")


def lan_address():
    probe = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        probe.connect(("192.0.2.1", 9))  # 送信はしない。経路の選択だけ。
        return probe.getsockname()[0]
    except OSError:
        return None
    finally:
        probe.close()


def field(line, key):
    match = re.search(r"(?:^| )" + re.escape(key) + r"=(\S+)", line)
    return match.group(1) if match else None


def hide_if_front(stop):
    script_front = 'tell application "System Events" to get unix id of first process whose frontmost is true'
    while not stop.is_set():
        game = subprocess.run(["pgrep", "-f", "--", "-phonesaberSmoke"], capture_output=True, text=True).stdout.split()
        if game:
            front = subprocess.run(["osascript", "-e", script_front], capture_output=True, text=True).stdout.strip()
            if front in game:
                subprocess.run(["osascript", "-e", 'tell application "System Events" to set visible of '
                                f'(first process whose unix id is {front}) to false'], capture_output=True)
        stop.wait(0.2)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--app", default=str(ROOT / "Builds/Mac/3D-Saber.app"))
    parser.add_argument("--seconds", type=int, default=12, help="1 scene あたりの秒数")
    parser.add_argument("--windowed", action="store_true")
    args = parser.parse_args()
    address = lan_address()
    if not address or address.startswith("127."):
        print("LAN アドレスがありません(Wi-Fi 未接続)。この確認はできません。")
        return 2
    if subprocess.run(["pgrep", "-f", "/3D-Saber.app/Contents/MacOS/"], capture_output=True).stdout.strip():
        print("ゲームが起動中です。終了してから実行してください。")
        return 2
    offset = EVENTS.stat().st_size if EVENTS.exists() else 0
    log = Path(os.environ.get("TMPDIR", "/tmp")) / "phonesaber-auto-latency-check.log"
    command = ["/usr/bin/open", "-g", "-j", "-n", "-W", args.app, "--args", "-phonesaberSmoke", str(args.seconds),
               "-logFile", str(log)]
    if not args.windowed:
        command.insert(command.index("--args") + 1, "-batchmode")
    player = subprocess.Popen(command)
    stop = threading.Event()
    if args.windowed:
        threading.Thread(target=hide_if_front, args=(stop,), daemon=True).start()
    sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sender.bind((address, 0))
    started = time.monotonic()
    next_metrics = 0.0
    frame = 0
    limit = args.seconds * 5 + 90
    while player.poll() is None and time.monotonic() - started < limit:
        now = time.monotonic() - started
        if now >= next_metrics:
            next_metrics = now + 5
            sender.sendto(METRICS.encode("ascii"), (address, 5007))
        sweep = 600 + (frame * 7) % 500
        try:
            sender.sendto(f"{sweep},300,{sweep + 300},700".encode("ascii"), (address, 5005))
            sender.sendto(f"{1500 - sweep},300,{1200 - sweep},700".encode("ascii"), (address, 5006))
        except OSError:
            pass  # Player がまだ受信を始めていない間は無視
        frame += 1
        time.sleep(1 / 60)
    stop.set()
    if player.poll() is None:
        subprocess.run(["pkill", "-KILL", "-f", "--", "-phonesaberSmoke"])
        print("Player が終わらないため中止しました:", log)
        return 1
    with EVENTS.open("rb") as handle:
        handle.seek(offset)
        lines = [line for line in handle.read().decode("utf-8", "replace").splitlines() if "event=latency-estimate" in line]
    if not lines:
        print("event=latency-estimate が1行もありません:", EVENTS)
        return 1
    statuses = {}
    for line in lines:
        statuses[field(line, "status")] = statuses.get(field(line, "status"), 0) + 1
    print(f"送信元 {address} / latency-estimate {len(lines)} 行 / status: {statuses}")
    matched = [line for line in lines if field(line, "sender") == address and field(line, "c2s-median-ms") == "36.9"]
    estimates = [line for line in matched if field(line, "status") == "estimate"]
    for line in (estimates or matched or lines)[-2:]:
        print("  " + " ".join(f"{key}={field(line, key)}" for key in (
            "colour", "status", "total-ms", "c2s-median-ms", "network-ms", "pc-median-ms", "pc-n", "display-ms",
            "refresh-hz", "phone-age-s")))
    if not matched:
        print("NG: スマホ役の診断が、座標の送信元と結び付いていません")
        return 1
    if not estimates:
        print("NG: 4項目そろった行(status=estimate)がありません")
        return 1
    print("OK: 4項目そろった目安が記録されました")
    return 0


if __name__ == "__main__":
    sys.exit(main())
