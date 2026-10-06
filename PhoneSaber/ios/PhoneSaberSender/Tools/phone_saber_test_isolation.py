"""Keep unit tests out of the operator's real PhoneSaber log directories.

Test modules that can reach ``run_codex`` (fake CLIs, receivers, repair gates)
use these as their module fixtures::

    from phone_saber_test_isolation import isolate_codex_logs as setUpModule
    from phone_saber_test_isolation import restore_codex_logs as tearDownModule

The variable is inherited by subprocesses (for example a receiver started by a
test), so their captures go to the temporary directory too.
"""

from __future__ import annotations

import os
import shutil
import tempfile

from phone_saber_codex_process import LOG_DIR_ENV

_state: list[tuple[str | None, str]] = []


def isolate_codex_logs() -> None:
    directory = tempfile.mkdtemp(prefix="phonesaber-test-codex-logs-")
    _state.append((os.environ.get(LOG_DIR_ENV), directory))
    os.environ[LOG_DIR_ENV] = directory


def restore_codex_logs() -> None:
    if not _state:
        return
    previous, directory = _state.pop()
    if previous is None:
        os.environ.pop(LOG_DIR_ENV, None)
    else:
        os.environ[LOG_DIR_ENV] = previous
    shutil.rmtree(directory, ignore_errors=True)
