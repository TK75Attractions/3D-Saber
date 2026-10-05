#!/usr/bin/env python3
"""Retired shadow diagnostics must not prevent re-analysis of old bundles."""
from __future__ import annotations

import copy
import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from phone_saber_metadata_schema import validate_document
from phone_saber_session_report import build_report, render_markdown
from phone_saber_sessions_overview import build_overview, render_html, render_markdown as overview_markdown
from phone_saber_triage_codex import dry_run_text, input_plan
from test_phone_saber_candidate_geometry import block, candidate
from test_phone_saber_emitter_diagnostics import EMITTER, GEOMETRY_EMITTER, TRACE
from test_phone_saber_triage_codex import write_codex_bundle

LEGACY_KEYS = ("shadowR7e", "shadowPF22", "shadowRuleTally")
LEGACY_EMITTER = {"shadowR7e": {"applied": False, "d240": 2.95, "shadowR7eEligible": False},
                  "shadowPF22": {"applied": False, "shadowPF22Eligible": True}}
LEGACY_TALLY = {"formatVersion": 1, "applied": False, "rules": ["r7e", "pf22"],
                "totalFrames": 300, "total": {"frames": 300}, "byLabel": {}, "byExposure": {}}


def snapshot(root):
    return {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in root.rglob("*") if p.is_file()}


class LegacyDiagnosticsTests(unittest.TestCase):
    def test_schema_ignores_legacy_keys_without_validating_their_payloads(self):
        document = {"formatVersion": 1, "frames": [{"candidateDiagnostics": {"red": {
            "topCandidates": [{"emitterDiagnostics": copy.deepcopy(EMITTER)}]}}}]}
        baseline = validate_document(document)
        # Retired payloads have no effect, even when incomplete or of an unknown shape.
        for payload in (LEGACY_EMITTER, {"shadowR7e": "retired", "shadowPF22": None}):
            legacy = copy.deepcopy(document)
            legacy["shadowRuleTally"] = copy.deepcopy(LEGACY_TALLY)
            legacy["frames"][0]["candidateDiagnostics"]["red"]["topCandidates"][0][
                "emitterDiagnostics"].update(payload)
            validated = validate_document(legacy)
            self.assertEqual(validated.document, baseline.document)
            self.assertEqual(validated.report.warnings, baseline.report.warnings)

    def test_old_bundle_contract_report_and_overview_ignore_legacy_fields(self):
        with tempfile.TemporaryDirectory() as directory:
            inbox = Path(directory)
            bundle = inbox / "phone_saber_triage_phonesaber_20261004_232850_471"
            write_codex_bundle(bundle)
            context_path = bundle / "frames/frame_100_1.json"
            context = json.loads(context_path.read_text())
            red = context["frames"][0]["red"]
            red.update(detected=True, predictionUsed=False, eligibleCandidateCount=1,
                       selectedCandidateIndex=0,
                       tracking={"candidateSwitch": True, "midpointDisplacement": 150.0,
                                 "candidateMatchConfidence": "geometryMatch", "endpointPathChanged": False,
                                 "detectedToggle": False, "stageDiscontinuities": {}, "instabilityScore": 1.0})
            red["candidateDecisionTrace"] = [dict(TRACE, emitterDiagnostics=copy.deepcopy(EMITTER))]
            geometry_candidate = candidate(0, rank=1)
            geometry_candidate["emitter"] = copy.deepcopy(GEOMETRY_EMITTER)
            red["candidateGeometry"] = block([geometry_candidate])
            context_path.write_text(json.dumps(context))
            baseline_plan = input_plan(bundle)
            baseline_report = build_report(bundle, margins=(), holds=())
            baseline_overview = build_overview(inbox)
            self.assertEqual(baseline_report["inputContract"], "PASS")
            self.assertEqual(baseline_report["errors"], [])
            summary_path = bundle / "summary.json"
            summary = json.loads(summary_path.read_text())
            summary["shadowRuleTally"] = copy.deepcopy(LEGACY_TALLY)
            summary_path.write_text(json.dumps(summary))
            red["candidateDecisionTrace"][0]["emitterDiagnostics"].update(LEGACY_EMITTER)
            geometry_candidate["emitter"].update(LEGACY_EMITTER)
            context_path.write_text(json.dumps(context))
            before = snapshot(bundle)
            self.assertEqual(input_plan(bundle), baseline_plan)
            self.assertIn("DRY RUN", dry_run_text(bundle))
            report = build_report(bundle, margins=(), holds=())
            self.assertEqual(report, baseline_report)
            overview = build_overview(inbox)
            self.assertEqual(overview["sessions"], baseline_overview["sessions"])
            self.assertEqual(overview["totals"], baseline_overview["totals"])
            selected = overview["sessions"][0]["selectedFrames"]
            self.assertEqual(selected["jumpsAtLeast100px"], 1)
            self.assertEqual(selected["candidateSwitch"], 1)
            rendered = "\n".join((render_markdown(report), overview_markdown(overview), render_html(overview),
                                  json.dumps(report), json.dumps(overview)))
            for key in (*LEGACY_KEYS, "R7e", "PF22"):
                self.assertNotIn(key, rendered)
            self.assertEqual(snapshot(bundle), before)


if __name__ == "__main__":
    unittest.main()


class WarmNoDeepRedDiagnosticsTests(unittest.TestCase):
    def test_production_warm_no_deep_red_verdict_is_accepted_in_both_encodings(self):
        from phone_saber_tracking_diagnostics import validate_emitter_diagnostics, BundleError
        base = {"emitterScore": 0.8, "emitterScoreMargin": 0.38, "hasEmitterCore": True}
        kept = {"applied": True, "deepCount": 29, "warmCount": 105, "pixelCount": 351,
                "warmFrac": 0.299, "rejected": False}
        validate_emitter_diagnostics({**base, "warmNoDeepRed": kept})
        validate_emitter_diagnostics({**base, "warmNoDeepRed": {**kept, "rejectionReason": None}})
        validate_emitter_diagnostics({**base, "warmNoDeepRed": {
            **kept, "deepCount": 0, "warmFrac": 0.9, "warmCount": 316, "rejected": True,
            "rejectionReason": "warmNoDeepRed"}})
        for broken in ({**kept, "rejected": True}, {**kept, "warmCount": 400}, {**kept, "extra": 1}):
            with self.assertRaises(BundleError):
                validate_emitter_diagnostics({**base, "warmNoDeepRed": broken})
