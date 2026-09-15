import socket
import unittest

from Tools.virtual_imu import VirtualImu


class FakeSocket:
    def __init__(self):
        self.packets = []

    def sendto(self, data, destination):
        self.packets.append((data, destination))


class VirtualImuTests(unittest.TestCase):
    def test_directions_and_sequence_increase(self):
        sock = FakeSocket()
        imu = VirtualImu(sock=sock, random_value=lambda: 1.0)
        for direction in ("left", "right", "up", "down"):
            self.assertTrue(imu.send_swing(direction))
        payloads = [packet.decode("ascii").split(",") for packet, _ in sock.packets]
        self.assertEqual(["0", "1", "2", "3"], [item[0].split(":")[1] for item in payloads])
        self.assertEqual(["left", "right", "up", "down"], [item[1] for item in payloads])

    def test_delay_and_jitter_are_applied(self):
        sleeps = []
        imu = VirtualImu(
            sock=FakeSocket(),
            delay_ms=20,
            jitter_ms=5,
            sleep=sleeps.append,
            uniform=lambda _minimum, _maximum: 5,
            random_value=lambda: 1.0,
        )
        imu.send_swing("left")
        self.assertEqual([0.025], sleeps)

    def test_interactive_delay_presets_cycle(self):
        imu = VirtualImu(sock=FakeSocket())
        self.assertEqual(10.0, imu.cycle_delay())
        self.assertEqual(25.0, imu.cycle_delay())
        self.assertEqual(50.0, imu.cycle_delay())
        self.assertEqual(100.0, imu.cycle_delay())
        self.assertEqual(0.0, imu.cycle_delay())

    def test_drop_still_advances_sequence(self):
        sock = FakeSocket()
        random_values = iter((0.0, 1.0))
        imu = VirtualImu(sock=sock, drop_rate=0.5, random_value=lambda: next(random_values))
        self.assertFalse(imu.send_swing("left"))
        self.assertTrue(imu.send_swing("right"))
        self.assertTrue(sock.packets[0][0].startswith(b"SWING:1,right,"))

    def test_auto_mode_alternates_without_initial_unsolicited_send(self):
        sock = FakeSocket()
        sleeps = []
        imu = VirtualImu(sock=sock, sleep=sleeps.append, random_value=lambda: 1.0)
        self.assertEqual([], sock.packets)
        imu.run_auto(interval=0.5, count=4)
        directions = [packet.decode("ascii").split(",")[1] for packet, _ in sock.packets]
        self.assertEqual(["left", "right", "left", "right"], directions)
        self.assertEqual([0.5, 0.5, 0.5], sleeps)

    def test_udp_9002_compatible_receive(self):
        receiver = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        receiver.bind(("127.0.0.1", 0))
        receiver.settimeout(1.0)
        port = receiver.getsockname()[1]
        imu = VirtualImu(port=port, random_value=lambda: 1.0)
        try:
            imu.send_swing("up")
            data, _ = receiver.recvfrom(1024)
            self.assertTrue(data.startswith(b"SWING:0,up,0.800,"))
            self.assertEqual(5, len(data.decode("ascii").split(",")))
        finally:
            imu.close()
            receiver.close()

    def test_invalid_direction(self):
        imu = VirtualImu(sock=FakeSocket())
        with self.assertRaises(ValueError):
            imu.send_swing("diagonal")


if __name__ == "__main__":
    unittest.main()
