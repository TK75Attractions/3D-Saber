"""Codex subprocess capture shared by analysis, repair and review.

Credentials are removed before persistence or display. Logs live outside bundles
and scratch directories, so evidence remains immutable and failures survive cleanup.
"""
from __future__ import annotations

import json
import os
import re
import shlex
import subprocess
import time
import uuid
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

LOG_DIR = Path.home() / "Library/Logs/PhoneSaber/codex"
DISPLAY_TAIL = 16 * 1024
SECRET_KEY = re.compile(r"(?i)(?:api.?key|token|cookie|authorization|password|secret|credential)")
# JSON, TOML, environment assignments, query strings and CLI credential flags.
# Linear-time form of the historical pattern
#   ([\w-]*KEYWORD[\w-]*["']?\s*(?:[:=]|\s)\s*)VALUE
# which backtracked cubically on long [\w-] runs (e.g. 20 KB of "x" took ~12 s).
# Equivalence: a key must end at its [\w-] run end (the next token is a quote,
# whitespace, ':' or '='), and every scan resumes outside a run, so matches
# start at a run start (lookbehind) containing a keyword (lookahead). Of the
# separator splits, only "all whitespace then [:=]" or "whitespace up to the
# next token" can lead to a different VALUE start, tried in the same order.
CREDENTIAL_ASSIGNMENT = re.compile(
    r'(?i)((?<![\w-])(?=[\w-]*?(?:api[_-]?key|token|cookie|authorization|password|secret|credential))'
    r'[\w-]*["\x27]?(?:\s*[:=]\s*|\s+))'
    r'(?:"[^"\n]*"|\x27[^\x27\n]*\x27|[^\s,;&}\n]+)')


def _text(value: str | bytes | None) -> str:
    return value.decode("utf-8", errors="replace") if isinstance(value, bytes) else (value or "")


class Redactor:
    def __init__(self) -> None:
        self.secrets = {v for k, v in os.environ.items() if SECRET_KEY.search(k) and len(v) >= 4}
        home = Path(os.environ.get("CODEX_HOME", str(Path.home() / ".codex")))
        # Do not display these files. Collect only credential values for removal.
        try:
            def collect(value: Any, sensitive: bool = False) -> None:
                if isinstance(value, dict):
                    for key, item in value.items():
                        collect(item, sensitive or bool(SECRET_KEY.search(key)))
                elif isinstance(value, list):
                    for item in value:
                        collect(item, sensitive)
                elif sensitive and isinstance(value, str) and len(value) >= 4:
                    self.secrets.add(value)
            collect(json.loads((home / "auth.json").read_text(encoding="utf-8")))
        except (OSError, ValueError):
            pass
        try:
            config = (home / "config.toml").read_text(encoding="utf-8")
            for match in re.finditer(r'(?m)^\s*([\w-]+)\s*=\s*["\x27]([^"\x27\n]+)["\x27]', config):
                if SECRET_KEY.search(match[1]) and len(match[2]) >= 4:
                    self.secrets.add(match[2])
        except OSError:
            pass

    def __call__(self, value: str | bytes | None) -> str:
        text = _text(value)
        for secret in sorted(self.secrets, key=len, reverse=True):
            text = text.replace(secret, "[REDACTED]")
        text = re.sub(r"\b(?:sk-[A-Za-z0-9_-]+|eyJ[A-Za-z0-9_.-]+)", "[REDACTED]", text)
        text = re.sub(r'(?i)(bearer\s+)[^\s"\x27,]+', r'\1[REDACTED]', text)
        text = re.sub(r'(?i)(https?://)[^\s/@:]+:[^\s/@]+@', r'\1[REDACTED]@', text)
        text = CREDENTIAL_ASSIGNMENT.sub(r'\1"[REDACTED]"', text)
        # Cookie/authorization headers may contain multiple values on the line.
        text = re.sub(r'(?im)^((?:set-cookie|cookie|authorization)\s*:).+$', r'\1 [REDACTED]', text)
        return text

    def value(self, value: Any) -> Any:
        if isinstance(value, dict):
            return {key: ("[REDACTED]" if SECRET_KEY.search(key) else self.value(item))
                    for key, item in value.items()}
        if isinstance(value, list):
            return [self.value(item) for item in value]
        return self(value) if isinstance(value, str) else value


def error_events(*streams: str) -> list[dict[str, Any]]:
    """Decode JSONL or pretty JSON embedded after e.g. ERROR:, retaining errors."""
    decoder = json.JSONDecoder()
    found: list[dict[str, Any]] = []

    def visit(value: Any, in_error: bool = False) -> None:
        if not isinstance(value, dict):
            return
        kind = value.get("type", "")
        active = in_error or kind in {"error", "turn.failed", "response.failed"} or "error" in value
        # Some CLI versions wrap the API's pretty JSON inside an event's
        # message/error string. Decode that payload as well as the outer JSONL.
        if active:
            for key in ("message", "error"):
                nested = value.get(key)
                if isinstance(nested, str) and "{" in nested:
                    try:
                        payload, _ = decoder.raw_decode(nested, nested.index("{"))
                    except (ValueError, RecursionError):  # pathological nesting is not an error event
                        continue
                    if isinstance(payload, dict) and ("error" in payload or "code" in payload):
                        visit(payload, True)
                        return
        if active and any(key in value for key in ("message", "code")):
            event = {key: value[key] for key in ("message", "type", "code", "param", "status") if key in value}
            if event not in found:
                found.append(event)
        if isinstance(value.get("error"), str):
            event = {"message": value["error"], "type": kind}
            if event not in found:
                found.append(event)
        for key in ("error", "response", "item"):
            visit(value.get(key), active)

    for stream in streams:
        position = 0
        while position < len(stream):
            start = stream.find("{", position)
            if start < 0:
                break
            try:
                value, end = decoder.raw_decode(stream, start)
            except RecursionError:
                # Pathological nesting must not abort log saving. Every later "{" of
                # the same run would recurse as deeply, so resume at the next JSONL line.
                newline = stream.find("\n", start)
                position = len(stream) if newline < 0 else newline + 1
                continue
            except ValueError:
                position = start + 1
                continue
            visit(value)
            position = end
    return found


def classify_error(events: list[dict[str, Any]], text: str) -> str:
    normalized = (json.dumps(events) + "\n" + text).casefold()
    if "invalid_json_schema" in normalized:
        return "INVALID_JSON_SCHEMA"
    if re.search(r'\bmodel\b.{0,120}\b(?:unavailable|not available|not supported|unsupported|not found|does not exist|unknown|invalid)\b', normalized, re.DOTALL) \
            or any(term in normalized for term in ("unknown model", "unsupported model", "invalid model", "not a valid model", "model_not_found", "unsupported reasoning effort", "invalid reasoning effort", "unknown reasoning effort", "reasoning effort is not supported", "invalid_reasoning_effort")) \
            or ("model_reasoning_effort" in normalized and any(term in normalized for term in ("invalid", "unsupported", "unknown"))):
        return "MODEL_UNAVAILABLE"
    if any(term in normalized for term in ("usage limit", "rate_limit", "rate limit", "quota_exceeded", "insufficient_quota")):
        return "USAGE_LIMIT"
    if any(term in normalized for term in ("unauthorized", "authentication", "not logged in", "invalid_api_key", "401")):
        return "AUTHENTICATION_FAILED"
    if any(term in normalized for term in ("permission_denied", "permission denied", "403")):
        return "PERMISSION_DENIED"
    return "CLI_FAILED"


class CodexProcessError(RuntimeError):
    def __init__(self, record: "CodexRun") -> None:
        self.record = record
        self.code = record.diagnostic["error_code"]
        super().__init__(record.describe())


@dataclass
class CodexRun:
    completed: subprocess.CompletedProcess[str]
    diagnostic: dict[str, Any]
    log_path: Path
    redactor: Redactor

    def save(self) -> None:
        self.log_path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
        # Unique filenames and restrictive permissions; never write raw streams.
        fd = os.open(self.log_path, os.O_WRONLY | os.O_CREAT | os.O_TRUNC, 0o600)
        with os.fdopen(fd, "w", encoding="utf-8") as output:
            output.write(json.dumps(self.redactor.value(self.diagnostic), ensure_ascii=False, indent=2) + "\n")

    def failure(self, message: str, code: str = "MALFORMED_OUTPUT") -> CodexProcessError:
        self.diagnostic["error_code"] = code
        self.diagnostic["output_error"] = self.redactor(message)
        self.save()
        return CodexProcessError(self)

    def describe(self) -> str:
        d = self.diagnostic
        payload = json.dumps(d["errors"], ensure_ascii=False) if d["errors"] else d.get("output_error", "No structured error event")
        return (f"{d['error_code']}: Codex CLI exit_code={d['exit_code']} elapsed={d['elapsed_seconds']:.2f}s "
                f"timestamp={d['timestamp']} version={d['cli_version']} model={d['model']} effort={d['reasoning_effort']}\n"
                f"command: {d['command']}\nworking directory: {d['working_directory']}\n"
                f"error payload: {payload}\nstdout (redacted tail):\n{d['stdout'][-DISPLAY_TAIL:] or '<empty>'}\n"
                f"stderr (redacted tail):\n{d['stderr'][-DISPLAY_TAIL:] or '<empty>'}\n"
                f"full redacted capture: {self.log_path}")


def run_codex(command: list[str], *, cwd: Path, prompt: str, model: str,
              effort: str, timeout: float, log_dir: Path | None = None) -> CodexRun:
    redactor = Redactor()
    timestamp = datetime.now(timezone.utc).isoformat()
    started = time.monotonic()
    version = "unavailable"
    version_error = ""
    try:
        result = subprocess.run([command[0], "--version"], capture_output=True, text=True,
                                encoding="utf-8", errors="replace", timeout=10, check=False)
        if result.returncode == 0:
            version = redactor(result.stdout.strip())
        else:
            version_error = redactor(result.stderr)
    except (OSError, subprocess.TimeoutExpired) as exc:
        version_error = redactor(str(exc))
    code = ""
    detail = ""
    try:
        completed = subprocess.run(command, cwd=cwd, input=prompt, capture_output=True,
                                   text=True, encoding="utf-8", errors="replace",
                                   timeout=timeout, check=False)
    except subprocess.TimeoutExpired as exc:
        completed = subprocess.CompletedProcess(command, None, _text(exc.stdout), _text(exc.stderr))
        code, detail = "CLI_TIMEOUT", f"Codex CLI timed out after {timeout}s; exit code unavailable"
    except OSError as exc:
        completed = subprocess.CompletedProcess(command, None, "", str(exc))
        code = "EXECUTABLE_MISSING" if isinstance(exc, FileNotFoundError) else "CLI_START_FAILED"
        detail = str(exc)
    stdout, stderr = redactor(completed.stdout), redactor(completed.stderr)
    events = redactor.value(error_events(completed.stdout, completed.stderr))
    if not code and completed.returncode != 0:
        code = classify_error(events, stdout + "\n" + stderr)
    diagnostic = {
        "timestamp": timestamp, "elapsed_seconds": round(time.monotonic() - started, 3),
        "exit_code": completed.returncode, "command": redactor(shlex.join(command)),
        "executable": redactor(command[0]),
        "cli_version": version, "version_error": version_error, "model": model,
        "reasoning_effort": effort, "working_directory": redactor(str(cwd)),
        "timeout_seconds": timeout, "stdin": "prompt via pipe; content omitted",
        "environment": "inherited; values omitted", "environment_names": sorted(os.environ),
        "codex_home": redactor(os.environ.get("CODEX_HOME", str(Path.home() / ".codex"))),
        "stdout": stdout, "stderr": stderr, "errors": events,
        "error_code": code, "output_error": redactor(detail),
    }
    path = (log_dir or LOG_DIR) / (datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S") + "-" + uuid.uuid4().hex + ".json")
    run = CodexRun(completed, diagnostic, path, redactor)
    run.save()
    if code:
        raise CodexProcessError(run)
    return run
