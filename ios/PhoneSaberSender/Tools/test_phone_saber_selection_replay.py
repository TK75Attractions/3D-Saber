#!/usr/bin/env python3
"""Offline candidate temporal-consistency replay (evidence tool for fix A)."""
from __future__ import annotations

import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import phone_saber_selection_replay as replay_tool
from phone_saber_selection_replay import Policy, gap_distribution, load_sequences, match_metrics, replay


def cand(index, rank, x, score, *, eligible=True):
    entry = {"listIndex": index, "eligible": eligible, "finalScore": score, "centroid": [x, 100.0],
             "bbox": [x - 50, 90, x + 50, 110], "rawPCASpan": 100.0,
             "finalOutputEndpoints": [x - 50, 100, x + 50, 100]}
    if eligible:
        entry["eligibleRank"] = rank
    return entry


def geometry(candidates, omitted=0):
    eligible = [c for c in candidates if c["eligible"]]
    return {"totalCandidateCount": len(candidates) + omitted, "eligibleCandidateCount": len(eligible) + omitted,
            "eligibleOmittedCount": omitted, "candidates": candidates}


# Saber at x=100 throughout; a distant light at x=600 narrowly wins frame 11 only.
SWITCH = {
    10: [cand(0, 1, 100, 80), cand(1, 2, 600, 70)],
    11: [cand(1, 1, 600, 80.5), cand(0, 2, 102, 80)],
    12: [cand(0, 1, 104, 81), cand(1, 2, 600, 70)],
}


def write_bundle(root: Path, frames: dict[int, list], *, omitted: dict[int, int] | None = None,
                 session="phonesaber_test") -> Path:
    bundle = root / session
    (bundle / "frames").mkdir(parents=True)
    context = {"sessionID": session, "frames": [
        {"frameID": fid, "red": {"candidateGeometry": geometry(c, (omitted or {}).get(fid, 0))}, "blue": {}}
        for fid, c in frames.items()]}
    (bundle / "frames" / "frame_11_1.json").write_text(json.dumps(context))
    return bundle


class SelectionReplayTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.policy = Policy(margin=1.0, max_distance=0.5, min_iou=0.2, hold_frames=3)

    def test_match_metrics_follow_the_swift_bbox_convention(self):
        same = match_metrics(cand(0, 1, 100, 1), cand(0, 1, 100, 1))
        self.assertEqual(same["bboxIoU"], 1.0)
        self.assertEqual(same["centroidDistanceNormalized"], 0.0)
        self.assertEqual(match_metrics(cand(0, 1, 600, 1), cand(0, 1, 100, 1))["bboxIoU"], 0.0)

    def test_distribution_reports_the_gap_of_a_recorded_switch(self):
        rows = gap_distribution(load_sequences([write_bundle(self.root, SWITCH)]), self.policy)
        self.assertEqual([(r["frameID"], r["scoreGap"], r["returnsToEarlierWinner"]) for r in rows],
                         [(11, 0.5, False), (12, 11, True)])
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            replay_tool.main([str(self.root / "phonesaber_test"), "--json"])
        self.assertEqual(json.loads(out.getvalue())["switchOutScoreGap"]["count"], 1)
        self.assertGreater(rows[0]["winnerJumpPx"], 400)
        self.assertLess(rows[0]["matchingJumpPx"], 10)

    def test_replay_keeps_the_continuing_candidate_within_the_margin(self):
        result = replay(load_sequences([write_bundle(self.root, SWITCH)]), self.policy, 100)
        self.assertEqual([(c["frameID"], c["replayListIndex"]) for c in result["changedFrames"]], [(11, 0)])
        self.assertEqual(result["jumpsAtLeast100pxRecorded"], 2)
        self.assertEqual(result["jumpsAtLeast100pxReplay"], 0)

    def test_replay_accepts_the_winner_outside_the_margin(self):
        policy = Policy(margin=0.1, max_distance=0.5, min_iou=0.2, hold_frames=3)
        result = replay(load_sequences([write_bundle(self.root, SWITCH)]), policy, 100)
        self.assertEqual(result["changedFrames"], [])

    def test_override_expires_and_is_not_extended(self):
        frames = {10: [cand(0, 1, 100, 80)]}
        for fid in range(11, 16):  # the far light keeps winning narrowly
            frames[fid] = [cand(1, 1, 600, 80.5), cand(0, 2, 100, 80)]
        policy = Policy(margin=1.0, max_distance=0.5, min_iou=0.2, hold_frames=2)
        changed = replay(load_sequences([write_bundle(self.root, frames)]), policy, 100)["changedFrames"]
        self.assertEqual([c["frameID"] for c in changed], [11, 12])

    def test_single_eligible_candidate_is_always_accepted(self):
        frames = {10: [cand(0, 1, 100, 80)], 11: [cand(1, 1, 600, 80)]}
        self.assertEqual(replay(load_sequences([write_bundle(self.root, frames)]), self.policy, 100)["changedFrames"], [])

    def test_truncated_eligible_lists_are_not_replayed(self):
        bundle = write_bundle(self.root, SWITCH, omitted={11: 2})
        sequences = load_sequences([bundle])
        self.assertEqual([fid for fid, _ in sequences[("phonesaber_test", "red")]], [10, 12])
        self.assertEqual(gap_distribution(sequences, self.policy), [])
        self.assertEqual(replay(sequences, self.policy, 100)["changedFrames"], [])

    def test_bundles_sharing_a_session_id_stay_separate(self):
        first = write_bundle(self.root / "one", SWITCH)
        second = write_bundle(self.root / "two", SWITCH)
        sequences = load_sequences([first, second])
        self.assertEqual(len(sequences), 2)
        rows = [r for r in gap_distribution(sequences, self.policy) if not r["returnsToEarlierWinner"]]
        self.assertEqual(len(rows), 2, "both bundles' switch-outs are counted")

    def test_cli_reports_missing_geometry_without_failing(self):
        bundle = self.root / "old"
        (bundle / "frames").mkdir(parents=True)
        (bundle / "frames" / "frame_1_1.json").write_text(json.dumps({"frames": [{"frameID": 1, "red": {}}]}))
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            self.assertEqual(replay_tool.main([str(bundle)]), 0)
        self.assertIn("no complete candidate geometry", err.getvalue())

    def test_cli_sweep_prints_one_row_per_policy(self):
        bundle = write_bundle(self.root, SWITCH)
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            replay_tool.main([str(bundle), "--mode", "replay", "--margin", "0.1,1", "--json"])
        rows = json.loads(out.getvalue())
        self.assertEqual([len(r["changedFrames"]) for r in rows], [0, 1])


if __name__ == "__main__":
    unittest.main()
