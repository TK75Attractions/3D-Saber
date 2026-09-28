#!/usr/bin/env python3
"""Safety tests for the bounded automatic repair state machine."""

from __future__ import annotations

import contextlib
import copy
import io
import json
import subprocess
import sys
import tempfile
import time
import unittest
from http.client import HTTPConnection
from pathlib import Path
from threading import Thread
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import phone_saber_auto_repair as repair
from phone_saber_triage_codex import input_plan
from test_phone_saber_triage_codex import EMPTY_ANALYSIS, fake_codex, write_codex_bundle
from phone_saber_triage_codex import ANALYSIS_MODEL, ANALYSIS_REASONING_EFFORT
from phone_saber_triage_protocol import CONTENT_TYPE
from phone_saber_triage_receiver import TriageHTTPServer


SOURCE = repair.REPAIR_FILES[0]
BASE = "a" * 40


def prepared_bundle(root: Path, *, actionable: bool = True, count: int = 1,
                    threshold: bool = False) -> Path:
    bundle = root / "phone_saber_triage_sample_session"
    write_codex_bundle(bundle, image_count=count,
                       failure_types=tuple(f"incident_{i}" for i in range(count)))
    bundle = bundle.resolve()
    plan = input_plan(bundle)
    analysis = copy.deepcopy(EMPTY_ANALYSIS)
    if actionable:
        ids = list(plan.image_ids)
        analysis["repair_assessment"] = {
            "decision": "actionable", "visible_saber_confirmed": True,
            "production_change_supported": True,
            "root_cause_stage": "candidate_generation",
            "diagnosis_consistent_with_metadata": True,
            "change_type": "threshold" if threshold else "candidate_logic",
            "independent_visual_examples": count,
            "affected_colors": ["RED"], "evidence_image_ids": ids,
            "reason": "Visible RED saber and candidate-generation dropout.",
        }
        analysis["false_negatives"] = [{
            "classification": ["B"], "color": "RED",
            "frame_ids": [image.frame_id for image in plan.images],
            "image_ids": ids, "issue_type": "candidate missing",
            "observation": "Visible RED saber", "interpretation": "candidate generation",
            "confidence": "high",
        }]
    references = [{"id": image.image_id,
                   "path": image.image_path.relative_to(bundle).as_posix(),
                   "contextPath": image.context_path.relative_to(bundle).as_posix(),
                   "frameID": image.frame_id, "incidentType": image.failure_type}
                  for image in plan.images]
    report = {"formatVersion": 3, "sessionID": plan.session_id,
              "analysisModel": ANALYSIS_MODEL,
              "analysisReasoningEffort": ANALYSIS_REASONING_EFFORT,
              "analysisExecuted": True,
              "input": {"imageCount": len(references),
                        "imagePaths": [item["path"] for item in references],
                        "contextPaths": [item["contextPath"] for item in references],
                        "imageReferences": references,
                        "videoIncluded": False, "fullMetadataIncluded": False},
              "analysis": analysis}
    (bundle / "analysis_report.json").write_text(json.dumps(report), encoding="utf-8")
    (bundle / "analysis_report.md").write_text("# Analysis\n", encoding="utf-8")
    return bundle


def miniature_repo(root: Path) -> Path:
    repo = root / "repo"
    for relative in repair.REPAIR_FILES:
        target = repo / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(f"original {relative}\n", encoding="utf-8")
    manifest = "ios/PhoneSaberSender/Tools/lossless_regression_manifest.json"
    (repo / manifest).write_bytes((repair.REPO_ROOT / manifest).read_bytes())
    return repo


def fake_git(_repo: Path, *args: str, **_kwargs: object) -> str:
    if args[:2] == ("rev-parse", "HEAD"):
        return BASE
    if args[:2] == ("status", "--porcelain"):
        return ""
    return ""


class RepairGateTests(unittest.TestCase):
    def test_receiver_runs_analysis_then_repair_gate_automatically(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inbox = root / "inbox"
            inbox.mkdir()
            executable = fake_codex(root, root / "spy.json", analysis=EMPTY_ANALYSIS)
            server = TriageHTTPServer(("127.0.0.1", 0), inbox,
                                      analysis_mode="automatic", codex_path=str(executable))
            thread = Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                body = write_codex_bundle(root / "bundle")
                connection = HTTPConnection("127.0.0.1", server.server_port, timeout=3)
                connection.request("POST", "/v1/bundle", body=body, headers={
                    "Content-Type": CONTENT_TYPE, "Content-Length": str(len(body)),
                    "Connection": "close",
                })
                response = connection.getresponse()
                self.assertEqual(response.status, 201)
                response.read()
                connection.close()
                server.analysis_queue.join()
                received = inbox / "phone_saber_triage_sample_session"
                self.assertTrue((received / "analysis_report.json").is_file())
                status = json.loads((received / "repair_status.json").read_text())
                self.assertEqual(status["status"], "needs_capture")
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)

    def test_legacy_or_invisible_saber_never_repairs(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = prepared_bundle(root, actionable=False)
            with mock.patch.object(repair, "main_safety_gate") as safety:
                with contextlib.redirect_stdout(io.StringIO()):
                    result = repair.repair_bundle(bundle, repo=repair.REPO_ROOT, dry_run=True)
            self.assertEqual(result["status"], "needs_capture")
            safety.assert_not_called()
            self.assertEqual(repair.repair_bundle(bundle, repo=repair.REPO_ROOT)["status"],
                             "needs_capture")

    def test_threshold_needs_two_distinct_examples(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            one = prepared_bundle(root, count=1, threshold=True)
            result = repair.repair_gate(repair.load_analysis(one, input_plan(one, allow_reports=True)),
                                        input_plan(one, allow_reports=True), repair.REPO_ROOT)
            self.assertEqual(result["decision"], "needs_capture")
            self.assertIn("two distinct", " ".join(result["reasons"]))

    def test_dry_run_does_not_call_codex_or_change_source_and_can_resume_real_run(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = prepared_bundle(root)
            repo = miniature_repo(root)
            before = (repo / SOURCE).read_bytes()
            with mock.patch.object(repair, "main_safety_gate", return_value={
                    "head": BASE, "origin": BASE}) as safety, \
                    mock.patch.object(repair, "find_codex_binary") as codex:
                result = repair.repair_bundle(bundle, repo=repo, dry_run=True)
            self.assertEqual(result["status"], "dry_run")
            self.assertEqual((repo / SOURCE).read_bytes(), before)
            safety.assert_called_once()
            codex.assert_not_called()
            plan = json.loads((bundle / "repair_report.json").read_text())["dryRunPlan"]
            self.assertIn("./tools/verify_phone_saber.sh", plan["verificationCommands"])
            self.assertIn("recognition", plan["repairPrompt"])
            self.assertEqual(plan["models"]["repair"], {
                "model": repair.REPAIR_MODEL,
                "reasoningEffort": repair.REPAIR_REASONING_EFFORT,
                "executed": False,
            })
            self.assertEqual(plan["models"]["review"], {
                "model": repair.REVIEW_MODEL,
                "reasoningEffort": repair.REVIEW_REASONING_EFFORT,
                "executed": False,
            })
            status = json.loads((bundle / "repair_status.json").read_text())
            self.assertEqual(status["analysisModel"], ANALYSIS_MODEL)
            self.assertEqual(status["analysisReasoningEffort"], ANALYSIS_REASONING_EFFORT)
            self.assertEqual(status["repairModel"], repair.REPAIR_MODEL)
            self.assertEqual(status["repairReasoningEffort"], repair.REPAIR_REASONING_EFFORT)
            self.assertFalse(status["repairExecuted"])
            self.assertFalse(status["reviewExecuted"])

    def test_interrupted_owned_edit_is_restored_without_starting_another_attempt(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = prepared_bundle(root)
            repo = miniature_repo(root)
            original = (repo / SOURCE).read_bytes()
            private = bundle.parent / ".phonesaber-repair-sample_session"
            backup = private / "backup" / SOURCE
            backup.parent.mkdir(parents=True)
            backup.write_bytes(original)
            (repo / SOURCE).write_text("interrupted candidate\n")
            planned = repair._sha256(repo / SOURCE)
            state = {"sessionID": "sample_session", "status": "running", "phase": "applying",
                     "startedAt": 0, "ownedFiles": {SOURCE: {
                         "original": repair._sha256(backup), "planned": planned,
                     }}}
            (bundle / "state.json").write_text(json.dumps(state))
            with mock.patch.object(repair, "find_codex_binary") as codex:
                result = repair.repair_bundle(bundle, repo=repo)
            self.assertEqual(result["status"], "repair_failed")
            self.assertEqual((repo / SOURCE).read_bytes(), original)
            codex.assert_not_called()

    def test_actionable_gate_reports_blocked_when_main_preflight_fails(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = prepared_bundle(root)
            repo = miniature_repo(root)
            original = (repo / SOURCE).read_bytes()
            with mock.patch.object(repair, "main_safety_gate", side_effect=repair.RepairError(
                    "BLOCKED main safety gate: branch=other, dirty=True, ahead/behind=0 1")), \
                    mock.patch.object(repair, "find_codex_binary") as codex:
                result = repair.repair_bundle(bundle, repo=repo)
            self.assertEqual(result["status"], "blocked")
            self.assertEqual((repo / SOURCE).read_bytes(), original)
            codex.assert_not_called()


class RepairPipelineTests(unittest.TestCase):
    def _run(self, *, codex_error: Exception | None = None,
             verification_error: Exception | None = None,
             review_decision: str = "approved", remote_error: bool = False,
             success: bool = False) -> tuple[dict, Path, Path, mock.Mock]:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        root = Path(temporary.name)
        bundle = prepared_bundle(root)
        repo = miniature_repo(root)
        calls = mock.Mock()

        def codex(_binary: str, scratch: Path, _prompt: str, schema: dict,
                  _images: list[Path], **_kwargs: object) -> dict:
            calls.codex()
            if codex_error:
                raise codex_error
            with (scratch / SOURCE).open("a", encoding="utf-8") as handle:
                handle.write("recognition fix\n")
            return {"decision": "changes_made", "summary": "candidate fix"}

        def verify(*_args: object) -> dict:
            calls.verify()
            if verification_error:
                raise verification_error
            return {"passed": True, "stages": {"Tools": "PASS"}}

        def review(*_args: object) -> dict:
            calls.review()
            return {"decision": review_decision, "reason": "reviewed", "risk_notes": []}

        def commit(*_args: object) -> str:
            calls.commit()
            if remote_error:
                raise repair.RepairError("BLOCKED_REMOTE_CHANGED: origin moved")
            return "b" * 40

        with mock.patch.object(repair, "main_safety_gate", return_value={
                "head": BASE, "origin": BASE}), \
                mock.patch.object(repair, "find_codex_binary", return_value="codex"), \
                mock.patch.object(repair, "_baseline", return_value={
                    "summary": {"passed": 40}}), \
                mock.patch.object(repair, "_codex_call", side_effect=codex), \
                mock.patch.object(repair, "_verify", side_effect=verify), \
                mock.patch.object(repair, "_current_diff", return_value="diff --git"), \
                mock.patch.object(repair, "_review", side_effect=review), \
                mock.patch.object(repair, "_commit_push", side_effect=commit), \
                mock.patch.object(repair, "_git", side_effect=fake_git):
            result = repair.repair_bundle(bundle, repo=repo)
            repeated = repair.repair_bundle(bundle, repo=repo)
        self.assertEqual(result, repeated)
        self.assertEqual(calls.commit.call_count, 1 if success or remote_error else 0)
        if not success:
            self.assertEqual((repo / SOURCE).read_text(), f"original {SOURCE}\n")
        return result, bundle, repo, calls

    def test_codex_failure_and_timeout_preserve_source(self) -> None:
        for exception in (repair.RepairError("Codex exit 9"), repair.RepairTimeout("Codex timed out")):
            with self.subTest(exception=exception):
                result, _bundle, _repo, calls = self._run(codex_error=exception)
                self.assertEqual(result["status"], "repair_failed")
                self.assertEqual(calls.codex.call_count, 1)

    def test_model_unavailable_stops_with_explicit_status_and_no_fallback(self) -> None:
        result, bundle, _repo, calls = self._run(
            codex_error=repair.RepairModelUnavailable("MODEL_UNAVAILABLE repair model"))
        self.assertEqual(result["status"], "MODEL_UNAVAILABLE")
        self.assertEqual(calls.codex.call_count, 1)
        status = json.loads((bundle / "repair_status.json").read_text())
        self.assertEqual(status["repairModel"], repair.REPAIR_MODEL)
        self.assertFalse(status["repairExecuted"])
        self.assertFalse(status["reviewExecuted"])

    def test_analysis_model_unavailable_report_marks_planned_roles_unexecuted(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = prepared_bundle(Path(directory))
            result = repair.record_analysis_model_unavailable(
                bundle, "MODEL_UNAVAILABLE: analysis requires gpt-6-luna/max")
            self.assertEqual(result["status"], "MODEL_UNAVAILABLE")
            final = json.loads((bundle / "final_report.json").read_text())
            self.assertEqual(final["analysisModel"], ANALYSIS_MODEL)
            self.assertEqual(final["analysisReasoningEffort"], ANALYSIS_REASONING_EFFORT)
            self.assertTrue(final["analysisAttempted"])
            self.assertFalse(final["analysisExecuted"])
            self.assertFalse(final["repairExecuted"])
            self.assertFalse(final["reviewExecuted"])

    def test_verification_failure_uses_exactly_two_attempts_then_restores(self) -> None:
        result, bundle, _repo, calls = self._run(
            verification_error=repair.RepairError("XCTest failed"))
        self.assertEqual(result["status"], "repair_failed")
        self.assertEqual(result["attempts"], 2)
        self.assertEqual(calls.codex.call_count, 2)
        self.assertEqual(calls.verify.call_count, 2)
        self.assertEqual(json.loads((bundle / "state.json").read_text())["phase"], "done")

    def test_review_rejection_stops_without_retry_or_commit(self) -> None:
        result, _bundle, _repo, calls = self._run(review_decision="rejected")
        self.assertEqual(result["status"], "repair_failed")
        self.assertEqual(calls.review.call_count, 1)

    def test_review_needs_more_evidence_preserves_source(self) -> None:
        result, _bundle, _repo, calls = self._run(review_decision="needs_more_evidence")
        self.assertEqual(result["status"], "needs_capture")
        self.assertEqual(calls.review.call_count, 1)

    def test_remote_change_blocks_and_restores(self) -> None:
        result, _bundle, _repo, calls = self._run(remote_error=True)
        self.assertEqual(result["status"], "blocked_remote_changed")
        self.assertEqual(calls.commit.call_count, 1)

    def test_approved_path_records_pushed_result(self) -> None:
        result, bundle, _repo, calls = self._run(success=True)
        self.assertEqual(result["status"], "repair_pushed")
        self.assertEqual(calls.commit.call_count, 1)
        self.assertTrue((bundle / "final_report.md").is_file())
        final = json.loads((bundle / "final_report.json").read_text())
        self.assertEqual(final["analysisModel"], ANALYSIS_MODEL)
        self.assertEqual(final["analysisReasoningEffort"], ANALYSIS_REASONING_EFFORT)
        self.assertTrue(final["analysisExecuted"])
        self.assertEqual(final["repairModel"], repair.REPAIR_MODEL)
        self.assertEqual(final["repairReasoningEffort"], repair.REPAIR_REASONING_EFFORT)
        self.assertTrue(final["repairExecuted"])
        self.assertEqual(final["reviewModel"], repair.REVIEW_MODEL)
        self.assertEqual(final["reviewReasoningEffort"], repair.REVIEW_REASONING_EFFORT)
        self.assertTrue(final["reviewExecuted"])


class VerificationAndIsolationTests(unittest.TestCase):
    def test_formal_manifest_cannot_weaken_existing_fixture(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            private, scratch = root / "private", root / "scratch"
            private.mkdir()
            manifest = scratch / "ios/PhoneSaberSender/Tools/lossless_regression_manifest.json"
            manifest.parent.mkdir(parents=True)
            original = (repair.REPO_ROOT /
                        "ios/PhoneSaberSender/Tools/lossless_regression_manifest.json").read_bytes()
            (private / "pinned_lossless_manifest.json").write_bytes(original)
            manifest.write_bytes(original)
            repair._protect_formal_manifest(private, scratch)
            candidate = json.loads(original)
            candidate["fixtures"][0]["expectedDetected"] = False
            manifest.write_text(json.dumps(candidate))
            with self.assertRaisesRegex(repair.RepairError, "protected fixture changed"):
                repair._protect_formal_manifest(private, scratch)

    def test_repair_and_reviewer_use_separate_bounded_codex_calls(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            scratch = root / "candidate"
            scratch.mkdir()
            image = scratch / "selected.png"
            image.write_bytes(b"selected")
            spy = root / "calls.jsonl"
            executable = root / "codex-spy"
            executable.write_text(
                f"#!{sys.executable}\n"
                "import json, pathlib, sys\n"
                "args = sys.argv[1:]\n"
                "mode = args[args.index('--sandbox') + 1]\n"
                "model = args[args.index('--model') + 1]\n"
                "effort = args[args.index('-c') + 1]\n"
                "images = [args[i+1] for i, v in enumerate(args[:-1]) if v == '--image']\n"
                f"with pathlib.Path({str(spy)!r}).open('a') as out:\n"
                "    out.write(json.dumps({'mode': mode, 'model': model, 'effort': effort, "
                "'images': images, 'stdin': sys.stdin.read(), "
                "'ephemeral': '--ephemeral' in args}) + '\\n')\n"
                "response = {'decision': 'changes_made', 'summary': 'fixed'} if mode == "
                "'workspace-write' else {'decision': 'approved', 'reason': 'safe', 'risk_notes': []}\n"
                "pathlib.Path(args[args.index('--output-last-message') + 1]).write_text(json.dumps(response))\n",
                encoding="utf-8")
            executable.chmod(0o755)
            first = repair._codex_call(str(executable), scratch, "repair prompt",
                                       repair.REPAIR_RESPONSE_SCHEMA, [image],
                                       role="repair", writable=True, timeout=10)
            second = repair._codex_call(str(executable), scratch, "review prompt",
                                        repair.REVIEW_RESPONSE_SCHEMA, [image],
                                        role="review", writable=False, timeout=10)
            calls = [json.loads(line) for line in spy.read_text().splitlines()]
            self.assertEqual((first["decision"], second["decision"]),
                             ("changes_made", "approved"))
            self.assertEqual([call["mode"] for call in calls], ["workspace-write", "read-only"])
            self.assertEqual([call["model"] for call in calls],
                             [repair.REPAIR_MODEL, repair.REVIEW_MODEL])
            self.assertEqual([call["effort"] for call in calls], [
                f'model_reasoning_effort="{repair.REPAIR_REASONING_EFFORT}"',
                f'model_reasoning_effort="{repair.REVIEW_REASONING_EFFORT}"',
            ])
            self.assertEqual([call["stdin"] for call in calls], ["repair prompt", "review prompt"])
            self.assertEqual(calls[0]["images"], [str(image)])
            self.assertTrue(all(call["ephemeral"] for call in calls))

    def test_repair_and_reviewer_model_rejection_never_retries_with_defaults(self) -> None:
        for role, model, effort, schema, writable in (
                ("repair", repair.REPAIR_MODEL, repair.REPAIR_REASONING_EFFORT,
                 repair.REPAIR_RESPONSE_SCHEMA, True),
                ("review", repair.REVIEW_MODEL, repair.REVIEW_REASONING_EFFORT,
                 repair.REVIEW_RESPONSE_SCHEMA, False)):
            with self.subTest(role=role), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                scratch = root / "candidate"
                scratch.mkdir()
                spy = root / "calls.jsonl"
                executable = root / "codex-reject-model"
                executable.write_text(
                    f"#!{sys.executable}\n"
                    "import json, pathlib, sys\n"
                    "args = sys.argv[1:]\n"
                    "record = {'args': args, 'model': args[args.index('--model') + 1], "
                    "'effort': args[args.index('-c') + 1]}\n"
                    f"with pathlib.Path({str(spy)!r}).open('a') as out:\n"
                    "    out.write(json.dumps(record) + '\\n')\n"
                    "print('unknown model requested', file=sys.stderr)\n"
                    "raise SystemExit(1)\n",
                    encoding="utf-8")
                executable.chmod(0o755)
                with self.assertRaisesRegex(repair.RepairModelUnavailable, "MODEL_UNAVAILABLE"):
                    repair._codex_call(str(executable), scratch, f"{role} prompt", schema, [],
                                       role=role, writable=writable, timeout=10)
                calls = [json.loads(line) for line in spy.read_text().splitlines()]
                self.assertEqual(len(calls), 1)
                self.assertEqual(calls[0]["model"], model)
                self.assertEqual(calls[0]["effort"], f'model_reasoning_effort="{effort}"')
                self.assertEqual(calls[0]["args"].count("--model"), 1)
                self.assertEqual(calls[0]["args"].count("-c"), 1)

    def test_known_good_output_change_fails_but_candidate_metrics_are_reported(self) -> None:
        before = {"fixtures": [{"name": "known", "passed": True, "detected": True,
                                "candidateType": "core", "endpoint": [1, 2],
                                "candidateCount": 2, "candidateScores": [4, 3],
                                "selectedScore": 4}]}
        after = copy.deepcopy(before)
        after["fixtures"][0]["candidateScores"][0] += 1e-12
        self.assertEqual(repair._compare_regression(before, after)["candidateDiagnosticChanges"], [])
        after["fixtures"][0]["candidateCount"] = 3
        comparison = repair._compare_regression(before, after)
        self.assertEqual(comparison["candidateDiagnosticChanges"][0]["fixture"], "known")
        after["fixtures"][0]["detected"] = False
        with self.assertRaisesRegex(repair.RepairError, "protected recognition outputs changed"):
            repair._compare_regression(before, after)

    def test_verify_parser_accepts_unity_blocked_only_when_required_stages_pass(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fixture = {"fixtures": [], "summary": {"fixture_count": 40, "failed": 0}}
            lines = [f"{stage:<20} PASS" for stage in repair.REQUIRED_VERIFY_STAGES]
            lines += ["Unity EditMode       BLOCKED", "Unity PlayMode       BLOCKED",
                      "Unity Compile        BLOCKED", "Unity capability     BLOCKED"]
            completed = subprocess.CompletedProcess([], 2, "\n".join(lines), "")
            with mock.patch.object(repair, "_formal_regression", return_value=fixture), \
                    mock.patch.object(repair, "_run", return_value=completed):
                self.assertTrue(repair._verify(root, root, fixture, 1)["passed"])
            bad = subprocess.CompletedProcess([], 1, "\n".join(lines).replace(
                "Detection            PASS", "Detection            FAIL"), "")
            with mock.patch.object(repair, "_formal_regression", return_value=fixture), \
                    mock.patch.object(repair, "_run", return_value=bad):
                with self.assertRaisesRegex(repair.RepairError, "verification stages"):
                    repair._verify(root, root, fixture, 2)

    def test_candidate_cannot_change_evidence_or_create_unlisted_files(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            scratch = root / "scratch"
            scratch.mkdir()
            source = scratch / SOURCE
            source.parent.mkdir(parents=True)
            source.write_text("original\n")
            evidence = scratch / "evidence/summary.json"
            evidence.parent.mkdir()
            evidence.write_text("{}")
            original = {SOURCE: repair._sha256(source)}
            hashes = {"evidence/summary.json": repair._sha256(evidence)}
            self.assertEqual(repair._candidate_files(scratch, original, hashes), [])
            evidence.write_text("{\"changed\": true}")
            with self.assertRaisesRegex(repair.RepairError, "read-only evidence"):
                repair._candidate_files(scratch, original, hashes)
            evidence.write_text("{}")
            (scratch / "forbidden.swift").write_text("bad")
            with self.assertRaisesRegex(repair.RepairError, "unauthorized file"):
                repair._candidate_files(scratch, original, hashes)


class GitSafetyTests(unittest.TestCase):
    def test_real_commit_push_uses_main_and_stops_if_origin_moves(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            origin, repo, bundle = root / "origin.git", root / "repo", root / "bundle"
            bundle.mkdir()
            subprocess.run(["git", "init", "--bare", str(origin)], check=True,
                           capture_output=True)
            subprocess.run(["git", "init", "-b", "main", str(repo)], check=True,
                           capture_output=True)
            def git(location: Path, *args: str) -> str:
                completed = subprocess.run(["git", *args], cwd=location, check=True,
                                           capture_output=True, text=True)
                return completed.stdout.strip()
            git(repo, "config", "user.name", "Test")
            git(repo, "config", "user.email", "test@example.com")
            git(repo, "remote", "add", "origin", str(origin))
            source = repo / SOURCE
            source.parent.mkdir(parents=True)
            source.write_text("baseline\n")
            (repo / "unrelated.txt").write_text("untouched\n")
            git(repo, "add", SOURCE, "unrelated.txt")
            git(repo, "commit", "-m", "baseline")
            git(repo, "push", "-u", "origin", "main")
            base = git(repo, "rev-parse", "HEAD")
            source.write_text("candidate\n")
            state = {"sessionID": "sample_session", "baseHead": base, "originHead": base,
                     "startedAt": time.time()}
            commit = repair._commit_push(repo, bundle, state, [SOURCE])
            self.assertEqual(git(origin, "rev-parse", "refs/heads/main"), commit)
            self.assertEqual(git(repo, "show", "--pretty=format:", "--name-only", "HEAD"), SOURCE)
            self.assertEqual(git(repo, "status", "--porcelain"), "")

            source.write_text("second candidate\n")
            peer = root / "peer"
            subprocess.run(["git", "clone", "-b", "main", str(origin), str(peer)],
                           check=True, capture_output=True)
            git(peer, "config", "user.name", "Peer")
            git(peer, "config", "user.email", "peer@example.com")
            (peer / "unrelated.txt").write_text("remote update\n")
            git(peer, "add", "unrelated.txt")
            git(peer, "commit", "-m", "remote update")
            git(peer, "push", "origin", "main")
            second = {"sessionID": "sample_session", "baseHead": commit,
                      "originHead": commit, "startedAt": time.time()}
            with self.assertRaisesRegex(repair.RepairError, "BLOCKED_REMOTE_CHANGED"):
                repair._commit_push(repo, bundle, second, [SOURCE])
            self.assertEqual(git(repo, "rev-parse", "HEAD"), commit)
            self.assertEqual(git(repo, "diff", "--name-only"), SOURCE)
            git(repo, "add", SOURCE)
            git(repo, "commit", "-m", "Repair PhoneSaber recognition from sample_session")
            unpushed = git(repo, "rev-parse", "HEAD")
            rollback_state = {"commit": unpushed, "baseHead": commit}
            repair._uncommit_unpushed(repo, rollback_state, [SOURCE])
            self.assertEqual(git(repo, "rev-parse", "HEAD"), commit)
            self.assertEqual(git(repo, "diff", "--cached", "--name-only"), "")
            self.assertEqual(git(repo, "diff", "--name-only"), SOURCE)

    def test_main_clean_and_remote_equal_are_required(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            origin, repo = root / "origin.git", root / "repo"
            subprocess.run(["git", "init", "--bare", str(origin)], check=True,
                           capture_output=True)
            subprocess.run(["git", "init", "-b", "main", str(repo)], check=True,
                           capture_output=True)
            def git(*args: str) -> str:
                result = subprocess.run(["git", *args], cwd=repo, check=True,
                                        capture_output=True, text=True)
                return result.stdout.strip()
            git("config", "user.name", "Test")
            git("config", "user.email", "test@example.com")
            git("remote", "add", "origin", str(origin))
            (repo / "file.txt").write_text("first\n")
            git("add", "file.txt")
            git("commit", "-m", "first")
            git("push", "-u", "origin", "main")
            self.assertEqual(repair.main_safety_gate(repo)["distance"], "0\t0")
            (repo / "file.txt").write_text("dirty\n")
            with self.assertRaisesRegex(repair.RepairError, "dirty=True"):
                repair.main_safety_gate(repo)
            git("restore", "file.txt")
            git("checkout", "-b", "other")
            with self.assertRaisesRegex(repair.RepairError, "branch=other"):
                repair.main_safety_gate(repo)
            git("checkout", "main")
            peer = root / "peer"
            subprocess.run(["git", "clone", "-b", "main", str(origin), str(peer)],
                           check=True, capture_output=True)
            subprocess.run(["git", "config", "user.name", "Peer"], cwd=peer, check=True)
            subprocess.run(["git", "config", "user.email", "peer@example.com"],
                           cwd=peer, check=True)
            (peer / "file.txt").write_text("remote\n")
            subprocess.run(["git", "add", "file.txt"], cwd=peer, check=True)
            subprocess.run(["git", "commit", "-m", "remote"], cwd=peer,
                           check=True, capture_output=True)
            subprocess.run(["git", "push", "origin", "main"], cwd=peer,
                           check=True, capture_output=True)
            with self.assertRaisesRegex(repair.RepairError, "ahead/behind=0\\s+1"):
                repair.main_safety_gate(repo)
            (repo / "file.txt").write_text("ahead\n")
            git("add", "file.txt")
            git("commit", "-m", "ahead")
            with self.assertRaisesRegex(repair.RepairError, "ahead/behind"):
                repair.main_safety_gate(repo)


if __name__ == "__main__":
    unittest.main()
