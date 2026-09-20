#!/bin/zsh
set -eu

SCRIPT_DIR="${0:A:h}"
cd "$SCRIPT_DIR"

PYTHON_BIN="python3"
if [[ -x /opt/homebrew/bin/python3 ]]; then
  PYTHON_BIN="/opt/homebrew/bin/python3"
fi

if [[ ! -x .venv/bin/python3 ]]; then
  echo "Creating project-local BLE bridge environment..."
  "$PYTHON_BIN" -m venv .venv
fi

echo "Installing BLE bridge dependency..."
.venv/bin/python3 -m pip install --upgrade pip
.venv/bin/python3 -m pip install -r requirements_ble_bridge.txt

echo
echo "BLE bridge setup complete. Unity can now start it automatically."
read -k 1 "?Press any key to close..."
echo
