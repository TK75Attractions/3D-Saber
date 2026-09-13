#!/bin/bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "$0")" && pwd)"
cd "$script_dir"

exec python3 -B "$script_dir/udp_receive_probe.py" \
  --host 0.0.0.0 \
  --port 5005 \
  --port 5006 \
  --live \
  --http-host 127.0.0.1 \
  --http-port 8765 \
  --html "$script_dir/saber_camera_test.html" \
  --open-browser
