#!/usr/bin/env python3
"""Install the 縁日 workspace files that live outside any Git repository.

The workspace root (two levels above this repo, e.g. ~/縁日) holds AGENTS.md,
PROJECT_STRUCTURE.md and docs/ennichi-camera-system.md, and ~/.claude/CLAUDE.md
holds the user's "always report in Japanese" rule. None of them are in Git, so
a second Mac lacks them. Their copies are kept in workspace/ of this repo.

Non-destructive: a missing file is copied; an identical file is left alone; a
different file is never overwritten — the repo copy is written next to it as
"<name>.from-repo" and reported. `--check` writes nothing.
"""
from __future__ import annotations

import argparse
import shutil
import sys
from pathlib import Path

REPO = Path(__file__).resolve().parents[1]
KIT = REPO / "workspace"
WORKSPACE_FILES = ("AGENTS.md", "PROJECT_STRUCTURE.md", "docs/ennichi-camera-system.md")
GLOBAL_CLAUDE = KIT / "claude" / "global-CLAUDE.md"


def install(source: Path, target: Path, check: bool) -> str:
    if not target.exists():
        if not check:
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source, target)
        return "missing" if check else "installed"
    if target.read_bytes() == source.read_bytes():
        return "same"
    if not check:
        shutil.copy2(source, target.with_name(target.name + ".from-repo"))
    return "differs" if check else "differs (repo copy saved as .from-repo)"


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--workspace", type=Path, default=REPO.parent.parent,
                        help="workspace root (default: two levels above the repo)")
    parser.add_argument("--claude-home", type=Path, default=Path.home() / ".claude",
                        help="Claude Code settings folder (default: ~/.claude)")
    parser.add_argument("--check", action="store_true", help="report only, write nothing")
    args = parser.parse_args(argv)

    pairs = [(KIT / name, args.workspace / name) for name in WORKSPACE_FILES]
    pairs.append((GLOBAL_CLAUDE, args.claude_home / "CLAUDE.md"))
    needs_attention = False
    for source, target in pairs:
        result = install(source, target, args.check)
        needs_attention |= result.startswith(("missing", "differs"))
        print(f"[workspace] {result:<40} {target}")
    if needs_attention:
        print("[workspace] differs: compare the file with its .from-repo copy and merge by hand.")
    return 2 if needs_attention else 0


if __name__ == "__main__":
    sys.exit(main())
