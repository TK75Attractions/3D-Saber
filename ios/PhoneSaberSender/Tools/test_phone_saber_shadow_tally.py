#!/usr/bin/env python3
"""Whole-session shadow R7e / PF22 tally: schema, Codex input contract, report, overview, PF22 check."""
from __future__ import annotations

import contextlib
import copy
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from phone_saber_test_isolation import isolate_codex_logs as setUpModule  # noqa: F401,E402
from phone_saber_test_isolation import restore_codex_logs as tearDownModule  # noqa: F401,E402
import phone_saber_pf22_check as pf22_check
import phone_saber_shadow_tally as shadow_tool
from phone_saber_metadata_schema import shadow_rule_tally_errors, validate_document
from phone_saber_session_report import build_report, render_markdown
from phone_saber_sessions_overview import build_overview, render_html, render_markdown as overview_markdown
from phone_saber_shadow_tally import render_lines, tally_view
from phone_saber_triage_codex import BundleError, input_plan
from test_phone_saber_triage_codex import write_codex_bundle

LABELS = ("unlabeled", "sabersVisible", "noSaber", "noSaberCovered")
BUCKETS = ("le1_240", "le1_120", "le1_60", "gt1_60", "unknown")
RULES = ("r7e", "pf22")
SAMPLE_LIMIT = 6


def _empty_color() -> dict:
    rule = {"winnersJudged": 0, "winnersRejected": 0, "eligibleJudged": 0, "eligibleRejected": 0,
            "noEligibleLeft": 0}
    return {"winners": 0, "eligibleCandidates": 0, "r7e": dict(rule), "pf22": dict(rule),
            "both": {"winnersJudged": 0, "winnersRejected": 0}}


def _bucket(colors) -> dict:
    return {"frames": 0, **{c: _empty_color() for c in colors}}


def _exposure_bucket(seconds) -> str:
    if not seconds or seconds <= 0:
        return "unknown"
    for limit, name in ((240, "le1_240"), (120, "le1_120"), (60, "le1_60")):
        if seconds <= 1.02 / limit:
            return name
    return "gt1_60"


def make_tally(frames, colors=("red", "blue"), setting=None) -> dict:
    """Mirror of DebugShadowRuleTally for synthetic streams.

    frames: (frameID, label, exposureSeconds, {color: [(r7eEligible, pf22Eligible), ...]})
    where the first eligible candidate is the winner and None means "no verdict".
    """
    colors = [c for c in ("red", "blue") if c in colors]
    total = _bucket(colors)
    by_label = {label: _bucket(colors) for label in LABELS}
    by_exposure = {name: _bucket(colors) for name in BUCKETS}
    samplers = {rule: {"samples": [], "stride": 1, "offered": 0} for rule in RULES}
    for frame_id, label, exposure, observations in frames:
        bucket_name = _exposure_bucket(exposure)
        targets = (total, by_label[label], by_exposure[bucket_name])
        for target in targets:
            target["frames"] += 1
        for color in colors:
            eligible = observations.get(color) or []
            if not eligible:
                continue
            winner = eligible[0]
            for target in targets:
                counts = target[color]
                counts["winners"] += 1
                counts["eligibleCandidates"] += len(eligible)
                for index, rule in enumerate(RULES):
                    verdicts = [candidate[index] for candidate in eligible]
                    judged = [v for v in verdicts if v is not None]
                    counts[rule]["eligibleJudged"] += len(judged)
                    counts[rule]["eligibleRejected"] += sum(1 for v in judged if v is False)
                    if winner[index] is not None:
                        counts[rule]["winnersJudged"] += 1
                        if winner[index] is False:
                            counts[rule]["winnersRejected"] += 1
                            if all(v is False for v in verdicts):
                                counts[rule]["noEligibleLeft"] += 1
                if winner[0] is not None and winner[1] is not None:
                    counts["both"]["winnersJudged"] += 1
                    if winner[0] is False and winner[1] is False:
                        counts["both"]["winnersRejected"] += 1
            for index, rule in enumerate(RULES):
                if winner[index] is False and label not in ("noSaber", "noSaberCovered"):
                    sampler = samplers[rule]
                    if sampler["offered"] % sampler["stride"] == 0:
                        sampler["samples"].append({
                            "frameID": frame_id, "timestamp": frame_id / 30, "label": label, "color": color,
                            "exposureBucket": bucket_name, "meanColorPurity": 0.3, "clippedWhiteRatio": 0.0,
                            "d240": 1.0, "shadowR7eEligible": winner[0] is not False,
                            "shadowPF22Eligible": winner[1] is not False})
                        if len(sampler["samples"]) > SAMPLE_LIMIT:
                            sampler["samples"] = sampler["samples"][::2]
                            sampler["stride"] *= 2
                    sampler["offered"] += 1
    tally = {"formatVersion": 1, "applied": False, "rules": list(RULES), "colors": colors,
             "totalFrames": total["frames"], "total": total, "byLabel": by_label,
             "byExposure": {k: v for k, v in by_exposure.items() if v["frames"]},
             "winnerRejectionSamples": {r: samplers[r]["samples"] for r in RULES},
             "winnerRejectionsOffered": {r: samplers[r]["offered"] for r in RULES},
             "sampleLimit": SAMPLE_LIMIT, "definition": "test"}
    if setting:
        tally["exposureExperimentSetting"] = setting
    return tally


PASS, R7E_REJ, PF22_REJ, BOTH_REJ, NONE = (True, True), (False, True), (True, False), (False, False), (None, None)


def mixed_frames() -> list:
    """sabersVisible: 4 red winners, 1 R7e rejection; noSaber(+covered): 5 winners, 4 R7e / 5 PF22
    rejections; unlabeled: 2 winners, 1 PF22 rejection; blue winners carry no verdict."""
    frames = []
    frames.append((1, "unlabeled", 1 / 120, {"red": [PASS], "blue": [NONE]}))
    frames.append((2, "unlabeled", 1 / 120, {"red": [PF22_REJ, PASS]}))
    for i, red in enumerate([PASS, PASS, R7E_REJ, PASS]):
        frames.append((10 + i, "sabersVisible", 1 / 240, {"red": [red]}))
    frames.append((14, "sabersVisible", 1 / 240, {}))
    for i, red in enumerate([BOTH_REJ, BOTH_REJ, PF22_REJ, BOTH_REJ]):
        frames.append((20 + i, "noSaber", 1 / 30, {"red": [red, BOTH_REJ]}))
    frames.append((30, "noSaberCovered", None, {"red": [BOTH_REJ]}))
    return frames


class ShadowTallySchemaTests(unittest.TestCase):
    def test_mirror_tally_is_valid_and_sample_rules_hold(self):
        tally = make_tally(mixed_frames(), setting="maxShutter1_120")
        self.assertEqual(shadow_rule_tally_errors(tally), [])
        self.assertEqual(tally["total"]["red"]["r7e"]["winnersRejected"], 5)
        self.assertEqual([s["frameID"] for s in tally["winnerRejectionSamples"]["r7e"]], [12])
        self.assertEqual([s["frameID"] for s in tally["winnerRejectionSamples"]["pf22"]], [2])
        # A long stream stays bounded and valid.
        long = make_tally([(i, "unlabeled", None, {"red": [R7E_REJ]}) for i in range(500)])
        self.assertEqual(shadow_rule_tally_errors(long), [])
        self.assertLessEqual(len(long["winnerRejectionSamples"]["r7e"]), SAMPLE_LIMIT)
        empty = make_tally([], colors=("red",))
        self.assertEqual(shadow_rule_tally_errors(empty), [])

    def test_inconsistent_tallies_are_rejected(self):
        good = make_tally(mixed_frames())

        def broken(edit):
            value = copy.deepcopy(good)
            edit(value)
            return shadow_rule_tally_errors(value)

        cases = {
            "applied": lambda t: t.update(applied=True),
            "unknown key": lambda t: t.update(extra=1),
            "colors": lambda t: t.update(colors=["green"]),
            "rejected > judged": lambda t: t["total"]["red"]["r7e"].update(winnersRejected=99),
            "label sum": lambda t: t["byLabel"]["noSaber"]["red"].update(winners=t["byLabel"]["noSaber"]["red"]["winners"] - 1),
            "frames sum": lambda t: t["byLabel"]["unlabeled"].update(frames=0),
            "exposure sum": lambda t: t["byExposure"].pop("gt1_60"),
            "totalFrames": lambda t: t.update(totalFrames=1),
            "sample in noSaber": lambda t: t["winnerRejectionSamples"]["r7e"][0].update(label="noSaber"),
            "too many samples": lambda t: t["winnerRejectionSamples"]["r7e"].extend(
                [dict(t["winnerRejectionSamples"]["r7e"][0], frameID=100 + i) for i in range(7)]),
            "offered > rejected": lambda t: t["winnerRejectionsOffered"].update(r7e=99),
            "not increasing": lambda t: t["winnerRejectionSamples"]["pf22"].append(
                dict(t["winnerRejectionSamples"]["pf22"][0])),
            "both > rule": lambda t: t["total"]["red"]["both"].update(winnersRejected=5),
            "setting": lambda t: t.update(exposureExperimentSetting="fast"),
            "missing rule": lambda t: t["total"]["red"].pop("pf22"),
            "bool count": lambda t: t["total"]["red"].update(winners=True),
        }
        for name, edit in cases.items():
            self.assertTrue(broken(edit), name)
        self.assertEqual(shadow_rule_tally_errors([]), ["shadowRuleTally must be an object"])

    def test_metadata_root_keeps_a_valid_tally_and_drops_a_broken_one(self):
        document = {"formatVersion": 1, "sessionID": "s", "width": 4, "height": 4, "frames": [],
                    "shadowRuleTally": make_tally(mixed_frames())}
        validated = validate_document(document)
        self.assertIn("shadowRuleTally", validated.document)
        self.assertFalse(any("shadowRuleTally" in key for key in validated.report.warnings))
        document["shadowRuleTally"]["applied"] = True
        validated = validate_document(document)
        self.assertNotIn("shadowRuleTally", validated.document)
        self.assertTrue(any(key.startswith("shadowRuleTally") for key in validated.report.warnings))
        old = validate_document({"formatVersion": 1, "sessionID": "s", "width": 4, "height": 4, "frames": []})
        self.assertNotIn("shadowRuleTally", old.document)


def bundle_with_tally(root: Path, tally: dict | None, *, session: str = "phonesaber_20261004_101010_100") -> Path:
    bundle = root / f"phone_saber_triage_{session}"
    write_codex_bundle(bundle)
    if tally is not None:
        path = bundle / "summary.json"
        summary = json.loads(path.read_text())
        summary["shadowRuleTally"] = tally
        path.write_text(json.dumps(summary))
    return bundle


class ShadowTallyToolTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)

    def tearDown(self):
        self.tmp.cleanup()

    def test_codex_input_contract_accepts_the_tally_and_rejects_a_broken_one(self):
        tally = make_tally(mixed_frames())
        input_plan(bundle_with_tally(self.root / "a", tally))
        tally["total"]["red"]["r7e"]["winnersRejected"] = 999
        with self.assertRaisesRegex(BundleError, "shadow rule tally is malformed"):
            input_plan(bundle_with_tally(self.root / "b", tally))

    def test_view_groups_labels_and_states_the_promotion_checks(self):
        tally = make_tally(mixed_frames(), setting="auto")
        tally["winnerRejectionSamples"]["r7e"][0]["image"] = "images/image_01_x.png"
        view = tally_view({"shadowRuleTally": tally})
        groups = view["groups"]
        self.assertEqual((groups["sabersVisible"]["winners"], groups["sabersVisible"]["r7e"]["winnersRejected"]), (4, 1))
        self.assertEqual(groups["background"]["winners"], 5)
        self.assertEqual(groups["background"]["r7e"]["winnersRejected"], 4)
        self.assertEqual(groups["background"]["pf22"]["rejectRate"], 1.0)
        self.assertEqual(groups["unlabeled"]["pf22"]["winnersRejected"], 1)
        self.assertEqual(groups["total"]["r7e"]["winnersJudged"], 11)
        self.assertEqual(view["promotion"]["r7e"]["litSaber"]["status"], "check_png")
        self.assertEqual(view["promotion"]["r7e"]["background"]["status"], "met")       # 4/5 = 80 %
        self.assertEqual(view["promotion"]["pf22"]["litSaber"]["status"], "zero_rejections_insufficient")
        self.assertEqual(set(view["byExposure"]), {"le1_120", "le1_240", "gt1_60", "unknown"})
        text = "\n".join(render_lines(view))
        self.assertIn("| saberあり | 5 | 4 | 1/4 (25%) |", text)
        self.assertIn("| saberなし+赤い物隠し | 5 | 5 | 4/5 (80%) | 5/5 (100%) |", text)
        self.assertIn("未設定区間の赤 winner 2 件", text)
        self.assertIn("12 [sabersVisible, le1_240", text)
        self.assertIn("images/image_01_x.png", text)
        self.assertEqual(shadow_tool.compact_cell(view, "pf22"), "6/11")
        # Absent, broken and blue-only tallies degrade instead of raising.
        self.assertFalse(tally_view({})["present"])
        self.assertIn("n/a", "\n".join(render_lines(tally_view({}))))
        tally["applied"] = True
        self.assertFalse(tally_view({"shadowRuleTally": tally})["valid"])
        blue = tally_view({"shadowRuleTally": make_tally([], colors=("blue",))})
        self.assertFalse(blue["colorActive"])
        self.assertIsNone(shadow_tool.compact_cell(blue, "r7e"))

    def test_unlabeled_session_is_still_useful(self):
        frames = [(i, "unlabeled", 1 / 120, {"red": [R7E_REJ if i % 3 == 0 else PASS]}) for i in range(30)]
        view = tally_view({"shadowRuleTally": make_tally(frames)})
        self.assertEqual(view["groups"]["unlabeled"]["r7e"]["winnersRejected"], 10)
        self.assertEqual(view["promotion"]["r7e"]["litSaber"]["status"], "no_data")
        self.assertEqual(view["promotion"]["r7e"]["background"]["status"], "no_data")
        self.assertEqual(len(view["samples"]["r7e"]), 5)  # 10 offered, stride 2 after the first overflow

    def test_cli_prints_each_bundle(self):
        bundle = bundle_with_tally(self.root, make_tally(mixed_frames()))
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(shadow_tool.main([str(bundle)]), 0)
        self.assertIn("全 frame", out.getvalue())
        with contextlib.redirect_stdout(io.StringIO()) as json_out:
            self.assertEqual(shadow_tool.main([str(bundle), "--json"]), 0)
        self.assertEqual(json.loads(json_out.getvalue())[0]["groups"]["total"]["winners"], 11)

    def test_session_report_prefers_the_recorded_whole_session_tally(self):
        bundle = bundle_with_tally(self.root, make_tally(mixed_frames()))
        report = build_report(bundle, margins=(), holds=())
        self.assertEqual(report["shadowRuleTally"]["groups"]["background"]["r7e"]["winnersRejected"], 4)
        text = render_markdown(report)
        self.assertIn("## shadow R7e / PF22 の全 frame 集計 (shadowRuleTally)", text)
        self.assertIn("R7E 採用目安", text)
        old = bundle_with_tally(self.root / "old", None)
        old_text = render_markdown(build_report(old, margins=(), holds=()))
        self.assertIn("n/a — not recorded", old_text)

    def test_overview_and_pf22_check_show_whole_session_counts(self):
        inbox = self.root / "inbox"
        bundle_with_tally(inbox, make_tally(mixed_frames()), session="phonesaber_20261004_101010_100")
        bundle_with_tally(inbox, None, session="phonesaber_20261003_101010_100")
        overview = build_overview(inbox)
        rows = {r["sessionID"]: r for r in overview["sessions"]}
        whole = rows["phonesaber_20261004_101010_100"]["shadowWholeSession"]
        self.assertEqual(whole["groups"]["background"]["r7e"], [4, 5])
        self.assertEqual(whole["promotion"]["r7e"], {"litSaber": "check_png", "background": "met"})
        self.assertIsNone(rows["phonesaber_20261003_101010_100"]["shadowWholeSession"])
        totals = overview["totals"]["wholeSessionShadow"]
        self.assertEqual((totals["sessions"], totals["total.r7e"], totals["sabersVisible.r7e"]), (1, [5, 11], [1, 4]))
        markdown = overview_markdown(overview)
        self.assertIn("全 5/11", markdown)
        self.assertIn("R7e 却下 saberあり 1/4 (25%) / saberなし 4/5 (80%)", markdown)
        self.assertIn("全 frame shadow R7e 却下", render_html(overview))

        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(pf22_check.main([str(p) for p in sorted(inbox.iterdir()) if p.is_dir()]), 0)
        text = out.getvalue()
        self.assertIn("| 101010_100 | not recorded |", text.replace("phonesaber_20261003_", ""))
        self.assertIn("| 6/11 (55%) | 0/4 (0%) | 5/5 (100%) | 1/2 (50%) | 5/11 (45%) | 1/4 (25%) | 4/5 (80%) "
                      "| 0/2 (0%) |", text)


if __name__ == "__main__":
    unittest.main()
