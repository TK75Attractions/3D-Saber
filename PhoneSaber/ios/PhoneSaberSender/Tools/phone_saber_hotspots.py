#!/usr/bin/env python3
"""Static hotspot map: background objects that keep producing candidates in one place.

Read-only evidence tool (free, offline, no model call). Matte red background
objects (a label strip, a carabiner) can become emitter-eligible and win RED when
no lit saber is visible. A lit saber in a hand moves; a background object does
not. This tool groups every recorded candidate position of a triage bundle by
image location and reports, per color, the clusters that stay put.

Per frame and color the richest available source is used:

1. ``candidateGeometry.candidates`` (all saved candidates, eligible or not);
2. ``selectedCandidate`` (winner bbox/centroid; older bundles, selected frames);
3. ``candidateDecisionTrace`` entries that carry bbox/centroid (if any);
4. ``endpoint`` of a detected, non-predicted winner (bbox = endpoint extent,
   centroid = midpoint). Older bundles therefore only show *winners*: a static
   object that was eligible but lost is invisible there.

Clusters are formed greedily per color: an observation joins the nearest
cluster whose representative is within ``cluster_radius`` (fraction of the image
diagonal) or whose bbox IoU is at least ``cluster_min_iou`` (``match_metrics``
from ``phone_saber_selection_replay``, the Swift bbox convention).

``likelyBackground`` is a HINT, never a conclusion and never an input to any
gate. It is true when a cluster is present in at least ``min_frames`` distinct
frames spanning at least ``min_span_seconds``, its centroid jitter (p90
distance from the cluster median) is at most ``max_jitter`` of the image
diagonal, its median bbox IoU to the representative bbox is at least
``min_bbox_iou``, and it is eligible (or winning) in at least
``min_eligible_fraction`` of its frames, and it is present in at least
``min_coverage`` of the frames of that color that have position data inside its
span (a wobbling hand-held object splits into sparse clusters that each look
still; coverage rejects them). A real saber held very still can meet
this too; confirm on the ORIGINAL PNG.

    phone_saber_hotspots.py <bundle...> [--json]
"""
from __future__ import annotations

import argparse
import json
import math
import struct
import sys
from collections import Counter
from dataclasses import dataclass, field
from pathlib import Path
from statistics import median
from typing import Any, Iterable

from phone_saber_selection_replay import COLORS, match_metrics

DEFAULT_IMAGE_SIZE = (480, 640)
MIN_PLAUSIBLE_IMAGE_SIDE = 64  # test fixtures carry tiny placeholder PNGs
FALLBACK_FRAME_SECONDS = 1 / 30
WINDOW_GAP_FRAMES = 3  # frame gap that separates two recorded event windows
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"

# Source priority per (frame, color): lower is richer.
SOURCE_PRIORITY = {"candidateGeometry": 0, "selectedCandidate": 1, "candidateDecisionTrace": 2, "endpoint": 3}


@dataclass(frozen=True)
class HotspotParams:
    """Heuristic inputs for the hint. They are not production values."""
    cluster_radius: float = 0.03       # fraction of image diagonal (≈24 px at 480x640)
    cluster_min_iou: float = 0.5
    min_frames: int = 5
    min_span_seconds: float = 0.25
    max_jitter: float = 0.02           # p90 centroid distance / image diagonal (≈16 px)
    min_bbox_iou: float = 0.3
    min_eligible_fraction: float = 0.5
    min_coverage: float = 0.6          # present / frames of this color with data inside the span


@dataclass
class Observation:
    frame_id: int
    timestamp: float | None
    color: str
    centroid: tuple[float, float]
    bbox: list[float] | None
    eligible: bool
    winning: bool
    final_score: float | None
    source_type: str | None
    source: str
    # Index of the candidate in its frame's candidate list (geometry ``listIndex``,
    # trace/selected ``index``); lets other evidence for the same candidate be joined.
    candidate_index: int | None = None


@dataclass
class _Cluster:
    color: str
    members: list[Observation] = field(default_factory=list)
    _cached: dict | None = field(default=None, repr=False)
    _cached_size: int = field(default=0, repr=False)

    def add(self, observation: Observation) -> None:
        self.members.append(observation)

    def representative(self) -> dict:
        # Medians are exact while a cluster is small and refreshed every 16 joins
        # after that: a static object's median barely moves, and recomputing it for
        # every observation made clustering quadratic (4,000 observations: 2-3 s).
        size = len(self.members)
        if self._cached is not None and size > 64 and size - self._cached_size < 16:
            return self._cached
        self._cached, self._cached_size = self._representative(), size
        return self._cached

    def _representative(self) -> dict:
        xs = [m.centroid[0] for m in self.members]
        ys = [m.centroid[1] for m in self.members]
        rep: dict[str, Any] = {"centroid": [median(xs), median(ys)]}
        boxes = [m.bbox for m in self.members if m.bbox]
        if boxes:
            rep["bbox"] = [median(b[i] for b in boxes) for i in range(4)]
        return rep


def _number(value: Any) -> float | None:
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        return None
    return float(value)


def _point(value: Any) -> tuple[float, float] | None:
    if isinstance(value, list) and len(value) == 2:
        x, y = _number(value[0]), _number(value[1])
        if x is not None and y is not None:
            return (x, y)
    return None


def _box(value: Any) -> list[float] | None:
    if isinstance(value, list) and len(value) == 4:
        numbers = [_number(v) for v in value]
        if all(n is not None for n in numbers):
            return numbers  # type: ignore[return-value]
    return None


def _candidate_observation(entry: dict, *, frame_id: int, timestamp: float | None, color: str,
                           eligible: bool, winning: bool, source: str) -> Observation | None:
    bbox = _box(entry.get("bbox"))
    centroid = _point(entry.get("centroid"))
    if centroid is None and bbox is not None:
        centroid = ((bbox[0] + bbox[2]) / 2, (bbox[1] + bbox[3]) / 2)
    if centroid is None:
        return None
    index = entry.get("listIndex", entry.get("index"))
    index = index if isinstance(index, int) and not isinstance(index, bool) else None
    return Observation(frame_id, timestamp, color, centroid, bbox, eligible, winning,
                       _number(entry.get("finalScore")), entry.get("sourceType"), source, index)


def frame_observations(frame: dict, color: str) -> tuple[str, list[Observation]] | None:
    """Observations of one frame/color from the richest source present, or None."""
    frame_id = frame.get("frameID")
    data = frame.get(color)
    if isinstance(frame_id, bool) or not isinstance(frame_id, int) or not isinstance(data, dict):
        return None
    timestamp = _number(frame.get("timestamp"))
    common = {"frame_id": frame_id, "timestamp": timestamp, "color": color}

    geometry = data.get("candidateGeometry")
    candidates = geometry.get("candidates") if isinstance(geometry, dict) else None
    if isinstance(candidates, list):
        found = []
        for entry in candidates:
            if not isinstance(entry, dict):
                continue
            eligible = entry.get("eligible") is True
            obs = _candidate_observation(entry, **common, eligible=eligible,
                                         winning=eligible and entry.get("eligibleRank") == 1,
                                         source="candidateGeometry")
            if obs is not None:
                found.append(obs)
        if found:
            return "candidateGeometry", found

    detected = data.get("detected") is True and data.get("predictionUsed") is not True
    selected = data.get("selectedCandidate")
    if detected and isinstance(selected, dict):
        obs = _candidate_observation(selected, **common, eligible=True, winning=True,
                                     source="selectedCandidate")
        if obs is not None:
            if obs.final_score is None:
                obs.final_score = _number(data.get("score"))
            return "selectedCandidate", [obs]

    trace = data.get("candidateDecisionTrace")
    if isinstance(trace, list):
        found = []
        selected_index = data.get("selectedCandidateIndex")
        for entry in trace:
            if not isinstance(entry, dict):
                continue
            eligible = entry.get("eligible") is True
            obs = _candidate_observation(entry, **common, eligible=eligible,
                                         winning=detected and eligible and entry.get("index") == selected_index,
                                         source="candidateDecisionTrace")
            if obs is not None:
                found.append(obs)
        if found:
            return "candidateDecisionTrace", found

    endpoint = _box(data.get("endpoint"))
    if detected and endpoint is not None and (data.get("eligibleCandidateCount") or 0) > 0:
        x1, y1, x2, y2 = endpoint
        bbox = [min(x1, x2), min(y1, y2), max(x1, x2), max(y1, y2)]
        index = data.get("selectedCandidateIndex")
        index = index if isinstance(index, int) and not isinstance(index, bool) else None
        return "endpoint", [Observation(frame_id, timestamp, color, ((x1 + x2) / 2, (y1 + y2) / 2), bbox,
                                        True, True, _number(data.get("score")),
                                        data.get("selectedCandidateType"), "endpoint", index)]
    return None


def _bundle_root(bundle_dir_or_plan: Any) -> Path:
    # Path itself has a ``root`` attribute ("/"), so only a non-path plan is unwrapped.
    if isinstance(bundle_dir_or_plan, (str, Path)):
        return Path(bundle_dir_or_plan)
    return Path(bundle_dir_or_plan.root)


def _png_size(path: Path) -> tuple[int, int] | None:
    try:
        with path.open("rb") as stream:
            header = stream.read(24)
    except OSError:
        return None
    if len(header) < 24 or not header.startswith(PNG_SIGNATURE) or header[12:16] != b"IHDR":
        return None
    return struct.unpack(">II", header[16:24])


def image_size(root: Path) -> tuple[tuple[int, int], str]:
    images = root / "images"
    for path in sorted(images.glob("*.png")) if images.is_dir() else []:
        if path.is_symlink():
            continue
        size = _png_size(path)
        if size and min(size) >= MIN_PLAUSIBLE_IMAGE_SIDE:
            return size, path.name
    return DEFAULT_IMAGE_SIZE, "default (no plausible bundle PNG)"


def load_observations(root: Path) -> tuple[str | None, list[Observation], Counter]:
    """Per (frame, color) the richest source; overlapping contexts are merged."""
    chosen: dict[tuple[int, str], tuple[int, list[Observation]]] = {}
    session = None
    frames_dir = root / "frames"
    for path in sorted(frames_dir.glob("*.json")) if frames_dir.is_dir() else []:
        if path.is_symlink():
            continue
        try:
            context = json.loads(path.read_text(encoding="utf-8"))
        except (OSError, UnicodeDecodeError, json.JSONDecodeError):
            continue
        if not isinstance(context, dict):
            continue
        session = session or (context.get("sessionID") if isinstance(context.get("sessionID"), str) else None)
        for frame in context.get("frames") or []:
            if not isinstance(frame, dict):
                continue
            for color in COLORS:
                result = frame_observations(frame, color)
                if result is None:
                    continue
                source, observations = result
                key = (observations[0].frame_id, color)
                rank = SOURCE_PRIORITY[source]
                current = chosen.get(key)
                if current is None or rank < current[0] or (rank == current[0] and len(observations) > len(current[1])):
                    chosen[key] = (rank, observations)
    observations = [o for _, obs in chosen.values() for o in obs]
    observations.sort(key=lambda o: (o.color, o.frame_id, -(o.final_score or 0)))
    sources = Counter(obs[0].source for _, obs in chosen.values())
    return session, observations, sources


def _cluster(observations: list[Observation], radius_px: float, min_iou: float) -> list[_Cluster]:
    clusters: list[_Cluster] = []
    for obs in observations:
        best, best_distance = None, math.inf
        probe = {"centroid": list(obs.centroid), "bbox": obs.bbox}
        for cluster in clusters:
            if cluster.color != obs.color:
                continue
            metrics = match_metrics(probe, cluster.representative())
            distance = metrics.get("centroidDistance", math.inf)
            if (distance <= radius_px or metrics.get("bboxIoU", 0.0) >= min_iou) and distance < best_distance:
                best, best_distance = cluster, distance
        if best is None:
            best = _Cluster(obs.color)
            clusters.append(best)
        best.add(obs)
    return clusters


def _p90(values: list[float]) -> float:
    ordered = sorted(values)
    return ordered[min(len(ordered) - 1, int(0.9 * (len(ordered) - 1) + 0.5))] if ordered else 0.0


def _windows(frame_ids: list[int]) -> int:
    return sum(1 for i, f in enumerate(frame_ids) if i == 0 or f - frame_ids[i - 1] > WINDOW_GAP_FRAMES)


def _summarize(cluster: _Cluster, diagonal: float, params: HotspotParams,
               winners_by_frame: dict[tuple[str, int], list[Observation]],
               images_by_frame: dict[int, list[str]], data_frames: set[tuple[str, int]],
               include_members: bool = False) -> dict:
    # One observation per frame: the best-scoring member (a frame can hold two pieces).
    per_frame: dict[int, Observation] = {}
    for member in cluster.members:
        kept = per_frame.get(member.frame_id)
        if kept is None or (member.winning, member.eligible, member.final_score or 0) > \
                (kept.winning, kept.eligible, kept.final_score or 0):
            per_frame[member.frame_id] = member
    frames = sorted(per_frame)
    observations = [per_frame[f] for f in frames]
    rep = cluster.representative()
    cx, cy = rep["centroid"]
    jitter_px = _p90([math.hypot(o.centroid[0] - cx, o.centroid[1] - cy) for o in observations])
    ious = [match_metrics({"bbox": o.bbox}, rep).get("bboxIoU") for o in observations if o.bbox and rep.get("bbox")]
    bbox_iou = median(ious) if ious else None
    timestamps = [o.timestamp for o in observations if o.timestamp is not None]
    span = (max(timestamps) - min(timestamps)) if len(timestamps) >= 2 \
        else (frames[-1] - frames[0]) * FALLBACK_FRAME_SECONDS
    eligible = sum(1 for o in observations if o.eligible)
    winning = [o.frame_id for o in observations if o.winning]
    scores = [o.final_score for o in observations if o.final_score is not None]
    eligible_scores = [o.final_score for o in observations if o.final_score is not None and o.eligible]
    # Frames of this cluster's span where the same color's winner was somewhere else.
    member_ids = {id(m) for m in cluster.members}
    elsewhere = sorted(fid for (color, fid), winners in winners_by_frame.items()
                       if color == cluster.color and frames[0] <= fid <= frames[-1]
                       and not any(id(w) in member_ids for w in winners))
    eligible_fraction = eligible / len(observations)
    in_span = sum(1 for color, fid in data_frames if color == cluster.color and frames[0] <= fid <= frames[-1])
    coverage = len(frames) / in_span if in_span else 0.0
    checks = {
        "frames": len(frames) >= params.min_frames,
        "span": span >= params.min_span_seconds,
        "jitter": jitter_px <= params.max_jitter * diagonal,
        "bboxStable": bbox_iou is None or bbox_iou >= params.min_bbox_iou,
        "eligibleOrWinning": eligible_fraction >= params.min_eligible_fraction or bool(winning),
        "coverage": coverage >= params.min_coverage,
    }
    sources = Counter(o.source for o in observations)
    result = {
        "color": cluster.color,
        "centroid": [round(cx, 1), round(cy, 1)],
        "bbox": [round(v, 1) for v in rep["bbox"]] if rep.get("bbox") else None,
        "framesPresent": len(frames),
        "frameIDs": frames,
        "windows": _windows(frames),
        "spanSeconds": round(span, 3),
        "coverage": round(coverage, 3),
        "eligibleFraction": round(eligible_fraction, 3),
        "winningFraction": round(len(winning) / len(observations), 3),
        "winningFrames": winning,
        "winnerElsewhereFrames": elsewhere,
        "maxFinalScore": round(max(scores), 2) if scores else None,
        "medianFinalScore": round(median(scores), 2) if scores else None,
        "medianEligibleFinalScore": round(median(eligible_scores), 2) if eligible_scores else None,
        "sourceTypes": dict(Counter(str(o.source_type) for o in observations if o.source_type)),
        "dataSources": dict(sources),
        "winnersOnly": set(sources) <= {"selectedCandidate", "endpoint"},
        "centroidJitterPx": round(jitter_px, 1),
        "centroidJitterFraction": round(jitter_px / diagonal, 4),
        "medianBboxIoU": round(bbox_iou, 3) if bbox_iou is not None else None,
        "checks": checks,
        "likelyBackground": all(checks.values()),
        "selectedImages": sorted({p for f in frames for p in images_by_frame.get(f, [])}),
    }
    if include_members:
        # Every member (a frame can hold two pieces), so other per-candidate evidence
        # can be joined by (frameID, candidateIndex). Opt-in: it grows with the bundle.
        result["members"] = [{"frameID": m.frame_id, "candidateIndex": m.candidate_index,
                              "eligible": m.eligible, "winning": m.winning, "finalScore": m.final_score,
                              "source": m.source}
                             for m in sorted(cluster.members, key=lambda m: (m.frame_id, m.candidate_index or 0))]
    return result


def _selected_images(root: Path) -> dict[int, list[str]]:
    try:
        summary = json.loads((root / "summary.json").read_text(encoding="utf-8"))
    except (OSError, UnicodeDecodeError, json.JSONDecodeError):
        return {}
    result: dict[int, list[str]] = {}
    for image in summary.get("images") or [] if isinstance(summary, dict) else []:
        if isinstance(image, dict) and isinstance(image.get("frameID"), int) and isinstance(image.get("path"), str):
            result.setdefault(image["frameID"], []).append(image["path"])
    return result


def static_hotspots(bundle_dir_or_plan: Any, params: HotspotParams = HotspotParams(), *,
                    image_size_override: tuple[int, int] | None = None,
                    include_members: bool = False) -> dict:
    """Clusters per color for one triage bundle (directory or input plan). Read-only.

    ``include_members`` adds each cluster's ``members`` (frameID, candidateIndex,
    eligible, winning, finalScore, source) for joining per-candidate evidence.
    """
    root = _bundle_root(bundle_dir_or_plan)
    session, observations, sources = load_observations(root)
    size, size_source = (image_size_override, "override") if image_size_override else image_size(root)
    diagonal = math.hypot(*size)
    winners_by_frame: dict[tuple[str, int], list[Observation]] = {}
    for obs in observations:
        if obs.winning:
            winners_by_frame.setdefault((obs.color, obs.frame_id), []).append(obs)
    clusters = _cluster(observations, params.cluster_radius * diagonal, params.cluster_min_iou)
    images_by_frame = _selected_images(root)
    data_frames = {(o.color, o.frame_id) for o in observations}
    summaries = [_summarize(c, diagonal, params, winners_by_frame, images_by_frame, data_frames,
                            include_members) for c in clusters]
    summaries.sort(key=lambda s: (not s["likelyBackground"], -s["framesPresent"], -(s["maxFinalScore"] or 0)))
    by_color: dict[str, list[dict]] = {color: [] for color in COLORS}
    for summary in summaries:
        by_color[summary["color"]].append(summary)
        summary["clusterID"] = f"{summary['color']}-{len(by_color[summary['color']])}"
    frame_count = len(data_frames)  # (color, frameID) pairs, not distinct frames
    return {
        "bundle": str(root),
        "sessionID": session,
        "imageSize": list(size),
        "imageSizeSource": size_source,
        "params": params.__dict__,
        "framesWithPositions": frame_count,
        "frameSources": dict(sources),
        "winnersOnly": bool(sources) and set(sources) <= {"selectedCandidate", "endpoint"},
        "clusters": by_color,
        "likelyBackgroundCount": sum(1 for s in summaries if s["likelyBackground"]),
        "note": "likelyBackground is a heuristic hint, not a conclusion; confirm on the ORIGINAL PNG",
    }


def format_cluster(cluster: dict) -> str:
    flag = "LIKELY BACKGROUND" if cluster["likelyBackground"] else "-"
    failed = [k for k, ok in cluster["checks"].items() if not ok]
    return (f"{cluster['clusterID']} [{flag}] centroid {cluster['centroid']} bbox {cluster['bbox']} "
            f"frames {cluster['framesPresent']} (windows {cluster['windows']}, span {cluster['spanSeconds']}s, "
            f"coverage {cluster['coverage']:.0%}, {cluster['frameIDs'][0]}–{cluster['frameIDs'][-1]}) eligible {cluster['eligibleFraction']:.0%} "
            f"winning {cluster['winningFraction']:.0%} score max/median {cluster['maxFinalScore']}/"
            f"{cluster['medianFinalScore']} jitter {cluster['centroidJitterPx']}px "
            f"bboxIoU {cluster['medianBboxIoU']} types {cluster['sourceTypes']}"
            + (f" failed {failed}" if failed else ""))


def render_text(result: dict, min_frames_to_list: int = 2) -> str:
    lines = [f"{result['sessionID'] or result['bundle']}: image {result['imageSize'][0]}x{result['imageSize'][1]} "
             f"({result['imageSizeSource']}), frame-color pairs with positions {result['framesWithPositions']} "
             f"sources {result['frameSources']}"
             + (" — winners only (no candidateGeometry): eligible losers are not visible" if result["winnersOnly"]
                else "")]
    if not result["framesWithPositions"]:
        lines.append("  n/a — no candidate geometry or winner positions in this bundle")
    for color in COLORS:
        shown = [c for c in result["clusters"][color] if c["framesPresent"] >= min_frames_to_list
                 or c["likelyBackground"]]
        hidden = len(result["clusters"][color]) - len(shown)
        if not result["clusters"][color]:
            continue
        lines.append(f"  {color}: {len(result['clusters'][color])} clusters"
                     + (f" ({hidden} with < {min_frames_to_list} frames hidden)" if hidden else ""))
        lines.extend(f"    {format_cluster(c)}" for c in shown)
    return "\n".join(lines)


def parse_args(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("bundles", nargs="+", type=Path, help="triage bundle directories (read-only)")
    parser.add_argument("--json", action="store_true", help="print machine-readable JSON")
    defaults = HotspotParams()
    parser.add_argument("--min-frames", type=int, default=defaults.min_frames)
    parser.add_argument("--min-span-seconds", type=float, default=defaults.min_span_seconds)
    parser.add_argument("--max-jitter", type=float, default=defaults.max_jitter,
                        help="p90 centroid jitter as a fraction of the image diagonal")
    parser.add_argument("--cluster-radius", type=float, default=defaults.cluster_radius,
                        help="cluster join distance as a fraction of the image diagonal")
    parser.add_argument("--min-coverage", type=float, default=defaults.min_coverage,
                        help="present frames / frames of that color with data inside the cluster span")
    parser.add_argument("--list-min-frames", type=int, default=2,
                        help="text output: hide clusters with fewer frames unless flagged")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    missing = [str(b) for b in args.bundles if not (b / "frames").is_dir()]
    if missing:
        print(f"not a triage bundle (no frames/): {', '.join(missing)}", file=sys.stderr)
        return 2
    params = HotspotParams(cluster_radius=args.cluster_radius, min_frames=args.min_frames,
                           min_span_seconds=args.min_span_seconds, max_jitter=args.max_jitter,
                           min_coverage=args.min_coverage)
    results = [static_hotspots(bundle, params) for bundle in args.bundles]
    if args.json:
        print(json.dumps(results if len(results) > 1 else results[0], ensure_ascii=False))
    else:
        print("\n\n".join(render_text(r, args.list_min_frames) for r in results))
        print("\nlikelyBackground is a hint only (static position + eligible/winning); confirm on ORIGINAL PNGs.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
