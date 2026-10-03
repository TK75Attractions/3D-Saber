#!/usr/bin/env python3
"""Whole-session shadow R7e / PF22 tally of a triage bundle (``summary.json`` ``shadowRuleTally``).

Read-only, free and offline. Newer recorders count the shadow verdicts over
EVERY recorded frame (not only the ~12 selected images / retained contexts) per
color, per operator segment label (including ``unlabeled``) and per exposure
bucket, and list up to six frame IDs per rule whose WINNER the rule would
reject outside the noSaber / noSaberCovered labels. Evidence only: the rules
are never applied to recognition. This module turns that tally into the two
promotion checks of docs/claude/analysis/2026-10-03_background_fp_rule_study.md:

- lit saber: zero shadow rejections of red winners on frames with a lit red
  saber (target 300+ frames). The tally gives an upper bound: rejections under
  ``sabersVisible`` must be checked on the listed frames' ORIGINAL PNGs;
- background: >= 80 % of red winners rejected under noSaber / noSaberCovered.

Unlabeled frames mix saber and background, so they are reported separately
and never counted toward either check.

    phone_saber_shadow_tally.py <bundle_dir> [<bundle_dir> ...] [--json]
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any

from phone_saber_metadata_schema import (
    FALSE_POSITIVE_SEGMENT_LABELS, SHADOW_EXPOSURE_BUCKETS, SHADOW_RULES, shadow_rule_tally_errors)

NA = "n/a"
# Promotion targets of the rule study (report wording only; never a production threshold).
LIT_SABER_MIN_FRAMES = 300
BACKGROUND_REJECT_RATE = 0.80
GROUPS = ("total", "sabersVisible", "background", "unlabeled")
GROUP_TITLES = {"total": "全 frame", "sabersVisible": "saberあり", "background": "saberなし+赤い物隠し",
                "unlabeled": "未設定"}


def _rate(rejected: int, judged: int) -> float | None:
    return round(rejected / judged, 4) if judged else None


def _group_counts(buckets: list[dict], color: str) -> dict:
    frames = sum(b.get("frames", 0) for b in buckets)
    entries = [b[color] for b in buckets if isinstance(b.get(color), dict)]
    result: dict[str, Any] = {"frames": frames, "winners": sum(e["winners"] for e in entries),
                              "eligibleCandidates": sum(e["eligibleCandidates"] for e in entries)}
    for rule in SHADOW_RULES:
        judged = sum(e[rule]["winnersJudged"] for e in entries)
        rejected = sum(e[rule]["winnersRejected"] for e in entries)
        result[rule] = {"winnersJudged": judged, "winnersRejected": rejected,
                        "rejectRate": _rate(rejected, judged),
                        "noEligibleLeft": sum(e[rule]["noEligibleLeft"] for e in entries),
                        "eligibleJudged": sum(e[rule]["eligibleJudged"] for e in entries),
                        "eligibleRejected": sum(e[rule]["eligibleRejected"] for e in entries)}
    judged = sum(e["both"]["winnersJudged"] for e in entries)
    rejected = sum(e["both"]["winnersRejected"] for e in entries)
    result["both"] = {"winnersJudged": judged, "winnersRejected": rejected, "rejectRate": _rate(rejected, judged)}
    return result


def tally_view(summary: Any, color: str = "red") -> dict:
    """The recorded whole-session tally of one color, grouped for the promotion checks.

    ``present`` is False for bundles recorded before the tally existed; ``valid``
    is False (with ``errors``) when the recorded object is inconsistent.
    """
    tally = summary.get("shadowRuleTally") if isinstance(summary, dict) else None
    if tally is None:
        return {"present": False, "note": "not recorded (bundle predates the whole-session shadow tally)"}
    errors = shadow_rule_tally_errors(tally)
    if errors:
        return {"present": True, "valid": False, "errors": errors[:5]}
    if color not in tally["colors"]:
        return {"present": True, "valid": True, "colorActive": False, "color": color}
    by_label = tally["byLabel"]
    groups = {
        "total": _group_counts([tally["total"]], color),
        "sabersVisible": _group_counts([by_label[k] for k in ("sabersVisible",) if k in by_label], color),
        "background": _group_counts([by_label[k] for k in FALSE_POSITIVE_SEGMENT_LABELS if k in by_label], color),
        "unlabeled": _group_counts([by_label[k] for k in ("unlabeled",) if k in by_label], color),
    }
    exposure = {name: _group_counts([tally["byExposure"][name]], color)
                for name in SHADOW_EXPOSURE_BUCKETS if name in tally["byExposure"]}
    samples = {rule: [s for s in tally["winnerRejectionSamples"][rule] if s.get("color") == color]
               for rule in SHADOW_RULES}
    return {"present": True, "valid": True, "colorActive": True, "color": color,
            "totalFrames": tally["totalFrames"], "exposureExperimentSetting": tally.get("exposureExperimentSetting"),
            "groups": groups, "byExposure": exposure, "samples": samples,
            "samplesOffered": dict(tally["winnerRejectionsOffered"]),
            "promotion": {rule: promotion_check(groups, rule) for rule in SHADOW_RULES}}


def promotion_check(groups: dict, rule: str) -> dict:
    """Wording of the two rule-study checks for one rule (evidence for a human, not a gate)."""
    visible, background = groups["sabersVisible"][rule], groups["background"][rule]
    if not visible["winnersJudged"]:
        lit = {"status": "no_data", "text": "saberあり区間に判定済みの赤 winner なし"}
    elif visible["winnersRejected"]:
        lit = {"status": "check_png",
               "text": f"saberあり区間で {visible['winnersRejected']}/{visible['winnersJudged']} を却下 — "
                       "列挙 frame の ORIGINAL PNG で点灯 saber か確認(点灯 saber なら不合格)"}
    else:
        short = visible["winnersJudged"] < LIT_SABER_MIN_FRAMES
        lit = {"status": "zero_rejections_insufficient" if short else "zero_rejections",
               "text": f"saberあり区間で却下 0/{visible['winnersJudged']}"
                       + (f"(目安 {LIT_SABER_MIN_FRAMES} frame 未満)" if short else "")}
    rate = background["rejectRate"]
    if rate is None:
        bg = {"status": "no_data", "text": "saberなし区間に判定済みの赤 winner なし"}
    else:
        met = rate >= BACKGROUND_REJECT_RATE
        bg = {"status": "met" if met else "not_met",
              "text": f"saberなし区間で {background['winnersRejected']}/{background['winnersJudged']} "
                      f"({rate * 100:.0f}%) を却下 — 目安 {BACKGROUND_REJECT_RATE * 100:.0f}% 以上"
                      + (" を満たす" if met else " に届かない")}
    return {"litSaber": lit, "background": bg}


def _cell(counts: dict) -> str:
    if not counts["winnersJudged"]:
        return NA
    rate = counts["rejectRate"]
    return f"{counts['winnersRejected']}/{counts['winnersJudged']} ({rate * 100:.0f}%)"


def compact_cell(view: dict, rule: str) -> str | None:
    """`rejected/judged` over the whole session, or None when not recorded."""
    if not view.get("present") or not view.get("valid") or not view.get("colorActive"):
        return None
    counts = view["groups"]["total"][rule]
    return f"{counts['winnersRejected']}/{counts['winnersJudged']}" if counts["winnersJudged"] else NA


def render_lines(view: dict, *, heading: str = "## shadow R7e / PF22 の全 frame 集計 (shadowRuleTally)") -> list[str]:
    lines = [heading, ""]
    if not view.get("present"):
        lines.append(f"- n/a — {view.get('note')}")
        return lines
    if not view.get("valid"):
        lines.append("- 記録が不正のため表示しない: " + "; ".join(view.get("errors") or []))
        return lines
    if not view.get("colorActive"):
        lines.append(f"- {view['color']} は診断対象外の録画")
        return lines
    lines.append(f"- evidence only (applied:false)。録画した全 {view['totalFrames']} frame を数えた"
                 f"(選択画像だけではない)。対象色 {view['color']}。露出実験の設定: "
                 f"{view.get('exposureExperimentSetting') or 'n/a(記録なし)'}")
    lines.append("")
    lines.append("| 区間 | frames | 赤 winner | R7e 却下 | PF22 却下 | 両方却下 | R7e 検出消失 | PF22 検出消失 |")
    lines.append("| --- | --- | --- | --- | --- | --- | --- | --- |")
    for group in GROUPS:
        g = view["groups"][group]
        lines.append(f"| {GROUP_TITLES[group]} | {g['frames']} | {g['winners']} | {_cell(g['r7e'])} | "
                     f"{_cell(g['pf22'])} | {_cell(g['both'])} | {g['r7e']['noEligibleLeft']} | "
                     f"{g['pf22']['noEligibleLeft']} |")
    if view["byExposure"]:
        lines.append("")
        lines.append("| 露出 (frame ごと) | frames | 赤 winner | R7e 却下 | PF22 却下 |")
        lines.append("| --- | --- | --- | --- | --- |")
        for name, g in view["byExposure"].items():
            lines.append(f"| {name} | {g['frames']} | {g['winners']} | {_cell(g['r7e'])} | {_cell(g['pf22'])} |")
    lines.append("")
    for rule in SHADOW_RULES:
        check = view["promotion"][rule]
        lines.append(f"- {rule.upper()} 採用目安: 点灯 saber — {check['litSaber']['text']}; "
                     f"背景 — {check['background']['text']}")
    unlabeled = view["groups"]["unlabeled"]
    if unlabeled["winners"]:
        lines.append(f"- 未設定区間の赤 winner {unlabeled['winners']} 件は saber と背景が混ざるので、"
                     "どちらの目安にも数えない(却下 frame は下の一覧の PNG で確認)。")
    for rule in SHADOW_RULES:
        samples = view["samples"][rule]
        offered = view["samplesOffered"].get(rule, 0)
        if not samples:
            lines.append(f"- {rule.upper()} が winner を却下した frame(saberなし区間を除く): なし")
            continue
        parts = []
        for sample in samples:
            where = sample.get("image") or ("context あり" if sample.get("retainedContext") else "全録画のみ")
            parts.append(f"{sample['frameID']} [{sample['label']}, {sample.get('exposureBucket', NA)}, "
                         f"purity {sample.get('meanColorPurity', NA)}, d240 {sample.get('d240', NA)}; {where}]")
        lines.append(f"- {rule.upper()} が winner を却下した frame(saberなし区間を除く、{offered} 件から"
                     f"時間的に分散して最大 {len(samples)} 件): " + ", ".join(parts))
    return lines


def _read_summary(bundle: Path) -> dict:
    value = json.loads((bundle / "summary.json").read_text(encoding="utf-8"))
    return value if isinstance(value, dict) else {}


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    parser.add_argument("bundles", nargs="+", type=Path)
    parser.add_argument("--json", action="store_true", help="print the per-session views as JSON")
    args = parser.parse_args(argv)
    views = []
    for bundle in args.bundles:
        try:
            views.append({"bundle": bundle.name, **tally_view(_read_summary(bundle))})
        except (OSError, UnicodeError, json.JSONDecodeError) as exc:
            print(f"{bundle}: summary.json unreadable: {exc}", file=sys.stderr)
            return 2
    if args.json:
        print(json.dumps(views, indent=2, ensure_ascii=False))
    else:
        for view in views:
            print("\n".join(render_lines(view, heading=f"## {view['bundle']}")))
            print()
    return 0


if __name__ == "__main__":
    sys.exit(main())
