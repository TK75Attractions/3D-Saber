import json
import contextlib
import io
import socket
import tempfile
import threading
import time
import unittest
import urllib.request
from unittest import mock
from pathlib import Path

import udp_receive_probe
from udp_receive_probe import LiveState, _percentile, arrival_minus_packet_timestamp_ms, nearest_display_frame, parse_packet, run, start_live_server, try_load_display_log


class UDPReceiveProbeTests(unittest.TestCase):
    def _reserve_udp_ports(self, count):
        ports = []
        try:
            while len(ports) < count:
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as allocator:
                    allocator.bind(("127.0.0.1", 0))
                    port = allocator.getsockname()[1]
                    if port not in ports:
                        ports.append(port)
        except PermissionError as error:
            self.skipTest(f"ソケット利用が環境で禁止されています: {error}")
        return ports

    def _reserve_tcp_port(self):
        try:
            with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as allocator:
                allocator.bind(("127.0.0.1", 0))
                return allocator.getsockname()[1]
        except PermissionError as error:
            self.skipTest(f"ソケット利用が環境で禁止されています: {error}")

    def test_live_state_keeps_bounded_history_and_never_serializes_invalid_delta(self):
        state = LiveState(history_limit=100)
        future_timestamp = 11.0
        for order in range(1, 104):
            state.record(5005, parse_packet(f"ts={future_timestamp};1,2,3,4".encode()), 10.0, order, "red")
        state.record(5006, parse_packet(b"1,2,3,4"), 12.0, 104, "blue")
        state.record(5006, parse_packet(b"ts=1.0;1,2,3,4"), 13.0, 105, "blue")
        snapshot = state.snapshot()
        self.assertEqual(snapshot["counts"]["red"], 103)
        self.assertEqual(snapshot["counts"]["blue"], 2)
        self.assertEqual(len(snapshot["history"]), 100)
        self.assertEqual(snapshot["history"][0]["order"], 6)
        self.assertEqual(snapshot["history"][-1]["order"], 105)
        self.assertLess(snapshot["latest"]["red"]["arrivalMinusPhoneMs"], 0)
        self.assertEqual(snapshot["latest"]["red"]["order"], 103)
        self.assertTrue(snapshot["latest"]["blue"]["validTimestamp"])
        self.assertEqual(snapshot["latest"]["blue"]["order"], 105)
        self.assertFalse(next(item for item in snapshot["history"] if item["order"] == 104)["validTimestamp"])
        json.dumps(snapshot, allow_nan=False)

    def test_live_status_exposes_bonjour_and_network_configuration(self):
        snapshot = LiveState().snapshot()
        network = snapshot["network"]
        self.assertEqual(network["ports"], {"red": 5005, "blue": 5006})
        self.assertEqual(network["bonjour"]["serviceType"], "_phonesaber._udp")
        self.assertIn("Unity", network["unityNote"])
        self.assertIsInstance(network["ipv4"], list)
        json.dumps(snapshot, allow_nan=False)

    def test_live_http_rejects_non_loopback_host(self):
        output = io.StringIO()
        with contextlib.redirect_stdout(output):
            result = run("127.0.0.1", (5005,), 0, live=True, http_host="0.0.0.0")
        self.assertEqual(result, 2)
        self.assertIn("loopback", output.getvalue())

    def test_live_http_server_serves_only_dashboard_and_status_api(self):
        with tempfile.TemporaryDirectory() as directory:
            html = Path(directory) / "saber_camera_test.html"
            html.write_text("<html>live</html>", encoding="utf-8")
            try:
                server, state = start_live_server("127.0.0.1", 0, html)
            except PermissionError as error:
                self.skipTest(f"HTTPソケット利用が環境で禁止されています: {error}")
            try:
                state.record(5005, parse_packet(b"ts=10;1,2,3,4"), 10.125, 1, "red")
                base = f"http://127.0.0.1:{server.address[1]}"
                with urllib.request.urlopen(base + "/api/status", timeout=1) as response:
                    payload = json.loads(response.read())
                self.assertEqual(payload["latest"]["red"]["arrivalMinusPhoneMs"], 125.0)
                with urllib.request.urlopen(base + "/saber_camera_test.html", timeout=1) as response:
                    self.assertEqual(response.read(), b"<html>live</html>")
                with self.assertRaises(urllib.error.HTTPError):
                    urllib.request.urlopen(base + "/private", timeout=1)
            finally:
                state.stop()
                server.close()

    def test_real_live_run_updates_red_blue_api_for_invalid_timestamps_and_bounds_history(self):
        red_port, blue_port = self._reserve_udp_ports(2)
        http_port = self._reserve_tcp_port()
        ready = threading.Event()
        result = []
        output = io.StringIO()
        with tempfile.TemporaryDirectory() as directory:
            html = Path(directory) / "dashboard.html"
            html.write_text("<html>live</html>", encoding="utf-8")

            def receive():
                with contextlib.redirect_stdout(output):
                    result.append(run("127.0.0.1", (red_port, blue_port), .8, on_ready=ready.set,
                                      port_colors={red_port: "red", blue_port: "blue"}, live=True,
                                      http_port=http_port, html_path=html))

            thread = threading.Thread(target=receive)
            thread.start()
            self.assertTrue(ready.wait(1.0))
            base = f"http://127.0.0.1:{http_port}"
            future_timestamp = time.time() + 10.0
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
                for _ in range(103):
                    sender.sendto(f"ts={future_timestamp};1,2,3,4".encode(), ("127.0.0.1", red_port))
                sender.sendto(b"1,2,3,4", ("127.0.0.1", blue_port))
                sender.sendto(b"ts=1.0;5,6,7,8", ("127.0.0.1", blue_port))
            deadline = time.monotonic() + 1.0
            payload = None
            while time.monotonic() < deadline:
                try:
                    with urllib.request.urlopen(base + "/api/status", timeout=.2) as response:
                        payload = json.loads(response.read())
                    if payload["counts"]["red"] == 103 and payload["counts"]["blue"] == 2:
                        break
                except (OSError, urllib.error.URLError):
                    pass
                time.sleep(.01)
            thread.join(2.0)
            self.assertFalse(thread.is_alive())
            self.assertEqual(result, [0])
            self.assertIsNotNone(payload)
            self.assertEqual(payload["counts"]["red"], 103)
            self.assertEqual(payload["counts"]["blue"], 2)
            self.assertEqual(len(payload["history"]), 100)
            self.assertEqual(payload["history"][0]["order"], 6)
            self.assertEqual(payload["history"][-1]["order"], 105)
            self.assertLess(payload["latest"]["red"]["arrivalMinusPhoneMs"], 0)
            self.assertEqual(payload["latest"]["red"]["order"], 103)
            self.assertEqual(payload["latest"]["blue"]["validTimestamp"], True)
            self.assertEqual(payload["latest"]["blue"]["order"], 105)
            self.assertFalse(next(item for item in payload["history"] if item["order"] == 104)["validTimestamp"])
            json.dumps(payload, allow_nan=False)
        for port in (red_port, blue_port):
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as rebound:
                rebound.bind(("127.0.0.1", port))
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as rebound:
            rebound.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            rebound.bind(("127.0.0.1", http_port))

    def test_live_run_opens_browser_only_after_http_ready_and_reports_false(self):
        port = self._reserve_udp_ports(1)[0]
        http_port = self._reserve_tcp_port()
        ready = threading.Event()
        browser_calls = []
        with tempfile.TemporaryDirectory() as directory:
            html = Path(directory) / "dashboard.html"
            html.write_text("<html>live</html>", encoding="utf-8")
            def on_ready():
                self.assertEqual(browser_calls, [])
                ready.set()
            def open_browser_and_check_http(url):
                browser_calls.append(url)
                with urllib.request.urlopen(url, timeout=1) as response:
                    self.assertEqual(response.status, 200)
                return True

            with mock.patch.object(udp_receive_probe.webbrowser, "open", side_effect=open_browser_and_check_http):
                result = run("127.0.0.1", (port,), .1, on_ready=on_ready, live=True,
                             http_port=http_port, html_path=html, open_browser=True)
            self.assertTrue(ready.is_set())
            self.assertEqual(result, 0)
            self.assertEqual(len(browser_calls), 1)

            with mock.patch.object(udp_receive_probe.webbrowser, "open", return_value=False):
                result = run("127.0.0.1", (port,), .1, live=True, http_port=http_port,
                             html_path=html, open_browser=True)
            self.assertEqual(result, 2)
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as rebound:
            rebound.bind(("127.0.0.1", port))
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as rebound:
            rebound.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            rebound.bind(("127.0.0.1", http_port))

    def test_live_start_failure_cleans_udp_when_http_port_is_occupied(self):
        udp_port = self._reserve_udp_ports(1)[0]
        http_port = self._reserve_tcp_port()
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as occupied:
            occupied.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            occupied.bind(("127.0.0.1", http_port))
            occupied.listen(1)
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                result = run("127.0.0.1", (udp_port,), .1, live=True, http_port=http_port)
        self.assertEqual(result, 2)
        self.assertIn("ライブ起動に失敗", output.getvalue())
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as rebound:
            rebound.bind(("127.0.0.1", udp_port))

    def test_live_missing_html_fails_before_browser_and_releases_udp(self):
        udp_port = self._reserve_udp_ports(1)[0]
        http_port = self._reserve_tcp_port()
        with mock.patch.object(udp_receive_probe.webbrowser, "open") as browser:
            output = io.StringIO()
            with contextlib.redirect_stdout(output):
                result = run("127.0.0.1", (udp_port,), .1, live=True, http_port=http_port,
                             html_path="/definitely/missing/saber_camera_test.html", open_browser=True)
        self.assertEqual(result, 2)
        self.assertIn("HTMLが存在しません", output.getvalue())
        browser.assert_not_called()
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as rebound:
            rebound.bind(("127.0.0.1", udp_port))
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
        log, warning = try_load_display_log("/definitely/missing/display-log.json")
        self.assertIsNone(log)
        self.assertIn("計測照合なし", warning)
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "bad.json"
            path.write_text("{bad", encoding="utf-8")
            log, warning = try_load_display_log(path)
            self.assertIsNone(log)
            self.assertIn("計測照合なし", warning)
            path.write_text(json.dumps({"frames": [None]}), encoding="utf-8")
            log, warning = try_load_display_log(path)
            self.assertIsNone(log)
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
        self.assertEqual(_percentile([10, 20, 30, 40], 0.0), 10)
        self.assertEqual(_percentile([10, 20, 30, 40], 0.95), 38.5)

    def test_loads_normal_display_log(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "display.json"
            state = {"frameId": 1, "trialId": "t", "stateId": 1, "color": "red", "displayEpochMs": 1000, "normalizedEndpoints": [{"x": 0, "y": 0}, {"x": 1, "y": 1}]}
            path.write_text(json.dumps({"trialId": "t", "states": [state]}), encoding="utf-8")
            log, warning = try_load_display_log(path)
            self.assertEqual(log.frames, [state]); self.assertEqual((log.started_epoch_ms, log.ended_epoch_ms), (1000.0, 1000.0)); self.assertIsNone(warning)

    def test_rejects_missing_or_non_unique_typed_state_identity(self):
        valid = {"trialId": "t", "stateId": 1, "color": "red", "displayEpochMs": 1000, "normalizedEndpoints": [{"x": 0, "y": 0}, {"x": 1, "y": 1}]}
        for invalid in (dict(valid, stateId=[1]), dict(valid, trialId=3), [dict(valid), dict(valid)]):
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "invalid.json"
                states = invalid if isinstance(invalid, list) else [invalid]
                path.write_text(json.dumps({"trialId": "t", "states": states}), encoding="utf-8")
                self.assertIsNone(try_load_display_log(path)[0])

    def test_rejects_invalid_color_and_endpoint_shape_without_type_error(self):
        valid = {"trialId": "t", "stateId": 1, "color": "red", "displayEpochMs": 1000, "normalizedEndpoints": [{"x": 0, "y": 0}, {"x": 1, "y": 1}]}
        for invalid in (dict(valid, color=[]), dict(valid, color={}), dict(valid, normalizedEndpoints=[{"x": "nan", "y": 0}, {"x": 1, "y": 1}]), dict(valid, normalizedEndpoints=[{"x": 2, "y": 0}, {"x": 1, "y": 1}])):
            with tempfile.TemporaryDirectory() as directory:
                path = Path(directory) / "invalid.json"
                path.write_text(json.dumps({"trialId": "t", "trialStartedEpochMs": 999, "trialEndedEpochMs": 1001, "states": [invalid]}), encoding="utf-8")
                self.assertIsNone(try_load_display_log(path)[0])

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
            log.write_text(json.dumps({"trialId": "t", "trialStartedEpochMs": (now - 0.01) * 1000, "trialEndedEpochMs": (now + 0.02) * 1000, "states": [state]}), encoding="utf-8")
            def receive():
                with contextlib.redirect_stdout(output):
                    result.append(run("127.0.0.1", (port,), 0.2, display_log=log, on_ready=ready.set, input_width=100, input_height=100, reject_threshold=0.0, port_colors={port: "red"}))
            thread = threading.Thread(target=receive); thread.start(); self.assertTrue(ready.wait(1.0))
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
                timestamp = now + 0.005
                payload = f"timestamp={timestamp};10,20,30,20".encode()
                sender.sendto(payload, ("127.0.0.1", port))
                sender.sendto(payload, ("127.0.0.1", port))
            thread.join(1.0); self.assertFalse(thread.is_alive()); self.assertEqual(result, [0])
            received = output.getvalue()
            self.assertIn("display_to_phone_ms=", received); self.assertIn("matched=1", received); self.assertIn("duplicate=1", received)

    def test_run_uses_validated_trial_interval_and_does_not_consume_outside_packet(self):
        ready = threading.Event(); result = []; output = io.StringIO()
        with tempfile.TemporaryDirectory() as directory:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as allocator:
                try: allocator.bind(("127.0.0.1", 0))
                except PermissionError as error: self.skipTest(f"ソケット利用が環境で禁止されています: {error}")
                port = allocator.getsockname()[1]
            now = time.time()
            state = {"trialId": "trial", "stateId": 0, "color": "blue", "displayEpochMs": now * 1000, "normalizedEndpoints": [{"x": 0.1, "y": 0.2}, {"x": 0.3, "y": 0.2}]}
            log = Path(directory) / "saved-after-receive.json"
            received_before_log = threading.Event()
            def receive():
                def save_after_receive(_port, _packet, _arrival):
                    log.write_text(json.dumps({"trialId": "trial", "trialStartedEpochMs": (now - .01) * 1000, "trialEndedEpochMs": (now + .03) * 1000, "states": [state]}), encoding="utf-8")
                    received_before_log.set()
                with contextlib.redirect_stdout(output): result.append(run("127.0.0.1", (port,), .25, display_log=log, on_ready=ready.set, input_width=100, input_height=100, reject_threshold=0.0, port_colors={port: "blue"}, on_packet=save_after_receive))
            thread = threading.Thread(target=receive); thread.start(); self.assertTrue(ready.wait(1.0))
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
                sender.sendto(f"ts={now - .5};10,20,30,20".encode(), ("127.0.0.1", port))
                sender.sendto(f"ts={now + .005};10,20,30,20".encode(), ("127.0.0.1", port))
            thread.join(1.0); self.assertFalse(thread.is_alive()); self.assertEqual(result, [0])
            self.assertTrue(received_before_log.is_set())
            received = output.getvalue()
            self.assertIn("matched=1", received); self.assertIn("unmatched=1", received); self.assertIn("color=blue", received)

    def test_real_run_reports_red_blue_statistics_threshold_boundary_and_first_timestamp(self):
        ready = threading.Event(); result = []; output = io.StringIO()
        with tempfile.TemporaryDirectory() as directory:
            ports = []
            try:
                for _ in range(2):
                    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as allocator:
                        allocator.bind(("127.0.0.1", 0)); ports.append(allocator.getsockname()[1])
            except PermissionError as error:
                self.skipTest(f"ソケット利用が環境で禁止されています: {error}")
            red_port, blue_port = ports
            now = time.time()
            red_states = [
                {"trialId": "trial", "stateId": 0, "color": "red", "displayEpochMs": now * 1000, "normalizedEndpoints": [{"x": .1, "y": .2}, {"x": .3, "y": .2}]},
                {"trialId": "trial", "stateId": 1, "color": "red", "displayEpochMs": (now + .009) * 1000, "normalizedEndpoints": [{"x": .4, "y": .4}, {"x": .6, "y": .4}]},
            ]
            blue_state = {"trialId": "trial", "stateId": 0, "color": "blue", "displayEpochMs": now * 1000, "normalizedEndpoints": [{"x": .25, "y": .5}, {"x": .75, "y": .5}]}
            log = Path(directory) / "known-samples.json"
            log.write_text(json.dumps({"trialId": "trial", "trialStartedEpochMs": (now - .01) * 1000, "trialEndedEpochMs": (now + .2) * 1000, "states": red_states + [blue_state]}), encoding="utf-8")
            def receive():
                with contextlib.redirect_stdout(output): result.append(run("127.0.0.1", (red_port, blue_port), .3, display_log=log, on_ready=ready.set, input_width=1000, input_height=1000, reject_threshold=.02, port_colors={red_port: "red", blue_port: "blue"}))
            thread = threading.Thread(target=receive); thread.start(); self.assertTrue(ready.wait(1.0))
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
                # input_width=1000 makes 20 px exactly the .02 threshold and 21 px just over it.
                sender.sendto(f"ts={now + .001};120,200,320,200".encode(), ("127.0.0.1", red_port))
                sender.sendto(f"ts={now + .010};100,200,300,200".encode(), ("127.0.0.1", red_port))
                sender.sendto(f"ts={now + .011};421,400,621,400".encode(), ("127.0.0.1", red_port))
                sender.sendto(f"ts={now + .019};400,400,600,400".encode(), ("127.0.0.1", red_port))
                sender.sendto(f"ts={now + .0055};250,500,750,500".encode(), ("127.0.0.1", blue_port))
            thread.join(1.0); self.assertFalse(thread.is_alive()); self.assertEqual(result, [0])
            received = output.getvalue()
            self.assertIn("reconcile_summary matched=3 unmatched=1 duplicate=1", received)
            self.assertIn("statistics color=all count=3 average_ms=5.500 median_ms=5.500 p50_ms=5.500 p95_ms=9.550", received)
            self.assertIn("statistics color=red count=2 average_ms=5.500 median_ms=5.500 p50_ms=5.500 p95_ms=9.550", received)
            self.assertIn("statistics color=blue count=1 average_ms=5.500 median_ms=5.500 p50_ms=5.500 p95_ms=5.500", received)

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
        with tempfile.TemporaryDirectory() as directory:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as allocator:
                try:
                    allocator.bind(("127.0.0.1", 0))
                except PermissionError as error:
                    self.skipTest(f"ソケット利用が環境で禁止されています: {error}")
                port = allocator.getsockname()[1]
            now = time.time()
            log = Path(directory) / "ctrl-c.json"
            log.write_text(json.dumps({"trialId": "ctrl-c", "trialStartedEpochMs": (now - .01) * 1000, "trialEndedEpochMs": (now + .01) * 1000, "states": [{"trialId": "ctrl-c", "stateId": 0, "color": "red", "displayEpochMs": now * 1000, "normalizedEndpoints": [{"x": .1, "y": .2}, {"x": .3, "y": .2}]}]}), encoding="utf-8")
            calls = 0
            def interrupt_after_first_receive(readers, _writers, _errors, _timeout):
                nonlocal calls
                calls += 1
                if calls == 1:
                    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
                        sender.sendto(f"timestamp={now + .001};10,20,30,20".encode(), ("127.0.0.1", port))
                    return readers, [], []
                raise KeyboardInterrupt
            with mock.patch.object(udp_receive_probe.select, "select", side_effect=interrupt_after_first_receive):
                with contextlib.redirect_stdout(output):
                    result = run("127.0.0.1", (port,), 0, display_log=log, input_width=100, input_height=100, reject_threshold=0.0, port_colors={port: "red"})
            self.assertEqual(result, 0)
            self.assertIn("stopped", output.getvalue())
            self.assertIn("statistics color=all count=1", output.getvalue())
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
