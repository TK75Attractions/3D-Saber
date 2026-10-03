#!/usr/bin/env python3
"""Portable, non-destructive Mac setup for PhoneSaber and its Unity project."""
from __future__ import annotations

import argparse
import datetime as dt
import os
import plistlib
import re
import subprocess
import sys
from pathlib import Path
from typing import Sequence

TOOLS = Path("ios/PhoneSaberSender/Tools")
MODEL_PINS = {
    "phone_saber_triage_codex.py": {"ANALYSIS_MODEL": "gpt-6-luna", "ANALYSIS_REASONING_EFFORT": "max"},
    "phone_saber_auto_repair.py": {"REPAIR_MODEL": "gpt-6-sol", "REPAIR_REASONING_EFFORT": "high", "REVIEW_MODEL": "gpt-6-sol", "REVIEW_REASONING_EFFORT": "high"},
}
REQUIRED_SERVICES = {"_phonesaber._udp", "_phonesaber-diag._tcp"}


def run(*args: str, cwd: Path | None = None, timeout: int = 30) -> subprocess.CompletedProcess[str]:
    try:
        return subprocess.run(args, cwd=cwd, capture_output=True, text=True,
                              encoding="utf-8", errors="replace", timeout=timeout, check=False,
                              env={**os.environ, "GIT_OPTIONAL_LOCKS": "0", "PYTHONDONTWRITEBYTECODE": "1"})
    except (OSError, subprocess.TimeoutExpired):
        return subprocess.CompletedProcess(args, 127, "", "")


def discover_unity(repo: Path, override: Path | None) -> Path:
    return (override if override is not None else repo.parent / "3D-Saber").expanduser().resolve()


def inspect_git(path: Path) -> tuple[bool, str]:
    if not path.is_dir():
        return False, "directory missing"
    root = run("git", "-C", str(path), "rev-parse", "--show-toplevel")
    if root.returncode != 0 or Path(root.stdout.strip()).resolve() != path.resolve():
        return False, "not an independent Git repository"
    branch = run("git", "-C", str(path), "branch", "--show-current").stdout.strip() or "detached HEAD"
    dirty = bool(run("git", "-C", str(path), "status", "--porcelain").stdout.strip())
    origin = run("git", "-C", str(path), "remote", "get-url", "origin").returncode == 0
    main = run("git", "-C", str(path), "show-ref", "--verify", "--quiet", "refs/heads/main").returncode == 0
    details = f"branch={branch}; working tree={'dirty' if dirty else 'clean'}; origin={'yes' if origin else 'no'}; main={'yes' if main else 'no'}"
    return True, details


def lfs_pointer_files(unity: Path) -> list[str] | None:
    result = run("git", "-C", str(unity), "lfs", "ls-files", "-n", timeout=60)
    if result.returncode != 0:
        return None
    pointers = []
    for relative in result.stdout.splitlines():
        asset = unity / relative
        try:
            with asset.open("rb") as stream:
                if stream.read(64).startswith(b"version https://git-lfs.github.com/spec/v1"):
                    pointers.append(relative)
        except OSError:
            pointers.append(relative)
    return pointers


def check_models(repo: Path) -> bool:
    for name, pins in MODEL_PINS.items():
        source = (repo / TOOLS / name).read_text(encoding="utf-8")
        for key, value in pins.items():
            if re.search(rf'^{key}\s*=\s*["\']{re.escape(value)}["\']\s*$', source, re.MULTILINE) is None:
                return False
    return True


def check_ios(repo: Path) -> tuple[bool, str]:
    project = repo / "ios/PhoneSaberSender/PhoneSaberSender.xcodeproj"
    settings = project / "project.pbxproj"
    plist = repo / "ios/PhoneSaberSender/BonjourInfo.plist"
    if not settings.is_file() or not plist.is_file():
        return False, "PhoneSaberSender project or BonjourInfo.plist missing"
    try:
        info = plistlib.loads(plist.read_bytes())
        config = settings.read_text(encoding="utf-8")
    except (OSError, ValueError):
        return False, "iOS project settings unreadable"
    if not REQUIRED_SERVICES.issubset(set(info.get("NSBonjourServices", []))):
        return False, "Bonjour service declaration missing"
    if "INFOPLIST_KEY_NSLocalNetworkUsageDescription" not in config or "INFOPLIST_FILE = BonjourInfo.plist" not in config:
        return False, "Local Network permission or plist wiring missing"
    signing = "configured team in project; select your own team in Xcode" if "DEVELOPMENT_TEAM =" in config else "development team not configured"
    return True, signing


def unity_version(unity: Path) -> str | None:
    file = unity / "ProjectSettings/ProjectVersion.txt"
    if not file.is_file():
        return None
    match = re.search(r"^m_EditorVersion:\s*(\S+)", file.read_text(encoding="utf-8"), re.MULTILINE)
    return match.group(1) if match else None


def editor_present(version: str) -> bool:
    candidates = [Path("/Applications/Unity/Hub/Editor") / version / "Unity.app/Contents/MacOS/Unity",
                  Path.home() / "Applications/Unity/Hub/Editor" / version / "Unity.app/Contents/MacOS/Unity"]
    return any(path.is_file() for path in candidates)


# Keep in sync with install_phone_saber_launcher.command.
DESKTOP_LAUNCHERS = ("Start PhoneSaber.command", "Open PhoneSaber Log.command",
                     "Open Latest PhoneSaber Images.command", "PhoneSaber Status.command")


def launcher_links(repo: Path, home: Path) -> bool:
    for name in DESKTOP_LAUNCHERS:
        link = home / "Desktop" / name
        if not link.is_symlink() or link.resolve() != (repo / TOOLS / name).resolve():
            return False
    return True


class Report:
    def __init__(self) -> None:
        self.lines: list[str] = []
        self.required = 0
        self.manual = 0

    def add(self, kind: str, label: str, detail: str = "") -> None:
        self.lines.append(f"[{kind}] {label}" + (f": {detail}" if detail else ""))
        if kind == "ERROR":
            self.required += 1
        elif kind == "MANUAL ACTION REQUIRED":
            self.manual += 1

    def render(self, check: bool) -> str:
        state = "CHECK ONLY" if check else "SETUP"
        readiness = "READY" if not self.required and not self.manual else f"REQUIRES ACTION: {self.required} failures, {self.manual} manual steps"
        return "\n".join(["=" * 40, f" PhoneSaber Mac Setup ({state})", "=" * 40,
                          *self.lines, readiness, "=" * 40]) + "\n"


def write_report(home: Path, content: str) -> Path:
    log_dir = home / "Library/Logs/PhoneSaber"
    log_dir.mkdir(parents=True, exist_ok=True)
    stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
    path = log_dir / f"setup-{stamp}.log"
    suffix = 1
    while path.exists():
        path = log_dir / f"setup-{stamp}-{suffix}.log"
        suffix += 1
    path.write_text(content, encoding="utf-8")
    latest = log_dir / "setup-latest.log"
    temporary = log_dir / f".setup-latest-{os.getpid()}.tmp"
    try:
        temporary.symlink_to(path.name)
        os.replace(temporary, latest)
    finally:
        temporary.unlink(missing_ok=True)
    return path


def perform(repo: Path, unity: Path, home: Path, check: bool, verify: bool) -> tuple[Report, str]:
    report = Report()
    mac = run("sw_vers", "-productVersion")
    arch = run("uname", "-m")
    if sys.platform != "darwin" or mac.returncode != 0 or arch.returncode != 0:
        report.add("ERROR", "macOS required")
    else:
        report.add("OK", "macOS", f"{mac.stdout.strip()} / {'Apple Silicon' if arch.stdout.strip() == 'arm64' else 'Intel' if arch.stdout.strip() == 'x86_64' else arch.stdout.strip()}")
    git = run("git", "--version")
    report.add("OK" if git.returncode == 0 else "ERROR", "Git", git.stdout.strip() if git.returncode == 0 else "install Xcode Command Line Tools")
    repo_state: dict[str, tuple[bool, str]] = {}
    for label, path in (("school-festival", repo), ("3D-Saber", unity)):
        good, detail = inspect_git(path) if git.returncode == 0 else (False, "Git unavailable")
        repo_state[label] = good, detail
        report.add("OK" if good else "ERROR", label, f"{path} ({detail})")
        if good and ("working tree=dirty" in detail or "origin=no" in detail or "main=no" in detail or detail.split(";")[0] != "branch=main"):
            report.add("WARN", f"{label} Git state", detail)
    safe_to_install = not check and sys.platform == "darwin" and all(state[0] for state in repo_state.values())
    brew = run("brew", "--version")
    report.add("OK" if brew.returncode == 0 else "WARN", "Homebrew", "installed" if brew.returncode == 0 else "Homebrew not installed")
    lfs = run("git", "lfs", "version")
    if lfs.returncode != 0 and safe_to_install and brew.returncode == 0:
        installed = run("brew", "install", "git-lfs", timeout=600)
        lfs = run("git", "lfs", "version") if installed.returncode == 0 else lfs
    if lfs.returncode:
        report.add("MANUAL ACTION REQUIRED", "Git LFS", "install Git LFS (brew install git-lfs)" if brew.returncode == 0 else "install Homebrew, then brew install git-lfs")
    else:
        report.add("OK", "Git LFS", lfs.stdout.strip())
        if safe_to_install:
            init = run("git", "lfs", "install", timeout=60)
            if init.returncode:
                report.add("ERROR", "git lfs install failed")
            if "working tree=dirty" in repo_state["3D-Saber"][1]:
                report.add("WARN", "3D-Saber LFS pull", "skipped because the working tree has changes")
            else:
                pull = run("git", "-C", str(unity), "lfs", "pull", timeout=900)
                if pull.returncode:
                    report.add("ERROR", "3D-Saber git lfs pull failed", "run git lfs pull in the Unity repository")
        if repo_state["3D-Saber"][0]:
            pointers = lfs_pointer_files(unity)
            report.add("ERROR" if pointers is None or pointers else "OK", "Unity LFS assets",
                       "could not list LFS assets" if pointers is None else f"{len(pointers)} pointer or missing asset(s)" if pointers else "no unresolved tracked pointers")
    py = run("python3", "--version")
    report.add("OK" if sys.version_info >= (3, 9) and py.returncode == 0 else "ERROR", "Python", f"{py.stdout.strip() or py.stderr.strip()} (PhoneSaber Tools use standard library)")
    codex = run("codex", "--version")
    if codex.returncode:
        report.add("MANUAL ACTION REQUIRED", "Codex CLI is not installed", "install from https://learn.chatgpt.com/docs/codex/cli, then run codex login")
    else:
        report.add("OK", "Codex CLI", codex.stdout.strip())
        login = run("codex", "login", "status", timeout=20)
        # Only report the state, never copy arbitrary CLI output into the log.
        authenticated = login.returncode == 0 and "Logged in" in (login.stdout + login.stderr)
        report.add("OK" if authenticated else "MANUAL ACTION REQUIRED",
                   "Codex login" if authenticated else "Codex login required",
                   "authenticated" if authenticated else "run codex login yourself")
    try:
        pins = check_models(repo)
    except OSError:
        pins = False
    report.add("OK" if pins else "ERROR", "Codex model pins", "analysis luna/max; repair and reviewer sol/high" if pins else "expected model settings missing")
    select = run("xcode-select", "-p")
    xcode = run("xcodebuild", "-version")
    full = select.returncode == 0 and xcode.returncode == 0 and "Xcode" in xcode.stdout
    report.add("OK" if full else "MANUAL ACTION REQUIRED", "Xcode" if full else "Install/open Xcode", xcode.stdout.splitlines()[0] if full else "Command Line Tools alone are insufficient")
    ios_ok, signing = check_ios(repo)
    report.add("OK" if ios_ok else "ERROR", "iOS project, Local Network and Bonjour", signing)
    report.add("MANUAL ACTION REQUIRED", "iPhone signing", "Open PhoneSaberSender.xcodeproj; select your Apple Development Team and iPhone; run once")
    version = unity_version(unity)
    if version is None:
        report.add("ERROR", "Unity project version", "ProjectSettings/ProjectVersion.txt missing")
    else:
        report.add("OK", "Required Unity", version)
        hub = Path("/Applications/Unity Hub.app").exists() or (home / "Applications/Unity Hub.app").exists()
        report.add("OK" if hub else "MANUAL ACTION REQUIRED", "Unity Hub" if hub else "Install Unity Hub")
        report.add("OK" if editor_present(version) else "MANUAL ACTION REQUIRED", "Unity Editor" if editor_present(version) else f"Install Unity {version}")
    fixture = unity / "Assets/Resources/Fonts/NotoSansJP-Light.otf"
    report.add("OK" if fixture.is_file() else "ERROR", "Unity EditMode font fixture", "NotoSansJP-Light.otf" if fixture.is_file() else "missing")
    font = unity / "Assets/Resources/Fonts/Makinas-4-Square.otf"
    report.add("OK" if font.is_file() else "OPTIONAL/MANUAL", "Makinas-4-Square.otf", "installed locally" if font.is_file() else "not installed locally; production UI may need it")
    installer = repo / TOOLS / "install_phone_saber_launcher.command"
    if not installer.is_file():
        report.add("ERROR", "PhoneSaber launcher installer", "missing")
    else:
        if safe_to_install:
            result = run("/bin/bash", str(installer), timeout=30)
            if result.returncode:
                report.add("ERROR", "PhoneSaber launcher installer", "failed or Desktop item conflicts")
        links = launcher_links(repo, home)
        report.add("OK" if links else ("WARN" if check or not safe_to_install else "ERROR"), "Desktop launchers", "installed" if links else "not installed")
        dry = run("python3", "-B", str(repo / TOOLS / "phone_saber_receiver_launcher.py"), "--dry-run", timeout=30)
        report.add("OK" if dry.returncode == 0 else "ERROR", "Start PhoneSaber dry-run", "repository resolved; receiver not started" if dry.returncode == 0 else "launcher check failed")
    log_dir = home / "Library/Logs/PhoneSaber"
    if check:
        report.add("OK" if log_dir.is_dir() and os.access(log_dir, os.W_OK) else "WARN", "PhoneSaber log directory", "writable" if log_dir.is_dir() and os.access(log_dir, os.W_OK) else "will be created during setup")
    else:
        try:
            log_dir.mkdir(parents=True, exist_ok=True)
            report.add("OK" if os.access(log_dir, os.W_OK) else "ERROR", "PhoneSaber log directory", str(log_dir))
        except OSError:
            report.add("ERROR", "PhoneSaber log directory", "cannot create")
    # This is read-only and shares the receiver's own process recognition logic.
    try:
        sys.dont_write_bytecode = True
        sys.path.insert(0, str(repo / TOOLS))
        from phone_saber_receiver_launcher import find_existing_receiver
        listener = find_existing_receiver()
        report.add("WARN" if listener and not listener.is_phone_saber_receiver else "OK", "TCP 8765", "occupied by another or unknown process" if listener and not listener.is_phone_saber_receiver else "PhoneSaber receiver running" if listener else "available")
    except Exception:
        report.add("WARN", "TCP 8765", "could not inspect listener")
    if not check:
        tests = run("python3", "-B", "-m", "unittest", "discover", "-s", str(repo / TOOLS), "-p", "test_*.py", cwd=repo, timeout=600)
        report.add("OK" if tests.returncode == 0 else "ERROR", "PhoneSaber Tools tests", "passed" if tests.returncode == 0 else "failed")
        syntax = run("python3", "-B", "-c", "import ast, pathlib, sys; [ast.parse(p.read_text(encoding='utf-8'), filename=str(p)) for p in pathlib.Path(sys.argv[1]).glob('*.py')]", str(repo / TOOLS), timeout=60)
        report.add("OK" if syntax.returncode == 0 else "ERROR", "Python syntax")
        diff = run("git", "-C", str(repo), "diff", "--check")
        report.add("OK" if diff.returncode == 0 else "ERROR", "git diff --check")
        if verify:
            own = run("python3", "-B", "-m", "unittest", "discover", "-s", str(repo / "tools"), "-p", "test_setup_mac.py", cwd=repo, timeout=120)
            report.add("OK" if own.returncode == 0 else "ERROR", "Mac setup tests", "passed" if own.returncode == 0 else "failed")
    return report, report.render(check)


def main(argv: Sequence[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parent.parent, help=argparse.SUPPRESS)
    parser.add_argument("--3d-saber", type=Path, help="path to the 3D-Saber repository")
    parser.add_argument("--check", action="store_true", help="read-only environment check (no log or installation)")
    parser.add_argument("--verify", action="store_true", help="also run setup tool tests (setup mode only)")
    args = parser.parse_args(argv)
    if args.check and args.verify:
        parser.error("--verify cannot be combined with --check")
    repo = args.repo.expanduser().resolve()
    unity = discover_unity(repo, args.__dict__["3d_saber"])
    if not unity.is_dir():
        print(f"[ERROR] 3D-Saber repository not found: {unity}")
    report, content = perform(repo, unity, Path.home(), args.check, args.verify)
    print(content, end="")
    if not args.check:
        try:
            print(f"Setup report: {write_report(Path.home(), content)}")
        except OSError as exc:
            print(f"[ERROR] Could not save setup report: {exc}", file=sys.stderr)
            return 1
    return 1 if report.required else 2 if report.manual else 0


if __name__ == "__main__":
    raise SystemExit(main())
