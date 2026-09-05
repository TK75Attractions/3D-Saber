"""Low-latency single-smartphone saber tracker with temporal safeguards.

This experimental tracker deliberately does not use the Mac camera or stereo
calibration. New tracks need consecutive detections, active tracks reject
large jumps, and short gaps are predicted from visual velocity plus optional
IMU gyro data mirrored by mac_ble_udp_bridge.py.
"""

import argparse
import json
import math
import os
import socket
import sys
import threading
import time
from dataclasses import dataclass, replace
from pathlib import Path

from python_runtime import reexec_with_cv2


reexec_with_cv2()

import cv2
import numpy as np


ROOT = Path(__file__).resolve().parent
THRESHOLD_PATH = ROOT / "camera_thresholds.json"
DEFAULT_RANGES = {
    "red": (
        np.array([170, 70, 100], dtype=np.uint8),
        np.array([10, 255, 255], dtype=np.uint8),
    ),
    "blue": (
        np.array([99, 137, 168], dtype=np.uint8),
        np.array([124, 255, 255], dtype=np.uint8),
    ),
}


@dataclass
class Detection:
    p1: tuple[float, float]
    p2: tuple[float, float]
    center: tuple[float, float]
    length: float
    angle: float
    score: float
    contour: np.ndarray | None = None
    predicted: bool = False


class LatestFrame(threading.Thread):
    def __init__(self, capture):
        super().__init__(daemon=True)
        self.capture = capture
        self.lock = threading.Lock()
        self.frame = None
        self.timestamp = 0.0
        self.sequence = 0
        self.running = True

    def run(self):
        while self.running:
            ok, frame = self.capture.read()
            if not ok or frame is None:
                time.sleep(0.002)
                continue
            with self.lock:
                self.frame = frame
                self.timestamp = time.perf_counter()
                self.sequence += 1

    def get(self):
        with self.lock:
            if self.frame is None:
                return None, 0.0, self.sequence
            return self.frame.copy(), self.timestamp, self.sequence

    def stop(self):
        self.running = False


class ImuReceiver(threading.Thread):
    def __init__(self, host, port):
        super().__init__(daemon=True)
        self.sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.sock.settimeout(0.1)
        self.sock.bind((host, port))
        self.lock = threading.Lock()
        self.gyro = (0.0, 0.0, 0.0)
        self.timestamp = 0.0
        self.running = True

    def run(self):
        while self.running:
            try:
                packet, _address = self.sock.recvfrom(512)
            except socket.timeout:
                continue
            except OSError:
                break
            text = packet.decode("utf-8", errors="ignore").strip()
            if text.startswith("IMU:"):
                text = text[4:]
            values = text.split(",")
            if len(values) < 6:
                continue
            try:
                gyro = tuple(float(value) for value in values[3:6])
            except ValueError:
                continue
            with self.lock:
                self.gyro = gyro
                self.timestamp = time.perf_counter()

    def angular_rate(self, axis, sign, scale, now):
        with self.lock:
            gyro = self.gyro
            timestamp = self.timestamp
        if now - timestamp > 0.15:
            return None
        return gyro[{"x": 0, "y": 1, "z": 2}[axis]] * sign * scale

    def stop(self):
        self.running = False
        self.sock.close()


def angle_delta(target, source):
    """Return signed stick-axis difference in degrees, modulo 180."""
    return (target - source + 90.0) % 180.0 - 90.0


def point_distance(a, b):
    return math.hypot(a[0] - b[0], a[1] - b[1])


def endpoints_from_center(center, length, angle):
    theta = math.radians(angle)
    dx = math.cos(theta) * length * 0.5
    dy = math.sin(theta) * length * 0.5
    return (
        (center[0] - dx, center[1] - dy),
        (center[0] + dx, center[1] + dy),
    )


class StickTracker:
    def __init__(
        self,
        name,
        frame_diagonal,
        confirm_frames,
        confirm_seconds,
        acquire_min_score,
        prediction_seconds,
        lost_seconds,
        jump_base_pixels,
        jump_ratio_per_second,
        imu_blend,
    ):
        self.name = name
        self.frame_diagonal = frame_diagonal
        self.confirm_frames = confirm_frames
        self.confirm_seconds = confirm_seconds
        self.acquire_min_score = acquire_min_score
        self.prediction_seconds = prediction_seconds
        self.lost_seconds = lost_seconds
        self.jump_base_pixels = jump_base_pixels
        self.jump_ratio_per_second = jump_ratio_per_second
        self.imu_blend = imu_blend
        self.active = False
        self.pending = None
        self.pending_count = 0
        self.pending_since = 0.0
        self.last_observed = None
        self.last_observed_time = 0.0
        self.last_output = None
        self.center_velocity = (0.0, 0.0)
        self.angular_velocity = 0.0
        self.rejected_jumps = 0

    def _jump_limit(self, elapsed):
        return self.jump_base_pixels + self.frame_diagonal * self.jump_ratio_per_second * elapsed

    def _orient_like_previous(self, detection):
        if self.last_output is None:
            return detection
        direct = point_distance(detection.p1, self.last_output.p1) + point_distance(
            detection.p2, self.last_output.p2
        )
        swapped = point_distance(detection.p2, self.last_output.p1) + point_distance(
            detection.p1, self.last_output.p2
        )
        if swapped < direct:
            return replace(detection, p1=detection.p2, p2=detection.p1)
        return detection

    def _select_active(self, candidates, now):
        if self.last_observed is None:
            return None
        elapsed = max(1.0 / 240.0, now - self.last_observed_time)
        predicted_center = (
            self.last_observed.center[0] + self.center_velocity[0] * elapsed,
            self.last_observed.center[1] + self.center_velocity[1] * elapsed,
        )
        jump_limit = self._jump_limit(elapsed)
        valid = []
        for candidate in candidates:
            distance = point_distance(candidate.center, predicted_center)
            if distance > jump_limit:
                continue
            angle_error = abs(angle_delta(candidate.angle, self.last_observed.angle))
            length_error = abs(math.log(max(candidate.length, 1.0) / max(self.last_observed.length, 1.0)))
            cost = distance / max(jump_limit, 1.0) + angle_error / 120.0 + length_error - candidate.score * 0.02
            valid.append((cost, candidate))
        if valid:
            return min(valid, key=lambda item: item[0])[1]
        if candidates:
            self.rejected_jumps += 1
        return None

    def _accept(self, detection, now):
        detection = self._orient_like_previous(detection)
        if self.last_observed is not None:
            elapsed = max(1.0 / 240.0, now - self.last_observed_time)
            measured_velocity = (
                (detection.center[0] - self.last_observed.center[0]) / elapsed,
                (detection.center[1] - self.last_observed.center[1]) / elapsed,
            )
            measured_angular = angle_delta(detection.angle, self.last_observed.angle) / elapsed
            measured_angular = max(-1080.0, min(1080.0, measured_angular))
            self.center_velocity = (
                self.center_velocity[0] * 0.45 + measured_velocity[0] * 0.55,
                self.center_velocity[1] * 0.45 + measured_velocity[1] * 0.55,
            )
            self.angular_velocity = self.angular_velocity * 0.45 + measured_angular * 0.55
        self.last_observed = detection
        self.last_observed_time = now
        self.last_output = detection
        return detection

    def _update_pending(self, candidates, now):
        candidates = [
            candidate for candidate in candidates if candidate.score >= self.acquire_min_score
        ]
        if not candidates:
            self.pending = None
            self.pending_count = 0
            self.pending_since = 0.0
            return None
        best = max(candidates, key=lambda candidate: candidate.score)
        if self.pending is None:
            self.pending = best
            self.pending_count = 1
            self.pending_since = now
            self.last_observed_time = now
            return None
        elapsed = max(1.0 / 240.0, now - self.last_observed_time)
        if point_distance(best.center, self.pending.center) <= self._jump_limit(elapsed):
            self.pending = best
            self.pending_count += 1
        else:
            self.pending = best
            self.pending_count = 1
            self.pending_since = now
        self.last_observed_time = now
        if (
            self.pending_count < self.confirm_frames
            or now - self.pending_since < self.confirm_seconds
        ):
            return None
        self.active = True
        self.last_observed = None
        self.last_output = None
        self.center_velocity = (0.0, 0.0)
        self.angular_velocity = 0.0
        self.pending = None
        self.pending_count = 0
        self.pending_since = 0.0
        return self._accept(best, now)

    def _predict(self, now, imu_rate):
        if self.last_observed is None:
            return None
        elapsed = now - self.last_observed_time
        if elapsed > self.lost_seconds:
            self.active = False
            self.pending = None
            self.pending_count = 0
            self.pending_since = 0.0
            self.last_observed = None
            self.last_output = None
            self.center_velocity = (0.0, 0.0)
            self.angular_velocity = 0.0
            return None
        if elapsed > self.prediction_seconds:
            return None

        angular_velocity = self.angular_velocity
        if imu_rate is not None:
            angular_velocity = (
                angular_velocity * (1.0 - self.imu_blend) + imu_rate * self.imu_blend
            )
        center = (
            self.last_observed.center[0] + self.center_velocity[0] * elapsed,
            self.last_observed.center[1] + self.center_velocity[1] * elapsed,
        )
        angle = self.last_observed.angle + angular_velocity * elapsed
        p1, p2 = endpoints_from_center(center, self.last_observed.length, angle)
        predicted = replace(
            self.last_observed,
            p1=p1,
            p2=p2,
            center=center,
            angle=angle,
            contour=None,
            predicted=True,
        )
        predicted = self._orient_like_previous(predicted)
        self.last_output = predicted
        return predicted

    def update(self, candidates, now, imu_rate=None):
        # Expire before matching: a late candidate must pass acquisition again.
        if self.active and now - self.last_observed_time > self.lost_seconds:
            self._predict(now, None)
        if not self.active:
            return self._update_pending(candidates, now)
        selected = self._select_active(candidates, now)
        if selected is not None:
            return self._accept(selected, now)
        return self._predict(now, imu_rate)

    @property
    def status(self):
        if self.active:
            return "tracking"
        if self.pending_count:
            return f"confirming {self.pending_count}/{self.confirm_frames}"
        return "searching"


def parse_args():
    parser = argparse.ArgumentParser(description="Single-smartphone red/blue saber tracker.")
    parser.add_argument("--camera", type=int, default=int(os.environ.get("CAMERA_INDEX", "0")))
    parser.add_argument("--width", type=int, default=640)
    parser.add_argument("--height", type=int, default=360)
    parser.add_argument("--output-width", type=int, default=1920)
    parser.add_argument("--output-height", type=int, default=1080)
    parser.add_argument("--background-seconds", type=float, default=2.0)
    parser.add_argument("--foreground-threshold", type=int, default=28)
    parser.add_argument("--confirm-frames", type=int, default=10)
    parser.add_argument("--confirm-ms", type=float, default=400.0)
    parser.add_argument("--acquire-min-score", type=float, default=18.0)
    parser.add_argument("--prediction-ms", type=float, default=120.0)
    parser.add_argument("--lost-ms", type=float, default=350.0)
    parser.add_argument("--jump-base-px", type=float, default=45.0)
    parser.add_argument("--jump-ratio-per-sec", type=float, default=2.0)
    parser.add_argument("--udp-ip", default="127.0.0.1")
    parser.add_argument("--red-port", type=int, default=5005)
    parser.add_argument("--blue-port", type=int, default=5006)
    parser.add_argument("--flip-h", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--flip-v", action=argparse.BooleanOptionalAction, default=True)
    parser.add_argument("--imu-stick", choices=("none", "red", "blue"), default="none")
    parser.add_argument("--imu-host", default="127.0.0.1")
    parser.add_argument("--imu-port", type=int, default=9003)
    parser.add_argument("--imu-axis", choices=("x", "y", "z"), default="z")
    parser.add_argument("--imu-sign", type=float, choices=(-1.0, 1.0), default=1.0)
    parser.add_argument("--imu-scale", type=float, default=1.0)
    parser.add_argument("--imu-blend", type=float, default=0.35)
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
        f"Camera {index} could not be read. Check Continuity Camera and macOS Camera "
        "permission, or select another index with --camera."
    )


def load_settings():
    settings = {}
    try:
        settings = json.loads(THRESHOLD_PATH.read_text(encoding="utf-8"))
    except (OSError, ValueError):
        pass

    def color_range(prefix, fallback):
        lower, upper = fallback
        return (
            np.array(
                [
                    settings.get(f"{prefix}_h_min", int(lower[0])),
                    settings.get(f"{prefix}_s_min", int(lower[1])),
                    settings.get(f"{prefix}_v_min", int(lower[2])),
                ],
                dtype=np.uint8,
            ),
            np.array(
                [
                    settings.get(f"{prefix}_h_max", int(upper[0])),
                    settings.get(f"{prefix}_s_max", int(upper[1])),
                    settings.get(f"{prefix}_v_max", int(upper[2])),
                ],
                dtype=np.uint8,
            ),
        )

    return {
        "red": color_range("a", DEFAULT_RANGES["red"]),
        "blue": color_range("b", DEFAULT_RANGES["blue"]),
        "min_area": max(20, int(settings.get("min_area", 170))),
        "merge_distance": max(1, int(settings.get("merge_dist", 5))),
    }


def hsv_mask(hsv, lower, upper):
    if int(lower[0]) <= int(upper[0]):
        return cv2.inRange(hsv, lower, upper)
    return cv2.bitwise_or(
        cv2.inRange(
            hsv,
            lower,
            np.array([179, upper[1], upper[2]], dtype=np.uint8),
        ),
        cv2.inRange(
            hsv,
            np.array([0, lower[1], lower[2]], dtype=np.uint8),
            upper,
        ),
    )


def fit_led_axis(core):
    """Fit the luminous pixels, excluding attached dim skin and reflections."""
    ys, xs = np.nonzero(core)
    if len(xs) < 12:
        return None
    points = np.column_stack((xs, ys)).astype(np.float32)
    vx, vy, x, y = cv2.fitLine(points, cv2.DIST_HUBER, 0, .01, .01).ravel()
    axis = np.array([vx, vy])
    origin = np.array([x, y])
    offset = points - origin
    along = offset[:, 0] * vx + offset[:, 1] * vy
    across = np.abs(-offset[:, 0] * vy + offset[:, 1] * vx)
    # Trim isolated bright outliers before estimating the visible endpoints.
    keep = across <= max(2.0, float(np.median(across)) * 3.0)
    if np.count_nonzero(keep) < 12:
        return None
    low, high = np.percentile(along[keep], [1, 99])
    length = float(high - low)
    width = max(1.0, float(np.percentile(across[keep], 90)) * 2)
    if length < 10 or length / width < 2:
        return None
    p1, p2 = origin + axis * low, origin + axis * high
    return tuple(p1), tuple(p2), tuple((p1 + p2) / 2), length, math.degrees(math.atan2(vy, vx))


def detect_candidates(frame, color_range, min_area, merge_distance, foreground_mask=None):
    hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
    mask = hsv_mask(hsv, *color_range)
    color_pixels = mask > 0
    if foreground_mask is not None:
        mask = cv2.bitwise_and(mask, foreground_mask)
    kernel_size = max(1, min(15, merge_distance))
    if kernel_size % 2 == 0:
        kernel_size += 1
    mask = cv2.morphologyEx(
        mask,
        cv2.MORPH_CLOSE,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (kernel_size, kernel_size)),
    )
    mask = cv2.morphologyEx(
        mask,
        cv2.MORPH_OPEN,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3)),
    )

    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    frame_area = float(frame.shape[0] * frame.shape[1])
    detections = []
    for contour in contours:
        area = float(cv2.contourArea(contour))
        if area < min_area or area / frame_area > 0.20:
            continue
        (cx, cy), (rect_width, rect_height), rect_angle = cv2.minAreaRect(contour)
        long_side = max(float(rect_width), float(rect_height), 1.0)
        short_side = max(min(float(rect_width), float(rect_height)), 1.0)
        aspect = long_side / short_side
        extent = area / max(float(rect_width) * float(rect_height), 1.0)
        bbox_x, bbox_y, bbox_width, bbox_height = cv2.boundingRect(contour)
        bbox_extent = area / max(float(bbox_width * bbox_height), 1.0)
        points = contour.reshape(-1, 2).astype(float)
        centered = points - points.mean(axis=0)
        try:
            eigenvalues = np.sort(
                np.maximum(np.linalg.eigvalsh(np.cov(centered, rowvar=False)), 0.0)
            )
            elongation = (
                float("inf")
                if eigenvalues[-2] <= 1e-6
                else float(eigenvalues[-1] / eigenvalues[-2])
            )
        except (ValueError, np.linalg.LinAlgError):
            elongation = 0.0
        if (
            aspect < 1.5
            or extent < 0.10
            or bbox_extent < 0.08
            or (elongation and elongation < 2.2)
        ):
            continue
        angle = rect_angle if rect_width >= rect_height else rect_angle + 90.0
        p1, p2 = endpoints_from_center((cx, cy), long_side, angle)
        contour_mask = np.zeros(mask.shape, dtype=np.uint8)
        cv2.drawContours(contour_mask, [contour], -1, 255, -1)
        brightness = hsv[:, :, 2][contour_mask > 0]
        bright_fraction = (
            float(np.count_nonzero(brightness >= 220)) / max(len(brightness), 1)
        )
        mean_brightness = float(brightness.mean()) / 255.0 if len(brightness) else 0.0
        # Brightness is evidence, not just a bonus that a large skin contour
        # can outweigh. Require colored highlights along the blade axis.
        core = (contour_mask > 0) & color_pixels & (hsv[:, :, 2] >= 225) & (hsv[:, :, 1] >= 100)
        core_y, core_x = np.nonzero(core)
        if len(core_x) < max(12, len(brightness) * 0.15):
            continue
        theta = math.radians(angle)
        projections = core_x * math.cos(theta) + core_y * math.sin(theta)
        if np.ptp(projections) < long_side * 0.55:
            continue
        fitted = fit_led_axis(core)
        if fitted is None:
            continue
        p1, p2, (cx, cy), long_side, angle = fitted
        score = (
            min(aspect, 8.0) * 2.0
            + extent * 3.0
            + bbox_extent * 1.5
            + min(elongation, 12.0) * 0.25
            + min(area / min_area, 20.0)
            + bright_fraction * 6.0
            + mean_brightness * 2.0
            - (area / frame_area) * 10.0
        )
        detections.append(
            Detection(p1, p2, (cx, cy), long_side, angle, score, contour=contour)
        )
    detections.sort(key=lambda detection: detection.score, reverse=True)
    return detections[:8], mask


def build_foreground_mask(frame, background, threshold):
    difference = cv2.absdiff(frame, cv2.convertScaleAbs(background))
    difference = np.max(difference, axis=2).astype(np.uint8)
    _, mask = cv2.threshold(difference, max(1, threshold), 255, cv2.THRESH_BINARY)
    mask = cv2.morphologyEx(
        mask,
        cv2.MORPH_OPEN,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3)),
    )
    return cv2.dilate(
        mask,
        cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (9, 9)),
        iterations=1,
    )


def encode_detection(detection, frame_size, output_size, flip_h, flip_v):
    frame_width, frame_height = frame_size
    output_width, output_height = output_size
    values = []
    for x, y in (detection.p1, detection.p2):
        x = x * output_width / max(frame_width, 1)
        y = y * output_height / max(frame_height, 1)
        if flip_h:
            x = output_width - x
        if flip_v:
            y = output_height - y
        values.extend((int(round(x)), int(round(y))))
    return ",".join(str(value) for value in values).encode("ascii")


def draw_detection(frame, detection, color, label):
    p1 = tuple(int(round(value)) for value in detection.p1)
    p2 = tuple(int(round(value)) for value in detection.p2)
    if detection.contour is not None:
        cv2.drawContours(frame, [detection.contour], -1, color, 1)
    cv2.line(frame, p1, p2, (0, 255, 255) if detection.predicted else color, 3)
    cv2.circle(frame, p1, 5, color, -1)
    cv2.circle(frame, p2, 5, color, -1)
    cv2.putText(
        frame,
        label + (" predicted" if detection.predicted else ""),
        (p1[0] + 6, p1[1] - 6),
        cv2.FONT_HERSHEY_SIMPLEX,
        0.5,
        color,
        2,
        cv2.LINE_AA,
    )


def main():
    args = parse_args()
    settings = load_settings()
    capture = open_camera(args.camera, args.width, args.height)
    latest = LatestFrame(capture)
    latest.start()
    udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    imu = None
    if args.imu_stick != "none":
        imu = ImuReceiver(args.imu_host, args.imu_port)
        imu.start()
        print(
            f"IMU prediction: stick={args.imu_stick}, UDP={args.imu_host}:{args.imu_port}, "
            f"axis={args.imu_axis}, sign={args.imu_sign:+.0f}"
        )

    trackers = None
    ports = {"red": args.red_port, "blue": args.blue_port}
    last_sequence = -1
    background = None
    background_started = 0.0
    background_ready = args.background_seconds <= 0.0
    last_background_second = None
    print("Smartphone-only mode. Q quits; yellow line means short-gap prediction.")
    if not background_ready:
        print(
            f"Learning the empty background for {args.background_seconds:.1f}s. "
            "Keep both sabers and people outside the frame."
        )

    try:
        while True:
            frame, timestamp, sequence = latest.get()
            if frame is None or sequence == last_sequence:
                time.sleep(0.001)
                continue
            last_sequence = sequence
            height, width = frame.shape[:2]
            if background is None:
                background = frame.astype(np.float32)
                background_started = timestamp
            if not background_ready:
                cv2.accumulateWeighted(frame, background, 0.08)
                remaining = max(
                    0.0, args.background_seconds - (timestamp - background_started)
                )
                whole_second = int(math.ceil(remaining))
                if whole_second != last_background_second:
                    print(f"Background learning: {remaining:.1f}s remaining")
                    last_background_second = whole_second
                if timestamp - background_started >= args.background_seconds:
                    background_ready = True
                    print("Background ready. Put the red and blue sabers into view.")
                if args.show:
                    learning = frame.copy()
                    cv2.putText(
                        learning,
                        f"EMPTY SCENE - learning background {remaining:.1f}s",
                        (12, 30),
                        cv2.FONT_HERSHEY_SIMPLEX,
                        0.65,
                        (0, 255, 255),
                        2,
                        cv2.LINE_AA,
                    )
                    cv2.imshow("smartphone saber tracking", learning)
                    if cv2.waitKey(1) & 0xFF in (ord("q"), 27):
                        break
                continue

            if trackers is None:
                diagonal = math.hypot(width, height)
                trackers = {
                    name: StickTracker(
                        name,
                        diagonal,
                        max(1, args.confirm_frames),
                        max(0.0, args.confirm_ms / 1000.0),
                        args.acquire_min_score,
                        max(0.0, args.prediction_ms / 1000.0),
                        max(args.prediction_ms, args.lost_ms) / 1000.0,
                        max(1.0, args.jump_base_px),
                        max(0.0, args.jump_ratio_per_sec),
                        max(0.0, min(1.0, args.imu_blend)),
                    )
                    for name in ("red", "blue")
                }

            display = frame.copy()
            masks = {}
            foreground_mask = (
                None
                if args.background_seconds <= 0.0
                else build_foreground_mask(frame, background, args.foreground_threshold)
            )
            for name, color in (("red", (0, 0, 255)), ("blue", (255, 0, 0))):
                detections, masks[name] = detect_candidates(
                    frame,
                    settings[name],
                    settings["min_area"],
                    settings["merge_distance"],
                    foreground_mask,
                )
                imu_rate = None
                if imu is not None and args.imu_stick == name:
                    imu_rate = imu.angular_rate(
                        args.imu_axis, args.imu_sign, args.imu_scale, timestamp
                    )
                output = trackers[name].update(detections, timestamp, imu_rate)
                if output is not None:
                    payload = encode_detection(
                        output,
                        (width, height),
                        (args.output_width, args.output_height),
                        args.flip_h,
                        args.flip_v,
                    )
                    udp.sendto(payload, (args.udp_ip, ports[name]))
                    if args.show:
                        draw_detection(display, output, color, name)
                if args.show:
                    cv2.putText(
                        display,
                        f"{name}: {trackers[name].status} jumps:{trackers[name].rejected_jumps}",
                        (12, 24 if name == "red" else 48),
                        cv2.FONT_HERSHEY_SIMPLEX,
                        0.55,
                        color,
                        2,
                        cv2.LINE_AA,
                    )

            if args.show:
                cv2.imshow("smartphone saber tracking", display)
                cv2.imshow("red mask", masks["red"])
                cv2.imshow("blue mask", masks["blue"])
                if cv2.waitKey(1) & 0xFF in (ord("q"), 27):
                    break
    except KeyboardInterrupt:
        pass
    finally:
        latest.stop()
        latest.join(timeout=1.0)
        capture.release()
        udp.close()
        if imu is not None:
            imu.stop()
            imu.join(timeout=1.0)
        if args.show:
            cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
