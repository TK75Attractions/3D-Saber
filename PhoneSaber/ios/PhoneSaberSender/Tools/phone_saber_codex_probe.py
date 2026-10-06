#!/usr/bin/env python3
"""Probe the pinned analysis CLI without a PhoneSaber bundle or production edits."""
from __future__ import annotations

import argparse
import json
import tempfile
from pathlib import Path

from phone_saber_codex_process import CodexProcessError, run_codex
from phone_saber_triage_codex import (
    ANALYSIS_MODEL, ANALYSIS_REASONING_EFFORT, _output_schema, find_codex_binary,
)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--codex-path")
    parser.add_argument("--timeout", type=int, default=120)
    args = parser.parse_args()
    binary = find_codex_binary(args.codex_path) or (args.codex_path or "codex")
    with tempfile.TemporaryDirectory(prefix="phonesaber-codex-probe-") as name:
        root = Path(name)
        working = root / "input"
        working.mkdir()
        schema = root / "analysis_schema.json"
        response = root / "codex_response.json"
        schema.write_text(json.dumps(_output_schema(("probe_image",))), encoding="utf-8")
        command = [binary, "exec", "--json", "--model", ANALYSIS_MODEL, "-c",
                   f"model_reasoning_effort={json.dumps(ANALYSIS_REASONING_EFFORT)}",
                   "--ephemeral", "--sandbox", "read-only", "--skip-git-repo-check",
                   "--cd", str(working), "--output-schema", str(schema),
                   "--output-last-message", str(response), "-"]
        try:
            capture = run_codex(command, cwd=working, prompt="Reply with OK.",
                                model=ANALYSIS_MODEL, effort=ANALYSIS_REASONING_EFFORT,
                                timeout=args.timeout)
            try:
                value = json.loads(response.read_text(encoding="utf-8"))
                if value.get("session_summary") != "OK":
                    raise ValueError("session_summary must be OK")
            except (OSError, ValueError, AttributeError) as exc:
                raise capture.failure(f"CLI probe returned invalid output: {exc}") from exc
        except CodexProcessError as exc:
            print(str(exc), flush=True)
            return 1
        print(f"CLI_PROBE_OK exit_code=0 version={capture.diagnostic['cli_version']} "
              f"model={ANALYSIS_MODEL} effort={ANALYSIS_REASONING_EFFORT} "
              f"sandbox=read-only elapsed={capture.diagnostic['elapsed_seconds']:.2f}s "
              f"capture={capture.log_path}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
