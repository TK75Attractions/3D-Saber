"""Direct AVFoundation, no encoder/pipe/receiver queue between camera and Python."""

import ctypes as ct
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import time

import cv2
import numpy as np

ROOT = Path(__file__).resolve().parent
SOURCE = ROOT / "native" / "ContinuityCapture.m"


def load_library():
    if sys.platform != "darwin":
        raise RuntimeError("Native capture requires macOS; use --capture-backend opencv.")
    build = ROOT / ".native-camera"
    build.mkdir(exist_ok=True)
    digest = hashlib.sha256(SOURCE.read_bytes()).hexdigest()[:16]
    library = build / f"capture-{digest}.dylib"
    if not library.exists():
        temporary = build / f"capture-{digest}-{os.getpid()}.dylib"
        print("Building native camera adapter (first run only)...", flush=True)
        try:
            subprocess.run([
                "xcrun", "clang", "-O2", "-dynamiclib", "-fobjc-arc", "-mmacosx-version-min=13.0",
                "-framework", "Foundation", "-framework", "AVFoundation",
                "-framework", "CoreMedia", "-framework", "CoreVideo",
                str(SOURCE), "-o", str(temporary),
            ], check=True, timeout=180)
            temporary.replace(library)
        finally:
            temporary.unlink(missing_ok=True)
    lib = ct.CDLL(str(library))
    lib.saber_devices.argtypes, lib.saber_devices.restype = [], ct.c_void_p
    lib.saber_free.argtypes, lib.saber_free.restype = [ct.c_void_p], None
    lib.saber_clock.argtypes, lib.saber_clock.restype = [], ct.c_double
    lib.saber_open.argtypes = [ct.c_char_p, ct.c_int32, ct.c_int32, ct.c_double,
                              ct.c_int32, ct.POINTER(ct.c_void_p)]
    lib.saber_open.restype = ct.c_void_p
    lib.saber_info.argtypes, lib.saber_info.restype = [ct.c_void_p], ct.c_void_p
    lib.saber_copy.argtypes = [ct.c_void_p, ct.c_int64, ct.c_void_p, ct.c_ssize_t,
                              ct.POINTER(ct.c_double)]
    lib.saber_copy.restype = ct.c_int32
    lib.saber_close.argtypes, lib.saber_close.restype = [ct.c_void_p], None
    return lib


def take_string(lib, pointer):
    if not pointer:
        raise RuntimeError("Native camera returned no message")
    try:
        return ct.string_at(pointer).decode("utf-8")
    finally:
        lib.saber_free(pointer)


def list_devices():
    lib = load_library()
    return json.loads(take_string(lib, lib.saber_devices()))


class NativeLatestFrame:
    """Single consumer. Frames returned by get() own their BGR storage."""

    def __init__(self, device_id="continuity", width=640, height=360, fps=30,
                 format_index=-1):
        self.lib = load_library()
        self.handle = None
        before = time.perf_counter()
        native_now = self.lib.saber_clock()
        after = time.perf_counter()
        self.clock_offset = (before + after) / 2 - native_now
        error = ct.c_void_p()
        self.handle = self.lib.saber_open(device_id.encode(), width, height, fps,
                                           format_index, ct.byref(error))
        if not self.handle:
            raise RuntimeError(take_string(self.lib, error.value))
        try:
            self.info = json.loads(take_string(self.lib, self.lib.saber_info(self.handle)))
            self.buffer = np.empty(max(1, width * height * 4), dtype=np.uint8)
            self.metadata = (ct.c_double * 6)()
            self.pts_age_ms = float("nan")
            self.frame_interval_s = float("nan")
            self.last_frame_shape = None
        except BaseException:
            self.stop()
            raise

    def start(self):
        pass  # AVFoundation already owns the capture queue.

    def get(self, after_sequence=-1):
        if not self.handle:
            return None, 0.0, after_sequence
        for _ in range(3):
            status = self.lib.saber_copy(self.handle, after_sequence,
                self.buffer.ctypes.data, self.buffer.nbytes, self.metadata)
            if status == 0:
                return None, 0.0, after_sequence
            width, height, sequence = map(int, self.metadata[:3])
            # The native mailbox reports the dimensions of this actual
            # CVPixelBuffer callback output, not the wireless source format.
            if status == -1:
                if not (0 < width <= 8192 and 0 < height <= 8192):
                    raise RuntimeError(f"Invalid native frame size: {width}x{height}")
                self.buffer = np.empty(width * height * 4, dtype=np.uint8)
                continue
            if status != 1:
                raise RuntimeError(f"Native camera pixel copy failed: {status}")
            bgra = self.buffer[:width * height * 4].reshape(height, width, 4)
            frame = cv2.cvtColor(bgra, cv2.COLOR_BGRA2BGR)
            self.last_frame_shape = frame.shape
            self.pts_age_ms = self.metadata[4] * 1000
            self.frame_interval_s = self.metadata[5]
            return frame, self.metadata[3] + self.clock_offset, sequence
        raise RuntimeError("Camera dimensions changed repeatedly during a frame read")

    def stop(self):
        if self.handle:
            handle, self.handle = self.handle, None
            self.lib.saber_close(handle)

    def join(self, timeout=None):
        pass

    release = stop

    def __enter__(self):
        return self

    def __exit__(self, *exc):
        self.stop()
