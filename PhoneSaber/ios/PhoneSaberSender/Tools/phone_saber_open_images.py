#!/usr/bin/env python3
"""Open the selected PNGs from the most recent PhoneSaber triage bundle."""

from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path
from typing import Callable, Sequence


INBOX = Path.home() / "Library" / "Application Support" / "PhoneSaber" / "diagnostics-inbox"
SESSION_PREFIX = "phone_saber_triage_"
SESSION_TIMESTAMP_RE = re.compile(r"^phonesaber_(\d{8}_\d{6}_\d{3})$")


class ViewerError(RuntimeError):
    """An error that should be shown clearly in the Terminal window."""


def session_sort_key(session: Path) -> tuple[int, int, str]:
    """Prefer capture time encoded in PhoneSaber session IDs over mutable mtimes."""
    session_id = session.name.removeprefix(SESSION_PREFIX)
    match = SESSION_TIMESTAMP_RE.fullmatch(session_id)
    if match:
        # The fixed-width ID timestamp sorts chronologically and stays unchanged
        # when analysis_report.md or other files are written later.
        return (1, int(match.group(1).replace("_", "")), session.name)
    # Older or externally created IDs have no embedded capture time.
    try:
        modified_ns = session.stat().st_mtime_ns
    except OSError:
        modified_ns = 0
    return (0, modified_ns, session.name)


def find_latest_session(inbox: Path) -> Path:
    if not inbox.exists():
        raise ViewerError(f"PhoneSaber diagnostics inbox does not exist: {inbox}")
    if inbox.is_symlink() or not inbox.is_dir():
        raise ViewerError(f"PhoneSaber diagnostics inbox is not a directory: {inbox}")

    sessions = [
        path for path in inbox.iterdir()
        if path.name.startswith(SESSION_PREFIX) and path.is_dir() and not path.is_symlink()
    ]
    if not sessions:
        raise ViewerError(f"No PhoneSaber sessions were found in: {inbox}")
    return max(sessions, key=session_sort_key)


def selected_pngs(session: Path) -> list[Path]:
    images_dir = session / "images"
    if images_dir.is_symlink() or not images_dir.is_dir():
        raise ViewerError(f"Latest session has no images directory: {images_dir}")
    return sorted(
        (path for path in images_dir.glob("*.png") if path.is_file() and not path.is_symlink()),
        key=lambda path: path.name,
    )


def parse_args(argv: Sequence[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--finder", action="store_true",
                        help="also reveal the latest session folder in Finder")
    parser.add_argument("--report", action="store_true",
                        help="also open analysis_report.md when it exists")
    parser.add_argument("--inbox", type=Path, default=INBOX,
                        help=argparse.SUPPRESS)
    return parser.parse_args(argv)


def _open(opener: Callable[..., subprocess.CompletedProcess[str]], command: list[str],
          description: str) -> bool:
    try:
        result = opener(command, check=False, capture_output=True, text=True)
    except OSError as exc:
        print(f"Could not open {description}: {exc}", file=sys.stderr)
        return False
    if result.returncode != 0:
        detail = (result.stderr or "").strip()
        suffix = f": {detail}" if detail else f" (exit status {result.returncode})"
        print(f"Could not open {description}{suffix}", file=sys.stderr)
        return False
    return True


def main(argv: Sequence[str] | None = None, *,
         opener: Callable[..., subprocess.CompletedProcess[str]] = subprocess.run) -> int:
    args = parse_args(argv)
    try:
        session = find_latest_session(args.inbox.expanduser())
    except ViewerError as exc:
        print("========================================")
        print(" PhoneSaber Selected Images")
        print("========================================")
        print("Session: (none)")
        print("Selected images: 0")
        print(f"Path:    {args.inbox.expanduser()}")
        print("========================================")
        print(f"Error: {exc}", file=sys.stderr)
        return 2

    image_error: str | None = None
    try:
        images = selected_pngs(session)
    except ViewerError as exc:
        images = []
        image_error = str(exc)

    print("========================================")
    print(" PhoneSaber Selected Images")
    print("========================================")
    print(f"Session: {session.name.removeprefix(SESSION_PREFIX)}")
    print(f"Selected images: {len(images)}")
    print(f"Path:    {session}")
    print("========================================")

    succeeded = True
    if args.finder:
        print("Opening latest session in Finder...", flush=True)
        succeeded = _open(opener, ["/usr/bin/open", "-R", str(session)],
                          "session folder in Finder") and succeeded

    if image_error is not None:
        print(f"Error: {image_error}", file=sys.stderr)
    elif images:
        print("Opening in Preview...", flush=True)
        succeeded = _open(opener, ["/usr/bin/open", "-a", "Preview", *map(str, images)],
                          "selected images in Preview") and succeeded
    else:
        image_error = f"No PNG images were found in the latest session: {session / 'images'}"
        print(image_error, file=sys.stderr)

    if args.report:
        report = session / "analysis_report.md"
        if report.is_file() and not report.is_symlink():
            print("Opening analysis report...", flush=True)
            succeeded = _open(opener, ["/usr/bin/open", str(report)],
                              "analysis report") and succeeded
        else:
            print(f"Analysis report not found: {report}")

    if image_error is not None:
        return 2
    return 0 if succeeded else 1


if __name__ == "__main__":
    raise SystemExit(main())
