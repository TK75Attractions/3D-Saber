#!/usr/bin/env python3
"""Evidence-only Fix B replay; production source files are never written.

Consumes recognition_benchmark.py's local JSON, copies the current C++ core to
a temporary build directory and adds an observer after component scoring. The
observer measures the retained body's PCA and raw tail support. Every original
production JSON field (including double bits) must match the input benchmark.
Endpoint substitutions below are exploratory Python policies, never UDP output.
"""
from __future__ import annotations

import argparse
from collections import Counter
from dataclasses import dataclass, replace
import hashlib
import json
import math
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import time

REPO = Path(__file__).resolve().parents[2]
PHONE = REPO / "PhoneSaber"
CORE = PHONE / "android/core"
COLORS = ("red", "blue")
STRUCT = """struct OfflineTailEvidence {
    std::optional<Endpoints> body_pca;
    int body_points = 0, body_core = 0, body_high = 0;
    int tail_points = 0, tail_core = 0, tail_high = 0, core_groups = 0;
    double body_aspect = 0, body_extent = 0, axis_alignment = 0;
};
"""
SERIALIZER = r'''
    const auto& off = c.offline;
    std::cout << ",\"offline\":{\"body_pca\":";
    if (off.body_pca) endpoints(*off.body_pca); else std::cout << "null";
    std::cout << ",\"body_points\":" << off.body_points << ",\"body_core\":" << off.body_core
              << ",\"body_high\":" << off.body_high << ",\"tail_points\":" << off.tail_points
              << ",\"tail_core\":" << off.tail_core << ",\"tail_high\":" << off.tail_high
              << ",\"core_groups\":" << off.core_groups << ",\"body_aspect\":" << off.body_aspect
              << ",\"body_extent\":" << off.body_extent << ",\"axis_alignment\":" << off.axis_alignment << '}';
'''


def digest(data):
    return hashlib.sha256(data).hexdigest()


def signature(value):
    return digest(json.dumps(value, sort_keys=True, separators=(",", ":"), allow_nan=False).encode())


def core_digest(core):
    files = [core / "Makefile", *sorted((core / "src").glob("*")),
             *sorted((core / "include/phonesaber").glob("*")),
             *(core / "tools" / name for name in ("png_cli.cpp", "png.cpp", "png.hpp"))]
    return signature([(str(p.relative_to(core)), digest(p.read_bytes())) for p in files if p.is_file()])


def patch_once(path, marker, replacement):
    text = path.read_text()
    if text.count(marker) != 1:
        raise ValueError("offline observer insertion contract changed: " + path.name)
    path.write_text(text.replace(marker, replacement, 1))


def materialize(core, destination):
    """Copy first; only the disposable copy receives observer fields/calls."""
    destination.mkdir(parents=True)
    for name in ("src", "include", "tools"):
        shutil.copytree(core / name, destination / name)
    shutil.copy2(core / "Makefile", destination / "Makefile")
    header = destination / "include/phonesaber/core.hpp"
    patch_once(header, "struct Candidate {", STRUCT + "struct Candidate {\n    OfflineTailEvidence offline;")
    detection = destination / "src/detection.cpp"
    marker = "static int zero_run("
    helper = Path(__file__).with_name("fixb_offline_evidence.cpp.inc").read_text()
    patch_once(detection, marker, helper + "\n" + marker)
    marker = "    return Scored{c,points};"
    patch_once(detection, marker, "    c.offline = offline_measure(points,body,e,w,angle,projections);\n" + marker)
    pipeline = destination / "src/pipeline.cpp"
    marker = "            c.comparison_endpoints = {scale(c.comparison_endpoints.first),scale(c.comparison_endpoints.second)};"
    patch_once(pipeline, marker, "            if (c.offline.body_pca) c.offline.body_pca = Endpoints{scale(c.offline.body_pca->first),scale(c.offline.body_pca->second)};\n" + marker)
    cli = destination / "tools/png_cli.cpp"
    marker = '    std::cout << ",\\\"source\\\":\\\""'
    patch_once(cli, marker, SERIALIZER + "\n" + marker)


def image_paths(downloads, inbox):
    roots = sorted(p for p in downloads.glob("phonesaber_*_forensic") if p.is_dir())
    roots += sorted(p for p in inbox.rglob("images") if p.is_dir())
    roots += sorted(p for p in PHONE.rglob("*") if p.is_dir() and p.name.lower() == "fixtures")
    by_hash = {}
    for root in roots:
        for path in sorted(root.rglob("*")):
            if path.is_file() and path.suffix.lower() == ".png" and not any(
                    tag in path.name.lower() for tag in ("annotated", "annotation", "overlay")):
                by_hash.setdefault(digest(path.read_bytes()), path)
    return by_hash


def probe(baseline, paths):
    source_digest = core_digest(CORE)
    if source_digest != baseline["core_sha256"]:
        raise ValueError("benchmark core differs; regenerate baseline from this production revision")
    rows = {r["id"]: r for r in baseline["rows"]}
    ids = sorted(rows)
    if any(key not in paths for key in ids):
        raise ValueError("baseline ORIGINALs are missing or changed")
    result = {}
    with tempfile.TemporaryDirectory(prefix="phonesaber-fixb-offline-") as temp:
        copy = Path(temp) / "core"
        materialize(CORE, copy)
        build = Path(temp) / "bin"
        completed = subprocess.run(["make", "-C", str(copy), "BUILD=" + str(build),
                                    str(build / "phonesaber-png")], capture_output=True, timeout=600)
        if completed.returncode:
            # Compiler text contains only temporary source paths, not recordings.
            print(completed.stderr.decode(errors="replace"), file=sys.stderr)
            raise RuntimeError("offline observer build failed")
        for start in range(0, len(ids), 32):
            batch = ids[start:start + 32]
            completed = subprocess.run([str(build / "phonesaber-png"), *(str(paths[k]) for k in batch)],
                                       capture_output=True, timeout=180)
            if completed.returncode:
                raise RuntimeError("offline PNG replay failed (private decoder text suppressed)")
            documents = [json.loads(line) for line in completed.stdout.splitlines()]
            if len(documents) != len(batch):
                raise ValueError("offline CLI frame count mismatch")
            for frame, (key, document) in enumerate(zip(batch, documents)):
                if document["frame"] != frame:
                    raise ValueError("offline CLI frame order mismatch")
                colors = document["colors"]
                stripped = {color: {"selected": colors[color]["selected"], "candidates": [
                    {k: v for k, v in c.items() if k != "offline"} for c in colors[color]["candidates"]]}
                            for color in COLORS}
                if signature(stripped) != rows[key]["recognition_sha256"]:
                    raise ValueError("observer changed production recognition for anonymous image " + key[:16])
                result[key] = {**rows[key], "colors": colors}
    return [result[key] for key in ids]


def winner(row, color):
    return next((c for c in row["colors"][color]["candidates"] if c["eligible"]), None)


def distance(a, b):
    return math.hypot(a["x"] - b["x"], a["y"] - b["y"])


def length(endpoints):
    return distance(*endpoints)


def endpoint_delta(a, b):
    direct = [distance(a[0], b[0]), distance(a[1], b[1])]
    cross = [distance(a[0], b[1]), distance(a[1], b[0])]
    return max(cross if sum(cross) < sum(direct) else direct)


def stats(values):
    values = sorted(values)
    if not values:
        return {"n": 0, "p50": None, "p95": None, "max": None}
    return {"n": len(values), "p50": values[math.ceil(len(values) * .5) - 1],
            "p95": values[math.ceil(len(values) * .95) - 1], "max": values[-1]}


@dataclass(frozen=True)
class Gate:
    # Exploratory mask proxies, not calibrated or production-authorized values.
    divergence_ratio: float = 1.8
    body_min_px: float = 12
    body_aspect: float = 2
    body_extent: float = .10
    retained_min: float = .25
    continuity_min: float = .70
    tail_core_max: float = .05
    tail_high_max: float = .25
    axis_alignment_min: float = .90


def gate_reasons(candidate, gate):
    c, off = candidate, candidate["offline"]
    reasons = []
    ratio = c["raw_pca_span"] / max(c["robust_body_length"], 1)
    if ratio < gate.divergence_ratio:
        reasons.append("no_raw_body_divergence")
    if off["body_pca"] is None or off["body_points"] < 4 or (
            off["body_pca"] is not None and length(off["body_pca"]) < gate.body_min_px):
        reasons.append("body_pca_short_or_missing")
    if off["body_aspect"] < gate.body_aspect or off["body_extent"] < gate.body_extent:
        reasons.append("body_not_rod_proxy")
    if c["retained_body_ratio"] < gate.retained_min or c["longitudinal_continuity"] < gate.continuity_min:
        reasons.append("body_support_weak")
    if off["tail_points"] == 0:
        reasons.append("no_tail")
    else:
        if off["tail_core"] / off["tail_points"] > gate.tail_core_max:
            reasons.append("tail_core_supported")
        if off["tail_high"] / off["tail_points"] > gate.tail_high_max:
            reasons.append("tail_high_supported")
    if off["core_groups"] > 1:
        reasons.append("separated_core_groups")
    if off["axis_alignment"] < gate.axis_alignment_min:
        reasons.append("body_axis_disagrees")
    return reasons


def endpoints(row, color, policy, gate=Gate()):
    c = winner(row, color)
    if c is None:
        return None
    if policy == "production":
        return c["endpoints"]
    robust = c["production"]["robust_endpoints"]
    if policy == "robust_all":
        return robust if robust is not None else c["endpoints"]
    if policy in ("divergent_robust", "divergent_body_pca"):
        replacement = robust if policy == "divergent_robust" else c["offline"]["body_pca"]
        if replacement and c["raw_pca_span"] >= gate.divergence_ratio * max(c["robust_body_length"], 1):
            return replacement
        return c["endpoints"]
    if policy == "measured_body":
        return c["offline"]["body_pca"] if not gate_reasons(c, gate) else c["endpoints"]
    raise ValueError("unknown offline policy")


def change_counts(rows, policy, gate=Gate()):
    result = {}
    for color in COLORS:
        result[color] = {}
        for truth in ("no_saber", "saber", "unknown"):
            selected = [r for r in rows if r["truth"][color] == truth]
            changed, deltas, shortened = 0, [], 0
            detection_changes = 0
            for row in selected:
                before, after = endpoints(row, color, "production"), endpoints(row, color, policy, gate)
                detection_changes += (before is None) != (after is None)
                if before is not None and after is not None:
                    delta = endpoint_delta(before, after)
                    changed += delta != 0
                    if delta:
                        deltas.append(delta)
                        shortened += length(after) < length(before)
            result[color][truth] = {"frames": len(selected), "changed": changed,
                                    "shortened": shortened, "detection_changes": detection_changes,
                                    "endpoint_delta_px": stats(deltas)}
    return result


def static_jitter(rows, labels, policy, gate=Gate()):
    by_hash = {r["id"]: r for r in rows}
    groups = {}
    for label in labels:
        if label["motion"] != "static" or label["color"] != "blue":
            continue
        pairs = list(zip(label["frames"], label["images"]))
        group = digest((label["session"] + ":blue").encode())[:16]
        values = groups.setdefault(group, {"deltas": [], "ge50": 0, "missing_pairs": 0, "changed_frames": []})
        for _, image in pairs:
            if image["sha256"] not in by_hash:
                raise ValueError("static ORIGINAL missing from baseline")
            row = by_hash[image["sha256"]]
            before, after = endpoints(row, "blue", "production"), endpoints(row, "blue", policy, gate)
            if before and after and endpoint_delta(before, after):
                if row["id"] not in values["changed_frames"]:
                    values["changed_frames"].append(row["id"])
        for (frame_a, image_a), (frame_b, image_b) in zip(pairs, pairs[1:]):
            if frame_b != frame_a + 1:
                continue
            a = endpoints(by_hash[image_a["sha256"]], "blue", policy, gate)
            b = endpoints(by_hash[image_b["sha256"]], "blue", policy, gate)
            if a is None or b is None:
                values["missing_pairs"] += 1
                continue
            delta = endpoint_delta(a, b)
            values["deltas"].append(delta)
            values["ge50"] += delta >= 50
    return {group: {"displacement_px": stats(v["deltas"]), "ge50": v["ge50"],
                    "missing_pairs": v["missing_pairs"], "changed_frames": len(v["changed_frames"])}
            for group, v in sorted(groups.items())}


def protections(rows, manifest, policy, gate=Gate()):
    by_hash = {r["id"]: r for r in rows}
    failures, changed, protected_changed = [], [], []
    for fixture in manifest["fixtures"]:
        row = by_hash[fixture["sha256"]]
        color = fixture["color"].lower()
        before = endpoints(row, color, "production")
        after = endpoints(row, color, policy, gate)
        key = digest((fixture["sha256"] + ":" + color).encode())[:16]
        if (after is not None) != fixture["expectedDetected"]:
            failures.append(key)
        elif after is not None and fixture.get("expectedEndpoint") is not None:
            if endpoint_delta(fixture["expectedEndpoint"], after) > fixture["endpointTolerancePx"]:
                failures.append(key)
        if before and after and endpoint_delta(before, after):
            changed.append(key)
            if fixture["failureClass"] in ("B", "G") or fixture["scenario"] == "blue-frame-0600":
                protected_changed.append(key)
    return {"fixtures": len(manifest["fixtures"]), "endpoint_or_detection_failures": len(failures),
            "changed_fixtures": len(changed), "long_or_point_led_changed": len(protected_changed)}


def analyze(rows, baseline, labels, manifest):
    policies = ("production", "robust_all", "divergent_robust", "divergent_body_pca", "measured_body")
    report = {"schema_version": 1, "baseline_commit": baseline["commit"],
              "core_sha256": baseline["core_sha256"], "inventory_sha256": baseline["inventory_sha256"],
              "unique_images": len(rows), "observer_production_mismatches": 0,
              "gate": Gate().__dict__, "policies": {}}
    for policy in policies:
        report["policies"][policy] = {"changes": change_counts(rows, policy),
                                       "static_blue": static_jitter(rows, labels, policy),
                                       "formal": protections(rows, manifest, policy)}
    reasons = Counter()
    for row in rows:
        for color in COLORS:
            c = winner(row, color)
            if c is not None:
                reasons.update(gate_reasons(c, Gate()))
    report["gate_veto_reasons"] = dict(sorted(reasons.items()))
    static_evidence = []
    by_hash = {r["id"]: r for r in rows}
    for label in labels:
        if label["motion"] != "static" or label["color"] != "blue":
            continue
        for frame, image in zip(label["frames"], label["images"]):
            c = winner(by_hash[image["sha256"]], "blue")
            if c is None:
                continue
            off = c["offline"]
            static_evidence.append({"id": image["sha256"], "frame": frame,
                "group": digest((label["session"] + ":blue").encode())[:16],
                "raw_span": c["raw_pca_span"], "robust_span": c["robust_body_length"],
                "body_aspect": off["body_aspect"], "axis_alignment": off["axis_alignment"],
                "tail_core_ratio": off["tail_core"] / off["tail_points"] if off["tail_points"] else None,
                "tail_high_ratio": off["tail_high"] / off["tail_points"] if off["tail_points"] else None,
                "core_groups": off["core_groups"], "vetoes": gate_reasons(c, Gate())})
    report["static_blue_evidence"] = static_evidence
    report["sensitivity"] = []
    for tail_core in (.05, .10, .20):
        for alignment in (.70, .90, .95):
            gate = replace(Gate(), tail_core_max=tail_core, axis_alignment_min=alignment)
            report["sensitivity"].append({"tail_core_max": tail_core, "axis_alignment_min": alignment,
                "changes": change_counts(rows, "measured_body", gate),
                "static_blue": static_jitter(rows, labels, "measured_body", gate),
                "formal": protections(rows, manifest, "measured_body", gate)})
    return report


def fmt(value):
    return "—" if value is None else f"{value:.2f}"


def markdown(report):
    static = next(v for v in report["policies"]["production"]["static_blue"].values()
                  if v["displacement_px"]["n"] == 9)["displacement_px"]
    robust = next(v for v in report["policies"]["divergent_robust"]["static_blue"].values()
                  if v["displacement_px"]["n"] == 9)["displacement_px"]
    gate_changes = report["policies"]["measured_body"]["changes"]
    lines = ["# Fix B: raw PCA tail / robust body の offline 試作", "",
             f"基準 commit `{report['baseline_commit']}`、source SHA-256 `{report['core_sha256']}`。",
             f"benchmark と同じ ORIGINAL **{report['unique_images']} 種類**で評価。コーパス SHA-256 `{report['inventory_sha256']}`。",
             f"追加 observer を除いた候補・score・eligibility・winner・端点・診断の差分 **{report['observer_production_mismatches']}**。",
             "", "## 結論と gate", "",
             f"乖離時の robust 置換で、静止青9遷移の D 中央値 **{fmt(static['p50'])}→{fmt(robust['p50'])}px**、最大 **{fmt(static['max'])}→{fmt(robust['max'])}px**。ただし formal 端点条件は **{report['policies']['divergent_robust']['formal']['endpoint_or_detection_failures']}件失敗**した。",
             f"保守的な measured_body 代理 gate では、ラベル済み剣あり/なしの端点変更は **{sum(gate_changes[c][t]['changed'] for c in COLORS for t in ('saber', 'no_saber'))}件**、静止青の揺れも変わらない。unknown 赤のみ **{gate_changes['red']['unknown']['changed']}件**変更。",
             "これは §4 B の証拠収集。production 採用・gate 通過を意味しない。順位・eligibility・候補生成・定数・UDP は変更しない。",
             "baseline JSON の winner を固定し、端点だけを offline で置換する。検出の有無は全案で変わらないため、FP を消す修正ではない。",
             "一律 robust 置換は効果を見る対照。divergent_body_pca は乖離条件だけで body PCA を使う対照。両案とも弱 tail・分離 LED 保護を保証しない。",
             "measured_body は production core の一時コピーに観測処理を追加し、実際の dominant_body 選択点から PCA を再計算する。",
             "robust interval の端点は元の raw 軸への投影であり、body 点だけの PCA とは別。両方を混同しない。", "",
             "探索用 measured_body 条件（未校正、production threshold ではない）:", "",
             "- raw/robust span 比 ≥1.8、body PCA 長 ≥12px、4点以上、body aspect ≥2、extent ≥0.10。",
             "- retained ≥0.25、既存 body continuity ≥0.70、body/raw 軸の |cos| ≥0.90。",
             "- tail は body 外の候補点。tail の core 比 ≤0.05、value≥220 比 ≤0.25。tail なしでは適用しない。",
             "- raw 軸で3空 bin 以上離れ、各3 core 点以上の group が複数ある候補は拒否。bin は sample grid（step=2）。",
             "", "core group の veto は mask 上の代理条件であり、物理的に分離 LED ではないという証明ではない。body の棒形状も実剣の意味ラベルを証明しない。", "",
             "## 何枚の端点が変わるか（画像 SHA-256 で重複除外）", "",
             "| 案 | 色 | 剣なし 変更 / n | 剣あり 変更 / n | unknown 変更 / n | 検出有無の変更 |",
             "|---|---|---:|---:|---:|---:|"]
    for policy, result in report["policies"].items():
        for color in COLORS:
            c = result["changes"][color]
            counts = [f"{c[t]['changed']} / {c[t]['frames']}" for t in ("no_saber", "saber", "unknown")]
            lines.append(f"| {policy} | {color} | " + " | ".join(counts) + f" | {sum(c[t]['detection_changes'] for t in c)} |")
    lines += ["", "## ほぼ静止の青: 現行 recognizer の再測定", "",
              "| 匿名区間 | 案 | 隣接遷移 n | D p50 | p95 | max | ≥50px | 変更 frame |",
              "|---|---|---:|---:|---:|---:|---:|---:|"]
    for policy, result in report["policies"].items():
        for group, cell in result["static_blue"].items():
            s = cell["displacement_px"]
            lines.append(f"| `{group}` | {policy} | {s['n']} | {fmt(s['p50'])} | {fmt(s['p95'])} | {fmt(s['max'])} | {cell['ge50']} | {cell['changed_frames']} |")
    lines += ["", "9遷移の区間は手ラベルの3/4/5 frame窓を合算、7遷移の区間は8 frame窓。窓間や欠測をまたがない。",
              "D は直前との距離和が小さい端点対応で max(|ΔA|,|ΔB|) を計算。source pixel 単位、nearest-rank quantile。",
              "小さい手ぶれを含む。旧 jitter-analysis の保存時の53.1→8.2pxとは recognizer の版・winner が違うため、その効果量を流用しない。",
              "winner が実剣上にあることや端点の真の位置はこのラベルだけでは保証されず、揺れの低下を精度改善と同一視しない。", "",
              "## 静止青9遷移区間の gate 観測", "",
              "| frame | raw / robust px | body aspect | tail core / high 比 | core group | veto |",
              "|---:|---:|---:|---:|---:|---|"]
    group9 = next(k for k, v in report["policies"]["production"]["static_blue"].items()
                  if v["displacement_px"]["n"] == 9)
    for cell in report["static_blue_evidence"]:
        if cell["group"] == group9:
            lines.append(f"| {cell['frame']} | {fmt(cell['raw_span'])} / {fmt(cell['robust_span'])} | {fmt(cell['body_aspect'])} | {fmt(cell['tail_core_ratio'])} / {fmt(cell['tail_high_ratio'])} | {cell['core_groups']} | " + ", ".join(cell["vetoes"]) + " |")
    lines += ["", "raw/robust が乖離する11 frameはすべて複数 core group と明るい tail を含む。弱 tail と非分離 LED の代理条件を同時に満たさない。",
              "背景・反射にも発光支持があるため、閾値を緩めて短くしただけでは §4 B の gate 根拠にならない。", "",
              "## 長剣・分離 LED と formal 期待端点", "",
              "| 案 | formal 条件失敗 / 40 | 変更 fixture | 長剣/point LED の変更 |",
              "|---|---:|---:|---:|"]
    for policy, result in report["policies"].items():
        f = result["formal"]
        lines.append(f"| {policy} | {f['endpoint_or_detection_failures']} / {f['fixtures']} | {f['changed_fixtures']} | {f['long_or_point_led_changed']} |")
    lines += ["", "この表は既存 formal の検出/端点 tolerance 条件のみ。candidate type/rejection の正式検証は別途 unchanged production に実施する。",
              "長剣/point LED は manifest failure class B/G と長い blue-frame-0600。未収集の長剣・分離 LED・端欠けへの安全性は未証明。", "",
              "## 代理 gate の感度分析", "",
              "| tail core 上限 | 軸 | 剣なし赤/青 変更 | 剣あり赤/青 変更 | 9遷移区間 p50 / max | formal 失敗 |",
              "|---:|---:|---:|---:|---:|---:|"]
    for item in report["sensitivity"]:
        changes = item["changes"]
        static = next(v for v in item["static_blue"].values() if v["displacement_px"]["n"] == 9)["displacement_px"]
        lines.append(f"| {item['tail_core_max']:.2f} | {item['axis_alignment_min']:.2f} | " +
                     "/".join(str(changes[c]["no_saber"]["changed"]) for c in COLORS) + " | " +
                     "/".join(str(changes[c]["saber"]["changed"]) for c in COLORS) +
                     f" | {fmt(static['p50'])} / {fmt(static['max'])} | {item['formal']['endpoint_or_detection_failures']} |")
    lines += ["", "条件を少し動かしただけで変わる frame と境界の不連続を確認するための表。最良値を production へ採用する探索ではない。",
              "全体の veto 理由（非排他）: `" + json.dumps(report["gate_veto_reasons"], sort_keys=True) + "`。", "",
              "## 再現と制約", "", "```bash",
              "# recognition-benchmark branch のツール、または両 branch を統合した worktree で baseline 作成",
              "python3 -B PhoneSaber/tools/recognition_benchmark.py --json /tmp/recognition-before.json",
              "python3 -B PhoneSaber/tools/fixb_offline.py --benchmark /tmp/recognition-before.json --json /tmp/fixb-offline.json",
              "python3 -B -m unittest discover -s PhoneSaber/tools -p test_fixb_offline.py -v", "```", "",
              "二つの task branch はそれぞれ origin/main 起点。fixb-offline branch 単独では、最初のコマンドだけ benchmark branch で実行する。",
              "baseline の source fingerprint が現在の core と一致しない、原画像が失われた、observer が recognition を変えた場合は停止する。",
              "observer の挿入位置は厳密に照合する。production ファイルを変更せず、一時コピーと実行ファイルは終了時に削除する。",
              "詳細 JSON・PNG・絶対パスはコミットしない。候補 identity の時系列選好、予測、UDP、Unity はこの replay の対象外。",
              "§4 B に必要な単独 body の妥当性、tail の物理的意味、未使用 capture での分離 LED 保護、境界の安定性、修正 A 後の順序条件は未解決。",
              f"測定時間 {report['seconds']:.2f} 秒（offline CLI の一時 build と解析を含む）。"]
    return "\n".join(lines) + "\n"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--benchmark", type=Path, required=True)
    parser.add_argument("--downloads", type=Path, default=Path.home() / "Downloads")
    parser.add_argument("--inbox", type=Path, default=Path.home() / "Library/Application Support/PhoneSaber/diagnostics-inbox")
    parser.add_argument("--labels", type=Path, default=PHONE / "docs/claude/data/jitter-image-labels.json")
    parser.add_argument("--manifest", type=Path, default=PHONE / "ios/PhoneSaberSender/Tools/lossless_regression_manifest.json")
    parser.add_argument("--report", type=Path, default=PHONE / "docs/claude/fixb-offline.md")
    parser.add_argument("--json", type=Path, help="local detailed anonymous evidence; do not commit")
    args = parser.parse_args()
    start = time.perf_counter()
    baseline = json.loads(args.benchmark.read_text())
    rows = probe(baseline, image_paths(args.downloads, args.inbox))
    labels = json.loads(args.labels.read_text())["labels"]
    manifest = json.loads(args.manifest.read_text())
    report = analyze(rows, baseline, labels, manifest)
    report["seconds"] = time.perf_counter() - start
    args.report.write_text(markdown(report))
    if args.json:
        args.json.write_text(json.dumps({**report, "rows": rows}, ensure_ascii=False, indent=2, allow_nan=False) + "\n")
    print(json.dumps({k: report[k] for k in ("unique_images", "observer_production_mismatches", "seconds", "policies")}, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        detail = str(error) if isinstance(error, (ValueError, RuntimeError)) else type(error).__name__
        print("offline replay failed: " + detail, file=sys.stderr)
        raise SystemExit(2)
