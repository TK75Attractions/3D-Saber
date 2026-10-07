#!/usr/bin/env python3
"""Run Unity test groups with bounded timeouts and visible progress."""

from __future__ import annotations

import argparse
import datetime as dt
import re
import subprocess
import sys
import tempfile
import time
from pathlib import Path


DEFAULT_UNITY = Path(
    "/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents/MacOS/Unity"
)


def playmode_classes(project: Path) -> list[str]:
    names: set[str] = set()
    for source in sorted((project / "Assets/Tests/PlayMode").glob("*.cs")):
        text = source.read_text(encoding="utf-8", errors="replace")
        for match in re.finditer(r"\bclass\s+([A-Za-z_]\w*)", text):
            name = match.group(1)
            if name.endswith("Tests") or name.endswith("Test"):
                names.add(name)
    return sorted(
        name
        for name in names
        if name not in {"FavoriteEffectsPlayTests", "PhoneSaberReliabilityPlayTests"}
    )


def groups(project: Path) -> list[dict[str, object]]:
    other = ";".join(playmode_classes(project))
    return [
        {
            "name": "font-regression",
            "platform": "editmode",
            "filter": "UISkinKitTests;SchoolSongChartTests;SongSelectVisualTests",
            "timeout": 600,
        },
        {"name": "editmode-all", "platform": "editmode", "filter": None, "timeout": 1800},
        {
            "name": "phonesaber-editmode",
            "platform": "editmode",
            "filter": "InputPointConversionTests;InputPointSingletonTests;PhoneSaberProjectSettingsTests;SaberInputBridgeTests",
            "timeout": 600,
        },
        {
            "name": "phonesaber-playmode",
            "platform": "playmode",
            "filter": "PhoneSaberReliabilityPlayTests",
            "timeout": 900,
        },
        {
            "name": "playmode-other",
            "platform": "playmode",
            "filter": other,
            "timeout": 1800,
        },
        {
            "name": "playmode-favorite-effects",
            "platform": "playmode",
            "filter": "FavoriteEffectsPlayTests",
            "timeout": 3600,
        },
    ]


def parse_result(path: Path) -> tuple[str, str]:
    import xml.etree.ElementTree as ET

    if not path.exists():
        return "NO_XML", "result XML was not generated"
    try:
        root = ET.parse(path).getroot()
    except (OSError, ET.ParseError) as error:
        return "MALFORMED", f"result XML is malformed: {error}"
    if root.tag != "test-run":
        return "MALFORMED", f"unexpected result XML root: {root.tag}"
    try:
        total = int(root.attrib["total"] if "total" in root.attrib else root.attrib["testcasecount"])
        passed = int(root.attrib["passed"])
        failed = int(root.attrib["failed"])
        skipped = int(root.attrib.get("skipped", "0"))
        inconclusive = int(root.attrib.get("inconclusive", "0"))
    except (KeyError, ValueError) as error:
        return "MALFORMED", f"invalid result counts: {error}"
    counts = (total, passed, failed, skipped, inconclusive)
    if any(count < 0 for count in counts) or passed + failed + skipped + inconclusive != total:
        return "MALFORMED", f"inconsistent result counts: {counts}"
    result = root.attrib.get("result", "unknown")
    detail = f"{passed}/{total} passed result={result} failed={failed} skipped={skipped} inconclusive={inconclusive}"
    if result == "Passed" and passed > 0 and failed == 0 and inconclusive == 0:
        return "PASS", detail
    return "FAIL", detail


def run_group(
    unity: Path,
    project: Path,
    log_dir: Path,
    group: dict[str, object],
    nographics: bool,
) -> str:
    name = str(group["name"])
    platform = str(group["platform"])
    test_filter = group["filter"]
    timeout = int(group["timeout"])
    result_path = log_dir / f"{name}.xml"
    log_path = log_dir / f"{name}.log"
    console_path = log_dir / f"{name}.console.log"
    # 同じ出力先で再実行しても、前回の成功結果やロックログを参照しない。
    result_path.unlink(missing_ok=True)
    log_path.unlink(missing_ok=True)
    command = [
        str(unity),
        "-batchmode",
        *(["-nographics"] if nographics else []),
        "-projectPath",
        str(project),
        "-runTests",
        "-testPlatform",
        platform,
    ]
    if test_filter:
        command.extend(["-testFilter", str(test_filter)])
    command.extend(["-testResults", str(result_path), "-logFile", str(log_path)])
    print(
        f"[UNITY_TEST][GROUP] name={name} platform={platform} timeout={timeout}s "
        f"start={dt.datetime.now().astimezone().isoformat(timespec='seconds')}",
        flush=True,
    )
    started = time.monotonic()
    with console_path.open("w", encoding="utf-8") as console_stream:
        process = subprocess.Popen(command, cwd=project, stdout=console_stream, stderr=subprocess.STDOUT)
    offset = 0
    console_offset = 0
    last_progress = "none"
    last_heartbeat = 0
    try:
        while process.poll() is None:
            elapsed = time.monotonic() - started
            if log_path.exists():
                with log_path.open("r", encoding="utf-8", errors="replace") as stream:
                    stream.seek(offset)
                    for line in stream:
                        if "[UNITY_TEST]" in line or "Test run completed" in line:
                            last_progress = line.strip()
                            print(last_progress, flush=True)
                    offset = stream.tell()
            if console_path.exists():
                with console_path.open("r", encoding="utf-8", errors="replace") as stream:
                    stream.seek(console_offset)
                    for line in stream:
                        line = line.strip()
                        if line and ("another Unity instance" in line or "already open" in line.lower()):
                            last_progress = line
                            print(f"[UNITY_TEST][UNITY] {line}", flush=True)
                    console_offset = stream.tell()
            if elapsed > timeout:
                print(
                    f"[UNITY_TEST][GROUP] name={name} state=TIMEOUT elapsed={elapsed:.1f}s "
                    f"last={last_progress}",
                    flush=True,
                )
                process.terminate()
                try:
                    process.wait(timeout=10)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
                return "TIMEOUT"
            heartbeat = int(elapsed // 10)
            if heartbeat > last_heartbeat:
                last_heartbeat = heartbeat
                print(
                    f"[UNITY_TEST][GROUP] name={name} elapsed={elapsed:.1f}s last={last_progress}",
                    flush=True,
                )
            time.sleep(1)
    finally:
        if process.poll() is None:
            process.terminate()
            process.wait()
    status, detail = parse_result(result_path)
    log_text = log_path.read_text(encoding="utf-8", errors="replace") if log_path.exists() else ""
    console_text = console_path.read_text(encoding="utf-8", errors="replace") if console_path.exists() else ""
    if process.returncode not in (0, None):
        if "already open" in log_text.lower() or "another instance" in log_text.lower() or "already open" in console_text.lower() or "another unity instance" in console_text.lower():
            status = "BLOCKED"
        elif status == "PASS":
            status = "EXECUTION_FAILURE"
        elif status == "NO_XML":
            status = "EXECUTION_FAILURE"
        detail = f"Unity exit={process.returncode}; {detail}"
    print(
        f"[UNITY_TEST][GROUP] name={name} state={status} elapsed={time.monotonic() - started:.1f}s {detail}",
        flush=True,
    )
    return status


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--project", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--unity", type=Path, default=DEFAULT_UNITY)
    parser.add_argument("--log-dir", type=Path)
    parser.add_argument(
        "--group",
        choices=[
            "all",
            "editmode",
            "playmode",
            "font-regression",
            "editmode-all",
            "phonesaber-editmode",
            "phonesaber-playmode",
            "playmode-other",
            "playmode-favorite-effects",
        ],
        default="all",
    )
    parser.add_argument("--nographics", action="store_true")
    args = parser.parse_args()
    project = args.project.resolve()
    if not args.unity.is_file():
        print(f"Unity executable not found: {args.unity}", file=sys.stderr)
        return 2
    if args.log_dir:
        log_dir = args.log_dir.resolve()
    else:
        stamp = dt.datetime.now().strftime("%Y%m%d-%H%M%S")
        log_dir = Path(tempfile.mkdtemp(prefix=f"3d-saber-unity-{stamp}-"))
    log_dir.mkdir(parents=True, exist_ok=True)
    selected = groups(project)
    if args.group == "editmode":
        selected = [group for group in selected if str(group["platform"]) == "editmode"]
    elif args.group == "playmode":
        selected = [group for group in selected if str(group["platform"]) == "playmode"]
    elif args.group != "all":
        selected = [group for group in selected if str(group["name"]) == args.group]
    statuses = [run_group(args.unity, project, log_dir, group, args.nographics) for group in selected]
    print(f"[UNITY_TEST][SUMMARY] log_dir={log_dir} statuses={' '.join(statuses)}", flush=True)
    return 0 if statuses and all(status == "PASS" for status in statuses) else 1


if __name__ == "__main__":
    raise SystemExit(main())
