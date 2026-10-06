#!/usr/bin/env python3
"""Run the production Swift detector on a video and render its decisions.

Requires ffmpeg/ffprobe, Xcode command-line tools, numpy, and opencv-python.
Generated output is diagnostic-only and never enters the iPhone hot path.
"""

from __future__ import annotations

import argparse
import json
import math
import statistics
import subprocess
import tempfile
from pathlib import Path

import cv2


COLORS = {"red": (0, 0, 255), "blue": (255, 150, 0)}


def command_output(*arguments: str) -> str:
    return subprocess.check_output(arguments, text=True).strip()


def axis_error(actual: list[dict[str, int]], expected: list[list[int]]) -> float:
    a = [(point["x"], point["y"]) for point in actual]
    direct = math.dist(a[0], expected[0]) + math.dist(a[1], expected[1])
    reverse = math.dist(a[0], expected[1]) + math.dist(a[1], expected[0])
    return min(direct, reverse) / 2


def draw_axis(frame, endpoints, color, width: int) -> None:
    points = [(int(point["x"]), int(point["y"])) if isinstance(point, dict)
              else (int(point[0]), int(point[1])) for point in endpoints]
    cv2.line(frame, points[0], points[1], color, width, cv2.LINE_AA)
    if width >= 3:
        cv2.circle(frame, points[0], width + 2, (255, 255, 255), -1)
        cv2.circle(frame, points[1], width + 2, (255, 255, 255), -1)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("video", type=Path)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--annotations", type=Path)
    parser.add_argument("--sample-step", type=int, default=2)
    arguments = parser.parse_args()
    arguments.output.mkdir(parents=True, exist_ok=True)

    test_directory = Path(__file__).resolve().parent
    source_directory = test_directory.parent / "PhoneSaberSender" / "PhoneSaberSender"
    probe = json.loads(command_output(
        "ffprobe", "-v", "error", "-select_streams", "v:0",
        "-show_entries", "stream=width,height,avg_frame_rate", "-of", "json",
        str(arguments.video),
    ))["streams"][0]
    width, height = int(probe["width"]), int(probe["height"])
    numerator, denominator = map(int, probe["avg_frame_rate"].split("/"))
    fps = numerator / denominator

    detections_path = arguments.output / "detections.jsonl"
    with tempfile.TemporaryDirectory(prefix="phonesaber-video-") as temporary:
        binary = Path(temporary) / "video-detection-diagnostic"
        subprocess.run([
            "xcrun", "swiftc", "-O",
            str(source_directory / "DetectionCore.swift"),
            str(source_directory / "BGRADetection.swift"),
            str(test_directory / "VideoDetectionDiagnostic.swift"),
            "-module-cache-path", str(Path(temporary) / "module-cache"),
            "-o", str(binary),
        ], check=True)
        decoder = subprocess.Popen([
            "ffmpeg", "-hide_banner", "-loglevel", "error", "-i", str(arguments.video),
            "-an", "-f", "rawvideo", "-pix_fmt", "bgra", "-",
        ], stdout=subprocess.PIPE)
        with detections_path.open("wb") as detections:
            detector = subprocess.Popen(
                [str(binary), str(width), str(height), str(arguments.sample_step)],
                stdin=decoder.stdout, stdout=detections,
            )
            assert decoder.stdout is not None
            decoder.stdout.close()
            detector_result = detector.wait()
            decoder_result = decoder.wait()
        if detector_result or decoder_result:
            raise RuntimeError(f"detector={detector_result}, decoder={decoder_result}")

    rows = [json.loads(line) for line in detections_path.read_text().splitlines()]
    annotations = {}
    if arguments.annotations:
        document = json.loads(arguments.annotations.read_text())
        annotations = {entry["frame"]: entry for entry in document["frames"]}

    capture = cv2.VideoCapture(str(arguments.video))
    writer = cv2.VideoWriter(
        str(arguments.output / "annotated.mp4"), cv2.VideoWriter_fourcc(*"mp4v"),
        fps, (width, height),
    )
    errors: list[float] = []
    successes = 0
    evaluated = 0
    frame_index = 0
    while True:
        ok, frame = capture.read()
        if not ok:
            break
        result = rows[frame_index]
        for name, color in COLORS.items():
            candidates = result["colors"][name]["candidates"]
            for candidate_index, candidate in enumerate(candidates):
                if candidate["eligible"]:
                    draw_axis(frame, candidate["endpoints"], tuple(channel // 2 for channel in color), 1)
                    label = f"{name[0].upper()}{candidate_index + 1}:{candidate['score']:.1f}"
                    point = candidate["endpoints"][0]
                    cv2.putText(frame, label, (point["x"], point["y"]),
                                cv2.FONT_HERSHEY_SIMPLEX, .34, color, 1, cv2.LINE_AA)
            selected = result["colors"][name]["selected"]
            if selected:
                draw_axis(frame, selected, color, 3)

        annotation = annotations.get(frame_index)
        if annotation:
            for name in COLORS:
                expected = annotation.get(name)
                if isinstance(expected, list):
                    draw_axis(frame, expected, (0, 255, 0), 2)
                    evaluated += 1
                    selected = result["colors"][name]["selected"]
                    if selected:
                        error = axis_error(selected, expected)
                        errors.append(error)
                        successes += error <= 24
            cv2.imwrite(str(arguments.output / f"frame-{frame_index:04d}-annotated.png"), frame)
        profile = result["profile"]
        cv2.putText(frame, f"frame {frame_index}  detection {profile['total_ms']:.2f} ms",
                    (10, 26), cv2.FONT_HERSHEY_SIMPLEX, .55, (255, 255, 255), 2, cv2.LINE_AA)
        writer.write(frame)
        frame_index += 1
    capture.release()
    writer.release()

    timings = [row["profile"]["total_ms"] for row in rows]
    summary = {
        "video": str(arguments.video),
        "frames": len(rows),
        "fps": fps,
        "sample_step": arguments.sample_step,
        "evaluated_axes": evaluated,
        "successful_axes_at_24px": successes,
        "success_rate": successes / evaluated if evaluated else None,
        "mean_endpoint_error_px": statistics.fmean(errors) if errors else None,
        "median_endpoint_error_px": statistics.median(errors) if errors else None,
        "detection_time_median_ms": statistics.median(timings),
        "detection_time_max_ms": max(timings),
        "red_detected_frames": sum(row["colors"]["red"]["selected"] is not None for row in rows),
        "blue_detected_frames": sum(row["colors"]["blue"]["selected"] is not None for row in rows),
    }
    (arguments.output / "summary.json").write_text(json.dumps(summary, indent=2) + "\n")
    print(json.dumps(summary, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
