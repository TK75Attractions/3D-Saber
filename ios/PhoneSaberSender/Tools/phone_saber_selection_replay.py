#!/usr/bin/env python3
"""Offline replay of candidate temporal consistency on recorded candidate geometry.

Read-only evidence tool for the planned "fix A" (CLAUDE.md §4). It never changes
recognition: it reads the eligible candidate geometry that Debug Recording
stores in triage frame contexts and answers two questions:

1. ``distribution``: when the recorded winner R does not continue the previous
   winner but another eligible candidate M does, how large is the score gap
   R - M?  Production thresholds must come from this distribution, not from
   guesses.
2. ``replay``: if selection preferred M within a margin / correspondence /
   expiry policy, which frames would change, and how many large output jumps
   would remain?  Run it with several parameter sets (``--sweep``) to compare.

Only consecutive frames whose eligible lists were saved completely are used, so
a truncated list never produces a confident replay. The windows in a triage
bundle are short (event windows only); the counts describe those windows.
"""
from __future__ import annotations

import argparse
import json
import math
import sys
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable

COLORS = ("red", "blue")


@dataclass(frozen=True)
class Policy:
    """Replay policy. Values are inputs to compare, never production defaults."""
    margin: float
    max_distance: float  # centroid distance / previous rawPCASpan
    min_iou: float
    hold_frames: int     # consecutive overrides allowed before R is accepted

    def label(self) -> str:
        return (f"margin={self.margin:g} maxDistance={self.max_distance:g} "
                f"minIoU={self.min_iou:g} hold={self.hold_frames}")


def _number(value: Any) -> float | None:
    return float(value) if isinstance(value, (int, float)) and not isinstance(value, bool) \
        and math.isfinite(value) else None


def match_metrics(candidate: dict, previous: dict) -> dict:
    """Same geometry correspondence as the Swift triage builder (matchMetrics)."""
    result: dict[str, float] = {}
    c, w = candidate.get("centroid") or [], previous.get("centroid") or []
    span = max(_number(previous.get("rawPCASpan")) or 0.0, 1.0)
    if len(c) == 2 and len(w) == 2:
        distance = math.hypot(c[0] - w[0], c[1] - w[1])
        result["centroidDistance"] = distance
        result["centroidDistanceNormalized"] = distance / span
    cb, wb = candidate.get("bbox") or [], previous.get("bbox") or []
    if len(cb) == 4 and len(wb) == 4:
        ix = max(0, min(cb[2], wb[2]) - max(cb[0], wb[0]) + 1)
        iy = max(0, min(cb[3], wb[3]) - max(cb[1], wb[1]) + 1)
        intersection = ix * iy
        union = (cb[2] - cb[0] + 1) * (cb[3] - cb[1] + 1) + (wb[2] - wb[0] + 1) * (wb[3] - wb[1] + 1) - intersection
        result["bboxIoU"] = intersection / union if union > 0 else 0.0
    return result


def corresponds(candidate: dict, previous: dict, policy: Policy) -> bool:
    match = match_metrics(candidate, previous)
    return match.get("bboxIoU", 0.0) >= policy.min_iou \
        or match.get("centroidDistanceNormalized", math.inf) <= policy.max_distance


def _midpoint(endpoints: list) -> tuple[float, float] | None:
    if not isinstance(endpoints, list) or len(endpoints) != 4:
        return None
    return ((endpoints[0] + endpoints[2]) / 2, (endpoints[1] + endpoints[3]) / 2)


def _jump(a: dict | None, b: dict | None) -> float | None:
    pa = _midpoint(a.get("finalOutputEndpoints")) if a else None
    pb = _midpoint(b.get("finalOutputEndpoints")) if b else None
    return math.hypot(pa[0] - pb[0], pa[1] - pb[1]) if pa and pb else None


def _complete_eligible(geometry: dict) -> list[dict] | None:
    """Eligible candidates in rank order, or None when the saved list is partial."""
    if not isinstance(geometry, dict) or geometry.get("eligibleOmittedCount", 0) != 0:
        return None
    candidates = geometry.get("candidates")
    if not isinstance(candidates, list):
        return None
    eligible = sorted((c for c in candidates if isinstance(c, dict) and c.get("eligible") is True),
                      key=lambda c: c.get("eligibleRank", 0))
    if len(eligible) != geometry.get("eligibleCandidateCount"):
        return None
    return eligible


def load_sequences(bundles: Iterable[Path]) -> dict[tuple[str, str], list[tuple[int, list[dict]]]]:
    """(session, color) -> frames sorted by frameID with complete eligible lists."""
    found: dict[tuple[str, str], dict[int, list[dict]]] = {}
    for bundle in bundles:
        frames_dir = bundle / "frames"
        for path in sorted(frames_dir.glob("*.json")) if frames_dir.is_dir() else []:
            try:
                context = json.loads(path.read_text(encoding="utf-8"))
            except (OSError, json.JSONDecodeError):
                continue
            session = str(context.get("sessionID") or bundle.name)
            for frame in context.get("frames") or []:
                frame_id = frame.get("frameID") if isinstance(frame, dict) else None
                if not isinstance(frame_id, int):
                    continue
                for color in COLORS:
                    geometry = (frame.get(color) or {}).get("candidateGeometry")
                    eligible = _complete_eligible(geometry) if geometry else None
                    if eligible is None:
                        continue
                    table = found.setdefault((session, color), {})
                    # Contexts overlap; the longest saved list for a frame wins.
                    if len(eligible) >= len(table.get(frame_id, [])):
                        table[frame_id] = eligible
    return {key: sorted(table.items()) for key, table in found.items()}


def _runs(frames: list[tuple[int, list[dict]]]) -> Iterable[list[tuple[int, list[dict]]]]:
    run: list[tuple[int, list[dict]]] = []
    for frame in frames:
        if run and frame[0] != run[-1][0] + 1:
            yield run
            run = []
        run.append(frame)
    if run:
        yield run


def gap_distribution(sequences: dict, policy: Policy) -> list[dict]:
    """Recorded switches: R breaks continuity while an eligible M continues it."""
    rows = []
    for (session, color), frames in sorted(sequences.items()):
        for run in _runs(frames):
            for position in range(1, len(run)):
                previous_list, (frame_id, eligible) = run[position - 1][1], run[position]
                if not previous_list or len(eligible) < 2:
                    continue
                previous, winner = previous_list[0], eligible[0]
                earlier_list = run[position - 2][1] if position >= 2 else []
                if corresponds(winner, previous, policy):
                    continue
                matching = [c for c in eligible[1:] if corresponds(c, previous, policy)]
                if not matching:
                    continue
                best = max(matching, key=lambda c: c.get("finalScore", 0))
                rows.append({"session": session, "color": color, "frameID": frame_id,
                             "winnerScore": winner.get("finalScore"), "matchingScore": best.get("finalScore"),
                             "scoreGap": round(winner.get("finalScore", 0) - best.get("finalScore", 0), 4),
                             "winnerJumpPx": round(_jump(winner, previous) or 0, 1),
                             "matchingJumpPx": round(_jump(best, previous) or 0, 1),
                             # A return to the winner before last closes a switch; it is
                             # listed but kept out of the switch-out gap quantiles.
                             "returnsToEarlierWinner": bool(earlier_list)
                                 and corresponds(winner, earlier_list[0], policy)})
    return rows


def replay(sequences: dict, policy: Policy, jump_px: float) -> dict:
    """Simulate the policy; selection always comes from the current eligible list."""
    changed: list[dict] = []
    jumps_before = jumps_after = frames_compared = 0
    for (session, color), frames in sorted(sequences.items()):
        for run in _runs(frames):
            recorded_prev: dict | None = None
            simulated_prev: dict | None = None
            overrides = 0
            for frame_id, eligible in run:
                recorded = eligible[0] if eligible else None
                simulated = recorded
                if eligible and len(eligible) > 1 and simulated_prev is not None \
                        and not corresponds(recorded, simulated_prev, policy) and overrides < policy.hold_frames:
                    matching = [c for c in eligible[1:] if corresponds(c, simulated_prev, policy)
                                and recorded.get("finalScore", 0) - c.get("finalScore", 0) <= policy.margin]
                    if matching:
                        simulated = max(matching, key=lambda c: c.get("finalScore", 0))
                if simulated is recorded:
                    overrides = 0
                else:
                    overrides += 1  # the expiry is never extended by an override
                    changed.append({"session": session, "color": color, "frameID": frame_id,
                                    "recordedListIndex": recorded.get("listIndex"),
                                    "replayListIndex": simulated.get("listIndex"),
                                    "scoreGap": round(recorded.get("finalScore", 0) - simulated.get("finalScore", 0), 4)})
                if recorded_prev is not None and recorded is not None and simulated_prev is not None \
                        and simulated is not None:
                    frames_compared += 1
                    jumps_before += (_jump(recorded, recorded_prev) or 0) >= jump_px
                    jumps_after += (_jump(simulated, simulated_prev) or 0) >= jump_px
                recorded_prev, simulated_prev = recorded, simulated
                if not eligible:
                    overrides = 0
    return {"policy": policy.label(), "framesCompared": frames_compared,
            f"jumpsAtLeast{jump_px:g}pxRecorded": jumps_before,
            f"jumpsAtLeast{jump_px:g}pxReplay": jumps_after,
            "changedFrames": changed}


def quantiles(values: list[float]) -> dict:
    if not values:
        return {"count": 0}
    ordered = sorted(values)
    pick = lambda q: ordered[min(len(ordered) - 1, int(q * (len(ordered) - 1) + 0.5))]
    return {"count": len(ordered), "min": ordered[0], "p25": pick(0.25), "median": pick(0.5),
            "p75": pick(0.75), "max": ordered[-1]}


def _floats(text: str) -> list[float]:
    return [float(x) for x in text.split(",") if x.strip()]


def parse_args(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("bundles", nargs="+", type=Path, help="triage bundle directories (read-only)")
    parser.add_argument("--mode", choices=("distribution", "replay"), default="distribution")
    parser.add_argument("--margin", default="5", help="score margin(s), comma separated for a sweep")
    parser.add_argument("--max-distance", default="0.5",
                        help="centroid distance / previous span for correspondence (comma separated)")
    parser.add_argument("--min-iou", default="0.2", help="bbox IoU for correspondence (comma separated)")
    parser.add_argument("--hold", default="3", help="consecutive overrides before expiry (comma separated)")
    parser.add_argument("--jump-px", type=float, default=100.0)
    parser.add_argument("--json", action="store_true", help="print machine-readable JSON")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    missing = [str(b) for b in args.bundles if not (b / "frames").is_dir()]
    if missing:
        print(f"not a triage bundle (no frames/): {', '.join(missing)}", file=sys.stderr)
        return 2
    sequences = load_sequences(args.bundles)
    policies = [Policy(m, d, i, int(h)) for m in _floats(args.margin) for d in _floats(args.max_distance)
                for i in _floats(args.min_iou) for h in _floats(args.hold)]
    if args.mode == "distribution":
        rows = gap_distribution(sequences, policies[0])
        result: Any = {"correspondence": policies[0].label(), "events": rows,
                       "switchOutScoreGap": quantiles([r["scoreGap"] for r in rows
                                                       if not r["returnsToEarlierWinner"]])}
        if not args.json:
            print(f"sequences={len(sequences)} correspondence: {policies[0].label()}")
            for row in rows:
                print(" ".join(f"{k}={v}" for k, v in row.items()))
            print("switchOutScoreGap " + json.dumps(result["switchOutScoreGap"]))
    else:
        result = [replay(sequences, policy, args.jump_px) for policy in policies]
        if not args.json:
            for row in result:
                summary = {k: v for k, v in row.items() if k != "changedFrames"}
                print(json.dumps(summary, ensure_ascii=False) + f" changed={len(row['changedFrames'])}")
    if args.json:
        print(json.dumps(result, ensure_ascii=False))
    if not sequences:
        print("no complete candidate geometry found (bundles older than geometry recording?)", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
