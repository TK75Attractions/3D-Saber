"""Actual Swift capture -> PNG mapping -> mocked LLM -> conservative repair gate."""
import contextlib
import copy
import io
import json
from pathlib import Path
import sys
import tempfile
import unittest
from unittest import mock

from phone_saber_tracking_e2e import capture_bundles
from phone_saber_tracking_diagnostics import (temporal_events, tracking_preflight,
    tracking_summary, PrecheckFailed)
from phone_saber_triage_protocol import pack_bundle, receive_bundle
from phone_saber_triage_codex import input_plan, analyze_bundle, _codex_prompt
import phone_saber_auto_repair as repair
from test_phone_saber_triage_codex import fake_codex, EMPTY_ANALYSIS
from test_phone_saber_tracking_diagnostics import tracking_analysis
from test_phone_saber_auto_repair import miniature_repo, fake_git, BASE


@unittest.skipUnless(sys.platform == "darwin", "actual AVFoundation capture harness requires macOS")
class TrackingPipelineE2ETests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.storage = tempfile.TemporaryDirectory(prefix="phonesaber-tracking-e2e-")
        cls.addClassCleanup(cls.storage.cleanup)
        cls.captures = capture_bundles(Path(cls.storage.name))

    def bundle(self, root, scenario):
        source = Path(self.captures[scenario]["bundle"])
        envelope = root / "capture.psbt"
        pack_bundle(source, envelope)
        with envelope.open("rb") as stream:
            return receive_bundle(stream, inbox=root / "inbox", content_length=envelope.stat().st_size)

    def response(self, plan, stage, actionable=True):
        event = temporal_events(plan)[0]
        ids = [f["imageID"] for f in event["frames"]]
        value = tracking_analysis(len(ids), actionable=actionable)
        value["tracking_assessment"].update(first_unstable_stage=stage, temporal_image_ids=ids)
        value["repair_assessment"].update(evidence_image_ids=ids,
            root_cause_stage="ranking" if stage == "candidate_selection" else "endpoint",
            change_type="candidate_logic" if stage == "candidate_selection" else "endpoint_logic")
        finding = value["wrong_candidate_and_endpoint_errors"][0]
        finding.update(frame_ids=[f["frameID"] for f in event["frames"]], image_ids=ids)
        return value

    def analyze(self, root, bundle, responses):
        binary = fake_codex(root, root / "spy.json", responses=responses)
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            result = analyze_bundle(bundle, codex_path=str(binary))
        self.assertEqual(result["status"], "completed")
        self.assertIn("TRACKING_SUMMARY", output.getvalue())
        plan = input_plan(bundle, allow_reports=True)
        return plan, repair.load_analysis(bundle, plan), json.loads((root / "spy.json").read_text())["calls"]

    def test_A_stable_tracking_and_E_stable_emitted_endpoints_never_repair(self):
        for scenario in ("stable", "emitted-stable"):
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp); bundle = self.bundle(root, scenario)
                plan = input_plan(bundle)
                event = temporal_events(plan)[0]
                self.assertTrue(all(f["detected"] for f in event["frames"]))
                self.assertEqual(max(f["tracking"]["instabilityScore"] for f in event["frames"]), 0)
                metadata = json.loads(Path(self.captures[scenario]["metadata"]).read_text())
                self.assertEqual(max(f["tracking"]["red"]["instabilityScore"] for f in metadata["frames"]), 0)
                self.assertEqual(len({tuple(f["emittedEndpoint"]) for f in event["frames"]}), 1)
                self.assertTrue(all(f["udpTransmissions"][0]["sourceEndpoint"] == f["emittedEndpoint"]
                                    for f in event["frames"]))
                plan, report, calls = self.analyze(root, bundle, [copy.deepcopy(EMPTY_ANALYSIS)])
                self.assertEqual(len(calls), 1)
                with mock.patch.object(repair, "main_safety_gate") as safety, contextlib.redirect_stdout(io.StringIO()):
                    result = repair.repair_bundle(bundle)
                safety.assert_not_called()
                self.assertEqual(result["status"], "needs_capture")
                self.assertFalse(result["repairExecuted"])

    def test_B_candidate_switch_C_raw_jump_D_path_switch_reach_context_and_gate(self):
        for scenario, stage, label in (("candidate-switch", "candidate_selection", "candidate-selection"),
                ("raw-jump", "PCA", "raw-pca"), ("path-switch", "endpoint_selection", "final-selection")):
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp); bundle = self.bundle(root, scenario); plan = input_plan(bundle)
                self.assertEqual(tracking_preflight(plan)["status"], "PASS")
                event = temporal_events(plan)[0]; center = event["centerFrameID"]
                self.assertEqual([f["frameID"] for f in event["frames"]], list(range(center - 5, center + 6)))
                peak = next(f for f in event["frames"] if f["role"] == "peak")
                self.assertTrue(peak["detected"])
                self.assertGreater(peak["tracking"]["instabilityScore"], 0)
                metadata = json.loads(Path(self.captures[scenario]["metadata"]).read_text())
                ranked = max(metadata["frames"], key=lambda f: f["tracking"]["red"]["instabilityScore"])
                self.assertEqual(center, ranked["frameID"])
                # The Swift host harness decoded every lossless PNG and checked
                # its identifying pixel against the mapped frame number.
                self.assertEqual(self.captures[scenario]["pngMappingVerified"], len(plan.images))
                prompt = _codex_prompt(plan.session_id, plan.images, plan.root)
                if scenario == "candidate-switch":
                    self.assertTrue(peak["tracking"]["candidateSwitch"])
                    self.assertIn('"candidateSwitch": true', prompt)
                elif scenario == "raw-jump":
                    self.assertGreater(peak["tracking"]["stageDiscontinuities"]["rawPCA"], 0)
                    self.assertIn('"rawPCA"', prompt)
                else:
                    self.assertTrue(peak["tracking"]["endpointPathChanged"])
                    sources = {f["selectedCandidate"]["endpointPipeline"]["endpointSource"] for f in event["frames"]}
                    self.assertEqual(sources, {"fallbackPCA", "bodyPCA"})
                    self.assertIn('"endpointPathChanged": true', prompt)
                response = self.response(plan, stage, actionable=scenario != "path-switch")
                if scenario == "path-switch":
                    # A path change with stable pixels/outputs is not a confirmed
                    # symptom and must not turn into a recognition repair.
                    response["tracking_assessment"]["symptom_confirmed_in_images"] = False
                    response["wrong_candidate_and_endpoint_errors"] = []
                plan, report, calls = self.analyze(root, bundle, [response])
                gate = repair.repair_gate(report, plan, repair.REPO_ROOT)
                expected_gate = "needs_capture" if scenario == "path-switch" else "actionable"
                self.assertEqual(gate["decision"], expected_gate, gate)
                row = tracking_summary(plan, report, gate)[0]
                self.assertEqual(row["firstUnstableStage"], label)
                self.assertEqual(row["gateResult"], expected_gate)
                self.assertEqual(len(calls), 1)
                if scenario == "raw-jump":
                    report["analysis"]["tracking_assessment"]["first_unstable_stage"] = "downstream"
                    self.assertIn("productionChangeUnsupported", repair.repair_gate(report, plan, repair.REPO_ROOT)["reasonCodes"])

    def test_F_missing_temporal_image_and_G_broken_mapping_make_zero_calls(self):
        for mutation, code in (("image", "imageFileMissing"), ("mapping", "frameMappingMissing")):
            with self.subTest(mutation=mutation), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp); bundle = self.bundle(root, "candidate-switch"); plan = input_plan(bundle)
                image = next(i for i in plan.images if i.frame_id == temporal_events(plan)[0]["centerFrameID"])
                if mutation == "image": image.image_path.unlink()
                else:
                    c = json.loads(image.context_path.read_text()); c["imageMapping"]["frameID"] += 1
                    image.context_path.write_text(json.dumps(c))
                before = {p.relative_to(bundle): p.read_bytes() for p in bundle.rglob("*") if p.is_file()}
                with mock.patch("phone_saber_triage_codex.find_codex_binary") as cli, contextlib.redirect_stdout(io.StringIO()):
                    with self.assertRaises(PrecheckFailed) as failed: analyze_bundle(bundle)
                cli.assert_not_called(); self.assertIn(code, failed.exception.reason_codes)
                self.assertEqual(before, {p.relative_to(bundle): p.read_bytes() for p in bundle.rglob("*") if p.is_file()})

    def test_detected_true_luna_reanalysis_sol_actionable_enters_repair(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); bundle = self.bundle(root, "candidate-switch"); plan = input_plan(bundle)
            initial = self.response(plan, "candidate_selection", False)
            initial.pop("tracking_assessment")
            initial["repair_assessment"]["reason"] = "Missing temporal evidence and candidate history."
            uncertain = self.response(plan, "candidate_selection", False)
            final = self.response(plan, "candidate_selection")
            plan, report, calls = self.analyze(root, bundle, [initial, uncertain, final])
            self.assertEqual([c["model"] for c in calls], ["gpt-6-luna", "gpt-6-luna", "gpt-6-sol"])
            self.assertTrue(report["analysisReanalysisExecuted"] and report["analysisEscalationExecuted"])
            self.assertIn("trackingEvents", calls[1]["prompt"])
            self.assertTrue(all(c["sandbox"] == "read-only" for c in calls))
            self.assertEqual(repair.repair_gate(report, plan, repair.REPO_ROOT)["decision"], "actionable")
            # A miniature repository isolates the real repair state machine.
            # The repair LLM deliberately requests more evidence; no recognition
            # source or external remote is touched by this integration harness.
            repo = miniature_repo(root)
            with mock.patch.object(repair, "main_safety_gate", return_value={"head": BASE, "origin": BASE}), \
                    mock.patch.object(repair, "find_codex_binary", return_value="fixture-cli"), \
                    mock.patch.object(repair, "_baseline", return_value={"fixtures": [], "summary": {"passed": 40, "failed": 0}}), \
                    mock.patch.object(repair, "_codex_call", return_value={"decision": "needs_more_evidence", "summary": "Fixture repair invoked"}) as repair_llm, \
                    mock.patch.object(repair, "_git", side_effect=fake_git), contextlib.redirect_stdout(io.StringIO()):
                result = repair.repair_bundle(bundle, repo=repo)
            repair_llm.assert_called_once()
            self.assertTrue(result["repairAttempted"])
            repair_report = json.loads((bundle / "repair_report.json").read_text())
            self.assertEqual(repair_report["gate"]["decision"], "actionable")
            self.assertTrue(repair_report["trackingSummary"][0]["solEscalationExecuted"])


if __name__ == "__main__": unittest.main()
