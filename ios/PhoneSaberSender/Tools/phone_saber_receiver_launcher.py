#!/usr/bin/env python3
"""Start the PhoneSaber triage receiver with status and session logging."""

from __future__ import annotations

import argparse
import errno
import fcntl
import json
import os
import re
import shutil
import signal
import socket
import subprocess
import sys
import time
import urllib.error
import urllib.request
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import IO, Sequence


TOOLS_DIR = Path(__file__).resolve().parent
RECEIVER_SCRIPT = TOOLS_DIR / "phone_saber_triage_receiver.py"
LOG_DIR = Path.home() / "Library" / "Logs" / "PhoneSaber"
DEFAULT_PORT = 8765
LOG_RETENTION_COUNT = 50
LOCK_FILE_NAME = ".launcher.lock"
HEALTH_TIMEOUT_SECONDS = 1.5
# How long a restart waits for the previous launcher to release its lock.
RESTART_WAIT_SECONDS = 20.0
NO_RESTART_ENV = "PHONESABER_NO_AUTO_RESTART"


class LauncherError(RuntimeError):
    """An error that should be shown clearly in the Terminal window."""


class StopRequested(Exception):
    def __init__(self, signum: int) -> None:
        super().__init__(signum)
        self.signum = signum


@dataclass(frozen=True)
class GitSnapshot:
    repo_root: Path
    branch: str
    status_short: str
    dirty: bool
    ahead: int | None
    behind: int | None

    @property
    def warnings(self) -> list[str]:
        result: list[str] = []
        if self.branch != "main":
            result.append(f"branch is {self.branch or '(detached HEAD)'}, not main")
        if self.dirty:
            result.append("working tree has uncommitted changes")
        if self.ahead is None or self.behind is None:
            result.append("could not compare HEAD with origin/main")
        elif self.ahead != 0 or self.behind != 0:
            result.append(f"HEAD differs from origin/main ({self.ahead} ahead / {self.behind} behind)")
        return result

    @property
    def git_summary(self) -> str:
        working_tree = "dirty" if self.dirty else "clean"
        ahead = "?" if self.ahead is None else str(self.ahead)
        behind = "?" if self.behind is None else str(self.behind)
        return f"{working_tree} / {ahead} ahead / {behind} behind"


@dataclass(frozen=True)
class ExistingListener:
    pids: tuple[int, ...]
    commands: tuple[str, ...]
    port: int

    @property
    def is_phone_saber_receiver(self) -> bool:
        return any("phone_saber_triage_receiver.py" in command for command in self.commands)


def resolve_repository_root(helper_path: Path = Path(__file__)) -> Path:
    """Resolve this helper's repository without relying on the caller's cwd."""
    resolved_helper = helper_path.resolve()
    if len(resolved_helper.parents) < 4:
        raise LauncherError(f"cannot resolve repository from launcher path: {resolved_helper}")
    expected_root = resolved_helper.parents[3]
    if not (expected_root / ".git").exists():
        raise LauncherError(
            f"repository not found at expected path: {expected_root}\n"
            "Keep this launcher inside ios/PhoneSaberSender/Tools in the school-festival repository."
        )
    try:
        result = subprocess.run(
            ["git", "-C", str(expected_root), "rev-parse", "--show-toplevel"],
            check=False, capture_output=True, text=True, encoding="utf-8", errors="replace",
        )
    except OSError as exc:
        raise LauncherError(f"cannot run git while checking repository {expected_root}: {exc}") from exc
    if result.returncode != 0:
        detail = result.stderr.strip() or "git could not identify the repository root"
        raise LauncherError(f"repository check failed for {expected_root}: {detail}")
    actual_root = Path(result.stdout.strip()).resolve()
    if actual_root != expected_root.resolve():
        raise LauncherError(
            f"launcher resolved to {expected_root}, but Git reports a different repository: {actual_root}"
        )
    return actual_root


def _git_output(repo_root: Path, *args: str, required: bool = True) -> str:
    try:
        result = subprocess.run(
            ["git", "-C", str(repo_root), *args],
            check=False, capture_output=True, text=True, encoding="utf-8", errors="replace",
        )
    except OSError as exc:
        if required:
            raise LauncherError(f"cannot run git {' '.join(args)}: {exc}") from exc
        return ""
    if result.returncode != 0:
        if required:
            detail = result.stderr.strip() or f"git {' '.join(args)} failed"
            raise LauncherError(detail)
        return ""
    return result.stdout.strip()


def collect_git_snapshot(repo_root: Path) -> GitSnapshot:
    branch = _git_output(repo_root, "branch", "--show-current")
    status_short = _git_output(repo_root, "status", "-sb")
    porcelain = _git_output(repo_root, "status", "--porcelain", "--untracked-files=all")
    distance = _git_output(
        repo_root, "rev-list", "--left-right", "--count", "HEAD...origin/main", required=False,
    ).split()
    ahead: int | None = None
    behind: int | None = None
    if len(distance) == 2:
        try:
            ahead, behind = int(distance[0]), int(distance[1])
        except ValueError:
            pass
    return GitSnapshot(
        repo_root=repo_root,
        branch=branch,
        status_short=status_short or "(no Git status output)",
        dirty=bool(porcelain),
        ahead=ahead,
        behind=behind,
    )


def _stamp() -> str:
    return datetime.now().strftime("[%Y-%m-%d %H:%M:%S] ")


def _emit(message: str, log_file: IO[str] | None = None, *, stamp: bool = True) -> None:
    """Terminal gets the plain line; the saved log gets a timestamp so events can be dated."""
    print(message, flush=True)
    if log_file is not None:
        log_file.write((_stamp() if stamp else "") + message + "\n")
        log_file.flush()


def _emit_banner(
    snapshot: GitSnapshot,
    log_path: Path | None,
    receiver_status: str,
    log_file: IO[str] | None = None,
) -> None:
    lines = [
        "========================================",
        " PhoneSaber Receiver",
        "========================================",
        f"Repo:           {snapshot.repo_root}",
        f"Branch:         {snapshot.branch or '(detached HEAD)'}",
        f"Git:            {snapshot.git_summary}",
        "Git status:",
        *[f"  {line}" for line in snapshot.status_short.splitlines()],
        f"Log:            {log_path if log_path is not None else '(not created in dry run)'}",
        f"Receiver status: {receiver_status}",
    ]
    lines.extend(f"WARNING: {warning}" for warning in snapshot.warnings)
    if snapshot.warnings:
        lines.append("The receiver will continue; the existing auto-repair safety gate blocks edits unless main is clean and synchronized.")
    lines.append("========================================")
    for line in lines:
        _emit(line, log_file, stamp=False)


def _read_lock_metadata(lock_file: IO[str]) -> dict[str, str]:
    try:
        lock_file.seek(0)
        value = json.load(lock_file)
    except (OSError, json.JSONDecodeError):
        return {}
    return value if isinstance(value, dict) else {}


def _acquire_single_instance_lock(log_dir: Path) -> tuple[IO[str] | None, dict[str, str]]:
    lock_path = log_dir / LOCK_FILE_NAME
    lock_file = lock_path.open("a+", encoding="utf-8")
    try:
        fcntl.flock(lock_file.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
    except OSError as exc:
        if exc.errno not in {errno.EACCES, errno.EAGAIN}:
            lock_file.close()
            raise
        metadata = _read_lock_metadata(lock_file)
        lock_file.close()
        return None, metadata
    return lock_file, {}


def _write_lock_metadata(lock_file: IO[str], *, pid: int, repo_root: Path, log_path: Path) -> None:
    lock_file.seek(0)
    lock_file.truncate()
    json.dump({"pid": str(pid), "repo": str(repo_root), "log": str(log_path)}, lock_file)
    lock_file.flush()


def _process_command(pid: int) -> str:
    ps = shutil.which("ps")
    if ps is None:
        return ""
    result = subprocess.run(
        [ps, "-p", str(pid), "-o", "command="],
        check=False, capture_output=True, text=True, encoding="utf-8", errors="replace",
    )
    return result.stdout.strip()


def find_existing_receiver(port: int = DEFAULT_PORT) -> ExistingListener | None:
    """Detect an existing listener on the receiver's TCP port."""
    lsof = shutil.which("lsof")
    if lsof is not None:
        result = subprocess.run(
            [lsof, "-nP", f"-iTCP:{port}", "-sTCP:LISTEN", "-t"],
            check=False, capture_output=True, text=True, encoding="utf-8", errors="replace",
        )
        pids: list[int] = []
        for value in result.stdout.split():
            try:
                pid = int(value)
            except ValueError:
                continue
            if pid not in pids:
                pids.append(pid)
        if pids:
            return ExistingListener(
                pids=tuple(pids), commands=tuple(_process_command(pid) for pid in pids), port=port,
            )

    probe = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        probe.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
        probe.bind(("0.0.0.0", port))
    except OSError as exc:
        if exc.errno == errno.EADDRINUSE:
            return ExistingListener(pids=(), commands=(), port=port)
        raise LauncherError(f"cannot verify whether TCP port {port} is available: {exc}") from exc
    finally:
        probe.close()
    return None


def _next_log_path(log_dir: Path) -> Path:
    while True:
        timestamp = datetime.now().strftime("%Y%m%d-%H%M%S")
        path = log_dir / f"triage-{timestamp}.log"
        try:
            with path.open("x", encoding="utf-8"):
                pass
            return path
        except FileExistsError:
            time.sleep(0.1)


def _update_latest_link(log_path: Path) -> None:
    latest = log_path.parent / "latest.log"
    temporary = log_path.parent / f".latest.log.{os.getpid()}.{time.monotonic_ns()}.tmp"
    try:
        temporary.symlink_to(log_path.name)
        os.replace(temporary, latest)
    finally:
        try:
            temporary.unlink()
        except FileNotFoundError:
            pass


def _rotate_logs(log_dir: Path, keep: int = LOG_RETENTION_COUNT) -> None:
    logs = [
        path for path in log_dir.glob("triage-*.log")
        if not path.is_symlink() and path.is_file()
    ]
    logs.sort(key=lambda path: path.stat().st_mtime_ns, reverse=True)
    for old_log in logs[keep:]:
        old_log.unlink(missing_ok=True)


def _announce_duplicate(
    *,
    log_dir: Path,
    metadata: dict[str, str] | None = None,
    listener: ExistingListener | None = None,
) -> None:
    metadata = metadata or {}
    if listener is not None and listener.is_phone_saber_receiver:
        _emit("PhoneSaber receiver is already running.")
    elif listener is not None and listener.pids:
        _emit(f"TCP port {listener.port} is already in use; refusing to start another receiver.")
    elif listener is not None:
        _emit(f"TCP port {listener.port} is already in use; refusing to start another receiver.")
    else:
        _emit("PhoneSaber receiver is already running or starting.")
    if metadata.get("pid"):
        _emit(f"Existing receiver PID: {metadata['pid']}")
    if metadata.get("log"):
        _emit(f"Existing log: {metadata['log']}")
    elif listener is not None:
        for pid, command in zip(listener.pids, listener.commands):
            _emit(f"Listener PID {pid}: {command or '(command unavailable)'}")
        latest = log_dir / "latest.log"
        if latest.exists():
            _emit(f"Latest launcher log (owner not confirmed): {latest}")


def fetch_receiver_health(port: int = DEFAULT_PORT,
                          timeout: float = HEALTH_TIMEOUT_SECONDS) -> dict | None:
    """GET /health of a receiver on this Mac; None when it is not a PhoneSaber receiver."""
    try:
        with urllib.request.urlopen(f"http://127.0.0.1:{port}/health", timeout=timeout) as response:  # noqa: S310
            if response.status != 200:
                return None
            payload = json.loads(response.read(65536).decode("utf-8", "replace"))
    except (OSError, ValueError, urllib.error.URLError):
        return None
    if not isinstance(payload, dict) or payload.get("service") != "phonesaber-triage":
        return None
    return payload


@dataclass(frozen=True)
class RestartDecision:
    restart: bool
    pid: int | None
    lines: tuple[str, ...]


MANUAL_RESTART = ("新しいコードを使うには、受信側の Terminal で Ctrl+C で止めてから Start PhoneSaber を"
                  "起動し直す(解析中なら終わってから)")


def assess_running_receiver(health: dict | None, metadata: dict[str, str], repo_root: Path, *,
                            allow_restart: bool = True) -> RestartDecision:
    """Decide whether an already running receiver is stale and safe to replace.

    A restart is chosen only when every condition holds: the receiver reports that
    its loaded Tools code changed on disk, this launcher family started it (the lock
    names the same pid and repository), and it is idle (no analysis running or
    queued, no upload being received). Anything else only prints what to do.
    """
    if health is None:
        return RestartDecision(False, None, (
            "受信側の /health に応答がないので、コードが最新かは確認できない。",))
    if "codeChangedSinceStart" not in health:
        return RestartDecision(False, None, (
            "WARNING: 受信側は、コード変更の検出より前の版で動いている(古いコード)。",
            MANUAL_RESTART))
    started = health.get("startedAt", "?")
    if not health.get("codeChangedSinceStart"):
        return RestartDecision(False, None, (f"受信側のコードは最新(起動 {started})。",))
    changed = ", ".join(str(name) for name in health.get("changedFiles") or []) or "?"
    lines = [f"WARNING: 受信側は古いコードで動いている(起動 {started}。その後に変更: {changed})。"]
    try:
        pid = int(health.get("pid"))
    except (TypeError, ValueError):
        pid = None
    owned = pid is not None and metadata.get("pid") == str(pid)
    if owned:
        try:
            owned = Path(metadata.get("repo", "")).resolve() == repo_root.resolve()
        except OSError:
            owned = False
    if not allow_restart:
        return RestartDecision(False, pid, (*lines, "自動再起動は無効(--no-restart / "
                                            f"{NO_RESTART_ENV}=1)。", MANUAL_RESTART))
    if not owned:
        return RestartDecision(False, pid, (*lines, "Start PhoneSaber が起動した受信側ではないので、自動では止めない。",
                                            MANUAL_RESTART))
    analysis = health.get("analysis") if isinstance(health.get("analysis"), dict) else {}
    if not health.get("idle"):
        busy = (f"解析中={analysis.get('running') or 'なし'}、待ち={analysis.get('queued', '?')}、"
                f"受信中={health.get('uploadsInProgress', '?')}")
        return RestartDecision(False, pid, (*lines, f"今は作業中({busy})なので再起動しない。"
                                            "終わってから Start PhoneSaber をもう一度ダブルクリックする。"))
    return RestartDecision(True, pid, (*lines, f"待機中なので、古い受信側(pid {pid})を止めて新しいコードで起動し直す。"))


def _request_stop(pid: int) -> bool:
    """SIGINT to the receiver's process group, as Ctrl+C in its own Terminal would do."""
    try:
        if os.getpgid(pid) == pid:
            os.killpg(pid, signal.SIGINT)
        else:
            os.kill(pid, signal.SIGINT)
    except ProcessLookupError:
        return True
    except PermissionError:
        return False
    return True


def _wait_for_lock(log_dir: Path, timeout: float) -> IO[str] | None:
    deadline = time.monotonic() + timeout
    while True:
        lock_file, _ = _acquire_single_instance_lock(log_dir)
        if lock_file is not None or time.monotonic() >= deadline:
            return lock_file
        time.sleep(0.2)


def _signal_handler(signum: int, _frame: object) -> None:
    raise StopRequested(signum)


def _cleanup_remaining_processes(process_group: int, log_file: IO[str]) -> None:
    # The receiver normally reaps its Bonjour and Codex children in its own
    # shutdown path. Terminate any process still in the isolated receiver
    # group so a child that outlives the receiver cannot keep running.
    try:
        os.killpg(process_group, 0)
    except ProcessLookupError:
        return
    _emit("[PHONE_SABER][STOP] cleaning up remaining receiver child processes", log_file)
    try:
        os.killpg(process_group, signal.SIGTERM)
    except ProcessLookupError:
        return
    deadline = time.monotonic() + 2
    while time.monotonic() < deadline:
        try:
            os.killpg(process_group, 0)
        except ProcessLookupError:
            return
        time.sleep(0.05)
    _emit("[PHONE_SABER][STOP] force-stopping remaining receiver child processes", log_file)
    try:
        os.killpg(process_group, signal.SIGKILL)
    except ProcessLookupError:
        pass


def _stop_process_group(process: subprocess.Popen[str], log_file: IO[str]) -> None:
    if process.poll() is not None:
        _cleanup_remaining_processes(process.pid, log_file)
        return
    _emit("[PHONE_SABER][STOP] sending SIGINT to receiver and its child processes", log_file)
    try:
        os.killpg(process.pid, signal.SIGINT)
    except ProcessLookupError:
        pass
    try:
        process.wait(timeout=10)
    except subprocess.TimeoutExpired:
        _emit("[PHONE_SABER][STOP] receiver did not finish; sending SIGTERM", log_file)
        try:
            os.killpg(process.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        try:
            process.wait(timeout=3)
        except subprocess.TimeoutExpired:
            _emit("[PHONE_SABER][STOP] receiver did not exit; sending SIGKILL", log_file)
            try:
                os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            process.wait()
    _cleanup_remaining_processes(process.pid, log_file)


def _forward_receiver_line(line: str, log_file: IO[str]) -> None:
    sys.stdout.write(line)
    sys.stdout.flush()
    log_file.write(_stamp() + line)
    log_file.flush()
    if "[triage] listening on " in line:
        _emit("[PHONE_SABER][WAITING]", log_file)
        _emit(f"Receiver status: RUNNING (TCP {DEFAULT_PORT})", log_file)
    received = re.search(r"received (phone_saber_triage_[^\s]+)", line)
    if received:
        bundle_name = received.group(1).rstrip(".,;:")
        session_id = bundle_name.removeprefix("phone_saber_triage_")
        _emit(f"[PHONE_SABER][SESSION] sessionID={session_id}", log_file)


def run_receiver(
    repo_root: Path,
    snapshot: GitSnapshot,
    log_dir: Path,
    *,
    receiver_command: Sequence[str] | None = None,
    listener_finder=find_existing_receiver,
    health_fetcher=fetch_receiver_health,
    allow_restart: bool = True,
    stopper=_request_stop,
    restart_wait: float = RESTART_WAIT_SECONDS,
) -> int:
    """Run a receiver command while holding the single-instance lock and log.

    If a receiver is already running, report whether it runs stale code and, only
    when assess_running_receiver allows it, stop it and start a fresh one.
    """
    log_dir.mkdir(parents=True, exist_ok=True)
    lock_file, existing = _acquire_single_instance_lock(log_dir)
    restarted = False
    if lock_file is None:
        _announce_duplicate(log_dir=log_dir, metadata=existing)
        decision = assess_running_receiver(health_fetcher(DEFAULT_PORT), existing, repo_root,
                                           allow_restart=allow_restart)
        for line in decision.lines:
            _emit(line)
        if not decision.restart or decision.pid is None:
            return 0
        _emit(f"[PHONE_SABER][RESTART] stopping stale receiver pid {decision.pid}")
        if not stopper(decision.pid):
            _emit("[PHONE_SABER][RESTART] could not signal the old receiver. " + MANUAL_RESTART)
            return 1
        lock_file = _wait_for_lock(log_dir, restart_wait)
        if lock_file is None:
            _emit(f"[PHONE_SABER][RESTART] the old receiver did not stop within {restart_wait:.0f} s. "
                  "その Terminal を確認する。")
            return 1
        restarted = True

    try:
        listener = listener_finder(DEFAULT_PORT)
        if listener is not None:
            _announce_duplicate(log_dir=log_dir, listener=listener)
            if not restarted:
                decision = assess_running_receiver(health_fetcher(DEFAULT_PORT), {}, repo_root,
                                                   allow_restart=False)
                for line in decision.lines:
                    _emit(line)
            return 0

        log_path = _next_log_path(log_dir)
        _update_latest_link(log_path)
        _rotate_logs(log_dir)
        command = list(receiver_command) if receiver_command is not None else [
            sys.executable, "-u", str(RECEIVER_SCRIPT),
        ]
        with log_path.open("a", encoding="utf-8", errors="replace") as log_file:
            _emit_banner(snapshot, log_path, "STARTING", log_file)
            if restarted:
                _emit("[PHONE_SABER][RESTART] previous receiver stopped; starting with the current code", log_file)
            _emit("[PHONE_SABER][START]", log_file)
            previous_handlers: dict[int, object] = {}
            process: subprocess.Popen[str] | None = None
            stop_signal: int | None = None
            try:
                for signum in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
                    previous_handlers[signum] = signal.signal(signum, _signal_handler)
                process = subprocess.Popen(
                    command,
                    cwd=repo_root,
                    stdout=subprocess.PIPE,
                    stderr=subprocess.STDOUT,
                    text=True,
                    encoding="utf-8",
                    errors="replace",
                    bufsize=1,
                    start_new_session=True,
                )
                _write_lock_metadata(lock_file, pid=process.pid, repo_root=repo_root, log_path=log_path)
                assert process.stdout is not None
                for line in iter(process.stdout.readline, ""):
                    _forward_receiver_line(line, log_file)
                return_code = process.wait()
                _cleanup_remaining_processes(process.pid, log_file)
                _emit(f"[PHONE_SABER][STOP] receiver exit={return_code}", log_file)
                return return_code
            except StopRequested as exc:
                _emit("[PHONE_SABER][STOP] requested from Terminal", log_file)
                if process is not None:
                    _stop_process_group(process, log_file)
                return 128 + exc.signum
            except OSError as exc:
                _emit(f"[PHONE_SABER][ERROR] receiver could not start: {exc}", log_file)
                if process is not None:
                    _stop_process_group(process, log_file)
                return 1
            finally:
                if process is not None and process.poll() is None:
                    _stop_process_group(process, log_file)
                if process is not None and process.stdout is not None:
                    process.stdout.close()
                for signum, previous in previous_handlers.items():
                    signal.signal(signum, previous)
    finally:
        lock_file.close()


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--dry-run", action="store_true",
        help="resolve the repository and show Git status without starting the receiver",
    )
    parser.add_argument(
        "--no-restart", action="store_true",
        help=f"never stop a running receiver, even an idle one running stale code (also {NO_RESTART_ENV}=1)",
    )
    return parser.parse_args(argv)


def main(argv: Sequence[str] | None = None) -> int:
    args = _parse_args(argv)
    try:
        repo_root = resolve_repository_root()
        snapshot = collect_git_snapshot(repo_root)
    except LauncherError as exc:
        print(f"PhoneSaber launcher error: {exc}", file=sys.stderr)
        return 2

    if args.dry_run:
        _emit_banner(snapshot, None, "DRY RUN (receiver not started)")
        _emit("[PHONE_SABER][START] dry-run complete")
        return 0
    if not RECEIVER_SCRIPT.is_file():
        print(f"PhoneSaber receiver script not found: {RECEIVER_SCRIPT}", file=sys.stderr)
        return 2
    try:
        allow_restart = not args.no_restart and os.environ.get(NO_RESTART_ENV) != "1"
        return run_receiver(repo_root, snapshot, LOG_DIR, allow_restart=allow_restart)
    except (LauncherError, OSError) as exc:
        print(f"PhoneSaber launcher error: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
