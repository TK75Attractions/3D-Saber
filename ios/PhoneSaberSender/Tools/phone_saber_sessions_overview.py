#!/usr/bin/env python3
"""Cross-session overview of every received PhoneSaber triage bundle (read-only on the bundles).

Scans the diagnostics inbox (``~/Library/Application Support/PhoneSaber/diagnostics-inbox``
or ``PHONESABER_DIAGNOSTICS_INBOX``), builds one row per ``phone_saber_triage_*``
bundle with the existing helpers (``phone_saber_session_report.build_report``,
which itself uses phone_saber_tracking_diagnostics / phone_saber_background_evidence),
and writes two files next to the bundles:

- ``phone_saber_sessions_overview.html``: one self-contained page (inline CSS and
  SVG, no network, Japanese labels, light/dark, phone width) with headline
  numbers, two small trend charts and the session table;
- ``phone_saber_sessions_overview.md``: the compact Markdown version.

Per session: date/time, frames and approximate duration, active colors, median
exposure (frames[].camera) and the optional ``cameraExposureExperiment``,
selected-frame >=100px jumps and candidateSwitch (the same rows the shadow PF22
tally uses), whole-recording endpoint_jump / candidate_switch signal counts,
CASE hints (countForTally rows only), shadow R7e / PF22 tallies (recorded
``shadowPF22`` or recomputed for older bundles), static-hotspot count, the upload
route (receiver log: 127.0.0.1 = P2P relay, private address = LAN), Codex
analysis status (analysis_report.json, else the receiver log) and links to the
one-page ``.report.md`` and ``analysis_report.md``.

Bundles are never written. ``--write-missing-reports`` additionally writes the
existing one-page report beside a bundle that has none (the receiver does the
same for every new upload). Everything here is evidence for a human, never a
production threshold or a gate input. Fields absent from older bundles show n/a.

    phone_saber_sessions_overview.py [--inbox DIR] [--output-dir DIR] [--open]
"""
from __future__ import annotations

import argparse
import html
import ipaddress
import json
import math
import os
import re
import subprocess
import sys
import time
from collections import Counter
from datetime import datetime
from pathlib import Path
from typing import Any, Callable
from urllib.parse import quote

from phone_saber_background_evidence import EVENT_JUMP_PX, _contexts
from phone_saber_session_report import (
    build_report, report_path_for, write_session_report, write_text_atomic)

INBOX_PREFIX = "phone_saber_triage_"
OVERVIEW_BASENAME = "phone_saber_sessions_overview"
NA = "n/a"
DEFAULT_FPS = 30.0
# Receiver logs are small (tens of KB); this only bounds a pathological file.
LOG_TAIL_BYTES = 4 * 1024 * 1024
SESSION_TIME = re.compile(r"(\d{8})_(\d{6})_(\d{3})")
RECEIVED = re.compile(r"\[triage\] received (" + INBOX_PREFIX + r"\S+)\s.*?\bfrom (\S+) →")
ANALYSIS_SESSION = re.compile(r"\[AUTO_REPAIR\]\[ANALYSIS\].*\bsessionID=(\S+)")
CASE_KEYS = ("A", "B", "C")

CODEX_LABELS = {
    "ok": "完了", "precheck": "precheck で中止", "timeout": "timeout", "failed": "失敗",
    "running": "実行中(または中断)", "not_run": "未実行", "unreadable": "読めない", "none": "記録なし",
}


def default_inbox() -> Path:
    return Path(os.environ.get(
        "PHONESABER_DIAGNOSTICS_INBOX",
        str(Path.home() / "Library" / "Application Support" / "PhoneSaber" / "diagnostics-inbox"))).expanduser()


def default_logs_dir() -> Path:
    return Path.home() / "Library" / "Logs" / "PhoneSaber"


def _get(value: Any, *keys: str) -> Any:
    for key in keys:
        if not isinstance(value, dict):
            return None
        value = value.get(key)
    return value


def _num(value: Any) -> float | None:
    return float(value) if isinstance(value, (int, float)) and not isinstance(value, bool) \
        and math.isfinite(value) else None


def find_bundles(inbox: Path) -> list[Path]:
    try:
        entries = list(inbox.iterdir())
    except OSError:
        return []
    return sorted((p for p in entries if p.name.startswith(INBOX_PREFIX) and p.is_dir() and not p.is_symlink()),
                  key=lambda p: p.name)


def session_started_at(name: str) -> datetime | None:
    match = SESSION_TIME.search(name)
    if not match:
        return None
    try:
        return datetime.strptime(match.group(1) + match.group(2) + match.group(3), "%Y%m%d%H%M%S%f")
    except ValueError:
        return None


def estimate_fps(summary: dict) -> tuple[float, bool]:
    """Frames per second from the selected images' (frameID, timestamp) spread;
    (DEFAULT_FPS, False) when the spread is too small to measure."""
    pairs = sorted({(int(i["frameID"]), float(i["timestamp"])) for i in summary.get("images") or []
                    if isinstance(i, dict) and isinstance(i.get("frameID"), int)
                    and not isinstance(i.get("frameID"), bool) and _num(i.get("timestamp")) is not None})
    if len(pairs) >= 2:
        (f0, t0), (f1, t1) = pairs[0], pairs[-1]
        if f1 - f0 >= 10 and t1 - t0 > 0.3:
            fps = (f1 - f0) / (t1 - t0)
            if 5 <= fps <= 240:
                return fps, True
    return DEFAULT_FPS, False


# --- receiver logs -------------------------------------------------------------

def read_receiver_logs(logs_dir: Path | None) -> dict[str, dict]:
    """bundle name / sessionID -> {"from": ip, "analysisLine": last [AUTO_REPAIR][ANALYSIS] line}.
    Logs are read oldest first so a later run overrides an earlier one."""
    index: dict[str, dict] = {}
    if logs_dir is None:
        return index
    try:
        paths = sorted(p for p in logs_dir.glob("triage-*.log") if p.is_file() and not p.is_symlink())
    except OSError:
        return index
    for path in paths:
        try:
            with path.open("rb") as stream:
                size = path.stat().st_size
                stream.seek(max(0, size - LOG_TAIL_BYTES))
                text = stream.read().decode("utf-8", "replace")
        except OSError:
            continue
        for line in text.splitlines():
            received = RECEIVED.search(line)
            if received:
                index.setdefault(received.group(1), {})["from"] = received.group(2)
                continue
            analysis = ANALYSIS_SESSION.search(line)
            if analysis:
                index.setdefault(INBOX_PREFIX + analysis.group(1), {})["analysisLine"] = line.strip()
    return index


def route_label(address: str | None) -> dict:
    if not address:
        return {"route": None, "label": NA, "from": None}
    try:
        parsed = ipaddress.ip_address(address.split("%", 1)[0])
    except ValueError:
        return {"route": "unknown", "label": address, "from": address}
    if parsed.is_loopback:
        return {"route": "p2p", "label": "P2P 中継", "from": address}
    if parsed.is_private or parsed.is_link_local:
        return {"route": "lan", "label": "LAN", "from": address}
    return {"route": "other", "label": address, "from": address}


def codex_status(bundle: Path, log_line: str | None) -> dict:
    """ok / precheck / timeout / failed / running / not_run / unreadable / none."""
    report_json = bundle / "analysis_report.json"
    report_md = bundle / "analysis_report.md"
    link = report_md if report_md.is_file() else report_json if report_json.is_file() else None
    status, detail = "none", None
    if report_json.is_file() and not report_json.is_symlink():
        try:
            report = json.loads(report_json.read_text(encoding="utf-8"))
        except (OSError, UnicodeError, json.JSONDecodeError):
            report = None
        if not isinstance(report, dict):
            status = "unreadable"
        elif report.get("analysisExecuted") is True or (
                "analysisExecuted" not in report and isinstance(report.get("analysis"), dict)):
            status = "ok"  # formatVersion 2 reports predate analysisExecuted
            detail = _get(report, "analysis", "repair_assessment", "decision")
        elif _get(report, "precheck", "reasonCodes"):
            status, detail = "precheck", ", ".join(map(str, report["precheck"]["reasonCodes"][:3]))
        else:
            status = "not_run"
    elif log_line:
        if "result=FAIL" in log_line:
            reason = log_line.split("result=FAIL", 1)[1].strip()
            status = "timeout" if "CLI_TIMEOUT" in reason else "precheck" if "PRECHECK" in reason else "failed"
            detail = reason[:120]
        elif "result=precheck_failed" in log_line:
            status = "precheck"
        elif "result=starting" in log_line:
            status = "running"
        elif "result=complete" in log_line or "result=existing_analysis" in log_line:
            status, detail = "ok", "analysis_report.json なし"
    return {"status": status, "label": CODEX_LABELS[status], "detail": detail,
            "link": str(link) if link is not None else None}


# --- one session ---------------------------------------------------------------

def _tracking_recorded(bundle: Path) -> bool:
    """Whether any context records per-frame tracking (older bundles do not)."""
    for _, context in _contexts(bundle):
        for frame in context["frames"]:
            if isinstance(frame, dict) and any(isinstance(_get(frame, color, "tracking"), dict)
                                               for color in ("red", "blue")):
                return True
    return False


def _signal_count(summary: dict, kind: str) -> int | None:
    count = _get(summary, "motionEventSummary", "signalDistributions", kind, "count")
    return int(count) if isinstance(count, int) and not isinstance(count, bool) else None


def _exposure_experiment(section: Any) -> dict:
    if not isinstance(section, dict) or not section.get("present"):
        return {"present": False, "label": NA}
    if not section.get("valid"):
        return {"present": True, "label": "不正な値", "verdict": section.get("verdict")}
    setting, status = section.get("setting"), section.get("status")
    applied = _num(section.get("appliedMaxExposureSeconds"))
    if setting == "auto":
        label = "自動"
    elif section.get("capActive") is True and applied:
        label = f"上限 1/{1 / applied:.0f} s"
    else:
        label = f"{setting} 要求・未適用 ({status})"
    return {"present": True, "label": label, "setting": setting, "status": status,
            "capActive": section.get("capActive"), "appliedMaxExposureSeconds": applied,
            "verdict": section.get("verdict")}


def _relative_link(target: Path | str | None, output_dir: Path) -> str | None:
    if target is None:
        return None
    try:
        relative = os.path.relpath(Path(target), output_dir)
    except ValueError:
        relative = str(Path(target).resolve())
    return quote(Path(relative).as_posix())


def session_row(bundle: Path, logs: dict[str, dict], output_dir: Path) -> dict:
    """Read-only. Never raises: a broken bundle becomes a row with errors."""
    errors: list[str] = []
    try:
        report = build_report(bundle, margins=(), holds=())  # the replay sweep is per-session detail
    except Exception as exc:  # noqa: BLE001 - one bad bundle must not hide the others
        errors.append(f"build_report: {type(exc).__name__}: {exc}")
        report = {}
    errors.extend(report.get("errors") or [])
    try:
        summary = json.loads((bundle / "summary.json").read_text(encoding="utf-8"))
        summary = summary if isinstance(summary, dict) else {}
    except (OSError, UnicodeError, json.JSONDecodeError):
        summary = {}
    session_id = bundle.name[len(INBOX_PREFIX):]
    started = session_started_at(session_id) or session_started_at(str(summary.get("sessionID") or ""))
    frames = summary.get("recordedFrameCount")
    frames = frames if isinstance(frames, int) and not isinstance(frames, bool) else None
    fps, measured = estimate_fps(summary)
    duration = frames / fps if frames is not None else None
    minutes = duration / 60 if duration else None

    background = report.get("backgroundEvidence") if isinstance(report.get("backgroundEvidence"), dict) else {}
    exposure = background.get("exposure") or {}
    exposure_median = _get(exposure, "exposureDurationSeconds", "median")
    pf22 = background.get("shadowPF22Tally") or {}
    event_rows = _get(pf22, "events", "rows") or []
    try:
        tracking = _tracking_recorded(bundle)
    except Exception:  # noqa: BLE001
        tracking = False
    jumps = sum(1 for r in event_rows if (_num(r.get("midpointDisplacementPx")) or 0) >= EVENT_JUMP_PX)
    switches = sum(1 for r in event_rows if r.get("candidateSwitch") is True)
    signal_jumps, signal_switches = _signal_count(summary, "endpoint_jump"), _signal_count(summary, "candidate_switch")

    case_counts = report.get("caseHintCounts")
    r7e = _get(background, "shadowR7eTally", "total") if background else None
    pf22_winners = pf22.get("winners") if isinstance(pf22.get("winners"), dict) else None
    hotspots = report.get("staticHotspots") if isinstance(report.get("staticHotspots"), dict) else None
    log = logs.get(bundle.name) or {}
    codex = codex_status(bundle, log.get("analysisLine"))
    session_report = report_path_for(bundle)
    return {
        "bundle": bundle.name,
        "sessionID": session_id,
        "startedAt": started.isoformat(timespec="seconds") if started else None,
        "recordedFrameCount": frames,
        "fps": round(fps, 1), "fpsMeasured": measured,
        "durationSeconds": round(duration, 1) if duration is not None else None,
        "activeColors": summary.get("activeColors") if isinstance(summary.get("activeColors"), list) else None,
        "inputContract": report.get("inputContract"),
        "exposure": {
            "framesWithCamera": exposure.get("framesWithCamera", 0),
            "medianExposureMs": round(exposure_median * 1000, 2) if _num(exposure_median) else None,
            "medianISO": _get(exposure, "iso", "median"),
            "experiment": _exposure_experiment(report.get("cameraExposureExperiment")),
        },
        "selectedFrames": {
            "trackingRecorded": tracking,
            "jumpsAtLeast100px": jumps if tracking else None,
            "candidateSwitch": switches if tracking else None,
            "definition": "selected-frame rows of the shadow PF22 tally (detected, not predicted, winner known)",
        },
        "wholeRecordingSignals": {
            "endpointJump": signal_jumps, "candidateSwitch": signal_switches,
            "endpointJumpPerMinute": round(signal_jumps / minutes, 2)
            if signal_jumps is not None and minutes else None,
            "candidateSwitchPerMinute": round(signal_switches / minutes, 2)
            if signal_switches is not None and minutes else None,
        },
        "caseHints": dict(case_counts) if isinstance(case_counts, dict) else None,
        "shadowR7e": dict(r7e) if isinstance(r7e, dict) else None,
        "shadowPF22": {
            "winners": pf22_winners.get("frames"), "withVerdict": pf22_winners.get("withVerdict"),
            "wouldReject": pf22_winners.get("pf22WouldReject"),
            "verdictSources": pf22_winners.get("verdictSources") or {},
            "eventOutcomes": _get(pf22, "events", "pf22Outcomes"),
        } if pf22_winners else None,
        "likelyBackgroundClusters": hotspots.get("likelyBackgroundCount") if hotspots else None,
        "redFalsePositiveVerdict": _get(report, "segments", "redFalsePositiveVerdict", "verdict"),
        "route": route_label(log.get("from")),
        "codex": {**codex, "href": _relative_link(codex["link"], output_dir)},
        "sessionReport": {"exists": session_report.is_file(),
                          "href": _relative_link(session_report, output_dir) if session_report.is_file() else None},
        "errors": errors,
    }


def build_overview(inbox: Path, *, logs_dir: Path | None = None, output_dir: Path | None = None,
                   write_missing_reports: bool = False, log: Callable[[str], None] = lambda _: None) -> dict:
    output_dir = output_dir or inbox
    bundles = find_bundles(inbox)
    if write_missing_reports:
        for bundle in bundles:
            if not report_path_for(bundle).is_file():
                try:
                    write_session_report(bundle)
                    log(f"report written: {report_path_for(bundle)}")
                except Exception as exc:  # noqa: BLE001 - the overview must still be written
                    log(f"report FAILED for {bundle.name}: {type(exc).__name__}: {exc}")
    logs = read_receiver_logs(logs_dir)
    rows = [session_row(bundle, logs, output_dir) for bundle in bundles]
    rows.sort(key=lambda r: (r["startedAt"] or "", r["bundle"]))
    return {"generatedAt": datetime.now().isoformat(timespec="seconds"), "inbox": str(inbox),
            "sessions": rows, "totals": totals(rows)}


def _sum(values: list[Any]) -> int | None:
    numbers = [v for v in values if isinstance(v, (int, float)) and not isinstance(v, bool)]
    return sum(numbers) if numbers else None


def totals(rows: list[dict]) -> dict:
    case = Counter()
    for row in rows:
        case.update({k: v for k, v in (row["caseHints"] or {}).items() if isinstance(v, int)})
    duration = _sum([r["durationSeconds"] for r in rows])
    return {
        "sessions": len(rows),
        "recordedFrames": _sum([r["recordedFrameCount"] for r in rows]),
        "durationMinutes": round(duration / 60, 1) if duration is not None else None,
        "sessionsWithTracking": sum(1 for r in rows if r["selectedFrames"]["trackingRecorded"]),
        "jumpsAtLeast100px": _sum([r["selectedFrames"]["jumpsAtLeast100px"] for r in rows]),
        "candidateSwitch": _sum([r["selectedFrames"]["candidateSwitch"] for r in rows]),
        "caseHints": dict(case),
        "r7eWouldReject": _sum([(r["shadowR7e"] or {}).get("r7eWouldReject") for r in rows]),
        "r7eWinners": _sum([(r["shadowR7e"] or {}).get("winners") for r in rows]),
        "pf22WouldReject": _sum([(r["shadowPF22"] or {}).get("wouldReject") for r in rows]),
        "pf22WithVerdict": _sum([(r["shadowPF22"] or {}).get("withVerdict") for r in rows]),
        "codex": dict(Counter(r["codex"]["status"] for r in rows)),
        "routes": dict(Counter(r["route"]["route"] or "n/a" for r in rows)),
        "sessionsWithErrors": sum(1 for r in rows if r["errors"]),
    }


# --- formatting helpers ----------------------------------------------------------

def _t(value: Any, suffix: str = "") -> str:
    if value is None:
        return NA
    if isinstance(value, float):
        return f"{value:g}{suffix}"
    return f"{value}{suffix}"


def _when(row: dict) -> str:
    if not row["startedAt"]:
        return row["sessionID"]
    return datetime.fromisoformat(row["startedAt"]).strftime("%m/%d %H:%M")


def _duration(row: dict) -> str:
    seconds = row["durationSeconds"]
    if seconds is None:
        return NA
    approx = "" if row["fpsMeasured"] else "≈"
    return f"{approx}{int(seconds // 60)}:{int(seconds % 60):02d}"


def _colors(row: dict) -> str:
    colors = row["activeColors"]
    return "+".join(c.upper() for c in colors) if colors else NA


def _case(row: dict) -> str:
    hints = row["caseHints"]
    if hints is None:
        return NA
    text = " ".join(f"{k}{hints.get(k, 0)}" for k in CASE_KEYS)
    unknown = sum(v for k, v in hints.items() if k not in CASE_KEYS and k != "none" and isinstance(v, int))
    return text + (f" ?{unknown}" if unknown else "")


def _r7e(row: dict) -> str:
    tally = row["shadowR7e"]
    if not tally or not tally.get("winners"):
        return NA
    judged = (tally.get("r7eWouldReject") or 0) + (tally.get("r7eKeeps") or 0)
    return f"{tally.get('r7eWouldReject', 0)}/{judged}" if judged else NA


def _pf22(row: dict) -> str:
    tally = row["shadowPF22"]
    if not tally or not tally.get("withVerdict"):
        return NA
    sources = tally.get("verdictSources") or {}
    mark = "" if sources.get("recorded") else "*"
    return f"{tally.get('wouldReject', 0)}/{tally['withVerdict']}{mark}"


def _signals(row: dict) -> str:
    signals = row["wholeRecordingSignals"]
    if signals["endpointJump"] is None:
        return NA
    rate = signals["endpointJumpPerMinute"]
    return f"{signals['endpointJump']} ({_t(rate)}/分)" if rate is not None else str(signals["endpointJump"])


def _exposure(row: dict) -> str:
    ms = row["exposure"]["medianExposureMs"]
    return f"{ms:g} ms" if ms is not None else NA


COLUMNS = ("日時", "長さ", "frames", "色", "露出中央値", "露出実験", "≥100px", "switch",
           "全体 jump (/分)", "CASE A/B/C", "R7e 却下", "PF22 却下", "背景候補", "経路", "Codex", "要約")


def render_markdown(overview: dict) -> str:
    t = overview["totals"]
    case = t["caseHints"]
    lines = [
        "# PhoneSaber セッション一覧",
        "",
        f"生成 {overview['generatedAt']} / inbox `{overview['inbox']}` / 読み取りのみ。数値は根拠であり production の閾値ではない。",
        "",
        f"- セッション {t['sessions']} 件、録画 {_t(t['recordedFrames'])} frames(約 {_t(t['durationMinutes'])} 分)",
        f"- 選択フレームの ≥{EVENT_JUMP_PX:g}px ジャンプ {_t(t['jumpsAtLeast100px'])}、candidateSwitch "
        f"{_t(t['candidateSwitch'])}(tracking 記録ありの {t['sessionsWithTracking']} 件)",
        f"- CASE ヒント(countForTally のみ): A {case.get('A', 0)} / B {case.get('B', 0)} / C {case.get('C', 0)}"
        f" / none {case.get('none', 0)}",
        f"- shadow R7e 却下 {_t(t['r7eWouldReject'])} / winners {_t(t['r7eWinners'])}、shadow PF22 却下 "
        f"{_t(t['pf22WouldReject'])} / 判定あり {_t(t['pf22WithVerdict'])}(red winners)",
        "- Codex: " + ", ".join(f"{CODEX_LABELS.get(k, k)} {v}" for k, v in sorted(t["codex"].items())),
        "",
        "| " + " | ".join(COLUMNS) + " |",
        "| " + " | ".join("---" for _ in COLUMNS) + " |",
    ]
    for row in reversed(overview["sessions"]):
        report = f"[要約]({row['sessionReport']['href']})" if row["sessionReport"]["href"] else "なし"
        codex = row["codex"]["label"] + (f" ({row['codex']['detail']})" if row["codex"]["status"] == "ok"
                                         and row["codex"]["detail"] else "")
        if row["codex"]["href"]:
            codex = f"[{codex}]({row['codex']['href']})"
        sel = row["selectedFrames"]
        lines.append("| " + " | ".join([
            f"{_when(row)} `{row['sessionID']}`", _duration(row), _t(row["recordedFrameCount"]), _colors(row),
            _exposure(row), row["exposure"]["experiment"]["label"], _t(sel["jumpsAtLeast100px"]),
            _t(sel["candidateSwitch"]), _signals(row), _case(row), _r7e(row), _pf22(row),
            _t(row["likelyBackgroundClusters"]), row["route"]["label"], codex, report]) + " |")
    lines += ["",
              f"- ≥100px / switch: 選択フレーム(bundle に入った event 窓)の中だけ。全体 jump は録画全体の "
              "endpoint_jump シグナル数(motionEventSummary、09-30 以降)。",
              "- R7e / PF22 = 却下になる winner 数 / 判定できた winner 数(shadow、applied:false)。"
              "PF22 の * は記録なし(古い bundle)で meanColorPurity / clippedWhiteRatio から再計算。",
              "- 背景候補 = likelyBackground の静的ホットスポット数(hint)。長さの ≈ は 30 fps と仮定した推定。",
              "- 経路は受信ログから: 127.0.0.1 = P2P 中継、プライベートアドレス = LAN。ログに無ければ n/a。",
              ""]
    errors = [(r["sessionID"], e) for r in overview["sessions"] for e in r["errors"]]
    errors += [(r["sessionID"], f"input contract {r['inputContract']}(CASE hint は n/a)")
               for r in overview["sessions"] if str(r["inputContract"]).startswith("FAIL")]
    if errors:
        lines += ["## 読み取りの注意", ""] + [f"- {sid}: {e}" for sid, e in errors] + [""]
    return "\n".join(lines)


# --- HTML ------------------------------------------------------------------------

CSS = """
:root{color-scheme:light;--bg:#f6f6f4;--surface:#fcfcfb;--border:#e2e1dc;--grid:#ecebe7;
--text:#0b0b0b;--text2:#52514e;--muted:#7a7974;--series:#2a78d6;--accent-bg:#eef4fc;
--ok:#1a7f37;--warn:#9a6700;--bad:#c62828;--info:#52514e}
@media (prefers-color-scheme:dark){:root:not([data-theme="light"]){color-scheme:dark;--bg:#121211;
--surface:#1a1a19;--border:#33332f;--grid:#2a2a27;--text:#ffffff;--text2:#c3c2b7;--muted:#959489;
--series:#3987e5;--accent-bg:#1d2633;--ok:#4cc26a;--warn:#e0a526;--bad:#ef6b6b;--info:#c3c2b7}}
:root[data-theme="dark"]{color-scheme:dark;--bg:#121211;--surface:#1a1a19;--border:#33332f;--grid:#2a2a27;
--text:#ffffff;--text2:#c3c2b7;--muted:#959489;--series:#3987e5;--accent-bg:#1d2633;--ok:#4cc26a;
--warn:#e0a526;--bad:#ef6b6b;--info:#c3c2b7}
*{box-sizing:border-box}
body{margin:0;background:var(--bg);color:var(--text);font:14px/1.5 -apple-system,BlinkMacSystemFont,
"Hiragino Sans","Hiragino Kaku Gothic ProN","Noto Sans JP",sans-serif}
main{max-width:1280px;margin:0 auto;padding:20px 16px 40px}
h1{font-size:20px;margin:0 0 4px}h2{font-size:15px;margin:28px 0 10px}
.sub{color:var(--text2);font-size:12px;margin:0;overflow-wrap:anywhere}
.tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(130px,1fr));gap:10px;margin-top:16px}
.tile{background:var(--surface);border:1px solid var(--border);border-radius:10px;padding:10px 12px}
.tile .k{color:var(--text2);font-size:12px}.tile .v{font-size:22px;font-weight:600;font-variant-numeric:tabular-nums}
.tile .d{color:var(--muted);font-size:11px}
.charts{display:grid;grid-template-columns:repeat(auto-fit,minmax(300px,1fr));gap:12px}
figure{margin:0;background:var(--surface);border:1px solid var(--border);border-radius:10px;padding:12px}
figcaption{font-size:13px;font-weight:600}figcaption span{display:block;font-weight:400;color:var(--text2);font-size:11px}
svg{display:block;width:100%;height:auto}
svg .grid{stroke:var(--grid);stroke-width:1}svg .axis{fill:var(--muted);font-size:11px}
.fnote{color:var(--muted);font-size:11px;margin:4px 0 0}
svg .line{fill:none;stroke:var(--series);stroke-width:2;stroke-linejoin:round;stroke-linecap:round}
svg .dot{fill:var(--series);stroke:var(--surface);stroke-width:2}
svg .hit{fill:transparent}svg .hit:hover+.dot,svg .dot:hover{r:6}
svg .end{fill:var(--text);font-size:11px;font-weight:600}
.scroll{overflow-x:auto;background:var(--surface);border:1px solid var(--border);border-radius:10px}
table{border-collapse:collapse;width:100%;font-size:12px;font-variant-numeric:tabular-nums}
th,td{padding:6px 8px;border-bottom:1px solid var(--grid);text-align:right;white-space:nowrap;vertical-align:top}
th{position:sticky;top:0;background:var(--surface);color:var(--text2);font-weight:600;font-size:11px}
td.l,th.l{text-align:left}tr:last-child td{border-bottom:0}
th:first-child,td:first-child{position:sticky;left:0;background:var(--surface);z-index:1}th:first-child{z-index:2}
tbody tr:hover td{background:var(--accent-bg)}
td .id{display:block;color:var(--muted);font-size:10px}
.na{color:var(--muted)}a{color:var(--series)}
.st{white-space:nowrap}.st-ok{color:var(--ok)}.st-warn{color:var(--warn)}.st-bad{color:var(--bad)}.st-info{color:var(--info)}
.notes{color:var(--text2);font-size:12px;padding-left:18px}.notes li{margin:2px 0}
.err{color:var(--bad);font-size:12px}
"""

CODEX_STYLE = {"ok": ("st-ok", "✓"), "precheck": ("st-warn", "!"), "timeout": ("st-bad", "⏱"),
               "failed": ("st-bad", "✕"), "running": ("st-info", "…"), "not_run": ("st-info", "–"),
               "unreadable": ("st-bad", "?"), "none": ("st-info", "–")}


def _e(value: Any) -> str:
    return html.escape(str(value), quote=True)


def _cell(text: str, *, left: bool = False, title: str | None = None) -> str:
    classes = [c for c in ("l" if left else "", "na" if text == NA else "") if c]
    cls = f' class="{" ".join(classes)}"' if classes else ""
    tip = f' title="{_e(title)}"' if title else ""
    return f"<td{cls}{tip}>{_e(text)}</td>"


def trend_chart(points: list[tuple[str, str, float | None]], *, title: str, subtitle: str, unit: str) -> str:
    """Single-series line over sessions in time order; a missing value breaks the line.
    points: (x label, tooltip label, value)."""
    width, height = 440, 180
    left, right, top, bottom = 36, 40, 10, 22
    # Sessions before the first recorded value (older builds) are left out, not drawn as a gap.
    first = next((i for i, (_, _, v) in enumerate(points) if v is not None), len(points))
    skipped, points = first, points[first:]
    values = [v for _, _, v in points if v is not None]
    caption = f"<figcaption>{_e(title)}<span>{_e(subtitle)}</span></figcaption>"
    if not values:
        return (f"<figure>{caption}<p class=\"sub\">データなし(該当する記録のある session がまだ無い)</p></figure>")
    top_value = max(values) or 1.0
    step = _nice_step(top_value / 4)
    y_max = step * math.ceil(top_value / step)
    plot_w, plot_h = width - left - right, height - top - bottom
    n = len(points)

    def x(i: int) -> float:
        return left + (plot_w * (i + 0.5) / n)

    def y(v: float) -> float:
        return top + plot_h - plot_h * (v / y_max if y_max else 0)

    parts = [f'<svg viewBox="0 0 {width} {height}" role="img" aria-label="{_e(title)}">']
    tick = 0.0
    while tick <= y_max + 1e-9:
        yy = y(tick)
        parts.append(f'<line class="grid" x1="{left}" x2="{width - right}" y1="{yy:.1f}" y2="{yy:.1f}"/>')
        parts.append(f'<text class="axis" x="{left - 6}" y="{yy + 3:.1f}" text-anchor="end">{tick:g}</text>')
        tick += step
    previous_label, previous_x = None, -math.inf
    for i, (label, _, _) in enumerate(points):
        # One label per date, and never closer than a label's width to the previous one.
        if label != previous_label and x(i) - previous_x >= 36:
            parts.append(f'<text class="axis" x="{x(i):.1f}" y="{height - bottom + 14}" '
                         f'text-anchor="middle">{_e(label)}</text>')
            previous_x = x(i)
        previous_label = label
    segment: list[str] = []
    paths = []
    for i, (_, _, value) in enumerate(points):
        if value is None:
            if len(segment) > 1:
                paths.append(segment)
            segment = []
            continue
        segment.append(f"{x(i):.1f},{y(value):.1f}")
    if len(segment) > 1:
        paths.append(segment)
    for path in paths:
        parts.append(f'<polyline class="line" points="{" ".join(path)}"/>')
    last_index = None
    for i, (_, tip, value) in enumerate(points):
        if value is None:
            continue
        last_index = i
        # The tooltip sits on the group so the larger hit target shows it too.
        parts.append(f'<g><title>{_e(tip)}: {value:g} {_e(unit)}</title>'
                     f'<circle class="hit" cx="{x(i):.1f}" cy="{y(value):.1f}" r="10"/>'
                     f'<circle class="dot" cx="{x(i):.1f}" cy="{y(value):.1f}" r="4"/></g>')
    if last_index is not None:
        value = points[last_index][2]
        parts.append(f'<text class="end" x="{x(last_index) + 8:.1f}" y="{y(value) + 4:.1f}">{value:g}</text>')
    parts.append("</svg>")
    note = f"古い → 新しい({n} sessions" + (f"、記録の無い古い {skipped} 件は省略" if skipped else "") + "、値なしは線を切る)"
    return f'<figure>{caption}{"".join(parts)}<p class="fnote">{_e(note)}</p></figure>'


def _nice_step(raw: float) -> float:
    if raw <= 0:
        return 1.0
    magnitude = 10 ** math.floor(math.log10(raw))
    for factor in (1, 2, 2.5, 5, 10):
        if raw <= factor * magnitude:
            return factor * magnitude
    return 10 * magnitude


def _tile(key: str, value: str, detail: str = "") -> str:
    return (f'<div class="tile"><div class="k">{_e(key)}</div><div class="v">{_e(value)}</div>'
            f'<div class="d">{_e(detail)}</div></div>')


def render_html(overview: dict) -> str:
    rows, t = overview["sessions"], overview["totals"]
    case = t["caseHints"]
    codex = t["codex"]
    tiles = "".join([
        _tile("セッション", str(t["sessions"]), f"約 {_t(t['durationMinutes'])} 分 / {_t(t['recordedFrames'])} frames"),
        _tile(f"≥{EVENT_JUMP_PX:g}px ジャンプ", _t(t["jumpsAtLeast100px"]),
              f"選択フレーム内、tracking 記録 {t['sessionsWithTracking']} 件"),
        _tile("candidateSwitch", _t(t["candidateSwitch"]), "選択フレーム内"),
        _tile("CASE A / B / C", f"{case.get('A', 0)} / {case.get('B', 0)} / {case.get('C', 0)}",
              "countForTally のみ・hint"),
        _tile("shadow R7e 却下", f"{_t(t['r7eWouldReject'])} / {_t(t['r7eWinners'])}", "winners(適用しない)"),
        _tile("shadow PF22 却下", f"{_t(t['pf22WouldReject'])} / {_t(t['pf22WithVerdict'])}",
              "red winners(適用しない)"),
        _tile("Codex 解析", f"{codex.get('ok', 0)} 完了",
              " / ".join(f"{CODEX_LABELS.get(k, k)} {v}" for k, v in sorted(codex.items()) if k != "ok")),
    ])
    points_jump = [(_when(r)[:5], f"{_when(r)} {r['sessionID']}", r["wholeRecordingSignals"]["endpointJumpPerMinute"])
                   for r in rows]
    points_exposure = [(_when(r)[:5], f"{_when(r)} {r['sessionID']}", r["exposure"]["medianExposureMs"])
                       for r in rows]
    charts = (trend_chart(points_jump, title="ジャンプ / 分",
                          subtitle="録画全体の endpoint_jump シグナル数 ÷ 録画時間(motionEventSummary、09-30 以降)",
                          unit="/分")
              + trend_chart(points_exposure, title="露出時間の中央値 (ms)",
                            subtitle="選択フレームの frames[].camera.exposureDurationSeconds(10-03 以降)",
                            unit="ms"))
    head = "".join(f'<th class="{"l" if i in (0, 13, 14, 15) else ""}">{_e(c)}</th>'
                   for i, c in enumerate(COLUMNS))
    body = []
    for row in reversed(rows):
        sel = row["selectedFrames"]
        report = row["sessionReport"]
        report_cell = (f'<td class="l"><a href="{_e(report["href"])}">要約</a></td>' if report["href"]
                       else '<td class="l na">なし</td>')
        style, icon = CODEX_STYLE.get(row["codex"]["status"], ("st-info", ""))
        codex_text = f"{icon} {row['codex']['label']}"
        if row["codex"]["status"] == "ok" and row["codex"]["detail"]:
            codex_text += f" ({row['codex']['detail']})"
        codex_inner = f'<span class="st {style}">{_e(codex_text)}</span>'
        if row["codex"]["href"]:
            codex_inner = f'<a href="{_e(row["codex"]["href"])}">{codex_inner}</a>'
        codex_title = row["codex"]["detail"] or ""
        experiment = row["exposure"]["experiment"]
        when = (f'<td class="l">{_e(_when(row))}<span class="id">{_e(row["sessionID"])}</span>'
                + (f'<span class="err">{_e(len(row["errors"]))} 件の読み取り注意</span>' if row["errors"] else "")
                + (f'<span class="err" title="{_e(row["inputContract"])}">入力契約 FAIL(CASE は n/a)</span>'
                   if str(row["inputContract"]).startswith("FAIL") else "")
                + "</td>")
        iso = row["exposure"]["medianISO"]
        body.append("<tr>" + "".join([
            when,
            _cell(_duration(row), title=f"{row['fps']} fps" + ("(実測)" if row["fpsMeasured"] else "(仮定)")),
            _cell(_t(row["recordedFrameCount"])),
            _cell(_colors(row)),
            _cell(_exposure(row), title=f"ISO 中央値 {iso:g}" if isinstance(iso, (int, float)) else None),
            _cell(experiment["label"], title=experiment.get("verdict")),
            _cell(_t(sel["jumpsAtLeast100px"])),
            _cell(_t(sel["candidateSwitch"])),
            _cell(_signals(row), title=f"candidate_switch シグナル {_t(row['wholeRecordingSignals']['candidateSwitch'])}"),
            _cell(_case(row), title=json.dumps(row["caseHints"], ensure_ascii=False) if row["caseHints"] else None),
            _cell(_r7e(row), title=json.dumps(row["shadowR7e"], ensure_ascii=False) if row["shadowR7e"] else None),
            _cell(_pf22(row), title=json.dumps(row["shadowPF22"], ensure_ascii=False) if row["shadowPF22"] else None),
            _cell(_t(row["likelyBackgroundClusters"])),
            _cell(row["route"]["label"], left=True, title=row["route"]["from"]),
            f'<td class="l" title="{_e(codex_title)}">{codex_inner}</td>',
            report_cell,
        ]) + "</tr>")
    errors = [(r["sessionID"], e) for r in rows for e in r["errors"]]
    error_html = ("<h2>読み取りの注意</h2><ul class=\"notes\">"
                  + "".join(f"<li>{_e(sid)}: {_e(e)}</li>" for sid, e in errors) + "</ul>") if errors else ""
    return f"""<!doctype html>
<html lang="ja"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<title>PhoneSaber セッション一覧</title><style>{CSS}</style></head>
<body><main>
<h1>PhoneSaber セッション一覧</h1>
<p class="sub">生成 {_e(overview['generatedAt'])} ・ inbox {_e(overview['inbox'])} ・ 読み取りのみ。数値は根拠であり production の閾値ではない。判断は ORIGINAL PNG で行う。</p>
<div class="tiles">{tiles}</div>
<h2>推移</h2>
<div class="charts">{charts}</div>
<h2>セッション(新しい順)</h2>
<div class="scroll"><table><thead><tr>{head}</tr></thead><tbody>{''.join(body)}</tbody></table></div>
<ul class="notes">
<li>≥100px / switch: bundle に入った選択フレーム(event 窓)の中だけを数える(shadow PF22 tally と同じ行)。n/a は tracking 記録の無い古い bundle。</li>
<li>全体 jump: 録画全体の endpoint_jump シグナル数と 1 分あたり(motionEventSummary.signalDistributions)。セルにマウスを置くと candidate_switch シグナル数。</li>
<li>CASE: candidate_selection_audit の hint(countForTally=true の行だけ)。?n は unknown 系。gate には使わない。</li>
<li>R7e / PF22: 却下になる winner 数 / 判定できた winner 数(shadow、applied:false)。PF22 の * は記録なしで再計算した古い bundle。</li>
<li>背景候補: likelyBackground の静的ホットスポット数(hint)。長さの ≈ は 30 fps と仮定した推定。</li>
<li>経路: 受信ログの送信元。127.0.0.1 = P2P 中継(PhoneSaberP2PBridge)、プライベートアドレス = LAN。</li>
<li>要約が「なし」の session は <code>phone_saber_sessions_overview.py --write-missing-reports</code>(または PhoneSaber Overview.command)で作れる。</li>
</ul>
{error_html}
</main></body></html>
"""


# --- output ----------------------------------------------------------------------

def write_overview(inbox: Path, *, output_dir: Path | None = None, logs_dir: Path | None = None,
                   write_missing_reports: bool = False,
                   log: Callable[[str], None] = lambda _: None) -> tuple[Path, Path, dict]:
    output_dir = output_dir or inbox
    overview = build_overview(inbox, logs_dir=logs_dir, output_dir=output_dir,
                              write_missing_reports=write_missing_reports, log=log)
    html_path = output_dir / f"{OVERVIEW_BASENAME}.html"
    md_path = output_dir / f"{OVERVIEW_BASENAME}.md"
    for bundle in find_bundles(inbox):
        for path in (html_path, md_path):
            try:
                if path.resolve().is_relative_to(bundle.resolve()):
                    raise ValueError("the overview must be written outside every bundle")
            except OSError:
                pass
    write_text_atomic(html_path, render_html(overview))
    write_text_atomic(md_path, render_markdown(overview))
    return html_path, md_path, overview


def parse_args(argv: list[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--inbox", type=Path, default=None,
                        help="diagnostics inbox (default: PHONESABER_DIAGNOSTICS_INBOX or "
                             "~/Library/Application Support/PhoneSaber/diagnostics-inbox)")
    parser.add_argument("--output-dir", type=Path, default=None, help="where to write the HTML/MD (default: inbox)")
    parser.add_argument("--logs-dir", type=Path, default=None,
                        help="receiver logs for route / Codex status (default: ~/Library/Logs/PhoneSaber)")
    parser.add_argument("--write-missing-reports", action="store_true",
                        help="write the one-page .report.md beside bundles that have none (never inside a bundle)")
    parser.add_argument("--json", action="store_true", help="also print the overview data as JSON")
    parser.add_argument("--open", action="store_true", help="open the HTML in the default browser (macOS)")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    inbox = (args.inbox or default_inbox()).expanduser()
    if not inbox.is_dir():
        print(f"inbox not found: {inbox}", file=sys.stderr)
        return 2
    started = time.monotonic()
    html_path, md_path, overview = write_overview(
        inbox, output_dir=args.output_dir.expanduser() if args.output_dir else None,
        logs_dir=(args.logs_dir or default_logs_dir()).expanduser(),
        write_missing_reports=args.write_missing_reports, log=print)
    if args.json:
        print(json.dumps(overview, ensure_ascii=False, indent=2))
    t = overview["totals"]
    print(f"overview: {t['sessions']} sessions in {time.monotonic() - started:.1f}s")
    print(f"html: {html_path}")
    print(f"markdown: {md_path}")
    # PHONESABER_OVERVIEW_NO_OPEN lets tests run the launcher without opening a browser.
    if args.open and sys.platform == "darwin" and not os.environ.get("PHONESABER_OVERVIEW_NO_OPEN"):
        subprocess.run(["/usr/bin/open", str(html_path)], check=False)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
