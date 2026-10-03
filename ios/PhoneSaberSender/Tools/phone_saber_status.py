#!/usr/bin/env python3
"""PhoneSaber の Mac 側を 1 回だけ点検し、日本語の OK / WARN / NG で表示する(読み取りのみ)。

縁日当日の確認用。何も起動・停止・変更しない:
- HTTP は 127.0.0.1:8765 の GET /health だけ(phone_saber_triage_receiver.py の既存 endpoint)。
- UDP は送らない。5005/5006 は lsof で「誰が bind しているか」を見るだけ。
- git は fetch しない(最後に fetch した時点の origin と比べる)。GIT_OPTIONAL_LOCKS=0 で index も書かない。
- ファイルは読むだけ(Unity の Editor.log、受信側の latest.log、診断 inbox)。

使い方: python3 phone_saber_status.py   (または PhoneSaber Status.command をダブルクリック)
終了コード: NG があれば 2、WARN だけなら 1、すべて OK なら 0。
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass, field
from datetime import datetime
from pathlib import Path
from typing import Callable, Optional


RECEIVER_PORT = 8765
RED_PORT = 5005
BLUE_PORT = 5006
BRIDGE_BINARY_NAME = "PhoneSaberP2PBridge"  # phone_saber_p2p_bridge.py の BINARY_NAME
INBOX_PREFIX = "phone_saber_triage_"
# 全体の時間予算(秒)。各 subprocess の timeout はこの残り時間で頭打ちにする。
TOTAL_BUDGET_SECONDS = 6.0
COMMAND_TIMEOUT_SECONDS = 2.0
HTTP_TIMEOUT_SECONDS = 1.5
# Editor.log は大きい(数 MB)ので末尾だけ読む。
EDITOR_LOG_TAIL_BYTES = 4 * 1024 * 1024
RECEIVER_LOG_TAIL_BYTES = 2 * 1024 * 1024
# maxGapMs の目安(この tool 独自の目安)。30 fps の本来の間隔は 33 ms。
# P2P_BRIDGE.md の実測では、ラグを感じた時は 170〜300 ms、ときに約 1 秒だった。
GAP_OK_BELOW_MS = 150
GAP_NG_FROM_MS = 500

LEVELS = ("OK", "WARN", "NG", "INFO")


@dataclass
class Check:
    level: str
    title: str
    detail: str = ""
    hints: list[str] = field(default_factory=list)


@dataclass
class CommandResult:
    returncode: int
    stdout: str


Runner = Callable[[list, float], Optional[CommandResult]]
HttpGet = Callable[[str, float], Optional[tuple]]


def _default_runner(args: list, timeout: float) -> Optional[CommandResult]:
    env = dict(os.environ)
    env["GIT_OPTIONAL_LOCKS"] = "0"  # git status が index を書き換えないように
    env["LC_ALL"] = "C"
    try:
        result = subprocess.run(args, capture_output=True, text=True, encoding="utf-8", errors="replace",
                                timeout=max(0.1, timeout), check=False, env=env, stdin=subprocess.DEVNULL)
    except (OSError, subprocess.SubprocessError):
        return None
    return CommandResult(result.returncode, result.stdout)


def _default_http_get(url: str, timeout: float) -> Optional[tuple]:
    try:
        with urllib.request.urlopen(url, timeout=timeout) as response:  # noqa: S310 - localhost only
            return response.status, response.read(4096).decode("utf-8", "replace")
    except urllib.error.HTTPError as exc:
        return exc.code, ""
    except (OSError, ValueError):
        return None


@dataclass
class Context:
    home: Path
    repo_root: Path
    unity_root: Optional[Path]
    inbox: Path
    runner: Runner = _default_runner
    http_get: HttpGet = _default_http_get
    which: Callable[[str], Optional[str]] = shutil.which
    now: Callable[[], float] = time.time
    deadline: float = 0.0

    def run(self, args: list, timeout: float = COMMAND_TIMEOUT_SECONDS) -> Optional[CommandResult]:
        remaining = self.deadline - time.monotonic() if self.deadline else timeout
        if remaining <= 0.05:
            return None
        return self.runner(args, min(timeout, remaining))

    @property
    def editor_log(self) -> Path:
        return self.home / "Library" / "Logs" / "Unity" / "Editor.log"

    @property
    def receiver_log(self) -> Path:
        return self.home / "Library" / "Logs" / "PhoneSaber" / "latest.log"


# ---------------------------------------------------------------- helpers

def read_tail(path: Path, max_bytes: int) -> Optional[str]:
    try:
        with path.open("rb") as handle:
            handle.seek(0, os.SEEK_END)
            size = handle.tell()
            handle.seek(max(0, size - max_bytes))
            data = handle.read()
    except OSError:
        return None
    text = data.decode("utf-8", "replace")
    if size > max_bytes:
        text = text.split("\n", 1)[-1]  # 途中で切れた最初の行を捨てる
    return text


def format_age(seconds: float) -> str:
    seconds = max(0, int(seconds))
    if seconds < 60:
        return f"{seconds} 秒前"
    if seconds < 3600:
        return f"{seconds // 60} 分前"
    if seconds < 86400:
        return f"{seconds // 3600} 時間前"
    return f"{seconds // 86400} 日前"


def file_age(ctx: Context, path: Path) -> Optional[float]:
    try:
        return ctx.now() - path.stat().st_mtime
    except OSError:
        return None


def parse_etime(value: str) -> Optional[int]:
    """ps -o etime= の [[dd-]hh:]mm:ss を秒にする。"""
    value = value.strip()
    match = re.fullmatch(r"(?:(\d+)-)?(?:(\d+):)?(\d+):(\d+)", value)
    if not match:
        return None
    days, hours, minutes, seconds = (int(part) if part else 0 for part in match.groups())
    return ((days * 24 + hours) * 60 + minutes) * 60 + seconds


def parse_lsof(stdout: str) -> list[dict]:
    """lsof -nP の表を {command, pid, name} の list にする(header 行は捨てる)。"""
    rows = []
    for line in stdout.splitlines():
        parts = line.split()
        if len(parts) < 9 or parts[0] == "COMMAND" or not parts[1].isdigit():
            continue
        name = " ".join(parts[8:])
        rows.append({"command": parts[0], "pid": int(parts[1]), "name": name})
    return rows


# ---------------------------------------------------------------- checks

def check_receiver(ctx: Context) -> list[Check]:
    url = f"http://127.0.0.1:{RECEIVER_PORT}/health"
    response = ctx.http_get(url, HTTP_TIMEOUT_SECONDS)
    listeners = []
    lsof = ctx.run(["lsof", "-nP", f"-iTCP:{RECEIVER_PORT}", "-sTCP:LISTEN"])
    if lsof is not None:
        listeners = parse_lsof(lsof.stdout)
    if response is not None and response[0] == 200:
        try:
            payload = json.loads(response[1])
        except ValueError:
            payload = {}
        if payload.get("service") == "phonesaber-triage" and payload.get("status") == "ready":
            checks = [Check("OK", "受信側(Start PhoneSaber)", f"起動中: {url} = ready")]
            checks.extend(_receiver_age(ctx, listeners))
            return checks
        return [Check("NG", "受信側(Start PhoneSaber)",
                      f"TCP {RECEIVER_PORT} に PhoneSaber 以外のサーバーがいる: {response[1][:80]!r}",
                      ["その process を止めてから、デスクトップの Start PhoneSaber を起動する"])]
    if listeners:
        owners = ", ".join(f"{row['command']}(pid {row['pid']})" for row in listeners)
        return [Check("NG", "受信側(Start PhoneSaber)",
                      f"TCP {RECEIVER_PORT} は {owners} が使用中だが /health に応答しない",
                      ["Start PhoneSaber のウィンドウを確認する。固まっていれば Ctrl+C で止めて起動し直す"])]
    return [Check("NG", "受信側(Start PhoneSaber)", f"起動していない(TCP {RECEIVER_PORT} に応答なし)",
                  ["デスクトップの Start PhoneSaber をダブルクリック(診断 bundle の受信と Codex 解析に必要。"
                   "ゲームの座標には不要)"])]


def _receiver_age(ctx: Context, listeners: list[dict]) -> list[Check]:
    """受信側の起動後に repo が更新されたら、古いコードで動いていると知らせる。"""
    if not listeners:
        return []
    ps = ctx.run(["ps", "-o", "etime=", "-p", str(listeners[0]["pid"])])
    head = ctx.run(["git", "-C", str(ctx.repo_root), "log", "-1", "--format=%ct"])
    if ps is None or head is None:
        return []
    elapsed = parse_etime(ps.stdout)
    try:
        commit_time = int(head.stdout.strip())
    except ValueError:
        return []
    if elapsed is None:
        return []
    started = ctx.now() - elapsed
    if commit_time > started + 60:
        return [Check("WARN", "受信側のコード",
                      f"受信側は {format_age(elapsed)}に起動。その後に repo の HEAD が更新された",
                      ["新しいコードを使うには Start PhoneSaber を Ctrl+C で止めて起動し直す(解析中なら終わってから)"])]
    return []


def check_unity_ports(ctx: Context) -> list[Check]:
    unity = ctx.run(["pgrep", "-x", "Unity"])
    unity_running = unity is not None and unity.returncode == 0 and unity.stdout.strip() != ""
    lsof = ctx.run(["lsof", "-nP", f"-iUDP:{RED_PORT}", f"-iUDP:{BLUE_PORT}"])
    if lsof is None:
        return [Check("WARN", "Unity 受信 UDP 5005/5006", "lsof を実行できず確認できない")]
    rows = parse_lsof(lsof.stdout)
    checks = []
    for port, color in ((RED_PORT, "RED"), (BLUE_PORT, "BLUE")):
        owners = [row for row in rows if re.search(rf":{port}\b", row["name"])]
        unity_owners = [row for row in owners if row["command"].startswith("Unity")]
        others = [row for row in owners if not row["command"].startswith("Unity")]
        title = f"Unity 受信 UDP {port}({color})"
        if unity_owners and not others:
            checks.append(Check("OK", title, f"Unity(pid {unity_owners[0]['pid']})が受信中"))
        elif others:
            names = ", ".join(f"{row['command']}(pid {row['pid']})" for row in others)
            checks.append(Check("NG", title, f"Unity 以外が使用中: {names}",
                                ["port の取り合い。その process を止め、Unity の Play を押し直す"
                                 "(Unity は 5005/5006 の両方を bind できるまで P2P bridge を起動しない)"]))
        elif unity_running:
            checks.append(Check("NG", title, "Unity は起動しているが受信していない(Play 前?)",
                                ["Unity で Play を押す。Play 中なら Console の "
                                 f"`[PhoneSaber][{color}] receiver failed` を確認する"]))
        else:
            checks.append(Check("NG", title, "Unity が起動していない",
                                ["Unity Hub から 3D-Saber を開いて Play を押す"]))
    return checks


def check_bridge_process(ctx: Context) -> list[Check]:
    result = ctx.run(["pgrep", "-fl", BRIDGE_BINARY_NAME])
    if result is None:
        return [Check("WARN", "P2P bridge", "pgrep を実行できず確認できない")]
    lines = [line for line in result.stdout.splitlines() if line.strip()]
    building = [line for line in lines if "swiftc" in line or "swift-frontend" in line]
    running = [line for line in lines if line not in building]
    if running:
        pid = running[0].split()[0]
        return [Check("OK", "P2P bridge", f"動作中(pid {pid}。Unity の Play で自動起動)")]
    if building:
        return [Check("WARN", "P2P bridge", "初回 build 中(数秒〜十数秒で起動する)")]
    return [Check("WARN", "P2P bridge", "動いていない(iPhone は LAN で送るので、同じ Wi-Fi でないと届かない)",
                  ["Unity で Play を押すと自動で起動する。Console の `[PhoneSaber][P2P]` の警告を確認する",
                   "環境変数 PHONESABER_P2P_BRIDGE=0 で自動起動を止めていないか確認する"])]


_STATS = re.compile(r"\[PhoneSaber\]\[P2P\] last (\d+)s: (.*)$")
_EVENTS = (
    ("peer alive", "OK", "iPhone から ping が再開(P2P を使える)"),
    ("peer connected", "OK", "iPhone が接続"),
    ("no ping for 3s", "WARN", "iPhone から ping が 3 秒ない(iPhone は LAN に戻る)"),
    ("peer idle", "WARN", "iPhone から 10 秒以上何も来ず、接続を閉じた"),
    ("peer closed", "WARN", "iPhone との接続が閉じた"),
)
_WARNINGS = (
    "bridge launcher not found", "bridge start failed", "bridge exited (code", "[P2P] listener failed",
    "[P2P] cannot", "diag relay: cannot", "bridge build failed", "receiver failed",
)


def parse_stats(text: str) -> dict:
    """`RED=313 BLUE=313 maxGapMsRED=121 ... peers=1` を dict にする。"""
    values: dict = {}
    for key, value in re.findall(r"(\w+)=(-?\d+)", text):
        values[key] = int(value)
    return values


def check_editor_log(ctx: Context, bridge_running: bool) -> list[Check]:
    path = ctx.editor_log
    text = read_tail(path, EDITOR_LOG_TAIL_BYTES)
    if text is None:
        return [Check("INFO", "Unity Console(Editor.log)", f"読めない: {path}")]
    age = file_age(ctx, path)
    age_text = f"Editor.log 最終更新 {format_age(age)}" if age is not None else "Editor.log"
    lines = text.splitlines()
    start = 0
    for index in range(len(lines) - 1, -1, -1):
        if "[PhoneSaber][P2P] bridge starting" in lines[index]:
            start = index
            break
    session = lines[start:]
    stale_note = "" if bridge_running else "(bridge は今は動いていないので過去の記録)"
    checks: list[Check] = []

    stats = [(m.group(1), m.group(2)) for m in (_STATS.search(line) for line in session) if m]
    if stats:
        _, last = stats[-1]
        values = parse_stats(last)
        gaps = [values[k] for k in ("maxGapMsRED", "maxGapMsBLUE") if k in values]
        recent = [max([v for k, v in parse_stats(body).items() if k.startswith("maxGapMs")] or [0])
                  for _, body in stats[-5:]]
        detail = (f"直近: RED={values.get('RED', 0)} BLUE={values.get('BLUE', 0)} "
                  f"maxGapMs RED={values.get('maxGapMsRED', '-')} BLUE={values.get('maxGapMsBLUE', '-')} "
                  f"peers={values.get('peers', '-')} / 直近{len(recent)}回の最大間隔(ms): "
                  f"{', '.join(str(v) for v in recent)} / {age_text}{stale_note}")
        worst = max(gaps) if gaps else 0
        hints = []
        if worst >= GAP_NG_FROM_MS:
            level = "NG" if bridge_running else "INFO"
            hints.append("座標が 0.5 秒以上止まった。AWDL の詰まり(P2P_BRIDGE.md「遅延について」)。"
                         "続くなら iPhone を Mac と同じ Wi-Fi に入れて「P2P優先」を OFF")
        elif worst >= GAP_OK_BELOW_MS:
            level = "WARN" if bridge_running else "INFO"
            hints.append("30 fps の本来の間隔は 33 ms。ラグを感じるなら上と同じ対処")
        else:
            level = "OK" if bridge_running else "INFO"
        for key in ("stale", "malformed", "forwardFailed"):
            if values.get(key):
                hints.append(f"{key}={values[key]}(forwardFailed は Unity 側の受信が無いときに増える)")
        checks.append(Check(level, "P2P 集計(10 秒ごと)", detail, hints))
    else:
        checks.append(Check("INFO", "P2P 集計(10 秒ごと)",
                            f"まだ無い(iPhone から P2P で座標が届くと出る) / {age_text}{stale_note}"))

    last_event = None
    for line in session:
        for needle, level, meaning in _EVENTS:
            if needle in line:
                last_event = (level, meaning, line.strip())
    if last_event is not None:
        level, meaning, line = last_event
        if not bridge_running:
            level = "INFO"
        checks.append(Check(level, "P2P 最後の接続イベント", f"{meaning}: {line[:160]}"))

    warnings = [line.strip() for line in session if any(needle in line for needle in _WARNINGS)]
    if warnings:
        checks.append(Check("WARN", "Unity Console の警告(直近の Play 以降)",
                            f"{len(warnings)} 件。最後: {warnings[-1][:200]}",
                            ["bridge が止まっても LAN 受信は動く。直すには Unity で script を再 compile するか Editor を再起動"]))
    relay = [line.strip() for line in session if "diag relay: upload from" in line and "closed after" in line]
    if relay:
        checks.append(Check("INFO", "診断 relay(P2P 経由の bundle 転送)", relay[-1][:200]))
    return checks


def latest_bundle(inbox: Path) -> Optional[Path]:
    try:
        bundles = [p for p in inbox.iterdir() if p.is_dir() and p.name.startswith(INBOX_PREFIX)]
    except OSError:
        return None
    if not bundles:
        return None
    return max(bundles, key=lambda p: p.stat().st_mtime)


RECEIVER_LOGS_TO_SEARCH = 5


def _last_analysis_log(ctx: Context, session_id: str) -> Optional[str]:
    """受信側のログ(latest.log と、新しい順に数個の triage-*.log)から、その session の最後の解析結果行を探す。
    受信側を起動し直すと latest.log は新しいファイルを指すので、前の run のログも見る。"""
    log_dir = ctx.receiver_log.parent
    try:
        runs = sorted(log_dir.glob("triage-*.log"), key=lambda p: p.stat().st_mtime, reverse=True)
    except OSError:
        runs = []
    paths = [ctx.receiver_log] + [p for p in runs[:RECEIVER_LOGS_TO_SEARCH]]
    seen: set = set()
    for path in paths:
        try:
            key = path.resolve()
        except OSError:
            continue
        if key in seen:
            continue
        seen.add(key)
        text = read_tail(path, RECEIVER_LOG_TAIL_BYTES)
        if text is None:
            continue
        found = None
        for line in text.splitlines():
            if f"sessionID={session_id}" in line and "[AUTO_REPAIR][ANALYSIS]" in line:
                found = line.strip()
        if found:
            return found
    return None


def check_inbox(ctx: Context) -> list[Check]:
    bundle = latest_bundle(ctx.inbox)
    if bundle is None:
        return [Check("INFO", "診断 bundle", f"まだ無い: {ctx.inbox}")]
    session_id = bundle.name[len(INBOX_PREFIX):]
    age = file_age(ctx, bundle)
    header = f"最新 {bundle.name}({format_age(age) if age is not None else '?'})"
    checks: list[Check] = []
    report_md = ctx.inbox / f"{bundle.name}.report.md"
    if report_md.is_file():
        checks.append(Check("OK", "診断 bundle", f"{header} / 1ページ要約あり: {report_md.name}"))
    else:
        checks.append(Check("WARN", "診断 bundle", f"{header} / 1ページ要約(.report.md)が無い",
                            [f"手動で作る: python3 phone_saber_session_report.py \"{bundle}\""]))

    analysis_json = bundle / "analysis_report.json"
    if analysis_json.is_file():
        try:
            report = json.loads(analysis_json.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            report = {}
        precheck = report.get("precheck") if isinstance(report.get("precheck"), dict) else {}
        codes = precheck.get("reasonCodes") or []
        if report.get("analysisExecuted"):
            checks.append(Check("OK", "Codex 解析", "完了(analysis_report.md を開く)"))
        elif codes:
            checks.append(Check("WARN", "Codex 解析", f"precheck で中止: {', '.join(codes)[:200]}",
                                ["選ばれた画像に時間方向の証拠が足りない(短すぎる録画など)。"
                                 "saber を映して数秒以上振る録画を撮り直す"]))
        else:
            checks.append(Check("INFO", "Codex 解析", "analysis_report.json あり(Codex は実行されていない)"))
        return checks

    log_line = _last_analysis_log(ctx, session_id)
    if log_line and "result=starting" in log_line:
        checks.append(Check("INFO", "Codex 解析", "実行中(最大 25 分、timeout なら high で 1 回だけ再試行)"))
    elif log_line and "result=FAIL" in log_line:
        reason = log_line.split("result=FAIL", 1)[1].strip()
        hints = [f"手動で再実行: python3 phone_saber_triage_codex.py \"{bundle}\""]
        if "CLI_TIMEOUT" in reason:
            hints.insert(0, "Codex の timeout。bundle は残っているので、空いた時間に手動で再実行する")
        checks.append(Check("WARN", "Codex 解析", f"失敗: {reason[:160]}", hints))
    else:
        checks.append(Check("INFO", "Codex 解析", "結果なし(受信側の latest.log に記録が無い。--no-codex で起動した可能性)",
                            [f"必要なら手動で: python3 phone_saber_triage_codex.py \"{bundle}\""]))
    return checks


def git_state(ctx: Context, label: str, root: Optional[Path]) -> Check:
    if root is None or not (root / ".git").exists():
        return Check("INFO", f"git {label}", f"見つからない: {root}")
    base = ["git", "-C", str(root)]
    branch = ctx.run(base + ["rev-parse", "--abbrev-ref", "HEAD"])
    status = ctx.run(base + ["status", "--porcelain", "--untracked-files=no"])
    counts = ctx.run(base + ["rev-list", "--left-right", "--count", "HEAD...@{upstream}"])
    compared = "upstream"
    if counts is None or counts.returncode != 0:
        counts = ctx.run(base + ["rev-list", "--left-right", "--count", "HEAD...origin/main"])
        compared = "origin/main"
    if branch is None or branch.returncode != 0:
        return Check("WARN", f"git {label}", f"git を実行できない: {root}")
    name = branch.stdout.strip()
    dirty = len([line for line in (status.stdout if status else "").splitlines() if line.strip()])
    ahead = behind = None
    if counts is not None and counts.returncode == 0:
        parts = counts.stdout.split()
        if len(parts) == 2 and all(part.isdigit() for part in parts):
            ahead, behind = int(parts[0]), int(parts[1])
    fetch_note = ""
    common = ctx.run(base + ["rev-parse", "--path-format=absolute", "--git-common-dir"])
    if common is not None and common.returncode == 0:
        fetch_age = file_age(ctx, Path(common.stdout.strip()) / "FETCH_HEAD")
        if fetch_age is not None:
            fetch_note = f"、最終 fetch {format_age(fetch_age)}"
    distance = "比較先なし" if ahead is None else f"{compared} と比べて {ahead} ahead / {behind} behind"
    detail = f"{name}、未commit {dirty} 件、{distance}{fetch_note}({root})"
    hints = []
    if behind:
        hints.append("origin に新しい commit がある。本番前に pull するか判断する(当日は不用意に更新しない)")
    if dirty:
        hints.append("未commit の変更がある(消さないこと)")
    level = "OK" if name == "main" and not dirty and ahead == 0 and behind == 0 else "WARN"
    return Check(level, f"git {label}", detail, hints)


def find_codex(ctx: Context) -> Optional[str]:
    """phone_saber_triage_codex.find_codex_binary と同じ探し方。"""
    found = ctx.which("codex")
    if found:
        return found
    app_relative = Path("Contents/Resources/codex-cli/CodexCLI.app/Contents/MacOS/codex")
    for path in (Path("/Applications/ChatGPT.app") / app_relative,
                 ctx.home / "Applications/ChatGPT.app" / app_relative,
                 Path("/opt/homebrew/bin/codex"), Path("/usr/local/bin/codex")):
        if path.is_file() and os.access(path, os.X_OK):
            return str(path)
    return None


def check_codex(ctx: Context) -> list[Check]:
    path = find_codex(ctx)
    if path is None:
        return [Check("WARN", "Codex CLI", "見つからない(bundle の受信と1ページ要約は動く。Codex 解析だけ動かない)")]
    version = ctx.run([path, "--version"], timeout=3.0)
    text = version.stdout.strip().splitlines()[0] if version and version.returncode == 0 and version.stdout.strip() else "版不明"
    return [Check("OK", "Codex CLI", f"{text}({path})")]


# ---------------------------------------------------------------- main

def resolve_repo_root() -> Path:
    return Path(__file__).resolve().parents[3]


def resolve_unity_root(ctx_runner: Runner, repo_root: Path) -> Optional[Path]:
    configured = os.environ.get("PHONESABER_UNITY_PROJECT")
    if configured:
        return Path(configured).expanduser()
    candidates = [repo_root.parent / "3D-Saber"]
    # worktree(.claude/worktrees/...)から実行したときは、本体 checkout の隣を見る。
    common = ctx_runner(["git", "-C", str(repo_root), "rev-parse", "--path-format=absolute", "--git-common-dir"],
                        COMMAND_TIMEOUT_SECONDS)
    if common is not None and common.returncode == 0 and common.stdout.strip():
        candidates.append(Path(common.stdout.strip()).parent.parent / "3D-Saber")
    for candidate in candidates:
        if (candidate / "Assets").is_dir():
            return candidate
    return candidates[0]


def collect(ctx: Context) -> list[Check]:
    checks: list[Check] = []
    checks.extend(check_receiver(ctx))
    checks.extend(check_unity_ports(ctx))
    bridge = check_bridge_process(ctx)
    checks.extend(bridge)
    checks.extend(check_editor_log(ctx, bridge_running=bridge[0].level == "OK"))
    checks.extend(check_inbox(ctx))
    checks.append(git_state(ctx, "school-festival", ctx.repo_root))
    checks.append(git_state(ctx, "3D-Saber", ctx.unity_root))
    checks.extend(check_codex(ctx))
    return checks


def render(checks: list[Check], now: float) -> str:
    stamp = datetime.fromtimestamp(now).strftime("%Y-%m-%d %H:%M:%S")
    lines = [f"PhoneSaber 状態チェック {stamp}(読み取りのみ。何も変更しません)", ""]
    for check in checks:
        tag = {"INFO": "----"}.get(check.level, check.level)
        lines.append(f"[{tag:<4}] {check.title}: {check.detail}")
        for hint in check.hints:
            lines.append(f"         → {hint}")
    counts = {level: sum(1 for c in checks if c.level == level) for level in LEVELS}
    lines.append("")
    lines.append(f"まとめ: NG {counts['NG']} / WARN {counts['WARN']} / OK {counts['OK']}"
                 "(詳しい対処は docs/claude/EVENT_DAY_RUNBOOK.md)")
    return "\n".join(lines)


def exit_code(checks: list[Check]) -> int:
    if any(c.level == "NG" for c in checks):
        return 2
    if any(c.level == "WARN" for c in checks):
        return 1
    return 0


def main(argv: Optional[list] = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--inbox", type=Path, default=None, help="診断 inbox(既定: PHONESABER_DIAGNOSTICS_INBOX "
                        "または ~/Library/Application Support/PhoneSaber/diagnostics-inbox)")
    args = parser.parse_args(argv)
    home = Path.home()
    inbox = args.inbox or Path(os.environ.get(
        "PHONESABER_DIAGNOSTICS_INBOX",
        str(home / "Library" / "Application Support" / "PhoneSaber" / "diagnostics-inbox")))
    repo_root = resolve_repo_root()
    ctx = Context(home=home, repo_root=repo_root, unity_root=resolve_unity_root(_default_runner, repo_root),
                  inbox=inbox.expanduser(), deadline=time.monotonic() + TOTAL_BUDGET_SECONDS)
    checks = collect(ctx)
    print(render(checks, ctx.now()))
    return exit_code(checks)


if __name__ == "__main__":
    sys.exit(main())
