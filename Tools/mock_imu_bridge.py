#!/usr/bin/env python3
import argparse
import math
import socket
import threading
import time


def command_loop(sock: socket.socket, stop_event: threading.Event) -> None:
    while not stop_event.is_set():
        try:
            data, _ = sock.recvfrom(1024)
        except OSError:
            break
        msg = data.decode("utf-8", errors="ignore").strip()
        if not msg:
            continue

        if msg.startswith("H:"):
            cmd = msg[2:].strip()
            if cmd.startswith("1"):
                print("[mock] HAPTIC ON")
            elif cmd.startswith("0"):
                print("[mock] HAPTIC OFF")
            else:
                print(f"[mock] HAPTIC unknown: {cmd}")
        elif msg == "PING":
            print("[mock] PING")


def main() -> None:
    parser = argparse.ArgumentParser(description="Local UDP IMU mock for Unity.")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--command-port", type=int, default=9001)
    parser.add_argument("--data-port", type=int, default=9002)
    parser.add_argument("--hz", type=float, default=50.0)
    args = parser.parse_args()

    cmd_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    cmd_sock.bind((args.host, args.command_port))

    data_sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)

    stop_event = threading.Event()
    thread = threading.Thread(target=command_loop, args=(cmd_sock, stop_event), daemon=True)
    thread.start()

    print(f"[mock] command listen {args.host}:{args.command_port}")
    print(f"[mock] imu send -> {args.host}:{args.data_port} ({args.hz:.1f} Hz)")

    data_sock.sendto(b"STATE:CONNECTED", (args.host, args.data_port))

    dt = 1.0 / max(args.hz, 1.0)
    t = 0.0

    try:
        while True:
            ax = 0.15 * math.sin(t * 1.7)
            ay = 0.08 * math.cos(t * 1.2)
            az = 1.0 + 0.03 * math.sin(t * 2.1)
            gx = 40.0 * math.sin(t * 2.7)
            gy = 25.0 * math.cos(t * 1.8)
            gz = 15.0 * math.sin(t * 3.3)

            payload = f"IMU:{ax:.3f},{ay:.3f},{az:.3f},{gx:.3f},{gy:.3f},{gz:.3f}".encode("utf-8")
            data_sock.sendto(payload, (args.host, args.data_port))

            t += dt
            time.sleep(dt)
    except KeyboardInterrupt:
        pass
    finally:
        stop_event.set()
        try:
            data_sock.sendto(b"STATE:DISCONNECTED", (args.host, args.data_port))
        except OSError:
            pass
        cmd_sock.close()
        data_sock.close()


if __name__ == "__main__":
    main()
