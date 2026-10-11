#!/usr/bin/env python3
"""Offline guards; private per-image output and temporary builds stay in TMPDIR."""
from __future__ import annotations

import argparse
from collections import Counter
import json
import math
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile

import recognition_benchmark as benchmark

SESSIONS = ("163949_658", "164236_960")
# 今回 ORIGINAL を目視した8枚。最初の3枚と屋内11枚には点灯剣が見えない。
DAYLIGHT_SABER_FRAMES = {21948, 21949, 21950, 21952, 21953, 21954, 21955, 21956}
CORE = benchmark.PHONE / "android/core"
BASELINE_INVENTORY = "72cd6839781474ce772891a118943b3529967b752dae72af4f9f50010c094976"


def build_probe(destination):
    """Instrument a temporary copy, retaining all production arithmetic."""
    destination.mkdir(parents=True, exist_ok=True)
    source = (CORE / "src/pipeline.cpp").read_text()
    anchor = "            auto scale = [step](PixelPoint p) -> PixelPoint"
    if source.count(anchor) != 1:
        raise ValueError("observer insertion point changed")
    source = '#include "coreline_shadow_support.hpp"\n' + source.replace(
        anchor, "            shadow_support(scored,evidence,w,step);\n" + anchor)
    pipeline = destination / "pipeline.cpp"
    pipeline.write_text(source)
    cli = destination / "shadow-png"
    subprocess.run(["clang++", "-std=c++17", "-O2", "-Wall", "-Wextra", "-Werror",
                    "-ffp-contract=off", "-DPHONESABER_DISABLE_CORELINE_GUARD", "-I" + str(CORE / "include"),
                    "-I" + str(CORE / "src"), "-I" + str(Path(__file__).parent),
                    str(CORE / "src/detection.cpp"), str(pipeline),
                    str(CORE / "src/frame_processor.cpp"), str(CORE / "tools/png_cli.cpp"),
                    str(CORE / "tools/png.cpp"), "-lz", "-o", str(cli)],
                   check=True, capture_output=True, timeout=600)
    return cli


def observe(cli, image, baseline):
    run = subprocess.run([str(cli), str(image.path)], capture_output=True, check=True, timeout=30)
    document = json.loads(run.stdout)
    if benchmark.signature(document["colors"]) != baseline["recognition_sha256"]:
        raise ValueError("observer changed production output " + image.digest[:16])
    support = {c: [] for c in benchmark.COLORS}
    for line in run.stderr.splitlines():
        item = json.loads(line)
        support[item.pop("color")].append(item)
    for color in benchmark.COLORS:
        if len(support[color]) != len(document["colors"][color]["candidates"]):
            raise ValueError("observer candidate count mismatch")
        for candidate, metrics in zip(document["colors"][color]["candidates"], support[color]):
            candidate["support"] = metrics
    return {**baseline, "colors": document["colors"]}


def accepts(candidate, color, rule, area_pixels):
    """Reject only current eligible candidates, without altering their scores."""
    if not candidate["eligible"]:
        return False
    if candidate["source"] == "core-line" and (not rule.get("raw_only") or candidate["used_point_led_fallback"]):
        s = candidate["support"]
        for field in ("span_per_core", "lit_gap_px", "core_gap_px"):
            if field in rule and s[field] > rule[field]:
                return False
        for field in ("lit_coverage", "core_coverage"):
            if field in rule and s[field] < rule[field]:
                return False
        if "reported_gap_px" in rule and candidate["largest_longitudinal_gap"]*2 > rule["reported_gap_px"]:
            return False
    if color == "blue":
        if candidate["color_purity"] < rule.get("blue_purity", 0):
            return False
        if candidate["high_value_ratio"] < rule.get("blue_hv", 0):
            return False
        if candidate["source"] == "color-mask":
            if candidate["point_count"]*4/area_pixels > rule.get("mask_area", 1):
                return False
            if candidate["high_value_ratio"] < rule.get("mask_hv", 0):
                return False
    return True


def eligible_candidates(data, color, rule, area_pixels):
    kept = [c for c in data["candidates"] if accepts(c, color, rule, area_pixels)]
    def bridged(c):
        if not ("guarded_lit" in rule and c["source"] == "core-line"
                and c["used_point_led_fallback"] and c["support"]["lit_coverage"] < rule["guarded_lit"]):
            return False
        return any(b["source"] != "core-line" and b["core_support"] >= .4
                   and b["high_value_ratio"] >= .35 and b["support"]["lit_coverage"] >= .9
                   and min(math.hypot(a["x"]-z["x"], a["y"]-z["y"])
                           for a in c["endpoints"] for z in b["endpoints"]) <= 30
                   for b in kept)
    return [c for c in kept if not bridged(c)]


def select(data, color, rule, area_pixels):
    return next(iter(eligible_candidates(data, color, rule, area_pixels)), None)


def output_key(candidate):
    return None if candidate is None else (candidate["source"], candidate["endpoints"])


def summarize(rows, rule):
    result = {}
    for color in benchmark.COLORS:
        counts = Counter()
        for row in rows:
            truth = row["truth"][color]
            old = benchmark.winner(row["colors"][color])
            new = select(row["colors"][color], color, rule, row["area_pixels"])
            counts[truth + "_n"] += 1
            counts[truth + "_before"] += old is not None
            counts[truth + "_after"] += new is not None
            counts[truth + "_changed"] += output_key(old) != output_key(new)
            counts[truth + "_lost"] += old is not None and new is None
        result[color] = dict(counts)
    return result


def rules():
    yield "baseline", {}
    for field, values in {
        "span_per_core": (1, 2, 3, 4, 6, 8),
        "lit_gap_px": (8, 16, 32, 64),
        "core_gap_px": (16, 32, 64),
        "reported_gap_px": (4,),
        "lit_coverage": (.25, .4, .5, .6, .7, .8),
        "core_coverage": (.2, .3, .4, .5),
        "mask_area": (.005, .01, .02, .03, .05),
        "mask_hv": (.35, .5, .65),
        "blue_hv": (.35, .5, .65, .75),
        "blue_purity": (.25,),
    }.items():
        for value in values:
            yield f"{field}={value}", {field: value}
    for value in (.4, .5, .6):
        yield f"lit={value}+mask", {"lit_coverage": value, "mask_hv": .35, "mask_area": .02}
    for value in (.35, .4, .5):
        yield f"raw_lit={value}", {"lit_coverage": value, "raw_only": True}
    for value in (.5, .6, .7):
        yield f"guarded_lit={value}", {"guarded_lit": value}
    yield "guarded+mask", {"guarded_lit": .6, "mask_hv": .35, "mask_area": .02}
    yield "guarded+mask+purity", {"guarded_lit": .6, "mask_hv": .35, "mask_area": .02, "blue_purity": .25}


def formal_check(rows, rule):
    # 正式 checker の source/端点許容差/必須棄却型をそのまま照合する。
    sys.path.insert(0, str(benchmark.PHONE / "ios/PhoneSaberSender/Tools"))
    import run_lossless_regression as formal
    _, fixtures = formal.load_fixtures(benchmark.MANIFEST)
    by_hash = {r["id"]: r for r in rows}
    failures, changes = [], []
    for fixture in fixtures:
        row = by_hash[fixture["sha256"]]
        colors = {}
        for color, data in row["colors"].items():
            kept = eligible_candidates(data, color, rule, row["area_pixels"])
            selected = next(iter(kept), None)
            kept_ids = {id(c) for c in kept}
            colors[color] = {"selected": selected["endpoints"] if selected else None,
                             "candidates": [dict(c, eligible=id(c) in kept_ids)
                                            for c in data["candidates"]]}
        color = fixture["color"].lower()
        if output_key(benchmark.winner(row["colors"][color])) != output_key(formal.selected_candidate(colors[color])):
            changes.append(fixture["name"])
        result = formal.evaluate_fixture(fixture, {"colors": colors, "profile": {"total_ms": 0}})
        if result["errors"]:
            failures.append({"name": fixture["name"], "errors": result["errors"]})
    return {"pass": len(fixtures)-len(failures), "total": len(fixtures),
            "changed": changes, "failures": failures}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    temp_root = Path(os.environ.get("TMPDIR", tempfile.gettempdir())).resolve()
    if not args.output.resolve().is_relative_to(temp_root):
        parser.error("private derived output must be under TMPDIR")
    args.output.mkdir(parents=True, exist_ok=True)
    downloads = Path.home() / "Downloads"
    inbox = Path.home() / "Library/Application Support/PhoneSaber/diagnostics-inbox"
    images, _ = benchmark.discover(downloads, inbox)
    old = [i for i in images if not (i.origin == "inbox" and "phonesaber_20261010_" in str(i.path))]
    sessions = [i for i in images if any("phonesaber_20261010_" + s in str(i.path) for s in SESSIONS)]
    inventory = benchmark.signature(sorted((i.digest, i.occurrence) for i in old))
    if inventory != BASELINE_INVENTORY or len(sessions) != 22:
        raise ValueError("capture inventory changed; rebuild the explicitly documented cohort")
    truths, _, missing = benchmark.truth_index(benchmark.LABELS, benchmark.MANIFEST, downloads, inbox)
    # guard 自体を比較する研究用 baseline は明示的に OFF。
    cli = benchmark.build_cli(CORE, args.output / "native", guard_enabled=False)
    probe = build_probe(args.output / "probe")
    rows = []
    for group, entries in (("benchmark", old), ("sessions", sessions)):
        baseline = benchmark.measure(cli, entries, truths)
        for image, row in zip(entries, baseline):
            observed = observe(probe, image, row)
            header = image.path.read_bytes()[:24]
            width, height = struct.unpack(">II", header[16:24])
            observed.update(group=group, area_pixels=width*height)
            if group == "sessions":
                observed.update(session=next(s for s in SESSIONS if s in str(image.path)),
                                frame=int(image.path.stem.split("_")[-1]))
                truth = "saber" if observed["session"] == SESSIONS[0] and observed["frame"] in DAYLIGHT_SABER_FRAMES else "no_saber"
                observed["truth"] = {c: truth for c in benchmark.COLORS}
            rows.append(observed)
        print(group, len(entries), "PNG parity PASS", flush=True)
    (args.output / "observed.json").write_text(json.dumps(rows))
    unique = benchmark.unique_rows(rows)
    old_rows = [r for r in unique if r["group"] == "benchmark"]
    document = {"core_sha256": benchmark.core_digest(CORE), "inventory_sha256": inventory,
                "missing_evidence": missing, "rows": rows,
                "rules": {name: {"guard": rule, "benchmark": summarize(old_rows, rule),
                                 "formal": formal_check(old_rows, rule),
                                 "sessions": summarize([r for r in unique if r["group"] == "sessions"], rule)}
                          for name, rule in rules()}}
    (args.output / "shadow.json").write_text(json.dumps(document, ensure_ascii=False))
    print("unique baseline", len(old_rows), "inventory", inventory, flush=True)


if __name__ == "__main__":
    main()
