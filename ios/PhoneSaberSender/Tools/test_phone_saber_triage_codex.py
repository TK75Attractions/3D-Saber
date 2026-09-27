#!/usr/bin/env python3
"""Tests for limited Codex CLI input and failure isolation."""

from __future__ import annotations

import contextlib
import copy
import io
import json
import os
import sys
import tempfile
import unittest
from http.client import HTTPConnection
from pathlib import Path
from threading import Thread

sys.path.insert(0, str(Path(__file__).resolve().parent))

from phone_saber_triage_codex import (
    CodexFailed,
    CodexUnavailable,
    analyze_bundle,
    dry_run_text,
    input_plan,
    _output_schema,
    _codex_prompt,
)
from phone_saber_triage_protocol import BundleError
from phone_saber_triage_protocol import CONTENT_TYPE, pack_bundle
from phone_saber_triage_receiver import TriageHTTPServer
from test_phone_saber_triage_protocol import write_bundle


EMPTY_ANALYSIS = {
    "session_summary": "One selected image reviewed.",
    "false_negatives": [],
    "wrong_candidate_and_endpoint_errors": [],
    "false_positive_suspects": [],
    "other_findings": [],
    "limitations": [],
}
FOUR_IMAGE_IDS = ["image_001", "image_002", "image_003", "image_004"]
EXPECTED_SUMMARY_SCOPE = "retained incident candidates and nearby context"


class CodexTriageTests(unittest.TestCase):
    def test_dry_run_lists_only_selected_png_and_compact_context(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle)
            codex_spy = Path(directory) / "codex-spy.json"
            codex = fake_codex(Path(directory), codex_spy, analysis=EMPTY_ANALYSIS)
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                result = analyze_bundle(bundle, dry_run=True, codex_path=str(codex))
            report = output.getvalue()
            self.assertEqual(result["status"], "dry_run")
            self.assertFalse(codex_spy.exists())
            self.assertFalse((bundle / "analysis_report.json").exists())
            self.assertIn("image_001 -> image_01.png", report)
            self.assertIn("images/image_01.png", report)
            self.assertIn("frames/frame_100_1.json", report)
            self.assertIn("summary.json", report)
            self.assertIn("bundle prompt.md is not attached", report)
            self.assertIn("Excluded: video, full metadata.json, unselected PNGs", report)
            self.assertNotIn("image_02.png", report)

    def test_codex_is_given_only_selected_images_and_compact_metadata(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_codex_bundle(
                bundle, image_count=4,
                failure_types=("dropout", "endpoint_jump", "manual_capture", "candidate_zero"),
            )
            plan = input_plan(bundle)
            spy = root / "codex-spy.json"
            codex = fake_codex(root, spy, analysis=analysis_with_image_ids(FOUR_IMAGE_IDS))

            result = analyze_bundle(bundle, codex_path=str(codex))

            self.assertEqual(result["status"], "completed")
            invoked = json.loads(spy.read_text(encoding="utf-8"))
            self.assertEqual(len(invoked["images"]), 4)
            self.assertEqual(invoked["image_flag_count"], 4)
            self.assertTrue(all(path.endswith(f"/images/image_{index:02d}.png")
                                for index, path in enumerate(invoked["images"], start=1)))
            self.assertEqual(invoked["prompt"], _codex_prompt("sample_session", plan.images, plan.root))
            self.assertIsNone(invoked["positional_prompt"])
            self.assertTrue(invoked["stdin_sentinel"])
            self.assertEqual(set(invoked["files"]), {
                "summary.json",
                "images/image_01.png", "images/image_02.png", "images/image_03.png", "images/image_04.png",
                "frames/frame_100_1.json", "frames/frame_101_2.json",
                "frames/frame_102_3.json", "frames/frame_103_4.json",
            })
            self.assertEqual(invoked["sandbox"], "read-only")
            self.assertTrue(invoked["ephemeral"])
            self.assertTrue(invoked["skip_git_repo_check"])
            self.assertIn("A = capture/data artifact", invoked["prompt"])
            self.assertIn("Do not edit, create, or propose applying production code", invoked["prompt"])
            self.assertIn("image_ids may contain only the exact", invoked["prompt"])
            self.assertIn("detected=false", invoked["prompt"])
            self.assertIn("Context filename: frames/frame_100_1.json", invoked["prompt"])
            for section in ("false_negatives", "wrong_candidate_and_endpoint_errors",
                            "false_positive_suspects", "other_findings"):
                self.assertEqual(
                    invoked["output_schema"]["properties"][section]["items"]
                    ["properties"]["image_ids"]["items"]["enum"],
                    FOUR_IMAGE_IDS,
                )
            report = json.loads((bundle / "analysis_report.json").read_text(encoding="utf-8"))
            self.assertEqual(report["formatVersion"], 2)
            self.assertEqual(report["input"]["imageCount"], 4)
            self.assertEqual([image["id"] for image in report["input"]["imageReferences"]],
                             FOUR_IMAGE_IDS)
            self.assertEqual(report["analysis"]["other_findings"][0]["image_ids"], FOUR_IMAGE_IDS)
            self.assertFalse(report["input"]["videoIncluded"])
            self.assertFalse(report["input"]["fullMetadataIncluded"])
            markdown = (bundle / "analysis_report.md").read_text(encoding="utf-8")
            self.assertIn("image_001 — `images/image_01.png`", markdown)
            self.assertIn("images: image_001, image_002, image_003, image_004", markdown)

    def test_unknown_or_path_like_image_references_are_rejected(self) -> None:
        for name, image_ids in (
            ("unknown ID", ["image_999"]),
            ("absolute path", ["/tmp/input/images/image_01.png"]),
            ("traversal path", ["../images/image_01.png"]),
        ):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                bundle = root / "bundle"
                write_codex_bundle(bundle)
                codex = fake_codex(root, root / "spy.json",
                                   analysis=analysis_with_image_ids(image_ids))
                with self.assertRaisesRegex(CodexFailed, "image ID that was not sent"):
                    analyze_bundle(bundle, codex_path=str(codex))
                self.assertTrue((bundle / "summary.json").is_file())
                self.assertFalse((bundle / "analysis_report.json").exists())
                self.assertFalse((bundle / "analysis_report.md").exists())

    def test_duplicate_image_reference_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_codex_bundle(bundle)
            codex = fake_codex(root, root / "spy.json",
                               analysis=analysis_with_image_ids(["image_001", "image_001"]))
            with self.assertRaisesRegex(CodexFailed, "duplicate image IDs"):
                analyze_bundle(bundle, codex_path=str(codex))
            self.assertTrue((bundle / "summary.json").is_file())
            self.assertFalse((bundle / "analysis_report.json").exists())

    def test_case_insensitive_basename_collision_is_rejected(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle, image_count=2)
            summary_path = bundle / "summary.json"
            summary = json.loads(summary_path.read_text(encoding="utf-8"))
            collision_path = bundle / "images/IMAGE_01.PNG"
            (bundle / summary["images"][1]["path"]).rename(collision_path)
            summary["images"][1]["path"] = "images/IMAGE_01.PNG"
            summary_path.write_text(json.dumps(summary), encoding="utf-8")
            with self.assertRaisesRegex(BundleError, "ambiguous selected image basenames"):
                input_plan(bundle)

    def test_output_schema_is_limited_to_selected_image_ids(self) -> None:
        schema = _output_schema(("image_001", "image_002"))
        for section in ("false_negatives", "wrong_candidate_and_endpoint_errors",
                        "false_positive_suspects", "other_findings"):
            item_schema = schema["properties"][section]["items"]
            self.assertNotIn("image_paths", item_schema["properties"])
            self.assertEqual(item_schema["properties"]["image_ids"]["items"], {
                "type": "string", "enum": ["image_001", "image_002"],
            })

    def test_codex_unavailable_preserves_received_bundle(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle)
            with self.assertRaises(CodexUnavailable):
                analyze_bundle(bundle, codex_path="/missing/codex")
            self.assertTrue((bundle / "summary.json").is_file())
            self.assertTrue((bundle / "images/image_01.png").is_file())
            self.assertFalse((bundle / "analysis_report.json").exists())

    def test_codex_failure_preserves_bundle_and_writes_no_partial_report(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = root / "bundle"
            write_codex_bundle(bundle)
            codex = fake_codex(root, root / "unused.json", fail=True)
            with self.assertRaises(CodexFailed):
                analyze_bundle(bundle, codex_path=str(codex))
            self.assertTrue((bundle / "summary.json").is_file())
            self.assertFalse((bundle / "analysis_report.json").exists())
            self.assertFalse((bundle / "analysis_report.md").exists())

    def test_zero_image_session_writes_local_report_without_codex(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle, image_count=0)
            result = analyze_bundle(bundle, codex_path="/missing/codex")
            self.assertEqual(result["status"], "no_images")
            report = json.loads((bundle / "analysis_report.json").read_text(encoding="utf-8"))
            self.assertEqual(report["input"]["imageCount"], 0)
            self.assertIn("No Codex request was made", report["analysis"]["session_summary"])

    def test_default_image_limit_and_failure_type_dedup_are_enforced(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle, image_count=3)
            with self.assertRaisesRegex(BundleError, "per-failure-type"):
                input_plan(bundle)
            with self.assertRaises(ValueError):
                input_plan(bundle, max_images=21)

    def test_malformed_summary_is_rejected_before_codex(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = Path(directory) / "bundle"
            write_codex_bundle(bundle)
            (bundle / "summary.json").write_text("{bad", encoding="utf-8")
            with self.assertRaisesRegex(BundleError, "summary.json is malformed"):
                input_plan(bundle)

    def test_full_session_metadata_cannot_be_attached_through_summary(self) -> None:
        for key, value in (
            ("frames", [{"frameID": index} for index in range(100)]),
            ("cameraSamples", [{"timestamp": index} for index in range(100)]),
            ("candidateDiagnostics", {"red": {"raw": "full session"}}),
        ):
            with self.subTest(key=key), tempfile.TemporaryDirectory() as directory:
                bundle = Path(directory) / "bundle"
                write_codex_bundle(bundle)
                summary_path = bundle / "summary.json"
                summary = json.loads(summary_path.read_text(encoding="utf-8"))
                summary[key] = value
                summary_path.write_text(json.dumps(summary), encoding="utf-8")
                with self.assertRaisesRegex(BundleError, "full-session metadata"):
                    input_plan(bundle)

    def test_retained_incident_context_frames_must_be_a_nonnegative_integer(self) -> None:
        for value in (True, -1, 1.5, "138"):
            with self.subTest(value=value), tempfile.TemporaryDirectory() as directory:
                bundle = Path(directory) / "bundle"
                write_codex_bundle(bundle)
                summary_path = bundle / "summary.json"
                summary = json.loads(summary_path.read_text(encoding="utf-8"))
                summary["retainedIncidentContextFrames"] = value
                summary_path.write_text(json.dumps(summary), encoding="utf-8")
                with self.assertRaisesRegex(BundleError, "retainedIncidentContextFrames"):
                    input_plan(bundle)

    def test_summary_scope_must_match_the_compact_triage_scope(self) -> None:
        for value in (True, 138, "", "full session metadata"):
            with self.subTest(value=value), tempfile.TemporaryDirectory() as directory:
                bundle = Path(directory) / "bundle"
                write_codex_bundle(bundle)
                summary_path = bundle / "summary.json"
                summary = json.loads(summary_path.read_text(encoding="utf-8"))
                summary["summaryScope"] = value
                summary_path.write_text(json.dumps(summary), encoding="utf-8")
                with self.assertRaisesRegex(BundleError, "summaryScope"):
                    input_plan(bundle)

    def test_receiver_dry_run_accepts_bundle_without_calling_codex(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inbox = root / "inbox"
            server = TriageHTTPServer(("127.0.0.1", 0), inbox,
                                      analysis_mode="dry-run", codex_path="/missing/codex")
            thread = Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                body = write_codex_bundle(root / "bundle")
                connection = HTTPConnection("127.0.0.1", server.server_port, timeout=3)
                connection.request("POST", "/v1/bundle", body=body, headers={
                    "Content-Type": CONTENT_TYPE,
                    "Content-Length": str(len(body)),
                    "Connection": "close",
                })
                response = connection.getresponse()
                self.assertEqual(response.status, 201)
                response.read()
                connection.close()
                server.analysis_queue.join()
                received = inbox / "phone_saber_triage_sample_session"
                self.assertTrue((received / "summary.json").is_file())
                self.assertFalse((received / "analysis_report.json").exists())
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)

    def test_receiver_remains_available_after_codex_failure(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            inbox = root / "inbox"
            codex = fake_codex(root, root / "unused.json", fail=True)
            server = TriageHTTPServer(("127.0.0.1", 0), inbox,
                                      analysis_mode="automatic", codex_path=str(codex))
            thread = Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                body = write_codex_bundle(root / "bundle")
                connection = HTTPConnection("127.0.0.1", server.server_port, timeout=3)
                connection.request("POST", "/v1/bundle", body=body, headers={
                    "Content-Type": CONTENT_TYPE,
                    "Content-Length": str(len(body)),
                    "Connection": "close",
                })
                response = connection.getresponse()
                self.assertEqual(response.status, 201)
                response.read()
                connection.close()
                server.analysis_queue.join()

                health = HTTPConnection("127.0.0.1", server.server_port, timeout=3)
                health.request("GET", "/health")
                health_response = health.getresponse()
                self.assertEqual(health_response.status, 200)
                health_response.read()
                health.close()
                received = inbox / "phone_saber_triage_sample_session"
                self.assertTrue((received / "summary.json").is_file())
                self.assertFalse((received / "analysis_report.json").exists())
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)


def fake_codex(root: Path, spy_path: Path, *, analysis: dict | None = None,
               fail: bool = False) -> Path:
    executable = root / "fake-codex"
    response_json = json.dumps(analysis or EMPTY_ANALYSIS, ensure_ascii=False)
    spy_literal = repr(str(spy_path))
    script = f"""#!{sys.executable}
import json, pathlib, sys
args = sys.argv[1:]
prompt = sys.stdin.read()
if {fail!r}:
    print('simulated Codex failure', file=sys.stderr)
    raise SystemExit(9)
image_paths = [args[i + 1] for i, value in enumerate(args[:-1]) if value == '--image']
input_root = pathlib.Path(args[args.index('--cd') + 1])
response_path = pathlib.Path(args[args.index('--output-last-message') + 1])
schema_path = pathlib.Path(args[args.index('--output-schema') + 1])
spy = {{
    'images': image_paths,
    'image_flag_count': args.count('--image'),
    'files': sorted(path.relative_to(input_root).as_posix() for path in input_root.rglob('*') if path.is_file()),
    'sandbox': args[args.index('--sandbox') + 1],
    'ephemeral': '--ephemeral' in args,
    'skip_git_repo_check': '--skip-git-repo-check' in args,
    'prompt': prompt,
    'positional_prompt': args[-1] if args and args[-1] != '-' else None,
    'stdin_sentinel': bool(args and args[-1] == '-'),
    'output_schema': json.loads(schema_path.read_text(encoding='utf-8')),
}}
pathlib.Path({spy_literal}).write_text(json.dumps(spy), encoding='utf-8')
response_path.write_text({response_json!r}, encoding='utf-8')
"""
    executable.write_text(script, encoding="utf-8")
    executable.chmod(0o755)
    return executable


def analysis_with_image_ids(image_ids: list[str]) -> dict:
    analysis = copy.deepcopy(EMPTY_ANALYSIS)
    analysis["other_findings"] = [{
        "classification": ["G"],
        "color": "UNKNOWN",
        "frame_ids": [100],
        "image_ids": image_ids,
        "issue_type": "Image reference contract test",
        "observation": "A selected image was supplied.",
        "interpretation": "Only supplied image IDs are referenced.",
        "confidence": "low",
    }]
    return analysis


def write_codex_bundle(bundle: Path, *, image_count: int = 1,
                       failure_types: tuple[str, ...] | None = None) -> bytes:
    write_bundle(bundle, image_count=image_count)
    summary_path = bundle / "summary.json"
    summary = json.loads(summary_path.read_text(encoding="utf-8"))
    summary["summaryScope"] = EXPECTED_SUMMARY_SCOPE
    summary["retainedIncidentContextFrames"] = 138
    if failure_types is not None:
        if len(failure_types) != image_count:
            raise ValueError("failure_types must match image_count")
        for image, failure_type in zip(summary["images"], failure_types):
            image["failureType"] = failure_type
    summary_path.write_text(json.dumps(summary), encoding="utf-8")
    envelope = bundle.with_suffix(".psbt")
    pack_bundle(bundle, envelope)
    return envelope.read_bytes()


if __name__ == "__main__":
    unittest.main()
