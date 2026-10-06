#!/usr/bin/env python3
"""Repeat the production Swift detector on the lossless fixtures.

This measures detector wall time on the host, not camera, UI, or device FPS.
"""

from __future__ import annotations

import argparse
import json
import os
import statistics
import tempfile
from pathlib import Path

from run_lossless_regression import (DEFAULT_MANIFEST, analyze_png, command_output, compile_harness,
                                     load_fixtures, percentile, selected_candidate)


# The fixture harness's component observer counts the cleaned-mask pass.
# On-device DEBUG aggregation additionally counts every proposal-mask pass.
METRICS = ("total_ms", "scan_ms", "morphology_ms", "component_score_ms",
           "line_proposal_ms", "line_score_ms", "selection_ms", "candidate_count",
           "line_proposal_count", "components")


def summarize(samples: list[dict]) -> dict:
    def value(row: dict, metric: str) -> float:
        return row["components"] if metric == "components" else row["profile"][metric]

    result = {}
    for blade_present in (True, False):
        group = [sample for sample in samples if sample["blade_present"] == blade_present]
        result["blade" if blade_present else "no_blade"] = {
            metric: {
                "median": statistics.median(value(row, metric) for row in group),
                "p95": percentile([value(row, metric) for row in group], 0.95),
                "max": max(value(row, metric) for row in group),
            }
            for metric in METRICS
        }
        result["blade" if blade_present else "no_blade"]["samples"] = len(group)
    return result


def compile_detector(destination: Path, source_root: Path | None) -> None:
    if source_root is None:
        compile_harness(destination)
        return
    source = source_root / "ios/PhoneSaberSender/PhoneSaberSender"
    harness_source = Path(__file__).resolve().parents[2] / "PhoneSaberSenderTests/VideoDetectionDiagnostic.swift"
    command_output(["xcrun", "swiftc", "-O", str(source / "DetectionCore.swift"),
                    str(source / "BGRADetection.swift"), str(harness_source),
                    "-o", str(destination)])


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, default=DEFAULT_MANIFEST)
    parser.add_argument("--iterations", type=int, default=5)
    parser.add_argument("--json", type=Path, required=True)
    parser.add_argument("--source-root", type=Path,
                        help="compile detector sources from this checkout (for baseline comparisons)")
    parser.add_argument("--baseline-source-root", type=Path,
                        help="interleave this baseline checkout with the current detector")
    args = parser.parse_args()
    if args.iterations < 5:
        parser.error("at least five iterations are required")
    fixtures, skipped = load_fixtures(args.manifest)
    # Each fixture is analyzed in a fresh Swift process. Fix Set's hash seed
    # so harmless floating-point summation order does not look like a score change.
    os.environ["SWIFT_DETERMINISTIC_HASHING"] = "1"
    samples = []
    with tempfile.TemporaryDirectory(prefix="phonesaber-benchmark-") as temporary:
        harness = Path(temporary) / "detector"
        compile_detector(harness, args.source_root)
        baseline = Path(temporary) / "baseline"
        if args.baseline_source_root is not None:
            compile_detector(baseline, args.baseline_source_root)
        for iteration in range(args.iterations):
            for fixture in fixtures:
                phases = [("after", harness)]
                if args.baseline_source_root is not None:
                    phases.append(("before", baseline))
                    if iteration % 2 == 0:
                        phases.reverse()
                for phase, executable in phases:
                    analysis = analyze_png(executable, fixture["resolved_path"])
                    colors = analysis["colors"]
                    selected = {
                        color: {
                            "detected": colors[color]["selected"] is not None,
                            "type": winner["source"] if (winner := selected_candidate(colors[color])) else None,
                            "endpoint": colors[color]["selected"],
                            "score": winner["score"] if winner else None,
                            "candidate_count": len(colors[color]["candidates"]),
                        }
                        for color in ("red", "blue")
                    }
                    samples.append({
                        "phase": phase, "iteration": iteration, "fixture": fixture["name"],
                        "blade_present": fixture["category"] not in ("no-blade", "background-negative"),
                        "profile": analysis["profile"],
                        "components": sum(value["components"] for value in analysis["pipeline"].values()),
                        "selected": selected,
                    })
    if args.baseline_source_root is None:
        result = summarize(samples)
    else:
        result = {phase: summarize([row for row in samples if row["phase"] == phase])
                  for phase in ("before", "after")}
    output = {"iterations": args.iterations, "fixture_count": len(fixtures),
              "skipped_optional": skipped, "summary": result, "samples": samples}
    args.json.write_text(json.dumps(output, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(output["summary"], indent=2))


if __name__ == "__main__":
    main()
