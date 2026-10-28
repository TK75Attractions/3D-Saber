#!/usr/bin/env python3
"""Tests for the auto clip summary helper and the receiver's auto clip routing."""

from __future__ import annotations

import io
import json
import struct
import sys
import tempfile
import unittest
import zlib
from http.client import HTTPConnection
from pathlib import Path
from threading import Thread
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import phone_saber_clip_report as report  # noqa: E402
import phone_saber_triage_protocol as protocol  # noqa: E402
from phone_saber_triage_receiver import TriageHTTPServer, TriageRequestHandler  # noqa: E402
from phone_saber_test_isolation import isolate_codex_logs as setUpModule  # noqa: F401,E402
from phone_saber_test_isolation import restore_codex_logs as tearDownModule  # noqa: F401,E402

COLUMNS = ["rank", "x1", "y1", "x2", "y2", "minX", "minY", "maxX", "maxY", "score", "flags", "source"]


def filtered_png(width: int, height: int, rgba: bytes, filters: list[int]) -> bytes:
    """PNG writer using the given filter per row (to test the decoder)."""
    channels, stride = 4, width * 4
    out, previous = bytearray(), bytearray(stride)
    for row in range(height):
        line = rgba[row * stride:(row + 1) * stride]
        kind = filters[row % len(filters)]
        encoded = bytearray(stride)
        for i in range(stride):
            a = line[i - channels] if i >= channels else 0
            b = previous[i]
            c = previous[i - channels] if i >= channels else 0
            if kind == 0:
                predictor = 0
            elif kind == 1:
                predictor = a
            elif kind == 2:
                predictor = b
            elif kind == 3:
                predictor = (a + b) >> 1
            else:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                predictor = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
            encoded[i] = (line[i] - predictor) & 255
        out += bytes((kind,)) + encoded
        previous = bytearray(line)

    def chunk(kind: bytes, body: bytes) -> bytes:
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(bytes(out))) + chunk(b"IEND", b""))


def solid_png(path: Path, width: int, height: int, rgb: tuple[int, int, int]) -> None:
    report.write_png(path, width, height, bytes(rgb) * (width * height))


def make_clip(root: Path, clip_id: str = "autoclip_20261019_120000_000_confusion_red", *,
              kind: str = "confusion") -> Path:
    """A small bundle in the iPhone auto clip format (80×120 frames)."""
    bundle = root / f"phone_saber_triage_{clip_id}"
    (bundle / "images").mkdir(parents=True)
    (bundle / "frames").mkdir()
    saber = [0, 10, 20, 10, 60, 7, 17, 13, 63, 120.5, 1 | 2 | 4, 0]
    cone = [3, 60, 100, 70, 110, 57, 97, 73, 113, 90.0, 1 | 2 | 8, 1]
    dim = [1, 40, 20, 44, 50, 37, 17, 47, 53, 70.0, 0, 0]
    frames = []
    for index in range(30):
        frame_id = 100 + index
        competitor = index >= 10
        frames.append({"f": frame_id, "t": 10 + index / 60, "ms": 9.5,
                       "red": {"n": 5, "e": 2 if competitor else 1, "k": 1 if competitor else 0,
                               "sel": [10, 20, 10, 60], "c": [saber, dim] + ([cone] if competitor else [])},
                       "blue": {"n": 0, "e": 0, "k": 0, "sel": None, "c": []}})
    images = []
    plan = [("context", 105, 0.5), ("event", 112, 1.0), ("event", 113, 1.0), ("context", 120, 0.5)]
    for number, (role, frame_id, scale) in enumerate(plan, 1):
        path = f"images/image_{number:02d}_{role}_{frame_id}.png"
        size = (80, 120) if scale == 1.0 else (40, 60)
        solid_png(bundle / path, *size, (30, 30, 30))
        context = f"frames/frame_{frame_id}_{number}.json"
        (bundle / context).write_text(json.dumps({
            "sessionID": clip_id, "selectedFrameID": frame_id, "frames": [
                {"frameID": frame_id, "timestamp": 10 + (frame_id - 100) / 60,
                 "red": {"detected": True}, "blue": {"detected": False}}]}))
        images.append({"path": path, "frameContextPath": context, "frameID": frame_id, "color": "red",
                       "failureType": f"autoclip_{kind}", "reason": "test", "role": role, "scale": scale,
                       "offsetSeconds": round((frame_id - 112) / 60, 3), "timestamp": 10 + (frame_id - 100) / 60})
    trigger = {"kind": kind, "color": "red", "frameID": 112, "timestamp": 10.2, "reason": "test trigger",
               "competitorCount": 1, "winner": [10, 20, 10, 60], "competitor": [60, 100, 70, 110],
               "previousWinner": [10, 20, 10, 60] if kind == "jump" else None,
               "jumpDistancePx": 150.0 if kind == "jump" else None, "jumpThresholdPx": 120.0 if kind == "jump" else None,
               "typicalLengthPx": 40.0, "note": None}
    summary = {
        "formatVersion": 1, "sessionID": clip_id, "summaryScope": "autoClip", "activeColors": ["red", "blue"],
        "selectedImageCount": len(images), "images": images, "incidentCount": 1, "recordedFrameCount": len(frames),
        "redBlueDetectionSummary": {"red": {"frames": 30}, "blue": {"frames": 30}},
        "dropoutSummary": {"red": {"dropoutStarts": 0}, "blue": {"dropoutStarts": 0}},
        "autoClip": {"formatVersion": 1, "trigger": trigger, "window": {"preSeconds": 3, "postSeconds": 2},
                     "frameSize": {"width": 80, "height": 120}, "camera": {"captureFPS": 60, "measuredFPS": 59.9,
                                                                         "thermalAtTrigger": "nominal",
                                                                         "thermalAtSave": "fair", "exposureBias": 0},
                     "ignoreZones": {"red": [], "blue": []}, "playArea": None,
                     "candidateColumns": COLUMNS, "sources": ["color-mask", "core-halo"], "frames": frames,
                     "thinning": 0},
    }
    (bundle / "summary.json").write_text(json.dumps(summary))
    (bundle / "prompt.md").write_text("auto clip")
    return bundle


class PNGTests(unittest.TestCase):
    def test_decoder_handles_every_filter_and_alpha(self) -> None:
        width, height = 7, 10
        rgba = bytes((x * 37 + y * 11 + c * 5) & 255 for y in range(height) for x in range(width) for c in range(4))
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "f.png"
            path.write_bytes(filtered_png(width, height, rgba, [0, 1, 2, 3, 4]))
            w, h, rgb = report.read_png(path)
            self.assertEqual((w, h), (width, height))
            expected = bytes(v for i, v in enumerate(rgba) if i % 4 != 3)
            self.assertEqual(bytes(rgb), expected)
            report.write_png(path, w, h, rgb)
            self.assertEqual(report.read_png(path)[2], rgb)


class ClipReportTests(unittest.TestCase):
    def test_build_station_warnings_and_ignore_frame_counts(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle = make_clip(Path(directory))
            path = bundle / "summary.json"
            summary = json.loads(path.read_text())
            clip = summary["autoClip"]
            clip["phone"] = {"gitCommit": "abcdef123456", "gitDirty": "true",
                             "buildTime": "2026-10-28T01:02:03Z", "station": "none"}
            clip["candidateColumns"].append("exclusionReason")
            for frame in clip["frames"]:
                for row in frame["red"]["c"]:
                    row.append("none" if row[10] & report.FLAG_SELECTABLE else "ignore:0,1")
                # 同じzoneの複数候補も1フレームに数える。
                frame["red"]["c"].append(list(frame["red"]["c"][1]))
            clip["frames"][0]["blue"]["c"] = [list(clip["frames"][0]["red"]["c"][1])]
            path.write_text(json.dumps(summary))
            info = report.describe(bundle)
            self.assertEqual(info["ignoreExcludedFrames"], {"red": {0: 30, 1: 30}, "blue": {0: 1, 1: 1}})
            self.assertEqual(info["exclusionUnknownFrames"], {"red": 0, "blue": 0})
            text = report.render_text(info)
            self.assertIn("build: abcdef123456+dirty 2026-10-28T01:02:03Z ⚠", text)
            self.assertIn("station: none ⚠", text)
            self.assertIn("red: zone 0=30 frames, zone 1=30 frames", text)
            self.assertIn("not selectable: ignore:0,1", text)
            for station in ("A", "B"):
                clip["phone"].update(gitDirty="false", station=station)
                path.write_text(json.dumps(summary))
                text = report.render_text(report.describe(bundle))
                self.assertNotIn("⚠", text)
                self.assertIn("station: " + station, text)
            clip["phone"]["gitCommit"] = "unknown"
            path.write_text(json.dumps(summary))
            self.assertIn("build: unknown 2026-10-28T01:02:03Z ⚠", report.render_text(report.describe(bundle)))

    def test_context_fallback_keeps_reason_and_legacy_is_unknown(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle = make_clip(Path(directory))
            path = bundle / "summary.json"
            summary = json.loads(path.read_text())
            summary["autoClip"]["frames"] = [f for f in summary["autoClip"]["frames"] if f["f"] != 105]
            image = summary["images"][0]
            context_path = bundle / image["frameContextPath"]
            context = json.loads(context_path.read_text())
            candidate = {"endpoints": [10, 20, 10, 60], "bbox": [7, 17, 13, 63],
                         "eligible": True, "selectable": False, "exclusionReason": "ignore:2"}
            context["frames"][0]["red"]["autoClipCandidates"] = [candidate]
            context_path.write_text(json.dumps(context))
            path.write_text(json.dumps(summary))
            self.assertEqual(report.describe(bundle)["ignoreExcludedFrames"]["red"], {2: 1})
            del candidate["exclusionReason"]
            context_path.write_text(json.dumps(context))
            model = report.load_clip(bundle)
            self.assertEqual(report.frame_candidates(model, 105, "red")[0]["exclusionReason"], "unknown")
            self.assertEqual(report.frame_candidates(model, 112, "red")[0]["exclusionReason"], "unknown")
            self.assertIn("build: unknown unknown ⚠", report.render_text(report.describe(bundle)))

    def test_real_1027_and_1028_legacy_clips_are_readable(self):
        names = ("phone_saber_triage_autoclip_20261028_170422_784_confusion_blue",
                 "phone_saber_triage_autoclip_20261027_185641_219_gap_red")
        for name in names:
            with self.subTest(clip=name):
                bundle = report.DEFAULT_INBOX / "autoclips" / name
                if not bundle.is_dir():
                    self.skipTest("private legacy clips unavailable")
                text = report.render_text(report.describe(bundle))
                self.assertIn("build: unknown unknown ⚠", text)
                self.assertIn("unknown reason frames=", text)

    def test_component_states_reasons_and_legacy_are_not_inferred(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle = make_clip(Path(directory))
            self.assertEqual(report.describe(bundle)["triggerFrameComponents"]["red"], {"state": "unknown"})
            summary = json.loads((bundle / "summary.json").read_text())
            image = next(i for i in summary["images"] if i["frameID"] == 112)
            path = bundle / image["frameContextPath"]
            context = json.loads(path.read_text())
            for state in ("notCollected", "maskEmpty", "collected"):
                value = {"state": state, "maskPixels": 0, "emitterMaskPixels": 12, "omitted": 9,
                         "columns": ["path", "stage", "reason"],
                         "rows": [["shutter-color-emitter", "emitter", "emitterAspect"]] if state == "collected" else []}
                context["frames"][0]["red"]["components"] = value
                path.write_text(json.dumps(context))
                info = report.describe(bundle)
                self.assertEqual(info["triggerFrameComponents"]["red"], value)
                text = report.render_text(info)
                self.assertIn(f"components: {state}", text)
                self.assertIn("omitted=9", text)
                if state == "collected":
                    self.assertIn("emitter/emitterAspect", text)
                self.assertEqual(report.frame_components(report.load_clip(bundle), 112, "blue"), {"state": "unknown"})

    def test_disabled_component_diagnostics_are_not_collected_at_clip_level(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle = make_clip(Path(directory))
            summary_path = bundle / "summary.json"
            summary = json.loads(summary_path.read_text())
            summary["autoClip"]["componentDiagnostics"] = {"enabled": False}
            summary_path.write_text(json.dumps(summary))
            for color in ("red", "blue"):
                self.assertEqual(report.frame_components(report.load_clip(bundle), 112, color),
                                 {"state": "notCollected"})
            self.assertIn("components: notCollected", report.render_text(report.describe(bundle)))

    def test_integrity_rejects_duplicate_ids_and_time_mismatches_before_overlay(self):
        for corruption in ('summary-duplicate', 'image-duplicate', 'context-duplicate',
                           'image-time', 'context-time', 'context-id', 'context-extra', 'invalid-time'):
            with self.subTest(corruption=corruption), tempfile.TemporaryDirectory() as directory:
                bundle = make_clip(Path(directory))
                path = bundle/'summary.json'
                summary = json.loads(path.read_text())
                image = summary['images'][1]
                context_path = bundle/image['frameContextPath']
                context = json.loads(context_path.read_text())
                if corruption == 'summary-duplicate':
                    summary['autoClip']['frames'].append(summary['autoClip']['frames'][0])
                elif corruption == 'image-duplicate':
                    summary['images'].append(image)
                elif corruption == 'context-duplicate':
                    context['frames'] *= 2
                elif corruption == 'image-time':
                    image['timestamp'] += 1.22
                elif corruption == 'context-time':
                    # summaryが間引かれていても、画像自身の時刻との比較で拒否する。
                    summary['autoClip']['frames'] = []
                    context['frames'][0]['timestamp'] += 1.22
                elif corruption == 'invalid-time':
                    image['timestamp'] = None
                elif corruption == 'context-extra':
                    context['frames'].insert(0, dict(frameID=999, timestamp=image['timestamp']))
                else:
                    context['frames'][0]['frameID'] = 999
                path.write_text(json.dumps(summary))
                context_path.write_text(json.dumps(context))
                with self.assertRaises(report.ClipIntegrityError):
                    report.write_clip_report(bundle)
                self.assertFalse(bundle.with_suffix('.contact.png').exists())
                self.assertFalse(bundle.with_suffix('.report.md').exists())
                with mock.patch('sys.stdout'), mock.patch('sys.stderr') as stderr:
                    self.assertEqual(report.main([str(bundle), '--write']), 2)
                self.assertIn('excluded clip', ''.join(c.args[0] for c in stderr.write.call_args_list))

    def test_integrity_accepts_thinned_summary_and_roundoff(self):
        with tempfile.TemporaryDirectory() as directory:
            bundle = make_clip(Path(directory))
            path = bundle/'summary.json'
            summary = json.loads(path.read_text())
            summary['autoClip']['frames'] = []
            summary['images'][0]['timestamp'] += 1e-9
            path.write_text(json.dumps(summary))
            self.assertTrue(report.load_clip(bundle)['summary']['images'])

    def test_confusion_summary_lists_competing_candidates(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = make_clip(Path(directory))
            info = report.describe(bundle)
            self.assertEqual(info["trigger"]["kind"], "confusion")
            candidates = info["triggerFrameCandidates"]["red"]
            winner = next(c for c in candidates if c["selected"])
            competitor = next(c for c in candidates if c["competitor"])
            self.assertEqual(winner["mid"], [10.0, 40.0])
            self.assertEqual(competitor["source"], "core-halo")
            self.assertEqual(competitor["mid"], [65.0, 105.0])
            self.assertEqual(competitor["boxSize"], [16, 16])
            self.assertEqual(competitor["score"], 90.0)
            self.assertEqual(info["competition"]["framesWithCompetitor"], 20)
            self.assertEqual(info["competition"]["winnerNearTriggerWinner"], 30)
            text = report.render_text(info)
            self.assertIn("COMPETITOR", text)
            self.assertIn("WINNER", text)
            self.assertIn("core-halo score 90.0", text)

    def test_v2_real_winner_suppression_and_emitter_columns(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = make_clip(Path(directory))
            path = bundle / "summary.json"
            summary = json.loads(path.read_text())
            clip = summary["autoClip"]
            clip["formatVersion"] = 2
            clip["candidateColumns"] += list(report.EVIDENCE_COLUMNS)
            for frame in clip["frames"]:
                frame["red"]["sup"] = False
                frame["blue"]["sup"] = True
                for row in frame["red"]["c"]:
                    row += [255, 0.83, 0.52, 230.5, True, 0.72, 400, 8.25]
            path.write_text(json.dumps(summary))
            info = report.describe(bundle)
            protocol.pack_bundle(bundle, Path(directory) / "new-format.psbt")
            self.assertEqual(info["formatVersion"], 2)
            self.assertEqual(info["suppressedFrames"], {"red": 0, "blue": 30})
            self.assertEqual(info["triggerFrameOutput"]["red"],
                             {"selected": [10, 20, 10, 60], "trackLockSuppressed": False})
            candidates = info["triggerFrameCandidates"]["red"]
            for c in candidates:
                self.assertEqual([c[k] for k in report.EVIDENCE_COLUMNS],
                                 [255, 0.83, 0.52, 230.5, True, 0.72, 400, 8.25])
            text = report.render_text(info)
            self.assertIn("WINNER", text)
            self.assertIn("COMPETITOR", text)
            self.assertIn("emitterScore=0.83", text)
            self.assertIn("trackLockSuppressed=False", text)
            self.assertNotIn("format v1", text)
            # 抑制フレームには実 winner を表示しない。
            frame = next(f for f in clip["frames"] if f["f"] == 112)
            frame["red"]["sel"] = None
            frame["red"]["sup"] = True
            frame["red"]["c"][0][10] = report.FLAG_ELIGIBLE | report.FLAG_SELECTABLE
            path.write_text(json.dumps(summary))
            info = report.describe(bundle)
            self.assertFalse(any(c["selected"] for c in info["triggerFrameCandidates"]["red"]))
            self.assertIn("trackLockSuppressed=True", report.render_text(info))

    def test_v1_uses_actual_sel_and_keeps_missing_evidence_unknown(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = make_clip(Path(directory))
            path = bundle / "summary.json"
            summary = json.loads(path.read_text())
            frame = next(f for f in summary["autoClip"]["frames"] if f["f"] == 112)
            frame["red"]["sel"] = [60, 100, 70, 110]  # 旧selectedフラグは先頭のまま
            path.write_text(json.dumps(summary))
            info = report.describe(bundle)
            winners = [c for c in info["triggerFrameCandidates"]["red"] if c["selected"]]
            self.assertEqual([c["rank"] for c in winners], [3])
            self.assertEqual(info["suppressedFrames"], {"red": None, "blue": None})
            self.assertIn("suppression and emitter evidence unknown", report.render_text(info))
            self.assertIn("emitterScore=unknown", report.render_text(info))

    def test_174032_old_flags_are_reconciled_for_2235_2247_2248(self) -> None:
        # 旧クリップで誤記されたrank→実出力rank。画像・私的bundleはテストに取り込まない。
        cases = [(2235, 4, 6, [276, 464, 300, 398]),
                 (2247, 3, 5, [252, 484, 272, 404]),
                 (2248, 2, 4, [244, 524, 260, 400])]
        for frame_id, old_rank, actual_rank, output in cases:
            with self.subTest(frame=frame_id):
                stale = [old_rank, 100, 100, 100, 200, 97, 97, 103, 203, 110, 7, 0]
                actual = [actual_rank, *output, 240, 398, 302, 526, 100, 11, 1]
                model = {"frames": {frame_id: {"blue": {"sel": output, "c": [stale, actual]}}},
                         "columns": COLUMNS, "sources": ["color-mask", "core-halo"]}
                winners = [c for c in report.frame_candidates(model, frame_id, "blue") if c["selected"]]
                self.assertEqual([c["rank"] for c in winners], [actual_rank])
                self.assertIsNone(report.frame_output(model, frame_id, "blue")["trackLockSuppressed"])

    def test_jump_summary(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            bundle = make_clip(Path(directory), "autoclip_x_jump_red", kind="jump")
            text = report.render_text(report.describe(bundle))
            self.assertIn("jump: 150.0 px (threshold 120.0 px", text)

    def test_contact_sheet_draws_boxes_and_report_stays_outside_the_bundle(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = make_clip(root)
            before = sorted(p.relative_to(bundle) for p in bundle.rglob("*"))
            path = report.write_clip_report(bundle)
            self.assertEqual(path, root / f"{bundle.name}.report.md")
            self.assertEqual(sorted(p.relative_to(bundle) for p in bundle.rglob("*")), before)
            sheet = root / f"{bundle.name}.contact.png"
            width, height, rgb = report.read_png(sheet)
            # 4 tiles of 40×60 in 3 columns → 2 rows, with 4 px gaps and 6 px bars.
            self.assertEqual((width, height), (3 * 40 + 4 * 4, 2 * (60 + 6) + 3 * 4))
            colors = {bytes(rgb[i:i + 3]) for i in range(0, len(rgb), 3)}
            self.assertIn(bytes(report.COMPETITOR), colors)
            self.assertIn(bytes(report.SELECTED), colors)
            self.assertIn(bytes(report.TRIGGER_BAR), colors)
            # The competitor box (bbox 57..73 × 97..113 → half 28..36 × 48..56) on the
            # first event tile (second tile, column 1): its top-left corner is magenta.
            left, top = 4 + 1 * (40 + 4), 4 + 6
            corner = ((top + 48) * width + left + 28) * 3
            self.assertEqual(bytes(rgb[corner:corner + 3]), bytes(report.COMPETITOR))
            self.assertIn("Contact sheet", path.read_text())

    def test_find_clips_and_cli(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            make_clip(root, "autoclip_a_confusion_red")
            make_clip(root, "autoclip_b_jump_red", kind="jump")
            other = root / "phone_saber_triage_phonesaber_x"
            other.mkdir()
            (other / "summary.json").write_text(json.dumps({"sessionID": "phonesaber_x"}))
            self.assertEqual([p.name for p in report.find_clips([root])],
                             ["phone_saber_triage_autoclip_a_confusion_red", "phone_saber_triage_autoclip_b_jump_red"])
            with mock.patch("sys.stdout") as stdout:
                self.assertEqual(report.main([str(root), "--json"]), 0)
            printed = "".join(call.args[0] for call in stdout.write.call_args_list)
            self.assertEqual(len(json.loads(printed)), 2)
            self.assertEqual(report.main([str(other)]), 1)


class ReceiverRoutingTests(unittest.TestCase):
    def test_manual_clip_receipt_precedes_report_and_duplicate_is_acknowledged(self) -> None:
        # socketを使わず実受信処理を検証。レポートや解析待ちをiPhoneへ伝播させない。
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = make_clip(root / "phone", "autoclip_manual", kind="manual")
            package = root / "manual.psbt"
            protocol.pack_bundle(bundle, package)
            events = []
            server = mock.Mock(inbox=root / "inbox")
            server.write_clip_report.side_effect = lambda path: events.append("report")
            handler = object.__new__(TriageRequestHandler)
            handler.server = server
            handler.client_address = ("127.0.0.1", 12345)
            handler._reply = lambda status, payload: events.append((status, payload))
            for _ in range(2):
                handler.rfile = io.BytesIO(package.read_bytes())
                handler._receive(package.stat().st_size)
            self.assertEqual(events[0][0], 201)
            self.assertEqual(events[1], "report")
            self.assertEqual(events[2], (200, {"status": "duplicate", "sessionID": "autoclip_manual"}))
            self.assertTrue((root / "inbox/autoclips" / bundle.name / "summary.json").is_file())
            server.enqueue_analysis.assert_not_called()
            server.write_clip_report.assert_called_once()

    def test_auto_clip_is_stored_in_autoclips_reported_and_not_analysed(self) -> None:
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            bundle = make_clip(root / "phone")
            package = root / "clip.psbt"
            protocol.pack_bundle(bundle, package)
            server = TriageHTTPServer(("127.0.0.1", 0), root / "inbox", analysis_mode="disabled")
            thread = Thread(target=server.serve_forever, daemon=True)
            thread.start()
            try:
                with mock.patch.object(server, "enqueue_analysis") as enqueue, \
                        mock.patch.object(server, "request_overview") as overview:
                    connection = HTTPConnection("127.0.0.1", server.server_port, timeout=10)
                    connection.request("POST", "/v1/bundle", body=package.read_bytes(),
                                       headers={"Content-Type": protocol.CONTENT_TYPE})
                    response = connection.getresponse()
                    self.assertEqual(response.status, 201)
                    response.read()
                    connection.close()
                    stored = root / "inbox/autoclips" / bundle.name
                    for _ in range(100):   # the report is written after the reply
                        if (root / "inbox/autoclips" / f"{bundle.name}.report.md").exists():
                            break
                        thread.join(0.05)
                    self.assertTrue((stored / "summary.json").is_file())
                    self.assertTrue((root / "inbox/autoclips" / f"{bundle.name}.report.md").is_file())
                    self.assertTrue((root / "inbox/autoclips" / f"{bundle.name}.contact.png").is_file())
                    self.assertFalse((root / "inbox" / bundle.name).exists())
                    enqueue.assert_not_called()
                    overview.assert_not_called()
                    # A retry of the same clip is acknowledged as a duplicate.
                    connection = HTTPConnection("127.0.0.1", server.server_port, timeout=10)
                    connection.request("POST", "/v1/bundle", body=package.read_bytes(),
                                       headers={"Content-Type": protocol.CONTENT_TYPE})
                    self.assertEqual(connection.getresponse().status, 200)
                    connection.close()
            finally:
                server.shutdown()
                server.server_close()
                thread.join(timeout=2)

    def test_destination_only_changes_for_auto_clips(self) -> None:
        inbox = Path("/tmp/inbox")
        route = protocol.auto_clip_destination(inbox)
        self.assertEqual(route({"summaryScope": "autoClip"}), inbox / "autoclips")
        self.assertEqual(route({"summaryScope": "1 retained incident"}), inbox)
        self.assertEqual(route({}), inbox)


if __name__ == "__main__":
    unittest.main()
