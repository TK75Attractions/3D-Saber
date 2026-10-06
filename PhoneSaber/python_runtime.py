"""Select a local Python environment that contains OpenCV before importing it."""

import importlib.util
import os
import subprocess
import sys
from pathlib import Path


def _has_cv2(interpreter):
    try:
        result = subprocess.run(
            [interpreter, "-c", "import cv2, numpy"],
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            check=False,
            timeout=8,
        )
        return result.returncode == 0
    except (OSError, subprocess.SubprocessError):
        return False


def reexec_with_cv2():
    """Re-exec this script with the project's working Python when needed."""
    if importlib.util.find_spec("cv2") is not None:
        return

    project_dir = Path(__file__).resolve().parent
    candidates = []
    requested = os.environ.get("CAMERA_PYTHON")
    if requested:
        candidates.append(Path(requested))
    candidates.extend([
        project_dir / ".venv" / "bin" / "python",
        Path.home() / ".pyenv" / "versions" / "3.12.1" / "bin" / "python3",
        Path.home() / ".pyenv" / "shims" / "python3",
    ])

    current = Path(sys.executable).resolve()
    for candidate in candidates:
        if candidate.exists() and candidate.resolve() != current and _has_cv2(str(candidate)):
            os.execv(str(candidate), [str(candidate), *sys.argv])

    raise RuntimeError(
        "OpenCV is not available in this Python. Install dependencies with "
        f"'{sys.executable} -m pip install -r {project_dir / 'requirements.txt'}'."
    )
