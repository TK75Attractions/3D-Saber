#!/usr/bin/env python3
"""会場画像の非公開回帰セット。画像・ラベル・結果は Git 外だけに保存する。

使い方:
  python3 field_regression.py add --labels /private/path/labels.json
  python3 field_regression.py run

labels.json の形式（合成座標の例）:
{"schemaVersion": 1, "frames": [{
  "sourceClip": "phone_saber_triage_autoclip_YYYYMMDD_HHMMSS_000_confusion_blue",
  "image": "image_10_event_123.png", "exposure": -2,
  "date": "2026-10-20T15:44:04+09:00",
  "red": "absent", "blue": [{"x": 10, "y": 20}, {"x": 50, "y": 80}],
  "expectedFail": {"blue": {"failure": "miss", "reason": "原画像で確認した既知の未検出"}},
  "notes": "原画像を目視。枠・候補・検出座標からラベルを作らない。"
}]}

座標は回転・縮小前 PNG の左上原点、画面内で見える発光部の両端。
隠れた端・画面外の端は推測せず、見える部分をラベルし notes に記す。
両色必須。expectedFail は任意、failure は miss / endpoints / false-positive。
既知失敗は同じ失敗種別だけ許容し、改善は XPASS と表示する（終了0）。
absent の新規誤検出も FAIL。既知失敗の自動登録・更新はしない。
run は lossless と同じ本番 analyzeSabers の静止画ホストビルドを使用する。
FrameProcessor の追跡状態・無視範囲・送信抑制・UDP の再現ではない。
"""

from __future__ import annotations

import argparse
from datetime import datetime
import hashlib
import json
import math
import os
from pathlib import Path
import re
import struct
import sys
import tempfile

import run_lossless_regression as lossless


PRIVATE_ROOT = Path.home() / "Library/Application Support/PhoneSaber"
DEFAULT_SET = PRIVATE_ROOT / "field-regression"
DEFAULT_INBOX = PRIVATE_ROOT / "diagnostics-inbox/autoclips"
COLORS = ("red", "blue")


def outside_git(path: Path) -> Path:
    path = path.expanduser().resolve()
    if any((parent / ".git").exists() for parent in (path, *path.parents)):
        raise ValueError(f"非公開データを Git 内には保存できません: {path}")
    return path


def write_json(path: Path, value: object) -> None:
    path = outside_git(path)
    # 上書きしない。既知失敗や ground truth の無意識な更新を防ぐ。
    with path.open("x", encoding="utf-8") as handle:
        os.chmod(path, 0o600)
        json.dump(value, handle, ensure_ascii=False, indent=2, allow_nan=False)
        handle.write("\n")


def png_size(path: Path) -> tuple[int, int]:
    with path.open("rb") as handle:
        header = handle.read(24)
    if len(header) != 24 or header[:8] != b"\x89PNG\r\n\x1a\n" or header[12:16] != b"IHDR":
        raise ValueError(f"PNG ではありません: {path}")
    width, height = struct.unpack(">II", header[16:24])
    if not width or not height:
        raise ValueError("PNG の寸法が不正です")
    return width, height


def validate_label(label: dict, size: tuple[int, int]) -> None:
    if not isinstance(label, dict):
        raise ValueError("フレームラベルは object が必要です")
    if not re.fullmatch(r"phone_saber_triage_autoclip_[A-Za-z0-9_-]+", label.get("sourceClip", "")):
        raise ValueError("sourceClip が不正です")
    if not re.fullmatch(r"[^/\\]+event[^/\\]*\.png", label.get("image", "")):
        raise ValueError("image は images 内の event PNG 名が必要です")
    exposure = label.get("exposure")
    if type(exposure) not in (int, float) or not math.isfinite(exposure):
        raise ValueError("exposure は有限の数値が必要です")
    if not isinstance(label.get("date"), str):
        raise ValueError("date は ISO 8601 日時が必要です")
    datetime.fromisoformat(label["date"])
    for color in COLORS:
        truth = label.get(color)
        if truth == "absent":
            continue
        if not isinstance(truth, list) or len(truth) != 2:
            raise ValueError(f"{color}: absent または両端点が必要です")
        for point in truth:
            if not isinstance(point, dict) or any(
                type(point.get(axis)) not in (int, float)
                or not math.isfinite(point[axis]) or not 0 <= point[axis] < limit
                for axis, limit in zip(("x", "y"), size)
            ):
                raise ValueError(f"{color}: 端点は原画像の範囲内の有限座標が必要です")
        if truth[0] == truth[1]:
            raise ValueError(f"{color}: 剣の長さが0です")
    expected = label.get("expectedFail", {})
    if not isinstance(expected, dict) or set(expected) - set(COLORS):
        raise ValueError("expectedFail は色別 object が必要です")
    for color, entry in expected.items():
        allowed = ("false-positive",) if label[color] == "absent" else ("miss", "endpoints")
        if (not isinstance(entry, dict) or entry.get("failure") not in allowed
                or not isinstance(entry.get("reason"), str) or not entry["reason"].strip()):
            raise ValueError(f"{color}: expectedFail は失敗種別と具体的な reason が必要です")


def add_frames(labels_path: Path, inbox: Path, destination: Path) -> int:
    document = json.loads(outside_git(labels_path).read_text(encoding="utf-8"))
    if (not isinstance(document, dict) or document.get("schemaVersion") != 1
            or not isinstance(document.get("frames"), list) or not document["frames"]):
        raise ValueError("schemaVersion=1 と空でない frames が必要です")
    destination = outside_git(destination)
    inbox = outside_git(inbox)
    pending = []
    names = set()
    for label in document["frames"]:
        validate_label(label, (2**32, 2**32))
        source = (inbox / label["sourceClip"] / "images" / label["image"]).resolve()
        if not source.is_relative_to(inbox):
            raise ValueError("原画像が inbox 外を参照しています")
        size = png_size(source)
        validate_label(label, size)
        metadata = json.loads((source.parent.parent / "summary.json").read_text(encoding="utf-8"))["autoClip"]
        if metadata["frameSize"] != {"width": size[0], "height": size[1]}:
            raise ValueError(f"フル解像度の event 画像が必要です: {source.name}")
        if label["exposure"] != metadata["camera"]["exposureBias"]:
            raise ValueError(f"露出がクリップの記録と異なります: {source.name}")
        name = label["sourceClip"] + "--" + label["image"]
        target = destination / name
        sidecar = target.with_suffix(".json")
        if name in names or target.exists() or sidecar.exists():
            raise ValueError(f"登録済みまたは重複: {name}")
        names.add(name)
        pending.append((source, target, sidecar, label, size))
    destination.mkdir(mode=0o700, parents=True, exist_ok=True)
    os.chmod(destination, 0o700)
    for source, target, sidecar, label, size in pending:
        data = source.read_bytes()
        with target.open("xb") as handle:
            os.chmod(target, 0o600)
            handle.write(data)
        write_json(sidecar, {**label, "schemaVersion": 1, "size": list(size),
                             "sha256": hashlib.sha256(data).hexdigest()})
    return len(pending)


def load_set(directory: Path) -> list[tuple[Path, dict]]:
    directory = outside_git(directory)
    images = sorted(directory.glob("*.png"))
    if not images or {p.stem for p in images} != {p.stem for p in directory.glob("*.json")}:
        raise ValueError("セットが空、または PNG とフレームラベルが対応していません")
    frames = []
    for path in images:
        if path.is_symlink() or path.with_suffix(".json").is_symlink():
            raise ValueError("非公開セット内の symlink は使用できません")
        label = json.loads(path.with_suffix(".json").read_text(encoding="utf-8"))
        size = png_size(path)
        validate_label(label, size)
        if (label.get("schemaVersion") != 1 or label.get("size") != list(size)
                or label.get("sha256") != hashlib.sha256(path.read_bytes()).hexdigest()):
            raise ValueError(f"画像・寸法・ハッシュが登録時と異なります: {path.name}")
        frames.append((path, label))
    return frames


def evaluate(label: dict, color: str, selected: list | None) -> dict:
    truth = label[color]
    error = tolerance = None
    if truth == "absent":
        failure = "false-positive" if selected is not None else None
    elif selected is None:
        failure = "miss"
    else:
        def distance(a: dict, b: dict) -> float:
            return math.hypot(a["x"] - b["x"], a["y"] - b["y"])

        tolerance = max(12.0, 0.15 * distance(truth[0], truth[1]))
        # 平均ではなく、両端それぞれが許容誤差に収まることを要求する。
        error = min(max(distance(selected[i], order[i]) for i in (0, 1))
                    for order in (truth, truth[::-1]))
        failure = "endpoints" if error > tolerance else None
    known = label.get("expectedFail", {}).get(color)
    status = ("XFAIL" if known and known["failure"] == failure else "FAIL") if failure else ("XPASS" if known else "PASS")
    return {"color": color, "status": status, "failure": failure, "selected": selected,
            "truth": truth, "errorPx": error, "tolerancePx": tolerance,
            "expectedFail": known}


def run_set(directory: Path) -> list[dict]:
    frames = load_set(directory)
    rows = []
    with tempfile.TemporaryDirectory(prefix="phonesaber-field-host-") as temporary:
        # sandbox でも既定のホーム側キャッシュへの書込みを必要としない。
        previous = {key: os.environ.get(key) for key in ("CLANG_MODULE_CACHE_PATH", "SWIFT_MODULECACHE_PATH")}
        try:
            for key in previous:
                os.environ[key] = str(Path(temporary) / "module-cache")
            harness = Path(temporary) / "detector"
            lossless.compile_harness(harness)
            for path, label in frames:
                analysis = lossless.analyze_png(harness, path)
                for color in COLORS:
                    rows.append({"frame": path.stem, **evaluate(label, color, analysis["colors"][color]["selected"])})
        finally:
            for key, value in previous.items():
                if value is None:
                    os.environ.pop(key, None)
                else:
                    os.environ[key] = value
    return rows


def print_table(rows: list[dict]) -> None:
    print("画像 | 色 | 判定 | 内容 | 最大端点誤差 / 許容 px")
    names = {None: "一致", "miss": "本物未検出", "endpoints": "本物の端点不一致", "false-positive": "誤検出"}
    for row in rows:
        error = "-" if row["errorPx"] is None else f"{row['errorPx']:.1f} / {row['tolerancePx']:.1f}"
        reason = f"（既知: {row['expectedFail']['reason']}）" if row["expectedFail"] else ""
        print(f"{row['frame']} | {'赤' if row['color'] == 'red' else '青'} | {row['status']} | {names[row['failure']]}{reason} | {error}")
    print("集計: " + ", ".join(f"{s}={sum(r['status'] == s for r in rows)}" for s in ("PASS", "FAIL", "XFAIL", "XPASS")))


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    add = commands.add_parser("add", help="手動ラベルのある原寸 event PNG を非公開セットへコピー")
    add.add_argument("--labels", type=Path, required=True)
    add.add_argument("--inbox", type=Path, default=DEFAULT_INBOX)
    add.add_argument("--set-dir", type=Path, default=DEFAULT_SET)
    run = commands.add_parser("run", help="本番の静止画認識で両色を比較")
    run.add_argument("--set-dir", type=Path, default=DEFAULT_SET)
    run.add_argument("--json", type=Path, help="非公開の新規 JSON 結果ファイル（セット外）")
    args = parser.parse_args(argv)
    os.umask(0o077)
    try:
        if args.command == "add":
            print(f"登録: {add_frames(args.labels, args.inbox, args.set_dir)} 画像")
            return 0
        if args.json:
            output = outside_git(args.json)
            if output.is_relative_to(args.set_dir.expanduser().resolve()) or output.exists():
                raise ValueError("結果はセット外の新規ファイルへ保存してください")
        rows = run_set(args.set_dir)
        print_table(rows)
        if args.json:
            write_json(output, {"schemaVersion": 1, "rows": rows})
        return int(any(row["status"] == "FAIL" for row in rows))
    except (OSError, ValueError, RuntimeError, KeyError, TypeError) as error:
        print(f"エラー: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
