#!/usr/bin/env python3
"""Free local one-page session report: read-only, tolerant of old bundles, receiver hook."""
from __future__ import annotations

import contextlib
import hashlib
import io
import json
import sys
import tempfile
import unittest
from http.client import HTTPConnection
from pathlib import Path
from threading import Thread
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import phone_saber_session_report as report_tool
from phone_saber_session_report import (
    build_report, memory_section, render_markdown, report_path_for, write_session_report)
from phone_saber_triage_codex import input_plan
from phone_saber_triage_protocol import CONTENT_TYPE
from phone_saber_triage_receiver import TriageHTTPServer
from test_phone_saber_triage_codex import write_codex_bundle
from test_phone_saber_tracking_diagnostics import write_tracking_bundle

MIB = 1024 * 1024


def snapshot(bundle: Path) -> dict[str, str]:
    return {p.relative_to(bundle).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(bundle.rglob("*")) if p.is_file()}


def set_runtime(bundle: Path, runtime: dict, **extra) -> None:
    path = bundle / "summary.json"
    summary = json.loads(path.read_text())
    motion = summary.setdefault("motionEventSummary", {"events": []})
    motion["runtime"] = runtime
    motion.update(extra)
    path.write_text(json.dumps(summary))


class SessionReportTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def test_minimal_old_bundle_reports_na_and_never_writes_into_the_bundle(self):
        bundle = self.root / "phone_saber_triage_sample_session"
        write_codex_bundle(bundle)
        before = snapshot(bundle)
        report = build_report(bundle)
        text = render_markdown(report)
        self.assertEqual(snapshot(bundle), before, "the report is read-only on the bundle")
        self.assertEqual(report["inputContract"], "PASS")
        self.assertEqual(report["sessionID"], "sample_session")
        self.assertIn("activeColors: n/a", text)
        self.assertIn("verdict: n/a", text)
        self.assertIn("trackingCapture: n/a", text)
        self.assertIn("predates candidate geometry", text)
        self.assertIn("status: **PASS**", text)
        self.assertIn("ORIGINAL PNG", text)
        self.assertIn("annotated は ground truth ではない", text)
        self.assertEqual(report["errors"], [])

    def test_tracking_event_frames_and_images_to_open_first(self):
        bundle = self.root / "phone_saber_triage_sample_session"
        write_tracking_bundle(bundle)
        summary_path = bundle / "summary.json"
        summary = json.loads(summary_path.read_text())
        summary["motionEventSummary"]["events"].append([7, "blue", 0.5, "bridge_priority"])
        summary["motionEventSummary"]["trackingCapture"] = {
            "recordingMaxFrameID": 105, "recordingMaxScore": 4.0, "recordingMaxColor": "red",
            "retainedPeakFrameID": 105, "retainedPeakScore": 4.0, "retainedPeakColor": "red",
            "temporalFramesRetained": 11, "contextIncomplete": False, "highestRankedFrameMissing": False}
        summary_path.write_text(json.dumps(summary))
        report = build_report(bundle)
        text = render_markdown(report)
        self.assertEqual(report["trackingPreflight"]["status"], "PASS")
        self.assertIn("recording max: frame 105 red score 4", text)
        self.assertIn("104(onset), 105(peak)", text)
        self.assertIn("bridge_priority: event 7 blue", text)
        self.assertEqual(report["tracking"]["ledgerCodes"], {"selected": 1, "bridge_priority": 1})
        first = report["imagesToOpen"]["trackingPeakOnset"]
        self.assertEqual(first, ["images/image_05.png", "images/image_06.png"])

    def test_memory_verdicts(self):
        unavailable = memory_section({"motionEventSummary": {"runtime": {
            "peakRetainedBGRABytes": 10 * MIB, "memoryHeadroom": {"available": False, "samples": 0}}}})
        self.assertTrue(unavailable["verdict"].startswith("unavailable"))
        ok = memory_section({"motionEventSummary": {"runtime": {"memoryHeadroom": {
            "available": True, "minimumAvailableBytes": 900 * MIB, "minimumFrameID": 42,
            "retainedBGRABytesAtMinimum": 30 * MIB}}}})
        self.assertTrue(ok["verdict"].startswith("OK — minimum available 900.0 MiB >= whole budget 256.0 MiB"))
        self.assertIn("frame 42", ok["verdict"])
        tight = memory_section({"motionEventSummary": {"runtime": {"memoryHeadroom": {
            "available": True, "minimumAvailableBytes": 200 * MIB, "retainedBGRABytesAtMinimum": 100 * MIB}}}})
        self.assertTrue(tight["verdict"].startswith("OK (tight)"))
        warning = memory_section({"motionEventSummary": {"runtime": {"memoryHeadroom": {
            "available": True, "minimumAvailableBytes": 100 * MIB, "retainedBGRABytesAtMinimum": 10 * MIB}}}})
        self.assertTrue(warning["verdict"].startswith("WARNING"))
        recorded = memory_section({"motionEventSummary": {"runtime": {},
                                                          "thresholds": {"maximumRetainedBGRABytes": 192 * MIB}}})
        self.assertEqual(recorded["budgetBytes"], 192 * MIB)
        self.assertIn("predates memoryHeadroom", recorded["verdict"])
        self.assertIn("not in this bundle", memory_section({})["verdict"])

    def test_memory_headroom_reaches_the_markdown(self):
        bundle = self.root / "phone_saber_triage_sample_session"
        write_codex_bundle(bundle)
        set_runtime(bundle, {"peakRetainedBGRABytes": 31948800, "memoryHeadroom": {
            "source": "os_proc_available_memory", "samples": 2000, "available": True,
            "minimumAvailableBytes": 1200 * MIB, "minimumFrameID": 900,
            "retainedBGRABytesAtMinimum": 30 * MIB}})
        text = render_markdown(build_report(bundle))
        self.assertIn("peakRetainedBGRABytes: 30.5 MiB", text)
        self.assertIn("minimumAvailableBytes: 1200.0 MiB", text)
        self.assertIn("**verdict: OK", text)

    def test_contract_failure_and_damaged_fields_still_render(self):
        bundle = self.root / "phone_saber_triage_sample_session"
        write_codex_bundle(bundle)
        summary_path = bundle / "summary.json"
        summary = json.loads(summary_path.read_text())
        summary["redBlueDetectionSummary"] = "garbage"
        summary["motionEventSummary"] = {"events": "garbage", "runtime": "garbage"}
        summary["bridgeDropoutSummary"] = []
        summary_path.write_text(json.dumps(summary))
        report = build_report(bundle)
        text = render_markdown(report)
        self.assertTrue(report["inputContract"].startswith("FAIL"))
        self.assertIsNone(report["trackingPreflight"])
        self.assertIn("## tracking_preflight\n\n- n/a", text)
        (bundle / "summary.json").write_text("{broken")
        text = render_markdown(build_report(bundle))
        self.assertIn("summary.json: JSONDecodeError", text)
        self.assertIn("sessionID: n/a", text)

    def test_replay_sweep_and_case_hints_are_labelled_as_evidence(self):
        from test_phone_saber_selection_replay import SWITCH, write_bundle as write_geometry_bundle
        bundle = write_geometry_bundle(self.root, SWITCH)
        report = build_report(bundle)
        self.assertTrue(report["inputContract"].startswith("FAIL"), "geometry-only fixture lacks summary")
        replay = report["selectionReplay"]
        self.assertEqual(replay["sequences"], 1)
        self.assertEqual(len(replay["sweep"]), 8)
        changed = {(r["margin"], r["hold"]): r["changedFrames"] for r in replay["sweep"]}
        self.assertEqual(changed[(0.5, 1)], [11])
        self.assertEqual(changed[(5.0, 3)], [11])
        text = render_markdown(report)
        self.assertIn("evidence only — NOT production values", text)
        self.assertIn("| 0.5 | 1 |", text)

    def test_cli_output_json_and_refuses_to_write_into_the_bundle(self):
        bundle = self.root / "phone_saber_triage_sample_session"
        write_codex_bundle(bundle)
        with contextlib.redirect_stderr(io.StringIO()):
            self.assertEqual(report_tool.main([str(bundle), "--output", str(bundle / "r.md")]), 2)
        self.assertFalse((bundle / "r.md").exists())
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(report_tool.main([str(bundle), "--json"]), 0)
        self.assertEqual(json.loads(out.getvalue())["sessionID"], "sample_session")
        target = self.root / "out" / "report.md"
        with contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(report_tool.main([str(bundle), "--output", str(target)]), 0)
        self.assertIn("# PhoneSaber session report — sample_session", target.read_text())

    def test_written_report_is_a_sibling_and_keeps_the_input_contract(self):
        bundle = self.root / "phone_saber_triage_sample_session"
        write_codex_bundle(bundle)
        path = write_session_report(bundle)
        self.assertEqual(path, self.root / "phone_saber_triage_sample_session.report.md")
        self.assertEqual(path, report_path_for(bundle))
        self.assertTrue(path.is_file())
        input_plan(bundle)  # strict contract (no allow_reports) still passes
        with self.assertRaises(ValueError):
            write_session_report(bundle, bundle / "inside.md")


class ReceiverReportHookTests(unittest.TestCase):
    def post(self, root: Path, *, patch_report=None) -> tuple[int, str, Path, mock.Mock]:
        inbox = root / "inbox"
        output = io.StringIO()
        analyze = mock.Mock(return_value={"status": "dry_run"})
        patches = [mock.patch("phone_saber_triage_receiver.analyze_bundle", analyze)]
        if patch_report is not None:
            patches.append(mock.patch("phone_saber_triage_receiver.write_session_report", patch_report))
        with contextlib.ExitStack() as stack:
            for item in patches:
                stack.enter_context(item)
            stack.enter_context(contextlib.redirect_stdout(output))
            server = TriageHTTPServer(("127.0.0.1", 0), inbox, analysis_mode="dry-run")
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
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)
        return status, output.getvalue(), inbox, analyze

    def test_report_is_written_beside_the_bundle_and_logged(self):
        with tempfile.TemporaryDirectory() as tmp:
            status, log, inbox, analyze = self.post(Path(tmp))
            self.assertEqual(status, 201)
            bundle = inbox / "phone_saber_triage_sample_session"
            report = inbox / "phone_saber_triage_sample_session.report.md"
            self.assertTrue(report.is_file())
            self.assertIn("# PhoneSaber session report — sample_session", report.read_text())
            self.assertIn(f"[PHONE_SABER][REPORT] sessionID=sample_session source=new_upload path={report}", log)
            analyze.assert_called_once()
            self.assertEqual(analyze.call_args.args[0], bundle)
            input_plan(bundle)  # nothing was added inside the received bundle

    def test_report_failure_never_breaks_receiving_or_analysis(self):
        with tempfile.TemporaryDirectory() as tmp:
            failing = mock.Mock(side_effect=RuntimeError("disk full"))
            status, log, inbox, analyze = self.post(Path(tmp), patch_report=failing)
            self.assertEqual(status, 201)
            failing.assert_called_once()
            self.assertTrue((inbox / "phone_saber_triage_sample_session" / "summary.json").is_file())
            self.assertIn("[PHONE_SABER][REPORT] sessionID=sample_session source=new_upload result=FAIL "
                          "RuntimeError: disk full", log)
            analyze.assert_called_once()


if __name__ == "__main__":
    unittest.main()
