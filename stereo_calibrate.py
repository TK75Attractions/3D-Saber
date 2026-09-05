"""Create a stereo calibration file for stereo_camera.py.

Usage:
    python stereo_calibrate.py --left 0 --right 1

Move a printed chessboard through the common camera view and press SPACE for
20-30 good pairs. Press Q to cancel. The resulting JSON is intentionally
simple so it can also be inspected or copied to another machine.
"""

import argparse
import json
import os
import time

from python_runtime import reexec_with_cv2

reexec_with_cv2()

import cv2
import numpy as np


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--left", type=int, default=0)
    parser.add_argument("--right", type=int, default=1)
    parser.add_argument("--cols", type=int, default=9, help="Inner corners")
    parser.add_argument("--rows", type=int, default=6, help="Inner corners")
    parser.add_argument("--square", type=float, default=25.0, help="Square size in mm")
    parser.add_argument("--samples", type=int, default=25)
    parser.add_argument("--output", default="stereo_calibration.json")
    return parser.parse_args()


def open_camera(index):
    backend = cv2.CAP_DSHOW if os.name == "nt" else cv2.CAP_ANY
    cap = cv2.VideoCapture(index, backend)
    if not cap.isOpened():
        cap.release()
        raise RuntimeError(f"Cannot open camera {index}")
    cap.set(cv2.CAP_PROP_FRAME_WIDTH, 640)
    cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 480)
    cap.set(cv2.CAP_PROP_BUFFERSIZE, 1)
    return cap


def crop_to_size(frame, size):
    target_w, target_h = size
    height, width = frame.shape[:2]
    if width < target_w or height < target_h:
        return cv2.resize(frame, (target_w, target_h), interpolation=cv2.INTER_AREA)
    x = (width - target_w) // 2
    y = (height - target_h) // 2
    return frame[y:y + target_h, x:x + target_w]


def main():
    args = parse_args()
    board_size = (args.cols, args.rows)
    object_points = np.zeros((args.cols * args.rows, 3), np.float32)
    object_points[:, :2] = np.mgrid[0:args.cols, 0:args.rows].T.reshape(-1, 2)
    object_points *= args.square

    left = open_camera(args.left)
    right = open_camera(args.right)
    obj_points, left_points, right_points = [], [], []
    last_capture = 0.0
    working_size = None
    print("Show the same chessboard to both cameras.")
    print("Press SPACE only when corners are detected in both views; Q quits.")
    try:
        while len(obj_points) < args.samples:
            ok_l, frame_l = left.read()
            ok_r, frame_r = right.read()
            if not ok_l or not ok_r:
                continue
            if working_size is None:
                working_size = (
                    min(frame_l.shape[1], frame_r.shape[1]),
                    min(frame_l.shape[0], frame_r.shape[0]),
                )
                print(f"using common calibration size: {working_size[0]}x{working_size[1]}")
            frame_l = crop_to_size(frame_l, working_size)
            frame_r = crop_to_size(frame_r, working_size)
            gray_l = cv2.cvtColor(frame_l, cv2.COLOR_BGR2GRAY)
            gray_r = cv2.cvtColor(frame_r, cv2.COLOR_BGR2GRAY)
            found_l, corners_l = cv2.findChessboardCorners(gray_l, board_size, None)
            found_r, corners_r = cv2.findChessboardCorners(gray_r, board_size, None)
            view_l, view_r = frame_l.copy(), frame_r.copy()
            if found_l:
                cv2.drawChessboardCorners(view_l, board_size, corners_l, found_l)
            if found_r:
                cv2.drawChessboardCorners(view_r, board_size, corners_r, found_r)
            combined = np.hstack((view_l, view_r))
            cv2.putText(combined, f"samples {len(obj_points)}/{args.samples}", (20, 35),
                        cv2.FONT_HERSHEY_SIMPLEX, 1.0, (0, 255, 0), 2)
            cv2.imshow("stereo calibration", combined)
            key = cv2.waitKey(1) & 0xFF
            if key in (ord("q"), 27):
                print(f"calibration canceled; captured {len(obj_points)} sample(s), file was not saved")
                return
            if key == ord(" ") and found_l and found_r and time.monotonic() - last_capture > 0.4:
                criteria = (cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 30, 0.001)
                corners_l = cv2.cornerSubPix(gray_l, corners_l, (11, 11), (-1, -1), criteria)
                corners_r = cv2.cornerSubPix(gray_r, corners_r, (11, 11), (-1, -1), criteria)
                obj_points.append(object_points.copy())
                left_points.append(corners_l)
                right_points.append(corners_r)
                last_capture = time.monotonic()
                print(f"captured {len(obj_points)}/{args.samples}")
    finally:
        left.release()
        right.release()
        cv2.destroyAllWindows()

    if len(obj_points) < 10:
        raise RuntimeError(
            f"Only {len(obj_points)} valid sample(s) captured. "
            "Capture at least 10 pairs with SPACE before quitting."
        )

    image_size = gray_l.shape[::-1]
    rms_l, k1, d1, _, _ = cv2.calibrateCamera(obj_points, left_points, image_size, None, None)
    rms_r, k2, d2, _, _ = cv2.calibrateCamera(obj_points, right_points, image_size, None, None)
    flags = cv2.CALIB_FIX_INTRINSIC
    rms, k1, d1, k2, d2, r, t, _, _ = cv2.stereoCalibrate(
        obj_points, left_points, right_points, k1, d1, k2, d2, image_size,
        criteria=(cv2.TERM_CRITERIA_EPS + cv2.TERM_CRITERIA_MAX_ITER, 100, 1e-6),
        flags=flags,
    )

    payload = {
        "image_size": list(image_size),
        "camera_matrix_left": k1.tolist(),
        "dist_coeffs_left": d1.tolist(),
        "camera_matrix_right": k2.tolist(),
        "dist_coeffs_right": d2.tolist(),
        "rotation": r.tolist(),
        "translation": t.tolist(),
        "rms_left": float(rms_l),
        "rms_right": float(rms_r),
        "rms_stereo": float(rms),
    }
    with open(args.output, "w", encoding="utf-8") as file:
        json.dump(payload, file, indent=2)
    print(f"saved {args.output}; stereo RMS={rms:.4f}px")


if __name__ == "__main__":
    main()
