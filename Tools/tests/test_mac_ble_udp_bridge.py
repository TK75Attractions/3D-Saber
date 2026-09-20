import unittest

from Tools.mac_ble_udp_bridge import (
    BleUdpBridge,
    BridgeConfig,
    SERVICE_UUID,
)


class FakeSocket:
    def __init__(self):
        self.sent = []

    def sendto(self, payload, destination):
        self.sent.append((payload, destination))

    def close(self):
        pass


class FakeDevice:
    def __init__(self, name, address):
        self.name = name
        self.address = address


class FakeAdvertisement:
    def __init__(self, local_name="", service_uuids=None):
        self.local_name = local_name
        self.service_uuids = service_uuids or []


class MacBleUdpBridgeTests(unittest.TestCase):
    def test_exact_configured_name_wins_over_service_fallback(self):
        bridge = BleUdpBridge(BridgeConfig(), out_sock=FakeSocket())
        discovered = {
            "fallback": (
                FakeDevice("other", "fallback"),
                FakeAdvertisement(service_uuids=[SERVICE_UUID]),
            ),
            "exact": (
                FakeDevice("XIAO-LSM6DSV16X", "exact"),
                FakeAdvertisement(),
            ),
        }
        self.assertEqual("exact", bridge.find_device(discovered).address)

    def test_service_uuid_is_used_when_name_is_unavailable(self):
        bridge = BleUdpBridge(BridgeConfig(), out_sock=FakeSocket())
        discovered = {
            "fallback": (
                FakeDevice("", "fallback"),
                FakeAdvertisement(service_uuids=[SERVICE_UUID.lower()]),
            )
        }
        self.assertEqual("fallback", bridge.find_device(discovered).address)

    def test_left_and_right_names_are_resolved_independently(self):
        bridge = BleUdpBridge(BridgeConfig(), out_sock=FakeSocket())
        left = FakeDevice("XIAO-SABER-L", "left")
        right = FakeDevice("XIAO-SABER-R", "right")
        found = bridge.find_devices({"l": (left, FakeAdvertisement()), "r": (right, FakeAdvertisement())})
        self.assertEqual("left", found["LEFT"].address)
        self.assertEqual("right", found["RIGHT"].address)

    def test_notification_keeps_existing_imu_payload_contract(self):
        sock = FakeSocket()
        bridge = BleUdpBridge(BridgeConfig(), out_sock=sock)
        bridge.notification_handler(1, bytearray(b"1,2,3,4,5,6\n"))
        self.assertEqual(
            b"IMU:1,2,3,4,5,6",
            sock.sent[0][0],
        )

    def test_binary_firmware_notification_is_tagged_with_physical_side(self):
        sock = FakeSocket()
        bridge = BleUdpBridge(BridgeConfig(), out_sock=sock)
        packet = bytearray(18)
        packet[0:3] = bytes((0x53, 1, 1))
        packet[4:6] = (7).to_bytes(2, "little")
        packet[6:8] = (800).to_bytes(2, "little")
        packet[8:12] = (1234).to_bytes(4, "little")
        bridge.notification_handler("LEFT", 1, packet)
        self.assertEqual(b"SWING:LEFT,7,unknown,0.8000,1234", sock.sent[0][0])

    def test_status_is_sent_only_on_transition_and_ping_returns_snapshot(self):
        sock = FakeSocket()
        bridge = BleUdpBridge(BridgeConfig(), out_sock=sock)
        bridge.set_state("SEARCHING")
        self.assertEqual([], sock.sent)
        bridge.set_state("CONNECTED")
        bridge.set_state("CONNECTED")
        self.assertEqual(1, len(sock.sent))
        bridge.enqueue_command("PING")
        payloads = [payload.decode("utf-8") for payload, _ in sock.sent]
        self.assertIn("STATE:BRIDGE_READY", payloads)
        self.assertIn(
            "STATE:BLE:CONNECTED:XIAO-LSM6DSV16X",
            payloads,
        )


if __name__ == "__main__":
    unittest.main()
