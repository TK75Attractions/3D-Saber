#!/usr/bin/env python3
"""Build (cached) and run the Mac-side PhoneSaber P2P bridge.

The bridge receives coordinates from PhoneSaberSender over Network.framework
peer-to-peer Wi-Fi (`_phonesaber-p2p._udp`) and forwards the unchanged payload to
127.0.0.1:5005 (RED) / 127.0.0.1:5006 (BLUE) for the existing Unity InputPoint.
It is optional: without it the iPhone keeps using the existing LAN UDP path.

    python3 ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py            # build if needed, run
    python3 ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py --build-only
    python3 ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py -- --name "Saber Mac"

Arguments after `--` go to the bridge (see PhoneSaberP2PBridge.swift --help).
"""
from __future__ import annotations

import argparse
import hashlib
import os
import subprocess
import sys
from pathlib import Path

TOOLS_DIR = Path(__file__).resolve().parent
SOURCES = (
    TOOLS_DIR.parent / "PhoneSaberSender" / "P2PProtocol.swift",
    TOOLS_DIR / "p2p_bridge" / "PhoneSaberP2PBridge.swift",
)
DEFAULT_CACHE = Path.home() / "Library" / "Caches" / "PhoneSaber" / "p2p-bridge"
BINARY_NAME = "PhoneSaberP2PBridge"


def source_digest(sources: tuple[Path, ...] = SOURCES) -> str:
    digest = hashlib.sha256()
    for path in sources:
        digest.update(path.name.encode())
        digest.update(path.read_bytes())
    return digest.hexdigest()[:16]


def build(cache_dir: Path = DEFAULT_CACHE, *, force: bool = False) -> Path:
    """Compile the bridge once per source revision; returns the binary path."""
    target = cache_dir / source_digest() / BINARY_NAME
    if target.is_file() and os.access(target, os.X_OK) and not force:
        return target
    target.parent.mkdir(parents=True, exist_ok=True)
    temporary = target.with_name(f".{BINARY_NAME}.{os.getpid()}.tmp")
    command = ["xcrun", "swiftc", "-O", "-parse-as-library", *map(str, SOURCES), "-o", str(temporary)]
    print(f"[P2P] building bridge: {' '.join(command)}", flush=True)
    completed = subprocess.run(command, capture_output=True, text=True)
    if completed.returncode != 0:
        temporary.unlink(missing_ok=True)
        raise RuntimeError(f"bridge build failed:\n{completed.stderr}")
    os.replace(temporary, target)
    return target


def main(argv: list[str] | None = None) -> int:
    argv = list(sys.argv[1:] if argv is None else argv)
    bridge_args: list[str] = []
    if "--" in argv:
        split = argv.index("--")
        argv, bridge_args = argv[:split], argv[split + 1:]
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--build-only", action="store_true", help="compile (if needed) and print the binary path")
    parser.add_argument("--rebuild", action="store_true", help="compile even if a cached binary exists")
    parser.add_argument("--cache-dir", type=Path, default=DEFAULT_CACHE)
    args = parser.parse_args(argv)
    try:
        binary = build(args.cache_dir, force=args.rebuild)
    except (OSError, RuntimeError) as error:
        print(f"[P2P] {error}", file=sys.stderr)
        return 1
    if args.build_only:
        print(binary)
        return 0
    os.execv(str(binary), [str(binary), *bridge_args])
    return 0  # unreachable


if __name__ == "__main__":
    raise SystemExit(main())
