"""Low-latency saber tracking assisted by multi-face ArUco markers.

Attach IDs 0-3 around the red saber handle and IDs 4-7 around the blue
saber handle.  A colored blade candidate is accepted only when one endpoint
is close to a marker belonging to that saber.  This rejects similarly colored
clothes and background objects without stereo calibration.
"""

import argparse
import json
import math
import os
import socket
import sys
import threading
import time
from dataclasses import dataclass
from pathlib import Path

from python_runtime import reexec_with_cv2


reexec_with_cv2()

import cv2
import numpy as np


ROOT = Path(__file__).resolve().parent
THRESHOLD_PATH = ROOT / "camera_thresholds.json"
DEFAULT_RED_IDS = (0, 1, 2, 3)
DEFAULT_BLUE_IDS = (4, 5, 6, 7)
DEFAULT_RED_LOWER = np.array([170, 70, 100], dtype=np.uint8)
DEFAULT_RED_UPPER = np.array([10, 255, 255], dtype=np.uint8)
DEFAULT_BLUE_LOWER = np.array([99, 137, 168], dtype=np.uint8)
DEFAULT_BLUE_UPPER = np.array([124, 255, 255], dtype=np.uint8)


@dataclass
class Candidate:
    contour: np.ndarray
    base: tuple[int, int]
    tip: tuple[int, int]
    center: tuple[float, float]
    length: float
    score: float


@dataclass
class MarkerObservation:
    marker_id: int
    center: tuple[float, float]
    side_px: float


class LatestFrame(threading.Thread):
    def __init__(self, capture):
        super().__init__(daemon=True)
        self.capture = capture
        self.lock = threading.Lock()
        self.frame = None
        self.running = True

    def run(self):
        while self.running:
            ok, frame = self.capture.read()
            if not ok or frame is None:
                time.sleep(0.002)
                continue
            with self.lock:
                self.frame = frame

    def get(self):
        with self.lock:
            return None if self.frame is None else self.frame.copy()

    def stop(self):
        self.running = False


def parse_ids(value):
    try:
        ids = tuple(int(part.strip()) for part in value.split(",") if part.strip())
    except ValueError as exc:
        raise argparse.ArgumentTypeError("marker IDs must be comma-separated integers") from exc
    if not ids:
        raise argparse.ArgumentTypeError("at least one marker ID is required")
    return ids


def parse_args():
    parser = argparse.ArgumentParser(
        description="Track red/blue glowing sabers using multi-face ArUco handle markers."
    )
    parser.add_argument("--camera", type=int, default=int(os.environ.get("CAMERA_INDEX", "0")))
    parser.add_argument("--width", type=int, default=640)
    parser.add_argument("--height", type=int, default=360)
    parser.add_argument("--red-ids", type=parse_ids, default=DEFAULT_RED_IDS)
    parser.add_argument("--blue-ids", type=parse_ids, default=DEFAULT_BLUE_IDS)
    parser.add_argument("--marker-grace-ms", type=float, default=180.0)
    parser.add_argument("--send-hz", type=float, default=60.0)
    parser.add_argument("--udp-ip", default="127.0.0.1")
    parser.add_argument("--red-port", type=int, default=5005)
    parser.add_argument("--blue-port", type=int, default=5006)
    parser.add_argument("--flip-h", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--flip-v", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--show", action="store_true")
    return parser.parse_args()


def open_camera(index, width, height):
    if sys.platform == "darwin" and hasattr(cv2, "CAP_AVFOUNDATION"):
        backends = (cv2.CAP_AVFOUNDATION, cv2.CAP_ANY)
    elif os.name == "nt":
        backends = (cv2.CAP_DSHOW, cv2.CAP_MSMF)
    else:
        backends = (cv2.CAP_ANY,)

    for backend in backends:
        capture = cv2.VideoCapture(index, backend)
        if not capture.isOpened():
            capture.release()
            continue
        capture.set(cv2.CAP_PROP_BUFFERSIZE, 1)
        capture.set(cv2.CAP_PROP_FRAME_WIDTH, width)
        capture.set(cv2.CAP_PROP_FRAME_HEIGHT, height)
        ok, frame = capture.read()
        if ok and frame is not None:
            print(
                f"Camera opened: index={index}, backend={backend}, "
                f"size={frame.shape[1]}x{frame.shape[0]}"
            )
            return capture
        capture.release()

    raise RuntimeError(
        f"Camera {index} could not be opened. Check macOS Camera permission and "
        "Continuity Camera, or choose another index with --camera."
    )


def load_thresholds():
    values = {}
    if THRESHOLD_PATH.exists():
        try:
            values = json.loads(THRESHOLD_PATH.read_text(encoding="utf-8"))
        except (OSError, ValueError):
            values = {}

    def hsv(prefix, lower, upper):
        return (
            np.array(
                [
                    values.get(f"{prefix}_h_min", int(lower[0])),
                    values.get(f"{prefix}_s_min", int(lower[1])),
                    values.get(f"{prefix}_v_min", int(lower[2])),
                ],
                dtype=np.uint8,
            ),
            np.array(
                [
                    values.get(f"{prefix}_h_max", int(upper[0])),
                    values.get(f"{prefix}_s_max", int(upper[1])),
                    values.get(f"{prefix}_v_max", int(upper[2])),
                ],
                dtype=np.uint8,
            ),
        )

    red = hsv("a", DEFAULT_RED_LOWER, DEFAULT_RED_UPPER)
    blue = hsv("b", DEFAULT_BLUE_LOWER, DEFAULT_BLUE_UPPER)
    return red, blue, max(20, int(values.get("min_area", 170))), max(
        1, int(values.get("merge_dist", 5))
    )


def hsv_mask(hsv, lower, upper):
    if int(lower[0]) <= int(upper[0]):
        return cv2.inRange(hsv, lower, upper)
    high = cv2.inRange(
        hsv,
        np.array([lower[0], lower[1], lower[2]], dtype=np.uint8),
        np.array([179, upper[1], upper[2]], dtype=np.uint8),
    )
    low = cv2.inRange(
        hsv,
        np.array([0, lower[1], lower[2]], dtype=np.uint8),
        np.array([upper[0], upper[1], upper[2]], dtype=np.uint8),
    )
    return cv2.bitwise_or(high, low)


def create_aruco_detector():
    if not hasattr(cv2, "aruco"):
        raise RuntimeError(
            "This OpenCV build has no ArUco support. Install a current OpenCV with "
            f"'{sys.executable} -m pip install -U opencv-python'."
        )
    dictionary = cv2.aruco.getPredefinedDictionary(cv2.aruco.DICT_4X4_50)
    parameters = cv2.aruco.DetectorParameters()
    parameters.cornerRefinementMethod = cv2.aruco.CORNER_REFINE_SUBPIX
    parameters.minMarkerPerimeterRate = 0.02
    if hasattr(cv2.aruco, "ArucoDetector"):
        detector = cv2.aruco.ArucoDetector(dictionary, parameters)
        return detector.detectMarkers
    return lambda image: cv2.aruco.detectMarkers(image, dictionary, parameters=parameters)


def detect_markers(gray, detect):
    corners, ids, _rejected = detect(gray)
    if ids is None:
        return [], corners, ids

    observations = []
    for marker_corners, marker_id in zip(corners, ids.flatten()):
        points = marker_corners.reshape(4, 2).astype(float)
        edges = np.roll(points, -1, axis=0) - points
        side_px = float(np.linalg.norm(edges, axis=1).mean())
        center = tuple(points.mean(axis=0))
        observations.append(MarkerObservation(int(marker_id), center, side_px))
    return observations, corners, ids


def rect_endpoints(contour):
    (cx, cy), (width, height), angle = cv2.minAreaRect(contour)
    if width >= height:
        theta = math.radians(angle)
        half = width * 0.5
    else:
        theta = math.radians(angle + 90.0)
        half = height * 0.5
    dx = math.cos(theta) * half
    dy = math.sin(theta) * half
    return (int(cx - dx), int(cy - dy)), (int(cx + dx), int(cy + dy))


def point_distance(a, b):
    return math.hypot(float(a[0]) - float(b[0]), float(a[1]) - float(b[1]))


def blade_candidates(mask, min_area, merge_distance):
    close_size = max(1, min(15, merge_distance))
    if close_size % 2 == 0:
        close_size += 1
    close_kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (close_size, close_size))
    processed = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, close_kernel)
    processed = cv2.morphologyEx(
        processed,
        cv2.MORPH_OPEN,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3)),
    )

    frame_area = float(mask.shape[0] * mask.shape[1])
    contours, _ = cv2.findContours(processed, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    candidates = []
    for contour in contours:
        area = float(cv2.contourArea(contour))
        if area < min_area or area / frame_area > 0.20:
            continue
        (_center, (width, height), _angle) = cv2.minAreaRect(contour)
        long_side = max(float(width), float(height), 1.0)
        short_side = max(min(float(width), float(height)), 1.0)
        aspect = long_side / short_side
        extent = area / max(float(width) * float(height), 1.0)
        if aspect < 1.6 or extent < 0.10:
            continue
        p1, p2 = rect_endpoints(contour)
        center = ((p1[0] + p2[0]) * 0.5, (p1[1] + p2[1]) * 0.5)
        score = aspect * 2.0 + extent * 3.0 + min(area / min_area, 8.0) * 0.25
        candidates.append(Candidate(contour, p1, p2, center, long_side, score))
    return candidates, processed


def select_near_markers(candidates, markers):
    if not candidates or not markers:
        return None

    best = None
    best_score = -float("inf")
    for candidate in candidates:
        for marker in markers:
            d1 = point_distance(candidate.base, marker.center)
            d2 = point_distance(candidate.tip, marker.center)
            distance = min(d1, d2)
            max_distance = max(28.0, marker.side_px * 5.0, candidate.length * 0.42)
            if distance > max_distance:
                continue
            base, tip = (
                (candidate.base, candidate.tip)
                if d1 <= d2
                else (candidate.tip, candidate.base)
            )
            score = candidate.score - 4.0 * distance / max_distance
            if score > best_score:
                best_score = score
                best = Candidate(
                    candidate.contour,
                    base,
                    tip,
                    candidate.center,
                    candidate.length,
                    score,
                )
    return best


def encode_candidate(candidate, width, height, flip_h, flip_v):
    points = []
    for x, y in (candidate.base, candidate.tip):
        if flip_h:
            x = width - x
        if flip_v:
            y = height - y
        points.extend((int(x), int(y)))
    return ",".join(str(value) for value in points).encode("ascii")


def draw_candidate(frame, candidate, color, label):
    cv2.drawContours(frame, [candidate.contour], -1, color, 1)
    cv2.circle(frame, candidate.base, 6, (0, 255, 255), -1)
    cv2.circle(frame, candidate.tip, 6, color, -1)
    cv2.line(frame, candidate.base, candidate.tip, color, 2)
    cv2.putText(
        frame,
        label,
        (candidate.base[0] + 8, candidate.base[1] - 8),
        cv2.FONT_HERSHEY_SIMPLEX,
        0.55,
        color,
        2,
        cv2.LINE_AA,
    )


def main():
    args = parse_args()
    red_range, blue_range, min_area, merge_distance = load_thresholds()
    detect_aruco = create_aruco_detector()
    capture = open_camera(args.camera, args.width, args.height)
    grabber = LatestFrame(capture)
    grabber.start()
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    grace_seconds = max(0.0, args.marker_grace_ms / 1000.0)
    last_detection = {"red": None, "blue": None}
    last_seen = {"red": 0.0, "blue": 0.0}
    last_payload = {"red": None, "blue": None}
    ports = {"red": args.red_port, "blue": args.blue_port}
    id_groups = {"red": set(args.red_ids), "blue": set(args.blue_ids)}
    ranges = {"red": red_range, "blue": blue_range}
    interval = 1.0 / max(args.send_hz, 1.0)
    next_frame = time.perf_counter()

    print(f"Red marker IDs: {sorted(id_groups['red'])}; blue marker IDs: {sorted(id_groups['blue'])}")
    print("Q quits. Yellow dot = handle/base; colored dot = blade tip.")

    try:
        while True:
            now = time.perf_counter()
            if now < next_frame:
                time.sleep(min(0.001, next_frame - now))
                continue
            next_frame = max(next_frame + interval, now)

            frame = grabber.get()
            if frame is None:
                time.sleep(0.002)
                continue
            height, width = frame.shape[:2]
            gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
            observations, marker_corners, marker_ids = detect_markers(gray, detect_aruco)
            hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
            display = frame.copy()
            processed_masks = {}

            if marker_ids is not None:
                cv2.aruco.drawDetectedMarkers(display, marker_corners, marker_ids)

            for name, draw_color in (("red", (0, 0, 255)), ("blue", (255, 0, 0))):
                lower, upper = ranges[name]
                mask = hsv_mask(hsv, lower, upper)
                candidates, processed_masks[name] = blade_candidates(
                    mask, min_area, merge_distance
                )
                matching_markers = [
                    marker for marker in observations if marker.marker_id in id_groups[name]
                ]
                selected = select_near_markers(candidates, matching_markers)
                if selected is not None:
                    last_detection[name] = selected
                    last_seen[name] = now
                elif now - last_seen[name] <= grace_seconds:
                    selected = last_detection[name]
                else:
                    last_detection[name] = None

                if selected is None:
                    continue
                payload = encode_candidate(
                    selected, width, height, args.flip_h, args.flip_v
                )
                # The processing loop itself is the fixed-rate UDP heartbeat.
                udp.sendto(payload, (args.udp_ip, ports[name]))
                last_payload[name] = payload
                if args.show:
                    draw_candidate(display, selected, draw_color, name)

            if args.show:
                cv2.imshow("ArUco saber tracking", display)
                cv2.imshow("red candidates", processed_masks["red"])
                cv2.imshow("blue candidates", processed_masks["blue"])
                if cv2.waitKey(1) & 0xFF in (ord("q"), 27):
                    break
    except KeyboardInterrupt:
        pass
    finally:
        grabber.stop()
        grabber.join(timeout=1.0)
        capture.release()
        udp.close()
        if args.show:
            cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
