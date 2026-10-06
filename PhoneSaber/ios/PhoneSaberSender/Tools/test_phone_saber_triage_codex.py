#!/usr/bin/env python3
"""Tests for limited Codex CLI input and failure isolation."""

from __future__ import annotations

import contextlib
import copy
import io
import json
import os
import sys
import tempfile
import unittest
from http.client import HTTPConnection
from pathlib import Path
from threading import Thread
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))

from phone_saber_triage_codex import (
    ANALYSIS_MODEL,
    ANALYSIS_REASONING_EFFORT,
    CodexFailed,
    CodexModelUnavailable,
    CodexUnavailable,
    analyze_bundle,
    dry_run_text,
    input_plan,
    _output_schema,
    _codex_prompt,
    _decision_trace_summary,
)
from phone_saber_triage_protocol import BundleError
from phone_saber_triage_protocol import CONTENT_TYPE, pack_bundle
from phone_saber_triage_receiver import TriageHTTPServer
from phone_saber_auto_repair import repair_bundle, repair_gate
from test_phone_saber_triage_protocol import write_bundle
from phone_saber_test_isolation import isolate_codex_logs as setUpModule  # noqa: F401,E402
from phone_saber_test_isolation import restore_codex_logs as tearDownModule  # noqa: F401,E402


EMPTY_ANALYSIS = {
    "session_summary": "One selected image reviewed.",
    "false_negatives": [],
    "wrong_candidate_and_endpoint_errors": [],
    "false_positive_suspects": [],
    "other_findings": [],
    "limitations": [],
    "repair_assessment": {
        "decision": "needs_capture", "visible_saber_confirmed": False,
        "production_change_supported": False, "root_cause_stage": "unknown",
        "diagnosis_consistent_with_metadata": False, "change_type": "none",
        "independent_visual_examples": 0, "affected_colors": [],
        "evidence_image_ids": [], "reason": "Insufficient visual evidence.",
    },
}
FOUR_IMAGE_IDS = ["image_001", "image_002", "image_003", "image_004"]
EXPECTED_SUMMARY_SCOPE = "retained incident candidates and nearby context"


class CodexTriageTests(unittest.TestCase):
    def test_motion_event_roles_and_scores_reach_luna_without_repair(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_codex_bundle(bundle, image_count=3,
                               failure_types=("motion_event_0_dropout",) * 3)
            summary_path = bundle / "summary.json"
            summary = json.loads(summary_path.read_text())
            summary["motionEventSummary"] = {"events": [[0, "red", 2.0, "selected"]],
                "signalDistributions": {"dropout": {"count": 1,
                    "meanScore": 2.0, "maxScore": 2.0}}}
            signals = [{"kind": "dropout", "color": "red", "value": 4.0,
                        "threshold": 3.0, "score": 2.0}]
            for index, role in enumerate(("event_pre", "event_at", "event_post")):
                entry = summary["images"][index]
                entry.update(eventIndex=0, role=role, anomalyScore=2.0,
                             signals=signals, signalAggregation="event_max_per_kind")
                context_path = bundle / entry["frameContextPath"]
                context = json.loads(context_path.read_text())
                context["motionEvent"] = {"eventIndex": 0, "role": role,
                                          "score": 2.0, "signals": signals,
                                          "signalAggregation": "event_max_per_kind"}
                context["frames"][0]["processingTimeSeconds"] = 0.01
                context_path.write_text(json.dumps(context))
            summary_path.write_text(json.dumps(summary))
            plan = input_plan(bundle)
            self.assertEqual(len(plan.image_paths), 3)
            codex = fake_codex(root, root / "spy.json", analysis=EMPTY_ANALYSIS)
            analyze_bundle(bundle, codex_path=str(codex))
            spy = json.loads((root / "spy.json").read_text())
            self.assertIn("event_pre", (bundle / summary["images"][0][
                "frameContextPath"]).read_text())
            self.assertIn("frame gap, processing delay", spy["prompt"])
            report = json.loads((bundle / "analysis_report.json").read_text())
            self.assertFalse(report["analysisEscalationExecuted"])
            with patch("phone_saber_auto_repair._corpus_coverage",
                       return_value=(True, "covered")):
                gate = repair_gate(report, input_plan(bundle, allow_reports=True), root)
            self.assertEqual(gate["decision"], "needs_capture")

    def test_motion_event_summary_context_mismatch_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle)
            summary_path = bundle / "summary.json"
            summary = json.loads(summary_path.read_text())
            image = summary["images"][0]
            image.update(failureType="motion_event_0_dropout", eventIndex=0,
                         role="event_at", anomalyScore=2.0,
                         signals=[{"kind": "dropout", "color": "red", "value": 4.0,
                                   "threshold": 3.0, "score": 2.0}],
                         signalAggregation="event_max_per_kind")
            summary["motionEventSummary"] = {"events": [[0, "red", 2.0, "selected"]]}
            summary_path.write_text(json.dumps(summary))
            with self.assertRaisesRegex(BundleError, "summary and context disagree"):
                input_plan(bundle)

    def test_eligibility_trace_reaches_luna_and_repeats_across_frames(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_codex_bundle(bundle)
            add_synthetic_eligibility_trace(bundle)
            plan = input_plan(bundle)
            traces = _decision_trace_summary(plan)
            self.assertEqual([item["frameID"] for item in traces], [100, 101])
            self.assertEqual([item["failedRule"] for item in traces],
                             ["peakValue", "peakValue"])
            codex = fake_codex(root, root / "spy.json")
            analyze_bundle(bundle, codex_path=str(codex))
            spy = json.loads((root / "spy.json").read_text())
            self.assertIn("candidateDecisionTrace", json.loads(
                (bundle / "frames/frame_100_1.json").read_text())["frames"][0]["red"])
            self.assertIn("eligibility dropouts", spy["prompt"])

    def test_untrusted_rule_shape_is_rejected_before_analysis(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle)
            add_synthetic_eligibility_trace(bundle)
            path = bundle / "frames/frame_100_1.json"
            context = json.loads(path.read_text())
            context["frames"][0]["red"]["candidateDecisionTrace"][0]["rules"][0][
                "comparison"] = "execute"
            path.write_text(json.dumps(context))
            with self.assertRaises(BundleError):
                input_plan(bundle)

    def test_missing_rejection_reason_reanalyzes_once_then_stops(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_codex_bundle(bundle)
            add_synthetic_eligibility_trace(bundle)
            missing = eligibility_assessment("needs_capture", "Missing rejection reason")
            actionable = eligibility_assessment("actionable", "Two visible examples support change")
            actionable["repair_assessment"].update({
                "production_change_supported": True, "change_type": "candidate_logic",
                "independent_visual_examples": 2})
            codex = fake_codex(root, root / "spy.json", responses=[missing, actionable])
            analyze_bundle(bundle, codex_path=str(codex))
            spy = json.loads((root / "spy.json").read_text())
            report = json.loads((bundle / "analysis_report.json").read_text())
            self.assertEqual(len(spy["calls"]), 2)
            self.assertEqual([call["model"] for call in spy["calls"]],
                             [ANALYSIS_MODEL, ANALYSIS_MODEL])
            self.assertTrue(report["analysisReanalysisExecuted"])
            self.assertFalse(report["analysisEscalationExecuted"])
            self.assertEqual(report["analysis"]["repair_assessment"]["decision"], "actionable")
            with patch("phone_saber_auto_repair._corpus_coverage",
                       return_value=(True, "covered")):
                gate = repair_gate(report, input_plan(bundle, allow_reports=True), root)
            self.assertEqual(gate["decision"], "actionable")

    def test_missing_trace_requires_capture_without_a_second_call(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_codex_bundle(bundle)
            missing = eligibility_assessment("needs_capture", "Missing rejection details")
            codex = fake_codex(root, root / "spy.json", responses=[missing])
            analyze_bundle(bundle, codex_path=str(codex))
            spy = json.loads((root / "spy.json").read_text())
            report = json.loads((bundle / "analysis_report.json").read_text())
            self.assertEqual(len(spy["calls"]), 1)
            self.assertFalse(report["analysisReanalysisExecuted"])
            self.assertFalse(report["analysisEscalationExecuted"])

    def test_reanalysis_never_repeats_when_reason_is_still_missing(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_codex_bundle(bundle)
            add_synthetic_eligibility_trace(bundle)
            missing = eligibility_assessment("needs_capture", "Missing rejection details")
            codex = fake_codex(root, root / "spy.json", responses=[missing, missing])
            analyze_bundle(bundle, codex_path=str(codex))
            spy = json.loads((root / "spy.json").read_text())
            report = json.loads((bundle / "analysis_report.json").read_text())
            self.assertEqual(len(spy["calls"]), 2)
            self.assertTrue(report["analysisReanalysisExecuted"])
            self.assertFalse(report["analysisEscalationExecuted"])

    def test_unconfirmed_red_dropout_stops_after_diagnostics_reanalysis(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_codex_bundle(bundle, image_count=3,
                               failure_types=("dropout", "dropout", "core_line_tail"))
            add_synthetic_eligibility_trace(bundle)
            missing = eligibility_assessment("needs_capture", "Missing rejection details")
            final = eligibility_assessment("needs_capture",
                "Image 003 confirms a visible red blade, but the RED dropout PNGs "
                "show only a compact glow rather than a discernible blade. The selected "
                "pixels do not provide independent visual examples of a real red-blade "
                "dropout, so capture frames showing the blade during rejection.")
            final["repair_assessment"]["evidence_image_ids"] = [
                "image_001", "image_002", "image_003"]
            final["false_negatives"] = []
            codex = fake_codex(root, root / "spy.json", responses=[missing, final])
            analyze_bundle(bundle, codex_path=str(codex))
            spy = json.loads((root / "spy.json").read_text())
            report = json.loads((bundle / "analysis_report.json").read_text())
            self.assertEqual(len(spy["calls"]), 2)
            self.assertEqual([call["model"] for call in spy["calls"]],
                             [ANALYSIS_MODEL, ANALYSIS_MODEL])
            self.assertTrue(report["analysisReanalysisExecuted"])
            self.assertFalse(report["analysisEscalationExecuted"])
            with patch("phone_saber_auto_repair._corpus_coverage",
                       return_value=(True, "covered")):
                gate = repair_gate(report, input_plan(bundle, allow_reports=True), root)
            self.assertEqual(gate["decision"], "needs_capture")

    def test_ambiguous_luna_escalates_once_without_repair(self) -> None:
        for final_decision in ("needs_capture", "actionable"):
            with self.subTest(final_decision=final_decision), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                bundle = root / "bundle"
                write_codex_bundle(bundle)
                add_synthetic_eligibility_trace(bundle)
                ambiguous = eligibility_assessment("needs_capture", "Interpretation remains uncertain")
                second = eligibility_assessment(final_decision, "Independent assessment")
                if final_decision == "actionable":
                    second["repair_assessment"].update({
                        "production_change_supported": True, "change_type": "candidate_logic",
                        "independent_visual_examples": 2})
                codex = fake_codex(root, root / "spy.json", responses=[ambiguous, second])
                analyze_bundle(bundle, codex_path=str(codex))
                spy = json.loads((root / "spy.json").read_text())
                report = json.loads((bundle / "analysis_report.json").read_text())
                self.assertEqual(len(spy["calls"]), 2)
                self.assertEqual(spy["calls"][1]["model"], "gpt-6-sol")
                self.assertEqual(spy["calls"][1]["sandbox"], "read-only")
                self.assertTrue(report["analysisEscalationExecuted"])
                self.assertEqual(report["analysis"]["repair_assessment"]["decision"],
                                 final_decision)
                with patch("phone_saber_auto_repair._corpus_coverage",
                           return_value=(True, "covered")):
                    gate = repair_gate(report, input_plan(bundle, allow_reports=True), root)
                self.assertEqual(gate["decision"],
                                 "actionable" if final_decision == "actionable"
                                 else "needs_capture")
                if final_decision == "needs_capture":
                    with patch("phone_saber_auto_repair._corpus_coverage",
                               return_value=(True, "covered")):
                        status = repair_bundle(bundle, repo=root, codex_path=str(codex))
                    self.assertEqual(status["status"], "needs_capture")
                    self.assertFalse(status["repairExecuted"])
                    self.assertEqual(len(json.loads((root / "spy.json").read_text())["calls"]), 2)

    def test_analysis_timeout_at_max_retries_once_at_high_with_the_local_digest(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle)
            spy = Path(directory) / "spy.json"
            codex = fake_codex(Path(directory), spy, analysis=EMPTY_ANALYSIS, sleep_on_max=5)
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                result = analyze_bundle(bundle, codex_path=str(codex), timeout_seconds=1)
            self.assertEqual(result["status"], "completed")
            report = json.loads((bundle / "analysis_report.json").read_text())
            self.assertEqual(report["analysisReasoningEffort"], "high")
            self.assertIn("retrying once at effort=high", output.getvalue())
            calls = json.loads(spy.read_text())["calls"]
            self.assertEqual(len(calls), 1, "only the retry reached the spy; the max call timed out first")
            self.assertIn("Local digest", calls[0]["prompt"])
            self.assertNotIn(str(bundle), calls[0]["prompt"], "no local paths in the digest")

    def test_dry_run_lists_only_selected_png_and_compact_context(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle)
            codex_spy = Path(directory) / "codex-spy.json"
            codex = fake_codex(Path(directory), codex_spy, analysis=EMPTY_ANALYSIS)
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                result = analyze_bundle(bundle, dry_run=True, codex_path=str(codex))
            report = output.getvalue()
            self.assertEqual(result["status"], "dry_run")
            self.assertFalse(codex_spy.exists())
            self.assertFalse((bundle / "analysis_report.json").exists())
            self.assertIn("image_001 -> image_01.png", report)
            self.assertIn("images/image_01.png", report)
            self.assertIn("frames/frame_100_1.json", report)
            self.assertIn("summary.json", report)
            self.assertIn("bundle prompt.md is not attached", report)
            self.assertIn("Excluded: video, full metadata.json, unselected PNGs", report)
            self.assertNotIn("image_02.png", report)
            # Geometry-free bundles still dry-run; the audit is simply empty.
            self.assertEqual(result["candidateAudit"], [])
            self.assertEqual(result["bridgeSummary"], [])

    def test_codex_is_given_only_selected_images_and_compact_metadata(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_codex_bundle(
                bundle, image_count=4,
                failure_types=("dropout", "endpoint_jump", "manual_capture", "candidate_zero"),
            )
            plan = input_plan(bundle)
            spy = root / "codex-spy.json"
            codex = fake_codex(root, spy, analysis=analysis_with_image_ids(FOUR_IMAGE_IDS))

            result = analyze_bundle(bundle, codex_path=str(codex))

            self.assertEqual(result["status"], "completed")
            invoked = json.loads(spy.read_text(encoding="utf-8"))
            self.assertEqual(len(invoked["images"]), 4)
            self.assertEqual(invoked["image_flag_count"], 4)
            self.assertTrue(all(path.endswith(f"/images/image_{index:02d}.png")
                                for index, path in enumerate(invoked["images"], start=1)))
            # The prompt is the fixed instructions plus the bounded local digest.
            self.assertTrue(invoked["prompt"].startswith(_codex_prompt("sample_session", plan.images, plan.root)))
            self.assertIn("Local digest", invoked["prompt"])
            self.assertIsNone(invoked["positional_prompt"])
            self.assertTrue(invoked["stdin_sentinel"])
            self.assertEqual(set(invoked["files"]), {
                "summary.json",
                "images/image_01.png", "images/image_02.png", "images/image_03.png", "images/image_04.png",
                "frames/frame_100_1.json", "frames/frame_101_2.json",
                "frames/frame_102_3.json", "frames/frame_103_4.json",
            })
            self.assertEqual(invoked["sandbox"], "read-only")
            self.assertEqual(invoked["model"], ANALYSIS_MODEL)
            self.assertEqual(invoked["effort_config"],
                             f'model_reasoning_effort="{ANALYSIS_REASONING_EFFORT}"')
            self.assertTrue(invoked["ephemeral"])
            self.assertTrue(invoked["skip_git_repo_check"])
            self.assertIn("A = capture/data artifact", invoked["prompt"])
            self.assertIn("Do not edit, create, or propose applying production code", invoked["prompt"])
            self.assertIn("image_ids may contain only the exact", invoked["prompt"])
            self.assertIn("detected=false", invoked["prompt"])
            self.assertIn("Context filename: frames/frame_100_1.json", invoked["prompt"])
            for section in ("false_negatives", "wrong_candidate_and_endpoint_errors",
                            "false_positive_suspects", "other_findings"):
                self.assertEqual(
                    invoked["output_schema"]["properties"][section]["items"]
                    ["properties"]["image_ids"]["items"]["enum"],
                    FOUR_IMAGE_IDS,
                )
            self.assertEqual(
                invoked["output_schema"]["properties"]["repair_assessment"]
                ["properties"]["evidence_image_ids"]["items"]["enum"], FOUR_IMAGE_IDS)
            report = json.loads((bundle / "analysis_report.json").read_text(encoding="utf-8"))
            self.assertEqual(report["formatVersion"], 3)
            self.assertEqual(report["analysisModel"], ANALYSIS_MODEL)
            self.assertEqual(report["analysisReasoningEffort"], ANALYSIS_REASONING_EFFORT)
            self.assertTrue(report["analysisExecuted"])
            self.assertEqual(report["input"]["imageCount"], 4)
            self.assertEqual([image["id"] for image in report["input"]["imageReferences"]],
                             FOUR_IMAGE_IDS)
            self.assertEqual(report["analysis"]["other_findings"][0]["image_ids"], FOUR_IMAGE_IDS)
            self.assertFalse(report["input"]["videoIncluded"])
            self.assertFalse(report["input"]["fullMetadataIncluded"])
            markdown = (bundle / "analysis_report.md").read_text(encoding="utf-8")
            self.assertIn("image_001 — `images/image_01.png`", markdown)
            self.assertIn("images: image_001, image_002, image_003, image_004", markdown)

    def test_unknown_or_path_like_image_references_are_rejected(self) -> None:
        for name, image_ids in (
            ("unknown ID", ["image_999"]),
            ("absolute path", ["/tmp/input/images/image_01.png"]),
            ("traversal path", ["../images/image_01.png"]),
        ):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                bundle = root / "bundle"
                write_codex_bundle(bundle)
                codex = fake_codex(root, root / "spy.json",
                                   analysis=analysis_with_image_ids(image_ids))
                with self.assertRaisesRegex(CodexFailed, "image ID that was not sent"):
                    analyze_bundle(bundle, codex_path=str(codex))
                self.assertTrue((bundle / "summary.json").is_file())
                self.assertFalse((bundle / "analysis_report.json").exists())
                self.assertFalse((bundle / "analysis_report.md").exists())

    def test_duplicate_image_reference_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_codex_bundle(bundle)
            codex = fake_codex(root, root / "spy.json",
                               analysis=analysis_with_image_ids(["image_001", "image_001"]))
            with self.assertRaisesRegex(CodexFailed, "duplicate image IDs"):
                analyze_bundle(bundle, codex_path=str(codex))
            self.assertTrue((bundle / "summary.json").is_file())
            self.assertFalse((bundle / "analysis_report.json").exists())

    def test_invalid_repair_assessment_is_rejected_before_report_publish(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_codex_bundle(bundle)
            invalid = copy.deepcopy(EMPTY_ANALYSIS)
            invalid["repair_assessment"]["evidence_image_ids"] = ["image_999"]
            codex = fake_codex(root, root / "spy.json", analysis=invalid)
            with self.assertRaisesRegex(CodexFailed, "repair assessment"):
                analyze_bundle(bundle, codex_path=str(codex))
            self.assertFalse((bundle / "analysis_report.json").exists())

    def test_case_insensitive_basename_collision_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle, image_count=2)
            summary_path = bundle / "summary.json"
            summary = json.loads(summary_path.read_text(encoding="utf-8"))
            collision_path = bundle / "images/IMAGE_01.PNG"
            (bundle / summary["images"][1]["path"]).rename(collision_path)
            summary["images"][1]["path"] = "images/IMAGE_01.PNG"
            summary_path.write_text(json.dumps(summary), encoding="utf-8")
            with self.assertRaisesRegex(BundleError, "ambiguous selected image basenames"):
                input_plan(bundle)

    def test_output_schema_is_limited_to_selected_image_ids(self) -> None:
        schema = _output_schema(("image_001", "image_002"))
        for section in ("false_negatives", "wrong_candidate_and_endpoint_errors",
                        "false_positive_suspects", "other_findings"):
            item_schema = schema["properties"][section]["items"]
            self.assertNotIn("image_paths", item_schema["properties"])
            self.assertEqual(item_schema["properties"]["image_ids"]["items"], {
                "type": "string", "enum": ["image_001", "image_002"],
            })
        self.assertEqual(
            schema["properties"]["repair_assessment"]["properties"]
            ["evidence_image_ids"]["items"]["enum"], ["image_001", "image_002"])

    def test_codex_unavailable_preserves_received_bundle(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle)
            with self.assertRaises(CodexUnavailable):
                analyze_bundle(bundle, codex_path="/missing/codex")
            self.assertTrue((bundle / "summary.json").is_file())
            self.assertTrue((bundle / "images/image_01.png").is_file())
            self.assertFalse((bundle / "analysis_report.json").exists())

    def test_codex_failure_preserves_bundle_and_writes_no_partial_report(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_codex_bundle(bundle)
            codex = fake_codex(root, root / "unused.json", fail=True)
            with self.assertRaises(CodexFailed):
                analyze_bundle(bundle, codex_path=str(codex))
            self.assertTrue((bundle / "summary.json").is_file())
            self.assertFalse((bundle / "analysis_report.json").exists())
            self.assertFalse((bundle / "analysis_report.md").exists())

    def test_model_unavailable_fails_without_fallback(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_codex_bundle(bundle)
            spy = root / "spy.json"
            codex = fake_codex(root, spy, model_error="unknown model gpt-6-luna")
            with self.assertRaisesRegex(CodexModelUnavailable, "MODEL_UNAVAILABLE"):
                analyze_bundle(bundle, codex_path=str(codex))
            invoked = json.loads(spy.read_text())
            self.assertEqual(invoked["model"], ANALYSIS_MODEL)
            self.assertEqual(invoked["effort_config"],
                             f'model_reasoning_effort="{ANALYSIS_REASONING_EFFORT}"')
            self.assertFalse((bundle / "analysis_report.json").exists())

    def test_zero_image_session_writes_local_report_without_codex(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle, image_count=0)
            result = analyze_bundle(bundle, codex_path="/missing/codex")
            self.assertEqual(result["status"], "no_images")
            report = json.loads((bundle / "analysis_report.json").read_text(encoding="utf-8"))
            self.assertEqual(report["input"]["imageCount"], 0)
            self.assertEqual(report["analysisModel"], ANALYSIS_MODEL)
            self.assertEqual(report["analysisReasoningEffort"], ANALYSIS_REASONING_EFFORT)
            self.assertFalse(report["analysisExecuted"])
            self.assertIn("No Codex request was made", report["analysis"]["session_summary"])
            self.assertEqual(report["analysis"]["repair_assessment"]["decision"], "needs_capture")

    def test_default_image_limit_and_failure_type_dedup_are_enforced(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle, image_count=3)
            with self.assertRaisesRegex(BundleError, "per-failure-type"):
                input_plan(bundle)
            with self.assertRaises(ValueError):
                input_plan(bundle, max_images=21)

    def test_guided_recording_may_keep_four_swing_lossless_frames(self) -> None:
        # Guided recordings keep up to 5 lossless frames; manual ones stay at 2.
        from test_phone_saber_metadata_schema import guided_recording_sample
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle, image_count=5, failure_types=("manual_capture",) * 5)
            with self.assertRaisesRegex(BundleError, "per-failure-type"):
                input_plan(bundle)
            summary_path = bundle / "summary.json"
            summary = json.loads(summary_path.read_text(encoding="utf-8"))
            summary["guidedRecording"] = guided_recording_sample()
            summary_path.write_text(json.dumps(summary), encoding="utf-8")
            self.assertEqual(len(input_plan(bundle).images), 5)

    def test_malformed_summary_is_rejected_before_codex(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle)
            (bundle / "summary.json").write_text("{bad", encoding="utf-8")
            with self.assertRaisesRegex(BundleError, "summary.json is malformed"):
                input_plan(bundle)

    def test_full_session_metadata_cannot_be_attached_through_summary(self) -> None:
        for key, value in (
            ("frames", [{"frameID": index} for index in range(100)]),
            ("cameraSamples", [{"timestamp": index} for index in range(100)]),
            ("candidateDiagnostics", {"red": {"raw": "full session"}}),
        ):
            with self.subTest(key=key), tempfile.TemporaryDirectory() as directory:
                bundle = Path(directory) / "bundle"
                write_codex_bundle(bundle)
                summary_path = bundle / "summary.json"
                summary = json.loads(summary_path.read_text(encoding="utf-8"))
                summary[key] = value
                summary_path.write_text(json.dumps(summary), encoding="utf-8")
                with self.assertRaisesRegex(BundleError, "full-session metadata"):
                    input_plan(bundle)

    def test_retained_incident_context_frames_must_be_a_nonnegative_integer(self) -> None:
        for value in (True, -1, 1.5, "138"):
            with self.subTest(value=value), tempfile.TemporaryDirectory() as directory:
                bundle = Path(directory) / "bundle"
                write_codex_bundle(bundle)
                summary_path = bundle / "summary.json"
                summary = json.loads(summary_path.read_text(encoding="utf-8"))
                summary["retainedIncidentContextFrames"] = value
                summary_path.write_text(json.dumps(summary), encoding="utf-8")
                with self.assertRaisesRegex(BundleError, "retainedIncidentContextFrames"):
                    input_plan(bundle)

    def test_summary_scope_must_match_the_compact_triage_scope(self) -> None:
        for value in (True, 138, "", "full session metadata"):
            with self.subTest(value=value), tempfile.TemporaryDirectory() as directory:
                bundle = Path(directory) / "bundle"
                write_codex_bundle(bundle)
                summary_path = bundle / "summary.json"
                summary = json.loads(summary_path.read_text(encoding="utf-8"))
                summary["summaryScope"] = value
                summary_path.write_text(json.dumps(summary), encoding="utf-8")
                with self.assertRaisesRegex(BundleError, "summaryScope"):
                    input_plan(bundle)

    def test_receiver_dry_run_accepts_bundle_without_calling_codex(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inbox = root / "inbox"
            server = TriageHTTPServer(("127.0.0.1", 0), inbox,
                                      analysis_mode="dry-run", codex_path="/missing/codex")
            thread = Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                body = write_codex_bundle(root / "bundle")
                connection = HTTPConnection("127.0.0.1", server.server_port, timeout=3)
                connection.request("POST", "/v1/bundle", body=body, headers={
                    "Content-Type": CONTENT_TYPE,
                    "Content-Length": str(len(body)),
                    "Connection": "close",
                })
                response = connection.getresponse()
                self.assertEqual(response.status, 201)
                response.read()
                connection.close()
                server.analysis_queue.join()
                received = inbox / "phone_saber_triage_sample_session"
                self.assertTrue((received / "summary.json").is_file())
                self.assertFalse((received / "analysis_report.json").exists())
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)

    def test_receiver_remains_available_after_codex_failure(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inbox = root / "inbox"
            codex = fake_codex(root, root / "unused.json", fail=True)
            server = TriageHTTPServer(("127.0.0.1", 0), inbox,
                                      analysis_mode="automatic", codex_path=str(codex))
            thread = Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                body = write_codex_bundle(root / "bundle")
                connection = HTTPConnection("127.0.0.1", server.server_port, timeout=3)
                connection.request("POST", "/v1/bundle", body=body, headers={
                    "Content-Type": CONTENT_TYPE,
                    "Content-Length": str(len(body)),
                    "Connection": "close",
                })
                response = connection.getresponse()
                self.assertEqual(response.status, 201)
                response.read()
                connection.close()
                server.analysis_queue.join()

                health = HTTPConnection("127.0.0.1", server.server_port, timeout=3)
                health.request("GET", "/health")
                health_response = health.getresponse()
                self.assertEqual(health_response.status, 200)
                health_response.read()
                health.close()
                received = inbox / "phone_saber_triage_sample_session"
                self.assertTrue((received / "summary.json").is_file())
                self.assertFalse((received / "analysis_report.json").exists())
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)


def add_synthetic_eligibility_trace(bundle: Path) -> None:
    path = bundle / "frames/frame_100_1.json"
    context = json.loads(path.read_text(encoding="utf-8"))
    frames = []
    for frame_id in (100, 101):
        candidate = {"index": 0, "sourceType": "color-mask", "eligible": False,
                     "finalScore": 12.5, "rejectionReasons": ["peakValue"],
                     "peakValue": 210, "meanValue": 180.0, "highValueRatio": 0.02,
                     "meanColorPurity": 0.54, "clippedWhiteRatio": 0.0,
                     "isCompactRed": False, "rawPCASpan": 140.0,
                     "robustMainIntervalLength": 105.0, "continuity": 0.8,
                     "density": 2.0, "componentArea": 310, "pointCount": 78,
                     "rules": [{"name": "peakValue", "result": "FAIL", "value": 210,
                                "comparison": ">=", "threshold": 218}]}
        frames.append({"frameID": frame_id, "timestamp": frame_id / 30,
                       "red": {"detected": False, "detectionSucceeded": False,
                               "candidateCount": 1, "eligibleCandidateCount": 0,
                               "failureStage": "eligibility",
                               "candidateDecisionTrace": [candidate]},
                       "blue": {"detected": True}})
    context["frames"] = frames
    path.write_text(json.dumps(context), encoding="utf-8")


def eligibility_assessment(decision: str, reason: str) -> dict:
    analysis = copy.deepcopy(EMPTY_ANALYSIS)
    analysis["repair_assessment"].update({
        "decision": decision, "visible_saber_confirmed": True,
        "root_cause_stage": "eligibility", "diagnosis_consistent_with_metadata": True,
        "affected_colors": ["RED"], "evidence_image_ids": ["image_001"],
        "reason": reason})
    analysis["false_negatives"] = [{
        "classification": ["C"], "color": "RED", "frame_ids": [100],
        "image_ids": ["image_001"], "issue_type": "eligibility dropout",
        "observation": "A lit red saber is visible in the selected image.",
        "interpretation": "A candidate failed an eligibility rule.", "confidence": "high"}]
    return analysis


def fake_codex(root: Path, spy_path: Path, *, analysis: dict | None = None,
               fail: bool = False, model_error: str | None = None,
               responses: list[dict] | None = None, sleep_on_max: float = 0) -> Path:
    executable = root / "fake-codex"
    response_json = json.dumps(analysis or EMPTY_ANALYSIS, ensure_ascii=False)
    response_sequence = json.dumps(responses or [], ensure_ascii=False)
    spy_literal = repr(str(spy_path))
    script = f"""#!{sys.executable}
import json, pathlib, sys
args = sys.argv[1:]
if args == ["--version"]:
    print("codex-cli test")
    raise SystemExit(0)
prompt = sys.stdin.read()
model = args[args.index('--model') + 1] if '--model' in args else None
effort_config = args[args.index('-c') + 1] if '-c' in args else None
if {sleep_on_max!r} and effort_config and '"max"' in effort_config:
    import time
    time.sleep({sleep_on_max!r})
if {fail or model_error is not None!r}:
    pathlib.Path({spy_literal}).write_text(json.dumps({{'model': model, 'effort_config': effort_config, 'args': args}}), encoding='utf-8')
    print({(model_error or 'simulated Codex failure')!r}, file=sys.stderr)
    raise SystemExit(9)
image_paths = [args[i + 1] for i, value in enumerate(args[:-1]) if value == '--image']
input_root = pathlib.Path(args[args.index('--cd') + 1])
response_path = pathlib.Path(args[args.index('--output-last-message') + 1])
schema_path = pathlib.Path(args[args.index('--output-schema') + 1])
spy = {{
    'images': image_paths,
    'image_flag_count': args.count('--image'),
    'files': sorted(path.relative_to(input_root).as_posix() for path in input_root.rglob('*') if path.is_file()),
    'sandbox': args[args.index('--sandbox') + 1],
    'model': model,
    'effort_config': effort_config,
    'ephemeral': '--ephemeral' in args,
    'skip_git_repo_check': '--skip-git-repo-check' in args,
    'prompt': prompt,
    'positional_prompt': args[-1] if args and args[-1] != '-' else None,
    'stdin_sentinel': bool(args and args[-1] == '-'),
    'output_schema': json.loads(schema_path.read_text(encoding='utf-8')),
}}
spy_file = pathlib.Path({spy_literal})
prior = json.loads(spy_file.read_text(encoding='utf-8')) if spy_file.exists() else {{}}
calls = prior.get('calls', [])
calls.append({{'model': model, 'prompt': prompt, 'sandbox': spy['sandbox']}})
spy['calls'] = calls
spy_file.write_text(json.dumps(spy), encoding='utf-8')
responses = json.loads({response_sequence!r})
response = responses[min(len(calls) - 1, len(responses) - 1)] if responses else json.loads({response_json!r})
response_path.write_text(json.dumps(response), encoding='utf-8')
"""
    executable.write_text(script, encoding="utf-8")
    executable.chmod(0o755)
    return executable


def analysis_with_image_ids(image_ids: list[str]) -> dict:
    analysis = copy.deepcopy(EMPTY_ANALYSIS)
    analysis["other_findings"] = [{
        "classification": ["G"],
        "color": "UNKNOWN",
        "frame_ids": [100],
        "image_ids": image_ids,
        "issue_type": "Image reference contract test",
        "observation": "A selected image was supplied.",
        "interpretation": "Only supplied image IDs are referenced.",
        "confidence": "low",
    }]
    return analysis


def write_codex_bundle(bundle: Path, *, image_count: int = 1,
                       failure_types: tuple[str, ...] | None = None) -> bytes:
    write_bundle(bundle, image_count=image_count)
    summary_path = bundle / "summary.json"
    summary = json.loads(summary_path.read_text(encoding="utf-8"))
    summary["summaryScope"] = EXPECTED_SUMMARY_SCOPE
    summary["retainedIncidentContextFrames"] = 138
    if failure_types is not None:
        if len(failure_types) != image_count:
            raise ValueError("failure_types must match image_count")
        for image, failure_type in zip(summary["images"], failure_types):
            image["failureType"] = failure_type
    summary_path.write_text(json.dumps(summary), encoding="utf-8")
    envelope = bundle.with_suffix(".psbt")
    pack_bundle(bundle, envelope)
    return envelope.read_bytes()


if __name__ == "__main__":
    unittest.main()
