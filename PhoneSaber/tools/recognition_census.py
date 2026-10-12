#!/usr/bin/env python3
"""読取専用の判定 census。画像・詳細 JSON・native build は TMPDIR のみ。"""
from __future__ import annotations

import argparse
from collections import Counter, defaultdict
import csv
import json
import math
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile

import recognition_benchmark as bench
import coreline_shadow as shadow

TOOLS = bench.PHONE / "ios/PhoneSaberSender/Tools"
sys.path.insert(0, str(TOOLS))
import run_lossless_regression as formal
import phone_saber_selection_replay as temporal


def midpoint(c):
    a, b = c["endpoints"]
    return ((a["x"] + b["x"]) / 2, (a["y"] + b["y"]) / 2)


def inside(c, box, pad=8):
    """物体 box 内の軸中点という位置 proxy。端点正解を意味しない。"""
    if c is None:
        return False
    x, y = midpoint(c)
    return box[0]-pad <= x <= box[2]+pad and box[1]-pad <= y <= box[3]+pad


def emitter_score(c):
    clamp = lambda x: max(0, min(1, x))
    # production double_bits[6] = clipped_white_ratio（既に0..1へ正規化）。
    clipped = struct.unpack("d", struct.pack("Q", int(c["production"]["double_bits"][6])))[0]
    return (clamp((c["peak_value"]-200)/55)*.32
            + clamp((c["mean_value"]-160)/95)*.23
            + c["high_value_ratio"]*.28 + c["color_purity"]*.12 + clipped*.05)


def rejection_rules(c):
    rules = []
    if c["peak_value"] < 218:
        rules.append("peak<218")
    if emitter_score(c) < .42:
        rules.append("emitterScore<.42")
    clipped = struct.unpack("d", struct.pack("Q", int(c["production"]["double_bits"][6])))[0]
    if not (c["high_value_ratio"] >= .08 or (c["peak_value"] >= 242 and c["mean_value"] >= 190) or clipped > 0):
        rules.append("hasEmitterCore=false")
    if c["production"]["compact_red"]:
        for field, floor in (("peak_value", 230), ("high_value_ratio", .5), ("color_purity", .5)):
            if c[field] < floor:
                rules.append("compact:" + field)
    if c["source"].startswith("core-line-low-confidence"):
        for field, floor in (("color_purity", .25), ("retained_body_ratio", .35),
                             ("longitudinal_continuity", .70), ("high_value_ratio", .35)):
            if c[field] < floor:
                rules.append("line:" + field)
    for name in ("warmNoDeepRed", "blueNoDeepSupport"):
        if c.get(name, {}).get("rejected") if c.get(name) else False:
            rules.append(name)
    for suffix in ("unsupported-core-line-candidate", "broad-coreless", "weak-raw-tail",
                   "weak-bridge", "subsegment", "overlap", "sparse", "short-body"):
        if suffix in c["source"]:
            rules.append(suffix)
    return rules or ["baseEligibility:hasCore/other"]


def build_observer(destination):
    """本番の一時コピーに観測だけを挿入。stdout の全bit parityを後で要求する。"""
    destination.mkdir(parents=True, exist_ok=True)
    core = shadow.CORE
    pipeline = (core / "src/pipeline.cpp").read_text()
    anchor = "            auto scale = [step](PixelPoint p) -> PixelPoint"
    if pipeline.count(anchor) != 1:
        raise ValueError("observer anchor changed")
    extra = r'''
            shadow_support(scored,evidence,w,step);
            auto bs = blue_support(scored.support_points,p,step);
            auto ws = warm_support(scored.support_points,p,step);
            std::cerr << "{\"extra\":true,\"color\":\"" << (color_index == 0 ? "red" : "blue")
                << "\",\"deep_blue\":" << bs.deep_count
                << ",\"warm_rejected\":" << (ws.rejected ? "true" : "false") << "}\n";
'''
    pipeline = '#include "coreline_shadow_support.hpp"\n' + pipeline.replace(anchor, extra+anchor)
    (destination / "pipeline.cpp").write_text(pipeline)
    detection = (core / "src/detection.cpp").read_text()
    anchor = "    int standard_minimum ="
    logger = r'''
    auto discard = [&](const char* rule) -> std::optional<Scored> {
        BoundingBox b{w,h,0,0};
        for (auto p : points) { b.min_x=std::min(b.min_x,p.x); b.min_y=std::min(b.min_y,p.y);
            b.max_x=std::max(b.max_x,p.x); b.max_y=std::max(b.max_y,p.y); }
        std::cerr << "{\"discard\":true,\"color\":\"" << (e.color == SaberColor::red ? "red" : "blue")
                << "\",\"source\":\"" << source << "\",\"rule\":\"" << rule << "\",\"point_count\":" << points.size()
            << ",\"box\":[" << b.min_x*2 << ',' << b.min_y*2 << ',' << b.max_x*2 << ',' << b.max_y*2 << "]}\n";
        return std::nullopt;
    };
'''
    detection = '#include <iostream>\n' + detection.replace(anchor, logger+anchor, 1)
    replacements = {
        "if (int(points.size()) < minimum) return std::nullopt;": 'if (int(points.size()) < minimum) return discard("minimumArea");',
        "if (area_ratio > 0.22) return std::nullopt;": 'if (area_ratio > 0.22) return discard("area>.22");',
        "|| aspect < (e.color == SaberColor::red ? 1.0 : 1.5) || extent < 0.10) return std::nullopt;":
            '|| aspect < (e.color == SaberColor::red ? 1.0 : 1.5) || extent < 0.10) return discard("span/aspect/extent");',
        "if (raw_count < 3) return std::nullopt;": 'if (raw_count < 3) return discard("rawCount<3");',
    }
    for old, new in replacements.items():
        if detection.count(old) != 1:
            raise ValueError("discard anchor changed")
        detection = detection.replace(old, new)
    (destination / "detection.cpp").write_text(detection)
    cli = destination / "census-png"
    subprocess.run(["clang++", "-std=c++17", "-O2", "-Wall", "-Wextra", "-Werror", "-ffp-contract=off",
                    "-I"+str(core/"include"), "-I"+str(core/"src"), "-I"+str(Path(__file__).parent),
                    str(destination/"detection.cpp"), str(destination/"pipeline.cpp"),
                    str(core/"src/frame_processor.cpp"), str(core/"tools/png_cli.cpp"),
                    str(core/"tools/png.cpp"), "-lz", "-o", str(cli)], check=True, capture_output=True)
    return cli


def observe(cli, image, baseline):
    run = subprocess.run([str(cli), str(image.path)], capture_output=True, check=True, timeout=30)
    colors = json.loads(run.stdout)["colors"]
    if bench.signature(colors) != baseline["recognition_sha256"]:
        raise ValueError("observer changed production: " + image.digest[:12])
    support, extras, discards = defaultdict(list), defaultdict(list), defaultdict(list)
    for line in run.stderr.splitlines():
        item = json.loads(line)
        color = item.pop("color")
        (discards if item.get("discard") else extras if item.get("extra") else support)[color].append(item)
    for color in bench.COLORS:
        cs = colors[color]["candidates"]
        if not len(cs) == len(support[color]) == len(extras[color]):
            raise ValueError("observer count mismatch")
        for index, (c, s, e) in enumerate(zip(cs, support[color], extras[color])):
            c.update(support=s, shadow_deep_blue=e["deep_blue"], shadow_warm_rejected=e["warm_rejected"],
                     shadow_candidate_index=index)
    return {**baseline, "colors": colors, "discards": dict(discards)}


def enrich(rows, images, inbox, annotations):
    """既存の物体box CSVは正式期待値とは別の真値。hashで画像へjoinする。"""
    by_hash = {r["id"]: r for r in rows}
    by_path = {i.path.resolve(): by_hash[i.digest] for i in images}
    by_session_frame = defaultdict(list)
    metadata = {}
    bundles = []
    for path in sorted(inbox.glob("*/summary.json")):
        bundles.append(path.parent)
        summary = json.loads(path.read_text())
        session = summary.get("sessionID", path.parent.name.removeprefix("phone_saber_triage_"))
        all_frames = {}
        for context_path in sorted((path.parent / "frames").glob("*.json")):
            context = json.loads(context_path.read_text())
            for frame in context.get("frames", []):
                previous = all_frames.setdefault(frame["frameID"], {})
                if frame.get("camera"):
                    previous["camera"] = frame["camera"]
        steps = (summary.get("guidedRecording") or {}).get("steps", [])
        for i in summary.get("images", []):
            row = by_path.get((path.parent/i["path"]).resolve())
            if row is None:
                continue
            frame = i["frameID"]
            row.update(session=session, frame=frame)
            row["camera"] = all_frames.get(frame, {}).get("camera", {})
            row["exposure_setting"] = summary.get("cameraExposureExperiment", {}).get("setting", "unknown")
            row["guided_step"] = next((s["id"] for s in steps if s.get("holdStartFrameID", math.inf) <= frame
                                       <= s.get("holdEndFrameID", -math.inf)), None)
            by_session_frame[session.removeprefix("phonesaber_"), frame].append(row)
            context_path = path.parent/i.get("frameContextPath", "missing")
            if context_path.is_file():
                context = json.loads(context_path.read_text())
                f = next((f for f in context.get("frames", []) if f.get("frameID") == frame), None)
                if f:
                    metadata[session, frame] = f
                    row["segment"] = context.get("segmentLabel", "unknown")
                    row["tracking"] = bool(context.get("motionEvent"))
    missing, conflicts = [], []
    csv_path = bench.PHONE/"docs/claude/data/2026-10-03_background_fp_labels.csv"
    for label in csv.DictReader(csv_path.open()):
        key = (label["session"], int(label["frame"]))
        targets = by_session_frame[key]
        if not targets:
            missing.append([*key, label["color"]])
        for row in targets:
            color = label["color"]
            truth = {"absent": "no_saber", "saber": "saber"}.get(label["truth"], "unknown")
            if truth != "unknown":
                if row["truth"][color] not in (truth, "unknown"):
                    conflicts.append([row["id"], color, row["truth"][color], truth])
                row["truth"][color] = truth
            row.setdefault("boxes", {})[color] = [int(v) for v in label["saber_box"].split()] if label["saber_box"] else None
            row.setdefault("scene", {})[color] = label["note"] or ("旧室内:" + label["session"][:8])
    for item in annotations:
        for row in by_session_frame[item["session"], item["frame"]]:
            if row["id"] != item["sha256"]:
                raise ValueError("manual annotation hash mismatch")
            for color, truth in item["truth"].items():
                row["truth"][color] = truth
                row.setdefault("scene", {})[color] = item["scene"]
            if item.get("boxes"):
                row.setdefault("boxes", {}).update(item["boxes"])
            if item.get("endpoints"):
                row.setdefault("manual_endpoints", {}).update(item["endpoints"])
            if item.get("blur_px"):
                row["manual_blur_px"] = item["blur_px"]
            row["visual_motion"] = item.get("motion", "unspecified")
    labels = json.loads(bench.LABELS.read_text())["labels"]
    for label in labels:
        for i in label["images"]:
            if i["sha256"] in by_hash:
                by_hash[i["sha256"]].setdefault("motion", {})[label["color"]] = label["motion"]
    return {"bundles": bundles, "missing_csv": missing, "conflicts": conflicts}


def eligible(row, color, rule):
    data = row["colors"][color]
    kept = shadow.eligible_candidates(data, color, rule, row["area_pixels"])
    if color == "red":
        kept = [c for c in kept if ("red_sources" in rule and c["source"] not in rule["red_sources"])
                or (c["high_value_ratio"] >= rule.get("red_hv", 0)
                    and c["mean_value"] >= rule.get("red_mean", 0)
                    and c["peak_value"] >= rule.get("red_peak", 0))]
        if "compact_purity" in rule:
            # base の compact purity だけを緩める。後段棄却sourceは復活させない。
            for c in data["candidates"]:
                if c["eligible"] or not c["production"]["compact_red"] or c["source"] not in (
                        "color-mask", "color-close", "color-emitter", "core-halo", "color-sparse-raw"):
                    continue
                other = [r for r in rejection_rules(c) if r != "compact:color_purity"]
                if other == ["baseEligibility:hasCore/other"]:
                    other = []
                if other or c["color_purity"] < rule["compact_purity"]:
                    continue
                # 元々base棄却の候補にも、最後のwarm gateを本番と同じ元画素で要求する。
                if not c.get("shadow_warm_rejected", True):
                    kept.append(dict(c, eligible=True))
    if color == "blue" and rule.get("local_cancel"):
        # 打ち消すlineと幾何が重ならない候補だけを復活。最終deep-blue gateも再評価する。
        lines = [c for c in data["candidates"] if c["source"] == "core-line-low-confidence"]
        for c in data["candidates"]:
            if not c["source"].endswith("-unsupported-core-line-candidate") or c["shadow_deep_blue"] == 0:
                continue
            box = c["bounding_box"]
            def overlaps(line):
                b = line["bounding_box"]
                return not (b["max_x"]+8 < box["min_x"] or box["max_x"]+8 < b["min_x"]
                            or b["max_y"]+8 < box["min_y"] or box["max_y"]+8 < b["min_y"])
            if not any(overlaps(line) for line in lines):
                kept.append(dict(c, eligible=True, source=c["source"].removesuffix("-unsupported-core-line-candidate")))
    return sorted(kept, key=lambda c: (-c["score"], c["shadow_candidate_index"]))


def formal_check(rows, rule):
    _, fixtures = formal.load_fixtures(bench.MANIFEST)
    by_hash = {r["id"]: r for r in rows}
    failures = []
    for fixture in fixtures:
        row = by_hash[fixture["sha256"]]
        colors = {}
        for color in bench.COLORS:
            kept = eligible(row, color, rule)
            output = kept[0] if kept else None
            survivors = {c["shadow_candidate_index"]: c for c in kept}
            candidates = [dict(survivors.get(c["shadow_candidate_index"], c),
                               eligible=c["shadow_candidate_index"] in survivors)
                          for c in row["colors"][color]["candidates"]]
            colors[color] = {"selected": output["endpoints"] if output else None, "candidates": candidates}
        result = formal.evaluate_fixture(fixture, {"colors": colors, "profile": {"total_ms": 0}})
        if result["errors"]:
            failures.append({"name": fixture["name"], "errors": result["errors"]})
    return {"pass": len(fixtures)-len(failures), "total": len(fixtures), "failures": failures}


def summarize(rows, rule):
    results = {}
    for color in bench.COLORS:
        counts = Counter()
        for row in rows:
            old = bench.winner(row["colors"][color])
            kept = eligible(row, color, rule)
            new = kept[0] if kept else None
            truth = row["truth"][color]
            counts[truth+"_n"] += 1
            counts[truth+"_before"] += old is not None
            counts[truth+"_after"] += new is not None
            counts[truth+"_lost"] += old is not None and new is None
            counts[truth+"_added"] += old is None and new is not None
            counts[truth+"_changed"] += shadow.output_key(old) != shadow.output_key(new)
            box = row.get("boxes", {}).get(color)
            if truth == "saber" and box:
                counts["box_n"] += 1
                a, b = inside(old, box), inside(new, box)
                counts["box_before"] += a
                counts["box_after"] += b
                counts["box_lost"] += a and not b
                counts["box_recovered"] += b and not a
        results[color] = dict(counts)
    return results


def census(rows):
    result = {}
    for color in bench.COLORS:
        failures, rejects, scenes, sources, examples = Counter(), Counter(), defaultdict(Counter), Counter(), []
        for row in rows:
            data = row["colors"][color]
            c = bench.winner(data)
            truth = row["truth"][color]
            if truth == "no_saber":
                scene = row.get("scene", {}).get(color, "formal/領域未ラベル")
                scenes[scene]["n"] += 1
                scenes[scene]["FP"] += c is not None
                if c:
                    sources[c["source"]] += 1
                    scenes[scene][c["source"]] += 1
            box = row.get("boxes", {}).get(color)
            if truth != "saber" or not box:
                continue
            matching = [c for c in data["candidates"] if inside(c, box)]
            if inside(c, box):
                category = "winner_in_box"
            elif any(c["eligible"] for c in matching):
                category = "wrong_winner"
            elif matching:
                category = "all_matching_ineligible"
                for rejected in matching:
                    rejects.update(rejection_rules(rejected))
            else:
                category = "no_matching_generated"
            failures[category] += 1
            if category != "winner_in_box":
                examples.append({"id": row["id"], "session": row.get("session"), "frame": row.get("frame"),
                                 "box": box, "category": category, "winner": c,
                                 "matching": matching, "discards": row.get("discards", {}).get(color, [])})
        result[color] = {"box_census": dict(failures), "rejections_candidate_counts": dict(rejects),
                         "FP_sources": dict(sources), "FP_scenes": {k: dict(v) for k, v in scenes.items()},
                         "failures": examples}
    return result


def endpoint_metrics(actual, expected):
    """端点順序を無視。短さ/長さは画像内の手ラベル発光部分に対する比。"""
    length = bench.length(expected)
    if length == 0:
        raise ValueError("zero length endpoint label")
    def angle(ep):
        a, b = ep
        return math.degrees(math.atan2(b["y"]-a["y"], b["x"]-a["x"]))
    delta = abs(angle(actual)-angle(expected)) % 180
    return {"length_ratio": bench.length(actual)/length, "angle_deg": min(delta, 180-delta),
            "endpoint_error_px": formal.endpoint_distance(actual, expected)}


def endpoints_summary(rows, rule):
    result = {}
    for color in bench.COLORS:
        values = []
        for row in rows:
            expected = row.get("manual_endpoints", {}).get(color)
            if not expected:
                continue
            kept = eligible(row, color, rule)
            value = endpoint_metrics(kept[0]["endpoints"], expected) if kept else None
            values.append({"session": row["session"], "frame": row["frame"], "value": value})
        measured = [v["value"] for v in values if v["value"]]
        result[color] = {"labelled": len(values), "detected": len(measured),
                         "short_lt80pct": sum(v["length_ratio"] < .8 for v in measured),
                         "long_gt120pct": sum(v["length_ratio"] > 1.2 for v in measured),
                         "angle_gt20deg": sum(v["angle_deg"] > 20 for v in measured),
                         "statistics": {k: bench.stats([v[k] for v in measured]) for k in (
                             "length_ratio", "angle_deg", "endpoint_error_px")}, "frames": values}
    return result


def exposure_summary(rows):
    groups = defaultdict(list)
    for row in rows:
        for color in bench.COLORS:
            if row["truth"][color] != "saber" or not row.get("camera", {}).get("exposureDurationSeconds"):
                continue
            duration = row["camera"]["exposureDurationSeconds"]
            bucket = "le1_120" if duration <= 1.02/120 else "le1_100" if duration <= .0102 else "gt1_100"
            motion = row.get("visual_motion") or row.get("motion", {}).get(color, "unclassified")
            if isinstance(motion, dict):
                motion = motion.get(color, "unclassified")
            winner = bench.winner(row["colors"][color])
            box = row.get("boxes", {}).get(color)
            groups[color, motion, bucket].append({"session": row["session"], "frame": row["frame"],
                "detected": bool(winner), "box_known": bool(box), "box_hit": inside(winner, box) if box else False,
                "exposure_ms": duration*1000, "iso": row["camera"].get("iso"),
                "blur_proxy_px": row.get("manual_blur_px", {}).get(color)})
    return [{"color": c, "motion": m, "bucket": b, "n": len(v),
             "sessions": len({r["session"] for r in v}), "detected": sum(r["detected"] for r in v),
             "box_n": sum(r["box_known"] for r in v), "box_hit": sum(r["box_hit"] for r in v),
             "iso": bench.stats([r["iso"] for r in v if r["iso"] is not None]),
             "frames": v} for (c, m, b), v in sorted(groups.items())]


def load_tracking_sequences(bundles):
    """完全なeligible一覧があるmotion contextだけ。重複frameを一度数える。"""
    tables = defaultdict(dict)
    missing = Counter()
    for bundle in bundles:
        for path in sorted((bundle / "frames").glob("*.json")):
            context = json.loads(path.read_text())
            if not context.get("motionEvent"):
                continue
            for frame in context.get("frames", []):
                for color in bench.COLORS:
                    geometry = (frame.get(color) or {}).get("candidateGeometry", {})
                    cs = temporal._complete_eligible(geometry)
                    if cs is None:
                        missing[color] += 1
                        continue
                    key = (context.get("sessionID", bundle.name), color)
                    previous = tables[key].get(frame["frameID"])
                    if previous is not None and previous != cs:
                        raise ValueError("conflicting complete candidate geometry")
                    tables[key][frame["frameID"]] = cs
    return {key: sorted(frames.items()) for key, frames in tables.items()}, dict(missing)


def strict_match(a, b):
    """仮の強い対応: 現候補の中心30px以内、長さ比1/2..2、角差20度以内。"""
    ea, eb = a.get("finalOutputEndpoints"), b.get("finalOutputEndpoints")
    if not ea or not eb:
        return False
    pa = [{"x": ea[0], "y": ea[1]}, {"x": ea[2], "y": ea[3]}]
    pb = [{"x": eb[0], "y": eb[1]}, {"x": eb[2], "y": eb[3]}]
    if bench.length(pb) == 0:
        return False
    metrics = endpoint_metrics(pa, pb)
    return (temporal._jump(a, b) or 0) <= 30 and .5 <= metrics["length_ratio"] <= 2 and metrics["angle_deg"] <= 20


def endpoint_jump(a, b):
    ea, eb = a["finalOutputEndpoints"], b["finalOutputEndpoints"]
    d = lambda x, y: math.hypot(ea[x]-eb[y], ea[x+1]-eb[y+1])
    return min(max(d(0, 0), d(2, 2)), max(d(0, 2), d(2, 0)))


def tracking_replay(sequences, margin, hold, strict=False):
    """過去端点の保持なし。現eligibleから再選択し、連続overrideの期限を延長しない。"""
    policy = temporal.Policy(margin, .5, .2, hold)
    matches = strict_match if strict else lambda a, b: temporal.corresponds(a, b, policy)
    chosen, changed, events = {}, [], []
    counts = {color: Counter() for color in bench.COLORS}
    for (session, color), frames in sequences.items():
        for run in temporal._runs(frames):
            old_prev = new_prev = None
            overrides = 0
            counts[color]["runs"] += 1
            for fid, cs in run:
                old = cs[0] if cs else None
                new = old
                counts[color]["frames"] += 1
                if old and old_prev:
                    counts[color]["pairs"] += 1
                    counts[color]["midpoint_jumps_before"] += (temporal._jump(old, old_prev) or 0) >= 100
                    counts[color]["endpoint_jumps_before"] += endpoint_jump(old, old_prev) >= 100
                    stable = [c for c in cs[1:] if matches(c, old_prev)]
                    if endpoint_jump(old, old_prev) >= 100 and not matches(old, old_prev) and stable:
                        alt = max(stable, key=lambda c: c.get("finalScore", 0))
                        events.append({"session": session, "color": color, "frame": fid,
                                       "gap": old["finalScore"]-alt["finalScore"],
                                       "jump": endpoint_jump(old, old_prev),
                                       "alternative_jump": endpoint_jump(alt, old_prev)})
                if old and new_prev and len(cs) > 1 and not matches(old, new_prev) and overrides < hold:
                    stable = [c for c in cs[1:] if matches(c, new_prev)
                              and old["finalScore"]-c["finalScore"] <= margin]
                    if stable:
                        new = max(stable, key=lambda c: c["finalScore"])
                if new is old:
                    overrides = 0
                else:
                    overrides += 1
                    counts[color]["changes"] += 1
                    changed.append({"session": session, "color": color, "frame": fid})
                if old and old_prev and new and new_prev:
                    counts[color]["midpoint_jumps_after"] += (temporal._jump(new, new_prev) or 0) >= 100
                    counts[color]["endpoint_jumps_after"] += endpoint_jump(new, new_prev) >= 100
                chosen[session, color, fid] = new
                old_prev, new_prev = old, new
    return {"margin": margin, "hold": hold, "strict": strict, "counts": {c: dict(v) for c, v in counts.items()},
            "events": events, "changed": changed}, chosen


def temporal_truth(rows, chosen):
    counts = {color: Counter() for color in bench.COLORS}
    for row in rows:
        for color in bench.COLORS:
            key = row.get("session"), color, row.get("frame")
            if key not in chosen:
                continue
            new = chosen[key]
            old = bench.winner(row["colors"][color])
            truth = row["truth"][color]
            counts[color][truth+"_n"] += 1
            counts[color][truth+"_before"] += old is not None
            counts[color][truth+"_after"] += new is not None
            flat = None if old is None else [v for p in old["endpoints"] for v in (p["x"], p["y"])]
            changed = flat != (new["finalOutputEndpoints"] if new else None)
            counts[color][truth+"_changed"] += changed
            box = row.get("boxes", {}).get(color)
            if truth == "saber" and box:
                hit = new and box[0]-8 <= (new["finalOutputEndpoints"][0]+new["finalOutputEndpoints"][2])/2 <= box[2]+8 \
                    and box[1]-8 <= (new["finalOutputEndpoints"][1]+new["finalOutputEndpoints"][3])/2 <= box[3]+8
                counts[color]["box_n"] += 1
                counts[color]["box_before"] += inside(old, box)
                counts[color]["box_after"] += bool(hit)
    return {color: dict(count) for color, count in counts.items()}


def geometry(c):
    box = c["bounding_box"]
    raw = c["production"]["comparison_endpoints"]
    return {"bbox": [box[k] for k in ("min_x", "min_y", "max_x", "max_y")],
            "centroid": list(midpoint(c)),
            "rawPCASpan": c["raw_pca_span"], "finalScore": c["score"],
            "finalOutputEndpoints": [v for p in c["endpoints"] for v in (p["x"],p["y"])],
            "rawPCAEndpoints": [v for p in raw for v in (p["x"],p["y"])]}


def production_sequences(rows, rule):
    sequences = defaultdict(dict)
    for row in rows:
        if "session" not in row or not row.get("tracking"):
            continue
        for color in bench.COLORS:
            cs = []
            for c in eligible(row, color, rule):
                g = geometry(c)
                raw = g["rawPCAEndpoints"]
                g["centroid"] = [(raw[0]+raw[2])/2, (raw[1]+raw[3])/2]
                cs.append(g)
            sequences[row["session"], color][row["frame"]] = cs
    return {key: sorted(frames.items()) for key, frames in sequences.items()}


def temporal_analysis(sequences):
    result = []
    for margin in (1, 3, 5, 10):
        for hold in (1, 3):
            policy = temporal.Policy(margin, .5, .2, hold)
            row = temporal.replay(sequences, policy, 100)
            row["events"] = temporal.gap_distribution(sequences, policy)
            result.append(row)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--annotations", type=Path, help="TMPDIR内の追加目視ラベルJSON")
    args = parser.parse_args()
    tmp = Path(os.environ.get("TMPDIR", tempfile.gettempdir())).resolve()
    if not args.output.resolve().is_relative_to(tmp) or (args.annotations and not args.annotations.resolve().is_relative_to(tmp)):
        parser.error("derived files must be under TMPDIR")
    args.output.mkdir(parents=True, exist_ok=True)
    downloads, inbox = Path.home()/"Downloads", Path.home()/"Library/Application Support/PhoneSaber/diagnostics-inbox"
    images, excluded = bench.discover(downloads, inbox)
    truths, evidence, missing = bench.truth_index(bench.LABELS, bench.MANIFEST, downloads, inbox)
    cli = bench.build_cli(shadow.CORE, args.output/"native")
    baseline = bench.measure(cli, images, truths)
    (args.output/"baseline.json").write_text(json.dumps(baseline))
    probe = build_observer(args.output/"observer")
    rows = [observe(probe, i, b) for i,b in zip(images, baseline)]
    rows = bench.unique_rows(rows)
    for row in rows:
        im = next(i for i in images if i.digest == row["id"])
        w,h = struct.unpack(">II", im.path.read_bytes()[16:24])
        row.update(width=w, height=h, area_pixels=w*h)
    annotations = json.loads(args.annotations.read_text()) if args.annotations else []
    enrichment = enrich(rows, images, inbox, annotations)
    rules = {"baseline": {}, "red_hv45": {"red_hv": .45}, "red_hv48": {"red_hv": .48},
             "red_hv50": {"red_hv": .50}, "red_hv60": {"red_hv": .60},
             "red_hv65": {"red_hv": .65}, "red_hv70": {"red_hv": .70},
             "red_brightness_combo": {"red_hv": .6, "red_mean": 215, "red_peak": 250},
             "red_compact35": {"compact_purity": .35}, "red_compact40": {"compact_purity": .40},
             "red_mask_close45": {"red_hv": .45, "red_sources": ["color-mask", "color-close", "color-sparse-raw"]},
             "red_close65": {"red_hv": .65, "red_sources": ["color-close"]},
             "blue_mask": {"mask_area": .02, "mask_hv": .35}, "blue_purity": {"blue_purity": .25},
             "blue_mask_purity": {"mask_area": .02, "mask_hv": .35, "blue_purity": .25},
             "blue_local_cancel": {"local_cancel": True},
             "core_guard_reference": {"guarded_lit": .6},
             "combined": {"guarded_lit": .6, "mask_area": .02, "mask_hv": .35, "blue_purity": .25, "red_hv": .65}}
    rules["next_combined"] = {"guarded_lit": .6, "mask_area": .02, "mask_hv": .35, "blue_purity": .25,
                              "red_hv": .45, "red_sources": ["color-mask", "color-close", "color-sparse-raw"]}
    current_sequences = production_sequences(rows, {})
    recorded_sequences, incomplete = load_tracking_sequences(enrichment["bundles"])
    tracking = {}
    for origin, sequences in (("current_original", current_sequences), ("recorded_context", recorded_sequences)):
        tracking[origin] = []
        for strict in (False, True):
            for margin in (1, 3, 5, 10):
                for hold in (1, 3):
                    evaluation, chosen = tracking_replay(sequences, margin, hold, strict)
                    if origin == "current_original":
                        evaluation["truth"] = temporal_truth(rows, chosen)
                    tracking[origin].append(evaluation)
    result = {"core_sha256": bench.core_digest(shadow.CORE), "files": len(images), "unique_images": len(rows),
              "origins": dict(Counter(i.origin for i in images)), "excluded": excluded,
              "inventory_sha256": bench.signature(sorted((i.digest,i.occurrence) for i in images)),
              "missing": missing, "truth_evidence": evidence,
              "missing_csv": enrichment["missing_csv"], "conflicts": enrichment["conflicts"],
              "bundles": len(enrichment["bundles"]), "observer_parity": len(images),
              "census": census(rows),
              "rules": {name: {"rule": rule, "summary": summarize(rows, rule), "formal": formal_check(rows, rule),
                               "endpoints": endpoints_summary(rows, rule)}
                        for name,rule in rules.items()},
              "exposure": exposure_summary(rows), "tracking": tracking,
              "incomplete_context_occurrences": incomplete}
    (args.output/"rows.json").write_text(json.dumps(rows, ensure_ascii=False))
    (args.output/"census.json").write_text(json.dumps(result, ensure_ascii=False))
    print(json.dumps({k:v for k,v in result.items() if k not in ("census", "tracking", "exposure", "rules")}, ensure_ascii=False))
    for name, r in result["rules"].items():
        print(name, r["summary"], "formal", r["formal"]["pass"])


if __name__ == "__main__":
    main()
