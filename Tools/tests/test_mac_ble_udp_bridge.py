import asyncio
import unittest
from unittest.mock import AsyncMock, Mock

from Tools.mac_ble_udp_bridge import (
    BleUdpBridge,
    BridgeConfig,
    RX_UUID,
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


class MacBleCommandLoopTests(unittest.IsolatedAsyncioTestCase):
    async def wait_for_delivery(self, worker, delivered):
        waiter = asyncio.create_task(delivered.wait())
        try:
            done, _ = await asyncio.wait({worker, waiter}, timeout=1.0,
                                         return_when=asyncio.FIRST_COMPLETED)
            self.assertFalse(worker.done(), "BLE write failure stopped the command consumer")
            self.assertIn(waiter, done, "Queued commands were not delivered")
        finally:
            waiter.cancel()
            await asyncio.gather(waiter, return_exceptions=True)

    async def test_successful_commands_keep_existing_delivery_paths(self):
        for mode in ("legacy", "dual", "disconnected"):
            with self.subTest(mode=mode):
                bridge = BleUdpBridge(BridgeConfig(), out_sock=FakeSocket())
                bridge.loop = asyncio.get_running_loop()
                delivered = asyncio.Event()

                async def write(_uuid, payload, *, response):
                    if payload == b"0\n":
                        delivered.set()

                left = Mock(is_connected=True, write_gatt_char=AsyncMock(side_effect=write))
                right = Mock(is_connected=mode == "dual", write_gatt_char=AsyncMock(side_effect=write))
                if mode == "legacy":
                    bridge.client = left
                else:
                    bridge.clients = {"LEFT": left, "RIGHT": right}
                worker = asyncio.create_task(bridge.run_command_loop())
                try:
                    bridge.enqueue_command("unknown")
                    bridge.enqueue_command("H:1")
                    bridge.enqueue_command("H:0")
                    await self.wait_for_delivery(worker, delivered)
                    expected = [((RX_UUID, b"1\n"), {"response": False}),
                                ((RX_UUID, b"0\n"), {"response": False})]
                    self.assertEqual(expected, left.write_gatt_char.await_args_list)
                    self.assertEqual(expected if mode == "dual" else [],
                                     right.write_gatt_char.await_args_list)
                finally:
                    worker.cancel()
                    await asyncio.gather(worker, return_exceptions=True)

    async def test_write_failure_does_not_accumulate_commands_after_reconnect(self):
        bridge = BleUdpBridge(BridgeConfig(), out_sock=FakeSocket())
        bridge.loop = asyncio.get_running_loop()
        failed = asyncio.Event()

        async def disconnect(*_args, **_kwargs):
            failed.set()
            raise OSError("disconnected during GATT write")

        bridge.clients["LEFT"] = Mock(is_connected=True, write_gatt_char=AsyncMock(side_effect=disconnect))
        worker = asyncio.create_task(bridge.run_command_loop())
        try:
            bridge.enqueue_command("H:1")
            await asyncio.wait_for(failed.wait(), timeout=1.0)
            # 再接続後もゲームが送り続けるコマンドが、未処理のまま蓄積しない。
            delivered = asyncio.Event()
            writes = []

            async def write(uuid, payload, *, response):
                writes.append((uuid, payload, response))
                if len(writes) == 512:
                    delivered.set()

            bridge.clients["LEFT"] = Mock(is_connected=True, write_gatt_char=AsyncMock(side_effect=write))
            for i in range(512):
                bridge.enqueue_command("H:1" if i % 2 == 0 else "H:0")
            await self.wait_for_delivery(worker, delivered)
            self.assertTrue(bridge.command_queue.empty())
            self.assertEqual([(RX_UUID, b"1\n" if i % 2 == 0 else b"0\n", False)
                              for i in range(512)], writes)
        finally:
            worker.cancel()
            await asyncio.gather(worker, return_exceptions=True)

    async def test_one_failed_saber_does_not_skip_the_other_saber(self):
        bridge = BleUdpBridge(BridgeConfig(), out_sock=FakeSocket())
        bridge.loop = asyncio.get_running_loop()
        delivered = asyncio.Event()

        async def write(*_args, **_kwargs):
            delivered.set()

        left = Mock(is_connected=True, write_gatt_char=AsyncMock(side_effect=RuntimeError("not connected")))
        right = Mock(is_connected=True, write_gatt_char=AsyncMock(side_effect=write))
        bridge.clients = {"LEFT": left, "RIGHT": right}
        worker = asyncio.create_task(bridge.run_command_loop())
        try:
            bridge.enqueue_command("H:0")
            await self.wait_for_delivery(worker, delivered)
            left.write_gatt_char.assert_awaited_once_with(RX_UUID, b"0\n", response=False)
            right.write_gatt_char.assert_awaited_once_with(RX_UUID, b"0\n", response=False)
        finally:
            worker.cancel()
            await asyncio.gather(worker, return_exceptions=True)

    async def test_cancellation_during_write_still_stops_the_consumer(self):
        bridge = BleUdpBridge(BridgeConfig(), out_sock=FakeSocket())
        bridge.loop = asyncio.get_running_loop()
        writing = asyncio.Event()

        async def write(*_args, **_kwargs):
            writing.set()
            await asyncio.Event().wait()

        bridge.client = Mock(is_connected=True, write_gatt_char=AsyncMock(side_effect=write))
        worker = asyncio.create_task(bridge.run_command_loop())
        try:
            bridge.enqueue_command("H:1")
            await asyncio.wait_for(writing.wait(), timeout=1.0)
            worker.cancel()
            with self.assertRaises(asyncio.CancelledError):
                await asyncio.wait_for(worker, timeout=1.0)
        finally:
            worker.cancel()
            await asyncio.gather(worker, return_exceptions=True)


if __name__ == "__main__":
    unittest.main()
