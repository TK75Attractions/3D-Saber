"""Receiver lifecycle attribution without changing startup retry eligibility."""

from __future__ import annotations

import contextlib
import io
import json
import sys
import tempfile
import unittest
from http.client import HTTPConnection
from pathlib import Path
from threading import Thread
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import phone_saber_triage_codex as analysis
import phone_saber_triage_receiver as receiver
from phone_saber_codex_process import CodexProcessError
from phone_saber_session_log import log_fields, session_log_context
from phone_saber_triage_protocol import CONTENT_TYPE
from test_phone_saber_auto_repair import prepared_bundle
from test_phone_saber_triage_codex import EMPTY_ANALYSIS, fake_codex, write_codex_bundle


class ReceiverObservabilityTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.inbox = self.root / "inbox"
        self.inbox.mkdir()
        self.codex = fake_codex(self.root, self.root / "spy.json", analysis=EMPTY_ANALYSIS)

    def server(self, mode: str = "automatic") -> receiver.TriageHTTPServer:
        server = receiver.TriageHTTPServer(("127.0.0.1", 0), self.inbox,
                                           analysis_mode=mode, codex_path=str(self.codex))
        self.addCleanup(server.server_close)
        return server

    def assert_phases(self, output: str, source: str, *phases: str) -> None:
        for phase in phases:
            lines = [line for line in output.splitlines() if f"[AUTO_REPAIR][{phase}]" in line]
            self.assertTrue(lines, f"missing {phase}: {output}")
            for line in lines:
                self.assertIn("sessionID=sample_session", line)
                self.assertIn(f"source={source}", line)

    def test_new_post_logs_upload_source_through_done(self) -> None:
        server = self.server()
        thread = Thread(target=server.serve_forever, daemon=True)
        thread.start()
        output = io.StringIO()
        try:
            with contextlib.redirect_stdout(output):
                body = write_codex_bundle(self.root / "upload")
                connection = HTTPConnection("127.0.0.1", server.server_port, timeout=3)
                try:
                    connection.request("POST", "/v1/bundle", body=body, headers={
                        "Content-Type": CONTENT_TYPE, "Content-Length": str(len(body)),
                    })
                    response = connection.getresponse()
                    self.assertEqual(response.status, 201)
                    response.read()
                finally:
                    connection.close()
                server.analysis_queue.join()
        finally:
            server.shutdown()
            thread.join(timeout=2)
        self.assert_phases(output.getvalue(), "new_upload", "PRECHECK", "ANALYSIS", "DONE")
        self.assertNotIn("[RESUME]", output.getvalue())

    def test_startup_resume_logs_reason_before_precheck_without_post(self) -> None:
        bundle = self.inbox / "phone_saber_triage_sample_session"
        write_codex_bundle(bundle)
        server = self.server()
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            server.resume_existing_sessions()
            server.analysis_queue.join()
        log = output.getvalue()
        self.assert_phases(log, "existing_inbox", "RESUME")
        self.assertIn("reason=repair_status_missing", log)
        self.assertIn("trigger=startup_resume", log)
        self.assertIn("analysis_report=missing state_file=missing", log)
        self.assertLess(log.index("[RESUME]"), log.index("[PRECHECK]"))
        self.assert_phases(log, "startup_resume", "PRECHECK", "ANALYSIS", "DONE")

    def test_completed_session_is_not_reprocessed_on_startup(self) -> None:
        bundle = self.inbox / "phone_saber_triage_sample_session"
        write_codex_bundle(bundle)
        server = self.server()
        with contextlib.redirect_stdout(io.StringIO()):
            server.resume_existing_sessions()
            server.analysis_queue.join()
        self.assertTrue(json.loads((bundle / "analysis_report.json").read_text())["analysisExecuted"])
        self.assertTrue((bundle / "repair_status.json").is_file())
        before = {p.name: p.read_bytes() for p in bundle.glob("*.json")}
        restarted = self.server()
        output = io.StringIO()
        with mock.patch.object(receiver, "analyze_bundle") as analyzer, \
                mock.patch.object(receiver, "repair_bundle") as repairer, \
                contextlib.redirect_stdout(output):
            restarted.resume_existing_sessions()
            restarted.analysis_queue.join()
        analyzer.assert_not_called()
        repairer.assert_not_called()
        self.assertEqual(output.getvalue(), "")
        self.assertEqual(before, {p.name: p.read_bytes() for p in bundle.glob("*.json")})

    def test_retryable_analysis_timeout_is_reprocessed_at_startup(self) -> None:
        bundle = self.inbox / "phone_saber_triage_sample_session"
        write_codex_bundle(bundle)
        server = self.server()
        output = io.StringIO()
        timed_out = mock.Mock(diagnostic={"error_code": "CLI_TIMEOUT"})
        timed_out.describe.return_value = "CLI_TIMEOUT: analysis timed out"
        with mock.patch.object(analysis, "run_codex", side_effect=CodexProcessError(timed_out)), \
                contextlib.redirect_stdout(output):
            server.enqueue_analysis(bundle, source="new_upload", reason="post_received")
            server.analysis_queue.join()
        self.assertIn("CLI_TIMEOUT", output.getvalue())
        self.assertFalse((bundle / "analysis_report.json").exists())
        self.assertFalse((bundle / "repair_status.json").exists())
        restarted = self.server()
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            restarted.resume_existing_sessions()
            restarted.analysis_queue.join()
        self.assert_phases(output.getvalue(), "existing_inbox", "RESUME")
        self.assert_phases(output.getvalue(), "startup_resume", "PRECHECK", "ANALYSIS", "DONE")
        self.assertTrue((bundle / "repair_status.json").is_file())

    def test_completed_analysis_without_repair_status_resumes_cached_analysis(self) -> None:
        bundle = prepared_bundle(self.inbox, actionable=False)
        server = self.server()
        output = io.StringIO()
        with mock.patch.object(receiver, "analyze_bundle") as analyzer, \
                contextlib.redirect_stdout(output):
            server.resume_existing_sessions()
            server.analysis_queue.join()
        analyzer.assert_not_called()
        self.assertIn("analysis_report=present", output.getvalue())
        self.assertIn("result=existing_analysis", output.getvalue())
        self.assertNotIn("[PRECHECK]", output.getvalue())
        self.assert_phases(output.getvalue(), "startup_resume", "ANALYSIS", "DONE")
        self.assertTrue((bundle / "repair_status.json").is_file())

    def test_startup_selection_depends_on_artifacts_not_status_names(self) -> None:
        # No worker is needed to inspect exactly which jobs startup selects.
        server = self.server("disabled")
        server.analysis_mode = "automatic"
        for status in ("completed", "needs_capture", "repair_failed", "failed", "timeout",
                       "repair_pushed", "blocked", "blocked_remote_changed", "dry_run",
                       "MODEL_UNAVAILABLE", "BLOCKED_BASELINE_UNSTABLE"):
            with self.subTest(status=status):
                bundle = self.inbox / f"phone_saber_triage_{status}"
                bundle.mkdir()
                (bundle / "summary.json").write_text(json.dumps({"sessionID": status}))
                (bundle / "state.json").write_text(json.dumps({"status": status}))
                status_path = bundle / "repair_status.json"
                status_path.write_text(json.dumps({"status": status}))
                with mock.patch.object(server, "enqueue_analysis") as enqueue:
                    server.resume_existing_sessions()
                    enqueue.assert_not_called()
                status_path.unlink()
                with mock.patch.object(server, "enqueue_analysis") as enqueue:
                    server.resume_existing_sessions()
                    enqueue.assert_called_once_with(bundle, source="startup_resume",
                                                    reason="repair_status_missing")
                status_path.write_text("{}")
        missing = self.inbox / "phone_saber_triage_missing_summary"
        missing.mkdir()
        (self.inbox / "phone_saber_triage_link").symlink_to(missing, target_is_directory=True)
        (self.inbox / "phone_saber_triage_file").write_text("not a directory")
        with mock.patch.object(server, "enqueue_analysis") as enqueue:
            server.resume_existing_sessions()
            enqueue.assert_not_called()
        for mode in ("disabled", "dry-run"):
            server.analysis_mode = mode
            write_codex_bundle(self.inbox / f"phone_saber_triage_pending_{mode}")
            with mock.patch.object(server, "enqueue_analysis") as enqueue:
                server.resume_existing_sessions()
                enqueue.assert_not_called()

    def test_main_invokes_startup_scan(self) -> None:
        with mock.patch.object(receiver, "TriageHTTPServer") as server_class, \
                contextlib.redirect_stdout(io.StringIO()):
            server_class.return_value.serve_forever.side_effect = KeyboardInterrupt
            self.assertEqual(receiver.main(["--inbox", str(self.inbox), "--no-bonjour"]), 0)
        server_class.return_value.resume_existing_sessions.assert_called_once_with()

    def test_manual_retry_and_nested_context_restore_attribution(self) -> None:
        bundle = self.inbox / "phone_saber_triage_sample_session"
        write_codex_bundle(bundle)
        server = self.server()
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            server.enqueue_analysis(bundle)
            server.analysis_queue.join()
        self.assert_phases(output.getvalue(), "manual_retry", "PRECHECK", "ANALYSIS", "DONE")
        with session_log_context(bundle, source="startup_resume"):
            with session_log_context(self.root / "phone_saber_triage_another", source="new_upload"):
                self.assertEqual(log_fields(), "sessionID=another source=new_upload")
            self.assertEqual(log_fields(), "sessionID=sample_session source=startup_resume")
        self.assertEqual(log_fields(), "sessionID=unknown source=manual_retry")


if __name__ == "__main__":
    unittest.main()
