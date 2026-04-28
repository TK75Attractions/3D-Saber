#!/usr/bin/env python3
import argparse
import asyncio
import socket
from dataclasses import dataclass
from typing import Optional

from bleak import BleakClient, BleakScanner

SERVICE_UUID = "6E400001-B5A3-F393-E0A9-E50E24DCCA9E"
RX_UUID = "6E400002-B5A3-F393-E0A9-E50E24DCCA9E"  # Unity -> ESP32
TX_UUID = "6E400003-B5A3-F393-E0A9-E50E24DCCA9E"  # ESP32 -> Unity


@dataclass
class BridgeConfig:
    host: str
    command_port: int
    data_port: int
    device_name: str
    device_address: Optional[str]


class CommandProtocol(asyncio.DatagramProtocol):
    def __init__(self, on_command):
        self.on_command = on_command

    def datagram_received(self, data: bytes, _addr):
        text = data.decode("utf-8", errors="ignore").strip()
        if text:
            self.on_command(text)


class BleUdpBridge:
    def __init__(self, config: BridgeConfig):
        self.config = config
        self.client: Optional[BleakClient] = None
        self.queue: asyncio.Queue[str] = asyncio.Queue()
        self.out_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.loop: Optional[asyncio.AbstractEventLoop] = None

    def send_udp(self, message: str) -> None:
        self.out_sock.sendto(message.encode("utf-8"), (self.config.host, self.config.data_port))

    def enqueue_command(self, message: str) -> None:
        if self.loop is None:
            return
        self.loop.call_soon_threadsafe(self.queue.put_nowait, message)

    async def resolve_device_address(self) -> str:
        if self.config.device_address:
            return self.config.device_address

        while True:
            print(f"[bridge] scanning for '{self.config.device_name}'")
            discovered = await BleakScanner.discover(timeout=4.0, return_adv=True)

            candidates = []
            for _addr, (dev, adv) in discovered.items():
                adv_name = (adv.local_name or "").strip()
                dev_name = (dev.name or "").strip()
                service_uuids = [u.lower() for u in (adv.service_uuids or [])]
                service_match = SERVICE_UUID.lower() in service_uuids

                if adv_name or dev_name:
                    candidates.append(f"{dev.address} | adv='{adv_name}' dev='{dev_name}'")

                if (
                    dev_name == self.config.device_name
                    or adv_name == self.config.device_name
                    or service_match
                ):
                    reason = "service uuid" if service_match else "name"
                    print(f"[bridge] found {dev.address} ({reason}) adv='{adv_name}' dev='{dev_name}'")
                    return dev.address

            if candidates:
                print("[bridge] visible devices:")
                for line in candidates[:8]:
                    print(f"  - {line}")
            await asyncio.sleep(1.0)

    async def run_command_loop(self) -> None:
        while True:
            cmd = await self.queue.get()
            if cmd == "PING":
                self.send_udp("STATE:CONNECTED" if self.client and self.client.is_connected else "STATE:DISCONNECTED")
                continue

            if not self.client or not self.client.is_connected:
                continue

            if cmd.startswith("H:"):
                state = cmd[2:].strip()
                if state.startswith("1"):
                    await self.client.write_gatt_char(RX_UUID, b"1\n", response=False)
                elif state.startswith("0"):
                    await self.client.write_gatt_char(RX_UUID, b"0\n", response=False)

    async def run(self) -> None:
        self.loop = asyncio.get_running_loop()

        transport, _ = await self.loop.create_datagram_endpoint(
            lambda: CommandProtocol(self.enqueue_command),
            local_addr=(self.config.host, self.config.command_port),
        )
        print(f"[bridge] command UDP {self.config.host}:{self.config.command_port}")
        print(f"[bridge] data UDP -> {self.config.host}:{self.config.data_port}")

        command_task = asyncio.create_task(self.run_command_loop())

        try:
            while True:
                address = await self.resolve_device_address()
                try:
                    print(f"[bridge] connecting {address}")
                    async with BleakClient(address) as client:
                        self.client = client
                        self.send_udp("STATE:CONNECTED")
                        print("[bridge] connected")

                        def on_notify(_handle: int, data: bytearray):
                            text = data.decode("utf-8", errors="ignore").strip()
                            if text:
                                self.send_udp(f"IMU:{text}")

                        await client.start_notify(TX_UUID, on_notify)

                        while client.is_connected:
                            await asyncio.sleep(0.3)
                except Exception as ex:
                    print(f"[bridge] error: {ex}")
                finally:
                    self.client = None
                    self.send_udp("STATE:DISCONNECTED")
                    print("[bridge] disconnected")
                    await asyncio.sleep(1.0)
        finally:
            command_task.cancel()
            transport.close()
            self.out_sock.close()


def parse_args() -> BridgeConfig:
    parser = argparse.ArgumentParser(description="Bridge ESP32 BLE <-> Unity UDP on macOS")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--command-port", type=int, default=9001)
    parser.add_argument("--data-port", type=int, default=9002)
    parser.add_argument("--device-name", default="XIAO-LSM6DSV16X")
    parser.add_argument("--device-address", default=None, help="Optional fixed BLE address/UUID")
    args = parser.parse_args()

    return BridgeConfig(
        host=args.host,
        command_port=args.command_port,
        data_port=args.data_port,
        device_name=args.device_name,
        device_address=args.device_address,
    )


def main() -> None:
    config = parse_args()
    bridge = BleUdpBridge(config)
    asyncio.run(bridge.run())


if __name__ == "__main__":
    main()
