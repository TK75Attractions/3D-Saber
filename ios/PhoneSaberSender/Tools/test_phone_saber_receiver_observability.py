"""Receiver startup preserves existing sessions; processing requires a POST or explicit retry."""

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

import phone_saber_auto_repair as repair
import phone_saber_triage_codex as analysis
import phone_saber_triage_receiver as receiver
from phone_saber_codex_process import CodexProcessError
from phone_saber_session_log import log_fields, session_log_context
from phone_saber_triage_protocol import CONTENT_TYPE
from test_phone_saber_auto_repair import prepared_bundle
from test_phone_saber_triage_codex import EMPTY_ANALYSIS, fake_codex, write_codex_bundle
from phone_saber_test_isolation import isolate_codex_logs as setUpModule  # noqa: F401,E402
from phone_saber_test_isolation import restore_codex_logs as tearDownModule  # noqa: F401,E402


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

    def snapshot(self, root: Path) -> dict:
        # Detect writes, deletions, new files, and directory changes, including nested PNGs.
        return {p.relative_to(root).as_posix(): (
            "directory" if p.is_dir() else p.read_bytes(), p.stat().st_mtime_ns)
            for p in root.rglob("*")}

    def assert_phases(self, output: str, source: str, *phases: str) -> None:
        for phase in phases:
            lines = [line for line in output.splitlines() if f"[AUTO_REPAIR][{phase}]" in line]
            self.assertTrue(lines, f"missing {phase}: {output}")
            for line in lines:
                self.assertIn("sessionID=sample_session", line)
                self.assertIn(f"source={source}", line)

    def assert_startup_preserves_inbox(self, *options: str) -> None:
        before = self.snapshot(self.inbox)
        output = io.StringIO()
        with mock.patch.object(receiver, "analyze_bundle") as analyzer, \
                mock.patch.object(receiver, "repair_bundle") as repairer, \
                mock.patch.object(receiver.TriageHTTPServer, "enqueue_analysis") as enqueue, \
                mock.patch.object(receiver.TriageHTTPServer, "serve_forever",
                                  side_effect=KeyboardInterrupt), \
                mock.patch.object(receiver.TriageHTTPServer, "shutdown"), \
                contextlib.redirect_stdout(output):
            # Use the real server constructor and main startup/shutdown paths, with no POST.
            result = receiver.main(["--inbox", str(self.inbox), "--host", "127.0.0.1",
                                    "--port", "0", "--no-bonjour", *options])
        self.assertEqual(result, 0)
        enqueue.assert_not_called()
        analyzer.assert_not_called()
        repairer.assert_not_called()
        self.assertIn("[PHONE_SABER][START]", output.getvalue())
        self.assertIn("[PHONE_SABER][WAITING]", output.getvalue())
        for phase in ("PRECHECK", "ANALYSIS", "RESUME"):
            self.assertNotIn(f"[AUTO_REPAIR][{phase}]", output.getvalue())
        self.assertEqual(before, self.snapshot(self.inbox))

    def test_old_unfinished_session_is_not_processed_or_changed_at_startup(self) -> None:
        bundle = prepared_bundle(self.inbox, actionable=False)
        (bundle / "state.json").write_text(json.dumps({
            "sessionID": "sample_session", "status": "running", "phase": "repairing",
            "ownedFiles": {},
        }))
        self.assert_startup_preserves_inbox()

    def test_old_session_without_analysis_report_is_not_processed_at_startup(self) -> None:
        bundle = self.inbox / "phone_saber_triage_sample_session"
        write_codex_bundle(bundle)
        self.assert_startup_preserves_inbox()
        self.assertFalse((bundle / "analysis_report.json").exists())

    def test_analysis_timeout_is_preserved_on_restart_until_manual_retry(self) -> None:
        bundle = self.inbox / "phone_saber_triage_sample_session"
        write_codex_bundle(bundle)
        server = self.server()
        output = io.StringIO()
        timed_out = mock.Mock(diagnostic={"error_code": "CLI_TIMEOUT"})
        timed_out.describe.return_value = "CLI_TIMEOUT: analysis timed out"
        with mock.patch.object(analysis, "run_codex", side_effect=CodexProcessError(timed_out)), \
                contextlib.redirect_stdout(output):
            server.enqueue_analysis(bundle)
            server.analysis_queue.join()
        self.assertIn("CLI_TIMEOUT", output.getvalue())
        self.assertFalse((bundle / "analysis_report.json").exists())
        self.assertFalse((bundle / "repair_status.json").exists())
        self.assert_startup_preserves_inbox()
        with contextlib.redirect_stdout(output):
            self.assertEqual(analysis.main([str(bundle), "--codex-path", str(self.codex)]), 0)
        self.assertTrue((bundle / "analysis_report.json").is_file())
        self.assert_phases(output.getvalue(), "manual_retry", "ANALYSIS")

    def test_completed_session_is_not_processed_at_startup(self) -> None:
        bundle = prepared_bundle(self.inbox, actionable=False)
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(repair.repair_bundle(bundle)["status"], "needs_capture")
        self.assertTrue((bundle / "repair_status.json").is_file())
        self.assert_startup_preserves_inbox()

    def test_all_startup_modes_preserve_existing_sessions(self) -> None:
        for status in ("completed", "needs_capture", "repair_failed", "failed", "timeout",
                       "repair_pushed", "blocked", "blocked_remote_changed", "dry_run",
                       "MODEL_UNAVAILABLE", "BLOCKED_BASELINE_UNSTABLE"):
            bundle = self.inbox / f"phone_saber_triage_{status}"
            bundle.mkdir()
            (bundle / "summary.json").write_text(json.dumps({"sessionID": status}))
            (bundle / "state.json").write_text(json.dumps({"status": status}))
        for options in ((), ("--dry-run",), ("--no-codex",)):
            with self.subTest(options=options):
                self.assert_startup_preserves_inbox(*options)

    def test_new_post_processes_only_new_upload_and_preserves_old_bundle(self) -> None:
        old = self.inbox / "phone_saber_triage_old_session"
        write_codex_bundle(old)
        # Give the old bundle a different session ID throughout its compact evidence.
        for path in [old / "summary.json", *old.glob("frames/*.json")]:
            data = json.loads(path.read_text())
            data["sessionID"] = "old_session"
            path.write_text(json.dumps(data))
        before = self.snapshot(old)
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
                    # An existing bundle cannot become an automatic retry via a duplicate POST.
                    connection.request("POST", "/v1/bundle", body=body, headers={
                        "Content-Type": CONTENT_TYPE, "Content-Length": str(len(body)),
                    })
                    response = connection.getresponse()
                    self.assertEqual(response.status, 409)
                    response.read()
                finally:
                    connection.close()
                server.analysis_queue.join()
        finally:
            server.shutdown()
            thread.join(timeout=2)
        log = output.getvalue()
        self.assertIn("[PHONE_SABER][SESSION] sessionID=sample_session source=new_upload", log)
        self.assertEqual(log.count("[PHONE_SABER][SESSION]"), 1)
        self.assert_phases(log, "new_upload", "PRECHECK", "ANALYSIS", "DONE")
        self.assertLess(log.index("[PHONE_SABER][SESSION]"), log.index("[PRECHECK]"))
        self.assertNotIn("[RESUME]", log)
        self.assertNotIn("sessionID=old_session", log)
        self.assertEqual(before, self.snapshot(old))
        self.assertTrue((self.inbox / "phone_saber_triage_sample_session" / "repair_status.json").is_file())

    def test_manual_clis_require_explicit_session_path(self) -> None:
        write_codex_bundle(self.inbox / "phone_saber_triage_sample_session")
        before = self.snapshot(self.inbox)
        for module, function in ((analysis, "analyze_bundle"), (repair, "repair_bundle")):
            with self.subTest(module=module.__name__), \
                    mock.patch.object(module, function) as process, \
                    contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as error:
                module.main([])
            self.assertEqual(error.exception.code, 2)
            process.assert_not_called()
        self.assertEqual(before, self.snapshot(self.inbox))

    def test_explicit_manual_retry_reuses_cached_analysis_with_manual_source(self) -> None:
        bundle = prepared_bundle(self.inbox, actionable=False)
        reports = {p.name: p.read_bytes() for p in bundle.glob("analysis_report.*")}
        self.assert_startup_preserves_inbox()
        output = io.StringIO()
        with mock.patch.object(receiver, "analyze_bundle") as analyzer, \
                contextlib.redirect_stdout(output):
            self.assertEqual(receiver.main([
                "--repair-bundle", str(bundle), "--repair-dry-run"]), 0)
        analyzer.assert_not_called()
        self.assert_phases(output.getvalue(), "manual_retry", "DONE")
        self.assertEqual(reports, {p.name: p.read_bytes() for p in bundle.glob("analysis_report.*")})
        self.assertTrue((bundle / "repair_status.json").is_file())

    def test_explicit_manual_queue_and_nested_context_restore_attribution(self) -> None:
        bundle = self.inbox / "phone_saber_triage_sample_session"
        write_codex_bundle(bundle)
        server = self.server()
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            server.enqueue_analysis(bundle)
            server.analysis_queue.join()
        self.assert_phases(output.getvalue(), "manual_retry", "PRECHECK", "ANALYSIS", "DONE")
        with session_log_context(bundle, source="manual_retry"):
            with session_log_context(self.root / "phone_saber_triage_another", source="new_upload"):
                self.assertEqual(log_fields(), "sessionID=another source=new_upload")
            self.assertEqual(log_fields(), "sessionID=sample_session source=manual_retry")
        self.assertEqual(log_fields(), "sessionID=unknown source=manual_retry")


if __name__ == "__main__":
    unittest.main()
