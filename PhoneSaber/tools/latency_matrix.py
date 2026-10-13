#!/usr/bin/env python3
"""無人の遅延比較: 条件を変えながら F9 の計測（-phonesaberLatencyProbe）を繰り返し、表にする。

前提: iPhone が USB か同じ LAN でこの Mac から見えていて（Xcode の開発用の接続）、カメラがこの Mac の
画面（本番ではプロジェクターのスクリーン）を向いている。ゲームは通常の全画面で前面に出る。

  python3 PhoneSaber/tools/latency_matrix.py                       # 既定の条件を1回ずつ
  python3 PhoneSaber/tools/latency_matrix.py --only baseline lowlat # 名前で選ぶ
  python3 PhoneSaber/tools/latency_matrix.py --repeat 2 --blocks 4

iPhone は条件ごとに起動引数つきで再起動する（保存されている設定は書き換えない）。
画面の方式や解像度を変える条件は入れていない（2026-10-14 に試したところ、ウィンドウ表示や解像度変更では
スマホから見える棒の位置が変わって測れず、設定も次の起動に残った。全画面・既定の解像度のまま測る）。
結果は events.log の latency-block / display-measured / latency-estimate から読む。
"""
import argparse
import json
from pathlib import Path
import re
import statistics
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[2]
EVENTS = Path.home() / "Library/Application Support/DefaultCompany/ServTechSlash/PhoneSaber/events.log"
BUNDLE = "com.satoshi.PhoneSaberSender"

# 名前: (iPhone の起動引数, Unity の追加引数, 説明)
CONDITIONS = {
    "baseline": (["-cameraFPS", "60"], [], "60fps・通常のカメラ設定・全画面"),
    "lowlat": (["-cameraFPS", "60", "-PhoneSaber.experiment.lowLatencyCamera", "YES"], [], "低遅延カメラ設定（試験）ON"),
    "fps30": (["-cameraFPS", "30"], [], "30fps"),
}


def field(line, key):
    match = re.search(r"(?:^| )" + re.escape(key) + r"=(\S+)", line)
    return match.group(1) if match else None


def number(text):
    try:
        value = float(text)
    except (TypeError, ValueError):
        return None
    return value if value == value else None


def device_id():
    listing = subprocess.run(["xcrun", "devicectl", "list", "devices"], capture_output=True, text=True).stdout
    for line in listing.splitlines():
        if "physical" in line and ("connected" in line or "available" in line):
            match = re.search(r"([0-9A-Fa-f]{8}-[0-9A-Fa-f]{16})", line)
            if match:
                return match.group(1)
    return None


def launch_phone(device, arguments):
    command = ["xcrun", "devicectl", "device", "process", "launch", "--device", device, "--terminate-existing",
               BUNDLE, "--", "-PhoneSaber.autoStartSending", "YES", *arguments]
    result = subprocess.run(command, capture_output=True, text=True)
    return "Launched" in result.stdout


def run_probe(app, blocks, unity_arguments, log):
    offset = EVENTS.stat().st_size if EVENTS.exists() else 0
    subprocess.run(["/usr/bin/open", "-n", "-W", str(app), "--args", "-phonesaberStation", "A",
                    "-phonesaberLatencyProbe", str(blocks), *unity_arguments, "-logFile", str(log)])
    with EVENTS.open("rb") as handle:
        handle.seek(offset)
        return handle.read().decode("utf-8", "replace").splitlines()


def summarise(lines):
    blocks = [line for line in lines if "event=latency-block" in line and (number(field(line, "n")) or 0) >= 10]
    end = next((line for line in reversed(lines) if "event=latency-loop-end" in line), "")
    display = [number(field(line, "display-ms")) for line in lines
               if "event=display-measured" in line and field(line, "status") == "valid"]
    estimates = [line for line in lines if "event=latency-estimate" in line and "colour=RED" in line
                 and field(line, "platform") in ("ios", "android")]
    def median(values):
        values = [value for value in values if value is not None]
        return statistics.median(values) if values else None
    return {
        "loop": median([number(field(line, "median-ms")) for line in blocks]),
        "loop_p95": number(field(end, "p95-ms")),
        "samples": int(number(field(end, "n")) or 0), "misses": int(number(field(end, "misses")) or 0),
        "pkt": median([number(field(line, "red-pkt-per-s")) for line in blocks]),
        "c2s": median([number(field(line, "c2s-median-ms")) for line in estimates]),
        "network": median([number(field(line, "network-ms")) for line in estimates]),
        "display": median(display),
        "thermal": sorted({field(line, "thermal") for line in estimates if field(line, "thermal")}),
        "fps_mode": sorted({field(line, "fps-mode") for line in estimates if field(line, "fps-mode")}),
        "seen": " ".join(part for part in (field(end, "seen-x"), field(end, "seen-y")) if part),
    }


def show(value, digits=0):
    return "-" if value is None else f"{value:.{digits}f}"


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--app", default=str(ROOT / "Builds/Mac/3D-Saber.app"))
    parser.add_argument("--only", nargs="*", help="実行する条件の名前（既定: baseline lowlat）")
    parser.add_argument("--repeat", type=int, default=1)
    parser.add_argument("--blocks", type=int, default=3)
    parser.add_argument("--warmup", type=float, default=10.0, help="iPhone を起動してから計測を始めるまでの秒数")
    parser.add_argument("--json", help="結果を JSON でも保存する")
    args = parser.parse_args()
    names = args.only or ["baseline", "lowlat"]
    unknown = [name for name in names if name not in CONDITIONS]
    if unknown:
        print("知らない条件:", ", ".join(unknown), "/ 使えるもの:", ", ".join(CONDITIONS))
        return 2
    if subprocess.run(["pgrep", "-f", "/3D-Saber.app/Contents/MacOS/"], capture_output=True).stdout.strip():
        print("ゲームが起動中です。終了してから実行してください。")
        return 2
    device = device_id()
    if not device:
        print("iPhone が見つかりません（USB でつなぐか、同じ LAN に置いてロックを解除してください）。")
        return 2
    log = Path("/tmp") / "phonesaber-latency-matrix.log"
    results = []
    for round_index in range(args.repeat):
        for name in names:
            phone_arguments, unity_arguments, description = CONDITIONS[name]
            if not launch_phone(device, phone_arguments):
                print(f"{name}: iPhone のアプリを起動できません（ロック中？）")
                continue
            time.sleep(args.warmup)
            summary = summarise(run_probe(args.app, args.blocks, unity_arguments, log))
            summary.update(name=name, description=description, round=round_index + 1)
            results.append(summary)
            print(f"{name:9} #{round_index + 1}: 往復 {show(summary['loop'])} ms (p95 {show(summary['loop_p95'])}, "
                  f"{summary['samples']} 回, 失敗 {summary['misses']}) / 受信 {show(summary['pkt'])}/s / "
                  f"スマホ {show(summary['c2s'], 1)} 通信 {show(summary['network'], 1)} 画面 {show(summary['display'], 1)} / "
                  f"発熱 {','.join(summary['thermal']) or '-'} fps {','.join(summary['fps_mode']) or '-'}", flush=True)
    if args.json:
        Path(args.json).write_text(json.dumps(results, ensure_ascii=False, indent=1), encoding="utf-8")
    return 0 if results else 1


if __name__ == "__main__":
    sys.exit(main())
