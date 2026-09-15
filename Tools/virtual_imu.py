#!/usr/bin/env python3
"""Interactive/event-driven virtual IMU for the Unity UDP 9002 input."""

from __future__ import annotations

import argparse
import random
import socket
import sys
import termios
import threading
import time
import tty
from typing import Callable, Optional

DIRECTION_KEYS = {
    "l": "left",
    "r": "right",
    "u": "up",
    "d": "down",
}
DELAY_PRESETS_MS = (0.0, 10.0, 25.0, 50.0, 100.0)


class VirtualImu:
    def __init__(
        self,
        host: str = "127.0.0.1",
        port: int = 9002,
        strength: float = 0.8,
        delay_ms: float = 0.0,
        jitter_ms: float = 0.0,
        drop_rate: float = 0.0,
        *,
        sock: Optional[socket.socket] = None,
        sleep: Callable[[float], None] = time.sleep,
        uniform: Callable[[float, float], float] = random.uniform,
        random_value: Callable[[], float] = random.random,
    ) -> None:
        self.destination = (host, port)
        self.strength = max(0.0, min(1.0, strength))
        self.delay_ms = max(0.0, delay_ms)
        self.jitter_ms = max(0.0, jitter_ms)
        self.drop_rate = max(0.0, min(1.0, drop_rate))
        self.sequence = 0
        self.socket = sock or socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        self._owns_socket = sock is None
        self._sleep = sleep
        self._uniform = uniform
        self._random = random_value

    def cycle_delay(self) -> float:
        nearest = min(
            range(len(DELAY_PRESETS_MS)),
            key=lambda index: abs(DELAY_PRESETS_MS[index] - self.delay_ms),
        )
        self.delay_ms = DELAY_PRESETS_MS[(nearest + 1) % len(DELAY_PRESETS_MS)]
        return self.delay_ms

    def send_state(self, connected: bool) -> None:
        state = b"STATE:CONNECTED" if connected else b"STATE:DISCONNECTED"
        self.socket.sendto(state, self.destination)

    def send_swing(self, direction: str) -> bool:
        direction = direction.lower()
        if direction not in DIRECTION_KEYS.values():
            raise ValueError(f"unsupported direction: {direction}")

        sequence = self.sequence
        self.sequence = (self.sequence + 1) & 0xFFFF
        trigger_ns = time.monotonic_ns()
        xiao_timestamp_us = (trigger_ns // 1000) & 0xFFFFFFFF
        simulated_delay_ms = max(
            0.0,
            self.delay_ms + self._uniform(-self.jitter_ms, self.jitter_ms),
        )
        if simulated_delay_ms > 0.0:
            self._sleep(simulated_delay_ms / 1000.0)

        if self._random() < self.drop_rate:
            print(
                f"DROP  seq={sequence} dir={direction} "
                f"simulated-delay={simulated_delay_ms:.1f}ms"
            )
            return False

        # This optional fifth field is the same-Mac send timestamp used only to
        # measure localhost UDP transport. Real BLE bridge packets remain valid
        # with the original four fields.
        sender_monotonic_ns = time.monotonic_ns()
        packet = (
            f"SWING:{sequence},{direction},{self.strength:.3f},"
            f"{xiao_timestamp_us},{sender_monotonic_ns}"
        ).encode("ascii")
        self.socket.sendto(packet, self.destination)
        print(
            f"SEND  seq={sequence} dir={direction} strength={self.strength:.2f} "
            f"simulated-delay={simulated_delay_ms:.1f}ms"
        )
        return True

    def run_auto(self, interval: float, count: Optional[int] = None) -> None:
        sent = 0
        directions = ("left", "right")
        while count is None or sent < count:
            self.send_swing(directions[sent % len(directions)])
            sent += 1
            if count is None or sent < count:
                self._sleep(max(0.0, interval))

    def close(self) -> None:
        if self._owns_socket:
            self.socket.close()


def command_loop(host: str, port: int, stop: threading.Event) -> None:
    command_socket = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    try:
        command_socket.bind((host, port))
        command_socket.settimeout(0.25)
        while not stop.is_set():
            try:
                data, _ = command_socket.recvfrom(256)
            except socket.timeout:
                continue
            text = data.decode("utf-8", errors="ignore").strip()
            if text.startswith("H:1"):
                print("HAPTIC ON  (virtual)")
            elif text.startswith("H:0"):
                print("HAPTIC OFF (virtual)")
    except OSError as exc:
        print(f"Haptic command port unavailable ({exc}); swing sending continues.")
    finally:
        command_socket.close()


def read_single_key() -> str:
    if not sys.stdin.isatty():
        return sys.stdin.readline()[:1].lower()
    descriptor = sys.stdin.fileno()
    previous = termios.tcgetattr(descriptor)
    try:
        tty.setcbreak(descriptor)
        return sys.stdin.read(1).lower()
    finally:
        termios.tcsetattr(descriptor, termios.TCSADRAIN, previous)


def run_interactive(virtual_imu: VirtualImu) -> None:
    print("\nVirtual IMU ready")
    print("[L] Left  [R] Right  [U] Up  [D] Down")
    print("[T] Cycle delay 0/10/25/50/100 ms  [Q] Quit")
    while True:
        key = read_single_key()
        if key == "q" or key == "":
            print("\nQuit")
            return
        direction = DIRECTION_KEYS.get(key)
        if direction is not None:
            print()
            virtual_imu.send_swing(direction)
        elif key == "t":
            print(f"\nDelay is now {virtual_imu.cycle_delay():.0f} ms")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=9002)
    parser.add_argument("--command-port", type=int, default=9001)
    parser.add_argument("--strength", type=float, default=0.8)
    parser.add_argument("--auto", action="store_true", help="send left/right repeatedly")
    parser.add_argument("--interval", type=float, default=0.5, help="auto interval in seconds")
    parser.add_argument("--delay-ms", type=float, default=0.0)
    parser.add_argument("--jitter-ms", type=float, default=0.0, help="uniform +/- jitter")
    parser.add_argument("--drop-rate", type=float, default=0.0, help="0.0 to 1.0")
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    virtual_imu = VirtualImu(
        host=args.host,
        port=args.port,
        strength=args.strength,
        delay_ms=args.delay_ms,
        jitter_ms=args.jitter_ms,
        drop_rate=args.drop_rate,
    )
    stop = threading.Event()
    listener = threading.Thread(
        target=command_loop,
        args=(args.host, args.command_port, stop),
        daemon=True,
    )
    listener.start()
    virtual_imu.send_state(True)
    try:
        if args.auto:
            print(
                f"Virtual IMU auto left/right every {args.interval:.3f}s; "
                "Ctrl-C to stop"
            )
            virtual_imu.run_auto(args.interval)
        else:
            run_interactive(virtual_imu)
    except KeyboardInterrupt:
        print("\nStopped")
    finally:
        virtual_imu.send_state(False)
        stop.set()
        virtual_imu.close()


if __name__ == "__main__":
    main()
