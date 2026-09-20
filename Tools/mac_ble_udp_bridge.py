#!/usr/bin/env python3
"""Bridge one XIAO IMU BLE connection to Unity UDP without blocking Unity."""

from __future__ import annotations

import argparse
import asyncio
import socket
from dataclasses import dataclass
from typing import Any, Callable, Optional

SERVICE_UUID = "6E400001-B5A3-F393-E0A9-E50E24DCCA9E"
RX_UUID = "6E400002-B5A3-F393-E0A9-E50E24DCCA9E"
TX_UUID = "6E400003-B5A3-F393-E0A9-E50E24DCCA9E"


def load_bleak():
    """Import lazily so protocol/unit tests do not require BLE dependencies."""
    try:
        from bleak import BleakClient, BleakScanner
    except ImportError as exc:
        raise RuntimeError(
            "Bleak is not installed. Run Tools/setup_ble_bridge.command first."
        ) from exc
    return BleakClient, BleakScanner


@dataclass(frozen=True)
class BridgeConfig:
    host: str = "127.0.0.1"
    command_port: int = 9001
    data_port: int = 9002
    mirror_data_port: Optional[int] = None
    device_name: str = "XIAO-LSM6DSV16X"
    left_device_name: str = "XIAO-SABER-L"
    right_device_name: str = "XIAO-SABER-R"
    device_address: Optional[str] = None
    scan_seconds: float = 4.0
    retry_seconds: float = 1.0


class CommandProtocol(asyncio.DatagramProtocol):
    def __init__(self, on_command: Callable[[str], None]):
        self.on_command = on_command

    def datagram_received(self, data: bytes, _addr) -> None:
        text = data.decode("utf-8", errors="ignore").strip()
        if text:
            self.on_command(text)


class BleUdpBridge:
    def __init__(
        self,
        config: BridgeConfig,
        *,
        bleak_loader: Callable[[], tuple[Any, Any]] = load_bleak,
        out_sock: Optional[socket.socket] = None,
    ) -> None:
        self.config = config
        self.bleak_loader = bleak_loader
        self.out_sock = out_sock or socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self.client: Optional[Any] = None
        self.clients: dict[str, Any] = {}
        self.loop: Optional[asyncio.AbstractEventLoop] = None
        self.command_queue: asyncio.Queue[str] = asyncio.Queue()
        self.state = "SEARCHING"
        self.stopping = False

    def send_udp(self, message: str) -> None:
        payload = message.encode("utf-8")
        self.out_sock.sendto(payload, (self.config.host, self.config.data_port))
        if self.config.mirror_data_port is not None:
            self.out_sock.sendto(
                payload,
                (self.config.host, self.config.mirror_data_port),
            )

    def set_state(self, state: str) -> None:
        if self.state == state:
            return
        self.state = state
        self.send_udp(f"STATE:BLE:{state}:{self.config.device_name}")
        print(f"[IMU BLE] {state}: {self.config.device_name}", flush=True)

    def send_snapshot(self) -> None:
        self.send_udp("STATE:BRIDGE_READY")
        self.send_udp(f"STATE:BLE:{self.state}:{self.config.device_name}")

    def enqueue_command(self, command: str) -> None:
        if command == "PING":
            self.send_snapshot()
            return
        if self.loop is not None:
            self.loop.call_soon_threadsafe(self.command_queue.put_nowait, command)

    async def run_command_loop(self) -> None:
        while not self.stopping:
            command = await self.command_queue.get()
            clients = list(self.clients.values()) or ([self.client] if self.client is not None else [])
            for client in clients:
                if client is None or not client.is_connected:
                    continue
                if command.startswith("H:1"):
                    await client.write_gatt_char(RX_UUID, b"1\n", response=False)
                elif command.startswith("H:0"):
                    await client.write_gatt_char(RX_UUID, b"0\n", response=False)

    @staticmethod
    def _names(device: Any, advertisement: Any) -> tuple[str, str]:
        return (
            (getattr(device, "name", None) or "").strip(),
            (getattr(advertisement, "local_name", None) or "").strip(),
        )

    def find_device(self, discovered: dict) -> Optional[Any]:
        service_fallback = None
        for _key, value in discovered.items():
            device, advertisement = value
            device_name, advertised_name = self._names(device, advertisement)
            if self.config.device_name in (device_name, advertised_name):
                return device
            service_uuids = [
                uuid.lower()
                for uuid in (getattr(advertisement, "service_uuids", None) or [])
            ]
            if SERVICE_UUID.lower() in service_uuids and service_fallback is None:
                service_fallback = device
        return service_fallback

    def find_devices(self, discovered: dict) -> dict[str, Any]:
        """Resolve both physical sabers without assigning one device twice."""
        result: dict[str, Any] = {}
        entries = list(discovered.values())
        for side, wanted in (("LEFT", self.config.left_device_name), ("RIGHT", self.config.right_device_name)):
            for device, advertisement in entries:
                names = self._names(device, advertisement)
                if wanted in names:
                    result[side] = device
                    break
        used = {id(device) for device in result.values()}
        for side in ("LEFT", "RIGHT"):
            if side in result:
                continue
            for device, advertisement in entries:
                if id(device) in used:
                    continue
                uuids = [u.lower() for u in (getattr(advertisement, "service_uuids", None) or [])]
                if SERVICE_UUID.lower() in uuids:
                    result[side] = device
                    used.add(id(device))
                    break
        return result

    async def scan_once(self, scanner_type: Any) -> Optional[Any]:
        if self.config.device_address:
            return self.config.device_address
        # NOT_FOUND後の再試行ごとにSEARCHING/NOT_FOUNDを往復通知して
        # Consoleを埋めない。切断等の別状態から探索へ戻るときだけ通知する。
        if self.state not in ("SEARCHING", "NOT_FOUND"):
            self.set_state("SEARCHING")
        try:
            discovered = await asyncio.wait_for(
                scanner_type.discover(
                    timeout=self.config.scan_seconds,
                    return_adv=True,
                ),
                timeout=self.config.scan_seconds + 2.0,
            )
        except asyncio.CancelledError:
            raise
        except Exception as exc:
            print(f"[IMU BLE] Scan failed; retrying: {exc}", flush=True)
            return None
        return self.find_device(discovered)

    def notification_handler(self, side: str | int, _handle: int | bytearray, data: bytearray | None = None) -> None:
        # Firmware binary v1 is converted to the existing Unity text contract,
        # with a leading physical side field. Legacy text notifications remain valid.
        if data is None:
            data = _handle  # legacy direct test call: (handle, payload)
            side = "UNKNOWN"
        if len(data) >= 18 and data[0] == 0x53 and data[1] == 1 and data[2] == 1:
            sequence = int.from_bytes(data[4:6], "little")
            strength = int.from_bytes(data[6:8], "little")
            timestamp = int.from_bytes(data[8:12], "little")
            self.send_udp(f"SWING:{side},{sequence},unknown,{strength / 1000.0:.4f},{timestamp}")
            return
        text = bytes(data).decode("utf-8", errors="ignore").strip()
        if text:
            if text.startswith("SWING:"):
                self.send_udp(f"SWING:{side},{text[6:]}")
            else:
                self.send_udp(f"IMU:{text}")

    async def connect_once(self, side: str, target: Any, client_type: Any) -> None:
        self.set_side_state(side, "DEVICE_FOUND", getattr(target, "name", ""))
        self.set_side_state(side, "CONNECTING", getattr(target, "name", ""))
        try:
            async with client_type(target) as client:
                self.client = client
                self.clients[side] = client
                self.set_side_state(side, "CONNECTED", getattr(target, "name", ""))
                await client.start_notify(TX_UUID, lambda handle, data: self.notification_handler(side, handle, data))
                self.set_side_state(side, "NOTIFICATIONS_ACTIVE", getattr(target, "name", ""))
                while not self.stopping and client.is_connected:
                    await asyncio.sleep(0.25)
        finally:
            self.clients.pop(side, None)
            self.client = None

    def set_side_state(self, side: str, state: str, name: str) -> None:
        self.send_udp(f"STATE:BLE:{side}:{state}:{name}")
        print(f"[IMU BLE] {side} {state}: {name}", flush=True)

    async def run_side(self, side: str, client_type: Any, scanner_type: Any) -> None:
        wanted = self.config.left_device_name if side == "LEFT" else self.config.right_device_name
        while not self.stopping:
            self.set_side_state(side, "SEARCHING", wanted)
            try:
                discovered = await asyncio.wait_for(
                    scanner_type.discover(timeout=self.config.scan_seconds, return_adv=True),
                    timeout=self.config.scan_seconds + 2.0,
                )
                target = None
                for device, advertisement in discovered.values():
                    if wanted in self._names(device, advertisement):
                        target = device
                        break
                if target is None:
                    self.set_side_state(side, "NOT_FOUND", wanted)
                    await asyncio.sleep(self.config.retry_seconds)
                    continue
                try:
                    await self.connect_once(side, target, client_type)
                except asyncio.CancelledError:
                    raise
                except Exception as exc:
                    print(f"[IMU BLE] {side} connection failed; retrying: {exc}", flush=True)
                if not self.stopping:
                    self.set_side_state(side, "DISCONNECTED", wanted)
                    await asyncio.sleep(self.config.retry_seconds)
            except asyncio.CancelledError:
                raise
            except Exception as exc:
                print(f"[IMU BLE] {side} scan failed; retrying: {exc}", flush=True)
                await asyncio.sleep(self.config.retry_seconds)

    async def run(self) -> None:
        client_type, scanner_type = self.bleak_loader()
        self.loop = asyncio.get_running_loop()
        transport, _ = await self.loop.create_datagram_endpoint(
            lambda: CommandProtocol(self.enqueue_command),
            local_addr=(self.config.host, self.config.command_port),
        )
        command_task = asyncio.create_task(self.run_command_loop())
        print("[IMU BLE] Bridge ready", flush=True)
        self.send_snapshot()

        try:
            tasks = [asyncio.create_task(self.run_side(side, client_type, scanner_type))
                     for side in ("LEFT", "RIGHT")]
            await asyncio.gather(*tasks)
        finally:
            self.stopping = True
            command_task.cancel()
            await asyncio.gather(command_task, return_exceptions=True)
            transport.close()
            self.out_sock.close()


def parse_args(argv: Optional[list[str]] = None) -> BridgeConfig:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--command-port", type=int, default=9001)
    parser.add_argument("--data-port", type=int, default=9002)
    parser.add_argument("--mirror-data-port", type=int, default=None)
    parser.add_argument("--device-name", default="XIAO-LSM6DSV16X")
    parser.add_argument("--left-device-name", default="XIAO-SABER-L")
    parser.add_argument("--right-device-name", default="XIAO-SABER-R")
    parser.add_argument("--device-address", default=None)
    args = parser.parse_args(argv)
    return BridgeConfig(
        host=args.host,
        command_port=args.command_port,
        data_port=args.data_port,
        mirror_data_port=args.mirror_data_port,
        device_name=args.device_name,
        left_device_name=args.left_device_name,
        right_device_name=args.right_device_name,
        device_address=args.device_address,
    )


def main() -> None:
    bridge = BleUdpBridge(parse_args())
    try:
        asyncio.run(bridge.run())
    except KeyboardInterrupt:
        pass
    except RuntimeError as exc:
        print(f"[IMU BLE] {exc}", flush=True)
        raise SystemExit(2) from exc


if __name__ == "__main__":
    main()
