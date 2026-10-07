import contextlib
import io
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

from Tools import verify_unity_tests as runner


class UnityResultTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="unity-result-test-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.result = self.root / "probe.xml"

    def parse(self, xml):
        self.result.write_text(xml, encoding="utf-8")
        return runner.parse_result(self.result)

    def test_accepts_completed_run_and_reports_skips(self):
        status, detail = self.parse(
            '<test-run result="Passed" total="3" passed="2" failed="0" skipped="1"/>')
        self.assertEqual(status, "PASS")
        self.assertIn("2/3 passed", detail)
        self.assertIn("skipped=1", detail)

    def test_rejects_empty_or_unexecuted_run(self):
        for counts in ('total="0" passed="0"', 'total="3" passed="0" skipped="3"'):
            with self.subTest(counts=counts):
                status, _ = self.parse(f'<test-run result="Passed" failed="0" {counts}/>')
                self.assertNotEqual(status, "PASS")

    def test_rejects_wrong_root_and_invalid_counts(self):
        for xml in (
            '<not-a-test-run result="Passed" total="3" passed="3" failed="0"/>',
            '<test-run result="Passed"/>',
            '<test-run result="Passed" total="3" passed="3" failed="many"/>',
            '<test-run result="Passed" total="3" passed="3" failed="-1"/>',
            '<test-run result="Passed" total="1" passed="3" failed="0"/>',
        ):
            with self.subTest(xml=xml):
                self.assertEqual(self.parse(xml)[0], "MALFORMED")

    def test_failed_inconclusive_missing_and_broken_results_do_not_pass(self):
        self.assertEqual(runner.parse_result(self.result)[0], "NO_XML")
        self.assertEqual(self.parse('<test-run')[0], "MALFORMED")
        for xml in (
            '<test-run result="Failed" total="3" passed="2" failed="1"/>',
            '<test-run result="Passed" total="3" passed="2" failed="1"/>',
            '<test-run result="Passed" total="3" passed="2" failed="0" inconclusive="1"/>',
        ):
            with self.subTest(xml=xml):
                self.assertEqual(self.parse(xml)[0], "FAIL")

    def run_process(self, script):
        # Unity の代わりに実プロセスを使い、終了コードと結果ファイルの契約を検証する。
        popen = subprocess.Popen

        def launch(command, **kwargs):
            return popen([sys.executable, "-c", script, *command[1:]], **kwargs)

        with mock.patch.object(runner.subprocess, "Popen", side_effect=launch), \
                contextlib.redirect_stdout(io.StringIO()):
            return runner.run_group(Path("fake-unity"), self.root, self.root,
                                    {"name": "probe", "platform": "editmode",
                                     "filter": None, "timeout": 10}, True)

    def test_successful_exit_without_new_xml_cannot_reuse_previous_pass(self):
        self.parse('<test-run result="Passed" total="3" passed="3" failed="0"/>')
        self.assertEqual(self.run_process("pass"), "NO_XML")

    def test_previous_lock_log_cannot_misclassify_new_failure(self):
        (self.root / "probe.log").write_text("project already open", encoding="utf-8")
        self.assertEqual(self.run_process("raise SystemExit(1)"), "EXECUTION_FAILURE")

    def test_new_results_respect_process_exit_status(self):
        script = ('import pathlib, sys; '
                  'pathlib.Path(sys.argv[sys.argv.index("-testResults") + 1]).write_text('
                  '\'<test-run result="Passed" total="3" passed="3" failed="0"/>\')')
        self.assertEqual(self.run_process(script), "PASS")
        self.assertEqual(self.run_process(script + "; raise SystemExit(1)"), "EXECUTION_FAILURE")


if __name__ == "__main__":
    unittest.main()
