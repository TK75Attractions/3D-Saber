import json
import contextlib
import io
import socket
import tempfile
import threading
import time
import unittest
from unittest import mock
from pathlib import Path

import udp_receive_probe
from udp_receive_probe import _percentile, arrival_minus_packet_timestamp_ms, nearest_display_frame, parse_packet, run, try_load_display_log


class UDPReceiveProbeTests(unittest.TestCase):
    def test_parses_coordinate_payload(self):
        packet = parse_packet(b"1,2,3,4")
        self.assertEqual(packet.payload, "1,2,3,4")
        self.assertIsNone(packet.timestamp)

    def test_parses_optional_timestamp_prefix(self):
        packet = parse_packet(b"ts=123.5;1,2,3,4")
        self.assertEqual(packet.payload, "1,2,3,4")
        self.assertEqual(packet.timestamp, 123.5)

    def test_parses_timestamp_prefix_alias(self):
        packet = parse_packet(b"timestamp=123.5;1,2,3,4")
        self.assertEqual(packet.payload, "1,2,3,4")
        self.assertEqual(packet.timestamp, 123.5)

    def test_keeps_malformed_timestamp_as_payload(self):
        packet = parse_packet(b"timestamp=bad;1,2,3,4")
        self.assertEqual(packet.payload, "timestamp=bad;1,2,3,4")
        self.assertIsNone(packet.timestamp)

    def test_calculates_same_epoch_arrival_difference(self):
        self.assertAlmostEqual(arrival_minus_packet_timestamp_ms(10.125, 10.0), 125.0)
        self.assertIsNone(arrival_minus_packet_timestamp_ms(10.125, None))

    def test_matches_payload_to_nearest_display_frame_and_preserves_endpoint_order(self):
        frames = [
            {
                "frameId": 10,
                "trialId": "t",
                "stateId": 0,
                "color": "red",
                "displayEpochMs": 10000,
                "normalizedEndpoints": [{"x": 0.10, "y": 0.20}, {"x": 0.30, "y": 0.20}],
            },
            {
                "frameId": 11,
                "trialId": "t",
                "stateId": 1,
                "color": "red",
                "displayEpochMs": 10016,
                "normalizedEndpoints": [{"x": 0.20, "y": 0.40}, {"x": 0.60, "y": 0.40}],
            },
        ]

        match = nearest_display_frame(frames, "1152,432,384,432", "red", 1920, 1080)

        self.assertIsNotNone(match)
        self.assertEqual(match.frame_id, 11)
        self.assertEqual(match.display_epoch_ms, 10016)
        self.assertAlmostEqual(match.coordinate_distance, 0.0)

    def test_matches_normal_blue_payload(self):
        frames = [{
            "frameId": 7,
            "trialId": "t",
            "stateId": 7,
            "color": "blue",
            "displayEpochMs": 20000,
            "normalizedEndpoints": [{"x": 0.25, "y": 0.5}, {"x": 0.75, "y": 0.5}],
        }]
        match = nearest_display_frame(frames, "480,540,1440,540", "blue", 1920, 1080)
        self.assertIsNotNone(match)
        self.assertEqual(match.frame_id, 7)
        self.assertAlmostEqual(match.coordinate_distance, 0.0)

    def test_ignores_two_point_payloads_for_display_matching(self):
        self.assertIsNone(nearest_display_frame([], "10,20", "red", 1920, 1080))

    def test_missing_or_invalid_display_log_does_not_block_receiving(self):
        frames, warning = try_load_display_log("/definitely/missing/display-log.json")
        self.assertEqual(frames, [])
        self.assertIn("計測照合なし", warning)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.json"
            path.write_text("{bad", encoding="utf-8")
            frames, warning = try_load_display_log(path)
            self.assertEqual(frames, [])
            self.assertIn("計測照合なし", warning)
            path.write_text(json.dumps({"frames": [None]}), encoding="utf-8")
            frames, warning = try_load_display_log(path)
            self.assertEqual(frames, [])
            self.assertIn("計測照合なし", warning)

    def test_ignores_non_finite_packet_coordinates(self):
        self.assertIsNone(nearest_display_frame([{"frameId": 1, "trialId": "t", "stateId": 1, "color": "red", "displayEpochMs": 1, "normalizedEndpoints": [{"x": 0, "y": 0}, {"x": 1, "y": 1}]}], "nan,0,1,1", "red", 1920, 1080))

    def test_ignores_overflow_and_non_finite_display_values(self):
        frames = [{"frameId": 1, "trialId": "t", "stateId": 1, "color": "red", "displayEpochMs": 1, "normalizedEndpoints": [{"x": 10**400, "y": 0}, {"x": 1, "y": 1}]}, {"frameId": 2, "trialId": "t", "stateId": 2, "color": "red", "displayEpochMs": float("inf"), "normalizedEndpoints": [{"x": 0, "y": 0}, {"x": 1, "y": 1}]}]
        self.assertIsNone(nearest_display_frame(frames, "1,2,3,4", "red", 1920, 1080))

    def test_reject_threshold_boundary_and_ambiguous_candidates(self):
        frame = {"stateId": 4, "trialId": "t", "displayEpochMs": 1000, "color": "red", "normalizedEndpoints": [{"x": 0.1, "y": 0.1}, {"x": 0.2, "y": 0.1}]}
        payload = "192,108,384,108"
        self.assertIsNotNone(nearest_display_frame([frame], payload, "red", 1920, 1080, reject_threshold=0.0, trial_id="t"))
        self.assertIsNone(nearest_display_frame([frame], "193,108,385,108", "red", 1920, 1080, reject_threshold=0.0005, trial_id="t"))
        duplicate = dict(frame, stateId=5, displayEpochMs=1010)
        self.assertIsNone(nearest_display_frame([frame, duplicate], payload, "red", 1920, 1080, reject_threshold=0.1, trial_id="t"))

    def test_percentile_and_zero_sample_definition(self):
        self.assertEqual(_percentile([], 0.95), None)
        self.assertEqual(_percentile([10, 20, 30, 40], 0.5), 25)

    def test_loads_normal_display_log(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "display.json"
            state = {"frameId": 1, "trialId": "t", "stateId": 1, "color": "red", "displayEpochMs": 1000, "normalizedEndpoints": [{"x": 0, "y": 0}, {"x": 1, "y": 1}]}
            path.write_text(json.dumps({"trialId": "t", "states": [state]}), encoding="utf-8")
            self.assertEqual(try_load_display_log(path), ([state], None))

    def test_rejects_missing_or_non_unique_typed_state_identity(self):
        valid = {"trialId": "t", "stateId": 1, "color": "red", "displayEpochMs": 1000, "normalizedEndpoints": [{"x": 0, "y": 0}, {"x": 1, "y": 1}]}
        for invalid in (dict(valid, stateId=[1]), dict(valid, trialId=3), [dict(valid), dict(valid)]):
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "invalid.json"
                states = invalid if isinstance(invalid, list) else [invalid]
                path.write_text(json.dumps({"trialId": "t", "states": states}), encoding="utf-8")
                self.assertEqual(try_load_display_log(path)[0], [])

    def test_probe_receives_and_releases_loopback_socket(self):
        ready = threading.Event()
        result = []
        output = io.StringIO()
        with tempfile.TemporaryDirectory() as directory:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as allocator:
                try:
                    allocator.bind(("127.0.0.1", 0))
                except PermissionError as error:
                    self.skipTest(f"ソケット利用が環境で禁止されています: {error}")
                port = allocator.getsockname()[1]

            def receive():
                with contextlib.redirect_stdout(output):
                    result.append(run("127.0.0.1", (port,), 0.2, on_ready=ready.set))

            thread = threading.Thread(target=receive)
            thread.start()
            self.assertTrue(ready.wait(1.0))
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
                sender.sendto(b"1,2,3,4", ("127.0.0.1", port))
            thread.join(1.0)
            self.assertFalse(thread.is_alive())
            self.assertEqual(result, [0])
            received = output.getvalue()
            self.assertIn(f"port={port}", received)
            self.assertIn("count=1", received)
            self.assertIn("payload=1,2,3,4", received)
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as rebound:
            rebound.bind(("127.0.0.1", port))

    def test_run_reconciles_same_trial_packets_and_prints_latency(self):
        ready = threading.Event()
        result = []
        output = io.StringIO()
        with tempfile.TemporaryDirectory() as directory:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as allocator:
                try:
                    allocator.bind(("127.0.0.1", 0))
                except PermissionError as error:
                    self.skipTest(f"ソケット利用が環境で禁止されています: {error}")
                port = allocator.getsockname()[1]
            now = time.time()
            state = {"trialId": "t", "stateId": 0, "color": "red", "displayEpochMs": now * 1000, "normalizedEndpoints": [{"x": 0.1, "y": 0.2}, {"x": 0.3, "y": 0.2}]}
            log = Path(directory) / "display.json"
            log.write_text(json.dumps({"trialId": "t", "trialStartedEpochMs": now * 1000, "trialEndedEpochMs": now * 1000, "states": [state]}), encoding="utf-8")
            def receive():
                with contextlib.redirect_stdout(output):
                    result.append(run("127.0.0.1", (5005,), 0.2, display_log=log, on_ready=ready.set, input_width=100, input_height=100, reject_threshold=0.0))
            thread = threading.Thread(target=receive); thread.start(); self.assertTrue(ready.wait(1.0))
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
                timestamp = now + 0.005
                payload = f"timestamp={timestamp};10,20,30,20".encode()
                sender.sendto(payload, ("127.0.0.1", 5005))
                sender.sendto(payload, ("127.0.0.1", 5005))
            thread.join(1.0); self.assertFalse(thread.is_alive()); self.assertEqual(result, [0])
            received = output.getvalue()
            self.assertIn("display_to_phone_ms=", received); self.assertIn("matched=1", received); self.assertIn("duplicate=1", received)

    def test_permission_error_display_log_warns_and_receiving_continues(self):
        ready = threading.Event()
        result = []
        output = io.StringIO()
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as allocator:
            try:
                allocator.bind(("127.0.0.1", 0))
            except PermissionError as error:
                self.skipTest(f"ソケット利用が環境で禁止されています: {error}")
            port = allocator.getsockname()[1]

        def receive():
            with contextlib.redirect_stdout(output):
                with mock.patch.object(Path, "open", side_effect=PermissionError("読取拒否")):
                    result.append(run("127.0.0.1", (port,), 0.2, display_log=Path("denied.json"), on_ready=ready.set))

        thread = threading.Thread(target=receive)
        thread.start()
        self.assertTrue(ready.wait(1.0))
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
            sender.sendto(b"1,2,3,4", ("127.0.0.1", port))
        thread.join(1.0)
        self.assertFalse(thread.is_alive())
        self.assertEqual(result, [0])
        received = output.getvalue()
        self.assertIn("計測照合なしで続行", received)
        self.assertIn("count=1", received)
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as rebound:
            rebound.bind(("127.0.0.1", port))

    def test_ctrl_c_stops_and_releases_socket(self):
        output = io.StringIO()
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as allocator:
            try:
                allocator.bind(("127.0.0.1", 0))
            except PermissionError as error:
                self.skipTest(f"ソケット利用が環境で禁止されています: {error}")
            port = allocator.getsockname()[1]
        with mock.patch.object(udp_receive_probe.select, "select", side_effect=KeyboardInterrupt):
            with contextlib.redirect_stdout(output):
                result = run("127.0.0.1", (port,), 0)
        self.assertEqual(result, 0)
        self.assertIn("stopped", output.getvalue())
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as rebound:
            rebound.bind(("127.0.0.1", port))

    def test_port_occupancy_releases_first_socket_and_reports_japanese(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as first_allocator:
            try:
                first_allocator.bind(("127.0.0.1", 0))
            except PermissionError as error:
                self.skipTest(f"ソケット利用が環境で禁止されています: {error}")
            first = first_allocator.getsockname()[1]
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as occupied:
                occupied.bind(("127.0.0.1", 0))
                second = occupied.getsockname()[1]
                first_allocator.close()
                output = io.StringIO()
                with contextlib.redirect_stdout(output):
                    result = run("127.0.0.1", (first, second), 0.1)
                self.assertEqual(result, 2)
                self.assertIn("Unityまたは別のprobe", output.getvalue())
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as rebound:
                    rebound.bind(("127.0.0.1", first))


if __name__ == "__main__":
    unittest.main()
