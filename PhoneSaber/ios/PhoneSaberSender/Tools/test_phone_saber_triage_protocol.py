#!/usr/bin/env python3
"""Tests for bounded PhoneSaber triage bundle packaging and receiving."""

from __future__ import annotations

import hashlib
import io
import json
import struct
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from unittest import mock
from http.client import HTTPConnection
from pathlib import Path
from threading import Thread

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


class TriageBundleProtocolTests(unittest.TestCase):
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
            body = write_bundle(root)
            inbox = Path(directory) / "inbox"
            received = protocol.receive_bundle(io.BytesIO(body), len(body), inbox)
            self.assertEqual(received.name, "phone_saber_triage_sample_session")
            self.assertEqual((received / "images/image_01.png").read_bytes(), png_bytes())
            self.assertTrue((received / "frames/frame_100_1.json").is_file())
            self.assertFalse(any(received.glob("*.mp4")))

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
