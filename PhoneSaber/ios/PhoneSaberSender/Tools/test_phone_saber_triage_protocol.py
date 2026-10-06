#!/usr/bin/env python3
"""Tests for bounded PhoneSaber triage bundle packaging and receiving."""

from __future__ import annotations

import hashlib
import io
import json
import struct
import sys
import tempfile
import unittest
from http.client import HTTPConnection
from pathlib import Path
from threading import Thread

sys.path.insert(0, str(Path(__file__).resolve().parent))

import phone_saber_triage_protocol as protocol
from phone_saber_triage_receiver import TriageHTTPServer
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


if __name__ == "__main__":
    unittest.main()
