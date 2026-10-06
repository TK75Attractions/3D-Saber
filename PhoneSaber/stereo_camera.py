"""Low-latency two-camera colored-stick tracker.

The cameras must be calibrated with stereo_calibrate.py first. The left view
is used for the existing 2D UDP protocol, while triangulated 3D endpoints are
also sent as JSON on UDP 5007.
"""

import argparse
import json
import math
import os
import socket
import threading
import time
from pathlib import Path

from python_runtime import reexec_with_cv2

reexec_with_cv2()

import cv2
import numpy as np


UDP_HOST = "127.0.0.1"
UDP_PORTS = (5005, 5006)
UDP_3D_PORT = 5007
DEFAULT_CALIBRATION = os.path.join(os.path.dirname(os.path.abspath(__file__)), "stereo_calibration.json")
COLORS = {
    "a": (np.array([170, 70, 100]), np.array([10, 255, 255])),
    "b": (np.array([99, 137, 168]), np.array([124, 255, 255])),
}


def open_camera(index, width, height):
    backend = cv2.CAP_DSHOW if os.name == "nt" else cv2.CAP_ANY
    cap = cv2.VideoCapture(index, backend)
    if not cap.isOpened():
        cap.release()
        raise RuntimeError(f"Cannot open camera {index}")
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, width)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, height)
    cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
    return cap


def load_calibration(path, image_size):
    with open(path, "r", encoding="utf-8") as file:
        data = json.load(file)
    expected = tuple(data["image_size"])
    if expected != tuple(image_size):
        raise ValueError(f"Calibration is {expected}, cameras are {image_size}; recalibrate at this resolution")
    return {key: np.asarray(data[key], dtype=np.float64) for key in (
        "camera_matrix_left", "dist_coeffs_left", "camera_matrix_right",
        "dist_coeffs_right", "rotation", "translation")}


def crop_to_size(frame, size):
    target_w, target_h = size
    height, width = frame.shape[:2]
    if width < target_w or height < target_h:
        return cv2.resize(frame, (target_w, target_h), interpolation=cv2.INTER_AREA)
    x = (width - target_w) // 2
    y = (height - target_h) // 2
    return frame[y:y + target_h, x:x + target_w]


def contour_metrics(contour):
    area = cv2.contourArea(contour)
    rect = cv2.minAreaRect(contour)
    rw, rh = rect[1]
    long_side, short_side = max(rw, rh), min(rw, rh)
    aspect = long_side / max(short_side, 1.0)
    x, y, w, h = cv2.boundingRect(contour)
    hull_area = cv2.contourArea(cv2.convexHull(contour))
    return area, rect, aspect, area / max(w * h, 1), area / max(hull_area, 1), (x, y, w, h)


def rect_endpoints(rect):
    points = cv2.boxPoints(rect)
    longest = (-1.0, None, None)
    for i in range(4):
        for j in range(i + 1, 4):
            distance = float(np.linalg.norm(points[i] - points[j]))
            if distance > longest[0]:
                longest = (distance, points[i], points[j])
    return tuple(np.rint(longest[1]).astype(int)), tuple(np.rint(longest[2]).astype(int))


def hsv_mask(hsv, lower, upper):
    if int(lower[0]) <= int(upper[0]):
        return cv2.inRange(hsv, lower, upper)
    return cv2.bitwise_or(
        cv2.inRange(hsv, lower, np.array([179, upper[1], upper[2]], np.uint8)),
        cv2.inRange(hsv, np.array([0, lower[1], lower[2]], np.uint8), upper),
    )


def detect_candidates(frame, lower, upper):
    hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
    mask = hsv_mask(hsv, lower, upper)
    mask = cv2.morphologyEx(mask, cv2.MORPH_OPEN, np.ones((3, 3), np.uint8))
    mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, np.ones((5, 5), np.uint8))
    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    candidates = []
    frame_area = frame.shape[0] * frame.shape[1]
    for contour in contours:
        area, rect, aspect, extent, solidity, bbox = contour_metrics(contour)
        if area < 100 or area / frame_area > 0.22 or aspect < 1.5 or extent < 0.10:
            continue
        p1, p2 = rect_endpoints(rect)
        center = ((p1[0] + p2[0]) * 0.5, (p1[1] + p2[1]) * 0.5)
        candidates.append({"p1": p1, "p2": p2, "center": center, "aspect": aspect,
                           "extent": extent, "solidity": solidity, "bbox": bbox})
    candidates.sort(key=lambda item: item["aspect"] + item["extent"], reverse=True)
    return candidates[:8], mask


def pair_candidates(left, right, width, min_disparity):
    best = None
    for a in left:
        for b in right:
            y_error = abs(a["center"][1] - b["center"][1]) / max(width, 1)
            disparity = a["center"][0] - b["center"][0]
            if disparity < min_disparity or y_error > 0.020:
                continue
            score = y_error * 8.0 + abs(math.log(max(a["aspect"], 1) / max(b["aspect"], 1)))
            if best is None or score < best[0]:
                best = (score, a, b)
    return None if best is None else (best[1], best[2])


def triangulate_points(points_left, points_right, p_left, p_right):
    left = np.asarray(points_left, dtype=np.float64).T
    right = np.asarray(points_right, dtype=np.float64).T
    homogeneous = cv2.triangulatePoints(p_left, p_right, left, right)
    homogeneous /= np.maximum(homogeneous[3:4], 1e-9)
    return homogeneous[:3].T


def reprojection_error(points_left, points_right, points_3d, p_left, p_right):
    points_h = np.c_[points_3d, np.ones(len(points_3d))].T
    projected_l = (p_left @ points_h).T
    projected_r = (p_right @ points_h).T
    projected_l = projected_l[:, :2] / projected_l[:, 2:3]
    projected_r = projected_r[:, :2] / projected_r[:, 2:3]
    error_l = np.linalg.norm(projected_l - np.asarray(points_left), axis=1)
    error_r = np.linalg.norm(projected_r - np.asarray(points_right), axis=1)
    return float(max(error_l.max(), error_r.max())), bool(np.all(points_3d[:, 2] > 0))


def raw_left_points(points, calibration, r1, p1):
    points = np.asarray(points, dtype=np.float64).reshape(-1, 1, 2)
    rectified_pixels = points.reshape(-1, 2)
    rectified_rays = (np.linalg.inv(p1[:, :3]) @ np.c_[rectified_pixels, np.ones(len(points))].T).T
    rays = (r1.T @ rectified_rays.T).T
    projected, _ = cv2.projectPoints(rays, np.zeros((3, 1)), np.zeros((3, 1)),
                                     calibration["camera_matrix_left"], calibration["dist_coeffs_left"])
    return np.rint(projected.reshape(-1, 2)).astype(int)


class LatestFrame:
    def __init__(self, cap):
        self.cap, self.lock, self.frame, self.timestamp, self.running = cap, threading.Lock(), None, 0.0, True
        self.thread = threading.Thread(target=self._run, daemon=True)
        self.thread.start()

    def _run(self):
        while self.running:
            ok, frame = self.cap.read()
            if ok:
                with self.lock:
                    self.frame = frame
                    self.timestamp = time.perf_counter()

    def get(self):
        with self.lock:
            return (None, 0.0) if self.frame is None else (self.frame.copy(), self.timestamp)

    def stop(self):
        self.running = False
        self.thread.join(timeout=1)


def send_stick(sock, port, endpoints):
    if endpoints is not None:
        payload = ",".join(str(int(value)) for point in endpoints for value in point)
        sock.sendto(payload.encode("ascii"), (UDP_HOST, port))


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--left", type=int, default=0)
    parser.add_argument("--right", type=int, default=1)
    parser.add_argument("--calibration", default=DEFAULT_CALIBRATION)
    parser.add_argument("--width", type=int, default=640)
    parser.add_argument("--height", type=int, default=480)
    parser.add_argument("--fps", type=float, default=60.0)
    parser.add_argument("--show", action="store_true")
    args = parser.parse_args()

    left_cap = open_camera(args.left, args.width, args.height)
    right_cap = open_camera(args.right, args.width, args.height)
    calibration_path = Path(args.calibration)
    if not calibration_path.is_absolute() and not calibration_path.exists():
        calibration_path = Path(__file__).resolve().parent / calibration_path
    with open(calibration_path, "r", encoding="utf-8") as file:
        calibration_size = tuple(json.load(file)["image_size"])
    calibration = load_calibration(str(calibration_path), calibration_size)
    size = calibration_size
    r1, r2, p1, p2, _, _, _ = cv2.stereoRectify(
        calibration["camera_matrix_left"], calibration["dist_coeffs_left"],
        calibration["camera_matrix_right"], calibration["dist_coeffs_right"], size,
        calibration["rotation"], calibration["translation"], flags=cv2.CALIB_ZERO_DISPARITY, alpha=0)
    map1x, map1y = cv2.initUndistortRectifyMap(calibration["camera_matrix_left"], calibration["dist_coeffs_left"], r1, p1, size, cv2.CV_16SC2)
    map2x, map2y = cv2.initUndistortRectifyMap(calibration["camera_matrix_right"], calibration["dist_coeffs_right"], r2, p2, size, cv2.CV_16SC2)
    left_latest, right_latest = LatestFrame(left_cap), LatestFrame(right_cap)
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    period = 1.0 / max(args.fps, 1.0)
    next_frame = time.perf_counter()
    try:
        while True:
            now = time.perf_counter()
            if now < next_frame:
                time.sleep(min(0.001, next_frame - now))
                continue
            next_frame += period
            raw_l, timestamp_l = left_latest.get()
            raw_r, timestamp_r = right_latest.get()
            if raw_l is None or raw_r is None:
                continue
            if abs(timestamp_l - timestamp_r) > 0.035:
                continue
            frame_l = cv2.remap(crop_to_size(raw_l, size), map1x, map1y, cv2.INTER_LINEAR)
            frame_r = cv2.remap(crop_to_size(raw_r, size), map2x, map2y, cv2.INTER_LINEAR)
            output = [None, None]
            debug = frame_l.copy()
            for index, (lower, upper) in enumerate(COLORS.values()):
                candidates_l, mask_l = detect_candidates(frame_l, lower, upper)
                candidates_r, mask_r = detect_candidates(frame_r, lower, upper)
                pair = pair_candidates(candidates_l, candidates_r, args.width, 1.0)
                if pair is None:
                    continue
                candidate_l, candidate_r = pair
                points_l = [candidate_l["p1"], candidate_l["p2"]]
                points_r = [candidate_r["p1"], candidate_r["p2"]]
                direct_error = abs(points_l[0][1] - points_r[0][1]) + abs(points_l[1][1] - points_r[1][1])
                swapped_error = abs(points_l[0][1] - points_r[1][1]) + abs(points_l[1][1] - points_r[0][1])
                if swapped_error < direct_error:
                    points_r.reverse()
                points_3d = triangulate_points(points_l, points_r, p1, p2)
                error, in_front = reprojection_error(points_l, points_r, points_3d, p1, p2)
                if not in_front or error > 4.0:
                    continue
                raw_points = raw_left_points(points_l, calibration, r1, p1)
                output[index] = tuple(tuple(int(v) for v in point) for point in raw_points)
                record = {"stick": index + 1, "timestamp_ms": int(time.time() * 1000),
                          "endpoints": points_3d.round(4).tolist(),
                          "confidence": max(0.0, 1.0 - error / 4.0)}
                sock.sendto(json.dumps(record, separators=(",", ":")).encode("utf-8"), (UDP_HOST, UDP_3D_PORT))
                if args.show:
                    cv2.line(debug, tuple(raw_points[0]), tuple(raw_points[1]), (0, 255, 255), 2)
            send_stick(sock, UDP_PORTS[0], output[0])
            send_stick(sock, UDP_PORTS[1], output[1])
            if args.show:
                cv2.imshow("stereo stick tracking", debug)
                if cv2.waitKey(1) & 0xFF in (ord("q"), 27):
                    break
    finally:
        left_latest.stop()
        right_latest.stop()
        left_cap.release()
        right_cap.release()
        sock.close()
        cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
