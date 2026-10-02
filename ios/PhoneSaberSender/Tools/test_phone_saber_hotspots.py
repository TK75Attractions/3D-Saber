#!/usr/bin/env python3
"""Static hotspot map (background false-positive hint) on synthetic triage contexts."""
from __future__ import annotations

import contextlib
import hashlib
import io
import json
import math
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import phone_saber_hotspots as hotspot_tool
from phone_saber_hotspots import HotspotParams, static_hotspots
from phone_saber_session_report import build_report, render_markdown
from test_phone_saber_triage_codex import write_codex_bundle

SESSION = "phonesaber_hotspot_test"
LABEL_BOX = [60, 243, 100, 247]


def geo_cand(index, centroid, bbox, score, *, rank=None, source_type="color-close"):
    entry = {"listIndex": index, "eligible": rank is not None, "finalScore": score, "centroid": centroid,
             "bbox": bbox, "sourceType": source_type}
    if rank is not None:
        entry["eligibleRank"] = rank
    return entry


def geometry(candidates):
    eligible = [c for c in candidates if c["eligible"]]
    return {"totalCandidateCount": len(candidates), "eligibleCandidateCount": len(eligible),
            "eligibleOmittedCount": 0, "candidates": candidates}


def write_contexts(root: Path, frames: list[dict], *, images: dict[int, str] | None = None,
                   chunk: int = 5) -> Path:
    """Overlapping-free contexts of up to `chunk` frames plus a summary listing images."""
    bundle = root / f"phone_saber_triage_{SESSION}"
    (bundle / "frames").mkdir(parents=True)
    for start in range(0, len(frames), chunk):
        part = frames[start:start + chunk]
        context = {"sessionID": SESSION, "selectedFrameID": part[0]["frameID"], "frames": part}
        (bundle / "frames" / f"frame_{part[0]['frameID']}_{start // chunk + 1}.json").write_text(json.dumps(context))
    summary = {"sessionID": SESSION, "images": [
        {"frameID": fid, "path": path} for fid, path in sorted((images or {}).items())]}
    (bundle / "summary.json").write_text(json.dumps(summary))
    return bundle


def label_and_moving_saber(count=12) -> list[dict]:
    frames = []
    for i in range(count):
        x = 150 + 25 * i
        saber = geo_cand(0, [x, 400 + 10 * math.sin(i)], [x - 50, 380, x + 50, 420], 90 + i, rank=1)
        label = geo_cand(1, [80.0 + (i % 2) * 0.5, 245.0], LABEL_BOX, 55 + (i % 3), rank=2)
        dim = geo_cand(2, [300, 100], [290, 95, 310, 105], 20)  # never eligible, also static
        frames.append({"frameID": 100 + i, "timestamp": i / 30,
                       "red": {"detected": True, "candidateGeometry": geometry([saber, label, dim])},
                       "blue": {"detected": False}})
    return frames


def snapshot(bundle: Path) -> dict[str, str]:
    return {p.relative_to(bundle).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest()
            for p in sorted(bundle.rglob("*")) if p.is_file()}


class StaticHotspotTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def test_fixed_label_is_flagged_and_moving_saber_is_not(self):
        bundle = write_contexts(self.root, label_and_moving_saber(), images={103: "images/image_01.png"})
        before = snapshot(bundle)
        result = static_hotspots(bundle)
        self.assertEqual(snapshot(bundle), before, "read-only on the bundle")
        self.assertFalse(result["winnersOnly"])
        red = result["clusters"]["red"]
        flagged = [c for c in red if c["likelyBackground"]]
        self.assertEqual(len(flagged), 1)
        label = flagged[0]
        self.assertEqual(label["clusterID"], "red-1")
        self.assertEqual(label["framesPresent"], 12)
        self.assertAlmostEqual(label["centroid"][0], 80, delta=1)
        self.assertEqual(label["bbox"], [float(v) for v in LABEL_BOX])
        self.assertEqual(label["eligibleFraction"], 1.0)
        self.assertEqual(label["winningFraction"], 0.0)
        self.assertEqual(label["maxFinalScore"], 57)
        self.assertEqual(label["sourceTypes"], {"color-close": 12})
        self.assertEqual(label["winnerElsewhereFrames"], list(range(100, 112)))
        self.assertEqual(label["selectedImages"], ["images/image_01.png"])
        # The saber is never one static cluster; the ineligible static blob is not flagged.
        saber_like = [c for c in red if c["centroid"][1] > 350]
        self.assertTrue(saber_like)
        self.assertFalse(any(c["likelyBackground"] for c in saber_like))
        dim = [c for c in red if c["centroid"] == [300.0, 100.0]]
        self.assertEqual(dim[0]["framesPresent"], 12)
        self.assertFalse(dim[0]["likelyBackground"])
        self.assertFalse(dim[0]["checks"]["eligibleOrWinning"])
        self.assertEqual(result["clusters"]["blue"], [])

    def test_jittery_moving_object_is_not_flagged(self):
        frames = []
        for i in range(15):
            # A hand-held saber drifting and wobbling ±20 px around a slow path.
            x, y = 240 + 4 * i + (20 if i % 2 else -20), 320 + (15 if i % 3 == 0 else -10)
            frames.append({"frameID": 10 + i, "timestamp": i / 30, "blue": {"candidateGeometry": geometry(
                [geo_cand(0, [x, y], [x - 30, y - 30, x + 30, y + 30], 80, rank=1)])}})
        result = static_hotspots(write_contexts(self.root, frames))
        self.assertTrue(result["clusters"]["blue"])
        self.assertEqual(result["likelyBackgroundCount"], 0)
        for cluster in result["clusters"]["blue"]:
            self.assertFalse(cluster["likelyBackground"], cluster)

    def test_short_static_presence_is_not_flagged(self):
        frames = label_and_moving_saber(count=3)
        result = static_hotspots(write_contexts(self.root, frames))
        label = [c for c in result["clusters"]["red"] if abs(c["centroid"][0] - 80) < 2]
        self.assertFalse(label[0]["likelyBackground"])
        self.assertFalse(label[0]["checks"]["frames"])
        relaxed = static_hotspots(self.root / f"phone_saber_triage_{SESSION}",
                                  HotspotParams(min_frames=3, min_span_seconds=0.05))
        self.assertEqual(relaxed["likelyBackgroundCount"], 1)

    def test_old_bundle_falls_back_to_selected_candidate_and_endpoint_winners(self):
        frames = []
        for i in range(10):
            red: dict = {"detected": True, "eligibleCandidateCount": 2, "score": 60 + i,
                         "selectedCandidateType": "color-close", "endpoint": [104, 250, 66, 250],
                         "candidateDecisionTrace": [{"index": 0, "eligible": True, "finalScore": 60 + i}]}
            if i == 4:  # the carabiner wins one frame
                red["endpoint"] = [436, 478, 436, 490]
                red["selectedCandidateType"] = "color-sparse-raw"
            if i == 2:  # selected frame: winner bbox/centroid recorded
                red["selectedCandidate"] = {"bbox": [66, 248, 106, 252], "centroid": [83.9, 250.5],
                                            "finalScore": 60.85, "sourceType": "color-close"}
            if i == 7:  # predicted output is not a detection position
                red["predictionUsed"] = True
                red["endpoint"] = [300, 300, 340, 300]
            frames.append({"frameID": 2531 + i, "timestamp": 76 + i / 30, "red": red,
                           "blue": {"detected": False, "eligibleCandidateCount": 0}})
        # An overlapping context repeats frame 2533 without selectedCandidate; the richer one wins.
        bundle = write_contexts(self.root, frames, images={2533: "images/image_01_before_2533.png",
                                                           2535: "images/image_05_onset_2535.png"})
        overlap = {"sessionID": SESSION, "frames": [dict(frames[2], red={**frames[2]["red"]})]}
        overlap["frames"][0]["red"].pop("selectedCandidate")
        (bundle / "frames" / "frame_2533_9.json").write_text(json.dumps(overlap))
        result = static_hotspots(bundle)
        self.assertTrue(result["winnersOnly"])
        self.assertEqual(result["frameSources"], {"endpoint": 8, "selectedCandidate": 1})
        red = result["clusters"]["red"]
        label = red[0]
        self.assertTrue(label["likelyBackground"])
        self.assertEqual(label["framesPresent"], 8)
        self.assertEqual(label["winningFraction"], 1.0)
        self.assertEqual(label["dataSources"], {"endpoint": 7, "selectedCandidate": 1})
        self.assertEqual(label["winnerElsewhereFrames"], [2535])
        self.assertEqual(label["selectedImages"], ["images/image_01_before_2533.png"])
        carabiner = [c for c in red if c["centroid"] == [436.0, 484.0]]
        self.assertEqual(carabiner[0]["framesPresent"], 1)
        self.assertFalse(carabiner[0]["likelyBackground"])
        self.assertFalse([c for c in red if c["centroid"] == [320.0, 300.0]], "predicted output excluded")

    def test_bundle_without_positions_and_malformed_contexts_are_na(self):
        bundle = write_contexts(self.root, [{"frameID": 1, "red": {"detected": False}, "blue": {}}])
        (bundle / "frames" / "frame_2_2.json").write_text("{bad")
        result = static_hotspots(bundle)
        self.assertEqual(result["framesWithPositions"], 0)
        self.assertEqual(result["clusters"], {"red": [], "blue": []})
        self.assertIn("n/a", hotspot_tool.render_text(result))

    def test_plan_like_input_and_png_image_size(self):
        bundle = write_contexts(self.root, label_and_moving_saber())
        (bundle / "images").mkdir()
        (bundle / "images" / "a.png").write_bytes(
            b"\x89PNG\r\n\x1a\n\x00\x00\x00\x0dIHDR" + (720).to_bytes(4, "big") + (1280).to_bytes(4, "big"))
        result = static_hotspots(mock.Mock(root=bundle))
        self.assertEqual(result["imageSize"], [720, 1280])
        self.assertEqual(result["likelyBackgroundCount"], 1)

    def test_cli_text_and_json(self):
        bundle = write_contexts(self.root, label_and_moving_saber())
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(hotspot_tool.main([str(bundle), "--json"]), 0)
        self.assertEqual(json.loads(out.getvalue())["likelyBackgroundCount"], 1)
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(hotspot_tool.main([str(bundle), str(bundle)]), 0)
        self.assertEqual(out.getvalue().count("[LIKELY BACKGROUND]"), 2)
        err = io.StringIO()
        with contextlib.redirect_stderr(err):
            self.assertEqual(hotspot_tool.main([str(self.root / "missing")]), 2)


class HotspotClusteringCostTests(unittest.TestCase):
    def test_many_static_observations_cluster_quickly_and_correctly(self):
        import time
        from phone_saber_hotspots import Observation, _cluster
        observations = [Observation(frame_id=i, timestamp=i / 30, color="red",
                                    centroid=(80.0 + (i % 3) * 0.5, 245.0), bbox=list(LABEL_BOX),
                                    eligible=True, winning=False, final_score=55.0,
                                    source_type="color-close", source="candidateGeometry")
                        for i in range(5000)]
        observations += [Observation(frame_id=i, timestamp=i / 30, color="red",
                                     centroid=(150.0 + 7 * (i % 50), 400.0), bbox=None,
                                     eligible=True, winning=True, final_score=90.0,
                                     source_type="core-line", source="candidateGeometry")
                         for i in range(500)]
        started = time.monotonic()
        clusters = _cluster(observations, radius_px=24.0, min_iou=0.5)
        elapsed = time.monotonic() - started
        label = max(clusters, key=lambda c: len(c.members))
        self.assertEqual(len(label.members), 5000)
        self.assertLess(abs(label.representative()["centroid"][0] - 80.5), 1.0)
        # ~2-3 s before the representative was cached; generous bound for loaded hosts.
        self.assertLess(elapsed, 2.0)


class SessionReportHotspotSectionTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)

    def test_section_lists_flagged_clusters_and_their_selected_images(self):
        bundle = write_contexts(self.root, label_and_moving_saber(), images={104: "images/image_02.png"})
        text = render_markdown(build_report(bundle))
        section = text.split("## 静的ホットスポット")[1].split("\n## ")[0]
        self.assertIn("red-1 **LIKELY BACKGROUND**", section)
        self.assertIn("`images/image_02.png`", section)
        self.assertIn("ORIGINAL PNG", section)

    def test_old_bundle_shows_na(self):
        bundle = self.root / "phone_saber_triage_sample_session"
        write_codex_bundle(bundle)
        report = build_report(bundle)
        text = render_markdown(report)
        self.assertIn("静的ホットスポット", text)
        self.assertIn("no candidate geometry or winner positions", text)
        self.assertEqual(report["errors"], [])

    def test_hotspot_failure_never_breaks_the_report(self):
        bundle = self.root / "phone_saber_triage_sample_session"
        write_codex_bundle(bundle)
        with mock.patch("phone_saber_session_report.static_hotspots", side_effect=RuntimeError("boom")):
            report = build_report(bundle)
        text = render_markdown(report)
        self.assertIsNone(report["staticHotspots"])
        self.assertIn("static_hotspots: RuntimeError: boom", report["errors"])
        self.assertIn("静的ホットスポット", text)
        # Malformed section data is rendered as n/a, not raised.
        report["staticHotspots"] = {"framesWithPositions": 3, "clusters": {"red": [None]}}
        self.assertIn("- n/a (", render_markdown(report))


if __name__ == "__main__":
    unittest.main()
