"""異常入力負荷試験で見つけた最小再現。ネットワークと保存先は loopback/tmp のみ。"""
import errno
import io
import json
from pathlib import Path
import struct
import tempfile
import threading
import unittest
from unittest import mock

import phone_saber_triage_protocol as protocol
import phone_saber_status as status
from phone_saber_triage_receiver import TriageHTTPServer
from phone_saber_test_isolation import isolate_codex_logs as setUpModule, restore_codex_logs as tearDownModule  # noqa: F401
from test_phone_saber_triage_protocol import empty_bundle_files, raw_envelope, write_bundle


class MalformedBundleTests(unittest.TestCase):
    def reject(self, data):
        with tempfile.TemporaryDirectory() as directory:
            inbox = Path(directory) / 'inbox'
            with self.assertRaises(protocol.BundleError):
                protocol.receive_bundle(io.BytesIO(data), len(data), inbox)
            self.assertFalse(inbox.exists() and list(inbox.iterdir()))

    def test_nested_manifest_is_a_bundle_error(self):
        manifest = b'[' * 10000 + b'0' + b']' * 10000
        self.reject(b'PSBT' + struct.pack('>I', len(manifest)) + manifest)

    def test_nested_summary_cannot_crash_packaging(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'bundle'
            root.mkdir()
            (root / 'summary.json').write_text('[' * 10000 + '0' + ']' * 10000)
            (root / 'prompt.md').write_text('x')
            with self.assertRaises(protocol.BundleError):
                protocol.pack_bundle(root, Path(directory) / 'bundle.psbt')

    def test_nested_summary_is_a_bundle_error(self):
        self.reject(raw_envelope(empty_bundle_files('[' * 10000 + '0' + ']' * 10000)))

    def test_nested_context_is_a_bundle_error(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'bundle'
            write_bundle(root)
            files = [(p.relative_to(root).as_posix(), p.read_bytes()) for p in root.rglob('*') if p.is_file()]
            files = [(name, b'[' * 10000 + b'0' + b']' * 10000 if name.startswith('frames/') else data)
                     for name, data in files]
            self.reject(raw_envelope(files))

    def test_array_or_object_color_is_a_bundle_error(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory) / 'bundle'
            write_bundle(root)
            for color in ([], {}):
                summary = json.loads((root / 'summary.json').read_text())
                summary['images'][0]['color'] = color
                files = [(p.relative_to(root).as_posix(), p.read_bytes()) for p in root.rglob('*') if p.is_file()]
                files = [(name, json.dumps(summary).encode() if name == 'summary.json' else data) for name, data in files]
                self.reject(raw_envelope(files))

    def test_manifest_larger_than_http_body_is_rejected_before_reading_it(self):
        source = io.BytesIO(b'PSBT' + struct.pack('>I', 100) + b'{}')
        with tempfile.TemporaryDirectory() as directory:
            with mock.patch.object(protocol, '_read_exact', wraps=protocol._read_exact) as read:
                with self.assertRaises(protocol.BundleError):
                    protocol.receive_bundle(source, 8, Path(directory))
            self.assertEqual([call.args[1] for call in read.call_args_list], [4, 4])

    def test_http_errors_and_disk_full_keep_serving(self):
        from http.client import HTTPConnection
        with tempfile.TemporaryDirectory() as directory:
            inbox = Path(directory) / 'inbox'
            server = TriageHTTPServer(('127.0.0.1', 0), inbox, analysis_mode='disabled')
            threading.Thread(target=server.serve_forever, daemon=True).start()
            try:
                manifest = b'[' * 10000 + b'0' + b']' * 10000
                deep = b'PSBT' + struct.pack('>I', len(manifest)) + manifest
                valid = raw_envelope(empty_bundle_files())
                upload_finished = server.upload_finished
                finished = threading.Event()

                def finish_upload():
                    upload_finished()
                    finished.set()

                for body, full in ((deep, False), (valid, True)):
                    finished.clear()
                    connection = HTTPConnection('127.0.0.1', server.server_port, timeout=3)
                    try:
                        with mock.patch.object(server, 'upload_finished', side_effect=finish_upload), \
                             (mock.patch.object(protocol.os, 'replace', side_effect=OSError(errno.ENOSPC, 'disk full')) if full else mock.patch.object(server, 'write_report')):
                            connection.request('POST', '/v1/bundle', body=body, headers={'Content-Type': protocol.CONTENT_TYPE})
                            response = connection.getresponse()
                            self.assertEqual(response.status, 400)
                            response.read()
                            # 本文の受信だけでは、ハンドラーの finally が終わった保証にならない。
                            self.assertTrue(finished.wait(3), 'upload cleanup did not finish')
                    finally:
                        connection.close()
                    self.assertFalse(inbox.exists() and list(inbox.iterdir()))
                    self.assertEqual(server.health()['uploadsInProgress'], 0)
                connection = HTTPConnection('127.0.0.1', server.server_port, timeout=3)
                connection.request('GET', '/health')
                response = connection.getresponse()
                self.assertEqual(response.status, 200)
                response.read()
                connection.close()
            finally:
                server.shutdown()
                server.server_close()


class CorruptStatusTests(unittest.TestCase):
    def context(self, root, payload='{}'):
        return status.Context(home=root, repo_root=root, unity_root=None, inbox=root / 'inbox',
                              runner=lambda *args: None, http_get=lambda *args: (200, payload))

    def test_non_object_or_deep_health_does_not_crash(self):
        for text in ('[]', 'null', '[' * 10000 + '0' + ']' * 10000):
            with self.subTest(text=text[:10]):
                checks = status.check_receiver(self.context(Path('/tmp'), text))
                self.assertEqual(checks[0].level, 'NG')

    def test_scalar_changed_files_does_not_crash(self):
        text = '{"service":"phonesaber-triage","status":"ready","codeChangedSinceStart":true,"changedFiles":3}'
        self.assertTrue(status.check_receiver(self.context(Path('/tmp'), text)))

    def test_too_many_digits_in_log_counter_are_ignored(self):
        self.assertEqual(status.parse_stats('RED=' + '9' * 10000 + ' BLUE=10'), {'BLUE': 10})

    def test_corrupt_analysis_report_does_not_crash(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / 'inbox/phone_saber_triage_sample'
            bundle.mkdir(parents=True)
            for report in ('[]', 'null', '[' * 10000 + '0' + ']' * 10000,
                           '{"precheck":{"reasonCodes":[{},3]}}'):
                with self.subTest(report=report[:20]):
                    (bundle / 'analysis_report.json').write_text(report)
                    self.assertTrue(status.check_inbox(self.context(root)))


if __name__ == '__main__':
    unittest.main()
