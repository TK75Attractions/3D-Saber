#!/usr/bin/env python3
"""Receive UDP coordinates and reconcile them with one saved HTML trial."""
from __future__ import annotations

import argparse
from collections import deque
import csv
from datetime import datetime
import json
import math
import select
import shutil
import socket
import statistics
import subprocess
import sys
import threading
import time
import webbrowser
import urllib.error
import urllib.request
from dataclasses import dataclass
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from typing import Callable
from urllib.parse import urlsplit

PORTS = (5005, 5006)
BONJOUR_SERVICE_TYPE = "_phonesaber._udp"
BONJOUR_SERVICE_NAME = "Phone Saber Mac"
LIVE_HISTORY_LIMIT = 100
RECONCILE_PACKET_LIMIT = 10_000
PACKET_RATE_WINDOW_SECONDS = 5.0
STALE_AFTER_SECONDS = 1.0
MAX_SAFE_PACKET_COUNT = 9_007_199_254_740_991


def local_ipv4_addresses() -> list[str]:
    """Return usable LAN IPv4 addresses without assuming a particular NIC."""
    addresses: set[str] = set()
    try:
        for _, _, _, _, sockaddr in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            address = sockaddr[0]
            if not address.startswith(("127.", "169.254.")):
                addresses.add(address)
    except OSError:
        pass
    return sorted(addresses)


class BonjourPublisher:
    """Publishes the existing red UDP listener via macOS dns-sd; no UDP proxy."""

    def __init__(self, port: int, service_name: str = BONJOUR_SERVICE_NAME):
        self._process = subprocess.Popen(
            ["/usr/bin/dns-sd", "-R", service_name, BONJOUR_SERVICE_TYPE, "local.", str(port)],
            stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
        )

    @property
    def running(self) -> bool:
        return self._process.poll() is None

    def close(self) -> None:
        if self.running:
            self._process.terminate()
            try:
                self._process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                self._process.kill()
                self._process.wait(timeout=2)

@dataclass(frozen=True)
class ParsedPacket:
    payload: str
    timestamp: float | None
    timestamp_valid: bool = True

@dataclass(frozen=True)
class DisplayMatch:
    frame_id: object
    display_epoch_ms: float
    coordinate_distance: float
    state_id: int
    trial_id: str

@dataclass(frozen=True)
class DisplayLog:
    frames: list[dict]
    started_epoch_ms: float
    ended_epoch_ms: float


class PacketRateWindow:
    """Fixed-size, one-second buckets for a five-second rolling packet rate."""

    def __init__(self, window_seconds: float = PACKET_RATE_WINDOW_SECONDS):
        self._window_seconds = max(1.0, float(window_seconds))
        self._buckets: dict[int, int] = {}
        self._started: float | None = None

    def record(self, observed_monotonic: float) -> None:
        if not math.isfinite(observed_monotonic):
            return
        if self._started is None:
            self._started = observed_monotonic
        bucket = math.floor(observed_monotonic)
        self._buckets[bucket] = min(MAX_SAFE_PACKET_COUNT, self._buckets.get(bucket, 0) + 1)
        self._prune(bucket)

    def rate(self, now_monotonic: float) -> float:
        if not math.isfinite(now_monotonic) or self._started is None:
            return 0.0
        bucket = math.floor(now_monotonic)
        self._prune(bucket)
        elapsed = max(1.0, min(self._window_seconds, now_monotonic - self._started))
        return sum(self._buckets.values()) / elapsed

    def _prune(self, current_bucket: int) -> None:
        oldest = current_bucket - math.ceil(self._window_seconds)
        for bucket in tuple(self._buckets):
            if bucket < oldest:
                del self._buckets[bucket]


class LatencyTest:
    """Mac-clock-only A/B display-to-UDP latency state machine.

    The browser only asks the dashboard to show A or B.  It never supplies a
    timestamp: both the display-switch acknowledgement and the matching UDP
    arrival are timestamped with ``clock`` on this Mac.  Coordinates are used
    solely to decide whether the iPhone has reached the left (A) or right (B)
    half of the deliberately widely-separated test display.
    """

    def __init__(self, results_directory: str | Path, clock: Callable[[], float] = time.monotonic,
                 timeout_seconds: float = 3.0, baseline_packets: int = 2,
                 input_width: float = 1920.0, input_height: float = 1080.0,
                 stale_guard_seconds: float = .020):
        self._results_directory = Path(results_directory)
        self._clock = clock
        self._timeout_seconds = timeout_seconds
        self._baseline_packets = baseline_packets
        self._input_width = input_width
        self._input_height = input_height
        self._stale_guard_seconds = stale_guard_seconds
        self.reset()

    def reset(self) -> None:
        self._status = "idle"
        self._trial_limit = 10
        self._results: list[dict] = []
        self._expected_state = "A"
        self._target_state: str | None = None
        self._displayed_state: str | None = None
        self._baseline_count = 0
        self._switch_monotonic: float | None = None
        self._saved_path: str | None = None
        self._save_error: str | None = None

    def start(self, trials: int) -> None:
        self.reset()
        self._trial_limit = max(1, min(int(trials), 100))
        self._status = "waiting_baseline"

    def stop(self) -> None:
        if self._status not in {"idle", "completed"}:
            self._status = "stopped"
        if self._results:
            self._save_results()

    def display_presented(self, state: object) -> bool:
        self._check_timeout()
        if state not in {"A", "B"}:
            return False
        self._displayed_state = state
        if self._status == "ready_to_switch" and state == self._target_state:
            self._switch_monotonic = self._clock()
            self._status = "awaiting_udp"
            return True
        return False

    def record_packet(self, payload: str, arrival_monotonic: float | None = None) -> None:
        self._check_timeout()
        observed = self._classify(payload)
        if observed is None:
            return
        now = self._clock() if arrival_monotonic is None else arrival_monotonic
        if self._status == "waiting_baseline":
            if observed == self._expected_state and self._displayed_state == observed:
                self._baseline_count += 1
                if self._baseline_count >= self._baseline_packets:
                    self._target_state = "B" if observed == "A" else "A"
                    self._status = "ready_to_switch"
            return
        if (self._status == "awaiting_udp" and observed == self._target_state and self._switch_monotonic is not None
                and now - self._switch_monotonic >= self._stale_guard_seconds):
            latency_ms = (now - self._switch_monotonic) * 1000
            if math.isfinite(latency_ms) and latency_ms >= 0:
                self._append_result(True, None, latency_ms, now)

    def _classify(self, payload: str) -> str | None:
        endpoints = parse_coordinate_endpoints(payload, self._input_width, self._input_height)
        if endpoints is None:
            return None
        midpoint = (endpoints[0][0] + endpoints[1][0]) / 2
        # The test bars are at 20% and 80%; the middle 20% is deliberately
        # ignored so a stale/intermediate coordinate cannot complete a trial.
        if midpoint < .4:
            return "A"
        if midpoint > .6:
            return "B"
        return None

    def _check_timeout(self) -> None:
        if self._status != "awaiting_udp" or self._switch_monotonic is None:
            return
        if self._clock() - self._switch_monotonic >= self._timeout_seconds:
            self._append_result(False, "timeout", None, None)

    def _append_result(self, success: bool, reason: str | None, latency_ms: float | None,
                       received_monotonic: float | None) -> None:
        self._results.append({
            "trial": len(self._results) + 1,
            "from": self._expected_state,
            "to": self._target_state,
            "latencyMs": latency_ms,
            "success": success,
            "reason": reason or "",
            "switchMonotonic": self._switch_monotonic,
            "receivedMonotonic": received_monotonic,
        })
        if len(self._results) >= self._trial_limit:
            self._status = "completed"
            self._save_results()
            return
        # The target remains on screen after a result/timeout.  Require two
        # fresh packets for it before requesting the next alternating switch.
        self._expected_state = self._target_state or self._expected_state
        self._target_state = None
        self._baseline_count = 0
        self._switch_monotonic = None
        self._status = "waiting_baseline"

    def _save_results(self) -> None:
        if self._saved_path is not None:
            return
        try:
            self._results_directory.mkdir(parents=True, exist_ok=True)
            path = self._results_directory / time.strftime("latency_%Y-%m-%d_%H%M%S.csv")
            suffix = 1
            while path.exists():
                path = self._results_directory / f"latency_{time.strftime('%Y-%m-%d_%H%M%S')}_{suffix}.csv"
                suffix += 1
            with path.open("w", newline="", encoding="utf-8") as handle:
                writer = csv.DictWriter(handle, fieldnames=("trial", "from", "to", "latency_ms", "success", "reason"))
                writer.writeheader()
                for result in self._results:
                    writer.writerow({"trial": result["trial"], "from": result["from"], "to": result["to"],
                                     "latency_ms": "" if result["latencyMs"] is None else f"{result['latencyMs']:.3f}",
                                     "success": result["success"], "reason": result["reason"]})
            self._saved_path = str(path)
        except OSError as error:
            self._save_error = str(error)

    def results_directory(self) -> Path:
        self._results_directory.mkdir(parents=True, exist_ok=True)
        return self._results_directory

    def snapshot(self) -> dict:
        self._check_timeout()
        successful = [result["latencyMs"] for result in self._results if result["success"]]
        stats = None if not successful else {
            "averageMs": statistics.mean(successful), "medianMs": statistics.median(successful),
            "minMs": min(successful), "maxMs": max(successful),
        }
        return {"status": self._status, "trialLimit": self._trial_limit, "results": [dict(item) for item in self._results],
                "expectedState": self._expected_state, "targetState": self._target_state,
                "displayedState": self._displayed_state, "baselinePackets": self._baseline_count,
                "timeoutSeconds": self._timeout_seconds, "stats": stats, "savedPath": self._saved_path,
                "saveError": self._save_error, "staleGuardMs": self._stale_guard_seconds * 1000}


class LiveState:
    """Small bounded, thread-safe state store for the local live dashboard."""

    def __init__(self, history_limit: int = LIVE_HISTORY_LIMIT, bonjour_publisher: BonjourPublisher | None = None,
                 latency_results_directory: str | Path = "latency_results", bound_ports: tuple[int, ...] = (),
                 port_by_color: dict[str, int] | None = None,
                 clock: Callable[[], float] = time.time,
                 monotonic_clock: Callable[[], float] = time.monotonic):
        self._lock = threading.Lock()
        self._history_limit = max(1, min(int(history_limit), LIVE_HISTORY_LIMIT))
        self._clock = clock
        self._monotonic_clock = monotonic_clock
        self._started = clock()
        self._started_monotonic = monotonic_clock()
        self._counts = {"red": 0, "blue": 0, "unknown": 0}
        self._latest = {"red": None, "blue": None}
        self._history: deque[dict] = deque(maxlen=self._history_limit)
        self._stopped = False
        self._bonjour_publisher = bonjour_publisher
        self._bound_ports = set(bound_ports)
        self._port_by_color = {"red": 5005, "blue": 5006}
        if port_by_color:
            self._port_by_color.update(port_by_color)
        self._last_received_epoch = {"red": None, "blue": None}
        self._last_received_monotonic = {"red": None, "blue": None}
        self._rates = {"red": PacketRateWindow(), "blue": PacketRateWindow()}
        self._latency = LatencyTest(latency_results_directory)

    def record(self, port: int, packet: ParsedPacket, arrival: float, order: int, color: str | None,
               arrival_monotonic: float | None = None) -> None:
        timestamp = packet.timestamp if packet.timestamp_valid and packet.timestamp is not None and math.isfinite(packet.timestamp) else None
        delta = arrival_minus_packet_timestamp_ms(arrival, timestamp)
        item = {
            "order": order,
            "port": port,
            "color": color or "unknown",
            "arrivalEpochSeconds": arrival if math.isfinite(arrival) else None,
            "receivedAt": _format_local_time(arrival),
            "phoneTimestampSeconds": timestamp,
            "arrivalMinusPhoneMs": delta if delta is not None and math.isfinite(delta) else None,
            "validTimestamp": timestamp is not None,
            "payload": packet.payload,
        }
        observed_monotonic = self._monotonic_clock() if arrival_monotonic is None else arrival_monotonic
        with self._lock:
            current_count = self._counts.get(item["color"], 0)
            self._counts[item["color"]] = min(MAX_SAFE_PACKET_COUNT, current_count + 1)
            if color in self._latest:
                self._latest[color] = item
                self._last_received_epoch[color] = arrival if math.isfinite(arrival) else None
                self._last_received_monotonic[color] = observed_monotonic if math.isfinite(observed_monotonic) else None
                self._rates[color].record(observed_monotonic)
            self._history.append(item)
            self._latency.record_packet(packet.payload, observed_monotonic)

    def set_bonjour_publisher(self, publisher: BonjourPublisher | None) -> None:
        with self._lock:
            self._bonjour_publisher = publisher

    def latency_start(self, trials: int) -> None:
        with self._lock:
            self._latency.start(trials)

    def latency_stop(self) -> None:
        with self._lock:
            self._latency.stop()

    def latency_reset(self) -> None:
        with self._lock:
            self._latency.reset()

    def latency_display_presented(self, state: object) -> bool:
        with self._lock:
            return self._latency.display_presented(state)

    def open_latency_results(self) -> None:
        with self._lock:
            path = self._latency.results_directory()
        subprocess.Popen(["open", str(path)])

    def snapshot(self) -> dict:
        with self._lock:
            now_epoch = self._clock()
            now_monotonic = self._monotonic_clock()
            ports = {
                "red": self._port_snapshot("red", self._port_by_color["red"], now_monotonic),
                "blue": self._port_snapshot("blue", self._port_by_color["blue"], now_monotonic),
            }
            publisher = self._bonjour_publisher
            bonjour_running = bool(publisher and publisher.running)
            bonjour_status = "publishing" if bonjour_running else ("failed" if publisher else "disabled")
            return {
                "listening": not self._stopped,
                "startedEpochSeconds": self._started,
                "counts": dict(self._counts),
                "latest": {key: value.copy() if value else None for key, value in self._latest.items()},
                "history": [item.copy() for item in self._history],
                "latency": self._latency.snapshot(),
                "network": {
                    "macName": socket.gethostname(),
                    "ipv4": local_ipv4_addresses(),
                    "ports": ports,
                    "staleAfterSeconds": STALE_AFTER_SECONDS,
                    "packetRateWindowSeconds": PACKET_RATE_WINDOW_SECONDS,
                    "bonjour": {"serviceName": BONJOUR_SERVICE_NAME, "serviceType": BONJOUR_SERVICE_TYPE,
                                "running": bonjour_running, "published": bonjour_running, "status": bonjour_status},
                    "unityNote": "Unityと同時にUDP 5005/5006をbindできません。診断時はUnityを停止してください。",
                },
            }

    def _port_snapshot(self, color: str, port: int, now_monotonic: float) -> dict:
        last_monotonic = self._last_received_monotonic[color]
        age = None if last_monotonic is None else max(0.0, now_monotonic - last_monotonic)
        requested = port in self._bound_ports
        return {
            "port": port,
            "bindStatus": "bound" if requested else "not-requested",
            "bound": requested,
            "lastPacketReceivedAt": _format_local_time(self._last_received_epoch[color]),
            "lastPacketReceivedEpochSeconds": self._last_received_epoch[color],
            "packetAgeSeconds": age,
            "packetRatePerSecond": self._rates[color].rate(now_monotonic),
            "packetCount": self._counts[color],
            "stale": age is None or age > STALE_AFTER_SECONDS,
            "status": "stale" if age is None or age > STALE_AFTER_SECONDS else "fresh",
        }

    def stop(self) -> None:
        with self._lock:
            self._stopped = True


class _LiveHandler(BaseHTTPRequestHandler):
    state: LiveState
    html_path: Path

    def log_message(self, _format, *_args):
        return

    def do_GET(self):
        path = urlsplit(self.path).path
        if path == "/api/status":
            body = json.dumps(self.state.snapshot(), ensure_ascii=False, allow_nan=False).encode("utf-8")
            self.send_response(200)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
        elif path in {"/", "/saber_camera_test.html"}:
            try:
                body = self.html_path.read_bytes()
            except OSError:
                self.send_error(404, "HTML not found")
                return
            self.send_response(200)
            self.send_header("Content-Type", "text/html; charset=utf-8")
            self.send_header("Cache-Control", "no-store")
        else:
            self.send_error(404, "Only the dashboard and status API are exposed")
            return
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_POST(self):
        path = urlsplit(self.path).path
        try:
            length = int(self.headers.get("Content-Length", "0"))
            if length < 0 or length > 4096:
                raise ValueError("invalid request length")
            raw = self.rfile.read(length) if length else b"{}"
            document = json.loads(raw.decode("utf-8"))
            if not isinstance(document, dict):
                raise ValueError("request body must be an object")
            if path == "/api/latency/start":
                self.state.latency_start(document.get("trials", 10))
                result = {"ok": True}
            elif path == "/api/latency/stop":
                self.state.latency_stop()
                result = {"ok": True}
            elif path == "/api/latency/reset":
                self.state.latency_reset()
                result = {"ok": True}
            elif path == "/api/latency/display":
                result = {"ok": True, "switchStarted": self.state.latency_display_presented(document.get("state"))}
            elif path == "/api/latency/open-results":
                self.state.open_latency_results()
                result = {"ok": True}
            else:
                self.send_error(404, "Unknown latency API")
                return
            body = json.dumps(result, ensure_ascii=False).encode("utf-8")
        except (ValueError, TypeError, json.JSONDecodeError) as error:
            body = json.dumps({"ok": False, "error": str(error)}, ensure_ascii=False).encode("utf-8")
            self.send_response(400)
        except OSError as error:
            body = json.dumps({"ok": False, "error": str(error)}, ensure_ascii=False).encode("utf-8")
            self.send_response(500)
        else:
            self.send_response(200)
        self.send_header("Content-Type", "application/json; charset=utf-8")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


class LiveServer:
    def __init__(self, host: str, port: int, html_path: Path, state: LiveState):
        handler = type("LiveHandler", (_LiveHandler,), {"state": state, "html_path": html_path})
        self.server = ThreadingHTTPServer((host, port), handler)
        self.thread = threading.Thread(target=self.server.serve_forever, name="saber-live-http", daemon=True)

    @property
    def address(self):
        return self.server.server_address

    def start(self):
        self.thread.start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=2)


def start_live_server(host: str = "127.0.0.1", port: int = 8765, html_path: str | Path = "saber_camera_test.html", bonjour_publisher: BonjourPublisher | None = None, bound_ports: tuple[int, ...] = (), port_by_color: dict[str, int] | None = None) -> tuple[LiveServer, LiveState]:
    dashboard_path = Path(html_path)
    state = LiveState(bonjour_publisher=bonjour_publisher, latency_results_directory=dashboard_path.parent / "latency_results", bound_ports=bound_ports, port_by_color=port_by_color)
    server = LiveServer(host, port, dashboard_path, state)
    server.start()
    return server, state


def verify_live_dashboard(server: LiveServer, html_path: str | Path) -> str:
    path = Path(html_path)
    if not path.is_file():
        raise RuntimeError(f"HTMLが存在しません: {path}")
    try:
        path.read_bytes()
    except OSError as error:
        raise RuntimeError(f"HTMLを読み取れません: {path} ({error})") from error
    host, port = server.address
    url = f"http://{host}:{port}/saber_camera_test.html?live=1"
    try:
        with urllib.request.urlopen(url, timeout=2) as response:
            if response.status != 200:
                raise RuntimeError(f"ライブHTTP応答が失敗しました: HTTP {response.status}")
            response.read()
    except (OSError, urllib.error.URLError) as error:
        raise RuntimeError(f"ライブHTTP応答を確認できません: {url} ({error})") from error
    return url

def parse_packet(data: bytes) -> ParsedPacket:
    text = data.decode("ascii", errors="replace").strip()
    for prefix in ("ts=", "timestamp="):
        if text.startswith(prefix):
            value, separator, payload = text[len(prefix):].partition(";")
            if separator:
                try:
                    timestamp = float(value)
                    return ParsedPacket(payload, timestamp, math.isfinite(timestamp))
                except (ValueError, OverflowError):
                    return ParsedPacket(text, None, False)
    return ParsedPacket(text, None, True)

def arrival_minus_packet_timestamp_ms(arrival: float, packet_timestamp: float | None) -> float | None:
    if packet_timestamp is None or not math.isfinite(arrival) or not math.isfinite(packet_timestamp): return None
    return (arrival - packet_timestamp) * 1000

def _format_local_time(epoch_seconds: float | None) -> str | None:
    if epoch_seconds is None or not math.isfinite(epoch_seconds):
        return None
    try:
        return datetime.fromtimestamp(epoch_seconds).astimezone().isoformat(timespec="milliseconds")
    except (OSError, OverflowError, ValueError):
        return None

def _valid_state_id(value: object) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0

def _valid_trial_id(value: object) -> bool:
    return isinstance(value, str) and bool(value.strip())

def _validate_frame(frame: dict, seen: set[tuple[str, str, int]]) -> None:
    trial_id, state_id, color = frame.get("trialId"), frame.get("stateId"), frame.get("color")
    if not _valid_trial_id(trial_id) or not _valid_state_id(state_id) or not isinstance(color, str) or color not in {"red", "blue"}:
        raise ValueError("display log stateId/trialId/color must be a unique typed value")
    key = (trial_id, color, state_id)
    if key in seen: raise ValueError(f"duplicate display state: {key}")
    seen.add(key)
    try: epoch = float(frame["displayEpochMs"])
    except (KeyError, TypeError, ValueError, OverflowError): raise ValueError("display log state must contain finite displayEpochMs") from None
    if not math.isfinite(epoch): raise ValueError("display log state must contain finite displayEpochMs")
    endpoints = frame.get("normalizedEndpoints")
    if not isinstance(endpoints, list) or len(endpoints) != 2:
        raise ValueError("display log state must contain two normalizedEndpoints")
    for point in endpoints:
        if not isinstance(point, dict) or set(point) != {"x", "y"}:
            raise ValueError("display log normalizedEndpoints have invalid structure")
        try: values = (float(point["x"]), float(point["y"]))
        except (TypeError, ValueError, OverflowError):
            raise ValueError("display log normalizedEndpoints must contain finite numbers") from None
        if not all(math.isfinite(value) and 0 <= value <= 1 for value in values):
            raise ValueError("display log normalizedEndpoints must contain finite normalized numbers")

def load_display_log(path: str | Path) -> DisplayLog:
    with Path(path).open(encoding="utf-8") as handle: document = json.load(handle)
    if not isinstance(document, dict): raise ValueError("display log must be an object")
    frames = document.get("states")
    if not isinstance(frames, list) or not frames or any(not isinstance(frame, dict) for frame in frames): raise ValueError("display log must contain a non-empty states array of objects")
    if not _valid_trial_id(document.get("trialId")): raise ValueError("display log must contain a trialId")
    seen: set[tuple[str, str, int]] = set()
    for frame in frames:
        if frame.get("trialId") != document["trialId"]: raise ValueError("display log contains a state from another trial")
        _validate_frame(frame, seen)
    epochs = [float(frame["displayEpochMs"]) for frame in frames]
    try: started, ended = float(document.get("trialStartedEpochMs", min(epochs))), float(document.get("trialEndedEpochMs", max(epochs)))
    except (TypeError, ValueError, OverflowError): raise ValueError("display log trial interval is invalid") from None
    if not math.isfinite(started) or not math.isfinite(ended) or started > ended or started > min(epochs) or ended < max(epochs): raise ValueError("display log trial interval does not contain its states")
    return DisplayLog(frames, started, ended)

def try_load_display_log(path: str | Path | None) -> tuple[DisplayLog | None, str | None]:
    if path is None: return None, None
    try: return load_display_log(path), None
    except (OSError, UnicodeError, ValueError, json.JSONDecodeError) as error: return None, f"表示ログを読み込めないため計測照合なしで続行します: {path} ({error})"

def parse_coordinate_endpoints(payload: str, width: float, height: float):
    if not math.isfinite(width) or not math.isfinite(height) or width <= 0 or height <= 0: return None
    parts = payload.split(",")
    if len(parts) != 4: return None
    try: values = [float(part) for part in parts]
    except (ValueError, OverflowError): return None
    if not all(math.isfinite(value) for value in values): return None
    return ((values[0] / width, values[1] / height), (values[2] / width, values[3] / height))

def _display_endpoints(frame: dict, color: str):
    if frame.get("color") != color: return None
    normalized = frame.get("normalizedEndpoints")
    if not isinstance(normalized, list) or len(normalized) != 2: return None
    try: points = tuple((float(point["x"]), float(point["y"])) for point in normalized)
    except (KeyError, TypeError, ValueError, OverflowError): return None
    return points if all(math.isfinite(value) for point in points for value in point) else None

def _endpoint_distance(first, second) -> float:
    def distance(a, b): return math.hypot(a[0] - b[0], a[1] - b[1])
    return min(distance(first[0], second[0]) + distance(first[1], second[1]), distance(first[0], second[1]) + distance(first[1], second[0])) / 2

def nearest_display_frame(frames, payload, color, input_width, input_height, reject_threshold=math.inf, trial_id=None):
    observed = parse_coordinate_endpoints(payload, input_width, input_height)
    if observed is None or (not math.isfinite(reject_threshold) and reject_threshold != math.inf) or reject_threshold < 0: return None
    matches = []
    for frame in frames:
        if not isinstance(frame, dict) or (trial_id is not None and frame.get("trialId") != trial_id): continue
        expected = _display_endpoints(frame, color)
        if expected is None or not _valid_state_id(frame.get("stateId")) or not _valid_trial_id(frame.get("trialId")): continue
        try: epoch, distance = float(frame["displayEpochMs"]), _endpoint_distance(observed, expected)
        except (KeyError, TypeError, ValueError, OverflowError): continue
        if math.isfinite(epoch) and math.isfinite(distance): matches.append(DisplayMatch(frame.get("frameId", frame["stateId"]), epoch, distance, frame["stateId"], frame["trialId"]))
    matches.sort(key=lambda item: item.coordinate_distance)
    if not matches or (matches[0].coordinate_distance > reject_threshold and not math.isclose(matches[0].coordinate_distance, reject_threshold, rel_tol=0, abs_tol=1e-12)): return None
    if len(matches) > 1 and math.isclose(matches[0].coordinate_distance, matches[1].coordinate_distance, rel_tol=0, abs_tol=1e-12): return None
    return matches[0]

def _percentile(values, q):
    if not values: return None
    ordered = sorted(values); position = (len(ordered) - 1) * q; low, high = math.floor(position), math.ceil(position)
    return ordered[low] if low == high else ordered[low] + (ordered[high] - ordered[low]) * (position - low)

def _print_stats(samples, label):
    values = [value for value in samples.get(label, []) if math.isfinite(value)]
    if not values: print(f"statistics color={label} count=0 average_ms=- median_ms=- p50_ms=- p95_ms=-", flush=True); return
    print(f"statistics color={label} count={len(values)} average_ms={statistics.mean(values):.3f} median_ms={statistics.median(values):.3f} p50_ms={_percentile(values, .50):.3f} p95_ms={_percentile(values, .95):.3f}", flush=True)

def _diagnostic_summary_lines(ports, bound_ports, packet_stats, now_monotonic, bonjour_publisher):
    def bind_status(port):
        if port in bound_ports:
            return "BOUND"
        return "FAILED" if port in ports else "NOT REQUESTED"

    def packet_status(color):
        stats = packet_stats[color]
        last = stats["lastMonotonic"]
        age = None if last is None else max(0.0, now_monotonic - last)
        freshness = "STALE" if age is None or age > STALE_AFTER_SECONDS else "FRESH"
        age_text = "never" if age is None else f"{age:.1f}s"
        rate = stats["rate"].rate(now_monotonic)
        return f"{freshness} age={age_text} rate={rate:.1f}pps"

    if bonjour_publisher is None:
        bonjour_status = "DISABLED"
    else:
        bonjour_status = "PUBLISHING" if bonjour_publisher.running else "FAILED"
    return (
        f"UDP 5005: {bind_status(5005)} | UDP 5006: {bind_status(5006)} | Bonjour: {bonjour_status}",
        f"RED {packet_status('red')} | BLUE {packet_status('blue')}",
    )

def _write_console_summary(lines, interactive: bool, redraw: bool = False, finish: bool = False) -> None:
    if not interactive:
        print(*lines, sep="\n", flush=True)
        return
    width = max(40, shutil.get_terminal_size(fallback=(100, 24)).columns - 1)
    first, second = (line[:width] for line in lines)
    if redraw:
        sys.stdout.write("\033[1A")
    sys.stdout.write("\r\033[2K" + first + "\n\r\033[2K" + second)
    if finish:
        sys.stdout.write("\n")
    sys.stdout.flush()

def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="0.0.0.0"); parser.add_argument("--duration", type=float, default=0.0); parser.add_argument("--port", type=int, action="append", dest="ports"); parser.add_argument("--display-log", type=Path); parser.add_argument("--trial-id"); parser.add_argument("--reject-threshold", type=float, default=0.08); parser.add_argument("--input-width", type=float, default=1920.0); parser.add_argument("--input-height", type=float, default=1080.0)
    parser.add_argument("--live", action="store_true", help="loopback HTTP dashboard and live UDP status API")
    parser.add_argument("--http-host", default="127.0.0.1"); parser.add_argument("--http-port", type=int, default=8765)
    parser.add_argument("--html", type=Path, default=Path(__file__).with_name("saber_camera_test.html"))
    parser.add_argument("--open-browser", action="store_true", help="open the live dashboard after both listeners are ready")
    parser.add_argument("--bonjour", action="store_true", help="publish _phonesaber._udp via macOS dns-sd")
    parser.add_argument("--verbose", action="store_true", help="print every raw packet to Terminal")
    return parser.parse_args()

def run(host: str, ports: tuple[int, ...], duration: float, display_log=None, input_width=1920.0, input_height=1080.0, on_ready: Callable[[], None] | None = None, reject_threshold=0.08, trial_id=None, port_colors: dict[int, str] | None = None, on_packet: Callable[[int, ParsedPacket, float], None] | None = None, live=False, http_host="127.0.0.1", http_port=8765, html_path: str | Path = "saber_camera_test.html", open_browser=False, bonjour=False, verbose=False) -> int:
    if not math.isfinite(duration) or duration < 0 or not math.isfinite(input_width) or not math.isfinite(input_height) or input_width <= 0 or input_height <= 0 or not math.isfinite(reject_threshold) or reject_threshold < 0:
        print("拒否閾値・期間・入力寸法は有限かつ非負（寸法は正）で指定してください", flush=True); return 2
    if live and http_host not in {"127.0.0.1", "localhost"}:
        print("ライブHTTPはIPv4 loopback（127.0.0.1/localhost）に限定してください", flush=True); return 2
    sockets = []
    live_requested = live
    packets = deque(maxlen=RECONCILE_PACKET_LIMIT) if display_log is not None else None
    packets_dropped = 0
    packet_order = 0
    color_counts: dict[str, int] = {}
    packet_stats = {
        "red": {"lastMonotonic": None, "rate": PacketRateWindow()},
        "blue": {"lastMonotonic": None, "rate": PacketRateWindow()},
    }
    bound_ports: set[int] = set()
    live_server = None
    live_state = None
    bonjour_publisher = None
    colors = port_colors or {5005: "red", 5006: "blue"}
    binding_port = None
    completed = False
    stopped_by_user = False
    summary_rendered = False
    terminal_summary = sys.stdout.isatty() and not verbose
    try:
        for port in ports:
            binding_port = port
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            try: sock.bind((host, port))
            except OSError: sock.close(); raise
            sock.setblocking(False); sockets.append((port, sock)); bound_ports.add(port)
        binding_port = None
        by_socket = {sock: port for port, sock in sockets}; deadline = time.monotonic() + duration if duration > 0 else None
        if bonjour:
            bonjour_publisher = BonjourPublisher(5005)
            if not bonjour_publisher.running:
                raise RuntimeError("Bonjourサービスを開始できません")
        if live:
            port_by_color = {color: port for port, color in colors.items() if color in {"red", "blue"}}
            live_server, live_state = start_live_server(http_host, http_port, html_path, bonjour_publisher,
                                                        tuple(bound_ports), port_by_color)
            dashboard_url = verify_live_dashboard(live_server, html_path)
            print(f"live dashboard ready: {dashboard_url}", flush=True)
        print(f"listening on {host}: {', '.join(map(str, ports))}; Ctrl-C to stop", flush=True)
        if on_ready: on_ready()
        _write_console_summary(
            _diagnostic_summary_lines(ports, bound_ports, packet_stats, time.monotonic(), bonjour_publisher),
            terminal_summary,
        )
        summary_rendered = True
        next_summary = time.monotonic() + 1.0
        if live and open_browser:
            try:
                opened = webbrowser.open(dashboard_url)
            except Exception as error:
                print(f"ブラウザを開けません: {error}", flush=True)
                return 2
            if not opened:
                print(f"ブラウザを開けません（webbrowser.openがFalse）: {dashboard_url}", flush=True)
                return 2
        while deadline is None or time.monotonic() < deadline:
            now_monotonic = time.monotonic()
            timeout = .5 if deadline is None else max(0, min(.5, deadline - now_monotonic))
            ready, _, _ = select.select(list(by_socket), [], [], timeout)
            for sock in ready:
                data, address = sock.recvfrom(4096)
                port = by_socket[sock]
                arrival = time.time()
                arrival_monotonic = time.monotonic()
                packet = parse_packet(data)
                packet_order = min(MAX_SAFE_PACKET_COUNT, packet_order + 1)
                order = packet_order
                if packets is not None:
                    if len(packets) == packets.maxlen:
                        packets_dropped = min(MAX_SAFE_PACKET_COUNT, packets_dropped + 1)
                    packets.append((port, packet, arrival, order, address))
                color = colors.get(port, "unknown")
                color_counts[color] = min(MAX_SAFE_PACKET_COUNT, color_counts.get(color, 0) + 1)
                if color in packet_stats:
                    packet_stats[color]["lastMonotonic"] = arrival_monotonic
                    packet_stats[color]["rate"].record(arrival_monotonic)
                if verbose:
                    print(f"packet order={order} port={port} count={color_counts[color]} payload={packet.payload} timestamp={packet.timestamp if packet.timestamp is not None else '-'}", flush=True)
                if live_state: live_state.record(port, packet, arrival, order, colors.get(port), arrival_monotonic)
                if on_packet: on_packet(port, packet, arrival)
            now_monotonic = time.monotonic()
            if terminal_summary and now_monotonic >= next_summary:
                _write_console_summary(
                    _diagnostic_summary_lines(ports, bound_ports, packet_stats, now_monotonic, bonjour_publisher),
                    True,
                    redraw=summary_rendered,
                )
                summary_rendered = True
                next_summary = now_monotonic + 1.0
        completed = True
    except KeyboardInterrupt:
        completed = True
        stopped_by_user = True
    except RuntimeError as error:
        print(f"ライブ起動に失敗しました: {error}", flush=True); return 2
    except OSError as error:
        if binding_port is not None:
            bind_report = ", ".join(
                f"UDP {port}={'BOUND' if port in bound_ports else 'FAILED' if port == binding_port else 'NOT TRIED'}"
                for port in ports
            )
            print(f"{bind_report}。Unityまたは別のprobeがポートを使用中なら先に停止してください。 ({error})", flush=True)
        elif live_requested:
            print(f"ライブ起動に失敗しました（UDP/HTTP資源を解放します）: {error}", flush=True)
        else:
            print(f"受信ソケットを開けません: {error}。Unityまたは別のprobeがポートを使用中なら先に停止してください。", flush=True)
        return 2
    finally:
        if completed:
            _write_console_summary(
                _diagnostic_summary_lines(ports, bound_ports, packet_stats, time.monotonic(), bonjour_publisher),
                terminal_summary,
                redraw=summary_rendered and terminal_summary,
                finish=terminal_summary,
            )
            if stopped_by_user:
                print("stopped", flush=True)
        for _, sock in sockets: sock.close()
        if live_state: live_state.stop()
        if live_server: live_server.close()
        if bonjour_publisher: bonjour_publisher.close()
    if packets is None:
        print("照合状態=未指定（保存済み表示ログなし）", flush=True)
        return 0
    loaded_log, warning = try_load_display_log(display_log)
    frames = loaded_log.frames if loaded_log else []
    if warning: print(warning, flush=True)
    if trial_id is not None: print("注意: --trial-id は保存ログを絞るだけでpacket自体に試行IDを付与しません。packetの時刻区間も検証します。", flush=True)
    if packets_dropped:
        print(f"packet_history_limit={RECONCILE_PACKET_LIMIT} dropped_oldest={packets_dropped}; 照合は直近packetのみです", flush=True)
    samples = {"all": [], "red": [], "blue": []}; seen: set[tuple[str, str, int]] = set(); matched = duplicates = unmatched = 0
    interval = (loaded_log.started_epoch_ms, loaded_log.ended_epoch_ms) if loaded_log else None
    for port, packet, _, order, _ in packets:
        color = colors.get(port); match = None
        if color and packet.timestamp_valid and packet.timestamp is not None and interval is not None and interval[0] / 1000 <= packet.timestamp <= interval[1] / 1000: match = nearest_display_frame(frames, packet.payload, color, input_width, input_height, reject_threshold, trial_id)
        key = (match.trial_id, color, match.state_id) if match else None; display_to_phone_ms = (packet.timestamp - match.display_epoch_ms / 1000) * 1000 if match and packet.timestamp is not None else None
        if match is None or packet.timestamp is None or display_to_phone_ms is None or not math.isfinite(display_to_phone_ms): unmatched += 1; state = "未対応"
        elif key in seen: duplicates += 1; state = "重複"
        else: seen.add(key); samples["all"].append(display_to_phone_ms); samples[color].append(display_to_phone_ms); matched += 1; state = "対応"
        print(f"reconcile order={order} color={color or '-'} stateId={match.state_id if match else '-'} trialId={match.trial_id if match else '-'} display_coordinate_distance={match.coordinate_distance if match else '-'} display_to_phone_ms={display_to_phone_ms if display_to_phone_ms is not None else '-'} reject_threshold={reject_threshold} status={state}", flush=True)
    print(f"reconcile_summary matched={matched} unmatched={unmatched} duplicate={duplicates}", flush=True); _print_stats(samples, "all"); _print_stats(samples, "red"); _print_stats(samples, "blue")
    return 0

if __name__ == "__main__":
    args = parse_args(); raise SystemExit(run(args.host, tuple(args.ports or PORTS), args.duration, args.display_log, args.input_width, args.input_height, reject_threshold=args.reject_threshold, trial_id=args.trial_id, live=args.live, http_host=args.http_host, http_port=args.http_port, html_path=args.html, open_browser=args.open_browser, bonjour=args.bonjour, verbose=args.verbose))
