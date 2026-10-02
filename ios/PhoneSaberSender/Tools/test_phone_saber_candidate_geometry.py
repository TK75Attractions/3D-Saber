#!/usr/bin/env python3
"""Candidate geometry contract and the hint audit that separates CASE A / B / C."""
from __future__ import annotations

import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parent))

from phone_saber_tracking_diagnostics import candidate_selection_audit, validate_candidate_geometry
from phone_saber_triage_protocol import BundleError


def candidate(index, *, eligible=True, rank=None, centroid=(100.0, 100.0), bbox=(50, 90, 150, 110),
              area=1000, span=100.0, final=(50, 100, 150, 100), match=None, reasons=(), score=80.0):
    entry = {"listIndex": index, "eligible": eligible, "sourceType": "color-component", "finalScore": score,
             "scoreBreakdown": {"total": 80.0, "radiance": 10.0}, "centroid": list(centroid),
             "centroidSource": "trace", "bbox": list(bbox), "componentArea": area, "rawPCASpan": span,
             "rawPCAEndpoints": list(final), "finalOutputEndpoints": list(final),
             "rejectionReasons": list(reasons)}
    if eligible:
        entry["eligibleRank"] = rank
    if match is not None:
        entry["matchToPreviousWinner"] = match
    return entry


SAME = {"centroidDistance": 0.0, "centroidDistanceNormalized": 0.0, "bboxIoU": 1.0, "areaRatio": 1.0,
        "spanRatio": 1.0, "orientationDifference": 0.0}
FAR = {"centroidDistance": 800.0, "centroidDistanceNormalized": 8.0, "bboxIoU": 0.0, "areaRatio": 1.0,
       "spanRatio": 1.0, "orientationDifference": 0.0}
PREVIOUS = {"frameID": 9, "listIndex": 0, "centroid": [100.0, 100.0], "bbox": [50, 90, 150, 110],
            "componentArea": 1000, "rawPCASpan": 100.0, "rawPCAEndpoints": [50, 100, 150, 100],
            "finalOutputEndpoints": [50, 100, 150, 100]}


def block(candidates, *, total=None, eligible=None, truncated=False, previous=True,
          omitted_eligible=0, omitted_ineligible=0, previous_winner=None, fill_match=True):
    # Swift writes matchToPreviousWinner on every entry exactly when a previous winner exists.
    if fill_match and previous:
        for c in candidates:
            c.setdefault("matchToPreviousWinner", dict(SAME))
    saved_e = [c for c in candidates if c["eligible"]]
    saved_i = [c for c in candidates if not c["eligible"]]
    eligible_total = len(saved_e) + omitted_eligible if eligible is None else eligible
    result = {"totalCandidateCount": eligible_total + len(saved_i) + omitted_ineligible if total is None else total,
              "eligibleCandidateCount": eligible_total, "savedEligibleCount": len(saved_e),
              "eligibleOmittedCount": omitted_eligible, "savedIneligibleCount": len(saved_i),
              "ineligibleOmittedCount": omitted_ineligible, "candidatesTruncated": truncated,
              "candidates": candidates, "previousFrameGeometryAvailable": previous}
    if previous:
        result["previousWinner"] = copy.deepcopy(previous_winner or PREVIOUS)
    return result


class CandidateGeometryTests(unittest.TestCase):
    def audit(self, geometry, tracking=None):
        tmp = tempfile.TemporaryDirectory(); self.addCleanup(tmp.cleanup)
        path = Path(tmp.name) / "context.json"
        color = {"candidateGeometry": geometry}
        if tracking:
            color["tracking"] = tracking
        path.write_text(json.dumps({"activeColors": ["red"], "frames": [
            {"frameID": 10, "timestamp": 0.3, "red": color, "blue": {}}]}))
        plan = SimpleNamespace(images=[SimpleNamespace(context_path=path, image_id="image_001", frame_id=10)])
        return candidate_selection_audit(plan)

    # --- the three shapes the next capture has to tell apart ---

    def test_case_a_matching_candidate_stays_eligible_but_a_far_one_wins(self):
        rows = self.audit(block([
            candidate(0, rank=1, centroid=(900.0, 100.0), bbox=(850, 90, 950, 110), match=FAR),
            candidate(1, rank=2, match=SAME)]))
        self.assertEqual([r["hint"] for r in rows], ["A"])
        self.assertEqual(rows[0]["winnerListIndex"], 0)
        self.assertEqual(rows[0]["matchingAlternativeListIndex"], 1)
        self.assertIn("scoreMargin", rows[0])
        self.assertEqual(rows[0]["previousWinnerFrameID"], 9)

    def test_case_a_is_found_even_when_the_list_order_changed(self):
        # The previous winner is now listed second; identity comes from geometry, not index.
        rows = self.audit(block([candidate(0, rank=1, match=FAR), candidate(1, rank=2, match=SAME)]))
        self.assertEqual(rows[0]["hint"], "A")
        self.assertEqual(rows[0]["matchingAlternativeListIndex"], 1)

    def test_case_b_candidate_was_never_generated(self):
        rows = self.audit(block([], total=0, eligible=0))
        self.assertEqual((rows[0]["hint"], rows[0]["bCause"]), ("B", "notGenerated"))

    def test_case_b_matching_candidate_is_ineligible_with_its_reasons(self):
        rows = self.audit(block([candidate(0, eligible=False, match=SAME, reasons=["peakValue"])]))
        self.assertEqual((rows[0]["hint"], rows[0]["bCause"]), ("B", "ineligible"))
        self.assertEqual(rows[0]["ineligibleMatchesPreviousWinner"],
                         [{"listIndex": 0, "rejectionReasons": ["peakValue"]}])

    def test_case_b_with_a_far_winner_and_nothing_matching(self):
        rows = self.audit(block([candidate(0, rank=1, match=FAR)]))
        self.assertEqual((rows[0]["hint"], rows[0]["bCause"]), ("B", "noCandidateMatchesPreviousWinner"))

    def test_truncation_prevents_a_confident_case_b(self):
        rows = self.audit(block([candidate(0, rank=1, match=FAR)], truncated=True, omitted_eligible=2))
        self.assertEqual((rows[0]["hint"], rows[0]["bCause"]), ("unknown", "unknownTruncated"))
        self.assertTrue(rows[0]["candidatesTruncated"])

    def test_case_c_winner_matches_but_its_endpoints_break(self):
        broken = candidate(0, rank=1, match=SAME, final=(50, 100, 80, 100))
        rows = self.audit(block([broken]))
        self.assertEqual(rows[0]["hint"], "C")
        self.assertLess(rows[0]["endpointDiscontinuity"]["lengthRatio"], 0.67)
        self.assertEqual(self.audit(block([candidate(0, rank=1, match=SAME)]))[0]["hint"], "none")

    def test_nothing_is_reported_without_previous_frame_geometry(self):
        self.assertEqual(self.audit(block([candidate(0, rank=1)], previous=False)), [])

    # --- contract ---

    def test_valid_block_is_accepted_and_reconciles_with_the_recorded_eligible_count(self):
        valid = block([candidate(0, rank=1, match=SAME), candidate(1, rank=2, match=FAR),
                       candidate(2, eligible=False, match=FAR, reasons=["peakValue"])],
                      omitted_eligible=1, truncated=True)
        validate_candidate_geometry(valid, eligible_count=3)
        with self.assertRaises(BundleError):
            validate_candidate_geometry(valid, eligible_count=2)

    def test_counts_and_truncation_flag_must_agree(self):
        base = block([candidate(0, rank=1), candidate(1, rank=2)])
        for mutate in (
            lambda b: b.update(eligibleCandidateCount=5),
            lambda b: b.update(candidatesTruncated=True),
            lambda b: b.update(eligibleOmittedCount=1),
            lambda b: b.update(savedEligibleCount=1),
            lambda b: b.update(totalCandidateCount=9),
            lambda b: b["candidates"][1].update(eligibleRank=1),
            lambda b: b["candidates"][0].pop("eligibleRank"),
            lambda b: b["candidates"][0].update(centroid=[1.0]),
            lambda b: b["candidates"][0].update(matchToPreviousWinner={"bboxIoU": "x"}),
            lambda b: b.update(previousFrameGeometryAvailable=False),
        ):
            broken = copy.deepcopy(base)
            mutate(broken)
            with self.subTest(), self.assertRaises(BundleError):
                validate_candidate_geometry(broken)

    def test_reduced_score_breakdown_is_explicit_and_accepted(self):
        reduced = block([candidate(0, rank=1)])
        reduced["candidates"][0]["scoreBreakdown"] = {"total": 80.0}
        reduced["candidates"][0]["scoreBreakdownReduced"] = True
        validate_candidate_geometry(reduced)
        reduced["candidates"][0]["scoreBreakdownReduced"] = False
        with self.assertRaises(BundleError):
            validate_candidate_geometry(reduced)


OUTLIER = {"centroid": (900.0, 100.0), "bbox": (850, 90, 950, 110), "final": (850, 100, 950, 100)}
ELSEWHERE = {"centroid": (500.0, 600.0), "bbox": (450, 590, 550, 610), "final": (450, 600, 550, 600)}


def winner_of(frame_id, geometry=None, index=0):
    g = geometry or {"centroid": (100.0, 100.0), "bbox": (50, 90, 150, 110), "final": (50, 100, 150, 100)}
    return {"frameID": frame_id, "listIndex": index, "centroid": list(g["centroid"]), "bbox": list(g["bbox"]),
            "componentArea": 1000, "rawPCASpan": 100.0, "rawPCAEndpoints": list(g["final"]),
            "finalOutputEndpoints": list(g["final"])}


class CandidateAuditRegressionTests(unittest.TestCase):
    def run_audit(self, context, images):
        tmp = tempfile.TemporaryDirectory(); self.addCleanup(tmp.cleanup)
        plan_images = []
        for number, frame_id in enumerate(images):
            path = Path(tmp.name) / f"context_{number}.json"
            path.write_text(json.dumps(context(frame_id) if callable(context) else context))
            plan_images.append(SimpleNamespace(context_path=path, image_id=f"image_{number + 1:03d}", frame_id=frame_id))
        return candidate_selection_audit(SimpleNamespace(images=plan_images))

    def single(self, geometry):
        return self.run_audit({"activeColors": ["red"], "frames": [
            {"frameID": 10, "timestamp": 0.3, "red": {"candidateGeometry": geometry}, "blue": {}}]}, [10])

    # 1. bundles the validator accepted used to crash the audit
    def test_contract_violations_are_rejected_and_never_crash_the_audit(self):
        def no_winner_match(b): b["candidates"][0].pop("matchToPreviousWinner")
        def no_previous(b): b.pop("previousWinner")
        def null_previous(b): b["previousWinner"] = None
        def partial_match(b): b["candidates"][1]["matchToPreviousWinner"].pop("spanRatio")
        def match_without_previous(b): b.update(previousFrameGeometryAvailable=False); b.pop("previousWinner")
        for mutate in (no_winner_match, no_previous, null_previous, partial_match, match_without_previous):
            broken = block([candidate(0, rank=1, match=dict(FAR)), candidate(1, rank=2, match=dict(SAME))])
            mutate(broken)
            with self.subTest(mutate.__name__):
                with self.assertRaises(BundleError):
                    validate_candidate_geometry(broken)
                rows = self.single(broken)  # must not raise
                self.assertEqual([r["hint"] for r in rows], ["unknown"])
        weird = {"activeColors": "red", "frames": [None, {"frameID": 10, "red": "x", "blue": {"candidateGeometry": 7}}]}
        self.assertEqual([r["hint"] for r in self.run_audit(weird, [10])], ["unknown"])

    # 2. eligible ranks are exactly the prefix 1..k
    def test_eligible_ranks_must_be_the_prefix_one_to_k(self):
        gap = block([candidate(0, rank=2, match=dict(FAR)), candidate(1, rank=3, match=dict(SAME))])
        with self.assertRaises(BundleError):
            validate_candidate_geometry(gap, eligible_count=2)
        validate_candidate_geometry(block([candidate(0, rank=1), candidate(1, rank=2)]), eligible_count=2)

    def test_eligible_candidates_that_were_not_saved_are_unknown_not_b(self):
        unsaved = block([candidate(0, eligible=False, match=dict(FAR), reasons=["peakValue"])],
                        omitted_eligible=2, truncated=True)
        validate_candidate_geometry(unsaved, eligible_count=2)
        row = self.single(unsaved)[0]
        self.assertEqual((row["hint"], row["bCause"]), ("unknown", "unknownTruncated"))

    # 3. only omitted ELIGIBLE candidates can hide an A
    def test_omitted_ineligible_candidates_do_not_turn_b_into_unknown(self):
        row = self.single(block([candidate(0, rank=1, match=dict(FAR))], truncated=True, omitted_ineligible=3))[0]
        self.assertEqual((row["hint"], row["bCause"]), ("B", "unknownTruncated"))
        row = self.single(block([candidate(0, rank=1, match=dict(FAR))], truncated=True, omitted_eligible=1))[0]
        self.assertEqual(row["hint"], "unknown")

    # 4. the return leg of a one-frame switch-out is not a second CASE A
    def switch_context(self, return_geometry=None):
        onset = block([candidate(0, rank=1, match=dict(FAR), **OUTLIER), candidate(1, rank=2, match=dict(SAME))],
                      previous_winner=winner_of(8))
        back = return_geometry or {"centroid": (100.0, 100.0), "bbox": (50, 90, 150, 110), "final": (50, 100, 150, 100)}
        ret = block([candidate(0, rank=1, match=dict(FAR), **back), candidate(1, rank=2, match=dict(SAME), **OUTLIER)],
                    previous_winner=winner_of(9, OUTLIER))
        stable = block([candidate(0, rank=1, match=dict(SAME))], previous_winner=winner_of(7))
        return {"activeColors": ["red"], "frames": [
            {"frameID": f, "timestamp": f / 30, "red": {"candidateGeometry": g}, "blue": {}}
            for f, g in ((8, stable), (9, onset), (10, ret))]}

    def test_switch_onset_is_a_and_the_return_frame_is_recovery(self):
        rows = self.run_audit(self.switch_context(), [9, 10])
        self.assertEqual([(r["frameID"], r["hint"]) for r in rows], [(9, "A"), (10, "recovery")])
        self.assertEqual((rows[1]["switchedOutFrameID"], rows[1]["returnsToWinnerOfFrameID"]), (9, 8))
        self.assertTrue(all(r["countForTally"] for r in rows))

    def test_a_second_jump_elsewhere_is_not_recovery(self):
        rows = self.run_audit(self.switch_context(ELSEWHERE), [10])
        self.assertEqual(rows[0]["hint"], "A")

    # 5. one tally row per (frameID, color)
    def test_rows_flag_the_subject_color_and_count_each_frame_color_once(self):
        geometry = block([candidate(0, rank=1, match=dict(FAR)), candidate(1, rank=2, match=dict(SAME))])
        context = {"selectedColor": "red", "activeColors": ["red", "blue"], "frames": [
            {"frameID": 10, "timestamp": 0.3, "red": {"candidateGeometry": geometry},
             "blue": {"candidateGeometry": copy.deepcopy(geometry)}}]}
        rows = self.run_audit(context, [10, 10])
        self.assertEqual([(r["color"], r["imageColor"], r["subjectColor"]) for r in rows],
                         [("red", "red", True), ("blue", "red", False)] * 2)
        tallied = [(r["frameID"], r["color"]) for r in rows if r["countForTally"]]
        self.assertEqual(sorted(tallied), [(10, "blue"), (10, "red")])

    # 6. narrowness is informational only
    def test_case_a_reports_the_score_margin_ratio_without_a_threshold(self):
        wide = block([candidate(0, rank=1, match=dict(FAR), score=80.0, **OUTLIER),
                      candidate(1, rank=2, match=dict(SAME), score=20.0)])
        row = self.single(wide)[0]
        self.assertEqual((row["hint"], row["scoreMargin"], row["scoreMarginRatio"]), ("A", 60.0, 0.75))


if __name__ == "__main__":
    unittest.main()
