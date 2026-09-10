import os
import sys
import platform
from python_runtime import reexec_with_cv2

# 特定の Python に固定しない。
# macOS では pyenv の Python を明示できるが、Windows ではそのパスが存在しないため
# そのまま現在の実行環境を使う。
_TARGET_PYTHON = os.environ.get("CAMERA_PYTHON")
if __name__ == "__main__":
    reexec_with_cv2()
    if _TARGET_PYTHON and os.path.exists(_TARGET_PYTHON) and os.path.realpath(sys.executable) != os.path.realpath(_TARGET_PYTHON):
        os.execv(_TARGET_PYTHON, [_TARGET_PYTHON, *sys.argv])

import cv2
import numpy as np
import socket
import math
import time
import threading
import json

# PyTorch(MPS) オプション: macOS Apple Silicon で GPU(Metal via MPS) を使う
try:
    import torch
    import torch.nn.functional as F
    TORCH_AVAILABLE = True
    MPS_AVAILABLE = getattr(torch.backends, 'mps', None) is not None and torch.backends.mps.is_available()
except Exception:
    TORCH_AVAILABLE = False
    MPS_AVAILABLE = False

# 有効なら MPS を使ってモルフォ処理を行う（自動判定）
USE_MPS_ACCEL = TORCH_AVAILABLE and MPS_AVAILABLE

def gpu_morph_close(mask, k):
    """MPS上でモルフォロジーの close (dilate then erode) を行う。
    mask: uint8 numpy array (0 or 255)
    k: kernel size (odd int)
    returns: uint8 numpy array (0 or 255)
    """
    if not USE_MPS_ACCEL or k <= 1:
        return mask
    try:
        device = torch.device('mps')
        # normalize to 0/1 float
        t = torch.from_numpy((mask > 0).astype('float32'))
        t = t.unsqueeze(0).unsqueeze(0).to(device)
        pad = k // 2
        # dilation: max pool
        dil = F.max_pool2d(t, kernel_size=k, stride=1, padding=pad)
        # erosion: 1 - max_pool(1 - x)
        er = 1.0 - F.max_pool2d(1.0 - dil, kernel_size=k, stride=1, padding=pad)
        out = (er.squeeze().cpu().numpy() > 0.5).astype('uint8') * 255
        return out
    except Exception:
        return mask

# UDP設定
UDP_IP = "127.0.0.1"
UDP_PORT = 5005
UDP_PORT_STICK2 = 5006
SEND_STICK2 = True
SEND_HZ = 60.0
HOLD_LAST_VALUE_WHEN_MISSING = True
sock = None
# 遅延最小化オプション: 検出変化時に即時送信する
IMMEDIATE_SEND_ON_CHANGE = True
_send_lock = threading.Lock()

# 処理改善フラグ
# デフォルトで UI と詳細ログを無効にして軽量化
SHOW_UI = False  # 表示を有効化
LOG_DETECTIONS = False  # 診断ログを抑制
# `detected` 用の簡易表示は残す(軽量な出力を維持)
SHOW_DETECTED = True

# カメラ番号は環境によって変わるため、CAMERA_INDEXで固定も自動探索もできる。
def open_camera():
    requested = os.environ.get("CAMERA_INDEX", "").strip()
    if requested:
        try:
            indices = [int(requested)]
        except ValueError as exc:
            raise RuntimeError("CAMERA_INDEX must be an integer") from exc
    else:
        # 以前動作していた番号0を最優先にする。番号0が使えない場合だけ
        # 他のカメラを探索する（環境によってContinuity Cameraの番号は変わる）。
        indices = [0] + list(range(1, 6))

    if os.name == "nt":
        backends = [cv2.CAP_DSHOW, cv2.CAP_MSMF]
    elif hasattr(cv2, "CAP_AVFOUNDATION"):
        # Continuity Cameraを含むmacOSのカメラはAVFoundationを優先する。
        backends = [cv2.CAP_AVFOUNDATION, cv2.CAP_ANY]
    else:
        backends = [cv2.CAP_ANY]

    for index in indices:
        for backend in backends:
            candidate = cv2.VideoCapture(index, backend)
            if not candidate.isOpened():
                candidate.release()
                continue
            ok, frame = candidate.read()
            if ok and frame is not None:
                print(f"Camera opened: index={index}, backend={backend}, size={frame.shape[1]}x{frame.shape[0]}")
                return candidate
            candidate.release()

    raise RuntimeError(
        "No camera frame could be read. Check macOS Camera permission, "
        "make sure the iPhone Continuity Camera is connected, or set CAMERA_INDEX."
    )


def selected_capture_backend(value=None):
    """Return the explicitly selected capture backend without opening a camera."""
    backend = (value if value is not None else
               os.environ.get("CAMERA_CAPTURE_BACKEND", "opencv")).strip().lower()
    if backend not in ("opencv", "native"):
        raise ValueError("CAMERA_CAPTURE_BACKEND must be 'opencv' or 'native'")
    return backend


def _environment_int(name, default):
    try:
        return int(os.environ.get(name, str(default)))
    except ValueError as exc:
        raise ValueError(f"{name} must be an integer") from exc


def open_native_camera():
    """Create the existing NativeLatestFrame source using environment settings."""
    from native_capture import NativeLatestFrame

    return NativeLatestFrame(
        os.environ.get("CAMERA_NATIVE_DEVICE_ID", "continuity"),
        _environment_int("CAMERA_NATIVE_WIDTH", 640),
        _environment_int("CAMERA_NATIVE_HEIGHT", 360),
        float(os.environ.get("CAMERA_NATIVE_FPS", "30")),
        _environment_int("CAMERA_NATIVE_FORMAT_INDEX", -1),
    )


def _native_dimensions(capture):
    info = getattr(capture, "info", {})
    active_format = info.get("active_format", {}) if isinstance(info, dict) else {}
    if not isinstance(active_format, dict):
        active_format = {}
    width = int(active_format.get("width", 0) or 0)
    height = int(active_format.get("height", 0) or 0)
    if width > 0 and height > 0:
        return width, height
    requested = info.get("requested", {}) if isinstance(info, dict) else {}
    if isinstance(requested, dict):
        return int(requested.get("width", 0) or 0), int(requested.get("height", 0) or 0)
    return 0, 0


# キャプチャ用スレッドと最新フレーム/送信用共有変数
cap = None
# 低解像度キャプチャをオプション化（デフォルト: 有効）
LOW_RES_CAPTURE = True

# キャプチャ開始直後のネイティブ解像度（送信時の基準とする）
try:
    ORIG_CAP_W = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
    ORIG_CAP_H = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
except Exception:
    ORIG_CAP_W = 0
    ORIG_CAP_H = 0

if LOW_RES_CAPTURE:
    try:
        cap.set(cv2.CAP_PROP_FRAME_WIDTH, 320)
        cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 240)
    except Exception:
        pass

# 実際のキャプチャ解像度（フレームごとに取得するが初期値をここに保持）
try:
    CAP_W = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
    CAP_H = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
except Exception:
    CAP_W = 0
    CAP_H = 0
_frame_lock = threading.Lock()
_frame_condition = threading.Condition(_frame_lock)
_latest_frame = None
_latest_frame_generation = 0
_running = True
last_frame = None

class FrameGrabber(threading.Thread):
    def __init__(self, cap):
        super().__init__(daemon=True)
        self.cap = cap

    def run(self):
        global _latest_frame, _latest_frame_generation, _running
        while _running:
            ret, frame = self.cap.read()
            if not ret:
                time.sleep(0.005)
                continue
            with _frame_condition:
                _latest_frame = frame
                _latest_frame_generation += 1
                _frame_condition.notify_all()

    def stop(self):
        global _running
        _running = False
        with _frame_condition:
            _frame_condition.notify_all()


class NativeFrameGrabber(threading.Thread):
    """Poll NativeLatestFrame and publish only frames newer than its sequence."""

    def __init__(self, capture):
        super().__init__(daemon=True)
        self.capture = capture
        self.sequence = -1
        self.error = None

    def run(self):
        global _latest_frame, _latest_frame_generation, _running
        while _running:
            try:
                frame, _timestamp, sequence = self.capture.get(self.sequence)
            except BaseException as exc:
                self.error = exc
                _running = False
                with _frame_condition:
                    _frame_condition.notify_all()
                return
            if frame is None or sequence <= self.sequence:
                time.sleep(0.001)
                continue
            self.sequence = sequence
            with _frame_condition:
                if not _running:
                    return
                _latest_frame = frame
                _latest_frame_generation += 1
                _frame_condition.notify_all()

    def stop(self):
        global _running
        _running = False
        with _frame_condition:
            _frame_condition.notify_all()


def wait_for_new_frame(last_generation, timeout=0.1):
    """最新フレームが更新されるまで待ち、画像と世代を1回だけ取得する。"""
    with _frame_condition:
        _frame_condition.wait_for(
            lambda: _latest_frame_generation > last_generation or not _running,
            timeout=timeout,
        )
        if _latest_frame is None or _latest_frame_generation <= last_generation:
            return None, _latest_frame_generation
        return _latest_frame, _latest_frame_generation


_payload_lock = threading.Lock()
last_payload_stick1 = None
last_payload_stick2 = None
last_sent_payload_stick1 = None
last_sent_payload_stick2 = None

class Sender(threading.Thread):
    def __init__(self, sock, hz=SEND_HZ):
        super().__init__(daemon=True)
        self.sock = sock
        self.hz = hz
        self.interval = 1.0 / float(hz)
        self._running = True

    def run(self):
        global last_payload_stick1, last_payload_stick2
        next_time = time.perf_counter()
        if LOG_DETECTIONS:
            print(f"Sender thread started, interval={self.interval:.4f}s, target={UDP_IP}:{UDP_PORT}")
        while self._running:
            now = time.perf_counter()
            if now >= next_time:
                with _send_lock:
                    with _payload_lock:
                        p1 = last_payload_stick1
                        p2 = last_payload_stick2
                    if p1 is not None:
                        try:
                            self.sock.sendto(p1, (UDP_IP, UDP_PORT))
                            if LOG_DETECTIONS:
                                print(f"SENT -> {UDP_IP}:{UDP_PORT} {p1}")
                        except Exception:
                            if LOG_DETECTIONS:
                                import traceback
                                print("Sender sendto exception:\n", traceback.format_exc())
                    if SEND_STICK2 and p2 is not None:
                        try:
                            self.sock.sendto(p2, (UDP_IP, UDP_PORT_STICK2))
                            if LOG_DETECTIONS:
                                print(f"SENT -> {UDP_IP}:{UDP_PORT_STICK2} {p2}")
                        except Exception:
                            if LOG_DETECTIONS:
                                import traceback
                                print("Sender sendto exception (stick2):\n", traceback.format_exc())
                # 保持位相
                while now >= next_time:
                    next_time += self.interval
            else:
                time.sleep(min(0.001, next_time - now))

    def stop(self):
        self._running = False

# 検出モード: "bright"(明るさベースで検出) または "color"(色ごとに棒2本)
# 明るさ重視で動かす
DETECT_MODE = "color"

THRESHOLD_CONFIG_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "camera_thresholds.json")

# 明るさ検出パラメータ
DEFAULT_BRIGHT_THRESHOLD = 201
DEFAULT_MIN_AREA = 170
# 近接した小領域を連結とみなすためのマージ距離（ピクセル）。0で無効。
DEFAULT_MERGE_DISTANCE = 5
# 送信する座標の反転設定(0/1)
DEFAULT_FLIP_H = 1
DEFAULT_FLIP_V = 1

# 色検出パラメータ(HSV)
# 必要に応じて値を調整してください。
# A=red. Red wraps around the OpenCV HSV hue boundary, so the mask helper
# below treats h_min > h_max as two ranges (170-179 and 0-10).
DEFAULT_COLOR_A_LOWER = np.array([170, 70, 100])
DEFAULT_COLOR_A_UPPER = np.array([10, 255, 255])
# B=blue.
DEFAULT_COLOR_B_LOWER = np.array([99, 137, 168])
DEFAULT_COLOR_B_UPPER = np.array([124, 255, 255])

BRIGHT_THRESHOLD = DEFAULT_BRIGHT_THRESHOLD
MIN_AREA = DEFAULT_MIN_AREA
MERGE_DISTANCE = DEFAULT_MERGE_DISTANCE
FLIP_H = DEFAULT_FLIP_H
FLIP_V = DEFAULT_FLIP_V
COLOR_A_LOWER = DEFAULT_COLOR_A_LOWER.copy()
COLOR_A_UPPER = DEFAULT_COLOR_A_UPPER.copy()
COLOR_B_LOWER = DEFAULT_COLOR_B_LOWER.copy()
COLOR_B_UPPER = DEFAULT_COLOR_B_UPPER.copy()


def _threshold_settings_from_globals():
    return {
        "bright": int(BRIGHT_THRESHOLD),
        "min_area": int(MIN_AREA),
        "merge_dist": int(MERGE_DISTANCE),
        "flip_h": int(FLIP_H),
        "flip_v": int(FLIP_V),
        "a_h_min": int(COLOR_A_LOWER[0]),
        "a_s_min": int(COLOR_A_LOWER[1]),
        "a_v_min": int(COLOR_A_LOWER[2]),
        "a_h_max": int(COLOR_A_UPPER[0]),
        "a_s_max": int(COLOR_A_UPPER[1]),
        "a_v_max": int(COLOR_A_UPPER[2]),
        "b_h_min": int(COLOR_B_LOWER[0]),
        "b_s_min": int(COLOR_B_LOWER[1]),
        "b_v_min": int(COLOR_B_LOWER[2]),
        "b_h_max": int(COLOR_B_UPPER[0]),
        "b_s_max": int(COLOR_B_UPPER[1]),
        "b_v_max": int(COLOR_B_UPPER[2]),
    }


def _apply_threshold_settings(settings):
    global BRIGHT_THRESHOLD, MIN_AREA, MERGE_DISTANCE, FLIP_H, FLIP_V
    global COLOR_A_LOWER, COLOR_A_UPPER, COLOR_B_LOWER, COLOR_B_UPPER

    BRIGHT_THRESHOLD = int(settings.get("bright", DEFAULT_BRIGHT_THRESHOLD))
    MIN_AREA = max(1, int(settings.get("min_area", DEFAULT_MIN_AREA)))
    MERGE_DISTANCE = max(0, int(settings.get("merge_dist", DEFAULT_MERGE_DISTANCE)))
    FLIP_H = 1 if int(settings.get("flip_h", DEFAULT_FLIP_H)) else 0
    FLIP_V = 1 if int(settings.get("flip_v", DEFAULT_FLIP_V)) else 0

    COLOR_A_LOWER = np.array([
        int(settings.get("a_h_min", int(DEFAULT_COLOR_A_LOWER[0]))),
        int(settings.get("a_s_min", int(DEFAULT_COLOR_A_LOWER[1]))),
        int(settings.get("a_v_min", int(DEFAULT_COLOR_A_LOWER[2]))),
    ])
    COLOR_A_UPPER = np.array([
        int(settings.get("a_h_max", int(DEFAULT_COLOR_A_UPPER[0]))),
        int(settings.get("a_s_max", int(DEFAULT_COLOR_A_UPPER[1]))),
        int(settings.get("a_v_max", int(DEFAULT_COLOR_A_UPPER[2]))),
    ])
    COLOR_B_LOWER = np.array([
        int(settings.get("b_h_min", int(DEFAULT_COLOR_B_LOWER[0]))),
        int(settings.get("b_s_min", int(DEFAULT_COLOR_B_LOWER[1]))),
        int(settings.get("b_v_min", int(DEFAULT_COLOR_B_LOWER[2]))),
    ])
    COLOR_B_UPPER = np.array([
        int(settings.get("b_h_max", int(DEFAULT_COLOR_B_UPPER[0]))),
        int(settings.get("b_s_max", int(DEFAULT_COLOR_B_UPPER[1]))),
        int(settings.get("b_v_max", int(DEFAULT_COLOR_B_UPPER[2]))),
    ])


def load_threshold_settings():
    if not os.path.exists(THRESHOLD_CONFIG_PATH):
        return
    try:
        with open(THRESHOLD_CONFIG_PATH, "r", encoding="utf-8") as f:
            settings = json.load(f)
        if isinstance(settings, dict):
            _apply_threshold_settings(settings)
    except Exception:
        pass


def save_threshold_settings():
    settings = _threshold_settings_from_globals()
    try:
        with open(THRESHOLD_CONFIG_PATH, "w", encoding="utf-8") as f:
            json.dump(settings, f, ensure_ascii=False, indent=2)
    except Exception:
        pass

TRACKBAR_WINDOW = "Thresholds"


def setup_trackbars():
    cv2.namedWindow(TRACKBAR_WINDOW, cv2.WINDOW_NORMAL)

    cv2.createTrackbar("bright", TRACKBAR_WINDOW, BRIGHT_THRESHOLD, 255, lambda _v: None)
    cv2.createTrackbar("min_area", TRACKBAR_WINDOW, MIN_AREA, 5000, lambda _v: None)
    cv2.createTrackbar("merge_dist", TRACKBAR_WINDOW, MERGE_DISTANCE, 200, lambda _v: None)
    cv2.createTrackbar("flip_h", TRACKBAR_WINDOW, FLIP_H, 1, lambda _v: None)
    cv2.createTrackbar("flip_v", TRACKBAR_WINDOW, FLIP_V, 1, lambda _v: None)

    cv2.createTrackbar("a_h_min", TRACKBAR_WINDOW, int(COLOR_A_LOWER[0]), 179, lambda _v: None)
    cv2.createTrackbar("a_s_min", TRACKBAR_WINDOW, int(COLOR_A_LOWER[1]), 255, lambda _v: None)
    cv2.createTrackbar("a_v_min", TRACKBAR_WINDOW, int(COLOR_A_LOWER[2]), 255, lambda _v: None)
    cv2.createTrackbar("a_h_max", TRACKBAR_WINDOW, int(COLOR_A_UPPER[0]), 179, lambda _v: None)
    cv2.createTrackbar("a_s_max", TRACKBAR_WINDOW, int(COLOR_A_UPPER[1]), 255, lambda _v: None)
    cv2.createTrackbar("a_v_max", TRACKBAR_WINDOW, int(COLOR_A_UPPER[2]), 255, lambda _v: None)

    cv2.createTrackbar("b_h_min", TRACKBAR_WINDOW, int(COLOR_B_LOWER[0]), 179, lambda _v: None)
    cv2.createTrackbar("b_s_min", TRACKBAR_WINDOW, int(COLOR_B_LOWER[1]), 255, lambda _v: None)
    cv2.createTrackbar("b_v_min", TRACKBAR_WINDOW, int(COLOR_B_LOWER[2]), 255, lambda _v: None)
    cv2.createTrackbar("b_h_max", TRACKBAR_WINDOW, int(COLOR_B_UPPER[0]), 179, lambda _v: None)
    cv2.createTrackbar("b_s_max", TRACKBAR_WINDOW, int(COLOR_B_UPPER[1]), 255, lambda _v: None)
    cv2.createTrackbar("b_v_max", TRACKBAR_WINDOW, int(COLOR_B_UPPER[2]), 255, lambda _v: None)


def enable_ui():
    """UI（トラックバーと表示ウィンドウ）を有効にする。実行中に呼び出して動的に切り替え可能。"""
    global SHOW_UI
    if SHOW_UI:
        return
    try:
        setup_trackbars()
        cv2.namedWindow("LED Tracking", cv2.WINDOW_NORMAL)
        cv2.setMouseCallback("LED Tracking", mouse_callback)
        SHOW_UI = True
    except Exception:
        # UI が作れない環境では無視
        SHOW_UI = False


def disable_ui():
    """UI を無効にしてウィンドウを閉じる。"""
    global SHOW_UI
    try:
        if cv2.getWindowProperty(TRACKBAR_WINDOW, cv2.WND_PROP_VISIBLE) >= 0:
            cv2.destroyWindow(TRACKBAR_WINDOW)
    except Exception:
        pass
    try:
        if cv2.getWindowProperty("LED Tracking", cv2.WND_PROP_VISIBLE) >= 0:
            cv2.destroyWindow("LED Tracking")
    except Exception:
        pass
    SHOW_UI = False


def mouse_callback(event, x, y, flags, param):
    """クリックした画素の HSV 値を出力します。閾値調整の補助に使ってください。"""
    global last_frame
    if event == cv2.EVENT_LBUTTONDOWN and last_frame is not None:
        hsv = cv2.cvtColor(last_frame, cv2.COLOR_BGR2HSV)
        h, s, v = hsv[y, x]
        if LOG_DETECTIONS:
            print(f"HSV at ({x},{y}) = {h},{s},{v}")


def hsv_mask(hsv, lower, upper):
    """Build an HSV mask and support colors crossing hue 0, such as red."""
    if int(lower[0]) <= int(upper[0]):
        return cv2.inRange(hsv, lower, upper)

    lower_high = np.array([lower[0], lower[1], lower[2]], dtype=np.uint8)
    upper_high = np.array([179, upper[1], upper[2]], dtype=np.uint8)
    lower_low = np.array([0, lower[1], lower[2]], dtype=np.uint8)
    upper_low = np.array([upper[0], upper[1], upper[2]], dtype=np.uint8)
    return cv2.bitwise_or(
        cv2.inRange(hsv, lower_high, upper_high),
        cv2.inRange(hsv, lower_low, upper_low),
    )


def update_thresholds_from_trackbars():
    global BRIGHT_THRESHOLD, MIN_AREA, COLOR_A_LOWER, COLOR_A_UPPER, COLOR_B_LOWER, COLOR_B_UPPER, MERGE_DISTANCE, FLIP_H, FLIP_V

    BRIGHT_THRESHOLD = cv2.getTrackbarPos("bright", TRACKBAR_WINDOW)
    MIN_AREA = max(1, cv2.getTrackbarPos("min_area", TRACKBAR_WINDOW))

    COLOR_A_LOWER = np.array([
        cv2.getTrackbarPos("a_h_min", TRACKBAR_WINDOW),
        cv2.getTrackbarPos("a_s_min", TRACKBAR_WINDOW),
        cv2.getTrackbarPos("a_v_min", TRACKBAR_WINDOW),
    ])
    COLOR_A_UPPER = np.array([
        cv2.getTrackbarPos("a_h_max", TRACKBAR_WINDOW),
        cv2.getTrackbarPos("a_s_max", TRACKBAR_WINDOW),
        cv2.getTrackbarPos("a_v_max", TRACKBAR_WINDOW),
    ])

    COLOR_B_LOWER = np.array([
        cv2.getTrackbarPos("b_h_min", TRACKBAR_WINDOW),
        cv2.getTrackbarPos("b_s_min", TRACKBAR_WINDOW),
        cv2.getTrackbarPos("b_v_min", TRACKBAR_WINDOW),
    ])
    COLOR_B_UPPER = np.array([
        cv2.getTrackbarPos("b_h_max", TRACKBAR_WINDOW),
        cv2.getTrackbarPos("b_s_max", TRACKBAR_WINDOW),
        cv2.getTrackbarPos("b_v_max", TRACKBAR_WINDOW),
    ])
    # トラックバーからマージ距離を取得（ピクセル）
    try:
        MERGE_DISTANCE = int(cv2.getTrackbarPos("merge_dist", TRACKBAR_WINDOW))
    except Exception:
        MERGE_DISTANCE = 0
    # 反転トグル
    try:
        FLIP_H = int(cv2.getTrackbarPos("flip_h", TRACKBAR_WINDOW))
        FLIP_V = int(cv2.getTrackbarPos("flip_v", TRACKBAR_WINDOW))
    except Exception:
        FLIP_H = 0
        FLIP_V = 0

    save_threshold_settings()


def contour_center(contour):
    m = cv2.moments(contour)
    if m["m00"] <= 0:
        return None
    return int(m["m10"] / m["m00"]), int(m["m01"] / m["m00"])


def find_largest_center(mask):
    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    if not contours:
        return None

    contours = [c for c in contours if cv2.contourArea(c) >= MIN_AREA]
    if not contours:
        return None

    c = max(contours, key=cv2.contourArea)
    return contour_center(c)


def find_top2_centers_from_mask(mask):
    contours, _ = cv2.findContours(mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    contours = [c for c in contours if cv2.contourArea(c) >= MIN_AREA]
    if len(contours) < 2:
        return None, None

    contours = sorted(contours, key=cv2.contourArea, reverse=True)[:2]
    p1 = contour_center(contours[0])
    p2 = contour_center(contours[1])
    if p1 is None or p2 is None:
        return None, None
    return p1, p2


def endpoints_from_rotated_rect(contour):
    rect = cv2.minAreaRect(contour)
    (cx, cy), (w, h), angle = rect

    # 長辺方向を棒の向きとして扱う
    if w >= h:
        theta = math.radians(angle)
        half = w * 0.5
    else:
        theta = math.radians(angle + 90.0)
        half = h * 0.5

    dx = math.cos(theta) * half
    dy = math.sin(theta) * half

    p1 = (int(cx - dx), int(cy - dy))
    p2 = (int(cx + dx), int(cy + dy))
    return p1, p2


def farthest_point_pair(contour):
    """凸包上で回転カルーセル(rotating calipers)を使って最遠点対を求める。
    凸包頂点数 m に対して O(m) で動作する。contour が None または点数不足なら (None,None) を返す。
    """
    if contour is None:
        return None, None

    pts = contour.reshape(-1, 2)
    if pts.shape[0] < 2:
        return None, None

    # OpenCV の convexHull は閉じていない順序で返す（CW/CCW）。returnPoints=True で点列を取得する。
    hull = cv2.convexHull(pts, returnPoints=True)
    hull_pts = hull.reshape(-1, 2)
    m = len(hull_pts)
    if m < 2:
        return None, None
    if m == 2:
        return (int(hull_pts[0, 0]), int(hull_pts[0, 1])), (int(hull_pts[1, 0]), int(hull_pts[1, 1]))

    def dist2(a, b):
        dx = int(a[0]) - int(b[0])
        dy = int(a[1]) - int(b[1])
        return dx * dx + dy * dy

    max_d2 = -1
    best = (None, None)

    # 回転カルーセルの初期 j を 1 にする
    j = 1
    for i in range(m):
        ni = (i + 1) % m
        # 辺 e = P[ni] - P[i]
        ex = hull_pts[ni, 0] - hull_pts[i, 0]
        ey = hull_pts[ni, 1] - hull_pts[i, 1]

        # j を進められるだけ進める（面積の増加を比較）
        while True:
            nj = (j + 1) % m
            # cross(e, P[nj]-P[i]) - cross(e, P[j]-P[i]) > 0 なら j を進める
            vj_x = hull_pts[j, 0] - hull_pts[i, 0]
            vj_y = hull_pts[j, 1] - hull_pts[i, 1]
            vnj_x = hull_pts[nj, 0] - hull_pts[i, 0]
            vnj_y = hull_pts[nj, 1] - hull_pts[i, 1]
            cross_curr = ex * vj_y - ey * vj_x
            cross_next = ex * vnj_y - ey * vnj_x
            if cross_next > cross_curr:
                j = nj
            else:
                break

        # 現在の j について距離をチェック
        d2 = dist2(hull_pts[i], hull_pts[j])
        if d2 > max_d2:
            max_d2 = d2
            best = ((int(hull_pts[i, 0]), int(hull_pts[i, 1])), (int(hull_pts[j, 0]), int(hull_pts[j, 1])))

        d2 = dist2(hull_pts[ni], hull_pts[j])
        if d2 > max_d2:
            max_d2 = d2
            best = ((int(hull_pts[ni, 0]), int(hull_pts[ni, 1])), (int(hull_pts[j, 0]), int(hull_pts[j, 1])))

    return best


def endpoints_via_pca(contour):
    """輪郭点の主成分（PCA）を取り、主軸方向への射影で最小/最大点を端点とする。
    細長い形状の両端を安定して取るのに有効。"""
    if contour is None:
        return None, None

    pts = contour.reshape(-1, 2).astype(float)
    if pts.shape[0] < 2:
        return None, None

    # 中心化
    mean = pts.mean(axis=0)
    centered = pts - mean

    # 共分散行列と固有ベクトル
    cov = np.cov(centered, rowvar=False)
    try:
        eigvals, eigvecs = np.linalg.eigh(cov)
    except Exception:
        return None, None

    # 最大固有値に対応する固有ベクトル（主成分）
    principal = eigvecs[:, np.argmax(eigvals)]

    # 各点を主軸に射影し、最小/最大の点を選ぶ
    projections = centered.dot(principal)
    min_idx = int(np.argmin(projections))
    max_idx = int(np.argmax(projections))
    p1 = (int(pts[min_idx, 0]), int(pts[min_idx, 1]))
    p2 = (int(pts[max_idx, 0]), int(pts[max_idx, 1]))
    return p1, p2


def _contour_stick_metrics(contour):
    area = float(cv2.contourArea(contour))
    if area <= 0:
        return None

    rect = cv2.minAreaRect(contour)
    (cx, cy), (w, h), angle = rect
    long_side = max(float(w), float(h), 1.0)
    short_side = max(min(float(w), float(h)), 1.0)
    aspect = long_side / short_side
    rect_area = max(float(w) * float(h), 1.0)
    extent = area / rect_area
    bbox = cv2.boundingRect(contour)
    bbox_area = max(float(bbox[2] * bbox[3]), 1.0)
    bbox_extent = area / bbox_area

    pts = contour.reshape(-1, 2).astype(float)
    mean = pts.mean(axis=0)
    centered = pts - mean
    try:
        cov = np.cov(centered, rowvar=False)
        eigvals, _ = np.linalg.eigh(cov)
        eigvals = np.sort(np.maximum(eigvals, 0.0))
        elongation = float('inf') if eigvals[-2] <= 1e-6 else float(eigvals[-1] / eigvals[-2])
    except Exception:
        elongation = 0.0

    return {
        'area': area,
        'rect': rect,
        'aspect': aspect,
        'extent': extent,
        'bbox_extent': bbox_extent,
        'elongation': elongation,
        'center': (cx, cy),
        'long_side': long_side,
        'short_side': short_side,
    }


def _is_stick_like(metrics, frame_area):
    if metrics is None:
        return False

    area = metrics['area']
    aspect = metrics['aspect']
    extent = metrics['extent']
    bbox_extent = metrics['bbox_extent']
    elongation = metrics['elongation']
    area_ratio = area / max(float(frame_area), 1.0)

    # 棒状に見えるものだけを通す。服や大きな背景物体はここで落ちやすい。
    if area_ratio > 0.22:
        return False
    if aspect < 1.5:
        return False
    if extent < 0.10:
        return False
    if bbox_extent < 0.08:
        return False
    if elongation and elongation < 2.2:
        return False
    return True


def _stick_score(metrics, frame_area):
    area_ratio = metrics['area'] / max(float(frame_area), 1.0)
    return (
        metrics['aspect'] * 2.0
        + metrics['extent'] * 3.0
        + metrics['bbox_extent'] * 1.5
        + min(metrics['elongation'], 30.0) * 0.25
        - area_ratio * 10.0
    )


def build_stick_candidate_mask(mask):
    """デバッグ表示用に、棒状判定を通過した輪郭だけを残す。"""
    if MERGE_DISTANCE and MERGE_DISTANCE > 0:
        k = max(1, min(31, int(MERGE_DISTANCE)))
        kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k))
        filtered = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel)
    else:
        filtered = mask.copy()
    open_kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3))
    filtered = cv2.morphologyEx(filtered, cv2.MORPH_OPEN, open_kernel)

    result = np.zeros_like(filtered)
    contours, _ = cv2.findContours(filtered, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    frame_area = float(filtered.shape[0] * filtered.shape[1])
    candidates = []
    for contour in contours:
        if cv2.contourArea(contour) < MIN_AREA:
            continue
        metrics = _contour_stick_metrics(contour)
        if _is_stick_like(metrics, frame_area):
            candidates.append((contour, metrics))
    if candidates:
        best_contour, best_metrics = max(
            candidates,
            key=lambda item: _stick_score(item[1], frame_area),
        )
        cv2.drawContours(result, [best_contour], -1, 255, -1)
    return result


def find_stick_endpoints_from_mask(mask, debug_name=""):
    # マージ距離が設定されていれば閉処理で小さなギャップを埋めてから輪郭抽出する
    if 'MERGE_DISTANCE' in globals() and MERGE_DISTANCE and MERGE_DISTANCE > 0:
        # カーネルは過度に大きくしない（処理負荷の抑制と安定化）
        k = max(1, min(31, int(MERGE_DISTANCE)))
        if USE_MPS_ACCEL:
            try:
                proc_mask = gpu_morph_close(mask, k)
            except Exception:
                kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k))
                proc_mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel)
        else:
            kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (k, k))
            proc_mask = cv2.morphologyEx(mask, cv2.MORPH_CLOSE, kernel)
    else:
        proc_mask = mask.copy()

    # 小さな点ノイズを落とす
    open_kernel = cv2.getStructuringElement(cv2.MORPH_ELLIPSE, (3, 3))
    proc_mask = cv2.morphologyEx(proc_mask, cv2.MORPH_OPEN, open_kernel)

    contours, _ = cv2.findContours(proc_mask, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    frame_area = float(proc_mask.shape[0] * proc_mask.shape[1])
    contour_data = []
    for c in contours:
        if cv2.contourArea(c) < MIN_AREA:
            continue
        metrics = _contour_stick_metrics(c)
        if not _is_stick_like(metrics, frame_area):
            if LOG_DETECTIONS:
                print(f"[{debug_name}] 棒状でない輪郭を除外: area={metrics['area']:.1f}, aspect={metrics['aspect']:.2f}, extent={metrics['extent']:.2f}, bbox_extent={metrics['bbox_extent']:.2f}, elongation={metrics['elongation']:.2f}")
            continue
        contour_data.append((c, metrics))

    if LOG_DETECTIONS:
        print(f"[{debug_name}] 検出輪郭数: 全{len(contours)}個 > 棒状候補{len(contour_data)}個 (MIN_AREA={MIN_AREA})")

    if not contour_data:
        return None, None

    c, metrics = max(contour_data, key=lambda item: _stick_score(item[1], frame_area))
    area = metrics['area']
    rect = metrics['rect']
    (cx, cy), (w, h), angle = rect
    if LOG_DETECTIONS:
        print(f"[{debug_name}] 採用輪郭: 面積={area:.1f}, aspect={metrics['aspect']:.2f}, extent={metrics['extent']:.2f}, bbox_extent={metrics['bbox_extent']:.2f}, elongation={metrics['elongation']:.2f}, rect(w={w:.1f}, h={h:.1f}, angle={angle:.1f})")

    # 2点が十分離れている場合だけ採用
    # 端点は全画面ではなく、採用した輪郭だけから計算する。
    candidate_mask = np.zeros_like(proc_mask)
    cv2.drawContours(candidate_mask, [c], -1, 255, -1)
    p1, p2 = find_top2_centers_from_mask(candidate_mask)
    if p1 is not None and p2 is not None:
        bx, by, bw, bh = cv2.boundingRect(c)
        diag = max(bw, bh)
        dist = math.hypot(p2[0] - p1[0], p2[1] - p1[1])
        if dist >= 0.7 * diag:
            if LOG_DETECTIONS:
                print(f"[{debug_name}] 2点検出モード: {p1}, {p2} (dist={dist:.1f} diag={diag})")
            return p1, p2
        elif LOG_DETECTIONS:
            print(f"[{debug_name}] 2点は近すぎるため無視: dist={dist:.1f} diag={diag}")

    # 回転矩形を優先して、形状に沿った端点を返す
    p1, p2 = endpoints_from_rotated_rect(c)
    if p1 is not None and p2 is not None:
        if LOG_DETECTIONS:
            print(f"[{debug_name}] rotrect端点対: {p1}, {p2}")
        return p1, p2

    p1, p2 = endpoints_via_pca(c)
    if p1 is not None and p2 is not None:
        if LOG_DETECTIONS:
            print(f"[{debug_name}] PCA端点対: {p1}, {p2}")
        return p1, p2

    p1, p2 = farthest_point_pair(c)
    if p1 is not None and p2 is not None:
        if LOG_DETECTIONS:
            print(f"[{debug_name}] 凸包最遠点対: {p1}, {p2}")
        return p1, p2

    if LOG_DETECTIONS:
        print(f"[{debug_name}] 端点計算失敗")
    return None, None


def find_top2_bright_centers(frame):
    gray = cv2.cvtColor(frame, cv2.COLOR_BGR2GRAY)
    _, thresh = cv2.threshold(gray, BRIGHT_THRESHOLD, 255, cv2.THRESH_BINARY)

    # ノイズ除去
    kernel = np.ones((3, 3), np.uint8)
    thresh = cv2.morphologyEx(thresh, cv2.MORPH_OPEN, kernel)

    contours, _ = cv2.findContours(thresh, cv2.RETR_EXTERNAL, cv2.CHAIN_APPROX_SIMPLE)
    contours = [c for c in contours if cv2.contourArea(c) >= MIN_AREA]
    if len(contours) < 2:
        return None, None

    contours = sorted(contours, key=cv2.contourArea, reverse=True)[:2]
    p1 = contour_center(contours[0])
    p2 = contour_center(contours[1])

    if p1 is None or p2 is None:
        return None, None
    return p1, p2


def find_color_pair(frame=None, hsv=None, masks=None):
    if masks is None:
        if hsv is None:
            hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
        masks = (
            hsv_mask(hsv, COLOR_A_LOWER, COLOR_A_UPPER),
            hsv_mask(hsv, COLOR_B_LOWER, COLOR_B_UPPER),
        )
    mask_a, mask_b = masks

    # 色ごとに1本ずつ棒を作る(2点検出/長軸検出の両対応)
    a1, a2 = find_stick_endpoints_from_mask(mask_a, "COLOR_A")
    b1, b2 = find_stick_endpoints_from_mask(mask_b, "COLOR_B")
    return (a1, a2), (b1, b2)


def detect_frame(frame):
    """1フレーム分の色変換・マスク生成・検出をまとめて行う。"""
    hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
    masks = (
        hsv_mask(hsv, COLOR_A_LOWER, COLOR_A_UPPER),
        hsv_mask(hsv, COLOR_B_LOWER, COLOR_B_UPPER),
    )
    (a1, a2), (b1, b2) = find_color_pair(hsv=hsv, masks=masks)
    return build_stick(a1, a2), build_stick(b1, b2), masks


def build_stick(p1, p2):
    if p1 is None or p2 is None:
        return None

    x1, y1 = p1
    x2, y2 = p2
    cx = int((x1 + x2) / 2)
    cy = int((y1 + y2) / 2)
    length = math.hypot(x2 - x1, y2 - y1)
    angle_deg = math.degrees(math.atan2(y2 - y1, x2 - x1))

    return {
        "p1": p1,
        "p2": p2,
        "center": (cx, cy),
        "length": length,
        "angle": angle_deg,
    }


def draw_stick(frame, stick, color_line, label):
    (x1, y1) = stick["p1"]
    (x2, y2) = stick["p2"]
    (cx, cy) = stick["center"]

    cv2.circle(frame, (x1, y1), 8, (0, 0, 255), -1)
    cv2.circle(frame, (x2, y2), 8, (255, 0, 0), -1)
    cv2.line(frame, (x1, y1), (x2, y2), color_line, 2)
    cv2.circle(frame, (cx, cy), 6, color_line, -1)
    cv2.putText(
        frame,
        f"{label} len:{stick['length']:.1f} ang:{stick['angle']:.1f}",
        (cx + 8, cy - 8),
        cv2.FONT_HERSHEY_SIMPLEX,
        0.45,
        color_line,
        1,
        cv2.LINE_AA,
    )

def payload_for_stick(stick, frame_shape):
    if stick is None:
        return None
    h, w = frame_shape[:2]
    scale_x = (ORIG_CAP_W / w) if ORIG_CAP_W and w else 1.0
    scale_y = (ORIG_CAP_H / h) if ORIG_CAP_H and h else 1.0
    coords = []
    for x, y in (stick['p1'], stick['p2']):
        sx = int(round(x * scale_x))
        sy = int(round(y * scale_y))
        coords.extend(((ORIG_CAP_W - sx) if FLIP_H and ORIG_CAP_W else (-sx if FLIP_H else sx),
                       (ORIG_CAP_H - sy) if FLIP_V and ORIG_CAP_H else (-sy if FLIP_V else sy)))
    return f"{coords[0]},{coords[1]},{coords[2]},{coords[3]}".encode('ascii')


def publish_payload(payload, stick_number, udp_socket):
    global last_payload_stick1, last_payload_stick2
    global last_sent_payload_stick1, last_sent_payload_stick2
    if stick_number == 2 and not SEND_STICK2:
        return
    port = UDP_PORT if stick_number == 1 else UDP_PORT_STICK2
    with _send_lock:
        with _payload_lock:
            if stick_number == 1:
                last_payload_stick1 = payload
                already_sent = last_sent_payload_stick1
            else:
                last_payload_stick2 = payload
                already_sent = last_sent_payload_stick2
        if IMMEDIATE_SEND_ON_CHANGE and payload != already_sent:
            try:
                udp_socket.sendto(payload, (UDP_IP, port))
                with _payload_lock:
                    if stick_number == 1:
                        last_sent_payload_stick1 = payload
                    else:
                        last_sent_payload_stick2 = payload
            except Exception:
                if LOG_DETECTIONS:
                    import traceback
                    print("Immediate send exception:\n", traceback.format_exc())


def clear_payload(stick_number):
    """未検出時の共有値を送信スナップショットと同期してクリアする。"""
    global last_payload_stick1, last_payload_stick2
    with _send_lock:
        with _payload_lock:
            if stick_number == 1:
                last_payload_stick1 = None
            else:
                last_payload_stick2 = None


def handle_key(key):
    """待機中を含む全ループのキー入力を処理する。"""
    global _running
    if key == ord('q'):
        _running = False
        return False
    if key == ord('u'):
        if SHOW_UI:
            disable_ui()
        else:
            enable_ui()
    return True


def display_detection(masks, sticks):
    """検出結果を表示する。送信後に呼び、無効時は表示処理を省略する。"""
    if SHOW_UI:
        cv2.imshow("mask_a", masks[0])
        cv2.imshow("mask_b", masks[1])
    if SHOW_DETECTED:
        for mask, stick, name in zip(masks, sticks, ("mask_a_detected", "mask_b_detected")):
            display = cv2.cvtColor(build_stick_candidate_mask(mask), cv2.COLOR_GRAY2BGR)
            if stick:
                cv2.circle(display, stick['p1'], 4, (0, 255, 0), -1)
                cv2.circle(display, stick['p2'], 4, (255, 0, 0), -1)
                cv2.line(display, stick['p1'], stick['p2'], (0, 255, 255), 4)
            cv2.imshow(name, display)


def close_capture(capture, grabber, backend):
    """Stop the reader before releasing the underlying capture source."""
    grabber.stop()
    grabber.join(timeout=1.0)
    is_alive = getattr(grabber, "is_alive", lambda: False)
    if is_alive():
        print("Camera reader is still stopping; skipping concurrent capture release.")
        return
    if backend == "native":
        stop = getattr(capture, "stop", None)
        if stop is not None:
            stop()
        else:
            capture.release()
    else:
        capture.release()


def _native_timeout(value=None):
    raw = value if value is not None else os.environ.get("CAMERA_FRAME_TIMEOUT", "10")
    try:
        timeout = float(raw)
    except (TypeError, ValueError) as exc:
        raise ValueError("CAMERA_FRAME_TIMEOUT must be positive and finite") from exc
    if not math.isfinite(timeout) or timeout <= 0:
        raise ValueError("CAMERA_FRAME_TIMEOUT must be positive and finite")
    return timeout


def main(capture=None, udp_socket=None, key_reader=None, capture_backend=None,
         frame_timeout=None):
    global cap, sock, ORIG_CAP_W, ORIG_CAP_H, CAP_W, CAP_H, _running, last_frame
    global _latest_frame, _latest_frame_generation
    backend = selected_capture_backend(capture_backend)
    cap = capture if capture is not None else (
        open_native_camera() if backend == "native" else open_camera()
    )
    sock = udp_socket if udp_socket is not None else socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    read_key = key_reader or (lambda: cv2.waitKey(1) & 0xFF)
    if backend == "native":
        frame_timeout = _native_timeout(frame_timeout)
        ORIG_CAP_W, ORIG_CAP_H = _native_dimensions(cap)
        CAP_W, CAP_H = ORIG_CAP_W, ORIG_CAP_H
    else:
        try:
            ORIG_CAP_W = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
            ORIG_CAP_H = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
        except Exception:
            ORIG_CAP_W = ORIG_CAP_H = 0
        if LOW_RES_CAPTURE:
            cap.set(cv2.CAP_PROP_FRAME_WIDTH, 320)
            cap.set(cv2.CAP_PROP_FRAME_HEIGHT, 240)
        CAP_W = int(cap.get(cv2.CAP_PROP_FRAME_WIDTH) or 0)
        CAP_H = int(cap.get(cv2.CAP_PROP_FRAME_HEIGHT) or 0)
    load_threshold_settings()
    if SHOW_UI:
        setup_trackbars()
        cv2.namedWindow("LED Tracking", cv2.WINDOW_NORMAL)
        cv2.setMouseCallback("LED Tracking", mouse_callback)
    if SHOW_DETECTED:
        cv2.namedWindow("mask_a_detected", cv2.WINDOW_NORMAL)
        cv2.namedWindow("mask_b_detected", cv2.WINDOW_NORMAL)
    with _frame_condition:
        _latest_frame = None
        _latest_frame_generation = 0
    _running = True
    if backend == "native":
        start = getattr(cap, "start", None)
        if start is not None:
            start()
        grabber = NativeFrameGrabber(cap)
    else:
        grabber = FrameGrabber(cap)
    sender = Sender(sock, hz=SEND_HZ)
    grabber.start()
    sender.start()
    generation = 0
    last_native_frame_at = time.monotonic()
    try:
        while _running:
            if SHOW_UI:
                update_thresholds_from_trackbars()
            frame, generation = wait_for_new_frame(generation, timeout=0.05)
            if frame is None:
                if getattr(grabber, "error", None) is not None:
                    raise RuntimeError("Native camera reader failed") from grabber.error
                if (backend == "native" and
                        time.monotonic() - last_native_frame_at > frame_timeout):
                    raise RuntimeError(
                        "No fresh native camera frames; refusing to process an old frame."
                    )
                if not handle_key(read_key()):
                    break
                continue
            if backend == "native":
                last_native_frame_at = time.monotonic()
                if not ORIG_CAP_W or not ORIG_CAP_H:
                    ORIG_CAP_W, ORIG_CAP_H = frame.shape[1], frame.shape[0]
            last_frame = frame
            stick1, stick2, masks = detect_frame(frame) if DETECT_MODE == "color" else (None, None, (None, None))
            publish_payload(payload_for_stick(stick1, frame.shape), 1, sock) if stick1 else None
            publish_payload(payload_for_stick(stick2, frame.shape), 2, sock) if stick2 else None
            if not stick1 and not HOLD_LAST_VALUE_WHEN_MISSING:
                clear_payload(1)
            if not stick2 and not HOLD_LAST_VALUE_WHEN_MISSING:
                clear_payload(2)
            # 検出結果の公開・即時送信を表示処理より先に行う。
            display_detection(masks, (stick1, stick2))
            if not handle_key(read_key()):
                break
            if SHOW_UI:
                try:
                    cv2.imshow("LED Tracking", frame)
                    cv2.imshow(TRACKBAR_WINDOW, np.zeros((1, 520, 3), dtype=np.uint8))
                except Exception:
                    disable_ui()
        if getattr(grabber, "error", None) is not None:
            raise RuntimeError("Native camera reader failed") from grabber.error
    except KeyboardInterrupt:
        pass
    finally:
        _running = False
        sender.stop()
        sender.join(timeout=1.0)
        close_capture(cap, grabber, backend)
        if SHOW_UI or SHOW_DETECTED:
            cv2.destroyAllWindows()


if __name__ == "__main__":
    main()
