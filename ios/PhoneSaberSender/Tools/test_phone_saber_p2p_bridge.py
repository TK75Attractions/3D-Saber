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
