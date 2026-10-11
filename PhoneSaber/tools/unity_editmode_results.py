"""Unity EditMode XML を検証し、明示した完全一致の既知失敗だけを許容する。"""

import argparse
from collections import Counter
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET


OUTCOMES = {"passed": "Passed", "failed": "Failed", "skipped": "Skipped",
            "inconclusive": "Inconclusive"}


def failed_tests(path):
    root = ET.parse(path).getroot()
    if root.tag != "test-run":
        raise ValueError("test-run ルートではありません")
    counts = {}
    for key in ("total", *OUTCOMES):
        # 古い結果に inconclusive がない場合だけ 0 とする。
        raw = root.get(key, "0" if key == "inconclusive" else "")
        if not re.fullmatch(r"[0-9]+", raw):
            raise ValueError(f"不正な集計: {key}={raw!r}")
        counts[key] = int(raw)
    if counts["total"] == 0 or counts["total"] != sum(counts[key] for key in OUTCOMES):
        raise ValueError("空結果、または集計の合計が一致しません")
    cases = list(root.iter("test-case"))
    names = [case.get("fullname") for case in cases]
    if any(not name or not name.strip() for name in names) or len(set(names)) != len(names):
        raise ValueError("test-case の完全名が欠落または重複しています")
    actual = Counter(case.get("result") for case in cases)
    if (len(cases) != counts["total"] or set(actual) - set(OUTCOMES.values()) or
            any(actual[outcome] != counts[key] for key, outcome in OUTCOMES.items())):
        raise ValueError("集計と test-case の結果が一致しません")
    result = root.get("result")
    if result == "Failed(Child)":
        result = "Failed"
    if result not in OUTCOMES.values() or (result == "Failed") != (counts["failed"] > 0):
        raise ValueError("test-run の結果と失敗集計が一致しません")
    # 子の失敗を示すだけの親 suite は許容するが、SetUp/TearDown 自体の失敗は許容しない。
    for suite in root.iter("test-suite"):
        suite_cases = list(suite.iter("test-case"))
        suite_actual = Counter(case.get("result") for case in suite_cases)
        suite_result = suite.get("result")
        suite_failed = suite_result in ("Failed", "Failed(Child)")
        if ((suite_result not in OUTCOMES.values() and suite_result != "Failed(Child)") or
                suite_failed != (suite_actual["Failed"] > 0)):
            raise ValueError("test-suite の結果と子の結果が一致しません")
        site = suite.get("site")
        if ((suite_failed and site not in (None, "Child")) or
                (suite.find("failure") is not None and (not suite_failed or site != "Child"))):
            raise ValueError("test-suite 自体の失敗があります")
        # suite に集計がある形式では、その値も検証する（ない形式は子の結果で確認）。
        if any(key in suite.attrib for key in ("total", *OUTCOMES)):
            expected = {"total": len(suite_cases),
                        **{key: suite_actual[outcome] for key, outcome in OUTCOMES.items()}}
            for key, count in expected.items():
                raw = suite.get(key, "0" if key == "inconclusive" else "")
                if not re.fullmatch(r"[0-9]+", raw) or int(raw) != count:
                    raise ValueError(f"test-suite の集計が一致しません: {key}={raw!r}")
    print(f"EditMode: total={counts['total']} passed={counts['passed']} failed={counts['failed']}")
    return [case.get("fullname") for case in cases if case.get("result") == "Failed"]


def known_failures(path):
    if path is None:
        return set()
    names = json.loads(path.read_text())
    if not isinstance(names, list) or any(
            not isinstance(name, str) or not name.strip() or "*" in name for name in names):
        raise ValueError("既知失敗は完全なテスト名の JSON 配列で指定してください（ワイルドカード不可）")
    return set(names)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("xml", type=Path)
    parser.add_argument("log", type=Path)
    parser.add_argument("--known-failures", type=Path)
    args = parser.parse_args(argv)
    try:
        allowed = known_failures(args.known_failures)
        failed = failed_tests(args.xml)
    except (OSError, ValueError, ET.ParseError) as error:
        print(f"EditMode: 結果を検証できません: {error}（log: {args.log}）", file=sys.stderr)
        return 2
    for name in failed:
        print(("KNOWN FAIL " if name in allowed else "FAIL ") + name)
    return 1 if any(name not in allowed for name in failed) else 0


if __name__ == "__main__":
    sys.exit(main())
