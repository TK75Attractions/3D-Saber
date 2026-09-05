import os
import sys
import platform
from python_runtime import reexec_with_cv2

reexec_with_cv2()

# 特定の Python に固定しない。
# macOS では pyenv の Python を明示できるが、Windows ではそのパスが存在しないため
# そのまま現在の実行環境を使う。
_TARGET_PYTHON = os.environ.get("CAMERA_PYTHON")
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
sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
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


# キャプチャ用スレッドと最新フレーム/送信用共有変数
cap = open_camera()
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
_latest_frame = None
_running = True

class FrameGrabber(threading.Thread):
    def __init__(self, cap):
        super().__init__(daemon=True)
        self.cap = cap

    def run(self):
        global _latest_frame, _running
        while _running:
            ret, frame = self.cap.read()
            if not ret:
                time.sleep(0.001)
                continue
            with _frame_lock:
                _latest_frame = frame

    def stop(self):
        pass


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


def find_color_pair(frame):
    hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)

    mask_a = hsv_mask(hsv, COLOR_A_LOWER, COLOR_A_UPPER)
    mask_b = hsv_mask(hsv, COLOR_B_LOWER, COLOR_B_UPPER)

    # 色ごとに1本ずつ棒を作る(2点検出/長軸検出の両対応)
    a1, a2 = find_stick_endpoints_from_mask(mask_a, "COLOR_A")
    b1, b2 = find_stick_endpoints_from_mask(mask_b, "COLOR_B")
    return (a1, a2), (b1, b2)


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

load_threshold_settings()

if LOG_DETECTIONS:
    print(cap.get(cv2.CAP_PROP_FRAME_WIDTH))
    print(cap.get(cv2.CAP_PROP_FRAME_HEIGHT))

if SHOW_UI:
    setup_trackbars()
    cv2.namedWindow("LED Tracking", cv2.WINDOW_NORMAL)
    cv2.setMouseCallback("LED Tracking", mouse_callback)

# 軽量表示: 検出結果だけ残す場合は専用ウィンドウを用意
if SHOW_DETECTED:
    cv2.namedWindow("mask_a_detected", cv2.WINDOW_NORMAL)
    cv2.namedWindow("mask_b_detected", cv2.WINDOW_NORMAL)

# スレッド開始
grabber = FrameGrabber(cap)
grabber.start()
sender = Sender(sock, hz=SEND_HZ)
sender.start()

try:
    # メイン処理は送信Hzに合わせて制限して軽量化
    process_interval = 1.0 / float(SEND_HZ)
    next_proc = time.perf_counter()
    while True:
        if SHOW_UI:
            update_thresholds_from_trackbars()
        with _frame_lock:
            frame = None if _latest_frame is None else _latest_frame.copy()
        if frame is None:
            time.sleep(0.001)
            continue
        last_frame = frame.copy()

        # 処理レート制御: 次の処理までスリープして負荷を抑える
        now_proc = time.perf_counter()
        if now_proc < next_proc:
            time.sleep(min(0.001, next_proc - now_proc))
            continue
        next_proc += process_interval
        stick1 = None
        stick2 = None

        if DETECT_MODE == "color":
            # デバッグ表示用にマスクを見えるようにする
            hsv = cv2.cvtColor(frame, cv2.COLOR_BGR2HSV)
            mask_a = hsv_mask(hsv, COLOR_A_LOWER, COLOR_A_UPPER)
            mask_b = hsv_mask(hsv, COLOR_B_LOWER, COLOR_B_UPPER)

            # マスク画像にコントラストを付けて見やすくする
            mask_a_display = cv2.cvtColor(build_stick_candidate_mask(mask_a), cv2.COLOR_GRAY2BGR)
            mask_b_display = cv2.cvtColor(build_stick_candidate_mask(mask_b), cv2.COLOR_GRAY2BGR)

            if SHOW_UI:
                cv2.imshow("mask_a", mask_a)
                cv2.imshow("mask_b", mask_b)

            (a1, a2), (b1, b2) = find_color_pair(frame)
            stick1 = build_stick(a1, a2)  # color1同士の棒
            stick2 = build_stick(b1, b2)  # color2同士の棒

            # 検出結果をマスク上に描画
            if a1 is not None and a2 is not None:
                cv2.circle(mask_a_display, a1, 4, (0, 255, 0), -1)
                cv2.circle(mask_a_display, a2, 4, (255, 0, 0), -1)
                cv2.line(mask_a_display, a1, a2, (0, 255, 255), 4)
            if SHOW_DETECTED:
                cv2.imshow("mask_a_detected", mask_a_display)

            if b1 is not None and b2 is not None:
                cv2.circle(mask_b_display, b1, 4, (0, 255, 0), -1)
                cv2.circle(mask_b_display, b2, 4, (255, 0, 0), -1)
                cv2.line(mask_b_display, b1, b2, (0, 255, 255), 4)
            if SHOW_DETECTED:
                cv2.imshow("mask_b_detected", mask_b_display)

        # 色モードのみ動作。未検出時は HOLD_LAST_VALUE_WHEN_MISSING に従いペイロードをクリア
        h, w = frame.shape[:2]
        # 検出結果をペイロードに変換して共有変数へセット
        if stick1 is not None:
            x1, y1 = stick1['p1']
            x2, y2 = stick1['p2']
            # フレーム座標から元のネイティブ解像度へスケーリング
            cur_w = w
            cur_h = h
            scale_x = (ORIG_CAP_W / cur_w) if (ORIG_CAP_W and cur_w) else 1.0
            scale_y = (ORIG_CAP_H / cur_h) if (ORIG_CAP_H and cur_h) else 1.0
            sx1 = int(round(x1 * scale_x))
            sx2 = int(round(x2 * scale_x))
            sy1 = int(round(y1 * scale_y))
            sy2 = int(round(y2 * scale_y))
            # 反転は送信座標系（元解像度）で行う
            if FLIP_H:
                sx1 = (ORIG_CAP_W - sx1) if ORIG_CAP_W else -sx1
                sx2 = (ORIG_CAP_W - sx2) if ORIG_CAP_W else -sx2
            if FLIP_V:
                sy1 = (ORIG_CAP_H - sy1) if ORIG_CAP_H else -sy1
                sy2 = (ORIG_CAP_H - sy2) if ORIG_CAP_H else -sy2
            payload = f"{sx1},{sy1},{sx2},{sy2}".encode('ascii')
            with _payload_lock:
                last_payload_stick1 = payload
            if LOG_DETECTIONS:
                print(f"Updated payload stick1: {payload}")
            # 即時送信(差分のみ)で遅延を減らす
            if IMMEDIATE_SEND_ON_CHANGE:
                with _send_lock:
                    if payload != last_sent_payload_stick1:
                        try:
                            sock.sendto(payload, (UDP_IP, UDP_PORT))
                            last_sent_payload_stick1 = payload
                            if LOG_DETECTIONS:
                                print(f"IMMEDIATE SENT -> {UDP_IP}:{UDP_PORT} {payload}")
                        except Exception:
                            if LOG_DETECTIONS:
                                import traceback
                                print("Immediate send exception:\n", traceback.format_exc())
        else:
            if not HOLD_LAST_VALUE_WHEN_MISSING:
                with _payload_lock:
                    last_payload_stick1 = None

        if stick2 is not None:
            x1, y1 = stick2['p1']
            x2, y2 = stick2['p2']
            # スケールして元解像度へマッピング
            cur_w = w
            cur_h = h
            scale_x = (ORIG_CAP_W / cur_w) if (ORIG_CAP_W and cur_w) else 1.0
            scale_y = (ORIG_CAP_H / cur_h) if (ORIG_CAP_H and cur_h) else 1.0
            sx1 = int(round(x1 * scale_x))
            sx2 = int(round(x2 * scale_x))
            sy1 = int(round(y1 * scale_y))
            sy2 = int(round(y2 * scale_y))
            if FLIP_H:
                sx1 = (ORIG_CAP_W - sx1) if ORIG_CAP_W else -sx1
                sx2 = (ORIG_CAP_W - sx2) if ORIG_CAP_W else -sx2
            if FLIP_V:
                sy1 = (ORIG_CAP_H - sy1) if ORIG_CAP_H else -sy1
                sy2 = (ORIG_CAP_H - sy2) if ORIG_CAP_H else -sy2
            payload = f"{sx1},{sy1},{sx2},{sy2}".encode('ascii')
            with _payload_lock:
                last_payload_stick2 = payload
            if LOG_DETECTIONS:
                print(f"Updated payload stick2: {payload}")
            if IMMEDIATE_SEND_ON_CHANGE and SEND_STICK2:
                with _send_lock:
                    if payload != last_sent_payload_stick2:
                        try:
                            sock.sendto(payload, (UDP_IP, UDP_PORT_STICK2))
                            last_sent_payload_stick2 = payload
                            if LOG_DETECTIONS:
                                print(f"IMMEDIATE SENT -> {UDP_IP}:{UDP_PORT_STICK2} {payload}")
                        except Exception:
                            if LOG_DETECTIONS:
                                import traceback
                                print("Immediate send exception (stick2):\n", traceback.format_exc())
        else:
            if not HOLD_LAST_VALUE_WHEN_MISSING:
                with _payload_lock:
                    last_payload_stick2 = None

        # キー処理: 常にキー入力を監視して UI トグルや終了を受け付ける
        # (ウィンドウがなくても一応ポーリングしておく)
        key = cv2.waitKey(1) & 0xFF
        if key == ord('q'):
            break
        # 'u' で UI (トラックバー + 表示ウィンドウ) を表示/非表示
        if key == ord('u'):
            if not SHOW_UI:
                enable_ui()
            else:
                disable_ui()

        # UI 表示が有効ならウィンドウ更新を行う
        if SHOW_UI:
            try:
                cv2.imshow("LED Tracking", frame)
                cv2.imshow(TRACKBAR_WINDOW, np.zeros((1, 520, 3), dtype=np.uint8))
            except Exception:
                # 表示に失敗したら UI を切る
                disable_ui()
except KeyboardInterrupt:
    pass
finally:
    # スレッド停止とリソース解放
    _running = False
    sender.stop()
    grabber.join(timeout=1.0)
    sender.join(timeout=1.0)
    cap.release()
    if SHOW_UI or SHOW_DETECTED:
        cv2.destroyAllWindows()
