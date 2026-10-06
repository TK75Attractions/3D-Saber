#!/usr/bin/env python3
"""Build (cached) and run the Mac-side PhoneSaber P2P bridge.

The bridge receives coordinates from PhoneSaberSender over Network.framework
peer-to-peer Wi-Fi (`_phonesaber-p2p._udp`) and forwards the unchanged payload to
127.0.0.1:5005 (RED) / 127.0.0.1:5006 (BLUE) for the existing Unity InputPoint.
It is optional: without it the iPhone keeps using the existing LAN UDP path.

Run these examples from PhoneSaber/ inside the Unity Git checkout:

    python3 ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py            # build if needed, run
    python3 ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py --build-only
    python3 ios/PhoneSaberSender/Tools/phone_saber_p2p_bridge.py -- --name "Saber Mac"

Arguments after `--` go to the bridge (see PhoneSaberP2PBridge.swift --help).
"""
from __future__ import annotations

import argparse
import hashlib
import os
import signal
import subprocess
import sys
import time
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
    _remove_stale_temporaries(target.parent)
    temporary = target.with_name(f".{BINARY_NAME}.{os.getpid()}.tmp")
    # Absolute xcrun: Unity launches this script with a minimal PATH.
    xcrun = "/usr/bin/xcrun" if os.path.exists("/usr/bin/xcrun") else "xcrun"
    command = [xcrun, "swiftc", "-O", "-parse-as-library", *map(str, SOURCES), "-o", str(temporary)]
    print(f"[P2P] building bridge: {' '.join(command)}", flush=True)
    compiler = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True)

    def stop_compiler(signum, _frame):
        # Unity stopping Play during the first build: never leave swiftc running.
        compiler.terminate()
        try:
            compiler.wait(timeout=5)
        except subprocess.TimeoutExpired:
            compiler.kill()
        temporary.unlink(missing_ok=True)
        raise SystemExit(128 + signum)

    previous = {sig: signal.signal(sig, stop_compiler) for sig in (signal.SIGTERM, signal.SIGINT)}
    try:
        _, stderr = compiler.communicate()
    finally:
        for sig, handler in previous.items():
            signal.signal(sig, handler)
    if compiler.returncode != 0:
        temporary.unlink(missing_ok=True)
        raise RuntimeError(f"bridge build failed:\n{stderr}")
    os.replace(temporary, target)
    return target


def _remove_stale_temporaries(directory: Path, older_than: float = 600) -> None:
    """Partial outputs of interrupted builds (pid-named, so never another live build's)."""
    now = time.time()
    for leftover in directory.glob(f".{BINARY_NAME}.*.tmp"):
        try:
            if now - leftover.stat().st_mtime > older_than:
                leftover.unlink()
        except OSError:
            pass


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
