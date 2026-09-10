#!/usr/bin/env python3
"""Receive UDP coordinates and reconcile them with one saved HTML trial."""
from __future__ import annotations

import argparse
import json
import math
import select
import socket
import statistics
import time
from dataclasses import dataclass
from pathlib import Path
from typing import Callable

PORTS = (5005, 5006)

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

def _valid_state_id(value: object) -> bool:
    return isinstance(value, int) and not isinstance(value, bool) and value >= 0

def _valid_trial_id(value: object) -> bool:
    return isinstance(value, str) and bool(value.strip())

def _validate_frame(frame: dict, seen: set[tuple[str, str, int]]) -> None:
    trial_id, state_id, color = frame.get("trialId"), frame.get("stateId"), frame.get("color")
    if not _valid_trial_id(trial_id) or not _valid_state_id(state_id) or color not in {"red", "blue"}:
        raise ValueError("display log stateId/trialId/color must be a unique typed value")
    key = (trial_id, color, state_id)
    if key in seen: raise ValueError(f"duplicate display state: {key}")
    seen.add(key)
    try: epoch = float(frame["displayEpochMs"])
    except (KeyError, TypeError, ValueError, OverflowError): raise ValueError("display log state must contain finite displayEpochMs") from None
    if not math.isfinite(epoch): raise ValueError("display log state must contain finite displayEpochMs")

def load_display_log(path: str | Path) -> list[dict]:
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
    return frames

def try_load_display_log(path: str | Path | None) -> tuple[list[dict], str | None]:
    if path is None: return [], None
    try: return load_display_log(path), None
    except (OSError, UnicodeError, ValueError, json.JSONDecodeError) as error: return [], f"表示ログを読み込めないため計測照合なしで続行します: {path} ({error})"

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
    if not matches or matches[0].coordinate_distance > reject_threshold: return None
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

def parse_args():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="0.0.0.0"); parser.add_argument("--duration", type=float, default=0.0); parser.add_argument("--port", type=int, action="append", dest="ports"); parser.add_argument("--display-log", type=Path); parser.add_argument("--trial-id"); parser.add_argument("--reject-threshold", type=float, default=0.08); parser.add_argument("--input-width", type=float, default=1920.0); parser.add_argument("--input-height", type=float, default=1080.0)
    return parser.parse_args()

def run(host: str, ports: tuple[int, ...], duration: float, display_log=None, input_width=1920.0, input_height=1080.0, on_ready: Callable[[], None] | None = None, reject_threshold=0.08, trial_id=None) -> int:
    if not math.isfinite(duration) or duration < 0 or not math.isfinite(input_width) or not math.isfinite(input_height) or input_width <= 0 or input_height <= 0 or not math.isfinite(reject_threshold) or reject_threshold < 0:
        print("拒否閾値・期間・入力寸法は有限かつ非負（寸法は正）で指定してください", flush=True); return 2
    sockets, packets = [], []
    try:
        for port in ports:
            sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
            try: sock.bind((host, port))
            except OSError: sock.close(); raise
            sock.setblocking(False); sockets.append((port, sock))
        by_socket = {sock: port for port, sock in sockets}; deadline = time.monotonic() + duration if duration > 0 else None
        print(f"listening on {host}: {', '.join(map(str, ports))}; Ctrl-C to stop", flush=True)
        if on_ready: on_ready()
        while deadline is None or time.monotonic() < deadline:
            timeout = .5 if deadline is None else max(0, min(.5, deadline - time.monotonic()))
            ready, _, _ = select.select(list(by_socket), [], [], timeout)
            for sock in ready:
                data, address = sock.recvfrom(4096); port = by_socket[sock]; arrival = time.time(); packet = parse_packet(data); order = len(packets) + 1; packets.append((port, packet, arrival, order, address))
                print(f"packet order={order} port={port} count={sum(1 for item in packets if item[0] == port)} payload={packet.payload} timestamp={packet.timestamp if packet.timestamp is not None else '-'}", flush=True)
    except KeyboardInterrupt: print("stopped", flush=True)
    except OSError as error:
        print(f"受信ソケットを開けません: {error}。Unityまたは別のprobeがポートを使用中なら先に停止してください。", flush=True); return 2
    finally:
        for _, sock in sockets: sock.close()
    frames, warning = try_load_display_log(display_log)
    if warning: print(warning, flush=True)
    if display_log is None: print("照合状態=未指定（packetはこの実行の終了まで保持します。終了後は失われるため、再実行だけでは照合できません）", flush=True)
    if trial_id is not None: print("注意: --trial-id は保存ログを絞るだけでpacket自体に試行IDを付与しません。packetの時刻区間も検証します。", flush=True)
    samples = {"all": [], "red": [], "blue": []}; seen: set[tuple[str, str, int]] = set(); matched = duplicates = unmatched = 0
    min_display = min((float(frame["displayEpochMs"]) for frame in frames), default=None); max_display = max((float(frame["displayEpochMs"]) for frame in frames), default=None)
    for port, packet, _, order, _ in packets:
        color = {5005: "red", 5006: "blue"}.get(port); match = None
        if color and packet.timestamp_valid and packet.timestamp is not None and min_display is not None and min_display / 1000 <= packet.timestamp <= max_display / 1000 + 1.0: match = nearest_display_frame(frames, packet.payload, color, input_width, input_height, reject_threshold, trial_id)
        key = (match.trial_id, color, match.state_id) if match else None; display_to_phone_ms = (packet.timestamp - match.display_epoch_ms / 1000) * 1000 if match and packet.timestamp is not None else None
        if match is None or packet.timestamp is None or display_to_phone_ms is None or not math.isfinite(display_to_phone_ms): unmatched += 1; state = "未対応"
        elif key in seen: duplicates += 1; state = "重複"
        else: seen.add(key); samples["all"].append(display_to_phone_ms); samples[color].append(display_to_phone_ms); matched += 1; state = "対応"
        print(f"reconcile order={order} color={color or '-'} stateId={match.state_id if match else '-'} trialId={match.trial_id if match else '-'} display_coordinate_distance={match.coordinate_distance if match else '-'} display_to_phone_ms={display_to_phone_ms if display_to_phone_ms is not None else '-'} reject_threshold={reject_threshold} status={state}", flush=True)
    print(f"reconcile_summary matched={matched} unmatched={unmatched} duplicate={duplicates}", flush=True); _print_stats(samples, "all"); _print_stats(samples, "red"); _print_stats(samples, "blue")
    return 0

if __name__ == "__main__":
    args = parse_args(); raise SystemExit(run(args.host, tuple(args.ports or PORTS), args.duration, args.display_log, args.input_width, args.input_height, reject_threshold=args.reject_threshold, trial_id=args.trial_id))
