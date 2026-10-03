#!/usr/bin/env python3
"""Offline study of candidate eligibility rules against hand-labelled frames.

Read-only evidence tool (docs/claude/analysis/2026-10-03_background_fp_rule_study.md).
It copies the production ``DetectionCore.swift`` / ``BGRADetection.swift`` into a
temporary directory, adds environment-controlled experimental eligibility gates
to the COPY only (red/blue purity floor, R7e, mean-value floor), compiles it with
``VideoDetectionDiagnostic.swift`` and replays every labelled frame of the
diagnostics inbox once per rule. The repository and the inbox are never written.

Labels come from a CSV (one row per session/frame/colour) whose ``truth`` is
``saber`` (with ``saber_box`` = "x0 y0 x1 y1" of the lit saber in the ORIGINAL
PNG), ``absent`` or ``unknown``. A winner is "real" when it overlaps the box,
"bg" otherwise. Per rule the report gives background winners, real-saber
winners lost against the unmodified detector, the fate of every >JUMP px output
jump (remains / becomes no detection / resolved), and optionally the formal
lossless corpus pass count.

    phone_saber_rule_study.py --labels docs/claude/analysis/2026-10-03_background_fp_labels.csv
        [--inbox DIR] [--rules base,r7e,pf22,r7e_pf22] [--gain 1.08] [--formal] [--json OUT]
"""
from __future__ import annotations

import argparse
import csv
import json
import math
import os
import shutil
import subprocess
import sys
import tempfile
from collections import Counter
from pathlib import Path
from typing import Any, Callable, Iterable

TOOLS_DIR = Path(__file__).resolve().parent
REPO = TOOLS_DIR.parents[2]
SOURCES = REPO / "ios/PhoneSaberSender/PhoneSaberSender"
DIAGNOSTIC = REPO / "ios/PhoneSaberSenderTests/VideoDetectionDiagnostic.swift"
DEFAULT_INBOX = Path.home() / "Library/Application Support/PhoneSaber/diagnostics-inbox"
COLORS = ("red", "blue")

# Experimental rules, all evaluated on a patched COPY of the detector.
RULES: dict[str, dict[str, str]] = {
    "base": {},
    "r7e": {"PS_RED_R7E": "1"},
    "pf22": {"PS_RED_PFLOOR": "0.22"},
    "r7e_pf22": {"PS_RED_R7E": "1", "PS_RED_PFLOOR": "0.22"},
    "bpf22": {"PS_BLUE_PFLOOR": "0.22"},
    "bmean225": {"PS_BLUE_MEANMIN": "225"},
    "r7e_pf22_bpf22": {"PS_RED_R7E": "1", "PS_RED_PFLOOR": "0.22", "PS_BLUE_PFLOOR": "0.22"},
}
ENV_KEYS = sorted({key for env in RULES.values() for key in env})

_GATE_ANCHOR = "private func clamp01(_ value: Double) -> Double { min(max(value, 0), 1) }\n"
_GATE_DECLARATIONS = '''
// EXPERIMENT ONLY (phone_saber_rule_study.py, temporary copy). Env-controlled gates.
private let expEnv = ProcessInfo.processInfo.environment
private func expD(_ k: String) -> Double? { expEnv[k].flatMap { Double($0) } }
private struct ExpGate {
    let pFloor: Double?; let pFloorClip: Double
    let r7e: Bool; let r7eClip: Double; let r7eThick: Double; let r7eSatD: Double; let r7eSatP: Double
    let meanMin: Double?; let meanMinClip: Double
}
private func expGate(_ c: String) -> ExpGate {
    ExpGate(pFloor: expD("PS_\\(c)_PFLOOR"), pFloorClip: expD("PS_\\(c)_PFLOOR_CLIP") ?? 0.35,
            r7e: expEnv["PS_\\(c)_R7E"] == "1", r7eClip: expD("PS_\\(c)_R7E_CLIP") ?? 0.35,
            r7eThick: expD("PS_\\(c)_R7E_THICK") ?? 4.2, r7eSatD: expD("PS_\\(c)_R7E_SATD") ?? 3.5,
            r7eSatP: expD("PS_\\(c)_R7E_SATP") ?? 0.60,
            meanMin: expD("PS_\\(c)_MEANMIN"), meanMinClip: expD("PS_\\(c)_MEANMIN_CLIP") ?? 0.35)
}
private let expRedGate = expGate("RED")
private let expBlueGate = expGate("BLUE")
'''
_ELIGIBILITY_ANCHOR = '''                && highRatio >= 0.50 && meanPurity >= 0.50
        }
'''
_GATE_BODY = '''        if isEmitterEligible {
            let g = evidence.color == .red ? expRedGate : expBlueGate
            let axial = body.density > 0 ? body.density : Double(points.count) / max(majorLength, 1.0)
            let d240 = axial * 240.0 / Double(max(min(width, height), 1))
            if let f = g.pFloor { isEmitterEligible = meanPurity >= f || clippedRatio >= g.pFloorClip }
            if isEmitterEligible, g.r7e {
                isEmitterEligible = clippedRatio >= g.r7eClip || d240 >= g.r7eThick
                    || (d240 >= g.r7eSatD && meanPurity >= g.r7eSatP)
            }
            if isEmitterEligible, let m = g.meanMin { isEmitterEligible = meanValue >= m || clippedRatio >= g.meanMinClip }
        }
'''


def patch_detection_core(source: str) -> str:
    """Return a copy of DetectionCore.swift with the env-controlled gates added.

    Raises ValueError when an anchor is missing or ambiguous (the production file
    changed shape), so the study never silently evaluates an unpatched copy.
    """
    if "expRedGate" in source:
        raise ValueError("source is already patched")
    for anchor in (_GATE_ANCHOR, _ELIGIBILITY_ANCHOR):
        if source.count(anchor) != 1:
            raise ValueError(f"anchor not found exactly once: {anchor.strip()[:60]!r}")
    source = source.replace(_GATE_ANCHOR, _GATE_ANCHOR + _GATE_DECLARATIONS, 1)
    return source.replace(_ELIGIBILITY_ANCHOR, _ELIGIBILITY_ANCHOR + _GATE_BODY, 1)


def build_harness(directory: Path) -> Path:
    directory.mkdir(parents=True, exist_ok=True)
    core = directory / "DetectionCore.swift"
    core.write_text(patch_detection_core((SOURCES / "DetectionCore.swift").read_text(encoding="utf-8")),
                    encoding="utf-8")
    shutil.copy(SOURCES / "BGRADetection.swift", directory / "BGRADetection.swift")
    shutil.copy(DIAGNOSTIC, directory / "VideoDetectionDiagnostic.swift")
    binary = directory / "rule-study-diagnostic"
    subprocess.run(["xcrun", "swiftc", "-O", str(core), str(directory / "BGRADetection.swift"),
                    str(directory / "VideoDetectionDiagnostic.swift"), "-o", str(binary)],
                   check=True, capture_output=True)
    return binary


# ---------------------------------------------------------------- labels

Truth = Any  # list[int] (saber box) | None (absent) | "?" (unknown)


def parse_truth(truth: str, box: str) -> Truth:
    if truth == "saber":
        values = [int(v) for v in box.split()]
        if len(values) != 4:
            raise ValueError(f"saber_box must have 4 integers: {box!r}")
        return values
    if truth == "absent":
        return None
    if truth == "unknown":
        return "?"
    raise ValueError(f"unknown truth {truth!r}")


def load_labels(path: Path) -> dict[tuple[str, int], dict[str, Truth]]:
    labels: dict[tuple[str, int], dict[str, Truth]] = {}
    with path.open(newline="", encoding="utf-8") as handle:
        for row in csv.DictReader(handle):
            if row["color"] not in COLORS:
                raise ValueError(f"bad color {row['color']!r}")
            labels.setdefault((row["session"], int(row["frame"])), {})[row["color"]] = parse_truth(
                row["truth"], row["saber_box"])
    for key, value in labels.items():
        if set(value) != set(COLORS):
            raise ValueError(f"{key}: both colors must be labelled")
    return labels


def resolve_image(inbox: Path, session: str, frame: int) -> Path | None:
    images = inbox / f"phone_saber_triage_phonesaber_{session}" / "images"
    if not images.is_dir():
        return None
    matches = sorted(p for p in images.glob(f"image_*_{frame}.png") if "annotated" not in p.name)
    return matches[0] if matches else None


# ---------------------------------------------------------------- evaluation


def _box(candidate: dict) -> list[int]:
    box = candidate["bounding_box"]
    return [box["min_x"], box["min_y"], box["max_x"], box["max_y"]]


def _area(box: list[float]) -> float:
    return max(0.0, box[2] - box[0] + 1) * max(0.0, box[3] - box[1] + 1)


def midpoint(candidate: dict) -> tuple[float, float]:
    a, b = candidate["endpoints"]
    return ((a["x"] + b["x"]) / 2, (a["y"] + b["y"]) / 2)


def label_candidate(candidate: dict, truth: Truth, pad: float = 15.0, overlap: float = 0.25) -> str:
    """'real' when the candidate box overlaps the saber box by >= `overlap` of its own
    area, or its output midpoint lies inside the padded saber box; else 'bg'."""
    if truth == "?":
        return "unk"
    if truth is None:
        return "bg"
    box = _box(candidate)
    inter = _area([max(box[0], truth[0]), max(box[1], truth[1]), min(box[2], truth[2]), min(box[3], truth[3])])
    mx, my = midpoint(candidate)
    inside = truth[0] - pad <= mx <= truth[2] + pad and truth[1] - pad <= my <= truth[3] + pad
    return "real" if inter >= overlap * _area(box) or inside else "bg"


def winner(color_result: dict) -> dict | None:
    """The production winner is the first eligible candidate of the ranked list."""
    return next((c for c in color_result["candidates"] if c["eligible"]), None)


Outcome = tuple[str, "dict | None"]


def frame_outcomes(analyses: dict[tuple[str, int], dict], labels: dict) -> dict[tuple[tuple[str, int], str], Outcome]:
    out = {}
    for key, analysis in analyses.items():
        for color in COLORS:
            best = winner(analysis["colors"][color])
            out[(key, color)] = (label_candidate(best, labels[key][color]) if best else "none", best)
    return out


def jump_events(outcomes: dict, threshold: float) -> set[tuple[tuple[str, int], str]]:
    events = set()
    for (key, color), (_, best) in outcomes.items():
        previous = outcomes.get(((key[0], key[1] - 1), color))
        if best is None or previous is None or previous[1] is None:
            continue
        if math.dist(midpoint(previous[1]), midpoint(best)) > threshold:
            events.add((key, color))
    return events


def jump_fate(event: tuple[tuple[str, int], str], outcomes: dict, threshold: float) -> str:
    (session, frame), color = event
    before = outcomes[((session, frame - 1), color)][1]
    after = outcomes[((session, frame), color)][1]
    if before is None or after is None:
        return "noDetection"
    return "remains" if math.dist(midpoint(before), midpoint(after)) > threshold else "resolved"


def summarize_rule(name: str, outcomes: dict, base: dict, labels: dict, threshold: float) -> dict:
    counts: Counter = Counter()
    for (key, color), (label, _) in outcomes.items():
        truth = labels[key][color]
        kind = "unknown" if truth == "?" else ("absent" if truth is None else "saber")
        counts[f"{color}:{kind}:{label}"] += 1
    lost = sorted(f"{key[0]}/{key[1]}/{color}" for (key, color), (label, _) in base.items()
                  if label == "real" and outcomes[(key, color)][0] != "real")
    base_events = jump_events(base, threshold)
    fates = Counter(jump_fate(event, outcomes, threshold) for event in base_events)
    new = sorted(f"{k[0]}/{k[1]}/{c}" for k, c in jump_events(outcomes, threshold) - base_events)
    return {"rule": name, "counts": dict(counts), "realLost": lost, "baseJumps": len(base_events),
            "jumpFates": dict(fates), "newJumps": new}


# ---------------------------------------------------------------- running


def apply_gain(raw: bytes, gain: float) -> bytes:
    """Scale B, G, R of a BGRA buffer by `gain` (rounded, clamped); alpha is kept."""
    if gain == 1.0:
        return raw
    table = bytes(min(255, max(0, round(value * gain))) for value in range(256))
    data = bytearray(raw.translate(table))
    data[3::4] = raw[3::4]
    return bytes(data)


def analyze_png(binary: Path, path: Path, env: dict[str, str], gain: float = 1.0) -> dict:
    size = subprocess.run(["ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries",
                           "stream=width,height", "-of", "csv=p=0", str(path)],
                          check=True, capture_output=True).stdout.decode().strip()
    width, height = (int(v) for v in size.split(","))
    raw = subprocess.run(["ffmpeg", "-loglevel", "error", "-i", str(path), "-frames:v", "1", "-f", "rawvideo",
                          "-pix_fmt", "bgra", "-"], check=True, capture_output=True).stdout
    raw = apply_gain(raw, gain)
    run_env = {k: v for k, v in os.environ.items() if k not in ENV_KEYS}
    run_env.update(env)
    line = subprocess.run([str(binary), str(width), str(height), "2"], input=raw, env=run_env,
                          check=True, capture_output=True).stdout.splitlines()[0]
    return json.loads(line)


def formal_pass_count(binary: Path, env: dict[str, str]) -> tuple[int, int, list[str]]:
    sys.path.insert(0, str(TOOLS_DIR))
    import run_lossless_regression as lossless  # noqa: E402
    _, fixtures = lossless.load_fixtures(lossless.DEFAULT_MANIFEST)
    cache: dict[Path, dict] = {}
    rows = []
    for fixture in fixtures:
        path = fixture["resolved_path"]
        if path not in cache:
            cache[path] = analyze_png(binary, path, env)
        rows.append(lossless.evaluate_fixture(fixture, cache[path]))
    failed = [row["name"] for row in rows if not row["passed"]]
    return len(rows) - len(failed), len(rows), failed


def render(summaries: list[dict]) -> str:
    lines = ["| rule | formal | red bg (absent) | red bg (saber) | red real | blue bg (absent) | blue bg (saber) "
             "| blue real | real lost | base jumps → remains / noDetection / resolved | new jumps |",
             "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |"]
    for s in summaries:
        c = s["counts"]
        f = s["jumpFates"]
        formal = s.get("formal", "-")
        lines.append(
            f"| {s['rule']} | {formal} | {c.get('red:absent:bg', 0)} | {c.get('red:saber:bg', 0)} "
            f"| {c.get('red:saber:real', 0)} | {c.get('blue:absent:bg', 0)} | {c.get('blue:saber:bg', 0)} "
            f"| {c.get('blue:saber:real', 0)} | {len(s['realLost'])} | {s['baseJumps']} → {f.get('remains', 0)} / "
            f"{f.get('noDetection', 0)} / {f.get('resolved', 0)} | {len(s['newJumps'])} |")
    return "\n".join(lines)


def run_study(labels: dict, inbox: Path, rules: Iterable[str], binary: Path, *, gain: float, threshold: float,
              formal: bool, analyze: Callable[..., dict] = analyze_png) -> list[dict]:
    paths = {key: resolve_image(inbox, *key) for key in labels}
    available = {key: path for key, path in paths.items() if path is not None}
    missing = sorted(set(paths) - set(available))
    if missing:
        print(f"warning: {len(missing)} labelled frames have no image in {inbox}", file=sys.stderr)
    usable = {key: labels[key] for key in available}
    base = None
    summaries = []
    for name in ["base", *[r for r in rules if r != "base"]]:
        analyses = {key: analyze(binary, path, RULES[name], gain) for key, path in available.items()}
        outcomes = frame_outcomes(analyses, usable)
        base = base or outcomes
        summary = summarize_rule(name, outcomes, base, usable, threshold)
        if formal:
            passed, total, failed = formal_pass_count(binary, RULES[name])
            summary["formal"] = f"{passed}/{total}"
            summary["formalFailed"] = failed
        summaries.append(summary)
    return summaries


def parse_args(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--labels", type=Path, required=True)
    parser.add_argument("--inbox", type=Path,
                        default=Path(os.environ.get("PHONESABER_DIAGNOSTICS_INBOX", DEFAULT_INBOX)))
    parser.add_argument("--rules", default="base,r7e,pf22,r7e_pf22")
    parser.add_argument("--gain", type=float, default=1.0, help="multiply BGR by this (exposure sensitivity)")
    parser.add_argument("--jump-px", type=float, default=100.0)
    parser.add_argument("--formal", action="store_true", help="also run the 40 formal lossless fixtures per rule")
    parser.add_argument("--json", type=Path)
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    rules = [r.strip() for r in args.rules.split(",") if r.strip()]
    unknown = [r for r in rules if r not in RULES]
    if unknown:
        print(f"error: unknown rules {unknown}; known: {sorted(RULES)}", file=sys.stderr)
        return 2
    labels = load_labels(args.labels)
    with tempfile.TemporaryDirectory(prefix="phonesaber-rule-study-") as temporary:
        binary = build_harness(Path(temporary))
        summaries = run_study(labels, args.inbox, rules, binary, gain=args.gain, threshold=args.jump_px,
                              formal=args.formal)
    print(render(summaries))
    if args.json:
        args.json.write_text(json.dumps(summaries, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
