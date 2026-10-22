"""診断受信機のBonjour広告を、実ネットワークに公開せず検証する。"""

import json
from pathlib import Path
import tempfile
import unittest
from unittest import mock

import phone_saber_triage_receiver as receiver


class TriageBonjourTests(unittest.TestCase):
    def test_advertisement_command_with_fake_dns_sd(self):
        with tempfile.TemporaryDirectory(prefix="phonesaber-bonjour-") as directory:
            root = Path(directory)
            args_file = root / "args.json"
            dns_sd = root / "dns-sd"
            dns_sd.write_text(
                "#!/usr/bin/env python3\nimport json, sys\n"
                f"with open({str(args_file)!r}, 'w') as output:\n"
                "    json.dump(sys.argv[1:], output)\n"
            )
            dns_sd.chmod(0o755)
            # 固定の実行ファイルパスだけ差し替え、実際にPopenした引数を取得する。
            with mock.patch.object(receiver.sys, "platform", "darwin"), \
                    mock.patch.object(receiver, "Path", return_value=dns_sd):
                process = receiver._publish_bonjour(8765, "Phone Saber Diagnostics (Mac)")
            self.assertIsNotNone(process)
            try:
                self.assertEqual(process.wait(timeout=5), 0)
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=5)
            self.assertEqual(json.loads(args_file.read_text()), [
                "-R", "Phone Saber Diagnostics (Mac)", "_phonesaber-diag._tcp.", "local.", "8765",
            ])

    def test_receiver_publishes_bound_port_and_stops_advertisement(self):
        with tempfile.TemporaryDirectory(prefix="phonesaber-bonjour-main-") as directory:
            server = mock.MagicMock(server_port=18765, analysis_mode="disabled")
            server.serve_forever.side_effect = KeyboardInterrupt
            with mock.patch.object(receiver, "TriageHTTPServer", return_value=server), \
                    mock.patch.object(receiver, "_publish_bonjour") as publish:
                self.assertEqual(receiver.main([
                    "--inbox", directory, "--no-codex", "--no-overview",
                    "--service-name", "Phone Saber Diagnostics",
                ]), 0)
            publish.assert_called_once_with(18765, "Phone Saber Diagnostics")
            publish.return_value.terminate.assert_called_once_with()
            publish.return_value.wait.assert_called_once_with(timeout=2)
            server.server_close.assert_called_once_with()

    def test_missing_dns_sd_keeps_address_upload_available(self):
        with mock.patch.object(receiver.sys, "platform", "darwin"), \
                mock.patch.object(receiver.Path, "is_file", return_value=False), \
                mock.patch.object(receiver.subprocess, "Popen") as popen:
            self.assertIsNone(receiver._publish_bonjour(8765, "Phone Saber Diagnostics"))
        popen.assert_not_called()


if __name__ == "__main__":
    unittest.main()
