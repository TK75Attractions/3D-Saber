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
    tracking_summary, PrecheckFailed, sufficient_temporal as sufficient_for_tracking)
from phone_saber_triage_protocol import pack_bundle, receive_bundle
from phone_saber_triage_codex import input_plan, analyze_bundle, _codex_prompt
import phone_saber_auto_repair as repair
from test_phone_saber_triage_codex import fake_codex, EMPTY_ANALYSIS
from test_phone_saber_tracking_diagnostics import tracking_analysis
from test_phone_saber_auto_repair import miniature_repo, fake_git, BASE
from phone_saber_test_isolation import isolate_codex_logs as setUpModule  # noqa: F401,E402
from phone_saber_test_isolation import restore_codex_logs as tearDownModule  # noqa: F401,E402


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


    # --- bridge dropouts: one temporal evidence event, annotated image is not evidence ---

    def bridge_analysis(self, evidence_ids, frame_ids, examples=2):
        value = copy.deepcopy(EMPTY_ANALYSIS)
        value["repair_assessment"].update(
            decision="actionable", visible_saber_confirmed=True, production_change_supported=True,
            root_cause_stage="eligibility", diagnosis_consistent_with_metadata=True,
            change_type="threshold", independent_visual_examples=examples,
            affected_colors=["RED"], evidence_image_ids=evidence_ids, reason="Fixture threshold proposal.")
        value["false_negatives"] = [{"classification": ["C"], "color": "RED", "frame_ids": frame_ids,
            "image_ids": evidence_ids, "issue_type": "eligibility", "observation": "visible saber",
            "interpretation": "rejected by an eligibility rule", "confidence": "high"}]
        return value

    def test_H_real_recorder_bridge_event_is_one_temporal_unit_with_original_and_annotated_png(self):
        from phone_saber_tracking_diagnostics import bridge_events, bridge_summary, complete_bridge_event
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); bundle = self.bundle(root, "bridge"); plan = input_plan(bundle)
            bridge_images = [i for i in plan.images if i.bridge_event_id is not None]
            self.assertEqual([i.role for i in bridge_images],
                             ["before_success", "dropout", "annotated_dropout", "after_success"])
            self.assertEqual({i.bridge_event_id for i in bridge_images}, {1})
            self.assertEqual([i.auxiliary for i in bridge_images], [False, False, True, False])
            self.assertEqual([i.frame_id for i in bridge_images], [1009, 1010, 1010, 1012])
            # Bridge events come first so they are never cut by the image limit.
            self.assertEqual([i.image_id for i in bridge_images],
                             ["image_001", "image_002", "image_003", "image_004"])
            events = bridge_events(plan)
            self.assertEqual(len(events), 1)
            self.assertTrue(complete_bridge_event(events[0]))
            self.assertEqual(events[0]["missingFrameCount"], 2)
            self.assertEqual(tracking_preflight(plan)["status"], "PASS")
            rows = bridge_summary(plan)
            self.assertEqual([(r["independentExamples"], r["annotatedImage"], r["frames"]) for r in rows],
                             [(1, True, [1009, 1010, 1012])])
            summary = json.loads((bundle / "summary.json").read_text())
            self.assertEqual(summary["activeColors"], ["red", "blue"])
            self.assertEqual(summary["bridgeDropoutSummary"]["selectedEventIDs"], [1])
            # Swift decoded every PNG (including the annotated copy) against its frame number.
            self.assertEqual(self.captures["bridge"]["pngMappingVerified"], len(plan.images))
            # The original dropout PNG is kept beside the annotated one.
            forensic = Path(self.captures["bridge"]["forensic"])
            metadata = json.loads(Path(self.captures["bridge"]["metadata"]).read_text())
            images = metadata["bridgeDropoutEvents"][0]["images"]
            dropout = next(i for i in images if i["role"] == "dropout")
            self.assertNotEqual((forensic / dropout["fileName"]).read_bytes(),
                                (forensic / dropout["annotatedFileName"]).read_bytes())
            prompt = _codex_prompt(plan.session_id, plan.images, plan.root)
            self.assertIn("ONE temporal evidence event", prompt)
            self.assertIn("never ground truth", prompt)
            self.assertIn('"evidenceUnit": "bridge_dropout_1"', prompt)
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                analyze_bundle(bundle, codex_path=str(fake_codex(root, root / "spy.json",
                                                                 analysis=EMPTY_ANALYSIS)))
            self.assertIn("[AUTO_REPAIR][BRIDGE_SUMMARY]", output.getvalue())

    def test_I_three_frames_of_one_bridge_event_are_one_example_and_annotated_is_not_evidence(self):
        import dataclasses
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp); bundle = self.bundle(root, "bridge"); plan = input_plan(bundle)
            originals = ["image_001", "image_002", "image_004"]
            response = self.bridge_analysis(originals, [1009, 1010, 1012])
            binary = fake_codex(root, root / "spy.json", responses=[response])
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(analyze_bundle(bundle, codex_path=str(binary))["status"], "completed")
            plan = input_plan(bundle, allow_reports=True)
            report = repair.load_analysis(bundle, plan)
            with mock.patch.object(repair, "_corpus_coverage", return_value=(True, "covered")):
                gate = repair.repair_gate(report, plan, repair.REPO_ROOT)
                # Three PNGs, three frame IDs, and a claimed count of two: still one event.
                self.assertEqual(gate["decision"], "needs_capture", gate)
                self.assertIn("insufficientIndependentVisualExamples", gate["reasonCodes"])
                # The same evidence from two distinct events clears only that unit check.
                second = dataclasses.replace(plan.images[3], bridge_event_id=2)
                two_events = dataclasses.replace(plan, images=plan.images[:3] + (second,))
                gate_two = repair.repair_gate(report, two_events, repair.REPO_ROOT)
                self.assertNotIn("insufficientIndependentVisualExamples", gate_two["reasonCodes"])
                # An annotated image alone never supports a repair.
                annotated = self.bridge_analysis(["image_003"], [1010])
                annotated_report = copy.deepcopy(report)
                annotated_report["analysis"] = annotated
                gate_annotated = repair.repair_gate(annotated_report, plan, repair.REPO_ROOT)
                self.assertEqual(gate_annotated["decision"], "needs_capture")
                self.assertTrue(any("no selected RED PNG supports" in r for r in gate_annotated["reasons"]))
                self.assertTrue(any("lack selected PNG references" in r for r in gate_annotated["reasons"]))

    def test_J_absence_before_first_and_after_last_detection_leaves_no_evidence(self):
        capture = self.captures["edge-absence"]
        metadata = json.loads(Path(capture["metadata"]).read_text())
        self.assertEqual(metadata["bridgeDropoutEvents"], [])
        window = metadata["diagnosticWindows"]["red"]
        self.assertEqual((window["firstSuccessFrameID"], window["lastSuccessFrameID"]), (1006, 1018))
        self.assertEqual(metadata["bridgeDropoutSummary"]["unclosedAtStop"], 1)
        # The saber leaving the view is not a tracking-instability event either.
        ranked = [f for f in metadata["frames"] if f["tracking"]["red"]["detectedToggle"]]
        self.assertTrue(ranked, "fixture must contain true<->false toggles")
        # Whatever was selected lies inside the detected window and is not an absence image.
        if capture["bundle"] is not None:
            summary = json.loads((Path(capture["bundle"]) / "summary.json").read_text())
            for image in summary["images"]:
                self.assertTrue(window["firstSuccessFrameID"] <= image["frameID"] <= window["lastSuccessFrameID"]
                                or image["frameID"] <= window["lastSuccessFrameID"] + 5, image)
                self.assertNotIn("candidate_zero", image["failureType"])
                self.assertNotIn("dropout", image["failureType"])
                context = json.loads((Path(capture["bundle"]) / image["frameContextPath"]).read_text())
                selected = next(f for f in context["frames"] if f["frameID"] == image["frameID"])
                if image["frameID"] <= window["lastSuccessFrameID"]:
                    self.assertTrue(selected["red"].get("detectionSucceeded"), image)

    def test_K_bridge_bundle_contract_rejects_partial_forged_or_oversized_events(self):
        from phone_saber_triage_protocol import BundleError
        def fresh():
            tmp = tempfile.TemporaryDirectory(); self.addCleanup(tmp.cleanup)
            bundle = self.bundle(Path(tmp.name), "bridge")
            return bundle, json.loads((bundle / "summary.json").read_text())
        def save(bundle, summary): (bundle / "summary.json").write_text(json.dumps(summary))
        # An event without its after_success image is never accepted as a partial event.
        bundle, summary = fresh()
        for entry in summary["images"]:
            if entry.get("role") == "after_success":
                entry.update(path=None)
        summary["images"] = [e for e in summary["images"] if e.get("role") != "after_success"]
        summary["selectedImageCount"] = len(summary["images"])
        save(bundle, summary)
        with self.assertRaises(BundleError): input_plan(bundle)
        # Summary and context must agree on event, role and frame.
        bundle, summary = fresh()
        next(e for e in summary["images"] if e.get("role") == "dropout")["bridgeEventID"] = 7
        save(bundle, summary)
        with self.assertRaises(BundleError): input_plan(bundle)
        # The annotated image must be derived from this event's own dropout PNG.
        bundle, summary = fresh()
        next(e for e in summary["images"] if e.get("role") == "annotated_dropout")["derivedFromImage"] = \
            next(e for e in summary["images"] if e.get("role") == "before_success")["path"]
        save(bundle, summary)
        with self.assertRaisesRegex(BundleError, "not derived from its dropout"): input_plan(bundle)
        # The three originals must be ordered before < dropout < after.
        bundle, summary = fresh()
        entry = next(e for e in summary["images"] if e.get("role") == "after_success")
        context_path = bundle / entry["frameContextPath"]
        context = json.loads(context_path.read_text())
        context["bridgeEvent"]["afterFrameID"] = context["bridgeEvent"]["beforeFrameID"]
        context_path.write_text(json.dumps(context))
        with self.assertRaisesRegex(BundleError, "not ordered"): input_plan(bundle)
        # The 32 KiB context size safety is unchanged.
        bundle, summary = fresh()
        entry = summary["images"][1]
        context_path = bundle / entry["frameContextPath"]
        context = json.loads(context_path.read_text())
        context_path.write_text(json.dumps(context) + " " * 40_000)  # valid JSON, over 32 KiB
        with self.assertRaisesRegex(BundleError, "size limit"): input_plan(bundle)

    def test_L_preflight_stops_a_bridge_event_that_is_not_detected_missed_detected(self):
        tmp = tempfile.TemporaryDirectory(); self.addCleanup(tmp.cleanup)
        bundle = self.bundle(Path(tmp.name), "bridge")
        plan = input_plan(bundle)
        before = next(i for i in plan.images if i.role == "before_success")
        context = json.loads(before.context_path.read_text())
        for frame in context["frames"]:
            if frame["frameID"] == before.frame_id:
                frame["red"]["detectionSucceeded"] = False; frame["red"]["detected"] = False
        before.context_path.write_text(json.dumps(context))
        result = tracking_preflight(plan)
        self.assertEqual(result["status"], "PRECHECK_FAILED")
        self.assertIn("bridgeEvidenceIncomplete", result["reasonCodes"])
        with mock.patch("phone_saber_triage_codex.find_codex_binary") as cli, contextlib.redirect_stdout(io.StringIO()):
            outcome = analyze_bundle(bundle)
        cli.assert_not_called()
        self.assertEqual(outcome["status"], "precheck_failed")
        self.assertIn("bridgeEvidenceIncomplete", outcome["reasonCodes"])

    def test_M_real_recorder_contexts_fit_the_consumer_size_limit_and_are_compact(self):
        from phone_saber_triage_codex import MAX_CODEX_CONTEXT_BYTES
        for scenario in ("bridge", "stable", "candidate-switch", "raw-jump", "path-switch", "bridge-switch", "bridge-overlap"):
            bundle = Path(self.captures[scenario]["bundle"])
            for path in (bundle / "frames").glob("*.json"):
                data = path.read_bytes()
                self.assertLess(len(data), MAX_CODEX_CONTEXT_BYTES, f"{scenario}/{path.name}")
                self.assertNotIn(b"\n  ", data, "contexts are written without pretty-print indentation")
            self.assertNotIn(b"\n  ", (bundle / "summary.json").read_bytes())


    # --- candidate geometry: can the next capture tell CASE A / B / C apart? ---

    def audit_hints(self, scenario):
        from phone_saber_tracking_diagnostics import candidate_selection_audit
        plan = input_plan(Path(self.captures[scenario]["bundle"]))
        return plan, candidate_selection_audit(plan)

    def test_N_real_recorder_geometry_distinguishes_cases_a_b_and_c(self):
        _, switch = self.audit_hints("candidate-switch")     # a different, equally eligible candidate takes over
        a_rows = [r for r in switch if r["hint"] == "A"]
        self.assertEqual([r["frameID"] for r in a_rows], [1010])
        self.assertEqual(a_rows[0]["eligibleCandidateCount"], 2)
        self.assertFalse(a_rows[0]["candidatesTruncated"])
        self.assertIn("matchingAlternativeListIndex", a_rows[0])
        _, jump = self.audit_hints("raw-jump")                # same candidate, endpoints break
        self.assertEqual([r["hint"] for r in jump if r["hint"] != "none"][:1], ["C"])
        self.assertTrue(all(r["hint"] != "A" for r in jump))
        _, bridge = self.audit_hints("bridge")                # the candidate is not generated at all
        b_rows = [r for r in bridge if r["hint"] == "B"]
        self.assertEqual([(r["frameID"], r["bCause"]) for r in b_rows], [(1010, "notGenerated")])
        _, stable = self.audit_hints("stable")
        self.assertTrue(all(r["hint"] == "none" for r in stable))

    def test_N2_offline_selection_replay_reads_real_recorder_geometry(self):
        from phone_saber_selection_replay import Policy, gap_distribution, load_sequences, replay
        sequences = load_sequences([Path(self.captures["candidate-switch"]["bundle"])])
        self.assertTrue(sequences, "replay finds complete eligible lists in a real bundle")
        policy = Policy(margin=1e9, max_distance=0.5, min_iou=0.2, hold_frames=1)
        switches = [r for r in gap_distribution(sequences, policy) if not r["returnsToEarlierWinner"]]
        self.assertIn(1010, [r["frameID"] for r in switches])
        self.assertIn(1010, [c["frameID"] for c in replay(sequences, policy, 100)["changedFrames"]])
        stable = load_sequences([Path(self.captures["stable"]["bundle"])])
        self.assertEqual(replay(stable, policy, 100)["changedFrames"], [])

    def test_O_selected_frame_geometry_is_complete_reconciled_and_never_silently_cut(self):
        for scenario in ("candidate-switch", "raw-jump", "path-switch", "bridge", "bridge-switch", "bridge-overlap"):
            plan = input_plan(Path(self.captures[scenario]["bundle"]))
            seen = 0
            for image in plan.images:
                context = json.loads(image.context_path.read_text())
                if context.get("bridgeEvent", {}).get("auxiliary"):
                    continue
                selected = next(f for f in context["frames"] if f["frameID"] == image.frame_id)
                geometry = selected["red"].get("candidateGeometry")
                self.assertIsNotNone(geometry, f"{scenario} image {image.image_id} lacks geometry")
                seen += 1
                # Recorded eligible count == saved + explicitly omitted; the flag follows.
                self.assertEqual(geometry["eligibleCandidateCount"], selected["red"]["eligibleCandidateCount"])
                self.assertEqual(geometry["eligibleCandidateCount"],
                                 geometry["savedEligibleCount"] + geometry["eligibleOmittedCount"])
                self.assertEqual(geometry["candidatesTruncated"],
                                 geometry["eligibleOmittedCount"] + geometry["ineligibleOmittedCount"] > 0)
                for entry in geometry["candidates"]:
                    for key in ("centroid", "bbox", "componentArea", "sourceType", "finalScore", "scoreBreakdown",
                                "eligible", "rejectionReasons", "rawPCAEndpoints", "finalOutputEndpoints"):
                        self.assertIn(key, entry)
            self.assertGreater(seen, 0)

    def test_P_bridge_and_a_real_switch_share_the_image_budget_without_losing_either(self):
        from phone_saber_tracking_diagnostics import candidate_selection_audit
        capture = self.captures["bridge-switch"]
        tmp = tempfile.TemporaryDirectory(); self.addCleanup(tmp.cleanup)
        plan = input_plan(Path(capture["bundle"]))
        self.assertLessEqual(len(plan.images), 12)
        bridge = [i for i in plan.images if i.bridge_event_id is not None]
        tracking = [i for i in plan.images if i.failure_type.endswith("tracking_instability")]
        self.assertEqual([i.role for i in bridge],
                         ["before_success", "dropout", "annotated_dropout", "after_success"])
        self.assertGreaterEqual(len(tracking), 5)
        summary = json.loads((Path(capture["bundle"]) / "summary.json").read_text())
        window = summary["bridgeDropoutSummary"]["trackingWindow"]
        self.assertEqual((window["selected"], window["available"]), (len(tracking), 11))
        frames = sorted(i.frame_id for i in tracking)
        self.assertEqual(frames, list(range(frames[0], frames[0] + len(frames))), "contiguous window")
        peak = next(i for i in tracking if json.loads(i.context_path.read_text())["motionEvent"]["role"] == "peak")
        self.assertTrue(frames[0] < peak.frame_id < frames[-1])
        self.assertEqual(tracking_preflight(plan)["status"], "PASS")
        hints = [r for r in candidate_selection_audit(plan) if r["hint"] == "A"]
        self.assertTrue(hints, "the candidate-selection switch is still diagnosable")
        events = temporal_events(plan)
        self.assertEqual(len(events), 1)
        self.assertTrue(sufficient_for_tracking(events[0]))

    def test_Q_session_report_summarizes_a_real_recorder_bundle_read_only(self):
        from phone_saber_session_report import build_report, render_markdown
        bundle = Path(self.captures["bridge-switch"]["bundle"])
        before = sorted((p.relative_to(bundle).as_posix(), p.stat().st_mtime_ns)
                        for p in bundle.rglob("*") if p.is_file())
        report = build_report(bundle)
        text = render_markdown(report)
        self.assertEqual(sorted((p.relative_to(bundle).as_posix(), p.stat().st_mtime_ns)
                                for p in bundle.rglob("*") if p.is_file()), before)
        self.assertEqual(report["inputContract"], "PASS")
        self.assertEqual(report["errors"], [])
        self.assertEqual(len(report["bridgeEvents"]), 1)
        self.assertIn("## Bridge dropout events", text)
        self.assertIn(f"| {report['bridgeEvents'][0]['bridgeEventID']} | red |", text)
        self.assertGreaterEqual(report["caseHintCounts"].get("A", 0), 1)
        self.assertIn("CASE A frame", text)
        self.assertIn("bridgeDropoutSummary.trackingWindow: selected", text)
        self.assertEqual(report["trackingPreflight"]["status"], "PASS")
        # The macOS host harness has no os_proc_available_memory().
        self.assertTrue(report["memory"]["verdict"].startswith("unavailable"))
        self.assertGreater(report["selectionReplay"]["sequences"], 0)
        self.assertTrue(report["imagesToOpen"]["bridgeOriginals"])
        self.assertTrue(report["imagesToOpen"]["annotatedViewingAidOnly"])
        # The real recorder always writes segmentSummary and per-context segmentLabel;
        # the harness never changes the label, so every frame is unlabeled.
        segments = report["segments"]
        self.assertTrue(segments["available"])
        self.assertEqual(segments["markers"], [])
        self.assertEqual(segments["labels"]["unlabeled"]["frames"], segments["totalFrames"])
        self.assertEqual(segments["totalFrames"], report["recordedFrameCount"])
        self.assertEqual(segments["redFalsePositiveVerdict"]["verdict"], "n/a")
        self.assertTrue(segments["images"])
        self.assertTrue(all(i["segmentLabel"] == "unlabeled" and i["labelSource"] == "context"
                            for i in segments["images"]))
        self.assertEqual(segments["backgroundOnlyImages"], [])
        self.assertGreaterEqual(report["caseHintCountsBySegment"]["unlabeled"].get("A", 0), 1)
        self.assertIn("## 区間ラベル(ground truth)", text)

    def test_R_bridge_event_inside_the_tracking_window_keeps_the_window_and_its_peak(self):
        # The loss (before 1013, dropout 1014, after 1015) shares frames with the
        # switch's window (peak 1016): the tracking PNGs of those frames still count.
        capture = self.captures["bridge-overlap"]
        plan = input_plan(Path(capture["bundle"]))
        self.assertEqual(len(plan.images), 12)
        self.assertEqual(len({i.image_path for i in plan.images}), len(plan.images))
        bridge = [i for i in plan.images if i.bridge_event_id is not None]
        tracking = [i for i in plan.images if i.failure_type.endswith("tracking_instability")]
        self.assertEqual([i.role for i in bridge],
                         ["before_success", "dropout", "annotated_dropout", "after_success"])
        frames = sorted(i.frame_id for i in tracking)
        self.assertEqual(len(frames), 8)
        self.assertEqual(frames, list(range(frames[0], frames[0] + len(frames))), "contiguous window")
        self.assertTrue({i.frame_id for i in bridge} & set(frames), "bridge and window overlap")
        summary = json.loads((Path(capture["bundle"]) / "summary.json").read_text())
        window = summary["bridgeDropoutSummary"]["trackingWindow"]
        self.assertEqual((window["selected"], window["available"]), (len(tracking), 11))
        peak = next(i for i in tracking if json.loads(i.context_path.read_text())["motionEvent"]["role"] == "peak")
        self.assertTrue(frames[0] < peak.frame_id < frames[-1])
        self.assertIn(peak.frame_id - 1, {i.frame_id for i in bridge}, "the peak's onset is a bridge frame")
        self.assertEqual(tracking_preflight(plan)["status"], "PASS")
        events = temporal_events(plan)
        self.assertEqual(len(events), 1)
        self.assertTrue(sufficient_for_tracking(events[0]))

    def test_S_background_evidence_section_renders_from_a_real_recorder_bundle(self):
        import shutil
        from phone_saber_session_report import build_report, render_markdown
        from test_phone_saber_emitter_diagnostics import CAMERA, EMITTER, GEOMETRY_EMITTER
        heading = "## 背景誤検出の証拠(emitter / shadow R7e / 露出)"
        source = Path(self.captures["candidate-switch"]["bundle"])
        report = build_report(source)
        self.assertEqual(report["errors"], [])
        evidence = report["backgroundEvidence"]
        # The host harness builds candidates without a radiance map and appends pixel
        # buffers without Exif attachments, so the recorder writes neither field.
        self.assertFalse(evidence["emitterEvidenceAvailable"])
        self.assertFalse(evidence["exposure"]["available"])
        self.assertGreater(len(evidence["winners"]), 0, "selected-frame winners are found in real contexts")
        self.assertTrue(all(w["evidence"] is None and w["clusterID"] for w in evidence["winners"]))
        text = render_markdown(report)
        self.assertIn(heading + "\n\n- **evidence only, NOT ground truth**", text)
        self.assertIn("- n/a — no `emitterDiagnostics`", text)
        # Add the fields, exactly where the schema puts them, to a copy of the real
        # bundle: the strict input contract accepts them and the section reads them.
        with tempfile.TemporaryDirectory() as tmp:
            bundle = Path(tmp) / source.name
            shutil.copytree(source, bundle)
            for path in sorted((bundle / "frames").glob("*.json")):
                context = json.loads(path.read_text())
                selected = next(f for f in context["frames"] if f["frameID"] == context["selectedFrameID"])
                selected["camera"] = copy.deepcopy(CAMERA)
                for entry in selected["red"].get("candidateDecisionTrace", []):
                    entry["emitterDiagnostics"] = copy.deepcopy(EMITTER)
                for entry in (selected["red"].get("candidateGeometry") or {}).get("candidates", []):
                    entry["emitter"] = copy.deepcopy(GEOMETRY_EMITTER)
                path.write_text(json.dumps(context, separators=(",", ":")))
            input_plan(bundle)
            augmented = build_report(bundle)
            self.assertEqual(augmented["errors"], [])
            evidence = augmented["backgroundEvidence"]
            red = [w for w in evidence["winners"] if w["color"] == "red"]
            self.assertTrue(red and all(w["evidence"] for w in red))
            self.assertTrue(all(w["r7eVerdict"] == "reject" for w in red))  # SHADOW: shadowR7eEligible false
            self.assertEqual(evidence["shadowR7eTally"]["total"]["r7eWouldReject"], len(red))
            # Every red winner gets a PF22 verdict (recomputed from the recorder's own
            # meanColorPurity / clippedWhiteRatio, which win over the added geometry copies).
            self.assertTrue(all(w["pf22Verdict"] in {"keep", "reject"} for w in red))
            pf22 = evidence["shadowPF22Tally"]["selectedWinnersTotal"]
            self.assertEqual(pf22["pf22Keeps"] + pf22["pf22WouldReject"], len(red))
            self.assertEqual(evidence["exposure"]["iso"]["range"], [320.0, 320.0])
            text = render_markdown(augmented)
            section = text.split(heading)[1].split("\n## ")[0]
            self.assertIn("### shadow R7e tally", section)
            self.assertIn("### shadow PF22 tally", section)
            self.assertIn("**would reject**", section)
            self.assertIn("- ISO 320–320", section)


if __name__ == "__main__": unittest.main()
