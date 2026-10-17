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
    # 世代交代では名前だけが変わる。各ファイルの開始位置を inode に結び付け、時計変更にも依存しない。
    offsets = {}
    for generation in range(5):
        path = Path(str(EVENTS) + (f".{generation}" if generation else ""))
        try:
            stat = path.stat()
            offsets[(stat.st_dev, stat.st_ino)] = stat.st_size
        except FileNotFoundError:
            pass
    log = Path(os.environ.get("TMPDIR", "/tmp")) / "phonesaber-auto-latency-check.log"
    # 前回の正常完了ログを、今回の途中終了の結果として再利用しない。
    try:
        log.unlink(missing_ok=True)
    except OSError as error:
        print(f"Player のログを初期化できません: {error}")
        return 1
    command = ["/usr/bin/open", "-g", "-j", "-n", "-W", args.app, "--args", "-phonesaberSmoke", str(args.seconds),
               "-logFile", str(log)]
    if not args.windowed:
        command.insert(command.index("--args") + 1, "-batchmode")
    # LAN アドレスが途中で失われても、送信不能な Player だけを起動したまま残さない。
    sender = None
    try:
        sender = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sender.bind((address, 0))
        player = subprocess.Popen(command)
    except OSError as error:
        if sender is not None:
            sender.close()
        print(f"送信ソケットの準備または Player の起動に失敗しました: {error}")
        return 1
    stop = threading.Event()
    if args.windowed:
        threading.Thread(target=hide_if_front, args=(stop,), daemon=True).start()
    started = time.monotonic()
    next_metrics = 0.0
    frame = 0
    limit = args.seconds * 5 + 90
    while player.poll() is None and time.monotonic() - started < limit:
        now = time.monotonic() - started
        # 画面の左下の隅で小さく揺らすだけにする。大きく振るとメニューの「剣で切って選ぶ」操作を
        # 切ってしまい、smoke の画面遷移が失敗する（2026-10-18: タイトルへ戻れなかった）。
        wiggle = (frame * 3) % 40
        try:
            if now >= next_metrics:
                next_metrics = now + 5
                sender.sendto(METRICS.encode("ascii"), (address, 5007))
            sender.sendto(f"{40 + wiggle},960,{80 + wiggle},1040".encode("ascii"), (address, 5005))
            sender.sendto(f"{1880 - wiggle},960,{1840 - wiggle},1040".encode("ascii"), (address, 5006))
        except OSError:
            pass  # 受信開始前や一時的な通信断でも、次の送信を続ける。
        frame += 1
        time.sleep(1 / 60)
    stop.set()
    sender.close()
    code = player.poll()
    if code is None:
        subprocess.run(["pkill", "-KILL", "-f", "--", "-phonesaberSmoke"])
        print("Player が終わらないため中止しました:", log)
        return 1
    # open が 0 でも Player は途中で落ち得る。遅延推定だけでなく、今回の正常完了も必須。
    try:
        completions = [line for line in log.read_text(encoding="utf-8", errors="replace").splitlines()
                       if "[PhoneSaberSmoke] done" in line]
    except OSError as error:
        print(f"Player の完了ログを読めません: {error}")
        return 1
    if code != 0 or len(completions) != 1 or not re.fullmatch(
            r"\[PhoneSaberSmoke\] done exceptions=0 errors=0 input=ok cuts=[1-9][0-9]* flow=ok", completions[0]):
        print(f"Player の正常完了を確認できません (exit={code}): {log}")
        return 1
    lines, judge_lines = [], []
    for generation in range(4, -1, -1):
        path = Path(str(EVENTS) + (f".{generation}" if generation else ""))
        try:
            with path.open("rb") as handle:
                stat = os.fstat(handle.fileno())
                handle.seek(offsets.get((stat.st_dev, stat.st_ino), 0))
                text = handle.read().decode("utf-8", "replace").splitlines()
                lines.extend(line for line in text if "event=latency-estimate" in line)
                judge_lines.extend(line for line in text if "event=judge-latency " in line)
        except FileNotFoundError:
            pass
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
    # 本編では、その目安が判定の遅れ補正（自動）に入る（PhoneSaberJudgeLatency）。
    totals = [float(field(line, "total-ms")) for line in estimates if (field(line, "total-ms") or "").replace(".", "", 1).isdigit()]
    applied = [line for line in judge_lines if field(line, "mode") == "auto" and field(line, "auto-ms") not in (None, "unknown")]
    if not applied:
        print("NG: 判定の遅れ補正（event=judge-latency mode=auto）に目安が入っていません")
        return 1
    # applied-ms には台ごとの調整と予測の先読みが入るので、比べるのは測定から作った auto-ms。
    value = float(field(applied[-1], "auto-ms"))
    span = f"{min(totals):.0f}〜{max(totals):.0f}" if totals else "不明"
    print(f"  判定の遅れ補正: auto-ms={value:.0f} applied-ms={field(applied[-1], 'applied-ms')}（目安 {span} ms、ゲーム既定 {field(applied[-1], 'game-ms')} ms）")
    if totals and not (min(totals) - 25 <= value <= max(totals) + 25):
        print("NG: 判定の遅れ補正が目安から離れています")
        return 1
    print("OK: 判定の遅れ補正に目安が入りました")
    return 0


if __name__ == "__main__":
    sys.exit(main())
