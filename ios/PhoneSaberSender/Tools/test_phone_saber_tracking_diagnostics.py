"""Synthetic tracking bundles test contracts and conservative repair decisions."""
from __future__ import annotations

import copy
import contextlib
import io
import json
from pathlib import Path
import tempfile
import unittest

from phone_saber_triage_protocol import BundleError, pack_bundle, receive_bundle
from phone_saber_triage_codex import (input_plan, analyze_bundle, _codex_prompt,
                                     ANALYSIS_MODEL, ANALYSIS_REASONING_EFFORT, _needs_second_opinion)
from phone_saber_tracking_diagnostics import temporal_events, sufficient_temporal, validate_compound
from phone_saber_auto_repair import repair_gate, REPO_ROOT
from test_phone_saber_triage_codex import write_codex_bundle, fake_codex, EMPTY_ANALYSIS


def endpoint(x: int = 0, length: int = 100) -> dict:
    return {"first": {"x": x, "y": 20}, "second": {"x": x + length, "y": 20}}


def write_tracking_bundle(bundle: Path, count: int = 11) -> None:
    write_codex_bundle(bundle, image_count=count,
                       failure_types=("motion_event_1000000000_tracking_instability",) * count)
    summary = json.loads((bundle / "summary.json").read_text())
    summary["motionEventSummary"] = {"events": [[1000000000, "red", 4.0, "selected"]]}
    peak = count // 2
    for index, image in enumerate(summary["images"]):
        role = "peak" if index == peak else "onset" if index == peak - 1 else \
            "before" if index < peak else "recovery" if index == peak + 5 else "after"
        signals = [{"kind": "tracking_instability", "color": "red", "value": 4.0,
                    "threshold": 1.0, "score": 4.0}]
        image.update(sessionID="sample_session", color="red", eventIndex=1000000000,
                     role=role, anomalyScore=4.0, signals=signals, signalAggregation="event_max_per_kind")
        candidate = {"index": 0, "sourceType": "color-mask", "finalScore": 80.0,
                     "centroid": [50.0, 20.0], "bbox": [0, 18, 100, 22],
                     "componentArea": 300, "pointCount": 75, "peakValue": 255,
                     "meanValue": 230.0, "highValueRatio": 0.8, "meanColorPurity": 0.8,
                     "clippedWhiteRatio": 0.1, "continuity": 1.0, "density": 3.0, "maxGap": 0,
                     "endpointPipeline": {"coordinateSpace": "sourceImagePixels", "rawPCA": endpoint(),
                         "robustInterval": endpoint(), "body": endpoint(length=180 if index == peak else 100),
                         "finalSelected": endpoint(length=180 if index == peak else 100),
                         "endpointSource": "bodyPCA", "bodyAdopted": True,
                         "robustIntervalAdopted": False, "gatingValues": {"retainedBodyRatio": 0.6}}}
        tracking = {"midpoint": [50.0, 20.0], "segmentLength": 180.0 if index == peak else 100.0,
                    "orientationRadians": 0.0, "candidateSwitch": False,
                    "candidateMatchConfidence": "geometryMatch", "endpointPathChanged": False,
                    "detectedToggle": False, "stageDiscontinuities": {"rawPCA": 0.0, "robustInterval": 0.0,
                        "body": 4.0 if index == peak else 0.0, "finalSelected": 4.0 if index == peak else 0.0},
                    "instabilityScore": 4.0 if index == peak else 0.0}
        mapping = {"sessionID": "sample_session", "frameID": image["frameID"], "timestamp": image["timestamp"],
                   "color": "red", "image": image["path"], "eventID": "1000000000", "eventRole": role}
        context = {"sessionID": "sample_session", "selectedFrameID": image["frameID"], "selectedColor": "red",
                   "selectedFailureType": image["failureType"], "selectedReasons": ["synthetic tracking test"],
                   "contextRadiusFrames": 2, "imageMapping": mapping,
                   "motionEvent": {"eventIndex": image["eventIndex"], "role": role, "score": 4.0,
                       "signals": signals, "signalAggregation": "event_max_per_kind"},
                   "frames": [{"frameID": image["frameID"], "timestamp": image["timestamp"],
                       "red": {"detected": True, "detectionSucceeded": True, "candidateCount": 1,
                               "eligibleCandidateCount": 1, "tracking": tracking, "selectedCandidate": candidate,
                               "endpoint": [0, 20, 180 if index == peak else 100, 20]},
                       "blue": {"detected": False}}],
                   "udpTransmissions": [{"frameID": image["frameID"], "color": "red", "endpoint": [0, 20, 100, 20],
                        "sourceEndpoint": [0, 20, 100, 20], "coordinateSpace": "configuredUDPOutputPixels",
                        "state": "sendStarted", "hostTimestamp": float(index)}]}
        (bundle / image["frameContextPath"]).write_text(json.dumps(context))
    (bundle / "summary.json").write_text(json.dumps(summary))


def tracking_analysis(count: int = 11, *, actionable: bool = True) -> dict:
    analysis = copy.deepcopy(EMPTY_ANALYSIS)
    ids = [f"image_{i + 1:03d}" for i in range(count)]
    analysis["repair_assessment"].update(decision="actionable" if actionable else "needs_capture",
        visible_saber_confirmed=True, production_change_supported=actionable,
        root_cause_stage="endpoint", diagnosis_consistent_with_metadata=True,
        change_type="endpoint_logic", independent_visual_examples=1, affected_colors=["RED"],
        evidence_image_ids=ids, reason="Body PCA change consistent with images." if actionable else "Uncertain which body rule; numeric evidence is available.")
    analysis["tracking_assessment"] = {"symptom_confirmed_in_images": True,
        "first_unstable_stage": "robust_body", "temporal_image_ids": ids,
        "concrete_cause": "Synthetic discontinuous body PCA", "concrete_production_change": "Synthetic body logic proposal",
        "expected_effect": "Keep observed body geometry", "regression_risk": "Point LEDs may use fallback"}
    analysis["wrong_candidate_and_endpoint_errors"] = [{"classification": ["D"], "color": "RED",
        "frame_ids": list(range(100, 100 + count)), "image_ids": ids, "issue_type": "tracking instability",
        "observation": "Synthetic test of visible geometry confirmation", "interpretation": "Body stage",
        "confidence": "high"}]
    return analysis


class TrackingDiagnosticTests(unittest.TestCase):
    def test_eleven_images_are_ordered_mapped_and_transported(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); bundle = root / "bundle"
            write_tracking_bundle(bundle)
            plan = input_plan(bundle)
            event = temporal_events(plan)[0]
            self.assertEqual([f["frameID"] for f in event["frames"]], list(range(100, 111)))
            self.assertEqual(event["centerFrameID"], 105)
            self.assertTrue(sufficient_temporal(event))
            self.assertTrue(all(f["transmissionEvidence"] == "observedSendStart" for f in event["frames"]))
            prompt = _codex_prompt(plan.session_id, plan.images, plan.root)
            self.assertIn("detected=true", prompt)
            self.assertIn("rawPCA", prompt)
            self.assertIn("Frame: 105 -> images/image_06.png", prompt)
            envelope = pack_bundle(bundle, root / "tracking.psbt")
            # Verify the actual envelope against the receiver contract.
            with (root / "tracking.psbt").open("rb") as source:
                received = receive_bundle(source, inbox=root / "inbox", content_length=(root / "tracking.psbt").stat().st_size)
            self.assertEqual(len(input_plan(received).images), 11)

    def test_mapping_mismatch_and_missing_mapping_fail_closed(self):
        for field in ("frameID", "timestamp", "image", "sessionID", "eventRole", "missing"):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as temporary:
                bundle = Path(temporary) / "bundle"; write_tracking_bundle(bundle)
                context_path = bundle / "frames/frame_100_1.json"
                context = json.loads(context_path.read_text())
                if field == "missing": context.pop("imageMapping")
                else: context["imageMapping"][field] = "wrong"
                context_path.write_text(json.dumps(context))
                with self.assertRaises(BundleError): input_plan(bundle)

    def test_tracking_gate_accepts_detected_true_with_complete_evidence_and_keeps_safety(self):
        with tempfile.TemporaryDirectory() as temporary:
            bundle = Path(temporary) / "bundle"; write_tracking_bundle(bundle)
            plan = input_plan(bundle)
            report = {"formatVersion": 3, "analysis": tracking_analysis(), "analysisModel": ANALYSIS_MODEL,
                      "analysisReasoningEffort": ANALYSIS_REASONING_EFFORT, "analysisExecuted": True}
            self.assertEqual(repair_gate(report, plan, REPO_ROOT)["decision"], "actionable")
            for field, code in (("symptom_confirmed_in_images", "visualEvidenceMissing"),
                                ("concrete_production_change", "noConcreteRepairProposed"),
                                ("first_unstable_stage", "rootCauseStageUnknown")):
                changed = copy.deepcopy(report)
                changed["analysis"]["tracking_assessment"][field] = False if field.startswith("symptom") else "unknown" if field.startswith("first") else ""
                gate = repair_gate(changed, plan, REPO_ROOT)
                self.assertEqual(gate["decision"], "needs_capture")
                self.assertIn(code, gate["reasonCodes"])
            changed = copy.deepcopy(report); changed["analysis"]["tracking_assessment"]["first_unstable_stage"] = "downstream"
            self.assertIn("productionChangeUnsupported", repair_gate(changed, plan, REPO_ROOT)["reasonCodes"])

    def test_temporal_evidence_missing_or_truncated_blocks_repair(self):
        with tempfile.TemporaryDirectory() as temporary:
            bundle = Path(temporary) / "bundle"; write_tracking_bundle(bundle, count=2)
            plan = input_plan(bundle)
            report = {"formatVersion": 3, "analysis": tracking_analysis(count=2), "analysisModel": ANALYSIS_MODEL,
                      "analysisReasoningEffort": ANALYSIS_REASONING_EFFORT, "analysisExecuted": True}
            gate = repair_gate(report, plan, REPO_ROOT)
            self.assertEqual(gate["decision"], "needs_capture")
            self.assertTrue({"temporalEvidenceMissing", "insufficientCandidateHistory", "insufficientEndpointHistory"} <= set(gate["reasonCodes"]))

    def test_luna_reanalysis_once_uses_existing_timeline(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); bundle = root / "bundle"; write_tracking_bundle(bundle)
            initial = tracking_analysis(actionable=False)
            initial.pop("tracking_assessment")
            initial["repair_assessment"]["reason"] = "Missing temporal evidence and endpoint history."
            codex = fake_codex(root, root / "spy.json", responses=[initial, tracking_analysis()])
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                analyze_bundle(bundle, codex_path=str(codex), source="manual_retry")
            for phase in ("ANALYSIS", "REANALYSIS"):
                lines = [line for line in output.getvalue().splitlines()
                         if f"[AUTO_REPAIR][{phase}]" in line]
                self.assertTrue(lines, f"missing {phase}")
                for line in lines:
                    self.assertIn("sessionID=sample_session source=manual_retry", line)
            calls = json.loads((root / "spy.json").read_text())["calls"]
            self.assertEqual(len(calls), 2)
            self.assertEqual([c["model"] for c in calls], ["gpt-6-luna", "gpt-6-luna"])
            self.assertIn("trackingEvents", calls[1]["prompt"])
            self.assertTrue(all(c["sandbox"] == "read-only" for c in calls))

    def test_missing_temporal_evidence_does_not_trigger_reanalysis_from_rejection_trace(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); bundle = root / "bundle"; write_tracking_bundle(bundle, count=2)
            initial = tracking_analysis(count=2, actionable=False)
            initial["repair_assessment"]["reason"] = "Missing temporal evidence and endpoint history."
            codex = fake_codex(root, root / "spy.json", analysis=initial)
            result = analyze_bundle(bundle, codex_path=str(codex))
            self.assertEqual(result["status"], "precheck_failed")
            self.assertIn("temporalEvidenceMissing", result["reasonCodes"])
            self.assertFalse((root / "spy.json").exists())

    def test_sol_escalation_requires_visual_temporal_confirmation(self):
        for visible in (True, False):
            with self.subTest(visible=visible), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary); bundle = root / "bundle"; write_tracking_bundle(bundle)
                initial = tracking_analysis(actionable=False)
                initial["tracking_assessment"]["symptom_confirmed_in_images"] = visible
                codex = fake_codex(root, root / "spy.json", responses=[initial, tracking_analysis()])
                output = io.StringIO()
                with contextlib.redirect_stdout(output):
                    analyze_bundle(bundle, codex_path=str(codex), source="new_upload")
                lines = [line for line in output.getvalue().splitlines()
                         if "[AUTO_REPAIR][SECOND_OPINION]" in line]
                self.assertEqual(bool(lines), visible)
                for line in lines:
                    self.assertIn("sessionID=sample_session source=new_upload", line)
                calls = json.loads((root / "spy.json").read_text())["calls"]
                self.assertEqual([c["model"] for c in calls], ["gpt-6-luna", "gpt-6-sol"] if visible else ["gpt-6-luna"])
                self.assertTrue(all(c["sandbox"] == "read-only" for c in calls))

    def test_dropouts_use_existing_gate_with_incidental_temporal_images(self):
        with tempfile.TemporaryDirectory() as temporary:
            bundle = Path(temporary) / "bundle"; write_tracking_bundle(bundle)
            for path in (bundle / "frames").glob("*.json"):
                context = json.loads(path.read_text())
                red = context["frames"][0]["red"]
                red["tracking"]["stageDiscontinuities"] = {}
                red["tracking"]["instabilityScore"] = 0.0
                if context["motionEvent"]["role"] == "peak":
                    red["detected"] = red["detectionSucceeded"] = False
                    red.pop("selectedCandidate")
                    red["eligibleCandidateCount"] = 0
                    red["tracking"]["detectedToggle"] = True
                    red["tracking"]["instabilityScore"] = 2.0
                path.write_text(json.dumps(context))
            plan = input_plan(bundle)
            analysis = tracking_analysis()
            analysis.pop("tracking_assessment")
            analysis["repair_assessment"].update(root_cause_stage="eligibility", change_type="candidate_logic")
            analysis["false_negatives"] = analysis["wrong_candidate_and_endpoint_errors"]
            analysis["wrong_candidate_and_endpoint_errors"] = []
            analysis["false_negatives"][0]["classification"] = ["C"]
            report = {"formatVersion": 3, "analysis": analysis, "analysisModel": ANALYSIS_MODEL,
                      "analysisReasoningEffort": ANALYSIS_REASONING_EFFORT, "analysisExecuted": True}
            self.assertEqual(repair_gate(report, plan, REPO_ROOT)["decision"], "actionable")

    def test_compound_numeric_evidence_can_reach_sol_for_eligibility(self):
        analysis = tracking_analysis(actionable=False)
        analysis.pop("tracking_assessment")
        analysis["wrong_candidate_and_endpoint_errors"] = []
        analysis["repair_assessment"].update(root_cause_stage="eligibility", reason="Uncertain which compound logic to change.")
        traces = [{"color": "red", "rejectionRule": "core-line-weak-bridge", "conditions": [
            {"condition": "bodyPurity", "value": 0.625, "comparison": ">=", "threshold": 0.495, "satisfied": True}]}]
        self.assertTrue(_needs_second_opinion(analysis, traces))
        analysis["repair_assessment"]["visible_saber_confirmed"] = False
        self.assertFalse(_needs_second_opinion(analysis, traces))

    def test_tracking_role_and_signal_cannot_be_disguised_as_another_failure_type(self):
        with tempfile.TemporaryDirectory() as temporary:
            bundle = Path(temporary) / "bundle"; write_tracking_bundle(bundle)
            summary = json.loads((bundle / "summary.json").read_text())
            summary["images"][0]["failureType"] = "motion_event_1000000000_dropout"
            (bundle / "summary.json").write_text(json.dumps(summary))
            with self.assertRaisesRegex(BundleError, "disagree"):
                input_plan(bundle)

    def test_compound_conditions_use_comparator_and_satisfaction(self):
        conditions = [{"condition": "span", "value": 78.3, "comparison": ">=", "threshold": 32.4, "satisfied": True},
                      {"condition": "retainedBody", "value": 0.56, "comparison": "<", "threshold": 0.65, "satisfied": True},
                      {"condition": "bodyPurity", "value": 0.625, "comparison": ">=", "threshold": 0.495, "satisfied": True}]
        group = [{"rejectionRule": "core-line-weak-bridge", "conditions": conditions}]
        validate_compound(group)
        group[0]["conditions"][2]["satisfied"] = False
        with self.assertRaisesRegex(BundleError, "satisfaction"): validate_compound(group)


if __name__ == "__main__":
    unittest.main()
