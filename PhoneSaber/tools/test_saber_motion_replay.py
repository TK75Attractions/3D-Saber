import contextlib
import io
import json
import math
import socket
import tempfile
import threading
import unittest
from pathlib import Path
from unittest import mock

import saber_motion_replay as replay


class ExtractionTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.root = Path(self.tmp.name)
        self.bundle = self.root / "inbox" / "bundle"
        (self.bundle / "frames").mkdir(parents=True)

    def context(self, name, frames, **kwargs):
        path = self.bundle / "frames" / name
        path.write_text(json.dumps(dict(sessionID="session", frames=frames, **kwargs)))
        return path

    def frame(self, fid, t, endpoint=None, detected=True):
        return dict(frameID=fid, timestamp=t, red=dict(detected=detected, endpoint=endpoint), blue={})

    def test_overlap_sorted_scaled_unknown_color_and_read_only(self):
        f1 = self.frame(1, 10, [0, 0, 639, 479])
        f2 = self.frame(2, 10 + 1 / 30, detected=False)
        f3 = self.frame(10, 10.3, [320, 240, 500, 300])
        self.context("a.json", [f3, f2])
        self.context("b.json", [f1, f2])
        before = {p: p.read_bytes() for p in self.bundle.rglob("*.json")}
        track = replay.extract_bundle(self.bundle, (640, 480))
        self.assertEqual([f["frame_id"] for f in track["frames"]], [1, 2, 10])
        self.assertAlmostEqual(track["sample_period"], 1 / 30)
        self.assertEqual(track["frames"][0]["red"]["endpoint"], [0, 0, 1919, 1079])
        self.assertNotIn("blue", track["frames"][0])
        self.assertFalse(track["frames"][1]["red"]["detected"])
        self.assertEqual(before, {p: p.read_bytes() for p in before})

    def test_transmitted_endpoint_preferred_and_range_filter(self):
        self.context("a.json", [self.frame(1, 1, [4, 5, 6, 7]), self.frame(2, 2, [8, 9, 10, 11])],
                     udpTransmissions=[dict(frameID=2, color="red", state="sendStarted",
                                            coordinateSpace="configuredUDPOutputPixels", endpoint=[100, 200, 300, 400])])
        track = replay.extract_bundle(self.bundle, first_frame=2, last_frame=2)
        self.assertEqual(track["frames"][0]["red"]["endpoint"], [100, 200, 300, 400])
        self.assertEqual(track["frames"][0]["t"], 0)

    def test_source_size_required_without_transmission(self):
        self.context("a.json", [self.frame(1, 1, [4, 5, 6, 7])])
        with self.assertRaisesRegex(ValueError, "source pixels"):
            replay.extract_bundle(self.bundle)

    def test_scaling_matches_phone_including_mirroring_and_half_rounding(self):
        self.assertEqual(replay.scale_endpoint([0, 0, 639, 479], (640, 480), True, True),
                         [1919, 1079, 0, 0])
        self.assertEqual(replay.phone_round(2.5), 3)
        self.assertEqual(replay.phone_round(-2.5), -3)
        self.assertEqual(replay.scale_endpoint([320, 240, 500, 300], (640, 480)),
                         [961, 541, 1502, 676])

    def test_conflicting_overlap_rejected(self):
        self.context("a.json", [self.frame(1, 1, [0, 0, 1, 1])])
        self.context("b.json", [self.frame(1, 1, [0, 0, 2, 2])])
        with self.assertRaisesRegex(ValueError, "conflicting"):
            replay.extract_bundle(self.bundle, (640, 480))

    def test_nonmonotonic_and_mixed_session_rejected(self):
        self.context("a.json", [self.frame(1, 2, detected=False), self.frame(2, 1, detected=False)])
        with self.assertRaisesRegex(ValueError, "timestamps"):
            replay.extract_bundle(self.bundle)
        path = self.context("b.json", [])
        path.write_text(json.dumps(dict(sessionID="other", frames=[])))
        with self.assertRaisesRegex(ValueError, "multiple sessions"):
            replay.extract_bundle(self.bundle)

    def test_json_csv_roundtrip_and_no_overwrite(self):
        track = replay.synthetic_track("idle", duration=0.1)
        track["frames"][0]["blue"] = dict(detected=False, endpoint=None)
        del track["frames"][1]["blue"]
        for extension in ("json", "csv"):
            path = self.root / "output" / f"track.{extension}"
            replay.write_track(track, path, self.bundle, self.root / "inbox")
            self.assertEqual(replay.read_track(path), track)
            with self.assertRaises(FileExistsError):
                replay.write_track(track, path, self.bundle, self.root / "inbox")

    def test_inbox_input_and_symlink_write_guard(self):
        track = replay.synthetic_track("idle", duration=0.1)
        alias = self.root / "alias"
        alias.symlink_to(self.root / "inbox", target_is_directory=True)
        for target in (self.bundle / "track.json", self.root / "inbox" / "track.json", alias / "track.csv"):
            with self.assertRaisesRegex(ValueError, "outside"):
                replay.write_track(track, target, self.bundle, self.root / "inbox")
            self.assertFalse(target.exists())
        outside_inbox = self.root / "elsewhere" / "bundle"
        with self.assertRaisesRegex(ValueError, "outside"):
            replay.write_track(track, outside_inbox / "track.json", outside_inbox, self.root / "inbox")


class SyntheticTests(unittest.TestCase):
    def center(self, frame):
        p = frame["red"]["endpoint"]
        return (p[0] + p[2]) / 2, (p[1] + p[3]) / 2

    def test_all_patterns_stay_in_canonical_bounds_and_emit_both_colors(self):
        for pattern in replay.PATTERNS:
            track = replay.synthetic_track(pattern, 2, 60, 1.7)
            replay.validate_track(track)
            self.assertEqual(len(track["frames"]), 120)
            for frame in track["frames"]:
                for color in replay.COLORS:
                    self.assertTrue(frame[color]["detected"])
                    for i, value in enumerate(frame[color]["endpoint"]):
                        self.assertGreaterEqual(value, 0)
                        self.assertLessEqual(value, 1919 if i % 2 == 0 else 1079)

    def test_slash_directions_and_pixel_y_down(self):
        for pattern, axis, sign in (("slash-left", 0, -1), ("slash-right", 0, 1),
                                    ("slash-up", 1, -1), ("slash-down", 1, 1)):
            track = replay.synthetic_track(pattern, 1)
            centers = [self.center(f)[axis] for f in track["frames"]]
            self.assertTrue(all((b - a) * sign >= 0 for a, b in zip(centers, centers[1:])))
            self.assertGreater((centers[-1] - centers[0]) * sign, 500)

    def test_idle_thrust_figure_eight_and_speed(self):
        idle = replay.synthetic_track("idle", 1)
        self.assertEqual(len({tuple(f["red"]["endpoint"]) for f in idle["frames"]}), 1)
        thrust = replay.synthetic_track("thrust", 1)
        lengths = [math.dist(f["red"]["endpoint"][:2], f["red"]["endpoint"][2:]) for f in thrust["frames"]]
        self.assertGreater(max(lengths), min(lengths) * 4)
        eight = replay.synthetic_track("figure-eight", 1, 32)
        self.assertEqual(self.center(eight["frames"][0]), self.center(eight["frames"][16]))
        slow = replay.synthetic_track("slash-right", 1, 60, 1, "red")
        fast = replay.synthetic_track("slash-right", 1, 60, 2, "red")
        self.assertEqual(slow["frames"][30]["red"], fast["frames"][15]["red"])
        self.assertNotIn("blue", fast["frames"][0])

    def test_invalid_inputs(self):
        for kwargs in (dict(fps=0), dict(speed=-1), dict(duration=float("nan")), dict(color="green")):
            with self.assertRaises(ValueError):
                replay.synthetic_track("idle", **kwargs)


class SchedulingTests(unittest.TestCase):
    def setUp(self):
        self.track = replay.synthetic_track("idle", 1, 30)

    def test_default_cadence_color_and_timestamp_payload(self):
        packets = replay.schedule(self.track)
        self.assertEqual(len(packets), 60)
        self.assertEqual(packets[0].color, "red")
        self.assertEqual(packets[1].color, "blue")
        self.assertAlmostEqual(packets[2].due, 1 / 30)
        self.assertEqual(replay.payload(packets[0]), b"860,670,860,410")
        self.assertEqual(replay.payload(packets[0], 100), b"ts=100.000000;860,670,860,410")

    def test_missing_undetected_and_sparse_gaps_never_send_held_packets(self):
        track = dict(version=1, size=[1920, 1080], sample_period=0.1, duration=1,
                     frames=[dict(t=0, red=dict(detected=True, endpoint=[100, 100, 200, 200])),
                             dict(t=0.1, red=dict(detected=False, endpoint=[100, 100, 200, 200])),
                             dict(t=0.9, blue=dict(detected=True, endpoint=[300, 300, 400, 400]))])
        packets = replay.schedule(track, fps=10)
        self.assertEqual([(p.captured, p.color) for p in packets], [(0, "red"), (0.9, "blue")])

    def test_resample_speed_and_repeat(self):
        packets = replay.schedule(self.track, fps=60, speed=2, repeats=2, color="blue")
        self.assertEqual(len(packets), 60)
        self.assertTrue(all(p.color == "blue" for p in packets))
        self.assertAlmostEqual(packets[-1].captured, 59 / 60)

    def test_latency_and_jitter_bounds_reproducible_and_reordering(self):
        faults = replay.Impairments(latency_ms=50, jitter_ms=100, seed=10)
        packets = replay.schedule(self.track, impairments=faults)
        self.assertEqual(packets, replay.schedule(self.track, impairments=faults))
        self.assertNotEqual(packets, replay.schedule(self.track, impairments=replay.Impairments(50, 100, seed=11)))
        self.assertTrue(all(0 <= p.due - p.captured <= 0.15 + 1e-9 for p in packets))
        self.assertTrue(any(a.captured > b.captured for a, b in zip(packets, packets[1:])))
        delayed = replay.schedule(self.track, impairments=replay.Impairments(latency_ms=200))
        self.assertTrue(all(abs(p.due - p.captured - 0.2) < 1e-9 for p in delayed))

    def test_loss_and_dropout_bursts_use_arrival_time(self):
        self.assertEqual(replay.schedule(self.track, impairments=replay.Impairments(loss=1)), [])
        half = replay.schedule(self.track, impairments=replay.Impairments(loss=0.5, seed=6))
        self.assertEqual(half, replay.schedule(self.track, impairments=replay.Impairments(loss=0.5, seed=6)))
        self.assertTrue(10 < len(half) < 50)
        faults = replay.Impairments(latency_ms=100, dropouts=((0.2, 0.2), (0.7, 0.1)))
        packets = replay.schedule(self.track, fps=10, impairments=faults)
        self.assertTrue(all(not (0.2 <= p.due < 0.4 or 0.7 <= p.due < 0.8) for p in packets))
        self.assertEqual(len(packets), 14)

    def test_false_positive_exact_frame_count_far_away_and_deterministic(self):
        faults = replay.Impairments(false_at=0.2, false_frames=3, seed=7)
        baseline = replay.schedule(self.track)
        packets = replay.schedule(self.track, impairments=faults)
        changed = [p for p, b in zip(packets, baseline) if p.endpoint != b.endpoint]
        self.assertEqual(len(changed), 6)
        self.assertEqual([p.captured for p in changed[::2]], [0.2, 7 / 30, 8 / 30])
        for packet in changed:
            original = self.track["frames"][0][packet.color]["endpoint"]
            a = [(packet.endpoint[i] + packet.endpoint[i + 2]) / 2 for i in range(2)]
            b = [(original[i] + original[i + 2]) / 2 for i in range(2)]
            self.assertGreater(math.dist(a, b), 700)
        self.assertEqual(packets, replay.schedule(self.track, impairments=faults))

    def test_false_positives_during_no_detection(self):
        for frame in self.track["frames"]:
            frame["red"] = dict(detected=False, endpoint=None)
        packets = replay.schedule(self.track, color="red", impairments=replay.Impairments(false_frames=4))
        self.assertEqual(len(packets), 4)

    def test_invalid_impairments_and_track_timing(self):
        for faults in (replay.Impairments(loss=1.1), replay.Impairments(jitter_ms=-1),
                       replay.Impairments(dropouts=((0, 0),)), replay.Impairments(false_frames=-1)):
            with self.assertRaises(ValueError):
                replay.schedule(self.track, impairments=faults)
        self.track["frames"][1]["t"] = 0
        with self.assertRaises(ValueError):
            replay.schedule(self.track)

    def test_dry_run_never_opens_sockets_even_with_discovery(self):
        with mock.patch.object(replay.socket, "socket", side_effect=AssertionError("network forbidden")):
            with contextlib.redirect_stdout(io.StringIO()):
                self.assertEqual(replay.main(["play", "--synthetic", "idle", "--station", "A", "--dry-run"]), 0)


class NetworkTests(unittest.TestCase):
    def test_sender_routes_colors_and_uses_absolute_deadlines(self):
        packets = [replay.Packet(0.05, 0, "red", (10, 20, 30, 40)),
                   replay.Packet(0.08, 0.03, "blue", (50, 60, 70, 80))]
        with mock.patch.object(replay.socket, "socket") as factory, \
                mock.patch.object(replay.time, "monotonic", side_effect=[10, 10.01, 10.06]), \
                mock.patch.object(replay.time, "time", return_value=100), \
                mock.patch.object(replay.time, "sleep") as sleep:
            replay.send_packets(packets, "127.0.0.1", 12345, 12346, timestamp=True)
            sock = factory.return_value.__enter__.return_value
            self.assertEqual(sock.sendto.call_args_list, [
                mock.call(b"ts=100.000000;10,20,30,40", ("127.0.0.1", 12345)),
                mock.call(b"ts=100.030000;50,60,70,80", ("127.0.0.1", 12346))])
            self.assertAlmostEqual(sleep.call_args_list[0].args[0], 0.04)
            self.assertAlmostEqual(sleep.call_args_list[1].args[0], 0.02)

    def test_discovery_reply_validation(self):
        self.assertEqual(replay.parse_discovery(b"PHONESABER_UNITY 1 red=5005 blue=5006 name=PC station=A", "127.0.0.1"),
                         ("127.0.0.1", 5005, 5006, "A"))
        for data in (b"PHONESABER_UNITY 2 red=5005 blue=5006", b"PHONESABER_UNITY 1 red=0 blue=5006"):
            with self.assertRaises(ValueError):
                replay.parse_discovery(data, "127.0.0.1")

    def test_station_filter_ambiguity_and_timeout_with_mock_socket(self):
        replies = [(b"garbage", ("127.0.0.1", 1)),
                   (b"PHONESABER_UNITY 1 red=5005 blue=5006 station=B", ("127.0.0.1", 1)),
                   (b"PHONESABER_UNITY 1 red=5005 blue=5006 station=A", ("127.0.0.2", 1))]
        with mock.patch.object(replay.socket, "socket") as factory:
            sock = factory.return_value.__enter__.return_value
            sock.recvfrom.side_effect = replies + [socket.timeout()]
            self.assertEqual(replay.discover("A"), ("127.0.0.2", 5005, 5006, "A"))
            sock.recvfrom.side_effect = replies + [socket.timeout()]
            with self.assertRaisesRegex(ValueError, "2 matching"):
                replay.discover()
            sock.recvfrom.side_effect = [socket.timeout()]
            with self.assertRaisesRegex(ValueError, "0 matching"):
                replay.discover("A")

    def test_localhost_ephemeral_sender_and_discovery(self):
        # 実ネットワーク試験は localhost の OS 割当ポートのみ。
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as red, socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as blue:
            try:
                red.bind(("127.0.0.1", 0))
                blue.bind(("127.0.0.1", 0))
            except PermissionError as error:
                self.skipTest(f"sandbox disallows localhost UDP binding: {error}")
            red.settimeout(1)
            blue.settimeout(1)
            packets = replay.schedule(replay.synthetic_track("idle", 1 / 30))
            replay.send_packets(packets, "127.0.0.1", red.getsockname()[1], blue.getsockname()[1])
            self.assertEqual(red.recvfrom(1024)[0], replay.payload(packets[0]))
            self.assertEqual(blue.recvfrom(1024)[0], replay.payload(packets[1]))
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as responder:
            responder.bind(("127.0.0.1", 0))
            responder.settimeout(1)
            errors = []

            def answer():
                try:
                    data, remote = responder.recvfrom(1024)
                    self.assertEqual(data, b"PHONESABER_DISCOVER 1")
                    responder.sendto(b"PHONESABER_UNITY 1 red=5005 blue=5006 station=A", remote)
                except Exception as error:
                    errors.append(error)

            thread = threading.Thread(target=answer)
            thread.start()
            try:
                target = replay.discover("A", "127.0.0.1", responder.getsockname()[1], timeout=0.1, bind_address="127.0.0.1")
            finally:
                thread.join(2)
            self.assertFalse(thread.is_alive())
            self.assertEqual(errors, [])
            self.assertEqual(target, ("127.0.0.1", 5005, 5006, "A"))


if __name__ == "__main__":
    unittest.main()
