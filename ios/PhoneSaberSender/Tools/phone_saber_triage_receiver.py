#!/usr/bin/env python3
"""Receive bounded PhoneSaber triage bundles on a trusted local network."""

from __future__ import annotations

import argparse
import ipaddress
import json
import queue
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from threading import Thread
from typing import Any

from phone_saber_session_log import log_fields, session_log_context

from phone_saber_triage_codex import (
    ANALYSIS_MODEL,
    ANALYSIS_REASONING_EFFORT,
    DEFAULT_MAX_IMAGES,
    CodexModelUnavailable,
    analyze_bundle,
)
from phone_saber_auto_repair import (
    RepairError,
    record_analysis_model_unavailable,
    repair_bundle,
)
from phone_saber_triage_protocol import (
    CONTENT_TYPE,
    MAX_BUNDLE_BYTES,
    MAX_IMAGES,
    BundleError,
    receive_bundle,
)


@dataclass(frozen=True)
class AnalysisJob:
    bundle: Path
    source: str


class TriageHTTPServer(ThreadingHTTPServer):
    daemon_threads = True
    allow_reuse_address = True

    def __init__(
        self,
        address: tuple[str, int],
        inbox: Path,
        *,
        analysis_mode: str = "disabled",
        max_images: int = DEFAULT_MAX_IMAGES,
        codex_path: str | None = None,
        repair_mode: str | None = None,
    ) -> None:
        super().__init__(address, TriageRequestHandler)
        self.inbox = inbox
        self.analysis_mode = analysis_mode
        self.max_images = max_images
        self.codex_path = codex_path
        self.repair_mode = repair_mode or (
            "automatic" if analysis_mode == "automatic" else "disabled")
        self.analysis_queue: queue.Queue[AnalysisJob] = queue.Queue(maxsize=4)
        if analysis_mode != "disabled":
            Thread(target=self._analysis_worker, name="phonesaber-codex-worker", daemon=True).start()

    def enqueue_analysis(self, bundle: Path, *, source: str = "manual_retry",
                         reason: str = "explicit_request") -> bool:
        if self.analysis_mode == "disabled":
            return False
        try:
            self.analysis_queue.put_nowait(AnalysisJob(bundle, source))
            return True
        except queue.Full:
            with session_log_context(bundle, source=source):
                print(f"[codex] {log_fields()} reason={reason} queue full; "
                      f"bundle preserved for manual analysis: {bundle}", flush=True)
            return False

    def _analysis_worker(self) -> None:
        while True:
            job = self.analysis_queue.get()
            bundle = job.bundle
            started = time.monotonic()
            with session_log_context(bundle, source=job.source):
                try:
                    self._process_analysis(bundle, started)
                finally:
                    self.analysis_queue.task_done()

    def _process_analysis(self, bundle: Path, started: float) -> None:
        try:
            if (bundle / "analysis_report.json").is_file():
                result = {"status": "existing_analysis", "bundle": str(bundle)}
            else:
                print(f"[AUTO_REPAIR][PRECHECK] {log_fields()} "
                      "elapsed=0.0s subprocess=none result=starting", flush=True)
                result = analyze_bundle(
                    bundle,
                    max_images=self.max_images,
                    codex_path=self.codex_path,
                    dry_run=self.analysis_mode == "dry-run",
                )
            print(f"[codex] {log_fields()} {result}", flush=True)
            subprocess_label = "codex read-only" if result["status"] == "completed" else "none"
            print(f"[AUTO_REPAIR][ANALYSIS] {log_fields()} elapsed={time.monotonic() - started:.1f}s "
                  f"subprocess={subprocess_label} result={result['status']}", flush=True)
            if self.repair_mode != "disabled" and result["status"] in {
                    "completed", "no_images", "existing_analysis"}:
                repair = repair_bundle(bundle, codex_path=self.codex_path,
                                       dry_run=self.repair_mode == "dry-run",
                                       max_images=self.max_images)
                print(f"[auto-repair] {log_fields()} {repair}", flush=True)
        except CodexModelUnavailable as exc:
            print(f"[AUTO_REPAIR][ANALYSIS] {log_fields()} elapsed={time.monotonic() - started:.1f}s "
                  f"subprocess=codex model={ANALYSIS_MODEL} effort={ANALYSIS_REASONING_EFFORT} "
                  f"result=MODEL_UNAVAILABLE {exc}", flush=True)
            try:
                result = record_analysis_model_unavailable(bundle, str(exc))
                print(f"[auto-repair] {log_fields()} {result}", flush=True)
            except Exception as report_error:
                print(f"[auto-repair] {log_fields()} could not persist MODEL_UNAVAILABLE status: {report_error}",
                      flush=True)
        except Exception as exc:
            print(f"[AUTO_REPAIR][ANALYSIS] {log_fields()} elapsed={time.monotonic() - started:.1f}s "
                  f"subprocess=codex read-only result=FAIL {exc}", flush=True)
            print(f"[codex] {log_fields()} analysis failed; bundle preserved: {exc}", flush=True)


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
        with session_log_context(bundle, source="new_upload"):
            print(f"[PHONE_SABER][SESSION] {log_fields()}", flush=True)
            print(f"[triage] received {bundle.name} {log_fields()} "
                  f"from {self.client_address[0]} → {bundle}", flush=True)
        self.server.enqueue_analysis(bundle, source="new_upload", reason="post_received")
        self._reply(201, {"accepted": True, "bundle": bundle.name})

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
    codex_group = parser.add_mutually_exclusive_group()
    codex_group.add_argument("--dry-run", action="store_true",
                             help="show the exact selected PNG/context input list; do not call Codex")
    codex_group.add_argument("--no-codex", action="store_true",
                             help="receive bundles only; do not start Codex analysis")
    parser.add_argument("--repair-dry-run", action="store_true",
                        help="run the repair gate, but do not edit source, commit, or push")
    parser.add_argument("--repair-bundle", type=Path,
                        help="evaluate one already received bundle and exit (use with --repair-dry-run)")
    parser.add_argument("--max-images", type=int, default=DEFAULT_MAX_IMAGES,
                        help=f"Codex image limit (default {DEFAULT_MAX_IMAGES}, hard max {MAX_IMAGES})")
    parser.add_argument("--codex-path", help="explicit Codex CLI executable; defaults to installed Codex")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv)
    if args.repair_bundle is not None:
        if not args.repair_dry_run:
            print("--repair-bundle requires --repair-dry-run", file=sys.stderr)
            return 2
        try:
            result = repair_bundle(args.repair_bundle, codex_path=args.codex_path,
                                   dry_run=True, max_images=args.max_images)
        except (BundleError, RepairError, OSError, ValueError) as exc:
            print(f"repair dry run failed: {exc}", file=sys.stderr)
            return 2
        print(json.dumps(result, ensure_ascii=False), flush=True)
        return 0 if result["status"] in {"needs_capture", "dry_run"} else 2
    if not 0 <= args.max_images <= MAX_IMAGES:
        print(f"--max-images must be between 0 and {MAX_IMAGES}", file=sys.stderr)
        return 2
    args.inbox.mkdir(parents=True, exist_ok=True)
    if args.inbox.is_symlink() or not args.inbox.is_dir():
        print(f"receiver inbox must be a real directory: {args.inbox}", file=sys.stderr)
        return 2
    try:
        mode = "dry-run" if args.dry_run else "disabled" if args.no_codex else "automatic"
        server = TriageHTTPServer((args.host, args.port), args.inbox.resolve(),
                                  analysis_mode=mode, max_images=args.max_images,
                                  codex_path=args.codex_path,
                                  repair_mode="dry-run" if args.repair_dry_run else
                                  "automatic" if mode == "automatic" else "disabled")
    except OSError as exc:
        print(f"cannot start PhoneSaber diagnostics receiver: {exc}", file=sys.stderr)
        return 2
    print("[PHONE_SABER][START]", flush=True)
    bonjour = None if args.no_bonjour else _publish_bonjour(server.server_port, args.service_name)
    print(f"[triage] listening on {args.host}:{server.server_port}; inbox={args.inbox}; codex={server.analysis_mode}; max-images={server.max_images}", flush=True)
    print("[PHONE_SABER][WAITING] source=new_upload", flush=True)
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
