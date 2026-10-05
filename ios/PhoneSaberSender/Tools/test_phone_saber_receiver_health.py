"""Receiver /health (stale code, activity), rejection logging and readable failure lines.

Regression tests for the 2026-10-03 receiver log audit
(summarised in docs/claude/FINDINGS.md).
"""

from __future__ import annotations

import contextlib
import importlib.util
import io
import json
import os
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
from phone_saber_precheck_text import explain_precheck
from phone_saber_test_isolation import isolate_codex_logs as setUpModule  # noqa: F401
from phone_saber_test_isolation import restore_codex_logs as tearDownModule  # noqa: F401
from phone_saber_tracking_diagnostics import PrecheckFailed
from phone_saber_triage_protocol import CONTENT_TYPE, MAX_BUNDLE_BYTES, BundleError
from test_phone_saber_triage_codex import EMPTY_ANALYSIS, fake_codex, write_codex_bundle


class CodeSnapshotTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.tools = Path(temporary.name).resolve()
        self.module_path = self.tools / "phone_saber_snapshot_probe.py"
        self.module_path.write_text("VALUE = 1\n", encoding="utf-8")
        (self.tools / "test_ignored.py").write_text("X = 1\n", encoding="utf-8")
        for name, path in (("phone_saber_snapshot_probe", self.module_path),
                           ("test_ignored", self.tools / "test_ignored.py")):
            spec = importlib.util.spec_from_file_location(name, path)
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            sys.modules[name] = module
            self.addCleanup(sys.modules.pop, name, None)

    def test_only_loaded_non_test_modules_are_tracked(self) -> None:
        snapshot = receiver.CodeSnapshot.capture(self.tools)
        self.assertEqual([path.name for path in snapshot.files], ["phone_saber_snapshot_probe.py"])
        self.assertEqual(snapshot.changed_files(), [])

    def test_touch_without_change_is_not_stale_but_edit_and_removal_are(self) -> None:
        snapshot = receiver.CodeSnapshot.capture(self.tools)
        stat = self.module_path.stat()
        os.utime(self.module_path, ns=(stat.st_atime_ns, stat.st_mtime_ns + 5_000_000_000))
        self.assertEqual(snapshot.changed_files(), [])
        self.module_path.write_text("VALUE = 2\n", encoding="utf-8")
        self.assertEqual(snapshot.changed_files(), ["phone_saber_snapshot_probe.py"])
        self.module_path.unlink()
        self.assertEqual(snapshot.changed_files(), ["phone_saber_snapshot_probe.py"])

    def test_real_receiver_tracks_its_own_source(self) -> None:
        names = {path.name for path in receiver.CodeSnapshot.capture().files}
        self.assertIn("phone_saber_triage_receiver.py", names)
        self.assertIn("phone_saber_triage_codex.py", names)
        self.assertFalse(any(name.startswith("test_") for name in names))


class ReceiverHealthTests(unittest.TestCase):
    def setUp(self) -> None:
        temporary = tempfile.TemporaryDirectory()
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.inbox = self.root / "inbox"
        self.inbox.mkdir()
        self.codex = fake_codex(self.root, self.root / "spy.json", analysis=EMPTY_ANALYSIS)

    def server(self, mode: str = "automatic") -> receiver.TriageHTTPServer:
        server = receiver.TriageHTTPServer(("127.0.0.1", 0), self.inbox, analysis_mode=mode,
                                           codex_path=str(self.codex), repair_mode="disabled")
        self.addCleanup(server.server_close)
        return server

    def serve(self, server: receiver.TriageHTTPServer) -> None:
        thread = Thread(target=server.serve_forever, kwargs={"poll_interval": 0.05}, daemon=True)
        thread.start()

        def stop() -> None:
            server.shutdown()
            thread.join(timeout=2)
        self.addCleanup(stop)

    def request(self, server, method: str, path: str, body: bytes | None = None,
                headers: dict | None = None) -> tuple[int, dict]:
        connection = HTTPConnection("127.0.0.1", server.server_port, timeout=3)
        try:
            connection.request(method, path, body=body, headers=headers or {})
            response = connection.getresponse()
            return response.status, json.loads(response.read() or b"{}")
        finally:
            connection.close()

    def test_health_reports_identity_code_state_and_idle(self) -> None:
        server = self.server()
        self.serve(server)
        with contextlib.redirect_stdout(io.StringIO()):
            status, payload = self.request(server, "GET", "/health")
        self.assertEqual(status, 200)
        self.assertEqual(payload["status"], "ready")
        self.assertEqual(payload["service"], "phonesaber-triage")
        self.assertEqual(payload["pid"], os.getpid())
        self.assertIs(payload["codeChangedSinceStart"], False)
        self.assertEqual(payload["changedFiles"], [])
        self.assertEqual(payload["analysis"], {"mode": "automatic", "running": None, "queued": 0})
        self.assertEqual(payload["uploadsInProgress"], 0)
        self.assertIs(payload["idle"], True)

    def test_health_reports_changed_code_and_busy_analysis(self) -> None:
        server = self.server()
        with mock.patch.object(server.code_snapshot, "changed_files", return_value=["phone_saber_triage_codex.py"]):
            server.analysis_running = "phone_saber_triage_x"
            payload = server.health()
        self.assertIs(payload["codeChangedSinceStart"], True)
        self.assertEqual(payload["changedFiles"], ["phone_saber_triage_codex.py"])
        self.assertEqual(payload["analysis"]["running"], "phone_saber_triage_x")
        self.assertIs(payload["idle"], False)

    def test_upload_counts_as_busy_until_analysis_is_queued(self) -> None:
        server = self.server(mode="disabled")
        seen: list[bool] = []
        original = server.enqueue_analysis

        def enqueue(bundle, **kwargs):
            seen.append(server.health()["idle"])
            return original(bundle, **kwargs)

        self.serve(server)
        body = write_codex_bundle(self.root / "upload")
        with mock.patch.object(server, "enqueue_analysis", side_effect=enqueue), \
                contextlib.redirect_stdout(io.StringIO()):
            status, _ = self.request(server, "POST", "/v1/bundle", body, {
                "Content-Type": CONTENT_TYPE, "Content-Length": str(len(body))})
        self.assertEqual(status, 201)
        self.assertEqual(seen, [False])
        self.assertTrue(server.health()["idle"])

    def test_rejected_uploads_are_logged_with_reason(self) -> None:
        server = self.server(mode="disabled")
        self.serve(server)
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            status, _ = self.request(server, "POST", "/v1/bundle", b"x" * 16, {
                "Content-Type": CONTENT_TYPE, "Content-Length": str(MAX_BUNDLE_BYTES + 1)})
            self.assertEqual(status, 413)
            status, _ = self.request(server, "POST", "/v1/bundle", b"not-a-bundle", {
                "Content-Type": CONTENT_TYPE, "Content-Length": "12"})
            self.assertEqual(status, 400)
        log = output.getvalue()
        self.assertIn(f"Content-Length={MAX_BUNDLE_BYTES + 1} outside 8..{MAX_BUNDLE_BYTES} bytes (413)", log)
        self.assertIn("rejected upload from 127.0.0.1 (400)", log)
        self.assertEqual(server.health()["uploadsInProgress"], 0)

    def test_codex_failure_dump_is_printed_once(self) -> None:
        bundle = self.inbox / "phone_saber_triage_sample_session"
        write_codex_bundle(bundle)
        server = self.server()
        timed_out = mock.Mock(diagnostic={"error_code": "CLI_TIMEOUT"})
        timed_out.describe.return_value = "CLI_TIMEOUT: timed out\nstdout (redacted tail):\nUNIQUE-TAIL-MARKER"
        output = io.StringIO()
        with mock.patch.object(analysis, "run_codex", side_effect=CodexProcessError(timed_out)), \
                contextlib.redirect_stdout(output):
            server.enqueue_analysis(bundle)
            server.analysis_queue.join()
        log = output.getvalue()
        # The timeout is retried once at effort high, so two failures reach the log at most once each.
        self.assertEqual(log.count("UNIQUE-TAIL-MARKER"), 1)
        self.assertIn("analysis failed; bundle preserved: CLI_TIMEOUT: timed out", log)
        self.assertTrue(server.health()["idle"])

    def test_precheck_result_gets_a_japanese_explanation(self) -> None:
        bundle = self.inbox / "phone_saber_triage_sample_session"
        bundle.mkdir()
        server = self.server()
        output = io.StringIO()
        result = {"status": "precheck_failed", "sessionID": "sample_session",
                  "reasonCodes": ["candidateHistoryMissing", "temporalEvidenceMissing"]}
        with mock.patch.object(receiver, "analyze_bundle", return_value=result), \
                contextlib.redirect_stdout(output):
            server.enqueue_analysis(bundle)
            server.analysis_queue.join()
        line = next(line for line in output.getvalue().splitlines() if "[PHONE_SABER][PRECHECK]" in line)
        self.assertIn("Codex は呼んでいない", line)
        self.assertIn("数秒以上振る録画", line)

    def test_input_precheck_failure_explains_the_context_size_limit(self) -> None:
        bundle = self.inbox / "phone_saber_triage_sample_session"
        bundle.mkdir()
        server = self.server()
        output = io.StringIO()
        failure = PrecheckFailed([{"code": "frameMappingMissing", "detail":
                                   "frame context exceeds its size limit: frames/a.json is 40000 bytes > 32768"}])
        with mock.patch.object(receiver, "analyze_bundle", side_effect=failure), \
                contextlib.redirect_stdout(output):
            server.enqueue_analysis(bundle)
            server.analysis_queue.join()
        line = next(line for line in output.getvalue().splitlines() if "[PHONE_SABER][PRECHECK]" in line)
        self.assertIn("32KB", line)
        self.assertIn("上限は緩めない", line)


class ContextLimitMessageTests(unittest.TestCase):
    def test_oversized_context_names_file_size_and_limit(self) -> None:
        with tempfile.TemporaryDirectory() as temporary:
            bundle = Path(temporary) / "bundle"
            write_codex_bundle(bundle)
            context = sorted((bundle / "frames").glob("*.json"))[0]
            context.write_text(context.read_text(encoding="utf-8") + " " * (33 * 1024), encoding="utf-8")
            size = context.stat().st_size
            with self.assertRaises(BundleError) as raised:
                analysis.input_plan(bundle)
        message = str(raised.exception)
        self.assertIn("frame context exceeds its size limit", message)
        self.assertIn(f"frames/{context.name} is {size} bytes", message)
        self.assertIn("> 32768", message)


class PrecheckTextTests(unittest.TestCase):
    def test_unknown_codes_are_kept_and_action_is_generic(self) -> None:
        text = explain_precheck(["somethingNew"])
        self.assertIn("somethingNew", text)
        self.assertIn("analysis_report.json", text)

    def test_empty_codes(self) -> None:
        self.assertIn("理由不明", explain_precheck([]))


if __name__ == "__main__":
    unittest.main()
