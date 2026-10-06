"""Session context for terminal logs only; never used for retry decisions."""

from __future__ import annotations

import json
from contextlib import contextmanager
from contextvars import ContextVar
from pathlib import Path
from typing import Iterator

from phone_saber_triage_protocol import MAX_SUMMARY_BYTES, SESSION_RE


_context: ContextVar[tuple[str, str]] = ContextVar(
    "phonesaber_log_context", default=("unknown", "manual_retry"))


def _session_id(bundle: Path) -> str:
    """Best-effort attribution even when precheck rejects a damaged bundle."""
    summary = bundle / "summary.json"
    try:
        if not bundle.is_symlink() and not summary.is_symlink() and summary.is_file():
            with summary.open("rb") as stream:
                raw = stream.read(MAX_SUMMARY_BYTES + 1)
            if len(raw) <= MAX_SUMMARY_BYTES:
                value = json.loads(raw)
                session_id = value.get("sessionID") if isinstance(value, dict) else None
                if isinstance(session_id, str) and SESSION_RE.fullmatch(session_id):
                    return session_id
    except (OSError, ValueError, UnicodeError, RecursionError):
        pass
    session_id = bundle.name.removeprefix("phone_saber_triage_")
    return session_id if SESSION_RE.fullmatch(session_id) else "unknown"


@contextmanager
def session_log_context(bundle: Path, *, source: str | None = None) -> Iterator[None]:
    token = _context.set((_session_id(bundle), source or _context.get()[1]))
    try:
        yield
    finally:
        _context.reset(token)


def log_fields(*, session_id: str | None = None, source: str | None = None) -> str:
    current_session, current_source = _context.get()
    return f"sessionID={session_id or current_session} source={source or current_source}"
