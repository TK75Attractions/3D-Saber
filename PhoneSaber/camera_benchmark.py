"""Compare Continuity capture paths using an optical round trip, without Larix.

The changing marker is displayed on the Mac and filmed by the iPhone. This
measures display submission -> frame available on Mac, NOT isolated wireless
latency. Marker/display refresh quantization remains; inspect distributions.
In optical mode, a clearly labeled live preview of the latest captured BGR
frame is shown beside the marker. Keep that preview outside the iPhone's view
to avoid recursive marker images. No camera images are saved.
Preview rendering can affect subsequent scheduling. Local processing timing
alone cannot establish an overall improvement because camera acquisition,
transfer, and optical latency may dominate.
"""

import argparse
import json
import math
from pathlib import Path
import time

from python_runtime import reexec_with_cv2

reexec_with_cv2()

import cv2
import numpy as np

from smartphone_camera import LatestFrame, close_frame_source, open_camera, processing_frame


DICTIONARY = cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_1000)
TARGET_TITLE = "Latency target - aim the iPhone here - Q quits"
PREVIEW_TITLE = "Live BGR preview - keep outside iPhone view"
PREVIEW_MAX_WIDTH = 640
PREVIEW_MAX_HEIGHT = 480
WARMUP_SECONDS = 3
WARMUP_MARKER_ID = 999
FPS_TOLERANCE = 0.02


def fps_matches_request(measured, requested):
    """Accept small measurement jitter without conflating 30 and 60 FPS."""
    if not math.isfinite(measured) or not math.isfinite(requested) or requested <= 0:
        return False
    tolerance = requested * FPS_TOLERANCE
    rounding_slack = 1e-12 * max(1.0, requested)
    return abs(measured - requested) <= tolerance + rounding_slack


def marker_target(marker_id, label, elapsed):
    target = np.full((600, 640, 3), 255, np.uint8)
    marker = cv2.aruco.generateImageMarker(DICTIONARY, marker_id, 384)
    target[92:476, 128:512] = cv2.cvtColor(marker, cv2.COLOR_GRAY2BGR)
    cv2.putText(target, label, (20, 32), cv2.FONT_HERSHEY_SIMPLEX, .65, (0, 0, 0), 2)
    cv2.putText(target, f"{elapsed:06.2f} s  /  {marker_id:03d}", (140, 540),
                cv2.FONT_HERSHEY_SIMPLEX, 1.1, (0, 0, 0), 2)
    return target


def preview_frame(frame):
    """Return a labeled, aspect-correct display copy bounded for the preview."""
    height, width = frame.shape[:2]
    scale = min(PREVIEW_MAX_WIDTH / width, PREVIEW_MAX_HEIGHT / height, 1.0)
    preview = frame
    if scale < 1:
        preview = cv2.resize(frame, (max(1, round(width * scale)),
                                     max(1, round(height * scale))),
                             interpolation=cv2.INTER_AREA)
    preview = preview.copy()
    cv2.putText(preview, "LIVE CAPTURED BGR FRAME", (12, 28),
                cv2.FONT_HERSHEY_SIMPLEX, .65, (0, 255, 255), 2)
    return preview


class OpticalSamples:
    def __init__(self):
        self.issued = {}
        self.seen = set()
        self.samples = []

    def record(self, marker_id, available, consumed):
        if marker_id not in self.issued or marker_id in self.seen:
            return False
        submitted = self.issued[marker_id]
        age = available - submitted
        if not (0 <= age <= 2) or consumed < available:
            return False
        self.seen.add(marker_id)
        self.samples.append({"id": marker_id, "display_to_available_ms": age * 1000,
                             "display_to_consumer_ms": (consumed - submitted) * 1000,
                             "available_to_consumer_ms": (consumed - available) * 1000})
        return True

    def summary(self):
        if not self.samples:
            return {"count": 0, "valid": False}
        # Same endpoint for both backends: BGR frame returned to the caller.
        # Native callback time is earlier than OpenCV read completion time.
        values = np.array([s["display_to_consumer_ms"] for s in self.samples])
        return {"count": len(values), "valid": len(values) >= 60,
                "p10_ms": float(np.percentile(values, 10)),
                "median_ms": float(np.median(values)),
                "p90_ms": float(np.percentile(values, 90)),
                "consumer_median_ms": float(np.median([
                    s["available_to_consumer_ms"] for s in self.samples]))}


def measure(backend, camera_index, device_id, width, height, fps, seconds, optical):
    label = f"{backend} {width}x{height} {fps} FPS"
    if backend == "native":
        from native_capture import NativeLatestFrame
        capture = NativeLatestFrame(device_id, width, height, fps)
        latest = capture
        info = capture.info
    else:
        capture = open_camera(camera_index, width, height, fps)
        latest = LatestFrame(capture)
        info = {"opencv_index": camera_index, "reported_fps": capture.get(cv2.CAP_PROP_FPS)}
    latest.start()
    detector = cv2.aruco.ArucoDetector(DICTIONARY)
    last_sequence = -1
    try:
        if optical:
            cv2.namedWindow(TARGET_TITLE, cv2.WINDOW_AUTOSIZE)
            cv2.namedWindow(PREVIEW_TITLE, cv2.WINDOW_AUTOSIZE)
            cv2.moveWindow(TARGET_TITLE, 0, 0)
            cv2.moveWindow(PREVIEW_TITLE, 660, 0)

            warmup_target = marker_target(WARMUP_MARKER_ID, f"{label} - WARM-UP", 0)
            cv2.imshow(TARGET_TITLE, warmup_target)
            key = cv2.waitKey(1) & 0xFF
            if key in (ord("q"), 27):
                raise KeyboardInterrupt
            warmup_deadline = time.perf_counter() + WARMUP_SECONDS
            while time.perf_counter() < warmup_deadline:
                frame, _available, sequence = latest.get(last_sequence)
                _consumed = time.perf_counter()
                if frame is not None:
                    last_sequence = sequence
                    cv2.imshow(PREVIEW_TITLE, preview_frame(frame))
                key = cv2.waitKey(1) & 0xFF
                if key in (ord("q"), 27):
                    raise KeyboardInterrupt
                if frame is None:
                    time.sleep(.001)

        samples = OpticalSamples()
        started = time.perf_counter()
        last_arrival = started
        next_target = started
        marker_id = 0
        first_sequence = None
        arrival_times, ages, shapes, target_intervals = [], [], set(), []
        last_target_time = None
        while time.perf_counter() - started < seconds:
            now = time.perf_counter()
            if optical and now >= next_target and marker_id < 1000:
                target = marker_target(marker_id, label, now - started)
                submitted = time.perf_counter()
                cv2.imshow(TARGET_TITLE, target)
                samples.issued[marker_id] = submitted
                if last_target_time is not None:
                    target_intervals.append((submitted - last_target_time) * 1000)
                last_target_time = submitted
                marker_id += 1
                if marker_id == WARMUP_MARKER_ID:
                    marker_id += 1
                # Never reuse an ID or queue up target updates after a slow frame.
                next_target = submitted + 1 / 60
            frame, available, sequence = latest.get(last_sequence)
            consumed = time.perf_counter()
            if frame is not None:
                last_sequence = sequence
                if optical and available < started:
                    frame = None
                else:
                    last_arrival = consumed
                    shapes.add((frame.shape[1], frame.shape[0]))
                    if first_sequence is None:
                        first_sequence = sequence
                    arrival_times.append(available)
                    ages.append((consumed - available) * 1000)
                if frame is not None and optical:
                    small = processing_frame(frame, 640, 480)
                    corners, ids, _ = detector.detectMarkers(small)
                    if ids is None:
                        corners, ids, _ = detector.detectMarkers(cv2.flip(small, 1))
                    if ids is not None:
                        candidates = sorted(zip(corners, ids.flatten()),
                            key=lambda item: cv2.contourArea(item[0]), reverse=True)
                        for _corners, found_id in candidates:
                            if samples.record(int(found_id), available, consumed):
                                break
                    cv2.imshow(PREVIEW_TITLE, preview_frame(frame))
            if frame is None and consumed - last_arrival > 5:
                raise RuntimeError("No fresh frames for 5 seconds; connection/permission test failed")
            if optical and cv2.waitKey(1) & 0xFF in (ord("q"), 27):
                raise KeyboardInterrupt
            if frame is None:
                time.sleep(.001)
        if len(arrival_times) < 2:
            raise RuntimeError("Too few frames to measure")
        duration = arrival_times[-1] - arrival_times[0]
        input_fps = (last_sequence - first_sequence) / duration if duration > 0 else 0
        intervals = np.diff(arrival_times) * 1000
        summary = samples.summary()
        measured_sizes = sorted(shapes)
        size_valid = measured_sizes == [(width, height)]
        # Validate the measured callback stream against the requested rate. A
        # narrow relative tolerance covers timing/sample-count quantization,
        # while keeping the 30 and 60 FPS profiles distinct.
        fps_valid = fps_matches_request(input_fps, fps)
        warnings = []
        if not size_valid:
            warnings.append(f"実フレーム寸法が要求値 {width}x{height} と不一致: {measured_sizes}")
        if not fps_valid:
            warnings.append(f"実測FPS {input_fps:.3f} が要求値 {fps:.3f} の未達")
        if fps == 60 and input_fps < 30:
            warnings.append(f"60FPS要求なのに実測FPSが30未満: {input_fps:.3f}")
        valid = size_valid and fps_valid
        summary["valid"] = summary["valid"] and valid
        if not valid:
            summary["warning"] = "; ".join(warnings)
        return {"profile": label, "camera": info, "frames_consumed": len(arrival_times),
                "requested": {"width": width, "height": height, "fps": fps},
                "actual_sizes": measured_sizes, "input_fps": input_fps,
                "measured": {"frame_sizes": measured_sizes, "fps": input_fps,
                             "valid": valid, "warnings": warnings},
                "valid": valid,
                "consumer_fps": (len(arrival_times) - 1) / max(duration, .001),
                "frame_interval_p90_ms": float(np.percentile(intervals, 90)),
                "since_available_median_ms": float(np.median(ages)),
                "target_interval_p90_ms": (float(np.percentile(target_intervals, 90))
                                            if target_intervals else None),
                "optical": summary, "samples": samples.samples}
    finally:
        close_frame_source(latest, capture)
        if optical:
            for title in (TARGET_TITLE, PREVIEW_TITLE):
                try:
                    cv2.destroyWindow(title)
                except cv2.error:
                    pass


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--camera", type=int, default=0, help="OpenCV index of the same iPhone")
    parser.add_argument("--device-id", default="continuity")
    parser.add_argument("--seconds", type=float, default=12)
    parser.add_argument("--optical", action="store_true",
                        help="Show marker and live BGR preview; frame marker while keeping preview outside iPhone view")
    parser.add_argument("--profile", choices=("compare", "opencv", "native30", "native60", "native720"), default="compare")
    parser.add_argument("--output", type=Path, help="New JSON report path; never saves camera images")
    args = parser.parse_args()
    if not math.isfinite(args.seconds) or not 5 <= args.seconds <= 15:
        parser.error("seconds must be between 5 and 15 (avoid marker ID reuse)")
    profiles = {"opencv": ("opencv", 640, 360, 30),
                "native30": ("native", 640, 480, 30),
                "native60": ("native", 640, 480, 60),
                "native720": ("native", 1280, 720, 30)}
    selected = list(profiles) if args.profile == "compare" else [args.profile]
    report = {"measurement": "display submission to BGR frame returned to Python; includes display phase, exposure, transfer and retrieval, excludes tracking and preview presentation",
              "warning": "Not isolated wireless latency. Preview rendering can affect subsequent scheduling, and local processing timing alone cannot establish overall improvement when camera acquisition, transfer, and optical latency may dominate. 60+ decoded IDs and >=85% requested FPS required. Compare multiple runs with fixed camera/light/effects. Lower resolution still needs red/blue detection validation.",
              "results": []}
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        # Reserve before opening a camera; don't overwrite a previous measurement.
        output_file = args.output.open("x", encoding="utf-8")
    else:
        output_file = None
    print("Stop other camera apps. Keep the same iPhone fixed throughout the comparison.", flush=True)
    if args.optical:
        print("Aim the iPhone at the large marker. Keep the live preview outside the iPhone's view to avoid recursion. No recording. Q/Escape stops.", flush=True)
    try:
        for key in selected:
            backend, width, height, fps = profiles[key]
            print(f"Testing {key}...", flush=True)
            try:
                result = measure(backend, args.camera, args.device_id, width, height,
                                 fps, args.seconds, args.optical)
            except (RuntimeError, OSError) as error:
                result = {"profile": key, "error": str(error)}
            report["results"].append(result)
            print(json.dumps({k: v for k, v in result.items() if k != "samples"}, ensure_ascii=False), flush=True)
    except KeyboardInterrupt:
        report["interrupted"] = True
    finally:
        if args.optical:
            cv2.destroyAllWindows()
        if output_file:
            with output_file:
                json.dump(report, output_file, indent=2, ensure_ascii=False)
                output_file.write("\n")
            print(f"Report: {args.output.resolve()}")
    if report.get("interrupted"):
        if args.output:
            print("Comparison stopped by user; report saved without an insufficient-samples failure.", flush=True)
        else:
            print("Comparison stopped by user; no report file was requested.", flush=True)
        return
    if not any(r.get("valid", False) for r in report["results"]):
        raise SystemExit("No successful camera measurements; no latency improvement has been verified.")
    if args.optical and not any(r.get("optical", {}).get("valid") for r in report["results"]):
        raise SystemExit("Insufficient optical samples/FPS. Aim the camera at the marker and rerun; no improvement verified.")
    if not args.optical:
        print("FPS/read timing only. Add --optical to compare delays including camera transfer.")


if __name__ == "__main__":
    main()
