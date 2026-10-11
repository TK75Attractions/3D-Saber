import contextlib
import io
import json
import re
import tempfile
import unittest
from pathlib import Path
from unittest import mock

import unity_playmode_batches as batches


class UnityPlayModeBatchesTests(unittest.TestCase):
    def test_empty_results_retry_and_fail_cli(self):
        with tempfile.TemporaryDirectory() as folder:
            def fake_unity(command, **kwargs):
                Path(command[command.index("-testResults") + 1]).write_text(
                    '<test-run total="0" passed="0" failed="0" skipped="0"/>')
                return batches.subprocess.CompletedProcess(command, 0)

            with mock.patch.object(batches.subprocess, "run", side_effect=fake_unity) as launch:
                with contextlib.redirect_stdout(io.StringIO()):
                    result = batches.main(["--project", folder, "--out", folder, "--classes", "ExampleTests"])
            self.assertEqual(result, 1)
            self.assertEqual(launch.call_count, 2)
            summary = json.loads((Path(folder) / "summary.json").read_text())
            self.assertEqual(summary["crashed"], ["ExampleTests"])
            self.assertEqual(summary["total"], 0)

    def test_unusable_results_are_rejected(self):
        reports = [
            '<test-run/>',
            '<test-run total="invalid" passed="0" failed="0" skipped="0"/>',
            '<test-run total="1" passed="-1" failed="0" skipped="0"/>',
            '<other total="1" passed="1" failed="0" skipped="0"/>',
        ]
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "results.xml"
            for report in reports:
                with self.subTest(report=report):
                    path.write_text(report)
                    self.assertIsNone(batches.parse_results(path))

    def test_valid_results_preserve_counts_and_failed_names(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "results.xml"
            path.write_text('<test-run total="3" passed="1" failed="1" skipped="1">'
                            '<test-case fullname="A.Pass" result="Passed"/>'
                            '<test-case fullname="A.Fail" result="Failed"/>'
                            '<test-case fullname="A.Skip" result="Skipped"/></test-run>')
            self.assertEqual(batches.parse_results(path), {
                "total": 3, "passed": 1, "failed": 1, "skipped": 1, "failed_tests": ["A.Fail"]})

    def test_discovers_only_classes_in_files_with_tests(self):
        with tempfile.TemporaryDirectory() as root:
            folder = Path(root)
            (folder / "A.cs").write_text("public class APlayTests {\n [UnityTest] public IEnumerator X() { yield break; } }\n")
            (folder / "Helper.cs").write_text("public static class Helper { }\n")
            (folder / "B.cs").write_text("public sealed class BTests { [TestCase(1)] public void Y(int v) {} }\n")
            (folder / "C.cs").write_text("public class CPlayTests { [UnityTest, Timeout(360000)] public IEnumerator Z() { yield break; } }\n")
            self.assertEqual(batches.discover_classes(folder), ["APlayTests", "BTests", "CPlayTests"])

    def test_crash_after_writing_passed_xml_is_retried_and_fails_cli(self):
        with tempfile.TemporaryDirectory() as folder:
            def fake_unity(command, **kwargs):
                Path(command[command.index("-testResults") + 1]).write_text(
                    '<test-run total="1" passed="1" failed="0" skipped="0"/>')
                return batches.subprocess.CompletedProcess(command, -11)

            with mock.patch.object(batches.subprocess, "run", side_effect=fake_unity) as launch, \
                    contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                code = batches.main(["--project", folder, "--out", folder, "--classes", "ExampleTests"])
            self.assertEqual(code, 1)
            self.assertEqual(launch.call_count, 2)
            summary = json.loads((Path(folder) / "summary.json").read_text())
            self.assertEqual(summary["crashed"], ["ExampleTests"])

    def test_test_runner_exit_two_preserves_the_failed_test_without_retry(self):
        with tempfile.TemporaryDirectory() as folder:
            def fake_unity(command, **kwargs):
                Path(command[command.index("-testResults") + 1]).write_text(
                    '<test-run total="1" passed="0" failed="1" skipped="0">'
                    '<test-case fullname="ExampleTests.Broken" result="Failed"/></test-run>')
                return batches.subprocess.CompletedProcess(command, 2)

            with mock.patch.object(batches.subprocess, "run", side_effect=fake_unity) as launch, \
                    contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()):
                code = batches.main(["--project", folder, "--out", folder, "--classes", "ExampleTests"])
            self.assertEqual(code, 1)
            self.assertEqual(launch.call_count, 1)
            summary = json.loads((Path(folder) / "summary.json").read_text())
            self.assertEqual(summary["failed_tests"], ["ExampleTests.Broken"])
            self.assertEqual(summary["failed"], 1)
            self.assertEqual(summary["crashed"], [])

    def test_skipped_or_inconclusive_tests_are_not_a_success(self):
        for passed, skipped in [(0, 1), (1, 1), (0, 0)]:
            with self.subTest(passed=passed, skipped=skipped), tempfile.TemporaryDirectory() as folder:
                result = {"total": max(1, passed + skipped), "passed": passed, "failed": 0,
                          "skipped": skipped, "failed_tests": []}
                with mock.patch.object(batches, "run_group", return_value=result), \
                        contextlib.redirect_stdout(io.StringIO()):
                    code = batches.main(["--project", folder, "--out", folder, "--classes", "ExampleTests"])
                self.assertEqual(code, 1)

    def test_chunks_cover_every_class_once(self):
        items = [f"C{i}" for i in range(11)]
        groups = batches.chunk(items, 4)
        self.assertEqual(sum(groups, []), items)
        self.assertEqual(len(groups), 4)
        self.assertEqual(batches.chunk(["A"], 6), [["A"]])

    def test_filter_is_anchored(self):
        pattern = re.compile(batches.test_filter(["PhoneSaberTests"]))
        self.assertTrue(pattern.search("PhoneSaberTests.Run"))
        self.assertFalse(pattern.search("PhoneSaberTestsExtra.Run"))
        self.assertFalse(pattern.search("OtherPhoneSaberTests.Run"))

    def test_crashed_group_is_retried_then_split_to_the_class(self):
        calls = []

        def fake(project, out, name, classes, unity):
            calls.append(tuple(classes))
            if "Bad" in classes:
                return None
            return {"total": len(classes), "passed": len(classes), "failed": 0, "skipped": 0, "failed_tests": []}

        with tempfile.TemporaryDirectory() as out, mock.patch.object(batches, "run_group", side_effect=fake):
            summary = batches.run(Path("/copy"), Path(out), ["A", "Bad", "C", "D"], groups=1)
        self.assertEqual(summary["crashed"], ["Bad"])
        self.assertEqual(summary["passed"], 3)
        self.assertEqual(calls[0], ("A", "Bad", "C", "D"))
        self.assertEqual(calls[1], ("A", "Bad", "C", "D"))  # one retry before splitting

    def test_a_run_that_executed_no_tests_is_not_reported_as_success(self):
        empty = {"total": 0, "passed": 0, "failed": 0, "skipped": 0, "failed_tests": []}
        with tempfile.TemporaryDirectory() as out, \
                mock.patch.object(batches, "run_group", return_value=empty), \
                contextlib.redirect_stdout(io.StringIO()), contextlib.redirect_stderr(io.StringIO()) as error:
            code = batches.main(["--project", "/copy", "--out", out, "--classes", "RenamedPlayTests"])
        self.assertEqual(code, 1)
        self.assertIn("1件も実行されていません", error.getvalue())


if __name__ == "__main__":
    unittest.main()
