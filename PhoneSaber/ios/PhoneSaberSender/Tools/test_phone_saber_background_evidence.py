#!/usr/bin/env python3
"""Session report section 「背景誤検出の証拠」 on synthetic triage contexts.

The contexts carry the additive fields exactly as PHONE_SABER_DEBUG_METADATA_SCHEMA.md
describes them: full ``emitterDiagnostics`` on selected-frame decision-trace
entries, the compact geometry ``emitter`` subset, and selected-frame ``camera``.
The fixtures reuse the validator-checked shapes of test_phone_saber_emitter_diagnostics.
"""
from __future__ import annotations

import contextlib
import copy
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import phone_saber_session_report as report_tool
from phone_saber_background_evidence import (
    background_evidence, normalize_emitter, peak_from_term, render_lines)
from phone_saber_hotspots import static_hotspots
from phone_saber_session_report import build_report, render_markdown
from phone_saber_tracking_diagnostics import validate_candidate_geometry, validate_emitter_diagnostics
from test_phone_saber_emitter_diagnostics import CAMERA, EMITTER, GEOMETRY_EMITTER
from test_phone_saber_hotspots import LABEL_BOX, geo_cand, geometry, write_contexts
from test_phone_saber_triage_codex import write_codex_bundle

SECTION = "## 背景誤検出の証拠(emitter / 露出)"

# A matte label strip that passes production eligibility.
LABEL_FULL = copy.deepcopy(EMITTER)
LABEL_FULL.update(emitterScore=0.6127, emitterScoreMargin=0.1927, peakTerm=0.2327, meanTerm=0.1, highValueTerm=0.19,
                  purityTerm=0.09, clippedWhiteTerm=0.0, coreByPeakAndMean=False)
LABEL_COMPACT = {k: LABEL_FULL[k] for k in GEOMETRY_EMITTER}

SABER_FULL = dict(copy.deepcopy(EMITTER), clippedWhiteTerm=0.02)
SABER_COMPACT = {k: SABER_FULL[k] for k in GEOMETRY_EMITTER}
BLUE_FULL = copy.deepcopy(EMITTER)

BRIGHT = dict(CAMERA, iso=800, exposureDurationSeconds=1 / 30, exposureBiasEV=0.0)
DIM = dict(CAMERA, iso=200, exposureDurationSeconds=1 / 120, exposureBiasEV=-0.3)


def trace(index, emitter, *, peak=None, eligible=True):
    entry = {"index": index, "sourceType": "color-close", "eligible": eligible, "finalScore": 60.0,
             "rejectionReasons": [], "rules": [], "emitterDiagnostics": copy.deepcopy(emitter)}
    if peak is not None:
        entry["peakValue"] = peak
    return entry


def label_then_saber_frames() -> list[dict]:
    """Frames 100-105: only the static label (wins). 106-111: a moving saber wins,
    the label is still eligible (rank 2) except in 110-111 where it is ineligible."""
    frames = []
    for i in range(12):
        frame_id = 100 + i
        label_eligible = i < 10
        if i < 6:
            label = geo_cand(0, [80.0 + (i % 2) * 0.5, 245.0], LABEL_BOX, 55.0, rank=1)
            label["emitter"] = copy.deepcopy(LABEL_COMPACT)
            red = {"detected": True, "predictionUsed": False, "selectedCandidateIndex": 0, "eligibleCandidateCount": 1,
                   "score": 55.0, "candidateDecisionTrace": [trace(0, LABEL_FULL, peak=241)],
                   "candidateGeometry": geometry([label])}
        else:
            x = 150 + 60 * i
            saber = geo_cand(0, [x, 400.0], [x - 50, 380, x + 50, 420], 90.0, rank=1)
            saber["emitter"] = copy.deepcopy(SABER_COMPACT)
            label = geo_cand(1, [80.0, 245.0], LABEL_BOX, 50.0, rank=2 if label_eligible else None)
            label["emitter"] = copy.deepcopy(LABEL_COMPACT)
            red = {"detected": True, "predictionUsed": False, "selectedCandidateIndex": 0,
                   "eligibleCandidateCount": 2 if label_eligible else 1, "score": 90.0,
                   "candidateDecisionTrace": [trace(0, SABER_FULL, peak=255)],
                   "candidateGeometry": geometry([saber, label])}
        blue: dict = {"detected": False}
        if i == 3:
            cand = geo_cand(0, [400.0, 100.0], [380, 90, 420, 110], 70.0, rank=1)
            cand["emitter"] = copy.deepcopy(GEOMETRY_EMITTER)
            blue = {"detected": True, "predictionUsed": False, "selectedCandidateIndex": 0,
                    "eligibleCandidateCount": 1, "candidateDecisionTrace": [trace(0, BLUE_FULL, peak=250)],
                    "candidateGeometry": geometry([cand])}
        frames.append({"frameID": frame_id, "timestamp": i / 30,
                       "camera": copy.deepcopy(BRIGHT if label_eligible else DIM), "red": red, "blue": blue})
    return frames


class FixtureShapeTests(unittest.TestCase):
    def test_fixtures_follow_the_validated_schema(self):
        for emitter in (LABEL_FULL, SABER_FULL, BLUE_FULL):
            validate_emitter_diagnostics(emitter)
        for frame in label_then_saber_frames():
            for color in ("red", "blue"):
                block = frame[color].get("candidateGeometry")
                if block:
                    for entry in block["candidates"]:  # the hotspot fixture omits unrelated geometry keys
                        entry.update({"centroidSource": "trace", "componentArea": 100, "rawPCASpan": 50.0,
                                      "rawPCAEndpoints": [0, 0, 1, 1], "finalOutputEndpoints": [0, 0, 1, 1],
                                      "rejectionReasons": [], "scoreBreakdown": {"total": 1.0}})
                    block.update(savedEligibleCount=sum(c["eligible"] for c in block["candidates"]),
                                 savedIneligibleCount=sum(not c["eligible"] for c in block["candidates"]),
                                 ineligibleOmittedCount=0, candidatesTruncated=False,
                                 previousFrameGeometryAvailable=False)
                    validate_candidate_geometry(block, block["eligibleCandidateCount"])


class NormalisationTests(unittest.TestCase):
    def test_terms_core_inputs_and_peak_inversion(self):
        full = normalize_emitter(EMITTER, "decisionTrace")
        self.assertEqual([d["term"] for d in full["dominantTerms"][:2]], ["peak", "highValue"])
        self.assertAlmostEqual(full["emitterScoreMargin"], 0.474)
        self.assertEqual(full["coreInputs"], {"coreByHighValueRatio": True, "coreByPeakAndMean": True,
                                              "coreByClippedWhite": False})
        compact = normalize_emitter(GEOMETRY_EMITTER, "candidateGeometry")
        self.assertIsNone(compact["coreInputs"])  # the compact subset has no core inputs
        self.assertEqual(peak_from_term(normalize_emitter(LABEL_COMPACT, "candidateGeometry")), 240.0)
        self.assertEqual(peak_from_term({"terms": {"peak": 0.32}}), 255.0)
        self.assertIsNone(peak_from_term({"terms": {"peak": 0.0}}))  # clamped low: unknown
        self.assertIsNone(normalize_emitter("garbage", "decisionTrace"))


class BackgroundEvidenceSectionTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        # One frame per context: every frame is a selected frame.
        self.bundle = write_contexts(self.root, label_then_saber_frames(), chunk=1)

    def section(self) -> dict:
        return background_evidence(self.bundle, static_hotspots(self.bundle, include_members=True))

    def test_winners_join_background_clusters_and_prefer_full_evidence(self):
        section = self.section()
        label_winners = [w for w in section["winners"] if w["likelyBackground"]]
        self.assertEqual({w["frameID"] for w in label_winners}, set(range(100, 106)))
        first = label_winners[0]
        self.assertEqual(first["evidence"]["evidenceSource"], "decisionTrace")
        self.assertEqual(first["peakValue"], 241)
        self.assertEqual(first["camera"]["iso"], 800)
        self.assertEqual(len([w for w in section["winners"] if w["color"] == "blue"]), 1)

    def test_tracking_events_keep_legacy_winner_fallback_and_skip_predictions(self):
        data = {"detected": True, "predictionUsed": False,
                "candidateDecisionTrace": [trace(0, EMITTER, eligible=False), trace(1, EMITTER)],
                "tracking": {"candidateSwitch": True, "midpointDisplacement": 120.0}}
        frames = [{"frameID": 200 + i, "timestamp": i / 30, "red": copy.deepcopy(data)}
                  for i in range(3)]
        frames[1]["red"]["predictionUsed"] = True
        # A gap before the first eligible trace entry means the winner is unknown.
        frames[2]["red"]["candidateDecisionTrace"].pop(0)
        bundle = write_contexts(self.root / "events", frames, chunk=1)
        events = background_evidence(bundle, None)["events"]
        self.assertEqual(len(events), 1)
        self.assertEqual(events[0]["frameID"], 200)
        self.assertEqual(events[0]["candidateIndex"], 1)
        self.assertTrue(events[0]["candidateSwitch"])
        self.assertEqual(events[0]["midpointDisplacementPx"], 120.0)

    def test_cluster_evidence_and_exposure_split(self):
        section = self.section()
        label = next(c for c in section["clusters"] if c["likelyBackground"])
        self.assertEqual(label["framesPresent"], 12)
        self.assertEqual(label["framesWithEvidence"], 12)
        self.assertEqual(label["framesWithFullEvidence"], 6)
        self.assertEqual(label["dominantTermCounts"], {"peak": 12})
        self.assertEqual(label["coreInputsTrue"], {"coreByHighValueRatio": 6, "coreByPeakAndMean": 0,
                                                   "coreByClippedWhite": 0})
        self.assertAlmostEqual(label["emitterScoreMargin"]["median"], 0.1927)
        eligible = label["exposureByEligibility"]["eligible"]
        ineligible = label["exposureByEligibility"]["ineligible"]
        self.assertEqual((eligible["frames"], eligible["medianISO"]), (10, 800))
        self.assertEqual((ineligible["frames"], ineligible["medianISO"]), (2, 200))
        self.assertAlmostEqual(ineligible["medianExposureDurationSeconds"], 1 / 120, places=6)
        # Trace peak (241) where the label was candidate 0, inverted peakTerm (240) elsewhere.
        self.assertEqual(eligible["medianPeakValue"], 241)
        exposure = section["exposure"]
        self.assertEqual(exposure["framesWithCamera"], 12)
        self.assertEqual(exposure["iso"]["range"], [200.0, 800.0])
        self.assertEqual(exposure["exposureBiasEV"]["range"], [-0.3, 0.0])

    def test_markdown_and_json_carry_the_section(self):
        report = build_report(self.bundle)
        self.assertEqual(len(report["backgroundEvidence"]["winners"]), 13)
        self.assertNotIn("members", json.dumps(report["staticHotspots"]))  # report keeps the old shape
        text = render_markdown(report)
        section = text.split(SECTION)[1].split("\n## ")[0]
        self.assertIn("evidence only, NOT ground truth", section)
        self.assertIn("| 100 | red | red-1 **BG?** | full | 0.613 | +0.193 | peak 0.233 (38%), "
                      "highValue 0.19 (31%) | true (T/F/F) | 241 | 800 | 1/30s |", section)
        self.assertIn("red-1 **LIKELY BACKGROUND**: frames 12, with emitter evidence 12 (full 6)", section)
        self.assertIn("eligible: 10 frames (10 with camera), median ISO 800, exposure 1/30s", section)
        self.assertIn("ineligible: 2 frames (2 with camera), median ISO 200, exposure 1/120s, bias -0.3 EV, "
                      "peak 240 (2 frames) — few frames, do not read a trend", section)
        self.assertIn("- ISO 200–800 (median 800, 12 frames)", section)
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(report_tool.main([str(self.bundle), "--json"]), 0)
        data = json.loads(out.getvalue())["backgroundEvidence"]
        self.assertEqual(data["clusters"][0]["exposureByEligibility"]["ineligible"]["medianISO"], 200)

    def test_compaction_reduced_and_missing_evidence_degrade_per_field(self):
        frames = label_then_saber_frames()
        for frame in frames:
            frame.pop("camera")
            for color in ("red", "blue"):
                for entry in frame[color].get("candidateDecisionTrace", []):
                    entry.pop("emitterDiagnostics")
                for entry in (frame[color].get("candidateGeometry") or {}).get("candidates", []):
                    if entry["listIndex"] == 1:
                        entry.pop("emitter")
                        entry["emitterDiagnosticsReduced"] = True
        bundle = write_contexts(self.root / "reduced", frames, chunk=1)
        section = background_evidence(bundle, static_hotspots(bundle, include_members=True))
        self.assertFalse(section["exposure"]["available"])
        self.assertEqual(section["emitterDiagnosticsReduced"], 6)
        self.assertEqual(section["candidatesWithFullEvidence"], 0)
        label = next(c for c in section["clusters"] if c["likelyBackground"])
        self.assertIsNone(label["coreInputsTrue"])  # compact evidence only
        text = "\n".join(render_lines(section))
        self.assertIn("inputs true n/a (compact evidence only)", text)
        self.assertIn("exposure by production eligibility: n/a (no camera", text)
        self.assertIn("n/a — no frame `camera` in this bundle", text)

    def test_hotspots_unavailable_keeps_winners_without_clusters(self):
        section = background_evidence(self.bundle, None)
        self.assertEqual(len(section["winners"]), 13)
        self.assertTrue(all(w["clusterID"] is None for w in section["winners"]))
        self.assertEqual(section["clusters"], [])
        self.assertIn("static hotspots n/a", "\n".join(render_lines(section)))


class OldBundleTests(unittest.TestCase):
    def test_bundle_without_the_new_fields_is_na(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle = Path(tmp) / "phone_saber_triage_sample_session"
            write_codex_bundle(bundle)
            report = build_report(bundle)
            self.assertEqual(report["errors"], [])
            evidence = report["backgroundEvidence"]
            self.assertFalse(evidence["emitterEvidenceAvailable"])
            self.assertFalse(evidence["exposure"]["available"])
            text = render_markdown(report)
            self.assertIn(SECTION + "\n\n- **evidence only, NOT ground truth**", text)
            self.assertIn("- n/a — no `emitterDiagnostics` / geometry `emitter` / frame `camera`", text)
        self.assertEqual(render_lines(None)[-1], "- n/a")
        self.assertIn("n/a (", render_lines({"emitterEvidenceAvailable": True})[-1])  # never raises


if __name__ == "__main__":
    unittest.main()
