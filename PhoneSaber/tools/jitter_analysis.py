#!/usr/bin/env python3
"""Read-only endpoint jitter census. Private JSON output belongs outside Git.

All cutoffs are reporting bins, never recognition thresholds. Image labels must
come from ORIGINAL PNG review, not the measured endpoints. Unlabelled frames
stay unknown. Counts refer to colour frames/transitions, not independent swings.
"""
import argparse
from collections import Counter, defaultdict
import json
import hashlib
import math
from pathlib import Path
import sys
import time
from types import SimpleNamespace

import real_motion_extract as extract

TRIAGE_TOOLS = Path(__file__).resolve().parents[1] / "ios/PhoneSaberSender/Tools"
sys.path.insert(0, str(TRIAGE_TOOLS))
import phone_saber_selection_replay as selection
import phone_saber_tracking_diagnostics as tracking


def distribution(values):
    ordered = sorted(values)
    if not ordered:
        return {"n": 0}
    def q(fraction):
        return ordered[int(fraction * (len(ordered) - 1) + .5)]
    return dict(n=len(ordered), p50=q(.5), p90=q(.9), p95=q(.95), p99=q(.99),
                maximum=ordered[-1], **{f"ge_{x}px": sum(v >= x for v in ordered) for x in (10, 25, 50, 100)})


def displacement(a, b):
    direct = [math.dist(a[:2], b[:2]), math.dist(a[2:], b[2:])]
    crossed = [math.dist(a[:2], b[2:]), math.dist(a[2:], b[:2])]
    flip = sum(crossed) < sum(direct)
    aligned = crossed if flip else direct
    return dict(direct=max(direct), aligned=max(aligned), prefers_cross=flip,
                midpoint=math.dist([(a[0]+a[2])/2, (a[1]+a[3])/2],
                                   [(b[0]+b[2])/2, (b[1]+b[3])/2]),
                length_change=math.dist(b[:2], b[2:]) - math.dist(a[:2], a[2:]),
                swap_suspect=flip and max(direct) >= 20 and max(aligned) <= max(direct)*.25)


def selected(state):
    return state.get("selectedCandidate") or next((c for c in state.get("topCandidates", []) if c.get("selected")), {})


def candidate_endpoints(state, stage):
    c = selected(state)
    value = c.get(stage)
    if value is None:
        value = c.get("endpointPipeline", {}).get({"rawPCAEndpoints": "rawPCA", "finalOutputEndpoints": "finalSelected", "robustMainIntervalEndpoints": "robustInterval"}[stage])
    points = extract.endpoint_values({"endpoint": value})
    return points if None not in points else None


def analyze(paths, labels=()):
    rows, manifest = extract.extract(paths, 480, 640)
    diagnostics = extract.diagnostic_frames(paths)
    names = {s["alias"]: s["source_session"] for s in manifest["sessions"]}
    label_map = {}
    for label in labels:
        for frame in label["frames"]:
            label_map[(label["session"], frame, label["color"])] = label["motion"]
    counts = Counter()
    groups = defaultdict(lambda: defaultdict(list))
    events, previous = [], {}
    for row in rows:
        session, color = names[row["session"]], row["color"]
        key = (session, row["frame"], color)
        s = diagnostics.get(key, {})
        counts["colour_frames"] += 1
        counts["detected_" + str(row["detected"])] += 1
        counts["predicted_" + str(row["predicted"])] += 1
        counts["tracking_available"] += bool(s.get("tracking"))
        counts["recorded_candidate_switch"] += s.get("tracking", {}).get("candidateSwitch") is True
        counts["recorded_path_change"] += s.get("tracking", {}).get("endpointPathChanged") is True
        tr = s.get("tracking", {})
        if tr.get("candidateMatchConfidence") == "geometryMatch" and tr.get("candidateSwitch") is False:
            counts["recorded_geometry_match"] += 1
            for cutoff in (10,25,50):
                counts[f"recorded_geometry_length_ge_{cutoff}px"] += abs(tr.get("lengthChange",0)) >= cutoff
        p = previous.get((session, color))
        previous[(session, color)] = (row, s)
        if p is None:
            continue
        old, old_state = p
        if row["frame"] != old["frame"] + 1 or not 0 < row["time_s"]-old["time_s"] <= .05:
            counts["gap_pairs_excluded"] += 1
            continue
        counts["adjacent_pairs"] += 1
        counts["dropout_onsets"] += old["detected"] is True and row["detected"] is False
        counts["recovery_onsets"] += old["detected"] is False and row["detected"] is True
        if any(r["detected"] is not True or r["predicted"] is True or any(r[k] is None for k in ("x1", "y1", "x2", "y2")) for r in (old, row)):
            counts["non_measured_pairs_excluded"] += 1
            continue
        a, b = [[r[k] for k in ("x1", "y1", "x2", "y2")] for r in (old, row)]
        metrics = displacement(a, b)
        counts["measured_pairs"] += 1
        counts["unknown_prediction_pairs"] += old["predicted"] is None or row["predicted"] is None
        counts["cross_preferred"] += metrics["prefers_cross"]
        counts["swap_suspects"] += metrics["swap_suspect"]
        motion = label_map.get(key, "unknown")
        if label_map.get((session, old["frame"], color)) != motion:
            motion = "unknown"
        report_groups = ["all", motion, color]
        if old["predicted"] is False and row["predicted"] is False:
            report_groups.append("known_prediction")
        for group in report_groups:
            for field in ("direct", "aligned", "midpoint"):
                groups[group][field].append(metrics[field])
        tr = s.get("tracking", {})
        ca, cb = selected(old_state), selected(s)
        stage_metrics = {}
        for stage in ("rawPCAEndpoints", "finalOutputEndpoints"):
            pa, pb = candidate_endpoints(old_state, stage), candidate_endpoints(s, stage)
            if pa is not None and pb is not None:
                stage_metrics[stage] = displacement(pa, pb)
                counts[stage+"_pairs"] += 1
                counts[stage+"_cross_preferred"] += stage_metrics[stage]["prefers_cross"]
                counts[stage+"_swap_suspects"] += stage_metrics[stage]["swap_suspect"]
        body_a, body_b = candidate_endpoints(old_state, "robustMainIntervalEndpoints"), candidate_endpoints(s, "robustMainIntervalEndpoints")
        if body_a and body_b and motion in ("static", "moving"):
            # 保存済みbody端点の反実仮想。実剣の正解や本番採否にはしない。
            body_metrics = displacement(body_a,body_b)
            for field in ("direct", "aligned", "midpoint"):
                groups[motion+"_"+color+"_robust"][field].append(body_metrics[field])
        body_proxy = bool(body_a and body_b and ca.get("sourceType") == cb.get("sourceType")
                          and math.dist(body_a[:2],body_a[2:]) >= 20
                          and displacement(body_a,body_b)["aligned"] <= math.dist(body_a[:2],body_a[2:])*.25)
        counts["same_body_proxy_pairs"] += body_proxy
        if body_proxy:
            for cutoff in (10,25,50):
                counts[f"same_body_proxy_length_ge_{cutoff}px"] += abs(metrics["length_change"]) >= cutoff
        same = tr.get("candidateMatchConfidence") == "geometryMatch" and tr.get("candidateSwitch") is False and ca.get("sourceType") == cb.get("sourceType") and bool(cb)
        counts["same_candidate_pairs"] += same
        if same:
            for cutoff in (10, 25, 50):
                counts[f"same_candidate_length_ge_{cutoff}px"] += abs(metrics["length_change"]) >= cutoff
        reason = "recorded_switch" if tr.get("candidateSwitch") else "same_candidate" if same else "identity_unknown"
        if metrics["aligned"] >= 100:
            counts["jump100_"+reason] += 1
        if metrics["swap_suspect"] or metrics["aligned"] >= 100 or tr.get("candidateSwitch") or (same and abs(metrics["length_change"]) >= 10) or any(m["swap_suspect"] for m in stage_metrics.values()) or (motion=="static" and metrics["aligned"]>=25):
            events.append(dict(session=session, frame=row["frame"], color=color, motion=motion,
                               reason=reason, previous_endpoints=a, endpoints=b, **metrics,
                               raw_span_before=old_state.get("rawPCASpan", ca.get("rawPCASpan")), raw_span_after=s.get("rawPCASpan", cb.get("rawPCASpan")),
                               robust_before=old_state.get("robustMainIntervalLength", ca.get("robustMainIntervalLength")),
                               robust_after=s.get("robustMainIntervalLength", cb.get("robustMainIntervalLength")),
                               source_before=ca.get("sourceType"), source_after=cb.get("sourceType"),
                               tracking=tr, same_body_proxy=body_proxy, stages=stage_metrics))
    return dict(sessions=len(manifest["sessions"]), counts=dict(counts),
                distributions={g: {f: distribution(v) for f,v in fields.items()} for g,fields in groups.items()},
                events=events, manifest=manifest)


def candidate_replay(inbox):
    bundles = sorted(p.parent for p in inbox.glob("*/frames") if p.is_dir())
    sequences = selection.load_sequences(bundles)
    policy = selection.Policy(5, .5, .2, 3)
    gaps = selection.gap_distribution(sequences, policy)
    return dict(complete_geometry_colour_frames=sum(len(f) for f in sequences.values()), gaps=gaps,
                replay=[selection.replay(sequences, selection.Policy(m,.5,.2,3),100) for m in (1,3,5)])


def candidate_audits(inbox):
    # offline で既存 hint を読むだけ。32KB preflight や本番 gate は変更しない。
    rows = []
    for summary in sorted(inbox.glob("*/summary.json")):
        document = json.loads(summary.read_text())
        images = [SimpleNamespace(image_id=str(i), frame_id=item["frameID"],
                                  context_path=summary.parent/item["frameContextPath"])
                  for i,item in enumerate(document.get("images", [])) if item.get("frameContextPath")]
        for row in tracking.candidate_selection_audit(SimpleNamespace(images=images)):
            if row["countForTally"]:
                rows.append(dict(session=document["sessionID"], **row))
    return dict(counts=dict(Counter(r["hint"] for r in rows)), rows=rows)


def verify_labels(labels, downloads, inbox):
    verified = set()
    for label in labels:
        for evidence in label["images"]:
            relative = Path(evidence["path"])
            root = inbox if relative.parts[0].startswith("phone_saber_triage_") else downloads
            path = (root/relative).resolve()
            if not path.is_relative_to(root.resolve()) or "annotated" in path.name:
                raise ValueError("labels require corpus-relative ORIGINAL images")
            if hashlib.sha256(path.read_bytes()).hexdigest() != evidence["sha256"]:
                raise ValueError(f"label evidence changed: {relative}")
            verified.add(str(path))
    return len(verified)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--downloads", type=Path, required=True)
    parser.add_argument("--inbox", type=Path, required=True)
    parser.add_argument("--labels", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    start = time.perf_counter()
    labels = json.loads(args.labels.read_text())["labels"] if args.labels else []
    verified = verify_labels(labels,args.downloads,args.inbox)
    report = {"downloads": analyze(sorted(args.downloads.glob("phonesaber_*_metadata.json")), labels),
              "triage": analyze([args.inbox], labels), "candidate_replay": candidate_replay(args.inbox),
              "candidate_audit": candidate_audits(args.inbox), "labelled_original_images_verified": verified}
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2, allow_nan=False)+"\n")
    print(json.dumps(dict(seconds=round(time.perf_counter()-start,3),
                         counts={k: report[k]["counts"] for k in ("downloads","triage")}), ensure_ascii=False))


if __name__ == "__main__":
    main()
