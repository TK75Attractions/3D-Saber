import unittest

import xctest_result_classifier as classifier


class XCTestResultClassifierTests(unittest.TestCase):
    def test_passed_result_bundle(self):
        result = classifier.classify_result(
            {
                "result": "Passed",
                "totalTestCount": 72,
                "passedTests": 72,
                "failedTests": 0,
                "skippedTests": 0,
                "testFailures": [],
            },
            xcodebuild_exit_code=0,
        )
        self.assertEqual(result["classification"], "PASS")
        self.assertEqual(result["passed_tests"], 72)

    def test_assertion_failure_is_not_a_worker_kill(self):
        result = classifier.classify_result(
            {
                "result": "Failed",
                "totalTestCount": 72,
                "passedTests": 71,
                "failedTests": 1,
                "testFailures": [
                    {
                        "testName": "testMetadataVersion",
                        "targetName": "PhoneSaberSenderTests",
                        "failureText": 'XCTAssertEqual failed: ("1") is not equal to ("2")',
                    },
                ],
            },
            xcodebuild_exit_code=65,
        )
        self.assertEqual(result["classification"], "ASSERTION_FAILURE")
        self.assertEqual(len(result["assertion_evidence"]), 1)
        self.assertEqual(result["worker_kill_evidence"], [])

    def test_worker_kill_is_not_an_assertion_failure(self):
        result = classifier.classify_result(
            {
                "result": "Failed",
                "totalTestCount": 72,
                "passedTests": 38,
                "failedTests": 1,
                "testFailures": [
                    {
                        "testName": "PhoneSaberSenderTests",
                        "targetName": "PhoneSaberSenderTests",
                        "failureText": "Test worker was killed by the system (signal 9)",
                    },
                ],
            },
            xcodebuild_exit_code=65,
        )
        self.assertEqual(result["classification"], "WORKER_KILLED")
        self.assertEqual(result["assertion_evidence"], [])
        self.assertGreaterEqual(len(result["worker_kill_evidence"]), 1)

    def test_worker_kill_in_logs_without_result_bundle(self):
        result = classifier.classify_result(
            None,
            xcodebuild_exit_code=65,
            xcodebuild_stderr="Test runner worker was terminated by SIGKILL after memory pressure.",
        )
        self.assertEqual(result["classification"], "WORKER_KILLED")
        self.assertEqual(result["assertion_evidence"], [])

    def test_passed_run_records_transient_worker_kill(self):
        result = classifier.classify_result(
            {
                "result": "Passed",
                "totalTestCount": 72,
                "passedTests": 72,
                "failedTests": 0,
                "testFailures": [],
            },
            xcodebuild_exit_code=0,
            xcodebuild_stderr="Test worker was killed by SIGKILL; Xcode restarted it.",
        )
        self.assertEqual(result["classification"], "PASS_WITH_WORKER_KILL")
        self.assertEqual(result["failed_tests"], 0)
        self.assertGreaterEqual(len(result["worker_kill_evidence"]), 1)


if __name__ == "__main__":
    unittest.main()
