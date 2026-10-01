"""Preflight failures make zero LLM calls and preserve selected evidence."""
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import phone_saber_triage_codex as codex
import phone_saber_auto_repair as repair
from phone_saber_tracking_diagnostics import PrecheckFailed, tracking_preflight, tracking_summary
from test_phone_saber_tracking_diagnostics import write_tracking_bundle, tracking_analysis
from test_phone_saber_triage_codex import write_codex_bundle, fake_codex, EMPTY_ANALYSIS


class TrackingPreflightTests(unittest.TestCase):
    def test_missing_history_stops_before_model_discovery_and_gate(self):
        for field, code in (("selectedCandidate", "candidateHistoryMissing"),
                            ("endpoint", "endpointHistoryMissing"),
                            ("tracking", "trackingTimelineMissing")):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as tmp:
                bundle = Path(tmp) / "bundle"; write_tracking_bundle(bundle)
                context_path = bundle / "frames/frame_105_6.json"
                context = json.loads(context_path.read_text())
                context["frames"][0]["red"].pop(field)
                context_path.write_text(json.dumps(context))
                with mock.patch.object(codex, "find_codex_binary") as discover:
                    result = codex.analyze_bundle(bundle)
                discover.assert_not_called()
                self.assertEqual(result["status"], "precheck_failed")
                self.assertIn(code, result["reasonCodes"])
                plan = codex.input_plan(bundle, allow_reports=True)
                report = repair.load_analysis(bundle, plan)
                self.assertFalse(report["analysisExecuted"])
                gate = repair.repair_gate(report, plan, repair.REPO_ROOT)
                self.assertEqual(gate["decision"], "needs_capture")
                self.assertIn(code, gate["reasonCodes"])

    def test_invalid_input_has_explicit_codes_and_never_substitutes(self):
        for mutation, expected in (("image", "imageFileMissing"), ("mapping", "frameMappingMissing"),
                ("timestamp", "timestampMissing"), ("event", "temporalEvidenceMissing"),
                ("duplicate", "frameMappingMissing")):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as tmp:
                bundle = Path(tmp) / "bundle"; write_tracking_bundle(bundle)
                summary_path = bundle / "summary.json"
                summary = json.loads(summary_path.read_text())
                image = summary["images"][5]
                context_path = bundle / image["frameContextPath"]
                context = json.loads(context_path.read_text())
                if mutation == "image": (bundle / image["path"]).unlink()
                if mutation == "mapping": context["imageMapping"]["frameID"] = 104
                if mutation == "timestamp": context["frames"][0].pop("timestamp")
                if mutation == "event": image.pop("eventIndex")
                if mutation == "duplicate": summary["images"][6]["path"] = image["path"]
                context_path.write_text(json.dumps(context)); summary_path.write_text(json.dumps(summary))
                before = {p.relative_to(bundle): p.read_bytes() for p in bundle.rglob("*") if p.is_file()}
                output = io.StringIO()
                with mock.patch.object(codex, "find_codex_binary") as discover, contextlib.redirect_stdout(output):
                    with self.assertRaises(PrecheckFailed) as failure: codex.analyze_bundle(bundle)
                discover.assert_not_called()
                self.assertIn(expected, failure.exception.reason_codes)
                self.assertIn("PRECHECK_FAILED", output.getvalue())
                self.assertEqual(before, {p.relative_to(bundle): p.read_bytes() for p in bundle.rglob("*") if p.is_file()})

    def test_order_and_center_checked_before_timeline_sorting(self):
        for mutation, code in (("reorder", "temporalOrderInvalid"), ("peak", "eventCenterMissing"),
                               ("gap", "temporalEvidenceMissing")):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as tmp:
                bundle = Path(tmp) / "bundle"; write_tracking_bundle(bundle)
                summary_path = bundle / "summary.json"; summary = json.loads(summary_path.read_text())
                if mutation == "reorder": summary["images"][0:2] = reversed(summary["images"][0:2])
                elif mutation == "peak":
                    image = summary["images"][5]; image["role"] = "after"
                    p = bundle / image["frameContextPath"]; c = json.loads(p.read_text())
                    c["motionEvent"]["role"] = c["imageMapping"]["eventRole"] = "after"; p.write_text(json.dumps(c))
                else:
                    image = summary["images"].pop(2); summary["selectedImageCount"] -= 1
                    (bundle / image["path"]).unlink(); (bundle / image["frameContextPath"]).unlink()
                summary_path.write_text(json.dumps(summary))
                plan = codex.input_plan(bundle)
                self.assertIn(code, tracking_preflight(plan)["reasonCodes"])
                with mock.patch.object(codex, "find_codex_binary") as discover: result = codex.analyze_bundle(bundle)
                discover.assert_not_called(); self.assertEqual(result["status"], "precheck_failed")

    def test_eligibility_only_and_legacy_inputs_keep_analysis(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); bundle = root / "bundle"; write_codex_bundle(bundle)
            binary = fake_codex(root, root / "spy.json", analysis=EMPTY_ANALYSIS)
            self.assertEqual(codex.analyze_bundle(bundle, codex_path=str(binary))["status"], "completed")
        for count in (1, 2, 11):
            with self.subTest(count=count), tempfile.TemporaryDirectory() as tmp:
                bundle = Path(tmp) / "bundle"; write_tracking_bundle(bundle, count=count)
                for p in (bundle / "frames").glob("*.json"):
                    c = json.loads(p.read_text()); red = c["frames"][0]["red"]
                    red.pop("tracking")
                    if c["motionEvent"]["role"] == "peak":
                        red["detected"] = red["detectionSucceeded"] = False
                        red.pop("selectedCandidate"); red.pop("endpoint")
                    p.write_text(json.dumps(c))
                result = tracking_preflight(codex.input_plan(bundle))
                self.assertEqual(result["status"], "PASS")
                self.assertEqual(result["eventModes"][0]["mode"], "eligibility-dropout")

    def test_summary_uses_readable_stages_and_actual_gate(self):
        with tempfile.TemporaryDirectory() as tmp:
            bundle = Path(tmp) / "bundle"; write_tracking_bundle(bundle)
            plan = codex.input_plan(bundle)
            for stage, label in (("candidate_selection", "candidate-selection"), ("PCA", "raw-pca"),
                    ("robust_body", "body-endpoint"), ("endpoint_selection", "final-selection"),
                    ("downstream", "downstream"), ("unknown", "unknown")):
                a = tracking_analysis(); a["tracking_assessment"]["first_unstable_stage"] = stage
                report = {"analysis": a, "analysisExecuted": True, "analysisReanalysisExecuted": True,
                          "analysisEscalationExecuted": True}
                row = tracking_summary(plan, report, {"decision": "needs_capture", "reasonCodes": ["test"]})[0]
                self.assertEqual(row["firstUnstableStage"], label)
                self.assertEqual(row["gateResult"], "needs_capture")
                self.assertTrue(row["reanalysisExecuted"] and row["solEscalationExecuted"])
                self.assertEqual(row["temporalEvidence"], "complete")


if __name__ == "__main__": unittest.main()
