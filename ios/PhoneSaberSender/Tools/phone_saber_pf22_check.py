#!/usr/bin/env python3
"""Offline check of the shadow PF22 red purity-floor rule across triage bundles.

Read-only, free and offline. PF22 (``meanColorPurity >= 0.22 OR
clippedWhiteRatio >= 0.35``, explored in
docs/claude/analysis/2026-10-03_motion_blur_and_blue_jumps.md) is recorded as
``shadowPF22`` with ``applied: false`` by newer recorders; for older bundles it
is recomputed from the plain candidate fields wherever they exist. Production
eligibility, ranking and UDP never read it. Per session this prints how many
red winners and eligible red candidates PF22 would reject, and what it would do
to the selected-frame red jump / candidateSwitch events (``noDetection`` only
when every eligible candidate of the frame is known and rejected).

    phone_saber_pf22_check.py <bundle_dir> [<bundle_dir> ...] [--json]
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

from phone_saber_background_evidence import EVENT_OUTCOMES, collect, pf22_tally


def session_row(bundle: Path) -> dict:
    tally = pf22_tally(collect(bundle))
    return {"bundle": bundle.name, **{k: tally[k] for k in ("winners", "eligibleCandidates")},
            "events": {k: v for k, v in tally["events"].items() if k != "rows"},
            "eventRows": [r for r in tally["events"]["rows"] if r["color"] == "red"]}


def render(rows: list[dict]) -> str:
    lines = ["| session | red winners (with verdict) | PF22 would reject | eligible red (with verdict) "
             "| eligible would reject | red events | → noDetection | → winnerChanges | unchanged | unknown/n/a "
             "| R7e → noDetection |",
             "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |"]
    totals = {"w": 0, "wv": 0, "wr": 0, "e": 0, "ev": 0, "er": 0, "events": 0,
              **{k: 0 for k in EVENT_OUTCOMES}, "r7eNo": 0}
    for row in rows:
        w, e, ev = row["winners"], row["eligibleCandidates"], row["events"]
        pf = ev["pf22Outcomes"]
        values = {"w": w["frames"], "wv": w["withVerdict"], "wr": w["pf22WouldReject"], "e": e["candidates"],
                  "ev": e["withVerdict"], "er": e["pf22WouldReject"], "events": ev["red"], **pf,
                  "r7eNo": ev["r7eOutcomes"]["noDetection"]}
        for key, value in values.items():
            totals[key] += value
        lines.append(_line(row["bundle"].removeprefix("phone_saber_triage_"), values))
    lines.append(_line("**total**", totals))
    lines.append("")
    lines.append("Evidence only (applied:false). noDetection requires the frame's full eligible list; "
                 "check ORIGINAL PNGs before reading any event as a real saber.")
    for row in rows:
        for event in row["eventRows"]:
            if event["pf22Outcome"] in {"noDetection", "winnerChanges"}:
                lines.append(f"- {row['bundle'].removeprefix('phone_saber_triage_')} frame {event['frameID']}: "
                             f"{event['pf22Outcome']} (switch {event['candidateSwitch']}, jump "
                             f"{event['midpointDisplacementPx']}px, winner purity "
                             f"{event.get('winnerMeanColorPurity')}, clippedWhite "
                             f"{event.get('winnerClippedWhiteRatio')}, R7e {event['r7eOutcome']})")
    return "\n".join(lines)


def _line(label: str, v: dict) -> str:
    return (f"| {label} | {v['w']} ({v['wv']}) | {v['wr']} | {v['e']} ({v['ev']}) | {v['er']} | {v['events']} "
            f"| {v['noDetection']} | {v['winnerChanges']} | {v['unchanged']} | {v['unknown'] + v['n/a']} "
            f"| {v['r7eNo']} |")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("bundles", nargs="+", type=Path)
    parser.add_argument("--json", action="store_true", help="print the per-session data as JSON")
    args = parser.parse_args(argv)
    rows = [session_row(bundle) for bundle in args.bundles if (bundle / "frames").is_dir()]
    if not rows:
        print("no triage bundle with a frames/ directory given", file=sys.stderr)
        return 2
    print(json.dumps(rows, indent=2, ensure_ascii=False) if args.json else render(rows))
    return 0


if __name__ == "__main__":
    sys.exit(main())
