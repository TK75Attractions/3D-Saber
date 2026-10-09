#!/usr/bin/env python3
"""Replay ORIGINAL PNGs through the unchanged production C++ recognizer.

Only hash-verified human truth labels are used for precision/recall denominators.
Recorded outputs and JNI expectations are recognition regressions, not truth.
No dependencies beyond Python 3.10+, make, clang++ and zlib. JSON contains only
anonymous IDs and current recognizer output; keep detailed output local.
"""
from __future__ import annotations

import argparse
from collections import Counter
from dataclasses import dataclass
import hashlib
import json
import math
from pathlib import Path
import subprocess
import sys
import tempfile
import time

REPO = Path(__file__).resolve().parents[2]
PHONE = REPO / "PhoneSaber"
COLORS = ("red", "blue")
LABELS = PHONE / "docs/claude/data/jitter-image-labels.json"
MANIFEST = PHONE / "ios/PhoneSaberSender/Tools/lossless_regression_manifest.json"
FIXTURES = PHONE / "ios/PhoneSaberSenderTests/Fixtures"


def sha256(data):
    return hashlib.sha256(data).hexdigest()


def signature(value):
    return sha256(json.dumps(value, sort_keys=True, separators=(",", ":"),
                             allow_nan=False).encode())


@dataclass(frozen=True)
class Image:
    path: Path
    digest: str
    origin: str
    occurrence: str


def original(path):
    return path.suffix.lower() == ".png" and not any(
        word in path.name.lower() for word in ("annotated", "annotation", "overlay"))


def discover(downloads, inbox, phone=PHONE):
    """Stable file order; run every copy, aggregate unique bytes separately."""
    entries, excluded = [], Counter()
    roots = [(p, "forensic", downloads) for p in sorted(downloads.glob("phonesaber_*_forensic"))
             if p.is_dir()]
    roots += [(p, "inbox", inbox) for p in sorted(inbox.rglob("images")) if p.is_dir()]
    roots += [(p, "fixture", phone) for p in sorted(phone.rglob("*"))
              if p.is_dir() and p.name.lower() == "fixtures"]
    seen = []
    for root, origin, relative_root in roots:
        for path in sorted(root.rglob("*")):
            if not path.is_file() or path.suffix.lower() != ".png" or path in seen:
                continue
            seen.append(path)
            if not original(path):
                excluded[origin] += 1
                continue
            relative = str(path.relative_to(relative_root))
            entries.append(Image(path, sha256(path.read_bytes()), origin,
                                 sha256((origin + ":" + relative).encode())))
    return entries, dict(excluded)


def contained(root, relative):
    path = (root / relative).resolve()
    if not path.is_relative_to(root.resolve()) or not original(path):
        raise ValueError("label/manifest must reference a corpus-relative ORIGINAL PNG")
    return path


def truth_index(labels_path, manifest_path, downloads, inbox, fixtures=FIXTURES):
    """Join truth by verified PNG hash, never by basename or detector outcome."""
    index, sources, missing = {}, Counter(), Counter()

    def add(digest, color, truth, source):
        if color not in COLORS:
            raise ValueError("unsupported truth color")
        key = (digest, color)
        if key in index and index[key] != truth:
            raise ValueError("conflicting truth for anonymous image " + digest[:16])
        index[key] = truth
        sources[source] += 1

    def verify(root, item, source):
        path = contained(root, item["path"])
        if not path.is_file():
            missing[source] += 1
            return None
        digest = sha256(path.read_bytes())
        if digest != item["sha256"]:
            raise ValueError("truth evidence hash mismatch: " + sha256(str(item["path"]).encode())[:16])
        return digest

    labels = json.loads(labels_path.read_text())["labels"] if labels_path else []
    for label in labels:
        truth = {"static": "saber", "moving": "saber", "no_saber": "no_saber"}.get(label["motion"])
        if truth is None:
            continue
        for item in label["images"]:
            root = inbox if Path(item["path"]).parts[0].startswith("phone_saber_triage_") else downloads
            digest = verify(root, item, "hand")
            if digest:
                add(digest, label["color"].lower(), truth, "hand")
    manifest = json.loads(manifest_path.read_text()) if manifest_path else {"fixtures": []}
    for item in manifest["fixtures"]:
        truth = {"positive": "saber", "negative": "no_saber"}.get(item.get("truth"))
        digest = verify(fixtures, item, "manifest")
        if digest and truth:
            add(digest, item["color"].lower(), truth, "manifest")
    return index, dict(sources), dict(missing)


def winner(color):
    # pipeline.cpp sorts candidates then selects the first eligible entry.
    result = next((c for c in color["candidates"] if c["eligible"]), None)
    if (result is None) != (color["selected"] is None) or (
            result is not None and result["endpoints"] != color["selected"]):
        raise ValueError("CLI selection contract changed")
    return result


def length(endpoints):
    a, b = endpoints
    return math.hypot(a["x"] - b["x"], a["y"] - b["y"])


def stats(values):
    ordered = sorted(values)
    if not ordered:
        return {"n": 0, "mean": None, "p50": None, "p95": None, "max": None, "min": None}
    return {"n": len(ordered), "mean": math.fsum(ordered) / len(ordered),
            "p50": ordered[math.ceil(len(ordered) * .5) - 1],
            "p95": ordered[math.ceil(len(ordered) * .95) - 1],
            "min": ordered[0], "max": ordered[-1]}


def summarize(rows):
    summary = {}
    for color in COLORS:
        result = {}
        for truth in ("no_saber", "saber", "unknown"):
            selected = [r for r in rows if r["truth"][color] == truth]
            detected = [r["colors"][color] for r in selected if r["colors"][color]["selected"] is not None]
            result[truth] = {"frames": len(selected), "detected": len(detected),
                             "rate": len(detected) / len(selected) if selected else None,
                             "winner_sources": dict(sorted(Counter(winner(c)["source"] for c in detected).items())),
                             "length_px": stats([length(c["selected"]) for c in detected])}
        summary[color] = result
    return summary


def measure(cli, images, truths):
    rows = []
    for image in images:
        start = time.perf_counter_ns()
        run = subprocess.run([str(cli), str(image.path)], capture_output=True, timeout=30)
        elapsed = (time.perf_counter_ns() - start) / 1e6
        if run.returncode:
            # Decoder errors can contain a private path; report only the ID.
            raise RuntimeError("recognizer failed for anonymous image " + image.digest[:16])
        document = json.loads(run.stdout)
        for color in COLORS:
            winner(document["colors"][color])
        rows.append({"id": image.digest, "occurrence_id": image.occurrence, "origin": image.origin,
                     "truth": {c: truths.get((image.digest, c), "unknown") for c in COLORS},
                     "colors": document["colors"], "runtime_ms": elapsed,
                     "recognition_sha256": signature(document["colors"])})
    return rows


def unique_rows(rows):
    unique = {}
    for row in rows:
        if row["id"] in unique and row["recognition_sha256"] != unique[row["id"]]["recognition_sha256"]:
            raise ValueError("identical input produced different recognition: " + row["id"][:16])
        unique.setdefault(row["id"], row)
    return [unique[key] for key in sorted(unique)]


def check_jni_expectations(rows, path):
    """Regression outputs are checked, but never promoted to semantic truth."""
    images = json.loads(path.read_text())["images"]
    by_hash = {row["id"]: row for row in unique_rows(rows)}
    checked, missing, mismatches = 0, 0, 0
    for image in images:
        row = by_hash.get(image["sha256"])
        if row is None:
            missing += 1
            continue
        for color in COLORS:
            actual = row["colors"][color]
            expected = image["colors"][color]
            candidate = winner(actual)
            source = candidate["source"] if candidate else None
            checked += 1
            mismatches += (actual["selected"] != expected["selected"] or source != expected["candidateType"])
    return {"color_frames_checked": checked, "missing_images": missing, "mismatches": mismatches}


def compare(before, after):
    left = {r["id"]: r for r in unique_rows(before["rows"])}
    right = {r["id"]: r for r in unique_rows(after["rows"])}
    shared = sorted(k for k in left if k in right)
    return {"shared_images": len(shared), "removed_images": len(left) - len(shared),
            "added_images": len(right) - len(shared),
            "truth_changed_images": sum(left[k]["truth"] != right[k]["truth"] for k in shared),
            "recognition_changed_images": sum(left[k]["recognition_sha256"] != right[k]["recognition_sha256"] for k in shared),
            "same_corpus": before["inventory_sha256"] == after["inventory_sha256"],
            "runtime_p50_ms_before": before["runtime_ms"]["p50"],
            "runtime_p50_ms_after": after["runtime_ms"]["p50"]}


def core_digest(core):
    files = [core / "Makefile", *sorted((core / "src").glob("*")),
             *sorted((core / "include/phonesaber").glob("*")),
             *(core / "tools" / name for name in ("png_cli.cpp", "png.cpp", "png.hpp"))]
    return signature([(str(p.relative_to(core)), sha256(p.read_bytes())) for p in files if p.is_file()])


def build_cli(core, destination):
    run = subprocess.run(["make", "-C", str(core), "BUILD=" + str(destination),
                          str(destination / "phonesaber-png")], capture_output=True, timeout=600)
    if run.returncode:
        raise RuntimeError("production CLI build failed; run make in android/core to inspect")
    return destination / "phonesaber-png"


def fmt(value):
    return "—" if value is None else f"{value:.2f}"


def markdown(report):
    lines = ["# 現行 production 認識 PNG benchmark", "",
             f"基準 commit `{report['commit']}`、recognizer source SHA-256 `{report['core_sha256']}`。",
             "production C++ `phonesaber-png` を変更せず、step=2、既定設定で全 ORIGINAL を再認識した。",
             "過去の metadata 出力・予測・tracking の再集計ではない。画像・ローカルパス・詳細診断はコミットしない。", "",
             "## コーパスとラベル", "",
             f"全ファイル **{len(report['rows'])}**、同一 SHA-256 を一度だけ数えた画像 **{report['unique_images']}**。",
             f"内訳: forensic {report['origins'].get('forensic', 0)}、inbox {report['origins'].get('inbox', 0)}、fixture {report['origins'].get('fixture', 0)}。",
             f"annotated 等の除外: {sum(report['excluded'].values())}。重複コピーもすべて実行し、認識一致を確認した。",
             "率の主表は画像の SHA-256 で重複除外。色別にラベルを付け、もう一方の色へ推定しない。",
             "手ラベルの static/moving は saber、no_saber は剣なし。formal manifest の truth positive/negative も使用し、元 PNG hash を照合する。",
             "ファイル名の dropout/false、保存時 detected、JNI の selected=null は正解ラベルとして使用しない。",
             f"JNI expectation の端点/source 回帰照合: {report['jni_expectations']['color_frames_checked']} 色別件、mismatch {report['jni_expectations']['mismatches']}、欠けた画像 {report['jni_expectations']['missing_images']}。",
             f"欠けたラベル証拠 {sum(report['missing_evidence'].values())} 件。unknown は率の分母から除外する。",
             f"コーパス SHA-256 `{report['inventory_sha256']}`。ラベル SHA-256 `{report['truth_sha256']}`。", "",
             "## 色別の baseline（重複除外）", "",
             "| 色 | 剣なし FP / n（率） | 剣あり出力 / n（検出率） | unknown 出力 / n |",
             "|---|---:|---:|---:|"]
    for color in COLORS:
        s = report["summary"][color]
        a, b, u = s["no_saber"], s["saber"], s["unknown"]
        rate = lambda r: "—" if r["rate"] is None else f"{r['rate'] * 100:.2f}%"
        lines.append(f"| {color} | {a['detected']} / {a['frames']}（{rate(a)}） | {b['detected']} / {b['frames']}（{rate(b)}） | {u['detected']} / {u['frames']} |")
    lines += ["", "剣あり検出率は出力の有無のみ。実剣の位置を当てた率ではなく、背景を winner にした出力も含み得る。",
              "異常を選抜した撮影窓・同じ場面の隣接画像・formal fixture の混合集団であり、実運用の FP 率・独立 swing 数へ外挿しない。", "",
              "## winner source と出力端点長（px、重複除外）", "",
              "| 色 / ラベル | winner source: 件数 | 長さ n | min | mean | p50 | p95 | max |",
              "|---|---|---:|---:|---:|---:|---:|---:|"]
    for color in COLORS:
        for truth in ("no_saber", "saber", "unknown"):
            cell = report["summary"][color][truth]
            sources = ", ".join(f"`{k}`: {v}" for k, v in cell["winner_sources"].items()) or "—"
            s = cell["length_px"]
            lines.append(f"| {color} / {truth} | {sources} | {s['n']} | " + " | ".join(fmt(s[k]) for k in ("min", "mean", "p50", "p95", "max")) + " |")
    s = report["runtime_ms"]
    lines += ["", "## 実行時間", "",
              f"全体 {report['seconds']:.2f} 秒（CLI build 含む）。フレーム {s['n']} 件、平均 {fmt(s['mean'])} ms、p50 {fmt(s['p50'])} ms、p95 {fmt(s['p95'])} ms、最大 {fmt(s['max'])} ms。",
              "フレーム時間は subprocess 起動、PNG decode、recognition、JSON 出力、終了待ちを含む wall time。Python の JSON parse は除外。",
              "純粋な認識 kernel 時間でも iPhone の実機時間でもない。逐次実行であり、他 worker の負荷・disk cache・起動コストの影響がある。",
              "quantile は最近傍順位。長さは CLI selected の元画像座標から計算する。", "",
              "## 再実行と比較", "", "Git worktree root から実行（Python 標準ライブラリのみ、make/clang++/zlib が必要）。", "", "```bash",
              "python3 -B PhoneSaber/tools/recognition_benchmark.py --json /tmp/recognition-before.json",
              "python3 -B PhoneSaber/tools/recognition_benchmark.py --json /tmp/recognition-after.json --compare /tmp/recognition-before.json",
              "python3 -B -m unittest discover -s PhoneSaber/tools -p test_recognition_benchmark.py -v", "```", "",
              "既定の Downloads forensic / diagnostics-inbox / PhoneSaber 内 fixture を自動探索。`--downloads`、`--inbox` で場所を指定できる。",
              "CLI build は worktree と source fingerprint ごとの一時ディレクトリに cache し、再実行で再利用する。ソース変更時は新規 build。高負荷時の compile 待ちは最大600秒。",
              "report はこの文書へ出力（`--report` で変更可能）。詳細 JSON はローカル保存のみ。画像 ID は PNG の SHA-256、コピー ID は相対パスの hash。",
              "比較ではコーパス・truth 変更と認識変更を区別し、候補全体・score・eligibility・winner・公開診断の JSON と double bit signature を比較する。",
              "同じ画像/ラベルでの前後比較を行う。欠けた分母、unknown、認識失敗を成功として埋めない。"]
    if "comparison" in report:
        lines += ["", "今回の比較: `" + json.dumps(report["comparison"], sort_keys=True) + "`"]
    return "\n".join(lines) + "\n"


def run_benchmark(args):
    start = time.perf_counter()
    images, excluded = discover(args.downloads, args.inbox)
    if not images:
        raise ValueError("no ORIGINAL PNGs discovered")
    truths, evidence, missing = truth_index(args.labels, args.manifest, args.downloads, args.inbox)
    # Source-addressed cache, isolated by worktree. A changed core cannot silently
    # reuse an earlier CLI, and repeated comparisons avoid compiler startup cost.
    digest = core_digest(PHONE / "android/core")
    build = Path(tempfile.gettempdir()) / ("phonesaber-recognition-" +
            sha256(str(REPO).encode())[:16] + "-" + digest[:16])
    cli = build_cli(PHONE / "android/core", build)
    rows = measure(cli, images, truths)
    unique = unique_rows(rows)
    commit = subprocess.check_output(["git", "-C", str(REPO), "rev-parse", "HEAD"], text=True).strip()
    return {"schema_version": 1, "commit": commit, "core_sha256": core_digest(PHONE / "android/core"),
            "inventory_sha256": signature(sorted((r["id"], r["occurrence_id"]) for r in rows)),
            "truth_sha256": signature([(r["id"], r["truth"]) for r in unique]),
            "origins": dict(sorted(Counter(r["origin"] for r in rows).items())),
            "excluded": excluded, "truth_evidence": evidence, "missing_evidence": missing,
            "jni_expectations": check_jni_expectations(rows, PHONE / "android/app/src/androidTest/assets/native_fixture_expectations.json"),
            "unique_images": len(unique), "summary": summarize(unique), "file_summary": summarize(rows),
            "runtime_ms": stats([r["runtime_ms"] for r in rows]),
            "seconds": time.perf_counter() - start, "rows": rows}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--downloads", type=Path, default=Path.home() / "Downloads")
    parser.add_argument("--inbox", type=Path, default=Path.home() / "Library/Application Support/PhoneSaber/diagnostics-inbox")
    parser.add_argument("--labels", type=Path, default=LABELS)
    parser.add_argument("--manifest", type=Path, default=MANIFEST)
    parser.add_argument("--report", type=Path, default=PHONE / "docs/claude/recognition-benchmark.md")
    parser.add_argument("--json", type=Path, help="local anonymous detailed output; do not commit")
    parser.add_argument("--compare", type=Path, help="previous local JSON")
    args = parser.parse_args()
    report = run_benchmark(args)
    if args.compare:
        report["comparison"] = compare(json.loads(args.compare.read_text()), report)
    args.report.write_text(markdown(report))
    if args.json:
        args.json.write_text(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False) + "\n")
    print(json.dumps({k: report[k] for k in ("unique_images", "origins", "seconds", "summary")}, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        # Avoid a traceback containing paths to private captures.
        detail = str(error) if isinstance(error, (ValueError, RuntimeError)) else type(error).__name__
        print("benchmark failed: " + detail, file=sys.stderr)
        raise SystemExit(2)
