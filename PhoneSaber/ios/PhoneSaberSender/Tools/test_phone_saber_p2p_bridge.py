#!/usr/bin/env python3
"""Mac P2P bridge: compile the real Swift bridge and drive it over loopback UDP.

Covers payload parsing, RED/BLUE routing to the local Unity ports, malformed
packets, duplicates/reordering, a new sender session (app restart), several
peers, a bridge restart, ping/pong liveness and log rate limiting. The actual
peer-to-peer Wi-Fi (AWDL) hop needs two devices; see P2P_BRIDGE.md.
"""
from __future__ import annotations

import os
import csv
import shutil
import signal
import socket
import struct
import subprocess
import sys
import tempfile
import threading
import time
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import phone_saber_p2p_bridge as launcher

COORDINATES, PING, PONG = 1, 2, 3
RED, BLUE = 0, 1


def datagram(kind: int, color: int, session: int, sequence: int, body: bytes = b"") -> bytes:
    return b"PSP2" + bytes([1, kind, color, 0]) + struct.pack(">IQ", session, sequence) + body


def free_udp_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


class BridgeTestCleanupTests(unittest.TestCase):
    def test_failed_setup_stops_the_started_bridge(self):
        with tempfile.TemporaryDirectory() as directory:
            case = P2PBridgeTests("test_red_and_blue_bodies_reach_their_unity_ports_unchanged")
            case.cache = mock.Mock()
            case.cache.name = directory
            process = mock.Mock()
            process.poll.return_value = None

            def fail_start():
                case.process = process
                raise AssertionError("listener startup failed")

            with mock.patch.object(case, "start_bridge", side_effect=fail_start):
                try:
                    with self.assertRaises(AssertionError):
                        case.setUp()
                finally:
                    case.doCleanups()
            process.terminate.assert_called_once()
            process.wait.assert_called_once_with(timeout=5)


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "requires the macOS Swift toolchain")
class P2PBridgeTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.cache = tempfile.TemporaryDirectory(prefix="phonesaber-p2p-bridge-")
        cls.addClassCleanup(cls.cache.cleanup)
        cls.binary = launcher.build(Path(cls.cache.name))

    def setUp(self):
        self.unity = {}
        for color in (RED, BLUE):
            receiver = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            receiver.bind(("127.0.0.1", 0))
            receiver.settimeout(0.5)
            self.addCleanup(receiver.close)
            self.unity[color] = receiver
        self.listen_port = free_udp_port()
        self.log_path = Path(self.cache.name) / f"bridge-{self.id().rsplit('.', 1)[-1]}.log"
        self.process = None
        # 起動待ちの assertion が失敗しても、子 process を次のテストに残さない。
        self.addCleanup(self.stop_bridge)
        self.start_bridge()
        self.phone = self.new_socket()

    def new_socket(self) -> socket.socket:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.settimeout(0.5)
        self.addCleanup(sock.close)
        return sock

    def start_bridge(self, extra_args=()):
        started_before = self.log().count("listening on UDP")
        log = self.log_path.open("a")
        self.addCleanup(log.close)
        self.process = subprocess.Popen(
            [str(self.binary), "--no-bonjour", "--loopback-only", "--listen-port", str(self.listen_port),
             "--red-port", str(self.unity[RED].getsockname()[1]),
             "--blue-port", str(self.unity[BLUE].getsockname()[1]), "--stats-interval", "0.5", *extra_args],
            stdout=log, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 10
        while self.log().count("listening on UDP") == started_before and time.monotonic() < deadline:
            time.sleep(0.02)
        self.assertGreater(self.log().count("listening on UDP"), started_before, self.log())

    def stop_bridge(self):
        if self.process and self.process.poll() is None:
            self.process.terminate()
            self.process.wait(timeout=5)

    def log(self) -> str:
        return self.log_path.read_text() if self.log_path.exists() else ""

    def send(self, data: bytes, sock: socket.socket | None = None):
        (sock or self.phone).sendto(data, ("127.0.0.1", self.listen_port))

    def received(self, color: int) -> bytes | None:
        try:
            return self.unity[color].recvfrom(512)[0]
        except socket.timeout:
            return None

    def ping(self, session: int, sequence: int, sock: socket.socket | None = None) -> bytes:
        sock = sock or self.phone
        self.send(datagram(PING, RED, session, sequence), sock)
        return sock.recvfrom(64)[0]

    def test_ping_is_answered_with_a_pong_for_the_same_session(self):
        reply = self.ping(1234, 7)
        self.assertEqual(reply, datagram(PONG, RED, 1234, 7))
        self.assertIn("peer alive (session 1234)", self.log())

    def test_red_and_blue_bodies_reach_their_unity_ports_unchanged(self):
        for sequence, (color, body) in enumerate([(RED, b"10,20,30,40"), (BLUE, b"-5,0,1919,1079"),
                                                  (RED, b"ts=1759400000.123456;1,2,3,4")], start=1):
            self.send(datagram(COORDINATES, color, 9, sequence, body))
            self.assertEqual(self.received(color), body)
            self.assertIsNone(self.received(BLUE if color == RED else RED), "no cross-routing")

    def test_shared_coordinate_contract_vectors_are_forwarded_unchanged(self):
        vectors = Path(__file__).resolve().parents[3] / "tools/fixtures/protocol_coordinates.tsv"
        with vectors.open() as source:
            for sequence, row in enumerate(csv.DictReader(source, delimiter="\t"), start=1):
                with self.subTest(vector=row["id"]):
                    body = row["payload"].encode("ascii")
                    color = sequence % 2
                    self.send(datagram(COORDINATES, color, 4242, sequence, body))
                    self.assertEqual(self.received(color), body if row["p2p_valid"] == "1" else None)

    def test_malformed_datagrams_are_dropped_and_counted_once_in_the_log(self):
        for bad in (b"garbage", b"PSP2" + bytes([9, 1, 0, 0]) + bytes(12) + b"1,2,3,4",
                    datagram(COORDINATES, 2, 1, 1, b"1,2,3,4"), datagram(COORDINATES, RED, 1, 2, b"1,2,3"),
                    datagram(COORDINATES, RED, 1, 3, b"a,b,c,d"), datagram(COORDINATES, RED, 1, 4, b"1" * 300),
                    datagram(PONG, RED, 1, 5)):
            self.send(bad)
        self.assertIsNone(self.received(RED))
        self.send(datagram(COORDINATES, RED, 1, 10, b"1,2,3,4"))
        self.assertEqual(self.received(RED), b"1,2,3,4", "a valid packet after garbage still flows")
        time.sleep(0.8)
        log = self.log()
        self.assertEqual(log.count("malformed datagram dropped"), 1)
        self.assertRegex(log, r"malformed=7")

    def test_duplicates_and_reordered_packets_are_dropped_and_a_restarted_app_is_accepted(self):
        self.send(datagram(COORDINATES, RED, 5, 10, b"1,1,1,1"))
        self.assertEqual(self.received(RED), b"1,1,1,1")
        self.send(datagram(COORDINATES, RED, 5, 10, b"2,2,2,2"))   # duplicate
        self.send(datagram(COORDINATES, RED, 5, 9, b"3,3,3,3"))    # older
        self.assertIsNone(self.received(RED))
        self.send(datagram(COORDINATES, RED, 6, 1, b"4,4,4,4"))    # app restarted: new session
        self.assertEqual(self.received(RED), b"4,4,4,4")
        self.assertIn("RED received (session 6", self.log())

    def test_two_peers_are_both_served(self):
        other = self.new_socket()
        self.assertEqual(self.ping(1, 1), datagram(PONG, RED, 1, 1))
        self.assertEqual(self.ping(2, 1, other), datagram(PONG, RED, 2, 1))
        self.send(datagram(COORDINATES, RED, 2, 2, b"7,7,7,7"), other)
        self.assertEqual(self.received(RED), b"7,7,7,7")
        self.assertIn("(peers: 2)", self.log())

    def test_two_peers_share_the_unity_sender_identity(self):
        # 現行の制約の監査: Unity の IP gate では P2P の別端末を区別できない。
        other = self.new_socket()
        forwarded_sources = []
        for sock, session, body in ((self.phone, 1, b"1,2,3,4"), (other, 2, b"7,8,9,10")):
            self.send(datagram(COORDINATES, RED, session, 1, body), sock)
            received, source = self.unity[RED].recvfrom(512)
            self.assertEqual(received, body)
            forwarded_sources.append(source)
        self.assertEqual(forwarded_sources[0], forwarded_sources[1])
        self.assertEqual(forwarded_sources[0][0], "127.0.0.1")

    def test_reconnecting_the_same_session_does_not_wait_for_the_old_peer(self):
        self.ping(41, 1)
        self.send(datagram(COORDINATES, RED, 41, 2, b"1,2,3,4"))
        self.assertEqual(self.received(RED), b"1,2,3,4")
        other = self.new_socket()
        self.assertEqual(self.ping(41, 3, other), datagram(PONG, RED, 41, 3))
        self.send(datagram(COORDINATES, RED, 41, 4, b"5,6,7,8"), other)
        self.assertEqual(self.received(RED), b"5,6,7,8")
        self.send(datagram(COORDINATES, RED, 41, 2, b"1,2,3,4"))
        self.assertIsNone(self.received(RED), "old peer cannot replay old coordinates")

    def test_an_idle_peer_is_closed_and_the_same_session_can_reconnect(self):
        self.stop_bridge()
        self.start_bridge(("--peer-idle-timeout", "1"))
        self.ping(44, 1)
        deadline = time.monotonic() + 4
        while "peer idle for 1s" not in self.log() and time.monotonic() < deadline:
            time.sleep(0.05)
        self.assertIn("peer idle for 1s", self.log())
        other = self.new_socket()
        self.assertEqual(self.ping(44, 2, other), datagram(PONG, RED, 44, 2))
        self.send(datagram(COORDINATES, RED, 44, 3, b"1,2,3,4"), other)
        self.assertEqual(self.received(RED), b"1,2,3,4")

    def test_sigint_stops_the_bridge_cleanly(self):
        self.ping(45, 1)
        self.process.send_signal(signal.SIGINT)
        self.assertEqual(self.process.wait(5), 0)
        self.assertIn("[P2P] stopping", self.log())

    def test_forwarding_recovers_when_unity_binds_after_datagrams_were_dropped(self):
        port = self.unity[RED].getsockname()[1]
        self.unity[RED].close()
        for sequence in range(10):
            self.send(datagram(COORDINATES, RED, 42, sequence, b"1,2,3,4"))
        self.ping(42, 10)  # waits until the bridge consumed the preceding packets
        receiver = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        receiver.bind(("127.0.0.1", port))
        receiver.settimeout(1)
        self.addCleanup(receiver.close)
        self.send(datagram(COORDINATES, RED, 42, 11, b"5,6,7,8"))
        self.assertEqual(receiver.recvfrom(64)[0], b"5,6,7,8")

    def test_large_and_random_malformed_packets_do_not_stop_valid_traffic(self):
        for size in (0, 1, 19, 20, 276, 277, 8192):
            self.send(os.urandom(size))
        for sequence in range(200):
            self.send(datagram(COORDINATES, RED, 43, sequence, b"nan,inf,,4"))
        self.assertEqual(self.ping(43, 201), datagram(PONG, RED, 43, 201))
        self.send(datagram(COORDINATES, BLUE, 43, 202, b"1,2,3,4"))
        self.assertEqual(self.received(BLUE), b"1,2,3,4")

    def test_nonfinite_intervals_are_rejected_without_starting_the_bridge(self):
        for option in ("--stats-interval", "--peer-idle-timeout", "--simulate-listener-failure-after"):
            for value in ("inf", "nan"):
                with self.subTest(option=option, value=value):
                    process = subprocess.Popen([str(self.binary), option, value],
                                               stdout=subprocess.PIPE, stderr=subprocess.PIPE)
                    try:
                        _, stderr = process.communicate(timeout=0.5)
                        self.assertEqual(process.returncode, 2, stderr)
                    finally:
                        if process.poll() is None:
                            process.kill()
                            process.communicate()

    def test_per_frame_traffic_logs_only_the_first_packet_and_periodic_totals(self):
        for sequence in range(1, 101):
            self.send(datagram(COORDINATES, RED, 3, sequence, b"%d,0,1,1" % sequence))
        time.sleep(0.8)
        log = self.log()
        self.assertEqual(log.count("RED received"), 1)
        self.assertLess(len(log.splitlines()), 15, log)
        self.assertRegex(log, r"RED=\d+")
        self.assertRegex(log, r"maxGapMsRED=\d+", "largest arrival gap is reported for stall diagnosis")

    def test_bridge_restart_resumes_forwarding_for_the_same_phone(self):
        self.assertEqual(self.ping(11, 1), datagram(PONG, RED, 11, 1))
        self.stop_bridge()
        self.send(datagram(PING, RED, 11, 2))
        with self.assertRaises(socket.timeout):
            self.phone.recvfrom(64)                       # receiver down: no pong -> phone falls back
        self.start_bridge()
        self.assertEqual(self.ping(11, 3), datagram(PONG, RED, 11, 3))
        self.send(datagram(COORDINATES, BLUE, 11, 4, b"5,6,7,8"))
        self.assertEqual(self.received(BLUE), b"5,6,7,8")

    def test_silence_after_pings_is_logged_as_lan_fallback(self):
        self.ping(21, 1)
        time.sleep(3.6)
        self.assertIn("fallback to LAN", self.log())


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "requires the macOS Swift toolchain")
class P2PBridgeParentWatchTests(unittest.TestCase):
    def test_bridge_exits_when_the_launching_process_is_gone(self):
        with tempfile.TemporaryDirectory() as directory:
            binary = launcher.build(Path(directory))
            parent = subprocess.Popen(["/bin/sleep", "60"])
            bridge = subprocess.Popen([str(binary), "--no-bonjour", "--loopback-only",
                                       "--listen-port", str(free_udp_port()),
                                       "--exit-with-parent", str(parent.pid)],
                                      stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
            self.addCleanup(bridge.stdout.close)
            try:
                time.sleep(1.5)
                self.assertIsNone(bridge.poll(), "keeps running while the parent lives")
                parent.kill()
                parent.wait()
                self.assertEqual(bridge.wait(timeout=5), 0)
                self.assertIn("parent process", bridge.stdout.read())
            finally:
                if bridge.poll() is None:
                    bridge.kill()
                if parent.poll() is None:
                    parent.kill()


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "requires the macOS Swift toolchain")
class P2PBridgeListenerRestartTests(unittest.TestCase):
    def test_a_failed_listener_is_rebuilt_on_the_same_port_instead_of_exiting(self):
        with tempfile.TemporaryDirectory() as directory:
            binary = launcher.build(Path(directory))
            unity = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            unity.bind(("127.0.0.1", 0))
            unity.settimeout(1)
            self.addCleanup(unity.close)
            port = free_udp_port()
            log_path = Path(directory) / "bridge.log"
            with log_path.open("w") as log:
                bridge = subprocess.Popen(
                    [str(binary), "--no-bonjour", "--loopback-only", "--listen-port", str(port),
                     "--red-port", str(unity.getsockname()[1]), "--blue-port", str(free_udp_port()),
                     "--simulate-listener-failure-after", "1"], stdout=log, stderr=subprocess.STDOUT)
            self.addCleanup(lambda: bridge.poll() is None and (bridge.terminate(), bridge.wait(5)))
            phone = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            phone.settimeout(0.5)
            self.addCleanup(phone.close)
            deadline = time.monotonic() + 10
            answered = None
            while answered is None and time.monotonic() < deadline:
                phone.sendto(datagram(PING, RED, 5, 1), ("127.0.0.1", port))
                try:
                    answered = phone.recvfrom(64)[0]
                except socket.timeout:
                    pass
            self.assertEqual(answered, datagram(PONG, RED, 5, 1))
            # Generous: both waits end as soon as the event happens; 8 s was too short
            # while parallel verify runs loaded the machine (load average 50+).
            deadline = time.monotonic() + 30
            while "restarting in" not in log_path.read_text() and time.monotonic() < deadline:
                time.sleep(0.05)
            self.assertIn("restarting in 1s", log_path.read_text())
            self.assertIsNone(bridge.poll(), "the bridge process keeps running")
            # The accepted UDP connection can still pong after listener.cancel().
            # Wait for the new listener, rather than mistaking that pong for recovery.
            while log_path.read_text().count("listening on UDP %d" % port) < 2 and time.monotonic() < deadline:
                time.sleep(0.05)
            self.assertEqual(log_path.read_text().count("listening on UDP %d" % port), 2, log_path.read_text())
            answered = None
            while answered is None and time.monotonic() < deadline:
                phone.sendto(datagram(PING, RED, 5, 1), ("127.0.0.1", port))
                try:
                    answered = phone.recvfrom(64)[0]
                except socket.timeout:
                    pass
            self.assertEqual(answered, datagram(PONG, RED, 5, 1), log_path.read_text())
            phone.sendto(datagram(COORDINATES, RED, 5, 2, b"1,2,3,4"), ("127.0.0.1", port))
            self.assertEqual(unity.recvfrom(64)[0], b"1,2,3,4")
            self.assertEqual(log_path.read_text().count("listening on UDP %d" % port), 2)


def free_tcp_port() -> int:
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


@unittest.skipUnless(sys.platform == "darwin" and shutil.which("xcrun"), "requires the macOS Swift toolchain")
class DiagnosticsRelayTests(unittest.TestCase):
    """Triage uploads over the bridge's TCP relay reach the local receiver unchanged."""

    @classmethod
    def setUpClass(cls):
        cls.cache = tempfile.TemporaryDirectory(prefix="phonesaber-diag-relay-")
        cls.addClassCleanup(cls.cache.cleanup)
        cls.binary = launcher.build(Path(cls.cache.name))

    def start_bridge(self, receiver_port: int) -> int:
        relay_port = free_tcp_port()
        log = (Path(self.cache.name) / f"{self.id().rsplit('.', 1)[-1]}.log").open("w")
        self.addCleanup(log.close)
        bridge = subprocess.Popen([str(self.binary), "--no-bonjour", "--loopback-only",
                                   "--listen-port", str(free_udp_port()), "--red-port", str(free_udp_port()),
                                   "--blue-port", str(free_udp_port()), "--diag-port", str(receiver_port),
                                   "--diag-listen-port", str(relay_port)], stdout=log, stderr=subprocess.STDOUT)
        self.addCleanup(lambda: (bridge.terminate(), bridge.wait(5)))
        self.log_path = Path(log.name)
        deadline = time.monotonic() + 10
        while "diag relay: listening" not in self.log_path.read_text() and time.monotonic() < deadline:
            time.sleep(0.05)
        self.assertIn("diag relay: listening", self.log_path.read_text())
        return relay_port

    def test_an_http_upload_is_piped_to_the_receiver_and_the_reply_comes_back(self):
        from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
        import threading
        received = {}

        class Receiver(BaseHTTPRequestHandler):
            def do_POST(self):
                received["path"] = self.path
                received["body"] = self.rfile.read(int(self.headers["Content-Length"]))
                body = b'{"accepted": true}'
                self.send_response(201)
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

            def log_message(self, *args):
                pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Receiver)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        relay_port = self.start_bridge(server.server_address[1])
        payload = os.urandom(2 * 1024 * 1024)
        with socket.create_connection(("127.0.0.1", relay_port), timeout=10) as client:
            client.sendall(b"POST /v1/bundle HTTP/1.1\r\nHost: phonesaber\r\n"
                           b"Content-Type: application/vnd.phonesaber.triage-v1\r\n"
                           b"Content-Length: %d\r\nConnection: close\r\n\r\n" % len(payload) + payload)
            client.shutdown(socket.SHUT_WR)
            reply = b""
            while chunk := client.recv(65536):
                reply += chunk
        self.assertTrue(reply.startswith(b"HTTP/1.0 201") or reply.startswith(b"HTTP/1.1 201"), reply[:80])
        self.assertEqual(received["path"], "/v1/bundle")
        self.assertEqual(received["body"], payload, "bytes arrive unchanged")
        time.sleep(0.3)
        self.assertRegex(self.log_path.read_text(), r"closed after \d+ bytes \((done|receiver closed)")
        self.assertNotIn("not reachable", self.log_path.read_text())

    def test_a_missing_receiver_closes_the_upload_instead_of_hanging(self):
        relay_port = self.start_bridge(free_tcp_port())
        with socket.create_connection(("127.0.0.1", relay_port), timeout=10) as client:
            client.sendall(b"POST /v1/bundle HTTP/1.1\r\nContent-Length: 1\r\n\r\nx")
            try:
                closed = client.recv(1024) == b""
            except ConnectionResetError:
                closed = True
            self.assertTrue(closed, "connection is closed, the iPhone retries or uses LAN")
        self.assertIn("receiver not reachable", self.log_path.read_text())

    def test_a_stalled_receiver_applies_backpressure_instead_of_queueing_the_upload(self):
        server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
        server.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4096)
        server.bind(("127.0.0.1", 0))
        server.listen()
        server.settimeout(5)
        self.addCleanup(server.close)
        relay_port = self.start_bridge(server.getsockname()[1])
        payload = b"x" * (32 * 1024 * 1024)
        with socket.create_connection(("127.0.0.1", relay_port), timeout=10) as client:
            receiver, _ = server.accept()
            with receiver:
                finished = threading.Event()
                errors = []

                def upload():
                    try:
                        client.sendall(payload)
                        client.shutdown(socket.SHUT_WR)
                    except OSError as error:
                        errors.append(error)
                    finally:
                        finished.set()

                sender = threading.Thread(target=upload)
                sender.start()
                try:
                    queued_entire_upload = finished.wait(2)
                    receiver.settimeout(10)
                    received = 0
                    while chunk := receiver.recv(65536):
                        self.assertEqual(chunk, b"x" * len(chunk))
                        received += len(chunk)
                    receiver.sendall(b"accepted")
                    receiver.shutdown(socket.SHUT_WR)
                    reply = b""
                    while chunk := client.recv(64):
                        reply += chunk
                    self.assertEqual(received, len(payload))
                    self.assertEqual(reply, b"accepted")
                    self.assertFalse(errors, errors)
                    self.assertFalse(queued_entire_upload,
                                     "a stalled receiver must not accumulate the whole upload in the bridge")
                finally:
                    client.close()
                    sender.join(10)

    def test_concurrent_partial_uploads_and_large_replies_stay_separate(self):
        from concurrent.futures import ThreadPoolExecutor
        from socketserver import BaseRequestHandler, ThreadingTCPServer

        class Receiver(BaseRequestHandler):
            def handle(self):
                data = b""
                while chunk := self.request.recv(65536):
                    data += chunk
                self.request.sendall(data[::-1])

        server = ThreadingTCPServer(("127.0.0.1", 0), Receiver)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        relay_port = self.start_bridge(server.server_address[1])
        payloads = [os.urandom(1024 * 1024), os.urandom(1024 * 1024 + 17)]
        barrier = threading.Barrier(2)

        def upload(payload):
            with socket.create_connection(("127.0.0.1", relay_port), timeout=10) as client:
                client.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 4096)
                barrier.wait(timeout=5)
                for offset in range(0, len(payload), 4093):
                    client.sendall(payload[offset:offset + 4093])
                client.shutdown(socket.SHUT_WR)
                time.sleep(0.1)  # receiver can close while response bytes are queued
                reply = b""
                while chunk := client.recv(65536):
                    reply += chunk
                return reply

        with ThreadPoolExecutor(max_workers=2) as pool:
            replies = list(pool.map(upload, payloads))
        for reply, payload in zip(replies, payloads):
            self.assertEqual(len(reply), len(payload), self.log_path.read_text())
            self.assertTrue(reply == payload[::-1], "concurrent uploads cannot cross streams")


class P2PBridgeLauncherTests(unittest.TestCase):
    def test_compiler_tree_enumeration_uses_pid_counts_not_byte_counts(self):
        tree = {100: list(range(101, 109)), 101: [201, 202, 203]}

        def children(pid, buffer, size):
            pids = tree.get(pid, [])
            if buffer is None:
                return len(pids)
            self.assertEqual(size, launcher.ctypes.sizeof(buffer))
            for index, child in enumerate(pids[:len(buffer)]):
                buffer[index] = child
            return min(len(pids), len(buffer))

        libproc = mock.Mock()
        libproc.proc_listchildpids.side_effect = children
        with mock.patch.object(launcher.ctypes, "CDLL", return_value=libproc), \
                mock.patch.object(launcher.os, "kill") as kill:
            launcher._kill_compiler_tree(100)
        pids = [100, *tree[100], *tree[101]]
        self.assertEqual(kill.call_args_list,
                         [mock.call(pid, signal.SIGSTOP) for pid in pids]
                         + [mock.call(pid, signal.SIGKILL) for pid in reversed(pids)])

    def test_compiler_tree_enumeration_retries_a_full_buffer(self):
        capacities = []

        def children(pid, buffer, size):
            if pid != 100:
                return 0
            if buffer is None:
                return 2
            capacities.append(len(buffer))
            pids = [101, 102, 103, 104, 105]
            for index, child in enumerate(pids[:len(buffer)]):
                buffer[index] = child
            return min(len(pids), len(buffer))

        libproc = mock.Mock()
        libproc.proc_listchildpids.side_effect = children
        with mock.patch.object(launcher.ctypes, "CDLL", return_value=libproc), \
                mock.patch.object(launcher.os, "kill") as kill:
            launcher._kill_compiler_tree(100)
        self.assertEqual(capacities[0], 2)
        self.assertGreater(capacities[-1], 5)
        self.assertEqual([call.args[0] for call in kill.call_args_list
                          if call.args[1] == signal.SIGKILL], [105, 104, 103, 102, 101, 100])

    @unittest.skipUnless(sys.platform == "darwin", "requires macOS libproc")
    def test_interrupted_build_stops_compiler_descendants(self):
        for signum in (signal.SIGTERM, signal.SIGINT):
            with self.subTest(signal=signum), tempfile.TemporaryDirectory() as directory:
                root = Path(directory)
                ready = root / "frontend-ready"
                driver_pid = root / "driver-pid"
                frontend = root / "frontend.py"
                frontend.write_text(
                    "import os, pathlib, signal, socket, time\n"
                    "os.setpgid(0, 0)\n"
                    "signal.signal(signal.SIGTERM, signal.SIG_IGN)\n"
                    "signal.signal(signal.SIGINT, signal.SIG_IGN)\n"
                    "server = socket.socket()\n"
                    "server.bind(('127.0.0.1', 0))\n"
                    "server.listen()\n"
                    f"pathlib.Path({str(ready)!r}).write_text(f'{{os.getpid()}} {{server.getsockname()[1]}}')\n"
                    "client, _ = server.accept()\n"
                    "client.sendall(b'ready')\n"
                    "time.sleep(60)\n")
                # swiftc と同様に子を起動する実プロセスで、ドライバだけ終了する場合も再現する。
                driver = root / "driver.py"
                driver.write_text(
                    "import os, pathlib, subprocess, sys, time\n"
                    f"pathlib.Path({str(driver_pid)!r}).write_text(str(os.getpid()))\n"
                    "pathlib.Path(sys.argv[1]).write_bytes(b'partial compiler output')\n"
                    f"subprocess.Popen([sys.executable, {str(frontend)!r}])\n"
                    "time.sleep(60)\n")
                harness = root / "build.py"
                harness.write_text(
                    "import pathlib, subprocess, sys\n"
                    f"sys.path.insert(0, {str(Path(launcher.__file__).parent)!r})\n"
                    "import phone_saber_p2p_bridge as launcher\n"
                    "popen = subprocess.Popen\n"
                    "def compile(command, **kwargs):\n"
                    f"    return popen([sys.executable, {str(driver)!r}, command[-1]], **kwargs)\n"
                    "launcher.subprocess.Popen = compile\n"
                    f"launcher.build(pathlib.Path({str(root / 'cache')!r}))\n")
                process = subprocess.Popen([sys.executable, "-B", str(harness)],
                                           stdout=subprocess.PIPE, stderr=subprocess.PIPE)
                try:
                    deadline = time.monotonic() + 10
                    while not ready.exists() and process.poll() is None and time.monotonic() < deadline:
                        time.sleep(0.02)
                    self.assertTrue(ready.exists(), "compiler frontend did not start")
                    _, port = map(int, ready.read_text().split())
                    with socket.create_connection(("127.0.0.1", port), timeout=3) as client:
                        self.assertEqual(client.recv(5), b"ready")
                        process.send_signal(signum)
                        _, stderr = process.communicate(timeout=10)
                        self.assertEqual(process.returncode, 128 + signum, stderr)
                        try:
                            closed = client.recv(1) == b""
                        except ConnectionResetError:
                            closed = True
                        except socket.timeout:
                            closed = False
                        self.assertTrue(closed, "compiler frontend survived build cancellation")
                    self.assertEqual(list((root / "cache").rglob("*.tmp")), [])
                finally:
                    if process.poll() is None:
                        process.kill()
                    process.communicate(timeout=5)
                    for path in (ready, driver_pid):
                        if path.exists():
                            try:
                                os.kill(int(path.read_text().split()[0]), signal.SIGKILL)
                            except ProcessLookupError:
                                pass

    def test_changed_sources_during_build_are_not_published_under_the_old_digest(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "bridge.swift"
            source.write_text("old source")
            digest = launcher.source_digest
            revision = digest((source,))

            def compile_output(command, **kwargs):
                source.write_text("changed source")
                Path(command[-1]).write_bytes(b"compiled changed source")
                compiler = mock.Mock(returncode=0)
                compiler.communicate.return_value = ("", "")
                return compiler

            with mock.patch.object(launcher, "source_digest", side_effect=lambda: digest((source,))), \
                    mock.patch.object(launcher.subprocess, "Popen", side_effect=compile_output):
                with self.assertRaisesRegex(RuntimeError, "source.*changed"):
                    launcher.build(Path(directory))
            self.assertFalse((Path(directory) / revision / launcher.BINARY_NAME).exists())
            self.assertEqual(list(Path(directory).rglob("*.tmp")), [])

    def test_stale_temporary_of_a_live_build_is_not_removed(self):
        with tempfile.TemporaryDirectory() as directory:
            temporary = Path(directory) / f".{launcher.BINARY_NAME}.{os.getpid()}.tmp"
            temporary.write_bytes(b"compiler output")
            old = time.time() - 3600
            os.utime(temporary, (old, old))
            launcher._remove_stale_temporaries(Path(directory))
            self.assertTrue(temporary.exists(), "age alone does not mean the compiler is dead")

    def test_build_is_cached_per_source_revision(self):
        if not (sys.platform == "darwin" and shutil.which("xcrun")):
            self.skipTest("requires the macOS Swift toolchain")
        with tempfile.TemporaryDirectory() as directory:
            first = launcher.build(Path(directory))
            stamp = first.stat().st_mtime_ns
            second = launcher.build(Path(directory))
            self.assertEqual(first, second)
            self.assertEqual(second.stat().st_mtime_ns, stamp, "no rebuild for unchanged sources")
            self.assertEqual(first.parent.name, launcher.source_digest())
            self.assertTrue(os.access(first, os.X_OK))
            usage = subprocess.run([str(first), "--help"], capture_output=True, text=True)
            self.assertEqual(usage.returncode, 2)
            self.assertIn("--no-bonjour", usage.stderr)

    def test_concurrent_launchers_share_a_cache_in_a_japanese_space_path(self):
        if not (sys.platform == "darwin" and shutil.which("xcrun")):
            self.skipTest("requires the macOS Swift toolchain")
        with tempfile.TemporaryDirectory(prefix="縁日 P2P cache ") as directory:
            command = [sys.executable, "-B", launcher.__file__, "--build-only", "--cache-dir", directory]
            processes = [subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                          text=True) for _ in range(2)]
            try:
                for process in processes:
                    stdout, stderr = process.communicate(timeout=30)
                    self.assertEqual(process.returncode, 0, stderr)
                    binary = Path(stdout.splitlines()[-1])
                    self.assertTrue(os.access(binary, os.X_OK))
                    usage = subprocess.run([str(binary), "--help"], capture_output=True, text=True)
                    self.assertEqual(usage.returncode, 2, usage.stderr)
                self.assertEqual(list(Path(directory).rglob("*.tmp")), [])
            finally:
                for process in processes:
                    if process.poll() is None:
                        process.terminate()
                        process.communicate(timeout=10)

    def test_stale_partial_builds_are_removed_but_recent_ones_kept(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            finished = subprocess.Popen([sys.executable, "-c", "pass"])
            finished.wait(5)
            stale = folder / f".{launcher.BINARY_NAME}.{finished.pid}.tmp"
            fresh = folder / f".{launcher.BINARY_NAME}.{os.getpid()}.tmp"
            stale.write_bytes(b"x")
            fresh.write_bytes(b"x")
            old = time.time() - 3600
            os.utime(stale, (old, old))
            launcher._remove_stale_temporaries(folder)
            self.assertFalse(stale.exists())
            self.assertTrue(fresh.exists(), "a build in progress is not disturbed")

    def test_terminating_the_launcher_mid_build_stops_the_compiler(self):
        if not (sys.platform == "darwin" and shutil.which("xcrun")):
            self.skipTest("requires the macOS Swift toolchain")
        with tempfile.TemporaryDirectory() as directory:
            pid_file = Path(directory) / "compiler.pid"
            # Record our own child directly; pgrep may be unavailable in a sandbox.
            harness = """
import sys
from pathlib import Path
sys.path.insert(0, sys.argv[1])
import phone_saber_p2p_bridge as launcher
original = launcher.subprocess.Popen
def tracked_compiler(*args, **kwargs):
    compiler = original(*args, **kwargs)
    Path(sys.argv[3]).write_text(str(compiler.pid))
    return compiler
launcher.subprocess.Popen = tracked_compiler
launcher.main(['--build-only', '--rebuild', '--cache-dir', sys.argv[2]])
"""
            process = subprocess.Popen([sys.executable, "-B", "-c", harness,
                                        str(Path(launcher.__file__).parent), directory, str(pid_file)],
                                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
            self.addCleanup(process.stdout.close)
            self.addCleanup(lambda: process.poll() is None and (process.kill(), process.wait(5)))
            deadline = time.monotonic() + 10
            compiler = None
            while time.monotonic() < deadline and compiler is None:
                compiler = pid_file.read_text().strip() if pid_file.exists() else None
                time.sleep(0.05)
            self.assertIsNotNone(compiler, "compiler child started")
            process.terminate()
            self.assertEqual(process.wait(timeout=10), 128 + signal.SIGTERM)
            time.sleep(0.5)
            alive = subprocess.run(["/bin/kill", "-0", compiler], capture_output=True)
            self.assertNotEqual(alive.returncode, 0, "swiftc must not outlive the launcher")
            self.assertEqual(list(Path(directory).rglob("*.tmp")), [])

    def test_bridge_arguments_follow_a_double_dash(self):
        calls = {}

        def fake_exec(path, argv):
            calls["argv"] = argv
            raise SystemExit(0)

        original_build, original_exec = launcher.build, os.execv
        launcher.build = lambda cache_dir, force=False: Path("/bin/echo")
        os.execv = fake_exec
        try:
            with self.assertRaises(SystemExit):
                launcher.main(["--", "--name", "Saber Mac"])
        finally:
            launcher.build, os.execv = original_build, original_exec
        self.assertEqual(calls["argv"], ["/bin/echo", "--name", "Saber Mac"])


if __name__ == "__main__":
    unittest.main()
