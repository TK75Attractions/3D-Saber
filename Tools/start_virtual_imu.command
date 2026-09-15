#!/bin/zsh
set -e
script_dir="${0:A:h}"
cd "$script_dir"
exec /usr/bin/env python3 virtual_imu.py
