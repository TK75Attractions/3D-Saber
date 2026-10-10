#!/usr/bin/env python3
"""Run Unity PlayMode tests in batches on a project COPY and merge the results.

Unity 6000.3 batchmode PlayMode runs of the whole suite sometimes die with a
native segv inside Enlighten worker threads (also on origin/main, 2026-10-09).
Running the test classes in smaller groups, each in a fresh Unity process,
avoids losing the whole run: a crashed group is retried once and then split in
halves until the crashing class is isolated.

Never point --project at the checkout that the Unity Editor has open; use a
copy (rsync Assets/Packages/ProjectSettings into a folder that has a Library).

    python3 PhoneSaber/tools/unity_playmode_batches.py \
        --project /path/to/copy --out /tmp/playmode --groups 6
"""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

UNITY = "/Applications/Unity/Hub/Editor/6000.3.9f1/Unity.app/Contents/MacOS/Unity"
CLASS_RE = re.compile(r"^\s*public\s+(?:sealed\s+|static\s+)?class\s+([A-Za-z_][A-Za-z0-9_]*)", re.M)
TEST_RE = re.compile(r"\[\s*(?:UnityTest|Test|TestCase|TestCaseSource)\b")


def discover_classes(test_dir: Path) -> list[str]:
    """Public classes declared in files that contain NUnit/Unity tests, sorted."""
    names: set[str] = set()
    for path in sorted(test_dir.glob("*.cs")):
        text = path.read_text(encoding="utf-8", errors="replace")
        if TEST_RE.search(text):
            names.update(CLASS_RE.findall(text))
    return sorted(names)


def chunk(items: list[str], groups: int) -> list[list[str]]:
    groups = max(1, min(groups, len(items))) if items else 1
    size, extra = divmod(len(items), groups)
    out, start = [], 0
    for index in range(groups):
        end = start + size + (1 if index < extra else 0)
        if end > start:
            out.append(items[start:end])
        start = end
    return out


def test_filter(classes: list[str]) -> str:
    # NUnit full names here are "Class.Test" (no namespace); anchor so that a
    # class name cannot match a longer one.
    return "^(" + "|".join(re.escape(name) for name in classes) + r")\."


def parse_results(path: Path) -> dict | None:
    if not path.exists():
        return None
    try:
        root = ET.parse(path).getroot()
        counts = {key: int(root.attrib[key]) for key in ("total", "passed", "failed", "skipped")}
    except (ET.ParseError, KeyError, ValueError):
        return None
    # 空の結果や別形式の XML を成功とせず、既存の再試行・分割処理に渡す。
    if root.tag != "test-run" or counts["total"] <= 0 or any(value < 0 for value in counts.values()):
        return None
    failed = [case.get("fullname", "") for case in root.iter("test-case") if case.get("result") == "Failed"]
    return {
        **counts,
        "failed_tests": failed,
    }


def run_group(project: Path, out: Path, name: str, classes: list[str], unity: str) -> dict | None:
    xml = out / f"{name}.xml"
    log = out / f"{name}.log"
    xml.unlink(missing_ok=True)
    command = [unity, "-batchmode", "-projectPath", str(project), "-runTests", "-testPlatform", "PlayMode",
               "-testFilter", test_filter(classes), "-testResults", str(xml), "-logFile", str(log)]
    subprocess.run(command, check=False)
    return parse_results(xml)


def run(project: Path, out: Path, classes: list[str], groups: int, unity: str = UNITY) -> dict:
    out.mkdir(parents=True, exist_ok=True)
    summary = {"total": 0, "passed": 0, "failed": 0, "skipped": 0, "failed_tests": [], "crashed": [], "runs": 0}
    pending = [(f"g{index:02d}", group) for index, group in enumerate(chunk(classes, groups))]
    while pending:
        name, group = pending.pop(0)
        result = run_group(project, out, name, group, unity)
        summary["runs"] += 1
        if result is None:  # crashed or no XML: retry once, then split
            result = run_group(project, out, name + "r", group, unity)
            summary["runs"] += 1
        if result is None:
            if len(group) == 1:
                summary["crashed"].append(group[0])
            else:
                half = len(group) // 2
                pending[:0] = [(name + "a", group[:half]), (name + "b", group[half:])]
            continue
        for key in ("total", "passed", "failed", "skipped"):
            summary[key] += result[key]
        summary["failed_tests"].extend(result["failed_tests"])
    return summary


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--project", required=True, type=Path, help="Unity project COPY (not the open checkout)")
    parser.add_argument("--out", required=True, type=Path)
    parser.add_argument("--groups", type=int, default=6)
    parser.add_argument("--unity", default=UNITY)
    parser.add_argument("--classes", nargs="*", help="run only these test classes")
    args = parser.parse_args(argv)
    classes = args.classes or discover_classes(args.project / "Assets" / "Tests" / "PlayMode")
    if not classes:
        print("PlayMode のテストクラスが見つかりません", file=sys.stderr)
        return 2
    summary = run(args.project, args.out, classes, args.groups, args.unity)
    (args.out / "summary.json").write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"PlayMode: total={summary['total']} passed={summary['passed']} failed={summary['failed']} "
          f"skipped={summary['skipped']} crashed_classes={len(summary['crashed'])} unity_runs={summary['runs']}")
    for name in summary["failed_tests"]:
        print("FAIL", name)
    for name in summary["crashed"]:
        print("CRASH", name)
    return 0 if summary["failed"] == 0 and not summary["crashed"] else 1


if __name__ == "__main__":
    sys.exit(main())
