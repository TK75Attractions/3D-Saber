#!/bin/sh
set -eu
tools_dir="$(CDPATH='' cd -- "$(dirname -- "$0")" && pwd)"
exec python3 "$tools_dir/phone_saber_triage_receiver.py" "$@"
