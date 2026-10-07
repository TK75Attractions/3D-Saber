#!/usr/bin/env python3
"""PhoneSaber の座標トラック抽出・合成・UDP 再生（標準ライブラリのみ）。"""

import argparse
import bisect
import csv
import json
import math
import random
import socket
import statistics
import time
from dataclasses import dataclass
from pathlib import Path

INBOX = Path.home() / "Library/Application Support/PhoneSaber/diagnostics-inbox"
COLORS = ("red", "blue")
WIDTH, HEIGHT = 1920, 1080
PATTERNS = ("slash-left", "slash-right", "slash-up", "slash-down", "thrust", "figure-eight", "idle")


def number(value, name, minimum=None, positive=False):
    if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value):
        raise ValueError(f"{name} must be finite")
    if (minimum is not None and value < minimum) or (positive and value <= 0):
        raise ValueError(f"invalid {name}: {value}")
    return value


def endpoints(value):
    if not isinstance(value, list) or len(value) != 4:
        raise ValueError("endpoint must contain x1,y1,x2,y2")
    return [number(v, "endpoint") for v in value]


def phone_round(value):
    # Swift rounded() と同じ、ちょうど半分はゼロから遠い整数へ丸める。
    return int(math.copysign(math.floor(abs(value) + 0.5), value))


def scale_endpoint(value, source_size, mirror_x=False, mirror_y=False):
    w, h = source_size
    if w <= 1 or h <= 1:
        raise ValueError("source size must be at least 2x2")
    result = []
    for i, v in enumerate(endpoints(value)):
        extent, source, mirror = (WIDTH - 1, w, mirror_x) if i % 2 == 0 else (HEIGHT - 1, h, mirror_y)
        scaled = v / (source - 1) * extent
        result.append(phone_round(extent - scaled if mirror else scaled))
    return result


def validate_track(track):
    if not isinstance(track, dict) or track.get("version") != 1 or track.get("size") != [WIDTH, HEIGHT]:
        raise ValueError("expected version=1, size=[1920,1080]")
    period = number(track.get("sample_period"), "sample_period", positive=True)
    duration = number(track.get("duration"), "duration", positive=True)
    frames = track.get("frames")
    if not isinstance(frames, list) or not frames:
        raise ValueError("track has no frames")
    previous = -1
    for frame in frames:
        t = number(frame["t"], "frame time", minimum=0)
        if t <= previous or t >= duration:
            raise ValueError("frame times must increase and be below duration")
        previous = t
        for color in COLORS:
            state = frame.get(color)
            if state is None:
                continue
            if not isinstance(state, dict) or not isinstance(state.get("detected"), bool):
                raise ValueError(f"invalid {color} detection state")
            if state.get("endpoint") is not None:
                endpoints(state["endpoint"])
            elif state["detected"]:
                raise ValueError(f"detected {color} requires endpoints")
    return period


def extract_bundle(bundle, source_size=None, mirror_x=False, mirror_y=False,
                   first_frame=None, last_frame=None):
    """重複 context は色ごとに統合し、相反する記録は黙って採用しない。"""
    bundle = Path(bundle)
    paths = sorted((bundle / "frames").glob("*.json"))
    if not paths:
        raise ValueError(f"no frames/*.json in {bundle}")
    if (mirror_x or mirror_y) and source_size is None:
        raise ValueError("mirroring requires --source-size")
    rows, transmissions, sessions = {}, {}, set()
    for path in paths:
        context = json.loads(path.read_text(encoding="utf-8"))
        if context.get("sessionID"):
            sessions.add(context["sessionID"])
        for tx in context.get("udpTransmissions", []):
            if tx.get("state") != "sendStarted" or tx.get("color") not in COLORS:
                continue
            if tx.get("coordinateSpace") != "configuredUDPOutputPixels":
                continue
            key = (tx["frameID"], tx["color"])
            value = endpoints(tx["endpoint"])
            if key in transmissions and transmissions[key] != value:
                raise ValueError(f"conflicting transmissions for {key}")
            transmissions[key] = value
        for frame in context["frames"]:
            fid = frame["frameID"]
            if not isinstance(fid, int) or isinstance(fid, bool) or fid < 0:
                raise ValueError("invalid frameID")
            if (first_frame is not None and fid < first_frame) or (last_frame is not None and fid > last_frame):
                continue
            stamp = number(frame["timestamp"], "timestamp", minimum=0)
            row = rows.setdefault(fid, {"frame_id": fid, "t": stamp})
            if row["t"] != stamp:
                raise ValueError(f"conflicting timestamps for frame {fid}")
            for color in COLORS:
                data = frame.get(color) or {}
                detected = data.get("detected")
                if detected is None:
                    continue  # 未記録の色を未検出と混同しない。
                if not isinstance(detected, bool):
                    raise ValueError(f"invalid detected for frame {fid}")
                state = {"detected": detected, "endpoint": data.get("endpoint")}
                old = row.get(color)
                if old is not None and old["detected"] != detected:
                    raise ValueError(f"conflicting {color} detection for frame {fid}")
                if old is not None and old["endpoint"] is not None:
                    if state["endpoint"] is not None and old["endpoint"] != state["endpoint"]:
                        raise ValueError(f"conflicting {color} endpoints for frame {fid}")
                    state = old
                row[color] = state
    if len(sessions) > 1:
        raise ValueError("bundle contains multiple sessions")
    frames = sorted(rows.values(), key=lambda row: row["frame_id"])
    if not frames:
        raise ValueError("no frames in requested range")
    deltas = []
    for a, b in zip(frames, frames[1:]):
        dt = b["t"] - a["t"]
        if dt <= 0:
            raise ValueError("timestamps must increase with frameID")
        deltas.append(dt / (b["frame_id"] - a["frame_id"]))
    period = statistics.median(deltas) if deltas else 1 / 30
    origin = frames[0]["t"]
    for frame in frames:
        frame["t"] -= origin
        for color in COLORS:
            state = frame.get(color)
            if state is None:
                continue
            recorded = transmissions.get((frame["frame_id"], color))
            if source_size is None and recorded is not None:
                state["endpoint"] = recorded
            elif state["endpoint"] is not None:
                if source_size is None:
                    raise ValueError("context endpoints are source pixels; specify --source-size WIDTHxHEIGHT "
                                     "(and phone mirror settings), or select frames with recorded UDP endpoints")
                state["endpoint"] = scale_endpoint(state["endpoint"], source_size, mirror_x, mirror_y)
    track = {"version": 1, "size": [WIDTH, HEIGHT], "sample_period": period,
             "duration": frames[-1]["t"] + period, "frames": frames}
    validate_track(track)
    return track


def write_track(track, output, bundle=None, inbox=INBOX):
    validate_track(track)
    output = Path(output).expanduser().resolve()
    # symlink も解決し、入力 bundle と受信 inbox を書き込み対象にしない。
    for protected in (inbox, bundle):
        if protected is not None and output.is_relative_to(Path(protected).expanduser().resolve()):
            raise ValueError("output must be outside the diagnostics inbox and input bundle")
    if output.suffix.lower() not in (".json", ".csv"):
        raise ValueError("output must end in .json or .csv")
    output.parent.mkdir(parents=True, exist_ok=True)
    with output.open("x", encoding="utf-8", newline="") as stream:
        if output.suffix.lower() == ".json":
            json.dump(track, stream, separators=(",", ":"), allow_nan=False)
            stream.write("\n")
        else:
            writer = csv.writer(stream)
            writer.writerow(["t", "frame_id", "sample_period", "duration"] +
                            [f"{c}_{k}" for c in COLORS for k in ("detected", "x1", "y1", "x2", "y2")])
            for frame in track["frames"]:
                row = [frame["t"], frame.get("frame_id", ""), track["sample_period"], track["duration"]]
                for color in COLORS:
                    state = frame.get(color)
                    row += ([int(state["detected"])] + (state.get("endpoint") or [""] * 4)) if state else [""] * 5
                writer.writerow(row)


def read_track(path):
    path = Path(path).expanduser()
    if path.suffix.lower() == ".csv":
        frames, metadata = [], None
        with path.open(encoding="utf-8", newline="") as stream:
            for row in csv.DictReader(stream):
                current = (float(row["sample_period"]), float(row["duration"]))
                if metadata is not None and metadata != current:
                    raise ValueError("inconsistent CSV timing metadata")
                metadata = current
                frame = {"t": float(row["t"])}
                if row["frame_id"]:
                    frame["frame_id"] = int(row["frame_id"])
                for color in COLORS:
                    flag = row[f"{color}_detected"]
                    if flag == "":
                        continue
                    if flag not in ("0", "1"):
                        raise ValueError("CSV detected must be 0, 1 or empty")
                    coords = [row[f"{color}_{k}"] for k in ("x1", "y1", "x2", "y2")]
                    frame[color] = {"detected": flag == "1", "endpoint":
                                    None if coords == [""] * 4 else [float(v) for v in coords]}
                frames.append(frame)
        if metadata is None:
            raise ValueError("empty CSV")
        track = {"version": 1, "size": [WIDTH, HEIGHT], "sample_period": metadata[0],
                 "duration": metadata[1], "frames": frames}
    else:
        track = json.loads(path.read_text(encoding="utf-8"))
    validate_track(track)
    return track


def synthetic_track(pattern, duration=5, fps=30, speed=1, color="both"):
    number(duration, "duration", positive=True)
    number(fps, "fps", positive=True)
    number(speed, "speed", positive=True)
    if pattern not in PATTERNS or color not in (*COLORS, "both"):
        raise ValueError("invalid synthetic pattern or color")
    frames = []
    for index in range(math.ceil(duration * fps)):
        t = index / fps
        phase = t * speed % 1
        # slash は往復せず、振り終わりで次の周期の開始点へ戻す。
        sweep = -math.cos(math.pi * phase)
        x, y, length, angle = 960, 540, 260, -math.pi / 2
        if pattern == "slash-left":
            x -= 550 * sweep
        elif pattern == "slash-right":
            x += 550 * sweep
        elif pattern == "slash-up":
            y -= 300 * sweep
            angle = 0
        elif pattern == "slash-down":
            y += 300 * sweep
            angle = 0
        elif pattern == "thrust":
            length = 80 + 400 * (0.5 - 0.5 * math.cos(2 * math.pi * phase))
        elif pattern == "figure-eight":
            x += 500 * math.sin(2 * math.pi * phase)
            y += 250 * math.sin(4 * math.pi * phase)
            angle = math.atan2(math.cos(4 * math.pi * phase), math.cos(2 * math.pi * phase))
        dx, dy = length / 2 * math.cos(angle), length / 2 * math.sin(angle)
        frame = {"t": t}
        for c in COLORS if color == "both" else (color,):
            offset = -100 if c == "red" else 100
            frame[c] = {"detected": True, "endpoint": [phone_round(v) for v in
                        (x + offset - dx, y - dy, x + offset + dx, y + dy)]}
        frames.append(frame)
    return {"version": 1, "size": [WIDTH, HEIGHT], "sample_period": 1 / fps,
            "duration": duration, "frames": frames}


@dataclass(frozen=True)
class Impairments:
    latency_ms: float = 0
    jitter_ms: float = 0
    loss: float = 0
    dropouts: tuple = ()  # (開始秒, 継続秒)、送信予定時刻で判定。
    false_at: float = 0
    false_frames: int = 0
    seed: int = 0

    def validate(self):
        number(self.latency_ms, "latency_ms", minimum=0)
        number(self.jitter_ms, "jitter_ms", minimum=0)
        number(self.loss, "loss", minimum=0)
        if self.loss > 1:
            raise ValueError("loss must be between 0 and 1")
        number(self.false_at, "false_at", minimum=0)
        if not isinstance(self.false_frames, int) or self.false_frames < 0:
            raise ValueError("false_frames must be a nonnegative integer")
        for start, duration in self.dropouts:
            number(start, "dropout start", minimum=0)
            number(duration, "dropout duration", positive=True)


@dataclass(frozen=True)
class Packet:
    due: float
    captured: float
    color: str
    endpoint: tuple


def schedule(track, fps=30, speed=1, repeats=1, color="both", impairments=None):
    period = validate_track(track)
    number(fps, "fps", positive=True)
    number(speed, "speed", positive=True)
    if not isinstance(repeats, int) or repeats < 1 or color not in (*COLORS, "both"):
        raise ValueError("invalid repeats or color")
    impairments = impairments or Impairments()
    impairments.validate()
    rng = random.Random(impairments.seed)
    duration = track["duration"] / speed
    frames = track["frames"]
    times = [f["t"] for f in frames]
    packets = []
    jump_start = math.ceil(impairments.false_at * fps - 1e-9)
    for tick in range(math.ceil(duration * repeats * fps)):
        captured = tick / fps
        source_time = (captured % duration) * speed
        index = bisect.bisect_right(times, source_time + 1e-9) - 1
        frame = frames[index] if index >= 0 and source_time < times[index] + period - 1e-9 else {}
        for c in COLORS if color == "both" else (color,):
            state = frame.get(c)
            endpoint = state["endpoint"] if state and state["detected"] else None
            if jump_start <= tick < jump_start + impairments.false_frames:
                # 元の中点から反対側の隅へ飛ばす。未検出中にも誤検出を作れる。
                mx = (endpoint[0] + endpoint[2]) / 2 if endpoint else 960
                my = (endpoint[1] + endpoint[3]) / 2 if endpoint else 540
                x = rng.uniform(80, 280) if mx >= 960 else rng.uniform(1640, 1840)
                y = rng.uniform(80, 220) if my >= 540 else rng.uniform(860, 1000)
                endpoint = [phone_round(v) for v in (x - 40, y, x + 40, y)]
            if endpoint is None:
                continue
            if rng.random() < impairments.loss:
                continue
            delay = max(0, impairments.latency_ms + rng.uniform(-impairments.jitter_ms, impairments.jitter_ms)) / 1000
            due = max(captured, round(captured + delay, 12))
            if any(round(start, 12) <= due < round(start + gap, 12) for start, gap in impairments.dropouts):
                continue
            packets.append(Packet(due, captured, c, tuple(endpoint)))
    # jitter による逆順受信をそのまま再現する。
    return sorted(packets, key=lambda p: p.due)


def payload(packet, epoch=None):
    text = ",".join(str(phone_round(v)) for v in packet.endpoint)
    if epoch is not None:
        text = f"ts={epoch + packet.captured:.6f};{text}"
    return text.encode("ascii")


def send_packets(packets, host, red_port=5005, blue_port=5006, timestamp=False):
    start, epoch = time.monotonic(), time.time() if timestamp else None
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        for packet in packets:
            time.sleep(max(0, start + packet.due - time.monotonic()))
            sock.sendto(payload(packet, epoch), (host, red_port if packet.color == "red" else blue_port))


def parse_discovery(data, host):
    parts = data.decode("ascii").split()
    if parts[:2] != ["PHONESABER_UNITY", "1"]:
        raise ValueError("invalid discovery reply")
    fields = dict(p.split("=", 1) for p in parts[2:])
    ports = [int(fields[c]) for c in COLORS]
    if any(not 1 <= p <= 65535 for p in ports):
        raise ValueError("invalid discovery ports")
    return (host, *ports, fields.get("station", ""))


def discover(station=None, address="255.255.255.255", port=5007, timeout=1, bind_address="0.0.0.0"):
    number(timeout, "discovery timeout", positive=True)
    found = set()
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sock:
        sock.setsockopt(socket.SOL_SOCKET, socket.SO_BROADCAST, 1)
        sock.bind((bind_address, 0))
        sock.sendto(b"PHONESABER_DISCOVER 1", (address, port))
        deadline = time.monotonic() + timeout
        while time.monotonic() < deadline:
            sock.settimeout(max(0.001, deadline - time.monotonic()))
            try:
                data, sender = sock.recvfrom(2048)
            except socket.timeout:
                break
            try:
                candidate = parse_discovery(data, sender[0])
            except (ValueError, UnicodeError, KeyError):
                continue
            if station is None or candidate[3] == station:
                found.add(candidate)
    if len(found) != 1:
        raise ValueError(f"discovery found {len(found)} matching targets; use --station or --host")
    return found.pop()


def source_size(text):
    try:
        w, h = map(int, text.lower().split("x"))
        if w > 1 and h > 1:
            return w, h
    except ValueError:
        pass
    raise argparse.ArgumentTypeError("expected WIDTHxHEIGHT, each at least 2")


def dropout(text):
    try:
        start, ms = map(float, text.split(":"))
        number(start, "start", minimum=0)
        number(ms, "duration", positive=True)
        return start, ms / 1000
    except ValueError as error:
        raise argparse.ArgumentTypeError("expected START_SECONDS:DURATION_MS") from error


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    extract = commands.add_parser("extract", help="read-only triage bundle -> compact JSON/CSV")
    extract.add_argument("bundle", help="bundle path or bundle name inside the diagnostics inbox")
    extract.add_argument("--output", required=True)
    extract.add_argument("--source-size", type=source_size)
    extract.add_argument("--mirror-x", action="store_true")
    extract.add_argument("--mirror-y", action="store_true")
    extract.add_argument("--first-frame", type=int)
    extract.add_argument("--last-frame", type=int)
    replay = commands.add_parser("play", help="send coordinate UDP packets")
    sources = replay.add_mutually_exclusive_group(required=True)
    sources.add_argument("--track")
    sources.add_argument("--synthetic", choices=PATTERNS)
    replay.add_argument("--duration", type=float, default=5, help="synthetic duration in seconds")
    replay.add_argument("--fps", type=float, default=30)
    replay.add_argument("--speed", type=float, default=1, help="synthetic cycles/sec or track playback multiplier")
    replay.add_argument("--repeat", type=int, default=1)
    replay.add_argument("--color", choices=(*COLORS, "both"), default="both")
    destination = replay.add_mutually_exclusive_group()
    destination.add_argument("--host", help="default: 127.0.0.1")
    destination.add_argument("--discover", action="store_true")
    replay.add_argument("--station", help="station label; enables discovery")
    replay.add_argument("--discovery-address", default="255.255.255.255")
    replay.add_argument("--discovery-timeout", type=float, default=1)
    replay.add_argument("--red-port", type=int)
    replay.add_argument("--blue-port", type=int)
    replay.add_argument("--timestamp", action="store_true")
    replay.add_argument("--latency-ms", type=float, default=0)
    replay.add_argument("--jitter-ms", type=float, default=0)
    replay.add_argument("--loss", type=float, default=0)
    replay.add_argument("--dropout", type=dropout, action="append", default=[])
    replay.add_argument("--false-positive-at", type=float, default=0)
    replay.add_argument("--false-positive-frames", type=int, default=0)
    replay.add_argument("--seed", type=int, default=0)
    replay.add_argument("--dry-run", action="store_true", help="schedule only; no sockets or discovery")
    args = parser.parse_args(argv)
    try:
        if args.command == "extract":
            bundle = Path(args.bundle).expanduser()
            if not bundle.exists() and len(bundle.parts) == 1:
                bundle = INBOX / bundle
            track = extract_bundle(bundle, args.source_size, args.mirror_x, args.mirror_y,
                                   args.first_frame, args.last_frame)
            write_track(track, args.output, bundle)
            print(f"Extracted {len(track['frames'])} frames, {track['duration']:.3f}s -> {args.output}")
            return 0
        if args.host and args.station:
            raise ValueError("--station selects discovery; use --host alone for a given IP")
        for port in (args.red_port, args.blue_port):
            if port is not None and not 1 <= port <= 65535:
                raise ValueError("ports must be between 1 and 65535")
        track = read_track(args.track) if args.track else synthetic_track(
            args.synthetic, args.duration, args.fps, args.speed, args.color)
        faults = Impairments(args.latency_ms, args.jitter_ms, args.loss, tuple(args.dropout),
                             args.false_positive_at, args.false_positive_frames, args.seed)
        packets = schedule(track, args.fps, args.speed if args.track else 1, args.repeat, args.color, faults)
        if args.dry_run:
            print(f"Scheduled {len(packets)} packets; last send {packets[-1].due if packets else 0:.3f}s; no network")
            return 0
        target = discover(args.station, args.discovery_address, timeout=args.discovery_timeout) if (
            args.discover or args.station is not None) else (args.host or "127.0.0.1", 5005, 5006, "")
        host, red, blue, station = target
        red, blue = args.red_port or red, args.blue_port or blue
        print(f"Replay {len(packets)} packets -> {host} RED:{red} BLUE:{blue} station={station or '-'}")
        send_packets(packets, host, red, blue, args.timestamp)
        return 0
    except (ValueError, OSError, KeyError, TypeError) as error:
        parser.error(str(error))
    except KeyboardInterrupt:
        print("Replay stopped")
        return 130


if __name__ == "__main__":
    raise SystemExit(main())
