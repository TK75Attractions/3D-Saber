import json
import os
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path


TOOLS = Path(__file__).resolve().parent
KNOWN = "ChartRecordingWorkflowTests.ConfirmedBaselineFailure"


def report(cases, **overrides):
    counts = {key: sum(result == value for _, result in cases) for key, value in
              {"passed": "Passed", "failed": "Failed", "skipped": "Skipped",
               "inconclusive": "Inconclusive"}.items()}
    root = ET.Element("test-run", total=str(len(cases)),
                      result="Failed" if counts["failed"] else "Passed",
                      **{key: str(value) for key, value in counts.items()})
    root.attrib.update(overrides)
    for name, result in cases:
        ET.SubElement(root, "test-case", fullname=name, result=result)
    return ET.tostring(root, encoding="unicode")


class EditModeResultsTests(unittest.TestCase):
    def run_report(self, xml, known=None):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "results.xml"
            if xml is not None:
                path.write_text(xml)
            command = [sys.executable, "-B", str(TOOLS / "unity_editmode_results.py"),
                       str(path), "unused.log"]
            if known is not None:
                allowlist = Path(directory) / "known.json"
                allowlist.write_text(json.dumps(known))
                command += ["--known-failures", str(allowlist)]
            return subprocess.run(command, capture_output=True, text=True)

    def test_success_and_nonfailure_outcomes(self):
        self.assertEqual(self.run_report(report([
            ("A.Pass", "Passed"), ("A.Skip", "Skipped"),
            ("A.Inconclusive", "Inconclusive")])).returncode, 0)

    def test_ordinary_failure(self):
        self.assertEqual(self.run_report(report([("A.Fail", "Failed")])).returncode, 1)

    def test_aggregate_failure_without_failed_case(self):
        xml = '<test-run total="1" passed="0" failed="1" skipped="0" result="Failed"><test-suite result="Failed"/></test-run>'
        self.assertNotEqual(self.run_report(xml).returncode, 0)

    def test_new_chart_failure_is_not_exempt(self):
        xml = report([(KNOWN, "Failed"), ("ChartRecordingWorkflowTests.NewRegression", "Failed")])
        self.assertEqual(self.run_report(xml, [KNOWN]).returncode, 1)

    def test_exact_known_failure_is_visible_and_allowed(self):
        result = self.run_report(report([("A.Pass", "Passed"), (KNOWN, "Failed")]), [KNOWN])
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("KNOWN FAIL " + KNOWN, result.stdout)

    def test_known_failure_without_configuration_fails(self):
        self.assertEqual(self.run_report(report([(KNOWN, "Failed")])).returncode, 1)

    def test_known_failure_that_now_passes_is_allowed(self):
        self.assertEqual(self.run_report(report([(KNOWN, "Passed")]), [KNOWN]).returncode, 0)

    def test_parameterized_and_prefix_names_are_not_exempt(self):
        for name in (KNOWN + "Extra", KNOWN + "(1)"):
            with self.subTest(name=name):
                self.assertEqual(self.run_report(report([(name, "Failed")]), [KNOWN]).returncode, 1)

    def test_invalid_reports_fail_closed(self):
        good = report([("A.Pass", "Passed")])
        reports = [None, "<broken", good.replace("test-run", "other"), report([]),
                   good.replace('total="1"', ''), good.replace('passed="1"', ''),
                   good.replace('failed="0"', ''), good.replace('skipped="0"', ''),
                   good.replace('fullname="A.Pass"', ''),
                   report([("A.Pass", "Passed")], total="-1"),
                   report([("A.Pass", "Passed")], total="bad"),
                   report([("A.Pass", "Passed")], total="1.0"),
                   report([("A.Pass", "Passed")], total="2"),
                   report([("A.Pass", "Passed")], passed="-1"),
                   report([("A.Pass", "Passed")], passed="0", failed="1"),
                   report([("A.Fail", "Failed")], result="Passed"),
                   report([("A.Pass", "Passed")], result="Failed"),
                   report([("A.Unknown", "Unknown")]),
                   report([("A.Pass", "Passed"), ("A.Pass", "Passed")])]
        for xml in reports:
            with self.subTest(xml=xml):
                self.assertNotEqual(self.run_report(xml).returncode, 0)

    def test_suite_setup_failure_is_not_hidden_by_known_case(self):
        root = ET.fromstring(report([(KNOWN, "Failed")]))
        suite = ET.SubElement(root, "test-suite", result="Failed", site="SetUp")
        ET.SubElement(suite, "failure")
        self.assertNotEqual(self.run_report(ET.tostring(root, encoding="unicode"), [KNOWN]).returncode, 0)

    def test_unity_child_failure_summary_allows_only_known_case(self):
        root = ET.fromstring(report([(KNOWN, "Failed")], result="Failed(Child)"))
        case = root.find("test-case")
        root.remove(case)
        suite = ET.SubElement(root, "test-suite", result="Failed", site="Child")
        ET.SubElement(suite, "failure")
        suite.append(case)
        xml = ET.tostring(root, encoding="unicode")
        self.assertEqual(self.run_report(xml, [KNOWN]).returncode, 0)
        self.assertEqual(self.run_report(xml).returncode, 1)

    def test_shell_editmode_propagates_validator_status_with_stubs(self):
        # 実 Unity、rsync、既存アプリには触れず、引数と終了コードの連携だけを検証する。
        with tempfile.TemporaryDirectory(prefix="editmode stub ") as directory:
            folder = Path(directory)
            unity = folder / "fake unity"
            unity.write_text(f"#!{sys.executable}\nimport os, sys\nfrom pathlib import Path\n"
                             "Path(sys.argv[sys.argv.index('-testResults') + 1]).write_text(os.environ['TEST_XML'])\n")
            unity.chmod(0o755)
            rsync = folder / "rsync"
            rsync.write_text("#!/bin/sh\nexit 0\n")
            rsync.chmod(0o755)
            known = folder / "known failures.json"
            known.write_text(json.dumps([KNOWN]))
            env = dict(os.environ, PATH=str(folder) + os.pathsep + os.environ["PATH"],
                       PHONESABER_UNITY=str(unity), PHONESABER_UNITY_COPY=str(folder / "copy"),
                       PHONESABER_UNITY_OUT=str(folder / "output"),
                       PHONESABER_EDITMODE_KNOWN_FAILURES=str(known))
            for name, outcome, expected in [("A.Pass", "Passed", 0), (KNOWN, "Failed", 0),
                                             ("ChartRecordingWorkflowTests.NewRegression", "Failed", 1)]:
                with self.subTest(name=name):
                    env["TEST_XML"] = report([(name, outcome)])
                    run = subprocess.run(["bash", str(TOOLS / "unity_copy.sh"), "editmode"],
                                         env=env, capture_output=True, text=True)
                    self.assertEqual(run.returncode, expected, run.stdout + run.stderr)

    def test_invalid_allowlist_is_rejected(self):
        for known in (["ChartRecordingWorkflowTests.*"], [""], [12], {}, "A.Fail"):
            with self.subTest(known=known):
                self.assertNotEqual(self.run_report(report([("A.Pass", "Passed")]), known).returncode, 0)


if __name__ == "__main__":
    unittest.main()
