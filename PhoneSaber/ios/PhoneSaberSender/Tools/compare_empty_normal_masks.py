#!/usr/bin/env python3
"""空の通常 mask の省略を、同一 Swift テスト・-O 設定・原寸 PNG で前後比較する。

事前に BGRADetection.swift を Git 外へコピーして --before-source に渡す。
--manifest は [{"path": "/private/.../images/image_10_event_123.png"}, ...]。
画像リスト・撮影時設定・ハッシュ・生計測・厳密比較結果は Git 外へ保存する。
"""
from __future__ import annotations

import argparse
import csv
import hashlib
import json
import math
import platform
from pathlib import Path
import statistics
import subprocess

from field_regression import png_size
from phone_saber_clip_report import validate_frame_integrity

ROOT = Path(__file__).resolve().parents[1]
TEST = ROOT.parent / "PhoneSaberSenderTests/StaticBGRADetectionTests.swift"
MODES = [("160-off", 160, "off"), ("2000-on", 2000, "on"), ("160-on", 160, "on")]


def sha(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def execute(command: list[str]) -> bytes:
    return subprocess.run(command, check=True, stdout=subprocess.PIPE).stdout


def validate_images(manifest: Path) -> list[dict]:
    images = []
    for row in json.loads(manifest.read_text()):
        path = Path(row["path"]).expanduser().resolve()
        clip = path.parent.parent
        if "144202_638_confusion_blue" in clip.name:
            raise ValueError("既知の破損 clip は比較に使えません")
        summary_path = clip / "summary.json"
        summary = json.loads(summary_path.read_text())
        validate_frame_integrity(clip, summary)
        metadata = next(i for i in summary["images"] if clip / i["path"] == path)
        if metadata.get("scale") != 1 or metadata.get("role") != "event":
            raise ValueError(f"原寸 event PNG が必要です: {path}")
        if f'event_{metadata["frameID"]}.png' not in path.name:
            raise ValueError(f"PNG 名と frameID が不一致: {path}")
        size = png_size(path)
        context = summary["autoClip"].get("contextImages", {})
        if context.get("scale"):
            expected = tuple(round(context[k] / context["scale"]) for k in ("width", "height"))
            if size != expected:
                raise ValueError(f"PNG と記録寸法が不一致: {path}: {size} != {expected}")
        frame_context = clip / metadata["frameContextPath"]
        images.append({"path": str(path), "frameID": metadata["frameID"], "size": size,
                       "sha256": sha(path), "summarySHA256": sha(summary_path),
                       "contextSHA256": sha(frame_context),
                       "capturedSettings": summary["autoClip"].get("camera", {}),
                       "integrity": "PASS"})
    if not images or len({i["path"] for i in images}) != len(images):
        raise ValueError("空または重複した画像リスト")
    return images


def stats(samples: list[float]) -> dict:
    ordered = sorted(samples)
    return {"median": statistics.median(samples), "p95": ordered[math.ceil(len(samples) * .95) - 1],
            "max": max(samples), "total": sum(samples), "n": len(samples)}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--before-source", type=Path, required=True)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--iterations", type=int, default=100)
    parser.add_argument("--rounds", type=int, default=7)
    args = parser.parse_args()
    output = args.output.expanduser().resolve()
    if any((p / ".git").exists() for p in [output, *output.parents]):
        parser.error("非公開計測結果は Git 外へ保存してください")
    if args.iterations < 1 or args.rounds < 3:
        parser.error("iterations >= 1, rounds >= 3 が必要です")
    output.mkdir(parents=True, exist_ok=False)
    images = validate_images(args.manifest)
    production = ROOT / "PhoneSaberSender/BGRADetection.swift"
    sources = {"before": args.before_source.resolve(), "after": production}
    metadata = {"host": platform.platform(), "flags": ["-O", "-module-name", "EmptyMaskComparison"],
                "testSHA256": sha(TEST), "coreSHA256": sha(ROOT / "PhoneSaberSender/DetectionCore.swift"),
                "compiler": execute(["xcrun", "swiftc", "--version"]).decode().strip(), "sources": {k: sha(v) for k, v in sources.items()},
                "images": images, "iterations": args.iterations, "rounds": args.rounds,
                "warmupPerInvocation": 5, "timing": "host analyzeSabers wall time; PNG decode excluded"}
    (output / "manifest.json").write_text(json.dumps(metadata, indent=2, ensure_ascii=False) + "\n")
    binaries = {}
    for name, source in sources.items():
        binary = output / name
        execute(["xcrun", "swiftc", *metadata["flags"], str(ROOT / "PhoneSaberSender/DetectionCore.swift"),
                 str(source), str(TEST), "-module-cache-path", str(output / "module-cache"), "-o", str(binary)])
        binaries[name] = str(binary)
    comparisons = []
    for mode, iso, emitter in MODES:
        settings = ["--shutter", "2000", "--iso", str(iso), "--emitter", emitter]
        for kind in ["--empty-normal-signatures", "--lossless-signatures"]:
            command = [kind, *(i["path"] for i in images)] if kind == "--lossless-signatures" else [kind]
            signatures = []
            for name, binary in binaries.items():
                result = execute([binary, *command, *settings])
                (output / f"{name}-{mode}-{kind[2:]}.txt").write_bytes(result)
                signatures.append(result)
            if signatures[0] != signatures[1]:
                raise RuntimeError(f"厳密比較が不一致: {mode} {kind}")
            comparisons.append({"mode": mode, "kind": kind, "bytes": len(signatures[0]),
                                "sha256": hashlib.sha256(signatures[0]).hexdigest(), "result": "PASS"})
            print(f"strict {mode} {kind}: PASS", flush=True)
    (output / "comparisons.json").write_text(json.dumps(comparisons, indent=2) + "\n")
    records = []
    # 前後の実行順を round ごとに反転する。画像読み込み・監査・段階計測は timed loop 外。
    with (output / "runs.jsonl").open("w") as raw:
        for round_index in range(args.rounds):
            names = ["before", "after"] if round_index % 2 == 0 else ["after", "before"]
            for mode, iso, emitter in MODES:
                for image in images:
                    for name in names:
                        result = execute([binaries[name], "--benchmark", image["path"], str(args.iterations),
                                          "--shutter", "2000", "--iso", str(iso), "--emitter", emitter])
                        (output / f'{name}-{mode}-{images.index(image)}-{round_index}.log').write_bytes(result)
                        for line in result.decode().splitlines():
                            if line.startswith("[NativeJSON] "):
                                record = json.loads(line[len("[NativeJSON] "):])
                                record.update(build=name, mode=mode, round=round_index)
                                records.append(record)
                                raw.write(json.dumps(record) + "\n"); raw.flush()
            print(f"timing round {round_index + 1}/{args.rounds}", flush=True)
    rows = []
    for mode, _, _ in MODES:
        for image in images:
            row = {"mode": mode, "image": image["path"]}
            for name in binaries:
                runs = [r for r in records if r["mode"] == mode and r["image"] == image["path"]
                        and r["build"] == name and r["case"] == "bright"]
                samples = [s for r in runs for s in r["samplesMs"]]
                row.update({f"{name}_{k}" if k == "n" else f"{name}_{k}_ms": v for k, v in stats(samples).items()})
                row[f"{name}_round_medians_ms"] = [statistics.median(r["samplesMs"]) for r in runs]
                row["skippedColorsPerFrame"] = len(runs[0]["emptyNormalLowISOColors"])
            row["median_delta_ms"] = row["after_median_ms"] - row["before_median_ms"]
            row["median_delta_percent"] = 100 * row["median_delta_ms"] / row["before_median_ms"]
            rows.append(row)
    if validate_images(args.manifest) != images:
        raise RuntimeError("計測中に画像または JSON が変更されました")
    aggregates = []
    for mode, _, _ in MODES:
        subset = [r for r in rows if r["mode"] == mode]
        before = sum(r["before_total_ms"] for r in subset)
        after = sum(r["after_total_ms"] for r in subset)
        aggregates.append({"mode": mode, "before_total_ms": before, "after_total_ms": after,
                           "delta_ms": after - before, "delta_percent": 100 * (after / before - 1),
                           "eligibleImages": sum(r["skippedColorsPerFrame"] > 0 for r in subset),
                           "skippedColorPaths": sum(r["skippedColorsPerFrame"] for r in subset)
                               * args.iterations * args.rounds})
    (output / "aggregate.json").write_text(json.dumps(aggregates, indent=2) + "\n")
    (output / "summary.json").write_text(json.dumps(rows, indent=2) + "\n")
    with (output / "summary.csv").open("w") as handle:
        writer = csv.DictWriter(handle, fieldnames=list(rows[0]))
        writer.writeheader(); writer.writerows(rows)
    print(f"Saved: {output}", flush=True)


if __name__ == "__main__":
    main()
