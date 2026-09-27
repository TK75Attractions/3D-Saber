#!/usr/bin/env python3
"""Receive bounded PhoneSaber triage bundles on a trusted local network."""

from __future__ import annotations

import argparse
import ipaddress
import json
import shutil
import subprocess
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Any

from phone_saber_triage_protocol import (
    CONTENT_TYPE,
    MAX_BUNDLE_BYTES,
    BundleError,
    receive_bundle,
)


class TriageHTTPServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(self, address: tuple[str, int], inbox: Path) -> None:
        super().__init__(address, TriageRequestHandler)
        self.inbox = inbox


class TriageRequestHandler(BaseHTTPRequestHandler):
    server: TriageHTTPServer
    protocol_version = "HTTP/1.1"

    def do_GET(self) -> None:  # noqa: N802 - stdlib handler API
        if self.path != "/health":
            self._reply(404, {"error": "not found"})
            return
        self._reply(200, {"status": "ready", "service": "phonesaber-triage"})

    def do_POST(self) -> None:  # noqa: N802 - stdlib handler API
        if self.path != "/v1/bundle":
            self._reply(404, {"error": "not found"})
            return
        if not _is_local_peer(self.client_address[0]):
            self._reply(403, {"error": "local network peers only"})
            return
        if self.headers.get_content_type() != CONTENT_TYPE:
            self._reply(415, {"error": "unsupported content type"})
            return
        length_header = self.headers.get("Content-Length")
        try:
            length = int(length_header or "")
        except ValueError:
            self._reply(411, {"error": "Content-Length is required"})
            return
        if length < 8 or length > MAX_BUNDLE_BYTES:
            self._reply(413, {"error": "bundle exceeds receiver limits"})
            return
        try:
            bundle = receive_bundle(self.rfile, length, self.server.inbox)
        except FileExistsError as exc:
            self._reply(409, {"error": str(exc)})
            return
        except (BundleError, OSError) as exc:
            self._reply(400, {"error": str(exc)})
            return
        self._reply(201, {"accepted": True, "bundle": bundle.name})
        print(f"[triage] received {bundle.name} from {self.client_address[0]} → {bundle}", flush=True)

    def log_message(self, fmt: str, *args: Any) -> None:
        print(f"[http] {self.address_string()} {fmt % args}", flush=True)

    def _reply(self, status: int, payload: dict[str, Any]) -> None:
        body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        self.send_response(status)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Connection", "close")
        self.end_headers()
        self.wfile.write(body)
        self.close_connection = True


def _is_local_peer(value: str) -> bool:
    try:
        address = ipaddress.ip_address(value.split("%", 1)[0])
    except ValueError:
        return False
    carrier_grade = ipaddress.ip_network("100.64.0.0/10")
    return address.is_private or address.is_loopback or address.is_link_local \
        or (address.version == 4 and address in carrier_grade)


def _publish_bonjour(port: int, service_name: str) -> subprocess.Popen[bytes] | None:
    dns_sd = Path("/usr/bin/dns-sd")
    if sys.platform != "darwin" or not dns_sd.is_file():
        print("[bonjour] dns-sd unavailable; receiver remains reachable by address only", flush=True)
        return None
    command = [str(dns_sd), "-R", service_name, "_phonesaber-diag._tcp.", "local.", str(port)]
    try:
        process = subprocess.Popen(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except OSError as exc:
        print(f"[bonjour] publish failed: {exc}", flush=True)
        return None
    print(f"[bonjour] publishing {service_name} _phonesaber-diag._tcp local. {port}", flush=True)
    return process


def parse_args(argv: list[str] | None = None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="0.0.0.0", help="bind address (default: all local interfaces)")
    parser.add_argument("--port", type=int, default=8765, help="HTTP port (default: 8765)")
    parser.add_argument(
        "--inbox", type=Path,
        default=Path.home() / "Library" / "Application Support" / "PhoneSaber" / "diagnostics-inbox",
        help="destination outside the repository",
    )
    parser.add_argument("--service-name", default="Phone Saber Diagnostics")
    parser.add_argument("--no-bonjour", action="store_true", help="do not publish the Bonjour service")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    args.inbox.mkdir(parents=True, exist_ok=True)
    if args.inbox.is_symlink() or not args.inbox.is_dir():
        print(f"receiver inbox must be a real directory: {args.inbox}", file=sys.stderr)
        return 2
    try:
        server = TriageHTTPServer((args.host, args.port), args.inbox.resolve())
    except OSError as exc:
        print(f"cannot start PhoneSaber diagnostics receiver: {exc}", file=sys.stderr)
        return 2
    bonjour = None if args.no_bonjour else _publish_bonjour(server.server_port, args.service_name)
    print(f"[triage] listening on {args.host}:{server.server_port}; inbox={args.inbox}", flush=True)
    try:
        server.serve_forever(poll_interval=0.25)
    except KeyboardInterrupt:
        print("[triage] stopping", flush=True)
    finally:
        server.shutdown()
        server.server_close()
        if bonjour is not None:
            bonjour.terminate()
            try:
                bonjour.wait(timeout=2)
            except subprocess.TimeoutExpired:
                bonjour.kill()
        # Remove only abandoned staging directories; received bundles are preserved.
        for temporary in args.inbox.glob(".phonesaber-receive-*"):
            if temporary.is_dir() and not temporary.is_symlink():
                shutil.rmtree(temporary, ignore_errors=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
