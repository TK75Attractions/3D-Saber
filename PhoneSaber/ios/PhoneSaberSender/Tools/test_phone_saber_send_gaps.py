#!/usr/bin/env python3
"""接続ログ1ファイルの追加と旧録画の互換性。"""
import io
import tempfile
import unittest
from http.client import HTTPConnection
from pathlib import Path
from threading import Thread

from phone_saber_session_report import build_report, render_markdown
from phone_saber_triage_codex import input_plan
from phone_saber_triage_protocol import BundleError, CONTENT_TYPE, pack_bundle, receive_bundle
from phone_saber_triage_receiver import TriageHTTPServer
from test_phone_saber_triage_protocol import empty_bundle_files, raw_envelope, write_bundle
from test_phone_saber_triage_codex import write_codex_bundle
from phone_saber_test_isolation import isolate_codex_logs as setUpModule
from phone_saber_test_isolation import restore_codex_logs as tearDownModule


class SendGapBundleTests(unittest.TestCase):
    def test_http_receiver_preserves_optional_log_and_accepts_old_bundle(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            log = "2026-10-13T16:15:40.000Z [SEND_GAP] cause=processing stalled 日本語\n"
            for present in [False, True]:
                bundle = root / f"source-{present}"
                write_bundle(bundle)
                if present:
                    (bundle / "connectivity.log").write_text(log, encoding="utf-8")
                package = root / "test.psbt"
                pack_bundle(bundle, package)
                server = TriageHTTPServer(("127.0.0.1", 0), root / f"inbox-{present}", analysis_mode="disabled")
                thread = Thread(target=server.serve_forever, daemon=True)
                thread.start()
                try:
                    connection = HTTPConnection(*server.server_address, timeout=5)
                    connection.request("POST", "/v1/bundle", body=package.read_bytes(), headers={"Content-Type": CONTENT_TYPE})
                    response = connection.getresponse()
                    self.assertEqual(response.status, 201, response.read())
                    connection.close()
                    received = next(p for p in (root / f"inbox-{present}").glob("phone_saber_triage_*") if p.is_dir())
                    self.assertEqual((received / "connectivity.log").exists(), present)
                    if present:
                        self.assertEqual((received / "connectivity.log").read_text(encoding="utf-8"), log)
                finally:
                    server.shutdown()
                    server.server_close()
                    thread.join(timeout=2)

    def test_report_and_codex_input_accept_both_versions_without_modifying_bundle(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle)
            before = (bundle / "summary.json").read_bytes()
            self.assertEqual(build_report(bundle)["connectivity"], {"present": False, "lines": []})
            self.assertIn("旧バンドル", render_markdown(build_report(bundle)))
            log = "2026-10-13T16:15:40Z [SEND_GAP] cause=camera delivered no frame <script>\n"
            (bundle / "connectivity.log").write_text(log, encoding="utf-8")
            input_plan(bundle)
            report = build_report(bundle)
            self.assertEqual(report["inputContract"], "PASS")
            self.assertEqual(report["connectivity"], {"present": True, "lines": log.splitlines()})
            self.assertIn("cause=camera delivered no frame &lt;script&gt;", render_markdown(report))
            self.assertEqual((bundle / "summary.json").read_bytes(), before)
            self.assertEqual((bundle / "connectivity.log").read_text(), log)

    def test_size_limit_and_unsafe_paths_are_rejected_on_both_sides(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_codex_bundle(bundle)
            path = bundle / "connectivity.log"
            path.write_bytes(b"x" * (64 * 1024 + 1))
            with self.assertRaises(BundleError):
                pack_bundle(bundle, root / "oversize.psbt")
            with self.assertRaises(BundleError):
                input_plan(bundle)
            for name, data in [("connectivity.log", b"x" * (64 * 1024 + 1)), ("../connectivity.log", b"log"),
                               ("logs/connectivity.log", b"log")]:
                payload = raw_envelope(empty_bundle_files() + [(name, data)])
                with self.assertRaises(BundleError):
                    receive_bundle(io.BytesIO(payload), len(payload), root / "inbox")
            path.unlink()
            path.symlink_to(bundle / "prompt.md")
            with self.assertRaises(BundleError):
                pack_bundle(bundle, root / "symlink.psbt")
            with self.assertRaises(BundleError):
                input_plan(bundle)

    def test_twenty_images_plus_log_remain_within_file_count_limit(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_bundle(bundle, image_count=20)
            (bundle / "connectivity.log").write_bytes(b"x" * (64 * 1024))
            package = root / "max.psbt"
            pack_bundle(bundle, package)
            data = package.read_bytes()
            received = receive_bundle(io.BytesIO(data), len(data), root / "inbox")
            self.assertEqual((received / "connectivity.log").stat().st_size, 64 * 1024)
            self.assertEqual(len([p for p in received.rglob("*") if p.is_file()]), 43)


if __name__ == "__main__":
    unittest.main()
