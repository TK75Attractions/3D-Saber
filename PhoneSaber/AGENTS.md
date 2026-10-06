# Repository Guidelines

## Project Structure & Module Organization

This directory tracks red/blue illuminated sabers and sends coordinates to the Unity project in its parent directory. The Git and Unity root is `3D-Saber/`; PhoneSaber tools and apps live in `PhoneSaber/`. Clone only `https://github.com/TK75Attractions/3D-Saber.git` with Git LFS installed, then run `PhoneSaber/setup_mac.command`.

- Root Python scripts provide basic (`camera.py`), smartphone-only (`smartphone_camera.py`), ArUco, and stereo tracking.
- `native_capture.py` wraps `native/ContinuityCapture.m`, the macOS AVFoundation adapter. Generated libraries live in ignored `.native-camera/`.
- Root `test_*.py` files contain automated tests; `native/test_capture.m` tests native frame handling.
- `camera_thresholds.json` stores detection settings; `stereo_chessboard.svg` is a calibration asset. Mode-specific Markdown guides document setup.
- `main.cpp` contains Arduino/ESP32 BLE/IMU firmware. Unity and the BLE bridge live in the parent directory (`../Assets/` and `../Tools/`).

## Build, Test, and Development Commands

Run from the `PhoneSaber/` directory using Python 3.12:

```bash
python3 -m venv .venv
source .venv/bin/activate
python3 -m pip install -r requirements.txt
python3 -B -m unittest discover -v
python3 smartphone_camera.py --camera 0 --show
python3 smartphone_camera.py --list-cameras
python3 camera_benchmark.py --optical --output latency-run1.json
git diff --check
```

These commands install dependencies, run tests, launch tracking, enumerate native camera formats, measure optical latency, and check whitespace. Native capture builds automatically with `xcrun clang`; macOS tests require Command Line Tools. No firmware build manifest is supplied.

## Coding Style & Naming Conventions

Use four-space indentation, `snake_case` functions/modules, `PascalCase` classes, and uppercase constants in Python. Preserve adjacent Objective-C style and the firmware's two-space indentation. Keep CLI names hyphenated. No formatter or linter configuration is currently present; avoid unrelated formatting changes.

## Testing Guidelines

Use `unittest`, naming files and methods `test_*`. Add synthetic red/blue regression cases for detector changes and lifecycle/buffer tests for capture changes. No numerical coverage threshold is configured. Native tests skip outside macOS. Hardware permission, actual FPS, clutter resistance, and end-to-end latency require separate real-device verification.

## Commit & Pull Request Guidelines

History contains short ad hoc messages and file-update subjects, not a consistent convention. Prefer descriptive imperative subjects, such as `Fix stale frame handling`. Keep changes focused. PRs should describe behavior, related issues when applicable, commands tested, hardware conditions, and remaining limitations. Include before/after measurements for performance changes and screenshots for detection changes.

## Configuration & Safety

Never equate local resizing or reported FPS with reduced wireless latency. Preserve UDP coordinate compatibility and explicit camera selection. Do not commit `.venv/`, generated binaries, private recordings, stream tokens, or local measurement reports. Preserve unrelated worktree changes.
