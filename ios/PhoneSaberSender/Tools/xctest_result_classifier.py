#!/usr/bin/env python3
"""Classify a PhoneSaber XCTest run using its xcresult summary and logs."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any


ASSERTION_PATTERN = re.compile(
    r"\bXCTAssert[A-Za-z0-9_]*\b.{0,120}\bfailed\b"
    r"|\bXCTFail\b"
    r"|\bAssertion failed\b"
    r"|\bfailed assertion\b",
    re.IGNORECASE,
)
WORKER_KILL_PATTERN = re.compile(
    r"\b(?:test\s*)?(?:worker|runner|test runner|xctest(?: worker| process)?|"
    r"simulator test worker|testmanagerd)\b.{0,220}"
    r"\b(?:was\s+)?(?:killed|terminated|received\s+SIGKILL|sent\s+SIGKILL|"
    r"signal\s*9|exited\s+(?:with\s+)?(?:signal\s*)?(?:9|137)|"
    r"crashed?\s+due\s+to\s+memory\s+pressure)\b"
    r"|\b(?:killed|terminated|SIGKILL|signal\s*9)\b.{0,220}"
    r"\b(?:worker|runner|xctest|simulator test worker|testmanagerd)\b",
    re.IGNORECASE,
)


def _integer(value: Any, fallback: int = 0) -> int:
    try:
        parsed = int(value)
    except (TypeError, ValueError):
        return fallback
    return parsed if parsed >= 0 else fallback


def _lines_and_adjacent_pairs(text: str) -> list[str]:
    lines = [line.strip() for line in text.splitlines() if line.strip()]
    return lines + [f"{left} {right}" for left, right in zip(lines, lines[1:])]


def classify_result(
    summary: dict[str, Any] | None,
    *,
    xcodebuild_exit_code: int,
    xcodebuild_stdout: str = "",
    xcodebuild_stderr: str = "",
    summary_tool_stderr: str = "",
) -> dict[str, Any]:
    """Return an outcome and independent assertion/worker-kill diagnostics."""
    summary = summary if isinstance(summary, dict) else None
    failure_records = summary.get("testFailures", []) if summary else []
    if not isinstance(failure_records, list):
        failure_records = []
    failure_records = [record for record in failure_records if isinstance(record, dict)]

    failed_tests = _integer(
        summary.get("failedTests") if summary else None,
        fallback=len(failure_records),
    )
    passed_tests = _integer(summary.get("passedTests") if summary else None)
    skipped_tests = _integer(summary.get("skippedTests") if summary else None)
    total_tests = _integer(summary.get("totalTestCount") if summary else None)
    test_result = summary.get("result", "unknown") if summary else "unknown"

    failure_texts = [
        " ".join(
            str(record.get(key, ""))
            for key in ("testName", "targetName", "failureText")
        ).strip()
        for record in failure_records
    ]
    failure_texts = [text for text in failure_texts if text]
    log_texts = [xcodebuild_stdout, xcodebuild_stderr, summary_tool_stderr]

    assertion_evidence = [text for text in failure_texts if ASSERTION_PATTERN.search(text)]
    if not assertion_evidence and (failed_tests > 0 or xcodebuild_exit_code != 0 or summary is None):
        assertion_evidence = [
            line
            for text in log_texts
            for line in _lines_and_adjacent_pairs(text)
            if ASSERTION_PATTERN.search(line)
        ]
    # Keep diagnostics compact while retaining one record for each distinct assertion.
    assertion_evidence = list(dict.fromkeys(assertion_evidence))

    worker_candidates = list(failure_texts)
    if summary:
        warnings = summary.get("runtimeWarnings", [])
        if isinstance(warnings, list):
            worker_candidates.extend(
                str(warning.get("message", ""))
                for warning in warnings
                if isinstance(warning, dict)
            )
    worker_candidates.extend(
        line
        for text in log_texts
        for line in _lines_and_adjacent_pairs(text)
    )
    worker_kill_evidence = list(
        dict.fromkeys(
            candidate
            for candidate in worker_candidates
            if candidate and WORKER_KILL_PATTERN.search(candidate)
        )
    )

    has_test_failure = failed_tests > 0 or bool(failure_records)
    has_assertion_failure = bool(assertion_evidence) and (
        has_test_failure or xcodebuild_exit_code != 0 or summary is None
    )
    has_worker_kill = bool(worker_kill_evidence)
    has_passed_summary = (
        summary is not None
        and test_result == "Passed"
        and total_tests > 0
        and failed_tests == 0
    )

    if has_assertion_failure and has_worker_kill:
        classification = "ASSERTION_FAILURE_AND_WORKER_KILLED"
    elif has_assertion_failure:
        classification = "ASSERTION_FAILURE"
    elif has_worker_kill and has_passed_summary and xcodebuild_exit_code == 0:
        classification = "PASS_WITH_WORKER_KILL"
    elif has_worker_kill:
        classification = "WORKER_KILLED"
    elif has_test_failure:
        classification = "TEST_FAILURE"
    elif xcodebuild_exit_code in (9, 137):
        classification = "XCODEBUILD_PROCESS_KILLED"
    elif xcodebuild_exit_code == 0 and has_passed_summary:
        classification = "PASS"
    else:
        classification = "EXECUTION_FAILURE"

    return {
        "classification": classification,
        "summary_available": summary is not None,
        "test_result": test_result,
        "total_tests": total_tests,
        "passed_tests": passed_tests,
        "failed_tests": failed_tests,
        "skipped_tests": skipped_tests,
        "assertion_evidence": assertion_evidence,
        "worker_kill_evidence": worker_kill_evidence,
        "failure_texts": failure_texts,
    }


def _read_text(path: str | None) -> str:
    if not path:
        return ""
    try:
        return Path(path).read_text(encoding="utf-8", errors="replace")
    except OSError as error:
        return f"Could not read {path}: {error}"


def _load_summary(path: str) -> tuple[dict[str, Any] | None, str]:
    try:
        value = json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, UnicodeError, json.JSONDecodeError) as error:
        return None, str(error)
    if not isinstance(value, dict):
        return None, "xcresult summary JSON must contain an object"
    return value, ""


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--summary", required=True, help="xcresulttool summary JSON log")
    parser.add_argument("--summary-exit-code", required=True, type=int)
    parser.add_argument("--summary-stderr-log", required=True)
    parser.add_argument("--xcodebuild-exit-code", required=True, type=int)
    parser.add_argument("--xcodebuild-stdout-log", required=True)
    parser.add_argument("--xcodebuild-stderr-log", required=True)
    args = parser.parse_args()

    summary, summary_error = _load_summary(args.summary)
    summary_stderr = _read_text(args.summary_stderr_log)
    result = classify_result(
        summary,
        xcodebuild_exit_code=args.xcodebuild_exit_code,
        xcodebuild_stdout=_read_text(args.xcodebuild_stdout_log),
        xcodebuild_stderr=_read_text(args.xcodebuild_stderr_log),
        summary_tool_stderr=summary_stderr,
    )

    print(f"classification={result['classification']}")
    print(f"xcodebuild_exit_code={args.xcodebuild_exit_code}")
    print(f"xcresult_summary_exit_code={args.summary_exit_code}")
    print(f"xcresult_summary_available={str(result['summary_available']).lower()}")
    print(f"test_result={result['test_result']}")
    print(f"total_tests={result['total_tests']}")
    print(f"passed_tests={result['passed_tests']}")
    print(f"failed_tests={result['failed_tests']}")
    print(f"skipped_tests={result['skipped_tests']}")
    print(f"assertion_failure_indicators={len(result['assertion_evidence'])}")
    print(f"worker_kill_indicators={len(result['worker_kill_evidence'])}")
    if summary_error:
        print(f"xcresult_summary_error={summary_error}")
    for index, detail in enumerate(result["assertion_evidence"], start=1):
        print(f"assertion_evidence[{index}]={detail}")
    for index, detail in enumerate(result["worker_kill_evidence"], start=1):
        print(f"worker_kill_evidence[{index}]={detail}")
    for index, detail in enumerate(result["failure_texts"], start=1):
        print(f"test_failure[{index}]={detail}")

    return 0 if result["classification"] in {"PASS", "PASS_WITH_WORKER_KILL"} else 1


if __name__ == "__main__":
    sys.exit(main())
