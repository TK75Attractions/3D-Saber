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
              area=1000, span=100.0, final=(50, 100, 150, 100), match=None, reasons=()):
    entry = {"listIndex": index, "eligible": eligible, "sourceType": "color-component", "finalScore": 80.0,
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
          omitted_eligible=0, omitted_ineligible=0):
    saved_e = [c for c in candidates if c["eligible"]]
    saved_i = [c for c in candidates if not c["eligible"]]
    eligible_total = len(saved_e) + omitted_eligible if eligible is None else eligible
    result = {"totalCandidateCount": eligible_total + len(saved_i) + omitted_ineligible if total is None else total,
              "eligibleCandidateCount": eligible_total, "savedEligibleCount": len(saved_e),
              "eligibleOmittedCount": omitted_eligible, "savedIneligibleCount": len(saved_i),
              "ineligibleOmittedCount": omitted_ineligible, "candidatesTruncated": truncated,
              "candidates": candidates, "previousFrameGeometryAvailable": previous}
    if previous:
        result["previousWinner"] = copy.deepcopy(PREVIOUS)
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


if __name__ == "__main__":
    unittest.main()
