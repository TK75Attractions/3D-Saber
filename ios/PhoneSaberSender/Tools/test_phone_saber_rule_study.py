#!/usr/bin/env python3
"""Tests for the offline eligibility-rule study (no Swift compile, no private images)."""
from __future__ import annotations

import csv
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import phone_saber_rule_study as study

REPO = Path(__file__).resolve().parents[3]
LABELS = REPO / "docs/claude/analysis/2026-10-03_background_fp_labels.csv"


def cand(x0, y0, x1, y1, *, eligible=True, ep=None):
    ep = ep or (x0, (y0 + y1) // 2, x1, (y0 + y1) // 2)
    return {"eligible": eligible, "bounding_box": {"min_x": x0, "min_y": y0, "max_x": x1, "max_y": y1},
            "endpoints": [{"x": ep[0], "y": ep[1]}, {"x": ep[2], "y": ep[3]}]}


def analysis(red, blue=()):
    return {"colors": {"red": {"candidates": list(red)}, "blue": {"candidates": list(blue)}}}


class PatchTests(unittest.TestCase):
    def test_patch_applies_once_to_the_current_production_source(self):
        source = (study.SOURCES / "DetectionCore.swift").read_text(encoding="utf-8")
        patched = study.patch_detection_core(source)
        self.assertEqual(patched.count("private let expRedGate = expGate(\"RED\")"), 1)
        self.assertEqual(patched.count("let g = evidence.color == .red ? expRedGate : expBlueGate"), 1)
        # The gate runs after the compact-red gate and before the light score.
        self.assertLess(patched.index("meanPurity >= 0.50\n        }\n        if isEmitterEligible {"),
                        patched.index("lightScore = emitterScore * 38.0"))
        # Nothing of the production source is removed.
        self.assertEqual(patched.replace(study._GATE_DECLARATIONS, "", 1).replace(study._GATE_BODY, "", 1), source)

    def test_patch_refuses_missing_anchor_or_double_patch(self):
        with self.assertRaises(ValueError):
            study.patch_detection_core("struct Nothing {}")
        source = (study.SOURCES / "DetectionCore.swift").read_text(encoding="utf-8")
        with self.assertRaises(ValueError):
            study.patch_detection_core(study.patch_detection_core(source))

    def test_production_sources_are_not_modified_by_the_tool(self):
        text = (study.SOURCES / "DetectionCore.swift").read_text(encoding="utf-8")
        self.assertNotIn("expRedGate", text)
        self.assertNotIn("PS_RED_R7E", text)

    def test_every_rule_only_uses_known_environment_keys(self):
        for env in study.RULES.values():
            for key in env:
                self.assertRegex(key, r"^PS_(RED|BLUE)_(R7E|PFLOOR|MEANMIN)$")
        self.assertEqual(study.RULES["base"], {})


class LabelTests(unittest.TestCase):
    def test_parse_truth(self):
        self.assertEqual(study.parse_truth("saber", "1 2 3 4"), [1, 2, 3, 4])
        self.assertIsNone(study.parse_truth("absent", ""))
        self.assertEqual(study.parse_truth("unknown", ""), "?")
        with self.assertRaises(ValueError):
            study.parse_truth("saber", "1 2 3")
        with self.assertRaises(ValueError):
            study.parse_truth("maybe", "")

    def test_committed_label_table_is_complete_and_parseable(self):
        labels = study.load_labels(LABELS)
        # The table grows as new captures are labelled (129 frames on 10-03, 151 on 10-04).
        self.assertGreaterEqual(len(labels), 151)
        kinds = {"saber": 0, "absent": 0, "unknown": 0}
        for value in labels.values():
            for truth in value.values():
                kinds["unknown" if truth == "?" else "absent" if truth is None else "saber"] += 1
        self.assertEqual(sum(kinds.values()), 2 * len(labels), "one row per frame and colour")
        self.assertGreater(kinds["saber"], 80)
        with LABELS.open(newline="", encoding="utf-8") as handle:
            header = next(csv.reader(handle))
        self.assertEqual(header[:6], ["session", "frame", "color", "truth", "saber_box", "note"])

    def test_load_labels_requires_both_colors(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "l.csv"
            path.write_text("session,frame,color,truth,saber_box,note\ns,1,red,absent,,\n", encoding="utf-8")
            with self.assertRaises(ValueError):
                study.load_labels(path)

    def test_resolve_image_skips_annotated_copies(self):
        with tempfile.TemporaryDirectory() as tmp:
            images = Path(tmp) / "phone_saber_triage_phonesaber_S" / "images"
            images.mkdir(parents=True)
            (images / "image_03_bridge_event_1_dropout_annotated_12.png").write_bytes(b"")
            (images / "image_02_bridge_event_1_dropout_12.png").write_bytes(b"")
            self.assertEqual(study.resolve_image(Path(tmp), "S", 12).name, "image_02_bridge_event_1_dropout_12.png")
            self.assertIsNone(study.resolve_image(Path(tmp), "S", 13))
            self.assertIsNone(study.resolve_image(Path(tmp), "missing", 12))


class EvaluationTests(unittest.TestCase):
    def test_label_candidate_by_overlap_or_midpoint(self):
        saber = [100, 100, 200, 140]
        self.assertEqual(study.label_candidate(cand(110, 105, 190, 135), saber), "real")
        self.assertEqual(study.label_candidate(cand(300, 300, 340, 320), saber), "bg")
        # Large bridge box with little overlap but its output midpoint on the saber.
        self.assertEqual(study.label_candidate(cand(0, 0, 400, 400, ep=(120, 120, 180, 120)), saber), "real")
        self.assertEqual(study.label_candidate(cand(0, 0, 10, 10), None), "bg")
        self.assertEqual(study.label_candidate(cand(0, 0, 10, 10), "?"), "unk")

    def test_winner_is_first_eligible(self):
        result = {"candidates": [cand(0, 0, 9, 9, eligible=False), cand(50, 50, 60, 60), cand(1, 1, 2, 2)]}
        self.assertEqual(study.winner(result)["bounding_box"]["min_x"], 50)
        self.assertIsNone(study.winner({"candidates": [cand(0, 0, 9, 9, eligible=False)]}))

    def test_jump_fates_and_summary(self):
        labels = {("S", f): {"red": None, "blue": None} for f in (1, 2, 3)}
        label_strip, carabiner = cand(60, 240, 100, 250), cand(430, 470, 440, 490)
        base = study.frame_outcomes({
            ("S", 1): analysis([label_strip]),
            ("S", 2): analysis([carabiner, label_strip]),  # ~420 px jump
            ("S", 3): analysis([label_strip]),             # jump back
        }, labels)
        self.assertEqual(study.jump_events(base, 100), {(("S", 2), "red"), (("S", 3), "red")})
        gated = study.frame_outcomes({
            ("S", 1): analysis([]),
            ("S", 2): analysis([dict(carabiner, eligible=False), dict(label_strip, eligible=False)]),
            ("S", 3): analysis([]),
        }, labels)
        summary = study.summarize_rule("gate", gated, base, labels, 100)
        self.assertEqual(summary["baseJumps"], 2)
        self.assertEqual(summary["jumpFates"], {"noDetection": 2})
        self.assertEqual(summary["counts"]["red:absent:none"], 3)
        self.assertEqual(summary["realLost"], [])
        self.assertEqual(summary["newJumps"], [])

    def test_real_loss_is_reported(self):
        labels = {("S", 1): {"red": [100, 100, 200, 140], "blue": None}}
        base = study.frame_outcomes({("S", 1): analysis([cand(110, 105, 190, 135)])}, labels)
        gated = study.frame_outcomes({("S", 1): analysis([cand(110, 105, 190, 135, eligible=False)])}, labels)
        summary = study.summarize_rule("gate", gated, base, labels, 100)
        self.assertEqual(summary["realLost"], ["S/1/red"])
        self.assertIn("| gate |", study.render([summary]))

    def test_apply_gain_keeps_alpha_and_clamps(self):
        raw = bytes([100, 200, 250, 255, 10, 20, 30, 128])
        self.assertEqual(study.apply_gain(raw, 1.0), raw)
        self.assertEqual(study.apply_gain(raw, 1.1), bytes([110, 220, 255, 255, 11, 22, 33, 128]))
        self.assertEqual(study.apply_gain(raw, 0.5), bytes([50, 100, 125, 255, 5, 10, 15, 128]))

    def test_run_study_uses_injected_analyzer_and_base_first(self):
        labels = {("S", 1): {"red": None, "blue": None}}
        calls = []

        def fake(binary, path, env, gain):
            calls.append(dict(env))
            return analysis([] if env else [cand(0, 0, 10, 10)])

        with tempfile.TemporaryDirectory() as tmp:
            images = Path(tmp) / "phone_saber_triage_phonesaber_S" / "images"
            images.mkdir(parents=True)
            (images / "image_01_frame_1.png").write_bytes(b"")
            summaries = study.run_study(labels, Path(tmp), ["pf22"], Path("unused"), gain=1.0, threshold=100,
                                        formal=False, analyze=fake)
        self.assertEqual([s["rule"] for s in summaries], ["base", "pf22"])
        self.assertEqual(calls, [{}, study.RULES["pf22"]])
        self.assertEqual(summaries[0]["counts"]["red:absent:bg"], 1)
        self.assertEqual(summaries[1]["counts"]["red:absent:none"], 1)


if __name__ == "__main__":
    unittest.main()
