#!/usr/bin/env python3.12
"""Reproducible local CPU benchmark for the pinned camera.py revision.

This never opens a camera, creates a socket, or displays a window.  It measures
only synthetic-frame detection plus coordinate-payload construction.  The
baseline is adapted below because revision 3525908a101991d0e735cc5bc8788b87fee619c9
opens a camera/socket and starts worker threads while importing camera.py.
"""

import argparse
import importlib.util
import json
from pathlib import Path
import statistics
import subprocess
import sys
import time

import cv2
import numpy as np


BASELINE_REVISION = "3525908a101991d0e735cc5bc8788b87fee619c9"
FRAME_COUNT = 60
WARMUP_COUNT = 10
FRAME_SHAPE = (240, 320, 3)


def repository_path():
    return Path(__file__).resolve().parent


def synthetic_frames():
    """Return the exact 60 BGR frames used by both paths."""
    frames = []
    for index in range(FRAME_COUNT):
        frame = np.zeros(FRAME_SHAPE, np.uint8)
        x1 = 40 + index % 3
        x2 = 240 + index % 3
        cv2.line(frame, (x1, 210), (x1, 30), (0, 0, 255), 16)
        cv2.line(frame, (x2, 210), (x2, 30), (255, 0, 0), 16)
        frames.append(frame)
    return frames


def load_working_module(repo):
    spec = importlib.util.spec_from_file_location("working_camera", repo / "camera.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def load_baseline_module(repo):
    """Load only baseline definitions, neutralizing its import-time runtime."""
    source = subprocess.check_output(
        ["git", "show", f"{BASELINE_REVISION}:camera.py"], cwd=repo, text=True
    )
    source = source.replace("from python_runtime import reexec_with_cv2\n", "")
    source = source.replace("reexec_with_cv2()", "pass", 1)
    source = source.replace("sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)", "sock = None", 1)
    source = source.replace("cap = open_camera()", "cap = None", 1)
    # Every function needed for the old detection path is defined before this
    # call.  Stop before settings/UI/thread startup, all irrelevant here.
    source = source.split("\nload_threshold_settings()", 1)[0] + "\n"
    namespace = {"__name__": "baseline_camera", "__file__": str(repo / "camera.py")}
    exec(compile(source, str(repo / "camera.py"), "exec"), namespace)
    return namespace


def payload_for_stick(stick, frame_shape, module):
    if stick is None:
        return None
    def value(name):
        return module[name] if isinstance(module, dict) else getattr(module, name)

    height, width = frame_shape[:2]
    orig_w = value("ORIG_CAP_W")
    orig_h = value("ORIG_CAP_H")
    scale_x = (orig_w / width) if orig_w else 1.0
    scale_y = (orig_h / height) if orig_h else 1.0
    points = []
    for x, y in (stick["p1"], stick["p2"]):
        sx = int(round(x * scale_x))
        sy = int(round(y * scale_y))
        if value("FLIP_H"):
            sx = orig_w - sx
        if value("FLIP_V"):
            sy = orig_h - sy
        points.extend((sx, sy))
    return f"{points[0]},{points[1]},{points[2]},{points[3]}".encode("ascii")


def make_path(name, module, frames):
    if name == "working":
        def detect(frame):
            stick1, stick2, _ = module.detect_frame(frame)
            return stick1, stick2
    else:
        def detect(frame):
            (a1, a2), (b1, b2) = module["find_color_pair"](frame)
            return module["build_stick"](a1, a2), module["build_stick"](b1, b2)

    for frame in frames[:WARMUP_COUNT]:
        sticks = detect(frame)
        payload_for_stick(sticks[0], frame.shape, module)
        payload_for_stick(sticks[1], frame.shape, module)

    samples = []
    coordinates = []
    for frame in frames:
        started = time.perf_counter_ns()
        stick1, stick2 = detect(frame)
        payloads = (payload_for_stick(stick1, frame.shape, module),
                    payload_for_stick(stick2, frame.shape, module))
        samples.append((time.perf_counter_ns() - started) / 1_000_000)
        coordinates.append(payloads)
    return {
        "count": len(samples),
        "median_ms": statistics.median(samples),
        "p95_ms": float(np.percentile(samples, 95)),
        "coordinates": sorted({tuple(p.decode("ascii") if p else None for p in pair)
                                for pair in coordinates}),
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--json", action="store_true", help="emit only the JSON report")
    args = parser.parse_args()
    repo = repository_path()
    if subprocess.call(["git", "cat-file", "-e", f"{BASELINE_REVISION}:camera.py"],
                       cwd=repo, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL):
        raise SystemExit(f"missing baseline revision {BASELINE_REVISION}")
    frames = synthetic_frames()
    working = load_working_module(repo)
    baseline = load_baseline_module(repo)
    # Baseline and working code must use the same source-resolution/flip setup.
    working.ORIG_CAP_W = baseline["ORIG_CAP_W"] = 640
    working.ORIG_CAP_H = baseline["ORIG_CAP_H"] = 480
    working.FLIP_H = baseline["FLIP_H"] = 1
    working.FLIP_V = baseline["FLIP_V"] = 1
    report = {
        "measurement": "local CPU detection plus payload construction only; no camera, GUI, UDP, optical, or wireless latency",
        "python": sys.version,
        "opencv": cv2.__version__,
        "numpy": np.__version__,
        "baseline_revision": BASELINE_REVISION,
        "frames": {"shape": list(FRAME_SHAPE), "count": FRAME_COUNT,
                    "warmup": WARMUP_COUNT, "construction": "black BGR; red line x=40+i%3 and blue line x=240+i%3, y=210..30, thickness 16"},
        "results": {"baseline": make_path("baseline", baseline, frames),
                    "working": make_path("working", working, frames)},
    }
    if args.json:
        print(json.dumps(report, ensure_ascii=False, indent=2))
    else:
        print(f"Local CPU only; {FRAME_COUNT} timed samples/path after {WARMUP_COUNT} warmups")
        for name, result in report["results"].items():
            print(f"{name}: median={result['median_ms']:.3f} ms p95={result['p95_ms']:.3f} ms "
                  f"count={result['count']} coordinates={result['coordinates']}")
        print(json.dumps({"python": report["python"], "opencv": report["opencv"],
                          "numpy": report["numpy"], "baseline_revision": BASELINE_REVISION},
                         ensure_ascii=False))


if __name__ == "__main__":
    main()
