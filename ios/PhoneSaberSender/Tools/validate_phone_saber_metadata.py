#!/usr/bin/env python3
"""Validate PhoneSaber Debug Recording metadata without rejecting legacy fields."""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from phone_saber_metadata_schema import CURRENT_FORMAT_VERSION, load_metadata_file


def validate_paths(paths: list[Path], *, strict: bool = False) -> int:
    failed = False
    warned = False
    for path in paths:
        try:
            validated = load_metadata_file(path)
        except (OSError, ValueError, UnicodeError) as error:
            print(f"{path}: INVALID: {error}", file=sys.stderr)
            failed = True
            continue

        report = validated.report
        status = "valid" if report.warning_count == 0 else "valid with unknown fields"
        version = "unknown" if report.format_version is None else str(report.format_version)
        print(
            f"{path}: {status}; formatVersion={version}; "
            f"frames={len(validated.frames)}; current={CURRENT_FORMAT_VERSION}"
        )
        if report.warning_count:
            warned = True
            for message, count in report.warnings.items():
                print(f"  {message} ({count} occurrence{'s' if count != 1 else ''})")

    if failed:
        return 2
    if strict and warned:
        return 1
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("metadata", nargs="+", type=Path, help="one or more metadata JSON files")
    parser.add_argument(
        "--strict", action="store_true",
        help="return status 1 when fields are missing or malformed (legacy mode still parses by default)",
    )
    arguments = parser.parse_args()
    return validate_paths(arguments.metadata, strict=arguments.strict)


if __name__ == "__main__":
    raise SystemExit(main())
