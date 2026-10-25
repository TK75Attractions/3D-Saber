#!/usr/bin/env python3
"""Tests for bounded PhoneSaber triage bundle packaging and receiving."""

from __future__ import annotations

import hashlib
import contextlib
import io
import json
import struct
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
import uuid
from unittest import mock
from http.client import HTTPConnection, HTTPMessage
from pathlib import Path
from threading import Thread
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parent))

import phone_saber_triage_protocol as protocol
from phone_saber_triage_receiver import TriageHTTPServer, TriageRequestHandler
from phone_saber_test_isolation import isolate_codex_logs as setUpModule  # noqa: F401,E402
from phone_saber_test_isolation import restore_codex_logs as tearDownModule  # noqa: F401,E402


def png_bytes(width: int = 16, height: int = 12) -> bytes:
    return protocol.PNG_SIGNATURE + b"\x00\x00\x00\x0dIHDR" + struct.pack(">II", width, height)


def write_bundle(root: Path, image_count: int = 1, *, malformed_summary: bool = False) -> bytes:
    root.mkdir(parents=True, exist_ok=True)
    image_entries = []
    for index in range(image_count):
        image_path = f"images/image_{index + 1:02d}.png"
        context_path = f"frames/frame_{100 + index}_{index + 1}.json"
        image_entries.append({
            "path": image_path,
            "frameContextPath": context_path,
            "frameID": 100 + index,
            "timestamp": index / 30,
            "color": "red" if index % 2 == 0 else "blue",
            "failureType": "dropout",
            "reason": "first false frame",
        })
        image_file = root / image_path
        image_file.parent.mkdir(parents=True, exist_ok=True)
        image_file.write_bytes(png_bytes())
        context_file = root / context_path
        context_file.parent.mkdir(parents=True, exist_ok=True)
        context_file.write_text(json.dumps({
            "sessionID": "sample_session",
            "selectedFrameID": 100 + index,
            "selectedColor": "red" if index % 2 == 0 else "blue",
            "selectedFailureType": "dropout",
            "selectedReasons": ["first false frame"],
            "contextRadiusFrames": 2,
            "frames": [{
                "frameID": 100 + index,
                "timestamp": index / 30,
                "red": {"detected": False},
                "blue": {"detected": True},
            }],
        }), encoding="utf-8")
    if malformed_summary:
        (root / "summary.json").write_text("{bad", encoding="utf-8")
    else:
        (root / "summary.json").write_text(json.dumps({
            "formatVersion": 1,
            "sessionID": "sample_session",
            "recordedFrameCount": 300,
            "redBlueDetectionSummary": {"red": {}, "blue": {}},
            "dropoutSummary": {"red": {}, "blue": {}},
            "selectedImageCount": image_count,
            "incidentCount": image_count,
            "incidents": [],
            "images": image_entries,
        }), encoding="utf-8")
    (root / "prompt.md").write_text("Analyze selected lossless frames only.", encoding="utf-8")
    envelope = root.parent / f"{root.name}.psbt"
    protocol.pack_bundle(root, envelope)
    return envelope.read_bytes()


def raw_envelope(files: list[tuple[str, bytes]], session_id: str = "sample_session") -> bytes:
    manifest_files = []
    for name, data in files:
        manifest_files.append({
            "path": name,
            "size": len(data),
            "sha256": hashlib.sha256(data).hexdigest(),
            "contentType": protocol._content_type(name),
        })
    manifest = json.dumps({
        "formatVersion": 1,
        "sessionID": session_id,
        "files": manifest_files,
    }, separators=(",", ":")).encode()
    return protocol.MAGIC + struct.pack(">I", len(manifest)) + manifest \
        + b"".join(data for _, data in files)


def empty_bundle_files(summary_text: str | None = None) -> list[tuple[str, bytes]]:
    summary = summary_text or json.dumps({
        "formatVersion": 1,
        "sessionID": "sample_session",
        "redBlueDetectionSummary": {"red": {}, "blue": {}},
        "dropoutSummary": {"red": {}, "blue": {}},
        "selectedImageCount": 0,
        "incidentCount": 0,
        "images": [],
    })
    return [("prompt.md", b"no selected images"), ("summary.json", summary.encode())]


class TriageReplyTests(unittest.TestCase):
    def test_shared_tracking_before_manifest_then_retry_201_and_duplicate_200(self):
        body = raw_envelope(empty_bundle_files())
        with tempfile.TemporaryDirectory() as directory, self.make_server(Path(directory) / "inbox") as server, \
                mock.patch.object(server, "write_report"), mock.patch.object(server, "enqueue_analysis") as enqueue, \
                mock.patch.object(server, "request_overview"):
            ids = []
            for number, (payload, expected_status, outcome) in enumerate([
                    (body[:6], 400, "failed-400-awaiting-retry"),
                    (body, 201, "stored-201"), (body, 200, "duplicate-200")], 1):
                handler = self.make_handler(server=server, body=payload, length=len(body))
                transfer_id = str(uuid.uuid4()).upper()
                ids.append(transfer_id)
                handler.headers["X-PhoneSaber-Session-ID"] = "sample_session"
                handler.headers["X-PhoneSaber-Transfer-ID"] = transfer_id
                handler.headers["X-PhoneSaber-Attempt"] = str(number)
                output = io.StringIO()
                with contextlib.redirect_stdout(output):
                    handler.do_POST()
                self.assertTrue(handler.wfile.getvalue().startswith(f"HTTP/1.1 {expected_status} ".encode()))
                lines = [line for line in output.getvalue().splitlines() if "[triage-upload]" in line]
                self.assertIn("outcome=started", lines[0])
                self.assertIn("received-bytes=0 ", lines[0])
                self.assertIn(f"outcome={outcome}", lines[-1])
                self.assertIn(f"received-bytes={len(payload)} ", lines[-1])
                for line in lines:
                    for field in (f"transferID={transfer_id}", "claimed-sessionID=sample_session",
                                  f"sender-attempt={number}", f"expected-bytes={len(body)} "):
                        self.assertIn(field, line)
                if number == 1:
                    self.assertIn("sessionID=unknown-before-manifest", lines[-1])
                    self.assertFalse(list(server.inbox.glob("phone_saber_triage_*")))
                else:
                    self.assertIn("sessionID=sample_session", lines[-1])
                    self.assertIn("sessionID-match=yes", lines[-1])
                self.assertEqual(server.health()["uploadsInProgress"], 0)
                self.assertFalse(list(server.inbox.glob(".phonesaber-receive-*")))
            self.assertEqual(len(set(ids)), 3)
            enqueue.assert_called_once()

    def test_tracking_headers_are_diagnostic_only_for_legacy_invalid_and_mismatch(self):
        body = raw_envelope(empty_bundle_files())
        valid_id = str(uuid.uuid4())
        cases = [({}, "unknown", "unknown", "unknown", "unknown"),
                 ({"X-PhoneSaber-Session-ID": "other_clip", "X-PhoneSaber-Transfer-ID": valid_id,
                   "X-PhoneSaber-Attempt": "7"}, "other_clip", valid_id, "7", "no"),
                 ({"X-PhoneSaber-Session-ID": "x" * 100}, "x" * 100, "unknown", "unknown", "no"),
                 ({"X-PhoneSaber-Session-ID": "../escape", "X-PhoneSaber-Transfer-ID": valid_id,
                   "X-PhoneSaber-Attempt": "3"}, "unknown", valid_id, "3", "unknown"),
                 ({"X-PhoneSaber-Session-ID": "sample_session", "X-PhoneSaber-Transfer-ID": "x" * 101,
                   "X-PhoneSaber-Attempt": "2"}, "sample_session", "unknown", "2", "yes")]
        for bad in ("../escape", "x" * 101, "clip\nforged=yes", "日本語", ""):
            cases.append(({"X-PhoneSaber-Session-ID": bad, "X-PhoneSaber-Transfer-ID": "not-a-uuid",
                           "X-PhoneSaber-Attempt": "-1"}, "unknown", "unknown", "unknown", "unknown"))
        for bad_attempt in ("0", "1\nforged=yes", "1" * 10, "１"):
            cases.append(({"X-PhoneSaber-Attempt": bad_attempt}, "unknown", "unknown", "unknown", "unknown"))
        for headers, claimed, transfer, number, match in cases:
            with self.subTest(headers=headers), tempfile.TemporaryDirectory() as directory, \
                    self.make_server(Path(directory) / "inbox") as server, \
                    mock.patch.object(server, "write_report"), mock.patch.object(server, "enqueue_analysis"), \
                    mock.patch.object(server, "request_overview"):
                changed = raw_envelope([(name, b"changed" if name == "prompt.md" else data)
                                        for name, data in empty_bundle_files()])
                # 同じ本文は200、同じmanifest IDの別本文は409、不正本文は400のまま。
                for payload, status in ((body, 201), (body, 200), (changed, 409), (b"invalid!", 400)):
                    handler = self.make_handler(server=server, body=payload)
                    for key, value in headers.items():
                        handler.headers[key] = value
                    output = io.StringIO()
                    with contextlib.redirect_stdout(output):
                        handler.do_POST()
                    self.assertTrue(handler.wfile.getvalue().startswith(f"HTTP/1.1 {status} ".encode()))
                    line = next(line for line in reversed(output.getvalue().splitlines()) if "[triage-upload]" in line)
                    for field in (f"claimed-sessionID={claimed}", f"transferID={transfer}",
                                  f"sender-attempt={number}",
                                  f"sessionID-match={match if status != 400 else 'unknown'}"):
                        self.assertIn(field, line)
                    if status != 400:
                        self.assertIn("sessionID=sample_session", line)
                self.assertEqual((server.inbox / "phone_saber_triage_sample_session/prompt.md").read_bytes(),
                                 b"no selected images")
                self.assertEqual([p.name for p in server.inbox.iterdir()], ["phone_saber_triage_sample_session"])

    def make_server(self, inbox):
        # 受信・保存・活動カウンターは実装を使い、ソケットの待受けだけを省く。
        with mock.patch.object(TriageHTTPServer, "server_bind"), \
                mock.patch.object(TriageHTTPServer, "server_activate"):
            return TriageHTTPServer(("127.0.0.1", 0), inbox)

    def make_handler(self, *, server=None, body=b"", length=None):
        handler = object.__new__(TriageRequestHandler)
        handler.client_address = ("127.0.0.1", 12345)
        handler.server = server
        handler.request_version = "HTTP/1.1"
        handler.requestline = "POST /v1/bundle HTTP/1.1"
        handler.command = "POST"
        handler.path = "/v1/bundle"
        handler.headers = HTTPMessage()
        handler.headers["Content-Type"] = protocol.CONTENT_TYPE
        handler.headers["Content-Length"] = str(len(body) if length is None else length)
        handler.rfile = io.BytesIO(body)
        handler.wfile = io.BytesIO()
        handler.close_connection = False
        return handler

    def inject_disconnect(self, handler, stage, error):
        # 実際のend_headers()を通し、ヘッダーか本文の書込みだけを失敗させる。
        handler.wfile = mock.Mock(wraps=io.BytesIO())
        handler.wfile.write.side_effect = [error] if stage == "headers" else [None, error]

    def test_reply_closes_connection_on_header_and_body_disconnects(self):
        for stage in ("headers", "body"):
            for error_type in (BrokenPipeError, ConnectionResetError, ConnectionAbortedError, TimeoutError):
                with self.subTest(stage=stage, error=error_type.__name__):
                    handler = self.make_handler()
                    self.inject_disconnect(handler, stage, error_type("peer disconnected"))
                    output = io.StringIO()
                    with contextlib.redirect_stdout(output):
                        handler._reply(400, {"error": "invalid bundle"})
                    self.assertTrue(handler.close_connection)
                    self.assertEqual(handler.wfile.write.call_count, 1 if stage == "headers" else 2)
                    self.assertIn("reply-failed-400", output.getvalue())
                    self.assertIn(error_type.__name__, output.getvalue())

    def test_reply_does_not_hide_unrelated_errors(self):
        for stage in ("headers", "body"):
            for error in (ValueError("bug"), OSError(28, "disk full")):
                with self.subTest(stage=stage, error=type(error).__name__):
                    handler = self.make_handler()
                    self.inject_disconnect(handler, stage, error)
                    with contextlib.redirect_stdout(io.StringIO()), self.assertRaises(type(error)):
                        handler._reply(201, {"accepted": True})
                    self.assertTrue(handler.close_connection)

    def test_successful_reply_keeps_http_format(self):
        handler = self.make_handler()
        payload = {"accepted": True, "bundle": "合成AutoClip"}
        with contextlib.redirect_stdout(io.StringIO()):
            handler._reply(201, payload)
        headers, body = handler.wfile.getvalue().split(b"\r\n\r\n", 1)
        self.assertTrue(headers.startswith(b"HTTP/1.1 201 Created\r\n"))
        self.assertIn(b"Content-Type: application/json; charset=utf-8\r\n", headers)
        self.assertIn(f"Content-Length: {len(body)}\r\n".encode(), headers)
        self.assertTrue(headers.endswith(b"Connection: close"))
        self.assertEqual(json.loads(body), payload)
        self.assertTrue(handler.close_connection)

    def test_autoclip_success_reply_precedes_report(self):
        summary = json.loads(empty_bundle_files()[1][1])
        summary["summaryScope"] = "autoClip"
        body = raw_envelope(empty_bundle_files(json.dumps(summary)))
        with tempfile.TemporaryDirectory() as directory, \
                self.make_server(Path(directory) / "inbox") as server:
            handler = self.make_handler(server=server, body=body)
            def report(bundle):
                headers, response = handler.wfile.getvalue().split(b"\r\n\r\n", 1)
                self.assertTrue(headers.startswith(b"HTTP/1.1 201 Created\r\n"))
                self.assertEqual(json.loads(response), {"accepted": True, "bundle": bundle.name})
                self.assertEqual(server.health()["uploadsInProgress"], 1)
            with mock.patch.object(server, "write_clip_report", side_effect=report) as clip_report, \
                    contextlib.redirect_stdout(io.StringIO()):
                handler.do_POST()
                clip_report.assert_called_once()
                self.assertEqual(server.health()["uploadsInProgress"], 0)

    def test_autoclip_report_runs_once_after_failed_201_and_duplicate_retry(self):
        for stage in ("headers", "body"):
            for error_type in (BrokenPipeError, ConnectionResetError):
                with self.subTest(stage=stage, error=error_type.__name__), tempfile.TemporaryDirectory() as directory:
                    root = Path(directory)
                    write_bundle(root / "bundle")
                    summary_path = root / "bundle/summary.json"
                    summary = json.loads(summary_path.read_text())
                    summary["summaryScope"] = "autoClip"
                    summary_path.write_text(json.dumps(summary))
                    protocol.pack_bundle(root / "bundle", root / "clip.psbt")
                    body = (root / "clip.psbt").read_bytes()
                    with self.make_server(root / "inbox") as server:
                        handler = self.make_handler(server=server, body=body)
                        transfer_id = str(uuid.uuid4())
                        handler.headers["X-PhoneSaber-Session-ID"] = "sample_session"
                        handler.headers["X-PhoneSaber-Transfer-ID"] = transfer_id
                        handler.headers["X-PhoneSaber-Attempt"] = "2"
                        self.inject_disconnect(handler, stage, error_type("peer disconnected"))
                        saved = root / "inbox/autoclips/phone_saber_triage_sample_session"
                        def report(bundle):
                            self.assertEqual(bundle, saved)
                            self.assertTrue((bundle / "summary.json").is_file())
                            self.assertEqual(handler.wfile.write.call_count, 1 if stage == "headers" else 2)
                        output = io.StringIO()
                        with mock.patch.object(server, "write_clip_report", side_effect=report) as clip_report, \
                                mock.patch.object(server, "write_report") as session_report, \
                                mock.patch.object(server, "enqueue_analysis") as enqueue, \
                                mock.patch.object(server, "request_overview") as overview, \
                                contextlib.redirect_stdout(output):
                            handler.do_POST()
                            failed = next(line for line in output.getvalue().splitlines()
                                          if "outcome=reply-failed-201" in line)
                            for field in (f"transferID={transfer_id}", "claimed-sessionID=sample_session",
                                          "sender-attempt=2"):
                                self.assertIn(field, failed)
                            self.assertTrue(handler.close_connection)
                            self.assertEqual(server.health()["uploadsInProgress"], 0)
                            snapshot = {p.relative_to(saved): p.read_bytes() for p in saved.rglob("*") if p.is_file()}
                            inode = saved.stat().st_ino
                            retry = self.make_handler(server=server, body=body)
                            retry.do_POST()
                            self.assertTrue(retry.wfile.getvalue().startswith(b"HTTP/1.1 200 OK\r\n"))
                            self.assertEqual(json.loads(retry.wfile.getvalue().split(b"\r\n\r\n")[1]),
                                             {"status": "duplicate", "sessionID": "sample_session"})
                            self.assertEqual(saved.stat().st_ino, inode)
                            self.assertEqual({p.relative_to(saved): p.read_bytes() for p in saved.rglob("*") if p.is_file()},
                                             snapshot)
                            clip_report.assert_called_once_with(saved)
                            session_report.assert_not_called()
                            enqueue.assert_not_called()
                            overview.assert_not_called()
                            self.assertEqual(server.health()["uploadsInProgress"], 0)
                            self.assertFalse(list(server.inbox.glob(".phonesaber-receive-*")))
                        lines = output.getvalue().splitlines()
                        stored = next(line for line in lines if "outcome=stored-201" in line)
                        failed = next(line for line in lines if "outcome=reply-failed-201" in line)
                        for field in ("sessionID=sample_session", stored.split("attempt=")[1].split()[0],
                                      error_type.__name__, f"received-bytes={len(body)}"):
                            self.assertIn(field, failed)

    def test_incomplete_upload_with_failed_400_releases_activity_and_accepts_retry(self):
        for stage in ("headers", "body"):
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                body = write_bundle(root / "bundle")
                with self.make_server(root / "inbox") as server, \
                        mock.patch.object(server, "write_report") as report, \
                        mock.patch.object(server, "enqueue_analysis") as enqueue, \
                        contextlib.redirect_stdout(io.StringIO()) as output:
                    handler = self.make_handler(server=server, body=body[:-1], length=len(body))
                    self.inject_disconnect(handler, stage, BrokenPipeError("peer disconnected"))
                    handler.do_POST()
                    self.assertTrue(handler.close_connection)
                    self.assertEqual(list(server.inbox.iterdir()), [])
                    self.assertEqual(server.health()["uploadsInProgress"], 0)
                    report.assert_not_called()
                    enqueue.assert_not_called()
                    for payload, expected in ((body, 201), (body, 200),
                                              (raw_envelope(empty_bundle_files()), 409)):
                        retry = self.make_handler(server=server, body=payload)
                        retry.do_POST()
                        self.assertTrue(retry.wfile.getvalue().startswith(f"HTTP/1.1 {expected} ".encode()))
                        self.assertEqual(server.health()["uploadsInProgress"], 0)
                    report.assert_called_once()
                    enqueue.assert_called_once()
                    self.assertFalse(list(server.inbox.glob(".phonesaber-receive-*")))
                    failed = next(line for line in output.getvalue().splitlines() if "outcome=reply-failed-400" in line)
                    self.assertIn("sessionID=sample_session", failed)
                    self.assertIn("attempt=", failed)


class TriageBundleProtocolTests(unittest.TestCase):
    def test_timeout_records_bytes_already_read_and_manifest_session(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            body = write_bundle(root / "bundle")
            class StalledStream(io.BytesIO):
                def read1(self, count):
                    chunk = super().read(min(count, 19))
                    if not chunk:
                        raise TimeoutError("upload stalled")
                    return chunk
                def read(self, count):
                    raise AssertionError("buffered read could hide bytes consumed before timeout")
            handler = object.__new__(TriageRequestHandler)
            handler.client_address = ("127.0.0.1", 12345)
            handler.rfile = StalledStream(body[:-1])
            handler.server = SimpleNamespace(inbox=root / "inbox")
            handler._reply = mock.Mock()
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                handler._receive(len(body))
            self.assertEqual(handler._reply.call_args.args[0], 400)
            line = next(line for line in output.getvalue().splitlines() if "outcome=failed-400" in line)
            for field in ("sessionID=sample_session", f"received-bytes={len(body) - 1}", "upload stalled"):
                self.assertIn(field, line)
            self.assertFalse(list((root / "inbox").glob(".phonesaber-receive-*")))

    def test_partial_upload_is_correlated_with_retry_and_duplicate_by_session(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            body = write_bundle(root / "bundle")
            handler = object.__new__(TriageRequestHandler)
            handler.client_address = ("127.0.0.1", 12345)
            handler.server = SimpleNamespace(inbox=root / "inbox", write_report=mock.Mock(),
                                             enqueue_analysis=mock.Mock(), request_overview=mock.Mock())
            handler._reply = mock.Mock()
            output = io.StringIO()
            def post(payload):
                handler.rfile = io.BytesIO(payload)
                handler._receive(len(body))
                return handler._reply.call_args.args[0]
            with contextlib.redirect_stdout(output):
                self.assertEqual(post(body[:-1]), 400)
                self.assertEqual(post(body), 201)
                self.assertEqual(post(body), 200)
                outcomes = [line for line in output.getvalue().splitlines() if any(
                    f"outcome={outcome}" in line for outcome in
                    ("failed-400-awaiting-retry", "stored-201", "duplicate-200"))]
                self.assertEqual(len(outcomes), 3, output.getvalue())
                attempts = set()
                for index, line in enumerate(outcomes):
                    self.assertIn("sessionID=sample_session", line)
                    self.assertIn(f"expected-bytes={len(body)} ", line)
                    self.assertIn(f"received-bytes={len(body) - (index == 0)} ", line)
                    self.assertRegex(line, r"time=\d{4}-\d\d-\d\dT.*[+-]\d\d:\d\d")
                    attempts.add(line.split("attempt=")[1].split()[0])
                self.assertEqual(len(attempts), 3)
                self.assertFalse(list((root / "inbox").glob(".phonesaber-receive-*")))
                self.assertEqual(handler.server.enqueue_analysis.call_count, 1)

    def test_same_bundle_retry_is_received_but_changed_bundle_keeps_409(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            body = write_bundle(root / "bundle")
            server = TriageHTTPServer(("127.0.0.1", 0), root / "inbox", analysis_mode="disabled")
            thread = Thread(target=server.serve_forever, daemon=True)
            thread.start()
            def post(payload: bytes) -> tuple[int, dict]:
                connection = HTTPConnection("127.0.0.1", server.server_port, timeout=3)
                try:
                    connection.request("POST", "/v1/bundle", body=payload, headers={
                        "Content-Type": protocol.CONTENT_TYPE,
                    })
                    response = connection.getresponse()
                    return response.status, json.loads(response.read())
                finally:
                    connection.close()
            try:
                with mock.patch.object(server, "enqueue_analysis") as enqueue:
                    self.assertEqual(post(body)[0], 201)
                    self.assertEqual(post(body), (200, {"status": "duplicate", "sessionID": "sample_session"}))
                    self.assertEqual(enqueue.call_count, 1)
                    # Receiver reports are extra files, not part of the uploaded bundle.
                    saved = root / "inbox/phone_saber_triage_sample_session"
                    (saved / "local-report.md").write_text("report")
                    self.assertEqual(post(body)[0], 200)
                    (root / "bundle/prompt.md").write_text("different bundle")
                    protocol.pack_bundle(root / "bundle", root / "changed.psbt")
                    self.assertEqual(post((root / "changed.psbt").read_bytes())[0], 409)
                    self.assertEqual((saved / "prompt.md").read_text(), "Analyze selected lossless frames only.")
                    # Even an identical manifest cannot acknowledge corrupted stored bytes.
                    (saved / "images/image_01.png").write_bytes(png_bytes(17))
                    self.assertEqual(post(body)[0], 409)
                    self.assertFalse(list((root / "inbox").glob(".phonesaber-receive-*")))
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)

    def test_manifest_session_must_match_summary_and_staging_is_removed(self) -> None:
        body = raw_envelope(empty_bundle_files(), session_id="different_session")
        with tempfile.TemporaryDirectory() as directory:
            inbox = Path(directory) / "inbox"
            with self.assertRaisesRegex(protocol.BundleError, "session.*match"):
                protocol.receive_bundle(io.BytesIO(body), len(body), inbox)
            self.assertEqual(list(inbox.iterdir()), [])

    def test_nul_in_image_name_is_rejected_as_a_bundle_error(self) -> None:
        body = raw_envelope(empty_bundle_files() + [("images/unsafe\x00.png", png_bytes()),
                                                   ("frames/frame_100_1.json", b"{}")])
        with tempfile.TemporaryDirectory() as directory:
            inbox = Path(directory) / "inbox"
            with self.assertRaises(protocol.BundleError):
                protocol.receive_bundle(io.BytesIO(body), len(body), inbox)
            self.assertEqual(list(inbox.glob("*")), [])

    def test_truncated_upload_and_disk_full_remove_staging(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            body = write_bundle(Path(directory) / "bundle")
            inbox = Path(directory) / "inbox"
            with self.assertRaisesRegex(protocol.BundleError, "ended before"):
                protocol.receive_bundle(io.BytesIO(body[:-1]), len(body), inbox)
            self.assertEqual(list(inbox.iterdir()), [])
            original_open = Path.open
            def fail_write(path, mode="r", *args, **kwargs):
                if mode == "xb":
                    raise OSError(28, "No space left on device")
                return original_open(path, mode, *args, **kwargs)
            with mock.patch.object(Path, "open", fail_write), self.assertRaises(OSError):
                protocol.receive_bundle(io.BytesIO(body), len(body), inbox)
            self.assertEqual(list(inbox.iterdir()), [])

    def test_valid_bundle_is_received_atomically_and_png_bytes_are_unchanged(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "bundle"
            write_bundle(root)
            phone = {"deviceModel": "iPhone16,2", "deviceName": "iPhone 15 Pro Max", "deviceID": "test-device",
                     "appVersion": "1.0", "appBuild": "20261024", "station": "B",
                     "destination": "Mac 192.168.1.2:5005/5006", "manualIPOverride": True}
            summary = json.loads((root / "summary.json").read_text())
            summary["phone"] = phone
            (root / "summary.json").write_text(json.dumps(summary), encoding="utf-8")
            package = root.parent / "identity.psbt"
            protocol.pack_bundle(root, package)
            body = package.read_bytes()
            inbox = Path(directory) / "inbox"
            received = protocol.receive_bundle(io.BytesIO(body), len(body), inbox)
            self.assertEqual(received.name, "phone_saber_triage_sample_session")
            self.assertEqual((received / "images/image_01.png").read_bytes(), png_bytes())
            self.assertTrue((received / "frames/frame_100_1.json").is_file())
            self.assertFalse(any(received.glob("*.mp4")))
            self.assertEqual(json.loads((received / "summary.json").read_text())["phone"], phone)

    def test_many_images_over_hard_limit_are_rejected_without_partial_bundle(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            files = empty_bundle_files()
            for index in range(protocol.MAX_IMAGES + 1):
                png_name = f"images/image_{index:02d}.png"
                frame_name = f"frames/frame_{index}_{index}.json"
                files.append((png_name, png_bytes()))
                files.append((frame_name, b"{}"))
            body = raw_envelope(files)
            inbox = Path(directory) / "inbox"
            with self.assertRaises(protocol.BundleError):
                protocol.receive_bundle(io.BytesIO(body), len(body), inbox)
            self.assertEqual(list(inbox.glob("phone_saber_triage_*")), [])

    def test_request_size_limit_is_checked_before_reading(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(protocol.BundleError, "request size"):
                protocol.receive_bundle(io.BytesIO(b""), protocol.MAX_BUNDLE_BYTES + 1, Path(directory))

    def test_malformed_metadata_summary_is_rejected_and_staging_removed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            body = raw_envelope(empty_bundle_files("{broken"))
            inbox = Path(directory) / "inbox"
            with self.assertRaisesRegex(protocol.BundleError, "summary.json is malformed"):
                protocol.receive_bundle(io.BytesIO(body), len(body), inbox)
            self.assertEqual(list(inbox.iterdir()), [])

    def test_unsafe_paths_are_rejected(self) -> None:
        body = raw_envelope([("../summary.json", b"{}"), ("prompt.md", b"prompt")])
        with tempfile.TemporaryDirectory() as directory:
            with self.assertRaisesRegex(protocol.BundleError, "escapes"):
                protocol.receive_bundle(io.BytesIO(body), len(body), Path(directory))

    def test_duplicate_context_reference_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "bundle"
            write_bundle(root, image_count=2)
            summary_path = root / "summary.json"
            summary = json.loads(summary_path.read_text(encoding="utf-8"))
            summary["images"][1]["frameContextPath"] = summary["images"][0]["frameContextPath"]
            summary_path.write_text(json.dumps(summary), encoding="utf-8")
            with self.assertRaisesRegex(protocol.BundleError, "frame context more than once"):
                protocol.pack_bundle(root, Path(directory) / "rejected.psbt")

    def test_hash_and_png_validation_reject_corrupted_image(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            body = write_bundle(Path(directory) / "bundle")
            corrupt = bytearray(body)
            corrupt[-1] ^= 0xFF
            with self.assertRaisesRegex(protocol.BundleError, "SHA-256 mismatch"):
                protocol.receive_bundle(io.BytesIO(corrupt), len(corrupt), Path(directory) / "inbox")

    def test_no_failure_session_can_be_received_with_zero_images(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            body = raw_envelope(empty_bundle_files())
            received = protocol.receive_bundle(io.BytesIO(body), len(body), Path(directory) / "inbox")
            summary = json.loads((received / "summary.json").read_text(encoding="utf-8"))
            self.assertEqual(summary["selectedImageCount"], 0)

    def test_http_receiver_accepts_bundle_and_reports_health(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            inbox = Path(directory) / "inbox"
            server = TriageHTTPServer(("127.0.0.1", 0), inbox)
            thread = Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                body = write_bundle(Path(directory) / "bundle")
                connection = HTTPConnection("127.0.0.1", server.server_port, timeout=3)
                connection.request("POST", "/v1/bundle", body=body, headers={
                    "Content-Type": protocol.CONTENT_TYPE,
                    "Content-Length": str(len(body)),
                    "Connection": "close",
                })
                response = connection.getresponse()
                result = json.loads(response.read())
                connection.close()
                self.assertEqual(response.status, 201)
                self.assertTrue(result["accepted"])
                self.assertTrue((inbox / result["bundle"] / "summary.json").is_file())
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)

    def test_transfer_failure_leaves_the_bundle_unchanged_for_retry(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / "bundle"
            body = write_bundle(root)
            expected_png = (root / "images/image_01.png").read_bytes()
            server = TriageHTTPServer(("127.0.0.1", 0), Path(directory) / "inbox")
            unused_port = server.server_port
            server.server_close()
            connection = HTTPConnection("127.0.0.1", unused_port, timeout=1)
            try:
                with self.assertRaises(OSError):
                    connection.request("POST", "/v1/bundle", body=body, headers={
                        "Content-Type": protocol.CONTENT_TYPE,
                        "Content-Length": str(len(body)),
                    })
            finally:
                connection.close()
            self.assertTrue((root / "summary.json").is_file())
            self.assertEqual((root / "images/image_01.png").read_bytes(), expected_png)


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "requires macOS Swift/Network.framework")
class SwiftBundleUploaderTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.storage = tempfile.TemporaryDirectory(prefix="phonesaber-swift-upload-")
        cls.addClassCleanup(cls.storage.cleanup)
        root = Path(cls.storage.name)
        source = Path(__file__).resolve().parent.parent / "PhoneSaberSender"
        support = root / "Support.swift"
        support.write_text("""import Foundation
        enum P2PPreferredMac {
            static let coordinateDestination = (station: "", lanHost: "", manual: false)
            static let current: String? = nil
        }
        enum P2PDiagnosticsServicePicker {
            static func pick(from: [String], preferred: String?, final: Bool, station: String) -> String? { nil }
        }
        enum DebugRecordingLimits {
            static let maximumDiskUsageBytes: Int64 = 864 * 1024 * 1024
            static func shouldAutoTransfer(recordedFrames: Int) -> Bool { recordedFrames >= 30 }
        }
        enum DebugRecordingStorage {
            static func diskUsage(sessionID: String, in directory: URL) -> Int64 { 0 }
        }
        """, encoding="utf-8")
        main = root / "Upload.swift"
        main.write_text("""import Foundation
        import Network
        @main struct UploadCheck {
            static func main() {
                let done = DispatchSemaphore(value: 0)
                let port = NWEndpoint.Port(rawValue: UInt16(CommandLine.arguments[1])!)!
                let uploader = P2PBundleUploader(endpointOverride: .hostPort(host: "127.0.0.1", port: port), transferTimeout: 10)
                uploader.upload(fileURL: URL(fileURLWithPath: CommandLine.arguments[2]), sessionID: "sample_session") { outcome in
                    switch outcome {
                    case .uploaded: print("uploaded")
                    case .failed: print("failed")
                    case .notFound: print("notFound")
                    }
                    done.signal()
                }
                if done.wait(timeout: .now() + 15) != .success { exit(1) }
            }
        }
        """, encoding="utf-8")
        cls.binary = root / "upload"
        result = subprocess.run(["xcrun", "swiftc", "-O", "-module-cache-path", str(root / "cache"),
            str(source / "P2PProtocol.swift"), str(source / "DebugBundleTransfer.swift"),
            str(support), str(main), "-o", str(cls.binary)], capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise AssertionError(result.stderr)

    def upload(self, server, package):
        result = subprocess.run([str(self.binary), str(server.server_port), str(package)],
                                capture_output=True, text=True, timeout=20)
        self.assertEqual(result.returncode, 0, result.stderr)
        return result.stdout.strip()

    def test_real_uploader_retries_a_lost_reply_and_keeps_conflicts_failed(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            write_bundle(root / "bundle")
            image = root / "bundle/images/image_01.png"
            image.write_bytes(image.read_bytes() + bytes(range(251)) * 3000)
            package = root / "upload.psbt"
            protocol.pack_bundle(root / "bundle", package)
            server = TriageHTTPServer(("127.0.0.1", 0), root / "inbox", analysis_mode="disabled")
            thread = Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                # Store the bundle, then lose only the HTTP acknowledgment.
                def lost_reply(handler, status, payload):
                    handler.close_connection = True
                with mock.patch.object(TriageRequestHandler, "_reply", lost_reply):
                    self.assertEqual(self.upload(server, package), "failed")
                self.assertEqual(self.upload(server, package), "uploaded")
                saved = root / "inbox/phone_saber_triage_sample_session"
                self.assertEqual((saved / "images/image_01.png").read_bytes(), image.read_bytes())
                (root / "bundle/prompt.md").write_text("different")
                protocol.pack_bundle(root / "bundle", package)
                self.assertEqual(self.upload(server, package), "failed")
                self.assertFalse(list((root / "inbox").glob(".phonesaber-receive-*")))
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)

    def test_fragmented_duplicate_receipts_and_old_receiver_statuses(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            body = write_bundle(root / "bundle")
            package = root / "upload.psbt"
            package.write_bytes(body)
            server = TriageHTTPServer(("127.0.0.1", 0), root / "inbox", analysis_mode="disabled")
            thread = Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                for status, session, expected in [(200, "sample_session", "uploaded"),
                        (200, "wrong", "failed"), (409, "sample_session", "failed"),
                        (201, "sample_session", "uploaded")]:
                    def reply(handler, length):
                        handler.rfile.read(length)
                        payload = json.dumps({"status": "duplicate", "sessionID": session}).encode()
                        handler.send_response(status)
                        handler.send_header("Content-Length", str(len(payload)))
                        handler.send_header("Connection", "close")
                        handler.end_headers()
                        handler.wfile.flush()
                        # Ensure the receipt body is a separate receive callback.
                        time.sleep(0.05)
                        handler.wfile.write(payload)
                        handler.close_connection = True
                    with mock.patch.object(TriageRequestHandler, "_receive", reply):
                        self.assertEqual(self.upload(server, package), expected)
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)


if __name__ == "__main__":
    unittest.main()
