import json
import sys
import tempfile
import unittest
from io import StringIO
from pathlib import Path
from unittest import mock

import cv2
import numpy as np

import camera_benchmark
from camera_benchmark import (DICTIONARY, PREVIEW_MAX_HEIGHT, PREVIEW_MAX_WIDTH,
                              FPS_TOLERANCE, OpticalSamples, WARMUP_MARKER_ID,
                              fps_matches_request, marker_target, preview_frame)


class FakeClock:
    def __init__(self, step=.1):
        self.value = 0
        self.step = step

    def __call__(self):
        self.value += self.step
        return self.value


class FakeLatest:
    def __init__(self, frames, events=None):
        self.frames = list(frames)
        self.events = events if events is not None else []
        self.get_calls = 0

    def start(self):
        self.events.append("start")

    def get(self, last_sequence):
        self.get_calls += 1
        self.events.append("get")
        if self.frames:
            item = self.frames.pop(0)
            if item is None:
                return None, 0, last_sequence
            frame, available, sequence = item
            return frame, available, sequence
        return None, 0, last_sequence


class FakeCapture:
    def get(self, _property):
        return 30


class OpticalMeasurementTests(unittest.TestCase):
    def test_measurement_invalidates_actual_size_and_fps_mismatch(self):
        frames = [
            (np.zeros((480, 1920, 3), np.uint8), 0.0, 1),
            (np.zeros((480, 1920, 3), np.uint8), 1.0, 2),
        ]
        latest = FakeLatest(frames)
        clock = FakeClock()
        with mock.patch.object(camera_benchmark, "open_camera", return_value=FakeCapture()), \
                mock.patch.object(camera_benchmark, "LatestFrame", return_value=latest), \
                mock.patch.object(camera_benchmark.time, "perf_counter", side_effect=clock), \
                mock.patch.object(camera_benchmark, "close_frame_source"):
            result = camera_benchmark.measure("opencv", 0, "unused", 640, 480, 60, 1, False)
        self.assertEqual(result["actual_sizes"], [(1920, 480)])
        self.assertFalse(result["valid"])
        self.assertFalse(result["measured"]["valid"])
        self.assertTrue(result["measured"]["warnings"])

    def test_measurement_accepts_small_fps_jitter_but_rejects_outside_tolerance(self):
        frames = [
            (np.zeros((480, 640, 3), np.uint8), 0.0, 1),
            (np.zeros((480, 640, 3), np.uint8), 1 / 29.97, 2),
        ]
        latest = FakeLatest(frames)
        clock = FakeClock()
        with mock.patch.object(camera_benchmark, "open_camera", return_value=FakeCapture()), \
                mock.patch.object(camera_benchmark, "LatestFrame", return_value=latest), \
                mock.patch.object(camera_benchmark.time, "perf_counter", side_effect=clock), \
                mock.patch.object(camera_benchmark, "close_frame_source"):
                result = camera_benchmark.measure("opencv", 0, "unused", 640, 480, 30, 1, False)
        self.assertAlmostEqual(result["input_fps"], 29.97)
        self.assertTrue(result["valid"])
        self.assertTrue(result["measured"]["valid"])

        self.assertTrue(fps_matches_request(30 * (1 - FPS_TOLERANCE), 30))
        self.assertTrue(fps_matches_request(60 * (1 + FPS_TOLERANCE), 60))
        self.assertFalse(fps_matches_request(30 * (1 + FPS_TOLERANCE + 1e-6), 30))
        self.assertFalse(fps_matches_request(45, 30))

    def test_target_round_trip(self):
        detector = cv2.aruco.ArucoDetector(DICTIONARY)
        for marker_id in (0, 1, 234, 998):
            target = marker_target(marker_id, "native test", 0.2)
            _, ids, _ = detector.detectMarkers(target)
            self.assertIsNotNone(ids)
            self.assertEqual(ids.flatten().tolist(), [marker_id])

    def test_samples_reject_unknown_duplicate_negative_stale(self):
        samples = OpticalSamples()
        samples.issued[42] = 10
        self.assertFalse(samples.record(41, 10.1, 10.2))
        self.assertFalse(samples.record(42, 9.9, 10.2))
        self.assertFalse(samples.record(42, 12.1, 12.2))
        self.assertFalse(samples.record(42, 10.1, 10.0))
        self.assertTrue(samples.record(42, 10.1, 10.102))
        self.assertFalse(samples.record(42, 10.2, 10.3))
        summary = samples.summary()
        self.assertEqual(summary["count"], 1)
        self.assertFalse(summary["valid"])
        self.assertAlmostEqual(summary["median_ms"], 102)
        self.assertAlmostEqual(summary["consumer_median_ms"], 2)

    def test_preview_is_labeled_bounded_aspect_correct_and_does_not_mutate_source(self):
        frame = np.full((720, 1280, 3), (1, 2, 3), dtype=np.uint8)
        preview = preview_frame(frame)
        self.assertEqual(preview.shape[:2], (360, 640))
        self.assertEqual(tuple(frame[100, 200]), (1, 2, 3))
        self.assertEqual(tuple(preview[100, 100]), (1, 2, 3))
        self.assertLessEqual(preview.shape[1], PREVIEW_MAX_WIDTH)
        self.assertLessEqual(preview.shape[0], PREVIEW_MAX_HEIGHT)

    def test_preview_keeps_small_frame_aspect_ratio(self):
        frame = np.zeros((240, 320, 3), dtype=np.uint8)
        preview = preview_frame(frame)
        self.assertEqual(preview.shape[:2], (240, 320))

    def test_optical_warmup_polls_preview_and_delayed_frames_do_not_measure(self):
        events = []
        warmup_frame = np.full((100, 200, 3), 9, np.uint8)
        stale_frame = np.full((110, 220, 3), 99, np.uint8)
        measured_frames = [
            (np.full((240, 320, 3), 1, np.uint8), 3.7, 52),
            (np.full((240, 320, 3), 2, np.uint8), 4.1, 54),
        ]
        frames = [(warmup_frame, .5, 50)] + [None] * 14 + [
            (stale_frame, .6, 51), *measured_frames]
        latest = FakeLatest(frames, events)
        clock = FakeClock()
        shown = []
        key_times = []
        target_times = []
        samples = OpticalSamples()

        def detect_marker(frame):
            value = int(frame[0, 0, 0])
            marker_id = {99: WARMUP_MARKER_ID, 1: 0, 2: 1}.get(value)
            if marker_id is None:
                return [], None, None
            corners = [np.array([[[0, 0], [1, 0], [1, 1], [0, 1]]], dtype=np.float32)]
            return corners, np.array([[marker_id]], dtype=np.int32), None

        def record_imshow(title, image):
            shown.append((title, image.copy()))
            if title == camera_benchmark.TARGET_TITLE:
                target_times.append(clock.value)

        def record_wait_key(_delay):
            key_times.append(clock.value)
            return -1

        with mock.patch.object(camera_benchmark, "open_camera", return_value=FakeCapture()), \
                mock.patch.object(camera_benchmark, "LatestFrame", return_value=latest), \
                mock.patch.object(camera_benchmark, "OpticalSamples", return_value=samples), \
                mock.patch.object(camera_benchmark, "processing_frame", side_effect=lambda frame, *_: frame), \
                mock.patch.object(camera_benchmark.time, "perf_counter", side_effect=clock), \
                mock.patch.object(camera_benchmark.cv2.aruco, "ArucoDetector", return_value=mock.Mock(detectMarkers=detect_marker)), \
                mock.patch.object(camera_benchmark.cv2, "namedWindow"), \
                mock.patch.object(camera_benchmark.cv2, "moveWindow"), \
                mock.patch.object(camera_benchmark.cv2, "imshow", side_effect=record_imshow), \
                mock.patch.object(camera_benchmark.cv2, "waitKey", side_effect=record_wait_key), \
                mock.patch.object(camera_benchmark.cv2, "destroyWindow"), \
                mock.patch.object(camera_benchmark, "close_frame_source"):
            result = camera_benchmark.measure("opencv", 0, "unused", 640, 480, 5, 1, True)

        targets = [image for title, image in shown if title == camera_benchmark.TARGET_TITLE]
        previews = [image for title, image in shown if title == camera_benchmark.PREVIEW_TITLE]
        target_detector = cv2.aruco.ArucoDetector(DICTIONARY)
        target_ids = [int(target_detector.detectMarkers(target)[1][0, 0]) for target in targets]
        self.assertEqual(target_ids[:2], [WARMUP_MARKER_ID, 0])
        self.assertGreaterEqual(target_times[1], 3)
        self.assertTrue(any(0 < value < 3 for value in key_times))
        self.assertEqual([int(preview[0, 0, 0]) for preview in previews], [9, 1, 2])
        self.assertEqual(result["frames_consumed"], 2)
        self.assertEqual(result["actual_sizes"], [(320, 240)])
        self.assertAlmostEqual(result["input_fps"], 5)
        self.assertAlmostEqual(result["frame_interval_p90_ms"], 400)
        self.assertGreater(result["since_available_median_ms"], 0)
        self.assertLess(result["since_available_median_ms"], 1000)
        self.assertNotIn(WARMUP_MARKER_ID, samples.issued)
        self.assertFalse(samples.record(WARMUP_MARKER_ID, 4.2, 4.3))
        self.assertEqual([sample["id"] for sample in result["samples"]], [0, 1])
        self.assertEqual(next(iter(samples.issued)), 0)
        self.assertGreater(result["target_interval_p90_ms"], 0)

    def test_optical_warmup_runs_for_three_seconds_before_measurement(self):
        frames = [None] * 15 + [
            (np.zeros((240, 320, 3), np.uint8), 3.7, 1),
            (np.zeros((240, 320, 3), np.uint8), 3.9, 2),
        ]
        latest = FakeLatest(frames)
        clock = FakeClock()
        target_times = []
        with mock.patch.object(camera_benchmark, "open_camera", return_value=FakeCapture()), \
                mock.patch.object(camera_benchmark, "LatestFrame", return_value=latest), \
                mock.patch.object(camera_benchmark.time, "perf_counter", side_effect=clock), \
                mock.patch.object(camera_benchmark.cv2.aruco, "ArucoDetector", return_value=mock.Mock(detectMarkers=lambda _frame: ([], None, None))), \
                mock.patch.object(camera_benchmark.cv2, "namedWindow"), \
                mock.patch.object(camera_benchmark.cv2, "moveWindow"), \
                mock.patch.object(camera_benchmark.cv2, "imshow", side_effect=lambda title, _image: target_times.append(clock.value) if title == camera_benchmark.TARGET_TITLE else None), \
                mock.patch.object(camera_benchmark.cv2, "waitKey", return_value=-1), \
                mock.patch.object(camera_benchmark.cv2, "destroyWindow"), \
                mock.patch.object(camera_benchmark, "close_frame_source"):
            camera_benchmark.measure("opencv", 0, "unused", 640, 480, 5, 1, True)
        self.assertGreaterEqual(len(target_times), 2)
        self.assertEqual(target_times[0], 0)
        self.assertGreaterEqual(target_times[1], 3)
        self.assertGreaterEqual(clock.value, 4.3)

    def test_optical_preview_uses_each_fresh_frame_once_and_after_consumption(self):
        events = []
        frames = [None] * 15 + [
            (np.full((480, 640, 3), value, np.uint8), available, value)
            for value, available in ((1, 3.7), (2, 3.9))]
        latest = FakeLatest(frames, events)
        shown = []
        clock = FakeClock()

        def record_imshow(title, image):
            shown.append((title, image.copy()))
            events.append(f"imshow:{title}")

        with mock.patch.object(camera_benchmark, "open_camera", return_value=FakeCapture()), \
                mock.patch.object(camera_benchmark, "LatestFrame", return_value=latest), \
                mock.patch.object(camera_benchmark, "processing_frame", side_effect=lambda frame, *_: frame), \
                mock.patch.object(camera_benchmark.time, "perf_counter", side_effect=clock), \
                mock.patch.object(camera_benchmark.cv2.aruco, "ArucoDetector", return_value=mock.Mock(detectMarkers=lambda _frame: ([], None, None))), \
                mock.patch.object(camera_benchmark.cv2, "namedWindow"), \
                mock.patch.object(camera_benchmark.cv2, "moveWindow"), \
                mock.patch.object(camera_benchmark.cv2, "imshow", side_effect=record_imshow), \
                mock.patch.object(camera_benchmark.cv2, "waitKey", return_value=-1), \
                mock.patch.object(camera_benchmark.cv2, "destroyWindow"), \
                mock.patch.object(camera_benchmark, "close_frame_source"):
            result = camera_benchmark.measure("opencv", 0, "unused", 640, 480, 30, 1, True)

        previews = [image for title, image in shown if title == camera_benchmark.PREVIEW_TITLE]
        self.assertEqual(len(previews), 2)
        self.assertEqual([int(image[100, 100, 0]) for image in previews], [1, 2])
        self.assertEqual([image.shape[:2] for image in previews], [(480, 640), (480, 640)])
        self.assertEqual(result["frames_consumed"], 2)
        get_index = events.index("get")
        self.assertIn("imshow:Live BGR preview - keep outside iPhone view", events[get_index + 1:])

    def test_measure_records_sample_before_preview_delay_and_excludes_delay(self):
        events = []
        frames = [None] * 15 + [
            (np.full((480, 640, 3), value, np.uint8), available, value - 1)
            for value, available in ((1, 3.65), (2, 3.75))]
        latest = FakeLatest(frames, events)
        clock = FakeClock()
        samples = OpticalSamples()
        original_record = samples.record

        def record_sample(marker_id, available, consumed):
            recorded = original_record(marker_id, available, consumed)
            if recorded:
                events.append("record")
            return recorded

        samples.record = record_sample

        def perf_counter():
            value = clock()
            if events and events[-1] == "get":
                events.append("consumed")
            return value

        def detect_marker(frame):
            marker_id = int(frame[0, 0, 0]) - 1
            corners = [np.array([[[0, 0], [1, 0], [1, 1], [0, 1]]], dtype=np.float32)]
            return corners, np.array([[marker_id]], dtype=np.int32), None

        def record_imshow(title, _image):
            events.append(f"imshow:{title}")
            if title == camera_benchmark.PREVIEW_TITLE:
                clock.value += 5.0

        with mock.patch.object(camera_benchmark, "open_camera", return_value=FakeCapture()), \
                mock.patch.object(camera_benchmark, "LatestFrame", return_value=latest), \
                mock.patch.object(camera_benchmark, "OpticalSamples", return_value=samples), \
                mock.patch.object(camera_benchmark, "processing_frame", side_effect=lambda frame, *_: frame), \
                mock.patch.object(camera_benchmark.time, "perf_counter", side_effect=perf_counter), \
                mock.patch.object(camera_benchmark.cv2.aruco, "ArucoDetector", return_value=mock.Mock(detectMarkers=detect_marker)), \
                mock.patch.object(camera_benchmark.cv2, "namedWindow"), \
                mock.patch.object(camera_benchmark.cv2, "moveWindow"), \
                mock.patch.object(camera_benchmark.cv2, "imshow", side_effect=record_imshow), \
                mock.patch.object(camera_benchmark.cv2, "waitKey", return_value=-1), \
                mock.patch.object(camera_benchmark.cv2, "destroyWindow"), \
                mock.patch.object(camera_benchmark, "close_frame_source"):
            result = camera_benchmark.measure("opencv", 0, "unused", 640, 480, 30, 10, True)

        self.assertEqual([sample["id"] for sample in result["samples"]], [0])
        first_record = events.index("record")
        first_preview = events.index(f"imshow:{camera_benchmark.PREVIEW_TITLE}")
        self.assertEqual(events[first_record - 1], "consumed")
        self.assertLess(first_record, first_preview)
        self.assertAlmostEqual(result["samples"][0]["display_to_consumer_ms"], 100)
        self.assertLess(result["samples"][0]["display_to_consumer_ms"], 5000)

    def test_non_optical_measurement_has_no_highgui_calls(self):
        latest = FakeLatest([(np.zeros((360, 640, 3), np.uint8), .1, 1),
                             (np.zeros((360, 640, 3), np.uint8), .2, 2)])
        clock = FakeClock()
        with mock.patch.object(camera_benchmark, "open_camera", return_value=FakeCapture()), \
                mock.patch.object(camera_benchmark, "LatestFrame", return_value=latest), \
                mock.patch.object(camera_benchmark.time, "perf_counter", side_effect=clock), \
                mock.patch.object(camera_benchmark.cv2, "namedWindow", side_effect=AssertionError), \
                mock.patch.object(camera_benchmark.cv2, "imshow", side_effect=AssertionError), \
                mock.patch.object(camera_benchmark.cv2, "waitKey", side_effect=AssertionError), \
                mock.patch.object(camera_benchmark.cv2, "destroyWindow", side_effect=AssertionError), \
                mock.patch.object(camera_benchmark, "close_frame_source"):
            result = camera_benchmark.measure("opencv", 0, "unused", 640, 360, 30, 1, False)
        self.assertEqual(result["frames_consumed"], 2)

    def test_normal_completion_cleans_up_capture_and_preview(self):
        latest = FakeLatest([None] * 15 + [
            (np.zeros((360, 640, 3), np.uint8), 3.7, 1),
            (np.zeros((360, 640, 3), np.uint8), 3.9, 2)])
        clock = FakeClock()
        with mock.patch.object(camera_benchmark, "open_camera", return_value=FakeCapture()), \
                mock.patch.object(camera_benchmark, "LatestFrame", return_value=latest), \
                mock.patch.object(camera_benchmark.time, "perf_counter", side_effect=clock), \
                mock.patch.object(camera_benchmark.cv2.aruco, "ArucoDetector", return_value=mock.Mock(detectMarkers=lambda _frame: ([], None, None))), \
                mock.patch.object(camera_benchmark.cv2, "namedWindow"), \
                mock.patch.object(camera_benchmark.cv2, "moveWindow"), \
                mock.patch.object(camera_benchmark.cv2, "imshow"), \
                mock.patch.object(camera_benchmark.cv2, "waitKey", return_value=-1), \
                mock.patch.object(camera_benchmark.cv2, "destroyWindow") as destroy_window, \
                mock.patch.object(camera_benchmark, "close_frame_source") as close_source:
            result = camera_benchmark.measure("opencv", 0, "unused", 640, 360, 30, 1, True)
        self.assertEqual(result["frames_consumed"], 2)
        close_source.assert_called_once_with(mock.ANY, mock.ANY)
        self.assertEqual(destroy_window.call_count, 2)

    def test_five_second_capture_timeout_cleans_up_capture_and_preview(self):
        latest = FakeLatest([])
        clock = FakeClock()
        with mock.patch.object(camera_benchmark, "open_camera", return_value=FakeCapture()), \
                mock.patch.object(camera_benchmark, "LatestFrame", return_value=latest), \
                mock.patch.object(camera_benchmark.time, "perf_counter", side_effect=clock), \
                mock.patch.object(camera_benchmark.cv2, "namedWindow"), \
                mock.patch.object(camera_benchmark.cv2, "moveWindow"), \
                mock.patch.object(camera_benchmark.cv2, "waitKey", return_value=-1), \
                mock.patch.object(camera_benchmark.cv2, "destroyWindow") as destroy_window, \
                mock.patch.object(camera_benchmark, "close_frame_source") as close_source:
            with self.assertRaisesRegex(RuntimeError, "No fresh frames for 5 seconds"):
                camera_benchmark.measure("opencv", 0, "unused", 640, 360, 30, 10, True)
        close_source.assert_called_once_with(mock.ANY, mock.ANY)
        self.assertEqual(destroy_window.call_count, 2)

    def test_q_and_escape_stop_measurement_and_cleanup(self):
        for key in (ord("q"), 27):
            latest = FakeLatest([(np.zeros((360, 640, 3), np.uint8), .1, 1)])
            with self.subTest(key=key), \
                    mock.patch.object(camera_benchmark, "open_camera", return_value=FakeCapture()), \
                    mock.patch.object(camera_benchmark, "LatestFrame", return_value=latest), \
                    mock.patch.object(camera_benchmark.cv2.aruco, "ArucoDetector", return_value=mock.Mock(detectMarkers=lambda _frame: ([], None, None))), \
                    mock.patch.object(camera_benchmark.cv2, "namedWindow"), \
                    mock.patch.object(camera_benchmark.cv2, "moveWindow"), \
                    mock.patch.object(camera_benchmark.cv2, "imshow"), \
                    mock.patch.object(camera_benchmark.cv2, "waitKey", return_value=key), \
                    mock.patch.object(camera_benchmark.cv2, "destroyWindow") as destroy_window, \
                    mock.patch.object(camera_benchmark, "close_frame_source") as close_source:
                with self.assertRaises(KeyboardInterrupt):
                    camera_benchmark.measure("opencv", 0, "unused", 640, 360, 30, .25, True)
            close_source.assert_called_once()
            self.assertEqual(destroy_window.call_count, 2)

    def test_q_and_escape_stop_after_warmup_and_cleanup(self):
        for key in (ord("q"), 27):
            latest = FakeLatest([None] * 15 + [
                (np.zeros((360, 640, 3), np.uint8), 3.7, 1)])
            wait_keys = [-1] * 16 + [key]
            with self.subTest(key=key), \
                    mock.patch.object(camera_benchmark, "open_camera", return_value=FakeCapture()), \
                    mock.patch.object(camera_benchmark, "LatestFrame", return_value=latest), \
                    mock.patch.object(camera_benchmark.cv2.aruco, "ArucoDetector", return_value=mock.Mock(detectMarkers=lambda _frame: ([], None, None))), \
                    mock.patch.object(camera_benchmark.cv2, "namedWindow"), \
                    mock.patch.object(camera_benchmark.cv2, "moveWindow"), \
                    mock.patch.object(camera_benchmark.cv2, "imshow"), \
                    mock.patch.object(camera_benchmark.cv2, "waitKey", side_effect=wait_keys), \
                    mock.patch.object(camera_benchmark.cv2, "destroyWindow") as destroy_window, \
                    mock.patch.object(camera_benchmark, "close_frame_source") as close_source:
                with self.assertRaises(KeyboardInterrupt):
                    camera_benchmark.measure("opencv", 0, "unused", 640, 360, 30, .25, True)
            close_source.assert_called_once()
            self.assertEqual(destroy_window.call_count, 2)

    def test_main_interrupt_closes_report_and_skips_failure(self):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "interrupted.json"
            with mock.patch.object(sys, "argv", ["camera_benchmark.py", "--optical",
                                                  "--output", str(output)]), \
                    mock.patch.object(camera_benchmark, "measure", side_effect=KeyboardInterrupt), \
                    mock.patch.object(camera_benchmark.cv2, "destroyAllWindows"):
                camera_benchmark.main()
            with output.open(encoding="utf-8") as stream:
                report = json.load(stream)
            self.assertTrue(report["interrupted"])
            self.assertEqual(report["results"], [])

    def test_main_interrupt_without_output_does_not_claim_report_saved(self):
        for key in (ord("q"), 27):
            with self.subTest(key=key), \
                    mock.patch.object(sys, "argv", ["camera_benchmark.py", "--optical"]), \
                    mock.patch.object(camera_benchmark, "measure", side_effect=KeyboardInterrupt) as measure, \
                    mock.patch.object(camera_benchmark.cv2, "destroyAllWindows"), \
                    mock.patch.object(sys, "stdout", new_callable=StringIO) as stdout:
                camera_benchmark.main()
            measure.assert_called_once()
            self.assertNotIn("report saved", stdout.getvalue().lower())

    def test_main_normal_compare_runs_each_profile_without_stale_preview(self):
        results = [{"profile": key, "optical": {"valid": False}, "valid": True}
                   for key in ("opencv", "native30", "native60", "native720")]
        with mock.patch.object(sys, "argv", ["camera_benchmark.py"]), \
                mock.patch.object(camera_benchmark, "measure", side_effect=results) as measure, \
                mock.patch.object(camera_benchmark.cv2, "destroyAllWindows"):
            camera_benchmark.main()
        self.assertEqual(measure.call_count, 4)
        self.assertEqual([call.args[0] for call in measure.call_args_list],
                         ["opencv", "native", "native", "native"])

    def test_main_q_or_escape_stops_before_subsequent_profiles(self):
        for key in (ord("q"), 27):
            with self.subTest(key=key), \
                    mock.patch.object(sys, "argv", ["camera_benchmark.py", "--optical"]), \
                    mock.patch.object(camera_benchmark, "measure", side_effect=KeyboardInterrupt) as measure, \
                    mock.patch.object(camera_benchmark.cv2, "destroyAllWindows"):
                camera_benchmark.main()
            self.assertEqual(measure.call_count, 1)


if __name__ == "__main__":
    unittest.main()
