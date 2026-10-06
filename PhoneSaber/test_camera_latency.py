import ast
import importlib
import json
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest import mock

import cv2
import numpy as np

import camera


class FakeCapture:
    def __init__(self, frames):
        self.frames = iter(frames)
        self.released = False

    def read(self):
        try:
            return True, next(self.frames)
        except StopIteration:
            time.sleep(0.001)
            return False, None

    def release(self):
        self.released = True

    def get(self, _property):
        return 640

    def set(self, _property, _value):
        return True


class FakeNativeCapture:
    def __init__(self, frames=()):
        self.frames = iter(enumerate(frames, 1))
        self.info = {
            "active_format": {"width": 640, "height": 480},
            "requested": {"width": 640, "height": 480},
        }
        self.started = False
        self.stopped = False

    def start(self):
        self.started = True

    def get(self, after_sequence=-1):
        try:
            sequence, frame = next(self.frames)
        except StopIteration:
            return None, 0.0, after_sequence
        if sequence <= after_sequence:
            return None, 0.0, after_sequence
        return frame, float(sequence), sequence

    def stop(self):
        self.stopped = True


class FakeSocket:
    def __init__(self):
        self.sent = []

    def sendto(self, payload, address):
        self.sent.append((payload, address))


class BlockingSocket(FakeSocket):
    def __init__(self):
        super().__init__()
        self.started = threading.Event()
        self.release_send = threading.Event()

    def sendto(self, payload, address):
        self.started.set()
        self.release_send.wait(timeout=1)
        super().sendto(payload, address)


class MainLoopCapture(FakeCapture):
    def __init__(self, frame):
        super().__init__([frame])
        self.read_failures = 0

    def read(self):
        ret, frame = super().read()
        if ret:
            time.sleep(0.01)
        if not ret:
            self.read_failures += 1
        return ret, frame


class TwoFrameCapture(FakeCapture):
    def __init__(self, frames):
        super().__init__(frames)
        self.second_allowed = threading.Event()
        self.read_count = 0

    def read(self):
        if self.read_count == 1:
            self.second_allowed.wait(timeout=1)
        ret, frame = super().read()
        if ret:
            self.read_count += 1
        return ret, frame


class DeterministicGrabber:
    """A main-loop-only grabber; frame delivery is driven by the test."""

    def __init__(self, capture):
        self.capture = capture
        self.stopped = False

    def start(self):
        pass

    def stop(self):
        self.stopped = True

    def join(self, timeout=None):
        pass


class DeterministicSender(DeterministicGrabber):
    def __init__(self, *_args, **_kwargs):
        super().__init__(None)


class RecordingReader(DeterministicGrabber):
    def __init__(self, capture, events):
        super().__init__(capture)
        self.events = events
        self.error = None

    def start(self):
        self.events.append("reader-start")

    def stop(self):
        self.events.append("reader-stop")
        super().stop()

    def join(self, timeout=None):
        self.events.append("reader-join")


class ControlledFrames:
    def __init__(self, frames):
        self.frames = iter(frames)
        self.generation = 0

    def __call__(self, _last_generation, timeout=None):
        try:
            self.generation += 1
            return next(self.frames), self.generation
        except StopIteration:
            return None, self.generation


class CameraLatencyTests(unittest.TestCase):
    def setUp(self):
        camera._running = True
        camera._latest_frame = None
        camera._latest_frame_generation = 0
        camera.last_payload_stick1 = None
        camera.last_payload_stick2 = None
        camera.last_sent_payload_stick1 = None
        camera.last_sent_payload_stick2 = None
        camera.SEND_STICK2 = True
        camera.IMMEDIATE_SEND_ON_CHANGE = True
        camera.HOLD_LAST_VALUE_WHEN_MISSING = True

    def test_grabber_notifies_latest_only_and_skips_duplicate_generation(self):
        first = np.full((4, 4, 3), 1, np.uint8)
        second = np.full((4, 4, 3), 2, np.uint8)
        third = np.full((4, 4, 3), 3, np.uint8)
        grabber = camera.FrameGrabber(FakeCapture([first]))
        grabber.start()
        frame, generation = camera.wait_for_new_frame(0, timeout=1)
        self.assertIsNotNone(frame)
        with camera._frame_condition:
            camera._latest_frame = second
            camera._latest_frame_generation += 1
            camera._latest_frame = third
            camera._latest_frame_generation += 1
            camera._frame_condition.notify_all()
        latest, latest_generation = camera.wait_for_new_frame(generation, timeout=1)
        self.assertEqual(latest_generation, generation + 2)
        self.assertEqual(int(latest[0, 0, 0]), 3)
        duplicate, duplicate_generation = camera.wait_for_new_frame(latest_generation, timeout=0.01)
        self.assertIsNone(duplicate)
        self.assertEqual(duplicate_generation, latest_generation)
        grabber.stop()
        grabber.join(timeout=1)
        self.assertFalse(grabber.is_alive())

    def test_native_grabber_keeps_only_the_newest_sequence(self):
        first = np.full((4, 4, 3), 1, np.uint8)
        second = np.full((4, 4, 3), 2, np.uint8)
        capture = FakeNativeCapture([first, second])
        grabber = camera.NativeFrameGrabber(capture)
        grabber.start()
        frame, generation = camera.wait_for_new_frame(0, timeout=1)
        self.assertIsNotNone(frame)
        self.assertEqual(int(frame[0, 0, 0]), 2)
        self.assertEqual(grabber.sequence, 2)
        self.assertGreaterEqual(generation, 1)
        grabber.stop()
        grabber.join(timeout=1)
        self.assertFalse(grabber.is_alive())

    def test_native_backend_selection_uses_active_resolution_and_join_before_stop(self):
        frame = np.zeros((240, 320, 3), np.uint8)
        stick = {"p1": (10, 20), "p2": (10, 200)}
        native = FakeNativeCapture()
        events = []
        reader = RecordingReader(native, events)
        controlled = ControlledFrames([frame])
        camera.SHOW_UI = False
        camera.SHOW_DETECTED = False
        camera.FLIP_H = camera.FLIP_V = 0
        with mock.patch.dict("os.environ", {"CAMERA_CAPTURE_BACKEND": "native"}), \
                mock.patch.object(camera, "open_native_camera", return_value=native) as open_native, \
                mock.patch.object(camera, "open_camera") as open_opencv, \
                mock.patch.object(camera, "NativeFrameGrabber", return_value=reader), \
                mock.patch.object(camera, "Sender", DeterministicSender), \
                mock.patch.object(camera, "wait_for_new_frame", side_effect=controlled), \
                mock.patch.object(camera, "detect_frame", return_value=(stick, None, (None, None))), \
                mock.patch.object(camera, "load_threshold_settings"), \
                mock.patch.object(camera, "display_detection"), \
                mock.patch.object(camera.cv2, "namedWindow"), \
                mock.patch.object(camera.cv2, "destroyAllWindows"), \
                mock.patch.object(camera.cv2, "imshow"):
            udp = FakeSocket()
            camera.main(udp_socket=udp, key_reader=lambda: ord("q"))
        open_native.assert_called_once_with()
        open_opencv.assert_not_called()
        self.assertTrue(native.started)
        self.assertTrue(native.stopped)
        self.assertEqual(events, ["reader-start", "reader-stop", "reader-join"])
        self.assertEqual(camera.ORIG_CAP_W, 640)
        self.assertEqual(camera.ORIG_CAP_H, 480)
        self.assertEqual(udp.sent[0][0], b"20,40,20,400")

    def test_opencv_remains_default_and_releases_after_reader_join(self):
        frame = np.zeros((240, 320, 3), np.uint8)
        capture = FakeCapture([])
        controlled = ControlledFrames([frame])
        camera.SHOW_UI = False
        camera.SHOW_DETECTED = False
        with mock.patch.dict("os.environ", {}, clear=True), \
                mock.patch.object(camera, "open_camera", return_value=capture) as open_opencv, \
                mock.patch.object(camera, "open_native_camera") as open_native, \
                mock.patch.object(camera, "FrameGrabber", DeterministicGrabber), \
                mock.patch.object(camera, "Sender", DeterministicSender), \
                mock.patch.object(camera, "wait_for_new_frame", side_effect=controlled), \
                mock.patch.object(camera, "detect_frame", return_value=(None, None, (None, None))), \
                mock.patch.object(camera, "load_threshold_settings"), \
                mock.patch.object(camera, "display_detection"), \
                mock.patch.object(camera.cv2, "namedWindow"), \
                mock.patch.object(camera.cv2, "destroyAllWindows"), \
                mock.patch.object(camera.cv2, "imshow"):
            camera.main(udp_socket=FakeSocket(), key_reader=lambda: ord("q"))
        open_opencv.assert_called_once_with()
        open_native.assert_not_called()
        self.assertTrue(capture.released)

    def test_native_timeout_stops_without_processing_stale_frame(self):
        native = FakeNativeCapture()
        reader = RecordingReader(native, [])
        camera.SHOW_UI = False
        camera.SHOW_DETECTED = False
        with mock.patch.object(camera, "NativeFrameGrabber", return_value=reader), \
                mock.patch.object(camera, "Sender", DeterministicSender), \
                mock.patch.object(camera, "wait_for_new_frame", return_value=(None, 0)), \
                mock.patch.object(camera, "detect_frame") as detect, \
                mock.patch.object(camera, "load_threshold_settings"), \
                mock.patch.object(camera.cv2, "namedWindow"), \
                mock.patch.object(camera.cv2, "destroyAllWindows"), \
                mock.patch.object(camera.cv2, "imshow"):
            with self.assertRaisesRegex(RuntimeError, "No fresh native camera frames"):
                camera.main(capture=native, capture_backend="native", udp_socket=FakeSocket(),
                            key_reader=lambda: -1, frame_timeout=0.001)
        detect.assert_not_called()
        self.assertTrue(native.stopped)

    def test_wait_stop_and_common_keys(self):
        result = []

        def wait():
            result.append(camera.wait_for_new_frame(0, timeout=2))

        waiter = threading.Thread(target=wait)
        waiter.start()
        time.sleep(0.01)
        camera._running = False
        with camera._frame_condition:
            camera._frame_condition.notify_all()
        waiter.join(timeout=1)
        self.assertFalse(waiter.is_alive())
        self.assertEqual(result, [(None, 0)])

        camera.SHOW_UI = False
        with mock.patch.object(camera, "enable_ui") as enable:
            self.assertTrue(camera.handle_key(ord("u")))
            enable.assert_called_once_with()
        self.assertFalse(camera.handle_key(ord("q")))
        self.assertFalse(camera._running)

    def test_both_colors_then_missing_clears_shared_values(self):
        frame = np.zeros((240, 320, 3), np.uint8)
        cv2.line(frame, (40, 210), (40, 30), (0, 0, 255), 16)
        cv2.line(frame, (240, 210), (240, 30), (255, 0, 0), 16)
        stick1, stick2, _ = camera.detect_frame(frame)
        self.assertIsNotNone(stick1)
        self.assertIsNotNone(stick2)
        sock = FakeSocket()
        camera.publish_payload(camera.payload_for_stick(stick1, frame.shape), 1, sock)
        camera.publish_payload(camera.payload_for_stick(stick2, frame.shape), 2, sock)
        camera.HOLD_LAST_VALUE_WHEN_MISSING = False
        camera.clear_payload(1)
        camera.clear_payload(2)
        self.assertIsNone(camera.last_payload_stick1)
        self.assertIsNone(camera.last_payload_stick2)

    def test_periodic_sender_cannot_reorder_clear_with_old_snapshot(self):
        fake_socket = BlockingSocket()
        camera.IMMEDIATE_SEND_ON_CHANGE = False
        camera.last_payload_stick1 = b"old"
        sender = camera.Sender(fake_socket, hz=1000)
        sender.start()
        self.assertTrue(fake_socket.started.wait(timeout=1))
        clear_done = threading.Event()

        def clear():
            camera.clear_payload(1)
            clear_done.set()

        clearer = threading.Thread(target=clear)
        clearer.start()
        time.sleep(0.01)
        self.assertFalse(clear_done.is_set())
        fake_socket.release_send.set()
        clearer.join(timeout=1)
        sender.stop()
        sender.join(timeout=1)
        self.assertTrue(clear_done.is_set())
        self.assertIsNone(camera.last_payload_stick1)
        sent_count = len(fake_socket.sent)
        time.sleep(0.005)
        self.assertEqual(len(fake_socket.sent), sent_count)

    def test_new_immediate_value_wins_against_blocked_periodic_old_value(self):
        fake_socket = BlockingSocket()
        camera.IMMEDIATE_SEND_ON_CHANGE = True
        camera.last_payload_stick1 = b"old"
        sender = camera.Sender(fake_socket, hz=1000)
        sender.start()
        self.assertTrue(fake_socket.started.wait(timeout=1))

        publish_done = threading.Event()

        def publish_new():
            camera.publish_payload(b"new", 1, fake_socket)
            publish_done.set()

        publisher = threading.Thread(target=publish_new)
        publisher.start()
        time.sleep(0.01)
        self.assertFalse(publish_done.is_set())
        fake_socket.release_send.set()
        publisher.join(timeout=1)
        sender.stop()
        sender.join(timeout=1)
        self.assertTrue(publish_done.is_set())

        payloads = [payload for payload, _address in fake_socket.sent]
        self.assertIn(b"old", payloads)
        self.assertIn(b"new", payloads)
        new_index = payloads.index(b"new")
        self.assertNotIn(b"old", payloads[new_index + 1:])
        self.assertEqual(camera.last_payload_stick1, b"new")
        self.assertEqual(camera.last_sent_payload_stick1, b"new")

    def test_detection_converts_once_and_payload_matches_scale_and_flip(self):
        frame = np.zeros((240, 320, 3), np.uint8)
        cv2.line(frame, (40, 210), (40, 30), (0, 0, 255), 16)
        cv2.line(frame, (240, 210), (240, 30), (255, 0, 0), 16)
        with mock.patch.object(camera.cv2, "cvtColor", wraps=cv2.cvtColor) as convert:
            stick1, stick2, _ = camera.detect_frame(frame)
        self.assertEqual(convert.call_count, 1)
        camera.ORIG_CAP_W, camera.ORIG_CAP_H = 640, 480
        camera.FLIP_H, camera.FLIP_V = 1, 1
        payload = camera.payload_for_stick(stick1, frame.shape)
        # 変更前 camera.py と同じ合成フレームに対する固定期待値。
        self.assertEqual(payload, b"562,44,560,436")
        self.assertEqual(payload.count(b","), 3)
        self.assertIsNotNone(stick2)

        camera.FLIP_H = camera.FLIP_V = 0
        self.assertEqual(camera.payload_for_stick(stick1, frame.shape), b"78,436,80,44")
        self.assertEqual(camera.payload_for_stick(stick2, frame.shape), b"480,436,480,44")

    def test_both_color_endpoints_udp_ports_and_send_stick2(self):
        frame = np.zeros((240, 320, 3), np.uint8)
        cv2.line(frame, (40, 210), (40, 30), (0, 0, 255), 16)
        cv2.line(frame, (240, 210), (240, 30), (255, 0, 0), 16)
        stick1, stick2, _ = camera.detect_frame(frame)
        camera.ORIG_CAP_W, camera.ORIG_CAP_H = 640, 480
        camera.FLIP_H = camera.FLIP_V = 1
        fake_socket = FakeSocket()
        camera.publish_payload(camera.payload_for_stick(stick1, frame.shape), 1, fake_socket)
        camera.publish_payload(camera.payload_for_stick(stick2, frame.shape), 2, fake_socket)
        self.assertEqual(fake_socket.sent, [
            (b"562,44,560,436", (camera.UDP_IP, 5005)),
            (b"160,44,160,436", (camera.UDP_IP, 5006)),
        ])
        fake_socket.sent.clear()
        camera.SEND_STICK2 = False
        camera.publish_payload(b"new-stick2", 2, fake_socket)
        self.assertEqual(fake_socket.sent, [])

    def test_missing_hold_setting_and_main_loop_detects_once_then_exits_on_q(self):
        frame = np.zeros((240, 320, 3), np.uint8)
        detections = []
        camera.SHOW_UI = False
        camera.SHOW_DETECTED = False
        camera.HOLD_LAST_VALUE_WHEN_MISSING = True
        controlled = ControlledFrames([frame])

        def detect(value):
            detections.append(value)
            return None, None, (None, None)

        with mock.patch.object(camera, "open_camera", return_value=FakeCapture([])) as open_camera, \
                mock.patch.object(camera.socket, "socket", return_value=FakeSocket()), \
                mock.patch.object(camera, "FrameGrabber", DeterministicGrabber), \
                mock.patch.object(camera, "Sender", DeterministicSender), \
                mock.patch.object(camera, "wait_for_new_frame", side_effect=controlled), \
                mock.patch.object(camera, "detect_frame", side_effect=detect), \
                mock.patch.object(camera, "load_threshold_settings"), \
                mock.patch.object(camera, "display_detection"), \
                mock.patch.object(camera.cv2, "namedWindow"), \
                mock.patch.object(camera.cv2, "destroyAllWindows"), \
                mock.patch.object(camera.cv2, "imshow"), \
                mock.patch.object(camera, "enable_ui") as enable_ui:
            camera.main(key_reader=lambda: ord("q"))
        open_camera.assert_called_once_with()
        self.assertEqual(len(detections), 1)
        enable_ui.assert_not_called()

        camera.last_payload_stick1 = b"held"
        camera.last_payload_stick2 = b"held2"
        camera.HOLD_LAST_VALUE_WHEN_MISSING = True
        camera.clear_payload(1) if not camera.HOLD_LAST_VALUE_WHEN_MISSING else None
        self.assertEqual(camera.last_payload_stick1, b"held")
        camera.HOLD_LAST_VALUE_WHEN_MISSING = False
        camera.clear_payload(1)
        camera.clear_payload(2)
        self.assertIsNone(camera.last_payload_stick1)
        self.assertIsNone(camera.last_payload_stick2)

    def test_main_sends_both_colors_before_display(self):
        frame = np.zeros((240, 320, 3), np.uint8)
        stick1 = {"p1": (10, 20), "p2": (10, 200)}
        stick2 = {"p1": (30, 20), "p2": (30, 200)}
        events = []

        class RecordingSocket(FakeSocket):
            def sendto(self, payload, address):
                events.append(("sendto", payload, address))
                super().sendto(payload, address)

        fake_socket = RecordingSocket()
        original_publish = camera.publish_payload

        def record_publish(payload, stick_number, udp_socket):
            return original_publish(payload, stick_number, udp_socket)

        camera.SHOW_UI = False
        camera.SHOW_DETECTED = True
        controlled = ControlledFrames([frame])

        def display(*_args):
            events.append(("display",))

        with mock.patch.object(camera, "open_camera", return_value=FakeCapture([])), \
                mock.patch.object(camera.socket, "socket", return_value=fake_socket), \
                mock.patch.object(camera, "FrameGrabber", DeterministicGrabber), \
                mock.patch.object(camera, "Sender", DeterministicSender), \
                mock.patch.object(camera, "wait_for_new_frame", side_effect=controlled), \
                mock.patch.object(camera, "detect_frame", return_value=(stick1, stick2, (None, None))), \
                mock.patch.object(camera, "publish_payload", side_effect=record_publish), \
                mock.patch.object(camera, "display_detection", side_effect=display), \
                mock.patch.object(camera, "load_threshold_settings"), \
                mock.patch.object(camera.cv2, "namedWindow"), \
                mock.patch.object(camera.cv2, "destroyAllWindows"), \
                mock.patch.object(camera.cv2, "imshow"), \
                mock.patch.object(camera, "build_stick_candidate_mask"):
            camera.main(key_reader=lambda: ord("q"))
        self.assertEqual([event[0] for event in events], ["sendto", "sendto", "display"])
        self.assertEqual([event[2][1] for event in events[:2]], [camera.UDP_PORT, camera.UDP_PORT_STICK2])

    def test_main_hold_setting_applies_when_detection_disappears(self):
        frame = np.zeros((240, 320, 3), np.uint8)
        sticks = ({"p1": (10, 20), "p2": (10, 200)}, {"p1": (30, 20), "p2": (30, 200)})

        camera.ORIG_CAP_W, camera.ORIG_CAP_H = 320, 240
        camera.FLIP_H = camera.FLIP_V = 0

        for hold, expected in ((True, (b"20,53,20,533", b"60,53,60,533")),
                               (False, (None, None))):
            camera._latest_frame = None
            camera._latest_frame_generation = 0
            camera.last_payload_stick1 = None
            camera.last_payload_stick2 = None
            fake_socket = FakeSocket()
            detections = iter([(sticks[0], sticks[1], (None, None)),
                               (None, None, (None, None))])
            detection_calls = []

            def detect(_frame):
                detection_calls.append(True)
                return next(detections)

            controlled = ControlledFrames([frame, frame.copy()])

            camera.SHOW_UI = False
            camera.SHOW_DETECTED = False
            camera.HOLD_LAST_VALUE_WHEN_MISSING = hold
            with mock.patch.object(camera, "open_camera", return_value=FakeCapture([])), \
                    mock.patch.object(camera.socket, "socket", return_value=fake_socket), \
                    mock.patch.object(camera, "FrameGrabber", DeterministicGrabber), \
                    mock.patch.object(camera, "Sender", DeterministicSender), \
                    mock.patch.object(camera, "wait_for_new_frame", side_effect=controlled), \
                    mock.patch.object(camera, "detect_frame", side_effect=detect), \
                    mock.patch.object(camera, "load_threshold_settings"), \
                    mock.patch.object(camera, "display_detection", side_effect=lambda *_: None), \
                    mock.patch.object(camera.cv2, "namedWindow"), \
                    mock.patch.object(camera.cv2, "destroyAllWindows"), \
                    mock.patch.object(camera.cv2, "imshow"):
                keys = iter([-1, ord("q")])
                camera.main(key_reader=lambda: next(keys))
            self.assertEqual(len(detection_calls), 2)
            self.assertEqual((camera.last_payload_stick1, camera.last_payload_stick2), expected)

    def test_disabled_display_skips_processing(self):
        with mock.patch.object(camera, "build_stick_candidate_mask") as build_mask:
            with mock.patch.object(camera.cv2, "imshow"):
                camera.SHOW_UI = False
                camera.SHOW_DETECTED = False
                camera.display_detection((np.zeros((2, 2), np.uint8),) * 2, (None, None))
        build_mask.assert_not_called()

    def test_local_processing_benchmark_is_reproducible_against_pinned_head(self):
        import camera_latency_benchmark as benchmark

        frames = benchmark.synthetic_frames()
        project = Path(__file__).parent
        working = benchmark.load_working_module(project)
        baseline = benchmark.load_baseline_module(project)
        working.ORIG_CAP_W = baseline["ORIG_CAP_W"] = 640
        working.ORIG_CAP_H = baseline["ORIG_CAP_H"] = 480
        working.FLIP_H = baseline["FLIP_H"] = 1
        working.FLIP_V = baseline["FLIP_V"] = 1
        current = benchmark.make_path("working", working, frames)
        pinned = benchmark.make_path("baseline", baseline, frames)
        self.assertEqual(pinned["count"], 60)
        self.assertEqual(current["count"], 60)
        self.assertEqual(pinned["coordinates"], current["coordinates"])

    def test_import_has_no_reexec_or_runtime_side_effect(self):
        with mock.patch.object(camera.os, "execv") as execv, \
                mock.patch("python_runtime.reexec_with_cv2") as reexec:
            importlib.reload(camera)
            execv.assert_not_called()
            reexec.assert_not_called()
        self.assertIsNone(camera.cap)
        self.assertIsNone(camera.sock)

    def test_script_startup_calls_runtime_selection_before_cv2_import(self):
        source = Path(camera.__file__).read_text(encoding="utf-8")
        tree = ast.parse(source)
        cv2_import = next(node for node in ast.walk(tree)
                          if isinstance(node, ast.Import) and any(alias.name == "cv2" for alias in node.names))
        guarded_call = next(node for node in ast.walk(tree)
                            if isinstance(node, ast.Call)
                            and isinstance(node.func, ast.Name)
                            and node.func.id == "reexec_with_cv2")
        self.assertLess(guarded_call.lineno, cv2_import.lineno)
        main_guard = next(node for node in tree.body
                          if isinstance(node, ast.If)
                          and ast.unparse(node.test) == "__name__ == '__main__'")
        self.assertTrue(any(isinstance(node, ast.Expr) and isinstance(node.value, ast.Call)
                            and isinstance(node.value.func, ast.Name)
                            and node.value.func.id == "reexec_with_cv2"
                            for node in main_guard.body))
        # cv2 がない通常起動の前置きだけを実行し、環境選択が import より先に呼ばれることを確認。
        prelude = source[:source.index("import cv2") + len("import cv2")]
        reexec = mock.Mock()
        real_import = __import__

        def block_cv2(name, *args, **kwargs):
            if name == "cv2":
                raise ModuleNotFoundError("cv2 intentionally blocked")
            return real_import(name, *args, **kwargs)

        with mock.patch.dict(sys.modules, {"python_runtime": SimpleNamespace(reexec_with_cv2=reexec)}), \
                mock.patch("builtins.__import__", side_effect=block_cv2):
            with self.assertRaises(ModuleNotFoundError):
                exec(compile(prelude, str(camera.__file__), "exec"), {"__name__": "__main__"})
        reexec.assert_called_once_with()


if __name__ == "__main__":
    unittest.main()
