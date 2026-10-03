#!/usr/bin/env python3
"""Mac P2P bridge: compile the real Swift bridge and drive it over loopback UDP.

Covers payload parsing, RED/BLUE routing to the local Unity ports, malformed
packets, duplicates/reordering, a new sender session (app restart), several
peers, a bridge restart, ping/pong liveness and log rate limiting. The actual
peer-to-peer Wi-Fi (AWDL) hop needs two devices; see P2P_BRIDGE.md.
"""
from __future__ import annotations

import os
import shutil
import signal
import socket
import struct
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

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
        self.start_bridge()
        self.addCleanup(self.stop_bridge)
        self.phone = self.new_socket()

    def new_socket(self) -> socket.socket:
        sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        sock.settimeout(0.5)
        self.addCleanup(sock.close)
        return sock

    def start_bridge(self):
        started_before = self.log().count("listening on UDP")
        log = self.log_path.open("a")
        self.addCleanup(log.close)
        self.process = subprocess.Popen(
            [str(self.binary), "--no-bonjour", "--loopback-only", "--listen-port", str(self.listen_port),
             "--red-port", str(self.unity[RED].getsockname()[1]),
             "--blue-port", str(self.unity[BLUE].getsockname()[1]), "--stats-interval", "0.5"],
            stdout=log, stderr=subprocess.STDOUT)
        deadline = time.monotonic() + 10
        while self.log().count("listening on UDP") == started_before and time.monotonic() < deadline:
            time.sleep(0.02)
        self.assertGreater(self.log().count("listening on UDP"), started_before)

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
            # Generous: both waits end as soon as the event happens; 8 s was too short
            # while parallel verify runs loaded the machine (load average 50+).
            deadline = time.monotonic() + 30
            while "restarting in" not in log_path.read_text() and time.monotonic() < deadline:
                time.sleep(0.05)
            self.assertIn("restarting in 1s", log_path.read_text())
            self.assertIsNone(bridge.poll(), "the bridge process keeps running")
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


class P2PBridgeLauncherTests(unittest.TestCase):
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

    def test_stale_partial_builds_are_removed_but_recent_ones_kept(self):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            stale = folder / f".{launcher.BINARY_NAME}.111.tmp"
            fresh = folder / f".{launcher.BINARY_NAME}.222.tmp"
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
            process = subprocess.Popen([sys.executable, "-B", launcher.__file__, "--build-only",
                                        "--rebuild", "--cache-dir", directory],
                                       stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
            deadline = time.monotonic() + 10
            compiler = None
            while time.monotonic() < deadline and compiler is None:
                found = subprocess.run(["/usr/bin/pgrep", "-P", str(process.pid)], capture_output=True, text=True)
                compiler = found.stdout.split()[0] if found.stdout.strip() else None
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
