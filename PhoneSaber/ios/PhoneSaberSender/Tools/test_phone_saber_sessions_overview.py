#!/usr/bin/env python3
"""Cross-session overview: synthetic old/new bundles, receiver logs, receiver hook."""
from __future__ import annotations

import contextlib
import hashlib
import io
import json
import os
import re
import subprocess
import sys
import tempfile
import time
import unittest
from http.client import HTTPConnection
from pathlib import Path
from threading import Thread
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

from phone_saber_test_isolation import isolate_codex_logs as setUpModule  # noqa: F401,E402
from phone_saber_test_isolation import restore_codex_logs as tearDownModule  # noqa: F401,E402
import phone_saber_sessions_overview as overview_tool
from phone_saber_sessions_overview import (
    OVERVIEW_BASENAME, build_overview, codex_status, read_receiver_logs, render_html, render_markdown,
    route_label, write_overview)
from phone_saber_triage_protocol import CONTENT_TYPE
from phone_saber_triage_receiver import TriageHTTPServer
from test_phone_saber_triage_codex import write_codex_bundle

TOOLS = Path(__file__).resolve().parent
OLD_ID = "phonesaber_20260929_140729_518"
NEW_ID = "phonesaber_20261003_155919_297"


def snapshot(root: Path) -> dict[str, str]:
    return {p.relative_to(root).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(root.rglob("*")) if p.is_file()}


def old_bundle(inbox: Path, session_id: str = OLD_ID) -> Path:
    """A bundle from before motionEventSummary / tracking / camera / exposure experiment."""
    bundle = inbox / f"phone_saber_triage_{session_id}"
    write_codex_bundle(bundle)
    return bundle


def new_bundle(inbox: Path, session_id: str = NEW_ID, *, experiment: bool = True) -> Path:
    """Two selected frames (red 100, blue 101) with every newer optional field."""
    bundle = inbox / f"phone_saber_triage_{session_id}"
    write_codex_bundle(bundle, image_count=2)
    summary_path = bundle / "summary.json"
    summary = json.loads(summary_path.read_text())
    summary["recordedFrameCount"] = 1800  # 60 s at 30 fps
    summary["images"][1]["frameID"] = 130  # 30 frames per second between the images
    summary["images"][1]["timestamp"] = 1.0
    summary["activeColors"] = ["red", "blue"]
    summary["motionEventSummary"] = {"events": [], "signalDistributions": {
        "endpoint_jump": {"count": 12, "maxScore": 4, "meanScore": 2},
        "candidate_switch": {"count": 5, "maxScore": 1, "meanScore": 1}}}
    if experiment:
        summary["cameraExposureExperiment"] = {
            "formatVersion": 1, "setting": "maxShutter1_240", "status": "applied", "capActive": True,
            "requestedMaxExposureSeconds": 1 / 240, "appliedMaxExposureSeconds": 1 / 240,
            "defaultMaxExposureSeconds": 1 / 30, "observedMaxExposureSeconds": 1 / 240}
    summary_path.write_text(json.dumps(summary))
    red_context = bundle / "frames" / "frame_100_1.json"
    context = json.loads(red_context.read_text())
    context["frames"][0]["camera"] = {"exposureDurationSeconds": 0.004, "iso": 400, "source": "exif"}
    context["frames"][0]["red"] = {
        "detected": True, "detectionSucceeded": True, "predictionUsed": False, "eligibleCandidateCount": 1,
        "selectedCandidateIndex": 0, "selectedCandidateType": "core-line", "score": 50.0,
        "selectedCandidate": {"index": 0, "meanColorPurity": 0.10, "clippedWhiteRatio": 0.0},
        "tracking": {"candidateSwitch": True, "midpointDisplacement": 150.0}}
    red_context.write_text(json.dumps(context))
    blue_context = bundle / "frames" / "frame_101_2.json"
    context = json.loads(blue_context.read_text())
    context["selectedFrameID"] = 130
    context["frames"][0]["frameID"] = 130
    context["frames"][0]["camera"] = {"exposureDurationSeconds": 0.004, "iso": 400, "source": "exif"}
    context["frames"][0]["blue"] = {
        "detected": True, "detectionSucceeded": True, "selectedCandidateIndex": 2, "eligibleCandidateCount": 3,
        "selectedCandidateType": "color-mask", "score": 40.0,
        "tracking": {"candidateSwitch": False, "midpointDisplacement": 120.0}}
    blue_context.write_text(json.dumps(context))
    return bundle


def write_log(logs: Path, name: str, lines: list[str]) -> None:
    logs.mkdir(parents=True, exist_ok=True)
    (logs / name).write_text("\n".join(lines) + "\n")


class OverviewRowTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.inbox = Path(self.tmp.name) / "inbox"
        self.inbox.mkdir()

    def rows(self, **kwargs) -> dict:
        return {r["sessionID"]: r for r in build_overview(self.inbox, **kwargs)["sessions"]}

    def test_old_bundle_without_new_fields_renders_na(self):
        old_bundle(self.inbox)
        row = self.rows()[OLD_ID]
        self.assertEqual(row["startedAt"], "2026-09-29T14:07:29")
        self.assertEqual(row["recordedFrameCount"], 300)
        self.assertIsNone(row["activeColors"])
        self.assertIsNone(row["exposure"]["medianExposureMs"])
        self.assertFalse(row["exposure"]["experiment"]["present"])
        self.assertFalse(row["selectedFrames"]["trackingRecorded"])
        self.assertIsNone(row["selectedFrames"]["jumpsAtLeast100px"])
        self.assertIsNone(row["wholeRecordingSignals"]["endpointJump"])
        self.assertEqual(row["inputContract"], "PASS")
        self.assertEqual(row["route"]["label"], "n/a")
        self.assertEqual(row["codex"]["status"], "none")
        self.assertFalse(row["sessionReport"]["exists"])
        self.assertEqual(row["errors"], [])

    def test_new_bundle_fields_are_tallied(self):
        new_bundle(self.inbox)
        row = self.rows()[NEW_ID]
        self.assertEqual(row["activeColors"], ["red", "blue"])
        self.assertTrue(row["fpsMeasured"])
        self.assertAlmostEqual(row["durationSeconds"], 60.0, places=1)
        self.assertEqual(row["exposure"]["medianExposureMs"], 4.0)
        self.assertEqual(row["exposure"]["medianISO"], 400)
        self.assertEqual(row["exposure"]["experiment"]["label"], "上限 1/240 s")
        selected = row["selectedFrames"]
        self.assertTrue(selected["trackingRecorded"])
        self.assertEqual(selected["jumpsAtLeast100px"], 2)  # red 150 px and blue 120 px
        self.assertEqual(selected["candidateSwitch"], 1)
        signals = row["wholeRecordingSignals"]
        self.assertEqual((signals["endpointJump"], signals["endpointJumpPerMinute"]), (12, 12.0))
        self.assertEqual(signals["candidateSwitchPerMinute"], 5.0)

    def test_case_hints_use_only_tally_rows(self):
        old_bundle(self.inbox)
        rows = [{"frameID": 100, "color": "red", "hint": "A", "countForTally": True},
                {"frameID": 100, "color": "red", "hint": "A", "countForTally": False},
                {"frameID": 130, "color": "blue", "hint": "B", "countForTally": True}]
        with mock.patch("phone_saber_session_report.candidate_selection_audit", return_value=rows):
            overview = build_overview(self.inbox)
        self.assertEqual(overview["sessions"][0]["caseHints"], {"A": 1, "B": 1})
        self.assertEqual(overview["totals"]["caseHints"], {"A": 1, "B": 1})

    def test_broken_bundle_becomes_a_row_and_does_not_hide_others(self):
        old_bundle(self.inbox)
        broken = self.inbox / "phone_saber_triage_phonesaber_20261001_000000_000"
        broken.mkdir()
        (broken / "summary.json").write_text("{not json")
        rows = self.rows()
        self.assertEqual(set(rows), {OLD_ID, "phonesaber_20261001_000000_000"})
        self.assertTrue(rows["phonesaber_20261001_000000_000"]["errors"])
        self.assertIn("読み取りの注意", render_html(build_overview(self.inbox)))

    def test_bundles_are_never_modified_and_files_beside_them_are_ignored(self):
        old_bundle(self.inbox)
        new_bundle(self.inbox)
        (self.inbox / "phone_saber_triage_stray.report.md").write_text("not a bundle")
        before = snapshot(self.inbox)
        html_path, md_path, overview = write_overview(self.inbox)
        after = snapshot(self.inbox)
        for name in (html_path.name, md_path.name):
            after.pop(name)
        self.assertEqual(after, before)
        self.assertEqual(overview["totals"]["sessions"], 2)
        self.assertEqual(html_path, self.inbox / f"{OVERVIEW_BASENAME}.html")


class OverviewStatusTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.inbox = self.root / "inbox"
        self.inbox.mkdir()
        self.logs = self.root / "logs"

    def test_route_labels(self):
        self.assertEqual(route_label("127.0.0.1")["label"], "P2P 中継")
        self.assertEqual(route_label("192.168.1.10")["label"], "LAN")
        self.assertEqual(route_label(None)["label"], "n/a")

    def test_receiver_logs_give_route_and_last_analysis_line(self):
        write_log(self.logs, "triage-20261001-000000.log", [
            f"[triage] received phone_saber_triage_{NEW_ID} from 192.168.1.10 → /x",
            f"[AUTO_REPAIR][ANALYSIS] sessionID={NEW_ID} source=new_upload result=starting"])
        write_log(self.logs, "triage-20261003-000000.log", [
            f"[triage] received phone_saber_triage_{NEW_ID} sessionID={NEW_ID} source=new_upload "
            "from 127.0.0.1 → /x",
            f"[AUTO_REPAIR][ANALYSIS] sessionID={NEW_ID} source=new_upload elapsed=600.5s "
            "subprocess=codex read-only result=FAIL CLI_TIMEOUT: Codex CLI exit_code=None"])
        index = read_receiver_logs(self.logs)[f"phone_saber_triage_{NEW_ID}"]
        self.assertEqual(index["from"], "127.0.0.1")
        bundle = new_bundle(self.inbox)
        self.assertEqual(codex_status(bundle, index["analysisLine"])["status"], "timeout")
        row = build_overview(self.inbox, logs_dir=self.logs)["sessions"][0]
        self.assertEqual(row["route"]["label"], "P2P 中継")
        self.assertEqual(row["codex"]["label"], "timeout")

    def test_codex_status_from_log_lines(self):
        bundle = old_bundle(self.inbox)
        cases = {"x result=starting": "running", "x result=FAIL boom": "failed",
                 "x result=FAIL PRECHECK_FAILED frameMappingMissing": "precheck",
                 "x subprocess=none result=precheck_failed": "precheck", None: "none"}
        for line, expected in cases.items():
            self.assertEqual(codex_status(bundle, line)["status"], expected, line)

    def test_analysis_report_wins_over_the_log(self):
        bundle = old_bundle(self.inbox)
        (bundle / "analysis_report.json").write_text(json.dumps({
            "formatVersion": 3, "analysisExecuted": True,
            "analysis": {"repair_assessment": {"decision": "needs_capture"}}}))
        (bundle / "analysis_report.md").write_text("# analysis")
        status = codex_status(bundle, "x result=FAIL CLI_TIMEOUT")
        self.assertEqual((status["status"], status["detail"]), ("ok", "needs_capture"))
        row = build_overview(self.inbox)["sessions"][0]
        self.assertEqual(row["codex"]["href"], f"phone_saber_triage_{OLD_ID}/analysis_report.md")
        (bundle / "analysis_report.json").write_text(json.dumps({
            "formatVersion": 3, "analysisExecuted": False, "precheck": {"reasonCodes": ["temporalEvidenceMissing"]}}))
        self.assertEqual(codex_status(bundle, None)["status"], "precheck")
        (bundle / "analysis_report.json").write_text(json.dumps({"formatVersion": 2, "analysis": {}}))
        self.assertEqual(codex_status(bundle, None)["status"], "ok")
        (bundle / "analysis_report.json").write_text("{broken")
        self.assertEqual(codex_status(bundle, None)["status"], "unreadable")


class OverviewOutputTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.inbox = self.root / "inbox"
        self.inbox.mkdir()
        old_bundle(self.inbox)
        new_bundle(self.inbox)

    def test_html_is_self_contained_and_themed(self):
        html_text = render_html(build_overview(self.inbox))
        self.assertNotRegex(html_text, r"(?:src|href)=\"(?:https?:)?//")
        self.assertNotIn("<script", html_text)
        self.assertIn('<meta name="viewport"', html_text)
        self.assertIn("prefers-color-scheme:dark", html_text)
        self.assertIn(':root[data-theme="dark"]', html_text)
        self.assertEqual(html_text.count("<svg"), 2)
        self.assertIn("ジャンプ / 分", html_text)
        self.assertIn("露出時間の中央値 (ms)", html_text)
        self.assertIn(NEW_ID, html_text)
        self.assertIn(OLD_ID, html_text)
        # newest first in the table
        self.assertLess(html_text.index(NEW_ID), html_text.index(OLD_ID))

    def test_markdown_summary_headline_and_rows(self):
        text = render_markdown(build_overview(self.inbox))
        self.assertIn("セッション 2 件", text)
        self.assertIn("≥100px ジャンプ 2、candidateSwitch 1", text)
        self.assertIn("上限 1/240 s", text)
        self.assertIn("4 ms", text)
        self.assertIn("12 (12/分)", text)
        self.assertEqual(len([line for line in text.splitlines() if "`phonesaber_" in line]), 2)

    def test_missing_reports_only_on_request_and_linked(self):
        write_overview(self.inbox)
        self.assertFalse(list(self.inbox.glob("*.report.md")))
        _, md_path, overview = write_overview(self.inbox, write_missing_reports=True)
        reports = sorted(p.name for p in self.inbox.glob("*.report.md"))
        self.assertEqual(reports, [f"phone_saber_triage_{OLD_ID}.report.md",
                                   f"phone_saber_triage_{NEW_ID}.report.md"])
        self.assertTrue(all(r["sessionReport"]["exists"] for r in overview["sessions"]))
        self.assertIn(f"[要約](phone_saber_triage_{NEW_ID}.report.md)", md_path.read_text())

    def test_cli_writes_to_output_dir_and_rejects_a_missing_inbox(self):
        out = self.root / "out"
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            self.assertEqual(overview_tool.main(["--inbox", str(self.inbox), "--output-dir", str(out),
                                                 "--logs-dir", str(self.root / "nologs")]), 0)
        self.assertTrue((out / f"{OVERVIEW_BASENAME}.html").is_file())
        self.assertTrue((out / f"{OVERVIEW_BASENAME}.md").is_file())
        self.assertIn("overview: 2 sessions", stdout.getvalue())
        self.assertFalse((self.inbox / f"{OVERVIEW_BASENAME}.html").exists())
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(overview_tool.main(["--inbox", str(self.root / "missing")]), 2)

    def test_launcher_runs_the_tool(self):
        out = self.root / "launcher_out"
        env = {**os.environ, "PHONESABER_DIAGNOSTICS_INBOX": str(self.inbox), "PHONESABER_OVERVIEW_NO_OPEN": "1"}
        result = subprocess.run(["bash", str(TOOLS / "PhoneSaber Overview.command"), "--output-dir", str(out)],
                                env=env, capture_output=True, text=True, timeout=120,
                                stdin=subprocess.DEVNULL)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertTrue((out / f"{OVERVIEW_BASENAME}.html").is_file())
        self.assertTrue((self.inbox / f"phone_saber_triage_{OLD_ID}.report.md").is_file())


class ReceiverOverviewHookTests(unittest.TestCase):
    def post(self, root: Path, *, overview: bool, patch_overview=None) -> tuple[int, str, Path, mock.Mock]:
        inbox = root / "inbox"
        output = io.StringIO()
        analyze = mock.Mock(return_value={"status": "dry_run"})
        with contextlib.ExitStack() as stack:
            stack.enter_context(mock.patch("phone_saber_triage_receiver.analyze_bundle", analyze))
            if patch_overview is not None:
                stack.enter_context(mock.patch("phone_saber_sessions_overview.write_overview", patch_overview))
            stack.enter_context(contextlib.redirect_stdout(output))
            server = TriageHTTPServer(("127.0.0.1", 0), inbox, analysis_mode="dry-run",
                                      overview=overview, logs_dir=root / "logs")
            thread = Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                body = write_codex_bundle(root / "bundle")
                connection = HTTPConnection("127.0.0.1", server.server_port, timeout=5)
                connection.request("POST", "/v1/bundle", body=body, headers={
                    "Content-Type": CONTENT_TYPE, "Content-Length": str(len(body)), "Connection": "close"})
                response = connection.getresponse()
                status = response.status
                response.read()
                connection.close()
                server.analysis_queue.join()
                deadline = time.monotonic() + 20
                while time.monotonic() < deadline:
                    with server._overview_lock:
                        if not server._overview_running and not server._overview_pending:
                            break
                    time.sleep(0.05)
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)
        return status, output.getvalue(), inbox, analyze

    def test_overview_is_regenerated_after_upload(self):
        with tempfile.TemporaryDirectory() as tmp:
            status, log, inbox, analyze = self.post(Path(tmp), overview=True)
            self.assertEqual(status, 201)
            analyze.assert_called_once()
            page = inbox / f"{OVERVIEW_BASENAME}.html"
            self.assertTrue(page.is_file())
            self.assertIn("sample_session", page.read_text())
            self.assertRegex(log, r"\[PHONE_SABER\]\[OVERVIEW\] reason=\w+ sessions=1 ")

    def test_overview_failure_never_breaks_receiving_or_analysis(self):
        with tempfile.TemporaryDirectory() as tmp:
            failing = mock.Mock(side_effect=RuntimeError("disk full"))
            status, log, inbox, analyze = self.post(Path(tmp), overview=True, patch_overview=failing)
            self.assertEqual(status, 201)
            self.assertTrue(failing.called)
            analyze.assert_called_once()
            self.assertTrue((inbox / "phone_saber_triage_sample_session" / "summary.json").is_file())
            self.assertIn("[PHONE_SABER][OVERVIEW] reason=", log)
            self.assertIn("result=FAIL RuntimeError: disk full; receiving and analysis continue", log)

    def test_overview_is_off_unless_enabled(self):
        with tempfile.TemporaryDirectory() as tmp:
            status, log, inbox, _ = self.post(Path(tmp), overview=False)
            self.assertEqual(status, 201)
            self.assertFalse((inbox / f"{OVERVIEW_BASENAME}.html").exists())
            self.assertNotIn("[PHONE_SABER][OVERVIEW]", log)


if __name__ == "__main__":
    unittest.main()
